using System.Diagnostics;
using WotBTreader.GameIntegration.Logs;
using WotBTreader.Host.Web.Contracts;
using WotBTreader.Host.Web.Infrastructure;

namespace WotBTreader.Host.Web.Services;

/// <summary>
/// Background service that monitors native Blitz replay lifecycle logs and
/// periodically probes the game process via Win32. Exposes the current game
/// and replay state through a thread-safe snapshot.
/// Registered as a singleton so API endpoints can query the latest state.
/// </summary>
public sealed class GameStateService : BackgroundService
{
    private const string GameWindowClass = "SDL_app";
    private const string GameExecutableName = "wotblitz.exe";
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(2);

    private readonly IBlitzReplayLogMonitor _logMonitor;
    private readonly GameMemoryReader _memoryReader;
    private readonly ILogger<GameStateService> _logger;

    private readonly Lock _gate = new();
    private bool _gameRunning;
    private bool _replayActive;
    private int _processId;
    private long _windowHandle;
    private DateTimeOffset? _lastStateObservedAtUtc;
    private string? _lastLogWatermark;

    public GameStateService(
        IBlitzReplayLogMonitor logMonitor,
        GameMemoryReader memoryReader,
        ILogger<GameStateService> logger)
    {
        _logMonitor = logMonitor ?? throw new ArgumentNullException(nameof(logMonitor));
        _memoryReader = memoryReader ?? throw new ArgumentNullException(nameof(memoryReader));
        _logger = logger;
    }

    /// <summary>
    /// Returns a snapshot of the current game and replay lifecycle state.
    /// Thread-safe; callable from any endpoint thread.
    /// </summary>
    public GameStateResponse GetState()
    {
        lock (_gate)
        {
            return new GameStateResponse
            {
                GameRunning = _gameRunning,
                ProcessId = _gameRunning ? _processId : null,
                WindowHandle = _gameRunning ? _windowHandle : null,
                ReplayState = _replayActive ? "OfflineReplayActive" : "NotRunning",
                ReplayStateObservedAtUtc = _lastStateObservedAtUtc,
                LogWatermark = _lastLogWatermark,
            };
        }
    }

    /// <summary>
    /// Launches a replay by opening the .wotbreplay file through Windows file
    /// association. Falls back to launching wotblitz.exe directly if no
    /// association exists.
    /// </summary>
    public GameLaunchResponse LaunchReplay(string replayPath)
    {
        if (string.IsNullOrWhiteSpace(replayPath))
        {
            return new GameLaunchResponse
            {
                Success = false,
                Message = "Replay path is required.",
            };
        }

        if (!File.Exists(replayPath))
        {
            return new GameLaunchResponse
            {
                Success = false,
                Message = "Replay file not found.",
            };
        }

        string? extension = Path.GetExtension(replayPath);
        if (!string.Equals(extension, ".wotbreplay", StringComparison.OrdinalIgnoreCase))
        {
            return new GameLaunchResponse
            {
                Success = false,
                Message = "File must be a .wotbreplay file.",
            };
        }

        try
        {
            // Copy to the game's replays folder so the in-game UI can find it.
            string replaysFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "wotblitz", "DAVAProject", "replays");
            Directory.CreateDirectory(replaysFolder);

            string target = Path.Combine(replaysFolder, Path.GetFileName(replayPath));
            if (!string.Equals(
                Path.GetFullPath(replayPath),
                Path.GetFullPath(target),
                StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(replayPath, target, overwrite: true);
            }

            // Launch via file association (same as double-clicking in Explorer).
            Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true,
            });

            return new GameLaunchResponse
            {
                Success = true,
                Message = "Game launched.",
            };
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No file association — try launching the game directly.
            string? gamePath = FindGameExecutablePath();
            if (gamePath is null)
            {
                return new GameLaunchResponse
                {
                    Success = false,
                    Message = "wotblitz.exe not found. Set WOTB_GAME_PATH environment variable.",
                };
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = gamePath,
                    UseShellExecute = true,
                });

                return new GameLaunchResponse
                {
                    Success = true,
                    Message = "Game launched (no file association).",
                };
            }
            catch (Exception ex)
            {
                return new GameLaunchResponse
                {
                    Success = false,
                    Message = $"Failed to launch: {ex.GetType().Name}.",
                };
            }
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new GameLaunchResponse
            {
                Success = false,
                Message = $"Launch failed: {ex.GetType().Name}.",
            };
        }
    }

    /// <summary>
    /// Returns the latest memory snapshot from the game process.
    /// Returns a default snapshot if not attached.
    /// </summary>
    public GameMemoryResponse GetMemory()
    {
        GameMemorySnapshot raw = _memoryReader.Poll();
        return new GameMemoryResponse
        {
            CapturedAtUtc = raw.CapturedAtUtc,
            ProcessAccessible = raw.ProcessAccessible,
            ReplayTimeSeconds = raw.ReplayTimeSeconds,
            PlayerHP = raw.PlayerHP,
            PlayerPositionX = raw.PlayerPositionX,
            PlayerPositionY = raw.PlayerPositionY,
            PlayerPositionZ = raw.PlayerPositionZ,
            PlayerYaw = raw.PlayerYaw,
            CameraPitch = raw.CameraPitch,
            AliveTankCount = raw.AliveTankCount,
            AnyOffsetsValidated = raw.AnyOffsetsValidated,
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run log monitoring and process probing concurrently
        Task logTask = MonitorLogsAsync(stoppingToken);
        Task probeTask = ProbeProcessAsync(stoppingToken);
        await Task.WhenAll(logTask, probeTask);
    }

    private async Task MonitorLogsAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (ReplayLogEvent logEvent in _logMonitor.WatchAsync(stoppingToken)
                               .ConfigureAwait(false))
            {
                UpdateStateFromLog(logEvent);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                new EventId(4300, "GameStateServiceError"),
                "Log monitoring stopped: {ExceptionType} — {Message}.",
                ex.GetType().Name,
                ex.Message);
        }
    }

    private async Task ProbeProcessAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                ProbeProcess();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(
                        "Process probe failed: {ExceptionType} — {Message}.",
                        ex.GetType().Name, ex.Message);
                }
            }

            await Task.Delay(ProbeInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    private void ProbeProcess()
    {
        // Find game window via SDL class name
        IntPtr hWnd = NativeMethods.FindWindowW(GameWindowClass, lpWindowName: null);
        if (hWnd == IntPtr.Zero)
        {
            lock (_gate)
            {
                _gameRunning = false;
                _processId = 0;
                _windowHandle = 0;
            }
            return;
        }

        uint pid;
        _ = NativeMethods.GetWindowThreadProcessId(hWnd, out pid);
        if (pid == 0)
        {
            return;
        }

        lock (_gate)
        {
            _gameRunning = true;
            _processId = (int)pid;
            _windowHandle = (long)hWnd;
        }
    }

    private void UpdateStateFromLog(ReplayLogEvent logEvent)
    {
        lock (_gate)
        {
            _lastStateObservedAtUtc = logEvent.ObservedAtUtc;
            _lastLogWatermark = logEvent.OpaqueSourceId.Value;

            switch (logEvent.Kind)
            {
                case ReplayLogMarkerKind.OfflineReplayStarted:
                    _replayActive = true;
                    break;

                case ReplayLogMarkerKind.OfflineReplayStopped:
                    _replayActive = false;
                    break;
            }
        }
    }

    private static string? FindGameExecutablePath()
    {
        string? envPath = Environment.GetEnvironmentVariable("WOTB_GAME_PATH");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
        {
            return envPath;
        }

        string[] roots =
        [
            Path.Combine(GetSystemDrive(), "Games", "World_of_Tanks_Blitz"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) ?? @"C:\Program Files (x86)",
                "Steam", "steamapps", "common", "World of Tanks Blitz"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) ?? @"C:\Program Files",
                "Steam", "steamapps", "common", "World of Tanks Blitz"),
            @"C:\Games\World_of_Tanks_Blitz",
        ];

        foreach (string root in roots)
        {
            string candidate = Path.Combine(root, GameExecutableName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string GetSystemDrive()
    {
        string? windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrWhiteSpace(windir))
        {
            string? root = Path.GetPathRoot(windir);
            if (!string.IsNullOrWhiteSpace(root))
            {
                return root;
            }
        }

        return @"C:\";
    }
}
