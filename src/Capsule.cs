// The two controls an advertisement carries: a capsule that says how much of the video is left and how to
// leave early, and a capsule that turns the sound off and on.
//
// Four decisions worth writing down, because each of them is the opposite of the obvious one:
//
//   * They are drawn here rather than by the system. A standard button cannot be translucent - it paints
//     itself out of the system's own colours, which over a video is a grey box - and what this replaced was a
//     static with the card's black painted behind its text, which is a black rectangle with a label in it.
//     What is wanted is a pill of black at a little over half strength with the label at full strength on top
//     of it, and the two strengths are different, so the pill has to be per-pixel alpha: a window of its own
//     with WS_EX_LAYERED, every pixel of it drawn here, and UpdateLayeredWindow handed the result.
//
//   * The icon is drawn as paths rather than decoded from an SVG. There is no SVG renderer in this program and
//     taking one on would be a dependency, a download and a parse for two small pictures. A speaker is a
//     polygon and two arcs, and paths are the right shape for this anyway: they are the same picture at every
//     DPI, where a bitmap would have to be picked by size and would be soft between the sizes.
//
//   * Every size comes from the height, and the height comes only from the scaling of the monitor the card is
//     on. Not from the card, and not from the window being advertised: a control that shrank with the window
//     would be unreadable on a small one and would change size in front of the user on a large one. See Style.
//
//   * The shape rule, which is the whole of what "capsule" means here and which probe/media checks without a
//     screen: both ends are a standard semicircle - the radius is exactly half the height - and the outermost
//     thing inside is placed so that its centre is half the height from the side it is nearest, which is the
//     same distance as from the top and the bottom. That last one is the centre of the round end, so the
//     outermost thing sits in the cap the way the dot sits in a circle: the icon in the mute capsule, and the
//     last character of the label in both. See Measure, which is where the rule is and where it is explained.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace Moa;

internal sealed class Capsule
{
    /// <summary>The three looks a capsule has, and the only thing a click and the cursor change.</summary>
    internal enum State { Normal, Hover, Pressed }

    /// <summary>The picture a capsule can carry to its left. None is the countdown, which is all words.</summary>
    internal enum Glyph { None, Sound, Muted }

    /// <summary>
    /// Where a capsule says something went wrong. Pointed at the card's own trace by the card, for the same
    /// reason AdsCard.Trace exists: this file cannot call Log without pulling the program's logging into every
    /// probe that compiles it.
    /// </summary>
    public static Action<string>? Trace;

    /// <summary>What is drawn between what the button says and what it does: "12s left | skip".</summary>
    private const string Separator = "|";

    // ---- the sizes, and the one thing they follow ---------------------------------------------------------

    /// <summary>
    /// Everything about a capsule's size at one DPI, and the whole of what a display is followed by.
    ///
    /// The height is the only number here: a comfortable size for a button on a 100 per cent display, scaled by
    /// the monitor's own scaling, and everything else is a fraction of it. One number to change, and the
    /// fractions are what keep a capsule looking like itself at 100, 150 and 200 per cent instead of coming out
    /// with a label that nearly fits or an icon that swims in its own padding.
    ///
    /// The DPI is the card's window's, so it is the monitor the card is really on rather than the system's idea
    /// of the display, and it is read when the controls are made - which is after the card has arrived wherever
    /// it is going, so it is the right monitor even for a window advertised on a second display.
    /// </summary>
    internal sealed class Style : IDisposable
    {
        private const int BaseHeight = 46;                // at 96 DPI, which is 100 per cent scaling

        public readonly int Height, Radius, Gap, Icon, FontPx;
        public readonly Font Font;

        public Style(int dpi)
        {
            // A DPI outside this is not a display, it is a call that failed: 0 from a window that does not
            // exist yet, or a number from a driver that has lost its mind. 96 is the answer that draws
            // something sensible, which is better than the nothing an unchecked scale would draw.
            if (dpi < 48 || dpi > 480) dpi = 96;

            Height = Math.Max(18, (int)Math.Round(BaseHeight * dpi / 96.0));
            Radius = Height / 2;                          // a standard semicircle, exactly half the height
            Gap = Math.Max(2, (int)Math.Round(Height * 0.24));
            Icon = Math.Max(8, (int)Math.Round(Height * 0.52));
            FontPx = Math.Max(8, (int)Math.Round(Height * 0.46));
            Font = new Font(Face, FontPx, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        public void Dispose() => Font.Dispose();
    }

    /// <summary>
    /// The face the labels are drawn in, picked once. A CJK label in a face without CJK glyphs is a row of
    /// boxes, and the one this is named after is the one Windows itself draws its own interface in on a Chinese
    /// system; the list is a fallback chain for the machines that do not have it, and the generic face at the
    /// end always exists. Measured walking the families is a few milliseconds, once per process.
    /// </summary>
    private static readonly string Face = PickFace();

    private static string PickFace()
    {
        string[] wanted = { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI" };
        try
        {
            foreach (var family in FontFamily.Families)
                foreach (string name in wanted)
                    if (string.Equals(family.Name, name, StringComparison.OrdinalIgnoreCase)) return name;
        }
        catch { }
        return FontFamily.GenericSansSerif.Name;
    }

    // ---- the shape rule -----------------------------------------------------------------------------------

    /// <summary>
    /// Where everything in one capsule goes, in the capsule's own coordinates, worked out from the labels and
    /// the style alone. Split out from the drawing so that the rule can be checked without a screen.
    ///
    /// Only horizontal positions: everything in a capsule is centred vertically, which is what makes "distance
    /// from the centre to the top" the same number as "distance from the centre to the bottom" and lets the
    /// rule be stated as one dimension.
    /// </summary>
    internal readonly struct Layout
    {
        public readonly int Width, Height, Radius;
        public readonly int IconX, IconW;
        public readonly int TextX, TextW;
        public readonly int SepX, SepW;
        public readonly int ActionX, ActionW;

        /// <summary>
        /// The outermost thing on each side, which is what the rule above is about: the icon or the first
        /// character on the left, the last character of the trailing label or of the text on the right.
        /// </summary>
        public readonly int OuterLeftX, OuterLeftW, OuterRightX, OuterRightW;

        public Layout(int width, int height, int radius, int iconX, int iconW, int textX, int textW,
                      int sepX, int sepW, int actionX, int actionW,
                      int outerLeftX, int outerLeftW, int outerRightX, int outerRightW)
        {
            Width = width; Height = height; Radius = radius;
            IconX = iconX; IconW = iconW; TextX = textX; TextW = textW;
            SepX = sepX; SepW = sepW; ActionX = actionX; ActionW = actionW;
            OuterLeftX = outerLeftX; OuterLeftW = outerLeftW;
            OuterRightX = outerRightX; OuterRightW = outerRightW;
        }
    }

    /// <summary>
    /// The layout, and with it the shape rule.
    ///
    /// Both ends are a semicircle: the radius is half the height, and that is not a taste - it is what makes the
    /// end a half circle rather than a rounded rectangle corner.
    ///
    /// The rule about the outermost thing is then the padding: the outermost thing's *centre* is put half the
    /// height from the side, so its distance to that side is the same as its distance to the top and the bottom,
    /// and it sits on the centre of the round end. The outermost thing is a character, not the whole label, and
    /// that is not a technicality: a label which held the outer positions itself would be its own padding, and a
    /// six-character label is wider than three of these capsules, so the rule could not hold for it at all. A
    /// character is the size of the cap, which is what makes the rule mean something. A glyph wider than the
    /// capsule is drawn from the edge rather than outside it, which is the one case where the rule is broken to
    /// keep the label inside its own button.
    /// </summary>
    internal static Layout Measure(Style st, string text, string? action, Glyph icon)
    {
        using var g = Graphics.FromHwnd(IntPtr.Zero);
        var fmt = StringFormat.GenericTypographic;

        int iconW = icon == Glyph.None ? 0 : st.Icon;
        int textW = Width(g, st.Font, fmt, text);
        int sepW = action == null ? 0 : Width(g, st.Font, fmt, Separator);
        int actW = action == null ? 0 : Width(g, st.Font, fmt, action);

        int leftW = iconW > 0 ? iconW : Atom(g, st.Font, fmt, text, first: true);
        int rightW = action != null ? Atom(g, st.Font, fmt, action, first: false)
                                    : Atom(g, st.Font, fmt, text, first: false);
        int padL = Math.Max(0, st.Height / 2 - leftW / 2);
        int padR = Math.Max(0, st.Height / 2 - rightW / 2);

        int x = padL, iconX = x;
        if (iconW > 0) x += iconW + st.Gap;
        int textX = x;
        x += textW;
        int sepX = 0, actionX = 0;
        if (action != null)
        {
            sepX = x + st.Gap;
            actionX = sepX + sepW + st.Gap;
            x = actionX + actW;
        }
        int width = x + padR;

        // The right-hand one is worked out from the right edge of the thing it belongs to, because that is the
        // edge the padding was measured from.
        int outerLeftX = iconW > 0 ? iconX : textX;
        int outerRightX = action != null ? actionX + actW - rightW : textX + textW - rightW;
        return new Layout(width, st.Height, st.Radius, iconX, iconW, textX, textW, sepX, sepW,
                          actionX, actW, outerLeftX, leftW, outerRightX, rightW);
    }

    /// <summary>How wide a whole string is, rounded up so that nothing is lost to the ceiling.</summary>
    private static int Width(Graphics g, Font font, StringFormat fmt, string s)
        => (int)Math.Ceiling(g.MeasureString(s, font, int.MaxValue, fmt).Width);

    /// <summary>
    /// How wide the first or last character of a string is. The font is asked for the one character rather than
    /// the string's own advance being divided by its length, because the two agree only for a monospaced face
    /// and this one is not.
    /// </summary>
    private static int Atom(Graphics g, Font font, StringFormat fmt, string s, bool first)
    {
        if (s.Length == 0) return 0;
        return Width(g, font, fmt, s.Substring(first ? 0 : s.Length - 1, 1));
    }

    // ---- the drawing --------------------------------------------------------------------------------------

    /// <summary>
    /// The capsule's pixels, at the size its own layout asks for, premultiplied and ready for the compositor.
    ///
    /// The bitmap is Format32bppPArgb and that is the whole of the alpha plumbing: GDI+ writes premultiplied
    /// values into a bitmap declared that way, and UpdateLayeredWindow expects premultiplied values, so the two
    /// agree and no pass over the pixels is needed. A 32bppArgb bitmap would be straight alpha and would have to
    /// be premultiplied by hand before it could be shown - which is what the program's own animation panel does,
    /// because it draws into a DIB section it made itself.
    ///
    /// The colours are deliberately simple and few: one pill, one hairline around it so that a black button is
    /// visible over a black video, and the label. Pressing inverts the capsule rather than darkening it again,
    /// because the two states a user is told apart at a glance are "this is what I am about to press" and "it
    /// went" - and over a video of any colour at all, white on black and black on white are the two that cannot
    /// be confused with each other or with the picture.
    /// </summary>
    internal static Bitmap Render(Style st, string text, string? action, Glyph icon, State state,
                                  out Layout layout)
    {
        layout = Measure(st, text, action, icon);
        var bmp = new Bitmap(layout.Width, layout.Height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.Clear(Color.Transparent);

        using var pill = Pill(0, 0, layout.Width, layout.Height, layout.Radius);
        using (var fill = new SolidBrush(Fill(state))) g.FillPath(fill, pill);
        // Inset by half its own width: a stroke is centred on the path, so a hairline drawn on the very edge has
        // half of itself outside the capsule and cut off, and what is left is fainter than it was asked to be.
        float edge = Math.Max(1f, layout.Height / 46f);
        using (var hairline = Pill(edge / 2, edge / 2, layout.Width - edge, layout.Height - edge,
                                   (layout.Height - edge) / 2f))
        using (var pen = new Pen(Hairline(state), edge))
        {
            g.DrawPath(pen, hairline);
        }

        var ink = Ink(state);
        using (var brush = new SolidBrush(ink))
        {
            // The label is drawn into a band of the capsule's own height so that the font's line box is centred
            // in it: centring by subtracting half the font's height instead puts the text where the font's own
            // idea of a line is centred, which for a CJK face leaves the glyphs sitting low.
            var centred = (StringFormat)StringFormat.GenericTypographic.Clone();
            centred.Alignment = StringAlignment.Near;
            centred.LineAlignment = StringAlignment.Center;
            centred.FormatFlags |= StringFormatFlags.NoWrap;
            var band = new RectangleF(layout.TextX, 0, layout.TextW + 2, layout.Height);
            g.DrawString(text, st.Font, brush, band, centred);

            if (action != null)
            {
                using var dim = new SolidBrush(Weak(state));
                g.DrawString(Separator, st.Font, dim,
                             new RectangleF(layout.SepX, 0, layout.SepW + 2, layout.Height), centred);
                g.DrawString(action, st.Font, brush,
                             new RectangleF(layout.ActionX, 0, layout.ActionW + 2, layout.Height), centred);
            }
        }

        if (icon != Glyph.None) Speaker(g, icon, layout, ink);
        return bmp;
    }

    /// <summary>
    /// The pill itself: two semicircles and the straight sides between them. The arcs are drawn from the 6 and
    /// 12 o'clock points of their own ellipses, which is what makes the ends meet the sides exactly rather than
    /// leaving a notch, and a radius of half the height makes those ellipses circles.
    /// </summary>
    private static GraphicsPath Pill(float x, float y, float w, float h, float r)
    {
        var path = new GraphicsPath();
        if (r <= 0)
        {
            path.AddRectangle(new RectangleF(x, y, w, h));
            return path;
        }
        path.AddArc(x, y, r * 2, h, 90, 180);
        path.AddArc(x + w - r * 2, y, r * 2, h, 270, 180);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// The speaker, in a box of its own size: the cone as one polygon, and then either two arcs opening away
    /// from it or a cross over where they would be. Everything is a fraction of the box, so the same picture is
    /// drawn at every DPI and at both states without a second copy of anything.
    /// </summary>
    private static void Speaker(Graphics g, Glyph icon, Layout lay, Color ink)
    {
        float s = lay.IconW;
        float x = lay.IconX;
        float y = (lay.Height - lay.IconW) / 2f;

        using (var brush = new SolidBrush(ink))
        using (var cone = new GraphicsPath())
        {
            cone.AddPolygon(new[]
            {
                new PointF(x + 0.05f * s, y + 0.36f * s),
                new PointF(x + 0.26f * s, y + 0.36f * s),
                new PointF(x + 0.50f * s, y + 0.14f * s),
                new PointF(x + 0.50f * s, y + 0.86f * s),
                new PointF(x + 0.26f * s, y + 0.64f * s),
                new PointF(x + 0.05f * s, y + 0.64f * s),
            });
            g.FillPath(brush, cone);
        }

        using var pen = new Pen(ink, Math.Max(1f, s * 0.09f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        if (icon == Glyph.Muted)
        {
            g.DrawLine(pen, x + 0.66f * s, y + 0.32f * s, x + 0.95f * s, y + 0.68f * s);
            g.DrawLine(pen, x + 0.95f * s, y + 0.32f * s, x + 0.66f * s, y + 0.68f * s);
            return;
        }
        // Sound: the two arcs of a speaker's wave, each the right-hand side of its own ellipse, so that they
        // curve away from the cone rather than around it.
        g.DrawArc(pen, x + 0.34f * s, y + 0.30f * s, 0.38f * s, 0.40f * s, -55, 110);
        g.DrawArc(pen, x + 0.34f * s, y + 0.12f * s, 0.66f * s, 0.76f * s, -55, 110);
    }

    private static Color Fill(State s) => s switch
    {
        State.Hover => Color.FromArgb(190, 10, 10, 12),
        State.Pressed => Color.FromArgb(235, 250, 250, 252),
        _ => Color.FromArgb(140, 10, 10, 12),
    };

    private static Color Hairline(State s) => s switch
    {
        State.Pressed => Color.FromArgb(90, 0, 0, 0),
        State.Hover => Color.FromArgb(90, 255, 255, 255),
        _ => Color.FromArgb(45, 255, 255, 255),
    };

    private static Color Ink(State s) => s switch
    {
        State.Pressed => Color.FromArgb(240, 12, 12, 14),
        State.Hover => Color.FromArgb(255, 255, 255, 255),
        _ => Color.FromArgb(235, 255, 255, 255),
    };

    /// <summary>The separator, which is punctuation rather than a word and is drawn as such.</summary>
    private static Color Weak(State s) => s switch
    {
        State.Pressed => Color.FromArgb(120, 12, 12, 14),
        _ => Color.FromArgb(120, 255, 255, 255),
    };

    // ---- the window the pixels go in -----------------------------------------------------------------------

    private const string ClassName = "MoaCapsule";
    private static readonly Native.WndProcDelegate Proc = Handle;
    private static bool _registered;

    private readonly Style _style;

    /// <summary>Where the capsule is anchored in screen coordinates, and which of its corners that is.</summary>
    private int _anchorX, _anchorY;
    private readonly bool _rightAligned;

    /// <summary>The trailing label, which never changes for the life of one capsule: "skip", or nothing.</summary>
    private readonly string? _action;

    private string _text = "";
    private Glyph _icon = Glyph.None;
    private State _state = State.Normal;
    private bool _shown;
    private int _width;

    public IntPtr Hwnd { get; }

    private Capsule(IntPtr hwnd, Style style, int x, int y, bool rightAligned, string? action)
    {
        Hwnd = hwnd; _style = style;
        _anchorX = x; _anchorY = y; _rightAligned = rightAligned; _action = action;
    }

    /// <summary>
    /// Makes one capsule, owned by the window it is drawn over and anchored to one of its corners.
    ///
    /// Owned rather than parented, which is not tidiness: measured earlier in this feature, a control that is a
    /// child of the card is not drawn over the video whatever its z-order, and a control that takes the click
    /// stops the card from hearing it at all. An owned window is in neither the card's painting nor its input.
    ///
    /// WS_EX_TRANSPARENT is what makes the click reach the card: the card decides what a click meant by where
    /// the cursor was, so a capsule that swallowed it would be a mute button that cannot be pressed.
    /// </summary>
    public static Capsule? Make(IntPtr owner, Style style, int x, int y, bool rightAligned, string? action)
    {
        if (!Register()) return null;
        IntPtr hwnd = Native.CreateWindowExW(
            Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW |
            Native.WS_EX_NOACTIVATE | (uint)Native.WS_EX_TOPMOST,
            ClassName, "Mobile Open Animation", Native.WS_POPUP,
            x, y, 1, 1, owner, IntPtr.Zero, Native.GetModuleHandleW(null), IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
        {
            Say($"capsule: the window would not be made (err {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
            return null;
        }
        return new Capsule(hwnd, style, x, y, rightAligned, action);
    }

    private static bool Register()
    {
        if (_registered) return true;
        var cls = new WNDCLASSEXW
        {
            CbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<WNDCLASSEXW>(),
            LpfnWndProc = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(Proc),
            HInstance = Native.GetModuleHandleW(null),
            LpszClassName = ClassName,
            HCursor = Native.LoadCursorW(IntPtr.Zero, Native.IDC_ARROW),
            // No background brush at all, which is not an omission: every pixel of this window comes from the
            // bitmap UpdateLayeredWindow was handed, and a class brush would have the system paint over the
            // part of it that is transparent.
            HbrBackground = IntPtr.Zero,
        };
        _registered = Native.RegisterClassExW(ref cls) != 0;
        return _registered;
    }

    /// <summary>What the capsule says, and the picture beside it. Nothing happens when neither has changed.</summary>
    public void Set(string text, Glyph icon)
    {
        if (_text == text && _icon == icon) return;
        _text = text;
        _icon = icon;
        Paint();
    }

    /// <summary>How it looks. Nothing happens when that has not changed either.</summary>
    public void Set(State state)
    {
        if (_state == state) return;
        _state = state;
        Paint();
    }

    public void Hide()
    {
        if (Hwnd != IntPtr.Zero) Native.ShowWindow(Hwnd, Native.SW_HIDE);
    }

    /// <summary>
    /// Comes along when the card it belongs to is moved.
    ///
    /// Moved rather than drawn again: what a capsule looks like is a bitmap the compositor is holding, and it is
    /// the same bitmap wherever the window is. Drawing it again for every step of a drag would be a GDI+ render
    /// of two pills per mouse move for no change at all.
    /// </summary>
    public void Move(int dx, int dy)
    {
        _anchorX += dx;
        _anchorY += dy;
        if (Hwnd == IntPtr.Zero) return;
        Native.SetWindowPos(Hwnd, IntPtr.Zero, _rightAligned ? _anchorX - _width : _anchorX, _anchorY, 0, 0,
                            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    public void Dispose()
    {
        if (Hwnd != IntPtr.Zero) Native.DestroyWindow(Hwnd);
    }

    /// <summary>
    /// Draws the capsule again and hands it to the compositor.
    ///
    /// The window is shown after the first pixels exist rather than at the end of Make, so that it is never a
    /// one-pixel square in the corner of the card: UpdateLayeredWindow moves and sizes the window as well as
    /// filling it, so by the time it is visible it is already the right shape in the right place.
    /// </summary>
    private void Paint()
    {
        if (Hwnd == IntPtr.Zero) return;
        using var bmp = Render(_style, _text, _action, _icon, _state, out var lay);
        int x = _rightAligned ? _anchorX - lay.Width : _anchorX;
        _width = lay.Width;
        Present(bmp, x, _anchorY);
        if (!_shown)
        {
            _shown = true;
            Native.ShowWindow(Hwnd, Native.SW_SHOWNOACTIVATE);
        }
    }

    /// <summary>
    /// The bitmap onto the window, at per-pixel alpha.
    ///
    /// GetHbitmap is how a GDI+ bitmap becomes the HBITMAP the compositor can be handed, and it is asked for a
    /// fully transparent background so that the alpha of the bitmap is what survives. The handle is ours and is
    /// deleted here; the device context is a fresh one every time rather than held, because a capsule is
    /// repainted when the cursor or the countdown changes and not sixty times a second.
    /// </summary>
    private void Present(Bitmap bmp, int x, int y)
    {
        IntPtr screen = Native.GetDC(IntPtr.Zero);
        IntPtr mem = Native.CreateCompatibleDC(screen);
        IntPtr pixels = bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr old = Native.SelectObject(mem, pixels);

        var dst = new POINT { X = x, Y = y };
        var size = new SIZE { Cx = bmp.Width, Cy = bmp.Height };
        var src = new POINT { X = 0, Y = 0 };
        var blend = new BLENDFUNCTION
        {
            BlendOp = Native.AC_SRC_OVER,
            BlendFlags = 0,
            // The whole capsule's opacity is in its pixels, so this is not a second place to change it.
            SourceConstantAlpha = 255,
            AlphaFormat = Native.AC_SRC_ALPHA,
        };
        Native.UpdateLayeredWindow(Hwnd, IntPtr.Zero, ref dst, ref size, mem, ref src, 0, ref blend,
                                   Native.ULW_ALPHA);

        Native.SelectObject(mem, old);
        Native.DeleteObject(pixels);
        Native.DeleteDC(mem);
        Native.ReleaseDC(IntPtr.Zero, screen);
    }

    /// <summary>
    /// Nothing, drawn here on purpose: the window has no background brush, so leaving WM_PAINT to the default
    /// procedure is what validates the update region without painting anything into it, and answering it here
    /// without doing the same would leave the window repainting itself forever.
    /// </summary>
    private static IntPtr Handle(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
        => Native.DefWindowProcW(hwnd, msg, w, l);

    private static void Say(string s) => Trace?.Invoke(s);
}
