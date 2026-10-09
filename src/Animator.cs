// The animation itself: a topmost per-pixel-alpha panel that grows from the point you
// clicked to the window's rect, carrying the app icon, and hands over once the app has
// painted. Everything here happens in our own process, on our own window.
//
// Two things are ported from the Windhawk mod rather than invented here, because inventing
// them is what made windows mismatch the animation:
//   * the target rect is re-read EVERY FRAME (the mod's SnapshotTarget, used per frame at
//     mobile-open-animation.wh.cpp:2799). Apps create their window and then move it -
//     centre it, restore saved geometry, apply DPI - so a single snapshot is stale by the
//     time the window is visible.
//   * the corner radius is interpolated along the same easing curve as the rect
//     (LerpInt(startRadius, radiusPx, ease), mobile-open-animation.wh.cpp:2800), so the
//     card starts round and settles into the window's own corner radius.

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

using System.Text;   // StringBuilder, for the window descriptions in the log

namespace Moa;

internal sealed class Animator
{
    private sealed class Anim
    {
        public required WindowInfo Target;
        public required Panel Panel;
        public long Started, Duration;
        public int AnchorX, AnchorY, StartSize;
        /// <summary>
        /// The card is growing out of a click rather than in place, so there is somewhere for a closing
        /// card to return to.
        /// </summary>
        public bool FromClick;

        /// <summary>
        /// That click was on the desktop or on the taskbar - the two launch points that are still there
        /// when the window closes. Only these windows get an Origin recorded, and which of the two it was
        /// is what the closing animation checks the remembered point against, once the window itself is
        /// discounted. See ClickTracker.Surface.
        /// </summary>
        public ClickTracker.Surface Surface;
        public int StartRadius, TargetRadius;
        public int Phase;              // 0 growing, 1 waiting for paint, 2 fading
        public long PhaseStarted;
        public long ReadyDeadline;
        public bool BecameVisible;
        public bool ReassertLogged;
        /// <summary>The application showed its window before we finished hiding it.</summary>
        public bool Cancelled;

        // ---- close animation ------------------------------------------------------------------
        /// <summary>
        /// The window is closing rather than opening. Nothing was hidden and nothing has to be
        /// released, so Guard is not involved at all: the panel shrinks from the rectangle the window
        /// had down to a small square at its centre.
        /// </summary>
        public bool Closing;
        public int StartRectX, StartRectY, StartRectW, StartRectH;
        public int EndX, EndY, EndSize;

        /// <summary>Corner radius the closing card rounds out to; see OnWindowClosed.</summary>
        public int EndRadius;

        /// <summary>When the hide was last re-asserted; see the note where it is used.</summary>
        public long LastReassert;
    }

    /// <summary>
    /// Where a window was opened from, kept so that closing it can send the card back there.
    ///
    /// Written at the moment the opening animation proves the window really appeared - the same moment
    /// Watcher.Remember is told a window has earned a closing animation at all - so a window can never be
    /// seen to return somewhere it never came from. Only windows opened from the desktop or the taskbar
    /// are recorded at all; see RememberOrigin for why the mechanism is deliberately not global.
    ///
    /// The start square is stored exactly as the opening animation used it (its top-left corner and its
    /// side), not as the settings and the DPI would compute it now: landing where the opening started is
    /// a fact about that moment, and a resize or a settings change in between must not move it. For the
    /// same reason the DPI the window had is stored with it and compared before the record is used.
    ///
    /// The record is discarded when the display no longer has the shape it was written under, because a
    /// screen position from the old shape points at nothing in the new one - after a resolution change the
    /// desktop lays its icons out again, so the remembered point is not merely stale, it is inside
    /// whichever icon moved over it. What the five numbers cannot see is a scaling change on its own,
    /// because they are physical pixels; the DPI comparison is what covers that, and a window that is
    /// already gone when its close arrives reports no DPI, which is read as "cannot say" rather than as a
    /// change - being strict there would throw the record away for every window that exits with its
    /// window open, which is exactly the case this is for.
    ///
    /// The window's process is not the whole story about the handle: the same process can lose a window
    /// without any event and be given the same handle for its next one, and that pair the pid check cannot
    /// tell apart. So a handle that is created again clears its entry - see OnWindowCreated - and the two
    /// checks together leave no case where a record outlives the window it describes.
    /// </summary>
    private sealed class Origin
    {
        public required uint Pid;
        public int X, Y, Size;
        public int DpiAtOpen;                              // 0 when the system would not say
        public int Monitors, VX, VY, VW, VH;               // the display's shape when it was written
        public ClickTracker.Surface Surface;               // which of the two it was opened from
    }

    private readonly Settings _s;
    private readonly HideGuard _guard;
    private readonly List<Anim> _active = new();

    /// <summary>
    /// Called with a window whose animation was abandoned before it was ever on screen, so that the
    /// record which would allow it a closing animation can be dropped. See Watcher.Forget.
    /// </summary>
    public Action<IntPtr>? OnAbandoned;

    /// <summary>
    /// Called with a window the first time it is actually on screen, which is when it has really had an
    /// opening animation and may therefore be allowed a closing one.
    /// </summary>
    public Action<IntPtr, uint>? OnBecameVisible;

    /// <summary>Owns every panel window and draws the frames. See AnimationThread.cs.</summary>
    private readonly AnimationThread _thread = new();

    /// <summary>
    /// Environment.TickCount at the moment each window was hidden, keyed by hwnd.
    ///
    /// GetTickCount deliberately, NOT Compat.TickCount64: the window events carry their own
    /// timestamp on the GetTickCount base, and the two clocks differ by a constant 238 ms on
    /// the machine this was developed on - larger than anything measured on this path.
    /// </summary>
    private readonly Dictionary<IntPtr, uint> _hideCompletedTick = new();

    /// <summary>
    /// Show events that arrived before their window's hide had finished, keyed by hwnd.
    ///
    /// The judgement about a late animation needs two moments: when Windows raised the show event, and when
    /// the hide completed. The show event can be delivered first - and the later our hide is, the likelier
    /// that order is, which is exactly the case the judgement exists for. Without this table the lookup in
    /// OnWindowShown found nothing and the function returned without a word: no measurement, no cancel, and
    /// no line in the log, so the animation played over a window the user had already seen and nothing
    /// anywhere said why. Kept until the hide finishes, which is where it is now judged.
    /// </summary>
    private readonly Dictionary<IntPtr, uint> _shownBeforeHide = new();

    /// <summary>
    /// How old a remembered show event may be and still be used. The two moments it compares are milliseconds
    /// apart in every real case; anything much older belongs to a window that has gone and whose handle has
    /// been reused, and judging with it would refuse an animation that was never late.
    /// </summary>
    private const int ShowBeforeHideMaxAgeMs = 2000;

    /// <summary>
    /// Where each open window came from, keyed by hwnd, for the closing animation. See Origin.
    /// </summary>
    private readonly Dictionary<IntPtr, Origin> _origins = new();

    /// <summary>
    /// How many origins may be remembered at once.
    ///
    /// Bounded for the same reason the watcher's accepted-windows table is, and by the same means: a
    /// window whose process is torn down raises no event, so an entry that will never be claimed can
    /// only be found by asking whether its window still exists. A few hundred bytes each makes the
    /// memory irrelevant; what the bound protects is the lookup at close time, and no machine has
    /// anywhere near this many windows open at once.
    /// </summary>
    private const int MaxOrigins = 256;

    /// <summary>
    /// How long the application's window may have been on screen before we hid it, in
    /// milliseconds, before we give up and play no animation for that window.
    ///
    /// This is a DURATION, not a time budget, and it is measured rather than assumed:
    /// OnWindowShown subtracts the moment Windows raised the window's show event from the moment
    /// our hide completed, both on the GetTickCount clock the events themselves use. So on a slow
    /// machine the measured number grows by itself and more windows are cancelled. That is the
    /// correct answer - on a slow machine we really do lose more of these races - and it means
    /// this constant does not need per-machine tuning. It only decides how late is too late to
    /// be worth drawing over.
    ///
    /// Five sits in the empty middle of a bimodal distribution measured on the development
    /// machine: windows we hid in time reported 0 ms, and windows that beat us reported 7-27 ms.
    /// It is a perceptual constant as much as a technical one - about a third of a frame at
    /// 60 Hz, just under one frame at 144 Hz - so a high-refresh display would justify a smaller
    /// value. The distribution itself is the part that could differ elsewhere: on a machine slow
    /// enough to close that 0-versus-7 gap, this threshold would become less reliable.
    ///
    /// Every measured value is logged, so it can be re-tuned from real numbers instead of
    /// guessed at.
    /// </summary>
    /// <summary>
    /// How long a window may have been on screen before the animation is abandoned: one composed frame.
    ///
    /// That is what the question is really about - was the window presented at least once, so that the user
    /// could have seen it - and it is a property of the display rather than a number to choose. It used to be
    /// a constant of 5 ms, which is less than a frame on a 60 Hz display (16.7 ms) and more than one on a
    /// 165 Hz display: on 60 Hz it abandoned animations nobody had seen, and on a fast display it let through
    /// a window that had already been presented. The cadence is measured at startup from the compositor and is
    /// the same one frames are paced by, so the two cannot disagree.
    ///
    /// Floored, so the rule is "strictly more than one frame": a window that was visible for exactly one frame
    /// period may or may not have been composited, and the animation is worth more than that doubt.
    /// </summary>
    private int VisibleLimitMs
    {
        get
        {
            double frame = _thread?.FrameMs ?? 1000.0 / 60.0;
            return Math.Max(1, (int)Math.Floor(frame));
        }
    }

    /// <summary>
    /// How often the hide is re-asserted, in milliseconds. See the note where it is used: this is a
    /// safety net measured against what it protects - an application undoing the hide - and that does
    /// not need frame-rate granularity.
    /// </summary>
    private const long ReassertIntervalMs = 50;

    public Animator(Settings s, HideGuard guard) { _s = s; _guard = guard; }

    /// <summary>
    /// Registers the panel window class and starts the animation thread.
    ///
    /// The class is registered here rather than on the thread because registration is process-wide:
    /// what is thread-affine is the window, which the thread creates and pumps. Doing it before the
    /// thread starts means Panel.Create can never find the class missing.
    /// </summary>
    public void Start(IntPtr tickTarget)
    {
        Panel.RegisterClass();
        ClickTracker.Start();
        KeyTracker.Start();
        _thread.Start(tickTarget);
        LogResources("at startup");
    }

    /// <summary>
    /// Destroys every panel, stops the thread, and removes both input hooks. The thread stop joins, so no
    /// panel outlives it; the hooks go last, mirroring the order Start sets things up in.
    /// </summary>
    public void Stop()
    {
        foreach (var a in _active) _thread.Release(a.Panel);
        _thread.Stop();
        _active.Clear();
        ClickTracker.Stop();
        KeyTracker.Stop();
    }

    /// <summary>
    /// The rect the panel has to cover: the frame the user actually sees.
    ///
    /// DWMWA_EXTENDED_FRAME_BOUNDS is that frame. GetWindowRect is not - it also includes the
    /// invisible resize border, which is never drawn. Taking the UNION of the two, as this did,
    /// therefore always made the panel slightly larger than the window: the safer of the two errors
    /// (too small leaves a ring of the real window showing for the whole animation, which is a
    /// visible defect), but still something the user can see.
    ///
    /// So the DWM frame is used on its own, and the alignment was confirmed by eye. Measured on a
    /// real window the two rects differ by 11 px on the left, right and bottom and 0 on top - and
    /// the difference is not constant: another window in the same run reported zero on all four
    /// sides, which is exactly why shrinking by a fixed inset would have been the wrong repair.
    /// </summary>
    private static RECT TargetRect(IntPtr hwnd)
    {
        Native.GetWindowRect(hwnd, out var wr);
        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS,
                                         out var fb, Marshal.SizeOf<RECT>()) == 0
            && fb.Right > fb.Left && fb.Bottom > fb.Top)
            return fb;
        return wr;
    }

    /// <summary>
    /// Both rects and the difference between them, once per window. This is the measurement that
    /// says whether the panel lines up: the window rect includes the invisible resize border, the
    /// DWM frame is what is drawn, and the insets are how far inside the former the latter sits.
    ///
    /// A diagnostic, and stopped at the top when debug mode is off rather than left to Log to swallow:
    /// its two queries would otherwise run for every window on the hot path and the answer be thrown
    /// away. (AlphaOf needs no gate - it is only ever called from inside a Log.Write argument, so it
    /// costs nothing until something is listening for the result.)
    /// </summary>
    private static void LogRectComparison(IntPtr hwnd)
    {
        if (!Log.On) return;
        Native.GetWindowRect(hwnd, out var wr);
        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS,
                                         out var fb, Marshal.SizeOf<RECT>()) != 0) return;
        Log.Write($"  rects: window {wr.Left},{wr.Top} {wr.Right - wr.Left}x{wr.Bottom - wr.Top}" +
                  $" | drawn {fb.Left},{fb.Top} {fb.Right - fb.Left}x{fb.Bottom - fb.Top}" +
                  $" | inset L{fb.Left - wr.Left} T{fb.Top - wr.Top} R{wr.Right - fb.Right} B{wr.Bottom - fb.Bottom}");
    }

    /// <summary>
    /// What will be visible in the target window's place once we make it transparent.
    ///
    /// A layered window at alpha 0 does not become a hole: it stops being drawn, and whatever is
    /// behind it in Z-order is what the user sees. For a window that fills the screen that is the
    /// desktop and nobody notices, but for a panel or a sub-frame it is frequently another window
    /// of the same application - which reads as "the window and the animation appeared together",
    /// because the window still on screen is not the one we hid.
    ///
    /// This is the measurement that decides whether that is what is happening, rather than it being
    /// reasoned about. The parent is logged too, although a top-level window that had one would
    /// already have been filtered out, so that the log shows that as a fact rather than an
    /// assumption.
    ///
    /// Stopped at the top while debug mode is off, because what it costs is a walk down the Z-order and
    /// what comes of it is a line nobody is reading.
    /// </summary>
    private static void LogWhatShowsThrough(IntPtr hwnd)
    {
        if (!Log.On) return;
        Log.Write($"  through: parent={Describe(Native.GetParent(hwnd))}" +
                  $" | first visible below={FirstVisibleBelow(hwnd)}");
    }

    /// <summary>
    /// The first window below this one in Z-order that is actually visible: what the user sees in
    /// the target window's place once it has been made transparent.
    ///
    /// One step of GetWindow(GW_HWNDNEXT) is not enough, and the first version of this measurement
    /// made exactly that mistake. The window immediately below a foreground window is very often an
    /// invisible helper - a 0x0 IME window, a collapsed combo list box - so the answer came back
    /// "nothing visible" and settled nothing. Walking until something visible turns up is the
    /// question that was actually being asked.
    ///
    /// Bounded, because the Z-order is long and a diagnostic must not become a hang.
    /// </summary>
    private static string FirstVisibleBelow(IntPtr hwnd)
    {
        IntPtr w = Native.GetWindow(hwnd, Native.GW_HWNDNEXT);
        for (int i = 0; i < 40 && w != IntPtr.Zero; i++)
        {
            if (Native.IsWindowVisible(w)) return $"[{i + 1}] {Describe(w)}";
            w = Native.GetWindow(w, Native.GW_HWNDNEXT);
        }
        return w == IntPtr.Zero ? "none (end of Z-order)" : "none within 40";
    }

    /// <summary>Class, size and visibility of a window, for the log lines above. Not itself
    /// conditional - it returns a value, which a Conditional method may not - and it is only ever
    /// called from a Conditional one, so it costs nothing in a Release build.</summary>
    private static string Describe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "none";
        var cls = new StringBuilder(128);
        Native.GetClassNameW(hwnd, cls, 128);
        Native.GetWindowRect(hwnd, out var r);
        return $"{cls} {r.Right - r.Left}x{r.Bottom - r.Top} visible={Native.IsWindowVisible(hwnd)}";
    }

    /// <summary>
    /// The window's layered alpha, or -1 when it is not layered at all, so the log can tell
    /// "no transparency" apart from "transparency with the wrong value".
    /// </summary>
    private static int AlphaOf(IntPtr hwnd)
        => Native.GetLayeredWindowAttributes(hwnd, out _, out byte a, out _) ? a : -1;

    /// <summary>
    /// The application showed a window. If it beat our hide by enough for the user to have seen
    /// it, abandon the animation for that window.
    ///
    /// This is a measurement, not a guess: the event carries the moment Windows raised it, so
    /// comparing that with when we finished hiding says exactly how long the window was on
    /// screen. The alternative - deciding in advance from a fixed threshold whether we will win
    /// - would sometimes abandon an animation we could have had.
    /// </summary>
    public void OnWindowShown(IntPtr hwnd, uint eventTime)
    {
        // A window that is shown again while its close animation is running was hidden, not closed -
        // closing to the tray, and then the user reopening it. A panel shrinking towards the middle of a
        // window that is back on screen is worse than no animation at all.
        //
        // Checked here, before the table below, because that table holds windows this program hid during
        // an opening animation and its entry is long gone by the time a window closes. The same check
        // made further down, behind that lookup, could therefore never run - the safety net had a hole
        // in it exactly where it was needed.
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var c = _active[i];
            if (!c.Closing || c.Target.Hwnd != hwnd) continue;
            Log.Write($"hwnd={hwnd} shown again while closing; that was a hide, not a close");
            Finish(i, c, restore: false);
            break;
        }

        if (!_hideCompletedTick.TryGetValue(hwnd, out uint hiddenAt))
        {
            // The show event beat the hide, so there is nothing to compare it with yet. Its moment is kept and
            // judged as soon as the hide finishes, which is the branch in OnWindowCreated; dropping it here,
            // which is what this used to do, left an animation to play over a window the user had already
            // seen and left no line in the log to say so.
            if (_shownBeforeHide.Count > 128) _shownBeforeHide.Clear();
            _shownBeforeHide[hwnd] = eventTime;
            return;
        }
        _hideCompletedTick.Remove(hwnd);

        // Unsigned subtraction, so this stays correct when the 32-bit tick count wraps.
        int visibleForMs = unchecked((int)(hiddenAt - eventTime));
        if (visibleForMs <= VisibleLimitMs)
        {
            Log.Write($"hwnd={hwnd} showed {visibleForMs} ms before we hid it; in time (a frame is" +
                      $" {VisibleLimitMs} ms)");
            // The animation is going to be played, and this is the earliest the program can know the window
            // is on screen: start it here and ask for its first frame at once, rather than leaving both to
            // the next tick. See MarkBecameVisible for what that pass costs.
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                var a = _active[i];
                if (a.Target.Hwnd != hwnd || a.Phase != 0 || a.BecameVisible) continue;
                if (!Native.IsWindowVisible(hwnd)) break;
                long now = Compat.TickCount64;
                MarkBecameVisible(a, now);
                RequestGrowFrame(a, now);
                break;
            }
            return;
        }

        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var a = _active[i];
            if (a.Target.Hwnd != hwnd) continue;

            Log.Write($"hwnd={hwnd} was on screen {visibleForMs} ms before we hid it, which is more than" +
                      $" one frame ({VisibleLimitMs} ms); cancelling this animation");
            a.Cancelled = true;
            return;
        }
    }

    /// <summary>
    /// The window has appeared, which is where an opening animation starts.
    ///
    /// Called from whichever notices first: the tick that polls for it, or the window's own show event. The
    /// event says the same thing up to a message-loop pass earlier, and that pass is what the card's first
    /// frame costs - measured, the first frame landed 12-23 ms after the window appeared when the tick found
    /// it. Starting from the event is the opening animation's half of what the closing one already has as
    /// FirstFrame: the frame handed over with the panel instead of waiting for a tick.
    /// </summary>
    private void MarkBecameVisible(Anim a, long now)
    {
        a.BecameVisible = true;
        a.Started = now;                  // the animation starts when it appears
        // This, not being accepted as a candidate, is the moment a window really had an opening animation -
        // and the only moment at which it may be allowed a closing one. See Watcher.Remember.
        OnBecameVisible?.Invoke(a.Target.Hwnd, a.Target.Pid);
        RememberOrigin(a);
        Log.Write($"hwnd={a.Target.Hwnd} became visible; animation starts now");
    }

    /// <summary>
    /// Asks the animation thread for the frame this moment's geometry calls for, and answers how far through
    /// the grow it is. Shared by the tick and by the show event, which is the point of it: the same geometry
    /// from either, so that starting the animation from the event cannot disagree with the frames after it.
    ///
    /// The card is opaque from this, its first frame. It used to fade in over 48 ms - which is what the mod
    /// does as fadeInPercent, and it was copied here to keep the animation from starting with a cut - but
    /// those 48 ms are 48 ms in which nothing is on screen at all, and by then the window has already gone:
    /// measured, the first frame lands 12-23 ms after the window appears, so the card was invisible for up to
    /// 70 ms. A cut to a card the size of an icon is not the moment that needed softening; the handoff at the
    /// other end is, and that one has its own fade.
    /// </summary>
    private double RequestGrowFrame(Anim a, long now)
    {
        double t = Compat.Clamp((now - a.Started) / (double)a.Duration, 0, 1);
        double e = Ease(t);
        var tgt = TargetRect(a.Target.Hwnd);          // live, every frame
        int tw = tgt.Right - tgt.Left, th = tgt.Bottom - tgt.Top;
        int x = (int)Math.Round(a.AnchorX + (tgt.Left - a.AnchorX) * e);
        int y = (int)Math.Round(a.AnchorY + (tgt.Top - a.AnchorY) * e);
        int w = (int)Math.Round(a.StartSize + (tw - a.StartSize) * e);
        int h = (int)Math.Round(a.StartSize + (th - a.StartSize) * e);
        // The corner is interpolated along the card's own curve, which is what the mod does
        // (mobile-open-animation.wh.cpp:2800) and what makes the two things one movement rather than two
        // running side by side: the rounding is at its roundest when the card is at its smallest, and it
        // straightens out in step with everything else.
        int rad = (int)Math.Round(a.StartRadius + (a.TargetRadius - a.StartRadius) * e);
        _thread!.RequestFrame(a.Panel, x, y, w, h, 255, rad);
        return t;
    }

    /// <summary>
    /// Remembers where the card started, at the moment the opening animation proves the window really
    /// appeared. See Origin for what is stored and why it is stored whole.
    ///
    /// Off means off: nothing is written while the setting is off, so turning it back on cannot
    /// resurrect a point from before.
    /// </summary>
    private void RememberOrigin(Anim a)
    {
        if (!_s.ReturnToOrigin) return;
        // Nothing was clicked, so there is nowhere this window "came from".
        if (!a.FromClick) return;
        // The return is deliberately not a global feature. It only means anything where the launch
        // point is still there when the window closes, and the two that are are the desktop icon the
        // click was on and the taskbar the click was made on - the icons sit there for as long as the
        // window is open, and the taskbar is always there. The Start menu and the search flyout are the
        // opposite: the click is on a tile or a result inside a flyout that has already gone by the time
        // the window closes, and shrinking a card towards an empty patch of screen where a menu used to
        // be would be describing a place the user cannot see. See ClickTracker.ClassifyClick.
        if (a.Surface == ClickTracker.Surface.None) return;
        if (_origins.Count >= MaxOrigins) PruneOrigins();

        _origins[a.Target.Hwnd] = new Origin
        {
            Pid = a.Target.Pid,
            X = a.AnchorX, Y = a.AnchorY, Size = a.StartSize,
            Surface = a.Surface,
            DpiAtOpen = (int)Native.GetDpiForWindow(a.Target.Hwnd),
            Monitors = Native.GetSystemMetrics(Native.SM_CMONITORS),
            VX = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN),
            VY = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN),
            VW = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN),
            VH = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN),
        };
        Log.Write($"hwnd={a.Target.Hwnd} origin remembered: {a.AnchorX},{a.AnchorY} size {a.StartSize}" +
                  $" (pid {a.Target.Pid}); closing it will come back here");
    }

    /// <summary>True when the display still has the shape an Origin was written under.</summary>
    private static bool SameDisplay(Origin o) =>
        o.Monitors == Native.GetSystemMetrics(Native.SM_CMONITORS) &&
        o.VX == Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN) &&
        o.VY == Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN) &&
        o.VW == Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN) &&
        o.VH == Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);

    /// <summary>
    /// The class of the window that is really in front of this point, discounting the window being closed
    /// and our own windows, or "" when nothing is there at all. <paramref name="underPointer"/> receives
    /// the class of the window the system itself hands a click to there, which is what makes a wrong
    /// verdict readable in the log instead of just absent.
    ///
    /// This is what separates "the desktop is still what the user sees at this point" from "something has
    /// been put in front of it since the window was opened", which is the case an anchor-only record
    /// cannot see: a window opened from a desktop icon at the top left, with a maximized application in
    /// front of that corner by the time it closes, would otherwise send the card to a place that is
    /// behind another window - the same mistake as sending it towards an empty patch of screen where the
    /// Start menu used to be.
    ///
    /// WindowFromPoint first, because it is one call and it accounts for child windows and layered windows
    /// the way the compositor does - and because a walk from the top of the Z-order cannot be trusted to
    /// arrive at all. Measured on this machine, that order holds 273 top-level windows, and a walk bounded
    /// at 200 never reached the desktop at the bottom of it: the first version of this check walked, hit
    /// the bound, and reported that nothing was at the point for every window it was asked about, which is
    /// how the feature came to be switched off for all of them at once.
    ///
    /// What WindowFromPoint cannot do is see past a discounted window, and the window being closed is
    /// usually exactly that, since it is very often far larger than the square it was opened from. So when
    /// what is under the pointer is the closing window, one of our panels, or something not really showing
    /// - invisible, minimized, or cloaked, because cloaking is how an application hides a window that is
    /// nominally visible - the search continues downwards from there. That part is small: it starts from
    /// somewhere already close to the answer and stops at the first thing that covers the point.
    ///
    /// The class is asked for rather than a handle, because the question is which shell surface is there;
    /// see ClickTracker.SurfaceOfClass.
    /// </summary>
    private static string FrontmostClassAt(POINT pt, IntPtr closing, out string underPointer)
    {
        uint self = Native.GetCurrentProcessId();
        IntPtr w = Native.WindowFromPoint(pt);
        underPointer = w == IntPtr.Zero ? "" : ClassNameOf(RootOf(w));

        for (int i = 0; i < 64 && w != IntPtr.Zero; i++)
        {
            IntPtr root = RootOf(w);
            Native.GetWindowThreadProcessId(root, out uint pid);
            bool discounted = root == closing || pid == self;
            if (!discounted && ReallyShowing(root) && Covers(root, pt)) return ClassNameOf(root);
            w = NextShowingCovering(pt, root);
        }
        return "";
    }

    private static IntPtr RootOf(IntPtr h)
    {
        IntPtr root = Native.GetAncestor(h, Native.GA_ROOT);
        return root == IntPtr.Zero ? h : root;
    }

    private static bool ReallyShowing(IntPtr h) =>
        Native.IsWindowVisible(h) && !Native.WindowIsMinimized(h) && !DwmCloaked(h);

    private static bool Covers(IntPtr h, POINT pt) =>
        Native.GetWindowRect(h, out var r)
        && pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom;

    /// <summary>The first window below this one that is really showing and covers the point, or Zero.</summary>
    private static IntPtr NextShowingCovering(POINT pt, IntPtr from)
    {
        for (IntPtr w = Native.GetWindow(from, Native.GW_HWNDNEXT); w != IntPtr.Zero;
             w = Native.GetWindow(w, Native.GW_HWNDNEXT))
        {
            if (ReallyShowing(w) && Covers(w, pt)) return w;
        }
        return IntPtr.Zero;
    }

    private static string ClassNameOf(IntPtr h)
    {
        var sb = new StringBuilder(64);
        Native.GetClassNameW(h, sb, 64);
        return sb.ToString();
    }

    /// <summary>
    /// Drops origins whose window no longer exists. Called only when the table is full, so nothing on
    /// the ordinary path pays for it - and an entry found gone is one whose window was torn down with
    /// its process, which raises no event for us to have removed it on.
    /// </summary>
    private void PruneOrigins()
    {
        var gone = new List<IntPtr>();
        foreach (var k in _origins.Keys) if (!Native.IsWindow(k)) gone.Add(k);
        foreach (var k in gone) _origins.Remove(k);
        // Still full means every entry is a live window, which is not a state to settle by choosing
        // victims: start over. These records are an extra, and losing one costs an animation, never a
        // window.
        if (_origins.Count >= MaxOrigins) _origins.Clear();
    }

    /// <summary>
    /// Starts the reverse animation for a window that is closing: the same icon panel the open
    /// animation uses, shrinking from the rectangle the window had down to a small square - at the
    /// window's own centre, or back at the square the opening animation started from when that is known
    /// (see Origin, and the setting that turns it off).
    ///
    /// Deliberately not a snapshot of the window. PrintWindow would have to run while the window is
    /// still alive, it sends WM_PRINT synchronously and will hang on a busy one, and the open
    /// animation has no equivalent of a real bitmap - so a bitmap would make the two ends of a
    /// window's life look like two different features.
    ///
    /// A remembered anchor was a thing this deliberately did not have, on the grounds that an entry per
    /// window, unbounded, keyed by an HWND that Windows reuses, holding coordinates that a monitor,
    /// resolution or DPI change invalidates, was more trouble than it was worth. Each of those is now
    /// answered rather than avoided: the table is bounded and pruned, an entry is checked against the
    /// process it was written for so a reused handle cannot claim it, the display's shape is compared
    /// before the stored point is used at all, and - because a position that is still arithmetically
    /// correct can still have been covered by something else in the meantime - whatever is now in front
    /// of that point is asked about too. See FrontmostClassAt.
    /// </summary>
    public void OnWindowClosed(WindowInfo w)
    {
        // Read per close rather than captured at startup, so turning it off in the tray menu stops
        // the next window that closes and not the next time the program is started.
        if (!_s.CloseAnimation) return;
        if (_active.Count >= 3) return;
        if (_active.Any(a => a.Closing && a.Target.Hwnd == w.Hwnd)) return;

        var rect = TargetRect(w.Hwnd);
        // A minimized window's rectangle is the parking spot Windows keeps it in - somewhere around
        // -32000 - and not anywhere the user ever saw it. Closing one from the taskbar is ordinary, so the
        // card has to start from where the window would be if it were restored; the placement remembers
        // that and the rectangle does not. The window may already be gone by the time this arrives, in
        // which case there is nothing to ask and the read above is all there is.
        if (Native.WindowIsMinimized(w.Hwnd))
        {
            var pl = new Native.WINDOWPLACEMENT { length = Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
            if (Native.GetWindowPlacement(w.Hwnd, ref pl)
                && pl.normalPosition.Right > pl.normalPosition.Left
                && pl.normalPosition.Bottom > pl.normalPosition.Top)
            {
                rect = pl.normalPosition;
                Log.Write($"hwnd={w.Hwnd} was minimized; closing from its restored rectangle instead");
            }
        }
        int rw = rect.Right - rect.Left, rh = rect.Bottom - rect.Top;
        if (rw < 8 || rh < 8)
        {
            // DwmGetWindowAttribute can already be refusing to answer for a window on its way out.
            // GetWindowRect was read a moment earlier by Inspect, so use that instead of giving up.
            rect.Left = w.X; rect.Top = w.Y; rw = w.Width; rh = w.Height;
        }
        if (rw < 8 || rh < 8) { Log.Write($"hwnd={w.Hwnd} closed, but no usable rectangle; not animated"); return; }

        int dpi = (int)Native.GetDpiForWindow(w.Hwnd);
        // Unreadable (the window is usually already gone on this path) is not a change, so the DPI test
        // below is skipped rather than failed - see Origin.
        int dpiReadable = dpi;
        if (dpi < 48 || dpi > 480) dpi = 96;
        // The card shrinks to the size the opening animation starts at, so its corners round out to
        // meet it: 22% of that size, the same proportion, so that a window closing and a window
        // opening are each other's reverse rather than merely both round. With the dynamic curve
        // turned off the radius simply stays where the window's own was.
        int endSize = Native.MulDiv(_s.StartSizePx, dpi, 96);
        // The radius the window itself has, which is where the closing animation starts from - it is
        // the shape the real window was in when it disappeared.
        int targetRadius = Native.IsWindows11 ? Native.MulDiv(8, dpi, 96) : 0;

        int cx = rect.Left + rw / 2, cy = rect.Top + rh / 2;
        int endX = cx - endSize / 2, endY = cy - endSize / 2;
        string dest = $"at the centre {cx},{cy}";

        // Consumed either way: a second close for the same window (destroy after hide, say) must not
        // find this record again and play a second return.
        if (_origins.TryGetValue(w.Hwnd, out var origin))
        {
            _origins.Remove(w.Hwnd);
            bool ours = origin.Pid == w.Pid;
            bool sameDisplay = SameDisplay(origin);
            bool sameDpi = dpiReadable == 0 || origin.DpiAtOpen == 0 || dpiReadable == origin.DpiAtOpen;
            // The point asked about is the remembered one itself - the click, which is also the corner the
            // card collapses into. Not the centre of the square: a click near an edge puts that centre off
            // the screen altogether (a taskbar is 60 pixels tall and the square is 144), and a point that
            // is not on any screen has nothing in front of it either, which would read as "covered" and
            // refuse the taskbar's own launches.
            var anchor = new POINT { X = origin.X, Y = origin.Y };
            string front = FrontmostClassAt(anchor, w.Hwnd, out string underPointer);
            bool exposed = ClickTracker.SurfaceOfClass(front) == origin.Surface;

            if (_s.ReturnToOrigin && ours && sameDisplay && sameDpi && exposed)
            {
                endX = origin.X; endY = origin.Y; endSize = origin.Size;
                dest = $"back to the launch point {origin.X},{origin.Y} (front={front})";
            }
            else if (_s.ReturnToOrigin && ours)
            {
                // Says which of the checks refused it, and for the last of them says both classes it
                // compared - what is in front of the point now, and what the system hands a click to
                // there. They differ whenever the closing window or one of our panels is what is under the
                // pointer, and both are needed to tell "the desktop really has been covered" apart from
                // "the search for what covers it went wrong".
                string why = !sameDisplay ? "the display has changed shape"
                           : !sameDpi ? "the window's DPI has changed"
                           : $"what is in front of {origin.X},{origin.Y} now is " +
                             (front.Length == 0 ? "nothing" : front) +
                             $", the pointer is over {(underPointer.Length == 0 ? "nothing" : underPointer)}";
                Log.Write($"hwnd={w.Hwnd} launch point dropped: {why}; closing to the centre instead");
            }
        }

        // Half of the square the card shrinks into, which is the roundest a square of that size can be and
        // is exactly the limit the drawing clamps to: the closing card therefore ends as a disc that fades
        // away, rather than as a rounded square. Taken as "half the shortest side" rather than a percentage
        // of it because the point is to start from the roundest shape the card can hold; the reachable
        // range, if this turns out to be too much, is from here down to the 22 per cent this used to be.
        int endRadius = _s.DynamicCorner ? endSize / 2 : targetRadius;
        // The first frame is known right now - this rectangle, fully opaque - so it is handed over
        // with the panel instead of waiting for a tick and then a flush. That wait is about 16 ms,
        // one frame, and it is the seam between the window vanishing and the panel appearing.
        var first = new AnimationThread.FirstFrame(rect.Left, rect.Top, rw, rh, 255, targetRadius);
        var panel = _thread.Attach(w, endSize, endRadius, rect.Left, rect.Top, endX, endY, endSize, first);

        Log.Write($"hwnd={w.Hwnd} closing: {rw}x{rh} at {rect.Left},{rect.Top} -> {endSize}px {dest}" +
                  $" radius {targetRadius}->{endRadius}");
        _active.Add(new Anim
        {
            Target = w,
            Panel = panel,
            Started = Compat.TickCount64,
            Duration = Math.Max(30, _s.DurationMs),
            Phase = 0,
            Closing = true,
            TargetRadius = targetRadius,
            EndRadius = endRadius,
            StartRectX = rect.Left, StartRectY = rect.Top, StartRectW = rw, StartRectH = rh,
            EndX = endX, EndY = endY, EndSize = endSize,
        });
    }

    // ------------------------------------------------------------------ entry point
    public void OnWindowCreated(WindowInfo w)
    {
        // Every millisecond between here and the hide below is a millisecond stolen from the
        // application's create->show gap. 7-Zip's is 1.96 ms (spike/RESULTS.md), so nothing
        // slow may happen in between: no file I/O, no registry, no messages to the target.
        // Stopwatch, not TickCount64: the latter steps in ~15.6 ms increments, which is
        // coarser than the few-millisecond regression this measurement exists to catch.
        long tEnter = System.Diagnostics.Stopwatch.GetTimestamp();
        Log.Write($"candidate hwnd={w.Hwnd} pid={w.Pid} proc={w.ProcessName} cls={w.ClassName} {w.Width}x{w.Height} at {w.X},{w.Y}");
        if (_active.Any(a => !a.Closing && a.Target.Hwnd == w.Hwnd)) { Log.Write("  -> already animating"); return; }
        if (_active.Count >= 3) { Log.Write("  -> too many animations in flight"); return; }

        // This handle is being created again, so anything remembered for it belongs to the window that
        // had the handle before: Windows reuses them, and the process check at close time cannot tell
        // those two windows apart, because they are the same process. Dropped here rather than at the
        // moment a window becomes visible, so that a window which never becomes visible cannot leave the
        // older point behind to be claimed either.
        _origins.Remove(w.Hwnd);

        // Where the card grows from.
        //
        // If the user clicked just now, that click is what opened this window, so grow out of
        // it. Otherwise the window opened on its own - Steam popping a login window after an
        // update, or anything appearing after a UAC consent - and growing it out of wherever
        // the pointer happens to be is nonsense, so it grows in place instead.
        //
        // UAC needs no special case: the consent prompt runs on the secure desktop, where a
        // low-level mouse hook never sees the click, so the consent itself can never be blamed for
        // a window - only the icon click that started it, and only if the consent came back quickly.
        //
        // "Just now" is not only a short window. A cold start - Chromium, and anything else that
        // takes seconds to put its window up - is past any grace short enough to be safe, yet the
        // click plainly started the process. That is a fact rather than a guess, so it is asked as
        // one: the process did not exist when the click was made (ClickTracker.LaunchedByClick).
        POINT pt;
        bool recent = ClickTracker.HasRecentClick(ClickTracker.DefaultGraceMs);
        bool started = !recent && ClickTracker.LaunchedByClick(w.Pid);
        bool fromClick = recent || started;
        // Only the in-place case gets the nudge below: the other two anchors are real points on the screen.
        bool inPlace = false;
        if (fromClick)
        {
            pt = new POINT { X = ClickTracker.LastX, Y = ClickTracker.LastY };
            Log.Write($"  -> anchor: click at {pt.X},{pt.Y}" +
                      (started ? $" (this click started pid {w.Pid})" : "") +
                      $" on {ClickTracker.DescribeLastSurface()}");
        }
        else if (KeyTracker.TryAnchor(w.Pid, out pt))
        {
            // Nothing was clicked, so the user started this with the keyboard. The point is the middle of the
            // control that had the focus when they pressed Enter - see KeyTracker for which places count and
            // why nothing else does. Deliberately not remembered as an origin: the return animation is for the
            // two surfaces that are still there when the window closes, and a key press was on neither.
            Log.Write($"  -> anchor: keyboard, Enter on {KeyTracker.LastSource} at {pt.X},{pt.Y}");
        }
        else
        {
            inPlace = true;
            pt = new POINT
            {
                X = w.X + w.Width / 2 - _s.StartSizePx / 2,
                Y = w.Y + w.Height / 2 - _s.StartSizePx / 2,
            };
            Log.Write($"  -> anchor: nothing to grow from ({KeyTracker.LastReason}); in place at {pt.X},{pt.Y}");
        }

        if (!_guard.Hide(w.Hwnd)) { Log.Write("  -> Hide() declined; nothing animated"); return; }
        // The number that matters. If this ever grows past a few milliseconds, windows with a
        // small create->show gap (7-Zip) will be visible before we hide them.
        double hideMs = (System.Diagnostics.Stopwatch.GetTimestamp() - tEnter) * 1000.0
                        / System.Diagnostics.Stopwatch.Frequency;
        Log.Write($"  -> hidden, {hideMs:F2} ms after the create event");
        // Remember when, so the window's own show event can tell us whether we were in time.
        // Bounded: a window whose show event never arrives is forgotten once this grows, which
        // can only cost us the chance to cancel, never hide anything from the user.
        if (_hideCompletedTick.Count > 128) _hideCompletedTick.Clear();
        uint hiddenAt = unchecked((uint)Environment.TickCount);
        _hideCompletedTick[w.Hwnd] = hiddenAt;

        // If this window's show event arrived before the hide finished, it could not be judged at the time -
        // there was nothing to compare it with - so it was kept until now, when there is. Judged here rather
        // than left to the animation, because at this point nothing has been created yet: a window the user
        // has already seen costs nothing at all, where cancelling later costs a panel that exists for a
        // millisecond and has to be taken apart again.
        if (_shownBeforeHide.TryGetValue(w.Hwnd, out uint shownAt))
        {
            _shownBeforeHide.Remove(w.Hwnd);
            int earlyMs = unchecked((int)(hiddenAt - shownAt));
            if (earlyMs >= 0 && earlyMs <= ShowBeforeHideMaxAgeMs)
            {
                if (earlyMs > VisibleLimitMs)
                {
                    Log.Write($"hwnd={w.Hwnd} showed {earlyMs} ms before the hide finished, and its show " +
                              $"event came first (more than one frame, {VisibleLimitMs} ms);" +
                              $" not animating at all");
                    // This window is not going to be animated, so there is nothing left to judge about it.
                    // Clearing it here keeps a later show event - the window being minimized and restored,
                    // say - from measuring itself against this hide and reaching a conclusion about an
                    // animation that does not exist.
                    _hideCompletedTick.Remove(w.Hwnd);
                    return;
                }
                Log.Write($"hwnd={w.Hwnd} showed {earlyMs} ms before the hide finished, its show event " +
                          $"having come first; in time");
            }
        }

        // What the window actually looks like at the moment we are about to draw over it, and how
        // the two rects differ. The MMC case showed a 260 ms hide with no cancellation, and none of
        // the numbers that existed then could say whether the real window was on screen while the
        // panel was drawn - so this records it instead of it being reasoned about.
        Log.Write($"  after hide: visible={Native.IsWindowVisible(w.Hwnd)} alpha={AlphaOf(w.Hwnd)}" +
                  $" exStyle=0x{Native.GetWindowLong(w.Hwnd, Native.GWL_EXSTYLE):X8}");
        LogRectComparison(w.Hwnd);
        LogWhatShowsThrough(w.Hwnd);

        int dpi = (int)Native.GetDpiForWindow(w.Hwnd);
        if (dpi < 48 || dpi > 480) dpi = 96;

        // The start square is in logical pixels, so it has to be scaled: 96 px is roughly a desktop
        // icon at 100%, and half an icon at 200%. The radius was already scaled and the size was
        // not, so on a mixed-DPI setup the panel started from a visibly different size depending on
        // which monitor the window was on.
        int startSize = Native.MulDiv(_s.StartSizePx, dpi, 96);

        // The anchor was worked out before the hide, and there is no room for a DPI query there -
        // that is the path where a stalled millisecond costs a window. In the in-place case the
        // anchor is the *top-left* of the start square, so it was placed using the unscaled size and
        // has to be nudged by half the difference to stay centred on the window. The click and
        // keyboard cases need no nudge: there the anchor is a point that was on the screen.
        if (inPlace)
        {
            int nudge = (startSize - _s.StartSizePx) / 2;
            pt = new POINT { X = pt.X - nudge, Y = pt.Y - nudge };
        }

        int targetRadius = Native.IsWindows11 ? Native.MulDiv(8, dpi, 96) : 0;
        // Half the start square, which is the roundest that square can be: at this size the card is a
        // disc, and it squares off into the window's own corner as it grows. This used to be 22 per cent
        // of the square - the proportion the Windhawk mod uses (mobile-open-animation.wh.cpp:2787) - and
        // the reasoning for matching the mod was that the target radius is ours to choose but how round
        // the card starts is a matter of how the animation looks. What that missed is the size of the
        // card: 22 per cent of a 144 pixel square is 31 pixels, and by the time the card is two thousand
        // pixels wide the same interpolation has it down to single figures, which reads as a sharp corner
        // on a large window. Starting from the roundest shape the card can hold keeps the corner round
        // through the visible part of the growth; the reachable range if this is too much is from here
        // down to that 22 per cent.
        //
        // With the dynamic curve off, the card is simply the shape of the window it grows into for its
        // whole life: no rounding out, and no transition to follow.
        int startRadius = _s.DynamicCorner ? startSize / 2 : targetRadius;

        // The panel window is created on the animation thread, not here. This thread only asks for
        // it and gets straight on with the next event; if creation fails, Panel.Failed is set and
        // Tick releases the window through the normal path.
        var panel = _thread.Attach(w, startSize, startRadius, pt.X, pt.Y, 0, 0, 0);
        Log.Write($"  -> panel requested, from {pt.X},{pt.Y} size {startSize} radius {startRadius}->{targetRadius} (dpi {dpi}, setting {_s.StartSizePx})");

        _active.Add(new Anim
        {
            Target = w,
            Panel = panel,
            Started = Compat.TickCount64,
            Duration = Math.Max(30, _s.DurationMs),
            AnchorX = pt.X, AnchorY = pt.Y,
            FromClick = fromClick,
            Surface = ClickTracker.LastSurface,
            StartSize = startSize,
            StartRadius = startRadius,
            TargetRadius = targetRadius,
            Phase = 0,
            ReadyDeadline = Compat.TickCount64 + Math.Max(500, _s.ReadyTimeoutMs),
        });
    }

    // ------------------------------------------------------------------ per-frame tick
    public void Tick()
    {
        Log.Flush();                   // buffered log lines reach the file here, never on the hot path
        _guard.ReleaseExpired();       // watchdog: nothing stays hidden past its deadline

        long now = Compat.TickCount64;
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var a = _active[i];
            // A closing animation is for a window that is already gone, so the liveness check cannot
            // apply to it: left as it is, it would end the animation on its very first frame.
            if (!a.Closing && !Native.IsWindow(a.Target.Hwnd)) { Finish(i, a, restore: false); continue; }

            // The application showed its window before we could hide it. Drawing the animation
            // over a window the user has already seen is the failure we are avoiding; playing
            // nothing at all is the better of the two. This normally lands before the panel has
            // been drawn even once, so usually nothing appears at all.
            if (a.Cancelled)
            {
                Log.Write($"hwnd={a.Target.Hwnd} animation cancelled, window left alone");
                _thread.Release(a.Panel);
                _guard.Release(a.Target.Hwnd);
                _active.RemoveAt(i);
                continue;
            }

            // Panel.Create runs on the animation thread, so a failure arrives as a flag rather than
            // as a return value here. Released through the normal path: never leave a window hidden
            // because a panel could not be made.
            if (a.Panel.Failed)
            {
                Log.Write($"hwnd={a.Target.Hwnd} panel could not be created; window released, nothing animated");
                Finish(i, a, restore: !a.Closing);
                continue;
            }

            // The reverse animation: one shrink-and-fade from the rectangle the window had to a small
            // square at its centre. It continues here rather than falling through, because everything
            // below assumes a window that is still there and a hide that has to be held.
            if (a.Closing)
            {
                double t = a.Duration <= 0 ? 1.0 : Compat.Clamp((now - a.Started) / (double)a.Duration, 0, 1);
                // The geometric progress is the opening curve played backwards: 1 - Ease(1 - t) is
                // slow at first and fast at the end, which is what "it goes back where it came from"
                // means. Running the opening curve forwards, as this did, put almost the entire
                // shrink into the first third of the duration and left a small square sitting there
                // for the remaining two thirds - which reads as collapsing instantly and then
                // vanishing, and was reported exactly that way.
                double e = 1.0 - Ease(1.0 - t);
                int cw = (int)Math.Round(a.StartRectW + (a.EndSize - a.StartRectW) * e);
                int ch = (int)Math.Round(a.StartRectH + (a.EndSize - a.StartRectH) * e);
                int cx = (int)Math.Round(a.StartRectX + (a.EndX - a.StartRectX) * e);
                int cy = (int)Math.Round(a.StartRectY + (a.EndY - a.StartRectY) * e);
                // The corners round out as the card shrinks, the exact reverse of the opening
                // animation, which straightens them as the card grows. This was missing: the closing
                // panel kept whatever corner the window's own had for its whole life, so a window
                // closing looked different from the same window opening.
                //
                // On the card's own curve, as the opening one is: the reverse of Material 3, the same curve
                // the shrink is on, so the rounding is part of one movement rather than a second one beside
                // it. It had a linear curve finishing at seventy per cent for one revision, so that the
                // rounded shape would be settled before the fade began; that was given up for the unity, and
                // the trade it makes is worth knowing about - the corner now does most of its rounding in the
                // last fifth of the time, which is where the fade starts too, so if it ever looks like the
                // corner is arriving late, this is the line that decided it.
                int crad = (int)Math.Round(a.TargetRadius + (a.EndRadius - a.TargetRadius) * e);
                // Opacity holds until the last part of the shrink, then goes quickly. Fading linearly
                // from the first frame did two wrong things: the panel washed out while it was still
                // large, and it reached the end still visibly solid and then vanished. Note that the
                // fade finishes before t reaches 1, so the last frames drawn are already fully
                // transparent - which matters, because Finish releases the panel and any frame asked
                // for at the same moment is dropped rather than drawn.
                const double FadeFrom = 0.70;
                int ca = t <= FadeFrom
                    ? 255
                    : (int)Math.Round(255 * (1 - (t - FadeFrom) / (1 - FadeFrom)));
                _thread.RequestFrame(a.Panel, cx, cy, cw, ch, ca, crad);
                if (t >= 1.0) Finish(i, a, restore: false);
                continue;
            }

            // The app keeps setting its own window state for a while after creating the
            // window, and can undo the hide. "-> hidden" in the log only proves the hide held
            // at one instant; this is what keeps it held for the whole animation, and the log
            // line is how we find out whether that was the problem all along.
            // Re-asserting is a cross-process synchronous send: the target window's own UI thread has
            // to service it, and the first one can take tens of milliseconds if that thread is busy.
            // Doing it once per frame was 165 of them a second on a 165 Hz display and 360 on a 360 Hz
            // one, for a check that is only a safety net - an application that undoes our hide will
            // still be there fifty milliseconds later. The rectangle is still read every frame, which
            // is the part that has to keep up: that is what makes the panel follow a window as it is
            // being resized.
            if (now - a.LastReassert >= ReassertIntervalMs
                && _guard.Reassert(a.Target.Hwnd) && !a.ReassertLogged)
            {
                a.ReassertLogged = true;
                Log.Write($"hwnd={a.Target.Hwnd} the app had undone our hide; re-applied");
            }
            if (now - a.LastReassert >= ReassertIntervalMs) a.LastReassert = now;

            switch (a.Phase)
            {
                case 0:
                {
                    // Nothing is drawn until the window is really on screen. Some programs
                    // create extra top-level windows that are never shown; without this gate
                    // we drew a panel over empty space and left it there until the timeout.
                    if (!Native.IsWindowVisible(a.Target.Hwnd))
                    {
                        if (now > a.ReadyDeadline)
                        {
                            Log.Write($"hwnd={a.Target.Hwnd} never became visible; releasing, no panel drawn");
                            OnAbandoned?.Invoke(a.Target.Hwnd);
                            Finish(i, a, restore: true);
                        }
                        break;
                    }
                    if (!a.BecameVisible) MarkBecameVisible(a, now);

                    double t = RequestGrowFrame(a, now);
                    if (t >= 1.0)
                    {
                        var tgt = TargetRect(a.Target.Hwnd);
                        a.Phase = 1; a.PhaseStarted = now;
                        Log.Write($"hwnd={a.Target.Hwnd} grow done at {tgt.Right - tgt.Left}x{tgt.Bottom - tgt.Top}" +
                                  $" ({tgt.Left},{tgt.Top}), waiting for paint");
                    }
                    break;
                }
                case 1:
                {
                    bool visible = Native.IsWindowVisible(a.Target.Hwnd);
                    bool cloaked = DwmCloaked(a.Target.Hwnd);
                    bool settled = now - a.PhaseStarted > 120;
                    if ((visible && !cloaked && settled) || now > a.ReadyDeadline)
                    {
                        Log.Write($"hwnd={a.Target.Hwnd} handoff (visible={visible} cloaked={cloaked} byTimeout={now > a.ReadyDeadline})");
                        _guard.Release(a.Target.Hwnd);   // reveal underneath the panel first
                        a.Phase = 2; a.PhaseStarted = now;
                    }
                    break;
                }
                case 2:
                {
                    int fade = Math.Max(0, _s.HandoffFadeMs);
                    double t = fade == 0 ? 1.0 : Compat.Clamp((now - a.PhaseStarted) / (double)fade, 0, 1);
                    int alpha = (int)Math.Round(255 * (1 - t));
                    var ft = TargetRect(a.Target.Hwnd);
                    _thread.RequestFrame(a.Panel, ft.Left, ft.Top, ft.Right - ft.Left,
                                         ft.Bottom - ft.Top, alpha, a.TargetRadius);
                    if (t >= 1.0) Finish(i, a, restore: true);
                    break;
                }
            }
        }
    }

    private void Finish(int index, Anim a, bool restore)
    {
        if (restore) _guard.Release(a.Target.Hwnd);
        // Only asks for the panel to go: destroying a window is the job of the thread that created
        // it, so this never calls Destroy itself.
        _thread.Release(a.Panel);
        _active.RemoveAt(index);
        LogResources("after an animation");
    }

    /// <summary>
    /// GDI objects, USER objects and managed heap, at a point where an animation has just finished
    /// and everything it made should be gone. Counts that climb with every animation are a leak, and
    /// the GDI one matters most: the limit is 10,000 objects and the failure is a crash much later,
    /// while the only visible symptom is a memory counter moving by a tenth of a megabyte.
    ///
    /// Stopped at the top while debug mode is off, and this one has to be: it forces a collection to
    /// separate a leak from an uncollected heap, which is a real cost to pay after every animation for a
    /// line nobody asked for.
    /// </summary>
    private static void LogResources(string where)
    {
        if (!Log.On) return;
        uint gdi = Native.GetGuiResources(System.Diagnostics.Process.GetCurrentProcess().Handle, 0);
        uint user = Native.GetGuiResources(System.Diagnostics.Process.GetCurrentProcess().Handle, 1);
        long loose = GC.GetTotalMemory(false) / 1024;
        // The collected figure is the one that answers the question. A number that only ever climbs
        // is what a leak looks like - and also what an uncollected heap looks like, because
        // GetTotalMemory(false) reports what has been allocated, not what survived. Forcing a
        // collection separates them: if this comes back to the startup figure every time, nothing
        // is being kept alive. It is only in Debug builds, where the cost does not matter.
        long held = GC.GetTotalMemory(true) / 1024;
        Log.Write($"resources {where}: GDI={gdi} USER={user} allocated={loose} KB held={held} KB");
    }

    private static bool DwmCloaked(IntPtr hwnd)
    {
        try
        {
            // DWMWA_CLOAKED = 14. Apps that wait for their first frame (Chromium, Electron)
            // cloak themselves; the value going back to 0 is a cross-process "it is painted"
            // signal, which is the EndPaint equivalent we cannot hook from outside.
            if (Native.DwmGetWindowAttribute(hwnd, 14, out int v, sizeof(int)) == 0) return v != 0;
        }
        catch (Exception ex)
        {
            // Answering "not cloaked" is the optimistic answer, and it is the one this check exists to
            // avoid: it lets the handoff reveal a window that has not painted yet. If that ever starts
            // happening, this line is how it will be known.
            Log.Write($"cloaked check failed for hwnd={hwnd}: {ex.GetType().Name}: {ex.Message}");
        }
        return false;
    }

    /// <summary>
    /// The animation curve: Material 3's "emphasized" token, which is a two-segment path and cannot be
    /// written as a single bezier:
    ///     M 0,0 C 0.05,0 0.133333,0.06 0.166666,0.4 C 0.208333,0.82 0.25,1 1,1
    ///
    /// Measured, it puts 43% of the distance into the first 17% of the time and 77% into the first 25%,
    /// so a 240 ms grow is visually over in about 60 ms.
    ///
    /// This used to be a setting, with Material 2 - the Windhawk mod's cubic-bezier(0.4, 0, 0.2, 1) -
    /// as the alternative. The choice is gone: one curve is a decision rather than a preference, and the
    /// menu entry existed only to offer the other one.
    /// </summary>
    private static double Ease(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;

        const double splitX = 0.166666;
        bool second = t > splitX;
        double lo3 = 0, hi3 = 1, u3 = 0.5;
        for (int i = 0; i < 30; i++)
        {
            u3 = (lo3 + hi3) / 2;
            double x3 = second
                ? Cubic(splitX, 0.208333, 0.25, 1.0, u3)
                : Cubic(0.0, 0.05, 0.133333, splitX, u3);
            if (x3 < t) lo3 = u3; else hi3 = u3;
        }
        return second ? Cubic(0.4, 0.82, 1.0, 1.0, u3)
                      : Cubic(0.0, 0.0, 0.06, 0.4, u3);
    }

    private static double Cubic(double p0, double p1, double p2, double p3, double u)
    {
        double v = 1 - u;
        return p0 * v * v * v + 3 * p1 * v * v * u + 3 * p2 * v * u * u + p3 * u * u * u;
    }
}

/// <summary>Our own topmost per-pixel-alpha window. The real window is hidden behind it.</summary>
internal sealed class Panel
{
    private const string ClassName = "MoaPanelClass";
    private static bool _registered;
    private static Native.WndProcDelegate? _proc;

    private IntPtr _hwnd;
    private Bitmap? _iconBitmap;

    /// <summary>
    /// The icon pre-scaled to the size it is drawn at, once per animation.
    ///
    /// The cache holds whatever the executable really contains, which after switching to
    /// PrivateExtractIcons is usually 256 by 256 - and a high quality reduction of that on every
    /// frame is far more work than the legacy 32 pixel icon used to be, which is exactly the kind of
    /// cost that shows up as a frame rate that no longer reaches the display's. Doing it once, here,
    /// leaves the frame doing a copy.
    /// </summary>
    private Bitmap? _iconDraw;
    private int _iconMaxPx = 128;

    // The bitmap and device context of the last card drawn, kept so that a frame which only changes the
    // opacity does not have to draw it again. Freed in ReleaseSurface, which Destroy calls.
    private IntPtr _dib = IntPtr.Zero, _memDc = IntPtr.Zero, _oldBitmap = IntPtr.Zero;
    /// <summary>
    /// The surface's pixels and its width in pixels. The width is the stride, and it is the *frame's* width
    /// rather than the card's: the card is a rectangle inside the frame, so a row of the surface is longer
    /// than a row of the card and every pass over it has to be told which rectangle it is working on.
    /// </summary>
    private IntPtr _bits;
    private int _stride;
    private bool _surfaceReady;
    private int _drawnW, _drawnH, _drawnRadius;
    private Color _bg;
    private Color _borderColor;
    private bool _hasBorder;
    private int _frames;

    // ---- the frame of reference the card is drawn in ------------------------------------------------
    //
    // The surface is allocated once, at the size of the rectangle the animation's frames can occupy, and
    // every frame is drawn into it in the same coordinate system: the card moves and grows *inside* that
    // frame, and UpdateLayeredWindow is handed the part of it the current card occupies.
    //
    // That is what makes a frame cheap. Drawing the card from scratch costs its whole area - a GDI+ fill, a
    // premultiply pass and an upload of the same pixels - and the area is at its largest exactly when the
    // animation covers most of the screen. With the frame fixed, the pixels drawn last frame are still
    // there and still correct, so only the ring between last frame's card and this one has changed, and the
    // work of a frame follows the perimeter instead of the area. It is what the Windhawk mod does: its
    // render surface is the final size at 1:1 while each frame fills only fw by fh from the top left, and
    // it remembers drawnW/drawnH/drawnRadius/drawnIcon to know what has to be filled again.
    //
    // Screen coordinates, because that is the space the frames arrive in; a frame's own rectangle is
    // derived from them. Everything outside the frame would be clipped away.
    private int _frameX, _frameY, _frameW, _frameH;
    private int _drawnX, _drawnY;      // where the last card actually was, in screen coordinates
    private bool _painted;             // whether the surface holds a card at all

    /// <summary>
    /// The cap on a frame's own memory, now against the frame rather than the card. It was four megapixels,
    /// which is smaller than 4K, so on a 4K display a maximised window drew no panel at all and the
    /// animation silently did not appear; past this one a frame would be more than a hundred megabytes and
    /// refusing is the lesser evil, but it says so instead of failing mute.
    /// </summary>
    private const long MaxFramePixels = 33_000_000;

    public static void RegisterClass()
    {
        if (_registered) return;
        _proc = (h, m, w, l) => Native.DefWindowProcW(h, m, w, l);
        var inst = Native.GetModuleHandleW(null);
        var wc = new WNDCLASSEXW
        {
            CbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            LpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            HInstance = inst,
            LpszClassName = ClassName,
        };
        _registered = Native.RegisterClassExW(ref wc) != 0;
    }

    /// <summary>
    /// Set when Create could not make the window. Create runs on the animation thread, so its return
    /// value has nowhere useful to go - the flag is how the message loop finds out, and Tick then
    /// releases the window through the normal path.
    /// </summary>
    public volatile bool Failed;

    public bool Create(WindowInfo target, int startSize, int startRadius, int anchorX, int anchorY,
                       int endX, int endY, int endSize)
    {
        if (!_registered) { Failed = true; return false; }

        // The frame of reference for the whole animation: the union of where it starts and where it ends.
        // An opening card travels from the start square to the window's rectangle and a closing one from
        // the window's rectangle to the square it came from, so the two ends bound every frame in between
        // and a surface of that size can be allocated once and kept.
        _frameX = Math.Min(anchorX, target.X);
        _frameY = Math.Min(anchorY, target.Y);
        int right = Math.Max(anchorX + startSize, target.X + target.Width);
        int bottom = Math.Max(anchorY + startSize, target.Y + target.Height);
        if (endSize > 0)
        {
            _frameX = Math.Min(_frameX, endX);
            _frameY = Math.Min(_frameY, endY);
            right = Math.Max(right, endX + endSize);
            bottom = Math.Max(bottom, endY + endSize);
        }
        _frameW = right - _frameX;
        _frameH = bottom - _frameY;

        // A small margin, because everything derived from a card is a pixel or two larger than its
        // rectangle: the anti-aliased edge of every shape, and the regions built from those rectangles,
        // which cannot express half a pixel. Without it a frame that fits the geometry exactly still fails
        // the fit test below and is grown by that one pixel.
        const int FrameMargin = 4;
        _frameX -= FrameMargin;
        _frameY -= FrameMargin;
        _frameW += FrameMargin * 2;
        _frameH += FrameMargin * 2;

        if ((long)_frameW * _frameH > MaxFramePixels)
        {
            Log.Write($"panel refused: the animation's frame is {_frameW}x{_frameH}, past the " +
                      $"{MaxFramePixels / 1_000_000} MP cap");
            Failed = true;
            return false;
        }

        ResolveColorsCached(out _bg, out _borderColor, out _hasBorder);

        if (!string.IsNullOrEmpty(target.ExePath)) _iconBitmap = CachedIcon(target.ExePath);

        // The icon is drawn at a fixed size that should look the same on any display, which means it
        // has to follow the display's scaling. It was a hardcoded 128 pixels, so on a 200% display the
        // icon came out half the size it should be next to everything else - the same mistake as the
        // one the animation's start size had.
        int iconDpi = (int)Native.GetDpiForWindow(target.Hwnd);
        if (iconDpi < 48 || iconDpi > 480) iconDpi = 96;
        _iconMaxPx = Native.MulDiv(128, iconDpi, 96);
        // The border follows the display too, and is resolved here rather than per frame: it is one of the
        // few things in the card that is measured in pixels rather than derived from the card's size.
        _borderWidthPx = Math.Max(1f, BorderWidthLogical * iconDpi / 96f);

        if (_iconBitmap is not null && _iconMaxPx > 0)
        {
            try
            {
                var scaled = new Bitmap(_iconMaxPx, _iconMaxPx,
                                        System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var gs = Graphics.FromImage(scaled))
                {
                    gs.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    gs.DrawImage(_iconBitmap, new Rectangle(0, 0, _iconMaxPx, _iconMaxPx));
                }
                _iconDraw = scaled;
            }
            catch { _iconDraw = null; }   // the unscaled icon is still usable
        }

        uint ex = Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT;
        _hwnd = Native.CreateWindowExW(ex, ClassName, "", Native.WS_POPUP,
                                       anchorX, anchorY, startSize, startSize,
                                       IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandleW(null), IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) { Failed = true; return false; }
        Native.SetWindowPos(_hwnd, (IntPtr)(-1) /* HWND_TOPMOST */, 0, 0, 0, 0,
                            0x0001 | 0x0002 | 0x0010);  // NOSIZE | NOMOVE | NOACTIVATE
        // UpdateLayeredWindow only updates content: it does not make the window visible.
        // This is the line the Windhawk mod has (ShowWindow(slot->splash, SW_SHOWNOACTIVATE))
        // and exactly the one this reimplementation was missing.
        Native.ShowWindow(_hwnd, Native.SW_SHOWNOACTIVATE);
        Log.Write($"panel 0x{_hwnd.ToInt64():X} shown: visible={Native.IsWindowVisible(_hwnd)}");
        // Frame 1 is drawn fully transparent on purpose: the panel window exists and is
        // "shown" from the start, but nothing is visible until the target window actually
        // appears. That is what keeps a never-shown helper window (the NVIDIA control panel
        // creates one) from leaving a panel sitting over empty space.
        Show(anchorX, anchorY, startSize, startSize, 0, startRadius);
        return true;
    }

    public void Destroy()
    {
        if (_hwnd != IntPtr.Zero) { Native.DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
        // The icon bitmap is shared and cached per executable, so it is deliberately not disposed
        // here. See CachedIcon.
        _iconBitmap = null;
        _iconDraw?.Dispose();
        _iconDraw = null;
        ReleaseSurface();
    }

    // Reading an icon out of the executable on disk and converting it is work that must not happen per
    // frame: one bitmap per executable the user opens windows from, not one per animation, and immutable
    // once made.
    private static readonly Dictionary<string, Bitmap?> IconCache = new();

    /// <summary>
    /// The application's icon as a bitmap, from the cache when it has been needed before.
    ///
    /// ToBitmap rather than GDI+'s DrawIcon, which does not write alpha for an icon's opaque pixels
    /// when the target DIB was cleared to transparent: those pixels stay at alpha 0, the premultiply
    /// then zeroes them and the icon comes out hollow. Only icons using the old-style AND mask hit
    /// this - 7-Zip's does - which made it look like a rule about 7-Zip rather than a drawing bug.
    /// </summary>
    private static Bitmap? CachedIcon(string exePath)
    {
        lock (IconCache)
        {
            if (IconCache.TryGetValue(exePath, out var cached)) return cached;

            Bitmap? made = LoadIconBitmap(exePath);

            // Sixty-four entries, because a 256 by 256 icon is a quarter of a megabyte each.
            if (IconCache.Count > 64) IconCache.Clear();
            IconCache[exePath] = made;
            return made;
        }
    }

    /// <summary>
    /// The largest icon the executable contains, as a bitmap, and the size it turned out to be.
    ///
    /// Worth logging, that size: it says whether this program is getting a proper icon out of the file
    /// or the legacy 32 by 32, which is the whole difference between a sharp animation and a soft one.
    /// Measured, the answer is 256 by 256 for anything modern, which is why no resampling is done.
    /// </summary>
    private static Bitmap? LoadIconBitmap(string exePath)
    {
        IntPtr handle = ExtractLargest(exePath);
        if (handle == IntPtr.Zero) return null;
        try
        {
            using var icon = Icon.FromHandle(handle);
            var made = icon.ToBitmap();
            Log.Write($"icon {System.IO.Path.GetFileName(exePath)}: {made.Width}x{made.Height}");
            return made;
        }
        catch { return null; }
        finally { Native.DestroyIcon(handle); }
    }

    /// <summary>
    /// One icon handle - the largest available - which the caller owns and has to destroy.
    ///
    /// PrivateExtractIcons asks for a size and returns whatever the file really holds, which for
    /// anything modern is 256 by 256. ExtractIconEx only ever returns the legacy 32 and 16, and is kept
    /// as the fallback for files the first one returns nothing for.
    /// </summary>
    private static IntPtr ExtractLargest(string exePath)
    {
        try
        {
            var got = new IntPtr[1];
            var ids = new uint[1];
            if (Native.PrivateExtractIconsW(exePath, 0, 256, 256, got, ids, 1, 0) > 0 &&
                got[0] != IntPtr.Zero)
            {
                return got[0];
            }
        }
        catch { /* fall through to the legacy sizes */ }

        IntPtr large = IntPtr.Zero, small = IntPtr.Zero;
        try
        {
            Native.ExtractIconExW(exePath, 0, out large, out small, 1);
            IntPtr use = large != IntPtr.Zero ? large : small;
            if (large != IntPtr.Zero && large != use) Native.DestroyIcon(large);
            if (small != IntPtr.Zero && small != use) Native.DestroyIcon(small);
            return use;
        }
        catch { return IntPtr.Zero; }
    }

    private static Color _cachedBg, _cachedBorder;
    private static bool _cachedHasBorder;
    private static long _colorsAt;

    /// <summary>
    /// The panel colours, resolved at most every couple of seconds. ResolveColors reads the registry,
    /// and it runs at the moment a window has just closed and the panel has to be on screen before
    /// the eye notices anything missing. The short lifetime is deliberate: switching the system theme
    /// still has to be picked up, and a second or two of staleness is not something anyone can see.
    /// </summary>
    private static void ResolveColorsCached(out Color background, out Color border, out bool hasBorder)
    {
        lock (IconCache)
        {
            long now = Compat.TickCount64;
            if (_colorsAt == 0 || now - _colorsAt > 2000)
            {
                ResolveColors(out _cachedBg, out _cachedBorder, out _cachedHasBorder);
                _colorsAt = now;
            }
            background = _cachedBg;
            border = _cachedBorder;
            hasBorder = _cachedHasBorder;
        }
    }

    /// <summary>Renders one frame: a rounded card carrying the app icon, at the given rect.</summary>
    public void Show(int x, int y, int w, int h, int alpha, int radius)
    {
        if (_hwnd == IntPtr.Zero || w <= 0 || h <= 0) return;

        if (_surfaceReady && (long)_frameW * _frameH > MaxFramePixels)
        {
            Log.Write($"panel frame refused: {_frameW}x{_frameH} is past the {MaxFramePixels / 1_000_000} MP cap");
            return;
        }

        // The same card, drawn again only to change how opaque it is, is not drawn again at all.
        //
        // The fade phases do exactly that: the opening animation spends 160 ms fading a panel whose
        // rectangle is already fixed, and the closing animation ends the same way. Measured, a frame
        // over three to four megapixels costs 8.6 ms of drawing and another 8.8 ms of waiting, and the
        // ones that only change the opacity paid all of it - so this is where the largest remaining
        // saving is, and it saves both halves: a frame not drawn is also a frame the compositor is not
        // handed.
        //
        // Position is part of the test now, where it used not to be. The surface is the whole frame the
        // animation can occupy rather than the card itself, so a card that has only moved has moved
        // *within* the surface and its pixels are in different places. Nothing else has to agree: the
        // colours are resolved once per panel and the icon is pre-scaled once per panel.
        if (_surfaceReady && x == _drawnX && y == _drawnY &&
            w == _drawnW && h == _drawnH && radius == _drawnRadius)
        {
            Present(x, y, w, h, alpha);
            return;
        }

        // Stage timing, taken only while debug mode is on. A frame *used* to cost about five nanoseconds per
        // pixel, measured, which put the area budget for a 165 Hz display at roughly one megapixel and made
        // anything larger fall short of the refresh rate in proportion to its area. That is now the cost of
        // a full frame only - the first of a panel, or the first after a resize - because the card is drawn
        // again just where it has changed since the last frame. This says which stage the time goes to, so
        // that the next change can be aimed rather than guessed at. The stages a reused frame skips stay at
        // zero, and those are only filled in while the numbers are being watched. tUpdate and the formatting
        // helper live in Present, which is where the frame is handed over.
        long tStart = 0, tAlloc = 0, tDraw = 0, tPremul = 0;
        if (Log.On) tStart = System.Diagnostics.Stopwatch.GetTimestamp();

        if (!_surfaceReady)
        {
            ReleaseSurface();
            if (!MakeSurface()) return;
        }
        else if (!InFrame(x, y, w, h))
        {
            // A card outside the frame the animation was given: the window was resized between its create
            // event and the handoff, or the geometry was not the two-ended interpolation it looked like.
            // The frame grows to hold it and what is already on the surface is copied over, which is the
            // cost of being right about a case the up-front estimate cannot know about.
            GrowFrame(x, y, w, h);
            if (Failed) return;
        }
        if (Log.On) tAlloc = System.Diagnostics.Stopwatch.GetTimestamp();

        // What has to be drawn again: the card as it is now, minus what is already right. With nothing on
        // the surface yet that is the whole of it, which is how the first frame of a panel asks for
        // everything and why the first frame still costs what all of them used to.
        var card = new Rectangle(x - _frameX, y - _frameY, w, h);
        using var dirty = new Region(Grow(card));
        if (_painted)
        {
            var was = new Rectangle(_drawnX - _frameX, _drawnY - _frameY, _drawnW, _drawnH);
            // The symmetric difference of the two cards - the ring between them, either way round - plus
            // the two things the ring cannot contain. The old corners are where a pixel that was rounded
            // away is still rounded away; the old border sits just inside the old card, which the new card
            // covers without ever painting over it. Every rectangle goes in with a pixel of margin, because
            // a region cannot express an anti-aliased edge and the outermost pixels would be left behind.
            dirty.Xor(Grow(was));
            dirty.Union(RectArray(CornerAreas(was, _drawnRadius)));
            dirty.Union(RectArray(EdgeBands(was)));
            UnionIcon(dirty, was);
        }
        // ... and the same two things about the card being drawn. A card that is *shrinking* has its own
        // corners and its own border well inside the card it came from, so the ring between the two does not
        // reach them: they would keep the bigger card's pixels, which is a square corner drawn over a round
        // one and a border that never appears. For a growing card both are already inside the ring and this
        // costs a few thin bands and four small squares.
        dirty.Union(RectArray(CornerAreas(card, radius)));
        dirty.Union(RectArray(EdgeBands(card)));
        UnionIcon(dirty, card);        // the icon this frame draws, which may have moved or grown

        var scans = dirty.GetRegionScans(new Matrix());
        Zero(scans);
        using (var g = Graphics.FromHdc(_memDc))
        {
            g.SetClip(dirty, CombineMode.Replace);
            DrawCard(g, card, radius);
        }
        if (Log.On) tDraw = System.Diagnostics.Stopwatch.GetTimestamp();

        Premultiply(scans);
        if (Log.On) tPremul = System.Diagnostics.Stopwatch.GetTimestamp();

        _painted = true;
        _drawnX = x; _drawnY = y; _drawnW = w; _drawnH = h; _drawnRadius = radius;
        Present(x, y, w, h, alpha, tStart, tAlloc, tDraw, tPremul);

        if (_frames < 3)
        {
            _frames++;
            Log.Write($"panel frame {_frames}: rect {x},{y} {w}x{h} radius={radius} alpha={alpha}");
        }
    }

    /// <summary>
    /// Allocates the surface: one DIB the size of the animation's frame, with a device context that stays
    /// selected for the panel's life, so that a frame only ever draws into it. Returns false when it cannot
    /// be made, which leaves the panel with nothing to show - the same as any other creation failure.
    ///
    /// It does not release what is already there: GrowFrame has to copy the old surface first, and that is
    /// the caller's business. ReleaseSurface does it for everyone else.
    /// </summary>
    private bool MakeSurface()
    {
        var bmi = new Native.BITMAPINFO
        {
            Header = new Native.BITMAPINFOHEADER
            {
                BiSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                BiWidth = _frameW,
                BiHeight = -_frameH,               // top-down
                BiPlanes = 1,
                BiBitCount = 32,
                BiCompression = 0,                 // BI_RGB
            }
        };
        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return false;
        IntPtr dib = Native.CreateDIBSection(screenDc, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        Native.ReleaseDC(IntPtr.Zero, screenDc);
        if (dib == IntPtr.Zero || bits == IntPtr.Zero)
        {
            if (dib != IntPtr.Zero) Native.DeleteObject(dib);
            return false;
        }
        IntPtr dc = Native.CreateCompatibleDC(IntPtr.Zero);
        _dib = dib; _memDc = dc; _oldBitmap = Native.SelectObject(dc, dib);
        _bits = bits; _stride = _frameW;
        _surfaceReady = true;
        return true;
    }

    /// <summary>
    /// Draws the card into the surface, in the frame's coordinates: the rounded background, its border and
    /// the icon. Exactly the drawing this always was - what changed is that the caller hands it a frame that
    /// is mostly already correct and clips it to the part that is not.
    /// </summary>
    private void DrawCard(Graphics g, Rectangle card, int radius)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int r = Compat.Clamp(radius, 0, Math.Min(card.Width, card.Height) / 2);
        // The border is inset by half its own width, because GDI+ draws a path centred on the line: at the
        // very edge, half the stroke falls outside the card and is cut off. A one-pixel border therefore
        // showed as half a pixel of anti-aliased line - which is why the accent border could not be seen.
        float borderWidth = _hasBorder ? _borderWidthPx : 0f;
        int inset = (int)Math.Ceiling(borderWidth / 2f);
        using var path = RoundedRect(card.X + inset, card.Y + inset,
                                     card.Width - inset * 2, card.Height - inset * 2, r);
        using var brush = new SolidBrush(_bg);
        g.FillPath(brush, path);
        if (_hasBorder)
        {
            using var pen = new Pen(_borderColor, borderWidth);
            g.DrawPath(pen, path);
        }
        var icon = _iconDraw ?? _iconBitmap;
        if (icon is null) return;
        int side = Math.Min(Math.Min(card.Width, card.Height) / 2, _iconMaxPx);
        int ix = card.X + (card.Width - side) / 2, iy = card.Y + (card.Height - side) / 2;
        // The pre-scaled icon is exactly the size this is drawn at whenever the card is big enough for the
        // full size, which is most of the animation: there it is a straight copy and no resampling happens
        // at all. Smaller than that only while the card is still growing, where the reduction is from the
        // pre-scaled size and therefore cheap.
        //
        // There is no DrawIcon fallback. It was the path that produced hollow icons - it does not write
        // alpha for an AND-mask icon's opaque pixels into a DIB cleared to transparent - and there is
        // nothing left for it to fall back to, since the bitmap is what gets cached.
        g.InterpolationMode = side == icon.Width
            ? InterpolationMode.NearestNeighbor
            : InterpolationMode.HighQualityBicubic;
        g.DrawImage(icon, new Rectangle(ix, iy, side, side));
    }

    /// <summary>
    /// The four corner squares of a card, which is where its rounding is. The ring between two cards of
    /// different size does not reach them, and a pixel that was rounded away there has to be drawn again.
    /// </summary>
    private static Rectangle[] CornerAreas(Rectangle card, int radius)
    {
        int side = Math.Min(card.Width, card.Height);
        int r = Math.Min(Compat.Clamp(radius, 0, side / 2) + 1, side);   // +1: the anti-aliased edge
        return new[]
        {
            new Rectangle(card.Left, card.Top, r, r),
            new Rectangle(card.Right - r, card.Top, r, r),
            new Rectangle(card.Left, card.Bottom - r, r, r),
            new Rectangle(card.Right - r, card.Bottom - r, r, r),
        };
    }

    /// <summary>
    /// A band around a card's whole edge, thick enough to cover its border and the pixels the border was
    /// anti-aliased into. The border is drawn inside the card, so the ring between two cards does not cover
    /// it and it would otherwise still be showing through the new background.
    /// </summary>
    private Rectangle[] EdgeBands(Rectangle card)
    {
        int t = Math.Min((int)Math.Ceiling(_borderWidthPx) + 1, Math.Min(card.Width, card.Height));
        return new[]
        {
            new Rectangle(card.Left, card.Top, card.Width, t),
            new Rectangle(card.Left, card.Bottom - t, card.Width, t),
            new Rectangle(card.Left, card.Top, t, card.Height),
            new Rectangle(card.Right - t, card.Top, t, card.Height),
        };
    }

    /// <summary>
    /// Whether a card fits inside the frame with a pixel to spare, so that the anti-aliased edge and the
    /// region's own rounding cannot fall outside it.
    /// </summary>
    private bool InFrame(int x, int y, int w, int h)
        => x - 1 >= _frameX && y - 1 >= _frameY &&
           x + w + 1 <= _frameX + _frameW && y + h + 1 <= _frameY + _frameH;

    /// <summary>
    /// Grows the frame to hold a card that does not fit, keeping what has already been drawn.
    ///
    /// This is the fallback for the estimate Create makes not holding - a window resized between its create
    /// event and the handoff, say. It costs a copy of the old frame and a new allocation, which is what a
    /// frame used to cost every time, and it says so in the log: if it is happening often, the estimate is
    /// wrong rather than the case being unusual.
    /// </summary>
    private void GrowFrame(int x, int y, int w, int h)
    {
        int left = Math.Min(_frameX, x - 1), top = Math.Min(_frameY, y - 1);
        int right = Math.Max(_frameX + _frameW, x + w + 1), bottom = Math.Max(_frameY + _frameH, y + h + 1);
        // Slack, because the case this exists for does not happen once. The measured one was a window that
        // sizes itself after it is created: the frame it was given was 1953x703 and the card it grew into
        // was 1506x856, so it was grown again on the next frame and on the frame after that, a few pixels at
        // a time, and each grow copies the whole surface - 434 of them in one session, which is most of what
        // the incremental drawing was saving. Slack in the frame costs nothing else, because the compositor
        // is handed the card and never the frame.
        int padX = Math.Max(64, (right - left) / 4), padY = Math.Max(64, (bottom - top) / 4);
        left -= padX; right += padX; top -= padY; bottom += padY;
        int nw = right - left, nh = bottom - top;
        if ((long)nw * nh > MaxFramePixels)
        {
            Log.Write($"panel frame would have to grow to {nw}x{nh}, past the " +
                      $"{MaxFramePixels / 1_000_000} MP cap; not drawn");
            Failed = true;
            return;
        }

        IntPtr oldDib = _dib, oldDc = _memDc, oldSelected = _oldBitmap;
        IntPtr oldBits = _bits;
        int oldStride = _stride, oldW = _frameW, oldH = _frameH;
        int dx = _frameX - left, dy = _frameY - top;

        Log.Write($"panel frame grew from {_frameW}x{_frameH} to {nw}x{nh}" +
                  $" for a {w}x{h} card at {x},{y}; the card was outside the frame it was given");

        _frameX = left; _frameY = top; _frameW = nw; _frameH = nh;
        _dib = IntPtr.Zero; _memDc = IntPtr.Zero; _oldBitmap = IntPtr.Zero; _bits = IntPtr.Zero;
        _surfaceReady = false;
        if (!MakeSurface())
        {
            FreeSurface(oldDib, oldDc, oldSelected);
            Failed = true;
            return;
        }

        // The old pixels are premultiplied ARGB and so are the new ones, so this is a straight copy of the
        // rows that had been drawn - everything outside them is untouched memory that nothing will read,
        // because every pixel the compositor is handed comes from the card's own rectangle.
        if (oldBits != IntPtr.Zero)
        {
            unsafe
            {
                var src = (byte*)oldBits;
                var dst = (byte*)_bits;
                for (int row = 0; row < oldH; row++)
                    Buffer.MemoryCopy(src + (long)row * oldStride * 4,
                                      dst + ((long)(row + dy) * _stride + dx) * 4,
                                      (long)nw * 4, (long)oldW * 4);
            }
        }
        FreeSurface(oldDib, oldDc, oldSelected);
    }

    private static void FreeSurface(IntPtr dib, IntPtr dc, IntPtr selected)
    {
        if (dc != IntPtr.Zero)
        {
            if (selected != IntPtr.Zero) Native.SelectObject(dc, selected);
            Native.DeleteDC(dc);
        }
        if (dib != IntPtr.Zero) Native.DeleteObject(dib);
    }

    /// <summary>
    /// A rectangle with a pixel of margin. A region is a set of whole pixels and cannot express an
    /// anti-aliased edge, so every rectangle that goes into one goes in slightly larger than the geometry
    /// it stands for - otherwise the outermost pixels of a previous frame's edge are never painted over.
    /// </summary>
    private static Rectangle Grow(Rectangle r)
        => new(r.X - 1, r.Y - 1, Math.Max(0, r.Width) + 2, Math.Max(0, r.Height) + 2);

    private static Region RectArray(Rectangle[] rects)
    {
        var region = new Region(Grow(rects[0]));
        for (int i = 1; i < rects.Length; i++) region.Union(Grow(rects[i]));
        return region;
    }

    /// <summary>
    /// Adds where the icon is drawn on a card, so that the previous frame's icon is painted over and this
    /// frame's is not clipped away.
    /// </summary>
    private void UnionIcon(Region into, Rectangle card)
    {
        var icon = _iconDraw ?? _iconBitmap;
        if (icon is null || card.Width <= 0 || card.Height <= 0) return;
        int side = Math.Min(Math.Min(card.Width, card.Height) / 2, _iconMaxPx);
        into.Union(Grow(new Rectangle(card.X + (card.Width - side) / 2,
                                      card.Y + (card.Height - side) / 2, side, side)));
    }

    /// <summary>
    /// Writes transparent over a set of rectangles. Clear() would do the whole frame, and only these pixels
    /// are wrong: the ones a card of some earlier size covered and this one does not.
    /// </summary>
    private unsafe void Zero(RectangleF[] rects)
    {
        var p = (uint*)_bits;
        foreach (var r in rects)
        {
            int x0 = Math.Max(0, (int)Math.Floor(r.Left)), x1 = Math.Min(_frameW, (int)Math.Ceiling(r.Right));
            int y0 = Math.Max(0, (int)Math.Floor(r.Top)), y1 = Math.Min(_frameH, (int)Math.Ceiling(r.Bottom));
            if (x1 <= x0) continue;
            for (int y = y0; y < y1; y++)
            {
                uint* row = p + (long)y * _stride + x0;
                for (int x = 0, n = x1 - x0; x < n; x++) row[x] = 0;
            }
        }
    }

    /// <summary>
    /// Hands the bitmap to the compositor at the given place, with the given opacity, without drawing
    /// anything. This is the whole cost of a frame that only changes the opacity.
    /// </summary>
    private void Present(int x, int y, int w, int h, int alpha,
                         long tStart = 0, long tAlloc = 0, long tDraw = 0, long tPremul = 0)
    {
        if (!_surfaceReady) return;

        var dst = new POINT { X = x, Y = y };
        var size = new SIZE { Cx = w, Cy = h };
        // The card is a rectangle inside the fixed frame, so the compositor is given that part of the
        // surface: the source offset moves with the card, the frame underneath does not move at all.
        var src = new POINT { X = x - _frameX, Y = y - _frameY };
        var blend = new BLENDFUNCTION
        {
            BlendOp = Native.AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = (byte)Compat.Clamp(alpha, 0, 255),
            AlphaFormat = Native.AC_SRC_ALPHA,
        };
        long tBefore = Log.On ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        Native.UpdateLayeredWindow(_hwnd, IntPtr.Zero, ref dst, ref size, _memDc, ref src, 0, ref blend,
                                   Native.ULW_ALPHA);
        // Only for panels big enough to be worth looking at: a small one is already inside the frame
        // budget, and logging every frame of every animation would bury these numbers. The size test
        // comes first so that the timestamp is not even read for the frames nobody watches.
        if (Log.On && (long)w * h > 400_000)
        {
            long tAfter = System.Diagnostics.Stopwatch.GetTimestamp();
            double Ms(long from, long to) => (to - from) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (tDraw == 0)
            {
                // Nothing was drawn: the same bitmap was handed over again to change only its opacity.
                // The stage timings belong to a drawn frame and are all zero here, which used to be
                // subtracted anyway and printed as tens of millions of milliseconds.
                Log.Write($"frame {w}x{h} ({w * (long)h / 1000} Kpx): reused," +
                          $" update={Ms(tBefore, tAfter):F2} ms alpha={alpha}");
            }
            else
            {
                Log.Write($"frame {w}x{h} ({w * (long)h / 1000} Kpx):" +
                          $" alloc+dc={Ms(tStart, tAlloc):F2} draw={Ms(tAlloc, tDraw):F2}" +
                          $" premul={Ms(tDraw, tPremul):F2} update={Ms(tPremul, tAfter):F2}" +
                          $" total={Ms(tStart, tAfter):F2} ms alpha={alpha}");
            }
        }
    }

    /// <summary>Frees the cached bitmap and device context, if there are any.</summary>
    private void ReleaseSurface()
    {
        FreeSurface(_dib, _memDc, _oldBitmap);
        _dib = IntPtr.Zero; _memDc = IntPtr.Zero; _oldBitmap = IntPtr.Zero;
        _bits = IntPtr.Zero; _stride = 0;
        _surfaceReady = false;
        // Nothing is on a surface that does not exist, so the next frame drawn is a full one.
        _painted = false;
        _drawnW = _drawnH = 0; _drawnRadius = 0;
    }

    /// <summary>
    /// UpdateLayeredWindow wants premultiplied ARGB; GDI+ writes straight alpha.
    ///
    /// Only the pixels a card was actually drawn into need it, which is the region the caller has just
    /// drawn - for a card that grew, the ring between it and the last one, not the card. Doing this a pixel
    /// at a time over the whole frame used to mean reading twelve megabytes that are not in cache at three
    /// or four megapixels: measured at 7.6 ms a frame, the largest single part of its cost and more than
    /// rendering the card and handing it to the compositor put together.
    ///
    /// The pixels are tested a pair at a time and whole pairs of finished pixels are stepped over. Only the
    /// test is vectorised: any pair that is not all-opaque goes through the same per-pixel code as before,
    /// which is what makes this unable to change the result for any pixel.
    /// </summary>
    private unsafe void Premultiply(RectangleF[] rects)
    {
        const ulong BothOpaque = 0xFF000000FF000000UL;
        var p = (uint*)_bits;
        foreach (var r in rects)
        {
            int x0 = Math.Max(0, (int)Math.Floor(r.Left)), x1 = Math.Min(_frameW, (int)Math.Ceiling(r.Right));
            int y0 = Math.Max(0, (int)Math.Floor(r.Top)), y1 = Math.Min(_frameH, (int)Math.Ceiling(r.Bottom));
            int n = x1 - x0;
            if (n <= 0) continue;
            for (int y = y0; y < y1; y++)
            {
                uint* row = p + (long)y * _stride + x0;
                int i = 0;
                for (; i <= n - 2; i += 2)
                {
                    if ((*(ulong*)(row + i) & BothOpaque) == BothOpaque) continue;
                    PremultiplyPixel(row, i);
                    PremultiplyPixel(row, i + 1);
                }
                // The remainder, which is at most one pixel.
                for (; i < n; i++) PremultiplyPixel(row, i);
            }
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static unsafe void PremultiplyPixel(uint* p, int i)
    {
        uint v = p[i];
        uint a = v >> 24;
        if (a == 255) return;
        if (a == 0) { p[i] = 0; return; }
        uint r = ((v >> 16) & 0xFF) * a / 255;
        uint g = ((v >> 8) & 0xFF) * a / 255;
        uint b = (v & 0xFF) * a / 255;
        p[i] = (a << 24) | (r << 16) | (g << 8) | b;
    }

    /// <summary>
    /// How thick the accent border is drawn, in logical pixels - two rather than one, because at one pixel
    /// the line lands on a half-pixel boundary and anti-aliasing spreads it across two rows at roughly 50%
    /// opacity each, which on a light card is barely visible. Two logical pixels is also what a window's own
    /// frame looks like at 100% scaling.
    ///
    /// Logical, and scaled by the window's DPI in Create: this was two *physical* pixels, so the border was
    /// a hairline on a 200% display and would have been a quarter of one at 400% - while everything it sits
    /// next to, the card's corner radius and the icon, was scaled and stayed the same size to the eye.
    /// </summary>
    private const float BorderWidthLogical = 2f;

    /// <summary>The border's width in the pixels this panel is drawn at, resolved once per panel.</summary>
    private float _borderWidthPx = BorderWidthLogical;

    private static GraphicsPath RoundedRect(int x, int y, int w, int h, int r)
    {
        var p = new GraphicsPath();
        if (r <= 0) { p.AddRectangle(new Rectangle(x, y, w, h)); return p; }
        p.AddArc(x, y, r * 2, r * 2, 180, 90);
        p.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
        p.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
        p.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>
    /// The panel's colours, following Settings > Personalization > Colors > "Show accent
    /// colour on the following surfaces". Auto-detected, never a setting:
    ///
    ///   * "Start, taskbar and action center" on  (Themes\Personalize\ColorPrevalence)
    ///     -> the panel itself takes the accent colour, exactly like those surfaces do;
    ///   * otherwise, if only "Title bars and window borders" is on (DWM\ColorPrevalence)
    ///     -> the panel keeps the black/white system colour and draws an accent-coloured
    ///        border, matching what happens to a window's frame in that mode;
    ///   * neither on -> plain black/white, as before.
    /// </summary>
    private static void ResolveColors(out Color background, out Color border, out bool hasBorder)
    {
        bool light = true;
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (k?.GetValue("AppsUseLightTheme") is int v) light = v != 0;
        }
        catch { /* no theme key: light, as a profile that has never chosen would be */ }

        background = light ? Color.FromArgb(255, 243, 243, 243) : Color.FromArgb(255, 32, 32, 32);
        border = background;
        hasBorder = false;

        if (AccentColor() is not Color accent) return;

        bool onStartAndTaskbar = Dword(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                                       "ColorPrevalence");
        bool onTitleBarsAndBorders = Dword(@"Software\Microsoft\Windows\DWM", "ColorPrevalence");

        if (onStartAndTaskbar)
        {
            background = accent;      // the whole panel, like the Start menu and taskbar
            border = accent;
        }
        else if (onTitleBarsAndBorders)
        {
            border = accent;          // only the frame, like a window border
            hasBorder = true;
        }
    }

    private static bool Dword(string path, string name)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path);
            return k?.GetValue(name) is int v && v != 0;
        }
        catch { return false; }   // no key: off, which is what an absent value means anyway
    }

    /// <summary>
    /// The current accent colour. Windows stores it as 0xAABBGGRR under DWM, cross-checked
    /// against ColorizationColor which holds the same colour as 0xAARRGGBB - the two agree
    /// once each is read in its own byte order, which is how the order here was confirmed.
    /// ColorizationColor is deliberately NOT a fallback: sharing one decoder with it would
    /// read it backwards and hand back the complement of the real accent.
    /// </summary>
    private static Color? AccentColor()
    {
        var sources = new (string path, string name)[]
        {
            (@"Software\Microsoft\Windows\DWM", "AccentColor"),
            (@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent", "AccentColorMenu"),
        };
        foreach (var (path, name) in sources)
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path);
                if (k?.GetValue(name) is int v)
                    return Color.FromArgb(255, v & 0xFF, (v >> 8) & 0xFF, (v >> 16) & 0xFF);
            }
            catch { /* no accent stored; the caller falls back */ }
        }
        return null;
    }
}
