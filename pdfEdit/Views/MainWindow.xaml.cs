using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PdfEdit.ViewModels;

namespace PdfEdit.Views;

public partial class MainWindow : Window
{
    /// <summary>Vertical rhythm of the page stack; must match the page template's margin.</summary>
    private const double PageGap = 14;

    private readonly MainViewModel _viewModel = new();

    private readonly List<Point> _strokePoints = [];
    private PageViewModel? _dragPage;
    private Canvas? _dragCanvas;
    private Rectangle? _rubberBand;
    private bool _isDragging;

    public MainWindow()
    {
        InitializeComponent();

        DataContext = _viewModel;
        _viewModel.ScrollToPage += ScrollToPage;

        Closing += OnClosing;
        Closed += (_, _) => _viewModel.Dispose();
        Loaded += (_, _) =>
        {
            // The viewport only becomes known once the window has laid out.
            _viewModel.OnViewportChanged(PageScroll.VerticalOffset, PageScroll.ViewportHeight,
                                         PageScroll.ViewportWidth);
        };
    }

    // ------------------------------------------------------------------ viewport

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        => _viewModel.OnViewportChanged(PageScroll.VerticalOffset, PageScroll.ViewportHeight,
                                        PageScroll.ViewportWidth);

    private void OnPageNumberKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        _viewModel.GoToPageNumberCommand.Execute((sender as TextBox)?.Text);
        e.Handled = true;
    }

    /// <summary>
    /// Scrolls by accumulating page heights rather than hunting for the generated container —
    /// virtualisation is off, so the arithmetic is exact and avoids a layout round-trip.
    /// </summary>
    private void ScrollToPage(PageViewModel page)
    {
        var index = _viewModel.Pages.IndexOf(page);
        if (index < 0) return;

        var offset = PageScroll.Padding.Top;
        for (var i = 0; i < index; i++)
            offset += _viewModel.Pages[i].DisplayHeight + PageGap;

        PageScroll.ScrollToVerticalOffset(offset);
    }

    // ------------------------------------------------------------------ page input

    private static PageViewModel? PageOf(object sender)
        => (sender as FrameworkElement)?.DataContext as PageViewModel;

    private Point ToPageSpace(Point devicePoint)
        => new(devicePoint.X / _viewModel.Zoom, devicePoint.Y / _viewModel.Zoom);

    private void OnPageMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Canvas canvas || PageOf(sender) is not { } page) return;
        if (!_viewModel.HasDocument || !_viewModel.IsEditMode) return;

        var position = e.GetPosition(canvas);

        // A double-click goes straight into editing whatever text sits under the pointer,
        // whatever tool happens to be armed.
        if (e.ClickCount == 2)
        {
            _viewModel.CurrentTool = EditorTool.EditContent;
            _viewModel.SelectObjectAt(page, ToPageSpace(position));
            e.Handled = true;
            return;
        }

        if (_viewModel.CurrentTool == EditorTool.InsertText)
        {
            _viewModel.InsertTextAt(page, ToPageSpace(position));
            return;
        }

        if (_viewModel.CurrentTool == EditorTool.EditContent)
        {
            // Content editing works on a single click, not a drag.
            _viewModel.SelectObjectAt(page, ToPageSpace(position));
            return;
        }

        if (_viewModel.CurrentTool == EditorTool.Select)
        {
            // There is no undo yet, so a stray left-click must not destroy work. Selecting only
            // reports what is under the pointer; right-click is the deliberate delete gesture.
            var found = FindAnnotationAt(page, position);
            _viewModel.StatusText = found?.Source is null
                ? "此处没有批注（右键可删除批注）。"
                : $"此处有一条{Describe(found.Source.Kind)}，右键点击可删除。";
            return;
        }

        _isDragging = true;
        _dragPage = page;
        _dragCanvas = canvas;
        _strokePoints.Clear();
        _strokePoints.Add(ToPageSpace(position));

        canvas.CaptureMouse();

        if (_viewModel.CurrentTool != EditorTool.Ink)
        {
            _rubberBand = new Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)),
                StrokeThickness = 1,
                StrokeDashArray = [4, 3],
                Fill = new SolidColorBrush(Color.FromArgb(28, 0x3B, 0x82, 0xF6)),
                IsHitTestVisible = false,
            };
            canvas.Children.Add(_rubberBand);
        }

        e.Handled = true;
    }

    private void OnPageMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging || _dragCanvas is null || sender is not Canvas canvas) return;

        var position = e.GetPosition(canvas);
        _strokePoints.Add(ToPageSpace(position));

        if (_rubberBand is null) return;

        // Rubber band is positioned in device space; the stroke itself is kept in page space.
        var origin = new Point(_strokePoints[0].X * _viewModel.Zoom, _strokePoints[0].Y * _viewModel.Zoom);
        Canvas.SetLeft(_rubberBand, Math.Min(origin.X, position.X));
        Canvas.SetTop(_rubberBand, Math.Min(origin.Y, position.Y));
        _rubberBand.Width = Math.Abs(position.X - origin.X);
        _rubberBand.Height = Math.Abs(position.Y - origin.Y);
    }

    private void OnPageMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging || _dragPage is null) return;

        _isDragging = false;
        _dragCanvas?.ReleaseMouseCapture();

        if (_rubberBand is not null)
        {
            _dragCanvas?.Children.Remove(_rubberBand);
            _rubberBand = null;
        }

        if (_strokePoints.Count > 0 && _viewModel.CurrentTool != EditorTool.Select)
            _viewModel.AddAnnotation(_dragPage, _viewModel.CurrentTool, _strokePoints);

        _strokePoints.Clear();
        _dragPage = null;
        _dragCanvas = null;
    }

    /// <summary>Right-click deletes whichever annotation is under the pointer, in any mode.</summary>
    private void OnPageRightClick(object sender, MouseButtonEventArgs e)
    {
        if (PageOf(sender) is not { } page) return;

        // Right-click is destructive, so it is an editing gesture like any other.
        if (!_viewModel.IsEditMode) return;

        DeleteAnnotationAt(page, e.GetPosition((IInputElement)sender));
        e.Handled = true;
    }

    private void DeleteAnnotationAt(PageViewModel page, Point devicePoint)
    {
        var hit = FindAnnotationAt(page, devicePoint);

        if (hit?.Source is not null)
            _viewModel.DeleteAnnotationCommand.Execute(hit.Source);
        else
            _viewModel.StatusText = "此处没有批注。";
    }

    /// <summary>
    /// Finds the annotation under a point. Bounds are already in device space, so no zoom
    /// conversion is needed; the smallest match wins so a highlight inside a large box is
    /// the one that gets picked.
    /// </summary>
    private static AnnotationVisual? FindAnnotationAt(PageViewModel page, Point devicePoint)
        => page.Annotations
            .Where(a => a.Bounds.Contains(devicePoint))
            .OrderBy(a => a.Bounds.Width * a.Bounds.Height)
            .FirstOrDefault();

    /// <summary>
    /// Enter commits the in-place edit, Escape abandons it — the keys mainstream editors use,
    /// so the mouse never has to leave the text being changed.
    /// </summary>
    private void OnInlineEditorKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                _viewModel.ApplyContentEditCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Escape:
                _viewModel.CancelContentEditCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    /// <summary>Focuses and pre-selects the text the moment the editor appears.</summary>
    private void OnInlineEditorVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox box || !box.IsVisible) return;

        // The element is only just becoming visible, so focus has to wait for layout.
        box.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            box.Focus();
            box.SelectAll();
        });
    }

    private static string Describe(Models.AnnotationKind kind) => kind switch
    {
        Models.AnnotationKind.Highlight => "高亮",
        Models.AnnotationKind.Underline => "下划线",
        Models.AnnotationKind.StrikeOut => "删除线",
        Models.AnnotationKind.Squiggly => "波浪线",
        Models.AnnotationKind.Rectangle => "矩形框",
        Models.AnnotationKind.Ellipse => "椭圆框",
        Models.AnnotationKind.Ink => "手绘",
        Models.AnnotationKind.Note => "便签",
        _ => "文字",
    };

    // ------------------------------------------------------------------ sidebar

    private void OnThumbnailClick(object sender, MouseButtonEventArgs e)
    {
        if (PageOf(sender) is { } page) ScrollToPage(page);
    }

    private void OnOutlineSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is Models.OutlineEntry entry)
            _viewModel.GoToOutlineCommand.Execute(entry);
    }

    private void OnSearchResultActivate(object sender, MouseButtonEventArgs e)
    {
        if ((sender as ListBox)?.SelectedItem is Models.SearchHit hit)
            _viewModel.GoToHitCommand.Execute(hit);
    }

    private void OnSearchResultKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if ((sender as ListBox)?.SelectedItem is Models.SearchHit hit)
            _viewModel.GoToHitCommand.Execute(hit);
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        if (_viewModel.SearchCommand.CanExecute(null))
            _viewModel.SearchCommand.Execute(null);
    }

    // ------------------------------------------------------------------ window

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFileDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            _viewModel.OpenFile(files[0]);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        switch (e.Key)
        {
            case Key.O when ctrl:
                _viewModel.OpenCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.S when ctrl:
                _viewModel.SaveCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Z when ctrl:
                _viewModel.UndoCommand.Execute(null);
                e.Handled = true;
                break;

            // Ctrl+Shift+Z and Ctrl+Y are both in common use for redo.
            case Key.Z when ctrl && (Keyboard.Modifiers & ModifierKeys.Shift) != 0:
                _viewModel.RedoCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Y when ctrl:
                _viewModel.RedoCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.OemPlus or Key.Add when ctrl:
                _viewModel.ZoomInCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.OemMinus or Key.Subtract when ctrl:
                _viewModel.ZoomOutCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.D0 when ctrl:
                _viewModel.ZoomResetCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.PageDown:
                MoveCurrentPage(1);
                e.Handled = true;
                break;

            case Key.PageUp:
                MoveCurrentPage(-1);
                e.Handled = true;
                break;

            case Key.Tab when _viewModel.IsEditMode:
                // Walk the page is regions instead of moving focus out of the canvas.
                _viewModel.StepSelection((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1);
                e.Handled = true;
                break;

            case Key.Escape:
                _viewModel.CurrentTool = EditorTool.Select;
                e.Handled = true;
                break;
        }

        base.OnPreviewKeyDown(e);
    }

    /// <summary>
    /// Refuses to close over unsaved work. Every edit here — annotations, retyped text, OCR
    /// layers — is held in memory until saved, so closing silently would discard all of it.
    /// </summary>
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_viewModel.IsDirty)
        {
            _viewModel.Dispose();
            return;
        }

        var choice = MessageBox.Show(
            "文档有未保存的修改。\n\n保存后关闭？",
            "PDF 阅读编辑器",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

        switch (choice)
        {
            case MessageBoxResult.Yes:
                _viewModel.SaveCommand.Execute(null);

                // Saving may itself fail (or be cancelled in the Save As dialog); if the document
                // is still dirty afterwards, keep the window open rather than losing the work.
                if (_viewModel.IsDirty) e.Cancel = true;
                break;

            case MessageBoxResult.No:
                break;

            default:
                e.Cancel = true;
                break;
        }
    }

    private void MoveCurrentPage(int delta)
    {
        var target = _viewModel.CurrentPageIndex + delta;
        if (target < 0 || target >= _viewModel.Pages.Count) return;

        ScrollToPage(_viewModel.Pages[target]);
    }

    /// <summary>Ctrl+wheel zooms; a plain wheel keeps its normal scrolling behaviour.</summary>
    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            _viewModel.Zoom *= e.Delta > 0 ? 1.1 : 1 / 1.1;
            e.Handled = true;
        }

        base.OnPreviewMouseWheel(e);
    }
}
