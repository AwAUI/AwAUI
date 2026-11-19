using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AwAUI.Layout;

/// <summary>轨道尺寸函数中一个「宽度」的种类（CSS Grid 1 §7.2.1 <c>&lt;track-breadth&gt;</c>）。</summary>
public enum GridBreadthKind : byte
{
    /// <summary><c>auto</c>：作最小值时为项目的最小贡献，作最大值时为 max-content 贡献（并可被拉伸）。</summary>
    Auto,
    /// <summary><c>&lt;length-percentage&gt;</c>（百分比相对网格容器内容盒在该轴的尺寸；不确定时按 auto）。</summary>
    Length,
    /// <summary><c>&lt;flex&gt;</c>（<c>fr</c>）。只能作最大值；作最小值时按 auto 处理。</summary>
    Flex,
    /// <summary><c>min-content</c>。</summary>
    MinContent,
    /// <summary><c>max-content</c>。</summary>
    MaxContent,
}

/// <summary><c>&lt;track-breadth&gt;</c>：长度百分比、fr、auto、min-content 或 max-content。默认值为 auto。</summary>
public readonly struct GridBreadth : IEquatable<GridBreadth>
{
    GridBreadth(GridBreadthKind kind, LengthPercentage length, float flex)
    {
        Kind = kind;
        Length = length;
        Flex = flex;
    }

    public GridBreadthKind Kind { get; }

    /// <summary><see cref="GridBreadthKind.Length"/> 的值。</summary>
    public LengthPercentage Length { get; }

    /// <summary><see cref="GridBreadthKind.Flex"/> 的伸缩因子（非负）。</summary>
    public float Flex { get; }

    public static GridBreadth Auto => default;
    public static GridBreadth MinContent => new(GridBreadthKind.MinContent, default, 0);
    public static GridBreadth MaxContent => new(GridBreadthKind.MaxContent, default, 0);
    public static GridBreadth Of(LengthPercentage length) => new(GridBreadthKind.Length, length, 0);
    public static GridBreadth Px(float px) => Of(LengthPercentage.Px(px));
    public static GridBreadth Percent(float percent) => Of(LengthPercentage.Percent(percent));

    /// <summary><c>&lt;flex&gt;</c>；负数与非有限值按 0。</summary>
    public static GridBreadth Fr(float flex) =>
        new(GridBreadthKind.Flex, default, flex > 0 && !float.IsInfinity(flex) ? Math.Min(flex, LayoutMath.MaxFactor) : 0);

    public static implicit operator GridBreadth(float px) => Px(px);

    public bool Equals(GridBreadth o) => Kind == o.Kind && Length.Equals(o.Length) && Flex.Equals(o.Flex);
    public override bool Equals(object? obj) => obj is GridBreadth b && Equals(b);
    public override int GetHashCode() => HashCodeCompat.Combine((int)Kind, Length.GetHashCode(), Flex.GetHashCode());
    public static bool operator ==(GridBreadth a, GridBreadth b) => a.Equals(b);
    public static bool operator !=(GridBreadth a, GridBreadth b) => !a.Equals(b);

    public override string ToString() => Kind switch
    {
        GridBreadthKind.Length => Length.ToString(),
        GridBreadthKind.Flex => Flex.ToString(CultureInfo.InvariantCulture) + "fr",
        GridBreadthKind.MinContent => "min-content",
        GridBreadthKind.MaxContent => "max-content",
        _ => "auto",
    };
}

/// <summary>
/// 轨道尺寸函数 <c>&lt;track-size&gt;</c>：<c>minmax(min, max)</c> 或 <c>fit-content(limit)</c>。
/// 单个宽度 <c>b</c> 等价于 <c>minmax(b, b)</c>，单个 <c>fr</c> 等价于 <c>minmax(auto, fr)</c>。默认值为 auto。
/// 可从 <see cref="float"/>（px）隐式转换。
/// </summary>
public readonly struct GridTrackSize : IEquatable<GridTrackSize>
{
    GridTrackSize(GridBreadth min, GridBreadth max, bool fitContent)
    {
        Min = min;
        Max = max;
        IsFitContent = fitContent;
    }

    /// <summary>最小尺寸函数（不会是 fr：构造时按 auto 处理）。</summary>
    public GridBreadth Min { get; }

    /// <summary>最大尺寸函数。<see cref="IsFitContent"/> 时是 fit-content 的上限（长度百分比）。</summary>
    public GridBreadth Max { get; }

    /// <summary>是否为 <c>fit-content(&lt;length-percentage&gt;)</c>：最小 auto，最大 max-content 但不超过上限。</summary>
    public bool IsFitContent { get; }

    public static GridTrackSize Auto => default;
    public static GridTrackSize MinContent => new(GridBreadth.MinContent, GridBreadth.MinContent, false);
    public static GridTrackSize MaxContent => new(GridBreadth.MaxContent, GridBreadth.MaxContent, false);
    public static GridTrackSize Px(float px) => Of(GridBreadth.Px(px));
    public static GridTrackSize Percent(float percent) => Of(GridBreadth.Percent(percent));
    public static GridTrackSize Fr(float flex) => Of(GridBreadth.Fr(flex));

    /// <summary>单个宽度：fr 为 <c>minmax(auto, fr)</c>，其余为 <c>minmax(b, b)</c>。</summary>
    public static GridTrackSize Of(GridBreadth breadth) =>
        breadth.Kind == GridBreadthKind.Flex ? new(GridBreadth.Auto, breadth, false) : new(breadth, breadth, false);

    /// <summary><c>minmax(min, max)</c>。fr 形式的最小值（CSS 中无效）按 auto 处理。</summary>
    public static GridTrackSize MinMax(GridBreadth min, GridBreadth max) =>
        new(min.Kind == GridBreadthKind.Flex ? GridBreadth.Auto : min, max, false);

    /// <summary><c>fit-content(limit)</c>。</summary>
    public static GridTrackSize FitContent(LengthPercentage limit) => new(GridBreadth.Auto, GridBreadth.Of(limit), true);

    public static implicit operator GridTrackSize(float px) => Px(px);

    public bool Equals(GridTrackSize o) => Min.Equals(o.Min) && Max.Equals(o.Max) && IsFitContent == o.IsFitContent;
    public override bool Equals(object? obj) => obj is GridTrackSize t && Equals(t);
    public override int GetHashCode() => HashCodeCompat.Combine(Min.GetHashCode(), Max.GetHashCode(), IsFitContent ? 1 : 0);
    public static bool operator ==(GridTrackSize a, GridTrackSize b) => a.Equals(b);
    public static bool operator !=(GridTrackSize a, GridTrackSize b) => !a.Equals(b);

    public override string ToString()
    {
        if (IsFitContent) return "fit-content(" + Max.Length + ")";
        if (Max.Kind == GridBreadthKind.Flex && Min.Kind == GridBreadthKind.Auto) return Max.ToString();
        if (Min.Equals(Max)) return Min.ToString();
        return "minmax(" + Min + ", " + Max + ")";
    }
}

/// <summary><c>repeat()</c> 的重复方式。</summary>
public enum GridRepeatKind : byte
{
    /// <summary>重复给定次数（不含 <c>repeat()</c> 的普通轨道也表示为重复 1 次）。</summary>
    Count,
    /// <summary><c>auto-fill</c>：按网格容器的确定尺寸（或最大尺寸）重复尽可能多次。</summary>
    AutoFill,
    /// <summary><c>auto-fit</c>：同 auto-fill，但没有项目的重复轨道折叠为 0（两侧间距合并）。</summary>
    AutoFit,
}

/// <summary>
/// 轨道列表的一段：<paramref name="tracks"/> 重复若干次。<see cref="LineNames"/> 有 <c>Tracks.Count + 1</c> 项，
/// 第 i 项是第 i 条轨道之前的线名（最后一项是最后一条轨道之后的线名）。相邻两次重复（以及相邻两段）交界处的线名合并到同一条线。
/// 没有轨道的段（只能是 <see cref="GridRepeatKind.Count"/>，次数为 1）只给当前这条线加上线名，例如 <c>[a] repeat(2, 10px)</c> 中的 <c>[a]</c>。
/// 不可变，按内容比较相等。
/// </summary>
public sealed class GridTrackRepeat : IEquatable<GridTrackRepeat>
{
    static readonly string[] s_noNames = Array.Empty<string>();

    /// <param name="kind">重复方式。</param>
    /// <param name="count"><see cref="GridRepeatKind.Count"/> 的次数，钳制到 [1, <see cref="GridLine.MaxLine"/>]；其他方式忽略。</param>
    /// <param name="tracks">被重复的轨道（auto-fill / auto-fit 至少一条；为空时是只有线名的段）。</param>
    /// <param name="lineNames">线名，null 表示没有；项数必须为 <c>tracks.Count + 1</c>。</param>
    public GridTrackRepeat(GridRepeatKind kind, int count, IReadOnlyList<GridTrackSize> tracks, IReadOnlyList<IReadOnlyList<string>>? lineNames = null)
    {
        if (tracks is null) throw new ArgumentNullException(nameof(tracks));
        if (tracks.Count == 0 && kind != GridRepeatKind.Count) throw new ArgumentException("auto-fill / auto-fit 至少需要一条轨道。", nameof(tracks));
        if (lineNames is not null && lineNames.Count != tracks.Count + 1)
            throw new ArgumentException("线名的项数必须比轨道数多 1。", nameof(lineNames));
        Kind = kind;
        Count = kind == GridRepeatKind.Count && tracks.Count > 0 ? Math.Max(1, Math.Min(count, GridLine.MaxLine)) : 1;
        var t = new GridTrackSize[tracks.Count];
        for (int i = 0; i < t.Length; i++) t[i] = tracks[i];
        Tracks = t;
        var names = new string[t.Length + 1][];
        for (int i = 0; i < names.Length; i++)
        {
            var src = lineNames?[i];
            if (src is null || src.Count == 0) names[i] = s_noNames;
            else
            {
                var a = new string[src.Count];
                for (int j = 0; j < a.Length; j++) a[j] = src[j] ?? throw new ArgumentException("线名不能为 null。", nameof(lineNames));
                names[i] = a;
            }
            if (names[i].Length > 0) HasNames = true;
        }
        LineNames = names;
    }

    public GridRepeatKind Kind { get; }

    /// <summary>重复次数（auto-fill / auto-fit 为 1，实际次数在布局时确定）。</summary>
    public int Count { get; }

    public IReadOnlyList<GridTrackSize> Tracks { get; }

    public IReadOnlyList<IReadOnlyList<string>> LineNames { get; }

    /// <summary>是否有任何线名。</summary>
    public bool HasNames { get; }

    internal GridTrackSize[] TrackArray => (GridTrackSize[])Tracks;

    internal string[][] NameArray => (string[][])LineNames;

    public bool Equals(GridTrackRepeat? o)
    {
        if (ReferenceEquals(this, o)) return true;
        if (o is null || Kind != o.Kind || Count != o.Count || Tracks.Count != o.Tracks.Count) return false;
        for (int i = 0; i < Tracks.Count; i++)
            if (!Tracks[i].Equals(o.Tracks[i])) return false;
        var a = NameArray;
        var b = o.NameArray;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i].Length != b[i].Length) return false;
            for (int j = 0; j < a[i].Length; j++)
                if (!string.Equals(a[i][j], b[i][j], StringComparison.Ordinal)) return false;
        }
        return true;
    }

    public override bool Equals(object? obj) => obj is GridTrackRepeat r && Equals(r);

    public override int GetHashCode()
    {
        int h = HashCodeCompat.Combine((int)Kind, Count, Tracks.Count);
        foreach (var t in Tracks) h = HashCodeCompat.Combine(h, t.GetHashCode());
        return h;
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        bool repeat = Kind != GridRepeatKind.Count || Count != 1;
        if (repeat)
        {
            sb.Append("repeat(").Append(Kind switch
            {
                GridRepeatKind.AutoFill => "auto-fill",
                GridRepeatKind.AutoFit => "auto-fit",
                _ => Count.ToString(CultureInfo.InvariantCulture),
            }).Append(", ");
        }
        var names = NameArray;
        for (int i = 0; i <= Tracks.Count; i++)
        {
            if (names[i].Length > 0)
            {
                if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' ');
                sb.Append('[').Append(string.Join(" ", names[i])).Append(']');
            }
            if (i < Tracks.Count)
            {
                if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' ');
                sb.Append(Tracks[i]);
            }
        }
        if (repeat) sb.Append(')');
        return sb.ToString();
    }
}

/// <summary>
/// <c>grid-template-columns / rows</c> 的轨道列表（也用于 <c>grid-auto-columns / rows</c>，此时只有一段且没有线名）。
/// 由若干 <see cref="GridTrackRepeat"/> 依次组成；至多一段是 auto-fill / auto-fit（多出的按重复 1 次处理）。
/// 不可变，按内容比较相等（<see cref="LayoutStyle"/> 的相等比较依赖这一点）。
/// </summary>
public sealed class GridTrackList : IEquatable<GridTrackList>
{
    public GridTrackList(IReadOnlyList<GridTrackRepeat> segments)
    {
        if (segments is null) throw new ArgumentNullException(nameof(segments));
        var a = new GridTrackRepeat[segments.Count];
        for (int i = 0; i < a.Length; i++)
        {
            a[i] = segments[i] ?? throw new ArgumentException("段不能为 null。", nameof(segments));
            if (a[i].HasNames) HasNames = true;
            if (a[i].Kind != GridRepeatKind.Count) HasAutoRepeat = true;
        }
        Segments = a;
    }

    /// <summary>不带线名的普通轨道列表（一段，重复 1 次）。</summary>
    public static GridTrackList Of(params GridTrackSize[] tracks) =>
        new(new[] { new GridTrackRepeat(GridRepeatKind.Count, 1, tracks) });

    public IReadOnlyList<GridTrackRepeat> Segments { get; }

    /// <summary>是否有任何线名。</summary>
    public bool HasNames { get; }

    /// <summary>是否含 auto-fill / auto-fit。</summary>
    public bool HasAutoRepeat { get; }

    internal GridTrackRepeat[] SegmentArray => (GridTrackRepeat[])Segments;

    public bool Equals(GridTrackList? o)
    {
        if (ReferenceEquals(this, o)) return true;
        if (o is null || Segments.Count != o.Segments.Count) return false;
        for (int i = 0; i < Segments.Count; i++)
            if (!Segments[i].Equals(o.Segments[i])) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is GridTrackList l && Equals(l);

    public override int GetHashCode()
    {
        int h = Segments.Count;
        foreach (var s in Segments) h = HashCodeCompat.Combine(h, s.GetHashCode());
        return h;
    }

    public override string ToString()
    {
        var parts = new string[Segments.Count];
        for (int i = 0; i < parts.Length; i++) parts[i] = Segments[i].ToString();
        return string.Join(" ", parts);
    }
}

/// <summary><see cref="GridLine"/> 的种类。</summary>
public enum GridLineKind : byte
{
    Auto,
    /// <summary>线编号（可带线名）或只有线名（<see cref="GridLine.Integer"/> 为 0）。</summary>
    Line,
    /// <summary><c>span</c>。</summary>
    Span,
}

/// <summary>
/// 网格放置属性的值 <c>&lt;grid-line&gt;</c>（<c>grid-row-start</c> 等）：<c>auto</c>、<c>&lt;integer&gt; &lt;name&gt;?</c>、
/// <c>&lt;name&gt;</c>、<c>span &lt;integer&gt; &lt;name&gt;?</c>。默认值为 auto。
/// </summary>
public readonly struct GridLine : IEquatable<GridLine>
{
    /// <summary>
    /// 网格线编号的绝对值上限（与 Firefox 相同）：更大的编号、span 与 <c>repeat()</c> 次数被钳制，显式网格最多
    /// <c>MaxLine - 1</c> 条轨道，自动放置也不会越过这条线（越界的项目被钳制而可能重叠）。所以每个轴最多约 2 × MaxLine 条轨道，
    /// 布局的时间与内存与 CSS 中的数值大小无关。
    /// </summary>
    public const int MaxLine = 10000;

    GridLine(GridLineKind kind, int integer, string? name)
    {
        Kind = kind;
        Integer = integer;
        Name = name;
    }

    public GridLineKind Kind { get; }

    /// <summary>线编号（可为负，0 表示只有线名）或 span 的跨度（≥ 1）。</summary>
    public int Integer { get; }

    /// <summary>线名；没有时为 null。</summary>
    public string? Name { get; }

    public bool IsAuto => Kind == GridLineKind.Auto;
    public bool IsSpan => Kind == GridLineKind.Span;

    public static GridLine Auto => default;

    /// <summary>第 <paramref name="n"/> 条线（负数从末端数）；带 <paramref name="name"/> 时为第 n 条同名线。<paramref name="n"/> 为 0 时必须给出线名。</summary>
    public static GridLine Line(int n, string? name = null)
    {
        if (n == 0 && name is null) throw new ArgumentException("线编号不能为 0。", nameof(n));
        return new GridLine(GridLineKind.Line, Clamp(n), name);
    }

    /// <summary>只有线名（<c>grid-row-start: header</c>）：先找 <c>header-start</c> / <c>header-end</c>，再找第一条名为 header 的线。</summary>
    public static GridLine Named(string name) => new(GridLineKind.Line, 0, name ?? throw new ArgumentNullException(nameof(name)));

    /// <summary><c>span n</c> 或 <c>span n name</c>。</summary>
    public static GridLine Span(int n = 1, string? name = null) => new(GridLineKind.Span, Math.Max(1, Clamp(n)), name);

    public static implicit operator GridLine(int n) => Line(n);

    static int Clamp(int v) => Math.Max(-MaxLine, Math.Min(MaxLine, v));

    public bool Equals(GridLine o) => Kind == o.Kind && Integer == o.Integer && string.Equals(Name, o.Name, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is GridLine l && Equals(l);
    public override int GetHashCode() => HashCodeCompat.Combine((int)Kind, Integer, Name is null ? 0 : StringComparer.Ordinal.GetHashCode(Name));
    public static bool operator ==(GridLine a, GridLine b) => a.Equals(b);
    public static bool operator !=(GridLine a, GridLine b) => !a.Equals(b);

    public override string ToString()
    {
        string n = Integer.ToString(CultureInfo.InvariantCulture);
        return Kind switch
        {
            GridLineKind.Span => Name is null ? "span " + n : "span " + n + " " + Name,
            GridLineKind.Line => Integer == 0 ? Name! : Name is null ? n : n + " " + Name,
            _ => "auto",
        };
    }
}

/// <summary><c>grid-template-areas</c> 中的一个命名区域（以 0 起的线下标表示，止线不含）。</summary>
public readonly struct GridArea : IEquatable<GridArea>
{
    public GridArea(string name, int rowStart, int rowEnd, int columnStart, int columnEnd)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        if (rowStart < 0 || rowEnd <= rowStart || columnStart < 0 || columnEnd <= columnStart)
            throw new ArgumentOutOfRangeException(nameof(rowStart), "区域必须是非空矩形。");
        RowStart = rowStart;
        RowEnd = rowEnd;
        ColumnStart = columnStart;
        ColumnEnd = columnEnd;
    }

    public string Name { get; }
    public int RowStart { get; }
    public int RowEnd { get; }
    public int ColumnStart { get; }
    public int ColumnEnd { get; }

    public bool Equals(GridArea o) => string.Equals(Name, o.Name, StringComparison.Ordinal) && RowStart == o.RowStart && RowEnd == o.RowEnd
                                      && ColumnStart == o.ColumnStart && ColumnEnd == o.ColumnEnd;
    public override bool Equals(object? obj) => obj is GridArea a && Equals(a);
    public override int GetHashCode() => HashCodeCompat.Combine(StringComparer.Ordinal.GetHashCode(Name), RowStart, RowEnd, HashCodeCompat.Combine(ColumnStart, ColumnEnd));
    public override string ToString() => FormattableString.Invariant($"{Name} {RowStart + 1} / {ColumnStart + 1} / {RowEnd + 1} / {ColumnEnd + 1}");
}

/// <summary>
/// <c>grid-template-areas</c>：显式网格的行数、列数与命名区域。每个区域隐式定义线名 <c>名字-start</c> 与 <c>名字-end</c>。
/// 不可变，按内容比较相等。
/// </summary>
public sealed class GridAreas : IEquatable<GridAreas>
{
    Dictionary<string, int>? _index;

    public GridAreas(int rows, int columns, IReadOnlyList<GridArea> areas)
    {
        if (rows < 0 || columns < 0) throw new ArgumentOutOfRangeException(nameof(rows));
        if (areas is null) throw new ArgumentNullException(nameof(areas));
        Rows = rows;
        Columns = columns;
        var a = new GridArea[areas.Count];
        for (int i = 0; i < a.Length; i++) a[i] = areas[i];
        Areas = a;
    }

    /// <summary>
    /// 从行字符串构造（测试与代码中使用的便捷写法）：每行按空白分隔单元，<c>.</c> 序列为空单元。
    /// 列数不一致或同名单元不构成矩形时返回 null（与 CSS 中该声明无效一致）。
    /// </summary>
    public static GridAreas? FromRows(params string[] rows)
    {
        if (rows is null || rows.Length == 0) return null;
        var cells = new List<string[]>();
        foreach (var r in rows)
        {
            var parts = (r ?? "").Split(new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || (cells.Count > 0 && parts.Length != cells[0].Length)) return null;
            cells.Add(parts);
        }
        var found = new Dictionary<string, (int r0, int r1, int c0, int c1)>(StringComparer.Ordinal);
        var order = new List<string>();
        for (int y = 0; y < cells.Count; y++)
        {
            for (int x = 0; x < cells[y].Length; x++)
            {
                var n = cells[y][x];
                if (n.Trim('.').Length == 0) continue;
                if (found.TryGetValue(n, out var b)) found[n] = (Math.Min(b.r0, y), Math.Max(b.r1, y + 1), Math.Min(b.c0, x), Math.Max(b.c1, x + 1));
                else
                {
                    found[n] = (y, y + 1, x, x + 1);
                    order.Add(n);
                }
            }
        }
        var list = new List<GridArea>();
        foreach (var n in order)
        {
            var b = found[n];
            for (int y = b.r0; y < b.r1; y++)
                for (int x = b.c0; x < b.c1; x++)
                    if (!string.Equals(cells[y][x], n, StringComparison.Ordinal)) return null;
            list.Add(new GridArea(n, b.r0, b.r1, b.c0, b.c1));
        }
        return new GridAreas(cells.Count, cells[0].Length, list);
    }

    public int Rows { get; }
    public int Columns { get; }
    public IReadOnlyList<GridArea> Areas { get; }

    /// <summary>按名字查找区域（区域多时建立索引；只读的惰性缓存，并发访问时最多重复建立）。</summary>
    internal bool TryFind(string name, out GridArea area)
    {
        var a = (GridArea[])Areas;
        if (a.Length <= 8)
        {
            foreach (var x in a)
            {
                if (string.Equals(x.Name, name, StringComparison.Ordinal))
                {
                    area = x;
                    return true;
                }
            }
            area = default;
            return false;
        }
        var index = _index;
        if (index is null)
        {
            index = new Dictionary<string, int>(a.Length, StringComparer.Ordinal);
            for (int i = 0; i < a.Length; i++) index[a[i].Name] = i;
            _index = index;
        }
        if (index.TryGetValue(name, out int k))
        {
            area = a[k];
            return true;
        }
        area = default;
        return false;
    }

    public bool Equals(GridAreas? o)
    {
        if (ReferenceEquals(this, o)) return true;
        if (o is null || Rows != o.Rows || Columns != o.Columns || Areas.Count != o.Areas.Count) return false;
        for (int i = 0; i < Areas.Count; i++)
            if (!Areas[i].Equals(o.Areas[i])) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is GridAreas a && Equals(a);
    public override int GetHashCode() => HashCodeCompat.Combine(Rows, Columns, Areas.Count);
    public override string ToString() => FormattableString.Invariant($"{Rows}x{Columns}, {Areas.Count} areas");
}

/// <summary><c>grid-auto-flow</c>。</summary>
public enum GridAutoFlow : byte
{
    Row = 0,
    Column = 1,
    /// <summary><c>row dense</c>。</summary>
    RowDense = 2,
    /// <summary><c>column dense</c>。</summary>
    ColumnDense = 3,
}

/// <summary><c>justify-items</c>（网格容器中 <c>normal</c> 等同 <c>stretch</c>；有宽高比或替换元素的项目按 start）。</summary>
public enum JustifyItems : byte
{
    Normal,
    Stretch,
    Start,
    End,
    Center,
    /// <summary>物理左侧（与书写方向无关）。</summary>
    Left,
    /// <summary>物理右侧。</summary>
    Right,
    FlexStart,
    FlexEnd,
    SelfStart,
    SelfEnd,
    /// <summary>基线对齐：网格布局目前不实现基线对齐，按 start 处理。</summary>
    Baseline,
}

/// <summary><c>justify-self</c>。<see cref="Auto"/> 取父容器的 <c>justify-items</c>。</summary>
public enum JustifySelf : byte
{
    Auto,
    Normal,
    Stretch,
    Start,
    End,
    Center,
    Left,
    Right,
    FlexStart,
    FlexEnd,
    SelfStart,
    SelfEnd,
    Baseline,
}
