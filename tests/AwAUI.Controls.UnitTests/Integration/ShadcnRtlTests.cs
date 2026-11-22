using AwAUI.Controls.UnitTests.Integration;
using AwAUI.Graphics;
using AwAUI.Icons.Lucide;
using AwAUI.UI;
using Side = AwAUI.Controls.Primitives.Side;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Shadcn.Integration;

/// <summary>
/// Shadcn S1 的 RTL 验收：根元素带 <c>dir="rtl"</c>；引擎把 <c>dir</c> 映射为 <c>direction</c>（表现提示）并传播到布局与文本，
/// 因此 flex 主轴、文本 start / end 对齐、行内起止内边距 / 外边距（<c>padding-inline-*</c> / <c>margin-inline-*</c>）
/// 与嵌套行都自动镜像；图标翻转仍用 <c>transform: scaleX(-1)</c>。场景使用真实 Shadcn 组件（Button / Card /
/// Input / Checkbox / Switch / Badge / Progress / Tabs / Table / Sheet）与真实 Tailwind + shadcn 样式表。
/// <para>
/// 黄金图片是 AwAUI 自身渲染的参考图，不是官网截图。
/// </para>
/// </summary>
public class ShadcnRtlTests
{
    static ShadcnRtlTests() => ShadcnTheme.Register();

    const float Width = 640;
    const float Height = 480;

    const string RtlCss = """
        root { background-color: var(--background); color: var(--foreground); }
        .rtl-page { box-sizing: border-box; width: 640px; min-height: 480px; padding: 16px; display: flex; flex-direction: column; gap: 12px;
                    background-color: var(--background); color: var(--foreground); }
        .rtl-row { display: flex; align-items: center; gap: 8px; }
        .rtl-between { justify-content: space-between; }
        .rtl-grid { display: grid; grid-template-columns: 1fr 1fr; align-items: start; gap: 12px; }
        .rtl-stack { display: flex; flex-direction: column; gap: 8px; }
        .rtl-heading { display: flex; flex-direction: column; gap: 2px; }
        .rtl-title { font-size: 18px; line-height: 1.2; font-weight: 600; }
        .rtl-subtitle, .rtl-muted { font-size: 12px; color: var(--muted-foreground); }
        .rtl-wide { grid-column: 1 / -1; }
        .rtl-row-box { display: flex; width: 400px; gap: 8px; background-color: var(--muted); }
        .rtl-inset { display: block; width: 200px; padding-inline-start: 8px; padding-inline-end: 32px; background-color: var(--muted); }
        .rtl-inset .rtl-inset-child { display: block; margin-inline-start: 4px; margin-inline-end: 6px; }
        .rtl-align { width: 200px; text-align: start; }
        .rtl-icon-box { display: inline-flex; align-items: center; justify-content: center; width: 24px; height: 24px; }
        .rtl-mirror:dir(rtl) { transform: scaleX(-1); }
        """;

    static Div Page(bool rtl, params Node[] children) => new()
    {
        Class = "rtl-page",
        Attributes = { ["dir"] = rtl ? "rtl" : "ltr" },
        Children = new NodeList(children),
    };

    static UIHarness Probe(bool rtl, Node content) =>
        new(() => Page(rtl, content), RtlCss, Width, Height, scale: 1, appStyleSheets: true);

    static UIHarness Harness() => new(Scene, RtlCss, Width, Height, scale: 1, appStyleSheets: true);

    static Node Scene() => Page(true,
        new Div
        {
            Class = "rtl-row rtl-between",
            Children =
            [
                new Div
                {
                    Class = "rtl-heading",
                    Children =
                    [
                        new UIText { Class = "rtl-title", Content = "Component gallery" },
                        new UIText { Class = "rtl-subtitle", Content = "RTL reference" },
                    ],
                },
                new Div
                {
                    Class = "rtl-row",
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
            Class = "rtl-grid",
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
                                    Class = "rtl-stack",
                                    Children =
                                    [
                                        new Div
                                        {
                                            Class = "rtl-stack",
                                            Children =
                                            [
                                                new Label { For = "name", Children = [new UIText("Name")] },
                                                new Input { Id = "name", Placeholder = "Ada Lovelace" },
                                            ],
                                        },
                                        new Div
                                        {
                                            Class = "rtl-row",
                                            Children =
                                            [
                                                new Checkbox { Id = "updates", DefaultChecked = true },
                                                new Label { For = "updates", Children = [new UIText("Product updates")] },
                                            ],
                                        },
                                        new Div
                                        {
                                            Class = "rtl-row",
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
                                    Class = "rtl-stack",
                                    Children =
                                    [
                                        new Div
                                        {
                                            Class = "rtl-row rtl-between",
                                            Children =
                                            [
                                                new Badge { Variant = BadgeVariant.Secondary, Children = [new UIText("Pro")] },
                                                new UIText { Class = "rtl-muted", Content = "$24 / month" },
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
                                                    Children = [new UIText("Included in your plan.")],
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
                    Class = "rtl-wide gap-2 py-3",
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
        });

    [Fact]
    public void Flex_rows_mirror_under_dir_rtl()
    {
        var ltr = Probe(false, new Div
        {
            Id = "row-box",
            Class = "rtl-row-box",
            Children =
            [
                new Button { Rest = new AttributeDictionary { ["id"] = "first" }, Children = [new UIText("First")] },
                new Button { Rest = new AttributeDictionary { ["id"] = "second" }, Children = [new UIText("Second")] },
            ],
        });
        var rtl = Probe(true, new Div
        {
            Id = "row-box",
            Class = "rtl-row-box",
            Children =
            [
                new Button { Rest = new AttributeDictionary { ["id"] = "first" }, Children = [new UIText("First")] },
                new Button { Rest = new AttributeDictionary { ["id"] = "second" }, Children = [new UIText("Second")] },
            ],
        });

        var ltrBox = ltr.ById("row-box").GetBoundingClientRect();
        var rtlBox = rtl.ById("row-box").GetBoundingClientRect();
        var ltrFirst = ltr.ById("first").GetBoundingClientRect();
        var ltrSecond = ltr.ById("second").GetBoundingClientRect();
        var rtlFirst = rtl.ById("first").GetBoundingClientRect();
        var rtlSecond = rtl.ById("second").GetBoundingClientRect();

        Assert.True(ltrFirst.Left < ltrSecond.Left, "LTR: the first button leads from the left");
        Assert.True(rtlFirst.Left > rtlSecond.Left, "RTL: the first button leads from the right");
        Assert.Equal(ltrFirst.Left - ltrBox.Left, rtlBox.Right - rtlFirst.Right, 1);
        Assert.Equal(ltrBox.Right - ltrSecond.Right, rtlSecond.Left - rtlBox.Left, 1);
    }

    [Fact]
    public void Inline_start_and_end_insets_swap_under_dir_rtl()
    {
        Node ProbeBox() => new Div
        {
            Id = "inset-box",
            Class = "rtl-inset",
            Children = [new Button { Rest = new AttributeDictionary { ["id"] = "inset-child" }, Class = "rtl-inset-child", Children = [new UIText("Inset")] }],
        };
        var ltr = Probe(false, ProbeBox());
        var rtl = Probe(true, ProbeBox());
        var ltrChild = ltr.ById("inset-child").GetBoundingClientRect();
        var ltrBox = ltr.ById("inset-box").GetBoundingClientRect();
        var rtlChild = rtl.ById("inset-child").GetBoundingClientRect();
        var rtlBox = rtl.ById("inset-box").GetBoundingClientRect();

        Assert.Equal(12, ltrChild.Left - ltrBox.Left, 1);
        Assert.Equal(38, ltrBox.Right - ltrChild.Right, 1);
        Assert.Equal(38, rtlChild.Left - rtlBox.Left, 1);
        Assert.Equal(12, rtlBox.Right - rtlChild.Right, 1);
    }

    [Fact]
    public void Text_alignment_flips_under_dir_rtl()
    {
        Node ProbeText() => new Div
        {
            Id = "align-box",
            Class = "rtl-align",
            Children = [new UIText { Content = "Right aligned" }],
        };
        var ltr = Probe(false, ProbeText());
        var rtl = Probe(true, ProbeText());
        using var ltrImage = ltr.Render(Color.White);
        using var rtlImage = rtl.Render(Color.White);
        var ltrBox = ltr.ById("align-box").GetBoundingClientRect();
        var rtlBox = rtl.ById("align-box").GetBoundingClientRect();

        var (ltrStart, _) = InkBounds(ltrImage, ltrBox);
        Assert.True(ltrStart - ltrBox.Left < 24, $"LTR text should start near the left edge, ink starts at {ltrStart - ltrBox.Left:F1}px");

        var (_, rtlEnd) = InkBounds(rtlImage, rtlBox);
        Assert.True(rtlBox.Right - rtlEnd < 24, $"RTL text should end near the right edge, ink ends {rtlBox.Right - rtlEnd:F1}px before it");
    }

    [Fact]
    public void Icons_mirror_under_dir_rtl()
    {
        Node ProbeIcon() => new Div
        {
            Id = "mirror-box",
            Class = "rtl-icon-box",
            Children = [new Icon(LucideIcons.ChevronRight) { Size = "24px", Class = "rtl-mirror" }],
        };
        var ltr = Probe(false, ProbeIcon());
        var rtl = Probe(true, ProbeIcon());
        using var ltrImage = ltr.Render(Color.White);
        using var rtlImage = rtl.Render(Color.White);
        var ltrBox = ltr.ById("mirror-box").GetBoundingClientRect();
        var rtlBox = rtl.ById("mirror-box").GetBoundingClientRect();

        Assert.Equal(ltrBox.Width, rtlBox.Width, 1);
        Assert.Equal(ltrBox.Height, rtlBox.Height, 1);
        var (ltrStart, ltrEnd) = InkBounds(ltrImage, ltrBox);
        var (rtlStart, rtlEnd) = InkBounds(rtlImage, rtlBox);
        Assert.True(Math.Abs((ltrEnd - ltrBox.Left) - (rtlBox.Right - rtlStart)) <= 1, "the mirrored icon's right-side ink should mirror the LTR left-side ink");
        Assert.True(Math.Abs((ltrBox.Right - ltrStart) - (rtlEnd - rtlBox.Left)) <= 1, "the mirrored icon's left-side ink should mirror the LTR right-side ink");
    }

    [Fact]
    public void Sheet_places_the_default_side_on_the_inline_start_edge()
    {
        var h = Probe(true, new Sheet
        {
            DefaultOpen = true,
            Children =
            [
                new SheetTrigger { Children = [new UIText("Preferences")] },
                new SheetContent
                {
                    Side = Side.Right,
                    Children = [new SheetTitle { Children = [new UIText("Preferences")] }],
                },
            ],
        });

        var panel = h.FindAll(e => e.GetAttribute("data-slot") == "sheet-content" && e.IsVisible).Single();
        var box = panel.GetBoundingClientRect();
        Assert.Equal("right", panel.GetAttribute("data-side"));
        Assert.Equal(Width, box.Right, 1);
        Assert.Equal(Width - box.Width, box.Left, 1);
        Assert.True(box.Width <= Width * 0.75f + 0.5f, $"the right sheet should be anchored to the inline-start (right) edge in RTL, got {box}");
    }

    [Theory]
    [InlineData(false, Side.InlineStart, "left")]
    [InlineData(true, Side.InlineStart, "right")]
    [InlineData(false, Side.InlineEnd, "right")]
    [InlineData(true, Side.InlineEnd, "left")]
    public void Sheet_accepts_a_logical_side_that_follows_the_direction(bool rtl, Side side, string physical)
    {
        var h = Probe(rtl, new Sheet
        {
            DefaultOpen = true,
            Children =
            [
                new SheetTrigger { Children = [new UIText("Preferences")] },
                new SheetContent
                {
                    Side = side,
                    Children = [new SheetTitle { Children = [new UIText("Preferences")] }],
                },
            ],
        });
        // Portal 挂在覆盖层（根元素的最后子元素），文档方向来自根元素而不是触发按钮所在子树
        h.Root.RootElement.SetAttributeOverride("dir", rtl ? "rtl" : null);
        h.Frame();

        var panel = h.FindAll(e => e.GetAttribute("data-slot") == "sheet-content" && e.IsVisible).Single();
        var box = panel.GetBoundingClientRect();
        Assert.Equal(side == Side.InlineStart ? "inline-start" : "inline-end", panel.GetAttribute("data-side"));
        if (physical == "left")
        {
            Assert.Equal(0, box.Left, 1);
            Assert.Equal(box.Width, box.Right, 1);
        }
        else
        {
            Assert.Equal(Width, box.Right, 1);
            Assert.Equal(Width - box.Width, box.Left, 1);
        }
    }

    [Fact]
    public void DropdownMenu_submenu_opens_on_the_inline_end_side_in_rtl()
    {
        var h = Probe(true, new DropdownMenu
        {
            Children =
            [
                new DropdownMenuTrigger { Children = [new UIText("Actions")] },
                new DropdownMenuContent
                {
                    Children =
                    [
                        new DropdownMenuSub
                        {
                            Children =
                            [
                                new DropdownMenuSubTrigger { Children = [new UIText("More")] },
                                new DropdownMenuSubContent
                                {
                                    Children = [new DropdownMenuItem { Children = [new UIText("Nested")] }],
                                },
                            ],
                        },
                    ],
                },
            ],
        });
        // Portal 位于根元素之下，文档方向从根元素继承
        h.Root.RootElement.SetAttributeOverride("dir", "rtl");
        h.Frame();

        h.Click(h.FindAll(e => e.GetAttribute("data-slot") == "dropdown-menu-trigger").Single());
        var subTrigger = h.FindAll(e => e.GetAttribute("data-slot") == "dropdown-menu-sub-trigger").Single();
        h.Click(subTrigger);

        var sub = h.FindAll(e => e.GetAttribute("data-slot") == "dropdown-menu-sub-content" && e.IsVisible).Single();
        Assert.Equal("left", sub.GetAttribute("data-side"));
        Assert.True(sub.GetBoundingClientRect().Right <= subTrigger.GetBoundingClientRect().Left + 1,
            $"RTL: the submenu should open on the inline-end (left) side, sub={sub.GetBoundingClientRect()} trigger={subTrigger.GetBoundingClientRect()}");
    }

    [Fact]
    public void Gallery_matches_rtl_golden_and_stays_well_formed()
    {
        var h = Harness();
        using var image = h.Render(Color.White);
        using var again = h.Render(Color.White);
        Assert.Equal(image.ToRgbaUnpremultiplied(), again.ToRgbaUnpremultiplied());
        Assert.Equal(640, image.Width);
        Assert.Equal(480, image.Height);
        Assert.True(CountSampledColours(image) >= 16, "RTL rendering is nearly blank");

        var cards = h.FindAll(e => e.GetAttribute("data-slot") == "card" && e.IsVisible);
        Assert.Equal(3, cards.Count);
        for (int i = 0; i < cards.Count; i++)
        {
            var a = cards[i].GetBoundingClientRect();
            Assert.True(a.Left >= -0.5f && a.Right <= Width + 0.5f && a.Bottom <= Height + 0.5f, $"card[{i}] is outside the viewport: {a}");
            for (int j = i + 1; j < cards.Count; j++)
                Assert.False(a.Intersects(cards[j].GetBoundingClientRect()), $"card[{i}] overlaps card[{j}]");
        }

        var footer = h.FindAll(e => e.GetAttribute("data-slot") == "card-footer" && e.IsVisible).First();
        var save = h.ById("save").GetBoundingClientRect();
        var cancel = h.ById("cancel").GetBoundingClientRect();
        Assert.True(save.Left < cancel.Left,
            $"RTL: the default action should be mirrored to the inline-start side of the footer; footer class={footer.ClassName} save={save} cancel={cancel}");

        GoldenImage.AssertMatches("shadcn-s1-rtl", image);
    }

    static (int Start, int End) InkBounds(Bitmap bitmap, Rect box)
    {
        int left = Math.Max(0, (int)Math.Floor(box.Left));
        int right = Math.Min(bitmap.Width - 1, (int)Math.Ceiling(box.Right));
        int top = Math.Max(0, (int)Math.Floor(box.Top));
        int bottom = Math.Min(bitmap.Height - 1, (int)Math.Ceiling(box.Bottom));
        int start = int.MaxValue, end = -1;
        for (int y = top; y <= bottom; y++)
            for (int x = left; x <= right; x++)
                if (bitmap.GetPixel(x, y).R < 0.5f)
                {
                    start = Math.Min(start, x);
                    end = Math.Max(end, x);
                }
        Assert.True(end >= 0, "no text ink in the expected box");
        return (start, end);
    }

    static int CountSampledColours(Bitmap bitmap)
    {
        var colors = new HashSet<int>();
        for (int y = 0; y < bitmap.Height; y += 4)
            for (int x = 0; x < bitmap.Width; x += 4)
            {
                var c = bitmap.GetPixel(x, y);
                colors.Add(((int)(c.R * 255) << 16) | ((int)(c.G * 255) << 8) | (int)(c.B * 255));
            }
        return colors.Count;
    }
}
