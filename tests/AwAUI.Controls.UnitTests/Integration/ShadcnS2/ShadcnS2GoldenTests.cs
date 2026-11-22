using AwAUI.Controls.UnitTests.Integration;
using AwAUI.Graphics;
using AwAUI.UI;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Shadcn.Integration.S2;

/// <summary>
/// Shadcn S2 的代表性场景：真实 <see cref="AwAUI.Shadcn"/> 组件 + 真实的 Tailwind / shadcn 样式表，由方块字体在
/// 640×480、1x 下确定性渲染。除像素比较外，测试还断言分格输入、分页与卡片几何稳定，明暗主题只改变颜色，
/// 并且 <c>dir="rtl"</c> 时行内顺序镜像。
/// <para>
/// 黄金图片是 AwAUI 自身渲染的参考图，不是 shadcn 官网截图。
/// </para>
/// </summary>
public class ShadcnS2GoldenTests
{
    static ShadcnS2GoldenTests() => ShadcnTheme.Register();

    const float Width = 640;
    const float Height = 620;

    const string GalleryCss = """
        root { background-color: var(--background); color: var(--foreground); }
        .s2-page { box-sizing: border-box; min-height: 480px; padding: 16px; display: flex; flex-direction: column; gap: 12px;
                   background-color: var(--background); color: var(--foreground); }
        .s2-header { display: flex; align-items: center; justify-content: space-between; gap: 12px; }
        .s2-heading { display: flex; flex-direction: column; gap: 2px; }
        .s2-title { font-size: 18px; line-height: 1.2; font-weight: 600; }
        .s2-subtitle, .s2-muted { font-size: 12px; color: var(--muted-foreground); }
        .s2-grid { display: grid; grid-template-columns: 1fr 1fr; align-items: start; gap: 12px; }
        .s2-stack { display: flex; flex-direction: column; gap: 8px; }
        .s2-row { display: flex; align-items: center; gap: 6px; }
        .s2-wide { grid-column: 1 / -1; }
        """;

    static UIHarness Harness(bool rtl = false) =>
        new(() => Page(rtl), GalleryCss, Width, Height, scale: 1, appStyleSheets: true);

    static Div Page(bool rtl)
    {
        var page = new Div
        {
            Class = "s2-page",
            Children =
            [
            new Div
            {
                Class = "s2-header",
                Children =
                [
                    new Div
                    {
                        Class = "s2-heading",
                        Children =
                        [
                            new UIText { Class = "s2-title", Content = "S2 gallery" },
                            new UIText { Class = "s2-subtitle", Content = "Composition, data, and navigation parts" },
                        ],
                    },
                    new Badge { Variant = BadgeVariant.Secondary, Children = [new UIText("S2")] },
                ],
            },
            new Div
            {
                Class = "s2-grid",
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
                                Children = [new CardTitle { Children = [new UIText("Composition")] }],
                            },
                            new CardContent
                            {
                                Class = "px-4",
                                Children =
                                [
                                    new Div
                                    {
                                        Class = "s2-stack",
                                        Children =
                                        [
                                            new Breadcrumb
                                            {
                                                Children =
                                                [
                                                    new BreadcrumbList
                                                    {
                                                        Children =
                                                        [
                                                            new BreadcrumbItem { Children = [new BreadcrumbLink { Href = "#", Children = [new UIText("Workspace")] }] },
                                                            new BreadcrumbSeparator(),
                                                            new BreadcrumbItem { Children = [new BreadcrumbPage { Children = [new UIText("Projects")] }] },
                                                        ],
                                                    },
                                                ],
                                            },
                                            new Field
                                            {
                                                Children =
                                                [
                                                    new FieldLabel { For = "email", Children = [new UIText("Email")] },
                                                    new Input { Id = "email", Placeholder = "you@example.com" },
                                                    new FieldDescription { Children = [new UIText("Used for invitations.")] },
                                                ],
                                            },
                                            new InputOTP
                                            {
                                                MaxLength = 3,
                                                AriaLabel = "Verification code",
                                                DefaultValue = "12",
                                                Children =
                                                [
                                                    new InputOTPGroup
                                                    {
                                                        Children =
                                                        [
                                                            new InputOTPSlot { Index = 0 },
                                                            new InputOTPSlot { Index = 1 },
                                                            new InputOTPSlot { Index = 2 },
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
                    new Card
                    {
                        Class = "gap-3 py-3",
                        Children =
                        [
                            new CardHeader
                            {
                                Class = "px-4",
                                Children = [new CardTitle { Children = [new UIText("Data")] }],
                            },
                            new CardContent
                            {
                                Class = "px-4",
                                Children =
                                [
                                    new Div
                                    {
                                        Class = "s2-stack",
                                        Children =
                                        [
                                            new ItemGroup
                                            {
                                                Children =
                                                [
                                                    new Item
                                                    {
                                                        Variant = ItemVariant.Outline,
                                                        Children =
                                                        [
                                                            new ItemContent
                                                            {
                                                                Children =
                                                                [
                                                                    new ItemTitle { Children = [new UIText("Design system")] },
                                                                    new ItemDescription { Children = [new UIText("12 files changed")] },
                                                                ],
                                                            },
                                                            new ItemActions
                                                            {
                                                                Children = [new Badge { Variant = BadgeVariant.Secondary, Children = [new UIText("Draft")] }],
                                                            },
                                                        ],
                                                    },
                                                    new Item
                                                    {
                                                        Variant = ItemVariant.Muted,
                                                        Children =
                                                        [
                                                            new ItemContent
                                                            {
                                                                Children =
                                                                [
                                                                    new ItemTitle { Children = [new UIText("Documentation")] },
                                                                    new ItemDescription { Children = [new UIText("Updated yesterday")] },
                                                                ],
                                                            },
                                                        ],
                                                    },
                                                ],
                                            },
                                            new Empty
                                            {
                                                Children =
                                                [
                                                    new EmptyHeader
                                                    {
                                                        Children =
                                                        [
                                                            new EmptyTitle { Children = [new UIText("No results")] },
                                                            new EmptyDescription { Children = [new UIText("Try a different search.")] },
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
                    new Card
                    {
                        Class = "s2-wide gap-2 py-3",
                        Children =
                        [
                            new CardHeader
                            {
                                Class = "px-4",
                                Children = [new CardTitle { Children = [new UIText("Pagination")] }],
                            },
                            new CardContent
                            {
                                Class = "px-4",
                                Children =
                                [
                                    new Pagination
                                    {
                                        Children =
                                        [
                                            new PaginationContent
                                            {
                                                Children =
                                                [
                                                    new PaginationItem { Children = [new PaginationPrevious { Href = "#" }] },
                                                    new PaginationItem { Children = [new PaginationLink { Href = "#", IsActive = true, Children = [new UIText("1")] }] },
                                                    new PaginationItem { Children = [new PaginationLink { Href = "#", Children = [new UIText("2")] }] },
                                                    new PaginationItem { Children = [new PaginationEllipsis()] },
                                                    new PaginationItem { Children = [new PaginationNext { Href = "#" }] },
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
        if (rtl) page.Attributes["dir"] = "rtl";
        return page;
    }

    [Fact]
    public void S2_gallery_matches_light_and_dark_goldens_and_keeps_geometry_stable()
    {
        var h = Harness();

        using var light = h.Render(Color.White);
        using var lightAgain = h.Render(Color.White);
        Assert.Equal(light.ToRgbaUnpremultiplied(), lightAgain.ToRgbaUnpremultiplied());
        AssertHasVisibleContent(light, "light");

        var slots = h.FindAll(e => e.GetAttribute("data-slot") == "input-otp-slot" && e.IsVisible)
            .OrderBy(e => e.GetBoundingClientRect().Left)
            .ToList();
        Assert.Equal(3, slots.Count);
        for (int i = 0; i < slots.Count; i++)
        {
            var rect = slots[i].GetBoundingClientRect();
            Assert.Equal(36f, rect.Width, 2);
            Assert.Equal(36f, rect.Height, 2);
            if (i > 0) Assert.True(rect.Left > slots[i - 1].GetBoundingClientRect().Left);
        }

        var geometry = StructuralGeometry(h);
        Assert.NotEmpty(geometry);
        AssertNoOverlaps(h.FindAll(e => e.GetAttribute("data-slot") == "card" && e.IsVisible), "cards");
        AssertNoOverlaps(h.FindAll(e => e.GetAttribute("data-slot") == "pagination-item" && e.IsVisible), "pagination items");

        ShadcnTheme.SetDark(h.Root, true);
        h.Frame();

        using var dark = h.Render(Color.White);
        AssertHasVisibleContent(dark, "dark");
        Assert.Equal(geometry, StructuralGeometry(h));
        Assert.NotEqual(light.ToRgbaUnpremultiplied(), dark.ToRgbaUnpremultiplied());
        Assert.True(light.GetPixel(2, 2).R > dark.GetPixel(2, 2).R + 0.2f,
            $"expected a light page and a dark page, got {light.GetPixel(2, 2)} and {dark.GetPixel(2, 2)}");

        GoldenImage.AssertMatches("shadcn-s2-light", light);
        GoldenImage.AssertMatches("shadcn-s2-dark", dark);
    }

    [Fact]
    public void S2_gallery_mirrors_in_rtl()
    {
        var h = Harness(rtl: true);
        using var rtl = h.Render(Color.White);
        AssertHasVisibleContent(rtl, "rtl");

        var slots = h.FindAll(e => e.GetAttribute("data-slot") == "input-otp-slot" && e.IsVisible)
            .OrderBy(e => e.GetAttribute("data-index"))
            .ToList();
        Assert.Equal(3, slots.Count);
        for (int i = 1; i < slots.Count; i++)
            Assert.True(slots[i].GetBoundingClientRect().Left < slots[i - 1].GetBoundingClientRect().Left,
                "slots should run right to left");

        var items = h.FindAll(e => e.GetAttribute("data-slot") == "pagination-item" && e.IsVisible)
            .OrderBy(e => e.GetBoundingClientRect().Left)
            .Select(e => e.TextContent)
            .ToList();
        Assert.Equal("Next", items[0]);
        Assert.Equal("Previous", items[^1]);

        GoldenImage.AssertMatches("shadcn-s2-rtl", rtl);
    }

    static List<string> StructuralGeometry(UIHarness h) =>
        h.FindAll(e => e.GetAttribute("data-slot") is "card" or "input-otp-slot" or "pagination-item" && e.IsVisible)
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
        Assert.Equal(620, bitmap.Height);
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
