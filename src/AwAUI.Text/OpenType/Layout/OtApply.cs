using System;
using AwAUI.Text.Shaping;

namespace AwAUI.Text.OpenType.Layout;

/// <summary>
/// GSUB / GPOS 查找的执行（语义与 HarfBuzz 的 hb_ot_apply_context_t 一致）：跳过规则（查找标志、标记附着类、标记过滤集、ZWJ / ZWNJ）、
/// 上下文匹配与嵌套查找、连字组件跟踪、标记与草书附着。数据错误抛出 <see cref="InvalidFontException"/>，由调用方把该查找标记为损坏。
/// 不可信输入的限制：嵌套深度 <see cref="MaxNesting"/>、上下文长度 <see cref="MaxContextLength"/>、缓冲长度与操作预算（见 <see cref="GlyphBuffer"/>）。
/// </summary>
internal sealed class OtApplyContext
{
    public const int MaxNesting = 8;
    public const int MaxContextLength = 64;

    readonly GlyphBuffer _buffer;
    readonly GdefTable? _gdef;
    LayoutTable _table = null!;
    bool _isGpos;
    public uint LookupMask;
    public uint LookupProps;
    public bool AutoZwj, AutoZwnj;
    int _nestingLeft = MaxNesting;
    int _lastBase = -1, _lastBaseUntil;
    readonly int[] _matchPositions = new int[MaxContextLength];

    public OtApplyContext(GlyphBuffer buffer, GdefTable? gdef)
    {
        _buffer = buffer;
        _gdef = gdef;
    }

    /// <summary>查找应用中途遇到数据错误后恢复缓冲的一致状态（未处理的输入原样保留）。</summary>
    public void RecoverAfterError()
    {
        _nestingLeft = MaxNesting;
        if (_buffer.HaveOutput) _buffer.Sync();
    }

    public void SetTable(LayoutTable table, bool isGpos)
    {
        _table = table;
        _isGpos = isGpos;
    }

    ReadOnlySpan<byte> S => _table.Data.Span;

    // ------------------------------------------------------------------ 驱动

    /// <summary>对整个缓冲应用一个查找（GSUB 经输出缓冲，GPOS 与反向链式替换原地）。返回是否有子表应用成功。</summary>
    public bool ApplyLookup(OtLookup lookup)
    {
        bool any = false;
        var b = _buffer;
        LookupProps = lookup.Props;
        _lastBase = -1;
        _lastBaseUntil = 0;
        var digest = lookup.Digest;
        bool reverse = !_isGpos && lookup.Type == 8;
        if (!reverse)
        {
            if (!_isGpos) b.ClearOutput();
            b.Idx = 0;
            while (b.Idx < b.Len)
            {
                ref var cur = ref b.Info[b.Idx];
                bool applied = false;
                if (b.MaxOps > 0 && digest.MayHave(cur.Glyph) && (cur.Mask & LookupMask) != 0 && CheckGlyphProperty(cur, LookupProps))
                    applied = ApplySubtables(lookup);
                any |= applied;
                if (!applied)
                {
                    if (_isGpos) b.Idx++;
                    else b.NextGlyph();
                }
            }
            if (!_isGpos) b.Sync();
        }
        else
        {
            b.Idx = b.Len - 1;
            while (b.Idx >= 0)
            {
                ref var cur = ref b.Info[b.Idx];
                if (b.MaxOps > 0 && digest.MayHave(cur.Glyph) && (cur.Mask & LookupMask) != 0 && CheckGlyphProperty(cur, LookupProps))
                    any |= ApplySubtables(lookup);
                b.Idx--;
            }
            b.Idx = 0;
        }
        return any;
    }

    bool ApplySubtables(OtLookup lookup)
    {
        var subtables = lookup.Subtables;
        var digests = lookup.SubtableDigests;
        int glyph = _buffer.Cur.Glyph;
        for (int i = 0; i < subtables.Length; i++)
        {
            if (!digests[i].MayHave(glyph)) continue;
            // 每次尝试子表都计入预算（不只是成功的）：恶意字体可以让大量共享覆盖表的子表都通过摘要却都不匹配
            if (--_buffer.MaxOps < 0) return false;
            int st = subtables[i];
            if (_isGpos ? ApplyGpos(lookup.Type, st) : ApplyGsub(lookup.Type, st)) return true;
        }
        return false;
    }

    /// <summary>上下文查找中的嵌套查找：只在当前位置应用一次，不检查当前字形的属性。</summary>
    bool Recurse(int lookupIndex)
    {
        if (_nestingLeft <= 0 || _buffer.MaxOps <= 0) return false;
        var lookup = _table.GetLookup(lookupIndex);
        if (lookup is null || lookup.Broken) return false;
        uint savedProps = LookupProps;
        LookupProps = lookup.Props;
        _nestingLeft--;
        bool ok;
        try
        {
            ok = ApplySubtables(lookup);
        }
        finally
        {
            _nestingLeft++;
            LookupProps = savedProps;
        }
        return ok;
    }

    // ------------------------------------------------------------------ 属性与跳过

    public bool CheckGlyphProperty(in GlyphInfo info, uint matchProps)
    {
        uint props = info.Props;
        if ((props & matchProps & LookupFlags.IgnoreFlags) != 0) return false;
        if ((props & GlyphProps.Mark) != 0)
        {
            if ((matchProps & LookupFlags.UseMarkFilteringSet) != 0)
                return _gdef is not null && _gdef.MarkSetCovers((int)(matchProps >> 16), info.Glyph);
            if ((matchProps & LookupFlags.MarkAttachmentType) != 0)
                return (matchProps & LookupFlags.MarkAttachmentType) == (props & LookupFlags.MarkAttachmentType);
        }
        return true;
    }

    enum Skip : byte { No, Yes, Maybe }

    Skip MaySkip(in GlyphInfo info, uint props, bool ignoreZwnj, bool ignoreZwj)
    {
        if (!CheckGlyphProperty(info, props)) return Skip.Yes;
        if ((info.Flags & (UFlags.DefaultIgnorable | UFlags.Hidden)) == UFlags.DefaultIgnorable
            && (ignoreZwnj || !info.IsZwnj) && (ignoreZwj || !info.IsZwj))
            return Skip.Maybe;
        return Skip.No;
    }

    /// <summary>匹配器：按字形、类别或 Coverage 匹配序列的第 i 项（数组是 16 位值或 16 位偏移）。</summary>
    readonly struct Matcher
    {
        public Matcher(MatchKind kind, int array, int classDef, int baseOffset)
        {
            Kind = kind;
            Array = array;
            ClassDef = classDef;
            Base = baseOffset;
        }

        public MatchKind Kind { get; }
        public int Array { get; }
        public int ClassDef { get; }
        public int Base { get; }
    }

    enum MatchKind : byte { None, Glyph, Class, Coverage }

    bool Matches(in Matcher m, int glyph, int i)
    {
        var s = S;
        int v = Be.U16(s, m.Array + i * 2);
        return m.Kind switch
        {
            MatchKind.Glyph => glyph == v,
            MatchKind.Class => Ot.ClassOf(s, m.ClassDef, glyph) == v,
            MatchKind.Coverage => Ot.Coverage(s, m.Base + v, glyph) != Ot.NotCovered,
            _ => true,
        };
    }

    /// <summary>跳跃迭代器（HarfBuzz skipping_iterator_t）。context = true 用于回溯 / 前瞻（不检查掩码）。</summary>
    struct SkippyIter
    {
        public int Idx;
        public int NumItems;
        public uint Mask;
        public uint Props;
        public bool IgnoreZwnj, IgnoreZwj;
        public Matcher Matcher;
        public int MatchIndex;
    }

    SkippyIter Iter(bool context, int start, int numItems, in Matcher m)
    {
        return new SkippyIter
        {
            Idx = start,
            NumItems = numItems,
            Mask = context ? uint.MaxValue : LookupMask,
            Props = LookupProps,
            IgnoreZwnj = _isGpos || context && AutoZwnj,
            IgnoreZwj = _isGpos || context || AutoZwj,
            Matcher = m,
        };
    }

    bool Next(ref SkippyIter it)
    {
        var b = _buffer;
        while (it.Idx + it.NumItems < b.Len)
        {
            if (--b.MaxOps < 0) return false;
            it.Idx++;
            ref readonly var info = ref b.Info[it.Idx];
            var skip = MaySkip(info, it.Props, it.IgnoreZwnj, it.IgnoreZwj);
            if (skip == Skip.Yes) continue;
            int match = MayMatch(info, ref it);
            if (match == 1 || match == 2 && skip == Skip.No)
            {
                it.NumItems--;
                it.MatchIndex++;
                return true;
            }
            if (skip == Skip.No) return false;
        }
        return false;
    }

    bool Prev(ref SkippyIter it)
    {
        var b = _buffer;
        var arr = b.OutInfo;
        while (it.Idx > it.NumItems - 1)
        {
            if (--b.MaxOps < 0) return false;
            it.Idx--;
            ref readonly var info = ref arr[it.Idx];
            var skip = MaySkip(info, it.Props, it.IgnoreZwnj, it.IgnoreZwj);
            if (skip == Skip.Yes) continue;
            int match = MayMatch(info, ref it);
            if (match == 1 || match == 2 && skip == Skip.No)
            {
                it.NumItems--;
                it.MatchIndex++;
                return true;
            }
            if (skip == Skip.No) return false;
        }
        return false;
    }

    /// <summary>0 = 不匹配，1 = 匹配，2 = 可能（没有匹配函数）。</summary>
    int MayMatch(in GlyphInfo info, ref SkippyIter it)
    {
        if ((info.Mask & it.Mask) == 0) return 0;
        if (it.Matcher.Kind == MatchKind.None) return 2;
        return Matches(it.Matcher, info.Glyph, it.MatchIndex) ? 1 : 0;
    }

    // ------------------------------------------------------------------ 上下文匹配

    /// <summary>从当前字形开始匹配输入序列（count 含第一个字形）。</summary>
    bool MatchInput(int count, in Matcher m, out int endPosition, out int totalComponents)
    {
        var b = _buffer;
        endPosition = b.Idx + 1;
        totalComponents = 0;
        if (count > MaxContextLength) return false;
        var it = Iter(false, b.Idx, count - 1, m);
        ref readonly var first = ref b.Info[b.Idx];
        totalComponents = first.LigNumComps;
        int firstLigId = first.LigId, firstLigComp = first.LigComp;
        int ligbase = 0;   // 0 未检查，1 不可跳过，2 可跳过
        _matchPositions[0] = b.Idx;
        for (int i = 1; i < count; i++)
        {
            if (!Next(ref it)) return false;
            _matchPositions[i] = it.Idx;
            ref readonly var info = ref b.Info[it.Idx];
            int thisLigId = info.LigId, thisLigComp = info.LigComp;
            if (firstLigId != 0 && firstLigComp != 0)
            {
                if (firstLigId != thisLigId || firstLigComp != thisLigComp)
                {
                    if (ligbase == 0)
                    {
                        bool found = false;
                        var outInfo = b.OutInfo;
                        int j = b.BacktrackLen;
                        while (j > 0 && outInfo[j - 1].LigId == firstLigId)
                        {
                            if (outInfo[j - 1].LigComp == 0)
                            {
                                j--;
                                found = true;
                                break;
                            }
                            j--;
                        }
                        ligbase = found && MaySkip(outInfo[j], it.Props, it.IgnoreZwnj, it.IgnoreZwj) == Skip.Yes ? 2 : 1;
                    }
                    if (ligbase == 1) return false;
                }
            }
            else if (thisLigId != 0 && thisLigComp != 0 && thisLigId != firstLigId) return false;
            totalComponents += info.LigNumComps;
        }
        endPosition = it.Idx + 1;
        return true;
    }

    bool MatchBacktrack(int count, in Matcher m)
    {
        var it = Iter(true, _buffer.BacktrackLen, count, m);
        for (int i = 0; i < count; i++)
            if (!Prev(ref it)) return false;
        return true;
    }

    bool MatchLookahead(int count, in Matcher m, int startIndex)
    {
        var it = Iter(true, startIndex - 1, count, m);
        for (int i = 0; i < count; i++)
            if (!Next(ref it)) return false;
        return true;
    }

    /// <summary>对匹配的输入序列应用嵌套查找（HarfBuzz apply_lookup，含缓冲长度变化时的位置修正）。</summary>
    void ApplyNested(int count, int lookupCount, int records, int matchEnd)
    {
        var b = _buffer;
        var pos = _matchPositions;
        int bl = b.BacktrackLen;
        int end = bl + matchEnd - b.Idx;
        int delta0 = bl - b.Idx;
        for (int j = 0; j < count; j++) pos[j] += delta0;

        var s = S;
        for (int i = 0; i < lookupCount; i++)
        {
            int idx = Be.U16(s, records + i * 4);
            int lookupIndex = Be.U16(s, records + i * 4 + 2);
            if (idx >= count) continue;
            int origLen = b.BacktrackLen + b.LookaheadLen;
            if (pos[idx] >= origLen) continue;
            if (!b.MoveTo(pos[idx])) break;
            if (b.MaxOps <= 0) break;
            if (!Recurse(lookupIndex)) continue;

            int newLen = b.BacktrackLen + b.LookaheadLen;
            int delta = newLen - origLen;
            if (delta == 0) continue;
            end += delta;
            if (end < pos[idx])
            {
                delta += pos[idx] - end;
                end = pos[idx];
            }
            int next = idx + 1;
            if (delta > 0)
            {
                if (delta + count > MaxContextLength) break;
            }
            else
            {
                delta = Math.Max(delta, next - count);
                next -= delta;
            }
            Array.Copy(pos, next, pos, next + delta, count - next);
            next += delta;
            count += delta;
            for (int j = idx + 1; j < next; j++) pos[j] = pos[j - 1] + 1;
            for (; next < count; next++) pos[next] += delta;
        }
        b.MoveTo(end);
    }

    // ------------------------------------------------------------------ 字形类别（替换后）

    void SetGlyphClass(ref GlyphInfo info, ushort glyph, ushort classGuess = 0, bool ligature = false, bool component = false)
    {
        ushort props = info.Props;
        props |= GlyphProps.Substituted;
        if (ligature)
        {
            props |= GlyphProps.Ligated;
            props &= unchecked((ushort)~GlyphProps.Multiplied);
        }
        if (component) props |= GlyphProps.Multiplied;
        if (_gdef is { HasGlyphClasses: true })
            props = (ushort)((props & GlyphProps.Preserve) | _gdef.GetGlyphProps(glyph));
        else if (classGuess != 0)
            props = (ushort)((props & GlyphProps.Preserve) | classGuess);
        info.Props = props;
    }

    void ReplaceGlyph(ushort glyph)
    {
        SetGlyphClass(ref _buffer.Cur, glyph);
        _buffer.ReplaceGlyph(glyph);
    }

    // ------------------------------------------------------------------ GSUB

    bool ApplyGsub(int type, int st)
    {
        var s = S;
        var b = _buffer;
        int glyph = b.Cur.Glyph;
        int format = Be.U16(s, st);
        switch (type)
        {
            case 1:
            {
                int ci = Ot.Coverage(s, st + Be.U16(s, st + 2), glyph);
                if (ci == Ot.NotCovered) return false;
                ushort g = format == 1
                    ? (ushort)(glyph + Be.I16(s, st + 4))
                    : format == 2 && ci < Be.U16(s, st + 4) ? Be.U16(s, st + 6 + ci * 2) : (ushort)0xFFFF;
                if (format != 1 && format != 2 || g == 0xFFFF && format == 2) return false;
                ReplaceGlyph(g);
                return true;
            }
            case 2:
            {
                if (format != 1) return false;
                int ci = Ot.Coverage(s, st + Be.U16(s, st + 2), glyph);
                if (ci == Ot.NotCovered || ci >= Be.U16(s, st + 4)) return false;
                int seq = st + Be.U16(s, st + 6 + ci * 2);
                int count = Be.U16(s, seq);
                if (count == 1)
                {
                    ReplaceGlyph(Be.U16(s, seq + 2));
                    return true;
                }
                if (count == 0)
                {
                    b.DeleteGlyph();
                    return true;
                }
                if (b.Room < count - 1) return false;
                ushort klass = b.Cur.IsLigature ? GlyphProps.BaseGlyph : (ushort)0;
                int ligId = b.Cur.LigId;
                for (int i = 0; i < count; i++)
                {
                    ushort g = Be.U16(s, seq + 2 + i * 2);
                    if (ligId == 0) b.Cur.SetLigPropsForComponent(i);
                    SetGlyphClass(ref b.Cur, g, klass, component: true);
                    b.OutputGlyph(g);
                }
                b.SkipGlyph();
                return true;
            }
            case 3:
            {
                if (format != 1) return false;
                int ci = Ot.Coverage(s, st + Be.U16(s, st + 2), glyph);
                if (ci == Ot.NotCovered || ci >= Be.U16(s, st + 4)) return false;
                int set = st + Be.U16(s, st + 6 + ci * 2);
                int count = Be.U16(s, set);
                uint lookupMask = LookupMask;
                if (lookupMask == 0) return false;
                int shift = TrailingZeros(lookupMask);
                uint altIndex = (lookupMask & b.Cur.Mask) >> shift;
                if (altIndex == 0 || altIndex > count) return false;
                ReplaceGlyph(Be.U16(s, set + 2 + ((int)altIndex - 1) * 2));
                return true;
            }
            case 4:
                return format == 1 && ApplyLigature(s, st, glyph);
            case 5:
                return ApplyContext(s, st, format, glyph, chain: false);
            case 6:
                return ApplyContext(s, st, format, glyph, chain: true);
            case 8:
                return ApplyReverseChain(s, st, format, glyph);
            default:
                return false;
        }
    }

    static int TrailingZeros(uint v)
    {
        int n = 0;
        while ((v & 1) == 0 && n < 32)
        {
            v >>= 1;
            n++;
        }
        return n;
    }

    bool ApplyLigature(ReadOnlySpan<byte> s, int st, int glyph)
    {
        int ci = Ot.Coverage(s, st + Be.U16(s, st + 2), glyph);
        if (ci == Ot.NotCovered || ci >= Be.U16(s, st + 4)) return false;
        int set = st + Be.U16(s, st + 6 + ci * 2);
        int ligCount = Be.U16(s, set);
        for (int i = 0; i < ligCount; i++)
        {
            int lig = set + Be.U16(s, set + 2 + i * 2);
            ushort ligGlyph = Be.U16(s, lig);
            int compCount = Be.U16(s, lig + 2);
            if (compCount == 0) continue;
            if (compCount == 1)
            {
                // 只有一个组件：等同单一替换
                SetGlyphClass(ref _buffer.Cur, ligGlyph, GlyphProps.Ligature, ligature: true);
                _buffer.ReplaceGlyph(ligGlyph);
                return true;
            }
            var m = new Matcher(MatchKind.Glyph, lig + 4, 0, 0);
            if (!MatchInput(compCount, m, out int end, out int total)) continue;
            LigateInput(compCount, end, ligGlyph, total);
            return true;
        }
        return false;
    }

    void LigateInput(int count, int matchEnd, ushort ligGlyph, int totalComponents)
    {
        var b = _buffer;
        var pos = _matchPositions;
        b.MergeClusters(b.Idx, matchEnd);
        bool isBaseLigature = b.Info[pos[0]].IsBaseGlyph;
        bool isMarkLigature = b.Info[pos[0]].IsMark;
        for (int i = 1; i < count; i++)
            if (!b.Info[pos[i]].IsMark)
            {
                isBaseLigature = false;
                isMarkLigature = false;
                break;
            }
        bool isLigature = !isBaseLigature && !isMarkLigature;
        ushort klass = isLigature ? GlyphProps.Ligature : (ushort)0;
        int ligId = isLigature ? AllocateLigId() : 0;
        int lastLigId = b.Cur.LigId;
        int lastNumComponents = b.Cur.LigNumComps;
        int componentsSoFar = lastNumComponents;
        if (isLigature)
        {
            b.Cur.SetLigPropsForLigature(ligId, totalComponents);
            b.Cur.Flags &= ~UFlags.NonspacingMark;
        }
        SetGlyphClass(ref b.Cur, ligGlyph, klass, ligature: true);
        b.ReplaceGlyph(ligGlyph);

        for (int i = 1; i < count; i++)
        {
            while (b.Idx < pos[i])
            {
                if (isLigature)
                {
                    int thisComp = b.Cur.LigComp;
                    if (thisComp == 0) thisComp = lastNumComponents;
                    int newLigComp = componentsSoFar - lastNumComponents + Math.Min(thisComp, lastNumComponents);
                    b.Cur.SetLigPropsForMark(ligId, newLigComp);
                }
                b.NextGlyph();
            }
            lastLigId = b.Cur.LigId;
            lastNumComponents = b.Cur.LigNumComps;
            componentsSoFar += lastNumComponents;
            b.SkipGlyph();   // 跳过被合并的组件
        }

        if (!isMarkLigature && lastLigId != 0)
        {
            // 之后附着在原连字组件上的标记重新编号
            for (int i = b.Idx; i < b.Len; i++)
            {
                if (lastLigId != b.Info[i].LigId) break;
                int thisComp = b.Info[i].LigComp;
                if (thisComp == 0) break;
                int newLigComp = componentsSoFar - lastNumComponents + Math.Min(thisComp, lastNumComponents);
                b.Info[i].SetLigPropsForMark(ligId, newLigComp);
            }
        }
    }

    int AllocateLigId()
    {
        int id;
        do id = _buffer.NextSerial() & 7;
        while (id == 0);
        return id;
    }

    bool ApplyContext(ReadOnlySpan<byte> s, int st, int format, int glyph, bool chain)
    {
        switch (format)
        {
            case 1:
            case 2:
            {
                int ci = Ot.Coverage(s, st + Be.U16(s, st + 2), glyph);
                if (ci == Ot.NotCovered) return false;
                int setIndex, setCountPos;
                int backCd = 0, inputCd = 0, aheadCd = 0;
                if (format == 1)
                {
                    setIndex = ci;
                    setCountPos = st + 4;
                }
                else if (!chain)
                {
                    inputCd = st + Be.U16(s, st + 4);
                    setIndex = Ot.ClassOf(s, inputCd, glyph);
                    setCountPos = st + 6;
                }
                else
                {
                    int bo = Be.U16(s, st + 4), io = Be.U16(s, st + 6), ao = Be.U16(s, st + 8);
                    backCd = bo == 0 ? 0 : st + bo;
                    inputCd = io == 0 ? 0 : st + io;
                    aheadCd = ao == 0 ? 0 : st + ao;
                    setIndex = Ot.ClassOf(s, inputCd, glyph);
                    setCountPos = st + 10;
                }
                if (setIndex >= Be.U16(s, setCountPos)) return false;
                int setOff = Be.U16(s, setCountPos + 2 + setIndex * 2);
                if (setOff == 0) return false;
                int set = st + setOff;
                int ruleCount = Be.U16(s, set);
                var kind = format == 1 ? MatchKind.Glyph : MatchKind.Class;
                for (int r = 0; r < ruleCount; r++)
                {
                    int rule = set + Be.U16(s, set + 2 + r * 2);
                    if (!chain)
                    {
                        int inputCount = Be.U16(s, rule);
                        int lookupCount = Be.U16(s, rule + 2);
                        if (inputCount == 0) continue;
                        var mi = new Matcher(kind, rule + 4, inputCd, 0);
                        if (!MatchInput(inputCount, mi, out int end, out _)) continue;
                        ApplyNested(inputCount, lookupCount, rule + 4 + (inputCount - 1) * 2, end);
                        return true;
                    }
                    else
                    {
                        int p = rule;
                        int backCount = Be.U16(s, p);
                        int backArr = p + 2;
                        p += 2 + backCount * 2;
                        int inputCount = Be.U16(s, p);
                        int inputArr = p + 2;
                        if (inputCount == 0) continue;
                        p += 2 + (inputCount - 1) * 2;
                        int aheadCount = Be.U16(s, p);
                        int aheadArr = p + 2;
                        p += 2 + aheadCount * 2;
                        int lookupCount = Be.U16(s, p);
                        if (!MatchInput(inputCount, new Matcher(kind, inputArr, inputCd, 0), out int end, out _)) continue;
                        if (!MatchBacktrack(backCount, new Matcher(kind, backArr, backCd, 0))) continue;
                        if (!MatchLookahead(aheadCount, new Matcher(kind, aheadArr, aheadCd, 0), end)) continue;
                        ApplyNested(inputCount, lookupCount, p + 2, end);
                        return true;
                    }
                }
                return false;
            }
            case 3:
            {
                if (!chain)
                {
                    int inputCount = Be.U16(s, st + 2);
                    int lookupCount = Be.U16(s, st + 4);
                    if (inputCount == 0) return false;
                    if (Ot.Coverage(s, st + Be.U16(s, st + 6), glyph) == Ot.NotCovered) return false;
                    // 输入序列的第 i 项（i ≥ 1）对应 coverageOffsets[i]：匹配器的数组从 coverageOffsets[1] 开始
                    if (!MatchInput(inputCount, new Matcher(MatchKind.Coverage, st + 8, 0, st), out int end, out _)) return false;
                    ApplyNested(inputCount, lookupCount, st + 6 + inputCount * 2, end);
                    return true;
                }
                else
                {
                    int p = st + 2;
                    int backCount = Be.U16(s, p);
                    int backArr = p + 2;
                    p += 2 + backCount * 2;
                    int inputCount = Be.U16(s, p);
                    int inputArr = p + 2;
                    p += 2 + inputCount * 2;
                    int aheadCount = Be.U16(s, p);
                    int aheadArr = p + 2;
                    p += 2 + aheadCount * 2;
                    int lookupCount = Be.U16(s, p);
                    if (inputCount == 0) return false;
                    if (Ot.Coverage(s, st + Be.U16(s, inputArr), glyph) == Ot.NotCovered) return false;
                    if (!MatchInput(inputCount, new Matcher(MatchKind.Coverage, inputArr + 2, 0, st), out int end, out _)) return false;
                    if (!MatchBacktrack(backCount, new Matcher(MatchKind.Coverage, backArr, 0, st))) return false;
                    if (!MatchLookahead(aheadCount, new Matcher(MatchKind.Coverage, aheadArr, 0, st), end)) return false;
                    ApplyNested(inputCount, lookupCount, p + 2, end);
                    return true;
                }
            }
            default:
                return false;
        }
    }

    bool ApplyReverseChain(ReadOnlySpan<byte> s, int st, int format, int glyph)
    {
        if (format != 1 || _nestingLeft != MaxNesting || _buffer.HaveOutput) return false;   // 不能被上下文查找调用
        int ci = Ot.Coverage(s, st + Be.U16(s, st + 2), glyph);
        if (ci == Ot.NotCovered) return false;
        int p = st + 4;
        int backCount = Be.U16(s, p);
        int backArr = p + 2;
        p += 2 + backCount * 2;
        int aheadCount = Be.U16(s, p);
        int aheadArr = p + 2;
        p += 2 + aheadCount * 2;
        int glyphCount = Be.U16(s, p);
        if (ci >= glyphCount) return false;
        if (!MatchBacktrack(backCount, new Matcher(MatchKind.Coverage, backArr, 0, st))) return false;
        if (!MatchLookahead(aheadCount, new Matcher(MatchKind.Coverage, aheadArr, 0, st), _buffer.Idx + 1)) return false;
        ushort g = Be.U16(s, p + 2 + ci * 2);
        SetGlyphClass(ref _buffer.Cur, g);
        _buffer.Cur.Glyph = g;
        return true;
    }

    // ------------------------------------------------------------------ GPOS

    bool ApplyGpos(int type, int st)
    {
        var s = S;
        var b = _buffer;
        int glyph = b.Cur.Glyph;
        int format = Be.U16(s, st);
        switch (type)
        {
            case 1:
            {
                int ci = Ot.Coverage(s, st + Be.U16(s, st + 2), glyph);
                if (ci == Ot.NotCovered) return false;
                int vf = Be.U16(s, st + 4);
                int rec;
                if (format == 1) rec = st + 6;
                else if (format == 2)
                {
                    if (ci >= Be.U16(s, st + 6)) return false;
                    rec = st + 8 + ci * ValueSize(vf);
                }
                else return false;
                ApplyValue(s, vf, rec, ref b.Pos[b.Idx]);
                b.Idx++;
                return true;
            }
            case 2:
                return ApplyPair(s, st, format, glyph);
            case 3:
                return format == 1 && ApplyCursive(s, st, glyph);
            case 4:
                return format == 1 && ApplyMarkBase(s, st, glyph);
            case 5:
                return format == 1 && ApplyMarkLig(s, st, glyph);
            case 6:
                return format == 1 && ApplyMarkMark(s, st, glyph);
            case 7:
                return ApplyContext(s, st, format, glyph, chain: false);
            case 8:
                return ApplyContext(s, st, format, glyph, chain: true);
            default:
                return false;
        }
    }

    static int ValueSize(int format) => PopCount(format & 0xFF) * 2;

    static int PopCount(int v)
    {
        int n = 0;
        while (v != 0)
        {
            v &= v - 1;
            n++;
        }
        return n;
    }

    /// <summary>水平文本的 ValueRecord：设备表被忽略，YAdvance 不作用于水平排版。</summary>
    static bool ApplyValue(ReadOnlySpan<byte> s, int format, int p, ref GlyphPos pos)
    {
        bool any = false;
        if ((format & 1) != 0)
        {
            int v = Be.I16(s, p);
            pos.XOffset += v;
            any |= v != 0;
            p += 2;
        }
        if ((format & 2) != 0)
        {
            int v = Be.I16(s, p);
            pos.YOffset += v;
            any |= v != 0;
            p += 2;
        }
        if ((format & 4) != 0)
        {
            int v = Be.I16(s, p);
            pos.XAdvance += v;
            any |= v != 0;
        }
        return any;
    }

    bool ApplyPair(ReadOnlySpan<byte> s, int st, int format, int glyph)
    {
        var b = _buffer;
        int ci = Ot.Coverage(s, st + Be.U16(s, st + 2), glyph);
        if (ci == Ot.NotCovered) return false;
        var it = Iter(false, b.Idx, 1, default);
        if (!Next(ref it)) return false;
        int second = it.Idx;
        int g2 = b.Info[second].Glyph;
        int vf1 = Be.U16(s, st + 4), vf2 = Be.U16(s, st + 6);
        int len1 = ValueSize(vf1), len2 = ValueSize(vf2);
        int rec;
        if (format == 1)
        {
            if (ci >= Be.U16(s, st + 8)) return false;
            int set = st + Be.U16(s, st + 10 + ci * 2);
            int count = Be.U16(s, set);
            int size = 2 + len1 + len2;
            int lo = 0, hi = count - 1;
            rec = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int r = set + 2 + mid * size;
                int sg = Be.U16(s, r);
                if (sg < g2) lo = mid + 1;
                else if (sg > g2) hi = mid - 1;
                else
                {
                    rec = r + 2;
                    break;
                }
            }
            if (rec < 0) return false;
        }
        else if (format == 2)
        {
            int c1 = Ot.ClassOf(s, st + Be.U16(s, st + 8), glyph);
            int c2 = Ot.ClassOf(s, st + Be.U16(s, st + 10), g2);
            int c1Count = Be.U16(s, st + 12), c2Count = Be.U16(s, st + 14);
            if (c1 >= c1Count || c2 >= c2Count) return false;
            rec = st + 16 + (c1 * c2Count + c2) * (len1 + len2);
        }
        else return false;
        bool a1 = ApplyValue(s, vf1, rec, ref b.Pos[b.Idx]);
        bool a2 = ApplyValue(s, vf2, rec + len1, ref b.Pos[second]);
        if (a1 || a2) b.Info[second].Flags |= UFlags.UnsafeToBreak;
        if (len2 != 0) second++;
        b.Idx = second;
        return true;
    }

    void ReadAnchor(ReadOnlySpan<byte> s, int anchor, out int x, out int y)
    {
        int format = Be.U16(s, anchor);
        if (format < 1 || format > 3) throw new InvalidFontException("Invalid anchor format.");
        x = Be.I16(s, anchor + 2);
        y = Be.I16(s, anchor + 4);
    }

    bool ApplyCursive(ReadOnlySpan<byte> s, int st, int glyph)
    {
        var b = _buffer;
        int ci = Ot.Coverage(s, st + Be.U16(s, st + 2), glyph);
        if (ci == Ot.NotCovered || ci >= Be.U16(s, st + 4)) return false;
        int entry = Be.U16(s, st + 6 + ci * 4);
        if (entry == 0) return false;
        var it = Iter(false, b.Idx, 1, default);
        if (!Prev(ref it)) return false;
        int i = it.Idx, j = b.Idx;
        int pci = Ot.Coverage(s, st + Be.U16(s, st + 2), b.Info[i].Glyph);
        if (pci == Ot.NotCovered || pci >= Be.U16(s, st + 4)) return false;
        int exit = Be.U16(s, st + 6 + pci * 4 + 2);
        if (exit == 0) return false;
        ReadAnchor(s, st + exit, out int exitX, out int exitY);
        ReadAnchor(s, st + entry, out int entryX, out int entryY);
        var pos = b.Pos;
        if (!b.RightToLeft)
        {
            pos[i].XAdvance = exitX + pos[i].XOffset;
            int d = entryX + pos[j].XOffset;
            pos[j].XAdvance -= d;
            pos[j].XOffset -= d;
        }
        else
        {
            int d = exitX + pos[i].XOffset;
            pos[i].XAdvance -= d;
            pos[i].XOffset -= d;
            pos[j].XAdvance = entryX + pos[j].XOffset;
        }
        int child = i, parent = j;
        int yOffset = entryY - exitY;
        if ((LookupProps & LookupFlags.RightToLeft) == 0)
        {
            (child, parent) = (parent, child);
            yOffset = -yOffset;
        }
        ReverseCursiveMinorOffset(pos, child, parent, b.Len);
        pos[child].AttachType = GlyphPos.AttachCursive;
        pos[child].AttachChain = parent - child;
        pos[child].YOffset = yOffset;
        if (pos[parent].AttachChain == -pos[child].AttachChain)
        {
            pos[parent].AttachChain = 0;
            pos[parent].YOffset = 0;
        }
        b.Info[j].Flags |= UFlags.UnsafeToBreak;
        b.Idx++;
        return true;
    }

    /// <summary>反转子节点原有的草书附着链（HarfBuzz reverse_cursive_minor_offset 的迭代版本：先沿链收集，再自底向上改写）。</summary>
    static void ReverseCursiveMinorOffset(GlyphPos[] pos, int i, int newParent, int len)
    {
        Span<int> nodes = stackalloc int[MaxNesting * 8];
        Span<int> chains = stackalloc int[MaxNesting * 8];
        int n = 0;
        int cur = i;
        while (n < nodes.Length)
        {
            int chain = pos[cur].AttachChain;
            if (chain == 0 || (pos[cur].AttachType & GlyphPos.AttachCursive) == 0) break;
            pos[cur].AttachChain = 0;
            int j = cur + chain;
            if (j == newParent || (uint)j >= (uint)len) break;
            nodes[n] = cur;
            chains[n] = chain;
            n++;
            cur = j;
        }
        for (int k = n - 1; k >= 0; k--)
        {
            int from = nodes[k], j = from + chains[k];
            pos[j].YOffset = -pos[from].YOffset;
            pos[j].AttachChain = -chains[k];
            pos[j].AttachType = GlyphPos.AttachCursive;
        }
    }

    bool MarkArrayApply(ReadOnlySpan<byte> s, int markArray, int markIndex, int glyphIndex, int anchorArray, int classCount, int glyphPos)
    {
        var b = _buffer;
        if (markIndex >= Be.U16(s, markArray)) return false;
        int rec = markArray + 2 + markIndex * 4;
        int markClass = Be.U16(s, rec);
        int markAnchor = markArray + Be.U16(s, rec + 2);
        if (markClass >= classCount) return false;
        int baseAnchorOff = Be.U16(s, anchorArray + 2 + (glyphIndex * classCount + markClass) * 2);
        if (baseAnchorOff == 0) return false;
        ReadAnchor(s, markAnchor, out int mx, out int my);
        ReadAnchor(s, anchorArray + baseAnchorOff, out int bx, out int by);
        ref var o = ref b.Pos[b.Idx];
        o.XOffset = bx - mx;
        // 与 HarfBuzz（11.x 之后）一致：竖直偏移在附着时就加上基字形（含其草书附着链）当时的竖直偏移，之后不再传递
        o.YOffset = by - my + ResolveCrossOffset(b.Pos, b.Len, glyphPos);
        o.AttachType = GlyphPos.AttachMark;
        o.AttachChain = glyphPos - b.Idx;
        b.Idx++;
        return true;
    }

    static int ResolveCrossOffset(GlyphPos[] pos, int len, int glyphPos)
    {
        int offset = pos[glyphPos].YOffset;
        for (int count = 1; count < len && (pos[glyphPos].AttachType & GlyphPos.AttachCursive) != 0; count++)
        {
            int chain = pos[glyphPos].AttachChain;
            if (chain == 0) break;
            int parent = glyphPos + chain;
            if ((uint)parent >= (uint)len) break;
            glyphPos = parent;
            offset += pos[glyphPos].YOffset;
        }
        return offset;
    }

    /// <summary>向前寻找附着目标（跳过标记）；与 HarfBuzz 一样缓存上一次的结果，避免长串标记时的二次方扫描。</summary>
    int FindBase(bool checkAccept, ReadOnlySpan<byte> s, int baseCoverage)
    {
        var b = _buffer;
        if (_lastBaseUntil > b.Idx)
        {
            _lastBaseUntil = 0;
            _lastBase = -1;
        }
        var it = Iter(false, 0, 0, default);
        it.Props = LookupFlags.IgnoreMarks;
        for (int j = b.Idx; j > _lastBaseUntil; j--)
        {
            ref readonly var info = ref b.Info[j - 1];
            var skip = MaySkip(info, it.Props, it.IgnoreZwnj, it.IgnoreZwj);
            if (skip == Skip.Yes) continue;
            int mm = (info.Mask & it.Mask) == 0 ? 0 : 2;
            bool match = mm == 2 && skip == Skip.No;
            if (match && checkAccept && !Accept(j - 1) && Ot.Coverage(s, baseCoverage, info.Glyph) == Ot.NotCovered) match = false;
            if (match)
            {
                _lastBase = j - 1;
                break;
            }
        }
        _lastBaseUntil = b.Idx;
        return _lastBase;
    }

    bool Accept(int idx)
    {
        var info = _buffer.Info;
        return !info[idx].IsMultiplied || info[idx].LigComp == 0 || idx == 0 || info[idx - 1].IsMark || !info[idx - 1].IsMultiplied
            || info[idx].LigId != info[idx - 1].LigId || info[idx].LigComp != info[idx - 1].LigComp + 1;
    }

    bool ApplyMarkBase(ReadOnlySpan<byte> s, int st, int glyph)
    {
        int markIndex = Ot.Coverage(s, st + Be.U16(s, st + 2), glyph);
        if (markIndex == Ot.NotCovered) return false;
        int baseCov = st + Be.U16(s, st + 4);
        int idx = FindBase(true, s, baseCov);
        if (idx < 0) return false;
        int baseIndex = Ot.Coverage(s, baseCov, _buffer.Info[idx].Glyph);
        if (baseIndex == Ot.NotCovered) return false;
        int classCount = Be.U16(s, st + 6);
        int markArray = st + Be.U16(s, st + 8);
        int baseArray = st + Be.U16(s, st + 10);
        if (baseIndex >= Be.U16(s, baseArray)) return false;
        return MarkArrayApply(s, markArray, markIndex, baseIndex, baseArray, classCount, idx);
    }

    bool ApplyMarkLig(ReadOnlySpan<byte> s, int st, int glyph)
    {
        var b = _buffer;
        int markIndex = Ot.Coverage(s, st + Be.U16(s, st + 2), glyph);
        if (markIndex == Ot.NotCovered) return false;
        int idx = FindBase(false, s, 0);
        if (idx < 0) return false;
        int ligIndex = Ot.Coverage(s, st + Be.U16(s, st + 4), b.Info[idx].Glyph);
        if (ligIndex == Ot.NotCovered) return false;
        int classCount = Be.U16(s, st + 6);
        int markArray = st + Be.U16(s, st + 8);
        int ligArray = st + Be.U16(s, st + 10);
        if (ligIndex >= Be.U16(s, ligArray)) return false;
        int attach = ligArray + Be.U16(s, ligArray + 2 + ligIndex * 2);
        int compCount = Be.U16(s, attach);
        if (compCount == 0) return false;
        int ligId = b.Info[idx].LigId;
        int markId = b.Cur.LigId;
        int markComp = b.Cur.LigComp;
        int compIndex = ligId != 0 && ligId == markId && markComp > 0 ? Math.Min(compCount, markComp) - 1 : compCount - 1;
        return MarkArrayApply(s, markArray, markIndex, compIndex, attach, classCount, idx);
    }

    bool ApplyMarkMark(ReadOnlySpan<byte> s, int st, int glyph)
    {
        var b = _buffer;
        int mark1Index = Ot.Coverage(s, st + Be.U16(s, st + 2), glyph);
        if (mark1Index == Ot.NotCovered) return false;
        var it = Iter(false, b.Idx, 1, default);
        it.Props = LookupProps & ~(uint)LookupFlags.IgnoreFlags;
        if (!Prev(ref it)) return false;
        int j = it.Idx;
        ref readonly var prev = ref b.Info[j];
        if (!prev.IsMark) return false;
        int id1 = b.Cur.LigId, id2 = prev.LigId;
        int comp1 = b.Cur.LigComp, comp2 = prev.LigComp;
        bool good;
        if (id1 == id2) good = id1 == 0 || comp1 == comp2;
        else good = id1 > 0 && comp1 == 0 || id2 > 0 && comp2 == 0;
        if (!good) return false;
        int mark2Index = Ot.Coverage(s, st + Be.U16(s, st + 4), prev.Glyph);
        if (mark2Index == Ot.NotCovered) return false;
        int classCount = Be.U16(s, st + 6);
        int mark1Array = st + Be.U16(s, st + 8);
        int mark2Array = st + Be.U16(s, st + 10);
        if (mark2Index >= Be.U16(s, mark2Array)) return false;
        return MarkArrayApply(s, mark1Array, mark1Index, mark2Index, mark2Array, classCount, j);
    }

    const int MaxBoundarySubtables = 4096;

    /// <summary>
    /// 只计算一对字形之间的成对调整（GPOS 类型 2，经扩展子表）：用于在独立整形的词之间补上字距。
    /// 返回第一个字形前进量的变化（设计单位）。
    /// </summary>
    public static bool TryGetPairAdjustment(LayoutTable gpos, OtLookup lookup, int first, int second, out int firstAdvance)
    {
        firstAdvance = 0;
        if (lookup.Type != 2 || !lookup.Digest.MayHave(first)) return false;
        var s = gpos.Data.Span;
        var subtables = lookup.Subtables;
        var digests = lookup.SubtableDigests;
        int attempts = 0;
        for (int i = 0; i < subtables.Length; i++)
        {
            if (!digests[i].MayHave(first)) continue;
            // 每个词边界都会调用：恶意字体的大量子表不能让每次调用都线性扫描（真实字体的字距查找远少于此）
            if (++attempts > MaxBoundarySubtables) return false;
            int st = subtables[i];
            int format = Be.U16(s, st);
            int ci = Ot.Coverage(s, st + Be.U16(s, st + 2), first);
            if (ci == Ot.NotCovered) continue;
            int vf1 = Be.U16(s, st + 4), vf2 = Be.U16(s, st + 6);
            int len1 = ValueSize(vf1), len2 = ValueSize(vf2);
            int rec = -1;
            if (format == 1)
            {
                if (ci >= Be.U16(s, st + 8)) continue;
                int set = st + Be.U16(s, st + 10 + ci * 2);
                int count = Be.U16(s, set);
                int size = 2 + len1 + len2;
                int lo = 0, hi = count - 1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) >> 1;
                    int r = set + 2 + mid * size;
                    int sg = Be.U16(s, r);
                    if (sg < second) lo = mid + 1;
                    else if (sg > second) hi = mid - 1;
                    else
                    {
                        rec = r + 2;
                        break;
                    }
                }
            }
            else if (format == 2)
            {
                int c1 = Ot.ClassOf(s, st + Be.U16(s, st + 8), first);
                int c2 = Ot.ClassOf(s, st + Be.U16(s, st + 10), second);
                int c1Count = Be.U16(s, st + 12), c2Count = Be.U16(s, st + 14);
                if (c1 < c1Count && c2 < c2Count) rec = st + 16 + (c1 * c2Count + c2) * (len1 + len2);
            }
            if (rec < 0) continue;
            var pos = default(GlyphPos);
            ApplyValue(s, vf1, rec, ref pos);
            firstAdvance = pos.XAdvance;
            return true;
        }
        return false;
    }
}
