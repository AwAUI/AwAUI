using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AwAUI.Controls.Primitives;

/// <summary>
/// Character-mask model shared by masked editors, following the familiar Windows Forms / Avalonia
/// <c>MaskedTextProvider</c> shape. The mask uses the usual placeholders:
/// <list type="bullet">
/// <item><c>0</c> required digit, <c>9</c> / <c>#</c> optional digit;</item>
/// <item><c>L</c> / <c>?</c> required / optional ASCII letter;</item>
/// <item><c>A</c> / <c>a</c> required / optional ASCII letter or digit;</item>
/// <item><c>&amp;</c> / <c>C</c> required / optional any non-control character;</item>
/// <item><c>&gt;</c> / <c>&lt;</c> uppercase / lowercase the characters that follow, <c>|</c> makes the next position optional.</item>
/// </list>
/// Every other character is a literal and <c>\</c> escapes one. The culture-sensitive literals <c>.</c>, <c>,</c>,
/// <c>:</c>, <c>/</c> and <c>$</c> expand at construction time to <see cref="Culture"/>'s number decimal / group
/// separators, time separator, date separator and currency symbol (a backslash escape always wins).
/// <para>
/// Values are kept per mask position; <see cref="Text"/> renders the formatted value, using <see cref="PromptChar"/> for
/// empty editable positions. Formatted indices (used by <see cref="GetEditPosition"/>, <see cref="FindEditPositionFrom"/>
/// and <see cref="RemoveRange"/>) address the full mask layout, including literals and prompt characters, independent of
/// <see cref="IncludeLiterals"/> / <see cref="IncludePrompt"/>.
/// </para>
/// </summary>
public sealed class MaskedTextProvider
{
    const string SpecialMaskCharacters = "09#L?Aa&C\\<>|";

    readonly MaskPosition[] _positions;
    readonly char[] _values;
    readonly int[] _formattedStart;
    readonly int[] _positionAtFormatted;
    readonly int[] _editablePositions;
    readonly CultureInfo _culture;
    char _promptChar;

    /// <summary>Creates a provider for <paramref name="mask"/> using <paramref name="culture"/> (defaults to the current culture).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="mask"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="promptChar"/> is null or a mask character.</exception>
    public MaskedTextProvider(string mask, CultureInfo? culture = null, char promptChar = '_')
    {
        if (mask is null) throw new ArgumentNullException(nameof(mask));
        ValidatePromptChar(promptChar);
        Mask = mask;
        _culture = culture ?? CultureInfo.CurrentCulture;
        _promptChar = promptChar;
        _positions = ParseMask(mask, _culture);
        _values = new char[_positions.Length];

        _formattedStart = new int[_positions.Length];
        var editable = new List<int>();
        var positionAtFormatted = new List<int>();
        for (int i = 0; i < _positions.Length; i++)
        {
            _formattedStart[i] = positionAtFormatted.Count;
            if (_positions[i].Editable)
            {
                editable.Add(i);
                positionAtFormatted.Add(i);
            }
            else
            {
                for (int k = 0; k < _positions[i].Literal.Length; k++) positionAtFormatted.Add(-1);
            }
        }
        _editablePositions = editable.ToArray();
        _positionAtFormatted = positionAtFormatted.ToArray();
    }

    /// <summary>The mask pattern; empty means there are no editable positions.</summary>
    public string Mask { get; }

    /// <summary>The culture used to expand the culture-sensitive mask literals.</summary>
    public CultureInfo Culture => _culture;

    /// <summary>Character shown at unfilled editable positions when <see cref="IncludePrompt"/> is true.</summary>
    public char PromptChar
    {
        get => _promptChar;
        set
        {
            ValidatePromptChar(value);
            _promptChar = value;
        }
    }

    /// <summary>Whether <see cref="Text"/> includes the literal characters of the mask. Defaults to true.</summary>
    public bool IncludeLiterals { get; set; } = true;

    /// <summary>Whether <see cref="Text"/> includes <see cref="PromptChar"/> for empty editable positions. Defaults to true.</summary>
    public bool IncludePrompt { get; set; } = true;

    /// <summary>The formatted text: literals and, for empty positions, <see cref="PromptChar"/>.</summary>
    public string Text
    {
        get
        {
            var sb = new StringBuilder(_positionAtFormatted.Length);
            for (int i = 0; i < _positions.Length; i++)
            {
                if (_positions[i].Editable)
                {
                    if (_values[i] != '\0') sb.Append(_values[i]);
                    else if (IncludePrompt) sb.Append(_promptChar);
                }
                else if (IncludeLiterals)
                {
                    sb.Append(_positions[i].Literal);
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>Every required editable position is filled (optional positions may stay empty).</summary>
    public bool MaskCompleted
    {
        get
        {
            foreach (int i in _editablePositions)
                if (_positions[i].Required && _values[i] == '\0') return false;
            return true;
        }
    }

    /// <summary>Every editable position is filled.</summary>
    public bool MaskFull
    {
        get
        {
            foreach (int i in _editablePositions)
                if (_values[i] == '\0') return false;
            return true;
        }
    }

    /// <summary>No editable position is filled.</summary>
    public bool IsEmpty
    {
        get
        {
            foreach (int i in _editablePositions)
                if (_values[i] != '\0') return false;
            return true;
        }
    }

    /// <summary>Length of <see cref="Text"/>, taking <see cref="IncludeLiterals"/> / <see cref="IncludePrompt"/> into account.</summary>
    public int Length => Text.Length;

    /// <summary>Length of the full mask layout (literals and prompt positions always included); the range of formatted indices.</summary>
    internal int FormattedLength => _positionAtFormatted.Length;

    /// <summary>Clears all values.</summary>
    public void Clear() => Array.Clear(_values, 0, _values.Length);

    /// <summary>
    /// Clears the provider and fills the editable positions sequentially from <paramref name="input"/>. A fully formatted
    /// input (matching literals, for example <c>(555) 123-4567</c>) is assigned position by position; otherwise characters
    /// that do not fit the current position are ignored.
    /// </summary>
    public void Set(string? input)
    {
        Clear();
        if (string.IsNullOrEmpty(input)) return;
        if (TrySetFormatted(input!)) return;

        int p = 0;
        for (int i = 0; i < input!.Length && p < _positions.Length; i++)
        {
            while (p < _positions.Length && !_positions[p].Editable) p++;
            if (p >= _positions.Length) break;
            char raw = input[i];
            if (raw == _promptChar) { p++; continue; }
            char c = Shift(_positions[p], raw);
            if (Valid(_positions[p].Kind, c))
            {
                _values[p] = c;
                p++;
            }
        }
    }

    /// <summary>
    /// Replaces the value at mask position <paramref name="position"/> (an index into the mask layout, literals included).
    /// Passing <see cref="PromptChar"/> clears the position. Returns false when the position is not editable or the
    /// character does not match the position.
    /// </summary>
    public bool Replace(char input, int position)
    {
        if (position < 0 || position >= _positions.Length) return false;
        MaskPosition pos = _positions[position];
        if (!pos.Editable) return false;
        if (input == _promptChar)
        {
            _values[position] = '\0';
            return true;
        }
        char c = Shift(pos, input);
        if (!Valid(pos.Kind, c)) return false;
        _values[position] = c;
        return true;
    }

    /// <summary>
    /// Inserts <paramref name="input"/> at the first editable position at or after <paramref name="position"/>, shifting the
    /// following values right by one editable slot. Returns false when there is no editable position or no room left.
    /// </summary>
    public bool InsertAt(char input, int position)
    {
        if (position < 0 || position > _positions.Length) return false;
        int target = NextEditablePosition(position);
        if (target < 0) return false;
        if (input == _promptChar) return false;
        char c = Shift(_positions[target], input);
        if (!Valid(_positions[target].Kind, c)) return false;
        if (_values[target] == '\0')
        {
            _values[target] = c;
            return true;
        }

        int k = EditableIndexOf(target);
        int empty = k;
        while (empty < _editablePositions.Length && _values[_editablePositions[empty]] != '\0') empty++;
        if (empty == _editablePositions.Length) return false; // no room to shift
        for (int m = empty; m > k; m--) _values[_editablePositions[m]] = _values[_editablePositions[m - 1]];
        _values[target] = c;
        return true;
    }

    /// <summary>Clears the editable position at mask position <paramref name="position"/>. Returns false for a literal position.</summary>
    public bool RemoveAt(int position)
    {
        if (position < 0 || position >= _positions.Length || !_positions[position].Editable) return false;
        _values[position] = '\0';
        return true;
    }

    /// <summary>
    /// Clears every editable position whose formatted index falls in <c>[start, start + length)</c>. Returns the number of
    /// editable positions in that range (whether or not they held a value), so callers can tell a real mask edit from a
    /// deletion that only removed literals.
    /// </summary>
    public int RemoveRange(int start, int length)
    {
        if (length <= 0) return 0;
        int end = start + length;
        int removed = 0;
        foreach (int i in _editablePositions)
        {
            int at = _formattedStart[i];
            if (at >= start && at < end)
            {
                _values[i] = '\0';
                removed++;
            }
        }
        return removed;
    }

    /// <summary>
    /// Returns the editable mask position index containing the formatted index <paramref name="formattedIndex"/>, or -1
    /// when the index is a literal or outside the layout.
    /// </summary>
    public int GetEditPosition(int formattedIndex)
    {
        if (formattedIndex < 0 || formattedIndex >= _positionAtFormatted.Length) return -1;
        return _positionAtFormatted[formattedIndex];
    }

    /// <summary>Returns the formatted index at which mask position <paramref name="position"/> starts, or -1 when out of range.</summary>
    public int GetFormattedIndex(int position)
    {
        if (position < 0 || position >= _positions.Length) return -1;
        return _formattedStart[position];
    }

    /// <summary>
    /// Returns the formatted index of the nearest editable position at or after (when <paramref name="forward"/> is true)
    /// or at or before <paramref name="formattedIndex"/>, or -1 when there is none.
    /// </summary>
    public int FindEditPositionFrom(int formattedIndex, bool forward)
    {
        if (_positionAtFormatted.Length == 0) return -1;
        if (forward)
        {
            for (int f = Math.Max(0, formattedIndex); f < _positionAtFormatted.Length; f++)
                if (_positionAtFormatted[f] >= 0) return f;
        }
        else
        {
            for (int f = Math.Min(formattedIndex, _positionAtFormatted.Length - 1); f >= 0; f--)
                if (_positionAtFormatted[f] >= 0) return f;
        }
        return -1;
    }

    /// <summary>Returns the formatted index of the nearest empty editable position in the given direction, or -1.</summary>
    internal int FindEmptyEditPositionFrom(int formattedIndex, bool forward)
    {
        if (_positionAtFormatted.Length == 0) return -1;
        if (forward)
        {
            for (int f = Math.Max(0, formattedIndex); f < _positionAtFormatted.Length; f++)
                if (_positionAtFormatted[f] >= 0 && _values[_positionAtFormatted[f]] == '\0') return f;
        }
        else
        {
            for (int f = Math.Min(formattedIndex, _positionAtFormatted.Length - 1); f >= 0; f--)
                if (_positionAtFormatted[f] >= 0 && _values[_positionAtFormatted[f]] == '\0') return f;
        }
        return -1;
    }

    bool TrySetFormatted(string input)
    {
        if (input.Length != _positionAtFormatted.Length) return false;
        for (int i = 0; i < _positions.Length; i++)
        {
            if (_positions[i].Editable) continue;
            string literal = _positions[i].Literal;
            int start = _formattedStart[i];
            for (int k = 0; k < literal.Length; k++)
                if (input[start + k] != literal[k]) return false;
        }
        foreach (int i in _editablePositions)
        {
            char raw = input[_formattedStart[i]];
            if (raw == _promptChar) continue;
            char c = Shift(_positions[i], raw);
            if (Valid(_positions[i].Kind, c)) _values[i] = c;
        }
        return true;
    }

    int NextEditablePosition(int position)
    {
        for (int i = Math.Max(0, position); i < _positions.Length; i++)
            if (_positions[i].Editable) return i;
        return -1;
    }

    int EditableIndexOf(int position)
    {
        for (int i = 0; i < _editablePositions.Length; i++)
            if (_editablePositions[i] == position) return i;
        return -1;
    }

    static void ValidatePromptChar(char promptChar)
    {
        if (promptChar == '\0' || SpecialMaskCharacters.IndexOf(promptChar) >= 0)
            throw new ArgumentException("The prompt character cannot be a mask character.", nameof(promptChar));
    }

    static string Expand(string? separator, char maskChar) =>
        string.IsNullOrEmpty(separator) ? maskChar.ToString() : separator!;

    static MaskPosition[] ParseMask(string mask, CultureInfo culture)
    {
        var positions = new List<MaskPosition>(mask.Length);
        char shift = '\0';
        bool optional = false;
        for (int i = 0; i < mask.Length; i++)
        {
            char c = mask[i];
            if (c == '\\' && i + 1 < mask.Length)
            {
                positions.Add(MaskPosition.FromLiteral(mask[++i].ToString()));
                continue;
            }
            // 大小写指令与可选指令不占位置，作用于其后的槽位
            if (c == '>') { shift = '>'; continue; }
            if (c == '<') { shift = '<'; continue; }
            if (c == '|') { optional = true; continue; }
            bool isOptional = optional;
            optional = false;
            switch (c)
            {
                case '0': positions.Add(MaskPosition.FromKind('0', required: !isOptional, shift)); break;
                case '9':
                case '#': positions.Add(MaskPosition.FromKind('9', required: false, shift)); break;
                case 'L': positions.Add(MaskPosition.FromKind('L', required: !isOptional, shift)); break;
                case '?': positions.Add(MaskPosition.FromKind('L', required: false, shift)); break;
                case 'A': positions.Add(MaskPosition.FromKind('A', required: !isOptional, shift)); break;
                case 'a': positions.Add(MaskPosition.FromKind('A', required: false, shift)); break;
                case '&': positions.Add(MaskPosition.FromKind('&', required: !isOptional, shift)); break;
                case 'C': positions.Add(MaskPosition.FromKind('&', required: false, shift)); break;
                // 区域文化分隔符：转义优先（上面的 '\' 分支），这里才展开
                case '.': positions.Add(MaskPosition.FromLiteral(Expand(culture.NumberFormat.NumberDecimalSeparator, c))); break;
                case ',': positions.Add(MaskPosition.FromLiteral(Expand(culture.NumberFormat.NumberGroupSeparator, c))); break;
                case ':': positions.Add(MaskPosition.FromLiteral(Expand(culture.DateTimeFormat.TimeSeparator, c))); break;
                case '/': positions.Add(MaskPosition.FromLiteral(Expand(culture.DateTimeFormat.DateSeparator, c))); break;
                case '$': positions.Add(MaskPosition.FromLiteral(Expand(culture.NumberFormat.CurrencySymbol, c))); break;
                default: positions.Add(MaskPosition.FromLiteral(c.ToString())); break;
            }
        }
        return positions.ToArray();
    }

    /// <summary>按位置的 <c>&gt;</c> / <c>&lt;</c> 指令转换输入字符的大小写；数字不受影响。</summary>
    static char Shift(in MaskPosition position, char c) => position.CaseShift switch
    {
        '>' => char.ToUpperInvariant(c),
        '<' => char.ToLowerInvariant(c),
        _ => c,
    };

    static bool Valid(char kind, char c) => kind switch
    {
        '0' or '9' => c >= '0' && c <= '9',
        'L' => IsAsciiLetter(c),
        'A' => IsAsciiLetter(c) || (c >= '0' && c <= '9'),
        _ => !char.IsControl(c),
    };

    static bool IsAsciiLetter(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

    readonly struct MaskPosition
    {
        MaskPosition(bool editable, string literal, char kind, bool required, char caseShift)
        {
            Editable = editable;
            Literal = literal;
            Kind = kind;
            Required = required;
            CaseShift = caseShift;
        }

        public bool Editable { get; }
        public string Literal { get; }
        public char Kind { get; }
        public bool Required { get; }

        /// <summary><c>&gt;</c> 或 <c>&lt;</c>：输入到此位置时转换大小写；<c>'\0'</c> 表示不转换。</summary>
        public char CaseShift { get; }

        public static MaskPosition FromLiteral(string literal) => new(false, literal, '\0', false, '\0');

        public static MaskPosition FromKind(char kind, bool required, char caseShift) => new(true, "", kind, required, caseShift);
    }
}
