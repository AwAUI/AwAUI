using AwAUI.MacOS;
using AwAUI.Platform;

namespace AwAUI.Platform.UnitTests;

/// <summary>
/// macOS NSAccessibility 桥的纯逻辑：ARIA → AX 角色 / 子角色、Cocoa 屏幕矩形换算、
/// 属性值格式化、动作与通知名选择。AppKit 交互本身只能在 macOS 上运行，这里覆盖可移植部分。
/// </summary>
public class MacAccessibilityTests
{
    [Theory]
    [InlineData("button", "AXButton")]
    [InlineData("pushbutton", "AXButton")]
    [InlineData("push-button", "AXButton")]
    [InlineData("checkbox", "AXCheckBox")]
    [InlineData("radio", "AXRadioButton")]
    [InlineData("switch", "AXCheckBox")]
    [InlineData("text", "AXStaticText")]
    [InlineData("statictext", "AXStaticText")]
    [InlineData("textbox", "AXTextField")]
    [InlineData("searchbox", "AXTextField")]
    [InlineData("textarea", "AXTextArea")]
    [InlineData("link", "AXLink")]
    [InlineData("heading", "AXHeading")]
    [InlineData("list", "AXList")]
    [InlineData("listitem", "AXRow")]
    [InlineData("table", "AXTable")]
    [InlineData("row", "AXRow")]
    [InlineData("cell", "AXCell")]
    [InlineData("combobox", "AXComboBox")]
    [InlineData("popupbutton", "AXPopUpButton")]
    [InlineData("slider", "AXSlider")]
    [InlineData("progressbar", "AXProgressIndicator")]
    [InlineData("tablist", "AXTabGroup")]
    [InlineData("tab", "AXTab")]
    [InlineData("group", "AXGroup")]
    [InlineData("scrollarea", "AXScrollArea")]
    [InlineData("separator", "AXSeparator")]
    [InlineData("img", "AXImage")]
    [InlineData("image", "AXImage")]
    public void Maps_aria_roles_to_nsaccessibility_roles(string ariaRole, string expected)
    {
        Assert.Equal(expected, MacAccessibility.Role(ariaRole));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown-widget")]
    [InlineData("presentation")]
    public void Unknown_roles_fall_back_to_group(string? ariaRole)
    {
        Assert.Equal("AXGroup", MacAccessibility.Role(ariaRole));
    }

    [Fact]
    public void Role_names_are_trimmed_and_case_insensitive()
    {
        Assert.Equal("AXButton", MacAccessibility.Role("  Button  "));
        Assert.Equal("AXCheckBox", MacAccessibility.Role("CHECKBOX"));
    }

    [Theory]
    [InlineData("dialog", "AXDialog")]
    [InlineData("alertdialog", "AXDialog")]
    [InlineData("searchbox", "AXSearchField")]
    [InlineData("switch", "AXSwitch")]
    public void Maps_subroles(string ariaRole, string expected)
    {
        Assert.Equal(expected, MacAccessibility.Subrole(ariaRole));
    }

    [Theory]
    [InlineData("button")]
    [InlineData("textbox")]
    [InlineData(null)]
    public void Ordinary_roles_have_no_subrole(string? ariaRole)
    {
        Assert.Null(MacAccessibility.Subrole(ariaRole));
    }

    [Fact]
    public void Converts_client_rect_to_screen_cocoa_rect()
    {
        // 内容区左上为 (0,0)、y 向下；窗口 Cocoa 左下角 (100,200)，内容高 300。
        var result = MacAccessibility.ToScreenCocoa(new Rect(10, 20, 30, 40), 300, 100, 200);
        Assert.Equal(110, result.X, 3);
        Assert.Equal(440, result.Y, 3); // 200 + 300 - 20 - 40
        Assert.Equal(30, result.Width, 3);
        Assert.Equal(40, result.Height, 3);
    }

    [Fact]
    public void Converts_rect_at_the_content_origin()
    {
        var result = MacAccessibility.ToScreenCocoa(new Rect(0, 0, 50, 60), 400, -20, 10);
        Assert.Equal(-20, result.X, 3);
        Assert.Equal(350, result.Y, 3); // 10 + 400 - 0 - 60
        Assert.Equal(50, result.Width, 3);
        Assert.Equal(60, result.Height, 3);
    }

    [Fact]
    public void Converts_framework_y_to_cocoa_y()
    {
        Assert.Equal(280, MacAccessibility.ToCocoaY(20, 300, 0), 3);
        Assert.Equal(480, MacAccessibility.ToCocoaY(20, 300, 200), 3);
    }

    [Fact]
    public void Formats_checkable_values_from_states()
    {
        var checkedValue = MacAccessibility.FormatValue("checkbox", null, new[] { "checked" });
        Assert.Equal(MacAccessibilityValueKind.Boolean, checkedValue.Kind);
        Assert.True(checkedValue.Boolean);

        var uncheckedValue = MacAccessibility.FormatValue("radio", null, Array.Empty<string>());
        Assert.Equal(MacAccessibilityValueKind.Boolean, uncheckedValue.Kind);
        Assert.False(uncheckedValue.Boolean);

        var mixedValue = MacAccessibility.FormatValue("checkbox", null, new[] { "mixed" });
        Assert.Equal(MacAccessibilityValueKind.Text, mixedValue.Kind);
        Assert.Equal("mixed", mixedValue.Text);
    }

    [Fact]
    public void Formats_numeric_values_and_falls_back_to_text()
    {
        var number = MacAccessibility.FormatValue("slider", "42.5", Array.Empty<string>());
        Assert.Equal(MacAccessibilityValueKind.Number, number.Kind);
        Assert.Equal(42.5, number.Number, 3);

        var text = MacAccessibility.FormatValue("progressbar", "almost done", Array.Empty<string>());
        Assert.Equal(MacAccessibilityValueKind.Text, text.Kind);
        Assert.Equal("almost done", text.Text);

        var none = MacAccessibility.FormatValue("slider", null, Array.Empty<string>());
        Assert.Equal(MacAccessibilityValueKind.None, none.Kind);
    }

    [Fact]
    public void Formats_text_values_including_empty_strings()
    {
        var empty = MacAccessibility.FormatValue("textbox", "", Array.Empty<string>());
        Assert.Equal(MacAccessibilityValueKind.Text, empty.Kind);
        Assert.Equal("", empty.Text);

        var missing = MacAccessibility.FormatValue("textarea", null, Array.Empty<string>());
        Assert.Equal(MacAccessibilityValueKind.None, missing.Kind);
    }

    [Fact]
    public void Formats_other_roles_as_optional_text()
    {
        Assert.Equal(MacAccessibilityValueKind.None, MacAccessibility.FormatValue("button", null, null).Kind);
        Assert.Equal(MacAccessibilityValueKind.None, MacAccessibility.FormatValue("button", "", null).Kind);

        var text = MacAccessibility.FormatValue("link", "docs", null);
        Assert.Equal(MacAccessibilityValueKind.Text, text.Kind);
        Assert.Equal("docs", text.Text);
    }

    [Theory]
    [InlineData(AccessibilityActionKind.Invoke, "AXPress")]
    [InlineData(AccessibilityActionKind.Toggle, "AXPress")]
    [InlineData(AccessibilityActionKind.Increment, "AXIncrement")]
    [InlineData(AccessibilityActionKind.Decrement, "AXDecrement")]
    [InlineData(AccessibilityActionKind.ScrollIntoView, "AXScrollToVisible")]
    [InlineData(AccessibilityActionKind.Expand, "AXShowMenu")]
    [InlineData(AccessibilityActionKind.Collapse, "AXCancel")]
    public void Maps_action_kinds_to_ax_action_names(AccessibilityActionKind action, string expected)
    {
        Assert.Equal(expected, MacAccessibility.ActionName(action));
    }

    [Fact]
    public void Set_value_and_focus_have_no_ax_action_name()
    {
        Assert.Null(MacAccessibility.ActionName(AccessibilityActionKind.SetValue));
        Assert.Null(MacAccessibility.ActionName(AccessibilityActionKind.Focus));
    }

    [Theory]
    [InlineData("AXPress", AccessibilityActionKind.Invoke)]
    [InlineData("AXConfirm", AccessibilityActionKind.Invoke)]
    [InlineData("AXIncrement", AccessibilityActionKind.Increment)]
    [InlineData("AXDecrement", AccessibilityActionKind.Decrement)]
    [InlineData("AXScrollToVisible", AccessibilityActionKind.ScrollIntoView)]
    [InlineData("AXShowMenu", AccessibilityActionKind.Expand)]
    [InlineData("AXCancel", AccessibilityActionKind.Collapse)]
    public void Maps_ax_action_names_back_to_action_kinds(string name, AccessibilityActionKind expected)
    {
        Assert.Equal(expected, MacAccessibility.ActionKind(name));
    }

    [Fact]
    public void Unknown_ax_action_names_are_ignored()
    {
        Assert.Null(MacAccessibility.ActionKind("AXNonsense"));
        Assert.Null(MacAccessibility.ActionKind(null));
    }

    [Fact]
    public void Builds_action_name_list_for_button()
    {
        var names = MacAccessibility.ActionNamesFor(
            new[] { AccessibilityActionKind.Invoke, AccessibilityActionKind.Focus, AccessibilityActionKind.Toggle },
            "button");
        Assert.Contains("AXPress", names);
        Assert.Contains("AXConfirm", names);
        Assert.DoesNotContain("AXIncrement", names);
    }

    [Fact]
    public void Builds_action_name_list_for_slider_without_duplicates()
    {
        var names = MacAccessibility.ActionNamesFor(
            new[]
            {
                AccessibilityActionKind.Increment,
                AccessibilityActionKind.Decrement,
                AccessibilityActionKind.ScrollIntoView,
            },
            "slider");
        Assert.Equal(new[] { "AXIncrement", "AXDecrement", "AXScrollToVisible" }, names);
    }

    [Theory]
    [InlineData(0, "NSAccessibilityFocusedUIElementChangedNotification")]
    [InlineData(1, "NSAccessibilityValueChangedNotification")]
    [InlineData(2, "NSAccessibilitySelectedChildrenChangedNotification")]
    [InlineData(3, "NSAccessibilityLayoutChangedNotification")]
    [InlineData(4, "NSAccessibilityAnnouncementRequestedNotification")]
    public void Maps_notifications_to_appkit_constants(int notification, string expected)
    {
        // 内部枚举不能出现在公开测试方法的签名里（CS0051），按序号转换。
        Assert.Equal(expected, MacAccessibility.NotificationName((MacAccessibilityNotification)notification));
    }

    [Fact]
    public void Derives_orientation_from_bounds()
    {
        Assert.Equal("AXHorizontalOrientation", MacAccessibility.Orientation(new Rect(0, 0, 100, 20)));
        Assert.Equal("AXHorizontalOrientation", MacAccessibility.Orientation(new Rect(0, 0, 20, 20)));
        Assert.Equal("AXVerticalOrientation", MacAccessibility.Orientation(new Rect(0, 0, 20, 100)));
    }

    [Theory]
    [InlineData("slider", true)]
    [InlineData("spinbutton", true)]
    [InlineData("progressbar", true)]
    [InlineData("button", false)]
    [InlineData("textbox", false)]
    public void Range_is_available_only_for_numeric_roles(string role, bool expected)
    {
        bool hasRange = MacAccessibility.TryGetRange(role, out double min, out double max);
        Assert.Equal(expected, hasRange);
        if (expected)
        {
            Assert.Equal(0, min, 3);
            Assert.Equal(100, max, 3);
        }
    }

    [Theory]
    [InlineData("checkbox", true)]
    [InlineData("radio", true)]
    [InlineData("switch", true)]
    [InlineData("button", false)]
    public void Identifies_checkable_roles(string role, bool expected)
    {
        Assert.Equal(expected, MacAccessibility.IsCheckable(role));
    }

    [Theory]
    [InlineData("slider", true)]
    [InlineData("progressbar", true)]
    [InlineData("button", false)]
    public void Identifies_numeric_roles(string role, bool expected)
    {
        Assert.Equal(expected, MacAccessibility.IsNumeric(role));
    }

    [Theory]
    [InlineData("textbox", true)]
    [InlineData("textarea", true)]
    [InlineData("combobox", true)]
    [InlineData("slider", false)]
    public void Identifies_textual_roles(string role, bool expected)
    {
        Assert.Equal(expected, MacAccessibility.IsTextual(role));
    }

    [Fact]
    public void Matches_states_case_sensitively_like_snapshots()
    {
        var states = new[] { "disabled", "focused", "checked" };
        Assert.True(MacAccessibility.ContainsState(states, "focused"));
        Assert.False(MacAccessibility.ContainsState(states, "Focused"));
        Assert.False(MacAccessibility.ContainsState(states, "selected"));
        Assert.False(MacAccessibility.ContainsState(null, "focused"));
    }
}
