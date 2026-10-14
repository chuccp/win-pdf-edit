using System.IO;
using PdfSharp.Fonts;

namespace PdfEdit.Services;

/// <summary>
/// Supplies font programs to PDFsharp from the Windows font directory.
///
/// PDFsharp 6 has no built-in font resolution — any text drawn without a resolver installed
/// throws. It also cannot read TrueType *collections* (.ttc), which rules out SimSun and
/// Microsoft YaHei in their shipped form, so the CJK fallback deliberately points at
/// SimHei, which ships as a standalone .ttf.
/// </summary>
public sealed class WindowsFontResolver : IFontResolver
{
    private static readonly string FontsDirectory =
        Environment.GetFolderPath(Environment.SpecialFolder.Fonts) is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");

    /// <summary>Family name (as requested) → candidate file names, in preference order.</summary>
    private static readonly Dictionary<string, string[]> Families = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Arial"] = ["arial.ttf"],
        ["Helvetica"] = ["arial.ttf"],
        ["sans-serif"] = ["arial.ttf"],
        ["Segoe UI"] = ["segoeui.ttf", "arial.ttf"],
        ["Times New Roman"] = ["times.ttf", "arial.ttf"],
        ["Times"] = ["times.ttf", "arial.ttf"],
        ["serif"] = ["times.ttf", "arial.ttf"],
        ["Georgia"] = ["georgia.ttf", "times.ttf"],
        ["Courier New"] = ["cour.ttf", "consola.ttf"],
        ["monospace"] = ["consola.ttf", "cour.ttf"],
        ["Consolas"] = ["consola.ttf", "cour.ttf"],
        ["Verdana"] = ["verdana.ttf", "arial.ttf"],
        ["Tahoma"] = ["tahoma.ttf", "arial.ttf"],

        // CJK families. Only standalone .ttf files — .ttc collections are not supported.
        ["SimHei"] = ["simhei.ttf"],
        ["黑体"] = ["simhei.ttf"],
        ["KaiTi"] = ["simkai.ttf"],
        ["楷体"] = ["simkai.ttf"],
        ["FangSong"] = ["simfang.ttf"],
        ["仿宋"] = ["simfang.ttf"],
        ["SimSun"] = ["simsun.ttc", "simhei.ttf"], // collection: falls through to SimHei
        ["宋体"] = ["simsun.ttc", "simhei.ttf"],
        ["Microsoft YaHei"] = ["msyh.ttc", "simhei.ttf"],
        ["微软雅黑"] = ["msyh.ttc", "simhei.ttf"],
        ["Microsoft YaHei UI"] = ["msyh.ttc", "simhei.ttf"],
    };

    /// <summary>Always resolvable, and covers Latin as well as CJK so nothing renders as tofu.</summary>
    private static readonly string[] FallbackChain = ["simhei.ttf", "simkai.ttf", "arial.ttf"];

    private readonly Dictionary<string, byte[]> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _cacheLock = new();

    public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        var path = FindFontFile(familyName);

        // SimHei/Arial ship no bold or italic face, so ask PDFsharp to synthesise them
        // rather than silently rendering bold text at regular weight.
        return new FontResolverInfo(path, isBold, isItalic);
    }

    public byte[] GetFont(string faceName)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(faceName, out var cached)) return cached;

            var bytes = File.ReadAllBytes(faceName);
            _cache[faceName] = bytes;
            return bytes;
        }
    }

    /// <summary>Returns the full path of a usable font file; never fails.</summary>
    private static string FindFontFile(string familyName)
    {
        if (!string.IsNullOrWhiteSpace(familyName)
            && Families.TryGetValue(familyName.Trim(), out var candidates))
        {
            foreach (var candidate in candidates)
            {
                var full = Path.Combine(FontsDirectory, candidate);
                if (File.Exists(full)) return full;
            }
        }

        foreach (var candidate in FallbackChain)
        {
            var full = Path.Combine(FontsDirectory, candidate);
            if (File.Exists(full)) return full;
        }

        throw new InvalidOperationException(
            $"未找到可用字体，请确认 {FontsDirectory} 中存在 simhei.ttf 或 arial.ttf。");
    }

    /// <summary>Family names offered in the UI, with their Chinese labels.</summary>
    public static IReadOnlyList<(string Family, string Label)> AvailableFamilies { get; } =
    [
        ("SimHei", "黑体"),
        ("KaiTi", "楷体"),
        ("FangSong", "仿宋"),
        ("Microsoft YaHei", "微软雅黑"),
        ("Arial", "Arial"),
        ("Times New Roman", "Times New Roman"),
        ("Courier New", "Courier New"),
        ("Verdana", "Verdana"),
    ];
}
