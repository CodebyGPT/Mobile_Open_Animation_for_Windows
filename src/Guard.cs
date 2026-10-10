// Hiding someone else's window is the dangerous part of this program, so it lives in one
// small class with one rule:
//
//     EVERY window we hide is restored, on EVERY exit path, exactly once.
//
// The failure this is built to make impossible is the one the Windhawk review caught in
// the original mod: an exit path that left a window at alpha 0, making it invisible for
// good. So: the original style and alpha are recorded before anything is touched, the
// restore is idempotent, and it is called from the normal path, the deadline sweep, the
// unhandled-exception handler and shutdown.

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
    /// Keeps one window hidden for longer than the watchdog would.
    ///
    /// The watchdog exists so that nothing can stay invisible for good, and its deadline is the same for every
    /// window because it is there to cover a hide that something forgot. This is the opposite case: a caller
    /// that knows exactly how long it means to keep the window - an advertisement that covers it, and the cap
    /// on that advertisement - and says so. The deadline is moved out rather than removed, so a caller that
    /// dies without saying anything still cannot leave a window invisible for ever: the watchdog puts it back
    /// at the moment the advertisement could no longer have been up anyway.
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
            // The app layered its own window: give it back its own alpha. Never 255 blindly,
            // or we would overwrite an alpha the app set for itself.
            if ((ex & Native.WS_EX_LAYERED) != 0)
                Native.SetLayeredWindowAttributes(e.Hwnd, 0, e.OrigAlpha, Native.LWA_ALPHA);
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
}
