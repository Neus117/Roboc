using System.Drawing;
using System.Drawing.Drawing2D;

namespace Roboc.App;

/// <summary>
/// Rounded rectangles, drawn with GDI+ rather than with GDI regions.
///
/// <b>Why this file exists.</b> Every rounded corner in this program used to be
/// <c>CreateRoundRectRgn</c> filled by <c>FillRgn</c>, and the corners came out visibly
/// stepped -- worst on the primary buttons, where a saturated blue meets a pale grey and
/// there is nothing to hide the staircase behind.
///
/// The cause is not the parameters. It is that a GDI <i>region</i> is a set of pixels,
/// not a shape: it has no notion of half a pixel, so a curve can only be approximated by
/// deciding, one whole pixel at a time, whether each one is inside. There is nothing to
/// tune; the aliasing is what a region is made of.
///
/// A <see cref="GraphicsPath"/> is geometry. Filled with <see cref="SmoothingMode.AntiAlias"/>
/// it is sampled with coverage, so a pixel the curve half covers is drawn half covered,
/// and the edge reads as an edge rather than as a staircase.
///
/// <b>The cost.</b> System.Drawing.Common is this project's first and only package
/// reference -- about half a megabyte in a twelve-megabyte portable build. The
/// alternative was to write a scanline rasteriser by hand, which is a great deal more
/// code for a worse result.
/// </summary>
internal static class RoundedBox
{
    /// <summary>
    /// The bridge between this file and everything around it.
    ///
    /// The rest of the drawing layer talks to a raw HDC through GDI P/Invoke -- FillRgn,
    /// DrawTextW, CreateSolidBrush. GDI+ needs a Graphics. Graphics.FromHdc converts one
    /// to the other, and the conversion is cheap enough to do per shape.
    ///
    /// <b>Disposed before returning, deliberately.</b> A Graphics holds drawing state on
    /// the device context while it is alive, and the surrounding GDI calls know nothing
    /// about it. Handing a short-lived Graphics to a helper and taking it back before the
    /// caller draws again keeps the two APIs from ever being in use at the same moment,
    /// which is the arrangement that does not produce surprises.
    /// </summary>
    private static Graphics From(nint dc) => Graphics.FromHdc(dc);

    /// <summary>
    /// A palette colour as a GDI+ colour.
    ///
    /// The palette stores 0x00BBGGRR, the COLORREF order GDI uses; Color.FromArgb wants
    /// 0xAARRGGBB. Swapping the red and blue bytes the wrong way round produces a
    /// perfectly plausible colour that is simply not the one intended, which is the kind
    /// of mistake that survives a glance at the screen.
    /// </summary>
    private static Color ToColor(uint rgb)
        => Color.FromArgb(
            255,
            (int)(rgb & 0xFF),
            (int)((rgb >> 8) & 0xFF),
            (int)((rgb >> 16) & 0xFF));

    /// <summary>
    /// The outline of a rounded rectangle, as geometry.
    ///
    /// The radius is clamped to half the shorter side: a radius larger than that produces
    /// arcs that cross each other, and the path self-intersects into a shape nobody asked
    /// for. Buttons are wider than they are tall, so this bites on short buttons.
    /// </summary>
    internal static GraphicsPath Path(int left, int top, int width, int height, int radius)
    {
        var path = new GraphicsPath();

        int limit = Math.Min(width, height) / 2;
        radius = Math.Max(0, Math.Min(radius, limit));

        if (radius == 0)
        {
            path.AddRectangle(new Rectangle(left, top, width, height));
            return path;
        }

        int diameter = radius * 2;

        // Four arcs joined by four straight edges, clockwise from the top left.
        path.AddArc(left, top, diameter, diameter, 180, 90);
        path.AddArc(left + width - diameter, top, diameter, diameter, 270, 90);
        path.AddArc(left + width - diameter, top + height - diameter, diameter, diameter, 0, 90);
        path.AddArc(left, top + height - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();

        return path;
    }

    /// <summary>Fills and then outlines, which is how almost every shape here is drawn.</summary>
    internal static void Draw(
        nint dc, int left, int top, int width, int height, int radius,
        uint fill, uint? outline)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        using Graphics graphics = From(dc);

        SmoothingMode previous = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        using (GraphicsPath path = Path(left, top, width, height, radius))
        using (var brush = new SolidBrush(ToColor(fill)))
        {
            graphics.FillPath(brush, path);
        }

        if (outline is { } colour)
        {
            // Inset by half the stroke: a pen straddles its path, so an un-inset outline
            // loses its outer half off the edge of the shape it is meant to outline.
            const float thickness = 1f;
            int inset = 1;

            using GraphicsPath path = Path(
                left + inset, top + inset,
                width - (inset * 2), height - (inset * 2),
                Math.Max(0, radius - inset));

            using var pen = new Pen(ToColor(colour), thickness);
            graphics.DrawPath(pen, path);
        }

        graphics.SmoothingMode = previous;
    }
}
