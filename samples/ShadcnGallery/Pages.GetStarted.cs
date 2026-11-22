using System.Collections.Generic;
using AwAUI.Shadcn;
using AwAUI.UI;
using Text = AwAUI.UI.Text;
using UIText = AwAUI.UI.Text;
using ShadcnButton = AwAUI.Shadcn.Button;

namespace ShadcnGallery;

/// <summary>The four Getting Started pages that precede the component reference in the sidebar.</summary>
public static class GetStartedPages
{
    public static void Register()
    {
        DocsCatalog.AddGuide("introduction", "Introduction",
            "A set of beautifully designed components you can copy into your apps. Accessible, customizable, and open source.",
            DocsExample.Of("Introduction", Introduction));

        DocsCatalog.AddGuide("installation", "Installation",
            "Add the package, register the stylesheet, and start composing your first screen.",
            DocsExample.Of("Installation", Installation));

        DocsCatalog.AddGuide("theming", "Theming",
            "Colors, radius, and chart tokens are CSS custom properties you own.",
            DocsExample.Of("Theming", Theming));

        DocsCatalog.AddGuide("dark-mode", "Dark Mode",
            "Every token has a dark counterpart; the gallery toggles them with a single class.",
            DocsExample.Of("Dark Mode", DarkMode));
    }

    static Node Introduction() => DocsUi.Stack(
        new Card
        {
            Class = "docs-hero",
            Children =
            [
                new CardHeader
                {
                    Children =
                    [
                        new CardTitle { Class = "docs-hero-title", Children = [new UIText("Build your component library")] },
                        new CardDescription
                        {
                            Children = [new UIText("A complete set of accessible primitives, packaged for AwAUI. Copy the components, keep the tokens, and make them yours.")],
                        },
                    ],
                },
                new CardContent
                {
                    Children =
                    [
                        DocsUi.Row(
                            new ShadcnButton { Children = [new UIText("Get started")] },
                            new ShadcnButton { Variant = ButtonVariant.Outline, Children = [new UIText("Browse components")] },
                            new Badge { Variant = BadgeVariant.Secondary, Children = [new UIText("v4 · new-york")] }),
                    ],
                },
            ],
        },
        DocsUi.Grid3(
            Feature("Accessible", "Roles, ARIA state, focus management, and keyboard behavior are built in."),
            Feature("Composable", "Small parts with data-slot markers compose into dialogs, menus, sidebars, and charts."),
            Feature("Themeable", "Light and dark tokens, RTL-aware layout, and the shadcn CSS variable system.")),
        DocsUi.Panel(
            DocsUi.Heading("Everything is a component", 2),
            DocsUi.Note("The gallery on the left mirrors the official documentation. Pick a component to see live examples rendered by AwAUI's own layout, text, and paint pipeline.")));

    static Card Feature(string title, string body) => new()
    {
        Children =
        [
            new CardHeader
            {
                Children =
                [
                    new CardTitle { Children = [new UIText(title)] },
                    new CardDescription { Children = [new UIText(body)] },
                ],
            },
        ],
    };

    static Node Installation() => DocsUi.Stack(
        Step("1", "Reference the component library",
            "Add a project or package reference to AwAUI.Shadcn. The library ships styles for every part; register them once at startup."),
        DocsUi.Code("""
            ShadcnTheme.Register();
            """),
        Step("2", "Use a component",
            "Components are ordinary AwAUI nodes. Compose them in .awaui markup or directly in C#."),
        DocsUi.Code("""
            <Button Variant="ButtonVariant.Outline">Save changes</Button>
            <Badge Variant="BadgeVariant.Secondary">Draft</Badge>
            """),
        Step("3", "Add your tokens",
            "The shadcn CSS variables define color, radius, and chart palettes. Override them on the root or a subtree."),
        DocsUi.Code("""
            root {
              --background: oklch(1 0 0);
              --foreground: oklch(0.145 0 0);
              --primary: oklch(0.205 0 0);
              --radius: 0.625rem;
            }
            """),
        DocsUi.Note("Tailwind utilities used by the components are compiled into the library's stylesheet at build time."));

    static Node Theming() => DocsUi.Stack(
        DocsUi.Heading("Color tokens", 2),
        DocsUi.Grid3(
            DocsUi.Swatch("--background", "var(--background)"),
            DocsUi.Swatch("--foreground", "var(--foreground)"),
            DocsUi.Swatch("--card", "var(--card)"),
            DocsUi.Swatch("--primary", "var(--primary)"),
            DocsUi.Swatch("--secondary", "var(--secondary)"),
            DocsUi.Swatch("--muted", "var(--muted)"),
            DocsUi.Swatch("--accent", "var(--accent)"),
            DocsUi.Swatch("--destructive", "var(--destructive)"),
            DocsUi.Swatch("--ring", "var(--ring)")),
        DocsUi.Heading("Chart palette", 2),
        DocsUi.Grid3(
            DocsUi.Swatch("--chart-1", "var(--chart-1)"),
            DocsUi.Swatch("--chart-2", "var(--chart-2)"),
            DocsUi.Swatch("--chart-3", "var(--chart-3)"),
            DocsUi.Swatch("--chart-4", "var(--chart-4)"),
            DocsUi.Swatch("--chart-5", "var(--chart-5)")),
        DocsUi.Heading("Radius", 2),
        DocsUi.Row(
            RadiusSample("sm", "var(--radius-sm)"),
            RadiusSample("md", "var(--radius-md)"),
            RadiusSample("lg", "var(--radius-lg)"),
            RadiusSample("xl", "var(--radius-xl)")));

    static Div RadiusSample(string name, string value) => new()
    {
        Class = "demo-radius",
        Style = "border-radius:" + value,
        Children = new NodeList([new UIText(name)]),
    };

    static Node DarkMode() => DocsUi.Stack(
        DocsUi.Note("The theme toggle in the header applies .dark to the gallery root. Components inherit the new tokens without rebuilding."),
        DocsUi.Grid(
            ThemePreview("Light", false),
            ThemePreview("Dark", true)),
        DocsUi.Panel(
            DocsUi.Heading("Scoped themes", 2),
            DocsUi.Note("Because tokens are inherited CSS custom properties, any subtree can opt into dark tokens by adding the dark class.")));

    static Div ThemePreview(string label, bool dark)
    {
        var card = new Card
        {
            Class = dark ? "dark docs-theme-preview" : "docs-theme-preview",
            Children =
            [
                new CardHeader
                {
                    Children =
                    [
                        new CardTitle { Children = [new UIText(label)] },
                        new CardDescription { Children = [new UIText("Card, badge, and button tokens")] },
                    ],
                },
                new CardContent
                {
                    Children =
                    [
                        DocsUi.Row(
                            new ShadcnButton { Children = [new UIText("Primary")] },
                            new Badge { Variant = BadgeVariant.Secondary, Children = [new UIText("Secondary")] }),
                    ],
                },
            ],
        };
        return new Div { Children = new NodeList([card]) };
    }

    static Div Step(string number, string title, string body) => new()
    {
        Class = "demo-step",
        Children = new NodeList(
        [
            new Div { Class = "demo-step-number", Children = new NodeList([new UIText(number)]) },
            DocsUi.Column(
                new Text { Class = "demo-step-title", Content = title, Role = Role.Heading, Attributes = { ["aria-level"] = "2" } },
                DocsUi.Muted(body)),
        ]),
    };
}
