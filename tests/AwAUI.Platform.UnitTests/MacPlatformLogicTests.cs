using System;
using AwAUI.MacOS;

namespace AwAUI.Platform.UnitTests;

/// <summary>
/// macOS 后端里与 AppKit 无关的纯逻辑（屏幕坐标换算、HTML 纯文本回退、Launcher 校验）。
/// AppKit 本身只能在 macOS 主线程上验证，见 design/04 的说明。
/// </summary>
public class MacPlatformLogicTests
{
    [Fact]
    public void Position_flips_between_cocoa_and_top_left_coordinates()
    {
        // 主显示器高 1440：Cocoa 的 (10, 200) + 高 400 → 框架 y = 1440 - (200 + 400) = 840
        Assert.Equal(new Point(10, 840), MacScreenMath.PositionFromCocoa(10, 200, 400, 1440));

        MacScreenMath.ToCocoaOrigin(new Point(10, 840), 400, 1440, out double x, out double y);
        Assert.Equal(10, x);
        Assert.Equal(200, y);
    }

    [Fact]
    public void Screen_rect_above_the_primary_gets_a_negative_top()
    {
        // 位于主显示器上方 900 点的副屏：Cocoa origin.y = 1440，高 900 → 框架 top = 1440 - (1440 + 900) = -900
        var rect = MacScreenMath.FromCocoa(0, 1440, 1920, 900, 1440);
        Assert.Equal(new Rect(0, -900, 1920, 900), rect);
    }

    [Fact]
    public void Html_fallback_strips_tags_and_decodes_entities()
    {
        Assert.Equal("Hello & world", MacHtml.ToPlainText("<b>Hello</b> &amp; <i>world</i>"));
        Assert.Equal("a b", MacHtml.ToPlainText("<p>a</p><p>b</p>"));
        Assert.Equal("visible", MacHtml.ToPlainText("<!-- hidden --> <span>visible</span>"));
        Assert.Equal("café", MacHtml.ToPlainText("caf&#233;"));
        Assert.Equal("", MacHtml.ToPlainText(null!));
    }

    [Theory]
    [InlineData("https://example.com/path", true)]
    [InlineData("http://localhost:8080/", true)]
    [InlineData("mailto:dev@example.com", true)]
    [InlineData("file:///tmp/notes.txt", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("ftp://example.com/file", false)]
    [InlineData("data:text/plain,hello", false)]
    public void Launcher_allows_only_whitelisted_schemes(string uri, bool expected) =>
        Assert.Equal(expected, MacLauncherValidation.CanLaunch(new Uri(uri)));

    [Fact]
    public void Launcher_rejects_null_and_relative_uris()
    {
        Assert.False(MacLauncherValidation.CanLaunch(null));
        Assert.False(MacLauncherValidation.CanLaunch(new Uri("/tmp/x", UriKind.Relative)));
    }

    [Theory]
    [InlineData("/tmp/notes.txt", true)]
    [InlineData("/Users/example/file", true)]
    [InlineData("relative/path", false)]
    [InlineData("", false)]
    [InlineData("/tmp/bad\nname", false)]
    public void Launcher_paths_must_be_absolute_and_free_of_control_characters(string path, bool expected) =>
        Assert.Equal(expected, MacLauncherValidation.IsSafePath(path));
}
