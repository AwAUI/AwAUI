using System;
using AwAUI.Graphics;
using AwAUI.Threading;

namespace AwAUI.Platform;

/// <summary>
/// 平台后端（design/04）。把操作系统的窗口、输入、系统服务翻译成统一接口；
/// 不知道元素树，也不知道渲染后端，只通过 <see cref="Graphics.NativeSurface"/> 交出可绘制的表面。
/// </summary>
public abstract class PlatformBackend
{
    public abstract string Name { get; }

    /// <summary>主线程调度器。</summary>
    public abstract IDispatcher Dispatcher { get; }

    /// <summary>创建顶层窗口（移动端与 Web 上为唯一的根视图）。</summary>
    public abstract WindowImpl CreateWindow(WindowOptions options);

    /// <summary>运行主消息循环，直到 <see cref="Exit"/> 被调用或最后一个窗口关闭。</summary>
    public abstract void Run();

    public abstract void Exit();

    /// <summary>系统剪贴板；平台不支持时为 null。默认取 <see cref="GetService{T}"/>。</summary>
    public virtual Clipboard? Clipboard => GetService<Clipboard>();

    /// <summary>显示器集合；平台不支持时为 null。默认取 <see cref="GetService{T}"/>。</summary>
    public virtual Screens? Screens => GetService<Screens>();

    /// <summary>文件与目录选择器；平台不支持时为 null。默认取 <see cref="GetService{T}"/>。</summary>
    public virtual StorageProvider? StorageProvider => GetService<StorageProvider>();

    /// <summary>打开 URI / 文件；平台不支持时为 null。默认取 <see cref="GetService{T}"/>。</summary>
    public virtual Launcher? Launcher => GetService<Launcher>();

    /// <summary>可选服务（剪贴板、文件对话框、触觉反馈……）。不支持时返回 null。</summary>
    public virtual T? GetService<T>() where T : class => null;
}

public sealed class WindowOptions
{
    public string Title { get; set; } = "AwAUI";
    public Size Size { get; set; } = new(800, 600);
    public bool Resizable { get; set; } = true;

    /// <summary>初始位置（屏幕逻辑像素）；null 时按 <see cref="StartupLocation"/> 决定。</summary>
    public Point? Position { get; set; }

    /// <summary>初始位置策略；<see cref="WindowStartupLocation.Manual"/> 时使用 <see cref="Position"/>，未给位置则交给平台。</summary>
    public WindowStartupLocation StartupLocation { get; set; } = WindowStartupLocation.Manual;

    /// <summary>初始窗口状态。</summary>
    public WindowState State { get; set; } = WindowState.Normal;

    /// <summary>是否置顶。</summary>
    public bool Topmost { get; set; }

    /// <summary>窗口最小宽度（逻辑像素）；null 表示平台默认。</summary>
    public double? MinWidth { get; set; }

    /// <summary>窗口最小高度（逻辑像素）；null 表示平台默认。</summary>
    public double? MinHeight { get; set; }

    /// <summary>窗口最大宽度（逻辑像素）；null 表示平台默认。</summary>
    public double? MaxWidth { get; set; }

    /// <summary>窗口最大高度（逻辑像素）；null 表示平台默认。</summary>
    public double? MaxHeight { get; set; }

    /// <summary>窗口图标；null 表示平台默认图标。后端可读取像素并转换为平台图标。</summary>
    public Bitmap? Icon { get; set; }

    /// <summary>是否在任务栏/程序坞中显示（支持的平台）。</summary>
    public bool ShowInTaskbar { get; set; } = true;

    /// <summary>按内容调整窗口尺寸的策略。</summary>
    public SizeToContent SizeToContent { get; set; } = SizeToContent.Manual;

    /// <summary>
    /// 应用标识（反向 DNS，例如 <c>com.example.app</c>）。Wayland 上对应
    /// <c>xdg_toplevel.set_app_id</c>（任务栏 / 应用菜单分组），Android / iOS 上作为包标识的提示；
    /// 其他平台忽略。null 时由后端使用默认值。
    /// </summary>
    public string? AppId { get; set; }

    /// <summary>
    /// 窗口边框与标题栏（design/04「窗口装饰」）。<see cref="WindowDecorations.None"/> 时平台不画标题栏与边框，
    /// 客户区覆盖整个窗口，由应用自绘标题栏；拖动、最小化、最大化经 <see cref="IWindowChrome"/> 完成。
    /// 移动端与 Web 后端忽略此选项。
    /// </summary>
    public WindowDecorations Decorations { get; set; } = WindowDecorations.System;
}

/// <summary>窗口装饰方式。</summary>
public enum WindowDecorations
{
    /// <summary>系统标题栏与边框（默认）。</summary>
    System,

    /// <summary>
    /// 无系统边框：客户区覆盖整个窗口，由应用自绘标题栏与窗口按钮（design/04）。
    /// 平台仍保留阴影、圆角与输入焦点等系统行为；拖动 / 最小化 / 最大化用 <see cref="IWindowChrome"/>。
    /// </summary>
    None,
}
