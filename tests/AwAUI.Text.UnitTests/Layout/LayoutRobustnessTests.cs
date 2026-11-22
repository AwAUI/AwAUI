using System.Diagnostics;
using AwAUI.Text.Testing;
using AwAUI.Unicode;
using Xunit.Abstractions;
using static AwAUI.Text.UnitTests.Layout.Kit;

namespace AwAUI.Text.UnitTests.Layout;

public class LayoutRobustnessTests
{
    readonly ITestOutputHelper _output;

    public LayoutRobustnessTests(ITestOutputHelper output) => _output = output;

    static void Exercise(TextLayout l)
    {
        foreach (var w in new[] { float.PositiveInfinity, 0, 1, 37, 100, float.NaN, -1, float.MaxValue })
        {
            l.Layout(w);
            _ = l.Width;
            _ = l.Height;
            _ = l.MinIntrinsicWidth;
            _ = l.MaxIntrinsicWidth;
            Assert.True(l.LineCount >= 1);
            Assert.False(float.IsNaN(l.Height));
            Record(l);
            var rects = new List<Rect>();
            l.GetSelectionRects(new TextRange(0, l.Text.Length), rects);
            var p = l.HitTest(new Point(5, 5));
            Assert.InRange(p.Offset, 0, l.Text.Length);
            foreach (CaretMove m in Enum.GetValues(typeof(CaretMove)))
                Assert.InRange(l.Move(p, m).Offset, 0, l.Text.Length);
            _ = l.GetCaretRect(new TextPosition(l.Text.Length));
            _ = l.GetCaretRect(new TextPosition(-5));
            _ = l.GetWordAt(l.Text.Length);
            _ = l.GetGraphemeAt(-1);
            _ = l.GetLineAt(int.MaxValue);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\n\n\n")]
    [InlineData("\r\r\n\n")]
    [InlineData("\u0001\u0007\u001b[0m\u007f\u0085")]
    [InlineData("\uD800a\uDC00\uDBFF")]
    [InlineData("\u200B\u200D\uFE0F\u00AD")]
    [InlineData("\u2029\u2028\u000B\u000C")]
    [InlineData("\u0301\u0301\u0301abc")]
    [InlineData("\uFFFC\uFFFC")]
    [InlineData("a\tb\t\tc")]
    public void Degenerate_text_in_every_white_space_mode(string text)
    {
        foreach (WhiteSpace ws in Enum.GetValues(typeof(WhiteSpace)))
            foreach (var dir in new[] { ParagraphDirection.LeftToRight, ParagraphDirection.RightToLeft, ParagraphDirection.Auto })
                Exercise(Lay(text, P(ws, TextAlign.Justify, dir, maxLines: 2, ellipsis: "…", overflowWrap: OverflowWrap.Anywhere)));
    }

    [Fact]
    public void Degenerate_style_values()
    {
        var styles = new[]
        {
            new TextStyle { FontSize = float.NaN },
            new TextStyle { FontSize = -10 },
            new TextStyle { FontSize = float.PositiveInfinity },
            new TextStyle { FontSize = 10, LetterSpacing = float.PositiveInfinity, WordSpacing = float.NaN },
            new TextStyle { FontSize = 10, LetterSpacing = -1000 },
            new TextStyle { FontSize = 10, DecorationThickness = float.MaxValue, Decoration = TextDecorationLine.Underline | TextDecorationLine.LineThrough, DecorationStyle = TextDecorationStyle.Wavy },
            new TextStyle { FontSize = 10, LineHeight = LineHeight.Pixels(float.MaxValue) },
            new TextStyle { FontSize = 10, LineHeight = LineHeight.Multiple(-3) },
        };
        foreach (var s in styles)
        {
            var b = Engine.CreateParagraphBuilder(P(align: TextAlign.Justify));
            b.PushStyle(s);
            b.AddText("hello world " + Hebrew + " 中文");
            b.AddPlaceholder(new PlaceholderStyle(float.NaN, float.PositiveInfinity, PlaceholderAlignment.Middle, float.NaN));
            b.Pop();
            b.Pop();
            Exercise(b.Build());
        }
        var zero = Lay("abc", P(style: new TextStyle { FontSize = 0 }));
        Assert.Equal(0, zero.Width);
        Assert.Empty(Record(zero).Glyphs);
    }

    [Fact]
    public void Deep_bidi_embedding_and_many_runs()
    {
        var text = new string('\u202B', 300) + "abc" + Hebrew + new string('\u202C', 300) + string.Concat(Enumerable.Repeat("a" + Alef, 2000));
        Exercise(Lay(text, P(dir: ParagraphDirection.Auto)));
    }

    [Fact]
    public void Huge_unbreakable_word_is_linear()
    {
        var text = new string('x', 200_000);
        var sw = Stopwatch.StartNew();
        var l = Lay(text, P(overflowWrap: OverflowWrap.Anywhere), 100);
        Assert.Equal(20_000, l.LineCount);
        l.Layout(1000);
        Assert.Equal(2_000, l.LineCount);
        var caret = l.GetCaretRect(new TextPosition(150_000));
        Assert.Equal(new Rect(0, 15_000, 0, 10), caret);   // 每行 100 个字符：第 1500 行行首
        Assert.Equal(new TextRange(0, 200_000), l.GetWordAt(5));
        sw.Stop();
        _output.WriteLine($"200k-char word: {sw.ElapsedMilliseconds} ms");
        Assert.True(sw.ElapsedMilliseconds < 10_000);
    }

    [Fact]
    public void Text_without_glyphs_and_many_styles_is_linear()
    {
        var engine = NotoLayoutTests.CreateEngine();
        var arabic = string.Concat(Enumerable.Repeat("\u0645\u0631\u062D\u0628\u0627 ", 20_000));
        var sw = Stopwatch.StartNew();
        var l = Lay(arabic, P(dir: ParagraphDirection.Auto), 300, engine);
        Assert.True(l.IsRightToLeft);
        var b = engine.CreateParagraphBuilder(P());
        for (int i = 0; i < 5_000; i++)
        {
            b.PushStyle(new TextStyle { FontSize = 10 + i % 7, LetterSpacing = i % 3 });
            b.AddText("ab ");
            b.Pop();
        }
        var styled = b.Build();
        styled.Layout(200);
        Record(styled);
        sw.Stop();
        _output.WriteLine($"120k chars of missing glyphs + 5000 styles: {sw.ElapsedMilliseconds} ms");
        Assert.True(sw.ElapsedMilliseconds < 10_000);
    }

    [Fact]
    public void Many_spaces_and_newlines_are_linear()
    {
        var text = string.Concat(Enumerable.Repeat(" \n \t", 50_000));
        var sw = Stopwatch.StartNew();
        foreach (WhiteSpace ws in Enum.GetValues(typeof(WhiteSpace)))
            Lay(text, P(ws), 50);
        sw.Stop();
        _output.WriteLine($"200k whitespace × 6 modes: {sw.ElapsedMilliseconds} ms");
        Assert.True(sw.ElapsedMilliseconds < 20_000);
    }

    [Fact]
    public void Builder_rejects_nulls_and_tolerates_extra_pops()
    {
        var b = new ParagraphBuilder(P(), Engine);
        Assert.Throws<ArgumentNullException>(() => b.PushStyle(null!));
        Assert.Throws<ArgumentNullException>(() => b.AddText((string)null!));
        b.Pop();
        b.AddText("a");
        Assert.Equal(1, b.Length);
        var first = b.Build();
        b.AddText("b");
        Assert.Equal("a", first.Text);
        Assert.Equal("ab", b.Build().Text);
        Assert.Throws<ArgumentNullException>(() => new ParagraphBuilder(null!, Engine));
        Assert.Throws<ArgumentOutOfRangeException>(() => first.GetLine(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => first.HitTestLine(-1, 0));
    }

    [Fact]
    public void Shape_cache_is_bounded()
    {
        var engine = new TestTextEngine(shapeCacheEntries: 100);
        var text = string.Join(" ", Enumerable.Range(0, 1000).Select(i => "w" + i));
        Lay(text, engine: engine);
        Assert.True(engine.ShapeCacheStatistics.Count <= 100);
        engine.ClearCaches();
        Assert.Equal(0, engine.ShapeCacheStatistics.Count);
        var off = new TestTextEngine(shapeCacheEntries: 0);
        Lay(text, engine: off);
        Assert.Equal(0, off.ShapeCacheStatistics.Count);
    }

    [Fact]
    public void Layout_can_be_read_from_other_threads_after_layout()
    {
        var l = Lay(string.Join(" ", Enumerable.Repeat("hello " + Hebrew, 200)), width: 200);
        var expected = Record(l).Glyphs.Count;
        Parallel.For(0, 8, _ =>
        {
            for (int i = 0; i < 20; i++)
            {
                Assert.Equal(expected, Record(l).Glyphs.Count);
                l.HitTest(new Point(i * 7, i * 13));
            }
        });
    }

    [Fact]
    public void Measures_a_2000_word_paragraph()
    {
        var words = new[] { "lorem", "ipsum", "dolor", "sit", "amet", "consectetur", "adipiscing", "elit", "sed", "do", "eiusmod", "tempor",
            "incididunt", "ut", "labore", "et", "dolore", "magna", "aliqua", "enim", "ad", "minim", "veniam", "quis", "nostrud", "exercitation" };
        var rng = new Random(42);
        var text = string.Join(" ", Enumerable.Range(0, 2000).Select(_ => words[rng.Next(words.Length)]));
        var style = P(style: new TextStyle { FontSize = 14 });
        foreach (var (name, engine) in new (string, TextEngine)[] { ("box font", new TestTextEngine()), ("Noto Sans", NotoLayoutTests.CreateEngine()) })
        {
            const int runs = 15;
            Lay(text, style, 400, engine);   // 预热 JIT
            var cold = new double[runs];
            var warm = new double[runs];
            var relayout = new double[runs];
            TextLayout l = null!;
            for (int r = 0; r < runs; r++)
            {
                engine.ClearCaches();                       // 冷缓存：每个词都要整形
                var sw = Stopwatch.StartNew();
                l = Lay(text, style, 400, engine);
                cold[r] = sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                Lay(text, style, 400, engine);              // 热缓存：词级整形缓存全部命中
                warm[r] = sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                l.Layout(300 + r);                          // 只重新断行
                relayout[r] = sw.Elapsed.TotalMilliseconds;
            }
            static double Median(double[] a) => a.OrderBy(x => x).ElementAt(a.Length / 2);
            _output.WriteLine($"{name}: {text.Length} chars / 2000 words, {l.LineCount} lines at 314px; build (itemize + shape + break opportunities) + layout: " +
                $"cold cache {Median(cold):F2} ms, warm cache {Median(warm):F2} ms; re-layout at a new width {Median(relayout):F3} ms (medians of {runs})");
            Assert.True(Median(relayout) < Median(cold));
        }
    }
}
