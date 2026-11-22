using AwAUI.Controls.UnitTests.Integration;
using AwAUI.UI;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Shadcn.Integration;

/// <summary>Shadcn S1 静态部件的语义验收：角色、ARIA 状态、原生元素与数据槽位。</summary>
public class ShadcnSemanticsTests
{
    static ShadcnSemanticsTests() => ShadcnTheme.Register();

    static UIHarness Harness(Func<Node> render) =>
        new(render, appStyleSheets: true, width: 640, height: 480);

    static string? A(Element e, string name) => e.GetAttribute(name);

    static Element BySlot(UIHarness h, string slot) =>
        h.FindAll(e => A(e, "data-slot") == slot).First();

    // ------------------------------------------------------------ 静态容器

    [Fact]
    public void Card_parts_expose_data_slots()
    {
        var h = Harness(() => new Card
        {
            Children =
            [
                new CardHeader
                {
                    Children =
                    [
                        new CardTitle { Children = [new UIText("Title")] },
                        new CardDescription { Children = [new UIText("Description")] },
                        new CardAction { Children = [new UIText("Action")] },
                    ],
                },
                new CardContent { Children = [new UIText("Content")] },
                new CardFooter { Children = [new UIText("Footer")] },
            ],
        });

        foreach (var slot in new[] { "card", "card-header", "card-title", "card-description", "card-action", "card-content", "card-footer" })
            Assert.NotNull(h.FindAll(e => A(e, "data-slot") == slot).FirstOrDefault());
    }

    [Fact]
    public void Alert_uses_a_live_region_and_badge_keeps_its_variant()
    {
        var h = Harness(() => new Alert
        {
            Variant = AlertVariant.Destructive,
            Children =
            [
                new AlertTitle { Children = [new UIText("Draft saved")] },
                new AlertDescription { Children = [new UIText("Available for this session")] },
                new Badge { Variant = BadgeVariant.Secondary, Children = [new UIText("Beta")] },
            ],
        });

        var alert = BySlot(h, "alert");
        Assert.Equal(Role.Alert, alert.Role);
        Assert.Contains("text-destructive", alert.ClassName);

        var badge = BySlot(h, "badge");
        Assert.Equal("secondary", A(badge, "data-variant"));
    }

    [Fact]
    public void Separator_distinguishes_decorative_and_semantic_roles()
    {
        var h = Harness(() => new Div
        {
            Children =
            [
                new Separator(),
                new Separator { Decorative = false },
                new Separator { Decorative = false, Orientation = SeparatorOrientation.Vertical },
            ],
        });

        var separators = h.FindAll(e => A(e, "data-slot") == "separator");
        Assert.Equal(3, separators.Count);
        Assert.Equal(Role.Presentation, separators[0].Role);
        Assert.Equal(Role.Separator, separators[1].Role);
        Assert.Null(A(separators[1], "aria-orientation"));
        Assert.Equal("vertical", A(separators[2], "aria-orientation"));
        Assert.Equal("vertical", A(separators[2], "data-orientation"));
    }

    [Fact]
    public void Kbd_skeleton_and_spinner_carry_status_semantics()
    {
        var h = Harness(() => new Div
        {
            Children =
            [
                new Kbd { Children = [new UIText("Tab")] },
                new Skeleton(),
                new Spinner(),
            ],
        });

        Assert.Equal("kbd", BySlot(h, "kbd").Tag);
        Assert.Equal("div", BySlot(h, "skeleton").Tag);
        var spinner = BySlot(h, "spinner");
        Assert.Equal(Role.Status, spinner.Role);
        Assert.Equal("Loading", A(spinner, "aria-label"));
    }

    [Fact]
    public void AspectRatio_wraps_content_in_an_inline_ratio_box()
    {
        var h = Harness(() => new AspectRatio
        {
            Ratio = 2,
            Children = [new UIText("16:8")],
        });

        var wrapper = h.FindAll(e => A(e, "data-radix-aspect-ratio-wrapper") is not null).Single();
        Assert.Contains("aspect-ratio", A(wrapper, "style"));
        Assert.Contains("2", A(wrapper, "style"));
        Assert.NotNull(h.FindAll(e => A(e, "data-slot") == "aspect-ratio").FirstOrDefault());
    }

    [Fact]
    public void Avatar_shows_the_fallback_when_no_image_is_loaded()
    {
        var h = Harness(() => new Avatar
        {
            Children =
            [
                new AvatarImage { Alt = "Alex Rivera" },
                new AvatarFallback { Children = [new UIText("AR")] },
            ],
        });

        Assert.True(BySlot(h, "avatar-fallback").IsVisible);
        Assert.False(BySlot(h, "avatar-image").IsVisible);
    }

    // ------------------------------------------------------------ 进度

    [Fact]
    public void Progress_reports_value_and_indeterminate_states()
    {
        var h = Harness(() => new Div
        {
            Children =
            [
                new Progress { Value = 25, Max = 100 },
                new Progress { Value = 100 },
                new Progress(),
            ],
        });

        var progresses = h.FindAll(e => A(e, "data-slot") == "progress");
        Assert.Equal(3, progresses.Count);
        var quarter = progresses[0];
        Assert.Equal(Role.ProgressBar, quarter.Role);
        Assert.Equal("0", A(quarter, "aria-valuemin"));
        Assert.Equal("100", A(quarter, "aria-valuemax"));
        Assert.Equal("25", A(quarter, "aria-valuenow"));
        Assert.Equal("25%", A(quarter, "aria-valuetext"));
        Assert.Equal("loading", A(quarter, "data-state"));

        var full = progresses[1];
        Assert.Equal("complete", A(full, "data-state"));
        Assert.Equal("100%", A(full, "aria-valuetext"));

        var idle = progresses[2];
        Assert.Null(A(idle, "aria-valuenow"));
        Assert.Equal("indeterminate", A(idle, "data-state"));
    }

    // ------------------------------------------------------------ 表格

    [Fact]
    public void Table_uses_native_table_elements()
    {
        var h = Harness(() => new Table
        {
            Columns = "2",
            Children =
            [
                new TableHeader
                {
                    Children = [new TableRow { Children = [new TableHead { Children = [new UIText("Name")] }, new TableHead { Children = [new UIText("Role")] }] }],
                },
                new TableBody
                {
                    Children = [new TableRow { Children = [new TableCell { Children = [new UIText("Alex")] }, new TableCell { Children = [new UIText("Owner")] }] }],
                },
                new TableFooter { Children = [new TableRow { Children = [new TableCell { Children = [new UIText("2 people")] }] }] },
                new TableCaption { Children = [new UIText("Members")] },
            ],
        });

        Assert.Equal("table", BySlot(h, "table").Tag);
        Assert.Equal("thead", BySlot(h, "table-header").Tag);
        Assert.Equal("tbody", BySlot(h, "table-body").Tag);
        Assert.Equal("tfoot", BySlot(h, "table-footer").Tag);
        Assert.Equal("tr", BySlot(h, "table-row").Tag);
        Assert.Equal("th", BySlot(h, "table-head").Tag);
        Assert.Equal("td", BySlot(h, "table-cell").Tag);
        Assert.Equal("caption", BySlot(h, "table-caption").Tag);
        Assert.Contains("--aw-table-columns", A(BySlot(h, "table"), "style"));
    }

    // ------------------------------------------------------------ 文本输入

    [Fact]
    public void Input_and_Textarea_report_edited_values()
    {
        string? input = null;
        string? area = null;
        var h = Harness(() => new Div
        {
            Children =
            [
                new Input { Id = "i", Placeholder = "Name", ValueChanged = v => input = v },
                new Textarea { Id = "t", ValueChanged = v => area = v },
            ],
        });

        h.Click(h.ById("i"));
        h.TypeText("Alex");
        Assert.Equal("Alex", input);

        var textarea = h.ById("t");
        Assert.Equal(Role.TextBox, textarea.Role);
        h.Click(textarea);
        h.TypeText("Notes");
        Assert.Equal("Notes", area);
    }

    [Fact]
    public void Label_click_focuses_the_associated_input()
    {
        var h = Harness(() => new Div
        {
            Children =
            [
                new Input { Id = "name" },
                new Label { For = "name", Children = [new UIText("Name")] },
            ],
        });

        h.Click(BySlot(h, "label"));
        Assert.True(h.ById("name").IsFocused);
    }

    // ------------------------------------------------------------ 滚动区域

    [Fact]
    public void ScrollArea_exposes_a_focusable_viewport()
    {
        var h = Harness(() => new ScrollArea
        {
            Children = [new Div { Children = [new UIText("Content")] }],
        });

        Assert.NotNull(h.FindAll(e => A(e, "data-slot") == "scroll-area").FirstOrDefault());
        var viewport = BySlot(h, "scroll-area-viewport");
        Assert.Equal("0", A(viewport, "tabindex"));
    }
}
