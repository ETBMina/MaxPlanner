using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Transfer;
using MaxPlanner.Presentation.Common.Services;

namespace MaxPlanner.Presentation.Settings.DataPage;

/// <summary>
/// The step model of the Data page wizards (design §4.6): a step list at L, "Step 2 of 5 · Map columns" at M/S, and
/// Back / Next / Cancel. Work runs off the UI thread; Cancel is honored only before a commit (T-10). Use on the UI thread.
/// </summary>
public abstract partial class WizardViewModel : ObservableObject, IDisposable
{
    private CancellationTokenSource? _work;

    protected WizardViewModel(ILocalizedStrings strings, IReadOnlyList<string> stepKeys)
    {
        Strings = strings ?? throw new ArgumentNullException(nameof(strings));
        Steps = [.. stepKeys.Select(k => new WizardStep(strings.GetString(k)))];
        UpdateSteps();
    }

    public IReadOnlyList<WizardStep> Steps { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepCaption), nameof(NextText))]
    [NotifyCanExecuteChangedFor(nameof(BackCommand), nameof(NextCommand))]
    public partial int StepIndex { get; protected set; }

    /// <summary>"Step 2 of 5 · Map columns" (design §4.6, M/S).</summary>
    /// <summary>The verb of the accent button on the current step (Next, Export, Apply, Restore).</summary>
    public virtual string NextText => Strings.GetString("Data_NextButtonText");

    public string StepCaption => Strings.Format("Data_StepCaption", StepIndex + 1, Steps.Count, Steps[StepIndex].Title);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(BackCommand), nameof(NextCommand), nameof(CancelWorkCommand))]
    public partial bool IsBusy { get; protected set; }

    public bool IsIdle => !IsBusy;

    /// <summary>What the wizard is doing while busy, shown beside the progress bar.</summary>
    [ObservableProperty]
    public partial string? ProgressText { get; protected set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; protected set; }

    public bool HasError => ErrorMessage is not null;

    protected ILocalizedStrings Strings { get; }

    protected bool IsDisposed { get; private set; }

    public virtual void Dispose()
    {
        IsDisposed = true;
        _work?.Cancel();
        _work?.Dispose();
        _work = null;
        GC.SuppressFinalize(this);
    }

    /// <summary>Whether Next is available on the current step.</summary>
    protected abstract bool CanAdvance { get; }

    /// <summary>Runs the current step's work and returns the next step index, or null to stay.</summary>
    protected abstract Task<int?> AdvanceAsync(CancellationToken cancellationToken);

    /// <summary>The step Back returns to from the current one.</summary>
    protected virtual int PreviousStep(int current) => current - 1;

    protected void RefreshCommands()
    {
        BackCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
    }

    protected void GoTo(int step)
    {
        StepIndex = Math.Clamp(step, 0, Steps.Count - 1);
        UpdateSteps();
    }

    /// <summary>Runs <paramref name="work"/> with a cancellation source that <see cref="CancelWorkCommand"/> cancels.</summary>
    protected async Task<T?> RunAsync<T>(Func<CancellationToken, Task<T>> work, string? progressKey)
    {
        _work?.Dispose();
        _work = new CancellationTokenSource();
        IsBusy = true;
        ErrorMessage = null;
        ProgressText = progressKey is null ? null : Strings.GetString(progressKey);
        try
        {
            return await work(_work.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = Strings.GetString("Error_Cancelled");
            return default;
        }
        finally
        {
            IsBusy = false;
            ProgressText = null;
        }
    }

    /// <summary>Localized, actionable text for a transfer or storage failure (T-06.7, T-10).</summary>
    protected string Describe(AppError error) => error switch
    {
        TransferFileError file => Strings.Format("Transfer_Error_" + file.Failure, file.Detail ?? string.Empty),
        ImportPlanStaleError => Strings.GetString("Import_Stale"),
        ImportNotApplicableError => Strings.GetString("Import_NotApplicable"),
        _ => CommandErrorText.Describe(error, "Record_Import", Strings),
    };

    protected static string Number(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    private bool CanGoBack => !IsBusy && StepIndex > 0 && StepIndex < Steps.Count - 1;

    private bool CanGoNext => !IsBusy && StepIndex < Steps.Count - 1 && CanAdvance;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back()
    {
        ErrorMessage = null;
        GoTo(PreviousStep(StepIndex));
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextAsync()
    {
        var next = await RunAsync(AdvanceAsync, "Data_Working").ConfigureAwait(true);
        if (!IsDisposed && next is { } step)
        {
            GoTo(step);
        }
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void CancelWork() => _work?.Cancel();

    private void UpdateSteps()
    {
        for (var i = 0; i < Steps.Count; i++)
        {
            Steps[i].IsCurrent = i == StepIndex;
            Steps[i].IsDone = i < StepIndex;
        }
    }
}

/// <summary>One entry of the step list; its state is text as well as a glyph (design §8).</summary>
public sealed partial class WizardStep(string title) : ObservableObject
{
    public string Title { get; } = title;

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    [ObservableProperty]
    public partial bool IsDone { get; set; }
}
