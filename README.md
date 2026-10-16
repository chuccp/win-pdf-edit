# PDF 阅读编辑器

Windows 桌面 PDF 阅读与编辑工具，基于 .NET 10 + WPF。

```bash
cd pdfEdit
dotnet run
```

## 功能

| 分类 | 功能 |
|---|---|
| 阅读与导航 | 连续滚动、缩放（10%–600%）、缩略图侧栏（按视口懒加载）、书签目录、全文搜索并跳转 |
| 页面操作 | 顺时针/逆时针旋转、删除、上移/下移、导出为 PNG 图片、打印 |
| 批注与标注 | 高亮、下划线、删除线、矩形框、椭圆框、自由绘制、便签（放置时填写内容） |
| 内容叠加 | 水印、页码（导出时绘制到页面内容流） |
| 编辑历史 | 完整撤销/重做，历史面板列出每一步，点击任意一步跳转到该状态 |

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
  FontMatcher.cs           按笔画指标为改写文字匹配最接近的系统字体
  PdfPrintPaginator.cs     把渲染页喂给 WPF 打印管线
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
- 每种批注的 `/Contents` 写入后回读一致（`FPDFAnnot_SetStringValue` 的 key 是**字节串**，用 UTF-16LE 编码会把它截成首字母，见下）
- 页面旋转 90° 后批注仍精确跟随文字（实测包围盒 `(439,269)-(447,295)` 对预期 `439,269.4→447.6,295.6`）
- 正文改写：复用原字形必成功、引入新字形被拒后**渲染逐像素不变**（快照回滚，不是字符串回写）
- 重排 + 删除后输出页数与内容正确
- 水印叠加生效
- 字体匹配回环：SimHei/KaiTi/FangSong 渲染出的样本能被 `FontMatcher` 认回原字体
- 历史面板跳转：首项/尾项/中间项各自落到正确的已应用计数

`PdfEdit.exe --uitest [文件.pdf]` 是无头 UI 冒烟测试，用于确认 XAML、绑定与渲染链路可用。

无文字层的纯扫描样张会自动跳过文本/搜索/改写相关断言（由 OCR 节覆盖），不算失败。

## 四个容易踩的坑

**1. 坐标系有两套。** PDFium 的 `FPDFText_GetRect` 返回的是 **PDF 用户空间**（原点在左下、Y 向上），表现为 `top` 大于 `bottom`；而 `FPDFAnnot_SetRect` 同样用用户空间，WPF 画布却是左上原点、Y 向下。转换由 `DisplayToPdfSpace` / `PdfToDisplaySpace` 一对互逆函数承担，并且都要处理页面自身的 `/Rotate`。

**2. 文字标注必须写 `/QuadPoints`。** 只调用 `FPDFAnnot_SetRect` 会生成一个**完全合法但不可见**的注释对象——文件里查得到（`FPDFPage_GetAnnotCount` 返回 1），渲染却什么都不画。高亮/下划线/删除线/波浪线都由 `/QuadPoints` 定位，需要 `FPDFAnnot_AppendAttachmentPoints`。

**3. `FPDF_RenderPageBitmap` 自己会应用 `/Rotate`。** `FPDF_GetPageWidthF` 返回的尺寸也已包含它。因此 `rotate` 参数只能传**额外的**旋转量；把页面自身的旋转再加一遍会得到倒转 180° 的页面。

**4. `FPDFAnnot_SetStringValue` 的 key 是字节串，value 才是宽串。** key 用 `Utf16Z` 编码的话，`"Contents"` 会在第一个字符后遇到 NUL，PDFium 读到的 key 是 `"C"`——批注内容写去了不存在的条目，外观完全看不出来。自检的 `/Contents` 回读断言就是盯着这个。

另外，`bblanchon.PDFium.Win32` 的 `.targets` 位于 `build/native/`，MSBuild 只对 C++ 项目导入它，所以 .NET 项目必须设置 `RuntimeIdentifier`，否则 `pdfium.dll` 不会复制到输出目录。

## 已知限制

- **表单填写未实现。** PDFium 的表单 API（`FPDFDOC_*`）未绑定。
- **文档自带的既有批注**会正常渲染，但不会读入到可编辑模型里。

## 待办

按影响排序。动手前请先读下面的「改动前必读」。

### 已完成

- [x] **便签输入内容** —— 放置便签时弹输入框填写 `Contents`，双击/右键可再编辑，悬停显示全文。
- [x] **缩略图懒加载** —— 缩略图随侧栏行进入视口才渲染。
- [x] **字体按原字形匹配** —— 用笔画覆盖率/游程/波动三项指标为改写文字匹配最接近的系统字体（`FontMatcher`）。
- [x] **搜索异步化** —— 结果逐页流入结果面板，带「第 X / N 页」进度，新搜索或清除会取消旧搜索。
- [x] **打印** —— `PdfPrintPaginator` 复用 `PdfiumEngine.RenderPage` 喂给 WPF `PrintDialog`，支持页码范围。
- [x] **撤销历史面板** —— 「历史」标签页列出每一步，点击跳转到该状态。

### 已知未解决

- **段落不会重排。** 改长的文字会溢出原位置。这是 PDF 没有文字流模型的固有限制，Acrobat 同样如此，但主流会提供「自动调整字号」之类的补偿选项。
- **纹理补丁取自同页邻近区域**，若整页都有底纹或水印则找不到干净候选，会回退为纯色填充并留下色块。
- **多字搜索在 `/Rotate` 页上可能失效。** PDFium 提取时会重排单字形文本对象的顺序（内容流逐字节不变），一页一长串的扫描文字层尤甚。自检用逐字定位兜底；产品侧暂无对策。

## 改动前必读

这几条是实际踩过的，不是理论风险：

1. **坐标系有两套，转换函数是成对的。** `DisplayToPdfSpace` / `PdfToDisplaySpace`（在 `PdfiumEngine`）与 `PdfExportService.ToPdfUserSpace` 必须保持一致，改了其中一个就要同步另一个。`--selftest` 里有旋转页的用例专门盯这个。

2. **文字标注必须写 `/QuadPoints`。** 只设 `/Rect` 会生成**完全合法但不可见**的注释对象：文件里查得到，渲染什么都不画。

3. **删除页面对象要延迟到导出时。** 当场删除就无法撤销了（对象一旦从 PDFium 文档移除就回不来）。`TextReplacement.HideObjectPath` 就是为此存在，撤销只是丢掉一条记录。

4. **被拒绝的编辑要回滚整个文档，不能回写字符串。** `FPDFText_SetText` 失败时可能已半改写了字形 run，而提取出来的"原文"并不忠实（空格位置会被挪动），写回去等于把整串字形位移。`SetObjectText` 的做法是编辑前 `Save()` 快照，被拒后 `ReloadFrom` 整份重开。

5. **任何改变文档的操作都要走 `PushEdit`。** 否则该操作不可撤销，而用户会以为可以。历史面板的跳转也依赖它维护的线性语义（新编辑清空重做栈）。

6. **选区相关属性务必在 setter 内发通知。** 曾经因为 `CanEditSelection` / `SelectionLabel` 只在部分路径通知，导致双击后编辑框是禁用的 —— 界面看着正常，就是打不进字。

7. **不要在无头模式下弹消息框。** `--selftest` / `--uitest` 里没人能点掉它，进程会挂到超时，看起来像死锁而不是报错。用 `_headless` 判断。

## 开发用后门

- **Ctrl+Shift+W** —— 立即关闭，跳过未保存提示。提示是给用户防丢工作的，开发时反复开关窗口纯属碍事。这个手势不会误触，也不在界面上暴露。
- **`--noconfirm`** —— 启动即禁用该提示，供脚本反复开关。

两者都会丢弃未保存的改动。

## 验证方式

```bash
# 像素级回归：坐标变换、旋转、批注、OCR、导出、撤销
PdfEdit.exe --selftest <某个.pdf> <输出目录>

# 无头 UI 冒烟：XAML、绑定、渲染链路，并输出窗口截图
PdfEdit.exe --uitest <某个.pdf>
```

`--uitest` 会在输出目录留下 `shot_settled.png`。**改界面时先看截图再改代码** —— 有几处问题（编辑框浮在左上角、字溢出框外）数值全是对的，只有看图才发现。
