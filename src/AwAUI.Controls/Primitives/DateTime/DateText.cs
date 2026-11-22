using System;
using System.Globalization;

namespace AwAUI.Controls.Primitives;

/// <summary>
/// Culture-aware date formatting and parsing used by the date pickers. Parsing is deliberately forgiving: invalid or
/// empty text returns <c>false</c> instead of throwing, so a text field can keep the user's input and fall back to the
/// last valid date.
/// </summary>
public static class DateText
{
    /// <summary>The format strings accepted when no explicit format is supplied.</summary>
    static readonly string[] s_fallbackFormats =
    {
        "d", "D", "yyyy-MM-dd", "yyyy/MM/dd", "yyyy.MM.dd",
        "M/d/yyyy", "MM/dd/yyyy", "d/M/yyyy", "dd/MM/yyyy", "d.M.yyyy", "dd.MM.yyyy",
    };

    /// <summary>Formats a date with <paramref name="format"/>, or the culture's short date pattern when it is null or empty.</summary>
    public static string Format(DateTime value, CultureInfo culture, string? format = null)
    {
        if (culture is null) throw new ArgumentNullException(nameof(culture));
        string pattern = string.IsNullOrEmpty(format) ? culture.DateTimeFormat.ShortDatePattern : format!;
        try
        {
            return value.Date.ToString(pattern, culture);
        }
        catch (FormatException)
        {
            return value.Date.ToString(culture.DateTimeFormat.ShortDatePattern, culture);
        }
    }

    /// <summary>Formats a nullable date; null becomes an empty string.</summary>
    public static string Format(DateTime? value, CultureInfo culture, string? format = null) =>
        value is { } date ? Format(date, culture, format) : "";

    /// <summary>
    /// Parses a date using the culture's short and long patterns plus a few unambiguous fallbacks. Only the date part
    /// is retained.
    /// </summary>
    public static bool TryParse(string? text, CultureInfo culture, out DateTime value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (culture is null) culture = CultureInfo.CurrentCulture;

        string trimmed = text!.Trim();
        if (DateTime.TryParse(trimmed, culture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
        {
            value = parsed.Date;
            return true;
        }
        for (int i = 0; i < s_fallbackFormats.Length; i++)
        {
            if (DateTime.TryParseExact(trimmed, s_fallbackFormats[i], culture, DateTimeStyles.AllowWhiteSpaces, out parsed))
            {
                value = parsed.Date;
                return true;
            }
        }
        return false;
    }

    /// <summary>Parses a date with one exact format, returning false on any mismatch.</summary>
    public static bool TryParseExact(string? text, CultureInfo culture, string? format, out DateTime value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (culture is null) culture = CultureInfo.CurrentCulture;
        string pattern = string.IsNullOrEmpty(format) ? culture.DateTimeFormat.ShortDatePattern : format!;
        try
        {
            if (DateTime.TryParseExact(text!.Trim(), pattern, culture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
            {
                value = parsed.Date;
                return true;
            }
        }
        catch (FormatException)
        {
            // 无效的自定义格式无法匹配；调用方回退到上一个有效值。
        }
        return false;
    }

    /// <summary>Whether two values fall on the same calendar day.</summary>
    public static bool IsSameDay(DateTime left, DateTime right) => left.Date == right.Date;
}
