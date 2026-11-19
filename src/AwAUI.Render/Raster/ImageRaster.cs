using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using AwAUI.Graphics;
using AwAUI.Graphics.Commands;

namespace AwAUI.Render.Raster;

/// <summary>
/// <see cref="DrawOp.DrawImage"/> 的采样计划，两个后端共用（design/03「图片」）：
/// 设备像素 → 纹素坐标的仿射映射、mip 级别、源矩形的钳制范围，以及盒式预滤波的 mip 级别构造。
/// <para>
/// 语义（软件后端是参考实现，GPU 着色器逐条照搬）：设备像素中心 p 映射到纹素坐标 (u, v)。
/// 最近：取纹素 ⌊u⌋, ⌊v⌋；双线性：纹素中心在 i + 0.5，取 ⌊u − 0.5⌋ 与其右侧纹素按小数部分插值（预乘颜色）。
/// 两种方式的纹素下标都钳制到 <see cref="ImagePlan.ClampRect"/>（源矩形向外取整后与位图求交）：源矩形之外的像素不会渗入。
/// 双线性在缩小超过 √2 倍时改在第 L 级（L = round(log2 ρ)）上采样：第 L 级由裁剪区域（而不是整张位图）构造，所以同样不渗入。
/// 每个像素直接由绝对设备坐标求值（不逐像素累加），结果与裁剪区和行起点无关。
/// </para>
/// </summary>
internal readonly struct ImagePlan
{
    /// <summary>mip 级别上限（2^15 = 32768 倍缩小）。</summary>
    public const int MaxLevel = 15;

    public ImagePlan(Matrix3x2 deviceToTexel, int level, PixelRect crop, PixelRect clampRect, ImageSampling sampling)
    {
        DeviceToTexel = deviceToTexel;
        Level = level;
        Crop = crop;
        ClampRect = clampRect;
        Sampling = sampling;
    }

    /// <summary>设备像素坐标 → 采样纹理（第 0 级为整张位图，第 L ≥ 1 级为 <see cref="Crop"/> 的 mip）中的纹素坐标。</summary>
    public Matrix3x2 DeviceToTexel { get; }

    /// <summary>mip 级别；0 表示直接采样位图。</summary>
    public int Level { get; }

    /// <summary>源矩形向外取整、与位图求交后的纹素区域（位图坐标）。</summary>
    public PixelRect Crop { get; }

    /// <summary>纹素下标的钳制范围（采样纹理的坐标；第 0 级为 <see cref="Crop"/>，第 L 级为 mip 的整个范围）。</summary>
    public PixelRect ClampRect { get; }

    public ImageSampling Sampling { get; }

    /// <summary>
    /// 为一条命令制定采样计划。命令无效（非有限、空矩形、不透明度不在 (0, 1]、变换不可逆）时返回 false。
    /// <paramref name="minLevel"/> 让调用方强制更高的级别（GPU 纹理超过尺寸上限时），<paramref name="maxLevel"/> 限制级别
    /// （软件后端本帧新构造的 mip 超出字节上限时改为 0：直接采样位图，不预滤波）。
    /// </summary>
    public static bool TryCreate(in DrawImageCmd c, int imageWidth, int imageHeight, in Matrix3x2 ctm, int minLevel, out ImagePlan plan,
        int maxLevel = MaxLevel)
    {
        plan = default;
        if (!IsValid(c) || imageWidth <= 0 || imageHeight <= 0) return false;
        var src = c.Src.Intersect(new Rect(0, 0, imageWidth, imageHeight));
        if (src.IsEmpty) return false;
        var crop = PixelRect.RoundOut(src).Intersect(new PixelRect(0, 0, imageWidth, imageHeight));
        if (crop.IsEmpty) return false;
        if (!Matrix3x2.Invert(ctm, out var inverse)) return false;
        // 局部 → 纹素：(l − dst.xy) · (src.size / dst.size) + src.xy
        float kx = c.Src.Width / c.Dst.Width, ky = c.Src.Height / c.Dst.Height;
        var localToImage = new Matrix3x2(kx, 0, 0, ky, c.Src.X - c.Dst.X * kx, c.Src.Y - c.Dst.Y * ky);
        var deviceToImage = inverse * localToImage;
        if (!IsFinite(deviceToImage)) return false;

        var sampling = c.Sampling == ImageSampling.Nearest ? ImageSampling.Nearest : ImageSampling.Linear;
        int level = sampling == ImageSampling.Linear ? LevelFor(deviceToImage) : 0;
        level = Math.Min(Math.Max(level, minLevel), maxLevel);
        // 级别不必超过让裁剪区缩成 1 个纹素所需的级别
        level = Math.Min(level, CeilLog2(Math.Max(crop.Width, crop.Height)));
        if (level == 0)
        {
            plan = new ImagePlan(deviceToImage, 0, crop, crop, sampling);
            return true;
        }
        float inv = 1f / (1 << level);
        var toLevel = deviceToImage * new Matrix3x2(inv, 0, 0, inv, -crop.X * inv, -crop.Y * inv);
        LevelSize(crop, level, out int lw, out int lh);
        plan = new ImagePlan(toLevel, level, crop, new PixelRect(0, 0, lw, lh), sampling);
        return true;
    }

    /// <summary>录制器保证的不变量（这里按不可信输入重新检查）。</summary>
    public static bool IsValid(in DrawImageCmd c) =>
        c.Src.IsFinite && c.Dst.IsFinite && !c.Src.IsEmpty && !c.Dst.IsEmpty && c.Opacity > 0 && c.Opacity <= 1;

    /// <summary>ρ = 每个设备像素对应的纹素数（两个方向中较大的）；ρ ≥ √2 时 L = round(log2 ρ)。</summary>
    public static int LevelFor(in Matrix3x2 deviceToImage)
    {
        double dx = Math.Sqrt((double)deviceToImage.M11 * deviceToImage.M11 + (double)deviceToImage.M12 * deviceToImage.M12);
        double dy = Math.Sqrt((double)deviceToImage.M21 * deviceToImage.M21 + (double)deviceToImage.M22 * deviceToImage.M22);
        double rho = Math.Max(dx, dy);
        if (!(rho >= 1.41421356)) return 0;
        double l = Math.Floor(Math.Log(rho, 2) + 0.5);
        return l >= MaxLevel ? MaxLevel : (int)l;
    }

    static int CeilLog2(int v)
    {
        int l = 0;
        while (l < MaxLevel && (1 << l) < v) l++;
        return l;
    }

    public static void LevelSize(PixelRect crop, int level, out int width, out int height)
    {
        int block = 1 << level;
        width = (crop.Width + block - 1) / block;
        height = (crop.Height + block - 1) / block;
    }

    static bool IsFinite(in Matrix3x2 m) =>
        IsFinite(m.M11) && IsFinite(m.M12) && IsFinite(m.M21) && IsFinite(m.M22) && IsFinite(m.M31) && IsFinite(m.M32);

    static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

    /// <summary>
    /// 构造 <paramref name="crop"/> 的第 <paramref name="level"/> 级（预乘 RGBA，行紧密排列）：每个纹素是 2^L × 2^L 块（与裁剪区求交）
    /// 各通道的平均值（整数求和、四舍五入），直接从第 0 级求得。耗时与裁剪区面积成正比，调用方把它计入每帧工作量。
    /// </summary>
    public static unsafe byte[] BuildLevel(Bitmap image, PixelRect crop, int level, out int width, out int height)
    {
        LevelSize(crop, level, out width, out height);
        var result = new byte[checked(width * height * 4)];
        int block = 1 << level;
        bool bgra = image.Format == PixelFormat.Bgra8888;
        var sums = new long[width * 4];
        var counts = new int[width];
        byte* basePtr = (byte*)image.Address;
        for (int ly = 0; ly < height; ly++)
        {
            Array.Clear(sums, 0, sums.Length);
            Array.Clear(counts, 0, counts.Length);
            int y0 = crop.Y + ly * block, y1 = Math.Min(crop.Bottom, y0 + block);
            for (int y = y0; y < y1; y++)
            {
                byte* row = basePtr + (long)y * image.RowBytes;
                for (int x = crop.X; x < crop.Right; x++)
                {
                    int lx = (x - crop.X) >> level;
                    byte* p = row + x * 4;
                    int o = lx * 4;
                    sums[o] += p[0]; sums[o + 1] += p[1]; sums[o + 2] += p[2]; sums[o + 3] += p[3];
                    counts[lx]++;
                }
            }
            int d = ly * width * 4;
            for (int lx = 0; lx < width; lx++)
            {
                long n = counts[lx];
                int o = lx * 4;
                if (n == 0) { d += 4; continue; }
                byte c0 = (byte)((sums[o] + n / 2) / n), c1 = (byte)((sums[o + 1] + n / 2) / n);
                byte c2 = (byte)((sums[o + 2] + n / 2) / n), c3 = (byte)((sums[o + 3] + n / 2) / n);
                if (bgra) (c0, c2) = (c2, c0);
                result[d] = c0; result[d + 1] = c1; result[d + 2] = c2; result[d + 3] = c3;
                d += 4;
            }
        }
        GC.KeepAlive(image);
        return result;
    }
}

/// <summary>按（位图对象，内容版本，裁剪区，级别）做键的图像派生数据（mip 级别、GPU 纹理）。</summary>
internal readonly struct ImageKey : IEquatable<ImageKey>
{
    public ImageKey(Bitmap bitmap, long version, PixelRect region, int level)
    {
        Bitmap = bitmap; Version = version; Region = region; Level = level;
    }

    public Bitmap Bitmap { get; }
    public long Version { get; }
    public PixelRect Region { get; }
    public int Level { get; }

    public bool Equals(ImageKey o) =>
        ReferenceEquals(Bitmap, o.Bitmap) && Version == o.Version && Region == o.Region && Level == o.Level;

    public override bool Equals(object? obj) => obj is ImageKey k && Equals(k);

    public override int GetHashCode() =>
        HashCodeCompat.Combine(HashCodeCompat.Combine(KeyHash.Seed, RuntimeHelpers.GetHashCode(Bitmap)), Version.GetHashCode(), Region.GetHashCode(), Level);
}

/// <summary>
/// 按字节预算淘汰（LRU）的图像缓存。<see cref="TryGet"/> 与 <see cref="Add"/> 记下本帧用过的条目：<see cref="Add"/> 只淘汰不在本帧用过的条目，
/// 所以一帧需要的全部条目可以暂时超出预算（调用方另有每帧上限），下一帧开始时再收缩。条目引用位图对象（因此也让它的像素保持存活）：
/// 已释放的位图、内容版本已改变的旧条目，以及 <see cref="MaxIdleFrames"/> 帧没有用过的条目在 <see cref="BeginFrame"/> 时丢弃。
/// </summary>
internal sealed class ImageLruCache<T> where T : class
{
    sealed class Entry
    {
        public ImageKey Key;
        public T Value = null!;
        public long Bytes;
        public long Frame;
        public Entry? Prev, Next;
    }

    readonly Dictionary<ImageKey, Entry> _map = new();
    readonly Action<T>? _dispose;
    Entry? _head, _tail;
    long _frame;

    public ImageLruCache(long budgetBytes, Action<T>? dispose = null)
    {
        BudgetBytes = budgetBytes;
        _dispose = dispose;
    }

    /// <summary>条目连续这么多帧（渲染过的帧）没有用到就丢弃，不再让已经不显示的位图常驻内存。</summary>
    public const long MaxIdleFrames = 300;

    /// <summary>条目数上限（小图的 mip 只有几个字节，只按字节计不能限制条目数与每帧扫描的代价）。</summary>
    public const int MaxEntries = 4096;

    public long BudgetBytes { get; }
    public long Bytes { get; private set; }
    public int Count => _map.Count;

    /// <summary>构造过的条目数（诊断与测试）。</summary>
    public long Builds { get; private set; }

    public void BeginFrame(long frame)
    {
        _frame = frame;
        // 丢弃已释放的位图与被新版本取代的条目（动画图片每帧换版本）
        for (var e = _tail; e is not null;)
        {
            var prev = e.Prev;
            if (e.Key.Bitmap.IsDisposed || e.Key.Bitmap.Version != e.Key.Version || frame - e.Frame > MaxIdleFrames) Remove(e);
            e = prev;
        }
        Shrink(BudgetBytes);
    }

    public bool TryGet(in ImageKey key, out T value)
    {
        if (_map.TryGetValue(key, out var e))
        {
            e.Frame = _frame;
            MoveToFront(e);
            value = e.Value;
            return true;
        }
        value = null!;
        return false;
    }

    public void Add(in ImageKey key, T value, long bytes)
    {
        if (_map.TryGetValue(key, out var old)) Remove(old);
        Shrink(Math.Max(0, BudgetBytes - bytes));
        var e = new Entry { Key = key, Value = value, Bytes = bytes, Frame = _frame };
        _map[key] = e;
        Bytes += bytes;
        Builds++;
        MoveToFront(e);
    }

    /// <summary>从最久未用的开始淘汰，直到不超过 <paramref name="target"/> 字节；本帧用过的条目保留。</summary>
    void Shrink(long target)
    {
        for (var e = _tail; e is not null && (Bytes > target || _map.Count >= MaxEntries);)
        {
            var prev = e.Prev;
            if (e.Frame != _frame) Remove(e);
            e = prev;
        }
    }

    public void Clear()
    {
        for (var e = _head; e is not null; e = e.Next) _dispose?.Invoke(e.Value);
        _map.Clear();
        _head = _tail = null;
        Bytes = 0;
    }

    void Remove(Entry e)
    {
        Unlink(e);
        _map.Remove(e.Key);
        Bytes -= e.Bytes;
        _dispose?.Invoke(e.Value);
    }

    void MoveToFront(Entry e)
    {
        if (_head == e) return;
        Unlink(e);
        e.Next = _head;
        if (_head is not null) _head.Prev = e;
        _head = e;
        _tail ??= e;
    }

    void Unlink(Entry e)
    {
        if (e.Prev is not null) e.Prev.Next = e.Next; else if (_head == e) _head = e.Next;
        if (e.Next is not null) e.Next.Prev = e.Prev; else if (_tail == e) _tail = e.Prev;
        e.Prev = e.Next = null;
    }
}

/// <summary>CPU 上的一个 mip 级别（预乘 RGBA）。</summary>
internal sealed class ImageLevel
{
    public ImageLevel(byte[] pixels, int width, int height)
    {
        Pixels = pixels; Width = width; Height = height;
    }

    public byte[] Pixels { get; }
    public int Width { get; }
    public int Height { get; }
}

/// <summary>
/// 位图采样着色器（软件后端），语义见 <see cref="ImagePlan"/>。每个像素由绝对设备坐标直接求纹素坐标，所以与行起点、裁剪区无关。
/// 颜色乘以不透明度。
/// </summary>
internal sealed unsafe class ImageShader : Shader
{
    readonly Bitmap? _bitmap;
    readonly ImageLevel? _level;
    readonly Matrix3x2 _map;
    readonly int _x0, _y0, _x1, _y1;
    readonly bool _nearest;
    readonly float _opacity;

    public ImageShader(in ImagePlan plan, Bitmap bitmap, ImageLevel? level, float opacity)
    {
        _bitmap = level is null ? bitmap : null;
        _level = level;
        _map = plan.DeviceToTexel;
        var r = plan.ClampRect;
        _x0 = r.X; _y0 = r.Y; _x1 = r.Right - 1; _y1 = r.Bottom - 1;
        _nearest = plan.Sampling == ImageSampling.Nearest;
        _opacity = opacity;
    }

    public override void Shade(int x, int y, Span<Vector4> destination)
    {
        const float k = 1 / 255f;
        byte* basePtr;
        int rowBytes;
        bool bgra;
        fixed (byte* levelPtr = _level?.Pixels)
        {
            if (_level is not null)
            {
                basePtr = levelPtr;
                rowBytes = _level.Width * 4;
                bgra = false;
            }
            else
            {
                basePtr = (byte*)_bitmap!.Address;
                rowBytes = _bitmap.RowBytes;
                bgra = _bitmap.Format == PixelFormat.Bgra8888;
            }
            float py = y + 0.5f;
            float bu = py * _map.M21 + _map.M31, bv = py * _map.M22 + _map.M32;
            for (int i = 0; i < destination.Length; i++)
            {
                float px = x + i + 0.5f;
                float u = px * _map.M11 + bu, v = px * _map.M12 + bv;
                Vector4 c;
                if (_nearest)
                {
                    c = Texel(basePtr, rowBytes, ClampIndex(MathF.Floor(u), _x0, _x1), ClampIndex(MathF.Floor(v), _y0, _y1));
                }
                else
                {
                    float fu = u - 0.5f, fv = v - 0.5f;
                    float iu = MathF.Floor(fu), iv = MathF.Floor(fv);
                    float tu = fu - iu, tv = fv - iv;
                    if (!(tu >= 0 && tu <= 1)) tu = 0;
                    if (!(tv >= 0 && tv <= 1)) tv = 0;
                    int xa = ClampIndex(iu, _x0, _x1), xb = ClampIndex(iu + 1, _x0, _x1);
                    int ya = ClampIndex(iv, _y0, _y1), yb = ClampIndex(iv + 1, _y0, _y1);
                    var c00 = Texel(basePtr, rowBytes, xa, ya);
                    var c10 = Texel(basePtr, rowBytes, xb, ya);
                    var c01 = Texel(basePtr, rowBytes, xa, yb);
                    var c11 = Texel(basePtr, rowBytes, xb, yb);
                    var top = c00 + (c10 - c00) * tu;
                    var bottom = c01 + (c11 - c01) * tu;
                    c = top + (bottom - top) * tv;
                }
                c *= k * _opacity;
                destination[i] = bgra ? new Vector4(c.Z, c.Y, c.X, c.W) : c;
            }
        }
        // 位图有终结器：读完像素之前不能被回收
        GC.KeepAlive(_bitmap);
    }

    /// <summary>浮点下标钳制到 [lo, hi]（NaN 按 lo）后转为整数；先在浮点域钳制，巨大的坐标也不会溢出。</summary>
    static int ClampIndex(float v, int lo, int hi) => !(v > lo) ? lo : v >= hi ? hi : (int)v;

    static Vector4 Texel(byte* basePtr, int rowBytes, int x, int y)
    {
        byte* p = basePtr + (long)y * rowBytes + x * 4;
        return new Vector4(p[0], p[1], p[2], p[3]);
    }
}
