using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwAUI.Controls.UnitTests.Integration;
using AwAUI.Platform;
using AwAUI.UI;

namespace AwAUI.Shadcn.Integration.S2;

/// <summary>
/// S2 CodeBlock acceptance: header (file name, language badge, copy button), line gutter, line splitting, and the
/// clipboard / "Copied" feedback.
/// </summary>
public class CodeBlockTests
{
    static CodeBlockTests() => ShadcnTheme.Register();

    static UIHarness Harness(Func<Node> render) =>
        new(render, appStyleSheets: true, width: 640, height: 480);

    static string? A(Element e, string name) => e.GetAttribute(name);

    static List<Element> Slots(UIHarness h, string slot) =>
        h.FindAll(e => A(e, "data-slot") == slot);

    sealed class RecordingClipboard : Clipboard
    {
        public string? Text { get; private set; }

        public override Task<string?> GetTextAsync() => Task.FromResult(Text);

        public override Task<bool> SetTextAsync(string text)
        {
            Text = text;
            return Task.FromResult(true);
        }
    }

    [Fact]
    public void CodeBlock_renders_header_lines_and_line_numbers()
    {
        var h = Harness(() => new CodeBlock
        {
            Code = "one\ntwo\nthree",
            Language = "csharp",
            FileName = "Program.cs",
        });

        Assert.Single(Slots(h, "code-block"));
        Assert.Single(Slots(h, "code-block-header"));
        Assert.Equal("Program.cs", Slots(h, "code-block-filename").Single().TextContent.Trim());
        Assert.Contains("csharp", Slots(h, "code-block-language").Single().TextContent);

        Assert.Equal(3, Slots(h, "code-block-lines").Single().Children.Count);
        Assert.Equal(3, Slots(h, "code-block-line").Count);
        Assert.Equal(new[] { "1", "2", "3" },
            Slots(h, "code-block-line-number").Select(n => n.TextContent.Trim()).ToArray());
        Assert.Equal(new[] { "one", "two", "three" },
            Slots(h, "code-block-code").Select(c => c.TextContent).ToArray());
        Assert.Single(Slots(h, "code-block-copy"));
    }

    [Fact]
    public void CodeBlock_can_hide_line_numbers_and_the_header()
    {
        var h = Harness(() => new CodeBlock
        {
            Code = "a\nb",
            ShowLineNumbers = false,
            ShowCopy = false,
        });

        Assert.Empty(Slots(h, "code-block-line-number"));
        Assert.Empty(Slots(h, "code-block-header"));
        Assert.Equal(2, Slots(h, "code-block-line").Count);
    }

    [Fact]
    public void Code_lines_can_be_selected_with_the_mouse_and_copied()
    {
        var clipboard = new RecordingClipboard();
        var h = Harness(() => new CodeBlock
        {
            Code = "one\ntwo",
            Language = "text",
            FileName = "notes.txt",
        });
        h.Root.Input.Clipboard = clipboard;

        var code = Slots(h, "code-block-code");
        var first = code[0].GetBoundingClientRect();
        var second = code[1].GetBoundingClientRect();
        var start = new Point(first.X + 0.5f, first.Center.Y);
        var end = new Point(second.Right - 0.5f, second.Center.Y);

        h.PointerDown(start);
        h.PointerMove(end);
        h.PointerUp(end);

        Assert.Equal("one\ntwo", h.Root.Selection.SelectedText);
        h.Key(PhysicalKey.KeyC, "c", Shortcuts.PrimaryModifier);
        Assert.Equal("one\ntwo", clipboard.Text);
    }

    [Fact]
    public void CodeBlock_copy_writes_to_the_clipboard_and_shows_copied_briefly()
    {
        var clock = new ManualUIClock();
        var clipboard = new RecordingClipboard();
        var h = Harness(() => new CodeBlock { Code = "Console.WriteLine();" });
        h.Root.Clock = clock;
        h.Root.Input.Clipboard = clipboard;

        h.Click(Slots(h, "code-block-copy").Single());

        Assert.Equal("Console.WriteLine();", clipboard.Text);
        Assert.Equal("true", A(Slots(h, "code-block-copy").Single(), "data-copied"));
        Assert.Contains("Copied", Slots(h, "code-block-copy").Single().TextContent);

        clock.Advance(TimeSpan.FromMilliseconds(1500));
        h.Frame();

        Assert.Equal("false", A(Slots(h, "code-block-copy").Single(), "data-copied"));
        Assert.Contains("Copy", Slots(h, "code-block-copy").Single().TextContent);
    }
}
