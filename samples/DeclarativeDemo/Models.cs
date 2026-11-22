using System;
using System.Collections.Generic;
using System.Linq;
using AwAUI.Icons.Lucide;
using AwAUI.Svg;

namespace DeclarativeDemo;

/// <summary>One entry in the application sidebar.</summary>
public sealed record NavItem(string Id, string Title, SvgDocument Icon, string? Badge = null);

/// <summary>A titled group of navigation entries.</summary>
public sealed record NavSection(string Title, IReadOnlyList<NavItem> Items);

/// <summary>A row of the Projects data table.</summary>
public sealed record ProjectRow(
    string Id,
    string Name,
    string Owner,
    string Status,
    string Priority,
    DateTime Updated,
    int Budget,
    int Progress);

/// <summary>A recent activity entry on the dashboard.</summary>
public sealed record ActivityEntry(string Title, string Detail, string Time, string Tone);

/// <summary>A notification shown by the header bell menu.</summary>
public sealed record NotificationEntry(string Title, string Detail, string Time, bool Unread);

/// <summary>A teammate available in the project dialog.</summary>
public sealed record TeamMember(string Name, string Role, string Initials);

/// <summary>A top-row statistic.</summary>
public sealed record StatEntry(string Label, string Value, string Delta, bool Positive, string Hint, SvgDocument Icon);

/// <summary>A billing history row rendered with the Table parts.</summary>
public sealed record InvoiceRow(string Id, string Date, string Amount, string Status);

/// <summary>A key/label pair for the Projects toolbar dropdowns.</summary>
public sealed record ColumnChoice(string Key, string Label);

/// <summary>The value produced by the New project form.</summary>
public sealed record ProjectDraft(
    string Name,
    string Description,
    string Owner,
    string Priority,
    DateTime? StartDate,
    IReadOnlyList<string> Members,
    bool NotifyTeam);

/// <summary>Static sample content shared by the pages. Keeping it in one type makes the markup read like data.</summary>
public static class SampleData
{
    public static readonly IReadOnlyList<NavSection> NavSections =
    [
        new("Workspace",
        [
            new NavItem("dashboard", "Dashboard", LucideIcons.LayoutDashboard),
            new NavItem("projects", "Projects", LucideIcons.Folder, "18"),
            new NavItem("schedule", "Schedule", LucideIcons.Calendar),
        ]),
        new("Account",
        [
            new NavItem("settings", "Settings", LucideIcons.Settings),
        ]),
    ];

    public static readonly IReadOnlyList<NavItem> AllNavItems =
        NavSections.SelectMany(section => section.Items).ToArray();

    public static readonly IReadOnlyList<StatEntry> Stats =
    [
        new("Revenue", "$84,230", "+12.4%", true, "vs. last month", LucideIcons.CircleCheck),
        new("Active projects", "18", "+3", true, "two due this week", LucideIcons.GripVertical),
        new("Team utilisation", "76%", "-4.1%", false, "capacity is available", LucideIcons.Info),
        new("Open tickets", "37", "+6", false, "4 need attention", LucideIcons.Calendar),
    ];

    public static readonly IReadOnlyList<ProjectRow> Projects =
    [
        new("p-01", "Atlas design system", "Alex Rivera", "Active", "High", new DateTime(2026, 11, 21), 128000, 68),
        new("p-02", "Northwind refresh", "Mina Park", "Active", "Medium", new DateTime(2026, 11, 20), 64000, 44),
        new("p-03", "Billing migration", "Jonas Weber", "On hold", "High", new DateTime(2026, 11, 18), 210000, 31),
        new("p-04", "Mobile onboarding", "Priya Shah", "Planning", "Medium", new DateTime(2026, 11, 17), 45000, 12),
        new("p-05", "Search relevance", "Alex Rivera", "Active", "High", new DateTime(2026, 11, 16), 92000, 57),
        new("p-06", "Data warehouse", "Noah Kim", "Active", "Low", new DateTime(2026, 11, 14), 160000, 82),
        new("p-07", "Customer portal", "Mina Park", "Completed", "Medium", new DateTime(2026, 11, 11), 73000, 100),
        new("p-08", "Accessibility audit", "Priya Shah", "Planning", "High", new DateTime(2026, 11, 10), 28000, 8),
        new("p-09", "Partner API v2", "Jonas Weber", "Active", "Medium", new DateTime(2026, 11, 8), 88000, 63),
        new("p-10", "Pricing experiments", "Noah Kim", "On hold", "Low", new DateTime(2026, 11, 6), 39000, 26),
        new("p-11", "Notification centre", "Alex Rivera", "Active", "Medium", new DateTime(2026, 11, 4), 52000, 49),
        new("p-12", "Localisation sweep", "Priya Shah", "Completed", "Low", new DateTime(2026, 11, 2), 21000, 100),
        new("p-13", "Analytics events", "Mina Park", "Active", "High", new DateTime(2026, 10, 30), 110000, 71),
        new("p-14", "Design tokens", "Jonas Weber", "Planning", "Medium", new DateTime(2026, 10, 28), 36000, 15),
        new("p-15", "Support macros", "Noah Kim", "Active", "Low", new DateTime(2026, 10, 26), 18000, 54),
        new("p-16", "SSO hardening", "Alex Rivera", "Completed", "High", new DateTime(2026, 10, 23), 59000, 100),
        new("p-17", "Usage reporting", "Priya Shah", "On hold", "Medium", new DateTime(2026, 10, 21), 47000, 22),
        new("p-18", "Marketing site", "Mina Park", "Active", "Low", new DateTime(2026, 10, 19), 33000, 61),
    ];

    public static readonly IReadOnlyList<ActivityEntry> Activity =
    [
        new("Atlas design system shipped v2.4", "Alex moved 14 tasks to done", "12m ago", "success"),
        new("Mina commented on Northwind refresh", "“The new hero needs one more pass.”", "48m ago", "info"),
        new("Budget threshold reached", "Billing migration is at 92% of its budget", "2h ago", "warning"),
        new("Jonas joined Partner API v2", "Added as a reviewer on 6 files", "4h ago", "info"),
        new("Weekly report generated", "18 projects, 214 tasks, 76% utilisation", "Yesterday", "success"),
    ];

    public static readonly IReadOnlyList<NotificationEntry> Notifications =
    [
        new("Standup starts in 15 minutes", "Product · daily sync", "now", true),
        new("Atlas design system was approved", "Mina approved your review request", "18m", true),
        new("Invoice #2026-118 is due", "Billing · $12,400", "2h", false),
        new("A teammate mentioned you", "Jonas in Partner API v2", "4h", false),
    ];

    public static readonly IReadOnlyList<TeamMember> Team =
    [
        new("Alex Rivera", "Product design", "AR"),
        new("Mina Park", "Engineering", "MP"),
        new("Jonas Weber", "Platform", "JW"),
        new("Priya Shah", "Research", "PS"),
        new("Noah Kim", "Data", "NK"),
    ];

    public static readonly IReadOnlyList<string> Owners = Team.Select(member => member.Name).ToArray();

    public static readonly IReadOnlyList<InvoiceRow> Invoices =
    [
        new("INV-2026-118", "Nov 1, 2026", "$12,400.00", "Paid"),
        new("INV-2026-104", "Oct 1, 2026", "$12,400.00", "Paid"),
        new("INV-2026-091", "Sep 1, 2026", "$11,200.00", "Paid"),
        new("INV-2026-077", "Aug 1, 2026", "$11,200.00", "Refunded"),
    ];
}
