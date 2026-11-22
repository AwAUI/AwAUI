using AwAUI.UI;
using Xunit;

namespace AwAUI.Mvvm.UnitTests;

public sealed class ValidationBindingTests
{
    [Fact]
    public void ConstructorAppliesExistingErrors()
    {
        var model = new ValidatingModel();
        model.AddError(nameof(Person.Name), "Name is required");
        var target = new Div();

        using var binding = new ValidationBinding(model, target, nameof(Person.Name));

        Assert.True(binding.HasErrors);
        Assert.Equal("Name is required", binding.ErrorText);
        Assert.Equal("true", target.Attributes["data-invalid"]);
        Assert.Equal("true", target.Attributes["aria-invalid"]);
        Assert.Equal("Name is required", target.Attributes["data-error-text"]);
    }

    [Fact]
    public void ErrorsChangedRefreshesAttributes()
    {
        var model = new ValidatingModel();
        var target = new Div();
        using var binding = new ValidationBinding(model, target, nameof(Person.Name));

        Assert.False(target.Attributes.Contains("data-invalid"));

        model.AddError(nameof(Person.Name), "Too short");

        Assert.Equal("true", target.Attributes["data-invalid"]);
        Assert.Equal("Too short", target.Attributes["data-error-text"]);

        model.ClearErrors(nameof(Person.Name));

        Assert.False(target.Attributes.Contains("data-invalid"));
        Assert.False(target.Attributes.Contains("aria-invalid"));
        Assert.False(target.Attributes.Contains("data-error-text"));
    }

    [Fact]
    public void MultipleErrorsAreJoined()
    {
        var model = new ValidatingModel();
        var target = new Div();
        using var binding = new ValidationBinding(model, target, nameof(Person.Name));

        model.AddError(nameof(Person.Name), "First");
        model.AddError(nameof(Person.Name), "Second");

        Assert.Equal("First; Second", binding.ErrorText);
        Assert.Equal("First; Second", target.Attributes["data-error-text"]);
    }

    [Fact]
    public void ErrorsForOtherPropertiesAreIgnored()
    {
        var model = new ValidatingModel();
        var target = new Div();
        using var binding = new ValidationBinding(model, target, nameof(Person.Name));

        model.AddError(nameof(Person.Age), "Invalid age");

        Assert.False(target.Attributes.Contains("data-invalid"));
    }

    [Fact]
    public void EntityLevelErrorsUseNullPropertyName()
    {
        var model = new ValidatingModel();
        var target = new Div();
        using var binding = new ValidationBinding(model, target);
        model.AddError(string.Empty, "Entity is invalid");

        // Entity-level errors are read with a null property name, so a string.Empty error is not observed.
        Assert.False(binding.HasErrors);
        Assert.Null(target.Attributes["data-invalid"]);
    }

    [Fact]
    public void DisposeStopsRefreshing()
    {
        var model = new ValidatingModel();
        var target = new Div();
        var binding = new ValidationBinding(model, target, nameof(Person.Name));

        binding.Dispose();
        model.AddError(nameof(Person.Name), "Ignored");

        Assert.False(target.Attributes.Contains("data-invalid"));
    }
}
