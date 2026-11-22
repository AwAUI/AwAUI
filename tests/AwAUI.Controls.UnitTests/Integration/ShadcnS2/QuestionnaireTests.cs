using System;
using System.Collections.Generic;
using System.Linq;
using AwAUI.Controls.UnitTests.Integration;
using AwAUI.UI;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Shadcn.Integration.S2;

/// <summary>
/// S2 Questionnaire acceptance: official part slots, progress, step navigation, single and multiple choice answers,
/// freeform input, skipping, and the submit callback.
/// </summary>
public class QuestionnaireTests
{
    static QuestionnaireTests() => ShadcnTheme.Register();

    static UIHarness Harness(Func<Node> render) =>
        new(render, appStyleSheets: true, width: 640, height: 480);

    static string? A(Element e, string name) => e.GetAttribute(name);

    static List<Element> Slots(UIHarness h, string slot) =>
        h.FindAll(e => A(e, "data-slot") == slot);

    static Element ActionButton(UIHarness h, string slot) =>
        Slots(h, slot).Single();

    static Element Option(UIHarness h, string value) =>
        h.FindAll(e => A(e, "data-slot") == "questionnaire-choice" && A(e, "data-value") == value).Single();

    static Element Progress(UIHarness h) => Slots(h, "questionnaire-progress").Single();

    static QuestionnaireQuestion Single(string id, string prompt, params string[] options) => new()
    {
        Id = id,
        Prompt = prompt,
        Kind = QuestionnaireQuestionKind.SingleChoice,
        Options = options.Select(o => new QuestionnaireOption { Value = o, Label = o }).ToArray(),
    };

    static QuestionnaireQuestion Multiple(string id, string prompt, params string[] options) => new()
    {
        Id = id,
        Prompt = prompt,
        Kind = QuestionnaireQuestionKind.MultipleChoice,
        Options = options.Select(o => new QuestionnaireOption { Value = o, Label = o }).ToArray(),
    };

    static QuestionnaireQuestion Freeform(string id, string prompt, string? placeholder) => new()
    {
        Id = id,
        Prompt = prompt,
        Kind = QuestionnaireQuestionKind.Freeform,
        Placeholder = placeholder,
    };

    [Fact]
    public void Questionnaire_navigates_steps_and_records_a_single_choice()
    {
        QuestionnaireAnswer? answered = null;
        var h = Harness(() => new Questionnaire
        {
            Questions = [Single("q1", "First?", "a", "b"), Single("q2", "Second?", "c", "d")],
            Answered = answer => answered = answer,
        });

        Assert.Equal("1", A(Progress(h), "aria-valuenow"));
        Assert.Equal("2", A(Progress(h), "aria-valuemax"));
        Assert.Contains("Question 1 of 2", Progress(h).TextContent);

        var item = Slots(h, "questionnaire-item").Single();
        Assert.Equal("fieldset", item.Tag);
        Assert.Equal("legend", Slots(h, "questionnaire-title").Single().Tag);
        Assert.Contains("First?", item.TextContent);

        // Next is disabled until the current question is answered.
        Assert.True(ActionButton(h, "questionnaire-next").IsDisabled);

        h.Click(Option(h, "a"));
        Assert.NotNull(A(Option(h, "a"), "data-checked"));
        Assert.Equal("true", A(Option(h, "a"), "aria-checked"));
        Assert.NotNull(answered);
        Assert.Equal("q1", answered!.QuestionId);
        Assert.Equal("a", answered.Value);

        h.Click(ActionButton(h, "questionnaire-next"));
        Assert.Equal("2", A(Progress(h), "aria-valuenow"));
        Assert.Contains("Second?", Slots(h, "questionnaire-item").Single().TextContent);
    }

    [Fact]
    public void Questionnaire_multiple_choice_toggles_and_submits_the_values()
    {
        IReadOnlyDictionary<string, string>? submitted = null;
        var h = Harness(() => new Questionnaire
        {
            Questions = [Multiple("q1", "Pick many", "a", "b")],
            Submitted = answers => submitted = answers,
        });

        Assert.Equal("group", A(Slots(h, "questionnaire-choices").Single(), "role"));
        Assert.Null(A(Option(h, "a"), "data-checked"));

        h.Click(Option(h, "a"));
        h.Click(Option(h, "b"));
        Assert.NotNull(A(Option(h, "a"), "data-checked"));
        Assert.NotNull(A(Option(h, "b"), "data-checked"));

        // Toggling the same option clears it again.
        h.Click(Option(h, "a"));
        Assert.Null(A(Option(h, "a"), "data-checked"));

        h.Click(Option(h, "a"));
        h.Click(ActionButton(h, "questionnaire-submit"));

        Assert.NotNull(submitted);
        Assert.Equal("a, b", submitted!["q1"]);
    }

    [Fact]
    public void Questionnaire_choices_expose_native_radio_inputs_and_the_registry_slots()
    {
        var h = Harness(() => new Questionnaire
        {
            Questions = [Single("q1", "Pick one", "a", "b")],
        });

        var choice = Option(h, "a");
        Assert.Equal("label", choice.Tag);
        Assert.Equal("radio", A(choice, "data-type"));
        Assert.Contains("group/questionnaire-choice", A(choice, "class")!);

        var input = Slots(h, "questionnaire-choice-input").First();
        Assert.Equal("input", input.Tag);
        Assert.Equal("radio", A(input, "type"));
        Assert.Equal("q1", A(input, "name"));
        Assert.Equal("a", A(input, "value"));
        Assert.Equal(2, Slots(h, "questionnaire-choice-indicator").Count);
        Assert.Equal(2, Slots(h, "questionnaire-choice-label").Count);
        Assert.Equal(2, Slots(h, "questionnaire-choice-indicator-dot").Count);
        Assert.Equal(2, Slots(h, "questionnaire-choice-indicator-check").Count);
    }

    [Fact]
    public void Questionnaire_skippable_question_advances_without_an_answer()
    {
        var h = Harness(() => new Questionnaire
        {
            Questions =
            [
                new QuestionnaireQuestion
                {
                    Id = "q1",
                    Prompt = "Optional?",
                    Kind = QuestionnaireQuestionKind.SingleChoice,
                    Skippable = true,
                    Options =
                    [
                        new QuestionnaireOption { Value = "a", Label = "A" },
                        new QuestionnaireOption { Value = "b", Label = "B" },
                    ],
                },
                Single("q2", "Second?", "c", "d"),
            ],
        });

        Assert.False(ActionButton(h, "questionnaire-next").IsDisabled);
        h.Click(ActionButton(h, "questionnaire-skip"));
        Assert.Equal("2", A(Progress(h), "aria-valuenow"));
        Assert.Contains("Second?", Slots(h, "questionnaire-item").Single().TextContent);
    }

    [Fact]
    public void Questionnaire_required_question_shows_the_error_until_answered()
    {
        var h = Harness(() => new Questionnaire
        {
            Questions = [Single("q1", "First?", "a", "b")],
        });

        Assert.Single(Slots(h, "questionnaire-error"));
        Assert.Equal("Choose an answer to continue.", Slots(h, "questionnaire-error").Single().TextContent.Trim());

        var item = Slots(h, "questionnaire-item").Single();
        var group = Slots(h, "questionnaire-choices").Single();
        var error = Slots(h, "questionnaire-error").Single();
        Assert.Equal("true", A(item, "aria-invalid"));
        Assert.Contains(A(error, "id")!, A(group, "aria-describedby")!);
        Assert.Equal("true", A(Slots(h, "questionnaire-choice-input").First(), "aria-invalid"));

        h.Click(Option(h, "a"));
        Assert.Empty(Slots(h, "questionnaire-error"));
        Assert.Null(A(Slots(h, "questionnaire-item").Single(), "aria-invalid"));
    }

    [Fact]
    public void Questionnaire_description_and_progress_use_the_registry_slots()
    {
        var h = Harness(() => new Questionnaire
        {
            Questions =
            [
                new QuestionnaireQuestion
                {
                    Id = "q1",
                    Prompt = "Where should messages appear?",
                    Description = "Select every surface that applies.",
                    Kind = QuestionnaireQuestionKind.SingleChoice,
                    Options = [new QuestionnaireOption { Value = "a", Label = "A" }],
                },
            ],
        });

        var description = Slots(h, "questionnaire-description").Single();
        Assert.Contains("Select every surface", description.TextContent);
        Assert.Contains("text-muted-foreground", A(description, "class")!);
        Assert.Contains(A(description, "id")!, A(Slots(h, "questionnaire-choices").Single(), "aria-describedby")!);
        Assert.Equal("progressbar", A(Progress(h), "role"));
    }

    [Fact]
    public void Questionnaire_freeform_input_is_collected_on_submit()
    {
        string? submitted = null;
        var h = Harness(() => new Questionnaire
        {
            Questions = [Freeform("q1", "Describe", "Type here")],
            Submitted = answers => submitted = answers["q1"],
        });

        Assert.Single(Slots(h, "questionnaire-input-wrapper"));
        var input = Slots(h, "questionnaire-input").Single();
        Assert.Equal("input", input.Tag);
        h.Click(input);
        h.TypeText("hello world");

        Assert.Equal("hello world", ((InputElement)input).Value);

        h.Click(ActionButton(h, "questionnaire-submit"));
        Assert.Equal("hello world", submitted);
    }
}
