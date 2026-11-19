# 10 无障碍

自绘框架对系统辅助技术（读屏器、开关控制、语音控制）天然是一块不透明的画布，必须主动向系统暴露一棵语义树。此事在架构上从第一天就纳入，否则后补等于重写控件库。

## 模型：沿用 Web 的 ARIA

既然样式沿用 CSS，语义也沿用 WAI-ARIA 的角色、状态、属性模型 —— 这同样是 AI 最熟悉的一套词汇，并且在 Web 平台上可以一比一映射。

```csharp
new Div
{
    Role = Role.Button,
    AriaLabel = "关闭",
    AriaPressed = isOn.Value,
    TabIndex = 0,
    OnClick = ...,
}
```

- 标准控件内建正确的语义，应用开发者通常只需要为图标按钮、图片补充文本标签。
- `Text` 的内容自动成为可访问名称；`Image` 要求 `Alt`（分析器警告缺失）。

## 语义树

```
元素树 ──(过滤 + 合并)──► 语义树 ──(增量更新)──► 平台无障碍桥
```

每个语义节点包含：

| 字段 | 示例 |
|---|---|
| 角色 | button、checkbox、slider、textbox、list、listitem、dialog、heading、image、tab…… |
| 名称 / 描述 / 值 | 由 ARIA 属性、文本内容、关联标签按名称计算规则得出 |
| 状态 | disabled、checked、selected、expanded、focused、busy、invalid、hidden |
| 动作 | 点击、聚焦、增减、滚动、展开/折叠、设置值、自定义动作 |
| 几何 | 屏幕坐标下的边界（考虑变换与滚动） |
| 关系 | 父子、labelled-by、controls、实时区域（live region） |
| 文本信息 | 可编辑文本的内容、选区、按字符/词/行的范围查询 |

规则：

- 纯装饰性的盒子不产生语义节点；`aria-hidden` 子树被剔除。
- 语义树随元素树脏标记**增量更新**，并且仅在有辅助技术连接、或 DevTools / 自动化测试请求时才构建，平时零开销。
- 虚拟化列表需要向辅助技术报告总数与位置（`aria-setsize / aria-posinset`），并支持「滚动到下一项」的动作，使未实例化的项可达。

现状（第七批）：语义树仍未实现，但可编辑元素与图片已经备好要映射的数据 —— `InputElement` 以公共属性暴露值、选区（`SelectionStart / End / Anchor / Focus`、`SelectedText`）与组合状态（`IsComposing`、`CompositionText`），并经 `GetReflectedAttribute` 提供 `selectionStart` / `selectionEnd`（见 [05](05-ui-core.md)「文本输入」）；`ImageElement.AccessibleName` = `aria-label`，否则 `alt`。语义树做出来之后可直接映射到上表的「文本信息」与「名称」。

## 平台桥

| 平台 | 技术 | 备注 |
|---|---|---|
| Windows | UI Automation provider | 实现相应的控件模式（Invoke、Toggle、Value、Text、Scroll…） |
| macOS | NSAccessibility 协议 | 通过 ObjC 运行时注册无障碍元素类 |
| iOS | UIAccessibilityElement 容器 | VoiceOver 手势、无障碍自定义动作、Dynamic Type |
| Android | AccessibilityNodeProvider（虚拟视图层级） | TalkBack、触摸浏览、无障碍焦点 |
| Linux | AT-SPI2（D-Bus） | 需要一个纯 C# 的 D-Bus 客户端 |
| Web | 覆盖在 canvas 上的不可见 ARIA DOM | 与语义树同步；ARIA 模型可直接映射 |

平台桥属于 `Platform.*`，通过 `AccessibilityBridge` 接收语义树的增量更新，并把辅助技术发起的动作回传给元素。

## 不止读屏器

| 需求 | 设计对应 |
|---|---|
| 键盘可达 | 所有交互控件可聚焦、可用键盘操作；`:focus-visible` 焦点环；焦点陷阱（对话框） |
| 触摸浏览 | TalkBack / VoiceOver 下单指滑动遍历、双击激活 —— 由平台桥驱动语义树上的动作，绕过普通手势竞技场 |
| 字体缩放 | 响应系统字体大小设置；`rem` 以其为基准 |
| 高对比度 | `@media (prefers-contrast)`、`forced-colors`；标准控件主题提供高对比变体 |
| 减少动效 | `@media (prefers-reduced-motion)` |
| 色彩 | DevTools 提供对比度检查与色觉模拟 |
| 最小触摸目标 | 见 09 |
| 从右到左 | `direction: rtl`；布局使用逻辑属性（`margin-inline-start` 等） |

## 测试与调试

- DevTools 无障碍面板：语义树、每个节点的计算名称及其来源、常见问题审计（缺名称、对比度不足、目标过小、焦点顺序异常）。
- 语义树是 UI 自动化测试的首选定位方式（按角色与名称查找元素），比按坐标或内部结构稳定。
- CI 可对示例与控件库跑无障碍审计。

## 分期

| 阶段 | 内容 |
|---|---|
| 随控件库 | ARIA 属性、语义树构建、键盘可达、焦点管理、DevTools 面板与审计 |
| 全平台阶段 | Windows UIA、macOS、Web ARIA DOM |
| 移动阶段 | Android、iOS 桥，触摸浏览 |
| 之后 | Linux AT-SPI2、文本范围的完整支持 |
