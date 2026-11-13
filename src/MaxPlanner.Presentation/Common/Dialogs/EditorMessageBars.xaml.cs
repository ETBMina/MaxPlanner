using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace MaxPlanner.Presentation.Common.Dialogs;

/// <summary>
/// The validation summary and save-error InfoBars of a draft editor (design §5.0, §5.10), bound to
/// <see cref="EditorViewModelBase{TFollowUp}.ValidationSummary"/> and <see cref="EditorViewModelBase{TFollowUp}.SaveError"/>.
/// The bars are <c>{AutomationPrefix}_ValidationInfoBar</c> and <c>{AutomationPrefix}_ErrorInfoBar</c>.
/// </summary>
/// <remarks>
/// During a conflict an editor with conflict steps (<see cref="ReviewConflictCommand"/> set) shows Review changes and
/// Reload under the message; an editor without them shows only Reload, as the bar's action button.
/// </remarks>
public sealed partial class EditorMessageBars : UserControl
{
    public static readonly DependencyProperty AutomationPrefixProperty = Register(nameof(AutomationPrefix), typeof(string), null);

    public static readonly DependencyProperty ValidationSummaryProperty = Register(nameof(ValidationSummary), typeof(string), null);

    public static readonly DependencyProperty SaveErrorProperty = Register(nameof(SaveError), typeof(string), null);

    public static readonly DependencyProperty IsConflictProperty = Register(nameof(IsConflict), typeof(bool), false);

    public static readonly DependencyProperty ReviewConflictCommandProperty = Register(nameof(ReviewConflictCommand), typeof(ICommand), null);

    public static readonly DependencyProperty ReloadCommandProperty = Register(nameof(ReloadCommand), typeof(ICommand), null);

    public EditorMessageBars()
    {
        InitializeComponent();
        Apply();
    }

    /// <summary>The editor's AutomationId prefix, for example <c>TaskEditor</c> (design §8.1).</summary>
    public string? AutomationPrefix
    {
        get => (string?)GetValue(AutomationPrefixProperty);
        set => SetValue(AutomationPrefixProperty, value);
    }

    /// <summary>"Fix 2 fields to save"; the validation bar is open while it is not null.</summary>
    public string? ValidationSummary
    {
        get => (string?)GetValue(ValidationSummaryProperty);
        set => SetValue(ValidationSummaryProperty, value);
    }

    /// <summary>The save failure; the error bar is open while it is not null.</summary>
    public string? SaveError
    {
        get => (string?)GetValue(SaveErrorProperty);
        set => SetValue(SaveErrorProperty, value);
    }

    /// <summary>Shows the conflict actions in the error bar.</summary>
    public bool IsConflict
    {
        get => (bool)GetValue(IsConflictProperty);
        set => SetValue(IsConflictProperty, value);
    }

    /// <summary>Review changes; null for an editor without conflict steps.</summary>
    public ICommand? ReviewConflictCommand
    {
        get => (ICommand?)GetValue(ReviewConflictCommandProperty);
        set => SetValue(ReviewConflictCommandProperty, value);
    }

    /// <summary>Reload: the reload confirmation step, or the reload itself for an editor without conflict steps.</summary>
    public ICommand? ReloadCommand
    {
        get => (ICommand?)GetValue(ReloadCommandProperty);
        set => SetValue(ReloadCommandProperty, value);
    }

    private static DependencyProperty Register(string name, Type type, object? defaultValue) =>
        DependencyProperty.Register(name, type, typeof(EditorMessageBars), new PropertyMetadata(defaultValue, (d, _) => ((EditorMessageBars)d).Apply()));

    private static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private void Apply()
    {
        var hasSummary = ValidationSummary is not null;
        var hasError = SaveError is not null;

        AutomationProperties.SetAutomationId(ValidationBar, AutomationPrefix + "_ValidationInfoBar");
        ValidationBar.Message = ValidationSummary;
        ValidationBar.IsOpen = hasSummary;
        ValidationBar.Visibility = Visible(hasSummary);

        AutomationProperties.SetAutomationId(ErrorBar, AutomationPrefix + "_ErrorInfoBar");
        ErrorBar.Message = SaveError;
        ErrorBar.IsOpen = hasError;
        ErrorBar.Visibility = Visible(hasError);

        var hasConflictSteps = ReviewConflictCommand is not null;
        ErrorBar.Content = hasConflictSteps ? ConflictActions : null;
        ErrorBar.ActionButton = hasConflictSteps ? null : ReloadActionButton;
        ReviewButton.Command = ReviewConflictCommand;
        ReloadButton.Command = ReloadCommand;
        ReloadActionButton.Command = ReloadCommand;
        ConflictActions.Visibility = Visible(IsConflict);
        ReloadActionButton.Visibility = Visible(IsConflict);

        Visibility = Visible(hasSummary || hasError);
    }
}
