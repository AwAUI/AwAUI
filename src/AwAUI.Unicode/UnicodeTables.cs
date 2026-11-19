using System;
using System.Runtime.CompilerServices;

namespace AwAUI.Unicode;

/// <summary>
/// 生成表的手写部分：打包位布局与读取辅助。表数据在 Generated/*.g.cs，由 src/tools/UnicodeDataGen 生成。
/// 所有表都是 <c>static ReadOnlySpan&lt;byte&gt;</c>，编译后位于程序集数据段，读取不分配、不需要静态构造。
/// </summary>
internal static partial class UnicodeTables
{
    // Segment 表：GCB[0..3] InCB[4..5] ExtPict[6] WB[7..11] SB[12..15]
    internal const int SegGcbMask = 0xF;
    internal const int SegInCBShift = 4;
    internal const int SegExtPictBit = 1 << 6;
    internal const int SegWbShift = 7;
    internal const int SegWbMask = 0x1F;
    internal const int SegSbShift = 12;

    // Line 表：LB[0..5] EAW[6..8] Pi[9] Pf[10] ExtPict[11] Cn[12] Mn/Mc[13]
    internal const int LineLbMask = 0x3F;
    internal const int LineEawShift = 6;
    internal const int LinePiBit = 1 << 9;
    internal const int LinePfBit = 1 << 10;
    internal const int LineExtPictBit = 1 << 11;
    internal const int LineCnBit = 1 << 12;
    internal const int LineMarkBit = 1 << 13;

    // Bidi 表：Bidi_Class[0..4] Bidi_Paired_Bracket_Type[5..6] Bidi_Mirrored[7]
    internal const int BidiClassMask = 0x1F;
    internal const int BidiBracketShift = 5;
    internal const int BidiMirroredBit = 1 << 7;

    // Shaping 表：Joining_Type[0..2] InSC[3..8] InPC[9..12] Vertical_Orientation[13..14]
    internal const int ShapingJtMask = 0x7;
    internal const int ShapingInscShift = 3;
    internal const int ShapingInscMask = 0x3F;
    internal const int ShapingInpcShift = 9;
    internal const int ShapingInpcMask = 0xF;
    internal const int ShapingVoShift = 13;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int ReadUInt16(ReadOnlySpan<byte> data, int index)
    {
        index <<= 1;
        return data[index] | (data[index + 1] << 8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int ReadInt32(ReadOnlySpan<byte> data, int offset)
        => data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int ReadUInt24(ReadOnlySpan<byte> data, int offset)
        => data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16);

    /// <summary>在 (码位, 值) 3+3 字节对表中二分查找；找不到返回 -1。</summary>
    internal static int LookupPair(ReadOnlySpan<byte> pairs, int count, int cp)
    {
        int lo = 0, hi = count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            int key = ReadUInt24(pairs, mid * 6);
            if (key == cp) return ReadUInt24(pairs, mid * 6 + 3);
            if (key < cp) lo = mid + 1;
            else hi = mid - 1;
        }
        return -1;
    }
}
