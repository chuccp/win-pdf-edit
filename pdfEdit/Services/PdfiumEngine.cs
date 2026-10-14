using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfEdit.Interop;
using PdfEdit.Models;

namespace PdfEdit.Services;

/// <summary>
/// Owns a PDFium document plus the pinned byte buffer it was loaded from.
///
/// PDFium does not copy the buffer passed to <c>FPDF_LoadMemDocument64</c> — it must stay
/// valid and immovable for the lifetime of the document, hence the pinned <see cref="GCHandle"/>.
///
/// PDFium documents are not thread-safe. Every entry point takes <see cref="_sync"/>, so a single
/// engine serialises its own calls; separate engines on separate threads still need external care
/// because the PDFium library itself has process-wide state.
/// </summary>
public sealed class PdfiumEngine : IDisposable
{
    private static readonly object LibrarySync = new();
    private static bool _libraryReady;

    private readonly object _sync = new();
    private readonly byte[] _data;
    private GCHandle _pinned;
    private IntPtr _document;
    private bool _disposed;

    public int PageCount { get; }
    public string? SourcePath { get; }

    private PdfiumEngine(byte[] data, IntPtr document, string? sourcePath)
    {
        _data = data;
        _document = document;
        SourcePath = sourcePath;
        PageCount = PdfiumNative.FPDF_GetPageCount(document);
    }

    // ------------------------------------------------------------------ library

    public static void Initialize()
    {
        lock (LibrarySync)
        {
            if (_libraryReady) return;

            var config = new PdfiumNative.FPDF_LIBRARY_CONFIG
            {
                version = 2,
                m_pUserFontPaths = IntPtr.Zero,
                m_pIsolate = IntPtr.Zero,
                m_v8EmbedderSlot = 0,
                m_pPlatform = IntPtr.Zero,
                m_RendererType = 0,
            };
            PdfiumNative.FPDF_InitLibraryWithConfig(ref config);
            _libraryReady = true;
        }
    }

    public static void Shutdown()
    {
        lock (LibrarySync)
        {
            if (!_libraryReady) return;
            PdfiumNative.FPDF_DestroyLibrary();
            _libraryReady = false;
        }
    }

    private static void ThrowLastError(string what)
    {
        var err = (PdfiumError)PdfiumNative.FPDF_GetLastError();
        throw new InvalidOperationException($"{what} 失败：{Describe(err)}");
    }

    private static string Describe(PdfiumError e) => e switch
    {
        PdfiumError.FileError => "文件无法读取",
        PdfiumError.FormatError => "不是有效的 PDF，或文件已损坏",
        PdfiumError.PasswordError => "文档已加密，需要密码",
        PdfiumError.SecurityError => "文档被安全策略阻止",
        PdfiumError.PageError => "页面索引超出范围",
        _ => $"PDFium 错误 ({(int)e})",
    };

    // ------------------------------------------------------------------ open / dispose

    public static PdfiumEngine Open(byte[] data, string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0) throw new InvalidOperationException("文件为空。");

        Initialize();
        return OpenCore(data, sourcePath);
    }

    public static PdfiumEngine OpenFile(string path)
        => Open(File.ReadAllBytes(path), path);

    private static PdfiumEngine OpenCore(byte[] data, string? sourcePath)
    {
        var pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
        IntPtr doc;
        try
        {
            doc = PdfiumNative.FPDF_LoadMemDocument64(
                pinned.AddrOfPinnedObject(), (nuint)data.Length, null);
        }
        catch
        {
            pinned.Free();
            throw;
        }

        if (doc == IntPtr.Zero)
        {
            pinned.Free();
            var err = (PdfiumError)PdfiumNative.FPDF_GetLastError();
            throw new InvalidOperationException(Describe(err));
        }

        var engine = new PdfiumEngine(data, doc, sourcePath);
        engine._pinned = pinned;
        return engine;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;

            if (_document != IntPtr.Zero)
            {
                PdfiumNative.FPDF_CloseDocument(_document);
                _document = IntPtr.Zero;
            }
        }

        if (_pinned.IsAllocated) _pinned.Free();
    }

    // ------------------------------------------------------------------ page metrics

    /// <summary>Displayed page size in points, accounting for the page's own /Rotate entry.</summary>
    public PageSize GetPageSize(int pageIndex)
    {
        lock (_sync)
        {
            var page = PdfiumNative.FPDF_LoadPage(_document, pageIndex);
            if (page == IntPtr.Zero) ThrowLastError($"读取第 {pageIndex + 1} 页");
            try
            {
                return new PageSize(
                    PdfiumNative.FPDF_GetPageWidthF(page),
                    PdfiumNative.FPDF_GetPageHeightF(page));
            }
            finally
            {
                PdfiumNative.FPDF_ClosePage(page);
            }
        }
    }

    /// <summary>Number of annotation objects attached to a page.</summary>
    public int GetAnnotationCount(int pageIndex)
    {
        lock (_sync)
        {
            var page = PdfiumNative.FPDF_LoadPage(_document, pageIndex);
            if (page == IntPtr.Zero) return 0;
            try
            {
                return PdfiumNative.FPDFPage_GetAnnotCount(page);
            }
            finally
            {
                PdfiumNative.FPDF_ClosePage(page);
            }
        }
    }

    /// <summary>Intrinsic /Rotate of the page: 0, 1, 2 or 3 quarter-turns clockwise.</summary>
    public int GetPageRotation(int pageIndex)
    {
        lock (_sync)
        {
            var page = PdfiumNative.FPDF_LoadPage(_document, pageIndex);
            if (page == IntPtr.Zero) ThrowLastError($"读取第 {pageIndex + 1} 页");
            try
            {
                return PdfiumNative.FPDFPage_GetRotation(page);
            }
            finally
            {
                PdfiumNative.FPDF_ClosePage(page);
            }
        }
    }

    /// <summary>
    /// Flips a display-space rect (top-left origin, Y down) into PDF user space
    /// (bottom-left origin, Y up), undoing the page's own rotation on the way.
    /// </summary>
    internal static TextRect DisplayToPdfSpace(TextRect r, double displayW, double displayH, int rotation)
    {
        // Unrotated page dimensions. For odd quarter-turns the displayed page is landscape.
        var (w, h) = rotation is 1 or 3 ? (displayH, displayW) : (displayW, displayH);

        (double x, double y) Map(double dx, double dy) => (rotation & 3) switch
        {
            0 => (dx, h - dy),
            1 => (dy, dx),
            2 => (w - dx, h - dy),
            _ => (w - dy, h - dx),
        };

        // Map both corners, then re-normalise: the flip can swap min/max.
        var (x1, y1) = Map(r.Left, r.Top);
        var (x2, y2) = Map(r.Right, r.Bottom);

        return new TextRect(
            Math.Min(x1, x2), Math.Min(y1, y2),
            Math.Max(x1, x2), Math.Max(y1, y2));
    }

    /// <summary>
    /// Inverse of <see cref="DisplayToPdfSpace"/>: turns PDF user space (bottom-left origin,
    /// Y up) into display space (top-left origin, Y down), applying the page's rotation.
    ///
    /// PDFium's text-rectangle API reports PDF user space, which is why a rect with a
    /// numerically larger "top" than "bottom" is normal rather than malformed.
    /// </summary>
    internal static TextRect PdfToDisplaySpace(TextRect r, double displayW, double displayH, int rotation)
    {
        var (w, h) = rotation is 1 or 3 ? (displayH, displayW) : (displayW, displayH);

        (double x, double y) Map(double px, double py) => (rotation & 3) switch
        {
            0 => (px, h - py),
            1 => (py, px),
            2 => (w - px, h - py),
            _ => (h - py, w - px),
        };

        var (x1, y1) = Map(r.Left, r.Top);
        var (x2, y2) = Map(r.Right, r.Bottom);

        // Re-normalise: the un-rotation can turn the input's top/bottom ordering inside out.
        return new TextRect(
            Math.Min(x1, x2), Math.Min(y1, y2),
            Math.Max(x1, x2), Math.Max(y1, y2));
    }

    // ------------------------------------------------------------------ rendering

    /// <summary>
    /// Renders a page to a frozen BGRA bitmap. Safe to call off the UI thread; the result is
    /// frozen so it can be handed straight to the UI.
    /// </summary>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <param name="scale">Zoom factor, 1.0 = 72 DPI.</param>
    /// <param name="extraRotation">Additional quarter-turns clockwise applied on top of the page's own /Rotate.</param>
    /// <param name="includeAnnotations">Draw embedded PDF annotations into the bitmap.</param>
    /// <param name="grayscale">Render in greyscale.</param>
    public BitmapSource RenderPage(int pageIndex, double scale, int extraRotation = 0,
        bool includeAnnotations = true, bool grayscale = false)
    {
        if (scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var page = PdfiumNative.FPDF_LoadPage(_document, pageIndex);
            if (page == IntPtr.Zero) ThrowLastError($"渲染第 {pageIndex + 1} 页");

            IntPtr bitmap = IntPtr.Zero;
            try
            {
                // FPDF_RenderPageBitmap applies the page's own /Rotate by itself, and
                // FPDF_GetPageWidthF/HeightF likewise already report the rotated size — so the
                // `rotate` argument must carry ONLY the extra turn the user asked for. Passing
                // the intrinsic rotation as well double-rotates the page.
                var rotation = ((extraRotation % 4) + 4) % 4;

                var displayW = (double)PdfiumNative.FPDF_GetPageWidthF(page);
                var displayH = (double)PdfiumNative.FPDF_GetPageHeightF(page);

                if (extraRotation % 2 != 0)
                    (displayW, displayH) = (displayH, displayW);

                var pixelW = Math.Max(1, (int)Math.Round(displayW * scale));
                var pixelH = Math.Max(1, (int)Math.Round(displayH * scale));

                // Guard against absurd allocations from a runaway zoom level.
                const long maxPixels = 80_000_000; // ~80 MP ≈ 320 MB at 4 bytes/px
                if ((long)pixelW * pixelH > maxPixels)
                {
                    var shrink = Math.Sqrt(maxPixels / (double)((long)pixelW * pixelH));
                    pixelW = Math.Max(1, (int)(pixelW * shrink));
                    pixelH = Math.Max(1, (int)(pixelH * shrink));
                }

                var stride = pixelW * 4;
                bitmap = PdfiumNative.FPDFBitmap_CreateEx(pixelW, pixelH,
                    PdfiumBitmapFormat.Bgra, IntPtr.Zero, stride);

                if (bitmap == IntPtr.Zero)
                    throw new InvalidOperationException("无法分配渲染缓冲区（图像过大）。");

                // White background: PDF pages are transparent by default, and WPF would
                // otherwise composite them onto black.
                PdfiumNative.FPDFBitmap_FillRect(bitmap, 0, 0, pixelW, pixelH, 0xFFFFFFFF);

                var flags = includeAnnotations ? PdfiumRenderFlags.Annot : 0;
                if (grayscale) flags |= PdfiumRenderFlags.Grayscale;

                PdfiumNative.FPDF_RenderPageBitmap(bitmap, page, 0, 0, pixelW, pixelH, rotation, flags);

                return Snapshot(bitmap, pixelW, pixelH, stride);
            }
            finally
            {
                if (bitmap != IntPtr.Zero) PdfiumNative.FPDFBitmap_Destroy(bitmap);
                PdfiumNative.FPDF_ClosePage(page);
            }
        }
    }

    private static BitmapSource Snapshot(IntPtr bitmap, int width, int height, int stride)
    {
        var buffer = PdfiumNative.FPDFBitmap_GetBuffer(bitmap);
        if (buffer == IntPtr.Zero) throw new InvalidOperationException("渲染缓冲区不可读。");

        // FPDFBitmap_BGRA is B,G,R,A in memory on little-endian, which is exactly
        // WPF's Bgra32 — no channel swizzle needed.
        var bmp = BitmapSource.Create(width, height, 96, 96,
            PixelFormats.Bgra32, null, buffer, stride * height, stride);

        bmp.Freeze();
        return bmp;
    }

    // ------------------------------------------------------------------ text

    public string GetPageText(int pageIndex)
    {
        lock (_sync)
        {
            using var text = new TextPageScope(this, pageIndex);
            var count = PdfiumNative.FPDFText_CountChars(text.Handle);
            if (count <= 0) return string.Empty;

            // +1 for the NUL terminator PDFium always appends.
            var buffer = new ushort[count + 1];
            var written = PdfiumNative.FPDFText_GetText(text.Handle, 0, count, buffer);
            return Utf16ToString(buffer, written);
        }
    }

    /// <summary>
    /// Interprets a UTF-16 code-unit buffer returned by PDFium. <paramref name="written"/>
    /// counts the trailing NUL, which is dropped.
    /// </summary>
    private static string Utf16ToString(ushort[] buffer, int written)
    {
        if (written <= 1) return string.Empty;
        var length = Math.Min(written - 1, buffer.Length);
        var chars = MemoryMarshal.Cast<ushort, char>(buffer.AsSpan(0, length));
        return new string(chars);
    }

    /// <summary>Bounding boxes for a run of characters, in display space (top-left origin).</summary>
    public IReadOnlyList<TextRect> GetTextRects(int pageIndex, int startIndex, int count)
    {
        lock (_sync)
        {
            using var text = new TextPageScope(this, pageIndex);
            return GetTextRectsCore(text, startIndex, count);
        }
    }

    /// <summary>
    /// Character index at a point given in display space, or -1 if there is no text nearby.
    /// Used to snap highlights to real text rather than to a freehand box.
    /// </summary>
    public int GetCharIndexAt(int pageIndex, double displayX, double displayY, double tolerance = 4)
    {
        lock (_sync)
        {
            using var text = new TextPageScope(this, pageIndex);

            // PDFium hit-tests in PDF user space, so undo the display transform first.
            var pdf = DisplayToPdfSpace(new TextRect(displayX, displayY, displayX, displayY),
                text.DisplayWidth, text.DisplayHeight, text.Rotation);

            return PdfiumNative.FPDFText_GetCharIndexAtPos(text.Handle, pdf.Left, pdf.Top,
                tolerance, tolerance);
        }
    }

    /// <summary>
    /// Bounding boxes for an inclusive character range, in display space. A range spanning
    /// several lines yields one rect per line, which is what a text-markup annotation needs.
    /// </summary>
    public IReadOnlyList<TextRect> GetTextRectsForRange(int pageIndex, int startChar, int endChar)
    {
        if (endChar < startChar) (startChar, endChar) = (endChar, startChar);

        var count = endChar - startChar + 1;
        if (startChar < 0 || count <= 0) return [];

        lock (_sync)
        {
            using var text = new TextPageScope(this, pageIndex);
            return GetTextRectsCore(text, startChar, count);
        }
    }

    /// <summary>Finds every occurrence of <paramref name="term"/> on one page.</summary>
    public IReadOnlyList<SearchHit> FindOnPage(int pageIndex, string term,
        bool matchCase = false, bool wholeWord = false)
    {
        if (string.IsNullOrEmpty(term)) return [];

        lock (_sync)
        {
            using var text = new TextPageScope(this, pageIndex);

            var flags = PdfiumSearchFlags.Consecutive;
            if (matchCase) flags |= PdfiumSearchFlags.MatchCase;
            if (wholeWord) flags |= PdfiumSearchFlags.MatchWholeWord;

            var needle = Utf16Z(term);
            var handle = PdfiumNative.FPDFText_FindStart(text.Handle, needle, flags, 0);
            if (handle == IntPtr.Zero) return [];

            try
            {
                var hits = new List<SearchHit>();
                while (PdfiumNative.FPDFText_FindNext(handle) != 0)
                {
                    var start = PdfiumNative.FPDFText_GetSchResultIndex(handle);
                    var length = PdfiumNative.FPDFText_GetSchCount(handle);
                    if (length <= 0) break;

                    var rects = GetTextRectsCore(text, start, length);
                    hits.Add(new SearchHit(pageIndex, start, length, rects));

                    // Guard against a pathological zero-width match looping forever.
                    if (hits.Count > 5000) break;
                }
                return hits;
            }
            finally
            {
                PdfiumNative.FPDFText_FindClose(handle);
            }
        }
    }

    /// <summary>
    /// Collects the rectangles covering a text range and converts them from PDF user space
    /// into display space so the UI and the annotation model share one convention.
    /// </summary>
    private static IReadOnlyList<TextRect> GetTextRectsCore(in TextPageScope text, int start, int count)
    {
        var rectCount = PdfiumNative.FPDFText_CountRects(text.Handle, start, count);
        var list = new List<TextRect>(Math.Max(0, rectCount));
        for (var i = 0; i < rectCount; i++)
        {
            if (PdfiumNative.FPDFText_GetRect(text.Handle, i, out var l, out var t, out var r, out var b) == 0)
                continue;

            // PDFium hands back PDF user space: bottom-left origin, and `top` is the
            // numerically larger Y. Flip it into display space before anything else sees it.
            list.Add(PdfToDisplaySpace(new TextRect(l, t, r, b),
                text.DisplayWidth, text.DisplayHeight, text.Rotation));
        }
        return list;
    }

    /// <summary>Extracts a snippet of text around a hit, for the search results list.</summary>
    public string GetTextRange(int pageIndex, int startIndex, int length, int padding = 40)
    {
        lock (_sync)
        {
            using var text = new TextPageScope(this, pageIndex);
            var total = PdfiumNative.FPDFText_CountChars(text.Handle);
            if (total <= 0) return string.Empty;

            var from = Math.Max(0, startIndex - padding);
            var to = Math.Min(total, startIndex + length + padding);
            var count = to - from;
            if (count <= 0) return string.Empty;

            var buffer = new ushort[count + 1];
            var written = PdfiumNative.FPDFText_GetText(text.Handle, from, count, buffer);
            return Utf16ToString(buffer, written).Replace('\r', ' ').Replace('\n', ' ').Trim();
        }
    }

    // ------------------------------------------------------------------ page content objects

    /// <summary>
    /// Walks a page's object tree, returning every drawable object with its text and bounds.
    ///
    /// The walk recurses into Form XObjects: content is routinely wrapped in one, and a
    /// page-level scan alone would report such a page as empty.
    /// </summary>
    public IReadOnlyList<PageObjectInfo> GetPageObjects(int pageIndex, int maxDepth = 6)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var page = PdfiumNative.FPDF_LoadPage(_document, pageIndex);
            if (page == IntPtr.Zero) ThrowLastError($"读取第 {pageIndex + 1} 页内容");

            try
            {
                var displayW = (double)PdfiumNative.FPDF_GetPageWidthF(page);
                var displayH = (double)PdfiumNative.FPDF_GetPageHeightF(page);
                var rotation = PdfiumNative.FPDFPage_GetRotation(page);
                var textPage = PdfiumNative.FPDFText_LoadPage(page);

                try
                {
                    var results = new List<PageObjectInfo>();
                    WalkObjects(page, textPage, [], PdfiumNative.FS_MATRIX.Identity,
                        depth: 0, maxDepth, displayW, displayH, rotation, results);
                    return results;
                }
                finally
                {
                    if (textPage != IntPtr.Zero) PdfiumNative.FPDFText_ClosePage(textPage);
                }
            }
            finally
            {
                PdfiumNative.FPDF_ClosePage(page);
            }
        }
    }

    private void WalkObjects(IntPtr container, IntPtr textPage, List<int> path,
        PdfiumNative.FS_MATRIX parentTransform, int depth, int maxDepth,
        double displayW, double displayH, int rotation, List<PageObjectInfo> into)
    {
        var isPage = depth == 0;
        var count = isPage
            ? PdfiumNative.FPDFPage_CountObjects(container)
            : PdfiumNative.FPDFFormObj_CountObjects(container);

        for (var i = 0; i < count; i++)
        {
            var obj = isPage
                ? PdfiumNative.FPDFPage_GetObject(container, i)
                : PdfiumNative.FPDFFormObj_GetObject(container, (uint)i);

            if (obj == IntPtr.Zero) continue;

            var type = PdfiumNative.FPDFPageObj_GetType(obj);

            // Nested objects sit in their parent's coordinate space, so the ancestor matrices
            // have to be folded in before the bounds mean anything on the page.
            var transform = parentTransform;
            if (!isPage && PdfiumNative.FPDFPageObj_GetMatrix(obj, out var own) != 0)
                transform = PdfiumNative.FS_MATRIX.Concat(own, parentTransform);

            path.Add(i);
            try
            {
                var bounds = GetDisplayBounds(obj, transform, displayW, displayH, rotation);
                var text = string.Empty;
                var fontName = string.Empty;
                var fontSize = 0.0;

                if (type == PdfiumPageObjType.Text)
                {
                    text = ReadObjectText(obj, textPage);
                    var font = PdfiumNative.FPDFTextObj_GetFont(obj);
                    fontName = ReadFontName(font);
                    if (PdfiumNative.FPDFTextObj_GetFontSize(obj, out var size) != 0)
                        fontSize = size;
                }

                into.Add(new PageObjectInfo([.. path], depth, type, bounds, text, fontName, fontSize));

                if (type == PdfiumPageObjType.Form && depth + 1 < maxDepth)
                {
                    WalkObjects(obj, textPage, path, transform, depth + 1, maxDepth,
                        displayW, displayH, rotation, into);
                }
            }
            finally
            {
                path.RemoveAt(path.Count - 1);
            }
        }
    }

    private static TextRect GetDisplayBounds(IntPtr obj, PdfiumNative.FS_MATRIX transform,
        double displayW, double displayH, int rotation)
    {
        if (PdfiumNative.FPDFPageObj_GetBounds(obj, out var l, out var b, out var r, out var t) == 0)
            return default;

        // Fold in any ancestor form matrices, then convert user space → display space.
        var (x1, y1) = transform.Apply(l, b);
        var (x2, y2) = transform.Apply(r, t);

        var userSpace = new TextRect(
            Math.Min(x1, x2), Math.Min(y1, y2),
            Math.Max(x1, x2), Math.Max(y1, y2));

        return PdfToDisplaySpace(userSpace, displayW, displayH, rotation);
    }

    private static string ReadObjectText(IntPtr obj, IntPtr textPage)
    {
        if (textPage == IntPtr.Zero) return string.Empty;

        // First call with a null buffer returns the byte length including the terminator.
        var bytes = PdfiumNative.FPDFTextObj_GetText(obj, textPage, null, 0);
        if (bytes <= 2) return string.Empty;

        var buffer = new ushort[bytes / 2 + 1];

        // FPDFTextObj_GetText measures `length` in BYTES, not code units — passing the element
        // count instead makes it write only half the string.
        var written = PdfiumNative.FPDFTextObj_GetText(obj, textPage, buffer, (uint)(buffer.Length * 2));
        if (written <= 2) return string.Empty;

        return Utf16ToString(buffer, (int)(written / 2));
    }

    private static string ReadFontName(IntPtr font)
    {
        if (font == IntPtr.Zero) return string.Empty;

        var length = PdfiumNative.FPDFFont_GetBaseFontName(font, null, 0);
        if (length <= 1) return string.Empty;

        var buffer = new byte[length];
        var written = PdfiumNative.FPDFFont_GetBaseFontName(font, buffer, length);
        if (written <= 1) return string.Empty;

        // ASCII, NUL-terminated.
        return Encoding.ASCII.GetString(buffer, 0, (int)written - 1);
    }

    /// <summary>
    /// Replaces the string of an existing text object and rewrites the page content stream.
    /// The object keeps its font, size and position — this edits text in place, it does not
    /// re-lay-out the page.
    ///
    /// Most PDFs embed only a <em>subset</em> of their fonts: just the glyphs the original text
    /// used. Writing a character whose glyph is absent produces a blank, and the original text
    /// is gone — silent data loss. So the edit is written, read back, and rolled back if it did
    /// not survive the round trip.
    /// </summary>
    public TextEditOutcome SetObjectText(int pageIndex, IReadOnlyList<int> path, string newText)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var page = PdfiumNative.FPDF_LoadPage(_document, pageIndex);
            if (page == IntPtr.Zero) return TextEditOutcome.ObjectNotFound;

            var textPage = IntPtr.Zero;
            try
            {
                textPage = PdfiumNative.FPDFText_LoadPage(page);

                var obj = ResolveObject(page, path);
                if (obj == IntPtr.Zero) return TextEditOutcome.ObjectNotFound;
                if (PdfiumNative.FPDFPageObj_GetType(obj) != PdfiumPageObjType.Text)
                    return TextEditOutcome.NotATextObject;

                var original = ReadObjectText(obj, textPage);

                if (PdfiumNative.FPDFText_SetText(obj, Utf16Z(newText)) == 0)
                    return TextEditOutcome.GlyphsUnavailable;

                // Commit first: the text page reads the content stream, which still holds the
                // old string until this runs.
                if (PdfiumNative.FPDFPage_GenerateContent(page) == 0)
                {
                    RestoreText(page, path, original);
                    return TextEditOutcome.GlyphsUnavailable;
                }

                // This text page was built from the pre-edit content stream, so it still maps the
                // old glyphs. Rebuild it, or the read-back below just returns the original text.
                PdfiumNative.FPDFText_ClosePage(textPage);
                textPage = PdfiumNative.FPDFText_LoadPage(page);

                // Re-resolve: regenerating content can rebuild the page's object list.
                var written = ResolveObject(page, path);
                var actual = written == IntPtr.Zero ? string.Empty : ReadObjectText(written, textPage);

                if (EquivalentText(actual, newText)) return TextEditOutcome.Applied;

                RestoreText(page, path, original);
                return TextEditOutcome.GlyphsUnavailable;
            }
            finally
            {
                if (textPage != IntPtr.Zero) PdfiumNative.FPDFText_ClosePage(textPage);
                PdfiumNative.FPDF_ClosePage(page);
            }
        }
    }

    /// <summary>Puts the original string back after a rejected edit, so nothing is lost.</summary>
    private static void RestoreText(IntPtr page, IReadOnlyList<int> path, string original)
    {
        var obj = ResolveObject(page, path);
        if (obj == IntPtr.Zero) return;

        PdfiumNative.FPDFText_SetText(obj, Utf16Z(original));
        PdfiumNative.FPDFPage_GenerateContent(page);
    }

    /// <summary>
    /// Whether a written string survived extraction. Whitespace is normalised because PDFium
    /// may re-encode spacing, but the visible characters must match exactly.
    /// </summary>
    private static bool EquivalentText(string actual, string intended)
        => string.Equals(Collapse(actual), Collapse(intended), StringComparison.Ordinal);

    private static string Collapse(string s)
        => new(s.Where(c => !char.IsWhiteSpace(c)).ToArray());

    /// <summary>Deletes an object from the page — this is what makes redaction possible.</summary>
    public bool RemoveObject(int pageIndex, IReadOnlyList<int> path)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (path.Count == 0) return false;

            var page = PdfiumNative.FPDF_LoadPage(_document, pageIndex);
            if (page == IntPtr.Zero) return false;

            try
            {
                var obj = ResolveObject(page, path);
                if (obj == IntPtr.Zero) return false;

                var removed = path.Count == 1
                    ? PdfiumNative.FPDFPage_RemoveObject(page, obj)
                    : RemoveFromForm(page, path, obj);

                return removed != 0 && PdfiumNative.FPDFPage_GenerateContent(page) != 0;
            }
            finally
            {
                PdfiumNative.FPDF_ClosePage(page);
            }
        }
    }

    private static int RemoveFromForm(IntPtr page, IReadOnlyList<int> path, IntPtr obj)
    {
        // The immediate parent of a nested object is the form at path[..^1].
        var parent = ResolveObject(page, path.Take(path.Count - 1).ToList());
        return parent == IntPtr.Zero ? 0 : PdfiumNative.FPDFFormObj_RemoveObject(parent, obj);
    }

    private static IntPtr ResolveObject(IntPtr page, IReadOnlyList<int> path)
    {
        if (path.Count == 0) return IntPtr.Zero;

        var current = PdfiumNative.FPDFPage_GetObject(page, path[0]);
        for (var i = 1; i < path.Count && current != IntPtr.Zero; i++)
            current = PdfiumNative.FPDFFormObj_GetObject(current, (uint)path[i]);

        return current;
    }

    // ------------------------------------------------------------------ text layer hiding

    /// <summary>
    /// Deletes the listed page objects and returns the saved document. Used to strip text that a
    /// replacement supersedes; deferred to export so the edit stays undoable.
    /// </summary>
    public static byte[] RemoveObjects(byte[] source,
        IReadOnlyDictionary<int, List<IReadOnlyList<int>>> pathsByPage)
    {
        if (pathsByPage.Count == 0) return source;

        using var engine = Open(source);

        lock (engine._sync)
        {
            var changed = false;

            foreach (var (pageIndex, paths) in pathsByPage)
            {
                if (pageIndex < 0 || pageIndex >= engine.PageCount) continue;

                var page = PdfiumNative.FPDF_LoadPage(engine._document, pageIndex);
                if (page == IntPtr.Zero) continue;

                try
                {
                    var removedAny = false;

                    // Deepest paths first: removing a parent would invalidate its children.
                    foreach (var path in paths.OrderByDescending(p => p.Count))
                    {
                        var obj = ResolveObject(page, path);
                        if (obj == IntPtr.Zero) continue;

                        var ok = path.Count == 1
                            ? PdfiumNative.FPDFPage_RemoveObject(page, obj)
                            : RemoveFromForm(page, path, obj);

                        if (ok != 0) removedAny = true;
                    }

                    if (removedAny)
                    {
                        PdfiumNative.FPDFPage_GenerateContent(page);
                        changed = true;
                    }
                }
                finally
                {
                    PdfiumNative.FPDF_ClosePage(page);
                }
            }

            return changed ? engine.Save() : source;
        }
    }

    /// <summary>
    /// Turns the OCR text layer invisible.
    ///
    /// PDFsharp is the only one of the two libraries that subsets an embedded CJK font (a full
    /// face is ~9 MB), but its drawing API exposes no text render mode and silently ignores a
    /// zero-alpha brush. PDFium can set <c>3 Tr</c> but embeds the whole font. So the layer is
    /// drawn visibly by PDFsharp and hidden here.
    ///
    /// The layer is appended after each page's own content, so the trailing text objects are the
    /// ones to hide. Each is also checked against the layer's font before being touched, so a
    /// page whose own text trails the content cannot be blanked by accident.
    /// </summary>
    public static byte[] HideTrailingText(byte[] source,
        IReadOnlyDictionary<int, int> countsPerPage, string expectFontName)
    {
        using var engine = Open(source);
        engine.HideTrailing(countsPerPage, expectFontName);
        return engine.Save();
    }

    private void HideTrailing(IReadOnlyDictionary<int, int> countsPerPage, string expectFontName)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            foreach (var (pageIndex, count) in countsPerPage)
            {
                if (count <= 0 || pageIndex < 0 || pageIndex >= PageCount) continue;

                var page = PdfiumNative.FPDF_LoadPage(_document, pageIndex);
                if (page == IntPtr.Zero) continue;

                var textPage = PdfiumNative.FPDFText_LoadPage(page);
                try
                {
                    var total = PdfiumNative.FPDFPage_CountObjects(page);
                    var hidden = 0;

                    for (var i = total - 1; i >= 0 && hidden < count; i--)
                    {
                        var obj = PdfiumNative.FPDFPage_GetObject(page, i);
                        if (obj == IntPtr.Zero) continue;
                        if (PdfiumNative.FPDFPageObj_GetType(obj) != PdfiumPageObjType.Text) continue;

                        // Guard against hiding the document's own text: only objects using the
                        // layer's font are candidates.
                        var font = ReadFontName(PdfiumNative.FPDFTextObj_GetFont(obj));
                        if (!font.Contains(expectFontName, StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (PdfiumNative.FPDFTextObj_SetTextRenderMode(obj, PdfiumTextRenderMode.Invisible) != 0)
                            hidden++;
                    }

                    if (hidden > 0) PdfiumNative.FPDFPage_GenerateContent(page);
                }
                finally
                {
                    if (textPage != IntPtr.Zero) PdfiumNative.FPDFText_ClosePage(textPage);
                    PdfiumNative.FPDF_ClosePage(page);
                }
            }
        }
    }

    // ------------------------------------------------------------------ outline

    public IReadOnlyList<OutlineEntry> GetOutline()
    {
        lock (_sync)
        {
            var list = new List<OutlineEntry>();
            var root = PdfiumNative.FPDFBookmark_GetFirstChild(_document, IntPtr.Zero);
            WalkOutline(root, 0, list);
            return list;
        }
    }

    private void WalkOutline(IntPtr bookmark, int depth, List<OutlineEntry> into)
    {
        // Depth-first, siblings in document order. Depth cap defends against a malformed
        // outline that points at itself.
        while (bookmark != IntPtr.Zero && depth < 32)
        {
            var title = ReadBookmarkTitle(bookmark);
            var dest = PdfiumNative.FPDFBookmark_GetDest(_document, bookmark);
            var pageIndex = dest == IntPtr.Zero
                ? -1
                : PdfiumNative.FPDFDest_GetDestPageIndex(_document, dest);

            if (!string.IsNullOrWhiteSpace(title))
                into.Add(new OutlineEntry(title, pageIndex, depth));

            var child = PdfiumNative.FPDFBookmark_GetFirstChild(_document, bookmark);
            if (child != IntPtr.Zero) WalkOutline(child, depth + 1, into);

            bookmark = PdfiumNative.FPDFBookmark_GetNextSibling(_document, bookmark);
        }
    }

    private static string ReadBookmarkTitle(IntPtr bookmark)
    {
        var bytes = PdfiumNative.FPDFBookmark_GetTitle(bookmark, null, 0);
        if (bytes <= 2) return string.Empty;

        var buffer = new byte[bytes];
        var written = PdfiumNative.FPDFBookmark_GetTitle(bookmark, buffer, bytes);
        if (written <= 2) return string.Empty;

        // Byte length includes the 2-byte NUL terminator.
        return Encoding.Unicode.GetString(buffer, 0, (int)written - 2);
    }

    // ------------------------------------------------------------------ save

    /// <summary>
    /// Materialises <paramref name="annotations"/> into real PDF annotation objects and returns
    /// the saved bytes. This runs against a throwaway engine so the live document stays clean —
    /// saving twice must not duplicate annotations.
    /// </summary>
    public static byte[] SaveWithAnnotations(byte[] source, IReadOnlyList<AnnotationSpec> annotations,
        string? sourcePath = null)
    {
        using var engine = Open(source, sourcePath);
        engine.ApplyAnnotations(annotations);
        return engine.Save();
    }

    private void ApplyAnnotations(IReadOnlyList<AnnotationSpec> annotations)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            foreach (var group in annotations.GroupBy(a => a.PageIndex))
            {
                if (group.Key < 0 || group.Key >= PageCount) continue;

                var page = PdfiumNative.FPDF_LoadPage(_document, group.Key);
                if (page == IntPtr.Zero) continue;

                try
                {
                    var pageRotation = PdfiumNative.FPDFPage_GetRotation(page);
                    var pageW = PdfiumNative.FPDF_GetPageWidthF(page);
                    var pageH = PdfiumNative.FPDF_GetPageHeightF(page);

                    foreach (var spec in group)
                    {
                        // /Rotate is purely a viewing hint — it does not change PDF user space.
                        // Annotations are stored in the display space the user drew them in, so
                        // undo any rotation added since, which yields the same user-space rect
                        // regardless of what /Rotate now says.
                        var rotation = ((pageRotation - spec.PageExtraRotation) % 4 + 4) % 4;

                        var displayW = pageW;
                        var displayH = pageH;
                        if (rotation % 2 != pageRotation % 2)
                            (displayW, displayH) = (displayH, displayW);

                        AttachAnnotation(page, spec, displayW, displayH, rotation);
                    }
                }
                finally
                {
                    PdfiumNative.FPDF_ClosePage(page);
                }
            }
        }
    }

    private static void AttachAnnotation(IntPtr page, AnnotationSpec spec,
        double displayW, double displayH, int rotation)
    {
        var subtype = spec.Kind switch
        {
            AnnotationKind.Highlight => PdfiumAnnotSubtype.Highlight,
            AnnotationKind.Underline => PdfiumAnnotSubtype.Underline,
            AnnotationKind.StrikeOut => PdfiumAnnotSubtype.StrikeOut,
            AnnotationKind.Squiggly => PdfiumAnnotSubtype.Squiggly,
            AnnotationKind.Note => PdfiumAnnotSubtype.Text,
            AnnotationKind.Rectangle => PdfiumAnnotSubtype.Square,
            AnnotationKind.Ellipse => PdfiumAnnotSubtype.Circle,
            AnnotationKind.Ink => PdfiumAnnotSubtype.Ink,
            AnnotationKind.FreeText => PdfiumAnnotSubtype.FreeText,
            _ => PdfiumAnnotSubtype.Highlight,
        };

        var annot = PdfiumNative.FPDFPage_CreateAnnot(page, subtype);
        if (annot == IntPtr.Zero) return;

        try
        {
            // The model stores display-space rects; PDFium wants PDF user space.
            var pdfRect = DisplayToPdfSpace(spec.Bounds, displayW, displayH, rotation);
            var rect = new PdfiumNative.FS_RECTF
            {
                left = (float)pdfRect.Left,
                top = (float)pdfRect.Top,
                right = (float)pdfRect.Right,
                bottom = (float)pdfRect.Bottom,
            };
            PdfiumNative.FPDFAnnot_SetRect(annot, ref rect);

            PdfiumNative.FPDFAnnot_SetColor(annot, PdfiumAnnotColorType.Color,
                spec.R, spec.G, spec.B, spec.A);

            if (spec.Kind is AnnotationKind.Rectangle or AnnotationKind.Ellipse)
            {
                PdfiumNative.FPDFAnnot_SetColor(annot, PdfiumAnnotColorType.InteriorColor,
                    spec.R, spec.G, spec.B, Math.Min(spec.A, (byte)60));
            }

            if (!string.IsNullOrEmpty(spec.Contents))
                PdfiumNative.FPDFAnnot_SetStringValue(annot, Utf16Z("Contents"), Utf16Z(spec.Contents));

            if (!string.IsNullOrEmpty(spec.Author))
                PdfiumNative.FPDFAnnot_SetStringValue(annot, Utf16Z("T"), Utf16Z(spec.Author));

            // Text-markup annotations are positioned by /QuadPoints; /Rect alone leaves them
            // invisible even though the object is present and valid.
            if (spec.Kind is AnnotationKind.Highlight or AnnotationKind.Underline
                or AnnotationKind.StrikeOut or AnnotationKind.Squiggly)
            {
                AttachQuadPoints(annot, spec, displayW, displayH, rotation);
            }

            if (spec.Kind == AnnotationKind.Ink && spec.InkStrokes.Count > 0)
                AttachInkPaths(page, annot, spec, displayW, displayH, rotation);
        }
        finally
        {
            PdfiumNative.FPDFPage_CloseAnnot(annot);
        }
    }

    /// <summary>
    /// Writes one quadrilateral per text line into the annotation's /QuadPoints.
    ///
    /// Corner order follows PDF 32000-1 Table 179: the four vertices are emitted as
    /// upper-left, upper-right, lower-left, lower-right. Since PDF user space is Y-up here,
    /// "upper" means the larger Y, which is the rect's Top after conversion.
    /// </summary>
    private static void AttachQuadPoints(IntPtr annot, AnnotationSpec spec,
        double displayW, double displayH, int rotation)
    {
        var source = spec.Quads.Count > 0 ? spec.Quads : [spec.Bounds];

        foreach (var quad in source)
        {
            var r = DisplayToPdfSpace(quad, displayW, displayH, rotation);

            var points = new PdfiumNative.FS_QUADPOINTSF
            {
                x1 = (float)r.Left,  y1 = (float)r.Bottom, // upper-left (Y-up: bottom edge is larger Y)
                x2 = (float)r.Right, y2 = (float)r.Bottom, // upper-right
                x3 = (float)r.Left,  y3 = (float)r.Top,    // lower-left
                x4 = (float)r.Right, y4 = (float)r.Top,    // lower-right
            };

            PdfiumNative.FPDFAnnot_AppendAttachmentPoints(annot, ref points);
        }
    }

    /// <summary>
    /// Ink annotations keep their geometry in /InkList (an array of point arrays) rather than in
    /// /Rect, so each stroke is appended separately. Points are converted to PDF user space.
    /// </summary>
    private static void AttachInkPaths(IntPtr page, IntPtr annot, AnnotationSpec spec,
        double displayW, double displayH, int rotation)
    {
        foreach (var stroke in spec.InkStrokes)
        {
            if (stroke.Points.Count < 2) continue;

            var pts = new PdfiumNative.FS_POINTF[stroke.Points.Count];
            for (var i = 0; i < stroke.Points.Count; i++)
            {
                var (x, y) = stroke.Points[i];
                var p = DisplayToPdfSpace(new TextRect(x, y, x, y), displayW, displayH, rotation);
                pts[i] = new PdfiumNative.FS_POINTF { x = (float)p.Left, y = (float)p.Top };
            }

            PdfiumNative.FPDFAnnot_AddInkStroke(annot, pts, (nuint)pts.Length);
        }

        // Ink needs an explicit stroke width; PDFium defaults it to hairline.
        PdfiumNative.FPDFAnnot_SetBorder(annot, 0f, 0f, 1.5f);
    }

    /// <summary>Serialises the (possibly modified) document to a byte array.</summary>
    public byte[] Save()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            using var sink = new MemoryStream();

            // PDFium hands the data back through a C callback, so the delegate must stay
            // rooted for the whole call and the struct must live in unmanaged memory.
            PdfiumNative.WriteBlockDelegate writer = (_, data, size) =>
            {
                if (size == 0) return 1;
                var chunk = new byte[size];
                Marshal.Copy(data, chunk, 0, (int)size);
                sink.Write(chunk, 0, (int)size);
                return 1;
            };

            var fileWrite = new PdfiumNative.FPDF_FILEWRITE
            {
                version = 1,
                WriteBlock = Marshal.GetFunctionPointerForDelegate(writer),
            };

            var size = Marshal.SizeOf<PdfiumNative.FPDF_FILEWRITE>();
            var native = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(fileWrite, native, false);
                if (PdfiumNative.FPDF_SaveAsCopy(_document, native, PdfiumSaveFlags.NoIncremental) == 0)
                    ThrowLastError("保存文档");
            }
            finally
            {
                Marshal.FreeHGlobal(native);
                GC.KeepAlive(writer);
            }

            return sink.ToArray();
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>UTF-16LE, NUL-terminated — PDFium's FPDF_WIDESTRING / FPDF_WIDESTRING key format.</summary>
    private static byte[] Utf16Z(string s)
    {
        var bytes = Encoding.Unicode.GetBytes(s);
        var buffer = new byte[bytes.Length + 2];
        Buffer.BlockCopy(bytes, 0, buffer, 0, bytes.Length);
        return buffer;
    }

    /// <summary>
    /// Keeps a FPDF_TEXTPAGE alive for the duration of a block, and carries the page geometry
    /// needed to translate PDFium's PDF-user-space rectangles into display space.
    /// </summary>
    private readonly struct TextPageScope : IDisposable
    {
        public IntPtr Handle { get; }

        /// <summary>Displayed page width in points, with the page's own /Rotate applied.</summary>
        public double DisplayWidth { get; }

        /// <summary>Displayed page height in points, with the page's own /Rotate applied.</summary>
        public double DisplayHeight { get; }

        /// <summary>Intrinsic /Rotate as quarter-turns clockwise: 0, 1, 2 or 3.</summary>
        public int Rotation { get; }

        private readonly IntPtr _page;

        public TextPageScope(PdfiumEngine engine, int pageIndex)
        {
            var page = PdfiumNative.FPDF_LoadPage(engine._document, pageIndex);
            if (page == IntPtr.Zero) ThrowLastError($"读取第 {pageIndex + 1} 页文本");

            _page = page;
            DisplayWidth = PdfiumNative.FPDF_GetPageWidthF(page);
            DisplayHeight = PdfiumNative.FPDF_GetPageHeightF(page);
            Rotation = PdfiumNative.FPDFPage_GetRotation(page);
            Handle = PdfiumNative.FPDFText_LoadPage(page);
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero) PdfiumNative.FPDFText_ClosePage(Handle);
            if (_page != IntPtr.Zero) PdfiumNative.FPDF_ClosePage(_page);
        }
    }
}
