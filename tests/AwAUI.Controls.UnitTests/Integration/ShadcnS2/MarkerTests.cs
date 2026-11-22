using System.Linq;
using AwAUI.Controls.UnitTests.Integration;
using AwAUI.UI;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Shadcn.Integration.S2;

/// <summary>
/// S2 Marker：三种变体的 data-variant 与分隔线 / 底边类、图标装饰语义，以及内容槽位的文本。
/// </summary>
public class MarkerTests
{
    static MarkerTests() => ShadcnTheme.Register();

    static UIHarness Harness(System.Func<Node> render) =>
        new(render, appStyleSheets: true, width: 640, height: 480);

    static string? A(Element e, string name) => e.GetAttribute(name);

    static List<Element> Markers(UIHarness h) => h.FindAll(e => A(e, "data-slot") == "marker");

    [Fact]
    public void Marker_variants_render_their_content_and_data_attributes()
    {
        var h = Harness(() => new Div
        {
            Children =
            [
                new Marker
                {
                    Variant = MarkerVariant.Default,
                    Children =
                    [
                        new MarkerIcon { Children = [new UIText("i")] },
                        new MarkerContent { Children = [new UIText("Today")] },
                    ],
                },
                new Marker
                {
                    Variant = MarkerVariant.Separator,
                    Children = [new MarkerContent { Children = [new UIText("Yesterday")] }],
                },
                new Marker
                {
                    Variant = MarkerVariant.Border,
                    Children = [new MarkerContent { Children = [new UIText("Last week")] }],
                },
            ],
        });

        var markers = Markers(h);
        Assert.Equal(3, markers.Count);

        Assert.Equal("default", A(markers[0], "data-variant"));
        Assert.Equal("separator", A(markers[1], "data-variant"));
        Assert.Equal("border", A(markers[2], "data-variant"));

        Assert.Contains("group/marker", A(markers[0], "class")!);
        Assert.Contains("before:bg-border", A(markers[1], "class")!);
        Assert.Contains("after:bg-border", A(markers[1], "class")!);
        Assert.Contains("border-b", A(markers[2], "class")!);

        var contents = h.FindAll(e => A(e, "data-slot") == "marker-content");
        Assert.Equal(new[] { "Today", "Yesterday", "Last week" },
            contents.Select(e => e.TextContent.Trim()).ToArray());
        Assert.All(contents, e => Assert.Contains("wrap-break-word", A(e, "class")!));
    }

    [Fact]
    public void Marker_icon_is_decorative_for_assistive_technology()
    {
        var h = Harness(() => new Marker
        {
            Children =
            [
                new MarkerIcon { Children = [new UIText("clock")] },
                new MarkerContent { Children = [new UIText("12:30 PM")] },
            ],
        });

        var icon = h.FindAll(e => A(e, "data-slot") == "marker-icon").Single();
        Assert.Equal("true", A(icon, "aria-hidden"));
        Assert.Contains("size-4", A(icon, "class")!);
        Assert.Equal("marker", A(icon.Parent!, "data-slot"));
    }
}
