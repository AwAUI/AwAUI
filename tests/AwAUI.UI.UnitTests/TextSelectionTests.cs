using AwAUI.Graphics;
using AwAUI.Graphics.Commands;
using AwAUI.Platform;

namespace AwAUI.UI.UnitTests;

/// <summary>
/// 只读文本选择（design/05「文本选择」）：鼠标拖选普通文本、复制、user-select 与代码块行号。
/// </summary>
public class TextSelectionTests
{
    sealed class RecordingClipboard : Clipboard
    {
        public string? Written;
        public override Task<string?> GetTextAsync() => Task.FromResult<string?>(null);
        public override Task<bool> SetTextAsync(string text)
        {
            Written = text;
            return Task.FromResult(true);
        }
    }

    static void Down(PipelineHarness h, Point p, KeyModifiers modifiers = KeyModifiers.None)
        => h.Root.Input.DispatchPointer(new RawPointerEvent(RawPointerEventType.Down, PointerType.Mouse, 1, p,
            PointerButtons.Left, PointerButtons.Left, modifiers, TimeSpan.Zero));

    static void Drag(PipelineHarness h, Point p)
        => h.Root.Input.DispatchPointer(new RawPointerEvent(RawPointerEventType.Move, PointerType.Mouse, 1, p,
            PointerButtons.Left, PointerButtons.Left, KeyModifiers.None, TimeSpan.Zero));

    static void Up(PipelineHarness h, Point p, KeyModifiers modifiers = KeyModifiers.None)
        => h.Root.Input.DispatchPointer(new RawPointerEvent(RawPointerEventType.Up, PointerType.Mouse, 1, p,
            PointerButtons.None, PointerButtons.Left, modifiers, TimeSpan.Zero));

    static Point At(Element element, float characters) => new(element.BoundingRect.X + characters * 14 + 0.25f,
        element.BoundingRect.Y + element.BoundingRect.Height / 2);

    static PipelineHarness TwoLines(string? css = null) => new(
        new Div
        {
            Children =
            [
                new Text("hello") { Id = "a" },
                new Text("world") { Id = "b" },
            ],
        }, css);

    [Fact]
    public void Dragging_selects_text_across_paragraphs()
    {
        var h = TwoLines();
        h.Frame();
        var a = h.El("a");
        var b = h.El("b");

        Down(h, At(a, 2));
        Drag(h, At(b, 3));
        Up(h, At(b, 3));

        Assert.True(h.Root.Selection.HasSelection);
        Assert.Equal("llo\nwor", h.Root.Selection.SelectedText);
    }

    static bool HasSelectionHighlight(PipelineHarness h)
    {
        bool found = false;
        FrameWalker.Walk(h.Pipeline.Frame, (DrawOp op, Point _, ref Graphics.DisplayListReader reader) =>
        {
            if (op != DrawOp.FillRect) return;
            var c = reader.Read<FillRectCmd>();
            if (c.Paint.IsSolid && Math.Abs(c.Paint.Color.A - 0.32f) < 0.01f && c.Rect.Width > 0.5f) found = true;
        });
        return found;
    }

    [Fact]
    public void Selection_changes_request_a_frame_and_repaint_the_paragraphs()
    {
        var h = TwoLines();
        h.Frame();
        var a = h.El("a");

        Down(h, At(a, 1));
        Drag(h, At(a, 4));

        Assert.True(h.Root.NeedsFrame);
        h.Frame();
        Assert.False(h.Root.NeedsFrame);
        Assert.True(HasSelectionHighlight(h));

        h.Root.Selection.Clear();
        h.Frame();
        Assert.False(HasSelectionHighlight(h));
    }

    [Fact]
    public void A_plain_click_collapses_the_selection_but_shift_click_extends_it()
    {
        var h = TwoLines();
        h.Frame();
        var a = h.El("a");
        var b = h.El("b");

        Down(h, At(b, 2));
        Up(h, At(b, 2));
        Assert.False(h.Root.Selection.HasSelection);

        Down(h, At(a, 1));
        Up(h, At(a, 1));
        Down(h, At(b, 4), KeyModifiers.Shift);
        Up(h, At(b, 4), KeyModifiers.Shift);
        Assert.Equal("ello\nworl", h.Root.Selection.SelectedText);
    }

    [Fact]
    public void Text_inside_a_focusable_scroll_container_is_still_selectable()
    {
        var container = new Div { TabIndex = 0, Children = [new Text("scrolled") { Id = "t" }] };
        var h = new PipelineHarness(container);
        h.Frame();

        Down(h, At(h.El("t"), 0));
        Drag(h, At(h.El("t"), 8));
        Up(h, At(h.El("t"), 8));

        Assert.Same(h.El("t").Parent, h.Root.FocusManager.FocusedElement);
        Assert.Equal("scrolled", h.Root.Selection.SelectedText);
    }

    [Fact]
    public void User_select_none_is_not_selected_or_copied()
    {
        var h = new PipelineHarness(new Div
        {
            Children =
            [
                new Span("ignore") { Id = "none" },
                new Text("keep") { Id = "keep" },
            ],
        }, "#none { user-select: none; }");
        h.Frame();
        var clipboard = new RecordingClipboard();
        h.Root.Input.Clipboard = clipboard;

        Down(h, At(h.El("keep"), 0));
        Drag(h, At(h.El("keep"), 4));
        Up(h, At(h.El("keep"), 4));
        Assert.Equal("keep", h.Root.Selection.SelectedText);

        h.Root.Input.DispatchKey(new RawKeyEvent(RawKeyEventType.Down, PhysicalKey.KeyA, Shortcuts.PrimaryModifier,
            false, 0, TimeSpan.Zero, "a"));
        Assert.Equal("keep", h.Root.Selection.SelectedText);
    }

    [Fact]
    public void Primary_c_copies_the_selection()
    {
        var h = TwoLines();
        h.Frame();
        var clipboard = new RecordingClipboard();
        h.Root.Input.Clipboard = clipboard;

        Down(h, At(h.El("a"), 1));
        Drag(h, At(h.El("b"), 5));
        Up(h, At(h.El("b"), 5));

        h.Root.Input.DispatchKey(new RawKeyEvent(RawKeyEventType.Down, PhysicalKey.KeyC, Shortcuts.PrimaryModifier,
            false, 0, TimeSpan.Zero, "c"));
        Assert.Equal("ello\nworld", clipboard.Written);
    }

    [Fact]
    public void Double_click_selects_a_word_and_triple_click_the_paragraph()
    {
        var h = new PipelineHarness(new Text("one two three") { Id = "t" });
        h.Frame();
        var t = h.El("t");
        var p = At(t, 5);
        var time = TimeSpan.FromMilliseconds(100);
        for (int i = 0; i < 2; i++)
        {
            h.Root.Input.DispatchPointer(new RawPointerEvent(RawPointerEventType.Down, PointerType.Mouse, 1, p,
                PointerButtons.Left, PointerButtons.Left, KeyModifiers.None, time));
            h.Root.Input.DispatchPointer(new RawPointerEvent(RawPointerEventType.Up, PointerType.Mouse, 1, p,
                PointerButtons.None, PointerButtons.Left, KeyModifiers.None, time));
            time += TimeSpan.FromMilliseconds(50);
        }
        Assert.Equal("two", h.Root.Selection.SelectedText);

        h.Root.Input.DispatchPointer(new RawPointerEvent(RawPointerEventType.Down, PointerType.Mouse, 1, p,
            PointerButtons.Left, PointerButtons.Left, KeyModifiers.None, time));
        Assert.Equal("one two three", h.Root.Selection.SelectedText);
    }

    [Fact]
    public void User_select_all_selects_the_whole_paragraph_on_press()
    {
        var h = new PipelineHarness(new Div
        {
            Children =
            [
                new Text("select me") { Id = "all" },
                new Text("other") { Id = "other" },
            ],
        }, "#all { user-select: all; }");
        h.Frame();

        Down(h, At(h.El("all"), 1));
        Up(h, At(h.El("all"), 1));
        Assert.Equal("select me", h.Root.Selection.SelectedText);
    }

    [Fact]
    public void Line_numbers_in_a_code_block_are_skipped()
    {
        var h = new PipelineHarness(new Div
        {
            Children =
            [
                new Div
                {
                    Children =
                    [
                        new Span("1") { Id = "n", Class = "gutter" },
                        new Span("code") { Id = "code" },
                    ],
                },
            ],
        }, ".gutter { user-select: none; }");
        h.Frame();
        var code = h.El("code");

        Down(h, At(code, 0));
        Drag(h, At(code, 4));
        Up(h, At(code, 4));
        Assert.Equal("code", h.Root.Selection.SelectedText);
        Assert.DoesNotContain("1", h.Root.Selection.SelectedText);
    }
}
