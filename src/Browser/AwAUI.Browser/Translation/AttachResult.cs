namespace AwAUI.Browser;

/// <summary>JS 端 attachCanvas 的结果状态。</summary>
internal enum AttachStatus : byte
{
    Ok,

    /// <summary>找不到该 id 的元素，或它不是 &lt;canvas&gt;。</summary>
    NotFound,

    /// <summary>该 canvas 已附加为另一个窗口。</summary>
    AlreadyAttached,

    /// <summary>取不到 2D 上下文（canvas 已有 WebGL / bitmaprenderer 上下文，或已 transferControlToOffscreen）。</summary>
    NoContext,
}

/// <summary>
/// 解析 JS 端 attachCanvas 返回的数字数组（编码见 wwwroot/awaui-browser.js）：
/// 成功时为 [CSS 宽, CSS 高, devicePixelRatio, 设备像素宽, 设备像素高, 上下文是否透明(0/1), 是否可见(0/1)]；
/// 失败时为 null（找不到）或 [负的错误码]。
/// </summary>
internal readonly struct AttachResult
{
    public const double ErrorAlreadyAttached = -1, ErrorNoContext = -2;
    const int Fields = 7;

    AttachResult(AttachStatus status, double cssWidth, double cssHeight, double dpr, double deviceWidth, double deviceHeight, bool alpha, bool visible)
    {
        Status = status; CssWidth = cssWidth; CssHeight = cssHeight; DevicePixelRatio = dpr;
        DeviceWidth = deviceWidth; DeviceHeight = deviceHeight; Alpha = alpha; Visible = visible;
    }

    public AttachStatus Status { get; }
    public double CssWidth { get; }
    public double CssHeight { get; }
    public double DevicePixelRatio { get; }
    public double DeviceWidth { get; }
    public double DeviceHeight { get; }

    /// <summary>
    /// 2D 上下文实际是否带 alpha。页面先以 alpha: true 取过上下文时，getContext 会返回那个已有的上下文而忽略新选项，
    /// 这时预乘帧缓冲必须反预乘后再 putImageData，否则半透明像素颜色错误。
    /// </summary>
    public bool Alpha { get; }

    /// <summary>页面当前是否可见（document.visibilityState）。</summary>
    public bool Visible { get; }

    public CanvasMetrics Metrics => CanvasMetrics.Compute(CssWidth, CssHeight, DevicePixelRatio, DeviceWidth, DeviceHeight);

    public static AttachResult Parse(double[]? values, bool requestedAlpha)
    {
        if (values is null || values.Length == 0) return Error(AttachStatus.NotFound);
        if (values.Length < Fields)
        {
            return values[0] == ErrorAlreadyAttached ? Error(AttachStatus.AlreadyAttached)
                : values[0] == ErrorNoContext ? Error(AttachStatus.NoContext)
                : Error(AttachStatus.NotFound);
        }
        // 未知 / 非法的标志值按调用方请求的透明度与「可见」处理
        bool alpha = values[5] == 1 || (values[5] != 0 && requestedAlpha);
        bool visible = values[6] != 0;
        return new AttachResult(AttachStatus.Ok, values[0], values[1], values[2], values[3], values[4], alpha, visible);
    }

    static AttachResult Error(AttachStatus status) => new(status, 0, 0, 1, double.NaN, double.NaN, false, true);
}
