using System;
using AwAUI.Platform;

namespace AwAUI.UI;

/// <summary>
/// <see cref="InputElement"/> 的文本输入客户端（design/04 <see cref="ITextInputClient"/>）。文档 = 值 + 插在
/// <see cref="InputElement.CompositionStart"/> 处的预编辑文本。平台的调用都在 UI 线程上，按派发入口包装
/// （处理器中未被错误边界捕获的异常在调用结束时抛出）。只读或禁用的输入框忽略修改。
/// </summary>
internal sealed class InputTextClient : ITextInputClient
{
    readonly InputElement _element;

    public InputTextClient(InputElement element) => _element = element;

    public InputElement Element => _element;

    bool Editable => _element.IsAttached && !_element.IsDisabled && !_element.ReadOnly;

    public TextInputOptions Options
    {
        get
        {
            var type = (_element.InputType ?? "text").ToLowerInvariant() switch
            {
                "password" => TextInputContentType.Password,
                "number" => TextInputContentType.Number,
                "email" => TextInputContentType.Email,
                "url" => TextInputContentType.Url,
                "search" => TextInputContentType.Search,
                "tel" => TextInputContentType.Phone,
                _ => TextInputContentType.Text,
            };
            return new TextInputOptions(type);
        }
    }

    public int Length => _element.Value.Length + _element.CompositionText.Length;

    public TextInputRange Selection
    {
        get
        {
            var e = _element;
            if (e.IsComposing)
                return new TextInputRange(e.CompositionStart + e.CompositionSelectionStart, e.CompositionStart + e.CompositionSelectionEnd);
            return new TextInputRange(e.SelectionStart, e.SelectionEnd);
        }
        set
        {
            var e = _element;
            if (!e.IsAttached) return;
            Run(() =>
            {
                var r = value.Clamp(Length);
                if (e.IsComposing)
                {
                    int cs = e.CompositionStart, ce = cs + e.CompositionText.Length;
                    if (r.Start >= cs && r.End <= ce)
                    {
                        // 预编辑文本内移动（移动端输入法）：只改组合内的选区
                        e.SetCompositionState(e.CompositionText, cs, r.Start - cs, r.End - cs);
                        return;
                    }
                    // 移出组合：按原样提交预编辑文本（此后文档下标与值下标相同）
                    e.Root!.Input.FinishComposition(e, commit: true, notifyPlatform: false);
                }
                e.SetSelectionCore(r.Start, r.End);
            });
        }
    }

    public TextInputRange? Composition =>
        _element.IsComposing ? new TextInputRange(_element.CompositionStart, _element.CompositionStart + _element.CompositionText.Length) : null;

    public string GetText(TextInputRange range)
    {
        var doc = _element.DisplayText;
        var r = range.Clamp(doc.Length);
        int start = TextEditing.Snap(doc, r.Start), end = TextEditing.Snap(doc, r.End);
        if (end <= start) return "";
        var text = doc.Substring(start, end - start);
        // 密码不交给输入法与剪贴板
        return _element.IsPassword ? TextEditing.Mask(text) : text;
    }

    public Rect GetCaretRect()
    {
        var sel = Selection;
        return _element.Root?.Input.GetTextBounds(_element, TextInputRange.Caret(sel.End)) ?? default;
    }

    public Rect GetTextBounds(TextInputRange range) => _element.Root?.Input.GetTextBounds(_element, range) ?? default;

    public void SetComposition(string text, TextInputRange selection)
    {
        text ??= "";
        if (!Editable) return;
        // 密码框关闭输入法组合：平台仍然送来组合时，忽略预编辑（提交的文本照常经 Commit 到达）
        if (_element.IsPassword && text.Length > 0) return;
        Run(() => _element.Root!.Input.UpdateComposition(_element, TextEditing.SanitizeSingleLine(text), selection.Start, selection.End));
    }

    public void Commit(string text)
    {
        text ??= "";
        if (!Editable)
        {
            if (_element.IsComposing && _element.IsAttached) Run(() => _element.Root!.Input.FinishComposition(_element, commit: false, notifyPlatform: false));
            return;
        }
        Run(() => _element.Root!.Input.CommitText(_element, text));
    }

    public void ReplaceRange(TextInputRange range, string text)
    {
        text ??= "";
        if (!Editable) return;
        Run(() =>
        {
            var e = _element;
            var r = range.Clamp(Length);
            if (e.IsComposing)
            {
                int cs = e.CompositionStart, ce = cs + e.CompositionText.Length;
                if (r.End > cs && r.Start < ce || r.Start == cs || r.End == ce)
                    e.Root!.Input.FinishComposition(e, commit: true, notifyPlatform: true);
                else
                {
                    // 与组合不相交：换算到值下标后替换，组合保留
                    int vs = e.DocumentToValue(r.Start), ve = e.DocumentToValue(r.End);
                    e.Root!.Input.ReplaceValueRange(e, vs, ve, text, keepComposition: true);
                    return;
                }
            }
            e.Root!.Input.ReplaceValueRange(e, r.Start, r.End, text, keepComposition: false);
        });
    }

    void Run(Action action)
    {
        var root = _element.Root;
        if (root is null) return;
        root.BeginDispatch();
        try
        {
            action();
        }
        finally
        {
            root.EndDispatch();
        }
    }
}
