using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using AwAUI.Browser;
using AwAUI.Platform;

namespace AwAUI.Browser.Tests;

/// <summary>
/// 影子 ARIA DOM 的纯 C# 部分：JSON 编码、角色映射、状态 / 动作编号。
/// 这些逻辑由 node/a11y-smoke.mjs 在真实 DOM 垫片上端到端验证，这里在普通 .NET 上验证数据与安全边界。
/// </summary>
public class AccessibilityTests
{
    // ------------------------------------------------------------ 构造快照

    static AccessibilityNodeSnapshot Node(string id, string role, string? name = null, string? value = null,
        string? parent = null, params string[] states)
    {
        return new AccessibilityNodeSnapshot
        {
            Id = id,
            Role = role,
            Name = name,
            Value = value,
            ParentId = parent,
            States = states,
        };
    }

    static AccessibilitySnapshot Snapshot(params AccessibilityNodeSnapshot[] nodes)
        => new AccessibilitySnapshot { Version = 7, RootId = nodes.Length > 0 ? nodes[0].Id : null, Nodes = nodes };

    static Dictionary<string, object?> Obj(object? value) => (Dictionary<string, object?>)value!;
    static List<object?> Arr(object? value) => (List<object?>)value!;
    static string Str(object? value) => (string)value!;
    static double Num(object? value) => (double)value!;
    static Dictionary<string, object?> FindNode(Dictionary<string, object?> model, string id)
    {
        foreach (var node in Arr(model["n"]))
            if (Str(Obj(node)["i"]) == id) return Obj(node);
        throw new InvalidOperationException("node not found: " + id);
    }

    // ------------------------------------------------------------ 编码

    [Fact]
    public void EncodesCoreFieldsAndNesting()
    {
        var root = Node("root", "root");
        root.ChildIds = new[] { "btn", "box", "live" };
        var button = Node("btn", "button", "关闭", parent: "root", states: new[] { "focused" });
        button.Actions = new[] { AccessibilityActionKind.Invoke, AccessibilityActionKind.Focus };
        button.Bounds = new Rect(10, 20, 30, 40);
        var box = Node("box", "textbox", "搜索", value: "hello", parent: "root");
        box.Actions = new[] { AccessibilityActionKind.SetValue, AccessibilityActionKind.Focus };
        box.Value = "hello";
        var live = Node("live", "status", "已保存", parent: "root");
        live.LiveRegion = AccessibilityPoliteness.Polite;

        var json = BrowserAriaCodec.Encode(Snapshot(root, button, box, live));
        var model = Obj(ParseJson(json));

        Assert.Equal(1.0, Num(model["v"]));
        Assert.Equal(7.0, Num(model["sv"]));
        Assert.Equal("root", Str(model["r"]));

        var rootNode = FindNode(model, "root");
        Assert.Equal("application", Str(rootNode["r"]));
        Assert.Equal(new List<object?> { "btn", "box", "live" }, Arr(rootNode["c"]));

        var btn = FindNode(model, "btn");
        Assert.Equal("button", Str(btn["r"]));
        Assert.Equal("关闭", Str(btn["nm"]));
        Assert.Equal(new List<object?> { "focused" }, Arr(btn["st"]));
        Assert.Equal(new List<object?> { 0.0, 1.0 }, Arr(btn["a"]));
        Assert.Equal(new List<object?> { 10.0, 20.0, 30.0, 40.0 }, Arr(btn["b"]));

        var text = FindNode(model, "box");
        Assert.Equal("textbox", Str(text["r"]));
        Assert.Equal("hello", Str(text["v"]));
        Assert.Equal(new List<object?> { 3.0, 1.0 }, Arr(text["a"]));

        Assert.Equal(1.0, Num(FindNode(model, "live")["lv"]));
    }

    [Fact]
    public void EmptySnapshotIsValidJson()
    {
        var model = Obj(ParseJson(BrowserAriaCodec.Encode(new AccessibilitySnapshot { Version = 1 })));
        Assert.Equal(1.0, Num(model["v"]));
        Assert.Empty(Arr(model["n"]));
        Assert.False(model.ContainsKey("r"));
    }

    [Fact]
    public void EscapesUntrustedStrings()
    {
        string hostile = "a\"b\\c\n\r\t\u0001 中文 😀 \uD800";
        var node = Node("n", "button", hostile);
        var json = BrowserAriaCodec.Encode(Snapshot(node));

        // 原始控制字符不能出现在 JSON 文本里；引号 / 反斜杠必须转义。
        Assert.Equal(-1, json.IndexOf('\u0001'));
        Assert.Equal(-1, json.IndexOf('\n'));
        Assert.Equal(-1, json.IndexOf('\r'));
        Assert.Contains("\\\"", json);
        Assert.Contains("\\\\", json);
        Assert.Contains("\\u0001", json);
        Assert.Contains("\\ud800", json);

        var model = Obj(ParseJson(json));
        Assert.Equal(hostile, Str(FindNode(model, "n")["nm"]));
    }

    [Fact]
    public void NonFiniteBoundsBecomeZero()
    {
        var node = Node("n", "button");
        node.Bounds = new Rect(float.NaN, float.PositiveInfinity, float.NegativeInfinity, float.NaN);
        var bounds = Arr(FindNode(Obj(ParseJson(BrowserAriaCodec.Encode(Snapshot(node)))), "n")["b"]);
        Assert.Equal(new List<object?> { 0.0, 0.0, 0.0, 0.0 }, bounds);
    }

    [Fact]
    public void UnknownStatesAndActionsAreDropped()
    {
        var node = Node("n", "button", states: new[] { "focused", "not-a-state" });
        node.Actions = new[] { AccessibilityActionKind.Invoke, AccessibilityActionKind.ScrollIntoView };
        var encoded = FindNode(Obj(ParseJson(BrowserAriaCodec.Encode(Snapshot(node)))), "n");
        Assert.Equal(new List<object?> { "focused" }, Arr(encoded["st"]));
        Assert.Equal(new List<object?> { 0.0, 6.0 }, Arr(encoded["a"]));
    }

    // ------------------------------------------------------------ 角色 / 状态 / 动作

    [Theory]
    [InlineData("root", "application")]
    [InlineData("generic", "group")]
    [InlineData("text", "paragraph")]
    [InlineData("statictext", "paragraph")]
    [InlineData("image", "img")]
    [InlineData("button", "button")]
    [InlineData("alertdialog", "alertdialog")]
    [InlineData("", "group")]
    [InlineData(null, "group")]
    [InlineData("Bad Role!", "group")]
    public void MapsRoles(string? role, string expected) => Assert.Equal(expected, BrowserAriaCodec.ToAriaRole(role));

    [Fact]
    public void MapsStatesToAriaAttributes()
    {
        Assert.True(BrowserAriaCodec.TryMapState("checked", out var attr, out var value));
        Assert.Equal(("aria-checked", "true"), (attr, value));
        Assert.True(BrowserAriaCodec.TryMapState("mixed", out attr, out value));
        Assert.Equal(("aria-checked", "mixed"), (attr, value));
        Assert.True(BrowserAriaCodec.TryMapState("readonly", out attr, out value));
        Assert.Equal(("aria-readonly", "true"), (attr, value));
        Assert.False(BrowserAriaCodec.TryMapState("bogus", out _, out _));
        Assert.False(BrowserAriaCodec.IsKnownState("bogus"));
        Assert.True(BrowserAriaCodec.IsKnownState("focused"));
    }

    [Fact]
    public void ActionNumbersRoundTripAndRejectUnknownKinds()
    {
        var expected = new[]
        {
            AccessibilityActionKind.Invoke, AccessibilityActionKind.Focus, AccessibilityActionKind.Toggle,
            AccessibilityActionKind.SetValue, AccessibilityActionKind.Increment, AccessibilityActionKind.Decrement,
            AccessibilityActionKind.ScrollIntoView, AccessibilityActionKind.Expand, AccessibilityActionKind.Collapse,
        };
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(i, BrowserAriaCodec.ActionCode(expected[i]));
            Assert.True(BrowserAriaCodec.TryParseAction(i, out var parsed));
            Assert.Equal(expected[i], parsed);
        }
        Assert.False(BrowserAriaCodec.TryParseAction(-1, out _));
        Assert.False(BrowserAriaCodec.TryParseAction(9, out _));
        Assert.Equal(-1, BrowserAriaCodec.ActionCode((AccessibilityActionKind)99));
    }

    [Fact]
    public void CollectActionsIndexesNodesForWhitelistChecks()
    {
        var a = Node("a", "button");
        a.Actions = new[] { AccessibilityActionKind.Invoke, AccessibilityActionKind.Focus };
        var b = Node("b", "paragraph");
        var map = BrowserAriaCodec.CollectActions(Snapshot(a, b));
        Assert.True(map.ContainsKey("a"));
        Assert.Equal(2, map["a"].Count);
        Assert.False(map.ContainsKey("b"));
    }

    [Fact]
    public void WhitelistRejectsUnknownNodesAndUndeclaredActions()
    {
        var a = Node("a", "button");
        a.Actions = new[] { AccessibilityActionKind.Invoke };
        var map = BrowserAriaCodec.CollectActions(Snapshot(a));
        Assert.True(BrowserAriaCodec.IsActionAllowed(map, "a", AccessibilityActionKind.Invoke));
        Assert.False(BrowserAriaCodec.IsActionAllowed(map, "a", AccessibilityActionKind.Toggle));
        Assert.False(BrowserAriaCodec.IsActionAllowed(map, "missing", AccessibilityActionKind.Invoke));
        Assert.False(BrowserAriaCodec.IsActionAllowed(map, null, AccessibilityActionKind.Invoke));
        Assert.False(BrowserAriaCodec.IsActionAllowed(null, "a", AccessibilityActionKind.Invoke));
    }

    [Fact]
    public void SanitizesSetValueText()
    {
        Assert.Equal("abc", BrowserAriaCodec.SanitizeValue("\u0000a\u0001b\u0002c"));
        Assert.Equal("", BrowserAriaCodec.SanitizeValue(null));
        var big = new string('x', BrowserAriaCodec.MaxValueLength + 100);
        Assert.Equal(BrowserAriaCodec.MaxValueLength, BrowserAriaCodec.SanitizeValue(big).Length);
    }

    // ------------------------------------------------------------ 关系 / 几何

    [Fact]
    public void EncodesRelationsLiveAndActiveDescendant()
    {
        var list = Node("list", "listbox");
        list.ChildIds = new[] { "opt" };
        var option = Node("opt", "option", "第一项", parent: "list", states: new[] { "focused", "selected" });
        list.LabelledBy = new[] { "label" };
        list.Controls = new[] { "panel" };
        list.DescribedBy = new[] { "hint" };
        var dialog = Node("dlg", "dialog");
        dialog.HeadingLevel = 2;

        var model = Obj(ParseJson(BrowserAriaCodec.Encode(Snapshot(list, option, dialog))));
        var listNode = FindNode(model, "list");
        Assert.Equal(new List<object?> { "label" }, Arr(listNode["lb"]));
        Assert.Equal(new List<object?> { "panel" }, Arr(listNode["ct"]));
        Assert.Equal(new List<object?> { "hint" }, Arr(listNode["db"]));
        Assert.Equal("opt", Str(listNode["ad"]));

        var dlg = FindNode(model, "dlg");
        Assert.Equal(1.0, Num(dlg["md"]));
        Assert.Equal(2.0, Num(dlg["lvl"]));

        var optionNode = FindNode(model, "opt");
        Assert.Equal(new List<object?> { "focused", "selected" }, Arr(optionNode["st"]));
    }

    [Fact]
    public void HasPopupForComboBoxAndExpandableButtons()
    {
        var combo = Node("c", "combobox");
        var button = Node("b", "button");
        button.Actions = new[] { AccessibilityActionKind.Expand };
        var model = Obj(ParseJson(BrowserAriaCodec.Encode(Snapshot(combo, button))));
        Assert.Equal("listbox", Str(FindNode(model, "c")["hp"]));
        Assert.Equal("true", Str(FindNode(model, "b")["hp"]));
    }

    [Fact]
    public void SupportsKeyShortcutsSuppliedByCaller()
    {
        var node = Node("n", "button", "保存");
        var shortcuts = new Dictionary<string, string> { ["n"] = "Ctrl+S" };
        var model = Obj(ParseJson(BrowserAriaCodec.Encode(Snapshot(node), shortcuts)));
        Assert.Equal("Ctrl+S", Str(FindNode(model, "n")["ks"]));
    }

    // ------------------------------------------------------------ 与 JS 胶水的编号一致性

    [Fact]
    public void ActionAndLiveCodesMatchGlue()
    {
        string js = RepoFile("src/Browser/AwAUI.Browser/wwwroot/awaui-browser.js");
        Assert.Equal(BrowserAriaCodec.FormatVersion, JsConst(js, "A11Y_FORMAT_VERSION"));
        Assert.Equal(BrowserAriaCodec.ActionInvoke, JsConst(js, "A11Y_ACTION_INVOKE"));
        Assert.Equal(BrowserAriaCodec.ActionFocus, JsConst(js, "A11Y_ACTION_FOCUS"));
        Assert.Equal(BrowserAriaCodec.ActionToggle, JsConst(js, "A11Y_ACTION_TOGGLE"));
        Assert.Equal(BrowserAriaCodec.ActionSetValue, JsConst(js, "A11Y_ACTION_SET_VALUE"));
        Assert.Equal(BrowserAriaCodec.ActionIncrement, JsConst(js, "A11Y_ACTION_INCREMENT"));
        Assert.Equal(BrowserAriaCodec.ActionDecrement, JsConst(js, "A11Y_ACTION_DECREMENT"));
        Assert.Equal(BrowserAriaCodec.ActionScrollIntoView, JsConst(js, "A11Y_ACTION_SCROLL_INTO_VIEW"));
        Assert.Equal(BrowserAriaCodec.ActionExpand, JsConst(js, "A11Y_ACTION_EXPAND"));
        Assert.Equal(BrowserAriaCodec.ActionCollapse, JsConst(js, "A11Y_ACTION_COLLAPSE"));
        Assert.Equal(BrowserAriaCodec.LiveOff, JsConst(js, "A11Y_LIVE_OFF"));
        Assert.Equal(BrowserAriaCodec.LivePolite, JsConst(js, "A11Y_LIVE_POLITE"));
        Assert.Equal(BrowserAriaCodec.LiveAssertive, JsConst(js, "A11Y_LIVE_ASSERTIVE"));
    }

    static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AwAUI.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, relative));
    }

    static int JsConst(string js, string name)
    {
        var m = Regex.Match(js, @"\b" + name + @"\s*=\s*(-?\d+)");
        Assert.True(m.Success, name);
        return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------ 最小 JSON 解析器（验证输出确实是合法 JSON）

    static object? ParseJson(string text)
    {
        int index = 0;
        var value = ParseValue(text, ref index);
        SkipWhitespace(text, ref index);
        Assert.Equal(text.Length, index);
        return value;
    }

    static object? ParseValue(string text, ref int i)
    {
        SkipWhitespace(text, ref i);
        Assert.True(i < text.Length, "unexpected end of JSON");
        char c = text[i];
        if (c == '{') return ParseObject(text, ref i);
        if (c == '[') return ParseArray(text, ref i);
        if (c == '"') return ParseString(text, ref i);
        if (c == 't') { Expect(text, ref i, "true"); return true; }
        if (c == 'f') { Expect(text, ref i, "false"); return false; }
        if (c == 'n') { Expect(text, ref i, "null"); return null; }
        return ParseNumber(text, ref i);
    }

    static Dictionary<string, object?> ParseObject(string text, ref int i)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        i++; // {
        SkipWhitespace(text, ref i);
        if (i < text.Length && text[i] == '}') { i++; return result; }
        while (true)
        {
            SkipWhitespace(text, ref i);
            string key = ParseString(text, ref i);
            SkipWhitespace(text, ref i);
            Assert.True(i < text.Length && text[i] == ':');
            i++;
            result[key] = ParseValue(text, ref i);
            SkipWhitespace(text, ref i);
            Assert.True(i < text.Length);
            if (text[i] == ',') { i++; continue; }
            Assert.Equal('}', text[i]);
            i++;
            return result;
        }
    }

    static List<object?> ParseArray(string text, ref int i)
    {
        var result = new List<object?>();
        i++; // [
        SkipWhitespace(text, ref i);
        if (i < text.Length && text[i] == ']') { i++; return result; }
        while (true)
        {
            result.Add(ParseValue(text, ref i));
            SkipWhitespace(text, ref i);
            Assert.True(i < text.Length);
            if (text[i] == ',') { i++; continue; }
            Assert.Equal(']', text[i]);
            i++;
            return result;
        }
    }

    static string ParseString(string text, ref int i)
    {
        Assert.Equal('"', text[i]);
        i++;
        var sb = new StringBuilder();
        while (i < text.Length)
        {
            char c = text[i++];
            if (c == '"') return sb.ToString();
            if (c != '\\') { sb.Append(c); continue; }
            Assert.True(i < text.Length);
            char e = text[i++];
            switch (e)
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                    Assert.True(i + 4 <= text.Length);
                    sb.Append((char)int.Parse(text.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 4;
                    break;
                default: throw new InvalidOperationException("bad escape: " + e);
            }
        }
        throw new InvalidOperationException("unterminated string");
    }

    static double ParseNumber(string text, ref int i)
    {
        int start = i;
        while (i < text.Length && "-+.eE0123456789".IndexOf(text[i]) >= 0) i++;
        Assert.True(i > start, "bad number");
        return double.Parse(text.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    static void SkipWhitespace(string text, ref int i)
    {
        while (i < text.Length && (text[i] == ' ' || text[i] == '\t' || text[i] == '\r' || text[i] == '\n')) i++;
    }

    static void Expect(string text, ref int i, string literal)
    {
        Assert.True(i + literal.Length <= text.Length && text.Substring(i, literal.Length) == literal);
        i += literal.Length;
    }
}
