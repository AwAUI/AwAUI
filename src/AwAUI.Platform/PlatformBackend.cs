using System;
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

    /// <summary>可选服务（剪贴板、文件对话框、触觉反馈……）。不支持时返回 null。</summary>
    public virtual T? GetService<T>() where T : class => null;
}

public sealed class WindowOptions
{
    public string Title { get; set; } = "AwAUI";
    public Size Size { get; set; } = new(800, 600);
    public bool Resizable { get; set; } = true;
}
