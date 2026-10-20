// The plumbing the two tool windows share: the blacklist panel and the advertisement rule window.
//
// Both are plain Win32 windows full of stock controls - statics, edits, buttons, a combo box, list boxes - and
// neither of them draws anything. What they used to do was carry a copy each of the same font arithmetic, the
// same work-area lookup, the same dark-theme hand-off, the same class registration and the same placement. Two
// copies of a font measurement is the kind of duplicate that only shows up as a window that is a pixel or a DPI
// step different from its neighbour, which is what the rule window was: the panel centred itself and the rule
// window sat eighty pixels in from the corner. They are one set of functions now.

using System.Runtime.InteropServices;

namespace Moa;

internal static class ToolWindow
{
    /// <summary>
    /// The usable area of a monitor, or of the primary display when there is no monitor to ask about.
    /// </summary>
    public static RECT WorkAreaOf(IntPtr monitor)
    {
        var info = new Native.MONITORINFO { CbSize = (uint)Marshal.SizeOf<Native.MONITORINFO>() };
        if (monitor != IntPtr.Zero && Native.GetMonitorInfoW(monitor, ref info)) return info.RcWork;
        return new RECT { Right = Native.GetSystemMetrics(0), Bottom = Native.GetSystemMetrics(1) };
    }

    /// <summary>
    /// Centres a window in a work area, at its own current size, shrinking it when it would not fit.
    ///
    /// The placement every one of these windows opens with: the monitor the cursor is on, so it comes up where
    /// the user is looking, and the middle of it, which is the one place a fixed-size window belongs.
    /// </summary>
    public static void Centre(IntPtr hwnd, RECT work)
    {
        if (!Native.GetWindowRect(hwnd, out var r)) return;
        Centre(hwnd, work, r.Right - r.Left, r.Bottom - r.Top);
    }

    /// <summary>Centres a window of this size in a work area, shrinking it when it would not fit.</summary>
    public static void Centre(IntPtr hwnd, RECT work, int w, int h)
    {
        int availW = work.Right - work.Left, availH = work.Bottom - work.Top;
        w = Math.Min(w, availW);
        h = Math.Min(h, availH);
        Native.SetWindowPos(hwnd, IntPtr.Zero,
                            work.Left + (availW - w) / 2, work.Top + (availH - h) / 2, w, h,
                            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOCOPYBITS);
    }

    /// <summary>
    /// Puts the window and every control on it onto the system's theme, and asks for one repaint. Nothing here
    /// draws: uxtheme is told which of its own sets of drawings to use and the controls paint themselves with it.
    ///
    /// The two exceptions are the ones the system has no dark answer for, and they are colour assignments rather
    /// than drawing either: the class background brush, which fills the parts of the window no control covers, and
    /// the text colour a static asks its parent for in WM_CTLCOLORSTATIC - see StaticColour. On the light theme
    /// both are left exactly as the system had them.
    /// </summary>
    public static void Theme(IntPtr hwnd, IntPtr[] controls)
    {
        Native.DarkMode.Style(hwnd);
        Native.DarkMode.Frame(hwnd);

        // Set rather than registered: the class is made once, at the first Show, and registering a second class
        // for the other theme would mean two windows that must never both exist.
        IntPtr background = Native.DarkMode.BackgroundBrush();
        Native.SetClassBackground(hwnd, background != IntPtr.Zero ? background
                                                                  : (IntPtr)(Native.COLOR_WINDOW + 1));
        foreach (var c in controls) Native.DarkMode.Style(c);

        // Erase as well as repaint: the client area is still in the old theme's colours underneath, and a repaint
        // that does not erase would leave them showing through.
        Native.InvalidateRect(hwnd, IntPtr.Zero, true);
    }

    /// <summary>
    /// The answer to WM_CTLCOLORSTATIC on the dark theme, or the system's own answer on the light one.
    ///
    /// A plain static's background and text come from its parent, and on the dark theme there is no system colour
    /// for either. Handing one back is the whole of the work; the control paints itself with it.
    /// </summary>
    public static IntPtr StaticColour(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
    {
        IntPtr brush = Native.DarkMode.BackgroundBrush();
        if (brush == IntPtr.Zero) return Native.DefWindowProcW(hwnd, msg, w, l);
        Native.SetTextColor(w, Native.DarkMode.DarkTextColour);
        Native.SetBkColor(w, 0x202020);
        return brush;
    }

    /// <summary>
    /// Registers a tool window's class once, and says whether it is there. <paramref name="redraw" /> is the
    /// CS_HREDRAW|CS_VREDRAW pair, for a window whose client area changes size with the frame rather than one
    /// that is laid out once.
    /// </summary>
    public static bool Register(string className, Native.WndProcDelegate proc, bool redraw, string what)
    {
        var wc = new WNDCLASSEXW
        {
            CbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            Style = redraw ? Native.CS_HREDRAW | Native.CS_VREDRAW : 0,
            LpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
            HInstance = Native.GetModuleHandleW(null),
            HbrBackground = (IntPtr)(Native.COLOR_WINDOW + 1),
            HCursor = Native.LoadCursorW(IntPtr.Zero, Native.IDC_ARROW),
            LpszClassName = className,
        };
        bool ok = Native.RegisterClassExW(ref wc) != 0;
        if (!ok) Log.Write($"{what}: RegisterClassExW failed");
        return ok;
    }

    /// <summary>
    /// The keyboard rules a window full of controls is owed by whoever is pumping the queue: Tab between the
    /// controls, the arrows and the page keys inside a list, Enter on the button that has the focus, and Esc.
    /// False when the message is not this window's and the caller should dispatch it as it always would.
    /// </summary>
    public static bool DialogMessage(IntPtr hwnd, ref MSG msg)
        => hwnd != IntPtr.Zero && Native.IsDialogMessageW(hwnd, ref msg);

    /// <summary>Destroys a tool window when the tray host goes, so nothing of this program is left open.</summary>
    public static void Destroy(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero && Native.IsWindow(hwnd)) Native.DestroyWindow(hwnd);
    }

    /// <summary>
    /// The font every control is given, and the one measurement the layouts are built from: the line height it
    /// comes out as, in 96 DPI units.
    ///
    /// The face, the weight and the point size come from System.Drawing's own reading of the system metrics -
    /// SystemFonts.MessageBoxFont - rather than from a struct of this program's. The call behind it is one whose
    /// layout has changed with Windows and whose failure mode is a font that is not the one asked for; .NET
    /// already has that right, so this does not keep a second copy of it. Its answer is a font at the dpi the
    /// process runs at, which is the system's, and the size is scaled from there to the display this window is
    /// on - that is the whole of the per-monitor part.
    ///
    /// The handle is made here rather than taken from that font, because a handle out of a Font object raises a
    /// question about who deletes it, and this one is the caller's to delete. What the font metrics are is then
    /// measured off the handle, since a font's own idea of how tall its lines are is not its size.
    ///
    /// The fallback is the face and size this used before the font came from the system, for a machine where the
    /// call throws. Losing the font entirely would lose the layout with it.
    /// </summary>
    public static IntPtr UiFont(uint dpi, out int line96, out string face, out double points, string what)
    {
        line96 = 0;
        face = "Segoe UI";
        points = 9.0 * dpi / 96;
        int system = (int)Native.GetDpiForSystem();
        if (system <= 0) system = 96;

        try
        {
            using var ui = System.Drawing.SystemFonts.MessageBoxFont;
            // The system's font is expressed in the process's own dpi; this window may be on a display at another
            // one. Both the size in points and the height in pixels come out of the same arithmetic, so the font
            // is its size at this display rather than its pixels at another.
            points = ui.SizeInPoints * dpi / system;
            face = ui.Name;
            IntPtr made = Native.CreateFontW(-(int)Math.Round(points * dpi / 72.0), 0, 0, 0,
                                             ui.Bold ? 700 : 400, ui.Italic ? 1u : 0u, 0, 0, ui.GdiCharSet,
                                             0, 0, 0, 0, face);
            line96 = LineOf(made, dpi);
            if (line96 > 0) return made;
            if (made != IntPtr.Zero) Native.DeleteObject(made);
            Log.Write($"{what}: the system font made no line height; asking for one instead");
        }
        catch (Exception e)
        {
            Log.Write($"{what}: the system font could not be read ({e.GetType().Name}); asking for one");
        }

        IntPtr font = Native.CreateFontW(-Native.MulDiv(9, (int)dpi, 72), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 0, 0,
                                         face);
        line96 = LineOf(font, dpi);
        return line96 > 0 ? font : IntPtr.Zero;
    }

    /// <summary>
    /// How tall one line of a font is, in 96 DPI units: the character cell plus the leading the font asks for,
    /// which together are what a row of text takes. Measured on a screen device context, because what a font's
    /// metrics are is a question for a device context and there is one of those on every machine.
    /// </summary>
    public static int LineOf(IntPtr font, uint dpi)
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
}
