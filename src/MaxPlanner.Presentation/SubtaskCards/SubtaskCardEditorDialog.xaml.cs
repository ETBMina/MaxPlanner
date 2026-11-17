using System.ComponentModel;
using MaxPlanner.Presentation.Common.Dialogs;
using MaxPlanner.Presentation.Common.Options;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MaxPlanner.Presentation.SubtaskCards;

/// <summary>
/// The Subtask card editor dialog. Buttons bind <see cref="SubtaskCardEditorViewModel"/> commands, and
/// <see cref="EditorDialogShell"/> wires the dialog buttons and keys; code-behind keeps the Status selection in step with it, moves focus where
/// the ViewModel asks, and sets the info box's columns by width; <see cref="EditorDialogLayout"/> uses a full-size dialog in
/// small or short windows (design §5.0).
/// </summary>
public sealed partial class SubtaskCardEditorDialog : ContentDialog
{
    public SubtaskCardEditorDialog(SubtaskCardEditorViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        EditorDialogShell.Attach(this, viewModel, Root, OnFocusRequested, () => SubtitleBox, size => FactsPanel.Columns = EditorFactsLayout.Columns(size.Width));
        StatusCombo.SelectedItem = viewModel.SelectedStatus;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Unloaded += (_, _) => viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    public SubtaskCardEditorViewModel ViewModel { get; }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SubtaskCardEditorViewModel.SelectedStatus) && !Equals(StatusCombo.SelectedItem, ViewModel.SelectedStatus))
        {
            StatusCombo.SelectedItem = ViewModel.SelectedStatus;
        }
    }

    private void OnFocusRequested(string field)
    {
        // Design §5.0: focus moves to the first invalid field.
        Control? target = field switch
        {
            "Subtitle" => SubtitleBox,
            "Status" => StatusCombo,
            "EndDate" => EndDateField.DatePicker,
            "Notes" => NotesBox,
            _ => null,
        };
        if (target is not null)
        {
            DispatcherQueue.TryEnqueue(() => target.Focus(FocusState.Programmatic));
        }
    }

    // Not a two-way SelectedItem: that could write null back while ItemsSource is (re)set.
    private void OnStatusSelectionChanged(object sender, SelectionChangedEventArgs e) => ViewModel.SelectedStatus = StatusCombo.SelectedItem as StatusOption;
}
