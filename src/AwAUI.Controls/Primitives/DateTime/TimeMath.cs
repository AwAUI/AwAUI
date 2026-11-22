using System;
using System.Collections.Generic;
using System.Globalization;

namespace AwAUI.Controls.Primitives;

/// <summary>The hour cycle used to display a time of day.</summary>
public enum ClockFormat : byte
{
    /// <summary>12-hour clock with an AM/PM designator.</summary>
    TwelveHour,

    /// <summary>24-hour clock with hours 00–23.</summary>
    TwentyFourHour,
}

/// <summary>
/// Pure time-of-day helpers: wrapping, snapping to a minute/second increment, 12/24-hour conversion, formatting, and
/// parsing. Times are represented as <see cref="TimeSpan"/> values within one day.
/// </summary>
public static class TimeMath
{
    static readonly TimeSpan s_day = TimeSpan.FromDays(1);

    /// <summary>Wraps a time into the range [00:00, 24:00).</summary>
    public static TimeSpan Normalize(TimeSpan value)
    {
        if (value >= TimeSpan.Zero && value < s_day) return value;
        long ticks = value.Ticks % s_day.Ticks;
        if (ticks < 0) ticks += s_day.Ticks;
        return TimeSpan.FromTicks(ticks);
    }

    /// <summary>Wraps a time into [00:00, 24:00) and floors its minutes to the given increment.</summary>
    public static TimeSpan SnapMinutes(TimeSpan value, int increment)
    {
        int step = NormalizeIncrement(increment, 60);
        if (step == 1) return Normalize(value);
        var time = Normalize(value);
        int totalMinutes = time.Hours * 60 + time.Minutes;
        int snapped = totalMinutes / step * step;
        return new TimeSpan(snapped / 60, snapped % 60, 0);
    }

    /// <summary>Wraps a time into [00:00, 24:00) and floors its seconds to the given increment.</summary>
    public static TimeSpan SnapSeconds(TimeSpan value, int increment)
    {
        int step = NormalizeIncrement(increment, 60);
        var time = Normalize(value);
        if (step == 1) return new TimeSpan(time.Hours, time.Minutes, time.Seconds);
        int totalSeconds = time.Hours * 3600 + time.Minutes * 60 + time.Seconds;
        int snapped = totalSeconds / step * step;
        return TimeSpan.FromSeconds(snapped);
    }

    /// <summary>Adds minutes and wraps back into the same day.</summary>
    public static TimeSpan AddMinutes(TimeSpan value, int minutes) => Normalize(value + TimeSpan.FromMinutes(minutes));

    /// <summary>Adds seconds and wraps back into the same day.</summary>
    public static TimeSpan AddSeconds(TimeSpan value, int seconds) => Normalize(value + TimeSpan.FromSeconds(seconds));

    /// <summary>Splits a time into hour, minute, and second components.</summary>
    public static void Split(TimeSpan value, out int hour, out int minute, out int second)
    {
        var time = Normalize(value);
        hour = time.Hours;
        minute = time.Minutes;
        second = time.Seconds;
    }

    /// <summary>Composes a normalized time from 24-hour components.</summary>
    public static TimeSpan Compose(int hour, int minute, int second = 0)
    {
        long ticks = (long)hour * TimeSpan.TicksPerHour + (long)minute * TimeSpan.TicksPerMinute + (long)second * TimeSpan.TicksPerSecond;
        return Normalize(TimeSpan.FromTicks(ticks));
    }

    /// <summary>Converts a 24-hour hour into a 12-hour hour (12 stays 12) and reports whether it is PM.</summary>
    public static int ToTwelveHour(int hour24, out bool isPm)
    {
        int hour = ((hour24 % 24) + 24) % 24;
        isPm = hour >= 12;
        int twelve = hour % 12;
        return twelve == 0 ? 12 : twelve;
    }

    /// <summary>Converts a 12-hour hour and designator into a 24-hour hour.</summary>
    public static int ToTwentyFourHour(int hour12, bool isPm)
    {
        int hour = hour12 % 12;
        return isPm ? hour + 12 : hour;
    }

    /// <summary>Minute values offered by a picker, from 0 to less than 60.</summary>
    public static IReadOnlyList<int> MinuteValues(int increment)
    {
        int step = NormalizeIncrement(increment, 60);
        var values = new List<int>();
        for (int value = 0; value < 60; value += step) values.Add(value);
        return values;
    }

    /// <summary>Second values offered by a picker, from 0 to less than 60.</summary>
    public static IReadOnlyList<int> SecondValues(int increment)
    {
        int step = NormalizeIncrement(increment, 60);
        var values = new List<int>();
        for (int value = 0; value < 60; value += step) values.Add(value);
        return values;
    }

    /// <summary>Formats a time using the culture's patterns or an explicit custom format.</summary>
    public static string Format(TimeSpan value, CultureInfo culture, ClockFormat clock, bool showSeconds, string? format = null)
    {
        if (culture is null) throw new ArgumentNullException(nameof(culture));
        string pattern = string.IsNullOrEmpty(format)
            ? (clock == ClockFormat.TwelveHour
                ? (showSeconds ? "h:mm:ss tt" : "h:mm tt")
                : (showSeconds ? "HH:mm:ss" : "HH:mm"))
            : format!;
        var date = DateTime.MinValue.Add(Normalize(value));
        try
        {
            return date.ToString(pattern, culture);
        }
        catch (FormatException)
        {
            return date.ToString(clock == ClockFormat.TwelveHour ? "h:mm tt" : "HH:mm", culture);
        }
    }

    /// <summary>
    /// Parses a time of day with the culture's short and long patterns plus clock-specific fallbacks. Returns false for
    /// empty or unrecognized text instead of throwing.
    /// </summary>
    public static bool TryParse(string? text, CultureInfo culture, ClockFormat clock, bool showSeconds, out TimeSpan value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (culture is null) culture = CultureInfo.CurrentCulture;
        string trimmed = text!.Trim();

        var formats = new List<string>
        {
            culture.DateTimeFormat.ShortTimePattern,
            culture.DateTimeFormat.LongTimePattern,
            "H:mm", "HH:mm", "H:mm:ss", "HH:mm:ss",
            "h:mm tt", "hh:mm tt", "h:mm:ss tt", "hh:mm:ss tt",
            "h:mmtt", "hh:mmtt", "h:mm:sstt", "hh:mm:sstt",
        };
        if (!showSeconds)
        {
            formats.Add("H:mm");
            formats.Add("HH:mm");
        }

        for (int i = 0; i < formats.Count; i++)
        {
            if (DateTime.TryParseExact(trimmed, formats[i], culture, DateTimeStyles.AllowWhiteSpaces, out var exact))
            {
                value = Normalize(exact.TimeOfDay);
                return true;
            }
        }

        if (DateTime.TryParse(trimmed, culture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.NoCurrentDateDefault, out var parsed))
        {
            value = Normalize(parsed.TimeOfDay);
            return true;
        }
        return false;
    }

    static int NormalizeIncrement(int increment, int maximum)
    {
        if (increment < 1) return 1;
        return increment > maximum ? maximum : increment;
    }
}
