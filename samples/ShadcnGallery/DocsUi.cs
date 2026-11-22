using System;
using System.Collections.Generic;
using AwAUI.Shadcn;
using AwAUI.UI;
using Text = AwAUI.UI.Text;
using ShadcnButton = AwAUI.Shadcn.Button;

namespace ShadcnGallery;

/// <summary>
/// Shared building blocks for the live examples. Pages only use these helpers plus classes documented in the
/// gallery stylesheet, so every page stays consistent with the docs layout.
/// </summary>
public static class DocsUi
{
    public static Div Stack(params Node[] children) =>
        new() { Class = "demo-stack", Children = new NodeList(children) };

    public static Div Row(params Node[] children) =>
        new() { Class = "demo-row", Children = new NodeList(children) };

    /// <summary>Keeps an intrinsically-sized component (menubar, menu trigger) from stretching inside a stack.</summary>
    public static Div Inline(Node child) => Row(child);

    public static Div Column(params Node[] children) =>
        new() { Class = "demo-column", Children = new NodeList(children) };

    public static Div Grid(params Node[] children) =>
        new() { Class = "demo-grid", Children = new NodeList(children) };

    public static Div Grid3(params Node[] children) =>
        new() { Class = "demo-grid demo-grid-3", Children = new NodeList(children) };

    public static Div Panel(params Node[] children) =>
        new() { Class = "demo-panel", Children = new NodeList(children) };

    public static Div Field(params Node[] children) =>
        new() { Class = "demo-field", Children = new NodeList(children) };

    public static Div Split(params Node[] children) =>
        new() { Class = "demo-split", Children = new NodeList(children) };

    public static Div Scroll(params Node[] children) =>
        new() { Class = "demo-scroll", Children = new NodeList(children) };

    public static Div Center(params Node[] children) =>
        new() { Class = "demo-center", Children = new NodeList(children) };

    public static Div Wide(Node child) =>
        new() { Class = "demo-wide", Children = new NodeList([child]) };

    public static Text Muted(string text) => new() { Class = "demo-muted", Content = text };

    public static Text Note(string text) => new() { Class = "demo-note", Content = text };

    public static Text Label(string text) => new() { Class = "demo-label", Content = text };

    public static Text Heading(string text, int level = 2)
    {
        var node = new Text { Class = level <= 2 ? "demo-heading" : "demo-subheading", Content = text, Role = Role.Heading };
        node.Attributes["aria-level"] = level.ToString();
        return node;
    }

    public static ShadcnButton Action(string label,
        Action<PointerEvent>? onClick = null,
        ButtonVariant variant = ButtonVariant.Outline,
        ButtonSize size = ButtonSize.Default) =>
        new() { Variant = variant, Size = size, OnClick = onClick, Children = new NodeList([new TextNode(label)]) };

    public static ShadcnButton Ghost(string label, Action<PointerEvent>? onClick = null) =>
        Action(label, onClick, ButtonVariant.Ghost, ButtonSize.Sm);

    public static Div Divider() => new() { Class = "demo-divider" };

    /// <summary>A lightweight monospace code sample used by the Getting Started pages.</summary>
    public static Div Code(string source) =>
        new() { Class = "demo-code", Children = new NodeList([new TextNode(source)]) };

    /// <summary>A colored token swatch with its CSS variable name.</summary>
    public static Div Swatch(string name, string value) => new()
    {
        Class = "demo-swatch",
        Children = new NodeList(
        [
            new Div { Class = "demo-swatch-color", Style = "background:" + value },
            Column(Label(name), Muted(value)),
        ]),
    };


    public static Div Space(float height) => new() { Class = "demo-space", Style = $"height:{height.ToString(System.Globalization.CultureInfo.InvariantCulture)}px" };

    /// <summary>Deterministic pseudo-random data used by the chart and table examples.</summary>
    public static double[] Series(int count, double min, double max, int seed)
    {
        var values = new double[count];
        uint state = (uint)seed;
        for (int i = 0; i < count; i++)
        {
            state = state * 1664525u + 1013904223u;
            double t = (state >> 8) / (double)(1 << 24);
            values[i] = min + (max - min) * t;
        }
        return values;
    }
}
