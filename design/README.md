# AwAUI 设计文档

AwAUI 是一个用 C# 实现的跨平台自绘 UI 引擎：**声明式组件（`.awaui` 单文件格式）+ 标准 CSS + 纯 C# 渲染器 + 内建 F12 调试**。本目录是设计文档；决策记录见下方表格。

## 目标

1. **全平台**：Windows、macOS、Linux（X11 / Wayland）、Android、iOS、WebAssembly，同一套 UI 代码。
2. **自绘且纯 C#**：矢量光栅化、文本整形与排版、图片解码、GPU 渲染器、CSS 引擎全部用 C# 实现，不依赖 Skia / HarfBuzz / FreeType 等第三方原生库。当前只提供这一套自研实现。
3. **绘制引擎可替换**：抽象层允许将来接入其他引擎，UI 层零改动；但现阶段不实现任何第三方后端。
4. **声明式、对 AI 友好**：UI = f(state)；默认用类 Razor 的 `.awaui` 单文件组件编写（标记 + C# + 作用域 CSS），也可直接写 C#；强类型、编译期可校验，运行时可被机器观察。
5. **内建 F12 调试**：元素树、组件树、CSS 规则溯源、布局、绘制、性能，是框架的一等能力。
6. **完整的文字支持**：覆盖全部书写系统的整形（含阿拉伯文、印度系等复杂文字）、双向文本、Emoji。
7. **移动端一等公民**：触摸、手势、惯性滚动、软键盘、无障碍从设计之初纳入。
8. **广泛的运行时支持**：.NET Standard 2.0、.NET Framework 4.7.2+、.NET 8–11。netstandard2.0 是功能基线，现代 .NET 是性能与 AOT 的增强层。
9. **AOT 优先**：iOS 与 WASM 无 JIT，全框架可裁剪、NativeAOT 友好，不依赖运行时反射。

## 非目标（v1）

- 不封装原生控件，所有像素自己画。
- 不做 XAML，不做完整的 HTML/文档排版引擎（CSS 支持的是应用 UI 子集，见 [08](08-css.md)）。
- 不做 3D 场景、视频解码、WebView 内嵌（留扩展点）。

## 「纯 C#」的边界

| 允许 | 不允许 |
|---|---|
| P/Invoke **操作系统自带**的库：user32、d3d11、libX11、libwayland-client、Metal.framework、libEGL 等 | 随框架分发任何自己编译的原生二进制 |
| Web 平台通过 `JSImport/JSExport` 调用浏览器 API，附带一小段 JS 胶水 | 依赖第三方原生库 |
| 着色器源码（GPU 只接受着色语言，无法用 C# 直接表达；见 [03](03-renderer.md)） | 运行时依赖原生着色器编译工具链 |

## 设计原则

- **三道接缝**：录制 API → 渲染后端 → GPU 设备，每层可独立替换（[02](02-graphics-abstraction.md)）。
- **UI 层永不接触后端**：只往自有的、可序列化的 DisplayList 里录制命令。
- **排版归框架，光栅归后端**：换后端不会导致布局变化。
- **声明式是写法，保留树是实现**：组件产出描述，协调到持久的元素树上；调试、动画、无障碍都建立在元素树上。
- **沿用 Web 的词汇**：CSS 做样式、ARIA 做语义、Pointer Events 做输入 —— 不重新发明开发者和 AI 已经熟悉的东西。
- **诊断即数据**：规则匹配、脏区原因、帧阶段耗时等在核心里以结构化形式存在。
- **源生成器替代反射**；**单线程可运行**（WASM）。
- **基线优先**：功能先在 netstandard2.0 上成立，新运行时只做加速；公共 API 不用依赖运行时的语言特性。

## 文档索引

| 文档 | 内容 |
|---|---|
| [01-architecture.md](01-architecture.md) | 分层、程序集划分、依赖规则、帧管线、线程模型 |
| [02-graphics-abstraction.md](02-graphics-abstraction.md) | 绘制抽象、DisplayList、后端接口、将来如何接入其他引擎 |
| [03-renderer.md](03-renderer.md) | 自研渲染器：CPU 光栅器、GPU 管线、RHI、文本栈、图片 |
| [04-platform.md](04-platform.md) | 平台抽象层：窗口、输入、IME、各平台实现方式 |
| [05-ui-core.md](05-ui-core.md) | 声明式组件、协调器、元素树、事件、动画 |
| [06-devtools.md](06-devtools.md) | F12 调试机制：探针、JSON-RPC 协议、传输、前端 |
| [07-roadmap.md](07-roadmap.md) | 里程碑、风险 |
| [08-css.md](08-css.md) | CSS 引擎：级联、支持范围、布局、与浏览器的差异 |
| [09-touch-gestures.md](09-touch-gestures.md) | 触摸输入、手势竞技场、滚动物理 |
| [10-accessibility.md](10-accessibility.md) | 语义树、ARIA 模型、各平台无障碍桥 |
| [11-compatibility.md](11-compatibility.md) | 运行时兼容：TFM、语言与 API 约束、互操作、性能分级 |
| [12-component-api.md](12-component-api.md) | 组件、Node、State、协调器的接口级草案 |
| [13-displaylist.md](13-displaylist.md) | DisplayList 的语义、命令表与二进制编码 |
| [14-awaui-format.md](14-awaui-format.md) | `.awaui` 单文件组件：语法、独立的标记编译器、工具支持 |
| [15-repo-structure.md](15-repo-structure.md) | 代码仓库结构（参考 Avalonia）、命名约定、包划分 |
| [16-css-properties.md](16-css-properties.md) | CSS 属性清单：类型、继承、动画、影响阶段、分期 |
| [17-awaui-grammar.md](17-awaui-grammar.md) | `.awaui` 正式文法、C# 边界规则、代码生成、诊断 |
| [18-scene-compositor.md](18-scene-compositor.md) | 层树、SceneFrame、线程交接、合成器、瓦片、帧调度 |
| [19-text-api.md](19-text-api.md) | 字体、整形、段落布局、命中与选区几何、整形器插件接口 |
| [20-devtools-protocol.md](20-devtools-protocol.md) | DevTools 协议的消息格式与逐方法定义 |
| [21-gesture-scroll-api.md](21-gesture-scroll-api.md) | 手势识别器、竞技场、滚动控制与滚动物理的接口 |
| [22-controls.md](22-controls.md) | 标准控件库：通用约定、设计令牌、基础设施（控件清单的早期草案） |
| [23-component-library.md](23-component-library.md) | 组件库总体设计：行为原语、Avalonia 风格基础控件与 Fluent 主题、Tailwind 兼容工具类、shadcn 组件库、注册表与扩展 |
| [24-mobile-wayland-backends.md](24-mobile-wayland-backends.md) | Android、iOS、Linux Wayland 后端的接口级实施方案：TFM、宿主、表面、输入/IME、服务、无障碍、打包与 CI |

## 决策记录

状态：✅ 已定 / ❓ 待讨论

| # | 决策 | 结论 | 状态 |
|---|---|---|---|
| D2 | UI 范式 | 声明式组件（UI = f(state)），协调到保留的元素树 | ✅ |
| D5 | 样式 | 直接支持标准 CSS（应用 UI 子集） | ✅ |
| D4 | 布局 | CSS 布局：Flexbox + Grid + 简化 Block + 定位 | ✅ |
| D6 | 绘制中间表示 | 自有二进制 DisplayList，可序列化、可回放 | ✅ |
| D7 | 文本整形归属 | 框架统一整形排版，后端只画 GlyphRun | ✅ |
| D8 | GPU 渲染策略 | UI 图元走 SDF 快速路径；任意路径与字形走 CPU 掩码 + GPU 合成 | ✅ |
| D11 | DevTools 协议 | 自有 JSON-RPC 协议 | ✅ |
| D12 | DevTools 前端 | 用 AwAUI 自己写，内嵌 / 独立窗口 / 远程 | ✅ |
| D14 | 后端范围 | 只做自研实现（软件 + GPU）；抽象保留，不做第三方适配 | ✅ |
| D15 | 文字范围 | 全部书写系统的整形，纯 C# 实现 | ✅ |
| D16 | 无障碍与触摸 | 纳入核心设计 | ✅ |
| D13 | Apple 互操作 | macOS 用裸 `objc_msgSend`（纯 C#，无 workload）；iOS 用 net-ios | ✅ |
| D1 | 运行时支持 | 编译目标 `netstandard2.0; net8.0; net10.0`，覆盖 .NET Framework 与 .NET 8–11（[11](11-compatibility.md)） | ✅ |
| D19 | .NET Framework 最低版本 | 4.7.2 | ✅ |
| D20 | DevTools 的 JSON 与 WebSocket | 自带轻量实现，核心零第三方依赖 | ✅ |
| D3 | 编写方式 | 默认 `.awaui`（类 Razor，独立编译器，[14](14-awaui-format.md)）；底层为对象初始化器式 C# API（[12](12-component-api.md)） | ✅ |
| D9 | 首批 GPU 后端 | Metal、D3D11、GLES3/WebGL2 | ✅ |
| D10 | 着色器来源 | 先手写三种语言，后续做「C# 子集 → 着色语言」转译器 | ✅ |
| D17 | CSS 与浏览器的差异 | 默认 border-box、无外边距折叠、块级元素不与文本混排（[08](08-css.md)） | ✅ |
| D18 | 元素命名 | 标记中用 `div / text / span / img / input / textarea / canvas / svg`，对应 C# 的 `Div / Text / ...` | ✅ |
| D21 | `.awaui` 编辑器支持 | 先做语法高亮 + 精确诊断，语言服务器后置 | ✅ |
| D22 | 仓库结构 | 参考 Avalonia 的组织方式（[15](15-repo-structure.md)） | ✅ |
| D23 | 扩展点形态 | 抽象基类 + 可选能力接口（因 netstandard2.0 无接口默认实现） | ✅ |
| D24 | 布局引擎归属 | 独立程序集 `AwAUI.Layout`，只依赖 Foundation，输入为自有的 `LayoutStyle`，UI 层负责从 `ComputedStyle` 映射（[08](08-css.md)） | ✅ |
| D25 | 运行时命名空间 | 组件运行时类型在 `AwAUI.UI`，避免 `Text` 元素与 `AwAUI.Text` 命名空间冲突；字体抽象在 `AwAUI.Graphics.Text`（[12](12-component-api.md)、[19](19-text-api.md)） | ✅ |
| D26 | 组合根与窗口托管 | `AwAUI.Desktop` 的 `AwAppBuilder.Create()` 提供托管组合根（`UsePlatformDetect` / `UseRenderer` / `UseDefaultTextEngine` / `UseStyleSheet` / `UseShutdownMode` / `Run<TApp>`）；平台在构建期按 RID 或宿主 OS 选定，应用不引用平台程序集。应用对象 `Application` 只持有全局样式与 `ApplicationLifetime`；`DesktopApplicationLifetime` 管理 `Windows / MainWindow / ShutdownMode`，`Window` 在 `Show` 时登记并支持自绘边框（`WindowDecorations.None` + `IWindowChrome`）。底层 API 仍是 `AwApp.Run / CreateHost`（`UIHost`），UI 不引用 Render；DevTools 按窗口 `EnableDevTools` 开启。GPU 自动选择仍是后续项（[01](01-architecture.md)、[04](04-platform.md)、[15](15-repo-structure.md)） | ✅ |
| D27 | DevTools 接缝 | `AwAUI.Diagnostics` 定义抽象类 `InspectableTree / StyleInspector / OverlayHost`，由 `AwAUI.UI` 实现；Diagnostics 只依赖 Foundation（[06](06-devtools.md)） | ✅ |
| D28 | `.awaui` 胶水与样式 | `.awaui` 类的 Props 胶水只由标记生成器产出，C# 胶水生成器跳过 `*.awaui.cs`；样式支持内联 `<style>`、`X.awaui.css`（唯一/作用域，含 `::deep`）、`X.awaui.global.css`，项目级共享样式由 `<AwAUIGlobalStylesheet>` 合并为 `AwAUIGlobalStyles` 并在模块初始化时登记；选择器由编译器改写、运行时在组件类型首次挂载时注册（[14](14-awaui-format.md)、[17](17-awaui-grammar.md)、[12](12-component-api.md)） | ✅ |
| D29 | RHI 形态 | 不提供 `CreatePipeline / CreateBuffer`：管线为枚举 + 混合状态，实例数据布局（`GpuQuadInstance` 等）作为契约定义在 RHI 中，缓冲只来自每帧的上传环；RHI 与 GPU 后端归入 `AwAUI.Render`，各图形 API 实现为独立程序集（[03](03-renderer.md)） | ✅ |
| D30 | Web 平台 TFM | `AwAUI.Browser` 只编译 `net10.0-browser`；其测试项目直接编译库的 `Translation/` 源码（[11](11-compatibility.md)） | ✅ |
| D31 | SVG 解析 | `AwAUI.Svg` 自写 XML 子集解析器而不用 System.Xml（不展开实体，避免 XXE / 实体膨胀，减小 AOT/WASM 体积）；只依赖 Foundation、Graphics（[07](07-roadmap.md)） | ✅ |
| D32 | UI 引用图片与 SVG | `AwAUI.UI` 直接引用 `AwAUI.Imaging` 与 `AwAUI.Svg`，`Image` / `Svg` 元素与 `<img src>` 开箱即用，组合根无需注册加载器（两者都是纯 C#，只依赖 Foundation、Graphics）。代价：`ImageDecoder` 运行时按文件头选编解码器，裁剪无法去掉未用的编解码器；特殊来源（网络、加密资源）由应用派生 `ImageSource`（[01](01-architecture.md)、[05](05-ui-core.md)） | ✅ |
| D33 | DevTools 面板 | 独立程序集 `AwAUI.DevTools`，用 `AwAUI.UI` 写成，在第二个窗口里打开；对被检查的应用**只**经 Diagnostics 的协议客户端（`DevToolsClient`，进程内或 WebSocket）读写，同一套界面可检查本进程与远程应用；不引用 Render / Imaging / Svg，后端与文本引擎由组合根传入；面板自己从不登记为调试目标（[06](06-devtools.md)） | ✅ |
| D34 | 平台文本输入抽象 | 平台实现抽象类 `TextInputMethod`（`SetClient` 不可重写，平台实现 `OnClientChanged`，其余为带默认实现的虚成员）与全异步的抽象类 `Clipboard`；框架实现接口 `ITextInputClient`（与 `IWindowEvents` 同类，登记在架构测试的允许列表），文档模型含预编辑文本，范围类型 `TextInputRange`；有客户端时一切文本经客户端，没有时经 `OnTextInput`（[04](04-platform.md)） | ✅ |
| D35 | 替换元素的呈现属性 | `object-fit`、`object-position`、`image-rendering` 只来自 CSS（第 1 期属性），删除 `Image.Fit / Position / Rendering` 等节点属性与 UI 类型，与 HTML 一致；单个元素用内联样式（[05](05-ui-core.md)、[16](16-css-properties.md)） | ✅ |
| D36 | 软件光栅与裁剪无关 | 软件光栅器在裁剪区内的输出只取决于根渲染目标，不取决于裁剪矩形（光栅器坐标系、渐变列、阴影网格都按目标绝对坐标对齐），局部光栅与整帧光栅逐字节一致、测试不留容差；代价是任意路径每行从形状左缘累积。GPU 后端不作此保证（[03](03-renderer.md)） | ✅ |
| D37 | 丢帧契约 | `RenderTarget.LastFramePresented`（虚属性，默认 true）报告交换链目标取不到可绘制纹理而丢掉的帧；不是错误、不经 `Degraded`；宿主保留损伤并在下一个节拍整帧重画，不在帧内循环重试（[03](03-renderer.md)、[05](05-ui-core.md)） | ✅ |
| D38 | 组件库分层 | 三层且行为只写一次：无样式原语 `AwAUI.Controls.Primitives`（参照 Radix / Floating UI）→ Avalonia 风格基础控件 `AwAUI.Controls` 与 shadcn 风格 `AwAUI.Shadcn` 两套成品，二者共用原语；Shadcn 是官方推荐的应用组件库（[23](23-component-library.md)） | ✅ |
| D39 | 基础控件自带 Fluent 主题 | `AwAUI.Controls` 对照 Avalonia 控件集（`ContentControl / ItemsControl / SelectingItemsControl` 模型、`TemplatedControl` 的可替换模板、`ICommand`），外观由独立的纯 CSS 主题 `AwAUI.Themes.Fluent` 提供（明 / 暗 / 高对比、强调色、紧凑密度）；不引入依赖属性系统（[23](23-component-library.md)） | ✅ |
| D40 | 两层 API 各用自己的习惯 | Controls 用 .NET / Avalonia 命名与属性式 API（`Content`、`ItemsSource`、`SelectedItem`、`IsChecked`、`IsOpen`）；Shadcn 用 shadcn / Radix 的组合式部件与命名（`DialogTrigger / DialogContent`、`Variant`、`Size`、`AsChild`、`Open + OnOpenChange`）（[23](23-component-library.md)） | ✅ |
| D41 | Tailwind 兼容工具类 | 新程序集 `AwAUI.Tailwind` 以 Tailwind v4 语义实现工具类子集（主题变量、变体、任意值、`@theme / @utility / @custom-variant / @apply`），构建期按文本扫描 `.awaui` 与 `.cs` 候选类名并预编译样式表，运行时补全可选（Debug 默认开）；不支持的类名给诊断；附 `Cn`（含 tailwind-merge 冲突消解）与 `Cva`（[23](23-component-library.md)） | ✅ |
| D42 | 分发与扩展 | NuGet 包与 shadcn 式源码复制并存：`awaui init / add / diff / build` + 参照 shadcn registry schema 的注册表（条目类型 ui / block / lib / theme / style，可跨注册表依赖）；扩展组件库用原语写行为、用 Shadcn 组件与工具类写外观，同一份源码既发包也发注册表条目（[23](23-component-library.md)） | ✅ |
| D43 | 视口滚动与滚动容器 | 根元素即视口滚动容器：根上的 `visible` 按 `auto`（hidden / clip 只能程序滚动），`position: fixed` 不随任何滚动移动、不计入根的可滚动溢出；其他元素按 CSS overflow 规则判定 —— 两轴都在 { visible, clip } 中时不是滚动容器，另一轴可滚动时 clip 按 hidden；内容尺寸取布局引擎的可滚动溢出（含末端外边距与内边距）（[21](21-gesture-scroll-api.md)、[05](05-ui-core.md)） | ✅ |
| D44 | 滚动偏移的施加 | 偏移对齐设备像素，在绝对几何遍历中施加（常规流随父元素、absolute 随包含块、fixed 不动），命中测试与元素矩形因此自动包含滚动；只滚动的帧不重新布局：只遍历该容器的子树、平移墨迹、重录容器与新露出的内容、损伤为容器视口（暂不搬移已有像素）；已施加的偏移与度量属于元素的几何状态，换控制器时由新控制器接过（[21](21-gesture-scroll-api.md)、[18](18-scene-compositor.md)） | ✅ |
| D45 | 滚动物理的线程 | 暂在 UI 线程上按帧时钟推进（`UIRoot.Clock` 为 null 时每帧 1/60 s，不读墙上时间）；`ScrollPhysics` 与 `Simulation` 保持纯函数、确定性，将来可原样移交合成器（[21](21-gesture-scroll-api.md)「与合成器的分工」仍是目标形态） | ✅ |
| D46 | 滚动条 | UI 自绘，经典（常驻、可拖动、点击轨道翻页）与覆盖（滚动或悬停时出现、淡出）两种样式按平台偏好（`WindowImpl.PreferredScrollbarStyle`），触摸总是覆盖式；v1 经典样式不占布局空间；画在容器的 0 / auto 定位后代之后、z-index 为正的后代之下（[21](21-gesture-scroll-api.md)） | ✅ |
| D47 | 默认整形器 | `OpenTypeShaper` 按 HarfBuzz 的 hb-ot 模型实现（GSUB / GPOS / GDEF、阿拉伯联接、希伯来、标记重排），是 `DefaultTextEngine` 的默认；`BasicTextShaper` 保留为测试引擎的默认与非 OpenType 字型的回退。保留逐词整形缓存：跨词字距在词边界用 `TextShaper.GetBoundaryKerning` 补上（LTR），阿拉伯类文字把至多 5 个码点的联接上下文交给整形器、邻字的联接类进入缓存键；`ScriptShaper` 插件接口暂不公开（[19](19-text-api.md)） | ✅ |
| D48 | Grid 对不可信输入的上限 | 线号钳制到 ±10000、显式轨道至多 9999 条、`repeat()` 次数至多 10000、至多记录 131072 个线名；自动放置的占用表内存与跨度无关；起止相同的项目合并分配。最坏开销 O(项目数 × 轨道数)（[08](08-css.md)） | ✅ |
| D49 | 逻辑属性 | 在解析时作为别名映射到左到右横排下的物理属性（与该情形下的级联完全一致）；代价是逻辑属性名按简写序列化（计算值文本为空），rtl 与竖排要等 `direction` / `writing-mode`（[16](16-css-properties.md)） | ✅ |
| D50 | 原语的纯逻辑与 UI 接入分离 | `AwAUI.Controls.Primitives` 的定位、选择、导航、关闭层栈、焦点作用域、Presence、虚拟化先实现为不访问元素树的纯计算：元素是泛型参数（UI 接入时为 `Element`），几何与时间由宿主测量或传入；Popper 以 Floating UI 1.7 为语义基准并做差分测试；浮层 CSS 变量用 `--aw-popper-*`；Virtualizer 的偏移用 double（[23](23-component-library.md)） | ✅ |
| D51 | Tailwind 的基准与依赖 | `AwAUI.Tailwind` 以 Tailwind v4.3.5 逐函数移植、输出逐字节一致，不依赖任何 AwAUI 程序集（只输出 CSS 文本，源生成器以源码链接方式编入）；运行时补全基于逐条规则与排序下标，不直接改写构建期合并后的样式表（[23](23-component-library.md)） | ✅ |
| D52 | 竖排与图标镜像 | 1.0 不做 `writing-mode` 竖排：逻辑属性的映射、行内轴换算与滚动方向一律按 `horizontal-tb`。图标镜像保持显式（组件选择镜像后的 SVG 或换图）：CSS transform 是几何变换，引擎目前也不实现 transform，因此没有自动镜像规则；竖排进入范围时再把使用的书写模式接入逻辑映射（[16](16-css-properties.md)、[21](21-gesture-scroll-api.md)、[23](23-component-library.md)） | ✅ |
| D53 | 移动端与 Wayland 后端 | Android 用 `net10.0-android36.0` + SurfaceView/EGL，iOS 用 `net10.0-ios18.0` + UIView/CAMetalLayer，Wayland 用 `libwayland-client` + 协议生成器 + wl_shm/EGL；桌面 Linux 运行时在 Wayland 与 X11 间回退。三者都只追加虚成员与可选接口（`WindowImpl.Accessibility`、`WindowOptions.AppId`、`RawPointerEvent.IsPredicted`、`IAndroidWindowSurface`、`IWlEglSurface`），不破坏现有后端（[24](24-mobile-wayland-backends.md)） | ✅ |

## 当前状态

决策表中的事项已全部确认，各模块均已细化到接口级。**M1 已验收**（`.awaui` 与纯 C# 两版计数器逐步一致；F12 拾取 + 协议实时改 CSS；面板在第七批以独立窗口形态补上），与之并行完成了 Metal GPU 后端、Web 平台（软件呈现）、SVG 与 GIF/WebP/BMP/ICO 解码；第六批把 GPU 后端合入 `AwAUI.Render`、macOS 窗口可用 Metal 呈现，UI 有了 `Image` / `Svg` 元素与增量绘制；第七批补上 `DrawImage`（`Image` 画出位图像素）、与裁剪无关的软件光栅、丢帧上报、五个平台的输入法 / 逻辑键 / 剪贴板与 `Input` 的完整单行编辑，以及独立窗口的 DevTools 面板（`AwAUI.DevTools`）；第八批补上滚动系统（滚轮、触控板、键盘、触摸拖动与惯性、自绘滚动条，在 UI 线程上）、CSS Grid 与 `order`、CFF / CFF2 轮廓与 OpenType 整形（`OpenTypeShaper` 成为默认整形器），以及图片失败占位、`Platform.UnitTests` 等第七批遗留，详见 [07](07-roadmap.md)。设计阶段剩余的工作是在实现中验证并回头修订，主要的不确定点：

1. ~~CPU 光栅器在 WASM 上的实际性能~~ —— 已测：约为原生的 19 倍，Web 上的常规路径必须是 WebGL2，软件后端只做兜底（见 [07](07-roadmap.md)）。.NET Framework 上的性能仍待在 Windows 上实测。
2. ~~macOS 裸 ObjC 互操作在 NativeAOT 下的可行性~~ —— 已验证可行（见 [07](07-roadmap.md) M0）。
3. 声明式重建的分配开销是否需要对 Node 做池化或结构体化。M1 初步数据：10 000 个带 Key 子组件、Props 全相等的父组件重建 2.7–8 ms（非正式计时），尚未做分配基准。
4. 滚动跟手性在「输入只到主线程」约束下的实际表现（见 [18](18-scene-compositor.md)）。第八批的滚动全在 UI 线程上：只滚动的帧不重新布局（2000 行列表管线约 4 ms），但软件后端每帧整块重画视口（2x 下约 36 ms），合成器与像素搬移仍未做，手机上的手感也还没有实测。

建议阅读顺序：本文 → 01 → 05 → 14 → 08 → 02 → 06，其余按需查阅。
