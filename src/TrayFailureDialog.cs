// The dialog shown when the tray icon cannot be registered: the one place this program says it cannot run and
// offers the two ways on, with a countdown on the exit button so a dialog that cannot be seen still lets the
// program go.
//
// It is a window of this program's own rather than a task dialog for one reason: a task dialog's custom buttons
// are not windows (probe: GetDlgItem on one returns 0), so their text cannot be changed while the dialog is up,
// and the countdown has to be written into the button itself. Everything else is the same arrangement as the
// blacklist panel and the rule window: stock STATIC and BUTTON controls, ToolWindow's font, theme and placement,
// and nothing drawn by this file.
//
// The countdown is the safeguard, not decoration. A tray program has no window of its own; if this box never
// becomes visible - covered, on a display that went away, or simply lost behind something - the user cannot
// click it, and the program would be running with no way to reach it while the single-instance mutex keeps the
// shortcut from starting another copy. So the exit happens on a wall-clock deadline rather than on a click:
// thirty seconds after the box is built, or at once if the clock has moved past the deadline (a system clock
// correction), the dialog closes and the process ends.

using System.Globalization;
using System.Runtime.InteropServices;

namespace Moa;

internal static class TrayFailureDialog
{
    private const string ClassName = "MobileOpenAnimationTrayFailure";

    /// <summary>
    /// A title bar and a close box, and nothing else: the body is a paragraph and two buttons. CLIPCHILDREN
    /// because the controls repaint themselves and this window never draws over them. No WS_EX_TOOLWINDOW, so
    /// it also has a taskbar button - a box whose whole point is that it can be missed should be findable in
    /// the place a user looks for a program that will not start.
    /// </summary>
    private const uint Style = (uint)(Native.WS_CAPTION | Native.WS_SYSMENU | Native.WS_CLIPCHILDREN);

    private const uint ChildStyle = Native.WS_CLIPSIBLINGS;
    private const uint TabStop = Native.WS_TABSTOP | Native.WS_VISIBLE;

    private const uint SsIcon = 0x00000003, SsNoPrefix = 0x00000080, BsDefPush = 0x00000001,
                       StmSetIcon = 0x0170;
    private const uint DtCalcRect = 0x0400, DtWordBreak = 0x0010, DtNoPrefix = 0x0800;

    private const int IdRestart = 100, IdExit = 101, IdTick = 1;
    private const int IdOk = 1, IdCancel = 2;

    /// <summary>The design, in 96 DPI units: the margins, the icon, the width the paragraph is given, and the
    /// padding inside a button. The height of everything is measured from the font and from the text itself.</summary>
    private const int Pad = 16, Gap = 10, IconSize = 32, BodyWidth = 520, ButtonPad = 28;

    /// <summary>Seconds until the dialog closes itself. See the note at the top of the file.</summary>
    private const int Seconds = 30;

    private static Native.WndProcDelegate? _proc;
    private static bool _registered;

    private static IntPtr _hwnd, _icon, _text, _restart, _exit, _font;
    private static int _line96;
    private static DateTime _deadline;
    private static bool _elevate;

    /// <summary>
    /// Shows the box and returns what the user chose: true means "restart as administrator", false means
    /// "leave". It does not return until the box is gone, and the box goes by a click, the X, Esc, or the
    /// countdown reaching nought.
    /// </summary>
    public static bool Show(string summary)
    {
        if (!Register()) return false;

        // The monitor the cursor is on, which is the one being looked at, and its work area, which is what the
        // box has to fit into.
        Native.GetCursorPos(out var cursor);
        var work = ToolWindow.WorkAreaOf(Native.MonitorFromPoint(cursor, Native.MONITOR_DEFAULTTONEAREST));

        _hwnd = Native.CreateWindowExW(0, ClassName, Strings.T("TrayFailedTitle"), Style,
                                       work.Left, work.Top, 200, 200, IntPtr.Zero, IntPtr.Zero,
                                       Native.GetModuleHandleW(null), IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) return false;

        uint dpi = Native.GetDpiForWindow(_hwnd);
        if (dpi == 0) dpi = Native.GetDpiForSystem();
        ReFont(dpi);

        string body = Strings.T("TrayFailedAsk") + "\n\n" + Strings.T("TrayFailedAdvice") +
                      "\n\n" + summary;

        _icon = Make("STATIC", Native.WS_CHILD | Native.WS_VISIBLE | SsIcon | ChildStyle, -1, "");
        _text = Make("STATIC", Native.WS_CHILD | Native.WS_VISIBLE | SsNoPrefix | ChildStyle, -1, body);
        _restart = Make("BUTTON", Native.WS_CHILD | TabStop | BsDefPush | ChildStyle, IdRestart,
                        Strings.T("RestartAdmin"));
        _exit = Make("BUTTON", Native.WS_CHILD | TabStop | ChildStyle, IdExit, ExitLabel(Seconds));

        // The red cross, not the app icon and not the blue information mark: this box appears because something
        // has failed, and the shape of the icon is the one thing that is read before the text is.
        if (_icon != IntPtr.Zero)
            Native.SendMessageW(_icon, StmSetIcon, Native.LoadIconW(IntPtr.Zero, Native.IDI_ERROR), IntPtr.Zero);

        Layout(dpi, work);
        ToolWindow.Theme(_hwnd, AllControls());
        ToolWindow.Centre(_hwnd, work);

        _elevate = false;
        _deadline = DateTime.Now.AddSeconds(Seconds);
        UpdateCountdown();

        Native.ShowWindow(_hwnd, Native.SW_SHOW);
        Native.SetForegroundWindow(_hwnd);
        Native.SetFocus(_restart);
        Native.SetTimer(_hwnd, (IntPtr)IdTick, 1000, IntPtr.Zero);

        // A loop of this box's own, because there is no program loop yet: this runs before the tray host is
        // made, and it is the countdown's ticks that keep the deadline alive while nothing else is looking.
        while (Native.GetMessageW(out MSG msg, IntPtr.Zero, 0, 0))
        {
            if (!Native.IsDialogMessageW(_hwnd, ref msg))
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessageW(ref msg);
            }
        }
        return _elevate;
    }

    /// <summary>
    /// Writes the seconds left into the button, and closes the box when the deadline has passed.
    ///
    /// The deadline is wall-clock and is compared rather than counted, which is what makes a clock correction
    /// safe: moving the clock forward past it ends the box at once instead of leaving it up for hours, and the
    /// display is clamped to the full count so a clock moved backwards cannot show a larger number than the
    /// countdown started with.
    /// </summary>
    private static void UpdateCountdown()
    {
        if (_hwnd == IntPtr.Zero) return;
        double left = (_deadline - DateTime.Now).TotalSeconds;
        int seconds = (int)Math.Floor(left);
        if (seconds > Seconds) seconds = Seconds;
        if (seconds < 0) seconds = 0;
        Native.SetWindowTextW(_exit, ExitLabel(seconds));
        if (left <= 0) Native.DestroyWindow(_hwnd);
    }

    private static string ExitLabel(int seconds)
        => string.Format(CultureInfo.InvariantCulture, Strings.T("TrayExitCountdown"), seconds);

    /// <summary>
    /// The font every control is given, and the line height the layout is built from. ToolWindow's, so this box
    /// measures its rows the same way the two settings windows do.
    /// </summary>
    private static void ReFont(uint dpi)
    {
        IntPtr font = ToolWindow.UiFont(dpi, out int line96, out _, out _, "tray failure");
        if (font == IntPtr.Zero) return;
        _line96 = line96;
        IntPtr old = _font;
        _font = font;
        foreach (var c in AllControls())
            if (c != IntPtr.Zero) Native.SendMessageW(c, Native.WM_SETFONT, font, (IntPtr)1);
        if (old != IntPtr.Zero) Native.DeleteObject(old);
    }

    /// <summary>
    /// Places the controls and sizes the window around them, in physical pixels at the window's scale.
    ///
    /// The paragraph's height is measured off the font and the text with the same call that will draw it, so a
    /// longer translation or a bigger GUI font makes a taller box instead of clipping the advice. The body
    /// width is the design width unless the monitor is narrower, in which case it narrows and the text grows
    /// downwards.
    /// </summary>
    private static void Layout(uint dpi, RECT work)
    {
        if (_line96 <= 0) return;
        int line = Native.MulDiv(_line96, (int)dpi, 96);
        int pad = Native.MulDiv(Pad, (int)dpi, 96);
        int gap = Native.MulDiv(Gap, (int)dpi, 96);
        int icon = Native.MulDiv(IconSize, (int)dpi, 96);
        int buttonH = line + Native.MulDiv(14, (int)dpi, 96);

        int textW = Native.MulDiv(BodyWidth, (int)dpi, 96);
        int maxTextW = work.Right - work.Left - 2 * pad - icon - gap;
        if (textW > maxTextW) textW = Math.Max(Native.MulDiv(200, (int)dpi, 96), maxTextW);
        int textH = MeasureText(textW);

        int restartW = TextWidth(_restart) + Native.MulDiv(ButtonPad, (int)dpi, 96);
        int exitW = TextWidth(_exit) + Native.MulDiv(ButtonPad, (int)dpi, 96);

        int bodyH = Math.Max(textH, icon);
        int clientW = pad + icon + gap + textW + pad;
        clientW = Math.Max(clientW, 2 * pad + restartW + gap + exitW);
        int clientH = pad + bodyH + pad + buttonH + pad;

        Place(_icon, pad, pad, icon, icon);
        Place(_text, pad + icon + gap, pad, textW, Math.Max(textH, line));
        int buttonY = pad + bodyH + pad;
        Place(_exit, clientW - pad - exitW, buttonY, exitW, buttonH);
        Place(_restart, clientW - pad - exitW - gap - restartW, buttonY, restartW, buttonH);

        var frame = new RECT { Right = clientW, Bottom = clientH };
        Native.AdjustWindowRectEx(ref frame, Style, false, 0);
        Native.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, frame.Right - frame.Left, frame.Bottom - frame.Top,
                            Native.SWP_NOMOVE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    private static void Place(IntPtr c, int x, int y, int w, int h)
    {
        if (c == IntPtr.Zero) return;
        Native.SetWindowPos(c, IntPtr.Zero, x, y, w, h,
                            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOCOPYBITS);
    }

    /// <summary>How tall the body paragraph is when it is wrapped to this width, in pixels.</summary>
    private static int MeasureText(int width)
    {
        IntPtr dc = Native.GetDC(_text);
        if (dc == IntPtr.Zero) return 0;
        IntPtr old = Native.SelectObject(dc, _font);
        var rect = new RECT { Right = width };
        int height = Native.DrawTextW(dc, Read(_text), -1, ref rect, DtCalcRect | DtWordBreak | DtNoPrefix);
        if (old != IntPtr.Zero) Native.SelectObject(dc, old);
        Native.ReleaseDC(_text, dc);
        return Math.Max(height, 0);
    }

    /// <summary>The width of a control's own text, in pixels, with the font it is drawn in.</summary>
    private static int TextWidth(IntPtr c)
    {
        if (c == IntPtr.Zero) return 0;
        IntPtr dc = Native.GetDC(c);
        if (dc == IntPtr.Zero) return 0;
        IntPtr old = Native.SelectObject(dc, _font);
        string text = Read(c);
        bool read = Native.GetTextExtentPoint32W(dc, text, text.Length, out var size);
        if (old != IntPtr.Zero) Native.SelectObject(dc, old);
        Native.ReleaseDC(c, dc);
        return read ? size.Cx : 0;
    }

    private static string Read(IntPtr c)
    {
        var sb = new System.Text.StringBuilder(1024);
        Native.GetWindowTextW(c, sb, sb.Capacity);
        return sb.ToString();
    }

    private static IntPtr Make(string cls, uint style, int id, string text)
    {
        IntPtr c = Native.CreateWindowExW(0, cls, text, style, 0, 0, 0, 0, _hwnd, (IntPtr)id,
                                          Native.GetModuleHandleW(null), IntPtr.Zero);
        if (c != IntPtr.Zero && _font != IntPtr.Zero)
            Native.SendMessageW(c, Native.WM_SETFONT, _font, (IntPtr)1);
        return c;
    }

    private static IntPtr[] AllControls() => new[] { _icon, _text, _restart, _exit };

    private static bool Register()
    {
        if (_registered) return true;
        _proc = WndProc;
        _registered = ToolWindow.Register(ClassName, _proc, false, "tray failure");
        return _registered;
    }

    /// <summary>
    /// Drops the handles and the font. The answer is not dropped: `_elevate` is what Show returns, and
    /// WM_DESTROY runs before it does.
    /// </summary>
    private static void Forget()
    {
        if (_font != IntPtr.Zero) Native.DeleteObject(_font);
        _font = IntPtr.Zero;
        _hwnd = _icon = _text = _restart = _exit = IntPtr.Zero;
        _line96 = 0;
    }

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
    {
        switch (msg)
        {
            case Native.WM_COMMAND:
            {
                int id = (int)(w.ToInt64() & 0xFFFF);
                if (id == IdRestart || id == IdOk) _elevate = true;
                if (id == IdRestart || id == IdOk || id == IdExit || id == IdCancel)
                    Native.DestroyWindow(hwnd);
                return IntPtr.Zero;
            }

            case Native.WM_TIMER:
                if (w == (IntPtr)IdTick) UpdateCountdown();
                return IntPtr.Zero;

            case Native.WM_CTLCOLORSTATIC:
                return ToolWindow.StaticColour(hwnd, msg, w, l);

            case Native.WM_SETTINGCHANGE:
                if ((l == IntPtr.Zero ? "" : Marshal.PtrToStringUni(l)) == "ImmersiveColorSet")
                {
                    Native.DarkMode.Apply();
                    ToolWindow.Theme(hwnd, AllControls());
                }
                return IntPtr.Zero;

            case Native.WM_DPICHANGED:
            {
                uint newDpi = (uint)(w.ToInt64() & 0xFFFF);
                var suggested = Marshal.PtrToStructure<RECT>(l);
                Native.SetWindowPos(hwnd, IntPtr.Zero, suggested.Left, suggested.Top,
                                    suggested.Right - suggested.Left, suggested.Bottom - suggested.Top,
                                    Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                if (newDpi == 0) newDpi = Native.GetDpiForWindow(hwnd);
                ReFont(newDpi);
                Layout(newDpi, ToolWindow.WorkAreaOf(Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST)));
                return IntPtr.Zero;
            }

            case Native.WM_CLOSE:
                // The X, Alt-F4 and Esc all end here: every one of them is the user saying no.
                Native.DestroyWindow(hwnd);
                return IntPtr.Zero;

            case Native.WM_DESTROY:
                Native.KillTimer(hwnd, (IntPtr)IdTick);
                // The local loop in Show is the only loop running, and this is what ends it. The program's own
                // message loop is made afterwards and is not waiting to be told to quit.
                Native.PostQuitMessage(0);
                Forget();
                return IntPtr.Zero;

            default:
                return Native.DefWindowProcW(hwnd, msg, w, l);
        }
    }
}
