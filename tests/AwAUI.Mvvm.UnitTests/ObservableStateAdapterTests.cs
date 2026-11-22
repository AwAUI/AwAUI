using Xunit;

namespace AwAUI.Mvvm.UnitTests;

public sealed class ObservableStateAdapterTests
{
    [Fact]
    public void AdapterMirrorsSourceAndWritesBack()
    {
        var person = new Person { Name = "Ann" };
        using var adapter = ObservableStateAdapter.For<string>(person, "Name");

        Assert.Equal("Ann", adapter.State.Value);

        person.Name = "Bob";
        Assert.Equal("Bob", adapter.State.Value);

        adapter.Value = "Cara";
        Assert.Equal("Cara", person.Name);
        Assert.Equal("Cara", adapter.State.Value);
    }

    [Fact]
    public void DisposeStopsMirroring()
    {
        var person = new Person { Name = "Ann" };
        var adapter = ObservableStateAdapter.For<string>(person, "Name");

        adapter.Dispose();
        person.Name = "Bob";

        Assert.Equal("Ann", adapter.State.Value);
    }

    [Fact]
    public void ToStateMirrorsSourceChanges()
    {
        var person = new Person { Name = "Ann" };
        var state = ObservableStateAdapter.ToState<string>(person, "Name");

        Assert.Equal("Ann", state.Value);

        person.Name = "Bob";
        Assert.Equal("Bob", state.Value);
    }

    [Fact]
    public void ToComputedReevaluatesWhenSourceChanges()
    {
        var person = new Person { Age = 3 };
        var computed = ObservableStateAdapter.ToComputed<int>(person, "Age");

        Assert.Equal(3, computed.Value);

        person.Age = 9;
        Assert.Equal(9, computed.Value);
    }

    [Fact]
    public void AdapterSupportsNestedPaths()
    {
        var person = new Person { Address = new Address { City = "Paris" } };
        using var adapter = ObservableStateAdapter.For<string>(person, "Address.City");

        person.Address!.City = "Rome";
        Assert.Equal("Rome", adapter.State.Value);

        person.Address = new Address { City = "Oslo" };
        Assert.Equal("Oslo", adapter.State.Value);
    }
}
