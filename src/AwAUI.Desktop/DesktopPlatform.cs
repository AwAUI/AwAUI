using System;
using AwAUI.Platform;

namespace AwAUI.Desktop;

/// <summary>
/// 构建期选定的桌面平台后端。目标平台在 <c>AwAUI.Desktop.props</c> 中按 RuntimeIdentifier / 宿主 OS 判定，
/// 编译时常量只留下一个后端；其他平台的程序集根本不在依赖图里（design/01 规则 5、design/15）。
/// </summary>
internal static class DesktopPlatform
{
#if AWAUI_DESKTOP_WIN32
    public const string TargetOS = "Windows";

    public static PlatformBackend Create() => new AwAUI.Win32.Win32Platform();
#elif AWAUI_DESKTOP_MACOS
    public const string TargetOS = "macOS";

    public static PlatformBackend Create() => new AwAUI.MacOS.MacPlatform();
#elif AWAUI_DESKTOP_X11
    public const string TargetOS = "Linux";

    public static PlatformBackend Create() => new AwAUI.X11.X11Platform();
#else
    public const string TargetOS = "unsupported";

    public static PlatformBackend Create() => throw new PlatformNotSupportedException(
        "AwAUI.Desktop 没有为目标平台引用窗口后端。请用 -r win-x64 / osx-arm64 / linux-x64 指定目标平台，"
        + "或在项目中显式调用 UsePlatform(...) 传入自定义 PlatformBackend。");
#endif

    /// <summary>当前构建实际引用的平台程序集名（诊断与错误信息使用）。</summary>
    public static string BackendAssembly => TargetOS switch
    {
        "Windows" => "AwAUI.Win32",
        "macOS" => "AwAUI.MacOS",
        "Linux" => "AwAUI.X11",
        _ => "(none)",
    };
}
