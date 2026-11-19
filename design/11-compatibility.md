# 11 运行时兼容：.NET Standard 2.0 / .NET Framework / .NET 8–11

要求：框架可用于 **.NET Standard 2.0、.NET Framework、.NET 8、9、10、11**。这一条对全局都有约束，本文定义目标框架、可用的语言与 API 范围，以及各能力在不同运行时上的分级。

## 目标框架（TFM）

| 程序集类别 | 编译目标 | 说明 |
|---|---|---|
| 核心库（Foundation、Graphics、Text、Css、Render、Scene、UI、Controls、Diagnostics、DevTools） | `netstandard2.0; net8.0; net10.0` | 下文称「通用 TFM」 |
| RHI：D3D11、GLES、Metal | 通用 TFM | 全部是裸指针互操作，不依赖新运行时。已实现的 Metal 只用 blittable 的 `objc_msgSend` 签名，命令完成靠轮询而不是原生回调 / block，所以 netstandard2.0 与 net8+ 代码路径相同（netstandard2.0 产物未在 Mono 下运行过） |
| 桌面平台：Win32、MacOS、X11、Wayland | 通用 TFM | |
| `AwAUI.Browser` | `net10.0-browser` | `JSImport / JSExport` 仅现代 .NET 的 browser 目标提供；.NET 8 已于 2026-11 结束支持，WASM 的实测也都在 .NET 10 上；只有一个目标即只有一份产物需要验证 |
| `AwAUI.Android` / `AwAUI.iOS` | 现代 .NET 的移动 workload TFM | 以当前 SDK 支持的 workload 版本为准 |
| 源生成器与分析器 | `netstandard2.0` | Roslyn 的要求 |

各运行时实际加载哪份产物：

| 应用运行在 | 使用的产物 | 支持的平台 |
|---|---|---|
| .NET Framework 4.7.2+ | netstandard2.0 | Windows |
| Mono 及其他实现 netstandard2.0 的运行时（含 Unity 等宿主） | netstandard2.0 | 取决于宿主；桌面平台实现可用 |
| .NET 8 / .NET 9 | net8.0 | 桌面、Web；移动端视 workload |
| .NET 10 / .NET 11 | net10.0 | 全部六个平台 |

- .NET 9 与 .NET 11 不单独编译产物（向前兼容加载 net8.0 / net10.0 的产物），但都在 CI 测试矩阵内。只有当某个版本提供了值得专门利用的 API 时才增加对应 TFM。
- .NET Framework 的最低版本定为 **4.7.2**：这是 netstandard2.0 无需额外垫片即可稳定使用的最低版本（4.6.1 名义支持但问题很多）。
- 结论上：**netstandard2.0 是功能基线，net8.0+ 是性能与 AOT 的增强层**。任何功能都必须先在基线上成立。

## 依赖策略

netstandard2.0 下缺失的基础类型通过微软官方的纯托管兼容包补齐，只允许这一类依赖：

| 包 | 提供 |
|---|---|
| `System.Memory` / `System.Buffers` | `Span<T>`、`Memory<T>`、`ArrayPool<T>` |
| `System.Numerics.Vectors` | `Vector2/3/4`、`Matrix3x2`、`Vector<T>` |
| `System.Runtime.CompilerServices.Unsafe` | `Unsafe` |
| `System.Threading.Tasks.Extensions` / `Microsoft.Bcl.AsyncInterfaces` | `ValueTask`、`IAsyncEnumerable` |

在 net8.0+ 目标下这些包全部不引用。除此之外核心库**零第三方依赖**，其余缺口自己补：

| 缺口 | 做法 |
|---|---|
| `Rune` | 文本栈自有 `CodePoint` 类型（本来就需要） |
| `MathF`、`HashCode`、`BitOperations`、`string.Create` 等 | 内部 polyfill |
| 编译器所需特性类型（`IsExternalInit`、`RequiredMember`、可空与裁剪标注特性等） | 内部 polyfill 源文件 |
| `ZLibStream`（.NET 6+） | 用 netstandard2.0 已有的 `DeflateStream` 加手写 zlib 头与 Adler-32 校验，全目标统一使用（M0 实现时由「自写 inflate」改为此方案，结果相同而代码少得多） |
| JSON（DevTools 协议） | 自带极简的 UTF-8 JSON 读写器，协议类型的序列化代码由自家源生成器产生；避免在 .NET Framework 上引入 `System.Text.Json` 的整串依赖 |
| WebSocket 服务端 | 基于 `TcpListener` 自实现 RFC 6455 的最小子集，全目标统一 |
| `NativeLibrary` | 各平台自己封装 `LoadLibrary` / `dlopen` |

## 语言特性

所有项目使用最新 C# 语言版本编译（构建机需要较新的 .NET SDK，与目标框架无关）。但语言特性分两类：

| 类别 | 例子 | 在 netstandard2.0 上 |
|---|---|---|
| 纯编译器特性 | 可空引用类型、模式匹配、`record`、`init`、`required`、集合表达式、主构造函数、`partial` 属性、文件范围命名空间、原始字符串、`ref struct`、`readonly struct`、`in` 参数、函数指针（经典调用约定） | 可用（必要时配 polyfill 特性类型） |
| 依赖运行时的特性 | 接口默认实现、接口静态抽象成员（泛型数学）、`ref` 字段、协变返回、内联数组、`allows ref struct` | **不可用** |

由此得出的编码规则：

1. **公共 API 中禁止出现依赖运行时的特性**。尤其是不用接口默认实现 —— 意味着接口发布后不能加成员。因此：**可能演进的扩展点用抽象类而不是接口**（如渲染后端、平台窗口、文本引擎的基类），接口只用于小而稳定的契约或「可选能力」查询。
2. 泛型数值代码不用泛型数学，按具体类型写（主要是 `float` / `double`）。
3. 内部实现允许 `#if NET8_0_OR_GREATER` 走快速路径，但必须有基线实现，且两条路径共用同一套测试。
4. 条件编译集中在少数「内核」文件中（SIMD 内核、互操作辅助、polyfill），业务代码不散落 `#if`。

### 使用方的语言版本

组件写法依赖较新的 C# 语法（`partial` 类配合源生成器、集合表达式等）。.NET Framework 项目的默认语言版本是 C# 7.3，因此：

- 要求使用 **SDK 风格项目** + 较新的 .NET SDK 构建（源生成器的前提）；NuGet 包内的 `.props` 在未显式指定时把 `LangVersion` 提升到框架所需版本，并在不满足时给出明确的构建错误。
- 公共 API 设计保证在老语法下也**可用**（例如子节点同时支持集合表达式 `Children = [a, b]` 与集合初始化器 `Children = { a, b }`），新语法只是更简洁。
- 不支持 `packages.config` 与旧式 csproj。

## 互操作

| 机制 | 现代 .NET | netstandard2.0 / .NET Framework | 统一做法 |
|---|---|---|---|
| P/Invoke 声明 | `LibraryImport`（源生成，AOT 友好） | 只有 `DllImport` | 所有签名**只用 blittable 类型**（指针、基元、blittable 结构体），不依赖运行时封送；同一声明用条件编译切换特性 |
| 原生回调（WndProc、ObjC 方法实现、Wayland 监听器） | `[UnmanagedCallersOnly]` + 函数指针 | 委托 + `Marshal.GetFunctionPointerForDelegate`（需保持委托存活） | 封装在 `NativeCallback` 辅助类型内 |
| COM（D3D11、UIA） | 可用 `ComWrappers` | 不可用 | 统一用裸 vtable 指针调用，不使用运行时 COM 封送 |
| JS 互操作 | `JSImport/JSExport` | 不适用 | 仅 `AwAUI.Browser` |

## 性能分级

同一份算法，按运行时能力分三档内核：

| 档位 | 运行时 | 手段 |
|---|---|---|
| 基线 | 全部 | 标量代码 |
| 向量化 | netstandard2.0（含 .NET Framework x64 RyuJIT） | `System.Numerics.Vector<T>` |
| 硬件内建 | net8.0+ | `Vector128/256/512`、平台内建函数（SSE/AVX、NEON、WASM SIMD） |

涉及：光栅器的覆盖率累积与混合、渐变求值、图片解码的色彩转换与缩放、CSS 选择器匹配的布隆过滤、文本的码点分类。

预期：.NET Framework 上功能完整但更慢（`Span<T>` 为慢速实现、JIT 优化较弱、无分层编译的 PGO）。软件渲染的大幅面高帧率场景在 .NET Framework 上建议开启 GPU 后端（D3D11 在其上完全可用）。性能预算分运行时分别设定与守护。

## 能力差异

| 能力 | .NET Framework / netstandard2.0 | .NET 8–9 | .NET 10–11 |
|---|---|---|---|
| Windows 桌面 | ✅ | ✅ | ✅ |
| macOS / Linux 桌面 | 经 Mono（尽力支持） | ✅ | ✅ |
| Android / iOS | — | 视 workload 支持 | ✅ |
| WebAssembly | — | ✅ | ✅ |
| NativeAOT / 裁剪 | — | ✅ | ✅ |
| 软件渲染 / GPU 渲染 | ✅ | ✅ | ✅ |
| 完整文本整形、CSS、组件、触摸、无障碍 | ✅ | ✅ | ✅ |
| DevTools（内嵌、独立窗口、远程） | ✅ | ✅ | ✅ |
| 热重载（代码） | —（仅 CSS 热更新） | ✅ | ✅ |
| 渲染线程 | ✅ | ✅（WASM 默认单线程） | 同左 |

原则：**功能不因运行时而缺失，只有平台可达性、性能和工具链能力有差异**。

## 对其他文档的影响

- [01](01-architecture.md)：程序集表中的 TFM 以本文为准；AOT/裁剪标注只作用于 net8.0+ 目标。
- [02](02-graphics-abstraction.md)、[04](04-platform.md)：扩展点已按规则 1 定为抽象基类 + 可选能力接口。
- [03](03-renderer.md)：SIMD 三档内核；PNG 解码使用自带 inflate。
- [05](05-ui-core.md)、[12](12-component-api.md)：组件 API 不依赖运行时特性，老语法可用。
- [06](06-devtools.md)：JSON 与 WebSocket 自实现；热重载按运行时分级。

## 测试矩阵

实现说明：测试项目在 Windows 上（或指定 `-p:IncludeNetFx=true` 时）额外以 `net48` 编译，此时被测库加载的是 netstandard2.0 产物 + System.Memory，即 .NET Framework 用户实际走的路径。CI 在 Windows 上执行它们。

例外（测试项目只以 `net10.0; net11.0` 为目标，不编译 net48）：

- `AwAUI.Rhi.Metal.Tests`：Metal 只在 macOS 上可用，而 .NET Framework 只在 Windows 上。
- `AwAUI.Samples.Tests`：引用 net10.0 的示例应用。
- `AwAUI.Diagnostics.UnitTests`：项目文件中注明的早期决定；库的 netstandard2.0 产物另有加载测试。

`AwAUI.Browser.Tests` 是另一类例外：普通的 net10.0 项目不能引用 `net10.0-browser` 的库，所以它直接编译库里 `Translation/` 下的纯 C# 源码（事件翻译、派发队列、附着结果解码等），照常以 net10.0 / net11.0（及 net48）运行；依赖浏览器的部分只由 `tests/AwAUI.Browser.Tests/node` 下的 Node 冒烟（DOM 垫片）与真实无头浏览器冒烟覆盖，不在 `dotnet test` 内。


| 运行时 | 操作系统 | 内容 |
|---|---|---|
| .NET Framework 4.7.2、4.8.1 | Windows | 全部单元测试、黄金图像、Win32 冒烟 |
| .NET 8、9、10、11 | Windows / macOS / Linux | 全部单元测试、黄金图像、各平台冒烟 |
| .NET 10、11 | Android / iOS / WASM | 冒烟与触摸自动化 |
| NativeAOT（net8.0、net10.0） | 桌面 | 发布并运行冒烟测试，裁剪警告即错误 |
| Mono | Linux | 核心库单元测试（尽力） |

黄金图像在各运行时之间必须**逐像素一致**（基线、向量化、硬件内建三档内核输出相同），这是对多档实现最有效的约束。
