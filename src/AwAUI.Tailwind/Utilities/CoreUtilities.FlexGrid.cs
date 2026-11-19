using System;
using System.Collections.Generic;
using AwAUI.Tailwind.Ast;
using AwAUI.Tailwind.Internal;

namespace AwAUI.Tailwind.Compilation;

internal sealed partial class CoreUtilities
{
    void RegisterFlexGrid()
    {
        S("grid-flow-row", ("grid-auto-flow", "row"));
        S("grid-flow-col", ("grid-auto-flow", "column"));
        S("grid-flow-dense", ("grid-auto-flow", "dense"));
        S("grid-flow-row-dense", ("grid-auto-flow", "row dense"));
        S("grid-flow-col-dense", ("grid-auto-flow", "column dense"));
        foreach (var (name, property, key) in new[] { ("auto-cols", "grid-auto-columns", "--grid-auto-columns"), ("auto-rows", "grid-auto-rows", "--grid-auto-rows") })
        {
            string p = property;
            Functional(name, new FunctionalOptions
            {
                ThemeKeys = K(key),
                HandleBareValue = v => _theme.Resolve(null, s_spacing) == null || !Js.IsMultipleOfQuarter(v.Value) ? null : "--spacing(" + v.Value + ")",
                Handle = (v, _) => L(D(p, v)),
                StaticValues = Statics(("auto", L(D(p, "auto"))), ("min", L(D(p, "min-content"))), ("max", L(D(p, "max-content"))), ("fr", L(D(p, "minmax(0, 1fr)")))),
            });
        }
        foreach (var (name, property, key) in new[] { ("grid-cols", "grid-template-columns", "--grid-template-columns"), ("grid-rows", "grid-template-rows", "--grid-template-rows") })
        {
            string p = property;
            Functional(name, new FunctionalOptions
            {
                ThemeKeys = K(key),
                HandleBareValue = v => Js.IsStrictPositiveInteger(v.Value) ? "repeat(" + v.Value + ", minmax(0, 1fr))" : null,
                Handle = (v, _) => L(D(p, v)),
                StaticValues = Statics(("none", L(D(p, "none"))), ("subgrid", L(D(p, "subgrid")))),
            });
        }
        S("flex-row", ("flex-direction", "row"));
        S("flex-row-reverse", ("flex-direction", "row-reverse"));
        S("flex-col", ("flex-direction", "column"));
        S("flex-col-reverse", ("flex-direction", "column-reverse"));
        S("flex-wrap", ("flex-wrap", "wrap"));
        S("flex-nowrap", ("flex-wrap", "nowrap"));
        S("flex-wrap-reverse", ("flex-wrap", "wrap-reverse"));

        foreach (var (n, v) in new[] { ("center", "center"), ("start", "start"), ("end", "end"), ("center-safe", "safe center"), ("end-safe", "safe end"), ("between", "space-between"), ("around", "space-around"), ("evenly", "space-evenly"), ("baseline", "baseline"), ("stretch", "stretch") })
            S("place-content-" + n, ("place-content", v));
        foreach (var (n, v) in new[] { ("center", "center"), ("start", "start"), ("end", "end"), ("center-safe", "safe center"), ("end-safe", "safe end"), ("baseline", "baseline"), ("stretch", "stretch") })
            S("place-items-" + n, ("place-items", v));
        foreach (var (n, v) in new[] { ("normal", "normal"), ("center", "center"), ("start", "flex-start"), ("end", "flex-end"), ("center-safe", "safe center"), ("end-safe", "safe flex-end"), ("between", "space-between"), ("around", "space-around"), ("evenly", "space-evenly"), ("baseline", "baseline"), ("stretch", "stretch") })
            S("content-" + n, ("align-content", v));
        foreach (var (n, v) in new[] { ("center", "center"), ("start", "flex-start"), ("end", "flex-end"), ("center-safe", "safe center"), ("end-safe", "safe flex-end"), ("baseline", "baseline"), ("baseline-last", "last baseline"), ("stretch", "stretch") })
            S("items-" + n, ("align-items", v));
        foreach (var (n, v) in new[] { ("normal", "normal"), ("center", "center"), ("start", "flex-start"), ("end", "flex-end"), ("center-safe", "safe center"), ("end-safe", "safe flex-end"), ("between", "space-between"), ("around", "space-around"), ("evenly", "space-evenly"), ("baseline", "baseline"), ("stretch", "stretch") })
            S("justify-" + n, ("justify-content", v));
        foreach (var (n, v) in new[] { ("normal", "normal"), ("center", "center"), ("start", "start"), ("end", "end"), ("center-safe", "safe center"), ("end-safe", "safe end"), ("stretch", "stretch") })
            S("justify-items-" + n, ("justify-items", v));

        Spacing("gap", K("--gap", "--spacing"), v => L(D("gap", v)));
        Spacing("gap-x", K("--gap", "--spacing"), v => L(D("column-gap", v)));
        Spacing("gap-y", K("--gap", "--spacing"), v => L(D("row-gap", v)));

        foreach (var axis in new[] { "x", "y" })
        {
            string a = axis;
            string start = a == "x" ? "margin-inline-start" : "margin-block-start";
            string end = a == "x" ? "margin-inline-end" : "margin-block-end";
            string sort = a == "x" ? "row-gap" : "column-gap";
            var props = Props(P("--tw-space-" + a + "-reverse", "0"));
            Spacing("space-" + a, K("--space", "--spacing"), v =>
            {
                bool isZero = IsZeroSpacing(v);
                return L(props(), Rule(":where(& > :not(:last-child))",
                    D("--tw-sort", sort),
                    D("--tw-space-" + a + "-reverse", "0"),
                    D(start, isZero ? "0" : "calc(" + v + " * var(--tw-space-" + a + "-reverse))"),
                    D(end, isZero ? "0" : "calc(" + v + " * calc(1 - var(--tw-space-" + a + "-reverse)))")));
            }, supportsNegative: true);
            S("space-" + a + "-reverse", () => L(props(), Rule(":where(& > :not(:last-child))", D("--tw-sort", sort), D("--tw-space-" + a + "-reverse", "1"))));
        }

        S("accent-auto", ("accent-color", "auto"));
        Color("accent", K("--accent-color", "--color"), v => L(D("accent-color", v)));
        Color("caret", K("--caret-color", "--color"), v => L(D("caret-color", v)));
        Color("divide", K("--divide-color", "--border-color", "--color"), v => L(Rule(":where(& > :not(:last-child))", D("--tw-sort", "divide-color"), D("border-color", v))));

        foreach (var (n, v) in new[] { ("auto", "auto"), ("start", "start"), ("end", "end"), ("center", "center"), ("end-safe", "safe end"), ("center-safe", "safe center"), ("stretch", "stretch") })
            S("place-self-" + n, ("place-self", v));
        foreach (var (n, v) in new[] { ("auto", "auto"), ("start", "flex-start"), ("end", "flex-end"), ("center", "center"), ("end-safe", "safe flex-end"), ("center-safe", "safe center"), ("stretch", "stretch"), ("baseline", "baseline"), ("baseline-last", "last baseline") })
            S("self-" + n, ("align-self", v));
        foreach (var (n, v) in new[] { ("auto", "auto"), ("start", "flex-start"), ("end", "flex-end"), ("center", "center"), ("end-safe", "safe flex-end"), ("center-safe", "safe center"), ("stretch", "stretch") })
            S("justify-self-" + n, ("justify-self", v));

        foreach (var v in new[] { "auto", "hidden", "clip", "visible", "scroll" })
        {
            S("overflow-" + v, ("overflow", v));
            S("overflow-x-" + v, ("overflow-x", v));
            S("overflow-y-" + v, ("overflow-y", v));
        }
        foreach (var v in new[] { "auto", "contain", "none" })
        {
            S("overscroll-" + v, ("overscroll-behavior", v));
            S("overscroll-x-" + v, ("overscroll-behavior-x", v));
            S("overscroll-y-" + v, ("overscroll-behavior-y", v));
        }
        S("scroll-auto", ("scroll-behavior", "auto"));
        S("scroll-smooth", ("scroll-behavior", "smooth"));
        S("scrollbar-auto", ("scrollbar-width", "auto"));
        S("scrollbar-thin", ("scrollbar-width", "thin"));
        S("scrollbar-none", ("scrollbar-width", "none"));
        var scrollbarProps = Props(P("--tw-scrollbar-thumb", "#0000", "<color>"), P("--tw-scrollbar-track", "#0000", "<color>"));
        Color("scrollbar-thumb", K("--color"), v => L(scrollbarProps(), D("--tw-scrollbar-thumb", v), D("scrollbar-color", "var(--tw-scrollbar-thumb) var(--tw-scrollbar-track)")));
        Color("scrollbar-track", K("--color"), v => L(scrollbarProps(), D("--tw-scrollbar-track", v), D("scrollbar-color", "var(--tw-scrollbar-thumb) var(--tw-scrollbar-track)")));
        S("scrollbar-gutter-auto", ("scrollbar-gutter", "auto"));
        S("scrollbar-gutter-stable", ("scrollbar-gutter", "stable"));
        S("scrollbar-gutter-both", ("scrollbar-gutter", "stable both-edges"));
        S("truncate", ("overflow", "hidden"), ("text-overflow", "ellipsis"), ("white-space", "nowrap"));
        S("text-ellipsis", ("text-overflow", "ellipsis"));
        S("text-clip", ("text-overflow", "clip"));
        foreach (var v in new[] { "none", "manual", "auto" }) S("hyphens-" + v, ("-webkit-hyphens", v), ("hyphens", v));
        foreach (var v in new[] { "normal", "nowrap", "pre", "pre-line", "pre-wrap", "break-spaces" }) S("whitespace-" + v, ("white-space", v));
        Functional("tab", new FunctionalOptions { HandleBareValue = BareInteger, Handle = (v, _) => L(D("tab-size", v)) });
        S("text-wrap", ("text-wrap", "wrap"));
        S("text-nowrap", ("text-wrap", "nowrap"));
        S("text-balance", ("text-wrap", "balance"));
        S("text-pretty", ("text-wrap", "pretty"));
        S("break-normal", ("overflow-wrap", "normal"), ("word-break", "normal"));
        S("break-all", ("word-break", "break-all"));
        S("break-keep", ("word-break", "keep-all"));
        S("wrap-anywhere", ("overflow-wrap", "anywhere"));
        S("wrap-break-word", ("overflow-wrap", "break-word"));
        S("wrap-normal", ("overflow-wrap", "normal"));
    }

    /// <summary><c>space-x-0</c> 一类的零值：直接输出 0。</summary>
    static bool IsZeroSpacing(string v)
    {
        if (v == "--spacing(0)" || v == "--spacing(-0)") return true;
        var dim = Dimension.Parse(v);
        return dim != null && dim.Value.Number == 0 && (dim.Value.Unit == null || DataTypes.IsLength(v));
    }
}
