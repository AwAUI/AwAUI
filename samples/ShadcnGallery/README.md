# Shadcn gallery

The gallery uses the packaged `AwAUI.Shadcn` components. Below the workspace cards it demonstrates multiline editing, selection, navigation, feedback, overlays, and data layout. Its project builds the page markup but does not scan or copy the component library's source files.

The Controls and Shadcn examples share the lower `AwAUI.Controls.Primitives.SliderBehavior`; keyboard steps, pointer capture, value snapping and ARIA bounds are implemented once in the primitives layer.

```sh
dotnet run --project samples/ShadcnGallery
```

This opens an interactive window on macOS, Windows, or Linux with X11. Use the theme button to switch colors. **Edit workspace** opens a dialog with keyboard focus, a labeled draft name, and Save and Cancel actions. Click **Workspace name** to focus the input. Press Enter to save; a blank name keeps the dialog open and shows an error. Tab stays within the open dialog; Escape closes it.

In Preferences, click either control or its label. The checkbox shows keyboard hints; the switch changes the page spacing. Under Overlays, open **Actions** and use the forward arrow or hover to enter **More actions**. Escape returns to the parent menu.

## Compare rendering backends

On macOS the gallery tries Metal and falls back to software if Metal is unavailable. To compare the same window and interaction in an optimized build:

```sh
dotnet run -c Release --project samples/ShadcnGallery -- --open-dialog --timings
dotnet run -c Release --project samples/ShadcnGallery -- --software --open-dialog --timings
dotnet run -c Release --project samples/ShadcnGallery -- --bench-render --scale 2
```

The console prints the active backend and device scale. Frame statistics split component build, style, layout, draw-list recording, and raster/submit time. `--bench-render` rasterizes the same open-Dialog display list at a selected scale to full software and Metal offscreen targets; after warm-up it reports submit and GPU execution times separately. The submit and execution measurements show CPU submission cost and GPU work respectively.

One Release run on an Apple M5 rendered the open Dialog at 1024×650 logical pixels and 2× scale. The software raster median was 62.45 ms (p95 63.28 ms). Metal submission took 0.14 ms (p95 0.15 ms) and GPU execution took 2.08 ms (p95 2.60 ms). The benchmark measures one scene offscreen. The window’s frame log also includes the UI thread and presentation.

## Headless previews

To render images without opening a window:

```sh
dotnet run --project samples/ShadcnGallery -- --height 1850 --headless gallery-light.png
dotnet run --project samples/ShadcnGallery -- --height 1850 --headless gallery-dark.png --dark
dotnet run --project samples/ShadcnGallery -- --headless gallery-dialog.png --dark --dialog
```

[Light](Previews/light.png) · [Dark](Previews/dark.png) · [Dialog](Previews/dialog.png)

Use `--width` and `--height` to change the window or screenshot size, and `--scale` for headless pixel density. The gallery stacks its cards when the viewport is narrower than 720 pixels.

## Inspect motion

[Opening and closing frame comparison](Previews/motion.png)

`--motion-frames` saves a sequence of PNGs at fixed timestamps after opening and closing a control. Use `--motion-component dialog|select|menu|submenu|popover|sheet|accordion` to choose the control and `--reduced-motion` for the reduced-motion version.

For Sheet, the capture also moves the pointer into the settled panel and records three hover frames. A native macOS window follows the system Reduce Motion setting, including changes made while the window is open.

```sh
dotnet run -c Release --project samples/ShadcnGallery -- --motion-frames /tmp/gallery-motion --motion-component menu
dotnet run -c Release --project samples/ShadcnGallery -- --motion-frames /tmp/gallery-motion-reduced --motion-component accordion --reduced-motion
```
