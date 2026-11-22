# 24 移动端与 Wayland 平台后端

本文把 Android、iOS、Linux Wayland 三个平台后端从设计落到可实施的接口级方案。三者都建立在 [04](04-platform.md) 的平台抽象之上，只在各自平台特有的部分展开；共用的 API 增补集中在第 1 节，先落地再并行实现。

## 0. 范围与现状

**已验证（截至第九批代码）**

- `AwAUI.Platform` 以 `netstandard2.0; net8.0; net10.0` 编译（`build/TargetFrameworks.props`），只引用 `AwAUI.Foundation`、`AwAUI.Graphics`。
- `WindowImpl` 已有 `Surface / ClientSize / RenderScale / RequestFrame / TextInput / Clipboard / Screens / StorageProvider / Launcher / Position / WindowState / Topmost / Activate / SafeAreaInsets / GetFeature<T>`；**没有** `Accessibility`。
- `NativeSurface` 只有 `IFramebufferSurface`、`IMetalLayerSurface`；软件后端要求 `IFramebufferSurface`。
- `UIHost` 实现 `IWindowEvents / IWindowLifecycleEvents / IWindowStateEvents`，构造时 `backend.CreateRenderTarget(window.Surface)`，并接入 `window.TextInput / Clipboard / SafeAreaInsets`。
- `AccessibilityTree.CreateSnapshot / ConnectBridge` 已存在，但 `UIHost` 从未连接平台的 `AccessibilityBridge`。
- `MetalSwapchain` 直接接受 `CAMetalLayer*`；`IMetalLayerSurface` 在 `AwAUI.Graphics`。`MetalDevice.IsSupported / Create` 目前硬编码 `OSPlatform.OSX`（`src/Rhi/AwAUI.Rhi.Metal/MetalDevice.cs:73/89`），iOS 上需放宽。
- 仓库没有 `AwAUI.Android`、`AwAUI.iOS`、`AwAUI.Wayland`，也没有 `AwAUI.Rhi.Gles`；GPU RHI 只有 Metal。`tests/AwAUI.ArchitectureTests` 用 `Assembly.Load` 加载项目引用，net10.0 测试无法直接引用移动 TFM 程序集。

**假设（实现时确认）**

- 已安装 .NET 10 的 `android` / `ios` workload；Android 用 `net10.0-android36.0`（SDK 只到 API 35 时改 35，`SupportedOSPlatformVersion=24`），iOS 用 `net10.0-ios18.0`（最低 15）。
- Android 不依赖 AndroidX：文件选择走平台 `Activity.StartActivityForResult / OnActivityResult`，原生库用 P/Invoke（`libandroid`、`libEGL`、`libGLESv3`）。
- Wayland 目标机提供 `libwayland-client.so.0`、`libxkbcommon`、EGL/GLES、xdg-desktop-portal、AT-SPI2；CI 用 weston headless。
- 三者都是单窗口（单 Activity / 单根视图 / 单 toplevel）宿主；v1 不做进程死亡恢复与多窗口。

## 1. 共用 API 增补

全部是追加虚成员或可选接口，符合 [11](11-compatibility.md) 的兼容规则（netstandard2.0 无默认接口成员，所以不改 `IWindowEvents`）。

| 程序集 | 增补 | 用途 |
|---|---|---|
| `AwAUI.Platform` | `WindowImpl.Accessibility => null`（`AccessibilityBridge?`） | 三个后端都把平台无障碍桥挂到窗口上 |
| `AwAUI.Platform` | `WindowOptions.AppId`（string?） | Wayland 的 `xdg_toplevel.set_app_id`；其他平台忽略 |
| `AwAUI.Platform` | `RawPointerEvent` 追加可选 `bool isPredicted = false` 与只读 `IsPredicted` | iOS `PredictedTouchesForTouch`；合并样本本身按时间排序，不需要额外标志 |
| `AwAUI.Platform` | 可选接口 `IWindowMemoryPressureEvents { void OnMemoryPressure(); }` | iOS `DidReceiveMemoryWarning`；`UIHost` 用 `Events as` 查询后调 `RenderBackend.InvalidateDeviceResources` |
| `AwAUI.Graphics` | `IAndroidWindowSurface { IntPtr ANativeWindow { get; } }` | EGL 窗口表面；加入 `DependencyRulesTests` 的允许接口列表 |
| `AwAUI.Graphics` | `IWlEglSurface { IntPtr WlDisplay; IntPtr WlSurface; IntPtr WlEglWindow; }` | Wayland EGL 表面 |
| `AwAUI.UI` | `UIHost` 构造时连接 `window.Accessibility`，`Dispose` 断开；语义树版本变化时推送快照 | 无障碍 |
| `AwAUI.UI` | `UIHost.SetRootFontScale(double)`（`rem` 基准） | iOS Dynamic Type / Android `fontScale` |
| `AwAUI.Text` | `SystemFonts` 增加 Android（`/system/fonts`，默认 Roboto，回退 Noto CJK/Emoji）与 iOS（`UIFont.FamilyNames` + `CTFontDescriptor` 的 `kCTFontURLAttribute`，回退 PingFang SC / Hiragino Sans / Apple SD Gothic Neo）分支 | 否则文本为空或豆腐块 |
| `AwAUI.Rhi.Metal` | `MetalDevice.IsSupported / Create` 增加 iOS 判定；新增加载预编译 `default.metallib` 的入口 | 避免每次启动 `newLibraryWithSource` |

桌面侧的 `Screens / StorageProvider / Launcher / Clipboard / TextInputMethod / WindowState / SafeAreaInsets / IWindowLifecycleEvents / IWindowStateEvents` 在第九批已经就绪，三个后端直接实现，不再扩展。

## 2. Android（`AwAUI.Android`）

### 2.1 工程与构建

`src/Android/AwAUI.Android/AwAUI.Android.csproj`：`Microsoft.NET.Sdk`，`TargetFramework=net10.0-android36.0`，`SupportedOSPlatformVersion=24`，`RootNamespace=AwAUI.Android`，`EnableTrimAnalyzer=true`，只引用 `AwAUI.Platform`；**不导入** `TargetFrameworks.props`（会强制 netstandard2.0）与 `Polyfills.props`，保留 `Directory.Build.props` 的可空、警告即错误与确定性构建。GPU 阶段新增 `src/Rhi/AwAUI.Rhi.Gles`（通用 TFM，引用 `AwAUI.Render`）。

同步修改：`AwAUI.slnx` 增加 `/src/Android/`；`DependencyRulesTests` 允许 `AwAUI.Android = [Foundation, Graphics, Platform]`；`tests/AwAUI.Platform.UnitTests.csproj` 以源码链接 `src/Android/AwAUI.Android/Input/**`（纯逻辑、不引用 Android 类型）；新增 `samples/ControlCatalog.Android`（`Exe`、`net10.0-android36.0`、`ApplicationId`）。

### 2.2 宿主与生命周期

- `AwAUIAndroidActivity : Android.App.Activity`：用户 MainActivity 加 `[Activity(MainLauncher=true)]` 继承。`OnCreate` 调 `AndroidPlatform.Attach(this, options)` 创建 `AwAUISurfaceView` 并 `SetContentView`；`RequestWindowFeature(NoTitle)`、`SoftInput.AdjustResize`；API 30+ `Window.SetDecorFitsSystemWindows(false)`，旧版用 `SystemUiVisibility`。
- `AndroidPlatform : PlatformBackend`：`Name="Android"`；`Dispatcher` 为 `Handler(Looper.MainLooper)`；`CreateWindow` 返回单例 `AndroidWindow`（重复调用抛 `InvalidOperationException`）；`Run()` 空实现；`Exit()` 调 `Activity.Finish()`。
- 生命周期映射：`OnStart→Foreground`、`OnResume→Active + OnActivated(true)`、`OnPause→Inactive + ResetComposition`、`OnStop→Background + OnSurfaceLost`、`OnDestroy→OnClosed/Dispose`、`OnWindowFocusChanged→OnFocusChanged + OnActivated`、`OnConfigurationChanged/insets→OnResized + OnSafeAreaChanged + Screens.Changed`；返回键先 `OnCloseRequested()`，允许才 `Finish()`。surface 的 lost/restored 由 SurfaceHolder 回调单独负责，不能只靠 Activity。
- Activity 重建：每个 Activity 一个 platform，应用状态放 ViewModel/DI；v1 不做进程死亡恢复。

### 2.3 表面与渲染路径

- `AwAUISurfaceView : SurfaceView, ISurfaceHolderCallback2`，`Holder.SetFormat(Format.Rgba8888)`。选 SurfaceView 而非 TextureView：独立原生 buffer、零拷贝。
- 状态机：`SurfaceCreated` → `ANativeWindow_fromSurface(JNIEnv.Handle, Holder.Surface.Handle)`；`SurfaceChanged(w,h)` → `ANativeWindow_setBuffersGeometry(w,h,RGBA_8888)`，更新 `ClientSize/Scale` 并 `OnResized`；`SurfaceDestroyed` → `OnSurfaceLost` 并释放；重建 → `OnSurfaceRestored`。
- `AndroidSurface : NativeSurface, IFramebufferSurface, IAndroidWindowSurface`：`Size` 为 buffer 像素，`Scale = Resources.DisplayMetrics.Density`。软件路径 `Lock()` 返回持久 `Bitmap`，`Dispose()` 按 `stride` 逐行拷入 `ANativeWindow` 后 `ANativeWindow_unlockAndPost`——满足「上次内容保留」契约，`UIHost.PartialRaster` 可保持开启，代价是每帧一次全屏 memcpy。若改为直接锁 `ANativeWindow`，必须 `host.PartialRaster=false`（buffer 轮换不保内容）。
- GPU：`AwAUI.Rhi.Gles` 用 EGL（`eglGetDisplay / eglInitialize / eglChooseConfig` 取 `EGL_OPENGL_ES3_BIT_KHR + EGL_WINDOW_BIT + RGBA8`，`eglCreateWindowSurface / eglCreateContext(3.0) / eglMakeCurrent / eglSwapBuffers`），`GlesSwapchain : GpuSwapchain`。软件是 v1，GPU 放 M6。
- 帧钟：`Choreographer.Instance.PostFrameCallback`，只保留一个待执行回调；`DoFrame(long ns)` → `Events.OnFrame`，帧内再次请求则重投；pause / surface lost 取消。`Invalidate → RequestFrame`，`SetCursor` 空实现，`PreferredScrollbarStyle=Overlay`。

### 2.4 输入 / IME / 文本

- 触摸：`OnTouchEvent` + `OnGenericMotionEvent`；`ActionMasked/ActionIndex`、`GetPointerId`、`GetX/Y`、`GetPressure`、`GetToolType`、`HistorySize/GetHistorical*`、`EventTime`。每个采样转 `RawPointerEvent`（坐标 px/density，时间相对 `SystemClock.UptimeMillis`）；Down/Up/Move/Cancel，触摸 Down 置 `ChangedButton=Left`；笔/鼠标 hover；`AxisVScroll/HScroll` 转 `RawPointerEvent.CreateWheel`（注意符号与单位）。边缘返回手势让给系统，仅对必须边缘拖动的控件用 `View.SetSystemGestureExclusionRects`。
- 键盘：`OnKeyDown/Up` → `Keycode` 到 `PhysicalKey` 表、`MetaKeyStates` 到 `KeyModifiers`（Shift/Ctrl/Alt/Meta/CapsLock）、`RepeatCount`；逻辑键用 `GetUnicodeChar(meta)` / `KeyCharacterMap`，死键为 `"Dead"`；被 IME 消费的键不转发。无客户端时提交文本走 `IWindowEvents.OnTextInput`。
- IME：`AndroidTextInput : TextInputMethod`，`AwAUIInputConnection : BaseInputConnection` 由 `AwAUISurfaceView.OnCreateInputConnection` 创建。`TextInputOptions` → `EditorInfo.InputType`（ClassText/Password/Number/Email/Url/Search/Phone、`TextFlagMultiLine / NoSuggestions / AutoCorrect / NoPersonalizedLearning`、`CapSentences/Words/Characters`）、`ImeOptions`（Done/Go/Next/Search/Send）、`ImeFlagNoExtractUi`、`InitialSelStart/End`。实现 `GetTextBefore/AfterCursor`、`GetSelectedText`、`CommitText`、`SetComposingText`、`FinishComposingText`、`DeleteSurroundingText`、`SetSelection`、`SendKeyEvent`（退格 → `ReplaceRange`）、`RequestCursorUpdates + CursorAnchorInfo`（光标 / 组合 / 选区）、`GetExtractedText`（或禁用全屏提取）。`OnClientChanged` 做 `RequestFocus / RestartInput / Show / Hide`；`NotifyStateChanged` 更新 cursor anchor；`ResetComposition` 调 `FinishComposingText / RestartInput`。`HandlesClipboardShortcuts=false`（IME 用 `deleteSurroundingText`；硬件 Ctrl+C/V 交框架）。
- 密码框：`EditorInfo` password + 关闭候选；客户端忽略组合。Android 10+ 仅前台可读剪贴板，读失败返回 null。

### 2.5 窗口状态 / 安全区 / 屏幕

- `ClientSize` 取 surface（surface 前用 view/display 回退，保证非零）；`RenderScale = DisplayMetrics.Density`。
- `SafeAreaInsets` = 系统栏 + 刘海 + IME（px/density）。`View.SetOnApplyWindowInsetsListener`（API 20+）；API 30 `WindowInsets.GetInsets(Type.SystemBars() | Type.DisplayCutout() | Type.Ime())`，旧版用 `SystemWindowInset*` + `DisplayCutout.SafeInset*`；变化时 `OnSafeAreaChanged`，旋转与 IME 也触发。布局保持 edge-to-edge，靠安全区调整。
- `AndroidScreens : Screens`：`DisplayManager.GetDisplays()` / `Activity.Display`；`WindowMetrics.Bounds`（API 30）或 `GetRealMetrics`；逻辑 px = px/density；`WorkingArea` 扣系统栏/刘海；`Scaling=density`；主屏为 default；`Changed` 接 `DisplayManager.DisplayChanged` 与配置变化。
- `WindowState`：仅 `FullScreen/Normal` 有意义，用 `WindowInsetsController`（API 30）或 `SystemUiFlags`；`Minimized/Maximized` 空操作。`IsActive = resumed + windowFocus`；`Activate / Position / SetPosition / Topmost` 空操作；`Icon / Decorations` 忽略。

### 2.6 系统服务

- **剪贴板**：`ClipboardManager + ClipData`；文本 `NewPlainText`、HTML `NewHtmlText`，长度用 `Clipboard.LimitLength`；图片/文件 v1 返回 null/false（引入 `AwAUI.Imaging` 会破坏依赖规则）。捕获 `SecurityException`。
- **文件选择**：SAF `Intent.ActionOpenDocument / ActionCreateDocument / ActionOpenDocumentTree`；MIME 取 `FilePickerFileType.MimeTypes` 或扩展名映射；`ExtraAllowMultiple / ExtraTitle`；flags `GrantRead/Write/PersistableUriPermission`；结果经 `OnActivityResult` 的 request-code 路由 + `TaskCompletionSource`；`TakePersistableUriPermission`；`AndroidStorageFile/Folder` 包 `Android.Net.Uri`，用 `ContentResolver.OpenInputStream/OpenOutputStream`，查询 `OpenableColumns.DisplayName/Size`。取消返回空/null；`OnDestroy` 让未完成请求失败。
- **Launcher**：`Intent.ActionView + Uri.Parse`，仅允许 http/https/mailto/tel/sms；`PackageManager.ResolveActivity` 判断可处理；捕获 `ActivityNotFoundException`。`LaunchFileAsync` 需应用配置 `FileProvider` authority + content URI，否则返回 false。
- **无障碍**：`AndroidAccessibilityBridge : AccessibilityBridge` + `AwAUIAccessibilityView` 重写 `GetAccessibilityNodeProvider()`。字符串 ID ↔ int virtual ID；`CreateAccessibilityNodeInfo` 用 `AccessibilityNodeInfo.Obtain(view,id)`，映射 role→ClassName、Name/Text/ContentDescription、states、`BoundsInScreen`（DIP×scale + 屏幕偏移）、Parent/AddChild、actions；`PerformAction` 在 UI 线程调 `RaiseActionRequested`；`Update(snapshot)` 保存并发 `TYPE_WINDOW_CONTENT_CHANGED / InvalidateVirtualView`；live region 用 `TYPE_ANNOUNCEMENT`（API 28）。按快照 `Version` 增量重建。
- 外观/动效：`IAppearancePreference` 读 `Configuration.UiMode`，`IReducedMotionPreference` 读 `Settings.Global.AnimatorDurationScale`，监听变化。

### 2.7 线程 / 打包 / CI

主线程 = `Looper.MainLooper`；`AndroidDispatcher` 用 `Handler.Post`，`CheckAccess` 比较 `Looper.MyLooper()`。v1 在 UI 线程构建、布局、光栅化、呈现；`OnFrame` 只在请求时执行，后台/失 surface 取消；热路径零分配，不 `Recycle` 框架交付的 `MotionEvent`。

发布：`OutputType=Exe`、`AndroidPackageFormat=aab`（发布）/`apk`（CI）、`TargetSdkVersion=36`；Release `PublishTrimmed=true`、`AndroidLinkMode=Full`、`RunAOTCompilation=true`、`AndroidEnableProfiledAot=true`、`AndroidUseInterpreter=false`；JNI 回调保持强引用。CI 安装 `android` workload，Linux runner + KVM 模拟器（API 34/36，x86_64）或 macOS arm64 模拟器，`reactivecircus/android-emulator-runner`，`dotnet publish` APK → `adb install / am start / input / screencap` 冒烟。

### 2.8 文件级拆分

```
src/Android/AwAUI.Android/
  AwAUI.Android.csproj
  AndroidPlatform.cs / AndroidPlatformOptions.cs
  AwAUIAndroidActivity.cs
  AndroidWindow.cs
  AndroidSurfaceView.cs / AndroidSurface.cs
  AndroidFrameClock.cs / AndroidDispatcher.cs
  AndroidTextInput.cs / AndroidInputConnection.cs
  Input/AndroidKeyMap.cs / AndroidMotionTranslator.cs / AndroidInsets.cs / MimeTypes.cs
  AndroidClipboard.cs
  AndroidStorageProvider.cs / AndroidStorageFile.cs / AndroidStorageFolder.cs
  AndroidLauncher.cs / AndroidScreens.cs
  AndroidAppearancePreference.cs / AndroidReducedMotionPreference.cs
  Accessibility/AndroidAccessibilityBridge.cs
  Interop/ANativeWindow.cs / Egl.cs
  AndroidManifest.xml
samples/ControlCatalog.Android/
src/Rhi/AwAUI.Rhi.Gles/            （M6）
```

## 3. iOS（`AwAUI.iOS`）

### 3.1 工程与构建

`src/iOS/AwAUI.iOS/AwAUI.iOS.csproj`：`TargetFramework=net10.0-ios18.0`，`SupportedOSPlatformVersion=15.0`，`OutputType=Library`，`IsTrimmable / IsAotCompatible=true`；**不导入** `TargetFrameworks.props` 与 `Polyfills.props`；只引用 `AwAUI.Platform`、`AwAUI.Graphics`（架构测试允许同 macOS 一样），不引用 `AwAUI.MacOS / AwAUI.Desktop`。

`AwAUI.slnx` 增加 `/src/iOS/`；CI 在 macOS 作业单独构建，Linux/Windows 作业按项目列表或 solution filter 排除。`tests/AwAUI.Platform.UnitTests` 链接 `src/iOS/AwAUI.iOS/Input/**`（纯逻辑）；另建 `tests/AwAUI.iOS.UnitTests`（net10.0，测换算）与 `tests/AwAUI.iOS.DeviceTests`（net10.0-ios + XHarness，模拟器冒烟）；`DependencyRulesTests.Allowed` 加 `AwAUI.iOS`。

### 3.2 宿主与生命周期

- `IosApplication`（`[Register] AwAUIAppDelegate : UIApplicationDelegate`）提供唯一入口：`IosApplication.Run(args, platform => UIHost)` 保存静态工厂后调 `UIApplication.Main(args, null, typeof(AwAUIAppDelegate))`。
- `FinishedLaunching` 内才创建 `IosPlatform` 与 `UIHost`：`configure(platform)` 通常调 `AwApp.CreateHost(platform, options, backend, textEngine, root, sheets)`；此时 UIKit 已就绪，`CreateWindow` 创建 `UIWindow / RootViewController / UIView`，`Show()` 执行 `MakeKeyAndVisible`。**不要**用桌面 `AwApp.Run / DesktopApplicationLifetime`；`IosPlatform.Run()` 空实现，`Exit()` 只停帧钟并标记退出（iOS 无合法强制退出 API）。
- 生命周期：`OnActivated / OnResignActivation` → `OnActivated` 与 `OnFocusChanged`；`DidEnterBackground` → `OnLifecycle(Background) + OnSurfaceLost`，停 `CADisplayLink`；`WillEnterForeground` → `OnSurfaceRestored + OnLifecycle(Foreground)`，重报安全区并 `RequestFrame`；`WillTerminate` → `OnCloseRequested / OnClosed`；`DidReceiveMemoryWarning` → `IWindowMemoryPressureEvents.OnMemoryPressure()`。
- 旋转：`ViewDidLayoutSubviews` 比较 `bounds/scale`，变化时发 `OnResized`；`ViewWillTransitionToSize` 只做尺寸预判；`CATransaction.DisableActions` 避免拉伸。

### 3.3 表面与渲染

- `IosWindow : WindowImpl` 的 `Surface` 为 `IosSurface : NativeSurface, IMetalLayerSurface, IFramebufferSurface`。
- 复用 Mac 已验证结构：`UIView` 保持普通 backing layer，其上铺满视图的 `CAMetalLayer` 子层；`IosSurface.MetalLayer` 返回 `CAMetalLayer.Handle`。平台设置 `Frame/Bounds`、`ContentsScale=UIScreen.MainScreen.Scale`、`Opaque=true`、`AllowsNextDrawableTimeout=true`；`MetalSwapchain` 负责 `Device`、`PixelFormat=Bgra8Unorm`、`FramebufferOnly=false`、`MaximumDrawableCount=3`、`DrawableSize`。
- `RenderScale` 取 `UIView.ContentScaleFactor`（等于 `UIScreen.Scale`；不用 `NativeScale`，避免 Plus 机型降采样错位）；`ClientSize` 取 `View.Bounds.Size`（点）。
- 软件回退：`IFramebufferSurface.Lock()` 生成 `Bitmap`，提交时用 `CGBitmapContext / CGImage` 设为子层 `Contents`（或 `UIView.Draw`），与 Mac 的双模式一致。
- 帧钟：`CADisplayLink` 加到 `NSRunLoop.Main` 的 `Common` 模式；`Paused` 控制空闲；读 `Timestamp / TargetTimestamp / Duration`，iOS 15+ `PreferredFrameRateRange` 设 60–120；后台/无请求暂停；每帧 `NSAutoreleasePool` 包裹。v1 不引入渲染线程。

### 3.4 输入 / IME / 文本

- 触摸：`AwAUIView` 重写 `TouchesBegan/Moved/Ended/Cancelled`；`CoalescedTouchesForTouch` 按时间全部上报，`PredictedTouchesForTouch` 作为预测样本（`IsPredicted`）。`UITouch.Type` 映射 `PointerType`（Direct→Touch、Pencil→Pen、IndirectPointer→Mouse）；`Force/MaximumPossibleForce` → `Pressure`；`LocationInView`，Pencil 用 `PreciseLocationInView`；弱表分配稳定 `pointerId`，Ended/Cancelled 移除。
- 鼠标/触控板：`UIHoverGestureRecognizer` → Move；`UIPanGestureRecognizer.AllowedScrollTypesMask = All` 接收触控板滚动，映射 `CreateWheel`（Continuous→`PreciseWheel`）；所有识别器 `CancelsTouchesInView=false`。
- 硬件键盘：`PressesBegan/Changed/Ended` 读 `UIPress.Key`（`UIKeyboardHidUsage`）→ `PhysicalKey`，`UIKey.ModifierFlags` → `KeyModifiers`；`key.Characters` 仅在无文本客户端时走 `OnTextInput`；Cmd 快捷键另发 `UIKeyCommand` 供系统菜单发现。纯逻辑放 `Input/IosKeyMap.cs`。
- IME：`AwAUIView` 实现 `IUITextInput / IUITextInputDelegate / IUIKeyInput`，由 `IosTextInput : TextInputMethod` 驱动。`Length/GetText` ↔ 自定义 `AwAUIUITextPosition(int)` 与 `AwAUIUITextRange`；`Selection` ↔ `SelectedTextRange`；`Composition` ↔ `MarkedTextRange/MarkedText`；`SetComposition` ↔ `SetMarkedText`；`Commit` ↔ `InsertText`；`ReplaceRange` ↔ `ReplaceText`；`GetCaretRect` ↔ `GetCaretRectForPosition`（转屏幕坐标给候选窗）；`NotifyStateChanged → ReloadInputViews`。配置 `KeyboardType / SecureTextEntry / AutocorrectionType / SpellCheckingType / ReturnKeyType / AutocapitalizationType / TextContentType`；切换客户端 `BecomeFirstResponder / ResignFirstResponder + ReloadInputViews`。无组合时退格合成 `RawKeyEvent(Backspace)` 交框架；有组合时 `SetComposition("", default)`。
- 软键盘 + 安全区：`SafeAreaInsetsDidChange` 与 `UIKeyboard.WillChangeFrameNotification`（`FrameEnd` 转屏幕坐标）合并，`Bottom = max(SafeArea.Bottom, 键盘与视图重叠)`，经 `OnSafeAreaChanged` 上报；浮窗键盘按重叠矩形算。

### 3.5 窗口状态 / 屏幕

- `WindowOptions` 只采用 `Size`（初始视图尺寸参考）与 `Title`（日志）；`Position / StartupLocation / State / Topmost / MinMax / Icon / ShowInTaskbar / SizeToContent / Decorations` 忽略。
- `CreateWindow` 只允许一次（第二次抛 `InvalidOperationException`）；`Position=(0,0)`、`SetPosition / Topmost / Activate / SetWindowState` 空操作、`WindowState` 恒 `Normal`；`IsActive` 跟随应用激活；`PreferredScrollbarStyle=Overlay`；`SetCursor` 无指针设备时忽略。
- `IosScreens : Screens`：`UIScreen.Screens`（或 `UIWindowScene.Screen`）→ `Screen`，`Bounds` 为点坐标，`WorkingArea` 用安全区扣减，`Scaling=UIScreen.Scale`；监听 `DidConnect / DidDisconnect / DidChangeMode` 触发 `Changed`。iOS 无桌面式全局多屏坐标，Stage Manager/外接屏只能近似，文档注明。

### 3.6 系统服务

- `IosClipboard : Clipboard`：`UIPasteboard.General`；文本 `String`，HTML `SetItems` 同时写 `public.html` 与 `public.utf8-plain-text`，图片 `UIImage`，文件 `Urls`；全部经 `IosDispatcher` 回主线程；`MaxTextLength` 截断；iOS 16 粘贴权限提示下不轮询，失败返回 null/false。
- `IosStorageProvider : StorageProvider`：`UIDocumentPickerViewController(UTType[] forOpeningContentTypes, asCopy:true)`（复制进沙盒，避免长期 security scope）；`FilePickerFileType.Patterns / AppleUniformTypeIdentifiers` → `UTType.CreateFromExtension / CreateFromIdentifier`；`OpenFolderAsync` 用 `UTType.Folder`。保存 v1 用「目录选择 + `SuggestedFileName` 创建」；`IosStorageFile` 持 `NSUrl` + security scope，`OpenReadAsync / OpenWriteAsync` 用 `FileStream`，`GetSizeAsync` 用 `NSFileManager`；iCloud 占位文件先 `StartDownloadingUbiquitousItem`，跨进程写用 `NSFileCoordinator`。
- `IosLauncher : Launcher`：`UIApplication.SharedApplication.OpenUrlAsync`；`CanOpenUri` 保留 http/https/mailto 并加 tel/sms 白名单；`LaunchFileAsync` 用 `UIActivityViewController / UIDocumentInteractionController`（分享，不是默认程序）；`LaunchFolderAsync` 返回 false。
- `IosAccessibilityBridge : AccessibilityBridge`：按快照建 `UIAccessibilityElement`（容器为根视图），映射 `AccessibilityTraits`（Button/StaticText/Header/Selected/NotEnabled/Adjustable）、`AccessibilityLabel/Value`、`AccessibilityFrameInContainerSpace`；`AccessibilityCustomActions` 映射 Increment/Decrement/Expand/Collapse，`AccessibilityActivate → RaiseActionRequested(Invoke)`；live region 用 `UIAccessibility.PostNotification(Announcement, text)`。按 `Version` 增量重建。
- 外观/动效：`IosAppearancePreference` 读 `UITraitCollection.UserInterfaceStyle` 并监听 `TraitCollectionDidChange`；`IosReducedMotionPreference` 读 `UIAccessibility.IsReduceMotionEnabled` 并监听通知。

### 3.7 打包 / AOT / CI

`Info.plist`：无 storyboard 的 `UILaunchScreen`、`UIRequiredDeviceCapabilities=metal`、方向、`UIApplicationSupportsIndirectInputEvents`、`LSApplicationQueriesSchemes`、`CFBundleDocumentTypes / UTExportedTypeDeclarations`。强制 AOT：`UseInterpreter=false`、设备 `MtouchUseLlvm=true`、`MtouchLink=SdkOnly`（或 Full + 对 AppDelegate/`[Register]` 类型 `[Preserve]`）、`EnableTrimAnalyzer`。CI 的 macOS runner 安装 `ios` workload；PR 构建 `iossimulator-arm64` 未签名并跑 `xe` 冒烟（`simctl boot/install/launch`，`simctl io screenshot` 留证）；Release 设备构建 `ios-arm64` + `CodesignKey / CodesignProvision`。NuGet `AwAUI.iOS` 只含 `lib/net10.0-ios`。

### 3.8 文件级拆分

```
src/iOS/AwAUI.iOS/
  AwAUI.iOS.csproj
  IosApplication.cs / IosPlatform.cs / IosDispatcher.cs
  IosWindow.cs / IosWindow.Input.cs / IosWindow.TextInput.cs / IosWindow.SafeArea.cs
  IosSurface.cs / IosFrameClock.cs / IosTextInput.cs
  Services/IosClipboard.cs / IosStorageProvider.cs / IosLauncher.cs / IosScreens.cs
  Services/IosAppearancePreference.cs / IosReducedMotionPreference.cs
  Accessibility/IosAccessibilityBridge.cs
  Text/IosSystemFontProvider.cs
  Interop/CoreGraphics.cs
  Input/IosTouchTranslation.cs / IosKeyMap.cs / IosScrollTranslation.cs
samples/IosDemo/
```

## 4. Linux Wayland（`AwAUI.Wayland`）

### 4.1 工程与构建

`src/Linux/AwAUI.Wayland/AwAUI.Wayland.csproj`：导入 `TargetFrameworks.props`、`Polyfills.props`、`Interop.props`，TFM 与 X11 相同（`netstandard2.0; net8.0; net10.0`），`AllowUnsafeBlocks=true`，引用 `AwAUI.Platform`。

新增 `src/tools/WaylandProtocolGen`（net10.0 控制台）：读 `wayland.xml`、`xdg-shell.xml`、`text-input-unstable-v3.xml`、`fractional-scale-v1.xml`、`xdg-decoration-unstable-v1.xml`、`xdg-activation-v1.xml`、`xdg-output-unstable-v1.xml`、`primary-selection-unstable-v1.xml`，生成 blittable 结构体、接口描述符与 `wl_*` 包装到 `Protocols/Generated/*.g.cs`（入库）；`build/Protocols.props` 保证 XML 变更后重生成。

`AwAUI.Desktop.props` 的 Linux 分支改为同时引用 `AwAUI.Wayland` 与 `AwAUI.X11`，定义 `AWAUI_DESKTOP_WAYLAND`；`DesktopPlatform.Create()` 在 Linux 上运行时选择：`WAYLAND_DISPLAY` 存在且 `libwayland-client` 可加载 → `WaylandPlatform`；否则 `DISPLAY` 存在 → `X11Platform`；都不可用才抛 `PlatformNotSupportedException`。`BackendAssembly` 返回实际后端名。这是对「构建期单一后端」的必要放宽，需同步更新 [01](01-architecture.md) 与 [04](04-platform.md)。

### 4.2 入口与生命周期

`WaylandPlatform : PlatformBackend, IDisposable`：构造时 `wl_display_connect(null)`，`wl_registry` 绑定 `wl_compositor`(v4+)、`wl_shm`、`wl_seat`、`wl_output`、`xdg_wm_base`、`zxdg_decoration_manager_v1`、`wp_fractional_scale_manager_v1`、`zwp_text_input_manager_v3`、`zxdg_activation_v1`、`zxdg_output_manager_v1`；`wl_display_roundtrip` 后缺少 compositor / wm_base / shm 即 `PlatformNotSupportedException`。

`Run()`：`wl_display_get_fd` 与自唤醒管道 `pipe2(O_NONBLOCK | O_CLOEXEC)` 一起 `poll`，循环 `wl_display_prepare_read / poll / read_events / wl_display_dispatch_pending`、`Dispatcher.Drain()`、帧投递；`Exit()` 置位并写管道；最后一个窗口关闭时 `Exit()`（与 X11 一致）。`Dispose()` 销毁全局对象、`wl_display_disconnect`、关闭管道。

### 4.3 表面与渲染

M0 走 `wl_shm`：`WaylandSurface : NativeSurface, IFramebufferSurface`，每窗口两条 `wl_buffer`（`memfd_create + ftruncate + mmap`，`ARGB8888/XRGB8888` 小端映射为 `PixelFormat.Bgra8888`，stride = width×4）。`Lock()` 返回 `ILockedFramebuffer`，`Dispose()` 提交；呈现用 `wl_surface_attach + damage + commit`，`wl_buffer.release` 后缓冲可复用。整数缩放用 `wl_surface_set_buffer_scale`；分数缩放绑定 `wp_fractional_scale_v1.preferred_scale`（120 分之一）并配合 `wp_viewporter` 的 destination 尺寸，`RenderScale = scale/120`。

M1 起加 EGL：`WaylandSurface` 实现 `IWlEglSurface`；`wl_egl_window_resize` 跟随尺寸，GLES RHI 直接消费，平台层不引用渲染实现。

帧节拍：`wl_surface_frame` 返回的 `wl_callback.done` 作为 `OnFrame` 源，存活期间只挂一个 callback；`RequestFrame()` 置脏，未映射时暂停，时间戳由 `CLOCK_MONOTONIC` 毫秒转 `TimeSpan`。

### 4.4 输入 / IME / 文本

`wl_seat` 能力 → `wl_pointer / wl_keyboard / wl_touch`。

- 指针：`enter/leave/motion/button/axis/frame`；`axis_source`（wheel/finger/continuous）与 `axis_discrete/value120` 映射 `CreateWheel`（`WheelDeltaMode.Line/Pixel`、`ScrollPhase`）；`BTN_LEFT=0x110` 等映射 `PointerButtons`，坐标除以 `RenderScale`。
- 键盘：`keymap`（`XKB_V1` fd）→ libxkbcommon（`xkb_keymap_new_from_string`、`xkb_state_new`、`xkb_state_update_mask`）；evdev code+8 → `PhysicalKey`（抽 `src/Shared/Linux/EvdevKeyCodes.cs` 供 X11/Wayland 共用）；Mod5 → `KeyModifiers.AltGraph`；`repeat_info` + 定时器实现重复；无 IME 客户端时 `xkb_state_key_get_utf8` 经 `OnTextInput`。
- IME：每 seat 一个 `zwp_text_input_v3`；焦点/客户端变化 `enable/disable`，`set_content_type`、`set_cursor_rectangle`、`commit`；`preedit_string → SetComposition`、`commit_string → Commit`、`delete_surrounding_text → ReplaceRange`、`done` 结束；密码框用 content hint 关闭组合。
- 触摸：`down/up/motion/frame/cancel` 全量上交，`PointerType.Touch`、`pointerId=touch id`。
- 光标：`libwayland-cursor` 主题 + `wl_pointer.set_cursor` 映射 `CursorShape`。

### 4.5 窗口状态 / 安全区 / 屏幕

`xdg_toplevel`：`set_title / set_app_id / set_min_size / set_max_size / set_maximized / set_minimized / set_fullscreen`；`configure(w,h,states)` 驱动 `OnResized` 与 `IWindowStateEvents.OnWindowStateChanged`；`close` 转 `OnCloseRequested`。

**协议限制（须文档化）**：xdg-shell 没有全局定位与置顶——`SetPosition` 为 no-op、`Position` 返回最近配置（通常 0）、`Topmost` 忽略。`IWindowChrome`：`BeginDragMove` 用最近一次 pointer 按下的 serial 调 `xdg_toplevel.move(seat, serial)`，`Minimize` 用 `set_minimized`，`ToggleMaximize` 用 maximize/unmaximize，`IsMaximized` 取 configure states。`Activate()` 经 `zxdg_activation_v1` token（含 `XDG_ACTIVATION_TOKEN` 启动令牌）。装饰用 `zxdg_decoration_manager_v1`：`System → SERVER_SIDE`，`None → CLIENT_SIDE`。

安全区桌面恒为 `Thickness.Zero`（不触发 `OnSafeAreaChanged`）。`Screens` 用 `wl_output + zxdg_output_v1`（logical_position/size/name）与 `scale` 构造 `Screen`；`WorkingArea` 因无面板信息取 `Bounds`，主屏取首个/包含原点的输出；热插拔重建并 `Changed`。

### 4.6 系统服务

- 剪贴板：`wl_data_device_manager` + `zwp_primary_selection_device_v1`，MIME 支持 `text/plain;charset=utf-8`、`UTF8_STRING`、`text/html`、`image/png`、`text/uri-list`；读写走 pipe fd，全异步、带上限与超时。
- 文件选择器：xdg-desktop-portal `org.freedesktop.portal.FileChooser`（`OpenFile / SaveFile` + `Request.Response`）；需在 `Portal/DBus.cs` 实现最小 D-Bus 客户端。
- Launcher：portal `org.freedesktop.portal.OpenURI`（parent + activation token），`CanLaunchUri` 校验绝对 URI。
- 无障碍：`AtspiBridge : AccessibilityBridge` 经 D-Bus 导出 AT-SPI2 树与动作；不可用时返回 null。
- 外观/动效：portal `org.freedesktop.portal.Settings` 的 `color-scheme` 与减少动效，实现 `IAppearancePreference / IReducedMotionPreference`。

### 4.7 线程 / 打包 / CI

全部 `wl_*` 调用限定在平台线程；`WaylandDispatcher : IDispatcher` 用 `pipe2 + poll` 自唤醒，`Post` 可跨线程；帧只在「已映射 + 有请求」时经 callback 产生，空闲时完全阻塞在 `poll`。

P/Invoke 只用 blittable 签名，监听器用 `[UnmanagedCallersOnly]` 函数指针并保存在静态表（防 GC）；生成代码无反射，AOT 友好。CI 的 Linux 作业新增 `libwayland-dev libxkbcommon-dev libegl-dev weston`，启动 `weston --backend=headless-backend.so --socket=wayland-test`，设 `XDG_RUNTIME_DIR/WAYLAND_DISPLAY` 后跑 `RenderDemo --frames 30 --dump`；另加 `WAYLAND_DISPLAY` 未设置 + Xvfb 的 X11 回退冒烟；`linux-x64` NativeAOT 发布冒烟。

### 4.8 文件级拆分

| 文件 | 职责 |
|---|---|
| `WaylandPlatform.cs` | 连接、registry、全局对象、Run/Exit、调度器 |
| `WaylandWindow.cs` | `WindowImpl` + `IWindowChrome`，xdg_toplevel、状态、关闭 |
| `WaylandSurface.cs` / `WaylandShmBuffer.cs` | `NativeSurface/IFramebufferSurface`、双缓冲、damage、EGL 能力 |
| `WaylandSeat.cs` / `WaylandPointer.cs` / `WaylandKeyboard.cs` / `WaylandTouch.cs` / `WaylandCursor.cs` | 输入设备与光标 |
| `WaylandTextInput.cs` | `TextInputMethod`，text-input-v3 状态机 |
| `WaylandClipboard.cs` | `Clipboard`，data_device/primary |
| `WaylandScreens.cs` / `WaylandOutput.cs` | `Screens`、输出热插拔 |
| `WaylandSettings.cs` | 外观/动效偏好 |
| `Portal/DBus.cs`、`Portal/PortalStorageProvider.cs`、`Portal/PortalLauncher.cs` | 最小 D-Bus、文件选择、打开 URI |
| `Accessibility/AtspiBridge.cs` | AT-SPI2 桥 |
| `Interop/WaylandClient.cs`、`XkbCommon.cs`、`Egl.cs`、`LibC.cs` | 原生绑定 |
| `Input/EvdevKeyCodes.cs`、`Input/WaylandAxis.cs`、`Input/WaylandKeyRepeat.cs` | 纯逻辑（供单元测试链接） |
| `Protocols/Generated/*.g.cs` | 协议生成物 |

## 5. 交叉验收矩阵

| 能力 | Android | iOS | Wayland |
|---|---|---|---|
| 软件呈现 | SurfaceView + 持久 Bitmap 拷贝 | CGImage → 子层 contents | wl_shm 双缓冲 |
| GPU 呈现 | EGL/GLES（M6） | CAMetalLayer（复用 Metal RHI） | EGL/GLES（M1 起） |
| 安全区 | 系统栏 + 刘海 + IME | 安全区 + 键盘 | 恒 0（桌面） |
| IME | InputConnection + cursor anchor | UITextInput + marked text | text-input-v3 |
| 文件选择 | SAF | UIDocumentPicker | portal FileChooser |
| 无障碍 | AccessibilityNodeProvider | UIAccessibilityElement | AT-SPI2（D-Bus） |
| 帧钟 | Choreographer | CADisplayLink | wl_callback |
| 打包 | APK/AAB + Mono AOT | 设备 AOT + 签名 | NativeAOT 单文件 |

三者的「不支持」都返回 null / false / 空操作，不抛异常；平台能力经 `GetFeature<T>` 与可选接口查询，与 [04](04-platform.md) 的 D23 一致。

## 6. 里程碑汇总

| 阶段 | Android | iOS | Wayland |
|---|---|---|---|
| M1 骨架 | 项目 + Activity + SurfaceView 软件呈现 + 帧钟 + APK 冒烟 | 项目 + AwAUIView + CAMetalLayer + CADisplayLink + 旋转 | 协议生成器 + wl_shm 窗口 + 帧回调 + 关闭 |
| M2 输入 | 多点触摸/笔/滚轮/旋转/insets/Screens | 触摸（合并/预测）/硬件键盘/滚动 | 指针/键盘/xkb/触摸/光标 |
| M3 文本 | IME + 剪贴板 | UITextInput + 软键盘 inset + 剪贴板 | text-input-v3 + 剪贴板 |
| M4 服务 | SAF + Launcher + 偏好 | Screens + Storage + Launcher + 偏好 | xdg 状态 + Screens + 分数缩放 + portal |
| M5 无障碍 | NodeProvider + TalkBack 冒烟 | VoiceOver + Dynamic Type + 字体回退 | EGL/GLES + AT-SPI + X11 回退 |
| M6 发布 | AOT/裁剪 + 模拟器 CI | 签名 + TestFlight + 模拟器冒烟 | NativeAOT + weston CI |

## 7. 主要风险

1. **表面语义差异**：Android buffer 轮换不保内容、Wayland shm 撕裂、iOS drawable 超时。去风险：先持久 Bitmap/双缓冲，明确 `PartialRaster` 契约，旋转/后台/遮挡回归。
2. **IME 碎片化**：Gboard/三星全屏提取、UITextInput 协议面大、text-input-v3 事件顺序。去风险：禁用 extract UI、cursor anchor、真实拼音/日语/韩语探针、状态机单测。
3. **AOT/裁剪**：JNI/ObjC 回调被裁、`[Register]` 类型被裁、AOT 回调被 GC。去风险：trim analyzer 从第一天开、静态根表、Release 冒烟。
4. **字体与无障碍**：无平台字体分支导致豆腐块；无障碍每帧全量快照。去风险：先修 `SystemFonts`，按 `Version` 增量、TalkBack/VoiceOver 冒烟。
5. **组合根误用**：`AwAppBuilder / DesktopApplicationLifetime` 在移动端会在 `Run` 返回后提前退出。去风险：示例直接用 `AwApp.CreateHost` / 自建 `UIHost`，文档写明。
6. **portal/D-Bus 不可用**：返回 null/空列表而非异常；服务测试按能力跳过。
7. **CI 环境**：Android 模拟器需要 KVM/arm64，iOS 需要 macOS + 证书，Wayland 需要 weston。去风险：PR 只做未签名/模拟器构建，设备签名用受保护变量。
