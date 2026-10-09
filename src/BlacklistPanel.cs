// The blacklist panel: the windows lately caught by the animation on the left, the rules on the right, and one
// button for each thing the user can decide.
//
// This is the program's first window with controls. Everything else is the tray menu, which Windows draws, a
// message box, and the animation panel, which draws itself - and that is the reason to reach for the standard
// controls here: a list of names, a selection and buttons is what they exist for, where doing it by hand would
// be a scrolling, hit-testing, focus-handling list of my own.
//
// Nothing in the window is drawn by this file. The lists are stock list boxes, so their rows, their selection
// and their scrollbars are the system's; a rule that is switched off says so in its text rather than in its
// colour, and a name too long for the window is reached with the list box's own horizontal scrollbar.
//
// There is one selection, not one per list. Two lists that each remember what was chosen in them leave the
// buttons asking which one the user meant, and leave the window showing two highlighted rows at once, which is
// what it did before this: whichever row was clicked last decides, and the other list is cleared.
//
// Modal, with a message loop of its own: the tray host's loop is on this same thread, so a nested loop keeps
// the tray - and the animation, which is the thing the user is watching - alive while the panel is up, and it
// avoids keeping an "is it open" state in a program that otherwise has none. The panel is a tool window, so it
// takes no place in the taskbar for the minute it is open.

using System.Runtime.InteropServices;

namespace Moa;

internal static class BlacklistPanel
{
    private const string ClassName = "MobileOpenAnimationBlacklist";

    // Logical pixels at 96 DPI. The window is not resizable, so these are what the layout is designed
    // around; what it is actually given is a function of the display's scale and of how much of the monitor
    // is usable, see Layout.
    private const int Width = 820, Height = 400, Pad = 12;
    private const int ListTop = 32, ListHeight = 268;
    private const int RowH = 26;

    // Buttons, and the two lists, kept in ranges that cannot be confused: the same WM_COMMAND carries a list
    // box's notifications, whose low word is the control's id just as a button's is.
    private const int IdByClass = 100, IdByProcess = 101, IdToggle = 102, IdRemove = 103, IdClose = 104,
                      IdRefresh = 105;
    private const int IdRecent = 200, IdRules = 201;

    private const int LbnSelChange = 1;

    /// <summary>
    /// Which list the one selection is in. The buttons are enabled from this, so a button can never act on a
    /// row the user did not choose.
    /// </summary>
    private enum Sel { None, Recent, Rule }

    private static Native.WndProcDelegate? _proc;
    private static bool _registered;

    private static IntPtr _hwnd, _recentBox, _rulesBox, _byClass, _byProcess, _toggle, _remove, _empty;
    private static IntPtr _recentLabel, _refresh, _ruleLabel, _close;
    private static List<Blacklist.Candidate> _candidates = new();
    private static List<Blacklist.Rule> _rules = new();
    private static readonly List<string> _recentText = new();
    private static readonly List<string> _ruleText = new();

    private static Sel _sel = Sel.None;
    private static int _selIndex = -1;

    private static bool _suppress;   // set while this file changes a selection itself
    private static bool _selectNewest;   // a rule was just added: show it
    private static IntPtr _tray;   // the tray's window: the panel's owner, so the panel dies with it
    private static IntPtr _font;

    /// <summary>
    /// Where every control goes, worked out rather than typed.
    ///
    /// The panel is deliberately not resizable - a settings list with a drag corner is a worse tool than one
    /// that is the right size - and that is exactly why the positions cannot be constants: the same window has
    /// to suit a 1024-wide display at 100% and a 3840-wide one at 200%, and it has to suit whichever monitor it
    /// is opened on. So every position is a function of the scale and of the client area it actually got, and
    /// the two lists are what gives up room when there is less of it - they scroll, the buttons do not.
    ///
    /// The two lists get the same width, and each column is then built from that one number: an entry in the
    /// left list and a rule in the right one are the same kind of thing, and giving one of them less room said
    /// otherwise. Both scroll horizontally, so a name longer than half the window is readable either way.
    /// </summary>
    private static void Layout(uint dpi, int clientW, int clientH)
    {
        int S(int v) => Native.MulDiv(v, (int)dpi, 96);

        // Everything below is in 96 DPI units: the client area converted back, so the arithmetic is about the
        // design rather than about pixels.
        int cw = Math.Max(320, Native.MulDiv(clientW, 96, (int)dpi));
        int ch = Math.Max(220, Native.MulDiv(clientH, 96, (int)dpi));

        // Two equal columns and three gaps, which is every pixel of the width spoken for. No floor on the
        // column: cw is clamped to 320 above, so this is never narrower than 142.
        int listW = (cw - 3 * Pad) / 2;
        int rightX = Pad + listW + Pad;
        int listH = Math.Max(80, ListHeight + (ch - Height));
        int rowY = ListTop + listH + 8;
        int half = (listW - 8) / 2;

        void Place(IntPtr h, int x, int y, int w, int hgt)
        {
            if (h != IntPtr.Zero)
                Native.SetWindowPos(h, IntPtr.Zero, S(x), S(y), S(w), S(hgt),
                                    Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }

        Place(_recentLabel, Pad, 10, listW - 110, 18);
        Place(_refresh, Pad + listW - 100, 7, 100, 22);
        Place(_ruleLabel, rightX, 10, listW, 18);
        Place(_recentBox, Pad, ListTop, listW, listH);
        Place(_rulesBox, rightX, ListTop, listW, listH);
        Place(_byClass, Pad, rowY, half, RowH);
        Place(_byProcess, Pad + half + 8, rowY, half, RowH);
        Place(_toggle, rightX, rowY, half, RowH);
        Place(_remove, rightX + half + 8, rowY, half, RowH);
        Place(_empty, Pad, ch - (Height - 342), cw - 2 * Pad, 20);
        Place(_close, cw - Pad - 150, ch - (Height - 366), 150, 28);
    }

    /// <summary>
    /// Sizes the window for this display and this monitor: the designed size at the display's scale, and where
    /// that does not fit the monitor's usable area, less than that - the layout then gives the missing height
    /// to the two lists, which can scroll, rather than to the buttons, which cannot.
    /// </summary>
    private static void FitToWorkArea(uint style, uint dpi, RECT work)
    {
        const uint ExStyle = (uint)Native.WS_EX_TOOLWINDOW;
        int S(int v) => Native.MulDiv(v, (int)dpi, 96);
        int availW = work.Right - work.Left, availH = work.Bottom - work.Top;

        var frame = new RECT { Right = S(Width), Bottom = S(Height) };
        Native.AdjustWindowRectEx(ref frame, style, false, ExStyle);
        int overW = Math.Max(0, (frame.Right - frame.Left) - availW);
        int overH = Math.Max(0, (frame.Bottom - frame.Top) - availH);
        if (overW > 0 || overH > 0)
        {
            frame = new RECT { Right = Math.Max(240, S(Width) - overW),
                               Bottom = Math.Max(180, S(Height) - overH) };
            Native.AdjustWindowRectEx(ref frame, style, false, ExStyle);
        }

        int ww = frame.Right - frame.Left, wh = frame.Bottom - frame.Top;
        Native.SetWindowPos(_hwnd, IntPtr.Zero,
                            work.Left + (availW - ww) / 2, work.Top + (availH - wh) / 2, ww, wh,
                            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    /// <summary>
    /// The usable area of a monitor, or of the primary one when there is no monitor to ask about.
    /// </summary>
    private static RECT WorkAreaOf(IntPtr monitor)
    {
        var info = new Native.MONITORINFO { CbSize = (uint)Marshal.SizeOf<Native.MONITORINFO>() };
        if (monitor != IntPtr.Zero && Native.GetMonitorInfoW(monitor, ref info)) return info.RcWork;
        return new RECT { Right = Native.GetSystemMetrics(0), Bottom = Native.GetSystemMetrics(1) };
    }

    /// <summary>
    /// A font for this display's scale, sent to every control. The stock GUI font would be the size 96 DPI
    /// asked for, which is visibly too small on a scaled display - and being per monitor aware, this program
    /// knows better. Called again when the window moves to a monitor at another scale.
    /// </summary>
    private static void ReFont(uint dpi)
    {
        IntPtr font = Native.CreateFontW(-Native.MulDiv(9, (int)dpi, 72), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 0, 0,
                                         "Segoe UI");
        if (font == IntPtr.Zero) return;
        IntPtr old = _font;
        _font = font;
        foreach (var c in AllControls())
            if (c != IntPtr.Zero) Native.SendMessageW(c, Native.WM_SETFONT, font, (IntPtr)1);
        if (old != IntPtr.Zero) Native.DeleteObject(old);
    }

    /// <summary>
    /// Opens the panel and does not return until it is closed. Opening it twice cannot happen: the tray menu
    /// would have to be reached while the panel holds the focus, and the handle says no even then.
    /// </summary>
    public static void Show()
    {
        if (_hwnd != IntPtr.Zero) return;
        if (!Register()) return;

        // The monitor the cursor is on, which is the one the tray icon and its menu are on, and therefore the
        // one the user is looking at. Its work area is what the window has to fit into, and its scale is the
        // one the layout is built at - not the system's, which is only the primary monitor's.
        Native.GetCursorPos(out var cursor);
        var work = WorkAreaOf(Native.MonitorFromPoint(cursor, Native.MONITOR_DEFAULTTONEAREST));

        uint style = (uint)(Native.WS_CAPTION | Native.WS_SYSMENU | Native.WS_BORDER);
        int x = work.Left + ((work.Right - work.Left) - Width) / 2;
        int y = work.Top + ((work.Bottom - work.Top) - Height) / 2;
        // Owned by the tray host, and that one argument is the whole of the panel's lifetime binding. Windows
        // destroys an owner's owned windows when it destroys the owner, so the panel cannot outlive the program:
        // nothing here notices anything, nothing polls, and a program that is killed outright takes its windows
        // with it anyway because the kernel tears a process's windows down with it. This replaced a timer that
        // asked "is the tray window still there" twice a second, which was a way of finding out something the
        // system already knew and could have been told for free.
        _hwnd = Native.CreateWindowExW((uint)Native.WS_EX_TOOLWINDOW, ClassName, Strings.T("Blacklist"),
            style, x, y, Width, Height, _tray, IntPtr.Zero, Native.GetModuleHandleW(null), IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) return;

        // The window's own scale, asked after it exists because that is the only moment the answer is about
        // the display this window is actually on. FitToWorkArea then sizes it for that scale and that screen,
        // and the client area it ends up with is what the layout is built from.
        uint dpi = Native.GetDpiForWindow(_hwnd);
        if (dpi == 0) dpi = Native.GetDpiForSystem();
        FitToWorkArea(style, dpi, work);
        Native.GetClientRect(_hwnd, out var client);
        int cw = client.Right - client.Left, ch = client.Bottom - client.Top;

        uint ListStyle() => Native.WS_CHILD | Native.WS_VISIBLE | Native.WS_BORDER | Native.WS_VSCROLL |
                            Native.WS_HSCROLL | Native.WS_TABSTOP | Native.LBS_NOTIFY |
                            Native.LBS_NOINTEGRALHEIGHT;
        uint BtnStyle() => Native.WS_CHILD | Native.WS_VISIBLE | Native.WS_TABSTOP;

        IntPtr Make(string cls, uint st, int cx, int cy, int cw2, int ch2, int id, string text)
        {
            IntPtr c = Native.CreateWindowExW(0, cls, text, st, 0, 0, 0, 0,
                                              _hwnd, (IntPtr)id, Native.GetModuleHandleW(null), IntPtr.Zero);
            if (c != IntPtr.Zero && _font != IntPtr.Zero)
                Native.SendMessageW(c, Native.WM_SETFONT, _font, (IntPtr)1);
            return c;
        }

        ReFont(dpi);   // first, so that the controls created below are made with it
        _recentLabel = Make("STATIC", Native.WS_CHILD | Native.WS_VISIBLE, 0, 0, 0, 0, -1,
                            Strings.T("BlacklistRecent"));
        _refresh = Make("BUTTON", BtnStyle(), 0, 0, 0, 0, IdRefresh, Strings.T("BlacklistRefresh"));
        _ruleLabel = Make("STATIC", Native.WS_CHILD | Native.WS_VISIBLE, 0, 0, 0, 0, -1,
                          Strings.T("BlacklistEntries"));
        _recentBox = Make("LISTBOX", ListStyle(), 0, 0, 0, 0, IdRecent, "");
        _rulesBox = Make("LISTBOX", ListStyle(), 0, 0, 0, 0, IdRules, "");
        _byClass = Make("BUTTON", BtnStyle(), 0, 0, 0, 0, IdByClass, Strings.T("BlacklistByClass"));
        _byProcess = Make("BUTTON", BtnStyle(), 0, 0, 0, 0, IdByProcess, Strings.T("BlacklistByProcess"));
        _toggle = Make("BUTTON", BtnStyle(), 0, 0, 0, 0, IdToggle, Strings.T("BlacklistToggle"));
        _remove = Make("BUTTON", BtnStyle(), 0, 0, 0, 0, IdRemove, Strings.T("BlacklistRemove"));
        _empty = Make("STATIC", Native.WS_CHILD | Native.WS_VISIBLE, 0, 0, 0, 0, -1, "");
        _close = Make("BUTTON", BtnStyle(), 0, 0, 0, 0, IdClose, Strings.T("BlacklistClose"));

        Layout(dpi, cw, ch);
        // Before ShowWindow, because uxtheme does not restyle a window it has already shown: a control has to
        // be told which theme it is on while it is still hidden, or it keeps the one it was made with.
        ApplyTheme(_hwnd);

        Blacklist.Reload();   // the one expensive read, before anything is shown
        Fill();
        Native.ShowWindow(_hwnd, Native.SW_SHOW);
        Native.SetFocus(_recentBox);
        // Asked of the list boxes themselves rather than of the lists this file filled, so that "nothing in
        // the window" and "nothing in the data" cannot be confused when the log is read afterwards.
        Log.Write($"blacklist panel: dpi={dpi} client {cw}x{ch}, " +
                  $"lists 0x{_recentBox:X}/0x{_rulesBox:X} holding " +
                  $"{Native.SendMessageW(_recentBox, Native.LB_GETCOUNT, IntPtr.Zero, IntPtr.Zero)}/" +
                  $"{Native.SendMessageW(_rulesBox, Native.LB_GETCOUNT, IntPtr.Zero, IntPtr.Zero)} rows, " +
                  $"{_candidates.Count} records and {_rules.Count} rules found, " +
                  $"{Blacklist.LogFileCount()} log files");
        // The layout, written down as well: these are the numbers a report about the panel being too small,
        // too large or cut off has to be read against, and they are what the arithmetic was checked with.
        Log.Write($"blacklist panel: layout at {dpi} dpi from client {cw}x{ch} " +
                  $"(logical {Native.MulDiv(cw, 96, (int)dpi)}x{Native.MulDiv(ch, 96, (int)dpi)})");
        // The nested loop. It ends when the panel is gone, and owning the panel is what guarantees that the
        // panel goes when the program does - so this is not a poll for the program still being there, it is the
        // ordinary end of a modal loop. WM_QUIT is the one message that must not be swallowed here: it means the
        // program is being asked to exit, and only the loop that owns the thread may act on that.
        var msg = new MSG();
        while (Native.IsWindow(_hwnd) && Native.GetMessageW(out msg, IntPtr.Zero, 0, 0))
        {
            if (msg.Message == Native.WM_QUIT) { Native.PostQuitMessage((int)msg.WParam); break; }
            if (!Native.IsDialogMessageW(_hwnd, ref msg))
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessageW(ref msg);
            }
        }

        Native.DeleteObject(_font);
        _font = IntPtr.Zero;
        _hwnd = _recentBox = _rulesBox = IntPtr.Zero;
        _byClass = _byProcess = _toggle = _remove = _empty = IntPtr.Zero;
        _recentLabel = _refresh = _ruleLabel = _close = IntPtr.Zero;
        _candidates.Clear();
        _rules.Clear();
        _recentText.Clear();
        _ruleText.Clear();
        _sel = Sel.None;
        _selIndex = -1;
    }

    /// <summary>
    /// Rebuilds both lists from the log and the rules. Called after every change as well, so that what the
    /// window shows is what the settings hold rather than what this file believes it did.
    /// </summary>
    private static void Fill()
    {
        // The records are the cached ones - reading the log again is the open path and the Refresh button, not
        // this one. A button press changes the rules, not the log, and rebuilding a few hundred rows from
        // memory is what keeps the animation behind this window from missing its frames while it is done.
        _candidates = Blacklist.Recent();
        _rules = Blacklist.Rules();

        _recentText.Clear();
        foreach (var c in _candidates)
            _recentText.Add($"{c.Time}  {c.Class}  ({c.Process})  {c.Width}x{c.Height}");
        _ruleText.Clear();
        foreach (var r in _rules)
            _ruleText.Add(RuleText(r));

        if (_sel == Sel.Recent && _selIndex >= _recentText.Count) { _sel = Sel.None; _selIndex = -1; }
        if (_sel == Sel.Rule && _selIndex >= _ruleText.Count) { _sel = Sel.None; _selIndex = -1; }

        _suppress = true;
        Reload(_recentBox, _recentText, _sel == Sel.Recent ? _selIndex : -1);
        Reload(_rulesBox, _ruleText, _sel == Sel.Rule ? _selIndex : -1);
        _suppress = false;

        // The line above the Close button, which carries the one thing this window has to say: what to do about
        // a list that is still empty. Closing needs no explanation - the button says it - so the rest of the
        // time the line is blank.
        Native.SetWindowTextW(_empty, _candidates.Count == 0 ? Strings.T("BlacklistEmpty") : "");

        // The newest rule is at the end of a list that may be scrolled past the fold. After a change that
        // lands there the panel selects it and brings it into view: the user pressed a button, and an answer
        // that cannot be seen is what makes a press look like it did nothing at all.
        if (_selectNewest && _ruleText.Count > 0)
        {
            _sel = Sel.Rule;
            _selIndex = _ruleText.Count - 1;
            _selectNewest = false;
        }
        if (_sel == Sel.Rule && _selIndex >= 0)
        {
            Native.SendMessageW(_rulesBox, Native.LB_SETCURSEL, (IntPtr)_selIndex, IntPtr.Zero);
            Native.SendMessageW(_rulesBox, Native.LB_SETTOPINDEX,
                                (IntPtr)Math.Max(0, _selIndex - 4), IntPtr.Zero);
        }

        UpdateButtons();
    }

    /// <summary>
    /// One rule as a line: the kind, then every note about the rule, then the name, then the process the name
    /// belongs to.
    ///
    /// The notes come as a group and all before the name, which is the fix for a row that used to read
    /// "...  SomeWindow  [covered by the process rule]" - the last note stranded after the name it describes,
    /// where it looked like part of the name. A rule that is switched off, one of the program's own and one
    /// already covered by a process rule are three facts about the same rule, so they are said in one place,
    /// in the order the user is likely to have caused them.
    ///
    /// The owner process stays after the name and is the one thing that does: it is not a note about the rule,
    /// it is the answer to the question the name raises - what is this class - and putting it before the name
    /// would delay the thing being looked for.
    /// </summary>
    private static string RuleText(Blacklist.Rule r)
    {
        string kind = r.ByProcess ? Strings.T("BlacklistProcess") : Strings.T("BlacklistClass");

        var notes = new List<string>();
        if (!r.Enabled) notes.Add(Strings.T("BlacklistDisabled"));
        if (r.BuiltIn) notes.Add(Strings.T("BlacklistBuiltIn"));
        if (Blacklist.Covered(r)) notes.Add(Strings.T("BlacklistCovered"));
        string prefix = notes.Count > 0 ? string.Join(" ", notes) + "  " : "";

        string owner = "";
        if (!r.ByProcess)
        {
            string proc = Blacklist.OwnerOfClass(r.Name);
            if (proc.Length > 0) owner = $"  [{proc}]";
        }

        return $"{kind}  {prefix}{r.Name}{owner}";
    }

    private static void Reload(IntPtr box, List<string> items, int select)
    {
        // Where the list was scrolled to, kept across the rebuild: rewriting the contents resets it to the top,
        // and a list that jumps back to the beginning after every button press reads as the window losing its
        // place - which is half of what "it feels laggy" was.
        long top = Native.SendMessageW(box, Native.LB_GETTOPINDEX, IntPtr.Zero, IntPtr.Zero).ToInt64();
        Native.SendMessageW(box, Native.LB_RESETCONTENT, IntPtr.Zero, IntPtr.Zero);
        foreach (var s in items) Native.SendMessageTextW(box, Native.LB_ADDSTRING, IntPtr.Zero, s);
        Native.SendMessageW(box, Native.LB_SETCURSEL, (IntPtr)select, IntPtr.Zero);
        if (top > 0) Native.SendMessageW(box, Native.LB_SETTOPINDEX, (IntPtr)top, IntPtr.Zero);
        Widen(box, items);
    }

    /// <summary>
    /// Tells a list box how far its rows reach, which is what makes its horizontal scroll bar appear and work.
    ///
    /// WS_HSCROLL on its own is not enough, and that is measured rather than assumed: with the style alone the
    /// horizontal range stays at the untouched default and nothing can be scrolled, while the same list box's
    /// vertical range is real as soon as it has more rows than fit. probe/aboutdialog checks both, next to each
    /// other, with the 200-row box as the control for the instrument.
    ///
    /// Measuring is not drawing. The width comes back from GDI with the rows left exactly as the list box made
    /// them; the list box then draws its own text into whatever position it has been scrolled to.
    /// </summary>
    private static void Widen(IntPtr box, List<string> items)
    {
        if (box == IntPtr.Zero || items.Count == 0) return;
        IntPtr dc = Native.GetDC(box);
        if (dc == IntPtr.Zero) return;
        // The control's own font, not the DC's: GetDC hands back a device context with the system font in it,
        // and measuring with that would size the rows for text the list box is not going to draw.
        IntPtr font = Native.SendMessageW(box, Native.WM_GETFONT, IntPtr.Zero, IntPtr.Zero);
        IntPtr old = font != IntPtr.Zero ? Native.SelectObject(dc, font) : IntPtr.Zero;
        try
        {
            int widest = 0;
            foreach (var row in items)
            {
                if (!Native.GetTextExtentPoint32W(dc, row, row.Length, out var size)) continue;
                if (size.Cx > widest) widest = size.Cx;
            }
            // A few pixels of room so the last character is not flush against the edge of the box. No scaling
            // here: the font the control is drawing with was already built for this display's DPI, so the
            // measurement is in the same pixels the rows are.
            if (widest > 0)
                Native.SendMessageW(box, Native.LB_SETHORIZONTALEXTENT, (IntPtr)(widest + 8), IntPtr.Zero);
        }
        finally
        {
            if (old != IntPtr.Zero) Native.SelectObject(dc, old);
            Native.ReleaseDC(box, dc);
        }
    }

    /// <summary>
    /// Enables exactly the buttons that mean something for what is selected. This is the whole reason there is
    /// one selection rather than two: a button that cannot act on the chosen row is greyed rather than doing
    /// nothing when pressed, which from the outside is what "it didn't work" looks like.
    /// </summary>
    private static void UpdateButtons()
    {
        bool recent = _sel == Sel.Recent && _selIndex >= 0 && _selIndex < _candidates.Count;
        bool rule = _sel == Sel.Rule && _selIndex >= 0 && _selIndex < _rules.Count;
        var r = rule ? _rules[_selIndex] : null;
        // Conversion needs the other name, and the log is the only thing that knows it: a rule whose class or
        // process was never seen gets no conversion rather than one that produces a name matching nothing.
        bool convertible = rule && !r!.BuiltIn && Blacklist.Counterpart(r).Length > 0;

        // A record can be blocked either way. A rule can be turned into the other kind - unless it is one of
        // the program's own, which are its own judgement and not the user's to redefine.
        Native.EnableWindow(_byClass, recent || (convertible && r!.ByProcess));
        Native.EnableWindow(_byProcess, recent || (convertible && !r!.ByProcess));
        Native.EnableWindow(_toggle, rule);
        Native.EnableWindow(_remove, rule && !r!.BuiltIn);
    }

    private static void OnSelectionChanged(IntPtr box)
    {
        if (_suppress) return;
        int index = Selected(box);
        _sel = index < 0 ? Sel.None : (box == _recentBox ? Sel.Recent : Sel.Rule);
        _selIndex = index;

        // One selection: the other list is cleared, so only ever one row is highlighted.
        _suppress = true;
        Native.SendMessageW(box == _recentBox ? _rulesBox : _recentBox, Native.LB_SETCURSEL,
                            (IntPtr)(-1), IntPtr.Zero);
        _suppress = false;

        UpdateButtons();
    }

    private static void OnCommand(int id, int note)
    {
        if (id is IdRecent or IdRules)
        {
            if (note == LbnSelChange) OnSelectionChanged(id == IdRecent ? _recentBox : _rulesBox);
            return;
        }

        switch (id)
        {
            case IdRefresh:
                Blacklist.Reload();
                Fill();
                return;

            case IdByClass:
                if (_sel == Sel.Recent)
                {
                    Blacklist.Add(_candidates[_selIndex].Class, byProcess: false);
                    SelectNewestRule();
                }
                else if (_sel == Sel.Rule) Blacklist.Convert(_rules[_selIndex]);
                break;

            case IdByProcess:
                if (_sel == Sel.Recent)
                {
                    Blacklist.Add(_candidates[_selIndex].Process, byProcess: true);
                    SelectNewestRule();
                }
                else if (_sel == Sel.Rule) Blacklist.Convert(_rules[_selIndex]);
                break;

            case IdToggle:
                if (_sel != Sel.Rule) return;
                Blacklist.Toggle(_rules[_selIndex]);
                break;

            case IdRemove:
                if (_sel != Sel.Rule) return;
                Blacklist.Remove(_rules[_selIndex]);
                break;

            case IdClose:
                Native.DestroyWindow(_hwnd);
                return;

            default:
                return;
        }
        Fill();
    }

    private static int Selected(IntPtr box)
    {
        long i = Native.SendMessageW(box, Native.LB_GETCURSEL, IntPtr.Zero, IntPtr.Zero).ToInt64();
        return i < 0 || i > int.MaxValue ? -1 : (int)i;
    }

    /// <summary>Asked for before the rebuild, because the new rule's index only exists afterwards.</summary>
    private static void SelectNewestRule() => _selectNewest = true;

    /// <summary>
    /// Closes the panel if it is open, and returns at once.
    ///
    /// A posted message rather than a function call, because the panel is a nested message loop: it is asleep
    /// in GetMessageW and can only be woken by a message. This is what the tray menu calls before it opens, so
    /// that a popup menu can never be shown while a modal window of this process holds the foreground - which
    /// is what made the menu's Exit item do nothing: the click went to a menu whose owner was not the
    /// foreground window, and the shell threw it away.
    /// </summary>
    public static void Close()
    {
        // Sent, not posted: whoever calls this is about to open the tray menu, and the point is that the panel
        // is gone by then. A posted message would still be in the queue at that moment.
        if (_hwnd != IntPtr.Zero && Native.IsWindow(_hwnd))
            Native.SendMessageW(_hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// The tray host's window, which becomes the panel's owner when it is created. Set once at start-up, before
    /// the panel can be opened; nothing here polls it afterwards, because being the owner is what does the work.
    /// </summary>
    public static void SetHost(IntPtr trayHwnd) => _tray = trayHwnd;

    /// <summary>
    /// Puts the window and every control on it onto the system's theme, and asks for one repaint. Nothing here
    /// draws: the windows, the list boxes and the buttons are all told which of uxtheme's own sets of drawings
    /// to use, and then paint themselves with it.
    ///
    /// The two exceptions are the ones the system has no dark answer for, and they are colour assignments
    /// rather than drawing either: the class background brush, which is what fills the parts of the window no
    /// control covers, and the text colour a static asks its parent for in WM_CTLCOLORSTATIC. On the light
    /// theme both are left exactly as the system had them.
    /// </summary>
    private static void ApplyTheme(IntPtr hwnd)
    {
        Native.DarkMode.Style(hwnd);
        Native.DarkMode.Frame(hwnd);

        // Set rather than registered: the class is made once, at the first Show, and registering a second
        // class for the other theme would mean two windows that must never both exist.
        IntPtr background = Native.DarkMode.BackgroundBrush();
        Native.SetClassBackground(hwnd, background != IntPtr.Zero ? background
                                                                 : (IntPtr)(Native.COLOR_WINDOW + 1));

        foreach (var c in AllControls()) Native.DarkMode.Style(c);

        // Erase as well as repaint: the client area is still in the old theme's colours underneath, and a
        // repaint that does not erase would leave them showing through.
        Native.InvalidateRect(hwnd, IntPtr.Zero, true);
    }

    /// <summary>Every handle on the window, in one place: the theme and the font both walk all of them.</summary>
    private static IntPtr[] AllControls() => new[]
    {
        _recentLabel, _refresh, _ruleLabel, _recentBox, _rulesBox, _byClass, _byProcess, _toggle,
        _remove, _empty, _close,
    };

    private static bool Register()
    {
        if (_registered) return true;
        _proc = WndProc;
        var wc = new WNDCLASSEXW
        {
            CbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            LpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            HInstance = Native.GetModuleHandleW(null),
            HbrBackground = (IntPtr)(Native.COLOR_WINDOW + 1),
            HCursor = Native.LoadCursorW(IntPtr.Zero, Native.IDC_ARROW),
            LpszClassName = ClassName,
        };
        _registered = Native.RegisterClassExW(ref wc) != 0;
        if (!_registered) Log.Write("blacklist panel: RegisterClassExW failed");
        return _registered;
    }

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
    {
        switch (msg)
        {
            case Native.WM_COMMAND:
                OnCommand((int)(w.ToInt64() & 0xFFFF), (int)((w.ToInt64() >> 16) & 0xFFFF));
                return IntPtr.Zero;

            case Native.WM_SETTINGCHANGE:
            {
                // Light and dark switched while the panel is open. Every top-level window is told, so this one
                // is; uxtheme is given the new answer and the window is styled from it.
                string area = l == IntPtr.Zero ? "" : Marshal.PtrToStringUni(l) ?? "";
                if (area == "ImmersiveColorSet")
                {
                    Native.DarkMode.Apply();
                    ApplyTheme(hwnd);
                    Log.Write($"blacklist panel: rethemed to {(Native.DarkMode.On ? "dark" : "light")}");
                }
                return IntPtr.Zero;
            }

            case Native.WM_CTLCOLORSTATIC:
            {
                // A plain static's background and text come from its parent, and on the dark theme there is no
                // system colour for either. Handing one back is the whole of the work; the control paints
                // itself with it. On the light theme this declines and the system's own answer stands.
                IntPtr brush = Native.DarkMode.BackgroundBrush();
                if (brush == IntPtr.Zero) return Native.DefWindowProcW(hwnd, msg, w, l);
                Native.SetTextColor(w, Native.DarkMode.DarkTextColour);
                Native.SetBkColor(w, 0x202020);
                return brush;
            }

            case Native.WM_DPICHANGED:
            {
                // The window has moved to a display at another scale. Windows proposes the rectangle it should
                // take there; taking it and rebuilding everything from the new scale is the whole of this
                // program's per monitor support - the manifest asks for it and without this the controls would
                // stay at the old size inside a window that had been resized around them.
                uint newDpi = (uint)(w.ToInt64() & 0xFFFF);
                var suggested = Marshal.PtrToStructure<RECT>(l);
                Native.SetWindowPos(hwnd, IntPtr.Zero, suggested.Left, suggested.Top,
                                    suggested.Right - suggested.Left, suggested.Bottom - suggested.Top,
                                    Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                if (newDpi == 0) newDpi = Native.GetDpiForWindow(hwnd);
                ReFont(newDpi);
                Native.GetClientRect(hwnd, out var client);
                Layout(newDpi, client.Right - client.Left, client.Bottom - client.Top);
                // Re-adding the rows is what makes the list boxes measure their items again at the new scale,
                // which they do from the font and not from anything this file passes them.
                Fill();
                Log.Write($"blacklist panel: moved to a display at {newDpi} dpi");
                return IntPtr.Zero;
            }

            case Native.WM_CLOSE:
                Native.DestroyWindow(hwnd);
                return IntPtr.Zero;

            case Native.WM_DESTROY:
                // Deliberately no PostQuitMessage here. The nested loop ends because the window it tests with
                // IsWindow has gone, and a quit posted here would not be there to be read by it: the loop
                // checks IsWindow before it checks the queue, so it leaves as soon as the window is destroyed
                // and the message is left for the program's own loop to find - which is how closing this panel
                // used to close the program.
                Log.Write("blacklist panel: closed");
                Log.Write("blacklist panel: closed");
                return IntPtr.Zero;

            default:
                return Native.DefWindowProcW(hwnd, msg, w, l);
        }
    }
}
