using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AwAUI.Css.Syntax;

namespace AwAUI.Css.Values;

// ================= 计算值（公开） =================

/// <summary><c>&lt;track-breadth&gt;</c> 的种类。</summary>
public enum CssGridBreadthKind : byte
{
    Auto,
    /// <summary><c>&lt;length-percentage [0,∞]&gt;</c>。</summary>
    Length,
    /// <summary><c>&lt;flex&gt;</c>（fr）。</summary>
    Flex,
    MinContent,
    MaxContent,
}

/// <summary>轨道尺寸函数中的一个宽度（计算值：长度已换算为 px，百分比与 calc 保留到布局）。</summary>
public readonly struct CssGridBreadth : IEquatable<CssGridBreadth>
{
    internal CssGridBreadth(CssGridBreadthKind kind, LengthPercentage length, float flex)
    {
        Kind = kind;
        Length = length;
        Flex = flex;
    }

    public CssGridBreadthKind Kind { get; }

    public LengthPercentage Length { get; }

    /// <summary>fr 的伸缩因子。</summary>
    public float Flex { get; }

    public static CssGridBreadth Auto => default;

    public bool Equals(CssGridBreadth o) => Kind == o.Kind && Length.Equals(o.Length) && Flex.Equals(o.Flex);
    public override bool Equals(object? obj) => obj is CssGridBreadth b && Equals(b);
    public override int GetHashCode() => HashCodeCompat.Combine((int)Kind, Length.GetHashCode(), Flex.GetHashCode());

    public override string ToString() => Kind switch
    {
        CssGridBreadthKind.Length => Length.ToString(),
        CssGridBreadthKind.Flex => ValueText.Number(Flex) + "fr",
        CssGridBreadthKind.MinContent => "min-content",
        CssGridBreadthKind.MaxContent => "max-content",
        _ => "auto",
    };
}

/// <summary>轨道尺寸函数的写法。</summary>
public enum CssGridTrackForm : byte
{
    /// <summary>单个宽度（<see cref="CssGridTrackSize.Min"/> 与 <see cref="CssGridTrackSize.Max"/> 相同）。</summary>
    Breadth,
    /// <summary><c>minmax(min, max)</c>。</summary>
    MinMax,
    /// <summary><c>fit-content(limit)</c>：上限在 <see cref="CssGridTrackSize.Max"/> 中。</summary>
    FitContent,
}

/// <summary><c>&lt;track-size&gt;</c> 的计算值。</summary>
public readonly struct CssGridTrackSize : IEquatable<CssGridTrackSize>
{
    internal CssGridTrackSize(CssGridTrackForm form, CssGridBreadth min, CssGridBreadth max)
    {
        Form = form;
        Min = min;
        Max = max;
    }

    public CssGridTrackForm Form { get; }
    public CssGridBreadth Min { get; }
    public CssGridBreadth Max { get; }

    public static CssGridTrackSize Auto => default;

    public bool Equals(CssGridTrackSize o) => Form == o.Form && Min.Equals(o.Min) && Max.Equals(o.Max);
    public override bool Equals(object? obj) => obj is CssGridTrackSize t && Equals(t);
    public override int GetHashCode() => HashCodeCompat.Combine((int)Form, Min.GetHashCode(), Max.GetHashCode());

    public override string ToString() => Form switch
    {
        CssGridTrackForm.MinMax => "minmax(" + Min + ", " + Max + ")",
        CssGridTrackForm.FitContent => "fit-content(" + Max.Length + ")",
        _ => Min.ToString(),
    };
}

/// <summary>轨道列表组成部分的种类。</summary>
public enum CssGridComponentKind : byte
{
    /// <summary><c>[name ...]</c>。</summary>
    LineNames,
    Track,
    /// <summary><c>repeat()</c>（只出现在顶层）。</summary>
    Repeat,
}

/// <summary><c>repeat()</c> 的重复方式。</summary>
public enum CssGridRepeatKind : byte
{
    Count,
    AutoFill,
    AutoFit,
}

/// <summary>轨道列表的一个组成部分：线名、轨道或 <c>repeat()</c>。</summary>
public readonly struct CssGridTrackComponent : IEquatable<CssGridTrackComponent>
{
    static readonly string[] s_none = Array.Empty<string>();

    internal CssGridTrackComponent(CssGridComponentKind kind, string[]? names, CssGridTrackSize track, CssGridRepeat? repeat)
    {
        Kind = kind;
        Names = names ?? s_none;
        Track = track;
        Repeat = repeat;
    }

    public CssGridComponentKind Kind { get; }

    /// <summary><see cref="CssGridComponentKind.LineNames"/> 的线名（区分大小写）。</summary>
    public IReadOnlyList<string> Names { get; }

    public CssGridTrackSize Track { get; }

    public CssGridRepeat? Repeat { get; }

    public bool Equals(CssGridTrackComponent o)
    {
        if (Kind != o.Kind || !Track.Equals(o.Track) || !Equals(Repeat, o.Repeat) || Names.Count != o.Names.Count) return false;
        for (int i = 0; i < Names.Count; i++)
            if (!string.Equals(Names[i], o.Names[i], StringComparison.Ordinal)) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is CssGridTrackComponent c && Equals(c);
    public override int GetHashCode() => HashCodeCompat.Combine((int)Kind, Track.GetHashCode(), Names.Count);

    internal void Write(StringBuilder sb)
    {
        switch (Kind)
        {
            case CssGridComponentKind.LineNames:
                sb.Append('[').Append(string.Join(" ", (string[])Names)).Append(']');
                break;
            case CssGridComponentKind.Track:
                sb.Append(Track.ToString());
                break;
            default:
                sb.Append(Repeat!.ToString());
                break;
        }
    }
}

/// <summary><c>repeat()</c> 的计算值。次数已钳制到 [1, 10000]（与布局引擎的网格线上限一致）。</summary>
public sealed class CssGridRepeat : IEquatable<CssGridRepeat>
{
    internal CssGridRepeat(CssGridRepeatKind kind, int count, CssGridTrackComponent[] components)
    {
        Kind = kind;
        Count = count;
        Components = components;
    }

    public CssGridRepeatKind Kind { get; }

    /// <summary><see cref="CssGridRepeatKind.Count"/> 的次数。</summary>
    public int Count { get; }

    /// <summary>被重复的线名与轨道。</summary>
    public IReadOnlyList<CssGridTrackComponent> Components { get; }

    public bool Equals(CssGridRepeat? o) =>
        o is not null && (ReferenceEquals(this, o) || (Kind == o.Kind && Count == o.Count && GridText.SequenceEqual(Components, o.Components)));

    public override bool Equals(object? obj) => obj is CssGridRepeat r && Equals(r);
    public override int GetHashCode() => HashCodeCompat.Combine((int)Kind, Count, Components.Count);

    public override string ToString()
    {
        var sb = new StringBuilder("repeat(");
        sb.Append(Kind switch
        {
            CssGridRepeatKind.AutoFill => "auto-fill",
            CssGridRepeatKind.AutoFit => "auto-fit",
            _ => Count.ToString(CultureInfo.InvariantCulture),
        }).Append(", ");
        GridText.Write(sb, Components);
        return sb.Append(')').ToString();
    }
}

/// <summary>
/// <c>grid-template-columns / rows</c>（<c>none | &lt;track-list&gt; | &lt;auto-track-list&gt;</c>）与 <c>grid-auto-columns / rows</c>
/// （<c>&lt;track-size&gt;+</c>，只含轨道）的计算值。按内容比较相等。
/// </summary>
public sealed class CssGridTrackList : IEquatable<CssGridTrackList>
{
    /// <summary><c>none</c>。</summary>
    public static readonly CssGridTrackList None = new(Array.Empty<CssGridTrackComponent>());

    /// <summary><c>auto</c>（<c>grid-auto-*</c> 的初始值）。</summary>
    public static readonly CssGridTrackList Auto = new(new[] { new CssGridTrackComponent(CssGridComponentKind.Track, null, CssGridTrackSize.Auto, null) });

    internal CssGridTrackList(CssGridTrackComponent[] components) => Components = components;

    public bool IsNone => Components.Count == 0;

    public IReadOnlyList<CssGridTrackComponent> Components { get; }

    public bool Equals(CssGridTrackList? o) => o is not null && (ReferenceEquals(this, o) || GridText.SequenceEqual(Components, o.Components));
    public override bool Equals(object? obj) => obj is CssGridTrackList l && Equals(l);
    public override int GetHashCode() => Components.Count == 0 ? 0 : HashCodeCompat.Combine(Components.Count, Components[0].GetHashCode());

    public override string ToString()
    {
        if (IsNone) return "none";
        var sb = new StringBuilder();
        GridText.Write(sb, Components);
        return sb.ToString();
    }
}

/// <summary><c>&lt;grid-line&gt;</c> 的种类。</summary>
public enum CssGridLineKind : byte
{
    Auto,
    /// <summary><c>&lt;integer&gt; &lt;custom-ident&gt;?</c> 或只有 <c>&lt;custom-ident&gt;</c>（<see cref="CssGridLine.Integer"/> 为 0）。</summary>
    Line,
    Span,
}

/// <summary><c>grid-row-start</c> 等的计算值。整数钳制到 ±10000（与布局引擎的网格线上限一致）。</summary>
public readonly struct CssGridLine : IEquatable<CssGridLine>
{
    internal CssGridLine(CssGridLineKind kind, int integer, string? name)
    {
        Kind = kind;
        Integer = integer;
        Name = name;
    }

    public static CssGridLine Auto => default;

    public CssGridLineKind Kind { get; }

    /// <summary>线编号（非零；只有线名时为 0）或 span 的跨度（≥ 1）。</summary>
    public int Integer { get; }

    public string? Name { get; }

    public bool IsAuto => Kind == CssGridLineKind.Auto;

    /// <summary>是否只有一个 <c>&lt;custom-ident&gt;</c>（简写补全省略的另一侧时使用）。</summary>
    public bool IsIdentOnly => Kind == CssGridLineKind.Line && Integer == 0;

    public bool Equals(CssGridLine o) => Kind == o.Kind && Integer == o.Integer && string.Equals(Name, o.Name, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is CssGridLine l && Equals(l);
    public override int GetHashCode() => HashCodeCompat.Combine((int)Kind, Integer, Name is null ? 0 : StringComparer.Ordinal.GetHashCode(Name));
    public static bool operator ==(CssGridLine a, CssGridLine b) => a.Equals(b);
    public static bool operator !=(CssGridLine a, CssGridLine b) => !a.Equals(b);

    public override string ToString()
    {
        string n = Integer.ToString(CultureInfo.InvariantCulture);
        return Kind switch
        {
            CssGridLineKind.Span => Name is null ? "span " + n : Integer == 1 ? "span " + Name : "span " + n + " " + Name,
            CssGridLineKind.Line => Integer == 0 ? Name! : Name is null ? n : n + " " + Name,
            _ => "auto",
        };
    }
}

/// <summary><c>grid-template-areas</c> 中的一个命名区域（0 起的线下标，止线不含）。</summary>
public readonly struct CssGridArea : IEquatable<CssGridArea>
{
    internal CssGridArea(string name, int rowStart, int rowEnd, int columnStart, int columnEnd)
    {
        Name = name;
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

    public bool Equals(CssGridArea o) => string.Equals(Name, o.Name, StringComparison.Ordinal) && RowStart == o.RowStart && RowEnd == o.RowEnd
                                         && ColumnStart == o.ColumnStart && ColumnEnd == o.ColumnEnd;
    public override bool Equals(object? obj) => obj is CssGridArea a && Equals(a);
    public override int GetHashCode() => HashCodeCompat.Combine(StringComparer.Ordinal.GetHashCode(Name), RowStart, ColumnStart);
}

/// <summary>
/// <c>grid-template-areas</c> 的计算值：规范化的行字符串（单元以一个空格分隔）与命名区域。按内容比较相等。
/// </summary>
public sealed class CssGridTemplateAreas : IEquatable<CssGridTemplateAreas>
{
    public static readonly CssGridTemplateAreas None = new(Array.Empty<string>(), 0, Array.Empty<CssGridArea>());

    internal CssGridTemplateAreas(string[] rows, int columns, CssGridArea[] areas)
    {
        RowStrings = rows;
        Columns = columns;
        Areas = areas;
    }

    public bool IsNone => RowStrings.Count == 0;

    /// <summary>各行（单元以一个空格分隔，空单元为 <c>.</c>）。</summary>
    public IReadOnlyList<string> RowStrings { get; }

    public int Rows => RowStrings.Count;

    public int Columns { get; }

    public IReadOnlyList<CssGridArea> Areas { get; }

    public bool Equals(CssGridTemplateAreas? o)
    {
        if (o is null) return false;
        if (ReferenceEquals(this, o)) return true;
        if (Columns != o.Columns || RowStrings.Count != o.RowStrings.Count) return false;
        for (int i = 0; i < RowStrings.Count; i++)
            if (!string.Equals(RowStrings[i], o.RowStrings[i], StringComparison.Ordinal)) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is CssGridTemplateAreas a && Equals(a);
    public override int GetHashCode() => HashCodeCompat.Combine(Columns, RowStrings.Count);

    public override string ToString()
    {
        if (IsNone) return "none";
        var sb = new StringBuilder();
        foreach (var r in RowStrings)
        {
            if (sb.Length > 0) sb.Append(' ');
            CssSerializer.WriteString(sb, r);
        }
        return sb.ToString();
    }
}

/// <summary><c>grid-auto-flow</c>。</summary>
public enum CssGridAutoFlow : byte
{
    Row,
    Column,
    RowDense,
    ColumnDense,
}

static class GridText
{
    public static bool SequenceEqual(IReadOnlyList<CssGridTrackComponent> a, IReadOnlyList<CssGridTrackComponent> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!a[i].Equals(b[i])) return false;
        return true;
    }

    public static void Write(StringBuilder sb, IReadOnlyList<CssGridTrackComponent> list)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            list[i].Write(sb);
        }
    }

    /// <summary><c>grid-auto-flow</c> 的文本（与 Chrome 一致：<c>row</c>、<c>column</c>、<c>dense</c>、<c>column dense</c>）。</summary>
    public static string AutoFlow(CssGridAutoFlow f) => f switch
    {
        CssGridAutoFlow.Column => "column",
        CssGridAutoFlow.RowDense => "dense",
        CssGridAutoFlow.ColumnDense => "column dense",
        _ => "row",
    };
}

// ================= 指定值（内部） =================

internal readonly struct GridBreadthSpec
{
    public GridBreadthSpec(CssGridBreadthKind kind, CalcNode? node, float flex)
    {
        Kind = kind;
        Node = node;
        Flex = flex;
    }

    public CssGridBreadthKind Kind { get; }
    public CalcNode? Node { get; }
    public float Flex { get; }

    /// <summary>是否为 <c>&lt;fixed-breadth&gt;</c>（长度百分比）。</summary>
    public bool IsFixed => Kind == CssGridBreadthKind.Length;

    public CssGridBreadth Compute(in LengthContext ctx) =>
        new(Kind, Node is null ? default : Node.Compute(ctx).ToLengthPercentage(), Flex);
}

internal readonly struct GridTrackSpec
{
    public GridTrackSpec(CssGridTrackForm form, GridBreadthSpec min, GridBreadthSpec max)
    {
        Form = form;
        Min = min;
        Max = max;
    }

    public CssGridTrackForm Form { get; }
    public GridBreadthSpec Min { get; }
    public GridBreadthSpec Max { get; }

    /// <summary><c>&lt;fixed-size&gt;</c>：auto-repeat 中及与之同列表的轨道必须是这种。</summary>
    public bool IsFixedSize => Form switch
    {
        CssGridTrackForm.Breadth => Min.IsFixed,
        CssGridTrackForm.MinMax => Min.IsFixed || (Max.IsFixed && Min.Kind != CssGridBreadthKind.Flex),
        _ => false,
    };

    public CssGridTrackSize Compute(in LengthContext ctx) => new(Form, Min.Compute(ctx), Max.Compute(ctx));
}

internal sealed class GridComponentSpec
{
    public CssGridComponentKind Kind;
    public string[]? Names;
    public GridTrackSpec Track;
    public CssGridRepeatKind RepeatKind;
    public int Count;
    public GridComponentSpec[]? Items;

    public bool IsContextDependent(out bool viewport)
    {
        viewport = false;
        bool dep = false;
        if (Kind == CssGridComponentKind.Track) Check(Track, ref dep, ref viewport);
        else if (Items is not null)
            foreach (var i in Items)
            {
                if (i.IsContextDependent(out bool v)) dep = true;
                viewport |= v;
            }
        return dep;
    }

    static void Check(GridTrackSpec t, ref bool dep, ref bool viewport)
    {
        foreach (var n in new[] { t.Min.Node, t.Max.Node })
        {
            if (n is null) continue;
            dep |= n.IsContextDependent;
            viewport |= n.UsesViewport;
        }
    }

    public CssGridTrackComponent Compute(in LengthContext ctx) => Kind switch
    {
        CssGridComponentKind.LineNames => new CssGridTrackComponent(Kind, Names, default, null),
        CssGridComponentKind.Track => new CssGridTrackComponent(Kind, null, Track.Compute(ctx), null),
        _ => new CssGridTrackComponent(Kind, null, default, new CssGridRepeat(RepeatKind, Count, ComputeAll(Items!, ctx))),
    };

    public static CssGridTrackComponent[] ComputeAll(GridComponentSpec[] items, in LengthContext ctx)
    {
        var a = new CssGridTrackComponent[items.Length];
        for (int i = 0; i < a.Length; i++) a[i] = items[i].Compute(ctx);
        return a;
    }
}

/// <summary>轨道列表的指定值；不依赖上下文（em、视口单位）时预先算好计算值。</summary>
internal sealed class GridTrackListValue : CssValue
{
    // 必须写在下面的静态实例之前（静态字段按文本顺序初始化）
    static readonly CssEnvironment s_env = new();

    public static readonly GridTrackListValue None = new(Array.Empty<GridComponentSpec>());
    public static readonly GridTrackListValue AutoTracks = new(new[]
    {
        new GridComponentSpec { Kind = CssGridComponentKind.Track, Track = default },
    });

    public GridTrackListValue(GridComponentSpec[] components)
    {
        Components = components;
        bool dep = false, vp = false;
        foreach (var c in components)
        {
            if (c.IsContextDependent(out bool v)) dep = true;
            vp |= v;
        }
        UsesViewport = vp;
        if (!dep) Computed = Compute(new LengthContext(16, 16, s_env));
    }

    public GridComponentSpec[] Components { get; }

    public CssGridTrackList? Computed { get; }

    public bool UsesViewport { get; }

    public CssGridTrackList Compute(in LengthContext ctx) =>
        Components.Length == 0 ? CssGridTrackList.None : new CssGridTrackList(GridComponentSpec.ComputeAll(Components, ctx));
}

internal sealed class GridLineSpecifiedValue : CssValue
{
    public static readonly GridLineSpecifiedValue Auto = new(CssGridLine.Auto);

    public GridLineSpecifiedValue(CssGridLine value) => Value = value;

    public CssGridLine Value { get; }
}

internal sealed class GridAreasSpecifiedValue : CssValue
{
    public static readonly GridAreasSpecifiedValue None = new(CssGridTemplateAreas.None);

    public GridAreasSpecifiedValue(CssGridTemplateAreas value) => Value = value;

    public CssGridTemplateAreas Value { get; }
}
