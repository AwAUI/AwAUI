using System;
using System.Collections.Generic;

namespace AwAUI.Markup.Compiler.Analysis;

/// <summary>内建元素属性值在编译期转换的目标类型。</summary>
public enum AttributeValueType
{
    String,
    Integer,
    Number,
    Boolean,
    Enum,
}

/// <summary>内建元素的一个已知属性。</summary>
public sealed class IntrinsicAttributeInfo
{
    public IntrinsicAttributeInfo(string name, AttributeValueType type, params string[] enumValues)
    {
        Name = name;
        Type = type;
        EnumValues = enumValues;
    }

    public string Name { get; }
    public AttributeValueType Type { get; }
    /// <summary><see cref="AttributeValueType.Enum"/> 时允许的取值。</summary>
    public IReadOnlyList<string> EnumValues { get; }
}

/// <summary>
/// 内建元素与其属性表（design/17：<c>div span text img input textarea canvas svg path</c>）。
/// 属性名采用 HTML 拼写；<c>aria-*</c> 与 <c>data-*</c> 对所有内建元素开放。
/// 注：设计文档只规定了元素名，属性清单是依据 design/12、14、22 中出现的属性整理的暂定版本。
/// </summary>
public static class IntrinsicElements
{
    static readonly string[] ElementNames = { "div", "span", "text", "img", "input", "textarea", "canvas", "svg", "path" };

    static readonly Dictionary<string, IntrinsicAttributeInfo> Global = Table(
        S("id"), S("class"), S("style"), S("role"), S("title"), S("lang"),
        new("hidden", AttributeValueType.Boolean),
        // HTML 的枚举属性（关键字 true / false，缺省与无效值为 auto），不是布尔属性：draggable="false" 与不写不同
        new("draggable", AttributeValueType.Enum, "true", "false"),
        new("tabindex", AttributeValueType.Integer),
        new("dir", AttributeValueType.Enum, "ltr", "rtl", "auto"));

    static readonly Dictionary<string, Dictionary<string, IntrinsicAttributeInfo>> PerElement = new(StringComparer.Ordinal)
    {
        ["div"] = Table(),
        ["span"] = Table(),
        ["text"] = Table(),
        ["img"] = Table(S("src"), S("alt"),
            new("width", AttributeValueType.Number), new("height", AttributeValueType.Number),
            new("loading", AttributeValueType.Enum, "eager", "lazy")),
        ["input"] = Table(S("value"), S("placeholder"), S("name"), S("autocomplete"), S("pattern"), S("min"), S("max"), S("step"),
            new("type", AttributeValueType.Enum, "text", "password", "number", "email", "search", "tel", "url", "checkbox", "radio", "range"),
            new("inputmode", AttributeValueType.Enum, "none", "text", "decimal", "numeric", "tel", "search", "email", "url"),
            B("disabled"), B("readonly"), B("required"), B("autofocus"), B("checked"), SpellCheck,
            new("maxlength", AttributeValueType.Integer), new("minlength", AttributeValueType.Integer)),
        ["textarea"] = Table(S("value"), S("placeholder"), S("name"),
            B("disabled"), B("readonly"), B("required"), B("autofocus"), SpellCheck,
            new("maxlength", AttributeValueType.Integer), new("minlength", AttributeValueType.Integer),
            new("rows", AttributeValueType.Integer), new("cols", AttributeValueType.Integer),
            new("wrap", AttributeValueType.Enum, "soft", "hard", "off")),
        ["canvas"] = Table(new("width", AttributeValueType.Integer), new("height", AttributeValueType.Integer)),
        ["svg"] = Table(S("viewBox"), S("width"), S("height"), S("fill"), S("stroke"), S("stroke-width"), S("xmlns"),
            S("preserveAspectRatio"), new("opacity", AttributeValueType.Number)),
        ["path"] = Table(S("d"), S("fill"), S("stroke"), S("stroke-width"), S("transform"), S("stroke-dasharray"),
            new("stroke-linecap", AttributeValueType.Enum, "butt", "round", "square"),
            new("stroke-linejoin", AttributeValueType.Enum, "miter", "round", "bevel"),
            new("fill-rule", AttributeValueType.Enum, "nonzero", "evenodd"),
            new("opacity", AttributeValueType.Number), new("fill-opacity", AttributeValueType.Number),
            new("stroke-opacity", AttributeValueType.Number)),
    };

    /// <summary>内建元素上可用的事件（<c>@on</c> 之后的部分，小写）。</summary>
    static readonly HashSet<string> EventNames = new(StringComparer.Ordinal)
    {
        "click", "dblclick", "contextmenu", "auxclick",
        "pointerdown", "pointerup", "pointermove", "pointerenter", "pointerleave", "pointerover", "pointerout", "pointercancel",
        "gotpointercapture", "lostpointercapture", "wheel",
        "keydown", "keyup", "beforeinput", "input", "change", "submit",
        "focus", "blur", "focusin", "focusout",
        "compositionstart", "compositionupdate", "compositionend",
        "scroll", "tap", "doubletap", "longpress", "pan", "pinch", "rotate", "swipe", "edgeswipe",
        "dragstart", "drag", "dragend", "dragenter", "dragleave", "dragover", "drop",
        "resize", "load", "error",
    };

    /// <summary>全部内建元素名。</summary>
    public static IReadOnlyList<string> Names => ElementNames;

    public static bool IsIntrinsic(string name) => PerElement.ContainsKey(name);

    /// <summary>
    /// 查找内建元素上的属性。<c>aria-*</c> / <c>data-*</c> 视为字符串属性。找不到返回 false。
    /// </summary>
    public static bool TryGetAttribute(string element, string attribute, out IntrinsicAttributeInfo? info)
    {
        info = null;
        if (!PerElement.TryGetValue(element, out var table)) return false;
        if (table.TryGetValue(attribute, out info) || Global.TryGetValue(attribute, out info)) return true;
        if ((attribute.StartsWith("aria-", StringComparison.Ordinal) || attribute.StartsWith("data-", StringComparison.Ordinal)) && attribute.Length > 5)
        {
            info = new IntrinsicAttributeInfo(attribute, AttributeValueType.String);
            return true;
        }
        return false;
    }

    /// <summary>是否是已知事件名（不含 on 前缀）。</summary>
    public static bool IsKnownEvent(string eventName) => EventNames.Contains(eventName);

    /// <summary>某个内建元素上全部已知属性名（用于给出「最接近的名称」）。</summary>
    public static IEnumerable<string> AttributeNames(string element)
    {
        if (PerElement.TryGetValue(element, out var table))
            foreach (var k in table.Keys) yield return k;
        foreach (var k in Global.Keys) yield return k;
    }

    internal static IEnumerable<string> EventAttributeNames()
    {
        foreach (var e in EventNames) yield return "on" + e;
    }

    static IntrinsicAttributeInfo S(string name) => new(name, AttributeValueType.String);

    /// <summary>
    /// HTML 的枚举属性 <c>spellcheck</c>（关键字 true / false，空串即 true，缺省与无效值为默认状态），不是布尔属性：
    /// <c>spellcheck="false"</c> 与不写不同。不带值的写法（空串）也有效。
    /// </summary>
    static IntrinsicAttributeInfo SpellCheck => new("spellcheck", AttributeValueType.Enum, "true", "false", "");
    static IntrinsicAttributeInfo B(string name) => new(name, AttributeValueType.Boolean);

    static Dictionary<string, IntrinsicAttributeInfo> Table(params IntrinsicAttributeInfo[] attrs)
    {
        var d = new Dictionary<string, IntrinsicAttributeInfo>(StringComparer.Ordinal);
        foreach (var a in attrs) d[a.Name] = a;
        return d;
    }
}
