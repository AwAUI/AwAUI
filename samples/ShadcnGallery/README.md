# Shadcn gallery

A docs-style showcase for `AwAUI.Shadcn`. The shell mirrors the official shadcn/ui documentation: a left sidebar
with Getting Started guides and every component page, breadcrumbs, a component search box, a light/dark toggle, a
`new-york-v4` badge, live examples with a **Code** toggle, and previous/next paging. Narrow windows (under 900
logical pixels) hide the sidebar and expose the same navigation in a sheet.

The 72 pages (4 guides and 68 component pages) follow the official examples and descriptions, including the
S2 composition primitives and the AI Elements set (`Attachment`, `Bubble`, `Marker`, `Message`,
`MessageScroller`, `Questionnaire`, `Trace`). The gallery references the packaged library; it does not scan or copy
the component sources.

## Run the gallery

```sh
dotnet run --project samples/ShadcnGallery
```

This opens an interactive window on macOS, Windows, or Linux with X11. Click a sidebar entry (or use the search
box), toggle the theme from the header, and expand **Code** under any example to see the markup that produced it.
The dialog, sheet, and popover examples are fully interactive: Escape closes them, focus returns to the trigger,
and the dialog traps Tab while open.

## Layout and styles

The shell is a worked example of the three CSS formats:

| File | Purpose |
| --- | --- |
| `App.awaui` | Shell markup: sidebar, header, content area, mobile sheet. |
| `App.awaui.css` | Scoped (unique) styles for elements written in `App.awaui`. The generator appends `[aw-xxxxxxxx]` to the last compound of every selector. |
| `App.awaui.global.css` | Component-global styles registered when the `App` type first mounts. Used for nodes built in `@code` (navigation links, example cards, the pager): those nodes never receive `__Scope`, so scoped selectors cannot reach them. |
| `Styles/app.css` | Project-shared stylesheet, included with `<AwAUIGlobalStylesheet Include="Styles/app.css" />`. The generator emits `ShadcnGallery.AwAUIGlobalStyles` and registers it through a module initializer, so every page sees the same `demo-*` helpers. |

See [design/08-css.md](../../design/08-css.md) for the scoping rules and `::deep`.

## Headless previews

```sh
dotnet run --project samples/ShadcnGallery -- --headless gallery-light.png
dotnet run --project samples/ShadcnGallery -- --headless gallery-dark.png --dark
dotnet run --project samples/ShadcnGallery -- --headless gallery-dialog.png --dialog
dotnet run --project samples/ShadcnGallery -- --headless gallery-button.png --page button
```

[Light](Previews/light.png) · [Dark](Previews/dark.png) · [Dialog](Previews/dialog.png) ·
[Button page](Previews/component-button.png) · [Chart page](Previews/component-chart.png)

`--page <id>` opens a specific page (for example `introduction`, `button`, `navigation-menu`, `chart`, or
`message`). Use `--width` and `--height` for the window or screenshot size and `--scale` for headless pixel
density. At 800 pixels wide the sidebar collapses and the header shows the navigation menu button.

## Inspect motion

`--motion-frames` saves a sequence of PNGs at fixed timestamps after opening and closing a control:

```sh
dotnet run -c Release --project samples/ShadcnGallery -- --motion-frames /tmp/gallery-motion --motion-component menu
dotnet run -c Release --project samples/ShadcnGallery -- --motion-frames /tmp/gallery-motion-reduced --motion-component accordion --reduced-motion
```

Use `--motion-component dialog|select|menu|submenu|popover|sheet|accordion` to choose the control.

For Sheet, the capture also moves the pointer into the settled panel and records three hover frames. A native
macOS window follows the system Reduce Motion setting, including changes made while the window is open.

## Compare rendering backends

On macOS the gallery tries Metal and falls back to software if Metal is unavailable. To compare the same window and
interaction in an optimized build:

```sh
dotnet run -c Release --project samples/ShadcnGallery -- --open-dialog --timings
dotnet run -c Release --project samples/ShadcnGallery -- --software --open-dialog --timings
dotnet run -c Release --project samples/ShadcnGallery -- --bench-render --scale 2
```

The console prints the active backend and device scale. Frame statistics split component build, style, layout,
draw-list recording, and raster/submit time. `--bench-render` rasterizes the same open-Dialog display list at a
selected scale to full software and Metal offscreen targets; after warm-up it reports submit and GPU execution
times separately.
