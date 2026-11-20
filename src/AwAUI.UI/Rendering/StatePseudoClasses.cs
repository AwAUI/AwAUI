using System;
using AwAUI.Css.Selectors;

namespace AwAUI.UI;

/// <summary>
/// 表单与文档状态伪类（<c>:required :read-write :invalid :open :link</c> ...）在 AwAUI 元素上的求值（HTML §4.16.3「伪类」的子集）。
/// 只依赖元素类型、属性与输入框的值；元素属性变化时元素自身已标记样式失效，祖先 / 兄弟由 <see cref="StyleInvalidation"/>
/// 按 <c>:has()</c> 与非主体位置的使用处理。
/// <list type="bullet">
/// <item>表单控件：<c>input</c>（<see cref="InputElement"/>）、<c>textarea</c>、<c>select</c>。<c>:required</c> 看 <c>required</c> 属性；
/// <c>:invalid</c> 只实现「必填且为空」（没有约束校验的其余部分）；<c>:user-valid / :user-invalid</c> 不匹配。</item>
/// <item><c>:read-write</c>：可编辑（未禁用、非只读）的输入框，或带 <c>contenteditable</c> 的元素；其余元素 <c>:read-only</c>。</item>
/// <item><c>:open</c>：带 <c>open</c> 属性的元素；<c>:closed</c>：没有 <c>open</c> 属性的 <c>details / dialog / select</c>。</item>
/// <item><c>:link / :any-link</c>：带 <c>href</c> 的 <c>a / area</c>（没有访问历史，<c>:visited</c> 永不匹配）。</item>
/// <item><c>:indeterminate</c>：<c>indeterminate</c> 属性或 <c>aria-checked="mixed"</c>；<c>:default</c>：<c>checked</c> / <c>selected</c> 属性。</item>
/// <item><c>:defined</c> 总是匹配；其余（<c>:modal :fullscreen :target :autofill :playing</c> ...）不匹配。</item>
/// </list>
/// </summary>
internal static class StatePseudoClasses
{
    public static bool Matches(Element e, PseudoClass pc)
    {
        switch (pc)
        {
            case PseudoClass.Required:
                return IsFormControl(e) && e.GetAttribute("required") is not null;
            case PseudoClass.Optional:
                return IsFormControl(e) && e.GetAttribute("required") is null;
            case PseudoClass.Valid:
                return IsFormControl(e) && !IsInvalid(e);
            case PseudoClass.Invalid:
                return IsFormControl(e) && IsInvalid(e);
            case PseudoClass.ReadWrite:
                return IsReadWrite(e);
            case PseudoClass.ReadOnly:
                return !IsReadWrite(e);
            case PseudoClass.Indeterminate:
                return e.GetAttribute("indeterminate") is not null
                       || string.Equals(e.GetAttribute("aria-checked"), "mixed", StringComparison.OrdinalIgnoreCase);
            case PseudoClass.Default:
                return e.GetAttribute("checked") is not null || e.GetAttribute("selected") is not null;
            case PseudoClass.Open:
                return e.GetAttribute("open") is not null;
            case PseudoClass.Closed:
                return e.GetAttribute("open") is null && (Is(e, "details") || Is(e, "dialog") || Is(e, "select"));
            case PseudoClass.Link:
            case PseudoClass.AnyLink:
                return (Is(e, "a") || Is(e, "area")) && e.GetAttribute("href") is not null;
            case PseudoClass.Defined:
                return true;
            default:
                return false;
        }
    }

    static bool Is(Element e, string tag) => string.Equals(e.Tag, tag, StringComparison.OrdinalIgnoreCase);

    static bool IsFormControl(Element e) => e is InputElement || Is(e, "textarea") || Is(e, "select");

    static bool IsInvalid(Element e)
    {
        if (e.GetAttribute("required") is null || e.IsDisabled) return false;
        return e is InputElement input ? input.Value.Length == 0 : string.IsNullOrEmpty(e.GetAttribute("value"));
    }

    static bool IsReadWrite(Element e)
    {
        if (e is InputElement input) return !input.ReadOnly && !input.IsDisabled;
        if (Is(e, "textarea")) return e.GetAttribute("readonly") is null && !e.IsDisabled;
        var ce = e.GetAttribute("contenteditable");
        return ce is not null && !string.Equals(ce, "false", StringComparison.OrdinalIgnoreCase);
    }
}
