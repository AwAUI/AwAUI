using System;
using System.Runtime.InteropServices;

namespace AwAUI.Graphics;

/// <summary>
/// CPU 内存中的位图：8 位每通道、预乘 alpha。像素存放在非托管内存中，
/// 既可自行分配，也可包装外部缓冲（如平台帧缓冲）。
/// <para>
/// 内容版本（<see cref="Version"/>）：DisplayList 的 <c>DrawImage</c> 按引用保存位图、在渲染时读取像素，后端按（位图对象，版本）缓存
/// 派生数据（mip 级别、GPU 纹理）。<see cref="Clear"/>、<see cref="CopyTo"/>（目标）与渲染到位图的软件目标会自动递增版本；
/// 经 <see cref="GetRow"/> 或 <see cref="Address"/> 直接写像素之后必须调用 <see cref="Invalidate"/>，否则缓存可能继续显示旧内容
/// （例如动画图片每帧复用同一张位图）。
/// </para>
/// </summary>
public sealed unsafe class Bitmap : IDisposable
{
    IntPtr _address;
    readonly bool _owns;
    long _version;

    /// <summary>内容版本：每次 <see cref="Invalidate"/>（以及会改写像素的成员）递增。线程安全读取。</summary>
    public long Version => System.Threading.Interlocked.Read(ref _version);

    /// <summary>声明像素已被改写（经 <see cref="GetRow"/> / <see cref="Address"/> 直接写入之后调用）：递增 <see cref="Version"/>。</summary>
    public void Invalidate() => System.Threading.Interlocked.Increment(ref _version);

    /// <summary>是否已释放（DisplayList 仍可能引用已释放的位图，后端跳过它）。</summary>
    public bool IsDisposed => _address == IntPtr.Zero;

    public Bitmap(PixelSize size, PixelFormat format = PixelFormat.Rgba8888)
    {
        if (size.Width <= 0 || size.Height <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        Size = size;
        Format = format;
        RowBytes = size.Width * 4;
        long bytes = (long)RowBytes * size.Height;
        _address = Marshal.AllocHGlobal(new IntPtr(bytes));
        new Span<byte>((void*)_address, checked((int)bytes)).Clear();
        _owns = true;
    }

    Bitmap(IntPtr address, PixelSize size, int rowBytes, PixelFormat format)
    {
        _address = address;
        Size = size;
        RowBytes = rowBytes;
        Format = format;
        _owns = false;
    }

    /// <summary>包装外部像素缓冲（不取得所有权）。</summary>
    public static Bitmap Wrap(IntPtr address, PixelSize size, int rowBytes, PixelFormat format) =>
        new(address, size, rowBytes, format);

    public PixelSize Size { get; }
    public int Width => Size.Width;
    public int Height => Size.Height;
    public int RowBytes { get; }
    public PixelFormat Format { get; }
    public IntPtr Address => _address != IntPtr.Zero ? _address : throw new ObjectDisposedException(nameof(Bitmap));

    public Span<byte> GetRow(int y)
    {
        if ((uint)y >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(y));
        return new Span<byte>((byte*)Address + (long)y * RowBytes, Width * 4);
    }

    /// <summary>以预乘颜色填满整张位图。</summary>
    public void Clear(Color color)
    {
        byte r = ToByte(color.R * color.A), g = ToByte(color.G * color.A), b = ToByte(color.B * color.A), a = ToByte(color.A);
        if (Format == PixelFormat.Bgra8888) (r, b) = (b, r);
        for (int y = 0; y < Height; y++)
        {
            var row = GetRow(y);
            for (int x = 0; x < row.Length; x += 4)
            {
                row[x] = r; row[x + 1] = g; row[x + 2] = b; row[x + 3] = a;
            }
        }
        Invalidate();
    }

    /// <summary>读取一个像素，返回非预乘颜色。</summary>
    public Color GetPixel(int x, int y)
    {
        var row = GetRow(y);
        int i = x * 4;
        float r = row[i], g = row[i + 1], b = row[i + 2], a = row[i + 3];
        if (Format == PixelFormat.Bgra8888) (r, b) = (b, r);
        if (a == 0) return Color.Transparent;
        return new Color(r / a, g / a, b / a, a / 255f);
    }

    /// <summary>导出为非预乘的 RGBA 字节（行紧密排列），用于编码图片。</summary>
    public byte[] ToRgbaUnpremultiplied()
    {
        var result = new byte[Width * Height * 4];
        int o = 0;
        for (int y = 0; y < Height; y++)
        {
            var row = GetRow(y);
            for (int x = 0; x < row.Length; x += 4)
            {
                int r = row[x], g = row[x + 1], b = row[x + 2], a = row[x + 3];
                if (Format == PixelFormat.Bgra8888) (r, b) = (b, r);
                if (a != 0 && a != 255)
                {
                    r = Math.Min(255, (r * 255 + a / 2) / a);
                    g = Math.Min(255, (g * 255 + a / 2) / a);
                    b = Math.Min(255, (b * 255 + a / 2) / a);
                }
                result[o++] = (byte)r; result[o++] = (byte)g; result[o++] = (byte)b; result[o++] = (byte)a;
            }
        }
        return result;
    }

    /// <summary>从非预乘的 RGBA 字节创建位图。</summary>
    public static Bitmap FromRgbaUnpremultiplied(ReadOnlySpan<byte> rgba, PixelSize size, PixelFormat format = PixelFormat.Rgba8888)
    {
        var bmp = new Bitmap(size, format);
        int i = 0;
        for (int y = 0; y < size.Height; y++)
        {
            var row = bmp.GetRow(y);
            for (int x = 0; x < row.Length; x += 4, i += 4)
            {
                int r = rgba[i], g = rgba[i + 1], b = rgba[i + 2], a = rgba[i + 3];
                if (a != 255)
                {
                    r = (r * a + 127) / 255; g = (g * a + 127) / 255; b = (b * a + 127) / 255;
                }
                if (format == PixelFormat.Bgra8888) (r, b) = (b, r);
                row[x] = (byte)r; row[x + 1] = (byte)g; row[x + 2] = (byte)b; row[x + 3] = (byte)a;
            }
        }
        return bmp;
    }

    public void CopyTo(Bitmap destination)
    {
        if (destination.Size != Size) throw new ArgumentException("Size mismatch.", nameof(destination));
        for (int y = 0; y < Height; y++)
        {
            var src = GetRow(y);
            var dst = destination.GetRow(y);
            if (destination.Format == Format) src.CopyTo(dst);
            else
                for (int x = 0; x < src.Length; x += 4)
                {
                    dst[x] = src[x + 2]; dst[x + 1] = src[x + 1]; dst[x + 2] = src[x]; dst[x + 3] = src[x + 3];
                }
        }
        destination.Invalidate();
    }

    static byte ToByte(float v) => (byte)(Math.Max(0f, Math.Min(1f, v)) * 255f + 0.5f);

    public void Dispose()
    {
        if (_owns && _address != IntPtr.Zero) Marshal.FreeHGlobal(_address);
        _address = IntPtr.Zero;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 安全网：没有释放、也不再被引用（例如只被已丢弃的 DisplayList 或 SVG 文档引用）的自有位图在回收时释放像素内存。
    /// 包装的外部缓冲（<see cref="Wrap"/>）不归位图所有，不释放。
    /// <para>
    /// 因此 <see cref="Address"/> 与 <see cref="GetRow"/> 返回的指针 / Span 不会让位图保持存活：只用指针或 Span、之后不再触及位图对象的代码
    /// （例如把像素交给本机 API）必须在使用结束后调用 <c>GC.KeepAlive(bitmap)</c> 或用 <c>using</c> 持有它，否则 JIT 可以提前认为位图已死，
    /// 终结器在使用期间释放内存。
    /// </para>
    /// </summary>
    ~Bitmap()
    {
        if (_owns && _address != IntPtr.Zero) Marshal.FreeHGlobal(_address);
        _address = IntPtr.Zero;
    }
}
