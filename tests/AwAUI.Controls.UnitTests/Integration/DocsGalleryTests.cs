using AwAUI.Controls.UnitTests.Integration;
using AwAUI.Graphics;
using AwAUI.Shadcn;
using AwAUI.UI;
using ShadcnGallery;

namespace AwAUI.Shadcn.Integration;

/// <summary>
/// The docs gallery is the showcase for every component, so each registered page must build and paint. The suite
/// also guards the official component inventory and its descriptions, which drive the sidebar and page headers.
/// </summary>
public class DocsGalleryTests
{
    static DocsGalleryTests() => ShadcnTheme.Register();

    const string Css = """
        root { background-color: var(--background); color: var(--foreground); }
        .docs-test-page { padding: 20px; display: flex; flex-direction: column; gap: 14px; width: 100%; }
        .demo-stack { display: flex; flex-direction: column; gap: 14px; }
        .demo-row { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
        .demo-column { display: flex; flex-direction: column; gap: 6px; }
        .demo-grid { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); gap: 14px; align-items: start; }
        .demo-grid-3 { grid-template-columns: repeat(3, minmax(0, 1fr)); }
        .demo-panel { border: 1px solid var(--border); border-radius: 10px; padding: 16px; display: flex; flex-direction: column; gap: 10px; }
        .demo-field { display: flex; flex-direction: column; gap: 6px; max-width: 360px; }
        .demo-split { display: flex; align-items: center; justify-content: space-between; gap: 16px; }
        .demo-scroll { max-height: 260px; overflow-y: auto; border: 1px solid var(--border); border-radius: 10px; padding: 16px; }
        .demo-center { display: flex; align-items: center; justify-content: center; min-height: 120px; }
        .demo-wide { grid-column: 1 / -1; }
        .demo-muted, .demo-note { font-size: 13px; color: var(--muted-foreground); }
        .demo-label { font-size: 13px; font-weight: 600; }
        .demo-heading { font-size: 20px; font-weight: 650; }
        .demo-subheading { font-size: 15px; font-weight: 600; }
        .demo-divider { height: 1px; background: var(--border); width: 100%; }
        .demo-code { font-family: monospace; font-size: 12px; white-space: pre; overflow-x: auto; background: var(--muted);
                     border: 1px solid var(--border); border-radius: 8px; padding: 14px 16px; }
        .demo-swatch { display: flex; align-items: center; gap: 10px; border: 1px solid var(--border); border-radius: 8px; padding: 8px; }
        .demo-swatch-color { width: 34px; height: 34px; border-radius: 6px; border: 1px solid var(--border); }
        .demo-theme-preview { border: 1px solid var(--border); border-radius: 10px; background: var(--background); color: var(--foreground); padding: 12px; }
        .demo-step { display: flex; align-items: flex-start; gap: 12px; }
        .demo-step-number { width: 26px; height: 26px; border-radius: 999px; background: var(--primary); color: var(--primary-foreground);
                            display: flex; align-items: center; justify-content: center; font-size: 12px; font-weight: 650; }
        .demo-chart { width: 100%; min-height: 200px; }
        .demo-fill { width: 100%; }
        .demo-narrow { max-width: 320px; }
        .demo-medium { max-width: 480px; }
        .demo-tall { min-height: 240px; }
        """;

    /// <summary>Every component page the official docs sidebar lists (plus the in-repo Form and Sonner pages).</summary>
    static readonly string[] OfficialComponentIds =
    [
        "accordion", "alert", "alert-dialog", "aspect-ratio", "attachment", "avatar", "badge", "breadcrumb",
        "bubble", "button", "button-group", "calendar", "card", "carousel", "chart", "checkbox", "code-block",
        "collapsible", "combobox", "command", "context-menu", "data-table", "date-picker", "dialog", "direction",
        "drawer", "dropdown-menu", "empty", "field", "form", "hover-card", "input", "input-group", "input-otp",
        "item", "kbd", "label", "marker", "menubar", "message", "message-scroller", "native-select",
        "navigation-menu", "pagination", "popover", "progress", "questionnaire", "radio-group", "resizable",
        "scroll-area", "select", "separator", "sheet", "sidebar", "skeleton", "slider", "sonner", "spinner",
        "switch", "table", "tabs", "textarea", "toast", "toggle", "toggle-group", "tooltip", "trace", "typography",
    ];

    [Fact]
    public void Docs_catalog_covers_the_official_component_inventory_with_descriptions()
    {
        var ids = DocsCatalog.Components.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var missing = OfficialComponentIds.Where(id => !ids.Contains(id)).ToList();
        Assert.True(missing.Count == 0, "Missing docs pages: " + string.Join(", ", missing));

        Assert.Equal(4, DocsCatalog.GettingStarted.Count);
        foreach (var page in DocsCatalog.Pages)
        {
            Assert.False(string.IsNullOrWhiteSpace(page.Title), page.Id + " has no title");
            Assert.False(string.IsNullOrWhiteSpace(page.Description), page.Id + " has no description");
            Assert.NotEmpty(page.Examples);
            foreach (var example in page.Examples)
                Assert.False(string.IsNullOrWhiteSpace(example.Title), page.Id + " has an untitled example");
        }
    }

    [Fact]
    public void Every_docs_page_builds_and_paints_visible_content()
    {
        foreach (var page in DocsCatalog.Pages)
        {
            var h = new UIHarness(() => new Div
            {
                Class = "docs-test-page",
                Children = new NodeList(page.Examples.Select(example => example.Build())),
            }, Css, 960, 1200, appStyleSheets: true);

            using var bitmap = h.Render(Color.White);
            AssertPainted(bitmap, page.Id);
        }
    }

    [Fact]
    public void Component_pages_render_in_dark_theme_too()
    {
        foreach (var page in DocsCatalog.Components)
        {
            var h = new UIHarness(() => new Div
            {
                Class = "docs-test-page",
                Children = new NodeList(page.Examples.Select(example => example.Build())),
            }, Css, 960, 1200, appStyleSheets: true);

            ShadcnTheme.SetDark(h.Root, true);
            h.Frame();
            using var bitmap = h.Render(Color.White);
            AssertPainted(bitmap, page.Id + " (dark)");
        }
    }

    static void AssertPainted(Bitmap bitmap, string page)
    {
        int opaque = 0;
        var colors = new HashSet<int>();
        for (int y = 0; y < bitmap.Height; y += 6)
            for (int x = 0; x < bitmap.Width; x += 6)
            {
                var c = bitmap.GetPixel(x, y);
                if (c.A > 0.5f) opaque++;
                colors.Add(((int)(c.R * 255) << 16) | ((int)(c.G * 255) << 8) | (int)(c.B * 255));
            }
        Assert.True(opaque > 0, $"docs page \"{page}\" painted nothing");
        Assert.True(colors.Count >= 3, $"docs page \"{page}\" is nearly blank ({colors.Count} sampled colours)");
    }
}
