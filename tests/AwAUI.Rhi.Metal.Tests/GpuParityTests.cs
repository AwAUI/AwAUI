using AwAUI.Graphics;
using AwAUI.RenderTests;
using Xunit.Abstractions;

namespace AwAUI.Rhi.Metal.Tests;

/// <summary>
/// GPU（Metal）与软件后端在黄金图像场景上的一致性（design/07 M2 验收：「软件与 GPU 输出差异在阈值内」）。
/// <para>
/// 容差（预乘 RGBA8，逐像素取通道差的最大值）：通道差 &gt; 8 的像素不超过 0.3%，&gt; 24 的不超过 0.02%，&gt; 2 的不超过 3%，最大差不超过 64。
/// 2 以内是 GPU 与 CPU 的浮点舍入、8 位查找表与掩码量化；8 以内是抗锯齿边缘上的近似：解析圆角（像素中心的半平面覆盖率）与软件后端
/// 展平容差 0.05 px 的内接多边形、解析高斯阴影与软件的三次盒式模糊。
/// </para>
/// <para>
/// 已知且有意保留的差别：软件光栅器按像素累积有符号面积后再钳制到 1，描边连接处的自交（内侧经过顶点的连接产生绕数为 2 的细条）
/// 因此会把描边内侧边缘的像素算得偏满。GPU 的解析描边（外圈 − 内圈）给出几何上正确的覆盖率，只在这些像素上不同：
/// 矩形描边的四个内角像素（transforms 场景的 4 个像素），以及圆角描边内侧的圆弧（ring-strokes 场景，单独放宽容差）。
/// 路径描边走与软件相同的光栅器，结果一致。
/// </para>
/// <para>
/// 每个场景的 GPU 输出都写到 tests/TestFiles/Output/Gpu，失败时（或设置 AWAUI_SAVE_DIFF=1 时）另存软件输出与差异图（红色 = 差值 × 8）。
/// </para>
/// </summary>
public class GpuParityTests
{
    readonly ITestOutputHelper _output;

    public GpuParityTests(ITestOutputHelper output) => _output = output;

    public readonly record struct Tolerance(int MaxDiff, double MaxOver8Percent, double MaxOver24Percent, double MaxOver2Percent);

    internal static readonly Tolerance Default = new(MaxDiff: 64, MaxOver8Percent: 0.3, MaxOver24Percent: 0.02, MaxOver2Percent: 3);

    /// <summary>圆角描边内侧：软件后端的过度覆盖（见类说明）。</summary>
    static readonly Tolerance RingStrokes = new(MaxDiff: 96, MaxOver8Percent: 1.0, MaxOver24Percent: 0.15, MaxOver2Percent: 3);

    /// <summary>
    /// 不抗锯齿：覆盖率按 0.5 取阈值，边界离像素中心不到约 0.05 px 时（软件的内接多边形与 GPU 的解析圆弧之差）会有个别像素整个翻转。
    /// </summary>
    static readonly Tolerance Aliased = new(MaxDiff: 255, MaxOver8Percent: 0.05, MaxOver24Percent: 0.05, MaxOver2Percent: 0.05);

    static Tolerance For(string id) =>
        id.StartsWith("gpu:ring-strokes", StringComparison.Ordinal) ? RingStrokes
        : id.StartsWith("gpu:aliased", StringComparison.Ordinal) ? Aliased
        : Default;

    public static IEnumerable<object[]> AllScenes()
    {
        foreach (var s in Scenes.All) yield return new object[] { "scene:" + s.Name };
        foreach (var s in TextScenes.All) yield return new object[] { "text:" + s.Name };
        foreach (var s in ParagraphScenes.All) yield return new object[] { "paragraph:" + s.Name };
        foreach (var s in GpuScenes.All) yield return new object[] { "gpu:" + s.Name };
        foreach (var s in ImageScenes.All) yield return new object[] { "image:" + s.Name };
        yield return new object[] { "image:image-sampling@2x" };
        yield return new object[] { "image:image-transforms@2x" };
        yield return new object[] { "gpu:ring-strokes@2x" };
        yield return new object[] { "gpu:nested-layers@2x" };
        yield return new object[] { "scene:shapes@2x" };
        yield return new object[] { "text:glyphs-paint@2x" };
        yield return new object[] { "demo" };
        yield return new object[] { "demo@2x" };
    }

    static (DisplayList List, int Width, int Height, double Scale) Build(string id)
    {
        double scale = id.EndsWith("@2x", StringComparison.Ordinal) ? 2 : 1;
        var name = scale == 2 ? id.Substring(0, id.Length - 3) : id;
        var dc = new DrawingContext();
        if (name == "demo")
        {
            var demo = new RenderDemo.DemoScene { Time = 0.7, Clicks = 5, Pointer = new Point(420, 300) };
            return (demo.Build(new Size(640, 440)), 640, 440, scale);
        }
        var colon = name.IndexOf(':');
        var kind = name.Substring(0, colon);
        var scene = name.Substring(colon + 1);
        var all = kind switch
        {
            "scene" => Scenes.All,
            "text" => TextScenes.All,
            "gpu" => GpuScenes.All,
            "image" => ImageScenes.All,
            _ => ParagraphScenes.All,
        };
        var s = all.Single(x => x.Name == scene);
        s.Draw(dc);
        return (dc.Finish(), s.Width, s.Height, scale);
    }

    [MetalTheory]
    [MemberData(nameof(AllScenes))]
    public void Gpu_output_matches_software(string id)
    {
        var (list, w, h, scale) = Build(id);
        using var expected = MetalTestSupport.RenderSoftware(list, w, h, scale);
        using var actual = MetalTestSupport.RenderGpu(list, w, h, scale);
        var file = id.Replace(':', '-');
        MetalTestSupport.Save(actual, file + ".gpu");
        var diff = ImageDiff.Compare(expected, actual);
        _output.WriteLine($"{id}: {diff}");
        var tol = For(id);
        bool ok = diff.MaxDiff <= tol.MaxDiff && diff.Over8Percent <= tol.MaxOver8Percent
                  && diff.Over24Percent <= tol.MaxOver24Percent && diff.Over2Percent <= tol.MaxOver2Percent;
        if (!ok || Environment.GetEnvironmentVariable("AWAUI_SAVE_DIFF") == "1")
        {
            MetalTestSupport.Save(expected, file + ".software");
            diff.SaveDiff(file);
        }
        if (!ok)
        {
            Assert.Fail($"{id}: GPU output differs from software beyond tolerance ({diff}; tolerance {tol}). See {MetalTestSupport.OutputDir}.");
        }
    }
}
