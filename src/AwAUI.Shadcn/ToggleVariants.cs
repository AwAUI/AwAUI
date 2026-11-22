namespace AwAUI.Shadcn;

/// <summary>Visual treatment for a toggle button.</summary>
public enum ToggleVariant { Default, Outline }

/// <summary>Size of a toggle button.</summary>
public enum ToggleSize { Default, Sm, Lg }

/// <summary>Shared class fragments for <see cref="Toggle"/> and <see cref="ToggleGroupItem"/> (shadcn toggleVariants).</summary>
internal static class ToggleStyles
{
    internal const string Base =
        "inline-flex items-center justify-center gap-2 rounded-md text-sm font-medium whitespace-nowrap transition-[color,box-shadow] outline-none " +
        "hover:bg-muted hover:text-muted-foreground focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50 " +
        "disabled:pointer-events-none disabled:opacity-50 aria-invalid:border-destructive aria-invalid:ring-destructive/20 " +
        "data-[state=on]:bg-accent data-[state=on]:text-accent-foreground dark:aria-invalid:ring-destructive/40 " +
        "[&_svg]:pointer-events-none [&_svg]:shrink-0 [&_svg:not([class*='size-'])]:size-4";

    internal static string Variant(ToggleVariant variant) => variant == ToggleVariant.Outline
        ? "border border-input bg-transparent shadow-xs hover:bg-accent hover:text-accent-foreground"
        : "bg-transparent";

    internal static string Size(ToggleSize size) => size switch
    {
        ToggleSize.Sm => "h-8 min-w-8 px-1.5",
        ToggleSize.Lg => "h-10 min-w-10 px-2.5",
        _ => "h-9 min-w-9 px-2",
    };

    internal static string VariantName(ToggleVariant variant) =>
        variant == ToggleVariant.Outline ? "outline" : "default";

    internal static string SizeName(ToggleSize size) => size switch
    {
        ToggleSize.Sm => "sm",
        ToggleSize.Lg => "lg",
        _ => "default",
    };
}
