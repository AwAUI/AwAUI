using System;
using System.Collections.Generic;
using System.Linq;
using AwAUI.Controls.Primitives;
using AwAUI.Icons.Lucide;
using AwAUI.Shadcn;
using AwAUI.Svg;
using AwAUI.UI;

namespace DeclarativeDemo;

// Code-behind for App.awaui. It is a partial of the generated class: do not repeat the base class.
public partial class App
{
    readonly State<string> _pageId = new("dashboard");
    readonly State<string> _query = new("");
    readonly State<bool> _dark = new(false);
    readonly State<bool> _mobileOpen = new(false);
    readonly State<bool> _dialogRequested = new(false);
    readonly State<List<NotificationEntry>> _notifications = new(SampleData.Notifications.ToList());
    readonly ToastStore _toasts = new();

    /// <summary>Initial theme from the command line (--dark).</summary>
    public bool Dark { get; set; }

    /// <summary>Initial page id from the command line (--page).</summary>
    public string? Page { get; set; }

    public ToastStore Toasts => _toasts;

    protected override void OnMount()
    {
        _dark.Value = Dark;
        ApplyTheme();
        if (Page == "project-dialog")
        {
            _pageId.Value = "projects";
            _dialogRequested.Value = true;
        }
        else if (!string.IsNullOrEmpty(Page)) _pageId.Value = Page!;
    }

    string CurrentTitle => _pageId.Value switch
    {
        "projects" => "Projects",
        "schedule" => "Schedule",
        "settings" => "Settings",
        _ => "Dashboard",
    };

    SvgDocument ThemeIcon => _dark.Value ? LucideIcons.Sun : LucideIcons.Moon;

    int UnreadCount => _notifications.Value.Count(note => note.Unread);

    bool HasMatches
    {
        get
        {
            string query = _query.Value.Trim();
            return query.Length == 0
                || SampleData.AllNavItems.Any(item => item.Title.Contains(query, StringComparison.OrdinalIgnoreCase));
        }
    }

    IEnumerable<NavItem> FilterItems(NavSection section)
    {
        string query = _query.Value.Trim();
        return query.Length == 0
            ? section.Items
            : section.Items.Where(item => item.Title.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    void Navigate(string id)
    {
        _pageId.Value = id;
        _query.Value = "";
        _mobileOpen.Value = false;
        if (id != "projects") _dialogRequested.Value = false;
    }

    void NewProject()
    {
        Navigate("projects");
        _dialogRequested.Value = true;
    }

    void OpenDashboard() => Navigate("dashboard");
    void OpenSettings() => Navigate("settings");
    void OpenMobile() => _mobileOpen.Value = true;

    void ToggleTheme()
    {
        _dark.Value = !_dark.Value;
        ApplyTheme();
    }

    void ApplyTheme()
    {
        if (Root is { } root) ShadcnTheme.SetDark(root, _dark.Value);
    }

    void OnMobileOpenChange(bool open) => _mobileOpen.Value = open;

    void OnSearch(string value) => _query.Value = value ?? "";

    void OnSearchSubmit(SubmitEvent e) => SubmitSearch();

    void SubmitSearch()
    {
        string query = _query.Value.Trim();
        if (query.Length == 0) return;
        var match = SampleData.AllNavItems.FirstOrDefault(item =>
            item.Title.Contains(query, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            Toast("No page found", $"Nothing matches “{query}”.", ToastVariant.Warning);
            return;
        }
        Navigate(match.Id);
    }

    void OnNotificationsOpen(bool open)
    {
        if (!open || UnreadCount == 0) return;
        _notifications.Value = _notifications.Value.Select(note => note with { Unread = false }).ToList();
    }

    void OpenNotification(NotificationEntry note) => Toast(note.Title, note.Detail, ToastVariant.Info);

    void Toast(string title, string? description = null, ToastVariant variant = ToastVariant.Default) =>
        _toasts.Show(new ToastOptions { Title = title, Description = description, Variant = variant });

    /// <summary>
    /// Builds one sidebar entry in code. Scoped CSS cannot see nodes created here, so the classes
    /// (<c>app-nav-*</c>) live in App.awaui.global.css.
    /// </summary>
    Node NavLink(NavItem item)
    {
        bool active = item.Id == _pageId.Value;
        var button = new AwAUI.Shadcn.Button
        {
            Key = item.Id,
            Variant = ButtonVariant.Ghost,
            Size = ButtonSize.Sm,
            OnClick = _ => Navigate(item.Id),
            Class = active ? "app-nav-link is-active" : "app-nav-link",
        };
        var children = button.Children ??= new NodeList();
        var icon = new AwAUI.UI.Svg(item.Icon) { Class = "app-nav-icon" };
        icon.Attributes["aria-hidden"] = "true";
        children.Add(icon);
        children.Add(new Text { Class = "app-nav-label", Content = item.Title });
        if (item.Badge is { } badge)
        {
            children.Add(new Badge
            {
                Variant = BadgeVariant.Secondary,
                Class = "app-nav-badge",
                Children = new NodeList([new TextNode(badge)]),
            });
        }
        var rest = button.Rest ??= new AttributeDictionary();
        rest["aria-current"] = active ? "page" : null;
        rest["data-active"] = active ? "true" : null;
        return button;
    }
}
