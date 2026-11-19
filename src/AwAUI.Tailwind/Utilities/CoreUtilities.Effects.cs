using System;
using System.Collections.Generic;
using AwAUI.Tailwind.Ast;
using AwAUI.Tailwind.Candidates;
using AwAUI.Tailwind.Internal;

namespace AwAUI.Tailwind.Compilation;

internal sealed partial class CoreUtilities
{
    const string FilterValue = "var(--tw-blur, ) var(--tw-brightness, ) var(--tw-contrast, ) var(--tw-grayscale, ) var(--tw-hue-rotate, ) var(--tw-invert, ) var(--tw-saturate, ) var(--tw-sepia, ) var(--tw-drop-shadow, )";
    const string BackdropValue = "var(--tw-backdrop-blur, ) var(--tw-backdrop-brightness, ) var(--tw-backdrop-contrast, ) var(--tw-backdrop-grayscale, ) var(--tw-backdrop-hue-rotate, ) var(--tw-backdrop-invert, ) var(--tw-backdrop-opacity, ) var(--tw-backdrop-saturate, ) var(--tw-backdrop-sepia, )";
    const string BoxShadowValue = "var(--tw-inset-shadow), var(--tw-inset-ring-shadow), var(--tw-ring-offset-shadow), var(--tw-ring-shadow), var(--tw-shadow)";

    /// <summary>阴影类工具的透明度修饰：<c>/50</c> → <c>50%</c>，任意值原样。</summary>
    static string? ShadowAlpha(CandidateModifier? m)
    {
        if (m == null) return null;
        if (m.IsArbitrary) return m.Value;
        return Js.IsMultipleOfQuarter(m.Value) ? m.Value + "%" : null;
    }

    void RegisterFilters()
    {
        var filterProps = Props(P("--tw-blur"), P("--tw-brightness"), P("--tw-contrast"), P("--tw-grayscale"), P("--tw-hue-rotate"), P("--tw-invert"), P("--tw-opacity"), P("--tw-saturate"), P("--tw-sepia"),
            P("--tw-drop-shadow"), P("--tw-drop-shadow-color"), P("--tw-drop-shadow-alpha", "100%", "<percentage>"), P("--tw-drop-shadow-size"));
        var backdropProps = Props(P("--tw-backdrop-blur"), P("--tw-backdrop-brightness"), P("--tw-backdrop-contrast"), P("--tw-backdrop-grayscale"), P("--tw-backdrop-hue-rotate"),
            P("--tw-backdrop-invert"), P("--tw-backdrop-opacity"), P("--tw-backdrop-saturate"), P("--tw-backdrop-sepia"));

        Fn("filter", c =>
        {
            if (c.Modifier != null) return null;
            if (c.Value == null) return L(filterProps(), D("filter", FilterValue));
            if (c.Value.IsArbitrary) return L(D("filter", c.Value.Value));
            if (c.Value.Value == "none") return L(D("filter", "none"));
            return null;
        });
        Fn("backdrop-filter", c =>
        {
            if (c.Modifier != null) return null;
            if (c.Value == null) return L(backdropProps(), D("-webkit-backdrop-filter", BackdropValue), D("backdrop-filter", BackdropValue));
            if (c.Value.IsArbitrary) return L(D("-webkit-backdrop-filter", c.Value.Value), D("backdrop-filter", c.Value.Value));
            if (c.Value.Value == "none") return L(D("-webkit-backdrop-filter", "none"), D("backdrop-filter", "none"));
            return null;
        });
        Functional("blur", new FunctionalOptions
        {
            ThemeKeys = K("--blur"),
            Handle = (v, _) => L(filterProps(), D("--tw-blur", "blur(" + v + ")"), D("filter", FilterValue)),
            StaticValues = Statics(("none", L(filterProps(), D("--tw-blur", " "), D("filter", FilterValue)))),
        });
        Functional("backdrop-blur", new FunctionalOptions
        {
            ThemeKeys = K("--backdrop-blur", "--blur"),
            Handle = (v, _) => L(backdropProps(), D("--tw-backdrop-blur", "blur(" + v + ")"), D("-webkit-backdrop-filter", BackdropValue), D("backdrop-filter", BackdropValue)),
            StaticValues = Statics(("none", L(backdropProps(), D("--tw-backdrop-blur", " "), D("-webkit-backdrop-filter", BackdropValue), D("backdrop-filter", BackdropValue)))),
        });
        void FilterPair(string name, string fn, string bareSuffix, bool supportsNegative, string? defaultValue)
        {
            Functional(name, new FunctionalOptions
            {
                SupportsNegative = supportsNegative,
                ThemeKeys = K("--" + name),
                HandleBareValue = BareSuffix(bareSuffix),
                HasDefaultValue = defaultValue != null,
                DefaultValue = defaultValue,
                Handle = (v, _) => L(filterProps(), D("--tw-" + name, fn + "(" + v + ")"), D("filter", FilterValue)),
            });
            Functional("backdrop-" + name, new FunctionalOptions
            {
                SupportsNegative = supportsNegative,
                ThemeKeys = K("--backdrop-" + name, "--" + name),
                HandleBareValue = BareSuffix(bareSuffix),
                HasDefaultValue = defaultValue != null,
                DefaultValue = defaultValue,
                Handle = (v, _) => L(backdropProps(), D("--tw-backdrop-" + name, fn + "(" + v + ")"), D("-webkit-backdrop-filter", BackdropValue), D("backdrop-filter", BackdropValue)),
            });
        }
        FilterPair("brightness", "brightness", "%", false, null);
        FilterPair("contrast", "contrast", "%", false, null);
        FilterPair("grayscale", "grayscale", "%", false, "100%");
        FilterPair("hue-rotate", "hue-rotate", "deg", true, null);
        FilterPair("invert", "invert", "%", false, "100%");
        FilterPair("saturate", "saturate", "%", false, null);
        FilterPair("sepia", "sepia", "%", false, "100%");

        S("drop-shadow-none", () => L(filterProps(), D("--tw-drop-shadow", " "), D("filter", FilterValue)));
        Fn("drop-shadow", c =>
        {
            string? alpha = ShadowAlpha(c.Modifier);
            if (c.Value == null)
            {
                string? raw = _theme.Get(K("--drop-shadow"));
                string? resolved = _theme.Resolve(null, K("--drop-shadow"));
                if (raw == null || resolved == null || (c.Modifier != null && alpha == null)) return null;
                var list = L(filterProps(), D("--tw-drop-shadow-alpha", alpha));
                list.AddRange(ShadowColors.AlphaReplacedDropShadow("--tw-drop-shadow-size", raw, alpha, x => "var(--tw-drop-shadow-color, " + x + ")"));
                list.Add(D("--tw-drop-shadow", DropShadows(resolved)));
                list.Add(D("filter", FilterValue));
                return list;
            }
            if (c.Value.IsArbitrary)
            {
                string v = c.Value.Value;
                if ((c.Value.DataType ?? DataTypes.Infer(v, new[] { "color" })) == "color")
                {
                    string? color = Colors.AsColor(v, c.Modifier, _theme);
                    if (color == null) return null;
                    return L(filterProps(), D("--tw-drop-shadow-color", Colors.WithAlpha(color, "var(--tw-drop-shadow-alpha)")), D("--tw-drop-shadow", "var(--tw-drop-shadow-size)"));
                }
                if (c.Modifier != null && alpha == null) return null;
                var list = L(filterProps(), D("--tw-drop-shadow-alpha", alpha));
                list.AddRange(ShadowColors.AlphaReplacedDropShadow("--tw-drop-shadow-size", v, alpha, x => "var(--tw-drop-shadow-color, " + x + ")"));
                list.Add(D("--tw-drop-shadow", "var(--tw-drop-shadow-size)"));
                list.Add(D("filter", FilterValue));
                return list;
            }
            {
                string? raw = _theme.Get("--drop-shadow-" + c.Value.Value);
                string? resolved = _theme.Resolve(c.Value.Value, K("--drop-shadow"));
                if (raw != null && resolved != null)
                {
                    if (c.Modifier != null && alpha == null) return null;
                    var list = L(filterProps(), D("--tw-drop-shadow-alpha", alpha));
                    list.AddRange(ShadowColors.AlphaReplacedDropShadow("--tw-drop-shadow-size", raw, alpha, x => "var(--tw-drop-shadow-color, " + x + ")"));
                    list.Add(D("--tw-drop-shadow", alpha != null ? "var(--tw-drop-shadow-size)" : DropShadows(resolved)));
                    list.Add(D("filter", FilterValue));
                    return list;
                }
            }
            {
                string? color = Colors.ResolveThemeColor(c, _theme, K("--drop-shadow-color", "--color"));
                if (color != null)
                {
                    if (color == "inherit") return L(filterProps(), D("--tw-drop-shadow-color", "inherit"), D("--tw-drop-shadow", "var(--tw-drop-shadow-size)"));
                    return L(filterProps(), D("--tw-drop-shadow-color", Colors.WithAlpha(color, "var(--tw-drop-shadow-alpha)")), D("--tw-drop-shadow", "var(--tw-drop-shadow-size)"));
                }
            }
            return null;
        });
        Functional("backdrop-opacity", new FunctionalOptions
        {
            ThemeKeys = K("--backdrop-opacity", "--opacity"),
            HandleBareValue = v => Js.IsMultipleOfQuarter(v.Value) ? v.Value + "%" : null,
            Handle = (v, _) => L(backdropProps(), D("--tw-backdrop-opacity", "opacity(" + v + ")"), D("-webkit-backdrop-filter", BackdropValue), D("backdrop-filter", BackdropValue)),
        });
    }

    static string DropShadows(string value)
    {
        var parts = Js.Segment(value, ',');
        for (int i = 0; i < parts.Count; i++) parts[i] = "drop-shadow(" + parts[i] + ")";
        return string.Join(" ", parts);
    }

    void RegisterTransitions()
    {
        string ease = "var(--tw-ease, " + (_theme.Resolve(null, K("--default-transition-timing-function")) ?? "ease") + ")";
        string duration = "var(--tw-duration, " + (_theme.Resolve(null, K("--default-transition-duration")) ?? "0s") + ")";
        List<AstNode> Transition(string property) => L(D("transition-property", property), D("transition-timing-function", ease), D("transition-duration", duration));
        Functional("transition", new FunctionalOptions
        {
            HasDefaultValue = true,
            DefaultValue = "color, background-color, border-color, outline-color, text-decoration-color, fill, stroke, --tw-gradient-from, --tw-gradient-via, --tw-gradient-to, opacity, box-shadow, transform, translate, scale, rotate, filter, -webkit-backdrop-filter, backdrop-filter, display, content-visibility, overlay, pointer-events",
            ThemeKeys = K("--transition-property"),
            Handle = (v, _) => Transition(v),
            StaticValues = Statics(
                ("none", L(D("transition-property", "none"))),
                ("all", Transition("all")),
                ("colors", Transition("color, background-color, border-color, outline-color, text-decoration-color, fill, stroke, --tw-gradient-from, --tw-gradient-via, --tw-gradient-to")),
                ("opacity", Transition("opacity")),
                ("shadow", Transition("box-shadow")),
                ("transform", Transition("transform, translate, scale, rotate"))),
        });
        S("transition-discrete", ("transition-behavior", "allow-discrete"));
        S("transition-normal", ("transition-behavior", "normal"));
        Functional("delay", new FunctionalOptions { HandleBareValue = BareSuffix("ms"), ThemeKeys = K("--transition-delay"), Handle = (v, _) => L(D("transition-delay", v)) });
        var durationProps = Props(P("--tw-duration"));
        S("duration-initial", () => L(durationProps(), D("--tw-duration", "initial")));
        Fn("duration", c =>
        {
            if (c.Modifier != null || c.Value == null) return null;
            string? v;
            if (c.Value.IsArbitrary) v = c.Value.Value;
            else
            {
                v = _theme.Resolve(c.Value.Fraction ?? c.Value.Value, K("--transition-duration"));
                if (v == null && Js.IsPositiveInteger(c.Value.Value)) v = c.Value.Value + "ms";
            }
            return v == null ? null : L(durationProps(), D("--tw-duration", v), D("transition-duration", v));
        });
        var easeProps = Props(P("--tw-ease"));
        Functional("ease", new FunctionalOptions
        {
            ThemeKeys = K("--ease"),
            Handle = (v, _) => L(easeProps(), D("--tw-ease", v), D("transition-timing-function", v)),
            StaticValues = Statics(("initial", L(easeProps(), D("--tw-ease", "initial"))), ("linear", L(easeProps(), D("--tw-ease", "linear"), D("transition-timing-function", "linear")))),
        });
        S("will-change-auto", ("will-change", "auto"));
        S("will-change-scroll", ("will-change", "scroll-position"));
        S("will-change-contents", ("will-change", "contents"));
        S("will-change-transform", ("will-change", "transform"));
        Functional("will-change", new FunctionalOptions { Handle = (v, _) => L(D("will-change", v)) });
        S("content-none", ("--tw-content", "none"), ("content", "none"));
        var contentProps = Props(P("--tw-content", "\"\""));
        Functional("content", new FunctionalOptions { ThemeKeys = K("--content"), Handle = (v, _) => L(contentProps(), D("--tw-content", v), D("content", "var(--tw-content)")) });
        const string ContainValue = "var(--tw-contain-size, ) var(--tw-contain-layout, ) var(--tw-contain-paint, ) var(--tw-contain-style, )";
        var containProps = Props(P("--tw-contain-size"), P("--tw-contain-layout"), P("--tw-contain-paint"), P("--tw-contain-style"));
        S("contain-none", ("contain", "none"));
        S("contain-content", ("contain", "content"));
        S("contain-strict", ("contain", "strict"));
        S("contain-size", () => L(containProps(), D("--tw-contain-size", "size"), D("contain", ContainValue)));
        S("contain-inline-size", () => L(containProps(), D("--tw-contain-size", "inline-size"), D("contain", ContainValue)));
        S("contain-layout", () => L(containProps(), D("--tw-contain-layout", "layout"), D("contain", ContainValue)));
        S("contain-paint", () => L(containProps(), D("--tw-contain-paint", "paint"), D("contain", ContainValue)));
        S("contain-style", () => L(containProps(), D("--tw-contain-style", "style"), D("contain", ContainValue)));
        Functional("contain", new FunctionalOptions { Handle = (v, _) => L(D("contain", v)) });
        S("forced-color-adjust-none", ("forced-color-adjust", "none"));
        S("forced-color-adjust-auto", ("forced-color-adjust", "auto"));
        var leadingProps = Props(P("--tw-leading"));
        Spacing("leading", K("--leading", "--spacing"), v => L(leadingProps(), D("--tw-leading", v), D("line-height", v)),
            staticValues: Statics(("none", L(leadingProps(), D("--tw-leading", "1"), D("line-height", "1")))));
        var trackingProps = Props(P("--tw-tracking"));
        Functional("tracking", new FunctionalOptions { SupportsNegative = true, ThemeKeys = K("--tracking"), Handle = (v, _) => L(trackingProps(), D("--tw-tracking", v), D("letter-spacing", v)) });
        S("antialiased", ("-webkit-font-smoothing", "antialiased"), ("-moz-osx-font-smoothing", "grayscale"));
        S("subpixel-antialiased", ("-webkit-font-smoothing", "auto"), ("-moz-osx-font-smoothing", "auto"));
        const string NumericValue = "var(--tw-ordinal, ) var(--tw-slashed-zero, ) var(--tw-numeric-figure, ) var(--tw-numeric-spacing, ) var(--tw-numeric-fraction, )";
        var numericProps = Props(P("--tw-ordinal"), P("--tw-slashed-zero"), P("--tw-numeric-figure"), P("--tw-numeric-spacing"), P("--tw-numeric-fraction"));
        S("normal-nums", ("font-variant-numeric", "normal"));
        foreach (var (name, variable, value) in new[]
                 {
                     ("ordinal", "--tw-ordinal", "ordinal"), ("slashed-zero", "--tw-slashed-zero", "slashed-zero"), ("lining-nums", "--tw-numeric-figure", "lining-nums"),
                     ("oldstyle-nums", "--tw-numeric-figure", "oldstyle-nums"), ("proportional-nums", "--tw-numeric-spacing", "proportional-nums"),
                     ("tabular-nums", "--tw-numeric-spacing", "tabular-nums"), ("diagonal-fractions", "--tw-numeric-fraction", "diagonal-fractions"),
                     ("stacked-fractions", "--tw-numeric-fraction", "stacked-fractions"),
                 })
        {
            string vr = variable, vl = value;
            S(name, () => L(numericProps(), D(vr, vl), D("font-variant-numeric", NumericValue)));
        }
        var outlineProps = Props(P("--tw-outline-style", "solid"));
        S("outline-hidden", () => L(D("--tw-outline-style", "none"), D("outline-style", "none"),
            AstNode.AtRule("@media", "(forced-colors: active)", L(D("outline", "2px solid transparent"), D("outline-offset", "2px")))));
        foreach (var s in new[] { "none", "solid", "dashed", "dotted", "double" }) S("outline-" + s, ("--tw-outline-style", s), ("outline-style", s));
        List<AstNode> OutlineWidth(string w) => L(outlineProps(), D("outline-style", "var(--tw-outline-style)"), D("outline-width", w));
        Fn("outline", c =>
        {
            if (c.Value == null)
            {
                if (c.Modifier != null) return null;
                return OutlineWidth(_theme.Get("--default-outline-width") ?? "1px");
            }
            if (c.Value.IsArbitrary)
            {
                string v = c.Value.Value;
                switch (c.Value.DataType ?? DataTypes.Infer(v, new[] { "color", "length", "number", "percentage" }))
                {
                    case "length":
                    case "number":
                    case "percentage":
                        return c.Modifier != null ? null : OutlineWidth(v);
                    default:
                    {
                        string? color = Colors.AsColor(v, c.Modifier, _theme);
                        return color == null ? null : L(D("outline-color", color));
                    }
                }
            }
            {
                string? color = Colors.ResolveThemeColor(c, _theme, K("--outline-color", "--color"));
                if (color != null) return L(D("outline-color", color));
            }
            if (c.Modifier != null) return null;
            string? width = _theme.Resolve(c.Value.Value, K("--outline-width"));
            if (width != null) return OutlineWidth(width);
            if (Js.IsPositiveInteger(c.Value.Value)) return OutlineWidth(c.Value.Value + "px");
            return null;
        });
        Functional("outline-offset", new FunctionalOptions { SupportsNegative = true, ThemeKeys = K("--outline-offset"), HandleBareValue = BareSuffix("px"), Handle = (v, _) => L(D("outline-offset", v)) });
        Functional("opacity", new FunctionalOptions { ThemeKeys = K("--opacity"), HandleBareValue = v => Js.IsMultipleOfQuarter(v.Value) ? v.Value + "%" : null, Handle = (v, _) => L(D("opacity", v)) });
        Functional("underline-offset", new FunctionalOptions
        {
            SupportsNegative = true,
            ThemeKeys = K("--text-underline-offset"),
            HandleBareValue = BareSuffix("px"),
            Handle = (v, _) => L(D("text-underline-offset", v)),
            StaticValues = Statics(("auto", L(D("text-underline-offset", "auto")))),
        });

        Fn("text", c =>
        {
            if (c.Value == null) return null;
            if (c.Value.IsArbitrary)
            {
                string v = c.Value.Value;
                switch (c.Value.DataType ?? DataTypes.Infer(v, new[] { "color", "length", "percentage", "absolute-size", "relative-size" }))
                {
                    case "size":
                    case "length":
                    case "percentage":
                    case "absolute-size":
                    case "relative-size":
                    {
                        if (c.Modifier != null)
                        {
                            string? lh = c.Modifier.IsArbitrary ? c.Modifier.Value : _theme.Resolve(c.Modifier.Value, K("--leading"));
                            if (lh == null && Js.IsMultipleOfQuarter(c.Modifier.Value))
                            {
                                if (_theme.Resolve(null, s_spacing) == null) return null;
                                lh = "--spacing(" + c.Modifier.Value + ")";
                            }
                            if (lh == null && c.Modifier.Value == "none") lh = "1";
                            return lh != null ? L(D("font-size", v), D("line-height", lh)) : null;
                        }
                        return L(D("font-size", v));
                    }
                    default:
                    {
                        string? color = Colors.AsColor(v, c.Modifier, _theme);
                        return color == null ? null : L(D("color", color));
                    }
                }
            }
            {
                string? color = Colors.ResolveThemeColor(c, _theme, K("--text-color", "--color"));
                if (color != null) return L(D("color", color));
            }
            var size = _theme.ResolveWith(c.Value.Value, K("--text"), K("--line-height", "--letter-spacing", "--font-weight"));
            if (size == null) return null;
            var (fontSize, extra) = size.Value;
            if (c.Modifier != null)
            {
                string? lh = c.Modifier.IsArbitrary ? c.Modifier.Value : _theme.Resolve(c.Modifier.Value, K("--leading"));
                if (lh == null && Js.IsMultipleOfQuarter(c.Modifier.Value))
                {
                    if (_theme.Resolve(null, s_spacing) == null) return null;
                    lh = "--spacing(" + c.Modifier.Value + ")";
                }
                if (lh == null && c.Modifier.Value == "none") lh = "1";
                if (lh == null) return null;
                return L(D("font-size", fontSize), D("line-height", lh));
            }
            extra.TryGetValue("--line-height", out var lineHeight);
            extra.TryGetValue("--letter-spacing", out var letterSpacing);
            extra.TryGetValue("--font-weight", out var fontWeight);
            return L(
                D("font-size", fontSize),
                D("line-height", lineHeight != null ? "var(--tw-leading, " + lineHeight + ")" : null),
                D("letter-spacing", letterSpacing != null ? "var(--tw-tracking, " + letterSpacing + ")" : null),
                D("font-weight", fontWeight != null ? "var(--tw-font-weight, " + fontWeight + ")" : null));
        });

        var textShadowProps = Props(P("--tw-text-shadow-color"), P("--tw-text-shadow-alpha", "100%", "<percentage>"));
        S("text-shadow-initial", () => L(textShadowProps(), D("--tw-text-shadow-color", "initial")));
        Fn("text-shadow", c =>
        {
            string? alpha = ShadowAlpha(c.Modifier);
            Func<string, string> inject = x => "var(--tw-text-shadow-color, " + x + ")";
            if (c.Value == null)
            {
                string? raw = _theme.Get(K("--text-shadow"));
                if (raw == null || (c.Modifier != null && alpha == null)) return null;
                var list = L(textShadowProps(), D("--tw-text-shadow-alpha", alpha));
                list.AddRange(ShadowColors.AlphaReplacedShadow("text-shadow", raw, alpha, inject));
                return list;
            }
            if (c.Value.IsArbitrary)
            {
                string v = c.Value.Value;
                if ((c.Value.DataType ?? DataTypes.Infer(v, new[] { "color" })) == "color")
                {
                    string? color = Colors.AsColor(v, c.Modifier, _theme);
                    return color == null ? null : L(textShadowProps(), D("--tw-text-shadow-color", Colors.WithAlpha(color, "var(--tw-text-shadow-alpha)")));
                }
                if (c.Modifier != null && alpha == null) return null;
                var list = L(textShadowProps(), D("--tw-text-shadow-alpha", alpha));
                list.AddRange(ShadowColors.AlphaReplacedShadow("text-shadow", v, alpha, inject));
                return list;
            }
            switch (c.Value.Value)
            {
                case "none":
                    return c.Modifier != null ? null : L(textShadowProps(), D("text-shadow", "none"));
                case "inherit":
                    return c.Modifier != null ? null : L(textShadowProps(), D("--tw-text-shadow-color", "inherit"));
            }
            {
                string? raw = _theme.Get("--text-shadow-" + c.Value.Value);
                if (raw != null)
                {
                    if (c.Modifier != null && alpha == null) return null;
                    var list = L(textShadowProps(), D("--tw-text-shadow-alpha", alpha));
                    list.AddRange(ShadowColors.AlphaReplacedShadow("text-shadow", raw, alpha, inject));
                    return list;
                }
            }
            string? col = Colors.ResolveThemeColor(c, _theme, K("--text-shadow-color", "--color"));
            return col != null ? L(textShadowProps(), D("--tw-text-shadow-color", Colors.WithAlpha(col, "var(--tw-text-shadow-alpha)"))) : null;
        });
    }

    void RegisterEffects()
    {
        const string Zero = "0 0 #0000";
        var shadowProps = Props(
            P("--tw-shadow", Zero), P("--tw-shadow-color"), P("--tw-shadow-alpha", "100%", "<percentage>"),
            P("--tw-inset-shadow", Zero), P("--tw-inset-shadow-color"), P("--tw-inset-shadow-alpha", "100%", "<percentage>"),
            P("--tw-ring-color"), P("--tw-ring-shadow", Zero), P("--tw-inset-ring-color"), P("--tw-inset-ring-shadow", Zero),
            P("--tw-ring-inset"), P("--tw-ring-offset-width", "0px", "<length>"), P("--tw-ring-offset-color", "#fff"), P("--tw-ring-offset-shadow", Zero));

        void ShadowUtility(string name, string variable, string colorVar, string alphaVar, string themeNs, bool inset)
        {
            Func<string, string> inject = x => "var(" + colorVar + ", " + x + ")";
            S(name + "-initial", () => L(shadowProps(), D(colorVar, "initial")));
            Fn(name, c =>
            {
                string? alpha = ShadowAlpha(c.Modifier);
                if (c.Value == null)
                {
                    string? raw = _theme.Get(K(themeNs));
                    if (raw == null || (c.Modifier != null && alpha == null)) return null;
                    var list = L(shadowProps(), D(alphaVar, alpha));
                    list.AddRange(ShadowColors.AlphaReplacedShadow(variable, raw, alpha, inject));
                    list.Add(D("box-shadow", BoxShadowValue));
                    return list;
                }
                if (c.Value.IsArbitrary)
                {
                    string v = c.Value.Value;
                    if ((c.Value.DataType ?? DataTypes.Infer(v, new[] { "color" })) == "color")
                    {
                        string? color = Colors.AsColor(v, c.Modifier, _theme);
                        return color == null ? null : L(shadowProps(), D(colorVar, Colors.WithAlpha(color, "var(" + alphaVar + ")")));
                    }
                    if (c.Modifier != null && alpha == null) return null;
                    var list = L(shadowProps(), D(alphaVar, alpha));
                    list.AddRange(ShadowColors.AlphaReplacedShadow(variable, v, alpha, inject, inset ? "inset" : ""));
                    list.Add(D("box-shadow", BoxShadowValue));
                    return list;
                }
                switch (c.Value.Value)
                {
                    case "none":
                        return c.Modifier != null ? null : L(shadowProps(), D(variable, inset ? "inset " + Zero : Zero), D("box-shadow", BoxShadowValue));
                    case "inherit":
                        return c.Modifier != null ? null : L(shadowProps(), D(colorVar, "inherit"));
                }
                {
                    string? raw = _theme.Get(themeNs + "-" + c.Value.Value);
                    if (raw != null)
                    {
                        if (c.Modifier != null && alpha == null) return null;
                        var list = L(shadowProps(), D(alphaVar, alpha));
                        list.AddRange(ShadowColors.AlphaReplacedShadow(variable, raw, alpha, inject));
                        list.Add(D("box-shadow", BoxShadowValue));
                        return list;
                    }
                }
                string? col = Colors.ResolveThemeColor(c, _theme, K("--box-shadow-color", "--color"));
                return col != null ? L(shadowProps(), D(colorVar, Colors.WithAlpha(col, "var(" + alphaVar + ")"))) : null;
            });
        }
        ShadowUtility("shadow", "--tw-shadow", "--tw-shadow-color", "--tw-shadow-alpha", "--shadow", false);
        ShadowUtility("inset-shadow", "--tw-inset-shadow", "--tw-inset-shadow-color", "--tw-inset-shadow-alpha", "--inset-shadow", true);

        S("ring-inset", () => L(shadowProps(), D("--tw-ring-inset", "inset")));
        string defaultRingColor = _theme.Get("--default-ring-color") ?? "currentcolor";
        string RingShadow(string w) => "var(--tw-ring-inset, ) 0 0 0 calc(" + w + " + var(--tw-ring-offset-width)) var(--tw-ring-color, " + defaultRingColor + ")";
        string InsetRingShadow(string w) => "inset 0 0 0 " + w + " var(--tw-inset-ring-color, currentcolor)";
        Fn("ring", c =>
        {
            if (c.Value == null)
            {
                if (c.Modifier != null) return null;
                string w = _theme.Get("--default-ring-width") ?? "1px";
                return L(shadowProps(), D("--tw-ring-shadow", RingShadow(w)), D("box-shadow", BoxShadowValue));
            }
            if (c.Value.IsArbitrary)
            {
                string v = c.Value.Value;
                if ((c.Value.DataType ?? DataTypes.Infer(v, new[] { "color", "length" })) == "length")
                    return c.Modifier != null ? null : L(shadowProps(), D("--tw-ring-shadow", RingShadow(v)), D("box-shadow", BoxShadowValue));
                string? color = Colors.AsColor(v, c.Modifier, _theme);
                return color == null ? null : L(D("--tw-ring-color", color));
            }
            {
                string? color = Colors.ResolveThemeColor(c, _theme, K("--ring-color", "--color"));
                if (color != null) return L(D("--tw-ring-color", color));
            }
            if (c.Modifier != null) return null;
            string? width = _theme.Resolve(c.Value.Value, K("--ring-width"));
            if (width == null && Js.IsPositiveInteger(c.Value.Value)) width = c.Value.Value + "px";
            return width != null ? L(shadowProps(), D("--tw-ring-shadow", RingShadow(width)), D("box-shadow", BoxShadowValue)) : null;
        });
        Fn("inset-ring", c =>
        {
            if (c.Value == null)
                return c.Modifier != null ? null : L(shadowProps(), D("--tw-inset-ring-shadow", InsetRingShadow("1px")), D("box-shadow", BoxShadowValue));
            if (c.Value.IsArbitrary)
            {
                string v = c.Value.Value;
                if ((c.Value.DataType ?? DataTypes.Infer(v, new[] { "color", "length" })) == "length")
                    return c.Modifier != null ? null : L(shadowProps(), D("--tw-inset-ring-shadow", InsetRingShadow(v)), D("box-shadow", BoxShadowValue));
                string? color = Colors.AsColor(v, c.Modifier, _theme);
                return color == null ? null : L(D("--tw-inset-ring-color", color));
            }
            {
                string? color = Colors.ResolveThemeColor(c, _theme, K("--ring-color", "--color"));
                if (color != null) return L(D("--tw-inset-ring-color", color));
            }
            if (c.Modifier != null) return null;
            string? width = _theme.Resolve(c.Value.Value, K("--ring-width"));
            if (width == null && Js.IsPositiveInteger(c.Value.Value)) width = c.Value.Value + "px";
            return width != null ? L(shadowProps(), D("--tw-inset-ring-shadow", InsetRingShadow(width)), D("box-shadow", BoxShadowValue)) : null;
        });
        const string RingOffsetShadow = "var(--tw-ring-inset, ) 0 0 0 var(--tw-ring-offset-width) var(--tw-ring-offset-color)";
        Fn("ring-offset", c =>
        {
            if (c.Value == null) return null;
            if (c.Value.IsArbitrary)
            {
                string v = c.Value.Value;
                if ((c.Value.DataType ?? DataTypes.Infer(v, new[] { "color", "length" })) == "length")
                    return c.Modifier != null ? null : L(D("--tw-ring-offset-width", v), D("--tw-ring-offset-shadow", RingOffsetShadow));
                string? color = Colors.AsColor(v, c.Modifier, _theme);
                return color == null ? null : L(D("--tw-ring-offset-color", color));
            }
            {
                string? width = _theme.Resolve(c.Value.Value, K("--ring-offset-width"));
                if (width != null) return c.Modifier != null ? null : L(D("--tw-ring-offset-width", width), D("--tw-ring-offset-shadow", RingOffsetShadow));
                if (Js.IsPositiveInteger(c.Value.Value)) return c.Modifier != null ? null : L(D("--tw-ring-offset-width", c.Value.Value + "px"), D("--tw-ring-offset-shadow", RingOffsetShadow));
            }
            string? col = Colors.ResolveThemeColor(c, _theme, K("--ring-offset-color", "--color"));
            return col != null ? L(D("--tw-ring-offset-color", col)) : null;
        });

        Fn("@container", c =>
        {
            string? type = null;
            if (c.Value == null) type = "inline-size";
            else if (c.Value.IsArbitrary) type = c.Value.Value;
            else if (c.Value.Value == "normal") type = "normal";
            else if (c.Value.Value == "size") type = "size";
            if (type == null) return null;
            return c.Modifier != null ? L(D("container-type", type), D("container-name", c.Modifier.Value)) : L(D("container-type", type));
        });
    }
}
