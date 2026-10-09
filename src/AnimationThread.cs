// The animation thread
// ====================
//
// Two things forced this thread into existence.
//
// First, DwmFlush blocks until the compositor presents its next frame, which is how the animation
// follows the display's refresh rate. It cannot do that on the message loop: that is the thread
// where the window-created callback runs, and a stalled millisecond there is a window the user sees
// before it can be hidden. So the waiting and the drawing live here together: this thread waits on
// the compositor, draws the frame that waiting produced, and posts one tick per presented frame for
// the message loop to work out the next one from.
//
// Second, rendering a frame is not free - a GDI+ render, a premultiply pass and UpdateLayeredWindow
// per frame - and it used to happen on the message loop as well, delaying the next window-created
// event by a frame's worth of work. That is what this thread takes away: the message loop now only
// decides what the frame should look like, which is arithmetic and P/Invokes, and this thread draws
// it.
//
// A window belongs to the thread that created it
// ---------------------------------------------
// So the panel is created, drawn and destroyed here, and never touched from anywhere else - nothing
// in the type system enforces that, which is why the rest of the program talks to this class and
// not to Panel. This thread also pumps its own messages, because a layered window still receives
// them and a thread that only draws would never process them.
//
// The handshake is one frame deep on purpose
// ------------------------------------------
// The message loop sets the frame it wants; this thread draws the most recent one and forgets the
// rest. There is no queue of stale frames to catch up on, so a busy message loop shows fewer frames
// rather than a growing backlog of them.
//
// When there is no compositor
// ---------------------------
// DwmFlush returns immediately when nothing is composing - a remote session, or composition turned
// off. A loop on it would then spin a core at 100% for the length of the animation, so a call that
// returns in well under a frame is treated as that case and paced by a sleep instead.

using System.Collections.Generic;
using System.Threading;

namespace Moa;

internal sealed class AnimationThread
{
    /// <summary>
    /// A frame to draw the moment the panel exists, without waiting for a tick and then a flush.
    ///
    /// That wait is about one frame - 16 ms at 60 Hz, 6 ms at 165 Hz - and it is the seam the eye sees
    /// between a closing window disappearing and its panel appearing. Only a closing animation can hand
    /// one over, because only there is the first frame known before the panel exists: it is the
    /// rectangle the window had, at full opacity. An opening animation's first frame depends on the
    /// window becoming visible, so it has nothing to hand over.
    /// </summary>
    internal readonly struct FirstFrame
    {
        public FirstFrame(int x, int y, int w, int h, int alpha, int radius)
        { X = x; Y = y; W = w; H = h; Alpha = alpha; Radius = radius; }

        public readonly int X, Y, W, H, Alpha, Radius;
    }

    private sealed class Item
    {
        public required Panel Panel;
        public required WindowInfo Target;
        public int StartSize, StartRadius, AnchorX, AnchorY;

        /// <summary>
        /// The other end of the animation, when it is a square: the point a closing card collapses into.
        /// Zero size for an animation that ends at the window it is opening, which is the window's own
        /// rectangle and comes from Target. Panel.Create unions the two ends to size its surface, so this
        /// is what keeps that surface big enough for every frame the animation will ask for.
        /// </summary>
        public int FinalX, FinalY, FinalSize;

        /// <summary>Drawn as soon as the panel is created, if one was handed over.</summary>
        public FirstFrame? First;

        public bool Created;      // Create has been attempted
        public bool Release;      // destroy on the next pass, from any thread's point of view

        /// <summary>When Attach was called, so the first drawn frame can report how long the window
        /// spent with nothing in its place. That gap is what the eye reads as a seam between the
        /// window disappearing and the panel appearing.</summary>
        public long AttachedAt;
        public bool FirstFrameLogged;

        /// <summary>Frames drawn for this panel, so the rate actually achieved can be reported rather
        /// than felt. A figure below the display's is a hint rather than proof of a slow frame: the span
        /// it is measured over begins when the panel is attached, and for an opening animation that
        /// includes the wait for the window to become visible.</summary>
        public int Frames;

        /// <summary>
        /// Per animation, for the frame-interval question: how long the drawing itself took, how long
        /// was spent waiting between one drawn frame and the next, and how many gaps that covers.
        ///
        /// Measured per drawn frame rather than per loop iteration, which is what the earlier attempt
        /// got wrong: frames belong to a panel and iterations belong to the loop, so with two panels
        /// alive the two counts diverge and any ratio between them is meaningless.
        ///
        /// Updated only while debug mode is on, by the two places that add to them and read by the one
        /// place that reports them; nothing else touches them.
        /// </summary>
        public long ShowTicks, GapTicks, LastDrawEnd, DrawnPixels, AaPixels;
        public int Gaps;

        // The most recent frame asked for. One deep by design: see the note above.
        public bool Wanted;
        public int X, Y, W, H, Alpha, Radius;
    }

    private readonly object _gate = new();
    private readonly List<Item> _items = new();
    private readonly ManualResetEventSlim _wake = new(false);
    private Thread? _thread;
    private volatile bool _stop;
    private IntPtr _tickTarget;

    /// <summary>How long one frame of the display lasts. Asked of the display once; see DetectRefresh.</summary>
    private double _frameMs = 1000.0 / 60.0;

    /// <summary>
    /// One frame of the display, in milliseconds, as measured from the compositor's own cadence.
    ///
    /// Read by the animation to decide whether a window had been on screen long enough for the user to have
    /// seen it: at least one presented frame. That question is about the display, not about a number, and
    /// this is the same cadence the frames themselves are paced by, so the two cannot disagree.
    /// </summary>
    public double FrameMs => _frameMs;

    /// <summary>Whether the system timer resolution is currently raised, and has to be lowered again.</summary>
    private bool _timerRaised;

    /// <summary>
    /// How long a frame lasts, asked of the display rather than assumed.
    ///
    /// DwmFlush does the pacing and waits for the compositor, but on some systems it returns at once,
    /// and then the pacing falls to this loop. That used a fixed sleep of sixteen milliseconds - which
    /// is about 60 frames a second whatever the display can do, and it measured exactly that on a
    /// 165 Hz screen. The length now comes from the display, and timeBeginPeriod makes a sleep that
    /// short actually possible, since Windows otherwise only guarantees about 15.6 ms of it.
    /// </summary>
    private void DetectRefresh()
    {
        double hz = 0;
        try
        {
            IntPtr dc = Native.GetDC(IntPtr.Zero);
            if (dc != IntPtr.Zero)
            {
                int refresh = Native.GetDeviceCaps(dc, Native.VREFRESH);
                Native.ReleaseDC(IntPtr.Zero, dc);
                if (refresh >= 20 && refresh <= 500) hz = refresh;
            }
        }
        catch { /* falls back to 60 below */ }

        if (hz <= 0) hz = 60;
        _frameMs = 1000.0 / hz;
        Log.Write($"display reports {hz:F0} Hz; a frame is {_frameMs:F2} ms");
    }

    /// <summary>
    /// Starts the thread. <paramref name="tickTarget"/> receives WM_APP_TICK once per presented
    /// frame, which is how the message loop is asked to work out the next frame.
    /// </summary>
    public void Start(IntPtr tickTarget)
    {
        if (_thread is not null) return;
        _tickTarget = tickTarget;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Moa animation" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        _wake.Set();
        try
        {
            // A join that timed out or threw is worth saying out loud: the drain at the end of Loop is
            // what destroys the remaining panels, and a thread still running has not reached it.
            if (_thread is not null && !_thread.Join(2000))
                Log.Write("animation thread did not stop within 2 s; panels may not have been drained");
        }
        catch (Exception ex)
        {
            Log.Write($"animation thread join threw {ex.GetType().Name}: {ex.Message}");
        }
        _thread = null;
    }

    /// <summary>
    /// Registers a panel to be created on the animation thread. The Panel object is safe to hold on
    /// the calling thread; only its window operations belong to the other one.
    /// </summary>
    public Panel Attach(WindowInfo target, int startSize, int startRadius, int anchorX, int anchorY,
                        int finalX, int finalY, int finalSize, FirstFrame? first = null)
    {
        var panel = new Panel();
        lock (_gate)
        {
            _items.Add(new Item
            {
                Panel = panel,
                Target = target,
                StartSize = startSize,
                StartRadius = startRadius,
                AnchorX = anchorX,
                AnchorY = anchorY,
                FinalX = finalX,
                FinalY = finalY,
                FinalSize = finalSize,
                First = first,
                AttachedAt = System.Diagnostics.Stopwatch.GetTimestamp(),
            });
        }
        _wake.Set();
        return panel;
    }

    /// <summary>
    /// Hands a panel the frame it should be created with, if it has not been created yet, and says whether it
    /// took it.
    ///
    /// The closing animation knows its first frame when it is attached and passes it with the panel, which is
    /// what FirstFrame is for. An opening one only knows it when the window appears - and for a window that
    /// appears while the panel is still being made, that is before the panel exists. Measured on this machine,
    /// the first frame after the window appeared took a median of 21 ms, most of it this thread's own
    /// iteration and the panel's creation; taking the frame at creation is what removes that. False when the
    /// panel is already up, where the caller asks for the frame the usual way instead.
    /// </summary>
    public bool HandOverFirst(Panel panel, int x, int y, int w, int h, int alpha, int radius)
    {
        lock (_gate)
        {
            foreach (var it in _items)
            {
                if (it.Panel != panel) continue;
                if (it.Created) return false;
                it.First = new FirstFrame(x, y, w, h, alpha, radius);
                _wake.Set();
                return true;
            }
        }
        return false;
    }

    /// <summary>Asks for a frame. Overwrites any frame not yet drawn.</summary>
    public void RequestFrame(Panel panel, int x, int y, int w, int h, int alpha, int radius)
    {
        lock (_gate)
        {
            foreach (var it in _items)
            {
                if (it.Panel != panel) continue;
                it.X = x; it.Y = y; it.W = w; it.H = h; it.Alpha = alpha; it.Radius = radius;
                it.Wanted = true;
                break;
            }
        }
    }

    /// <summary>Asks for the panel to be destroyed on the animation thread.</summary>
    public void Release(Panel panel)
    {
        lock (_gate)
        {
            foreach (var it in _items) { if (it.Panel == panel) { it.Release = true; it.Wanted = false; break; } }
        }
        _wake.Set();
    }

    private void Loop()
    {
        DetectRefresh();

        var msg = default(MSG);
        // Counters for one batch of animation: from the first iteration with something to do until the
        // last. The number that matters is how many iterations found nothing to draw. This thread is
        // driven by the message loop, which is asked to work out the next frame by the tick posted at
        // the bottom of the loop - so if the loop goes round more often than frames arrive, most
        // iterations have nothing to do and the message loop is the limit, not the drawing. That is the
        // question the stage timings inside Show could not answer.
        //
        // Counted only while debug mode is on, which is what the tests below are for: they are the whole
        // cost of this instrumentation, and they are three branches around counters that nothing reads
        // when it is off.
        int iter = 0, drew = 0;
        long busyStart = 0;
        while (!_stop)
        {
            bool busy;
            lock (_gate) { busy = _items.Count > 0; }
            if (Log.On)
            {
                if (busy)
                {
                    if (iter == 0) busyStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    iter++;
                }
            }
            else if (iter != 0)
            {
                // Debug mode was switched off mid-batch. The counters start over rather than counting the
                // time it was off, which would otherwise turn the first line after it was switched back on
                // into a span covering minutes of doing nothing.
                iter = 0; drew = 0;
            }

            // Create the ones that are new, draw the ones with a frame waiting, destroy the rest.
            bool drewThis = false;
            List<Item>? snapshot = null;
            lock (_gate)
            {
                if (_items.Count > 0) snapshot = new List<Item>(_items);
            }
            if (snapshot is not null)
            {
                foreach (var it in snapshot)
                {
                    if (it.Release)
                    {
                        if (it.Frames > 1)
                        {
                            double ms = Compat.ElapsedMs(it.AttachedAt);
                            Log.Write($"panel drew {it.Frames} frames in {ms:F0} ms" +
                                      $" = {it.Frames * 1000.0 / ms:F1} fps");
                            // The two halves of a frame's interval, averaged over the animation. If the
                            // gap is much more than the display's own frame length - 6.06 ms at 165 Hz -
                            // then something between drawing one frame and being asked for the next is
                            // taking the time, and it is not the drawing.
                            if (Log.On && it.Gaps > 0)
                            {
                                double freq = System.Diagnostics.Stopwatch.Frequency;
                                Log.Write($"  of which: draw={(it.ShowTicks / freq * 1000.0 / it.Frames):F2} ms/frame," +
                                          $" gap={(it.GapTicks / freq * 1000.0 / it.Gaps):F2} ms/frame" +
                                          $" (display frame is {_frameMs:F2} ms)," +
                                          $" {it.DrawnPixels / 1000 / Math.Max(1, it.Frames)} Kpx/frame of which" +
                                          $" GDI+ {it.AaPixels / 1000 / Math.Max(1, it.Frames)} Kpx");
                            }
                        }
                        it.Panel.Destroy();
                        lock (_gate) { _items.Remove(it); }
                        continue;
                    }
                    if (!it.Created)
                    {
                        it.Panel.Create(it.Target, it.StartSize, it.StartRadius, it.AnchorX, it.AnchorY,
                                        it.FinalX, it.FinalY, it.FinalSize);
                        it.Created = true;
                        // A panel that could not be created is left Failed for Animator.Tick to
                        // notice, which releases the window through the normal path.
                        if (it.First is FirstFrame f && !it.Panel.Failed)
                        {
                            long tFirst0 = 0, tFirst1 = 0;
                            if (Log.On) tFirst0 = System.Diagnostics.Stopwatch.GetTimestamp();
                            it.Panel.Show(f.X, f.Y, f.W, f.H, f.Alpha, f.Radius);
                            it.Frames++; it.Wanted = false;
                            drewThis = true;
                            if (Log.On)
                            {
                                tFirst1 = System.Diagnostics.Stopwatch.GetTimestamp();
                                it.ShowTicks += tFirst1 - tFirst0;
                                it.DrawnPixels += it.Panel.LastPixels;
                                it.AaPixels += it.Panel.LastAaPixels;
                                if (it.LastDrawEnd != 0) { it.GapTicks += tFirst0 - it.LastDrawEnd; it.Gaps++; }
                                it.LastDrawEnd = tFirst1;
                            }
                            it.FirstFrameLogged = true;
                            // Showing a frame costs CreateDIBSection, a GDI+ render and a premultiply
                            // pass over the whole rectangle, so it scales with the window's area: 6 ms
                            // for a small window against 11.8 ms at 1855x1055. That raises the odds of
                            // missing the next vertical blank rather than being the reason one window
                            // gaps and another does not.
                            Log.Write($"panel first frame drawn at creation after {Compat.ElapsedMs(it.AttachedAt):F1} ms" +
                                      $" for {f.W}x{f.H}");
                        }
                        continue;
                    }
                    if (it.Wanted && !it.Panel.Failed)
                    {
                        long tDrawn0 = 0, tDrawn1 = 0;
                        if (Log.On) tDrawn0 = System.Diagnostics.Stopwatch.GetTimestamp();
                        it.Panel.Show(it.X, it.Y, it.W, it.H, it.Alpha, it.Radius);
                        it.Frames++; it.Wanted = false;
                        drewThis = true;
                        if (Log.On)
                        {
                            tDrawn1 = System.Diagnostics.Stopwatch.GetTimestamp();
                            it.ShowTicks += tDrawn1 - tDrawn0;
                            it.DrawnPixels += it.Panel.LastPixels;
                            it.AaPixels += it.Panel.LastAaPixels;
                            if (it.LastDrawEnd != 0) { it.GapTicks += tDrawn0 - it.LastDrawEnd; it.Gaps++; }
                            it.LastDrawEnd = tDrawn1;
                            drew++;
                        }
                        if (!it.FirstFrameLogged)
                        {
                            it.FirstFrameLogged = true;
                            Log.Write($"panel first frame after {Compat.ElapsedMs(it.AttachedAt):F1} ms" +
                                      $" (window class {it.Target.ClassName})");
                        }
                    }
                }
            }

            // Messages for our own windows, including the panel's.
            while (Native.PeekMessageW(out msg, IntPtr.Zero, 0, 0, Native.PM_REMOVE))
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessageW(ref msg);
            }

            if (!busy)
            {
                // Nothing to draw: block until there is. The wait has no timeout on purpose. Items
                // only ever appear through Attach, which sets this event, and a frame can only be
                // requested for an item that exists - so while this is waiting there is nothing that
                // could ask it to wake, and a timeout would only wake it ten times a second to do
                // nothing at all.
                if (Log.On && iter > 0)
                {
                    double span = (System.Diagnostics.Stopwatch.GetTimestamp() - busyStart)
                                  * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    Log.Write($"batch: {drew} frames over {iter} iterations in {span:F0} ms" +
                              $" ({drew * 1000.0 / Math.Max(1, span):F1} fps); iterations with" +
                              $" nothing to draw={iter - drew} ({(iter - drew) * 100.0 / iter:F0}%)");
                    iter = 0; drew = 0;
                }
                if (_timerRaised) { Native.TimeEndPeriod(1); _timerRaised = false; }
                _wake.Wait();
                _wake.Reset();
                continue;
            }
            // A finer timer only while something is animating: it costs power, and an idle program
            // should not be paying for it.
            if (!_timerRaised) { _timerRaised = true; Native.TimeBeginPeriod(1); }

            // Ask for the next frame BEFORE waiting for the display, so the message loop's own work
            // overlaps the wait for the next vertical blank instead of queueing behind it. Measured,
            // this did not shorten the first frame's latency: that stayed at about one frame, because
            // the first frame waits for a flush whatever the order. What removes that wait is handing
            // the first frame over with Attach, which is why FirstFrame exists.
            Native.PostMessageW(_tickTarget, Native.WM_APP_TICK, IntPtr.Zero, IntPtr.Zero);

            // The tick always goes out: it is what asks the message loop for the next frame, and the
            // animation cannot advance without it.
            Native.PostMessageW(_tickTarget, Native.WM_APP_TICK, IntPtr.Zero, IntPtr.Zero);

            // Waiting on the compositor, though, is what paces a frame - and there is no frame to pace
            // when this iteration drew nothing. That is the whole time a panel is waiting for its window
            // to appear or to paint, during which this loop was still going round about a hundred and
            // fifty times a second, waiting on the compositor each time and holding the system timer at
            // a millisecond resolution while it did. Measured: two and a half seconds of that in a
            // single batch, ninety-one per cent of its iterations drawing nothing. A short sleep keeps
            // what actually matters - noticing that the window has appeared - without the spinning.
            if (!drewThis)
            {
                // A whole display frame, not less. The point is to go round less often, and the wait
                // this replaces - the flush - was already about six milliseconds, so anything shorter
                // makes the spinning worse rather than better. Sixteen milliseconds is one frame at
                // 60 Hz and costs the opening animation at most that much before it notices its window
                // has appeared, which is the one thing this phase exists to notice; the closing
                // animation never waits here, since its first frame is drawn with the panel.
                Thread.Sleep(16);
                continue;
            }

            // Then pace to the display.
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int hr = Native.DwmFlush();
            double waited = Compat.ElapsedMs(t0);
            if (hr != 0 || waited < 1.0)
            {
                // DwmFlush did not wait for anything, so the pacing is this loop's job. The interval
                // comes from the display, less a millisecond for the sleep's own overshoot.
                int ms = (int)Math.Round(_frameMs) - 1;
                Thread.Sleep(ms < 0 ? 0 : ms);
            }
        }

        // Drain on the way out. Every panel must be destroyed by the thread that created it, so
        // Stop has to join this thread rather than destroy them from the caller.
        lock (_gate)
        {
            foreach (var it in _items) it.Panel.Destroy();
            _items.Clear();
        }
    }
}
