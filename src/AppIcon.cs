// The program's own icon, drawn rather than shipped as a picture.
//
// It is IconPark's "curve adjustment": a filled square with one S-curve drawn across it. Two of its three colours
// are the system's rather than ours - the outline is the accent colour Windows is using and the fill is white on
// a light system and black on a dark one - so a bitmap could not hold it: the same icon has to be drawn again
// when the user changes either. What is fixed is the shape, and the numbers for that are the SVG's own: a 48-unit
// box, a 4-unit stroke, a square from 4 to 44 and a curve from 38,10 to 10,38 through 24,24. icon/ is where that
// file lives, and its geometry is written here instead of being parsed out of it because this program has no SVG
// parser and is not going to grow one for two paths - the same reason Capsule draws its magnifier.
//
// What it is for is the tray, which sits on the taskbar, so the light-or-dark answer is the *system* one
// (Themes\Personalize\SystemUsesLightTheme) and not the app one the program's own dialogs follow; see
// DarkMode.SystemIsDark for that other value and why both are read from the registry rather than from an API.
//
// The icon in the exe - src/app.ico, which Explorer shows for the file - is generated from this same drawing by
// probe/icon, so the shape is described once. That one is the SVG's own colours rather than the theme's, because
// a file's icon has no window to be themed against.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Moa;

internal static class AppIcon
{
    /// <summary>The side of the box the SVG's numbers are in. Everything below is divided by it.</summary>
    private const float Box = 48f;

    /// <summary>The SVG's stroke width, in its own units.</summary>
    private const float StrokeWidth = 4f;

    /// <summary>
    /// Draws the icon, filling a <paramref name="size" /> by <paramref name="size" /> square of
    /// <paramref name="g" /> that the caller has already made transparent.
    ///
    /// Two paths, in the order the SVG has them: the square is drawn filled and stroked, and the curve is
    /// stroked over it. Both strokes are rounded at the ends and corners, which is what the SVG asks for and
    /// what makes a 1.3-pixel stroke at tray size look like a line rather than a smear.
    /// </summary>
    internal static void Draw(Graphics g, float size, Color fill, Color stroke)
    {
        float k = size / Box;
        using var pen = new Pen(stroke, StrokeWidth * k)
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };

        // The square, whose stroke is centred on its own outline: half of the 4-unit stroke falls outside the
        // 4-to-44 rectangle, which is why the icon reaches from 2 to 46 of its 48-unit box rather than touching
        // the edge. That is the SVG's own arithmetic and not a fudge: the same icon drawn to the edge would have
        // its outline clipped.
        using var square = new GraphicsPath();
        square.AddRectangle(new RectangleF(4 * k, 4 * k, 40 * k, 40 * k));
        using (var brush = new SolidBrush(fill)) g.FillPath(brush, square);
        g.DrawPath(pen, square);

        // And the curve: one cubic from the top right to the middle and another from there to the bottom left,
        // which meets at 24,24 and is why the join is rounded.
        using var curve = new GraphicsPath();
        curve.AddBezier(new PointF(38 * k, 10 * k), new PointF(32 * k, 10 * k),
                        new PointF(27 * k, 14 * k), new PointF(24 * k, 24 * k));
        curve.AddBezier(new PointF(24 * k, 24 * k), new PointF(21 * k, 34 * k),
                        new PointF(16 * k, 38 * k), new PointF(10 * k, 38 * k));
        g.DrawPath(pen, curve);
    }

    /// <summary>The icon as a bitmap with per-pixel alpha, which is what the icon handle is built from.</summary>
    internal static Bitmap Render(int px, Color fill, Color stroke)
    {
        var bmp = new Bitmap(Math.Max(1, px), Math.Max(1, px), PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        Draw(g, bmp.Width, fill, stroke);
        return bmp;
    }

    /// <summary>
    /// The icon as the shell wants it, at <paramref name="px" /> pixels square. The caller owns the handle and
    /// destroys it with DestroyIcon; zero means the drawing or the icon could not be made, which is the caller's
    /// business to fall back from.
    ///
    /// The colour bitmap is a 32-bit DIB section holding the drawing's own pixels, and the mask is a blank one.
    /// That is the whole of what makes a drawn icon possible: an HBITMAP has no alpha channel to GDI, so an icon
    /// built from GDI+'s own GetHbitmap comes out hard-edged, with the mask - one bit per pixel - deciding what
    /// is transparent. A DIB section is the surface that does carry alpha, and an icon whose colour bitmap has an
    /// alpha channel is composited from it, which is what makes this icon look drawn at 16 pixels.
    /// </summary>
    internal static IntPtr Handle(int px, Color fill, Color stroke)
    {
        if (px < 8 || px > 512) return IntPtr.Zero;
        using var bmp = Render(px, fill, stroke);
        IntPtr colour = Surface(bmp, px);
        if (colour == IntPtr.Zero) return IntPtr.Zero;

        // A 1bpp bitmap of the icon's own size, all of it zero. CreateBitmap with no bits leaves the contents
        // undefined rather than blank, so it is handed a zero array: with an undefined mask, the icon would be
        // transparent wherever the garbage bit happened to be set.
        IntPtr mask = Native.CreateBitmap(px, px, 1, 1, new byte[((px + 15) / 16) * 2 * px]);
        try
        {
            var info = new Native.ICONINFO { FIcon = true, HbmMask = mask, HbmColor = colour };
            return Native.CreateIconIndirect(ref info);
        }
        finally
        {
            Native.DeleteObject(colour);
            Native.DeleteObject(mask);
        }
    }

    /// <summary>
    /// A 32-bit DIB section holding <paramref name="bmp" />'s pixels, alpha included.
    ///
    /// Row by row and by hand, because that is the only way the alpha survives: GDI+ and GDI agree on the layout
    /// of a 32bpp Argb bitmap (BGRA, straight alpha, rows top down) but nothing in either API converts between
    /// them without dropping the fourth byte.
    /// </summary>
    private static IntPtr Surface(Bitmap bmp, int px)
    {
        var bmi = new Native.BITMAPINFO
        {
            Header = new Native.BITMAPINFOHEADER
            {
                BiSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                BiWidth = px,
                BiHeight = -px,        // top-down, the same way round as the bitmap
                BiPlanes = 1,
                BiBitCount = 32,
                BiCompression = 0,     // BI_RGB
            }
        };

        IntPtr dc = Native.GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return IntPtr.Zero;
        IntPtr dib = Native.CreateDIBSection(dc, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        Native.ReleaseDC(IntPtr.Zero, dc);
        if (dib == IntPtr.Zero || bits == IntPtr.Zero)
        {
            if (dib != IntPtr.Zero) Native.DeleteObject(dib);
            return IntPtr.Zero;
        }

        int stride = px * 4;
        var row = new byte[stride];
        var data = bmp.LockBits(new Rectangle(0, 0, px, px), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < px; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, stride);
                Marshal.Copy(row, 0, bits + y * stride, stride);
            }
        }
        finally { bmp.UnlockBits(data); }
        return dib;
    }

    /// <summary>
    /// The program's *static* icon: the one the exe carries, in the SVG's own colours, taken out of this
    /// process's own executable rather than drawn. The caller owns the handle and destroys it with DestroyIcon;
    /// zero means there was nothing to take out, which is a build with no icon in it at all.
    ///
    /// This is the half of the icon that does not depend on the theme, and it is the one to hand to anything that
    /// draws an icon itself: the About box's task dialog composes it exactly as it composes its own information
    /// icon, so the caller that uses this does no drawing at all. See AboutDialog.Show - that box is the one
    /// place in this program that is not allowed to draw anything.
    ///
    /// Asked for at 256 so the dialog can have whatever size it wants: PrivateExtractIconsW asks a file for a size
    /// and returns the nearest one it really holds, and a box on a scaled display wants more than the 32 pixels
    /// ExtractIconEx would hand over.
    /// </summary>
    internal static IntPtr FromExe()
    {
        var got = new IntPtr[1];
        var ids = new uint[1];
        return Native.PrivateExtractIconsW(Compat.ExePath, 0, 256, 256, got, ids, 1, 0) > 0 ? got[0] : IntPtr.Zero;
    }

    /// <summary>
    /// The fill: white on a light system and black on a dark one, which is the colour of the surface the icon is
    /// drawn on rather than a colour of its own - the square is the taskbar, and the curve is over it.
    /// </summary>
    internal static Color Fill() => SystemIsLight() ? Color.White : Color.Black;

    /// <summary>
    /// The stroke: the accent Windows is using, which is the colour the card's border is drawn in as well, from
    /// the one reader of it (Native.Accent). When no accent has ever been chosen there is nothing to read, and the
    /// answer is the opposite of the fill rather than the fill itself - an outline the same colour as the square
    /// under it is not an outline.
    /// </summary>
    internal static Color Stroke() => Native.Accent() ?? (SystemIsLight() ? Color.Black : Color.White);

    /// <summary>
    /// Whether the system - the taskbar, the tray, the Start menu - is on the light theme.
    ///
    /// The system value rather than the app one, for the reason above: this is drawn on the taskbar. A missing or
    /// unreadable value counts as light, which is what this program answers everywhere else it reads this key.
    /// </summary>
    internal static bool SystemIsLight()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is not int light || light != 0;
        }
        catch { return true; }
    }
}
