using System;
using System.Collections.Generic;
using System.Linq;
using AwAUI.Controls.UnitTests.Integration;
using AwAUI.Platform;
using AwAUI.UI;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Shadcn.Integration.S2;

/// <summary>
/// S2 Trace acceptance: the official part slots, collapsible rows, line status attributes, and pointer / keyboard
/// toggling.
/// </summary>
public class TraceTests
{
    static TraceTests() => ShadcnTheme.Register();

    static UIHarness Harness(Func<Node> render) =>
        new(render, appStyleSheets: true, width: 640, height: 480);

    static string? A(Element e, string name) => e.GetAttribute(name);

    static List<Element> Slots(UIHarness h, string slot) =>
        h.FindAll(e => A(e, "data-slot") == slot);

    static Node View() => new Trace
    {
        Children =
        [
            new TraceCollapsible
            {
                DefaultOpen = true,
                Children =
                [
                    new TraceCollapsibleTrigger
                    {
                        Children =
                        [
                            new TraceLine
                            {
                                Status = TraceStatusKind.Running,
                                Children =
                                [
                                    new TraceIcon { Children = [new UIText("i")] },
                                    new TraceTitle { Children = [new UIText("Thinking")] },
                                    new TraceDescription { Children = [new UIText("1.2s")] },
                                ],
                            },
                        ],
                    },
                    new TraceCollapsibleContent { Children = [new UIText("Details")] },
                ],
            },
            new TraceCollapsible
            {
                Children =
                [
                    new TraceCollapsibleTrigger
                    {
                        Children =
                        [
                            new TraceLine
                            {
                                Status = TraceStatusKind.Error,
                                Children = [new TraceTitle { Children = [new UIText("Tool call")] }],
                            },
                        ],
                    },
                    new TraceCollapsibleContent { Children = [new UIText("Failed")] },
                ],
            },
        ],
    };

    [Fact]
    public void Trace_items_expand_and_collapse_with_click()
    {
        var h = Harness(View);

        Assert.Single(Slots(h, "trace"));
        Assert.Equal(2, Slots(h, "trace-collapsible").Count);

        var lines = Slots(h, "trace-line");
        Assert.Equal(2, lines.Count);
        Assert.Equal("running", A(lines[0], "data-status"));
        Assert.Equal("error", A(lines[1], "data-status"));

        Assert.Equal("true", A(Slots(h, "trace-collapsible-trigger")[0], "aria-expanded"));
        Assert.Equal("false", A(Slots(h, "trace-collapsible-trigger")[1], "aria-expanded"));
        Assert.Single(Slots(h, "trace-collapsible-content"));

        h.Click(Slots(h, "trace-collapsible-trigger")[1]);
        Assert.Equal("true", A(Slots(h, "trace-collapsible-trigger")[1], "aria-expanded"));
        Assert.Equal(2, Slots(h, "trace-collapsible-content").Count);

        h.Click(Slots(h, "trace-collapsible-trigger")[0]);
        Assert.Equal("false", A(Slots(h, "trace-collapsible-trigger")[0], "aria-expanded"));
        Assert.Single(Slots(h, "trace-collapsible-content"));
    }

    [Fact]
    public void Trace_trigger_toggles_with_the_keyboard()
    {
        var h = Harness(View);

        var trigger = Slots(h, "trace-collapsible-trigger")[1];
        trigger.Focus();
        h.Frame();
        Assert.True(trigger.IsFocused);

        h.Key(PhysicalKey.Enter, "Enter");
        Assert.Equal("true", A(Slots(h, "trace-collapsible-trigger")[1], "aria-expanded"));

        h.Key(PhysicalKey.Space, " ");
        Assert.Equal("false", A(Slots(h, "trace-collapsible-trigger")[1], "aria-expanded"));
    }

    [Fact]
    public void Trace_status_parts_render_the_official_slots()
    {
        var h = Harness(View);

        Assert.Equal("Thinking", Slots(h, "trace-title")[0].TextContent.Trim());
        Assert.Equal("1.2s", Slots(h, "trace-description")[0].TextContent.Trim());
        Assert.Equal(3, Slots(h, "trace-icon").Count); // one line icon plus the two trigger chevrons
        Assert.Contains("group-aria-expanded/trace-collapsible-trigger:rotate-90",
            A(Slots(h, "trace-icon").Last(), "class")!);

        var content = Slots(h, "trace-collapsible-content").Single();
        Assert.Equal("open", A(content, "data-state"));
        Assert.Contains("Details", Slots(h, "trace-collapsible-content-inner").Single().TextContent);
    }

    [Fact]
    public void Trace_line_without_a_status_has_no_data_status()
    {
        var h = Harness(() => new Trace
        {
            Children =
            [
                new TraceLine { Status = TraceStatusKind.Complete, Children = [new TraceTitle { Children = [new UIText("Done")] }] },
            ],
        });

        Assert.Null(A(Slots(h, "trace-line").Single(), "data-status"));
    }

    [Fact]
    public void Trace_legacy_parts_use_the_same_official_slots()
    {
        var h = Harness(() => new Trace
        {
            Children =
            [
                new TraceItem
                {
                    DefaultOpen = true,
                    Children =
                    [
                        new TraceTrigger { Children = [new TraceLabel { Children = [new UIText("Legacy")] }] },
                        new TraceContent { Children = [new UIText("Body")] },
                    ],
                },
            ],
        });

        Assert.Single(Slots(h, "trace-collapsible"));
        Assert.Single(Slots(h, "trace-collapsible-trigger"));
        Assert.Single(Slots(h, "trace-title"));
        Assert.Single(Slots(h, "trace-collapsible-content"));
        Assert.Single(Slots(h, "trace-collapsible-content-inner"));
    }
}
