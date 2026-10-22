using System.Windows.Input;
using MaxPlanner.Presentation.Workspace;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace MaxPlanner.Presentation.Common.Controls;

/// <summary>
/// The shared data-region states of design §2.5: Loading (after a 300 ms delay), Empty, Filtered-empty,
/// Error, and Populated (the page's <see cref="Body"/>). The page header and commands stay outside it, so
/// they remain usable in every state.
/// </summary>
/// <remarks>
/// AutomationIds of the inner parts derive from <see cref="PageKey"/>: <c>{Page}_StateView</c>,
/// <c>{Page}_StateViewActionButton</c> (the primary create action or Retry), following design §8.1.
/// </remarks>
[ContentProperty(Name = nameof(Body))]
public sealed partial class StateView : UserControl
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(ViewState), typeof(StateView), new PropertyMetadata(ViewState.Loading, (d, _) => ((StateView)d).Apply()));

    public static readonly DependencyProperty BodyProperty = DependencyProperty.Register(
        nameof(Body), typeof(object), typeof(StateView), new PropertyMetadata(null));

    public static readonly DependencyProperty PageKeyProperty = DependencyProperty.Register(
        nameof(PageKey), typeof(string), typeof(StateView), new PropertyMetadata(string.Empty, (d, _) => ((StateView)d).ApplyAutomationIds()));

    public static readonly DependencyProperty LoadingMessageProperty = Text(nameof(LoadingMessage));
    public static readonly DependencyProperty EmptyTitleProperty = Text(nameof(EmptyTitle));
    public static readonly DependencyProperty EmptyMessageProperty = Text(nameof(EmptyMessage));
    public static readonly DependencyProperty EmptyGlyphProperty = Text(nameof(EmptyGlyph));
    public static readonly DependencyProperty EmptyActionTextProperty = Text(nameof(EmptyActionText));
    public static readonly DependencyProperty FilteredTitleProperty = Text(nameof(FilteredTitle));
    public static readonly DependencyProperty FilteredMessageProperty = Text(nameof(FilteredMessage));
    public static readonly DependencyProperty ErrorTitleProperty = Text(nameof(ErrorTitle));
    public static readonly DependencyProperty ErrorMessageProperty = Text(nameof(ErrorMessage));
    public static readonly DependencyProperty ErrorDetailsTextProperty = Text(nameof(ErrorDetailsText));
    public static readonly DependencyProperty RetryTextProperty = Text(nameof(RetryText));

    public static readonly DependencyProperty EmptyActionCommandProperty = DependencyProperty.Register(
        nameof(EmptyActionCommand), typeof(ICommand), typeof(StateView), new PropertyMetadata(null, (d, _) => ((StateView)d).Apply()));

    public static readonly DependencyProperty RetryCommandProperty = DependencyProperty.Register(
        nameof(RetryCommand), typeof(ICommand), typeof(StateView), new PropertyMetadata(null, (d, _) => ((StateView)d).Apply()));

    // Design §2.5: a quick load shows nothing instead of flashing a progress ring.
    private static readonly TimeSpan LoadingDelay = TimeSpan.FromMilliseconds(300);

    private readonly DispatcherTimer _loadingTimer = new() { Interval = LoadingDelay };
    private ViewState? _announced;

    public StateView()
    {
        InitializeComponent();
        _loadingTimer.Tick += OnLoadingDelayElapsed;
        Unloaded += (_, _) => _loadingTimer.Stop();
        Loaded += (_, _) => Apply();
    }

    public ViewState State
    {
        get => (ViewState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public object? Body
    {
        get => GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }

    /// <summary>The page prefix of the inner AutomationIds, e.g. <c>Grid</c>.</summary>
    public string PageKey
    {
        get => (string)GetValue(PageKeyProperty);
        set => SetValue(PageKeyProperty, value);
    }

    public string LoadingMessage { get => (string)GetValue(LoadingMessageProperty); set => SetValue(LoadingMessageProperty, value); }

    public string EmptyTitle { get => (string)GetValue(EmptyTitleProperty); set => SetValue(EmptyTitleProperty, value); }

    public string EmptyMessage { get => (string)GetValue(EmptyMessageProperty); set => SetValue(EmptyMessageProperty, value); }

    /// <summary>A Segoe Fluent Icons glyph for the Empty state.</summary>
    public string EmptyGlyph { get => (string)GetValue(EmptyGlyphProperty); set => SetValue(EmptyGlyphProperty, value); }

    public string EmptyActionText { get => (string)GetValue(EmptyActionTextProperty); set => SetValue(EmptyActionTextProperty, value); }

    public ICommand? EmptyActionCommand { get => (ICommand?)GetValue(EmptyActionCommandProperty); set => SetValue(EmptyActionCommandProperty, value); }

    public string FilteredTitle { get => (string)GetValue(FilteredTitleProperty); set => SetValue(FilteredTitleProperty, value); }

    public string FilteredMessage { get => (string)GetValue(FilteredMessageProperty); set => SetValue(FilteredMessageProperty, value); }

    public string ErrorTitle { get => (string)GetValue(ErrorTitleProperty); set => SetValue(ErrorTitleProperty, value); }

    public string ErrorMessage { get => (string)GetValue(ErrorMessageProperty); set => SetValue(ErrorMessageProperty, value); }

    /// <summary>The operation ID or other technical detail behind the "Details" expander.</summary>
    public string ErrorDetailsText { get => (string)GetValue(ErrorDetailsTextProperty); set => SetValue(ErrorDetailsTextProperty, value); }

    public string RetryText { get => (string)GetValue(RetryTextProperty); set => SetValue(RetryTextProperty, value); }

    public ICommand? RetryCommand { get => (ICommand?)GetValue(RetryCommandProperty); set => SetValue(RetryCommandProperty, value); }

    private static DependencyProperty Text(string name) =>
        DependencyProperty.Register(name, typeof(string), typeof(StateView), new PropertyMetadata(string.Empty, (d, _) => ((StateView)d).Apply()));

    private void Apply()
    {
        var state = State;
        BodyPresenter.Visibility = state == ViewState.Populated ? Visibility.Visible : Visibility.Collapsed;
        if (state == ViewState.Loading)
        {
            if (LoadingPanel.Visibility == Visibility.Collapsed && !_loadingTimer.IsEnabled)
            {
                _loadingTimer.Start();
            }
        }
        else
        {
            _loadingTimer.Stop();
            LoadingRing.IsActive = false;
            LoadingPanel.Visibility = Visibility.Collapsed;
        }

        var (glyph, title, message, actionText, action) = state switch
        {
            ViewState.Empty => (EmptyGlyph, EmptyTitle, EmptyMessage, EmptyActionText, EmptyActionCommand),
            ViewState.FilteredEmpty => ("", FilteredTitle, FilteredMessage, string.Empty, (ICommand?)null),
            ViewState.Error => ("", ErrorTitle, ErrorMessage, RetryText, RetryCommand),
            _ => (string.Empty, string.Empty, string.Empty, string.Empty, (ICommand?)null),
        };

        var showsMessage = state is ViewState.Empty or ViewState.FilteredEmpty or ViewState.Error;
        MessagePanel.Visibility = showsMessage ? Visibility.Visible : Visibility.Collapsed;
        MessageGlyph.Glyph = glyph;
        MessageGlyph.Visibility = state == ViewState.Error ? Visibility.Collapsed : Visibility.Visible;
        ErrorGlyph.Visibility = state == ViewState.Error ? Visibility.Visible : Visibility.Collapsed;
        MessageTitle.Text = title;
        MessageText.Text = message;
        MessageText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        ErrorDetails.Visibility = state == ViewState.Error && !string.IsNullOrEmpty(ErrorDetailsText) ? Visibility.Visible : Visibility.Collapsed;
        ActionButton.Content = actionText;
        ActionButton.Visibility = action is not null && !string.IsNullOrEmpty(actionText) ? Visibility.Visible : Visibility.Collapsed;
        Announce(state, title);
    }

    // Design §2.5 announcements: Filtered-empty polite, Error assertive; Loading uses the text's live setting.
    private void Announce(ViewState state, string title)
    {
        if (_announced == state || !IsLoaded)
        {
            return;
        }

        _announced = state;
        if (state is ViewState.FilteredEmpty or ViewState.Error && !string.IsNullOrEmpty(title))
        {
            var peer = FrameworkElementAutomationPeer.FromElement(MessageTitle) ?? FrameworkElementAutomationPeer.CreatePeerForElement(MessageTitle);
            peer?.RaiseNotificationEvent(
                AutomationNotificationKind.Other,
                state == ViewState.Error ? AutomationNotificationProcessing.ImportantMostRecent : AutomationNotificationProcessing.MostRecent,
                title,
                "StateView");
        }
    }

    private void ApplyAutomationIds()
    {
        var key = PageKey;
        AutomationProperties.SetAutomationId(this, key + "_StateView");
        AutomationProperties.SetAutomationId(ActionButton, key + "_StateViewActionButton");
        AutomationProperties.SetAutomationId(MessageTitle, key + "_StateViewTitle");
        AutomationProperties.SetAutomationId(ErrorDetails, key + "_StateViewDetails");
    }

    private void OnLoadingDelayElapsed(object? sender, object e)
    {
        _loadingTimer.Stop();
        if (State == ViewState.Loading)
        {
            LoadingPanel.Visibility = Visibility.Visible;
            LoadingRing.IsActive = true;
        }
    }

    private void OnActionClick(object sender, RoutedEventArgs e)
    {
        var command = State == ViewState.Error ? RetryCommand : EmptyActionCommand;
        if (command?.CanExecute(null) == true)
        {
            command.Execute(null);
        }
    }
}
