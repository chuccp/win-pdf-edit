using System.Collections.ObjectModel;
using PdfEdit.Common;

namespace PdfEdit.Models;

/// <summary>
/// One page in the editing sequence. It points back at a page of the source document, so
/// reordering or deleting pages never touches the original bytes — the export step replays
/// this list against the source.
/// </summary>
public sealed class PageItem : ObservableObject
{
    /// <summary>Zero-based page index in the source document.</summary>
    public required int SourceIndex { get; init; }

    private int _rotation;
    /// <summary>Extra clockwise quarter-turns applied on top of the page's own /Rotate.</summary>
    public int Rotation
    {
        get => _rotation;
        set
        {
            if (SetField(ref _rotation, ((value % 4) + 4) % 4))
                OnPropertyChanged(nameof(RotationDegrees));
        }
    }

    public int RotationDegrees => Rotation * 90;

    private bool _isDeleted;
    public bool IsDeleted
    {
        get => _isDeleted;
        set => SetField(ref _isDeleted, value);
    }

    /// <summary>1-based position in the current sequence; kept in sync by <see cref="DocumentModel"/>.</summary>
    private int _displayNumber;
    public int DisplayNumber
    {
        get => _displayNumber;
        set => SetField(ref _displayNumber, value);
    }

    /// <summary>Memoisable key for the render cache: identical source + rotation ⇒ identical pixels.</summary>
    public (int Source, int Rotation) RenderKey => (SourceIndex, Rotation);
}

public enum OverlayKind
{
    Watermark,
    PageNumber,
    Text,
    Image,
}

public enum OverlayAnchor
{
    TopLeft, TopCenter, TopRight,
    MiddleLeft, Center, MiddleRight,
    BottomLeft, BottomCenter, BottomRight,
}

/// <summary>
/// Content drawn on top of pages at export time via PDFsharp. Overlays are baked into the
/// page content stream rather than stored as annotations — they are decoration, not markup.
/// </summary>
public sealed class OverlaySpec : ObservableObject
{
    public required OverlayKind Kind { get; init; }

    private string _text = string.Empty;
    public string Text { get => _text; set => SetField(ref _text, value); }

    private string _fontFamily = "SimHei";
    public string FontFamily { get => _fontFamily; set => SetField(ref _fontFamily, value); }

    private double _fontSize = 48;
    public double FontSize { get => _fontSize; set => SetField(ref _fontSize, value); }

    private byte _r = 255, _g = 0, _b = 0;
    public byte R { get => _r; set => SetField(ref _r, value); }
    public byte G { get => _g; set => SetField(ref _g, value); }
    public byte B { get => _b; set => SetField(ref _b, value); }

    /// <summary>0–1.</summary>
    private double _opacity = 0.25;
    public double Opacity { get => _opacity; set => SetField(ref _opacity, value); }

    /// <summary>Clockwise degrees; watermarks conventionally use 45.</summary>
    private double _angle;
    public double Angle { get => _angle; set => SetField(ref _angle, value); }

    private OverlayAnchor _anchor = OverlayAnchor.Center;
    public OverlayAnchor Anchor { get => _anchor; set => SetField(ref _anchor, value); }

    /// <summary>Offset from the anchor, in points.</summary>
    private double _offsetX, _offsetY;
    public double OffsetX { get => _offsetX; set => SetField(ref _offsetX, value); }
    public double OffsetY { get => _offsetY; set => SetField(ref _offsetY, value); }

    // Image overlays
    private string? _imagePath;
    public string? ImagePath { get => _imagePath; set => SetField(ref _imagePath, value); }

    private double _imageScale = 0.5;
    public double ImageScale { get => _imageScale; set => SetField(ref _imageScale, value); }

    // Page numbering
    private int _numberStart = 1;
    public int NumberStart { get => _numberStart; set => SetField(ref _numberStart, value); }

    private string _numberFormat = "{0} / {1}";
    public string NumberFormat { get => _numberFormat; set => SetField(ref _numberFormat, value); }

    private bool _firstPageSkipped;
    public bool FirstPageSkipped { get => _firstPageSkipped; set => SetField(ref _firstPageSkipped, value); }
}

/// <summary>
/// The whole editing session: the untouched source bytes plus everything the user has done to
/// them. Nothing is written back to the source until an explicit export.
/// </summary>
public sealed class DocumentModel : ObservableObject
{
    public byte[] SourceBytes { get; private set; } = [];
    public string? SourcePath { get; private set; }

    /// <summary>
    /// Source bytes with in-place content edits applied, or null when the original content is
    /// untouched. This is the input the exporter starts from: content edits have to be folded in
    /// before PDFsharp rebuilds the page structure, or they would be lost.
    /// </summary>
    public byte[]? EditedBytes { get; set; }

    public ObservableCollection<PageItem> Pages { get; } = [];
    public ObservableCollection<AnnotationSpec> Annotations { get; } = [];

    /// <summary>
    /// OCR results awaiting the export pass, keyed by page.
    ///
    /// These are not written immediately because PDFium embeds a whole CJK font when it creates
    /// text objects (~4.8 MB), while PDFsharp subsets it (~22 KB). So the layer is materialised
    /// by the PDFsharp stage instead, and only exists in the saved file — not in the live document.
    /// </summary>
    public Dictionary<PageItem, List<OcrWord>> TextLayers { get; } = new();

    /// <summary>
    /// Text edits that could not be applied in place and are drawn by the export stage instead.
    /// </summary>
    public ObservableCollection<TextReplacement> TextReplacements { get; } = [];
    public ObservableCollection<OverlaySpec> Overlays { get; } = [];
    public ObservableCollection<OutlineEntry> Outline { get; } = [];

    private bool _isDirty;
    public bool IsDirty
    {
        get => _isDirty;
        set => SetField(ref _isDirty, value);
    }

    /// <summary>Pages that survive to the output, in order.</summary>
    public IReadOnlyList<PageItem> LivePages => Pages.Where(p => !p.IsDeleted).ToList();

    public void Load(byte[] source, string? path)
    {
        SourceBytes = source;
        SourcePath = path;
        EditedBytes = null;
        Pages.Clear();
        Annotations.Clear();
        Overlays.Clear();
        Outline.Clear();
        TextLayers.Clear();
        TextReplacements.Clear();
        IsDirty = false;
        Renumber();
    }

    /// <summary>Refreshes the 1-based display numbers after any reorder or delete.</summary>
    public void Renumber()
    {
        var n = 1;
        foreach (var page in Pages)
            page.DisplayNumber = page.IsDeleted ? 0 : n++;
    }

    /// <summary>
    /// Maps each page to its index in the exported document. Annotations are keyed to
    /// <see cref="PageItem"/>s, so this is what lets them land on the right page after
    /// pages have been reordered or removed.
    /// </summary>
    public Dictionary<PageItem, int> BuildOutputIndexMap()
    {
        var map = new Dictionary<PageItem, int>();
        var index = 0;
        foreach (var page in Pages)
        {
            if (page.IsDeleted) continue;
            map[page] = index++;
        }
        return map;
    }
}
