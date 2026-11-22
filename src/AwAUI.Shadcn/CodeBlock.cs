using System;
using System.Globalization;
using AwAUI.Controls.Primitives;
using AwAUI.Icons.Lucide;
using AwAUI.Tailwind;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Shadcn;

/// <summary>
/// A bordered code block with an optional file header (file name, language badge, copy button), an optional line-number
/// gutter and monospace code lines. Copy writes <see cref="Code"/> to <c>Root.Input.Clipboard</c> and shows
/// "Copied" for a short time when the root has a clock. Code lines are ordinary read-only text: they can be selected
/// with the mouse (the gutter is <c>select-none</c>) and copied with the primary shortcut.
/// </summary>
public sealed partial class CodeBlock : Component
{
    const string BlockClass = "relative overflow-hidden rounded-lg border bg-muted/40 text-sm";
    const string HeaderClass = "flex items-center justify-between gap-2 border-b bg-muted/40 px-4 py-2 text-xs";
    const string CopyClass =
        "inline-flex h-6 items-center gap-1 rounded-sm px-2 text-xs text-muted-foreground transition-colors " +
        "hover:bg-accent hover:text-accent-foreground focus-visible:border-ring focus-visible:ring-[3px] " +
        "focus-visible:ring-ring/50";
    const string LineNumberClass = "w-8 shrink-0 select-none pr-4 text-right text-muted-foreground";
    const string CodeClass = "font-mono";

    readonly State<bool> _copied = new(false);
    IDisposable? _timer;

    public string? Code { get; set; }
    public string? Language { get; set; }
    public string? FileName { get; set; }
    public bool ShowLineNumbers { get; set; } = true;
    public bool ShowCopy { get; set; } = true;
    public bool Wrap { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        bool copied = _copied.Value;
        var block = new Div();
        if (FileName is not null || Language is not null || ShowCopy) block.Children.Add(BuildHeader(copied));
        block.Children.Add(BuildLines());
        return StaticParts.Style(block, "code-block", BlockClass, Class, Rest);
    }

    protected override void OnUnmount()
    {
        _timer?.Dispose();
        _timer = null;
    }

    Div BuildHeader(bool copied)
    {
        var header = new Div { Class = HeaderClass };
        header.Attributes["data-slot"] = "code-block-header";

        var info = new Div { Class = "flex min-w-0 items-center gap-2" };
        if (FileName is not null)
        {
            var name = new Span(FileName) { Class = "truncate font-medium text-foreground" };
            name.Attributes["data-slot"] = "code-block-filename";
            info.Children.Add(name);
        }
        if (Language is not null)
        {
            var language = new Span(Language)
            {
                Class = "rounded-sm bg-muted px-1.5 py-0.5 font-mono text-[10px] tracking-wide text-muted-foreground uppercase",
            };
            language.Attributes["data-slot"] = "code-block-language";
            info.Children.Add(language);
        }
        header.Children.Add(info);
        if (ShowCopy) header.Children.Add(BuildCopy(copied));
        return header;
    }

    UIButton BuildCopy(bool copied)
    {
        var children = new NodeList();
        if (copied)
        {
            var glyph = StaticParts.Glyph(LucideIcons.Check, "size-3.5");
            glyph.Attributes["data-slot"] = "code-block-copy-icon";
            children.Add(glyph);
        }
        children.Add(new UIText(copied ? "Copied" : "Copy"));

        var button = new UIButton { Children = children };
        button.Class = TailwindMerge.Cn(CopyClass);
        button.Attributes["type"] = "button";
        button.Attributes["data-slot"] = "code-block-copy";
        button.Attributes["data-copied"] = TabsParts.Bool(copied);
        button.Attributes["aria-label"] = copied ? "Copied" : "Copy code";
        button.OnClick = EventComposition.Compose(button.OnClick, e =>
        {
            if (!e.DefaultPrevented) Copy();
        });
        return button;
    }

    Div BuildLines()
    {
        var lines = new Div
        {
            Class = TailwindMerge.Cn("overflow-x-auto p-4 text-sm",
                Wrap ? "whitespace-pre-wrap break-words" : "whitespace-pre"),
        };
        lines.Attributes["data-slot"] = "code-block-lines";

        string[] source = (Code ?? "").Split(new[] { '\n' }, StringSplitOptions.None);
        for (int index = 0; index < source.Length; index++)
        {
            var line = new Div { Class = "flex min-h-[1.25rem]" };
            line.Attributes["data-slot"] = "code-block-line";
            if (ShowLineNumbers)
            {
                var number = new Span((index + 1).ToString(CultureInfo.InvariantCulture)) { Class = LineNumberClass };
                number.Attributes["data-slot"] = "code-block-line-number";
                number.Attributes["aria-hidden"] = "true";
                line.Children.Add(number);
            }
            var code = new Span(source[index])
            {
                Class = TailwindMerge.Cn(CodeClass, Wrap ? "whitespace-pre-wrap break-words" : "whitespace-pre"),
            };
            code.Attributes["data-slot"] = "code-block-code";
            line.Children.Add(code);
            lines.Children.Add(line);
        }
        if (Children is not null) lines.Children.AddRange(Children);
        return lines;
    }

    void Copy()
    {
        string code = Code ?? "";
        if (Root?.Input.Clipboard is { } clipboard) _ = clipboard.SetTextAsync(code);
        if (Root?.Clock is not { } clock) return;
        _timer?.Dispose();
        _copied.Value = true;
        _timer = clock.Schedule(TimeSpan.FromMilliseconds(1500), () =>
        {
            _timer = null;
            _copied.Value = false;
        });
    }
}
