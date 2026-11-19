using AwAUI.Headless;
using AwAUI.Platform;

namespace AwAUI.UI.UnitTests;

/// <summary>第七批 B7Input 评审发现的问题的回归测试（输入框作为文本输入客户端、剪贴板失败路径）。</summary>
[Collection(nameof(ShortcutConventionsCollection))]
public class TextInputReviewTests
{
    /// <summary>可控的剪贴板：读写的结果由测试决定（异步完成、失败、同步抛异常）。</summary>
    sealed class ScriptedClipboard : Clipboard
    {
        public TaskCompletionSource<string?>? PendingRead;
        public Func<string, Task<bool>>? Write;
        public string? Written;

        public override Task<string?> GetTextAsync()
        {
            PendingRead = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            return PendingRead.Task;
        }

        public override Task<bool> SetTextAsync(string text)
        {
            Written = text;
            return Write?.Invoke(text) ?? Task.FromResult(true);
        }
    }

    static TextInputFixture Typed(string text, out InputElement input, out ScriptedClipboard clipboard)
    {
        var f = TextInputFixture.Simple();
        f.Focus();
        f.Window.TypeText(text);
        input = f.Input();
        clipboard = new ScriptedClipboard();
        f.Host.Root.Input.Clipboard = clipboard;
        return f;
    }

    [Fact]
    public void Cut_keeps_the_text_when_the_clipboard_rejects_the_write()
    {
        var f = Typed("hello", out var input, out var clipboard);
        clipboard.Write = _ => Task.FromResult(false);
        var mod = Shortcuts.PrimaryModifier;
        f.Key(PhysicalKey.KeyA, mod);
        f.Log.Clear();
        f.Key(PhysicalKey.KeyX, mod);
        Assert.Equal("hello", clipboard.Written);
        Assert.Equal("hello", input.Value); // 写入失败：剪切不删除（否则文本丢失）
        Assert.Empty(f.Log);

        // 成功（同步或异步完成）时照常删除
        clipboard.Write = _ => Task.FromResult(true);
        f.Key(PhysicalKey.KeyX, mod);
        Assert.Equal("", input.Value);
    }

    [Fact]
    public void Clipboard_exceptions_do_not_escape_the_key_dispatch()
    {
        var f = Typed("hello", out var input, out var clipboard);
        clipboard.Write = _ => throw new InvalidOperationException("clipboard broken");
        var mod = Shortcuts.PrimaryModifier;
        f.Key(PhysicalKey.KeyA, mod);
        f.Key(PhysicalKey.KeyC, mod); // 不抛出
        f.Key(PhysicalKey.KeyX, mod);
        Assert.Equal("hello", input.Value);
        clipboard.Write = _ => Task.FromException<bool>(new InvalidOperationException("already failed"));
        f.Key(PhysicalKey.KeyX, mod); // 已经失败的任务：不删除
        Assert.Equal("hello", input.Value);
        var pending = new TaskCompletionSource<bool>();
        clipboard.Write = _ => pending.Task;
        f.Key(PhysicalKey.KeyX, mod); // 尚未完成：无法等待，照常删除（与浏览器的剪切一致）
        Assert.Equal("", input.Value);
        pending.SetException(new InvalidOperationException("async failure")); // 异常被观察，不成为未观察的任务异常
    }

    [Fact]
    public void Asynchronous_paste_lands_in_the_same_input_on_the_ui_thread()
    {
        var f = Typed("ab", out var input, out var clipboard);
        var mod = Shortcuts.PrimaryModifier;
        f.Key(PhysicalKey.KeyV, mod);
        Assert.NotNull(clipboard.PendingRead);
        Assert.Equal("ab", input.Value);
        clipboard.PendingRead!.SetResult("XY");
        // 结果在线程池上完成，投递到 UI 线程：等它到达分派器
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (input.Value == "ab" && DateTime.UtcNow < deadline)
        {
            f.Frame();
            Thread.Sleep(1);
        }
        Assert.Equal("abXY", input.Value);

        // 读取期间焦点离开：结果被丢弃
        f.Key(PhysicalKey.KeyV, mod);
        f.Focus("other");
        clipboard.PendingRead!.SetResult("ZZ");
        for (int i = 0; i < 50; i++)
        {
            f.Frame();
            Thread.Sleep(1);
        }
        Assert.Equal("abXY", input.Value);
    }

    [Fact]
    public void Replace_range_around_the_composition_keeps_document_offsets_consistent()
    {
        var f = TextInputFixture.Simple();
        f.Focus();
        f.Window.TypeText("abcd");
        var input = f.Input();
        var client = f.Ime.Client!;
        client.Selection = TextInputRange.Caret(2);
        f.Ime.Compose("XY");
        Assert.Equal("abXYcd", input.DisplayText);

        // 组合之后的范围（文档下标）：换算到值下标
        f.Ime.ReplaceRange(new TextInputRange(5, 6), "D");
        Assert.Equal("abXYcD", input.DisplayText);
        Assert.Equal("abcD", input.Value);
        Assert.Equal(new TextInputRange(2, 4), client.Composition);

        // 跨越组合的范围：先按原样提交预编辑文本，再替换
        f.Ime.ReplaceRange(new TextInputRange(1, 3), "-");
        Assert.False(input.IsComposing);
        Assert.Equal("a-YcD", input.Value);
        Assert.Equal(2, input.SelectionStart);
    }

    [Fact]
    public void Removing_a_composing_input_discards_its_preedit()
    {
        var show = new State<bool>(true);
        var f = new TextInputFixture(x => new Div
        {
            Children = { show.Value ? new Input { Id = "in" } : new Span { Children = { "gone" } } },
        });
        f.Focus();
        var input = f.Input();
        f.Ime.Compose("ka");
        Assert.True(input.IsComposing);
        show.Value = false;
        f.Frame();
        Assert.Null(f.Ime.Client);
        Assert.False(f.Ime.IsComposing);
        Assert.False(input.IsComposing); // 元素不再显示过期的预编辑文本
        Assert.Equal("", input.Value);
    }
    [Fact]
    public void Numpad_navigation_keys_with_numlock_off_edit_like_their_named_keys()
    {
        // NumLock 关闭：物理键仍是 Numpad4 等，逻辑键是 ArrowLeft 等（W3C code / key；Win32 的导航虚拟键、X11 的 KP_Left、浏览器都如此）
        var f = TextInputFixture.Simple();
        f.Focus();
        f.Window.TypeText("abcd");
        var input = f.Input();
        void Pad(PhysicalKey key, string logical, KeyModifiers mods = KeyModifiers.None)
        {
            f.Window.SendKey(new RawKeyEvent(RawKeyEventType.Down, key, mods, false, 0, TimeSpan.Zero, logical));
            f.Window.SendKey(new RawKeyEvent(RawKeyEventType.Up, key, mods, false, 0, TimeSpan.Zero, logical));
        }
        Pad(PhysicalKey.Numpad4, "ArrowLeft");
        Assert.Equal(3, input.SelectionStart);
        Pad(PhysicalKey.Numpad7, "Home");
        Assert.Equal(0, input.SelectionStart);
        Pad(PhysicalKey.Numpad1, "End", KeyModifiers.Shift);
        Assert.Equal((0, 4), (input.SelectionStart, input.SelectionEnd));
        Pad(PhysicalKey.Numpad6, "ArrowRight");
        Assert.Equal((4, 4), (input.SelectionStart, input.SelectionEnd));
        Pad(PhysicalKey.Numpad7, "Home");
        Pad(PhysicalKey.NumpadDecimal, "Delete");
        Assert.Equal("bcd", input.Value);
        // NumLock 打开：数字键不是编辑键
        Pad(PhysicalKey.Numpad4, "4");
        Assert.Equal(0, input.SelectionStart);
        Assert.Equal("bcd", input.Value);
    }
}
