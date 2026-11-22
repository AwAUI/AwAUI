using AwAUI.Graphics;
using AwAUI.Render;
using AwAUI.Text;
using AwAUI.Text.Testing;

namespace AwAUI.UI.UnitTests;

/// <summary>帧管线测试的装配：<see cref="TestTextEngine"/>（每字符 1em 的方块字体，结果只取决于字符数）+ 内存中的 UIRoot。</summary>
sealed class PipelineHarness
{
    public readonly UIRoot Root;
    public readonly RenderPipeline Pipeline;
    public readonly TextEngine Text;
    public int FrameRequests;

    readonly Node _node;

    public PipelineHarness(Node node, string? css = null, float width = 400, float height = 300, float scale = 1, bool defaultSheet = true, bool deferMount = false, TextEngine? engine = null)
    {
        _node = node;
        Text = engine ?? new TestTextEngine();
        Pipeline = new RenderPipeline(Text, includeDefaultStyleSheet: defaultSheet);
        if (css is not null) Pipeline.AddStyleSheet(css, "test.css");
        Pipeline.SetViewport(new Size(width, height), scale);
        Root = new UIRoot { Pipeline = Pipeline };
        Root.FrameRequested += _ => FrameRequests++;
        if (!deferMount) MountNow();
    }

    /// <summary>挂载（构造时 <c>deferMount</c> 为 true 时，先配置根——如时钟——再调用它）。</summary>
    public void MountNow() => Root.Mount(_node is Component ? _node : new Lambda { Render = () => _node });

    public Element El(string id) => Root.RootElement.ById(id);

    public Rect Box(string id) => El(id).BoundingRect;

    /// <summary>执行帧直到没有脏标记与待重建组件（最多 10 帧）。</summary>
    public void Frame()
    {
        for (int i = 0; i < 10; i++)
        {
            Root.RunFrame();
            if (!Root.NeedsFrame) return;
        }
    }

    public void Move(float x, float y) =>
        Root.Input.DispatchPointer(new Platform.RawPointerEvent(Platform.RawPointerEventType.Move, Platform.PointerType.Mouse, 1, new Point(x, y),
            Platform.PointerButtons.None, Platform.PointerButtons.None, Platform.KeyModifiers.None, TimeSpan.Zero));

    public void Click(float x, float y)
    {
        var p = new Point(x, y);
        Root.Input.DispatchPointer(new Platform.RawPointerEvent(Platform.RawPointerEventType.Down, Platform.PointerType.Mouse, 1, p,
            Platform.PointerButtons.Left, Platform.PointerButtons.Left, Platform.KeyModifiers.None, TimeSpan.Zero));
        Root.Input.DispatchPointer(new Platform.RawPointerEvent(Platform.RawPointerEventType.Up, Platform.PointerType.Mouse, 1, p,
            Platform.PointerButtons.Left, Platform.PointerButtons.None, Platform.KeyModifiers.None, TimeSpan.Zero));
    }

    /// <summary>用软件后端把当前帧光栅化到离屏位图。</summary>
    public Bitmap Render(Color? clear = null)
    {
        var backend = new SoftwareRenderBackend();
        var px = PixelSize.FromSize(Pipeline.Viewport, Pipeline.Scale);
        using var target = (BitmapRenderTarget)backend.CreateOffscreenTarget(px, Pipeline.Scale);
        target.Render(Pipeline.Frame, new RenderOptions(clear ?? Color.White));
        var copy = new Bitmap(px);
        target.Bitmap.CopyTo(copy);
        return copy;
    }
}

static class ColorAssert
{
    public static void Near(Color expected, Color actual, float tolerance = 0.02f)
    {
        bool ok = Math.Abs(expected.R - actual.R) <= tolerance && Math.Abs(expected.G - actual.G) <= tolerance
                  && Math.Abs(expected.B - actual.B) <= tolerance && Math.Abs(expected.A - actual.A) <= tolerance;
        Assert.True(ok, $"expected {expected}, actual {actual}");
    }
}

/// <summary>
/// 把帧 DisplayList 展开成「绝对坐标下的命令序列」：增量绘制把元素录成局部坐标的子列表，再以 Save / Translate / DrawDisplayList / Restore 合成，
/// 断言命令内容与顺序的测试用它看到与整帧平铺录制等价的序列。<see cref="DrawOp.Save"/> / <see cref="DrawOp.Restore"/> / <see cref="DrawOp.Translate"/>
/// / <see cref="DrawOp.DrawDisplayList"/> 被吸收，不回调。
/// </summary>
static class FrameWalker
{
    public delegate void Visit(DrawOp op, Point offset, ref Graphics.DisplayListReader reader);

    public static void Walk(DisplayList list, Visit visit) => Walk(list, new Point(0, 0), visit, 0);

    static void Walk(DisplayList list, Point origin, Visit visit, int depth)
    {
        if (depth > 256) throw new InvalidOperationException("DisplayList nesting too deep");
        var stack = new Stack<Point>();
        var offset = origin;
        var r = list.GetReader();
        while (r.MoveNext())
        {
            switch (r.Op)
            {
                case DrawOp.Save:
                    stack.Push(offset);
                    break;
                case DrawOp.Restore:
                    if (stack.Count > 0) offset = stack.Pop();
                    break;
                case DrawOp.Translate:
                {
                    var t = r.Read<Graphics.Commands.TranslateCmd>();
                    offset = new Point(offset.X + t.Dx, offset.Y + t.Dy);
                    break;
                }
                case DrawOp.DrawDisplayList:
                    Walk(r.GetResource<DisplayList>(r.Read<Graphics.Commands.DrawDisplayListCmd>().DisplayListRef), offset, visit, depth + 1);
                    break;
                default:
                    visit(r.Op, offset, ref r);
                    break;
            }
        }
    }

    /// <summary>展开后的操作码序列。</summary>
    public static List<DrawOp> Ops(DisplayList list)
    {
        var ops = new List<DrawOp>();
        Walk(list, (DrawOp op, Point _, ref Graphics.DisplayListReader _) => ops.Add(op));
        return ops;
    }

    public static Rect Offset(Rect r, Point o) => new(r.X + o.X, r.Y + o.Y, r.Width, r.Height);

    /// <summary>嵌套的 DisplayList 层数（含自身）。</summary>
    public static int Depth(DisplayList list)
    {
        int max = 0;
        var r = list.GetReader();
        while (r.MoveNext())
            if (r.Op == DrawOp.DrawDisplayList)
                max = Math.Max(max, Depth(r.GetResource<DisplayList>(r.Read<Graphics.Commands.DrawDisplayListCmd>().DisplayListRef)));
        return max + 1;
    }
}
