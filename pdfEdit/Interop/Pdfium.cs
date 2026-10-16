using System.Runtime.InteropServices;

namespace PdfEdit.Interop;

/// <summary>
/// Raw P/Invoke bindings against pdfium.dll.
///
/// Signatures are transcribed from the official PDFium C headers that ship with the
/// bblanchon.PDFium.Win32 package (build/native/include/pdfium/*.h). Notable details:
///   * PDFium mixes naming conventions — <c>FPDF_Page_*</c> keeps the underscore but
///     <c>FPDFText_*</c>, <c>FPDFAnnot_*</c> and <c>FPDFBitmap_*</c> do not.
///   * <c>FPDF_CALLCONV</c> is <c>__stdcall</c> on Windows.
///   * <c>unsigned long</c> is 32-bit on Windows (LLP64), so it maps to <see cref="uint"/>.
/// </summary>
internal static class PdfiumNative
{
    private const string Lib = "pdfium";

    // ---------------------------------------------------------------- library

    [StructLayout(LayoutKind.Sequential)]
    internal struct FPDF_LIBRARY_CONFIG
    {
        public int version;
        public IntPtr m_pUserFontPaths;
        public IntPtr m_pIsolate;
        public uint m_v8EmbedderSlot;
        public IntPtr m_pPlatform;
        public int m_RendererType;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern void FPDF_InitLibraryWithConfig(ref FPDF_LIBRARY_CONFIG config);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern void FPDF_DestroyLibrary();

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern uint FPDF_GetLastError();

    // ---------------------------------------------------------------- document

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern IntPtr FPDF_LoadMemDocument64(IntPtr dataBuf, nuint size, byte[]? password);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern void FPDF_CloseDocument(IntPtr document);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern int FPDF_GetPageCount(IntPtr document);

    /// <summary>Writes the (possibly modified) document through the supplied writer.</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern int FPDF_SaveAsCopy(IntPtr document, IntPtr fileWrite, uint flags);

    // ---------------------------------------------------------------- page

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern IntPtr FPDF_LoadPage(IntPtr document, int pageIndex);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern void FPDF_ClosePage(IntPtr page);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern float FPDF_GetPageWidthF(IntPtr page);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern float FPDF_GetPageHeightF(IntPtr page);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern int FPDFPage_GetRotation(IntPtr page);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern void FPDFPage_SetRotation(IntPtr page, int rotate);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern void FPDFPage_Delete(IntPtr document, int pageIndex);

    // ---------------------------------------------------------------- bitmap

    [StructLayout(LayoutKind.Sequential)]
    internal struct FS_RECTF
    {
        public float left, top, right, bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FS_SIZEF
    {
        public float width, height;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FS_POINTF
    {
        public float x, y;
    }

    /// <summary>Four corners of a quadrilateral, as used by text-markup annotations.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct FS_QUADPOINTSF
    {
        public float x1, y1, x2, y2, x3, y3, x4, y4;
    }

    /// <summary>
    /// Affine transform in PDF order: x' = a·x + c·y + e, y' = b·x + d·y + f.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct FS_MATRIX
    {
        public float a, b, c, d, e, f;

        public static FS_MATRIX Identity => new() { a = 1, b = 0, c = 0, d = 1, e = 0, f = 0 };

        /// <summary>Maps a point through this transform.</summary>
        public readonly (double X, double Y) Apply(double x, double y)
            => (a * x + c * y + e, b * x + d * y + f);

        /// <summary>
        /// Composes two transforms so that <paramref name="inner"/> is applied first and
        /// <paramref name="outer"/> second — the operation needed to lift a nested object's
        /// coordinates into its parent's space.
        /// </summary>
        public static FS_MATRIX Concat(FS_MATRIX inner, FS_MATRIX outer) => new()
        {
            a = outer.a * inner.a + outer.c * inner.b,
            b = outer.b * inner.a + outer.d * inner.b,
            c = outer.a * inner.c + outer.c * inner.d,
            d = outer.b * inner.c + outer.d * inner.d,
            e = outer.a * inner.e + outer.c * inner.f + outer.e,
            f = outer.b * inner.e + outer.d * inner.f + outer.f,
        };
    }

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern IntPtr FPDFBitmap_CreateEx(int width, int height, int format, IntPtr firstScan, int stride);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern void FPDFBitmap_Destroy(IntPtr bitmap);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern int FPDFBitmap_GetStride(IntPtr bitmap);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern void FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int startX, int startY,
        int sizeX, int sizeY, int rotate, int flags);

    // ---------------------------------------------------------------- text

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern IntPtr FPDFText_LoadPage(IntPtr page);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern void FPDFText_ClosePage(IntPtr textPage);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern int FPDFText_CountChars(IntPtr textPage);

    /// <summary>Fills <paramref name="result"/> with UTF-16LE code units. Returns the count written, including the terminator.</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern int FPDFText_GetText(IntPtr textPage, int startIndex, int count, [Out] ushort[] result);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern int FPDFText_CountRects(IntPtr textPage, int startIndex, int count);

    /// <summary>Character index at or near a point, in PDF user space. Returns -1 when there is none.</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern int FPDFText_GetCharIndexAtPos(IntPtr textPage, double x, double y,
        double xTolerance, double yTolerance);

    /// <summary>Rectangle coordinates are in page space, origin top-left, Y growing downward.</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern int FPDFText_GetRect(IntPtr textPage, int rectIndex,
        out double left, out double top, out double right, out double bottom);

    /// <summary><paramref name="findWhat"/> must be a NUL-terminated UTF-16LE buffer.</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern IntPtr FPDFText_FindStart(IntPtr textPage, byte[] findWhat, uint flags, int startIndex);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern int FPDFText_FindNext(IntPtr handle);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern int FPDFText_GetSchResultIndex(IntPtr handle);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern int FPDFText_GetSchCount(IntPtr handle);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern void FPDFText_FindClose(IntPtr handle);

    // ---------------------------------------------------------------- page objects
    //
    // Object handles returned by FPDFPage_GetObject / FPDFFormObj_GetObject are owned by the
    // page or form and must NOT be freed. They are only valid while that page handle is open,
    // so any mutation has to happen inside a page scope.

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern int FPDFPage_CountObjects(IntPtr page);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern IntPtr FPDFPage_GetObject(IntPtr page, int index);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern int FPDFPageObj_GetType(IntPtr pageObject);

    /// <summary>Bounds in PDF user space: bottom is the smaller Y, top the larger.</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern int FPDFPageObj_GetBounds(IntPtr pageObject,
        out float left, out float bottom, out float right, out float top);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern void FPDFPageObj_Transform(IntPtr pageObject,
        double a, double b, double c, double d, double e, double f);

    /// <summary>Matrix mapping this object's own space into its parent's space.</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern int FPDFPageObj_GetMatrix(IntPtr pageObject, out PdfiumNative.FS_MATRIX matrix);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern int FPDFPageObj_SetFillColor(IntPtr pageObject,
        uint r, uint g, uint b, uint a);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern int FPDFPageObj_SetStrokeColor(IntPtr pageObject,
        uint r, uint g, uint b, uint a);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern void FPDFPage_InsertObject(IntPtr page, IntPtr pageObject);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern int FPDFPage_RemoveObject(IntPtr page, IntPtr pageObject);

    /// <summary>Rewrites the page's content stream from its object list. Required after edits.</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern int FPDFPage_GenerateContent(IntPtr page);

    // Form XObjects nest their own object lists; page-level enumeration does not see into them.

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern int FPDFFormObj_CountObjects(IntPtr formObject);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern IntPtr FPDFFormObj_GetObject(IntPtr formObject, uint index);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern int FPDFFormObj_RemoveObject(IntPtr formObject, IntPtr pageObject);

    // Text objects

    /// <summary>Extracts a text object's string. Returns the byte length, including the terminator.</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern uint FPDFTextObj_GetText(IntPtr textObject, IntPtr textPage,
        [Out] ushort[]? buffer, uint length);

    /// <summary>Replaces a text object's string. <paramref name="text"/> is NUL-terminated UTF-16LE.</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern int FPDFText_SetText(IntPtr textObject, byte[] text);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern IntPtr FPDFTextObj_GetFont(IntPtr textObject);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern int FPDFTextObj_GetFontSize(IntPtr textObject, out float size);

    /// <summary>Writes the font's PostScript name as ASCII. Returns the byte length, including the terminator.</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern nuint FPDFFont_GetBaseFontName(IntPtr font, byte[]? buffer, nuint length);

    /// <summary>
    /// Loads a font from an in-memory TTF into the document. PDFium copies the data and
    /// auto-generates the encoding tables (including ToUnicode) needed to embed it.
    /// Pass <paramref name="cid"/> = true for fonts with CJK glyphs.
    /// </summary>
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern IntPtr FPDFText_LoadFont(IntPtr document, byte[] data, uint size,
        int fontType, int cid);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern void FPDFFont_Close(IntPtr font);

    /// <summary>Creates a detached text object. It must be inserted into a page to appear.</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern IntPtr FPDFPageObj_CreateTextObj(IntPtr document, IntPtr font, float fontSize);

    /// <summary>
    /// Sets how text is painted. <see cref="PdfiumTextRenderMode.Invisible"/> puts the glyphs in
    /// the content stream so they can be selected and searched, but paints nothing.
    /// </summary>
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern int FPDFTextObj_SetTextRenderMode(IntPtr textObject, int renderMode);

    // ---------------------------------------------------------------- annotations

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern IntPtr FPDFPage_CreateAnnot(IntPtr page, int subtype);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern int FPDFPage_GetAnnotCount(IntPtr page);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern IntPtr FPDFPage_GetAnnot(IntPtr page, int index);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern void FPDFPage_CloseAnnot(IntPtr annot);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern int FPDFAnnot_SetRect(IntPtr annot, ref FS_RECTF rect);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern int FPDFAnnot_SetColor(IntPtr annot, int colorType,
        uint r, uint g, uint b, uint a);

    /// <summary>
    /// <paramref name="key"/> must be a NUL-terminated byte string (FPDF_BYTESTRING — ASCII),
    /// <paramref name="value"/> a NUL-terminated UTF-16LE buffer (FPDF_WIDESTRING). Encoding the
    /// key as UTF-16LE silently truncates it at the first NUL, so "Contents" would arrive as "C".
    /// </summary>
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern int FPDFAnnot_SetStringValue(IntPtr annot, byte[] key, byte[] value);

    /// <summary>Reads a string entry such as /Contents. Returns the required byte length of the
    /// UTF-16LE value including the terminator; call with a null buffer first to size it.</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern uint FPDFAnnot_GetStringValue(IntPtr annot, byte[] key, byte[]? buffer, uint buflen);

    /// <summary>Appends one stroke to an ink annotation. Points are in PDF user space.</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern int FPDFAnnot_AddInkStroke(IntPtr annot, [In] PdfiumNative.FS_POINTF[] points, nuint pointCount);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern int FPDFAnnot_SetBorder(IntPtr annot, float horizontalRadius, float verticalRadius, float borderWidth);

    /// <summary>
    /// Appends one quadrilateral to an annotation's /QuadPoints. Text-markup annotations
    /// (highlight, underline, strikeout, squiggly) are drawn from these, not from /Rect —
    /// without them the annotation exists but renders as nothing.
    /// </summary>
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern int FPDFAnnot_AppendAttachmentPoints(IntPtr annot, ref PdfiumNative.FS_QUADPOINTSF quadPoints);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern nuint FPDFAnnot_CountAttachmentPoints(IntPtr annot);

    // ---------------------------------------------------------------- bookmarks

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern IntPtr FPDFBookmark_GetFirstChild(IntPtr document, IntPtr bookmark);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern IntPtr FPDFBookmark_GetNextSibling(IntPtr document, IntPtr bookmark);

    /// <summary>Returns the byte length of the UTF-16LE title, including the terminator.</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern uint FPDFBookmark_GetTitle(IntPtr bookmark, byte[]? buffer, uint bufLen);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern IntPtr FPDFBookmark_GetDest(IntPtr document, IntPtr bookmark);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    internal static extern int FPDFDest_GetDestPageIndex(IntPtr document, IntPtr dest);

    // ---------------------------------------------------------------- save callback

    [StructLayout(LayoutKind.Sequential)]
    internal struct FPDF_FILEWRITE
    {
        public int version;
        public IntPtr WriteBlock;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int WriteBlockDelegate(IntPtr pThis, IntPtr pData, uint size);
}

/// <summary>Bitmap formats accepted by <c>FPDFBitmap_CreateEx</c>.</summary>
internal static class PdfiumBitmapFormat
{
    public const int Gray = 1;
    public const int Bgr = 2;
    public const int Bgrx = 3;
    public const int Bgra = 4;
}

/// <summary>Flags for <c>FPDF_RenderPageBitmap</c>.</summary>
internal static class PdfiumRenderFlags
{
    public const int Annot = 0x01;
    public const int LcdText = 0x02;
    public const int Grayscale = 0x08;
    /// <summary>Emit BGRA rather than RGBA byte order.</summary>
    public const int ReverseByteOrder = 0x10;
}

/// <summary>Text painting modes, from <c>FPDF_TEXT_RENDERMODE</c> in fpdfview.h.</summary>
internal static class PdfiumTextRenderMode
{
    public const int Unknown = -1;
    public const int Fill = 0;
    public const int Stroke = 1;
    public const int FillStroke = 2;

    /// <summary>Glyphs are present for selection and search but paint nothing.</summary>
    public const int Invisible = 3;
}

/// <summary>Font flavours accepted by <c>FPDFText_LoadFont</c>.</summary>
internal static class PdfiumFontType
{
    public const int Type1 = 1;
    public const int TrueType = 2;
}

/// <summary>Page object kinds, from <c>FPDF_PAGEOBJ_*</c> in fpdf_edit.h.</summary>
internal static class PdfiumPageObjType
{
    public const int Unknown = 0;
    public const int Text = 1;
    public const int Path = 2;
    public const int Image = 3;
    public const int Shading = 4;
    public const int Form = 5;
}

/// <summary>Annotation subtype constants (from fpdf_annot.h; these are #defines, not an enum).</summary>
internal static class PdfiumAnnotSubtype
{
    public const int Text = 1;
    public const int FreeText = 3;
    public const int Square = 5;
    public const int Circle = 6;
    public const int Highlight = 9;
    public const int Underline = 10;
    public const int Squiggly = 11;
    public const int StrikeOut = 12;
    public const int Ink = 15;
}

/// <summary>Color slots on an annotation.</summary>
internal static class PdfiumAnnotColorType
{
    public const int Color = 0;
    public const int InteriorColor = 1;
}

internal static class PdfiumSearchFlags
{
    public const uint MatchCase = 0x00000001;
    public const uint MatchWholeWord = 0x00000002;
    public const uint Consecutive = 0x00000004;
}

/// <summary>Flags for <c>FPDF_SaveAsCopy</c>.</summary>
internal static class PdfiumSaveFlags
{
    public const uint Incremental = 1;
    public const uint NoIncremental = 2;
    public const uint RemoveSecurity = 3;
}

/// <summary>Values returned by <c>FPDF_GetLastError</c>.</summary>
internal enum PdfiumError : uint
{
    Success = 0,
    Unknown = 1,
    FileError = 2,
    FormatError = 3,
    PasswordError = 4,
    SecurityError = 5,
    PageError = 6,
}
