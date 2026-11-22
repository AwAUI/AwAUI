using System;
using Xunit;

namespace AwAUI.Mvvm.UnitTests;

public sealed class PropertyPathTests
{
    [Fact]
    public void ParseSplitsSimpleSegments()
    {
        var path = PropertyPath.Parse("Address.City");

        Assert.Equal(2, path.Segments.Count);
        Assert.Equal("Address", path.Segments[0].Name);
        Assert.Equal("City", path.Segments[1].Name);
        Assert.False(path.IsEmpty);
        Assert.Equal("Address.City", path.Path);
    }

    [Fact]
    public void GetValueResolvesNestedProperties()
    {
        var person = new Person { Address = new Address { City = "Paris" } };

        Assert.Equal("Paris", PropertyPath.Parse("Address.City").GetValue(person));
    }

    [Fact]
    public void GetValueResolvesIndexers()
    {
        var person = new Person();
        person.Items.Add(new Item { Name = "First" });
        person.Items.Add(new Item { Name = "Second" });

        Assert.Equal("First", PropertyPath.Parse("Items[0].Name").GetValue(person));
        Assert.Equal("Second", PropertyPath.Parse("Items[1].Name").GetValue(person));
        Assert.Equal("Second", PropertyPath.Parse("Items[1].Name").GetValue(person));
    }

    [Fact]
    public void SetValueWritesNestedProperty()
    {
        var person = new Person { Address = new Address() };

        PropertyPath.Parse("Address.City").SetValue(person, "Berlin");

        Assert.Equal("Berlin", person.Address!.City);
    }

    [Fact]
    public void SetValueWritesThroughIndexer()
    {
        var person = new Person();
        person.Items.Add(new Item { Name = "Old" });

        PropertyPath.Parse("Items[0].Name").SetValue(person, "New");

        Assert.Equal("New", person.Items[0].Name);
    }

    [Fact]
    public void SetValueWritesIndexerItself()
    {
        var person = new Person();
        person.Items.Add(new Item { Name = "Old" });

        PropertyPath.Parse("Items[0]").SetValue(person, new Item { Name = "Replaced" });

        Assert.Equal("Replaced", person.Items[0].Name);
    }

    [Fact]
    public void TryGetValueReturnsFalseForNullIntermediate()
    {
        var person = new Person { Address = null };

        Assert.False(PropertyPath.Parse("Address.City").TryGetValue(person, out var value));
        Assert.Null(value);
    }

    [Fact]
    public void TrySetValueReturnsFalseForReadOnlyProperty()
    {
        var person = new Person();

        Assert.False(PropertyPath.Parse("ReadOnly").TrySetValue(person, 7));
    }

    [Fact]
    public void TrySetValueReturnsFalseForNullRoot()
    {
        Assert.False(PropertyPath.Parse("Name").TrySetValue(null, "x"));
    }

    [Theory]
    [InlineData("A..B")]
    [InlineData(".A")]
    [InlineData("A.")]
    [InlineData("A[")]
    [InlineData("A[0")]
    [InlineData("A[]")]
    [InlineData("A[0]B")]
    public void InvalidPathsThrowFormatException(string text)
    {
        Assert.Throws<FormatException>(() => PropertyPath.Parse(text));
        Assert.False(PropertyPath.TryParse(text, out _));
    }

    [Fact]
    public void EmptyPathIsValidAndReturnsRoot()
    {
        var person = new Person();
        var path = PropertyPath.Parse("   ");

        Assert.True(path.IsEmpty);
        Assert.Same(person, path.GetValue(person));
    }

    [Fact]
    public void QuotedStringIndexerIsParsed()
    {
        var path = PropertyPath.Parse("Map[\"key\"]");

        Assert.Single(path.Segments);
        Assert.Equal("key", path.Segments[0].Indexes[0]);
    }

    [Fact]
    public void PathEqualityUsesText()
    {
        Assert.Equal(PropertyPath.Parse("Address.City"), PropertyPath.Parse("Address.City"));
        Assert.NotEqual(PropertyPath.Parse("Address.City"), PropertyPath.Parse("Address.Name"));
    }
}
