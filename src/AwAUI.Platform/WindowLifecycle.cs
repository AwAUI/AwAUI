using System;

namespace AwAUI.Platform;

/// <summary>移动端/桌面应用的前后台状态（design/04「生命周期」）。</summary>
public enum AppLifecycle
{
    /// <summary>未知或平台不报告。</summary>
    Unknown,

    /// <summary>前台且可交互。</summary>
    Active,

    /// <summary>前台但窗口失去焦点。</summary>
    Inactive,

    /// <summary>进入后台（移动端切到后台、桌面最小化到托盘）。</summary>
    Background,

    /// <summary>从后台回到前台。</summary>
    Foreground,
}

/// <summary>
/// 窗口的上下左右内边距（安全区、软键盘占位）。逻辑像素。
/// </summary>
public readonly struct Thickness : IEquatable<Thickness>
{
    public Thickness(double uniform) : this(uniform, uniform, uniform, uniform)
    {
    }

    public Thickness(double left, double top, double right, double bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    public double Left { get; }
    public double Top { get; }
    public double Right { get; }
    public double Bottom { get; }

    public static Thickness Zero => default;

    public bool IsZero => Left == 0 && Top == 0 && Right == 0 && Bottom == 0;

    public bool Equals(Thickness other) =>
        Left.Equals(other.Left) && Top.Equals(other.Top) && Right.Equals(other.Right) && Bottom.Equals(other.Bottom);

    public override bool Equals(object? obj) => obj is Thickness other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = Left.GetHashCode();
            hash = (hash * 397) ^ Top.GetHashCode();
            hash = (hash * 397) ^ Right.GetHashCode();
            hash = (hash * 397) ^ Bottom.GetHashCode();
            return hash;
        }
    }

    public static bool operator ==(Thickness left, Thickness right) => left.Equals(right);
    public static bool operator !=(Thickness left, Thickness right) => !left.Equals(right);

    public override string ToString() => $"({Left}, {Top}, {Right}, {Bottom})";
}

/// <summary>
/// 平台 → 框架的可选生命周期回调（design/04）。与 <see cref="IWindowEvents"/> 分开，
/// 以保持既有实现（示例、测试宿主）不被新增成员破坏；平台用 <c>Events as IWindowLifecycleEvents</c> 查询。
/// </summary>
public interface IWindowLifecycleEvents
{
    /// <summary>图形表面丢失（移动端切后台、设备丢失）；缓存的 GPU 资源应视为无效。</summary>
    void OnSurfaceLost();

    /// <summary>图形表面恢复；应重建设备资源并请求一帧。</summary>
    void OnSurfaceRestored();

    /// <summary>应用前后台状态变化。</summary>
    void OnLifecycle(AppLifecycle state);

    /// <summary>窗口获得/失去激活（键盘焦点之外的窗口级激活）。</summary>
    void OnActivated(bool activated);

    /// <summary>安全区/软键盘占位变化。</summary>
    void OnSafeAreaChanged(Thickness insets);
}

/// <summary>平台 → 框架的可选窗口状态回调；同样与 <see cref="IWindowEvents"/> 分开以保持兼容。</summary>
public interface IWindowStateEvents
{
    /// <summary>窗口状态变化（用户点标题栏最大化/最小化、平台全屏切换等）。</summary>
    void OnWindowStateChanged(WindowState state);

    /// <summary>窗口位置变化（用户拖动窗口）。</summary>
    void OnPositionChanged(Point position);
}

/// <summary>
/// 平台 → 框架的可选内存压力回调（iOS <c>didReceiveMemoryWarning</c>、Android <c>onTrimMemory</c>）。
/// 宿主应释放可重建的缓存（图集、字形位图、离屏图层），不要丢弃窗口内容。
/// </summary>
public interface IWindowMemoryPressureEvents
{
    /// <summary>系统报告内存压力。</summary>
    void OnMemoryPressure();
}
