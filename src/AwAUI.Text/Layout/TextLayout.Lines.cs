using System;
using AwAUI.Graphics;
using AwAUI.Graphics.Text;
using AwAUI.Unicode;

namespace AwAUI.Text;

public sealed partial class TextLayout
{
    struct LineInfo
    {
        public int UnitStart, UnitEnd, ContentEnd, HangStart, LastContentUnit;
        public int StartOffset, EndOffset, EndNoBreak;
        public float Width, HangWidth, X, Top, Height, Baseline, Above, Below, JustifyExtra;
        public int FragStart, FragCount;
        public byte ParaLevel;
        public bool HardBreak, LastInParagraph;
        public ShapedRun? Ellipsis;
        public int EllipsisItem;
    }

    struct Frag
    {
        public int Item;            // -1：省略号
        public int UnitStart, UnitEnd;
        public byte Level;
        public bool Hang;
        public float X, Width;
    }

    readonly ValueList<LineInfo> _lines = new();
    readonly ValueList<Frag> _frags = new();
    readonly ValueList<(int Start, int End)> _ranges = new();
    float _maxWidth = float.NaN;
    bool _laidOut;
    bool _exceeded;
    float _width, _height;
    Rect[]? _placeholderRects;
    PaintData? _paintData;

    /// <summary>
    /// 按可用宽度 <paramref name="maxWidth"/> 断行、重排与对齐。可用不同宽度反复调用：整形结果复用，只重新断行（相同宽度直接返回）。
    /// 无穷大表示不限宽度（只在强制换行处断开）；NaN 与大于 <see cref="MaxFiniteWidth"/> 的值视同无穷大，负数视同 0。
    /// </summary>
    public void Layout(float maxWidth)
    {
        // 接近 float.MaxValue 的宽度（有的布局引擎用它表示「不限」）会让居中、右对齐与两端对齐的坐标溢出为无穷大
        if (float.IsNaN(maxWidth) || maxWidth > MaxFiniteWidth) maxWidth = float.PositiveInfinity;
        else if (maxWidth < 0) maxWidth = 0;
        if (_laidOut && maxWidth.Equals(_maxWidth)) return;
        _maxWidth = maxWidth;
        _laidOut = true;
        _lines.Clear();
        _frags.Clear();
        _ranges.Clear();
        _paintData = null;

        bool wrap = _style.Wraps && !float.IsPositiveInfinity(maxWidth);
        bool emergency = _style.OverflowWrap != OverflowWrap.Normal;
        int maxLines = _style.MaxLines > 0 ? _style.MaxLines : int.MaxValue;

        int u = 0, seg = 0;
        while (u < _units.Length && _ranges.Count < maxLines)
        {
            int end = FindLineEnd(u, ref seg, maxWidth, wrap, emergency);
            _ranges.Add((u, end));
            u = end;
        }
        _exceeded = u < _units.Length;
        // 文本为空或以强制换行结束：末尾还有一个空行（插入点可以停在那里）
        if (!_exceeded && _ranges.Count < maxLines && (_units.Length == 0 || (_units[_units.Length - 1].Flags & UnitFlags.HardBreak) != 0))
            _ranges.Add((_units.Length, _units.Length));

        bool hasEllipsis = !string.IsNullOrEmpty(_style.Ellipsis);
        for (int i = 0; i < _ranges.Count; i++)
        {
            var (s, e) = _ranges[i];
            bool last = e == s || (_units[e - 1].Flags & UnitFlags.HardBreak) != 0 || e == _units.Length;
            FinishLine(s, e, last, hasEllipsis && _exceeded && i == _ranges.Count - 1, maxWidth);
        }
        PositionLines(maxWidth);
    }

    void EnsureLayout()
    {
        if (!_laidOut) Layout(float.PositiveInfinity);
    }

    /// <summary><see cref="Layout"/> 接受的最大有限宽度（逻辑像素）；更大的值按不限宽度处理。</summary>
    public const float MaxFiniteWidth = 1e9f;

    /// <summary>最近一次 <see cref="Layout"/> 的可用宽度（尚未布局时为 NaN）。</summary>
    public float MaxWidth => _maxWidth;

    /// <summary>实际占用宽度：最宽一行的内容宽度（不含悬挂的行尾空白）。</summary>
    public float Width
    {
        get
        {
            EnsureLayout();
            return _width;
        }
    }

    /// <summary>全部行的总高度。</summary>
    public float Height
    {
        get
        {
            EnsureLayout();
            return _height;
        }
    }

    public int LineCount
    {
        get
        {
            EnsureLayout();
            return _lines.Count;
        }
    }

    /// <summary>是否因 <see cref="ParagraphStyle.MaxLines"/> 截断了内容。</summary>
    public bool DidExceedMaxLines
    {
        get
        {
            EnsureLayout();
            return _exceeded;
        }
    }

    /// <summary>第一行基线的 y 坐标。</summary>
    public float FirstBaseline
    {
        get
        {
            EnsureLayout();
            return _lines[0].Baseline;
        }
    }

    /// <summary>最后一行基线的 y 坐标。</summary>
    public float LastBaseline
    {
        get
        {
            EnsureLayout();
            return _lines[_lines.Count - 1].Baseline;
        }
    }

    public LineMetrics GetLine(int index)
    {
        EnsureLayout();
        if ((uint)index >= (uint)_lines.Count) throw new ArgumentOutOfRangeException(nameof(index));
        ref readonly var l = ref _lines[index];
        return new LineMetrics(index, l.StartOffset, l.EndOffset, l.EndNoBreak, l.HardBreak, ContentLeft(l), l.Width,
            l.Top, l.Height, l.Baseline, l.Above, l.Below);
    }

    static float ContentLeft(in LineInfo l) => l.X + ((l.ParaLevel & 1) != 0 ? l.HangWidth : 0);

    /// <summary>
    /// 第 <paramref name="index"/> 个占位盒的矩形。被最大行数或省略号截掉的占位盒返回空矩形。
    /// </summary>
    public Rect GetPlaceholderRect(int index)
    {
        EnsureLayout();
        if ((uint)index >= (uint)_placeholders.Length) throw new ArgumentOutOfRangeException(nameof(index));
        return _placeholderRects![index];
    }

    // ------------------------------------------------------------------ 断行

    /// <summary>从单元 <paramref name="start"/> 开始的一行在哪个单元结束（贪心：尽量多放）。</summary>
    int FindLineEnd(int start, ref int seg, float maxWidth, bool wrap, bool emergency)
    {
        while (seg < _segments.Length && _segments[seg].UnitEnd <= start) seg++;
        float w = 0;
        int committed = start;
        bool content = false;
        for (; seg < _segments.Length; seg++)
        {
            ref readonly var sg = ref _segments[seg];
            int from = Math.Max(sg.UnitStart, start);
            float sw, hang;
            if (from == sg.UnitStart)
            {
                sw = sg.Width;
                hang = sg.Hang;
            }
            else
            {
                // 从段中间开始（上一行在此紧急断开）：用前缀和避免每行重新累加整段（长词逐行断开时会变成平方复杂度）
                sw = PrefixSum(from, sg.UnitEnd);
                hang = PrefixSum(Math.Max(from, sg.HangStart), sg.UnitEnd);
            }
            if (wrap && w + sw - hang > maxWidth + Epsilon)
            {
                // 放不下：在上一个断点处断开；本行还没有可见内容时，按 overflow-wrap 在字素簇之间紧急断开，否则溢出
                if (content) return committed;
                if (emergency)
                {
                    int limit = Math.Max(from, sg.HangStart);
                    float acc = w;
                    bool any = false;
                    int k = from;
                    while (k < limit)
                    {
                        float a = _units[k].Advance;
                        if (any && acc + a > maxWidth + Epsilon) break;
                        acc += a;
                        if ((_units[k].Flags & UnitFlags.Removed) == 0) any = true;
                        k++;
                    }
                    if (k < limit) return k;
                }
            }
            w += sw;
            committed = sg.UnitEnd;
            if (!content)
                for (int k = from; k < sg.UnitEnd && !content; k++) content = (_units[k].Flags & UnitFlags.Removed) == 0;
            if (sg.Mandatory)
            {
                seg++;
                return committed;
            }
        }
        return committed;
    }

    void FinishLine(int start, int end, bool lastInParagraph, bool forceEllipsis, float maxWidth)
    {
        var line = new LineInfo
        {
            UnitStart = start,
            UnitEnd = end,
            StartOffset = start < _units.Length ? _units[start].Start : _text.Length,
            EllipsisItem = -1,
            LastInParagraph = lastInParagraph,
        };
        line.EndOffset = end > start ? _units[end - 1].End : line.StartOffset;
        line.HardBreak = end > start && (_units[end - 1].Flags & UnitFlags.HardBreak) != 0;
        line.EndNoBreak = line.HardBreak ? _units[end - 1].Start : line.EndOffset;
        line.ParaLevel = ParagraphLevelAt(line.StartOffset);

        int hangStart = end;
        while (hangStart > start && (_units[hangStart - 1].Flags & UnitFlags.Hang) != 0) hangStart--;
        float width = SumAdvance(start, hangStart);
        float hangWidth = SumAdvance(hangStart, end);
        int contentEnd = end;

        // 省略号：超出最大行数时的最后一行，或宽于可用宽度的行
        float ellipsisWidth = 0;
        if (!string.IsNullOrEmpty(_style.Ellipsis) && (forceEllipsis || width > maxWidth + Epsilon))
        {
            int item = hangStart > start ? _pieces[_units[hangStart - 1].Piece].Item : start < _units.Length ? _pieces[_units[start].Piece].Item : -1;
            if (item >= 0 && _items[item].Placeholder >= 0) item = -1;
            var run = ShapeEllipsis(item, line.ParaLevel);
            ellipsisWidth = run.Width;
            float avail = maxWidth - ellipsisWidth;
            int k = hangStart;
            float w = width;
            while (k > start && w > avail + Epsilon) w -= _units[--k].Advance;
            while (k > start && (_units[k - 1].Flags & UnitFlags.Hang) != 0) k--;
            contentEnd = hangStart = k;
            width = SumAdvance(start, k) + ellipsisWidth;
            hangWidth = 0;
            line.Ellipsis = run;
            line.EllipsisItem = item;
        }
        line.ContentEnd = contentEnd;
        line.HangStart = hangStart;
        line.LastContentUnit = hangStart - 1;

        // 片段：同一 run、同一层级的连续单元；行尾悬挂的空白按 L1 取段落层级
        line.FragStart = _frags.Count;
        for (int u = start; u < contentEnd;)
        {
            int item = _pieces[_units[u].Piece].Item;
            bool hang = u >= hangStart;
            int v = u + 1;
            while (v < contentEnd && _pieces[_units[v].Piece].Item == item && (v >= hangStart) == hang) v++;
            _frags.Add(new Frag
            {
                Item = item,
                UnitStart = u,
                UnitEnd = v,
                Level = hang ? line.ParaLevel : _items[item].Level,
                Hang = hang,
                Width = SumAdvance(u, v),
            });
            u = v;
        }
        if (line.Ellipsis != null)
            _frags.Add(new Frag { Item = -1, UnitStart = contentEnd, UnitEnd = contentEnd, Level = line.ParaLevel, Width = ellipsisWidth });
        line.FragCount = _frags.Count - line.FragStart;

        // 两端对齐：把剩余空间平均分配到扩展点（词分隔符；东亚宽字符之间）
        if (_style.TextAlign == TextAlign.Justify && !lastInParagraph && line.Ellipsis == null && !float.IsInfinity(maxWidth) && width < maxWidth)
        {
            int count = 0;
            for (int u = start; u < hangStart; u++)
                if (IsJustificationOpportunity(u, line.LastContentUnit)) count++;
            if (count > 0)
            {
                line.JustifyExtra = (maxWidth - width) / count;
                for (int f = line.FragStart; f < line.FragStart + line.FragCount; f++)
                {
                    ref var fr = ref _frags[f];
                    if (fr.Hang) continue;
                    int opp = 0;
                    for (int u = fr.UnitStart; u < fr.UnitEnd; u++)
                        if (IsJustificationOpportunity(u, line.LastContentUnit)) opp++;
                    fr.Width += opp * line.JustifyExtra;
                }
                width = maxWidth;
            }
        }
        line.Width = width;
        line.HangWidth = hangWidth;

        ReorderFragments(line.FragStart, line.FragCount);

        // 竖直度量：支柱 + 各 run 的行内盒 + 占位盒
        float above = _strutAbove, below = _strutBelow;
        float topAligned = 0, bottomAligned = 0;
        for (int f = line.FragStart; f < line.FragStart + line.FragCount; f++)
        {
            int item = _frags[f].Item;
            if (item < 0)
            {
                item = line.EllipsisItem;
                if (item < 0) continue;
            }
            ref readonly var it = ref _items[item];
            if (it.Placeholder < 0)
            {
                above = Math.Max(above, it.Above);
                below = Math.Max(below, it.Below);
                continue;
            }
            var p = _placeholders[it.Placeholder].Style;
            switch (p.Alignment)
            {
                case PlaceholderAlignment.Top:
                    topAligned = Math.Max(topAligned, p.Height);
                    break;
                case PlaceholderAlignment.Bottom:
                    bottomAligned = Math.Max(bottomAligned, p.Height);
                    break;
                case PlaceholderAlignment.Middle:
                    above = Math.Max(above, p.Height / 2 + _strutXHeight / 2);
                    below = Math.Max(below, p.Height / 2 - _strutXHeight / 2);
                    break;
                default:
                    above = Math.Max(above, p.Baseline);
                    below = Math.Max(below, p.Height - p.Baseline);
                    break;
            }
        }
        if (topAligned > above + below) below = topAligned - above;
        if (bottomAligned > above + below) above = bottomAligned - below;
        line.Above = above;
        line.Below = below;
        line.Height = above + below;
        _lines.Add(line);
    }

    bool IsJustificationOpportunity(int u, int lastContentUnit)
    {
        var f = _units[u].Flags;
        return (f & UnitFlags.Space) != 0 || ((f & UnitFlags.Cjk) != 0 && u != lastContentUnit);
    }

    /// <summary>UAX #9 L2：按片段的层级排成视觉顺序（原地）。</summary>
    void ReorderFragments(int start, int count)
    {
        bool mixed = false;
        for (int f = start; f < start + count && !mixed; f++) mixed = _frags[f].Level != 0;
        if (!mixed || count == 0) return;
        Span<byte> levels = count <= 256 ? stackalloc byte[count] : new byte[count];
        Span<int> order = count <= 256 ? stackalloc int[count] : new int[count];
        for (int f = 0; f < count; f++) levels[f] = _frags[start + f].Level;
        BidiAlgorithm.ReorderVisual(levels, order);
        var copy = _frags.AsSpan(start, count).ToArray();
        for (int f = 0; f < count; f++) _frags[start + f] = copy[order[f]];
    }

    ShapedRun ShapeEllipsis(int item, byte paraLevel)
    {
        var info = item >= 0 ? _styles[_items[item].Style] : _strutStyle;
        var face = item >= 0 ? _items[item].Face! : info.Primary;
        string text = _style.Ellipsis!;
        int first = -1;
        var chars = text.ToCharArray();
        if (!Covers(face, chars, 0, chars.Length, ref first))
        {
            var fb = first >= 0 ? _engine.Fonts.Fallback(first, info.Query, info.Style.Locale) : null;
            int f2 = -1;
            if (fb != null && Covers(fb, chars, 0, chars.Length, ref f2)) face = fb;
            else if (text == ParagraphStyle.HorizontalEllipsis) text = "...";
        }
        return _engine.ShapeWord(text.AsMemory(), face, info.Size, (paraLevel & 1) != 0, Script.Common, info.Style.Locale,
            info.Style.Variation, info.Features);
    }

    void PositionLines(float maxWidth)
    {
        float widest = 0;
        for (int i = 0; i < _lines.Count; i++) widest = Math.Max(widest, _lines[i].Width);
        _width = widest;
        float alignWidth = float.IsInfinity(maxWidth) ? widest : maxWidth;
        float y = 0;
        for (int i = 0; i < _lines.Count; i++)
        {
            ref var l = ref _lines[i];
            l.Top = y;
            l.Baseline = y + l.Above;
            y += l.Height;

            bool rtl = (l.ParaLevel & 1) != 0;
            var align = _style.TextAlign;
            if (align == TextAlign.Justify) align = TextAlign.Start;
            if (align == TextAlign.Start) align = rtl ? TextAlign.Right : TextAlign.Left;
            else if (align == TextAlign.End) align = rtl ? TextAlign.Left : TextAlign.Right;
            float free = alignWidth - l.Width;
            float left;
            if (free < 0) left = rtl ? free : 0;   // 放不下时按行首对齐，向行尾方向溢出
            else
                left = align switch
                {
                    TextAlign.Right => free,
                    TextAlign.Center => free / 2,
                    _ => 0,
                };
            l.X = left - (rtl ? l.HangWidth : 0);
            float x = 0;
            for (int f = l.FragStart; f < l.FragStart + l.FragCount; f++)
            {
                ref var fr = ref _frags[f];
                fr.X = x;
                x += fr.Width;
            }
        }
        _height = y;

        // 占位盒
        _placeholderRects ??= new Rect[_placeholders.Length];
        Array.Clear(_placeholderRects, 0, _placeholderRects.Length);
        for (int i = 0; i < _lines.Count; i++)
        {
            ref readonly var l = ref _lines[i];
            for (int f = l.FragStart; f < l.FragStart + l.FragCount; f++)
            {
                ref readonly var fr = ref _frags[f];
                if (fr.Item < 0 || _items[fr.Item].Placeholder < 0) continue;
                var p = _placeholders[_items[fr.Item].Placeholder].Style;
                float top = p.Alignment switch
                {
                    PlaceholderAlignment.Top => l.Top,
                    PlaceholderAlignment.Bottom => l.Top + l.Height - p.Height,
                    PlaceholderAlignment.Middle => l.Baseline - _strutXHeight / 2 - p.Height / 2,
                    _ => l.Baseline - p.Baseline,
                };
                _placeholderRects[_items[fr.Item].Placeholder] = new Rect(l.X + fr.X, top, p.Width, p.Height);
            }
        }
    }
}
