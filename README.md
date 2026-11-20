# AwAUI

纯 C# 实现的跨平台自绘 UI 引擎：声明式组件（`.awaui`）+ 标准 CSS + 自研渲染器 + 内建 F12 调试。
目标平台：Windows、macOS、Linux、Android、iOS、WebAssembly；运行时：.NET Framework 4.7.2+、.NET 8–11。

设计文档在 [design/](design/README.md)。组件库正在推进首批标准控件和 Shadcn 组件，见 [路线图](design/07-roadmap.md)。

## 已经可以用的部分

| 程序集 | 内容 |
|---|---|
| `AwAUI.Foundation` | 几何、颜色、调度器抽象 |
| `AwAUI.Graphics` | 录制 API `DrawingContext`、DisplayList 格式、`VectorPath`、画刷、`Bitmap`、渲染后端抽象 |
| `AwAUI.Render` | 纯 C# 软件渲染器（解析式抗锯齿、描边、渐变、裁剪、图层、混合模式、盒阴影） |
| `AwAUI.Imaging` | PNG 编解码 |
| `AwAUI.Platform` | 平台抽象（窗口、输入） |
| `AwAUI.Headless` | 无窗口平台（测试、截图） |
| `AwAUI.MacOS` | macOS 窗口（直接驱动 AppKit，无 workload 依赖，支持 NativeAOT） |

## 构建与测试

需要 .NET SDK 11（见 `global.json`）。

```sh
dotnet build AwAUI.slnx
dotnet test AwAUI.slnx
```

黄金图像在 `tests/TestFiles/Golden/`。比对失败时实际输出与差异图写入 `tests/TestFiles/Output/`；
确认新输出正确后，用 `AWAUI_UPDATE_GOLDEN=1 dotnet test` 更新期望图片。

在 macOS 上编译 .NET Framework 测试目标（只编译，运行需要 Windows）：

```sh
dotnet build AwAUI.slnx -p:IncludeNetFx=true
```

## 运行示例

- [ControlCatalog](samples/ControlCatalog)：Fluent 标准控件，支持明、暗、高对比与紧凑密度。
- [ShadcnGallery](samples/ShadcnGallery)：Shadcn 组件与浮层示例，并提供软件渲染和 Metal 的同场景测量。

```sh
dotnet run --project samples/RenderDemo                       # macOS：打开窗口
dotnet run --project samples/RenderDemo -- --headless out.png # 任意平台：渲染一帧到 PNG

# NativeAOT 单文件发布
dotnet publish samples/RenderDemo -c Release -r osx-arm64 -p:NativeAot=true -o artifacts/aot
```

## 最小用法

```csharp
var dc = new DrawingContext();
dc.DrawBoxShadow(new RoundRect(new Rect(20, 20, 200, 120), 12), new BoxShadow(0, 6, 16, 0, Color.Parse("#00000055")));
dc.FillRoundRect(new RoundRect(new Rect(20, 20, 200, 120), 12), Color.White);
dc.StrokePath(VectorPath.FromEllipse(new Rect(60, 40, 80, 80)), Color.Parse("#4f46e5"), new Stroke(4));
DisplayList list = dc.Finish();

using var backend = new SoftwareRenderBackend();
var target = (BitmapRenderTarget)backend.CreateOffscreenTarget(new PixelSize(480, 320), scale: 2);
target.Render(list, new RenderOptions(Color.Parse("#f3f4f6")));
PngCodec.Save(target.Bitmap, "out.png");
```
