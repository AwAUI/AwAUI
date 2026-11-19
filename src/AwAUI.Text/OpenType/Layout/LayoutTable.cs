using System;
using System.Collections.Generic;
using System.Threading;

namespace AwAUI.Text.OpenType.Layout;

/// <summary>一个已解析的查找：类型（已穿透扩展子表）、标志、子表的绝对偏移与覆盖摘要。</summary>
internal sealed class OtLookup
{
    public OtLookup(int type, int flag, int markFilteringSet, int[] subtables, GlyphDigest[] subtableDigests, GlyphDigest digest)
    {
        Type = type;
        Flag = flag;
        MarkFilteringSet = markFilteringSet;
        Subtables = subtables;
        SubtableDigests = subtableDigests;
        Digest = digest;
    }

    /// <summary>查找类型（GSUB 1–8、GPOS 1–9；扩展类型已替换为实际类型）。</summary>
    public int Type { get; }
    public int Flag { get; }
    public int MarkFilteringSet { get; }
    /// <summary>各子表相对表开头的偏移（同一查找中的扩展子表都指向同一实际类型）。</summary>
    public int[] Subtables { get; }
    public GlyphDigest Digest { get; }
    /// <summary>每个子表首个字形的覆盖摘要（与 <see cref="Subtables"/> 一一对应）：逐子表快速排除，免去大型 Coverage 表的二分查找。</summary>
    public GlyphDigest[] SubtableDigests { get; }

    /// <summary>匹配属性：低 16 位为 LookupFlag，高 16 位为标记过滤集。</summary>
    public uint Props => (uint)Flag | ((Flag & LookupFlags.UseMarkFilteringSet) != 0 ? (uint)MarkFilteringSet << 16 : 0);

    /// <summary>应用时出现数据错误：此后跳过（坏字体不会在每个字形上反复抛异常）。</summary>
    public volatile bool Broken;
}

internal static class LookupFlags
{
    public const int RightToLeft = 0x0001;
    public const int IgnoreBaseGlyphs = 0x0002;
    public const int IgnoreLigatures = 0x0004;
    public const int IgnoreMarks = 0x0008;
    public const int IgnoreFlags = 0x000E;
    public const int UseMarkFilteringSet = 0x0010;
    public const int MarkAttachmentType = 0xFF00;
}

/// <summary>
/// 'GSUB' 或 'GPOS' 表的公共部分：ScriptList、FeatureList、LookupList。FeatureVariations 被忽略（默认实例）。
/// 查找按需解析并缓存（线程安全：解析在锁内进行，结果不可变）。
/// 不可信输入：许多查找可以指向同一个查找表、许多子表可以共享同一个（很大的）覆盖表，解析工作量因此不受表大小约束；
/// 解析预算与表长度成正比（子表引用各计 1，覆盖表按条目数计且按偏移只计算一次），子表引用超出预算的查找视为损坏，
/// 覆盖表超出预算时摘要退化为「全部可能」（只影响速度，不影响结果）。
/// </summary>
internal sealed class LayoutTable
{
    readonly ReadOnlyMemory<byte> _data;
    readonly int _scriptList, _featureList, _lookupList;
    readonly OtLookup?[] _lookups;
    readonly bool _isGpos;
    readonly object _parseLock = new();
    long _parseBudget;
    Dictionary<int, GlyphDigest>? _coverageDigests;

    /// <summary>扩展查找的类型：GSUB 7，GPOS 9。</summary>
    int ExtensionType => _isGpos ? 9 : 7;

    LayoutTable(ReadOnlyMemory<byte> data, bool isGpos)
    {
        _data = data;
        _isGpos = isGpos;
        var s = data.Span;
        if (Be.U16(s, 0) != 1) throw new InvalidFontException("Unsupported layout table version.");
        _scriptList = Be.U16(s, 4);
        _featureList = Be.U16(s, 6);
        _lookupList = Be.U16(s, 8);
        int count = _lookupList != 0 ? Be.U16(s, _lookupList) : 0;
        _lookups = new OtLookup?[count];
        _parseBudget = Math.Max(1L << 16, 8L * data.Length);
    }

    public static LayoutTable Parse(ReadOnlyMemory<byte> data, bool isGpos) => new(data, isGpos);

    public ReadOnlyMemory<byte> Data => _data;
    public int LookupCount => _lookups.Length;

    // ------------------------------------------------------------------ Script / LangSys / Feature

    /// <summary>查找文字表；返回其偏移，不存在时 −1。</summary>
    public int FindScript(uint tag)
    {
        if (_scriptList == 0) return -1;
        var s = _data.Span;
        int count = Be.U16(s, _scriptList);
        for (int i = 0; i < count; i++)
        {
            int r = _scriptList + 2 + i * 6;
            if (Be.U32(s, r) == tag) return _scriptList + Be.U16(s, r + 4);
        }
        return -1;
    }

    /// <summary>文字表中的语言系统；找不到指定语言时用默认语言系统；都没有时 −1。</summary>
    public int FindLangSys(int script, uint langTag)
    {
        var s = _data.Span;
        if (langTag != 0)
        {
            int count = Be.U16(s, script + 2);
            for (int i = 0; i < count; i++)
            {
                int r = script + 4 + i * 6;
                if (Be.U32(s, r) == langTag) return script + Be.U16(s, r + 4);
            }
        }
        int def = Be.U16(s, script);
        return def != 0 ? script + def : -1;
    }

    /// <summary>语言系统中的特性（必需特性以 isRequired 标出）。</summary>
    public void GetLangSysFeatures(int langSys, List<int> features, out int required)
    {
        var s = _data.Span;
        required = Be.U16(s, langSys + 2);
        if (required == 0xFFFF) required = -1;
        int count = Be.U16(s, langSys + 4);
        for (int i = 0; i < count; i++) features.Add(Be.U16(s, langSys + 6 + i * 2));
    }

    public uint GetFeatureTag(int featureIndex)
    {
        var s = _data.Span;
        int count = Be.U16(s, _featureList);
        if ((uint)featureIndex >= (uint)count) return 0;
        return Be.U32(s, _featureList + 2 + featureIndex * 6);
    }

    public void GetFeatureLookups(int featureIndex, List<int> lookups)
    {
        var s = _data.Span;
        int count = Be.U16(s, _featureList);
        if ((uint)featureIndex >= (uint)count) return;
        int f = _featureList + Be.U16(s, _featureList + 2 + featureIndex * 6 + 4);
        int n = Be.U16(s, f + 2);
        for (int i = 0; i < n; i++) lookups.Add(Be.U16(s, f + 4 + i * 2));
    }

    // ------------------------------------------------------------------ Lookup

    public OtLookup? GetLookup(int index)
    {
        if ((uint)index >= (uint)_lookups.Length) return null;
        var l = Volatile.Read(ref _lookups[index]);
        if (l is not null) return l;
        lock (_parseLock)
        {
            l = Volatile.Read(ref _lookups[index]);
            if (l is not null) return l;
            try
            {
                l = ParseLookup(index);
            }
            catch (InvalidFontException)
            {
                l = new OtLookup(0, 0, 0, Array.Empty<int>(), Array.Empty<GlyphDigest>(), default) { Broken = true };
            }
            Volatile.Write(ref _lookups[index], l);
            return l;
        }
    }

    OtLookup ParseLookup(int index)
    {
        var s = _data.Span;
        int lookup = _lookupList + Be.U16(s, _lookupList + 2 + index * 2);
        int type = Be.U16(s, lookup);
        int flag = Be.U16(s, lookup + 2);
        int count = Be.U16(s, lookup + 4);
        int markSet = (flag & LookupFlags.UseMarkFilteringSet) != 0 ? Be.U16(s, lookup + 6 + count * 2) : 0;
        var subtables = new int[count];
        var digests = new GlyphDigest[count];
        int actualType = type;
        var digest = new GlyphDigest();
        int n = 0;
        for (int i = 0; i < count; i++)
        {
            if (--_parseBudget < 0) throw new InvalidFontException("Layout table has too many subtable references.");
            int st = lookup + Be.U16(s, lookup + 6 + i * 2);
            if (type == ExtensionType)
            {
                // 扩展子表：format(1) extensionLookupType extensionOffset(32 位，相对扩展子表)
                if (Be.U16(s, st) != 1) continue;
                int extType = Be.U16(s, st + 2);
                long target = st + (long)Be.U32(s, st + 4);
                if (target >= s.Length || extType == ExtensionType) continue;
                if (actualType == ExtensionType) actualType = extType;
                else if (extType != actualType) continue;   // 同一查找中的扩展子表类型必须一致
                st = (int)target;
            }
            var d = new GlyphDigest();
            try
            {
                AddPrimaryCoverage(ref d, s, st, actualType);
            }
            catch (InvalidFontException)
            {
                d.AddAll();
            }
            digest.Union(d);
            digests[n] = d;
            subtables[n++] = st;
        }
        if (n != count)
        {
            Array.Resize(ref subtables, n);
            Array.Resize(ref digests, n);
        }
        return new OtLookup(actualType == ExtensionType ? 0 : actualType, flag, markSet, subtables, digests, digest);
    }

    /// <summary>子表首个（输入）字形可能命中的集合。</summary>
    void AddPrimaryCoverage(ref GlyphDigest d, ReadOnlySpan<byte> s, int st, int type)
    {
        int format = Be.U16(s, st);
        bool contextual = _isGpos ? type is 7 or 8 : type is 5 or 6;
        bool chain = _isGpos ? type == 8 : type == 6;
        if (contextual && format == 3)
        {
            if (!chain)
            {
                // glyphCount seqLookupCount coverageOffsets[]
                if (Be.U16(s, st + 2) == 0) return;
                AddCoverage(ref d, s, st + Be.U16(s, st + 6));
            }
            else
            {
                int backtrack = Be.U16(s, st + 2);
                int p = st + 4 + backtrack * 2;
                int input = Be.U16(s, p);
                if (input == 0) return;
                AddCoverage(ref d, s, st + Be.U16(s, p + 2));
            }
            return;
        }
        // 其他所有子表（含上下文格式 1 / 2、反向链式、所有 GPOS 类型）的覆盖表都在 +2
        AddCoverage(ref d, s, st + Be.U16(s, st + 2));
    }

    /// <summary>覆盖表的摘要：按偏移缓存（共享的覆盖表只扫描一次），按条目数计入解析预算，预算不足时退化为全部可能。</summary>
    void AddCoverage(ref GlyphDigest d, ReadOnlySpan<byte> s, int offset)
    {
        var memo = _coverageDigests ??= new Dictionary<int, GlyphDigest>();
        if (!memo.TryGetValue(offset, out var cd))
        {
            long cost = 1 + Be.U16(s, offset + 2);
            if (_parseBudget >= cost)
            {
                _parseBudget -= cost;
                Ot.AddCoverage(ref cd, s, offset);
            }
            else cd.AddAll();
            memo[offset] = cd;
        }
        d.Union(cd);
    }
}
