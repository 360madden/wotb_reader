using System.Diagnostics;
using System.Runtime.InteropServices;
using WotBTreader.Overlay.ViewModels;

namespace WotBTreader.Overlay.Windowing;

/// <summary>Validated screen bounds for the target game window.</summary>
internal readonly record struct GameWindowBounds(int Left, int Top, int Width, int Height);

/// <summary>Bounded result of probing the target game window.</summary>
internal enum GameWindowProbeState
{
    NotFound,
    Ambiguous,
    BoundsUnavailable,
    BoundsInvalid,
    Ready,
}

/// <summary>Result of a target-window probe. Bounds are meaningful only for <see cref="GameWindowProbeState.Ready"/>.</summary>
internal readonly record struct GameWindowProbe(GameWindowProbeState State, GameWindowBounds Bounds)
{
    public static GameWindowProbe NotFound() => new(GameWindowProbeState.NotFound, default);

    public static GameWindowProbe Ambiguous() => new(GameWindowProbeState.Ambiguous, default);

    public static GameWindowProbe BoundsUnavailable() => new(GameWindowProbeState.BoundsUnavailable, default);

    public static GameWindowProbe BoundsInvalid() => new(GameWindowProbeState.BoundsInvalid, default);

    public static GameWindowProbe Ready(GameWindowBounds bounds) => new(GameWindowProbeState.Ready, bounds);
}

/// <summary>Win32 seam used by the HUD's game-window tracker.</summary>
internal interface IGameWindowTracker
{
    GameWindowProbe Probe();

    bool TryPositionOverlay(IntPtr overlayHandle, GameWindowBounds bounds);
}

/// <summary>Result of one deterministic tracking attempt.</summary>
internal readonly record struct GameWindowTrackingResult(
    HudGameWindowState State,
    bool IsTracking,
    bool TrackingStarted,
    GameWindowBounds? Bounds);

/// <summary>
/// Pure orchestration for one tracking tick. It keeps Win32 calls injectable so
/// state transitions and alignment failures can be tested without launching
/// the game or depending on a desktop window.
/// </summary>
internal static class GameWindowTrackingCoordinator
{
    public static GameWindowTrackingResult Track(
        IGameWindowTracker tracker,
        IntPtr overlayHandle,
        bool wasTracking)
    {
        ArgumentNullException.ThrowIfNull(tracker);

        GameWindowProbe probe = tracker.Probe();
        switch (probe.State)
        {
            case GameWindowProbeState.NotFound:
                return new(HudGameWindowState.NotFound, false, false, null);
            case GameWindowProbeState.Ambiguous:
                return new(HudGameWindowState.Ambiguous, false, false, null);
            case GameWindowProbeState.BoundsUnavailable:
                return new(HudGameWindowState.BoundsUnavailable, false, false, null);
            case GameWindowProbeState.BoundsInvalid:
                return new(HudGameWindowState.BoundsInvalid, false, false, null);
            case GameWindowProbeState.Ready:
                if (!tracker.TryPositionOverlay(overlayHandle, probe.Bounds))
                {
                    return new(HudGameWindowState.RepositionFailed, false, false, probe.Bounds);
                }

                return new(
                    HudGameWindowState.Tracking,
                    true,
                    !wasTracking,
                    probe.Bounds);
            default:
                return new(HudGameWindowState.NotFound, false, false, null);
        }
    }
}

/// <summary>Production Windows implementation of the injectable window seam.</summary>
internal sealed class Win32GameWindowTracker : IGameWindowTracker
{
    // The installed client currently exposes "WoT Blitz", while older builds
    // used "World of Tanks Blitz". Match the executable identity instead of a
    // localized/renamed caption so startup and tracking do not disagree.
    private const string GameProcessName = "wotblitz";
    private const int MinimumWindowWidth = 320;
    private const int MinimumWindowHeight = 200;
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public GameWindowProbe Probe()
    {
        List<IntPtr> windows = [];
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(GameProcessName);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return GameWindowProbe.NotFound();
        }

        foreach (Process process in processes)
        {
            using (process)
            {
                try
                {
                    process.Refresh();
                    if (process.HasExited || process.MainWindowHandle == IntPtr.Zero)
                    {
                        continue;
                    }

                    IntPtr window = process.MainWindowHandle;
                    if (IsWindowVisible(window))
                    {
                        windows.Add(window);
                    }
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException
                        or System.ComponentModel.Win32Exception
                        or NotSupportedException)
                {
                    // A process can exit between enumeration and inspection.
                    // Treat an uninspectable candidate as unavailable, never as
                    // proof that a different window is the game.
                }
            }
        }

        if (windows.Count == 0)
        {
            return GameWindowProbe.NotFound();
        }

        if (windows.Count != 1)
        {
            return GameWindowProbe.Ambiguous();
        }

        if (IsIconic(windows[0]))
        {
            return GameWindowProbe.BoundsInvalid();
        }

        if (!GetWindowRect(windows[0], out RECT rect))
        {
            return GameWindowProbe.BoundsUnavailable();
        }

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        return width < MinimumWindowWidth || height < MinimumWindowHeight
            ? GameWindowProbe.BoundsInvalid()
            : GameWindowProbe.Ready(new GameWindowBounds(rect.Left, rect.Top, width, height));
    }

    public bool TryPositionOverlay(IntPtr overlayHandle, GameWindowBounds bounds) =>
        SetWindowPos(
            overlayHandle,
            HwndTopmost,
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            SwpNoActivate);
}
