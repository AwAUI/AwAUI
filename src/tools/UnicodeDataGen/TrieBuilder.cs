using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AwAUI.Tools.UnicodeDataGen;

/// <summary>
/// 三级查找表（trie）：码位按 [高位 | 中位 | 低位] 拆分。
/// 一级按高位索引二级块号；二级块按中位索引三级块号；三级块按低位给出值。
/// 二、三级块去重共享。各级元素宽 1 或 2 字节，按需要自动选择；在若干位宽组合中取总字节数最小者。
/// </summary>
internal sealed class TrieBuilder
{
    public required string Name { get; init; }
    public int MidBits { get; private set; }
    public int LowBits { get; private set; }
    public int Stage1Width { get; private set; }
    public int Stage2Width { get; private set; }
    public int ValueWidth { get; private set; }
    public byte[] Stage1 { get; private set; } = Array.Empty<byte>();
    public byte[] Stage2 { get; private set; } = Array.Empty<byte>();
    public byte[] Stage3 { get; private set; } = Array.Empty<byte>();
    public int TotalBytes => Stage1.Length + Stage2.Length + Stage3.Length;

    public static TrieBuilder Build(string name, int[] values, int valueWidth)
    {
        if (values.Length != Ucd.CodePointCount) throw new ArgumentException("values");
        foreach (var v in values)
            if (v < 0 || v >= (1 << (8 * valueWidth))) throw new ArgumentException($"{name}: value {v} does not fit in {valueWidth} byte(s).");
        TrieBuilder? best = null;
        for (int low = 3; low <= 8; low++)
            for (int mid = 3; mid <= 8; mid++)
            {
                var t = new TrieBuilder { Name = name };
                if (!t.TryBuild(values, valueWidth, mid, low)) continue;
                // 平局时取更小的位宽组合，保证确定性
                if (best == null || t.TotalBytes < best.TotalBytes) best = t;
            }
        return best ?? throw new InvalidOperationException($"{name}: no trie layout fits.");
    }

    bool TryBuild(int[] values, int valueWidth, int midBits, int lowBits)
    {
        MidBits = midBits;
        LowBits = lowBits;
        ValueWidth = valueWidth;
        int lowSize = 1 << lowBits, midSize = 1 << midBits;
        int s1Len = Ucd.CodePointCount >> (lowBits + midBits);
        if ((s1Len << (lowBits + midBits)) != Ucd.CodePointCount) return false;

        // 三级块去重
        var blocks3 = new List<int[]>();
        var map3 = new Dictionary<int[], int>(BlockComparer.Instance);
        int chunks3 = Ucd.CodePointCount >> lowBits;
        var blockIndex3 = new int[chunks3];
        for (int c = 0; c < chunks3; c++)
        {
            var block = new int[lowSize];
            Array.Copy(values, c << lowBits, block, 0, lowSize);
            if (!map3.TryGetValue(block, out var idx))
            {
                idx = blocks3.Count;
                blocks3.Add(block);
                map3[block] = idx;
            }
            blockIndex3[c] = idx;
        }
        // 二级块去重
        var blocks2 = new List<int[]>();
        var map2 = new Dictionary<int[], int>(BlockComparer.Instance);
        var stage1 = new int[s1Len];
        for (int c = 0; c < s1Len; c++)
        {
            var block = new int[midSize];
            Array.Copy(blockIndex3, c << midBits, block, 0, midSize);
            if (!map2.TryGetValue(block, out var idx))
            {
                idx = blocks2.Count;
                blocks2.Add(block);
                map2[block] = idx;
            }
            stage1[c] = idx;
        }
        if (blocks2.Count > 65536 || blocks3.Count > 65536) return false;
        Stage1Width = blocks2.Count <= 256 ? 1 : 2;
        Stage2Width = blocks3.Count <= 256 ? 1 : 2;
        Stage1 = Pack(stage1, Stage1Width);
        var flat2 = new List<int>();
        foreach (var b in blocks2) flat2.AddRange(b);
        Stage2 = Pack(flat2.ToArray(), Stage2Width);
        var flat3 = new List<int>();
        foreach (var b in blocks3) flat3.AddRange(b);
        Stage3 = Pack(flat3.ToArray(), valueWidth);
        return true;
    }

    sealed class BlockComparer : IEqualityComparer<int[]>
    {
        public static readonly BlockComparer Instance = new();
        public bool Equals(int[]? x, int[]? y) => x.AsSpan().SequenceEqual(y);
        public int GetHashCode(int[] block)
        {
            var h = new HashCode();
            foreach (var v in block) h.Add(v);
            return h.ToHashCode();
        }
    }

    static byte[] Pack(int[] values, int width)
    {
        var result = new byte[values.Length * width];
        for (int i = 0; i < values.Length; i++)
        {
            result[i * width] = (byte)values[i];
            if (width == 2) result[i * width + 1] = (byte)(values[i] >> 8);
        }
        return result;
    }

    /// <summary>生成方法读取的值，用于自检。</summary>
    public int Lookup(int cp)
    {
        int i1 = Read(Stage1, Stage1Width, cp >> (MidBits + LowBits));
        int i2 = Read(Stage2, Stage2Width, (i1 << MidBits) + ((cp >> LowBits) & ((1 << MidBits) - 1)));
        return Read(Stage3, ValueWidth, (i2 << LowBits) + (cp & ((1 << LowBits) - 1)));
    }

    static int Read(byte[] data, int width, int index)
        => width == 1 ? data[index] : data[index * 2] | (data[index * 2 + 1] << 8);

    /// <summary>输出数据属性与查找方法（调用方保证 0 ≤ cp ≤ 0x10FFFF）。</summary>
    public void Emit(StringBuilder sb, string summary)
    {
        string s1 = Name + "Stage1", s2 = Name + "Stage2", s3 = Name + "Stage3";
        int midMask = (1 << MidBits) - 1, lowMask = (1 << LowBits) - 1;
        sb.Append("    /// <summary>").Append(summary).Append(string.Format(CultureInfo.InvariantCulture,
            "三级表：{0}+{1}+{2} 位，元素宽 {3}/{4}/{5} 字节，共 {6} 字节。调用方保证 0 ≤ cp ≤ 0x10FFFF。</summary>\n",
            21 - MidBits - LowBits, MidBits, LowBits, Stage1Width, Stage2Width, ValueWidth, TotalBytes));
        sb.Append("    [MethodImpl(MethodImplOptions.AggressiveInlining)]\n");
        sb.Append("    internal static int ").Append(Name).Append("(int cp)\n    {\n");
        sb.Append("        int i1 = ").Append(ReadExpr(s1, Stage1Width, $"cp >> {MidBits + LowBits}")).Append(";\n");
        sb.Append("        int i2 = ").Append(ReadExpr(s2, Stage2Width, $"(i1 << {MidBits}) + ((cp >> {LowBits}) & {midMask})")).Append(";\n");
        sb.Append("        return ").Append(ReadExpr(s3, ValueWidth, $"(i2 << {LowBits}) + (cp & {lowMask})")).Append(";\n");
        sb.Append("    }\n\n");
        EmitBytes(sb, s1, Stage1);
        EmitBytes(sb, s2, Stage2);
        EmitBytes(sb, s3, Stage3);
    }

    static string ReadExpr(string array, int width, string index)
    {
        if (width == 1) return $"{array}[{index}]";
        return $"ReadUInt16({array}, {index})";
    }

    public static void EmitBytes(StringBuilder sb, string name, byte[] data)
    {
        sb.Append("    internal static ReadOnlySpan<byte> ").Append(name).Append(" => new byte[]\n    {\n");
        for (int i = 0; i < data.Length; i += 48)
        {
            sb.Append("        ");
            int end = Math.Min(data.Length, i + 48);
            for (int j = i; j < end; j++) sb.Append(data[j].ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append('\n');
        }
        sb.Append("    };\n\n");
    }
}
