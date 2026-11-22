using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Xunit;

namespace AwAUI.Mvvm.UnitTests;

public sealed class CollectionViewTests
{
    [Fact]
    public void FilterLimitsItems()
    {
        var people = new[]
        {
            new Person { Name = "A", Age = 10 },
            new Person { Name = "B", Age = 20 },
            new Person { Name = "C", Age = 30 },
        };

        var view = new CollectionView<Person>(people) { Filter = person => person.Age >= 20 };

        Assert.Equal(2, view.Count);
        Assert.Equal("B", view[0].Name);
        Assert.Equal("C", view[1].Name);
    }

    [Fact]
    public void SortDescriptionsAreAppliedInOrder()
    {
        var people = new[]
        {
            new Person { Name = "B", Age = 20 },
            new Person { Name = "A", Age = 30 },
            new Person { Name = "A", Age = 20 },
        };

        using var view = new CollectionView<Person>(people);
        view.SortDescriptions.Add(new SortDescription("Age"));
        view.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Descending));
        view.Refresh();

        Assert.Equal(new[] { "B", "A", "A" }, view.Select(person => person.Name));
    }

    [Fact]
    public void NestedSortPathResolves()
    {
        var people = new[]
        {
            new Person { Name = "A", Address = new Address { City = "Zurich" } },
            new Person { Name = "B", Address = new Address { City = "Athens" } },
        };

        using var view = new CollectionView<Person>(people);
        view.SortDescriptions.Add(new SortDescription("Address.City"));
        view.Refresh();

        Assert.Equal(new[] { "B", "A" }, view.Select(person => person.Name));
    }

    [Fact]
    public void GroupDescriptionsProduceGroups()
    {
        var people = new[]
        {
            new Person { Name = "A", Age = 20 },
            new Person { Name = "B", Age = 20 },
            new Person { Name = "C", Age = 30 },
        };

        using var view = new CollectionView<Person>(people);
        view.GroupDescriptions.Add(new GroupDescription("Age"));
        view.Refresh();

        Assert.Equal(2, view.Groups.Count);
        var age20 = view.Groups.Single(group => Equals(group.Key, 20));
        Assert.Equal(2, age20.ItemCount);
        Assert.False(age20.HasSubgroups);
    }

    [Fact]
    public void NestedGroupDescriptionsProduceSubgroups()
    {
        var people = new[]
        {
            new Person { Name = "A", Age = 20, Address = new Address { City = "Paris" } },
            new Person { Name = "B", Age = 20, Address = new Address { City = "Berlin" } },
        };

        using var view = new CollectionView<Person>(people);
        view.GroupDescriptions.Add(new GroupDescription("Age"));
        view.GroupDescriptions.Add(new GroupDescription("Address.City"));
        view.Refresh();

        var age20 = Assert.Single(view.Groups);
        Assert.True(age20.HasSubgroups);
        Assert.Equal(2, age20.Groups.Count);
        Assert.Equal(0, age20.ItemCount);
    }

    [Fact]
    public void SourceCollectionChangesRefreshAndRaiseCollectionChanged()
    {
        var source = new ObservableCollection<Person> { new Person { Name = "A" } };
        using var view = new CollectionView<Person>(source);
        int notifications = 0;
        view.CollectionChanged += (_, _) => notifications++;

        source.Add(new Person { Name = "B" });

        Assert.Equal(2, view.Count);
        Assert.True(notifications > 0);
    }

    [Fact]
    public void CurrentItemNavigatesTheView()
    {
        var first = new Person { Name = "A" };
        var second = new Person { Name = "B" };
        var view = new CollectionView<Person>(new[] { first, second });

        Assert.Same(first, view.CurrentItem);
        Assert.Equal(0, view.CurrentPosition);
        Assert.True(view.IsCurrentInView);

        Assert.True(view.MoveCurrentTo(second));
        Assert.Same(second, view.CurrentItem);
        Assert.Equal(1, view.CurrentPosition);

        Assert.False(view.MoveCurrentToNext());
        Assert.True(view.MoveCurrentToFirst());
        Assert.Same(first, view.CurrentItem);
    }

    [Fact]
    public void DisposeStopsObservingSourceChanges()
    {
        var source = new ObservableCollection<Person> { new Person { Name = "A" } };
        var view = new CollectionView<Person>(source);

        view.Dispose();
        source.Add(new Person { Name = "B" });

        Assert.Single(view);
    }

    [Fact]
    public void RefreshPreservesCurrentItemWhenStillVisible()
    {
        var first = new Person { Name = "A", Age = 10 };
        var second = new Person { Name = "B", Age = 20 };
        using var view = new CollectionView<Person>(new[] { first, second });
        view.MoveCurrentTo(second);

        view.Filter = person => person.Age >= 10;
        view.Refresh();

        Assert.Same(second, view.CurrentItem);
        Assert.Equal(1, view.CurrentPosition);
    }
}
