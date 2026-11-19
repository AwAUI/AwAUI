using System;
using System.Runtime.CompilerServices;

namespace AwAUI.Unicode;

/// <summary>
/// UAX #14 默认断行算法（LB1–LB31，Unicode 18 版规则，未做定制）枚举断行机会（不分配）。
/// 每个机会给出新行起点的 UTF-16 偏移，以及是否为强制换行；文本末尾总是一个强制断点（LB3）。
/// LB1 的默认解析：AI/SG/XX → AL；SA → CM（Mn/Mc）或 AL；CJ 按 <see cref="LineBreakStrictness"/> 解析为 NS（默认）或 ID。
/// 东南亚文字（SA）不做词典断词。
/// </summary>
public ref struct LineBreakEnumerator
{
    // 断行类别常量（与 LineBreakClass 一致），以及两个哨兵：文本开始/结束
    const int XX = (int)LineBreakClass.XX, AI = (int)LineBreakClass.AI, AK = (int)LineBreakClass.AK, AL = (int)LineBreakClass.AL;
    const int AP = (int)LineBreakClass.AP, AS = (int)LineBreakClass.AS, B2 = (int)LineBreakClass.B2, BA = (int)LineBreakClass.BA;
    const int BB = (int)LineBreakClass.BB, BK = (int)LineBreakClass.BK, CB = (int)LineBreakClass.CB, CJ = (int)LineBreakClass.CJ;
    const int CL = (int)LineBreakClass.CL, CM = (int)LineBreakClass.CM, CP = (int)LineBreakClass.CP, CR = (int)LineBreakClass.CR;
    const int EB = (int)LineBreakClass.EB, EM = (int)LineBreakClass.EM, EX = (int)LineBreakClass.EX, GL = (int)LineBreakClass.GL;
    const int H2 = (int)LineBreakClass.H2, H3 = (int)LineBreakClass.H3, HH = (int)LineBreakClass.HH, HL = (int)LineBreakClass.HL;
    const int HY = (int)LineBreakClass.HY, ID = (int)LineBreakClass.ID, IN = (int)LineBreakClass.IN, IS = (int)LineBreakClass.IS;
    const int JL = (int)LineBreakClass.JL, JT = (int)LineBreakClass.JT, JV = (int)LineBreakClass.JV, LF = (int)LineBreakClass.LF;
    const int NL = (int)LineBreakClass.NL, NS = (int)LineBreakClass.NS, NU = (int)LineBreakClass.NU, OP = (int)LineBreakClass.OP;
    const int PO = (int)LineBreakClass.PO, PR = (int)LineBreakClass.PR, QU = (int)LineBreakClass.QU, RI = (int)LineBreakClass.RI;
    const int SA = (int)LineBreakClass.SA, SG = (int)LineBreakClass.SG, SP = (int)LineBreakClass.SP, SY = (int)LineBreakClass.SY;
    const int VF = (int)LineBreakClass.VF, VI = (int)LineBreakClass.VI, WJ = (int)LineBreakClass.WJ, ZW = (int)LineBreakClass.ZW;
    const int ZWJ = (int)LineBreakClass.ZWJ;
    const int Sot = 62, Eot = 63;

    // 单元（LB9 合并后的“基字符 + 组合符号”）的打包：低 8 位类别，其余为标志
    const int ClassMask = 0xFF;
    const int FlagEastAsian = 1 << 8;   // East_Asian_Width ∈ {F, W, H}
    const int FlagPi = 1 << 9;
    const int FlagPf = 1 << 10;
    const int FlagExtPictCn = 1 << 11;  // Extended_Pictographic 且未分配（LB30b）
    const int FlagDottedCircle = 1 << 12; // U+25CC（LB28a）

    const ulong NonAbsorbing = (1UL << BK) | (1UL << CR) | (1UL << LF) | (1UL << NL) | (1UL << SP) | (1UL << ZW);
    const ulong HardBreaks = (1UL << BK) | (1UL << CR) | (1UL << LF) | (1UL << NL);

    // 数字状态（LB25）
    const int NumNone = 0, NumIn = 1, NumAfterClose = 2;

    readonly ReadOnlySpan<char> _text;
    int _pos;               // 下一个待判断的码位
    int _prevUnit;          // 边界前的单元
    int _prevPrevUnit;      // 它之前的单元
    int _lastNonSp;         // 最近的非 SP 单元（之后全是 SP）
    int _lastNonSpPrev;     // 该单元之前的单元
    bool _prevRawZwj;       // 紧邻的上一个码位是 ZWJ（LB8a）
    bool _riOdd;            // 紧邻的连续 RI 单元个数为奇数（LB30a）
    int _num;               // LB25 数字状态
    readonly int _cjClass;  // CJ 的解析结果（NS 或 ID）

    internal LineBreakEnumerator(ReadOnlySpan<char> text, LineBreakStrictness strictness = LineBreakStrictness.Strict)
    {
        _text = text;
        _cjClass = strictness == LineBreakStrictness.Normal ? ID : NS;
        _pos = 0;
        _prevUnit = Sot;
        _prevPrevUnit = Sot;
        _lastNonSp = Sot;
        _lastNonSpPrev = Sot;
        _prevRawZwj = false;
        _riOdd = false;
        _num = NumNone;
        Current = default;
    }

    /// <summary>当前断行机会。</summary>
    public LineBreakOpportunity Current { get; private set; }

    /// <summary>供 foreach 使用。</summary>
    public LineBreakEnumerator GetEnumerator() => this;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool In(int cls, ulong set) => ((1UL << cls) & set) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong Set(int a) => 1UL << a;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong Set(int a, int b) => (1UL << a) | (1UL << b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong Set(int a, int b, int c) => (1UL << a) | (1UL << b) | (1UL << c);

    /// <summary>
    /// LB1：解析码位的断行类别并附带后续规则需要的标志。CM/ZWJ 保持原样（由 LB9/LB10 处理）。
    /// <paramref name="cjClass"/> 为 CJ 的解析结果；向后看的辅助函数只关心 NU/SP/IS 等类别与东亚宽度标志，与 CJ 的解析无关，统一传 NS。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int Classify(int cp, int cjClass)
    {
        int info = UnicodeTables.Line(cp);
        int cls = info & UnicodeTables.LineLbMask;
        switch (cls)
        {
            case AI:
            case SG:
            case XX:
                cls = AL;
                break;
            case SA:
                cls = (info & UnicodeTables.LineMarkBit) != 0 ? CM : AL;
                break;
            case CJ:
                cls = cjClass;
                break;
        }
        int unit = cls;
        int ea = (info >> UnicodeTables.LineEawShift) & 7;
        if (ea == (int)EastAsianWidth.Fullwidth || ea == (int)EastAsianWidth.Wide || ea == (int)EastAsianWidth.Halfwidth) unit |= FlagEastAsian;
        if ((info & UnicodeTables.LinePiBit) != 0) unit |= FlagPi;
        if ((info & UnicodeTables.LinePfBit) != 0) unit |= FlagPf;
        if ((info & (UnicodeTables.LineExtPictBit | UnicodeTables.LineCnBit)) == (UnicodeTables.LineExtPictBit | UnicodeTables.LineCnBit)) unit |= FlagExtPictCn;
        if (cp == 0x25CC) unit |= FlagDottedCircle;
        return unit;
    }

    /// <summary>前进到下一个断行机会。</summary>
    public bool MoveNext()
    {
        var text = _text;
        if (_pos >= text.Length)
        {
            if (_pos == text.Length && text.Length > 0)
            {
                // LB3：文本末尾总是断开
                _pos++;
                Current = new LineBreakOpportunity(text.Length, true);
                return true;
            }
            return false;
        }
        while (_pos < text.Length)
        {
            int p = _pos;
            int cp = CodePoint.Decode(text, p, out int len);
            int raw = Classify(cp, _cjClass);
            int rawCls = raw & ClassMask;
            int decision = p == 0 ? NoBreak : Decide(text, p + len, raw);   // LB2：开头不断开
            // 并入状态
            bool absorbed = (rawCls == CM || rawCls == ZWJ) && _prevUnit != Sot && !In(_prevUnit & ClassMask, NonAbsorbing);
            if (!absorbed)
            {
                int unit = rawCls == CM || rawCls == ZWJ ? AL : raw;   // LB10
                PushUnit(unit);
            }
            _prevRawZwj = rawCls == ZWJ;
            _pos = p + len;
            if (decision != NoBreak)
            {
                Current = new LineBreakOpportunity(p, decision == Mandatory);
                return true;
            }
        }
        // 末尾
        _pos = text.Length + 1;
        Current = new LineBreakOpportunity(text.Length, true);
        return true;
    }

    void PushUnit(int unit)
    {
        int cls = unit & ClassMask;
        _prevPrevUnit = _prevUnit;
        _prevUnit = unit;
        if (cls != SP)
        {
            _lastNonSpPrev = _prevPrevUnit;
            _lastNonSp = unit;
        }
        _riOdd = cls == RI && !_riOdd;
        if (cls == NU) _num = NumIn;
        else if ((cls == SY || cls == IS) && _num == NumIn) _num = NumIn;
        else if ((cls == CL || cls == CP) && _num == NumIn) _num = NumAfterClose;
        else _num = NumNone;
    }

    const int NoBreak = 0, Allowed = 1, Mandatory = 2;

    /// <summary>判断下一个码位（解析后为 <paramref name="raw"/>，其后文本从 <paramref name="after"/> 开始）之前的断行类型。</summary>
    readonly int Decide(ReadOnlySpan<char> text, int after, int raw)
    {
        int rawCls = raw & ClassMask;
        int P = _prevUnit;
        int pc = P & ClassMask;

        // 不可定制部分
        if (pc == BK) return Mandatory;                                         // LB4
        if (pc == CR && rawCls == LF) return NoBreak;                           // LB5
        if (pc == CR || pc == LF || pc == NL) return Mandatory;                 // LB5
        if (In(rawCls, HardBreaks)) return NoBreak;                             // LB6
        if (rawCls == SP || rawCls == ZW) return NoBreak;                       // LB7
        if ((_lastNonSp & ClassMask) == ZW) return Allowed;                     // LB8：ZW SP* ÷
        if (_prevRawZwj) return NoBreak;                                        // LB8a
        if ((rawCls == CM || rawCls == ZWJ) && !In(pc, NonAbsorbing)) return NoBreak;   // LB9
        int N = rawCls == CM || rawCls == ZWJ ? AL : raw;                       // LB10
        int nc = N & ClassMask;

        if (nc == WJ || pc == WJ) return NoBreak;                               // LB11
        if (pc == GL) return NoBreak;                                           // LB12

        // 可定制部分（默认规则）
        if (nc == GL && !In(pc, Set(SP, HY, HH))) return NoBreak;               // LB12a
        if (In(nc, Set(CL, CP) | Set(EX, SY))) return NoBreak;                  // LB13
        int lns = _lastNonSp & ClassMask;
        if (lns == OP) return NoBreak;                                          // LB14：OP SP* ×
        if (lns == QU && (_lastNonSp & FlagPi) != 0
            && In(_lastNonSpPrev & ClassMask, Set(Sot, BK, CR) | Set(LF, NL, OP) | Set(QU, GL, SP) | Set(ZW)))
            return NoBreak;                                                     // LB15a
        if (nc == QU && (N & FlagPf) != 0)
        {
            int nn = PeekUnit(text, after, nc) & ClassMask;
            if (In(nn, Set(SP, GL, WJ) | Set(CL, QU, CP) | Set(EX, IS, SY) | Set(BK, CR, LF) | Set(NL, ZW, Eot)))
                return NoBreak;                                                 // LB15b
        }
        if (pc == SP && nc == IS && (PeekUnit(text, after, nc) & ClassMask) == NU) return Allowed;   // LB15c
        if (nc == IS) return NoBreak;                                           // LB15d
        if (In(lns, Set(CL, CP)) && nc == NS) return NoBreak;                   // LB16
        if (lns == B2 && nc == B2) return NoBreak;                              // LB17
        if (pc == SP) return Allowed;                                           // LB18

        if (nc == QU && (N & FlagPi) == 0) return NoBreak;                      // LB19
        if (pc == QU && (P & FlagPf) == 0) return NoBreak;                      // LB19
        if (nc == QU)
        {
            if ((P & FlagEastAsian) == 0) return NoBreak;                       // LB19a：[^EA] × QU
            int nn = PeekUnit(text, after, nc);
            if ((nn & ClassMask) == Eot || (nn & FlagEastAsian) == 0) return NoBreak;   // LB19a：× QU ([^EA] | eot)
        }
        if (pc == QU)
        {
            if ((N & FlagEastAsian) == 0) return NoBreak;                       // LB19a：QU × [^EA]
            if ((_prevPrevUnit & ClassMask) == Sot || (_prevPrevUnit & FlagEastAsian) == 0) return NoBreak;   // LB19a：(sot | [^EA]) QU ×
        }
        if (nc == CB || pc == CB) return Allowed;                               // LB20
        int ppc = _prevPrevUnit & ClassMask;
        if ((pc == HY || pc == HH) && (nc == AL || nc == HL)
            && In(ppc, Set(Sot, BK, CR) | Set(LF, NL, SP) | Set(ZW, CB, GL)))
            return NoBreak;                                                     // LB20a
        if (In(nc, Set(BA, HH, HY) | Set(NS))) return NoBreak;                  // LB21
        if (pc == BB) return NoBreak;                                           // LB21
        if (ppc == HL && (pc == HY || pc == HH) && nc != HL) return NoBreak;    // LB21a
        if (pc == SY && nc == HL) return NoBreak;                               // LB21b
        if (nc == IN) return NoBreak;                                           // LB22
        if ((pc == AL || pc == HL) && nc == NU) return NoBreak;                 // LB23
        if (pc == NU && (nc == AL || nc == HL)) return NoBreak;                 // LB23
        if (pc == PR && In(nc, Set(ID, EB, EM))) return NoBreak;                // LB23a
        if (In(pc, Set(ID, EB, EM)) && nc == PO) return NoBreak;                // LB23a
        if ((pc == PR || pc == PO) && (nc == AL || nc == HL)) return NoBreak;   // LB24
        if ((pc == AL || pc == HL) && (nc == PR || nc == PO)) return NoBreak;   // LB24

        // LB25
        if ((nc == PO || nc == PR) && _num != NumNone) return NoBreak;          // NU (SY|IS)* (CL|CP)? × (PO|PR)
        if (pc == PO || pc == PR)
        {
            if (nc == NU) return NoBreak;                                       // (PO|PR) × NU
            if (nc == OP)
            {
                int nnPos = SkipUnit(text, after, nc);
                int nn = PeekUnitAt(text, nnPos) & ClassMask;
                if (nn == NU) return NoBreak;                                   // (PO|PR) × OP NU
                if (nn == IS)
                {
                    int nnnPos = SkipUnit(text, NextIndex(text, nnPos), IS);
                    if ((PeekUnitAt(text, nnnPos) & ClassMask) == NU) return NoBreak;   // (PO|PR) × OP IS NU
                }
            }
        }
        if ((pc == HY || pc == IS) && nc == NU) return NoBreak;                 // HY × NU, IS × NU
        if (nc == NU && _num == NumIn) return NoBreak;                          // NU (SY|IS)* × NU

        if (pc == JL && In(nc, Set(JL, JV, H2) | Set(H3))) return NoBreak;      // LB26
        if ((pc == JV || pc == H2) && (nc == JV || nc == JT)) return NoBreak;   // LB26
        if ((pc == JT || pc == H3) && nc == JT) return NoBreak;                 // LB26
        if (In(pc, Set(JL, JV, JT) | Set(H2, H3)) && nc == PO) return NoBreak;  // LB27
        if (pc == PR && In(nc, Set(JL, JV, JT) | Set(H2, H3))) return NoBreak;  // LB27
        if ((pc == AL || pc == HL) && (nc == AL || nc == HL)) return NoBreak;   // LB28

        // LB28a
        bool prevAksara = pc == AK || pc == AS || (P & FlagDottedCircle) != 0;
        bool nextAksara = nc == AK || nc == AS || (N & FlagDottedCircle) != 0;
        if (pc == AP && nextAksara) return NoBreak;                             // AP × (AK | ◌ | AS)
        if (prevAksara && (nc == VF || nc == VI)) return NoBreak;               // (AK | ◌ | AS) × (VF | VI)
        if (pc == VI && (ppc == AK || ppc == AS || (_prevPrevUnit & FlagDottedCircle) != 0)
            && (nc == AK || (N & FlagDottedCircle) != 0)) return NoBreak;       // (AK | ◌ | AS) VI × (AK | ◌)
        if (prevAksara && nextAksara && (PeekUnit(text, after, nc) & ClassMask) == VF) return NoBreak;   // (AK | ◌ | AS) × (AK | ◌ | AS) VF

        if (pc == IS && (nc == AL || nc == HL)) return NoBreak;                 // LB29
        if (In(pc, Set(AL, HL, NU)) && nc == OP && (N & FlagEastAsian) == 0) return NoBreak;   // LB30
        if (pc == CP && (P & FlagEastAsian) == 0 && In(nc, Set(AL, HL, NU))) return NoBreak;  // LB30
        if (pc == RI && nc == RI && _riOdd) return NoBreak;                     // LB30a
        if (nc == EM && (pc == EB || (P & FlagExtPictCn) != 0)) return NoBreak; // LB30b
        return Allowed;                                                         // LB31
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int NextIndex(ReadOnlySpan<char> text, int p)
    {
        if (p >= text.Length) return p;
        CodePoint.Decode(text, p, out int len);
        return p + len;
    }

    /// <summary>跳过一个单元在基字符之后附着的 CM/ZWJ（LB9），返回下一单元的起点。</summary>
    static int SkipUnit(ReadOnlySpan<char> text, int p, int baseClass)
    {
        if (In(baseClass, NonAbsorbing)) return p;
        while (p < text.Length)
        {
            int cp = CodePoint.Decode(text, p, out int len);
            int cls = Classify(cp, NS) & ClassMask;
            if (cls != CM && cls != ZWJ) break;
            p += len;
        }
        return p;
    }

    /// <summary>读取从 <paramref name="p"/> 开始的单元（已应用 LB10）；文本末尾返回 Eot。</summary>
    static int PeekUnitAt(ReadOnlySpan<char> text, int p)
    {
        if (p >= text.Length) return Eot;
        int unit = Classify(CodePoint.Decode(text, p, out _), NS);
        int cls = unit & ClassMask;
        return cls == CM || cls == ZWJ ? AL : unit;
    }

    /// <summary>下一单元（基字符类别为 <paramref name="baseClass"/>，其后文本从 <paramref name="after"/> 开始）之后的单元。</summary>
    static int PeekUnit(ReadOnlySpan<char> text, int after, int baseClass) => PeekUnitAt(text, SkipUnit(text, after, baseClass));
}
