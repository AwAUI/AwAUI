namespace AwAUI.Platform;

/// <summary>窗口状态（design/04「窗口状态与几何」）。</summary>
public enum WindowState
{
    /// <summary>普通（还原）状态。</summary>
    Normal,

    /// <summary>最小化。</summary>
    Minimized,

    /// <summary>最大化。</summary>
    Maximized,

    /// <summary>全屏（独占或无边界的全屏；由平台能力决定）。</summary>
    FullScreen,
}

/// <summary>首次显示窗口时如何决定位置。</summary>
public enum WindowStartupLocation
{
    /// <summary>使用 <see cref="WindowOptions.Position"/>；未给位置时由平台决定。</summary>
    Manual,

    /// <summary>在主显示器工作区居中。</summary>
    CenterScreen,

    /// <summary>在所有者窗口的中心（没有所有者时等同 <see cref="CenterScreen"/>）。</summary>
    CenterOwner,
}

/// <summary>窗口按内容调整尺寸的策略（尚未在所有后端实现；作为 API 占位，默认 <see cref="Manual"/>）。</summary>
public enum SizeToContent
{
    /// <summary>不按内容调整。</summary>
    Manual,

    /// <summary>只调整宽度。</summary>
    Width,

    /// <summary>只调整高度。</summary>
    Height,

    /// <summary>同时调整宽高。</summary>
    WidthAndHeight,
}
