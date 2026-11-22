using System;
using System.Linq;
using AwAUI.Controls.Primitives;
using AwAUI.Controls.UnitTests.Integration;
using AwAUI.UI;
using Xunit;

namespace AwAUI.Controls.UnitTests.Feedback;

/// <summary>Value clamping and formatting for the progress controls.</summary>
public class ProgressMathTests
{
    [Fact]
    public void Inverted_and_non_finite_ranges_are_normalized()
    {
        ProgressMath.NormalizeRange(100, 0, out double min, out double max);
        Assert.Equal(0, min);
        Assert.Equal(100, max);

        ProgressMath.NormalizeRange(double.NaN, double.PositiveInfinity, out min, out max);
        Assert.Equal(0, min);
        Assert.Equal(100, max);
    }

    [Fact]
    public void Values_are_clamped_and_fractions_are_bounded()
    {
        Assert.Equal(0, ProgressMath.ClampValue(-5, 0, 100));
        Assert.Equal(100, ProgressMath.ClampValue(150, 0, 100));
        Assert.Equal(0, ProgressMath.ClampValue(double.NaN, 0, 100));
        Assert.Equal(0.25, ProgressMath.Fraction(25, 0, 100));
        Assert.Equal(0, ProgressMath.Fraction(5, 10, 10)); // zero-width range
    }

    [Fact]
    public void Text_formats_the_completed_percentage()
    {
        Assert.Equal("50%", ProgressMath.FormatText(null, 50, 0, 100));
        Assert.Equal("50", ProgressMath.FormatText("{0:0}", 50, 0, 100));
        Assert.Equal("100", ProgressMath.FormatText("{0:0}", 150, 0, 100));
        Assert.Equal("50%", ProgressMath.FormatText("{bad", 50, 0, 100));
    }
}

/// <summary>ProgressBar and ProgressRing ARIA semantics and value clamping.</summary>
public class ProgressControlsTests
{
    static Element Progress(UIHarness h) => h.FindAll(e => e.Role == Role.ProgressBar).Single();

    [Fact]
    public void Progress_bar_exposes_the_clamped_value()
    {
        var h = new UIHarness(() => new ProgressBar { Value = 50, ShowProgressText = true });
        var progress = Progress(h);

        Assert.Equal("0", progress.GetAttribute("aria-valuemin"));
        Assert.Equal("100", progress.GetAttribute("aria-valuemax"));
        Assert.Equal("50", progress.GetAttribute("aria-valuenow"));
        Assert.Equal("50%", h.FindAll(e => e.GetAttribute("part") == "progress-text").Single().TextContent);
    }

    [Theory]
    [InlineData(-10, "0")]
    [InlineData(150, "100")]
    public void Progress_bar_clamps_out_of_range_values(double value, string expected)
    {
        var h = new UIHarness(() => new ProgressBar { Value = value, Minimum = 0, Maximum = 100 });
        Assert.Equal(expected, Progress(h).GetAttribute("aria-valuenow"));
    }

    [Fact]
    public void Indeterminate_progress_omits_the_value_and_sets_busy()
    {
        var h = new UIHarness(() => new ProgressBar { Value = 40, IsIndeterminate = true, ShowProgressText = true });
        var progress = Progress(h);

        Assert.Null(progress.GetAttribute("aria-valuenow"));
        Assert.Equal("true", progress.GetAttribute("aria-busy"));
        Assert.NotNull(progress.GetAttribute("data-indeterminate"));
        Assert.Empty(h.FindAll(e => e.GetAttribute("part") == "progress-text"));
    }

    [Fact]
    public void Progress_text_honours_a_custom_format()
    {
        var h = new UIHarness(() => new ProgressBar { Value = 25, ShowProgressText = true, ProgressTextFormat = "{0:0}" });
        Assert.Equal("25", h.FindAll(e => e.GetAttribute("part") == "progress-text").Single().TextContent);
    }

    [Fact]
    public void Progress_ring_renders_svg_and_shares_the_progress_api()
    {
        var h = new UIHarness(() => new ProgressRing { Value = 25, ShowProgressText = true });
        var ring = Progress(h);

        Assert.Equal("25", ring.GetAttribute("aria-valuenow"));
        Assert.NotEmpty(h.FindAll(e => e.Tag == "svg"));
        Assert.Equal("25%", h.FindAll(e => e.GetAttribute("part") == "progress-text").Single().TextContent);
    }

    [Fact]
    public void Indeterminate_progress_ring_omits_the_value()
    {
        var h = new UIHarness(() => new ProgressRing { IsIndeterminate = true, Diameter = 48, Thickness = 6 });
        var ring = Progress(h);

        Assert.Null(ring.GetAttribute("aria-valuenow"));
        Assert.Equal("true", ring.GetAttribute("aria-busy"));
        Assert.NotEmpty(h.FindAll(e => e.Tag == "svg"));
    }

    [Fact]
    public void Progress_ring_renders_with_the_software_backend()
    {
        var h = new UIHarness(() => new ProgressRing { Value = 60, Diameter = 48 });
        var bitmap = h.Render();

        Assert.NotNull(bitmap);
    }
}

/// <summary>InfoBar severity mapping, close behavior, and separator orientation.</summary>
public class FeedbackElementsTests
{
    [Fact]
    public void Error_info_bar_uses_the_alert_role_and_assertive_live_region()
    {
        var h = new UIHarness(() => new InfoBar
        {
            Severity = InfoBarSeverity.Error,
            Title = "Upload failed",
            Message = "The file could not be uploaded.",
        });

        var bar = h.FindAll(e => e.Role == Role.Alert).Single();
        Assert.Equal("assertive", bar.GetAttribute("aria-live"));
        Assert.Equal("error", bar.GetAttribute("data-severity"));
        Assert.Equal("true", bar.GetAttribute("aria-atomic"));
        Assert.Equal("Upload failed", h.FindAll(e => e.GetAttribute("part") == "title").Single().TextContent);
        Assert.Equal("The file could not be uploaded.", h.FindAll(e => e.GetAttribute("part") == "message").Single().TextContent);
    }

    [Theory]
    [InlineData(InfoBarSeverity.Informational, "informational")]
    [InlineData(InfoBarSeverity.Success, "success")]
    [InlineData(InfoBarSeverity.Warning, "warning")]
    public void Non_error_severities_use_status_and_polite(InfoBarSeverity severity, string name)
    {
        var h = new UIHarness(() => new InfoBar { Severity = severity, Message = "Notice" });

        var bar = h.FindAll(e => e.Role == Role.Status).Single();
        Assert.Equal("polite", bar.GetAttribute("aria-live"));
        Assert.Equal(name, bar.GetAttribute("data-severity"));
    }

    [Fact]
    public void Close_button_reports_the_request_and_hides_the_bar()
    {
        bool? reported = null;
        bool clicked = false;
        var h = new UIHarness(() => new InfoBar
        {
            Message = "Saved",
            CloseButtonClick = _ => clicked = true,
            IsOpenChanged = open => reported = open,
        });

        h.Click(h.FindAll(e => e.GetAttribute("part") == "close").Single());

        Assert.True(clicked);
        Assert.False(reported);
        Assert.Empty(h.FindAll(e => e.Role == Role.Status));
    }

    [Fact]
    public void Non_closable_bar_has_no_close_button()
    {
        var h = new UIHarness(() => new InfoBar { Message = "Pinned", IsClosable = false });
        Assert.Empty(h.FindAll(e => e.GetAttribute("part") == "close"));
    }

    [Fact]
    public void Closed_bar_renders_nothing()
    {
        var h = new UIHarness(() => new InfoBar { Message = "Hidden", IsOpen = false });
        Assert.Empty(h.FindAll(e => e.Role == Role.Status || e.Role == Role.Alert));
    }

    [Fact]
    public void Action_button_is_placed_in_the_action_part()
    {
        var h = new UIHarness(() => new InfoBar
        {
            Message = "Update available",
            ActionButton = new Button { Children = new NodeList { "Update" } },
        });

        var action = h.FindAll(e => e.GetAttribute("part") == "action").Single();
        Assert.Equal("Update", action.TextContent);
    }

    [Theory]
    [InlineData(Orientation.Horizontal, "horizontal")]
    [InlineData(Orientation.Vertical, "vertical")]
    public void Separator_exposes_its_orientation(Orientation orientation, string expected)
    {
        var h = new UIHarness(() => new Separator { Orientation = orientation });
        var separator = h.FindAll(e => e.Role == Role.Separator).Single();

        Assert.Equal(expected, separator.GetAttribute("aria-orientation"));
        Assert.Equal(expected, separator.GetAttribute("data-orientation"));
    }
}
