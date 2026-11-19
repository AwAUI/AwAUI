using AwAUI.Browser;
using AwAUI.Platform;

namespace AwAUI.Browser.Tests;

public class KeyboardTests
{
    [Fact]
    public void EveryPhysicalKeyHasItsW3CCode()
    {
        // PhysicalKey 的成员名就是 KeyboardEvent.code：每个成员都必须能由同名的码查到
        foreach (PhysicalKey key in Enum.GetValues(typeof(PhysicalKey)))
        {
            if (key == PhysicalKey.Unknown) continue;
            Assert.Equal(key, DomKeyMap.ToPhysicalKey(key.ToString()));
        }
    }

    [Fact]
    public void TableEntriesAreCanonicalOrKnownAliases()
    {
        var aliases = new HashSet<string> { "OSLeft", "OSRight" };
        foreach (var pair in DomKeyMap.Entries)
            Assert.True(pair.Key == pair.Value.ToString() || aliases.Contains(pair.Key), pair.Key);
    }

    [Theory]
    [InlineData("OSLeft", PhysicalKey.MetaLeft)]
    [InlineData("OSRight", PhysicalKey.MetaRight)]
    [InlineData("NumpadEnter", PhysicalKey.NumpadEnter)]
    [InlineData("Numpad1", PhysicalKey.Numpad1)]
    [InlineData("IntlBackslash", PhysicalKey.IntlBackslash)]
    [InlineData("Lang1", PhysicalKey.Unknown)]
    [InlineData("keya", PhysicalKey.Unknown)] // 区分大小写
    [InlineData("", PhysicalKey.Unknown)]
    [InlineData(null, PhysicalKey.Unknown)]
    [InlineData("Unidentified", PhysicalKey.Unknown)]
    public void AliasesAndUnknownCodes(string? code, PhysicalKey expected) => Assert.Equal(expected, DomKeyMap.ToPhysicalKey(code));

    [Fact]
    public void OverlongCodeIsUnknownWithoutLookup() =>
        Assert.Equal(PhysicalKey.Unknown, DomKeyMap.ToPhysicalKey(new string('K', 100_000)));

    [Theory]
    [InlineData(0, KeyModifiers.None)]
    [InlineData(1, KeyModifiers.Shift)]
    [InlineData(2, KeyModifiers.Control)]
    [InlineData(4, KeyModifiers.Alt)]
    [InlineData(8, KeyModifiers.Meta)]
    [InlineData(15, KeyModifiers.Shift | KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)]
    [InlineData(-1, KeyModifiers.Shift | KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)]
    [InlineData(16, KeyModifiers.None)]
    public void Modifiers(int mask, KeyModifiers expected) => Assert.Equal(expected, DomKeyMap.ToModifiers(mask));

    [Fact]
    public void ImeKeysAreNotForwardedNorPrevented()
    {
        Assert.False(KeyboardPolicy.ShouldForward(isComposing: true, keyCode: 13));
        Assert.False(KeyboardPolicy.ShouldForward(isComposing: false, keyCode: 229));
        Assert.True(KeyboardPolicy.ShouldForward(isComposing: false, keyCode: 13));
        Assert.False(KeyboardPolicy.ShouldPreventDefault(PhysicalKey.Enter, KeyModifiers.None, true, 13));
        Assert.False(KeyboardPolicy.ShouldPreventDefault(PhysicalKey.ArrowDown, KeyModifiers.None, false, 229));
    }

    [Theory]
    // 导航与编辑键：阻止（焦点移出、页面滚动、浏览器后退……）
    [InlineData(PhysicalKey.Tab, KeyModifiers.None, true)]
    [InlineData(PhysicalKey.Tab, KeyModifiers.Shift, true)]
    [InlineData(PhysicalKey.ArrowUp, KeyModifiers.None, true)]
    [InlineData(PhysicalKey.PageDown, KeyModifiers.None, true)]
    [InlineData(PhysicalKey.Backspace, KeyModifiers.None, true)]
    [InlineData(PhysicalKey.Enter, KeyModifiers.None, true)]
    [InlineData(PhysicalKey.NumpadEnter, KeyModifiers.None, true)]
    [InlineData(PhysicalKey.Escape, KeyModifiers.None, true)]
    [InlineData(PhysicalKey.Numpad5, KeyModifiers.None, false)]
    // 产生文本的按键：放行，否则浏览器不派发 beforeinput / 输入法事件
    [InlineData(PhysicalKey.KeyA, KeyModifiers.None, false)]
    [InlineData(PhysicalKey.KeyA, KeyModifiers.Shift, false)]
    [InlineData(PhysicalKey.Space, KeyModifiers.None, false)]
    [InlineData(PhysicalKey.Digit1, KeyModifiers.Alt, false)] // AltGr / Option 组合出的字符
    [InlineData(PhysicalKey.Unknown, KeyModifiers.None, false)]
    // 剪贴板、刷新、开发者工具：放行
    [InlineData(PhysicalKey.KeyC, KeyModifiers.Control, false)]
    [InlineData(PhysicalKey.KeyV, KeyModifiers.Meta, false)]
    [InlineData(PhysicalKey.KeyX, KeyModifiers.Control, false)]
    [InlineData(PhysicalKey.KeyR, KeyModifiers.Control, false)]
    [InlineData(PhysicalKey.F5, KeyModifiers.None, false)]
    [InlineData(PhysicalKey.F12, KeyModifiers.None, false)]
    [InlineData(PhysicalKey.KeyI, KeyModifiers.Control | KeyModifiers.Shift, false)]
    [InlineData(PhysicalKey.KeyI, KeyModifiers.Meta | KeyModifiers.Alt, false)]
    // 其余命令组合键归框架
    [InlineData(PhysicalKey.KeyS, KeyModifiers.Control, true)]
    [InlineData(PhysicalKey.KeyZ, KeyModifiers.Meta, true)]
    [InlineData(PhysicalKey.KeyI, KeyModifiers.Control, true)]
    [InlineData(PhysicalKey.F1, KeyModifiers.None, false)]
    [InlineData(PhysicalKey.F1, KeyModifiers.Control, true)]
    public void PreventDefaultPolicy(PhysicalKey key, KeyModifiers mods, bool expected) =>
        Assert.Equal(expected, KeyboardPolicy.ShouldPreventDefault(key, mods, false, 0));

    [Theory]
    // Windows 上浏览器把 AltGr 报告为 Ctrl+Alt（德语 AltGr+Q = @、AltGr+E = €、AltGr+7 = {、AltGr+Minus = \\）：必须放行，
    // 否则 keydown 被阻止、beforeinput 不派发，字符无法输入（审查发现的问题）
    [InlineData(PhysicalKey.KeyQ, KeyModifiers.Control | KeyModifiers.Alt, "@", false, false)]
    [InlineData(PhysicalKey.KeyE, KeyModifiers.Control | KeyModifiers.Alt, "€", false, false)]
    [InlineData(PhysicalKey.Digit7, KeyModifiers.Control | KeyModifiers.Alt, "{", false, false)]
    [InlineData(PhysicalKey.Minus, KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift, "\\", false, false)]
    [InlineData(PhysicalKey.KeyQ, KeyModifiers.Control | KeyModifiers.Alt, "\U0001F600", false, false)] // 代理对
    [InlineData(PhysicalKey.Equal, KeyModifiers.Control | KeyModifiers.Alt, "Dead", false, false)] // AltGr 死键
    [InlineData(PhysicalKey.KeyQ, KeyModifiers.Control, "@", true, false)] // getModifierState("AltGraph") 也算
    // 不是 AltGr 字符：照旧阻止
    [InlineData(PhysicalKey.KeyQ, KeyModifiers.Control, "q", false, true)] // 普通 Ctrl+Q
    [InlineData(PhysicalKey.KeyS, KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta, "s", false, true)]
    [InlineData(PhysicalKey.ArrowLeft, KeyModifiers.Control | KeyModifiers.Alt, "ArrowLeft", false, true)]
    [InlineData(PhysicalKey.F1, KeyModifiers.Control | KeyModifiers.Alt, "F1", false, true)]
    [InlineData(PhysicalKey.Backspace, KeyModifiers.None, "Backspace", true, true)]
    [InlineData(PhysicalKey.KeyQ, KeyModifiers.Control | KeyModifiers.Alt, null, false, true)]
    [InlineData(PhysicalKey.KeyQ, KeyModifiers.Control | KeyModifiers.Alt, "\u0007", false, true)] // 控制字符不算可打印
    // US 布局上的 Ctrl+Alt+字母 / 数字：浏览器报告去掉修饰键的键值，没有 AltGraph 状态时是快捷键（评审发现）
    [InlineData(PhysicalKey.KeyA, KeyModifiers.Control | KeyModifiers.Alt, "a", false, true)]
    [InlineData(PhysicalKey.Digit1, KeyModifiers.Control | KeyModifiers.Alt, "1", false, true)]
    [InlineData(PhysicalKey.KeyQ, KeyModifiers.Control | KeyModifiers.Alt, "q", true, false)] // 有 AltGraph 状态：仍按 AltGr 文本
    public void AltGraphCharactersAreNotPrevented(PhysicalKey key, KeyModifiers mods, string? keyValue, bool altGraph, bool expected) =>
        Assert.Equal(expected, KeyboardPolicy.ShouldPreventDefault(key, mods, false, 0, keyValue, altGraph));

    [Theory]
    [InlineData("a", true)]
    [InlineData(" ", true)]
    [InlineData("你", true)]
    [InlineData("\U0001F600", true)]
    [InlineData("Enter", false)]
    [InlineData("F1", false)]
    [InlineData("Unidentified", false)]
    [InlineData("\uD800", false)]
    [InlineData("\uD800\uD800", false)]
    [InlineData("\t", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void PrintableKeyValues(string? keyValue, bool expected) => Assert.Equal(expected, KeyboardPolicy.IsPrintableKeyValue(keyValue));

    [Theory]
    // Windows 上的 AltGr：浏览器报告 Ctrl+Alt + AltGraph 状态 → AltGraph（去掉模拟出的 Ctrl 与 Alt）
    [InlineData(2 | 4 | 16, "@", KeyModifiers.AltGraph)]
    [InlineData(2 | 4, "€", KeyModifiers.AltGraph)]                       // 没有 AltGraph 状态，但 Ctrl+Alt 产生字符
    [InlineData(1 | 2 | 4 | 16, "\\", KeyModifiers.AltGraph | KeyModifiers.Shift)]
    [InlineData(2 | 4 | 16, "Dead", KeyModifiers.AltGraph)]
    // Linux：AltGr 只报告 AltGraph 状态
    [InlineData(16, "@", KeyModifiers.AltGraph)]
    // macOS 上把 Option 报告为 Alt + AltGraph 的浏览器：Option+E 的死键是 AltGr 文本
    [InlineData(4 | 16, "Dead", KeyModifiers.AltGraph)]
    // 不产生字符：保持浏览器报告的修饰键（Option+方向键按词移动、Ctrl+Alt+Delete……）
    [InlineData(4 | 16, "ArrowLeft", KeyModifiers.Alt)]
    [InlineData(2 | 4, "Delete", KeyModifiers.Control | KeyModifiers.Alt)]
    [InlineData(2 | 4, "a", KeyModifiers.Control | KeyModifiers.Alt)]     // US 布局的 Ctrl+Alt+A 是快捷键（评审发现）
    [InlineData(1 | 2 | 4, "A", KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift)]
    [InlineData(2 | 4, "ą", KeyModifiers.AltGraph)]                       // 波兰语 AltGr+A
    [InlineData(2 | 4 | 8, "q", KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)]
    [InlineData(2, "c", KeyModifiers.Control)]
    [InlineData(0, "a", KeyModifiers.None)]
    public void KeyModifiersSeparateAltGraphText(int mask, string key, KeyModifiers expected) =>
        Assert.Equal(expected, DomKeyMap.ToKeyModifiers(mask, key));

    [Theory]
    [InlineData("a", "a")]
    [InlineData("Enter", "Enter")]
    [InlineData("Dead", "Dead")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("\u0007", null)]
    public void LogicalKeys(string? key, string? expected) => Assert.Equal(expected, DomKeyMap.ToLogicalKey(key));

    [Fact]
    public void OverlongLogicalKeyIsDropped() => Assert.Null(DomKeyMap.ToLogicalKey(new string('x', KeyValues.MaxLength + 1)));
}
