using System.IO;
using PdfEdit.Models;

namespace PdfEdit.Services;

/// <summary>
/// Runs the two-stage export.
///
///   1. PDFsharp rebuilds the document: final page order, deletions applied, extra rotation
///      baked into /Rotate, decorative overlays drawn into the content stream.
///   2. PDFium writes the annotations as real PDF annotation objects on top of that result.
///
/// The stages run in this order because annotations are addressed by page index, and those
/// indices only become final once the page structure is settled.
/// </summary>
public static class DocumentExporter
{
    public static byte[] Export(DocumentModel model)
    {
        var livePages = model.LivePages;
        if (livePages.Count == 0)
            throw new InvalidOperationException("所有页面都被删除了，没有内容可导出。");

        // Content edits live in the PDFium document, not in the source bytes, so the caller
        // folds them in via EditedBytes before we get here.
        var contentBytes = model.EditedBytes ?? model.SourceBytes;

        // Text replaced in place has its original object removed here rather than at edit time,
        // so that undoing the edit is possible: a deleted object cannot be restored.
        var replacements = model.TextReplacements.ToList();
        contentBytes = StripReplacedObjects(contentBytes, livePages, replacements);

        // Stage 1 — structure, OCR text layers and decorations.
        var structural = PdfExportService.Build(contentBytes, livePages, model.Overlays.ToList(),
            model.TextLayers, replacements);

        // Stage 1b — hide the text layer. PDFsharp draws it visibly because it is the only
        // library here that subsets an embedded CJK font; PDFium then flips those objects to
        // the invisible text render mode.
        var layerCounts = PdfExportService.CountTextLayerObjects(livePages, model.TextLayers);
        if (layerCounts.Count > 0)
        {
            structural = PdfiumEngine.HideTrailingText(structural, layerCounts,
                PdfExportService.TextLayerFont);
        }

        // Stage 2 — annotations, re-addressed to their page's position in the output.
        var indexMap = model.BuildOutputIndexMap();
        var annotations = model.Annotations
            .Where(a => a.Page is not null && indexMap.ContainsKey(a.Page))
            .Select(a => Retarget(a, indexMap[a.Page!]))
            .ToList();

        return annotations.Count == 0
            ? structural
            : PdfiumEngine.SaveWithAnnotations(structural, annotations);
    }

    /// <summary>Deletes the page objects superseded by text replacements.</summary>
    private static byte[] StripReplacedObjects(byte[] content,
        IReadOnlyList<PageItem> livePages, IReadOnlyList<TextReplacement> replacements)
    {
        var byOutputIndex = new Dictionary<PageItem, int>();
        var position = 0;
        foreach (var page in livePages) byOutputIndex[page] = position++;

        var paths = new Dictionary<int, List<IReadOnlyList<int>>>();
        foreach (var replacement in replacements)
        {
            if (replacement.HideObjectPath is not { Count: > 0 } path) continue;
            if (!byOutputIndex.TryGetValue(replacement.Page, out var index)) continue;

            if (!paths.TryGetValue(index, out var list)) paths[index] = list = [];
            list.Add(path);
        }

        return PdfiumEngine.RemoveObjects(content, paths);
    }

    /// <summary>
    /// Produces the annotation as it should appear in the exported document: same geometry,
    /// but tagged with its destination page index and the rotation added since it was drawn.
    /// </summary>
    private static AnnotationSpec Retarget(AnnotationSpec source, int outputPageIndex)
    {
        var copy = new AnnotationSpec
        {
            Kind = source.Kind,
            PageIndex = outputPageIndex,
            Page = source.Page,
            PageExtraRotation = source.Page?.Rotation ?? 0,
            Left = source.Left,
            Top = source.Top,
            Right = source.Right,
            Bottom = source.Bottom,
            R = source.R,
            G = source.G,
            B = source.B,
            A = source.A,
            Contents = source.Contents,
            Author = source.Author,
        };

        copy.Quads.AddRange(source.Quads);
        copy.InkStrokes.AddRange(source.InkStrokes);
        return copy;
    }

    /// <summary>Exports straight to a file, creating the directory if needed.</summary>
    public static void ExportToFile(DocumentModel model, string path)
    {
        var bytes = Export(model);

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        File.WriteAllBytes(path, bytes);
    }
}
