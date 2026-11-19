using System;
using System.Collections.Generic;
using AwAUI.Tailwind.Ast;
using AwAUI.Tailwind.Candidates;
using AwAUI.Tailwind.Internal;
using AwAUI.Tailwind.Theming;

namespace AwAUI.Tailwind.Compilation;

/// <summary>函数式工具的选项（Tailwind <c>functionalUtility</c> 的参数）。</summary>
internal sealed class FunctionalOptions
{
    public string[] ThemeKeys = Array.Empty<string>();
    /// <summary>是否显式给了默认值（JS 中 <c>defaultValue !== undefined</c>）。</summary>
    public bool HasDefaultValue;
    public string? DefaultValue;
    public bool SupportsNegative;
    public bool SupportsFractions;
    public Func<CandidateValue, string?>? HandleBareValue;
    public Func<CandidateValue, string?>? HandleNegativeBareValue;
    public Func<string, string?, List<AstNode>?> Handle = null!;
    public Dictionary<string, List<AstNode>>? StaticValues;
}

/// <summary>
/// Tailwind CSS v4 的核心工具（移植自 <c>utilities.ts</c>，v4.3.5）。注册顺序、生成的声明与 Tailwind 逐一对应，
/// 以便输出与官方一致（一致性测试见 tests/AwAUI.Tailwind.UnitTests）。
/// </summary>
internal sealed partial class CoreUtilities
{
    readonly Theme _theme;
    readonly UtilityRegistry _u = new();

    CoreUtilities(Theme theme)
    {
        _theme = theme;
    }

    public static UtilityRegistry Create(Theme theme)
    {
        var c = new CoreUtilities(theme);
        c.RegisterLayout();
        c.RegisterSizing();
        c.RegisterTransforms();
        c.RegisterInteractivity();
        c.RegisterFlexGrid();
        c.RegisterBorders();
        c.RegisterBackgrounds();
        c.RegisterMasks();
        c.RegisterSvgAndTypography();
        c.RegisterFilters();
        c.RegisterTransitions();
        c.RegisterEffects();
        c.RegisterLegacy();
        return c._u;
    }

    // ---------------- 节点构造 ----------------

    static AstNode D(string property, string? value) => AstNode.Decl(property, value);

    static List<AstNode> L(params AstNode[] nodes) => new(nodes);

    static AstNode Rule(string selector, params AstNode[] nodes) => AstNode.Rule(selector, new List<AstNode>(nodes));

    static AstNode AtRoot(params AstNode[] nodes) => AstNode.AtRoot(new List<AstNode>(nodes));

    /// <summary><c>@property</c> 注册（Tailwind 中的 <c>property()</c>）。</summary>
    static AstNode Prop(string name, string? initial = null, string? syntax = null)
    {
        var nodes = new List<AstNode>
        {
            D("syntax", syntax != null ? "\"" + syntax + "\"" : "\"*\""),
            D("inherits", "false"),
        };
        if (initial != null) nodes.Add(D("initial-value", initial));
        return AstNode.AtRule("@property", name, nodes);
    }

    static Func<AstNode> Props(params (string Name, string? Initial, string? Syntax)[] props)
    {
        return () =>
        {
            var list = new AstNode[props.Length];
            for (int i = 0; i < props.Length; i++) list[i] = Prop(props[i].Name, props[i].Initial, props[i].Syntax);
            return AtRoot(list);
        };
    }

    static (string, string?, string?) P(string name, string? initial = null, string? syntax = null) => (name, initial, syntax);

    // ---------------- 注册辅助 ----------------

    /// <summary>静态工具：一组声明。</summary>
    void S(string name, params (string Property, string Value)[] decls)
    {
        _u.Static(name, () =>
        {
            var list = new List<AstNode>(decls.Length);
            foreach (var (p, v) in decls) list.Add(D(p, v));
            return list;
        });
    }

    /// <summary>静态工具：任意节点。</summary>
    void S(string name, Func<List<AstNode>> build) => _u.Static(name, build);

    void Fn(string name, UtilityCompileFn fn) => _u.Functional(name, fn);

    /// <summary>Tailwind <c>functionalUtility</c>。</summary>
    void Functional(string name, FunctionalOptions o)
    {
        if (o.SupportsNegative) _u.Functional("-" + name, Build(o, true));
        _u.Functional(name, Build(o, false));
    }

    UtilityCompileFn Build(FunctionalOptions o, bool negative)
    {
        return candidate =>
        {
            string? value = null;
            string? dataType = null;
            if (candidate.Value != null)
            {
                var cv = candidate.Value;
                if (cv.IsArbitrary)
                {
                    if (candidate.Modifier != null) return null;
                    value = cv.Value;
                    dataType = cv.DataType;
                }
                else
                {
                    value = _theme.Resolve(cv.Fraction ?? cv.Value, o.ThemeKeys);
                    if (value != null && candidate.Modifier != null && cv.Fraction == null) return null;
                    if (value == null && o.SupportsFractions && cv.Fraction != null)
                    {
                        var parts = Js.Segment(cv.Fraction, '/');
                        if (parts.Count < 2 || !Js.IsPositiveInteger(parts[0]) || !Js.IsPositiveInteger(parts[1])) return null;
                        value = "calc(" + parts[0] + " / " + parts[1] + " * 100%)";
                    }
                    if (value == null && negative && o.HandleNegativeBareValue != null)
                    {
                        value = o.HandleNegativeBareValue(cv);
                        if ((value == null || value.IndexOf('/') < 0) && candidate.Modifier != null) return null;
                        if (value != null) return o.Handle(value, null);
                    }
                    if (value == null && o.HandleBareValue != null)
                    {
                        value = o.HandleBareValue(cv);
                        if ((value == null || value.IndexOf('/') < 0) && candidate.Modifier != null) return null;
                    }
                    if (value == null && !negative && o.StaticValues != null && candidate.Modifier == null)
                    {
                        if (o.StaticValues.TryGetValue(cv.Value, out var nodes)) return AstNode.CloneList(nodes);
                    }
                }
            }
            else
            {
                if (candidate.Modifier != null) return null;
                value = o.HasDefaultValue ? o.DefaultValue : _theme.Resolve(null, o.ThemeKeys);
            }
            if (value == null) return null;
            return o.Handle(negative ? DataTypes.AddWhitespaceAroundMathOperators("calc(" + value + " * -1)") : value, dataType);
        };
    }

    /// <summary>Tailwind <c>colorUtility</c>。</summary>
    void Color(string name, string[] themeKeys, Func<string, List<AstNode>> handle)
    {
        _u.Functional(name, candidate =>
        {
            if (candidate.Value == null) return null;
            string? value = candidate.Value.IsArbitrary
                ? Colors.AsColor(candidate.Value.Value, candidate.Modifier, _theme)
                : Colors.ResolveThemeColor(candidate, _theme, themeKeys);
            return value == null ? null : handle(value);
        });
    }

    /// <summary>Tailwind <c>spacingUtility</c>：主题键 + <c>--spacing</c> 倍数 + <c>-px</c>。</summary>
    void Spacing(string name, string[] themeKeys, Func<string, List<AstNode>> handle, bool supportsNegative = false, bool supportsFractions = false, Dictionary<string, List<AstNode>>? staticValues = null)
    {
        if (supportsNegative) _u.Static("-" + name + "-px", () => handle("-1px"));
        _u.Static(name + "-px", () => handle("1px"));
        Functional(name, new FunctionalOptions
        {
            ThemeKeys = themeKeys,
            SupportsFractions = supportsFractions,
            SupportsNegative = supportsNegative,
            HasDefaultValue = true,
            DefaultValue = null,
            HandleBareValue = v => _theme.Resolve(null, s_spacing) == null || !Js.IsMultipleOfQuarter(v.Value) ? null : "--spacing(" + v.Value + ")",
            HandleNegativeBareValue = v => _theme.Resolve(null, s_spacing) == null || !Js.IsMultipleOfQuarter(v.Value) ? null : "--spacing(-" + v.Value + ")",
            Handle = (v, _) => handle(v),
            StaticValues = staticValues,
        });
    }

    static readonly string[] s_spacing = { "--spacing" };

    static string[] K(params string[] keys) => keys;

    static Dictionary<string, List<AstNode>> Statics(params (string Key, List<AstNode> Nodes)[] entries)
    {
        var d = new Dictionary<string, List<AstNode>>(StringComparer.Ordinal);
        foreach (var (k, n) in entries) d[k] = n;
        return d;
    }

    static Func<CandidateValue, string?> BareInteger => v => Js.IsPositiveInteger(v.Value) ? v.Value : null;

    static Func<CandidateValue, string?> BareSuffix(string suffix) => v => Js.IsPositiveInteger(v.Value) ? v.Value + suffix : null;
}
