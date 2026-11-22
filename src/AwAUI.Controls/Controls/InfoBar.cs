using System;
using AwAUI.Controls.Primitives;
using AwAUI.Platform;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Controls;

/// <summary>The severity of an <see cref="InfoBar"/>, which also selects its role and live-region politeness.</summary>
public enum InfoBarSeverity : byte
{
    Informational,
    Success,
    Warning,
    Error,
}

/// <summary>
/// An inline feedback banner. <see cref="Severity"/> chooses the ARIA role: Informational, Success, and Warning use
/// <c>status</c> with <c>aria-live="polite"</c>; Error uses <c>alert</c> with <c>aria-live="assertive"</c>. The optional
/// close button reports <see cref="CloseButtonClick"/> and then requests <see cref="IsOpen"/> to become false.
/// </summary>
public sealed partial class InfoBar : Control
{
    readonly ControllableState<bool> _open = new();

    protected override string GlobalStyleSheet => InfoBarStyles.StyleSheet;

    /// <summary>The banner severity. Defaults to Informational.</summary>
    public InfoBarSeverity Severity { get; set; } = InfoBarSeverity.Informational;

    public string? Title { get; set; }

    public string? Message { get; set; }

    /// <summary>Controls visibility; null uses <see cref="DefaultIsOpen"/>.</summary>
    public bool? IsOpen { get; set; }

    /// <summary>Initial visibility for uncontrolled use. Defaults to true.</summary>
    public bool DefaultIsOpen { get; set; } = true;

    /// <summary>Reports an open-state change and accepts a close request.</summary>
    public Action<bool>? IsOpenChanged { get; set; }

    /// <summary>Shows the close button. Defaults to true.</summary>
    public bool IsClosable { get; set; } = true;

    /// <summary>Optional action rendered after the message, typically a <see cref="Button"/>.</summary>
    public Node? ActionButton { get; set; }

    /// <summary>Raised when the close button is activated, before the open state changes.</summary>
    public Action<PointerEvent>? CloseButtonClick { get; set; }

    /// <summary>Accessible name of the close button. Defaults to "Close".</summary>
    public string? CloseButtonAriaLabel { get; set; } = "Close";

    /// <summary>Shows a severity glyph. Defaults to true.</summary>
    public bool IsIconVisible { get; set; } = true;

    protected override Node Build()
    {
        bool open = _open.Sync(IsOpen, DefaultIsOpen, IsOpenChanged);
        if (!open) return new Fragment();

        bool error = Severity == InfoBarSeverity.Error;
        var root = Apply(new Div { Role = error ? Role.Alert : Role.Status }, "aw-infobar");
        root.AriaLabel = AriaLabel;
        root.Attributes["aria-live"] = error ? "assertive" : "polite";
        root.Attributes["aria-atomic"] = "true";
        root.Attributes["data-severity"] = SeverityName(Severity);

        if (IsIconVisible)
        {
            var icon = new Div { Children = new NodeList { new UIText(SeverityGlyph(Severity)) } };
            icon.Attributes["part"] = "icon";
            icon.Attributes["aria-hidden"] = "true";
            root.Children.Add(icon);
        }

        var content = new Div();
        content.Attributes["part"] = "content";
        if (Title is not null)
        {
            var title = new Div { Children = new NodeList { new UIText(Title) } };
            title.Attributes["part"] = "title";
            content.Children.Add(title);
        }
        if (Message is not null)
        {
            var message = new Div { Children = new NodeList { new UIText(Message) } };
            message.Attributes["part"] = "message";
            content.Children.Add(message);
        }
        root.Children.Add(content);

        if (ActionButton is not null)
        {
            var action = new Div { Children = new NodeList { ActionButton } };
            action.Attributes["part"] = "action";
            root.Children.Add(action);
        }

        if (IsClosable)
        {
            var close = new UIButton
            {
                AriaLabel = CloseButtonAriaLabel,
                Children = new NodeList { new UIText("×") },
            };
            close.Attributes["type"] = "button";
            close.Attributes["part"] = "close";
            close.OnClick = e =>
            {
                if (e.DefaultPrevented) return;
                CloseButtonClick?.Invoke(e);
                if (!e.DefaultPrevented) _open.Set(false);
            };
            root.Children.Add(close);
        }

        return root;
    }

    static string SeverityName(InfoBarSeverity severity) => severity switch
    {
        InfoBarSeverity.Success => "success",
        InfoBarSeverity.Warning => "warning",
        InfoBarSeverity.Error => "error",
        _ => "informational",
    };

    static string SeverityGlyph(InfoBarSeverity severity) => severity switch
    {
        InfoBarSeverity.Success => "✓",
        InfoBarSeverity.Warning => "!",
        InfoBarSeverity.Error => "✕",
        _ => "i",
    };
}

static class InfoBarStyles
{
    internal const string StyleSheet = """
        @layer aw-infobar-defaults {
          :where(.aw-infobar) {
            display: flex;
            flex-direction: row;
            align-items: flex-start;
            gap: 8px;
            padding: 8px 12px;
            min-width: 0;
          }
          :where(.aw-infobar [part="icon"]) {
            flex: 0 0 auto;
            display: inline-flex;
            align-items: center;
            justify-content: center;
            min-width: 20px;
            min-height: 20px;
          }
          :where(.aw-infobar [part="content"]) {
            display: flex;
            flex: 1 1 auto;
            flex-direction: column;
            gap: 2px;
            min-width: 0;
          }
          :where(.aw-infobar [part="title"]) {
            font-weight: 600;
          }
          :where(.aw-infobar [part="action"]) {
            flex: 0 0 auto;
          }
          :where(.aw-infobar [part="close"]) {
            flex: 0 0 auto;
            display: inline-flex;
            align-items: center;
            justify-content: center;
            min-width: 24px;
            min-height: 24px;
            padding: 0;
            border: 0;
            background: transparent;
            font: inherit;
            cursor: pointer;
          }
          :where(.aw-infobar[data-severity="success"] [part="icon"]) { color: currentColor; }
          :where(.aw-infobar[data-severity="warning"] [part="icon"]),
          :where(.aw-infobar[data-severity="error"] [part="icon"]) { font-weight: 700; }
        }
        """;
}
