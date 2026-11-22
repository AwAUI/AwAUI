# 03 自研渲染器（AwRenderer）

`RenderBackend` 的默认实现，全部 C#。分三块：CPU 光栅器、GPU 渲染管线、RHI（图形 API 抽象）。文本栈见文末。

## 总体策略（决策 D8，已定）

全 GPU 路径渲染（Vello / Pathfinder 路线）依赖计算着色器，WebGL2 与 GLES 3.0 上不可用，且实现复杂度很高。UI 场景的绘制内容高度集中：矩形、圆角矩形、边框、阴影、图片、文字占 95% 以上。因此建议混合路线：

| 内容 | 路径 |
|---|---|
| 矩形 / 圆角矩形 / 边框 / 盒阴影 / 渐变 | **GPU 快速路径**：实例化四边形 + 片元着色器内 SDF 解析抗锯齿 |
| 图片 | GPU 纹理四边形 |
| 文字 | CPU 光栅化字形 → 字形图集（A8 / RGBA 彩色）→ GPU 实例化 |
| 任意路径（图标、图表、自定义形状） | CPU 光栅化覆盖率掩码 → 掩码图集 → GPU 着色；带缓存键（路径哈希 + 缩放） |
| 层 / 滤镜 | 离屏纹理 + 全屏/分块 pass |

好处：

- **CPU 光栅器只写一遍**，同时服务于「纯软件后端」「GPU 后端的掩码生成」「字形光栅化」三处，结果像素级一致。
- 着色器集合很小（约 8–10 个），手写多语言版本可行。
- 只要求 GLES 3.0 / WebGL2 级别的 GPU 能力，覆盖所有目标平台。
- 纯软件模式天然存在：没有 GPU / 驱动黑名单 / Headless 测试时直接用。

代价：超大或每帧变化的复杂路径（如全屏动态矢量动画）会受 CPU 光栅化制约。缓解：稀疏条带（sparse strips）只为边缘像素生成掩码，内部实心区域走 GPU 矩形填充；将来可在支持计算着色器的设备上增加全 GPU 路径作为可选优化，不影响架构。

## CPU 光栅器（AwAUI.Render.Raster）

管线：`Path → 变换 → 展平(曲线细分) → 描边展开(若描边) → 边表 → 覆盖率累积 → 着色/混合`

- **覆盖率算法**：有符号面积累积（font-rs / tiny-skia 思路），解析式抗锯齿，无需超采样；支持 non-zero 与 even-odd。
- **稀疏条带**：按 tile 行输出「边缘段 alpha + 实心 span」，既是软件混合的输入，也是 GPU 掩码图集的输入。
- **描边器**：join（miter/round/bevel）、cap、虚线；输出为填充路径。
- **着色**：纯色、渐变（预计算 LUT）、图像采样（最近/双线性）；预乘 alpha，线性/sRGB 混合可配置。
- **SIMD**：三档内核 —— 标量（基线）、`System.Numerics.Vector<T>`（netstandard2.0 / .NET Framework）、`Vector128/256` 硬件内建（net8.0+，命中 SSE/AVX、NEON、WASM SIMD）。三档输出必须逐像素一致，共用同一套测试（见 [11](11-compatibility.md)）。
- **并行**：按 tile 行分带并行（单线程模式下顺序执行）。
- **零分配**：边表、累积缓冲全部池化。
- **恶意输入的边界**（M1 终审 + 第六批加固；软件与 GPU 后端共用，常量在 `RasterContext`、`Stroker`、`Flattener`、`ShadowRasterizer`，GPU 的 `FrameRecorder` 引用同一组常量）。超出任何一项都不抛异常，而是经 `RenderBackend.Degraded` 上报降级；两个后端都按帧去重（同一帧内相同的信息只报一次，每帧最多 1024 种），否则扇出的输入可在一帧内触发数百万次事件：

| 上限 | 值 | 超出时 |
|---|---|---|
| `DrawDisplayList` 嵌套深度（`MaxNestingDepth`） | 256 | 更深的子列表跳过 |
| 每帧重放的命令数（`MaxCommandsPerFrame`） | 2^22 | 本帧其余命令跳过（每层两次引用同一子列表、40 层的输入否则要重放 2^40 次） |
| **每帧工作量**（`MaxWorkPerFrame`，评审新增） | 2^30 | 本帧其余命令跳过。单位 = 混合的像素数（`FillSink.Pixels`）+ 光栅化区域面积 + 点数 × 16 行条带数（`Rasterizer.Work`），约等于 1280×880 全屏重画 1000 次；全部黄金场景在 2x 下不到它的 1/1000（有测试守护）。只靠命令数挡不住「命令少但每条都贵」的扇出：2^14 次 640×440 半透明全屏填充在未优化构建中就超过 80 s CPU |
| 同时打开的离屏图层（`MaxLayerDepth`） | 32 | 更深的图层直接画进父级 |
| 每次展平新增的曲线点（`Flattener.MaxSubdivisionPoints`） | 2^20 | 完全落在（外扩的）裁剪区外的曲线换成弦，可见像素不变；虚线描边不做弦替换（会改变可见部分的弧长与相位）。用尽预算目前**不**上报降级 |
| 每次描边的圆角连接 / 线帽弧点（`Stroker.MaxArcPoints`） | 2^20 | 同上，不上报 |
| 每次描边的虚线段（`Stroker.MaxDashPieces`） | 2^18 | 改画实线并上报（此前 2^20 段时是静默截断） |
| 阴影 σ（`ShadowRasterizer.MaxShadowSigma`） | 4096 设备像素 | 钳制并上报；工作区超过 4M 像素时在 1/k 分辨率下光栅化并模糊，k ≥ σ/8（巨大旋转模糊从 5 s 以上降到 42 ms，σ = 120 时与全分辨率差 ≤ 2/255） |
| 非有限的阴影参数 | — | 忽略该阴影 |

其余加固：`DrawLine` 在 x 为 NaN 时也钳制；路径包围盒拒绝非有限点；单个色标、NaN 或 ±∞ 偏移的渐变不再抛异常或产生 NaN 颜色（`GradientBrush` 构造时把 NaN 偏移改为前一个偏移，第一个为 0）；虚线按裁剪区在局部空间剔除（1e6 px 的点线边框从 6.6 s / 828 MB 降到 17 ms / 0.2 MB），弧长用 double 累加。工作量上限只把扇出攻击的代价限制在约一个上限的量级（优化构建估计为秒级），页面每次重绘都会重新付出，恶意内容仍能把帧率压到个位数；2^30 是按「正常内容的百倍余量」选的，没有在空闲机器上标定对应的耗时。

- **渐变查找表**（`GradientLut`）：一次前向扫描建 256 项表，与原来的逐项查找逐位一致；每个渲染目标按色标内容缓存（256 项 LRU，同一画刷对象有快速路径）。渐变表与字形缓存的键用 NaN 自等的比较与每进程随机种子（`KeyHash.Seed`）。`HashCodeCompat.Combine`（第七批）是非线性的：net8+ 直接用 `System.HashCode`（每进程随机，只在进程内稳定，仓库里没有持久化哈希或按哈希排序输出的地方），netstandard2.0 用带种子的确定性链 `Step(h, v) = fmix32(rotl(h·P1, 13) + v·P2)`。原来的「异或再乘」对最高位的差异是线性的，可以离线构造与种子无关的碰撞（`KeyHash.Hash(Color)` 的 2000 对符号翻转颜色全部碰撞，现在 0 对；`KeyHashTests`）。
- **双点锥形渐变**：`RadialGradientBrush` 有焦点（`FocalPoint` / `FocalRadius` / `HasFocus`），语义同 Canvas / SVG 2 的双点锥形。软件着色器在归一化坐标上按像素用 double 解 a·t² − 2b·t + c = 0，取 r ≥ 0 的最大根；|a| < 1e-5 时按「焦点恰在结束圆上」解一次方程（焦点背后的半平面透明，同 Skia 的近似）；无解的像素透明。不带焦点的径向渐变保持原来的 SIMD 路径，逐字节不变。已知：焦点圆不在结束圆内（锥形情形）时锥的边缘两个后端都没有抗锯齿（Canvas 实现同样如此）。
- **图层隔离**（`LayerIsolation`，两个后端共用）：`PushLayer` 是隔离组；不透明度 1、以 SrcOver 合成的图层，只有组内某处读取目标（DstIn、DstOut、Clear、Src、Multiply 等，或子图层以非 SrcOver 合成）时才画到离屏，否则直接画进父级。每个 DisplayList 扫描一次，结果按对象缓存（`ConditionalWeakTable`，嵌套深度 256 以外按保守处理）。实测（1280×880 示例场景，负载下）：逐层判定的开销测不出来；一个隔离的全屏图层要多 3–6 ms，4 层嵌套 32–38 ms（跳过时约 14.5 ms），所以保留逐层判定。帧末仍未关闭的图层会被合成并释放。已知：图层合成只应用内层裁剪的矩形，不应用圆角或路径裁剪蒙版（只有在非矩形裁剪下以读目标的模式合成时可见）。
- **与裁剪无关**（第七批）：软件光栅器在裁剪区内的输出与裁剪矩形无关，局部光栅化（只画损伤矩形）与整帧光栅化逐字节一致，没有容差。此前有三处依赖裁剪，已在源头修正：
  - 剔除、光栅器的累积坐标系（列原点、左右边的拆分、16 行条带的原点）与描边 / 虚线剔除都由整个根目标（`RasterContext._window`）决定，而不是当前裁剪。`Rasterizer.Fill(poly, rule, aa, clip, window, sink)` 接收这个规范窗口；旧重载令 window = clip，供光栅化到自己矩形里的 GPU 掩码与字形掩码使用。
  - 渐变按绝对列下标求值，而不是相对本行第一个绘制的像素。
  - 盒阴影的工作区对齐到降采样因子 k 的绝对倍数，边距覆盖整个模糊支撑，盒式模糊用 2^20 定点，上采样用绝对坐标，低分辨率路径与 k 按整个阴影范围选择。

  代价：任意路径的每一行从形状左缘（以目标宽度为界）开始累积，而不只是裁剪宽度；矩形、字形、图片是逐像素的，不受影响。负载下的未优化 A/B 测不出回归（噪声大）。保证只相对根渲染目标成立：目标尺寸本身变化时结果可以不同，这与局部光栅无关。GPU 后端没有做成与裁剪无关（掩码仍按裁剪区生成）。验证：`ClipIndependenceTests`（默认 40 个种子 + 4 个阴影用例，`AWAUI_CLIP_SEEDS=N` 加大，跑过 600），撤掉任一修复都会失败；UI 的 `IncrementalDifferentialTests` 与 `IncrementalPaintTests`（阴影用例 1x / 1.5x / 2x）容差都已收紧到 0。存储的黄金图像只在容差内变化：box-shadows 1x 最大差 1、2x 最大差 2，gradients 与 layer-isolation@2x 最大差 1，其余场景逐字节不变。

软件呈现：光栅器输出到 `IFramebufferSurface`（平台层提供可锁定的像素缓冲：Win32 DIB、X11 XImage/SHM、Wayland shm、Android `ANativeWindow_lock`、Web `putImageData`、macOS/iOS `CGImage`→layer contents）。

## GPU 渲染管线（AwAUI.Render.Gpu）

```
SceneFrame
  → 层遍历：确定每层的目标（屏幕 / 离屏纹理）与脏区
  → DisplayList 解释：命令 → DrawItem（图元类型、包围盒、材质键、z 序）
  → 剔除（裁剪矩形、脏区、不透明遮挡）
  → 资源准备：字形/路径掩码光栅化入图集，图片上传，渐变 LUT
  → 批处理：按（管线, 纹理, 混合, 裁剪）合批，保持绘制顺序语义
  → RenderPass 编码 → GpuDevice
```

要点：

- **统一实例格式**：大多数图元是「一个四边形 + 一段参数」，一个通用实例结构 + 少量着色器变体即可覆盖，批次很大。
- **裁剪**：矩形裁剪用 scissor；圆角矩形裁剪在着色器内用 SDF 相乘；任意路径裁剪退化为掩码层。
- **图集**：字形图集（A8）、彩色图集（RGBA，Emoji/小图）、路径掩码图集；架子式分配 + LRU 淘汰；满了就开新页。
- **层缓存**：`PushLayer` 与合成器提升的层渲染到离屏纹理，未脏时跨帧复用。
- **滤镜**：高斯模糊（分离卷积 + 降采样）、阴影（圆角矩形阴影用解析近似，免离屏）、backdrop（需读回目标）。
- **设备丢失**：全部 GPU 资源可从逻辑资源重建。

### 实现现状（Metal，第五批；第六批合入）

代码在 `AwAUI.Render` 的 `Gpu/`（命名空间 `AwAUI.Render.Gpu`，以及 `.Atlas`、`.Frame`）与 `Rhi/`（命名空间 `AwAUI.Render.Rhi`）。第五批的临时项目 `AwAUI.Render.Gpu`、`AwAUI.Rhi` 已删除，GPU 后端里复制的 CPU 光栅器文件也已删除，修复并入原件：`FrameRecorder` 直接使用 `Raster/` 的 `Rasterizer`、`PolyList`、`Flattener`、`Stroker`、`MaskSink` / `ByteMaskSink`、`Kernels`（三档 SIMD）、`GlyphCache`、`GlyphRasterizer`、`ShadowRasterizer`（旋转阴影的 CPU 回退，取代 GPU 自己那份 σ ≤ 128 的副本）、`GradientLut` / `StopsKey`（GPU 量化同一张表）、`KeyHash` 与 `LayerIsolation`。`AwAUI.Rhi.Metal` 是独立程序集，引用 `AwAUI.Render`。全部为通用 TFM、无反射。公开类型：`GpuRenderBackend : RenderBackend`（`Capabilities = Gpu | AdvancedBlendModes`，可从 `NativeSurface` 或 `GpuSwapchain` 建目标）、`GpuRenderTarget`（`LastFrameStats`、`WaitForGpu()`）、`GpuOffscreenRenderTarget`（暴露 `Texture`）、`GpuFrameStats`。

| 方面 | 实现 |
|---|---|
| 着色器 | 一个 Quad 管线、一份「超级着色器」：形状种类（矩形、圆角矩形、外圈 Ring、A8 掩码、图层、外阴影、内阴影）与画刷种类（纯色、线性、径向、锥形）都是逐实例参数。四边形在顶点着色器中由 `vertex_id` 生成，不用顶点缓冲 |
| 覆盖率 | 轴对齐矩形用精确面积（与软件 `FillRect` 相同）；圆角按像素中心到椭圆的距离加半平面的单位方格覆盖率；无虚线的描边矩形、圆形角圆角矩形描边按「外形减内形」解析绘制；盒阴影直边用 erf 精确积分、角带分 12 段积分；旋转形状的阴影回退到 CPU 模糊成掩码 |
| 路径、字形、裁剪蒙版 | 复用 CPU 光栅器，与软件后端逐字节一致。放进 2048×2048 的 A8 图集页（skyline 装箱）；淘汰按页进行，选「上一帧使用字节最少」的页，当前帧用到的页不淘汰；大于一页的掩码用只活一帧的页，超出常规页数的部分每帧上限 256 MB，再多则丢弃并上报降级。路径掩码按内容（动词、点、变换、描边、区域）跨帧缓存，每帧重建的 DisplayList 也能命中；字形缓存与软件后端同键，页被淘汰后从保存的字节重新上传 |
| 裁剪 | 像素对齐的矩形裁剪并入四边形边界（不改 scissor）；只有缩放 + 平移时的抗锯齿矩形 / 圆角矩形裁剪成为每帧一个数组中的解析裁剪形状；其他裁剪（或嵌在其中的非矩形裁剪）用 CPU 算法生成浮点蒙版，每帧上传一次（还没有裁剪蒙版缓存）。示例场景的静态帧 CPU 侧因此从 1.6 ms 降到 0.013 ms |
| 渐变 | 每个渐变一行 256 项查找表，与软件后端同一张表（`GradientLut`）；按色标内容做键、O(1) LRU；设备逆变换折成每实例两行仿射。双点锥形渐变为 `QuadPaint.TwoPointConical = 4`：`Extra = (cd.x, cd.y, r0, dr)`、`Paint1.w = a`，归一化坐标，a 的绝对值小于 1e-5 时解一次方程；参数与软件后端相同但用 float，着色器按 fast math 编译，所以「无解」用 bool 表示而不是 NaN。与软件后端最大差 1/255 |
| 图片（第七批） | `QuadPaint.Image = 5`，纹理绑定在渐变表的槽位 1，着色器用 `read()` 按整数纹素坐标采样（不用硬件采样器，源矩形钳制是精确的），语义与软件的 `ImageShader` 相同（见下文「图片」）。纹理缓存 `ImageTextures` 见「图片」一节。与软件后端对照：image-sampling、image-downscale 最大差 1/255，旋转与图层下（image-transforms）最大差 7/255 |
| 批处理 | 只有绑定的纹理变化才断批；实例不读取的槽位与任何绑定兼容，所以同一图集页与 LUT 页上的掩码、字形、解析形状、渐变能合成一次 draw；从不重排绘制顺序。静态示例帧 1–3 次 draw |
| 图层与混合 | 是否需要离屏按 `LayerIsolation` 判定（同软件后端，见上文）。需要时 `PushLayer` 渲染到池化的离屏纹理，与父级交错编码（父级的 render pass 延迟打开），合成后立即归还，同时占用的纹理数取决于嵌套深度；离屏嵌套上限 32，更深的直接画进父级并上报降级。SrcOver 以外的混合模式先拷贝目标区域，再在着色器里按 W3C 可分离公式计算（固定功能为 Replace），不重叠的实例成组；四种不可分离模式与软件后端一样回退为 SrcOver。离屏目标的局部重绘用 Replace 四边形清理脏矩形；交换链帧总是整帧重画 |
| 恶意输入的边界 | 与软件后端共用上文 CPU 光栅器一节的上限表（嵌套、命令数、图层、展平、弧点、虚线、σ）；GPU 另有三项每帧上限：CPU 掩码光栅化工作量 2^30（与软件后端同一个 `Rasterizer.Work` 计数）、实例覆盖的像素总数 2^34（`MaxInstancePixelsPerFrame`，GPU 单像素成本约为 CPU 的百分之一，所以放宽 16 倍）、实例数 2^20（`MaxInstancesPerFrame`，实例缓冲约 180 MB），计数在唯一出口 `Emit` 里按实例包围盒累加；另有 LUT 页 64、目标拷贝组 64 个实例。超出时跳过本帧其余命令，经 `RenderBackend.Degraded` 上报（按帧去重），不抛异常 |
| 缓存键 | 渐变色标与路径掩码的键用 NaN 自等的浮点比较（`float.Equals`）与每进程随机种子的哈希（同 `TextEngine`），避免 NaN 键使缓存无界增长、避免哈希碰撞攻击。编码中途失败的帧会清空图集、渐变表与路径掩码缓存并归还池化纹理，后续帧不会采样到未上传的内容 |

与软件后端的对照（`GpuParityTests`，逐场景比较）：默认阈值为最大差 64/255、差值超过 2/255 的像素 ≤ 3%、超过 8/255 的 ≤ 0.3%、超过 24/255 的 ≤ 0.02%；ring-strokes 场景放宽为 96/255、1%、0.15%，不抗锯齿场景另设阈值。实际结果见 [07](07-roadmap.md) 第五批。已知差异：

- 软件后端把曲线展平为 0.05 px 的多边形，圆角的抗锯齿相差可达约 12/255。
- **描边内边缘**：软件光栅器逐像素截断绕数，描边的连接处与曲线描边的内侧会过填；GPU 的解析外圈在几何上正确，圆角矩形描边上相差可达 50/255（1x）/ 80/255（2x）。
- 几乎正好穿过像素中心的不抗锯齿边可能翻转个别像素（两个后端都按 0.5 取阈值）。

尚未实现：`DrawBorder`、滤镜与 backdrop、彩色字形、保留层与 Scene 合成器集成（`RetainedLayers` 未置位）、线性空间混合、MSAA、`DrawCustom`。Apple GPU 可以直接读帧缓冲，目前的混合模式仍走「断 pass + 拷贝」。只在 Apple Silicon（M5）上测试过。

## RHI（接缝③）

一个刻意做小的图形 API 抽象 —— 只覆盖 2D 渲染器实际需要的子集，不追求通用：

```csharp
public abstract class GpuDevice : IDisposable
{
    public abstract GpuCapabilities Capabilities { get; }
    public abstract GpuSwapchain CreateSwapchain(NativeSurface surface, in SwapchainDesc d);
    public abstract GpuTexture   CreateTexture(in TextureDesc d);
    public abstract GpuBuffer    CreateBuffer(in BufferDesc d);
    public abstract GpuPipeline  CreatePipeline(in PipelineDesc d);     // 着色器 + 顶点布局 + 混合 + 模板
    public abstract GpuCommandEncoder BeginFrame();
    public event Action? DeviceLost;
    public abstract void Dispose();
}

public abstract class GpuCommandEncoder
{
    public abstract void UploadTexture(GpuTexture t, in PixelRect region, ReadOnlySpan<byte> data);
    public abstract void UpdateBuffer(GpuBuffer b, ReadOnlySpan<byte> data);
    public abstract GpuRenderPass BeginPass(in RenderPassDesc d);
    public abstract void Submit();
}

public abstract class GpuRenderPass
{
    public abstract void SetPipeline(GpuPipeline p);
    public abstract void SetScissor(in PixelRect r);
    public abstract void Bind(int slot, GpuTexture t, SamplerMode s);
    public abstract void BindUniforms(ReadOnlySpan<byte> data);
    public abstract void DrawInstanced(GpuBuffer quad, GpuBuffer instances, int first, int count);
    public abstract void End();
}
```

`GpuTexture / GpuBuffer / GpuPipeline / GpuSwapchain` 同为抽象类，由各图形 API 的实现派生。

上面是最初的草案。**第五批实际实现的 RHI**（第六批起位于 `AwAUI.Render` 的 `Rhi/`，命名空间 `AwAUI.Render.Rhi`，遵守「程序集名 = 根命名空间、文件夹为子命名空间」；`AwAUI.Rhi` 这个名字已不存在。只依赖 Foundation、Graphics，不出现任何 Metal 类型）更小 —— 没有 `CreatePipeline`、`CreateBuffer`：

```csharp
public abstract class GpuDevice : IDisposable
{
    public abstract string Name { get; }
    public abstract string Api { get; }                         // "Metal"
    public abstract GpuCapabilities Capabilities { get; }       // MaxTextureSize、SupportsGpuTimestamps
    public abstract GpuTexture CreateTexture(in GpuTextureDesc desc);   // Rgba8Unorm / Bgra8Unorm / R8Unorm；Sampled / RenderTarget
    public abstract GpuSwapchain CreateSwapchain(NativeSurface surface);
    public abstract GpuCommandList BeginCommands();
    public abstract void WaitIdle();
    public event Action? DeviceLost;
}

public abstract class GpuCommandList : IDisposable
{
    public abstract GpuBufferRange Upload(ReadOnlySpan<byte> data, int alignment = 256);   // 写入本帧的上传环
    public abstract void UploadTexture(GpuTexture texture, PixelRect region, ReadOnlySpan<byte> data, int rowBytes);
    public abstract void CopyTexture(GpuTexture source, PixelRect sourceRect, GpuTexture destination, int destinationX, int destinationY);
    public abstract GpuRenderPass BeginRenderPass(in GpuRenderPassDesc desc);   // 目标 + Load / Clear / DontCare + 清屏色
    public abstract GpuReadback ReadTexture(GpuTexture texture, PixelRect rect);
    public abstract void Present(GpuSwapchain swapchain);
    public abstract GpuSubmission Submit();                      // IsCompleted、Wait()、GpuTime
}

public abstract class GpuRenderPass
{
    public abstract void SetPipeline(GpuPipelineKind kind, GpuBlendState blend);   // 目前只有 Quad；Replace / PremultipliedOver
    public abstract void SetScissor(PixelRect rect);
    public abstract void SetTexture(int slot, GpuTexture texture, GpuSamplerFilter filter = GpuSamplerFilter.Nearest);
    public abstract void SetBuffer(int slot, in GpuBufferRange range);
    public abstract void SetUniforms(int slot, ReadOnlySpan<byte> data);
    public abstract void DrawInstanced(int vertexCount, int instanceCount, int firstInstance);
    public abstract void End();                                  // 视口总是整个附件
}
```

- **管线是枚举 + 混合状态**，不是可创建的对象：渲染器只有一个 Quad 管线，各 API 的实现在内部预建好组合。
- **数据契约在 RHI 中**：`GpuQuadInstance`（176 字节 = 11 个 16 字节字段：Bounds、Shape、RadiiX、RadiiY、Color、Paint0、Paint1、Texture、Clip、Extra、Kinds）、`GpuClipShape`（64 字节）、`GpuQuadUniforms`（32 字节）以及 `QuadShape` / `QuadPaint` 常量规定了每个字段的语义，将来 D3D11、GLES 的着色器必须按同一语义实现。纹理槽位：0 覆盖率 / 图层，1 渐变表（图片绘制时为位图纹理，`QuadPaint.Image`，字段语义见其 XML 注释），2 裁剪蒙版，3 目标拷贝（读目标的混合模式），全部按整数纹素读取、不过滤。测试用 `Unsafe.SizeOf` 与 `Marshal.OffsetOf` 检查 C# 布局（任何系统上都运行）。
- **上传环**：`Upload` 写入共享内存的 4 MB 块，块在其命令缓冲完成后回收，最多 3 帧在途；更大的请求按「最小的够用空闲块」复用，空闲池上限 32 MB（`MetalDevice.MaxFreeUploadBytes`），超出时先释放最大的块。纹理用私有存储，经环上的 blit 填充，所以上传按命令顺序生效。
- **Metal**（`AwAUI.Rhi.Metal`）：裸 `objc_msgSend`（只用 blittable 签名，不用返回结构体的消息）+ `MTLCreateSystemDefaultDevice`；完成状态靠轮询 `MTLCommandBuffer.status`，不需要原生回调或 block，所以 netstandard2.0 与 net8+ 的代码路径相同；`IsSupported` / `TryCreate()` 在非 macOS 上返回 false / null，`Create()` 抛 `PlatformNotSupportedException`。`MetalDevice.CreateSwapchain(IntPtr caMetalLayer, PixelSize)` 直接接受 CAMetalLayer；第六批起 `AwAUI.MacOS` 的窗口表面实现 `IMetalLayerSurface`，窗口可以直接用 Metal 呈现（见 [04](04-platform.md)）。

#### `MetalRenderer` 与设备丢失回退（`AwAUI.Rhi.Metal`，第六批）

- **组合根助手**：`MetalRenderer.Create(preferGpu = true)` 返回 `MetalRenderBackend`（GPU）或 `SoftwareRenderBackend`；`TryCreate()` 只返回 GPU 后端或 null（设备创建失败，或构造 `GpuRenderBackend` 时图集、查找表纹理抛 `GpuException`，都返回 null）；`AWAUI_RENDERER=software` 强制软件。用法见 [01](01-architecture.md) 组合根。
- **回退语义**：`MetalRenderBackend` 包装 `GpuRenderBackend`，按需创建软件后端，由内部的 `FallbackRenderTarget` 转发与切换。设备丢失或 `GpuException` 后整个后端**永久**切到软件，不尝试重建 GPU 设备（命令缓冲出错也可能是内容导致的 GPU 超时，反复重建可能成环）。已有目标在下一次 `Render` 时为同一表面改建软件目标并整帧重画当前帧；离屏目标改为同尺寸位图；窗口随之切回帧缓冲呈现。表面不提供 CAMetalLayer 时直接用软件目标并上报降级。`Name` 与 `Capabilities` 随回退状态变化（`IsSoftwareFallback`、`FallbackReason`），所以 `UIHost.BackendName` 与默认的 `PartialRaster` 会跟着变（见 [05](05-ui-core.md)）。
- **诊断**：`SimulateDeviceLoss()`（参照 WebGL 的 `WEBGL_lose_context.loseContext()`，走与命令缓冲出错相同的 DeviceLost 路径）；`LastFrameStats`、`LastDrawableWait`；`CapturePresentedFrames` + `ReadPresentedFrame()` 在同一个命令缓冲里于呈现前把可绘制纹理复制到回读缓冲，用于自检。`MetalSwapchain` 另有 `LastAcquireTime`（`nextDrawable` 的阻塞时间，不是 CPU 工作），并跟随层的 `presentsWithTransaction`：打开时改为提交、`waitUntilScheduled`、在主线程 `[drawable present]`。`Resize` 在尺寸变化且持有「取得后没有呈现」的可绘制对象时先丢弃它（评审修复）。
- 这个类将来可泛化后移入 `AwAUI.Render/Gpu`，供 D3D11 复用。
- **验证范围**：设备丢失只做过模拟，没有真实拔出 eGPU 或驱动重置；「本帧中途丢失」（`Render` 内抛 `GpuException` 或渲染中收到丢失通知）只做了逐行复核，没有直接测试。只在 Apple M5 / macOS 27.2（arm64）上运行过。
- **丢帧契约**（第七批，`RenderTarget.LastFramePresented`）：虚属性，默认 true，所以软件目标与离屏目标不变。每次 `Render` 正常返回时更新（抛异常时不更新），第一次渲染之前为 true。只有交换链目标取不到可绘制纹理（窗口隐藏或最小化、尺寸为 0、`nextDrawable` 超时）时为 false：这一帧什么都没画也没呈现，表面仍显示更早的一帧。`FallbackRenderTarget` 转发它，永久切到软件后恒为 true。为 false 时调用方保留这一帧的损伤、在下一个节拍或 vsync 整帧重画，不要立即循环重试（取可绘制纹理可能已阻塞约 1 秒）。丢帧不是错误，不经 `Degraded` 上报。`UIHost` 已按此处理（`UIHost.DroppedFrames`，见 [05](05-ui-core.md)）；macOS 平台层「重新可见时 `OnResized`」的权宜做法因此可以删除，但**还没有删**（见 [07](07-roadmap.md) 后续事项）。

### 后端矩阵（决策 D9，已定）

| 平台 | 首选 | 回退 | 备注 |
|---|---|---|---|
| Windows | D3D11 | 软件 | D3D12 不必要；D3D11 覆盖面最广、驱动最稳 |
| macOS / iOS | Metal | 软件 | OpenGL 已弃用，不做 |
| Linux | GLES3 / GL3 (EGL) | 软件 | Vulkan 后置 |
| Android | GLES3 (EGL) | 软件 | Vulkan 后置 |
| Web | WebGL2 | Canvas2D 后端 / 软件 | WebGPU 后置 |

首批只做 **Metal、D3D11、GLES3(含 WebGL2)** 三个，即可覆盖全部六个平台。GLES3 与 WebGL2 共用一套渲染逻辑，仅调用层不同（P/Invoke vs JSImport）。

WebGL 调用跨 JS 边界开销大：批处理后每帧 draw call 应控制在几十个量级；顶点/实例数据通过共享的 WASM 线性内存视图传递，避免拷贝。

### 着色器（决策 D10，已定：先手写，后转译器）

着色器是唯一无法直接用 C# 表达的部分 —— GPU 只接受各自的着色语言。渲染器只需要固定的一小组着色器（约 8–10 个）。

- **第一步（建议）**：每个着色器手写 HLSL / MSL / GLSL ES 三份，以字符串嵌入，运行时由**系统自带**的编译器编译（D3DCompile、Metal 运行时、GL 驱动）。构建期脚本校验三份的接口一致。
- **第二步**：实现「C# 子集 → 着色语言」的源生成器，着色器用带特性标记的 C# 静态方法编写，生成三种（将来加上 WGSL / SPIR-V）目标代码。这样渲染器从算法到着色器全部是 C#，同一份着色逻辑还能直接在 CPU 上执行，用作软件后端的参考实现与单元测试。

第二步工作量不小，但与「尽可能用 C#」的方向一致，且随着后端数量增加收益递增；安排在三个首批后端稳定之后。

现状：只有 Metal 一份 MSL 超级着色器（字符串嵌入，运行时由 Metal 编译）；HLSL、GLSL ES 与三份接口一致性校验（`ShaderCheck`）尚未开始。

## 文本栈（AwAUI.Text）

已定：**覆盖全部书写系统，纯 C# 实现**。这相当于用 C# 实现一套与 HarfBuzz 同等能力的整形引擎，是全项目工作量最大的单项，架构上必须按可独立交付的层次拆开。

对外 API 与整形器插件接口见 [19-text-api.md](19-text-api.md)。

### 分层

```
TextLayout（段落）   断行 UAX#14 · 双向 UAX#9 · 对齐 · 行高 · 省略 · 多样式 run
      │
Itemizer（分段）     按 文字(script) / 方向 / 字体回退 / 语言 切分 run
      │
Shaper（整形）       ┌ 各文字整形器（预处理、重排、特性选择）
      │              └ OpenType 布局核心：GSUB / GPOS / GDEF 全部查找类型
      │              （AAT：morx / kerx，Apple 系统字体需要）
Font（字体）         表解析 · cmap · 轮廓(glyf / CFF / CFF2) · 可变字体 · 彩色字形
```

### 整形器

沿用业界成熟的划分方式，每个整形器是一个独立模块，可按需裁剪（对 WASM 包体很重要）：

| 整形器 | 覆盖 |
|---|---|
| Default | 拉丁、西里尔、希腊、CJK 等无需重排的文字 |
| Arabic | 阿拉伯、叙利亚、N'Ko、蒙古文等连写文字（字形联接分析） |
| Hebrew | 希伯来文 |
| Hangul | 谚文字母组合 |
| Indic | 天城、孟加拉、泰米尔、泰卢固等九种印度文字（音节分析与重排） |
| Khmer / Myanmar | 高棉文、缅甸文 |
| Thai / Lao | 泰文、老挝文 |
| Tibetan | 藏文 |
| USE（通用整形引擎） | 其余所有复杂文字（爪哇、巴厘、天城扩展等数十种），按 Unicode 字符类别驱动 |

### 实施顺序

| 阶段 | 内容 |
|---|---|
| P1 | TrueType 表解析与轮廓；cmap + hmtx + kern 的基础整形；断行、字素簇；字体回退；字形图集。拉丁与 CJK 可用 |
| P2 | CFF/CFF2；完整 GSUB/GPOS/GDEF；Bidi；彩色字形（sbix / CBDT / COLRv0/v1） |
| P3 | Arabic、Hebrew、Hangul、Thai 整形器；USE（一次覆盖大量文字） |
| P4 | Indic、Khmer、Myanmar、Tibetan 整形器；可变字体（fvar/gvar/avar/HVAR） |
| P5 | AAT（morx/kerx）；竖排（vert/vrt2、`writing-mode`） |

**进展**：P1 在 M1 完成。第八批完成 P2 的 CFF / CFF2 轮廓与 GSUB / GPOS / GDEF（`OpenTypeShaper`，默认整形器）和 P3 的 Arabic、Hebrew（内部的 `ScriptShaperKind`，没有公开插件接口），见 [19](19-text-api.md)；彩色字形、Hangul / Thai / USE 与 P4、P5 未开始。

### 正确性

- 以公开的整形一致性测试数据（各文字的「输入文本 + 字体 → 期望字形序列与位置」）作为验收标准，逐个整形器达标后才标记为支持。测试数据只是数据文件，不引入原生依赖。
- Unicode 数据表（script、断行类别、bidi 类别、联接类型、印度系音节类别、USE 类别等）由构建期工具从 UCD 生成紧凑 trie，不依赖 ICU。
- Hinting（TrueType 字节码解释器）**不做**：高 DPI 下用无 hinting + 垂直方向网格对齐。

### 渲染相关

- 字形以 A8 覆盖率入图集，按（字体、字号、子像素偏移量化到 1/4 px）缓存；彩色字形入 RGBA 图集。
- 默认灰度抗锯齿；LCD 子像素渲染作为能力位后置。
- 大字号不入图集，走通用路径管线（阈值见下文软件后端的实现）。
- `TextEngine` / `ScriptShaper` 保持为公开扩展点。

**软件后端的实现（M1）**：`RasterContext` 处理 `DrawGlyphRun`（`RasterContext.Glyphs.cs`、`GlyphCache.cs`）。

| 项 | 实现 |
|---|---|
| 快速路径条件 | 变换为均匀正缩放 + 平移，且设备字号 ≤ 128px（`MaxCachedGlyphSize`，让 2x 下 64px 的逻辑字号仍走缓存） |
| 缓存键 | 字型（按对象引用）+ `FontVariation` + glyph id + 设备字号（1/64 px 量化）+ 子像素 x（1/4 px 量化，≥ 0.875 进到下一整像素）+ 样式位（合成粗体、合成斜体、无抗锯齿）+ 合成粗体的设备像素外扩量（1/256 px 量化）。y 对齐到整像素；缩放折进设备字号，所以只有一类变换 |
| 缓存 | 每个 `RasterContext`（即每个渲染目标）一份，LRU，默认上限 8192 项 / 8 MB，每项另计 96 字节开销；对 `FontFace` 持强引用 |
| 合成 | 掩码经 `FillSink.RowA8`（跳过零字节、256 项查找表）进入与其他操作相同的裁剪蒙版、着色、混合路径，所以裁剪、图层、混合模式、不透明度、渐变行为一致。逐字形合成（同 Skia），重叠字形的抗锯齿边缘会混合两次 |
| 回退路径 | 旋转、斜切、非均匀缩放、翻转、设备字号 > 128px、掩码 > 65 536 像素（256×256）：把整条 run 的轮廓在局部空间作为一条路径填充，不缓存。逐字形按「'head' 包围盒 + 1 em」对裁剪区剔除 |
| 合成粗体 | 外扩量按**逻辑字号**计算（`GlyphRun.GetSyntheticBoldExtent`，同 Skia：9px 时 1/24 em 到 36px 及以上 1/32 em），再乘设备缩放，所以粗细与设备像素比无关，缓存路径与回退路径一致。用 (0,0)、(w,0)、(w/2,±w/2) 四份平移副本的非零并集近似轮廓外扩：斜笔画约 0.7w，细于 w/2 的笔画可能出现接缝；不改变 advance |
| 合成斜体 | 轮廓斜切 0.25（约 14°）：x' = x − y·0.25 |
| 恶意输入 | 无效 glyph id、NaN 或超大位置、超大字号或缩放、坐标超大的字体都在分配前用浮点检查，不崩溃也不无界分配 |

三档 SIMD 内核输出逐字节一致（文字场景已加入 `SimdTierTests`）。实测（1280×880 设备像素、约 2000 个 14px 字形、Apple Silicon、`Vector128`，机器同时有其他负载，±15%）：优化构建下 1x 冷帧 4.5 ms、热帧 3.4 ms，同内容走路径填充 14.4 ms；2x 冷 8.7 ms、热 7.1 ms。热帧时间主要花在浮点 SrcOver 混合（每掩码行约 130 ns），纯色 A8 的整数快速路径是下一步优化点（需要三档内核都实现）。

尚未实现：彩色字形（COLR / sbix / CBDT 目前画成普通轮廓，纯位图字形不画；`BackendCapabilities.ColorGlyphs` 未置位）；「正确性」一节所说的垂直方向网格对齐；`OpenTypeFace` 忽略可变轴（P4），尽管 `FontVariation` 已参与缓存键。

## 图片

纯 C# 解码（`AwAUI.Imaging`，只依赖 Foundation、Graphics）。规划中的「单线程模式下分片解码」与「大图按显示尺寸降采样后再上传」未实现。

**UI 侧的接线（`AwAUI.UI/Media`，第六批）**：

- **来源** `ImageSource`（抽象类，相等 = 类型 + `Key`，所以每次 `Build()` 新建的 `FromUri("a.png")` 不会重新加载）：`FromBitmap`、`FromBytes`、`FromStream`、`FromFile`、`FromResource`、`FromUri`、`FromSvg`，字符串隐式转换走 `FromUri` —— `data:` URI 直接解码，其他先交给 `ImageCache.ResourceResolver`，再回退为相对 `ImageCache.BaseDirectory`（默认 `AppContext.BaseDirectory`）的文件路径；无法解码且以 `<` 开头的数据按 SVG 再试。编码输入上限 `ImageCache.MaxEncodedBytes`（默认 64 MB），解码受 Imaging 的 `DecodeLimits` 与 `SvgParseOptions` 约束。特殊来源（网络、加密资源）由应用派生 `ImageSource`。`FromStream`（第七批）在编码字节不超过 `ImageSource.MaxRetainedStreamBytes`（16 MiB）时保留它们，所以同一来源释放后或被另一个根再次加载都可以；超过时不保留，第二次加载报 AW1010 并说明原因，建议改用 `FromBytes` / `FromFile`。读取在锁内进行（多个根可能同时在线程池上解码）。`FromBitmap` 指向用户持有的位图，显示期间不要释放它；改写后调用 `ImageCache.NotifyChanged(source)`（第八批）：位图版本号加一并重绘。对其他来源，`NotifyChanged` 重新加载，旧内容保留到新内容就绪，按加载代数（`ImageEntry.Generation`）丢弃过时的异步结果，被替换的内容进入「绘制之后释放」的队列，仍在光栅化的帧不会引用已释放的位图；`FromSvg` 的文档不可变，调用无效果。
- **缓存** `ImageCache`（每个 `UIRoot` 一份，`root.Images`）：按来源键共享、引用计数，最后一个使用者卸载后释放（位图晚一次绘制再 Dispose）。较大的来源（`ImageSource.PreferAsync`；字节来源以 64 KB 即 `ImageLoadContext.AsyncThresholdBytes` 为界）在线程池解码，经调度器回到 UI 线程；没有调度器时同步解码（测试与单线程无头渲染）。加载期间元素不占空间、不绘制，除非给了 Width / Height 提示；加载失败报 AW1010，第八批起显示「图片损坏」占位与替代文本（规则见 [05](05-ui-core.md)「替换元素的布局」；图标为矢量，灰色相框、山与太阳、一道红色裂痕）。`Loaded` 与 AW1010（第七批）不再在布局中途触发：同步解码时排队，在绘制之后（`ImageCache.AfterPaint` 末尾）或一个投递的调度器回调中触发，以先到者为准，每次加载只触发一次；处理器抛出的异常交给错误边界或 `UnhandledError`，其余通知照常触发。异步路径本来就在布局之外，直接调用处理器。
- **动画**（GIF / WebP 的 `AnimatedImage`）：时间线在缓存项里，显示同一图片的元素共享。每次绘制该图片只安排一个定时器（`UIRoot.Clock`，测试用 `ManualUIClock`），触发时只有上一帧画过它才前进，否则暂停、再次绘制时继续；没有逐帧轮询，静止画面请求 0 帧。≤ 10 ms 的帧延迟按 100 ms 播放（`ImageCache.MinFrameDuration` / `ClampedFrameDuration`），遵守循环次数，迟到的定时器跳帧。帧在 UI 线程上合成，每个动画复用一张 `Bitmap`；每次 `RenderFrame` 改写后在 `finally` 中调用 `Bitmap.Invalidate()`，帧解码失败时也调用并让使用者重绘（像素可能已部分改写），所以局部光栅的软件后端与总是整帧重画的 GPU 后端显示相同的内容。
- **绘制**（第七批）：`ImagePainter.DrawBitmap`（`src/AwAUI.UI/Elements/ReplacedElement.cs`）调用 `dc.DrawImage(bitmap, src, dst, sampling)`。采样由计算后的 CSS `image-rendering`（`CssImageRendering`）决定：`pixelated` 与 `crisp-edges` 为 `Nearest`，其余（`auto`、`smooth`、`high-quality`）为 `Linear`，渲染器的 `Linear` 在大比例缩小时自动用 mip，所以没有单独的高质量模式。只画落在内容盒内的部分：`ObjectFitting.Crop` 给出可见目标矩形与对应的小数源矩形，所以 cover、none 与偏移的 object-position 不发裁剪命令，GPU 也不需要裁剪蒙版（代价：内容盒边缘一行像素与「整图加裁剪」可能略有差别）。位图的目标矩形先按绝对坐标对齐设备像素（`ObjectFitting.SnapToDevice`，同 Blink），SVG 不对齐。元素 opacity 仍由图层施加，DrawImage 的不透明度固定为 1。元素有 `border-radius` 时替换内容（img 与 svg）裁到内容盒的圆角形状（半径 = 外缘 − 边框 − 内边距）。通过 `Image` 显示的 SVG 内容经 `DrawDisplayList` 画出；SVG 里的 `<image>` 见下文。

**`DrawImage`（`AwAUI.Graphics` + 两个后端，第七批）**：

- **API**：`DrawingContext.DrawImage(Bitmap, Rect source, Rect destination, ImageSampling sampling = Linear, float opacity = 1)` 与整图重载 `DrawImage(Bitmap, Rect destination, ...)`；`enum ImageSampling : byte { Nearest = 0, Linear = 1 }`，值是二进制格式的一部分。命令格式见 [13](13-displaylist.md)。
- **采样语义**（两个后端相同）：设备像素中心映射到纹素坐标；`Nearest` 取 ⌊u⌋，`Linear` 的纹素中心在 i + 0.5。纹素下标先在浮点域钳制到 `RoundOut(src) ∩ 位图`，所以源矩形之外的颜色不会渗入。`Linear` 在 ρ ≥ √2 时（ρ = 两个方向中较大的「每设备像素的纹素数」）改用第 L 级，L = round(log2 ρ)；第 L 级是 2^L 方块的精确整数平均，由 CPU 从源矩形的裁剪区构造（`ImagePlan.BuildLevel`，所以 mip 同样不渗色），两个后端共用这份字节；级别之间不混合（没有三线性）。`Nearest` 从不用 mip（像素画）。边缘用解析面积覆盖率，旋转下用矩形路径蒙版；结果乘以不透明度后按 SrcOver 合成。
- **缓存**：软件后端的 mip 缓存（`RasterContext.ImageCacheBudget`，64 MB LRU）与 GPU 的 `ImageTextures`（256 MB LRU）都以（位图，`Version`，区域，级别）为键；位图释放、版本变化或 300 个渲染帧未用时丢弃，至多 4096 项。mip 构造代价计入 `MaxWorkPerFrame`；软件后端每帧新构造的 mip 字节另有上限 `RasterContext.MaxLevelBytesPerFrame` = 256 MB（与 GPU 每帧上传上限相同），超出后本帧其余图片直接从第 0 级采样（不预滤波），图仍然画出，并上报降级。非缩放平移变换下，完全在裁剪区外的图片不构造 mip。GPU 另有：单张纹理不超过 `MaxTextureSize` 与 128 MB，否则第 0 级只上传裁剪区或改用更高级别并上报；每帧上传至多 256 MB，超出的图片跳过并上报（第 0 级也要上传，所以 GPU 在这种已降级的极端情况下与软件结果不同，是有意的取舍）。缓存连同版本持有位图的强引用，身份不会被复用（没有 ABA 问题）；代价是之后不再渲染时缓存会一直持有这些位图，终结器也无法回收被丢弃但未释放的大位图。
- **位图内容版本与生命周期**：`Bitmap.Version`（原子读）、`Invalidate()`、`IsDisposed`；`Clear`、`CopyTo`（目标）与软件渲染进 `BitmapRenderTarget` 自动递增，经 `GetRow` / `Address` 写入后必须显式 `Invalidate`。`Bitmap` 新增终结器，作为释放自有内存的保底（DisplayList 与 `SvgDocument` 持有位图但不拥有它）；`Dispose` 仍立即释放并抑制终结器，包装的外部缓冲从不释放。因此 `Address` / `GetRow` 返回的指针与 Span 不能让位图保持存活，只用指针的调用方需要 `GC.KeepAlive` 或 `using`。仍可能被渲染的 DisplayList 引用的位图不要 Dispose；后端跳过已释放的位图并上报。
- **SVG `<image>`**：`AwAUI.Svg` 新增抽象类 `SvgImageResolver`（`abstract Bitmap? Decode(ReadOnlySpan<byte>, string mediaType)`、`virtual Bitmap? ResolveExternal(string href)`，默认返回 null，不做外部加载），经 `SvgParseOptions.ImageResolver` 传入；Svg 不引用 Imaging，实现在 UI：`ImagingSvgImageResolver`（sealed，`Instance` / `DefaultOptions`），`ImageCache.SvgOptions` 为 null 时使用 `DefaultOptions`，所以 `<img src="x.svg">` 里嵌入的 PNG/JPEG 能显示。`data:` URI（base64 含折行，或百分号编码）在 Svg 内线性解码；`image/svg+xml` 被拒并报诊断；同一个 `<image>` 被 `<use>` 多次引用也只解码一次。支持 x/y/width/height（auto 按固有尺寸与宽高比）、`preserveAspectRatio` 的 meet / slice / none（slice 变成源子矩形，无需裁剪）、`image-rendering` 映射为 `Nearest`、opacity 折进 DrawImage（不开图层）。`SvgParseOptions.MaxImagePixels`（默认 64M）：第一张超出预算的图片被拒之后，其余 `<image>` 不再调用解析器，只上报一次诊断。用户自己解析的 `SvgDocument` 不会自动带上解析器，需要时传入 `ImagingSvgImageResolver.DefaultOptions`；Markup 编译出的内联 svg 第八批起用它解析。`ImagingSvgImageResolver` 解码每张嵌入位图时传 `MaxPixelsPerImage`（64M 像素）作为调用方上限。
- **已知限制**：软件的 `ImageShader` 是逐像素标量代码（没有 SIMD），全屏大图的开销与渐变填充相当或更高，没有做优化构建的吞吐量基准；mip 级别之间不混合，连续缩放在 √2 的倍数处锐度略有跳变，奇数尺寸的级别边缘纹素略有偏移；纹素边界恰好落在像素中心时 `Nearest` 在 CPU 与 GPU 上可能取不同的纹素；GPU 的动画图片每换一个版本新建一张纹理（不复用、不原地更新，1080p 动画每帧约 8 MB）；一帧需要的 mip 超过 64 MB 时 LRU 每帧淘汰再重建；缓存条目只计 mip 或纹理的字节，不计被引用的位图本身；一帧内 GPU 图片纹理可临时超出缓存预算至多一个上传上限（约 512 MB）。UI 层图片的 GPU 与软件对照第八批进了仓库：`Rhi.Metal.Tests` 的 `UiImageParityTests`（项目引用 `AwAUI.UI`，用独立的 `GpuRenderBackend`；场景含 object-fit 五种取值与 object-position、最近邻与双线性、512² 条纹大比例缩小、圆角头像、作为图片的 SVG、内联 SVG 图标、透明度、失败占位），1x / 2x 最大差 19/255 与 14/255，差值超过 8 的像素不到 0.005%、超过 24 的为 0；UI 不支持 `transform`，旋转与缩放采样由 `GpuParityTests` 的 image-transforms 场景在 DisplayList 层覆盖。只在 Apple M5 上运行过，net48 只编译。

| 格式 | 覆盖 | 对照 |
|---|---|---|
| PNG | 全运行时行为一致的 inflate（`DeflateStream` + 手写 zlib 头与 Adler-32） | |
| JPEG | baseline + progressive、任意采样、CMYK / YCCK、EXIF 方向 | libjpeg 逐像素 |
| GIF | 含动画。画布 = max(逻辑屏幕, 第一帧范围)（同 Chrome），后续帧只裁剪不扩大；画布初始透明，处置方式 2 清为透明、3 恢复、4–7 按 0；越界的调色板索引为不透明黑；截断的帧保留已解出的像素，非法 LZW 码抛 `InvalidDataException`（浏览器会显示部分帧）；`LoopCount` = NETSCAPE 的 N+1（0 为无限） | giflib 解出的索引 + 浏览器合成规则 |
| WebP | VP8L（无损）、VP8 + ALPH（有损带 alpha）、VP8X、ANIM / ANMF 动画；有损部分在位读取、预测、反变换、环路滤波、色度上采样与定点 YUV→RGB 上都照搬 libwebp 的行为；动画合成规则同 `WebPAnimDecoder` | libwebp 1.6 逐字节 |
| BMP | OS/2 1.x 与 BITMAPINFOHEADER / V2–V5 头；1/2/4/8 位调色板、16/24/32 位、RLE4 / RLE8、位域；不足 8 位的通道按位复制扩展（同 Firefox）；32 位全零 alpha 视为不透明 | Pillow |
| ICO / CUR | BMP 项（用 AND 蒙版，32 位且 alpha 可用时除外）与 PNG 项；可取最大项或按目标尺寸选项，CUR 给出热点 | Pillow |

- **动画** `AnimatedImage`（`ImageDecoder.DecodeAnimation`；静态格式得到 1 帧）：先完整解析并校验容器，帧像素按需解码、按顺序合成到同一张非预乘 RGBA 画布上，`RenderFrame(i)` 返回应用了处置与混合的整张画布。内存与画布大小成正比，每帧耗时与帧面积成正比；向后跳转（含循环）从第 0 帧重新合成；不是线程安全的。GIF 与动画 WebP 的 `AnimatedImage` 引用调用方的 `byte[]` 并按需解码，调用方不能复用或池化这个缓冲。帧时长按文件原样返回（浏览器把 ≤ 10 ms 当作 100 ms），钳制由播放方负责。
- **解码上限**（内部的 `DecodeLimits`）：一次解码中需要逐像素处理的像素总数 ≤ 4M + 输入字节数 × 4096，超出抛 `NotSupportedException`；GIF 与有损 WebP 按构造不会超出，极易压缩的无损 WebP 与 BMP 的 RLE 可能超出。单张 RGBA 位图不超过 2 GB（约 5.4 亿像素）。第八批起各格式（PNG、JPEG、WebP 含 VP8 / VP8L / VP8X、GIF、BMP、ICO 中的条目）都在读完头部、分配任何与尺寸成正比的缓冲之前检查声明的尺寸（`CheckDimensions`）；与输入大小的关系：PNG 另受 DEFLATE 最大压缩比约束（每输入字节至多约 1032 像素），JPEG 每字节至多 256–512 像素，GIF / WebP / BMP 按上面的像素预算。`ImageDecoder.Decode(byte[] data, long maxPixels)` 让调用方给出更小的像素上限，超出时在分配像素前抛 `NotSupportedException`（经线程静态值传给各解码器，AOT 友好）；`Decode(byte[])` 的默认上限仍允许合法的大图分配到 2 GB，`ImageCache` 解码普通图片时没有传上限。`HostileHeaderTests` 用手工构造的 20–200 字节文件（声明 3000² 与 16383²）覆盖全部格式与三个入口，每项分配 < 256 KB。损坏输入只抛 `InvalidDataException` 或 `NotSupportedException`，内部的越界类异常被包装为 `InvalidDataException`（模糊测试一旦遇到即失败）。
- 尚未实现：色彩管理（ICC、WebP 的 EXIF 方向被忽略）、内嵌 JPEG/PNG 的 BMP、64 位 BMP；WebP 没有 SIMD（无损耗时约为 libwebp 的 2 倍，有损约 3.2 倍）。

## 性能预算（设计约束）

| 指标 | 目标 |
|---|---|
| 典型应用界面一帧（GPU） | CPU 侧 < 4 ms（桌面），draw call < 50 |
| 滚动长列表 | 仅新露出内容光栅化，其余层复用 |
| 空闲 | 0 帧、0 分配 |
| 启动到首帧的托管分配（HelloAot） | < 6 MB（已提交堆实测约 5 MB；其中盒阴影蒙版 2.4 MB，留在缓存里供重绘复用） |
| 几何不变的盒阴影重绘 | 不重新光栅化、不分配（`ShadowRasterizer` 的蒙版缓存，上限约 4 MB） |
| 进程内存（HelloAot，软件渲染） | macOS < 40 MB，Windows 私有内存 < 20 MB；规则见 [01](01-architecture.md)「内存规则」 |
| WASM 首屏（软件 / WebGL2） | AOT 后包体与启动时间单独设预算，待原型实测后定 |
