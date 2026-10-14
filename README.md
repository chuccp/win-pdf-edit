# PDF 阅读编辑器

Windows 桌面 PDF 阅读与编辑工具，基于 .NET 10 + WPF。

```bash
cd pdfEdit
dotnet run
```

## 功能

| 分类 | 功能 |
|---|---|
| 阅读与导航 | 连续滚动、缩放（10%–600%）、缩略图侧栏、书签目录、全文搜索并跳转 |
| 页面操作 | 顺时针/逆时针旋转、删除、上移/下移、导出为 PNG 图片 |
| 批注与标注 | 高亮、下划线、删除线、矩形框、椭圆框、自由绘制、便签 |
| 内容叠加 | 水印、页码（导出时绘制到页面内容流） |

批注是**真实的 PDF 注释对象**（`/Highlight`、`/Square`、`/Ink` 等），Acrobat、Edge 等阅读器可直接识别与编辑。

## 技术栈

| 用途 | 库 | 许可 |
|---|---|---|
| 渲染、文字提取、搜索、批注 | PDFium（`bblanchon.PDFium.Win32`，手写 P/Invoke） | BSD-3 |
| 页面结构、叠加内容、字体嵌入 | PDFsharp 6 | MIT |

选择直接绑定 PDFium 而非 `PDFtoImage`：后者会连带引入 SkiaSharp 与 WebAssembly/Blazor 原生资源（约 100 MB 无用依赖），而直接绑定可以拿到完整的文字与注释 API，并直接输出 BGRA 缓冲区供 WPF 使用，省掉一次 SkiaSharp → WPF 的格式转换。

## 架构

```
Interop/Pdfium.cs          PDFium 的 P/Invoke 绑定（签名逐条对照官方头文件）
Services/
  PdfiumEngine.cs          文档生命周期、渲染、文字、搜索、目录、批注、保存
  RenderService.cs         带 LRU 上限的位图缓存
  PdfExportService.cs      页面结构 + 叠加内容（PDFsharp）
  DocumentExporter.cs      两阶段导出的编排
  WindowsFontResolver.cs   PDFsharp 6 的字体解析器
ViewModels/                MainViewModel（命令与状态）、PageViewModel（单页）
Views/                     MainWindow、转换器
```

### 导出管线

导出分两阶段，顺序不可颠倒：

1. **PDFsharp** 重建文档 —— 最终页序、删除、额外旋转、水印/页码写入内容流。
2. **PDFium** 写入批注 —— 此时页序已定，批注才能按页索引寻址。

批注绑定在 `PageItem` 上而非页索引，因此重排、删除页面后批注仍跟随原页。

## 已验证的不变量

`PdfEdit.exe --selftest <文件.pdf> <输出目录>` 会做像素级往返验证，全部通过：

- 六种批注子类型写入文件后重新渲染，着色像素全部落在目标区域内，**区域外 0 像素**
- 页面旋转 90° 后批注仍精确跟随文字（实测包围盒 `(439,269)-(447,295)` 对预期 `439,269.4→447.6,295.6`）
- 重排 + 删除后输出页数与内容正确
- 水印叠加生效

`PdfEdit.exe --uitest [文件.pdf]` 是无头 UI 冒烟测试，用于确认 XAML、绑定与渲染链路可用。

## 三个容易踩的坑

**1. 坐标系有两套。** PDFium 的 `FPDFText_GetRect` 返回的是 **PDF 用户空间**（原点在左下、Y 向上），表现为 `top` 大于 `bottom`；而 `FPDFAnnot_SetRect` 同样用用户空间，WPF 画布却是左上原点、Y 向下。转换由 `DisplayToPdfSpace` / `PdfToDisplaySpace` 一对互逆函数承担，并且都要处理页面自身的 `/Rotate`。

**2. 文字标注必须写 `/QuadPoints`。** 只调用 `FPDFAnnot_SetRect` 会生成一个**完全合法但不可见**的注释对象——文件里查得到（`FPDFPage_GetAnnotCount` 返回 1），渲染却什么都不画。高亮/下划线/删除线/波浪线都由 `/QuadPoints` 定位，需要 `FPDFAnnot_AppendAttachmentPoints`。

**3. `FPDF_RenderPageBitmap` 自己会应用 `/Rotate`。** `FPDF_GetPageWidthF` 返回的尺寸也已包含它。因此 `rotate` 参数只能传**额外的**旋转量；把页面自身的旋转再加一遍会得到倒转 180° 的页面。

另外，`bblanchon.PDFium.Win32` 的 `.targets` 位于 `build/native/`，MSBuild 只对 C++ 项目导入它，所以 .NET 项目必须设置 `RuntimeIdentifier`，否则 `pdfium.dll` 不会复制到输出目录。

## 已知限制

- **无撤销。** 删除批注后只能重新绘制，因此左键点击批注只做提示，删除需右键。
- **不编辑正文文字。** PDF 没有可重排的文字模型，本工具不改动原有正文，只叠加新内容（这也是所有同类工具的通用做法）。
- **表单填写未实现。** PDFium 的表单 API（`FPDFDOC_*`）未绑定。
- **加密 PDF 不支持**，会提示需要密码。
- **文档自带的既有批注**会正常渲染，但不会读入到可编辑模型里。

## 待办

按影响排序。动手前请先读下面的「改动前必读」。

### 高

- [ ] **批注编辑** —— 改内容或颜色只能删了重画。需要批注选中态，以及重新打开编辑框的入口。
- [ ] **便签输入内容** —— `便签` 工具只画了个图标，点了没反应，应弹出输入框填写 `Contents`。

### 中

- [ ] **缩略图懒加载** —— 打开文档时一次性渲染全部页面缩略图，几百页会明显卡顿。应改为按视口渲染。
- [ ] **字体按原字形匹配** —— 扫描件改写目前固定用 SimHei。应分析原文字的笔画粗细，在系统字体中选最接近的一个。
- [ ] **搜索异步化** —— 搜索已在 `Task.Run` 中执行，但结果逐页累积，超大文档仍会让状态栏长时间停在“搜索中”。

### 低

- [ ] **打印** —— 走 WPF 的 `PrintDialog`，可直接复用 `PdfiumEngine.RenderPage`。
- [ ] **撤销历史面板** —— 撤销栈已记录 `IUndoableEdit.Label`，可直接列出来供跳转。

### 已知未解决

- **段落不会重排。** 改长的文字会溢出原位置。这是 PDF 没有文字流模型的固有限制，Acrobat 同样如此，但主流会提供「自动调整字号」之类的补偿选项。
- **纹理补丁取自同页邻近区域**，若整页都有底纹或水印则找不到干净候选，会回退为纯色填充并留下色块。

## 改动前必读

这几条是实际踩过的，不是理论风险：

1. **坐标系有两套，转换函数是成对的。** `DisplayToPdfSpace` / `PdfToDisplaySpace`（在 `PdfiumEngine`）与 `PdfExportService.ToPdfUserSpace` 必须保持一致，改了其中一个就要同步另一个。`--selftest` 里有旋转页的用例专门盯这个。

2. **文字标注必须写 `/QuadPoints`。** 只设 `/Rect` 会生成**完全合法但不可见**的注释对象：文件里查得到，渲染什么都不画。

3. **删除页面对象要延迟到导出时。** 当场删除就无法撤销了（对象一旦从 PDFium 文档移除就回不来）。`TextReplacement.HideObjectPath` 就是为此存在，撤销只是丢掉一条记录。

4. **任何改变文档的操作都要走 `PushEdit`。** 否则该操作不可撤销，而用户会以为可以。

5. **选区相关属性务必在 setter 内发通知。** 曾经因为 `CanEditSelection` / `SelectionLabel` 只在部分路径通知，导致双击后编辑框是禁用的 —— 界面看着正常，就是打不进字。

6. **不要在无头模式下弹消息框。** `--selftest` / `--uitest` 里没人能点掉它，进程会挂到超时，看起来像死锁而不是报错。用 `_headless` 判断。

## 验证方式

```bash
# 像素级回归：坐标变换、旋转、批注、OCR、导出、撤销
PdfEdit.exe --selftest <某个.pdf> <输出目录>

# 无头 UI 冒烟：XAML、绑定、渲染链路，并输出窗口截图
PdfEdit.exe --uitest <某个.pdf>
```

`--uitest` 会在输出目录留下 `shot_settled.png`。**改界面时先看截图再改代码** —— 有几处问题（编辑框浮在左上角、字溢出框外）数值全是对的，只有看图才发现。
