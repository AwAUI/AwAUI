# 09 触摸与手势

移动端体验的好坏基本取决于触摸手感。自绘框架拿不到系统控件的手感，必须自己把这一层做对。

接口定义见 [21-gesture-scroll-api.md](21-gesture-scroll-api.md)。

## 输入管线

```
平台原始触摸事件（多点、带时间戳、可能合并/高刷新率）
   → 归一化为 Pointer 事件（id、类型、位置、压力、接触面积、倾斜）
   → 重采样：按帧时间对触摸点插值/预测，消除采样率与刷新率不一致造成的抖动
   → 命中测试（仅在 pointerdown 时确定目标，之后隐式捕获）
   → 手势竞技场裁决
   → 派发：Pointer 事件 + 高层手势事件
```

- **统一 Pointer 模型**：鼠标、触摸、触控笔同一套事件，`pointerType` 区分；多点触摸以 `pointerId` 区分。
- **隐式捕获**：触摸按下后，后续 move/up 都发往同一目标（与 W3C Pointer Events 一致）。
- **重采样与预测**：高刷屏和 120Hz+ 触摸采样下必须做，否则拖拽与滚动会有可见的不均匀。
- **取消语义**：系统手势接管、来电、应用切后台时发 `pointercancel`，所有识别器必须正确复位。

## 手势竞技场

同一次触摸可能同时是点击、长按、滚动、拖拽、缩放的候选，嵌套滚动容器之间也会竞争。采用竞技场模型：

1. `pointerdown` 时，命中路径上所有元素的手势识别器进入竞技场。
2. 各识别器观察后续事件，可以**宣告胜出**（如移动超过 touch slop 且方向匹配的滚动）或**退出**（如长按超时前手指移动）。
3. 只剩一个识别器，或有识别器胜出时裁决；胜者独占后续事件，其余收到取消。
4. 裁决前不触发有副作用的回调，但允许即时的视觉反馈（按下态）。

内建识别器：Tap、DoubleTap、LongPress、Pan（含方向锁定）、Scroll、Pinch（缩放）、Rotate、Swipe（带速度阈值）、EdgeSwipe（系统边缘返回）。可自定义识别器。

组件上的用法是声明式的：

```csharp
new Div { OnTap = ..., OnLongPress = ..., OnPan = e => offset.Value += e.Delta }
```

## 与 CSS 的衔接

| CSS | 作用 |
|---|---|
| `touch-action: none / pan-x / pan-y / pinch-zoom / manipulation` | 声明该元素上允许哪些默认手势，是自定义手势与滚动之间的裁决依据 |
| `overscroll-behavior` | 滚动到边界后是否把滚动链传给父容器、是否显示越界效果 |
| `scroll-snap-type / scroll-snap-align` | 分页、轮播 |
| `@media (hover: hover)` / `(pointer: coarse)` | 区分有无悬停能力与指针精度，适配触摸与鼠标 |
| `:hover` | 触摸设备上不保持悬停态，避免「粘住」 |
| `:active` | 触摸按下时延迟极短时间再显示，滚动开始即取消，避免滑动列表时闪烁 |
| `user-select` | 是否允许长按选择文本 |
| `env(safe-area-inset-*)` | 避开刘海、圆角、底部手势条 |

## 滚动

- **合成器线程滚动**：滚动偏移在渲染线程更新，UI 线程繁忙时仍然跟手。可滚动内容自动提升为合成层。
- **惯性**：手指离开时由速度追踪器（对最近若干采样做最小二乘拟合）估算速度，进入减速动画。
- **平台化的物理参数**：iOS 风格（减速曲线 + 橡皮筋回弹）与 Android 风格（样条减速 + 拉伸越界效果），默认随平台，可通过设置或 CSS 扩展覆盖。
- **嵌套滚动**：内层滚到边界后按 `overscroll-behavior` 决定是否把剩余位移交给外层；方向不同的嵌套（横向轮播在纵向列表里）靠方向锁定裁决。
- **滚动锚定**、滚动条（触摸下为自动隐藏的指示条）、点击状态栏回到顶部（iOS）。
- 常见复合模式作为标准组件提供：下拉刷新、侧滑操作、底部抽屉（Sheet）、可拖拽排序。

**现状（第十一批）**：
- 已有：`VelocityTracker`（约 100 ms 的加权最小二乘）；触摸移动超过 8 px 容差后成为滚动拖动并锁定主轴，被按下的元素收到 `pointercancel`、不触发 click；松手后按物理惯性；`BouncingPhysics`（iOS 风格，橡皮筋与弹簧回弹）、`ClampingPhysics`（Android 风格样条减速）、`DesktopPhysics` 三种内建物理，桌面默认 `DesktopPhysics`，触摸可经 `RenderPipeline.TouchScrollPhysics` 换掉；按 `OverscrollBehavior` 把滚动交给外层容器；触摸下滚动条总是覆盖式指示条。`scroll-snap-type / align / stop` 已接入滚动管理器（用户输入结束后吸附到最近点，`always` 的吸附点截住惯性；内容 / 视口变化后的重新吸附推迟到下一次用户滚动）。`NestedScrollConnection` 的 pre / post 钩子覆盖滚轮与触控板、触摸拖动与惯性、键盘、滚动条翻页（程序滚动与拖动滑块不调用）。接口见 [21](21-gesture-scroll-api.md)。
- 手势：竞技场与内建 Tap / DoubleTap / LongPress / Pan / Swipe / Pinch / Rotate / EdgeSwipe 全部实现；`touch-action` 控制 Pan / Pinch / Rotate 入场；Pinch / Rotate 通过多指针转发同时观察两个接触点，第二根手指不会打断同一元素上的 Tap / Pan。`EdgeSwipeGestureRecognizer` 按视口坐标判定边缘带（默认 20 px，可限定单边），向内移动超过容差后胜出、抬起时派发 `EdgeSwipeEvent`；`touch-action: none` 不阻止它入场。手势参数可按子树覆盖：命中元素所属组件用 `Context.Provide` 提供 `GestureSettings` 后，竞技场在按下时取最近的设置写入本次序列的全部识别器；没有提供时用 `GestureSettings.Default`。`@ontap` / `@onlongpress` / `@onedgeswipe` 等标记属性已映射到处理器（见 [21](21-gesture-scroll-api.md)）。
- 滚动锚定：每个可纵向滚动的容器默认开启（`ScrollController.ScrollAnchoring`）。采样视口内文档序第一个可见常规流元素，内容在锚点上方变化而偏移未被用户输入 / 程序滚动改变时，调整偏移使锚点停在视口内同一位置；只做纵向，横向未实现。拖动、惯性 / 平滑动画、滚动条拖动与当帧 `ScrollTo` / `JumpTo` 期间不校正，只重设基准。锚点选择沿「第一个可见子元素」路径下探（跳过绝对 / 固定 / sticky、`visibility: hidden`、嵌套滚动容器），深度上限 64；无 `Key` 的列表在顶部插入时按序号复用元素，锚点身份可能与新内容对应，依赖 `Key` 的稳定列表最准确。
- 还没有：重采样与预测、按平台取手势 / 物理默认值、拉伸 / 光晕越界效果（画成钳制）、横向滚动锚定、合成器线程滚动（物理在 UI 线程上推进）。滚动相关 CSS 已接入：`overscroll-behavior`、`scrollbar-width / color`、`scroll-behavior`、`scroll-margin / padding`、`scrollbar-gutter`，经典滚动条按 `overflow` / `scrollbar-gutter` 在布局中预留宽度。触摸拖动滚动仍内建在滚动管理器里，不经竞技场，滚动接管时取消该指针的竞技场；多指识别器不参与单指针竞技场的冲突裁决，只按 `touch-action` 与命中路径过滤，只跟踪前两个指针。滚动吸附不处理 writing-mode（`block` 恒为纵向、`inline` 恒为横向），吸附区域按最近的可滚动祖先归属。Win32 的触摸以模拟鼠标到达，触摸拖动在那里不适用。触摸下的文本选择（长按放大镜 / 选择手柄、软键盘联动）尚未实现。没有在真实触摸设备上验证过手感。

## 触摸下的文本交互

- 点击定位光标、双击选词、长按呼出放大镜与选择手柄、拖动手柄调整选区。
- 选择工具条（复制/粘贴/全选）使用 Overlay 层，位置避开手指与软键盘。
- 软键盘弹出：视口变化事件 + 自动把焦点输入框滚动到可见区域；两种策略（调整布局高度 / 整体上推）可选。
- 键盘类型、回车键样式、自动更正等通过 `Input` 的属性传给平台（见 04 的 `TextInputOptions`）。

## 可用性约束

- **最小触摸目标**：DevTools 检查项，对小于 44×44（iOS）/ 48×48（Android）逻辑像素的可点击元素给出警告；支持不改变视觉尺寸的命中区域外扩（`-aw-hit-slop`）。
- **Touch slop**、双击间隔、长按时长取平台设置值。
- **系统手势让路**：屏幕边缘返回手势、底部上滑区域由平台层声明排除区域。
- **触觉反馈**：`Haptics` 平台服务（轻/中/重、选择、成功/失败），标准控件在合适的时机调用。
- **防误触**：手掌误触由系统处理；笔输入时可忽略同时出现的触摸。

## 平台对接

| 平台 | 来源 | 注意点 |
|---|---|---|
| Android | `MotionEvent`（含历史批量采样） | 利用批量历史点做重采样；`requestUnbufferedDispatch` 可选 |
| iOS | `UITouch`（含 coalesced / predicted touches） | 直接使用系统提供的合并与预测触摸 |
| Web | Pointer Events（`getCoalescedEvents`） | canvas 需设 `touch-action: none` 由框架全权处理；被动监听器限制 |
| Windows | `WM_POINTER` | 触摸屏与笔 |
| macOS | 触控板手势事件 | 捏合、旋转、惯性滚动由系统提供相位信息（第八批：滚动的手势相位与惯性相位已接入，直接跟随、不叠加框架自己的惯性） |
| Linux | XInput2 / Wayland touch | |

## 调试支持（与 06 联动）

- 触摸点可视化叠加层（第十一批：DevTools 叠加层绘制活动触摸 / 笔指针的圆与十字准星；轨迹仍待做）。
- 手势竞技场日志：每次触摸有哪些识别器参赛、谁在何时胜出或退出及原因。
- 桌面上用鼠标模拟触摸（含 Alt 拖动模拟双指捏合）。
- 录制并重放触摸序列，用于复现问题与自动化测试。
