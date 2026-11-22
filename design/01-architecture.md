# 01 整体架构

## 分层

```
┌──────────────────────────────────────────────────────────────────┐
│ 应用层          组件 (.awaui / .cs) + 样式 (.css)                   │
├──────────────────────────────────────────────────────────────────┤
│ 控件库          AwAUI.Controls   (Button, Select, Dialog, ...)     │
├──────────────────────────────────────────────────────────────────┤
│ UI 核心         AwAUI.UI                                           │
│                 组件 · State · 协调器 · 元素树 · 事件 · 手势          │
│                 文本编辑 · 语义树(a11y)                              │
│ CSS 引擎        AwAUI.Css                                          │
│                 解析 · 选择器 · 级联 · 计算值 · 动画                 │
│ 布局引擎        AwAUI.Layout                                       │
│                 Flex · Block · 定位 · 固有尺寸（Grid 后续）          │
├───────────────────────────────┬──────────────────────────────────┤
│ 场景与合成  AwAUI.Scene        │ 诊断  AwAUI.Diagnostics           │
│ DisplayList · LayerTree       │ 探针 · 事件流 · DevTools Agent     │
│ 脏区 · 帧调度                  │                                  │
├───────────────────────────────┴──────────────────────────────────┤
│ 绘制抽象        AwAUI.Graphics                                      │
│                 DrawingContext · Path · Paint · Image · GlyphRun   │
│                 RenderBackend · TextEngine · ImageCodec         │
├──────────────────────┬───────────────────────┬───────────────────┤
│ 自研渲染器            │ 文本栈                 │ (预留)             │
│ AwAUI.Render         │ AwAUI.Text            │ 其他绘制引擎的适配  │
│  ├ Raster (CPU)      │ 字体解析·整形·断行·Bidi │ 现阶段不实现        │
│  ├ Gpu (合成/批处理)  │                       │                   │
│  └ Rhi (设备抽象)     │                       │                   │
│     Metal/D3D11/GLES │                       │                   │
├──────────────────────┴───────────────────────┴───────────────────┤
│ 平台抽象        AwAUI.Platform  (接口)                              │
│ 实现            .Win32 · .MacOS · .X11 · .Wayland                  │
│                 .Android · .iOS · .Browser · .Headless             │
├──────────────────────────────────────────────────────────────────┤
│ 基础            AwAUI.Foundation                                    │
│                 几何/颜色/矩阵 · 池化 · 调度器抽象 · 日志             │
└──────────────────────────────────────────────────────────────────┘
```

## 程序集与依赖规则

| 程序集 | TFM | 依赖 | 说明 |
|---|---|---|---|
| `AwAUI.Foundation` | 通用 | — | 值类型几何、`Color`、`Matrix3x2`、对象池、`IDispatcher` |
| `AwAUI.Graphics` | 通用 | Foundation | **纯抽象 + DisplayList 数据格式**，无任何实现 |
| `AwAUI.Imaging` | 通用 | Graphics | 纯 C# 图片解码（已有 PNG、JPEG、GIF、WebP、BMP、ICO/CUR，含动画）；与渲染器分开，可整体裁剪 |
| `AwAUI.Unicode` | 通用 | — | Unicode 18 字符属性表（构建期由 UCD 生成）、UAX #29 字素/单词/句子分段、UAX #14 断行、UAX #9 双向；Text、Css、文本编辑共用 |
| `AwAUI.Text` | 通用 | Graphics, Unicode | 字体解析、整形、段落排版（默认 `TextEngine` 实现） |
| `AwAUI.Render` | 通用 | Graphics | 自研渲染器：CPU 光栅器与软件后端（`Raster/`）、GPU 后端（`Gpu/`，命名空间 `AwAUI.Render.Gpu`）、RHI 抽象（`Rhi/`，命名空间 `AwAUI.Render.Rhi`）。第六批已合入，原来的临时项目 `AwAUI.Render.Gpu`、`AwAUI.Rhi` 已删除；两个后端共用 `Raster/` 下唯一的一份光栅代码 |
| `AwAUI.Svg` | 通用 | Foundation, Graphics | SVG 解析与渲染（产出 DisplayList 或直接录制到 `DrawingContext`），不依赖 System.Xml |
| `AwAUI.Rhi.*` | 通用 | Foundation, Graphics, Render | Metal / D3D11 / GLES / Vulkan / WebGPU 各一个，位于 `src/Rhi/`，各自是独立程序集；已有 `AwAUI.Rhi.Metal`（含组合根助手 `MetalRenderer`，见下文） |
| `AwAUI.Platform` | 通用 | Foundation | 窗口/输入/IME/剪贴板等接口 |
| `AwAUI.Win32 等平台包` | 通用 / 视平台 | Platform | 桌面平台为通用 TFM；Android、iOS 仅现代 .NET；`AwAUI.Browser` 只编译 `net10.0-browser`（见 [11](11-compatibility.md)） |
| `AwAUI.Scene` | 通用 | Graphics | 层树、帧调度、脏区、合成器 |
| `AwAUI.Css` | 通用 | Foundation（可选 Unicode） | CSS 解析、选择器、级联、计算值、动画插值（不依赖元素树的具体类型，通过 `StyleElement` 抽象访问节点） |
| `AwAUI.Layout` | 通用 | Foundation | 布局算法（Flex / Block / 定位，Grid 后续）。输入是自己的强类型 `LayoutStyle`，不引用 Css |
| `AwAUI.UI` | 通用 | Css, Layout, Text, Scene, Platform, Diagnostics | 组件、协调器、元素树、事件与手势、帧管线（样式 → 布局 → 绘制 → 命中测试）与窗口宿主；负责把 `ComputedStyle` 映射为 `LayoutStyle`，并实现 Diagnostics 定义的 DevTools 检查接缝。实际引用 Foundation、Graphics、Platform、Css、Layout、Text、Diagnostics，第六批起另引用 Imaging、Svg（决策 D32：`Image` / `Svg` 元素开箱即用；两者纯 C#、只依赖 Foundation 与 Graphics；代价是裁剪去不掉未用的编解码器）。Scene 尚不存在；不引用 Render，也不直接引用 Unicode；运行时类型在命名空间 `AwAUI.UI` |
| `AwAUI.Controls` | 通用 | UI（另允许 Svg） | 标准控件。`ProgressRing` 用 SVG 描边画圆环（BoxPainter 不画 `conic-gradient`），因此允许引用 `AwAUI.Svg`；其余控件只建立在 UI 及其公开下层之上 |
| `AwAUI.Diagnostics` | 通用 | Foundation | 探针 + Agent + 协议；Tree / Css / Overlay 的数据经抽象类 `InspectableTree / StyleInspector / OverlayHost` 由 UI 层提供，因此只依赖 Foundation |
| `AwAUI.DevTools` | 通用 | Controls | F12 前端（用 AwAUI 写）。第七批建立：实际引用 Foundation、Graphics、Platform、Css、Text、Diagnostics、UI（允许列表另含 Layout），不引用 Render、Imaging、Svg（后端与文本引擎由组合根传入）；对被检查的应用只经 Diagnostics 的 `DevToolsClient` 通信（决策 D33，见 [06](06-devtools.md)）。Controls 尚不存在 |
| `AwAUI.Generators` | netstandard2.0 | — | Roslyn 源生成器与分析器（组件胶水、CSS 预编译、诊断） |
| `AwAUI.Markup.Compiler` | netstandard2.0 | — | `.awaui` 编译器本体，不依赖 Roslyn 与框架运行时（见 [14](14-awaui-format.md)） |
| `AwAUI.Markup.Generator` | netstandard2.0 | Markup.Compiler（以源码链接） | 把 `.awaui` 接入构建的增量源生成器（Roslyn 4.14）；分析器不能携带普通依赖，所以链接编译器与 `src/Shared/CodeGen` 的源码而不引用 DLL。NuGet 包名 `AwAUI.Markup`，核心不依赖它 |

「通用」= `netstandard2.0; net8.0; net10.0`，覆盖 .NET Framework 4.7.2+ 与 .NET 8–11，详见 [11-compatibility.md](11-compatibility.md)。

硬性规则（用架构测试在 CI 强制）：

1. `AwAUI.UI`、`AwAUI.Css` 及以上**禁止**引用 `AwAUI.Render`，只能看到 `AwAUI.Graphics` 的抽象。
2. `AwAUI.Graphics` 不引用 `Platform`；后端与平台的衔接只通过 `NativeSurface`（句柄的不透明包装）。
3. 核心程序集的 net8.0+ 目标全部 `IsAotCompatible=true`、`IsTrimmable=true`，CI 开启 trim/AOT 警告即错误；netstandard2.0 目标是功能基线，任何功能必须先在其上成立。
4. 公共 API 不使用依赖运行时的语言特性（接口默认实现、静态抽象成员、`ref` 字段等）；可能演进的扩展点用抽象类。
5. 平台、后端、文本引擎的装配只发生在应用入口的组合根：

```csharp
// AwAUI.Desktop：应用不引用任何平台程序集
AwAppBuilder.Create()
    .UsePlatformDetect()            // 构建期选定：Windows → Win32，macOS → AppKit，Linux → X11
    .UseSoftwareRenderer()          // 或 UseRenderer(gpu) / UseDefaultTextEngine() / UseStyleSheetFile("app.css")
    .UseShutdownMode(ShutdownMode.OnMainWindowClose)
    .Run<MyApplication>();          // 应用对象；窗口在 OnFrameworkInitializationCompleted 里创建

sealed class MyApplication : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        ((DesktopApplicationLifetime)Lifetime!).MainWindow = new Window(new WindowOptions
        {
            Title = "Counter",
            Size = new Size(800, 600),
        })
        {
            Content = new Counter(),    // 根组件（Node）
        };
    }
}
```

`Application` 只保存应用级样式与生命周期；窗口集合、主窗口与退出策略在 `DesktopApplicationLifetime`；
`Window` 是一次 `Show` 后登记到生命周期的顶层宿主。三者都在 `AwAUI.UI`（不引用 Render），平台、渲染后端与
文本引擎仍由 `AwAUI.Desktop` 注入。`Application.Styles` 在每个窗口显示时先于窗口样式与组件样式进入级联。

`AwAUI.Desktop` 在构建期按 `RuntimeIdentifier`（没有 RID 时按宿主 OS）只引用目标平台的后端：
Windows 构建的依赖图里没有 macOS / X11 程序集，普通 build 与 NativeAOT 裁剪都不需要处理其他平台。
`Run<T>()`、`UseShutdownMode` 与 `UseDevTools` 已实现；GPU 自动选择（`.UseAwRenderer(o => o.PreferGpu = true)`）
仍是目标形态。底层 API 仍在 `AwAUI.UI`（`Hosting/UIHost.cs`），由应用自己创建平台、后端与文本引擎再交给它
（UI 因此不需要引用 Render）：

```csharp
// 创建窗口与宿主、加入样式表、挂载根组件，并进入平台消息循环（窗口关闭时退出）
AwApp.Run(new MacPlatform(), new WindowOptions { Title = "Counter" }, new SoftwareRenderBackend(),
          textEngine, new Counter(), StyleSheet.Parse(css));

// 需要先配置宿主时（例如开 DevTools）：CreateHost 做同样的事但不进入消息循环
var host = AwApp.CreateHost(platform, options, backend, textEngine, new Counter(), sheet);
host.EnableDevTools(new UIDevToolsOptions { ListenWebSocket = true, Port = 9230 });   // 可选，按窗口开启
platform.Run();
```

现阶段 `UseAwRenderer` 的对应物是 `AwAUI.Rhi.Metal` 的 `MetalRenderer`（第六批）：`Create(preferGpu)` 有 Metal 时返回 `MetalRenderBackend`（GPU，设备丢失或渲染出错后永久回退软件），否则返回 `SoftwareRenderBackend`；`TryCreate()` 只返回 GPU 后端或 null；环境变量 `AWAUI_RENDERER=software` 强制软件。

```csharp
using var backend = MetalRenderer.Create(preferGpu: !args.Contains("--software"));
var host = AwApp.CreateHost(new MacPlatform(), options, backend, textEngine, new Counter(), sheet);
```

- `UIHost : IWindowEvents` 把一个 `WindowImpl` 接到 `RenderPipeline`（样式 → 布局 → 文本 → 绘制 → 命中测试），只在有变化时请求帧、转发 CSS `cursor`；`Mount(Node)`、`AddStyleSheet`、`RunFrame`、`LastFrame`（各阶段耗时）。
- DevTools 按窗口显式开启；整个诊断层仍受特性开关 `AwAUI.Diagnostics.Enabled` 控制（见 [06](06-devtools.md)）。

## 帧管线

一帧由平台的 vsync / `requestAnimationFrame` / `CADisplayLink` / `Choreographer` 驱动，按固定阶段执行：

```
 ① Input      处理排队的平台事件 → 命中测试 → 路由事件
 ② Animate    推进帧时钟，动画写入属性（Animation 优先级）
 ③ Build      重建依赖已变化的组件，协调器把差异应用到元素树
 ④ Style      对脏元素重新匹配 CSS 规则、级联、计算 ComputedStyle
 ⑤ Layout     对脏子树执行 CSS 布局
 ⑥ Paint      对脏元素重新录制 DisplayList（未脏的复用缓存）
 ⑦ Commit     生成不可变的 SceneFrame（层树 + DisplayList 引用）
 ─────────────────────────── 线程边界（可选） ───────────────────────────
 ⑧ Composite  合成器动画（transform/opacity/scroll）、层剔除
 ⑨ Raster     后端把 DisplayList 渲染到层纹理 / 目标表面
 ⑩ Present    交换缓冲
```

- 每个阶段只处理对应的脏集合。每个 CSS 属性在属性清单中声明它影响哪个阶段（布局 / 绘制 / 仅合成），ComputedStyle 变化时自动打对应的脏标记。
- 没有任何脏标记且没有活动动画时不请求下一帧（空闲零功耗）。
- 每个阶段入口/出口都有诊断探针，这就是 DevTools 性能面板的数据来源。

M1 的实现是这条管线的单线程简化版：没有 Scene、Commit 与合成器，③–⑥ 由 `UIRoot` 与 `RenderPipeline` 在 UI 线程上执行，⑨ 由宿主直接调用 `RenderTarget.Render`。第六批起 ⑥ 是增量的：每个元素录成自己的局部坐标 DisplayList，只重录绘制或自身几何变了的元素及其祖先，每帧报告一个损伤矩形，软件后端只光栅化该矩形（GPU 后端仍整窗重画；细节见 [05](05-ui-core.md)）；没有变化时复用上一帧、跳过光栅。各阶段耗时记录在 `FrameTimings` / `HostFrameStatistics` 中，尚未接入诊断探针。

## 线程模型

层树、帧交接与调度的细节见 [18-scene-compositor.md](18-scene-compositor.md)。

| 模式 | 适用 | 说明 |
|---|---|---|
| 单线程 | WASM（默认）、Headless、调试 | ①–⑩ 全在 UI 线程 |
| 双线程 | 桌面、移动端 | ①–⑦ 在 UI 线程，⑧–⑩ 在渲染线程 |

关键约束：

- `SceneFrame` 是不可变快照，是两个线程之间唯一的数据交换物；DisplayList 录制完成后冻结，引用计数管理。
- 元素树只能在 UI 线程访问（`Dispatcher.VerifyAccess()`，Debug 下强制检查）。
- 合成器动画（滚动、transform、opacity）可以在渲染线程独立推进，UI 线程卡顿时滚动仍然流畅 —— 这要求层树从设计之初就把这三类属性与内容分离。
- 所有代码路径必须在单线程模式下可工作；不允许出现「必须阻塞等待另一线程」的同步点。

## 资源与生命周期

- 图片、字体、路径几何、渐变等都是「逻辑资源」（`AwAUI.Graphics` 中的不可变对象），后端按需为其创建并缓存「设备资源」（纹理、图集条目、顶点缓冲）。
- 设备丢失（D3D device lost、Android surface 销毁、WebGL context lost）是常态：后端丢弃全部设备资源后可从逻辑资源完整重建，UI 层无感知。
- 热路径零分配：DisplayList、布局、命中测试使用池化缓冲和 `Span<T>`；CI 中用分配基准守护。

### 内存规则

进程内存由四部分组成，每一部分有各自的规则。数字是 HelloAot（420×260 窗口，软件渲染，NativeAOT）在 2026-11-22 的实测值。

| 部分 | 规则 | 实测 |
|---|---|---|
| 托管堆的存活对象 | 元素、样式、布局节点等框架对象；只随界面规模增长 | 约 0.7 MB |
| 启动期的临时分配 | 不触发 GC 时会一直留在已提交的堆里，所以按「启动总分配量」计预算 | 12.6 MB → 约 5 MB |
| 文件数据（字体、图片源文件） | **不读进托管数组**：字体文件用只读内存映射，只有访问过的页驻留，且是可回收的文件页 | 映射 60 MB，驻留约 1.5 MB |
| 原生缓冲（后备位图、平台窗口） | 与窗口像素数成正比；窗口销毁时释放 | macOS 约 5 MB |

规则：

1. **按需加载，按名索引**。能从文件头或目录读到的信息（字体族名）不为此加载整个文件。系统字体索引只读每个面的 `name` 表（[19](19-text-api.md)）；曾经的实现为建索引把整个字体目录读进内存，Windows 上是 470 MB，macOS 上是 800 MB。
2. **大块只读数据走内存映射**。`FontFileData` 映射字体文件；CJK 字体 20–100 MB，排版只用到其中的几张表和用到的字形。
3. **扫描式查找用完即还**。全量 cmap 扫描逐个文件映射、检查、未命中立即解除映射，只有命中的文件留在缓存里。
4. **每帧的临时缓冲由拥有者复用**。光栅器的行缓冲、内阴影的形状覆盖率缓冲挂在 `RasterContext` / `ShadowRasterizer` 上；超过 1 MB 的缓冲用完不保留，避免一次大绘制让进程长期多占内存。外阴影挖掉形状时逐行直接乘进蒙版，不再分配第二份与工作区等大的数组。
5. **昂贵且稳定的结果缓存，缓存有上限**。盒阴影的模糊蒙版按（形状、偏移、模糊、扩展、内外、变换、窗口）缓存在 `ShadowRasterizer` 里：界面的大多数重绘里卡片没有动，直接复用；蒙版与裁剪无关，局部重画的子区域也命中，结果与重新计算逐位相同。总量上限约 4 MB（`MaxCachedFloats`）、至多 16 项，按最久未用淘汰，超过上限的单个蒙版不缓存。
6. **每次解析都会新建的对象不带大缓存**。`CssTokenizer` 的名字缓存按线程共享——解析一条声明就新建一个分词器，自带 8 KB 缓存会在启动期产生约 2 MB 垃圾。
7. **空闲时零分配、零重绘**（见 [03](03-renderer.md)「性能预算」）。

实测结果（同一个 HelloAot）：

| 平台 | 修改前 | 修改后 |
|---|---:|---:|
| macOS arm64，物理占用（`footprint`） | 832 MB | 31–36 MB（差别来自 AppKit 自身的 malloc，11–15 MB） |
| Windows x64，工作集 / 私有内存 | 481 MB / 476 MB | 18 MB / 5.4 MB（无交互桌面的会话中测得） |

macOS 剩余部分：AppKit 自身的 malloc 11–15 MB，窗口后备缓冲（IOSurface 与 CG 位图）约 5 MB，启动期分配留下的已提交堆约 5 MB（其中卡片阴影的蒙版 2.4 MB 留在缓存里）。
启动完成后做一次压缩 GC 可以把已提交的堆再还给系统约 4 MB，目前不做（多一次停顿，收益小）。

## 测试策略（影响架构）

- `AwAUI.Headless` + CPU 光栅器 = 无窗口环境可跑完整 UI，输出位图 → **黄金图像测试**。
- DisplayList 可序列化 → 可做**录制快照测试**（不依赖光栅化的像素差异）。
- 同一 DisplayList 分别喂给软件后端和 GPU 后端做**差异对照**；CSS 行为用 Web Platform Tests 子集校验，文本整形用公开的整形测试集校验。
- DevTools 协议同时也是 UI 自动化测试的驱动接口（查元素、读属性、注入输入）。
