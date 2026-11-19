using AwAUI.Graphics;

namespace AwAUI.Imaging.UnitTests;

/// <summary>测试图片定位与逐像素比较。图片由 tests/TestFiles/Images/generate.py 生成。</summary>
internal static class TestImages
{
    public static string RepoRoot { get; } = FindRoot();

    static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(System.IO.Path.Combine(dir.FullName, "global.json"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    public static string Dir => System.IO.Path.Combine(RepoRoot, "tests", "TestFiles", "Images");
    public static string GoldenDir => System.IO.Path.Combine(RepoRoot, "tests", "TestFiles", "Golden");

    public static string PathOf(string name) => System.IO.Path.Combine(Dir, name);

    public static byte[] Read(string name) => File.ReadAllBytes(PathOf(name));

    public readonly record struct Diff(int Max, double Mean, int Over2, int MaxX, int MaxY);

    /// <summary>比较两张不透明位图的 RGB 通道。</summary>
    public static Diff Compare(Bitmap actual, Bitmap expected)
    {
        Assert.Equal(expected.Size, actual.Size);
        long sum = 0;
        int max = 0, over = 0, mx = 0, my = 0;
        for (int y = 0; y < actual.Height; y++)
        {
            var a = actual.GetRow(y);
            var e = expected.GetRow(y);
            for (int x = 0; x < actual.Width; x++)
            {
                int i = x * 4;
                Assert.Equal(255, a[i + 3]);
                for (int c = 0; c < 3; c++)
                {
                    int d = Math.Abs(a[i + c] - e[i + c]);
                    sum += d;
                    if (d > 2) over++;
                    if (d > max)
                    {
                        max = d;
                        mx = x;
                        my = y;
                    }
                }
            }
        }
        return new Diff(max, sum / (3.0 * actual.Width * actual.Height), over, mx, my);
    }
}
