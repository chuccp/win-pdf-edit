using System.IO;
using PdfEdit.Models;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfEdit.Services;

/// <summary>
/// Structural editing and decorative overlays, via PDFsharp.
///
/// This is stage one of a two-stage export: it produces a document with the final page order,
/// rotation and overlays, and stage two (<see cref="PdfiumEngine.SaveWithAnnotations"/>) writes
/// the annotations into that result. The order matters — page indices have to be final before
/// annotations can be addressed by index.
/// </summary>
public static class PdfExportService
{
    private static readonly Lock InitLock = new();
    private static bool _initialized;

    /// <summary>
    /// PDFsharp 6 resolves no fonts on its own; without this, any DrawString throws.
    /// Must run before the first XFont is constructed.
    /// </summary>
    public static void Initialize()
    {
        lock (InitLock)
        {
            if (_initialized) return;
            GlobalFontSettings.FontResolver = new WindowsFontResolver();
            _initialized = true;
        }
    }

    // ------------------------------------------------------------------ structure

    /// <summary>
    /// Builds a new document from the surviving pages of <paramref name="source"/>, in the order
    /// given, applying each page's extra rotation and then the overlays.
    /// </summary>
    public static byte[] Build(byte[] source, IReadOnlyList<PageItem> livePages,
        IReadOnlyList<OverlaySpec> overlays,
        IReadOnlyDictionary<PageItem, List<OcrWord>>? textLayers = null,
        IReadOnlyList<TextReplacement>? replacements = null)
    {
        Initialize();

        using var sourceStream = new MemoryStream(source, writable: false);
        using var input = PdfReader.Open(sourceStream, PdfDocumentOpenMode.Import);

        using var output = new PdfDocument();
        var layerByOutputPage = new Dictionary<int, List<OcrWord>>();

        var outputIndex = 0;
        foreach (var item in livePages)
        {
            if (item.SourceIndex < 0 || item.SourceIndex >= input.PageCount) continue;

            // AddPage(PdfPage) deep-imports the page from the other document.
            var page = output.AddPage(input.Pages[item.SourceIndex]);

            // PageItem.Rotation counts quarter-turns (matching PDFium's convention);
            // PDFsharp's PdfPage.Rotate is in degrees and only accepts 0/90/180/270.
            if (item.Rotation != 0)
                page.Rotate = ((page.Rotate + item.Rotation * 90) % 360 + 360) % 360;

            if (textLayers is not null && textLayers.TryGetValue(item, out var words) && words.Count > 0)
                layerByOutputPage[outputIndex] = words;

            outputIndex++;
        }

        if (livePages.Count == 0)
            throw new InvalidOperationException("没有可导出的页面。");

        // Replacements are drawn before the invisible text layer so a replaced word's OCR box
        // does not end up describing text that is no longer there.
        ApplyTextReplacements(output, livePages, replacements);
        ApplyTextLayers(output, layerByOutputPage);
        ApplyOverlays(output, overlays);

        using var result = new MemoryStream();
        output.Save(result, closeStream: false);
        return result.ToArray();
    }

    /// <summary>Appends every page of <paramref name="other"/> to <paramref name="source"/>.</summary>
    public static byte[] Append(byte[] source, byte[] other)
    {
        Initialize();

        using var output = new PdfDocument();

        using (var first = new MemoryStream(source, writable: false))
        using (var doc = PdfReader.Open(first, PdfDocumentOpenMode.Import))
        {
            for (var i = 0; i < doc.PageCount; i++)
                output.AddPage(doc.Pages[i]);
        }

        using (var second = new MemoryStream(other, writable: false))
        using (var doc = PdfReader.Open(second, PdfDocumentOpenMode.Import))
        {
            for (var i = 0; i < doc.PageCount; i++)
                output.AddPage(doc.Pages[i]);
        }

        using var result = new MemoryStream();
        output.Save(result, closeStream: false);
        return result.ToArray();
    }

    // ------------------------------------------------------------------ edited text

    /// <summary>
    /// Draws text that was edited but could not be rewritten in place. Each replacement optionally
    /// patches the area first, which is what makes editing a scanned page possible: the original
    /// words only exist as pixels, so they have to be painted over before the new text goes down.
    /// </summary>
    private static void ApplyTextReplacements(PdfDocument document,
        IReadOnlyList<PageItem> livePages, IReadOnlyList<TextReplacement>? replacements)
    {
        if (replacements is null || replacements.Count == 0) return;

        // Which output page each source page landed on.
        var indexOf = new Dictionary<PageItem, int>();
        var position = 0;
        foreach (var item in livePages) indexOf[item] = position++;

        foreach (var group in replacements.Where(r => indexOf.ContainsKey(r.Page))
                                          .GroupBy(r => indexOf[r.Page]))
        {
            if (group.Key < 0 || group.Key >= document.PageCount) continue;

            var page = document.Pages[group.Key];
            var mediaW = page.Width.Point;
            var mediaH = page.Height.Point;
            var quarterTurns = ((page.Rotate / 90) % 4 + 4) % 4;

            var (displayW, displayH) = quarterTurns % 2 == 0
                ? (mediaW, mediaH)
                : (mediaH, mediaW);

            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);

            foreach (var replacement in group)
            {
                var pdfRect = ToPdfUserSpace(replacement.Bounds, displayW, displayH, quarterTurns);
                var boxW = pdfRect.Right - pdfRect.Left;
                var boxH = pdfRect.Bottom - pdfRect.Top;
                if (boxW <= 0.5 || boxH <= 0.5) continue;

                var x = pdfRect.Left;
                var y = mediaH - pdfRect.Bottom;

                if (replacement.RedactBackground)
                {
                    if (replacement.BackgroundPatch is { Length: > 0 } patch)
                    {
                        // Real pixels from a nearby blank area: carries paper texture and any
                        // tint across, so the cover-up does not read as a pasted rectangle.
                        try
                        {
                            using var stream = new MemoryStream(patch, writable: false);
                            using var image = XImage.FromStream(stream);
                            gfx.DrawImage(image, x, y, boxW, boxH);
                        }
                        catch
                        {
                            // A malformed patch should not lose the edit; fall through to flat fill.
                            DrawFlatCover(gfx, replacement, x, y, boxW, boxH);
                        }
                    }
                    else
                    {
                        DrawFlatCover(gfx, replacement, x, y, boxW, boxH);
                    }
                }

                var font = new XFont(replacement.FontFamily, replacement.FontSize);
                var brush = new XSolidBrush(XColor.FromArgb(
                    replacement.R, replacement.G, replacement.B));

                // Shrink to fit rather than overflow the original footprint.
                var natural = gfx.MeasureString(replacement.Text, font).Width;
                var scaleX = natural > 0.5 ? Math.Min(1.0, boxW / natural) : 1.0;
                if (scaleX < 0.2) scaleX = 0.2;

                var state = gfx.Save();
                try
                {
                    gfx.TranslateTransform(x, y + boxH * 0.82);
                    gfx.ScaleTransform(scaleX, 1);
                    gfx.DrawString(replacement.Text, font, brush, new XPoint(0, 0),
                        XStringFormats.Default);
                }
                finally
                {
                    gfx.Restore(state);
                }
            }
        }
    }

    /// <summary>Plain colour cover-up, used when no usable background patch was captured.</summary>
    private static void DrawFlatCover(XGraphics gfx, TextReplacement replacement,
        double x, double y, double boxW, double boxH)
        => gfx.DrawRectangle(
            new XSolidBrush(XColor.FromArgb(
                replacement.BackgroundR, replacement.BackgroundG, replacement.BackgroundB)),
            x, y, boxW, boxH);

    // ------------------------------------------------------------------ OCR text layer

    /// <summary>
    /// Font used for the searchable layer. Any CJK-capable face will do — it is hidden after
    /// export. The name is also how the hiding pass recognises its own objects.
    /// </summary>
    public const string TextLayerFont = "SimHei";

    /// <summary>
    /// How many text objects the layer contributed to each output page. The hiding pass needs
    /// this to walk back over exactly those objects and no others.
    /// </summary>
    public static IReadOnlyDictionary<int, int> CountTextLayerObjects(
        IReadOnlyList<PageItem> livePages,
        IReadOnlyDictionary<PageItem, List<OcrWord>>? textLayers)
    {
        var counts = new Dictionary<int, int>();
        if (textLayers is null) return counts;

        var outputIndex = 0;
        foreach (var item in livePages)
        {
            if (textLayers.TryGetValue(item, out var words) && words.Count > 0)
                counts[outputIndex] = words.Count(w => !string.IsNullOrWhiteSpace(w.Text));

            outputIndex++;
        }

        return counts;
    }

    /// <summary>
    /// Draws recognised text as fully transparent glyphs, making a scanned page searchable and
    /// selectable while leaving its appearance untouched.
    ///
    /// Alpha 0 is used rather than the PDF `3 Tr` invisible mode because PDFsharp's drawing API
    /// exposes no text render mode — and, as the self-test confirms, transparent text is still
    /// extracted and matched by PDFium. The payoff is that PDFsharp subsets the embedded font,
    /// which matters enormously here: a full CJK face is ~9 MB.
    /// </summary>
    private static void ApplyTextLayers(PdfDocument document,
        IReadOnlyDictionary<int, List<OcrWord>> layers)
    {
        if (layers.Count == 0) return;

        var transparent = new XSolidBrush(XColor.FromArgb(0, 0, 0, 0));

        foreach (var (pageIndex, words) in layers)
        {
            if (pageIndex < 0 || pageIndex >= document.PageCount) continue;

            var page = document.Pages[pageIndex];

            // OCR boxes are in the display space the user saw. PDFsharp draws in the page's
            // unrotated MediaBox space with Y increasing downward, so undo /Rotate and flip.
            var rotate = page.Rotate;
            var mediaW = page.Width.Point;
            var mediaH = page.Height.Point;
            var quarterTurns = ((rotate / 90) % 4 + 4) % 4;

            var (displayW, displayH) = quarterTurns % 2 == 0
                ? (mediaW, mediaH)
                : (mediaH, mediaW);

            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);

            foreach (var word in words)
            {
                if (string.IsNullOrWhiteSpace(word.Text)) continue;

                var pdfRect = ToPdfUserSpace(word.Bounds, displayW, displayH, quarterTurns);

                // PDFsharp's origin is the top-left of the MediaBox, Y down.
                var boxW = pdfRect.Right - pdfRect.Left;
                var boxH = pdfRect.Bottom - pdfRect.Top;
                if (boxW <= 0.5 || boxH <= 0.5) continue;

                var x = pdfRect.Left;
                var y = mediaH - pdfRect.Bottom;

                // CJK glyphs fill roughly 0.88 em; this keeps the invisible run close to the
                // height of the words it describes, which is what selection rectangles use.
                var font = new XFont(TextLayerFont, boxH * 0.88);

                var natural = gfx.MeasureString(word.Text, font).Width;
                var scaleX = natural > 0.5 ? boxW / natural : 1.0;
                if (scaleX is < 0.2 or > 5.0) scaleX = 1.0;

                var state = gfx.Save();
                try
                {
                    gfx.TranslateTransform(x, y + boxH * 0.82); // baseline, allowing for the descender
                    gfx.ScaleTransform(scaleX, 1);
                    gfx.DrawString(word.Text, font, transparent, new XPoint(0, 0), XStringFormats.Default);
                }
                finally
                {
                    gfx.Restore(state);
                }
            }
        }
    }

    /// <summary>
    /// Display space (top-left origin, /Rotate applied) → PDF user space (bottom-left origin).
    /// Mirrors <c>PdfiumEngine.DisplayToPdfSpace</c> so the two agree on where a box lands.
    /// </summary>
    private static TextRect ToPdfUserSpace(TextRect box, double displayW, double displayH, int rotation)
    {
        var (w, h) = rotation is 1 or 3 ? (displayH, displayW) : (displayW, displayH);

        (double x, double y) Map(double dx, double dy) => (rotation & 3) switch
        {
            0 => (dx, h - dy),
            1 => (dy, dx),
            2 => (w - dx, h - dy),
            _ => (w - dy, h - dx),
        };

        var (x1, y1) = Map(box.Left, box.Top);
        var (x2, y2) = Map(box.Right, box.Bottom);

        return new TextRect(
            Math.Min(x1, x2), Math.Min(y1, y2),
            Math.Max(x1, x2), Math.Max(y1, y2));
    }

    // ------------------------------------------------------------------ overlays

    private static void ApplyOverlays(PdfDocument document, IReadOnlyList<OverlaySpec> overlays)
    {
        if (overlays.Count == 0) return;

        var total = document.PageCount;

        for (var i = 0; i < total; i++)
        {
            var page = document.Pages[i];
            var pageW = page.Width.Point;
            var pageH = page.Height.Point;

            // Append keeps existing content and layers the overlay on top of it.
            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);

            foreach (var overlay in overlays)
            {
                switch (overlay.Kind)
                {
                    case OverlayKind.Watermark:
                        DrawWatermark(gfx, overlay, pageW, pageH);
                        break;
                    case OverlayKind.PageNumber:
                        DrawPageNumber(gfx, overlay, pageW, pageH, i + 1, total);
                        break;
                    case OverlayKind.Text:
                        DrawText(gfx, overlay, pageW, pageH);
                        break;
                    case OverlayKind.Image:
                        DrawImage(gfx, overlay, pageW, pageH);
                        break;
                }
            }
        }
    }

    private static XBrush BrushFor(OverlaySpec overlay)
    {
        var alpha = (int)Math.Round(Math.Clamp(overlay.Opacity, 0, 1) * 255);
        return new XSolidBrush(XColor.FromArgb(alpha, overlay.R, overlay.G, overlay.B));
    }

    private static XFont FontFor(OverlaySpec overlay)
        => new(overlay.FontFamily, overlay.FontSize, XFontStyleEx.Regular);

    private static void DrawWatermark(XGraphics gfx, OverlaySpec overlay, double pageW, double pageH)
    {
        if (string.IsNullOrEmpty(overlay.Text)) return;

        var font = FontFor(overlay);
        var brush = BrushFor(overlay);

        // Rotate about the page centre, then draw the text centred on the origin.
        var state = gfx.Save();
        gfx.TranslateTransform(pageW / 2 + overlay.OffsetX, pageH / 2 + overlay.OffsetY);
        gfx.RotateTransform(-overlay.Angle);
        gfx.DrawString(overlay.Text, font, brush, new XPoint(0, 0), XStringFormats.Center);
        gfx.Restore(state);
    }

    private static void DrawPageNumber(XGraphics gfx, OverlaySpec overlay,
        double pageW, double pageH, int number, int total)
    {
        if (overlay.FirstPageSkipped && number == 1) return;

        var label = string.Format(overlay.NumberFormat,
            number + overlay.NumberStart - 1, total + overlay.NumberStart - 1);

        var font = FontFor(overlay);
        var brush = BrushFor(overlay);
        var size = gfx.MeasureString(label, font);

        var point = AnchorPoint(overlay.Anchor, pageW, pageH, size.Width, size.Height,
            overlay.OffsetX, overlay.OffsetY);

        gfx.DrawString(label, font, brush, point, XStringFormats.TopLeft);
    }

    private static void DrawText(XGraphics gfx, OverlaySpec overlay, double pageW, double pageH)
    {
        if (string.IsNullOrEmpty(overlay.Text)) return;

        var font = FontFor(overlay);
        var brush = BrushFor(overlay);

        if (overlay.Angle == 0)
        {
            var size = gfx.MeasureString(overlay.Text, font);
            var point = AnchorPoint(overlay.Anchor, pageW, pageH, size.Width, size.Height,
                overlay.OffsetX, overlay.OffsetY);
            gfx.DrawString(overlay.Text, font, brush, point, XStringFormats.TopLeft);
            return;
        }

        var state = gfx.Save();
        gfx.TranslateTransform(pageW / 2 + overlay.OffsetX, pageH / 2 + overlay.OffsetY);
        gfx.RotateTransform(-overlay.Angle);
        gfx.DrawString(overlay.Text, font, brush, new XPoint(0, 0), XStringFormats.Center);
        gfx.Restore(state);
    }

    private static void DrawImage(XGraphics gfx, OverlaySpec overlay, double pageW, double pageH)
    {
        if (string.IsNullOrEmpty(overlay.ImagePath) || !File.Exists(overlay.ImagePath)) return;

        using var image = XImage.FromFile(overlay.ImagePath);
        var width = pageW * Math.Clamp(overlay.ImageScale, 0.02, 1.0);
        var height = width * image.PointHeight / image.PointWidth;

        var point = AnchorPoint(overlay.Anchor, pageW, pageH, width, height,
            overlay.OffsetX, overlay.OffsetY);

        gfx.DrawImage(image, point.X, point.Y, width, height);
    }

    /// <summary>Top-left corner for content of the given size, placed by anchor and offset.</summary>
    private static XPoint AnchorPoint(OverlayAnchor anchor, double pageW, double pageH,
        double contentW, double contentH, double offsetX, double offsetY)
    {
        var x = anchor switch
        {
            OverlayAnchor.TopLeft or OverlayAnchor.MiddleLeft or OverlayAnchor.BottomLeft => 0,
            OverlayAnchor.TopCenter or OverlayAnchor.Center or OverlayAnchor.BottomCenter
                => (pageW - contentW) / 2,
            _ => pageW - contentW,
        };

        var y = anchor switch
        {
            OverlayAnchor.TopLeft or OverlayAnchor.TopCenter or OverlayAnchor.TopRight => 0,
            OverlayAnchor.MiddleLeft or OverlayAnchor.Center or OverlayAnchor.MiddleRight
                => (pageH - contentH) / 2,
            _ => pageH - contentH,
        };

        return new XPoint(x + offsetX, y + offsetY);
    }
}
