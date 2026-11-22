using System;
using System.Collections.Generic;

namespace AwAUI.UI.Accessibility;

/// <summary>
/// Kinds of semantic-tree events (design/10). Events are produced while the semantic tree is rebuilt
/// from the differences between the previous and current snapshots; they are independent of the
/// platform bridge, which still consumes <see cref="AccessibilityTree.CreateSnapshot"/>.
/// </summary>
public enum AccessibilityEventKind
{
    /// <summary>Focus moved from one node to another.</summary>
    FocusChanged,

    /// <summary>A node's value or text selection changed.</summary>
    ValueChanged,

    /// <summary>A node's accessible name changed.</summary>
    NameChanged,

    /// <summary>A node was added or removed, or its role/parent relationship changed.</summary>
    StructureChanged,

    /// <summary>The readable text of a live region changed and should be announced.</summary>
    Announcement,
}

/// <summary>
/// A structured semantic-tree event. Fields are used according to <see cref="Kind"/>; unused fields
/// keep their default values.
/// </summary>
public sealed class AccessibilityEvent
{
    internal AccessibilityEvent(AccessibilityEventKind kind, long version, string? nodeId)
    {
        Kind = kind;
        Version = version;
        NodeId = nodeId;
    }

    /// <summary>The event kind.</summary>
    public AccessibilityEventKind Kind { get; }

    /// <summary>Semantic-tree version at which the event was produced.</summary>
    public long Version { get; }

    /// <summary>The node the event refers to; null when focus was lost and no new node gained it.</summary>
    public string? NodeId { get; internal set; }

    /// <summary>The previously focused node id (only for <see cref="AccessibilityEventKind.FocusChanged"/>).</summary>
    public string? PreviousNodeId { get; internal set; }

    /// <summary>The current name (only for <see cref="AccessibilityEventKind.NameChanged"/>).</summary>
    public string? Name { get; internal set; }

    /// <summary>The previous name (only for <see cref="AccessibilityEventKind.NameChanged"/>).</summary>
    public string? PreviousName { get; internal set; }

    /// <summary>The current value (used by <see cref="AccessibilityEventKind.ValueChanged"/> and announcements).</summary>
    public string? Value { get; internal set; }

    /// <summary>The previous value (only for <see cref="AccessibilityEventKind.ValueChanged"/>).</summary>
    public string? PreviousValue { get; internal set; }

    /// <summary>Announcement politeness (only for <see cref="AccessibilityEventKind.Announcement"/>).</summary>
    public AccessibilityLiveRegion Politeness { get; internal set; }

    /// <summary>The text to announce (only for <see cref="AccessibilityEventKind.Announcement"/>).</summary>
    public string? Text { get; internal set; }

    /// <summary>Ids of newly added nodes (only for <see cref="AccessibilityEventKind.StructureChanged"/>; may be empty).</summary>
    public IReadOnlyList<string> AddedNodeIds { get; internal set; } = Array.Empty<string>();

    /// <summary>Ids of removed nodes (only for <see cref="AccessibilityEventKind.StructureChanged"/>; may be empty).</summary>
    public IReadOnlyList<string> RemovedNodeIds { get; internal set; } = Array.Empty<string>();

    public override string ToString()
    {
        switch (Kind)
        {
            case AccessibilityEventKind.FocusChanged:
                return "FocusChanged " + (PreviousNodeId ?? "-") + " -> " + (NodeId ?? "-");
            case AccessibilityEventKind.NameChanged:
                return "NameChanged " + NodeId + " \"" + PreviousName + "\" -> \"" + Name + "\"";
            case AccessibilityEventKind.ValueChanged:
                return "ValueChanged " + NodeId + " \"" + PreviousValue + "\" -> \"" + Value + "\"";
            case AccessibilityEventKind.StructureChanged:
                return "StructureChanged +" + AddedNodeIds.Count + " -" + RemovedNodeIds.Count;
            default:
                return "Announcement " + Politeness + " \"" + Text + "\"";
        }
    }
}
