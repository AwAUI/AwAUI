using AwAUI.Controls.Primitives;
using AwAUI.Imaging;
using AwAUI.Text;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Controls.UnitTests.Integration;

/// <summary>
/// 一张总览图：常规内容之上打开的模态对话框（遮罩、标题、描述、按钮），对话框中再打开一个带箭头的 Popover（真实字体 Noto Sans，2x）。
/// 断言结构与几何；设置环境变量 <c>AWAUI_PRIMITIVES_SHOT=路径</c> 时把 PNG 写到该路径供人工查看。
/// </summary>
public class OverviewScreenshotTests
{
    const string Css = """
        root { font-family: sans-serif; font-size: 14px; color: #18181b; background-color: #f4f4f5; }
        .page { padding: 32px; }
        .card { width: 420px; padding: 24px; border: 1px solid #e4e4e7; border-radius: 12px; background-color: #ffffff; box-shadow: 0 1px 3px rgba(0, 0, 0, 0.1); }
        .card-title { font-size: 20px; font-weight: 600; margin-bottom: 8px; }
        .muted { color: #71717a; }
        .row { display: flex; gap: 12px; margin-top: 20px; }
        .btn { padding: 8px 16px; border-radius: 8px; border: 1px solid #e4e4e7; background-color: #ffffff; color: #18181b; font-size: 14px; }
        .btn-primary { background-color: #18181b; color: #fafafa; border-color: #18181b; }
        .overlay { position: fixed; left: 0; top: 0; right: 0; bottom: 0; background-color: rgba(0, 0, 0, 0.5); z-index: 50; }
        .dialog { position: fixed; left: 190px; top: 120px; width: 420px; padding: 24px; border-radius: 12px; border: 1px solid #e4e4e7;
                  background-color: #ffffff; z-index: 50; box-shadow: 0 10px 30px rgba(0, 0, 0, 0.25); }
        .dialog-title { font-size: 18px; font-weight: 600; }
        .dialog-desc { color: #71717a; margin-top: 6px; }
        .field { margin-top: 18px; padding: 8px 12px; border: 1px solid #e4e4e7; border-radius: 8px; color: #71717a; }
        .footer { display: flex; justify-content: flex-end; gap: 8px; margin-top: 22px; }
        .popover { width: 220px; padding: 14px; border-radius: 10px; border: 1px solid #e4e4e7; background-color: #ffffff; z-index: 50;
                   box-shadow: 0 6px 20px rgba(0, 0, 0, 0.18); }
        .popover-title { font-weight: 600; }
        .arrow { background-color: #18181b; }
        """;

    static TextEngine RealText()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var font = Path.Combine(dir.FullName, "tests", "TestFiles", "Fonts", "NotoSans-Regular.ttf");
            if (!File.Exists(font)) continue;
            var fonts = new FontCollection();
            fonts.RegisterFile(font);
            foreach (var generic in new[] { "sans-serif", "system-ui", "serif", "monospace" }) fonts.SetGenericFamily(generic, "Noto Sans");
            return new DefaultTextEngine(fonts);
        }
        throw new DirectoryNotFoundException("tests/TestFiles/Fonts not found");
    }

    static Node Scene() => new Div
    {
        Class = "page",
        Children =
        [
            new Div
            {
                Class = "card",
                Children =
                {
                    new UIText { Class = "card-title", Content = "Account" },
                    new UIText { Class = "muted", Content = "Manage your profile, notifications and security settings." },
                    new Div
                    {
                        Class = "row",
                        Children =
                        {
                            new Dialog.Root
                            {
                                DefaultOpen = true,
                                Children =
                                [
                                    new Dialog.Trigger { Class = "btn btn-primary", Children = ["Edit profile"] },
                                    new Dialog.Portal
                                    {
                                        Children =
                                        [
                                            new Dialog.Overlay { Class = "overlay" },
                                            new Dialog.Content
                                            {
                                                Class = "dialog",
                                                Children =
                                                [
                                                    new Dialog.Title { Class = "dialog-title", Children = ["Edit profile"] },
                                                    new Dialog.Description { Class = "dialog-desc", Children = ["Make changes to your profile here. Click save when you are done."] },
                                                    new Div { Class = "field", Children = { new UIText { Content = "Ada Lovelace" } } },
                                                    new Div
                                                    {
                                                        Class = "footer",
                                                        Children =
                                                        {
                                                            new Popover.Root
                                                            {
                                                                DefaultOpen = true,
                                                                Children =
                                                                [
                                                                    new Popover.Trigger { Class = "btn", Attributes = new() { ["id"] = "more" }, Children = ["More options"] },
                                                                    new Popover.Portal
                                                                    {
                                                                        Children =
                                                                        [
                                                                            new Popover.Content
                                                                            {
                                                                                Class = "popover",
                                                                                SideOffset = 4,
                                                                                CollisionPadding = 8,
                                                                                Children =
                                                                                [
                                                                                    new UIText { Class = "popover-title", Content = "Dimensions" },
                                                                                    new UIText { Class = "muted", Content = "Set the dimensions for the layer." },
                                                                                    new Popover.Arrow { Class = "arrow", Width = 12, Height = 6 },
                                                                                ],
                                                                            },
                                                                        ],
                                                                    },
                                                                ],
                                                            },
                                                            new Dialog.Close { Class = "btn btn-primary", Children = ["Save changes"] },
                                                        },
                                                    },
                                                ],
                                            },
                                        ],
                                    },
                                ],
                            },
                            new UIButton { Class = "btn", Children = { "Sign out" } },
                        },
                    },
                },
            },
        ],
    };

    [Fact]
    public void Open_dialog_with_open_popover_over_content()
    {
        var h = new UIHarness(Scene, Css, 800, 520, scale: 2, text: RealText());
        var dialog = h.FindAll(e => e.HasClass("dialog")).Single();
        var popover = h.FindAll(e => e.HasClass("popover")).Single();
        var more = h.ById("more");
        Assert.Equal(2, h.Root.Overlay.Layers.Count);
        Assert.True(h.Root.Overlay.Layers[0].IsInclusiveAncestorOf(dialog));
        Assert.True(h.Root.Overlay.Layers[1].IsInclusiveAncestorOf(popover));
        // 浮层在「More options」下方，对话框内容在遮罩之上，浮层在对话框之上
        var mr = more.GetBoundingClientRect();
        var pr = popover.GetBoundingClientRect();
        Assert.Equal("bottom", popover.GetAttribute("data-side"));
        Assert.True(Math.Abs(pr.Y - (mr.Bottom + 4 + 6)) < 0.51f, pr + " vs " + mr);
        Assert.True(popover.IsInclusiveAncestorOf(h.Pipeline.HitTest(pr.Center)));
        Assert.True(dialog.IsInclusiveAncestorOf(h.Pipeline.HitTest(new Point(dialog.GetBoundingClientRect().X + 10, dialog.GetBoundingClientRect().Y + 10))));
        // 焦点在最上层的浮层中（最后打开的作用域生效）
        Assert.True(popover.IsInclusiveAncestorOf(h.Focused) || ReferenceEquals(popover, h.Focused));

        using var bitmap = h.Render(new Color(0.96f, 0.96f, 0.96f, 1));
        // 遮罩使常规内容变暗：卡片左上角附近的像素比背景暗
        var px = bitmap.GetPixel(60 * 2, 60 * 2);
        Assert.True(px.R < 0.7f, px.ToString());
        if (Environment.GetEnvironmentVariable("AWAUI_PRIMITIVES_SHOT") is { Length: > 0 } path) PngCodec.Save(bitmap, path);
    }
}
