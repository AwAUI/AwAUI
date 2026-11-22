using System;
using System.Collections.Generic;
using AwAUI.Shadcn;
using AwAUI.UI;

namespace DeclarativeDemo.Pages;

// Code-behind for Settings.awaui: tab state, form fields, the confirm dialog and toast helpers.
public partial class Settings
{
    readonly State<string> _tab = new("profile");
    readonly State<string> _displayName = new("Alex Rivera");
    readonly State<string> _email = new("alex@northwind.dev");
    readonly State<string> _bio = new("Designing calm product experiences for busy teams.");
    readonly State<double> _hours = new(24);
    readonly State<string> _visibility = new("team");
    readonly State<bool> _emailUpdates = new(true);
    readonly State<bool> _weeklyDigest = new(true);
    readonly State<bool> _mentions = new(true);
    readonly State<bool> _productNews = new(false);
    readonly State<string> _channel = new("both");
    readonly State<string> _plan = new("pro");
    readonly State<string?> _profileError = new((string?)null);
    readonly State<bool> _confirmOpen = new(false);

    /// <summary>Toaster supplied by the shell.</summary>
    public ToastStore? Toasts { get; set; }

    IReadOnlyList<string?>? ProfileErrors =>
        _profileError.Value is { } message ? new string?[] { message } : null;

    void SetTab(string? value) => _tab.Value = value ?? "profile";

    void OnDisplayName(string value) => _displayName.Value = value;

    void OnEmail(string value) => _email.Value = value;

    void OnBio(string value) => _bio.Value = value;

    void OnHours(double value) => _hours.Value = value;

    void OnVisibility(string value) => _visibility.Value = value;

    void OnEmailUpdates(bool value) => _emailUpdates.Value = value;

    void OnWeeklyDigest(bool value) => _weeklyDigest.Value = value;

    void OnMentions(bool value) => _mentions.Value = value;

    void OnProductNews(bool value) => _productNews.Value = value;

    void OnChannel(string value) => _channel.Value = value;

    void OnPlan(string value) => _plan.Value = value;

    void SaveProfile()
    {
        string name = _displayName.Value.Trim();
        if (name.Length < 2)
        {
            _profileError.Value = "Enter at least 2 characters.";
            return;
        }
        _profileError.Value = null;
        Notify("Profile saved", "Your changes are visible to the workspace.", ToastVariant.Success);
    }

    void ResetProfile()
    {
        _displayName.Value = "Alex Rivera";
        _email.Value = "alex@northwind.dev";
        _bio.Value = "Designing calm product experiences for busy teams.";
        _hours.Value = 24;
        _visibility.Value = "team";
        _emailUpdates.Value = true;
        _profileError.Value = null;
        Notify("Changes discarded", "The profile form was reset.", ToastVariant.Default);
    }

    void UploadAvatar() => Notify("Upload opened", "Choose an image to replace the current avatar.", ToastVariant.Info);

    void RemoveAvatar() => Notify("Avatar removed", "The initials fallback is shown instead.", ToastVariant.Info);

    void OpenConfirm() => _confirmOpen.Value = true;

    void CloseConfirm() => _confirmOpen.Value = false;

    void OnConfirmOpenChange(bool open) => _confirmOpen.Value = open;

    void ConfirmDelete()
    {
        _confirmOpen.Value = false;
        Notify("Workspace scheduled for deletion", "You have 30 days to restore it from Billing.", ToastVariant.Error);
    }

    void Notify(string title, string description, ToastVariant variant) =>
        Toasts?.Show(new ToastOptions { Title = title, Description = description, Variant = variant });
}
