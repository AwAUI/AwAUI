using System;
using System.Collections.Generic;
using AwAUI.Tailwind.Ast;
using AwAUI.Tailwind.Internal;

namespace AwAUI.Tailwind.Compilation;

internal sealed partial class CoreUtilities
{
    const string MaskImage = "var(--tw-mask-linear), var(--tw-mask-radial), var(--tw-mask-conic)";

    void RegisterMasks()
    {
        S("mask-none", ("mask-image", "none"));
        Fn("mask", c =>
        {
            if (c.Value == null || c.Modifier != null || !c.Value.IsArbitrary) return null;
            string v = c.Value.Value;
            switch (c.Value.DataType ?? DataTypes.Infer(v, new[] { "image", "percentage", "position", "bg-size", "length", "url" }))
            {
                case "percentage":
                case "position":
                    return L(D("mask-position", v));
                case "bg-size":
                case "length":
                case "size":
                    return L(D("mask-size", v));
                default:
                    return L(D("mask-image", v));
            }
        });
        S("mask-add", ("mask-composite", "add"));
        S("mask-subtract", ("mask-composite", "subtract"));
        S("mask-intersect", ("mask-composite", "intersect"));
        S("mask-exclude", ("mask-composite", "exclude"));
        S("mask-alpha", ("mask-mode", "alpha"));
        S("mask-luminance", ("mask-mode", "luminance"));
        S("mask-match", ("mask-mode", "match-source"));
        S("mask-type-alpha", ("mask-type", "alpha"));
        S("mask-type-luminance", ("mask-type", "luminance"));
        S("mask-auto", ("mask-size", "auto"));
        S("mask-cover", ("mask-size", "cover"));
        S("mask-contain", ("mask-size", "contain"));
        Functional("mask-size", new FunctionalOptions { Handle = (v, _) => L(D("mask-size", v)) });
        foreach (var (n, v) in new[] { ("top", "top"), ("top-left", "left top"), ("top-right", "right top"), ("bottom", "bottom"), ("bottom-left", "left bottom"), ("bottom-right", "right bottom"), ("left", "left"), ("right", "right"), ("center", "center") })
            S("mask-" + n, ("mask-position", v));
        Functional("mask-position", new FunctionalOptions { Handle = (v, _) => L(D("mask-position", v)) });
        foreach (var (n, v) in new[] { ("repeat", "repeat"), ("no-repeat", "no-repeat"), ("repeat-x", "repeat-x"), ("repeat-y", "repeat-y"), ("repeat-round", "round"), ("repeat-space", "space") })
            S("mask-" + n, ("mask-repeat", v));
        foreach (var (n, v) in new[] { ("border", "border-box"), ("padding", "padding-box"), ("content", "content-box"), ("fill", "fill-box"), ("stroke", "stroke-box"), ("view", "view-box") })
            S("mask-clip-" + n, ("mask-clip", v));
        S("mask-no-clip", ("mask-clip", "no-clip"));
        foreach (var (n, v) in new[] { ("border", "border-box"), ("padding", "padding-box"), ("content", "content-box"), ("fill", "fill-box"), ("stroke", "stroke-box"), ("view", "view-box") })
            S("mask-origin-" + n, ("mask-origin", v));

        var maskProps = Props(P("--tw-mask-linear", "linear-gradient(#fff, #fff)"), P("--tw-mask-radial", "linear-gradient(#fff, #fff)"), P("--tw-mask-conic", "linear-gradient(#fff, #fff)"));
        var edgeProps = Props(P("--tw-mask-left", "linear-gradient(#fff, #fff)"), P("--tw-mask-right", "linear-gradient(#fff, #fff)"), P("--tw-mask-bottom", "linear-gradient(#fff, #fff)"), P("--tw-mask-top", "linear-gradient(#fff, #fff)"));

        void MaskStop(string name, Func<string, List<AstNode>> color, Func<string, List<AstNode>> position)
        {
            Fn(name, c =>
            {
                if (c.Value == null) return null;
                if (c.Value.IsArbitrary)
                {
                    string v = c.Value.Value;
                    switch (c.Value.DataType ?? DataTypes.Infer(v, new[] { "length", "percentage", "color" }))
                    {
                        case "color":
                        {
                            string? col = Colors.AsColor(v, c.Modifier, _theme);
                            return col == null ? null : color(col);
                        }
                        case "percentage":
                            return c.Modifier != null || !Js.IsPositiveInteger(v.Slice(0, -1)) ? null : position(v);
                        default:
                            return c.Modifier != null ? null : position(v);
                    }
                }
                {
                    string? col = Colors.ResolveThemeColor(c, _theme, K("--background-color", "--color"));
                    if (col != null) return color(col);
                }
                if (c.Modifier != null) return null;
                string? type = DataTypes.Infer(c.Value.Value, new[] { "number", "percentage" });
                if (type == null) return null;
                if (type == "number")
                    return _theme.Resolve(null, s_spacing) == null || !Js.IsMultipleOfQuarter(c.Value.Value) ? null : position("--spacing(" + c.Value.Value + ")");
                if (type == "percentage") return Js.IsPositiveInteger(c.Value.Value.Slice(0, -1)) ? position(c.Value.Value) : null;
                return null;
            });
        }

        void EdgeMask(string name, string stop, bool top, bool right, bool bottom, bool left)
        {
            List<AstNode> Build(string value, string kind)
            {
                var list = L(maskProps(), edgeProps(), D("mask-image", MaskImage), D("mask-composite", "intersect"),
                    D("--tw-mask-linear", "var(--tw-mask-left), var(--tw-mask-right), var(--tw-mask-bottom), var(--tw-mask-top)"));
                foreach (var (side, enabled) in new[] { ("top", top), ("right", right), ("bottom", bottom), ("left", left) })
                {
                    if (!enabled) continue;
                    list.Add(D("--tw-mask-" + side, "linear-gradient(to " + side + ", var(--tw-mask-" + side + "-from-color) var(--tw-mask-" + side + "-from-position), var(--tw-mask-" + side + "-to-color) var(--tw-mask-" + side + "-to-position))"));
                    list.Add(AtRoot(Prop("--tw-mask-" + side + "-from-position", "0%"), Prop("--tw-mask-" + side + "-to-position", "100%"), Prop("--tw-mask-" + side + "-from-color", "black"), Prop("--tw-mask-" + side + "-to-color", "transparent")));
                    list.Add(D("--tw-mask-" + side + "-" + stop + "-" + kind, value));
                }
                return list;
            }
            MaskStop(name, v => Build(v, "color"), v => Build(v, "position"));
        }
        EdgeMask("mask-x-from", "from", false, true, false, true);
        EdgeMask("mask-x-to", "to", false, true, false, true);
        EdgeMask("mask-y-from", "from", true, false, true, false);
        EdgeMask("mask-y-to", "to", true, false, true, false);
        EdgeMask("mask-t-from", "from", true, false, false, false);
        EdgeMask("mask-t-to", "to", true, false, false, false);
        EdgeMask("mask-r-from", "from", false, true, false, false);
        EdgeMask("mask-r-to", "to", false, true, false, false);
        EdgeMask("mask-b-from", "from", false, false, true, false);
        EdgeMask("mask-b-to", "to", false, false, true, false);
        EdgeMask("mask-l-from", "from", false, false, false, true);
        EdgeMask("mask-l-to", "to", false, false, false, true);

        var linearProps = Props(P("--tw-mask-linear-position", "0deg"), P("--tw-mask-linear-from-position", "0%"), P("--tw-mask-linear-to-position", "100%"), P("--tw-mask-linear-from-color", "black"), P("--tw-mask-linear-to-color", "transparent"));
        Func<CandidateValueBare, string?> angleBare(bool negative) => v =>
        {
            if (!Js.IsPositiveInteger(v.Value)) return null;
            double n = Js.Number(v.Value);
            if (n == 0) return "0deg";
            if (n == 1) return negative ? "-1deg" : "1deg";
            return negative ? "calc(1deg * -" + v.Value + ")" : "calc(1deg * " + v.Value + ")";
        };
        Functional("mask-linear", new FunctionalOptions
        {
            HasDefaultValue = true,
            DefaultValue = null,
            SupportsNegative = true,
            HandleBareValue = v => angleBare(false)(new CandidateValueBare(v.Value)),
            HandleNegativeBareValue = v => angleBare(true)(new CandidateValueBare(v.Value)),
            Handle = (v, _) => L(maskProps(), linearProps(), D("mask-image", MaskImage), D("mask-composite", "intersect"),
                D("--tw-mask-linear", "linear-gradient(var(--tw-mask-linear-stops, var(--tw-mask-linear-position)))"), D("--tw-mask-linear-position", v)),
        });
        const string LinearStops = "var(--tw-mask-linear-position), var(--tw-mask-linear-from-color) var(--tw-mask-linear-from-position), var(--tw-mask-linear-to-color) var(--tw-mask-linear-to-position)";
        foreach (var stop in new[] { "from", "to" })
        {
            string s = stop;
            List<AstNode> Build(string v, string kind) => L(maskProps(), linearProps(), D("mask-image", MaskImage), D("mask-composite", "intersect"),
                D("--tw-mask-linear-stops", LinearStops), D("--tw-mask-linear", "linear-gradient(var(--tw-mask-linear-stops))"), D("--tw-mask-linear-" + s + "-" + kind, v));
            MaskStop("mask-linear-" + s, v => Build(v, "color"), v => Build(v, "position"));
        }

        var radialProps = Props(P("--tw-mask-radial-from-position", "0%"), P("--tw-mask-radial-to-position", "100%"), P("--tw-mask-radial-from-color", "black"), P("--tw-mask-radial-to-color", "transparent"),
            P("--tw-mask-radial-shape", "ellipse"), P("--tw-mask-radial-size", "farthest-corner"), P("--tw-mask-radial-position", "center"));
        S("mask-circle", ("--tw-mask-radial-shape", "circle"));
        S("mask-ellipse", ("--tw-mask-radial-shape", "ellipse"));
        foreach (var v in new[] { "closest-side", "farthest-side", "closest-corner", "farthest-corner" }) S("mask-radial-" + v, ("--tw-mask-radial-size", v));
        foreach (var (n, v) in new[] { ("top", "top"), ("top-left", "top left"), ("top-right", "top right"), ("bottom", "bottom"), ("bottom-left", "bottom left"), ("bottom-right", "bottom right"), ("left", "left"), ("right", "right"), ("center", "center") })
            S("mask-radial-at-" + n, ("--tw-mask-radial-position", v));
        Functional("mask-radial-at", new FunctionalOptions { HasDefaultValue = true, DefaultValue = null, Handle = (v, _) => L(D("--tw-mask-radial-position", v)) });
        Functional("mask-radial", new FunctionalOptions
        {
            HasDefaultValue = true,
            DefaultValue = null,
            Handle = (v, _) => L(maskProps(), radialProps(), D("mask-image", MaskImage), D("mask-composite", "intersect"),
                D("--tw-mask-radial", "radial-gradient(var(--tw-mask-radial-stops, var(--tw-mask-radial-size)))"), D("--tw-mask-radial-size", v)),
        });
        const string RadialStops = "var(--tw-mask-radial-shape) var(--tw-mask-radial-size) at var(--tw-mask-radial-position), var(--tw-mask-radial-from-color) var(--tw-mask-radial-from-position), var(--tw-mask-radial-to-color) var(--tw-mask-radial-to-position)";
        foreach (var stop in new[] { "from", "to" })
        {
            string s = stop;
            List<AstNode> Build(string v, string kind) => L(maskProps(), radialProps(), D("mask-image", MaskImage), D("mask-composite", "intersect"),
                D("--tw-mask-radial-stops", RadialStops), D("--tw-mask-radial", "radial-gradient(var(--tw-mask-radial-stops))"), D("--tw-mask-radial-" + s + "-" + kind, v));
            MaskStop("mask-radial-" + s, v => Build(v, "color"), v => Build(v, "position"));
        }

        var conicProps = Props(P("--tw-mask-conic-position", "0deg"), P("--tw-mask-conic-from-position", "0%"), P("--tw-mask-conic-to-position", "100%"), P("--tw-mask-conic-from-color", "black"), P("--tw-mask-conic-to-color", "transparent"));
        Functional("mask-conic", new FunctionalOptions
        {
            HasDefaultValue = true,
            DefaultValue = null,
            SupportsNegative = true,
            HandleBareValue = v => angleBare(false)(new CandidateValueBare(v.Value)),
            HandleNegativeBareValue = v => angleBare(true)(new CandidateValueBare(v.Value)),
            Handle = (v, _) => L(maskProps(), conicProps(), D("mask-image", MaskImage), D("mask-composite", "intersect"),
                D("--tw-mask-conic", "conic-gradient(var(--tw-mask-conic-stops, var(--tw-mask-conic-position)))"), D("--tw-mask-conic-position", v)),
        });
        const string ConicStops = "from var(--tw-mask-conic-position), var(--tw-mask-conic-from-color) var(--tw-mask-conic-from-position), var(--tw-mask-conic-to-color) var(--tw-mask-conic-to-position)";
        foreach (var stop in new[] { "from", "to" })
        {
            string s = stop;
            List<AstNode> Build(string v, string kind) => L(maskProps(), conicProps(), D("mask-image", MaskImage), D("mask-composite", "intersect"),
                D("--tw-mask-conic-stops", ConicStops), D("--tw-mask-conic", "conic-gradient(var(--tw-mask-conic-stops))"), D("--tw-mask-conic-" + s + "-" + kind, v));
            MaskStop("mask-conic-" + s, v => Build(v, "color"), v => Build(v, "position"));
        }
    }

    readonly struct CandidateValueBare
    {
        public readonly string Value;

        public CandidateValueBare(string value) => Value = value;
    }
}
