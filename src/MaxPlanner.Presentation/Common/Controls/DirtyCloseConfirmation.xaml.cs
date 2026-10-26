using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace MaxPlanner.Presentation.Common.Controls;

public enum DirtyCloseChoice
{
    Save,
    Discard,
    ContinueEditing,
}

/// <summary>
/// The dirty-close confirmation (design §5.8): Save (default, focused), Discard, Continue editing. Esc and a press on
/// its smoke mean Continue editing, and focus stays inside it until it closes. View-side only; use on the UI thread.
/// </summary>
public sealed partial class DirtyCloseConfirmation : UserControl
{
    private const double WindowMargin = 16;

    private readonly TaskCompletionSource<DirtyCloseChoice> _choice = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private DirtyCloseConfirmation()
    {
        InitializeComponent();
        AutomationProperties.SetName(this, TitleText.Text);
        AutomationProperties.SetHelpText(this, MessageText.Text);
        LosingFocus += OnLosingFocus;
    }

    /// <summary>
    /// Shows the confirmation above everything in <paramref name="root"/>, including an open ContentDialog, and
    /// completes with the user's choice. Cancelling <paramref name="cancellationToken"/> closes it with
    /// <see cref="DirtyCloseChoice.ContinueEditing"/>, for example when the editor beneath closed by itself.
    /// </summary>
    public static async Task<DirtyCloseChoice> ShowAsync(XamlRoot root, ElementTheme theme, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        var view = new DirtyCloseConfirmation { RequestedTheme = theme };

        // A popup opened after the ContentDialog's own popup is drawn above it (one ContentDialog per XamlRoot).
        var popup = new Popup
        {
            XamlRoot = root,
            Child = view,
            IsLightDismissEnabled = false,
            ShouldConstrainToRootBounds = true,
        };
        void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => view.Fit(sender.Size);
        view.Fit(root.Size);
        root.Changed += OnRootChanged;
        view.Loaded += (_, _) => view.SaveButton.Focus(FocusState.Programmatic);
        using var registration = cancellationToken.Register(() => view.Complete(DirtyCloseChoice.ContinueEditing));
        popup.IsOpen = true;
        try
        {
            return await view._choice.Task.ConfigureAwait(true);
        }
        finally
        {
            root.Changed -= OnRootChanged;
            view.LosingFocus -= view.OnLosingFocus;
            popup.IsOpen = false;
            popup.Child = null;
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new DirtyCloseConfirmationAutomationPeer(this);

    private void Fit(Size size)
    {
        Width = size.Width;
        Height = size.Height;
        var maxWidth = Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue("ContentDialogMaxWidth", out var value) && value is double max ? max : 548;
        Card.Width = Math.Max(Card.MinWidth, Math.Min(maxWidth, size.Width - (2 * WindowMargin)));
    }

    private void Complete(DirtyCloseChoice choice) => _choice.TrySetResult(choice);

    private void OnSaveClick(object sender, RoutedEventArgs e) => Complete(DirtyCloseChoice.Save);

    private void OnDiscardClick(object sender, RoutedEventArgs e) => Complete(DirtyCloseChoice.Discard);

    private void OnContinueClick(object sender, RoutedEventArgs e) => Complete(DirtyCloseChoice.ContinueEditing);

    // Enter on another button invokes that button (it handles the key); otherwise Enter means the default, Save.
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape:
                e.Handled = true;
                Complete(DirtyCloseChoice.ContinueEditing);
                break;
            case VirtualKey.Enter when !e.Handled:
                e.Handled = true;
                Complete(DirtyCloseChoice.Save);
                break;
            default:
                break;
        }
    }

    private void OnSmokePressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (point.PointerDeviceType != PointerDeviceType.Mouse || point.Properties.IsLeftButtonPressed)
        {
            e.Handled = true;
            Complete(DirtyCloseChoice.ContinueEditing);
        }
    }

    // The editor beneath stays inert while this is open: focus never leaves the confirmation (design §7).
    private void OnLosingFocus(UIElement sender, LosingFocusEventArgs args)
    {
        if (!_choice.Task.IsCompleted && args.NewFocusedElement is not null && !IsWithin(args.NewFocusedElement))
        {
            args.TryCancel();
        }
    }

    private bool IsWithin(DependencyObject? element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, this))
            {
                return true;
            }
        }

        return false;
    }

    // Reports a dialog window to UI Automation, so Narrator announces it like a ContentDialog.
    private sealed partial class DirtyCloseConfirmationAutomationPeer(DirtyCloseConfirmation owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Window;

        protected override string GetClassNameCore() => nameof(DirtyCloseConfirmation);

        protected override bool IsDialogCore() => true;
    }
}
