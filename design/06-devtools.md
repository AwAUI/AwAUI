# 06 F12 调试机制（DevTools）

目标：像浏览器 F12 一样，在任何平台上检查和修改正在运行的 UI —— 元素树、组件树、CSS 规则、布局盒模型、绘制命令、帧性能、事件流。

由于框架采用「元素树 + 标准 CSS」，元素面板与样式面板的形态可以与浏览器 DevTools **几乎一致**：左边是带 class/id 的元素树，右边是命中的 CSS 规则（被覆盖的声明划掉）、计算值和盒模型，可以直接改 CSS 文本并立即生效。

## 架构

```
┌───────────────────── 被调试的应用进程 ─────────────────────┐
│  UI 核心 / Scene / Render / Platform                      │
│        │ ① 探针（结构化诊断事件 + 可查询状态）               │
│        ▼                                                 │
│  AwAUI.Diagnostics                                       │
│   ├ DiagnosticHub      事件总线，无订阅者时近零开销          │
│   ├ ObjectRegistry     元素/资源 ↔ 稳定 ID                 │
│   └ DevToolsAgent      ② 协议实现（按 Domain 组织）         │
│        │                                                 │
│        │ ③ 传输：InProc | WebSocket | 自定义                │
└────────┼─────────────────────────────────────────────────┘
         ▼
   DevTools 前端（AwAUI.DevTools，用 AwAUI 自己写）
   形态：应用内停靠面板 / 独立窗口 / 远程独立应用（桌面或浏览器）
```

四个部分各自独立：**探针**在核心里，**Agent** 把探针翻译成协议，**传输**可替换，**前端**只认协议。因此同一前端既能调试本进程，也能调试手机、浏览器里的应用；协议同时可被自动化测试和 IDE 插件复用。

## ① 探针：核心必须为调试提供什么

这是对其他模块的**反向约束**，需要从第一天就设计进去：

| 模块 | 必须提供 |
|---|---|
| CSS 引擎 | 每个元素命中的规则列表（选择器、特异性、来源文件:行）；每个属性的胜出声明与被覆盖声明；计算值；无效或不支持的声明及原因；运行时增删改规则。DevTools 的修改属于级联中独立的最高来源，可一键还原，且不会被组件重建冲掉 |
| 组件 | 组件树及其与元素的对应关系；Props、State 当前值；每次重建的触发原因（哪个 State 变了）与耗时；修改 State |
| 元素树 | 增删改事件；稳定 ID；所属组件；创建处源码位置（文件:行，`.awaui` 组件经源映射定位到具体标签） |
| 布局 | 最近一次的约束、期望尺寸、最终矩形、margin/padding/border；重排原因（谁打的脏） |
| 绘制 | 每个元素的 DisplayList（可序列化）；重绘原因；脏区 |
| 合成 | 层树；每层的提升原因、尺寸、纹理内存、是否本帧重绘 |
| 帧调度 | 每帧各阶段耗时、被处理的脏元素数、是否掉帧 |
| 输入 | 原始事件、命中测试结果、传播路径、谁阻止了传播；手势竞技场的参赛者与裁决过程 |
| 渲染后端 | 批次数、draw call、图集占用、能力降级事件、GPU 资源清单 |
| 文本 | 字体回退链、整形结果（字形、簇） |
| 响应式状态 | State / Computed 的当前值、订阅者、变更历史 |

开销控制：

- 探针写法为 `if (DiagnosticHub.IsEnabled(Category.Layout)) hub.Write(new LayoutEvent(...))`，事件为 struct，未启用时仅一次静态字段判断。
- 整体由特性开关 `AwAUI.Diagnostics.Enabled` 控制（`RuntimeFeature` + ILLink 替换），Release 关闭时探针代码被**裁剪掉**，零体积零开销。
- 「值来源」「创建位置」这类需要额外存储的数据，仅在开关打开时记录。

## ② 协议（决策 D11，已定：自有 JSON-RPC）

逐方法的签名见 [20-devtools-protocol.md](20-devtools-protocol.md)。

JSON-RPC 2.0 风格：请求/响应 + 事件推送，按 Domain 划分，借鉴 Chrome DevTools Protocol 的组织方式但使用自己的数据模型。序列化使用自带的轻量 UTF-8 JSON 读写器 + 自家源生成器产生的序列化代码（AOT 安全，且在 .NET Framework 上不引入额外依赖，见 [11](11-compatibility.md)）。

| Domain | 方法（示例） | 事件（示例） |
|---|---|---|
| `Tree` | `getDocument` `requestChildren` `pushNodePath` `describeNode` `querySelector(All)` `getNodeAt` `setAttribute` `removeAttribute` `setClasses` `getOuterText` `search` | `childrenChanged` `childMoved` `attributesChanged` `textChanged` `pseudoStateChanged` |
| `Components` | `getTree` `getProps` `getState` `setState` `getElementsOf` | `componentRebuilt`（含原因） |
| `Css` | `getMatchedRules` `getComputedStyle` `getInlineStyle` `setStyleText` `addRule` `getStyleSheets` `getStyleSheetText` `setStyleSheetText` `forcePseudoState` `clearOverrides` | `styleSheetChanged` `styleSheetAdded` `computedStyleInvalidated` |
| `Layout` | `getBoxModel` `getLayoutInfo` `explainLayout` | `layoutPass` |
| `Overlay` | `highlightNode` `hideHighlight` `setInspectMode` `setShowOptions` | `nodePicked` `inspectModeCanceled` |
| `Render` | `getDisplayList` `getLayerTree` `setDebugOption`(重绘闪烁/层边框/过度绘制/批次着色) `captureScreenshot` `captureNode` | `framePresented` |
| `Perf` | `startRecording` `stopRecording` `getFrameStats` | `frame`（各阶段耗时） |
| `Input` | `startLog` `dispatchPointer` `dispatchTouch` `dispatchKey` `emulateTouch` `recordGesture` `replayGesture` | `eventRouted` `gestureResolved` |
| `Text` | `getTextLayout` `getFontFallback` | |
| `Resources` | `listImages` `listFonts` `getAtlas` `getMemory` | |
| `A11y` | `getSemanticsTree` `getNodeSemantics` | `semanticsChanged` |
| `State` | `listStores` `getValue` `setValue` `getHistory` | `stateChanged` |
| `Console` | `getLogs` `listCommands` `invokeCommand` | `logEntry` |
| `App` | `getInfo`（平台、后端、能力、版本）`setTheme` `setScale` `simulate`(慢动画/安全区/字体缩放/RTL/减少动效) `reload` | |

表中是示例，准确的签名以 [20](20-devtools-protocol.md) 为准（属性编辑归入 `Tree` 域，不单设 `Attrs` 域）。M1 已实现 `App`、`Console`、`Tree`、`Css`、`Layout`（只有 `getBoxModel`）、`Overlay` 六个域，未实现的方法见 20。

关于 Console：AOT 平台不能运行时 `eval` C#。以**命令注册表**替代 —— 框架与应用可注册具名的调试命令（带类型化参数），DevTools 列出并调用。在支持 JIT 的桌面 Debug 构建上，可选接入 Roslyn 脚本作为增强（独立包）。

**关于 CDP**：采用元素树 + CSS 之后，`Tree`/`Css`/`Overlay` 域与 Chrome DevTools Protocol 的 DOM/CSS/Overlay 域在结构上已经很接近。主协议仍是自有的 JSON-RPC（组件、层、DisplayList、手势等信息 CDP 无处安放），但将来在 Agent 之上加一个 CDP 适配器的成本因此变低，可作为后置选项。

## ③ 传输

```csharp
public abstract class DevToolsTransport
{
    public abstract ValueTask SendAsync(ReadOnlyMemory<byte> message);
    public event Action<ReadOnlyMemory<byte>>? Received;
}
```

| 传输 | 场景 |
|---|---|
| `InProc` | 前端与应用同进程，直接对象传递，跳过序列化 |
| `WebSocket Server` | 桌面：监听本地回环端口；Android：`adb forward` 后从电脑连接；iOS：局域网或 USB 转发 |
| `WebSocket Client`（反连） | WASM 不能监听端口 → 应用主动连接到 DevTools 独立应用或开发服务器中继 |
| `BroadcastChannel` | WASM：同源另一个浏览器标签页里的 DevTools |

实现说明：netstandard2.0 没有 `ValueTask`，传输层的异步方法返回 `Task`；抽象基类另有 `SendWithBlobsAsync`（回复与其二进制块原子发送）、`AcceptsRemoteConnections` 与 `DisconnectAsync`。发布构建可通过 `RuntimeHostConfigurationOption`（`AwAUI.Diagnostics.Enabled`，`Trim="true"`）把整个诊断层裁剪掉。

安全：远程传输默认关闭；开启时默认只绑定回环地址；绑定到非回环地址时必须设置配对令牌，否则 Agent 拒绝挂接；跨设备连接需要一次性配对令牌；Release 构建默认把整个 Agent 裁剪掉，需显式 `UseDevTools()` + 开关才包含。

## ④ 前端

用 AwAUI 自己实现（最好的吃狗粮场景：树控件、虚拟化列表、属性编辑器、图表、分割面板全都用得上）。

**三种形态**

| 形态 | 说明 | 适用 |
|---|---|---|
| 内嵌停靠 | 在应用窗口内底部/右侧停靠，应用内容区域相应缩小 | 所有平台，尤其 Web 与平板 |
| 独立窗口 | 同进程第二个原生窗口 | 桌面默认 |
| 远程 | 独立的 DevTools 应用（桌面或 WASM 版），通过 WebSocket 连接目标 | 手机、嵌入式、调试卡死的应用 |

**与被调试 UI 的隔离**（内嵌与独立窗口形态）

- DevTools 拥有独立的元素树根、独立的样式作用域与主题，不受应用样式影响，也不出现在 `Tree` 域的结果中。
- 高亮、标尺、拾取器绘制在独立的 Overlay 层，位于应用内容之上、不参与应用的命中测试与布局。
- 拾取模式下，输入在路由进应用之前被 Agent 截获。
- DevTools 自身产生的帧与分配从性能统计中扣除并单独标示。
- 仍保留「调试 DevTools 自己」的开关（开发框架时需要）。

**唤起方式**

| 平台 | 默认 |
|---|---|
| 桌面 | `F12`；`Ctrl/Cmd+Shift+C` 直接进入元素拾取 |
| Web | 浏览器占用 F12 → 默认 `Ctrl+Shift+F12`，或 URL 参数 `?awdevtools`，或页面内悬浮按钮 |
| 移动端 | 三指长按 / 摇一摇 / 悬浮按钮；更常用的是远程连接 |
| 任意 | 代码 `DevTools.Open()`；可配置、可禁用 |

**面板**

| 面板 | 功能 |
|---|---|
| 元素 | 元素树（标签、id、class 一目了然）、搜索（支持 CSS 选择器）、拾取、悬停高亮；右侧：**样式**（命中的规则按级联顺序排列，被覆盖的声明划掉，可直接编辑、增删声明、切换 class、强制伪状态）、**计算值**（可追溯到来源规则）、盒模型图、属性、跳转源码 |
| 组件 | 组件树、Props 与 State 查看和修改、重建次数与原因高亮（找出多余的重建）、组件 ↔ 元素互跳 |
| 样式表 | 浏览与编辑全部样式表源文本，实时生效；不支持或无效的声明带警告标记 |
| 布局 | 约束与尺寸、重排原因、布局边界可视化、Flex/Grid 网格线叠加 |
| 绘制 | 选中元素的 DisplayList 逐命令列表，可**单步回放**（拖动滑块看画到第几条）；层树与提升原因；调试叠加：重绘闪烁、层边框、过度绘制热图、批次着色 |
| 性能 | 实时 FPS 与帧时间图；录制后按帧展开各阶段耗时瀑布、脏元素数量、掉帧标记；分配统计 |
| 事件 | 输入事件日志、传播路径、处理者；手势竞技场时间线；触摸轨迹叠加；录制与重放 |
| 资源 | 图片、字体（含回退链）、图集可视化、GPU 内存 |
| 无障碍 | 语义树、缺失名称等常见问题检查 |
| 状态 | 全局 Store 与各 State 的当前值、变更历史 |
| 控制台 | 日志（接 `ILogger`）、调试命令 |
| 环境模拟 | 设备尺寸与像素比、媒体特性（明暗、`hover`、`pointer`、减少动效）、鼠标模拟触摸（含双指捏合）、安全区、软键盘、字体缩放、RTL、慢速动画、强制软件渲染 |

## 实现顺序建议

DevTools 不是最后做的东西 —— 越早有它，框架自身开发越快：

1. **与元素树同期**：DiagnosticHub、ObjectRegistry、`Tree`/`Css`/`Overlay` 域、InProc 传输；一个最简的内嵌面板（元素树 + 命中规则 + 高亮）。—— M1 完成了除内嵌面板以外的部分（另有 `Layout.getBoxModel` 与 WebSocket 传输），见下文「M1 实现」；面板在第七批以独立窗口形态完成，见下文「第七批：AwAUI.DevTools 面板」。
2. **组件模型与布局完成后**：`Components` 域、CSS 实时编辑、盒模型、`Layout` 域。
3. **GPU 渲染器完成后**：`Render`/`Perf` 域、DisplayList 回放、调试叠加。
4. **移动/Web 平台接入时**：WebSocket 传输、远程形态、触摸模拟与手势日志。
5. 之后：事件、资源、无障碍、状态面板；自动化测试驱动 API；面向 AI 的接口；IDE 插件；可选 CDP 适配。

## M1 实现

**接缝**：`AwAUI.Diagnostics` 定义 UI 层要实现的抽象类 `InspectableTree`（节点描述、子节点、盒模型、选择器查询，可选的命中与属性编辑，以及 `Notify*` 变更通知）、`StyleInspector`（匹配规则、计算值与内联样式、改声明块与样式表、加规则、强制伪状态、清除覆盖）、`OverlayHost`（高亮、拾取模式），由 `DevToolsTarget.Tree / Styles / Overlay / Dispatcher` 挂到窗口上；数据以普通描述对象交换，Diagnostics 因此仍只依赖 Foundation。`AwAUI.UI/DevTools` 中的 `ElementInspectableTree`、`ElementStyleInspector`、`ElementOverlayHost` 实现它们（内部类型），节点 id 用 `Element.UniqueId`。

**开启**：`UIHost.EnableDevTools(UIDevToolsOptions)` 把窗口注册为目标，可选监听 `ws://127.0.0.1:<port>/devtools`（示例用 9230；监听非回环地址需要配对令牌），也可用 `UIDevTools.Connect(transport)` 接进程内前端；多个窗口可共享一个 Agent。DevTools 按窗口显式开启，整个诊断层仍受 `AwAUI.Diagnostics.Enabled` 特性开关控制。未开启时每次树变更通知只多一次字段判断；开启时建一次 O(n) 的 id 索引，之后每次插入 / 移除做线性的子树遍历。每个窗口在每帧 `RunFrame` 结束时调用 `Agent.FlushFrame()`。

**F12 拾取**：F12（`UIDevToolsOptions.ToggleInspectKey`）切换拾取模式，Esc 取消；拾取期间宿主捕获指针输入，输入不会到达应用，命中测试忽略 `pointer-events`。本地用 F12 发起的拾取在选中一次后退出并保留高亮（同浏览器）；前端经协议 `Overlay.setInspectMode` 发起的拾取保持开启，直到前端关闭（见 20）。

**覆盖层**：高亮按浏览器配色画 margin / border / padding / content 与尺寸标签，是一个单独的 DisplayList，由 `UIHost` 在呈现时叠加在应用帧之上；它不进入应用的 `Pipeline.Frame`，也不参与命中测试。覆盖层版本变化时即使应用帧没变也重新呈现，并且每次合成都重新读取布局，因此跟随布局变化。

**前端**：M1 时尚未实现，示例把 `UIDevTools.DescribeElement(picked)`（选中元素的匹配规则）打印到控制台，完整的检查与编辑经协议进行。第七批完成了独立窗口形态，见下文。

**与「① 探针」CSS 一行的偏差**：

- 经 `Css.setStyleText({ nodeId })` 或 `Tree.setAttribute(..., "style")` 做的内联编辑存放在管线里（`RenderBox.InlineOverride`），取代元素自己的 `Style` 文本、不会被组件重建冲掉、可由 `clearOverrides` 撤销，但它在级联中处于**内联层级**，而不是独立的 DevTools 来源。只有 `Css.addRule` 加的规则在 DevTools 来源（id 为 `devtools` 的样式表）。
- 规则编辑只替换该规则的声明块文本，重新解析整张表，并拒绝改变可编辑规则数量或种类的编辑（`@media`、嵌套规则、`{ }` 注入都会被拒），然后用 `StyleEngine.ReplaceStyleSheet` 原地替换（保持来源与级联位置）；每张表第一次被编辑时保存原表，`clearOverrides` 还原。
- 元素还没有源码位置（未生成 `.awaui` 源映射表）；组件样式表的 `sourceUrl` 是组件类型全名而不是 `.awaui` 路径（读 `[ComponentSource]` 需要反射）。

验证：DevTools 测试经进程内传输驱动真实 Agent（拾取、getDocument、getMatchedRules、setStyleText 改变下一帧像素、clearOverrides 还原）；整合任务曾在真实 macOS 窗口上经 WebSocket 跑通一次协议会话，但无法向真实窗口注入 F12 与点击。

## 第七批：AwAUI.DevTools 面板

新程序集 `AwAUI.DevTools`：用 `AwAUI.UI` 写成的检查器，在同进程的**第二个窗口**里打开（「独立窗口」形态）。应用内停靠形态与远程独立应用（单独的可执行程序）仍未做。

**只走协议**（决策 D33）：面板对应用的一切读写都经 `AwAUI.Diagnostics.Client.DevToolsClient`（见 [20](20-devtools-protocol.md)「实现约定」），同进程用 `InProcTransport`，远程用 `WebSocketClientTransport`，同一套界面。唯一直接接触应用宿主的地方是 `Attach` 时的接线：调用 `EnableDevTools` / `Connect`，并在 `FrameCompleted` 里读取 `UIDevTools.InspectMode`，以便应用按 F12 时打开面板。依赖：csproj 只引用 Foundation、Graphics、Platform、Css、Text、Diagnostics、UI；`ArchitectureTests` 的允许列表另含 Layout（只是允许，未实际引用）；**不引用** Render、Imaging、Svg —— 渲染后端与文本引擎由组合根传入，所以树的展开箭头用 CSS 边框三角形画，不用 `Icon`。

**托管 API**：

- `DevToolsPanel.Attach(UIHost app, PlatformBackend, RenderBackend, TextEngine, DevToolsPanelOptions?)`：进程内。只在应用**进入**拾取模式的那一刻打开面板（`OpenOnInspect`），所以拾取中关掉面板不会立刻又弹出来。
- `DevToolsPanel.Connect(DevToolsTransport, PlatformBackend, RenderBackend, TextEngine, DevToolsPanelOptions?)`：任意传输（例如 `WebSocketClientTransport`）。
- 之后用 `Open()` / `Close()` 控制窗口；另有 `IsOpen`、`Host`、`Window`、`Client`、`SelectedNodeId`、事件 `Opened` / `Closed`、`Dispose()`。`DevToolsPanelOptions { Title, Size, OpenOnInspect, TargetId, PairingToken }`。
- 面板自己的 `UIHost` 从不调用 `EnableDevTools`，所以检查不到自己（测试确认）；面板窗口里的 F12 由面板根元素的 `OnKeyDown` 处理，走协议拾取（第八批之前由事件包装 `PanelWindowEvents` 拦截）。应用窗口关闭时面板关闭、Agent 上的进程内会话被摘除，之后 `Open` 抛 `ObjectDisposedException`；请求在途时释放面板是安全的，高亮会撤销。

**功能**：

- **元素树**：按节点 id 增量镜像（`TreeMirror`，由 Tree 域事件维护）；遍历全是迭代的，节点数有上限，重复或成环的 id 被忽略。初次用 `getDocument` depth -1（Agent 每次最多返回 1 万个节点），展开时 `requestChildren` depth 2。只有一个短文本子节点的元素画成一行（同浏览器），组件的第一个元素旁标出组件名。树视图虚拟化：行高固定 20px，只为视口内的行（再多一行）建元素，行的 Key 是节点 id；滚动条可拖动（`SetPointerCapture`）；展开 / 折叠、方向键与 PageUp/PageDown 导航。结构事件只标记「需要夹紧滚动」，在下一次 `TreeView` 重建时执行，所以一批事件的代价是 O(事件数 × 子节点数) 加一次 O(行数)。
- **选中与拾取**：悬停某行时应用里高亮该元素，离开后回到选中元素的高亮；应用里按 F12 打开面板并进入拾取；面板工具栏的 Select、面板窗口里的 F12 或 Ctrl/Cmd+Shift+C 走协议拾取，选中一次后退出；Esc 取消。
- **Styles 页**：element.style、匹配的规则（来源文件:行号）与继承的规则（只显示可继承属性）；被覆盖的声明有删除线，无效或不支持的声明标出原因，颜色值旁有色块。
- **编辑**：点值即编辑（编辑框初始文本含 `!important`，与显示一致），回车提交、Esc 取消、失焦提交；可以新增 `name: value`；勾选框注释掉或恢复一条声明；Reset edits 调用 `Css.clearOverrides`。全部用已有的 `Css.setStyleText` 替换整个声明块，协议没有新增方法；块文本由已解析的声明重建（注释掉的写成 `/* */`），所以原来的注释与格式不保留；默认样式表不可编辑。原样提交不发请求；编辑期间推迟的刷新在提交后补做。
- **Computed 页**（默认隐藏初始值）、**Events 页**（最近 300 条协议事件）、底部盒模型图（`Layout.getBoxModel`）、状态栏的祖先路径与节点数。
- **状态**：集中在 `InspectorModel`，树、样式、外框、日志四个 `State<int>` 版本号分别驱动对应组件重建；样式刷新按令牌丢弃过期响应，同一轮调度里的多个事件只刷新一次。

**滚轮与按键**（第八批）：滚动系统落地后（[21](21-gesture-scroll-api.md)）删除了包装 `IWindowEvents` 的 `PanelWindowEvents`。树视口与右侧样式区用元素的 `OnWheel`：虚拟化的树只描述可见的行，由模型自己滚动，所以处理器调用 `PreventDefault` 阻止滚动系统的默认滚动（样式区同样由模型滚动，只有滚轮、没有滚动条）。快捷键挂在面板根元素的 `OnKeyDown` 上，根元素 `TabIndex = -1`（可程序聚焦、不在 Tab 顺序中），点击面板任何位置焦点都落在面板内，按键冒泡到根。测试 `PanelScrollTests`。

**示例**：Counter 的 `--devtools-panel`：窗口模式启动时打开面板；`--headless` 时另写一张「应用 | 面板」并排的 `<输出>-devtools.png`，面板中选中 `--highlight` 指定的元素（默认 `count`）。

**实测**（`-p:Optimize=true`，无头软件后端，面板 980×640 @1x，负载 7–19，只能当上界）：约 1 万个元素的应用（镜像 10 001 个节点，摊平后 6 802 行）打开面板并加载全部节点 166–318 ms 墙钟；滚轮后面板一帧中位数 3.9–9.65 ms（主要是光栅）；重新摊平 6 802 行 0.21–0.73 ms；一帧插入 400 行（401 条事件）只摊平 1 次、0 个额外请求。面板元素约 530 个、可见行 31 行，与树的大小无关（测试断言少于 1500 个）。

**验证范围与已知缺口**：

- 真实 macOS 窗口里只跑过 `Counter --devtools-panel --exit-after-ms`（应用与面板两个 Metal 窗口共用一个后端，面板渲染了 10 帧，没有崩溃）；没有截过真实窗口的图，也无法向真实窗口注入 F12 或点击，像素层面只经无头测试与 PNG 验证。Windows、Linux 与 net48 运行时都没有跑过。
- 没做：搜索（依赖协议里尚未实现的 `Tree.search`）、强制伪状态与属性 / class 编辑的界面（协议的 `Css.forcePseudoState`、`Tree.setAttribute` 已有）、样式表编辑、源码跳转、组件页。
- 面板模型收到 `Disconnected` 后只显示「Disconnected」，不会重连后重新 hello 与加载（客户端层支持重连，目前没有自动重连的传输）。
- 任何 `childrenChanged`、`childMoved`、`textChanged` 都会刷新选中元素的样式（3 个请求，按调度轮合并），应用每帧改文本时面板每帧发 3 个请求。
- 事件日志先把完整参数序列化再截成 160 个字符，大的 `inserted` 子树是 O(参数大小)。
- 超过 1 万节点后的「全部展开」没有测试（拾取路径经 `pushNodePath` 测过）。
- `ElementStyleInspector.ValidateDeclarationText` 曾把以完整注释 `/* … */` 结尾的声明文本当作「以 '/' 结尾」拒绝，面板在 `StyleBlock.BuildText` 里补一个 `;` 绕过；整合时已在 UI 层修好（在记号层判断末尾的 '/' 分隔记号，以完整注释结尾的文本被接受，`StyleTextValidationTests`），面板的 `;` 无害，仍保留。

## 派生价值

- **AI 闭环**：协议可以把运行中的界面以文本形式交给 AI —— 元素树（带 class 与计算后的关键样式）、命中的 CSS 规则、布局矩形、截图、构建期与运行期的诊断信息。在协议之上包一层 MCP 服务，AI 编码工具就能「写代码 → 运行 → 查看实际结果 → 修正」，而不是盲写。这是声明式 + CSS 路线对 AI 友好的另一半：不仅容易写，而且写完能被检查。

- **UI 自动化测试**：测试代码通过同一协议查找元素、读属性、注入输入、截图比对。
- **Bug 报告**：一键导出「元素树 + 属性 + DisplayList + 截图」快照，离线用 DevTools 打开复现。
- **设计走查**：标尺、间距测量、令牌显示。
