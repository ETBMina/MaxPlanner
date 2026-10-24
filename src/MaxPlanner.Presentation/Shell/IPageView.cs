using Microsoft.UI.Xaml;

namespace MaxPlanner.Presentation.Shell;

/// <summary>What the shell needs from a page view for focus routes (design §7.1, §7.7). View-side only.</summary>
public interface IPageView
{
    /// <summary>The page header: title, commands, search, and filter context (design §2.3).</summary>
    FrameworkElement HeaderElement { get; }

    /// <summary>The page's data region (F6 moves focus to its first focusable element), or null when it has none.</summary>
    FrameworkElement? ContentElement { get; }

    /// <summary>Focuses the page title so its heading is announced after navigation.</summary>
    void FocusTitle();

    /// <summary>Ctrl+F: focuses the page search box. Returns false when the page has no search.</summary>
    bool FocusSearch();
}
