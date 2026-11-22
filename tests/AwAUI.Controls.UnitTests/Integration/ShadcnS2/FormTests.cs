using AwAUI.Controls.UnitTests.Integration;
using AwAUI.Platform;
using AwAUI.UI;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Shadcn.Integration.S2;

/// <summary>
/// S2 Form 验收：提交时的校验、首提交之后的值变化重新校验、从 FormControl 写入的 ID / ARIA，
/// 以及错误消息、标签状态与点击标签聚焦。
/// </summary>
public class FormTests
{
    static FormTests() => ShadcnTheme.Register();

    static UIHarness Harness(Func<Node> render) =>
        new(render, appStyleSheets: true, width: 640, height: 480);

    static string? A(Element e, string name) => e.GetAttribute(name);

    static Element SingleSlot(UIHarness h, string slot) =>
        h.FindAll(e => A(e, "data-slot") == slot).Single();

    static Element Control(UIHarness h) =>
        h.FindAll(e => A(e, "data-form-control") == "true").Single();

    static Node EmailField() => new FormItem
    {
        Children =
        [
            new FormLabel { Children = [new UIText("Email")] },
            new FormControl { Children = [new Input { Placeholder = "you@example.com" }] },
            new FormDescription { Children = [new UIText("We never share it.")] },
            new FormMessage(),
        ],
    };

    [Fact]
    public void Form_submit_validates_and_wires_aria_and_message()
    {
        var controller = new FormController();
        bool? succeeded = null;
        controller.SubmitSucceeded = _ => succeeded = true;
        var h = Harness(() => new Form
        {
            Controller = controller,
            Children =
            [
                new FormField
                {
                    Name = "email",
                    Validators = [FormValidators.Required("Email is required.")],
                    Children = [EmailField()],
                },
            ],
        });

        var label = h.FindAll(e => A(e, "data-slot") == "label").Single();
        var description = SingleSlot(h, "form-description");
        var control = Control(h);

        // 未提交：没有错误，FormControl 写入 id / aria-describedby / aria-invalid=false
        Assert.Equal("false", A(label, "data-error"));
        Assert.Equal("false", A(control, "aria-invalid"));
        Assert.Equal(A(label, "for"), A(control, "id"));
        Assert.Equal(A(description, "id"), A(control, "aria-describedby"));
        Assert.Empty(h.FindAll(e => A(e, "data-slot") == "form-message"));
        Assert.Null(controller.GetError("email"));

        // 提交失败：错误消息、data-error、aria-invalid 与 aria-describedby 都更新
        Assert.False(controller.Submit());
        h.Frame();

        Assert.Null(succeeded);
        Assert.Equal("Email is required.", controller.GetError("email"));
        var message = SingleSlot(h, "form-message");
        Assert.Equal("true", A(label, "data-error"));
        Assert.Equal("true", A(control, "aria-invalid"));
        string describedBy = A(control, "aria-describedby")!;
        Assert.Contains(A(description, "id")!, describedBy);
        Assert.Contains(A(message, "id")!, describedBy);
        Assert.Equal("Email is required.", ((TextNodeElement)message.Children[0]).Text);

        // 首提交之后：输入合法值立即清除错误
        h.Click(control);
        h.TypeText("a@b.com");
        h.Frame();
        Assert.Null(controller.GetError("email"));
        Assert.Equal("a@b.com", controller.GetValue("email"));
        Assert.Equal("false", A(control, "aria-invalid"));
        Assert.Empty(h.FindAll(e => A(e, "data-slot") == "form-message"));
        Assert.Equal("false", A(label, "data-error"));
    }

    [Fact]
    public void Form_does_not_validate_on_change_before_first_submit()
    {
        var controller = new FormController();
        var h = Harness(() => new Form
        {
            Controller = controller,
            Children =
            [
                new FormField
                {
                    Name = "name",
                    Validators = [FormValidators.MinLength(3, "Too short.")],
                    Children = [EmailField()],
                },
            ],
        });

        var control = Control(h);
        h.Click(control);
        h.TypeText("x");
        h.Frame();

        // 未提交时不校验，也不显示消息
        Assert.Null(controller.GetError("name"));
        Assert.Empty(h.FindAll(e => A(e, "data-slot") == "form-message"));
        Assert.Equal("false", A(control, "aria-invalid"));

        Assert.False(controller.Submit());
        h.Frame();
        Assert.Equal("Too short.", controller.GetError("name"));

        // 提交后：值变化立即重新校验，直到合法
        h.TypeText("y");
        h.Frame();
        Assert.Equal("Too short.", controller.GetError("name"));
        h.TypeText("z");
        h.Frame();
        Assert.Null(controller.GetError("name"));
        Assert.Empty(h.FindAll(e => A(e, "data-slot") == "form-message"));
    }

    [Fact]
    public void Form_control_binds_default_value_and_reports_to_value_changed()
    {
        string? reported = null;
        var controller = new FormController();
        var h = Harness(() => new Form
        {
            Controller = controller,
            Children =
            [
                new FormField
                {
                    Name = "workspace",
                    DefaultValue = "Acme",
                    ValueChanged = v => reported = v,
                    Children =
                    [
                        new FormItem
                        {
                            Children =
                            [
                                new FormLabel { Children = [new UIText("Workspace")] },
                                new FormControl { Children = [new Input()] },
                            ],
                        },
                    ],
                },
            ],
        });

        var control = Assert.IsType<InputElement>(Control(h));
        Assert.Equal("Acme", control.Value);
        Assert.Equal("Acme", controller.GetValue("workspace"));

        h.Click(control);
        h.TypeText("!");
        h.Frame();
        Assert.Equal("Acme!", reported);
        Assert.Equal("Acme!", controller.GetValue("workspace"));
        Assert.Equal("Acme!", control.Value);
    }

    [Fact]
    public void Form_label_click_focuses_the_control()
    {
        var h = Harness(() => new Form
        {
            Children =
            [
                new FormField
                {
                    Name = "email",
                    Children = [EmailField()],
                },
            ],
        });

        var label = h.FindAll(e => A(e, "data-slot") == "label").Single();
        var control = Control(h);
        Assert.False(control.IsFocused);

        h.Click(label);
        Assert.True(control.IsFocused);
    }

    [Fact]
    public void Form_submit_success_invokes_callbacks_and_leaves_no_error()
    {
        var controller = new FormController();
        bool submitted = false;
        bool valid = false;
        bool invalid = false;
        controller.Submitted = _ => submitted = true;
        controller.SubmitSucceeded = _ => valid = true;
        controller.SubmitFailed = _ => invalid = true;

        var h = Harness(() => new Form
        {
            Controller = controller,
            Children =
            [
                new FormField
                {
                    Name = "email",
                    DefaultValue = "a@b.com",
                    Validators = [FormValidators.Required()],
                    Children = [EmailField()],
                },
            ],
        });

        Assert.True(controller.Submit());
        h.Frame();
        Assert.True(submitted);
        Assert.True(valid);
        Assert.False(invalid);
        Assert.False(controller.HasErrors);
        Assert.Empty(h.FindAll(e => A(e, "data-slot") == "form-message"));

        // Reset 清空已提交标记与错误
        controller.Reset();
        h.Frame();
        Assert.False(controller.IsSubmitted);
    }
}
