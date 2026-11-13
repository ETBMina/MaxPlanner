using MaxPlanner.Presentation.Common.Dialogs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MaxPlanner.Presentation.Reviews;

/// <summary>
/// The review editor dialog. Buttons bind <see cref="ReviewEditorViewModel"/> commands, and <see cref="EditorDialogShell"/>
/// wires the dialog buttons and Ctrl+Enter; code-behind moves focus where the ViewModel asks, and uses a full-size dialog in small or short windows (design §5.0).
/// </summary>
public sealed partial class ReviewEditorDialog : ContentDialog
{
    public ReviewEditorDialog(ReviewEditorViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        EditorDialogShell.Attach(this, viewModel, Root, OnFocusRequested, () => TitleBox);
    }

    public ReviewEditorViewModel ViewModel { get; }

    private void OnFocusRequested(string field)
    {
        // Design §5.0: focus moves to the first invalid field.
        Action? focus = field switch
        {
            "Title" => () => TitleBox.Focus(FocusState.Programmatic),
            "CrNumber" => () => CrNumberBox.Focus(FocusState.Programmatic),
            "TaskNumber" => () => TaskNumberBox.Focus(FocusState.Programmatic),
            "Services" => () => ServicesPicker.FocusBox(),
            "Developer" => () => DeveloperPicker.FocusBox(),
            "Comments" => () => CommentsBox.Focus(FocusState.Programmatic),
            _ => null,
        };
        if (focus is not null)
        {
            DispatcherQueue.TryEnqueue(() => focus());
        }
    }
}
