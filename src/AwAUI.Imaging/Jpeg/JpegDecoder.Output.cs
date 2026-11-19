using System;
using System.IO;
using AwAUI.Graphics;

namespace AwAUI.Imaging.Jpeg;

// 输出阶段：色度上采样（与 libjpeg 的 fancy upsampling 逐位一致）+ 颜色转换，写入 RGBA 位图。
internal sealed unsafe partial class JpegDecoder
{
    const int ScaleBits = 16;
    const int OneHalf = 1 << (ScaleBits - 1);

    // libjpeg jdcolor.c 的 YCbCr → RGB 定点表
    static readonly int[] CrToR = new int[256];
    static readonly int[] CbToB = new int[256];
    static readonly int[] CrToG = new int[256];
    static readonly int[] CbToG = new int[256];
    /// <summary>钳位表：RangeLimit[v + 384] = clamp(v, 0, 255)，覆盖 v ∈ [-384, 639]。</summary>
    static readonly byte[] RangeLimit = new byte[1024];
    const int RangeOffset = 384;

    /// <summary>颜色转换所需的、已固定的查找表指针。</summary>
    struct ColorTables
    {
        public int* CrR, CbB, CrG, CbG;
        public byte* Limit;
    }

    static JpegDecoder()
    {
        static int Fix(double x) => (int)(x * (1 << ScaleBits) + 0.5);
        for (int i = 0, x = -128; i < 256; i++, x++)
        {
            CrToR[i] = (Fix(1.40200) * x + OneHalf) >> ScaleBits;
            CbToB[i] = (Fix(1.77200) * x + OneHalf) >> ScaleBits;
            CrToG[i] = -Fix(0.71414) * x;
            CbToG[i] = -Fix(0.34414) * x + OneHalf;
        }
        for (int i = 0; i < RangeLimit.Length; i++) RangeLimit[i] = Clamp(i - RangeOffset);
    }

    Bitmap Output()
    {
        var colorSpace = ResolveColorSpace();
        int n = _components.Length;
        if (colorSpace == JpegColorSpace.Unknown || n is 2 or > 4)
            throw new NotSupportedException($"JPEG with {n} components is not supported.");

        var bitmap = new Bitmap(new PixelSize(_width, _height));
        try
        {
            var scratch = new byte[4][];
            for (int i = 0; i < 4; i++)
            {
                if (i < n)
                {
                    var c = _components[i];
                    scratch[i] = new byte[Math.Max(_width, c.Width * (_hMax / c.H)) + 16];
                }
                else
                {
                    scratch[i] = Array.Empty<byte>();
                }
            }

            byte[] e = Array.Empty<byte>();
            fixed (byte* p0 = _components[0].Pixels!, p1 = n > 1 ? _components[1].Pixels! : e,
                   p2 = n > 2 ? _components[2].Pixels! : e, p3 = n > 3 ? _components[3].Pixels! : e)
            fixed (byte* s0 = scratch[0], s1 = scratch[1], s2 = scratch[2], s3 = scratch[3])
            fixed (int* crR = CrToR, cbB = CbToB, crG = CrToG, cbG = CbToG)
            fixed (byte* limit = RangeLimit)
            {
                var t = new ColorTables { CrR = crR, CbB = cbB, CrG = crG, CbG = cbG, Limit = limit + RangeOffset };
                byte** planes = stackalloc byte*[4];
                byte** scr = stackalloc byte*[4];
                byte** rows = stackalloc byte*[4];
                planes[0] = p0; planes[1] = p1; planes[2] = p2; planes[3] = p3;
                scr[0] = s0; scr[1] = s1; scr[2] = s2; scr[3] = s3;

                byte* dstBase = (byte*)bitmap.Address;
                for (int y = 0; y < _height; y++)
                {
                    for (int i = 0; i < n; i++) rows[i] = UpsampleRow(_components[i], planes[i], scr[i], y);
                    byte* dst = dstBase + (long)y * bitmap.RowBytes;
                    switch (colorSpace)
                    {
                        case JpegColorSpace.Grayscale: ConvertGray(rows[0], dst, _width); break;
                        case JpegColorSpace.YCbCr: ConvertYCbCr(rows[0], rows[1], rows[2], dst, _width, t); break;
                        case JpegColorSpace.Rgb: ConvertRgb(rows[0], rows[1], rows[2], dst, _width); break;
                        case JpegColorSpace.Cmyk: ConvertCmyk(rows[0], rows[1], rows[2], rows[3], dst, _width); break;
                        default: ConvertYcck(rows[0], rows[1], rows[2], rows[3], dst, _width, t); break;
                    }
                }
            }
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 返回分量在输出第 <paramref name="y"/> 行的全分辨率采样（至少 _width 项）。
    /// 2 倍方向使用 libjpeg 的三角滤波（h2v1 / h1v2 / h2v2 fancy），图像边缘复制最近的真实行/列；
    /// 其他整数倍、以及下采样宽度不超过 2 的水平 2 倍按复制放大（与 libjpeg 的 int_upsample / h2v1_upsample /
    /// h2v2_upsample 一致）。
    /// </summary>
    byte* UpsampleRow(Component c, byte* plane, byte* outp, int y)
    {
        int hexp = _hMax / c.H, vexp = _vMax / c.V;
        int stride = c.Stride, w = c.Width;
        if (hexp == 1 && vexp == 1) return plane + (long)y * stride;

        // 与 libjpeg-turbo 的 jinit_upsampler 相同：水平 2 倍的平滑滤波只在下采样宽度 > 2 时使用，
        // 否则 h2v1 / h2v2 都退化为纯复制（h2v2 此时也不做垂直平滑）；h1v2 平滑不受宽度限制
        if (vexp == 1 && hexp == 2 && w > 2)
        {
            H2V1(plane + (long)y * stride, outp, w);
        }
        else if (vexp == 2 && (hexp == 1 || (hexp == 2 && w > 2)))
        {
            int cy = y >> 1;
            bool upper = (y & 1) == 0;
            int fy = upper ? Math.Max(cy - 1, 0) : Math.Min(cy + 1, c.Height - 1);
            byte* near = plane + (long)cy * stride, far = plane + (long)fy * stride;
            if (hexp == 1) H1V2(near, far, outp, w, upper ? 1 : 2);
            else H2V2(near, far, outp, w);
        }
        else
        {
            byte* src = plane + (long)(y / vexp) * stride;
            byte* o = outp;
            for (int x = 0; x < w; x++)
            {
                byte v = src[x];
                for (int k = 0; k < hexp; k++) *o++ = v;
            }
        }
        return outp;
    }

    static void H2V1(byte* inp, byte* o, int w)
    {
        int v = inp[0];
        o[0] = (byte)v;
        o[1] = (byte)((v * 3 + inp[1] + 2) >> 2);
        for (int x = 1; x < w - 1; x++)
        {
            v = inp[x] * 3;
            o[2 * x] = (byte)((v + inp[x - 1] + 1) >> 2);
            o[2 * x + 1] = (byte)((v + inp[x + 1] + 2) >> 2);
        }
        v = inp[w - 1];
        o[2 * w - 2] = (byte)((v * 3 + inp[w - 2] + 1) >> 2);
        o[2 * w - 1] = (byte)v;
    }

    static void H1V2(byte* near, byte* far, byte* o, int w, int bias)
    {
        for (int x = 0; x < w; x++) o[x] = (byte)((near[x] * 3 + far[x] + bias) >> 2);
    }

    static void H2V2(byte* near, byte* far, byte* o, int w)
    {
        int thisSum = near[0] * 3 + far[0];
        int nextSum = near[1] * 3 + far[1];
        o[0] = (byte)((thisSum * 4 + 8) >> 4);
        o[1] = (byte)((thisSum * 3 + nextSum + 7) >> 4);
        int lastSum = thisSum;
        thisSum = nextSum;
        for (int x = 1; x < w - 1; x++)
        {
            nextSum = near[x + 1] * 3 + far[x + 1];
            o[2 * x] = (byte)((thisSum * 3 + lastSum + 8) >> 4);
            o[2 * x + 1] = (byte)((thisSum * 3 + nextSum + 7) >> 4);
            lastSum = thisSum;
            thisSum = nextSum;
        }
        o[2 * w - 2] = (byte)((thisSum * 3 + lastSum + 8) >> 4);
        o[2 * w - 1] = (byte)((thisSum * 4 + 7) >> 4);
    }

    // ---------------------------------------------------------------- 颜色转换

    static byte Clamp(int v) => v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)v;

    /// <summary>写一个不透明像素（RGBA 字节序）。小端机器上合并为一次 32 位写入。</summary>
    static void Store(byte* d, int r, int g, int b)
    {
        if (BitConverter.IsLittleEndian)
        {
            *(uint*)d = (uint)(r | g << 8 | b << 16) | 0xFF000000u;
        }
        else
        {
            d[0] = (byte)r; d[1] = (byte)g; d[2] = (byte)b; d[3] = 255;
        }
    }

    static void ConvertGray(byte* g, byte* d, int w)
    {
        for (int x = 0; x < w; x++, d += 4)
        {
            int v = g[x];
            Store(d, v, v, v);
        }
    }

    static void ConvertYCbCr(byte* yp, byte* cbp, byte* crp, byte* d, int w, in ColorTables t)
    {
        int* crR = t.CrR, cbB = t.CbB, crG = t.CrG, cbG = t.CbG;
        byte* lim = t.Limit;
        for (int x = 0; x < w; x++, d += 4)
        {
            int y = yp[x], cb = cbp[x], cr = crp[x];
            Store(d, lim[y + crR[cr]], lim[y + ((cbG[cb] + crG[cr]) >> ScaleBits)], lim[y + cbB[cb]]);
        }
    }

    static void ConvertRgb(byte* r, byte* g, byte* b, byte* d, int w)
    {
        for (int x = 0; x < w; x++, d += 4) Store(d, r[x], g[x], b[x]);
    }

    /// <summary>Adobe 反相 CMYK：存储值 = 255 − 墨量，故 R = C·K / 255（与 libjpeg-turbo 的 cmyk_to_rgb 相同）。</summary>
    static void ConvertCmyk(byte* c, byte* m, byte* yy, byte* k, byte* d, int w)
    {
        for (int x = 0; x < w; x++, d += 4)
        {
            int kv = k[x];
            Store(d, (c[x] * kv + 127) / 255, (m[x] * kv + 127) / 255, (yy[x] * kv + 127) / 255);
        }
    }

    /// <summary>YCCK → CMYK（libjpeg ycck_cmyk_convert）→ RGB。</summary>
    static void ConvertYcck(byte* yp, byte* cbp, byte* crp, byte* k, byte* d, int w, in ColorTables t)
    {
        int* crR = t.CrR, cbB = t.CbB, crG = t.CrG, cbG = t.CbG;
        byte* lim = t.Limit;
        for (int x = 0; x < w; x++, d += 4)
        {
            int y = yp[x], cb = cbp[x], cr = crp[x], kv = k[x];
            int c = 255 - lim[y + crR[cr]];
            int m = 255 - lim[y + ((cbG[cb] + crG[cr]) >> ScaleBits)];
            int yel = 255 - lim[y + cbB[cb]];
            Store(d, (c * kv + 127) / 255, (m * kv + 127) / 255, (yel * kv + 127) / 255);
        }
    }
}
