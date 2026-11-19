# 02 绘制抽象与后端接入

本文回答核心问题：**自绘用 C# 自己实现，但又要能接入其他绘制引擎，抽象怎么切？**

> 现阶段只实现自研后端（软件 + GPU）。下文提到的 Skia、Direct2D、Canvas2D 仅用于说明抽象的承载能力，不在实现计划内。

## 三道接缝

```
   盒绘制器(按 CSS 画背景/边框/阴影) · 文本 · Canvas 元素
                 │   接缝① 录制 API（面向控件作者，稳定）
                 ▼
        DisplayList（框架自有二进制命令流）
                 │   接缝② 渲染后端 RenderBackend（面向引擎接入者）
        ┌────────┼─────────────────┐
        ▼        ▼                 ▼
  软件后端    GPU 后端      (将来：其他引擎)
                 │   接缝③ GPU 设备 GpuDevice（面向图形 API 接入者）
            ┌────┼─────┬──────┐
            ▼    ▼     ▼      ▼
          Metal D3D11 GLES  Vulkan/WebGPU
```

| 接缝 | 谁实现 | 换掉它意味着 |
|---|---|---|
| ① `DrawingContext` | 框架唯一实现（录制器） | 不可替换。它不是接口而是具体类，保证录制零虚调用开销 |
| ② `RenderBackend` | 自研软件后端、自研 GPU 后端；将来的其他引擎 | 整个光栅化与合成引擎换掉 |
| ③ `GpuDevice` | 每种图形 API 一个 | 自研渲染器跑在新的图形 API 上 |

为什么不让控件直接调后端的 Canvas（Avalonia 早期、SkiaSharp 直绘的做法）：

- 中间有 DisplayList 才能做**缓存复用**（未脏元素不重录）、**跨线程渲染**、**DevTools 回放/逐命令检查**、**序列化测试**。
- 后端接口面向「一批命令」而不是「一个个调用」，后端可以做全局排序与批处理。
- 接入新引擎只需写一个「DisplayList 解释器」，工作量可控。

## 接缝① 录制 API

```csharp
public sealed class DrawingContext
{
    // 状态
    public void Save();
    public void Restore();
    public void Transform(in Matrix3x2 m);
    public void ClipRect(in Rect r, bool antialias = true);
    public void ClipRoundRect(in RoundRect rr);
    public void ClipPath(VectorPath path);

    // 图元：UI 高频形状单独成命令，方便后端走快速路径
    public void DrawRect(in Rect r, Paint paint);
    public void DrawRoundRect(in RoundRect rr, Paint paint);
    public void DrawLine(Point a, Point b, Paint paint);
    public void FillPath(VectorPath path, Paint paint);
    public void StrokePath(VectorPath path, Paint paint, Stroke stroke);
    public void DrawImage(Bitmap image, Rect source, Rect destination,
                          ImageSampling sampling = ImageSampling.Linear, float opacity = 1);   // 第七批实现，操作码 34
    public void DrawGlyphRun(GlyphRun run, Point origin, Paint paint);
    public void DrawShadow(in RoundRect shape, in Shadow shadow);   // 盒阴影专用命令

    // 分组 / 效果
    public void PushLayer(in LayerOptions o);   // opacity、blend、mask、filter(blur 等)、backdrop
    public void PopLayer();

    // 嵌套（子元素缓存的 DisplayList 直接引用，不拷贝）
    public void DrawDisplayList(DisplayList child);

    // 逃生舱：后端相关的自定义绘制（见下文）
    public void DrawCustom(ICustomDrawOperation op, in Rect bounds);
}
```

逻辑资源（全部不可变、线程安全、与后端无关）：

| 类型 | 说明 |
|---|---|
| `Paint` | 填充/描边、`Brush`（纯色/线性/径向/锥形渐变/图像）、描边参数、混合模式、抗锯齿 |
| `VectorPath` | 动词 + 点数组；`PathBuilder` 构建；含 fill rule。不叫 `Path`：新项目默认开启隐式 using，会与 `System.IO.Path` 冲突 |
| `Image` | 解码后的像素或延迟解码句柄；后端自行上传纹理。现状：`DrawImage` 直接接受 `Bitmap`，它带内容版本号 `Version` / `Invalidate()`，后端按（位图，版本）缓存 mip 与纹理；`Bitmap` 有释放自有内存的终结器作为保底（DisplayList 与 `SvgDocument` 持有它但不拥有它），见 [03](03-renderer.md)「图片」 |
| `GlyphRun` | `FontFace` + 字号 + glyph id 数组 + 位置数组（已整形）+ 合成粗体/斜体标志 + 可变轴；命名空间 `AwAUI.Graphics.Text` |
| `FontFace` | 可访问原始字体表数据与字形轮廓，任何后端都能据此创建自己的字体对象；命名空间 `AwAUI.Graphics.Text`（实现 `OpenTypeFace` 在 `AwAUI.Text`），API 见 [19](19-text-api.md) |

## DisplayList 格式

完整的命令表、语义与二进制编码见 [13-displaylist.md](13-displaylist.md)，这里只给概要。

- 一块连续字节缓冲：`[opcode:u8][payload]...`，payload 为 blittable 结构；资源通过旁路的资源表索引引用。
- 无逐命令对象分配；录制器从池取缓冲，冻结后只读。
- 每个 DisplayList 带：包围盒、是否不透明、资源引用表、内容哈希（用于缓存与 DevTools diff）。
- 格式带版本号，可序列化到流 —— 用于远程 DevTools、快照测试、bug 复现附件。
- 提供 `DisplayListReader`（`ref struct` 迭代器），后端用 `switch(opcode)` 解释。

```csharp
foreach (ref readonly var cmd in displayList.Read())
{
    switch (cmd.Op)
    {
        case DrawOp.RoundRect: backend.FillRoundRect(cmd.As<RoundRectCmd>()); break;
        ...
    }
}
```

## 接缝② 渲染后端

```csharp
public abstract class RenderBackend : IDisposable
{
    public abstract BackendCapabilities Capabilities { get; }

    // 为一个原生表面（窗口 / Canvas / 离屏位图）创建渲染目标
    public abstract RenderTarget CreateRenderTarget(NativeSurface surface);
    public abstract RenderTarget CreateOffscreenTarget(PixelSize size, double scale);

    // 设备丢失后调用，丢弃全部设备资源
    public abstract void InvalidateDeviceResources();

    // 可选能力：以后新增能力时在这里加带默认实现的虚方法，不破坏已有后端
    public virtual T? GetFeature<T>() where T : class => null;

    public abstract void Dispose();
}

public abstract class RenderTarget : IDisposable
{
    public abstract void Resize(PixelSize size, double scale);

    // 渲染一帧：把 DisplayList 画到目标上（RenderOptions 含清屏色与受损区域）。
    // 层树与 SceneFrame 由 Scene 中的合成器消费，合成器再调用这里（见下文「层合成归谁」）。
    public abstract void Render(DisplayList content, in RenderOptions options);

    // 最近一帧是否已交给表面呈现（第七批）；交换链取不到可绘制纹理时为 false，调用方下一个节拍整帧重画
    public virtual bool LastFramePresented => true;

    // 回读像素（截图、黄金图像测试、DevTools 截屏）
    public abstract void ReadPixels(in PixelRect rect, Span<byte> destination, PixelFormat format);

    public abstract void Dispose();
}
```

丢帧契约（`LastFramePresented`）：每次 `Render` 正常返回时更新，第一次渲染之前为 true；软件与离屏目标恒为 true。它不是错误，不经 `Degraded` 上报；细节与 `UIHost` 的处理见 [03](03-renderer.md)「`MetalRenderer` 与设备丢失回退」与 [05](05-ui-core.md)。

扩展点用抽象类而不是接口：netstandard2.0 没有接口默认实现，接口一旦发布就不能再加成员，而抽象类可以追加带默认实现的虚成员（见 [11](11-compatibility.md)）。

`NativeSurface` 是平台层与后端之间的唯一契约，后端按能力查询自己能用的形态：

```csharp
public abstract class NativeSurface
{
    public abstract PixelSize Size { get; }
    public abstract double Scale { get; }
    public abstract T? GetFeature<T>() where T : class;
    // 例如 IWin32Surface(HWND)、IMetalLayerSurface(CAMetalLayer*)、
    //      IEglSurface、IWebCanvasSurface、IFramebufferSurface(软件呈现)
}
```

「能力特性」是小而稳定的契约，保留为接口。

现状：`IFramebufferSurface`（软件呈现）与 `IMetalLayerSurface { IntPtr MetalLayer }` 都在 Graphics 的 `Backends/Backends.cs`（后者第六批从 `AwAUI.Rhi.Metal` 移入），`AwAUI.MacOS` 的窗口表面实现它时不引用 Metal RHI。一个表面可以同时提供多种能力，由后端按能力选择：macOS 窗口表面同时实现两者，软件后端锁定帧缓冲即为软件呈现，GPU 后端读取 `MetalLayer` 的副作用是窗口切换到 GPU 呈现；没有 CAMetalLayer 时 `GetFeature<IMetalLayerSurface>()` 返回 null（见 [04](04-platform.md)）。

`IFramebufferSurface` 尚未写明的契约：锁定的缓冲在两次 `Lock()` 之间保留上一帧内容（尺寸变化时除外）。`UIHost` 的局部光栅化依赖这一点；macOS、Win32（DIB 只在尺寸变化时重建）、X11、Browser 与 `BitmapRenderTarget` 按代码阅读都满足，但接口文档注释还没写（见 [07](07-roadmap.md) 后续事项）。

### 能力协商与降级

```csharp
[Flags]
public enum BackendCapabilities
{
    None = 0, Gpu = 1, BackdropFilter = 2, BlurFilter = 4,
    RetainedLayers = 8, SubpixelText = 16, ColorGlyphs = 32, WideGamut = 64, ...
}
```

- UI 层与合成器只依据能力位做决策（例如无 `RetainedLayers` 时合成器把层压平后整体提交）。
- 后端遇到不支持的效果必须**可预期地降级**（忽略 backdrop blur、filter 退化为透明度），不得抛异常。降级事件经诊断通道上报，DevTools 可见。

### 层合成归谁

合成（层缓存、合成器动画、脏区管理）有两种归属：

- **方案 A（建议）**：合成逻辑在 `AwAUI.Scene` 中与后端无关，后端只需实现「把 DisplayList 画到某个层目标」「把层目标按变换/透明度贴到父目标」两件事。接入新引擎成本最低。
- 方案 B：整棵层树交给后端自行合成，后端自由度最大但每个后端都要重写合成器。

建议 A，并允许后端通过可选接口 `SceneCompositor` 接管（给将来接系统合成器，如 DirectComposition / CoreAnimation 留口子）。

## 文本：为什么单独切一刀

如果让各后端自己排版，换后端后同一段文字的宽度会变，布局随之改变 —— 这是不可接受的。因此：

```csharp
public abstract class TextEngine          // 默认实现：AwAUI.Text（纯 C#）
{
    public abstract FontManager Fonts { get; }                       // 字体匹配、回退
    public abstract ShapedRun Shape(ReadOnlySpan<char> text, in ShapeOptions o);
    public abstract TextLayout CreateLayout(TextSource source, in ParagraphStyle style, double maxWidth);
}
```

实现时的调整：`TextEngine` 放在 `AwAUI.Text` 而不是 Graphics（它交出的 `ShapedRun`、`TextLayout` 都是 Text 的类型）；整形通过 `Shaper` 属性（`TextShaper`）完成；`CreateLayout(string, ParagraphStyle, float maxWidth)` 是便捷方法，富文本走 `CreateParagraphBuilder`。Graphics 只保留后端需要的 `FontFace` 与 `GlyphRun`。实际 API 见 [19](19-text-api.md)。

- 整形、断行、Bidi、度量 → `TextEngine`，输出与后端无关的 `GlyphRun`。
- 字形光栅化 → 后端。自研后端用自己的轮廓光栅器；Skia 后端用 `FontFace` 的原始数据创建 `SKTypeface` 后按 glyph id 绘制。
- `TextEngine` 本身也可替换，它与渲染后端是**正交**的两个插件点。默认实现即覆盖全部书写系统（见 03）。

系统字体枚举属于平台能力，由 `Platform` 提供 `SystemFontProvider`（返回字体文件路径/数据），`FontManager` 消费。

## 图片编解码

```csharp
public abstract class ImageCodec
{
    public abstract bool CanDecode(ReadOnlySpan<byte> header);
    public abstract ImageInfo ReadInfo(Stream s);
    public abstract void Decode(Stream s, Span<byte> pixels, in DecodeOptions o);
}
```

默认提供纯 C# 的 PNG / JPEG / GIF / WebP / BMP / ICO；注册表式扩展。Web 平台可选注册一个「浏览器解码」实现以减小包体。

现状：上面的 `ImageCodec` 抽象类与注册表尚未实现。`AwAUI.Imaging` 目前是一组静态编解码器（`PngCodec`、`JpegCodec`、`GifCodec`、`WebPCodec`、`BmpCodec`、`IcoCodec`）加按文件头分派的 `ImageDecoder`，输出 `Bitmap`；动画走 `ImageDecoder.DecodeAnimation → AnimatedImage`（见 [03](03-renderer.md)「图片」）。DisplayList 的 `DrawImage`（操作码 34）第七批已在两个后端实现，UI 的 `Image` 元素经它画出位图像素（见 [03](03-renderer.md)「图片」、[13](13-displaylist.md)）。SVG 的 `<image>` 经抽象类 `SvgImageResolver` 解码，Svg 不引用 Imaging，实现（`ImagingSvgImageResolver`）在 UI。

## 逃生舱：后端相关的自定义绘制

少数场景（嵌入 3D 视图、视频帧、用户想直接用 SKCanvas）需要绕过 DisplayList：

```csharp
public interface ICustomDrawOperation
{
    // 后端把自己的上下文以特性对象的形式交出来，调用方自行判断
    void Render(ICustomDrawContext ctx);   // ctx.TryGetFeature<ISkiaCanvasFeature>() 等
    bool HitTest(Point p);
}
```

使用它的代码自然绑定到特定后端，这是显式选择；框架自身与标准控件库禁止使用。

## 验证抽象是否成立

只有一家实现的抽象容易走形。在不引入第三方引擎的前提下，用以下手段约束：

1. **软件后端与 GPU 后端各自独立实现 `RenderBackend`**（共享光栅器代码，但不共享后端接口之下的结构），二者能力差异很大（有无保留层、有无滤镜加速），天然检验能力协商与降级路径。
2. **`NullBackend`**：只统计命令，用于基准测试与 Headless，同时保证接口不隐含「一定有像素」的假设。
3. **纸面推演**：接口每次变更时，对照「如果用 Skia / Canvas2D 实现这个接口要怎么写」做评审，发现只有自研实现才满足的隐含假设即视为设计缺陷。
4. DisplayList 格式与后端接口单独版本化，为将来的第三方后端保留稳定契约。
