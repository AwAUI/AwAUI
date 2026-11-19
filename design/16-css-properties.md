# 16 CSS 属性清单

[08](08-css.md) 定义了范围，本文定义每个属性在引擎中的落地方式：计算值类型、是否继承、是否可动画、变化时影响帧管线的哪个阶段、在哪一期实现。

## 属性清单即代码

所有属性定义在一份声明式清单里（`src/AwAUI.Css/Properties.json`），由 `CssPropertyGen` 工具生成：属性 ID 枚举、值解析器、`ComputedStyle` 的字段与访问器、初始值、继承与脏标记逻辑、插值函数、简写展开、DevTools 的属性元数据、`.awaui` 与内联样式的编译期校验表。清单是唯一事实来源，新增一个属性只改这一处。

```json
{
  "name": "border-top-left-radius",
  "type": "Radius",
  "initial": "0",
  "inherited": false,
  "animatable": "by-computed-value",
  "affects": "paint",
  "group": "Border",
  "phase": 1
}
```

清单的顶层是 `groups`、`enums`、`longhands`、`shorthands`、`planned`、`unsupported` 六个数组：

| 字段 | 说明 |
|---|---|
| `type` | 取自 `CssPropertyGen` 内固定的类型表（`Keyword`、`Size`、`LengthPercentage`、`LineWidth`、`Number`、`Alpha`、`Color`、`Radius`、`ImageList`、`ShadowList`、`FontFamily`、`FontSize`、`FontWeight`、`FontStretch`、`LineHeight`、`IntegerOrAuto`、`Cursor` …），决定解析器与计算值类型 |
| `enum` / `flags` | `Keyword` 类型对应的关键字枚举；附加标志如 `Auto`、`None`、`Normal`、`Content`、`Intrinsic`、`NonNegative`（计算值截断为非负；允许负值的属性不写这个标志即可） |
| `animatable` | `no` / `discrete` / `by-computed-value` |
| `affects` | `structure` / `layout` / `paint` / `composite` / `none`，对应下表的 S / L / P / C / — |
| `planned` | 尚未支持但已排期的属性及其期，AW2002 据此提示「计划在第 N 期支持」 |
| `unsupported` | 不打算支持的属性及中文替代建议：`float`、`clear`、`columns`、`column-count`、`column-width`、`table-layout`、`border-collapse`、`border-spacing`、`vertical-align` |

M1 清单：9 个组、20 个关键字枚举、74 个第 1 期长属性、20 个简写（`margin` `padding` `inset` `border` `border-top/right/bottom/left` `border-width/style/color` `border-radius` `outline` `flex` `flex-flow` `gap` `overflow` `background` `font` `all`）。M1 整合时把 `outline-width / -style / -color / -offset` 与 `outline` 简写从第 2 期提前到第 1 期（属于 Effects 组），供焦点轮廓使用。`CssPropertyGen`（`src/tools/CssPropertyGen`，net10.0）把它生成为 `src/AwAUI.Css/Generated/*.g.cs` 五个入库文件：属性 ID 枚举、元数据表、关键字枚举与表、解析分派、`ComputedStyle` 的组类与逐属性访问器、写时复制的构建器、差异比较与计算值序列化。生成逻辑同时编译进测试项目，单元测试在内存中重新生成并在入库文件过期时失败；命令行 `--check` 做同样的检查。第七批把 `aspect-ratio`（Box 组）、`object-fit` 与 `object-position`（Effects 组，只影响绘制）、`image-rendering`（继承，放在 Text 组，避免新增组或改动 `RenderPipeline.InheritedChanged`）从计划移入第 1 期，清单现为 22 个关键字枚举、78 个第 1 期长属性、20 个简写。

## 影响阶段

| 标记 | 含义 | 变化时 |
|---|---|---|
| **L** 布局 | 改变盒子尺寸或位置 | 重排（到布局边界为止）+ 重绘 |
| **P** 绘制 | 只改变外观 | 仅重录该元素的 DisplayList |
| **C** 合成 | 只改变层属性 | 不重排、不重录，可在渲染线程动画 |
| **S** 结构 | 改变盒树结构或层叠上下文 | 重建该子树的布局对象，然后 L |
| **—** | 不产生视觉变化 | 只更新行为状态 |

## ComputedStyle 的存储

- 强类型结构，按变化频率与继承性**分组**：`Box`、`Layout`（flex/grid）、`Position`、`Border`、`Background`、`Effects`、`Transform`、`Text`（继承）、`Font`（继承）、`Interaction`、`Animation`。
- 每组是引用计数的不可变对象，写时复制：绝大多数元素的大多数组等于初始值或与父/兄弟相同，直接共享指针。继承类的组在子元素未覆盖时与父元素共享同一实例。
- 自定义属性（`--*`）存于一棵持久化映射，子元素共享父元素的映射并只记录差异。
- 长度类的值保留为「计算值」（如百分比、`calc()` 表达式树），在布局时才解析为像素。

M1 的实际实现（`AwAUI.Css.ComputedStyle`，不可变 `sealed class`）：

- 9 个组，类型为 `BoxStyleGroup`、`PositionStyleGroup`、`FlexStyleGroup`（即上文的 `Layout` 组）、`BorderStyleGroup`、`BackgroundStyleGroup`、`EffectsStyleGroup`、`FontStyleGroup`（继承）、`TextStyleGroup`（继承）、`InteractionStyleGroup`（继承），以 `BoxGroup`、`PositionGroup` … 属性暴露（加 `Group` 后缀是因为长属性访问器 `Position` 与组名冲突）；另有每个长属性一个扁平访问器。`Transform`、`Animation` 组没有第 1 期属性，暂未生成。
- 一个组只放继承性相同的属性：所以 `visibility`（继承）在 `Text` 组，`text-overflow`（不继承）在 `Effects` 组。
- 组与初始样式或父样式的对应组相等时按引用共享，只有值真正变化才复制（不是引用计数，靠 GC）。相同样式的元素共享整个 `ComputedStyle`（匹配属性缓存）尚未实现。
- 自定义属性存于与父共享的持久化映射 `CustomPropertyMap`，链长超过 8 时扁平化。
- `ComputedStyle.Diff(old, new)` 返回 `StyleDifference` 标志（Structure / Layout / Paint / Composite / Behavior / CustomProperties），跳过引用相等的组，未变化的样式比较很便宜。
- **计算值规则**：em/rem/ex/ch 与视口单位在计算样式时转为 px（符合规范）；百分比及含百分比的 calc 保留为 `LengthPercentage`（px + %，或表达式树），布局时解析。含百分比的值无法在计算期截断为非负，布局映射一侧必须截断（见 [08](08-css.md)）。边框样式为 none 时边框宽度计算为 0，否则初始为 medium（3px）——所以初始边框宽度是 0，几乎所有元素可以共享初始的 Border 组。overflow 的 visible/clip 在另一轴可滚动时按规范调整；根元素、绝对定位元素与 flex/grid 子元素的 display 块级化。无单位的 line-height 保持为数值，百分比转为长度。`ComputedStyle.Flags` 标记 `DependsOnViewport / DependsOnEnvironment`（只用 em 的渐变也会被保守地标为依赖视口）。
- `background` 简写在第 1 期只产生 `background-color` 与 `background-image`；其中的 repeat、attachment、box、position、size 被识别并忽略，而不是让整条声明无效。

### 引擎限制

防止恶意 CSS 造成指数展开或栈溢出：

| 项 | 上限 |
|---|---|
| 单个值替换后的 token 数 | 65 536 |
| 单个元素所有替换的 token 总数 | 1 048 576 |
| `var()` 解析深度 | 512（同时计入解析与 token 嵌套，约合 256 层 `var()` 链），更深的在计算值时无效 |
| `calc()` 嵌套 / 节点数 | 32 层 / 1024 个 |
| 属性名建议 | 名字超过 64 字符时不做编辑距离建议 |

层名长度的开销是线性的（评审中修复了点分层名的二次方内存问题）。

## 清单

期：**1** = M1，**2** = M2，**3** = M3，**4** = 之后。

### 盒模型与尺寸

| 属性 | 类型 | 继承 | 动画 | 影响 | 期 |
|---|---|---|---|---|---|
| `display` | 枚举 | 否 | 离散 | S | 1（flex/block/none），2（grid/contents/inline-flex） |
| `box-sizing` | 枚举 | 否 | 否 | L | 1 |
| `width` `height` `min-*` `max-*` | 长度/百分比/auto/固有尺寸关键字 | 否 | 是 | L | 1 |
| `aspect-ratio` | `auto \|\| <ratio>`（`AspectRatioValue`） | 否 | 是 | L | 1（第七批） |
| `margin-*` | 长度/百分比/auto | 否 | 是 | L | 1 |
| `padding-*` | 长度/百分比 | 否 | 是 | L | 1 |
| `border-*-width` | 长度 | 否 | 是 | L | 1 |
| `overflow-x` `overflow-y` | 枚举 | 否 | 否 | S | 1（hidden），2（scroll/auto） |
| `visibility` | 枚举 | 是 | 离散 | P | 1 |

逻辑属性（`margin-inline-start`、`padding-block`、`inset-inline` 等）在级联时按 `direction` 映射到物理属性，第 3 期。

### 定位与层叠

| 属性 | 类型 | 继承 | 动画 | 影响 | 期 |
|---|---|---|---|---|---|
| `position` | 枚举 | 否 | 否 | S | 1（relative/absolute），2（fixed/sticky） |
| `top` `right` `bottom` `left` | 长度/百分比/auto | 否 | 是 | L | 1 |
| `z-index` | 整数/auto | 否 | 是 | S | 1 |
| `isolation` | 枚举 | 否 | 否 | S | 2 |

### Flexbox

| 属性 | 类型 | 继承 | 动画 | 影响 | 期 |
|---|---|---|---|---|---|
| `flex-direction` `flex-wrap` | 枚举 | 否 | 否 | L | 1 |
| `flex-grow` `flex-shrink` | 数值 | 否 | 是 | L | 1 |
| `flex-basis` | 长度/百分比/auto/content | 否 | 是 | L | 1 |
| `justify-content` `align-items` `align-self` `align-content` | 枚举 | 否 | 否 | L | 1 |
| `gap`（`row-gap` `column-gap`） | 长度/百分比 | 否 | 是 | L | 1 |
| `order` | 整数 | 否 | 是 | L | 2 |

### Grid

| 属性 | 类型 | 继承 | 动画 | 影响 | 期 |
|---|---|---|---|---|---|
| `grid-template-columns` `grid-template-rows` | 轨道列表 | 否 | 是（同构时） | L | 2 |
| `grid-template-areas` | 区域表 | 否 | 否 | L | 2 |
| `grid-auto-columns` `grid-auto-rows` `grid-auto-flow` | 轨道/枚举 | 否 | 否 | L | 2 |
| `grid-row-start/end` `grid-column-start/end` | 网格线 | 否 | 否 | L | 2 |
| `justify-items` `justify-self` `place-*` | 枚举 | 否 | 否 | L | 2 |

### 背景与边框

| 属性 | 类型 | 继承 | 动画 | 影响 | 期 |
|---|---|---|---|---|---|
| `background-color` | 颜色 | 否 | 是 | P | 1 |
| `background-image` | 图层列表（url / 渐变） | 否 | 部分 | P | 1（渐变），2（图片） |
| `background-size` `-position` `-repeat` `-clip` `-origin` | 逐层列表 | 否 | 是 | P | 2 |
| `border-*-color` | 颜色 | 否 | 是 | P | 1 |
| `border-*-style` | 枚举 | 否 | 否 | P | 1（solid），2（dashed/dotted/double）。M1 绘制已支持四边统一的 dashed / dotted，double / groove / ridge / inset / outset 画成实线 |
| `border-*-radius` | 长度/百分比对 | 否 | 是 | P | 1 |
| `outline-color/style/width/offset` | 同边框 | 否 | 是 | P | 1（M1 提前；`outline-offset` 目前接受百分比，不合规范；`outline: auto` 无效） |
| `box-shadow` | 阴影列表 | 否 | 是 | P | 1 |
| `border-image-*` | 图像切片 | 否 | 否 | P | 4 |

### 效果与变换

| 属性 | 类型 | 继承 | 动画 | 影响 | 期 |
|---|---|---|---|---|---|
| `opacity` | 数值 | 否 | 是 | C | 1 |
| `transform` | 变换函数列表 | 否 | 是 | C | 2（2D），4（3D） |
| `transform-origin` | 位置 | 否 | 是 | C | 2 |
| `translate` `rotate` `scale` | 独立变换 | 否 | 是 | C | 2 |
| `filter` | 滤镜列表 | 否 | 是 | P（层内容） | 2 |
| `backdrop-filter` | 滤镜列表 | 否 | 是 | P | 3 |
| `mix-blend-mode` | 枚举 | 否 | 否 | P | 3 |
| `clip-path` | 基本形状 / path() | 否 | 是 | P | 3 |
| `mask-*` | 图层 | 否 | 部分 | P | 4 |
| `will-change` | 提示 | 否 | 否 | S（层提升） | 2 |

### 文本与字体

文本层（[19](19-text-api.md) 的 `TextStyle` / `ParagraphStyle`）已经能承载下表中第 1、2 期的大部分属性：`white-space` 全部六个值、`word-break` normal/break-all/keep-all、`overflow-wrap` normal/break-word/anywhere（anywhere 使 min-content 降为最宽字素）、`text-overflow: ellipsis`、`line-clamp`（MaxLines）、`line-height` normal/数值/px（每个行内 run 半行距 + strut）、`letter-spacing`（每个字素簇之后）、`word-spacing`（词分隔符上）、`text-decoration` 线/样式/颜色/粗细、`text-transform`（简单 Unicode 大小写映射）。请求字重 ≥ 600 而字型不足 600 时合成粗体；请求 italic/oblique 而字型为正体时合成斜体。CSS 一侧仍按下表的期解析，第 2 期的属性目前报 AW2002。

| 属性 | 类型 | 继承 | 动画 | 影响 | 期 |
|---|---|---|---|---|---|
| `color` | 颜色 | 是 | 是 | P | 1 |
| `font-family` | 字体族列表 | 是 | 否 | L | 1 |
| `font-size` | 长度 | 是 | 是 | L | 1 |
| `font-weight` `font-style` `font-stretch` | 数值/枚举 | 是 | 是 | L | 1 |
| `line-height` | 数值/长度/normal | 是 | 是 | L | 1 |
| `text-align` | 枚举 | 是 | 否 | L | 1 |
| `white-space` `word-break` `overflow-wrap` | 枚举 | 是 | 否 | L | 1 |
| `text-overflow` | 枚举 | 否 | 否 | P | 1 |
| `-webkit-line-clamp` / `line-clamp` | 整数 | 否 | 否 | L | 2 |
| `letter-spacing` `word-spacing` | 长度 | 是 | 是 | L | 2 |
| `text-decoration-line/color/style/thickness` | 枚举/颜色/长度 | 否（按规范传播） | 部分 | P | 2 |
| `text-transform` | 枚举 | 是 | 否 | L | 2 |
| `text-shadow` | 阴影列表 | 是 | 是 | P | 2 |
| `font-feature-settings` `font-variation-settings` `font-kerning` | 列表 | 是 | 部分 | L | 4 |
| `direction` `unicode-bidi` | 枚举 | 是 / 否 | 否 | L | 4 |
| `writing-mode` `text-orientation` | 枚举 | 是 | 否 | S | 4 |
| `tab-size` `text-indent` `hyphens` | 数值/长度/枚举 | 是 | 部分 | L | 4 |

### 滚动与交互

| 属性 | 类型 | 继承 | 动画 | 影响 | 期 |
|---|---|---|---|---|---|
| `cursor` | 枚举/图像 | 是 | 否 | — | 1 |
| `pointer-events` | 枚举 | 是 | 否 | — | 1 |
| `user-select` | 枚举 | 否 | 否 | — | 2 |
| `touch-action` | 标志集 | 否 | 否 | — | 3 |
| `overscroll-behavior-x/y` | 枚举 | 否 | 否 | — | 3 |
| `scroll-behavior` | 枚举 | 否 | 否 | — | 2 |
| `scroll-snap-type` `scroll-snap-align` `scroll-snap-stop` | 枚举 | 否 | 否 | — | 3 |
| `scroll-padding-*` `scroll-margin-*` | 长度 | 否 | 否 | — | 3 |
| `scrollbar-width` `scrollbar-color` `scrollbar-gutter` | 枚举/颜色 | 部分 | 否 | L / P | 3 |
| `caret-color` `accent-color` | 颜色 | 是 | 是 | P | 2 |
| `object-fit` `object-position` | 枚举（`CssObjectFit`）/ 位置（`PositionValue`） | 否 | 离散 / 是 | P | 1（第七批） |
| `image-rendering` | 枚举（`CssImageRendering`：auto、smooth、high-quality、pixelated、crisp-edges） | 是 | 离散 | P | 1（第七批） |
| `content`（`::before` / `::after`） | 字符串/图像 | 否 | 否 | S | 3 |

替换元素相关属性的计算值（第七批，`src/AwAUI.Css/Values/ReplacedValues.cs`，解析在 `ValueParsers.cs`，计算在 `StyleComputer.cs`）：

- `PositionValue`：计算后的 `<position>`，接受 1、2、4 值形式（3 值形式按 CSS Values 4 拒绝），关键字可交换，百分比与 calc 保留到使用时（`ResolveX / ResolveY`）；序列化如 `50% 50%`。
- `AspectRatioValue`：`auto || <ratio>`。`auto 16/9` 表示「有自然比例就用它，否则 16/9」；含 0 的比例按 auto（CSS Sizing 4）；单个数值序列化为 `N / 1`；`Resolve(naturalRatio)` 给出实际使用的比例。
- `image-rendering` 的旧值 `optimizeSpeed` / `optimizeQuality` 与 `-webkit-optimize-contrast` 按无效值拒绝（浏览器接受为别名）。
- 这些值是 `Image` / `Svg` 的唯一来源，节点上不再有对应属性（决策 D35，见 [05](05-ui-core.md)）。

### 过渡与动画

`transition-property/duration/timing-function/delay/behavior`、`animation-name/duration/timing-function/delay/iteration-count/direction/fill-mode/play-state/composition`：不继承、自身不可动画、变化只影响动画调度。第 2 期。

### 简写

级联前展开为上面的长属性：`margin` `padding` `inset` `border` `border-top…` `border-width/style/color` `border-radius` `outline` `background` `flex` `flex-flow` `gap` `grid` `grid-template` `grid-area` `grid-row` `grid-column` `place-content/items/self` `overflow` `font` `text-decoration` `transition` `animation` `overscroll-behavior` `scroll-snap…` `all`。简写在 DevTools 中保持原样显示并可展开。

### 扩展属性（`-aw-` 前缀）

| 属性 | 用途 |
|---|---|
| `-aw-hit-slop` | 不改变视觉尺寸地扩大命中区域（触摸目标） |
| `-aw-scroll-physics` | `auto / ios / android`，覆盖平台默认的滚动物理 |
| `-aw-font-smoothing` | 字形抗锯齿方式 |
| `-aw-layer` | 显式要求或禁止提升为合成层 |

扩展属性保持最少；能用标准属性表达的不新增。

## 动画插值

| 类别 | 规则 |
|---|---|
| 长度、百分比、数值、颜色 | 逐分量线性插值；长度与百分比混合时生成 `calc()` |
| 颜色 | 预乘 sRGB 空间插值 |
| `transform` | 函数列表同构时逐函数插值，否则分解矩阵后插值 |
| 阴影、滤镜、渐变列表 | 长度对齐后逐项插值 |
| 枚举、关键字 | 离散：在进度 50% 处切换 |
| `display`、`visibility` | 按规范的离散/特殊规则，使进出场过渡可用 |

只触及 **C** 类属性的过渡与动画整体交给合成器执行。

## 未列入的属性

出现未在清单中的标准属性时：构建期给出 `AW2002`（附「不支持」或「计划在第 N 期支持」及可用的替代写法），运行时解析则记入诊断通道并在 DevTools 样式面板中以警告标记显示。
