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
//
// The box also has an egg in it, and it is a mouse's: clicking the icon - the program's own, which is what
// TDF_USE_HICON_MAIN puts there - writes the same thirteen-step code into the box one glyph at a time,
// up up down down left left right right A B A B and Enter, for as long as the clicks keep coming within two
// seconds of each other; two seconds of nothing puts the title back, and the fourteenth click does nothing
// more than the thirteenth. Two things about it are not obvious. It cannot be a control, because the icon is
// drawn by the dialog's DirectUI scene and there is nothing to click - so the hook has a point and the icon's
// cell is measured into the source (see IconCell). And it is a second low-level hook rather than a use of the
// keyboard one, because it is watching a mouse and because it must work in the box's other state, when the
// launch-ad entry is already in the menu and no key is watched for at all.

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

    /// <summary>
    /// True from the moment a box is asked for until it is closed. See <see cref="Show" />: the box's own
    /// modal loop is what lets a second ask arrive while the first is still on the stack.
    /// </summary>
    private static bool _open;

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

    /// <summary>The step waiting to be played, 0 for none.
    ///
    /// Written by the hook callback and read by the timer below, which are the same thread at different
    /// moments - which is why one number is enough and why it needs no queue.
    /// </summary>
    private static int _due;

    /// <summary>
    /// The id of the timer that is waiting to play that step, as SetTimer returned it.
    ///
    /// A timer with no window does not answer to the id it was asked for. SetTimer(NULL, 1, ...) is asked for
    /// id 1, generates one of its own and hands that to the callback; KillTimer(NULL, 1) then kills nothing at
    /// all, and the timer - armed at one millisecond - keeps ticking for the rest of the program's life. Every
    /// id here is therefore the one that came back, and it is what kills the timer. Measured in
    /// probe/aboutdialog, which counts the ticks that a timer killed by the id it asked for goes on making.
    /// </summary>
    private static IntPtr _dueId;

    /// <summary>
    /// The id the waiting-step timer is armed under, and the callback it is armed with. The id is only what
    /// the call is made with: a timer with no window is given one of Windows' own and it is that one which
    /// has to be kept. See _dueId.
    /// </summary>
    private static readonly IntPtr DueTimer = (IntPtr)1;
    private static Native.TimerProc? _dueProc;

    /// <summary>
    /// Shows the box and returns when it is closed, saying whether the launch-ad code was entered while it
    /// was up. True is the caller's cue to reveal the entry and open the menu; this file knows nothing about
    /// either.
    ///
    /// One box at a time. The box runs a modal loop of its own, and that loop is what pumps this thread's
    /// messages - which is where the tray icon's menu is answered. Asking for "About" a second time while the
    /// first box is up therefore arrives here with this method still on the stack, and building a second box
    /// over the first is not what the click meant. The second ask brings the box that is already there to
    /// the front instead, which is what it did mean.
    /// </summary>
    public static bool Show(IntPtr owner)
    {
        if (_open)
        {
            Log.Write("about: the box is already up; bringing it forward instead of building a second one");
            if (_dlg != IntPtr.Zero) Native.SetForegroundWindow(_dlg);
            return false;
        }
        _open = true;
        try { return ShowOnce(owner); }
        finally { _open = false; }
    }

    private static bool ShowOnce(IntPtr owner)
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
        _egg = 0;
        _clickOnIcon = -1;
        _writeProc = OnEggWrite;
        _clockProc = OnEggClock;
        _writeId = _clockId = IntPtr.Zero;
        _dueId = IntPtr.Zero;
        _mouseProc = OnMouse;
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

        // The click hook goes on either way: the icon is the box's own and the egg is a mouse, not a key. It
        // is the only thing wanted for the whole life of the box, and the keyboard hook's own rule - nothing
        // may outlive the dialog - is kept by the finally below.
        _mouseHook = Native.SetWindowsHookExW(Native.WH_MOUSE_LL, _mouseProc,
                                              Native.GetModuleHandleW(null), 0);
        if (_mouseHook == IntPtr.Zero) Log.Write("about: mouse hook FAILED; the egg cannot be clicked up");

        // The program's own icon as the box's icon, in place of the blue information one. The dialog draws
        // whatever it is handed here exactly as it draws its own - see TDF_USE_HICON_MAIN - so this box still
        // draws nothing itself: the handle is the exe's own icon resource, taken out and given back.
        //
        // The flag is set only when there is a handle, because the field means two different things depending on
        // it: without it, 0xFFFD is the information icon, and with it the same value is a handle to nothing.
        IntPtr icon = AppIcon.FromExe();

        var config = new Native.TASKDIALOGCONFIG
        {
            CbSize = (uint)Marshal.SizeOf<Native.TASKDIALOGCONFIG>(),
            HwndParent = owner,
            // The link flag is what turns <a href> in the content into something clickable; the dialog does not
            // follow links itself, it tells the callback which one was followed. Cancellation is what keeps the
            // box closable with no button in it - without it a dialog with no buttons could not be dismissed.
            DwFlags = Native.TDF_ENABLE_HYPERLINKS | Native.TDF_ALLOW_DIALOG_CANCELLATION |
                      (icon != IntPtr.Zero ? Native.TDF_USE_HICON_MAIN : 0),
            PszWindowTitle = _title,
            MainIcon = icon != IntPtr.Zero ? icon : Native.TD_INFORMATION_ICON,
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
            // By the ids the calls returned, never by the ones they were asked for: see _dueId.
            if (_dueId != IntPtr.Zero) Native.KillTimer(IntPtr.Zero, _dueId);
            if (_writeId != IntPtr.Zero) Native.KillTimer(IntPtr.Zero, _writeId);
            if (_clockId != IntPtr.Zero) Native.KillTimer(IntPtr.Zero, _clockId);
            if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
            if (_mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_mouseHook);
            _hook = IntPtr.Zero;
            _mouseHook = IntPtr.Zero;
            _due = 0;
            _dueId = _writeId = _clockId = IntPtr.Zero;
            _egg = 0;
            _clickOnIcon = -1;
            _dlg = IntPtr.Zero;
            // After the dialog has returned and not before: it is the dialog that is drawing it. See AppIcon.
            if (icon != IntPtr.Zero) Native.DestroyIcon(icon);
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
        else if (msg == Native.TDN_DIALOG_CONSTRUCTED)
        {
            _dlg = hwnd;
            // Written down because the egg's hit test is a box this program worked out rather than one the
            // system was asked for, and a click that misses has to have something to be compared against.
            if (_mouseHook != IntPtr.Zero)
            {
                var cell = IconCell();
                Log.Write($"about: the icon's cell is ({cell.Left},{cell.Top})-({cell.Right},{cell.Bottom}) " +
                          $"in the client area, at {Native.GetDpiForWindow(hwnd)} dpi");
            }
        }
        return 0;   // S_OK
    }

    private static void Open(string url)
    {
        if (url.Length == 0) return;
        // Through the same door the advertisement's name uses, and for the same reason: this program runs as
        // administrator, and a browser started by it would be too. See Native.OpenAsUser.
        bool opened = Native.OpenAsUser(url, out string why);
        Log.Write(opened ? $"about: opening {url} - {why}" : $"about: could not open {url}: {why}");
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
                    _dueId = Native.SetTimer(IntPtr.Zero, DueTimer, 1, _dueProc!);
                }
                else if (Konami.LastDrop != Konami.DropNone)
                {
                    // Not logged here either. What the log gets is the same fact, one timer tick later.
                    _due = -Konami.LastDrop;
                    _dueId = Native.SetTimer(IntPtr.Zero, DueTimer, 1, _dueProc!);
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
        // By the id it came in with, not by the one it was asked for: see _dueId.
        Native.KillTimer(IntPtr.Zero, id);
        _dueId = IntPtr.Zero;
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

    // ---- the egg: clicking the icon --------------------------------------------------------------------

    /// <summary>
    /// The arcade code as glyphs, which is what the box writes itself for a click: up up down down left left
    /// right right A B A B and Enter, spelled out (U+2191, U+2193, U+2190, U+2192, U+21B5). The same thirteen
    /// as <see cref="Konami" />, and what a click buys is one more of them.
    /// </summary>
    private const string Egg = "\u2191\u2191\u2193\u2193\u2190\u2190\u2192\u2192ABAB\u21B5";

    /// <summary>How long after a click the box goes back to saying what it said.</summary>
    private const int EggBackMs = 2000;

    /// <summary>
    /// How much of <see cref="Egg" /> is showing. Zero is the box as it was built, and it never passes the
    /// code's own length: at thirteen the whole thing is up and further clicks only hold it there.
    /// </summary>
    private static int _egg;

    /// <summary>
    /// The mouse hook, and the reason it decides nothing either: the hook that watches the launch-ad code
    /// already lives by this rule and the egg's write is the same re-measure and repaint. See
    /// <see cref="OnKey" />.
    /// </summary>
    private static IntPtr _mouseHook;
    private static Native.HookProc? _mouseProc;

    /// <summary>The click the hook left behind, and the timer that writes it out.</summary>
    private static readonly IntPtr EggWriteTimer = (IntPtr)2, EggClockTimer = (IntPtr)3;
    private static Native.TimerProc? _writeProc, _clockProc;

    /// <summary>
    /// The two waiting timers, by the id SetTimer returned - the only id that means anything for a timer with
    /// no window, and the only one that kills. See _dueId for the whole of that; what is here is the two of
    /// them, one per click (plays it) and one per egg (ends it), so that the finally that closes the box can
    /// stop either if it is still waiting.
    /// </summary>
    private static IntPtr _writeId, _clockId;

    /// <summary>
    /// The last click that landed on the box itself, in the dialog's client coordinates, and whether it was
    /// on the icon - -1 when there is nothing waiting to be written. It exists because the icon's cell is a
    /// number this program worked out rather than one it was given, so "the egg does not answer" is otherwise
    /// a question with nothing in the log to answer it with. Clicked by the hook, written out by the timer:
    /// the file is on the other side of that line deliberately.
    /// </summary>
    private static int _clickX, _clickY, _clickOnIcon = -1;

    /// <summary>
    /// Where the box's main icon is drawn, in device-independent pixels of the dialog's client area, scaled
    /// to the display it is on: a 32-pixel cell starting at (11,11) with the box widened to give a click
    /// around the drawing somewhere to land - and widened no further, because the main instruction's own
    /// text starts to the right of it and a click on that is not a click on the icon.
    ///
    /// Measured, not worked out. comctl32 draws the icon itself, in its DirectUI scene, so there is no
    /// control to ask and no rectangle to read: probe/aboutdialog photographs the box with the same flags and
    /// the same instruction, finds the icon's ink in the picture - 11,11 to 40,41 at 96 dpi, with the
    /// information icon's disc filling a 32-pixel cell - and that is where these come from. The icon's own
    /// drawing is inset about a pixel inside its cell, which is what the spare pixel on each side is for.
    /// </summary>
    private const int IconLeftDip = 10, IconTopDip = 10, IconRightDip = 44, IconBottomDip = 44;

    /// <summary>
    /// The icon's cell in the dialog's own coordinates. The dialog's DPI rather than the display's, because
    /// the box may be on the monitor that is not the one this program started on, and 96 when the handle is
    /// not a window yet.
    /// </summary>
    private static RECT IconCell()
    {
        int dpi = (int)Native.GetDpiForWindow(_dlg);
        if (dpi < 96) dpi = 96;
        return new RECT
        {
            Left = IconLeftDip * dpi / 96, Top = IconTopDip * dpi / 96,
            Right = IconRightDip * dpi / 96, Bottom = IconBottomDip * dpi / 96,
        };
    }

    /// <summary>
    /// The click hook. It counts and it names a timer; it does not read the file, and it does not write the
    /// box's text - see <see cref="OnKey" /> for why that rule exists. A click is only the egg's if it was on
    /// the box at all: the icon's cell is a rectangle on the screen, and a window covering the box would
    /// otherwise have its clicks counted as the box's.
    /// </summary>
    private static IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0 && (uint)wParam == Native.WM_LBUTTONDOWN && _dlg != IntPtr.Zero)
            {
                var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                IntPtr under = Native.WindowFromPoint(info.Pt);
                var p = info.Pt;
                if (under != IntPtr.Zero && Native.GetAncestor(under, Native.GA_ROOT) == _dlg &&
                    Native.ScreenToClient(_dlg, ref p) && InsideClient(p.X, p.Y))
                {
                    var cell = IconCell();
                    bool on = p.X >= cell.Left && p.X < cell.Right && p.Y >= cell.Top && p.Y < cell.Bottom;
                    if (on && _egg < Egg.Length) _egg++;
                    _clickX = p.X;
                    _clickY = p.Y;
                    _clickOnIcon = on ? 1 : 0;
                    // Two seconds from this click, or the egg is over - armed again on every click, which is
                    // why the one that was already waiting is killed first. A timer with no window is
                    // delivered by whoever pumps this thread's queue, which while the box is up is the box's
                    // own modal loop.
                    if (on) RearmClock();
                    _writeId = Native.SetTimer(IntPtr.Zero, EggWriteTimer, 1, _writeProc!);
                }
            }
        }
        catch { }
        return Native.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    /// <summary>Whether a point in the dialog's own coordinates is inside the page rather than the frame.</summary>
    private static bool InsideClient(int x, int y)
    {
        if (!Native.GetClientRect(_dlg, out var r)) return false;
        return x >= 0 && y >= 0 && x < r.Right && y < r.Bottom;
    }

    /// <summary>
    /// Starts the two seconds, or starts them again: what the last one was going to end, the next click has
    /// just extended. Killing it first is not tidiness. A timer with no window does not take the id it is
    /// asked for - SetTimer generates one - so asking twice leaves two timers running, and the older one would
    /// then put the text back while the clicks were still coming. Measured in probe/aboutdialog: two arms of
    /// one 60 ms timer ticked twice as often, not once.
    /// </summary>
    private static void RearmClock()
    {
        if (_clockId != IntPtr.Zero) Native.KillTimer(IntPtr.Zero, _clockId);
        _clockId = Native.SetTimer(IntPtr.Zero, EggClockTimer, EggBackMs, _clockProc!);
    }

    /// <summary>
    /// Plays what a click left behind: the click itself, so that a click that missed the icon says so and
    /// where it was, and one step of the code. One tick and it is over, which is why the timer is killed by
    /// the id it came in with before anything else happens here.
    /// </summary>
    private static void OnEggWrite(IntPtr hwnd, uint msg, IntPtr id, uint time)
    {
        Native.KillTimer(IntPtr.Zero, id);
        _writeId = IntPtr.Zero;
        if (_dlg == IntPtr.Zero) return;

        if (_clickOnIcon >= 0)
        {
            Log.Write($"about: click at ({_clickX},{_clickY}) in the client area - " +
                      (_clickOnIcon == 1
                          ? $"on the icon, egg {_egg}/{Egg.Length}"
                          : "not on the icon, which is what the cell in the log line above is for"));
            _clickOnIcon = -1;
        }
        if (_egg > 0) Instruction(Egg.Substring(0, _egg));
    }

    /// <summary>
    /// The two seconds are up: the box goes back to saying what it said.
    ///
    /// A click during the launch-ad code's own reaction is not reconciled with it - whichever of the two wrote
    /// last is what is on screen, and the next key of the code or this reset puts it back. Both are thirteen
    /// steps long and neither is done in the other's company.
    /// </summary>
    private static void OnEggClock(IntPtr hwnd, uint msg, IntPtr id, uint time)
    {
        Native.KillTimer(IntPtr.Zero, id);
        _clockId = IntPtr.Zero;
        if (_dlg == IntPtr.Zero) return;

        _egg = 0;
        Instruction(_instruction);
        Log.Write("about: the egg is over; the box says what it said");
    }

    /// <summary>The one element the egg ever writes, which is the instruction the icon sits beside.</summary>
    private static void Instruction(string text)
        => Native.SendMessageTextW(_dlg, Native.TDM_SET_ELEMENT_TEXT,
                                   (IntPtr)Native.TDE_MAIN_INSTRUCTION, text);
}

