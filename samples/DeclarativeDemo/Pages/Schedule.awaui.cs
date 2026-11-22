using System;
using AwAUI.Shadcn;
using AwAUI.UI;

namespace DeclarativeDemo.Pages;

// Code-behind for Schedule.awaui: the range is controlled here so the summary can read it.
public partial class Schedule
{
    readonly State<DateTime?> _rangeStart = new(DateTime.Today.AddDays(3));
    readonly State<DateTime?> _rangeEnd = new(DateTime.Today.AddDays(9));

    /// <summary>Toaster supplied by the shell.</summary>
    public ToastStore? Toasts { get; set; }

    bool HasCompleteRange => _rangeStart.Value is not null && _rangeEnd.Value is not null;

    int RangeDays => HasCompleteRange
        ? (_rangeEnd.Value!.Value.Date - _rangeStart.Value!.Value.Date).Days + 1
        : 0;

    string RangeText => HasCompleteRange
        ? FormattableString.Invariant(
            $"{_rangeStart.Value!.Value:MMM d} – {_rangeEnd.Value!.Value:MMM d, yyyy}")
        : "";

    void OnRangeChanged(DateTime? start, DateTime? end)
    {
        _rangeStart.Value = start;
        _rangeEnd.Value = end;
    }

    void ClearRange()
    {
        _rangeStart.Value = null;
        _rangeEnd.Value = null;
    }

    void NotifyTeam()
    {
        Toasts?.Show(new ToastOptions
        {
            Title = HasCompleteRange ? "Range shared" : "Pick a range first",
            Description = HasCompleteRange ? RangeText : "Choose a start and end date, then share again.",
            Variant = HasCompleteRange ? ToastVariant.Success : ToastVariant.Warning,
        });
    }

    static bool IsDateDisabled(DateTime date) =>
        date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
        || (date.Month == 11 && date.Day == 25)
        || (date.Month == 12 && date.Day >= 24)
        || (date.Month == 1 && date.Day <= 2);

    static string Availability(string name) => name switch
    {
        "Mina Park" => "Partial",
        "Noah Kim" => "Away",
        _ => "Available",
    };

    static BadgeVariant AvailabilityVariant(string name) => name switch
    {
        "Mina Park" => BadgeVariant.Outline,
        "Noah Kim" => BadgeVariant.Destructive,
        _ => BadgeVariant.Secondary,
    };
}
