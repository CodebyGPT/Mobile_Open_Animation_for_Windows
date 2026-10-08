// Finding the window. No injection and no hooking of the target's APIs: the system tells us a
// window was created, and we work from that.
//
// The event arrives immediately - measured at 0 ms after Windows raises it - but being told first
// is not the same as acting first. What decides whether a window can be hidden before the user
// sees it is how long the *target's* thread takes to service the request, which ranges from half
// a millisecond to tens of milliseconds and cannot be hurried from outside that process.
// Animator.OnWindowShown gives up on the windows we lose; this file only decides which windows are
// worth trying at all. See spike/RESULTS.md for the original measurements.

using System.Text;

namespace Moa;

internal sealed record WindowInfo(
    IntPtr Hwnd, uint Pid, string ProcessName, string ExePath, string ClassName,
    int X, int Y, int Width, int Height);

internal sealed class Watcher : IDisposable
{
    private readonly Settings _s;
    private readonly Action<WindowInfo> _onCandidate;
    private readonly Action<IntPtr, uint> _onShown;
    private readonly Action<WindowInfo> _onClosed;
    private readonly Action<string> _log;
    private int _destroyEvents, _destroyCandidates;
    private bool _motionOffLogged;

    /// <summary>
    /// Windows that passed the filters and were handed over to be animated, by handle, with the
    /// process they belonged to.
    ///
    /// Its purpose is the opposite of a cache. Having animated a window once is proof that it is an
    /// application window, so its close is allowed through on that evidence rather than by putting it
    /// through the filters a second time - which makes the two ends symmetric by construction instead
    /// of by assuming a window still looks the way it did when it opened. That assumption is a
    /// precaution: no window has been observed changing anything those filters test for.
    ///
    /// Every entry is removed when its window is destroyed, which is what keeps this bounded: it can
    /// only ever hold windows that are still open. The one gap is a destroy event that never arrives,
    /// such as a process killed outright, and that is what the prune is for. Nothing walks this on a
    /// timer, so it costs nothing while the program is idle.
    /// </summary>
    private readonly Dictionary<IntPtr, uint> _accepted = new();

    /// <summary>
    /// Drops the record that permits a closing animation, for a window whose animation was abandoned
    /// before it was ever on screen.
    ///
    /// Being accepted as a candidate is not the same as having been animated, and this program treated
    /// the two as one. The shell's file-type inquiry creates a hidden full-screen helper window, class
    /// ANIMATION_TIMER_HWND, which passes the filters because a fullscreen window is exempt from
    /// needing a caption. It is never shown: the opening animation waits for it, times out and draws
    /// nothing, which is correct. But the record had already been written, so dismissing the dialog
    /// played a full-screen closing animation for a window that never had an opening one.
    /// </summary>
    public void Forget(IntPtr hwnd) => _accepted.Remove(hwnd);
    private Native.WinEventDelegate? _cb;
    private IntPtr _hook = IntPtr.Zero;
    private readonly uint _selfPid = Native.GetCurrentProcessId();
    private readonly Dictionary<uint, string> _procNames = new();

    public Watcher(Settings s, Action<WindowInfo> onCandidate, Action<IntPtr, uint> onShown,
                   Action<WindowInfo> onClosed, Action<string> log)
    {
        _s = s; _onCandidate = onCandidate; _onShown = onShown;
        _onClosed = onClosed; _log = log;
    }

    /// <summary>
    /// A window is being destroyed, and it is worth animating if it would have been worth animating
    /// when it opened.
    ///
    /// Inspect does that deciding, which is what makes the two ends symmetric: exactly the windows
    /// the create path accepts are the ones the destroy path accepts, and the flood of helper
    /// windows - OLE and CIC message windows, our own panels - is rejected by the same rules rather
    /// than by a second set written to match.
    ///
    /// Inspect also answers the question the whole feature depends on: whether the window is still
    /// there. Out-of-context events arrive through the message queue, so this runs at some point
    /// after DestroyWindow, and a window whose process has already exited cannot be read at all -
    /// it comes back with no class, no rectangle and pid 0. Those are counted rather than logged one
    /// by one, because the ratio is the number that matters and it is worth having on a real machine
    /// with real applications.
    /// </summary>
    private void OnDestroyed(IntPtr hwnd, uint time)
    {
        _destroyEvents++;
        // How old the event already was when it reached us. For CREATE this was measured at 0 ms, but
        // a destroy is different in kind: the window is already gone when the event is raised, so
        // this age is the first part of the blank the eye sees between a window disappearing and its
        // panel appearing - the part no amount of work in this program can remove.
        long age = unchecked((uint)Environment.TickCount - time);
        var info = Inspect(hwnd);
        bool accepted = _accepted.TryGetValue(hwnd, out uint acceptedPid);
        if (accepted) _accepted.Remove(hwnd);
        PruneAccepted();

        // A window that never had an opening animation gets no closing one, however eligible it looks
        // now. Inspect alone was not enough, and this is the reported bug: the shell's file-type inquiry
        // creates a hidden full-screen helper window, class ANIMATION_TIMER_HWND, which passes the
        // filters - a fullscreen window needs no caption - and is then never shown, so it had no opening
        // animation; dismissing the dialog destroyed it, passed the same filters again, and gave it a
        // full-screen closing animation. The record is written only once a window has actually been
        // shown, so requiring it here is the principle stated as code rather than intended.
        if (!accepted)
        {
            // Guarded here rather than left to Log, because this is the one line that arrives with every
            // destroy event there is - twenty thousand of them in a single session's log - and building
            // the string in order to throw it away would be the most expensive thing this program does per
            // event.
            if (Log.On) Log.Write($"destroy: hwnd={hwnd} was never shown, so it gets no closing animation");
            return;
        }

        if (info is null) info = DescribeUnfiltered(hwnd, acceptedPid);
        if (info is null)
        {
            // The size threshold is what keeps this quiet: explorer churns through desktop WorkerW
            // windows roughly 200 by 56 pixels, and without it they turned this into hundreds of lines
            // that buried the one case worth seeing.
            Native.GetWindowRect(hwnd, out var rr);
            int rw = rr.Right - rr.Left, rh = rr.Bottom - rr.Top;
            Native.GetWindowThreadProcessId(hwnd, out uint refusedPid);
            var clsName = new StringBuilder(128);
            Native.GetClassNameW(hwnd, clsName, 128);
            // Only windows that could have been candidates at all: top level, unowned, not this
            // program's own. Without those conditions this reported Explorer's internal child windows
            // - SHELLDLL_DefView, NamespaceTreeControl, ShellTabWindowClass and half a dozen more -
            // every time a folder window closed, and this program's own panels once per animation.
            // Both made the report useless for the one thing it exists to show: an application window
            // that was refused, and why.
            if (Native.IsWindow(hwnd) && rw >= 120 && rh >= 80
                && refusedPid != _selfPid
                && Native.GetParent(hwnd) == IntPtr.Zero
                && Native.GetWindow(hwnd, Native.GW_OWNER) == IntPtr.Zero)
            {
                Log.Write($"destroy refused: age={age} ms cls={clsName} {rw}x{rh}" +
                          $" ex=0x{Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE):X8}");
            }
            if (_destroyEvents % 20 == 0)
                Log.Write($"destroy: {_destroyEvents} events, {_destroyCandidates} were windows worth animating");
            return;
        }
        _destroyCandidates++;
        Log.Write($"closing age={age} ms (the window was already gone for that long before we were told)");
        _onClosed(info);
    }

    public void Start()
    {
        _cb = OnEvent;
        // CREATE through HIDE. SHOW is how we learn that an application beat us to it; DESTROY and
        // HIDE are the two shapes of close. Widening the range costs nothing, because the callback
        // returns immediately for anything it does not handle.
        _hook = Native.SetWinEventHook(Native.EVENT_OBJECT_CREATE, Native.EVENT_OBJECT_HIDE,
                                       IntPtr.Zero, _cb, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        if (_hook == IntPtr.Zero) _log("SetWinEventHook failed; no window will animate");
    }

    private void OnEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject,
                         int idChild, uint thread, uint time)
    {
        // Windows' own animation-effects switch (Settings > Accessibility > Visual effects >
        // Animation effects), read here because this is the one place every event arrives, and the
        // question is not about any particular window: it is the user saying they do not want motion
        // anywhere, and this program is the largest single piece of motion on the screen.
        //
        // Returning stops the opening animation and the closing one, and the destroy and hide
        // bookkeeping with them. That is harmless rather than merely cheap: nothing is ever accepted
        // while it is off, so there is nothing for that bookkeeping to keep.
        //
        // Read on demand, not cached and invalidated from WM_SETTINGCHANGE. A cache is one more thing
        // that can be wrong - a missed broadcast would leave the switch apparently ignored until the
        // program was restarted - and this costs about 4 microseconds per event.
        if (!Native.SystemAnimationsEnabled())
        {
            // Said once per run rather than once per window: silence would otherwise be
            // indistinguishable from a program that had stopped working, and a line per window would
            // bury everything else in the log.
            if (!_motionOffLogged)
            {
                _motionOffLogged = true;
                Log.Write("system animation effects are switched off " +
                          "(Settings > Accessibility > Visual effects); nothing will animate");
            }
            return;
        }

        // A window that is hidden rather than destroyed. An application that closes to the tray hides
        // its window and keeps running, so for that shape of close this is the only notice there is.
        if (evt == Native.EVENT_OBJECT_HIDE)
        {
            if (idObject == Native.OBJID_WINDOW && hwnd != IntPtr.Zero)
                try { OnHidden(hwnd); } catch { /* never throw on the message loop */ }
            return;
        }
        if (evt == Native.EVENT_OBJECT_SHOW)
        {
            if (idObject == Native.OBJID_WINDOW && hwnd != IntPtr.Zero)
                try { _onShown(hwnd, time); } catch { /* never throw on the message loop */ }
            return;
        }
        // DESTROY sits between CREATE and SHOW in the range the hook already covers, so these were
        // being delivered and discarded. They are what the close animation works from.
        if (evt == Native.EVENT_OBJECT_DESTROY)
        {
            if (idObject == Native.OBJID_WINDOW && hwnd != IntPtr.Zero)
                try { OnDestroyed(hwnd, time); } catch { /* never throw on the message loop */ }
            return;
        }
        if (evt != Native.EVENT_OBJECT_CREATE || idObject != Native.OBJID_WINDOW) return;
        if (hwnd == IntPtr.Zero) return;

        try
        {
            // How stale is this event by the time we are called?
            //
            // `time` is the moment Windows raised it, on the GetTickCount base, so the
            // comparison must use Environment.TickCount - NOT Compat.TickCount64, which is
            // built on Stopwatch/QueryPerformanceCounter. Those two clocks share their zero
            // (boot) but not their value: measured on this machine they differ by a constant
            // 238 ms, which is larger than everything we are trying to measure.
            // (The animation's own deadlines use Compat.TickCount64 consistently at both
            // ends, so they are unaffected; only a comparison across the two bases is wrong.)
            uint eventAgeMs = (uint)((long)(uint)Environment.TickCount - time);
            long tInspect = System.Diagnostics.Stopwatch.GetTimestamp();
            var info = Inspect(hwnd);
            if (info is not null)
            {
                // The other two worth guarding before they format anything: every candidate window pays
                // for both, and they are measurements rather than events.
                if (Log.On)
                    Log.Write($"event was already {eventAgeMs} ms old; inspect took {Compat.ElapsedMs(tInspect):F2} ms");
                _onCandidate(info);
                if (Log.On)
                    Log.Write($"create event -> animation started: {eventAgeMs} ms late + {Compat.ElapsedMs(tInspect):F2} ms of our work");
            }
        }
        catch { /* a watcher must never throw on the message loop thread */ }
    }

    private WindowInfo? Inspect(IntPtr hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == _selfPid) return null;

        // Top-level only. Child windows are not the app opening.
        if (Native.GetParent(hwnd) != IntPtr.Zero) return null;

        // An owner means a dialog, a property sheet or a tool palette belonging to a real window,
        // rather than an application opening. The MMC snap-ins that Task Scheduler lives in create a
        // dozen of these every time a pane changes, and each one used to get its own animation. A
        // window with no owner is treated as a main window and animates exactly as before.
        if (Native.GetWindow(hwnd, Native.GW_OWNER) != IntPtr.Zero) return null;

        var cls = new StringBuilder(256);
        Native.GetClassNameW(hwnd, cls, 256);
        string className = cls.ToString();
        if (className == Native.SelfClassName) return null;
        if (_s.ExcludedClasses.Any(c => string.Equals(c, className, StringComparison.OrdinalIgnoreCase)))
            return null;

        Native.GetWindowRect(hwnd, out var rc);
        int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
        if (w < 120 || h < 80) return null;          // splash-ish or helper windows

        int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
        if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return null;   // tray flyouts, tooltips, OSD

        // The mod's rule (mobile-open-animation.wh.cpp:977) and the reason a program like the
        // NVIDIA control panel used to get two panels: without WS_CAPTION we accept the
        // invisible helper window it creates alongside its real one, and animate empty space.
        // Fullscreen windows legitimately have no caption, so they are exempt.
        int style = Native.GetWindowLong(hwnd, Native.GWL_STYLE);
        bool fullscreen = w >= Native.GetSystemMetrics(0) && h >= Native.GetSystemMetrics(1);
        if ((style & Native.WS_CAPTION) == 0 && !fullscreen) return null;

        // Standard dialogs ("How do you want to open this file?", Open, Save As, Run, and
        // every other #32770) are not the app opening: the mod skips them by default
        // (animateDialogs = false) and so do we. They are also built at a default position
        // and then re-centred, which is why a panel over one looked completely wrong.
        if (className == "#32770") return null;

        // Fetched fresh every time, deliberately - see ProcessImagePath. A pid is not a stable name
        // for a process, and a cache keyed by one handed the icon of an application that had already
        // closed to whatever process next inherited its number.
        string path = ProcessImagePath(pid);
        string name = Path.GetFileNameWithoutExtension(path);
        if (name.Length == 0) return null;
        if (_s.ExcludedProcesses.Any(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase)))
            return null;

        // explorer.exe owns a lot of top-level windows that are shell plumbing rather than
        // "an app opening": the desktop, drag images, and the window behind the "How do you
        // want to open this file?" flow. The only explorer window that is an app opening is a
        // folder window. (Diagnosed from a report that double-clicking a file with an unknown
        // extension produced an animation carrying Explorer's icon - the icon is extracted
        // from the owning process, so that window belonged to explorer.exe, not to
        // OpenWith.exe, which is why filtering on the dialog class #32770 did not catch it.)
        if (string.Equals(name, "explorer", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(className, "CabinetWClass", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new WindowInfo(hwnd, pid, name, path, className, rc.Left, rc.Top, w, h);
    }

    /// <summary>
    /// A window was hidden. Acted on only for windows this program animated when they opened, which
    /// keeps the log quiet and asks exactly the question at hand: an application that closes to the
    /// tray hides a window it showed us a moment ago, and nothing about that reaches the destroy path.
    /// </summary>
    private void OnHidden(IntPtr hwnd)
    {
        // Only windows this program animated when they opened: hiding is common and uninteresting for
        // everything else.
        if (!_accepted.TryGetValue(hwnd, out uint pid)) return;

        // Checked here as well as when a window opens, because otherwise a window that disappears
        // without any event is only reported the next time some other window opens - and if nothing
        // else opens, never.
        PruneAccepted();

        // Minimizing is not a close, and hiding is what both look like from out here. IsIconic alone
        // misses the case that matters - a window that is minimized and then hidden is no longer iconic
        // by the time the hide arrives - so the placement is consulted too, since it remembers the
        // command that hid the window.
        //
        // That catches only applications which minimize before hiding. One that hides without ever
        // minimizing was measured reporting a normal placement, and so gets a close animation for what
        // was a minimize. Nothing observable separates the two, and that was accepted.
        var placement = new Native.WINDOWPLACEMENT();
        placement.length = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.WINDOWPLACEMENT));
        bool placed = Native.GetWindowPlacement(hwnd, ref placement);
        int showCmd = placed ? placement.showCmd : -1;

        if (Native.WindowIsMinimized(hwnd) || showCmd == Native.SW_SHOWMINIMIZED)
        {
            Log.Write($"hidden: hwnd={hwnd} was minimized (showCmd={showCmd}), so this is not a" +
                      $" close; nothing animated");
            return;
        }

        var info = Inspect(hwnd) ?? DescribeUnfiltered(hwnd, pid);
        if (info is null)
        {
            Log.Write($"hidden: hwnd={hwnd} could not be described; nothing animated");
            return;
        }

        // Not consumed here: the window may still be destroyed afterwards, and the entry is also what
        // the destroy path uses to recognise this window as one of ours.
        Log.Write($"hidden and not minimized: hwnd={hwnd} showCmd={showCmd}; treating it as a close");
        _onClosed(info);
    }

    /// <summary>
    /// Drops entries whose window no longer exists, and says so.
    ///
    /// An entry can only be left behind by an event that never arrived, so what is found here is the
    /// signature of a window destroyed as its process exits: the system tears those down without going
    /// through DestroyWindow, and raises no destroy event for them. Saying it out loud matters, because
    /// "no event arrived" and "an event arrived and was passed over" are otherwise the same silence.
    ///
    /// Called when a window opens, is destroyed or is hidden, never on a timer - so a window that
    /// vanishes with no event is only reported the next time some other window does one of those.
    /// </summary>
    private void PruneAccepted()
    {
        var gone = new List<IntPtr>();
        foreach (var entry in _accepted)
            if (!Native.IsWindow(entry.Key)) gone.Add(entry.Key);
        foreach (var k in gone)
        {
            _accepted.Remove(k);
            Log.Write($"hwnd={k} is gone but no destroy or hide event ever arrived for it");
        }
        if (_accepted.Count > 256) _accepted.Clear();   // backstop, never reached in practice
    }

    public void Remember(IntPtr hwnd, uint pid)
    {
        PruneAccepted();
        _accepted[hwnd] = pid;
    }

    /// <summary>
    /// The description of a window that was animated when it opened, taken without the filters that
    /// decide whether a window is interesting: that question was already answered for this one, so
    /// asking it again of a window that may no longer look the same is a question with no useful answer.
    ///
    /// The process is still checked. Window handles are reused, and a handle that has been handed to a
    /// different process is a different window.
    /// </summary>
    private WindowInfo? DescribeUnfiltered(IntPtr hwnd, uint expectedPid)
    {
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid != expectedPid) return null;

        Native.GetWindowRect(hwnd, out var rc);
        int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
        if (w < 8 || h < 8) return null;           // nothing left to animate by

        // Fresh, for the same reason as in Inspect: a pid is not a stable name for a process.
        string path = ProcessImagePath(pid);
        string name = Path.GetFileNameWithoutExtension(path);
        if (name.Length == 0) return null;

        var cls = new StringBuilder(256);
        Native.GetClassNameW(hwnd, cls, 256);
        Log.Write($"hwnd={hwnd} refused by the filters but animated when it opened; close allowed");
        return new WindowInfo(hwnd, pid, name, path, cls.ToString(), rc.Left, rc.Top, w, h);
    }

    /// <summary>
    /// The full image path of a process, asked freshly every time.
    ///
    /// QueryFullProcessImageNameW, NOT Process.GetProcessById(pid).MainModule: MainModule opens the
    /// target process and walks its module list, which costs milliseconds on a process that is still
    /// initialising - and it runs before the window is hidden, in the one place where every
    /// millisecond comes out of the application's create-to-show gap.
    ///
    /// There used to be a cache here, keyed by pid, on the reasoning that a fresh launch never hit it
    /// anyway. It is gone because Windows reuses pids: once a process had exited, the next one given
    /// its number inherited the dead process's path, and the panel could draw the icon of an
    /// application that had already closed.
    /// </summary>
    private static string ProcessImagePath(uint pid)
    {
        IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return "";
        try
        {
            var sb = new StringBuilder(1024);
            int len = sb.Capacity;
            if (!Native.QueryFullProcessImageNameW(h, 0, sb, ref len))
            {
                Log.Write($"process image path: QueryFullProcessImageNameW failed for pid {pid}" +
                          $" err={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
                return "";
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            // "" is also the not-found answer, so a failure has to be said out loud - otherwise a
            // process whose name could not be read at all looks exactly like one that has no name.
            Log.Write($"process image path for pid {pid} threw {ex.GetType().Name}: {ex.Message}");
            return "";
        }
        finally { Native.CloseHandle(h); }
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) { Native.UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
    }
}
