using System.Numerics;
using AwAUI.Graphics;

namespace AwAUI.RenderTests;

/// <summary>
/// DrawImage（操作码 34）的参考场景：最近 / 双线性放大、小数位置、源矩形（不渗入相邻像素）、不透明度、BGRA 位图、
/// 旋转 / 错切 / 翻转、裁剪与图层、各种倍数的缩小（mip）。软件黄金图像在 tests/TestFiles/Golden，GPU 对照测试链接本文件。
/// </summary>
public static class ImageScenes
{
    static Color C(string hex) => Color.Parse(hex);

    static readonly Lazy<Bitmap> s_checker = new(() => TestImages.Checker(8, 6));
    static readonly Lazy<Bitmap> s_checkerBgra = new(() => TestImages.Checker(8, 6, PixelFormat.Bgra8888));
    static readonly Lazy<Bitmap> s_quadrants = new(() => TestImages.Quadrants(8));
    static readonly Lazy<Bitmap> s_stripes = new(() => TestImages.Stripes(512, 256));
    static readonly Lazy<Bitmap> s_photo = new(() => TestImages.Checker(97, 61));

    public static readonly (string Name, int Width, int Height, Action<DrawingContext> Draw)[] All =
    {
        ("image-sampling", 320, 200, Sampling),
        ("image-transforms", 320, 220, Transforms),
        ("image-downscale", 320, 200, Downscale),
    };

    public static void Sampling(DrawingContext dc)
    {
        var img = s_checker.Value;
        dc.FillRect(new Rect(0, 0, 320, 200), C("#eceff1"));
        // 放大 8 倍：最近 vs 双线性
        dc.DrawImage(img, new Rect(10, 10, 64, 48), ImageSampling.Nearest);
        dc.DrawImage(img, new Rect(84, 10, 64, 48), ImageSampling.Linear);
        // 1:1 与小数位置
        dc.DrawImage(img, new Rect(160, 10, 8, 6));
        dc.DrawImage(img, new Rect(175.5f, 10.25f, 8, 6));
        dc.DrawImage(img, new Rect(190.3f, 10.7f, 37.3f, 23.1f), ImageSampling.Nearest);
        dc.DrawImage(img, new Rect(235.3f, 10.7f, 37.3f, 23.1f));
        // 不透明度
        dc.DrawImage(img, new Rect(160, 40, 48, 36), ImageSampling.Linear, 0.5f);
        dc.DrawImage(img, new Rect(215, 40, 48, 36), ImageSampling.Nearest, 0.25f);
        // BGRA 位图与 RGBA 结果相同
        dc.DrawImage(s_checkerBgra.Value, new Rect(270, 40, 40, 30));
        // 源矩形：象限图的单个象限放大，不得渗入相邻象限的颜色（双线性 + 最近）
        var q = s_quadrants.Value;
        dc.DrawImage(q, new Rect(0, 0, 8, 8), new Rect(10, 70, 60, 60));
        dc.DrawImage(q, new Rect(8, 8, 8, 8), new Rect(80, 70, 60, 60), ImageSampling.Nearest);
        // 小数源矩形（半个纹素）
        dc.DrawImage(q, new Rect(6.5f, 2, 3, 3), new Rect(150, 90, 40, 40));
        // 超出位图的源矩形按 Canvas 规则裁剪，目标随之收缩
        dc.DrawImage(q, new Rect(-8, -8, 32, 32), new Rect(200, 85, 64, 64));
        // 整张象限图（各象限之间按双线性过渡）
        dc.DrawImage(q, new Rect(10, 140, 100, 50));
        dc.DrawImage(img, new Rect(120, 140, 190, 50), ImageSampling.Linear, 0.8f);
    }

    public static void Transforms(DrawingContext dc)
    {
        var img = s_photo.Value;
        dc.FillRect(new Rect(0, 0, 320, 220), C("#ffffff"));
        // 旋转
        dc.Save();
        dc.Transform(Matrix3x2.CreateRotation(0.35f, new Vector2(70, 60)));
        dc.DrawImage(img, new Rect(20, 25, 100, 70));
        dc.Restore();
        // 错切 + 最近（系数避开「纹素边界恰好穿过像素中心」的平局：最近邻在平局处取哪一侧取决于浮点舍入，两个后端可能不同）
        dc.Save();
        dc.Transform(new Matrix3x2(1, 0.2137f, -0.4123f, 1.0173f, 180.31f, 15.17f));
        dc.DrawImage(s_checker.Value, new Rect(0, 0, 80, 60), ImageSampling.Nearest);
        dc.Restore();
        // 水平翻转
        dc.Save();
        dc.Transform(new Matrix3x2(-1, 0, 0, 1, 310, 0));
        dc.DrawImage(img, new Rect(0, 100, 90, 56));
        dc.Restore();
        // 圆角裁剪 + 半透明图层
        dc.Save();
        dc.ClipRoundRect(new RoundRect(new Rect(10, 120, 120, 90), 24));
        dc.PushLayer(new LayerOptions(0.7f));
        dc.DrawImage(img, new Rect(0, 110, 150, 110));
        dc.PopLayer();
        dc.Restore();
        // 路径裁剪内的旋转缩小
        dc.Save();
        dc.ClipPath(VectorPath.FromEllipse(new Rect(140, 160, 90, 55)));
        dc.Transform(Matrix3x2.CreateRotation(-0.6f, new Vector2(185, 187)));
        dc.DrawImage(s_stripes.Value, new Rect(130, 150, 110, 70));
        dc.Restore();
        // 嵌套 DisplayList 中的图片
        var child = new DrawingContext();
        child.DrawImage(s_quadrants.Value, new Rect(0, 0, 16, 16), new Rect(0, 0, 40, 40), ImageSampling.Nearest, 0.9f);
        dc.Save();
        dc.Translate(250, 165);
        dc.Transform(Matrix3x2.CreateScale(1.2f, 0.93f));
        dc.DrawDisplayList(child.Finish());
        dc.Restore();
    }

    public static void Downscale(DrawingContext dc)
    {
        var s = s_stripes.Value;
        dc.FillRect(new Rect(0, 0, 320, 200), C("#3f5f9f"));
        // 512×256 的 1 像素条纹：双线性按 mip 预滤波（接近均匀灰），最近邻按定义走样
        dc.DrawImage(s, new Rect(10, 10, 64, 32));
        dc.DrawImage(s, new Rect(84, 10, 64, 32), ImageSampling.Nearest);
        dc.DrawImage(s, new Rect(158, 10, 170.6f, 85.3f));          // 3 倍
        dc.DrawImage(s, new Rect(10, 50, 128, 64));                  // 4 倍
        dc.DrawImage(s, new Rect(10, 120, 8, 4));                    // 64 倍
        dc.DrawImage(s, new Rect(30, 120, 1.5f, 1.5f));              // 小于一个像素
        // 源矩形 + 缩小：mip 由裁剪区构造，不渗入相邻象限
        var q = s_quadrants.Value;
        dc.DrawImage(q, new Rect(0, 0, 8, 8), new Rect(160, 110, 2, 2));
        dc.DrawImage(q, new Rect(8, 0, 8, 8), new Rect(170, 110, 3, 3));
        dc.DrawImage(s_photo.Value, new Rect(200, 110, 24.25f, 15.25f));
        // 非均匀缩小（按较大的方向选级别）
        dc.DrawImage(s, new Rect(160, 140, 150, 12));
        dc.DrawImage(s, new Rect(160, 160, 30, 35));
    }
}
