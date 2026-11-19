using AwAUI.Tailwind.Internal;

namespace AwAUI.Tailwind.Compilation;

internal sealed partial class CoreUtilities
{
    /// <summary>v3 兼容工具（Tailwind v4 在 <c>registerLegacyUtilities</c> 中注册，默认可用）。</summary>
    void RegisterLegacy()
    {
        foreach (var (n, d) in new[] { ("t", "top"), ("tr", "top right"), ("r", "right"), ("br", "bottom right"), ("b", "bottom"), ("bl", "bottom left"), ("l", "left"), ("tl", "top left") })
            S("bg-gradient-to-" + n, ("--tw-gradient-position", "to " + d + " in oklab"), ("background-image", "linear-gradient(var(--tw-gradient-stops))"));
        S("bg-left-top", ("background-position", "left top"));
        S("bg-right-top", ("background-position", "right top"));
        S("bg-left-bottom", ("background-position", "left bottom"));
        S("bg-right-bottom", ("background-position", "right bottom"));
        S("object-left-top", ("object-position", "left top"));
        S("object-right-top", ("object-position", "right top"));
        S("object-left-bottom", ("object-position", "left bottom"));
        S("object-right-bottom", ("object-position", "right bottom"));
        Fn("max-w-screen", c =>
        {
            if (c.Value == null || c.Value.IsArbitrary) return null;
            string? v = _theme.Resolve(c.Value.Value, K("--breakpoint"));
            return v != null ? L(D("max-width", v)) : null;
        });
        S("overflow-ellipsis", ("text-overflow", "ellipsis"));
        S("decoration-slice", ("-webkit-box-decoration-break", "slice"), ("box-decoration-break", "slice"));
        S("decoration-clone", ("-webkit-box-decoration-break", "clone"), ("box-decoration-break", "clone"));
        foreach (var name in new[] { "flex-shrink", "flex-grow" })
        {
            string p = name;
            Fn(p, c =>
            {
                if (c.Modifier != null) return null;
                if (c.Value == null) return L(D(p, "1"));
                if (c.Value.IsArbitrary) return L(D(p, c.Value.Value));
                if (Js.IsPositiveInteger(c.Value.Value)) return L(D(p, c.Value.Value));
                return null;
            });
        }
        S("order-none", ("order", "0"));
        S("break-words", ("overflow-wrap", "break-word"));
        foreach (var (name, property) in new[] { ("start", "inset-inline-start"), ("end", "inset-inline-end") })
        {
            string p = property;
            UtilityCompileFn Inset(bool negative) => c =>
            {
                if (c.Value == null) return null;
                if (c.Value.IsArbitrary)
                {
                    if (c.Modifier != null) return null;
                    string v = c.Value.Value;
                    return L(D(p, negative ? "calc(" + v + " * -1)" : v));
                }
                string? value = _theme.Resolve(c.Value.Fraction ?? c.Value.Value, K("--inset", "--spacing"));
                if (value == null && c.Value.Fraction != null)
                {
                    var parts = Js.Segment(c.Value.Fraction, '/');
                    if (parts.Count < 2 || !Js.IsPositiveInteger(parts[0]) || !Js.IsPositiveInteger(parts[1])) return null;
                    value = "calc(" + c.Value.Fraction + " * 100%)";
                }
                if (value == null && negative)
                {
                    string? spacing = _theme.Resolve(null, s_spacing);
                    if (spacing != null && Js.IsMultipleOfQuarter(c.Value.Value))
                    {
                        value = "calc(" + spacing + " * -" + c.Value.Value + ")";
                        return L(D(p, value));
                    }
                }
                if (value == null)
                {
                    string? spacing = _theme.Resolve(null, s_spacing);
                    if (spacing != null && Js.IsMultipleOfQuarter(c.Value.Value)) value = "calc(" + spacing + " * " + c.Value.Value + ")";
                }
                return value != null ? L(D(p, negative ? "calc(" + value + " * -1)" : value)) : null;
            };
            S(name + "-auto", (p, "auto"));
            S(name + "-full", (p, "100%"));
            S("-" + name + "-full", (p, "-100%"));
            S(name + "-px", (p, "1px"));
            S("-" + name + "-px", (p, "-1px"));
            Fn("-" + name, Inset(true));
            Fn(name, Inset(false));
        }
    }
}
