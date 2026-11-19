using AwAUI.Browser;
using AwAUI.Platform;

namespace AwAUI.Browser.Tests;

sealed class RecordingSink : ITextInputSink
{
    public readonly List<TextCompositionEvent> Events = new();
    public readonly List<string> Commits = new();
    public void OnComposition(in TextCompositionEvent e) => Events.Add(e);
    public void OnCommit(string text) => Commits.Add(text);
}

public class CompositionTrackerTests
{
    static TextCompositionEvent E(TextCompositionKind k, string t, int a, int b) => new(k, t, a, b);

    [Fact]
    public void ChromeSequence()
    {
        // Chrome：compositionstart → (compositionupdate, input)* → compositionend
        var sink = new RecordingSink();
        var t = new CompositionTracker(sink);
        t.Handle(DomProtocol.CompositionStart, "", -1, -1);
        t.Handle(DomProtocol.CompositionUpdate, "n", -1, -1);
        t.Handle(DomProtocol.CompositionUpdate, "n", 1, 1);   // input 事件带回相同状态：合并
        t.Handle(DomProtocol.CompositionUpdate, "ni", -1, -1);
        Assert.True(t.IsComposing);
        Assert.Equal("ni", t.Text);
        t.Handle(DomProtocol.CompositionEnd, "你", -1, -1);
        Assert.False(t.IsComposing);
        Assert.Equal(new[]
        {
            E(TextCompositionKind.Start, "", 0, 0),
            E(TextCompositionKind.Update, "n", 1, 1),
            E(TextCompositionKind.Update, "ni", 2, 2),
            E(TextCompositionKind.End, "你", 1, 1),
        }, sink.Events);
        Assert.Equal(new[] { "你" }, sink.Commits);
    }

    [Fact]
    public void CaretInsideComposition()
    {
        var sink = new RecordingSink();
        var t = new CompositionTracker(sink);
        t.Start();
        t.Update("nihao", 3, 3);
        Assert.Equal(3, t.SelectionStart);
        t.Update("nihao", 4, 2); // 反向选区归一化
        Assert.Equal(2, t.SelectionStart);
        Assert.Equal(4, t.SelectionEnd);
        t.Update("ni", 9, 12); // 越界钳制
        Assert.Equal(2, t.SelectionStart);
        Assert.Equal(2, t.SelectionEnd);
    }

    [Fact]
    public void MissingStartIsSynthesized()
    {
        var sink = new RecordingSink();
        var t = new CompositionTracker(sink);
        t.Update("ka", -1, -1);
        Assert.Equal(TextCompositionKind.Start, sink.Events[0].Kind);
        Assert.Equal(E(TextCompositionKind.Update, "ka", 2, 2), sink.Events[1]);
    }

    [Fact]
    public void DuplicateStartKeepsComposition()
    {
        var sink = new RecordingSink();
        var t = new CompositionTracker(sink);
        t.Start();
        t.Update("a", -1, -1);
        t.Start();
        Assert.Equal("a", t.Text);
        Assert.Equal(2, sink.Events.Count);
    }

    [Fact]
    public void CancelledCompositionCommitsNothing()
    {
        var sink = new RecordingSink();
        var t = new CompositionTracker(sink);
        t.Start();
        t.Update("abc", -1, -1);
        t.End("");
        Assert.Equal(E(TextCompositionKind.End, "", 0, 0), sink.Events[sink.Events.Count - 1]);
        Assert.Empty(sink.Commits);
    }

    [Fact]
    public void EndWithoutStartOnlyCommits()
    {
        var sink = new RecordingSink();
        var t = new CompositionTracker(sink);
        t.End("x");
        Assert.Empty(sink.Events);
        Assert.Equal(new[] { "x" }, sink.Commits);
        t.End(null);
        Assert.Single(sink.Commits);
    }

    [Fact]
    public void ResetEndsWithoutCommit()
    {
        var sink = new RecordingSink();
        var t = new CompositionTracker(sink);
        t.Reset();
        Assert.Empty(sink.Events);
        t.Update("zz", -1, -1);
        t.Reset();
        Assert.False(t.IsComposing);
        Assert.Equal(TextCompositionKind.End, sink.Events[sink.Events.Count - 1].Kind);
        Assert.Empty(sink.Commits);
    }

    [Fact]
    public void UnknownKindIgnored()
    {
        var sink = new RecordingSink();
        new CompositionTracker(sink).Handle(77, "x", 0, 0);
        Assert.Empty(sink.Events);
        Assert.Empty(sink.Commits);
    }

    [Fact]
    public void OverlongCompositionIsTruncatedWithoutSplittingSurrogates()
    {
        var sink = new RecordingSink();
        var t = new CompositionTracker(sink);
        string s = new string('a', CompositionTracker.MaxCompositionLength - 1) + "😀" + "tail";
        t.Update(s, -1, -1);
        Assert.Equal(CompositionTracker.MaxCompositionLength - 1, t.Text.Length);
        Assert.False(char.IsHighSurrogate(t.Text[t.Text.Length - 1]));
    }

    [Theory]
    [InlineData("abc", 5, "abc")]
    [InlineData("abc", 2, "ab")]
    [InlineData("a😀", 2, "a")]
    [InlineData("a😀", 3, "a😀")]
    [InlineData("😀", 0, "")]
    public void Truncate(string s, int max, string expected) => Assert.Equal(expected, CompositionTracker.Truncate(s, max));
}

public class TextInputFilterTests
{
    [Theory]
    [InlineData("insertText", false, (int)BeforeInputAction.PreventAndCommit)]
    [InlineData("insertReplacementText", false, (int)BeforeInputAction.PreventAndCommit)]
    [InlineData("insertText", true, (int)BeforeInputAction.PassThrough)]
    [InlineData("insertCompositionText", false, (int)BeforeInputAction.PassThrough)]
    [InlineData("deleteCompositionText", false, (int)BeforeInputAction.PassThrough)]
    [InlineData("insertFromComposition", false, (int)BeforeInputAction.PassThrough)]
    [InlineData("deleteByComposition", false, (int)BeforeInputAction.PassThrough)]
    [InlineData("insertLineBreak", false, (int)BeforeInputAction.Prevent)]
    [InlineData("insertParagraph", false, (int)BeforeInputAction.Prevent)]
    [InlineData("deleteContentBackward", false, (int)BeforeInputAction.Prevent)]
    [InlineData("historyUndo", false, (int)BeforeInputAction.Prevent)]
    [InlineData("insertFromPaste", false, (int)BeforeInputAction.Prevent)]
    [InlineData("insertFromDrop", false, (int)BeforeInputAction.Prevent)]
    [InlineData("", false, (int)BeforeInputAction.Prevent)]
    [InlineData(null, false, (int)BeforeInputAction.Prevent)]
    public void Classify(string? inputType, bool composing, int expected) =>
        Assert.Equal((BeforeInputAction)expected, TextInputFilter.Classify(inputType, composing));

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("hello", "hello")]
    [InlineData("a\r\nb", "a\nb")]
    [InlineData("a\tb", "a\tb")]
    [InlineData("\u0000x\u001b\u007f", "x")]
    [InlineData("你好😀", "你好😀")]
    public void Sanitize(string? input, string expected) => Assert.Equal(expected, TextInputFilter.SanitizeCommit(input));

    [Fact]
    public void SanitizeTruncatesHugePaste()
    {
        var s = TextInputFilter.SanitizeCommit(new string('x', CompositionTracker.MaxCommitLength + 10));
        Assert.Equal(CompositionTracker.MaxCommitLength, s.Length);
    }
}

public class PresentHelperTests
{
    [Theory]
    [InlineData(CursorShape.Arrow, "default")]
    [InlineData(CursorShape.IBeam, "text")]
    [InlineData(CursorShape.Hand, "pointer")]
    [InlineData(CursorShape.Crosshair, "crosshair")]
    [InlineData(CursorShape.ResizeHorizontal, "ew-resize")]
    [InlineData(CursorShape.ResizeVertical, "ns-resize")]
    [InlineData(CursorShape.Wait, "wait")]
    [InlineData(CursorShape.NotAllowed, "not-allowed")]
    [InlineData(CursorShape.None, "none")]
    [InlineData((CursorShape)200, "default")]
    public void Cursors(CursorShape shape, string css) => Assert.Equal(css, CursorCss.ToCss(shape));

    [Fact]
    public void EveryCursorShapeMapsToACssKeyword()
    {
        var css = new HashSet<string>();
        foreach (CursorShape c in Enum.GetValues(typeof(CursorShape))) css.Add(CursorCss.ToCss(c));
        Assert.Equal(Enum.GetValues(typeof(CursorShape)).Length, css.Count); // 互不相同
    }

    [Fact]
    public void Unpremultiply()
    {
        byte[] src =
        {
            10, 20, 30, 255,   // 不透明：原样
            0, 0, 0, 0,        // 全透明
            64, 32, 0, 128,    // 半透明：64/128 → 128
            200, 0, 0, 100,    // 非法的预乘值（颜色 > alpha）：钳制到 255
        };
        var dst = new byte[src.Length];
        PixelConversion.UnpremultiplyRgba(src, dst);
        Assert.Equal(new byte[] { 10, 20, 30, 255, 0, 0, 0, 0, 128, 64, 0, 128, 255, 0, 0, 100 }, dst);
    }

    [Fact]
    public void UnpremultiplyInPlaceAndRoundTrip()
    {
        var buf = new byte[256 * 4];
        for (int a = 0; a < 256; a++)
        {
            buf[a * 4] = (byte)((200 * a + 127) / 255); // 预乘的 200
            buf[a * 4 + 3] = (byte)a;
        }
        PixelConversion.UnpremultiplyRgba(buf, buf);
        for (int a = 8; a < 256; a++) Assert.InRange(buf[a * 4], 200 - 255 / a - 1, 200 + 255 / a + 1);
    }

    [Fact]
    public void UnpremultiplyRejectsShortDestination() =>
        Assert.Throws<ArgumentException>(() => PixelConversion.UnpremultiplyRgba(new byte[8], new byte[4]));
}

public class ClientEditingTests
{
    [Theory]
    [InlineData("deleteContentBackward", (int)BeforeInputAction.PreventAndDeleteBackward)]
    [InlineData("deleteContentForward", (int)BeforeInputAction.PreventAndDeleteForward)]
    [InlineData("insertParagraph", (int)BeforeInputAction.PreventAndEnter)]
    [InlineData("insertLineBreak", (int)BeforeInputAction.PreventAndEnter)]
    [InlineData("insertText", (int)BeforeInputAction.PreventAndCommit)]
    [InlineData("deleteWordBackward", (int)BeforeInputAction.Prevent)]
    [InlineData("insertCompositionText", (int)BeforeInputAction.PassThrough)]
    public void WithAClientMobileEditsBecomeClientOperations(string inputType, int expected) =>
        Assert.Equal((BeforeInputAction)expected, TextInputFilter.Classify(inputType, isComposing: false, hasClient: true));

    [Fact]
    public void WithoutAClientDeletionsArePreventedAsBefore()
    {
        Assert.Equal(BeforeInputAction.Prevent, TextInputFilter.Classify("deleteContentBackward", false));
        Assert.Equal(BeforeInputAction.Prevent, TextInputFilter.Classify("insertParagraph", false));
        Assert.Equal(BeforeInputAction.PassThrough, TextInputFilter.Classify("deleteContentBackward", true, hasClient: true));
    }

    [Theory]
    [InlineData(3, 3, 5, true, "ab", "cd", 2, 3)]
    [InlineData(0, 0, 5, true, "", "ab", 0, 0)]       // 文首退格：不删除
    [InlineData(5, 5, 5, false, "ab", "", 5, 5)]      // 文末向前删除：不删除
    [InlineData(1, 4, 5, true, "a", "e", 1, 4)]       // 有选区：删除选区
    [InlineData(4, 4, 6, true, "a\uD83D\uDE00", "b", 2, 4)] // 代理对整体删除
    [InlineData(1, 1, 6, false, "a", "\uD83D\uDE00b", 1, 3)]
    public void DeletionRanges(int s, int e, int len, bool back, string before, string after, int rs, int re) =>
        Assert.Equal(new TextInputRange(rs, re), TextInputFilter.DeletionRange(new TextInputRange(s, e), len, back, before, after));
}
