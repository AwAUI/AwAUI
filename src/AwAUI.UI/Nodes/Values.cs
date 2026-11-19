using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;

namespace AwAUI.UI;

/// <summary>
/// class 列表的描述值：可由字符串隐式转换（<c>Class = "a b"</c>），条件类名用 <see cref="Cx(string?, (string Name, bool On)[])"/>。
/// 内部保存规范化的文本（单个空格分隔、无首尾空白），按文本比较相等。
/// </summary>
public readonly struct ClassValue : IEquatable<ClassValue>
{
    readonly string? _text;

    public ClassValue(string? text) => _text = Normalize(text);

    /// <summary>规范化后的文本；没有 class 时为空串。</summary>
    public string Text => _text ?? "";

    public bool IsEmpty => string.IsNullOrEmpty(_text);

    public static implicit operator ClassValue(string? text) => new(text);

    /// <summary>条件类名：<c>Cx("tab", ("active", isActive), ("disabled", !enabled))</c>。</summary>
    public static ClassValue Cx(string? baseClasses, params (string Name, bool On)[] conditional)
    {
        var sb = new StringBuilder(baseClasses ?? "");
        if (conditional is not null)
        {
            foreach (var (name, on) in conditional)
            {
                if (!on || string.IsNullOrEmpty(name)) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(name);
            }
        }
        return new ClassValue(sb.ToString());
    }

    /// <summary>只有条件类名的写法：<c>Cx(("a", x), ("b", y))</c>。</summary>
    public static ClassValue Cx(params (string Name, bool On)[] conditional) => Cx(null, conditional);

    /// <summary>拆分为互不重复的类名（保持首次出现的顺序）。</summary>
    internal string[] Split()
    {
        var text = _text;
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
        var parts = text!.Split(' ');
        if (parts.Length == 1) return parts;
        int n = 0;
        if (parts.Length > 16)
        {
            // 类名可能来自数据：避免平方复杂度
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < parts.Length; i++)
                if (seen.Add(parts[i])) parts[n++] = parts[i];
        }
        else
        {
            for (int i = 0; i < parts.Length; i++)
            {
                bool dup = false;
                for (int j = 0; j < n; j++)
                {
                    if (string.Equals(parts[j], parts[i], StringComparison.Ordinal)) { dup = true; break; }
                }
                if (!dup) parts[n++] = parts[i];
            }
        }
        if (n != parts.Length) Array.Resize(ref parts, n);
        return parts;
    }

    static string? Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        // 快速路径：已经是规范形式
        bool clean = !IsWs(text![0]) && !IsWs(text[text.Length - 1]);
        if (clean)
        {
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (IsWs(c) && (c != ' ' || IsWs(text[i + 1]))) { clean = false; break; }
            }
        }
        if (clean) return text;
        var sb = new StringBuilder(text.Length);
        bool pendingSpace = false;
        foreach (char c in text)
        {
            if (IsWs(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }
            if (pendingSpace) sb.Append(' ');
            pendingSpace = false;
            sb.Append(c);
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    static bool IsWs(char c) => c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';

    public bool Equals(ClassValue other) => string.Equals(Text, other.Text, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is ClassValue o && Equals(o);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Text);

    public static bool operator ==(ClassValue a, ClassValue b) => a.Equals(b);

    public static bool operator !=(ClassValue a, ClassValue b) => !a.Equals(b);

    public override string ToString() => Text;
}

/// <summary>
/// 内联样式的描述值。目前以原始 CSS 声明文本保存（<c>Style = "padding: 4px"</c>），
/// 解析与强类型样式对象在样式集成阶段接入（design/08）。
/// </summary>
public readonly struct StyleValue : IEquatable<StyleValue>
{
    readonly string? _text;

    public StyleValue(string? text) => _text = string.IsNullOrEmpty(text) ? null : text;

    /// <summary>原始声明文本；没有内联样式时为 null。</summary>
    public string? Text => _text;

    public bool IsEmpty => _text is null;

    public static implicit operator StyleValue(string? text) => new(text);

    public bool Equals(StyleValue other) => string.Equals(_text, other._text, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is StyleValue o && Equals(o);

    public override int GetHashCode() => _text is null ? 0 : StringComparer.Ordinal.GetHashCode(_text);

    public static bool operator ==(StyleValue a, StyleValue b) => a.Equals(b);

    public static bool operator !=(StyleValue a, StyleValue b) => !a.Equals(b);

    public override string ToString() => _text ?? "";
}

/// <summary>
/// 自定义属性表（<c>data-*</c>、<c>aria-*</c> 等），供 CSS 属性选择器使用。属性名按 ASCII 大小写不敏感比较，
/// 保持插入顺序；值为 null 表示删除。
/// </summary>
public sealed class AttributeMap : IReadOnlyList<KeyValuePair<string, string>>
{
    const int IndexThreshold = 16;
    readonly List<KeyValuePair<string, string>> _items = new();
    Dictionary<string, int>? _index;

    public AttributeMap() { }

    /// <summary>复制。</summary>
    public AttributeMap(AttributeMap? other)
    {
        if (other is not null) _items.AddRange(other._items);
    }

    public int Count => _items.Count;

    public KeyValuePair<string, string> this[int index] => _items[index];

    /// <summary>读写属性；写入 null 删除该属性。</summary>
    public string? this[string name]
    {
        get
        {
            int i = IndexOf(name);
            return i < 0 ? null : _items[i].Value;
        }
        set
        {
            if (name is null) throw new ArgumentNullException(nameof(name));
            int i = IndexOf(name);
            if (value is null)
            {
                if (i >= 0)
                {
                    _items.RemoveAt(i);
                    _index = null;
                }
            }
            else if (i >= 0)
            {
                _items[i] = new KeyValuePair<string, string>(_items[i].Key, value);
            }
            else
            {
                _items.Add(new KeyValuePair<string, string>(name, value));
                _index?.Add(name, _items.Count - 1);
            }
        }
    }

    public void Add(string name, string? value) => this[name] = value;

    /// <summary>属性透传（<c>@attributes</c>）：逐项写入。</summary>
    public void AddRange(IEnumerable<KeyValuePair<string, string>>? attributes)
    {
        if (attributes is null) return;
        foreach (var kv in attributes) this[kv.Key] = kv.Value;
    }

    /// <summary>属性透传（<c>@attributes</c>）：值按字符串化写入，null 值删除。</summary>
    public void AddRange(IEnumerable<KeyValuePair<string, object?>>? attributes)
    {
        if (attributes is null) return;
        foreach (var kv in attributes) this[kv.Key] = kv.Value is bool b ? (b ? "true" : "false") : kv.Value?.ToString();
    }

    public bool Contains(string name) => IndexOf(name) >= 0;

    int IndexOf(string name)
    {
        if (_items.Count > IndexThreshold)
        {
            if (_index is null)
            {
                _index = new Dictionary<string, int>(_items.Count, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < _items.Count; i++) _index[_items[i].Key] = i;
            }
            return _index.TryGetValue(name, out int idx) ? idx : -1;
        }
        for (int i = 0; i < _items.Count; i++)
            if (string.Equals(_items[i].Key, name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>内容相等（与顺序无关）。</summary>
    internal static bool ContentEquals(AttributeMap? a, AttributeMap? b)
    {
        int ca = a?.Count ?? 0, cb = b?.Count ?? 0;
        if (ca != cb) return false;
        if (ca == 0) return true;
        foreach (var kv in a!._items)
        {
            if (!string.Equals(b![kv.Key], kv.Value, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    public List<KeyValuePair<string, string>>.Enumerator GetEnumerator() => _items.GetEnumerator();

    IEnumerator<KeyValuePair<string, string>> IEnumerable<KeyValuePair<string, string>>.GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();
}

/// <summary>无障碍角色（design/10）。<see cref="None"/> 表示使用元素的默认角色。</summary>
public enum Role : byte
{
    None,
    Generic,
    Button,
    Link,
    Text,
    Image,
    TextBox,
    CheckBox,
    Radio,
    Switch,
    Slider,
    ProgressBar,
    ComboBox,
    ListBox,
    Option,
    List,
    ListItem,
    Tree,
    TreeItem,
    Grid,
    Row,
    Cell,
    Tab,
    TabList,
    TabPanel,
    Menu,
    MenuBar,
    MenuItem,
    Dialog,
    AlertDialog,
    Alert,
    Status,
    Tooltip,
    Toolbar,
    Separator,
    Group,
    Region,
    Navigation,
    Heading,
    Presentation,
}
