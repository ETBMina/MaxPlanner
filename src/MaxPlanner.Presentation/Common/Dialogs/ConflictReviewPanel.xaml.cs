using System.Windows.Input;
using MaxPlanner.Presentation.Common.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MaxPlanner.Presentation.Common.Dialogs;

/// <summary>
/// The conflict review and reload confirmation steps of a draft editor (design §5.10), bound to a
/// <see cref="ConflictEditorViewModelBase{TFollowUp}"/>. Collapsed while neither step is shown.
/// </summary>
public sealed partial class ConflictReviewPanel : UserControl
{
    /// <summary>The reload message most editors use.</summary>
    public const string DefaultReloadMessageKey = EditorControlRules.DefaultReloadMessageKey;

    public static readonly DependencyProperty IsReviewStepProperty = Register(nameof(IsReviewStep), typeof(bool), false);

    public static readonly DependencyProperty IsReloadStepProperty = Register(nameof(IsReloadStep), typeof(bool), false);

    public static readonly DependencyProperty RowsProperty = Register(nameof(Rows), typeof(object), null);

    public static readonly DependencyProperty BackCommandProperty = Register(nameof(BackCommand), typeof(ICommand), null);

    public static readonly DependencyProperty ApplyCommandProperty = Register(nameof(ApplyCommand), typeof(ICommand), null);

    public static readonly DependencyProperty ConfirmReloadCommandProperty = Register(nameof(ConfirmReloadCommand), typeof(ICommand), null);

    public static readonly DependencyProperty ReloadMessageKeyProperty = Register(nameof(ReloadMessageKey), typeof(string), DefaultReloadMessageKey);

    private string? _shownReloadMessageKey;

    public ConflictReviewPanel()
    {
        InitializeComponent();
        Apply();
    }

    public bool IsReviewStep
    {
        get => (bool)GetValue(IsReviewStepProperty);
        set => SetValue(IsReviewStepProperty, value);
    }

    public bool IsReloadStep
    {
        get => (bool)GetValue(IsReloadStepProperty);
        set => SetValue(IsReloadStepProperty, value);
    }

    /// <summary>The <see cref="ConflictRowViewModel"/> rows of the review.</summary>
    public object? Rows
    {
        get => GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    /// <summary>Back from either step.</summary>
    public ICommand? BackCommand
    {
        get => (ICommand?)GetValue(BackCommandProperty);
        set => SetValue(BackCommandProperty, value);
    }

    /// <summary>Apply to draft.</summary>
    public ICommand? ApplyCommand
    {
        get => (ICommand?)GetValue(ApplyCommandProperty);
        set => SetValue(ApplyCommandProperty, value);
    }

    public ICommand? ConfirmReloadCommand
    {
        get => (ICommand?)GetValue(ConfirmReloadCommandProperty);
        set => SetValue(ConfirmReloadCommandProperty, value);
    }

    /// <summary>
    /// The <c>x:Uid</c> of the reload message, whose <c>.Text</c> resource is shown; <see cref="DefaultReloadMessageKey"/>
    /// unless the editor names the record, for example <c>CrEditor_ReloadMessage</c>.
    /// </summary>
    public string? ReloadMessageKey
    {
        get => (string?)GetValue(ReloadMessageKeyProperty);
        set => SetValue(ReloadMessageKeyProperty, value);
    }

    private static DependencyProperty Register(string name, Type type, object? defaultValue) =>
        DependencyProperty.Register(name, type, typeof(ConflictReviewPanel), new PropertyMetadata(defaultValue, (d, _) => ((ConflictReviewPanel)d).Apply()));

    private static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private void Apply()
    {
        RowsList.ItemsSource = Rows;
        BackButton.Command = BackCommand;
        ReloadBackButton.Command = BackCommand;
        ApplyButton.Command = ApplyCommand;
        ReloadConfirmButton.Command = ConfirmReloadCommand;

        var reloadMessageKey = EditorControlRules.ReloadMessageKeyOrDefault(ReloadMessageKey);
        if (!string.Equals(_shownReloadMessageKey, reloadMessageKey, StringComparison.Ordinal))
        {
            ReloadMessageText.Text = ResourceStrings.Shared.GetString(XamlResourceKeys.Text(reloadMessageKey));
            _shownReloadMessageKey = reloadMessageKey;
        }

        ReviewStep.Visibility = Visible(IsReviewStep);
        ReloadStep.Visibility = Visible(IsReloadStep);
        Visibility = Visible(IsReviewStep || IsReloadStep);
    }
}
