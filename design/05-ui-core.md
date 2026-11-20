# 05 UI 核心：声明式组件 + 元素树

已定方向：**声明式写法 + CSS 样式**。整体模型与「React + DOM + CSS」同构，只是全部用 C# 实现：

```
 组件树 (Component)          开发者 / AI 编写：UI = f(state)
      │  Build() 产出 Node 描述树
      ▼
 协调器 (Reconciler)         新旧描述 diff → 最小化变更
      ▼
 元素树 (Element)            框架持有的保留对象：标签、id、class、属性、内联样式
      │  CSS 级联 → ComputedStyle          （见 08-css.md）
      ▼
 布局树 → DisplayList → 层树 → 渲染后端
```

## 为什么声明式对 AI 更友好

我同意这个判断，理由是具体的：

1. **局部可推理**：一个组件的界面完全由它的 `Build()` 决定，不需要追踪「哪段代码在什么时候改了哪个控件」。AI 生成和修改代码时最容易出错的就是跨位置的命令式状态同步。
2. **与训练数据同构**：JSX/React、SwiftUI、Compose、Flutter 以及 HTML+CSS 是模型见得最多的 UI 代码形态；CSS 尤其如此。沿用同样的心智模型，生成质量直接受益。
3. **编译器即反馈回路**：纯 C# 强类型描述，写错属性名、类型不匹配在编译期报错，AI 能据此自我修正；运行时字符串绑定（如 XAML 绑定路径）则是静默失败。
4. **热重载天然成立**：重跑 `Build()` 即可，状态保留。
5. **可被机器观察**：元素树 + CSS 可以序列化为文本，AI 能通过 DevTools 协议「看到」自己写出的界面并迭代（见 06）。

但底层仍保留**持久的元素树**（相当于 DOM）：F12 检查、精确脏标记、合成器动画、无障碍、触摸命中测试都需要稳定的对象。声明式是写法，保留树是实现。

## 组件

```csharp
public partial class Counter : Component
{
    public int Step { get; init; } = 1;            // Props：父组件传入，不可变

    readonly State<int> count = new(0);            // State：组件实例持有，跨重建保留

    protected override Node Build() =>
        new Div { Class = "counter", Children =
        [
            new Text($"当前计数：{count.Value}"),
            new Button
            {
                Class = "primary",
                OnClick = _ => count.Value += Step,
                Children = [ "增加" ],
            },
        ]};
}
```

要点：

- **Node 是轻量、不可变的描述对象**，不是真实元素；每次 `Build()` 重新生成，由协调器对比后更新元素树。
- **State 自动追踪**：`Build()` 期间读取过的 `State<T>` / `Computed<T>` 被记录为该组件的依赖；值变化 → 仅该组件在下一帧的 Build 阶段重建。不需要手动 `setState`，State 也没有 Hooks 的调用顺序规则（状态就是字段，组件实例是持久的）；唯一按调用顺序识别的是 `UseEffect`，它只能在 `Build()` 中调用。
- **协调**：按「类型 + Key」匹配；列表项要求显式 `Key`（分析器对缺失 Key 的动态列表报警告，`AW1004` 尚未实现；重复 Key 在运行时报 `AW1008`）。Props 相等且无依赖变化的子组件跳过重建；init Props 变化时组件重新挂载（见 12）。
- **生命周期**：`OnMount / OnUnmount`；副作用 `UseEffect(() => ..., deps)`，返回清理函数。顺序规则见 [12](12-component-api.md)。
- **上下文**：`Provide<T>(value)` / `Use<T>()`，用于主题、导航、依赖注入。
- **命令式逃生舱**：`Ref<Element>` 获取真实元素，用于聚焦、滚动到位置、测量、手势驱动动画。
- **源生成器**负责：Props 相等比较、组件元数据（供 DevTools 组件面板）、创建处源码位置、作用域 CSS 关联。无反射。M1 已实现的只有 Props 胶水（`PropsEqual / AdoptProps / CanAdoptProps`）；作用域样式的关联由 `.awaui` 编译器与运行时完成（见 [12](12-component-api.md)「作用域样式」），组件元数据与源码位置待做。

接口级的 API 定义见 [12-component-api.md](12-component-api.md)。

### 编写方式（D3，已定）

| 方式 | 示例 | 定位 |
|---|---|---|
| **`.awaui` 单文件组件** | `<div class="x">@count.Value</div>` + `@code { }` + `<style>` | 默认方式。类 Razor 语法，由独立的标记编译器在构建期编译为下面的 C#（见 [14](14-awaui-format.md)） |
| **纯 C#** | `new Div { Class = "x", Children = [ ... ] }` | 底层 API 与编译目标，可直接手写（见 [12](12-component-api.md)） |

两者产出同一种 `Component`，可在同一项目中混用。核心框架只认识 C# 的 Node API，不依赖标记编译器。

## 元素树

元素是框架持有的保留对象，对应 DOM 节点（M1 的 `AwAUI.UI.Element` 有标签、id、class、属性、内联样式原文、伪状态、语义、脏标记；公开的 `ComputedStyle` 与由布局填入的 `BoundingRect`（绝对坐标，对齐到设备像素）；样式适配、布局节点与段落等管线数据放在一个内部的强类型字段里；第六批起有元素级的绘制缓存，见下文「增量绘制」）：

| 成员 | 说明 |
|---|---|
| `Tag` | 元素类型名，CSS 类型选择器匹配它 |
| `Id` / `ClassList` / 属性 | CSS 选择器匹配依据；变化时精确失效相关样式 |
| `InlineStyle` | 内联样式声明 |
| `ComputedStyle` | 级联结果，强类型结构体（见 08） |
| `Children` / `Parent` | 树结构 |
| 伪状态 | `:hover :active :focus :focus-visible :disabled :checked ...` 由框架维护 |
| 语义 | `Role`、`AriaLabel` 等无障碍属性（见 10） |
| 布局结果 | 盒模型各矩形、滚动偏移 |
| 绘制缓存 | 该元素的 DisplayList |

### 内建元素（保持很少）

| 元素 | 用途 |
|---|---|
| `Div` | 通用盒子，布局与装饰全由 CSS 决定 |
| `Text` / `Span` | 文本与行内富文本 |
| `Button` | 原生按钮元素（标签 `button`，有 `Disabled`、Enter/Space 激活）；[22](22-controls.md) 的控件 `Button` 组件包装它，或包装 `role=button` 的 `div` |
| `Image` | 位图（替换元素，标签 `img`）：来源、异步解码、缓存、按自然尺寸与宽高比布局、CSS object-fit / object-position / image-rendering、GIF / WebP 动画；第七批起经 `DrawImage` 画出位图像素。`ImageElement.AccessibleName` = `aria-label`，否则 `Alt`。细节见 [03](03-renderer.md)「图片」 |
| `Svg` | 矢量图（替换元素，标签 `svg`），见下 |
| `Input` / `TextArea` | 文本编辑（实现 `ITextInputClient`）。第七批 `Input` 已有光标、选区、输入法与剪贴板（见下文「文本输入」）；9c 加入基础多行 `TextArea` |
| `Canvas` | 自定义绘制：`OnPaint(DrawingContext)` |
| `VirtualList` | 虚拟化长列表（项构建器 + 回收） |
| `Portal` | 把子树挂到顶层 Overlay（弹层、菜单、Toast） |

滚动不是专门的元素：任何盒子设置 `overflow: auto | scroll` 即可滚动（CSS 语义）。第八批实现：overflow 为 auto / scroll / hidden 的元素是滚动容器（hidden 只能程序滚动），根元素即视口（visible 按 auto，fixed 不滚动，决策 D43）；元素有 `ScrollController`，节点有 `OnScroll / OnWheel / ScrollController`。偏移对齐设备像素、在绝对几何遍历中施加（常规流随父元素、absolute 随包含块），只滚动的帧不重新布局、只重录容器并重画其视口（决策 D44）；物理在 UI 线程上按帧时钟推进（决策 D45）；滚动条由 UI 自绘（决策 D46）。接口与细节见 [21](21-gesture-scroll-api.md)。

`Svg` 元素（第六批实现）建立在 `AwAUI.Svg` 上：`Document` 是解析好的 `SvgDocument`（不接受子元素），`CurrentColor` 取元素计算后的 CSS `color`；录制结果缓存在元素上，键为（文档、尺寸，以及仅当文档用到 currentColor 时的颜色），以 Translate 组合，所以给图标换色只有在文档真用到 currentColor 时才重录 SVG。依赖规则因此允许 `AwAUI.UI → AwAUI.Svg`（以及 `AwAUI.Imaging`，决策 D32）。`AwAUI.UI.Svg`（元素）与 `AwAUI.Svg` 命名空间同名，与 `AwAUI.UI.Text` 的情况相同，其他 `AwAUI.*` 命名空间里的代码要写 `UI.Svg`。`Icon` 组件渲染 `<svg class="aw-icon">`（默认样式表中 1em × 1em、`flex-shrink: 0`），给了 `Label` 时有可访问名称，否则标为装饰（见 [22](22-controls.md)）。默认样式表另有 `img, svg { display: inline-block }`。

替换元素的布局（第七批修订，第八批更新）：

- **CSS 是唯一来源**（决策 D35）：`object-fit`、`object-position`、`image-rendering` 取自计算后的 CSS（第 1 期属性，见 [16](16-css-properties.md)）；原来的节点属性 `Image.Fit / Position / Rendering`、`ImageElement.Fit / ObjectPosition / Rendering` 与 UI 类型 `ObjectFit`、`ImageRendering`、`ObjectPosition` 已删除（HTML 里它们同样只有 CSS 形式；非空的属性也无法表达「未设置」）。单个元素用内联样式 `Style="object-fit: cover"`。定位由内部的 `ObjectFitting.Place` 完成，用内容自己的自然尺寸（`img` 的 width/height 是尺寸提示，不参与定位；`Svg` 用 `Measure.NaturalSize`，提示覆盖文档尺寸）；只有宽高比的内容（只有 viewBox 的 SVG）：contain / none / scale-down 按比例放进盒内，cover 铺满，fill 拉伸；目标矩形钳制到 ±1e7。`Svg` 元素同样应用 object-fit / object-position（与 `<img src=svg>` 相同），文档的 `preserveAspectRatio` 在得到的矩形内生效；初值 `fill` 时结果与以前相同。绘制细节（源矩形裁剪、设备像素对齐、圆角裁剪）见 [03](03-renderer.md)「图片」。
- **aspect-ratio**：`LayoutMapper` 对所有元素映射（`AspectRatioValue.Resolve(null)`），替换与非替换元素都生效。替换元素在 `ReplacedMeasure.Update` 中用 `AspectRatioValue.Resolve(naturalRatio)` 与自然比例合并：`auto 16/9` 表示「有自然比例就用它，否则 16/9」；`<ratio>` 与自然比例不同且宽高都是 auto 时保持自然宽度、高度按比例（同浏览器）；`width` 与 `height` 提示都给出时忽略 CSS 比例。退化的比例按 auto。
- **块级替换元素不拉伸**：`RenderPipeline.MapLayoutStyle` 对替换元素调用 `LayoutMapper.ForReplaced`，设置 `LayoutStyle.IsReplaced` 并把使用的宽高比（`ReplacedMeasure.AspectRatio`）交给引擎；引擎据此在块布局与根中不撑满 auto 宽度（CSS 2.1 §10.3.4）、绝对定位给出 left + right（或 top + bottom）时不拉伸、网格的 normal 对齐按 start。第七批「display 映射为 `InlineFlex`」的规避已删除。常见的 `img { display:block; max-width:100%; margin:0 auto }` 照常正确。
- **min / max**：`ReplacedMeasure.Measure` 只在测量中按 CSS 2.1 §10.4 的约束表处理能直接解析的 min/max（长度、不含百分比的 calc；border-box 时内边距须为长度，否则跳过），保持宽高比，包括 max-height / min-height 经比例传到宽度；测量约束中的确定宽度 / 高度只是可用空间，不参与。百分比形式的 min/max、跨宽高比传递的限制以及由钳制后的宽度推出高度都由引擎按 `IsReplaced` 与 `AspectRatio` 处理（第八批，见 [08](08-css.md)），所以 `min-width: 10%` 的 50×25 图片在 1000px 容器里得到 100×50（第七批的猜测分支得到 1000×500，已删除）。替换元素的比例作用于内容盒，不施加非替换元素那种「不小于内容高度」的自动最小值，固有尺寸也不报告为可滚动溢出。
- **尺寸提示**：负数、NaN、无穷的 `Width / Height` 提示当作没有给出（同 HTML 忽略无效的 width/height 属性）。提示仍是自然尺寸提示，不是 CSS 表现提示：`<img width=100 height=50 style="width:200px">` 这里得到高 100，浏览器是 50。改为表现提示需要 Css 增加表现提示的级联层级（见 [07](07-roadmap.md) 第八批后续事项）；在 UI 层模拟会让 `img { max-width:100%; height:auto }` 变形，所以没有做。
- **加载与通知**：`ImageCache.Loaded` 与 AW1010 推迟到绘制之后或投递的调度器回调中触发，从不在布局或绘制中途触发；`FromStream` 保留至多 16 MiB 的编码字节（见 [03](03-renderer.md)「图片」）。
- **失败占位与替代文本**（第八批，内部的 `ImageFallback`）：加载中仍什么都不画。加载失败时画 16×16 的「图片损坏」矢量图标（任何缩放下清晰），右侧隔 4px 画替代文本 —— 用元素的计算样式排版（字体、颜色、行高、空白处理），可换行，裁剪到内容盒（有圆角时按内容盒的圆角形状），只取前 1000 个字符。`alt=""` 视为装饰性图片，什么都不画、尺寸为 0；没有来源但有替代文本时只画文本。占位的尺寸成为元素的固有尺寸，但不算固有宽高比（`ReplacedContent.SizeDefinesRatio = false`），所以失败时 `aspect-ratio: auto 4/3` 照样按 4/3。行为变化：失败的图片原来是 0×0，现在至少 16×16。`SetAlt` 只在显示占位时额外标脏布局与绘制。
- **「位图已改写」**（第八批）：`ImageCache.NotifyChanged(ImageSource)` —— 调用方持有的位图（`FromBitmap`）版本号加一并重绘；其他来源重新加载，旧内容保留到新内容就绪，按加载代数丢弃过时的异步结果，被替换的内容在绘制之后释放；已解析的 SVG 文档来源（`FromSvg`）不可变，调用无效果。

控件清单与约定见 [22-controls.md](22-controls.md)。**标准控件库**（Button、Checkbox、Switch、Slider、Select、Tabs、Dialog、Menu、Tooltip……）全部是用上述元素写成的普通组件，外观由一份默认样式表定义，可通过 class、CSS 变量和 `::part()` 定制。

## 帧内流程（UI 线程部分）

```
 Input → Animate → Build(组件重建+协调) → Style(CSS 级联) → Layout → Paint → Commit
```

每一步只处理脏集合：

| 触发 | 影响 |
|---|---|
| State 变化 | 该组件 Build → 协调产生的元素变更 |
| class / id / 属性 / 伪状态变化 | 受影响选择器命中的元素重算样式 |
| ComputedStyle 中布局类属性变化 | 向上到布局边界的子树重排 |
| 绘制类属性变化 | 仅该元素重录 DisplayList |
| `transform / opacity / 滚动偏移` 变化 | 只更新合成层属性，不重排不重录 |

### M1 帧管线（`RenderPipeline`）

`RenderPipeline : FramePipeline` 在 UI 线程上依次跑样式 → 布局 → 文本 → 绘制，并提供命中测试；`UIHost` 把它接到窗口（见 [01](01-architecture.md) 组合根）。与上表的目标相比的简化与实际规则：

- **布局后的组件更新**：依赖视口测量的组件可在 `LayoutCompleted` 中调用 `InvalidateFromLayout()`。标准管线在绘制前有限次重建这些组件并补做样式和布局，让虚拟列表的大幅滚动与行挂载出现在同一帧。普通 `State` 写入仍按下一帧处理。
- **样式**：每个元素一个惰性创建的 `StyleElement` 适配对象，伪状态位按位强转；`::part()` 的宿主是最近的、属于另一个组件的祖先。启动时扫描样式表，找出用在祖先或兄弟位置的 class / id / 属性 / 状态选择器：这些值变化时重算整棵子树（`+` / `~` 时重算兄弟），其他变化只重算元素自身；继承值变化时重算子元素；样式表含结构伪类时，子元素增删移动会重算该父元素的全部子元素（偏保守）。样式差异只含绘制类属性时只标重绘，含布局或结构类属性时标重排 + 重绘；管线自身在帧内造成的变化不再请求新帧。
- **文本**：容器里的裸字符串（如按钮标签）成为使用父元素样式的匿名文本叶子；`Text`、`Input` 和 `Text` 之外的 `Span` 各自是一个段落，`Text` 里的 `Span` 是段落内的样式 run。每元素复用一个段落对象，只改颜色时替换段落但不使尺寸缓存失效；max-content 宽度加 0.01 px 余量，避免最后一个词被折行。
- **绘制是增量的**（第六批，细节见下文「增量绘制」）：只重录变化的元素，每帧报告一个损伤矩形；没有变化时复用上一帧，宿主跳过光栅。录制前自下而上算出每棵子树的可见范围（墨迹：含阴影、轮廓、溢出的文字），整棵在视口外的子树跳过 —— 1000 行列表的悬停帧因此从 7001 条命令降到 103 条（M1），增量后只剩 4 条顶层命令。
- **层叠顺序**（CSS 层叠顺序的简化版）：层叠上下文来自根、z-index 不为 auto 的定位元素与 flex / grid 项、opacity < 1（画进离屏图层），以及**总是建立层叠上下文的 `position: fixed / sticky`**；z-index 对定位元素和 flex、grid 项生效。层叠上下文内部按树序画「元素背景，再内容」，没有区分块级 / 行内 / 浮动层。
- **裁剪**：不按树序绘制的元素沿包含块链重新应用祖先的 overflow 裁剪 —— absolute 元素忽略包含块之外的裁剪，fixed 元素忽略全部祖先裁剪。**裁剪逃逸**（第六批修复 M1 终审的已知偏差：opacity 层叠上下文里的 absolute 元素被位于它与包含块之间的 overflow:hidden 祖先裁掉）：层叠结构变化时，每个 absolute / fixed 元素向上走到包含块（fixed 为根），路径上「自身与包含块之间有溢出裁剪」的层叠上下文标为 `ClipEscape`；这种上下文不整体应用外层裁剪链，而是背景、内容、轮廓与分层子元素各自应用自己的链。`ApplyClipChain` 止于当前裁剪作用域（正在录制的上下文），顺带去掉了嵌套上下文中圆角祖先裁剪的重复应用。扫描上限 4M 步，超出后所有上下文都走逐块路径。命中测试遵守同一规则。根元素的背景色铺满整个窗口（同浏览器的画布背景）。
- **盒绘制**：边框宽度向下对齐到设备像素（非零时至少 1 设备像素）；同色边框画成一个圆环，分边颜色画成梯形（圆角时裁剪），统一的 dashed / dotted 用描边；double、groove、ridge、inset、outset 画成实线。渐变色标按 CSS 规则补齐位置、保证不递减，色标提示近似为 50/50 色标；重复渐变以首尾色标的区间为周期。
- **命中测试**：按绘制顺序倒序重放，遵守 z-index、裁剪、`pointer-events: none` 与 `visibility`；只有最内层的圆角裁剪精确判断，外层圆角裁剪按矩形处理；`Text` 里的 `Span` 命中为所在的 `Text`。布局变化后重新评估悬停；CSS `cursor` 转发给窗口（`auto` 在输入框上显示文本光标）。命中测试表仍在每次重绘时遍历可见树重建（复用的子树不重录，但仍被遍历）。

### 增量绘制（第六批）

- **元素级录制单元**：每个元素（以及每个层叠上下文的内部）录成自己的、局部坐标的 DisplayList，在父级中用 `Translate` + `DrawDisplayList` 放置；嵌套至多 24 层（`MaxUnitDepth`，让光栅器的递归有界），更深的直接录进外层单元。`opacity: 0` 下不录制（只做命中测试）。
- **何时重录**：元素在绘制脏标记或自身几何变化时重录，其祖先随之重录；只是位置变了的元素在组合时平移即可（整列下移只重录 2 个元素）。缓存键（`PaintGeometry`）是几何签名 —— 边框尺寸、对齐后的内容偏移与内容尺寸（相对边框盒）、对齐后的内容框右 / 下边（替换元素的内容矩形用）、`Input` 的未对齐小数部分（文本垂直居中用）—— 加上相对父级的偏移；签名变化时元素及全部祖先变脏，只有偏移变化时只有父级及其祖先变脏。缓存还记录相对元素的剔除矩形与是否剔除过，内容滚入视口时会重录。缩放变化或 `InvalidateAll` 通过全局纪元使一切失效。
- **持久失效**（评审修复）：绘制脏标记每帧末清除，而一帧里没被录制的元素（被剔除、位于 `opacity: 0` 之下、在 `ClipEscape` 上下文里、超出嵌套上限）原来会在之后重用变化前的旧缓存（例如在屏幕外改了颜色再滚回来，仍显示旧色）。现在 `PaintPass` 在任何重用判断之前沿绘制脏路径给元素打 `RenderBox.DirtySerial`，失效一直保留到该元素真的重录，与它在哪一帧被访问无关；开销与已有的 `ComputeInk(dirtyOnly)` 遍历相当。
- **墨迹与损伤**：重算墨迹之前，把「自身绘制变化」的元素的旧墨迹（到该元素为止，它的墨迹覆盖子树）、几何变化的元素与被移除的子树加入损伤，重算后再加入新墨迹；根脏、视口变化或纪元变化时整帧受损。墨迹只沿绘制脏路径重算（除非跑过布局）；层叠上下文列表也缓存，只在结构、位置、z-index、opacity<1、display、overflow 变化后重新收集。段落墨迹包含字形溢出的估计（评审修复：line-height 小于字体内容区或斜体悬垂时字形会画出行框）：竖直方向外扩 max(0, (1.6em − 最小行高)/2)，水平 0.3em，em 取段落及其行内后代的最大字号（`TextParagraph.MaxFontSize`）。这是刻意的高估，字形特别高的字体仍可能超出，准确值需要 `AwAUI.Text` 提供墨迹包围盒。损伤是每帧一个包围矩形（`RenderPipeline.LastDamage`），所以从第 1 行悬停到第 3 行也会重画第 2 行；结构变化把父级标脏，损伤整个父级，偏保守但正确。
- **丢帧**（第七批）：`UIHost` 真正调用了 `Render` 且 `RenderTarget.LastFramePresented` 为 false 时，设 `_needsPresent`（下一帧把整个目标作为损伤重画）、`UIHost.DroppedFrames` 加一、这一帧的 `HostFrameStatistics.Rendered` 为 false，并 `RequestFrame()`；每个帧时钟节拍最多重试一次，不在帧内循环。损伤为空时不调用 `Render`，也就不读上一帧的旧值。只用模拟丢帧的假目标做了无头测试。
- **局部光栅化**：`UIHost` 把损伤交给后端（`RenderOptions.Damage`，`UIHost.LastDamage`），只在帧版本恰好前进 1、没有（也从未）显示 DevTools 覆盖层、且后端不是 GPU 时使用。`UIHost.PartialRaster` 默认每帧按后端当前的 `Capabilities` 判断（软件开、GPU 关），所以 GPU 后端运行中回退到软件后会重新打开；显式赋值后固定为该值。它依赖软件帧缓冲在帧间保留像素（`IFramebufferSurface` 的契约，见 [02](02-graphics-abstraction.md)）。
- **一致性**：随机差分测试（`IncrementalDifferentialTests`：随机的 div、文本、用 currentColor 的 SVG、输入框，22 个 class 覆盖 opacity、定位、z-index、overflow、display / visibility、轮廓、圆角、flex、小行高、负外边距、推到屏幕外；每个种子 60 步随机修改，偏重「隐藏子树、在里面改、再恢复」）要求每一步的增量结果（累积的局部光栅化）与关闭 `IncrementalPaint` 的整帧光栅化逐字节相同，命中测试在网格上一致。第七批起软件光栅与裁剪无关（见 [03](03-renderer.md)），测试不再容忍「raster depends on clip」（仍用这个标签诊断），固定的 11 步场景（`IncrementalPaintTests`）连同模糊阴影用例在 1x、1.5x、2x 下都要求逐字节一致（容差 0）。另有图片的局部光栅测试：缩放 1、1.5、2 下交替做 GIF 换帧与状态变化，局部与整帧逐字节相同。
- **诊断**：`RenderPipeline.IncrementalPaint`、`LastDamage`、`LastRecordedElements`；`FrameTimings.RecordedElements / ReusedElements`。DevTools 无需改动：高亮落在复用的缓存元素上也正确，显示高亮覆盖层时窗口整帧重画。

### 元素树变更通知的顺序

`ElementTreeObserver` 在一次宿主同步中依次收到：移除；从后往前的移动（`OnChildMoved(parent, child, before)`，`before` 是下一个非新插入的兄弟）；从后往前的插入。每次通知时，下一个兄弟都已处于最终位置，所以维护子节点顺序副本的观察者（DevTools 的树镜像）可以逐条增量放置，每次同步 O(子节点数)。有随机化测试核对镜像与真实树一致。

## 布局

布局语义**直接采用 CSS**（详见 08）：Flexbox、Grid、简化的 Block 流、绝对/固定/粘性定位，文本的行内排版只发生在 `Text` 内部。实现上是一个纯 C# 的 CSS 布局引擎：

- 内部协议仍是「约束下传、尺寸上报」，并支持 min-content / max-content 固有尺寸查询。
- 测量缓存 + 布局边界，避免全树重排。
- 布局在逻辑像素下计算，最终对齐到设备像素。
- 文本测量通过 `TextEngine`，`TextLayout` 结果缓存并直接用于绘制。

**实现（`AwAUI.Layout`，M1 第四批）**：

- 输入是不可变的 `LayoutStyle`（`sealed record`，可在节点间共享），节点是 `LayoutNode`（样式、有序子节点、叶子的 `LayoutMeasure`、`Owner` 反向引用、结果）。样式不等、结构变化或 `MarkDirty()` 会清除该节点直到根的缓存。
- **每次节点计算的输入**：（已知的边框盒尺寸、包含块尺寸、外边距盒的可用空间、运行模式 ComputeSize / PerformLayout、尺寸模式 Inherent / Content、是否需要基线、是否只求宽度 WidthOnly）。这个元组就是缓存键，每节点最多 6 个尺寸结果加最近一次完整布局。
- **缓存键规范化**（否则增量重排会把 6 槽缓存冲掉）：宽度已知时丢弃可用宽度；可用高度一律视为 max-content（水平书写模式下 auto 高度不依赖它）；样式中没有 %、calc、fit-content 的节点丢弃包含块尺寸。
- **只求宽度的查询**：固有宽度查询（content 的 flex-basis、min/max-content 贡献、尺寸关键字、行内 flex 项的自动最小尺寸、列 flex 项的交叉尺寸）只要宽度，算法在宽度确定后立即返回，不会在派生宽度下布局后代；这类查询还丢弃输入的已知高度（叶子与有 aspect-ratio 的节点除外）。这是每节点键数保持为小常数的关键：不这样做时嵌套的 flex 容器会随深度指数增长（评审中发现并修复）。代价：非叶子 flex 项求固有宽度时，被拉伸得到的确定高度不会传给后代，`height:100%` + aspect-ratio 这类依赖高度求宽度的后代按高度不确定处理。
- **叶子测量契约**：`MeasureConstraints` 是内容盒尺寸。确定的 Width 既可能表示「恰好」也可能表示「至多」（fit-content），引擎会覆盖它已经确定的轴；Height 只在高度已定而宽度未定时是确定值，其他情况为 max-content。
- 目前**没有布局边界**：`MarkDirty` 总是走到根（O(深度)），增量成本来自缓存（脏祖先重跑算法，干净子节点命中缓存）。实测 1 万节点树中一个叶子变脏约 0.15 ms。
- 数值防护：所有有限长度截断到 ±1e9 逻辑像素，flex 因子上限 1e9；非有限的样式值与测量结果被清洗；深树抛 `InsufficientExecutionStackException` 而不是崩溃进程。

具体算法范围与已知差异见 [08](08-css.md) 的「布局实现现状」。

## 事件

- 采用 DOM 式事件模型：捕获 → 目标 → 冒泡；`StopPropagation`、`PreventDefault`。
- 统一 Pointer 事件（鼠标/触摸/笔），其上是手势识别（见 09）。
- 键盘、焦点（Tab 顺序、焦点作用域、方向键导航）、快捷键（Ctrl ↔ Cmd 自动映射）。
- 事件处理器是组件上的强类型委托属性：`OnClick`、`OnPointerDown`、`OnKeyDown`、`OnInput`、`OnScroll`、手势类 `OnTap / OnLongPress / OnPan / OnPinch`；捕获阶段用 `On*Capture` 变体。
- M1 已实现分发、指针捕获、悬停、`:active`、点击合成、焦点与 Tab 顺序、快捷键、命中测试（细节见 [12](12-component-api.md)「元素与事件」与下文「M1 帧管线」）。第八批加入 `EventKind.Scroll / Wheel`、`ScrollEvent`（不冒泡，每帧至多一次）与可取消的 `WheelEvent`：`InputManager` 把滚轮与未被消费的滚动键转给滚动管理器，触摸拖动开始时向按下的元素发 `pointercancel`（`CancelPointerForScroll`），键盘或程序聚焦把元素滚到可见（见 [21](21-gesture-scroll-api.md)）。手势识别器尚未实现。
- **快捷键按逻辑键匹配**（第七批）：`KeyGesture.Matches` 与 `Shortcuts.ShortcutKey(physical, logicalKey)` 对字母键用逻辑键（QWERTZ 上的 Ctrl+Z、AZERTY 上的 Ctrl+A 都正确）；逻辑键不是 ASCII 字母时（非拉丁布局、数字与标点、平台没提供逻辑键）退回物理键，与浏览器和主流桌面程序一致。`Shortcuts.IsPrimaryShortcut` 排除 Alt 与 AltGraph（AltGr 输入的字符不会触发 Ctrl+Alt 快捷键）。`KeyEvent` 新增 `Location`。

### 文本输入（`Input`，第七批）

`InputElement`（移到 `Input/InputElement.cs`）经 `InputTextClient` 实现 `ITextInputClient`（见 [04](04-platform.md)「IME / 文本输入」），编辑逻辑在 `InputManager` 的 partial 文件 `Input/InputManager.TextEditing.cs`：

- **状态**：选区（锚点 / 焦点：`SelectionStart / End / Anchor / Focus`、`SelectedText`、`SetSelectionRange`、`SelectAll`）、预编辑（`IsComposing`、`CompositionText`、`CompositionStart`、`CompositionSelectionStart / End`）与水平滚动。`Value` 只含已提交的文本，组合期间受控组件的值不会被预编辑污染；组合期间不派发 `input` 事件，提交后才派发。
- **焦点**：获得焦点时把客户端交给窗口的 `TextInputMethod`（`InputManager.TextInputMethod / Clipboard`），失焦时提交预编辑并断开；每帧之后（`InputManager.AfterFrame`）如有变化就调用 `NotifyStateChanged`（光标矩形用于候选窗定位）。受控值被程序改变时光标移到末尾并放弃组合；组合中的输入框被移出树时静默丢弃预编辑（不派发事件），插入点留在原来组合的起点。
- **编辑键**：退格 / 删除 / 方向键按字素簇；按词操作（macOS 用 Option，其他平台用 Ctrl）；macOS 的 Cmd+方向键到行首 / 行尾；Home / End；Shift 扩展选区。小键盘在 NumLock 关闭时按逻辑键（Home、ArrowLeft、Delete……）处理（`InputManager.EditingKey`）。编辑命令可能在两帧之间连续到达，所以字素簇与词边界不依赖段落布局：`TextEditing` 实现扩展字素簇规则的常用子集（组合记号、ZWJ、变体选择符、肤色修饰、标签、区域指示符对、CRLF），向后回溯至多 64 个码点（病态输入上一次退格是常数时间）；词边界按字符类别划分，汉字和假名一字一词，没有词典切分。命中测试与候选窗矩形只在绘制时记录的显示文本与当前内容一致时才用布局，否则退回最近一次绘制的光标位置。
- **剪贴板**：Mod+A / C / X / V 按逻辑键匹配；平台 `HandlesClipboardShortcuts` 为 true（Browser）时由平台经浏览器事件处理。粘贴支持异步剪贴板，结果回到 UI 线程；单行输入框按 HTML 的值清洗规则去掉换行（多行文本合并成一行）；值的长度受 16M 码元上限约束。密码框不能复制，也不接受预编辑。剪切在已知写入失败时不删除选区（见 [04](04-platform.md)）。
- **指针**：点击定位光标、拖动选择（捕获指针）、双击选词、三击全选（只按真实时间戳判断）。
- **绘制**（`Input/RenderPipeline.Input.cs`）：预编辑文本加下划线，转换中的文节加粗；选区高亮；文本超宽时水平滚动并裁剪。`PaintParagraph` 里有一处转交给输入框绘制的钩子，旧的文末光标代码已删除。
- **测试**：无头端到端（`TextInputTests` 与评审补的 `TextInputReviewTests`）：中文组合 / 提交 / 取消、组合替换选区、失焦提交、受控值变化放弃组合、自动更正式的 `ReplaceRange`、粘贴 / 复制 / 剪切（Ctrl 与 Cmd 两种约定、剪贴板拒绝写入、抛异常）、德语 AltGr 输入 @ € { |、逻辑键快捷键、小键盘、编辑键、光标矩形、滚动、指针。
- **多行（9c）**：`TextArea` 复用编辑器与输入法客户端，支持换行、自动折行、选区、剪贴板和保持光标可见的垂直滚动。`Enter` 插入换行；单行 `Input` 仍用 `Enter` 提交。
- **未做**：撤销 / 重做；光标闪烁；双向文本的视觉光标移动（左右键按逻辑顺序移动）；输入法重新转换；失焦时的非活动选区显示；没有调度器且剪贴板任务没有同步完成时这次粘贴被丢弃；`GetText` 的范围切开代理对时返回的字符串比请求的长 1。

## 动画

- **CSS transition / animation / @keyframes** 覆盖绝大多数需求，纯声明。
- **命令式动画 API**（弹簧、手势跟随、可中断）：通过 `Ref` 或 `AnimatedValue` 直接驱动样式值，不经过组件重建。
- 仅涉及 `transform / opacity / 滚动` 的动画提交合成器，在渲染线程推进。
- 进出场动画：协调器在移除元素前给出退出阶段（否则声明式下元素会直接消失）。尚未实现，目前移除是立即的。
- 尊重 `prefers-reduced-motion`。

## 绘制与层

- 盒子的背景、边框、圆角、阴影、渐变、裁剪由框架的「盒绘制器」按 ComputedStyle 录制到 DisplayList；`Canvas` 元素由用户代码录制；`img` 与 `svg` 是替换元素（`ReplacedElement`）。
- 已实现（第六批）：每个元素（及每个层叠上下文的内部）缓存为局部坐标的 DisplayList，以 `Translate` + `DrawDisplayList` 放置，嵌套至多 24 层；按绘制脏标记（持久的 `DirtySerial` 失效）或自身几何变化重录，只有偏移变化时在组合时平移；每次重绘报告一个损伤矩形，软件后端只光栅化该矩形。还没有层树与层提升（见 [18](18-scene-compositor.md)），下一条仍是目标。
- 层提升条件：合成器动画、可滚动内容、`will-change`、需要离屏的效果（组透明度、filter、backdrop-filter、带圆角的溢出裁剪）。提升原因记录在案，DevTools 可见。

## 状态与数据

- `State<T>`、`Computed<T>`、`Effect`：响应式原语，组件内外通用，可作为跨组件的 Store。
- 变更在帧的 Build 阶段批量生效，同一帧多次修改只重建一次。
- 异步：`Resource<T>`（加载中/成功/失败三态）配合 `Build()` 中的模式匹配渲染（尚未实现）。
- 不需要绑定表达式、`INotifyPropertyChanged`、值转换器这一整套机制。
