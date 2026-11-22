using System;
using System.Collections.Generic;
using System.Linq;
using AwAUI.UI;

namespace ShadcnGallery;

/// <summary>Which sidebar section a documentation page belongs to.</summary>
public enum DocsSection { GettingStarted, Components }

/// <summary>A single live example on a documentation page. <see cref="Code"/> is optional; the shell shows a
/// "Code" toggle only when a snippet is supplied.</summary>
public sealed record DocsExample(string Title, Func<Node> Build, string? Code = null)
{
    public static DocsExample Of(string title, Node node) => new(title, () => node);

    public static DocsExample Of(string title, Func<Node> build) => new(title, build);

    public static DocsExample Of(string title, string code, Func<Node> build) => new(title, build, code);
}

/// <summary>One page of the docs gallery: title, official description, and live examples.</summary>
public sealed record DocsPage(string Id, string Title, string Description, DocsSection Section, IReadOnlyList<DocsExample> Examples);

/// <summary>
/// The page registry behind the docs-style gallery. Page files call <see cref="Add"/> (components) or
/// <see cref="AddGuide"/> (Getting Started) from their own <c>Register</c> method; the static constructor below
/// runs each category in order.
/// </summary>
public static class DocsCatalog
{
    static readonly List<DocsPage> s_pages = new();

    static DocsCatalog()
    {
        GetStartedPages.Register();
        BasicPages.Register();
        FormsPages.Register();
        LayoutPages.Register();
        NavigationPages.Register();
        OverlayPages.Register();
        FeedbackPages.Register();
        DataPages.Register();
        AiPages.Register();
    }

    /// <summary>All pages in registration order.</summary>
    public static IReadOnlyList<DocsPage> Pages => s_pages;

    /// <summary>Component pages sorted the way the official sidebar lists them.</summary>
    public static IReadOnlyList<DocsPage> Components =>
        s_pages.Where(p => p.Section == DocsSection.Components)
            .OrderBy(p => p.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static IReadOnlyList<DocsPage> GettingStarted =>
        s_pages.Where(p => p.Section == DocsSection.GettingStarted).ToList();

    public static DocsPage Find(string id) =>
        s_pages.FirstOrDefault(p => p.Id == id) ?? s_pages[0];

    /// <summary>Index of <paramref name="page"/> within its sidebar section, -1 when absent.</summary>
    public static int IndexOf(DocsPage page)
    {
        var section = page.Section == DocsSection.Components ? Components : GettingStarted;
        for (int i = 0; i < section.Count; i++)
            if (ReferenceEquals(section[i], page)) return i;
        return -1;
    }

    /// <summary>Previous / next page within the page's sidebar section.</summary>
    public static (DocsPage? Previous, DocsPage? Next) Neighbors(DocsPage page)
    {
        var section = page.Section == DocsSection.Components ? Components : GettingStarted;
        int index = IndexOf(page);
        return index < 0
            ? (null, null)
            : (index > 0 ? section[index - 1] : null, index + 1 < section.Count ? section[index + 1] : null);
    }

    /// <summary>Registers a component page; its description comes from <see cref="DocsDescriptions"/>.</summary>
    public static void Add(string id, string title, params DocsExample[] examples) =>
        Add(new DocsPage(id, title, DocsDescriptions.For(id), DocsSection.Components, examples));

    public static void Add(string id, string title, string description, params DocsExample[] examples) =>
        Add(new DocsPage(id, title, description, DocsSection.Components, examples));

    /// <summary>Registers a Getting Started page (kept in the official intro order).</summary>
    public static void AddGuide(string id, string title, string description, params DocsExample[] examples) =>
        Add(new DocsPage(id, title, description, DocsSection.GettingStarted, examples));

    static void Add(DocsPage page)
    {
        if (s_pages.Any(p => p.Id == page.Id))
            throw new InvalidOperationException($"Duplicate documentation page id \"{page.Id}\".");
        s_pages.Add(page);
    }
}
