using System.Runtime.InteropServices.JavaScript;

namespace AwAUI.Browser.Interop;

/// <summary>
/// JS → C#：DOM 事件的入口（[JSExport]，JS 端经 getAssemblyExports("AwAUI.Browser") 取得）。
/// 每个方法按句柄找到窗口后交给它处理；句柄已失效（窗口关闭后仍在途的事件）时静默忽略。
/// 参数编码见 <see cref="DomProtocol"/>。
/// </summary>
internal static partial class BrowserCallbacks
{
    static BrowserWindow? Find(int handle) => BrowserPlatform.Current?.FindWindow(handle);

    [JSExport]
    internal static void OnFrame(int handle, double timeMs) => Find(handle)?.HandleAnimationFrame(timeMs);

    [JSExport]
    internal static void OnResize(int handle, double cssWidth, double cssHeight, double devicePixelRatio, double deviceWidth, double deviceHeight) =>
        Find(handle)?.HandleResize(cssWidth, cssHeight, devicePixelRatio, deviceWidth, deviceHeight);

    [JSExport]
    internal static void OnPointer(int handle, int kind, int device, int pointerId, double offsetX, double offsetY, double rectWidth,
        double rectHeight, int button, int buttons, double pressure, int modifiers, double timeMs) =>
        Find(handle)?.HandlePointer(new DomPointerSample(kind, device, pointerId, offsetX, offsetY, rectWidth, rectHeight,
            button, buttons, pressure, modifiers, timeMs));

    [JSExport]
    internal static void OnWheel(int handle, double offsetX, double offsetY, double rectWidth, double rectHeight, double deltaX,
        double deltaY, int deltaMode, double legacyWheelDeltaY, int buttons, int modifiers, double timeMs) =>
        Find(handle)?.HandleWheel(new DomWheelSample(offsetX, offsetY, rectWidth, rectHeight, deltaX, deltaY, deltaMode,
            legacyWheelDeltaY, buttons, modifiers, timeMs));

    /// <summary>返回 true 时 JS 端对该 keydown/keyup 调用 preventDefault。</summary>
    [JSExport]
    internal static bool OnKey(int handle, bool down, string code, string key, int keyCode, int modifiers, bool repeat,
        bool composing, double timeMs) =>
        Find(handle)?.HandleKey(down, code, key, keyCode, modifiers, repeat, composing, timeMs) ?? false;

    /// <summary>返回 true 时 JS 端对该 beforeinput 调用 preventDefault。</summary>
    [JSExport]
    internal static bool OnBeforeInput(int handle, string inputType, string? data, bool composing) =>
        Find(handle)?.HandleBeforeInput(inputType, data, composing) ?? false;

    /// <summary>不支持可取消 beforeinput 的浏览器的后备路径：代理文本框里出现的非组合文本。</summary>
    [JSExport]
    internal static void OnTextInput(int handle, string text) => Find(handle)?.HandleText(text);

    [JSExport]
    internal static void OnComposition(int handle, int kind, string? data, int selectionStart, int selectionEnd) =>
        Find(handle)?.HandleComposition(kind, data, selectionStart, selectionEnd);

    [JSExport]
    internal static void OnFocus(int handle, bool focused) => Find(handle)?.HandleFocus(focused);

    [JSExport]
    internal static void OnVisibility(int handle, bool visible) => Find(handle)?.HandleVisibility(visible);

    [JSExport]
    internal static void OnPaste(int handle, string text) => Find(handle)?.HandlePaste(text);

    /// <summary>copy / cut 事件：返回要写入剪贴板的文本；null 表示不处理（不阻止浏览器默认行为）。</summary>
    [JSExport]
    internal static string? OnCopy(int handle, bool cut) => Find(handle)?.HandleCopy(cut);

    [JSExport]
    internal static void Drain() => BrowserPlatform.Current?.DrainDispatcher();
}
