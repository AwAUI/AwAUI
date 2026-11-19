using AwAUI.Platform;
using AwAUI.Win32;

namespace AwAUI.Platform.UnitTests;

/// <summary>Win32 后端的纯逻辑（Win32 本身只能在 Windows 上运行；这里在任何系统上验证翻译表与状态机）。</summary>
public class Win32InputTests
{
    [Theory]
    [InlineData(0x52, PhysicalKey.Numpad0)]
    [InlineData(0x4F, PhysicalKey.Numpad1)]
    [InlineData(0x47, PhysicalKey.Numpad7)]
    [InlineData(0x53, PhysicalKey.NumpadDecimal)]
    [InlineData(0x37, PhysicalKey.NumpadMultiply)]
    [InlineData(0x4A, PhysicalKey.NumpadSubtract)]
    [InlineData(0x4E, PhysicalKey.NumpadAdd)]
    [InlineData(0xE035, PhysicalKey.NumpadDivide)]
    [InlineData(0xE01C, PhysicalKey.NumpadEnter)]
    [InlineData(0x1C, PhysicalKey.Enter)]
    [InlineData(0xE045, PhysicalKey.NumLock)]
    [InlineData(0x45, PhysicalKey.Pause)]
    [InlineData(0x56, PhysicalKey.IntlBackslash)]
    [InlineData(0xE038, PhysicalKey.AltRight)]
    [InlineData(0x64, PhysicalKey.F13)]
    [InlineData(0x76, PhysicalKey.F24)]
    [InlineData(0x7F, PhysicalKey.Unknown)]
    public void Scan_codes(int scanCode, PhysicalKey expected) => Assert.Equal(expected, Win32ScanCodes.Get(scanCode));

    [Fact]
    public void Every_physical_key_except_meta_aliases_has_a_scan_code()
    {
        var mapped = new HashSet<PhysicalKey>(Win32ScanCodes.Entries.Select(e => e.Value));
        foreach (PhysicalKey key in Enum.GetValues(typeof(PhysicalKey)))
        {
            if (key is PhysicalKey.Unknown) continue;
            Assert.True(mapped.Contains(key), key.ToString());
        }
    }

    [Theory]
    [InlineData(Win32Keys.VK_HOME, PhysicalKey.Numpad7, "Home")] // NumLock 关闭：物理键仍是 Numpad7
    [InlineData(0x67, PhysicalKey.Numpad7, null)]                 // VK_NUMPAD7：按布局求字符
    [InlineData(Win32Keys.VK_RETURN, PhysicalKey.NumpadEnter, "Enter")]
    [InlineData(Win32Keys.VK_LEFT, PhysicalKey.ArrowLeft, "ArrowLeft")]
    [InlineData(0x7C, PhysicalKey.F13, "F13")]
    [InlineData(Win32Keys.VK_PROCESSKEY, PhysicalKey.KeyA, "Process")]
    [InlineData(0x41, PhysicalKey.KeyA, null)]
    [InlineData(Win32Keys.VK_CLEAR, PhysicalKey.Numpad5, "Clear")]
    public void Named_keys(int vk, PhysicalKey physical, string? expected) => Assert.Equal(expected, Win32Keys.NamedKey(vk, physical));

    [Fact]
    public void AltGr_is_ctrl_alt_that_produces_text()
    {
        // 德语 AltGr+Q：Windows 报告左 Ctrl + 右 Alt，且该状态下产生 @
        Assert.Equal(KeyModifiers.AltGraph, Win32Keys.ResolveModifiers(false, true, true, false, producesTextWithCtrlAlt: true));
        Assert.Equal(KeyModifiers.AltGraph | KeyModifiers.Shift, Win32Keys.ResolveModifiers(true, true, true, false, true));
        // US 布局上的 Ctrl+Alt+Q 不产生字符：是快捷键
        Assert.Equal(KeyModifiers.Control | KeyModifiers.Alt, Win32Keys.ResolveModifiers(false, true, true, false, false));
        Assert.Equal(KeyModifiers.Control, Win32Keys.ResolveModifiers(false, true, false, false, true));
        Assert.Equal(KeyModifiers.Meta | KeyModifiers.Alt, Win32Keys.ResolveModifiers(false, false, true, true, false));
    }

    [Fact]
    public void ToUnicode_results()
    {
        Assert.Equal(KeyValues.Dead, Win32Keys.FromToUnicode(-1, new char[8]));
        Assert.Null(Win32Keys.FromToUnicode(0, new char[8]));
        Assert.Equal("@", Win32Keys.FromToUnicode(1, new[] { '@', '\0' }));
        Assert.Null(Win32Keys.FromToUnicode(1, new[] { '\u0003' })); // Ctrl+C 的控制字符
        Assert.Equal("😀", Win32Keys.FromToUnicode(2, "😀".ToCharArray()));
        Assert.Equal("a", Win32Keys.FromToUnicode(99, new[] { 'a' })); // 结果不超过缓冲
    }

    [Fact]
    public void Wm_char_surrogates_are_assembled_and_controls_dropped()
    {
        var a = new Utf16Assembler();
        Assert.Null(a.Push('\uD83D'));
        Assert.Equal("😀", a.Push('\uDE00'));
        Assert.Null(a.Push('\uDE00'));        // 孤立的低代理
        Assert.Null(a.Push('\uD83D'));
        Assert.Equal("x", a.Push('x'));       // 高代理后跟 BMP 字符：丢弃高代理
        Assert.Null(a.Push('\uDE00'));
        Assert.Null(a.Push('\b'));
        Assert.Null(a.Push('\r'));
        Assert.Null(a.Push('\u0001'));
        Assert.Equal("é", a.Push('é'));
        Assert.Null(a.Push('\uD83D'));
        a.Reset();
        Assert.Null(a.Push('\uDE00'));
    }

    [Fact]
    public void Composition_selection_is_the_target_clause()
    {
        // 「にほんご」转换中：第二个文节（2..4）是目标
        var attrs = new byte[] { ImeComposition.ATTR_CONVERTED, ImeComposition.ATTR_CONVERTED, ImeComposition.ATTR_TARGET_CONVERTED, ImeComposition.ATTR_TARGET_CONVERTED };
        Assert.Equal(new TextInputRange(2, 4), ImeComposition.Selection(attrs, 4, 0, 4));
        Assert.Equal(new TextInputRange(0, 1), ImeComposition.Selection(new byte[] { 3, 0, 3 }, 3, 3, 3)); // 只取第一段
        Assert.Equal(TextInputRange.Caret(2), ImeComposition.Selection(new byte[] { 0, 0, 0 }, 3, 2, 3));
        Assert.Equal(TextInputRange.Caret(3), ImeComposition.Selection(null, 0, -1, 3));
        Assert.Equal(TextInputRange.Caret(3), ImeComposition.Selection(null, 0, 99, 3));
        Assert.Equal(TextInputRange.Caret(2), ImeComposition.Selection(new byte[] { 1, 1, 1, 1 }, 0, 2, 4)); // 属性数为 0
        Assert.Equal(new TextInputRange(0, 2), ImeComposition.Selection(new byte[] { 1, 1, 1, 1 }, 4, 0, 2)); // 不超过字符串长度
        Assert.Equal(0, ImeComposition.CharCount(-1));

        // WM_IME_SETCONTEXT：去掉 ISC_SHOWUICOMPOSITIONWINDOW。32 位进程上 ISC_SHOWUIALL（0xC000000F）的 lParam 是负数（符号扩展），
        // 结果必须能放进 32 位（评审发现：原来对 64 位值清位得到 -3221225457，32 位进程上转 IntPtr 抛 OverflowException）
        long signExtended = unchecked((int)0xC000000F);
        Assert.Equal(0x4000000FL, ImeComposition.WithoutCompositionWindow(signExtended));
        Assert.Equal(0x4000000FL, ImeComposition.WithoutCompositionWindow(0xC000000FL)); // 64 位进程：零扩展
        Assert.InRange(ImeComposition.WithoutCompositionWindow(-1), 0, int.MaxValue);
        Assert.Equal(0x0000000FL, ImeComposition.WithoutCompositionWindow(0x0000000F));
        Assert.Equal(2, ImeComposition.CharCount(5));
        Assert.Equal(ImeComposition.MaxLength, ImeComposition.CharCount(int.MaxValue));
    }

    [Fact]
    public unsafe void Clipboard_text_is_bounded_and_normalized()
    {
        fixed (char* p = "a\r\nb\0garbage")
            Assert.Equal("a\nb", Win32ClipboardText.Decode(p, 13));
        fixed (char* p = "abcdef")
            Assert.Equal("abc", Win32ClipboardText.Decode(p, 3)); // 没有 NUL：以 GlobalSize 为界
        Assert.Equal("", Win32ClipboardText.Decode(null, 10));
        Assert.Equal("a\r\nb\r\nc", Win32ClipboardText.ToCrLf("a\nb\r\nc"));
        Assert.Equal("x", Win32ClipboardText.ToCrLf("x"));
    }
}
