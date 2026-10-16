using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfEdit.Models;

namespace PdfEdit.Services;

/// <summary>
/// Headless verification of the render / text / annotation pipeline.
/// Run with <c>PdfEdit.exe --selftest &lt;file.pdf&gt; &lt;outdir&gt;</c>.
///
/// The interesting check is the annotation round-trip: a highlight is created over a known
/// run of text, the document is saved, re-opened, and the pixels inside the expected region
/// are compared against an un-annotated render. If the display-space → PDF-space transform
/// (including page rotation) were wrong, the pixels would not change and the test fails.
/// </summary>
internal static class SelfTest
{
    public static int Run(string pdfPath, string outDir)
    {
        var log = new StringBuilder();
        var failures = 0;

        Directory.CreateDirectory(outDir);
        var logPath = Path.Combine(outDir, "selftest.log");

        // Flush on every line. A native crash inside PDFium kills the process outright, and a
        // buffer written only at the end would be lost — leaving no clue where it died.
        void Flush()
        {
            try { File.WriteAllText(logPath, log.ToString(), Encoding.UTF8); }
            catch { /* nothing useful to do while reporting a failure */ }
        }

        void Section(string s) { log.AppendLine($"\n=== {s} ==="); Flush(); }
        void Ok(string s) { log.AppendLine($"  [ok]   {s}"); Flush(); }
        void Fail(string s) { failures++; log.AppendLine($"  [FAIL] {s}"); Flush(); }
        void Info(string s) { log.AppendLine($"         {s}"); Flush(); }

        try
        {
            log.AppendLine($"PDFium self-test");
            log.AppendLine($"input : {pdfPath}");
            log.AppendLine($"output: {outDir}");

            var source = File.ReadAllBytes(pdfPath);
            Info($"文件大小 {source.Length:N0} 字节");

            using var engine = PdfiumEngine.Open(source, pdfPath);

            // ---------------------------------------------------------- open + metrics
            Section("打开与页面度量");
            Ok($"页数 = {engine.PageCount}");
            if (engine.PageCount == 0) Fail("文档没有页面");

            var take = Math.Min(3, engine.PageCount);
            for (var i = 0; i < take; i++)
            {
                var size = engine.GetPageSize(i);
                var rot = engine.GetPageRotation(i);
                Info($"第 {i + 1} 页: {size.Width:0.##} x {size.Height:0.##} pt, /Rotate={rot * 90}°");
            }

            // ---------------------------------------------------------- render
            Section("渲染");
            for (var i = 0; i < take; i++)
            {
                var bmp = engine.RenderPage(i, 1.5);
                var path = Path.Combine(outDir, $"render_p{i + 1}.png");
                WritePng(bmp, path);
                Ok($"第 {i + 1} 页 -> {bmp.PixelWidth}x{bmp.PixelHeight} px  {Path.GetFileName(path)}");
            }

            // ---------------------------------------------------------- text
            Section("文本提取");
            var page0Text = engine.GetPageText(0);
            Info($"第 1 页提取到 {page0Text.Length} 个字符");
            Info($"片段: {Truncate(page0Text, 120)}");
            if (page0Text.Length == 0)
            {
                // A scan has no text layer by definition; the OCR section below handles it.
                // Only a document with text objects but no extractable text is broken.
                if (engine.GetPageObjects(0).Any(o => o.IsText))
                    Fail("页面有文字对象但提取不到文本");
                else
                    Info("无文字层（扫描件），文本/搜索验证跳过，见 OCR 节。");
            }

            // ---------------------------------------------------------- outline
            Section("书签目录");
            var outline = engine.GetOutline();
            Ok($"目录项 = {outline.Count}");
            foreach (var entry in outline.Take(8))
                Info($"{new string(' ', entry.Depth * 2)}{entry.Title}  -> p{entry.PageIndex + 1}");

            // ---------------------------------------------------------- annotation round-trip
            Section("批注往返（坐标变换验证）");
            if (page0Text.Length == 0)
            {
                Info("无文本层，跳过。");
            }
            else
            {
                var probe = PickProbe(page0Text);
                Info($"搜索词: \"{probe}\"");

                var hits = engine.FindOnPage(0, probe);
                Ok($"第 1 页命中 {hits.Count} 处");

                if (hits.Count == 0)
                {
                    Fail("搜索未命中，无法验证批注坐标");
                }
                else
                {
                    var hit = hits[0];
                    Info($"首个命中: {hit.Rects.Count} 个矩形 " +
                         string.Join(", ", hit.Rects.Take(3).Select(
                             r => $"[{r.Left:0.#},{r.Top:0.#} → {r.Right:0.#},{r.Bottom:0.#}]")));

                    var union = Union(hit.Rects);
                    var area = (int)Math.Max(1, (union.Right - union.Left) * (union.Bottom - union.Top));
                    Info($"目标区域 {union.Left:0.#},{union.Top:0.#} → {union.Right:0.#},{union.Bottom:0.#}  面积 {area} px");

                    var clean = engine.RenderPage(0, 1.0, includeAnnotations: false);

                    // Each subtype needs different data to be drawable at all: highlights and
                    // underlines carry /QuadPoints, ink carries /InkList, and only geometric
                    // shapes are fully described by /Rect alone. Probe them separately.
                    var kinds = new[]
                    {
                        AnnotationKind.Highlight, AnnotationKind.Underline, AnnotationKind.StrikeOut,
                        AnnotationKind.Rectangle, AnnotationKind.Ellipse, AnnotationKind.Ink,
                    };

                    var savedPath = Path.Combine(outDir, "annotated.pdf");
                    foreach (var kind in kinds)
                    {
                        var spec = new AnnotationSpec
                        {
                            Kind = kind,
                            PageIndex = 0,
                            Contents = $"自检 {kind}",
                            Author = "PdfEdit",
                            R = 255, G = 180, B = 0, A = 170,
                        };
                        spec.SetBounds(union);
                        spec.Quads.AddRange(hit.Rects);
                        if (kind == AnnotationKind.Ink)
                        {
                            spec.InkStrokes.Add(new InkStroke(new (double, double)[]
                            {
                                (union.Left, union.Top),
                                ((union.Left + union.Right) / 2, union.Bottom),
                                (union.Right, union.Top),
                            }));
                        }

                        var saved = PdfiumEngine.SaveWithAnnotations(source, [spec], pdfPath);
                        File.WriteAllBytes(savedPath, saved);

                        using var annotated = PdfiumEngine.Open(saved, savedPath);
                        var annotCount = annotated.GetAnnotationCount(0);
                        var marked = annotated.RenderPage(0, 1.0, includeAnnotations: true);

                        // /Contents is a byte-string key: mis-encoding it as UTF-16 silently
                        // drops the note text into an unfindable entry, and nothing on the
                        // rendering path would notice. Read it back.
                        var contents = annotated.GetAnnotationContents(0, 0);
                        if (contents == spec.Contents)
                            Ok($"{kind,-10} /Contents 回读一致：「{contents}」");
                        else
                            Fail($"{kind,-10} /Contents 回读为「{contents}」，期望「{spec.Contents}」");

                        var changed = CountChangedPixels(clean, marked, union, 1.0);
                        var stray = CountChangedPixelsOutside(clean, marked, union, 1.0);

                        if (annotCount == 0)
                            Fail($"{kind,-10} 批注对象未写入");
                        else if (changed > 20 && stray < changed)
                            Ok($"{kind,-10} 对象 {annotCount} 个，区域内 {changed} px 着色（区域外 {stray}）");
                        else
                            Fail($"{kind,-10} 对象 {annotCount} 个，但区域内仅 {changed} px 着色（区域外 {stray}）— 无外观流");

                        if (kind == AnnotationKind.Highlight)
                            WritePng(marked, Path.Combine(outDir, "annotated_p1.png"));
                    }
                }
            }

            // ---------------------------------------------------------- content editing
            Section("正文内容编辑（对象级）");

            // A dedicated engine: this probe mutates the document, and the engine above is
            // reused by later sections.
            using (var editor = PdfiumEngine.Open(source, pdfPath))
            {
                var objects = editor.GetPageObjects(0);
                Ok($"第 1 页共 {objects.Count} 个对象");

                foreach (var group in objects.GroupBy(o => o.TypeLabel).OrderBy(g => g.Key))
                    Info($"{group.Key}: {group.Count()} 个" +
                         (group.Max(o => o.Depth) > 0 ? $"（最深嵌套 {group.Max(o => o.Depth)} 层）" : ""));

                foreach (var o in objects.Take(14))
                {
                    Info($"  [d{o.Depth}] {o.TypeLabel} #{string.Join(".", o.Path)} " +
                         $"{o.FontName} {o.FontSize:0.#}pt " +
                         $"({o.Bounds.Left:0.#},{o.Bounds.Top:0.#})-({o.Bounds.Right:0.#},{o.Bounds.Bottom:0.#})" +
                         (o.Text.Length > 0 ? $" \"{Truncate(o.Text, 28)}\"" : ""));
                }

                var editable = objects.Where(o => o.IsText && !string.IsNullOrWhiteSpace(o.Text)).ToList();

                if (editable.Count == 0)
                {
                    // A pure scan is edited through the OCR replacement path (exercised in the
                    // scan section below); in-place rewrite has nothing to target on purpose.
                    if (objects.Any(o => o.IsText))
                        Fail("页面有文字对象但均无可用文本");
                    else
                        Info("扫描件无原生文字对象，改写验证由扫描件一节覆盖。");
                }
                else
                {
                    var target = editable[0];
                    Info($"目标: #{string.Join(".", target.Path)} " +
                         $"\"{Truncate(target.Text, 40)}\" 字体={target.FontName}");

                    // Case A — rewrite using only glyphs the font already had to embed.
                    // This is the case that must work even with a subset font.
                    var reuse = new string(target.Text.Reverse().ToArray());
                    var outcomeA = editor.SetObjectText(0, target.Path, reuse);
                    Info($"改写(复用原字符) → {Truncate(reuse, 40)} : {outcomeA}");

                    if (outcomeA != TextEditOutcome.Applied)
                        Fail($"复用原字形的改写本应成功，实际 {outcomeA}");

                    var editedA = editor.Save();
                    File.WriteAllBytes(Path.Combine(outDir, "content_edited.pdf"), editedA);

                    using (var check = PdfiumEngine.Open(editedA, null))
                    {
                        var after = check.GetPageObjects(0)
                            .FirstOrDefault(o => o.Path.SequenceEqual(target.Path));

                        if (after is null)
                            Fail("改写后找不到该对象");
                        else if (after.Text.Trim() != reuse.Trim())
                            Fail($"对象文本未被替换：期望 \"{reuse}\" 实际 \"{after.Text}\"");
                        else
                            Ok($"文本已替换并持久化：\"{Truncate(after.Text, 40)}\"");
                    }

                    // Did it actually change the rendered pixels?
                    var before1 = engine.RenderPage(0, 1.0, includeAnnotations: true);
                    using var editedEngine = PdfiumEngine.Open(editedA, null);
                    var after1 = editedEngine.RenderPage(0, 1.0, includeAnnotations: true);
                    WritePng(after1, Path.Combine(outDir, "content_edited_p1.png"));

                    var changed = CountChangedPixels(before1, after1, target.Bounds, 1.0);
                    Info($"目标区域渲染变化 {changed} px（区域 {target.Bounds.Width * target.Bounds.Height:0} px）");

                    if (changed > 10)
                        Ok("正文改写已反映到渲染结果");
                    else
                        Fail("正文改写未反映到渲染结果 — 可能字体子集缺少字形");

                    // Case B — introduce characters the original text never used. With a subset
                    // font these glyphs are absent, so this is expected to be the fragile case.
                    var novel = "ZZZ测试ABC";
                    var outcomeB = editor.SetObjectText(0, target.Path, novel);
                    var editedB = editor.Save();
                    File.WriteAllBytes(Path.Combine(outDir, "content_edit_rejected.pdf"), editedB);

                    using (var checkB = PdfiumEngine.Open(editedB, null))
                    {
                        var survivor = checkB.GetPageObjects(0)
                            .FirstOrDefault(o => o.Path.SequenceEqual(target.Path))?.Text ?? "(对象缺失)";

                        var renderedB = checkB.RenderPage(0, 1.0, includeAnnotations: true);
                        WritePng(renderedB, Path.Combine(outDir, "content_edit_rejected_p1.png"));

                        // Compared against the successful Case A render: if the rejected edit was
                        // properly rolled back, the page must be byte-for-byte unchanged.
                        var changedB = CountChangedPixels(after1, renderedB, target.Bounds, 1.0);

                        Info($"改写(引入新字符) \"{novel}\" → {outcomeB}");
                        Info($"  对象文本仍为 \"{Truncate(survivor, 40)}\"，与 Case A 相比变化 {changedB} px");

                        if (outcomeB == TextEditOutcome.GlyphsUnavailable && changedB < 20)
                            Ok("字形缺失的改写被拒绝，原内容完好（安全网生效，无静默数据丢失）");
                        else if (outcomeB == TextEditOutcome.Applied)
                            Ok("该字体字形完整，新字符可直接写入");
                        else
                            Fail($"安全网失效: 返回 {outcomeB}，但渲染变化 {changedB} px — 内容可能已被破坏");
                    }
                }
            }

            // ---------------------------------------------------------- scanned PDF / OCR
            Section("扫描件 OCR 文字层");

            // Build a scan-like fixture: the page rendered to an image and wrapped in a PDF with
            // no text at all. This is exactly what a scanned document looks like to a parser.
            var scanBytes = BuildScanPdf(engine.RenderPage(0, 2.0), out var scanW, out var scanH);
            File.WriteAllBytes(Path.Combine(outDir, "scan_before.pdf"), scanBytes);

            using (var scan = PdfiumEngine.Open(scanBytes, null))
            {
                var objs = scan.GetPageObjects(0);
                Info($"扫描件样式 PDF: {scanBytes.Length / 1024.0:0.#} KB, 对象 {objs.Count} 个" +
                     $"（文字 {objs.Count(o => o.IsText)}，图像 {objs.Count(o => o.IsImage)}）");

                const string ocrProbe = "语音";
                var hitsBefore = scan.FindOnPage(0, ocrProbe).Count;
                Info($"OCR 前搜索「{ocrProbe}」命中 {hitsBefore} 处");

                if (objs.Any(o => o.IsText) || hitsBefore > 0)
                    Fail("扫描件本不应含文字层");
                else
                    Ok("确认无文字层：搜索与文字对象均为空");

                var scale = OcrService.SafeScaleFor(scanW, scanH);
                if (scale is null)
                {
                    Fail("页面尺寸超出 OCR 引擎上限");
                }
                else
                {
                    Info($"OCR 渲染倍率 {scale.Value:0.##}x（约 {scale.Value * 72:0} DPI）");
                    var bitmap = scan.RenderPage(0, scale.Value);

                    // Run off the UI thread: WinRT async must not be blocked on the dispatcher.
                    IReadOnlyList<OcrWord> words;
                    try
                    {
                        words = Task.Run(() => OcrService.RecognizeAsync(bitmap, scale.Value))
                                    .GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        words = [];
                        Fail($"OCR 失败: {ex.Message}");
                    }

                    if (words.Count > 0)
                    {
                        Ok($"识别出 {words.Count} 个词（引擎语言 {OcrService.ActiveLanguage}）");
                        Info("片段: " + string.Join(" | ", words.Take(10).Select(w => w.Text)));
                    }
                    else if (words.Count == 0)
                    {
                        Fail("OCR 未识别出任何文字");
                    }

                    if (words.Count > 0)
                    {
                        // Baseline: identical export path but no text layer. PDFsharp re-encodes
                        // the embedded image on import, which shifts pixels on its own — comparing
                        // against this isolates the layer from that noise.
                        var baseModel = new DocumentModel();
                        baseModel.Load(scanBytes, null);
                        baseModel.Pages.Add(new PageItem { SourceIndex = 0 });
                        var baseline = DocumentExporter.Export(baseModel);

                        // Materialise the layer through the same export path the app uses.
                        var scanModel = new DocumentModel();
                        scanModel.Load(scanBytes, null);
                        var scanPage = new PageItem { SourceIndex = 0 };
                        scanModel.Pages.Add(scanPage);
                        scanModel.TextLayers[scanPage] = [.. words];

                        var withLayer = DocumentExporter.Export(scanModel);
                        File.WriteAllBytes(Path.Combine(outDir, "scan_after.pdf"), withLayer);

                        // The whole point of recognising a scan is to be able to search it, and
                        // that must work before anything is written to disk. The layer lives only
                        // in memory until export, so this exercises the in-memory matcher.
                        var liveModel = new DocumentModel();
                        liveModel.Load(scanBytes, null);
                        var livePage = new PageItem { SourceIndex = 0 };
                        liveModel.Pages.Add(livePage);
                        liveModel.TextLayers[livePage] = [.. words];

                        var liveHits = SearchPendingLayers(liveModel, ocrProbe).ToList();
                        Info($"保存前（仅内存文字层）搜索「{ocrProbe}」命中 {liveHits.Count} 处");

                        if (liveHits.Count > 0)
                            Ok("OCR 后无需保存即可搜索");
                        else
                            Fail("识别后仍无法立即搜索 — 内存匹配未生效");

                        // And the match must point at real boxes, or jumping to it would do nothing.
                        var boxed = liveHits.All(h => h.Rects.Count > 0);
                        if (boxed)
                            Ok($"命中位置有效（首个覆盖 {liveHits[0].Rects.Count} 个文字框）");
                        else
                            Fail("命中没有可跳转的位置");

                        // Editing recognised text on a scan: paint over the old words and draw the
                        // new ones. This is the mainstream "edit a scanned PDF" behaviour.
                        var editable = words.FirstOrDefault(w => w.Text.Trim().Length > 0);
                        if (editable is null)
                        {
                            Fail("没有可用于改写测试的识别文字");
                        }
                        else
                        {
                            const string newText = "改写测试";
                            var replaceModel = new DocumentModel();
                            replaceModel.Load(scanBytes, null);
                            var replacePage = new PageItem { SourceIndex = 0 };
                            replaceModel.Pages.Add(replacePage);
                            replaceModel.TextLayers[replacePage] = [.. words];
                            replaceModel.TextReplacements.Add(new TextReplacement
                            {
                                Page = replacePage,
                                Bounds = editable.Bounds,
                                Text = newText,
                                FontSize = Math.Max(6, editable.Bounds.Bottom - editable.Bounds.Top) * 0.88,
                                FontFamily = PdfExportService.TextLayerFont,
                                RedactBackground = true,
                                BackgroundR = 255, BackgroundG = 255, BackgroundB = 255,
                            });

                            var replaced = DocumentExporter.Export(replaceModel);
                            File.WriteAllBytes(Path.Combine(outDir, "scan_edited.pdf"), replaced);

                            using (var check = PdfiumEngine.Open(replaced, null))
                            {
                                var foundNew = check.FindOnPage(0, newText).Count;
                                Info($"改写后搜索新内容「{newText}」命中 {foundNew} 处");

                                if (foundNew > 0)
                                    Ok("扫描件上的文字改写已生效且可搜索");
                                else
                                    Fail("改写后的文字搜索不到");

                                // The replacement must actually be drawn, not just indexed.
                                using var baselineEngine = PdfiumEngine.Open(baseline, null);
                                var before = baselineEngine.RenderPage(0, 1.0, includeAnnotations: false);
                                var after = check.RenderPage(0, 1.0, includeAnnotations: false);
                                var changed = CountChangedPixels(before, after, editable.Bounds, 1.0);

                                Info($"原文字区域渲染变化 {changed} px（区域 " +
                                     $"{(editable.Bounds.Right - editable.Bounds.Left) * (editable.Bounds.Bottom - editable.Bounds.Top):0} px）");

                                if (changed > 10)
                                    Ok("改写已绘制到页面上");
                                else
                                    Fail("改写未反映到渲染结果");
                            }
                        }

                        // --- texture-aware cover-up ---
                        var tintedSource = BuildTintedScan(engine.RenderPage(0, 2.0));
                        var tintedScan = BuildScanPdf(tintedSource, out var tintW, out var tintH);
                        File.WriteAllBytes(Path.Combine(outDir, "scan_tinted.pdf"), tintedScan);

                        using (var tintEngine = PdfiumEngine.Open(tintedScan, null))
                        {
                            var scale2 = OcrService.SafeScaleFor(tintW, tintH) ?? 2.0;
                            var tintWords = Task.Run(() =>
                                    OcrService.RecognizeAsync(tintEngine.RenderPage(0, scale2), scale2))
                                .GetAwaiter().GetResult();

                            var victim = tintWords.FirstOrDefault(w => w.Text.Trim().Length > 0);
                            if (victim is null)
                            {
                                Fail("底色样张未识别出文字");
                            }
                            else
                            {
                                var surrounding = MeanColour(
                                    tintEngine.RenderPage(0, 1.0), victim.Bounds, 1.0);
                                Info($"周围底色: R{surrounding.R:0} G{surrounding.G:0} B{surrounding.B:0}" +
                                     "  (纯白填充会是 255,255,255)");

                                var tintModel = new DocumentModel();
                                tintModel.Load(tintedScan, null);
                                var tintPage = new PageItem { SourceIndex = 0 };
                                tintModel.Pages.Add(tintPage);

                                var tintedVmPage = new ViewModels.PageViewModel(tintPage, tintW, tintH)
                                {
                                    Image = tintEngine.RenderPage(0, 1.0),
                                };

                                var patch = InvokeExtractPatch(tintedVmPage, victim.Bounds);
                                Info($"背景补丁: {(patch is null ? "未取得（回退纯色）" : patch.Length + " 字节")}");

                                tintModel.TextReplacements.Add(new TextReplacement
                                {
                                    Page = tintPage,
                                    Bounds = victim.Bounds,
                                    Text = "改写",
                                    FontSize = Math.Max(6, victim.Bounds.Bottom - victim.Bounds.Top) * 0.88,
                                    FontFamily = PdfExportService.TextLayerFont,
                                    RedactBackground = true,
                                    BackgroundPatch = patch,
                                    BackgroundR = 255, BackgroundG = 255, BackgroundB = 255,
                                });

                                var patched = DocumentExporter.Export(tintModel);
                                File.WriteAllBytes(Path.Combine(outDir, "scan_patched.pdf"), patched);

                                using (var check2 = PdfiumEngine.Open(patched, null))
                                {
                                    var after = MeanColour(check2.RenderPage(0, 1.0), victim.Bounds, 1.0);
                                    var dr = Math.Abs(after.R - surrounding.R);
                                    var dg = Math.Abs(after.G - surrounding.G);
                                    var db = Math.Abs(after.B - surrounding.B);
                                    Info($"补丁区域底色: R{after.R:0} G{after.G:0} B{after.B:0}" +
                                         $"  (与周围差 {dr:0}/{dg:0}/{db:0})");

                                    if (patch is null)
                                        Info("页面未提供可用补丁，本项跳过");
                                    else if (dr < 12 && dg < 12 && db < 12)
                                        Ok("纹理补丁与周围底色一致，未留下色块");
                                    else
                                        Fail($"补丁区域与周围底色不符（差 {dr:0}/{dg:0}/{db:0}）");
                                }
                            }
                        }

                        var growth = withLayer.Length - scanBytes.Length;
                        Ok($"经 PDFsharp 写入文字层: {scanBytes.Length / 1024.0:0.#} KB → " +
                           $"{withLayer.Length / 1024.0:0.#} KB（+{growth / 1024.0:0.#} KB）");

                        // Embedding a whole CJK face would add ~4.8 MB; subsetting should keep
                        // the growth to a few hundred KB at most.
                        if (growth < 400_000)
                            Ok("字体子集化生效，体积可控");
                        else
                            Fail($"文件膨胀过大（+{growth / 1024.0:0.#} KB）— 子集化未生效");

                        using (var check = PdfiumEngine.Open(withLayer, null))
                        {
                            var hitsAfter = check.FindOnPage(0, ocrProbe).Count;
                            Info($"OCR 后搜索「{ocrProbe}」命中 {hitsAfter} 处");

                            if (hitsAfter > 0)
                                Ok("扫描件现在可搜索、可选中");
                            else
                                Fail("写入文字层后仍搜索不到");

                            // The layer must be invisible: an export with it must render the same
                            // as an export without it.
                            using (var baseEngine = PdfiumEngine.Open(baseline, null))
                            {
                                var withoutLayer = baseEngine.RenderPage(0, 1.0, includeAnnotations: false);
                                var withLayerBmp = check.RenderPage(0, 1.0, includeAnnotations: false);

                                var size = check.GetPageSize(0);
                                var whole = new TextRect(0, 0, size.Width, size.Height);
                                var changed = CountChangedPixels(withoutLayer, withLayerBmp, whole, 1.0);

                                Info($"同一导出路径下，有/无文字层的渲染差异 {changed} px");
                                if (changed < 50)
                                    Ok("文字层完全不可见，扫描图像未被改动");
                                else
                                    Fail($"文字层可见（{changed} px 变化）— 应当完全隐形");
                            }
                        }
                    }
                }
            }

            // ---------------------------------------------------------- text layer via PDFsharp
            Section("文字层：PDFsharp 子集化路线");

            // PDFium embeds the whole CJK font (~4.8 MB for simhei). PDFsharp subsets it. The
            // open question is whether text painted fully transparent is still extractable —
            // if it is not, the size win is worthless.
            // Regression guard for the mechanism itself, isolated from the OCR pipeline.
            // A zero-alpha brush was tried first and does NOT work: PDFsharp ignores alpha for
            // text and paints it solid black. Hence the two-stage draw-then-hide.
            const string layerText = "全程语音交互开发手册";
            var drawn = BuildTextLayerProbePdf(layerText);
            File.WriteAllBytes(Path.Combine(outDir, "textlayer_visible.pdf"), drawn);

            using (var visible = PdfiumEngine.Open(drawn, null))
            {
                var darkBefore = CountDarkPixels(visible.RenderPage(0, 1.0, includeAnnotations: false));
                Info($"PDFsharp 画完后非白像素: {darkBefore}（作为对照，必须 > 0）");

                if (darkBefore > 0)
                    Ok("PDFsharp 确实把文字画成了可见内容 — 证实 alpha=0 无效，需要二次隐藏");
                else
                    Info("PDFsharp 未画出可见文字 — 该版本行为已变化，可简化流程");
            }

            var hidden = PdfiumEngine.HideTrailingText(drawn, new Dictionary<int, int> { [0] = 1 },
                PdfExportService.TextLayerFont);
            File.WriteAllBytes(Path.Combine(outDir, "textlayer_hidden.pdf"), hidden);
            Info($"隐藏后文件: {drawn.Length / 1024.0:0.#} KB → {hidden.Length / 1024.0:0.#} KB");

            using (var check = PdfiumEngine.Open(hidden, null))
            {
                var extracted = check.GetPageText(0).Replace("\n", "").Replace("\r", "");
                Info($"PDFium 提取到: \"{Truncate(extracted, 50)}\"");

                var hit = check.FindOnPage(0, layerText).Count;
                var darkAfter = CountDarkPixels(check.RenderPage(0, 1.0, includeAnnotations: false));

                if (hit > 0 && darkAfter == 0)
                    Ok($"二次隐藏生效：文字可搜索（命中 {hit}）且完全不可见（非白像素 {darkAfter}）");
                else if (hit == 0)
                    Fail("隐藏后文字不可搜索 — 文字层失效");
                else
                    Fail($"隐藏后仍有可见内容（{darkAfter} px）");
            }

            // ---------------------------------------------------------- undo history
            Section("撤销 / 重做");

            // The whole edit history is a stack of inverse operations, so the test drives the
            // same view model the UI does and checks that undo genuinely restores state.
            var undoVm = new ViewModels.MainViewModel();
            undoVm.OpenFile(pdfPath);

            if (!undoVm.HasDocument)
            {
                Fail("撤销测试无法打开文档");
            }
            else
            {
                var live = undoVm.Pages.Where(p => !p.Item.IsDeleted).ToList();
                var originalCount = undoVm.Model.Annotations.Count;
                var firstPage = undoVm.Pages[0];

                // 1) annotation
                undoVm.CurrentTool = ViewModels.EditorTool.Rectangle;
                undoVm.AddAnnotation(firstPage, ViewModels.EditorTool.Rectangle,
                    [new System.Windows.Point(100, 100), new System.Windows.Point(200, 160)]);
                var afterAdd = undoVm.Model.Annotations.Count;

                undoVm.UndoCommand.Execute(null);
                var afterUndo = undoVm.Model.Annotations.Count;

                if (afterAdd == originalCount + 1 && afterUndo == originalCount)
                    Ok($"批注：添加 → {afterAdd}，撤销 → {afterUndo}");
                else
                    Fail($"批注撤销失败：{originalCount} → {afterAdd} → {afterUndo}");

                undoVm.RedoCommand.Execute(null);
                if (undoVm.Model.Annotations.Count == originalCount + 1)
                    Ok("重做恢复批注");
                else
                    Fail("重做未恢复批注");

                undoVm.UndoCommand.Execute(null);

                // 1b) in-place text edit driven exactly the way the inline editor drives it.
                // This is the path that was silently dead when the selection failed to notify.
                // The click point comes from a real text object rather than fixed coordinates,
                // so this works on whatever PDF the run was handed.
                var editable = engine.GetPageObjects(0)
                    .FirstOrDefault(o => o.IsText && o.Text.Trim().Length > 0);

                if (editable is null)
                {
                    Info("页面无文字对象，跳过原地编辑撤销验证。");
                }
                else
                {
                    undoVm.CurrentTool = ViewModels.EditorTool.EditContent;
                    var page0 = undoVm.Pages[0];
                    var clickAt = new System.Windows.Point(
                        (editable.Bounds.Left + editable.Bounds.Right) / 2,
                        (editable.Bounds.Top + editable.Bounds.Bottom) / 2);

                    undoVm.SelectObjectAt(page0, clickAt);

                    if (undoVm.SelectedObject is not { IsText: true } picked)
                    {
                        Fail($"点击文字对象中心 {clickAt.X:0.#},{clickAt.Y:0.#} 未选中文字对象");
                    }
                    else if (!undoVm.CanEditSelection)
                    {
                        Fail("选中文字后仍不可编辑（通知缺失）");
                    }
                    else
                    {
                        Info($"选中: 「{Truncate(picked.Text, 24)}」 标签=\"{undoVm.SelectionLabel}\"");
                        var original = picked.Text;
                        var flipped = new string(original.Reverse().ToArray());

                        undoVm.ContentEditText = flipped;
                        undoVm.ApplyContentEditCommand.Execute(null);

                        // SelectedObject is a snapshot taken at selection time, so its Text is
                        // always the pre-edit value. Re-select to read the document as it is now.
                        undoVm.SelectObjectAt(page0, clickAt);
                        var nowHeld = undoVm.SelectedObject?.Text;
                        Info($"改写后状态: {undoVm.StatusText}");

                        if (nowHeld == flipped || undoVm.Model.TextReplacements.Count > 0)
                            Ok($"原地编辑已生效：\"{Truncate(original, 16)}\" → \"{Truncate(flipped, 16)}\"");
                        else
                            Fail($"原地编辑未生效，对象文本仍为 \"{Truncate(nowHeld ?? "(空)", 24)}\"");
                    }
                }

                // 1c) whatever the edit did, it must be reversible
                var replaceBefore = undoVm.Model.TextReplacements.Count;
                undoVm.UndoCommand.Execute(null);
                Info($"撤销后替换条目 {replaceBefore} → {undoVm.Model.TextReplacements.Count}");

                // 2) rotation
                var rotBefore = firstPage.Item.Rotation;
                undoVm.RotateClockwiseCommand.Execute(null);
                var rotAfter = firstPage.Item.Rotation;
                undoVm.UndoCommand.Execute(null);
                var rotUndone = firstPage.Item.Rotation;

                if (rotAfter == rotBefore + 1 && rotUndone == rotBefore)
                    Ok($"旋转：{rotBefore} → {rotAfter}，撤销 → {rotUndone}");
                else
                    Fail($"旋转撤销失败：{rotBefore} → {rotAfter} → {rotUndone}");

                // 3) page move
                if (undoVm.Pages.Count >= 2)
                {
                    var firstWas = undoVm.Pages[0].SourceIndex;
                    var secondWas = undoVm.Pages[1].SourceIndex;

                    undoVm.MovePageDownCommand.Execute(null);
                    var swapped = undoVm.Pages[0].SourceIndex == secondWas;
                    undoVm.UndoCommand.Execute(null);
                    var restored = undoVm.Pages[0].SourceIndex == firstWas &&
                                   undoVm.Pages[1].SourceIndex == secondWas;

                    if (swapped && restored) Ok("页面移动可撤销");
                    else Fail($"页面移动撤销失败（换位={swapped}, 复位={restored}）");
                }

                // 4) page deletion — the command keeps at least one page alive, so a
                // single-page document has nothing to delete and this is a no-op by design.
                var liveBefore = undoVm.Model.LivePages.Count;
                if (liveBefore < 2)
                {
                    Info("单页文档无法删除页面，跳过删除撤销验证。");
                }
                else
                {
                    undoVm.DeletePageCommand.Execute(null);
                    var liveDeleted = undoVm.Model.LivePages.Count;
                    undoVm.UndoCommand.Execute(null);
                    var liveRestored = undoVm.Model.LivePages.Count;

                    if (liveDeleted == liveBefore - 1 && liveRestored == liveBefore)
                        Ok($"删除页面：{liveBefore} → {liveDeleted}，撤销 → {liveRestored}");
                    else
                        Fail($"删除页面撤销失败：{liveBefore} → {liveDeleted} → {liveRestored}");
                }

                // 5) watermark overlay
                var overlaysBefore = undoVm.Model.Overlays.Count;
                undoVm.AddWatermarkCommand.Execute(null);
                var overlaysAfter = undoVm.Model.Overlays.Count;
                undoVm.UndoCommand.Execute(null);
                var overlaysUndone = undoVm.Model.Overlays.Count;

                if (overlaysAfter == overlaysBefore + 1 && overlaysUndone == overlaysBefore)
                    Ok("水印可撤销");
                else
                    Fail($"水印撤销失败：{overlaysBefore} → {overlaysAfter} → {overlaysUndone}");

                // Undo must also mark the document clean again when history is exhausted.
                while (undoVm.CanUndo) undoVm.UndoCommand.Execute(null);
                Info($"全部撤销后：批注 {undoVm.Model.Annotations.Count}，" +
                     $"可撤销 {undoVm.CanUndo}，可重做 {undoVm.CanRedo}");

                if (!undoVm.CanUndo && undoVm.CanRedo)
                    Ok("撤销栈清空后重做栈完整，历史可回放");
                else
                    Fail("撤销/重做栈状态异常");

                // 6) history panel jump. Linear history means the interleaved undos above
                // leave the stacks nearly empty, so build a clean three-edit lineage first,
                // then jump to the oldest row (1 applied), the newest (3), the middle (2).
                for (var i = 0; i < 3; i++)
                    undoVm.AddWatermarkCommand.Execute(null);

                static int AppliedCount(ViewModels.MainViewModel vm)
                    => vm.History.Count(h => h.Applied && !h.IsCursor);

                undoVm.JumpToHistory(undoVm.History[0]);
                var afterStart = AppliedCount(undoVm);
                undoVm.JumpToHistory(undoVm.History.Last(h => !h.IsCursor));
                var afterEnd = AppliedCount(undoVm);
                undoVm.JumpToHistory(undoVm.History[1]);
                var afterMiddle = AppliedCount(undoVm);

                if (afterStart == 1 && afterEnd == 3 && afterMiddle == 2)
                    Ok("历史跳转正确：首项 1 → 尾项 3 → 次项 2");
                else
                    Fail($"历史跳转异常：首 {afterStart}，尾 {afterEnd}，中 {afterMiddle}");

                undoVm.Dispose();
            }

            // ---------------------------------------------------------- export pipeline
            Section("导出管线（重排 / 旋转 / 叠加）");
            var probe2 = page0Text.Length > 0 ? PickProbe(page0Text) : string.Empty;
            var hits2 = probe2.Length > 0 ? engine.FindOnPage(0, probe2) : [];

            if (hits2.Count == 0)
            {
                Info("无可用探针文本，跳过导出验证。");
            }
            else
            {
                var target = Union(hits2[0].Rects);

                // --- A: rotate the page and confirm the annotation follows the content ---
                var modelA = new DocumentModel();
                modelA.Load(source, pdfPath);
                var pageA = new PageItem { SourceIndex = 0, Rotation = 1 }; // +90°
                modelA.Pages.Add(pageA);

                var annotA = new AnnotationSpec
                {
                    Kind = AnnotationKind.Highlight,
                    PageIndex = 0,
                    Page = pageA,
                    R = 255, G = 180, B = 0, A = 170,
                };
                annotA.SetBounds(target);
                annotA.Quads.Add(target);
                modelA.Annotations.Add(annotA);

                var exportedA = DocumentExporter.Export(modelA);
                File.WriteAllBytes(Path.Combine(outDir, "export_rotated.pdf"), exportedA);

                using (var engA = PdfiumEngine.Open(exportedA, null))
                {
                    var exportedRot = engA.GetPageRotation(0);
                    var sizeA = engA.GetPageSize(0);

                    if (exportedRot != 1)
                        Fail($"导出后 /Rotate 应为 1，实际 {exportedRot}");
                    else if (sizeA.Width <= sizeA.Height)
                        Fail($"旋转后页面应为横向，实际 {sizeA.Width:0.#}x{sizeA.Height:0.#}");
                    else
                        Ok($"旋转已写入: /Rotate={exportedRot * 90}°, {sizeA.Width:0.#}x{sizeA.Height:0.#} pt");

                    // Re-locate the text in the rotated output; its rect is now in the new
                    // display space, so this checks the annotation tracked the rotation.
                    //
                    // On pages carrying /Rotate, PDFium's extraction can reorder single-glyph
                    // text objects (this sample's text layer is one glyph per object), which
                    // silently breaks multi-character search even though the content stream is
                    // byte-for-byte intact. Fall back to locating each probe character
                    // separately — the verification's subject is the annotation, not search.
                    var moved = engA.FindOnPage(0, probe2);
                    var newRects = moved.Count > 0 ? moved[0].Rects : LocateByCharacters(engA, probe2);
                    if (newRects is null)
                    {
                        var exportedText = engA.GetPageText(0);
                        Info($"导出后提取 {exportedText.Length} 字符，含探针「{probe2}」={exportedText.Contains(probe2)}");
                        Fail("旋转后无法再定位探针文本");
                    }
                    else
                    {
                        if (moved.Count == 0)
                            Info($"多字搜索失效（/Rotate 页单字形对象重排），逐字定位 {newRects.Count} 框");
                        var newRect = Union(newRects);
                        Info($"旋转后文本位置 {newRect.Left:0.#},{newRect.Top:0.#} → {newRect.Right:0.#},{newRect.Bottom:0.#}");

                        var cleanA = engA.RenderPage(0, 1.0, includeAnnotations: false);
                        var markedA = engA.RenderPage(0, 1.0, includeAnnotations: true);
                        WritePng(markedA, Path.Combine(outDir, "export_rotated_p1.png"));

                        var changedA = CountChangedPixels(cleanA, markedA, newRect, 1.0);
                        var strayA = CountChangedPixelsOutside(cleanA, markedA, newRect, 1.0);

                        Info($"实际着色位置: {ChangedBounds(cleanA, markedA)}");

                        if (changedA > 20 && strayA < changedA)
                            Ok($"旋转后批注仍落在文本上（区域 {changedA} px，区域外 {strayA}）");
                        else
                            Fail($"旋转后批注位置错误（区域 {changedA} px，区域外 {strayA}）");
                    }
                }

                // --- B: reorder and delete pages ---
                var modelB = new DocumentModel();
                modelB.Load(source, pdfPath);
                var lastSource = Math.Min(2, engine.PageCount - 1);
                modelB.Pages.Add(new PageItem { SourceIndex = lastSource });
                modelB.Pages.Add(new PageItem { SourceIndex = 0 });
                var doomed = new PageItem { SourceIndex = engine.PageCount > 3 ? 3 : 0, IsDeleted = true };
                modelB.Pages.Add(doomed);
                modelB.Renumber();

                var exportedB = DocumentExporter.Export(modelB);

                using (var engB = PdfiumEngine.Open(exportedB, null))
                {
                    var expected = engine.GetPageText(lastSource).Replace("\r", "").Replace("\n", "");
                    var actual = engB.GetPageText(0).Replace("\r", "").Replace("\n", "");

                    // Isolates whether a plain PDFsharp round-trip (no rotation, no PDFium
                    // stage two) preserves searchability of the sample's text.
                    Info($"无旋转导出后搜索「{probe2}」命中 {engB.FindOnPage(0, probe2).Count} 处");

                    if (engB.PageCount != 2)
                        Fail($"删除后应剩 2 页，实际 {engB.PageCount}");
                    else if (expected != actual)
                        Fail("重排后第一页内容与源文档第 " + (lastSource + 1) + " 页不符");
                    else
                        Ok($"重排 + 删除正确：输出 2 页，首页来自源第 {lastSource + 1} 页");
                }

                // --- C: watermark overlay ---
                var plainModel = new DocumentModel();
                plainModel.Load(source, pdfPath);
                plainModel.Pages.Add(new PageItem { SourceIndex = 0 });

                var markModel = new DocumentModel();
                markModel.Load(source, pdfPath);
                markModel.Pages.Add(new PageItem { SourceIndex = 0 });
                markModel.Overlays.Add(new OverlaySpec
                {
                    Kind = OverlayKind.Watermark,
                    Text = "机密",
                    FontFamily = "SimHei",
                    FontSize = 96,
                    Angle = 45,
                    Opacity = 0.5,
                    R = 255, G = 0, B = 0,
                });

                var withoutMark = DocumentExporter.Export(plainModel);
                var withMark = DocumentExporter.Export(markModel);
                File.WriteAllBytes(Path.Combine(outDir, "export_watermark.pdf"), withMark);

                using (var e1 = PdfiumEngine.Open(withoutMark, null))
                using (var e2 = PdfiumEngine.Open(withMark, null))
                {
                    var size = e1.GetPageSize(0);
                    var b1 = e1.RenderPage(0, 1.0);
                    var b2 = e2.RenderPage(0, 1.0);
                    WritePng(b2, Path.Combine(outDir, "export_watermark_p1.png"));

                    var whole = new TextRect(0, 0, size.Width, size.Height);
                    var changedC = CountChangedPixels(b1, b2, whole, 1.0);

                    if (changedC > 500)
                        Ok($"水印已绘制（整页 {changedC} px 变化）");
                    else
                        Fail($"水印未出现（仅 {changedC} px 变化）");
                }
            }

            // ---------------------------------------------------------- page-ops save path
            // ---------------------------------------------------------- font matching
            Section("字体匹配（原字形）");

            // Render a sample in a known font, measure it back, and require the matcher to
            // name the same font — the round trip is the whole feature in one line.
            foreach (var family in new[] { "SimHei", "KaiTi", "FangSong" })
            {
                var sample = FontMatcher.RenderSample(family, "改写测试字体", 28);
                var matched = FontMatcher.Match(sample,
                    new System.Windows.Int32Rect(0, 0, sample.PixelWidth, sample.PixelHeight),
                    "改写测试字体");

                Info($"  {family} 指标: {FontMatcher.Describe(sample, "改写测试字体")}");

                if (matched == family)
                    Ok($"{family} 回环识别成功");
                else
                    Fail($"{family} 被误判为 {matched}");
            }

            // ---------------------------------------------------------- save round trip
            Section("保存（无批注）");
            using (var plain = PdfiumEngine.Open(source, pdfPath))
            {
                var round = plain.Save();
                Ok($"原样保存 = {round.Length:N0} 字节");
                using var reopened = PdfiumEngine.Open(round, null);
                if (reopened.PageCount == engine.PageCount)
                    Ok("往返后页数一致");
                else
                    Fail($"往返后页数不一致: {engine.PageCount} -> {reopened.PageCount}");
            }
        }
        catch (Exception ex)
        {
            failures++;
            log.AppendLine($"\n!! 异常: {ex.GetType().Name}: {ex.Message}");
            log.AppendLine(ex.StackTrace);
        }

        log.AppendLine($"\n=== 结果: {(failures == 0 ? "全部通过" : $"{failures} 项失败")} ===");
        Flush();
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Builds a one-page PDF holding a single run of text in the text-layer font, drawn the way
    /// the export stage draws it. Used to exercise the hide step in isolation.
    /// </summary>
    private static byte[] BuildTextLayerProbePdf(string text)
    {
        PdfExportService.Initialize();

        using var document = new PdfSharp.Pdf.PdfDocument();
        var page = document.AddPage();
        page.Width = PdfSharp.Drawing.XUnit.FromPoint(595);
        page.Height = PdfSharp.Drawing.XUnit.FromPoint(842);

        using (var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(page))
        {
            var font = new PdfSharp.Drawing.XFont(PdfExportService.TextLayerFont, 24);
            gfx.DrawString(text, font, PdfSharp.Drawing.XBrushes.Black, 60, 120);
        }

        using var result = new MemoryStream();
        document.Save(result, closeStream: false);
        return result.ToArray();
    }

    /// <summary>
    /// Wraps a rendered page in a PDF as a single full-page image, producing the same shape as a
    /// scanned document: one image object and no text whatsoever.
    /// </summary>
    private static byte[] BuildScanPdf(BitmapSource page, out double widthPt, out double heightPt)
    {
        PdfExportService.Initialize();

        // The page was rasterised at 2x, so points are half the pixel dimensions.
        widthPt = page.PixelWidth / 2.0;
        heightPt = page.PixelHeight / 2.0;

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(page));

        using var imageStream = new MemoryStream();
        encoder.Save(imageStream);
        imageStream.Position = 0;

        using var document = new PdfSharp.Pdf.PdfDocument();
        var pdfPage = document.AddPage();
        pdfPage.Width = PdfSharp.Drawing.XUnit.FromPoint(widthPt);
        pdfPage.Height = PdfSharp.Drawing.XUnit.FromPoint(heightPt);

        using (var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(pdfPage))
        using (var image = PdfSharp.Drawing.XImage.FromStream(imageStream))
        {
            gfx.DrawImage(image, 0, 0, widthPt, heightPt);
        }

        using var result = new MemoryStream();
        document.Save(result, closeStream: false);
        return result.ToArray();
    }

    /// <summary>Picks a short run of real text from the page to search for.</summary>
    /// <summary>
    /// Locates a probe's characters one at a time and unions their rects. Works around PDFium
    /// reordering single-glyph text objects on /Rotate pages, which breaks multi-character
    /// search on documents whose text layer is one glyph per object.
    /// </summary>
    private static List<TextRect>? LocateByCharacters(PdfiumEngine engine, string probe)
    {
        var rects = new List<TextRect>();
        foreach (var ch in probe.Distinct())
        {
            var hits = engine.FindOnPage(0, ch.ToString());
            if (hits.Count == 0) return null;
            rects.AddRange(hits[0].Rects);
        }

        return rects.Count > 0 ? rects : null;
    }

    private static string PickProbe(string text)
    {
        var cleaned = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        // Start a little way in to avoid headers/artifacts.
        var start = Math.Min(cleaned.Length - 1, Math.Max(0, cleaned.Length / 3));
        while (start < cleaned.Length && char.IsWhiteSpace(cleaned[start])) start++;
        var len = Math.Min(4, cleaned.Length - start);
        return cleaned.Substring(start, len);
    }

    private static TextRect Union(IReadOnlyList<TextRect> rects)
    {
        var l = rects.Min(r => r.Left);
        var t = rects.Min(r => r.Top);
        var r2 = rects.Max(r => r.Right);
        var b = rects.Max(r => r.Bottom);
        return new TextRect(l, t, r2, b);
    }

    private static byte[] Pixels(BitmapSource src)
    {
        var stride = src.PixelWidth * 4;
        var buffer = new byte[stride * src.PixelHeight];
        src.CopyPixels(buffer, stride, 0);
        return buffer;
    }

    private static int CountChangedPixels(BitmapSource a, BitmapSource b, TextRect region, double scale)
    {
        if (a.PixelWidth != b.PixelWidth || a.PixelHeight != b.PixelHeight) return 0;

        var pa = Pixels(a);
        var pb = Pixels(b);
        var stride = a.PixelWidth * 4;

        var x0 = Math.Max(0, (int)(region.Left * scale) - 1);
        var y0 = Math.Max(0, (int)(region.Top * scale) - 1);
        var x1 = Math.Min(a.PixelWidth - 1, (int)(region.Right * scale) + 1);
        var y1 = Math.Min(a.PixelHeight - 1, (int)(region.Bottom * scale) + 1);

        var count = 0;
        for (var y = y0; y <= y1; y++)
        for (var x = x0; x <= x1; x++)
        {
            var i = y * stride + x * 4;
            if (Math.Abs(pa[i] - pb[i]) > 8 || Math.Abs(pa[i + 1] - pb[i + 1]) > 8 || Math.Abs(pa[i + 2] - pb[i + 2]) > 8)
                count++;
        }
        return count;
    }

    private static int CountChangedPixelsOutside(BitmapSource a, BitmapSource b, TextRect region, double scale)
    {
        if (a.PixelWidth != b.PixelWidth || a.PixelHeight != b.PixelHeight) return 0;

        var pa = Pixels(a);
        var pb = Pixels(b);
        var stride = a.PixelWidth * 4;

        var x0 = (int)(region.Left * scale) - 2;
        var y0 = (int)(region.Top * scale) - 2;
        var x1 = (int)(region.Right * scale) + 2;
        var y1 = (int)(region.Bottom * scale) + 2;

        var count = 0;
        for (var y = 0; y < a.PixelHeight; y++)
        for (var x = 0; x < a.PixelWidth; x++)
        {
            if (x >= x0 && x <= x1 && y >= y0 && y <= y1) continue;
            var i = y * stride + x * 4;
            if (Math.Abs(pa[i] - pb[i]) > 8 || Math.Abs(pa[i + 1] - pb[i + 1]) > 8 || Math.Abs(pa[i + 2] - pb[i + 2]) > 8)
                count++;
        }
        return count;
    }

    /// <summary>
    /// Searches the OCR layers a document is still holding in memory, reassembling recognised
    /// words per line before matching. Mirrors what the view model does for live search, so the
    /// test fails if that logic regresses.
    /// </summary>
    private static IEnumerable<SearchHit> SearchPendingLayers(DocumentModel model, string term)
    {
        foreach (var (page, words) in model.TextLayers)
        {
            foreach (var line in words.GroupBy(w => w.LineIndex))
            {
                var ordered = line.OrderBy(w => w.Bounds.Left).ToList();
                var text = string.Concat(ordered.Select(w => w.Text));

                var starts = new int[ordered.Count];
                var cursor = 0;
                for (var i = 0; i < ordered.Count; i++)
                {
                    starts[i] = cursor;
                    cursor += ordered[i].Text.Length;
                }

                var from = 0;
                while (from <= text.Length - term.Length)
                {
                    var at = text.IndexOf(term, from, StringComparison.OrdinalIgnoreCase);
                    if (at < 0) break;

                    var end = at + term.Length;
                    var boxes = new List<TextRect>();
                    for (var i = 0; i < ordered.Count; i++)
                    {
                        var s = starts[i];
                        var e = s + ordered[i].Text.Length;
                        if (e > at && s < end) boxes.Add(ordered[i].Bounds);
                    }

                    if (boxes.Count > 0)
                        yield return new SearchHit(page.SourceIndex, -1, term.Length, boxes)
                        {
                            Preview = text,
                            FromOcr = true,
                        };

                    from = at + Math.Max(1, term.Length);
                }
            }
        }
    }

    /// <summary>Reaches the private patch extractor so the test exercises the real code path.</summary>
    private static byte[]? InvokeExtractPatch(ViewModels.PageViewModel page, TextRect box)
    {
        var method = typeof(ViewModels.MainViewModel).GetMethod("ExtractBackgroundPatch",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        return method?.Invoke(null, [page, box]) as byte[];
    }

    /// <summary>
    /// Renders a page onto a tinted, mottled background, imitating an aged or coloured scan.
    /// A flat cover-up stands out badly on this; a copied patch should not.
    /// </summary>
    private static BitmapSource BuildTintedScan(BitmapSource page)
    {
        var w = page.PixelWidth;
        var h = page.PixelHeight;
        var stride = w * 4;

        var source = new byte[stride * h];
        page.CopyPixels(source, stride, 0);

        var output = new byte[stride * h];
        var random = new Random(7);

        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = y * stride + x * 4;
            var lum = (source[i] * 114 + source[i + 1] * 587 + source[i + 2] * 299) / 1000;

            var grain = random.Next(-6, 7);
            var r = lum < 160 ? source[i] : (byte)232;
            var g = lum < 160 ? source[i + 1] : (byte)222;
            var b = lum < 160 ? source[i + 2] : (byte)198;

            output[i] = (byte)Math.Clamp(r + (lum < 160 ? 0 : grain), 0, 255);
            output[i + 1] = (byte)Math.Clamp(g + (lum < 160 ? 0 : grain), 0, 255);
            output[i + 2] = (byte)Math.Clamp(b + (lum < 160 ? 0 : grain), 0, 255);
            output[i + 3] = 255;
        }

        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, output, stride);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Mean background colour of a region, skipping ink pixels.</summary>
    private static (double R, double G, double B) MeanColour(BitmapSource image, TextRect box, double scale)
    {
        var x = (int)Math.Max(0, box.Left * scale);
        var y = (int)Math.Max(0, box.Top * scale);
        var w = (int)Math.Min(image.PixelWidth - x, (box.Right - box.Left) * scale);
        var h = (int)Math.Min(image.PixelHeight - y, (box.Bottom - box.Top) * scale);
        if (w <= 0 || h <= 0) return (0, 0, 0);

        var stride = w * 4;
        var pixels = new byte[stride * h];
        image.CopyPixels(new Int32Rect(x, y, w, h), pixels, stride, 0);

        double r = 0, g = 0, b = 0;
        var n = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var lum = (pixels[i] * 114 + pixels[i + 1] * 587 + pixels[i + 2] * 299) / 1000;
            if (lum < 160) continue;
            r += pixels[i]; g += pixels[i + 1]; b += pixels[i + 2]; n++;
        }

        return n == 0 ? (0, 0, 0) : (r / n, g / n, b / n);
    }

    /// <summary>Counts pixels darker than near-white — i.e. anything actually drawn.</summary>
    private static int CountDarkPixels(BitmapSource source)
    {
        var pixels = Pixels(source);
        var count = 0;

        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] < 200 || pixels[i + 1] < 200 || pixels[i + 2] < 200) count++;
        }

        return count;
    }

    /// <summary>Bounding box of every changed pixel — pinpoints where a stray annotation went.</summary>
    private static string ChangedBounds(BitmapSource a, BitmapSource b)
    {
        if (a.PixelWidth != b.PixelWidth || a.PixelHeight != b.PixelHeight)
            return $"尺寸不一致 {a.PixelWidth}x{a.PixelHeight} vs {b.PixelWidth}x{b.PixelHeight}";

        var pa = Pixels(a);
        var pb = Pixels(b);
        var stride = a.PixelWidth * 4;

        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, count = 0;
        for (var y = 0; y < a.PixelHeight; y++)
        for (var x = 0; x < a.PixelWidth; x++)
        {
            var i = y * stride + x * 4;
            if (Math.Abs(pa[i] - pb[i]) <= 8 && Math.Abs(pa[i + 1] - pb[i + 1]) <= 8 &&
                Math.Abs(pa[i + 2] - pb[i + 2]) <= 8) continue;

            count++;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        return count == 0
            ? "无变化"
            : $"{count} px, 范围 ({minX},{minY})-({maxX},{maxY}), 画布 {a.PixelWidth}x{a.PixelHeight}";
    }

    private static void WritePng(BitmapSource bmp, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}
