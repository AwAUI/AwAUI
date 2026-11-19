using AwAUI.Graphics;
using AwAUI.Headless;
using AwAUI.Platform;
using AwAUI.Render;
using AwAUI.Text.Testing;

namespace AwAUI.UI.UnitTests;

/// <summary>
/// 修改进程级 <see cref="Shortcuts.UseMacConventions"/> 或依赖它的测试：与其他测试串行执行。
/// </summary>
[CollectionDefinition(nameof(ShortcutConventionsCollection), DisableParallelization = true)]
public sealed class ShortcutConventionsCollection { }

/// <summary>无头平台上的端到端装配：输入框 + 可脚本化的输入法桥 + 内存剪贴板。测试文本引擎每个字符宽 1em（默认 14px）。</summary>
sealed class TextInputFixture
{
    public readonly HeadlessPlatform Platform = new();
    public readonly UIHost Host;
    public readonly HeadlessWindow Window;
    public readonly List<string> Log = new();

    public TextInputFixture(Func<TextInputFixture, Node> build, string? css = null)
    {
        var sheets = css is null ? Array.Empty<AwAUI.Css.StyleSheet>() : new[] { AwAUI.Css.StyleSheet.Parse(css) };
        Host = AwApp.CreateHost(Platform, new WindowOptions { Size = new Size(400, 200) }, new SoftwareRenderBackend(), new TestTextEngine(),
            new Lambda { Render = () => build(this) }, sheets);
        Window = (HeadlessWindow)Host.Window;
        Frame();
    }

    /// <summary>默认场景：一个输入框（id = in）记录 input / change / submit。</summary>
    public static TextInputFixture Simple(string? type = null, string? css = null) => new(f => new Div
    {
        Children =
        {
            new Input
            {
                Id = "in", InputType = type,
                OnInput = e => f.Log.Add("input:" + e.Value),
                OnChange = e => f.Log.Add("change:" + e.Value),
                OnSubmit = e => f.Log.Add("submit:" + e.Value),
            },
            new Button { Id = "other", Children = { "x" } },
        },
    }, css);

    public HeadlessTextInput Ime => Window.HeadlessTextInput;

    public InputElement Input(string id = "in") => (InputElement)Host.Root.RootElement.ById(id);

    public void Frame() => Platform.RunFrame();

    public void Focus(string id = "in")
    {
        Assert.True(Host.Root.RootElement.ById(id).Focus());
        Frame();
    }

    public void Type(string keys)
    {
        foreach (char c in keys)
        {
            if (c >= 'a' && c <= 'z') Window.TypeKey((PhysicalKey)((int)PhysicalKey.KeyA + (c - 'a')));
            else if (c == ' ') Window.TypeKey(PhysicalKey.Space);
            else throw new ArgumentException("unsupported key " + c);
        }
    }

    public void Key(PhysicalKey key, KeyModifiers mods = KeyModifiers.None) => Window.TypeKey(key, mods);

    public void Click(float x, float y, long timestampMs = 0)
    {
        var p = new Point(x, y);
        var t = TimeSpan.FromMilliseconds(timestampMs);
        Window.SendPointer(new RawPointerEvent(RawPointerEventType.Down, PointerType.Mouse, 1, p, PointerButtons.Left, PointerButtons.Left, KeyModifiers.None, t));
        Window.SendPointer(new RawPointerEvent(RawPointerEventType.Up, PointerType.Mouse, 1, p, PointerButtons.Left, PointerButtons.None, KeyModifiers.None, t));
    }

    /// <summary>输入框内第 <paramref name="index"/> 个字符左缘的窗口 x 坐标（边框 1 + 内边距 8 + 每字符 14）。</summary>
    public float CharX(int index, string id = "in") => Input(id).BoundingRect.X + 1 + 8 + 14 * index;

    public byte[] Pixels() => Window.Framebuffer!.ToRgbaUnpremultiplied();
}

[Collection(nameof(ShortcutConventionsCollection))]
public class TextInputCompositionTests
{
    [Fact]
    public void Cjk_composition_shows_underlined_preedit_and_commit_inserts_it()
    {
        var f = TextInputFixture.Simple();
        var input = f.Input();
        Assert.Null(f.Ime.Client);

        f.Window.TypeKey(PhysicalKey.Tab); // 键盘聚焦
        f.Frame();
        Assert.True(input.IsFocused);
        Assert.NotNull(f.Ime.Client);
        Assert.Same(input, f.Host.Root.Input.TextInputTarget);
        Assert.True(f.Ime.IsKeyboardShown);
        Assert.True(f.Ime.IsImeEnabled);

        // 拼音组合：预编辑文本显示，但不计入值、不派发 input
        f.Ime.Compose("ni");
        f.Ime.Compose("nihao");
        Assert.True(input.IsComposing);
        Assert.Equal("nihao", input.CompositionText);
        Assert.Equal("", input.Value);
        Assert.Equal("nihao", input.DisplayText);
        Assert.Empty(f.Log);
        Assert.False(input.States.HasFlag(PseudoStates.PlaceholderShown));
        var client = f.Ime.Client!;
        Assert.Equal(new TextInputRange(0, 5), client.Composition);
        Assert.Equal(TextInputRange.Caret(5), client.Selection);
        Assert.Equal("nihao", client.GetText(new TextInputRange(0, 100)));

        // 布局之后插入点矩形报告给输入法（候选窗定位）：在第 5 个字符之后
        int before = f.Ime.StateChangedCount;
        f.Frame();
        Assert.True(f.Ime.StateChangedCount > before);
        var caret = f.Ime.LastCaretRect!.Value;
        Assert.Equal(f.CharX(5), caret.X, 0.5f);
        Assert.InRange(caret.Y, input.BoundingRect.Y, input.BoundingRect.Bottom);
        Assert.True(caret.Height > 10);
        var composingPixels = f.Pixels();

        // 输入法选中候选：转换中的文节加粗下划线；提交后成为值的一部分
        f.Ime.Compose("你好", 0, 2);
        Assert.Equal(new TextInputRange(0, 2), client.Selection);
        f.Frame();
        f.Ime.CommitComposition("你好");
        Assert.False(input.IsComposing);
        Assert.Equal("你好", input.Value);
        Assert.Equal(new[] { "input:你好" }, f.Log);
        Assert.Equal(2, input.SelectionStart);
        Assert.Equal(2, input.SelectionEnd);
        Assert.Null(client.Composition);

        // 下划线只属于预编辑：同样的文本组合中与提交后像素不同
        f.Ime.Compose("ab");
        f.Frame();
        var underlined = f.Pixels();
        f.Ime.CommitComposition("ab");
        f.Frame();
        Assert.Equal("你好ab", input.Value);
        Assert.False(underlined.AsSpan().SequenceEqual(f.Pixels()));
        Assert.False(composingPixels.AsSpan().SequenceEqual(underlined));
    }

    [Fact]
    public void Cancelled_composition_leaves_the_value_untouched()
    {
        var f = TextInputFixture.Simple();
        f.Focus();
        f.Type("ab");
        f.Log.Clear();
        f.Ime.Compose("ka");
        f.Frame();
        f.Ime.CancelComposition();
        f.Frame();
        var input = f.Input();
        Assert.False(input.IsComposing);
        Assert.Equal("ab", input.Value);
        Assert.Equal(2, input.SelectionStart);
        Assert.Empty(f.Log);
    }

    [Fact]
    public void Composition_replaces_the_selection_and_commits_at_the_caret()
    {
        var f = TextInputFixture.Simple();
        f.Focus();
        f.Type("ad");
        f.Key(PhysicalKey.ArrowLeft);
        var input = f.Input();
        Assert.Equal(1, input.SelectionStart);
        f.Ime.Compose("bc");
        Assert.Equal("abcd", input.DisplayText);
        Assert.Equal("ad", input.Value);
        Assert.Equal(new TextInputRange(1, 3), f.Ime.Client!.Composition);
        f.Ime.CommitComposition("bc");
        Assert.Equal("abcd", input.Value);
        Assert.Equal(3, input.SelectionStart);

        // 选中 [0, 3) 后开始组合：选中的文本先被删除（派发 input），组合插在原选区起点
        f.Key(PhysicalKey.Home, KeyModifiers.Shift);
        Assert.Equal((0, 3), (input.SelectionStart, input.SelectionEnd));
        f.Log.Clear();
        f.Ime.Compose("x");
        Assert.Equal(new[] { "input:d" }, f.Log);
        Assert.Equal("xd", input.DisplayText);
        f.Ime.CommitComposition("X");
        Assert.Equal("Xd", input.Value);
        Assert.Equal(1, input.SelectionEnd);
    }

    [Fact]
    public void Losing_focus_commits_the_preedit_and_detaches_the_client()
    {
        var f = TextInputFixture.Simple();
        f.Focus();
        f.Ime.Compose("abc");
        int resets = f.Ime.ResetCount;
        f.Focus("other");
        var input = f.Input();
        Assert.False(input.IsComposing);
        Assert.Equal("abc", input.Value);
        Assert.Equal(new[] { "input:abc", "change:abc" }, f.Log);
        Assert.True(f.Ime.ResetCount > resets); // 平台侧的组合被放弃（框架已经提交了它）
        Assert.Null(f.Ime.Client);
        Assert.False(f.Ime.IsKeyboardShown);
        Assert.Throws<InvalidOperationException>(() => f.Ime.Compose("x"));
    }

    [Fact]
    public void Programmatic_value_change_discards_the_composition()
    {
        var state = new State<string>("start");
        TextInputFixture? fx = null;
        fx = new TextInputFixture(f => new Div
        {
            Children =
            {
                new Input { Id = "in", Value = state.Value, OnInput = e => state.Value = e.Value },
            },
        });
        fx.Focus();
        var input = fx.Input();
        Assert.Equal(5, input.SelectionStart); // 聚焦时插入点在末尾
        fx.Ime.Compose("zz");
        int resets = fx.Ime.ResetCount;
        state.Value = "reset";
        fx.Frame();
        Assert.False(input.IsComposing);
        Assert.Equal("reset", input.Value);
        Assert.Equal(5, input.SelectionStart);
        Assert.Equal(resets + 1, fx.Ime.ResetCount);
    }

    [Fact]
    public void Client_replace_range_and_selection_support_autocorrect()
    {
        var f = TextInputFixture.Simple();
        f.Focus();
        f.Type("teh cat");
        var client = f.Ime.Client!;
        Assert.Equal(7, client.Length);
        Assert.Equal("teh", client.GetText(new TextInputRange(0, 3)));
        f.Log.Clear();
        f.Ime.ReplaceRange(new TextInputRange(0, 3), "the");
        var input = f.Input();
        Assert.Equal("the cat", input.Value);
        Assert.Equal(3, input.SelectionStart);
        Assert.Equal(new[] { "input:the cat" }, f.Log);

        client.Selection = new TextInputRange(4, 7);
        Assert.Equal("cat", input.SelectedText);
        client.Selection = new TextInputRange(-5, 1000); // 越界被限制
        Assert.Equal((0, 7), (input.SelectionStart, input.SelectionEnd));

        // 组合中替换组合之前的文本：组合保留并随之移动
        client.Selection = TextInputRange.Caret(7);
        f.Ime.Compose("s");
        f.Ime.ReplaceRange(new TextInputRange(0, 3), "a");
        Assert.True(input.IsComposing);
        Assert.Equal("a cats", input.DisplayText);
        Assert.Equal(new TextInputRange(5, 6), client.Composition);
        f.Ime.CommitComposition("s");
        Assert.Equal("a cats", input.Value);
    }

    [Fact]
    public void Committed_text_is_sanitized_for_a_single_line_input()
    {
        var f = TextInputFixture.Simple();
        f.Focus();
        f.Window.TypeText("a\r\nb\u0000c\td");
        Assert.Equal("abc\td", f.Input().Value);
    }

    [Fact]
    public void Readonly_and_disabled_inputs_ignore_text_input()
    {
        var f = new TextInputFixture(x => new Div { Children = { new Input { Id = "in", ReadOnly = true, Value = "ro" } } });
        f.Focus();
        f.Window.TypeText("x");
        f.Ime.Compose("y");
        Assert.Equal("ro", f.Input().Value);
        Assert.False(f.Input().IsComposing);
        Assert.False(f.Ime.IsKeyboardShown); // 只读框不弹软键盘
    }

    [Fact]
    public void Password_input_disables_the_ime_and_masks_text()
    {
        var f = TextInputFixture.Simple(type: "password");
        f.Focus();
        Assert.False(f.Ime.IsImeEnabled);
        Assert.True(f.Ime.Client!.Options.IsPassword);
        Assert.Throws<InvalidOperationException>(() => f.Ime.Compose("x"));
        f.Type("ab");
        f.Window.TypeText("😀");
        Assert.Equal("ab😀", f.Input().Value);
        Assert.Equal("•••", f.Ime.Client.GetText(new TextInputRange(0, 4)));
        f.Frame();
        // 掩码按码点：插入点在第 3 个圆点之后
        Assert.Equal(f.CharX(3), f.Ime.LastCaretRect!.Value.X, 0.5f);
    }
}

[Collection(nameof(ShortcutConventionsCollection))]
public class TextInputKeyboardTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Clipboard_shortcuts_use_the_platform_primary_modifier(bool mac)
    {
        try
        {
            Shortcuts.UseMacConventions = mac;
            var mod = mac ? KeyModifiers.Meta : KeyModifiers.Control;
            var other = mac ? KeyModifiers.Control : KeyModifiers.Meta;
            var f = TextInputFixture.Simple();
            var clipboard = f.Platform.HeadlessClipboard;
            f.Focus();
            f.Type("hello world");
            var input = f.Input();

            f.Key(PhysicalKey.KeyA, other); // 另一个修饰键不是平台的主快捷键
            Assert.Equal(input.SelectionStart, input.SelectionEnd);
            f.Key(PhysicalKey.KeyA, mod);
            Assert.Equal((0, 11), (input.SelectionStart, input.SelectionEnd));
            Assert.Equal("hello world", input.Value); // 快捷键不产生文本
            f.Key(PhysicalKey.KeyC, mod);
            Assert.Equal("hello world", clipboard.Text);

            f.Key(PhysicalKey.End);
            f.Key(PhysicalKey.ArrowLeft, mac ? KeyModifiers.Alt | KeyModifiers.Shift : KeyModifiers.Control | KeyModifiers.Shift);
            Assert.Equal("world", input.SelectedText);
            f.Log.Clear();
            f.Key(PhysicalKey.KeyX, mod);
            Assert.Equal("world", clipboard.Text);
            Assert.Equal("hello ", input.Value);
            Assert.Equal(new[] { "input:hello " }, f.Log);

            clipboard.Text = "big\r\nworld";
            f.Key(PhysicalKey.KeyV, mod);
            Assert.Equal("hello bigworld", input.Value);
            Assert.Equal(14, input.SelectionStart);

            clipboard.DenyAccess = true;
            f.Key(PhysicalKey.KeyV, mod);
            Assert.Equal("hello bigworld", input.Value);
        }
        finally
        {
            Shortcuts.ResetPlatformConventions();
        }
    }

    [Fact]
    public void Password_text_is_never_copied()
    {
        var f = TextInputFixture.Simple(type: "password");
        f.Platform.HeadlessClipboard.Text = "keep";
        f.Focus();
        f.Type("secret");
        var mod = Shortcuts.PrimaryModifier;
        f.Key(PhysicalKey.KeyA, mod);
        f.Key(PhysicalKey.KeyC, mod);
        f.Key(PhysicalKey.KeyX, mod);
        Assert.Equal("keep", f.Platform.HeadlessClipboard.Text);
        Assert.Equal("secret", f.Input().Value);
    }

    [Fact]
    public void AltGr_on_a_german_layout_types_characters_instead_of_ctrl_alt_shortcuts()
    {
        var gestures = new List<string>();
        var keys = new List<string>();
        var ctrlAltQ = KeyGesture.Parse("Ctrl+Alt+Q");
        var f = new TextInputFixture(x => new Div
        {
            OnKeyDown = e =>
            {
                keys.Add(e.Key);
                if (e.Matches(ctrlAltQ)) gestures.Add("ctrl+alt+q");
            },
            Children = { new Input { Id = "in", OnInput = e => x.Log.Add(e.Value) } },
        });
        f.Window.KeyboardLayout = HeadlessKeyboardLayout.German;
        f.Focus();
        f.Key(PhysicalKey.KeyQ, KeyModifiers.AltGraph);
        f.Key(PhysicalKey.KeyE, KeyModifiers.AltGraph);
        f.Key(PhysicalKey.Digit7, KeyModifiers.AltGraph);
        f.Key(PhysicalKey.IntlBackslash, KeyModifiers.AltGraph | KeyModifiers.Shift); // AltGr 层没有 Shift 变体：仍是 |
        f.Key(PhysicalKey.KeyY); // QWERTZ：物理 Y 键是 z
        Assert.Equal("@€{|z", f.Input().Value);
        Assert.Empty(gestures);
        Assert.Equal(new[] { "@", "€", "{", "|", "z" }, keys);

        // 真正的 Ctrl+Alt+Q 是快捷键：不产生文本
        f.Key(PhysicalKey.KeyQ, KeyModifiers.Control | KeyModifiers.Alt);
        Assert.Equal(new[] { "ctrl+alt+q" }, gestures);
        Assert.Equal("@€{|z", f.Input().Value);

        // 死键：逻辑键为 Dead，不产生文本
        keys.Clear();
        f.Key(PhysicalKey.Equal);
        Assert.Equal(new[] { "Dead" }, keys);
        Assert.Equal("@€{|z", f.Input().Value);
    }

    [Fact]
    public void Letter_shortcuts_match_the_logical_key_and_fall_back_to_the_physical_key()
    {
        var hits = new List<string>();
        var undo = KeyGesture.Parse("Mod+Z");
        var f = new TextInputFixture(x => new Div
        {
            Id = "box", TabIndex = 0,
            OnKeyDown = e => { if (e.Matches(undo)) hits.Add(e.PhysicalKey.ToString()); },
        });
        f.Focus("box");
        var mod = Shortcuts.PrimaryModifier;
        f.Window.KeyboardLayout = HeadlessKeyboardLayout.German;
        f.Key(PhysicalKey.KeyY, mod); // 德语布局上的 Z
        f.Key(PhysicalKey.KeyZ, mod); // 德语布局上的 Y：不是撤销
        Assert.Equal(new[] { "KeyY" }, hits);

        // 平台没有逻辑键（或非拉丁布局）时按物理键
        hits.Clear();
        f.Window.SendKey(new RawKeyEvent(RawKeyEventType.Down, PhysicalKey.KeyZ, mod, false, 0, TimeSpan.Zero, "я"));
        f.Window.SendKey(new RawKeyEvent(RawKeyEventType.Down, PhysicalKey.KeyZ, mod, false, 0, TimeSpan.Zero));
        Assert.Equal(new[] { "KeyZ", "KeyZ" }, hits);
        Assert.Equal(PhysicalKey.KeyZ, Shortcuts.ShortcutKey(PhysicalKey.KeyY, "Z"));
        Assert.Equal(PhysicalKey.Digit1, Shortcuts.ShortcutKey(PhysicalKey.Digit1, "&"));
        Assert.True(Shortcuts.IsPrimaryShortcut(mod | KeyModifiers.Shift));
        Assert.False(Shortcuts.IsPrimaryShortcut(mod | KeyModifiers.AltGraph));
        Assert.False(Shortcuts.IsPrimaryShortcut(mod | KeyModifiers.Alt));
    }

    [Fact]
    public void Numpad_keys_type_digits_report_their_location_and_numpad_enter_submits()
    {
        var events = new List<(string Key, KeyLocation Location)>();
        var f = new TextInputFixture(x => new Div
        {
            OnKeyDown = e => events.Add((e.Key, e.Location)),
            Children = { new Input { Id = "in", OnSubmit = e => x.Log.Add("submit:" + e.Value) } },
        });
        f.Focus();
        f.Key(PhysicalKey.Numpad1);
        f.Key(PhysicalKey.Numpad2);
        f.Key(PhysicalKey.NumpadDecimal);
        f.Key(PhysicalKey.Numpad5);
        f.Key(PhysicalKey.NumpadEnter);
        Assert.Equal("12.5", f.Input().Value);
        Assert.Equal(new[] { "submit:12.5" }, f.Log);
        Assert.Equal(("1", KeyLocation.Numpad), events[0]);
        Assert.Equal(("Enter", KeyLocation.Numpad), events[events.Count - 1]);
        f.Key(PhysicalKey.ShiftRight);
        Assert.Equal(("Shift", KeyLocation.Right), events[events.Count - 1]);
    }

    [Fact]
    public void Editing_keys_work_on_grapheme_clusters_words_and_selection()
    {
        var f = TextInputFixture.Simple();
        f.Focus();
        var input = f.Input();
        f.Window.TypeText("ab👍🏽é");
        Assert.Equal(8, input.SelectionStart);
        f.Key(PhysicalKey.Backspace); // é（e + 组合重音）整体删除
        Assert.Equal("ab👍🏽", input.Value);
        f.Key(PhysicalKey.ArrowLeft); // 跨过带肤色修饰的表情
        Assert.Equal(2, input.SelectionStart);
        f.Key(PhysicalKey.Delete);
        Assert.Equal("ab", input.Value);

        f.Window.TypeText(" foo bar");
        var word = Shortcuts.UseMacConventions ? KeyModifiers.Alt : KeyModifiers.Control;
        f.Key(PhysicalKey.Backspace, word);
        Assert.Equal("ab foo ", input.Value);
        f.Key(PhysicalKey.Home);
        Assert.Equal(0, input.SelectionStart);
        f.Key(PhysicalKey.ArrowRight, word);
        Assert.Equal(2, input.SelectionStart);
        f.Key(PhysicalKey.ArrowRight, word | KeyModifiers.Shift);
        Assert.Equal(" foo", input.SelectedText);
        f.Key(PhysicalKey.ArrowLeft); // 折叠到选区起点
        Assert.Equal((2, 2), (input.SelectionStart, input.SelectionEnd));
        f.Key(PhysicalKey.End, KeyModifiers.Shift);
        Assert.Equal(" foo ", input.SelectedText);
        f.Window.TypeText("!");
        Assert.Equal("ab!", input.Value);
        f.Key(PhysicalKey.Delete); // 末尾：没有可删的
        f.Key(PhysicalKey.ArrowUp);
        Assert.Equal(0, input.SelectionStart);
        f.Key(PhysicalKey.ArrowDown, KeyModifiers.Shift);
        Assert.Equal("ab!", input.SelectedText);
    }

    [Fact]
    public void Typing_moves_the_caret_rect_reported_to_the_ime()
    {
        var f = TextInputFixture.Simple();
        f.Focus();
        var first = f.Ime.LastCaretRect!.Value;
        Assert.Equal(f.CharX(0), first.X, 0.5f);
        f.Type("abc");
        f.Frame();
        Assert.Equal(f.CharX(3), f.Ime.LastCaretRect!.Value.X, 0.5f);
        int n = f.Ime.StateChangedCount;
        f.Frame(); // 没有变化：不重复通知
        Assert.Equal(n, f.Ime.StateChangedCount);
        f.Key(PhysicalKey.ArrowLeft);
        f.Frame();
        Assert.Equal(f.CharX(2), f.Ime.LastCaretRect!.Value.X, 0.5f);
        var bounds = f.Ime.Client!.GetTextBounds(new TextInputRange(0, 2));
        Assert.Equal(f.CharX(0), bounds.X, 0.5f);
        Assert.Equal(28, bounds.Width, 0.5f);
    }

    [Fact]
    public void Long_text_scrolls_horizontally_to_keep_the_caret_visible()
    {
        var f = TextInputFixture.Simple(css: "input { width: 100px; min-width: 0; }");
        f.Focus();
        f.Window.TypeText(new string('m', 30));
        f.Frame();
        var input = f.Input();
        var box = input.BoundingRect;
        Assert.True(input.ScrollX > 0);
        var caret = f.Ime.LastCaretRect!.Value;
        Assert.InRange(caret.X, box.X, box.Right);
        f.Key(PhysicalKey.Home);
        f.Frame();
        Assert.Equal(0, input.ScrollX);
        Assert.Equal(f.CharX(0), f.Ime.LastCaretRect!.Value.X, 0.5f);
    }
}

[Collection(nameof(ShortcutConventionsCollection))]
public class TextInputPointerTests
{
    [Fact]
    public void Click_places_the_caret_double_click_selects_a_word_and_drag_extends()
    {
        var f = TextInputFixture.Simple();
        f.Focus();
        f.Window.TypeText("hello big world");
        f.Frame();
        var input = f.Input();
        float y = input.BoundingRect.Center.Y;

        f.Click(f.CharX(3) + 2, y);
        Assert.Equal((3, 3), (input.SelectionStart, input.SelectionEnd));
        f.Click(f.CharX(5) + 9, y); // 越过字符中点：插入点落在后面
        Assert.Equal(6, input.SelectionStart);

        f.Click(f.CharX(7), y, 1000);
        f.Click(f.CharX(7), y, 1200);
        Assert.Equal("big", input.SelectedText);
        f.Click(f.CharX(7), y, 1300);
        Assert.Equal("hello big world", input.SelectedText);
        f.Click(f.CharX(7), y, 5000); // 间隔太长：单击
        Assert.Equal(7, input.SelectionStart);
        Assert.Equal(7, input.SelectionEnd);

        // 拖动选择（指针被输入框捕获，拖出边界也能继续）
        var down = new Point(f.CharX(1), y);
        f.Window.SendPointer(new RawPointerEvent(RawPointerEventType.Down, PointerType.Mouse, 1, down, PointerButtons.Left, PointerButtons.Left, KeyModifiers.None, TimeSpan.Zero));
        f.Window.SendPointer(new RawPointerEvent(RawPointerEventType.Move, PointerType.Mouse, 1, new Point(f.CharX(4), y + 40), PointerButtons.None, PointerButtons.Left, KeyModifiers.None, TimeSpan.Zero));
        Assert.Equal("ell", input.SelectedText);
        f.Window.SendPointer(new RawPointerEvent(RawPointerEventType.Up, PointerType.Mouse, 1, new Point(f.CharX(4), y), PointerButtons.Left, PointerButtons.None, KeyModifiers.None, TimeSpan.Zero));
        f.Window.SendPointer(new RawPointerEvent(RawPointerEventType.Move, PointerType.Mouse, 1, new Point(f.CharX(9), y), PointerButtons.None, PointerButtons.None, KeyModifiers.None, TimeSpan.Zero));
        Assert.Equal("ell", input.SelectedText);
    }

    [Fact]
    public void Click_during_composition_commits_the_preedit_first()
    {
        var f = TextInputFixture.Simple();
        f.Focus();
        f.Window.TypeText("ab");
        f.Ime.Compose("xy");
        f.Frame();
        var input = f.Input();
        f.Click(f.CharX(1), input.BoundingRect.Center.Y);
        Assert.False(input.IsComposing);
        Assert.Equal("abxy", input.Value);
        Assert.Equal(1, input.SelectionStart);
    }
}

public class TextEditingTests
{
    [Theory]
    [InlineData("abc", 0, 1)]
    [InlineData("éx", 0, 2)]
    [InlineData("👍🏽x", 0, 4)]
    [InlineData("👨‍👩‍👧x", 0, 8)]
    [InlineData("🇩🇪🇫🇷", 0, 4)]
    [InlineData("🇩🇪🇫🇷", 4, 8)]
    [InlineData("a\r\nb", 1, 3)]
    [InlineData("abc", 3, 3)]
    public void Next_grapheme(string s, int from, int expected) => Assert.Equal(expected, TextEditing.NextGrapheme(s, from));

    [Theory]
    [InlineData("abc", 3, 2)]
    [InlineData("xé", 3, 1)]
    [InlineData("x👍🏽", 5, 1)]
    [InlineData("x👨‍👩‍👧", 9, 1)]
    [InlineData("🇩🇪🇫🇷", 8, 4)]
    [InlineData("🇩🇪🇫🇷", 4, 0)]
    [InlineData("a\r\nb", 3, 1)]
    [InlineData("abc", 0, 0)]
    public void Previous_grapheme(string s, int from, int expected) => Assert.Equal(expected, TextEditing.PreviousGrapheme(s, from));

    [Theory]
    [InlineData("hello  world", 0, 5)]
    [InlineData("hello  world", 5, 12)]
    [InlineData("a.b", 0, 1)]
    [InlineData("你好世界", 0, 1)]
    public void Next_word_end(string s, int from, int expected) => Assert.Equal(expected, TextEditing.NextWordEnd(s, from));

    [Theory]
    [InlineData("hello  world", 12, 7)]
    [InlineData("hello  world", 7, 0)]
    [InlineData("foo...", 6, 3)]
    public void Previous_word_start(string s, int from, int expected) => Assert.Equal(expected, TextEditing.PreviousWordStart(s, from));

    [Fact]
    public void Word_at_and_masks()
    {
        Assert.Equal((6, 9), TextEditing.WordAt("hello big world", 7));
        Assert.Equal((5, 6), TextEditing.WordAt("hello big world", 5));
        Assert.Equal("••", TextEditing.Mask("a😀"));
        Assert.Equal(2, TextEditing.ToDisplay("a😀b", 3, password: true));
        Assert.Equal(3, TextEditing.FromDisplay("a😀b", 2, password: true));
        Assert.Equal(1, TextEditing.Snap("😀", 1) + 1); // 代理对中间对齐到前面
        Assert.Equal("ab\tc", TextEditing.SanitizeSingleLine("a\nb\tc\u007F\uD800"));
    }

    [Fact]
    public void Previous_grapheme_is_bounded_on_pathological_input()
    {
        // 上万个组合记号：一次退格的回溯步数有上限
        var s = "a" + new string('́', 20000);
        int p = TextEditing.PreviousGrapheme(s, s.Length);
        Assert.True(p > 0 && p < s.Length);
        Assert.True(s.Length - p <= 66);
    }
}
