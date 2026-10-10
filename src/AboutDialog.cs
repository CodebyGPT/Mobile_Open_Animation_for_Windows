// The About box, and the only place in the program that opens a browser.
//
// A task dialog rather than the message box this used to be, for the one thing a message box cannot do: a link
// in the text that can be followed. comctl32 draws it - the frame, the icon and the link are all the system's,
// and there is no drawing routine here. The callback exists to be told which link was followed and nothing else.
//
// No buttons. Closing is the title bar's X, Esc, or Alt-F4, all of which TDF_ALLOW_DIALOG_CANCELLATION allows
// with no button present. There is nothing to decide in this box, so a button would only be a second way to do
// the one thing there is to do. The ini path is gone as well: "Open config directory" in the tray menu is where
// somebody asks for that, and the box is not the place to repeat it.
//
// The box has a second job. The launch-ad entry in the tray menu is hidden until somebody asks for it, and the
// way to ask is to play the old arcade code into this box: thirteen keys - the four arrows twice each, A B A B,
// Enter - with no more than three seconds between one key and the next. Every key that lands plays one step of
// the box's reaction: a step of 5% of the display's height around the display, with the text going to A and to B
// along the way, and the last key closes the box and opens the tray menu with the entry now in it. A wrong or
// late key stops the reaction where it is, and the next attempt starts from the beginning. Konami holds the
// sequence and the clock; the steps are below.
//
// That is a second job only while the entry is hidden. Once it is in the menu there is nothing left to reveal,
// so the box is not watched at all and the arrows and the letters belong to whoever is reading it.
//
// The keys are watched with a low-level keyboard hook rather than by subclassing the dialog, and that is not a
// preference. Keyboard messages go to whichever control holds the focus, this dialog's controls are text and a
// hidden default button, and a hook is the only thing that sees the arrows and the letters alike. It is
// installed for the life of the dialog and removed the moment TaskDialogIndirect returns, so nothing of it
// outlives the box.
//
// The hook decides which step was earned and nothing else, because a low-level hook callback that takes too
// long is removed by Windows and the sequence then stops being seen at all - halfway through, with the next key
// silently doing nothing. The reaction is well over that line, so it is played on a window-less timer instead.
// See OnKey.
//
// The text is rewritten with TDM_SET_ELEMENT_TEXT rather than by finding the controls and setting their window
// text, which was the first thing tried and silently did nothing: the dialog's content is a SysLink inside a
// DirectUIHWND and its main instruction is not a control at all, so there is no window holding the text. The
// title bar does have one - it is the dialog's own - and that is set directly. probe/aboutdialog measures both.

using System.Runtime.InteropServices;
using System.Text;

namespace Moa;

internal static class AboutDialog
{
    /// <summary>
    /// Held for the life of the dialog. A delegate marshalled as a function pointer is not kept alive by the
    /// native side, and the dialog outlives this method's stack frame, so a collected one would be a crash
    /// rather than a dialog that stops answering.
    /// </summary>
    private static Native.TaskDialogCallback? _callback;

    // ---- the launch-ad code ---------------------------------------------------------------------------

    /// <summary>The dialog, once it exists. Zero before that, and cleared as soon as it has been closed.</summary>
    private static IntPtr _dlg;

    /// <summary>
    /// The three strings the box is made of, kept because the step that starts an attempt is "put the text
    /// back" and these are what it goes back to. Nothing has to be read off the dialog for that: it was built
    /// from them, and its own elements are the ones the code writes over.
    /// </summary>
    private static string _title = "", _instruction = "", _content = "";

    /// <summary>Set when the code was entered. It is the answer Show hands back to the caller.</summary>
    private static bool _reveal;

    private static IntPtr _hook;
    private static Native.HookProc? _hookProc;

    /// <summary>
    /// The step waiting to be played, 0 for none.
    ///
    /// Written by the hook callback and read by the timer below, which are the same thread at different
    /// moments - which is why one number is enough and why it needs no queue.
    /// </summary>
    private static int _due;

    /// <summary>The id the waiting-step timer is armed under, and the callback it was armed with.</summary>
    private static readonly IntPtr DueTimer = (IntPtr)1;
    private static Native.TimerProc? _dueProc;

    /// <summary>
    /// Shows the box and returns when it is closed, saying whether the launch-ad code was entered while it was
    /// up. True is the caller's cue to reveal the entry and open the menu; this file knows nothing about either.
    /// </summary>
    public static bool Show(IntPtr owner)
    {
        _callback = OnNotification;
        _reveal = false;
        _dlg = IntPtr.Zero;
        _title = Strings.T("About");
        _instruction = "Mobile Open Animation for Windows";
        // The version first because it is the thing most often wanted from this box: which build am I running.
        // Everything AGPL-3.0 section 0 asks an interactive interface to show is below it.
        _content = Strings.T("Version") + ": " + Build.Version + "\n\n" + Strings.T("AboutText");

        Konami.Reset();
        _due = 0;
        _dueProc = OnDue;
        _hookProc = OnKey;
        // Watched for only while there is something left to reveal. Once the entry is in the menu the code has
        // done its job, and this box goes back to being a box: the arrows and the letters belong to whoever is
        // reading it, and nothing is hooked at all. Editing the ini can put it back - see Settings.AdsVisible.
        if (Program.S.AdsVisible)
        {
            Log.Write("about: the launch-ad entry is already in the menu; the code is not watched for");
        }
        else
        {
            _hook = Native.SetWindowsHookExW(Native.WH_KEYBOARD_LL, _hookProc,
                                             Native.GetModuleHandleW(null), 0);
            Log.Write($"about: keyboard hook {(_hook != IntPtr.Zero ? "installed" : "FAILED")}");
        }

        var config = new Native.TASKDIALOGCONFIG
        {
            CbSize = (uint)Marshal.SizeOf<Native.TASKDIALOGCONFIG>(),
            HwndParent = owner,
            // The link flag is what turns <a href> in the content into something clickable; the dialog does not
            // follow links itself, it tells the callback which one was followed. Cancellation is what keeps the
            // box closable with no button in it - without it a dialog with no buttons could not be dismissed.
            DwFlags = Native.TDF_ENABLE_HYPERLINKS | Native.TDF_ALLOW_DIALOG_CANCELLATION,
            PszWindowTitle = _title,
            MainIcon = Native.TD_INFORMATION_ICON,
            PszMainInstruction = _instruction,
            PszContent = _content,
            PfCallback = _callback,
        };

        int hr;
        try
        {
            hr = Native.TaskDialogIndirect(ref config, out _, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            Native.KillTimer(IntPtr.Zero, DueTimer);
            if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
            _due = 0;
            _dlg = IntPtr.Zero;
        }
        // Written either way. A dialog that never appeared and one that was closed both come out of this call
        // as nothing happening, and only the second is what the user meant.
        Log.Write(hr != 0
            ? $"about: TaskDialogIndirect failed 0x{hr:X8}; nothing was shown"
            : "about: closed");
        return hr == 0 && _reveal;
    }

    private static int OnNotification(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr data)
    {
        // The URL comes from our own string, but read it anyway rather than substituting the constant: what is
        // opened should be what was clicked.
        if (msg == Native.TDN_HYPERLINK_CLICKED) Open(Marshal.PtrToStringUni(lParam) ?? "");
        // Sent once the box and its controls are both built, and it is where the handle comes from: the callback
        // is handed the dialog on every notification, but the keys have to be watched from before the box is
        // ever shown, so the handle has to be written down when it first exists.
        else if (msg == Native.TDN_DIALOG_CONSTRUCTED) _dlg = hwnd;
        return 0;   // S_OK
    }

    private static void Open(string url)
    {
        if (url.Length == 0) return;
        Log.Write($"about: opening {url}");
        try
        {
            Native.ShellExecuteW(IntPtr.Zero, "open", url, null, null, Native.SW_SHOWNORMAL);
        }
        catch (Exception ex)
        {
            Log.Write($"about: could not open {url} ({ex.GetType().Name}: {ex.Message})");
        }
    }

    // ---- what the box does about the code -------------------------------------------------------------

    /// <summary>
    /// The hook, and the reason it does nothing but decide: it runs on the input path, for every key press in
    /// the system, and Windows removes a low-level hook whose callback takes too long. The reaction is well
    /// over that line - rewriting the box's text makes it re-measure and repaint - so playing it here is how
    /// the sequence stops being seen at all, halfway through, with the next key simply doing nothing.
    ///
    /// So this only asks Konami what the key earned and names a timer. SetTimer with no window is delivered by
    /// whoever pumps this thread's queue, which while the box is up is the box's own modal loop, measured in
    /// probe/aboutdialog - and the callback runs on the same thread as the dialog, so everything it does to
    /// the dialog is an ordinary same-thread call.
    /// </summary>
    private static IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0 && (uint)wParam == Native.WM_KEYDOWN && _dlg != IntPtr.Zero)
            {
                var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
                int step = Konami.Press(info.VkCode, Compat.TickCount64);
                if (step != 0)
                {
                    _due = step;
                    Native.SetTimer(IntPtr.Zero, DueTimer, 1, _dueProc!);
                }
                else if (Konami.LastDrop != Konami.DropNone)
                {
                    // Not logged here either. What the log gets is the same fact, one timer tick later.
                    _due = -Konami.LastDrop;
                    Native.SetTimer(IntPtr.Zero, DueTimer, 1, _dueProc!);
                }
            }
        }
        catch { }
        return Native.CallNextHookEx(_hook, code, wParam, lParam);
    }

    /// <summary>
    /// Plays the step the hook left behind. One tick, then the timer goes: what is waited for is the end of
    /// the hook callback, not an interval, and a hook callback returns in microseconds.
    /// </summary>
    private static void OnDue(IntPtr hwnd, uint msg, IntPtr id, uint time)
    {
        Native.KillTimer(IntPtr.Zero, DueTimer);
        int due = _due;
        _due = 0;
        if (due == 0 || _dlg == IntPtr.Zero) return;

        if (due < 0)
        {
            Log.Write(due == -Konami.DropTooLate
                ? $"about: launch-ad code dropped - nothing came within {Konami.WindowMs} ms of the key before"
                : "about: launch-ad code dropped - the wrong key, and the next attempt starts over");
            return;
        }

        long at = Compat.TickCount64;
        Step(due);
        long took = Compat.TickCount64 - at;
        Log.Write($"about: launch-ad code {due}/{Konami.Steps}, played in {took} ms");
        if (due != Konami.Steps) return;

        // Asked for, not done: the settings file and the tray menu are the caller's business, and the box is
        // closed first so that the menu opens over nothing.
        _reveal = true;
        Native.PostMessageW(_dlg, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// One step of the reaction, numbered the same way the keys are. 13 is the caller's: by then there is
    /// nothing left to play, because the box is closing.
    /// </summary>
    private static void Step(int step)
    {
        // 5% of the display's height, in whichever direction the step asks for - height for the sideways steps
        // as well, which is what the feature asks for and what makes the two steps each way cancel out exactly.
        int d = DisplayHeight() * 5 / 100;
        switch (step)
        {
            case 1:  Restore(); Nudge(0, -d); break;
            case 2:  Nudge(0, -d); break;
            case 3:  Nudge(0,  d); break;
            case 4:  Nudge(0,  d); break;
            case 5:  Nudge(-d, 0); break;
            case 6:  Nudge(-d, 0); break;
            case 7:  Nudge( d, 0); break;
            case 8:  Nudge( d, 0); break;
            case 9:  Letters('A'); break;
            case 10: Letters('B'); break;
            case 11: Letters('A'); break;
            case 12: Letters('B'); break;
        }
    }

    /// <summary>The height of the display the box is on, asked for each time because the box has moved.</summary>
    private static int DisplayHeight()
    {
        IntPtr monitor = Native.MonitorFromWindow(_dlg, Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MONITORINFO { CbSize = (uint)Marshal.SizeOf<Native.MONITORINFO>() };
        if (monitor == IntPtr.Zero || !Native.GetMonitorInfoW(monitor, ref info)) return 0;
        return info.RcMonitor.Bottom - info.RcMonitor.Top;
    }

    private static void Nudge(int dx, int dy)
    {
        if ((dx == 0 && dy == 0) || !Native.GetWindowRect(_dlg, out var r)) return;
        Native.SetWindowPos(_dlg, IntPtr.Zero, r.Left + dx, r.Top + dy, 0, 0,
                            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    /// <summary>The text as it was, which is both the first step and the way out of a half-finished attempt.</summary>
    private static void Restore()
    {
        Native.SendMessageTextW(_dlg, Native.TDM_SET_ELEMENT_TEXT,
                                (IntPtr)Native.TDE_MAIN_INSTRUCTION, _instruction);
        Native.SendMessageTextW(_dlg, Native.TDM_SET_ELEMENT_TEXT, (IntPtr)Native.TDE_CONTENT, _content);
        Native.SetWindowTextW(_dlg, _title);
    }

    /// <summary>
    /// Every character replaced by one letter, the length and the line breaks kept so that the box keeps its
    /// shape while it is being written over - apart from the height, which it re-measures, and which is what
    /// makes the overwriting visible at all. probe/aboutdialog measured that and this message.
    /// </summary>
    private static void Letters(char c)
    {
        Native.SendMessageTextW(_dlg, Native.TDM_SET_ELEMENT_TEXT, (IntPtr)Native.TDE_MAIN_INSTRUCTION,
                                Overwrite(_instruction, c));
        Native.SendMessageTextW(_dlg, Native.TDM_SET_ELEMENT_TEXT, (IntPtr)Native.TDE_CONTENT,
                                Overwrite(_content, c));
        Native.SetWindowTextW(_dlg, Overwrite(_title, c));
    }

    private static string Overwrite(string text, char c)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char ch in text) sb.Append(ch is '\n' or '\r' ? ch : c);
        return sb.ToString();
    }
}

