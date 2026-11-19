# 13 DisplayList 规范（接口级草案）

DisplayList 是 UI 层与渲染后端之间的唯一契约（[02](02-graphics-abstraction.md) 的接缝②），也是最难事后修改的格式。本文定义其语义与编码。

## 基本语义

| 项 | 规定 |
|---|---|
| 坐标 | 逻辑像素，`float32`；x 向右、y 向下；像素中心在 `.5` |
| 变换 | 2D 仿射 `Matrix3x2`；当前变换矩阵作用于其后的所有绘制与裁剪 |
| 缩放 | 逻辑像素 → 设备像素的比例由渲染目标提供，不写入 DisplayList（同一 DisplayList 可在不同 DPI 下重放） |
| 颜色 | 非预乘 RGBA `float32 ×4`，sRGB 编码；后端内部自行预乘 |
| 混合空间 | 默认在 sRGB 编码空间混合（与浏览器的 CSS 渲染一致）；线性空间混合作为渲染目标选项后置 |
| 抗锯齿 | 默认开；逐命令可关。规范要求解析式覆盖率，结果与采样方式无关 |
| 填充规则 | non-zero（默认）/ even-odd，记录在 Path 上 |
| 像素对齐 | DisplayList 不做对齐；对齐是布局阶段的职责，后端按给定几何如实绘制 |
| 绘制顺序 | 严格按命令顺序（画家算法）；后端的任何重排都不得改变可见结果 |

## 状态栈

`Save / Restore` 保存与恢复：当前变换、裁剪。`PushLayer / PopLayer` 隐含一次 Save/Restore。DisplayList 录制结束时栈必须平衡，录制器在 Debug 下校验。

## 命令表

操作码的数值以 `src/AwAUI.Graphics/DisplayLists/DrawOp.cs` 为准（尚未实现的操作码已保留编号）；载荷结构在 `Commands.cs`。

| 操作码 | 载荷 | 说明 |
|---|---|---|
| `Save` / `Restore` | — | |
| `Translate` | dx, dy | 高频，单独成码 |
| `Transform` | Matrix3x2 | 右乘到当前变换 |
| `ClipRect` | Rect, aa | 与当前裁剪求交 |
| `ClipRoundRect` | RoundRect, aa | |
| `ClipPath` | pathRef, aa | |
| `FillRect` | Rect, paintRef | |
| `FillRoundRect` | RoundRect, paintRef | 四角半径独立，支持椭圆角 |
| `StrokeRect` / `StrokeRoundRect` | 形状, paintRef, strokeRef | |
| `DrawBorder` | 外 RoundRect、四边宽度、四边颜色、四边样式 | CSS 边框专用：一条命令表达不等宽、不同色、虚线边框，后端可走快速路径 |
| `DrawBoxShadow` | RoundRect, 偏移, 模糊半径, 扩展, 颜色, inset | CSS 盒阴影专用，可解析求值，免离屏 |
| `DrawLine` | p0, p1, paintRef, strokeRef | |
| `FillPath` / `StrokePath` | pathRef, paintRef, [strokeRef] | |
| `DrawImage` | imageRef, src, dst, 采样方式, 不透明度 | 操作码 34，第七批实现（此前保留）。`DrawingContext.DrawImage(Bitmap image, Rect source, Rect destination, ImageSampling sampling = Linear, float opacity = 1)` 与整图重载 `DrawImage(Bitmap, Rect destination, ...)`；载荷 `DrawImageCmd { int ImageRef; Rect Src; Rect Dst; float Opacity; ImageSampling Sampling; 3 字节填充 }`（44 字节），标志 `AntiAlias`；`enum ImageSampling : byte { Nearest = 0, Linear = 1 }`；读取用 `DisplayListReader.GetImage(in DrawImageCmd)`。录制器按 Canvas 的方式把 Src 裁到位图内并按比例收缩 Dst；丢弃空的或非有限的矩形、不大于 0 或 NaN 的不透明度、已释放或空的位图；大于 1 的不透明度钳制为 1，未知的采样值映射为 `Linear`。采样语义、mip 与缓存见 [03](03-renderer.md)「图片」 |
| `DrawImageNine` | imageRef, 中心区, dst | 九宫格（`border-image`） |
| `DrawGlyphRun` | glyphRunRef, 原点, Paint | 字形已整形定位；操作码 36，载荷 `DrawGlyphRunCmd { int GlyphRunRef; Point Origin; PaintData Paint; }`，纯色同其他命令内联。空 run、字号 0、非有限原点或包围盒、不可见画刷在录制期丢弃 |
| `DrawTextDecoration` | 线段, 粗细, 样式, paintRef | 下划线、删除线、波浪线 |
| `PushLayer` | 边界, 不透明度, 混合模式, filterRef?, maskRef?, backdropFilterRef? | 开启**隔离组**（W3C Compositing 的 isolated group）：组内内容先画在透明表面上，再以组的不透明度与混合模式合成回父表面。后端只有在不透明度为 1、以 SrcOver 合成、且组内没有任何读取目标的绘制（DstIn、DstOut、Clear、Src、Multiply 等，或以非 SrcOver 合成的子图层）时，才可以跳过离屏直接画进父级，可见结果必须相同 |
| `PopLayer` | — | 合成回父目标 |
| `DrawDisplayList` | displayListRef | 嵌入子 DisplayList，子列表在当前变换与裁剪下重放 |
| `DrawCustom` | customRef, 边界 | 后端相关的逃生舱 |
| `Annotation` | 元素 ID, 类别 | 不产生像素；DevTools 用它把命令归属到元素，命中测试与无障碍几何也可使用 |

高频 UI 图元（矩形、圆角矩形、边框、阴影、图片、字形）单独成码是有意的：后端无需从通用路径里「猜」出这些形状即可走快速路径。

## 资源

命令通过索引引用资源表中的不可变资源：

| 资源 | 内容 |
|---|---|
| `Paint` | 画刷 + 混合模式 + 抗锯齿标志 |
| 画刷 | 纯色；线性 / 径向 / 锥形渐变（色标、扩展模式、渐变空间变换）；图像画刷（平铺模式、变换、采样） |
| `Stroke` | 线宽、端点、连接、斜接限制、虚线数组与相位 |
| `Path` | 动词序列 + 点；填充规则；缓存的包围盒与内容哈希 |
| `Image` | 逻辑图像句柄（尺寸、格式、像素或延迟解码源） |
| `GlyphRun` | 字体、字号、字形 ID 数组、位置数组、合成加粗/倾斜标志、可变轴（`AwAUI.Graphics.Text.GlyphRun`，读取用 `DisplayListReader.GetGlyphRun(in DrawGlyphRunCmd)`） |
| `Filter` | 高斯模糊、投影、颜色矩阵、亮度/对比度/饱和度等，可链式组合 |
| `Mask` | Alpha 遮罩（图像或子 DisplayList） |

- 纯色是最常见的画刷，以内联形式编码在命令里，不占资源表。
- **径向渐变的焦点**（第六批）：`RadialGradientBrush` 有 `FocalPoint` / `FocalRadius`（新构造函数 `RadialGradientBrush(center, radius, focalPoint, focalRadius, stops, spread, transform)`，负数或 NaN 的焦点半径取 0；原构造函数把焦点设为圆心、半径 0，`HasFocus` 为假）。语义同 Canvas / SVG 2 的双点锥形渐变：没有有效圆的像素透明，两圆相同时什么都不画。画刷是资源对象，所以命令编码与 `FormatVersion = 1` 不变；将来的导出格式必须编码焦点字段。
- **色标偏移**：NaN 偏移在构造画刷时改为前一个色标的偏移（第一个为 0）；单个色标、±∞ 偏移都不抛异常。
- 资源引用计数随 DisplayList 生命周期管理；后端按资源的内容标识缓存设备对象。
- **位图资源**（第七批）：`Bitmap` 有内容版本号 `Version`（`Invalidate()` 递增），后端按（位图对象，`Version`）缓存派生的 mip 与纹理。`ContentHash` 混入的是**录制时**的 `Version`：录制之后再 `Invalidate`（动画复用同一张位图）不会改变已有列表的哈希，所以按 `ContentHash` 缓存渲染结果的代码（将来的保留层）还必须比较所引用位图的当前版本。DisplayList 持有位图但不拥有它；`Bitmap` 有终结器作为保底，仍可能被渲染的列表引用的位图不要 Dispose（生命周期规则见 [03](03-renderer.md)「图片」）。操作码 34 早已保留、旧读取器按大小跳过未知操作码，所以 `FormatVersion` 仍为 1；将来的导出格式必须编码位图资源。

## 二进制编码

```
DisplayList := Header · CommandStream · DataBlob · ResourceTable

Header        魔数、格式版本、包围盒、标志（是否不透明、是否含文字、是否含 Custom）、
              命令数、内容哈希
CommandStream 命令序列，每条 4 字节对齐：
              [op: u8][flags: u8][size: u16（以 4 字节为单位）][payload ...]
DataBlob      变长数据（虚线数组、超长载荷），由命令以偏移引用
ResourceTable 资源引用数组
```

- 小端序；载荷为固定布局的 blittable 结构体，读取即 `MemoryMarshal.Cast`，无解析开销。
- 每隔若干命令插入一条带包围盒的**块标记**，后端与 DevTools 可据此快速跳过脏区之外的命令。
- **序列化**：进程内资源表存对象引用；导出时资源被编码为内联数据或外部引用（图片、字体按内容哈希去重）。导出格式用于远程 DevTools、快照测试、问题复现。
- **版本**：格式版本号单调递增；新增操作码不破坏旧读取器对已知命令的解释，未知操作码按 `size` 跳过并通过诊断通道上报。

## 读取 API

```csharp
public readonly ref struct DisplayListReader
{
    public bool MoveNext();
    public DrawOp Op { get; }
    public ref readonly T Payload<T>() where T : unmanaged;
    public ReadOnlySpan<byte> Data(DataRef r);
}
```

后端解释器的典型结构是一个 `switch (reader.Op)`；软件后端与 GPU 后端各有一份，`NullBackend` 的那份只做统计。DevTools 的逐命令回放就是「执行到第 N 条为止」。

## 不变量（后端可以依赖，录制器必须保证）

1. 状态栈平衡；`PopLayer` 与 `PushLayer` 严格配对。
2. 所有几何为有限值；空矩形与零面积路径的命令在录制期即被丢弃。
3. 包围盒是保守的（实际绘制不超出），包含描边、阴影与滤镜外扩。已知例外：`GlyphRun.Bounds` 取自字体声明的 'head' 包围盒，墨迹超出声明框的字体会被低估；软件后端缓存路径把 y 对齐到整像素、x 取 1/4 px，还可能再超出至多 0.5 设备像素。损伤跟踪开始使用 `DisplayList.Bounds` 之前需要修正（录制期不知道设备缩放，可考虑把记录的包围盒外扩约 1 逻辑像素）。第六批的 UI 损伤跟踪没有用 `DisplayList.Bounds`，而是用元素墨迹加按 em 估计的字形溢出（见 [05](05-ui-core.md)「增量绘制」）。
4. 冻结后的 DisplayList 及其资源不可变，可跨线程读取。
5. 嵌套 DisplayList 不成环。

## 后端上限（恶意输入）

DisplayList 可以来自不可信的生产者（SVG、远程 DevTools、问题复现），后端必须对任意合法编码的输入在有界的时间与内存内完成，超出时跳过并经 `RenderBackend.Degraded` 上报（每帧按信息去重），不得抛异常。自研的两个后端共用以下上限（数值与细节见 [03](03-renderer.md)）：`DrawDisplayList` 嵌套 256 层；每帧重放 2^22 条命令；每帧工作量 2^30（混合像素 + 光栅化面积 + 点数 × 条带数，用来挡住嵌套扇出出来的大量昂贵命令）；同时打开的离屏图层 32 个；每次展平新增 2^20 个曲线点；每次描边 2^20 个弧点、2^18 段虚线（之后改画实线）；阴影 σ ≤ 4096 设备像素；非有限的阴影参数忽略。GPU 后端另有每帧实例像素 2^34、实例数 2^20。工作量上限只把代价限制在约一个上限的量级，不能阻止恶意内容把帧率压低。

## 一致性测试

规范伴随一组**参考场景**（覆盖每个操作码及其边界情况）与期望图像。任何后端 —— 自研的软件、GPU 后端，以及将来的第三方适配 —— 以通过这组场景为「符合规范」的定义；软件后端的输出即参考实现。
