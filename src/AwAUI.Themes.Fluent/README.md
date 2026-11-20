# AwAUI.Themes.Fluent

A CSS theme for `AwAUI.Controls`. Add a reference to the theme and opt the window in before mounting:

```csharp
using AwAUI.Themes.Fluent;

host.UseFluentTheme();
FluentTheme.SetVariant(host.Root, FluentThemeVariant.Dark);
FluentTheme.SetCompact(host.Root, compact: true);
```

The embedded stylesheet is parsed once per process. With `System`, CSS selects the palette using the host's media environment. The platform hosts do not currently feed native appearance changes into that environment. An explicit light, dark, or high contrast variant applies to regular content and portal overlays in that window.

Override `--aw-*` variables or add styles outside the theme's cascade layers to customize the appearance. See [ControlCatalog](../../samples/ControlCatalog) for an example.
