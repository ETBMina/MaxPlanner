using System.ComponentModel;
using MaxPlanner.Presentation.Grid;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MaxPlanner.Presentation.Tasks;

/// <summary>
/// The Copy task dialog. Buttons bind commands; code-behind forwards the primary button and keeps the two lookups'
/// selections in step.
/// </summary>
public sealed partial class CopyTaskDialog : ContentDialog
{
    public CopyTaskDialog(CopyTaskViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        StatusCombo.SelectedItem = viewModel.SelectedStatus;
        PriorityCombo.SelectedItem = viewModel.SelectedPriority;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Unloaded += (_, _) => viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    public CopyTaskViewModel ViewModel { get; }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // "Keep current status" changes the selection from the ViewModel side.
        if (e.PropertyName == nameof(CopyTaskViewModel.SelectedStatus) && !Equals(StatusCombo.SelectedItem, ViewModel.SelectedStatus))
        {
            StatusCombo.SelectedItem = ViewModel.SelectedStatus;
        }
    }

    private void OnPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Options, then Review, then Create copy; the ViewModel closes the dialog after a successful create.
        args.Cancel = true;
        ViewModel.PrimaryCommand.Execute(null);
    }

    // Not two-way SelectedItem bindings: these keep the last real choice when the selection becomes null.
    private void OnStatusSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StatusCombo.SelectedItem is StatusOption option)
        {
            ViewModel.SelectedStatus = option;
        }
    }

    private void OnPrioritySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PriorityCombo.SelectedItem is PriorityOption option)
        {
            ViewModel.SelectedPriority = option;
        }
    }

    private void OnCrSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CrResultsList.SelectedItem is CrChoice choice)
        {
            ViewModel.CrPicker.Selected = choice;
        }
    }
}
