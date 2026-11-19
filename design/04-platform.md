# 04 平台抽象层（PAL）

平台层只做一件事：把操作系统的窗口、输入、系统服务翻译成统一接口。它**不知道** UI 元素树，也**不知道**渲染后端，只通过 `NativeSurface` 把「可绘制的东西」交出去。

## 核心接口

```csharp
public abstract class PlatformBackend
{
    public abstract IDispatcher MainDispatcher { get; }       // 主线程消息循环 / 投递
    public abstract WindowingSystem Windowing { get; }        // 桌面：多窗口；移动/Web：单一视图
    public virtual Clipboard? Clipboard => GetService<Clipboard>();   // 第七批：虚成员，不支持时为 null
    public abstract SystemFontProvider Fonts { get; }
    public abstract PlatformSettings Settings { get; }        // 深色模式、强调色、动画偏好、双击间隔...
    public abstract Screens Screens { get; }
    public virtual T? GetService<T>() where T : class => null; // 可选能力：文件对话框、拖放、托盘、触觉反馈...
}

public abstract class WindowImpl : IDisposable   // 桌面是窗口，移动端是 Activity/UIView，Web 是 canvas
{
    public abstract NativeSurface Surface { get; }
    public abstract Size ClientSize { get; }
    public abstract double RenderScale { get; }               // DPI 缩放
    public abstract Thickness SafeAreaInsets { get; }         // 刘海、圆角、软键盘占位

    public IWindowEvents? Events { get; set; }                // 平台 → 框架 的回调入口
    public virtual TextInputMethod? TextInput => null;        // IME（第七批五个平台都已实现）
    public virtual Clipboard? Clipboard => null;              // 同平台的剪贴板，只持有窗口的宿主经它访问
    public virtual AccessibilityBridge? Accessibility => null;

    public abstract void RequestFrame();                      // 请求下一次 vsync 回调
    public abstract void SetCursor(CursorShape cursor);
    public abstract void Invalidate(in Rect rect);

    public virtual T? GetFeature<T>() where T : class => null; // 桌面特有：标题、最小化、无边框、置顶、弹出窗...
    public abstract void Dispose();
}

public interface IWindowEvents                   // 只由框架实现，平台调用
{
    void OnFrame(TimeSpan timestamp);            // vsync 驱动帧管线
    void OnResized(Size size, double scale);
    void OnPointer(in RawPointerEvent e);        // 鼠标/触摸/笔 统一为 Pointer
    void OnKey(in RawKeyEvent e);
    void OnTextInput(string text);               // 没有输入法客户端时的文本
    void OnFocusChanged(bool focused);
    void OnSurfaceLost();  void OnSurfaceRestored();
    bool OnCloseRequested();
    void OnLifecycle(AppLifecycle state);        // 移动端前后台
}
```

平台实现要派生的类型（`PlatformBackend`、`WindowImpl`、`TextInputMethod`、`AccessibilityBridge` 等）是抽象类，以后可以追加带默认实现的成员而不破坏已有平台；`IWindowEvents`、`ITextInputClient` 由框架自己实现，保留为接口（见 [11](11-compatibility.md)）。

设计要点：

- **输入统一为 Pointer 模型**（W3C Pointer Events 思路）：`pointerId / type / position / pressure / buttons / 时间戳`，滚轮与触控板手势是独立事件。平台层须原样上交多点触摸的全部采样（含系统合并的历史点与预测点），重采样与手势识别在上层完成，见 [09](09-touch-gestures.md)。
- **核心接口最小化 + 特性查询**：只有所有平台都有的东西进 `WindowImpl`；桌面窗口装饰、移动端状态栏等通过 `GetFeature<T>()`，避免接口上出现大量「此平台不支持」。
- **帧节拍来自平台**：`RequestFrame()` → `OnFrame()`。各平台分别对接 DWM / CVDisplayLink / CADisplayLink / Choreographer / requestAnimationFrame / Wayland frame callback。
- **键盘**：同时提供物理键（scancode → 统一 `PhysicalKey`）与逻辑键（按当前布局）；文本输入一律走 `OnTextInput` 或 IME，不从按键推断字符。

### 键盘（第七批实现）

- `RawKeyEvent.LogicalKey`：W3C `KeyboardEvent.key` 拼写的逻辑键，含 Shift 与 AltGr 的效果、不含 Ctrl / Alt / Meta 的效果，死键为 `Dead`；超过 `KeyValues.MaxLength`（32）的值视为未知（null）。旧构造函数保留，已有调用方不用改。`RawKeyEvent.Location`（`KeyLocation { Standard, Left, Right, Numpad }`）由物理键推出。`KeyValues` 提供常量、`IsCharacter`、`FromPhysicalKey`（平台不提供逻辑键时按 US 推断）、`NamedKey`。
- `KeyModifiers.AltGraph = 16`：Ctrl 与 Alt 同时按下且该状态下产生字符时报告 `AltGraph`，并去掉由它模拟出来的 Ctrl 与 Alt；不产生字符的 Ctrl+Alt 照常报告。判定都在平台层：Win32 用 `ToUnicodeEx`，Browser 用 `getModifierState('AltGraph')` 或「Ctrl+Alt 加上 key 是字符」（排除单个 ASCII 字母和数字，见 Web 一节），X11 是 Mod5。UI 只看 `IsPrimaryShortcut` 与精确的修饰键匹配。
- `PhysicalKey` 在枚举**末尾**追加了 NumLock、Numpad0–9 与小键盘运算键（Add/Subtract/Multiply/Divide/Decimal/Enter/Equal/Comma）、IntlBackslash/IntlRo/IntlYen、PrintScreen/ScrollLock/Pause、F13–F24；辅助类 `PhysicalKeys`（`Last`、`GetLocation`、`IsNumpad`、`IsModifier`）。
- 被输入法处理的按键一律不交给框架：Win32 的 `VK_PROCESSKEY`、macOS 中被 `inputContext` 处理（组合中或这一键改变了预编辑）的、X11 中被 `XFilterEvent` 过滤的、Browser 中 `isComposing` 或 keyCode 229 的。与 W3C 的 `isComposing` 语义一致，回车确认候选时不会顺便提交表单。

### 滚轮与触控板（第八批实现）

- `RawPointerEvent` 的滚轮事件带增量（逻辑像素；正值 = 内容向右 / 向下移动，即向左 / 向上滚动，与 DOM 的 deltaX / deltaY 符号相反）、设备报告增量的单位 `WheelDeltaMode { Pixel, Line, Page }`、`PreciseWheel`、触控板手势相位 `ScrollPhase` 与系统惯性相位 `MomentumPhase`（`ScrollPhase { None, MayBegin, Began, Changed, Ended, Cancelled }`，惯性只用 Began / Changed / Ended）。用 `RawPointerEvent.CreateWheel(...)` 构造；旧构造函数保留，单位与相位取默认值。平台已按系统设置把行 / 页换算成像素；Page 增量按窗口客户区的一页换算，框架再按滚动容器自己的页尺寸重新换算。
- `WheelDeltas`（`PixelsPerLine`、`DefaultLinesPerNotch`、`MaxDelta`、`Sanitize`、`NotchesToPixels`）：默认每格 3 行 = 100 px（Chromium 约定）；NaN、无穷与超大值钳制。
- `WindowImpl.PreferredScrollbarStyle`（虚属性，默认 `Overlay`）：macOS 跟随 `[NSScroller preferredScrollerStyle]`（「显示滚动条」设置，只在宿主创建时读一次），Win32 与 X11 为 `Classic`，Headless 可设置（`HeadlessWindow.ScrollbarStyle`、`HeadlessPlatform.DefaultScrollbarStyle`）。
- 各平台：

| 平台 | 实现 | 验证 |
|---|---|---|
| macOS | `scrollingDeltaX/Y`、`hasPreciseScrollingDeltas`、`phase` 与 `momentumPhase`；非精确的行增量每行 40 px（系统已加速，WebKit 约定）；纯逻辑在 `MacWheel`（暂放在 `KeyMap.cs` 末尾以沿用已有的源码链接） | 普通与精确滚轮经真实 `NSEvent`（`eventWithCGEvent` 后直接调用视图的 `scrollWheel:`）验证；手势相位与惯性相位只有单元测试，没有注入过真实触控板手势 |
| Win32 | `WM_MOUSEWHEEL`（正值向上）/ `WM_MOUSEHWHEEL`（正值向右，取反），屏幕坐标经 `ScreenToClient`；每格行数来自 `SystemParametersInfo`，`WM_SETTINGCHANGE` 后重读；非 120 整数倍的增量（高分辨率滚轮、精确式触控板）按精确处理、没有相位；`WHEEL_PAGESCROLL`（一次一屏）累加满一格才发一页；没用 DirectManipulation | **只编译、纯逻辑测试与逐行阅读**，没有在 Windows 上运行；触摸以模拟鼠标到达，框架的触摸拖动滚动在这里不适用 |
| X11 | 按钮 4–7 的按下，每格 100 px；XInput2 平滑滚动未做（原因写在代码注释里） | **只编译与纯逻辑测试** |
| Browser | `deltaMode` 映射为 Pixel / Line / Page，每行 40 px | 单元测试（`WheelModeTests`） |
| Headless | `HeadlessWindow.WheelNotches(...)`、`TrackpadScroll(...)` 脚本化滚轮与触控板手势 | 单元测试 |

## IME / 文本输入

自绘框架中最易翻车的部分，接口按「最复杂的平台」设计（Android `InputConnection` / iOS `UITextInput` / macOS `NSTextInputClient` 都要求平台能**反向查询**文档内容）：

```csharp
// 第七批落地的形态（src/AwAUI.Platform/TextInput.cs、Clipboard.cs）
public abstract class TextInputMethod            // 每个窗口一个，经 WindowImpl.TextInput 取得
{
    public ITextInputClient? Client { get; }
    public void SetClient(ITextInputClient? client);                 // 不可重写：焦点进入/离开可编辑元素
    protected abstract void OnClientChanged(ITextInputClient? oldClient, ITextInputClient? newClient);
    public virtual void NotifyStateChanged() { }                     // 选区/内容/光标矩形变化
    public virtual void ResetComposition() { }                       // 放弃平台侧的组合状态
    public virtual void Show() { }                                   // 软键盘
    public virtual void Hide() { }
    public virtual bool HandlesClipboardShortcuts => false;          // 平台自己经浏览器事件处理 Mod+C/X/V
}

public interface ITextInputClient                // 由框架（UI 的可编辑元素）实现，平台只调用
{
    TextInputOptions Options { get; }            // ContentType（Text/Password/Number/Email/Url/Search/Phone）、Multiline、AutoCorrect
    int Length { get; }                          // 文档长度（含预编辑文本）
    TextInputRange Selection { get; set; }
    TextInputRange? Composition { get; }
    string GetText(TextInputRange range);
    Rect GetCaretRect();                         // 候选窗定位（窗口客户区逻辑坐标）
    Rect GetTextBounds(TextInputRange range);    // 范围第一行的包围矩形
    void SetComposition(string text, TextInputRange selection);   // 空文本 = 取消组合
    void Commit(string text);
    void ReplaceRange(TextInputRange range, string text);
}

public abstract class Clipboard                  // 全异步
{
    public const int MaxTextLength = 1 << 24;    // 16M 码元
    public abstract Task<string?> GetTextAsync();
    public abstract Task<bool> SetTextAsync(string text);
    public virtual Task<bool> ClearAsync();
    public static string LimitLength(string text, int max = MaxTextLength);
    public static string NormalizeNewlines(string text);
}
```

与早先草案的差别：范围类型叫 `TextInputRange`（不叫 `TextRange`：平台层不引用 `AwAUI.Text`，而 UI 代码常同时用到两者）；文本参数用 `string` 而不是 `ReadOnlySpan<char>`，与 `IWindowEvents.OnTextInput` 一致；`SetClient` 不可重写，平台实现 `OnClientChanged`，其余成员都是带默认实现的虚成员。`ITextInputClient` 与 `IWindowEvents` 同类（框架实现、平台调用），所以保留为接口并登记在 `ArchitectureTests` 的允许列表里（决策 D34，见 [README](README.md)）。

约定（所有平台相同）：

- **文档模型**：客户端的文档包含预编辑文本（与 `NSTextInputClient`、Android 一致），`Selection`、`Composition` 与各方法的范围都是文档中的 UTF-16 下标。
- **文本路由**：有客户端时，普通按键产生的字符与输入法提交的文本都走 `client.Commit`，组合走 `SetComposition`，自动更正、删除光标周围文本走 `ReplaceRange`；没有客户端时文本经 `IWindowEvents.OnTextInput` 送达（不用框架的应用不受影响）。
- **失焦**：框架按原样提交预编辑（与 Chrome 一致），然后调用 `ResetComposition` 放弃平台侧状态；受控值被程序改变时直接放弃组合。
- **剪贴板**：`PlatformBackend.Clipboard`（默认经 `GetService<Clipboard>()`，即 null）与 `WindowImpl.Clipboard` / `TextInput` 都是虚成员、不支持时为 null（不是 abstract）。桌面平台同步完成并返回已完成的任务，从其他线程调用时投递到 UI 线程。框架的剪切在**已知**写入失败时（同步返回 false、已失败的任务或同步异常）不删除选区；写入尚未完成时照常删除（与浏览器一致），之后若失败文本仍会丢失。
- **不可信输入的上限**：剪贴板文本 16M 码元（单个输入框的值同样受此上限）；预编辑 4096（UI、Win32、X11）；X11 读取剪贴板 64 MB、每步超时 2 s、同时发送的 INCR 传输至多 16 个、每个 5 s 超时；macOS 的 `attributedSubstringForProposedRange:` 一次至多 64K；Win32 读剪贴板以 `GlobalSize` 为界；页面送来的 DOM 逻辑键超过 32 个码元或含控制字符时丢弃。
- **无头平台**：`HeadlessTextInput`（可脚本化：`Compose`、`CommitComposition`、`CancelComposition`、`ReplaceRange`，并记录通知次数、最近的光标矩形、重置次数）、内存剪贴板 `HeadlessClipboard`、模拟键盘布局 `HeadlessKeyboardLayout`（US，以及带 AltGr 层和死键的德语布局）；`HeadlessWindow.TypeKey / TypeText` 按真实平台的规则投递按键与文本（Ctrl/Alt/Meta 组合不产生文本），可以端到端测试「德语 AltGr+Q 输入 @，不触发 Ctrl+Alt+Q」。

### 各平台的输入法与剪贴板（第七批）

| 平台 | 输入法 | 逻辑键 | 剪贴板 | 验证 |
|---|---|---|---|---|
| Win32 | IMM32：有客户端时关联输入上下文，没有客户端或是密码框时解除关联；框架自绘预编辑（`WM_IME_SETCONTEXT` 去掉 `ISC_SHOWUICOMPOSITIONWINDOW`，按 32 位无符号数清位，避免 32 位进程上溢出；`WM_IME_START/COMPOSITION/ENDCOMPOSITION` 自己处理，不交给 `DefWindowProc`，解析 `GCS_COMPSTR/CURSORPOS/COMPATTR` 的转换文节与 `GCS_RESULTSTR`）；`ImmSetCompositionWindow` / `ImmSetCandidateWindow`（`CFS_POINT` / `CFS_EXCLUDE`）定位；`WM_CHAR` 的代理对组装在 `Utf16Assembler` | `ToUnicodeEx(flags = 4)`，并据此识别 AltGr | `CF_UNICODETEXT`（失败重试、换行转换、长度受 `GlobalSize` 约束） | **只编译与纯逻辑测试**，没有在 Windows 上运行 |
| macOS | 视图遵循 `NSTextInputClient`（`insertText:replacementRange:`、`setMarkedText:…`、`unmarkText`、`selectedRange` / `markedRange`、`attributedSubstringForProposedRange:`、`firstRectForCharacterRange:` 换算到屏幕坐标、`characterIndexForPoint:`、`doCommandBySelector:`）；`keyDown:` 交给 `inputContext` 的 `handleEvent:`；放弃组合（`discardMarkedText`）期间忽略输入法的同步回调（防御性） | 键码表补了小键盘、ISO/JIS 键与逻辑键 | `NSPasteboard` | scratchpad 中的 AppKit 探针（25 项）；**只部分验证**，见下 |
| X11 | 服务器支持 `XIMPreeditCallbacks` 时用 on-the-spot（Start/Draw/Caret/Done 经 `XimPreeditState` 交给客户端；Draw 中 `XIMText` 的 string 为 NULL 而 feedback 不为 NULL 时只改高亮），否则退回 Root 风格；`XNSpotLocation` 定位候选窗，`Xutf8ResetIC` 放弃组合 | keysym 转换（`X11Keysyms`），Mod5 → AltGraph | CLIPBOARD 选择由隐藏的 InputOnly 窗口服务：`X11SelectionServer` 支持 TARGETS / UTF8_STRING / `text/plain;charset=utf-8` / TEXT / STRING，大数据走 INCR；`X11SelectionReader` UTF8 失败时退回 STRING、支持 INCR 接收；超时检查接入事件循环 | **只编译与纯逻辑测试**；选择协议用两个客户端加内存假 X 服务器端到端测试，没有连过真实 X 服务器 |
| Browser | 见下文 Web 一节 | `KeyboardEvent.key` | 浏览器事件 | 真实无头 Chromium |
| Headless | `HeadlessTextInput`（见上） | `HeadlessKeyboardLayout` | `HeadlessClipboard` | 单元测试 |

与系统 API 无关的逻辑放在各平台的 `Input/` 目录（macOS 是 `KeyMap.cs`），在 `tests/AwAUI.Platform.UnitTests`（第八批从 `Browser.Tests/Platforms/` 拆出）以源码链接方式编译，任何系统（含 net48）都能跑；第八批的滚轮换算（`WheelInputTests`）也在这里。`Browser.Tests` 只保留 `Translation/` 的测试。

**未验证 / 已知限制**：

- **Win32**：IMM32、`ToUnicodeEx`、`CF_UNICODETEXT` 都没有在 Windows 上运行过。`ToUnicodeEx` 的 flags = 4 需要 Windows 10 1607+，更早的系统可能消耗死键状态；AltGr 按下时系统发出的假左 Ctrl 按键会作为一次 Control 按下交给框架（没有像 Chromium 那样过滤）；没有客户端而输入法开着时系统显示默认的组合窗；没有 NUL 结尾又刚好截在容量处时读剪贴板可能留下孤立的高代理。
- **X11**：XIM on-the-spot 回调（`XIMText` 解码假定区域设置为 UTF-8）、`XNSpotLocation`、`Xutf8ResetIC`、CLIPBOARD 的 Xlib 实现（含 LP64 上 32 位格式属性按 long 读写）都没有在 Linux 上运行过。可变参数的 `XCreateIC` / `XVaCreateNestedList` / `XGetIMValues` / `XSetICValues` 按固定形状声明，依赖 x86_64 与 AAPCS64 的可变参数约定。只实现了 CLIPBOARD，没有 PRIMARY（中键粘贴）与 MULTIPLE。没有客户端或是密码框时输入上下文仍有焦点（没有 `XUnsetICFocus`），输入法照常组合但预编辑不显示，与 Win32 不一致。同一请求者对同一属性重复发起 INCR 时第二个传输只能等超时。预编辑插入是 O(n²)（n ≤ 4096）。
- **macOS**：只用探针部分验证 —— 真实 `NSEvent` 的 `keyDown:` 经 `handleEvent:` 到达 `insertText:`；`setMarkedText` / `unmarkText` / `firstRect` 是直接发送消息调用的；**没有切换到真实的拼音或日语输入法**，所以 discard 期间真实输入法是否回调没有验证。剪贴板往返用私有命名剪贴板，`generalPasteboard` 只走了同一段代码。探针在 scratchpad 中，需手动运行（仍没有 macOS 自动化测试项目）。`flagsChanged:` 仍被忽略（单独按修饰键不产生按键事件）；Cocoa 的 Emacs 键绑定（`doCommandBySelector:`，如 Ctrl+A/E）被忽略；`characterIndexForPoint:` 返回 `NSNotFound`；重新转换只支持 `replacementRange` 的基本情形；密码框不经输入法，所以死键不起作用。
- 真实 IME 与剪贴板在 Windows 与 Linux X11 运行器上的冒烟测试是后续事项（见 [07](07-roadmap.md)）。

## 各平台实现方式

互操作统一约定（P/Invoke 只用 blittable 签名、原生回调经 `NativeCallback` 封装、COM 走裸 vtable）见 [11-compatibility.md](11-compatibility.md)，使同一份平台代码可同时用于 .NET Framework 与现代 .NET。

| 平台 | 程序集 / TFM | 互操作方式 | 表面 | 备注 |
|---|---|---|---|---|
| Windows | `AwAUI.Win32` / 通用 TFM | blittable P/Invoke → user32/gdi32/imm32/dwmapi/shcore | HWND → D3D11 swapchain / DIB | IME 用 TSF 或 IMM32（先 IMM32）；a11y 用 UIA |
| macOS | `AwAUI.MacOS` / 通用 TFM | 裸 `objc_msgSend` + 运行时注册 ObjC 类 | NSView + CAMetalLayer | 不依赖 net-macos workload，可 NativeAOT；见 D13。软件呈现（CGImage → layer contents）与 CAMetalLayer 呈现（第六批）都已实现，由渲染后端第一次使用的表面能力决定，见下文 |
| Linux X11 | `AwAUI.X11` / 通用 TFM | libX11 / XInput2 / Xrandr / XIM 或 IBus(DBus) | X Window → EGL / XShm | |
| Linux Wayland | `AwAUI.Wayland` / 通用 TFM | libwayland-client + 协议绑定（由 XML 源生成） | wl_surface → EGL / wl_shm | 客户端装饰需自绘（正好自绘）；text-input-v3 |
| Android | `AwAUI.Android` / 现代 .NET 的 android TFM | .NET for Android (JNI 绑定) | SurfaceView → EGL / ANativeWindow | Activity 宿主；InputConnection；生命周期与 surface 丢失 |
| iOS | `AwAUI.iOS` / 现代 .NET 的 ios TFM | .NET for iOS 绑定 | UIView + CAMetalLayer | UITextInput；安全区；必须 AOT |
| Web | `AwAUI.Browser` / `net10.0-browser` | `JSImport/JSExport` + 小段 JS 胶水 | `<canvas>` → WebGL2 / 2D | 见下；目前只有软件呈现 |
| Headless | `AwAUI.Headless` / 通用 TFM | 无 | 内存位图 | 测试、服务端渲染截图、CI |

运行时选择：`UsePlatformDetect()` 在桌面 TFM 下根据 OS 选择 Win32 / MacOS / Wayland→X11 回退；移动端与 Web 由各自的宿主项目头显式引用。

### D13：Apple 平台互操作（已定）

- macOS 桌面：`net10.0-macos` workload 带来完整绑定，但绑定体积大、构建链重。裸 `objc_msgSend` 方案只需要我们实际用到的几十个选择子，产物是普通的通用 TFM 程序集，在现代 .NET 上可 NativeAOT 单文件发布。桌面走裸互操作，与「尽可能纯 C#、少依赖」一致。
- iOS：应用打包、签名、AOT 都依赖 `net10.0-ios` 工具链，没有绕开的价值，直接用 workload。
- 两者共享 Metal RHI 实现（Metal 调用本身也走 `objc_msgSend`，与 workload 无关）。

### macOS 的 GPU 呈现（第六批）

- **模式选择**：窗口表面（内部类 `MacSurface`）同时实现 `IFramebufferSurface` 与 `IMetalLayerSurface`，窗口不在 `WindowOptions` 或平台上加开关，而是看后端用了哪种能力：软件后端锁定帧缓冲即软件模式，GPU 后端读取 `MetalLayer` 即 GPU 模式（`MacWindow.PresentationMode`，枚举 `MacPresentationMode { Framebuffer, MetalLayer }`）。`UIHost` 在窗口 `Show` 之前就用 `backend.CreateRenderTarget(window.Surface)` 建好目标，所以模式在第一帧之前确定，应用不用做任何设置。读取 `MetalLayer` 时检查主线程；没有 CAMetalLayer 时 `GetFeature<IMetalLayerSurface>()` 返回 null。
- **层**：CAMetalLayer 作为**子层**铺满视图的后备层（`contentsGravity` 为 TopLeft，视图的 `layerContentsRedrawPolicy` 设为 Never），colorspace 标为 sRGB —— 不标记时同样的颜色在 P3 屏上比软件模式更艳（软件模式的 CGImage 标记了 sRGB，会被色彩管理），这是截取真实窗口时发现的。最初把视图自身的层换成 CAMetalLayer，但回退到软件后 `drawRect:` 只再被调用一次，所以改为子层：回退时移除子层、恢复重绘策略即可。层的 `contentsScale` 跟随 `backingScaleFactor`。
- **出帧**：GPU 模式的帧由节拍源（CADisplayLink / CVDisplayLink）直接驱动，不经过 `drawRect:`；没有节拍源时投递到消息循环。`windowDidResize:` 与 `windowDidChangeBackingProperties:` 时同步出帧；`windowWillStartLiveResize:` 到 `windowDidEndLiveResize:` 之间层设为 `presentsWithTransaction`，新帧与新尺寸在同一个 CA 事务里上屏，新帧来不及画时内容贴左上角而不拉伸。平台层与 Metal 实现只通过 CAMetalLayer 本身沟通，平台层不引用 Metal。不可见（遮挡、最小化）时停止出帧，沿用原有机制。
- **评审修复**：每帧（两种模式共用的 `DispatchFrame`）包一层 `objc_autoreleasePoolPush/Pop`，GPU 模式的帧不在 AppKit 的绘制周期里，否则自动释放的对象要等消息循环排空外层池；尺寸通知在 `OnFrame` 内部到达时只登记 `RequestFrame`，不嵌套调用 `RunFrame`；GPU 模式下窗口从不可见恢复可见时，以当前尺寸调用一次 `IWindowEvents.OnResized`，要求宿主整帧重呈现 —— 不可见期间合成器可能不归还可绘制纹理，`nextDrawable` 等满 1 秒超时后返回 nil，那一帧被丢弃（尺寸没变时 `SetViewport` 直接返回，开销只是一帧 GPU 渲染）。这是权宜做法。第七批起渲染目标经 `RenderTarget.LastFramePresented` 上报丢帧，`UIHost` 在下一个节拍整帧重画（见 [03](03-renderer.md)、[05](05-ui-core.md)），所以这个变通在第八批**删除**（`UpdateOcclusion` 重新可见时只恢复节拍）。删除后在真实 Metal 窗口里用 `MacWindow.CaptureContent` 验证：最小化后恢复、被其他窗口遮挡后移开，三轮都显示最新内容；`DroppedFrames` 始终为 0，所以丢帧后整帧重画的路径仍只有无头的假目标测试。负载约 50 时 macOS 的「重新可见」通知曾迟到 1.5 s 以上，这发生在任何出帧之前，删掉的变通同样只响应这个通知。
- **回退**：后端回退到软件时，软件目标锁定帧缓冲，窗口随之移除子层、回到 `drawRect:` 呈现，事件处理与软件路径不受影响。
- **诊断**：`MacWindow.InLiveResize`；`MacWindow.CaptureContent()` 用 `CGWindowListCreateImage` 截取本进程窗口经窗口服务器合成后的内容区（RGBA8 预乘、转换回 sRGB；不需要屏幕录制权限）。该 API 在新 SDK 中已标记弃用，不可用时返回 null，自检会说明「不可用」而不是失败。Counter 退出时打印节拍源、节拍数、实际出帧数、刷新间隔与是否被遮挡。

**已知限制**：

- 被**其他进程**的窗口盖住、而遮挡通知还没到达时，GPU 模式的 `nextDrawable` 可能阻塞主线程 1 秒（`allowsNextDrawableTimeout` 的固定超时，实测一次 1001 ms）；同进程窗口遮挡与最小化复现不出来。根治需要按 `addPresentedHandler:` 自行限制在途帧数（需要 block 互操作），或把呈现放到渲染线程。
- ~~可见状态下 `nextDrawable` 若超时（罕见），丢掉的帧要等下一次状态变化才补上~~ —— 第七批：`UIHost` 按 `LastFramePresented` 在下一个节拍重画（只用模拟丢帧的假目标做了无头测试，没有在真实窗口中验证最小化、遮挡、恢复）。
- 没有节拍源（`AWAUI_MAC_FRAME_CLOCK=none`）时 GPU 模式只靠 `nextDrawable` 阻塞限速，Counter 在 60 Hz 显示器上约 105 fps。

**验证范围**：只在 Apple M5 / macOS 27.2（arm64）上运行过，x86_64 Mac 与 macOS 14 以前（只有 CVDisplayLink）没有测试（用 `AWAUI_MAC_FRAME_CLOCK=cvdisplaylink|none` 强制过另外两种节拍）。**拖动缩放没有人工拖过**，只用向窗口代理发送 `windowWillStart/DidEndLiveResize:` 加程序化 `setContentSize:` 再截取真实窗口的探针验证；重新可见后的内容也只由探针验证。`AwAUI.MacOS` 没有自动化测试项目（AppKit 要求主线程，xunit 不在主线程上运行）。窗口帧率与耗时都在高负载、且常被其他进程窗口遮挡的机器上测得，只能当量级参考（数字见 [07](07-roadmap.md) 第六批）。

### Web 平台的特殊性

- **实测**（M0）：软件渲染约为原生的 19 倍，Web 的常规路径应是 WebGL2。jiterpreter 是必需的（比纯解释器快 3–10 倍）。软件呈现可以零拷贝：在 WASM 堆上直接构造指向 `Bitmap` 地址的 `ImageData`（内存增长后需重建）；注意 `ImageData` 是非预乘的，非不透明内容需要反预乘或改用 WebGL 上传。

- **线程**：默认单线程；帧管线全程跑在 `requestAnimationFrame` 回调内。多线程 WASM 需要跨源隔离头，作为可选优化。
- **IME**：canvas 无法直接接收输入法。标准做法是维护一个隐藏的 `<textarea>`/`contenteditable` 并跟随光标定位，监听 composition 事件转发给 `ITextInputClient`。
- **无障碍**：canvas 对读屏器不可见，需要在 canvas 上方维护一棵与语义树同步的不可见 ARIA DOM。
- **剪贴板 / 文件**：异步且受用户手势限制，`Clipboard` 接口必须全异步。
- **字体**：浏览器不开放系统字体文件。必须随应用分发或按需下载字体（含 CJK 回退字体的分片加载策略），`SystemFontProvider` 在 Web 上由应用配置的字体清单实现。
- **包体**：AOT + 裁剪后仍然偏大，需要对 Unicode 数据表、编解码器做可裁剪的特性开关。
- **F12 冲突**：浏览器自己占用 F12，见 [06-devtools.md](06-devtools.md)。

#### 实现（`AwAUI.Browser`，第五批）

只编译 `net10.0-browser`（理由见 [11](11-compatibility.md)）。项目用 `Microsoft.NET.Sdk.Razor` 只是为了把 `wwwroot/awaui-browser.js` 作为静态 Web 资源发布（应用里位于 `_content/AwAUI.Browser/`），没有 Razor 组件，也不依赖 ASP.NET；不需要 wasm-tools 工作负载。只引用 `AwAUI.Platform`。

| 方面 | 实现 |
|---|---|
| 入口 | `BrowserPlatform.CreateAsync()` 加载 JS 胶水；`AttachCanvas(canvasId)` 把页面已有的 `<canvas>` 作为一个 `BrowserWindow`，`CreateWindow` 则在 `<body>` 末尾新建 canvas。`Run()` 立即返回（浏览器拥有事件循环），`RunAsync()` 在 `Exit()` 时完成；示例的 `main.js` 用 `dotnet.create()` + `runMain()`（`dotnet.run()` 会在 Main 返回时关闭运行时）。调度器在 `MessageChannel` 任务里排空队列 |
| 互操作 | 只有 `[JSImport]` / `[JSExport]`，无反射；跨边界只传数字与短字符串。坐标、按钮、滚轮、DPR、键码、preventDefault 策略、IME 状态、帧调度、反预乘等转换全部是 `Translation/` 下的普通 C#，可在浏览器外单元测试；有测试比对 JS 与 C# 两侧的常量、函数名与参数个数 |
| 呈现 | 帧缓冲位于 WASM 线性内存，JS 在 `HEAPU8.buffer` 上缓存一个 `ImageData`（内存增长或地址、尺寸变化时重建），帧缓冲解锁提交时立即 `putImageData`，C# 到 JS 没有逐帧拷贝。不透明 canvas（`alpha:false`）直接显示预乘像素；透明 canvas 先反预乘到暂存位图。是否反预乘取决于 2D 上下文实际的 alpha（`getContextAttributes`），而不是请求的选项。canvas 的 `width/height` 只在呈现新尺寸的帧时才改，避免缩放时闪白 |
| 尺寸与 DPR | ResizeObserver（优先 `device-pixel-content-box`）+ `resolution` 媒体查询监听 DPR；与 CSS 尺寸 × DPR 不符的设备像素尺寸被忽略；canvas 每边 ≤ 16384、总计 ≤ 32 Mpx，超出时降低有效缩放。canvas 不能有 CSS 边框或内边距 |
| 帧时钟 | 至多一个 `requestAnimationFrame` 回调，且只在有帧请求、canvas 可见时存在；页面隐藏（Page Visibility）或 canvas 滚出视口（IntersectionObserver）时暂停，迟到的回调被忽略，所以空闲页面真正空闲（Chromium 中验证回调计数不再增长） |
| 指针 | Pointer Events（鼠标、触摸、笔），指针捕获、合并采样（`getCoalescedEvents`）、组合按键、滚轮符号与其他平台一致；默认阻止右键菜单 |
| 键盘 | `KeyboardEvent.code` → `PhysicalKey`（第七批起小键盘、Intl 键与 F13–F24 有了真实的 `PhysicalKey`）；`KeyboardEvent.key` 作为 `RawKeyEvent.LogicalKey`（超过 32 个码元或含控制字符时丢弃），`BrowserWindow.CurrentKeyValue` 保留 |
| 文本与 IME | 键盘焦点放在一个隐藏、透明、跟随光标的 `<textarea>` 上（浏览器只对可编辑元素派发 `beforeinput` 与 composition 事件）；`BrowserTextInput.SetCaretRect` 移动它使候选窗跟随光标。普通输入取自可取消的 `beforeinput`，不可取消时改用 `input`；composition 事件整理成干净的开始 / 更新 / 结束序列（`TextCompositionEvent`），第七批起 `BrowserTextInput` 派生自 `TextInputMethod`：有客户端时组合、提交与粘贴 / 复制 / 剪切都经客户端完成，没有客户端时提交的文本经 `OnTextInput` 送达；JS 新增 `resetComposition`（失焦再聚焦，期间的事件与重置后、新的 `compositionstart` 之前迟到的 `compositionend` 被忽略）；有客户端时 `beforeinput` 的 `deleteContentBackward/Forward` 与 `insertParagraph` 变成客户端编辑或合成的回车按键（修复移动端退格与回车被丢弃，**没有真机验证**）；组合中的按键（`isComposing` / keyCode 229）不转给应用；Firefox、Safari 在 `compositionend` 之后多发的 input 被忽略。移动端软键盘用 `inputmode` text / none 控制（`Show / Hide`） |
| preventDefault 策略 | 产生文本的键从不 preventDefault（否则 `beforeinput` 与 IME 事件不会触发）；Tab、Enter、Esc、退格、Delete、Insert、Home/End、翻页与方向键总是阻止；Ctrl/⌘ 加 C / V / X / R、F5、F12 与打开浏览器开发者工具的组合键交给浏览器，从而 copy / cut / paste 事件照常触发、刷新与浏览器 DevTools 可用；其余带 Ctrl/⌘ 的组合归框架，不带的（含 F1–F11）交给浏览器。**AltGr**：Windows 上浏览器把 AltGr 报告为 Ctrl+Alt，所以 Ctrl+Alt（或 `getModifierState('AltGraph')`）且未按 Meta、`key` 为单个可打印字符或 `Dead` 时不阻止，字符经 `beforeinput` 到达；第七批起按键事件以 `AltGraph` 修饰转发（`ToKeyModifiers`，去掉模拟出来的 Ctrl/Alt）。没有 `AltGraph` 状态、只凭 Ctrl+Alt 推断时排除单个 ASCII 字母和数字（`KeyboardPolicy.IsAltGraphText`）：浏览器对不产生字符的 Ctrl+Alt 组合报告去掉修饰键后的键值（US 布局 Ctrl+Alt+A 的 key 是 "a"），它是快捷键。这假设没有布局会在 AltGr 层产生单个 ASCII 字母或数字；报告 AltGraph 状态的浏览器不受影响 |
| 剪贴板 | 两条路径：异步 `navigator.clipboard`（需要权限）与 copy / cut / paste 事件（总是可用；`CopyHandler` 同步提供内容，`Pasted` 接收粘贴，没有订阅者时粘贴文本经 `OnTextInput` 送达，CRLF 变为 LF）。第七批起 `BrowserClipboard` 派生自 `Clipboard`，原有 API 保留；剪贴板快捷键经浏览器的 copy / cut / paste 事件交给客户端（不需要权限），`HandlesClipboardShortcuts` 为 true，UI 因此不再处理这些快捷键，避免重复粘贴 |
| 不可信输入 | 页面脚本可以伪造事件，所以全部清洗：坐标限制在 ±1e7，NaN / ∞ 取安全默认值，文本有长度上限（组合 4096 字符、单次提交 1M 字符，不拆代理对） |

实测与验证范围见 [07](07-roadmap.md) 第五批：呈现约 0.07 ms（1280×880），整帧约 104 ms，几乎全是软件光栅。只在 Chromium 153 与 Edge 157 的无头模式下验证过（IME 用 CDP 模拟）；Firefox、Safari、移动浏览器、真实系统输入法与真实 DPR 变化事件都未验证。第七批的输入改动在真实无头 Chromium（chrome-headless-shell 1243，冒烟 48 项）与 Node（57 项）中验证：经客户端的组合、提交、粘贴、AltGr 与组合重置；但去掉 JS 的重置保护后「组合被放弃」检查仍然通过，说明无头 Chromium 在这个场景下不会补发 `compositionend`，这个保护是否必要没有被证明。代理是 textarea，所以浏览器不会像对真正的密码框那样关闭输入法，客户端只是忽略密码框的预编辑。已知缺口：笔的倾角 / 旋转 / 橡皮擦与预测采样未上交；`beforeunload` 未接 `OnCloseRequested`；没有生命周期事件；帧缓冲接口没有脏矩形，所以总是整帧 `putImageData`；多线程 WASM 不支持（`ImageData` 不能建在 SharedArrayBuffer 上）；没有 Web 字体加载，演示中文字画成占位块；影子 ARIA DOM 未实现。

### 移动端的特殊性

- **Surface 生命周期**：进入后台时 surface 销毁、GPU 上下文可能丢失；恢复时走「设备资源重建」路径（与桌面的 device lost 是同一套机制）。
- **安全区与软键盘**：`SafeAreaInsets` 变化触发根布局；软键盘弹出时提供「调整布局 / 平移视图」两种策略。
- **手势与系统手势冲突**：边缘返回手势区域需让给系统。
- **触摸滚动**：惯性、回弹等物理参数按平台取默认值（`PlatformSettings`），保证手感接近原生。

## 无障碍桥

UI 层产出与平台无关的**语义树**（角色、名称、状态、动作、边界、父子关系，见 05），各平台桥接：

| 平台 | 技术 |
|---|---|
| Windows | UI Automation provider |
| macOS | NSAccessibility 协议 |
| Linux | AT-SPI2 (DBus) |
| Android | AccessibilityNodeProvider |
| iOS | UIAccessibilityElement |
| Web | 影子 ARIA DOM |

语义树的模型与分期见 [10-accessibility.md](10-accessibility.md)。
