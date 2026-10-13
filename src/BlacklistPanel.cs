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
// The panel is a tool window, so it takes no place in the taskbar for the minute it is open, and it lives in
// the program's own message loop like any other window - there is no loop of its own and nothing nests, which
// is the whole of why Show may be called at any time and from anywhere. The keyboard rules a window full of
// controls expects - Tab between them, the arrows inside a list, Esc - are applied by that loop; see
// IsDialogMessage.
//
// What closes it is the user and nothing else: the title bar's X, and the program going away. The program
// going away is not code - the panel's owner is the tray host's window, and Windows destroys an owner's owned
// windows with it, whether the host is being closed or has crashed. Nothing polls for that, nothing watches
// for it, and nothing has to be told: it is the same mechanism that makes the tray icon disappear.

// The window is resizable, and the layout is what makes that possible rather than the other way round: every
// position is a function of the client area it actually has, the rows are as tall as the font it is actually
// using, and the two lists are the only elastic thing in it - they scroll, and a column of buttons that grew
// with the window would be a worse tool than one that stayed its size. The floor a user cannot drag it below is
// that same arithmetic rather than a typed number: see Smallest, which WM_GETMINMAXINFO is answered with.
//
// Nothing in the layout is a row height typed into the file. Each one is the line height of the font, which is
// measured off the font itself (ReFont), plus the padding that turns a line of text into a header, a button or
// a hint. A machine whose GUI font is bigger, or whose "make text bigger" setting is up, therefore gets a
// taller panel and rows with room in them, rather than text falling out of a row that was sized on this
// machine. What that costs is that the numbers are not in this file to be read: they are in the log, one line
// per session, next to the font they came from.

using System.Runtime.InteropServices;

namespace Moa;

internal static class BlacklistPanel
{
    private const string ClassName = "MobileOpenAnimationBlacklist";

    /// <summary>
    /// A title bar with a system menu and a resizing frame, and no place in the taskbar, and clip children: the
    /// background under the controls is the controls' own business, so this window never paints over them. There
    /// is no maximize box on purpose: a list of names and a column of buttons has no bigger version that is more
    /// useful, and the frame is there for the width.
    /// </summary>
    private const uint Style = (uint)(Native.WS_CAPTION | Native.WS_SYSMENU | Native.WS_THICKFRAME |
                                      Native.WS_CLIPCHILDREN);

    /// <summary>
    /// What every child window is given on top of its own style: do not draw over a sibling. A control that has
    /// just been moved or resized repaints itself, and without this it can paint into the area of the control
    /// next to it - which is where the fragments of a scrollbar or of the label beside the right column's title
    /// came from while the window was being dragged.
    /// </summary>
    private const uint ChildStyle = Native.WS_CLIPSIBLINGS;

    // The horizontal design, in 96 DPI units. The vertical one is not here: it is derived from the font, see
    // Rows - and these three are the ones no font can answer, because a column is as wide as it is useful for
    // a filename rather than as wide as a letter is tall.
    private const int Pad = 12;             // the side margins, and the gutter between the two columns
    private const int Width = 820;          // the width the two columns were designed around
    private const int MinColumn = 180;      // the narrowest a column may be dragged to and still be readable
    private const int ListHeight = 268;     // how tall the lists are when there is room for that
    private const int MinListHeight = 80;   // and the least they may be dragged to

    // Buttons, and the two lists, kept in ranges that cannot be confused: the same WM_COMMAND carries a list
    // box's notifications, whose low word is the control's id just as a button's is.
    //
    // There is no close button among them. The window has a title bar and a system menu, so the X is already
    // there and a second way to do the one thing the X does is a second thing to keep in step - and the X is
    // the one the user asked for, which is why nothing else closes this window either. See WM_CLOSE.
    private const int IdByClass = 100, IdByProcess = 101, IdToggle = 102, IdRemove = 103,
                      IdRefresh = 104;
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
    private static IntPtr _recentLabel, _refresh, _ruleLabel;
    private static List<Blacklist.Candidate> _candidates = new();
    private static List<Blacklist.Rule> _rules = new();
    private static readonly List<string> _recentText = new();
    private static readonly List<string> _ruleText = new();

    private static Sel _sel = Sel.None;
    private static int _selIndex = -1;

    private static bool _suppress;   // set while this file changes a selection itself
    private static bool _selectNewest;   // a rule was just added: show it
    private static bool _closing;   // WM_CLOSE has been seen, so the WM_DESTROY that follows is the user's X
    private static IntPtr _font;

    /// <summary>
    /// How tall one line of the panel's font is, in 96 DPI units, measured off the font itself by ReFont. Zero
    /// until the window has a font, which is before it has controls - so a layout call that finds it zero has
    /// nothing to place anyway.
    /// </summary>
    private static int _line96;

    /// <summary>
    /// The vertical measurements of the layout, in 96 DPI units, all of them one line of text plus the padding
    /// that turns it into a row: a header, a button, a hint.
    ///
    /// This is what "the rows are not typed in" means, and the whole of why a machine whose GUI font is bigger
    /// gets a taller panel rather than a row of text that does not fit. The paddings are what is left of the
    /// numbers this file used to hold, and they were chosen against the line height this machine measures -
    /// fifteen units, from the 9 point face the system's metrics name at 144 dpi - because that is what makes
    /// the rows come out as the 22, 26 and 20 and the 100 the panel was designed with. On a display at 100% the
    /// same font measures sixteen, and every row is one unit taller, which is the font's hinting rather than
    /// this arithmetic: the pixel height of a line is not exactly proportional to the dpi it is set at.
    /// </summary>
    private readonly struct Rows
    {
        public readonly int Top;        // above the first row
        public readonly int Header;     // the two labels and the Refresh button
        public readonly int Label;      // and the labels inside that row, which are shorter than it
        public readonly int HeaderGap;  // the header row to the lists
        public readonly int Row;        // the row of four buttons
        public readonly int RowGap;     // the lists to that row, and that row to the hint below it
        public readonly int Hint;       // the line under the buttons that says what to do next
        public readonly int Bottom;     // and the margin under it

        /// <summary>Where the labels sit inside a header row that is three units taller than they are.</summary>
        public const int LabelDrop = 3;

        public Rows(int line)
        {
            Top = 7;
            Header = line + 7;
            Label = line + 3;
            HeaderGap = 3;
            Row = line + 11;
            RowGap = 8;
            Hint = line + 5;
            Bottom = 6;
        }

        /// <summary>All of it but the lists: what the window owes the rows that do not stretch.</summary>
        public int Fixed => Top + Header + HeaderGap + RowGap + Row + RowGap + Hint + Bottom;
    }

    /// <summary>
    /// A size in the window's own pixels from one in 96 DPI units, which is the space every number above is in.
    /// </summary>
    private static int S(int v, uint dpi) => Native.MulDiv(v, (int)dpi, 96);

    /// <summary>
    /// A whole window size for a client area of this size: the frame is what this adds, and it is the same
    /// arithmetic the layout's own sizing needs - see Desired, Smallest and FitToWorkArea.
    /// </summary>
    private static void WindowFor(uint dpi, int clientW, int clientH, out int w, out int h)
    {
        var frame = new RECT { Right = S(clientW, dpi), Bottom = S(clientH, dpi) };
        Native.AdjustWindowRectEx(ref frame, Style, false, (uint)Native.WS_EX_TOOLWINDOW);
        w = frame.Right - frame.Left;
        h = frame.Bottom - frame.Top;
    }

    /// <summary>The client area the layout asks for when there is room for all of it.</summary>
    private static void Desired(uint dpi, out int w, out int h)
    {
        var m = new Rows(_line96);
        w = Math.Max(Width, 3 * Pad + 2 * MinColumn);
        h = m.Fixed + ListHeight;
    }

    /// <summary>
    /// The client area below which the layout stops working: the two columns at their narrowest, and the lists
    /// at their shortest. This is the floor the user is held to while dragging, and it is the same arithmetic
    /// rather than a second set of numbers that could drift from it. A monitor too small for even this is
    /// answered by FitToWorkArea, which goes under it rather than putting the window off the screen.
    /// </summary>
    private static void Smallest(uint dpi, out int w, out int h)
    {
        var m = new Rows(_line96);
        w = 3 * Pad + 2 * MinColumn;
        h = m.Fixed + MinListHeight;
    }

    /// <summary>
    /// Where every control goes, worked out rather than typed, from the client area the window actually has and
    /// the font height the rows come from.
    ///
    /// The rows are placed in order, top down, and the lists take what is left. That is the whole of the
    /// vertical layout, and it is why there is no way for a change to one row to leave a band of nothing at the
    /// bottom of the window, or to place anything outside it: the middle is the elastic part and everything
    /// else is a row that follows the one before it. A window too short for the rows gives the lists their
    /// shortest height and lets the lower rows run off the bottom; the window cannot be dragged that short by
    /// hand (Smallest is what stops that) and a monitor that small has nowhere better to put it.
    ///
    /// The two lists get the same width, and each column is then built from that one number: an entry in the
    /// left list and a rule in the right one are the same kind of thing, and giving one of them less room said
    /// otherwise. Both scroll horizontally, so a name longer than half the window is readable either way.
    /// </summary>
    private static void Layout(uint dpi, int clientW, int clientH)
    {
        // Everything below is in 96 DPI units: the client area converted back, so the arithmetic is about the
        // design rather than about pixels.
        int cw = Math.Max(3 * Pad + 2 * MinColumn, Native.MulDiv(clientW, 96, (int)dpi));
        int ch = Math.Max(120, Native.MulDiv(clientH, 96, (int)dpi));

        var m = new Rows(_line96);

        // Two equal columns and three gaps, which is every pixel of the width spoken for.
        int listW = (cw - 3 * Pad) / 2;
        int rightX = Pad + listW + Pad;
        int half = (listW - 8) / 2;

        void Place(IntPtr h, int x, int y, int w, int hgt)
        {
            // No copy-bits: a control that has just changed size must be repainted rather than have its old
            // pixels stretched into the new shape, which is where the fragments of a scrollbar and of the
            // labels came from while this window was being dragged. See Native.SWP_NOCOPYBITS.
            if (h != IntPtr.Zero)
                Native.SetWindowPos(h, IntPtr.Zero, S(x, dpi), S(y, dpi), S(w, dpi), S(hgt, dpi),
                                    Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOCOPYBITS);
        }

        int y = m.Top;
        Place(_recentLabel, Pad, y + Rows.LabelDrop, listW - 110, m.Label);
        Place(_refresh, Pad + listW - 100, y, 100, m.Header);
        Place(_ruleLabel, rightX, y + Rows.LabelDrop, listW, m.Label);
        y += m.Header + m.HeaderGap;

        // The one elastic row: everything above and below it has a height of its own, so the lists are what
        // takes a window that is taller or shorter than the design, and they scroll when that is not enough.
        int listH = Math.Max(MinListHeight, ch - m.Fixed);
        Place(_recentBox, Pad, y, listW, listH);
        Place(_rulesBox, rightX, y, listW, listH);
        y += listH + m.RowGap;

        Place(_byClass, Pad, y, half, m.Row);
        Place(_byProcess, Pad + half + 8, y, half, m.Row);
        Place(_toggle, rightX, y, half, m.Row);
        Place(_remove, rightX + half + 8, y, half, m.Row);
        y += m.Row + m.RowGap;

        Place(_empty, Pad, y, cw - 2 * Pad, m.Hint);
    }

    /// <summary>
    /// Sizes the window for this display and this monitor: the size the layout asks for at the display's scale,
    /// and where that does not fit the monitor's usable area, less than that - the layout then gives the missing
    /// height to the two lists, which can scroll, rather than to the buttons, which cannot.
    ///
    /// The floor here is the layout's own, not this method's: a monitor that cannot hold even that gets a window
    /// that is too small, which is better than a window with part of it off the screen.
    /// </summary>
    private static void FitToWorkArea(uint dpi, RECT work)
    {
        int availW = work.Right - work.Left, availH = work.Bottom - work.Top;
        Desired(dpi, out int cw, out int ch);                 // in 96 DPI units, like everything else here
        WindowFor(dpi, cw, ch, out int ww, out int wh);

        // What is over the monitor, worked out on the window sizes and then given back in design units, because
        // reducing the client area by a number of pixels is not the same as reducing it by the frame's worth of
        // those pixels as well.
        int overW = Math.Max(0, ww - availW), overH = Math.Max(0, wh - availH);
        if (overW > 0 || overH > 0)
        {
            Smallest(dpi, out int minW, out int minH);
            cw = Math.Max(minW, cw - Native.MulDiv(overW, 96, (int)dpi));
            ch = Math.Max(minH, ch - Native.MulDiv(overH, 96, (int)dpi));
            WindowFor(dpi, cw, ch, out ww, out wh);
        }

        Native.SetWindowPos(_hwnd, IntPtr.Zero,
                            work.Left + (availW - ww) / 2, work.Top + (availH - wh) / 2, ww, wh,
                            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOCOPYBITS);
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
    /// The font every control is given, and the one measurement the layout is built from: the line height it
    /// comes out as.
    ///
    /// The face, the weight and the point size come from System.Drawing's own reading of the system metrics -
    /// SystemFonts.MessageBoxFont - rather than from a struct of this program's. The call behind it is one whose
    /// layout has changed with Windows and whose failure mode is a font that is not the one asked for; .NET
    /// already has that right, so this file does not keep a second copy of it. Its answer is a font at the dpi
    /// the process runs at, which is the system's, and the size is scaled from there to the display this window
    /// is on - that is the whole of the per-monitor part, and the probe measures both ends of it.
    ///
    /// The handle is made here rather than taken from that font, because a handle out of a Font object raises a
    /// question about who deletes it, and this one is this file's to delete. What the font metrics are is then
    /// measured off the handle, since a font's own idea of how tall its lines are is not its size.
    ///
    /// The fallback is the face and size this file asked for before the font came from the system, for a machine
    /// where the call throws. Losing the font entirely would lose the layout with it.
    /// </summary>
    private static void ReFont(uint dpi)
    {
        IntPtr font = UiFont(dpi, out int line96);
        if (font == IntPtr.Zero) return;
        _line96 = line96;
        IntPtr old = _font;
        _font = font;
        foreach (var c in AllControls())
            if (c != IntPtr.Zero) Native.SendMessageW(c, Native.WM_SETFONT, font, (IntPtr)1);
        if (old != IntPtr.Zero) Native.DeleteObject(old);
    }

    private static IntPtr UiFont(uint dpi, out int line96)
    {
        line96 = 0;
        int system = (int)Native.GetDpiForSystem();
        if (system <= 0) system = 96;

        try
        {
            using var ui = System.Drawing.SystemFonts.MessageBoxFont;
            // The system's font is expressed in the process's own dpi; this window may be on a display at
            // another one. Both the size in points and the height in pixels come out of the same arithmetic, so
            // the font is its size at this display rather than its pixels at another.
            double points = ui.SizeInPoints * dpi / system;
            _fontFace = ui.Name;
            _fontPoints = points;
            IntPtr made = Native.CreateFontW(-(int)Math.Round(points * dpi / 72.0), 0, 0, 0,
                                             ui.Bold ? 700 : 400, ui.Italic ? 1u : 0u, 0, 0, ui.GdiCharSet,
                                             0, 0, 0, 0, _fontFace);
            line96 = LineOf(made, dpi);
            if (line96 > 0) return made;
            if (made != IntPtr.Zero) Native.DeleteObject(made);
            Log.Write("blacklist panel: the system font made no line height; asking for one instead");
        }
        catch (Exception e)
        {
            Log.Write($"blacklist panel: the system font could not be read ({e.GetType().Name}); asking for one");
        }

        _fontFace = "Segoe UI";
        _fontPoints = 9.0 * dpi / 96;
        IntPtr font = Native.CreateFontW(-Native.MulDiv(9, (int)dpi, 72), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 0, 0,
                                        _fontFace);
        line96 = LineOf(font, dpi);
        return line96 > 0 ? font : IntPtr.Zero;
    }

    /// <summary>The face and the point size the font was made at, for the log and the log only.</summary>
    private static string _fontFace = "";
    private static double _fontPoints;

    /// <summary>
    /// How tall one line of this font is, in 96 DPI units: the character cell plus the leading the font asks
    /// for, which together are what a row of text takes. Measured on a screen device context, because what a
    /// font's metrics are is a question for a device context and there is one of those on every machine.
    /// </summary>
    private static int LineOf(IntPtr font, uint dpi)
    {
        if (font == IntPtr.Zero) return 0;
        IntPtr dc = Native.GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return 0;
        IntPtr old = Native.SelectObject(dc, font);
        bool read = Native.GetTextMetricsW(dc, out var tm);
        if (old != IntPtr.Zero) Native.SelectObject(dc, old);
        Native.ReleaseDC(IntPtr.Zero, dc);
        int px = read ? tm.Height + tm.ExternalLeading : 0;
        return px <= 0 ? 0 : Native.MulDiv(px, 96, (int)dpi);
    }

    /// <summary>
    /// Lays the window out again for the client area it has now, which is what a resize and a move to a monitor
    /// at another scale both end up asking for.
    /// </summary>
    private static void Relayout(IntPtr hwnd)
    {
        uint dpi = Native.GetDpiForWindow(hwnd);
        if (dpi == 0) dpi = Native.GetDpiForSystem();
        Native.GetClientRect(hwnd, out var client);
        Layout(dpi, client.Right - client.Left, client.Bottom - client.Top);
    }

    /// <summary>
    /// Opens the panel, or brings it to the front if it is already open, and returns at once either way.
    ///
    /// Opening it twice cannot happen, and this is the whole of the guard: the handle says so, which is why the
    /// handle is cleared in WM_DESTROY and not anywhere else. What it was before was a nested loop, and the
    /// guard then tested a handle that a destroyed window was still holding: the request that arrived between
    /// the destruction and the unwinding was refused as if a panel were up, and the panel came back only on the
    /// third try. See the note at the top of this file.
    /// </summary>
    public static void Show()
    {
        if (_hwnd != IntPtr.Zero)
        {
            // Created and not yet destroyed: there is one panel, and the ask means "show me the one there is".
            if (Native.IsWindow(_hwnd))
            {
                // Written down because a report of the panel going out of sight when the desktop is clicked, and
                // coming back when the tray is, left nothing at all in the log: no line of this file's and
                // nothing that destroyed it, so it was not destroyed. What is left is the state it is in when
                // somebody asks for it again - hidden, minimized, or simply behind something - and the window at
                // its own middle, which names whatever was in front of it if it was covered.
                Native.GetWindowRect(_hwnd, out var r);
                var middle = new POINT { X = (r.Left + r.Right) / 2, Y = (r.Top + r.Bottom) / 2 };
                Log.Write($"blacklist panel: already open; bringing it forward (visible=" +
                          $"{Native.IsWindowVisible(_hwnd)}, minimized={Native.WindowIsMinimized(_hwnd)}, " +
                          $"at its middle: {ClassOf(Native.WindowFromPoint(middle))})");

                // Being out of sight has three forms and each has one call. None of them is reached when the
                // panel is merely behind something, and none of them costs anything then either.
                if (Native.WindowIsMinimized(_hwnd)) Native.ShowWindow(_hwnd, Native.SW_RESTORE);
                else if (!Native.IsWindowVisible(_hwnd)) Native.ShowWindow(_hwnd, Native.SW_SHOW);
                Native.SetWindowPos(_hwnd, Native.HWND_TOP, 0, 0, 0, 0,
                                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                Native.SetForegroundWindow(_hwnd);
                return;
            }
            // Destroyed, and not by us: the WM_DESTROY that would have cleared this should have run first. Said
            // out loud because a handle that outlives its window is the one state this file must never be in and
            // a line in the log is how it would be found, and cleared here so that the window built below is not
            // built over the dead one's font and controls.
            Log.Write("blacklist panel: the handle outlived its window; starting again");
            Forget();
        }
        if (!Register()) return;

        // The monitor the cursor is on, which is the one the tray icon and its menu are on, and therefore the
        // one the user is looking at. Its work area is what the window has to fit into, and its scale is the
        // one the layout is built at - not the system's, which is only the primary monitor's.
        Native.GetCursorPos(out var cursor);
        var work = WorkAreaOf(Native.MonitorFromPoint(cursor, Native.MONITOR_DEFAULTTONEAREST));

        // Created hidden, at the corner of that monitor and square: not one of those four numbers survives, and
        // they are only here because a window has to be created at some size. It is not shown until after
        // FitToWorkArea, which cannot run until the window exists - asking a window about its display and its
        // font is the whole of how the size is worked out.
        //
        // UNOWNED, and that is the whole of a report that took a long time to read. It used to be owned by the
        // tray host - for the lifetime, and because an owner's windows are destroyed with it - and what that
        // cost was this: an owned window is dragged down the Z-order with its owner, and the owner here is a
        // window nobody ever sees, which the shell moves about on its own account. Every click on the tray icon
        // makes that host the foreground window, and the panel and the About box went down with it until they
        // sat just above the desktop's own window - after which a click on the desktop raised the desktop's
        // full-screen view over them and the panel was out of sight, still visible, still in place, and beyond
        // reach: a process without the foreground cannot raise its own window back over one that has it.
        // Measured in probe/panelzorder: with the invisible owner sent to the bottom the windows it owns follow
        // it to 340 and 343 while an unowned window of the same kind stays at 45, and the desktop's window is at
        // 345 - one above the owned pair and hundreds below the unowned one.
        //
        // The lifetime the owner used to carry is now DestroyWithHost, called from the tray host's own WM_DESTROY
        // - which is the same moment, arrived at from our side, with no polling and nothing to notice: the
        // program going away destroys the panel, and a program that is killed outright takes its windows with it
        // anyway because the kernel tears a process's windows down with it.
        _hwnd = Native.CreateWindowExW((uint)Native.WS_EX_TOOLWINDOW, ClassName,
            Strings.T("Blacklist"), Style, work.Left, work.Top, Width, Width, IntPtr.Zero, IntPtr.Zero,
            Native.GetModuleHandleW(null), IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) return;

        // The window's own scale, asked after it exists because that is the only moment the answer is about the
        // display this window is actually on. The font comes first, because the rows of the layout are made of
        // its line height and the size the window wants is the sum of those rows; then FitToWorkArea sizes it
        // for that scale and that screen, and the client area it ends up with is what the layout is built from.
        uint dpi = Native.GetDpiForWindow(_hwnd);
        if (dpi == 0) dpi = Native.GetDpiForSystem();
        ReFont(dpi);
        FitToWorkArea(dpi, work);
        Native.GetClientRect(_hwnd, out var client);
        int cw = client.Right - client.Left, ch = client.Bottom - client.Top;

        uint ListStyle() => Native.WS_CHILD | Native.WS_VISIBLE | Native.WS_BORDER | Native.WS_VSCROLL |
                            Native.WS_HSCROLL | Native.WS_TABSTOP | Native.LBS_NOTIFY |
                            Native.LBS_NOINTEGRALHEIGHT | ChildStyle;
        uint BtnStyle() => Native.WS_CHILD | Native.WS_VISIBLE | Native.WS_TABSTOP | ChildStyle;

        IntPtr Make(string cls, uint st, int cx, int cy, int cw2, int ch2, int id, string text)
        {
            IntPtr c = Native.CreateWindowExW(0, cls, text, st, 0, 0, 0, 0,
                                              _hwnd, (IntPtr)id, Native.GetModuleHandleW(null), IntPtr.Zero);
            if (c != IntPtr.Zero && _font != IntPtr.Zero)
                Native.SendMessageW(c, Native.WM_SETFONT, _font, (IntPtr)1);
            return c;
        }

        _recentLabel = Make("STATIC", Native.WS_CHILD | Native.WS_VISIBLE | ChildStyle, 0, 0, 0, 0, -1,
                            Strings.T("BlacklistRecent"));
        _refresh = Make("BUTTON", BtnStyle(), 0, 0, 0, 0, IdRefresh, Strings.T("BlacklistRefresh"));
        _ruleLabel = Make("STATIC", Native.WS_CHILD | Native.WS_VISIBLE | ChildStyle, 0, 0, 0, 0, -1,
                          Strings.T("BlacklistEntries"));
        _recentBox = Make("LISTBOX", ListStyle(), 0, 0, 0, 0, IdRecent, "");
        _rulesBox = Make("LISTBOX", ListStyle(), 0, 0, 0, 0, IdRules, "");
        _byClass = Make("BUTTON", BtnStyle(), 0, 0, 0, 0, IdByClass, Strings.T("BlacklistByClass"));
        _byProcess = Make("BUTTON", BtnStyle(), 0, 0, 0, 0, IdByProcess, Strings.T("BlacklistByProcess"));
        _toggle = Make("BUTTON", BtnStyle(), 0, 0, 0, 0, IdToggle, Strings.T("BlacklistToggle"));
        _remove = Make("BUTTON", BtnStyle(), 0, 0, 0, 0, IdRemove, Strings.T("BlacklistRemove"));
        _empty = Make("STATIC", Native.WS_CHILD | Native.WS_VISIBLE | ChildStyle, 0, 0, 0, 0, -1, "");

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
        // And where those numbers came from. The rows are not in the source any more - they are a line of the
        // font plus padding - so the font and the line height it measured as are the two numbers that say
        // whether the panel on screen is the panel the layout meant, and they are the ones to read when a
        // machine's text size or GUI font makes it look wrong. The window's own floor is on the same line.
        var rows = new Rows(_line96);
        Smallest(dpi, out int minW, out int minH);
        WindowFor(dpi, minW, minH, out int minTrackW, out int minTrackH);
        Log.Write($"blacklist panel: rows from a {_line96} unit line (header {rows.Header}, label {rows.Label}, " +
                  $"button {rows.Row}, hint {rows.Hint}, all of it but the lists {rows.Fixed}), " +
                  $"font \"{_fontFace}\" {_fontPoints:0.#} pt at {dpi} dpi, smallest drag {minTrackW}x{minTrackH} px");
        // And that is all: the window is up and this returns. It used to wait here, in a loop of its own, until
        // the panel was closed - see the note at the top of the file for what that cost. The program's loop is
        // the only one now, and it reaches the panel through IsDialogMessage the same way this loop did.
    }

    /// <summary>
    /// The class of a window, for the log: it is what names whatever is standing in front of the panel when a
    /// report says the panel is not there. Empty for a window that is not there at all.
    /// </summary>
    private static string ClassOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "(nothing)";
        var name = new System.Text.StringBuilder(64);
        Native.GetClassNameW(hwnd, name, name.Capacity);
        return name.Length == 0 ? "(no class)" : name.ToString();
    }

    /// <summary>
    /// Puts the window back where it can be seen, if the display it was on has gone or has shrunk out from
    /// under it.
    ///
    /// A monitor being unplugged, or changing resolution or scale, moves nothing that was already on screen:
    /// Windows leaves the window where it was, off the edge of every screen, where it can be neither read nor
    /// closed. This is the one thing about more than one display that this file does, and it is done at the
    /// moment the system says something about the displays - WM_DISPLAYCHANGE and the environment changing -
    /// rather than by asking. Nothing here polls, and nothing here draws: the window is moved and sized, which
    /// is the same call that opened it.
    ///
    /// The test is an overlap with the work area of the monitor nearest the window, not containment: a window
    /// hung half off the edge of a screen on purpose is where the user put it and stays there, and only one
    /// with nothing on any screen is moved.
    /// </summary>
    private static void PutBackOnScreen(IntPtr hwnd)
    {
        if (!Native.GetWindowRect(hwnd, out var r)) return;
        var here = WorkAreaOf(Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST));
        bool onAScreen = r.Right > here.Left && r.Left < here.Right && r.Bottom > here.Top && r.Top < here.Bottom;
        if (onAScreen) return;

        // The monitor the cursor is on, which is the one being looked at, and the size the layout asks for at
        // that monitor's scale. The same call the panel is opened with, so it comes back in the shape and the
        // place it would have opened in.
        Native.GetCursorPos(out var cursor);
        var target = WorkAreaOf(Native.MonitorFromPoint(cursor, Native.MONITOR_DEFAULTTONEAREST));
        uint dpi = Native.GetDpiForWindow(hwnd);
        if (dpi == 0) dpi = Native.GetDpiForSystem();
        Log.Write($"blacklist panel: {r.Left},{r.Top} is off every screen now (the display it was on changed); " +
                  $"putting it back on the one the cursor is on, at {dpi} dpi");
        FitToWorkArea(dpi, target);
    }

    /// <summary>
    /// The keyboard rules a window full of controls is owed by whoever is pumping the queue, which is the tray
    /// host's loop and no longer a loop of this file's own: Tab between the controls, the arrows and the page
    /// keys inside a list, Enter on the button that has the focus, and Esc.
    ///
    /// False when the message is not the panel's and the caller should dispatch it as it always would. The
    /// handle is not checked with IsWindow: it is cleared in WM_DESTROY, and a message cannot arrive for a
    /// window that is gone without that having happened first.
    /// </summary>
    public static bool IsDialogMessage(ref MSG msg)
        => _hwnd != IntPtr.Zero && Native.IsDialogMessageW(_hwnd, ref msg);

    /// <summary>
    /// Forgets the window and everything that was on it, called where the panel is known to be gone: WM_DESTROY
    /// for the ordinary case, and the stale-handle case in Show, which should not happen and is handled anyway.
    ///
    /// A method rather than a block in WM_DESTROY because a second call to Show would otherwise build a second
    /// set of controls and a second font over the first set's handles, with only one of them ever reachable.
    /// </summary>
    private static void Forget()
    {
        if (_font != IntPtr.Zero) Native.DeleteObject(_font);
        _font = IntPtr.Zero;
        _hwnd = _recentBox = _rulesBox = IntPtr.Zero;
        _byClass = _byProcess = _toggle = _remove = _empty = IntPtr.Zero;
        _recentLabel = _refresh = _ruleLabel = IntPtr.Zero;
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
    /// Destroys the panel when the tray host is destroyed, which is the lifetime that used to be carried by the
    /// owner argument and cannot be any more: being owned is what put the panel at the bottom of the Z-order
    /// with an invisible owner. Called from the host's own WM_DESTROY, which is the last moment anything of this
    /// program runs, so there is nothing left open behind the message loop and nothing is left for the kernel to
    /// do on the way out either. A program that is killed outright never gets here and does not need to: its
    /// windows go with the process.
    /// </summary>
    public static void DestroyWithHost()
    {
        if (_hwnd != IntPtr.Zero && Native.IsWindow(_hwnd)) Native.DestroyWindow(_hwnd);
    }

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
        _remove, _empty,
    };

    private static bool Register()
    {
        if (_registered) return true;
        _proc = WndProc;
        var wc = new WNDCLASSEXW
        {
            CbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            // Redraw the whole client area on a resize: this window is full of controls that move and change
            // size with it, and each of them repaints only itself. Without this the background they left behind
            // is never painted again, which is the other half of the fragments a resize used to leave.
            Style = Native.CS_HREDRAW | Native.CS_VREDRAW,
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

            case Native.WM_SIZE:
                // Dragged to another size, or given one by FitToWorkArea. Not while minimized: a minimized
                // window is zero by zero and laying the controls out at that size would be work for nothing.
                if (w.ToInt64() != Native.SIZE_MINIMIZED) Relayout(hwnd);
                return IntPtr.Zero;

            case Native.WM_GETMINMAXINFO:
            {
                // How small the user may drag it, from the layout's own arithmetic: the rows at the height of the
                // font this window has, and the lists at their shortest. Not sent before the font is made (the
                // window is created before it has one), and there is nothing to answer with until then.
                if (_line96 <= 0) return Native.DefWindowProcW(hwnd, msg, w, l);
                uint dpi = Native.GetDpiForWindow(hwnd);
                if (dpi == 0) dpi = Native.GetDpiForSystem();
                Smallest(dpi, out int minW, out int minH);
                WindowFor(dpi, minW, minH, out int minTrackW, out int minTrackH);
                // And how large: the display this window is on, less its taskbar. Left to Windows, the cap is
                // about the primary display, and a window on a bigger monitor would stop growing at the wrong
                // one. The monitor is asked for rather than remembered, because the window can be dragged to
                // another one between two drags of its corner.
                var mm = Marshal.PtrToStructure<Native.MINMAXINFO>(l);
                mm.MinTrackSize = new POINT { X = minTrackW, Y = minTrackH };
                var work = WorkAreaOf(Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST));
                mm.MaxTrackSize = new POINT { X = work.Right - work.Left, Y = work.Bottom - work.Top };
                Marshal.StructureToPtr(mm, l, false);
                return IntPtr.Zero;
            }

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
                // The metrics of the window frame itself, which is what a taskbar that grew, a scale that
                // changed or a display that went away all arrive as. The window may have been left on no
                // screen at all by it. See PutBackOnScreen.
                else if (area is "WindowMetrics" or "Display") PutBackOnScreen(hwnd);
                return IntPtr.Zero;
            }

            case Native.WM_DISPLAYCHANGE:
                // A display was added, removed or resized. Sent to every top-level window, so this one hears it
                // directly - and it is the only other moment at which this file does anything about where it is.
                PutBackOnScreen(hwnd);
                return IntPtr.Zero;

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
                Relayout(hwnd);
                // Re-adding the rows is what makes the list boxes measure their items again at the new scale,
                // which they do from the font and not from anything this file passes them.
                Fill();
                Log.Write($"blacklist panel: moved to a display at {newDpi} dpi, line {_line96} units");
                return IntPtr.Zero;
            }

            case Native.WM_CLOSE:
                // Written down because this is the only way the panel is supposed to go, and anything else that
                // closes it has to be found rather than guessed at: the title bar's X and the system menu's
                // Close both come through here, and so would anyone else's WM_CLOSE - which is exactly what a
                // line in the log tells apart. Alt+F4 sends SC_CLOSE to the same end.
                Log.Write("blacklist panel: WM_CLOSE - the X, or something that sent one");
                _closing = true;
                Native.DestroyWindow(hwnd);
                return IntPtr.Zero;

            case Native.WM_DESTROY:
                // Deliberately no PostQuitMessage here. There used to be a nested loop that ended because the
                // window it tested with IsWindow had gone, and a quit posted here would have been picked up by
                // the wrong loop; now there is one loop and it is not told anything: WM_QUIT arrives on its own
                // when the program is asked to leave, and this window's going away is not that.
                //
                // Which of the two ways it went, said out loud, because they mean different things: a WM_CLOSE
                // is the user's X, and no WM_CLOSE is either the owned window going with its owner - the
                // program leaving, closed or crashed - or a DestroyWindow from somewhere in this program that
                // has no business touching this window. Nothing else reaches here.
                Log.Write(_closing
                    ? "blacklist panel: closed, by a WM_CLOSE"
                    : "blacklist panel: closed without a WM_CLOSE - its owner going, or a DestroyWindow of its own");
                _closing = false;
                Forget();
                return IntPtr.Zero;

            default:
                return Native.DefWindowProcW(hwnd, msg, w, l);
        }
    }
}
