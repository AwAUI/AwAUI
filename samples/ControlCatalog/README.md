# Control Catalog

The catalog uses the standard controls from `AwAUI.Controls` and the standalone `AwAUI.Themes.Fluent` stylesheet. The page is written in `.awaui` and runs in a desktop window on macOS, Windows, or Linux with X11.

```sh
dotnet run -c Release --project samples/ControlCatalog
```

Use the theme button to cycle through system, light, dark, and high contrast. **Compact** switches the control density. Tab moves between controls, Space activates the focused button or toggle, and a click on a field label focuses its editor. The notification checkbox cycles through on, off, and mixed states. Enter inserts a newline in **Notes**. The ComboBox supports arrow navigation and typeahead; the TreeView uses Left and Right to collapse and expand branches.

The virtualized list demonstrates 10,000 rows; use **First row** and **Last row** to jump between its ends. The dock and canvas card shows how edge docking and pixel offsets can be combined. Its delivery schedule uses arrow keys to select between the enabled radios.

The tab strip changes a panel owned by the sample. Use the arrow keys or drag the slider to adjust preview zoom. Hold **Hold to add** with a pointer or Space to repeat the counter action. **Preview link destination** shows the URI in the sample, and **Toggle navigation** switches the split view between the full pane and the compact rail. The Viewbox example scales fixed-size content into an allocated area; select the text example to copy its contents. **Actions**, **Popup**, and **Help tip** show a keyboard-navigable menu, explicitly anchored surface, and hover or focus tooltip.

To render a preview without opening a window:

```sh
dotnet run -c Release --project samples/ControlCatalog -- --theme light --height 2520 --headless fluent-light.png
dotnet run -c Release --project samples/ControlCatalog -- --theme dark --height 2520 --headless fluent-dark.png
dotnet run -c Release --project samples/ControlCatalog -- --theme high-contrast --compact --height 2520 --headless fluent-contrast.png
dotnet run -c Release --project samples/ControlCatalog -- --theme light --height 2520 --list-end --headless fluent-list-end.png
dotnet run -c Release --project samples/ControlCatalog -- --overlay menu --theme light --headless fluent-menu.png
dotnet run -c Release --project samples/ControlCatalog -- --overlay popup --theme light --headless fluent-popup.png
dotnet run -c Release --project samples/ControlCatalog -- --overlay tooltip --theme light --headless fluent-tooltip.png
```

[Light](Previews/light.png) · [Dark](Previews/dark.png) · [High contrast and compact](Previews/high-contrast.png)

The current preview height is 2520 pixels to include the later 9c control examples. `--overlay menu|popup|tooltip` opens that surface in the headless catalog so it can be inspected separately.

`system` uses the host's CSS media environment, which currently defaults to light. Explicit theme selection applies to the window and its portal overlays.
