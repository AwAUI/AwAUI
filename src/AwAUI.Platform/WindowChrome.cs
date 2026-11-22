namespace AwAUI.Platform;

/// <summary>
/// 自绘标题栏需要的系统窗口动作（design/04「窗口装饰」）。经 <see cref="WindowImpl.GetFeature{T}()"/> 查询：
/// 桌面后端实现它，移动端与 Web 返回 null。设置了 <see cref="WindowDecorations.None"/> 的应用用这些方法
/// 实现拖动、最小化与最大化；系统边框仍存在时它们也照常可用。
/// <para>
/// 由框架的输入派发同步调用：<see cref="BeginDragMove"/> 只在指针按下的事件处理中有效（平台据此取得
/// 当前的鼠标事件或捕获），拖动期间平台拥有指针，返回后框架收到一次取消事件以清除按下状态。
/// </para>
/// </summary>
public interface IWindowChrome
{
    /// <summary>开始拖动窗口（标题栏按下）。必须在指针按下的事件处理中调用。</summary>
    void BeginDragMove();

    /// <summary>最小化。</summary>
    void Minimize();

    /// <summary>在最大化与还原之间切换。</summary>
    void ToggleMaximize();

    /// <summary>是否已最大化（平台报告；X11 没有可靠同步时按最近一次切换报告）。</summary>
    bool IsMaximized { get; }
}
