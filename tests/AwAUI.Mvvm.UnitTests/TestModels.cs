using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;

namespace AwAUI.Mvvm.UnitTests;

public class Person : ObservableObject
{
    string _name = "";
    int _age;
    Address? _address;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public int Age
    {
        get => _age;
        set => SetProperty(ref _age, value);
    }

    public Address? Address
    {
        get => _address;
        set => SetProperty(ref _address, value);
    }

    public List<Item> Items { get; } = new();

    public int ReadOnly => 42;

    public string this[int index]
    {
        get => Items[index].Name;
        set => Items[index].Name = value;
    }
}

public sealed class Address : ObservableObject
{
    string _city = "";

    public string City
    {
        get => _city;
        set => SetProperty(ref _city, value);
    }
}

public sealed class Item : ObservableObject
{
    string _name = "";

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }
}

public sealed class ValidatingModel : ObservableObject, INotifyDataErrorInfo
{
    readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

    public bool HasErrors => _errors.Count > 0;

    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    public IEnumerable GetErrors(string? propertyName)
        => propertyName is not null && _errors.TryGetValue(propertyName, out var list)
            ? list
            : Array.Empty<string>();

    public void AddError(string propertyName, string message)
    {
        if (!_errors.TryGetValue(propertyName, out var list))
        {
            list = new List<string>();
            _errors[propertyName] = list;
        }
        list.Add(message);
        ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(propertyName));
    }

    public void ClearErrors(string propertyName)
    {
        if (_errors.Remove(propertyName))
            ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(propertyName));
    }
}

public sealed class TestComponent : AwAUI.UI.Component
{
    public AwAUI.UI.Node? Content { get; set; }

    protected override AwAUI.UI.Node Build() => Content ?? new AwAUI.UI.Fragment();
}
