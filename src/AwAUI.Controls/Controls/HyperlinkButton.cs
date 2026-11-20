using System;
using System.Windows.Input;
using AwAUI.Controls.Primitives;
using AwAUI.Platform;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;

namespace AwAUI.Controls;

/// <summary>A focusable link that delegates navigation to the application.</summary>
public sealed partial class HyperlinkButton : Control
{
    /// <summary>An absolute HTTP or HTTPS URI without embedded credentials. Other values are ignored.</summary>
    public string? NavigateUri { get; set; }

    /// <summary>Called with the validated destination after an uncanceled activation. AwAUI does not open it.</summary>
    public Action<Uri>? OnNavigate { get; set; }

    public bool IsEnabled { get; set; } = true;
    /// <summary>Raise <see cref="ICommand.CanExecuteChanged"/> on the UI thread.</summary>
    public ICommand? Command { get; set; }
    public object? CommandParameter { get; set; }
    public Action<PointerEvent>? OnClick { get; set; }
    public NodeList? Children { get; set; }

    protected override Node Build()
    {
        var command = Command;
        // There is no built-in anchor with disabled/focus semantics. Use the native button
        // element with a link role so :disabled, focus, and activation agree.
        var link = new UIButton
        {
            Disabled = !CanActivate,
            Role = Role.Link,
            Children = Children ?? [],
            OnClick = OnClick,
        };
        link.Attributes["part"] = "link";
        Apply(link, "aw-hyperlink-button");
        bool disabled = link.Disabled;
        var destination = disabled ? null : GetDestination(NavigateUri);
        link.Attributes["href"] = destination?.AbsoluteUri;
        link.Attributes["aria-disabled"] = disabled ? "true" : null;
        if (disabled) link.TabIndex = null;

        UseEffect(() =>
        {
            if (command is null) return null;
            EventHandler refresh = (_, _) => Invalidate();
            command.CanExecuteChanged += refresh;
            return () => command.CanExecuteChanged -= refresh;
        }, command);
        UseEffect(() =>
        {
            if (disabled)
            {
                foreach (var element in GetRootElements()) element.Blur();
            }
            return null;
        }, disabled);

        var click = link.OnClick;
        link.OnClick = e =>
        {
            if (!CanActivate || e.CurrentTarget is not { IsDisabled: false, IsAttached: true }) return;
            click?.Invoke(e);
            if (e.DefaultPrevented || !IsEnabled || e.CurrentTarget is not { IsDisabled: false, IsAttached: true }) return;

            if (Command is { } current)
            {
                if (!current.CanExecute(CommandParameter)) return;
                current.Execute(CommandParameter);
            }
            if (!e.DefaultPrevented && IsEnabled && e.CurrentTarget is { IsDisabled: false, IsAttached: true } && destination is not null)
                OnNavigate?.Invoke(destination);
        };
        // The native button dispatches Enter after key handlers finish. Link activation
        // should not use Space, which remains available to caller key handlers.
        link.OnKeyDown = EventComposition.Chain<KeyEvent>(link.OnKeyDown, e =>
        {
            if (e.PhysicalKey == PhysicalKey.Space && ReferenceEquals(e.Target, e.CurrentTarget))
                e.PreventDefault();
        });
        return link;
    }

    bool CanActivate => IsEnabled && (Command is not { } command || command.CanExecute(CommandParameter));

    static Uri? GetDestination(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri is not null && uri.IsWellFormedOriginalString()
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && uri.Host.Length != 0 && uri.UserInfo.Length == 0 ? uri : null;
}
