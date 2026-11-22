using AwAUI.Controls.UnitTests.Integration;
using AwAUI.Platform;
using AwAUI.UI;

namespace AwAUI.Shadcn.Integration.S2;

/// <summary>
/// S2 InputOTP 验收：单一逻辑值、自动前进、退格回退、方向键 / Home / End、多字符粘贴、字符过滤、
/// 受控值与禁用，以及组和分格的 ARIA。
/// </summary>
public class InputOtpTests
{
    static InputOtpTests() => ShadcnTheme.Register();

    static UIHarness Harness(Func<Node> render) =>
        new(render, appStyleSheets: true, width: 640, height: 480);

    static string? A(Element e, string name) => e.GetAttribute(name);

    static List<Element> Slots(UIHarness h) =>
        h.FindAll(e => A(e, "data-slot") == "input-otp-slot")
            .OrderBy(e => int.Parse(A(e, "data-index") ?? "0", System.Globalization.CultureInfo.InvariantCulture))
            .ToList();

    static Node Slot(int index) => new InputOTPSlot { Index = index };

    static Node Group(params int[] indexes)
    {
        var children = new NodeList();
        foreach (int index in indexes) children.Add(Slot(index));
        return new InputOTPGroup { Children = children };
    }

    static string SlotValue(Element slot) => ((InputElement)slot).Value;

    [Fact]
    public void InputOTP_group_is_labelled_and_typing_advances()
    {
        string? value = null;
        var h = Harness(() => new InputOTP
        {
            AriaLabel = "One-time code",
            MaxLength = 3,
            ValueChanged = v => value = v,
            Children = [Group(0, 1, 2)],
        });

        var group = h.FindAll(e => A(e, "data-slot") == "input-otp").Single();
        Assert.Equal(Role.Group, group.Role);
        Assert.Equal("One-time code", A(group, "aria-label"));

        var slots = Slots(h);
        Assert.Equal(3, slots.Count);
        Assert.All(slots, s => Assert.Equal(Role.TextBox, s.Role));
        Assert.All(slots, s => Assert.False(string.IsNullOrEmpty(A(s, "aria-label"))));

        h.Click(slots[0]);
        Assert.True(slots[0].IsFocused);

        h.TypeText("1");
        Assert.Equal("1", value);
        Assert.Equal("1", SlotValue(slots[0]));
        Assert.True(slots[1].IsFocused);

        h.TypeText("2");
        Assert.Equal("12", value);
        Assert.True(slots[2].IsFocused);

        // 最后一格保持焦点
        h.TypeText("3");
        Assert.Equal("123", value);
        Assert.True(slots[2].IsFocused);
        Assert.Equal("3", SlotValue(slots[2]));
    }

    [Fact]
    public void InputOTP_backspace_clears_and_steps_back()
    {
        string? value = null;
        var h = Harness(() => new InputOTP
        {
            MaxLength = 3,
            ValueChanged = v => value = v,
            Children = [Group(0, 1, 2)],
        });

        var slots = Slots(h);
        h.Click(slots[0]);
        h.TypeText("12");
        Assert.Equal("12", value);
        Assert.True(slots[2].IsFocused);

        // 当前格为空：退格删除上一格并聚焦上一格
        h.Key(PhysicalKey.Backspace, "Backspace");
        Assert.Equal("1", value);
        Assert.True(slots[1].IsFocused);

        h.Key(PhysicalKey.Backspace, "Backspace");
        Assert.Equal("", value);
        Assert.True(slots[0].IsFocused);

        // 在空格上继续退格不越界
        h.Key(PhysicalKey.Backspace, "Backspace");
        Assert.Equal("", value);
        Assert.True(slots[0].IsFocused);

        // Delete 清除当前格
        h.TypeText("5");
        h.Key(PhysicalKey.ArrowLeft, "ArrowLeft");
        h.Key(PhysicalKey.Delete, "Delete");
        Assert.Equal("", value);
    }

    [Fact]
    public void InputOTP_arrow_home_end_move_focus_only()
    {
        string? value = null;
        var h = Harness(() => new InputOTP
        {
            MaxLength = 4,
            ValueChanged = v => value = v,
            Children = [Group(0, 1, 2, 3)],
        });

        var slots = Slots(h);
        h.Click(slots[0]);
        Assert.True(slots[0].IsFocused);

        h.Key(PhysicalKey.ArrowRight, "ArrowRight");
        Assert.True(slots[1].IsFocused);
        h.Key(PhysicalKey.End, "End");
        Assert.True(slots[3].IsFocused);
        h.Key(PhysicalKey.ArrowLeft, "ArrowLeft");
        Assert.True(slots[2].IsFocused);
        h.Key(PhysicalKey.Home, "Home");
        Assert.True(slots[0].IsFocused);

        // 导航不改变值
        Assert.Null(value);
    }

    [Fact]
    public void InputOTP_paste_fills_multiple_slots_and_filters_characters()
    {
        string? value = null;
        var h = Harness(() => new InputOTP
        {
            MaxLength = 3,
            ValueChanged = v => value = v,
            Children = [Group(0, 1, 2)],
        });

        var slots = Slots(h);
        h.Click(slots[0]);
        h.TypeText("1a2b3");
        Assert.Equal("123", value);
        Assert.Equal("1", SlotValue(slots[0]));
        Assert.Equal("2", SlotValue(slots[1]));
        Assert.Equal("3", SlotValue(slots[2]));
        Assert.True(slots[2].IsFocused);
    }

    [Fact]
    public void InputOTP_pattern_accepts_ascii_letters()
    {
        string? value = null;
        var h = Harness(() => new InputOTP
        {
            Pattern = "[A-Za-z]",
            MaxLength = 3,
            ValueChanged = v => value = v,
            Children = [Group(0, 1, 2)],
        });

        var slots = Slots(h);
        h.Click(slots[0]);
        h.TypeText("a1B2c");
        Assert.Equal("aBc", value);
        Assert.Equal("a", SlotValue(slots[0]));
        Assert.Equal("B", SlotValue(slots[1]));
        Assert.Equal("c", SlotValue(slots[2]));
    }

    [Fact]
    public void InputOTP_controlled_value_reports_but_keeps_the_controlled_text()
    {
        string? reported = null;
        var h = Harness(() => new InputOTP
        {
            Value = "12",
            MaxLength = 3,
            ValueChanged = v => reported = v,
            Children = [Group(0, 1, 2)],
        });

        var slots = Slots(h);
        Assert.Equal("1", SlotValue(slots[0]));
        Assert.Equal("2", SlotValue(slots[1]));

        h.Click(slots[0]);
        h.TypeText("3");

        // 受控：组件报告新值但显示仍来自 Value
        Assert.Equal("32", reported);
        Assert.Equal("1", SlotValue(slots[0]));
        Assert.Equal("2", SlotValue(slots[1]));
    }

    [Fact]
    public void InputOTP_disabled_ignores_pointer_and_text_input()
    {
        string? value = null;
        var h = Harness(() => new InputOTP
        {
            Disabled = true,
            MaxLength = 3,
            ValueChanged = v => value = v,
            Children = [Group(0, 1, 2)],
        });

        var group = h.FindAll(e => A(e, "data-slot") == "input-otp").Single();
        Assert.Equal("true", A(group, "aria-disabled"));
        Assert.Equal("", A(group, "data-disabled"));

        var slots = Slots(h);
        Assert.True(slots[0].IsDisabled);
        h.Click(slots[0]);
        Assert.False(slots[0].IsFocused);
        h.TypeText("1");
        Assert.Null(value);
        Assert.Equal("", SlotValue(slots[0]));
    }

    [Fact]
    public void InputOTP_slots_expose_active_and_invalid_aria()
    {
        var h = Harness(() => new InputOTP
        {
            AriaLabel = "Verification code",
            Invalid = true,
            MaxLength = 4,
            Children = [Group(0, 1, 2, 3)],
        });

        var slots = Slots(h);
        Assert.Equal("true", A(slots[0], "aria-invalid"));
        Assert.Equal("false", A(slots[0], "data-active"));
        Assert.Contains("1", A(slots[0], "aria-label")!);
        Assert.Contains("4", A(slots[3], "aria-label")!);
        Assert.Equal("one-time-code", A(slots[0], "autocomplete"));

        h.Click(slots[0]);
        Assert.Equal("true", A(slots[0], "data-active"));
        Assert.Equal("false", A(slots[1], "data-active"));
    }

    [Fact]
    public void InputOTP_separator_has_separator_role_and_a_glyph()
    {
        var h = Harness(() => new InputOTP
        {
            MaxLength = 4,
            Children =
            [
                Group(0, 1),
                new InputOTPSeparator(),
                Group(2, 3),
            ],
        });

        var separator = h.FindAll(e => e.Role == Role.Separator).Single();
        Assert.Equal("input-otp-separator", A(separator, "data-slot"));
        Assert.NotEmpty(separator.Children);
        Assert.Equal(4, Slots(h).Count);
    }
}
