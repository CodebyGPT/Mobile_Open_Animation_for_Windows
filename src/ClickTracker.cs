// Records real left-button clicks, in our own process only.
//
// This is what lets the animation tell "the user clicked something and this window is the
// result" from "this window opened on its own". Without it every window - including ones
// that no one asked for, like Steam popping a login window after an update - grew out of
// wherever the mouse happened to be, which looks broken.
//
// It also covers the UAC case for free: the consent prompt runs on the secure desktop,
// where a low-level mouse hook never sees the click, so a window that appears after a UAC
// consent simply has no recent click and grows in place instead.
//
// WH_MOUSE_LL is installed in our process only. Nothing is injected anywhere.

using System.Runtime.InteropServices;
using System.Text;   // StringBuilder, for the class name under a click

namespace Moa;

internal static class ClickTracker
{
    public const int DefaultGraceMs = 1200;

    /// <summary>
    /// How long after a click a process may still be the one that click started.
    ///
    /// The mod bounds the same number at 2000 ms (mobile-open-animation.wh.cpp:452, kLaunchGraceMs)
    /// from its own measurement of click -> process-created latency: p50 141 ms, p75 1437 ms, and
    /// 82% of the reads inside 2000 ms.
    /// </summary>
    public const int LaunchGraceMs = 2000;

    /// <summary>
    /// How old the click record may be before it is discarded outright, whatever the process did.
    ///
    /// The lag test below does not expire the record by itself: a process started by a click and
    /// showing another window minutes later, with no clicks in between, satisfies it, because the
    /// process's age and the click's age grow together so the difference stays small. The mod bounds
    /// the record's own age at 30000 ms for the same reason (mobile-open-animation.wh.cpp:435,
    /// kClickValidMs), after finding that an in-process click let a 27-second-old one through, and
    /// every Explorer window then grew out of that stale spot (wh.cpp:594).
    /// </summary>
    public const int ClickValidMs = 30000;

    private static IntPtr _hook;
    private static Native.HookProc? _proc;

    public static long LastTick;
    public static int LastX, LastY;

    /// <summary>Which shell surface the last click landed on. See ClassifyClick.</summary>
    public enum Surface { None, Desktop, Taskbar }
    public static Surface LastSurface;

    /// <summary>
    /// The top-level class the last click was classified from, for the log - whatever it turned out to
    /// be, so a point that was refused can be read rather than guessed at. Empty when there was no
    /// window under the point at all.
    /// </summary>
    public static string LastSurfaceClass = "";

    public static void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _proc = Callback;
        _hook = Native.SetWindowsHookExW(Native.WH_MOUSE_LL, _proc,
                                         Native.GetModuleHandleW(null), 0);
        Log.Write($"mouse hook: {(_hook != IntPtr.Zero ? "installed" : "FAILED")}");
    }

    public static void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    /// <summary>Was there a real left-button click within the last <paramref name="graceMs"/>?</summary>
    public static bool HasRecentClick(int graceMs)
        => LastTick != 0 && Compat.TickCount64 - LastTick <= graceMs;

    /// <summary>
    /// Whether the last click started the process that owns a window, rather than merely happening
    /// near it in time.
    ///
    /// This covers what a flat grace window cannot, and must not be widened to cover: a cold
    /// Chromium start puts its process up ~100 ms after the click but its window two or three
    /// seconds later, so by the time the window appears the click is past any grace short enough to
    /// be safe. The test here is causal instead - the process did not exist when the click was made -
    /// so that window still grows out of the icon, with no extra room for an unrelated click.
    ///
    /// Borrowed from the mod, which does the same subtraction (mobile-open-animation.wh.cpp:585):
    /// process-created minus click, unsigned, so a click *newer* than the process wraps to a huge
    /// value and is rejected rather than being read as a launch.
    /// </summary>
    public static bool LaunchedByClick(uint pid)
    {
        if (LastTick == 0) return false;
        long clickAge = Compat.TickCount64 - LastTick;
        if (clickAge > ClickValidMs) return false;  // the record itself has expired
        long age = ProcessAgeMs(pid);
        if (age < 0) return false;                  // unreadable counts as "not a launch"
        long lag = clickAge - age;
        return lag >= 0 && lag <= LaunchGraceMs;
    }

    /// <summary>Milliseconds since the process was created, or -1 when it cannot be read.</summary>
    private static long ProcessAgeMs(uint pid)
    {
        IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return -1;
        try
        {
            if (!Native.GetProcessTimes(h, out var created, out _, out _, out _)) return -1;
            long ticks = ((long)created.High << 32) | created.Low;
            return (long)(DateTime.UtcNow - DateTime.FromFileTimeUtc(ticks)).TotalMilliseconds;
        }
        catch { return -1; }
        finally { Native.CloseHandle(h); }
    }

    /// <summary>The last click's classification, as one phrase, for the log.</summary>
    public static string DescribeLastSurface()
    {
        string what = LastSurfaceClass.Length > 0 ? LastSurfaceClass : "no window there";
        switch (LastSurface)
        {
            case Surface.Desktop: return $"the desktop ({what})";
            case Surface.Taskbar: return $"the taskbar ({what})";
            default:              return $"neither the desktop nor the taskbar ({what})";
        }
    }

    /// <summary>
    /// Which shell surface a top-level window's class is, or None. Shared: the click asks it of the
    /// window under the pointer, and the closing animation asks it again of whatever is in front of the
    /// remembered point by then - see Animator.ShellSurfaceStillAt - so both ends of a window's life use
    /// one list of class names rather than two that could drift apart.
    /// </summary>
    public static Surface SurfaceOfClass(string cls)
    {
        switch (cls)
        {
            case "Shell_TrayWnd":
            case "Shell_SecondaryTrayWnd": return Surface.Taskbar;
            case "Progman":
            case "WorkerW":                return Surface.Desktop;
            default:                       return Surface.None;
        }
    }

    /// <summary>
    /// Whether a click landed on the desktop or on the taskbar, which is what decides whether a window
    /// opened by it may return there when it closes.
    ///
    /// The question is asked at the moment of the click, because that is the only moment the answer
    /// exists: the window the click is about to open very often covers the icon it was on. One
    /// WindowFromPoint and two more user32 calls in this process - nothing is sent to the shell and
    /// nothing is injected.
    ///
    /// The class of the TOP-LEVEL window under the point is what is compared, because those four names
    /// belong to the shell and to nothing else, and because WindowFromPoint gives the deepest child -
    /// over an icon that is inside the desktop's SysListView32, over a taskbar button it is one of the
    /// taskbar's own child classes.
    ///
    /// The Start menu and the search flyout are top-level windows of their own classes, so a click on a
    /// tile or on a search result comes back None. That is the point of the whole check: those flyouts
    /// are gone by the time the window closes, so the place the click was made no longer exists, and a
    /// card shrinking towards a spot in an empty Start-menu area would be a lie.
    /// </summary>
    private static Surface ClassifyClick(POINT pt)
    {
        IntPtr h = Native.WindowFromPoint(pt);
        if (h == IntPtr.Zero) { LastSurfaceClass = ""; return Surface.None; }

        IntPtr root = Native.GetAncestor(h, Native.GA_ROOT);
        if (root == IntPtr.Zero) root = h;

        var sb = new StringBuilder(64);
        Native.GetClassNameW(root, sb, 64);
        LastSurfaceClass = sb.ToString();
        return SurfaceOfClass(LastSurfaceClass);
    }

    private static IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        // Must be quick and must never throw: this runs on the input path.
        try
        {
            if (code >= 0 && (uint)wParam == Native.WM_LBUTTONDOWN)
            {
                var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                LastX = info.Pt.X;
                LastY = info.Pt.Y;
                LastSurface = ClassifyClick(info.Pt);
                LastTick = Compat.TickCount64;
            }
        }
        catch { }
        return Native.CallNextHookEx(_hook, code, wParam, lParam);
    }
}
