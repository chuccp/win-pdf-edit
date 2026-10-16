using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using PdfEdit.Models;

namespace PdfEdit.Services;

/// <summary>
/// Feeds rendered pages to WPF's printing pipeline one at a time.
///
/// Pages are rasterised straight from the engine rather than through <see cref="RenderService"/>:
/// a 300 DPI print bitmap dwarfs the screen-resolution cache and would evict every entry the
/// viewer is still using. Print rasters are transient — used, drawn, collected.
/// </summary>
public sealed class PdfPrintPaginator : DocumentPaginator
{
    /// <summary>Print rasterisation is capped here; higher eats memory with little visible gain.</summary>
    private const int MaxDpi = 600;

    private readonly PdfiumEngine _engine;
    private readonly IReadOnlyList<PageItem> _pages;
    private readonly double _printableWidth;
    private readonly double _printableHeight;
    private readonly double _dpi;

    /// <param name="printableWidth">Printer's printable width in DIPs (1/96 inch).</param>
    /// <param name="printableHeight">Printer's printable height in DIPs.</param>
    /// <param name="dpi">Raster resolution; the PDF point size is 1/72 inch.</param>
    public PdfPrintPaginator(PdfiumEngine engine, IReadOnlyList<PageItem> pages,
        double printableWidth, double printableHeight, int dpi)
    {
        _engine = engine;
        _pages = pages;
        _printableWidth = printableWidth;
        _printableHeight = printableHeight;
        _dpi = Math.Clamp(dpi, 96, MaxDpi);
    }

    public override bool IsPageCountValid => true;

    public override int PageCount => _pages.Count;

    /// <summary>Reported size of the first page; each DocumentPage carries its own.</summary>
    public override Size PageSize { get; set; }

    public override IDocumentPaginatorSource Source => null!;

    public override DocumentPage GetPage(int pageNumber)
    {
        var item = _pages[pageNumber];
        var size = _engine.GetPageSize(item.SourceIndex);

        // PDF points → DIPs. The page is fit inside the printable area, aspect preserved.
        var pageW = size.Width * 96.0 / 72.0;
        var pageH = size.Height * 96.0 / 72.0;
        var fit = Math.Min(_printableWidth / pageW, _printableHeight / pageH);
        var width = pageW * fit;
        var height = pageH * fit;

        // points × (dpi/72) = pixels at the printer's resolution.
        var bitmap = _engine.RenderPage(item.SourceIndex, _dpi / 72.0,
            item.Rotation, includeAnnotations: true);

        var image = new Image
        {
            Source = bitmap,
            Stretch = Stretch.Uniform,
            Width = width,
            Height = height,
        };

        var visual = new Border
        {
            Width = width,
            Height = height,
            Background = Brushes.White,
            Child = image,
        };

        // This runtime's DocumentPage only has the full bleedBox/contentBox constructor.
        var box = new Rect(0, 0, width, height);
        return new DocumentPage(visual, new Size(width, height), box, box);
    }
}
