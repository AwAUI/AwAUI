# HelloAot

最小桌面程序：界面全部写在 [App.awaui](App.awaui)（声明式组件），`Program.cs` 只配置组合根
（`AwAUI.Desktop` 选择平台与渲染后端），应用对象在主窗口里放根组件。用于演示声明式界面、
托管组合根、`Application` / `Window` 托管与 NativeAOT 单文件发布的体积。

```csharp
AwAppBuilder.Create()
    .UsePlatformDetect()          // Windows → Win32，macOS → AppKit，Linux → X11
    .UseSoftwareRenderer()
    .UseDefaultTextEngine()
    .Run<HelloApplication>();

sealed class HelloApplication : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        ((DesktopApplicationLifetime)Lifetime!).MainWindow = new Window(new WindowOptions
        {
            Title = "AwAUI Hello",
            Size = new Size(420, 260),
        })
        {
            Content = new App(),
        };
    }
}
```

应用**不引用** `AwAUI.Win32` / `AwAUI.MacOS` / `AwAUI.X11`。`AwAUI.Desktop` 在构建期按
`RuntimeIdentifier`（或宿主 OS）只引用目标平台的后端，其他平台的程序集不会进入依赖图，
因此普通 build 和 AOT 发布都不会带上它们。

```sh
dotnet run --project samples/HelloAot

# NativeAOT 单文件发布（macOS Apple Silicon）
dotnet publish samples/HelloAot -c Release -r osx-arm64 -p:NativeAot=true -o artifacts/hello-aot

# 体积优先的 AOT 发布（关闭符号、堆栈跟踪与 globalization）
dotnet publish samples/HelloAot -c Release -r osx-arm64 -p:NativeAot=true -p:SizeOptimized=true -o artifacts/hello-aot-size

# Windows x64（必须在 Windows 上执行；NativeAOT 不支持跨 OS 链接）
dotnet publish samples/HelloAot -c Release -r win-x64 -p:NativeAot=true -p:SizeOptimized=true -o artifacts/hello-aot-win
```

Windows 需要 Visual Studio（Build Tools 即可，2022 或更新）的“使用 C++ 的桌面开发”与 Windows SDK。在 macOS 上执行同一条
`win-x64` 命令时，托管程序集会正确只引用 `AwAUI.Win32`，但原生链接阶段会报
`Cross-OS native compilation is not supported.`。

csproj 使用 `<OutputType>WinExe</OutputType>`：Windows 上生成 GUI 子系统的 PE，从资源管理器或快捷方式启动
不会分配控制台窗口；macOS / Linux 上 `WinExe` 等同 `Exe`，同一份 csproj 通用（见 design/04「Windows 桌面应用的子系统」）。

## 体积（macOS arm64，.NET 11 SDK，2026-11-22）

| 发布方式 | 可执行文件 | 说明 |
|---|---:|---|
| 默认 AOT | **5,331,688 B（5.1 MiB）** | 同目录 dSYM 另占 17 MiB（不随应用分发） |
| `SizeOptimized` | **4,618,856 B（4.4 MiB）** | 关闭符号、堆栈跟踪、globalization，并开启 `--Os` |
| `SizeOptimized`，Windows x64 | **4,395,008 B（4.19 MiB）** | Windows 11 26300 + .NET 11.0.100 上测得；PE 为 GUI 子系统 |
| 同上，xz -9 压缩 | 1,570,136 B（1.5 MiB） | 仅作分发压缩参考 |
| 同上，zstd -19 压缩 | 1,779,761 B（1.7 MiB） | 仅作分发压缩参考 |

参考下限：同样开关下的纯控制台 HelloWorld（不引用 AwAUI）为 970,264 B（0.93 MiB）。
其中 AwAUI 引擎约占 1.5 MiB（CSS 0.75、文本 + Unicode 0.36、UI 0.21、布局 0.10、渲染 0.11），
其余是 .NET 运行时与 ObjC / 文本 / 异常等基础设施。`Application` / `Window` 托管层（应用生命周期、
窗口登记与关闭策略）实测只增加约 16 KB。`UseDevTools()` 通过延迟委托接线，未调用时整个 Diagnostics
协议栈会被裁剪掉（实测省约 0.9 MiB）。

单文件可执行程序运行时只依赖系统库（libSystem / libobjc / Swift / CoreFoundation / Network 等），
不需要安装 .NET 运行时；各产物都实际启动验证过 3 秒无崩溃。
