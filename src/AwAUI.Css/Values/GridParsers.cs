using System;
using System.Collections.Generic;
using AwAUI.Css.Syntax;

namespace AwAUI.Css.Values;

/// <summary>
/// 网格相关属性与 <c>order</c> 的值解析（CSS Grid Layout 1 §7、§8）。所有开销对输入长度线性；<c>repeat()</c> 次数与线编号
/// 在计算值中钳制到 <see cref="MaxLine"/>（与布局引擎 <c>GridLine.MaxLine</c> 相同），展开工作在布局引擎中另有上限。
/// </summary>
internal static class GridParsers
{
    /// <summary>线编号与 <c>repeat()</c> 次数的上限。</summary>
    public const int MaxLine = 10000;

    // ================= order =================

    /// <summary><c>&lt;integer&gt;</c>（含结果为整数的 calc()）。</summary>
    public static CssValue? Integer(ref ValueReader r)
    {
        var v = r.Next();
        if (v is CssToken { Type: CssTokenType.Number, NumberType: CssNumberType.Number }) return null;
        var node = ValueParsers.NumberNode(v, nonNegative: false);
        return node is null ? null : new NumericValue(node);
    }

    // ================= grid-auto-flow =================

    /// <summary><c>[ row | column ] || dense</c>。</summary>
    public static CssValue? AutoFlow(ref ValueReader r)
    {
        bool? column = null;
        bool dense = false;
        int n = 0;
        while (!r.AtEnd && n < 2)
        {
            int save = r.Position;
            var id = r.PeekIdentLower();
            if (id == "row" && column is null) column = false;
            else if (id == "column" && column is null) column = true;
            else if (id == "dense" && !dense) dense = true;
            else
            {
                r.Position = save;
                break;
            }
            r.Next();
            n++;
        }
        if (n == 0) return null;
        int v = (column == true ? 1 : 0) + (dense ? 2 : 0);
        return KeywordValue.Of(v);
    }

    // ================= <grid-line> =================

    public static CssValue? Line(ref ValueReader r)
    {
        var line = ParseLine(ref r);
        return line is CssGridLine l ? (l.IsAuto ? GridLineSpecifiedValue.Auto : new GridLineSpecifiedValue(l)) : null;
    }

    /// <summary>
    /// <c>auto | &lt;custom-ident&gt; | [ &lt;integer&gt; &amp;&amp; &lt;custom-ident&gt;? ] | [ span &amp;&amp; [ &lt;integer [1,∞]&gt; || &lt;custom-ident&gt; ] ]</c>。
    /// 在 <c>/</c> 或末尾停止。
    /// </summary>
    internal static CssGridLine? ParseLine(ref ValueReader r)
    {
        if (r.TryIdent("auto")) return CssGridLine.Auto;
        // span 只能在开头或末尾：span && [ <integer> || <custom-ident> ] 中后一组是一个整体（「2 span」「foo 2 span」有效，
        // 「foo span 2」无效，与 Chromium 相同）
        bool span = false;
        int? integer = null;
        string? name = null;
        int count = 0;
        while (count < 3 && !r.AtEnd)
        {
            var v = r.Peek();
            if (v is CssToken { Type: CssTokenType.Ident } id)
            {
                if (!span && id.IsIdent("span"))
                {
                    // 开头的 span，或末尾的 span（前面已有整数或名字，之后不能再有）
                    if (count > 0)
                    {
                        r.Next();
                        span = true;
                        count++;
                        break;
                    }
                    span = true;
                    r.Next();
                    count++;
                    continue;
                }
                if (name is null && IsCustomIdent(id.Value)) { name = id.Value; r.Next(); count++; continue; }
                return null;
            }
            if (v is CssToken { Type: CssTokenType.Number, NumberType: CssNumberType.Integer } num && integer is null)
            {
                double d = Math.Max(-MaxLine, Math.Min(MaxLine, num.Number));
                integer = (int)d;
                r.Next();
                count++;
                continue;
            }
            break;
        }
        if (count == 0) return null;
        if (span)
        {
            if (integer is null && name is null) return null;
            if (integer is int k && k <= 0) return null;
            return new CssGridLine(CssGridLineKind.Span, integer ?? 1, name);
        }
        if (integer is int n)
        {
            if (n == 0) return null;
            return new CssGridLine(CssGridLineKind.Line, n, name);
        }
        return new CssGridLine(CssGridLineKind.Line, 0, name);
    }

    /// <summary><c>&lt;custom-ident&gt;</c>，排除全局关键字、<c>default</c>、<c>span</c>、<c>auto</c>。</summary>
    internal static bool IsCustomIdent(string value)
    {
        var lower = CssSerializer.ToAsciiLower(value);
        return lower is not ("span" or "auto" or "default") && !CssWideKeywordValue.TryParse(lower, out _);
    }

    // ================= 轨道列表 =================

    /// <summary><c>none | &lt;track-list&gt; | &lt;auto-track-list&gt;</c>（<c>grid-template-columns / rows</c>）。</summary>
    public static CssValue? TrackList(ref ValueReader r)
    {
        if (r.TryIdent("none")) return GridTrackListValue.None;
        var list = ParseTrackList(ref r, allowRepeat: true);
        return list is null ? null : new GridTrackListValue(list);
    }

    /// <summary><c>&lt;track-size&gt;+</c>（<c>grid-auto-columns / rows</c>）。</summary>
    public static CssValue? TrackSizes(ref ValueReader r)
    {
        var items = new List<GridComponentSpec>();
        while (!r.AtEnd)
        {
            int save = r.Position;
            var t = ParseTrackSize(r.Next());
            if (t is null)
            {
                r.Position = save;
                break;
            }
            items.Add(new GridComponentSpec { Kind = CssGridComponentKind.Track, Track = t.Value });
        }
        return items.Count == 0 ? null : new GridTrackListValue(items.ToArray());
    }

    /// <summary>
    /// 解析轨道列表（读到末尾或顶层的 <c>/</c> 为止）。<paramref name="allowRepeat"/> 为 false 时是 <c>&lt;explicit-track-list&gt;</c>。
    /// 校验：至少一条轨道；相邻的线名不能连写；至多一个 auto-repeat，且有 auto-repeat 时其余轨道必须是 <c>&lt;fixed-size&gt;</c>。
    /// </summary>
    internal static GridComponentSpec[]? ParseTrackList(ref ValueReader r, bool allowRepeat)
    {
        var items = new List<GridComponentSpec>();
        bool anyTrack = false, autoRepeat = false, allFixed = true;
        while (!r.AtEnd)
        {
            var v = r.Peek();
            if (v is CssToken t && t.IsDelim('/')) break;
            r.Next();
            if (v is CssSimpleBlock { Open: '[' } block)
            {
                if (items.Count > 0 && items[items.Count - 1].Kind == CssGridComponentKind.LineNames) return null;
                var names = ParseNames(block);
                if (names is null) return null;
                items.Add(new GridComponentSpec { Kind = CssGridComponentKind.LineNames, Names = names });
                continue;
            }
            if (v is CssFunction f && CssSerializer.EqualsIgnoreAsciiCase(f.Name, "repeat"))
            {
                if (!allowRepeat) return null;
                var rep = ParseRepeat(f, out bool isAuto, out bool fixedInner);
                if (rep is null) return null;
                if (isAuto)
                {
                    if (autoRepeat) return null;
                    autoRepeat = true;
                }
                allFixed &= fixedInner;
                items.Add(rep);
                anyTrack = true;
                continue;
            }
            var size = ParseTrackSize(v);
            if (size is null) return null;
            allFixed &= size.Value.IsFixedSize;
            items.Add(new GridComponentSpec { Kind = CssGridComponentKind.Track, Track = size.Value });
            anyTrack = true;
        }
        if (!anyTrack) return null;
        if (autoRepeat && !allFixed) return null;
        return items.ToArray();
    }

    static GridComponentSpec? ParseRepeat(CssFunction f, out bool isAuto, out bool allFixed)
    {
        isAuto = false;
        allFixed = true;
        var r = new ValueReader(f.Arguments);
        var kind = CssGridRepeatKind.Count;
        int count = 1;
        var first = r.Next();
        if (first is CssToken { Type: CssTokenType.Ident } id)
        {
            if (id.IsIdent("auto-fill")) kind = CssGridRepeatKind.AutoFill;
            else if (id.IsIdent("auto-fit")) kind = CssGridRepeatKind.AutoFit;
            else return null;
            isAuto = true;
        }
        else if (first is CssToken { Type: CssTokenType.Number, NumberType: CssNumberType.Integer } n && n.Number >= 1)
            count = (int)Math.Min(n.Number, MaxLine);
        else return null;
        if (!r.TryComma()) return null;
        var items = new List<GridComponentSpec>();
        bool anyTrack = false;
        while (!r.AtEnd)
        {
            var v = r.Next();
            if (v is CssSimpleBlock { Open: '[' } block)
            {
                if (items.Count > 0 && items[items.Count - 1].Kind == CssGridComponentKind.LineNames) return null;
                var names = ParseNames(block);
                if (names is null) return null;
                items.Add(new GridComponentSpec { Kind = CssGridComponentKind.LineNames, Names = names });
                continue;
            }
            var size = ParseTrackSize(v);
            if (size is null) return null;
            allFixed &= size.Value.IsFixedSize;
            items.Add(new GridComponentSpec { Kind = CssGridComponentKind.Track, Track = size.Value });
            anyTrack = true;
        }
        if (!anyTrack) return null;
        // auto-repeat 中只能是 <fixed-size>
        if (isAuto && !allFixed) return null;
        return new GridComponentSpec { Kind = CssGridComponentKind.Repeat, RepeatKind = kind, Count = count, Items = items.ToArray() };
    }

    /// <summary><c>'[' &lt;custom-ident&gt;* ']'</c>。</summary>
    internal static string[]? ParseNames(CssSimpleBlock block)
    {
        var list = new List<string>();
        foreach (var v in block.Values)
        {
            if (v is CssToken { Type: CssTokenType.Whitespace }) continue;
            if (v is CssToken { Type: CssTokenType.Ident } id && IsCustomIdent(id.Value)) list.Add(id.Value);
            else return null;
        }
        return list.ToArray();
    }

    /// <summary><c>&lt;track-size&gt;</c>：宽度、<c>minmax()</c> 或 <c>fit-content()</c>。</summary>
    internal static GridTrackSpec? ParseTrackSize(CssComponentValue? v)
    {
        if (v is CssFunction f)
        {
            if (CssSerializer.EqualsIgnoreAsciiCase(f.Name, "minmax"))
            {
                var r = new ValueReader(f.Arguments);
                var min = ParseBreadth(r.Next());
                if (min is null || min.Value.Kind == CssGridBreadthKind.Flex || !r.TryComma()) return null;
                var max = ParseBreadth(r.Next());
                if (max is null || !r.AtEnd) return null;
                return new GridTrackSpec(CssGridTrackForm.MinMax, min.Value, max.Value);
            }
            if (CssSerializer.EqualsIgnoreAsciiCase(f.Name, "fit-content"))
            {
                var r = new ValueReader(f.Arguments);
                var node = ValueParsers.LengthPercentageNode(r.Next(), nonNegative: true);
                if (node is null || !r.AtEnd) return null;
                var lim = new GridBreadthSpec(CssGridBreadthKind.Length, node, 0);
                return new GridTrackSpec(CssGridTrackForm.FitContent, default, lim);
            }
        }
        var b = ParseBreadth(v);
        return b is null ? null : new GridTrackSpec(CssGridTrackForm.Breadth, b.Value, b.Value);
    }

    /// <summary><c>&lt;track-breadth&gt;</c>。</summary>
    static GridBreadthSpec? ParseBreadth(CssComponentValue? v)
    {
        if (v is CssToken { Type: CssTokenType.Ident } id)
        {
            switch (CssSerializer.ToAsciiLower(id.Value))
            {
                case "auto": return new GridBreadthSpec(CssGridBreadthKind.Auto, null, 0);
                case "min-content": return new GridBreadthSpec(CssGridBreadthKind.MinContent, null, 0);
                case "max-content": return new GridBreadthSpec(CssGridBreadthKind.MaxContent, null, 0);
                default: return null;
            }
        }
        if (v is CssToken { Type: CssTokenType.Dimension } d && CssSerializer.EqualsIgnoreAsciiCase(d.Unit, "fr"))
        {
            if (!(d.Number >= 0) || double.IsInfinity(d.Number)) return null;
            return new GridBreadthSpec(CssGridBreadthKind.Flex, null, (float)Math.Min(d.Number, 1e9));
        }
        var node = ValueParsers.LengthPercentageNode(v, nonNegative: true);
        return node is null ? null : new GridBreadthSpec(CssGridBreadthKind.Length, node, 0);
    }

    // ================= grid-template-areas =================

    public static CssValue? Areas(ref ValueReader r)
    {
        if (r.TryIdent("none")) return GridAreasSpecifiedValue.None;
        var strings = new List<string>();
        while (!r.AtEnd && r.Peek() is CssToken { Type: CssTokenType.String } s)
        {
            r.Next();
            strings.Add(s.Value);
        }
        if (strings.Count == 0) return null;
        var areas = BuildAreas(strings);
        return areas is null ? null : new GridAreasSpecifiedValue(areas);
    }

    /// <summary>
    /// 按 §7.3.1 把字符串切成单元：名字码点序列为命名单元，<c>.</c> 序列为空单元，其他非空白字符使声明无效。
    /// 各行单元数必须相同；同名单元必须恰好填满其外接矩形。时间与输入长度线性。
    /// </summary>
    internal static CssGridTemplateAreas? BuildAreas(List<string> strings)
    {
        var rows = new string[strings.Count];
        int columns = -1;
        var bounds = new Dictionary<string, (int r0, int r1, int c0, int c1, long count)>(StringComparer.Ordinal);
        var order = new List<string>();
        var cells = new List<string>();
        for (int y = 0; y < strings.Count; y++)
        {
            cells.Clear();
            var s = strings[y];
            int i = 0;
            while (i < s.Length)
            {
                char ch = s[i];
                if (ch is ' ' or '\t' or '\n' or '\r' or '\f') { i++; continue; }
                int start = i;
                if (ch == '.')
                {
                    while (i < s.Length && s[i] == '.') i++;
                    cells.Add(".");
                    continue;
                }
                while (i < s.Length && IsNameCodePoint(s[i])) i++;
                if (i == start) return null;   // 垃圾记号
                cells.Add(s.Substring(start, i - start));
            }
            if (cells.Count == 0 || (columns >= 0 && cells.Count != columns)) return null;
            columns = cells.Count;
            for (int x = 0; x < cells.Count; x++)
            {
                var n = cells[x];
                if (n == ".") continue;
                if (bounds.TryGetValue(n, out var b))
                    bounds[n] = (Math.Min(b.r0, y), Math.Max(b.r1, y + 1), Math.Min(b.c0, x), Math.Max(b.c1, x + 1), b.count + 1);
                else
                {
                    bounds[n] = (y, y + 1, x, x + 1, 1);
                    order.Add(n);
                }
            }
            rows[y] = string.Join(" ", cells);
        }
        var areas = new CssGridArea[order.Count];
        for (int k = 0; k < order.Count; k++)
        {
            var b = bounds[order[k]];
            if ((long)(b.r1 - b.r0) * (b.c1 - b.c0) != b.count) return null;   // 不是填满的矩形
            areas[k] = new CssGridArea(order[k], b.r0, b.r1, b.c0, b.c1);
        }
        return new CssGridTemplateAreas(rows, columns, areas);
    }

    static bool IsNameCodePoint(char c) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' || c >= 0x80;

    // ================= 简写的辅助 =================

    /// <summary>按顶层的 <c>/</c> 切分组件值（去掉 / 本身）。</summary>
    internal static List<List<CssComponentValue>> SplitSlash(IReadOnlyList<CssComponentValue> value)
    {
        var parts = new List<List<CssComponentValue>> { new() };
        foreach (var v in value)
        {
            if (v is CssToken t && t.IsDelim('/')) parts.Add(new List<CssComponentValue>());
            else parts[parts.Count - 1].Add(v);
        }
        return parts;
    }

    internal static CssGridLine? LineOf(List<CssComponentValue> part)
    {
        var r = new ValueReader(part);
        var l = ParseLine(ref r);
        return l is not null && r.AtEnd ? l : null;
    }
}

internal static partial class Shorthands
{
    static CssValue LineValue(CssGridLine l) => l.IsAuto ? GridLineSpecifiedValue.Auto : new GridLineSpecifiedValue(l);

    /// <summary>省略的另一侧：前一侧只有 <c>&lt;custom-ident&gt;</c> 时取同名，否则 auto（§8.4）。</summary>
    static CssGridLine Fallback(CssGridLine other) => other.IsIdentOnly ? other : CssGridLine.Auto;

    /// <summary><c>grid-row / grid-column: &lt;grid-line&gt; [ / &lt;grid-line&gt; ]?</c>。</summary>
    public static bool GridLinePair(IReadOnlyList<CssComponentValue> value, List<PropertyValue> output, CssPropertyId[] longhands)
    {
        var parts = GridParsers.SplitSlash(value);
        if (parts.Count > 2) return false;
        var a = GridParsers.LineOf(parts[0]);
        if (a is null) return false;
        var b = parts.Count > 1 ? GridParsers.LineOf(parts[1]) : Fallback(a.Value);
        if (b is null) return false;
        output.Add(new PropertyValue(longhands[0], LineValue(a.Value)));
        output.Add(new PropertyValue(longhands[1], LineValue(b.Value)));
        return true;
    }

    /// <summary><c>grid-area: &lt;grid-line&gt; [ / &lt;grid-line&gt; ]{0,3}</c>（行起、列起、行止、列止）。</summary>
    public static bool GridArea(IReadOnlyList<CssComponentValue> value, List<PropertyValue> output, CssPropertyId[] longhands)
    {
        var parts = GridParsers.SplitSlash(value);
        if (parts.Count > 4) return false;
        var lines = new CssGridLine[4];
        for (int i = 0; i < parts.Count; i++)
        {
            var l = GridParsers.LineOf(parts[i]);
            if (l is null) return false;
            lines[i] = l.Value;
        }
        if (parts.Count < 2) lines[1] = Fallback(lines[0]);
        if (parts.Count < 3) lines[2] = Fallback(lines[0]);
        if (parts.Count < 4) lines[3] = Fallback(lines[1]);
        for (int i = 0; i < 4; i++) output.Add(new PropertyValue(longhands[i], LineValue(lines[i])));
        return true;
    }

    /// <summary>
    /// <c>grid-template: none | [ &lt;'grid-template-rows'&gt; / &lt;'grid-template-columns'&gt; ]
    /// | [ &lt;line-names&gt;? &lt;string&gt; &lt;track-size&gt;? &lt;line-names&gt;? ]+ [ / &lt;explicit-track-list&gt; ]?</c>。
    /// 长属性顺序：rows、columns、areas。
    /// </summary>
    public static bool GridTemplate(IReadOnlyList<CssComponentValue> value, List<PropertyValue> output, CssPropertyId[] longhands)
    {
        if (!ParseTemplate(value, out var rows, out var cols, out var areas)) return false;
        output.Add(new PropertyValue(longhands[0], rows));
        output.Add(new PropertyValue(longhands[1], cols));
        output.Add(new PropertyValue(longhands[2], areas));
        return true;
    }

    static bool ParseTemplate(IReadOnlyList<CssComponentValue> value, out CssValue rows, out CssValue cols, out CssValue areas)
    {
        rows = cols = GridTrackListValue.None;
        areas = GridAreasSpecifiedValue.None;
        var r = new ValueReader(value);
        if (r.TryIdent("none")) return r.AtEnd;
        r.Position = 0;
        bool hasString = false;
        foreach (var v in value)
        {
            if (v is CssToken t && t.IsDelim('/')) break;
            if (v is CssToken { Type: CssTokenType.String }) hasString = true;
        }
        var parts = GridParsers.SplitSlash(value);
        if (!hasString)
        {
            if (parts.Count != 2) return false;
            var rr = new ValueReader(parts[0]);
            var rv = GridParsers.TrackList(ref rr);
            var cr = new ValueReader(parts[1]);
            var cv = GridParsers.TrackList(ref cr);
            if (rv is null || cv is null || !rr.AtEnd || !cr.AtEnd) return false;
            rows = rv;
            cols = cv;
            return true;
        }
        if (parts.Count > 2) return false;
        // 区域形式：每行 [names]? "string" <track-size>? [names]?，相邻两行之间的线名合并到同一条线
        var items = new List<GridComponentSpec>();
        var strings = new List<string>();
        var pr = new ValueReader(parts[0]);
        int blocks = 0;   // 连续的线名组数：第一行之前与最后一行之后至多 1 组，两行之间至多 2 组（合并到同一条线）
        while (!pr.AtEnd)
        {
            var v = pr.Next();
            if (v is CssSimpleBlock { Open: '[' } block)
            {
                var names = GridParsers.ParseNames(block);
                if (names is null) return false;
                if (++blocks > (strings.Count == 0 ? 1 : 2)) return false;
                if (blocks == 2)
                {
                    var prev = items[items.Count - 1];
                    var merged = new string[prev.Names!.Length + names.Length];
                    prev.Names.CopyTo(merged, 0);
                    names.CopyTo(merged, prev.Names.Length);
                    prev.Names = merged;
                }
                else items.Add(new GridComponentSpec { Kind = CssGridComponentKind.LineNames, Names = names });
                continue;
            }
            if (v is not CssToken { Type: CssTokenType.String } s) return false;
            strings.Add(s.Value);
            blocks = 0;
            int save = pr.Position;
            var next = pr.Next();
            var size = next is CssToken { Type: CssTokenType.String } || next is CssSimpleBlock ? null : GridParsers.ParseTrackSize(next);
            if (size is null) pr.Position = save;
            items.Add(new GridComponentSpec { Kind = CssGridComponentKind.Track, Track = size ?? default });
        }
        if (strings.Count == 0 || blocks > 1) return false;
        var built = GridParsers.BuildAreas(strings);
        if (built is null) return false;
        areas = new GridAreasSpecifiedValue(built);
        rows = new GridTrackListValue(items.ToArray());
        if (parts.Count == 2)
        {
            var cr = new ValueReader(parts[1]);
            if (cr.TryIdent("none")) return false;
            var list = GridParsers.ParseTrackList(ref cr, allowRepeat: false);
            if (list is null || !cr.AtEnd) return false;
            cols = new GridTrackListValue(list);
        }
        return true;
    }

    /// <summary>
    /// <c>grid: &lt;'grid-template'&gt; | &lt;'grid-template-rows'&gt; / [ auto-flow &amp;&amp; dense? ] &lt;'grid-auto-columns'&gt;?
    /// | [ auto-flow &amp;&amp; dense? ] &lt;'grid-auto-rows'&gt;? / &lt;'grid-template-columns'&gt;</c>。
    /// 长属性顺序：template-rows、template-columns、template-areas、auto-rows、auto-columns、auto-flow；未给出的重置为初始值。
    /// </summary>
    public static bool Grid(IReadOnlyList<CssComponentValue> value, List<PropertyValue> output, CssPropertyId[] longhands)
    {
        var parts = GridParsers.SplitSlash(value);
        int flowSide = -1;
        if (parts.Count == 2)
        {
            for (int side = 0; side < 2 && flowSide < 0; side++)
                foreach (var v in parts[side])
                    if (v is CssToken t && t.IsIdent("auto-flow")) { flowSide = side; break; }
        }
        CssValue tRows = GridTrackListValue.None, tCols = GridTrackListValue.None, tAreas = GridAreasSpecifiedValue.None;
        CssValue aRows = CssWideKeywordValue.Initial, aCols = CssWideKeywordValue.Initial, flow = CssWideKeywordValue.Initial;
        if (flowSide < 0)
        {
            if (!ParseTemplate(value, out tRows, out tCols, out tAreas)) return false;
        }
        else
        {
            var r = new ValueReader(parts[flowSide]);
            bool dense = false, autoFlow = false;
            while (!r.AtEnd)
            {
                if (!autoFlow && r.TryIdent("auto-flow")) { autoFlow = true; continue; }
                if (!dense && r.TryIdent("dense")) { dense = true; continue; }
                break;
            }
            if (!autoFlow) return false;
            CssValue? autoSizes = null;
            if (!r.AtEnd)
            {
                autoSizes = GridParsers.TrackSizes(ref r);
                if (autoSizes is null || !r.AtEnd) return false;
            }
            var other = new ValueReader(parts[1 - flowSide]);
            var tl = GridParsers.TrackList(ref other);
            if (tl is null || !other.AtEnd) return false;
            bool column = flowSide == 1;
            flow = KeywordValue.Of((column ? 1 : 0) + (dense ? 2 : 0));
            if (column)
            {
                tRows = tl;
                if (autoSizes is not null) aCols = autoSizes;
            }
            else
            {
                tCols = tl;
                if (autoSizes is not null) aRows = autoSizes;
            }
        }
        output.Add(new PropertyValue(longhands[0], tRows));
        output.Add(new PropertyValue(longhands[1], tCols));
        output.Add(new PropertyValue(longhands[2], tAreas));
        output.Add(new PropertyValue(longhands[3], aRows));
        output.Add(new PropertyValue(longhands[4], aCols));
        output.Add(new PropertyValue(longhands[5], flow));
        return true;
    }

    /// <summary>
    /// <c>place-items / place-self / place-content: &lt;align&gt; &lt;justify&gt;?</c>：省略第二个值时用同一个关键字解析 justify 一侧
    /// （两侧的关键字枚举不同，所以按各自的长属性分别解析）。
    /// </summary>
    public static bool PlacePair(IReadOnlyList<CssComponentValue> value, List<PropertyValue> output, CssPropertyId[] longhands)
    {
        var r = new ValueReader(value);
        int start = r.Position;
        var a = Parse(longhands[0], ref r);
        if (a is null) return false;
        CssValue? b;
        if (r.AtEnd)
        {
            r.Position = start;
            b = Parse(longhands[1], ref r);
        }
        else
        {
            b = Parse(longhands[1], ref r);
            if (!r.AtEnd) return false;
        }
        if (b is null) return false;
        output.Add(new PropertyValue(longhands[0], a));
        output.Add(new PropertyValue(longhands[1], b));
        return true;
    }

    /// <summary>
    /// 逻辑属性（<c>margin-inline-start</c> 等）：按横排、从左到右的书写模式在解析时映射为对应的物理长属性
    /// （AwAUI 尚不支持 <c>direction</c> / <c>writing-mode</c>，所以映射是固定的）。值按该长属性的语法解析。
    /// </summary>
    public static bool Alias(IReadOnlyList<CssComponentValue> value, List<PropertyValue> output, CssPropertyId[] longhands)
    {
        var v = PropertyParsers.ParseLonghand(longhands[0], value);
        if (v is null) return false;
        output.Add(new PropertyValue(longhands[0], v));
        return true;
    }

    /// <summary><c>border-inline / border-block</c>：两侧相同的 <c>&lt;line-width&gt; || &lt;line-style&gt; || &lt;color&gt;</c>（长属性：起始侧 3 个、结束侧 3 个）。</summary>
    public static bool BorderSidePair(IReadOnlyList<CssComponentValue> value, List<PropertyValue> output, CssPropertyId[] longhands)
    {
        if (!LineParts(value, longhands[0], longhands[1], longhands[2], out var w, out var s, out var c)) return false;
        for (int side = 0; side < 2; side++)
        {
            output.Add(new PropertyValue(longhands[side * 3], w));
            output.Add(new PropertyValue(longhands[side * 3 + 1], s));
            output.Add(new PropertyValue(longhands[side * 3 + 2], c));
        }
        return true;
    }
}
