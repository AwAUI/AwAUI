using AwAUI.Graphics;
using AwAUI.Render;
using AwAUI.Render.Gpu;
using AwAUI.Text.Testing;
using AwAUI.UI;
using Xunit.Abstractions;

namespace AwAUI.Rhi.Metal.Tests;

/// <summary>
/// 第九批：UI 的 CSS 变换（2D、错切、压平的 3D）、clip-path（圆、多边形、inset 圆角）、filter（模糊、投影、灰度 + 对比度、色相）、
/// backdrop-filter（毛玻璃）与 mix-blend-mode 经两个后端画出的结果一致。容差与 <see cref="GpuParityTests"/> 的默认容差相同。
/// 没有 Metal 设备时跳过。
/// </summary>
public class UiEffectsParityTests
{
    readonly ITestOutputHelper _output;

    public UiEffectsParityTests(ITestOutputHelper output) => _output = output;

    const int Width = 420, Height = 300;

    const string Css = """
        root { background-color: #f1f5f9; }
        .page { position: relative; width: 420px; height: 300px; }
        .stripes { position: absolute; left: 0; top: 0; width: 420px; height: 300px;
                   background-image: repeating-linear-gradient(135deg, #fde68a 0px, #fde68a 10px, #93c5fd 10px, #93c5fd 20px); }
        .card { position: absolute; width: 90px; height: 60px; border-radius: 10px; background-color: #4f46e5; color: #ffffff; font-size: 10px; padding: 6px; }
        .rot { left: 20px; top: 20px; transform: rotate(-12deg); }
        .skew { left: 140px; top: 20px; transform: skewX(-15deg) scale(0.9); background-color: #059669; }
        .three { left: 260px; top: 20px; transform: rotateY(50deg) rotateX(20deg); background-color: #db2777; }
        .circle { left: 20px; top: 110px; clip-path: circle(40% at 50% 50%); background-color: #ea580c; }
        .poly { left: 130px; top: 110px; clip-path: polygon(50% 0, 100% 50%, 50% 100%, 0 50%); background-color: #0891b2; }
        .inset { left: 240px; top: 110px; clip-path: inset(8px 4px round 14px); background-image: linear-gradient(to right, #22c55e, #a855f7); transform: rotate(8deg); }
        .blur { left: 330px; top: 110px; width: 60px; filter: blur(3px); }
        .shadow { left: 20px; top: 200px; filter: drop-shadow(4px 4px 3px rgba(0, 0, 0, 0.6)); background-color: #fbbf24; color: #111827; }
        .gray { left: 140px; top: 200px; filter: grayscale(1) contrast(1.4) hue-rotate(40deg); background-image: linear-gradient(to right, #ef4444, #3b82f6); }
        .glass { left: 250px; top: 190px; width: 130px; height: 70px; background-color: rgba(255, 255, 255, 0.25); backdrop-filter: blur(5px) saturate(1.8); border: 1px solid #ffffff; }
        .mult { left: 360px; top: 30px; width: 40px; height: 40px; mix-blend-mode: multiply; background-color: #22d3ee; border-radius: 50%; }
        """;

    static Node Scene() => new Div
    {
        Class = "page",
        Children =
        [
            new Div { Class = "stripes" },
            new Div { Class = "card rot", Children = [new UI.Text("rotate")] },
            new Div { Class = "card skew", Children = [new UI.Text("skew")] },
            new Div { Class = "card three", Children = [new UI.Text("3d")] },
            new Div { Class = "card circle" },
            new Div { Class = "card poly" },
            new Div { Class = "card inset" },
            new Div { Class = "card blur", Children = [new UI.Text("blur")] },
            new Div { Class = "card shadow", Children = [new UI.Text("shadow")] },
            new Div { Class = "card gray", Children = [new UI.Text("gray")] },
            new Div { Class = "card glass", Children = [new UI.Text("glass")] },
            new Div { Class = "card mult" },
        ],
    };

    static DisplayList BuildFrame(float scale)
    {
        var pipeline = new RenderPipeline(new TestTextEngine(), includeDefaultStyleSheet: false);
        pipeline.AddStyleSheet(Css, "ui-effects.css");
        pipeline.SetViewport(new Size(Width, Height), scale);
        var root = new UIRoot { Pipeline = pipeline, Clock = new ManualUIClock() };
        root.Mount(Scene());
        for (int i = 0; i < 5 && root.NeedsFrame; i++) root.RunFrame();
        return pipeline.Frame;
    }

    static Bitmap RenderGpuIsolated(DisplayList list, float scale)
    {
        lock (MetalTestSupport.Lock)
        {
            using var backend = new GpuRenderBackend(MetalDevice.Create());
            var size = new PixelSize((int)Math.Ceiling(Width * scale), (int)Math.Ceiling(Height * scale));
            using var target = backend.CreateOffscreenTarget(size, scale);
            target.Render(list, new RenderOptions(Color.White));
            var bmp = new Bitmap(size);
            unsafe
            {
                var span = new Span<byte>((void*)bmp.Address, bmp.RowBytes * size.Height);
                target.ReadPixels(new PixelRect(0, 0, size.Width, size.Height), span, PixelFormat.Rgba8888);
            }
            return bmp;
        }
    }

    [MetalTheory]
    [InlineData(1f)]
    [InlineData(2f)]
    public void Ui_effects_match_between_software_and_gpu(float scale)
    {
        var list = BuildFrame(scale);
        using var expected = MetalTestSupport.RenderSoftware(list, Width, Height, scale);
        using var actual = RenderGpuIsolated(list, scale);
        string file = scale == 1 ? "ui-effects" : "ui-effects@2x";
        MetalTestSupport.Save(actual, file + ".gpu");
        var diff = ImageDiff.Compare(expected, actual);
        _output.WriteLine($"{file}: {diff}");
        var tol = GpuParityTests.Default;
        bool ok = diff.MaxDiff <= tol.MaxDiff && diff.Over8Percent <= tol.MaxOver8Percent
                  && diff.Over24Percent <= tol.MaxOver24Percent && diff.Over2Percent <= tol.MaxOver2Percent;
        if (!ok || Environment.GetEnvironmentVariable("AWAUI_SAVE_DIFF") == "1")
        {
            MetalTestSupport.Save(expected, file + ".software");
            diff.SaveDiff(file);
        }
        Assert.True(ok, $"{file}: GPU output differs from software beyond tolerance ({diff}; tolerance {tol}). See {MetalTestSupport.OutputDir}.");
    }

    [Fact]
    public void Scene_contains_transforms_filters_backdrop_and_clips()
    {
        // 场景本身（不需要 Metal）：确认对照覆盖了预期的命令
        var ops = new HashSet<DrawOp>();
        int filterLayers = 0;
        void Walk(DisplayList l, int depth)
        {
            var r = l.GetReader();
            while (r.MoveNext())
            {
                ops.Add(r.Op);
                if (r.Op == DrawOp.PushLayer && r.Read<Graphics.Commands.PushLayerCmd>().HasFilter != 0) filterLayers++;
                if (r.Op == DrawOp.DrawDisplayList && depth < 64) Walk(r.GetResource<DisplayList>(r.Read<Graphics.Commands.DrawDisplayListCmd>().DisplayListRef), depth + 1);
            }
        }
        Walk(BuildFrame(1), 0);
        Assert.Contains(DrawOp.Transform, ops);
        Assert.Contains(DrawOp.ClipPath, ops);
        Assert.Contains(DrawOp.DrawBackdrop, ops);
        Assert.Equal(3, filterLayers);
    }
}
