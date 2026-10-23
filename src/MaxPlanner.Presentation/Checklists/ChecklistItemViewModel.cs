using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.Checklists;

/// <summary>
/// The card that owns checklist rows: the General to-dos card or a task group (B-09). Every command goes through
/// that card's use cases, so an item row never decides a rule itself (P8).
/// </summary>
public interface IChecklistItemOwner
{
    /// <summary>"Pop out to card" exists for task items only (B-16.3).</summary>
    bool CanPopOut { get; }

    Task ToggleAsync(ChecklistItemViewModel item, bool isChecked);

    /// <returns>Null when saved, otherwise the message to show under the rename box.</returns>
    Task<string?> RenameAsync(ChecklistItemViewModel item, string text);

    /// <summary>Move up/down within the item's own section (open or completed), next to its visible neighbour.</summary>
    Task MoveAsync(ChecklistItemViewModel item, bool up);

    bool CanMove(ChecklistItemViewModel item, bool up);

    Task DeleteAsync(ChecklistItemViewModel item);

    Task PopOutAsync(ChecklistItemViewModel item);
}

/// <summary>
/// One checklist row: a General to-do or a task's checklist item (design §4.5 item row). <see cref="IsChecked"/>
/// and <see cref="Text"/> are always the committed state (T-07); the rename text is a draft. Use on the UI thread.
/// </summary>
public sealed partial class ChecklistItemViewModel : ObservableObject
{
    private readonly IChecklistItemOwner _owner;
    private readonly ILocalizedStrings _strings;

    public ChecklistItemViewModel(Guid id, string text, bool isChecked, IChecklistItemOwner owner, ILocalizedStrings strings)
    {
        Id = id;
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _strings = strings ?? throw new ArgumentNullException(nameof(strings));
        Text = text;
        IsChecked = isChecked;
        PopOutCommand = new AsyncRelayCommand(() => _owner.PopOutAsync(this), () => !IsPending && _owner.CanPopOut);
        MoveUpCommand = new AsyncRelayCommand(() => _owner.MoveAsync(this, up: true), () => !IsPending && _owner.CanMove(this, up: true));
        MoveDownCommand = new AsyncRelayCommand(() => _owner.MoveAsync(this, up: false), () => !IsPending && _owner.CanMove(this, up: false));
    }

    public Guid Id { get; }

    public bool CanPopOut => _owner.CanPopOut;

    /// <summary>The row's keyboard help (design §7.6), read by screen readers.</summary>
    public string HelpText => _strings.GetString(CanPopOut ? "Checklists_ItemHelpTask" : "Checklists_ItemHelp");

    /// <summary>Item ⋯ › "Pop out to card" (B-16.3, design §4.5); task items only.</summary>
    public IAsyncRelayCommand PopOutCommand { get; }

    public IAsyncRelayCommand MoveUpCommand { get; }

    public IAsyncRelayCommand MoveDownCommand { get; }

    /// <summary>"More actions for Draft API spec" (design §8.2: icon buttons name the action and target).</summary>
    public string MenuButtonName => _strings.Format("Checklists_ItemMenuName", Text);

    /// <summary>"Delete 'Draft API spec'? This can't be undone." (design §4.5 light flyout).</summary>
    public string DeletePrompt => _strings.Format("Checklists_DeletePrompt", Text);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MenuButtonName), nameof(DeletePrompt))]
    public partial string Text { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsChecked { get; private set; }

    /// <summary>True while a command on this item is being saved.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PopOutCommand), nameof(MoveUpCommand), nameof(MoveDownCommand))]
    public partial bool IsPending { get; internal set; }

    /// <summary>F2 or double-click shows the inline rename box (design §4.5).</summary>
    [ObservableProperty]
    public partial bool IsRenaming { get; private set; }

    [ObservableProperty]
    public partial string RenameText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRenameError))]
    public partial string? RenameError { get; private set; }

    public bool HasRenameError => RenameError is not null;

    public void BeginRename()
    {
        if (IsPending)
        {
            return;
        }

        RenameText = Text;
        RenameError = null;
        IsRenaming = true;
    }

    /// <summary>Enter in the rename box: saves the new text; a failure keeps the box open with its draft and error.</summary>
    public async Task<bool> CommitRenameAsync()
    {
        if (!IsRenaming || IsPending)
        {
            return false;
        }

        if (RenameText == Text)
        {
            CancelRename();
            return true;
        }

        RenameError = await _owner.RenameAsync(this, RenameText).ConfigureAwait(true);
        if (RenameError is null)
        {
            IsRenaming = false;
        }

        return RenameError is null;
    }

    /// <summary>Esc: the committed text stays.</summary>
    public void CancelRename()
    {
        IsRenaming = false;
        RenameError = null;
    }

    /// <summary>Delete after the flyout confirmation (design §4.5, B-05).</summary>
    public Task DeleteAsync() => _owner.DeleteAsync(this);

    /// <summary>Checkbox Checked/Unchecked from the user (B-09).</summary>
    public Task ToggleAsync(bool isChecked) => _owner.ToggleAsync(this, isChecked);

    internal void Update(string text, bool isChecked)
    {
        Text = text;
        IsChecked = isChecked;
    }

    /// <summary>The neighbours changed, so Move up/down may now be (un)available.</summary>
    internal void RefreshCommands()
    {
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        PopOutCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// A failed toggle: the checkbox already shows the user's click, so the committed value is announced
    /// again to put the control back (x:Bind re-reads it on the notification).
    /// </summary>
    internal void RestoreCommitted() => OnPropertyChanged(nameof(IsChecked));
}

/// <summary>
/// Runs a card's checklist commands one at a time and reports actual results (T-07). Items of a task are edited
/// through the task's revision (T-05), so a second quick command waits until the first result is displayed and
/// then reads the new revision instead of conflicting with the first.
/// </summary>
internal sealed class ChecklistCommandQueue(WorkspaceContext context, string recordKey)
{
    private static readonly TimeSpan DisplayTimeout = TimeSpan.FromSeconds(5);

    private Task _tail = Task.CompletedTask;

    /// <summary>Runs <paramref name="command"/> after the card's previous command and its store update.</summary>
    public Task Enqueue(Func<Task> command)
    {
        var previous = _tail;
        var next = RunAfterAsync(previous, command);
        _tail = next;
        return next;
    }

    /// <returns>Null when committed and displayed; otherwise the failure message (also in the save status).</returns>
    public async Task<string?> RunAsync(Func<OperationId, Task<AppResult<CommandOutcome<long>>>> command)
    {
        if (context.IsEditingBlocked)
        {
            return context.Strings.GetString("Error_EditingBlocked");
        }

        var operation = OperationId.New();
        context.SaveStatus.Begin();
        var result = await command(operation).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            var failure = CommandFailure.From(result.Error!, operation, recordKey, context.Strings);
            context.SaveStatus.Report(failure);
            context.Announce(failure.Message, isFailure: true);
            return failure.Message;
        }

        context.SaveStatus.Complete();
        await context.WhenDisplayedAsync(result.Value.CommitSequence, DisplayTimeout).ConfigureAwait(true);
        return null;
    }

    private static async Task RunAfterAsync(Task previous, Func<Task> command)
    {
        await previous.ConfigureAwait(true);
        await command().ConfigureAwait(true);
    }
}
