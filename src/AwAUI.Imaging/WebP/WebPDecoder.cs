using System;
using System.Collections.Generic;
using System.IO;

namespace AwAUI.Imaging.WebP;

/// <summary>容器中的一张图像（静态图或动画帧）：VP8L，或 VP8 + 可选 ALPH。</summary>
internal sealed class WebPImageChunk
{
    public int DataOffset, DataLength;
    public bool IsLossless;
    public int AlphaOffset = -1, AlphaLength;
    public int Width, Height;
    public bool HasAlpha;
}

/// <summary>ANMF 帧。</summary>
internal sealed class WebPFrame
{
    public WebPImageChunk Image = null!;
    public int X, Y, Duration;
    public bool DisposeToBackground, Blend;
    public PixelRect Rect => new(X, Y, Image.Width, Image.Height);
}

/// <summary>解析后的 WebP 文件。</summary>
internal sealed class WebPFile
{
    public int CanvasWidth, CanvasHeight;
    public WebPImageChunk? Still;
    public List<WebPFrame> Frames = new();
    public int LoopCount;
    public uint BackgroundBgra;
    public bool IsAnimated => Still == null;
}

/// <summary>
/// WebP 容器（RIFF）解析与图像解码。规则按 libwebp：RIFF 大小超出数据视为截断；简单格式只有一个 VP8 / VP8L 块；
/// 扩展格式（VP8X）跳过 ICCP / EXIF / XMP 与未知块，静态图取第一个图像块（与画布尺寸必须一致），
/// 动画由 ANIM + ANMF 组成，帧必须位于画布内且尺寸与位流一致。
/// </summary>
internal static class WebPContainer
{
    public static bool HasSignature(ReadOnlySpan<byte> h) =>
        h.Length >= 12 && h[0] == 'R' && h[1] == 'I' && h[2] == 'F' && h[3] == 'F' && h[8] == 'W' && h[9] == 'E' && h[10] == 'B' && h[11] == 'P';

    static uint U32(byte[] d, int o) => (uint)(d[o] | d[o + 1] << 8 | d[o + 2] << 16 | d[o + 3] << 24);
    static int U24(byte[] d, int o) => d[o] | d[o + 1] << 8 | d[o + 2] << 16;

    static bool Tag(byte[] d, int o, string t) => d[o] == t[0] && d[o + 1] == t[1] && d[o + 2] == t[2] && d[o + 3] == t[3];

    /// <summary>读取 [pos, end) 中的一个块头。返回负载偏移与长度，以及下一个块的位置。</summary>
    static bool NextChunk(byte[] d, ref int pos, int end, out int tagPos, out int payload, out int size)
    {
        tagPos = payload = size = 0;
        if (pos + 8 > end) return false;
        tagPos = pos;
        uint s = U32(d, pos + 4);
        if (s > (uint)(end - pos - 8)) throw new InvalidDataException("Truncated WebP chunk.");
        size = (int)s;
        payload = pos + 8;
        pos = payload + size + (size & 1);
        if (pos > end) pos = end;
        return true;
    }

    public static WebPFile Parse(byte[] d)
    {
        if (!HasSignature(d)) throw new InvalidDataException("Not a WebP file.");
        uint riff = U32(d, 4);
        if (riff < 12) throw new InvalidDataException("Invalid WebP RIFF size.");
        if (riff > (uint)(d.Length - 8)) throw new InvalidDataException("Truncated WebP file.");
        int end = 8 + (int)riff;
        int pos = 12;
        var file = new WebPFile();
        if (!NextChunk(d, ref pos, end, out int tag, out int payload, out int size)) throw new InvalidDataException("WebP file has no image.");

        if (Tag(d, tag, "VP8 ") || Tag(d, tag, "VP8L"))
        {
            file.Still = ReadImage(d, tag, payload, size, -1, 0);
            file.CanvasWidth = file.Still.Width;
            file.CanvasHeight = file.Still.Height;
            DecodeLimits.CheckDimensions(file.CanvasWidth, file.CanvasHeight, "WebP");
            return file;
        }
        if (!Tag(d, tag, "VP8X")) throw new InvalidDataException("Unknown WebP chunk layout.");
        if (size < 10) throw new InvalidDataException("Invalid VP8X chunk.");
        file.CanvasWidth = 1 + U24(d, payload + 4);
        file.CanvasHeight = 1 + U24(d, payload + 7);
        DecodeLimits.CheckDimensions(file.CanvasWidth, file.CanvasHeight, "WebP");

        WebPImageChunk? still = null;
        int alphaOff = -1, alphaLen = 0;
        while (NextChunk(d, ref pos, end, out tag, out payload, out size))
        {
            if (Tag(d, tag, "ANIM"))
            {
                if (size < 6) throw new InvalidDataException("Invalid ANIM chunk.");
                file.BackgroundBgra = U32(d, payload);
                file.LoopCount = d[payload + 4] | d[payload + 5] << 8;
            }
            else if (Tag(d, tag, "ANMF"))
            {
                file.Frames.Add(ReadFrame(d, payload, size, file));
            }
            else if (still == null && Tag(d, tag, "ALPH"))
            {
                if (alphaOff < 0)
                {
                    alphaOff = payload;
                    alphaLen = size;
                }
            }
            else if (still == null && (Tag(d, tag, "VP8 ") || Tag(d, tag, "VP8L")))
            {
                still = ReadImage(d, tag, payload, size, alphaOff, alphaLen);
            }
        }
        if (file.Frames.Count > 0) return file;
        if (still == null) throw new InvalidDataException("WebP file has no image.");
        if (still.Width != file.CanvasWidth || still.Height != file.CanvasHeight)
            throw new InvalidDataException("WebP image size does not match the canvas.");
        file.Still = still;
        return file;
    }

    static WebPImageChunk ReadImage(byte[] d, int tag, int payload, int size, int alphaOff, int alphaLen)
    {
        var img = new WebPImageChunk { DataOffset = payload, DataLength = size };
        if (Tag(d, tag, "VP8L"))
        {
            if (!Vp8LDecoder.TryReadHeader(d.AsSpan(payload, size), out img.Width, out img.Height, out img.HasAlpha))
                throw new InvalidDataException("Invalid VP8L header.");
            img.IsLossless = true;
        }
        else
        {
            if (!Vp8Decoder.TryReadInfo(d.AsSpan(payload, size), out img.Width, out img.Height))
                throw new InvalidDataException("Invalid VP8 frame header.");
            img.AlphaOffset = alphaOff;
            img.AlphaLength = alphaLen;
            img.HasAlpha = alphaOff >= 0;
        }
        return img;
    }

    static WebPFrame ReadFrame(byte[] d, int payload, int size, WebPFile file)
    {
        if (size < 16) throw new InvalidDataException("Invalid ANMF chunk.");
        var f = new WebPFrame
        {
            X = 2 * U24(d, payload),
            Y = 2 * U24(d, payload + 3),
            Duration = U24(d, payload + 12),
        };
        int w = 1 + U24(d, payload + 6), h = 1 + U24(d, payload + 9);
        int flags = d[payload + 15];
        f.DisposeToBackground = (flags & 1) != 0;
        f.Blend = (flags & 2) == 0;
        int pos = payload + 16, end = payload + size;
        int alphaOff = -1, alphaLen = 0;
        WebPImageChunk? img = null;
        while (img == null && NextChunk(d, ref pos, end, out int tag, out int p, out int s))
        {
            if (Tag(d, tag, "ALPH"))
            {
                if (alphaOff < 0)
                {
                    alphaOff = p;
                    alphaLen = s;
                }
            }
            else if (Tag(d, tag, "VP8 ") || Tag(d, tag, "VP8L"))
            {
                img = ReadImage(d, tag, p, s, alphaOff, alphaLen);
            }
        }
        if (img == null) throw new InvalidDataException("ANMF frame has no image.");
        if (img.Width != w || img.Height != h) throw new InvalidDataException("ANMF frame size does not match its bitstream.");
        if ((long)f.X + w > file.CanvasWidth || (long)f.Y + h > file.CanvasHeight) throw new InvalidDataException("ANMF frame lies outside the canvas.");
        f.Image = img;
        return f;
    }

    /// <summary>
    /// 解码一张图像的位流（尚不写入目标）：返回 VP8L 的 ARGB（uint[]）或 VP8 的 YUV 平面与 alpha。
    /// 先解码再分配目标，保证尺寸检查在大块分配之前完成。
    /// </summary>
    public static object DecodeImage(byte[] d, WebPImageChunk img)
    {
        if (img.IsLossless)
        {
            var argb = Vp8LDecoder.Decode(d, img.DataOffset, img.DataLength, d.Length, out int w, out int h);
            if (w != img.Width || h != img.Height) throw new InvalidDataException("VP8L size mismatch.");
            return argb;
        }
        var yuv = Vp8Decoder.Decode(d, img.DataOffset, img.DataLength, d.Length);
        if (yuv.Width != img.Width || yuv.Height != img.Height) throw new InvalidDataException("VP8 size mismatch.");
        if (img.AlphaOffset >= 0) yuv.Alpha = WebPAlpha.Decode(d, img.AlphaOffset, img.AlphaLength, img.Width, img.Height);
        return yuv;
    }

    /// <summary>把 <see cref="DecodeImage"/> 的结果写成非预乘 RGBA，覆盖 <paramref name="dst"/> 的指定区域。</summary>
    public static void WriteRgba(object decoded, int width, int height, byte[] dst, int offset, int stride)
    {
        if (decoded is uint[] argb)
        {
            for (int y = 0; y < height; y++)
            {
                int o = offset + y * stride, s = y * width;
                for (int x = 0; x < width; x++, o += 4)
                {
                    uint p = argb[s + x];
                    dst[o] = (byte)(p >> 16);
                    dst[o + 1] = (byte)(p >> 8);
                    dst[o + 2] = (byte)p;
                    dst[o + 3] = (byte)(p >> 24);
                }
            }
            return;
        }
        var yuv = (Vp8Image)decoded;
        YuvConverter.ToRgba(yuv, dst, offset, stride, yuv.Alpha);
    }

    /// <summary>解码并写入目标区域。</summary>
    public static void DecodeInto(byte[] d, WebPImageChunk img, byte[] dst, int offset, int stride) =>
        WriteRgba(DecodeImage(d, img), img.Width, img.Height, dst, offset, stride);
}

/// <summary>ALPH 块：头字节（压缩方式、预测滤波、预处理）+ 原始或 VP8L 压缩的 alpha 平面。</summary>
internal static class WebPAlpha
{
    public static byte[] Decode(byte[] d, int offset, int length, int width, int height)
    {
        if (length < 1) throw new InvalidDataException("Empty ALPH chunk.");
        int header = d[offset];
        int method = header & 3, filter = (header >> 2) & 3, pre = (header >> 4) & 3, reserved = header >> 6;
        if (method > 1 || pre > 1 || reserved != 0) throw new InvalidDataException("Invalid ALPH header.");
        byte[] alpha;
        int n = width * height;
        if (method == 0)
        {
            if (length - 1 < n) throw new InvalidDataException("Truncated ALPH data.");
            alpha = new byte[n];
            Buffer.BlockCopy(d, offset + 1, alpha, 0, n);
        }
        else
        {
            alpha = Vp8LDecoder.DecodeAlpha(d, offset + 1, length - 1, width, height, d.Length);
        }
        if (filter != 0) Unfilter(alpha, width, height, filter);
        return alpha;
    }

    static void Unfilter(byte[] a, int width, int height, int filter)
    {
        // 第 0 行：首像素不变，其余以左侧为预测
        for (int x = 1; x < width; x++) a[x] = (byte)(a[x] + a[x - 1]);
        for (int y = 1; y < height; y++)
        {
            int row = y * width, prev = row - width;
            switch (filter)
            {
                case 1:   // 水平：首像素以上方为预测
                    a[row] = (byte)(a[row] + a[prev]);
                    for (int x = 1; x < width; x++) a[row + x] = (byte)(a[row + x] + a[row + x - 1]);
                    break;
                case 2:   // 竖直
                    for (int x = 0; x < width; x++) a[row + x] = (byte)(a[row + x] + a[prev + x]);
                    break;
                default:  // 梯度
                {
                    int top = a[prev], topLeft = top, left = top;
                    for (int x = 0; x < width; x++)
                    {
                        top = a[prev + x];
                        int g = left + top - topLeft;
                        int pred = (g & ~0xFF) == 0 ? g : g < 0 ? 0 : 255;
                        left = (byte)(a[row + x] + pred);
                        topLeft = top;
                        a[row + x] = (byte)left;
                    }
                    break;
                }
            }
        }
    }
}

/// <summary>
/// YUV 4:2:0 → RGBA，逐位复刻 libwebp 默认的「fancy」上采样（色度在两个方向上按 9:3:3:1 插值）
/// 与 14 位定点的 BT.601 转换。
/// </summary>
internal static class YuvConverter
{
    static int MultHi(int v, int coeff) => (v * coeff) >> 8;

    static byte Clip8(int v) => (v & ~16383) == 0 ? (byte)(v >> 6) : v < 0 ? (byte)0 : (byte)255;

    static void Write(int y, uint u, uint v, byte[] dst, int o)
    {
        int yy = MultHi(y, 19077);
        dst[o] = Clip8(yy + MultHi((int)v, 26149) - 14234);
        dst[o + 1] = Clip8(yy - MultHi((int)u, 6419) - MultHi((int)v, 13320) + 8708);
        dst[o + 2] = Clip8(yy + MultHi((int)u, 33050) - 17685);
    }

    static uint LoadUv(byte[] u, byte[] v, int i) => u[i] | (uint)v[i] << 16;

    static void UpsamplePair(byte[] Y, int topY, int bottomY, byte[] U, byte[] V, int topUv, int curUv,
        byte[] dst, int topDst, int bottomDst, int len)
    {
        int lastPair = (len - 1) >> 1;
        uint tl = LoadUv(U, V, topUv), l = LoadUv(U, V, curUv);
        uint uv0 = (3 * tl + l + 0x00020002u) >> 2;
        Write(Y[topY], uv0 & 0xFF, uv0 >> 16, dst, topDst);
        if (bottomY >= 0)
        {
            uv0 = (3 * l + tl + 0x00020002u) >> 2;
            Write(Y[bottomY], uv0 & 0xFF, uv0 >> 16, dst, bottomDst);
        }
        for (int x = 1; x <= lastPair; x++)
        {
            uint t = LoadUv(U, V, topUv + x), uv = LoadUv(U, V, curUv + x);
            uint avg = tl + t + l + uv + 0x00080008u;
            uint diag12 = (avg + 2 * (t + l)) >> 3;
            uint diag03 = (avg + 2 * (tl + uv)) >> 3;
            uint a = (diag12 + tl) >> 1, b = (diag03 + t) >> 1;
            Write(Y[topY + 2 * x - 1], a & 0xFF, a >> 16, dst, topDst + (2 * x - 1) * 4);
            Write(Y[topY + 2 * x], b & 0xFF, b >> 16, dst, topDst + 2 * x * 4);
            if (bottomY >= 0)
            {
                a = (diag03 + l) >> 1;
                b = (diag12 + uv) >> 1;
                Write(Y[bottomY + 2 * x - 1], a & 0xFF, a >> 16, dst, bottomDst + (2 * x - 1) * 4);
                Write(Y[bottomY + 2 * x], b & 0xFF, b >> 16, dst, bottomDst + 2 * x * 4);
            }
            tl = t;
            l = uv;
        }
        if ((len & 1) == 0)
        {
            uv0 = (3 * tl + l + 0x00020002u) >> 2;
            Write(Y[topY + len - 1], uv0 & 0xFF, uv0 >> 16, dst, topDst + (len - 1) * 4);
            if (bottomY >= 0)
            {
                uv0 = (3 * l + tl + 0x00020002u) >> 2;
                Write(Y[bottomY + len - 1], uv0 & 0xFF, uv0 >> 16, dst, bottomDst + (len - 1) * 4);
            }
        }
    }

    public static void ToRgba(Vp8Image img, byte[] dst, int offset, int stride, byte[]? alpha)
    {
        int w = img.Width, h = img.Height, ys = img.YStride, uvs = img.UVStride;
        // 第 0 行只用第 0 行色度；之后每两行 (2k-1, 2k) 在色度行 k-1 与 k 之间插值；偶数高度的最后一行单独处理
        UpsamplePair(img.Y, 0, -1, img.U, img.V, 0, 0, dst, offset, -1, w);
        int y = 0;
        for (; y + 2 < h; y += 2)
        {
            int topUv = (y >> 1) * uvs, curUv = topUv + uvs;
            UpsamplePair(img.Y, (y + 1) * ys, (y + 2) * ys, img.U, img.V, topUv, curUv,
                dst, offset + (y + 1) * stride, offset + (y + 2) * stride, w);
        }
        if ((h & 1) == 0 && h > 1)
        {
            int uv = (y >> 1) * uvs;
            UpsamplePair(img.Y, (h - 1) * ys, -1, img.U, img.V, uv, uv, dst, offset + (h - 1) * stride, -1, w);
        }
        for (int r = 0; r < h; r++)
        {
            int o = offset + r * stride + 3;
            if (alpha == null)
                for (int x = 0; x < w; x++, o += 4) dst[o] = 255;
            else
            {
                int s = r * w;
                for (int x = 0; x < w; x++, o += 4) dst[o] = alpha[s + x];
            }
        }
    }
}

/// <summary>
/// 动画 WebP 的合成，规则与 libwebp 的 WebPAnimDecoder（浏览器同此）逐位一致：
/// 关键帧从全透明画布开始；非关键帧在上一帧（处置后）的画布上解码帧区域，需要混合时与原内容做非预乘 alpha 混合，
/// 但不与上一帧刚处置为透明的区域混合。只维护一张画布：处置与清除都只作用于相关矩形，每帧耗时与帧面积成正比。
/// </summary>
internal sealed class WebPCompositor : AnimationCompositor
{
    readonly byte[] _data;
    readonly WebPFile _file;
    byte[] _under = Array.Empty<byte>();
    PixelRect _dirty;
    bool _prevWasKey;

    public WebPCompositor(byte[] data, WebPFile file) : base(new PixelSize(file.CanvasWidth, file.CanvasHeight))
    {
        _data = data;
        _file = file;
    }

    public override void Reset()
    {
        Canvas.AsSpan().Clear();
        _dirty = default;
        _prevWasKey = false;
    }

    bool IsFull(PixelRect r) => r.Width == CanvasSize.Width && r.Height == CanvasSize.Height;

    void Zero(PixelRect r)
    {
        int stride = CanvasSize.Width * 4;
        for (int y = r.Y; y < r.Bottom; y++) Canvas.AsSpan(y * stride + r.X * 4, r.Width * 4).Clear();
    }

    public override void Compose(int index)
    {
        var f = _file.Frames[index];
        var rect = f.Rect;
        WebPFrame? prev = index > 0 ? _file.Frames[index - 1] : null;
        bool key;
        if (prev == null) key = true;
        else if ((!f.Image.HasAlpha || !f.Blend) && IsFull(rect)) key = true;
        else key = prev.DisposeToBackground && (IsFull(prev.Rect) || _prevWasKey);

        if (prev != null && prev.DisposeToBackground) Zero(prev.Rect);
        if (key)
        {
            Zero(_dirty);
            _dirty = default;
        }

        int stride = CanvasSize.Width * 4;
        bool blend = prev != null && f.Blend && !key;
        int rowBytes = rect.Width * 4;
        if (blend)
        {
            if (_under.Length < rowBytes * rect.Height) _under = new byte[rowBytes * rect.Height];
            for (int y = 0; y < rect.Height; y++)
                Buffer.BlockCopy(Canvas, (rect.Y + y) * stride + rect.X * 4, _under, y * rowBytes, rowBytes);
        }
        WebPContainer.DecodeInto(_data, f.Image, Canvas, rect.Y * stride + rect.X * 4, stride);
        _dirty = _dirty.Union(rect);

        if (blend)
        {
            var p = prev!.Rect;
            for (int y = 0; y < rect.Height; y++)
            {
                int cy = rect.Y + y;
                if (prev.DisposeToBackground && cy >= p.Y && cy < p.Bottom && rect.X < p.Right && rect.Right > p.X)
                {
                    // 只混合上一帧处置区域之外的部分
                    if (rect.X < p.X) BlendRow(cy, y, rect.X, p.X - rect.X, rect);
                    if (rect.Right > p.Right) BlendRow(cy, y, p.Right, rect.Right - p.Right, rect);
                }
                else BlendRow(cy, y, rect.X, rect.Width, rect);
            }
        }
        _prevWasKey = key;
    }

    void BlendRow(int canvasY, int frameY, int x0, int count, PixelRect rect)
    {
        var c = Canvas;
        int o = (canvasY * CanvasSize.Width + x0) * 4;
        int u = (frameY * rect.Width + (x0 - rect.X)) * 4;
        for (int i = 0; i < count; i++, o += 4, u += 4)
        {
            int srcA = c[o + 3];
            if (srcA == 255) continue;
            if (srcA == 0)
            {
                c[o] = _under[u];
                c[o + 1] = _under[u + 1];
                c[o + 2] = _under[u + 2];
                c[o + 3] = _under[u + 3];
                continue;
            }
            int dstA = _under[u + 3];
            int dstFactor = (dstA * (256 - srcA)) >> 8;
            int blendA = srcA + dstFactor;
            uint scale = (1u << 24) / (uint)blendA;
            c[o] = (byte)((uint)(c[o] * srcA + _under[u] * dstFactor) * scale >> 24);
            c[o + 1] = (byte)((uint)(c[o + 1] * srcA + _under[u + 1] * dstFactor) * scale >> 24);
            c[o + 2] = (byte)((uint)(c[o + 2] * srcA + _under[u + 2] * dstFactor) * scale >> 24);
            c[o + 3] = (byte)blendA;
        }
    }
}
