using System;
using System.Globalization;
using AwAUI.UI;
using Xunit;

namespace AwAUI.Mvvm.UnitTests;

public sealed class BindingTests
{
    [Fact]
    public void OneWayWritesInitialAndSubsequentValues()
    {
        var person = new Person { Name = "Ann" };
        var target = new RecordingTarget();

        using var expression = new Binding("Name").Attach(person, target);

        Assert.Equal("Ann", target.Value);

        person.Name = "Bob";

        Assert.Equal("Bob", target.Value);
    }

    [Fact]
    public void OneTimeWritesOnlyInitialValue()
    {
        var person = new Person { Name = "Ann" };
        var target = new RecordingTarget();

        using var expression = new Binding("Name") { Mode = BindingMode.OneTime }.Attach(person, target);
        person.Name = "Bob";

        Assert.Equal("Ann", target.Value);
    }

    [Fact]
    public void TwoWayWritesTargetChangesBackToSource()
    {
        var person = new Person { Name = "Ann" };
        var target = new RecordingTarget();

        using var expression = new Binding("Name") { Mode = BindingMode.TwoWay }.Attach(person, target);
        target.Raise("Carol");

        Assert.Equal("Carol", person.Name);
        Assert.Equal("Carol", target.Value);
    }

    [Fact]
    public void TwoWayDoesNotWriteEqualValuesBack()
    {
        var person = new Person { Name = "Ann" };
        var target = new RecordingTarget();
        using var expression = new Binding("Name") { Mode = BindingMode.TwoWay }.Attach(person, target);

        int notifications = 0;
        person.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Person.Name)) notifications++;
        };

        target.Raise("Ann");

        Assert.Equal(0, notifications);
        Assert.Equal("Ann", person.Name);
    }

    [Fact]
    public void NestedPathTracksIntermediateReplacement()
    {
        var person = new Person { Address = new Address { City = "A" } };
        var target = new RecordingTarget();
        using var expression = new Binding("Address.City").Attach(person, target);

        Assert.Equal("A", target.Value);

        person.Address!.City = "B";
        Assert.Equal("B", target.Value);

        person.Address = new Address { City = "C" };
        Assert.Equal("C", target.Value);
    }

    [Fact]
    public void ConverterConvertsBothDirections()
    {
        var person = new Person { Age = 5 };
        var target = new RecordingTarget();
        var converter = new FuncValueConverter<int, string>(value => "#" + value.ToString(CultureInfo.InvariantCulture),
            value => int.Parse(value.TrimStart('#'), CultureInfo.InvariantCulture));

        using var expression = new Binding("Age")
        {
            Mode = BindingMode.TwoWay,
            Converter = converter,
        }.Attach(person, target);

        Assert.Equal("#5", target.Value);

        target.Raise("#7");

        Assert.Equal(7, person.Age);
    }

    [Fact]
    public void StringFormatFormatsConvertedValue()
    {
        var person = new Person { Age = 5 };
        var target = new RecordingTarget();

        using var expression = new Binding("Age")
        {
            StringFormat = "N2",
            ConverterCulture = CultureInfo.InvariantCulture,
        }.Attach(person, target);

        Assert.Equal("5.00", target.Value);
    }

    [Fact]
    public void FallbackValueIsUsedWhenPathCannotBeResolved()
    {
        var person = new Person { Address = null };
        var target = new RecordingTarget();

        using var expression = new Binding("Address.City") { FallbackValue = "n/a" }.Attach(person, target);

        Assert.Equal("n/a", target.Value);
    }

    [Fact]
    public void TargetNullValueIsUsedForNullSource()
    {
        var person = new Person { Address = null };
        var target = new RecordingTarget();

        using var expression = new Binding("Address") { TargetNullValue = "(none)" }.Attach(person, target);

        Assert.Equal("(none)", target.Value);
    }

    [Fact]
    public void DisposeStopsObservingSource()
    {
        var person = new Person { Name = "Ann" };
        var target = new RecordingTarget();
        var expression = new Binding("Name").Attach(person, target);

        expression.Dispose();
        person.Name = "Bob";

        Assert.Equal("Ann", target.Value);
    }

    [Fact]
    public void StateTargetReceivesSourceChanges()
    {
        var person = new Person { Name = "Ann" };
        var state = new State<string>("initial");

        using var expression = new Binding("Name").AttachTo(person, state);

        Assert.Equal("Ann", state.Value);

        person.Name = "Bob";
        Assert.Equal("Bob", state.Value);
    }

    [Fact]
    public void TwoWayStateTargetWritesBackToSource()
    {
        var person = new Person { Name = "Ann" };
        var state = new State<string>("Ann");

        using var expression = new Binding("Name") { Mode = BindingMode.TwoWay }.AttachTo(person, state);
        state.Value = "Cara";

        Assert.Equal("Cara", person.Name);
    }

    sealed class RecordingTarget : IBindingTarget
    {
        Action<object?>? _onChanged;

        public object? Value { get; private set; }

        public Type? TargetType => typeof(object);

        public object? GetValue() => Value;

        public void SetValue(object? value) => Value = value;

        public IDisposable? Subscribe(Action<object?> onChanged)
        {
            _onChanged = onChanged;
            return new Disposable(() => _onChanged = null);
        }

        public void Raise(object? value)
        {
            Value = value;
            _onChanged?.Invoke(value);
        }

        sealed class Disposable : IDisposable
        {
            readonly Action _dispose;

            public Disposable(Action dispose) => _dispose = dispose;

            public void Dispose() => _dispose();
        }
    }
}
