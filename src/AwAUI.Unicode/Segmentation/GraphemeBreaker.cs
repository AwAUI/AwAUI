using System;
using System.Runtime.CompilerServices;

namespace AwAUI.Unicode;

/// <summary>
/// UAX #29 扩展字素簇的断点状态机（GB1–GB999，含 GB9c 与表情 ZWJ 序列）。
/// 状态只依赖已经过的码位，不需要向后看；按簇起点重新初始化与连续运行等价。
/// </summary>
internal struct GraphemeBreaker
{
    const int Other = (int)GraphemeClusterBreak.Other, CR = (int)GraphemeClusterBreak.CR, LF = (int)GraphemeClusterBreak.LF;
    const int Control = (int)GraphemeClusterBreak.Control, Extend = (int)GraphemeClusterBreak.Extend, ZWJ = (int)GraphemeClusterBreak.ZWJ;
    const int RI = (int)GraphemeClusterBreak.RegionalIndicator, Prepend = (int)GraphemeClusterBreak.Prepend;
    const int SpacingMark = (int)GraphemeClusterBreak.SpacingMark, L = (int)GraphemeClusterBreak.L, V = (int)GraphemeClusterBreak.V;
    const int T = (int)GraphemeClusterBreak.T, LV = (int)GraphemeClusterBreak.LV, LVT = (int)GraphemeClusterBreak.LVT;
    const int InCBLinker = (int)IndicConjunctBreak.Linker, InCBConsonant = (int)IndicConjunctBreak.Consonant, InCBExtend = (int)IndicConjunctBreak.Extend;

    int _prev;          // 上一个码位的 GCB
    bool _riOdd;        // 紧邻之前连续 RI 的个数是否为奇数
    byte _emoji;        // 0：无；1：ExtPict Extend*；2：ExtPict Extend* ZWJ
    bool _linker;       // 处于 InCB=Linker InCB=Extend* 之后

    /// <summary>以簇的第一个码位（Segment 表的打包值）初始化。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reset(int seg)
    {
        _prev = -1;
        _riOdd = false;
        _emoji = 0;
        _linker = false;
        Advance(seg);
    }

    /// <summary>判断下一个码位之前是否断开，并把它并入状态。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool BreakBefore(int seg)
    {
        bool brk = IsBreak(seg);
        Advance(seg);
        return brk;
    }

    readonly bool IsBreak(int seg)
    {
        int next = seg & UnicodeTables.SegGcbMask;
        int prev = _prev;
        if (prev == CR && next == LF) return false;                                   // GB3
        if (prev == Control || prev == CR || prev == LF) return true;                 // GB4
        if (next == Control || next == CR || next == LF) return true;                 // GB5
        if (prev == L && (next == L || next == V || next == LV || next == LVT)) return false;   // GB6
        if ((prev == LV || prev == V) && (next == V || next == T)) return false;      // GB7
        if ((prev == LVT || prev == T) && next == T) return false;                    // GB8
        if (next == Extend || next == ZWJ || next == SpacingMark) return false;       // GB9, GB9a
        if (prev == Prepend) return false;                                            // GB9b
        if (_linker && ((seg >> UnicodeTables.SegInCBShift) & 3) == InCBConsonant) return false;   // GB9c
        if (_emoji == 2 && (seg & UnicodeTables.SegExtPictBit) != 0) return false;   // GB11
        if (prev == RI && next == RI && _riOdd) return false;                         // GB12, GB13
        return true;                                                                  // GB999
    }

    void Advance(int seg)
    {
        int gcb = seg & UnicodeTables.SegGcbMask;
        int incb = (seg >> UnicodeTables.SegInCBShift) & 3;
        _riOdd = gcb == RI && !_riOdd;
        if ((seg & UnicodeTables.SegExtPictBit) != 0) _emoji = 1;
        else if (_emoji == 1 && gcb == Extend) _emoji = 1;
        else if (_emoji == 1 && gcb == ZWJ) _emoji = 2;
        else _emoji = 0;
        if (incb == InCBLinker) _linker = true;
        else if (!(_linker && incb == InCBExtend)) _linker = false;
        _prev = gcb;
    }

    /// <summary>
    /// 两个相邻码位之间是否一定是簇边界、且与更早的上下文无关（用于向前回溯寻找安全起点）。
    /// 只依赖两侧码位的规则（GB3–GB9b）判定为断开，且依赖更早上下文的规则（GB9c、GB11、GB12/13）不可能适用：
    /// 后者是 InCB=Consonant 时前者不是 InCB=Linker/Extend；后者是 ExtPict 时前者不是 ZWJ；两者不同时为 RI。
    /// 满足时，从后者重新初始化的状态与连续运行的状态完全相同（Extend/ZWJ/SpacingMark 作为后者总是不断开，已被排除）。
    /// </summary>
    public static bool IsSafeBoundary(int prevSeg, int nextSeg)
    {
        int prev = prevSeg & UnicodeTables.SegGcbMask;
        int next = nextSeg & UnicodeTables.SegGcbMask;
        if (prev == CR && next == LF) return false;                                   // GB3
        if (prev == Control || prev == CR || prev == LF) return true;                 // GB4
        if (next == Control || next == CR || next == LF) return true;                 // GB5
        if (prev == L && (next == L || next == V || next == LV || next == LVT)) return false;   // GB6
        if ((prev == LV || prev == V) && (next == V || next == T)) return false;      // GB7
        if ((prev == LVT || prev == T) && next == T) return false;                    // GB8
        if (next == Extend || next == ZWJ || next == SpacingMark) return false;       // GB9, GB9a
        if (prev == Prepend) return false;                                            // GB9b
        int prevInCB = (prevSeg >> UnicodeTables.SegInCBShift) & 3;
        if (((nextSeg >> UnicodeTables.SegInCBShift) & 3) == InCBConsonant
            && (prevInCB == InCBLinker || prevInCB == InCBExtend)) return false;      // GB9c 可能适用
        if ((nextSeg & UnicodeTables.SegExtPictBit) != 0 && prev == ZWJ) return false;   // GB11 可能适用
        if (prev == RI && next == RI) return false;                                   // GB12/13 取决于 RI 的奇偶
        return true;
    }
}

/// <summary>按扩展字素簇枚举文本（不分配）。<see cref="Current"/> 是当前簇的 UTF-16 范围。</summary>
public ref struct GraphemeEnumerator
{
    readonly ReadOnlySpan<char> _text;
    int _pos;
    GraphemeBreaker _state;

    internal GraphemeEnumerator(ReadOnlySpan<char> text)
    {
        _text = text;
        _pos = 0;
        _state = default;
        Current = default;
    }

    /// <summary>当前字素簇。</summary>
    public TextSegment Current { get; private set; }

    /// <summary>供 foreach 使用。</summary>
    public GraphemeEnumerator GetEnumerator() => this;

    /// <summary>前进到下一个字素簇。</summary>
    public bool MoveNext()
    {
        var text = _text;
        int start = _pos;
        if (start >= text.Length) return false;
        int cp = CodePoint.Decode(text, start, out int len);
        _state.Reset(UnicodeTables.Segment(cp));
        int p = start + len;
        while (p < text.Length)
        {
            cp = CodePoint.Decode(text, p, out len);
            if (_state.BreakBefore(UnicodeTables.Segment(cp))) break;
            p += len;
        }
        _pos = p;
        Current = new TextSegment(start, p - start);
        return true;
    }
}
