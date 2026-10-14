namespace PdfEdit.Models;

/// <summary>Page dimensions in PDF points (1/72 inch), already accounting for page rotation.</summary>
public readonly record struct PageSize(double Width, double Height);

/// <summary>
/// A rectangle in page space: origin is the top-left of the displayed page, Y grows downward.
/// This matches PDFium's device space and WPF's canvas, but is NOT raw PDF user space
/// (which is bottom-left origin) — conversion happens at the PDFium boundary.
/// </summary>
public readonly record struct TextRect(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;
}

public sealed record SearchHit(int PageIndex, int StartIndex, int Length, IReadOnlyList<TextRect> Rects)
{
    public string Preview { get; init; } = string.Empty;

    /// <summary>
    /// True when this match came from a pending OCR layer rather than the PDF's own text.
    /// Such hits have no character offset in the document (<see cref="StartIndex"/> is -1)
    /// and exist only until the layer is written out on save.
    /// </summary>
    public bool FromOcr { get; init; }
}

public sealed record OutlineEntry(string Title, int PageIndex, int Depth);

/// <summary>
/// A drawable object inside a page's content stream.
///
/// <paramref name="Path"/> addresses the object through the page's object tree: a single index
/// for a top-level object, or a chain of indices when it lives inside a nested Form XObject.
/// Content is routinely wrapped in forms, so a page-level scan alone misses most real text.
/// </summary>
public sealed record PageObjectInfo(
    IReadOnlyList<int> Path,
    int Depth,
    int Type,
    TextRect Bounds,
    string Text,
    string FontName,
    double FontSize)
{
    public bool IsText => Type == 1;
    public bool IsImage => Type == 3;
    public bool IsForm => Type == 5;

    public string TypeLabel => Type switch
    {
        1 => "文字",
        2 => "图形",
        3 => "图像",
        4 => "渐变",
        5 => "容器",
        _ => "未知",
    };

    /// <summary>Bounding box in display space, for hit-testing against pointer coordinates.</summary>
    public bool Contains(double x, double y) =>
        x >= Bounds.Left && x <= Bounds.Right && y >= Bounds.Top && y <= Bounds.Bottom;
}

/// <summary>
/// A word recovered by OCR, with its position in display space (top-left origin, points).
///
/// <paramref name="LineIndex"/> groups words that sit on the same line. The recogniser often
/// returns one word per character for Chinese, so matching a multi-character term requires
/// reassembling the line first.
/// </summary>
public sealed record OcrWord(string Text, TextRect Bounds, int LineIndex = 0);

/// <summary>
/// A piece of page text that was edited and could not be rewritten in place — because the font
/// embedded in the PDF has no glyph for the new characters.
///
/// Mainstream editors do not refuse such an edit; they paint a substitute font. That is what
/// happens here: the original object is deleted and the replacement is drawn by the export
/// stage, which can embed a fresh subset. Nothing is written until save, so it can be cancelled
/// right up to that point.
/// </summary>
public sealed class TextReplacement
{
    public required PageItem Page { get; init; }

    /// <summary>
    /// Area the text occupies, in display space at the time of editing. Settable because a
    /// revision may need a wider box than the string it replaces.
    /// </summary>
    public required TextRect Bounds { get; set; }

    public required string Text { get; set; }
    public double FontSize { get; set; } = 12;
    public string FontFamily { get; set; } = "SimHei";

    public byte R { get; set; } = 20;
    public byte G { get; set; } = 20;
    public byte B { get; set; } = 20;

    /// <summary>
    /// Fill the original area with <see cref="BackgroundR/G/B"/> before drawing. Needed on
    /// scanned pages, where the old text exists only as pixels and would otherwise show through.
    /// </summary>
    public bool RedactBackground { get; set; }

    public byte BackgroundR { get; set; } = 255;
    public byte BackgroundG { get; set; } = 255;
    public byte BackgroundB { get; set; } = 255;

    /// <summary>
    /// Background patch (PNG) lifted from a clean area of the same page, drawn over the original
    /// words before the new text goes down.
    ///
    /// A flat fill sampled from around the text leaves a visible rectangle wherever the page is
    /// not uniform — paper texture, tinted scans, gradients, stamps. Copying real pixels from a
    /// nearby blank region carries that texture across, so the patch blends in.
    /// </summary>
    public byte[]? BackgroundPatch { get; set; }

    /// <summary>
    /// Page object this replacement supersedes, deleted at export.
    ///
    /// The deletion is deferred rather than done when the user applies the edit: a removed
    /// object cannot be put back, which would make undo impossible. Deferring keeps the whole
    /// operation reversible — undoing simply drops this record.
    /// </summary>
    public IReadOnlyList<int>? HideObjectPath { get; init; }
}

/// <summary>Result of attempting to rewrite a text object's content.</summary>
public enum TextEditOutcome
{
    Applied,

    ObjectNotFound,
    NotATextObject,

    /// <summary>
    /// The object's font cannot represent the requested characters. The PDF almost certainly
    /// embeds a subset of the font containing only the glyphs its original text used, so the
    /// new text would render blank. The edit was rolled back and nothing was changed.
    /// </summary>
    GlyphsUnavailable,
}

public enum AnnotationKind
{
    Highlight,
    Underline,
    StrikeOut,
    Squiggly,
    Note,
    Rectangle,
    Ellipse,
    Ink,
    FreeText,
}

public sealed record InkStroke(IReadOnlyList<(double X, double Y)> Points);

/// <summary>
/// A pending annotation held in the editing model. It is drawn as a WPF overlay while editing
/// and materialised into a real PDF annotation object at save time.
/// </summary>
public sealed class AnnotationSpec
{
    public required AnnotationKind Kind { get; init; }

    /// <summary>
    /// Index of the page this annotation is drawn on, in the coordinates of whatever document
    /// is currently loaded. When <see cref="Page"/> is set, the exporter recomputes this from
    /// the page's position in the output, so annotations follow their page through reordering.
    /// </summary>
    public required int PageIndex { get; set; }

    /// <summary>The page this annotation belongs to. Survives reorder, delete and duplicate.</summary>
    public PageItem? Page { get; set; }

    /// <summary>
    /// Extra quarter-turns the export step applies to this page beyond what it had when the
    /// annotation was drawn. The annotation's coordinates are in the display space the user
    /// actually saw, so this is subtracted when converting back to PDF user space.
    /// </summary>
    public int PageExtraRotation { get; set; }

    /// <summary>Bounds in page points, top-left origin.</summary>
    public double Left { get; set; }
    public double Top { get; set; }
    public double Right { get; set; }
    public double Bottom { get; set; }

    public byte R { get; set; } = 255;
    public byte G { get; set; } = 214;
    public byte B { get; set; } = 0;
    public byte A { get; set; } = 100;

    public string? Contents { get; set; }
    public string? Author { get; set; }

    public List<InkStroke> InkStrokes { get; } = new();

    /// <summary>
    /// Per-line quadrilaterals for text-markup kinds, in display space. A highlight spanning
    /// three lines needs three quads; PDFium draws markup annotations from these rather than
    /// from the bounding box. When empty, the bounding box is used as a single quad.
    /// </summary>
    public List<TextRect> Quads { get; } = new();

    public TextRect Bounds => new(Left, Top, Right, Bottom);

    public void SetBounds(TextRect r)
    {
        Left = r.Left;
        Top = r.Top;
        Right = r.Right;
        Bottom = r.Bottom;
    }
}
