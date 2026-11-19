# 20 DevTools 协议（逐方法定义）

[06](06-devtools.md) 定义了架构与 Domain 划分，本文定义线上格式与各方法的签名。签名用类 TypeScript 记法书写，仅为表达形状；实现是 C# 类型 + 生成的序列化代码。

## 消息格式

JSON-RPC 2.0，UTF-8 文本帧：

```jsonc
// 请求
{ "jsonrpc": "2.0", "id": 12, "method": "Css.getMatchedRules", "params": { "nodeId": 345 }, "target": "w1" }
// 响应
{ "jsonrpc": "2.0", "id": 12, "result": { ... } }
// 错误
{ "jsonrpc": "2.0", "id": 12, "error": { "code": -32001, "message": "Node not found", "data": { "nodeId": 345 } } }
// 事件（无 id）
{ "jsonrpc": "2.0", "method": "Tree.childrenChanged", "params": { ... }, "target": "w1" }
```

- `target`：一个应用可以有多个窗口（每个窗口一棵元素树）。省略时指默认目标。
- 大块二进制（截图、图集、序列化的 DisplayList）走 WebSocket 二进制帧：先发一条带 `blobId` 与长度的 JSON 响应，紧跟一个二进制帧。进程内传输直接传递对象。
- 进程内传输跳过 JSON，直接调用同一套强类型接口；协议的类型定义是唯一事实来源。

### 实现约定（M0 实现中确定）

- 不支持批量请求：数组形式的请求返回 -32600，id 为 null。
- 无效的请求对象即使没有 id 也返回 -32600（id 为 null）；按位置传参（数组 params）返回 -32602，通知则静默丢弃。
- `App.hello` 主版本不匹配时返回 -32602（data 中带服务端的 protocolVersion），随后断开连接。
- 停用某个域时，已产生但仍在发件队列中、尚未送达的事件会被丢弃（停用即停止推送）；需要完整日志的前端应在重新启用时依赖缓冲区补发。
- WebSocket 发送队列有字节上限（默认 32MiB），溢出时以 1008 关闭连接，前端需重连并重新 enable；未发送的 pong 会合并为最新一个。

### 实现约定（M1 检查域）

- `App.hello` 的 `domains` 列出 App、Console、Tree、Css、Layout、Overlay；窗口没有对应接缝时，该域的方法返回 -32004。
- Tree / Css / Layout / Overlay 中访问元素树的方法投递到目标窗口的 UI 线程（`DevToolsTarget.Dispatcher`，没有时用 Agent 的调度器），处理、响应与后续动作在那里一次完成：响应总在同一线程上之后产生的事件之前发出，对同一窗口的请求按到达顺序处理。enable / disable、`App.*`、`Console.*` 不投递，因此可能比之前排队在 UI 线程上的请求先得到响应（JSON-RPC 允许乱序，客户端按 id 匹配）。
- 会话在 `{Domain}.enable` 的响应发出之后才开始接收该域的事件；从 enable 被处理到响应发出之间（在其他线程上）产生的事件不发给该会话，所以事件总在启用它们的响应之后。需要 enable 的方法若在投递到 UI 线程期间会话已停用该域，返回 -32003 且不产生副作用（已做出的高亮、拾取模式、强制伪状态会被撤销）。
- 接缝抛出的 `FormatException` 映射为 -32005，`NotSupportedException` 映射为 -32004；「找不到」由接缝以 null / false / 0 表示。
- 标识符（由 UI 层的适配器产生）：节点 id = `Element.UniqueId`，必须稳定、不复用、在 [1, 2^53−1] 内；`Text` 元素的 `Content` 报告为一个合成的文本子节点，id = 2^52 + 元素 id。样式表 id 按发现顺序为 `s1`、`s2`……，DevTools 来源的样式表为 `devtools`；规则 id = `<sheetId>/<index>`，index 按 `EnumerateRules` 顺序数样式规则与嵌套声明规则，在 `setStyleText` 之后保持不变（`setStyleSheetText` 之后可能变化）。组件样式表的 `sourceUrl` 是组件类型全名（全局样式加 ` (global)`）。
- 按帧合并的事件在 `DevToolsAgent.FlushFrame()` 时发出，宿主必须每帧在 UI 线程上调用（`UIHost` 在每次 `RunFrame` 末尾调用）。
- 上限（`DevToolsAgentOptions`）：一次响应最多展开 `MaxTreeNodesPerResponse`（10 000）个节点、嵌套 `MaxTreeDepthPerResponse`（24）层（让响应不超过默认的 64 层读取上限），广度优先，被请求节点的直接子节点总是完整返回；文本截断到 `MaxNodeTextLength`（10 000，不拆代理对）；`querySelectorAll` 超限时带 `truncated: true`；`getOuterText` 受节点预算与约 1M 字符限制。所有遍历都是迭代的。

### 实现约定（第七批：前端侧客户端）

`AwAUI.Diagnostics` 新增前端用的客户端，`AwAUI.DevTools` 面板只经它访问应用（见 [06](06-devtools.md)）：

- `AwAUI.Diagnostics.Client.DevToolsClient(DevToolsTransport, DevToolsClientOptions?)`：JSON-RPC 前端。`Call(method, params, target, Action<DevToolsResponse>)`、`CallAsync(...)`、`Hello(client, token, cb)`；事件 `EventReceived(DevToolsEvent)`、`Disconnected`；`PendingCount`、`DroppedMessages`。按 id 匹配响应、分发事件；`DevToolsClientOptions.Dispatcher` 设置后所有回调都投递到该调度器并按消息到达顺序执行，即使当前已在 UI 线程上也照样投递，所以进程内的同步投递不会在调用方的栈上重入。拒绝请求时锁内只决定结果、锁外再回调（没有调度器时回调同步执行，不会在客户端锁内死锁）。
- 客户端错误码（**不在**下表的协议错误码中，只由客户端本地产生）：`DevToolsClient.DisconnectedErrorCode = -32099`（断开或已释放：未完成的请求立即失败，断开期间的新请求也立即失败，不会挂起）、`TooManyRequestsErrorCode = -32098`（未完成请求超过 `MaxPendingRequests`，默认 4096）。重连后旧连接的迟到响应被丢弃，不会完成新请求，并计入 `DroppedMessages`。JSON 深度上限 `MaxMessageDepth`（默认 128），畸形消息丢弃并计数。
- `AwAUI.Diagnostics.Transport.WebSocketClientTransport.ConnectAsync(Uri, WebSocketClientTransportOptions?, CancellationToken)`：基于 `ClientWebSocket`。`WebSocketClientTransportOptions { MaxMessageSize = 64 MiB, ConnectTimeout = 10 s, KeepAliveInterval = 0 }`；入站消息超过上限时以 1009 关闭；blob 与响应原子发送；断开只报告一次。不自动重连。

### 错误码

| 码 | 含义 |
|---|---|
| -32700 … -32600 | JSON-RPC 标准错误（解析、无效请求、方法不存在、参数无效） |
| -32001 | 节点不存在或已被移除 |
| -32002 | 目标不存在 |
| -32003 | Domain 未启用 |
| -32004 | 当前运行时或平台不支持该操作 |
| -32005 | 值无法解析（CSS 文本、属性值） |
| -32006 | 未授权（远程连接未完成配对） |

## 会话

```ts
App.hello(params: { client: string, protocolVersion: string, token?: string })
  → { protocolVersion: string, app: AppInfo, targets: TargetInfo[], domains: string[] }

type AppInfo = {
  name: string, frameworkVersion: string, runtime: string,          // ".NET 10.0.1"、".NET Framework 4.8.1"
  platform: string, renderBackend: string, capabilities: string[],
  features: { hotReload: boolean, renderThread: boolean, aot: boolean }
}
type TargetInfo = { id: string, title: string, size: Size, scale: number }
```

- **版本**：`主.次`。主版本不同拒绝连接；次版本取双方较小者，新方法在旧端返回「方法不存在」，前端据此隐藏功能。
- **启用**：每个 Domain 有 `enable() / disable()`。未启用的 Domain 不产生事件、对应探针保持关闭，保证「只开元素面板」时不付出性能面板的开销。
- **事件节流**：高频事件（属性变化、帧统计）按帧合并，每帧至多一批。
- **远程安全**：`App.hello` 必须携带配对令牌，否则除 `App.hello` 外一律返回 -32006。

## 公共类型

```ts
type NodeId = number            // 会话内稳定；节点移除后不复用
type ComponentId = number
type Rect = { x: number, y: number, width: number, height: number }
type Quad = [number, number, number, number, number, number, number, number]   // 变换后的四个角
type Color = string             // "#rrggbbaa"
type SourceLocation = { file: string, line: number, column: number }

type Node = {
  id: NodeId, parentId?: NodeId,
  kind: "element" | "text",
  tag?: string, elementId?: string, classes?: string[],
  attributes?: Record<string, string>,
  text?: string,                              // 文本节点内容（过长时截断并标记）
  componentId?: ComponentId,                  // 产生它的组件
  pseudoStates?: string[],
  childCount: number, children?: Node[],
  source?: SourceLocation
}
```

## Tree

```ts
Tree.enable() / Tree.disable()
Tree.getDocument(params: { depth?: number }) → { root: Node }          // depth 默认 2，-1 为整棵树
Tree.requestChildren(params: { nodeId, depth?: number }) → { children: Node[] }
Tree.querySelector(params: { nodeId, selector: string }) → { nodeId?: NodeId }
Tree.querySelectorAll(params: { nodeId, selector: string }) → { nodeIds: NodeId[] }
Tree.search(params: { query: string }) → { results: { nodeId, match: string }[] }   // 文本、标签、class、选择器
Tree.getNodeAt(params: { x, y, ignorePointerEvents?: boolean }) → { nodeId?: NodeId }
Tree.setAttribute(params: { nodeId, name, value }) / Tree.removeAttribute(params: { nodeId, name })
Tree.setClasses(params: { nodeId, classes: string[] })
Tree.getOuterText(params: { nodeId, includeStyles?: "none" | "key" | "all" }) → { text: string }
Tree.scrollIntoView(params: { nodeId }) / Tree.focus(params: { nodeId })
// M1 新增（CDP 式）
Tree.pushNodePath(params: { nodeId }) → { path: NodeId[], children: { parentId, children: Node[] }[] }   // 展开祖先以显示拾取到的节点；path 从根开始
Tree.describeNode(params: { nodeId }) → { node: Node }                                                  // 单节点快照，不进入镜像

// 事件
Tree.childrenChanged { parentId, removed: NodeId[], inserted: { node: Node, beforeId?: NodeId }[] }
Tree.childMoved { parentId, nodeId, beforeId? }                 // M1 新增：同一父节点内移动，前端保留子树
Tree.attributesChanged { nodeId, attributes: Record<string, string | null>, classes?: string[], elementId?: string | null }
Tree.textChanged { nodeId, text, textTruncated? }
Tree.pseudoStateChanged { nodeId, states: string[] }
Tree.childCountUpdated { nodeId, childCount }                   // M1 新增：已发送但未展开的节点
Tree.documentUpdated {}                                         // M1 新增：整棵树被替换，前端重新 getDocument
```

M1 实现说明：

- `Node` 另有 `componentName?` 与 `textTruncated?`。`Text` 内的 `Span` 与文本节点同样是节点，但没有自己的布局盒（见下文 `Layout.getBoxModel`）。
- **镜像**：服务端为每个会话、每个窗口记住已发送的节点（同 CDP）。节点「已发送」指它是 getDocument 的根或某个已展开节点的子节点；节点的 children 一旦发送即「已展开」，它的所有直接子节点都已发送（叶子元素在应展开的层级上带 `children: []`，以便之后的插入以 childrenChanged 到达）。结构事件只发给已有该节点的会话，所以展开了不同部分的会话收到不同的事件；已展开父节点下推送 childrenChanged / childMoved，未展开的只推送 childCountUpdated。`describeNode`、`querySelector` 返回的 id 不进入镜像，但可用于任何方法。
- `getDocument`、`requestChildren`、`pushNodePath` 需要 `Tree.enable`（它们建立的镜像只靠事件保持更新），disable 丢弃镜像；对未发送给本会话的节点 `requestChildren` 返回 -32602。只读方法（`describeNode`、`querySelector*`、`getOuterText`）不需要 enable。
- **跨父移动**可以按「移除 + 插入」以任意顺序报告；若先报插入，服务端先向该会话推送一条从旧父节点移除的 childrenChanged，再推送插入，迟到的移除通知被忽略。`NotifyChildMoved` 只用于同一父节点内的移动。
- 按帧合并：attributesChanged（`attributes` 只含本帧变化的属性，null 表示移除；class 变化带完整 `classes`，id 变化带 `elementId`）、textChanged、pseudoStateChanged、childCountUpdated。立即发送：childrenChanged、childMoved、documentUpdated。
- `getOuterText` 格式：每行一个节点，每级缩进两个空格，不写结束标签；元素写作 `<tag id=".." class=".." attr=".." style="..">`，文本节点为带引号的转义字符串；`includeStyles: "key"` 输出固定的一组计算属性，`"all"` 输出全部。
- `setAttribute`：id、class、style 与自定义属性可编辑（name 为 `id` / `class` 时修改元素 id / class，style 见下文 Css）；映射到内建属性的 role、aria-label、tabindex、src、alt、type、value、placeholder、disabled、readonly 返回 -32004。
- 尚未实现：`Tree.search`、`Tree.scrollIntoView`、`Tree.focus`（接缝上也还没有对应方法）。

`Tree.getOuterText` 把子树输出为缩进的标记文本，是给日志、Bug 报告和 AI 使用的紧凑形式。

## Css

```ts
Css.getMatchedRules(params: { nodeId }) → {
  inline: Declaration[],
  matched: RuleMatch[],                       // 按级联优先级从高到低
  inherited: { fromNodeId: NodeId, matched: RuleMatch[] }[],
  pseudoElements: { name: string, matched: RuleMatch[] }[]
}
Css.getComputedStyle(params: { nodeId }) → { properties: ComputedProperty[] }
Css.setStyleText(params: { target: { nodeId } | { ruleId }, text: string }) → { declarations: Declaration[] }
Css.addRule(params: { selector: string, text?: string }) → { rule: Rule }           // 写入 DevTools 专用样式表
Css.getStyleSheets() → { sheets: StyleSheetInfo[] }
Css.getStyleSheetText(params: { sheetId }) → { text: string }
Css.setStyleSheetText(params: { sheetId, text: string }) → { diagnostics: Diagnostic[] }
Css.forcePseudoState(params: { nodeId, states: string[] })
Css.clearOverrides(params: { nodeId?: NodeId })
Css.getVariables(params: { nodeId }) → { variables: { name, value, definedOn: NodeId, ruleId?: string }[] }

type RuleMatch = { rule: Rule, matchedSelector: string, specificity: [number, number, number] }
type Rule = {
  id: string, selectorText: string, origin: "default" | "author" | "devtools",
  layer?: string, media?: string, sheetId: string, source?: SourceLocation,
  declarations: Declaration[]
}
type Declaration = {
  name: string, value: string, important: boolean,
  status: "active" | "overridden" | "invalid" | "unsupported" | "inactive",
  reason?: string,                            // 无效或不生效的原因，如「父元素不是 flex 容器」
  disabled?: boolean, range?: { start: number, end: number }
}
type ComputedProperty = { name: string, value: string, from?: { ruleId?: string, nodeId?: NodeId, origin: string } }

// 事件
Css.styleSheetChanged { sheetId }
Css.styleSheetAdded { sheet: StyleSheetInfo } / Css.styleSheetRemoved { sheetId }
Css.computedStyleInvalidated { nodeIds: NodeId[] }
```

M1 实现说明：

- `Css.getInlineStyle(params: { nodeId }) → { declarations: Declaration[] }`。
- 本文未定义的形状：`StyleSheetInfo = { sheetId, origin, sourceUrl?, title?, disabled, editable, ownerNodeId? }`（M1 不设 `ownerNodeId`）；`Diagnostic = { severity: "error" | "warning" | "info", message, code?, line?, column? }`；`Declaration.range = { start, end }` 是声明块文本中的位置；没有声明设置值时 `ComputedProperty.from.origin` 为 `"initial"`。
- 增加、删除、开关声明都用 `setStyleText`（替换整个声明块）；注释掉的声明 `/* name: value */` 以 `disabled: true` 报告。对规则调用时只替换声明块（花括号内、第一个嵌套规则之前的文本），含 `{` 或 `}` 的文本返回 -32005（内联样式同样）；对节点调用时设置内联覆盖（见 [06](06-devtools.md)「M1 实现」）。不存在的规则或样式表返回 -32602，data 带 `{ ruleId }` 或 `{ sheetId }`。
- `forcePseudoState` 需要 enable，只接受 hover、active、focus、focus-visible、focus-within、disabled、checked、placeholder-shown。
- 声明状态按源声明聚合 `StyleMatchRecord`：任一展开后的长属性胜出即 active，否则为 overridden / invalid / unsupported。继承的规则与计算值来源最多向上查 64 个祖先。
- `Css.computedStyleInvalidated` 按帧合并，只列出 Tree 域已发送给该会话的节点；styleSheet* 事件立即发送。`Css.enable` 不会把已有样式表重放为 styleSheetAdded，前端应调用 `getStyleSheets`。
- 尚未实现：`Css.getVariables`。

## Components

```ts
Components.getTree(params: { depth?: number }) → { roots: ComponentNode[] }
Components.getDetails(params: { componentId }) → {
  type: string, source?: SourceLocation,
  props: Value[], state: Value[], context: Value[],
  rootNodeIds: NodeId[], rebuildCount: number, lastRebuild?: RebuildInfo
}
Components.setState(params: { componentId, name: string, value: string })   // value 为 JSON 字面量
Components.highlightRebuilds(params: { enabled: boolean })

type ComponentNode = { id: ComponentId, type: string, key?: string, children: ComponentNode[], childCount: number }
type Value = { name: string, type: string, preview: string, editable: boolean, expandable: boolean, handle?: string }
type RebuildInfo = { frameId: number, durationMs: number, causes: { kind: "state" | "props" | "context" | "parent", name?: string }[] }

Components.getValueChildren(params: { handle: string }) → { values: Value[] }   // 展开对象与集合

// 事件
Components.treeChanged { parentId?: ComponentId }
Components.rebuilt { componentId, info: RebuildInfo }
```

## Layout / Overlay

```ts
Layout.getBoxModel(params: { nodeId }) → {
  content: Quad, padding: Quad, border: Quad, margin: Quad,
  width: number, height: number, scrollOffset?: Point, scrollSize?: Size
}
Layout.getLayoutInfo(params: { nodeId }) → {
  display: string, constraints: { minWidth, maxWidth, minHeight, maxHeight },
  flex?: { ... }, grid?: { columns: number[], rows: number[], areas?: ... },
  isLayoutBoundary: boolean, lastLayoutFrame: number, invalidatedBy?: { nodeId: NodeId, property?: string }
}

Overlay.highlightNode(params: { nodeId, config?: HighlightConfig }) / Overlay.hideHighlight()
Overlay.setInspectMode(params: { mode: "none" | "pick" | "measure" })
Overlay.setShowOptions(params: { layoutGrids?: NodeId[], rulers?: boolean, hitTargets?: boolean, touches?: boolean })

// 事件
Overlay.nodePicked { nodeId } / Overlay.inspectModeCanceled {}

type HighlightConfig = { showInfo?, showRulers?, showExtensionLines?,
                         contentColor?, paddingColor?, borderColor?, marginColor? }   // 颜色为 #rgb / #rgba / #rrggbb / #rrggbbaa
```

M1 实现说明：

- `Layout.getBoxModel` 的四边形为 8 个数（从左上角起顺时针，窗口坐标），`width / height` 是边框盒尺寸；没有布局盒的节点（`Text` 内的 `Span`、文本节点）返回 -32602，非有限坐标写成 0。`Layout.getLayoutInfo` 与 `layoutPass` 事件尚未实现。
- Overlay 的方法都需要 enable；颜色格式错误返回 -32602。覆盖层由一个窗口的所有会话共享，后设置者生效；设置高亮或拾取模式的会话停用 Overlay 或断开时，高亮被隐藏、模式回到 none（所以「高亮后立即断开」的脚本看不到高亮）。`nodePicked` 发给所有启用了 Overlay 的会话；经协议进入的拾取模式不会因一次拾取而退出。`Overlay.setShowOptions` 尚未实现。

## Render / Perf

```ts
Render.getLayerTree() → { root: Layer }
Render.getDisplayList(params: { nodeId?: NodeId, layerId?: string, format: "summary" | "commands" | "binary" })
  → { commands?: Command[], blobId?: string, bounds: Rect }
Render.replay(params: { layerId: string, toCommand: number, scale?: number }) → { blobId: string }   // 回放到第 N 条的图像
Render.captureScreenshot(params: { nodeId?: NodeId, format: "png", scale?: number }) → { blobId: string }
Render.setDebugOptions(params: { paintFlashing?, layerBorders?, tileBorders?, overdraw?, batchColors?, forceSoftware? })
Render.getStats() → { layers, textureBytes, atlasPages, drawCalls, batches, degradations: string[] }

type Layer = { id: string, kind: string, nodeId?: NodeId, bounds: Rect, reasons: string[], memoryBytes: number,
               repaintedFrame: number, children: Layer[] }
type Command = { index: number, op: string, bounds?: Rect, nodeId?: NodeId, summary: string }

Perf.startRecording(params: { categories?: string[] }) / Perf.stopRecording() → { blobId: string }
Perf.getRealtime(params: { enabled: boolean })

// 事件
Perf.frame { frameId, timestamp, phases: { input, animate, build, style, layout, paint, commit, composite, raster, present },
             dirty: { components, styled, laidOut, painted }, dropped: boolean, longTasks?: { name, ms }[] }
```

## Input

```ts
Input.dispatchPointer(params: { type: "down" | "move" | "up" | "cancel", pointerType: "mouse" | "touch" | "pen",
                                pointerId?: number, x, y, button?: number, pressure?: number, modifiers?: string[] })
Input.dispatchWheel(params: { x, y, deltaX, deltaY, precise?: boolean })
Input.dispatchKey(params: { type: "down" | "up", key: string, code?: string, modifiers?: string[] })
Input.insertText(params: { text: string })
Input.dispatchGesture(params: { kind: "tap" | "longPress" | "swipe" | "pinch" | "scroll", ... })   // 合成整段触摸序列
Input.setTouchEmulation(params: { enabled: boolean })
Input.startRecording() / Input.stopRecording() → { blobId: string } / Input.replay(params: { blobId: string, speed?: number })

// 事件（需 Input.enable）
Input.eventDispatched { type, targetId: NodeId, path: NodeId[], handledBy?: NodeId, defaultPrevented: boolean }
Input.gestureArena { pointerId, entries: { recognizer: string, nodeId: NodeId, outcome: "won" | "rejected" | "cancelled", reason?: string, atMs: number }[] }
```

## 其余 Domain

```ts
Text.getLayout(params: { nodeId }) → { lines: { baseline, rect: Rect, runs: { font, size, script, direction, glyphCount, text }[] }[] }
Text.getFontFallback(params: { nodeId }) → { requested: string[], used: { family, face, source, codepoints: string }[] }

Resources.getImages() → { images: { id, source, size: Size, decodedBytes, usedBy: NodeId[] }[] }
Resources.getFonts() → { fonts: { family, face, source, loadedTables: string[] }[] }
Resources.getAtlas(params: { kind: "glyph" | "color" | "mask", page: number }) → { blobId: string, usage: number }
Resources.getMemory() → { managedBytes, textureBytes, displayListBytes, breakdown: Record<string, number> }

A11y.getTree(params: { nodeId?: NodeId }) → { root: SemanticsNode }
A11y.getNode(params: { nodeId }) → { role, name, nameSource: string, description?, value?, states: string[], actions: string[], bounds: Rect }
A11y.audit(params: { nodeId?: NodeId }) → { issues: { nodeId, rule: string, severity, message }[] }
A11y.performAction(params: { nodeId, action: string, value?: string })

State.getStores() → { stores: Value[] }
State.setValue(params: { handle: string, value: string })
State.stateChanged { name: string, preview: string, frameId: number }

Console.enable() → 之后推送 Console.entry { level, message, timestamp, source?: SourceLocation, componentId?, exception? }
Console.getCommands() → { commands: { name, description, parameters: { name, type, optional }[] }[] }
Console.invoke(params: { name: string, args: Record<string, unknown> }) → { result?: string }

App.setEmulation(params: { colorScheme?, reducedMotion?, hover?, pointer?, scale?, fontScale?, direction?,
                           safeArea?: Insets, viewport?: Size, keyboardHeight?: number, animationSpeed?: number })
App.reloadStyles() / App.restart()
```

## 面向自动化与 AI 的约定

- 所有读取类方法**无副作用且可在任意时刻调用**；写入类方法在下一帧生效，调用方可用 `App.waitForIdle()`（无脏标记、无运行中的过渡）等待稳定后再读取或截图。
- `Tree.getOuterText` + `Css.getMatchedRules` + `Render.captureScreenshot` + 编译与运行期诊断，构成 AI 检查界面所需的最小集合；上层的 MCP 服务只是对这些方法的薄封装。
- 协议的类型定义同时生成一份机器可读的描述文件（方法、参数、类型），供第三方客户端生成绑定。
