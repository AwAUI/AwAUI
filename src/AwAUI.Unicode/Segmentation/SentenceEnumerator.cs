using System;
using System.Runtime.CompilerServices;

namespace AwAUI.Unicode;

/// <summary>按 UAX #29 默认句子边界（SB1–SB998）枚举文本（不分配）。<see cref="Current"/> 是当前句子的 UTF-16 范围。</summary>
public ref struct SentenceEnumerator
{
    const int Other = (int)SentenceBreak.Other, CR = (int)SentenceBreak.CR, LF = (int)SentenceBreak.LF;
    const int Extend = (int)SentenceBreak.Extend, Sep = (int)SentenceBreak.Sep, Format = (int)SentenceBreak.Format;
    const int Sp = (int)SentenceBreak.Sp, Lower = (int)SentenceBreak.Lower, Upper = (int)SentenceBreak.Upper;
    const int OLetter = (int)SentenceBreak.OLetter, Numeric = (int)SentenceBreak.Numeric, ATerm = (int)SentenceBreak.ATerm;
    const int SContinue = (int)SentenceBreak.SContinue, STerm = (int)SentenceBreak.STerm, Close = (int)SentenceBreak.Close;
    const int None = 31;

    // “SATerm Close* Sp* ParaSep?” 的进度
    const int StageNone = 0, StageClose = 1, StageSp = 2, StageParaSep = 3;

    readonly ReadOnlySpan<char> _text;
    int _pos;
    int _prevRaw;    // 紧邻的上一个码位的 SB
    int _prev;       // SB5 之后的上一个有效字符
    int _prevPrev;   // 再上一个有效字符
    int _term;       // 最近的 SATerm（ATerm/STerm），StageNone 时无意义
    int _stage;
    // SB8 向后看的缓存：从 [_lookStart, _lookEnd] 内任一位置开始扫描都在 _lookEnd 处停下，结果相同
    int _lookStart;
    int _lookEnd;
    bool _lookResult;

    internal SentenceEnumerator(ReadOnlySpan<char> text)
    {
        _text = text;
        _pos = 0;
        _prevRaw = None;
        _prev = None;
        _prevPrev = None;
        _term = None;
        _stage = StageNone;
        _lookStart = 0;
        _lookEnd = -1;
        _lookResult = false;
        Current = default;
    }

    /// <summary>当前句子。</summary>
    public TextSegment Current { get; private set; }

    /// <summary>供 foreach 使用。</summary>
    public SentenceEnumerator GetEnumerator() => this;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int Sb(int cp) => UnicodeTables.Segment(cp) >> UnicodeTables.SegSbShift;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool IsParaSep(int sb) => sb == Sep || sb == CR || sb == LF;

    /// <summary>前进到下一个句子。</summary>
    public bool MoveNext()
    {
        var text = _text;
        int start = _pos;
        if (start >= text.Length) return false;
        int p = start;
        while (true)
        {
            int cp = CodePoint.Decode(text, p, out int len);
            int next = Sb(cp);
            if (p > start && IsBreak(text, p, next)) break;
            Consume(next);
            p += len;
            if (p >= text.Length) break;
        }
        _pos = p;
        Current = new TextSegment(start, p - start);
        return true;
    }

    void Consume(int next)
    {
        if ((next == Extend || next == Format) && _prev != None && !IsParaSep(_prevRaw))
        {
            _prevRaw = next; // SB5：附着到前一个有效字符
            return;
        }
        _prevRaw = next;
        _prevPrev = _prev;
        _prev = next;
        if (next == ATerm || next == STerm)
        {
            _term = next;
            _stage = StageClose;
        }
        else if (_stage == StageClose && next == Close) { }
        else if ((_stage == StageClose || _stage == StageSp) && next == Sp) _stage = StageSp;
        else if ((_stage == StageClose || _stage == StageSp) && IsParaSep(next)) _stage = StageParaSep;
        else _stage = StageNone;
    }

    bool IsBreak(ReadOnlySpan<char> text, int p, int next)
    {
        if (_prevRaw == CR && next == LF) return false;                         // SB3
        if (IsParaSep(_prevRaw)) return true;                                   // SB4
        if (next == Extend || next == Format) return false;                     // SB5
        int prev = _prev;
        if (prev == ATerm && next == Numeric) return false;                     // SB6
        if ((_prevPrev == Upper || _prevPrev == Lower) && prev == ATerm && next == Upper) return false;   // SB7
        bool afterTerm = _stage == StageClose || _stage == StageSp;             // SATerm Close* Sp*
        if (afterTerm && _term == ATerm && LowerFollows(text, p)) return false; // SB8
        if (afterTerm && (next == SContinue || next == ATerm || next == STerm)) return false;   // SB8a
        if (_stage == StageClose && (next == Close || next == Sp || IsParaSep(next))) return false;   // SB9
        if (afterTerm && (next == Sp || IsParaSep(next))) return false;         // SB10
        if (_stage != StageNone) return true;                                   // SB11
        return false;                                                           // SB998
    }

    /// <summary>
    /// SB8 的向后看：( ¬(OLetter | Upper | Lower | ParaSep | SATerm) )* Lower（Extend/Format 也被跳过）。
    /// 扫描在第一个“终止字符”处停下，结果只取决于该字符；因此 Close*/Sp* 串中后续位置复用同一结果，整体保持线性。
    /// </summary>
    bool LowerFollows(ReadOnlySpan<char> text, int p)
    {
        if (p >= _lookStart && p <= _lookEnd) return _lookResult;
        int q = p;
        bool result = false;
        while (q < text.Length)
        {
            int sb = Sb(CodePoint.Decode(text, q, out int len));
            if (sb == Lower) { result = true; break; }
            if (sb == OLetter || sb == Upper || IsParaSep(sb) || sb == ATerm || sb == STerm) break;
            q += len;
        }
        _lookStart = p;
        _lookEnd = q;
        _lookResult = result;
        return result;
    }
}
