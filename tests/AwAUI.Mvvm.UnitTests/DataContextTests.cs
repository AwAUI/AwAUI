using AwAUI.UI;
using Xunit;

namespace AwAUI.Mvvm.UnitTests;

public sealed class DataContextTests
{
    [Fact]
    public void PropagateWritesValueToDescendants()
    {
        var child = new Div();
        var root = new Div { Children = { child } };

        var result = DataContext.Propagate("context", root);

        Assert.Same(root, result);
        Assert.Equal("context", DataContext.Get(root));
        Assert.Equal("context", DataContext.Get(child));
    }

    [Fact]
    public void NestedScopesOverrideForTheirSubtree()
    {
        var innerChild = new Div();
        var innerScope = new DataContextScope { Value = "inner", Child = innerChild };
        var outerChild = new Div();
        var outer = new Div { Children = { outerChild, innerScope } };

        new DataContextScope { Value = "outer", Child = outer }.Apply();
        innerScope.Apply();

        Assert.Equal("outer", DataContext.Get(outerChild));
        Assert.Equal("outer", DataContext.Get(innerScope));
        Assert.Equal("inner", DataContext.Get(innerChild));
    }

    [Fact]
    public void ComponentBoundaryStopsPropagation()
    {
        var content = new Div();
        var component = new TestComponent { Content = content };

        new DataContextScope { Value = "outer", Child = component }.Apply();

        Assert.Equal("outer", DataContext.Get(component));
        Assert.Null(DataContext.Get(content));
    }

    [Fact]
    public void TypedGetReturnsValueWhenTypeMatches()
    {
        var node = new Div();
        var value = new Address { City = "Paris" };
        DataContext.Set(node, value);

        Assert.Same(value, DataContext.Get<Address>(node));
        Assert.Null(DataContext.Get<Person>(node));
    }

    [Fact]
    public void NullIsAValidStoredValue()
    {
        var node = new Div();
        DataContext.Set(node, null);

        Assert.True(DataContext.TryGet(node, out var value));
        Assert.Null(value);
        Assert.Null(DataContext.Get(node));
    }

    [Fact]
    public void ClearRemovesValue()
    {
        var node = new Div();
        DataContext.Set(node, "x");

        DataContext.Clear(node);

        Assert.False(DataContext.TryGet(node, out _));
        Assert.Null(DataContext.Get(node));
    }

    [Fact]
    public void FragmentPropagatesToChildren()
    {
        var child = new Div();
        var fragment = new Fragment { Children = { child } };

        new DataContextScope { Value = 42, Child = fragment }.Apply();

        Assert.Equal(42, DataContext.Get(child));
    }
}
