using System;
using System.Collections.Generic;
using System.Numerics;
using AwAUI.Graphics;

namespace AwAUI.Text.OpenType;

/// <summary>CFF 字形的三次曲线轮廓（设计单位，y 向上）。先完整解码再输出，损坏的字形不会向路径写入半截内容。</summary>
internal sealed class CffOutline
{
    internal enum Verb : byte { Move, Line, Cubic, Close }

    public readonly List<Verb> Verbs = new();
    public readonly List<Vector2> Points = new();

    public void Clear()
    {
        Verbs.Clear();
        Points.Clear();
    }

    public bool IsEmpty => Verbs.Count == 0;

    public void Emit(PathBuilder sink, in Matrix3x2 m)
    {
        int p = 0;
        foreach (var v in Verbs)
        {
            switch (v)
            {
                case Verb.Move:
                    sink.MoveTo(Map(Points[p++], m));
                    break;
                case Verb.Line:
                    sink.LineTo(Map(Points[p++], m));
                    break;
                case Verb.Cubic:
                    sink.CubicTo(Map(Points[p], m), Map(Points[p + 1], m), Map(Points[p + 2], m));
                    p += 3;
                    break;
                default:
                    sink.Close();
                    break;
            }
        }
    }

    static Point Map(Vector2 p, in Matrix3x2 m)
    {
        var v = Vector2.Transform(new Vector2(p.X, -p.Y), m);
        return new Point(v.X, v.Y);
    }

    /// <summary>精确边界（含三次曲线的极值点），设计单位、y 向上。空轮廓返回 false。</summary>
    public bool TryGetBounds(out float xMin, out float yMin, out float xMax, out float yMax)
    {
        xMin = yMin = float.PositiveInfinity;
        xMax = yMax = float.NegativeInfinity;
        if (Points.Count == 0) return false;
        int p = 0;
        var cur = Vector2.Zero;
        foreach (var v in Verbs)
        {
            switch (v)
            {
                case Verb.Move:
                case Verb.Line:
                    cur = Points[p++];
                    Add(cur, ref xMin, ref yMin, ref xMax, ref yMax);
                    break;
                case Verb.Cubic:
                {
                    var c1 = Points[p];
                    var c2 = Points[p + 1];
                    var e = Points[p + 2];
                    p += 3;
                    Add(e, ref xMin, ref yMin, ref xMax, ref yMax);
                    CubicExtrema(cur.X, c1.X, c2.X, e.X, ref xMin, ref xMax);
                    CubicExtrema(cur.Y, c1.Y, c2.Y, e.Y, ref yMin, ref yMax);
                    cur = e;
                    break;
                }
            }
        }
        return xMin <= xMax;
    }

    static void Add(Vector2 v, ref float xMin, ref float yMin, ref float xMax, ref float yMax)
    {
        xMin = Math.Min(xMin, v.X);
        yMin = Math.Min(yMin, v.Y);
        xMax = Math.Max(xMax, v.X);
        yMax = Math.Max(yMax, v.Y);
    }

    /// <summary>一维三次贝塞尔在 (0, 1) 内的极值。</summary>
    static void CubicExtrema(float p0, float p1, float p2, float p3, ref float min, ref float max)
    {
        // 控制点都在端点范围内时没有更远的极值
        float lo = Math.Min(p0, p3), hi = Math.Max(p0, p3);
        if (p1 >= lo && p1 <= hi && p2 >= lo && p2 <= hi) return;
        // B'(t)/3 = a t² + b t + c
        double a = -p0 + 3.0 * p1 - 3.0 * p2 + p3;
        double b = 2.0 * (p0 - 2.0 * p1 + p2);
        double c = p1 - (double)p0;
        Span<double> roots = stackalloc double[2];
        int n = 0;
        if (Math.Abs(a) < 1e-12)
        {
            if (Math.Abs(b) > 1e-12) roots[n++] = -c / b;
        }
        else
        {
            double d = b * b - 4 * a * c;
            if (d >= 0)
            {
                double sq = Math.Sqrt(d);
                roots[n++] = (-b + sq) / (2 * a);
                roots[n++] = (-b - sq) / (2 * a);
            }
        }
        for (int i = 0; i < n; i++)
        {
            double t = roots[i];
            if (!(t > 0 && t < 1)) continue;
            double mt = 1 - t;
            float v = (float)(mt * mt * mt * p0 + 3 * mt * mt * t * p1 + 3 * mt * t * t * p2 + t * t * t * p3);
            min = Math.Min(min, v);
            max = Math.Max(max, v);
        }
    }
}

/// <summary>
/// Type 2 字符串解释器（Adobe TN #5177；CFF2 的差异见 OpenType 'CFF2' 规范）。
/// 全部路径操作符；提示操作符只用于计数（hintmask / cntrmask 据此跳过掩码字节）；callsubr / callgsubr 带偏置；
/// endchar 的四参数形式按 seac 组合基字形与重音；CFF2 的 blend 取默认实例、vsindex 选择区域数；已废弃的算术与存储操作符照常执行。
/// 防御不可信输入：操作数栈 48（CFF）/ 513（CFF2），子程序嵌套 10 层，每个字形的操作数与操作符总数有上限，点数有上限，所有读取都检查边界。
/// </summary>
internal sealed class CffInterpreter
{
    /// <summary>子程序最大嵌套深度（Type 2 规范的上限）。</summary>
    public const int MaxSubrDepth = 10;
    /// <summary>每个字形（含展开的子程序与 seac 组件）执行的操作数与操作符总数上限，防止通过子程序反复调用造成的指数级展开。</summary>
    public const int MaxOperations = 1 << 17;
    /// <summary>每个字形的输出点数上限。</summary>
    public const int MaxPoints = 1 << 16;
    const int TransientSize = 32;

    readonly CffTable _cff;
    readonly float[] _stack;
    readonly int _maxStack;
    readonly float[] _transient = new float[TransientSize];
    CffOutline _out = null!;
    CffTable.FontDict _fd = null!;
    int _sp, _nStems, _budget, _vsIndex;
    bool _widthDone, _open, _done;
    float _x, _y;
    uint _random = 0x9E3779B9;

    public CffInterpreter(CffTable cff)
    {
        _cff = cff;
        _maxStack = cff.IsCff2 ? CffTable.MaxStackCff2 : CffTable.MaxStackCff;
        _stack = new float[_maxStack];
    }

    /// <summary>解码字形轮廓；数据损坏或超出限制时抛出 <see cref="InvalidFontException"/>。</summary>
    public void Decode(int glyph, CffOutline outline)
    {
        outline.Clear();
        _out = outline;
        _budget = MaxOperations;
        Run(glyph, 0, 0, allowSeac: true);
    }

    void Run(int glyph, float dx, float dy, bool allowSeac)
    {
        if ((uint)glyph >= (uint)_cff.GlyphCount) throw new InvalidFontException($"CFF glyph {glyph} is out of range.");
        var s = _cff.Span;
        _cff.CharStrings.Get(s, glyph, out int start, out int length);
        _fd = _cff.GetFontDict(glyph);
        _vsIndex = _fd.VsIndex;
        _sp = 0;
        _nStems = 0;
        _widthDone = _cff.IsCff2;
        _open = false;
        _done = false;
        _x = dx;
        _y = dy;
        _allowSeac = allowSeac;
        Execute(s, start, start + length, 0);
        if (!_done || _cff.IsCff2) ClosePath();   // CFF2 没有 endchar：字符串结束即字形结束
    }

    bool _allowSeac;

    void ClosePath()
    {
        if (_open)
        {
            _out.Verbs.Add(CffOutline.Verb.Close);
            _open = false;
        }
    }

    void Spend()
    {
        if (--_budget < 0) throw new InvalidFontException("CFF charstring exceeds the operation budget.");
    }

    void Push(float v)
    {
        if (_sp >= _maxStack) throw new InvalidFontException("CFF charstring argument stack overflow.");
        _stack[_sp++] = v;
    }

    void Need(int n)
    {
        if (_sp < n) throw new InvalidFontException("CFF charstring argument stack underflow.");
    }

    void CheckPoints(int add)
    {
        if (_out.Points.Count + add > MaxPoints) throw new InvalidFontException("CFF glyph has too many points.");
    }

    void MoveTo(float dx, float dy)
    {
        ClosePath();
        CheckPoints(1);
        _x += dx;
        _y += dy;
        _out.Verbs.Add(CffOutline.Verb.Move);
        _out.Points.Add(new Vector2(_x, _y));
        _open = true;
    }

    void EnsureOpen()
    {
        // 规范要求先有 moveto；宽松处理：从当前点开始一条子路径
        if (!_open)
        {
            CheckPoints(1);
            _out.Verbs.Add(CffOutline.Verb.Move);
            _out.Points.Add(new Vector2(_x, _y));
            _open = true;
        }
    }

    void LineTo(float dx, float dy)
    {
        EnsureOpen();
        CheckPoints(1);
        _x += dx;
        _y += dy;
        _out.Verbs.Add(CffOutline.Verb.Line);
        _out.Points.Add(new Vector2(_x, _y));
    }

    void CurveTo(float dx1, float dy1, float dx2, float dy2, float dx3, float dy3)
    {
        EnsureOpen();
        CheckPoints(3);
        float x1 = _x + dx1, y1 = _y + dy1;
        float x2 = x1 + dx2, y2 = y1 + dy2;
        _x = x2 + dx3;
        _y = y2 + dy3;
        _out.Verbs.Add(CffOutline.Verb.Cubic);
        _out.Points.Add(new Vector2(x1, y1));
        _out.Points.Add(new Vector2(x2, y2));
        _out.Points.Add(new Vector2(_x, _y));
    }

    /// <summary>第一个清栈操作符前多出的一个操作数是字形宽度（CFF2 没有宽度）。</summary>
    int TakeWidth(bool hasExtra)
    {
        if (_widthDone) return 0;
        _widthDone = true;
        return hasExtra ? 1 : 0;
    }

    void Stems()
    {
        int first = TakeWidth((_sp & 1) != 0);
        _nStems += (_sp - first) / 2;
        _sp = 0;
    }

    void Execute(ReadOnlySpan<byte> s, int p, int end, int depth)
    {
        bool cff2 = _cff.IsCff2;
        while (p < end)
        {
            Spend();
            int b0 = s[p];
            // ---- 操作数
            if (b0 >= 32 || b0 == 28)
            {
                if (b0 == 28)
                {
                    if (p + 2 >= end) throw new InvalidFontException("CFF charstring operand is truncated.");
                    Push((short)((s[p + 1] << 8) | s[p + 2]));
                    p += 3;
                }
                else if (b0 <= 246)
                {
                    Push(b0 - 139);
                    p++;
                }
                else if (b0 <= 250)
                {
                    if (p + 1 >= end) throw new InvalidFontException("CFF charstring operand is truncated.");
                    Push((b0 - 247) * 256 + s[p + 1] + 108);
                    p += 2;
                }
                else if (b0 <= 254)
                {
                    if (p + 1 >= end) throw new InvalidFontException("CFF charstring operand is truncated.");
                    Push(-(b0 - 251) * 256 - s[p + 1] - 108);
                    p += 2;
                }
                else
                {
                    // 255：16.16 定点数
                    if (p + 4 >= end) throw new InvalidFontException("CFF charstring operand is truncated.");
                    int v = (s[p + 1] << 24) | (s[p + 2] << 16) | (s[p + 3] << 8) | s[p + 4];
                    Push(v / 65536f);
                    p += 5;
                }
                continue;
            }

            // ---- 操作符
            p++;
            switch (b0)
            {
                case 1:   // hstem
                case 3:   // vstem
                case 18:  // hstemhm
                case 23:  // vstemhm
                    Stems();
                    break;

                case 19:  // hintmask
                case 20:  // cntrmask
                {
                    // 栈上的操作数是隐含的 vstemhm
                    int first = TakeWidth((_sp & 1) != 0);
                    _nStems += (_sp - first) / 2;
                    _sp = 0;
                    int bytes = (_nStems + 7) >> 3;
                    if (p + bytes > end) throw new InvalidFontException("CFF hintmask is truncated.");
                    p += bytes;
                    break;
                }

                case 21:  // rmoveto
                {
                    int first = TakeWidth(_sp > 2);
                    Need(first + 2);
                    MoveTo(_stack[first], _stack[first + 1]);
                    _sp = 0;
                    break;
                }
                case 22:  // hmoveto
                {
                    int first = TakeWidth(_sp > 1);
                    Need(first + 1);
                    MoveTo(_stack[first], 0);
                    _sp = 0;
                    break;
                }
                case 4:   // vmoveto
                {
                    int first = TakeWidth(_sp > 1);
                    Need(first + 1);
                    MoveTo(0, _stack[first]);
                    _sp = 0;
                    break;
                }

                case 5:   // rlineto {dxa dya}+
                    for (int i = 0; i + 1 < _sp; i += 2) LineTo(_stack[i], _stack[i + 1]);
                    _sp = 0;
                    break;
                case 6:   // hlineto
                case 7:   // vlineto
                {
                    bool horizontal = b0 == 6;
                    for (int i = 0; i < _sp; i++)
                    {
                        if (horizontal) LineTo(_stack[i], 0);
                        else LineTo(0, _stack[i]);
                        horizontal = !horizontal;
                    }
                    _sp = 0;
                    break;
                }

                case 8:   // rrcurveto {dxa dya dxb dyb dxc dyc}+
                    for (int i = 0; i + 5 < _sp; i += 6)
                        CurveTo(_stack[i], _stack[i + 1], _stack[i + 2], _stack[i + 3], _stack[i + 4], _stack[i + 5]);
                    _sp = 0;
                    break;

                case 24:  // rcurveline {dxa dya dxb dyb dxc dyc}+ dxd dyd
                {
                    if (_sp < 8) throw new InvalidFontException("CFF rcurveline has too few arguments.");
                    int i = 0;
                    for (; i + 6 + 2 <= _sp; i += 6)
                        CurveTo(_stack[i], _stack[i + 1], _stack[i + 2], _stack[i + 3], _stack[i + 4], _stack[i + 5]);
                    LineTo(_stack[i], _stack[i + 1]);
                    _sp = 0;
                    break;
                }
                case 25:  // rlinecurve {dxa dya}+ dxb dyb dxc dyc dxd dyd
                {
                    if (_sp < 8) throw new InvalidFontException("CFF rlinecurve has too few arguments.");
                    int i = 0;
                    for (; i + 2 + 6 <= _sp; i += 2) LineTo(_stack[i], _stack[i + 1]);
                    CurveTo(_stack[i], _stack[i + 1], _stack[i + 2], _stack[i + 3], _stack[i + 4], _stack[i + 5]);
                    _sp = 0;
                    break;
                }

                case 26:  // vvcurveto dx1? {dya dxb dyb dyc}+
                {
                    int i = 0;
                    float dx1 = 0;
                    if ((_sp & 1) != 0) dx1 = _stack[i++];
                    for (; i + 3 < _sp; i += 4)
                    {
                        CurveTo(dx1, _stack[i], _stack[i + 1], _stack[i + 2], 0, _stack[i + 3]);
                        dx1 = 0;
                    }
                    _sp = 0;
                    break;
                }
                case 27:  // hhcurveto dy1? {dxa dxb dyb dxc}+
                {
                    int i = 0;
                    float dy1 = 0;
                    if ((_sp & 1) != 0) dy1 = _stack[i++];
                    for (; i + 3 < _sp; i += 4)
                    {
                        CurveTo(_stack[i], dy1, _stack[i + 1], _stack[i + 2], _stack[i + 3], 0);
                        dy1 = 0;
                    }
                    _sp = 0;
                    break;
                }
                case 30:  // vhcurveto
                case 31:  // hvcurveto
                {
                    bool horizontal = b0 == 31;
                    int i = 0;
                    while (_sp - i >= 4)
                    {
                        bool last = _sp - i == 5;
                        float extra = last ? _stack[i + 4] : 0;
                        if (horizontal) CurveTo(_stack[i], 0, _stack[i + 1], _stack[i + 2], extra, _stack[i + 3]);
                        else CurveTo(0, _stack[i], _stack[i + 1], _stack[i + 2], _stack[i + 3], extra);
                        i += last ? 5 : 4;
                        horizontal = !horizontal;
                    }
                    _sp = 0;
                    break;
                }

                case 10:  // callsubr
                case 29:  // callgsubr
                {
                    Need(1);
                    CffTable.Index subrs;
                    if (b0 == 10)
                    {
                        if (!_fd.HasLocalSubrs) throw new InvalidFontException("CFF callsubr without local subroutines.");
                        subrs = _fd.LocalSubrs;
                    }
                    else subrs = _cff.GlobalSubrs;
                    int index = (int)_stack[--_sp] + Bias(subrs.Count);
                    if (depth + 1 > MaxSubrDepth) throw new InvalidFontException("CFF subroutines are nested too deeply.");
                    subrs.Get(s, index, out int ss, out int sl);
                    Execute(s, ss, ss + sl, depth + 1);
                    if (_done) return;
                    break;
                }
                case 11:  // return
                    if (cff2) throw new InvalidFontException("CFF2 charstrings have no return operator.");
                    if (depth == 0) throw new InvalidFontException("CFF return outside a subroutine.");
                    return;

                case 14:  // endchar
                {
                    if (cff2) throw new InvalidFontException("CFF2 charstrings have no endchar operator.");
                    int first = TakeWidth(_sp == 1 || _sp == 5);
                    if (_sp - first >= 4)
                    {
                        // seac：adx ady bchar achar
                        float adx = _stack[first], ady = _stack[first + 1];
                        int bchar = (int)_stack[first + 2], achar = (int)_stack[first + 3];
                        ClosePath();
                        Seac(adx, ady, bchar, achar);
                    }
                    else ClosePath();
                    _sp = 0;
                    _done = true;
                    return;
                }

                case 15:  // vsindex（CFF2）
                    if (!cff2) throw new InvalidFontException("vsindex is only valid in CFF2.");
                    Need(1);
                    _vsIndex = (int)_stack[--_sp];
                    _sp = 0;
                    break;
                case 16:  // blend（CFF2）：默认实例保留 n 个默认值，丢弃 n × k 个增量
                {
                    if (!cff2) throw new InvalidFontException("blend is only valid in CFF2.");
                    Need(1);
                    int n = (int)_stack[--_sp];
                    int k = _cff.GetRegionCount(_vsIndex);
                    long consumed = (long)n * (k + 1);
                    if (n < 0 || consumed > _sp) throw new InvalidFontException("CFF2 blend operand count is invalid.");
                    _sp = _sp - (int)consumed + n;
                    break;
                }

                case 12:
                {
                    if (p >= end) throw new InvalidFontException("CFF escape operator is truncated.");
                    int b1 = s[p++];
                    Escape(b1);
                    break;
                }

                default:
                    throw new InvalidFontException($"Invalid CFF charstring operator {b0}.");
            }
        }
        // 子程序在数据结束处隐式返回（CFF2；CFF 宽松处理）
    }

    static int Bias(int count) => count < 1240 ? 107 : count < 33900 ? 1131 : 32768;

    void Seac(float adx, float ady, int bchar, int achar)
    {
        if (!_allowSeac) throw new InvalidFontException("CFF seac components cannot use seac.");
        int baseGlyph = _cff.GlyphForStandardCode(bchar);
        int accentGlyph = _cff.GlyphForStandardCode(achar);
        if (baseGlyph < 0 || accentGlyph < 0) throw new InvalidFontException("CFF seac refers to a missing glyph.");
        var fd = _fd;
        Run(baseGlyph, 0, 0, allowSeac: false);
        Run(accentGlyph, adx, ady, allowSeac: false);
        _fd = fd;
    }

    void Escape(int op)
    {
        bool cff2 = _cff.IsCff2;
        switch (op)
        {
            case 35:  // flex dx1 dy1 … dx6 dy6 fd
            {
                Need(13);
                var a = _stack;
                CurveTo(a[0], a[1], a[2], a[3], a[4], a[5]);
                CurveTo(a[6], a[7], a[8], a[9], a[10], a[11]);
                _sp = 0;
                return;
            }
            case 34:  // hflex dx1 dx2 dy2 dx3 dx4 dx5 dx6
            {
                Need(7);
                var a = _stack;
                CurveTo(a[0], 0, a[1], a[2], a[3], 0);
                CurveTo(a[4], 0, a[5], -a[2], a[6], 0);
                _sp = 0;
                return;
            }
            case 36:  // hflex1 dx1 dy1 dx2 dy2 dx3 dx4 dx5 dy5 dx6
            {
                Need(9);
                var a = _stack;
                CurveTo(a[0], a[1], a[2], a[3], a[4], 0);
                CurveTo(a[5], 0, a[6], a[7], a[8], -(a[1] + a[3] + a[7]));
                _sp = 0;
                return;
            }
            case 37:  // flex1 dx1 dy1 … dx5 dy5 d6
            {
                Need(11);
                var a = _stack;
                float dx = a[0] + a[2] + a[4] + a[6] + a[8];
                float dy = a[1] + a[3] + a[5] + a[7] + a[9];
                CurveTo(a[0], a[1], a[2], a[3], a[4], a[5]);
                if (Math.Abs(dx) > Math.Abs(dy)) CurveTo(a[6], a[7], a[8], a[9], a[10], -dy);
                else CurveTo(a[6], a[7], a[8], a[9], -dx, a[10]);
                _sp = 0;
                return;
            }
        }
        if (cff2) throw new InvalidFontException($"Invalid CFF2 escape operator {op}.");
        // 已废弃的算术 / 存储 / 条件操作符（Type 2 规范 4.4–4.6），FreeType 与 fontTools 同样支持
        switch (op)
        {
            case 3:   // and
                Need(2);
                _sp--;
                _stack[_sp - 1] = _stack[_sp - 1] != 0 && _stack[_sp] != 0 ? 1 : 0;
                break;
            case 4:   // or
                Need(2);
                _sp--;
                _stack[_sp - 1] = _stack[_sp - 1] != 0 || _stack[_sp] != 0 ? 1 : 0;
                break;
            case 5:   // not
                Need(1);
                _stack[_sp - 1] = _stack[_sp - 1] == 0 ? 1 : 0;
                break;
            case 9:   // abs
                Need(1);
                _stack[_sp - 1] = Math.Abs(_stack[_sp - 1]);
                break;
            case 10:  // add
                Need(2);
                _sp--;
                _stack[_sp - 1] = Finite(_stack[_sp - 1] + _stack[_sp]);
                break;
            case 11:  // sub
                Need(2);
                _sp--;
                _stack[_sp - 1] = Finite(_stack[_sp - 1] - _stack[_sp]);
                break;
            case 12:  // div
                Need(2);
                _sp--;
                _stack[_sp - 1] = _stack[_sp] == 0 ? 0 : Finite(_stack[_sp - 1] / _stack[_sp]);
                break;
            case 14:  // neg
                Need(1);
                _stack[_sp - 1] = -_stack[_sp - 1];
                break;
            case 15:  // eq
                Need(2);
                _sp--;
                _stack[_sp - 1] = _stack[_sp - 1] == _stack[_sp] ? 1 : 0;
                break;
            case 18:  // drop
                Need(1);
                _sp--;
                break;
            case 20:  // put val i
            {
                Need(2);
                int i = (int)_stack[_sp - 1];
                float v = _stack[_sp - 2];
                _sp -= 2;
                if ((uint)i >= TransientSize) throw new InvalidFontException("CFF put index is out of range.");
                _transient[i] = v;
                break;
            }
            case 21:  // get i
            {
                Need(1);
                int i = (int)_stack[_sp - 1];
                if ((uint)i >= TransientSize) throw new InvalidFontException("CFF get index is out of range.");
                _stack[_sp - 1] = _transient[i];
                break;
            }
            case 22:  // ifelse s1 s2 v1 v2
            {
                Need(4);
                float s1 = _stack[_sp - 4], s2 = _stack[_sp - 3], v1 = _stack[_sp - 2], v2 = _stack[_sp - 1];
                _sp -= 3;
                _stack[_sp - 1] = v1 <= v2 ? s1 : s2;
                break;
            }
            case 23:  // random：确定性的伪随机数 (0, 1]
                _random = _random * 1664525 + 1013904223;
                Push(((_random >> 8) + 1) / 16777216f);
                break;
            case 24:  // mul
                Need(2);
                _sp--;
                _stack[_sp - 1] = Finite(_stack[_sp - 1] * _stack[_sp]);
                break;
            case 26:  // sqrt
                Need(1);
                _stack[_sp - 1] = _stack[_sp - 1] > 0 ? (float)Math.Sqrt(_stack[_sp - 1]) : 0;
                break;
            case 27:  // dup
                Need(1);
                Push(_stack[_sp - 1]);
                break;
            case 28:  // exch
            {
                Need(2);
                (_stack[_sp - 1], _stack[_sp - 2]) = (_stack[_sp - 2], _stack[_sp - 1]);
                break;
            }
            case 29:  // index
            {
                Need(1);
                int i = (int)_stack[_sp - 1];
                if (i < 0) i = 0;
                if (i >= _sp - 1) throw new InvalidFontException("CFF index operand is out of range.");
                _stack[_sp - 1] = _stack[_sp - 2 - i];
                break;
            }
            case 30:  // roll n j
            {
                Need(2);
                int n = (int)_stack[_sp - 2];
                int j = (int)_stack[_sp - 1];
                _sp -= 2;
                if (n < 0 || n > _sp) throw new InvalidFontException("CFF roll operand is out of range.");
                if (n > 1)
                {
                    j %= n;
                    if (j < 0) j += n;
                    // 用三次反转实现循环移位，不分配
                    Array.Reverse(_stack, _sp - n, n);
                    Array.Reverse(_stack, _sp - n, j);
                    Array.Reverse(_stack, _sp - n + j, n - j);
                }
                break;
            }
            default:
                throw new InvalidFontException($"Invalid CFF escape operator {op}.");
        }
    }

    static float Finite(float v) => float.IsNaN(v) || float.IsInfinity(v) ? 0 : v;
}
