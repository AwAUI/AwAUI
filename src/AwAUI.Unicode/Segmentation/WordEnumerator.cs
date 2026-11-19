using System;
using System.Runtime.CompilerServices;

namespace AwAUI.Unicode;

/// <summary>
/// 按 UAX #29 默认单词边界（WB1–WB999）枚举文本（不分配）。
/// 空白、标点各自成段；<see cref="WordSegment.IsWordLike"/> 标出含字母、数字或表意文字的段。
/// 不做词典断词：泰文、老挝文、高棉文、缅甸文等（Line_Break=SA）的字母 WB=Other，按默认规则每个字符各成一段（均为 IsWordLike）；
/// 中日文表意文字同样逐字成段。
/// </summary>
public ref struct WordEnumerator
{
    const int Other = (int)WordBreak.Other, CR = (int)WordBreak.CR, LF = (int)WordBreak.LF, Newline = (int)WordBreak.Newline;
    const int Extend = (int)WordBreak.Extend, ZWJ = (int)WordBreak.ZWJ, RI = (int)WordBreak.RegionalIndicator, Format = (int)WordBreak.Format;
    const int Katakana = (int)WordBreak.Katakana, HL = (int)WordBreak.HebrewLetter, ALetter = (int)WordBreak.ALetter;
    const int SQ = (int)WordBreak.SingleQuote, DQ = (int)WordBreak.DoubleQuote, MidNumLet = (int)WordBreak.MidNumLet;
    const int MidLetter = (int)WordBreak.MidLetter, MidNum = (int)WordBreak.MidNum, Numeric = (int)WordBreak.Numeric;
    const int ExtendNumLet = (int)WordBreak.ExtendNumLet, WSegSpace = (int)WordBreak.WSegSpace;
    const int None = 31; // sot 或尚无有效字符

    readonly ReadOnlySpan<char> _text;
    int _pos;
    // 跨段保留的状态（规则需要的上下文可能跨越边界，例如 WB7 的 AHLetter Mid × AHLetter 在 Mid 前后都断开时）
    int _prevRaw;       // 紧邻的上一个码位的 WB
    int _prev;          // WB4 之后的上一个有效字符
    int _prevPrev;      // 再上一个有效字符
    bool _riOdd;        // 有效字符中紧邻的连续 RI 个数为奇数

    internal WordEnumerator(ReadOnlySpan<char> text)
    {
        _text = text;
        _pos = 0;
        _prevRaw = None;
        _prev = None;
        _prevPrev = None;
        _riOdd = false;
        Current = default;
    }

    /// <summary>当前段。</summary>
    public WordSegment Current { get; private set; }

    /// <summary>供 foreach 使用。</summary>
    public WordEnumerator GetEnumerator() => this;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int Wb(int seg) => (seg >> UnicodeTables.SegWbShift) & UnicodeTables.SegWbMask;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool IsAHLetter(int wb) => wb == ALetter || wb == HL;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool IsMidNumLetQ(int wb) => wb == MidNumLet || wb == SQ;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool IsIgnorable(int wb) => wb == Extend || wb == Format || wb == ZWJ;

    /// <summary>前进到下一段。</summary>
    public bool MoveNext()
    {
        var text = _text;
        int start = _pos;
        if (start >= text.Length) return false;
        bool wordLike = false;
        int p = start;
        while (true)
        {
            int cp = CodePoint.Decode(text, p, out int len);
            int seg = UnicodeTables.Segment(cp);
            int next = Wb(seg);
            if (p > start && IsBreak(text, p + len, next, seg)) break;
            if (!wordLike) wordLike = IsWordLikeChar(next, cp);
            Consume(next);
            p += len;
            if (p >= text.Length) break;
        }
        _pos = p;
        Current = new WordSegment(start, p - start, wordLike);
        return true;
    }

    static bool IsWordLikeChar(int wb, int cp)
    {
        // ExtendNumLet（如 '_'）只在与字母/数字相连时才属于词，单独成段时不算
        if (wb == ALetter || wb == HL || wb == Numeric || wb == Katakana) return true;
        // 表意文字、泰文等 WB=Other 的字母：按通用类别判断（L* 的取值为 0..4）
        return wb == Other && UnicodeTables.GeneralCategory(cp) <= (int)GeneralCategory.OtherLetter;
    }

    void Consume(int next)
    {
        if (IsIgnorable(next) && _prev != None && _prevRaw != CR && _prevRaw != LF && _prevRaw != Newline)
        {
            // WB4：附着到前一个有效字符，不改变有效上下文
            _prevRaw = next;
            return;
        }
        _prevPrev = _prev;
        _prev = next;
        _riOdd = next == RI && !_riOdd;
        _prevRaw = next;
    }

    /// <summary>判断下一个码位（WB 为 <paramref name="next"/>，之后的文本从 <paramref name="after"/> 开始）之前是否断开。</summary>
    readonly bool IsBreak(ReadOnlySpan<char> text, int after, int next, int nextSeg)
    {
        int prevRaw = _prevRaw;
        if (prevRaw == CR && next == LF) return false;                                     // WB3
        if (prevRaw == CR || prevRaw == LF || prevRaw == Newline) return true;             // WB3a
        if (next == CR || next == LF || next == Newline) return true;                      // WB3b
        if (prevRaw == ZWJ && (nextSeg & UnicodeTables.SegExtPictBit) != 0) return false;  // WB3c
        if (prevRaw == WSegSpace && next == WSegSpace) return false;                       // WB3d
        if (IsIgnorable(next)) return false;                                               // WB4
        int prev = _prev;
        if (IsAHLetter(prev) && IsAHLetter(next)) return false;                            // WB5
        if (IsAHLetter(prev) && (next == MidLetter || IsMidNumLetQ(next)) && IsAHLetter(PeekEffective(text, after))) return false; // WB6
        if (IsAHLetter(_prevPrev) && (prev == MidLetter || IsMidNumLetQ(prev)) && IsAHLetter(next)) return false;                  // WB7
        if (prev == HL && next == SQ) return false;                                        // WB7a
        if (prev == HL && next == DQ && PeekEffective(text, after) == HL) return false;    // WB7b
        if (_prevPrev == HL && prev == DQ && next == HL) return false;                     // WB7c
        if (prev == Numeric && next == Numeric) return false;                              // WB8
        if (IsAHLetter(prev) && next == Numeric) return false;                             // WB9
        if (prev == Numeric && IsAHLetter(next)) return false;                             // WB10
        if (_prevPrev == Numeric && (prev == MidNum || IsMidNumLetQ(prev)) && next == Numeric) return false;                       // WB11
        if (prev == Numeric && (next == MidNum || IsMidNumLetQ(next)) && PeekEffective(text, after) == Numeric) return false;      // WB12
        if (prev == Katakana && next == Katakana) return false;                            // WB13
        if ((IsAHLetter(prev) || prev == Numeric || prev == Katakana || prev == ExtendNumLet) && next == ExtendNumLet) return false; // WB13a
        if (prev == ExtendNumLet && (IsAHLetter(next) || next == Numeric || next == Katakana)) return false;                       // WB13b
        if (prev == RI && next == RI && _riOdd) return false;                              // WB15, WB16
        return true;                                                                       // WB999
    }

    /// <summary>从 <paramref name="p"/> 起跳过 Extend/Format/ZWJ，返回下一个有效字符的 WB；到达文本末尾返回 None。</summary>
    static int PeekEffective(ReadOnlySpan<char> text, int p)
    {
        while (p < text.Length)
        {
            int cp = CodePoint.Decode(text, p, out int len);
            int wb = Wb(UnicodeTables.Segment(cp));
            if (!IsIgnorable(wb)) return wb;
            p += len;
        }
        return None;
    }
}
