# AwAUI.Icons.Lucide

A curated set of [Lucide](https://github.com/lucide-icons/lucide) icons for AwAUI and Shadcn controls. Add a reference to `AwAUI.Icons.Lucide.csproj` from your project.

```csharp
using AwAUI.Icons.Lucide;
using AwAUI.UI;

var check = new Icon(LucideIcons.Check);
var search = new Icon(LucideIcons.Search) { Size = "16px", Label = "Search" };
```

`LucideIcons` exposes static, get-only `AwAUI.Svg.SvgDocument` properties:

- `Check`, `Minus`, `Plus`, `X`, `Circle`
- `ChevronDown`, `ChevronLeft`, `ChevronRight`, `ChevronUp`, `ChevronsUpDown`
- `Ellipsis`, `MoreHorizontal`, `LoaderCircle`, `PanelLeft`, `Search`, `Sun`, `Moon`
- `Bell`, `Settings`, `Folder`, `LayoutDashboard`, `Copy`, `RefreshCw`, `ThumbsUp`, `ThumbsDown`

Each document is parsed on first access and cached. `MoreHorizontal` is an alias for `Ellipsis` and returns the same document. The SVGs retain Lucide's `24 × 24` view box and `currentColor` strokes. `Icon` uses the surrounding text color and defaults to `1em`; omit `Label` for decorative icons. `LoaderCircle` is static artwork; animate it at the call site if needed.

## Source and license

The files in `Icons/` and the [LICENSE](LICENSE) come from the official Lucide repository at tag [`1.56.0`](https://github.com/lucide-icons/lucide/tree/1.56.0), commit `440a1e1dfb1e712505308da46bead3c28b1f1398`. The license includes both Lucide's ISC terms and the MIT terms for icons derived from Feather. `LICENSE` is included in the NuGet package.

## Regenerate

From the repository root, run:

```sh
python3 src/AwAUI.Icons.Lucide/generate.py
```

The generator uses only the Python 3 standard library and the SVGs checked into `Icons/`. It sorts by filename and writes `Generated/LucideIcons.g.cs` as UTF-8 with LF line endings. When refreshing or adding an icon, copy the original SVG from the pinned upstream revision and regenerate. Update the pinned revision and license together when upgrading Lucide. Builds use the checked-in C# and do not run the generator or fetch icon assets.
