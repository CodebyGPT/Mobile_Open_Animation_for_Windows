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
                       MenuSplashAd = 13, MenuAdRule = 14;

    private static Settings _s = null!;
    internal static Settings S => _s;
    internal static Animator A => _anim;
    private static HideGuard _guard = null!;
    private static Watcher _watcher = null!;

    /// <summary>
    /// Whether the start-up launcher is there and is this program's, cached so the tray menu opens instantly - the
    /// check is one file read and the menu is built on every right click. Refreshed at start-up, on every
    /// right click, and after a change.
    /// </summary>
    internal static bool AutoStartOn;

    /// <summary>
    /// The argument this program gives the copy of itself it starts elevated, and the marker that keeps the two
    /// of them from doing the same work twice.
    ///
    /// A copy carrying it is past the point of asking for elevation, so it does not ask again. Without a marker
    /// here the continuation would ask for elevation again and the two would take turns forever.
    /// </summary>
    internal const string ElevatedArgument = "--elevated";

    /// <summary>
    /// Whether this copy is the continuation of one that already asked for elevation. See
    /// <see cref="ElevatedArgument" />.
    /// </summary>
    internal static bool StartedElevated;

    private static bool Says(string[] args, string what)
    {
        foreach (string arg in args)
            if (string.Equals(arg, what, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
    private static TrayHost _host = null!;
    private static Animator _anim = null!;

    [STAThread]
    private static int Main()
    {        // A WinExe has no console, and the CLR's own crash dialog says nothing useful, so
        // every failure has to be written down and shown.
        try { return Run(); }
        catch (Exception ex) { Report("startup", ex); return 1; }
    }

    /// <summary>
    /// Whether this copy can do the one thing this program is: hide another process's window. Asked of the
    /// token's integrity level, because that is what UIPI compares - see Native.IntegrityIsHighEnough for why
    /// the elevation flag is not the question. Measured rather than argued: a copy this machine's Task
    /// Scheduler started had the flag set and low integrity, and both role-check and flag said "elevated" about
    /// a copy that could not draw a tray icon or hide anything.
    /// </summary>
    internal static bool IsElevated() => Native.IntegrityIsHighEnough();

    /// <summary>
    /// Asks Windows for elevation, which is what this program means by rights: UIPI lets it touch only windows at
    /// its own integrity level or below, so unelevated it would look broken rather than limited.
    ///
    /// The copy that asked does not wait for the answer - it leaves, and the continuation does the work. Two
    /// processes exist for the moment in between, and neither invariant is at risk: the continuation is the one
    /// marked with the argument that stops it asking again and playing the advertisement again, and the mutex
    /// that makes an instance unique is taken only by a copy that is staying.
    ///
    /// False is the user saying no, or an account that cannot elevate at all; the caller leaves either way,
    /// without a word, because the prompt was the question and there is nothing left to say about the answer.
    /// </summary>
    private static bool SelfElevate()
    {
        string arguments = ElevatedArgument;
        Log.Write($"not elevated: asking for elevation and offering the work to the copy that gets it ({arguments})");

        bool started = Native.StartElevated(Compat.ExePath, arguments,
                                            Path.GetDirectoryName(Compat.ExePath) ?? "");
        if (!started)
            Log.Write($"the request for elevation was refused (error {Marshal.GetLastWin32Error()})");
        return started;
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
        // Whether the program was put in a folder with other things in it, asked *before* anything at all is
        // read or written and answered as a dialog much later - the order of the two is the whole of why it is
        // here. Reading the settings is what writes the ini when there is not one, and this question exists so
        // that a user who does not want the program in that folder can leave without it having made a file
        // there: asked after the settings, the question would arrive with the answer already written down and
        // nothing left to refuse. See FolderCheck.
        string? crowded = FolderCheck.Why(AppContext.BaseDirectory, FolderCheck.Self());

        // The theme before the question below, so that the question comes up in it rather than always in the
        // light one. It reads the registry itself and needs nothing from the settings, which is why it can be
        // this early.
        Native.DarkMode.Apply();

        if (crowded is string why)
        {
            // In the language the user's Windows is in, which is the only source there is here: the ini does not
            // exist - that is the first thing this question is about - so there is no setting to read, and the
            // answer is the same one the first run below is about to choose anyway.
            Strings.Use(Settings.WindowsIsChinese() ? Lang.ChineseSimplified : Lang.English);
            bool start = Native.Ask(Strings.T("CrowdedTitle"),
                                    Strings.T("CrowdedAsk"), Strings.T("CrowdedAdvice"),
                                    Strings.T("ContinueAnyway"), Strings.T("Exit"));
            if (!start)
            {
                // Nothing is written on the way out, not even a log: a log file in the folder the user has just
                // said the program should not be in would be the same mess this question is about.
                return 1;
            }
        }

        // Settings first, because debug mode is one of them and it decides whether the log is written at
        // all: with it off there is no file to write a first line into, and everything below this is silent
        // by design rather than by accident. Log.Start is what opens the run in the log, so it comes after
        // the switch is known, not before.
        //
        // The language is handed over as the first run's, which means "use this one if there is no ini" - the
        // one language this program picks for a user rather than asking them, and only for a user whose Windows
        // is in Chinese. Everyone else keeps the English default, which is what an ini that does not exist says
        // anyway. See WindowsIsChinese, and Lang, which has two languages and no third.
        _s = Settings.Load(Settings.WindowsIsChinese() ? Lang.ChineseSimplified : null);
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
        Log.Write($"elevated={IsElevated()} integrity={Native.Integrity()}");
        Strings.Use(_s.Language);

        // Silent on purpose. A tray program that is already running needs no dialog to say so: the tray
        // icon is next to the clock, and being told "already running" by a second copy of something you
        // have just asked for is noise rather than information. The log keeps the evidence.
        //
        // This copy's own marker, read before anything is decided: whether it is the continuation of a copy that
        // already asked for elevation. See ElevatedArgument.
        StartedElevated = Says(Environment.GetCommandLineArgs(), ElevatedArgument);
        Log.Write($"already elevated and continuing: {StartedElevated} " +
                  $"(this copy's command line is " +
                  $"{string.Join(" ", Environment.GetCommandLineArgs().Skip(1))})");

        // The first thing this program does about rights is ask for them, and the asking is the last thing this
        // copy does: everything else needs the rights the answer is about, so there is nothing to do before it
        // and no card to play first - there is no logon advertisement any more.
        //
        // This copy then finishes. It holds no single-instance mutex, installs no hooks and draws no tray icon:
        // nothing that has to be unique is done by a copy that is going to leave. That is what keeps the
        // single-instance rule intact with two processes briefly alive.
        if (!IsElevated() && !StartedElevated)
        {
            if (!SelfElevate()) return 1;
            return 0;
        }

        // Past the point where asking is possible: either this copy is elevated, or it is the continuation of one
        // that asked and the answer was no. Nothing is shown for the second - the consent prompt was the
        // question, and it has been answered - and the log says which of the two this was.
        if (!IsElevated())
        {
            Log.Write("the request for elevation was refused, so this copy cannot hide other processes' windows; " +
                      "it is leaving");
            return 1;
        }

        // Placed after the elevation decision rather than before it, which is the opposite of what this used to
        // do and for a reason that is now the whole design: the copy that asks for elevation must not be the
        // copy that claims the session. Only the copy that is going to stay takes this, so there is exactly one
        // of those no matter how many asked.
        if (!OnlyCopyInThisSession())
        {
            Log.Write("another copy is already running in this session; leaving it to do the work");
            return 0;
        }

        // What the answer was, written down where it can be read afterwards: the question itself is asked once
        // per folder and is answered by somebody who is not going to remember which folder it was about. It goes
        // here rather than beside the question because there is no log to write to until the settings above have
        // been read, and the run that was refused writes nothing at all.
        Log.Write(crowded is string said
            ? $"folder: {AppContext.BaseDirectory} is not a folder of its own - {said}; the user chose to carry on"
            : $"folder: {AppContext.BaseDirectory} is a folder of its own");
        if (Native.AskNote is string whyNot)
            Log.Write($"folder: the question was not asked the way it should have been ({whyNot})");

        _host = new TrayHost();
        _host.Create();

        _guard = new HideGuard(_s.MaxHideMs);
        // The watchdog gets a thread of its own. It used to sweep from the animation thread's tick, which is
        // the thread that talks to other processes' windows - so the thing that exists to put a window back
        // after something went wrong could itself be blocked by the kind of window that makes things go wrong.
        _guard.Start();
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

        // The launcher is looked at once here, before the menu can be opened, and what the look found is logged
        // either way - including what it was looking for, when something under our name is not ours.
        AutoStartOn = AutoStart.Look(_s.StartupHash, out string autoWhy) == AutoStart.State.Ours;
        Log.Write($"autostart: {autoWhy}");

        // And the opening advertisement, on the same terms: the setting says what the user asked for, the folder
        // says whether it can still be done, and where they disagree the setting is what gets corrected. See
        // ReconcileOpeningAd, which is also called when the menu is drawn and never on a timer.
        ReconcileOpeningAd();

        // Running means working, and exiting means not working: there is deliberately no paused
        // state, because a menu item that silently turns the program off is a support question.
        _host.Notify($"Mobile Open Animation for Windows: watching (pid {Native.GetCurrentProcessId()})");

        _host.RunMessageLoop();

        _watcher.Dispose();
        _anim.Stop();
        _guard.ReleaseAll();
        // After the release, never before: the sweeper is the last line of defence and it is stopped only
        // once there is nothing left for it to defend.
        _guard.Stop();
        _host.Destroy();
        return 0;
    }

    /// <summary>
    /// The window advertisement: called the moment an opening animation has finished, with the window and the
    /// rectangle the card stopped at. Returning a window puts it over that rectangle; returning nothing leaves
    /// the application to be revealed as it always was.
    ///
    /// Whether it may play at all is AdMayPlayAgain's answer, and that is about the program rather than the
    /// process: PowerToys' image resizer is one process per showing, so opening it twice played the advertisement
    /// twice, and its window is not PowerToys' main window either. The main window is the one that gets it, and a
    /// dialog, a sub-feature or a second copy of the same program is refused, because it is the same program.
    ///
    /// Windows that never animate - which includes every blacklisted one, since those never reach an animation
    /// at all - are not offered one, because this is only ever called from an animation.
    /// </summary>
    private static IntPtr OnHandoff(IntPtr hwnd, uint pid, RECT rect, int radius)
    {
        if (!_s.WindowAd) return IntPtr.Zero;

        string program = ProgramName(pid);
        if (!AdMayPlayAgain(program)) return IntPtr.Zero;

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
        // The hold the advertisement takes, and it is renewed by the animator's tick for as long as the
        // advertisement is up - see Animator.KeepHeldWindowHidden and HideGuard.HoldRenewMs. Taken once for the
        // advertisement's whole length, which is what this used to be, it is six minutes in which a card that
        // died without releasing leaves a transparent window behind it.
        _anim.KeepHidden = hwnd;
        _anim.HeldWindow = hwnd;
        _guard.Hold(hwnd, HideGuard.HoldRenewMs);
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

        // The window is covered and the log says what stands in for it. The rule's clock is not reset here: a
        // window's appearance is what resets it, and that has already happened in AdMayPlayAgain.
        Log.Write($"ads: window advertisement for {program} (pid {pid}) hwnd={hwnd} at " +
                  $"{rect.Left},{rect.Top} {rect.Right - rect.Left}x{rect.Bottom - rect.Top}," +
                  $" muted={muted} radius={radius}");
        return card.Hwnd;
    }

    /// <summary>
    /// Whether this program may have an advertisement for this window.
    ///
    /// A program that has not been seen before always may: the rule is about re-triggering, and a first
    /// advertisement that could be rolled away would be a feature half the users never see.
    ///
    /// Every window after that is a new starting point. Its appearance resets that program's chance to
    /// AdRuleReset, and the roll is made against the time since the previous appearance - so a program opened
    /// every few minutes is asked the same low chance each time, instead of a chance that kept climbing from one
    /// window to the next until an advertisement played. That climb is what made the programs opened most often
    /// the ones most likely to show another advertisement. See AdRule.Chance.
    /// Nothing is remembered beyond this run: the map is in memory, so every start gives every program its first
    /// advertisement again.
    /// </summary>
    private static bool AdMayPlayAgain(string program)
    {
        bool seen = _adLastSeen.TryGetValue(program, out var last);
        // Written before the roll, because the window has appeared either way and this appearance is the next
        // one's starting point - an advertisement that plays does not get a second reset out of it.
        _adLastSeen[program] = DateTime.UtcNow;
        if (!seen) return true;

        AdRule.TryParse(_s.AdRuleUnit, out var unit);
        // Wall clock rather than a tick count, which is what makes the "no chance at all" end of the rule
        // reachable: a system clock moved backwards is a negative elapsed time, and the rule refuses it.
        double elapsedMs = (DateTime.UtcNow - last).TotalMilliseconds;
        double chance = AdRule.Chance(unit, _s.AdRuleIncrease, _s.AdRuleReset, elapsedMs);
        bool yes = AdRule.SaysYes(chance, _roll.NextDouble());

        Log.Write($"ads: {program} last appeared at {last:HH:mm:ss}, " +
                  $"{elapsedMs / 1000.0:F1}s ago; the chance is now {chance:F2}%, so " +
                  (yes ? "it plays another" : "none this time"));
        return yes;
    }

    /// <summary>
    /// When each program's window last appeared, which is the moment its chance restarts from.
    ///
    /// A name rather than a process id, because it is the program the rule is about, and a name that two
    /// processes of one program agree on: ProductName is the field Windows keeps for exactly this and PowerToys'
    /// own two executables both call themselves PowerToys, which is the case this went wrong on. Programs that
    /// leave it blank - many do - fall back to the path of the executable, which is the same answer for them as
    /// long as they do not ship two of them.
    ///
    /// In memory and deliberately not in the file: the feature is meant to start again from nothing when the
    /// program restarts, so every run gives every program its first advertisement again, and a record that
    /// outlived the run would be a record of everything the user has ever opened.
    /// </summary>
    private static readonly Dictionary<string, DateTime> _adLastSeen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The one roll of the dice per advertisement decision. See AdRule.SaysYes.</summary>
    private static readonly Random _roll = new();

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
    /// Hands the card everything that comes from the settings rather than from the card itself: where it says
    /// what it is doing, what its mute control says, where to write down a mute the user asked for, and how it
    /// goes away.
    ///
    /// The card does not animate: it appears where it belongs and leaves by fading where it stands, so the only
    /// two things it is told about its own looks are the frame the fade is stepped at and the corner it wears.
    /// <paramref name="radius" /> is the corner the opening animation's card ended on, handed over by the
    /// animator, and is what the window advertisement wears while it is up - zero for the logon advertisement,
    /// whose card is its own and has nothing to line up with.
    /// </summary>
    private static void WireCard(int radius)
    {
        AdsCard.Trace = Log.Write;
        AdsCard.CaptionMute = Strings.T("AdsMute");
        AdsCard.CaptionUnmute = Strings.T("AdsUnmute");
        AdsCard.CaptionRemaining = Strings.T("AdsRemaining");
        AdsCard.CaptionPending = Strings.T("AdsPending");
        AdsCard.CaptionSkip = Strings.T("AdsSkip");
        AdsCard.CaptionSearch = Strings.T("AdsSearch");
        AdsCard.MuteRemembered = m => { _s.AdsMuted = m; _s.Save(); };
        // The advertisement's own name, clicked: the card builds the URL and this is the only thing that
        // launches anything. It goes to the shell as a URL to open, which is what hands it to whichever browser
        // the user has, with nothing in this program knowing or caring which that is - see Strings.SearchUrl for
        // the two engines and AdsCard.SearchUrlFor for why a file's name cannot be anything but a search term in
        // it. A path, a file name or a command line is never passed to the shell from here: an advertisement can
        // be a file somebody else put in the folder, and "open this file" on one of those is what runs it.
        AdsCard.SearchUrl = Strings.SearchUrl;
        AdsCard.Searched = url =>
        {
            // Nothing is said to the user about this either way: a search that cannot be opened fails, and what
            // it failed at is written to the log - which is written at all only when Debug mode is on. See
            // Log.Write. A popup over an advertisement for a click that did not work is a second thing to dismiss.
            bool opened = Native.OpenAsUser(url, out string why);
            Log.Write(opened ? $"ads: searching, {url}" + (why.Length == 0 ? "" : $" - {why}")
                             : $"ads: the search was not opened: {why}");
        };
        // The card's redraw interval is the display's frame time, which the animator works out from the monitor's
        // refresh rate. The copy that plays the logon advertisement before asking for elevation has no animator -
        // it installs no hooks and watches nothing, because the advertisement is the only thing it is there for -
        // so it uses the 60 Hz default. The video is drawn by the media engine into its own child window and does
        // not depend on this at all; it is the countdown and the fade that follow it. (Running WireCard before
        // that copy existed was a NullReferenceException at startup, which is how this line was found.)
        AdsCard.FrameMs = _anim is null ? 16 : (int)Math.Round(_anim.FrameMs);
        AdsCard.Radius = radius;
    }

    /// <summary>
    /// Switches the opening advertisement on, or says why it cannot be.
    ///
    /// Switching it off is unconditional - there is nothing to check about not playing something. Switching it
    /// on is the one setting in this program that is refused rather than obeyed, and the reason is that the
    /// feature is nothing but the files: a switch that is on with nothing to play promises an advertisement that
    /// never comes, and the user has no way to tell that from a program that is broken. So the folder is
    /// checked, then the videos in it, and then - the only part a file's own bytes cannot answer - whether the
    /// player will agree there is anything to play. See MediaPlayer.MeasureMs.
    ///
    /// The check is deliberately about *any* video in the folder rather than about the one that would be picked:
    /// the card shuffles its candidates and passes over the ones that will not open, so one file that plays is
    /// the whole of what the switch promises.
    /// </summary>
    private static void SwitchOnOpeningAd()
    {
        if (_s.WindowAd) { _s.WindowAd = false; _s.Save(); return; }

        if (!Ads.FolderExists)
        {
            Log.Write($"ads: no {Ads.Folder} folder, so the opening advertisement cannot be switched on");
            SayNothingToPlay();
            return;
        }
        if (!Ads.AnyVideo())
        {
            Log.Write($"ads: {Ads.Folder} holds no video file with anything in it, so the opening " +
                      "advertisement cannot be switched on");
            SayNothingToPlay();
            return;
        }

        // The files that pass the size check, measured with the player the card would use. Bounded on both axes
        // - files tried and time per file - because this runs inside a menu click: a folder full of files that
        // will not open must not hold the tray closed while it finds out.
        int tried = 0;
        foreach (string file in Ads.Videos())
        {
            if (new FileInfo(file).Length == 0) continue;
            if (++tried > AdProbeMaxFiles) break;
            int ms = MediaPlayer.MeasureMs(file, _host.Hwnd, AdProbeTimeoutMs);
            Log.Write($"ads: {Path.GetFileName(file)} measures {ms} ms " +
                      $"({tried} of the first {AdProbeMaxFiles} files tried)");
            if (ms > 0) { _s.WindowAd = true; _s.Save(); return; }
        }

        Log.Write($"ads: none of the first {tried} video file(s) in {Ads.Folder} would play, so the opening " +
                  "advertisement cannot be switched on");
        SayNothingToPlay();
    }

    /// <summary>
    /// How many files switching the advertisement on will open, and how long it gives each. Small because this
    /// is a menu click: the files it looks at are the ones the user just put there, so the first is the one that
    /// answers, and a folder of junk is refused after a second or two rather than after a minute.
    /// </summary>
    private const int AdProbeMaxFiles = 3, AdProbeTimeoutMs = 1200;

    /// <summary>
    /// Turns the opening advertisement off when there is nothing left for it to play, and does nothing when
    /// there is.
    ///
    /// The same shape as the start-up switch's reconcile: the setting says what the user asked for, the folder
    /// says whether it can still be done, and when the two disagree it is the setting that is corrected. Called
    /// at start-up and on every right click - the three moments the user named - and never on a timer: what
    /// changes underneath it is a folder the user edits, so there is nothing to watch for, and a program that
    /// polled a directory to find out would be doing work for a change that cannot happen on its own.
    ///
    /// What is checked is the cheap half: the folder, and a video file with something in it. Whether the player
    /// will play it is only asked when the switch is switched on, because that is the question that opens files.
    /// So a folder emptied of its *playable* videos is caught here, and a folder whose only video is corrupt is
    /// caught by the next click; the advertisement itself checks again anyway, at the handoff, before it puts a
    /// card up. See OnHandoff.
    /// </summary>
    internal static void ReconcileOpeningAd()
    {
        if (!_s.WindowAd) return;
        if (Ads.AnyVideo()) return;

        Log.Write($"ads: {Ads.Folder} no longer holds a video with anything in it, so the opening " +
                  "advertisement is being switched off");
        _s.WindowAd = false;
        _s.Save();
    }

    /// <summary>
    /// What the user is told when there is nothing to play: make the folder, put a named video in it, and what
    /// this machine will play.
    ///
    /// A question mark rather than the red X this started as, and the user asked for it: nothing has failed here
    /// - the program was asked to do something it cannot do yet, and what it needs is for the user to go and do
    /// one thing. It is the dialog's own icon, drawn by the system; nothing here draws anything.
    /// </summary>
    private static void SayNothingToPlay()
        => Native.MessageBoxW(IntPtr.Zero, Strings.T("AdsNeeded"), Strings.T("AdsNeededTitle"),
                              0x00000020 /* MB_ICONQUESTION */);

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
        MenuReturnOrigin => "Return animation",
        MenuSplashAd => "Opening ad",
        MenuAdRule => "Custom opening ad trigger conditions",
        MenuDebugEnable => "Debug mode",
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
                // Windows 11 only: the card settles into an eight-pixel corner there, and a dynamic corner is
                // the shape at the other end of that move. On Windows 10 the window is square and the entry is
                // not on the menu at all; the value is forced off in Settings.Load, so this is the refusal of a
                // click on a menu that was drawn before the check, not the only check.
                if (!Native.IsWindows11) break;
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
            case MenuSplashAd:
                // The one entry that can refuse: see SwitchOnOpeningAd. Nothing is created for it - the folder
                // and the videos in it are the feature, and a program that made the folder would be making half
                // of what the user is being asked for.
                SwitchOnOpeningAd();
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
                // Reconcile first, act second. The check mark can be stale - the file may have been written,
                // deleted or edited by something else since the menu was drawn, and Windows' Start-up settings
                // page and Task Manager's Start-up tab both edit this folder - so if the display disagrees with
                // reality, the click's job is to correct the display, and nothing is written or deleted at all.
                // Only when the display was accurate is the click read as "I want the opposite of this".
                bool shown = AutoStartOn;
                bool real = AutoStart.Look(_s.StartupHash, out _) == AutoStart.State.Ours;
                if (real != shown)
                {
                    Log.Write($"autostart: menu showed {shown}, the launcher is {real}; correcting the menu only");
                    AutoStartOn = real;
                    break;
                }

                bool wanted = !real;

                // A file of that name is already there and is not the one this program wrote - edited, somebody
                // else's, or written before the ini lost its record of it. Writing ours over it is a decision
                // about a file the program did not put there, so it is the user's: asked, and the file left
                // alone on anything but the first answer. See Native.Ask, which is the only dialog here that can
                // name its own buttons.
                if (wanted && AutoStart.Look(_s.StartupHash, out _) == AutoStart.State.Changed)
                {
                    bool overwrite = Native.Ask(Strings.T("AutoStartConflictTitle"),
                                                Strings.T("AutoStartConflict"), "",
                                                Strings.T("AutoStartOverwrite"), Strings.T("AutoStartCancel"));
                    Log.Write($"autostart: a file under our name is not ours; the user said " +
                              $"{(overwrite ? "overwrite" : "leave it alone")} ({Native.AskNote ?? "as asked"})");
                    if (!overwrite) break;
                }

                // The hash of what was written is kept, and that is what makes the next look able to tell this
                // file from one that has been edited since. Turning the launcher off records nothing, which is
                // how "no launcher of ours" is written down.
                string err;
                bool now = AutoStart.Apply(wanted, out string hash, out err);
                if (now != wanted)
                    Native.MessageBoxW(IntPtr.Zero,
                        Strings.T("AutoStartFailed") + "\n\n" + err,
                        "Mobile Open Animation for Windows", 0x00000030 /* MB_ICONWARNING */);
                else
                {
                    _s.StartupHash = hash;
                    _s.Save();
                    AutoStartOn = now;
                }
                break;
            }
            case MenuConfig:
                // Through the shell's own token like everything else that starts a program for the user: from
                // this process, which is running as administrator, an Explorer window opened the obvious way is an
                // Explorer window with administrator rights. The path is ours - the ini beside the program - and
                // not anything out of the Ads folder.
                if (!Native.AskExplorer("/select,\"" + Settings.IniPath + "\"", out string whyNot))
                    Log.Write($"open config folder failed: {whyNot}");
                break;
            case MenuAbout:
                // A task dialog, not a message box: it is the only system dialog that draws a link that can
                // be followed and offers a button whose caption is ours. Everything AGPL-3.0 section 0 asks an
                // interactive interface to show is in the text, and the second button goes to the newest
                // release. The ini path that used to be the last line is gone - "Open config directory", a
                // few entries up, is where that is asked for.
                //
                // And that is all it is. It used to watch for thirteen keys and count clicks on its own icon,
                // with a low-level keyboard hook and a low-level mouse hook to see them, and the last key of
                // the sequence revealed the advertisement entry in this menu. Nothing is hidden any more, so
                // there is nothing to reveal and no reason for a box to be listening to the whole machine's
                // input while it is open.
                AboutDialog.Show(IntPtr.Zero);
                break;
            case MenuBlacklist:
                // A window, not a dialog box and not a modal one: it opens and this returns, so the animation
                // keeps running behind it, the tray menu keeps answering over it, and asking for it twice
                // brings the one that is there to the front. What closes it is its own X, or this program
                // going away - the panel is owned by the tray host's window, so the system does the rest.
                BlacklistPanel.Show();
                break;
            case MenuAdRule:
                // The same shape, and for the same reason: two lines of settings that save themselves as they
                // are changed, so there is nothing to be modal about and no Save button to press. See
                // AdRuleDialog.
                AdRuleDialog.Show();
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

        // The program's own icon, drawn for the theme the tray is on: an accent-coloured outline and a fill of
        // white or black, so the same file looks like itself on a light taskbar and on a dark one. Drawn rather
        // than extracted because it has to be drawn *again* when either colour changes - see AppIcon, and the
        // ImmersiveColorSet case below, which is where that happens.
        //
        // The exe's own icon is the fallback rather than the source: it is the same shape in the SVG's own colours
        // (white square, black outline), which is right for a file in Explorer and wrong for a taskbar.
        _trayIcon = DrawTrayIcon();
        if (_trayIcon == IntPtr.Zero)
        {
            Native.ExtractIconExW(Compat.ExePath, 0, out _iconLarge, out _iconSmall, 1);
            _trayIcon = _iconSmall != IntPtr.Zero ? _iconSmall : IntPtr.Zero;
        }

        Moa.Log.Write($"tray: TaskbarCreated=0x{_taskbarCreated:X8} icon={_trayIcon}" +
                      $" (system light={AppIcon.SystemIsLight()}, accent={AppIcon.Stroke()})" +
                      (_taskbarCreated == 0 ? " (registration failed; the icon cannot be restored)" : ""));
        WantTray();
        // The safety watchdog. Measured against what it protects: a window hidden by us must never be
        // left hidden, and the absolute cap on that is twenty seconds - so a second of granularity is
        // far more than enough, and four wakeups a second were never needed. It also flushes the log,
        // which while an animation is running happens at frame rate anyway, so a second is only the
        // worst case for a line written while nothing is happening.
        Native.SetTimer(_hwnd, (IntPtr)1, 1000, IntPtr.Zero);
    }

    /// <summary>
    /// The tray icon, drawn at the size the tray wants and in the colours the tray is using. Zero when it could
    /// not be made, which the caller falls back from.
    ///
    /// <c>SM_CXSMICON</c> is the size a small icon is on this display, which is the tray's own size - 16 pixels at
    /// 100 per cent and 20 or 24 at the scalings that ask for them - and drawing at that size rather than handing
    /// the shell something bigger to shrink is the whole of why the icon is drawn at all: a 16-pixel curve is
    /// either drawn at 16 pixels or it is a resampled blur.
    /// </summary>
    private static IntPtr DrawTrayIcon() => AppIcon.Handle(
        Math.Max(16, Native.GetSystemMetrics(Native.SM_CXSMICON)), AppIcon.Fill(), AppIcon.Stroke());

    /// <summary>
    /// Draws the icon again and hands the new one to the tray, if there is one in it. Called when the colours the
    /// icon is made of have changed under a running program - the light/dark switch or a new accent colour - and
    /// it is the one thing in this program that is *not* asked for at the moment it is needed, because the shell
    /// holds a copy of the handle. Everything else here is asked for per use; this has to be pushed.
    /// </summary>
    private void RedrawTrayIcon()
    {
        IntPtr icon = DrawTrayIcon();
        if (icon == IntPtr.Zero) return;
        if (_trayIcon != IntPtr.Zero && _trayIcon != _iconSmall) Native.DestroyIcon(_trayIcon);
        _trayIcon = icon;
        if (!_trayAdded) return;
        var nid = NewNid();
        nid.UFlags = Native.NIF_ICON;
        nid.HIcon = icon;
        Native.Shell_NotifyIconW(Native.NIM_MODIFY, ref nid);
        Moa.Log.Write($"tray: the icon was drawn again ({icon}, system light={AppIcon.SystemIsLight()})");
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
            // The blacklist panel is a window full of stock controls, and what a dialog gets for free - Tab
            // from one control to the next, the arrows and the page keys inside a list, Enter on the button
            // that has the focus, Esc - has to be applied by whoever is pumping the queue. It used to be the
            // panel's own loop; the panel has none now, so it is this one, and for every message that is not
            // the window's the call answers false and nothing changes.
            //
            // The advertisement's rule window is the same kind of thing and joins the same queue, for the same
            // reason: it has a drop-down, two fields and a button, and Tab between them is not something a
            // window without a dialog procedure gets on its own.
            if (!BlacklistPanel.IsDialogMessage(ref msg) && !AdRuleDialog.IsDialogMessage(ref msg))
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessageW(ref msg);
            }
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
        // Only the icons this program made: the one the tray is holding is either drawn here or taken out of the
        // exe, and both of those are ours. See Create, which never hands the tray a shared system icon.
        if (_trayIcon != IntPtr.Zero && _trayIcon != _iconSmall) Native.DestroyIcon(_trayIcon);
        _trayIcon = IntPtr.Zero;
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
                    // And the icon is drawn again. Two of the things it is made of arrive as this message: the
                    // light/dark setting and the accent colour come as ImmersiveColorSet, and a change of display
                    // scaling comes as WindowMetrics. The scaling matters because the icon is drawn at the size
                    // the tray asks for (SM_CXSMICON), which is a fact about the primary display - so without this
                    // the icon would keep the pixel size it was drawn at and the shell would scale it, which is a
                    // soft icon rather than a wrong one. See DrawTrayIcon.
                    if (area is "ImmersiveColorSet" or "WindowMetrics") RedrawTrayIcon();
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
                    // The panel is not owned by this window any more - being owned is what dragged it to the
                    // bottom of the Z-order with an owner nobody ever sees - so its lifetime is said here
                    // instead: this is the program going away, and the panel goes with it while the message loop
                    // is still running rather than being taken down by the kernel with everything else.
                    BlacklistPanel.DestroyWithHost();
                    AdRuleDialog.DestroyWithHost();
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
        // Re-checked here and not only at start-up: the file can be deleted or edited by anything else on the
        // machine - the user emptying their Startup folder, Windows' Start-up settings page, Task Manager's
        // Start-up tab, a cleanup tool, an editor - and a check mark that disagrees with reality is worse than
        // none at all. Costs one file read and one hash before the menu appears.
        //
        // Look, NOT a bare existence check, and the same call the click handler makes: when the menu used
        // existence alone and the handler used the stricter test, the two disagreed, so every click concluded the
        // display was stale and corrected the menu instead of ever reaching the write or the delete. The hash is
        // what makes "the file is there" and "the file is ours" the same question here as there.
        Program.AutoStartOn = AutoStart.Look(Program.S.StartupHash, out _) == AutoStart.State.Ours;

        // And the same for the opening advertisement, which is on the same terms: a folder that has been
        // emptied stops the switch from claiming an advertisement it can no longer play. Event-driven, like
        // everything else here - see ReconcileOpeningAd.
        Program.ReconcileOpeningAd();

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
        // The return animation comes before the corner radius, and that is not the order they were written in:
        // the entry was appended below the corner one when the feature was added, and asked for above it at the
        // time. The two are a pair - what a card looks like while it is up, and where it goes when it leaves -
        // so the one that changes the card's shape at its end is the one that belongs next to the switch that
        // decides whether it leaves at all.
        Native.AppendMenuW(menu, Native.MF_STRING | returnGroup |
            (Program.S.ReturnToOrigin ? Native.MF_CHECKED : Native.MF_UNCHECKED),
            (IntPtr)Program.MenuReturnOrigin, Strings.T("ReturnToOrigin"));
        // Windows 11 only, and absent rather than greyed on Windows 10: there it is not a setting that is off,
        // it is a shape whose windows are square - and an entry that could only ever be a square card turning
        // into another square card has nothing to offer.
        if (Native.IsWindows11)
            Native.AppendMenuW(menu, Native.MF_STRING | animationGroup |
                (Program.S.DynamicCorner ? Native.MF_CHECKED : Native.MF_UNCHECKED),
                (IntPtr)Program.MenuDynCorner, Strings.T("DynamicCorner"));

        // And the opening advertisement, next to the animation switches rather than under a submenu of its own.
        //
        // It used to be hidden behind a thirteen-key code played into the About box, on the argument that a
        // menu is not a place to advertise an entertainment feature. What that cost was a feature nobody could
        // find and a box that had to listen to every key press and every click in the system to reveal it; what
        // it is now is one toggle that says what it does, which is what the other three are and what the user
        // can act on. It was hidden and refused; it is visible and refuses only when there is nothing to play -
        // see SwitchOnOpeningAd, which is the whole of what clicking it does.
        Native.AppendMenuW(menu, Native.MF_STRING |
            (Program.S.WindowAd ? Native.MF_CHECKED : Native.MF_UNCHECKED),
            (IntPtr)Program.MenuSplashAd, Strings.T("AdsWindow"));

        Native.AppendMenuW(menu, Native.MF_SEPARATOR, IntPtr.Zero, "");
        Native.AppendMenuW(menu, Native.MF_POPUP, lang, Strings.T("Language"));
        Native.AppendMenuW(menu, Native.MF_STRING |
            (Program.AutoStartOn ? Native.MF_CHECKED : Native.MF_UNCHECKED),
            (IntPtr)Program.MenuAutoStart, Strings.T("AutoStart"));

        // Under the start-up switch, and only while the opening advertisement is on: this entry is the rule for
        // when a program that has already shown an advertisement may show another, and with the advertisement
        // switched off there is nothing for it to be a rule about. Rebuilt with the menu on every right click,
        // so it comes and goes with the switch rather than needing to be told about it.
        if (Program.S.WindowAd)
            Native.AppendMenuW(menu, Native.MF_STRING, (IntPtr)Program.MenuAdRule, Strings.T("AdRule"));

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
        Native.AppendMenuW(menu, Native.MF_POPUP, debug, Strings.T("DebugMode"));

        Native.AppendMenuW(menu, Native.MF_STRING, (IntPtr)Program.MenuConfig, Strings.T("OpenConfig"));
        Native.AppendMenuW(menu, Native.MF_SEPARATOR, IntPtr.Zero, "");
        Native.AppendMenuW(menu, Native.MF_STRING, (IntPtr)Program.MenuAbout, Strings.T("About"));
        Native.AppendMenuW(menu, Native.MF_STRING, (IntPtr)Program.MenuExit, Strings.T("Exit"));

        Native.GetCursorPos(out var p);

        // A popup menu is driven by the window that owns it only while that window is the foreground one, so
        // the host is made foreground before the menu is asked for. That is the whole of it: the blacklist
        // panel used to be closed here, because it was modal and held the focus, and a menu whose owner was
        // not foreground drew correctly and then did nothing - Exit included. The panel is not modal any more
        // and does not hold anything; it stays where it is and the menu is used over it. If a test ever shows
        // the menu's items not answering while the panel is open, the answer is to hide the panel for the
        // length of the menu rather than to close it - its lifetime is the user's now, not this method's.
        Native.SetForegroundWindow(_hwnd);
        int cmd = Native.TrackPopupMenu(menu, Native.TPM_RIGHTBUTTON | Native.TPM_RETURNCMD,
                                        p.X, p.Y, 0, _hwnd, IntPtr.Zero);
        Native.DestroyMenu(menu);
        if (cmd > 0) Program.OnMenu(cmd);
    }
}
