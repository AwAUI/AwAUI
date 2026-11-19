using AwAUI.Graphics;

namespace AwAUI.RenderTests;

/// <summary>DrawImage 测试用的合成位图（确定性，不依赖文件）。软件与 GPU 测试共用（Metal 测试以链接方式包含本文件）。</summary>
public static class TestImages
{
    /// <summary>彩色棋盘：每个纹素颜色不同（便于看出采样位置），左上角一格半透明，右下角一格全透明。</summary>
    public static Bitmap Checker(int width, int height, PixelFormat format = PixelFormat.Rgba8888)
    {
        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4;
                bool dark = ((x + y) & 1) == 0;
                rgba[o] = (byte)(dark ? 30 + x * 200 / Math.Max(1, width - 1) : 240);
                rgba[o + 1] = (byte)(dark ? 40 : 200 - y * 150 / Math.Max(1, height - 1));
                rgba[o + 2] = (byte)(dark ? 200 - x * 150 / Math.Max(1, width - 1) : 60 + y * 180 / Math.Max(1, height - 1));
                rgba[o + 3] = 255;
            }
        rgba[3] = 128;
        rgba[rgba.Length - 1] = 0;
        return Bitmap.FromRgbaUnpremultiplied(rgba, new PixelSize(width, height), format);
    }

    /// <summary>高频图案：1 像素宽的黑白竖线与横线交替（缩小时最容易走样）。</summary>
    public static Bitmap Stripes(int width, int height)
    {
        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4;
                byte v = (byte)(((x & 1) ^ ((y >> 3) & 1)) != 0 ? 255 : 0);
                rgba[o] = v; rgba[o + 1] = v; rgba[o + 2] = v; rgba[o + 3] = 255;
            }
        return Bitmap.FromRgbaUnpremultiplied(rgba, new PixelSize(width, height));
    }

    /// <summary>由四个纯色象限组成（检验源矩形不渗入相邻象限）。</summary>
    public static Bitmap Quadrants(int half)
    {
        var colors = new[] { Color.Parse("#e53935"), Color.Parse("#43a047"), Color.Parse("#1e88e5"), Color.Parse("#fdd835") };
        var bmp = new Bitmap(new PixelSize(half * 2, half * 2));
        for (int y = 0; y < half * 2; y++)
        {
            var row = bmp.GetRow(y);
            for (int x = 0; x < half * 2; x++)
            {
                var c = colors[(y < half ? 0 : 2) + (x < half ? 0 : 1)];
                row[x * 4] = (byte)(c.R * 255 + 0.5f);
                row[x * 4 + 1] = (byte)(c.G * 255 + 0.5f);
                row[x * 4 + 2] = (byte)(c.B * 255 + 0.5f);
                row[x * 4 + 3] = 255;
            }
        }
        bmp.Invalidate();
        return bmp;
    }
}
