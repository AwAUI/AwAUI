using System;
using System.Globalization;
using AwAUI.UI;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Controls.DataGrid;

public sealed partial class DataGrid
{
    bool _isEditing;
    int _editRow = -1;
    int _editColumn = -1;
    string? _editDraft;
    bool _editBoolDraft;
    bool _editInvalid;
    bool _focusEditor;
    Ref<Element>? _editRef;

    /// <summary>Whether a cell is currently in edit mode.</summary>
    public bool IsEditing => _isEditing;

    /// <summary>The row index of the cell in edit mode, or -1.</summary>
    public int EditRowIndex => _editRow;

    /// <summary>The column index of the cell in edit mode, or -1.</summary>
    public int EditColumnIndex => _editColumn;

    /// <summary>The editor text currently held for the cell in edit mode (null when not editing).</summary>
    public string? EditText => _editDraft;

    /// <summary>Whether the last commit failed; the cell keeps the editor open and is marked <c>data-invalid</c>.</summary>
    public bool IsEditInvalid => _editInvalid;

    internal bool EditBoolDraft => _editBoolDraft;

    /// <summary>Puts the current cell into edit mode.</summary>
    public bool BeginEdit() => BeginEdit(_currentRow, _currentColumn);

    /// <summary>Puts the cell at <paramref name="rowIndex"/> and <paramref name="columnIndex"/> into edit mode.</summary>
    public bool BeginEdit(int rowIndex, int columnIndex)
    {
        if (!IsEnabled || IsReadOnly) return false;
        if (rowIndex < 0 || rowIndex >= RowCount || columnIndex < 0 || columnIndex >= _columns.Count) return false;
        var column = _columns[columnIndex];
        if (!column.CanEdit || column.IsReadOnly) return false;
        if (_isEditing)
        {
            if (rowIndex == _editRow && columnIndex == _editColumn) return true;
            if (!CommitEdit()) return false;
        }

        var item = _displayItems[rowIndex];
        _editRow = rowIndex;
        _editColumn = columnIndex;
        _currentRow = rowIndex;
        _currentColumn = columnIndex;
        _editInvalid = false;
        _isEditing = true;
        _focusEditor = true;
        if (column is DataGridCheckBoxColumn)
        {
            _editBoolDraft = ((DataGridCheckBoxColumn)column).IsChecked(item);
            _editDraft = _editBoolDraft ? "true" : "false";
        }
        else if (column is DataGridTextColumn text)
        {
            _editDraft = text.Format(item);
        }
        else
        {
            _editDraft = Convert.ToString(column.GetValue(item), CultureInfo.CurrentCulture) ?? "";
        }
        Invalidate();
        return true;
    }

    /// <summary>Puts a cell of the given column into edit mode.</summary>
    public bool BeginEdit(int rowIndex, DataGridColumn column)
    {
        if (column is null) return false;
        for (int i = 0; i < _columns.Count; i++)
            if (ReferenceEquals(_columns[i], column)) return BeginEdit(rowIndex, i);
        return false;
    }

    /// <summary>
    /// Writes the editor value back through the row's property setter. On failure the editor stays open, the cell is
    /// marked <c>data-invalid</c>, and the method returns false.
    /// </summary>
    public bool CommitEdit()
    {
        if (!_isEditing) return false;
        if (_editRow < 0 || _editRow >= _displayItems.Count || _editColumn < 0 || _editColumn >= _columns.Count)
        {
            FinishEdit();
            return false;
        }

        var column = _columns[_editColumn];
        var item = _displayItems[_editRow];
        object? value;
        string? error;
        bool parsed;
        if (column is DataGridCheckBoxColumn)
        {
            parsed = DataGridBinding.TryConvert(_editBoolDraft ? "true" : "false",
                DataGridBinding.GetValueType(item, column.Binding) ?? typeof(bool), out value, out error);
        }
        else
        {
            parsed = column.TryParseDraft(item, _editDraft, out value, out error);
        }
        if (!parsed || !column.TrySetValue(item, value, out error))
        {
            _editInvalid = true;
            Invalidate();
            return false;
        }

        FinishEdit();
        return true;
    }

    /// <summary>Discards the editor value and leaves edit mode.</summary>
    public void CancelEdit()
    {
        if (!_isEditing) return;
        FinishEdit();
    }

    /// <summary>Replaces the editor text; useful when hosting the grid without a text input (tests, programmatic edits).</summary>
    public void SetEditText(string? text)
    {
        if (!_isEditing) return;
        _editDraft = text;
        _editInvalid = false;
        Invalidate();
    }

    void FinishEdit()
    {
        _isEditing = false;
        _editInvalid = false;
        _editRow = -1;
        _editColumn = -1;
        _editDraft = null;
        _editBoolDraft = false;
        _focusEditor = false;
        Invalidate();
    }

    internal Node BuildTextEditor()
    {
        var input = new Input
        {
            Value = _editDraft ?? "",
            Ref = _editRef ??= new Ref<Element>(),
            Class = "aw-datagrid-editor",
        };
        input.Role = Role.TextBox;
        input.OnInput = e =>
        {
            _editDraft = e.Value;
            if (_editInvalid) _editInvalid = false;
        };
        input.OnSubmit = e =>
        {
            e.PreventDefault();
            if (CommitEdit()) MoveCurrent(1, 0, extend: false, keepSelection: false);
        };
        QueueEditorFocus();
        return input;
    }

    internal Node BuildCheckBox(DataGridCell cell, bool isChecked, bool editable)
    {
        var box = new Div
        {
            Role = Role.CheckBox,
            Class = "aw-datagrid-checkbox",
            Children = { new UIText(isChecked ? "✓" : "") },
        };
        box.Attributes["aria-checked"] = isChecked ? "true" : "false";
        box.Attributes["data-checked"] = isChecked ? "true" : "false";
        if (editable)
        {
            box.OnClick = e =>
            {
                e.PreventDefault();
                _editBoolDraft = !_editBoolDraft;
                _editDraft = _editBoolDraft ? "true" : "false";
                Invalidate();
            };
        }
        return box;
    }

    /// <summary>Toggles a check-box cell and commits immediately; a failed write-back leaves edit mode invalid.</summary>
    void ToggleCheckBoxCell(int rowIndex, int columnIndex)
    {
        if (!CanEditCell(rowIndex, columnIndex)) return;
        if (!_isEditing || rowIndex != _editRow || columnIndex != _editColumn)
        {
            if (!BeginEdit(rowIndex, columnIndex)) return;
        }
        var column = _columns[columnIndex];
        if (column is not DataGridCheckBoxColumn checkBox) return;
        _editBoolDraft = !checkBox.IsChecked(_displayItems[rowIndex]);
        _editDraft = _editBoolDraft ? "true" : "false";
        CommitEdit();
    }

    bool CanEditCell(int rowIndex, int columnIndex)
    {
        if (!IsEnabled || IsReadOnly) return false;
        if (rowIndex < 0 || rowIndex >= RowCount || columnIndex < 0 || columnIndex >= _columns.Count) return false;
        var column = _columns[columnIndex];
        return column.CanEdit && !column.IsReadOnly;
    }

    void QueueEditorFocus()
    {
        if (!_focusEditor) return;
        _focusEditor = false;
        var root = Root;
        if (root is null) return;
        root.RequestAfterLayout(() =>
        {
            if (!_isEditing || _editRow < 0) return;
            if (_editRef?.Current is { IsFocusable: true, IsAttached: true } element)
                element.Focus(new FocusOptions { Origin = FocusOrigin.Keyboard, SelectText = true });
        });
    }
}
