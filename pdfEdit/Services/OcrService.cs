using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfEdit.Models;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;

// Windows.Media.Ocr declares its own OcrWord; ours is the one this file means.
using OcrWord = PdfEdit.Models.OcrWord;

namespace PdfEdit.Services;

/// <summary>
/// Text recognition for scanned pages, backed by the OCR engine built into Windows
/// (Windows.Media.Ocr). No third-party OCR dependency, but the available languages are whatever
/// the machine has installed — the Chinese recognizer ships with the OS, others need a language
/// pack.
/// </summary>
public static class OcrService
{
    /// <summary>
    /// Renders at roughly this multiple of 72 DPI before recognising. OCR accuracy falls off
    /// sharply below ~150 DPI; 300 DPI is the usual sweet spot for print-quality scans.
    /// </summary>
    public const double OcrScale = 300.0 / 72.0;

    private static OcrEngine? _engine;
    private static readonly Lock EngineLock = new();

    /// <summary>Reports which language the recognizer is using, for display in the UI.</summary>
    public static string? ActiveLanguage { get; private set; }

    public static bool IsAvailable
    {
        get
        {
            lock (EngineLock) return ResolveEngine() is not null;
        }
    }

    private static OcrEngine? ResolveEngine()
    {
        if (_engine is not null) return _engine;

        try
        {
            // Prefer the user's own languages, then fall back to any installed recognizer —
            // a machine set up for English may still have only the Chinese recognizer.
            _engine = OcrEngine.TryCreateFromUserProfileLanguages();

            if (_engine is null && OcrEngine.AvailableRecognizerLanguages.Count > 0)
                _engine = OcrEngine.TryCreateFromLanguage(OcrEngine.AvailableRecognizerLanguages[0]);

            ActiveLanguage = _engine?.RecognizerLanguage?.DisplayName;
        }
        catch
        {
            _engine = null;
        }

        return _engine;
    }

    /// <summary>
    /// Recognises the text in a rendered page and returns the words with their positions,
    /// converted back into display space (points, top-left origin).
    /// </summary>
    /// <param name="pageBitmap">A page rendered at <paramref name="scale"/>.</param>
    /// <param name="scale">Pixels per point the bitmap was rendered at.</param>
    public static async Task<IReadOnlyList<OcrWord>> RecognizeAsync(BitmapSource pageBitmap, double scale)
    {
        var engine = ResolveEngine()
            ?? throw new InvalidOperationException(
                "系统未安装任何 OCR 语言包。请在「设置 → 时间和语言 → 语言」中添加语言并勾选光学字符识别。");

        using var software = ToSoftwareBitmap(pageBitmap);

        var result = await engine.RecognizeAsync(software);

        var words = new List<OcrWord>();
        var lineIndex = 0;

        foreach (var line in result.Lines)
        {
            foreach (var word in line.Words)
            {
                if (string.IsNullOrWhiteSpace(word.Text)) continue;

                var r = word.BoundingRect;
                if (r.Width <= 0 || r.Height <= 0) continue;

                // Pixel space → display space.
                words.Add(new OcrWord(word.Text, new TextRect(
                    r.X / scale, r.Y / scale,
                    (r.X + r.Width) / scale, (r.Y + r.Height) / scale),
                    lineIndex));
            }

            lineIndex++;
        }

        return words;
    }

    /// <summary>
    /// Builds a SoftwareBitmap for the recognizer. A fresh BitmapSource is created every time
    /// because the WinRT bitmap takes ownership of the buffer it is handed.
    /// </summary>
    private static SoftwareBitmap ToSoftwareBitmap(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var stride = width * 4;

        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);

        var buffer = CryptographicBuffer.CreateFromByteArray(pixels);

        return SoftwareBitmap.CreateCopyFromBuffer(
            buffer, BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
    }

    /// <summary>
    /// Renders dimensions that keep the image within the recognizer's limits.
    /// Returns null when the page cannot fit at any workable resolution.
    /// </summary>
    public static double? SafeScaleFor(double widthPoints, double heightPoints)
    {
        var scale = OcrScale;
        var max = (double)OcrEngine.MaxImageDimension;

        var longest = Math.Max(widthPoints, heightPoints) * scale;
        if (longest <= max) return scale;

        // Shrink to fit, but give up if the result would be too coarse to read.
        var reduced = scale * (max / longest);
        return reduced >= 1.5 ? reduced : null;
    }
}
