// The other half of "what did the user just activate": a key press, so that a window opened with Enter can
// grow out of the place the user was looking at instead of out of its own middle.
//
// The mouse has a point and ClickTracker keeps it. A key press has no point; what it has is a FOCUS, and
// that focus is only knowable at the moment the key goes down - press Enter on a search result and the
// flyout is gone within a frame, long before the application's window is created. So the sampling happens
// inside the hook, on the same principle as the click: capture now, decide later.
//
// What is captured is the focused CONTROL rather than the foreground window, because that is what the user
// was looking at: for the Run box it is the text field, for a taskbar button it is that button, and for a
// XAML surface - the Start menu and search, on Windows 10 and 11 alike - it is the whole surface, since the
// elements inside it are not window handles at all.
//
// Deliberately narrow: only the shell's own surfaces are accepted, because those are the places a program
// can be started from with the keyboard alone and where "grow out of the thing you were using" is true. The
// taskbar, the Start menu, search and the Run box, and nothing else. A terminal running a command, a
// document, a third-party launcher: no anchor, and the window grows in place exactly as it did before this
// existed.
//
// The desktop is in that group, and for a reason worth writing down rather than rediscovering: the icon
// Enter activated is an item inside the shell's list view, not a window of its own. Reading its position
// would take either the list-view messages, which carry pointers into the target process and do not work
// across processes, or a UI Automation client - and the path this feeds has two milliseconds to hide a
// window. So a desktop launch grows in place, and the log says that is what happened.

using System.Runtime.InteropServices;
using System.Text;

namespace Moa;

internal static class KeyTracker
{
    /// <summary>
    /// How long after a key press a window may still be attributed to it. The same shape as the click's grace.
    /// </summary>
    public const int ActivationGraceMs = 1200;

    /// <summary>
    /// How long the press that started a process stays usable.
    ///
    /// Shorter than the click's thirty seconds on purpose: Enter is pressed all day and the mouse is not, so
    /// the same age means less here. It is still long enough for the case the bound exists for, which is a
    /// process started by the press that shows a window of its own some seconds later.
    /// </summary>
    private const int KeyValidMs = 5000;

    /// <summary>When the last Enter went down, on the same clock as everything else.</summary>
    public static long LastTick;

    /// <summary>What the last one was pressed on, for the log. Also set when it could not be used.</summary>
    public static string LastSource = "";

    /// <summary>Why the last press could not be used as an anchor, for the log.</summary>
    public static string LastReason = "no Enter has been pressed";

    private static POINT _anchor;
    private static bool _haveAnchor;

    private static IntPtr _hook;
    private static Native.HookProc? _proc;

    public static void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _proc = Callback;
        _hook = Native.SetWindowsHookExW(Native.WH_KEYBOARD_LL, _proc,
                                         Native.GetModuleHandleW(null), 0);
        Log.Write($"keyboard hook: {(_hook != IntPtr.Zero ? "installed" : "FAILED")}");
    }

    public static void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    /// <summary>
    /// The point a window started by this key press should grow out of, when there is one.
    ///
    /// False means there is nothing usable - nothing was pressed, it was pressed somewhere that is not the
    /// shell, or it is too old to be this window's cause - and the caller grows the window in place. The
    /// reason is left in LastReason so that the caller's log line can say which it was.
    /// </summary>
    public static bool TryAnchor(uint pid, out POINT anchor)
    {
        anchor = default;
        if (LastTick == 0) return false;
        if (!_haveAnchor)
        {
            LastReason = $"the last Enter was on {LastSource}";
            return false;
        }

        long keyAge = Compat.TickCount64 - LastTick;
        if (keyAge > ActivationGraceMs)
        {
            // Not recent, so it is only this window's cause if the process did not exist when it was pressed
            // - the same reasoning the click anchor uses, and the only thing that can tell a launch from a
            // window that opened by itself.
            if (keyAge > KeyValidMs)
            {
                LastReason = $"the last Enter was {keyAge} ms ago, past the {KeyValidMs} ms a press stays usable";
                return false;
            }
            long age = ClickTracker.ProcessAgeMs(pid);
            if (age < 0)
            {
                LastReason = "the process's age could not be read";
                return false;
            }
            long lag = keyAge - age;
            if (lag < 0)
            {
                LastReason = $"that Enter was pressed when the process was already {age} ms old";
                return false;
            }
            if (lag > ClickTracker.LaunchGraceMs)
            {
                LastReason = $"the process started {lag} ms after that Enter, past the " +
                             $"{ClickTracker.LaunchGraceMs} ms bound";
                return false;
            }
        }

        anchor = _anchor;
        return true;
    }

    /// <summary>
    /// Samples where the focus was, now, because in a moment it will not be knowable.
    /// </summary>
    private static void Capture()
    {
        LastTick = Compat.TickCount64;
        _haveAnchor = false;
        LastSource = "nowhere that could be found";

        // The order here is deliberate, and it is not the obvious one. GetForegroundWindow reads a value and
        // cannot block. GetGUIThreadInfo asks another thread about its own state, and a thread that is not
        // answering can leave that call waiting - inside a hook callback that every key press in the system
        // is queued behind, so the whole keyboard would wait with it. So the cheap question is asked first and
        // the fine one only of a shell surface, which is the only place its answer would change anything: a
        // window that is going to be refused anyway is refused without going near its thread.
        IntPtr foreground = Native.GetForegroundWindow();
        if (foreground == IntPtr.Zero) return;

        IntPtr root = Native.GetAncestor(foreground, Native.GA_ROOT);
        if (root == IntPtr.Zero) root = foreground;

        var cls = new StringBuilder(64);
        Native.GetClassNameW(root, cls, 64);
        string clsName = cls.ToString();

        if (clsName is "Progman" or "WorkerW")
        {
            LastSource = "the desktop, whose icon position cannot be read";
            return;
        }
        if (!IsShellSurface(clsName, root))
        {
            LastSource = $"{clsName}, which is not a shell surface";
            return;
        }

        // The control inside that surface, which is the point worth having rather than the surface itself:
        // the Run box's text field, one button on the taskbar. Asking for it is what the paragraph above is
        // about, and it is only asked here.
        IntPtr target = foreground;
        var gti = new Native.GUITHREADINFO { CbSize = (uint)Marshal.SizeOf<Native.GUITHREADINFO>() };
        if (Native.GetGUIThreadInfo(0, ref gti) && gti.HwndFocus != IntPtr.Zero) target = gti.HwndFocus;

        if (!Native.GetWindowRect(target, out var r) || r.Right <= r.Left || r.Bottom <= r.Top)
        {
            LastSource = $"{clsName}, which reported no useful rectangle";
            return;
        }

        _anchor = new POINT { X = (r.Left + r.Right) / 2, Y = (r.Top + r.Bottom) / 2 };
        _haveAnchor = true;
        LastSource = clsName;
    }

    /// <summary>
    /// Whether this is one of the places the shell itself starts a program from.
    ///
    /// The names are the shell's, and the XAML ones cover both versions: the Start menu and search are a
    /// Windows.UI.Core.CoreWindow on Windows 10 and one of the island classes on Windows 11, and there is no
    /// point in the program caring which is which.
    /// </summary>
    private static bool IsShellSurface(string cls, IntPtr root)
    {
        switch (cls)
        {
            case "Shell_TrayWnd":
            case "Shell_SecondaryTrayWnd":
            case "Windows.UI.Core.CoreWindow":
            case "XamlExplorerHostIslandWindow":
            case "Microsoft.UI.Content.PopupWindowSiteBridge":
            case "Windows.UI.Composition.DesktopWindowContentBridge":
            case "Windows.UI.Input.InputSite.WindowClass":
                return true;

            case "#32770":
                // The Run box is a plain dialog, and so is every picker and every confirmation in the system:
                // the class cannot tell them apart. The process can - the Run box belongs to the shell - and
                // the shell's process is read from its own window rather than by looking for explorer.exe by
                // name, which is what makes this work when the shell is something else.
                Native.GetWindowThreadProcessId(root, out uint pid);
                return pid != 0 && pid == ShellPid();

            default:
                return false;
        }
    }

    private static uint _shellPid;

    private static uint ShellPid()
    {
        if (_shellPid != 0) return _shellPid;
        IntPtr shell = Native.GetShellWindow();
        if (shell != IntPtr.Zero) Native.GetWindowThreadProcessId(shell, out _shellPid);
        return _shellPid;
    }

    private static IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        // Must be quick and must never throw: this runs on the input path, for every key press in the system.
        // The work is kept to the one key that launches things, because sampling costs a handful of calls and
        // keys arrive far more often than clicks do.
        try
        {
            if (code >= 0 && ((uint)wParam == Native.WM_KEYDOWN || (uint)wParam == Native.WM_SYSKEYDOWN))
            {
                var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
                // VK_RETURN is the main Enter and the numeric keypad's Enter both: the latter arrives as the
                // same virtual key with the extended flag set, so one test covers the two.
                if (info.VkCode == Native.VK_RETURN) Capture();
            }
        }
        catch { }
        return Native.CallNextHookEx(_hook, code, wParam, lParam);
    }
}
