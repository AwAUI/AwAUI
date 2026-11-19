# 18 场景、合成器与帧调度（接口级）

`AwAUI.Scene` 位于 UI 层与渲染后端之间，负责三件事：把元素树的绘制结果组织成**层树**、在 UI 线程与渲染线程之间**交接帧**、决定**什么时候出帧**。它不依赖具体后端，软件与 GPU 后端共用。

> 现状（第六批）：`AwAUI.Scene` 还不存在。元素级的 DisplayList 缓存与损伤跟踪已在 `AwAUI.UI` 的 `RenderPipeline` 中实现，但没有层树：每个元素的 DisplayList 以 `DrawDisplayList` 嵌入父级，损伤是每帧一个包围矩形（`RenderPipeline.LastDamage`），软件后端只光栅化该矩形，GPU 后端整窗重画（见 [05](05-ui-core.md)「增量绘制」）。`LayerNode`、`SceneFrame`、渲染线程与合成器仍是后续工作；本文其余部分是目标设计。

## 数据模型

```
UI 线程                                   渲染线程
────────                                  ────────
元素树 ──Paint──► LayerNode 树（不可变）
                      │ Commit
                      ▼
                  SceneFrame ─────────►  CompositorTree（持久，跨帧）
                  （不可变快照）            ├ 每层的设备资源（纹理 / 瓦片）
                                          ├ 合成器属性的当前值（变换、透明度、滚动偏移）
                                          └ 运行中的合成器动画与惯性滚动
```

### LayerNode

```csharp
public abstract class LayerNode                 // 不可变；内容没变的层在相邻两帧间是同一个对象
{
    public LayerId Id { get; }                  // 跨帧稳定，由所属元素派生
    public Rect Bounds { get; }                 // 本层坐标系下的内容范围
    public CompositorProps Props { get; }       // 可由合成器独立驱动的属性
    public RoundRect? Clip { get; }
    public ImmutableArray<LayerNode> Children { get; }
    public LayerReasons Reasons { get; }        // 为什么成为独立的层（诊断用）
}

public readonly struct CompositorProps
{
    public Matrix3x2 Transform { get; }
    public float Opacity { get; }
    public Point ScrollOffset { get; }
    public uint Generation { get; }             // UI 线程每次主动改写时递增
}
```

| 层类型 | 内容 | 产生条件 |
|---|---|---|
| `ContentLayer` | 一份 DisplayList | 根层；带合成器动画、`will-change`、`-aw-layer` 的元素 |
| `ScrollLayer` | 视口裁剪 + 可滚动内容子层 + 滚动范围与吸附点 | `overflow: scroll / auto` 且内容溢出 |
| `EffectLayer` | 组透明度、滤镜、混合模式、遮罩 | 这些效果作用于有子内容的元素 |
| `BackdropLayer` | 读取背后内容并施加滤镜 | `backdrop-filter` |
| `SurfaceLayer` | 外部内容（原生表面、视频帧、`DrawCustom`） | 逃生舱 |

`LayerReasons` 是标志集：`Root / CompositorAnimation / Scroll / WillChange / Opacity / Filter / Backdrop / Mask / ClipWithRadius / Fixed / Sticky / Explicit`，DevTools 层面板直接显示。

**不提升为层的元素**把自己的 DisplayList 通过 `DrawDisplayList` 嵌入最近的祖先层。层要占用纹理内存，所以提升条件保守；单帧层数与纹理总量有预算，超出时按「最久未变化的先合并回父层」降级，并上报诊断。

### SceneFrame

```csharp
public sealed class SceneFrame
{
    public long FrameId { get; }
    public PixelSize ViewportSize { get; }
    public double Scale { get; }
    public LayerNode Root { get; }
    public DirtyRegion Damage { get; }                         // 相对上一个已提交帧
    public ImmutableArray<CompositorAnimation> Animations { get; }   // 本帧新启动 / 取消的合成器动画
    public ImmutableArray<ScrollCommand> ScrollCommands { get; }     // scrollTo、平滑滚动、吸附
    public FrameTimings UiTimings { get; }                     // 各 UI 阶段耗时，随帧带到渲染线程汇总
}
```

- **结构共享**：UI 线程只为变化的子树生成新的 `LayerNode`，其余沿用旧对象。合成器用引用相等判断「这一层要不要重新光栅化」，无需比较内容。
- **所有权**：`SceneFrame` 及其引用的 DisplayList、资源在提交后只读。DisplayList 采用引用计数，最后一个引用它的帧在渲染线程退役时释放；设备资源的销毁进入渲染线程的延迟删除队列。

## 帧交接

```
UI 线程:    … Paint → Commit ──► [ 单槽信箱 ] ──► 渲染线程取走 → Composite → Raster → Present
```

- **单槽、后到者覆盖**：渲染线程来不及消费时，新帧替换信箱里的旧帧，同时合并两者的 `Damage`、动画与滚动命令（命令不能丢，像素可以跳）。
- **背压**：信箱非空时 UI 线程照常处理输入与状态，但推迟下一次 Paint/Commit，避免做无用功。
- **不阻塞**：任何一方都不同步等待另一方。唯一的例外是窗口尺寸变化与截图回读，走显式的同步请求，带超时。

### 回传

合成器独立推进的状态需要让 UI 线程知道：

| 回传 | 时机 | 用途 |
|---|---|---|
| 滚动偏移 | 每个合成帧后投递，UI 线程在下一帧 Input 阶段最前读取 | 命中测试、`OnScroll` 事件、粘性定位、虚拟列表决定渲染哪些项 |
| 动画结束 / 取消 | 发生时 | 触发 `transitionend` 等事件，清理状态 |
| 帧呈现时间与耗时 | 每帧 | 性能面板、动态调整预算 |
| 设备丢失 | 发生时 | 触发资源重建 |

**谁说了算**：布局与内容以 UI 线程为准；手势或惯性进行期间，滚动偏移以合成器为准。UI 线程主动改写（`scrollTo`）时递增 `Generation`，合成器看到更大的代数即放弃自己的值；合成器回传的值带着它所基于的代数，UI 线程丢弃过期的回传。

## 合成器

每个合成帧执行：

1. 取信箱中的新 `SceneFrame`（若有），与 `CompositorTree` 对账：新增、移除、内容变化（引用不等）、仅属性变化。
2. 推进合成器动画与惯性滚动，得到各层属性的当前值。
3. 确定需要光栅化的内容：内容变化的层的受损部分、因滚动而进入预渲染范围的瓦片。
4. 调用后端光栅化，然后按层树自底向上合成到目标，只重画受损区域。
5. 呈现，记录时间，安排回传。

### 瓦片

大于视口的层（滚动内容）不整层光栅化：

- 切成固定大小的瓦片（设备像素 256 或 512，按后端能力定），只保留视口附近的瓦片，滚动方向上多预渲染一段。
- 瓦片光栅化有每帧预算；预算用完时未就绪的瓦片先显示背景色（或低分辨率占位），下一帧补上。宁可晚一帧出清晰内容，也不掉帧。
- 缩放动画期间沿用旧瓦片做纹理缩放，停止后再按新比例重新光栅化。

### 后端能力差异

| 能力 | GPU 后端 | 软件后端 |
|---|---|---|
| 保留层（`RetainedLayers`） | 层与瓦片缓存为纹理，属性变化只需重新合成 | 没有层缓存：合成器把层树压平，直接对受损区域重放 DisplayList |
| 滚动 | 平移已有瓦片 | 位块搬移已有像素 + 重画新露出的条带 |
| 合成器动画 | 每帧只合成 | 每帧重画受影响区域 |

合成器只依据能力位分支，上层代码与 `SceneFrame` 格式完全相同。

**滚动的现状**（第八批，还没有层树与合成器）：滚动在 UI 线程上完成（决策 D44、D45）。只滚动的帧不运行布局：只遍历该滚动容器的子树、平移已存的墨迹、作废容器与祖先的录制缓存，损伤为容器视口；随后两个后端都重录容器与新露出的内容，并把**整个视口**重新光栅化 —— 上表的「平移瓦片」与「位块搬移 + 重画新条带」都还没有做。实测（软件后端，2000 行列表，2x，每帧 3 px，优化构建，负载 21–54）：管线中位数 4.2 ms，光栅 36 ms（视口约 1560×1120 设备像素），每帧重录 6.8 个、复用 15 个单元。偏移对齐设备像素，所以缓存的 DisplayList 平移整数个设备像素后逐像素不变，这也是将来做像素搬移的前提。

## 帧调度

```csharp
public abstract class FrameTimer                 // 由平台层实现
{
    public abstract void RequestTick();          // 请求下一次垂直同步回调
    public event Action<FrameTick>? Tick;        // 时间戳 + 预计呈现时间 + 刷新间隔
}
```

调度器是一个状态机，UI 与合成器各有一份「是否需要帧」的需求：

| 需求来源 | 触发 |
|---|---|
| UI 需要帧 | 有待处理的输入、State 变化、样式/布局/绘制脏标记、UI 线程动画、合成器回传需要响应 |
| 合成器需要帧 | 信箱里有新帧、有运行中的合成器动画或惯性滚动、有未完成的瓦片 |

- 两者都不需要时不请求 Tick，应用完全静止（零 CPU、零 GPU）。
- 一次 Tick 内：UI 线程跑 Input → Animate → Build → Style → Layout → Paint → Commit；渲染线程在同一个刷新间隔内合成并呈现。UI 线程超时则本间隔合成器照常用旧内容出帧（动画与滚动不断），新内容下个间隔上屏。
- **帧预算**：按刷新间隔分配（如 120Hz 下约 8ms）。Build 阶段可分片：超出预算时，低优先级的组件重建推迟到下一帧（输入直接触发的更新为高优先级）。
- **可变刷新率**：平台支持时，只有合成器动画时可请求降低或提高刷新率（`FrameTimer` 的可选能力）。
- **不可见时**：窗口最小化、被完全遮挡、应用进入后台 → 停止出帧，定时类动画按时间戳跳到正确状态。

**macOS GPU 模式的现状**（第六批，单线程，见 [04](04-platform.md)）：节拍（CADisplayLink / CVDisplayLink）直接出帧，不经过 `drawRect:`；尺寸或缩放变化时同步出帧；拖动缩放期间提交后 `waitUntilScheduled`，再在主线程 `[drawable present]`，新帧随 CA 事务上屏；不可见时不出帧，重新可见时整帧重呈现一次。`MetalSwapchain.LastAcquireTime` / `MetalRenderBackend.LastDrawableWait` 单独记录 `nextDrawable` 的阻塞时间：它算进 `Render` 调用的墙钟时间但不是 CPU 工作，所以 CPU 每帧耗时按「录制 + 编码」报告。已知限制：被其他进程的窗口遮挡、而遮挡通知未到时，`nextDrawable` 可能阻塞主线程 1 秒（固定超时）；把呈现放到渲染线程是根治办法之一。

### 输入与线程的现实约束

大多数平台的输入事件只投递到主线程，也就是 UI 线程。因此：

- **惯性滚动、吸附、合成器动画**在 UI 线程繁忙时仍然流畅（它们在渲染线程自行推进）。
- **手指跟随的滚动**需要 UI 线程把触摸位移转给合成器。这一步放在 Input 阶段最前、只做命中与转发，不等待后续阶段；但如果 UI 线程被一段长时间的用户代码完全占住，跟手仍会受影响。对策是帧预算分片与诊断（DevTools 标出阻塞主线程的长任务），而不是假装能在任何情况下跟手。

第八批的滚动物理与惯性还在 UI 线程上按帧时钟推进（`Simulation` 是纯函数，将来按上述分工移交合成器）；UI 线程繁忙时惯性也会卡顿。

## 单线程模式

WASM 默认以及 Headless 下，没有渲染线程：`Commit` 之后同步调用合成器的同一套代码。信箱、回传、代数机制都保留（退化为同步传递），保证两种模式行为一致、同一套测试覆盖。

## 命中测试

在 UI 线程上基于元素树进行，使用合成器回传的最新滚动偏移与动画中的变换。层树不参与命中测试，避免维护两套几何。

## 诊断

每帧记录：各阶段耗时、层数与纹理内存、光栅化的瓦片数、受损面积占比、被信箱覆盖的帧数、超预算的阶段。调试叠加（层边框、重绘闪烁、瓦片边界、过度绘制）由合成器在最终目标上追加绘制，不改变应用的 DisplayList。
