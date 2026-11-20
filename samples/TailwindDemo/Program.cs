using System;
using System.IO;
using AwAUI;
using AwAUI.Headless;
using AwAUI.Imaging;
using AwAUI.Platform;
using AwAUI.Render;
using AwAUI.Text;
using AwAUI.UI;
using TailwindDemo;
using TailwindDemo.Components;

// 用法：TailwindDemo [输出.png] [--dark] [--scale 2] [--width 1100] [--height 820] [--css 输出.css]
// 无窗口渲染一帧到 PNG。Tailwind 样式表由源生成器在构建期生成（TailwindStyles.Css），模块初始化器已登记到 AppStyleSheets，
// 渲染管线在第一帧自动加入级联：这里不需要手动加载任何样式表。
string output = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : "tailwind-demo.png";
bool dark = Array.IndexOf(args, "--dark") >= 0;
float scale = float.TryParse(Arg("--scale"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 2f;
int width = int.TryParse(Arg("--width"), out var w) ? w : 1100;
int height = int.TryParse(Arg("--height"), out var h) ? h : 820;
if (Arg("--css") is { } cssOut) File.WriteAllText(cssOut, TailwindStyles.Css);

var platform = new HeadlessPlatform { DefaultScale = scale };
var host = AwApp.CreateHost(platform, new WindowOptions { Title = "AwAUI Tailwind", Size = new Size(width, height) },
    new SoftwareRenderBackend(), Fonts.CreateTextEngine(), new App { Dark = dark });
var diagnostics = 0;
host.Root.Diagnostic += d =>
{
    if (diagnostics++ < 40) Console.Error.WriteLine("[ui] " + d);
};
for (int i = 0; i < 3; i++) platform.RunFrame();
var window = (HeadlessWindow)host.Window;
PngCodec.Save(window.Framebuffer!, output);
Console.WriteLine($"Tailwind: {TailwindStyles.Candidates.Count} classes, {TailwindStyles.Css.Length / 1024.0:F1} KiB CSS ({TailwindStyles.SourceName})");
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
        /// <summary>随附的 Noto Sans（OFL）作为 sans-serif / system-ui；本机有中日韩字体时注册为回退。</summary>
        public static TextEngine CreateTextEngine()
        {
            var fonts = new FontCollection();
            fonts.RegisterFile(Path.Combine(AppContext.BaseDirectory, "Fonts", "NotoSans-Regular.ttf"));
            foreach (var generic in new[] { "sans-serif", "system-ui", "ui-sans-serif", "serif", "monospace", "ui-monospace" })
                fonts.SetGenericFamily(generic, "Noto Sans");
            foreach (var candidate in new[]
                     {
                         "/System/Library/Fonts/Supplemental/Arial Unicode.ttf", "/Library/Fonts/Arial Unicode.ttf",
                         "/System/Library/Fonts/Hiragino Sans GB.ttc",
                     })
            {
                if (!File.Exists(candidate)) continue;
                try
                {
                    fonts.RegisterFile(candidate);
                    break;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[font] {candidate}: {ex.Message}");
                }
            }
            return new DefaultTextEngine(fonts);
        }
    }
}
