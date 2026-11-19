using System;
using System.Collections.Generic;
using AwAUI.Tailwind.Ast;
using AwAUI.Tailwind.Candidates;
using AwAUI.Tailwind.Internal;

namespace AwAUI.Tailwind.Compilation;

internal sealed partial class CoreUtilities
{
    void RegisterBorders()
    {
        foreach (var (name, properties) in new[]
                 {
                     ("rounded", new[] { "border-radius" }),
                     ("rounded-s", new[] { "border-start-start-radius", "border-end-start-radius" }),
                     ("rounded-e", new[] { "border-start-end-radius", "border-end-end-radius" }),
                     ("rounded-t", new[] { "border-top-left-radius", "border-top-right-radius" }),
                     ("rounded-r", new[] { "border-top-right-radius", "border-bottom-right-radius" }),
                     ("rounded-b", new[] { "border-bottom-right-radius", "border-bottom-left-radius" }),
                     ("rounded-l", new[] { "border-top-left-radius", "border-bottom-left-radius" }),
                     ("rounded-ss", new[] { "border-start-start-radius" }),
                     ("rounded-se", new[] { "border-start-end-radius" }),
                     ("rounded-ee", new[] { "border-end-end-radius" }),
                     ("rounded-es", new[] { "border-end-start-radius" }),
                     ("rounded-tl", new[] { "border-top-left-radius" }),
                     ("rounded-tr", new[] { "border-top-right-radius" }),
                     ("rounded-br", new[] { "border-bottom-right-radius" }),
                     ("rounded-bl", new[] { "border-bottom-left-radius" }),
                 })
        {
            var props = properties;
            List<AstNode> All(string v)
            {
                var list = new List<AstNode>(props.Length);
                foreach (var p in props) list.Add(D(p, v));
                return list;
            }
            Functional(name, new FunctionalOptions
            {
                ThemeKeys = K("--radius"),
                Handle = (v, _) => All(v),
                StaticValues = Statics(("none", All("0")), ("full", All("calc(infinity * 1px)"))),
            });
        }

        foreach (var s in new[] { "solid", "dashed", "dotted", "double", "hidden", "none" }) S("border-" + s, ("--tw-border-style", s), ("border-style", s));

        var borderProps = Props(P("--tw-border-style", "solid"));
        void BorderSide(string name, string widthProp, string styleProp, string colorProp)
        {
            List<AstNode> Width(string v) => L(borderProps(), D(styleProp, "var(--tw-border-style)"), D(widthProp, v));
            List<AstNode> ColorDecl(string v) => L(D(colorProp, v));
            Fn(name, c =>
            {
                if (c.Value == null)
                {
                    if (c.Modifier != null) return null;
                    string w = _theme.Get("--default-border-width") ?? "1px";
                    return Width(w);
                }
                if (c.Value.IsArbitrary)
                {
                    string v = c.Value.Value;
                    string? type = c.Value.DataType ?? DataTypes.Infer(v, new[] { "color", "line-width", "length" });
                    if (type == "line-width" || type == "length") return c.Modifier != null ? null : Width(v);
                    string? color = Colors.AsColor(v, c.Modifier, _theme);
                    return color == null ? null : ColorDecl(color);
                }
                {
                    string? color = Colors.ResolveThemeColor(c, _theme, K("--border-color", "--color"));
                    if (color != null) return ColorDecl(color);
                }
                if (c.Modifier != null) return null;
                string? width = _theme.Resolve(c.Value.Value, K("--border-width"));
                if (width != null) return Width(width);
                if (Js.IsPositiveInteger(c.Value.Value)) return Width(c.Value.Value + "px");
                return null;
            });
        }
        BorderSide("border", "border-width", "border-style", "border-color");
        BorderSide("border-x", "border-inline-width", "border-inline-style", "border-inline-color");
        BorderSide("border-y", "border-block-width", "border-block-style", "border-block-color");
        BorderSide("border-s", "border-inline-start-width", "border-inline-start-style", "border-inline-start-color");
        BorderSide("border-e", "border-inline-end-width", "border-inline-end-style", "border-inline-end-color");
        BorderSide("border-bs", "border-block-start-width", "border-block-start-style", "border-block-start-color");
        BorderSide("border-be", "border-block-end-width", "border-block-end-style", "border-block-end-color");
        BorderSide("border-t", "border-top-width", "border-top-style", "border-top-color");
        BorderSide("border-r", "border-right-width", "border-right-style", "border-right-color");
        BorderSide("border-b", "border-bottom-width", "border-bottom-style", "border-bottom-color");
        BorderSide("border-l", "border-left-width", "border-left-style", "border-left-color");

        string defaultBorder = _theme.Get("--default-border-width") ?? "1px";
        var divideX = Props(P("--tw-divide-x-reverse", "0"));
        var divideY = Props(P("--tw-divide-y-reverse", "0"));
        Functional("divide-x", new FunctionalOptions
        {
            HasDefaultValue = true,
            DefaultValue = defaultBorder,
            ThemeKeys = K("--divide-width", "--border-width"),
            HandleBareValue = BareSuffix("px"),
            Handle = (v, _) => L(divideX(), Rule(":where(& > :not(:last-child))",
                D("--tw-sort", "divide-x-width"),
                borderProps(),
                D("--tw-divide-x-reverse", "0"),
                D("border-inline-style", "var(--tw-border-style)"),
                D("border-inline-start-width", "calc(" + v + " * var(--tw-divide-x-reverse))"),
                D("border-inline-end-width", "calc(" + v + " * calc(1 - var(--tw-divide-x-reverse)))"))),
        });
        Functional("divide-y", new FunctionalOptions
        {
            HasDefaultValue = true,
            DefaultValue = defaultBorder,
            ThemeKeys = K("--divide-width", "--border-width"),
            HandleBareValue = BareSuffix("px"),
            Handle = (v, _) => L(divideY(), Rule(":where(& > :not(:last-child))",
                D("--tw-sort", "divide-y-width"),
                borderProps(),
                D("--tw-divide-y-reverse", "0"),
                D("border-bottom-style", "var(--tw-border-style)"),
                D("border-top-style", "var(--tw-border-style)"),
                D("border-top-width", "calc(" + v + " * var(--tw-divide-y-reverse))"),
                D("border-bottom-width", "calc(" + v + " * calc(1 - var(--tw-divide-y-reverse)))"))),
        });
        S("divide-x-reverse", () => L(divideX(), Rule(":where(& > :not(:last-child))", D("--tw-divide-x-reverse", "1"))));
        S("divide-y-reverse", () => L(divideY(), Rule(":where(& > :not(:last-child))", D("--tw-divide-y-reverse", "1"))));
        foreach (var s in new[] { "solid", "dashed", "dotted", "double", "none" })
        {
            string st = s;
            S("divide-" + st, () => L(Rule(":where(& > :not(:last-child))", D("--tw-sort", "divide-style"), D("--tw-border-style", st), D("border-style", st))));
        }
    }

    void RegisterBackgrounds()
    {
        S("bg-auto", ("background-size", "auto"));
        S("bg-cover", ("background-size", "cover"));
        S("bg-contain", ("background-size", "contain"));
        Functional("bg-size", new FunctionalOptions { Handle = (v, _) => L(D("background-size", v)) });
        S("bg-fixed", ("background-attachment", "fixed"));
        S("bg-local", ("background-attachment", "local"));
        S("bg-scroll", ("background-attachment", "scroll"));
        foreach (var (n, v) in new[] { ("top", "top"), ("top-left", "left top"), ("top-right", "right top"), ("bottom", "bottom"), ("bottom-left", "left bottom"), ("bottom-right", "right bottom"), ("left", "left"), ("right", "right"), ("center", "center") })
            S("bg-" + n, ("background-position", v));
        Functional("bg-position", new FunctionalOptions { Handle = (v, _) => L(D("background-position", v)) });
        foreach (var (n, v) in new[] { ("repeat", "repeat"), ("no-repeat", "no-repeat"), ("repeat-x", "repeat-x"), ("repeat-y", "repeat-y"), ("repeat-round", "round"), ("repeat-space", "space") })
            S("bg-" + n, ("background-repeat", v));
        S("bg-none", ("background-image", "none"));

        string Interpolation(CandidateModifier? m)
        {
            string interpolation = "in oklab";
            if (m != null && !m.IsArbitrary)
            {
                switch (m.Value)
                {
                    case "longer":
                    case "shorter":
                    case "increasing":
                    case "decreasing":
                        interpolation = "in oklch " + m.Value + " hue";
                        break;
                    default:
                        interpolation = "in " + m.Value;
                        break;
                }
            }
            else if (m != null) interpolation = m.Value;
            return interpolation;
        }
        var directions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["to-t"] = "to top", ["to-tr"] = "to top right", ["to-r"] = "to right", ["to-br"] = "to bottom right",
            ["to-b"] = "to bottom", ["to-bl"] = "to bottom left", ["to-l"] = "to left", ["to-tl"] = "to top left",
        };
        UtilityCompileFn Linear(bool negative) => c =>
        {
            if (c.Value == null) return null;
            if (c.Value.IsArbitrary)
            {
                if (c.Modifier != null) return null;
                string v = c.Value.Value;
                string? type = c.Value.DataType ?? DataTypes.Infer(v, new[] { "angle" });
                if (type == "angle")
                {
                    v = negative ? "calc(" + v + " * -1)" : v;
                    return L(D("--tw-gradient-position", v), D("background-image", "linear-gradient(var(--tw-gradient-stops," + v + "))"));
                }
                if (negative) return null;
                return L(D("--tw-gradient-position", v), D("background-image", "linear-gradient(var(--tw-gradient-stops," + v + "))"));
            }
            string value = c.Value.Value;
            if (!negative && directions.TryGetValue(value, out var dir)) value = dir;
            else if (Js.IsPositiveInteger(value)) value = negative ? "calc(" + value + "deg * -1)" : value + "deg";
            else return null;
            string interpolation = Interpolation(c.Modifier);
            return L(
                D("--tw-gradient-position", value),
                AstNode.RuleOrAtRule("@supports (background-image: linear-gradient(in lab, red, red))", L(D("--tw-gradient-position", value + " " + interpolation))),
                D("background-image", "linear-gradient(var(--tw-gradient-stops))"));
        };
        UtilityCompileFn Conic(bool negative) => c =>
        {
            if (c.Value != null && c.Value.IsArbitrary)
            {
                if (c.Modifier != null) return null;
                string v = c.Value.Value;
                return L(D("--tw-gradient-position", v), D("background-image", "conic-gradient(var(--tw-gradient-stops," + v + "))"));
            }
            string interpolation = Interpolation(c.Modifier);
            if (c.Value == null) return L(D("--tw-gradient-position", interpolation), D("background-image", "conic-gradient(var(--tw-gradient-stops))"));
            string value = c.Value.Value;
            if (!Js.IsPositiveInteger(value)) return null;
            value = negative ? "calc(" + value + "deg * -1)" : value + "deg";
            return L(D("--tw-gradient-position", "from " + value + " " + interpolation), D("background-image", "conic-gradient(var(--tw-gradient-stops))"));
        };
        Fn("-bg-linear", Linear(true));
        Fn("bg-linear", Linear(false));
        Fn("-bg-conic", Conic(true));
        Fn("bg-conic", Conic(false));
        Fn("bg-radial", c =>
        {
            if (c.Value == null) return L(D("--tw-gradient-position", Interpolation(c.Modifier)), D("background-image", "radial-gradient(var(--tw-gradient-stops))"));
            if (c.Value.IsArbitrary)
            {
                if (c.Modifier != null) return null;
                string v = c.Value.Value;
                return L(D("--tw-gradient-position", v), D("background-image", "radial-gradient(var(--tw-gradient-stops," + v + "))"));
            }
            return null;
        });

        Fn("bg", c =>
        {
            if (c.Value == null) return null;
            if (c.Value.IsArbitrary)
            {
                string v = c.Value.Value;
                switch (c.Value.DataType ?? DataTypes.Infer(v, new[] { "image", "color", "percentage", "position", "bg-size", "length", "url" }))
                {
                    case "percentage":
                    case "position":
                        return c.Modifier != null ? null : L(D("background-position", v));
                    case "bg-size":
                    case "length":
                    case "size":
                        return c.Modifier != null ? null : L(D("background-size", v));
                    case "image":
                    case "url":
                        return c.Modifier != null ? null : L(D("background-image", v));
                    default:
                    {
                        string? color = Colors.AsColor(v, c.Modifier, _theme);
                        return color == null ? null : L(D("background-color", color));
                    }
                }
            }
            {
                string? color = Colors.ResolveThemeColor(c, _theme, K("--background-color", "--color"));
                if (color != null) return L(D("background-color", color));
            }
            if (c.Modifier != null) return null;
            string? image = _theme.Resolve(c.Value.Value, K("--background-image"));
            return image != null ? L(D("background-image", image)) : null;
        });

        var gradientProps = Props(
            P("--tw-gradient-position"), P("--tw-gradient-from", "#0000", "<color>"), P("--tw-gradient-via", "#0000", "<color>"), P("--tw-gradient-to", "#0000", "<color>"),
            P("--tw-gradient-stops"), P("--tw-gradient-via-stops"), P("--tw-gradient-from-position", "0%", "<length-percentage>"),
            P("--tw-gradient-via-position", "50%", "<length-percentage>"), P("--tw-gradient-to-position", "100%", "<length-percentage>"));
        void GradientStop(string name, Func<string, List<AstNode>> color, Func<string, List<AstNode>> position)
        {
            Fn(name, c =>
            {
                if (c.Value == null) return null;
                if (c.Value.IsArbitrary)
                {
                    string v = c.Value.Value;
                    switch (c.Value.DataType ?? DataTypes.Infer(v, new[] { "color", "length", "percentage" }))
                    {
                        case "length":
                        case "percentage":
                            return c.Modifier != null ? null : position(v);
                        default:
                        {
                            string? col = Colors.AsColor(v, c.Modifier, _theme);
                            return col == null ? null : color(col);
                        }
                    }
                }
                {
                    string? col = Colors.ResolveThemeColor(c, _theme, K("--background-color", "--color"));
                    if (col != null) return color(col);
                }
                if (c.Modifier != null) return null;
                string? pos = _theme.Resolve(c.Value.Value, K("--gradient-color-stop-positions"));
                if (pos != null) return position(pos);
                string val = c.Value.Value;
                if (val.Length > 0 && val[val.Length - 1] == '%' && Js.IsPositiveInteger(val.Substring(0, val.Length - 1))) return position(val);
                return null;
            });
        }
        const string Stops = "var(--tw-gradient-via-stops, var(--tw-gradient-position), var(--tw-gradient-from) var(--tw-gradient-from-position), var(--tw-gradient-to) var(--tw-gradient-to-position))";
        GradientStop("from",
            v => L(gradientProps(), D("--tw-sort", "--tw-gradient-from"), D("--tw-gradient-from", v), D("--tw-gradient-stops", Stops)),
            v => L(gradientProps(), D("--tw-gradient-from-position", v)));
        S("via-none", ("--tw-gradient-via-stops", "initial"));
        GradientStop("via",
            v => L(gradientProps(), D("--tw-sort", "--tw-gradient-via"), D("--tw-gradient-via", v),
                D("--tw-gradient-via-stops", "var(--tw-gradient-position), var(--tw-gradient-from) var(--tw-gradient-from-position), var(--tw-gradient-via) var(--tw-gradient-via-position), var(--tw-gradient-to) var(--tw-gradient-to-position)"),
                D("--tw-gradient-stops", "var(--tw-gradient-via-stops)")),
            v => L(gradientProps(), D("--tw-gradient-via-position", v)));
        GradientStop("to",
            v => L(gradientProps(), D("--tw-sort", "--tw-gradient-to"), D("--tw-gradient-to", v), D("--tw-gradient-stops", Stops)),
            v => L(gradientProps(), D("--tw-gradient-to-position", v)));

        S("box-decoration-slice", ("-webkit-box-decoration-break", "slice"), ("box-decoration-break", "slice"));
        S("box-decoration-clone", ("-webkit-box-decoration-break", "clone"), ("box-decoration-break", "clone"));
        S("bg-clip-text", ("background-clip", "text"));
        S("bg-clip-border", ("background-clip", "border-box"));
        S("bg-clip-padding", ("background-clip", "padding-box"));
        S("bg-clip-content", ("background-clip", "content-box"));
        S("bg-origin-border", ("background-origin", "border-box"));
        S("bg-origin-padding", ("background-origin", "padding-box"));
        S("bg-origin-content", ("background-origin", "content-box"));
        foreach (var v in new[] { "normal", "multiply", "screen", "overlay", "darken", "lighten", "color-dodge", "color-burn", "hard-light", "soft-light", "difference", "exclusion", "hue", "saturation", "color", "luminosity" })
        {
            S("bg-blend-" + v, ("background-blend-mode", v));
            S("mix-blend-" + v, ("mix-blend-mode", v));
        }
        S("mix-blend-plus-darker", ("mix-blend-mode", "plus-darker"));
        S("mix-blend-plus-lighter", ("mix-blend-mode", "plus-lighter"));
    }
}
