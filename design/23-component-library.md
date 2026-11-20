# 23 组件库总体设计：基础控件、Fluent 主题、shadcn 与 Tailwind

本文确定组件库的分层、各层的 API 风格、样式机制、分发与扩展方式，以及落地需要引擎先补齐的能力。[22](22-controls.md) 定义所有控件共同遵守的约定与公共基础设施，本文在其上定义具体的库。决策见 [README](README.md) 的 D38–D42。

## 总体分层

```
应用 / 第三方扩展组件库（registry 条目或 NuGet 包）
        │
        ├── AwAUI.Shadcn ────────── shadcn/ui 风格的成品组件（Tailwind 工具类 + 设计令牌，组合式 API）
        │        │
        │        ├── AwAUI.Tailwind ── Tailwind 兼容工具类：候选解析、工具与变体注册表、CSS 生成、Cn / Cva
        │        │
        ├── AwAUI.Controls ──────── Avalonia 风格的基础控件（.NET 命名、属性式 API）
        │        │  └── AwAUI.Themes.Fluent ── 基础控件的 Fluent 外观（纯 CSS 样式表）
        │        │
        └────────┴── AwAUI.Controls.Primitives ── 无样式的行为原语（浮层、定位、焦点、选择、虚拟化 ...）
                              │
                         AwAUI.UI（内建元素、组件模型、事件、样式、滚动）
```

| 程序集 / 命名空间 | 角色 | 依赖 |
|---|---|---|
| `AwAUI.Controls.Primitives`（在 `AwAUI.Controls` 程序集内） | 行为原语，相当于 Radix Primitives + Floating UI + 若干 Avalonia 内部机制。无视觉，只输出结构、状态属性（`data-state` 等）、无障碍与键盘 | `AwAUI.UI` |
| `AwAUI.Controls` | Avalonia 控件集的对应物，`ContentControl` / `ItemsControl` 模型、.NET 命名 | `AwAUI.UI` |
| `AwAUI.Themes.Fluent` | `AwAUI.Controls` 的默认外观：Fluent 2 设计令牌（明 / 暗 / 高对比、强调色、紧凑密度）与每个控件部件的样式。只有 CSS 与令牌，无代码逻辑 | `AwAUI.Controls` |
| `AwAUI.Tailwind` | 工具类引擎，构建期与运行时共用（netstandard2.0，源生成器以源码链接方式编入） | 无（只输出 CSS 文本，由 AwAUI.Css 解析） |
| `AwAUI.Shadcn` | shadcn/ui 组件的移植，组合式 API；默认样式即 shadcn 的 Tailwind 类 | `AwAUI.Controls`、`AwAUI.Tailwind` |
| `AwAUI.Icons.Lucide` | shadcn 使用的 Lucide 图标（ISC 许可），由 SVG 生成静态 `SvgDocument` | `AwAUI.UI` |
| `src/tools/AwAUI.Cli` 的 `add / init / diff` | shadcn 式源码复制与注册表 | — |

原则：

1. **行为只写一次**。`ComboBox`（Controls）和 `Select`（Shadcn）都建立在 `Primitives.Select*` 之上；键盘、类型查找、定位、无障碍的修复同时惠及两层。
2. **外观只来自 CSS**。Controls 的视觉全部由主题样式表给出（Avalonia 的 `ControlTheme` 在这里就是样式表）；Shadcn 的视觉全部是组件模板里的 Tailwind 类。没有在 C# 里写死的颜色和尺寸。
3. **两层各用自己的习惯**（D40）：Controls 面向 .NET / Avalonia 开发者，Shadcn 面向 web / shadcn 开发者与 AI 生成；两者可以在同一应用里混用。
4. **系统级**：Shadcn 是官方推荐的应用组件库，与平台深度集成（见「系统集成」），新项目模板默认使用它；Controls + Fluent 适合偏桌面、偏传统 .NET 的应用，也是 DevTools 等框架自带界面的基础。

## 第 1 层：行为原语 `AwAUI.Controls.Primitives`

在 [22](22-controls.md)「基础设施」表的基础上，确定为以下公开组件与服务（名字参照 Radix，便于对照）：

| 原语 | 内容 | 对应 |
|---|---|---|
| `Portal` / `OverlayLayer` | 把子树挂到窗口的覆盖层栈；层级与 `z-index` 令牌；可选独立原生弹出窗口（桌面菜单超出主窗口） | Radix Portal、Avalonia `OverlayLayer` / `PopupRoot` |
| `Popper`（`PopperAnchor / PopperContent / PopperArrow`） | 锚定定位：`side / align / sideOffset / alignOffset / avoidCollisions / collisionPadding / sticky`，翻转、平移、尺寸约束（输出 `--aw-popper-available-width/height`、`--aw-popper-anchor-width` 变量），随锚点滚动与尺寸变化更新 | Floating UI、Radix Popper |
| `DismissableLayer` | 点击外部、Esc、焦点移出、滚动时关闭；嵌套只关最上层；`OnEscapeKeyDown / OnPointerDownOutside / OnInteractOutside` 可 `PreventDefault` | Radix |
| `FocusScope` | 焦点陷阱、打开时自动聚焦、关闭后恢复焦点 | Radix FocusScope |
| `RovingFocusGroup` | 一组项只占一个 Tab 位，方向键移动，`loop / orientation / dir` | Radix |
| `Presence` | 等退出动画 / 过渡结束才卸载（依赖 CSS 动画引擎），`ForceMount` | Radix Presence |
| `ScrollLock` / `ScrollArea` 原语 | 模态打开时锁住背景滚动；自绘滚动条的 viewport / scrollbar / thumb 部件（建立在第八批滚动系统上） | Radix ScrollArea、Avalonia `ScrollViewer` |
| `Collection` + `TypeAhead` | 有序收集子项（跳过禁用项）、键入字符跳转 | Radix Collection |
| `SelectionModel<T>` | 单选、多选、范围（Shift）、切换（Ctrl/Cmd），按索引或按键；列表类共用 | Avalonia `SelectionModel` |
| `Virtualizer` | 定高 / 变高（测量后缓存）、分组、粘性组头、滚动锚定；水平与网格两维 | Avalonia `ItemsRepeater` / `VirtualizingStackPanel`、TanStack Virtual |
| 复合行为组 | `Primitives.Dialog / AlertDialog / Popover / Tooltip / HoverCard / Menu / ContextMenu / Menubar / NavigationMenu / Select / Tabs / Accordion / Collapsible / Checkbox / RadioGroup / Switch / Slider / Toggle / ToggleGroup / Toolbar / Progress / Avatar / Label / Separator` | Radix 的同名包 |
| `FormContext` | 字段注册、校验（同步 / 异步）、提交、首个错误聚焦 | react-hook-form 的最小子集 |

约定：

- 原语只输出状态属性，不带 class：`data-state="open|closed|checked|unchecked|indeterminate|active|inactive"`、`data-side / data-align`（浮层实际方位）、`data-orientation`、`data-disabled`、`data-highlighted`、`data-placeholder`，加上 `data-slot="..."`（标记部件，Tailwind 的 `has-data-[slot=...]` 依赖它）。
- 受控与非受控：`Open + OnOpenChange` / `DefaultOpen`，`Value + OnValueChange` / `DefaultValue`，与 [22](22-controls.md) 的约定一致。
- 所有原语的状态机可脱离渲染测试（输入事件序列，断言状态与属性）。

> 实现现状（第九批 9a）：`src/AwAUI.Controls/Primitives` 已有各原语的纯逻辑部分：`PopperEngine` / `PopperLayout`（Floating UI 1.7.5 语义，中间件 offset / flip / shift + limitShift / size / arrow / hide / autoPlacement / transformOrigin；引擎与 Floating UI 本体做 1500 例差分测试，`PopperLayout` 与 Radix Popper 的中间件组合做 800 例差分测试）、`SelectionModel<T>` + `SelectionGestures`（Avalonia 语义，与参考模型做随机测试）、`ControllableState<T>`、`ItemCollection<TKey>` + `TypeAhead`、`RovingFocus` / `RovingFocusGroup<TKey>`、`SubmenuGraceArea`、`Virtualizer`（定高 O(1)、变高用树状数组、滚动锚定、粘性项、lanes）、`DismissableLayerStack<TNode>`、`FocusScopeModel<T>` / `FocusScopeStack<T>`、`Presence`。元素以泛型参数表示、几何与时间由调用方传入（D50）。Portal / 覆盖层栈、真实的焦点移动、元素几何、CSS 动画事件、滚动时关闭等 UI 接入依赖 AwAUI.UI 补充的公开接口，列在 [07](07-roadmap.md)「组件库路线」。

> 实现现状（第九批 9b）：`Portal`、覆盖层与焦点 API 已接入帧管线；`Popper`、`DismissableLayer`、`FocusScope`、`RovingFocusGroup`、`Slot`、`ScrollLock` 和 `VisuallyHidden` 已有组件实现。首批复合原语是 `Popover` 与 `Dialog`。它们的浮层使用窗口坐标，锚点滚动或变换后在同一帧重新放置。`PresenceGate` 跟踪关闭时在内容根元素上启动的有限 CSS 动画和过渡；重新打开会取消待卸载，无时钟时立即卸载。`Virtualizer`、`SelectionModel` 与 `TypeAhead` 的纯逻辑可以使用，其成品控件仍按后续批次接入。

## 第 2 层：Avalonia 风格基础控件 `AwAUI.Controls`

### 模型

Avalonia 的核心概念在 AwAUI 组件模型（[12](12-component-api.md)）里的对应：

| Avalonia | AwAUI.Controls |
|---|---|
| `TemplatedControl` + `ControlTemplate` | 抽象类 `TemplatedControl : Component`：默认结构由受保护的虚方法 `BuildTemplate()` 给出；`Template` 属性（`ControlTemplate<TSelf>`，本质是 `Func<TSelf, Node>`）可整体替换结构，部件以 `part` 属性命名，控件按部件名查找（相当于 `PART_` 约定） |
| `ContentControl.Content` + `ContentTemplate` | `Content`（`object?`：`Node` 直接渲染，`string` 成为文本，其它对象经 `ContentTemplate` 或上下文中的 `DataTemplates` 转换） |
| `ItemsControl.ItemsSource / ItemTemplate / ItemsPanel` | 同名属性；`ItemsSource` 接受 `IEnumerable`，识别 `INotifyCollectionChanged` 做增量更新；`ItemTemplate` 是 `DataTemplate<T>`；默认虚拟化 |
| `SelectingItemsControl` | `SelectedItem / SelectedIndex / SelectedItems / SelectionMode / Selection`（`SelectionModel<T>`）+ `SelectionChanged` |
| `DataTemplates` / `IDataTemplate` | `DataTemplate<T>`（`Func<T, Node>`）与按类型匹配的 `DataTemplates` 上下文 |
| 样式类、伪类 `:pointerover :pressed :checked :selected` | CSS 类与伪类：标准伪类优先（`:hover :active :checked :focus-visible :disabled`），其余用状态属性（`[data-selected]`、`[data-state]`）；DevTools 可强制 |
| `Classes` / `ControlTheme` / `Theme` 属性 | `Class` / 主题样式表 / `Theme` 属性换用另一组类名前缀 |
| `IsEnabled / IsVisible / IsChecked / IsOpen ...` | 同名布尔属性，配 `...Changed` 事件；可双向绑定（`.awaui` 的 `@bind-IsChecked`） |
| `Command` / `CommandParameter` | 保留：接受 `System.Windows.Input.ICommand`（netstandard2.0 可用），与 MVVM 库兼容 |
| 布局面板 | `StackPanel / DockPanel / WrapPanel / Grid / UniformGrid / Canvas / RelativePanel / Viewbox` 映射到 CSS flex / grid / 绝对定位，属性名沿用（`Orientation`、`Spacing`、`Grid.Row`（附加属性写成 `GridRow`）、`DockPanel.Dock`） |

`Component` 已有 Props 胶水、State 与协调器；控件不引入依赖属性系统（AwAUI 用不可变 Node + 重建，没有 `AvaloniaProperty`）。数据绑定走 `.awaui` 的 `@bind` 与 MVVM 可观察对象（`INotifyPropertyChanged` 适配器作为可选包 `AwAUI.Mvvm`，后续）。

### 控件清单（对照 Avalonia 11）

| 类别 | 控件 | 期 |
|---|---|---|
| 内容与文本 | `ContentControl`、`HeaderedContentControl`、`Label`、`TextBlock`、`SelectableTextBlock`、`Border`、`Decorator`、`Viewbox`、`TransitioningContentControl` | A |
| 布局面板 | `StackPanel`、`DockPanel`、`WrapPanel`、`Grid`、`UniformGrid`、`Canvas`、`RelativePanel`、`GridSplitter` | A |
| 按钮 | `Button`、`RepeatButton`、`ToggleButton`、`HyperlinkButton`、`DropDownButton`、`SplitButton`、`ToggleSplitButton` | A |
| 选择 | `CheckBox`（三态）、`RadioButton`（`GroupName`）、`ToggleSwitch`、`Slider`、`ComboBox`、`ListBox`、`TabControl` / `TabStrip`、`TreeView` | A |
| 文本输入 | `TextBox`（单行 / 多行 `AcceptsReturn`、`Watermark`、`PasswordChar`、`MaxLength`、`InnerLeftContent / InnerRightContent`）、`NumericUpDown`、`AutoCompleteBox`、`MaskedTextBox` | A / B |
| 滚动与容器 | `ScrollViewer`、`Expander`、`SplitView`、`Carousel`、`ItemsControl`、`ItemsRepeater` | A |
| 浮层 | `Popup`、`Flyout`、`MenuFlyout`、`ToolTip`（附加：`ToolTip.Tip` 写成 `ToolTip` 属性）、`ContextMenu`、`Menu` / `MenuItem`、`Window` 内对话框 `DialogHost` | A |
| 进度与反馈 | `ProgressBar`、`ProgressRing`、`WindowNotificationManager` / `NotificationCard`、`InfoBar` | B |
| 日期时间 | `Calendar`、`CalendarDatePicker`、`DatePicker`、`TimePicker` | B |
| 数据 | `DataGrid`（独立包 `AwAUI.Controls.DataGrid`，与 Avalonia 相同的拆分）、`TreeDataGrid` 不做 | B |
| 其它 | `ColorPicker` / `ColorView`、`RefreshContainer`、`NativeMenu`（经平台服务）、`TitleBar`、`ThemeVariantScope` | C |

[22](22-controls.md) 原清单中的 `TextField / Select / Tabs / Dialog ...` 等名字从此只用于 Shadcn 层；Controls 层一律用上表的 Avalonia 名字。

### Fluent 主题 `AwAUI.Themes.Fluent`

- 参照 Fluent 2 与 Avalonia `FluentTheme`（MIT）的度量：控件高度 32（紧凑 24）、圆角 4 / 8、焦点框双层描边、强调色派生色阶（Light1–3、Dark1–3）。颜色与尺寸全部是 CSS 变量（`--fluent-*`），并映射到 [22](22-controls.md) 的通用令牌 `--aw-*`。
- `ThemeVariant`：`Light / Dark / HighContrast`，默认跟随系统（见「系统集成」）；子树局部切换用 `ThemeVariantScope`（设置 `data-theme` 属性，令牌按属性选择器切换）。
- 交付：样式表在构建期预编译为 `StyleSheet`（CSS 预编译生成器已规划，见 [15](15-repo-structure.md)），应用通过 `app.UseFluentTheme(options)` 加入。不需要 Tailwind。

9c 的首批落地：`AwAUI.Themes.Fluent` 内嵌 CSS，首次使用时解析，之后共享 `StyleSheet`。用 `host.UseFluentTheme()` 启用，再用 `SetVariant` / `SetCompact` 切换单个窗口。`System` 读取 CSS 媒体环境；原生窗口暂未提供系统明暗外观变更通知。macOS 的「减少动态效果」已通过 `UIHost` 同步到 CSS 媒体环境。`samples/ControlCatalog` 提供首批标准控件和三套外观的示例，其中 `ItemsRepeater` 使用定高行与预渲染范围，支持在滚动发生的同一帧更新挂载内容。普通 `ItemsControl`、`ListBox` 和 `TreeView` 暂不虚拟化。完整的 Controls A 清单见上表。

Controls 的 `Slider` 和 Shadcn 的 `Slider` 复用 `Primitives.SliderBehavior`，目前支持水平单值。`TabStrip` 与 `TabControl` 复用标签导航；独立 `TabStrip` 的面板和 ARIA 关联由调用方提供。`SplitView` 可内联或覆盖显示侧栏，也可在折叠时保留窄栏。覆盖模式在控件区域内定位，无默认进出场动画。

`MenuFlyout`、`Popup` 与 `ToolTip` 分别是扁平操作菜单、应用控制的显式锚点浮层和非交互说明提示。它们复用 `Popper` / `Portal` / `DismissableLayer` 等原语，但由 `MenuFlyout` 自行管理其菜单焦点与定向键盘导航。ToolTip 的目标必须是一个受支持的直接子元素，避免为了附加属性额外改变目标尺寸和 Tab 顺序。`SelectableTextBlock` 复用只读多行输入和原有选区绘制；`TransitioningContentControl` 按内容 key 立即替换旧内容，并只动画新内容的进入。

## 第 3 层：Tailwind 兼容工具类 `AwAUI.Tailwind`

### 范围

以 **Tailwind CSS v4** 的语义为准（shadcn/ui 当前版本基于 v4）：

| 方面 | 支持 |
|---|---|
| 主题 | v4 的 `@theme` 变量命名空间（`--color-*`、`--spacing`、`--radius-*`、`--text-*`、`--font-*`、`--shadow-*`、`--breakpoint-*`、`--animate-*`、`--ease-*`）；主题可由 CSS 文件（`@theme { ... }`、`@theme inline`）或 C# 配置给出 |
| 工具 | 布局、flex、grid、间距（`p-* m-* gap-* space-*`）、尺寸（`w h size min-* max-*`）、排版、颜色（含 `/<opacity>` 修饰）、边框与圆角、`ring` 与 `outline`、阴影、透明度、变换（独立的 `translate / scale / rotate`）、过渡与动画、交互（`cursor pointer-events select`）、`sr-only`、`truncate`、`line-clamp`、滚动（`overflow-*`、`scroll-*`、`overscroll-*`） |
| 变体 | 伪类（`hover focus focus-visible focus-within active disabled checked first last odd even placeholder`）、`aria-*`、`data-*`（含 `data-[state=open]`）、`group-*` / `peer-*`（含具名 `group/name`）、`has-*`、`not-*`、`*:`、`dark:`、断点 `sm md lg xl 2xl`（按窗口宽度）、`@container` 容器查询变体、`motion-safe / motion-reduce`、`rtl / ltr`、`pointer-coarse / pointer-fine`，以及 AwAUI 自有的平台变体（`macos: windows: linux: android: ios: browser:`，见「系统集成」） |
| 任意值 | `w-[200px]`、`bg-(--my-var)`、`grid-cols-[1fr_auto]`、任意属性 `[mask-type:luminance]`、任意变体 `[&_svg]:size-4`、`!` 重要性 |
| 指令 | `@theme`、`@utility`、`@custom-variant`、`@variant`、`@apply`、`@layer`（base / components / utilities）、`@source`、`--value()` 函数工具 |
| 不支持 | 插件 JS API、`@config` 的 JS 配置文件、浏览器专有前缀相关工具；遇到不支持的类名给出诊断，而不是静默忽略 |

### 机制

1. **候选提取**：与 Tailwind 一样按文本扫描源文件中的候选类名，不解析语义：`.awaui` 的 `class` 属性与其中的字符串字面量、`.cs` 文件里的字符串字面量（`Class = "..."`、`Cn(...)`、`Cva(...)`）。范围由 `@source` 与项目项 `<AwAUITailwindSource Include="..." />` 控制。
2. **生成**：候选 → 解析（变体链 + 工具名 + 值 + 修饰）→ 工具注册表展开为 CSS 声明 → 按 v4 的顺序规则排序（层、变体顺序、属性数量）→ 输出样式表。生成发生在**构建期**。9b 中，`AwAUI.Markup.Generator` 调用 `AwAUI.Tailwind`，把 CSS 文本和候选列表写入生成代码；模块初始化器登记样式表，`AppStyleSheets` 在首次使用时把它解析为 `StyleSheet` 并缓存。预编译样式树是后续工作。
3. **运行时补全（计划，可选）**：`TailwindRuntime` 在遇到构建期未生成的类名时即时生成规则并追加到工具层，用于热重载、DevTools 中编辑 class、来自数据的动态类名。默认在 Debug 开启、Release 关闭；关闭时未知类名经诊断报告一次。
4. **诊断**：入口样式表的编译错误报告 AW5001；扫描范围内的未知工具类字面量报告 AW5002。DevTools 显示每个类名对应规则的功能留待后续。
5. **基准与一致性**（第九批 9a 实现）：以 Tailwind CSS **v4.3.5** 为基准逐函数移植，输出与官方逐字节一致——shadcn new-york-v4 S1 语料 492 个类名、按工具族的 804 个、全部 23506 个类名加变体的全量语料，以及整份样式表（主题、preflight、`@layer`、`@property`、`@keyframes`）；另有 38768 个随机组合候选的模糊比对。参考输出由固定版本的 npm 包生成，入库在 `tests/TestFiles/Tailwind`（含生成脚本与 `VERSION.txt`），测试时不依赖 Node。`Cn` 对齐 tailwind-merge **v3.7.0**（1277 个官方用例），`Cva` 对齐 class-variance-authority 0.7.1。preflight 默认用 AwAUI 版（可选原版）；主题根选择器可配置（`TailwindOptions.ThemeRootSelector`，AwAUI.Css 支持 `:host` 之前设为 `:root`）。不可信输入有上限：候选长度、变体深度、选择器扇出、`@apply` / `@custom-variant` / `@import` 的扇出与循环、`theme()` 互相引用。
6. **运行时补全的流程**：用同一份入口 CSS 创建 `TailwindCompiler`，先 `Build([])` 得到主题、preflight 等不依赖候选的样式；再 `AddCandidates(构建期候选)` 得到逐条的基线规则（每条带排序下标 `Index`）；之后遇到未知类名调用 `AddCandidates`，按 `Index` 把新规则插入逐条规则列表，`ThemeCss` 进 theme 层，`GlobalCss`（`@property`、`@keyframes`）追加。`Build` 的输出会合并相邻的同条件 at 规则，下标与它不对应，所以运行时的 utilities 层用逐条规则而不是构建期合并后的样式表。
7. **辅助 API**：
   - `Cn(params ClassValue[])`：条件拼接 + **tailwind-merge** 语义的冲突消解（后者覆盖前者：`Cn("px-2 py-1", "p-3")` → `"p-3"`），供组件把调用方的 `Class` 合并到默认类上。
   - `Cva`：class-variance-authority 的移植，按变体表生成类名（`base`、`variants`、`compoundVariants`、`defaultVariants`），源生成器可为它生成强类型的变体枚举。

### 引擎前置条件

工具类最终要由 CSS 引擎解析并在布局与绘制中生效。下表列出 shadcn v4 默认主题与组件使用的能力。9b 已通过 shadcn 语料的解析差距检查（`CssGapReportTests`），这一检查不测布局、绘制或交互；验收仍以组件示例为准。`samples/TailwindDemo` 演示了构建期生成、明暗令牌、工具类与属性转发。

| 能力 | 用途 |
|---|---|
| 选择器 `:host`（无 Shadow DOM 时永不匹配） | v4 的主题规则是 `:root, :host`，不支持时整条规则被丢弃、全部令牌丢失（临时办法见上文 `ThemeRootSelector`） |
| `oklch()` / `oklab()` 颜色、`color-mix()` | shadcn v4 令牌全部是 oklch；`bg-primary/90` 这类透明度修饰经 `color-mix(in oklab, ...)` 实现 |
| 过渡与 `@keyframes` 动画引擎（第 2 期 → 第 1 期） | `transition-*`、`animate-in / fade-in-0 / zoom-in-95 / slide-in-from-top-2`（tw-animate-css）、`Presence` 的退出动画 |
| `@property` 注册自定义属性（初始值、可动画） | v4 用它给 `--tw-ring-shadow`、`--tw-translate-x` 等提供默认值 |
| 逻辑属性 | v4 的 `px-4` 生成 `padding-inline`，`ms-* me-*`、`start-* end-*` |
| 容器查询 `@container` 与容器单位 | shadcn v4 的 `Card` 头部等 |
| `:has()`、`:not()`、`:is()` 的完整支持；`::before / ::after`、`::placeholder`、`::selection` 渲染 | `has-data-[slot=...]`、`before:` / `after:` 变体、占位文字 |
| 独立变换属性 `translate / scale / rotate` | v4 变换工具 |
| CSS Grid（第八批）、`order`、`aspect-ratio` | 布局类工具 |
| `backdrop-filter`、`filter` | 遮罩层 `backdrop-blur`、`blur-*` |
| 其余属性 | `content`（`before:` / `after:`）、`transform` / `transform-origin`、`fill`、`letter-spacing`、`vertical-align`、`font-feature-settings` / `font-variation-settings`、`text-decoration-line`、`text-underline-offset`、`text-wrap`、`text-indent`、`tab-size`、`list-style`、`border-collapse` / `caption-side`、`clip-path`、`field-sizing`、`scroll-margin-*`、`touch-action`、`user-select`、`-webkit-line-clamp` / `-webkit-box-orient`、`display: table*` 与 `-webkit-box` |
| 其余选择器与条件 | `::file-selector-button`、`@media (forced-colors: active)`；范围更广的语料另需 `@supports`、`@starting-style`、表单伪类（`:invalid :required :read-only :indeterminate` 等）、`::marker`、`::backdrop` |

## 第 4 层：shadcn 组件库 `AwAUI.Shadcn`

### 来源与版式

- 移植 shadcn/ui（MIT）的 **new-york v4** 样式：组件结构、`data-slot` 命名、Tailwind 类原样保留，只把 React 写法换成 `.awaui`。版权与许可声明随包发布（`THIRD-PARTY-NOTICES`）。
- 令牌与 shadcn 完全一致：`--background --foreground --card --popover --primary --secondary --muted --accent --destructive --border --input --ring --chart-1…5 --sidebar-*`、`--radius`；基础色预设 `neutral / zinc / stone / gray / slate`，明暗两套。应用可直接粘贴 shadcn 主题编辑器导出的 CSS 变量。

9c 首批已实现 `AwAUI.Shadcn` 的 Button、Card 与 Dialog。Button 支持变体、尺寸、`AsChild` 和未匹配属性转发；Card 有 Header、Title、Description、Action、Content、Footer 部件；Dialog 包装现有行为原语，默认提供 Portal、模态遮罩和带可访问名称的关闭按钮。库构建时生成组件的工具 CSS，使用方无需扫描库源码。首批提供 neutral 明暗令牌；把 `data-aw-theme="dark"` 设在 `UIRoot.RootElement` 上可同时切换普通内容与覆盖层。`samples/ShadcnGallery` 演示原生窗口、可编辑的 Dialog 与明暗截图。

9c 第二组新增 Input、Label 与 Badge。Input 包装单行编辑器，支持 `Value` / `ValueChanged` 和属性透传；Label 的 `For` 关联同一窗口里的输入框，点击后聚焦，`aria-labelledby` 由调用方设置；Badge 支持变体与 `AsChild`。`Input` 专用于单行，不提供文件选择。

9c 第三组新增 Checkbox、Switch 与 Separator。Checkbox 和 Switch 复用按钮的键盘激活与焦点，并用 `ControllableState` 实现受控和非受控绑定。Checkbox 可以显示混合状态，Switch 支持两种尺寸，Separator 支持横向和纵向，可选择是否暴露语义。

9c 已将 S1 的其余组件接入库，Gallery 展示 `Textarea`、Select、Tabs、DropdownMenu（含子菜单）、浮层、表格等。`Textarea` 包装 UI 的多行编辑器；Form 与 InputOTP 属于 S2。常用图标由 `AwAUI.Icons.Lucide` 提供，按图标延迟解析并缓存。这里记录的是组件入口与示例覆盖；移动端 Select 形态、系统外观联动、完整的交互和视觉验收仍各有待办。

### API：组合式（D40）

与 shadcn / Radix 相同的部件组合，命名转成 C# 的 PascalCase，属性名沿用 shadcn（`Variant`、`Size`、`AsChild`、`Open / OnOpenChange`、`Value / OnValueChange`、`Side`、`Align`）：

```razor
@* 应用代码 *@
<Dialog @bind-Open="editing">
    <DialogTrigger AsChild>
        <Button Variant="ButtonVariant.Outline">编辑资料</Button>
    </DialogTrigger>
    <DialogContent class="sm:max-w-[425px]">
        <DialogHeader>
            <DialogTitle>编辑资料</DialogTitle>
            <DialogDescription>修改后点击保存。</DialogDescription>
        </DialogHeader>
        <div class="grid gap-4">
            <Label For="name">名称</Label>
            <Input Id="name" @bind-Value="name" />
        </div>
        <DialogFooter>
            <Button OnClick="Save">保存</Button>
        </DialogFooter>
    </DialogContent>
</Dialog>
```

```razor
@* AwAUI.Shadcn/Button.awaui（节选）*@
@code {
    static readonly Cva<ButtonVariant, ButtonSize> Variants = new(
        "inline-flex items-center justify-center gap-2 whitespace-nowrap rounded-md text-sm font-medium transition-all disabled:pointer-events-none disabled:opacity-50 [&_svg]:pointer-events-none [&_svg:not([class*='size-'])]:size-4 shrink-0 outline-none focus-visible:border-ring focus-visible:ring-ring/50 focus-visible:ring-[3px]",
        first: new() {        // 变体（实际签名 Cva<T1, T2>(base, first, second, compound)）
            [ButtonVariant.Default]     = "bg-primary text-primary-foreground shadow-xs hover:bg-primary/90",
            [ButtonVariant.Destructive] = "bg-destructive text-white shadow-xs hover:bg-destructive/90",
            [ButtonVariant.Outline]     = "border bg-background shadow-xs hover:bg-accent hover:text-accent-foreground",
            [ButtonVariant.Secondary]   = "bg-secondary text-secondary-foreground shadow-xs hover:bg-secondary/80",
            [ButtonVariant.Ghost]       = "hover:bg-accent hover:text-accent-foreground",
            [ButtonVariant.Link]        = "text-primary underline-offset-4 hover:underline",
        },
        second: new() {       // 尺寸
            [ButtonSize.Default] = "h-9 px-4 py-2 has-[>svg]:px-3",
            [ButtonSize.Sm]      = "h-8 rounded-md gap-1.5 px-3 has-[>svg]:px-2.5",
            [ButtonSize.Lg]      = "h-10 rounded-md px-6 has-[>svg]:px-4",
            [ButtonSize.Icon]    = "size-9",
        });
}
<Slot AsChild="AsChild" Tag="button" data-slot="button" class="@Cn(Variants.Of(Variant, Size), Class)" @attributes="Rest">
    @Children
</Slot>
```

- `AsChild` 由 `Slot` 原语实现：把自身的类、属性、事件合并到唯一子元素上（Radix Slot 的语义），解决「触发器是一个自定义按钮」这类组合。
- `@attributes="Rest"`：未声明的属性（`data-*`、`aria-*`、事件）透传到根部件；需要 `.awaui` 支持属性展开（列入 [17](17-awaui-grammar.md) 待做）。
- 依赖第三方 React 库的 shadcn 组件改为建立在 AwAUI 自己的实现上：

| shadcn 组件 | 原依赖 | AwAUI 中的基础 |
|---|---|---|
| Calendar / Date Picker | react-day-picker | `Controls.Calendar` 的日期逻辑（本地化、周起始、范围选择）+ shadcn 外观 |
| Data Table | TanStack Table | `AwAUI.Shadcn.DataTable`：列定义、排序、过滤、分页、行选择的无头表格模型 + `Virtualizer` |
| Command / Combobox | cmdk | `Primitives.Command`：过滤与打分、分组、键盘导航 |
| Sonner（Toast） | sonner | `Primitives.Toast` 队列 + 实时区域播报 |
| Drawer | vaul | `Primitives.Drawer`：拖拽、分段停靠，建立在手势与滚动物理（第八批）上 |
| Carousel | Embla | 基于滚动吸附与 `ScrollController` |
| Resizable | react-resizable-panels | `Primitives.ResizablePanelGroup`（与 `GridSplitter` 共用拖拽逻辑） |
| Input OTP | input-otp | 基于 `Input` 的分格输入 |
| Chart | Recharts | `AwAUI.Charts`（自绘，后续）；shadcn `Chart` 只提供容器、图例、提示框与 `--chart-*` 令牌 |
| Form | react-hook-form + zod | `FormContext` + DataAnnotations / 委托校验 |

### 组件清单

与 shadcn/ui 官方列表对齐，分三期：

| 期 | 组件 |
|---|---|
| S1 | Button、Badge、Card、Input、Label、Textarea、Checkbox、Switch、RadioGroup、Separator、Skeleton、Spinner、Kbd、Progress、Slider、Avatar、Alert、Tooltip、Popover、Dialog、AlertDialog、Sheet、DropdownMenu、Select、Tabs、Accordion、Collapsible、Toggle、ToggleGroup、ScrollArea、Table、AspectRatio |
| S2 | Command、Combobox、ContextMenu、Menubar、HoverCard、NavigationMenu、Calendar、DatePicker、DataTable、Drawer、Sonner、Sidebar、Breadcrumb、Pagination、Form、Field、InputGroup、ButtonGroup、Item、Empty、InputOTP、Resizable、Carousel |
| S3 | Chart（依赖 `AwAUI.Charts`）、Blocks（仪表盘、登录、侧栏布局等成套页面，以 registry 条目发布）、Typography 文档 |

### 系统集成（「系统级」的含义）

| 方面 | 做法 |
|---|---|
| 明暗模式 | 默认跟随系统外观，经平台服务通知切换（根元素切换 `.dark`，即 shadcn 的约定；Fluent 主题切换 `data-theme`） |
| 强调色 | 可选 `UseSystemAccent`：把平台强调色写入 `--primary` / `--ring`（Windows、macOS、GNOME、Android Material You） |
| 字体 | `--font-sans` 默认解析到平台 UI 字体（Segoe UI Variable、SF Pro / PingFang、Cantarell / Noto、Roboto），CJK 回退链按系统语言 |
| 减少动效、高对比、字体缩放 | 平台设置映射到 `prefers-reduced-motion`、`prefers-contrast`、根字号；`motion-reduce:` 变体与 Presence 遵守 |
| 指针与密度 | `pointer-coarse:` 下触控目标至少 44px；桌面菜单、悬停卡片在触摸下有长按替代 |
| 平台变体 | Tailwind 变体 `macos: windows: linux: android: ios: browser:`；组件内部用于细节（macOS 菜单项圆角高亮、Windows 焦点样式、移动端 Select 改用底部抽屉） |
| 窗口外壳 | `TitleBar` 组件 + 平台的自定义标题栏服务（拖动区、窗口按钮位置随平台） |
| DevTools | 主题编辑器（改令牌实时生效、导出 CSS）、类名到生成规则的反查、组件部件（`data-slot`）高亮 |

## 分发与扩展

### 两种方式并存（D42）

1. **包**：`AwAUI.Shadcn`、`AwAUI.Controls`、`AwAUI.Themes.Fluent` 作为 NuGet 包，开箱即用。包内 Shadcn 组件的 Tailwind 类在包构建时生成并随包发布（应用不需要扫描包的源码）；应用自己的类由应用构建生成；两份样式表按层合并、去重。
2. **源码复制**：`awaui add button dialog` 把组件的 `.awaui` / `.cs` 源码复制到项目（默认 `Components/UI/`，命名空间取项目根命名空间），之后应用完全拥有并修改它们；`awaui diff` 显示本地副本与上游的差异；`awaui init` 生成 `awaui.components.json`（目标目录、命名空间、基础色、主题 CSS 文件、是否用包作为依赖）。同名组件以本地副本为准（命名空间不同，不冲突）。

### 注册表（registry）

- 格式参照 shadcn registry schema，改为 AwAUI 的文件类型：条目 `type` 为 `registry:ui`（组件）、`registry:block`（成套页面）、`registry:lib`（辅助代码）、`registry:theme`（令牌）、`registry:style`；`files` 为 `.awaui / .cs / .css`；`dependencies` 为 NuGet 包；`registryDependencies` 为其它条目（可跨注册表，如 `@acme/data-grid`）；`cssVars` 为令牌增量；`tailwind` 段为 `@theme` / `@utility` 增量。
- 官方注册表随 `AwAUI.Shadcn` 发布（S1、S2 组件与 Blocks）；第三方注册表以 URL 或本地目录登记在 `awaui.components.json`，`awaui add @acme/kanban` 解析命名空间前缀。
- `awaui build` 把一个项目内的组件打成注册表 JSON，便于发布扩展组件库。

### 扩展组件库的写法

基于 shadcn 扩展更丰富组件（如 Kanban、富文本工具栏、文件上传、多选标签输入、时间线、树形表格）时：

- 用 `Primitives` 写行为，用 Shadcn 组件与 Tailwind 类写外观，令牌只引用 shadcn 的变量（自动适配明暗与主题预设）。
- 组件公开 `Class` 并用 `Cn` 合并，公开 `data-slot`，使用方可以用工具类与 `has-data-[slot=...]` 定制。
- 发布为 NuGet 包（编译好的组件 + 预生成样式表）和 / 或注册表条目（源码），两者来自同一份源码。
- 官方的扩展集合命名为 `AwAUI.Shadcn.Extensions`，先做 DataTable 的高级形态（列固定、列宽拖动、分组、虚拟化）、MultiSelect、TagInput、FileUpload、Timeline、TreeView、Stepper。

## 质量门槛

沿用 [22](22-controls.md)「质量门槛」，另加：

1. Shadcn 组件与 shadcn 官网的同名示例做视觉对照：同一尺寸、明暗两套的截图并排入库（AwAUI 侧为黄金图），差异需要逐项说明。
2. Tailwind 生成器有一致性测试：对一组类名，把生成的声明与 Tailwind v4 官方输出（预先生成的快照，不在构建时依赖 Node）逐条比对；不支持项单独列表。
3. Controls 的每个控件在 Fluent 明 / 暗 / 高对比下有黄金图；键盘行为与 Avalonia / WinUI 的同名控件一致（差异记入控件文档）。
4. 组件库全部 AOT 安全；`Cva` 与模板不使用反射。

## 实施顺序

详见 [07](07-roadmap.md)「组件库路线」：

1. **基础（引擎）**：CSS 颜色（oklch、color-mix）、`@property`、过渡与动画引擎、逻辑属性、容器查询、`::before / ::after` 渲染、独立变换；`.awaui` 属性展开；多行 `TextArea` 与撤销。
2. **原语 + Tailwind**：`AwAUI.Controls.Primitives` 的浮层、定位、焦点、Presence、Roving、选择、虚拟化；`AwAUI.Tailwind` 生成器、构建期接入、`Cn` / `Cva`、一致性快照。
3. **第一批控件**：Controls A 期 + Fluent 主题 + `samples/ControlCatalog`；Shadcn S1 + Lucide 图标 + `samples/ShadcnGallery`。
4. **第二批控件**：Controls B 期、DataGrid；Shadcn S2；`awaui add / init / diff / build` 与官方注册表；系统集成（强调色、明暗跟随）。
5. **扩展**：`AwAUI.Shadcn.Extensions`、Blocks、`AwAUI.Charts`；用 Controls 重写 DevTools 面板的界面（自举）。
