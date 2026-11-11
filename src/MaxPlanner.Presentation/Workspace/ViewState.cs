
namespace MaxPlanner.Presentation.Workspace;

/// <summary>The state a page's <c>StateView</c> shows (design §2.5).</summary>
public enum ViewState
{
    Loading,
    Empty,
    FilteredEmpty,
    Error,
    Populated,
}
