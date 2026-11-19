# 15 代码仓库结构

参考 Avalonia 的仓库组织方式：`src / samples / tests / build` 四大目录、平台实现按操作系统分文件夹、共享的 MSBuild 属性集中在 `build/`、工具与生成器放 `src/tools`、渲染测试带黄金图像目录、公共 API 基线入库。在此基础上按 AwAUI 的分层做调整。

## 目录

```
AwAUI/
├─ design/                          设计文档（本目录）
├─ src/
│  ├─ AwAUI.Foundation/             几何、颜色、池化、调度器抽象、日志
│  ├─ AwAUI.Graphics/               绘制抽象：DrawingContext、DisplayList、Path、Paint、后端基类
│  ├─ AwAUI.Imaging/                图片解码（PNG、JPEG、GIF、WebP、BMP、ICO）
│  ├─ AwAUI.Svg/                    SVG 解析与渲染（只依赖 Foundation、Graphics）
│  ├─ AwAUI.Unicode/                Unicode 数据表（UCD 生成）、文本分段、断行、双向
│  ├─ AwAUI.Text/                   字体解析、整形、段落排版
│  │   └─ Shapers/                  各文字整形器（Arabic、Indic、USE ...）
│  ├─ AwAUI.Css/                    CSS 解析、选择器、级联、计算值、动画
│  ├─ AwAUI.Layout/                 布局算法（Flex / Block / 定位），输入为 LayoutStyle
│  ├─ AwAUI.Scene/                  层树、帧调度、脏区、合成器
│  ├─ AwAUI.UI/                     组件、State、协调器、元素树、事件、手势、语义树
│  ├─ AwAUI.Controls/               Avalonia 风格基础控件（Button、TextBox、ComboBox、ListBox、TreeView ...），含 Primitives/ 行为原语（见 23）
│  ├─ AwAUI.Controls.DataGrid/      DataGrid（与 Avalonia 相同，独立包）
│  ├─ AwAUI.Themes.Fluent/          基础控件的 Fluent 主题（纯 CSS 与令牌）
│  ├─ AwAUI.Tailwind/               Tailwind v4 兼容工具类引擎（构建期与运行时共用）、Cn、Cva
│  ├─ AwAUI.Shadcn/                 shadcn/ui 组件（.awaui + Tailwind 类），含官方注册表 registry/
│  ├─ AwAUI.Shadcn.Extensions/      基于 shadcn 的扩展组件（高级 DataTable、MultiSelect、TagInput ...）
│  ├─ AwAUI.Icons.Lucide/           Lucide 图标（由 SVG 生成）
│  ├─ AwAUI.Diagnostics/            探针、DevTools Agent、JSON-RPC 协议、传输
│  ├─ AwAUI.DevTools/               F12 前端（用 AwAUI 自己写）；第七批建立：独立窗口面板，只经 Diagnostics 的协议客户端访问应用
│  ├─ AwAUI.Render/                 自研渲染器
│  │   ├─ Raster/                   CPU 光栅器、软件后端
│  │   ├─ Gpu/                      GPU 后端：批处理、图集、层缓存（命名空间 AwAUI.Render.Gpu、.Atlas、.Frame）
│  │   ├─ Rhi/                      GPU 设备抽象基类（命名空间 AwAUI.Render.Rhi；第六批由临时项目合入，AwAUI.Rhi 这个名字已不存在）
│  │   └─ Shaders/                  着色器源（HLSL / MSL / GLSL）
│  ├─ Rhi/                          各图形 API 的实现，独立程序集，引用 AwAUI.Render
│  │   ├─ AwAUI.Rhi.D3D11/
│  │   ├─ AwAUI.Rhi.Metal/          已实现（命名空间 AwAUI.Rhi.Metal；含 MetalRenderer 组合根助手）
│  │   └─ AwAUI.Rhi.Gles/           GLES3 与 WebGL2 共用渲染逻辑
│  ├─ AwAUI.Platform/               平台抽象基类（窗口、输入、IME、剪贴板 ...）
│  ├─ Windows/
│  │   └─ AwAUI.Win32/
│  ├─ MacOS/
│  │   └─ AwAUI.MacOS/              裸 ObjC 运行时互操作
│  ├─ Linux/
│  │   ├─ AwAUI.X11/
│  │   └─ AwAUI.Wayland/
│  ├─ Android/
│  │   └─ AwAUI.Android/
│  ├─ iOS/
│  │   └─ AwAUI.iOS/
│  ├─ Browser/
│  │   └─ AwAUI.Browser/            含 JS 胶水（wwwroot/）
│  ├─ Headless/
│  │   ├─ AwAUI.Headless/           无窗口平台，测试与截图
│  │   └─ AwAUI.Headless.XUnit/     测试框架集成
│  ├─ Markup/
│  │   ├─ AwAUI.Markup.Compiler/    .awaui 编译器本体
│  │   ├─ AwAUI.Markup.Generator/   接入构建的源生成器
│  │   └─ AwAUI.Markup/             NuGet 打包项目（props/targets）
│  ├─ AwAUI/                        元包：核心 + 默认渲染器 + 文本 + 控件 + Fluent 主题（Shadcn 单独引用）
│  ├─ AwAUI.Desktop/                元包：Win32 + MacOS + X11 + Wayland + 三个 RHI，提供 UsePlatformDetect()
│  ├─ Shared/                       以链接方式共享的源码
│  │   ├─ Polyfills/                netstandard2.0 所需的特性类型与 API 补丁
│  │   ├─ Interop/                  NativeCallback、库加载、blittable 辅助
│  │   └─ CodeGen/                  组件胶水生成逻辑（生成器与标记编译器共用）
│  └─ tools/
│      ├─ AwAUI.Generators/         源生成器（组件胶水、CSS 预编译、协议序列化）
│      ├─ AwAUI.Analyzers/          分析器与代码修复（AWxxxx 诊断）
│      ├─ AwAUI.Cli/                awaui 命令行（compile / format / check；组件库的 init / add / diff / build）
│      ├─ UnicodeDataGen/           从 UCD 生成 Unicode 数据表
│      ├─ CssPropertyGen/           从属性清单生成 ComputedStyle 与解析器
│      ├─ WaylandProtocolGen/       从协议 XML 生成绑定
│      └─ ShaderCheck/              校验多语言着色器接口一致
├─ samples/
│  ├─ ControlCatalog/               控件总览（共享项目，.awaui 编写）
│  ├─ ControlCatalog.Desktop/       各平台宿主头
│  ├─ ControlCatalog.NetFramework/
│  ├─ ControlCatalog.Android/
│  ├─ ControlCatalog.iOS/
│  ├─ ControlCatalog.Browser/
│  ├─ ShadcnGallery/                shadcn 组件总览与 Blocks 示例
│  ├─ Counter/  TodoApp/            入门示例（.awaui 与纯 C# 各一份）
│  ├─ RenderDemo/                   直接使用 DrawingContext 的渲染演示
│  └─ Sandbox/                      开发时随手试验
├─ tests/
│  ├─ AwAUI.Foundation.UnitTests/   每个程序集一个单元测试项目
│  ├─ AwAUI.Graphics.UnitTests/
│  ├─ AwAUI.Css.UnitTests/
│  ├─ AwAUI.Layout.UnitTests/
│  ├─ AwAUI.UI.UnitTests/
│  ├─ AwAUI.Controls.UnitTests/
│  ├─ AwAUI.Tailwind.UnitTests/     含与 Tailwind v4 官方输出的快照比对
│  ├─ AwAUI.Shadcn.UnitTests/
│  ├─ AwAUI.Markup.UnitTests/       .awaui → C# 的快照测试
│  ├─ AwAUI.RenderTests/            黄金图像测试（软件 / GPU 各跑一遍）；第七批的 DrawImage 黄金图暂存在本项目的 Golden/
│  ├─ AwAUI.DevTools.Tests/         DevTools 面板的端到端测试（第七批；名字不符合 .UnitTests 约定，csproj 里单独声明 InternalsVisibleTo）
│  ├─ AwAUI.Browser.Tests/          Browser 的 Translation/ 源码测试；Platforms/ 下以源码链接方式测 Win32 / X11 / macOS 输入的纯逻辑（暂放，计划拆出 AwAUI.Platform.UnitTests）
│  ├─ AwAUI.Css.ConformanceTests/   由 Web Platform Tests 子集转换
│  ├─ AwAUI.Text.ShapingTests/      整形一致性测试
│  ├─ AwAUI.IntegrationTests/       经 DevTools 协议驱动的端到端测试
│  ├─ AwAUI.ArchitectureTests/      程序集依赖规则、公共 API 规则
│  ├─ AwAUI.AotSmokeTests/          NativeAOT 发布并运行
│  ├─ AwAUI.Benchmarks/
│  ├─ Shared/                       测试项目以链接方式共享的源码（如 ThreadCpuClock.cs，命名空间 AwAUI.Tests.Shared；由 Markup、Imaging、DevTools 测试链接）
│  └─ TestFiles/                    黄金图像、测试字体、整形数据、CSS 用例
├─ tooling/
│  └─ vscode-awaui/                 .awaui 语法高亮（TextMate 语法），后续语言服务器
├─ build/                           共享的 MSBuild 属性
│  ├─ TargetFrameworks.props        通用 TFM 定义
│  ├─ Polyfills.props               链接 Shared/Polyfills 与兼容包引用
│  ├─ Trimming.props                AOT / 裁剪标注（仅 net8.0+）
│  ├─ Analyzers.props               仓库自用分析器与规则集
│  ├─ SourceGenerators.props        引用 src/tools 下的生成器
│  └─ Package.props                 NuGet 元数据、版本
├─ api/                             各程序集的公共 API 基线，变更须显式更新
├─ docs/                            面向使用者的文档
├─ Directory.Build.props
├─ Directory.Build.targets
├─ Directory.Packages.props         集中式包版本管理
├─ global.json
├─ AwAUI.sln
└─ .editorconfig
```

## 命名约定

- 程序集名 = NuGet 包名 = 根命名空间。
- 平台实现直接以平台命名：`AwAUI.Win32`、`AwAUI.X11`、`AwAUI.Android`（与 Avalonia 一致）。此前文档中的 `Platform.Win32`、`Render.Rhi.Metal` 等写法以本文为准：`AwAUI.Win32`、`AwAUI.Rhi.Metal`。
- 抽象基类不带 `I` 前缀（`RenderBackend`、`WindowImpl`），可选能力接口带 `I` 前缀（`IWin32Surface`）。
- 测试项目：`<程序集名>.UnitTests`。
- 内部共享代码放 `src/Shared`，通过 `<Compile Include=... Link=...>` 链接进各项目并标记为 `internal`，不单独成程序集。

## 与 Avalonia 的差异及原因

| Avalonia | AwAUI | 原因 |
|---|---|---|
| `Avalonia.Base` 一个大程序集承载视觉、布局、输入、动画、样式 | 拆成 Graphics / Css / Layout / Scene / UI | 「UI 层不可见渲染实现」「CSS 引擎不依赖元素树具体类型」需要用程序集边界强制 |
| `src/Skia/Avalonia.Skia` | `src/AwAUI.Render` + `src/Rhi/*` | 自研渲染器，按图形 API 拆分设备层 |
| `native/`（ObjC++ 的 macOS 原生库） | 无 | 纯 C#，macOS 走 ObjC 运行时互操作 |
| `src/Markup/Avalonia.Markup.Xaml*`（XAML 编译器） | `src/Markup/AwAUI.Markup.*`（`.awaui` 编译器） | 位置相同，内容不同 |
| `Avalonia.Diagnostics`（内置 DevTools 窗口） | `AwAUI.Diagnostics`（探针 + 协议）与 `AwAUI.DevTools`（前端）分开 | 协议要被远程前端、自动化测试、AI 工具复用 |
| `Avalonia.Themes.Fluent / Simple`（XAML 主题） | `AwAUI.Themes.Fluent`（CSS）；另有 `AwAUI.Shadcn`（Tailwind 工具类，见 23） | |
| Nuke 构建脚本 | 先用 `dotnet` 命令 + CI 配置，复杂后再引入构建脚本工程 | 早期保持简单 |

保留的做法：`Directory.Build.props` 统一配置、`build/*.props` 按关注点拆分并由各项目按需导入、`samples/ControlCatalog` 加各平台宿主头、`tests/TestFiles` 存放黄金图像、`api/` 公共 API 基线、元包（`AwAUI`、`AwAUI.Desktop`）简化使用方的引用。

## 项目文件约定

典型的核心库项目只需几行，其余由共享属性提供：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <Import Project="$(RepoRoot)build/TargetFrameworks.props" />
  <Import Project="$(RepoRoot)build/Polyfills.props" />
  <Import Project="$(RepoRoot)build/Trimming.props" />
  <ItemGroup>
    <ProjectReference Include="../AwAUI.Foundation/AwAUI.Foundation.csproj" />
  </ItemGroup>
</Project>
```

`Directory.Build.props` 统一设置：最新语言版本、可空引用类型、警告即错误、确定性构建、`InternalsVisibleTo` 到对应测试项目、仓库自用分析器。

## 使用方看到的包

| 场景 | 引用 |
|---|---|
| 桌面应用（.NET Framework 或现代 .NET） | `AwAUI` + `AwAUI.Desktop` + `AwAUI.Markup` |
| Android / iOS / Web | `AwAUI` + 对应平台包 + `AwAUI.Markup` |
| 只用纯 C# 写法 | 去掉 `AwAUI.Markup` |
| 开发期调试 | `AwAUI.DevTools`（Debug 条件引用） |
| 测试 | `AwAUI.Headless.XUnit` |

## 建立顺序

仓库骨架按里程碑逐步长出，不一次建全：M0 只需要 `Foundation`、`Graphics`、`Imaging`、`Render`（Raster）、`Platform`、`Headless`、`MacOS`、`Shared`、`build/`、`samples/RenderDemo`、`Graphics.UnitTests`、`RenderTests` 与 `ArchitectureTests`（已建立）。
