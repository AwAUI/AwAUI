using System;
using System.Collections.Generic;
using System.Globalization;
using AwAUI.Controls.Primitives;
using AwAUI.Icons.Lucide;
using AwAUI.Tailwind;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;
using UIInput = AwAUI.UI.Input;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Shadcn;

/// <summary>How a question accepts its answer.</summary>
public enum QuestionnaireQuestionKind { SingleChoice, MultipleChoice, Freeform }

/// <summary>One selectable option of a choice question.</summary>
public sealed class QuestionnaireOption
{
    public string Value { get; set; } = "";
    public string? Label { get; set; }
    public bool Disabled { get; set; }
}

/// <summary>One step of a <see cref="Questionnaire"/>.</summary>
public sealed class QuestionnaireQuestion
{
    public string Id { get; set; } = "";
    public string Prompt { get; set; } = "";
    /// <summary>Optional supporting text rendered as <c>questionnaire-description</c>.</summary>
    public string? Description { get; set; }
    public QuestionnaireQuestionKind Kind { get; set; }
    public IReadOnlyList<QuestionnaireOption> Options { get; set; } = Array.Empty<QuestionnaireOption>();
    public bool Skippable { get; set; }
    public string? Placeholder { get; set; }
}

/// <summary>One answer reported by <see cref="Questionnaire.Answered"/>.</summary>
public sealed record QuestionnaireAnswer(string QuestionId, string? Value, IReadOnlyList<string>? Values);

/// <summary>
/// A multi-step questionnaire: a progress indicator, one question at a time, and Previous / Skip / Next / Submit actions.
/// <para>
/// The part slots and classes follow the shadcn <c>questionnaire</c> registry component: <c>questionnaire</c>,
/// <c>questionnaire-progress</c>, <c>questionnaire-item</c>, <c>questionnaire-title</c>,
/// <c>questionnaire-description</c>, <c>questionnaire-choices</c>, <c>questionnaire-choice</c>,
/// <c>questionnaire-choice-input</c>, <c>questionnaire-choice-indicator</c>, <c>questionnaire-choice-label</c>,
/// <c>questionnaire-input</c>, <c>questionnaire-error</c>, <c>questionnaire-actions</c>,
/// <c>questionnaire-previous</c>, <c>questionnaire-skip</c>, <c>questionnaire-next</c> and
/// <c>questionnaire-submit</c>.
/// </para>
/// <para>
/// The step is uncontrolled unless <see cref="Step"/> is set; answers are kept inside the component. Single-choice
/// questions behave as a radio group, multiple-choice questions as a group of checkboxes, and freeform questions use
/// a text input. <see cref="Answered"/> fires whenever an answer changes; <see cref="Submitted"/> receives the
/// collected answers (multiple-choice values joined with <c>", "</c>).
/// </para>
/// </summary>
public sealed partial class Questionnaire : Component
{
    const string ChoiceClass =
        "group/questionnaire-choice relative flex min-h-11 cursor-pointer items-start gap-2.5 rounded-lg border border-input " +
        "bg-transparent px-3 py-2.5 text-start text-sm transition-colors outline-none select-none hover:bg-muted/50 " +
        "has-[>input:focus-visible]:border-ring has-[>input:focus-visible]:ring-3 has-[>input:focus-visible]:ring-ring/50 " +
        "data-invalid:border-destructive dark:bg-input/20 data-checked:border-primary/40 data-checked:bg-muted " +
        "dark:data-checked:bg-muted data-disabled:pointer-events-none data-disabled:cursor-not-allowed data-disabled:opacity-50";

    const string IndicatorClass =
        "pointer-events-none relative flex size-4 shrink-0 translate-y-[--spacing(0.45)] items-center justify-center " +
        "rounded-[4px] border border-input group-has-data-[slot=questionnaire-choice-description]/questionnaire-choice:translate-y-0.5 " +
        "group-data-[type=radio]/questionnaire-choice:rounded-full group-data-checked/questionnaire-choice:border-primary " +
        "group-data-checked/questionnaire-choice:bg-primary group-data-checked/questionnaire-choice:text-primary-foreground " +
        "dark:bg-input/30 dark:group-data-checked/questionnaire-choice:bg-primary";

    const string InputClass =
        "h-8 min-h-11 w-full min-w-0 rounded-lg border border-input bg-transparent px-2.5 py-1 text-base " +
        "transition-[color,box-shadow,background-color] outline-none focus-visible:border-ring focus-visible:ring-3 " +
        "focus-visible:ring-ring/50 disabled:pointer-events-none disabled:cursor-not-allowed disabled:bg-input/50 " +
        "disabled:opacity-50 aria-invalid:border-destructive aria-invalid:ring-3 aria-invalid:ring-destructive/20 " +
        "sm:min-h-0 md:text-sm dark:bg-input/30 dark:disabled:bg-input/80 dark:aria-invalid:border-destructive/50 " +
        "dark:aria-invalid:ring-destructive/40 selection:bg-primary selection:text-primary-foreground " +
        "placeholder:text-muted-foreground";

    readonly ControllableState<int> _step = new();
    readonly State<int> _version = new(0);

    readonly Dictionary<string, string> _single = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<string>> _multiple = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _freeform = new(StringComparer.Ordinal);

    public IReadOnlyList<QuestionnaireQuestion> Questions { get; set; } = Array.Empty<QuestionnaireQuestion>();
    public int? Step { get; set; }
    public int DefaultStep { get; set; }
    public Action<int>? StepChanged { get; set; }
    public Action<QuestionnaireAnswer>? Answered { get; set; }
    public Action<IReadOnlyDictionary<string, string>>? Submitted { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        int count = Questions.Count;
        int step = _step.Sync(Step, DefaultStep, StepChanged);
        // Subscribe to answer changes so selections and typed text re-render.
        _ = _version.Value;

        var root = new Div();
        if (count == 0)
        {
            root.Children.Add(new Div { Children = [new UIText("No questions")] });
            return StaticParts.Style(root, "questionnaire", "flex w-full min-w-0 flex-col gap-4", Class, Rest);
        }

        if (step < 0) step = 0;
        if (step > count - 1) step = count - 1;
        var question = Questions[step];

        root.Children.Add(BuildProgress(step, count));
        root.Children.Add(BuildQuestion(question, !question.Skippable));
        root.Children.Add(BuildFooter(question, step, count));
        if (Children is not null) root.Children.AddRange(Children);

        return StaticParts.Style(root, "questionnaire", "flex w-full min-w-0 flex-col gap-4", Class, Rest);
    }

    static Div BuildProgress(int step, int count)
    {
        var text = new UIText(string.Format(CultureInfo.InvariantCulture, "Question {0} of {1}", step + 1, count));
        var progress = new Div
        {
            Role = Role.ProgressBar,
            Children = [text],
            Class = "min-h-[1lh] w-fit min-w-[14ch] text-xs font-medium text-muted-foreground tabular-nums",
        };
        progress.Attributes["data-slot"] = "questionnaire-progress";
        progress.Attributes["aria-valuemin"] = "1";
        progress.Attributes["aria-valuemax"] = count.ToString(CultureInfo.InvariantCulture);
        progress.Attributes["aria-valuenow"] = (step + 1).ToString(CultureInfo.InvariantCulture);
        progress.Attributes["aria-valuetext"] = string.Format(CultureInfo.InvariantCulture,
            "Question {0} of {1}", step + 1, count);
        return progress;
    }

    Node BuildQuestion(QuestionnaireQuestion question, bool required)
    {
        string promptId = TabsParts.ValueId("questionnaire-title", question.Id);
        bool invalid = required && !HasAnswer(question);
        string? descriptionId = string.IsNullOrEmpty(question.Description)
            ? null
            : TabsParts.ValueId("questionnaire-description", question.Id);
        string errorId = TabsParts.ValueId("questionnaire-error", question.Id);
        string? describedBy = invalid
            ? descriptionId is null ? errorId : descriptionId + " " + errorId
            : descriptionId;

        var container = new StaticParts.TagElement("fieldset")
        {
            Class = "flex min-w-0 flex-col gap-4 border-0 p-0 outline-none",
        };
        container.Attributes["data-slot"] = "questionnaire-item";
        container.Attributes["data-question-id"] = question.Id;
        container.Attributes["aria-invalid"] = invalid ? "true" : null;

        var prompt = new StaticParts.TagElement("legend") { Id = promptId, Children = [new UIText(question.Prompt)] };
        prompt.Class = "text-base leading-snug font-medium text-pretty" +
            (string.IsNullOrEmpty(question.Description) ? " mb-4" : "");
        prompt.Attributes["data-slot"] = "questionnaire-title";
        container.Children.Add(prompt);

        if (!string.IsNullOrEmpty(question.Description))
        {
            var description = new Span { Id = descriptionId, Children = [new UIText(question.Description!)] };
            description.Class = "text-sm text-pretty text-muted-foreground";
            description.Attributes["data-slot"] = "questionnaire-description";
            container.Children.Add(description);
        }

        ElementNode field = question.Kind == QuestionnaireQuestionKind.Freeform
            ? BuildInput(question, promptId, describedBy, invalid)
            : BuildOptions(question, promptId, describedBy, invalid);
        field.Attributes["aria-describedby"] = describedBy;
        container.Children.Add(field);

        if (invalid)
        {
            var error = new Span { Id = errorId, Children = [new UIText("Choose an answer to continue.")] };
            error.Class = "mt-2 text-sm text-destructive";
            error.Attributes["data-slot"] = "questionnaire-error";
            error.Attributes["role"] = "alert";
            container.Children.Add(error);
        }
        return container;
    }

    ElementNode BuildOptions(QuestionnaireQuestion question, string promptId, string? describedBy, bool invalid)
    {
        bool multiple = question.Kind == QuestionnaireQuestionKind.MultipleChoice;
        var group = new Div { Class = "group/questionnaire-choices grid min-w-0 gap-2" };
        group.Attributes["data-slot"] = "questionnaire-choices";
        group.Attributes["role"] = multiple ? "group" : "radiogroup";
        group.Attributes["aria-labelledby"] = promptId;
        group.Attributes["aria-describedby"] = describedBy;
        group.Attributes["aria-invalid"] = invalid ? "true" : null;

        foreach (var option in question.Options)
        {
            bool selected = IsSelected(question, option.Value);
            var input = new UIInput
            {
                InputType = multiple ? "checkbox" : "radio",
                Disabled = option.Disabled,
            };
            input.Class = "absolute inset-0 z-10 size-full cursor-pointer opacity-0";
            input.Attributes["data-slot"] = "questionnaire-choice-input";
            input.Attributes["name"] = question.Id;
            input.Attributes["value"] = option.Value;
            input.Attributes["checked"] = selected ? "" : null;
            input.Attributes["aria-checked"] = TabsParts.Bool(selected);
            input.Attributes["aria-invalid"] = invalid ? "true" : null;
            input.Attributes["aria-describedby"] = describedBy;

            var indicator = new Span { Class = IndicatorClass };
            indicator.Attributes["data-slot"] = "questionnaire-choice-indicator";
            indicator.Attributes["aria-hidden"] = "true";

            var dot = new Span
            {
                Class = "hidden size-2 rounded-full bg-primary-foreground " +
                    "group-data-[type=checkbox]/questionnaire-choice:hidden " +
                    "group-data-checked/questionnaire-choice:block",
            };
            dot.Attributes["data-slot"] = "questionnaire-choice-indicator-dot";
            indicator.Children.Add(dot);

            var check = StaticParts.Glyph(LucideIcons.Check,
                "hidden size-3 group-data-[type=radio]/questionnaire-choice:hidden " +
                "group-data-checked/questionnaire-choice:block");
            check.Attributes["data-slot"] = "questionnaire-choice-indicator-check";
            indicator.Children.Add(check);

            var label = new Span
            {
                Class = "flex min-w-0 flex-1 flex-col gap-0.5 leading-snug",
                Children = [new UIText(option.Label ?? option.Value)],
            };
            label.Attributes["data-slot"] = "questionnaire-choice-label";

            var choice = new StaticParts.TagElement("label") { Class = ChoiceClass };
            choice.Attributes["data-slot"] = "questionnaire-choice";
            choice.Attributes["data-value"] = option.Value;
            choice.Attributes["data-type"] = multiple ? "checkbox" : "radio";
            choice.Attributes["data-checked"] = selected ? "" : null;
            choice.Attributes["data-disabled"] = option.Disabled ? "" : null;
            choice.Attributes["aria-checked"] = TabsParts.Bool(selected);
            choice.Attributes["aria-invalid"] = invalid ? "true" : null;
            choice.Children.Add(input);
            choice.Children.Add(indicator);
            choice.Children.Add(label);
            if (!option.Disabled) choice.OnClick = _ => Choose(question, option);
            group.Children.Add(choice);
        }
        return group;
    }

    ElementNode BuildInput(QuestionnaireQuestion question, string promptId, string? describedBy, bool invalid)
    {
        string value = GetFreeform(question.Id);
        var input = new UIInput
        {
            Value = value,
            Placeholder = question.Placeholder,
            AriaLabel = question.Prompt,
        };
        input.Class = TailwindMerge.Cn(InputClass);
        input.Attributes["data-slot"] = "questionnaire-input";
        input.Attributes["aria-labelledby"] = promptId;
        input.Attributes["aria-describedby"] = describedBy;
        input.Attributes["aria-invalid"] = invalid ? "true" : null;
        input.OnInput = e =>
        {
            _freeform[question.Id] = e.Value;
            _version.Value = _version.Peek() + 1;
            Answered?.Invoke(new QuestionnaireAnswer(question.Id, e.Value, null));
        };

        var wrapper = new Div { Children = [input], Class = "group/questionnaire-input relative w-full min-w-0" };
        wrapper.Attributes["data-slot"] = "questionnaire-input-wrapper";
        return wrapper;
    }

    Div BuildFooter(QuestionnaireQuestion question, int step, int count)
    {
        var footer = new Div
        {
            Class = "grid min-h-11 w-full grid-cols-[minmax(0,1fr)_auto_auto] items-center gap-2 sm:min-h-8",
        };
        footer.Attributes["data-slot"] = "questionnaire-actions";

        footer.Children.Add(ActionButton("questionnaire-previous", "Previous", ButtonVariant.Outline,
            "col-start-1 row-start-1 min-h-11 justify-self-start sm:min-h-0",
            step == 0,
            () =>
            {
                if (step > 0) _step.Set(step - 1);
            }));

        if (question.Skippable && step < count - 1)
        {
            footer.Children.Add(ActionButton("questionnaire-skip", "Skip", ButtonVariant.Outline,
                "col-start-2 row-start-1 min-h-11 justify-self-end sm:min-h-0",
                false,
                () => _step.Set(step + 1)));
        }

        bool canAdvance = HasAnswer(question) || question.Skippable;
        if (step < count - 1)
        {
            footer.Children.Add(ActionButton("questionnaire-next", "Next", ButtonVariant.Default,
                "col-start-3 row-start-1 min-h-11 justify-self-end sm:min-h-0",
                !canAdvance,
                () =>
                {
                    if (HasAnswer(question) || question.Skippable) _step.Set(step + 1);
                }));
        }
        else
        {
            footer.Children.Add(ActionButton("questionnaire-submit", "Submit", ButtonVariant.Default,
                "col-start-3 row-start-1 min-h-11 justify-self-end sm:min-h-0",
                !canAdvance,
                Submit));
        }
        return footer;
    }

    static Node ActionButton(string slot, string label, ButtonVariant variant, string position, bool disabled,
        Action onClick)
    {
        var button = new UIButton { Disabled = disabled, Children = [new UIText(label)] };
        button.Attributes["data-slot"] = slot;
        button.Attributes["type"] = "button";
        TabsParts.Activate(button, onClick);
        return new Button
        {
            Variant = variant,
            Size = ButtonSize.Default,
            AsChild = true,
            Class = position,
            Children = [button],
        };
    }

    void Choose(QuestionnaireQuestion question, QuestionnaireOption option)
    {
        if (question.Kind == QuestionnaireQuestionKind.MultipleChoice)
        {
            if (!_multiple.TryGetValue(question.Id, out var values))
            {
                values = new List<string>();
                _multiple[question.Id] = values;
            }
            if (!values.Remove(option.Value)) values.Add(option.Value);
            _version.Value = _version.Peek() + 1;
            Answered?.Invoke(new QuestionnaireAnswer(question.Id, null, new List<string>(values)));
            return;
        }

        _single[question.Id] = option.Value;
        _version.Value = _version.Peek() + 1;
        Answered?.Invoke(new QuestionnaireAnswer(question.Id, option.Value, null));
    }

    void Submit()
    {
        var answers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var question in Questions)
        {
            switch (question.Kind)
            {
                case QuestionnaireQuestionKind.MultipleChoice:
                    if (_multiple.TryGetValue(question.Id, out var values) && values.Count > 0)
                    {
                        var ordered = new List<string>(values.Count);
                        foreach (var option in question.Options)
                            if (values.Contains(option.Value)) ordered.Add(option.Value);
                        answers[question.Id] = string.Join(", ", ordered);
                    }
                    break;
                case QuestionnaireQuestionKind.Freeform:
                    string text = GetFreeform(question.Id);
                    if (text.Length > 0) answers[question.Id] = text;
                    break;
                default:
                    if (_single.TryGetValue(question.Id, out var value)) answers[question.Id] = value;
                    break;
            }
        }
        Submitted?.Invoke(answers);
    }

    bool HasAnswer(QuestionnaireQuestion question) => question.Kind switch
    {
        QuestionnaireQuestionKind.MultipleChoice =>
            _multiple.TryGetValue(question.Id, out var values) && values.Count > 0,
        QuestionnaireQuestionKind.Freeform => !string.IsNullOrWhiteSpace(GetFreeform(question.Id)),
        _ => _single.ContainsKey(question.Id),
    };

    bool IsSelected(QuestionnaireQuestion question, string value) => question.Kind switch
    {
        QuestionnaireQuestionKind.MultipleChoice =>
            _multiple.TryGetValue(question.Id, out var values) && values.Contains(value),
        _ => _single.TryGetValue(question.Id, out var selected) && string.Equals(selected, value, StringComparison.Ordinal),
    };

    string GetFreeform(string questionId) => _freeform.TryGetValue(questionId, out var value) ? value : "";
}
