using CommunityToolkit.Mvvm.Input;

namespace MaxPlanner.Presentation.Common.Dialogs;

/// <summary>
/// What <see cref="EditorDialogShell"/> needs from a draft editor: Save, the close guard, and focus requests
/// (design §5.0, §5.8). <see cref="EditorViewModelBase{TFollowUp}"/> implements it.
/// </summary>
public interface IEditorDialogViewModel : IDirtyCloseEditor
{
    /// <summary>Asks the view to focus a field key such as <c>Title</c> (design §5.0).</summary>
    event EventHandler<string>? FocusRequested;

    /// <summary>Ctrl+Enter and the primary button.</summary>
    IAsyncRelayCommand SaveCommand { get; }
}

/// <summary>An editor with inline steps besides its form (design §5.0); Alt+Left goes back from them (design §7.2).</summary>
public interface IInlineStepEditor
{
    bool IsFormStep { get; }

    /// <summary>Back from an inline step.</summary>
    void StepBack();
}
