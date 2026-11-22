using System.Globalization;
using System.Runtime.CompilerServices;
using AwAUI.Graphics;
using AwAUI.Imaging;

namespace AwAUI.Controls.UnitTests.Integration;

/// <summary>
/// 本项目的黄金图像比较。期望图片在 <c>tests/AwAUI.Controls.UnitTests/Golden</c>；失败时实际图片与差异图片写到测试输出目录的
/// <c>GoldenOutput</c>，并给出差异像素数与首个差异坐标。设置 <c>AWAUI_UPDATE_GOLDEN=1</c> 重写期望图片（人工审核后再提交）。
/// <para>
/// 测试进程可能通过 <c>--artifacts-path</c> 在仓库外运行，所以仓库根依次尝试：<c>AWAUI_REPO_ROOT</c> 环境变量、从
/// <see cref="AppContext.BaseDirectory"/> 向上查找、从当前目录向上查找、以及源文件的编译时路径。黄金图片是 AwAUI 自身渲染的参考图，
/// 不是 shadcn 官网截图（离线环境无法获取官网截图）。
/// </para>
/// </summary>
static class GoldenImage
{
    static readonly string s_repoRoot = FindRepoRoot();
    static readonly string s_goldenDirectory = Path.Combine(s_repoRoot, "tests", "AwAUI.Controls.UnitTests", "Golden");
    static readonly string s_outputDirectory = Path.Combine(AppContext.BaseDirectory, "GoldenOutput");

    public static string RepoRoot => s_repoRoot;

    public static string GoldenDirectory
    {
        get
        {
            Directory.CreateDirectory(s_goldenDirectory);
            return s_goldenDirectory;
        }
    }

    public static string OutputDirectory
    {
        get
        {
            Directory.CreateDirectory(s_outputDirectory);
            return s_outputDirectory;
        }
    }

    /// <summary>
    /// 比较当前帧与同名的黄金图片。像素逐通道精确比较；<paramref name="tolerance"/> 默认 0，只有明确需要容忍后端舍入时才应放宽。
    /// </summary>
    public static void AssertMatches(string name, Bitmap actual, int tolerance = 0)
    {
        if (name is null) throw new ArgumentNullException(nameof(name));
        if (actual is null) throw new ArgumentNullException(nameof(actual));
        var path = Path.Combine(GoldenDirectory, name + ".png");
        if (Environment.GetEnvironmentVariable("AWAUI_UPDATE_GOLDEN") == "1")
        {
            PngCodec.Save(actual, path);
            return;
        }

        var output = OutputDirectory;
        if (!File.Exists(path))
        {
            var missingPath = Path.Combine(output, name + ".actual.png");
            PngCodec.Save(actual, missingPath);
            Assert.Fail(
                $"Golden image '{name}.png' is missing. AwAUI-rendered reference image: {path}. " +
                $"The current rendering was written to {missingPath}. Review it and run with AWAUI_UPDATE_GOLDEN=1 to create the reference.");
        }

        using var expected = PngCodec.Load(path);
        if (expected.Size != actual.Size)
        {
            var sizePath = Path.Combine(output, name + ".actual.png");
            PngCodec.Save(actual, sizePath);
            Assert.Fail(
                $"Golden image '{name}.png' has size {expected.Size} but the current rendering is {actual.Size}. " +
                $"Current rendering: {sizePath}. Run with AWAUI_UPDATE_GOLDEN=1 after reviewing the size change.");
        }

        var e = expected.ToRgbaUnpremultiplied();
        var a = actual.ToRgbaUnpremultiplied();
        int width = expected.Width;
        int height = expected.Height;
        int bad = 0;
        int maxDiff = 0;
        int firstX = -1, firstY = -1;
        var diff = new byte[e.Length];
        for (int i = 0; i < e.Length; i += 4)
        {
            int channel = 0;
            for (int c = 0; c < 4; c++)
                channel = Math.Max(channel, Math.Abs(e[i + c] - a[i + c]));
            if (channel > tolerance)
            {
                bad++;
                if (firstX < 0)
                {
                    int pixel = i / 4;
                    firstX = pixel % width;
                    firstY = pixel / width;
                }
            }
            maxDiff = Math.Max(maxDiff, channel);
            if (channel > tolerance)
            {
                diff[i] = 255;
                diff[i + 1] = 0;
                diff[i + 2] = 255;
                diff[i + 3] = 255;
            }
            else
            {
                // 未变化的像素变淡，差异位置更醒目。
                diff[i] = (byte)(a[i] / 4);
                diff[i + 1] = (byte)(a[i + 1] / 4);
                diff[i + 2] = (byte)(a[i + 2] / 4);
                diff[i + 3] = 255;
            }
        }

        if (bad == 0) return;

        var actualPath = Path.Combine(output, name + ".actual.png");
        var diffPath = Path.Combine(output, name + ".diff.png");
        PngCodec.Save(actual, actualPath);
        using (var diffBitmap = Bitmap.FromRgbaUnpremultiplied(diff, expected.Size))
            PngCodec.Save(diffBitmap, diffPath);
        Assert.Fail(
            $"Golden image '{name}.png' differs: {bad.ToString(CultureInfo.InvariantCulture)} of " +
            $"{(width * height).ToString(CultureInfo.InvariantCulture)} pixels exceed tolerance {tolerance.ToString(CultureInfo.InvariantCulture)} " +
            $"(max channel delta {maxDiff.ToString(CultureInfo.InvariantCulture)}, first difference at {firstX.ToString(CultureInfo.InvariantCulture)}," +
            $"{firstY.ToString(CultureInfo.InvariantCulture)}). Expected: {path}. Actual: {actualPath}. Diff: {diffPath}. " +
            "Run with AWAUI_UPDATE_GOLDEN=1 after reviewing the change.");
    }

    static string FindRepoRoot()
    {
        var env = Environment.GetEnvironmentVariable("AWAUI_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(env) && IsRepoRoot(env)) return Path.GetFullPath(env);

        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory(), SourceDirectory() })
        {
            if (string.IsNullOrEmpty(start)) continue;
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                if (IsRepoRoot(dir.FullName)) return dir.FullName;
        }

        throw new DirectoryNotFoundException(
            "Could not find the AwAUI repository root. Set AWAUI_REPO_ROOT to the repository root (the directory containing 'tests' and 'src').");
    }

    static string SourceDirectory([CallerFilePath] string file = "") =>
        string.IsNullOrEmpty(file) ? "" : Path.GetDirectoryName(file) ?? "";

    static bool IsRepoRoot(string path) =>
        Directory.Exists(Path.Combine(path, "tests", "AwAUI.Controls.UnitTests")) &&
        Directory.Exists(Path.Combine(path, "src", "AwAUI.UI"));
}
