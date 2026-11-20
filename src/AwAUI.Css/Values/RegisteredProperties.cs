using System;
using System.Collections.Generic;
using System.Text;
using AwAUI.Css.Syntax;

namespace AwAUI.Css.Values;

/// <summary>
/// <c>@property</c> 的 <c>syntax</c> 描述符（CSS Properties and Values API 1 §5）：<c>"*"</c>，或用 <c>|</c> 分隔的若干项，
/// 每项是数据类型（<c>&lt;length&gt;</c> 等）或标识符，可带 <c>+</c>（空白分隔的列表）或 <c>#</c>（逗号分隔的列表）。
/// 支持的数据类型：length、number、percentage、length-percentage、color、image、url、integer、angle、time、resolution、
/// transform-function、transform-list、custom-ident、string。
/// </summary>
public sealed class CssPropertySyntax
{
    internal enum DataType : byte
    {
        Ident,
        Length,
        Number,
        Percentage,
        LengthPercentage,
        Color,
        Image,
        Url,
        Integer,
        Angle,
        Time,
        Resolution,
        TransformFunction,
        TransformList,
        CustomIdent,
        String,
    }

    internal readonly struct Component
    {
        public Component(DataType type, string? ident, char multiplier)
        {
            Type = type;
            Ident = ident;
            Multiplier = multiplier;
        }

        public DataType Type { get; }
        public string? Ident { get; }

        /// <summary>'\0'、'+' 或 '#'。</summary>
        public char Multiplier { get; }
    }

    /// <summary><c>"*"</c>：接受任何值。</summary>
    public static readonly CssPropertySyntax Universal = new("*", Array.Empty<Component>());

    CssPropertySyntax(string text, Component[] components)
    {
        Text = text;
        _components = components;
    }

    readonly Component[] _components;

    /// <summary>语法字符串原文。</summary>
    public string Text { get; }

    /// <summary>是否为通用语法 <c>*</c>。</summary>
    public bool IsUniversal => _components.Length == 0;

    /// <summary>解析语法字符串；无效返回 null。</summary>
    public static CssPropertySyntax? Parse(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        var t = text.Trim(' ', '\t', '\n', '\r', '\f');
        if (t == "*") return Universal;
        if (t.Length == 0 || t.Length > 1024) return null;
        var parts = t.Split('|');
        var list = new List<Component>(parts.Length);
        foreach (var raw in parts)
        {
            var p = raw.Trim(' ', '\t', '\n', '\r', '\f');
            if (p.Length == 0) return null;
            char mult = '\0';
            if (p[p.Length - 1] is '+' or '#')
            {
                mult = p[p.Length - 1];
                p = p.Substring(0, p.Length - 1);
            }
            if (p.Length >= 2 && p[0] == '<' && p[p.Length - 1] == '>')
            {
                var name = p.Substring(1, p.Length - 2);
                DataType? dt = name switch
                {
                    "length" => DataType.Length,
                    "number" => DataType.Number,
                    "percentage" => DataType.Percentage,
                    "length-percentage" => DataType.LengthPercentage,
                    "color" => DataType.Color,
                    "image" => DataType.Image,
                    "url" => DataType.Url,
                    "integer" => DataType.Integer,
                    "angle" => DataType.Angle,
                    "time" => DataType.Time,
                    "resolution" => DataType.Resolution,
                    "transform-function" => DataType.TransformFunction,
                    "transform-list" => DataType.TransformList,
                    "custom-ident" => DataType.CustomIdent,
                    "string" => DataType.String,
                    _ => null,
                };
                if (dt is null) return null;
                // <transform-list> 本身就是列表，不能再带乘号
                if (dt == DataType.TransformList && mult != '\0') return null;
                list.Add(new Component(dt.Value, null, mult));
            }
            else
            {
                // 字面标识符：必须是合法标识符，且不能是 CSS 全局关键字
                foreach (char c in p)
                    if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c > 0x7F)) return null;
                if (char.IsDigit(p[0]) || CssWideKeywordValue.TryParse(p, out _) || p == "default") return null;
                list.Add(new Component(DataType.Ident, p, mult));
            }
        }
        return new CssPropertySyntax(t, list.ToArray());
    }

    static readonly CssEnvironment s_env = new();

    /// <summary>
    /// 按语法校验并计算值：长度（含 em、视口单位）换算为 px，数值类的 calc() 求值为单个记号，其余保持原样。
    /// 不符合语法返回 null。<paramref name="context"/> 为 null 时只校验（不换算相对长度）。
    /// </summary>
    internal IReadOnlyList<CssComponentValue>? Compute(IReadOnlyList<CssComponentValue> value, LengthContext? context)
    {
        if (IsUniversal) return value;
        foreach (var c in _components)
        {
            var r = TryComponent(c, value, context);
            if (r is not null) return r;
        }
        return null;
    }

    /// <summary>初始值是否计算期独立（不含 em、视口单位、var() 等）且符合语法。</summary>
    internal bool IsValidInitialValue(IReadOnlyList<CssComponentValue> value)
    {
        if (IsUniversal) return true;
        if (PropertyParsers.ContainsReferences(value, 0)) return false;
        foreach (var v in value)
            if (v is CssToken { Type: CssTokenType.Dimension } d && CssLength.TryParseUnit(d.Unit, out var u) && !new CssLength(d.Number, u).IsAbsolute)
                return false;
        return Compute(value, null) is not null && !ContainsRelativeInCalc(value, 0);
    }

    static bool ContainsRelativeInCalc(IReadOnlyList<CssComponentValue> list, int depth)
    {
        if (depth > 64) return true;
        foreach (var v in list)
        {
            if (v is CssToken { Type: CssTokenType.Dimension } d && CssLength.TryParseUnit(d.Unit, out var u) && !new CssLength(d.Number, u).IsAbsolute) return true;
            if (v is CssFunction f && ContainsRelativeInCalc(f.Arguments, depth + 1)) return true;
            if (v is CssSimpleBlock b && ContainsRelativeInCalc(b.Values, depth + 1)) return true;
        }
        return false;
    }

    IReadOnlyList<CssComponentValue>? TryComponent(Component c, IReadOnlyList<CssComponentValue> value, LengthContext? ctx)
    {
        if (c.Type == DataType.TransformList)
        {
            var r = new ValueReader(value);
            return TransformParsers.TransformList(ref r) is not null && r.AtEnd ? value : null;
        }
        // 拆成项
        var items = new List<List<CssComponentValue>>();
        var cur = new List<CssComponentValue>();
        foreach (var v in value)
        {
            if (c.Multiplier == '#' && v is CssToken { Type: CssTokenType.Comma }) { items.Add(cur); cur = new List<CssComponentValue>(); continue; }
            if (v is CssToken { Type: CssTokenType.Whitespace })
            {
                if (c.Multiplier == '+' && cur.Count > 0) { items.Add(cur); cur = new List<CssComponentValue>(); }
                continue;
            }
            cur.Add(v);
        }
        if (cur.Count > 0 || c.Multiplier == '#') items.Add(cur);
        if (items.Count == 0 || (c.Multiplier == '\0' && items.Count != 1)) return null;
        var output = new List<CssComponentValue>();
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Count != 1) return null;
            var computed = Single(c, item[0], ctx);
            if (computed is null) return null;
            if (i > 0)
            {
                if (c.Multiplier == '#') output.Add(CommaToken);
                output.Add(CssToken.CreateWhitespace());
            }
            output.Add(computed);
        }
        return output;
    }

    static readonly CssToken CommaToken = (CssToken)CssComponentValue.ParseList(",")[0];

    static CssComponentValue? Single(Component c, CssComponentValue v, LengthContext? ctx)
    {
        var context = ctx ?? new LengthContext(16, 16, s_env);
        switch (c.Type)
        {
            case DataType.Ident:
                return v is CssToken { Type: CssTokenType.Ident } id && id.Value == c.Ident ? v : null;
            case DataType.CustomIdent:
                return v is CssToken { Type: CssTokenType.Ident } ci && !CssWideKeywordValue.TryParse(ci.Value, out _)
                    && !CssSerializer.EqualsIgnoreAsciiCase(ci.Value, "default") ? v : null;
            case DataType.String:
                return v is CssToken { Type: CssTokenType.String } ? v : null;
            case DataType.Url:
                return v is CssToken { Type: CssTokenType.Url } || v is CssFunction uf && CssSerializer.EqualsIgnoreAsciiCase(uf.Name, "url") ? v : null;
            case DataType.Color:
                return CssColorParser.TryParse(v, out _) ? v : null;
            case DataType.Image:
                return ValueParsers.Image(v) is not null ? v : null;
            case DataType.TransformFunction:
            {
                var r = new ValueReader(new[] { v });
                return v is CssFunction && TransformParsers.TransformList(ref r) is not null ? v : null;
            }
            case DataType.Length:
            case DataType.LengthPercentage:
            {
                var node = ValueParsers.LengthPercentageNode(v, nonNegative: false, allowPercent: c.Type == DataType.LengthPercentage);
                if (node is null) return null;
                if (ctx is null) return v;
                var lp = node.Compute(context).ToLengthPercentage();
                if (lp.IsFixed) return CssToken.CreateDimension(lp.Length, "px");
                if (lp.Calc is null && lp.Length == 0) return CssToken.CreatePercentage(lp.Percentage);
                var list = CssComponentValue.ParseList(lp.ToString());
                return list.Count == 1 ? list[0] : v;
            }
            case DataType.Number:
            case DataType.Integer:
            {
                var node = ValueParsers.NumberNode(v, nonNegative: false);
                if (node is null) return null;
                if (c.Type == DataType.Integer && v is CssToken { Type: CssTokenType.Number, NumberType: not CssNumberType.Integer }) return null;
                if (node.IsLeaf) return v;
                double n = CalcMath.FiniteDouble(node.Compute(context).Number);
                if (c.Type == DataType.Integer) n = Math.Round(n, MidpointRounding.AwayFromZero);
                return CssToken.CreateNumber(n);
            }
            case DataType.Percentage:
            {
                var node = CalcParser.ParseComponent(v, PercentMode.Length, allowUnitlessZero: false);
                if (node is null || node.Category != CalcCategory.Percent) return null;
                if (node.IsLeaf) return v;
                var res = node.Compute(context);
                return res.Tree is null ? CssToken.CreatePercentage(res.Pct) : null;
            }
            case DataType.Angle:
            {
                if (v is CssToken { Type: CssTokenType.Number }) return null;
                if (!ValueParsers.TryAngle(v, out var deg)) return null;
                return v is CssToken ? v : CssToken.CreateDimension(deg, "deg");
            }
            case DataType.Time:
            {
                if (!ValueParsers.TryTime(v, out var ms)) return null;
                return v is CssToken ? v : CssToken.CreateDimension(ms, "ms");
            }
            case DataType.Resolution:
                return v is CssToken { Type: CssTokenType.Dimension } rd && CssSerializer.ToAsciiLower(rd.Unit) is "dppx" or "x" or "dpi" or "dpcm" ? v : null;
            default:
                return null;
        }
    }

    public override string ToString() => Text;
}

/// <summary>一个已注册的自定义属性（<c>@property</c> 的结果）。</summary>
public sealed class RegisteredCustomProperty
{
    internal RegisteredCustomProperty(string name, CssPropertySyntax syntax, bool inherits, CustomPropertyValue? initialValue)
    {
        Name = name;
        Syntax = syntax;
        Inherits = inherits;
        InitialValue = initialValue;
    }

    public string Name { get; }

    public CssPropertySyntax Syntax { get; }

    public bool Inherits { get; }

    /// <summary>初始值；语法为 <c>*</c> 且未给出时为 null（保证无效）。</summary>
    public CustomPropertyValue? InitialValue { get; }
}

/// <summary>
/// 已注册自定义属性的表：由 <see cref="Cascade.StyleEngine"/> 在编译时从全部样式表的 <c>@property</c> 收集（同名的以出现顺序最后者为准，
/// 与浏览器一致；不受所在 <c>@media</c> 条件影响）。
/// </summary>
public sealed class CustomPropertyRegistry
{
    public static readonly CustomPropertyRegistry Empty = new();

    readonly Dictionary<string, RegisteredCustomProperty> _map = new(StringComparer.Ordinal);

    /// <summary>根元素的自定义属性映射：全部带初始值的已注册属性。</summary>
    internal Cascade.CustomPropertyMap InitialMap { get; private set; } = Cascade.CustomPropertyMap.Empty;

    /// <summary>是否有不继承的已注册属性（快速路径）。</summary>
    internal bool HasNonInherited { get; private set; }

    public int Count => _map.Count;

    public bool TryGet(string name, out RegisteredCustomProperty property) => _map.TryGetValue(name, out property!);

    public IEnumerable<RegisteredCustomProperty> Properties => _map.Values;

    internal void Register(RegisteredCustomProperty p)
    {
        _map[p.Name] = p;
        if (!p.Inherits) HasNonInherited = true;
    }

    internal void Seal()
    {
        var initial = new Dictionary<string, CustomPropertyValue?>(StringComparer.Ordinal);
        foreach (var p in _map.Values)
            if (p.InitialValue is not null) initial[p.Name] = p.InitialValue;
        InitialMap = Cascade.CustomPropertyMap.CreateFlat(initial);
    }
}
