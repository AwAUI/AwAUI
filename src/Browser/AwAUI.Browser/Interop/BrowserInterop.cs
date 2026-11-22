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

    // ---------------------------------------------------------------- 富剪贴板（HTML / 图片）

    [JSImport("clipboardReadHtml", ModuleName)]
    public static partial Task<string?> ClipboardReadHtml();

    [JSImport("clipboardWriteHtml", ModuleName)]
    public static partial Task<bool> ClipboardWriteHtml(string html);

    /// <summary>读取剪贴板图片并暂存在 JS 端；返回 "width,height,id"，没有图片或权限被拒绝时返回 null。</summary>
    [JSImport("clipboardReadImageInfo", ModuleName)]
    public static partial Task<string?> ClipboardReadImageInfo();

    /// <summary>把 JS 暂存的图片像素复制到 WASM 堆中 <paramref name="address"/> 处的 RGBA 缓冲。</summary>
    [JSImport("clipboardTakeImage", ModuleName)]
    public static partial bool ClipboardTakeImage(int id, int address, int length);

    /// <summary>丢弃未被取走的暂存图片（尺寸被 C# 拒绝时）。</summary>
    [JSImport("clipboardDiscardImage", ModuleName)]
    public static partial void ClipboardDiscardImage(int id);

    /// <summary>把 WASM 堆中的 RGBA 像素同步复制到 JS 暂存区；返回暂存 id（0 表示失败）。</summary>
    [JSImport("clipboardStageImage", ModuleName)]
    public static partial int ClipboardStageImage(int width, int height, int address, int length);

    /// <summary>把暂存的图片编码为 PNG 并写入剪贴板。</summary>
    [JSImport("clipboardWriteStagedImage", ModuleName)]
    public static partial Task<bool> ClipboardWriteStagedImage(int id);

    // ---------------------------------------------------------------- 窗口状态 / 生命周期 / 显示器

    [JSImport("hasFocus", ModuleName)]
    public static partial bool HasFocus();

    [JSImport("isFullscreen", ModuleName)]
    public static partial bool IsFullscreen();

    [JSImport("requestFullscreen", ModuleName)]
    public static partial Task<bool> RequestFullscreen(int handle);

    [JSImport("exitFullscreen", ModuleName)]
    public static partial Task<bool> ExitFullscreen();

    /// <summary>聚焦代理文本框并尽力把页面窗口提到前台。</summary>
    [JSImport("activateWindow", ModuleName)]
    public static partial void ActivateWindow(int handle);

    /// <summary>把 WindowOptions 中浏览器能表达的部分（最小/最大尺寸、启动位置滚动、全屏）应用到 canvas。</summary>
    [JSImport("applyWindowOptions", ModuleName)]
    public static partial void ApplyWindowOptions(int handle, int startupLocation, bool hasPosition, double x, double y,
        bool fullscreen, double minWidth, double minHeight, double maxWidth, double maxHeight);

    /// <summary>安全区（CSS env(safe-area-inset-*)），顺序为 left, top, right, bottom。</summary>
    [JSImport("safeArea", ModuleName)]
    [return: JSMarshalAs<JSType.Array<JSType.Number>>]
    public static partial double[]? SafeArea(int handle);

    /// <summary>主显示器信息："orientation|width|height|availWidth|availHeight|availLeft|availTop|devicePixelRatio"。</summary>
    [JSImport("screensInfo", ModuleName)]
    public static partial string? ScreensInfo();

    [JSImport("watchScreens", ModuleName)]
    public static partial void WatchScreens();

    // ---------------------------------------------------------------- Launcher

    /// <summary>在带 noopener,noreferrer 的新浏览上下文中打开 URI；非白名单方案或被拦截时返回 false。</summary>
    [JSImport("launcherOpen", ModuleName)]
    public static partial bool LauncherOpen(string url);

    // ---------------------------------------------------------------- 文件选择器

    /// <summary>显示隐藏的 &lt;input type=file&gt;；返回编码后的文件元数据（见 BrowserStorageCodec），取消时为空串。</summary>
    [JSImport("storageOpenFiles", ModuleName)]
    public static partial Task<string?> StorageOpenFiles(string? accept, bool multiple);

    /// <summary>显示保存对话框（或下载回退）；返回编码后的文件元数据，取消时为 null。</summary>
    [JSImport("storageSaveFile", ModuleName)]
    public static partial Task<string?> StorageSaveFile(string suggestedName);

    /// <summary>文件字节数；未知或取不到时返回 -1。</summary>
    [JSImport("storageFileSize", ModuleName)]
    public static partial Task<double> StorageFileSize(int handle);

    /// <summary>把文件内容读入 WASM 堆中 <paramref name="address"/> 处的缓冲；文件更大或读取失败时返回 false。</summary>
    [JSImport("storageReadFileInto", ModuleName)]
    public static partial Task<bool> StorageReadFileInto(int handle, int address, int length);

    /// <summary>把 WASM 堆中 <paramref name="address"/> 处的缓冲写入文件（File System Access）或触发下载。</summary>
    [JSImport("storageFinishWrite", ModuleName)]
    public static partial Task<bool> StorageFinishWrite(int handle, int address, int length);

    // ---------------------------------------------------------------- 影子 ARIA DOM

    /// <summary>
    /// 为窗口创建覆盖 canvas 的不可见 ARIA 容器；返回 false 表示页面禁用（AWAUI_BROWSER_A11Y=0）或窗口不存在。
    /// 容器用 opacity:0、pointer-events:none 隐藏，不能用 display/visibility/aria-hidden。
    /// </summary>
    [JSImport("a11yAttach", ModuleName)]
    public static partial bool A11yAttach(int handle);

    /// <summary>把 <see cref="BrowserAriaCodec"/> 编码的语义树快照交给 JS；模型非法或窗口不存在时返回 false。</summary>
    [JSImport("a11yUpdate", ModuleName)]
    public static partial bool A11yUpdate(int handle, string json);

    /// <summary>移除影子树与 DOM 监听器。</summary>
    [JSImport("a11yDetach", ModuleName)]
    public static partial void A11yDetach(int handle);

    /// <summary>投递一次宏任务（MessageChannel），回调 <see cref="BrowserCallbacks.Drain"/>。</summary>
    [JSImport("scheduleDrain", ModuleName)]
    public static partial void ScheduleDrain();

    /// <summary>performance.now()。</summary>
    [JSImport("now", ModuleName)]
    public static partial double Now();
}
