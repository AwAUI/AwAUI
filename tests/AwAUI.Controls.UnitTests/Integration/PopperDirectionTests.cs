using AwAUI.Controls.Primitives;
using AwAUI.UI;
using PopperPrimitive = AwAUI.Controls.Primitives.Popper;

namespace AwAUI.Controls.UnitTests.Integration;

/// <summary>Popper 读取浮层元素的计算方向：RTL 下上 / 下边的 <c>start</c> 对齐锚点右缘。</summary>
public class PopperDirectionTests
{
    const string Css = """
        #anchor { position: absolute; left: 100px; top: 100px; width: 40px; height: 20px; }
        #popup { width: 60px; height: 30px; }
        """;

    static UIHarness Harness(string dir) => new(() => new Div
    {
        Attributes = { ["dir"] = dir },
        Children =
        [
            new PopperPrimitive
            {
                Children =
                [
                    new PopperAnchor { AsChild = true, Children = [new Div { Id = "anchor" }] },
                    new PopperContent
                    {
                        Side = Side.Bottom,
                        Align = PopperAlign.Start,
                        Children = [new Div { Id = "popup" }],
                    },
                ],
            },
        ],
    }, Css);

    [Fact]
    public void Start_alignment_follows_the_computed_direction()
    {
        var ltr = Harness("ltr");
        ltr.Frame();
        var rtl = Harness("rtl");
        rtl.Frame();
        var ltrAnchor = ltr.Box("anchor");
        var rtlAnchor = rtl.Box("anchor");
        var ltrPopup = ltr.Box("popup");
        var rtlPopup = rtl.Box("popup");

        Assert.Equal(ltrAnchor.Left, ltrPopup.Left, 1);
        Assert.Equal(rtlAnchor.Right, rtlPopup.Right, 1);
        Assert.Equal(ltrAnchor.Left - ltrPopup.Left, rtlPopup.Right - rtlAnchor.Right, 1);
        // 上 / 下对齐不受影响：两边的浮层都在锚点下方
        Assert.Equal(ltrAnchor.Bottom, ltrPopup.Top, 1);
        Assert.Equal(rtlAnchor.Bottom, rtlPopup.Top, 1);
    }
}
