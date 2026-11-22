using System;
using System.IO;
using AwAUI;
using AwAUI.Headless;
using AwAUI.Imaging;
using AwAUI.Platform;
using AwAUI.Render;
using AwAUI.Text;
using AwAUI.Tailwind.Runtime;
using AwAUI.UI;
using TailwindDemo;
using TailwindDemo.Components;

// 用法：TailwindDemo [输出.png] [--dark] [--scale 2] [--width 1100] [--height 820] [--css 输出.css] [--runtime]
// 无窗口渲染一帧到 PNG。Tailwind 样式表由源生成器在构建期生成（TailwindStyles.Css），这里登记到 AppStyleSheets，
// 渲染管线在第一帧自动加入级联。--runtime 改用运行时补全（TailwindRuntime）：构建期未生成的类名在运行时即时生成规则，
// 通过 AppStyleSheet.Update 替换级联里的旧样式表。
string output = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : "tailwind-demo.png";
bool dark = Array.IndexOf(args, "--dark") >= 0;
bool runtimeMode = Array.IndexOf(args, "--runtime") >= 0;
float scale = float.TryParse(Arg("--scale"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 2f;
int width = int.TryParse(Arg("--width"), out var w) ? w : 1100;
int height = int.TryParse(Arg("--height"), out var h) ? h : 820;

// --runtime：用同一份入口样式表建立运行时补全，并登记成可热替换的应用样式表。
// 生成器的模块初始化器在 AwAUITailwindRegister=false 时关闭，所以默认路径手动调用 TailwindStyles.Register()。
TailwindRuntime? runtime = null;
if (runtimeMode)
{
    TailwindRuntime.Enabled = true;
    runtime = TailwindStyles.CreateRuntime();
    var runtimeSheet = AppStyleSheets.Register(runtime.Css, TailwindStyles.SourceName);
    // BindIncremental：立即应用当前 CSS，之后每次运行时补全变化都带着第一个变化的顶层偏移替换级联里的样式表，
    // AppStyleSheet 复用已解析的前缀规则，只重新解析变化后的尾部
    runtime.BindIncremental(runtimeSheet.Update);
    // 构建期没有扫描到的类名（演示补全本身；界面示例不依赖它们）
    runtime.AddCandidates(new[] { "bg-lime-500", "hover:bg-lime-600" });
}
else TailwindStyles.Register();
if (Arg("--css") is { } cssOut) File.WriteAllText(cssOut, runtime?.Css ?? TailwindStyles.Css);

var platform = new HeadlessPlatform { DefaultScale = scale };
var host = AwApp.CreateHost(platform, new WindowOptions { Title = "AwAUI Tailwind", Size = new Size(width, height) },
    new SoftwareRenderBackend(), Fonts.CreateTextEngine(), new App { Dark = dark });
var diagnostics = 0;
host.Root.Diagnostic += d =>
{
    if (diagnostics++ < 40) Console.Error.WriteLine("[ui] " + d);
};
// 运行时 Tailwind：无法生成的类名按 AW2001 进入 UI 诊断通道（每个类名只报一次），而不是只留在 InvalidCandidates 里
if (runtime is not null)
{
    runtime.InvalidCandidate += (_, candidate) => host.Root.ReportDiagnostic(new UIDiagnostic("AW2001",
        DiagnosticSeverity.Warning, "运行时 Tailwind 无法为类名 \"" + candidate + "\" 生成样式。"));
}
for (int i = 0; i < 3; i++) platform.RunFrame();
var window = (HeadlessWindow)host.Window;
PngCodec.Save(window.Framebuffer!, output);
Console.WriteLine($"Tailwind: {TailwindStyles.Candidates.Count} classes, {(runtime?.Css ?? TailwindStyles.Css).Length / 1024.0:F1} KiB CSS ({TailwindStyles.SourceName}{(runtime is null ? "" : ", runtime completion")})");
Console.WriteLine($"Wrote {output} ({window.Framebuffer!.Width}x{window.Framebuffer.Height} px), {diagnostics} diagnostics");

string? Arg(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

namespace TailwindDemo
{
    static class Fonts
    {
        /// <summary>随附的 Noto Sans（OFL）作为 sans-serif / system-ui；缺字时回退到系统字体（design/19「字体回退」）。</summary>
        public static TextEngine CreateTextEngine()
        {
            var fonts = new FontCollection { SystemFonts = SystemFonts.Default };
            fonts.RegisterFile(Path.Combine(AppContext.BaseDirectory, "Fonts", "NotoSans-Regular.ttf"));
            foreach (var generic in new[] { "sans-serif", "system-ui", "ui-sans-serif", "serif", "monospace", "ui-monospace" })
                fonts.SetGenericFamily(generic, "Noto Sans");
            return new DefaultTextEngine(fonts);
        }
    }
}
