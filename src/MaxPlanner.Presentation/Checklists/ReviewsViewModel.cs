using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaxPlanner.Application.Filtering;
using MaxPlanner.Application.Reviews;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Reviews;
using MaxPlanner.Presentation.Common.Projections;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Reviews;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.Checklists;

/// <summary>
/// One review line (B-17.2, design §4.5): Done checkbox, title, and the CR/task reference as plain text (never a
/// link, B-17.3). It shows committed state only. Use on the UI thread.
/// </summary>
public sealed partial class ReviewRowViewModel : ObservableObject
{
    private readonly ReviewsViewModel _owner;
    private readonly ILocalizedStrings _strings;

    internal ReviewRowViewModel(ReviewItem model, ReviewsViewModel owner, ILocalizedStrings strings)
    {
        _owner = owner;
        _strings = strings;
        Model = model;
        MoveUpCommand = new AsyncRelayCommand(() => _owner.MoveAsync(this, up: true), () => !IsPending && _owner.CanMove(this, up: true));
        MoveDownCommand = new AsyncRelayCommand(() => _owner.MoveAsync(this, up: false), () => !IsPending && _owner.CanMove(this, up: false));
    }

    public ReviewItem Model { get; private set; }

    public Guid Id => Model.Id.Value;

    public string Title => Model.Content.Title.Value;

    public bool IsDone => Model.IsDone;

    /// <summary>"CR-2201 · Task 0091" (design §4.5), or empty when neither number was given.</summary>
    public string Reference => string.Join(" · ", new[]
    {
        Model.Content.CrNumberText?.Value,
        Model.Content.TaskNumberText is { } task ? _strings.Format("Reviews_TaskReference", task.Value) : null,
    }.Where(part => !string.IsNullOrEmpty(part)));

    public bool HasReference => Reference.Length > 0;

    /// <summary>"Done Oct 20" for the Completed section (design §4.5).</summary>
    public string DoneText => Model.CompletedOn is { } day ? _strings.Format("Reviews_DoneOn", day.ToString("MMM d", CultureInfo.CurrentCulture)) : string.Empty;

    public string AccessibleName => string.Join(", ", new[] { Title, Reference, IsDone ? DoneText : _strings.GetString("Reviews_Open") }.Where(p => p.Length > 0));

    public string MenuButtonName => _strings.Format("Checklists_ItemMenuName", Title);

    /// <summary>"Done: Review card limits" (the checkbox's accessible name).</summary>
    public string CheckName => _strings.Format("Checklists_ReviewCheckName", Title);

    public string DeletePrompt => _strings.Format("Checklists_DeletePrompt", Title);

    public IAsyncRelayCommand MoveUpCommand { get; }

    public IAsyncRelayCommand MoveDownCommand { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand), nameof(MoveDownCommand))]
    public partial bool IsPending { get; internal set; }

    public Task ToggleAsync(bool isDone) => _owner.ToggleAsync(this, isDone);

    public Task EditAsync() => _owner.EditAsync(this);

    public Task DeleteAsync() => _owner.DeleteAsync(this);

    internal void Update(ReviewItem model)
    {
        if (!ReferenceEquals(Model, model))
        {
            Model = model;
            OnPropertyChanged(string.Empty);
        }
    }

    internal void RefreshCommands()
    {
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    /// <summary>A failed toggle puts the checkbox back to the committed value.</summary>
    internal void RestoreCommitted() => OnPropertyChanged(nameof(IsDone));
}

/// <summary>
/// The pinned Reviews card of the Checklists page (B-17, design §4.5): open reviews in explicit order, an inline
/// Add a review box, a collapsed Completed section, the review editor, and search over every review field
/// (B-17.5). Use on the UI thread.
/// </summary>
public sealed partial class ReviewsViewModel : ObservableObject, IDisposable
{
    private readonly WorkspaceContext _context;
    private readonly ChecklistCommandQueue _queue;
    private readonly ObservableCollection<ReviewRowViewModel> _open = [];
    private readonly ObservableCollection<ReviewRowViewModel> _completed = [];
    private readonly IDisposable _commits;
    private string _query = string.Empty;
    private ReviewFilterSpec? _dashboard;

    public ReviewsViewModel(WorkspaceContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _queue = new ChecklistCommandQueue(context, "Record_Review");
        Open = new ReadOnlyObservableCollection<ReviewRowViewModel>(_open);
        Completed = new ReadOnlyObservableCollection<ReviewRowViewModel>(_completed);
        _commits = context.Store.Committed.ObserveOn(context.UiScheduler).Subscribe(_ => Sync());
        Sync();
    }

    public ReadOnlyObservableCollection<ReviewRowViewModel> Open { get; }

    public ReadOnlyObservableCollection<ReviewRowViewModel> Completed { get; }

    /// <summary>Open reviews in the dataset, whatever the search (page subtitle and nav badge).</summary>
    [ObservableProperty]
    public partial int OpenCount { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText), nameof(ProgressPercent))]
    public partial int TotalCount { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText), nameof(ProgressPercent))]
    public partial int DoneCount { get; private set; }

    public int MatchCount => _open.Count + _completed.Count;

    public string ProgressText => _context.Strings.Format("Checklists_Progress", DoneCount, TotalCount);

    public double ProgressPercent => TotalCount == 0 ? 0 : 100.0 * DoneCount / TotalCount;

    /// <summary>The Add a review box's draft; bound two-way and kept when adding fails.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddReviewCommand))]
    public partial string NewItemText { get; set; } = string.Empty;

    /// <summary>The inline error under the Add a review box (blank title, storage).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNewItemError))]
    public partial string? NewItemError { get; private set; }

    public bool HasNewItemError => NewItemError is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddReviewCommand))]
    public partial bool IsAdding { get; private set; }

    public string CompletedHeader => _context.Strings.Format("Checklists_CompletedHeader", _completed.Count);

    public bool HasCompleted => _completed.Count > 0;

    public bool HasOpen => _open.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActionError))]
    public partial string? ActionError { get; private set; }

    public bool HasActionError => ActionError is not null;

    /// <summary>B-17.5: search covers every review text field, including the developer's name.</summary>
    public void ApplySearch(string query)
    {
        if (_query != query)
        {
            _query = query;
            Sync();
        }
    }

    /// <summary>
    /// A Dashboard review drill-down (B-15.3, design §2.9): shows only the reviews of that metric and developer, with
    /// the same predicate the Dashboard counted; null ends it.
    /// </summary>
    public void ApplyDashboard(ReviewFilterSpec? spec)
    {
        _dashboard = spec;
        Sync();
    }

    /// <summary>The Reviews card's "New review…" (design §4.5): the review editor in create mode.</summary>
    [RelayCommand]
    public Task NewReviewAsync() => RunEditorAsync(null);

    public Task EditAsync(ReviewRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return RunEditorAsync(row.Model.Id);
    }

    /// <summary>The row checkbox (B-17.1): Done sets CompletedOn to today, unchecking clears it.</summary>
    public Task ToggleAsync(ReviewRowViewModel row, bool isDone)
    {
        ArgumentNullException.ThrowIfNull(row);
        return _queue.Enqueue(async () =>
        {
            if (row.IsDone == isDone || Find(row) is not { } review)
            {
                return;
            }

            row.IsPending = true;
            ActionError = await _queue.RunAsync(op => _context.UseCases.SetReviewDone.ExecuteAsync(
                new SetReviewDoneCommand(op, review.Id, review.Revision, isDone), CancellationToken.None)).ConfigureAwait(true);
            row.IsPending = false;
            if (ActionError is not null)
            {
                row.RestoreCommitted();
            }
            else
            {
                // Design §8.3: "Review Card limits done, completed today".
                _context.Announce(_context.Strings.Format(isDone ? "Reviews_DoneAnnounced" : "Reviews_OpenAnnounced", row.Title));
            }
        });
    }

    public bool CanMove(ReviewRowViewModel row, bool up) => Neighbour(row, up) is not null;

    public Task MoveAsync(ReviewRowViewModel row, bool up)
    {
        ArgumentNullException.ThrowIfNull(row);
        return _queue.Enqueue(async () =>
        {
            if (Neighbour(row, up) is not { } anchor || Find(row) is not { } review)
            {
                return;
            }

            row.IsPending = true;
            ActionError = await _queue.RunAsync(op => _context.UseCases.MoveReview.ExecuteAsync(
                new MoveReviewCommand(op, review.Id, review.Revision, anchor.Model.Id, PlaceAfter: !up), CancellationToken.None)).ConfigureAwait(true);
            row.IsPending = false;
        });
    }

    public Task DeleteAsync(ReviewRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return DeleteAsync(row.Model.Id, row.Title);
    }

    public void Dispose() => _commits.Dispose();

    /// <summary>
    /// B-09 completed order for reviews: most recently completed first, by CompletedOn and then the audit timestamp
    /// within a day; the sort is stable, so ties keep the explicit order of <paramref name="ranked"/>.
    /// </summary>
    public static IEnumerable<ReviewItem> CompletedDisplayOrder(IEnumerable<ReviewItem> ranked) =>
        ranked.Where(r => r.IsDone).OrderByDescending(r => r.CompletedOn).ThenByDescending(r => r.UpdatedAtUtc);

    /// <summary>Enter in the Add a review box or its Add button: a title-only review; blank text shows an inline error.</summary>
    [RelayCommand(CanExecute = nameof(CanAddReview))]
    private Task AddReviewAsync() => _queue.Enqueue(async () =>
    {
        IsAdding = true;
        var draft = new ReviewDraft(NewItemText, CrNumber: null, TaskNumber: null, Services: [], DeveloperId: null, Comments: null);
        NewItemError = await _queue.RunAsync(op => _context.UseCases.CreateReview.ExecuteAsync(
            new CreateReviewCommand(op, new ReviewItemId(Guid.NewGuid()), draft, NewDeveloper: null, IsDone: false), CancellationToken.None)).ConfigureAwait(true);
        if (NewItemError is null)
        {
            NewItemText = string.Empty;
        }

        IsAdding = false;
    });

    private bool CanAddReview() => !IsAdding;

    private Task DeleteAsync(ReviewItemId id, string title) => _queue.Enqueue(async () =>
    {
        if (_context.Store.ReviewItems.Lookup(id) is not { HasValue: true } review)
        {
            return;
        }

        ActionError = await _queue.RunAsync(op => _context.UseCases.DeleteReview.ExecuteAsync(
            new DeleteReviewCommand(op, id, review.Value.Revision), CancellationToken.None)).ConfigureAwait(true);
        if (ActionError is null)
        {
            _context.Announce(_context.Strings.Format("Checklists_ItemDeleted", title));
        }
    });

    private async Task RunEditorAsync(ReviewItemId? id)
    {
        if (_context.Store.CurrentWorkflow is null)
        {
            return;
        }

        var editor = new ReviewEditorViewModel(_context, id);
        using (editor)
        {
            await _context.ShowDialogAsync(editor).ConfigureAwait(true);
        }

        // Dialogs never stack (design §5.0): follow-ups run after the editor closed.
        switch (editor.FollowUp)
        {
            case ReviewEditorFollowUp.OpenCr when editor.MatchingCrId is { } crId:
                await _context.ChangeRequests.OpenAsync(crId).ConfigureAwait(true);
                break;
            case ReviewEditorFollowUp.Delete when id is { } reviewId && _context.Store.ReviewItems.Lookup(reviewId) is { HasValue: true } review:
                var confirmation = new Common.Dialogs.ConfirmationViewModel(
                    "DeleteReview_Dialog",
                    _context.Strings.GetString("Reviews_DeleteTitle"),
                    _context.Strings.Format("Checklists_DeletePrompt", review.Value.Content.Title.Value),
                    _context.Strings.GetString("Reviews_DeleteButton"),
                    null,
                    _context.Strings.GetString("Common_Cancel"));
                await _context.ShowDialogAsync(confirmation).ConfigureAwait(true);
                if (confirmation.Result == Common.Dialogs.ConfirmationResult.Primary)
                {
                    await DeleteAsync(reviewId, review.Value.Content.Title.Value).ConfigureAwait(true);
                }

                break;
            default:
                break;
        }
    }

    private ReviewItem? Find(ReviewRowViewModel row) =>
        _context.Store.ReviewItems.Lookup(row.Model.Id) is { HasValue: true } review ? review.Value : null;

    // Move up/down applies to open reviews; completed ones are shown in completion order (B-09).
    private ReviewRowViewModel? Neighbour(ReviewRowViewModel row, bool up)
    {
        var index = _open.IndexOf(row);
        var target = up ? index - 1 : index + 1;
        return index >= 0 && target >= 0 && target < _open.Count ? _open[target] : null;
    }

    private bool Matches(ReviewItem review)
    {
        if (_dashboard is { } dashboard && !ReviewFilterMatcher.Matches(dashboard, review, _context.Store.Today))
        {
            return false;
        }

        if (_query.Length == 0)
        {
            return true;
        }

        var content = review.Content;
        var developer = content.DeveloperId is { } id ? _context.Store.CurrentWorkflow?.FindPerson(id)?.Name.Display : null;
        return Has(content.Title.Value) || Has(content.CrNumberText?.Value) || Has(content.TaskNumberText?.Value)
            || content.Services.Any(s => Has(s.Value)) || Has(developer) || Has(content.Comments?.Value);

        bool Has(string? value) => value?.Contains(_query, StringComparison.CurrentCultureIgnoreCase) == true;
    }

    private void Sync()
    {
        var all = _context.Store.ReviewItems.Items.OrderBy(r => r.Rank).ThenBy(r => r.Id.Value).ToList();
        TotalCount = all.Count;
        DoneCount = all.Count(r => r.IsDone);
        OpenCount = TotalCount - DoneCount;
        var shown = all.Where(Matches).ToList();
        var existing = _open.Concat(_completed).ToDictionary(r => r.Id);

        // A row moving between sections is inserted as a new key there, so the reused row takes the new model
        // first; otherwise it renders its previous Done state (see ChecklistSections.Sync).
        ReviewRowViewModel Reuse(ReviewItem model)
        {
            if (!existing.TryGetValue(model.Id.Value, out var row))
            {
                return new ReviewRowViewModel(model, this, _context.Strings);
            }

            row.Update(model);
            return row;
        }

        KeyedCollectionSync.Sync(_open, [.. shown.Where(r => !r.IsDone)], m => m.Id.Value, vm => vm.Id, Reuse, (vm, m) => vm.Update(m));
        KeyedCollectionSync.Sync(_completed, [.. CompletedDisplayOrder(shown)], m => m.Id.Value, vm => vm.Id, Reuse, (vm, m) => vm.Update(m));
        foreach (var row in _open.Concat(_completed))
        {
            row.RefreshCommands();
        }

        OnPropertyChanged(nameof(CompletedHeader));
        OnPropertyChanged(nameof(HasCompleted));
        OnPropertyChanged(nameof(HasOpen));
        OnPropertyChanged(nameof(MatchCount));
    }
}
