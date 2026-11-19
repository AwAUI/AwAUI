using System.Collections.Generic;
using AwAUI.Tailwind.Ast;
using AwAUI.Tailwind.Internal;

namespace AwAUI.Tailwind.Compilation;

internal sealed partial class CoreUtilities
{
    void RegisterLayout()
    {
        S("sr-only", ("position", "absolute"), ("width", "1px"), ("height", "1px"), ("padding", "0"), ("margin", "-1px"), ("overflow", "hidden"), ("clip-path", "inset(50%)"), ("white-space", "nowrap"), ("border-width", "0"));
        S("not-sr-only", ("position", "static"), ("width", "auto"), ("height", "auto"), ("padding", "0"), ("margin", "0"), ("overflow", "visible"), ("clip-path", "none"), ("white-space", "normal"));
        S("pointer-events-none", ("pointer-events", "none"));
        S("pointer-events-auto", ("pointer-events", "auto"));
        S("visible", ("visibility", "visible"));
        S("invisible", ("visibility", "hidden"));
        S("collapse", ("visibility", "collapse"));
        S("static", ("position", "static"));
        S("fixed", ("position", "fixed"));
        S("absolute", ("position", "absolute"));
        S("relative", ("position", "relative"));
        S("sticky", ("position", "sticky"));

        foreach (var (name, property) in new[]
                 {
                     ("inset", "inset"), ("inset-x", "inset-inline"), ("inset-y", "inset-block"), ("inset-s", "inset-inline-start"), ("inset-e", "inset-inline-end"),
                     ("inset-bs", "inset-block-start"), ("inset-be", "inset-block-end"), ("top", "top"), ("right", "right"), ("bottom", "bottom"), ("left", "left"),
                 })
        {
            S(name + "-auto", (property, "auto"));
            S(name + "-full", (property, "100%"));
            S("-" + name + "-full", (property, "-100%"));
            string p = property;
            Spacing(name, K("--inset", "--spacing"), v => L(D(p, v)), supportsNegative: true, supportsFractions: true);
        }

        S("isolate", ("isolation", "isolate"));
        S("isolation-auto", ("isolation", "auto"));
        Functional("z", new FunctionalOptions { SupportsNegative = true, HandleBareValue = BareInteger, ThemeKeys = K("--z-index"), Handle = (v, _) => L(D("z-index", v)), StaticValues = Statics(("auto", L(D("z-index", "auto")))) });
        Functional("order", new FunctionalOptions { SupportsNegative = true, HandleBareValue = BareInteger, ThemeKeys = K("--order"), Handle = (v, _) => L(D("order", v)), StaticValues = Statics(("first", L(D("order", "-9999"))), ("last", L(D("order", "9999")))) });
        Functional("col", new FunctionalOptions { SupportsNegative = true, HandleBareValue = BareInteger, ThemeKeys = K("--grid-column"), Handle = (v, _) => L(D("grid-column", v)), StaticValues = Statics(("auto", L(D("grid-column", "auto")))) });
        Functional("col-span", new FunctionalOptions { HandleBareValue = BareInteger, Handle = (v, _) => L(D("grid-column", "span " + v + " / span " + v)), StaticValues = Statics(("full", L(D("grid-column", "1 / -1")))) });
        Functional("col-start", new FunctionalOptions { SupportsNegative = true, HandleBareValue = BareInteger, ThemeKeys = K("--grid-column-start"), Handle = (v, _) => L(D("grid-column-start", v)), StaticValues = Statics(("auto", L(D("grid-column-start", "auto")))) });
        Functional("col-end", new FunctionalOptions { SupportsNegative = true, HandleBareValue = BareInteger, ThemeKeys = K("--grid-column-end"), Handle = (v, _) => L(D("grid-column-end", v)), StaticValues = Statics(("auto", L(D("grid-column-end", "auto")))) });
        Functional("row", new FunctionalOptions { SupportsNegative = true, HandleBareValue = BareInteger, ThemeKeys = K("--grid-row"), Handle = (v, _) => L(D("grid-row", v)), StaticValues = Statics(("auto", L(D("grid-row", "auto")))) });
        Functional("row-span", new FunctionalOptions { HandleBareValue = BareInteger, Handle = (v, _) => L(D("grid-row", "span " + v + " / span " + v)), StaticValues = Statics(("full", L(D("grid-row", "1 / -1")))) });
        Functional("row-start", new FunctionalOptions { SupportsNegative = true, HandleBareValue = BareInteger, ThemeKeys = K("--grid-row-start"), Handle = (v, _) => L(D("grid-row-start", v)), StaticValues = Statics(("auto", L(D("grid-row-start", "auto")))) });
        Functional("row-end", new FunctionalOptions { SupportsNegative = true, HandleBareValue = BareInteger, ThemeKeys = K("--grid-row-end"), Handle = (v, _) => L(D("grid-row-end", v)), StaticValues = Statics(("auto", L(D("grid-row-end", "auto")))) });

        S("float-start", ("float", "inline-start"));
        S("float-end", ("float", "inline-end"));
        S("float-right", ("float", "right"));
        S("float-left", ("float", "left"));
        S("float-none", ("float", "none"));
        S("clear-start", ("clear", "inline-start"));
        S("clear-end", ("clear", "inline-end"));
        S("clear-right", ("clear", "right"));
        S("clear-left", ("clear", "left"));
        S("clear-both", ("clear", "both"));
        S("clear-none", ("clear", "none"));

        foreach (var (name, property) in new[]
                 {
                     ("m", "margin"), ("mx", "margin-inline"), ("my", "margin-block"), ("ms", "margin-inline-start"), ("me", "margin-inline-end"),
                     ("mbs", "margin-block-start"), ("mbe", "margin-block-end"), ("mt", "margin-top"), ("mr", "margin-right"), ("mb", "margin-bottom"), ("ml", "margin-left"),
                 })
        {
            S(name + "-auto", (property, "auto"));
            string p = property;
            Spacing(name, K("--margin", "--spacing"), v => L(D(p, v)), supportsNegative: true);
        }

        S("box-border", ("box-sizing", "border-box"));
        S("box-content", ("box-sizing", "content-box"));
        Functional("line-clamp", new FunctionalOptions
        {
            ThemeKeys = K("--line-clamp"),
            HandleBareValue = BareInteger,
            Handle = (v, _) => L(D("overflow", "hidden"), D("display", "-webkit-box"), D("-webkit-box-orient", "vertical"), D("-webkit-line-clamp", v)),
            StaticValues = Statics(("none", L(D("overflow", "visible"), D("display", "block"), D("-webkit-box-orient", "horizontal"), D("-webkit-line-clamp", "unset")))),
        });

        foreach (var (name, value) in new[]
                 {
                     ("block", "block"), ("inline-block", "inline-block"), ("inline", "inline"), ("hidden", "none"), ("inline-flex", "inline-flex"), ("table", "table"),
                     ("inline-table", "inline-table"), ("table-caption", "table-caption"), ("table-cell", "table-cell"), ("table-column", "table-column"),
                     ("table-column-group", "table-column-group"), ("table-footer-group", "table-footer-group"), ("table-header-group", "table-header-group"),
                     ("table-row-group", "table-row-group"), ("table-row", "table-row"), ("flow-root", "flow-root"), ("flex", "flex"), ("grid", "grid"),
                     ("inline-grid", "inline-grid"), ("contents", "contents"), ("list-item", "list-item"),
                 })
        {
            S(name, ("display", value));
        }
        S("field-sizing-content", ("field-sizing", "content"));
        S("field-sizing-fixed", ("field-sizing", "fixed"));
        Functional("aspect", new FunctionalOptions
        {
            ThemeKeys = K("--aspect"),
            HandleBareValue = v =>
            {
                if (v.Fraction == null) return null;
                var parts = Js.Segment(v.Fraction, '/');
                if (parts.Count < 2 || !Js.IsMultipleOfQuarter(parts[0]) || !Js.IsMultipleOfQuarter(parts[1])) return null;
                return v.Fraction;
            },
            Handle = (v, _) => L(D("aspect-ratio", v)),
            StaticValues = Statics(("auto", L(D("aspect-ratio", "auto"))), ("square", L(D("aspect-ratio", "1 / 1")))),
        });
    }

    void RegisterSizing()
    {
        foreach (var (name, value) in new[] { ("full", "100%"), ("svw", "100svw"), ("lvw", "100lvw"), ("dvw", "100dvw"), ("svh", "100svh"), ("lvh", "100lvh"), ("dvh", "100dvh"), ("min", "min-content"), ("max", "max-content"), ("fit", "fit-content") })
        {
            S("size-" + name, ("--tw-sort", "size"), ("width", value), ("height", value));
            S("w-" + name, ("width", value));
            S("h-" + name, ("height", value));
            S("min-w-" + name, ("min-width", value));
            S("min-h-" + name, ("min-height", value));
            S("max-w-" + name, ("max-width", value));
            S("max-h-" + name, ("max-height", value));
        }
        S("size-auto", ("--tw-sort", "size"), ("width", "auto"), ("height", "auto"));
        S("w-auto", ("width", "auto"));
        S("h-auto", ("height", "auto"));
        S("min-w-auto", ("min-width", "auto"));
        S("min-h-auto", ("min-height", "auto"));
        S("h-lh", ("height", "1lh"));
        S("min-h-lh", ("min-height", "1lh"));
        S("max-h-lh", ("max-height", "1lh"));
        S("w-screen", ("width", "100vw"));
        S("min-w-screen", ("min-width", "100vw"));
        S("max-w-screen", ("max-width", "100vw"));
        S("h-screen", ("height", "100vh"));
        S("min-h-screen", ("min-height", "100vh"));
        S("max-h-screen", ("max-height", "100vh"));
        S("max-w-none", ("max-width", "none"));
        S("max-h-none", ("max-height", "none"));
        Spacing("size", K("--size", "--spacing"), v => L(D("--tw-sort", "size"), D("width", v), D("height", v)), supportsFractions: true);

        foreach (var (name, keys, property) in new[]
                 {
                     ("w", K("--width", "--spacing", "--container"), "width"),
                     ("min-w", K("--min-width", "--spacing", "--container"), "min-width"),
                     ("max-w", K("--max-width", "--spacing", "--container"), "max-width"),
                     ("h", K("--height", "--spacing"), "height"),
                     ("min-h", K("--min-height", "--height", "--spacing"), "min-height"),
                     ("max-h", K("--max-height", "--height", "--spacing"), "max-height"),
                 })
        {
            string p = property;
            Spacing(name, keys, v => L(D(p, v)), supportsFractions: true);
        }

        foreach (var (name, value) in new[] { ("full", "100%"), ("min", "min-content"), ("max", "max-content"), ("fit", "fit-content") })
        {
            S("inline-" + name, ("inline-size", value));
            S("block-" + name, ("block-size", value));
            S("min-inline-" + name, ("min-inline-size", value));
            S("min-block-" + name, ("min-block-size", value));
            S("max-inline-" + name, ("max-inline-size", value));
            S("max-block-" + name, ("max-block-size", value));
        }
        foreach (var (name, value) in new[] { ("svw", "100svw"), ("lvw", "100lvw"), ("dvw", "100dvw") })
        {
            S("inline-" + name, ("inline-size", value));
            S("min-inline-" + name, ("min-inline-size", value));
            S("max-inline-" + name, ("max-inline-size", value));
        }
        foreach (var (name, value) in new[] { ("svh", "100svh"), ("lvh", "100lvh"), ("dvh", "100dvh") })
        {
            S("block-" + name, ("block-size", value));
            S("min-block-" + name, ("min-block-size", value));
            S("max-block-" + name, ("max-block-size", value));
        }
        S("inline-auto", ("inline-size", "auto"));
        S("block-auto", ("block-size", "auto"));
        S("min-inline-auto", ("min-inline-size", "auto"));
        S("min-block-auto", ("min-block-size", "auto"));
        S("block-lh", ("block-size", "1lh"));
        S("min-block-lh", ("min-block-size", "1lh"));
        S("max-block-lh", ("max-block-size", "1lh"));
        S("inline-screen", ("inline-size", "100vw"));
        S("min-inline-screen", ("min-inline-size", "100vw"));
        S("max-inline-screen", ("max-inline-size", "100vw"));
        S("block-screen", ("block-size", "100vh"));
        S("min-block-screen", ("min-block-size", "100vh"));
        S("max-block-screen", ("max-block-size", "100vh"));
        S("max-inline-none", ("max-inline-size", "none"));
        S("max-block-none", ("max-block-size", "none"));
        foreach (var (name, keys, property) in new[]
                 {
                     ("inline", K("--spacing", "--container"), "inline-size"),
                     ("min-inline", K("--spacing", "--container"), "min-inline-size"),
                     ("max-inline", K("--spacing", "--container"), "max-inline-size"),
                     ("block", K("--spacing"), "block-size"),
                     ("min-block", K("--spacing"), "min-block-size"),
                     ("max-block", K("--spacing"), "max-block-size"),
                 })
        {
            string p = property;
            Spacing(name, keys, v => L(D(p, v)), supportsFractions: true);
        }

        S("container", () =>
        {
            var breakpoints = new List<string>();
            foreach (var kv in _theme.Namespace("--breakpoint")) breakpoints.Add(kv.Value);
            Sorting.StableSort(breakpoints, (a, b) => Js.CompareBreakpoints(a, b, true));
            var decls = L(D("--tw-sort", "--tw-container-component"), D("width", "100%"));
            foreach (var bp in breakpoints) decls.Add(AstNode.AtRule("@media", "(width >= " + bp + ")", L(D("max-width", bp))));
            return decls;
        });

        S("flex-auto", ("flex", "auto"));
        S("flex-initial", ("flex", "0 auto"));
        S("flex-none", ("flex", "none"));
        Fn("flex", c =>
        {
            if (c.Value == null) return null;
            if (c.Value.IsArbitrary) return c.Modifier != null ? null : L(D("flex", c.Value.Value));
            if (c.Value.Fraction != null)
            {
                var parts = Js.Segment(c.Value.Fraction, '/');
                if (parts.Count < 2 || !Js.IsPositiveInteger(parts[0]) || !Js.IsPositiveInteger(parts[1])) return null;
                return L(D("flex", "calc(" + c.Value.Fraction + " * 100%)"));
            }
            if (Js.IsPositiveInteger(c.Value.Value)) return c.Modifier != null ? null : L(D("flex", c.Value.Value));
            return null;
        });
        Functional("shrink", new FunctionalOptions { HasDefaultValue = true, DefaultValue = "1", HandleBareValue = BareInteger, Handle = (v, _) => L(D("flex-shrink", v)) });
        Functional("grow", new FunctionalOptions { HasDefaultValue = true, DefaultValue = "1", HandleBareValue = BareInteger, Handle = (v, _) => L(D("flex-grow", v)) });
        S("basis-auto", ("flex-basis", "auto"));
        S("basis-full", ("flex-basis", "100%"));
        Spacing("basis", K("--flex-basis", "--spacing", "--container"), v => L(D("flex-basis", v)), supportsFractions: true);
        S("table-auto", ("table-layout", "auto"));
        S("table-fixed", ("table-layout", "fixed"));
        S("caption-top", ("caption-side", "top"));
        S("caption-bottom", ("caption-side", "bottom"));
        S("border-collapse", ("border-collapse", "collapse"));
        S("border-separate", ("border-collapse", "separate"));

        var borderSpacingProps = Props(P("--tw-border-spacing-x", "0", "<length>"), P("--tw-border-spacing-y", "0", "<length>"));
        Spacing("border-spacing", K("--border-spacing", "--spacing"), v => L(borderSpacingProps(), D("--tw-border-spacing-x", v), D("--tw-border-spacing-y", v), D("border-spacing", "var(--tw-border-spacing-x) var(--tw-border-spacing-y)")));
        Spacing("border-spacing-x", K("--border-spacing", "--spacing"), v => L(borderSpacingProps(), D("--tw-border-spacing-x", v), D("border-spacing", "var(--tw-border-spacing-x) var(--tw-border-spacing-y)")));
        Spacing("border-spacing-y", K("--border-spacing", "--spacing"), v => L(borderSpacingProps(), D("--tw-border-spacing-y", v), D("border-spacing", "var(--tw-border-spacing-x) var(--tw-border-spacing-y)")));

        foreach (var (name, property) in new[] { ("origin", "transform-origin"), ("perspective-origin", "perspective-origin") })
        {
            string p = property;
            Functional(name, new FunctionalOptions
            {
                ThemeKeys = K(name == "origin" ? "--transform-origin" : "--perspective-origin"),
                Handle = (v, _) => L(D(p, v)),
                StaticValues = Statics(
                    ("center", L(D(p, "center"))), ("top", L(D(p, "top"))), ("top-right", L(D(p, "100% 0"))), ("right", L(D(p, "100%"))),
                    ("bottom-right", L(D(p, "100% 100%"))), ("bottom", L(D(p, "bottom"))), ("bottom-left", L(D(p, "0 100%"))), ("left", L(D(p, "0"))),
                    ("top-left", L(D(p, "0 0")))),
            });
        }
        Functional("perspective", new FunctionalOptions { ThemeKeys = K("--perspective"), Handle = (v, _) => L(D("perspective", v)), StaticValues = Statics(("none", L(D("perspective", "none")))) });
    }
}
