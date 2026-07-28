using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SkiaSharp;
using WotBTreader.GameIntegration;
using WotBTreader.GameIntegration.Logs;

namespace WotBTreader.GameHarness;

/// <summary>
/// Full Win32 implementation of <see cref="IGameHarnessPlatform"/> for production use
/// on Windows. Uses P/Invoke for process discovery, window capture, and guarded input;
/// delegates log tailing to the registered <see cref="IBlitzReplayLogMonitor"/>.
/// </summary>
public sealed class Win32Platform : IGameHarnessPlatform, IDisposable
{
    private const string GameWindowClassName = "SDL_app";
    private const string ReplaysSubdirectory = "DAVAProject\\replays";
    private const int MaxExecutablePathChars = 260;

    private readonly IBlitzReplayLogMonitor? _logMonitor;
    private readonly IBlitzReplayLifecycleParser? _logParser;
    private readonly GameIntegrationOptions _gameOptions;
    private readonly TimeProvider _timeProvider;
    private readonly GameHarnessCapabilities _capabilities;
    private bool _disposed;

    public GameHarnessCapabilities Capabilities => _capabilities;

    // ── Construction ──────────────────────────────────────────

    public Win32Platform(
        IBlitzReplayLogMonitor? logMonitor = null,
        IBlitzReplayLifecycleParser? logParser = null,
        GameIntegrationOptions? gameOptions = null,
        TimeProvider? timeProvider = null)
    {
        _logMonitor = logMonitor;
        _logParser = logParser;
        _gameOptions = gameOptions ?? new GameIntegrationOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;

        var captureBackend = DetermineCaptureBackend();
        _capabilities = new GameHarnessCapabilities(
            ProcessDiscovery: true,
            ReplayLaunch: true,
            WindowCapture: captureBackend is not null,
            WindowFocus: true,
            GuardedInput: true,
            NativeLogTail: _logMonitor is not null,
            LifecycleWait: _logMonitor is not null,
            CaptureBackend: captureBackend,
            UnavailableReason: captureBackend is null
                ? "No supported window capture backend is available."
                : null);
    }

    private static string? DetermineCaptureBackend()
    {
        // GDI is the most reliable capture backend on all Windows versions.
        // WinRT GraphicsCapture requires packaged/registered process identity
        // and is not available to standalone tools.
        return Environment.OSVersion.Platform == PlatformID.Win32NT ? "gdi" : null;
    }

    // ── IGameHarnessPlatform ─────────────────────────────────

    public ValueTask<GameProcessObservation?> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 1. Find the game window
        IntPtr hWnd = NativeMethods.FindWindowW(GameWindowClassName, lpWindowName: null);
        if (hWnd == IntPtr.Zero)
        {
            return ValueTask.FromResult<GameProcessObservation?>(null);
        }

        // 2. Get process ID from window
        uint processId;
        _ = NativeMethods.GetWindowThreadProcessId(hWnd, out processId);
        if (processId == 0)
        {
            return ValueTask.FromResult<GameProcessObservation?>(null);
        }

        // 3. Open process with minimal rights
        using SafeProcessHandle hProcess = NativeMethods.OpenProcess(
            Win32Constants.PROCESS_QUERY_INFORMATION | Win32Constants.PROCESS_VM_READ,
            bInheritHandle: false,
            processId);

        if (hProcess.IsInvalid)
        {
            // Process exists but we can't open it — still report running
            return ValueTask.FromResult<GameProcessObservation?>(new GameProcessObservation(
                IsRunning: true,
                ProcessId: (int)processId,
                WindowHandle: (long)hWnd,
                ExecutablePath: string.Empty,
                ExecutableVersion: string.Empty,
                ExecutableSha256: string.Empty,
                IsForegroundWindow: NativeMethods.GetForegroundWindow() == hWnd,
                GameIntegrity: ProcessIntegrityLevel.Unknown,
                HarnessIntegrity: ProcessIntegrityLevel.Unknown,
                DpiX: 96,
                DpiY: 96,
                Lifecycle: BuildLifecycleEvidence()));
        }

        // 4. Get executable path
        string executablePath = GetExecutablePath(hProcess);
        string executableVersion = GetFileVersion(executablePath);
        string sha256 = ComputeSha256(executablePath);
        bool isForeground = NativeMethods.GetForegroundWindow() == hWnd;

        // 5. Get window DPI (placeholder — proper DPI needs GetDpiForWindow Win10 1607+)
        int dpiX = 96, dpiY = 96;

        var observation = new GameProcessObservation(
            IsRunning: true,
            ProcessId: (int)processId,
            WindowHandle: (long)hWnd,
            ExecutablePath: executablePath,
            ExecutableVersion: executableVersion,
            ExecutableSha256: sha256,
            IsForegroundWindow: isForeground,
            GameIntegrity: ProcessIntegrityLevel.Unknown,
            HarnessIntegrity: ProcessIntegrityLevel.Unknown,
            DpiX: dpiX,
            DpiY: dpiY,
            Lifecycle: BuildLifecycleEvidence());

        return ValueTask.FromResult<GameProcessObservation?>(observation);
    }

    public async ValueTask<ReplayLaunchResult> LaunchReplayAsync(
        ReplayLaunchRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string replaysDir = GetReplaysDirectory();

        // Copy the replay file to the game's replays directory
        string destFileName = Path.GetFileName(request.ReplayPath);
        string destPath = Path.Combine(replaysDir, destFileName);

        // Only copy if the source is different from destination or doesn't exist
        if (!string.Equals(request.ReplayPath, destPath, StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(replaysDir);
            File.Copy(request.ReplayPath, destPath, overwrite: true);
        }

        // Launch via shell execute — same as double-clicking in Explorer
        var psi = new ProcessStartInfo
        {
            FileName = destPath,
            UseShellExecute = true,
        };

        Process? process = Process.Start(psi);
        int processId = process?.Id ?? 0;

        // Don't wait for the process — just fire and return
        if (process is not null)
        {
            // Detach — the game manages its own lifetime
            process.Dispose();
        }

        return new ReplayLaunchResult(
            Guid.CreateVersion7(_timeProvider.GetUtcNow()),
            processId,
            _timeProvider.GetUtcNow());
    }

    public async ValueTask<WindowCaptureResult> CaptureWindowAsync(
        WindowCaptureRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IntPtr hWnd = checked((IntPtr)request.WindowHandle);
        if (!NativeMethods.IsWindow(hWnd))
        {
            throw new Win32Exception("Game window is no longer valid.");
        }

        NativeMethods.GetWindowRect(hWnd, out RECT rect);
        int width = rect.Width;
        int height = rect.Height;

        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("Game window has zero-area client region.");
        }

        // GDI capture: BitBlt from screen DC
        string outputPath = Path.Combine(
            request.OutputDirectory,
            $"{request.FileStem}.png");

        Directory.CreateDirectory(request.OutputDirectory);

        using var bitmap = CaptureWindowGdi(hWnd, width, height);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        using var fileStream = File.Create(outputPath);
        data.SaveTo(fileStream);

        string sha256 = ComputeSha256(outputPath);

        return new WindowCaptureResult(
            outputPath,
            sha256,
            width,
            height,
            "gdi",
            _timeProvider.GetUtcNow());
    }

    public ValueTask<PlatformOperationResult> FocusWindowAsync(
        int processId,
        long windowHandle,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IntPtr hWnd = checked((IntPtr)windowHandle);
        if (!NativeMethods.IsWindow(hWnd))
        {
            return ValueTask.FromResult(new PlatformOperationResult(
                false, "harness.window_invalid", "Game window is no longer valid."));
        }

        // Show and restore the window
        NativeMethods.ShowWindow(hWnd, Win32Constants.SW_RESTORE);
        bool result = NativeMethods.SetForegroundWindow(hWnd);

        return ValueTask.FromResult(result
            ? new PlatformOperationResult(true, null, null)
            : new PlatformOperationResult(
                false, "harness.focus_denied", "SetForegroundWindow returned false."));
    }

    public ValueTask<PlatformOperationResult> SendReplayKeyAsync(
        int processId,
        long windowHandle,
        ReplayKeyControl control,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ushort virtualKey = MapReplayKeyToVirtualKey(control);
        SendKeyInput(virtualKey);

        return ValueTask.FromResult(new PlatformOperationResult(true, null, null));
    }

    public ValueTask<PlatformOperationResult> ClickReplayControlAsync(
        int processId,
        long windowHandle,
        ReplayClickControl control,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Click controls are mapped to the same key sequences as SendReplayKeyAsync
        // since WoT Blitz uses keyboard shortcuts for playback controls.
        ushort virtualKey = MapClickControlToVirtualKey(control);
        SendKeyInput(virtualKey);

        return ValueTask.FromResult(new PlatformOperationResult(true, null, null));
    }

    public async ValueTask<IReadOnlyList<NativeReplayLogEvent>> TailReplayLogAsync(
        LogTailRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_logMonitor is null)
        {
            return [];
        }

        var events = new List<NativeReplayLogEvent>();
        long sequence = 0;
        bool pastWatermark = string.IsNullOrEmpty(request.AfterWatermark);

        await foreach (ReplayLogEvent logEvent in _logMonitor.WatchAsync(cancellationToken)
            .WithCancellation(cancellationToken))
        {
            string watermark = logEvent.OpaqueSourceId.Value;
            if (!pastWatermark)
            {
                if (watermark == request.AfterWatermark)
                {
                    pastWatermark = true;
                }
                continue;
            }

            var nativeEvent = new NativeReplayLogEvent(
                Sequence: Interlocked.Increment(ref sequence),
                ObservedAtUtc: logEvent.ObservedAtUtc,
                EventType: logEvent.Kind.ToString(),
                State: MapLogKindToLifecycleState(logEvent.Kind),
                Watermark: watermark,
                LaunchCorrelationId: null // Log events don't carry correlation IDs
            );

            events.Add(nativeEvent);

            if (events.Count >= request.MaximumEvents)
            {
                break;
            }
        }

        return events;
    }

    public async ValueTask<ReplayStateWaitResult> WaitForReplayStateAsync(
        ReplayStateWaitRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_logMonitor is null)
        {
            return new ReplayStateWaitResult(
                false,
                new ReplayLifecycleEvidence(
                    ReplayLifecycleState.Unknown,
                    _timeProvider.GetUtcNow(),
                    string.Empty,
                    null,
                    null,
                    "platform"),
                TimeSpan.Zero);
        }

        var startedAt = _timeProvider.GetTimestamp();
        ReplayLifecycleState targetState = request.ExpectedState;

        using var timeoutCts = new CancellationTokenSource(request.Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);

        try
        {
            await foreach (ReplayLogEvent logEvent in _logMonitor.WatchAsync(linkedCts.Token)
                .WithCancellation(linkedCts.Token))
            {
                ReplayLifecycleState observed = MapLogKindToLifecycleState(logEvent.Kind);
                if (observed == targetState)
                {
                    var elapsed = _timeProvider.GetElapsedTime(startedAt);
                    return new ReplayStateWaitResult(
                        true,
                        new ReplayLifecycleEvidence(
                            observed,
                            logEvent.ObservedAtUtc,
                            logEvent.OpaqueSourceId.Value,
                            null,
                            null,
                            "native-log"),
                        elapsed);
                }
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            // Timed out
        }

        var totalElapsed = _timeProvider.GetElapsedTime(startedAt);
        return new ReplayStateWaitResult(
            false,
            new ReplayLifecycleEvidence(
                ReplayLifecycleState.NotRunning,
                _timeProvider.GetUtcNow(),
                string.Empty,
                null,
                null,
                "platform"),
            totalElapsed);
    }

    // ── Private helpers ──────────────────────────────────────

    private static string GetExecutablePath(SafeProcessHandle hProcess)
    {
        char[] buffer = new char[MaxExecutablePathChars];
        uint result = NativeMethods.GetModuleFileNameEx(
            hProcess, IntPtr.Zero, buffer, (uint)buffer.Length);
        return result > 0 ? new string(buffer, 0, (int)result) : string.Empty;
    }

    private static string GetFileVersion(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return string.Empty;
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return info.FileVersion ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ComputeSha256(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return string.Empty;
        }

        try
        {
            using var stream = File.OpenRead(path);
            byte[] hash = SHA256.HashData(stream);
            return Convert.ToHexStringLower(hash);
        }
        catch
        {
            return string.Empty;
        }
    }

    private string GetReplaysDirectory()
    {
        // WoT Blitz stores replays under %LOCALAPPDATA%\wotblitz\DAVAProject\replays
        // unless overridden by GameIntegrationOptions
        string? userDataRoot = _gameOptions.UserDataRoots.Count > 0
            ? _gameOptions.UserDataRoots[0]
            : null;
        if (!string.IsNullOrWhiteSpace(userDataRoot))
        {
            return Path.Combine(userDataRoot, ReplaysSubdirectory);
        }

        string localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "wotblitz", ReplaysSubdirectory);
    }

    private ReplayLifecycleEvidence BuildLifecycleEvidence() =>
        new(
            ReplayLifecycleState.Unknown,
            _timeProvider.GetUtcNow(),
            string.Empty,
            null,
            null,
            "platform");

    private static ReplayLifecycleState MapLogKindToLifecycleState(ReplayLogMarkerKind kind) =>
        kind switch
        {
            ReplayLogMarkerKind.OfflineReplayStarted => ReplayLifecycleState.OfflineReplayActive,
            ReplayLogMarkerKind.OfflineReplayStopped => ReplayLifecycleState.OfflineReplayStopped,
            ReplayLogMarkerKind.ReplayRecordingStarted => ReplayLifecycleState.OnlineBattle,
            ReplayLogMarkerKind.ReplayRecordingStopped => ReplayLifecycleState.NotRunning,
            _ => ReplayLifecycleState.Unknown,
        };

    private static ushort MapReplayKeyToVirtualKey(ReplayKeyControl control) =>
        control switch
        {
            ReplayKeyControl.TogglePause => Win32Constants.VK_SPACE,
            ReplayKeyControl.SpeedUp => Win32Constants.VK_2,
            ReplayKeyControl.SpeedDown => Win32Constants.VK_1,
            ReplayKeyControl.SeekForward => Win32Constants.VK_RIGHT,
            ReplayKeyControl.SeekBackward => Win32Constants.VK_LEFT,
            _ => throw new ArgumentOutOfRangeException(nameof(control)),
        };

    private static ushort MapClickControlToVirtualKey(ReplayClickControl control) =>
        control switch
        {
            ReplayClickControl.PlayPause => Win32Constants.VK_SPACE,
            ReplayClickControl.SpeedUp => Win32Constants.VK_2,
            ReplayClickControl.SpeedDown => Win32Constants.VK_1,
            ReplayClickControl.TimelineForward => Win32Constants.VK_RIGHT,
            ReplayClickControl.TimelineBackward => Win32Constants.VK_LEFT,
            _ => throw new ArgumentOutOfRangeException(nameof(control)),
        };

    private static void SendKeyInput(ushort virtualKey)
    {
        var inputs = new INPUT[2];

        // Key down
        inputs[0] = new INPUT
        {
            Type = InputType.Keyboard,
            Union = new INPUT_UNION
            {
                Keyboard = new KEYBDINPUT
                {
                    VirtualKeyCode = virtualKey,
                    ScanCode = 0,
                    Flags = Win32Constants.KEYEVENTF_KEYDOWN,
                    Time = 0,
                    ExtraInfo = IntPtr.Zero,
                },
            },
        };

        // Key up
        inputs[1] = new INPUT
        {
            Type = InputType.Keyboard,
            Union = new INPUT_UNION
            {
                Keyboard = new KEYBDINPUT
                {
                    VirtualKeyCode = virtualKey,
                    ScanCode = 0,
                    Flags = Win32Constants.KEYEVENTF_KEYUP,
                    Time = 0,
                    ExtraInfo = IntPtr.Zero,
                },
            },
        };

        uint sent = NativeMethods.SendInput(2, inputs, Marshal.SizeOf<INPUT>());
        if (sent != 2)
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"SendInput failed: only {sent}/2 events were injected.");
        }
    }

    private static SKBitmap CaptureWindowGdi(
        IntPtr hWnd, int width, int height)
    {
        // Get the window DC
        IntPtr hdcWindow = GetDC(hWnd);
        IntPtr hdcMem = CreateCompatibleDC(hdcWindow);
        IntPtr hBitmap = CreateCompatibleBitmap(hdcWindow, width, height);
        IntPtr hOld = SelectObject(hdcMem, hBitmap);

        try
        {
            BitBlt(hdcMem, 0, 0, width, height, hdcWindow, 0, 0, SRCCOPY);

            // Convert GDI bitmap to SkiaSharp bitmap via DIB sections
            var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            IntPtr pixelsPtr = bitmap.GetPixels();

            // Use GetDIBits to extract pixel data
            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height, // Negative = top-down
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0, // BI_RGB
                },
            };

            IntPtr hdcScreen = GetDC(IntPtr.Zero);
            try
            {
                _ = GetDIBits(hdcScreen, hBitmap, 0, (uint)height, pixelsPtr, ref bmi, 0);
            }
            finally
            {
                _ = ReleaseDC(IntPtr.Zero, hdcScreen);
            }

            return bitmap;
        }
        finally
        {
            SelectObject(hdcMem, hOld);
            DeleteObject(hBitmap);
            DeleteDC(hdcMem);
            _ = ReleaseDC(hWnd, hdcWindow);
        }
    }

    // ── GDI P/Invoke (local scope — used only by CaptureWindowGdi) ──

    private const int SRCCOPY = 0x00CC0020;
    private const int DIB_RGB_COLORS = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        // No palette — 32-bit uses no palette
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hDC, int width, int height);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(
        IntPtr hdcDest, int xDest, int yDest, int width, int height,
        IntPtr hdcSrc, int xSrc, int ySrc, int dwRop);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetDIBits(
        IntPtr hdc, IntPtr hbm, uint start, uint cLines,
        IntPtr lpvBits, ref BITMAPINFO lpbmi, uint usage);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    // ── IDisposable ──────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_logMonitor is IDisposable monitorDisposable)
        {
            monitorDisposable.Dispose();
        }

        if (_logParser is IDisposable parserDisposable)
        {
            parserDisposable.Dispose();
        }
    }
}
