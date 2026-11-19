using System;
using System.Collections.Generic;
using AwAUI.Tailwind.Ast;
using AwAUI.Tailwind.Internal;

namespace AwAUI.Tailwind.Compilation;

internal sealed partial class CoreUtilities
{
    void RegisterSvgAndTypography()
    {
        S("fill-none", ("fill", "none"));
        Fn("fill", c =>
        {
            if (c.Value == null) return null;
            if (c.Value.IsArbitrary)
            {
                string? v = Colors.AsColor(c.Value.Value, c.Modifier, _theme);
                return v == null ? null : L(D("fill", v));
            }
            string? color = Colors.ResolveThemeColor(c, _theme, K("--fill", "--color"));
            return color != null ? L(D("fill", color)) : null;
        });
        S("stroke-none", ("stroke", "none"));
        Fn("stroke", c =>
        {
            if (c.Value == null) return null;
            if (c.Value.IsArbitrary)
            {
                string v = c.Value.Value;
                switch (c.Value.DataType ?? DataTypes.Infer(v, new[] { "color", "number", "length", "percentage" }))
                {
                    case "number":
                    case "length":
                    case "percentage":
                        return c.Modifier != null ? null : L(D("stroke-width", v));
                    default:
                    {
                        string? color = Colors.AsColor(c.Value.Value, c.Modifier, _theme);
                        return color == null ? null : L(D("stroke", color));
                    }
                }
            }
            {
                string? color = Colors.ResolveThemeColor(c, _theme, K("--stroke", "--color"));
                if (color != null) return L(D("stroke", color));
            }
            if (c.Modifier != null) return null;
            string? width = _theme.Resolve(c.Value.Value, K("--stroke-width"));
            if (width != null) return L(D("stroke-width", width));
            if (Js.IsPositiveInteger(c.Value.Value)) return L(D("stroke-width", c.Value.Value));
            return null;
        });
        foreach (var v in new[] { "contain", "cover", "fill", "none", "scale-down" }) S("object-" + v, ("object-fit", v));
        Functional("object", new FunctionalOptions
        {
            ThemeKeys = K("--object-position"),
            Handle = (v, _) => L(D("object-position", v)),
            StaticValues = Statics(
                ("top", L(D("object-position", "top"))), ("top-left", L(D("object-position", "left top"))), ("top-right", L(D("object-position", "right top"))),
                ("bottom", L(D("object-position", "bottom"))), ("bottom-left", L(D("object-position", "left bottom"))), ("bottom-right", L(D("object-position", "right bottom"))),
                ("left", L(D("object-position", "left"))), ("right", L(D("object-position", "right"))), ("center", L(D("object-position", "center")))),
        });
        foreach (var (name, property) in new[]
                 {
                     ("p", "padding"), ("px", "padding-inline"), ("py", "padding-block"), ("ps", "padding-inline-start"), ("pe", "padding-inline-end"),
                     ("pbs", "padding-block-start"), ("pbe", "padding-block-end"), ("pt", "padding-top"), ("pr", "padding-right"), ("pb", "padding-bottom"), ("pl", "padding-left"),
                 })
        {
            string p = property;
            Spacing(name, K("--padding", "--spacing"), v => L(D(p, v)));
        }
        foreach (var v in new[] { "left", "center", "right", "justify", "start", "end" }) S("text-" + v, ("text-align", v));
        Spacing("indent", K("--text-indent", "--spacing"), v => L(D("text-indent", v)), supportsNegative: true);
        foreach (var v in new[] { "baseline", "top", "middle", "bottom", "text-top", "text-bottom", "sub", "super" }) S("align-" + v, ("vertical-align", v));
        Functional("align", new FunctionalOptions { Handle = (v, _) => L(D("vertical-align", v)) });

        var fontWeightProps = Props(P("--tw-font-weight"));
        Fn("font", c =>
        {
            if (c.Value == null || c.Modifier != null) return null;
            if (c.Value.IsArbitrary)
            {
                string v = c.Value.Value;
                switch (c.Value.DataType ?? DataTypes.Infer(v, new[] { "number", "generic-name", "family-name" }))
                {
                    case "generic-name":
                    case "family-name":
                        return L(D("font-family", v));
                    default:
                        return L(fontWeightProps(), D("--tw-font-weight", v), D("font-weight", v));
                }
            }
            var family = _theme.ResolveWith(c.Value.Value, K("--font"), K("--font-feature-settings", "--font-variation-settings"));
            if (family != null)
            {
                var (value, extra) = family.Value;
                extra.TryGetValue("--font-feature-settings", out var ffs);
                extra.TryGetValue("--font-variation-settings", out var fvs);
                return L(D("font-family", value), D("font-feature-settings", ffs), D("font-variation-settings", fvs));
            }
            string? weight = _theme.Resolve(c.Value.Value, K("--font-weight"));
            if (weight != null) return L(fontWeightProps(), D("--tw-font-weight", weight), D("font-weight", weight));
            return null;
        });
        Functional("font-features", new FunctionalOptions { Handle = (v, _) => L(D("font-feature-settings", v)) });
        S("uppercase", ("text-transform", "uppercase"));
        S("lowercase", ("text-transform", "lowercase"));
        S("capitalize", ("text-transform", "capitalize"));
        S("normal-case", ("text-transform", "none"));
        S("italic", ("font-style", "italic"));
        S("not-italic", ("font-style", "normal"));
        S("underline", ("text-decoration-line", "underline"));
        S("overline", ("text-decoration-line", "overline"));
        S("line-through", ("text-decoration-line", "line-through"));
        S("no-underline", ("text-decoration-line", "none"));
        foreach (var v in new[] { "normal", "ultra-condensed", "extra-condensed", "condensed", "semi-condensed", "semi-expanded", "expanded", "extra-expanded", "ultra-expanded" })
            S("font-stretch-" + v, ("font-stretch", v));
        Functional("font-stretch", new FunctionalOptions
        {
            HandleBareValue = v =>
            {
                string a = v.Value;
                if (!a.EndsWith("%", StringComparison.Ordinal)) return null;
                double n = Js.Number(a.Substring(0, a.Length - 1));
                if (!Js.IsPositiveInteger(n) || double.IsNaN(n) || n < 50 || n > 200) return null;
                return a;
            },
            Handle = (v, _) => L(D("font-stretch", v)),
        });
        Color("placeholder", K("--placeholder-color", "--color"), v => L(Rule("&::placeholder", D("--tw-sort", "placeholder-color"), D("color", v))));
        foreach (var v in new[] { "solid", "double", "dotted", "dashed", "wavy" }) S("decoration-" + v, ("text-decoration-style", v));
        S("decoration-auto", ("text-decoration-thickness", "auto"));
        S("decoration-from-font", ("text-decoration-thickness", "from-font"));
        Fn("decoration", c =>
        {
            if (c.Value == null) return null;
            if (c.Value.IsArbitrary)
            {
                string v = c.Value.Value;
                switch (c.Value.DataType ?? DataTypes.Infer(v, new[] { "color", "length", "percentage" }))
                {
                    case "length":
                    case "percentage":
                        return c.Modifier != null ? null : L(D("text-decoration-thickness", v));
                    default:
                    {
                        string? color = Colors.AsColor(v, c.Modifier, _theme);
                        return color == null ? null : L(D("text-decoration-color", color));
                    }
                }
            }
            {
                string? thickness = _theme.Resolve(c.Value.Value, K("--text-decoration-thickness"));
                if (thickness != null) return c.Modifier != null ? null : L(D("text-decoration-thickness", thickness));
                if (Js.IsPositiveInteger(c.Value.Value)) return c.Modifier != null ? null : L(D("text-decoration-thickness", c.Value.Value + "px"));
            }
            string? col = Colors.ResolveThemeColor(c, _theme, K("--text-decoration-color", "--color"));
            return col != null ? L(D("text-decoration-color", col)) : null;
        });
        Functional("animate", new FunctionalOptions { ThemeKeys = K("--animate"), Handle = (v, _) => L(D("animation", v)), StaticValues = Statics(("none", L(D("animation", "none")))) });
    }
}
