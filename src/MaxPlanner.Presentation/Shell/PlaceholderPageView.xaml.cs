using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace MaxPlanner.Presentation.Shell;

/// <summary>
/// Stands in for a destination that is not built in this release step (Dashboard, Calendar, Settings), so
/// the navigation structure of design §2.2 is complete and honest about what is missing.
/// </summary>
public sealed partial class PlaceholderPageView : UserControl, IPageView
{
    public PlaceholderPageView(AppPage page, string title, string glyph)
    {
        InitializeComponent();
        PageTitle.Text = title;
        PageGlyph.Glyph = glyph;
        AutomationProperties.SetAutomationId(PageTitle, page + "_PageTitle");
    }

    /// <inheritdoc/>
    public FrameworkElement HeaderElement => PageTitle;

    /// <inheritdoc/>
    public FrameworkElement? ContentElement => null;

    public void FocusTitle() => PageTitle.Focus(FocusState.Programmatic);

    /// <inheritdoc/>
    public bool FocusSearch() => false;
}
