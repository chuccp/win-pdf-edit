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
