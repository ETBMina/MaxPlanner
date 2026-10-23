using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MaxPlanner.Presentation.Settings.DataPage;

/// <summary>Manage backups (T-09). Code-behind routes the row's Delete button and applies the page insets.</summary>
public sealed partial class BackupsView : UserControl
{
    public BackupsView(BackupsViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
    }

    public BackupsViewModel ViewModel { get; }

    public void SetLayout(double padding) => BackupsPanel.Padding = new Thickness(padding, 0, padding, 16);

    private void OnDeleteBackupClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BackupRowViewModel row })
        {
            ViewModel.DeleteCommand.Execute(row);
        }
    }
}
