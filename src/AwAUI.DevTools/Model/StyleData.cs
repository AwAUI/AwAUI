using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AwAUI.Css.Values;
using AwAUI.Diagnostics.Json;

namespace AwAUI.DevTools;

/// <summary>一条声明（design/20 <c>Declaration</c>）。</summary>
internal sealed class StyleDecl
{
    public string Name = "";
    public string Value = "";
    public bool Important;
    /// <summary>active / overridden / invalid / unsupported / inactive。</summary>
    public string Status = "active";
    public bool Disabled;
    public string? Reason;

    public bool IsActive => Status == "active" && !Disabled;

    /// <summary>写回声明块时的文本（被注释掉的声明写成注释，与 Agent 的约定一致）。</summary>
    public void AppendTo(StringBuilder sb)
    {
        if (Disabled) sb.Append("/* ");
        sb.Append(Name).Append(": ").Append(Value);
        if (Important) sb.Append(" !important");
        sb.Append(';');
        if (Disabled) sb.Append(" */");
    }
}

/// <summary>样式面板中的一块（<c>element.style</c> 或一条匹配的规则）。</summary>
internal sealed class StyleBlock
{
    /// <summary>规则 id；内联样式（<c>element.style</c>）为 null。</summary>
    public string? RuleId;
    /// <summary>内联样式所属的节点（<see cref="RuleId"/> 为 null 时）。</summary>
    public long NodeId;
    public string Selector = "";
    public string? MatchedSelector;
    public string Origin = "author";
    public string? Source;
    public string? Media;
    public readonly List<StyleDecl> Decls = new();
    /// <summary>来自祖先（继承）。只显示可继承的声明，其余淡化。</summary>
    public bool Inherited;

    public bool IsInline => RuleId is null;

    /// <summary>默认样式表（相当于浏览器的 user agent stylesheet）不可编辑。</summary>
    public bool Editable => Origin != "default";

    /// <summary>编辑后的完整声明块文本：<paramref name="index"/> 处的声明换成 <paramref name="replacement"/>（null 表示删除），index = -1 表示追加。</summary>
    public string BuildText(int index, StyleDecl? replacement)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Decls.Count; i++)
        {
            var d = i == index ? replacement : Decls[i];
            if (d is null) continue;
            if (sb.Length > 0) sb.Append(' ');
            d.AppendTo(sb);
        }
        if (index < 0 && replacement is not null)
        {
            if (sb.Length > 0) sb.Append(' ');
            replacement.AppendTo(sb);
        }
        // Agent 拒绝以 '/' 结尾的声明文本（防止与后面的 '*' 拼成注释），完整的注释也不例外：补一个空声明
        if (sb.Length >= 2 && sb[sb.Length - 1] == '/' && sb[sb.Length - 2] == '*') sb.Append(';');
        return sb.ToString();
    }
}

/// <summary>样式面板的一个分区：本元素的规则，或「继承自 …」。</summary>
internal sealed class StyleSection
{
    public string? Title;
    public long FromNodeId;
    public readonly List<StyleBlock> Blocks = new();
}

/// <summary><c>Css.getMatchedRules</c> 的结果。</summary>
internal sealed class StylesSnapshot
{
    public long NodeId;
    public readonly List<StyleSection> Sections = new();

    public static StylesSnapshot Parse(long nodeId, JsonObject result, Func<string, string?> sheetName, Func<long, string> describe)
    {
        var s = new StylesSnapshot { NodeId = nodeId };
        var own = new StyleSection();
        var inline = new StyleBlock { RuleId = null, NodeId = nodeId, Selector = "element.style", Origin = "inline" };
        ParseDecls(inline.Decls, result["inline"]);
        own.Blocks.Add(inline);
        ParseMatches(own.Blocks, result["matched"], sheetName, inherited: false);
        s.Sections.Add(own);
        if (result["inherited"] is JsonArray inh)
            foreach (var item in inh)
            {
                if (item is not JsonObject io) continue;
                long from = io["fromNodeId"] is JsonNumber fn && fn.TryGetInt64(out long f) ? f : 0;
                var sec = new StyleSection { FromNodeId = from, Title = "Inherited from " + describe(from) };
                ParseMatches(sec.Blocks, io["matched"], sheetName, inherited: true);
                if (sec.Blocks.Count > 0) s.Sections.Add(sec);
            }
        return s;
    }

    static void ParseMatches(List<StyleBlock> into, JsonValue? matched, Func<string, string?> sheetName, bool inherited)
    {
        if (matched is not JsonArray arr) return;
        foreach (var item in arr)
        {
            if (item is not JsonObject m || m["rule"] is not JsonObject r) continue;
            var b = new StyleBlock
            {
                RuleId = r.GetStringOrDefault("id") ?? "",
                Selector = r.GetStringOrDefault("selectorText") ?? "",
                MatchedSelector = m.GetStringOrDefault("matchedSelector"),
                Origin = r.GetStringOrDefault("origin") ?? "author",
                Media = r.GetStringOrDefault("media"),
                Inherited = inherited,
            };
            if (r["source"] is JsonObject src && src.GetStringOrDefault("file") is { Length: > 0 } file)
            {
                long line = src["line"] is JsonNumber ln && ln.TryGetInt64(out long l) ? l : 0;
                b.Source = ShortName(file) + (line > 0 ? ":" + line.ToString(CultureInfo.InvariantCulture) : "");
            }
            else if (r.GetStringOrDefault("sheetId") is { } sheet)
            {
                b.Source = sheetName(sheet) ?? sheet;
            }
            if (b.Origin == "default") b.Source = "default stylesheet";
            ParseDecls(b.Decls, r["declarations"]);
            into.Add(b);
        }
    }

    static string ShortName(string path)
    {
        int i = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return i >= 0 && i < path.Length - 1 ? path.Substring(i + 1) : path;
    }

    internal static void ParseDecls(List<StyleDecl> into, JsonValue? decls)
    {
        if (decls is not JsonArray arr) return;
        foreach (var item in arr)
        {
            if (item is not JsonObject d) continue;
            into.Add(new StyleDecl
            {
                Name = d.GetStringOrDefault("name") ?? "",
                Value = d.GetStringOrDefault("value") ?? "",
                Important = d["important"] is JsonBoolean ib && ib.GetBoolean(),
                Status = d.GetStringOrDefault("status") ?? "active",
                Disabled = d["disabled"] is JsonBoolean db && db.GetBoolean(),
                Reason = d.GetStringOrDefault("reason"),
            });
        }
    }

    /// <summary>属性是否可继承（简写看任一长属性）；未知属性按可继承处理（不淡化）。</summary>
    public static bool IsInheritedProperty(string name)
    {
        if (name.StartsWith("--", StringComparison.Ordinal)) return true;
        if (!CssProperties.TryGet(name, out var p)) return true;
        if (!p.IsShorthand) return p.Inherited;
        foreach (var id in p.Longhands)
            if (CssProperties.Get(id).Inherited) return true;
        return false;
    }
}

/// <summary>一个计算值（design/20 <c>ComputedProperty</c>）。</summary>
internal readonly struct ComputedEntry
{
    public ComputedEntry(string name, string value, string origin)
    {
        Name = name;
        Value = value;
        Origin = origin;
    }

    public readonly string Name;
    public readonly string Value;
    /// <summary>值的来源：default / author / devtools / inline / initial ...</summary>
    public readonly string Origin;
}

/// <summary>样式表列表里的一项（<c>Css.getStyleSheets</c> 的 <c>StyleSheetInfo</c>）。</summary>
internal sealed class StyleSheetEntry
{
    public StyleSheetEntry(string sheetId)
    {
        SheetId = sheetId;
    }

    /// <summary>协议中的样式表 id（<c>Css.getStyleSheetText</c> / <c>setStyleSheetText</c> 使用）。</summary>
    public readonly string SheetId;
    public string Origin = "author";
    public string? SourceUrl;
    public string? Title;
    public bool Disabled;
    /// <summary>文本能否通过 <c>Css.setStyleSheetText</c> 替换；false 时编辑器只读。</summary>
    public bool Editable;
    /// <summary>拥有该样式表的节点（组件作用域样式）；没有为 0。</summary>
    public long OwnerNodeId;

    /// <summary>列表 / 编辑器标题里显示的名字（来源优先，其次标题，最后 id）。</summary>
    public string DisplayName =>
        SourceUrl is { Length: > 0 } url ? ShortName(url)
        : Title is { Length: > 0 } title ? title
        : SheetId;

    static string ShortName(string path)
    {
        int i = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return i >= 0 && i < path.Length - 1 ? path.Substring(i + 1) : path;
    }
}

/// <summary><c>Css.setStyleSheetText</c> 返回的一条诊断。</summary>
internal sealed class StyleSheetDiagnostic
{
    /// <summary><c>error</c> / <c>warning</c> / <c>info</c>。</summary>
    public string Severity = "info";
    public string Message = "";
    public string? Code;
    /// <summary>1 起的行号；0 表示未知。</summary>
    public int Line;
    /// <summary>1 起的列号；0 表示未知。</summary>
    public int Column;
}

/// <summary><c>Layout.getBoxModel</c> 的结果，换算成四个盒的边距。</summary>
internal sealed class BoxSnapshot
{
    public float Width, Height;
    public Thickness Margin, Border, Padding;
    public float ContentWidth, ContentHeight;
    public float X, Y;

    internal readonly struct Thickness
    {
        public Thickness(float top, float right, float bottom, float left)
        {
            Top = top; Right = right; Bottom = bottom; Left = left;
        }
        public readonly float Top, Right, Bottom, Left;
    }

    public static BoxSnapshot? Parse(JsonObject r)
    {
        if (!Quad(r["margin"], out var m) || !Quad(r["border"], out var b) || !Quad(r["padding"], out var p) || !Quad(r["content"], out var c))
            return null;
        return new BoxSnapshot
        {
            Width = Num(r["width"]),
            Height = Num(r["height"]),
            X = b.L,
            Y = b.T,
            Margin = Diff(m, b),
            Border = Diff(b, p),
            Padding = Diff(p, c),
            ContentWidth = c.R - c.L,
            ContentHeight = c.B - c.T,
        };
    }

    static Thickness Diff((float L, float T, float R, float B) outer, (float L, float T, float R, float B) inner) =>
        new(inner.T - outer.T, outer.R - inner.R, outer.B - inner.B, inner.L - outer.L);

    /// <summary>四边形（8 个数，从左上起顺时针）→ 轴对齐包围盒。</summary>
    static bool Quad(JsonValue? v, out (float L, float T, float R, float B) rect)
    {
        rect = default;
        if (v is not JsonArray a || a.Count != 8) return false;
        float l = float.MaxValue, t = float.MaxValue, r = float.MinValue, b = float.MinValue;
        for (int i = 0; i < 8; i += 2)
        {
            float x = Num(a[i]), y = Num(a[i + 1]);
            l = Math.Min(l, x); r = Math.Max(r, x);
            t = Math.Min(t, y); b = Math.Max(b, y);
        }
        rect = (l, t, r, b);
        return true;
    }

    static float Num(JsonValue? v)
    {
        if (v is not JsonNumber n) return 0;
        double d = n.GetDouble();
        return double.IsNaN(d) || double.IsInfinity(d) ? 0 : (float)Math.Max(-1e9, Math.Min(1e9, d));
    }

    /// <summary>盒模型图中的数值：整数不带小数，其余最多两位；0 写作「-」（同浏览器）。</summary>
    public static string Format(float v)
    {
        if (Math.Abs(v) < 0.005f) return "-";
        return v.ToString(Math.Abs(v - Math.Round(v)) < 0.005 ? "0" : "0.##", CultureInfo.InvariantCulture);
    }
}
