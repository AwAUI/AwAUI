using System.Runtime.InteropServices.JavaScript;
using System.Threading.Tasks;

namespace AwAUI.Browser.Interop;

/// <summary>
/// C# → JS：wwwroot/awaui-browser.js 导出的函数。全部用编译期生成的 [JSImport] 封送代码，无反射。
/// 参数只用数字、布尔与字符串；每个窗口由一个整数句柄标识，JS 端用 Map 保存它的 DOM 状态。
/// </summary>
internal static partial class BrowserInterop
{
    public const string ModuleName = "AwAUI.Browser";

    /// <summary>静态 Web 资产的默认位置。JSHost.ImportAsync 的相对 URL 以 _framework/dotnet.runtime.js 为基准解析。</summary>
    public const string DefaultModuleUrl = "../_content/AwAUI.Browser/awaui-browser.js";

    /// <summary>取得运行时与本程序集的 [JSExport] 表，建立调度通道。</summary>
    [JSImport("init", ModuleName)]
    public static partial Task Init();

    /// <summary>
    /// 把句柄绑定到 id 为 <paramref name="canvasId"/> 的 &lt;canvas&gt;，挂上事件监听、ResizeObserver、代理文本框。
    /// 返回 [CSS 宽, CSS 高, devicePixelRatio, 设备像素宽, 设备像素高]；找不到 canvas 时返回 null。
    /// </summary>
    [JSImport("attachCanvas", ModuleName)]
    [return: JSMarshalAs<JSType.Array<JSType.Number>>]
    public static partial double[]? AttachCanvas(int handle, string canvasId, bool transparent, int flags);

    /// <summary>在 body 末尾创建一个给定 CSS 尺寸的 canvas，返回它的 id。</summary>
    [JSImport("createCanvas", ModuleName)]
    public static partial string CreateCanvas(double cssWidth, double cssHeight);

    [JSImport("detach", ModuleName)]
    public static partial void Detach(int handle, bool removeCanvas);

    [JSImport("requestFrame", ModuleName)]
    public static partial void RequestFrame(int handle);

    [JSImport("cancelFrame", ModuleName)]
    public static partial void CancelFrame(int handle);

    /// <summary>
    /// 把 WASM 线性内存中 <paramref name="address"/> 处的 RGBA 帧缓冲呈现到 canvas（putImageData，必要时先调整 canvas 尺寸）。
    /// JS 端直接在 HEAPU8 上建视图，跨边界不复制；返回呈现耗时（毫秒）。
    /// </summary>
    [JSImport("present", ModuleName)]
    public static partial double Present(int handle, int address, int width, int height);

    [JSImport("setCursor", ModuleName)]
    public static partial void SetCursor(int handle, string css);

    [JSImport("setTitle", ModuleName)]
    public static partial void SetTitle(string title);

    /// <summary>让代理文本框获得焦点（键盘与输入法事件都从它进入）。</summary>
    [JSImport("focus", ModuleName)]
    public static partial void Focus(int handle);

    /// <summary>移动端软键盘：通过 inputmode 切换 "text" / "none"。</summary>
    [JSImport("setTextInputEnabled", ModuleName)]
    public static partial void SetTextInputEnabled(int handle, bool enabled);

    /// <summary>把代理文本框移到光标处（逻辑坐标），让候选窗跟随光标。</summary>
    [JSImport("setTextInputRect", ModuleName)]
    public static partial void SetTextInputRect(int handle, double x, double y, double width, double height);

    /// <summary>放弃代理文本框上进行中的输入法组合（失焦再聚焦；期间的焦点与组合事件被忽略）。</summary>
    [JSImport("resetComposition", ModuleName)]
    public static partial void ResetComposition(int handle);

    [JSImport("clipboardReadText", ModuleName)]
    public static partial Task<string?> ClipboardReadText();

    [JSImport("clipboardWriteText", ModuleName)]
    public static partial Task<bool> ClipboardWriteText(string text);

    /// <summary>投递一次宏任务（MessageChannel），回调 <see cref="BrowserCallbacks.Drain"/>。</summary>
    [JSImport("scheduleDrain", ModuleName)]
    public static partial void ScheduleDrain();

    /// <summary>performance.now()。</summary>
    [JSImport("now", ModuleName)]
    public static partial double Now();
}
