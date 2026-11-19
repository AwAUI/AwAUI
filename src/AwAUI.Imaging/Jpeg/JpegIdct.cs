namespace AwAUI.Imaging.Jpeg;

/// <summary>
/// 8×8 反 DCT：逐位复刻 libjpeg 的 jpeg_idct_islow（Loeffler–Ligtenberg–Moschytz，13 位定点常数，
/// 第一遍保留 2 位额外精度）。唯一差别：输出钳位到 [0,255]，而 libjpeg 对超出 ±512 的极端值做的是回绕。
/// </summary>
#if NET5_0_OR_GREATER
// stackalloc 工作区在读取前都会被完整写入，无需清零
[System.Runtime.CompilerServices.SkipLocalsInit]
#endif
internal static unsafe class JpegIdct
{
    const int ConstBits = 13;
    const int Pass1Bits = 2;

    const int Fix0_298631336 = 2446;
    const int Fix0_390180644 = 3196;
    const int Fix0_541196100 = 4433;
    const int Fix0_765366865 = 6270;
    const int Fix0_899976223 = 7373;
    const int Fix1_175875602 = 9633;
    const int Fix1_501321110 = 12299;
    const int Fix1_847759065 = 15137;
    const int Fix1_961570560 = 16069;
    const int Fix2_053119869 = 16819;
    const int Fix2_562915447 = 20995;
    const int Fix3_072711026 = 25172;

    /// <summary>
    /// 反量化并做 IDCT。<paramref name="coef"/> 与 <paramref name="quant"/> 均为自然（行优先）顺序的 64 项；
    /// 结果写入 <paramref name="dst"/> 起始的 8×8 区域（行距 <paramref name="stride"/>）。
    /// </summary>
    public static void Transform(short* coef, int* quant, byte* dst, int stride)
    {
        int* ws = stackalloc int[64];

        // 第一遍：按列处理输入，结果放入工作区
        for (int c = 0; c < 8; c++)
        {
            short* inp = coef + c;
            int* q = quant + c;
            int* w = ws + c;
            if (inp[8] == 0 && inp[16] == 0 && inp[24] == 0 && inp[32] == 0 &&
                inp[40] == 0 && inp[48] == 0 && inp[56] == 0)
            {
                int dc = (inp[0] * q[0]) << Pass1Bits;
                w[0] = dc; w[8] = dc; w[16] = dc; w[24] = dc; w[32] = dc; w[40] = dc; w[48] = dc; w[56] = dc;
                continue;
            }

            // 偶数部分
            int z2 = inp[16] * q[16];
            int z3 = inp[48] * q[48];
            int z1 = (z2 + z3) * Fix0_541196100;
            int tmp2 = z1 - z3 * Fix1_847759065;
            int tmp3 = z1 + z2 * Fix0_765366865;
            z2 = inp[0] * q[0];
            z3 = inp[32] * q[32];
            int tmp0 = (z2 + z3) << ConstBits;
            int tmp1 = (z2 - z3) << ConstBits;
            int tmp10 = tmp0 + tmp3;
            int tmp13 = tmp0 - tmp3;
            int tmp11 = tmp1 + tmp2;
            int tmp12 = tmp1 - tmp2;

            // 奇数部分
            tmp0 = inp[56] * q[56];
            tmp1 = inp[40] * q[40];
            tmp2 = inp[24] * q[24];
            tmp3 = inp[8] * q[8];
            Odd(ref tmp0, ref tmp1, ref tmp2, ref tmp3);

            const int round = 1 << (ConstBits - Pass1Bits - 1);
            const int shift = ConstBits - Pass1Bits;
            w[0] = (tmp10 + tmp3 + round) >> shift;
            w[56] = (tmp10 - tmp3 + round) >> shift;
            w[8] = (tmp11 + tmp2 + round) >> shift;
            w[48] = (tmp11 - tmp2 + round) >> shift;
            w[16] = (tmp12 + tmp1 + round) >> shift;
            w[40] = (tmp12 - tmp1 + round) >> shift;
            w[24] = (tmp13 + tmp0 + round) >> shift;
            w[32] = (tmp13 - tmp0 + round) >> shift;
        }

        // 第二遍：按行处理工作区，输出像素
        for (int r = 0; r < 8; r++)
        {
            int* w = ws + r * 8;
            byte* o = dst + r * stride;
            if (w[1] == 0 && w[2] == 0 && w[3] == 0 && w[4] == 0 && w[5] == 0 && w[6] == 0 && w[7] == 0)
            {
                byte v = Clamp(((w[0] + (1 << (Pass1Bits + 2))) >> (Pass1Bits + 3)) + 128);
                o[0] = v; o[1] = v; o[2] = v; o[3] = v; o[4] = v; o[5] = v; o[6] = v; o[7] = v;
                continue;
            }

            int z2 = w[2];
            int z3 = w[6];
            int z1 = (z2 + z3) * Fix0_541196100;
            int tmp2 = z1 - z3 * Fix1_847759065;
            int tmp3 = z1 + z2 * Fix0_765366865;
            int tmp0 = (w[0] + w[4]) << ConstBits;
            int tmp1 = (w[0] - w[4]) << ConstBits;
            int tmp10 = tmp0 + tmp3;
            int tmp13 = tmp0 - tmp3;
            int tmp11 = tmp1 + tmp2;
            int tmp12 = tmp1 - tmp2;

            tmp0 = w[7];
            tmp1 = w[5];
            tmp2 = w[3];
            tmp3 = w[1];
            Odd(ref tmp0, ref tmp1, ref tmp2, ref tmp3);

            const int shift = ConstBits + Pass1Bits + 3;
            const int round = 1 << (shift - 1);
            o[0] = Clamp(((tmp10 + tmp3 + round) >> shift) + 128);
            o[7] = Clamp(((tmp10 - tmp3 + round) >> shift) + 128);
            o[1] = Clamp(((tmp11 + tmp2 + round) >> shift) + 128);
            o[6] = Clamp(((tmp11 - tmp2 + round) >> shift) + 128);
            o[2] = Clamp(((tmp12 + tmp1 + round) >> shift) + 128);
            o[5] = Clamp(((tmp12 - tmp1 + round) >> shift) + 128);
            o[3] = Clamp(((tmp13 + tmp0 + round) >> shift) + 128);
            o[4] = Clamp(((tmp13 - tmp0 + round) >> shift) + 128);
        }
    }

    /// <summary>奇数部分（两遍共用）。输入为第 7、5、3、1 项，输出 tmp0..tmp3。</summary>
    static void Odd(ref int tmp0, ref int tmp1, ref int tmp2, ref int tmp3)
    {
        int z1 = tmp0 + tmp3;
        int z2 = tmp1 + tmp2;
        int z3 = tmp0 + tmp2;
        int z4 = tmp1 + tmp3;
        int z5 = (z3 + z4) * Fix1_175875602;

        tmp0 *= Fix0_298631336;
        tmp1 *= Fix2_053119869;
        tmp2 *= Fix3_072711026;
        tmp3 *= Fix1_501321110;
        z1 *= -Fix0_899976223;
        z2 *= -Fix2_562915447;
        z3 = z3 * -Fix1_961570560 + z5;
        z4 = z4 * -Fix0_390180644 + z5;

        tmp0 += z1 + z3;
        tmp1 += z2 + z4;
        tmp2 += z2 + z3;
        tmp3 += z1 + z4;
    }

    static byte Clamp(int v) => v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)v;
}
