using System;
using System.Collections.Generic;
using AwAUI.Unicode;

namespace AwAUI.Text;

public sealed partial class TextLayout
{
    /// <summary>一行中按视觉顺序排列的字素单元。</summary>
    readonly struct VisualUnit
    {
        public VisualUnit(int unit, float x, float width, bool rtl)
        {
            Unit = unit;
            X = x;
            Width = width;
            Rtl = rtl;
        }

        public int Unit { get; }
        public float X { get; }
        public float Width { get; }
        public bool Rtl { get; }
        public float Right => X + Width;
    }

    enum UnitFilter : byte
    {
        /// <summary>全部单元（含强制换行符与折叠掉的空白）。</summary>
        All,
        /// <summary>不含强制换行符（命中测试：点击行尾不会落到换行符之后）。</summary>
        NoHardBreak,
        /// <summary>不含零宽的折叠空白、控制字符与换行符（光标的视觉移动）。</summary>
        Navigable,
    }

    void CollectVisualUnits(int lineIndex, List<VisualUnit> list, UnitFilter filter)
    {
        list.Clear();
        ref readonly var l = ref _lines[lineIndex];
        for (int f = l.FragStart; f < l.FragStart + l.FragCount; f++)
        {
            ref readonly var fr = ref _frags[f];
            if (fr.Item < 0) continue;   // 省略号不对应文本
            bool rtl = (fr.Level & 1) != 0;
            float x = l.X + fr.X;
            int count = fr.UnitEnd - fr.UnitStart;
            for (int i = 0; i < count; i++)
            {
                int u = rtl ? fr.UnitEnd - 1 - i : fr.UnitStart + i;
                float w = UnitWidth(l, u);
                var flags = _units[u].Flags;
                bool skip = filter switch
                {
                    UnitFilter.NoHardBreak => (flags & UnitFlags.HardBreak) != 0,
                    UnitFilter.Navigable => (flags & UnitFlags.Removed) != 0,
                    _ => false,
                };
                if (!skip) list.Add(new VisualUnit(u, x, w, rtl));
                x += w;
            }
        }
    }

    int ClampOffset(int offset) => offset < 0 ? 0 : offset > _text.Length ? _text.Length : offset;

    /// <summary>插入点所在的行：软换行处的上游位置属于上一行。</summary>
    int LineIndexOf(TextPosition position)
    {
        int o = ClampOffset(position.Offset);
        int lo = 0, hi = _lines.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (_lines[mid].StartOffset <= o) lo = mid;
            else hi = mid - 1;
        }
        if (position.Affinity == TextAffinity.Upstream && lo > 0 && o == _lines[lo].StartOffset
            && _lines[lo - 1].EndOffset == o && !_lines[lo - 1].HardBreak)
            lo--;
        return lo;
    }

    int LineIndexAtY(float y)
    {
        int lo = 0, hi = _lines.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (y < _lines[mid].Top + _lines[mid].Height) hi = mid;
            else lo = mid + 1;
        }
        return lo;
    }

    /// <summary>命中测试：离点最近的插入点（偏移 + 亲和性）。点在第一行之上或最后一行之下时取该行；在行的左右之外时取行的视觉端点。</summary>
    public TextPosition HitTest(Point point)
    {
        EnsureLayout();
        float y = float.IsNaN(point.Y) ? 0 : point.Y;
        return HitTestLine(LineIndexAtY(y), point.X);
    }

    /// <summary>在第 <paramref name="lineIndex"/> 行中按水平位置 <paramref name="x"/> 求插入点（上下移动光标时保持列位置）。</summary>
    public TextPosition HitTestLine(int lineIndex, float x)
    {
        EnsureLayout();
        if ((uint)lineIndex >= (uint)_lines.Count) throw new ArgumentOutOfRangeException(nameof(lineIndex));
        if (float.IsNaN(x)) x = 0;
        var list = new List<VisualUnit>();
        CollectVisualUnits(lineIndex, list, UnitFilter.NoHardBreak);
        ref readonly var l = ref _lines[lineIndex];
        if (list.Count == 0) return new TextPosition(l.StartOffset);
        foreach (var vu in list)
        {
            if (x < vu.X + vu.Width / 2) return Normalize(lineIndex, LeftEdge(vu));
            if (x < vu.Right) return Normalize(lineIndex, RightEdge(vu));
        }
        return Normalize(lineIndex, RightEdge(list[list.Count - 1]));
    }

    /// <summary>
    /// 上游亲和性只在必要时保留：软换行的行尾，或双向文本中上下游显示在不同位置时。其余情况规范化为下游。
    /// </summary>
    TextPosition Normalize(int lineIndex, TextPosition p)
    {
        if (p.Affinity == TextAffinity.Downstream) return p;
        ref readonly var l = ref _lines[lineIndex];
        if (p.Offset == l.EndOffset && !l.HardBreak && lineIndex + 1 < _lines.Count) return p;
        var all = new List<VisualUnit>();
        CollectVisualUnits(lineIndex, all, UnitFilter.All);
        var down = new TextPosition(p.Offset);
        return Math.Abs(CaretX(all, l, down) - CaretX(all, l, p)) < Epsilon ? down : p;
    }

    /// <summary>单元左边缘处的插入点：从左到右时是它之前（下游），从右到左时是它之后（上游）。</summary>
    TextPosition LeftEdge(in VisualUnit vu) =>
        vu.Rtl ? new TextPosition(_units[vu.Unit].End, TextAffinity.Upstream) : new TextPosition(_units[vu.Unit].Start);

    TextPosition RightEdge(in VisualUnit vu) =>
        vu.Rtl ? new TextPosition(_units[vu.Unit].Start) : new TextPosition(_units[vu.Unit].End, TextAffinity.Upstream);

    /// <summary>插入点的矩形：宽度为 0、高度为行高，x 为光标位置。双向文本中按亲和性选择前一个或后一个字素簇的边缘。</summary>
    public Rect GetCaretRect(TextPosition position)
    {
        EnsureLayout();
        int li = LineIndexOf(position);
        ref readonly var l = ref _lines[li];
        var list = new List<VisualUnit>();
        CollectVisualUnits(li, list, UnitFilter.All);
        return new Rect(CaretX(list, l, position), l.Top, 0, l.Height);
    }

    float CaretX(List<VisualUnit> list, in LineInfo l, TextPosition position)
    {
        int o = ClampOffset(position.Offset);
        int after = -1, before = -1, inside = -1;
        for (int i = 0; i < list.Count; i++)
        {
            ref readonly var u = ref _units[list[i].Unit];
            if (u.Start == o) after = i;
            if (u.End == o) before = i;
            if (u.Start < o && o < u.End) inside = i;
        }
        if (position.Affinity == TextAffinity.Upstream && before >= 0) return TrailingX(list[before]);
        if (after >= 0) return LeadingX(list[after]);
        if (before >= 0) return TrailingX(list[before]);
        if (inside >= 0) return LeadingX(list[inside]);
        if (list.Count == 0) return ContentLeft(l) + ((l.ParaLevel & 1) != 0 ? l.Width : 0);
        // 被省略号截掉的部分：行的视觉末端
        return (l.ParaLevel & 1) != 0 ? list[0].X : list[list.Count - 1].Right;
    }

    static float LeadingX(in VisualUnit vu) => vu.Rtl ? vu.Right : vu.X;

    static float TrailingX(in VisualUnit vu) => vu.Rtl ? vu.X : vu.Right;

    /// <summary>选区矩形：每行按视觉顺序合并相邻的被选单元，双向文本中一行可能有多个不相邻的矩形。结果追加到 <paramref name="result"/>。</summary>
    public void GetSelectionRects(TextRange range, IList<Rect> result)
    {
        if (result is null) throw new ArgumentNullException(nameof(result));
        EnsureLayout();
        int min = ClampOffset(range.Min), max = ClampOffset(range.Max);
        if (min >= max) return;
        var list = new List<VisualUnit>();
        for (int li = 0; li < _lines.Count; li++)
        {
            ref readonly var l = ref _lines[li];
            if (l.EndOffset <= min || l.StartOffset >= max) continue;
            CollectVisualUnits(li, list, UnitFilter.All);
            float left = float.NaN, right = 0;
            foreach (var vu in list)
            {
                if (vu.Width == 0) continue;
                ref readonly var u = ref _units[vu.Unit];
                bool selected = u.Start < max && u.End > min;
                if (selected)
                {
                    if (!float.IsNaN(left) && Math.Abs(vu.X - right) < Epsilon) right = vu.Right;
                    else
                    {
                        if (!float.IsNaN(left)) result.Add(Rect.FromLTRB(Math.Min(left, right), l.Top, Math.Max(left, right), l.Top + l.Height));
                        left = vu.X;
                        right = vu.Right;
                    }
                }
                else if (!float.IsNaN(left))
                {
                    result.Add(Rect.FromLTRB(Math.Min(left, right), l.Top, Math.Max(left, right), l.Top + l.Height));
                    left = float.NaN;
                }
            }
            if (!float.IsNaN(left)) result.Add(Rect.FromLTRB(Math.Min(left, right), l.Top, Math.Max(left, right), l.Top + l.Height));
        }
    }

    // ------------------------------------------------------------------ 边界

    /// <summary>
    /// 包含 <paramref name="offset"/> 处字符的字素簇（offset 为文本长度时取最后一个簇；空文本返回空范围）。
    /// 与布局使用的簇一致：占位盒总是单独成簇。
    /// </summary>
    public TextRange GetGraphemeAt(int offset)
    {
        if (_text.Length == 0) return default;
        int o = Math.Min(ClampOffset(offset), _text.Length - 1);
        ref readonly var u = ref _units[_unitOfChar[o]];
        return new TextRange(u.Start, u.End);
    }

    /// <summary>offset 之后的下一个字素簇边界（与布局的簇一致）。</summary>
    int NextGraphemeBoundary(int offset) => offset >= _text.Length ? _text.Length : _units[_unitOfChar[offset]].End;

    /// <summary>offset 之前的上一个字素簇边界（与布局的簇一致）。</summary>
    int PreviousGraphemeBoundary(int offset) => offset <= 0 ? 0 : _units[_unitOfChar[Math.Min(offset, _text.Length) - 1]].Start;

    /// <summary>包含 <paramref name="offset"/> 处字符的词段（UAX #29；空白与标点也各自成段）。</summary>
    public TextRange GetWordAt(int offset)
    {
        if (_text.Length == 0) return default;
        int o = Math.Min(ClampOffset(offset), _text.Length - 1);
        int p = ParagraphStartBefore(o);
        var e = TextSegmenter.EnumerateWords(_text.AsSpan(p));
        while (e.MoveNext())
        {
            var w = e.Current;
            if (p + w.End > o) return new TextRange(p + w.Start, p + w.End);
        }
        return new TextRange(o, o);
    }

    /// <summary>包含 <paramref name="offset"/> 的行的文本范围（含行尾空白与换行符）。</summary>
    public TextRange GetLineAt(int offset)
    {
        EnsureLayout();
        ref readonly var l = ref _lines[LineIndexOf(new TextPosition(offset))];
        return new TextRange(l.StartOffset, l.EndOffset);
    }

    /// <summary>不晚于 <paramref name="offset"/> 的硬段落（强制换行之后）起点。</summary>
    int ParagraphStartBefore(int offset)
    {
        int p = offset;
        while (p > 0 && !IsHardBreakChar(_text[p - 1])) p--;
        return p;
    }

    // ------------------------------------------------------------------ 光标移动

    /// <summary>
    /// 移动插入点：左右按视觉顺序（双向文本中与屏幕方向一致），前后按逻辑顺序，上下保持水平位置，词首尾与行首尾按逻辑顺序。
    /// 在文本两端无法继续移动时返回端点。
    /// </summary>
    public TextPosition Move(TextPosition from, CaretMove move)
    {
        EnsureLayout();
        int len = _text.Length;
        int o = ClampOffset(from.Offset);
        switch (move)
        {
            case CaretMove.TextStart:
                return new TextPosition(0);
            case CaretMove.TextEnd:
                return new TextPosition(len);
            case CaretMove.Backward:
            {
                int p = o;
                do p = PreviousGraphemeBoundary(p);
                while (p > 0 && IsCollapsed(p));
                return new TextPosition(p);
            }
            case CaretMove.Forward:
            {
                int p = o;
                do p = NextGraphemeBoundary(p);
                while (p < len && IsCollapsed(p));
                return new TextPosition(p);
            }
            case CaretMove.LineStart:
                return new TextPosition(_lines[LineIndexOf(from)].StartOffset);
            case CaretMove.LineEnd:
                return LineEndPosition(LineIndexOf(from));
            case CaretMove.Up:
            case CaretMove.Down:
            {
                int li = LineIndexOf(from);
                int target = move == CaretMove.Up ? li - 1 : li + 1;
                if (target < 0) return new TextPosition(0);
                if (target >= _lines.Count) return new TextPosition(len);
                return HitTestLine(target, GetCaretRect(from).X);
            }
            case CaretMove.WordStart:
            {
                int result = 0;
                var e = TextSegmenter.EnumerateWords(_text);
                while (e.MoveNext())
                {
                    var w = e.Current;
                    if (w.Start >= o) break;
                    if (w.IsWordLike) result = w.Start;
                }
                return new TextPosition(result);
            }
            case CaretMove.WordEnd:
            {
                var e = TextSegmenter.EnumerateWords(_text);
                while (e.MoveNext())
                {
                    var w = e.Current;
                    if (w.IsWordLike && w.End > o) return new TextPosition(w.End);
                }
                return new TextPosition(len);
            }
            case CaretMove.Left:
                return MoveVisual(from, right: false);
            case CaretMove.Right:
                return MoveVisual(from, right: true);
            default:
                throw new ArgumentOutOfRangeException(nameof(move));
        }
    }

    /// <summary>offset 处的单元是否是零宽的折叠空白或控制字符（不含强制换行符）。</summary>
    bool IsCollapsed(int offset)
    {
        if (offset < 0 || offset >= _text.Length) return false;
        var f = _units[_unitOfChar[offset]].Flags;
        return (f & UnitFlags.Removed) != 0 && (f & UnitFlags.HardBreak) == 0;
    }

    TextPosition LineEndPosition(int li)
    {
        ref readonly var l = ref _lines[li];
        bool soft = !l.HardBreak && li + 1 < _lines.Count && _lines[li + 1].StartOffset == l.EndOffset;
        return new TextPosition(l.EndNoBreak, soft ? TextAffinity.Upstream : TextAffinity.Downstream);
    }

    TextPosition MoveVisual(TextPosition from, bool right)
    {
        int li = LineIndexOf(from);
        var list = new List<VisualUnit>();
        CollectVisualUnits(li, list, UnitFilter.Navigable);
        int slot = FindSlot(list, li, from);
        bool rtlPara = (_lines[li].ParaLevel & 1) != 0;
        if (right)
        {
            if (slot < list.Count) return Normalize(li, RightEdge(list[slot]));
            // 越过行的右端：从左到右的段落进入下一行行首，从右到左的段落进入上一行行尾
            if (!rtlPara) return li + 1 < _lines.Count ? new TextPosition(_lines[li + 1].StartOffset) : LineEndPosition(li);
            return li > 0 ? LineEndPosition(li - 1) : new TextPosition(_lines[li].StartOffset);
        }
        if (slot > 0) return Normalize(li, LeftEdge(list[slot - 1]));
        if (!rtlPara) return li > 0 ? LineEndPosition(li - 1) : new TextPosition(_lines[li].StartOffset);
        return li + 1 < _lines.Count ? new TextPosition(_lines[li + 1].StartOffset) : LineEndPosition(li);
    }

    /// <summary>插入点在视觉单元序列中的槽位（0..Count，槽 k 位于第 k 个单元的左边缘）。</summary>
    int FindSlot(List<VisualUnit> list, int li, TextPosition position)
    {
        int o = ClampOffset(position.Offset);
        int after = -1, before = -1;
        for (int i = 0; i < list.Count; i++)
        {
            ref readonly var u = ref _units[list[i].Unit];
            if (u.Start == o) after = i;
            if (u.End == o) before = i;
        }
        if (position.Affinity == TextAffinity.Upstream && before >= 0) return list[before].Rtl ? before : before + 1;
        if (after >= 0) return list[after].Rtl ? after + 1 : after;
        if (before >= 0) return list[before].Rtl ? before : before + 1;
        // 落在零宽单元或截断区域中：取 x 最近的槽位
        var all = new List<VisualUnit>();
        CollectVisualUnits(li, all, UnitFilter.All);
        float x = CaretX(all, _lines[li], position);
        int best = 0;
        float bestDist = float.PositiveInfinity;
        for (int k = 0; k <= list.Count; k++)
        {
            float bx = k < list.Count ? list[k].X : list.Count > 0 ? list[list.Count - 1].Right : x;
            float d = Math.Abs(bx - x);
            if (d < bestDist)
            {
                bestDist = d;
                best = k;
            }
        }
        return best;
    }
}
