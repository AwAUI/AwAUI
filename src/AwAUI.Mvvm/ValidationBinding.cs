using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using AwAUI.UI;

namespace AwAUI.Mvvm;

/// <summary>
/// Projects <see cref="INotifyDataErrorInfo"/> errors for a property onto a target <see cref="ElementNode"/>.
/// The node receives <c>data-invalid</c>, <c>aria-invalid</c> and <c>data-error-text</c> custom attributes, which
/// the element diff applies to the live element and CSS can match.
/// </summary>
public sealed class ValidationBinding : IDisposable
{
    readonly INotifyDataErrorInfo _source;
    readonly ElementNode _target;
    readonly string? _propertyName;
    IReadOnlyList<string> _errors = Array.Empty<string>();
    bool _disposed;

    /// <summary>Creates a validation binding and applies the current errors.</summary>
    /// <param name="source">The validation source.</param>
    /// <param name="target">The node that receives the validation attributes.</param>
    /// <param name="propertyName">The property to watch; <c>null</c> watches entity-level errors.</param>
    public ValidationBinding(INotifyDataErrorInfo source, ElementNode target, string? propertyName = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _propertyName = propertyName;
        _source.ErrorsChanged += OnErrorsChanged;
        Refresh();
    }

    /// <summary>The watched property name, or <c>null</c> for entity-level errors.</summary>
    public string? PropertyName => _propertyName;

    /// <summary>The current error messages, in source order.</summary>
    public IReadOnlyList<string> Errors => _errors;

    /// <summary>Whether the source currently reports errors.</summary>
    public bool HasErrors => _errors.Count > 0;

    /// <summary>The joined error text written to <c>data-error-text</c>, or <c>null</c> when there are no errors.</summary>
    public string? ErrorText => _errors.Count == 0 ? null : string.Join("; ", _errors);

    /// <summary>Raised after the validation attributes are refreshed.</summary>
    public event EventHandler? ErrorsChanged;

    /// <summary>Reads the current errors from the source and updates the target attributes.</summary>
    public void Refresh()
    {
        if (_disposed) return;

        var errors = new List<string>();
        try
        {
            var sourceErrors = _source.GetErrors(_propertyName);
            if (sourceErrors is not null)
            {
                foreach (object? error in sourceErrors)
                {
                    string? text = error?.ToString();
                    if (!string.IsNullOrEmpty(text)) errors.Add(text!);
                }
            }
        }
        catch (Exception)
        {
            // A faulty validation source must not break rendering; treat it as having no errors.
            errors.Clear();
        }

        _errors = new ReadOnlyCollection<string>(errors);
        Apply();
        ErrorsChanged?.Invoke(this, EventArgs.Empty);
    }

    void Apply()
    {
        var attributes = _target.Attributes;
        if (_errors.Count > 0)
        {
            attributes["data-invalid"] = "true";
            attributes["aria-invalid"] = "true";
            attributes["data-error-text"] = ErrorText;
        }
        else
        {
            attributes["data-invalid"] = null;
            attributes["aria-invalid"] = null;
            attributes["data-error-text"] = null;
        }
    }

    void OnErrorsChanged(object? sender, DataErrorsChangedEventArgs e)
    {
        if (_disposed) return;
        if (_propertyName is not null && !string.IsNullOrEmpty(e.PropertyName) &&
            !string.Equals(e.PropertyName, _propertyName, StringComparison.Ordinal))
            return;
        Refresh();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _source.ErrorsChanged -= OnErrorsChanged;
    }
}
