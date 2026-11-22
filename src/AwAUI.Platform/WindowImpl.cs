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

    /// <summary>窗口所在显示器集合；不支持时为 null。</summary>
    public virtual Screens? Screens => null;

    /// <summary>文件与目录选择器；不支持时为 null。</summary>
    public virtual StorageProvider? StorageProvider => null;

    /// <summary>打开 URI / 文件；不支持时为 null。</summary>
    public virtual Launcher? Launcher => null;

    /// <summary>
    /// 平台无障碍桥（design/10）：框架把语义树快照交给它，由它向系统暴露辅助技术可用的树。
    /// 平台不支持无障碍时为 null；<see cref="UI.UIHost"/> 在创建时连接、销毁时断开。
    /// </summary>
    public virtual AccessibilityBridge? Accessibility => null;

    /// <summary>窗口左上角在屏幕逻辑坐标中的位置；平台不报告时返回默认值。</summary>
    public virtual Point Position => default;

    /// <summary>移动窗口（屏幕逻辑像素）。平台不支持时忽略。</summary>
    public virtual void SetPosition(Point position) { }

    /// <summary>当前窗口状态；平台不报告时为 <see cref="AwAUI.Platform.WindowState.Normal"/>。</summary>
    public virtual WindowState WindowState => AwAUI.Platform.WindowState.Normal;

    /// <summary>切换窗口状态（普通/最小化/最大化/全屏）。平台不支持时忽略。</summary>
    public virtual void SetWindowState(WindowState state) { }

    /// <summary>是否置顶。设置在不支持的平台上被忽略。</summary>
    public virtual bool Topmost
    {
        get => false;
        set { }
    }

    /// <summary>窗口是否处于激活状态。</summary>
    public virtual bool IsActive => true;

    /// <summary>请求平台激活窗口（置于前台并获得焦点）。</summary>
    public virtual void Activate() { }

    /// <summary>安全区（刘海、圆角、软键盘占位），逻辑像素；桌面通常为 <see cref="Thickness.Zero"/>。</summary>
    public virtual Thickness SafeAreaInsets => Thickness.Zero;

    /// <summary>请求平台重绘指定区域（屏幕逻辑坐标；空矩形表示整窗）。默认忽略，由平台按自身帧机制处理。</summary>
    public virtual void Invalidate(in Rect rect) { }

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
