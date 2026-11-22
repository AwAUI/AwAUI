using System;
using System.Collections.Generic;
using System.Linq;
using AwAUI.Shadcn;
using AwAUI.UI;

namespace DeclarativeDemo.Pages;

// Code-behind for ProjectDialog.awaui. The FormController lives here so validation survives rebuilds.
public partial class ProjectDialog
{
    static readonly IReadOnlyList<FormValidator> _nameValidators =
    [
        FormValidators.Required("Give the project a name."),
        FormValidators.MinLength(3, "Use at least 3 characters."),
    ];

    static readonly IReadOnlyList<FormValidator> _descriptionValidators =
    [
        FormValidators.MaxLength(240, "Keep the description under 240 characters."),
    ];

    readonly FormController _controller = new();
    readonly State<string> _owner = new("Alex Rivera");
    readonly State<string> _priority = new("Medium");
    readonly State<DateTime?> _startDate = new(DateTime.Today.AddDays(7));
    readonly State<IReadOnlyList<string>> _members = new(Array.Empty<string>());
    readonly State<bool> _notify = new(true);

    /// <summary>Controlled open state, owned by the Projects page.</summary>
    public bool Open { get; set; }

    public Action<bool>? OnOpenChange { get; set; }

    /// <summary>Toaster shared with the shell (the success toast is raised here).</summary>
    public ToastStore? Toasts { get; set; }

    /// <summary>Raised after a valid submit. The host page appends the new row.</summary>
    public Action<ProjectDraft>? OnCreated { get; set; }

    void OnOwnerChanged(string? value) => _owner.Value = value ?? "";

    void OnPriorityChanged(string value) => _priority.Value = value;

    void OnStartDate(DateTime? date) => _startDate.Value = date;

    void OnNotifyChanged(bool value) => _notify.Value = value;

    bool IsMember(string name) => _members.Value.Contains(name);

    void SetMember(string name, bool selected)
    {
        var members = _members.Value.ToList();
        if (selected)
        {
            if (!members.Contains(name)) members.Add(name);
        }
        else members.Remove(name);
        _members.Value = members;
    }

    void Submit() => _controller.Submit();

    void OnValid(FormController controller)
    {
        string name = (controller.GetValue("name") ?? "").Trim();
        string description = (controller.GetValue("description") ?? "").Trim();
        OnCreated?.Invoke(new ProjectDraft(name, description, _owner.Value, _priority.Value, _startDate.Value, _members.Value, _notify.Value));

        Toasts?.Show(new ToastOptions
        {
            Title = "Project created",
            Description = $"“{name}” is ready in the workspace.",
            Variant = ToastVariant.Success,
        });
        Reset();
        OnOpenChange?.Invoke(false);
    }

    void OnInvalid(FormController controller) => Toasts?.Show(new ToastOptions
    {
        Title = "Check the form",
        Description = controller.ErrorCount == 1 ? "One field needs attention." : $"{controller.ErrorCount} fields need attention.",
        Variant = ToastVariant.Warning,
    });

    void Cancel()
    {
        Reset();
        OnOpenChange?.Invoke(false);
    }

    void Reset()
    {
        _controller.Reset();
        _owner.Value = "Alex Rivera";
        _priority.Value = "Medium";
        _startDate.Value = DateTime.Today.AddDays(7);
        _members.Value = Array.Empty<string>();
        _notify.Value = true;
    }
}
