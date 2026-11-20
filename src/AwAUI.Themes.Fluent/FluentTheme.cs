using System;
using System.IO;
using AwAUI.Css;
using AwAUI.UI;

namespace AwAUI.Themes.Fluent;

/// <summary>The color palette for an opted-in Fluent root.</summary>
public enum FluentThemeVariant
{
    /// <summary>Follow the color scheme and contrast preferences in the CSS media environment.</summary>
    System,
    Light,
    Dark,
    HighContrast,
}

/// <summary>The bundled Fluent stylesheet and per-window settings.</summary>
public static class FluentTheme
{
    internal const string OptInAttribute = "data-aw-fluent";
    const string ThemeAttribute = "data-theme";
    const string CompactAttribute = "data-aw-compact";
    const string ResourceName = "AwAUI.Themes.Fluent.Styles.fluent.css";

    static readonly Lazy<StyleSheet> s_styleSheet = new(LoadStyleSheet);

    /// <summary>
    /// The shared stylesheet, parsed on first access. When adding it directly to a custom host,
    /// set <c>data-aw-fluent</c> on <see cref="UIRoot.RootElement"/> to enable its rules.
    /// </summary>
    public static StyleSheet StyleSheet => s_styleSheet.Value;

    /// <summary>Sets the root palette. System removes the override so CSS can follow media preferences.</summary>
    public static void SetVariant(UIRoot root, FluentThemeVariant variant)
    {
        if (root is null) throw new ArgumentNullException(nameof(root));
        string? value = variant switch
        {
            FluentThemeVariant.System => null,
            FluentThemeVariant.Light => "light",
            FluentThemeVariant.Dark => "dark",
            FluentThemeVariant.HighContrast => "high-contrast",
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };
        root.RootElement.SetAttributeOverride(ThemeAttribute, value);
    }

    /// <summary>Sets whether the window uses the compact control sizes and spacing.</summary>
    public static void SetCompact(UIRoot root, bool compact)
    {
        if (root is null) throw new ArgumentNullException(nameof(root));
        root.RootElement.SetAttributeOverride(CompactAttribute, compact ? "" : null);
    }

    static StyleSheet LoadStyleSheet()
    {
        using var stream = typeof(FluentTheme).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("Missing embedded Fluent stylesheet: " + ResourceName);
        using var reader = new StreamReader(stream);
        return StyleSheet.Parse(reader.ReadToEnd(), "AwAUI.Themes.Fluent/Styles/fluent.css");
    }
}

/// <summary>Fluent theme registration for a window host.</summary>
public static class FluentThemeHostExtensions
{
    /// <summary>Adds the Fluent stylesheet once to this host and opts its root in to Fluent styling.</summary>
    public static UIHost UseFluentTheme(this UIHost host)
    {
        if (host is null) throw new ArgumentNullException(nameof(host));
        var sheet = FluentTheme.StyleSheet;
        foreach (var registered in host.Pipeline.StyleEngine.StyleSheets)
        {
            if (ReferenceEquals(registered.Sheet, sheet))
            {
                host.Root.RootElement.SetAttributeOverride(FluentTheme.OptInAttribute, "");
                return host;
            }
        }

        host.AddStyleSheet(sheet);
        host.Root.RootElement.SetAttributeOverride(FluentTheme.OptInAttribute, "");
        return host;
    }
}
