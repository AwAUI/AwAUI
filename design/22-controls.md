# 22 标准控件库：通用约定与基础设施

控件全部是用内建元素写成的普通组件（[05](05-ui-core.md)）。组件库分为三层（见 [23](23-component-library.md)，决策 D38–D42）：无样式的行为原语 `AwAUI.Controls.Primitives`；Avalonia 风格的基础控件 `AwAUI.Controls`，外观由 `AwAUI.Themes.Fluent` 的 CSS 定义；shadcn/ui 风格的 `AwAUI.Shadcn`，外观由 Tailwind 兼容工具类定义。本文定义各层共同遵守的约定与公共基础设施；具体的控件清单、命名与 API 风格以 23 为准，本文末尾的「控件清单」是早期草案，保留用于对照分期与部件需求。

## 通用约定

| 方面 | 约定 |
|---|---|
| 结构 | 每个控件公开它的**部件**（part），可用 `::part(name)` 选中；内部其余结构不保证稳定 |
| 状态 | 用标准伪类（`:hover :active :focus-visible :disabled :checked`）；控件特有的状态用属性表达（`[data-state="open"]`、`[data-orientation="vertical"]`），都可在 CSS 中选择 |
| 变体与尺寸 | 用 class：`.primary .danger .ghost`、`.small .large`；主题只定义这些 class 的样式，应用可自行增加 |
| 定制 | 优先级从轻到重：改设计令牌（CSS 变量）→ 给控件加 class → `::part()` 改部件 → 用无样式的基础组件重新组合 |
| 受控与非受控 | 有值的控件同时支持 `Value` + `OnChange`（受控，可 `@bind`）与 `DefaultValue`（非受控，内部自持状态） |
| 无障碍 | 角色、名称、状态、键盘操作内建；遵循 ARIA 编写实践中对应模式的键位 |
| 触摸 | 可点击部件满足最小触摸目标；悬停才出现的内容在触摸下有替代入口 |
| 方向 | 布局使用逻辑属性，RTL 下自动镜像 |
| 表单 | 输入类控件有统一的 `Name / Disabled / ReadOnly / Required / Invalid` 与校验消息 |

### 行为与外观分离

每个有一定复杂度的控件都建立在无样式的行为原语上（`AwAUI.Controls.Primitives`，见 [23](23-component-library.md)）：原语只负责状态、键盘、焦点、无障碍、定位，不带视觉，例如 `Primitives.Select` 的 `SelectRoot / SelectTrigger / SelectContent / SelectItem`。成品控件有两套：`AwAUI.Controls.ComboBox`（Avalonia 命名，Fluent 主题）与 `AwAUI.Shadcn.Select`（shadcn 组合式 API，Tailwind 类），二者共用同一组原语。

需要完全不同外观的应用直接用原语重新组合，不必和默认样式对抗。

### 设计令牌

通用令牌是定义在根元素上的 CSS 变量，明暗两套取值。Fluent 主题以 `--fluent-*` 定义自己的令牌并映射到这些通用令牌；Shadcn 使用 shadcn 原有的令牌名（`--background --primary --radius` 等，见 [23](23-component-library.md)），不经过 `--aw-*`。通用令牌：

| 类别 | 变量（节选） |
|---|---|
| 颜色 | `--aw-bg` `--aw-surface` `--aw-surface-hover` `--aw-border` `--aw-text` `--aw-text-secondary` `--aw-accent` `--aw-accent-text` `--aw-danger` `--aw-focus-ring` |
| 尺寸 | `--aw-radius-s/m/l` `--aw-space-1 … 8` `--aw-control-height-s/m/l` `--aw-border-width` |
| 文字 | `--aw-font-family` `--aw-font-mono` `--aw-font-size-s/m/l` `--aw-line-height` |
| 动效 | `--aw-duration-fast/normal` `--aw-easing-standard` |
| 层次 | `--aw-shadow-1/2/3` `--aw-z-popup` `--aw-z-modal` `--aw-z-toast` |

## 一个控件的完整样子

（下例用 `role="button"` 的 `div` 实现。M1 的 `AwAUI.UI` 另有内建的 `Button` 元素（标签 `button`，自带 `Disabled` 与 Enter/Space 激活，见 [12](12-component-api.md)）；控件 `Button` 组件可以包装它，也可以像下例这样包装 `div`，二者在 C# 中分别是 `AwAUI.UI.Button` 与 `AwAUI.Controls.Button`。）

```razor
@* Button.awaui *@
<div role="button"
     class="aw-button @Class"
     tabindex="@(Disabled ? -1 : 0)"
     aria-disabled="@Disabled"
     data-loading="@Loading"
     @onclick="HandleClick"
     @onkeydown="HandleKey">
    @if (Loading) { <Spinner part="spinner" /> }
    @if (Icon != null) { <span part="icon">@Icon</span> }
    <span part="label">@Children</span>
</div>

@code {
    public ClassValue Class { get; set; }
    public bool Disabled { get; set; }
    public bool Loading { get; set; }
    public Node? Icon { get; set; }
    public NodeList Children { get; set; }
    public Action<PointerEvent>? OnClick { get; set; }
    ...
}
```

```css
/* 主题样式表（早期示例；Fluent 主题的实际类名与令牌见 23） */
.aw-button {
  display: inline-flex; align-items: center; justify-content: center; gap: var(--aw-space-2);
  height: var(--aw-control-height-m); padding: 0 var(--aw-space-4);
  border: var(--aw-border-width) solid var(--aw-border); border-radius: var(--aw-radius-m);
  background: var(--aw-surface); color: var(--aw-text);
  transition: background var(--aw-duration-fast) var(--aw-easing-standard);
  cursor: pointer; user-select: none;
}
.aw-button:hover { background: var(--aw-surface-hover); }
.aw-button:focus-visible { outline: 2px solid var(--aw-focus-ring); outline-offset: 2px; }
.aw-button[aria-disabled="true"] { opacity: .5; pointer-events: none; }
.aw-button.primary { background: var(--aw-accent); color: var(--aw-accent-text); border-color: transparent; }
@media (pointer: coarse) { .aw-button { min-height: 44px; } }
```

## 基础设施

控件库依赖的几项公共机制，它们本身也是公开 API：

| 机制 | 内容 |
|---|---|
| **Overlay / Portal** | 把子树渲染到窗口顶层的覆盖层；层级栈管理（弹层套弹层）；桌面上可选择用独立原生窗口承载以超出主窗口边界 |
| **定位引擎** | 相对锚点元素放置浮层：首选方位、空间不足时翻转与平移、与视口保持间距、箭头对齐、跟随锚点滚动与尺寸变化 |
| **关闭行为** | 点击外部、按 Esc、失去焦点、滚动时关闭，各自可配置；嵌套时只关闭最上层 |
| **焦点管理** | 焦点陷阱（模态）、打开时聚焦指定元素、关闭后恢复原焦点、漫游焦点（一组项只占一个 Tab 位，方向键切换） |
| **滚动锁定** | 模态层打开时阻止背景滚动 |
| **进出场** | `[data-state="open" / "closed"]` + CSS 过渡；协调器在退出动画结束后才移除元素 |
| **虚拟化** | 只创建可见范围内的项；支持定高、变高（测量后缓存）、分组、粘性组头；与滚动锚定、键盘导航、无障碍的集合信息配合 |
| **选择模型** | 单选、多选、范围选（Shift）、切换选（Ctrl/Cmd），列表类控件共用 |
| **类型前缀查找** | 在列表、菜单、下拉中键入字符跳到匹配项 |
| **表单** | `Form` 上下文：字段注册、校验（同步与异步）、提交、错误汇总与聚焦首个错误 |
| **图标** | `Icon` 组件渲染 SVG 路径，颜色随 `currentColor`。**已在 `AwAUI.UI` 实现**（第六批）：基于 `Svg` 内建元素（见 [05](05-ui-core.md)）的小组件，属性 `Document / Size / Label / Class`，渲染 `<svg class="aw-icon">`（默认样式表中 1em × 1em、`flex-shrink: 0`）；给了 `Label` 时角色为 Image 并带可访问名称，否则 `aria-hidden`。`SvgDocument` 解析一次，`CurrentColor` 取计算后的 CSS `color`，按尺寸缓存录制结果，文档用到 currentColor 时颜色才进入缓存键。控件库可以包装或直接复用它。第七批起内联 `<svg>` 可以直接写在 `.awaui` 里（编译期序列化，见 [17](17-awaui-grammar.md)），`Svg` 元素也应用 `object-fit` / `object-position` |
| **文本编辑基础**（第七批） | 内建 `Input` 已实现光标、选区、输入法（经平台 `TextInputMethod` / `ITextInputClient`）、剪贴板、按字素簇与按词编辑、指针选择与水平滚动（见 [05](05-ui-core.md)「文本输入」）。`TextField`、`SearchField`、`PasswordField`、`NumberField` 可直接包装它；`TextArea`（多行）、撤销 / 重做与光标闪烁仍缺 |
| **图片** | `Image` 第七批起画出位图像素，`object-fit` / `object-position` / `image-rendering` 只由 CSS 提供；元素有 `border-radius` 时内容裁成圆角（圆形头像）。加载中与失败时没有占位图或替代文本渲染，`Avatar` 的回退需要控件自己根据 `LoadState` 实现 |

## 控件清单（早期草案）

> 命名以 [23](23-component-library.md) 为准：`AwAUI.Controls` 用 Avalonia 名字（`TextBox`、`ComboBox`、`ListBox`、`TabControl`、`Expander`、`ToolTip`、`Flyout` ...），`AwAUI.Shadcn` 用 shadcn 名字（`Input`、`Select`、`Tabs`、`Accordion`、`Tooltip`、`Popover` ...）。下表保留用于对照部件与键盘需求。

期：**A** 随 M1–M2（框架自身与 DevTools 需要），**B** 随 M5 首批，**C** 之后。

### 基础与排版

| 控件 | 部件 | 说明 | 期 |
|---|---|---|---|
| `Button` | icon, label, spinner | 变体与尺寸 class；`Loading`；链接样式 | A |
| `IconButton` | icon | 必须提供可访问名称 | A |
| `Link` | — | 可聚焦的文本链接，角色 link | B |
| `Icon` | — | SVG 路径或图标集；`AwAUI.UI` 已有基础版（见上文「图标」） | A |
| `Divider` | — | 水平 / 垂直，角色 separator | A |
| `Badge` / `Tag` | label, close | 状态标记、可移除标签 | B |
| `Avatar` | image, fallback | 图片加载失败回退到文字 | C |
| `Spinner` / `Progress` | track, indicator | 确定与不确定两种；角色 progressbar | A / B |
| `Skeleton` | — | 加载占位 | C |

### 输入

| 控件 | 部件 | 键盘与要点 | 期 |
|---|---|---|---|
| `TextField` | input, prefix, suffix, clear | 包装 `input`：标签、前后缀、清除、错误提示 | A |
| `TextArea` | input | 自动增高、最大行数 | A |
| `PasswordField` | input, toggle | 显示/隐藏切换 | B |
| `NumberField` | input, increment, decrement | 上下键、滚轮、步进、范围、格式化 | B |
| `SearchField` | input, clear | 回车提交、Esc 清除 | B |
| `Checkbox` | box, indicator, label | 空格切换；不确定态；角色 checkbox | A |
| `Radio` / `RadioGroup` | circle, indicator, label | 方向键在组内移动并选中；漫游焦点 | B |
| `Switch` | track, thumb, label | 空格切换；触摸下可拖动；角色 switch | B |
| `Slider` | track, range, thumb | 方向键、Home/End、PageUp/Down；双滑块范围；触摸拖动与 `touch-action` | B |
| `Select` | trigger, value, popup, option | 上下键、类型查找、分组；移动端可切换为底部抽屉 | A |
| `Combobox` | input, popup, option | 输入过滤、异步选项、自由输入 | B |
| `DatePicker` / `Calendar` | grid, cell, nav | 方向键按日移动、PageUp/Down 换月；本地化 | C |
| `TimePicker` | — | | C |
| `ColorPicker` | area, slider, swatch | DevTools 样式面板需要 | A |
| `FileTrigger` | — | 调平台文件选择服务 | C |

### 容器与导航

| 控件 | 部件 | 说明 | 期 |
|---|---|---|---|
| `ScrollArea` | viewport, scrollbar, thumb | 自定义滚动条外观的滚动容器 | A |
| `SplitView` | pane, divider | 可拖动分隔；键盘调整；DevTools 需要 | A |
| `Tabs` | list, tab, panel | 方向键切换、自动或手动激活、可滚动的标签栏 | A |
| `Accordion` / `Disclosure` | header, trigger, content | 展开收起动画 | B |
| `Card` | header, body, footer | 具名插槽 | B |
| `Toolbar` | — | 漫游焦点 | A |
| `Breadcrumb` | item, separator | | C |
| `Pagination` | item, prev, next | | C |
| `NavigationView` | — | 侧栏 / 底部标签栏随宽度切换 | C |
| `Stepper` | step | | C |

### 集合

| 控件 | 说明 | 期 |
|---|---|---|
| `List` / `ListItem` | 虚拟化、选择模型、键盘导航、分组、空状态 | A |
| `Tree` / `TreeItem` | 虚拟化、展开收起、左右键进出层级、惰性加载子项；DevTools 元素面板需要 | A |
| `Table` | 虚拟化（行与列）、表头固定、列宽拖动、排序、行选择；角色 grid | B |
| `DataGrid` | 在 `Table` 之上：单元格编辑、冻结列、列重排、分组 | C |
| `Carousel` | 基于滚动吸附；指示器 | C |

### 浮层与反馈

| 控件 | 说明 | 期 |
|---|---|---|
| `Popover` | 锚定的非模态浮层 | A |
| `Tooltip` | 悬停延迟与键盘聚焦触发；触摸下长按；不可交互 | A |
| `Menu` / `ContextMenu` / `MenuBar` | 子菜单、勾选项、快捷键提示、类型查找；右键与长按触发 | A |
| `Dialog` / `AlertDialog` | 模态、焦点陷阱、Esc 关闭、滚动锁定；角色 dialog / alertdialog | A |
| `Drawer` / `Sheet` | 侧边与底部抽屉；底部抽屉支持拖拽、分段停靠 | B |
| `Toast` | 队列、自动消失、悬停暂停、滑动关闭；实时区域播报 | B |
| `Banner` / `Alert` | 行内提示 | B |
| `PullToRefresh` | 嵌套滚动联动 | B |
| `SwipeActions` | 列表项侧滑操作 | C |

### 应用外壳

| 控件 | 说明 | 期 |
|---|---|---|
| `Window` / `TitleBar` | 自绘标题栏、窗口控制按钮、可拖动区域；随平台调整按钮位置 | B |
| `SafeArea` | 按安全区内边距包裹内容 | B |
| `KeyboardAvoider` | 软键盘弹出时调整内容 | B |
| `ThemeProvider` | 切换主题与局部主题 | A |
| `ErrorBoundary` | 捕获子树错误显示后备界面 | A |

## 质量门槛

一个控件标记为完成需要：

1. 键盘、指针、触摸三种操作都可用，并有对应的自动化测试（经 DevTools 协议驱动）。
2. 无障碍审计无问题；语义树快照入库。
3. 明暗主题、RTL、放大字体下的黄金图像。
4. 出现在 `ControlCatalog`（Controls）或 `ShadcnGallery`（Shadcn）示例中，带用法与可定制项说明。
5. 公共 API（属性、事件、部件名、用到的 CSS 变量）记入 API 基线。
