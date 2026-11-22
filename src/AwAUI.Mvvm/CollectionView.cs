using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;

namespace AwAUI.Mvvm;

/// <summary>Sort direction for a <see cref="SortDescription"/>.</summary>
public enum ListSortDirection
{
    /// <summary>Ascending order.</summary>
    Ascending,

    /// <summary>Descending order.</summary>
    Descending,
}

/// <summary>Describes one sort key of a <see cref="CollectionView{T}"/>.</summary>
public sealed class SortDescription
{
    /// <summary>Creates an ascending sort description.</summary>
    /// <param name="propertyName">The property path used as the key.</param>
    public SortDescription(string propertyName) : this(propertyName, ListSortDirection.Ascending) { }

    /// <summary>Creates a sort description.</summary>
    /// <param name="propertyName">The property path used as the key.</param>
    /// <param name="direction">The sort direction.</param>
    public SortDescription(string propertyName, ListSortDirection direction)
    {
        PropertyName = propertyName ?? throw new ArgumentNullException(nameof(propertyName));
        Direction = direction;
    }

    /// <summary>The property path used as the key.</summary>
    public string PropertyName { get; }

    /// <summary>The sort direction.</summary>
    public ListSortDirection Direction { get; }
}

/// <summary>Describes one grouping level of a <see cref="CollectionView{T}"/>.</summary>
public sealed class GroupDescription
{
    /// <summary>Creates a grouping description.</summary>
    /// <param name="propertyName">The property path used as the group key.</param>
    public GroupDescription(string propertyName)
        => PropertyName = propertyName ?? throw new ArgumentNullException(nameof(propertyName));

    /// <summary>The property path used as the group key.</summary>
    public string PropertyName { get; }
}

/// <summary>A group produced by a <see cref="CollectionView{T}"/>.</summary>
/// <typeparam name="T">The item type.</typeparam>
public sealed class CollectionViewGroup<T>
{
    internal CollectionViewGroup(object? key, List<T> items, List<CollectionViewGroup<T>> groups)
    {
        Key = key;
        Items = items.AsReadOnly();
        Groups = groups.AsReadOnly();
    }

    /// <summary>The group key.</summary>
    public object? Key { get; }

    /// <summary>The items in this group; empty when the group has subgroups.</summary>
    public IReadOnlyList<T> Items { get; }

    /// <summary>The subgroups of this group.</summary>
    public IReadOnlyList<CollectionViewGroup<T>> Groups { get; }

    /// <summary>Whether this group is split into subgroups.</summary>
    public bool HasSubgroups => Groups.Count > 0;

    /// <summary>The number of direct items in this group.</summary>
    public int ItemCount => Items.Count;
}

/// <summary>
/// A filterable, sortable and groupable view over an <see cref="IEnumerable{T}"/>. Source collection changes
/// trigger a refresh and are reported as a <see cref="NotifyCollectionChangedAction.Reset"/>.
/// </summary>
/// <typeparam name="T">The item type.</typeparam>
public sealed class CollectionView<T> : IReadOnlyList<T>, INotifyCollectionChanged, INotifyPropertyChanged, IDisposable
{
    readonly IEnumerable<T> _source;
    readonly INotifyCollectionChanged? _sourceChanged;
    List<T> _view = new();
    List<CollectionViewGroup<T>> _groups = new();
    Predicate<T>? _filter;
    int _currentPosition = -1;
    T? _currentItem;
    bool _hasCurrent;
    bool _refreshing;
    bool _disposed;

    /// <summary>Creates a view over a source sequence.</summary>
    /// <param name="source">The source sequence.</param>
    public CollectionView(IEnumerable<T> source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        if (source is INotifyCollectionChanged changed)
        {
            _sourceChanged = changed;
            changed.CollectionChanged += OnSourceCollectionChanged;
        }
        Refresh();
    }

    /// <summary>The wrapped source sequence.</summary>
    public IEnumerable<T> Source => _source;

    /// <summary>The optional item filter. Assigning a filter refreshes the view.</summary>
    public Predicate<T>? Filter
    {
        get => _filter;
        set
        {
            _filter = value;
            Refresh();
        }
    }

    /// <summary>The sort descriptions, applied in order.</summary>
    public IList<SortDescription> SortDescriptions { get; } = new List<SortDescription>();

    /// <summary>The grouping descriptions, applied as nested levels in order.</summary>
    public IList<GroupDescription> GroupDescriptions { get; } = new List<GroupDescription>();

    /// <summary>The top-level groups; empty when <see cref="GroupDescriptions"/> is empty.</summary>
    public IReadOnlyList<CollectionViewGroup<T>> Groups => _groups;

    /// <summary>The number of items in the view.</summary>
    public int Count => _view.Count;

    /// <summary>Gets the item at an index in the view.</summary>
    /// <param name="index">The view index.</param>
    /// <returns>The item.</returns>
    public T this[int index] => _view[index];

    /// <summary>The current item; the first item after construction and after refreshes that keep it in view.</summary>
    public T? CurrentItem => _hasCurrent ? _currentItem : default;

    /// <summary>The current item position, or -1 when there is no current item.</summary>
    public int CurrentPosition => _currentPosition;

    /// <summary>Whether <see cref="CurrentPosition"/> addresses an item in the view.</summary>
    public bool IsCurrentInView => _currentPosition >= 0 && _currentPosition < _view.Count;

    /// <inheritdoc />
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when <see cref="CurrentItem"/> or <see cref="CurrentPosition"/> changes.</summary>
    public event EventHandler? CurrentChanged;

    /// <summary>Reapplies filtering, sorting and grouping to the source.</summary>
    public void Refresh()
    {
        if (_refreshing || _disposed) return;
        _refreshing = true;

        T oldItem = _hasCurrent ? _currentItem! : default!;
        int oldPosition = _currentPosition;
        bool oldHasCurrent = _hasCurrent;

        try
        {
            IEnumerable<T> query = _source;
            if (Filter is not null) query = query.Where(item => Filter(item));

            var items = query.ToList();
            if (SortDescriptions.Count > 0) items = Sort(items);
            _view = items;
            _groups = BuildGroups(items, 0);

            if (_hasCurrent)
            {
                int position = _view.IndexOf(_currentItem!);
                if (position >= 0)
                {
                    _currentPosition = position;
                    _currentItem = _view[position];
                    _hasCurrent = true;
                }
                else if (_view.Count > 0)
                {
                    _currentPosition = 0;
                    _currentItem = _view[0];
                    _hasCurrent = true;
                }
                else
                {
                    _currentPosition = -1;
                    _currentItem = default;
                    _hasCurrent = false;
                }
            }
            else if (_view.Count > 0)
            {
                _currentPosition = 0;
                _currentItem = _view[0];
                _hasCurrent = true;
            }
            else
            {
                _currentPosition = -1;
                _currentItem = default;
                _hasCurrent = false;
            }
        }
        finally
        {
            _refreshing = false;
        }

        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(Groups));
        OnPropertyChanged(nameof(CurrentItem));
        OnPropertyChanged(nameof(CurrentPosition));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));

        bool currentChanged = oldHasCurrent != _hasCurrent || oldPosition != _currentPosition ||
            !EqualityComparer<T>.Default.Equals(oldItem, _currentItem!);
        if (currentChanged) CurrentChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Moves the current item to a view position.</summary>
    /// <param name="position">The new position, or -1 to clear the current item.</param>
    /// <returns><c>true</c> when the position is valid and the current item changed.</returns>
    public bool MoveCurrentToPosition(int position)
    {
        if (position < -1 || position >= _view.Count) return false;
        if (position == _currentPosition && _hasCurrent == (position >= 0)) return true;

        _currentPosition = position;
        _hasCurrent = position >= 0;
        _currentItem = _hasCurrent ? _view[position] : default;
        OnPropertyChanged(nameof(CurrentItem));
        OnPropertyChanged(nameof(CurrentPosition));
        CurrentChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Moves the current item to a specific item.</summary>
    /// <param name="item">The item to make current.</param>
    /// <returns><c>true</c> when the item is in the view.</returns>
    public bool MoveCurrentTo(T item) => MoveCurrentToPosition(_view.IndexOf(item));

    /// <summary>Moves the current item to the first item.</summary>
    /// <returns><c>true</c> when the view is not empty.</returns>
    public bool MoveCurrentToFirst() => MoveCurrentToPosition(0);

    /// <summary>Moves the current item to the last item.</summary>
    /// <returns><c>true</c> when the view is not empty.</returns>
    public bool MoveCurrentToLast() => MoveCurrentToPosition(_view.Count - 1);

    /// <summary>Moves the current item to the next item.</summary>
    /// <returns><c>true</c> when a next item exists.</returns>
    public bool MoveCurrentToNext() => MoveCurrentToPosition(_currentPosition + 1);

    /// <summary>Moves the current item to the previous item.</summary>
    /// <returns><c>true</c> when a previous item exists.</returns>
    public bool MoveCurrentToPrevious() => MoveCurrentToPosition(_currentPosition - 1);

    List<T> Sort(List<T> items)
    {
        IOrderedEnumerable<T>? ordered = null;
        var comparer = ObjectComparer.Instance;
        foreach (var description in SortDescriptions)
        {
            var path = PropertyPath.Parse(description.PropertyName);
            Func<T, object?> key = item => path.TryGetValue(item, out object? value) ? value : null;
            bool ascending = description.Direction == ListSortDirection.Ascending;
            ordered = ordered is null
                ? ascending ? items.OrderBy(key, comparer) : items.OrderByDescending(key, comparer)
                : ascending ? ordered.ThenBy(key, comparer) : ordered.ThenByDescending(key, comparer);
        }
        return ordered?.ToList() ?? items;
    }

    List<CollectionViewGroup<T>> BuildGroups(List<T> items, int depth)
    {
        if (depth >= GroupDescriptions.Count) return new List<CollectionViewGroup<T>>();

        var path = PropertyPath.Parse(GroupDescriptions[depth].PropertyName);
        var buckets = new List<GroupBucket>();
        foreach (var item in items)
        {
            object? key = path.TryGetValue(item, out object? value) ? value : null;
            GroupBucket? bucket = null;
            foreach (var candidate in buckets)
            {
                if (Equals(candidate.Key, key))
                {
                    bucket = candidate;
                    break;
                }
            }
            if (bucket is null)
            {
                bucket = new GroupBucket(key);
                buckets.Add(bucket);
            }
            bucket.Items.Add(item);
        }

        bool hasSubgroups = depth + 1 < GroupDescriptions.Count;
        var groups = new List<CollectionViewGroup<T>>(buckets.Count);
        foreach (var bucket in buckets)
        {
            var subgroups = hasSubgroups ? BuildGroups(bucket.Items, depth + 1) : new List<CollectionViewGroup<T>>();
            var groupItems = hasSubgroups ? new List<T>() : bucket.Items;
            groups.Add(new CollectionViewGroup<T>(bucket.Key, groupItems, subgroups));
        }
        return groups;
    }

    void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
        => CollectionChanged?.Invoke(this, e);

    /// <inheritdoc />
    public IEnumerator<T> GetEnumerator() => _view.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_sourceChanged is not null) _sourceChanged.CollectionChanged -= OnSourceCollectionChanged;
    }

    sealed class GroupBucket
    {
        public GroupBucket(object? key) => Key = key;

        public object? Key { get; }

        public List<T> Items { get; } = new();
    }

    sealed class ObjectComparer : IComparer<object?>
    {
        public static readonly ObjectComparer Instance = new();

        public int Compare(object? x, object? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            if (x.GetType() == y.GetType() && x is IComparable comparable)
                return comparable.CompareTo(y);

            if (x is IComparable left)
            {
                try
                {
                    return left.CompareTo(y);
                }
                catch (ArgumentException)
                {
                    // Fall back to text comparison for heterogeneous values.
                }
            }

            return string.Compare(
                Convert.ToString(x, CultureInfo.InvariantCulture),
                Convert.ToString(y, CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
        }
    }
}
