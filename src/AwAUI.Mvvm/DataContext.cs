using System;
using System.Runtime.CompilerServices;
using AwAUI.UI;

namespace AwAUI.Mvvm;

/// <summary>
/// Ambient data context storage for a description tree. <see cref="DataContextScope"/> writes a value onto the
/// nodes it wraps; <see cref="Get(Node)"/> returns the nearest value written by a scope.
/// <para>
/// Values are stored in a <see cref="ConditionalWeakTable{TKey,TValue}"/> keyed by the description node, so they
/// never keep a discarded description tree alive. The table is thread-safe.
/// </para>
/// </summary>
public static class DataContext
{
    static readonly object s_null = new();
    static readonly ConditionalWeakTable<Node, object> s_values = new();

    /// <summary>Associates a value with a node, replacing any previous value.</summary>
    /// <param name="node">The description node.</param>
    /// <param name="value">The data context value; <c>null</c> is a valid value.</param>
    public static void Set(Node node, object? value)
    {
        if (node is null) throw new ArgumentNullException(nameof(node));
        s_values.Remove(node);
        s_values.Add(node, value ?? s_null);
    }

    /// <summary>Gets the value associated with a node, or <c>null</c> when none was set.</summary>
    /// <param name="node">The description node.</param>
    /// <returns>The associated value, or <c>null</c>.</returns>
    public static object? Get(Node node)
    {
        if (node is null) return null;
        return s_values.TryGetValue(node, out object? value) && !ReferenceEquals(value, s_null) ? value : null;
    }

    /// <summary>Attempts to get the value associated with a node.</summary>
    /// <param name="node">The description node.</param>
    /// <param name="value">The associated value when found.</param>
    /// <returns><c>true</c> when a value (including <c>null</c>) was associated with the node.</returns>
    public static bool TryGet(Node node, out object? value)
    {
        value = null;
        if (node is null || !s_values.TryGetValue(node, out object? stored)) return false;
        value = ReferenceEquals(stored, s_null) ? null : stored;
        return true;
    }

    /// <summary>Gets the value associated with a node cast to <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The expected reference type.</typeparam>
    /// <param name="node">The description node.</param>
    /// <returns>The value as <typeparamref name="T"/>, or <c>null</c>.</returns>
    public static T? Get<T>(Node node) where T : class => Get(node) as T;

    /// <summary>Removes the value associated with a node.</summary>
    /// <param name="node">The description node.</param>
    public static void Clear(Node node)
    {
        if (node is not null) s_values.Remove(node);
    }

    /// <summary>
    /// Writes <paramref name="value"/> onto <paramref name="node"/> and every descendant description node,
    /// stopping at component boundaries (a component node itself receives the value, but its own
    /// <c>Build()</c> output is not part of this description tree).
    /// </summary>
    /// <param name="value">The data context value.</param>
    /// <param name="node">The root description node; may be <c>null</c>.</param>
    /// <returns>The same <paramref name="node"/>, or an empty <see cref="Fragment"/> when it is <c>null</c>.</returns>
    public static Node Propagate(object? value, Node? node)
    {
        if (node is null) return new Fragment();
        Walk(value, node);
        return node;
    }

    static void Walk(object? value, Node node)
    {
        Set(node, value);
        // Components build their own descriptions later; stop here so nested scopes can override.
        if (node is Component) return;
        if (node is ElementNode element)
        {
            foreach (var child in element.Children) Walk(value, child);
        }
        else if (node is Fragment fragment)
        {
            foreach (var child in fragment.Children) Walk(value, child);
        }
    }
}

/// <summary>
/// Provides a <see cref="DataContext"/> value to the description tree returned by <see cref="Child"/>.
/// Nested scopes override the value for their own subtree.
/// </summary>
public sealed partial class DataContextScope : Component
{
    /// <summary>The data context value provided to the subtree.</summary>
    public object? Value { get; set; }

    /// <summary>The wrapped description node.</summary>
    public Node? Child { get; set; }

    /// <inheritdoc />
    protected override Node Build() => DataContext.Propagate(Value, Child);

    /// <summary>
    /// Applies <see cref="Value"/> to <see cref="Child"/> without mounting the scope. Useful for tests and for
    /// command-style description construction.
    /// </summary>
    /// <returns>The wrapped node, or an empty <see cref="Fragment"/> when <see cref="Child"/> is <c>null</c>.</returns>
    public Node Apply() => DataContext.Propagate(Value, Child);
}
