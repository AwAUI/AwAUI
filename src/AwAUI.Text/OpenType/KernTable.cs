using System;
using System.Collections.Generic;

namespace AwAUI.Text.OpenType;

/// <summary>
/// 'kern' 表的水平格式 0 子表（成对字距）。同时支持 Microsoft（16 位头）与 Apple（32 位头）两种表头。
/// 垂直、跨流方向与「最小值」子表被忽略；其他格式（2、3 等）跳过。
/// </summary>
internal sealed class KernTable
{
    readonly struct PairSubtable
    {
        public PairSubtable(int offset, int count, bool overrides)
        {
            Offset = offset;
            Count = count;
            Override = overrides;
        }

        /// <summary>第一个字距对相对于表开头的位置。</summary>
        public int Offset { get; }
        public int Count { get; }
        public bool Override { get; }
    }

    readonly ReadOnlyMemory<byte> _data;
    readonly PairSubtable[] _subtables;

    KernTable(ReadOnlyMemory<byte> data, PairSubtable[] subtables)
    {
        _data = data;
        _subtables = subtables;
    }

    public int PairCount
    {
        get
        {
            int n = 0;
            foreach (var t in _subtables) n += t.Count;
            return n;
        }
    }

    /// <summary>两个字形之间的字距调整（设计单位），没有时为 0。</summary>
    public int GetKerning(ushort left, ushort right)
    {
        var s = _data.Span;
        uint key = ((uint)left << 16) | right;
        int value = 0;
        foreach (var t in _subtables)
        {
            int lo = 0, hi = t.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int p = t.Offset + mid * 6;
                uint k = Be.U32(s, p);
                if (k < key) lo = mid + 1;
                else if (k > key) hi = mid - 1;
                else
                {
                    short v = Be.I16(s, p + 4);
                    value = t.Override ? v : value + v;
                    break;
                }
            }
        }
        return value;
    }

    /// <summary>解析 kern 表；没有可用的子表时返回 null。</summary>
    public static KernTable? Parse(ReadOnlyMemory<byte> table)
    {
        var s = table.Span;
        if (s.Length < 4) return null;
        var list = new List<PairSubtable>();
        if (Be.U16(s, 0) == 0)
        {
            // Microsoft：version(16) nTables(16)；子表头 version(16) length(16) coverage(16)
            int n = Be.U16(s, 2);
            int pos = 4;
            for (int i = 0; i < n && pos + 6 <= s.Length; i++)
            {
                int length = Be.U16(s, pos + 2);
                int coverage = Be.U16(s, pos + 4);
                int format = coverage >> 8;
                bool horizontal = (coverage & 1) != 0, minimum = (coverage & 2) != 0, cross = (coverage & 4) != 0;
                int pairs = 0;
                if (format == 0 && horizontal && !minimum && !cross)
                    pairs = AddFormat0(s, pos + 6, (coverage & 8) != 0, list);
                else if (format == 0)
                    pairs = Format0PairCount(s, pos + 6);
                // 16 位 length 在大表中会溢出：格式 0 以字距对数量推算实际长度
                int actual = format == 0 ? 6 + 8 + pairs * 6 : length;
                if (actual < 6) break;
                pos += Math.Max(length, actual);
            }
        }
        else if (Be.U32(s, 0) == 0x00010000)
        {
            // Apple：version(32) nTables(32)；子表头 length(32) coverage(8) format(8) tupleIndex(16)
            // coverage 字节：0x80 垂直、0x40 跨流方向、0x20 变体（读作 16 位时位于高字节）
            uint n = Be.U32(s, 4);
            int pos = 8;
            for (uint i = 0; i < n && pos + 8 <= s.Length; i++)
            {
                uint length = Be.U32(s, pos);
                int coverage = Be.U16(s, pos + 4);
                int format = coverage & 0xFF;
                bool vertical = (coverage & 0x8000) != 0, cross = (coverage & 0x4000) != 0, variation = (coverage & 0x2000) != 0;
                if (format == 0 && !vertical && !cross && !variation)
                    AddFormat0(s, pos + 8, false, list);
                if (length < 8 || length > (uint)(s.Length - pos)) break;
                pos += (int)length;
            }
        }
        return list.Count > 0 ? new KernTable(table, list.ToArray()) : null;
    }

    static int Format0PairCount(ReadOnlySpan<byte> s, int header)
    {
        if (header + 8 > s.Length) return 0;
        return Math.Min(Be.U16(s, header), (s.Length - header - 8) / 6);
    }

    static int AddFormat0(ReadOnlySpan<byte> s, int header, bool overrides, List<PairSubtable> list)
    {
        int count = Format0PairCount(s, header);
        if (count > 0) list.Add(new PairSubtable(header + 8, count, overrides));
        return count;
    }
}
