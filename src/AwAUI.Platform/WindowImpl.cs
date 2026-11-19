using System;
using AwAUI.Graphics;

namespace AwAUI.Platform;

/// <summary>
/// 平台窗口：桌面上是窗口，移动端是 Activity/UIView，Web 上是 canvas。
/// 只有所有平台都有的能力进入这里；平台特有能力经 <see cref="GetFeature{T}"/> 查询。
/// </summary>
public abstract class WindowImpl : IDisposable
{
    /// <summary>平台 → 框架的回调入口。</summary>
    public IWindowEvents? Events { get; set; }

    public abstract NativeSurface Surface { get; }

    /// <summary>客户区尺寸（逻辑像素）。</summary>
    public abstract Size ClientSize { get; }

    /// <summary>逻辑像素到设备像素的缩放。</summary>
    public abstract double RenderScale { get; }

    public abstract void Show();
    public abstract void Close();
    public abstract void SetTitle(string title);
    public virtual void SetCursor(CursorShape cursor) { }

    /// <summary>请求在下一次垂直同步时收到 <see cref="IWindowEvents.OnFrame"/>。</summary>
    public abstract void RequestFrame();

    /// <summary>
    /// 输入法桥（design/04「IME」）：焦点进入可编辑元素时框架把文本输入客户端交给它。不支持文本输入的平台返回 null，
    /// 此时文本只经 <see cref="IWindowEvents.OnTextInput"/> 送达。
    /// </summary>
    public virtual TextInputMethod? TextInput => null;

    /// <summary>窗口所属平台的剪贴板（与 <see cref="PlatformBackend.Clipboard"/> 相同；只持有窗口的宿主经它访问）。不支持时为 null。</summary>
    public virtual Clipboard? Clipboard => null;

    /// <summary>
    /// 平台偏好的滚动条样式（macOS 跟随「显示滚动条」系统设置，Windows / Linux 为经典式）。框架在触摸输入时总是用覆盖式指示条。
    /// </summary>
    public virtual ScrollbarStyle PreferredScrollbarStyle => ScrollbarStyle.Overlay;

    public virtual T? GetFeature<T>() where T : class => this as T;

    public virtual void Dispose() => Close();
}

/// <summary>由框架实现、平台调用。所有回调都在主线程上发生。</summary>
public interface IWindowEvents
{
    void OnFrame(TimeSpan timestamp);
    void OnResized(Size clientSize, double scale);
    void OnPointer(in RawPointerEvent e);
    void OnKey(in RawKeyEvent e);
    void OnTextInput(string text);
    void OnFocusChanged(bool focused);

    /// <summary>返回 false 取消关闭。</summary>
    bool OnCloseRequested();
    void OnClosed();
}

public enum CursorShape { Arrow, IBeam, Hand, Crosshair, ResizeHorizontal, ResizeVertical, Wait, NotAllowed, None }
