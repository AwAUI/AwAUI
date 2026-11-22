using System;
using AwAUI.Controls.Primitives;
using AwAUI.Controls.UnitTests.Integration;
using AwAUI.Layout;
using AwAUI.Platform;
using AwAUI.UI;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Shadcn.Integration.S2;

/// <summary>Resizable 的尺寸、拖拽、键盘、双击复位与 RTL。</summary>
public class ResizableTests
{
    static ResizableTests() => ShadcnTheme.Register();

    static UIHarness Harness(Func<Node> render) =>
        new(render, appStyleSheets: true, width: 640, height: 480);

    static string? A(Element e, string name) => e.GetAttribute(name);

    static Element Slot(UIHarness h, string slot) =>
        h.FindAll(e => A(e, "data-slot") == slot).First();

    static List<Element> Slots(UIHarness h, string slot) =>
        h.FindAll(e => A(e, "data-slot") == slot);

    static Node Group(Action<double[]> report, Orientation orientation = Orientation.Horizontal, Direction? dir = null) =>
        new ResizablePanelGroup
        {
            Orientation = orientation,
            Dir = dir,
            SizesChanged = sizes => report(sizes.ToArray()),
            Children =
            [
                new ResizablePanel { DefaultSize = 40, MinSize = 20, Children = [new UIText("One")] },
                new ResizableHandle { WithHandle = true },
                new ResizablePanel { DefaultSize = 60, MinSize = 20, Children = [new UIText("Two")] },
            ],
        };

    [Fact]
    public void Resizable_panels_expose_default_sizes_and_a_separator()
    {
        var h = Harness(() => Group(_ => { }));

        var group = Slot(h, "resizable-panel-group");
        Assert.Equal("horizontal", A(group, "data-orientation"));

        var panels = Slots(h, "resizable-panel");
        Assert.Equal(2, panels.Count);
        Assert.Equal("40", A(panels[0], "data-size"));
        Assert.Equal("60", A(panels[1], "data-size"));

        var handle = Slot(h, "resizable-handle");
        Assert.Equal(Role.Separator, handle.Role);
        Assert.Equal("vertical", A(handle, "aria-orientation"));
        Assert.Equal("40", A(handle, "aria-valuenow"));
        Assert.Equal(0, handle.TabIndex);
        Assert.NotNull(Slot(h, "resizable-handle-icon"));
    }

    [Fact]
    public void Resizable_drag_reports_new_sizes_and_respects_minimums()
    {
        double[]? reported = null;
        var h = Harness(() => Group(sizes => reported = sizes));
        var handle = Slot(h, "resizable-handle");
        var center = handle.GetBoundingClientRect().Center;

        h.PointerDown(center);
        h.PointerMove(new Point(center.X + 64, center.Y));
        h.PointerUp(new Point(center.X + 64, center.Y));

        Assert.NotNull(reported);
        var sizes = reported!;
        Assert.Equal(2, sizes.Length);
        Assert.True(sizes[0] > 45, $"left panel did not grow: {sizes[0]}");
        Assert.True(sizes[1] < 55, $"right panel did not shrink: {sizes[1]}");
        Assert.NotEqual("40", A(Slot(h, "resizable-handle"), "aria-valuenow"));
    }

    [Fact]
    public void Resizable_keyboard_arrows_change_the_boundary_and_double_click_resets()
    {
        double[]? reported = null;
        var h = Harness(() => Group(sizes => reported = sizes));

        h.Tab();
        var handle = Slot(h, "resizable-handle");
        Assert.True(handle.IsFocused);

        h.Key(PhysicalKey.ArrowRight, "ArrowRight");
        Assert.Equal("45", A(Slot(h, "resizable-handle"), "aria-valuenow"));

        h.Key(PhysicalKey.ArrowRight, "ArrowRight", KeyModifiers.Shift);
        Assert.Equal("55", A(Slot(h, "resizable-handle"), "aria-valuenow"));

        // Two clicks within the double-click window restore the default sizes.
        var center = Slot(h, "resizable-handle").GetBoundingClientRect().Center;
        h.ClickAt(center);
        h.ClickAt(center);
        Assert.Equal("40", A(Slot(h, "resizable-handle"), "aria-valuenow"));
        Assert.NotNull(reported);
        Assert.Equal(40d, reported![0]);
    }

    [Fact]
    public void Resizable_vertical_group_uses_horizontal_separator_and_arrow_down()
    {
        double[]? reported = null;
        var h = Harness(() => Group(sizes => reported = sizes, Orientation.Vertical));

        var handle = Slot(h, "resizable-handle");
        Assert.Equal("horizontal", A(handle, "aria-orientation"));

        h.Tab();
        h.Key(PhysicalKey.ArrowDown, "ArrowDown");
        Assert.NotNull(reported);
        Assert.Equal(45d, reported![0]);
    }

    [Fact]
    public void Resizable_rtl_drag_reverses_the_logical_direction()
    {
        double[]? reported = null;
        var h = Harness(() => Group(sizes => reported = sizes, dir: Direction.Rtl));
        var group = Slot(h, "resizable-panel-group");
        Assert.Equal("rtl", A(group, "dir"));

        var handle = Slot(h, "resizable-handle");
        var center = handle.GetBoundingClientRect().Center;
        h.PointerDown(center);
        h.PointerMove(new Point(center.X + 64, center.Y));
        h.PointerUp(new Point(center.X + 64, center.Y));

        Assert.NotNull(reported);
        Assert.True(reported![0] < 40, $"RTL drag should shrink the first panel: {reported[0]}");
    }
}
