using AwAUI.Platform;

namespace AwAUI.UI.UnitTests;

/// <summary>输入框的撤销 / 重做：编辑组合并、光标移动分组、程序改写清空历史。</summary>
[Collection(nameof(ShortcutConventionsCollection))]
public class TextUndoRedoTests
{
    static KeyModifiers Primary => Shortcuts.PrimaryModifier;

    [Fact]
    public void A_typing_run_undoes_and_redoes_as_one_group()
    {
        var f = TextInputFixture.Simple();
        f.Focus();
        f.Type("abc");
        Assert.Equal("abc", f.Input().Value);

        f.Key(PhysicalKey.KeyZ, Primary);
        Assert.Equal("", f.Input().Value);
        Assert.Equal(0, f.Input().SelectionFocus);

        f.Key(PhysicalKey.KeyZ, Primary | KeyModifiers.Shift);
        Assert.Equal("abc", f.Input().Value);
        Assert.Equal(3, f.Input().SelectionFocus);
    }

    [Fact]
    public void A_deletion_run_merges_into_one_group()
    {
        var f = TextInputFixture.Simple();
        f.Focus();
        f.Type("abc");
        f.Key(PhysicalKey.Backspace);
        f.Key(PhysicalKey.Backspace);
        Assert.Equal("a", f.Input().Value);

        f.Key(PhysicalKey.KeyZ, Primary);
        Assert.Equal("abc", f.Input().Value);
        Assert.Equal(3, f.Input().SelectionFocus);
    }

    [Fact]
    public void Moving_the_caret_starts_a_new_group()
    {
        var f = TextInputFixture.Simple();
        f.Focus();
        f.Type("abc");
        f.Key(PhysicalKey.ArrowLeft);
        f.Type("x");
        Assert.Equal("abxc", f.Input().Value);

        f.Key(PhysicalKey.KeyZ, Primary);
        Assert.Equal("abc", f.Input().Value);
        f.Key(PhysicalKey.KeyZ, Primary);
        Assert.Equal("", f.Input().Value);
    }

    [Fact]
    public void Replacing_a_selection_is_its_own_group()
    {
        var f = TextInputFixture.Simple();
        f.Focus();
        f.Type("abc");
        f.Key(PhysicalKey.KeyA, Primary);
        f.Type("x");
        Assert.Equal("x", f.Input().Value);

        f.Key(PhysicalKey.KeyZ, Primary);
        Assert.Equal("abc", f.Input().Value);
    }

    [Fact]
    public void A_new_edit_after_undo_clears_the_redo_stack()
    {
        var f = TextInputFixture.Simple();
        f.Focus();
        f.Type("abc");
        f.Key(PhysicalKey.KeyZ, Primary);
        Assert.Equal("", f.Input().Value);

        f.Type("d");
        Assert.Equal("d", f.Input().Value);
        f.Key(PhysicalKey.KeyZ, Primary | KeyModifiers.Shift);
        Assert.Equal("d", f.Input().Value);
    }

    [Fact]
    public void Undo_and_redo_dispatch_input_events()
    {
        var f = TextInputFixture.Simple();
        f.Focus();
        f.Type("ab");
        f.Log.Clear();

        f.Key(PhysicalKey.KeyZ, Primary);
        Assert.Equal(new[] { "input:" }, f.Log);
        f.Log.Clear();

        f.Key(PhysicalKey.KeyZ, Primary | KeyModifiers.Shift);
        Assert.Equal(new[] { "input:ab" }, f.Log);
    }

    [Fact]
    public void Programmatic_value_changes_reset_the_history()
    {
        var f = TextInputFixture.Simple();
        f.Focus();
        f.Type("abc");
        f.Input().SetValue("z");

        f.Key(PhysicalKey.KeyZ, Primary);
        Assert.Equal("z", f.Input().Value);
        Assert.False(f.Input().History.CanUndo);
    }

    [Fact]
    public void Undo_is_ignored_in_a_readonly_input()
    {
        var f = new TextInputFixture(_ => new Div
        {
            Children = { new Input { Id = "in", Value = "fixed", ReadOnly = true } },
        });
        f.Focus();
        f.Key(PhysicalKey.KeyZ, Primary);
        Assert.Equal("fixed", f.Input().Value);
    }

    [Fact]
    public void Multiline_typing_and_enter_undo_together()
    {
        var f = new TextInputFixture(_ => new Div
        {
            Children = { new TextArea { Id = "in" } },
        });
        f.Focus();
        f.Type("ab");
        f.Window.TypeKey(PhysicalKey.Enter);
        f.Type("c");
        Assert.Equal("ab\nc", f.Input().Value);

        f.Key(PhysicalKey.KeyZ, Primary);
        Assert.Equal("ab\n", f.Input().Value);
    }

    [Fact]
    public void Ime_commit_is_a_single_undo_group()
    {
        var f = TextInputFixture.Simple();
        f.Focus();
        f.Ime.Compose("nihao");
        f.Ime.CommitComposition("你好");
        Assert.Equal("你好", f.Input().Value);

        f.Key(PhysicalKey.KeyZ, Primary);
        Assert.Equal("", f.Input().Value);
    }
}
