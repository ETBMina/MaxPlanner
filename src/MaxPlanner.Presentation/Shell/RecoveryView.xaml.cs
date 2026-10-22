using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MaxPlanner.Presentation.Shell;

public sealed partial class RecoveryView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(RecoveryViewModel), typeof(RecoveryView), new PropertyMetadata(null, OnViewModelChanged));

    public RecoveryView()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
    }

    public RecoveryViewModel? ViewModel
    {
        get => (RecoveryViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    // Slice 1f evidence: AdaptiveTrigger states inside this UserControl did not follow the window at 500 epx
    // (docs/evidence/phase-1/2026-10-22), so the T-08.1 breakpoint is applied from the page's own width.
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var state = e.NewSize.Width >= 1008 ? "Large" : e.NewSize.Width >= 640 ? "Medium" : "Small";
        VisualStateManager.GoToState(this, state, useTransitions: false);
    }

    private static void OnViewModelChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var view = (RecoveryView)sender;
        if (args.OldValue is RecoveryViewModel previous)
        {
            previous.PropertyChanged -= view.OnViewModelPropertyChanged;
        }

        if (args.NewValue is RecoveryViewModel current)
        {
            current.PropertyChanged += view.OnViewModelPropertyChanged;
        }
    }

    // Focus moves with the confirmation step so keyboard and Narrator users land on the next decision.
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RecoveryViewModel.IsConfirmingCreate) || sender is not RecoveryViewModel viewModel)
        {
            return;
        }

        var target = viewModel.IsConfirmingCreate ? ConfirmCreateButton : CreateNewButton;

        // Queued so the button is visible (layout updated) before focus is requested.
        DispatcherQueue.TryEnqueue(() => target.Focus(FocusState.Programmatic));
    }
}
