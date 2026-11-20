using AwAUI.UI;

namespace AwAUI.Controls;

/// <summary>Displays selectable plain text. Pointer selection and platform copy shortcuts use the read-only multiline text engine.</summary>
public sealed partial class SelectableTextBlock : Control
{
    protected override string GlobalStyleSheet => """
        @layer aw-selectable-text-defaults {
          :where(.aw-selectable-textblock) {
            display: block;
            max-width: 100%;
            min-width: 0;
            min-height: 0;
            padding: 0;
            border: 0;
            border-radius: 0;
            background: transparent;
            color: inherit;
            font: inherit;
            field-sizing: content;
            user-select: text;
            white-space: pre-wrap;
            overflow-wrap: anywhere;
            cursor: text;
          }
        }
        """;

    public string? Text { get; set; }

    protected override Node Build()
    {
        var field = new TextArea { Value = Text ?? "", ReadOnly = true, Role = Role.TextBox };
        field.Attributes["aria-readonly"] = "true";
        return Apply(field, "aw-selectable-textblock");
    }
}
