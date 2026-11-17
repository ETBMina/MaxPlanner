using MaxPlanner.Presentation.Common.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace MaxPlanner.Presentation.Common.Controls;

/// <summary>
/// A page header (design §2.3): the level-1 title, an optional subtitle, optional <see cref="Actions"/> beside them, and
/// the page's own rows as content (<see cref="Body"/>). The page sets the title and subtitle by the <c>x:Uid</c>s and
/// AutomationIds they always had, calls <see cref="ApplyWidth"/> from its breakpoint code (or sets
/// <see cref="HeaderPadding"/> for its own rule), and routes <c>IPageView.FocusTitle</c> to <see cref="FocusTitle"/>.
/// </summary>
[ContentProperty(Name = nameof(Body))]
public sealed partial class PageHeader : UserControl
{
    public static readonly DependencyProperty TitleUidProperty = DependencyProperty.Register(
        nameof(TitleUid), typeof(string), typeof(PageHeader), new PropertyMetadata(null, (d, _) => ((PageHeader)d).ShowTitle()));

    public static readonly DependencyProperty TitleAutomationIdProperty = DependencyProperty.Register(
        nameof(TitleAutomationId), typeof(string), typeof(PageHeader), new PropertyMetadata(null, (d, e) => AutomationProperties.SetAutomationId(((PageHeader)d).TitleText, (string?)e.NewValue ?? string.Empty)));

    public static readonly DependencyProperty SubtitleUidProperty = DependencyProperty.Register(
        nameof(SubtitleUid), typeof(string), typeof(PageHeader), new PropertyMetadata(null, (d, _) => ((PageHeader)d).ShowSubtitle()));

    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
        nameof(Subtitle), typeof(string), typeof(PageHeader), new PropertyMetadata(null, (d, _) => ((PageHeader)d).ShowSubtitle()));

    public static readonly DependencyProperty HasSubtitleProperty = DependencyProperty.Register(
        nameof(HasSubtitle), typeof(bool), typeof(PageHeader), new PropertyMetadata(false, (d, _) => ((PageHeader)d).ShowSubtitle()));

    public static readonly DependencyProperty SubtitleAutomationIdProperty = DependencyProperty.Register(
        nameof(SubtitleAutomationId), typeof(string), typeof(PageHeader), new PropertyMetadata(null, (d, e) => AutomationProperties.SetAutomationId(((PageHeader)d).SubtitleText, (string?)e.NewValue ?? string.Empty)));

    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(
        nameof(Actions), typeof(UIElement), typeof(PageHeader), new PropertyMetadata(null, (d, _) => ((PageHeader)d).ShowActions()));

    public static readonly DependencyProperty BodyProperty = DependencyProperty.Register(
        nameof(Body), typeof(UIElement), typeof(PageHeader), new PropertyMetadata(null, (d, _) => ((PageHeader)d).ShowBody()));

    public static readonly DependencyProperty HeaderPaddingProperty = DependencyProperty.Register(
        nameof(HeaderPadding), typeof(Thickness), typeof(PageHeader), new PropertyMetadata(new Thickness(32, 24, 32, 12), (d, e) => ((PageHeader)d).Root.Padding = (Thickness)e.NewValue));

    public PageHeader() => InitializeComponent();

    /// <summary>The title's <c>x:Uid</c>, for example <c>Board_PageTitle</c>; the header shows its <c>.Text</c> string.</summary>
    public string? TitleUid
    {
        get => (string?)GetValue(TitleUidProperty);
        set => SetValue(TitleUidProperty, value);
    }

    /// <summary>The title's AutomationId, for example <c>Board_PageTitle</c> (design §8.1).</summary>
    public string? TitleAutomationId
    {
        get => (string?)GetValue(TitleAutomationIdProperty);
        set => SetValue(TitleAutomationIdProperty, value);
    }

    /// <summary>A fixed subtitle's <c>x:Uid</c>; setting it shows the subtitle line.</summary>
    public string? SubtitleUid
    {
        get => (string?)GetValue(SubtitleUidProperty);
        set => SetValue(SubtitleUidProperty, value);
    }

    /// <summary>A bound subtitle's text, shown when <see cref="HasSubtitle"/> is on and no <see cref="SubtitleUid"/> is set.</summary>
    public string? Subtitle
    {
        get => (string?)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    /// <summary>
    /// Whether the header has a bound subtitle line. It keeps its line while <see cref="Subtitle"/> is empty; it is a
    /// separate switch because x:Bind passes a null text as an empty one.
    /// </summary>
    public bool HasSubtitle
    {
        get => (bool)GetValue(HasSubtitleProperty);
        set => SetValue(HasSubtitleProperty, value);
    }

    public string? SubtitleAutomationId
    {
        get => (string?)GetValue(SubtitleAutomationIdProperty);
        set => SetValue(SubtitleAutomationIdProperty, value);
    }

    /// <summary>
    /// Commands beside the title block, 12 epx after it, in a column sized to them. Their host stretches to the row's
    /// height, so commands that should line up with the title's centre set <c>VerticalAlignment="Center"</c>. In a narrow
    /// row they get at most <see cref="PageHeaderLayout.ActionsMaxWidth"/>; a <c>CommandBar</c> that should overflow
    /// sits in a star column of the actions so it receives that limit.
    /// </summary>
    public UIElement? Actions
    {
        get => (UIElement?)GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    /// <summary>The page's rows below the title block, 8 epx under it.</summary>
    public UIElement? Body
    {
        get => (UIElement?)GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }

    /// <summary>The padding around the whole header; <see cref="ApplyWidth"/> sets it by the shared rule.</summary>
    public Thickness HeaderPadding
    {
        get => (Thickness)GetValue(HeaderPaddingProperty);
        set => SetValue(HeaderPaddingProperty, value);
    }

    /// <summary>
    /// Applies the shared padding rule (<see cref="PageHeaderLayout.Padding"/>) for the page's width and its window's
    /// width (<see cref="PagePadding.WindowWidth"/>).
    /// </summary>
    public void ApplyWidth(double pageWidth, double windowWidth) => HeaderPadding = PageHeaderLayout.Padding(pageWidth, windowWidth);

    /// <summary>Focuses the title so its level-1 heading is announced (design §7.7).</summary>
    public bool FocusTitle() => TitleText.Focus(FocusState.Programmatic);

    private static string Lookup(string uid) => ResourceStrings.Shared.GetString(XamlResourceKeys.Text(uid));

    private void ShowTitle() => TitleText.Text = TitleUid is { Length: > 0 } uid ? Lookup(uid) : string.Empty;

    private void ShowSubtitle()
    {
        var shows = PageHeaderLayout.ShowsSubtitle(HasSubtitle, SubtitleUid);
        SubtitleText.Text = shows ? PageHeaderLayout.SubtitleText(SubtitleUid, Subtitle, Lookup) : string.Empty;
        SubtitleText.Visibility = shows ? Visibility.Visible : Visibility.Collapsed;
        TitleStack.Spacing = PageHeaderLayout.TitleSpacing(shows);
    }

    private void ShowActions()
    {
        var has = Actions is not null;
        ActionsHost.Child = Actions;
        ActionsHost.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        TitleRow.ColumnSpacing = PageHeaderLayout.ColumnSpacing(has);
    }

    private void OnTitleRowSizeChanged(object sender, SizeChangedEventArgs e) => ActionsHost.MaxWidth = PageHeaderLayout.ActionsMaxWidth(e.NewSize.Width);

    private void ShowBody()
    {
        var has = Body is not null;
        BodyHost.Child = Body;
        BodyHost.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        Root.Spacing = PageHeaderLayout.BodySpacing(has);
    }
}
