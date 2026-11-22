using System;
using System.Collections.Generic;
using System.Linq;
using AwAUI.Controls.UnitTests.Integration;
using AwAUI.Css.Values;
using AwAUI.Imaging;
using AwAUI.UI;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Shadcn.Integration.S2;

/// <summary>
/// S2 ScrollArea acceptance: the viewport clips its content on the first painted frame (no stray rows outside the
/// box), the orientation selects the scroll axis, and a caller-supplied controller is reused.
/// </summary>
public class ScrollAreaTests
{
    static ScrollAreaTests() => ShadcnTheme.Register();

    static UIHarness Harness(Func<Node> render) =>
        new(render, appStyleSheets: true, width: 300, height: 300);

    static string? A(Element e, string name) => e.GetAttribute(name);

    static Div Rows(int count)
    {
        var list = new Div();
        for (int i = 0; i < count; i++)
            list.Children.Add(new Div { Children = [new UIText("Row " + i)] });
        return list;
    }

    static ScrollArea Area(ScrollAreaOrientation orientation, int rows, ScrollController? controller = null) => new()
    {
        Orientation = orientation,
        ScrollController = controller,
        Rest = new AttributeDictionary { ["style"] = "height: 80px; width: 200px; border: 1px solid black" },
        Children = [new Div { Style = "padding: 4px", Children = [Rows(rows)] }],
    };

    [Fact]
    public void ScrollArea_clips_overflow_on_the_first_frame()
    {
        var h = Harness(() => Area(ScrollAreaOrientation.Vertical, 40));

        using var bitmap = h.Render(Color.White);

        // Nothing may paint below the 80px viewport: the rows beyond it must be clipped, not spilled.
        for (int y = 100; y < 140; y++)
            for (int x = 8; x < 190; x++)
            {
                var c = bitmap.GetPixel(x, y);
                Assert.True(c.R > 0.9f && c.G > 0.9f && c.B > 0.9f,
                    $"content painted outside the scroll viewport at ({x},{y}): {c}");
            }
    }

    [Fact]
    public void ScrollArea_viewport_scrolls_only_on_the_requested_axis()
    {
        var vertical = Harness(() => Area(ScrollAreaOrientation.Vertical, 40));
        var vp = vertical.FindAll(e => A(e, "data-slot") == "scroll-area-viewport").Single();
        Assert.Equal(0, vp.TabIndex);
        Assert.Equal(CssOverflow.Hidden, vp.ComputedStyle?.OverflowX);
        Assert.Equal(CssOverflow.Auto, vp.ComputedStyle?.OverflowY);

        var horizontal = Harness(() => Area(ScrollAreaOrientation.Horizontal, 40));
        var hp = horizontal.FindAll(e => A(e, "data-slot") == "scroll-area-viewport").Single();
        Assert.Equal(CssOverflow.Auto, hp.ComputedStyle?.OverflowX);
        Assert.Equal(CssOverflow.Hidden, hp.ComputedStyle?.OverflowY);
    }

    [Fact]
    public void ScrollArea_reuses_a_caller_supplied_controller()
    {
        var supplied = new ScrollController();
        var h = Harness(() => Area(ScrollAreaOrientation.Vertical, 40, supplied));

        var viewport = h.FindAll(e => A(e, "data-slot") == "scroll-area-viewport").Single();
        Assert.Same(supplied, viewport.ScrollController);
    }
}
