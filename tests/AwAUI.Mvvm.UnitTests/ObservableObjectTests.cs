using System.Collections.Generic;
using Xunit;

namespace AwAUI.Mvvm.UnitTests;

public sealed class ObservableObjectTests
{
    [Fact]
    public void SetPropertyRaisesPropertyChangedWithCallerMemberName()
    {
        var person = new Person();
        var names = new List<string?>();
        person.PropertyChanged += (_, e) => names.Add(e.PropertyName);

        person.Name = "Ada";

        Assert.Equal("Ada", person.Name);
        Assert.Equal(new[] { "Name" }, names);
    }

    [Fact]
    public void SetPropertyWithEqualValueDoesNotRaise()
    {
        var person = new Person { Age = 5 };
        int count = 0;
        person.PropertyChanged += (_, _) => count++;

        person.Age = 5;

        Assert.Equal(0, count);
    }

    [Fact]
    public void RaisePropertyChangedNotifiesExplicitName()
    {
        var person = new PersonNotificationProbe();
        string? observed = null;
        person.PropertyChanged += (_, e) => observed = e.PropertyName;

        person.RaisePropertyChangedForTest("Age");

        Assert.Equal("Age", observed);
    }

    [Fact]
    public void RaisePropertyChangedNotifiesMultipleNames()
    {
        var person = new PersonNotificationProbe();
        var names = new List<string?>();
        person.PropertyChanged += (_, e) => names.Add(e.PropertyName);

        person.RaisePropertyChangedForTest("Name", "Age");

        Assert.Equal(new[] { "Name", "Age" }, names);
    }
}

public sealed class PersonNotificationProbe : Person
{
    public void RaisePropertyChangedForTest(params string[] names) => RaisePropertyChanged(names);
}
