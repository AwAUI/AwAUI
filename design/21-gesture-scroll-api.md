# 21 手势识别与滚动（接口级）

[09](09-touch-gestures.md) 描述了行为，本文定义接口：手势识别器、竞技场、滚动控制、滚动物理，以及它们与合成器（[18](18-scene-compositor.md)）的分工。类型位于 `AwAUI.UI`。

**实现状态（第八批）**：滚动部分已实现（滚动容器、`ScrollController`、物理与模拟、滚轮 / 触控板 / 键盘 / 触摸拖动、自绘滚动条），见下文各节与「实现现状」；物理暂在 UI 线程上推进（决策 D45）。手势识别器、竞技场、`NestedScrollConnection` 与拖放**尚未实现**：触摸拖动滚动内建在滚动管理器里，不经竞技场。

## 手势识别器（未实现）

```csharp
public abstract class GestureRecognizer
{
    public GestureState State { get; }           // Possible → Began → Changed* → Ended | Cancelled；或 Possible → Failed
    public Element Target { get; }

    // 是否参与这次按下（可按指针类型、按键、touch-action 过滤）
    protected internal virtual bool AcceptsPointer(in PointerEvent down) => true;

    // 逐事件处理；通过 Arena 宣告胜出或退出
    protected internal abstract void HandleEvent(in PointerEvent e, ArenaEntry entry);

    // 裁决结果
    protected internal virtual void OnAccepted(int pointerId) { }
    protected internal virtual void OnRejected(int pointerId) { }

    // 关系
    public void RequireFailureOf(GestureRecognizer other);      // 等对方失败后才能胜出（单击等双击）
    public void AllowSimultaneousWith(GestureRecognizer other); // 可同时生效（捏合 + 旋转）
}

public readonly struct ArenaEntry
{
    public void Accept();      // 宣告胜出
    public void Reject();      // 主动退出
    public void Hold();        // 请求延后裁决（等待超时，如双击的第二次按下）
    public void Release();
}
```

### 竞技场规则

1. `pointerdown`：沿命中路径（由内到外）收集接受该指针的识别器，按「内层优先」入场。
2. 任一识别器 `Accept()`：它胜出，其余收到 `OnRejected`。
3. 只剩一个未退出的识别器：它自动胜出。
4. `pointerup` 时仍无人胜出且无人 `Hold`：按入场顺序第一个未退出者胜出（最内层）。
5. `AllowSimultaneousWith` 的识别器组成一个队，整队共同胜负。
6. `pointercancel`：全部取消。

CSS `touch-action` 在第 1 步生效：元素声明不允许的手势，其对应的内建识别器不入场。

### 内建识别器与事件

| 识别器 | 组件属性 | 事件数据 | 胜出条件 |
|---|---|---|---|
| Tap | `OnTap` | 位置、指针类型、点击次数 | 抬起时未超出触摸容差 |
| DoubleTap | `OnDoubleTap` | 位置 | 间隔时间内第二次点击 |
| LongPress | `OnLongPress`（开始/移动/结束） | 位置 | 按住超过时长且未移动 |
| Pan | `OnPanStart / OnPan / OnPanEnd` | 位移增量、累计位移、速度 | 移动超出触摸容差；可限定轴向 |
| Scroll | 内建于可滚动元素 | — | 同 Pan，且方向上还能滚动或允许越界 |
| Pinch | `OnPinch` | 缩放比、焦点、增量 | 两指间距变化超出阈值 |
| Rotate | `OnRotate` | 角度、焦点 | 两指连线角度变化超出阈值 |
| Swipe | `OnSwipe` | 方向、速度 | 抬起时速度超过阈值 |

`OnClick` 是语义层面的「激活」，由 Tap、键盘（回车/空格）和无障碍动作共同触发；普通按钮应使用 `OnClick` 而不是 `OnTap`。

### 参数

| 参数 | 默认值来源 | 典型值 |
|---|---|---|
| 触摸容差（touch slop） | 平台设置 | 触摸约 8–10 逻辑像素；鼠标约 2 |
| 长按时长 | 平台设置 | 400–500 ms |
| 双击间隔 / 距离 | 平台设置 | 300 ms / 约 40 逻辑像素 |
| 惯性最小 / 最大速度 | 平台设置 | 约 50 / 8000 逻辑像素每秒 |
| 按下态显示延迟 | 框架 | 触摸下可滚动容器内约 60–100 ms，其余为 0 |

全部通过 `GestureSettings` 读取，可按应用或子树覆盖（经上下文提供）。现状：只有全局的 `GestureSettings.Default`（`TouchSlop` 8、`MouseSlop` 2、方向锁定比 `AxisLockRatio` 2），平台设置与按子树覆盖未接入，滚动拖动用到其中的容差。

### 速度追踪

```csharp
public sealed class VelocityTracker
{
    public void AddSample(TimeSpan time, Point position);
    public void Reset();
    public Vector2 GetVelocity();      // 对最近约 100 ms（HorizonMs）的采样做加权最小二乘拟合；采样不足或停顿超过 40 ms 后返回零
}
```

## 滚动

### 对组件暴露的 API

```csharp
public sealed class ScrollController
{
    public Point Offset { get; }
    public Size ContentSize { get; }
    public Size ViewportSize { get; }
    public Point MaxOffset { get; }
    public bool IsScrolling { get; }                  // 拖动、触控板手势、动画进行中或按住滚动条
    public Element? Element { get; }                  // 当前附着的滚动容器

    public ScrollPhysics? Physics { get; set; }       // 触摸拖动与惯性用；null 时用 RenderPipeline 的默认
    public OverscrollBehavior OverscrollBehaviorX { get; set; }   // Auto / Contain / None（CSS 属性尚未接入，见下）
    public OverscrollBehavior OverscrollBehaviorY { get; set; }
    public ScrollbarWidth ScrollbarWidth { get; set; }            // Auto / Thin / None
    public Color? ScrollbarThumbColor { get; set; }
    public Color? ScrollbarTrackColor { get; set; }

    public void JumpTo(Point offset);
    public Task ScrollTo(Point offset, ScrollAnimation? animation = null);   // 默认平滑滚动（300 ms，EaseInOut）；任务在停止或被打断时完成
    public Task ScrollIntoView(Element element, ScrollAlignment alignment = default);
    public void Stop();

    public event Action<ScrollEvent>? Scrolled;       // 节流到每帧一次
    public event Action? ScrollEnd;
}
```

用法：`new Div { Style = "overflow:auto", ScrollController = controller }`（节点属性 `ElementNode.ScrollController`；不给时元素按需自建，经 `Element.ScrollController` 取得）。`OnScroll` 事件属性等价于订阅 `Scrolled`；`OnWheel` 收到可取消的 `WheelEvent`（`DeltaX / DeltaY / DeltaMode / IsPrecise / ScrollPhase / MomentumPhase`），`PreventDefault` 后不再滚动。滚动位置跨重建保留；内容变小时偏移钳制到新范围并发出 scroll 事件。`ScrollAnimation`：`Default`、`Instant`、`Duration`、`Easing`（`ScrollEasing`）。`.awaui` 里的 `@onscroll / @onwheel` 与 `ScrollController` 属性尚未映射（见 [07](07-roadmap.md) 第八批后续事项）。

### 滚动物理

```csharp
public abstract class ScrollPhysics
{
    // 拖动中：把手指位移换算成实际位移（越界时加阻尼）
    public abstract float ApplyDrag(in ScrollMetrics m, float delta);
    public virtual float MinFlingVelocity => 50f;   // 逻辑像素每秒
    public virtual float MaxFlingVelocity => 8000f;

    // 松手后：返回一段运动模拟（惯性、回弹、吸附）；返回 null 表示立即停止
    public abstract Simulation? CreateFling(in ScrollMetrics m, float velocity);

    // 是否允许越界，以及越界的呈现方式
    public abstract OverscrollMode Overscroll { get; }        // None / Bounce / Stretch / Glow
}

public abstract class Simulation                 // 纯函数、不可变：可交给渲染线程执行
{
    public abstract float Position(double seconds);
    public abstract float Velocity(double seconds);
    public abstract bool IsDone(double seconds);
}
```

| 内建物理 | 拖动越界 | 惯性 | 越界呈现 |
|---|---|---|---|
| `BouncingPhysics`（iOS 风格） | 橡皮筋阻尼，位移随越界距离递减 | 指数衰减；到边界后转为弹簧回弹 | 内容整体越界再弹回 |
| `ClampingPhysics`（Android 风格） | 不越界 | 样条减速曲线 | 边缘拉伸效果 |
| `DesktopPhysics` | 不越界 | 滚轮：短促的平滑动画；触控板：使用系统给出的惯性相位，不叠加自己的惯性 | 无（macOS 触控板除外，随系统） |

内建模拟（第八批）：`SpringSimulation`（`SpringDescription`）、`FrictionSimulation`、`BouncingScrollSimulation`、`ClampingScrollSimulation`（Android 样条）、`ClampedSimulation`（把内层模拟限制在范围内；位置到达边界且速度朝外或为零才算结束）、`SmoothScrollSimulation`（滚轮、键盘与 `ScrollTo` 的平滑动画）。`DesktopPhysics` 的拖动不越界、惯性借用 Clamping 的曲线。

默认按平台与输入设备选择；`-aw-scroll-physics` 或上下文可覆盖。现状：`RenderPipeline.DefaultScrollPhysics`（默认 `DesktopPhysics`）、`RenderPipeline.TouchScrollPhysics`（触摸拖动用，null 时用默认）与 `ScrollController.Physics`；`-aw-scroll-physics` 与上下文覆盖未做，Stretch / Glow 越界效果声明了但画成钳制。吸附（`scroll-snap-*`）包装在任何物理之上：惯性的落点被修正到最近的吸附点。曲线参数集中在各物理类的常量表里，按实机对比调校。

### 嵌套滚动

```csharp
public abstract class NestedScrollConnection
{
    // 子滚动器消费之前，父级可以先拿走一部分（如先折叠标题栏）
    public virtual Vector2 OnPreScroll(Vector2 available, ScrollSource source) => default;
    // 子滚动器消费之后，剩余部分交给父级
    public virtual Vector2 OnPostScroll(Vector2 consumed, Vector2 available, ScrollSource source) => default;
    public virtual Vector2 OnPreFling(Vector2 velocity) => default;
    public virtual Vector2 OnPostFling(Vector2 consumed, Vector2 available) => default;
}
```

- 可滚动元素默认实现「自己滚到头后把剩余位移交给最近的可滚动祖先」，受 `overscroll-behavior` 约束（`contain` 截断传递，`none` 同时禁用越界效果）。
- 自定义连接用于折叠头部、下拉刷新、底部抽屉这类联动；标准组件内建。

现状（第八批）：默认的传递已实现，但 `overscroll-behavior` 只能经控制器的 `OverscrollBehaviorX/Y` 设置（CSS 属性未接入）；`NestedScrollConnection` 未实现。滚轮不拆分剩余位移：选「在该方向上还能滚」的最内层容器整份交给它（与浏览器一致）。

### 与合成器的分工

| 环节 | 线程 | 说明 |
|---|---|---|
| 手势裁决、`ApplyDrag` | UI | 每次触摸移动：算出位移 → 作为滚动命令随帧（或即时通道）发给合成器 |
| 惯性、回弹、吸附动画 | 渲染 | 松手时 UI 线程创建 `Simulation` 并移交，之后合成器逐帧求值，不再经过 UI 线程 |
| 偏移回传 | 渲染 → UI | 每合成帧一次，见 18 的代数机制 |
| `Scrolled` 事件、虚拟列表更新、粘性定位 | UI | 基于回传的偏移，最多落后一帧 |
| 打断（新的按下、`JumpTo`、`Stop`） | UI → 渲染 | 递增代数，合成器停止当前模拟 |

自定义的 `ScrollPhysics` 与 `Simulation` 因此必须是无副作用的纯计算。带 `NestedScrollConnection` 的自定义联动在 UI 线程执行，该滚动器的惯性也随之留在 UI 线程（功能优先于离线程，诊断中会标明）。

**现状（第八批，决策 D45）**：还没有渲染线程与合成器，上表全部在 UI 线程：`ScrollManager.BeginFrame` 用帧时钟推进模拟，`EndFrame` 发出本帧的 scroll 与 scroll-end 事件；模拟运行超过 `MaxSimulationSeconds`（30 s）强制结束，防止自定义物理无限出帧。`Simulation` 仍是纯函数，将来按上表移交时接口不变。

### 滚轮、触控板、键盘

- 滚轮：离散增量按系统设置的行数换算，套一段可叠加的短动画；按住 Shift 横向滚动。
- 触控板：使用平台提供的精确增量与相位（开始、变化、惯性、结束）；有相位信息时直接跟随，不做二次平滑。
- 键盘：方向键、PageUp/Down、Home/End、空格，作用于焦点所在的最近可滚动元素。
- 滚动条：经典样式（常驻、可拖动、可点击轨道）与覆盖样式（自动隐藏的指示条）按平台与指针类型选择，可用 `scrollbar-*` 属性定制。

第八批的实现：

- **增量换算**（`AwAUI.Platform.WheelDeltas`）：Win32、X11、Headless 每格 3 行 = 100 px（Chromium 约定），macOS 行模式每行 40 px（系统已加速，WebKit 约定），Browser 每行 40 px；Page 增量换回页数后按目标容器视口的 87.5% 缩放。非有限与超大增量钳制。
- **路由**：可取消的 `wheel` 事件先发给目标元素（`captured ?? hit`，与浏览器不同，浏览器的 wheel 不受指针捕获影响）；没有被取消时，从目标向外找第一个「在该方向上还能滚」的容器，`OverscrollBehavior` 为 Contain / None 时到此为止。普通滚轮的平滑动画为 140 ms（`DesktopPhysics.WheelAnimationMs`），连续多格叠加；`RenderPipeline.SmoothScrolling = false` 关闭。精确增量或带相位的事件直接跟随，并锁定在手势开始时选中的容器直到惯性结束；没有惯性相位的手势在最后一个事件 100 ms 后结束（`GestureEndDelaySeconds`，与 Chrome 的 scrollend 去抖相同）。
- **键盘**：按键没有被消费、焦点不在输入框里时，方向键、PageUp/PageDown、Home/End、空格滚动焦点所在的最近滚动容器；没有焦点时作用于最近一次按下所在的容器，否则作用于视口。键盘或程序聚焦时以最小移动把元素滚到可见（指针聚焦不滚动）。
- **触摸**：按下后移动超过容差（8 px）即成为拖动并锁定主轴；被按下的元素收到 `pointercancel`，不再触发 click；拖动按 `ApplyDrag`，松手按 `VelocityTracker` 的速度调用 `CreateFling`；Bouncing 物理在边缘可以开始橡皮筋拖动。
- **滚动条**（决策 D46）：样式由 `RenderPipeline.ScrollbarStyle`（`UIHost` 取 `WindowImpl.PreferredScrollbarStyle`）决定，最近一次按下来自触摸时总是覆盖式（`EffectiveScrollbarStyle`）。经典样式常驻、滑块可拖动、点击轨道翻一页（没有箭头按钮，按住轨道不连续翻页），v1 不占布局空间，画在内边距盒内侧、盖住内容边缘；覆盖样式在滚动或悬停时出现，停止 1 s 后淡出（等待期间用时钟定时器，只有淡出时逐帧）。滑块长度按视口与内容之比（不小于 20 px），位置按已施加（对齐设备像素）的偏移计算，所以滑块与内容总在同一帧变化。滚动条可命中（覆盖式只在可见时），受祖先裁剪与圆角裁剪、不受容器自己的溢出裁剪；不是层叠上下文的容器，其滚动条推迟到该层叠上下文中容器的最后一个 0 / auto 定位后代之后再画，z-index 为正的后代仍在滚动条之上（与浏览器的细节不同）。`scrollbar-*` CSS 属性尚未接入，宽度与颜色经控制器设置。

## 实现现状（第八批）

- **滚动容器**（决策 D43，`RenderPipeline.ScrollAxes`）：至少一个轴的 overflow 不在 { visible, clip } 中的元素；auto / scroll 可由用户滚动，hidden（以及另一轴可滚动时的 clip）只能程序滚动。根元素即视口：visible 按 auto，`position: fixed` 不随滚动移动、不计入根的可滚动溢出。段落（`text`、`input`）与替换元素不是滚动容器。视口是内边距盒（根为窗口客户区），内容尺寸是布局引擎的可滚动溢出（含末端外边距与内边距；没有 CSS transform，所以不考虑变换）。
- **几何**（决策 D44）：偏移对齐设备像素，在绝对几何遍历中施加。遍历为每一项带三种平移：父元素自身的、给常规流子元素的、给绝对定位子元素的（最近定位祖先或根的内容平移），所以包含块在滚动容器之外的绝对定位内容不随它滚动，fixed 从不滚动。命中测试与 `BoundingRect` 因此自动包含滚动，嵌套滚动同样成立。
- **只滚动的帧**：不运行布局。从沿包含块链重建的上下文（`ShiftOf`）出发只遍历该容器的子树，平移已存的墨迹矩形，作废容器与祖先的录制缓存，损伤为容器视口；有绝对定位或 fixed 后代没有随之移动时退回完整的墨迹重算。同一帧的多个嵌套容器各自遍历、顺序无关。之后重录容器与新露出的内容，并重新光栅化整个视口（没有像素搬移）。宿主在这样的帧之后重新做悬停检测（`RenderPipeline.LastFrameScrolled`）。
- **状态归属**：已施加的偏移与视口 / 内容度量属于元素的几何状态；换控制器时新控制器总是接过它们，偏移位置按新控制器是否用过决定（用过的按本元素的范围钳制）；控制器被别的元素拿走时，原元素立即换上新的控制器并回到位置 0。
- **钳制**：平滑动画（滚轮、键盘、`ScrollTo`、`ScrollIntoView`）任何时候都不越界；只有拖动和惯性 / 回弹期间由物理决定是否越界。用户输入打断程序动画时两轴的模拟都清除、任务完成。
- **时间**：`UIRoot.Clock` 为 null 时滚动的时间每帧推进 1/60 s（确定性的无窗口渲染），平滑滚动约 9 帧完成、覆盖式滚动条约 80 帧后隐藏。
- **出帧**：`RenderPipeline.NeedsAnimationFrame` 在有模拟、待施加的偏移或正在淡出的滚动条时为真；`UIHost` 每帧之后检查它，`UIRoot.NeedsFrame` 还不包含它（见 [07](07-roadmap.md) 第八批后续事项）。
- **测试**：`ScrollDifferentialTests`（随机场景，局部重画与整帧非增量渲染逐字节比较并核对命中测试）、`ScrollReviewTests`（只平移与强制完整布局逐元素比较），种子数用 `AWAUI_SCROLL_DIFF_SEEDS` 加大；物理与速度追踪对固定时间点求值。

## 拖放

应用内拖放建立在 Pan 之上：`Draggable`（长按或移动后开始、拖动预览走 Overlay 层）、`DropTarget`（进入、悬停、离开、放下）。跨应用的系统拖放通过平台服务桥接，数据格式与剪贴板共用一套抽象。

## 测试

- 识别器与物理都是确定性的：输入「时间戳 + 位置」序列，断言状态转换与输出；物理模拟对固定时间点求值比对。
- 录制的真实触摸序列（来自 DevTools 的 `Input.startRecording`）作为回归用例。
- 手感类参数的验收靠实机与原生列表并排对比，这部分无法自动化，列为各平台发布前的检查项。
