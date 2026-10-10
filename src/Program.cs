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

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Moa;

internal static class Program
{
    internal const int MenuLangEn = 1, MenuLangZh = 2, MenuAutoStart = 3,
                       MenuConfig = 4, MenuAbout = 5, MenuExit = 6, MenuCloseAnim = 7,
                       MenuDynCorner = 8, MenuReturnOrigin = 9, MenuDebugEnable = 10, MenuBlacklist = 11,
                       MenuAdsWindow = 13, MenuAdsBootAd = 16, MenuDebugBootAd = 17;

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
        // The configuration this run starts from, so that a log is readable on its own: what was in force
        // and when it changed, rather than what a later reader has to infer from the ini's current state.
        _s.LogState("startup");
        Log.Write($"NOTIFYICONDATAW size={Marshal.SizeOf<NOTIFYICONDATAW>()} (the shell expects 976 on x64)");
        // UIPI: we can only touch windows of processes at the same or a lower integrity
        // level, and Shell_NotifyIcon is a message to Explorer's tray window, which is
        // subject to the same rule. If this says False while the tray icon only appears
        // when elevated, that is the whole explanation for both symptoms.
        Log.Write($"elevated={IsElevated()}");
        Strings.Use(_s.Language);

        // Before the first window is made. uxtheme takes the preference only while nothing has been created
        // with the old one, so a tray icon or a menu built before this would keep the theme it was built
        // with. It is also what makes the two boxes below - the already-running note is silent, the elevation
        // one is not - come up in the user's theme rather than always in the light one.
        Native.DarkMode.Apply();

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
        _anim.OnHandoff = OnHandoff;
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

        // The logon advertisement, if it is due. It runs on this thread like everything else - the card's
        // timer arrives in the message loop below - so it goes up before the loop starts and its countdown is
        // served by it.
        ShowBootAdIfDue();

        _host.RunMessageLoop();

        _watcher.Dispose();
        _anim.Stop();
        _guard.ReleaseAll();
        _host.Destroy();
        return 0;
    }

    /// <summary>
    /// The window advertisement: called the moment an opening animation has finished, with the window and the
    /// rectangle the card stopped at. Returning a window puts it over that rectangle; returning nothing leaves
    /// the application to be revealed as it always was.
    ///
    /// One rule decides it: **a program gets one advertisement, ever**. It was written as "a process gets one"
    /// first, and that is not the same rule - PowerToys' image resizer is one process per showing, so opening
    /// it twice played the advertisement twice, and its window is not PowerToys' main window either. What the
    /// rule is about is the program, so the program is what is remembered; see ProgramName. The main window
    /// gets it for the same reason as before - the first window of a program is its main one - and a dialog,
    /// a sub-feature or a second copy of the same program is refused, because it is the same program.
    ///
    /// Windows that never animate - which includes every blacklisted one, since those never reach an animation
    /// at all - are not offered one, because this is only ever called from an animation.
    /// </summary>
    private static IntPtr OnHandoff(IntPtr hwnd, uint pid, RECT rect, int radius)
    {
        if (!Ads.Allowed(_s.AdsVisible) || !_s.WindowAd) return IntPtr.Zero;

        string program = ProgramName(pid);
        if (_adsShown.Contains(program))
        {
            Log.Write($"ads: {program} has already had its advertisement; none for hwnd={hwnd}");
            return IntPtr.Zero;
        }

        string[] videos = Ads.Videos();
        if (videos.Length == 0) return IntPtr.Zero;

        bool muted = _s.AdsMuted;
        WireCard(radius);

        var card = AdsCard.ShowOver(rect, videos, Ads.CapSeconds, muted, _s.CloseAnimation);
        if (card == null) return IntPtr.Zero;

        // The window an advertisement covers is hidden while it is covered. That is what the feature is: an
        // advertisement in place of a window, not in front of one - and it is also the only arrangement in
        // which the Z-order cannot matter, because a card over a hidden window is the advertisement whatever
        // band the card ends up in. Said here rather than at the handoff because this is the program's own
        // decision about its own feature; the animator only needs to be told not to put the window back yet.
        //
        // The hold is what keeps the guard's watchdog from giving the window back mid-advertisement: the
        // watchdog's deadline is twenty seconds and the cap is six minutes, so without it a long advertisement
        // would be sitting over a window that had quietly come back.
        _anim.KeepHidden = hwnd;
        _anim.HeldWindow = hwnd;
        _guard.Hold(hwnd, Ads.CapSeconds * 1000 + 5000);
        card.WhileUp = _anim.KeepHeldWindowHidden;
        // The window comes along when the advertisement is dragged. It is invisible while this happens, and that
        // is the point: the advertisement is meant to be the window, so the two being in different places is
        // something the user would only notice when the advertisement ends. See Animator.MoveHeldWindow.
        card.Moved = _anim.MoveHeldWindow;
        card.Closed = () =>
        {
            Log.Write($"ads: the advertisement is gone; releasing hwnd={hwnd}");
            _anim.HeldWindow = IntPtr.Zero;
            _guard.Release(hwnd);
        };

        _adsShown.Add(program);
        Log.Write($"ads: window advertisement for {program} (pid {pid}) hwnd={hwnd} at " +
                  $"{rect.Left},{rect.Top} {rect.Right - rect.Left}x{rect.Bottom - rect.Top}," +
                  $" muted={muted} radius={radius}");
        return card.Hwnd;
    }

    /// <summary>
    /// The programs that have had their one advertisement. A name rather than a process id, because it is the
    /// program the rule is about, and a name that two processes of one program agree on: ProductName is the
    /// field Windows keeps for exactly this and PowerToys' own two executables both call themselves PowerToys,
    /// which is the case this went wrong on. Programs that leave it blank - many do - fall back to the path of
    /// the executable, which is the same answer for them as long as they do not ship two of them.
    ///
    /// In memory and deliberately not in the file: the feature is meant to start again from nothing when the
    /// program restarts, and a record that outlived the run would be a record of everything the user has ever
    /// opened.
    /// </summary>
    private static readonly HashSet<string> _adsShown = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Who each process turned out to be. Asked once per process, not once per window it opens.</summary>
    private static readonly Dictionary<uint, string> _programOf = new();

    private static string ProgramName(uint pid)
    {
        if (_programOf.TryGetValue(pid, out string? known)) return known;

        string name = $"pid {pid}";
        try
        {
            string path = Process.GetProcessById((int)pid).MainModule?.FileName ?? "";
            if (path.Length > 0)
            {
                string product = FileVersionInfo.GetVersionInfo(path).ProductName?.Trim() ?? "";
                name = product.Length > 0 ? product : path;
            }
        }
        // A process that has gone, or one this program is not allowed to look at. Falling back to the id keeps
        // the advertisement working and keeps it to one per process, which is weaker than the rule but never
        // wrong in the direction that annoys: it cannot play one that should not have been played.
        catch (Exception ex)
        {
            Log.Write($"ads: pid {pid} would not say which program it is ({ex.GetType().Name}); using its id");
        }

        _programOf[pid] = name;
        return name;
    }

    /// <summary>
    /// Puts the logon advertisement up, if this run has earned one.
    ///
    /// Asked of the same function that decides whether the menu's logon-ad entry can be clicked, rather than of
    /// a second copy of its conditions: a program whose menu says the switch is frozen must not then run one
    /// anyway, and the two going out of step is a mistake this codebase has already made once with the logon
    /// task.
    /// </summary>
    private static void ShowBootAdIfDue() => PlayBootAd(forced: false);

    /// <summary>
    /// Hands the card everything that comes from the settings rather than from the card itself: where it says
    /// what it is doing, what its mute control says, where to write down a mute the user asked for, and how it
    /// goes away.
    ///
    /// The collapse follows the program's own closing cards rather than a second opinion: the length is the
    /// animation length in the ini, the frame is the display's, the curve is the one its own animations move
    /// on, and whether it rounds off into a disc is the dynamic-corner switch. <paramref name="radius" /> is
    /// the corner the opening animation's card ended on, handed over by the animator, and is what the
    /// advertisement wears while it is up - zero for the logon advertisement, whose card is its own.
    /// </summary>
    private static void WireCard(int radius)
    {
        AdsCard.Trace = Log.Write;
        AdsCard.CaptionMute = Strings.T("AdsMute");
        AdsCard.CaptionUnmute = Strings.T("AdsUnmute");
        AdsCard.CaptionRemaining = Strings.T("AdsRemaining");
        AdsCard.CaptionSkip = Strings.T("AdsSkip");
        AdsCard.MuteRemembered = m => { _s.AdsMuted = m; _s.Save(); };
        AdsCard.Curve = Animator.Ease;
        AdsCard.CollapseMs = _s.DurationMs;
        AdsCard.FrameMs = (int)Math.Round(_anim.FrameMs);
        AdsCard.Radius = radius;
        AdsCard.RoundOff = _s.DynamicCorner;
    }

    /// <summary>
    /// Puts the logon advertisement up.
    ///
    /// Asked of the same function that decides whether the menu's logon-ad entry can be clicked, rather than of
    /// a second copy of its conditions: a program whose menu says the switch is frozen must not then run one
    /// anyway, and the two going out of step is a mistake this codebase has already made once with the logon
    /// task.
    ///
    /// <paramref name="forced" /> skips those conditions and nothing else - the Debug menu's entry runs one
    /// however the logon task and the logon-advertisement switch are set, because looking at the advertisement
    /// is the only thing that entry is for. What it cannot skip is the folder being empty, which is the one
    /// condition that is not a setting but a fact, and the submenu being hidden - see Ads.Allowed, which is a
    /// rule about the two switches rather than one of their conditions.
    ///
    /// The mute is whatever the last one was left at, which the mute control keeps up to date.
    /// </summary>
    private static void PlayBootAd(bool forced)
    {
        if (!Ads.Allowed(_s.AdsVisible))
        {
            Log.Write("ads: the advertisement submenu is hidden, so neither of the two switches is acted on");
            return;
        }

        if (!forced)
        {
            var ads = Ads.Menu(Ads.Available, AutoStartOn, _s.WindowAd, _s.BootAd);
            if (!ads.BootAdUsable || !ads.BootAd)
            {
                Log.Write($"ads: no logon advertisement (bootAd={ads.BootAd} usable={ads.BootAdUsable})");
                return;
            }
        }

        string[] videos = Ads.Videos();
        if (videos.Length == 0)
        {
            Log.Write($"ads: the logon advertisement is due but Ads is empty ({Ads.Folder})");
            return;
        }

        bool muted = _s.AdsMuted;
        WireCard(0);
        Log.Write($"ads: {videos.Length} videos, logon advertisement for at most " +
                  $"{Ads.CapSeconds}s, muted={muted}{(forced ? ", asked for from Debug mode" : "")}");
        var card = AdsCard.ShowBoot(videos, Ads.CapSeconds, muted, _s.CloseAnimation);
        if (card == null) Log.Write("ads: a card is already up, so no logon advertisement");
    }

    /// <summary>
    /// Makes the folder the videos go in, so that switching one of the two kinds on leaves a note of where the
    /// videos go rather than an advertisement with nothing to play. Nothing is said when neither is on.
    /// </summary>
    private static void NoteFolder()
    {
        if (!_s.WindowAd && !_s.BootAd) return;
        bool made = Ads.EnsureFolder();
        Log.Write($"ads: {Ads.Folder} {(made ? "created" : "already there")}");
    }

    /// <summary>
    /// A tray menu entry's name, for the log. The ids are internal and would mean nothing to whoever reads
    /// the file later; the names are the ones the user clicked on.
    /// </summary>
    private static string MenuName(int id) => id switch
    {
        MenuLangEn => "Language: English",
        MenuLangZh => "Language: Chinese",
        MenuAutoStart => "Start with Windows",
        MenuConfig => "Open config directory",
        MenuAbout => "About",
        MenuExit => "Exit",
        MenuCloseAnim => "Window close animation",
        MenuDynCorner => "Dynamic corner radius",
        MenuReturnOrigin => "Desktop return animation",
        MenuAdsWindow => "Ad > Window ad",
        MenuAdsBootAd => "Ad > Logon full-screen ad",
        MenuDebugEnable => "Debug mode",
        MenuDebugBootAd => "Debug mode > Play the logon ad",
        MenuBlacklist => "Blacklist",
        _ => $"unknown ({id})",
    };

    internal static void OnMenu(int id)
    {
        // What the user did, written before what it did. A session read later has to say which entry was
        // clicked, and that is true of the entries that change nothing as much as of the ones that do: a
        // click refused because the system's animations are off, or the entry for the language already in
        // force, leaves the settings exactly as they were and would otherwise leave no trace at all.
        Log.Write($"menu: {MenuName(id)}");
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
                // Refused as well as greyed, and for the same reason the system-animation toggles are: a menu
                // drawn before the close animation was switched off can still deliver this click, and the
                // return is the tail of a close animation that is no longer being played. Said out loud,
                // because the log otherwise shows an entry that was clicked and nothing that came of it.
                if (!_s.CloseAnimation)
                {
                    Log.Write("  refused: the window close animation is off, and the return is part of it");
                    break;
                }
                _s.ReturnToOrigin = !_s.ReturnToOrigin; _s.Save(); break;
            // Either kind switched on makes the folder the videos go in, as the note of where to put them, and
            // only if it is not already there - a folder that exists is the one being filled. See
            // Ads.EnsureFolder for why it is compared by name instead of asked for with Directory.Exists.
            //
            // Done when a kind is switched *on* rather than when the feature is first asked for, because there
            // is no switch for the feature itself any more: the two entries are switches of their own, and the
            // moment to make the folder is the moment one of them is switched on with nothing there to play.
            case MenuAdsWindow:
                _s.WindowAd = !_s.WindowAd;
                NoteFolder();
                _s.Save();
                break;
            case MenuAdsBootAd:
                // Refused as well as frozen, and asked of the same function that drew the menu rather than of
                // a second copy of its conditions: a menu drawn before Start with Windows was removed can
                // still deliver this click, and a logon advertisement with no logon to run at is not one.
                if (!Ads.Menu(Ads.Available, AutoStartOn, _s.WindowAd, _s.BootAd).BootAdUsable)
                {
                    Log.Write("ads: the logon-advertisement switch is frozen; refused");
                    break;
                }
                _s.BootAd = !_s.BootAd;
                NoteFolder();
                _s.Save();
                break;
            case MenuDebugBootAd:
                // The one way to run an advertisement with the switches against it, so it says so: a log that
                // showed an advertisement nothing asked for would otherwise read as a bug.
                PlayBootAd(forced: true);
                break;
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
                // A task dialog, not a message box: it is the only system dialog that draws a link that can
                // be followed and offers a button whose caption is ours. Everything AGPL-3.0 section 0 asks an
                // interactive interface to show is in the text, and the second button goes to the newest
                // release. The ini path that used to be the last line is gone - "Open config directory", a
                // few entries up, is where that is asked for.
                //
                // True means the launch-ad code was played into the box while it was open. That is the whole
                // way in: reveal the entry, then open the menu the code's last step asked for, so the entry
                // that was just revealed is on screen rather than being something to go and look for.
                if (AboutDialog.Show(_host.Hwnd))
                {
                    Log.Write("about: the launch-ad code was entered; revealing the menu entry");
                    _s.AdsVisible = true;
                    _s.Save();
                    _host.ShowMenu();
                }
                break;
            case MenuBlacklist:
                // Modal, and its own loop, so the animation keeps running behind it: the windows it is there to
                // list are the ones being animated while the user is looking at the panel.
                BlacklistPanel.Show();
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
        // So that the panel can tell the program is going away even if it is exited by something that never
        // reaches its own menu: logoff, Task Manager, a crash in the tray.
        BlacklistPanel.SetHost(_hwnd);

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

    /// <summary>
    /// The tray host's window procedure: the icon's messages, the menu they open, the watchdog and the
    /// environment changing underneath.
    /// </summary>
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

                // The environment changing under a program that is already running: a session that moves
                // between monitors, changes resolution, or switches between light and dark. Nothing here is
                // cached that could go stale - the monitor under a window, its DPI and the card's colours are
                // all asked for when they are needed - so these are recorded rather than acted on. They are
                // the moments an oddity later in the file has to be read against.
                case Native.WM_DISPLAYCHANGE:
                    Moa.Log.Write($"WM_DISPLAYCHANGE: {w.ToInt64() & 0xFF} bpp," +
                                  $" {l.ToInt64() & 0xFFFF}x{(l.ToInt64() >> 16) & 0xFFFF}");
                    break;

                case Native.WM_SETTINGCHANGE:
                {
                    string area = l == IntPtr.Zero ? "" : Marshal.PtrToStringUni(l) ?? "";
                    // Two of the dozens of reasons this message is broadcast are this program's business:
                    // the colours the card is drawn in, and the metrics a window's frame is measured with.
                    // The rest - a policy, a locale, an environment variable - are not, and logging all of
                    // them would put a burst of lines into the file every time Windows is told something.
                    if (area is "ImmersiveColorSet" or "WindowMetrics")
                        Moa.Log.Write($"WM_SETTINGCHANGE: {area}");
                    // The user switched between light and dark while this program was running. uxtheme is told
                    // again, which is what recolours the menu, and the panel - if it is open - styles itself
                    // from the same answer when it hears this message.
                    if (area == "ImmersiveColorSet") Native.DarkMode.Apply();
                    break;
                }

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

    /// <summary>Opens the tray menu. Public because the About box's launch-ad code ends by asking for it.</summary>
    public void ShowMenu()
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

        // And the return animation goes grey when the close animation is off, for the same reason and with
        // the same treatment. It is not an animation of its own: it is the last part of a closing card, so
        // with closing switched off there is nothing for it to happen in and it can neither be changed nor
        // be said to be on. Its value is left alone, so switching closing back on restores whatever it was.
        uint returnGroup = animationGroup != 0 || !Program.S.CloseAnimation
            ? Native.MF_GRAYED
            : 0u;

        Native.AppendMenuW(menu, Native.MF_STRING | animationGroup |
            (Program.S.CloseAnimation ? Native.MF_CHECKED : Native.MF_UNCHECKED),
            (IntPtr)Program.MenuCloseAnim, Strings.T("CloseAnim"));
        Native.AppendMenuW(menu, Native.MF_STRING | animationGroup |
            (Program.S.DynamicCorner ? Native.MF_CHECKED : Native.MF_UNCHECKED),
            (IntPtr)Program.MenuDynCorner, Strings.T("DynamicCorner"));
        Native.AppendMenuW(menu, Native.MF_STRING | returnGroup |
            (Program.S.ReturnToOrigin ? Native.MF_CHECKED : Native.MF_UNCHECKED),
            (IntPtr)Program.MenuReturnOrigin, Strings.T("ReturnToOrigin"));

        // Hidden until it is asked for, and the only entry in this menu that is: an entertainment feature is
        // not something to advertise in a menu. What is under it is the two kinds of advertisement, and what
        // can be clicked is worked out in one place - Ads.Menu - rather than here.
        //
        // There is no entry for the feature itself. Two switches that each say what they do do not need a
        // third saying that either of them might, and a master switch is one more thing for the ticks to
        // disagree with.
        //
        // The whole submenu is frozen while Ads holds nothing to play, because nothing under it can do
        // anything then. A frozen popup cannot be opened, which is the point: entries that could still be
        // ticked would be promising something that cannot happen. The ticks are left as the ini has them.
        if (Program.S.AdsVisible)
        {
            var ads = Ads.Menu(Ads.Available, Program.AutoStartOn, Program.S.WindowAd, Program.S.BootAd);
            uint frozen = ads.Submenu ? 0u : Native.MF_GRAYED;

            IntPtr adsMenu = Native.CreatePopupMenu();
            Native.AppendMenuW(adsMenu, Native.MF_STRING | frozen |
                (ads.WindowAd ? Native.MF_CHECKED : Native.MF_UNCHECKED),
                (IntPtr)Program.MenuAdsWindow, Strings.T("AdsWindow"));
            Native.AppendMenuW(adsMenu, Native.MF_STRING |
                (ads.BootAdUsable ? 0u : Native.MF_GRAYED) |
                (ads.BootAd ? Native.MF_CHECKED : Native.MF_UNCHECKED),
                (IntPtr)Program.MenuAdsBootAd, Strings.T("AdsBootAd"));

            Native.AppendMenuW(menu, Native.MF_POPUP | frozen, adsMenu, Strings.T("Ads"));
        }

        Native.AppendMenuW(menu, Native.MF_SEPARATOR, IntPtr.Zero, "");
        Native.AppendMenuW(menu, Native.MF_POPUP, lang, Strings.T("Language"));
        Native.AppendMenuW(menu, Native.MF_STRING |
            (Program.AutoStartOn ? Native.MF_CHECKED : Native.MF_UNCHECKED),
            (IntPtr)Program.MenuAutoStart, Strings.T("AutoStart"));

        // A submenu rather than one more toggle among the animation ones, because what belongs under it is
        // any number of separate diagnostics and none of them is a setting in the sense the others are: an
        // entry is off by default, has nothing to do with how the program looks, and turning it on is a
        // deliberate step taken to find out what the program is doing.
        //
        // The blacklist goes under the switch rather than beside it, and it reads the log, so it has anything
        // to show only once the switch has been on: what it lists is what debug mode recorded.
        var debug = Native.CreatePopupMenu();
        Native.AppendMenuW(debug, Native.MF_STRING |
            (Program.S.DebugMode ? Native.MF_CHECKED : Native.MF_UNCHECKED),
            (IntPtr)Program.MenuDebugEnable, Strings.T("DebugEnable"));
        Native.AppendMenuW(debug, Native.MF_STRING, (IntPtr)Program.MenuBlacklist, Strings.T("Blacklist"));
        // The logon advertisement on demand, so that it can be looked at without waiting for a logon and
        // without turning the logon-ad switch on. It is here rather than in the launch-ad submenu because it
        // is not a setting: it plays the advertisement once, now, and changes nothing.
        //
        // Shown only while that submenu is, which is the same switch and on purpose: this is the one entry
        // that would otherwise tell whoever opened Debug mode that the feature exists at all.
        if (Program.S.AdsVisible)
            Native.AppendMenuW(debug, Native.MF_STRING | (Ads.Available ? 0u : Native.MF_GRAYED),
                (IntPtr)Program.MenuDebugBootAd, Strings.T("DebugBootAd"));
        Native.AppendMenuW(menu, Native.MF_POPUP, debug, Strings.T("DebugMode"));

        Native.AppendMenuW(menu, Native.MF_STRING, (IntPtr)Program.MenuConfig, Strings.T("OpenConfig"));
        Native.AppendMenuW(menu, Native.MF_SEPARATOR, IntPtr.Zero, "");
        Native.AppendMenuW(menu, Native.MF_STRING, (IntPtr)Program.MenuAbout, Strings.T("About"));
        Native.AppendMenuW(menu, Native.MF_STRING, (IntPtr)Program.MenuExit, Strings.T("Exit"));

        Native.GetCursorPos(out var p);

        // The panel goes before the menu appears, and this is not tidiness. A popup menu is driven by the
        // window that owns it only while that window is the foreground one, and the panel - modal, holding the
        // focus - is not it. What happened was worse than a menu in the wrong place: the menu drew correctly
        // and its items did nothing, Exit included, because the click went to a menu whose owner was not
        // foreground and the shell discarded it. Nothing of ours may stand between the user and this menu, so
        // the panel is closed first and synchronously: a posted close would still be in the queue here.
        BlacklistPanel.Close();

        Native.SetForegroundWindow(_hwnd);
        int cmd = Native.TrackPopupMenu(menu, Native.TPM_RIGHTBUTTON | Native.TPM_RETURNCMD,
                                        p.X, p.Y, 0, _hwnd, IntPtr.Zero);
        Native.DestroyMenu(menu);
        if (cmd > 0) Program.OnMenu(cmd);
    }
}
