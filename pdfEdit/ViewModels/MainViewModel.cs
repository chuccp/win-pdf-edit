using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using PdfEdit.Common;
using PdfEdit.Models;
using PdfEdit.Services;

namespace PdfEdit.ViewModels;

/// <summary>An annotation tool the user can arm. <see cref="Select"/> draws nothing.</summary>
public enum EditorTool
{
    Select,
    Highlight,
    Underline,
    StrikeOut,
    Rectangle,
    Ellipse,
    Ink,
    Note,

    /// <summary>
    /// Selects existing page content for in-place editing, rather than adding an annotation.
    /// This is the tool that modifies the PDF's own text and objects.
    /// </summary>
    EditContent,

    /// <summary>Places brand-new text on the page at the click point.</summary>
    InsertText,
}

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const double PageGap = 14;

    private readonly DocumentModel _model = new();
    private readonly Dictionary<PageViewModel, double> _renderedAt = [];
    private readonly HashSet<PageViewModel> _rendering = [];

    private PdfiumEngine? _engine;
    private RenderService? _renderer;

    public ObservableCollection<PageViewModel> Pages { get; } = [];
    public ObservableCollection<SearchHit> SearchResults { get; } = [];
    public ObservableCollection<OutlineEntry> OutlineItems { get; } = [];

    public DocumentModel Model => _model;

    // ------------------------------------------------------------------ state

    private string _filePath = string.Empty;
    public string FilePath
    {
        get => _filePath;
        private set { if (SetField(ref _filePath, value)) OnPropertyChanged(nameof(Title)); }
    }

    public string Title => string.IsNullOrEmpty(FilePath)
        ? "PDF 阅读编辑器"
        : $"{Path.GetFileName(FilePath)}{(_model.IsDirty ? " *" : "")} — PDF 阅读编辑器";

    private string _statusText = "打开一个 PDF 文件开始。";
    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    private double _zoom = 1.0;
    public double Zoom
    {
        get => _zoom;
        set
        {
            var clamped = Math.Clamp(Math.Round(value, 3), 0.1, 6.0);
            if (!SetField(ref _zoom, clamped)) return;
            OnPropertyChanged(nameof(ZoomPercent));
            ApplyZoomToPages();
        }
    }

    public string ZoomPercent => $"{Zoom * 100:0}%";

    private int _currentPageIndex;
    public int CurrentPageIndex
    {
        get => _currentPageIndex;
        set
        {
            if (!SetField(ref _currentPageIndex, value)) return;
            OnPropertyChanged(nameof(PageLabel));
        }
    }

    public int PageCount => Pages.Count;

    public string PageLabel => Pages.Count == 0
        ? "— / —"
        : $"{CurrentPageIndex + 1} / {Pages.Count}";

    private EditorTool _currentTool = EditorTool.Select;
    public EditorTool CurrentTool
    {
        get => _currentTool;
        set
        {
            if (!SetField(ref _currentTool, value)) return;
            OnPropertyChanged(nameof(ShowEditableBoxes));
            RefreshEditableBoxes();

            StatusText = value switch
            {
                EditorTool.Select => "选择模式：点击查看批注，右键删除。",
                EditorTool.EditContent => "正文编辑：带框的文字点一下即可原地修改，双击任意文字也行。",
                EditorTool.InsertText => "插入文字：在页面上点击要放置的位置。",
                _ => $"绘图模式：在页面上拖拽以添加{DescribeTool(value)}。",
            };
        }
    }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set => SetField(ref _searchText, value);
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set => SetField(ref _isBusy, value);
    }

    private bool _hasDocument;
    public bool HasDocument
    {
        get => _hasDocument;
        private set
        {
            if (SetField(ref _hasDocument, value)) OnPropertyChanged(nameof(HasNoDocument));
        }
    }

    /// <summary>Drives the empty-state panel. Explicit because XAML cannot chain converters.</summary>
    public bool HasNoDocument => !HasDocument;

    /// <summary>True when there are edits that have not been written to disk.</summary>
    public bool IsDirty => _model.IsDirty;

    /// <summary>Drives the "no bookmarks" hint. Kept as its own flag because XAML cannot chain converters.</summary>
    public bool HasNoOutline => OutlineItems.Count == 0;

    private bool _isEditMode;
    /// <summary>
    /// Gates every editing affordance — annotation tools, page operations and overlays.
    /// Reading is the default so the toolbar stays uncluttered until the user asks to edit.
    /// </summary>
    public bool IsEditMode
    {
        get => _isEditMode;
        set
        {
            if (!SetField(ref _isEditMode, value)) return;
            OnPropertyChanged(nameof(IsReadMode));
            OnPropertyChanged(nameof(ModeLabel));
            ApplyMode();
        }
    }

    /// <summary>Explicit inverse: XAML cannot chain converters, so views bind this directly.</summary>
    public bool IsReadMode => !IsEditMode;

    public string ModeLabel => IsEditMode ? "编辑模式" : "阅读模式";

    private void ApplyMode()
    {
        OnPropertyChanged(nameof(ShowEditableBoxes));
        RefreshEditableBoxes();

        if (IsEditMode)
        {
            StatusText = "编辑模式：用「正文」工具点带框的文字可直接改写。";
            return;
        }

        // Leaving edit mode must drop any armed tool, or the next click would still draw.
        CurrentTool = EditorTool.Select;
        StatusText = "阅读模式：可浏览、缩放、搜索。点击「编辑模式」开始批注。";
    }

    // ------------------------------------------------------------------ commands

    public RelayCommand OpenCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand SaveAsCommand { get; }
    public RelayCommand ZoomInCommand { get; }
    public RelayCommand ZoomOutCommand { get; }
    public RelayCommand ZoomResetCommand { get; }
    public RelayCommand ZoomFitWidthCommand { get; }
    public RelayCommand ZoomFitPageCommand { get; }
    public RelayCommand NextPageCommand { get; }
    public RelayCommand PreviousPageCommand { get; }
    public RelayCommand<string> GoToPageNumberCommand { get; }
    public RelayCommand SearchCommand { get; }
    public RelayCommand ClearSearchCommand { get; }
    public RelayCommand<SearchHit> GoToHitCommand { get; }
    public RelayCommand<OutlineEntry> GoToOutlineCommand { get; }
    public RelayCommand<PageViewModel> GoToPageCommand { get; }
    public RelayCommand RotateClockwiseCommand { get; }
    public RelayCommand RotateCounterClockwiseCommand { get; }
    public RelayCommand DeletePageCommand { get; }
    public RelayCommand RestorePageCommand { get; }
    public RelayCommand MovePageUpCommand { get; }
    public RelayCommand MovePageDownCommand { get; }
    public RelayCommand ClearAnnotationsCommand { get; }
    public RelayCommand AddWatermarkCommand { get; }
    public RelayCommand AddPageNumbersCommand { get; }
    public RelayCommand ExportImagesCommand { get; }
    public RelayCommand<AnnotationSpec> DeleteAnnotationCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand RedoCommand { get; }
    public RelayCommand RunOcrCommand { get; }
    public RelayCommand RunOcrAllCommand { get; }
    public RelayCommand ApplyContentEditCommand { get; }
    public RelayCommand DeleteContentObjectCommand { get; }
    public RelayCommand CancelContentEditCommand { get; }

    public MainViewModel()
    {
        OpenCommand = new RelayCommand(Open);
        SaveCommand = new RelayCommand(() => Save(FilePath), () => HasDocument);
        SaveAsCommand = new RelayCommand(SaveAs, () => HasDocument);
        ZoomInCommand = new RelayCommand(() => Zoom *= 1.25);
        ZoomOutCommand = new RelayCommand(() => Zoom /= 1.25);
        ZoomResetCommand = new RelayCommand(() => Zoom = 1.0);
        ZoomFitWidthCommand = new RelayCommand(FitWidth, () => HasDocument);
        ZoomFitPageCommand = new RelayCommand(FitPage, () => HasDocument);
        NextPageCommand = new RelayCommand(() => StepPage(1), () => HasDocument);
        PreviousPageCommand = new RelayCommand(() => StepPage(-1), () => HasDocument);
        GoToPageNumberCommand = new RelayCommand<string>(GoToPageNumber, _ => HasDocument);
        SearchCommand = new RelayCommand(RunSearch, () => HasDocument && SearchText.Length > 0);
        ClearSearchCommand = new RelayCommand(() =>
        {
            SearchResults.Clear();
            RefreshSearchHighlights();
            StatusText = "已清除搜索结果。";
        });
        GoToHitCommand = new RelayCommand<SearchHit>(GoToHit);
        GoToOutlineCommand = new RelayCommand<OutlineEntry>(GoToOutline);
        GoToPageCommand = new RelayCommand<PageViewModel>(GoToPage);
        RotateClockwiseCommand = new RelayCommand(() => RotateCurrent(1), () => HasDocument);
        RotateCounterClockwiseCommand = new RelayCommand(() => RotateCurrent(-1), () => HasDocument);
        DeletePageCommand = new RelayCommand(() => SetCurrentDeleted(true), () => HasDocument);
        RestorePageCommand = new RelayCommand(() => SetCurrentDeleted(false), () => HasDocument);
        MovePageUpCommand = new RelayCommand(() => MoveCurrent(-1), () => HasDocument);
        MovePageDownCommand = new RelayCommand(() => MoveCurrent(1), () => HasDocument);
        ClearAnnotationsCommand = new RelayCommand(ClearAnnotations, () => _model.Annotations.Count > 0);
        AddWatermarkCommand = new RelayCommand(AddWatermark, () => HasDocument);
        AddPageNumbersCommand = new RelayCommand(AddPageNumbers, () => HasDocument);
        ExportImagesCommand = new RelayCommand(ExportImages, () => HasDocument);
        DeleteAnnotationCommand = new RelayCommand<AnnotationSpec>(RemoveAnnotation);
        UndoCommand = new RelayCommand(Undo, () => _undoStack.Count > 0);
        RedoCommand = new RelayCommand(Redo, () => _redoStack.Count > 0);
        RunOcrCommand = new RelayCommand(() => _ = RunOcrAsync(singlePage: true), () => HasDocument);
        RunOcrAllCommand = new RelayCommand(() => _ = RunOcrAsync(singlePage: false), () => HasDocument);
        // Gated on CanEditSelection, not on SelectedObject: recognised words and inserted
        // replacements are editable too, and keying off the object alone silently disabled
        // the command for both.
        ApplyContentEditCommand = new RelayCommand(ApplyContentEdit, () => CanEditSelection);
        DeleteContentObjectCommand = new RelayCommand(DeleteContentObject,
            () => SelectedObject is not null || _selectedOcrWord is not null);
        CancelContentEditCommand = new RelayCommand(ClearObjectSelection);
    }

    // ------------------------------------------------------------------ undo / redo

    private readonly Stack<IUndoableEdit> _undoStack = new();
    private readonly Stack<IUndoableEdit> _redoStack = new();

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    public string UndoLabel => _undoStack.Count > 0 ? $"撤销 {_undoStack.Peek().Label}" : "撤销";
    public string RedoLabel => _redoStack.Count > 0 ? $"重做 {_redoStack.Peek().Label}" : "重做";

    private sealed class LambdaEdit(string label, Action undo, Action redo) : IUndoableEdit
    {
        public string Label { get; } = label;
        public void Undo() => undo();
        public void Redo() => redo();
    }

    /// <summary>
    /// Records a reversible edit. Every mutation that changes the document goes through here,
    /// so nothing the user does is unrecoverable.
    /// </summary>
    private void PushEdit(string label, Action undo, Action redo)
    {
        _undoStack.Push(new LambdaEdit(label, undo, redo));

        // A fresh edit invalidates anything that was undone — standard linear history.
        _redoStack.Clear();

        MarkDirty();
        RefreshUndoState();
    }

    private void Undo()
    {
        if (_undoStack.Count == 0) return;

        var edit = _undoStack.Pop();
        edit.Undo();
        _redoStack.Push(edit);

        MarkDirty();
        RefreshUndoState();
        StatusText = $"已撤销：{edit.Label}";
    }

    private void Redo()
    {
        if (_redoStack.Count == 0) return;

        var edit = _redoStack.Pop();
        edit.Redo();
        _undoStack.Push(edit);

        MarkDirty();
        RefreshUndoState();
        StatusText = $"已重做：{edit.Label}";
    }

    private void RefreshUndoState()
    {
        UndoCommand.RaiseCanExecuteChanged();
        RedoCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(UndoLabel));
        OnPropertyChanged(nameof(RedoLabel));
    }

    private void ClearHistory()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        RefreshUndoState();
    }

    /// <summary>Applies a rotation and refreshes everything that depends on it.</summary>
    private void RotatePage(PageViewModel page, int quarterTurns)
    {
        page.Item.Rotation += quarterTurns;
        _renderedAt.Remove(page);
        RebuildPage(page);

        // OCR boxes describe the page as it looked when recognised, so a turn invalidates them.
        if (_model.TextLayers.Remove(page.Item))
            OnPropertyChanged(nameof(CurrentPageHasTextLayer));

        RefreshEditableBoxes();
    }

    private void SetPageDeleted(PageViewModel page, bool deleted)
    {
        page.Item.IsDeleted = deleted;
        _model.Renumber();
        foreach (var p in Pages) p.RefreshCaption();
        RefreshEditableBoxes();
    }

    private void MovePage(int from, int to)
    {
        Pages.Move(from, to);
        _model.Pages.Move(from, to);
        _model.Renumber();
        foreach (var p in Pages) p.RefreshCaption();

        CurrentPageIndex = to;
        ScrollToPage?.Invoke(Pages[to]);
    }

    /// <summary>Adds a text replacement as one undoable step.</summary>
    private void AddReplacement(TextReplacement replacement, string label)
    {
        _model.TextReplacements.Add(replacement);
        _contentEdited = true;

        PushEdit(label,
            undo: () => _model.TextReplacements.Remove(replacement),
            redo: () => _model.TextReplacements.Add(replacement));

        RefreshAfterContentEdit(
            Pages.FirstOrDefault(p => p.Item == replacement.Page) ?? CurrentPage!,
            "已记录改写。");
    }

    // ------------------------------------------------------------------ OCR

    /// <summary>
    /// Recognises text on scanned pages and queues it as a searchable layer.
    ///
    /// The layer is not written into the live document: PDFium would embed a whole CJK font
    /// (~4.8 MB), while the export stage only adds ~28 KB. So it lands in the saved file, and
    /// becomes searchable here once the document is reloaded after saving.
    /// </summary>
    private async Task RunOcrAsync(bool singlePage)
    {
        if (_engine is null || _renderer is null || !HasDocument) return;

        if (!OcrService.IsAvailable)
        {
            MessageBox.Show(
                "系统未安装可用的 OCR 语言包。\n\n" +
                "请在「设置 → 时间和语言 → 语言和区域」中添加语言，并勾选「光学字符识别」。",
                "OCR 不可用", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var targets = singlePage && CurrentPage is { } p
            ? [p]
            : Pages.Where(x => !x.Item.IsDeleted).ToList();

        if (targets.Count == 0) return;

        IsBusy = true;
        var totalWords = 0;
        var done = 0;

        try
        {
            foreach (var page in targets)
            {
                StatusText = $"正在识别第 {done + 1} / {targets.Count} 页…";
                done++;

                // OCR wants a high-resolution raster; the cache key includes the scale so this
                // does not disturb the on-screen render.
                var scale = OcrService.SafeScaleFor(page.WidthInPoints, page.HeightInPoints) ?? 2.0;
                var source = page.SourceIndex;
                var rotation = page.Item.Rotation;

                var words = await Task.Run(async () =>
                {
                    var bitmap = _renderer.Get(source, scale, rotation, false);
                    return await OcrService.RecognizeAsync(bitmap, scale);
                });

                if (words.Count == 0)
                {
                    _model.TextLayers.Remove(page.Item);
                    continue;
                }

                var layer = new List<OcrWord>(words);
                _model.TextLayers[page.Item] = layer;
                totalWords += words.Count;
            }

            if (totalWords > 0)
            {
                MarkDirty();
                ShowOcrRegions = true;
                RefreshOcrOverlays();
                OnPropertyChanged(nameof(CurrentPageHasTextLayer));

                StatusText = $"已识别 {totalWords} 个词（绿色框为识别区域），现在即可搜索。" +
                             "用「正文」工具点击这些文字可直接改写。";
            }
            else
            {
                StatusText = "未识别出任何文字。扫描件分辨率过低或不是文本页。";
            }
        }
        catch (Exception ex)
        {
            StatusText = "OCR 失败。";
            MessageBox.Show($"OCR 识别失败：\n{ex.Message}", "OCR 失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
            RefreshCommands();
        }
    }

    /// <summary>Whether the current page already carries a pending OCR layer.</summary>
    public bool CurrentPageHasTextLayer => CurrentPage is { } p && _model.TextLayers.ContainsKey(p.Item);

    private bool _showOcrRegions = true;
    /// <summary>
    /// Draws the recognised word boxes over the page. On by default: an OCR run that changes
    /// nothing on screen reads as a failure, even when it worked.
    /// </summary>
    public bool ShowOcrRegions
    {
        get => _showOcrRegions;
        set => SetField(ref _showOcrRegions, value);
    }

    /// <summary>
    /// True while the content-edit tool is armed; drives the outline over every editable region.
    /// </summary>
    public bool ShowEditableBoxes => IsEditMode && CurrentTool == EditorTool.EditContent;

    /// <summary>
    /// Collects the editable text regions of a page: recognised words on a scan, real text
    /// objects otherwise. Scanned pages have no text objects, so OCR is what makes them editable.
    /// </summary>
    private void RefreshEditableBoxes()
    {
        foreach (var page in Pages)
        {
            if (!ShowEditableBoxes || page.Item.IsDeleted)
            {
                page.EditableBoxes.Clear();
                continue;
            }

            if (_model.TextLayers.TryGetValue(page.Item, out var ocrWords))
            {
                page.RebuildEditableBoxes(ocrWords.Select(w => w.Bounds));
                continue;
            }

            if (_engine is null) continue;

            try
            {
                // The scan happens per page on demand; enumerating the object tree of a large
                // document up front would stall the UI for no benefit.
                var regions = _engine.GetPageObjects(page.SourceIndex)
                    .Where(o => o.IsText && !string.IsNullOrWhiteSpace(o.Text))
                    .Select(o => o.Bounds);

                page.RebuildEditableBoxes(regions);
            }
            catch
            {
                page.EditableBoxes.Clear();
            }
        }
    }

    /// <summary>Recomputes every page's OCR overlay, e.g. after recognition or a zoom change.</summary>
    private void RefreshOcrOverlays()
    {
        foreach (var page in Pages)
        {
            page.RebuildOcrBoxes(_model.TextLayers.TryGetValue(page.Item, out var words)
                ? words
                : []);
        }
    }

    // ------------------------------------------------------------------ content editing

    private PageObjectInfo? _selectedObject;
    /// <summary>The page object currently picked for in-place editing, if any.</summary>
    public PageObjectInfo? SelectedObject
    {
        get => _selectedObject;
        private set
        {
            if (!SetField(ref _selectedObject, value)) return;

            // Everything derived from the selection has to be announced here rather than at each
            // call site. When these were only raised on some paths, the inline editor stayed
            // disabled for ordinary page text and typing into it did nothing.
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectionLabel));
            OnPropertyChanged(nameof(CanEditSelection));
            OnPropertyChanged(nameof(SelectedBounds));
            OnPropertyChanged(nameof(IsOcrSelection));
            OnPropertyChanged(nameof(IsReplacementSelection));

            ApplyContentEditCommand.RaiseCanExecuteChanged();
            DeleteContentObjectCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Explicit inverse for XAML binding; converters cannot be chained.</summary>
    public bool HasSelection =>
        SelectedObject is not null || _selectedOcrWord is not null || _editingReplacement is not null;

    /// <summary>True when the current selection is a word read off a scan rather than real PDF text.</summary>
    public bool IsOcrSelection => _selectedOcrWord is not null;

    /// <summary>A replacement being composed or revised, e.g. freshly inserted text.</summary>
    private TextReplacement? _editingReplacement;
    public bool IsReplacementSelection => _editingReplacement is not null;

    /// <summary>Font size applied to the edit. Changing it reflows a replacement immediately.</summary>
    public double EditFontSize
    {
        get => _editFontSize;
        set
        {
            if (!SetField(ref _editFontSize, Math.Clamp(value, 4, 200))) return;
            ApplyFormattingToSelection();
        }
    }
    private double _editFontSize = 12;

    /// <summary>Hex colour applied to the edit, e.g. "#C81E1E".</summary>
    public string EditColour
    {
        get => _editColour;
        set
        {
            if (!SetField(ref _editColour, value)) return;
            OnPropertyChanged(nameof(EditColourBrush));
            ApplyFormattingToSelection();
        }
    }
    private string _editColour = "#141414";

    /// <summary>Swatch shown next to the colour box.</summary>
    public System.Windows.Media.Brush EditColourBrush
    {
        get
        {
            try
            {
                return new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter
                        .ConvertFromString(EditColour));
            }
            catch
            {
                return System.Windows.Media.Brushes.Black;
            }
        }
    }

    /// <summary>Pushes the current size and colour onto whatever is being edited.</summary>
    private void ApplyFormattingToSelection()
    {
        if (_editingReplacement is null) return;

        _editingReplacement.FontSize = EditFontSize;
        ParseColour(EditColour, out var r, out var g, out var b);
        _editingReplacement.R = r;
        _editingReplacement.G = g;
        _editingReplacement.B = b;

        RefreshPageOf(_editingReplacement);
    }

    private static void ParseColour(string hex, out byte r, out byte g, out byte b)
    {
        r = g = b = 20;
        try
        {
            var colour = (System.Windows.Media.Color)System.Windows.Media.ColorConverter
                .ConvertFromString(hex);
            r = colour.R; g = colour.G; b = colour.B;
        }
        catch
        {
            // Keep the default rather than failing the edit over a malformed colour.
        }
    }

    /// <summary>Number of pages the document holds, for status text and bounds checks.</summary>
    private void RefreshPageOf(TextReplacement replacement)
    {
        var owner = Pages.FirstOrDefault(p => p.Item == replacement.Page);
        if (owner is not null) RefreshAfterContentEdit(owner, "已更新格式。");
    }

    /// <summary>Area of whatever is selected, in display space, for drawing the highlight box.</summary>
    public TextRect? SelectedBounds =>
        _editingReplacement?.Bounds ?? _selectedOcrWord?.Bounds ?? SelectedObject?.Bounds;

    /// <summary>
    /// Whether the selected thing can be retyped. Recognised words always can; a page object
    /// only when it is actually text.
    /// </summary>
    public bool CanEditSelection =>
        _editingReplacement is not null ||
        _selectedOcrWord is not null ||
        SelectedObject?.IsText == true;

    public string SelectionLabel => _editingReplacement is { } repl
        ? (repl.HideObjectPath is null ? "新插入的文字" : "替换文字")
        : _selectedOcrWord is { } w
            ? $"识别文字「{w.Text}」"
            : SelectedObject is { } o
                ? $"{o.TypeLabel}对象 {o.FontName} {o.FontSize:0.#}pt"
                : string.Empty;

    private OcrWord? _selectedOcrWord;
    private PageViewModel? _selectedObjectPage;

    private string _contentEditText = string.Empty;
    public string ContentEditText
    {
        get => _contentEditText;
        set => SetField(ref _contentEditText, value);
    }

    private string _contentEditHint = string.Empty;
    /// <summary>Explains a rejected edit — normally the embedded font subset lacks the glyphs.</summary>
    public string ContentEditHint
    {
        get => _contentEditHint;
        private set
        {
            if (SetField(ref _contentEditHint, value)) OnPropertyChanged(nameof(HasContentEditHint));
        }
    }

    public bool HasContentEditHint => ContentEditHint.Length > 0;

    /// <summary>Set once any in-place content edit has been applied.</summary>
    private bool _contentEdited;

    /// <summary>
    /// Hit-tests page content and selects the object under the pointer. Text objects are
    /// preferred and, of those, the smallest — a click inside a big text block should pick the
    /// line under the cursor rather than the whole block.
    /// </summary>
    public void SelectObjectAt(PageViewModel page, Point pagePoint)
    {
        if (_engine is null) return;

        try
        {
            // A scanned page has no text objects at all, so recognised words are the only
            // editable target. Prefer them when the page has an OCR layer.
            if (_model.TextLayers.TryGetValue(page.Item, out var layerWords))
            {
                var word = layerWords
                    .Where(w => pagePoint.X >= w.Bounds.Left && pagePoint.X <= w.Bounds.Right &&
                                pagePoint.Y >= w.Bounds.Top && pagePoint.Y <= w.Bounds.Bottom)
                    .OrderBy(w => w.Bounds.Width * w.Bounds.Height)
                    .FirstOrDefault();

                if (word is not null)
                {
                    _selectedOcrWord = word;
                    _selectedObjectPage = page;
                    SelectedObject = null; // an OCR word is not a page object
                    ContentEditText = word.Text;
                    ContentEditHint = string.Empty;
                    ShowSelectionOn(page);
                    OnPropertyChanged(nameof(IsOcrSelection));
                    OnPropertyChanged(nameof(SelectionLabel));

                    StatusText = $"已选中识别出的文字「{word.Text}」。可直接改写，保存时覆盖原位置。";
                    ApplyContentEditCommand.RaiseCanExecuteChanged();
                    DeleteContentObjectCommand.RaiseCanExecuteChanged();
                    return;
                }
            }

            var objects = _engine.GetPageObjects(page.SourceIndex);

            var hit = objects
                .Where(o => !o.IsForm && o.Contains(pagePoint.X, pagePoint.Y))
                .OrderBy(o => o.IsText ? 0 : 1)
                .ThenBy(o => o.Bounds.Width * o.Bounds.Height)
                .FirstOrDefault();

            if (hit is null)
            {
                ClearObjectSelection();
                StatusText = "此处没有可编辑的对象。试试点击文字。";
                return;
            }

            SelectedObject = hit;
            _selectedObjectPage = page;
            ContentEditText = hit.Text;
            ContentEditHint = string.Empty;
            ShowSelectionOn(page);

            StatusText = $"已选中{hit.TypeLabel}对象" +
                         (hit.FontName.Length > 0 ? $"（字体 {hit.FontName}，{hit.FontSize:0.#}pt）" : "");
        }
        catch (Exception ex)
        {
            StatusText = $"读取页面对象失败：{ex.Message}";
        }
    }

    /// <summary>
    /// Moves the outline onto <paramref name="page"/> and updates it for the current selection.
    ///
    /// This lives here rather than in the window's code-behind: when it was only called from the
    /// mouse handlers, selecting anything by any other route left the outline showing the old
    /// geometry, or none at all.
    /// </summary>
    private void ShowSelectionOn(PageViewModel page)
    {
        foreach (var other in Pages) other.SelectionBounds = null;

        if (SelectedBounds is not { } bounds) return;

        var zoom = Zoom;
        page.SelectionBounds = new Rect(
            bounds.Left * zoom, bounds.Top * zoom,
            Math.Max(1, (bounds.Right - bounds.Left) * zoom),
            Math.Max(1, (bounds.Bottom - bounds.Top) * zoom));
    }

    public void ClearObjectSelection()
    {
        SelectedObject = null;
        _selectedOcrWord = null;
        _editingReplacement = null;

        foreach (var p in Pages) p.SelectionBounds = null;
        _selectedObjectPage = null;
        ContentEditText = string.Empty;
        ContentEditHint = string.Empty;
        OnPropertyChanged(nameof(IsOcrSelection));
        OnPropertyChanged(nameof(IsReplacementSelection));
        OnPropertyChanged(nameof(SelectionLabel));
        OnPropertyChanged(nameof(HasSelection));
    }

    /// <summary>
    /// Picks the colour to paint over a scanned word with, by sampling the rendered page just
    /// outside the word's box where the original text is unlikely to reach. Falls back to white,
    /// which is right for most scans.
    /// </summary>
    private static (byte R, byte G, byte B) SampleBackground(PageViewModel page, TextRect box)
    {
        if (page.Image is not { } image) return (255, 255, 255);

        var zoom = page.Zoom <= 0 ? 1 : page.Zoom;

        // A band sitting just above the words, sampled across their width.
        var sampleY = (int)Math.Round(box.Top * zoom) - 3;
        var left = (int)Math.Round(box.Left * zoom);
        var right = (int)Math.Round(box.Right * zoom);

        if (sampleY < 0 || sampleY >= image.PixelHeight || right <= left) return (255, 255, 255);

        var width = Math.Min(right - left, 64);
        if (width <= 0) return (255, 255, 255);

        try
        {
            var pixels = new byte[width * 4];
            image.CopyPixels(new Int32Rect(left, sampleY, width, 1), pixels, width * 4, 0);

            // Median-ish: average is thrown off by a speck of ink or a stamp edge.
            var rs = new List<byte>(width);
            var gs = new List<byte>(width);
            var bs = new List<byte>(width);
            for (var i = 0; i < width; i++)
            {
                rs.Add(pixels[i * 4]);
                gs.Add(pixels[i * 4 + 1]);
                bs.Add(pixels[i * 4 + 2]);
            }

            rs.Sort(); gs.Sort(); bs.Sort();
            return (rs[width / 2], gs[width / 2], bs[width / 2]);
        }
        catch
        {
            return (255, 255, 255);
        }
    }

    private void ApplyContentEdit()
    {
        if (_engine is null || _selectedObjectPage is not { } page) return;

        // Revising a replacement (an inserted string, or one already swapped for a substitute).
        if (_editingReplacement is { } editing)
        {
            editing.Text = ContentEditText;
            editing.FontSize = EditFontSize;
            ParseColour(EditColour, out var er, out var eg, out var eb);
            editing.R = er; editing.G = eg; editing.B = eb;

            // Widen the box so longer text is not squeezed by the shrink-to-fit at export.
            var width = Math.Max(editing.Bounds.Right - editing.Bounds.Left,
                                 ContentEditText.Length * EditFontSize * 0.95);
            editing.Bounds = new TextRect(editing.Bounds.Left, editing.Bounds.Top,
                editing.Bounds.Left + width, editing.Bounds.Top + EditFontSize * 1.4);

            ClearObjectSelection();
            RefreshAfterContentEdit(page, "已更新文字。");
            return;
        }

        // Recognised text on a scan: there is nothing to rewrite, only pixels. Paint over the
        // original area with the surrounding colour and draw the new text in its place.
        if (_selectedOcrWord is { } ocrWord)
        {
            if (string.Equals(ContentEditText, ocrWord.Text, StringComparison.Ordinal))
            {
                StatusText = "内容没有变化。";
                return;
            }

            var (bgR, bgG, bgB) = SampleBackground(page, ocrWord.Bounds);

            _model.TextReplacements.Add(new TextReplacement
            {
                Page = page.Item,
                Bounds = ocrWord.Bounds,
                Text = ContentEditText,
                FontSize = Math.Max(6, ocrWord.Bounds.Bottom - ocrWord.Bounds.Top) * 0.88,
                FontFamily = PdfExportService.TextLayerFont,
                RedactBackground = true,
                BackgroundR = bgR,
                BackgroundG = bgG,
                BackgroundB = bgB,
                R = 20, G = 20, B = 20,
            });

            _contentEdited = true;
            MarkDirty();
            ClearObjectSelection();
            RefreshAfterContentEdit(page, $"已改写识别文字「{ocrWord.Text}」→「{ContentEditText}」。");
            return;
        }

        if (SelectedObject is not { } target) return;

        if (!target.IsText)
        {
            ContentEditHint = $"选中的是{target.TypeLabel}对象，只能编辑文字内容。";
            return;
        }

        if (string.Equals(ContentEditText, target.Text, StringComparison.Ordinal))
        {
            StatusText = "内容没有变化。";
            return;
        }

        try
        {
            var outcome = _engine.SetObjectText(page.SourceIndex, target.Path, ContentEditText);

            switch (outcome)
            {
                case TextEditOutcome.Applied:
                    _contentEdited = true;
                    MarkDirty();
                    RefreshAfterContentEdit(page, "正文已改写。");
                    break;

                case TextEditOutcome.GlyphsUnavailable:
                    // The embedded font is a subset and has no glyph for the new characters.
                    // Mainstream editors do not refuse here — they substitute a font. Do the
                    // same: drop the original object and let the export stage redraw the text.
                    var replacement = new TextReplacement
                    {
                        Page = page.Item,
                        Bounds = target.Bounds,
                        Text = ContentEditText,
                        FontSize = target.FontSize > 1 ? target.FontSize : 12,
                        FontFamily = PdfExportService.TextLayerFont,
                        HideObjectPath = target.Path,
                    };

                    AddReplacement(replacement, $"改写「{target.Text}」");
                    ClearObjectSelection();
                    StatusText = $"原字体「{target.FontName}」缺少新字符的字形，" +
                                 "已改为保存时用替代字体重绘。";
                    break;

                case TextEditOutcome.NotATextObject:
                    ContentEditHint = "该对象不是文字。";
                    break;

                default:
                    ContentEditHint = "找不到该对象，页面可能已被改动。";
                    break;
            }
        }
        catch (Exception ex)
        {
            ContentEditHint = $"改写失败：{ex.Message}";
        }
    }

    private void DeleteContentObject()
    {
        if (_engine is null || SelectedObject is not { } target || _selectedObjectPage is not { } page)
            return;

        try
        {
            if (!_engine.RemoveObject(page.SourceIndex, target.Path))
            {
                ContentEditHint = "删除失败，该对象可能位于嵌套容器中。";
                return;
            }

            _contentEdited = true;
            MarkDirty();
            ClearObjectSelection();
            RefreshAfterContentEdit(page, $"已删除该{target.TypeLabel}对象。");
        }
        catch (Exception ex)
        {
            ContentEditHint = $"删除失败：{ex.Message}";
        }
    }

    /// <summary>Content changed under the renderer, so its cached bitmap for that page is stale.</summary>
    private void RefreshAfterContentEdit(PageViewModel page, string message)
    {
        _renderer?.InvalidatePage(page.SourceIndex);
        _renderedAt.Remove(page);
        QueueRender(page);

        // The thumbnail is stale too; re-render just that one.
        _ = RefreshThumbnailAsync(page);

        StatusText = message + "保存后写入 PDF。";
    }

    private async Task RefreshThumbnailAsync(PageViewModel page)
    {
        if (_renderer is null) return;
        var renderer = _renderer;

        try
        {
            const double target = 110.0;
            var scale = target / Math.Max(1, page.WidthInPoints);
            var bitmap = await Task.Run(() =>
                renderer.Get(page.SourceIndex, scale, page.Item.Rotation, true));

            page.Thumbnail = bitmap;
        }
        catch
        {
            // A stale thumbnail is not worth surfacing.
        }
    }

    /// <summary>Raised when the view should scroll a page into view.</summary>
    public event Action<PageViewModel>? ScrollToPage;

    // ------------------------------------------------------------------ document

    public void OpenFile(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            LoadDocument(bytes, path);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法打开文件：\n{ex.Message}", "打开失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Open()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "打开 PDF",
            Filter = "PDF 文件 (*.pdf)|*.pdf|所有文件 (*.*)|*.*",
        };

        if (dialog.ShowDialog() == true) OpenFile(dialog.FileName);
    }

    private void LoadDocument(byte[] bytes, string path)
    {
        _renderer?.Dispose();
        _engine?.Dispose();

        var engine = PdfiumEngine.Open(bytes, path);
        var renderer = new RenderService(engine);

        _engine = engine;
        _renderer = renderer;
        _renderedAt.Clear();
        _rendering.Clear();
        _contentEdited = false;
        ClearObjectSelection();
        ClearHistory();

        _model.Load(bytes, path);

        Pages.Clear();
        SearchResults.Clear();
        OutlineItems.Clear();

        for (var i = 0; i < engine.PageCount; i++)
        {
            var size = engine.GetPageSize(i);
            var item = new PageItem { SourceIndex = i };
            _model.Pages.Add(item);
            Pages.Add(new PageViewModel(item, size.Width, size.Height) { Zoom = Zoom });
        }

        _model.Renumber();

        foreach (var entry in engine.GetOutline())
            OutlineItems.Add(entry);
        OnPropertyChanged(nameof(HasNoOutline));

        FilePath = path;
        HasDocument = true;
        CurrentPageIndex = 0;
        IsBusy = false;

        ApplyZoomToPages();
        StatusText = $"已打开 {engine.PageCount} 页 · {bytes.Length / 1024.0:0.#} KB" +
                     (OutlineItems.Count > 0 ? $" · 目录 {OutlineItems.Count} 项" : "");

        RefreshCommands();
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(PageLabel));
        OnPropertyChanged(nameof(PageCount));

        // Thumbnails are cheap at this size and let the sidebar fill in immediately.
        _ = RenderThumbnailsAsync();
    }

    private void SaveAs()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "另存为",
            Filter = "PDF 文件 (*.pdf)|*.pdf",
            FileName = string.IsNullOrEmpty(FilePath)
                ? "output.pdf"
                : Path.GetFileNameWithoutExtension(FilePath) + "_edited.pdf",
        };

        if (dialog.ShowDialog() == true) Save(dialog.FileName);
    }

    private void Save(string path)
    {
        if (!HasDocument) return;

        try
        {
            IsBusy = true;
            StatusText = "正在导出…";

            // In-place content edits live in the PDFium document, not in the source bytes, so
            // serialise them first — otherwise the exporter would rebuild from the original
            // content and silently discard every text edit.
            _model.EditedBytes = _contentEdited ? _engine?.Save() : null;

            var bytes = DocumentExporter.Export(_model);
            File.WriteAllBytes(path, bytes);

            IsBusy = false;
            StatusText = $"已保存到 {path}（{bytes.Length / 1024.0:0.#} KB）";

            // Re-open the result so the annotations just baked into the file are not also
            // drawn from the model — otherwise they would appear twice.
            LoadDocument(bytes, path);
        }
        catch (Exception ex)
        {
            IsBusy = false;
            StatusText = "保存失败。";
            MessageBox.Show($"保存失败：\n{ex.Message}", "保存失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ------------------------------------------------------------------ zoom & viewport

    private void ApplyZoomToPages()
    {
        foreach (var page in Pages)
        {
            page.Zoom = Zoom;
            page.RebuildAnnotations(AnnotationsFor(page));
            page.RebuildOcrBoxes(_model.TextLayers.TryGetValue(page.Item, out var words) ? words : []);
        }

        RefreshEditableBoxes();
    }

    private IEnumerable<AnnotationSpec> AnnotationsFor(PageViewModel page)
        => _model.Annotations.Where(a => a.Page == page.Item);

    /// <summary>Search boxes belonging to a page, keyed by its source index.</summary>
    private List<TextRect> SearchRectsFor(PageViewModel page)
        => SearchResults
            .Where(h => h.PageIndex == page.SourceIndex)
            .SelectMany(h => h.Rects)
            .ToList();

    /// <summary>Repaints the on-page highlight for the current result set.</summary>
    private void RefreshSearchHighlights()
    {
        foreach (var page in Pages)
            page.RebuildSearchHighlights(SearchRectsFor(page));
    }

    /// <summary>
    /// Called by the view on scroll/zoom. Renders pages near the viewport, and drops the
    /// bitmaps of pages that have scrolled far away to keep memory bounded.
    /// </summary>
    /// <summary>Last reported viewport width, used by the fit-to-width zoom.</summary>
    public double ViewportWidth { get; private set; }

    public void OnViewportChanged(double verticalOffset, double viewportHeight, double viewportWidth = 0)
    {
        if (viewportWidth > 0) ViewportWidth = viewportWidth;
        if (_engine is null || _renderer is null || Pages.Count == 0) return;

        var tops = new double[Pages.Count];
        var y = 0.0;
        for (var i = 0; i < Pages.Count; i++)
        {
            tops[i] = y;
            y += Pages[i].DisplayHeight + PageGap;
        }

        var viewTop = verticalOffset - viewportHeight;
        var viewBottom = verticalOffset + viewportHeight * 2;

        var firstVisible = 0;
        for (var i = 0; i < Pages.Count; i++)
        {
            if (tops[i] + Pages[i].DisplayHeight >= verticalOffset &&
                tops[i] <= verticalOffset + viewportHeight)
            {
                firstVisible = i;
                break;
            }
        }

        if (CurrentPageIndex != firstVisible)
        {
            CurrentPageIndex = firstVisible;
            OnPropertyChanged(nameof(PageLabel));
        }

        for (var i = 0; i < Pages.Count; i++)
        {
            var page = Pages[i];
            if (page.Item.IsDeleted) continue;

            var inRange = tops[i] + Pages[i].DisplayHeight >= viewTop && tops[i] <= viewBottom;
            if (!inRange) continue;

            // Re-render only when the zoom this bitmap was made at no longer matches.
            if (_renderedAt.TryGetValue(page, out var renderedAt) &&
                Math.Abs(renderedAt - Zoom) < 0.001) continue;

            QueueRender(page);
        }
    }

    private async void QueueRender(PageViewModel page)
    {
        if (_renderer is null || !_rendering.Add(page)) return;

        page.State = RenderState.Rendering;
        var scale = Zoom;
        var source = page.SourceIndex;
        var rotation = page.Item.Rotation;
        var renderer = _renderer;

        try
        {
            var bitmap = await Task.Run(() => renderer.Get(source, scale, rotation, true));

            page.Image = bitmap;
            page.State = RenderState.Ready;
            _renderedAt[page] = scale;
        }
        catch (Exception)
        {
            page.State = RenderState.Failed;
        }
        finally
        {
            _rendering.Remove(page);
        }
    }

    private async Task RenderThumbnailsAsync()
    {
        if (_renderer is null) return;
        var renderer = _renderer;
        var pages = Pages.ToList();

        await Task.Run(() =>
        {
            foreach (var page in pages)
            {
                try
                {
                    const double target = 110.0;
                    var scale = target / Math.Max(1, page.WidthInPoints);
                    var bitmap = renderer.Get(page.SourceIndex, scale, page.Item.Rotation, true);
                    Application.Current?.Dispatcher.Invoke(() => page.Thumbnail = bitmap);
                }
                catch
                {
                    // A thumbnail failing is not worth interrupting the user over.
                }
            }
        });
    }

    // ------------------------------------------------------------------ pages

    private PageViewModel? CurrentPage =>
        CurrentPageIndex >= 0 && CurrentPageIndex < Pages.Count ? Pages[CurrentPageIndex] : null;

    private void RotateCurrent(int quarterTurns)
    {
        if (CurrentPage is not { } page) return;

        var applied = quarterTurns;
        RotatePage(page, applied);

        PushEdit($"旋转第 {page.Item.DisplayNumber} 页",
            undo: () => RotatePage(page, -applied),
            redo: () => RotatePage(page, applied));

        // OCR boxes are recorded in the display space the page had when it was recognised, so
        // turning the page afterwards leaves them pointing at the wrong places.
        var droppedLayer = _model.TextLayers.Remove(page.Item);

        StatusText = $"第 {page.Item.DisplayNumber} 页旋转至 {page.Item.RotationDegrees}°。" +
                     (droppedLayer ? "该页的 OCR 文字层已作废，请重新识别。" : string.Empty);

        if (droppedLayer) OnPropertyChanged(nameof(CurrentPageHasTextLayer));
    }

    private void SetCurrentDeleted(bool deleted)
    {
        if (CurrentPage is not { } page) return;

        if (deleted && _model.LivePages.Count <= 1)
        {
            StatusText = "至少要保留一页。";
            return;
        }

        SetPageDeleted(page, deleted);

        PushEdit(deleted ? "删除页面" : "恢复页面",
            undo: () => SetPageDeleted(page, !deleted),
            redo: () => SetPageDeleted(page, deleted));

        StatusText = deleted
            ? $"已删除第 {CurrentPageIndex + 1} 页（导出时生效，可撤销）。"
            : "已恢复该页。";
    }

    /// <summary>Scales the widest page to the viewport, leaving a small margin.</summary>
    private void FitWidth()
    {
        if (Pages.Count == 0 || ViewportWidth <= 0) return;

        var widest = Pages.Where(p => !p.Item.IsDeleted).Max(p => p.WidthInPoints);
        if (widest <= 0) return;

        Zoom = Math.Max(0.1, (ViewportWidth - 32) / widest);
        StatusText = $"适合宽度 {ZoomPercent}";
    }

    /// <summary>Scales so a whole page fits vertically, which is how most people read a PDF.</summary>
    private void FitPage()
    {
        if (Pages.Count == 0 || ViewportWidth <= 0) return;

        var page = CurrentPage ?? Pages[0];
        var byWidth = (ViewportWidth - 32) / Math.Max(1, page.WidthInPoints);

        // ViewportHeight is not tracked, so derive it from the width and the page's own ratio
        // is not possible — fall back to fitting width and letting the page scroll.
        Zoom = Math.Max(0.1, Math.Min(byWidth, 2.0));
        StatusText = $"适合页面 {ZoomPercent}";
    }

    private void StepPage(int delta)
    {
        var target = CurrentPageIndex + delta;
        if (target < 0 || target >= Pages.Count) return;
        ScrollToPage?.Invoke(Pages[target]);
    }

    /// <summary>Jumps to a page from the status-bar box; ignores anything unparseable.</summary>
    private void GoToPageNumber(string? text)
    {
        if (!int.TryParse(text?.Trim(), out var number)) return;
        if (number < 1 || number > Pages.Count) return;

        ScrollToPage?.Invoke(Pages[number - 1]);
    }

    private void MoveCurrent(int delta)
    {
        var from = CurrentPageIndex;
        var to = from + delta;
        if (from < 0 || to < 0 || to >= Pages.Count) return;

        MovePage(from, to);

        PushEdit("移动页面",
            undo: () => MovePage(to, from),
            redo: () => MovePage(from, to));
    }

    private void GoToPage(PageViewModel? page)
    {
        if (page is not null) ScrollToPage?.Invoke(page);
    }

    // ------------------------------------------------------------------ annotations

    /// <summary>
    /// Places a new text box on the page. The text is held as a replacement and drawn at export,
    /// the same path an edited string takes, so inserting and editing behave identically.
    /// </summary>
    public void InsertTextAt(PageViewModel page, Point pagePoint)
    {
        var replacement = new TextReplacement
        {
            Page = page.Item,
            Bounds = new TextRect(pagePoint.X, pagePoint.Y,
                                 pagePoint.X + EditFontSize * 6, pagePoint.Y + EditFontSize * 1.4),
            Text = "新文字",
            FontSize = EditFontSize,
            FontFamily = PdfExportService.TextLayerFont,
        };
        ParseColour(EditColour, out var r, out var g, out var b);
        replacement.R = r; replacement.G = g; replacement.B = b;

        _model.TextReplacements.Add(replacement);
        _contentEdited = true;

        PushEdit("插入文字",
            undo: () => _model.TextReplacements.Remove(replacement),
            redo: () => _model.TextReplacements.Add(replacement));

        _editingReplacement = replacement;
        _selectedObjectPage = page;
        ContentEditText = replacement.Text;
        ContentEditHint = string.Empty;
        ShowSelectionOn(page);

        OnPropertyChanged(nameof(IsReplacementSelection));
        OnPropertyChanged(nameof(SelectionLabel));
        OnPropertyChanged(nameof(HasSelection));
        ApplyContentEditCommand.RaiseCanExecuteChanged();
        DeleteContentObjectCommand.RaiseCanExecuteChanged();

        RefreshAfterContentEdit(page, "已插入文字。");
        StatusText = "已插入文字，直接输入内容即可。";
    }

    /// <summary>
    /// Adds an annotation from a drag on a page. <paramref name="path"/> is the pointer trail in
    /// page points — a single point for a click, many for ink. Text-markup tools snap the run
    /// between the first and last point onto the underlying text.
    /// </summary>
    public AnnotationSpec? AddAnnotation(PageViewModel page, EditorTool tool, IReadOnlyList<Point> path)
    {
        // Select and EditContent are pointer modes, not annotation tools.
        if (_engine is null ||
            tool is EditorTool.Select or EditorTool.EditContent or EditorTool.InsertText ||
            path.Count == 0)
            return null;

        var dragStart = path[0];
        var dragEnd = path[^1];

        var kind = tool switch
        {
            EditorTool.Highlight => AnnotationKind.Highlight,
            EditorTool.Underline => AnnotationKind.Underline,
            EditorTool.StrikeOut => AnnotationKind.StrikeOut,
            EditorTool.Rectangle => AnnotationKind.Rectangle,
            EditorTool.Ellipse => AnnotationKind.Ellipse,
            EditorTool.Ink => AnnotationKind.Ink,
            EditorTool.Note => AnnotationKind.Note,
            _ => AnnotationKind.Highlight,
        };

        var spec = new AnnotationSpec
        {
            Kind = kind,
            PageIndex = page.SourceIndex,
            Page = page.Item,
            Author = Environment.UserName,
        };

        ApplyToolColour(spec, kind);

        if (kind == AnnotationKind.Ink)
        {
            // Ink carries its own polyline; the bounding box is derived from the points.
            var points = path.Select(p => (p.X, p.Y)).ToList();
            spec.InkStrokes.Add(new InkStroke(points));
            spec.SetBounds(FromPoints(points));
        }
        else if (kind is AnnotationKind.Highlight or AnnotationKind.Underline or AnnotationKind.StrikeOut)
        {
            // Snap to real text: hit-test both drag corners, then take everything between.
            var first = _engine.GetCharIndexAt(page.SourceIndex, dragStart.X, dragStart.Y);
            var last = _engine.GetCharIndexAt(page.SourceIndex, dragEnd.X, dragEnd.Y);

            if (first >= 0 && last >= 0)
            {
                var rects = _engine.GetTextRectsForRange(page.SourceIndex, first, last);
                if (rects.Count > 0)
                {
                    spec.Quads.AddRange(rects);
                    spec.SetBounds(Union(rects));
                }
            }
            else
            {
                // Dragged over blank space (or a scanned page) — fall back to the raw box.
                spec.SetBounds(Normalize(dragStart, dragEnd));
                spec.Quads.Add(spec.Bounds);
            }
        }
        else
        {
            var rect = Normalize(dragStart, dragEnd);

            // A plain click with no drag still has to leave something visible behind.
            if (rect.Width < 2 || rect.Height < 2)
                rect = new TextRect(rect.Left, rect.Top, rect.Left + 26, rect.Top + 26);

            spec.SetBounds(rect);
        }

        _model.Annotations.Add(spec);
        PushEdit($"添加{DescribeTool(tool)}",
            undo: () => { _model.Annotations.Remove(spec); RenderedPage(page); },
            redo: () => { _model.Annotations.Add(spec); RenderedPage(page); });
        RenderedPage(page);
        StatusText = $"已添加{DescribeTool(tool)}。保存后写入 PDF。";

        return spec;
    }

    private void RenderedPage(PageViewModel page)
    {
        page.RebuildAnnotations(AnnotationsFor(page));
        RefreshCommands();
    }

    private void RemoveAnnotation(AnnotationSpec? spec)
    {
        if (spec is null) return;

        _model.Annotations.Remove(spec);

        var owner = Pages.FirstOrDefault(p => p.Item == spec.Page);
        if (owner is not null) RenderedPage(owner);

        PushEdit("删除批注",
            undo: () => { _model.Annotations.Add(spec); if (owner is not null) RenderedPage(owner); },
            redo: () => { _model.Annotations.Remove(spec); if (owner is not null) RenderedPage(owner); });

        StatusText = "已删除该批注。";
    }

    private void ClearAnnotations()
    {
        if (_model.Annotations.Count == 0) return;

        var cleared = _model.Annotations.ToList();
        _model.Annotations.Clear();
        foreach (var page in Pages) page.RebuildAnnotations(AnnotationsFor(page));

        PushEdit($"清除 {cleared.Count} 条批注",
            undo: () => { foreach (var a in cleared) _model.Annotations.Add(a); foreach (var pg in Pages) pg.RebuildAnnotations(AnnotationsFor(pg)); },
            redo: () => { _model.Annotations.Clear(); foreach (var pg in Pages) pg.RebuildAnnotations([]); });

        RefreshCommands();
        StatusText = "已清除全部批注。";
    }

    private static void ApplyToolColour(AnnotationSpec spec, AnnotationKind kind)
    {
        (spec.R, spec.G, spec.B, spec.A) = kind switch
        {
            AnnotationKind.Highlight => ((byte)255, (byte)214, (byte)0, (byte)110),
            AnnotationKind.Underline => ((byte)220, (byte)40, (byte)40, (byte)255),
            AnnotationKind.StrikeOut => ((byte)220, (byte)40, (byte)40, (byte)255),
            AnnotationKind.Rectangle => ((byte)40, (byte)110, (byte)220, (byte)40),
            AnnotationKind.Ellipse => ((byte)40, (byte)160, (byte)90, (byte)40),
            AnnotationKind.Ink => ((byte)200, (byte)30, (byte)30, (byte)255),
            AnnotationKind.Note => ((byte)255, (byte)200, (byte)40, (byte)230),
            _ => ((byte)255, (byte)214, (byte)0, (byte)110),
        };
    }

    // ------------------------------------------------------------------ search

    private async void RunSearch()
    {
        if (_engine is null || string.IsNullOrWhiteSpace(SearchText)) return;

        var term = SearchText;
        var engine = _engine;
        IsBusy = true;
        StatusText = $"正在搜索「{term}」…";

        try
        {
            var layers = _model.TextLayers.ToList();

            var results = await Task.Run(() =>
            {
                var all = new List<SearchHit>();
                for (var i = 0; i < engine.PageCount; i++)
                {
                    foreach (var hit in engine.FindOnPage(i, term))
                    {
                        all.Add(hit with { Preview = engine.GetTextRange(i, hit.StartIndex, hit.Length) });
                    }
                }

                // Recognised text is not in the PDF until export, so it is matched in memory.
                // Without this, freshly OCR'd pages would not be searchable until saved.
                foreach (var (page, words) in layers)
                    all.AddRange(SearchOcrLayer(page.SourceIndex, words, term));

                return all;
            });

            SearchResults.Clear();
            foreach (var hit in results) SearchResults.Add(hit);

            RefreshSearchHighlights();

            if (results.Count == 0)
            {
                StatusText = $"未找到「{term}」。";
            }
            else
            {
                var ocrCount = results.Count(r => r.FromOcr);
                StatusText = $"找到 {results.Count} 处匹配" +
                             (ocrCount > 0 ? $"（其中 {ocrCount} 处来自 OCR 识别结果）" : "") + "。";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"搜索失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Finds <paramref name="term"/> in a page's pending OCR layer.
    ///
    /// The recogniser segments Chinese into individual characters, so a term like「语音」spans
    /// several entries. Words are therefore reassembled per line, matched in that combined text,
    /// and each match mapped back onto the boxes of the words it covers.
    /// </summary>
    private static IEnumerable<SearchHit> SearchOcrLayer(int sourcePageIndex,
        List<OcrWord> words, string term)
    {
        var comparison = StringComparison.OrdinalIgnoreCase;

        foreach (var line in words.GroupBy(w => w.LineIndex))
        {
            // Reading order within a line, so character offsets line up with the boxes.
            var ordered = line.OrderBy(w => w.Bounds.Left).ToList();
            if (ordered.Count == 0) continue;

            var lineText = string.Concat(ordered.Select(w => w.Text));
            if (lineText.Length == 0) continue;

            // Cumulative offsets: which word covers character N of the line.
            var starts = new int[ordered.Count];
            var cursor = 0;
            for (var i = 0; i < ordered.Count; i++)
            {
                starts[i] = cursor;
                cursor += ordered[i].Text.Length;
            }

            var from = 0;
            while (from <= lineText.Length - term.Length)
            {
                var at = lineText.IndexOf(term, from, comparison);
                if (at < 0) break;

                var end = at + term.Length;

                // Collect the boxes of every word overlapping the match.
                var boxes = new List<TextRect>();
                for (var i = 0; i < ordered.Count; i++)
                {
                    var wordStart = starts[i];
                    var wordEnd = wordStart + ordered[i].Text.Length;
                    if (wordEnd > at && wordStart < end) boxes.Add(ordered[i].Bounds);
                }

                if (boxes.Count > 0)
                {
                    yield return new SearchHit(sourcePageIndex, -1, term.Length, boxes)
                    {
                        Preview = lineText,
                        FromOcr = true,
                    };
                }

                // Step past this match so overlapping repeats are not reported twice.
                from = at + Math.Max(1, term.Length);
            }
        }
    }

    private void GoToHit(SearchHit? hit)
    {
        if (hit is null || hit.PageIndex < 0 || hit.PageIndex >= Pages.Count) return;

        // Search results index source pages; map back through any reordering.
        var page = Pages.FirstOrDefault(p => p.SourceIndex == hit.PageIndex);
        if (page is not null) ScrollToPage?.Invoke(page);
    }

    private void GoToOutline(OutlineEntry? entry)
    {
        if (entry is null || entry.PageIndex < 0) return;

        var page = Pages.FirstOrDefault(p => p.SourceIndex == entry.PageIndex);
        if (page is not null) ScrollToPage?.Invoke(page);
    }

    // ------------------------------------------------------------------ overlays & export

    private void AddWatermark()
    {
        var overlay = new OverlaySpec
        {
            Kind = OverlayKind.Watermark,
            Text = "机密",
            FontFamily = "SimHei",
            FontSize = 90,
            Angle = 45,
            Opacity = 0.22,
            R = 220, G = 30, B = 30,
        };

        _model.Overlays.Add(overlay);
        PushEdit("添加水印",
            undo: () => _model.Overlays.Remove(overlay),
            redo: () => _model.Overlays.Add(overlay));

        StatusText = "已添加水印叠加（导出时绘制）。";
    }

    private void AddPageNumbers()
    {
        var overlay = new OverlaySpec
        {
            Kind = OverlayKind.PageNumber,
            FontFamily = "SimHei",
            FontSize = 10,
            Anchor = OverlayAnchor.BottomCenter,
            OffsetY = -24,
            NumberFormat = "{0} / {1}",
            Opacity = 0.85,
            R = 60, G = 60, B = 60,
        };

        _model.Overlays.Add(overlay);
        PushEdit("添加页码",
            undo: () => _model.Overlays.Remove(overlay),
            redo: () => _model.Overlays.Add(overlay));

        StatusText = "已添加页码（导出时绘制）。";
    }

    private void ExportImages()
    {
        if (_engine is null || _renderer is null) return;

        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择导出图片的文件夹" };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var renderer = _renderer;
            var pages = _model.LivePages;

            for (var i = 0; i < pages.Count; i++)
            {
                var bitmap = renderer.Get(pages[i].SourceIndex, 2.0, pages[i].Rotation, true);
                var path = Path.Combine(dialog.FolderName, $"page_{i + 1:000}.png");
                using var stream = File.Create(path);

                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                encoder.Save(stream);
            }

            StatusText = $"已导出 {pages.Count} 张图片到 {dialog.FolderName}";
        }
        catch (Exception ex)
        {
            StatusText = "导出图片失败。";
            MessageBox.Show($"导出图片失败：\n{ex.Message}", "导出失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ------------------------------------------------------------------ helpers

    private void MarkDirty()
    {
        _model.IsDirty = true;
        OnPropertyChanged(nameof(Title));
    }

    private void RebuildPage(PageViewModel page)
    {
        page.RebuildAnnotations(AnnotationsFor(page));
        QueueRender(page);
    }

    /// <summary>Forces every visible page to re-render, e.g. after a rotation.</summary>
    public void InvalidateVisible()
    {
        foreach (var page in Pages) _renderedAt.Remove(page);
    }

    private void RefreshCommands()
    {
        SaveCommand.RaiseCanExecuteChanged();
        SaveAsCommand.RaiseCanExecuteChanged();
        ClearAnnotationsCommand.RaiseCanExecuteChanged();
        RotateClockwiseCommand.RaiseCanExecuteChanged();
        RotateCounterClockwiseCommand.RaiseCanExecuteChanged();
    }

    private static TextRect Normalize(Point a, Point b) => new(
        Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
        Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    private static TextRect FromPoints(IReadOnlyList<(double X, double Y)> points)
    {
        var minX = points.Min(p => p.X);
        var minY = points.Min(p => p.Y);
        var maxX = points.Max(p => p.X);
        var maxY = points.Max(p => p.Y);
        return new TextRect(minX, minY, maxX, maxY);
    }

    private static TextRect Union(IReadOnlyList<TextRect> rects) => new(
        rects.Min(r => r.Left), rects.Min(r => r.Top),
        rects.Max(r => r.Right), rects.Max(r => r.Bottom));

    private static string DescribeTool(EditorTool tool) => tool switch
    {
        EditorTool.Highlight => "高亮",
        EditorTool.Underline => "下划线",
        EditorTool.StrikeOut => "删除线",
        EditorTool.Rectangle => "矩形框",
        EditorTool.Ellipse => "椭圆框",
        EditorTool.Ink => "自由绘制",
        EditorTool.Note => "便签",
        EditorTool.EditContent => "正文编辑",
        EditorTool.InsertText => "插入文字",
        _ => "批注",
    };

    public void Dispose()
    {
        _renderer?.Dispose();
        _engine?.Dispose();
    }
}
