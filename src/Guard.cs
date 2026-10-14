// Hiding someone else's window is the dangerous part of this program, so it lives in one
// small class with three rules:
//
//     EVERY window we hide is restored, on EVERY exit path, exactly once.
//     NOTHING is ever handed back at alpha nought.
//     A hold that is not renewed expires on its own, and the sweep that enforces that does not
//     depend on the animation thread.
//
// The first rule is the one the Windhawk review caught the original mod on: an exit path that left a
// window at alpha 0, making it invisible for good. So the original style and alpha are recorded before
// anything is touched, the restore is idempotent, and it is called from the normal path, the deadline
// sweep, the unhandled-exception handler and shutdown.
//
// The second rule is what the first one was missing, and it is a bug that reached users. The alpha is
// not ours: it is a property the application sets for itself, and an application that creates its window
// transparent and fades it in owns it too. Handing back the value we *found* - which can be the nought of
// the first frame of such a fade, or a value from the middle of one - is what made windows invisible for
// good, because alpha nought is invisible *and* shown: Windows and the application both believe the
// window is on screen, so nothing - not the taskbar, not the application - ever writes that alpha again.
// The value handed back is therefore the one that is there now if the application has written its own,
// and otherwise the recorded one only when that was not nought, and full visibility when it was.
//
// probe/guard drives all of this against real windows.

namespace Moa;

internal sealed class HideGuard
{
    private sealed class Entry
    {
        public IntPtr Hwnd;
        public int OrigExStyle;
        public bool WasLayered;
        public byte OrigAlpha;
        public bool WeAddedLayered;
        public long HiddenAt;
        public long Deadline;
    }

    private readonly Dictionary<IntPtr, Entry> _hidden = new();
    private readonly object _lock = new();
    private readonly int _maxHideMs;

    public HideGuard(int maxHideMs) => _maxHideMs = Compat.Clamp(maxHideMs, 500, 60000);

    /// <summary>
    /// Hides the window and remembers exactly how to put it back.
    /// Returns false (and changes nothing) if it is not safe or not possible.
    /// </summary>
    public bool Hide(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return false;

        // A hung window would block the cross-thread style change. Cheap, non-blocking check.
        if (Native.IsHungAppWindow(hwnd)) return false;

        lock (_lock)
        {
            if (_hidden.ContainsKey(hwnd)) return false;

            int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
            bool wasLayered = (ex & Native.WS_EX_LAYERED) != 0;
            byte origAlpha = 255;
            if (wasLayered)
                Native.GetLayeredWindowAttributes(hwnd, out _, out origAlpha, out _);

            var e = new Entry
            {
                Hwnd = hwnd,
                OrigExStyle = ex,
                WasLayered = wasLayered,
                OrigAlpha = origAlpha,
                WeAddedLayered = false,
                HiddenAt = Compat.TickCount64,
                Deadline = Compat.TickCount64 + _maxHideMs,
            };

            // Adding the style first; if the window already had it we only drive the alpha.
            if (!wasLayered)
            {
                Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_LAYERED);
                if ((Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE) & Native.WS_EX_LAYERED) == 0)
                {
                    // The style did not take (the window went away, or the call failed).
                    // Put back whatever there was and report failure.
                    Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex);
                    return false;
                }
                e.WeAddedLayered = true;
            }

            if (!Native.SetLayeredWindowAttributes(hwnd, 0, 0, Native.LWA_ALPHA))
            {
                // Could not make it transparent - undo and report failure.
                if (e.WeAddedLayered)
                    Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, e.OrigExStyle);
                return false;
            }

            _hidden[hwnd] = e;
            return true;
        }
    }

    /// <summary>Restores one window. Safe to call more than once, and on a dead window.</summary>
    public void Release(IntPtr hwnd)
    {
        Entry? e;
        lock (_lock)
        {
            if (!_hidden.TryGetValue(hwnd, out e)) return;
            _hidden.Remove(hwnd);
        }
        Restore(e);
    }

    /// <summary>
    /// Moves one window's deadline out by <paramref name="ms"/>, which is what a caller that means to keep
    /// the window does every frame.
    ///
    /// The watchdog exists so that nothing can stay invisible for good, and its deadline is the same for every
    /// window because it is there to cover a hide that something forgot. This is the opposite case: a caller
    /// that knows it wants the window for longer - an advertisement covering it - says so, and says so again
    /// while it still wants it. That the hold has to be *renewed* is the point: a hold taken once for the
    /// advertisement's whole length is six minutes in which a card that died, hung or crashed without
    /// releasing leaves a transparent window behind it, and nothing but the deadline would ever put that
    /// window back. Renewed, the same death gives the window back a second after the renewals stop. See
    /// Animator.KeepHeldWindowHidden, which renews it from the tick, and HoldRenewMs.
    /// </summary>
    public void Hold(IntPtr hwnd, int ms)
    {
        lock (_lock)
        {
            if (!_hidden.TryGetValue(hwnd, out var e)) return;
            e.Deadline = Compat.TickCount64 + Compat.Clamp(ms, 500, 600000);
        }
    }

    /// <summary>
    /// Re-applies the hide if the application has undone it.
    ///
    /// The hide is applied at window creation, but a launching app carries on setting its own
    /// window state for a while afterwards - it can strip WS_EX_LAYERED or drive the alpha
    /// itself - and the log's "-> hidden" only ever proves the hide held at one instant. The
    /// Windhawk mod never meets this because it hides at the moment ShowWindow is called, so
    /// nothing comes after it.
    ///
    /// Costs two reads and only writes when something actually changed. Returns true when it
    /// had to intervene, which is also how we learn whether this is what was happening.
    /// </summary>
    public bool Reassert(IntPtr hwnd)
    {
        lock (_lock) { if (!_hidden.ContainsKey(hwnd)) return false; }
        if (!Native.IsWindow(hwnd) || Native.IsHungAppWindow(hwnd)) return false;

        int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
        if ((ex & Native.WS_EX_LAYERED) == 0)
        {
            // The app replaced the extended style and took our layering with it.
            Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_LAYERED);
            Native.SetLayeredWindowAttributes(hwnd, 0, 0, Native.LWA_ALPHA);
            return true;
        }
        if (Native.GetLayeredWindowAttributes(hwnd, out _, out byte a, out _) && a != 0)
        {
            Native.SetLayeredWindowAttributes(hwnd, 0, 0, Native.LWA_ALPHA);
            return true;
        }
        return false;
    }

    /// <summary>Watchdog sweep: anything past its deadline is put back.</summary>
    public int ReleaseExpired()
    {
        List<Entry> due;
        lock (_lock)
        {
            long now = Compat.TickCount64;
            due = _hidden.Values.Where(x => now >= x.Deadline).ToList();
        }

        int done = 0;
        foreach (var e in due)
        {
            // An entry is removed only once its window has actually been restored. Removing them all up
            // front, which this used to do, meant that a window which could not be touched - one that was
            // hung - was dropped from the table anyway: this sweep had forgotten it and no later sweep
            // would look again, so it stayed invisible for good. That is the single failure this class
            // exists to make impossible, and the comment in Restore was describing a retry that could
            // never happen.
            if (Native.IsWindow(e.Hwnd) && Native.IsHungAppWindow(e.Hwnd)) continue;

            lock (_lock) _hidden.Remove(e.Hwnd);
            Restore(e);
            done++;
        }
        return done;
    }

    public void ReleaseAll()
    {
        List<Entry> all;
        lock (_lock) { all = _hidden.Values.ToList(); _hidden.Clear(); }
        foreach (var e in all) Restore(e);
    }

    private static void Restore(Entry e)
    {
        if (!Native.IsWindow(e.Hwnd)) return;          // the app closed it; nothing to do
        if (Native.IsHungAppWindow(e.Hwnd)) return;    // would block; the deadline sweep retries

        int ex = Native.GetWindowLong(e.Hwnd, Native.GWL_EXSTYLE);

        if (e.WasLayered)
        {
            // The application layered its own window, so the alpha it has now is the newest thing known
            // about what it should look like - and if the application has written one while we held the
            // window, it is the application's answer and not ours to overrule. Left exactly as it is.
            if ((ex & Native.WS_EX_LAYERED) == 0) return;
            if (!Native.GetLayeredWindowAttributes(e.Hwnd, out _, out byte now, out _) || now != 0) return;

            // Ours, then, or nobody's: and this is the one value that must never be written back. Alpha
            // nought is invisible *and* shown, so Windows and the application both think this window is on
            // screen and neither will ever write that alpha again - the window is gone for good, and
            // clicking its taskbar button only activates something that is already "visible". More visible
            // than the application wanted is a cosmetic fault; invisible is not a window any more. See the
            // header.
            byte back = e.OrigAlpha != 0 ? e.OrigAlpha : (byte)255;
            Native.SetLayeredWindowAttributes(e.Hwnd, 0, back, Native.LWA_ALPHA);
            if (e.OrigAlpha == 0)
                Log.Write($"hwnd={e.Hwnd} was found at alpha 0 and nothing wrote one while we held it; " +
                          "handing it back visible rather than invisible for good");
            return;
        }

        // Alpha first and unconditionally, then the style. Restoring the style makes DWM
        // recreate the surface (one frame of flicker), so the caller only does this while
        // the panel still covers the window.
        if ((ex & Native.WS_EX_LAYERED) != 0)
        {
            Native.SetLayeredWindowAttributes(e.Hwnd, 0, 255, Native.LWA_ALPHA);

            // Only clear the bit we set; leave any other style change the app made alone.
            Native.SetWindowLong(e.Hwnd, Native.GWL_EXSTYLE, ex & ~Native.WS_EX_LAYERED);
        }
    }

    /// <summary>
    /// How long a hold lasts before it has to be renewed, and how often the sweep looks.
    ///
    /// A hold is taken and renewed by the caller that means to keep the window - an advertisement, which
    /// knows it wants the window for as long as the card is up - and it expires a second after the last
    /// renewal. The alternative, a hold taken once for the advertisement's whole length, is six minutes in
    /// which a card that died without releasing leaves a transparent window behind it; renewed, the same
    /// death gives the window back a second later.
    /// </summary>
    public const int HoldRenewMs = 1000;

    /// <summary>How often the sweep thread looks for a hide that has run out.</summary>
    private const int SweepIntervalMs = 250;

    private Thread? _sweeper;
    private volatile bool _stopSweeper;

    /// <summary>
    /// Starts the thread that puts expired hides back, and it is deliberately a thread of its own: the
    /// animation thread is the one that talks to other processes' windows, and talking to a window that has
    /// stopped answering can block. The last line of defence must not be able to block behind the thing it
    /// is a defence against.
    /// </summary>
    public void Start()
    {
        if (_sweeper != null) return;
        _stopSweeper = false;
        _sweeper = new Thread(Sweep) { IsBackground = true, Name = "MoaHideSweeper" };
        _sweeper.Start();
    }

    /// <summary>Stops the sweep thread. Anything still hidden is the caller's to release.</summary>
    public void Stop()
    {
        _stopSweeper = true;
        try { _sweeper?.Join(500); } catch { }
        _sweeper = null;
    }

    private void Sweep()
    {
        while (!_stopSweeper)
        {
            try { ReleaseExpired(); } catch { /* a sweeper that throws once must keep sweeping */ }
            Thread.Sleep(SweepIntervalMs);
        }
    }
}
