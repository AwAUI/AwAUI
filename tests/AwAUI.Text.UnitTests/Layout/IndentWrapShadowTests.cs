using AwAUI.Graphics;
using static AwAUI.Text.UnitTests.Layout.Kit;

namespace AwAUI.Text.UnitTests.Layout;

/// <summary>第九批的段落特性：text-indent、tab-size、text-wrap: balance、text-shadow、text-underline-offset / position，以及 GetStyleAt。</summary>
public class IndentWrapShadowTests
{
    static ParagraphStyle With(ParagraphStyle p, float indent = 0, float indentPct = 0, bool hanging = false, bool eachLine = false,
        float tab = 8, bool tabLength = false, TextWrapStyle wrap = TextWrapStyle.Auto) => new()
    {
        TextStyle = p.TextStyle,
        WhiteSpace = p.WhiteSpace,
        TextAlign = p.TextAlign,
        Direction = p.Direction,
        TextIndent = indent,
        TextIndentPercent = indentPct,
        HangingIndent = hanging,
        IndentEachLine = eachLine,
        TabSize = tab,
        TabSizeIsLength = tabLength,
        TextWrapStyle = wrap,
    };

    [Fact]
    public void Text_indent_first_line_hanging_each_line_and_percentage()
    {
        var l = Lay("aaaa bbbb", With(P(), indent: 20), 60);
        Assert.Equal(new[] { "aaaa ", "bbbb" }, Lines(l));
        Assert.Equal(20, l.GetLine(0).Left);
        Assert.Equal(0, l.GetLine(1).Left);
        // 缩进占用首行宽度："aaaa bb" 在 60 宽、缩进 20 时只放得下 aaaa
        var hang = Lay("aaaa bbbb", With(P(), indent: 20, hanging: true), 60);
        Assert.Equal(0, hang.GetLine(0).Left);
        Assert.Equal(20, hang.GetLine(1).Left);
        var each = Lay("ab\ncd", With(P(WhiteSpace.PreLine), indent: 10, eachLine: true), 100);
        Assert.Equal(10, each.GetLine(0).Left);
        Assert.Equal(10, each.GetLine(1).Left);
        var pct = Lay("ab", With(P(), indentPct: 0.1f), 200);
        Assert.Equal(20, pct.GetLine(0).Left);
        // 右到左：缩进在右侧
        var rtl = Lay("ab", With(P(dir: ParagraphDirection.RightToLeft), indent: 10), 100);
        Assert.Equal(70, rtl.GetLine(0).Left);
        // 固有宽度计入正缩进
        Assert.Equal(40, Lay("ab", With(P(), indent: 20)).MaxIntrinsicWidth);
    }

    [Fact]
    public void Tab_size_in_spaces_and_length_only_when_white_space_is_preserved()
    {
        Assert.Equal(50, Lay("\tx", With(P(WhiteSpace.Pre), tab: 4)).Width);
        Assert.Equal(35, Lay("\tx", With(P(WhiteSpace.Pre), tab: 25, tabLength: true)).Width);
        Assert.Equal(90, Lay("\tx", P(WhiteSpace.Pre)).Width); // 默认 8
        Assert.Equal(10, Lay("\tx", With(P(), tab: 4)).Width);  // 折叠空白：行首空白被移除
    }

    [Fact]
    public void Balance_keeps_line_count_and_evens_lengths()
    {
        var greedy = Lay("aa bb cc dd ee ff gg", P(), 150);
        Assert.Equal(2, greedy.LineCount);
        var bal = Lay("aa bb cc dd ee ff gg", With(P(), wrap: TextWrapStyle.Balance), 150);
        Assert.Equal(2, bal.LineCount);
        Assert.True(Math.Abs(bal.GetLine(0).Width - bal.GetLine(1).Width) < Math.Abs(greedy.GetLine(0).Width - greedy.GetLine(1).Width));
        // 对齐仍按原可用宽度
        var centered = Lay("aa bb cc dd ee ff gg", new ParagraphStyle { TextStyle = Ts(), TextAlign = TextAlign.Center, TextWrapStyle = TextWrapStyle.Balance }, 150);
        Assert.Equal((150 - centered.GetLine(0).Width) / 2, centered.GetLine(0).Left, 0.01f);
        // 超过 MaxBalancedLines 行按普通换行
        string many = string.Join(" ", Enumerable.Repeat("aaaa", 20));
        Assert.Equal(Lines(Lay(many, P(), 100)), Lines(Lay(many, With(P(), wrap: TextWrapStyle.Balance), 100)));
    }

    [Fact]
    public void Text_shadow_layers_are_drawn_below_text_last_layer_first()
    {
        var style = new TextStyle
        {
            FontSize = 10,
            Color = Color.Black,
            Shadows = new[] { new TextShadow(1, 2, 0, Color.Parse("#ff0000")), new TextShadow(3, 3, 0, Color.Parse("#0000ff")) },
        };
        var l = Lay("ab", P(style: style));
        var (glyphs, _, _) = Record(l);
        Assert.Equal(3, glyphs.Count);
        Assert.Equal(new Point(3, 8 + 3), glyphs[0].Origin);   // 第二层（蓝）在最下
        Assert.Equal(new Point(1, 8 + 2), glyphs[1].Origin);
        Assert.Equal(new Point(0, 8), glyphs[2].Origin);
        // 数量有上限
        var many = new TextStyle { FontSize = 10, Shadows = Enumerable.Repeat(new TextShadow(1, 1, 0, Color.Black), 1000).ToArray() };
        Assert.Equal(TextStyle.MaxShadows + 1, Record(Lay("a", P(style: many))).Glyphs.Count);
    }

    [Fact]
    public void Underline_offset_and_under_position_move_the_underline()
    {
        Rect Underline(TextStyle s) => Record(Lay("a", P(style: s))).Rects.Single().Rect;
        var auto = Underline(new TextStyle { FontSize = 10, Decoration = TextDecorationLine.Underline, DecorationThickness = 1 });
        var offset = Underline(new TextStyle { FontSize = 10, Decoration = TextDecorationLine.Underline, DecorationThickness = 1, UnderlineOffset = 4 });
        var under = Underline(new TextStyle { FontSize = 10, Decoration = TextDecorationLine.Underline, DecorationThickness = 1, UnderlinePosition = UnderlinePosition.Under });
        Assert.Equal(8 + 4, offset.Top, 0.01f);          // 基线 8 之下 4
        Assert.Equal(8 + 2, under.Top, 0.01f);           // 下伸部（0.2 em）之下
        Assert.True(auto.Top > 8 && auto.Top < 10);
    }

    [Fact]
    public void Get_style_at_returns_span_styles()
    {
        var b = Engine.CreateParagraphBuilder(P());
        var bold = new TextStyle { FontSize = 10, FontWeight = new AwAUI.Graphics.Text.FontWeight(700) };
        b.AddText("ab");
        b.PushStyle(bold);
        b.AddText("cd");
        b.Pop();
        var l = b.Build();
        Assert.Same(bold, l.GetStyleAt(2));
        Assert.Same(bold, l.GetStyleAt(3));
        Assert.Same(l.Style.TextStyle, l.GetStyleAt(99));
    }
}
