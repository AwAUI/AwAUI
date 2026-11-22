# CustomChrome

无边框窗口与自绘标题栏（design/04「窗口装饰」）：

- `WindowOptions.Decorations = WindowDecorations.None`：平台不画标题栏与边框，客户区铺满整个窗口。
- 标题栏的 `@onpointerdown` 调用 `HostWindow.BeginDragMove()`；按钮调用 `Minimize / ToggleMaximize / Close`。
- 组件里的 `HostWindow` 是承载它的 `AwAUI.UI.Window`；这些动作经 `WindowImpl.GetFeature<IWindowChrome>()`
  转发到平台（macOS 的 `performWindowDragWithEvent:`、Win32 的 `WM_NCLBUTTONDOWN/HTCAPTION`、
  X11 的 `_NET_WM_MOVERESIZE` 等）。平台不支持时方法返回 false。

```sh
dotnet run --project samples/CustomChrome
```

应用仍然只引用 `AwAUI.Controls` / `AwAUI.Desktop`，不直接引用任何平台程序集；`AwAUI.Desktop` 在构建期
只引用目标平台的后端。macOS 上无边框窗口使用 `AwAUIWindow`（`canBecomeKeyWindow` 为 YES），因此仍能获得
键盘焦点；Win32 使用 `WS_POPUP` + `WS_EX_APPWINDOW`；X11 在映射前设置 `_MOTIF_WM_HINTS`。
