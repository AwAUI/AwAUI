using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using AwAUI;
using AwAUI.Graphics;
using AwAUI.Render;
using RenderDemo;

namespace WasmProbe;

/// <summary>
/// 软件光栅器基准：同一份代码既编进 WASM 探针，也编进原生对照程序（Native/），保证两边测的是同样的工作量。
/// 逻辑尺寸固定 640x440，分别以 1x（640x440 像素）与 2x（1280x880 像素）渲染。
/// </summary>
public static class Bench
{
    public const float LogicalWidth = 640, LogicalHeight = 440;

    static readonly Color Clear = Color.Transparent;

    const string KernelsType = "AwAUI.Render.Raster.Kernels";

    /// <summary>
    /// 通过反射强制光栅器内核档位（Scalar / Vector / Vector128；null 恢复默认）。
    /// Kernels 是 AwAUI.Render 的内部类型，探针不是它的友元程序集，所以只能走反射；DynamicDependency 让裁剪器保留它。
    /// 返回实际生效的档位名，找不到开关时返回 null。
    /// </summary>
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, KernelsType, "AwAUI.Render")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "由 DynamicDependency 保留")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "由 DynamicDependency 保留")]
    public static string? SetTier(string? tier)
    {
        var kernels = typeof(SoftwareRenderBackend).Assembly.GetType(KernelsType);
        var forced = kernels?.GetProperty("ForcedTier");
        var active = kernels?.GetProperty("Active");
        if (forced is null || active is null) return null;
        if (tier != null)
        {
            var enumType = Nullable.GetUnderlyingType(forced.PropertyType) ?? forced.PropertyType;
            forced.SetValue(null, Enum.Parse(enumType, tier, ignoreCase: true));
        }
        return active.GetValue(null)?.ToString();
    }

    /// <summary>运行全部用例并把结果打印到标准输出。</summary>
    public static void Run(int warmup, int frames, string filter, string? tier = null, bool series = false)
    {
        Console.WriteLine($"# kernel tier: {SetTier(tier) ?? "n/a (no Kernels switch in this AwAUI.Render build)"}");
        Console.WriteLine($"# runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}, " +
                          $"{System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}, " +
                          $"Vector.IsHardwareAccelerated={System.Numerics.Vector.IsHardwareAccelerated}, " +
                          $"Vector128.IsHardwareAccelerated={Vector128Accelerated()}");
        Console.WriteLine($"# warmup={warmup} frames={frames}");
        Console.WriteLine("case                     pixels        mean     median        min   (ms/frame)");

        foreach (var scale in new[] { 1, 2 })
        {
            var backend = new SoftwareRenderBackend();
            using var target = (BitmapRenderTarget)backend.CreateOffscreenTarget(
                new PixelSize((int)(LogicalWidth * scale), (int)(LogicalHeight * scale)), scale);
            var size = new Size(LogicalWidth, LogicalHeight);
            var full = new Rect(0, 0, LogicalWidth, LogicalHeight);

            // 单图元用例的 DisplayList 只录制一次，计时只含光栅化（含每帧清屏，可减去 clear-only）
            var solid = One(dc => dc.FillRect(full, Color.Parse("#336699")));
            var alpha = One(dc => dc.FillRect(full, Color.Parse("#33669980")));
            var gradient = One(dc => dc.FillRect(full,
                new LinearGradientBrush(new Point(0, 0), new Point(LogicalWidth, LogicalHeight),
                    new[] { new GradientStop(0, Color.Parse("#4f46e5")), new GradientStop(1, Color.Parse("#06b6d4")) })));
            var shadow = One(dc =>
            {
                var card = new RoundRect(new Rect((LogicalWidth - 420) / 2, (LogicalHeight - 300) / 2, 420, 300), 16);
                dc.DrawBoxShadow(card, new BoxShadow(0, 12, 32, -4, Color.Parse("#1a2b4a33")));
                dc.FillRoundRect(card, Color.Parse("#ffffff"));
            });
            var cases = new List<(string Name, Func<int, DisplayList> Build)>
            {
                ("clear-only", _ => DisplayList.Empty),
                ("solid-opaque", _ => solid),
                ("solid-alpha50", _ => alpha),
                ("linear-gradient", _ => gradient),
                ("shadow-420x300-blur32", _ => shadow),
            };

            var scene = new DemoScene();
            cases.Add(("scene-record-only", i => { scene.Time = i / 60.0; return DisplayList.Empty; }));
            cases.Add(("demo-scene", i => { scene.Time = i / 60.0; return scene.Build(size); }));

            foreach (var (name, build) in cases)
            {
                if (filter.Length > 0 && name.IndexOf(filter, StringComparison.Ordinal) < 0) continue;
                bool recordOnly = name == "scene-record-only";
                var times = new double[frames];
                for (int i = 0; i < warmup + frames; i++)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    if (recordOnly) scene.Build(size);
                    else target.Render(build(i), new RenderOptions(Clear));
                    double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                    if (i >= warmup) times[i - warmup] = ms;
                }
                if (series)
                    Console.WriteLine("  series: " + string.Join(" ", Array.ConvertAll(times, t => t.ToString("F1", CultureInfo.InvariantCulture))));
                Array.Sort(times);
                double sum = 0;
                foreach (var t in times) sum += t;
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "{0,-24} {1,4}x{2,-4}@{3} {4,9:F2} {5,10:F2} {6,10:F2}",
                    name, target.PixelSize.Width, target.PixelSize.Height, scale,
                    sum / frames, times[frames / 2], times[0]));
            }
        }
    }

    /// <summary>以固定时间渲染一帧演示场景（用于 WASM 与原生的像素对照），返回渲染目标。</summary>
    public static BitmapRenderTarget RenderScene(int scale, double time)
    {
        var backend = new SoftwareRenderBackend();
        var target = (BitmapRenderTarget)backend.CreateOffscreenTarget(
            new PixelSize((int)(LogicalWidth * scale), (int)(LogicalHeight * scale)), scale);
        var scene = new DemoScene { Time = time };
        target.Render(scene.Build(new Size(LogicalWidth, LogicalHeight)), new RenderOptions(Clear));
        return target;
    }

    static DisplayList One(Action<DrawingContext> draw)
    {
        var dc = new DrawingContext();
        draw(dc);
        return dc.Finish();
    }

    static bool Vector128Accelerated() =>
#if NET8_0_OR_GREATER
        System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated;
#else
        false;
#endif
}
