// The window that edits the advertisement's re-trigger rule, and the only window in this program with a
// control that is not a stock one - two fields that are not allowed to hold anything but a number.
//
// It is a window of its own rather than a dialog box because a message box has no room in it for a drop-down
// and two text fields: this program has no WinForms, so the controls are Win32 ones made here, the mask is a
// function over text (AdRule.FixIncrease and AdRule.FixReset), and everything else - the font, the scale, the
// dark theme, the tab order - is the same machinery the blacklist panel uses, for the same reasons.
//
// Two decisions worth reading before the code:
//
//   * There is a Reset button and no Save button, because there is nothing to save: every change is written to
//     the settings as it is made, so the ini always holds what the window shows and the title bar's X is the
//     only way out. A field that is still being typed in is not a number yet, and what the settings hold then
//     is the default - see AdRule.IncreaseOf, which is where that rule lives so that it can be tested.
//   * The window is not modal and has no loop of its own. The blacklist panel taught this file that lesson:
//     the program's loop is the one that pumps, and a window of controls joins it through IsDialogMessage.
//
// The controls, in the order the question reads:
//
//     Every [minute v] add [01.00]% chance to re-trigger the ad
//     After an ad plays, reset the chance to [000]%
//                                                    [ Reset ]
//
// The two fields are EDITs of a fixed width - five characters and three - and the paragraph around them is
// STATICs, so the row is as wide as its own text: the layout measures each fragment with the font it is
// actually drawn in rather than adding up design units and hoping.

using System.Runtime.InteropServices;
using System.Text;
using Unit = Moa.AdRule.Unit;

namespace Moa;

internal static class AdRuleDialog
{
    private const string ClassName = "MobileOpenAnimationAdRule";

    /// <summary>
    /// A title bar and a close box, and nothing else: the window is two lines and a button at a fixed size, so
    /// there is no frame to drag and no maximize box to be useful. CLIPCHILDREN because the fields repaint
    /// themselves and this window never draws over them.
    /// </summary>
    private const uint Style = (uint)(Native.WS_CAPTION | Native.WS_SYSMENU | Native.WS_CLIPCHILDREN);

    private const uint ChildStyle = Native.WS_CLIPSIBLINGS;

    // What every control is given on top of its own style. The two fields take the keyboard, so they are
    // tab stops, and so are the drop-down and the button: the whole window is reachable without a mouse.
    private const uint TabStop = Native.WS_TABSTOP | Native.WS_VISIBLE;

    private const int IdUnit = 100, IdIncrease = 101, IdResetValue = 102, IdResetButton = 103;

    private const int CbnSelChange = 1;
    private const int EnChange = 0x0300;

    /// <summary>The design, in 96 DPI units: the side margins, the gaps, and the width of the two fields - the
    /// only numbers here that are a choice rather than a measurement, because a field is as wide as the mask it
    /// holds and no wider.</summary>
    private const int Pad = 10, Gap = 5, Field5 = 56, Field3 = 42, Combo = 96, RowGap = 8;

    private static Native.WndProcDelegate? _proc;
    private static bool _registered;

    private static IntPtr _hwnd, _every, _unit, _add, _increase, _chance, _after, _resetValue, _afterSuffix, _reset;
    private static IntPtr _font;
    private static int _line96;

    /// <summary>
    /// Set while this file writes a field itself. The fix a keystroke causes is a second change notification,
    /// and acting on it would put the caret where the fix left it - which is at the end either way, but the
    /// settings would be written twice per character for no reason.
    /// </summary>
    private static bool _fixing;

    private static bool _closing;

    /// <summary>
    /// Shows the window, or brings the one that is already up to the front. Called from the tray menu, and the
    /// menu is only ever offered this entry while the advertisement is switched on - see the menu itself.
    /// </summary>
    public static void Show()
    {
        if (_hwnd != IntPtr.Zero)
        {
            if (Native.IsWindow(_hwnd))
            {
                if (Native.WindowIsMinimized(_hwnd)) Native.ShowWindow(_hwnd, Native.SW_RESTORE);
                else if (!Native.IsWindowVisible(_hwnd)) Native.ShowWindow(_hwnd, Native.SW_SHOW);
                Native.SetWindowPos(_hwnd, Native.HWND_TOP, 0, 0, 0, 0,
                                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                Native.SetForegroundWindow(_hwnd);
                return;
            }
            Log.Write("ad rule: the handle outlived its window; starting again");
            Forget();
        }
        if (!Register()) return;

        // The monitor the cursor is on - the one the tray menu was on, and therefore the one being looked at -
        // and its work area, which is what keeps the window on a screen. Created at that monitor's corner and
        // hidden: the size is worked out below, from the text and the font, and a window cannot be asked about
        // either of those before it exists.
        Native.GetCursorPos(out var cursor);
        var work = WorkAreaOf(Native.MonitorFromPoint(cursor, Native.MONITOR_DEFAULTTONEAREST));

        _hwnd = Native.CreateWindowExW((uint)Native.WS_EX_TOOLWINDOW, ClassName, Strings.T("AdRule"),
            Style, work.Left, work.Top, 200, 200, IntPtr.Zero, IntPtr.Zero,
            Native.GetModuleHandleW(null), IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) return;

        uint dpi = Native.GetDpiForWindow(_hwnd);
        if (dpi == 0) dpi = Native.GetDpiForSystem();

        IntPtr Make(string cls, uint st, int id, string text, uint extra = 0)
        {
            IntPtr c = Native.CreateWindowExW(0, cls, text, st | extra, 0, 0, 0, 0,
                                              _hwnd, (IntPtr)id, Native.GetModuleHandleW(null), IntPtr.Zero);
            if (c != IntPtr.Zero && _font != IntPtr.Zero)
                Native.SendMessageW(c, Native.WM_SETFONT, _font, (IntPtr)1);
            return c;
        }

        uint EditStyle() => Native.WS_CHILD | TabStop | Native.WS_BORDER | Native.ES_AUTOHSCROLL | ChildStyle;
        uint StaticStyle() => Native.WS_CHILD | Native.WS_VISIBLE | ChildStyle;

        ReFont(dpi);
        _every = Make("STATIC", StaticStyle(), -1, Strings.T("AdRuleEvery"));
        _unit = Make("COMBOBOX", Native.WS_CHILD | TabStop | Native.CBS_DROPDOWNLIST | Native.WS_VSCROLL |
                                ChildStyle, IdUnit, "");
        _add = Make("STATIC", StaticStyle(), -1, Strings.T("AdRuleAdd"));
        _increase = Make("EDIT", EditStyle(), IdIncrease, AdRule.ShowIncrease(Program.S.AdRuleIncrease));
        _chance = Make("STATIC", StaticStyle(), -1, Strings.T("AdRuleChance"));
        _after = Make("STATIC", StaticStyle(), -1, Strings.T("AdRuleAfter"));
        _resetValue = Make("EDIT", EditStyle() | Native.ES_NUMBER, IdResetValue, AdRule.ShowReset(Program.S.AdRuleReset));
        _afterSuffix = Make("STATIC", StaticStyle(), -1, "%");
        _reset = Make("BUTTON", Native.WS_CHILD | TabStop | ChildStyle, IdResetButton, Strings.T("AdRuleReset"));

        foreach (Unit u in new[] { Unit.Second, Unit.Minute, Unit.Hour, Unit.Day })
            Native.SendMessageTextW(_unit, Native.CB_ADDSTRING, IntPtr.Zero, Strings.T(UnitString(u)));
        Native.SendMessageW(_unit, Native.CB_SETCURSEL, (IntPtr)Index(CurrentUnit()), IntPtr.Zero);

        Layout(dpi);
        Native.DarkMode.Style(_hwnd);
        Native.DarkMode.Frame(_hwnd);
        IntPtr background = Native.DarkMode.BackgroundBrush();
        Native.SetClassBackground(_hwnd, background != IntPtr.Zero ? background
                                                                  : (IntPtr)(Native.COLOR_WINDOW + 1));
        foreach (var c in AllControls()) Native.DarkMode.Style(c);
        FitToWorkArea(dpi, work);

        Native.ShowWindow(_hwnd, Native.SW_SHOW);
        Native.SetFocus(_increase);
        Log.Write($"ad rule: shown at {dpi} dpi, every {Program.S.AdRuleUnit} " +
                  $"+{Program.S.AdRuleIncrease}%, reset to {Program.S.AdRuleReset}%");
    }

    /// <summary>
    /// The font, and the line height the layout is made of. The same arithmetic as the blacklist panel's, and
    /// for the same reason: this window may be on a display whose scale is not the system's, and its text has to
    /// be the system font's size at that scale rather than its pixels at another.
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
            double points = ui.SizeInPoints * dpi / system;
            IntPtr made = Native.CreateFontW(-(int)Math.Round(points * dpi / 72.0), 0, 0, 0,
                                             ui.Bold ? 700 : 400, ui.Italic ? 1u : 0u, 0, 0, ui.GdiCharSet,
                                             0, 0, 0, 0, ui.Name);
            line96 = LineOf(made, dpi);
            if (line96 > 0) return made;
            if (made != IntPtr.Zero) Native.DeleteObject(made);
        }
        catch (Exception e)
        {
            Log.Write($"ad rule: the system font could not be read ({e.GetType().Name}); asking for one instead");
        }
        IntPtr font = Native.CreateFontW(-Native.MulDiv(9, (int)dpi, 72), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 0, 0,
                                         "Segoe UI");
        line96 = LineOf(font, dpi);
        return line96 > 0 ? font : IntPtr.Zero;
    }

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
    /// Places the controls and sizes the window around them, in physical pixels at the window's scale.
    ///
    /// The width of each fragment is measured with the font it is drawn in, so the row is exactly as wide as
    /// its own text and the window does not depend on a translated string being shorter than the one it was
    /// designed with. The height is two rows of the font plus the button and the margins.
    /// </summary>
    private static void Layout(uint dpi)
    {
        if (_line96 <= 0) return;
        int line = Native.MulDiv(_line96, (int)dpi, 96);
        int pad = Native.MulDiv(Pad, (int)dpi, 96);
        int gap = Native.MulDiv(Gap, (int)dpi, 96);
        int rowGap = Native.MulDiv(RowGap, (int)dpi, 96);
        int fieldH = line + Native.MulDiv(6, (int)dpi, 96);   // a field is a line plus its own border
        int comboW = Native.MulDiv(Combo, (int)dpi, 96);

        // One line of text, vertically centred in a row as tall as a field: a static is shorter than an edit,
        // and a row whose text sits at the top of its own field is the one thing that makes a form look wrong.
        int row1Y = pad + (fieldH - line) / 2;
        int x = pad;
        Place(_every, ref x, row1Y, TextWidth(_every), line);
        Place(_unit, ref x, pad, comboW, fieldH);
        Place(_add, ref x, row1Y, TextWidth(_add), line);
        Place(_increase, ref x, pad, Native.MulDiv(Field5, (int)dpi, 96), fieldH);
        Place(_chance, ref x, row1Y, TextWidth(_chance), line);
        int row1Right = x;

        int row2Y = pad + fieldH + rowGap;
        int x2 = pad;
        Place(_after, ref x2, row2Y + (fieldH - line) / 2, TextWidth(_after), line);
        Place(_resetValue, ref x2, row2Y, Native.MulDiv(Field3, (int)dpi, 96), fieldH);
        Place(_afterSuffix, ref x2, row2Y + (fieldH - line) / 2, TextWidth(_afterSuffix), line);
        int row2Right = x2;

        // And the Reset button under them, right-aligned: it acts on the whole window rather than on the field
        // above it, so it belongs at the end of the rows rather than beside them.
        int buttonH = line + Native.MulDiv(12, (int)dpi, 96);
        int buttonW = TextWidth(_reset) + Native.MulDiv(28, (int)dpi, 96);
        int width = Math.Max(row1Right, row2Right) + pad;
        int height = row2Y + fieldH + rowGap + buttonH + pad;
        Native.SetWindowPos(_reset, IntPtr.Zero, width - pad - buttonW, row2Y + fieldH + rowGap,
                            buttonW, buttonH, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);

        var rect = new RECT { Left = 0, Top = 0, Right = width, Bottom = height };
        Native.AdjustWindowRectEx(ref rect, Style, false, (uint)Native.WS_EX_TOOLWINDOW);
        Native.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, rect.Right - rect.Left, rect.Bottom - rect.Top,
                            Native.SWP_NOMOVE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);

        void Place(IntPtr c, ref int at, int y, int w, int h)
        {
            if (c == IntPtr.Zero) return;
            Native.SetWindowPos(c, IntPtr.Zero, at, y, w, h, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            at += w + gap;
        }
    }

    /// <summary>The width of a control's own text, in pixels, with the font it is drawn in.</summary>
    private static int TextWidth(IntPtr c)
    {
        var sb = new StringBuilder(256);
        Native.GetWindowTextW(c, sb, sb.Capacity);
        IntPtr dc = Native.GetDC(c);
        if (dc == IntPtr.Zero) return 0;
        IntPtr old = Native.SelectObject(dc, _font);
        bool read = Native.GetTextExtentPoint32W(dc, sb.ToString(), sb.Length, out var size);
        if (old != IntPtr.Zero) Native.SelectObject(dc, old);
        Native.ReleaseDC(c, dc);
        return read ? size.Cx : 0;
    }

    /// <summary>
    /// Keeps the window inside the work area of the monitor it was made on: a window whose title bar is off the
    /// top of the screen cannot be moved, and the two lines it shows are not worth that.
    /// </summary>
    private static void FitToWorkArea(uint dpi, RECT work)
    {
        Native.GetWindowRect(_hwnd, out var r);
        int w = Math.Min(r.Right - r.Left, work.Right - work.Left);
        int h = Math.Min(r.Bottom - r.Top, work.Bottom - work.Top);
        int x = Math.Max(work.Left, Math.Min(work.Left + Native.MulDiv(80, (int)dpi, 96), work.Right - w));
        int y = Math.Max(work.Top, Math.Min(work.Top + Native.MulDiv(80, (int)dpi, 96), work.Bottom - h));
        Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, w, h, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    private static RECT WorkAreaOf(IntPtr monitor)
    {
        var info = new Native.MONITORINFO { CbSize = (uint)Marshal.SizeOf<Native.MONITORINFO>() };
        return Native.GetMonitorInfoW(monitor, ref info) ? info.RcWork : new RECT
        {
            Left = 0, Top = 0,
            Right = Native.GetSystemMetrics(Native.SM_CXSCREEN),
            Bottom = Native.GetSystemMetrics(Native.SM_CYSCREEN),
        };
    }

    /// <summary>The four unit names, as the strings table knows them.</summary>
    private static string UnitString(Unit unit) => unit switch
    {
        Unit.Second => "AdRuleUnitSecond",
        Unit.Hour => "AdRuleUnitHour",
        Unit.Day => "AdRuleUnitDay",
        _ => "AdRuleUnitMinute",
    };

    /// <summary>The unit in the settings, falling back to the default for a name that is not one of the four.</summary>
    private static Unit CurrentUnit()
    {
        AdRule.TryParse(Program.S.AdRuleUnit, out var unit);
        return unit;
    }

    private static int Index(Unit unit) => unit switch
    {
        Unit.Second => 0,
        Unit.Hour => 2,
        Unit.Day => 3,
        _ => 1,
    };

    /// <summary>The unit at a drop-down index, and the default for an index that is not one.</summary>
    private static Unit At(int index) => index switch
    {
        0 => Unit.Second,
        2 => Unit.Hour,
        3 => Unit.Day,
        _ => Unit.Minute,
    };

    /// <summary>
    /// Writes what the window shows into the settings, and the settings to the ini.
    ///
    /// Called from every change - a keystroke, a choice from the drop-down, the Reset button - which is what
    /// "there is no Save button" means. A field that is not complete is written as the default rather than as
    /// half a number, which is the rule AdRule owns so that it can be tested without this window.
    /// </summary>
    private static void Store()
    {
        var s = Program.S;
        s.AdRuleIncrease = AdRule.IncreaseOf(Read(_increase), AdRule.DefaultIncrease);
        s.AdRuleReset = AdRule.ResetOf(Read(_resetValue), AdRule.DefaultReset);
        s.AdRuleUnit = AdRule.Name(At(Selected(_unit)));
        s.Save();
        Log.Write($"ad rule: every {s.AdRuleUnit} +{s.AdRuleIncrease}%, reset to {s.AdRuleReset}% " +
                  $"(fields: \"{Read(_increase)}\" and \"{Read(_resetValue)}\")");
    }

    private static string Read(IntPtr c)
    {
        var sb = new StringBuilder(64);
        Native.GetWindowTextW(c, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>
    /// Puts back the defaults, in the settings and on the window. The defaults are AdRule's own, so "Reset"
    /// means the same thing here as it does in a fresh ini.
    /// </summary>
    private static void ResetToDefaults()
    {
        var s = Program.S;
        s.AdRuleUnit = AdRule.Name(AdRule.DefaultUnit);
        s.AdRuleIncrease = AdRule.DefaultIncrease;
        s.AdRuleReset = AdRule.DefaultReset;
        s.Save();

        _fixing = true;
        Native.SetWindowTextW(_increase, AdRule.ShowIncrease(s.AdRuleIncrease));
        Native.SetWindowTextW(_resetValue, AdRule.ShowReset(s.AdRuleReset));
        Native.SendMessageW(_unit, Native.CB_SETCURSEL, (IntPtr)Index(AdRule.DefaultUnit), IntPtr.Zero);
        _fixing = false;

        Log.Write($"ad rule: reset to every {s.AdRuleUnit} +{s.AdRuleIncrease}%, reset to {s.AdRuleReset}%");
    }

    /// <summary>
    /// A field was typed in. What is allowed to be in it is AdRule's business; this is where the text it
    /// refuses is taken back out, with the caret left at the end because that is where the user is typing.
    /// </summary>
    private static void OnFieldChanged(int id)
    {
        if (_fixing) return;
        IntPtr field = id == IdIncrease ? _increase : _resetValue;
        string was = Read(field);
        string now = id == IdIncrease ? AdRule.FixIncrease(was) : AdRule.FixReset(was);

        if (now != was)
        {
            _fixing = true;
            Native.SetWindowTextW(field, now);
            Native.SendMessageW(field, Native.EM_SETSEL, (IntPtr)(-1), (IntPtr)(-1));
            _fixing = false;
        }
        Store();
    }

    private static int Selected(IntPtr box)
    {
        long i = Native.SendMessageW(box, Native.CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero).ToInt64();
        return i < 0 || i > int.MaxValue ? -1 : (int)i;
    }

    private static void OnCommand(int id, int note)
    {
        switch (id)
        {
            case IdIncrease:
            case IdResetValue:
                if (note == EnChange) OnFieldChanged(id);
                return;
            case IdUnit:
                if (note == CbnSelChange) Store();
                return;
            case IdResetButton:
                ResetToDefaults();
                return;
        }
    }

    /// <summary>
    /// True while the window is up, so the program's loop can hand it the messages a dialog gets for free - Tab
    /// between the controls, Enter on the button that has the focus, Esc - before dispatching anything. False
    /// for every message that is not the window's, which is every message while it is closed.
    /// </summary>
    public static bool IsDialogMessage(ref MSG msg)
        => _hwnd != IntPtr.Zero && Native.IsDialogMessageW(_hwnd, ref msg);

    /// <summary>Destroys the window when the tray host goes, so nothing of this program is left open.</summary>
    public static void DestroyWithHost()
    {
        if (_hwnd != IntPtr.Zero && Native.IsWindow(_hwnd)) Native.DestroyWindow(_hwnd);
    }

    private static void Forget()
    {
        if (_font != IntPtr.Zero) Native.DeleteObject(_font);
        _font = IntPtr.Zero;
        _hwnd = _every = _unit = _add = _increase = _chance = _after = _resetValue = _afterSuffix = _reset =
            IntPtr.Zero;
        _line96 = 0;
    }

    private static IntPtr[] AllControls() => new[]
    {
        _every, _unit, _add, _increase, _chance, _after, _resetValue, _afterSuffix, _reset,
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
        if (!_registered) Log.Write("ad rule: RegisterClassExW failed");
        return _registered;
    }

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
    {
        switch (msg)
        {
            case Native.WM_COMMAND:
                OnCommand((int)(w.ToInt64() & 0xFFFF), (int)((w.ToInt64() >> 16) & 0xFFFF));
                return IntPtr.Zero;

            case Native.WM_CTLCOLORSTATIC:
            {
                IntPtr brush = Native.DarkMode.BackgroundBrush();
                if (brush == IntPtr.Zero) return Native.DefWindowProcW(hwnd, msg, w, l);
                Native.SetTextColor(w, Native.DarkMode.DarkTextColour);
                Native.SetBkColor(w, 0x202020);
                return brush;
            }

            case Native.WM_SETTINGCHANGE:
                if ((l == IntPtr.Zero ? "" : Marshal.PtrToStringUni(l)) == "ImmersiveColorSet")
                    Native.DarkMode.Apply();
                return IntPtr.Zero;

            case Native.WM_DPICHANGED:
            {
                // Moved to a display at another scale: Windows proposes the rectangle, and everything is built
                // again from the new one - the font first, because the layout is made of its line height.
                uint newDpi = (uint)(w.ToInt64() & 0xFFFF);
                var suggested = Marshal.PtrToStructure<RECT>(l);
                Native.SetWindowPos(hwnd, IntPtr.Zero, suggested.Left, suggested.Top,
                                    suggested.Right - suggested.Left, suggested.Bottom - suggested.Top,
                                    Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                if (newDpi == 0) newDpi = Native.GetDpiForWindow(hwnd);
                ReFont(newDpi);
                Layout(newDpi);
                return IntPtr.Zero;
            }

            case Native.WM_CLOSE:
                // The title bar's X and the system menu's Close, which is the whole of how this window is meant
                // to go: there is nothing to save on the way out, so it is destroyed rather than hidden.
                Log.Write("ad rule: WM_CLOSE - the X, or something that sent one");
                _closing = true;
                Native.DestroyWindow(hwnd);
                return IntPtr.Zero;

            case Native.WM_DESTROY:
                Log.Write(_closing ? "ad rule: closed, by a WM_CLOSE"
                                   : "ad rule: closed without a WM_CLOSE - the program going away");
                _closing = false;
                Forget();
                return IntPtr.Zero;

            default:
                return Native.DefWindowProcW(hwnd, msg, w, l);
        }
    }
}
