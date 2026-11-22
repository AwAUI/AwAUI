using AwAUI.Svg;

namespace AwAUI.Icons.Lucide.Integration;

/// <summary>
/// Verifies the Lucide documents vendored for shadcn parity: every icon parses into a non-empty 24x24 document with no
/// diagnostics, and the public properties hand back one cached instance.
/// </summary>
public class LucideIconsTests
{
    [Fact]
    public void ArrowDown_parses_as_cached_24x24_document() =>
        AssertIcon(LucideIcons.ArrowDown, () => LucideIcons.ArrowDown);

    [Fact]
    public void ArrowLeft_parses_as_cached_24x24_document() =>
        AssertIcon(LucideIcons.ArrowLeft, () => LucideIcons.ArrowLeft);

    [Fact]
    public void ArrowRight_parses_as_cached_24x24_document() =>
        AssertIcon(LucideIcons.ArrowRight, () => LucideIcons.ArrowRight);

    [Fact]
    public void Calendar_parses_as_cached_24x24_document() =>
        AssertIcon(LucideIcons.Calendar, () => LucideIcons.Calendar);

    [Fact]
    public void GripVertical_parses_as_cached_24x24_document() =>
        AssertIcon(LucideIcons.GripVertical, () => LucideIcons.GripVertical);

    static void AssertIcon(SvgDocument icon, Func<SvgDocument> access)
    {
        // The property is backed by a static cache, so every access returns the same parsed document.
        Assert.Same(icon, access());
        Assert.True(icon.HasViewBox);
        Assert.Equal(new Rect(0, 0, 24, 24), icon.ViewBox);
        Assert.Equal(new Size(24, 24), icon.IntrinsicSize);
        Assert.Empty(icon.Diagnostics);
        Assert.False(icon.IsEmpty);
        Assert.False(icon.ToDisplayList().IsEmpty);
    }
}
