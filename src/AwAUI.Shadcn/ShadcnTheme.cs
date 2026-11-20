using System;
using AwAUI.UI;

namespace AwAUI.Shadcn;

/// <summary>The bundled neutral New York theme.</summary>
public static class ShadcnTheme
{
    /// <summary>Registers the bundled CSS. Call before mounting the app to establish its cascade layers early.</summary>
    public static void Register() => TailwindStyles.Register();

    /// <summary>Applies the dark palette to the window, including its portal layers.</summary>
    public static void SetDark(UIRoot root, bool dark)
    {
        if (root is null) throw new ArgumentNullException(nameof(root));
        root.RootElement.SetAttributeOverride("data-aw-theme", dark ? "dark" : null);
    }
}
