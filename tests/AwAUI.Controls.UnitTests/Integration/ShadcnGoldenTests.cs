using System.Globalization;
using AwAUI.Controls.UnitTests.Integration;
using AwAUI.Graphics;
using AwAUI.UI;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Shadcn.Integration;

/// <summary>
/// Shadcn S1 的代表性 gallery 场景：真实 <see cref="AwAUI.Shadcn"/> 组件 + 真实的 Tailwind / shadcn 样式表，
/// 由 <see cref="AwAUI.Text.Testing.TestTextEngine"/>（方块字体，无系统字体）在 640×480、1x 下确定性渲染。
/// <para>
/// 黄金图片是 AwAUI 自身渲染的参考图，不是 shadcn 官网截图（离线测试环境无法获取官网图片）。除像素比较外，
/// 测试还断言卡片与按钮的几何稳定、没有重叠，并且明暗主题只改变颜色而不改变布局。
/// </para>
/// </summary>
public class ShadcnGoldenTests
{
    static ShadcnGoldenTests() => ShadcnTheme.Register();

    const float Width = 640;
    const float Height = 480;

    const string GalleryCss = """
        root { background-color: var(--background); color: var(--foreground); }
        .s1-page { box-sizing: border-box; min-height: 480px; padding: 16px; display: flex; flex-direction: column; gap: 12px;
                   background-color: var(--background); color: var(--foreground); }
        .s1-header { display: flex; align-items: center; justify-content: space-between; gap: 12px; }
        .s1-heading { display: flex; flex-direction: column; gap: 2px; }
        .s1-title { font-size: 18px; line-height: 1.2; font-weight: 600; }
        .s1-subtitle, .s1-muted { font-size: 12px; color: var(--muted-foreground); }
        .s1-actions { display: flex; align-items: center; gap: 8px; }
        .s1-grid { display: grid; grid-template-columns: 1fr 1fr; align-items: start; gap: 12px; }
        .s1-stack { display: flex; flex-direction: column; gap: 8px; }
        .s1-field { display: flex; flex-direction: column; gap: 4px; }
        .s1-row { display: flex; align-items: center; gap: 6px; }
        .s1-between { justify-content: space-between; }
        .s1-text { font-size: 12px; color: var(--foreground); }
        .s1-wide { grid-column: 1 / -1; }
        """;

    static UIHarness Harness() => new(Scene, GalleryCss, Width, Height, scale: 1, appStyleSheets: true);

    static Node Scene() => new Div
    {
        Class = "s1-page",
        Children =
        [
            new Div
            {
                Class = "s1-header",
                Children =
                [
                    new Div
                    {
                        Class = "s1-heading",
                        Children =
                        [
                            new UIText { Class = "s1-title", Content = "Component gallery" },
                            new UIText { Class = "s1-subtitle", Content = "Neutral theme · light and dark reference" },
                        ],
                    },
                    new Div
                    {
                        Class = "s1-actions",
                        Children =
                        [
                            new Badge { Variant = BadgeVariant.Secondary, Children = [new UIText("S1")] },
                            new Button { Rest = new AttributeDictionary { ["id"] = "new-item" }, Children = [new UIText("New item")] },
                        ],
                    },
                ],
            },
            new Div
            {
                Class = "s1-grid",
                Children =
                [
                    new Card
                    {
                        Class = "gap-3 py-3",
                        Children =
                        [
                            new CardHeader
                            {
                                Class = "px-4",
                                Children =
                                [
                                    new CardTitle { Children = [new UIText("Profile")] },
                                    new CardDescription { Children = [new UIText("Manage the account")] },
                                ],
                            },
                            new CardContent
                            {
                                Class = "px-4",
                                Children =
                                [
                                    new Div
                                    {
                                        Class = "s1-stack",
                                        Children =
                                        [
                                            new Div
                                            {
                                                Class = "s1-field",
                                                Children =
                                                [
                                                    new Label { For = "name", Children = [new UIText("Name")] },
                                                    new Input { Id = "name", Placeholder = "Ada Lovelace" },
                                                ],
                                            },
                                            new Div
                                            {
                                                Class = "s1-row",
                                                Children =
                                                [
                                                    new Checkbox { Id = "updates", DefaultChecked = true },
                                                    new Label { For = "updates", Children = [new UIText("Product updates")] },
                                                ],
                                            },
                                            new Div
                                            {
                                                Class = "s1-row",
                                                Children =
                                                [
                                                    new Switch { Id = "compact", DefaultChecked = true },
                                                    new Label { For = "compact", Children = [new UIText("Compact layout")] },
                                                ],
                                            },
                                        ],
                                    },
                                ],
                            },
                            new CardFooter
                            {
                                Class = "px-4 pt-1",
                                Children =
                                [
                                    new Button { Rest = new AttributeDictionary { ["id"] = "cancel" }, Variant = ButtonVariant.Outline, Children = [new UIText("Cancel")] },
                                    new Button { Rest = new AttributeDictionary { ["id"] = "save" }, Children = [new UIText("Save")] },
                                ],
                            },
                        ],
                    },
                    new Card
                    {
                        Class = "gap-3 py-3",
                        Children =
                        [
                            new CardHeader
                            {
                                Class = "px-4",
                                Children =
                                [
                                    new CardTitle { Children = [new UIText("Team plan")] },
                                    new CardDescription { Children = [new UIText("3 of 5 seats used")] },
                                ],
                            },
                            new CardContent
                            {
                                Class = "px-4",
                                Children =
                                [
                                    new Div
                                    {
                                        Class = "s1-stack",
                                        Children =
                                        [
                                            new Div
                                            {
                                                Class = "s1-row s1-between",
                                                Children =
                                                [
                                                    new Badge { Variant = BadgeVariant.Secondary, Children = [new UIText("Pro")] },
                                                    new UIText { Class = "s1-muted", Content = "$24 / month" },
                                                ],
                                            },
                                            new Progress { Value = 60 },
                                            new Tabs
                                            {
                                                DefaultValue = "overview",
                                                Children =
                                                [
                                                    new TabsList
                                                    {
                                                        Children =
                                                        [
                                                            new TabsTrigger { Value = "overview", Children = [new UIText("Overview")] },
                                                            new TabsTrigger { Value = "billing", Children = [new UIText("Billing")] },
                                                        ],
                                                    },
                                                    new TabsContent
                                                    {
                                                        Value = "overview",
                                                        Children = [new UIText { Class = "s1-text", Content = "Included in your plan." }],
                                                    },
                                                    new TabsContent
                                                    {
                                                        Value = "billing",
                                                        Children = [new UIText { Class = "s1-text", Content = "Next invoice in May." }],
                                                    },
                                                ],
                                            },
                                        ],
                                    },
                                ],
                            },
                        ],
                    },
                    new Card
                    {
                        Class = "s1-wide gap-2 py-3",
                        Children =
                        [
                            new CardHeader
                            {
                                Class = "px-4",
                                Children = [new CardTitle { Children = [new UIText("Members")] }],
                            },
                            new CardContent
                            {
                                Class = "px-4",
                                Children =
                                [
                                    new Table
                                    {
                                        Columns = "3",
                                        Children =
                                        [
                                            new TableHeader
                                            {
                                                Children =
                                                [
                                                    new TableRow
                                                    {
                                                        Children =
                                                        [
                                                            new TableHead { Children = [new UIText("Name")] },
                                                            new TableHead { Children = [new UIText("Role")] },
                                                            new TableHead { Children = [new UIText("Status")] },
                                                        ],
                                                    },
                                                ],
                                            },
                                            new TableBody
                                            {
                                                Children =
                                                [
                                                    new TableRow
                                                    {
                                                        Children =
                                                        [
                                                            new TableCell { Children = [new UIText("Ada Lovelace")] },
                                                            new TableCell { Children = [new UIText("Owner")] },
                                                            new TableCell { Children = [new UIText("Active")] },
                                                        ],
                                                    },
                                                ],
                                            },
                                        ],
                                    },
                                ],
                            },
                        ],
                    },
                ],
            },
        ],
    };

    [Fact]
    public void Gallery_matches_light_and_dark_goldens_and_keeps_geometry_stable()
    {
        var h = Harness();

        using var light = h.Render(Color.White);
        using var lightAgain = h.Render(Color.White);
        Assert.Equal(light.ToRgbaUnpremultiplied(), lightAgain.ToRgbaUnpremultiplied());
        AssertHasVisibleContent(light, "light");

        var lightButtons = ButtonGeometry(h);
        Assert.NotEmpty(lightButtons);
        Assert.Equal(36f, h.ById("save").GetBoundingClientRect().Height, 2);
        Assert.Equal(36f, h.ById("cancel").GetBoundingClientRect().Height, 2);
        Assert.True(h.ById("save").GetBoundingClientRect().Left > h.ById("cancel").GetBoundingClientRect().Left);
        Assert.Equal(h.ById("cancel").GetBoundingClientRect().Top, h.ById("save").GetBoundingClientRect().Top, 2);
        AssertNoOverlaps(h.FindAll(e => e.GetAttribute("data-slot") == "card" && e.IsVisible), "cards");
        AssertNoOverlaps(h.FindAll(e => e.GetAttribute("data-slot") == "button" && e.IsVisible), "buttons");

        ShadcnTheme.SetDark(h.Root, true);
        h.Frame();

        using var dark = h.Render(Color.White);
        AssertHasVisibleContent(dark, "dark");
        Assert.Equal(lightButtons, ButtonGeometry(h));
        Assert.NotEqual(light.ToRgbaUnpremultiplied(), dark.ToRgbaUnpremultiplied());
        Assert.True(light.GetPixel(2, 2).R > dark.GetPixel(2, 2).R + 0.2f,
            $"expected a light page and a dark page, got {light.GetPixel(2, 2)} and {dark.GetPixel(2, 2)}");

        GoldenImage.AssertMatches("shadcn-s1-light", light);
        GoldenImage.AssertMatches("shadcn-s1-dark", dark);
    }

    static List<string> ButtonGeometry(UIHarness h) =>
        h.FindAll(e => e.GetAttribute("data-slot") == "button" && e.IsVisible)
            .Select(e => e.GetBoundingClientRect())
            .OrderBy(r => r.Top)
            .ThenBy(r => r.Left)
            .Select(r => FormattableString.Invariant($"{r.X:F2},{r.Y:F2},{r.Width:F2},{r.Height:F2}"))
            .ToList();

    static void AssertNoOverlaps(IReadOnlyList<Element> elements, string what)
    {
        for (int i = 0; i < elements.Count; i++)
        {
            var a = elements[i].GetBoundingClientRect();
            Assert.True(a.Width > 0 && a.Height > 0, $"{what}[{i}] has an empty box: {a}");
            Assert.True(a.Left >= -0.5f && a.Top >= -0.5f && a.Right <= Width + 0.5f && a.Bottom <= Height + 0.5f,
                $"{what}[{i}] is outside the viewport: {a}");
            for (int j = i + 1; j < elements.Count; j++)
            {
                var b = elements[j].GetBoundingClientRect();
                Assert.False(a.Intersects(b), $"{what}[{i}] {a} overlaps {what}[{j}] {b}");
            }
        }
    }

    static void AssertHasVisibleContent(Bitmap bitmap, string theme)
    {
        Assert.Equal(640, bitmap.Width);
        Assert.Equal(480, bitmap.Height);
        var colors = new HashSet<int>();
        int opaque = 0;
        for (int y = 0; y < bitmap.Height; y += 4)
            for (int x = 0; x < bitmap.Width; x += 4)
            {
                var c = bitmap.GetPixel(x, y);
                if (c.A > 0.5f) opaque++;
                colors.Add(((int)(c.R * 255) << 16) | ((int)(c.G * 255) << 8) | (int)(c.B * 255));
            }
        Assert.True(opaque > 0, $"{theme} rendering is fully transparent");
        Assert.True(colors.Count >= 16, $"{theme} rendering is nearly blank ({colors.Count} sampled colours)");
    }
}
