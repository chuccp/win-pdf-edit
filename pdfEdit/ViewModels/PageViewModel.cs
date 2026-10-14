using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfEdit.Common;
using PdfEdit.Models;

namespace PdfEdit.ViewModels;

/// <summary>How far along a page's rasterisation is.</summary>
public enum RenderState
{
    Pending,
    Rendering,
    Ready,
    Failed,
}

/// <summary>
/// A pending annotation flattened into something a WPF canvas can draw directly.
/// Coordinates are in device-independent pixels at the current zoom.
/// </summary>
public sealed class AnnotationVisual
{
    public required Rect Bounds { get; init; }
    public Brush? Fill { get; init; }
    public Brush? Stroke { get; init; }
    public double StrokeThickness { get; init; } = 1;
    public bool IsEllipse { get; init; }
    public bool IsNote { get; init; }
    public PointCollection? InkPoints { get; init; }
    public AnnotationSpec? Source { get; init; }

    /// <summary>
    /// True for underline and strikeout, which are a single rule rather than an outlined box.
    /// </summary>
    public bool IsLine { get; init; }

    /// <summary>Where that rule sits vertically within the bounds, in DIPs.</summary>
    public double LineY { get; init; }
}

/// <summary>
/// One page in the viewer: its geometry, its rendered bitmap, and the annotations drawn over it.
/// </summary>
public sealed class PageViewModel : ObservableObject
{
    public PageItem Item { get; }
    public int SourceIndex => Item.SourceIndex;

    /// <summary>Page size in PDF points, as displayed (page's own /Rotate already applied).</summary>
    public double WidthInPoints { get; }
    public double HeightInPoints { get; }

    public ObservableCollection<AnnotationVisual> Annotations { get; } = [];

    /// <summary>
    /// Boxes of the text recognised on this page, in device pixels. Drawn as an overlay so the
    /// result of an OCR run is actually visible — the text layer itself is invisible by design,
    /// which otherwise makes a successful recognition look like nothing happened.
    /// </summary>
    public ObservableCollection<Rect> OcrBoxes { get; } = [];

    /// <summary>
    /// Every editable text region on the page, in device pixels. Drawn while the content-edit
    /// tool is armed so it is obvious what can be clicked, the way mainstream editors outline
    /// editable text.
    /// </summary>
    public ObservableCollection<Rect> EditableBoxes { get; } = [];

    /// <summary>
    /// Where the current search matched on this page, in device pixels. Without this a search
    /// reports hits that the user cannot see or locate on the page.
    /// </summary>
    public ObservableCollection<Rect> SearchHighlights { get; } = [];

    public void RebuildSearchHighlights(IEnumerable<TextRect> rects)
    {
        SearchHighlights.Clear();

        foreach (var r in rects)
        {
            SearchHighlights.Add(new Rect(
                r.Left * Zoom, r.Top * Zoom,
                Math.Max(2, (r.Right - r.Left) * Zoom),
                Math.Max(2, (r.Bottom - r.Top) * Zoom)));
        }
    }

    public void RebuildEditableBoxes(IEnumerable<TextRect> regions)
    {
        EditableBoxes.Clear();

        foreach (var region in regions)
        {
            EditableBoxes.Add(new Rect(
                region.Left * Zoom, region.Top * Zoom,
                Math.Max(1, (region.Right - region.Left) * Zoom),
                Math.Max(1, (region.Bottom - region.Top) * Zoom)));
        }
    }

    public PageViewModel(PageItem item, double widthInPoints, double heightInPoints)
    {
        Item = item;
        WidthInPoints = widthInPoints;
        HeightInPoints = heightInPoints;
    }

    private double _zoom = 1;
    public double Zoom
    {
        get => _zoom;
        set
        {
            if (!SetField(ref _zoom, value)) return;
            OnPropertyChanged(nameof(DisplayWidth));
            OnPropertyChanged(nameof(DisplayHeight));
        }
    }

    public double DisplayWidth => WidthInPoints * Zoom;
    public double DisplayHeight => HeightInPoints * Zoom;

    private BitmapSource? _image;
    public BitmapSource? Image
    {
        get => _image;
        set
        {
            if (!SetField(ref _image, value)) return;
            OnPropertyChanged(nameof(HasImage));
        }
    }

    public bool HasImage => _image is not null;

    private BitmapSource? _thumbnail;
    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set => SetField(ref _thumbnail, value);
    }

    /// <summary>Rebuilds the OCR overlay for the current zoom.</summary>
    public void RebuildOcrBoxes(IEnumerable<OcrWord> words)
    {
        OcrBoxes.Clear();

        foreach (var word in words)
        {
            var b = word.Bounds;
            OcrBoxes.Add(new Rect(
                b.Left * Zoom, b.Top * Zoom,
                Math.Max(1, (b.Right - b.Left) * Zoom),
                Math.Max(1, (b.Bottom - b.Top) * Zoom)));
        }
    }

    private Rect? _selectionBounds;
    /// <summary>Outline drawn around the page object currently picked for content editing.</summary>
    public Rect? SelectionBounds
    {
        get => _selectionBounds;
        set
        {
            if (SetField(ref _selectionBounds, value)) OnPropertyChanged(nameof(HasSelection));
        }
    }

    public bool HasSelection => _selectionBounds is not null;

    private RenderState _state = RenderState.Pending;
    public RenderState State
    {
        get => _state;
        set
        {
            if (!SetField(ref _state, value)) return;
            OnPropertyChanged(nameof(IsBusy));
        }
    }

    public bool IsBusy => _state is RenderState.Pending or RenderState.Rendering;

    /// <summary>Shown in the status bar and the thumbnail caption.</summary>
    public string Caption => Item.IsDeleted ? "—" : Item.DisplayNumber.ToString();

    public void RefreshCaption() => OnPropertyChanged(nameof(Caption));

    /// <summary>
    /// Rebuilds the drawable annotation list. Called when the zoom changes or the annotation
    /// set is edited, since both change the device-space geometry.
    /// </summary>
    public void RebuildAnnotations(IEnumerable<AnnotationSpec> specs)
    {
        Annotations.Clear();

        foreach (var spec in specs)
        {
            var color = Color.FromRgb(spec.R, spec.G, spec.B);
            var fillAlpha = spec.Kind is AnnotationKind.Highlight or AnnotationKind.Rectangle
                or AnnotationKind.Ellipse
                ? spec.A
                : (byte)0;

            Annotations.Add(new AnnotationVisual
            {
                Bounds = new Rect(
                    spec.Left * Zoom, spec.Top * Zoom,
                    Math.Max(1, (spec.Right - spec.Left) * Zoom),
                    Math.Max(1, (spec.Bottom - spec.Top) * Zoom)),
                Fill = fillAlpha > 0
                    ? new SolidColorBrush(Color.FromArgb(fillAlpha, spec.R, spec.G, spec.B))
                    : null,
                Stroke = spec.Kind is AnnotationKind.Note ? null : new SolidColorBrush(color),
                StrokeThickness = spec.Kind switch
                {
                    AnnotationKind.Underline or AnnotationKind.StrikeOut => 2,
                    AnnotationKind.Rectangle or AnnotationKind.Ellipse => 1.5,
                    AnnotationKind.Ink => 1.8,
                    _ => 0,
                },
                IsEllipse = spec.Kind == AnnotationKind.Ellipse,
                IsNote = spec.Kind == AnnotationKind.Note,
                IsLine = spec.Kind is AnnotationKind.Underline or AnnotationKind.StrikeOut,
                // Underline hugs the baseline; strikeout runs through the middle of the text.
                LineY = spec.Kind == AnnotationKind.Underline
                    ? (spec.Bottom - spec.Top) * Zoom
                    : (spec.Bottom - spec.Top) * Zoom / 2,
                InkPoints = BuildInkPoints(spec),
                Source = spec,
            });
        }
    }

    private PointCollection? BuildInkPoints(AnnotationSpec spec)
    {
        if (spec.Kind != AnnotationKind.Ink || spec.InkStrokes.Count == 0) return null;

        var collection = new PointCollection();
        foreach (var stroke in spec.InkStrokes)
        foreach (var (x, y) in stroke.Points)
            collection.Add(new Point(x * Zoom, y * Zoom));

        return collection;
    }
}
