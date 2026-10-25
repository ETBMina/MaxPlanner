using Microsoft.UI.Xaml;

namespace MaxPlanner.Presentation.Shell;

/// <summary>What the shell needs from a page view for focus routes (design §7.1, §7.7). View-side only.</summary>
/// <remarks>
/// A visited or preloaded page view stays in the shell's tree for the session and is collapsed while another page is shown, so
/// Loaded and Unloaded run once, not at every page switch. Work tied to the page appearing or disappearing belongs in
/// <see cref="OnShown"/> and <see cref="OnHidden"/>; Unloaded still runs when the dataset closes and the views are dropped.
/// </remarks>
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

    /// <summary>
    /// Called after the page became the visible one, before the shell moves focus to it: restores transient UI that
    /// <see cref="OnHidden"/> closed. Nothing may be rebuilt wholesale here; the page kept its tree while hidden.
    /// </summary>
    void OnShown()
    {
    }

    /// <summary>
    /// Called after the page was collapsed because another page is shown: stops timers and closes popups the page
    /// owns. Drafts and edit state are kept (the shell also closes every open light-dismiss popup after this). A page
    /// the shell preloads gets this call once before it enters the tree, so it is never treated as the visible page
    /// until <see cref="OnShown"/>.
    /// </summary>
    void OnHidden()
    {
    }
}
