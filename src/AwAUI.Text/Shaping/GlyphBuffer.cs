using System;

namespace AwAUI.Text.Shaping;

/// <summary>字形属性（与 HarfBuzz 的 glyph_props 一致）：低字节为类别与替换标志，高字节为标记附着类。</summary>
internal static class GlyphProps
{
    public const ushort BaseGlyph = 0x02;
    public const ushort Ligature = 0x04;
    public const ushort Mark = 0x08;
    public const ushort ClassMask = BaseGlyph | Ligature | Mark;
    public const ushort Substituted = 0x10;
    public const ushort Ligated = 0x20;
    public const ushort Multiplied = 0x40;
    public const ushort Preserve = Substituted | Ligated | Multiplied;
}

/// <summary>每个字形的 Unicode 相关标志。</summary>
[Flags]
internal enum UFlags : byte
{
    None = 0,
    /// <summary>Default_Ignorable_Code_Point（不含谚文填充符）。</summary>
    DefaultIgnorable = 1,
    Zwj = 2,
    Zwnj = 4,
    /// <summary>General_Category = Mn。</summary>
    NonspacingMark = 8,
    /// <summary>字素簇的延续字符（与前一个字符同属一个簇）。</summary>
    Continuation = 16,
    /// <summary>整形后不可见（默认可忽略字符）。</summary>
    Hidden = 32,
    /// <summary>此字形之前断开需要重新整形。</summary>
    UnsafeToBreak = 64,
}

/// <summary>整形缓冲中的一个字形（对应 HarfBuzz 的 hb_glyph_info_t 加上整形用的附加字段）。</summary>
internal struct GlyphInfo
{
    public ushort Glyph;
    public ushort Props;
    public int Cluster;
    public int CodePoint;
    public uint Mask;
    /// <summary>连字属性：(lig_id &lt;&lt; 5) | IsLigBase(0x10) | 组件序号或组件数（低 4 位）。</summary>
    public byte LigProps;
    public UFlags Flags;
    /// <summary>阿拉伯连写动作（<see cref="ArabicShaping"/>）。</summary>
    public byte JoiningAction;
    /// <summary>修正后的规范组合类（见 <see cref="MarkOrdering.ModifiedCombiningClass"/>）。</summary>
    public byte Ccc;

    public readonly bool IsMark => (Props & GlyphProps.Mark) != 0;
    public readonly bool IsLigature => (Props & GlyphProps.Ligature) != 0;
    public readonly bool IsBaseGlyph => (Props & GlyphProps.BaseGlyph) != 0;
    public readonly bool IsMultiplied => (Props & GlyphProps.Multiplied) != 0;
    public readonly bool IsLigated => (Props & GlyphProps.Ligated) != 0;

    const byte IsLigBase = 0x10;

    public readonly int LigId => LigProps >> 5;
    public readonly int LigComp => (LigProps & IsLigBase) != 0 ? 0 : LigProps & 0x0F;
    public readonly int LigNumComps => (LigProps & IsLigBase) != 0 && IsLigature ? LigProps & 0x0F : 1;

    public void SetLigPropsForLigature(int ligId, int numComps) => LigProps = (byte)((ligId << 5) | IsLigBase | (numComps & 0x0F));
    public void SetLigPropsForMark(int ligId, int comp) => LigProps = (byte)((ligId << 5) | (comp & 0x0F));
    public void SetLigPropsForComponent(int comp) => SetLigPropsForMark(0, comp);

    public readonly bool IsDefaultIgnorable => (Flags & UFlags.DefaultIgnorable) != 0;
    public readonly bool IsZwj => (Flags & UFlags.Zwj) != 0;
    public readonly bool IsZwnj => (Flags & UFlags.Zwnj) != 0;
}

/// <summary>字形位置（设计单位）与附着链（GPOS 标记 / 草书附着）。</summary>
internal struct GlyphPos
{
    public int XAdvance, YAdvance, XOffset, YOffset;
    /// <summary>附着目标的相对下标（0 = 没有附着）。</summary>
    public int AttachChain;
    /// <summary>1 = 标记，2 = 草书。</summary>
    public byte AttachType;

    public const byte AttachMark = 1, AttachCursive = 2;
}

/// <summary>
/// 整形缓冲：输入数组 + 输出数组（GSUB 的一遍查找从输入读、向输出写，结束时交换），语义与 HarfBuzz 的 hb_buffer_t 一致，
/// 包括上下文查找中 <see cref="MoveTo"/> 的「回卷」。长度有上限（<see cref="MaxLength"/>），超出时拒绝增长而不是抛异常。
/// </summary>
internal sealed class GlyphBuffer
{
    public GlyphInfo[] Info = new GlyphInfo[16];
    GlyphInfo[] _out = new GlyphInfo[16];
    public GlyphPos[] Pos = Array.Empty<GlyphPos>();
    public int Len;
    public int Idx;
    public int OutLen;
    public bool HaveOutput;
    public bool RightToLeft;
    /// <summary>缓冲允许的最大字形数（多重替换的膨胀上限）。</summary>
    public int MaxLength;
    /// <summary>剩余的操作预算（尝试的子表与跳跃迭代步数），耗尽后不再应用查找。</summary>
    public int MaxOps;
    int _serial;

    public void Reset(int capacity)
    {
        if (Info.Length < capacity) Info = new GlyphInfo[capacity];
        if (_out.Length < capacity) _out = new GlyphInfo[capacity];
        Len = Idx = OutLen = 0;
        HaveOutput = false;
        _serial = 0;
    }

    public int NextSerial() => ++_serial;

    public ref GlyphInfo Cur => ref Info[Idx];

    public void Add(in GlyphInfo info)
    {
        Ensure(ref Info, Len + 1);
        Info[Len++] = info;
    }

    static void Ensure(ref GlyphInfo[] a, int size)
    {
        if (a.Length >= size) return;
        var n = new GlyphInfo[Math.Max(size, a.Length * 2)];
        Array.Copy(a, n, a.Length);
        a = n;
    }

    /// <summary>输出端已处理的字形（回溯上下文从这里读取）。</summary>
    public GlyphInfo[] OutInfo => HaveOutput ? _out : Info;
    public int BacktrackLen => HaveOutput ? OutLen : Idx;
    public int LookaheadLen => Len - Idx;

    /// <summary>还能增加多少个字形。</summary>
    public int Room => MaxLength - (OutLen + (Len - Idx));

    public void ClearOutput()
    {
        HaveOutput = true;
        OutLen = 0;
    }

    /// <summary>结束一遍：剩余输入搬到输出，再交换输入与输出。</summary>
    public void Sync()
    {
        if (!HaveOutput) return;
        // 剩余输入
        int rest = Len - Idx;
        Ensure(ref _out, OutLen + rest);
        Array.Copy(Info, Idx, _out, OutLen, rest);
        OutLen += rest;
        (Info, _out) = (_out, Info);
        Len = OutLen;
        HaveOutput = false;
        OutLen = 0;
        Idx = 0;
    }

    public void NextGlyph()
    {
        if (HaveOutput)
        {
            Ensure(ref _out, OutLen + 1);
            _out[OutLen++] = Info[Idx];
        }
        Idx++;
    }

    public void SkipGlyph() => Idx++;

    public void ReplaceGlyph(ushort glyph)
    {
        var info = Info[Idx];
        info.Glyph = glyph;
        Ensure(ref _out, OutLen + 1);
        _out[OutLen++] = info;
        Idx++;
    }

    /// <summary>以当前字形为模板输出一个新字形（不前进输入）。</summary>
    public void OutputGlyph(ushort glyph)
    {
        var info = Info[Idx];
        info.Glyph = glyph;
        Ensure(ref _out, OutLen + 1);
        _out[OutLen++] = info;
    }

    /// <summary>移动到输出位置 <paramref name="i"/>（HarfBuzz move_to：可向前搬入，也可回卷）。</summary>
    public bool MoveTo(int i)
    {
        if (!HaveOutput)
        {
            if (i < 0 || i > Len) return false;
            Idx = i;
            return true;
        }
        if (i < 0 || i > OutLen + (Len - Idx)) return false;
        if (OutLen < i)
        {
            int count = i - OutLen;
            Ensure(ref _out, OutLen + count);
            Array.Copy(Info, Idx, _out, OutLen, count);
            Idx += count;
            OutLen += count;
        }
        else if (OutLen > i)
        {
            int count = OutLen - i;
            if (Idx < count)
            {
                // 输入前部没有足够空间：整体后移
                int shift = count - Idx;
                Ensure(ref Info, Len + shift);
                Array.Copy(Info, Idx, Info, Idx + shift, Len - Idx);
                Len += shift;
                Idx += shift;
            }
            Idx -= count;
            OutLen -= count;
            Array.Copy(_out, OutLen, Info, Idx, count);
        }
        return true;
    }

    /// <summary>删除当前字形（簇值并入相邻字形，与 HarfBuzz delete_glyph 相同）。</summary>
    public void DeleteGlyph()
    {
        int cluster = Info[Idx].Cluster;
        if (Idx + 1 < Len && cluster == Info[Idx + 1].Cluster || OutLen > 0 && cluster == _out[OutLen - 1].Cluster)
        {
            // 簇仍有字形
        }
        else if (OutLen > 0)
        {
            if (cluster < _out[OutLen - 1].Cluster)
            {
                int old = _out[OutLen - 1].Cluster;
                for (int i = OutLen; i > 0 && _out[i - 1].Cluster == old; i--) _out[i - 1].Cluster = cluster;
            }
        }
        else if (Idx + 1 < Len) MergeClusters(Idx, Idx + 2);
        SkipGlyph();
    }

    /// <summary>把输入 [start, end) 的簇合并为其中的最小簇值，并扩展到相邻的同簇字形（含输出端）。</summary>
    public void MergeClusters(int start, int end)
    {
        if (end - start < 2) return;
        int cluster = Info[start].Cluster;
        for (int i = start + 1; i < end; i++) cluster = Math.Min(cluster, Info[i].Cluster);
        if (cluster != Info[end - 1].Cluster)
            while (end < Len && Info[end - 1].Cluster == Info[end].Cluster) end++;
        if (cluster != Info[start].Cluster)
            while (Idx < start && Info[start - 1].Cluster == Info[start].Cluster) start--;
        if (Idx == start && Info[start].Cluster != cluster && HaveOutput)
            for (int i = OutLen; i > 0 && _out[i - 1].Cluster == Info[start].Cluster; i--) _out[i - 1].Cluster = cluster;
        for (int i = start; i < end; i++) Info[i].Cluster = cluster;
    }

    /// <summary>定位阶段前：按最终长度分配位置数组。</summary>
    public void ClearPositions()
    {
        if (Pos.Length < Len) Pos = new GlyphPos[Math.Max(Len, 16)];
        else Array.Clear(Pos, 0, Len);
    }

    public void Reverse()
    {
        Array.Reverse(Info, 0, Len);
        Array.Reverse(Pos, 0, Len);
    }
}
