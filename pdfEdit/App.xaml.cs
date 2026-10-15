using System.IO;
using System.Windows;
using System.Windows.Threading;
using PdfEdit.Services;

namespace PdfEdit;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // PDFium is process-global: initialise once, tear down on exit.
        PdfiumEngine.Initialize();
        Exit += (_, _) => PdfiumEngine.Shutdown();

        // Headless smoke test: `PdfEdit.exe --selftest <file.pdf> <outdir>`.
        // A WinExe has no console attached, so results go to a log file.
        if (e.Args.Length >= 3 && e.Args[0] == "--selftest")
        {
            _headless = true;
            var code = SelfTest.Run(e.Args[1], e.Args[2]);
            Shutdown(code);
            return;
        }

        // `PdfEdit.exe --uitest [file.pdf]` builds and shows the real window, optionally loads a
        // document, then closes itself. Exercises XAML parsing, bindings and the render pipeline
        // without needing a human at the keyboard.
        if (e.Args.Length >= 1 && e.Args[0] == "--uitest")
        {
            RunUiSmokeTest(e.Args.Length >= 2 ? e.Args[1] : null);
            return;
        }

        // `--noconfirm` launches with the unsaved-changes prompt disabled, for scripted runs
        // that open and close the app repeatedly.
        var noConfirm = e.Args.Any(a => string.Equals(a, "--noconfirm", StringComparison.OrdinalIgnoreCase));

        var window = new Views.MainWindow(noConfirm);
        window.Show();

        // Support `PdfEdit.exe some.pdf`, plus shell "Open with" and dragging a file onto the exe.
        var initialFile = e.Args.FirstOrDefault(a => !a.StartsWith('-'));
        if (initialFile is not null && File.Exists(initialFile))
            ((ViewModels.MainViewModel)window.DataContext).OpenFile(initialFile);

        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;

            // Headless runs must never raise a dialog: nothing can dismiss it, so the process
            // would hang until killed and the failure would look like a timeout.
            if (_headless)
            {
                File.AppendAllText(
                    Path.Combine(AppContext.BaseDirectory, "uitest.log"),
                    "\n!! 未处理的异常: " + args.Exception + "\n");
                return;
            }

            MessageBox.Show(args.Exception.Message, "PDF 编辑器 — 未处理的错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        };
    }

    /// <summary>Renders the window's visual tree to a PNG so a run can be inspected visually.</summary>
    private static void CaptureWindow(Window window, string path)
    {
        try
        {
            // Force a layout and render pass first. Without this the capture happens before
            // WPF has arranged anything that just became visible, and the shot comes out with
            // those elements missing entirely.
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { },
                System.Windows.Threading.DispatcherPriority.Render);

            var width = (int)Math.Max(1, window.ActualWidth);
            var height = (int)Math.Max(1, window.ActualHeight);

            var target = new System.Windows.Media.Imaging.RenderTargetBitmap(
                width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            target.Render(window);

            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(target));

            using var stream = File.Create(path);
            encoder.Save(stream);
        }
        catch (Exception ex)
        {
            File.AppendAllText(path + ".error.txt", ex.ToString());
        }
    }

    /// <summary>Set while running --selftest / --uitest, where a modal dialog would deadlock.</summary>
    private static bool _headless;

    private void RunUiSmokeTest(string? pdfPath)
    {
        _headless = true;
        var log = new System.Text.StringBuilder();
        var logPath = Path.Combine(AppContext.BaseDirectory, "uitest.log");
        void Flush() => File.WriteAllText(logPath, log.ToString());

        try
        {
            // Force-close: the smoke test deliberately dirties the document, and the
            // unsaved-changes prompt would block on a dialog nobody can dismiss headlessly.
            // That was the real cause of the intermittent 124 timeouts.
            var window = new Views.MainWindow(forceClose: true);
            window.Show();
            log.AppendLine("窗口已创建并显示");

            if (pdfPath is not null)
            {
                ((ViewModels.MainViewModel)window.DataContext).OpenFile(pdfPath);
                log.AppendLine($"已加载: {pdfPath}");
                log.AppendLine($"页数: {((ViewModels.MainViewModel)window.DataContext).Pages.Count}");
            }

            // Let layout, binding and the first render pass actually run.
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();

                var vm = (ViewModels.MainViewModel)window.DataContext;
                log.AppendLine($"状态栏: {vm.StatusText}");
                log.AppendLine($"当前页: {vm.PageLabel}");
                log.AppendLine($"缩放: {vm.ZoomPercent}");
                log.AppendLine($"目录项: {vm.OutlineItems.Count}");

                // The first page must have produced a bitmap by now, or the render path is dead.
                var first = vm.Pages.FirstOrDefault();
                log.AppendLine(first is null
                    ? "首页: 无"
                    : $"首页: {first.State}, 位图 {(first.Image is null ? "无" : $"{first.Image.PixelWidth}x{first.Image.PixelHeight}")}, " +
                      $"缩略图 {(first.Thumbnail is null ? "无" : "有")}");

                // Exercise the read/edit mode switch.
                log.AppendLine($"初始模式: {vm.ModeLabel} (IsEditMode={vm.IsEditMode})");

                vm.IsEditMode = true;
                vm.CurrentTool = ViewModels.EditorTool.Highlight;
                log.AppendLine($"切到编辑模式: {vm.ModeLabel}, 工具={vm.CurrentTool}");

                vm.IsEditMode = false;
                var toolDropped = vm.CurrentTool == ViewModels.EditorTool.Select;
                log.AppendLine($"切回阅读模式: {vm.ModeLabel}, 工具={vm.CurrentTool}" +
                               (toolDropped ? " (已自动复位)" : " (!! 未复位)"));

                if (!toolDropped)
                {
                    log.AppendLine("UI 冒烟测试失败: 退出编辑模式后工具未复位");
                    window.Close();
                    Flush();
                    Shutdown(1);
                    return;
                }

                // Content editing: pick a real page object and rewrite it in place.
                if (vm.Pages.Count > 0)
                {
                    vm.CurrentTool = ViewModels.EditorTool.EditContent;

                    // A point inside the title text block on page 1 of the test document.
                    vm.SelectObjectAt(vm.Pages[0], new System.Windows.Point(297, 264));
                    var picked = vm.SelectedObject;
                    log.AppendLine($"选中对象: {picked?.TypeLabel} \"{picked?.Text}\" 字体={picked?.FontName}");

                    if (picked is null)
                    {
                        log.AppendLine("UI 冒烟测试失败: 未选中任何对象");
                        window.Close();
                        Flush();
                        Shutdown(1);
                        return;
                    }

                    // Reverse the string: same glyphs, so this must succeed even with a subset font.
                    // Where does the inline editor actually land, and how big is it?
                    var selPage = vm.Pages[0];
                    log.AppendLine($"选中框: left={selPage.SelectionLeft:0.#} top={selPage.SelectionTop:0.#} " +
                                   $"width={selPage.SelectionWidth:0.#} (zoom={vm.Zoom:0.##})");
                    log.AppendLine($"页尺寸: {selPage.DisplayWidth:0.#} x {selPage.DisplayHeight:0.#} px");
                    var pb = picked.Bounds;
                    log.AppendLine($"对象范围(pt): ({pb.Left:0.#},{pb.Top:0.#})-({pb.Right:0.#},{pb.Bottom:0.#}) " +
                                   $"→ 应为 {pb.Left * vm.Zoom:0.#},{pb.Top * vm.Zoom:0.#} " +
                                   $"宽 {(pb.Right - pb.Left) * vm.Zoom:0.#}");

                    // Capture the window so the result can actually be looked at, rather than
                    // inferred from property values.
                    CaptureWindow(window, Path.Combine(AppContext.BaseDirectory, "shot_editor.png"));

                    vm.ContentEditText = new string(picked.Text.Reverse().ToArray());
                    vm.ApplyContentEditCommand.Execute(null);
                    log.AppendLine($"改写后状态: {vm.StatusText}");

                    log.AppendLine(vm.HasContentEditHint
                        ? $"提示: {vm.ContentEditHint}"
                        : "无提示（改写被接受）");
                }

                // Re-capture now that the UI has settled: selects the text again so the inline
                // editor is on screen, then takes the shot.
                vm.CurrentTool = ViewModels.EditorTool.EditContent;
                vm.SelectObjectAt(vm.Pages[0], new System.Windows.Point(297, 264));
                CaptureWindow(window, Path.Combine(AppContext.BaseDirectory, "shot_settled.png"));
                log.AppendLine($"SelectionFontSize 绑定值 = {vm.SelectionFontSize:0.#} " +
                               $"(对象 {vm.SelectedObject?.FontSize ?? 0:0.#}pt, zoom {vm.Zoom:0.##})");
                log.AppendLine($"SelectionHeight = {vm.Pages[0].SelectionHeight:0.#}");

                log.AppendLine(first?.Image is null ? "UI 冒烟测试失败: 首页未渲染" : "UI 冒烟测试通过");

                window.Close();
                Flush();
                Shutdown(first?.Image is null ? 1 : 0);
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            log.AppendLine("UI 冒烟测试失败:");
            log.AppendLine(ex.ToString());
            Flush();
            Shutdown(1);
        }

        Flush();
    }
}
