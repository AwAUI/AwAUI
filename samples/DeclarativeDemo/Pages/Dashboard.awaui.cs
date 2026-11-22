using System;
using System.Collections.Generic;
using AwAUI.Icons.Lucide;
using AwAUI.Shadcn;
using AwAUI.UI;

namespace DeclarativeDemo.Pages;

// Code-behind for Dashboard.awaui. State fields survive rebuilds; the markup reads them directly.
public partial class Dashboard
{
    const double Attainment = 58;

    static readonly DateTime _today = DateTime.Today;

    static readonly string[] _chartLabels =
        ["Sep 29", "Oct 6", "Oct 13", "Oct 20", "Oct 27", "Nov 3", "Nov 10", "Nov 17"];

    static readonly IReadOnlyList<ChartSeries> _chartSeries =
    [
        new("revenue", [42.0, 48.0, 45.0, 57.0, 61.0, 55.0, 68.0, 74.0]),
        new("target", [40.0, 44.0, 48.0, 52.0, 56.0, 60.0, 64.0, 68.0]),
    ];

    static readonly IReadOnlyDictionary<string, ChartConfig> _chartConfig =
        new Dictionary<string, ChartConfig>
        {
            ["revenue"] = new("Revenue", "var(--chart-1)"),
            ["target"] = new("Target", "var(--chart-3)"),
        };

    readonly State<IReadOnlyList<double>> _goal = new(new double[] { 55, 75 });
    readonly State<DateTime?> _reviewDate = new(_today.AddDays(14));

    /// <summary>Receives the toaster so page actions can raise Sonner-style toasts.</summary>
    public ToastStore? Toasts { get; set; }

    /// <summary>Raises a page id to the shell. This is a parent callback, declared like any Prop.</summary>
    public Action<string>? OnNavigate { get; set; }

    /// <summary>Opens the New project flow in the shell.</summary>
    public Action? OnNewProject { get; set; }

    double GoalLow => _goal.Value[0];
    double GoalHigh => _goal.Value[1];
    string GoalText => FormattableString.Invariant($"{GoalLow:0}–{GoalHigh:0}%");
    bool GoalOnTrack => Attainment >= GoalLow && Attainment <= GoalHigh;
    string GoalStatus => GoalOnTrack ? "On track" : "Outside band";
    double GoalProgress => Math.Clamp(Attainment / 100.0, 0, 1);
    string AttainmentText => FormattableString.Invariant(
        $"Actual attainment {Attainment:0}%, target band {GoalText}.");

    void OnGoalChanged(IReadOnlyList<double> values) => _goal.Value = values;

    void OnReviewDate(DateTime? date) => _reviewDate.Value = date;

    void NewProject() => OnNewProject?.Invoke();

    void OpenProjects() => OnNavigate?.Invoke("projects");

    void OpenSchedule() => OnNavigate?.Invoke("schedule");

    void OpenSettings() => OnNavigate?.Invoke("settings");

    void ViewActivity() => Toasts?.Show(new ToastOptions
    {
        Title = "Activity feed",
        Description = "The full audit log lives in Reports.",
        Variant = ToastVariant.Info,
    });
}
