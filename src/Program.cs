// Mobile Open Animation for Windows - standalone.
//
// No settings window: everything is the tray icon's right-click menu, and the two lists a
// menu cannot edit live in MobileOpenAnimation.ini next to the exe.
//
// The design is the one the spikes validated:
//   * watch for a window being created (no injection, we are told before it is visible)
//   * hide it by adding WS_EX_LAYERED + alpha 0, and remember exactly how to put it back
//   * grow our own topmost panel from the click point to the window rect, carrying the icon
//   * once the app has painted, fade the panel out and put the real window back

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Moa;

internal static class Program
{
    internal const int MenuLangEn = 1, MenuLangZh = 2, MenuAutoStart = 3,
                       MenuConfig = 4, MenuAbout = 5, MenuExit = 6, MenuCloseAnim = 7,
                       MenuDynCorner = 8, MenuReturnOrigin = 9, MenuDebugEnable = 10;

    private static Settings _s = null!;
    internal static Settings S => _s;
    internal static Animator A => _anim;
    private static HideGuard _guard = null!;
    private static Watcher _watcher = null!;

    /// <summary>
    /// Whether the logon task exists, cached so the tray menu opens instantly - schtasks takes tens of
    /// milliseconds and the menu is built on every right click. Refreshed at start-up, on every right
    /// click, and after a change.
    /// </summary>
    internal static bool AutoStartOn;
    private static TrayHost _host = null!;
    private static Animator _anim = null!;

    [STAThread]
    private static int Main()
    {        // A WinExe has no console, and the CLR's own crash dialog says nothing useful, so
        // every failure has to be written down and shown.
        try { return Run(); }
        catch (Exception ex) { Report("startup", ex); return 1; }
    }

    internal static bool IsElevated()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>
    /// The single-instance guard, held for the whole life of the process and never disposed - and that is
    /// the point of it rather than an oversight. A named Mutex that only lives inside the method that made
    /// it is collected once that method returns, its handle closes with it, and the next copy of the
    /// program then starts perfectly happily. Keeping the reference here is what makes the object, and
    /// therefore the name, exist until the process ends; the process ending is also what releases it, which
    /// is why there is nothing to dispose.
    /// </summary>
    private static Mutex? _singleInstance;

    /// <summary>
    /// True when this is the only copy of the program running in this session, false when one is already
    /// up.
    ///
    /// Local\ rather than Global\, because this is a per-session tray application and two users logged in
    /// at once are entitled to their own copy - the namespace is what scopes the name to the session, so
    /// the name itself says nothing about the user. Nothing else about the program is per-user either way.
    ///
    /// Created first rather than opened: a Mutex that already exists is not owned by the caller, and the
    /// flag out of the constructor is exactly the answer wanted. A copy that lost a race with another one
    /// starting at the same instant is the same case as one that lost by an hour, and both are refused.
    /// </summary>
    private static bool OnlyCopyInThisSession()
    {
        _singleInstance = new Mutex(true, @"Local\MobileOpenAnimationForWindows", out bool first);
        return first;
    }

    /// <summary>
    /// The last resort: a failure bad enough that the program cannot go on. The box is always shown,
    /// because it is the one thing that cannot be missed by somebody who was not looking at a log - and
    /// the file is written only while debug mode is on, since "no log file unless it was asked for" has no
    /// exception for crashes. The message says which of the two happened rather than promising a file that
    /// is not there.
    /// </summary>
    internal static void Report(string where, Exception ex)    {
        try
        {
            string tail;
            if (Log.On)
            {
                string path = Log.ErrorPath;
                File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss}] {where}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
                tail = $"{Environment.NewLine}{Environment.NewLine}Written to:{Environment.NewLine}{path}";
            }
            else
            {
                tail = $"{Environment.NewLine}{Environment.NewLine}Turn Debug mode on in the tray menu to" +
                       $" have this written down.";
            }
            Native.MessageBoxW(IntPtr.Zero,
                $"{where}: {ex.GetType().Name}{Environment.NewLine}{Environment.NewLine}" +
                $"{ex.Message}{tail}",
                "Mobile Open Animation for Windows", 0x00000010 /* MB_ICONERROR */);
        }
        catch { }
    }

    private static int Run()
    {
        // Settings first, because debug mode is one of them and it decides whether the log is written at
        // all: with it off there is no file to write a first line into, and everything below this is silent
        // by design rather than by accident. Log.Start is what opens the run in the log, so it comes after
        // the switch is known, not before.
        _s = Settings.Load();
        Log.On = _s.DebugMode;
        Log.Start("startup");
        Log.Write($"pid={Native.GetCurrentProcessId()} lang={_s.Language} ini={Settings.IniPath}");
        Log.Write($"NOTIFYICONDATAW size={Marshal.SizeOf<NOTIFYICONDATAW>()} (the shell expects 976 on x64)");
        // UIPI: we can only touch windows of processes at the same or a lower integrity
        // level, and Shell_NotifyIcon is a message to Explorer's tray window, which is
        // subject to the same rule. If this says False while the tray icon only appears
        // when elevated, that is the whole explanation for both symptoms.
        Log.Write($"elevated={IsElevated()}");
        Strings.Use(_s.Language);

        // Silent on purpose. A tray program that is already running needs no dialog to say so: the tray
        // icon is next to the clock, and being told "already running" by a second copy of something you
        // have just asked for is noise rather than information. The log keeps the evidence.
        //
        // Placed before the elevation check rather than after: a copy that is already running is a better
        // explanation for "nothing happened" than missing rights are, so it is the one worth answering
        // first - and answering it with a warning box would send someone looking for a problem that is not
        // there.
        if (!OnlyCopyInThisSession())
        {
            Log.Write("another copy is already running in this session; leaving it to do the work");
            return 0;
        }

        // Required, not optional. The manifest asks Windows to elevate before the process starts;
        // this is the belt to that braces, and it is what a launch that bypassed the manifest gets
        // instead of silently animating nothing.
        if (!IsElevated())
        {
            Native.MessageBoxW(IntPtr.Zero, Strings.T("AdminRequired"),
                               "Mobile Open Animation for Windows", 0x00000030 /* MB_ICONWARNING */);
            return 1;
        }

        _host = new TrayHost();
        _host.Create();

        _guard = new HideGuard(_s.MaxHideMs);
        _anim = new Animator(_s, _guard);
        _watcher = new Watcher(_s, _anim.OnWindowCreated, _anim.OnWindowShown,
                               _anim.OnWindowClosed, _host.Alert);
        // A window that was never shown never really had an opening animation, so it must not be given
        // a closing one.
        _anim.OnAbandoned = _watcher.Forget;
        _anim.OnBecameVisible = _watcher.Remember;
        _watcher.Start();

        // Whatever happens, nothing stays hidden.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try { _guard.ReleaseAll(); } catch { }
            if (e.ExceptionObject is Exception ex) Report("unhandled", ex);
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { _guard.ReleaseAll(); } catch { } };

        // The animation thread owns every panel window and does the frame pacing itself, taking the
        // frame length from the primary display, with nothing to configure. It needs the tray window to
        // post its frame ticks to.
        _anim.Start(_host.Hwnd);

        // The logon task is read once here. ShowMenu must not run schtasks on every right click.
        // The exit code and output go to the log because "there is no task" and "schtasks could
        // not be run at all" both come out as false, and only one of them is worth worrying about.
        int autoRc = AutoStart.Query(out string autoOut);
        AutoStartOn = autoRc == 0;
        Log.Write($"autostart={AutoStartOn} (schtasks rc={autoRc}) {autoOut.Trim()}");

        // Running means working, and exiting means not working: there is deliberately no paused
        // state, because a menu item that silently turns the program off is a support question.
        _host.Notify($"Mobile Open Animation for Windows: watching (pid {Native.GetCurrentProcessId()})");

        _host.RunMessageLoop();

        _watcher.Dispose();
        _anim.Stop();
        _guard.ReleaseAll();
        _host.Destroy();
        return 0;
    }

    internal static void OnMenu(int id)
    {
        switch (id)
        {
            case MenuLangEn: _s.Language = Lang.English; Strings.Use(_s.Language); _s.Save(); break;
            // Greyed while the system's animation effects are off. A menu that was drawn before the
            // switch was flipped can still deliver the click, so it is refused here as well as there.
            case MenuCloseAnim:
                if (!Native.SystemAnimationsEnabled()) break;
                _s.CloseAnimation = !_s.CloseAnimation; _s.Save(); break;
            case MenuDynCorner:
                if (!Native.SystemAnimationsEnabled()) break;
                _s.DynamicCorner = !_s.DynamicCorner; _s.Save(); break;
            case MenuReturnOrigin:
                if (!Native.SystemAnimationsEnabled()) break;
                _s.ReturnToOrigin = !_s.ReturnToOrigin; _s.Save(); break;
            case MenuDebugEnable:
            {
                // On: the switch first, so the lines below are actually written, then the header that
                // opens this run in the file, then the settings that make the rest of the log readable.
                // Off: the closing line and a forced flush happen while it is still on, because after that
                // there is nothing left that writes anything - and the file is left closed rather than
                // growing a line per event for the rest of the session.
                if (!_s.DebugMode)
                {
                    _s.DebugMode = true;
                    Log.On = true;
                    Log.Start("debug mode enabled from the tray menu");
                    Log.Write($"pid={Native.GetCurrentProcessId()} lang={_s.Language} ini={Settings.IniPath}" +
                              $" log={Log.Path}");
                    Log.Flush(force: true);
                }
                else
                {
                    Log.Write("debug mode disabled from the tray menu; the file is closed from here");
                    Log.Flush(force: true);
                    Log.On = false;
                    _s.DebugMode = false;
                }
                _s.Save();
                break;
            }
            case MenuLangZh: _s.Language = Lang.ChineseSimplified; Strings.Use(_s.Language); _s.Save(); break;
            case MenuAutoStart:
            {
                // Reconcile first, act second. The check mark can be stale - the task may have been
                // created or removed by something else since the menu was drawn - so if the display
                // disagrees with reality, the click's job is to correct the display, and no
                // schtasks command is run at all. Only when the display was accurate is the click
                // read as "I want the opposite of this".
                bool shown = AutoStartOn;
                bool real = AutoStart.IsEnabled();
                if (real != shown)
                {
                    Log.Write($"autostart: menu showed {shown}, task is {real}; correcting the menu only");
                    AutoStartOn = real;
                    break;
                }

                bool wanted = !real;
                string err;
                bool now = AutoStart.Apply(wanted, out err);
                if (now != wanted)
                    Native.MessageBoxW(IntPtr.Zero,
                        Strings.T("AutoStartFailed") + "\n\n" + err,
                        "Mobile Open Animation for Windows", 0x00000030 /* MB_ICONWARNING */);
                else
                    AutoStartOn = now;
                break;
            }
            case MenuConfig:
                try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + Settings.IniPath + "\""); }
                catch (Exception ex)
                {
                    Log.Write($"open config folder failed: {ex.GetType().Name}: {ex.Message}");
                }
                break;
            case MenuAbout:
                // The version comes first because it is the thing most often wanted from this box: which build
                // am I running. Everything AGPL-3.0 section 0 asks an interactive interface to show is below
                // it, and the ini path is the one other thing people look here for.
                Native.MessageBoxW(IntPtr.Zero,
                    Strings.T("Version") + ": " + Build.Version + "\n\n" +
                    Strings.T("AboutText") + "\n\n" + Settings.IniPath,
                    Strings.T("About"), 0x00000040 /* MB_ICONINFORMATION */);
                break;
            case MenuExit:
                _guard.ReleaseAll();
                _host.RequestExit();
                break;
            default:
                break;
        }
    }
}

/// <summary>The hidden window that owns the tray icon and the menu.</summary>
internal sealed class TrayHost
{
    private IntPtr _hwnd = IntPtr.Zero;
    private Native.WndProcDelegate? _wndProc;
    private IntPtr _iconLarge = IntPtr.Zero, _iconSmall = IntPtr.Zero;
    private string _tip = "Mobile Open Animation for Windows";
    private bool _trayAdded;

    /// <summary>
    /// The icon every tray entry is made with, kept because an entry can be taken away from us and the
    /// icon has to be handed over again when it is put back. See WantTray.
    /// </summary>
    private IntPtr _trayIcon = IntPtr.Zero;

    /// <summary>
    /// Explorer's "the tray has been rebuilt" broadcast, registered in Create. 0 means the registration
    /// failed, and then there is nothing to listen for: the comparison in WndProc is skipped rather than
    /// being allowed to match every message that happens to carry the id 0.
    /// </summary>
    private uint _taskbarCreated;

    /// <summary>
    /// How many more times the watchdog may try to put the icon into the tray, out of the count set when
    /// one is asked for. Bounded on purpose: one attempt per second forever would fill the log with a
    /// line a second if the shell never came back, and thirty is far longer than Explorer needs to have a
    /// tray window after it has announced one.
    /// </summary>
    private int _trayRetriesLeft;
    private const int TrayRetries = 30;

    /// <summary>
    /// Shows a modal dialog. The watcher is given this as its sink, but it is not where anything is
    /// logged - the watcher calls the static Log for that. It exists for the one failure that leaves
    /// nothing working and that nobody would otherwise notice, such as the event hook refusing to
    /// install, so it must stay rare: every call stops the program until somebody dismisses it.
    /// </summary>
    public void Alert(string s) => Native.MessageBoxW(IntPtr.Zero, s, "Mobile Open Animation for Windows", 0x00000040);
    public void Notify(string s) => _tip = s.Length > 120 ? s.Substring(0, 120) : s;

    /// <summary>The host window, which is never shown. The animation thread posts WM_APP_TICK here.</summary>
    public IntPtr Hwnd => _hwnd;

    public void Create()
    {
        _wndProc = WndProc;
        var inst = Native.GetModuleHandleW(null);
        var cls = new WNDCLASSEXW
        {
            CbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            LpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            HInstance = inst,
            LpszClassName = Native.SelfClassName,
        };
        if (Native.RegisterClassExW(ref cls) == 0) throw new InvalidOperationException("RegisterClassExW failed");
        _hwnd = Native.CreateWindowExW(0, Native.SelfClassName, "Mobile Open Animation for Windows",
                                       0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);

        // Registered before the icon is added, so the handler is armed before there is anything that could
        // be lost. Explorer broadcasts this to every top-level window when it builds its tray, which it
        // does again after it crashes, is restarted by an update, or is restarted by hand.
        _taskbarCreated = Native.RegisterWindowMessageW("TaskbarCreated");

        // Use the exe's own icon; fall back to the system application icon.
        Native.ExtractIconExW(Compat.ExePath, 0, out _iconLarge, out _iconSmall, 1);
        _trayIcon = _iconSmall != IntPtr.Zero ? _iconSmall : Native.LoadIconW(IntPtr.Zero, (IntPtr)32512);

        Moa.Log.Write($"tray: TaskbarCreated=0x{_taskbarCreated:X8} icon={_trayIcon}" +
                      (_taskbarCreated == 0 ? " (registration failed; the icon cannot be restored)" : ""));
        WantTray();
        // The safety watchdog. Measured against what it protects: a window hidden by us must never be
        // left hidden, and the absolute cap on that is twenty seconds - so a second of granularity is
        // far more than enough, and four wakeups a second were never needed. It also flushes the log,
        // which while an animation is running happens at frame rate anyway, so a second is only the
        // worst case for a line written while nothing is happening.
        Native.SetTimer(_hwnd, (IntPtr)1, 1000, IntPtr.Zero);
    }

    private void AddTray(IntPtr icon)
    {
        var nid = NewNid();
        nid.UFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP;
        nid.HIcon = icon;
        nid.SzTip = _tip;
        _trayAdded = Native.Shell_NotifyIconW(Native.NIM_ADD, ref nid);
        Moa.Log.Write($"Shell_NotifyIcon(NIM_ADD) -> {_trayAdded} err={Marshal.GetLastWin32Error()} hwnd={_hwnd} icon={icon}");
    }

    /// <summary>
    /// Asks for the icon to be in the tray, and keeps asking for a while if the shell is not listening yet.
    ///
    /// The retry is for one specific instant. Explorer announces the new tray as soon as its taskbar window
    /// exists, and a Shell_NotifyIcon arriving in that same moment can still be refused - the same refusal a
    /// program started at logon gets when there is no tray at all yet. The watchdog timer is already running
    /// once a second, so it does the asking, and it stops asking after TrayRetries so that a shell which
    /// never comes back cannot become a line in the log every second.
    ///
    /// Nothing else about a restarted Explorer has to be redone, and that is by design rather than luck:
    /// everything else this program knows about the shell - what is under a point, where the taskbar is,
    /// which monitor a window is on - is asked for at the moment it is needed instead of being remembered.
    /// The tray icon was the one thing held rather than asked for.
    /// </summary>
    private void WantTray()
    {
        AddTray(_trayIcon);
        _trayRetriesLeft = _trayAdded ? 0 : TrayRetries;
    }

    private NOTIFYICONDATAW NewNid() => new()
    {
        CbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        Hwnd = _hwnd,
        UID = 1,
        UCallbackMessage = Native.WM_TRAYICON,
        SzTip = _tip,
        SzInfo = "",
        SzInfoTitle = "",
    };

    public void RunMessageLoop()
    {
        while (Native.PeekMessageW(out var m, IntPtr.Zero, 0, 0, Native.PM_REMOVE))
        {
            if (m.Message == 0x0012 /* WM_QUIT */) return;
            Native.TranslateMessage(ref m);
            Native.DispatchMessageW(ref m);
        }
        while (Native.GetMessageW(out var msg, IntPtr.Zero, 0, 0))
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessageW(ref msg);
        }
    }

    public void RequestExit() => Native.PostMessageW(_hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    public void Destroy()
    {
        if (_trayAdded)
        {
            var nid = NewNid();
            Native.Shell_NotifyIconW(Native.NIM_DELETE, ref nid);
            _trayAdded = false;
        }
        if (_iconLarge != IntPtr.Zero) Native.DestroyIcon(_iconLarge);
        if (_iconSmall != IntPtr.Zero) Native.DestroyIcon(_iconSmall);
        if (_hwnd != IntPtr.Zero) { Native.DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
    {
        try
        {
            // Explorer has rebuilt its tray: it crashed, an update replaced it, or someone restarted it.
            // Everything we had in the old one went with it, and the tray icon is this program's entire
            // interface - the menu, the language, Start with Windows, About and Exit are there and nowhere
            // else. Left unhandled, an Explorer restart produces a running program that cannot be reached
            // or stopped from the screen, with nothing to say why; and since a second copy now refuses to
            // start, not even by launching it again.
            //
            // Compared before the switch because a registered message's id is chosen by the system at run
            // time and is therefore not one of the constants a switch can hold.
            if (_taskbarCreated != 0 && msg == _taskbarCreated)
            {
                Moa.Log.Write("Explorer rebuilt the tray; asking for the icon again");
                WantTray();
                return IntPtr.Zero;
            }

            switch (msg)
            {
                case Native.WM_TRAYICON:
                    uint evt = (uint)(l.ToInt64() & 0xFFFF);
                    Moa.Log.Write($"WM_TRAYICON evt=0x{evt:X4}");
                    if (evt is Native.WM_RBUTTONUP or Native.WM_CONTEXTMENU) ShowMenu();
                    else if (evt == Native.WM_LBUTTONUP) ShowMenu();
                    return IntPtr.Zero;

                case Native.WM_COMMAND:
                    Program.OnMenu((int)(w.ToInt64() & 0xFFFF));
                    return IntPtr.Zero;

                case Native.WM_TIMER:
                    // The safety watchdog. It runs whether or not anything is animating, which is
                    // what would release a window if a hide were ever left behind.
                    Program.A.Tick();
                    // It is also where a tray entry the shell was not ready for gets asked for again:
                    // the broadcast says the tray exists, and this is the moment just after it.
                    if (!_trayAdded && _trayRetriesLeft > 0)
                    {
                        _trayRetriesLeft--;
                        AddTray(_trayIcon);
                    }
                    return IntPtr.Zero;

                case Native.WM_APP_TICK:
                    // One displayed frame, asked for by the animation thread.
                    Program.A.Tick();
                    return IntPtr.Zero;

                case Native.WM_CLOSE:
                    Native.DestroyWindow(hwnd);
                    return IntPtr.Zero;

                case Native.WM_DESTROY:
                    Native.PostQuitMessage(0);
                    return IntPtr.Zero;
            }
        }
        catch (Exception ex) { Program.Report("tray wndproc (msg " + msg + ")", ex); /* never let the tray thread die */ }
        return Native.DefWindowProcW(hwnd, msg, w, l);
    }

    private void ShowMenu()
    {
        // Re-checked here and not only at start-up: the task can be removed by anything else on the
        // machine - Task Scheduler, another user, a cleanup tool - and a check mark that disagrees
        // with reality is worse than none at all. Costs one schtasks run, tens of milliseconds,
        // before the menu appears.
        // IsEnabled, NOT the bare existence query. The menu and the click handler have to use the
        // same definition: when the menu used existence alone and the handler used the full field
        // check, the two disagreed, so every click concluded the display was stale and corrected
        // the menu instead of ever reaching the create or delete.
        Program.AutoStartOn = AutoStart.IsEnabled();

        IntPtr menu = Native.CreatePopupMenu();
        var lang = Native.CreatePopupMenu();
        // Checked, like the style submenu above it. Without it there is no way to see which language
        // is currently selected, which is the one thing a language menu exists to show.
        Native.AppendMenuW(lang, Native.MF_STRING |
            (Program.S.Language == Lang.English ? Native.MF_CHECKED : Native.MF_UNCHECKED),
            (IntPtr)Program.MenuLangEn, "English");
        Native.AppendMenuW(lang, Native.MF_STRING |
            (Program.S.Language == Lang.ChineseSimplified ? Native.MF_CHECKED : Native.MF_UNCHECKED),
            (IntPtr)Program.MenuLangZh, "简体中文");

        // The two animation toggles go grey while Windows' own animation effects are off, because the
        // program animates nothing then and a clickable "animate windows closing" would be a lie.
        // Their values are left alone, so they come back exactly as they were.
        uint animationGroup = Native.SystemAnimationsEnabled() ? 0u : Native.MF_GRAYED;

        Native.AppendMenuW(menu, Native.MF_STRING | animationGroup |
            (Program.S.CloseAnimation ? Native.MF_CHECKED : Native.MF_UNCHECKED),
            (IntPtr)Program.MenuCloseAnim, Strings.T("CloseAnim"));
        Native.AppendMenuW(menu, Native.MF_STRING | animationGroup |
            (Program.S.DynamicCorner ? Native.MF_CHECKED : Native.MF_UNCHECKED),
            (IntPtr)Program.MenuDynCorner, Strings.T("DynamicCorner"));
        Native.AppendMenuW(menu, Native.MF_STRING | animationGroup |
            (Program.S.ReturnToOrigin ? Native.MF_CHECKED : Native.MF_UNCHECKED),
            (IntPtr)Program.MenuReturnOrigin, Strings.T("ReturnToOrigin"));
        Native.AppendMenuW(menu, Native.MF_SEPARATOR, IntPtr.Zero, "");
        Native.AppendMenuW(menu, Native.MF_POPUP, lang, Strings.T("Language"));
        Native.AppendMenuW(menu, Native.MF_STRING |
            (Program.AutoStartOn ? Native.MF_CHECKED : Native.MF_UNCHECKED),
            (IntPtr)Program.MenuAutoStart, Strings.T("AutoStart"));

        // A submenu rather than one more toggle among the animation ones, because what belongs under it is
        // any number of separate diagnostics and none of them is a setting in the sense the others are: an
        // entry is off by default, has nothing to do with how the program looks, and turning it on is a
        // deliberate step taken to find out what the program is doing. One entry in it so far.
        var debug = Native.CreatePopupMenu();
        Native.AppendMenuW(debug, Native.MF_STRING |
            (Program.S.DebugMode ? Native.MF_CHECKED : Native.MF_UNCHECKED),
            (IntPtr)Program.MenuDebugEnable, Strings.T("DebugEnable"));
        Native.AppendMenuW(menu, Native.MF_POPUP, debug, Strings.T("DebugMode"));

        Native.AppendMenuW(menu, Native.MF_STRING, (IntPtr)Program.MenuConfig, Strings.T("OpenConfig"));
        Native.AppendMenuW(menu, Native.MF_SEPARATOR, IntPtr.Zero, "");
        Native.AppendMenuW(menu, Native.MF_STRING, (IntPtr)Program.MenuAbout, Strings.T("About"));
        Native.AppendMenuW(menu, Native.MF_STRING, (IntPtr)Program.MenuExit, Strings.T("Exit"));

        Native.GetCursorPos(out var p);
        Native.SetForegroundWindow(_hwnd);
        int cmd = Native.TrackPopupMenu(menu, Native.TPM_RIGHTBUTTON | Native.TPM_RETURNCMD,
                                        p.X, p.Y, 0, _hwnd, IntPtr.Zero);
        Native.DestroyMenu(menu);
        if (cmd > 0) Program.OnMenu(cmd);
    }
}
