using System.Drawing;
using System.Drawing.Drawing2D;

namespace Roboc.App;

/// <summary>
/// The warning badge: an equilateral triangle outline with an exclamation inside.
///
/// <b>Why a drawn shape instead of the ⚠️ character.</b> A font glyph is only as
/// large as the font says, so "make it a little bigger than the body text" cannot
/// be done without enlarging the body text too -- and on a machine whose font lacks
/// the codepoint it does not appear at all. Every dialog used to start with "⚠️ "
/// inside the message string, which is why the first line read like body text. See
/// `Icons-Drawing.md` for the specification this follows.
///
/// <b>The geometry is not invented here.</b> It is the one settled in the SSEject
/// dialog badges and refined in File Tracker (2026-10-24 / 2026-10-26), and the four
/// corrections below came from the user looking at the rendered result:
///
/// <list type="bullet">
/// <item>the exclamation is a uniform bar with no round caps, 0.072 s wide;</item>
/// <item>its dot shrinks with the bar, to 0.042 s;</item>
/// <item>the pair sits slightly above the centroid, at 0.0315 s;</item>
/// <item>the whole badge is 1.8 x the body line height ("make it a little bigger").</item>
/// </list>
///
/// It is an <b>outline</b>, not a filled triangle: the interior is painted in the
/// dialog's own background colour, which is also what makes the shape read as a
/// frame rather than a blob at 30-odd pixels.
/// </summary>
internal static class WarningBadge
{
    /// <summary>Badge side as a multiple of the body line height. See the type remarks.</summary>
    internal const float SideToLineHeight = 1.8f;

    /// <summary>Gap between the badge and the text column, in logical pixels.</summary>
    internal const int Gap = 8;

    private const float MarginRatio = 0.075f;
    private const float StrokeRatio = 0.062f;
    private const float BarWidthRatio = 0.072f;
    private const float BarLengthRatio = 0.20f;
    private const float DotGapRatio = 0.060f;
    private const float DotRadiusRatio = 0.042f;
    private const float LiftRatio = 0.0315f;

    /// <summary>
    /// Draws the badge into <paramref name="hdc"/> at (<paramref name="left"/>,
    /// <paramref name="top"/>) with side <paramref name="size"/>.
    ///
    /// GDI+ rather than GDI: the triangle has two steep edges and GDI's polygon fill
    /// is not antialiased, which at this size reads as a jagged star. The rest of the
    /// dialog stays on GDI; only this shape needs the smoother rasteriser.
    /// </summary>
    internal static void Draw(
        nint hdc, int left, int top, int size, uint lineColour, uint fillColour)
    {
        if (size <= 0)
        {
            return;
        }

        float s = size;
        float leftF = left;
        float topF = top;

        // Equilateral: base 0.85 s and height base * sqrt(3)/2. The bounding box is then
        // re-centred vertically -- without that the triangle sits visibly low next to a
        // circle of the same size.
        float margin = MarginRatio * s;
        float baseWidth = s - (2 * margin);
        float height = baseWidth * MathF.Sqrt(3f) / 2f;

        float bottom = s - margin;
        float apexY = bottom - height;
        float shift = (s / 2f) - ((apexY + bottom) / 2f);
        apexY += shift;
        bottom += shift;

        float centreX = s / 2f;
        var apex = new PointF(leftF + centreX, topF + apexY);
        var lowerLeft = new PointF(leftF + margin, topF + bottom);
        var lowerRight = new PointF(leftF + s - margin, topF + bottom);

        float centroidX = (apex.X + lowerLeft.X + lowerRight.X) / 3f;
        float centroidY = (apex.Y + lowerLeft.Y + lowerRight.Y) / 3f;

        // The hole is the same triangle scaled about the centroid, so all three sides
        // keep the same width. Scaling about the bounding-box centre would make the
        // bottom stroke thinner than the two slanted ones.
        float stroke = StrokeRatio * s;
        float innerScale = MathF.Max(
            0.55f, 1f - ((2.4f * stroke) / MathF.Max(1f, s - (2 * margin))));

        var innerApex = Scale(apex, centroidX, centroidY, innerScale);
        var innerLeft = Scale(lowerLeft, centroidX, centroidY, innerScale);
        var innerRight = Scale(lowerRight, centroidX, centroidY, innerScale);

        using Graphics graphics = Graphics.FromHdc(hdc);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        using (var line = new SolidBrush(FromColourRef(lineColour)))
        using (var hole = new SolidBrush(FromColourRef(fillColour)))
        {
            graphics.FillPolygon(line, new[] { apex, lowerLeft, lowerRight });
            graphics.FillPolygon(hole, new[] { innerApex, innerLeft, innerRight });

            float barWidth = BarWidthRatio * s;
            float barLength = BarLengthRatio * s;
            float dotGap = DotGapRatio * s;
            float dotRadius = DotRadiusRatio * s;

            // Centred on the centroid, then lifted by the ratio the user settled on.
            //
            // centroidY already includes topF -- adding the origin again put the mark a
            // whole badge-height below the triangle, which is exactly how it looked on the
            // first render.
            float total = barLength + dotGap + (2 * dotRadius);
            float barTop = centroidY - (LiftRatio * s) - (total / 2f);

            graphics.FillRectangle(
                line, (leftF + centreX) - (barWidth / 2f), barTop, barWidth, barLength);

            graphics.FillEllipse(
                line,
                (leftF + centreX) - dotRadius,
                barTop + barLength + dotGap,
                2 * dotRadius,
                2 * dotRadius);
        }
    }

    private static PointF Scale(PointF point, float originX, float originY, float factor)
        => new(
            originX + ((point.X - originX) * factor),
            originY + ((point.Y - originY) * factor));

    /// <summary>A COLORREF (0x00BBGGRR) as a GDI+ colour.</summary>
    private static Color FromColourRef(uint colour) => Color.FromArgb(
        (int)(colour & 0xFF),
        (int)((colour >> 8) & 0xFF),
        (int)((colour >> 16) & 0xFF));
}
