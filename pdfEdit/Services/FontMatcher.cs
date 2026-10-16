using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PdfEdit.Services;

/// <summary>
/// Picks the system font whose stroke weight most closely matches the original text's.
///
/// Rewriting a scanned word always leaves the choice of substitute font, and a fixed pick
/// makes thin body text render in heavy 黑体. The original's ink is right there in the page
/// raster, so instead of guessing: measure how thick its strokes are, render the candidates
/// at the same size, measure the same way, and take the nearest.
///
/// Two metrics, both content-independent:
/// <list type="bullet">
///   <item>horizontal run length — a scan line crossing a glyph runs through vertical stems,
///   so the mean run is the stem width;</item>
///   <item>ink coverage within the ink's own bounding box — dense faces score higher than
///   thin ones regardless of how much whitespace the crop contains.</item>
/// </list>
/// </summary>
public static class FontMatcher
{
    /// <summary>CJK faces that resolve to distinct font programs in <see cref="WindowsFontResolver"/>.</summary>
    private static readonly string[] CjkCandidates = ["SimHei", "KaiTi", "FangSong"];

    private static readonly string[] LatinCandidates =
        ["Arial", "Times New Roman", "Verdana", "Courier New"];

    private const byte InkThreshold = 128;

    /// <summary>Result of <see cref="Measure"/>.</summary>
    /// <param name="Coverage">Ink pixels over the ink's bounding box.</param>
    /// <param name="MeanRun">Mean horizontal run length — the stem width — as a fraction of
    /// the ink's height, so the numbers compare across render sizes and zoom levels.</param>
    /// <param name="RunSpread">Standard deviation of run length, same normalisation.
    /// Calligraphic faces like 楷体 taper their strokes, so their runs vary; uniform faces
    /// like 仿宋 do not.</param>
    private readonly record struct Metrics(double Coverage, double MeanRun, double RunSpread)
    {
        public bool IsValid => Coverage > 0 && MeanRun > 0;
    }

    /// <summary>
    /// Returns the candidate family whose metrics best match the original crop, falling back
    /// to <see cref="PdfExportService.TextLayerFont"/> when the crop has no measurable ink.
    /// </summary>
    /// <param name="image">Page raster containing the original text.</param>
    /// <param name="rect">Crop of that raster covering the original glyphs.</param>
    /// <param name="sample">The text that will be drawn — candidates are ranked on it.</param>
    public static string Match(BitmapSource image, Int32Rect rect, string sample)
    {
        if (rect.Width < 3 || rect.Height < 3) return PdfExportService.TextLayerFont;
        if (string.IsNullOrWhiteSpace(sample)) return PdfExportService.TextLayerFont;

        // One crop read; the candidates only need its two numbers.
        var original = Measure(image, rect);
        if (!original.IsValid) return PdfExportService.TextLayerFont;

        // A short sample keeps the ranking about stroke weight rather than glyph mix.
        var text = sample.Length > 16 ? sample[..16] : sample;
        var candidates = text.Any(IsCjk) ? CjkCandidates : LatinCandidates;

        // Candidates are all rendered at one size, so a scale mismatch against the crop shifts
        // every score alike and leaves the argmin unchanged — the rank is what matters.
        var glyphPx = Math.Max(8, rect.Height * 0.88);

        var best = PdfExportService.TextLayerFont;
        var bestScore = double.MaxValue;

        foreach (var family in candidates)
        {
            try
            {
                var rendered = RenderSample(family, text, glyphPx);
                var metrics = Measure(rendered, new Int32Rect(0, 0, rendered.PixelWidth, rendered.PixelHeight));
                if (!metrics.IsValid) continue;

                var score = RelativeDiff(metrics.MeanRun, original.MeanRun)
                          + RelativeDiff(metrics.Coverage, original.Coverage)
                          + RelativeDiff(metrics.RunSpread, original.RunSpread);

                if (score < bestScore)
                {
                    bestScore = score;
                    best = family;
                }
            }
            catch
            {
                // A font that cannot render at all is simply not a candidate.
            }
        }

        return best;
    }

    /// <summary>Diagnostic one-liner: the measured metrics of a rendered sample.</summary>
    public static string Describe(BitmapSource image, string sample)
    {
        var m = Measure(image, new Int32Rect(0, 0, image.PixelWidth, image.PixelHeight));
        return $"覆盖 {m.Coverage:0.###} 游程 {m.MeanRun:0.##} 波动 {m.RunSpread:0.##}";
    }

    /// <summary>
    /// Renders one family's take on <paramref name="sample"/> at <paramref name="pixelHeight"/>
    /// device pixels, black on white — the same arrangement <see cref="Measure"/> expects.
    /// Public so the self-test can round-trip it through <see cref="Match"/>.
    /// </summary>
    public static BitmapSource RenderSample(string family, string sample, double pixelHeight)
    {
        var typeface = new Typeface(
            new FontFamily(family), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        var formatted = new FormattedText(
            sample, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            typeface, pixelHeight * 0.88, Brushes.Black, pixelsPerDip: 1.0);

        var width = Math.Max(4, (int)Math.Ceiling(formatted.WidthIncludingTrailingWhitespace) + 4);
        var height = Math.Max(4, (int)Math.Ceiling(pixelHeight * 1.5));

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            dc.DrawText(formatted, new Point(2, (height - formatted.Height) / 2));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static double RelativeDiff(double value, double reference)
        => Math.Abs(value - reference) / Math.Max(reference, 0.02);

    private static Metrics Measure(BitmapSource image, Int32Rect rect)
    {
        // Clamp to the bitmap: callers derive the crop arithmetically and may overrun by a pixel.
        var x = Math.Max(0, Math.Min(rect.X, image.PixelWidth - 1));
        var y = Math.Max(0, Math.Min(rect.Y, image.PixelHeight - 1));
        var w = Math.Max(1, Math.Min(rect.Width, image.PixelWidth - x));
        var h = Math.Max(1, Math.Min(rect.Height, image.PixelHeight - y));

        var stride = w * 4;
        var pixels = new byte[stride * h];
        image.CopyPixels(new Int32Rect(x, y, w, h), pixels, stride, 0);

        // First pass: ink mask plus the ink's bounding box, so coverage is measured against
        // the glyphs themselves and not the crop's padding.
        var ink = new bool[w * h];
        var count = 0;
        var minX = w; var minY = h; var maxX = -1; var maxY = -1;

        for (var row = 0; row < h; row++)
        for (var col = 0; col < w; col++)
        {
            var i = (row * w + col) * 4;
            var luma = (pixels[i] * 114 + pixels[i + 1] * 587 + pixels[i + 2] * 299) / 1000;
            if (luma >= InkThreshold) continue;

            ink[row * w + col] = true;
            count++;
            if (col < minX) minX = col;
            if (col > maxX) maxX = col;
            if (row < minY) minY = row;
            if (row > maxY) maxY = row;
        }

        if (count == 0 || maxX < minX) return default;

        // Second pass: horizontal run lengths over ink rows — the stem width.
        var runs = 0;
        var runTotal = 0;
        var runSquareSum = 0.0;
        for (var row = minY; row <= maxY; row++)
        {
            var length = 0;
            for (var col = 0; col <= w; col++)
            {
                var on = col < w && ink[row * w + col];
                if (on)
                {
                    length++;
                }
                else if (length > 0)
                {
                    runs++;
                    runTotal += length;
                    runSquareSum += (double)length * length;
                    length = 0;
                }
            }
        }

        if (runs == 0) return default;

        // Normalise run lengths by the ink's height: without this the metrics carry pixels,
        // and re-rendering a candidate at a slightly different size shifts the ranking.
        var inkHeight = maxY - minY + 1;
        var meanRun = runTotal / (double)runs / inkHeight;
        var variance = Math.Max(0, runSquareSum / runs - (runTotal / (double)runs) * (runTotal / (double)runs));

        var boxArea = (maxX - minX + 1) * inkHeight;
        return new Metrics(count / (double)boxArea, meanRun, Math.Sqrt(variance) / inkHeight);
    }

    private static bool IsCjk(char c) =>
        (c >= 0x2E80 && c <= 0x9FFF) || (c >= 0xF900 && c <= 0xFAFF) || (c >= 0xFF00 && c <= 0xFFEF);
}
