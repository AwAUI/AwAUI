using System;
using System.Globalization;
using AwAUI.Controls.Primitives;
using AwAUI.Platform;
using AwAUI.UI;

namespace AwAUI.Controls;

/// <summary>
/// Single-line editor constrained by a character mask. The mask uses the familiar Windows Forms / Avalonia
/// placeholders:
/// <list type="bullet">
/// <item><c>0</c> required digit, <c>9</c> optional digit, <c>#</c> optional digit;</item>
/// <item><c>L</c> / <c>?</c> required / optional ASCII letter;</item>
/// <item><c>A</c> / <c>a</c> required / optional ASCII letter or digit;</item>
/// <item><c>&amp;</c> / <c>C</c> required / optional any non-control character;</item>
/// <item><c>&gt;</c> / <c>&lt;</c> uppercase / lowercase the characters that follow, <c>|</c> makes the next position optional.</item>
/// </list>
/// Every other character is a literal and <c>\</c> escapes one. The culture-sensitive literals <c>.</c>, <c>,</c>,
/// <c>:</c>, <c>/</c> and <c>$</c> expand to <see cref="Culture"/>'s separators and currency symbol. Unfilled positions
/// render as <see cref="PromptChar"/>, so <see cref="Text"/> (and <see cref="TextChanged"/>) always carry the formatted
/// value.
/// <para>
/// Editing is modelled by <see cref="MaskedTextProvider"/>: typing replaces the selected formatted range, Backspace
/// clears the previous editable position, Delete clears the next, and the caret stays on the next empty editable
/// position after typing. <see cref="SelectionStart"/> / <see cref="SelectionEnd"/> expose the formatted selection and
/// are kept in sync with the underlying input element.
/// </para>
/// </summary>
public sealed partial class MaskedTextBox : Control
{
    readonly ControllableState<string> _text = new(StringComparer.Ordinal);
    readonly Ref<Element> _inputRef = new();

    MaskedTextProvider _provider = new("");
    InputElement? _inputElement;
    string _formatted = "";
    string? _mask;
    CultureInfo? _culture;
    char _promptChar = '_';
    int _caret;
    int _selectionAnchor;
    int _selectionFocus;
    int _preEditCaret;
    PhysicalKey? _editKey;
    long _editVersion;

    /// <summary>Mask pattern; empty means the editor accepts any text.</summary>
    public string? Mask { get; set; }

    /// <summary>Culture used for the culture-sensitive mask literals. Defaults to <see cref="CultureInfo.CurrentCulture"/>.</summary>
    public CultureInfo Culture { get; set; } = CultureInfo.CurrentCulture;

    /// <summary>The formatted text, including literals and <see cref="PromptChar"/> for empty positions.</summary>
    public string? Text { get; set; }

    public string DefaultText { get; set; } = "";

    public Action<string>? TextChanged { get; set; }

    /// <summary>Character shown at unfilled positions. Defaults to <c>_</c>.</summary>
    public char PromptChar { get; set; } = '_';

    public string? Watermark { get; set; }

    public bool IsReadOnly { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>Every editable position is filled.</summary>
    public bool MaskFull => _provider.MaskFull;

    /// <summary>Every required position is filled (optional positions may stay empty).</summary>
    public bool MaskCompleted => _provider.MaskCompleted;

    /// <summary>Start of the formatted selection (clamped to the formatted text).</summary>
    public int SelectionStart => ClampIndex(Math.Min(LiveAnchor, LiveFocus));

    /// <summary>End of the formatted selection (clamped to the formatted text).</summary>
    public int SelectionEnd => ClampIndex(Math.Max(LiveAnchor, LiveFocus));

    /// <summary>Length of the formatted selection.</summary>
    public int SelectionLength => SelectionEnd - SelectionStart;

    /// <summary>Formatted index of the caret (the focus end of the selection), clamped to the formatted text.</summary>
    public int CaretIndex => ClampIndex(LiveFocus);

    InputElement? HeldInput =>
        _inputRef.Current as InputElement ?? Ref?.Current as InputElement ?? _inputElement;

    int LiveAnchor => HeldInput?.SelectionAnchor ?? _selectionAnchor;

    int LiveFocus => HeldInput?.SelectionFocus ?? _selectionFocus;

    protected override Node Build()
    {
        SyncProvider();

        string incoming = _text.Sync(Text, DefaultText, TextChanged);
        if (_provider.Mask.Length == 0)
            _formatted = incoming; // 没有掩码时按普通文本框处理
        else
        {
            if (!string.Equals(incoming, _formatted, StringComparison.Ordinal))
            {
                _provider.Set(incoming);
                _formatted = _provider.Text;
                _caret = _selectionAnchor = _selectionFocus = _formatted.Length;
            }
            else
            {
                _formatted = _provider.Text;
            }
        }

        // 每次输入（_editVersion）或格式化值变化后，把计算好的掩码选区写回输入元素；受控值重建会把插入点移到末尾，
        // 因此必须在这里恢复。
        long version = _editVersion;
        int anchor = _selectionAnchor;
        int focus = _selectionFocus;
        UseEffect(() =>
        {
            if (_provider.Mask.Length != 0 && HeldInput is { } element)
                element.SetSelectionRange(ClampIndex(anchor), ClampIndex(focus));
            return null;
        }, version, _formatted, anchor, focus);

        var input = new Input
        {
            Id = Id,
            Ref = Ref ?? _inputRef,
            AriaLabel = AriaLabel,
            Role = Role.TextBox,
            Value = _formatted,
            Placeholder = Watermark,
            ReadOnly = IsReadOnly,
            Disabled = !IsEnabled,
            InputType = "text",
            OnInput = OnInput,
            OnKeyDown = OnEditKeyDown,
        };
        input.Attributes["part"] = "input";
        input.Attributes["aria-valuetext"] = _formatted;
        input.Attributes["data-mask-complete"] = MaskCompleted ? "true" : null;
        input.MergeAttributes(Attributes);

        var box = PartProps.Apply(new Div { Class = "aw-textbox aw-masked-textbox" }, Class, Style, null);
        box.Attributes["aria-disabled"] = input.Disabled ? "true" : null;
        box.Children.Add(input);
        return box;
    }

    /// <summary>记住 Backspace / Delete 按下前的插入点：删除字面量时据此决定清理哪一侧的可编辑位置。</summary>
    void OnEditKeyDown(KeyEvent e)
    {
        if (e.PhysicalKey is PhysicalKey.Backspace or PhysicalKey.Delete)
        {
            _preEditCaret = HeldInput?.SelectionFocus ?? _caret;
            _editKey = e.PhysicalKey;
        }
    }

    void OnInput(InputEvent e)
    {
        if (e.CurrentTarget is InputElement current) _inputElement = current;
        PhysicalKey? editKey = _editKey;
        _editKey = null;
        if (_provider.Mask.Length == 0)
        {
            _formatted = e.Value;
            _text.Set(_formatted);
            return;
        }

        // 输入框把文本插在插入点：与上一次的格式化值做差异比较，把删除 / 插入映射到掩码位置。
        string previous = _formatted;
        string next = e.Value;
        int prefix = 0;
        while (prefix < previous.Length && prefix < next.Length && previous[prefix] == next[prefix]) prefix++;
        int suffix = 0;
        while (suffix < previous.Length - prefix && suffix < next.Length - prefix
               && previous[previous.Length - 1 - suffix] == next[next.Length - 1 - suffix]) suffix++;

        int removedStart = prefix;
        int removedEnd = previous.Length - suffix;
        string inserted = next.Substring(prefix, next.Length - suffix - prefix);

        // 删除范围内的可编辑槽位：选区替换会一次清空多个位置。
        int removedEdit = _provider.RemoveRange(removedStart, removedEnd - removedStart);

        int caret;
        if (inserted.Length > 0)
        {
            int at = _provider.FindEditPositionFrom(prefix, true);
            // 输入框在末尾追加（例如焦点进入时插入点在末尾）而掩码后面已经没有槽位：从第一个空槽开始填。
            if (at < 0) at = _provider.FindEmptyEditPositionFrom(0, true);

            int lastWritten = -1;
            for (int i = 0; i < inserted.Length && at >= 0; i++)
            {
                char raw = inserted[i];
                if (raw == PromptChar)
                {
                    at = _provider.FindEditPositionFrom(at + 1, true);
                    continue;
                }
                int slot = _provider.GetEditPosition(at);
                if (_provider.Replace(raw, slot))
                {
                    lastWritten = at;
                    at = _provider.FindEditPositionFrom(at + 1, true);
                }
                // 与位置不匹配的字符被忽略，后续字符继续尝试同一槽位
            }

            if (lastWritten >= 0)
            {
                int nextEmpty = _provider.FindEmptyEditPositionFrom(lastWritten + 1, true);
                caret = nextEmpty >= 0 ? nextEmpty : _provider.FormattedLength;
            }
            else
            {
                caret = SnapForward(removedEdit > 0 ? removedStart : prefix);
            }
        }
        else if (removedEdit > 0)
        {
            // 删除 / 替换了可编辑位置：插入点落在第一个被清空的位置。
            caret = SnapForward(removedStart);
        }
        else if (removedEnd - removedStart == 1)
        {
            // 只删除了一个字面量（例如电话号码里的 ")"）：按方向清理相邻的可编辑位置。
            bool backspace = editKey == PhysicalKey.Backspace
                || (editKey is null && _preEditCaret >= removedEnd);
            int target = backspace
                ? _provider.FindEditPositionFrom(Math.Max(0, removedStart - 1), forward: false)
                : _provider.FindEditPositionFrom(removedEnd, forward: true);
            if (target >= 0)
            {
                _provider.RemoveAt(_provider.GetEditPosition(target));
                caret = target;
            }
            else
            {
                caret = SnapForward(removedStart);
            }
        }
        else
        {
            // 选中并删除了多个纯字面量：不额外清理可编辑位置。
            caret = SnapForward(removedStart);
        }

        _formatted = _provider.Text;
        caret = ClampIndex(caret);
        _caret = _selectionAnchor = _selectionFocus = caret;
        _editVersion++;
        _text.Set(_formatted);
        Invalidate();
    }

    /// <summary>Nearest editable formatted index at or after <paramref name="formattedIndex"/>; the end of the mask when there is none.</summary>
    int SnapForward(int formattedIndex)
    {
        int at = _provider.FindEditPositionFrom(formattedIndex, forward: true);
        return at >= 0 ? at : _provider.FormattedLength;
    }

    int ClampIndex(int index) => Math.Max(0, Math.Min(index, _formatted.Length));

    void SyncProvider()
    {
        string mask = Mask ?? "";
        CultureInfo culture = Culture ?? CultureInfo.CurrentCulture;
        if (string.Equals(mask, _mask, StringComparison.Ordinal)
            && _culture is not null && _culture.Equals(culture)
            && PromptChar == _promptChar) return;

        _mask = mask;
        _culture = culture;
        _promptChar = PromptChar;
        _provider = new MaskedTextProvider(mask, culture, PromptChar);
        _formatted = "";
        _caret = _selectionAnchor = _selectionFocus = 0;
        _editVersion++;
    }
}
