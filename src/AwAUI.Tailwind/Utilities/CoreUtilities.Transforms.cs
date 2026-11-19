using System.Collections.Generic;
using AwAUI.Tailwind.Ast;
using AwAUI.Tailwind.Candidates;
using AwAUI.Tailwind.Internal;

namespace AwAUI.Tailwind.Compilation;

internal sealed partial class CoreUtilities
{
    const string TranslateXY = "var(--tw-translate-x) var(--tw-translate-y)";
    const string TranslateXYZ = "var(--tw-translate-x) var(--tw-translate-y) var(--tw-translate-z)";
    const string TransformValue = "var(--tw-rotate-x, ) var(--tw-rotate-y, ) var(--tw-rotate-z, ) var(--tw-skew-x, ) var(--tw-skew-y, )";

    void RegisterTransforms()
    {
        var translateProps = Props(P("--tw-translate-x", "0"), P("--tw-translate-y", "0"), P("--tw-translate-z", "0"));
        S("translate-none", ("translate", "none"));
        S("-translate-full", () => L(translateProps(), D("--tw-translate-x", "-100%"), D("--tw-translate-y", "-100%"), D("translate", TranslateXY)));
        S("translate-full", () => L(translateProps(), D("--tw-translate-x", "100%"), D("--tw-translate-y", "100%"), D("translate", TranslateXY)));
        Spacing("translate", K("--translate", "--spacing"), v => L(translateProps(), D("--tw-translate-x", v), D("--tw-translate-y", v), D("translate", TranslateXY)), supportsNegative: true, supportsFractions: true);
        foreach (var axis in new[] { "x", "y" })
        {
            string a = axis;
            S("-translate-" + a + "-full", () => L(translateProps(), D("--tw-translate-" + a, "-100%"), D("translate", TranslateXY)));
            S("translate-" + a + "-full", () => L(translateProps(), D("--tw-translate-" + a, "100%"), D("translate", TranslateXY)));
            Spacing("translate-" + a, K("--translate", "--spacing"), v => L(translateProps(), D("--tw-translate-" + a, v), D("translate", TranslateXY)), supportsNegative: true, supportsFractions: true);
        }
        Spacing("translate-z", K("--translate", "--spacing"), v => L(translateProps(), D("--tw-translate-z", v), D("translate", TranslateXYZ)), supportsNegative: true);
        S("translate-3d", () => L(translateProps(), D("translate", TranslateXYZ)));

        var scaleProps = Props(P("--tw-scale-x", "1"), P("--tw-scale-y", "1"), P("--tw-scale-z", "1"));
        S("scale-none", ("scale", "none"));
        UtilityCompileFn Scale(bool negative) => c =>
        {
            if (c.Value == null || c.Modifier != null) return null;
            string? v;
            if (c.Value.IsArbitrary)
            {
                v = c.Value.Value;
                v = negative ? "calc(" + v + " * -1)" : v;
                return L(D("scale", v));
            }
            v = _theme.Resolve(c.Value.Value, K("--scale"));
            if (v == null && Js.IsPositiveInteger(c.Value.Value)) v = c.Value.Value + "%";
            if (v == null) return null;
            v = negative ? "calc(" + v + " * -1)" : v;
            return L(scaleProps(), D("--tw-scale-x", v), D("--tw-scale-y", v), D("--tw-scale-z", v), D("scale", "var(--tw-scale-x) var(--tw-scale-y)"));
        };
        Fn("-scale", Scale(true));
        Fn("scale", Scale(false));
        foreach (var axis in new[] { "x", "y", "z" })
        {
            string a = axis;
            Functional("scale-" + a, new FunctionalOptions
            {
                SupportsNegative = true,
                ThemeKeys = K("--scale"),
                HandleBareValue = BareSuffix("%"),
                Handle = (v, _) => L(scaleProps(), D("--tw-scale-" + a, v), D("scale", "var(--tw-scale-x) var(--tw-scale-y)" + (a == "z" ? " var(--tw-scale-z)" : ""))),
            });
        }
        S("scale-3d", () => L(scaleProps(), D("scale", "var(--tw-scale-x) var(--tw-scale-y) var(--tw-scale-z)")));

        S("rotate-none", ("rotate", "none"));
        UtilityCompileFn Rotate(bool negative) => c =>
        {
            if (c.Value == null || c.Modifier != null) return null;
            string? v;
            if (c.Value.IsArbitrary)
            {
                v = c.Value.Value;
                string? type = c.Value.DataType ?? DataTypes.Infer(v, new[] { "angle", "vector" });
                if (type == "vector") return L(D("rotate", v + " var(--tw-rotate)"));
                if (type != "angle") return L(D("rotate", negative ? "calc(" + v + " * -1)" : v));
            }
            else
            {
                v = _theme.Resolve(c.Value.Value, K("--rotate"));
                if (v == null && Js.IsPositiveInteger(c.Value.Value)) v = c.Value.Value + "deg";
                if (v == null) return null;
            }
            return L(D("rotate", negative ? "calc(" + v + " * -1)" : v));
        };
        Fn("-rotate", Rotate(true));
        Fn("rotate", Rotate(false));

        var transformProps = Props(P("--tw-rotate-x"), P("--tw-rotate-y"), P("--tw-rotate-z"), P("--tw-skew-x"), P("--tw-skew-y"));
        foreach (var axis in new[] { "x", "y", "z" })
        {
            string a = axis;
            Functional("rotate-" + a, new FunctionalOptions
            {
                SupportsNegative = true,
                ThemeKeys = K("--rotate"),
                HandleBareValue = BareSuffix("deg"),
                Handle = (v, _) => L(transformProps(), D("--tw-rotate-" + a, "rotate" + a.ToUpperInvariant() + "(" + v + ")"), D("transform", TransformValue)),
            });
        }
        Functional("skew", new FunctionalOptions { SupportsNegative = true, ThemeKeys = K("--skew"), HandleBareValue = BareSuffix("deg"), Handle = (v, _) => L(transformProps(), D("--tw-skew-x", "skewX(" + v + ")"), D("--tw-skew-y", "skewY(" + v + ")"), D("transform", TransformValue)) });
        Functional("skew-x", new FunctionalOptions { SupportsNegative = true, ThemeKeys = K("--skew"), HandleBareValue = BareSuffix("deg"), Handle = (v, _) => L(transformProps(), D("--tw-skew-x", "skewX(" + v + ")"), D("transform", TransformValue)) });
        Functional("skew-y", new FunctionalOptions { SupportsNegative = true, ThemeKeys = K("--skew"), HandleBareValue = BareSuffix("deg"), Handle = (v, _) => L(transformProps(), D("--tw-skew-y", "skewY(" + v + ")"), D("transform", TransformValue)) });
        Fn("transform", c =>
        {
            if (c.Modifier != null) return null;
            string? v = null;
            if (c.Value == null) v = TransformValue;
            else if (c.Value.IsArbitrary) v = c.Value.Value;
            return v == null ? null : L(transformProps(), D("transform", v));
        });
        S("transform-cpu", ("transform", TransformValue));
        S("transform-gpu", ("transform", "translateZ(0) " + TransformValue));
        S("transform-none", ("transform", "none"));
        Functional("zoom", new FunctionalOptions { HandleBareValue = BareSuffix("%"), Handle = (v, _) => L(D("zoom", v)) });
        S("transform-flat", ("transform-style", "flat"));
        S("transform-3d", ("transform-style", "preserve-3d"));
        S("transform-content", ("transform-box", "content-box"));
        S("transform-border", ("transform-box", "border-box"));
        S("transform-fill", ("transform-box", "fill-box"));
        S("transform-stroke", ("transform-box", "stroke-box"));
        S("transform-view", ("transform-box", "view-box"));
        S("backface-visible", ("backface-visibility", "visible"));
        S("backface-hidden", ("backface-visibility", "hidden"));
    }

    void RegisterInteractivity()
    {
        foreach (var c in new[]
                 {
                     "auto", "default", "pointer", "wait", "text", "move", "help", "not-allowed", "none", "context-menu", "progress", "cell", "crosshair",
                     "vertical-text", "alias", "copy", "no-drop", "grab", "grabbing", "all-scroll", "col-resize", "row-resize", "n-resize", "e-resize",
                     "s-resize", "w-resize", "ne-resize", "nw-resize", "se-resize", "sw-resize", "ew-resize", "ns-resize", "nesw-resize", "nwse-resize",
                     "zoom-in", "zoom-out",
                 })
        {
            S("cursor-" + c, ("cursor", c));
        }
        Functional("cursor", new FunctionalOptions { ThemeKeys = K("--cursor"), Handle = (v, _) => L(D("cursor", v)) });
        foreach (var t in new[] { "auto", "none", "manipulation" }) S("touch-" + t, ("touch-action", t));
        var touchProps = Props(P("--tw-pan-x"), P("--tw-pan-y"), P("--tw-pinch-zoom"));
        const string TouchValue = "var(--tw-pan-x, ) var(--tw-pan-y, ) var(--tw-pinch-zoom, )";
        foreach (var t in new[] { "x", "left", "right" })
        {
            string tt = t;
            S("touch-pan-" + tt, () => L(touchProps(), D("--tw-pan-x", "pan-" + tt), D("touch-action", TouchValue)));
        }
        foreach (var t in new[] { "y", "up", "down" })
        {
            string tt = t;
            S("touch-pan-" + tt, () => L(touchProps(), D("--tw-pan-y", "pan-" + tt), D("touch-action", TouchValue)));
        }
        S("touch-pinch-zoom", () => L(touchProps(), D("--tw-pinch-zoom", "pinch-zoom"), D("touch-action", TouchValue)));
        foreach (var v in new[] { "none", "text", "all", "auto" }) S("select-" + v, ("-webkit-user-select", v), ("user-select", v));
        S("resize-none", ("resize", "none"));
        S("resize-x", ("resize", "horizontal"));
        S("resize-y", ("resize", "vertical"));
        S("resize", ("resize", "both"));
        S("snap-none", ("scroll-snap-type", "none"));
        var snapProps = Props(P("--tw-scroll-snap-strictness", "proximity", "*"));
        foreach (var v in new[] { "x", "y", "both" })
        {
            string vv = v;
            S("snap-" + vv, () => L(snapProps(), D("scroll-snap-type", vv + " var(--tw-scroll-snap-strictness)")));
        }
        S("snap-mandatory", () => L(snapProps(), D("--tw-scroll-snap-strictness", "mandatory")));
        S("snap-proximity", () => L(snapProps(), D("--tw-scroll-snap-strictness", "proximity")));
        S("snap-align-none", ("scroll-snap-align", "none"));
        S("snap-start", ("scroll-snap-align", "start"));
        S("snap-end", ("scroll-snap-align", "end"));
        S("snap-center", ("scroll-snap-align", "center"));
        S("snap-normal", ("scroll-snap-stop", "normal"));
        S("snap-always", ("scroll-snap-stop", "always"));
        foreach (var (name, property) in new[]
                 {
                     ("scroll-m", "scroll-margin"), ("scroll-mx", "scroll-margin-inline"), ("scroll-my", "scroll-margin-block"), ("scroll-ms", "scroll-margin-inline-start"),
                     ("scroll-me", "scroll-margin-inline-end"), ("scroll-mbs", "scroll-margin-block-start"), ("scroll-mbe", "scroll-margin-block-end"),
                     ("scroll-mt", "scroll-margin-top"), ("scroll-mr", "scroll-margin-right"), ("scroll-mb", "scroll-margin-bottom"), ("scroll-ml", "scroll-margin-left"),
                 })
        {
            string p = property;
            Spacing(name, K("--scroll-margin", "--spacing"), v => L(D(p, v)), supportsNegative: true);
        }
        foreach (var (name, property) in new[]
                 {
                     ("scroll-p", "scroll-padding"), ("scroll-px", "scroll-padding-inline"), ("scroll-py", "scroll-padding-block"), ("scroll-ps", "scroll-padding-inline-start"),
                     ("scroll-pe", "scroll-padding-inline-end"), ("scroll-pbs", "scroll-padding-block-start"), ("scroll-pbe", "scroll-padding-block-end"),
                     ("scroll-pt", "scroll-padding-top"), ("scroll-pr", "scroll-padding-right"), ("scroll-pb", "scroll-padding-bottom"), ("scroll-pl", "scroll-padding-left"),
                 })
        {
            string p = property;
            Spacing(name, K("--scroll-padding", "--spacing"), v => L(D(p, v)));
        }
        S("list-inside", ("list-style-position", "inside"));
        S("list-outside", ("list-style-position", "outside"));
        Functional("list", new FunctionalOptions
        {
            ThemeKeys = K("--list-style-type"),
            Handle = (v, _) => L(D("list-style-type", v)),
            StaticValues = Statics(("none", L(D("list-style-type", "none"))), ("disc", L(D("list-style-type", "disc"))), ("decimal", L(D("list-style-type", "decimal")))),
        });
        Functional("list-image", new FunctionalOptions { ThemeKeys = K("--list-style-image"), Handle = (v, _) => L(D("list-style-image", v)), StaticValues = Statics(("none", L(D("list-style-image", "none")))) });
        S("appearance-none", ("appearance", "none"));
        S("appearance-auto", ("appearance", "auto"));
        S("scheme-normal", ("color-scheme", "normal"));
        S("scheme-dark", ("color-scheme", "dark"));
        S("scheme-light", ("color-scheme", "light"));
        S("scheme-light-dark", ("color-scheme", "light dark"));
        S("scheme-only-dark", ("color-scheme", "only dark"));
        S("scheme-only-light", ("color-scheme", "only light"));
        Functional("columns", new FunctionalOptions { ThemeKeys = K("--columns", "--container"), HandleBareValue = BareInteger, Handle = (v, _) => L(D("columns", v)), StaticValues = Statics(("auto", L(D("columns", "auto")))) });
        foreach (var v in new[] { "auto", "avoid", "all", "avoid-page", "page", "left", "right", "column" }) S("break-before-" + v, ("break-before", v));
        foreach (var v in new[] { "auto", "avoid", "avoid-page", "avoid-column" }) S("break-inside-" + v, ("break-inside", v));
        foreach (var v in new[] { "auto", "avoid", "all", "avoid-page", "page", "left", "right", "column" }) S("break-after-" + v, ("break-after", v));
    }
}
