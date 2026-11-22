using AwAUI.Graphics;
using AwAUI.MacOS;
using AwAUI.Platform;

namespace AwAUI.Platform.UnitTests;

/// <summary>macOS 输入桥的纯逻辑：组合内的方向键命令与 <c>characterIndexForPoint:</c> 的下标换算。</summary>
public class MacInputLogicTests
{
    /// <summary>文档中每个下标对应一个宽 10、高 10 的插入点矩形（x = 下标 × 10）。</summary>
    sealed class FakeClient(int length) : ITextInputClient
    {
        public TextInputOptions Options => default;
        public int Length { get; } = length;
        public TextInputRange Selection { get; set; }
        public TextInputRange? Composition => null;
        public string GetText(TextInputRange range) => new('x', Math.Max(0, range.End - range.Start));
        public Rect GetCaretRect() => GetTextBounds(TextInputRange.Caret(Selection.Start));
        public Rect GetTextBounds(TextInputRange range) => new(range.Start * 10, 0, 10, 10);
        public void SetComposition(string text, TextInputRange selection) { }
        public void Commit(string text) { }
        public void ReplaceRange(TextInputRange range, string text) { }
    }

    [Fact]
    public void Move_caret_forward_and_backward_inside_the_composition()
    {
        var composition = new TextInputRange(0, 5);
        Assert.Equal(new TextInputRange(3, 3),
            MacInputLogic.MoveCompositionSelection(new TextInputRange(2, 2), composition, forward: true));
        Assert.Equal(new TextInputRange(1, 1),
            MacInputLogic.MoveCompositionSelection(new TextInputRange(2, 2), composition, forward: false));
    }

    [Fact]
    public void Move_collapses_a_selection_to_the_movement_direction()
    {
        var composition = new TextInputRange(0, 5);
        Assert.Equal(new TextInputRange(4, 4),
            MacInputLogic.MoveCompositionSelection(new TextInputRange(1, 4), composition, forward: true));
        Assert.Equal(new TextInputRange(1, 1),
            MacInputLogic.MoveCompositionSelection(new TextInputRange(1, 4), composition, forward: false));
    }

    [Fact]
    public void Move_stays_within_the_composition()
    {
        var composition = new TextInputRange(0, 5);
        Assert.Equal(new TextInputRange(5, 5),
            MacInputLogic.MoveCompositionSelection(new TextInputRange(5, 5), composition, forward: true));
        Assert.Equal(new TextInputRange(0, 0),
            MacInputLogic.MoveCompositionSelection(new TextInputRange(0, 0), composition, forward: false));
    }

    [Fact]
    public void Move_enters_the_composition_when_the_selection_is_outside()
    {
        var composition = new TextInputRange(0, 5);
        var result = MacInputLogic.MoveCompositionSelection(new TextInputRange(10, 10), composition, forward: false);
        Assert.Equal(new TextInputRange(4, 4), result);
    }

    [Fact]
    public void Character_index_returns_the_nearest_caret()
    {
        var client = new FakeClient(10);
        Assert.Equal(2, MacInputLogic.CharacterIndex(client, new Point(25, 5)));   // 2 的右缘与 3 的左缘之间：更近 2
        Assert.Equal(0, MacInputLogic.CharacterIndex(client, new Point(-100, 5)));
        Assert.Equal(10, MacInputLogic.CharacterIndex(client, new Point(1000, 5)));
        Assert.Equal(1, MacInputLogic.CharacterIndex(client, new Point(11, 5)));
    }

    [Fact]
    public void Character_index_of_an_empty_document_is_zero()
    {
        Assert.Equal(0, MacInputLogic.CharacterIndex(new FakeClient(0), new Point(50, 5)));
    }
}
