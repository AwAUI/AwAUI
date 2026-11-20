# 08 样式：直接支持 CSS

目标：开发者（和 AI）写**标准 CSS 语法**，框架原生理解，而不是发明一套「类 CSS」。实现为纯 C# 的 CSS 引擎：解析器、选择器匹配、级联、值计算、布局。

## 原则

1. **语法与语义遵循 W3C 规范**。支持的特性行为与浏览器一致；不支持的特性**明确报告**（构建期警告 + DevTools 中标记），绝不静默忽略。
2. **支持的是「应用 UI 子集」**，不是文档排版引擎。不做浮动、表格布局、多栏、完整的行内格式化上下文。
3. **非标准扩展一律加前缀** `-aw-`，不污染标准命名空间。
4. **强类型落地**：CSS 文本最终变成强类型的 `ComputedStyle` 结构体，布局与绘制不接触字符串。

## 样式从哪里来

| 来源 | 写法 | 说明 |
|---|---|---|
| 全局样式表 | `app.css`、主题文件 | 作为构建输入，源生成器在编译期解析、校验、预编译为二进制规则表并嵌入 |
| 组件作用域样式 | `Counter.cs` 旁放 `Counter.css` | 自动限定作用域（类似 CSS Modules），不会泄漏到其他组件 |
| 内联样式 | `Style = "padding: 8px"` 或强类型 `Style = new() { Padding = 8 }` | 字符串常量在编译期由生成器解析校验 |
| 运行时样式表 | `StyleSheet.Parse(text)` | 动态主题、DevTools 实时编辑、热重载都走这条路径 |
| 默认样式表 | 框架内置 | 相当于浏览器 UA 样式表，定义内建元素与标准控件的默认外观 |

运行时解析器始终存在（DevTools 与热重载需要），但发布构建可通过特性开关裁剪掉，只保留预编译规则。

**编译期辅助**：分析器检查 C# 中使用的 class 名是否在任何样式表中定义；未知属性、非法值、不支持的特性都是带位置的诊断信息 —— 这对 AI 生成代码的自我纠错尤其有用。

## 级联

完整实现 CSS 级联排序：

```
来源与重要性 → @layer → 特异性 → 出现顺序
```

来源从低到高：默认样式表 → 应用样式表 → 内联样式 → `!important` → 过渡/动画 → DevTools 覆盖。

按 CSS Cascade 5 落地后的完整层级（`AwAUI.Css.Cascade.StyleEngine`，从低到高）：

```
UA 普通 < 作者普通 < 内联 < [动画槽] < 作者 !important < 内联 !important < UA !important < [过渡槽] < DevTools 普通 < DevTools !important
```

- 样式表以 `AddStyleSheet(sheet, CascadeOrigin)` 加入，来源为 `UserAgent / Author / DevTools`（默认 Author）。动画与过渡两个槽位已预留但为空（第 2 期由动画引擎填入）。
- `@layer`：跨样式表生效，支持匿名层、嵌套层与 `@layer a, b;` 语句；普通声明中未分层的规则最后（最高），`!important` 中未分层的最先（最低）。**位于不匹配的 `@media` 内的层声明不参与层排序**（同 CSS Cascade 5 与 Chromium/Firefox）；这类条件翻转时引擎自动重编译。
- 特异性取选择器列表中**命中的最具体**那个；出现顺序跨所有样式表计算。
- `revert`：在 DevTools 来源中回退到作者来源；内联样式按作者来源处理；内联上的 `revert-layer` 只跳过其他内联声明。由 `var()` 替换产生的 `revert / revert-layer` 目前按 `unset` 处理。
- `@import`：只为 `layer()` 注册层序，被导入的样式表本身不由引擎加载，由宿主按正确顺序 `AddStyleSheet`。
- `EnvironmentChanged()` 重新求值 `@media` 门控，返回是否有结果变化；引擎有待重编译（增删样式表、层序变化）时也返回 true，此时调用方应全量重算样式。使用视口单位或 `env()` 的样式在 `ComputedStyle.Flags` 上标记 `DependsOnViewport / DependsOnEnvironment`，视口变化只需重算这些元素加上 `@media` 结果变化影响的部分。
- `StyleEngine` 复用暂存缓冲，**非线程安全**；需要并行计算样式时每线程一个实例。

- **继承**：按规范区分可继承属性（`color`、`font-*`、`line-height`、`direction` 等）与不可继承属性；支持 `inherit / initial / unset / revert`。
- **自定义属性**：`--token: value` 与 `var(--token, fallback)`，这是主题与设计令牌的承载机制。`var()` 与 `env()` 替换支持回退值与环依赖检测（环上所有属性无效，未使用的回退值里的引用也计入依赖图）；替换后无效的值按 `unset` 处理。
- **匹配性能**：规则按最右侧选择器的 class / id / tag 分桶；祖先布隆过滤器快速排除；相同样式的元素共享 ComputedStyle；class 或伪状态变化时只对可能受影响的子树重新匹配。
  - 现状：候选规则来自 `SelectorIndex`，声明在加入样式表时每条规则只解析一次，逐元素的暂存缓冲复用。**「相同样式的元素共享同一个 ComputedStyle」（匹配属性缓存）尚未实现**，目前只共享属性组（见 [16](16-css-properties.md)），每元素约分配 1.36 KB。
  - 实测（2000 元素随机树 × 500 条规则，含 class/复合/子代/`:hover`/id/后代/`@media` 选择器与 `var()`、calc、渐变、简写；机器同时有其他负载）：每次全树计算约 13.2–13.6 ms（约 6.6–6.8 µs/元素），负载更重时测得 18.9 ms；解析并编译这 500 条规则约 23–25 ms。
- **匹配记录**：每个元素保留命中的规则列表及每个属性的胜出声明 —— 这就是 F12 样式面板的数据源，与浏览器 DevTools 的展示完全同构。实现为可选的 `StyleMatchRecord`：按优先级列出命中规则（来源、层名、特异性、命中的选择器、媒体链、样式表、源范围），以及每个长属性的状态（生效、被覆盖、无效（含替换后无效）、不支持）。空规则（`div {}`）目前不出现在记录中。
- **伪元素**：`Resolve(element, parentStyle, PseudoElementKind, record)` 计算 `::before / ::after`，继承自原元素，忽略内联样式。

## 支持范围

逐属性的类型、继承、可动画性、影响阶段与实现分期见 [16-css-properties.md](16-css-properties.md)。

### 选择器

| 支持 | 后置 |
|---|---|
| 类型、`.class`、`#id`、`*`、属性选择器 `[attr]` `[attr=v]` 等 | `:has()` |
| 组合器：后代、`>`、`+`、`~` | `::first-line` `::first-letter` |
| `:hover :active :focus :focus-visible :focus-within :disabled :enabled :checked :placeholder-shown` | |
| `:first-child :last-child :nth-child() :only-child :empty :root` | |
| `:not() :is() :where()` | |
| `::before ::after ::placeholder ::selection ::part()` | |
| CSS 嵌套（`&`） | |

### at 规则

| 支持 | 后置 |
|---|---|
| `@media`：`width/height`、`orientation`、`prefers-color-scheme`、`prefers-reduced-motion`、`hover`、`pointer`、`resolution` | `@supports` |
| `@keyframes`、`@font-face`、`@import`、`@layer` | `@container` |

### 值与单位

`px`、`%`、`em`、`rem`、`vw/vh/vmin/vmax`（含 `dvh` 等动态视口单位）、`fr`、`deg/turn`、`s/ms`；`calc() min() max() clamp()`；颜色 `#hex / rgb() / hsl() / 命名色 / transparent / currentColor`（`oklch()`、`color-mix()` 后置）；`env(safe-area-inset-*)`（移动端刘海与手势条）。

M1 已实现：全部绝对单位（px、cm、mm、q、in、pt、pc）、em/rem/ex/ch、vw/vh/vmin/vmax/vi/vb 及 sv\*/lv\*/dv\* 视口单位、百分比；calc/min/max/clamp 按 CSS Values 4 做类型检查（嵌套上限 32 层、1024 个节点）；颜色 hex、rgb()/rgba()/hsl()/hsla()（逗号与空格两种语法）、148 个命名色、transparent、currentColor（保留为关键字，使用时解析）；线性/径向渐变（含 repeating、色标提示、双位置色标）、url() 图像、盒阴影、字体族、字号关键字（含 larger/smaller）、bolder/lighter。ex 与 ch 因为没有字体度量暂按 0.5em 近似。尚未实现：`image-set()`、`conic-gradient()`、`color-mix()`、`oklch()`、`hwb()`、系统颜色、`font-style: oblique <angle>`；cursor 的图像 URL 被解析后丢弃，只保留回退关键字；`<position>` 废弃的三值形式被拒绝。

### 布局

| 特性 | 状态 |
|---|---|
| `display: flex / inline-flex` | 完整 Flexbox |
| `display: grid / inline-grid` | 第八批实现：轨道、`fr`、`repeat()`、`minmax()`、`fit-content()`、`auto-fill/auto-fit`、线名与区域命名、自动放置（含 dense）、`gap`、对齐；`subgrid`、masonry、基线对齐未做 |
| `display: block` | **简化版**：块级子元素纵向堆叠、宽度撑满；不做外边距折叠与浮动 |
| `display: none / contents` | 支持 |
| 逻辑属性（`margin-inline`、`padding-block-start`、`inset-inline`、`border-block` 等 42 个） | 第八批：解析时映射为左到右横排下的物理属性（决策 D49）；不支持 `direction` / `writing-mode`，所以 rtl 与竖排下的映射不对 |
| 行内排版 | 仅在 `Text` 元素内部（`Span` 嵌套、行内图片） |
| `position: relative / absolute / fixed / sticky`、`inset`、`z-index` 与层叠上下文 | 支持 |
| 盒模型：`width/height/min-*/max-*`、`margin`、`padding`、`border`、`aspect-ratio`、`box-sizing` | 支持 |
| `overflow: visible / hidden / clip / scroll / auto`、`scroll-snap-*`、`overscroll-behavior`、`scroll-behavior` | `overflow` 支持（第八批起 auto / scroll / hidden 可滚动，见 [21](21-gesture-scroll-api.md)）；其余是计划中的属性 |
| `float`、`display: table*`、多栏 | **不做** |

#### 布局实现现状（`AwAUI.Layout`，M1）

- **Flexbox** 按 Flexbox L1 §9 实现：基础与假设尺寸、§4.5 自动最小尺寸（惰性计算）、带 gap 的分行、容器主尺寸（收缩适应时为 fit-content）、伸缩长度的 min/max 冻结循环、交叉尺寸、stretch、单行基线对齐（含嵌套容器基线）、auto 外边距、justify-content / align-content 及负剩余空间的规范回退、wrap / wrap-reverse / *-reverse、rtl、按书写方向解释 `start / end`、百分比 gap。
- **Grid**（第八批，`LayoutEngine.Grid.cs`）：显式与隐式网格（隐式轨道可在第 1 条线之前，`grid-auto-*` 在显式网格前后都循环使用，与 Chromium 一致）；按线号、线名、区域放置；自动放置（行 / 列、稀疏 / 稠密，占用表按放置方向分带存储，碰撞检查直接跳过阻挡的区间；稠密放置的起始行只前进）；轨道尺寸算法 §11.5 第 2–5 步（计划增量与项目增量、按容量 / 权重排序的注水分配、「可无限增长」标记、按伸缩因子分给 fr 轨道、不确定空间下的 fr 尺寸、容器 min/max 参与 fr 计算；贡献在任何分配之前全部算完，因为计算贡献可能递归布局共享临时状态的嵌套网格）；gap、对齐、rtl、百分比轨道、min/max；绝对定位子元素以网格区域为包含块；基本的容器基线。列方向的内容分布在行尺寸之前施加，网格区域因此包含额外的间距；自动高度网格的行按 max-content 约束计算，`min-height` 确定时 auto 行拉伸到它。
  - 项目的自动最小尺寸按 §6.6：有指定尺寸用它，否则用 min-content（与 flexbox 的 min(指定, 内容) 不同）；滚动容器、以及跨多条轨道且含 fr 轨道的项目为 0；只跨「最大尺寸函数固定」的轨道时钳制到这些轨道与其间 gap 之和（fit-content 不算固定；只作用于 auto 最小值）。
  - normal 对齐按 §6.2：没有宽高比的项目拉伸；有宽高比的非替换元素在行内轴撑满、块轴高度由比例推出；替换元素（`IsReplaced`）两轴都按 start。
  - **Grid 的上限**（不可信 CSS，决策 D48）：线号钳制到 ±10000（`GridLine.MaxLine`），显式轨道至多 9999 条，自动放置不越过第 10000 条线（越界的项目被钳制，可能重叠），`repeat()` 次数钳制到 10000，至多记录 131072 个线名；占用表的内存与跨度数值无关。轨道尺寸算法对起止相同的项目合并分配（同一阶段项目增量随贡献单调不减，计划增量取最大值，所以只用最大贡献分配一次，结果不变），最坏开销仍是 O(项目数 × 轨道数)：5000 个起止各不相同、贡献递增的项目跨 5000 到 10000 列约 1.5 s CPU（高负载下）。实测（优化构建，负载 13–40）：`repeat(auto-fill, minmax(100px, 1fr))` 下 1 万个自动放置的项目首次布局 13–16 ms、改宽度后 6–10 ms；`grid-row: 1 / 100000000`、`repeat(INT_MAX, 1px)`、2000 个各跨 5000 列的项目、50 万格的区域字符串都有 CPU 时间或线性测试守护；嵌套 40 层的开销与深度成正比。
  - 已知差异：spanning 项目跨 fit-content 轨道时按 §11.5.1（超出上限的空间分给仍「开放」的 fit-content 轨道）：两条 `fit-content(50px)` 列、一个 min-content 为 300 的跨两列项目、第 1 列另有一个空项目时，这里是 250/50，Chrome 是 0/300。
- **`order`**（第八批，第 1 期）：flex 与 grid 项目按（order，文档顺序）稳定排序后布局；绘制顺序同样按 order 修正后的文档顺序（CSS Display 3，`LayoutMapper.PaintOrder`，z-index 相同的层叠项也按它排序），命中测试按绘制的逆序，随之一致。
- **Block**（简化）：子元素纵向堆叠，auto 宽度填满包含块减外边距，水平 auto 外边距居中，rtl 靠右；不做外边距折叠与浮动。
- **定位**：relative 在放置时偏移；absolute 相对最近定位祖先的内边距盒（没有则为视口），fixed 总是相对视口；支持所有 inset 组合、auto 外边距居中、收缩适应、过约束（ltr 忽略 right，rtl 忽略 left）与静态位置（flex 父元素下按「唯一 flex 项」计算）。左右 inset 都为 auto 时在「静态位置到包含块远端」的范围内收缩适应（css-position-3 §4.1）。
- **固有尺寸**：block、行/列 flex 容器的 min-content / max-content / fit-content 贡献；关键字可用于 width / min-width / max-width（用在高度上按 auto / none 处理）。行 flex 容器的固有尺寸按 Chromium 而非 §9.9.1 的 flex 分数算法：max-content = 各项 max-content 贡献之和加 gap；min-content = 各项 min-content 贡献之和（nowrap）或最大值（wrap）；此时忽略 flex-basis。收缩适应的行 flex 容器在求固有尺寸时把项目的百分比视为不确定，最终布局再按容器最终内宽解析（与浏览器一致）。
- `display: none` 整棵子树为零盒；`display: contents` 把子节点提升到父节点，自身为 (0,0) 处的零盒；`aspect-ratio`、`box-sizing` 已支持（第七批起 CSS `aspect-ratio` 是第 1 期属性，`LayoutMapper` 对所有元素映射；替换元素与自然比例的合并见 [05](05-ui-core.md)）。第八批：宽高都为 auto 时，`ComputeLeaf` 用自身 min/max-width 与经比例传来的 min/max-height 的交集钳制宽度，再由宽度推出高度；替换元素（`LayoutStyle.IsReplaced`，UI 经 `LayoutMapper.ForReplaced` 设置）按测量尺寸的比值作用于内容盒、不加自动最小高度，块布局与 left + right 的绝对定位都不拉伸其 auto 宽度；非替换元素（block / flex / grid 容器与文本叶子）按样式比值与 box-sizing，由比例推出的高度不小于内容高度（CSS Sizing 4，再受 max-height 约束；flex 与 grid 容器把它变成「带内容高度的 min-height」，代价是子元素的百分比高度变为不确定，block 容器保持比例高度确定）；`inline-flex` 在 block 中像 block 一样堆叠但宽度为 fit-content（没有行内格式化上下文）。
- `RoundToPixels` 对齐的是盒的边而不是尺寸，相邻盒之间不留缝。
- 实测（10 101 个节点，50 段 × 4 行 × 49 个文本叶子，net10.0，机器同时有其他负载，故为上界）：视口宽度变化后全量重排约 10 ms（约 6 667 次算法运行、每次约 13 字节分配）；视口不变的干净重排 0.001–0.004 ms；一个叶子变脏 0.15–0.3 ms。尚未用 BenchmarkDotNet 在空闲机器上复测。
- 尚未实现：`sticky`、宽度上的 `fit-content(<length>)` 函数（网格轨道中的已支持）、竖排、rtl 下的逻辑属性、flex 自动最小尺寸中的 transferred size、last-baseline；列容器与 wrap-reverse 的基线对齐回退为 flex-start；网格的基线对齐、subgrid、masonry 与 §11.1 第 3、4 步；`safe / unsafe` 对齐关键字与 `legacy left / right`；没有布局边界。
- **UI 映射时的回退**（`AwAUI.UI` 把 `ComputedStyle` 映射为 `LayoutStyle` 时；M1 没有行内格式化上下文）：`display: inline` 与 `inline-block` 都按收缩适应（shrink-to-fit）的盒子布局，`inline-block` 像 block 一样纵向堆叠子元素；`grid` / `inline-grid` 第八批起映射为网格（轨道等计算值按实例缓存转换、按值比较，相同的样式不触发重排）；`position: sticky` 按 relative 布局（但总是建立层叠上下文）；`order` 同时作用于布局与绘制顺序。负的百分比 padding 在映射 / 布局时截断为 0。
- 已知差异：交叉轴上的百分比 gap 在容器交叉尺寸不确定时按 0 处理且之后不重新解析（flex 下与 Chromium 一致）；auto 宽度、收缩适应语境中的列向换行 flex 容器取最宽项的宽度而不是各列之和（Chromium 同样如此）；space-around / space-evenly 在负剩余空间时回退为 center（Flexbox L1），未采用 Box Alignment 3 的 safe center。

### 视觉

`background`（颜色、多重、渐变、图片、`size/position/repeat/clip`）、`border`（各边宽度/颜色/样式）、`border-radius`、`box-shadow`（含内阴影）、`outline`、`opacity`、`transform`（2D；3D 后置）、`transform-origin`、`filter`、`backdrop-filter`、`clip-path`（基本形状）、`mask`（后置）、`mix-blend-mode`、`visibility`、`cursor`、`will-change`。

M1 已绘制：背景色、线性 / 径向 / 重复渐变、边框（分边颜色、圆角；统一的 dashed / dotted；double、groove、ridge、inset、outset 画成实线；相邻且颜色不同的圆角边在角上可能有一条淡线）、内外 `box-shadow`、`outline`（第 1 期，见 [16](16-css-properties.md)）、`opacity`（离屏图层）、overflow 裁剪、`visibility`、`cursor`。第一个色标不在 0 的 `repeating-radial-gradient` 未做相位补偿（近似）。背景图片、`transform`、`filter`、`clip-path`、`mix-blend-mode` 尚未接入。`::before / ::after` 会计算样式但不渲染。

### 文本

`font-family / size / weight / style / stretch`、`font-feature-settings`、`font-variation-settings`、`line-height`、`letter-spacing`、`word-spacing`、`text-align`、`text-decoration`、`text-transform`、`text-overflow`、`white-space`、`word-break`、`overflow-wrap`、`line-clamp`、`text-shadow`、`direction`、`unicode-bidi`；`writing-mode`（竖排）后置。

### 动效与交互

`transition`、`animation`、缓动函数（`cubic-bezier()`、`steps()`、`linear()`）；`pointer-events`、`user-select`、`touch-action`（触摸手势裁决的关键，见 09）、`caret-color`、`accent-color`。

## 与浏览器的已知差异（已确认）

| 差异 | 理由 |
|---|---|
| 默认样式表设 `box-sizing: border-box` | 应用 UI 的通行做法，几乎所有项目都会重置成这样 |
| `display: block` 不做外边距折叠 | 折叠规则复杂且在应用 UI 中多为困扰 |
| 文本节点不能与块级元素混排 | 避免实现完整的行内格式化上下文；文本必须放在 `Text` 里 |
| 没有 HTML 元素的默认语义样式 | 元素集是 `Div/Text/...` 而不是 HTML 标签 |

这些差异要么在默认样式表里体现（可被覆盖），要么在构建期给出明确诊断。

## 主题

- 主题 = 一组 CSS 自定义属性 + 可选的覆盖样式表。明暗主题通过 `@media (prefers-color-scheme)` 或根元素上的 class 切换。
- 标准控件的所有颜色、圆角、间距都引用自定义属性，换主题只需重定义变量。
- 运行时切换主题只触发样式重算，不重建组件。

## 实现模块

| 模块 | 内容 |
|---|---|
| `AwAUI.Css.Syntax` | 符合 CSS Syntax Level 3 的分词器与解析器，带错误恢复与源位置 |
| `AwAUI.Css.Selectors` | 选择器解析、特异性、匹配、失效集计算 |
| `AwAUI.Css.Values` | 属性定义表（由一份声明式的属性清单源生成：解析、初始值、是否继承、是否可动画、影响哪个阶段）与强类型值 |
| `AwAUI.Css.Cascade` | 级联、继承、`var()` 替换、计算值（`StyleEngine`） |
| `AwAUI.Layout`（独立程序集） | Block / Flex / Grid / 定位 的布局算法 |
| `AwAUI.Css.Animation` | 过渡、关键帧、插值（未开始） |

M1 现状：Syntax、Selectors、Values、Cascade 已实现，`AwAUI.Css` 仍只依赖 Foundation（分词器按 UTF-16 处理，只在行内检查代理对，没有用到 Unicode 的必要）。元素通过 `StyleElement` 抽象接入：新增了虚属性 `StyleElement.InlineStyle`（返回 `CssInlineStyle`，由 `CssInlineStyle.Parse` 从 style 文本得到）。

布局引擎拆成独立程序集 `AwAUI.Layout`（与 Yoga、Taffy 的做法相同），**只依赖 `AwAUI.Foundation`**：它只接受自己的强类型输入 `LayoutStyle` 和节点抽象，不知道 CSS 值如何表示。UI 层在影响布局的计算值（[16](16-css-properties.md) 中标 L / S 的属性）变化时，把 `ComputedStyle` 映射成 `LayoutStyle`；`calc()`、`min()`、`max()`、`clamp()` 中仍含百分比的表达式由 Css 一侧实现布局层的抽象类 `LayoutCalc`（`Resolve(percentBasis)`，基准不确定时传 NaN）。`LayoutStyle` 的相等比较对 `LayoutCalc` 用 `object.Equals`，布局缓存也假设 `Resolve` 是纯函数，所以实现者必须提供值相等（或复用实例），否则每次级联都会被当成样式变化而触发重排。好处是两边可以独立演进和测试，布局引擎也能脱离 CSS 单独使用（例如 `Canvas` 内的自定义排版）。

映射时还要注意：Css 只能把纯像素的结果截断为非负，含百分比的 `padding`、`width`、`gap` 等（如 `calc(10% - 100px)`）只能在布局时解析后再截断为非负，这一步由映射 / 布局一侧负责。

正确性保障：选取 Web Platform Tests 中与所支持子集对应的用例（选择器、级联、Flexbox、Grid）转成自动化测试，把「行为与浏览器一致」变成可度量的指标。
