using System.Globalization;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Lookups;
using MaxPlanner.Application.Reviews;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Reviews;
using MaxPlanner.Presentation.Common.Dialogs;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Tasks;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.Reviews;

/// <summary>What the review editor asked for after it closes (design §5.0: dialogs never stack).</summary>
public enum ReviewEditorFollowUp
{
    None,
    OpenCr,
    Delete,
}

/// <summary>
/// The review editor (B-17, design §5.4): Title*, CR number (free text with an "Open CR" affordance when it
/// matches an existing CR, never a link), Task number, Services, Developer (add-new on save), Review comments and
/// Done. It edits a draft and commits it through one use case with a revision check (B-02.8–9). Use on the UI thread.
/// </summary>
public sealed partial class ReviewEditorViewModel : ObservableObject, IDialogViewModel, IDirtyCloseEditor, IDisposable
{
    private static readonly TimeSpan DisplayTimeout = TimeSpan.FromSeconds(5);

    private readonly WorkspaceContext _context;
    private readonly NewNameRegistry _newPeople = new();
    private readonly NewNameRegistry _services = new();
    private readonly IDisposable _commits;
    private readonly ReviewItemId _id;
    private ReviewItem? _base;
    private string _baseSignature = string.Empty;
    private bool _closeApproved;
    private bool _isLoading;
    private OperationId? _retryOperation;

    /// <param name="reviewId">The review to edit, or null for a new review.</param>
    public ReviewEditorViewModel(WorkspaceContext context, ReviewItemId? reviewId)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        var workflow = context.Store.CurrentWorkflow
            ?? throw new InvalidOperationException("The review editor needs the loaded workflow; editing is blocked until the store is ready.");
        IsNew = reviewId is null;
        _id = reviewId ?? new ReviewItemId(Guid.NewGuid());

        // Design §5.0: Developer is a single-person picker; Services are free-text chips.
        Developer = new TokenPickerViewModel(context.Strings, () => (context.Store.CurrentWorkflow ?? workflow).People.Select(p => (p.Id.Value, p.Name.Display)), _newPeople) { IsSingleSelection = true, IsPeople = true };
        Services = new TokenPickerViewModel(context.Strings, () => [], _services);
        Developer.Changed += OnPickerChanged;
        Services.Changed += OnPickerChanged;

        if (!IsNew)
        {
            var model = context.Store.ReviewItems.Lookup(_id);
            if (model.HasValue)
            {
                Load(model.Value);
            }
            else
            {
                IsDeletedElsewhere = true;
                SaveError = context.Strings.GetString("Reviews_DeletedElsewhere");
            }
        }

        _baseSignature = Signature();
        _commits = context.Store.Committed.ObserveOn(context.UiScheduler).Subscribe(_ => OnCommitted());
    }

    public event EventHandler? CloseRequested;

    /// <summary>Asks the view to focus a field key such as <c>Title</c>.</summary>
    public event EventHandler<string>? FocusRequested;

    public bool IsNew { get; }

    public ReviewEditorFollowUp FollowUp { get; private set; }

    public TokenPickerViewModel Developer { get; }

    public TokenPickerViewModel Services { get; }

    public string DialogTitle => _context.Strings.GetString(IsNew ? "ReviewEditor_TitleNew" : "ReviewEditor_Title");

    public string PrimaryButtonText => _context.Strings.GetString(IsNew ? "ReviewEditor_CreateButton" : "TaskEditor_SaveButton");

    public string CloseButtonText => _context.Strings.GetString("Common_Cancel");

    public string Caption => _base is { } b ? _context.Strings.Format("TaskEditor_CreatedCaption", b.CreatedAtUtc.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)) : string.Empty;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchingCrId), nameof(HasMatchingCr), nameof(OpenCrText))]
    public partial string CrNumber { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TaskNumber { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Comments { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DoneCaption))]
    public partial bool IsDone { get; set; }

    [ObservableProperty]
    public partial string? TitleError { get; private set; }

    [ObservableProperty]
    public partial string? CrNumberError { get; private set; }

    [ObservableProperty]
    public partial string? TaskNumberError { get; private set; }

    [ObservableProperty]
    public partial string? ServicesError { get; private set; }

    [ObservableProperty]
    public partial string? DeveloperError { get; private set; }

    [ObservableProperty]
    public partial string? CommentsError { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValidationSummary))]
    public partial string? ValidationSummary { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSaveError))]
    public partial string? SaveError { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave), nameof(CanEditFields), nameof(CanDelete))]
    public partial bool IsSaving { get; private set; }

    [ObservableProperty]
    public partial bool IsDirty { get; private set; }

    [ObservableProperty]
    public partial bool IsDirtyClosePromptOpen { get; private set; }

    /// <summary>T-05: the review changed since this editor loaded it; Reload replaces the draft with the saved values.</summary>
    [ObservableProperty]
    public partial bool IsConflict { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave), nameof(CanEditFields), nameof(CanDelete))]
    public partial bool IsDeletedElsewhere { get; private set; }

    public bool HasValidationSummary => ValidationSummary is not null;

    public bool HasSaveError => SaveError is not null;

    public bool CanSave => !IsSaving && !IsDeletedElsewhere;

    public bool CanEditFields => !IsSaving && !IsDeletedElsewhere;

    public bool CanDelete => !IsNew && !IsSaving && !IsDeletedElsewhere;

    /// <summary>"Completed on Oct 20, 2026" while Done is checked (design §5.4); today for a newly checked box.</summary>
    public string DoneCaption => !IsDone
        ? string.Empty
        : _context.Strings.Format("ReviewEditor_CompletedOn", (_base?.CompletedOn ?? _context.Store.Today).ToString("D", CultureInfo.CurrentCulture));

    /// <summary>
    /// B-17.3: the CR whose normalized number equals the typed text, for the "Open CR" affordance only. The review
    /// never stores a link or identity.
    /// </summary>
    public ChangeRequestId? MatchingCrId =>
        Domain.Values.CrNumber.Create(CrNumber) is { IsSuccess: true, Value: { } number }
            ? _context.Store.ChangeRequests.Items.FirstOrDefault(cr => cr.NormalizedNumber == number.Normalized)?.Id
            : null;

    public bool HasMatchingCr => MatchingCrId is not null;

    public string OpenCrText => MatchingCrId is { } id && _context.Store.ChangeRequests.Lookup(id) is { HasValue: true } cr
        ? _context.Strings.Format("CrEditor_OpenDuplicate", cr.Value.Number)
        : string.Empty;

    public async Task<bool> SaveAsync()
    {
        if (!CanSave)
        {
            return false;
        }

        // Text typed in the Developer box counts even when Save comes from Ctrl+Enter, before the box loses focus.
        Developer.CommitText();

        if (!IsNew && !IsDirty)
        {
            Approve();
            return true;
        }

        if (_context.IsEditingBlocked)
        {
            SaveError = _context.Strings.GetString("Error_EditingBlocked");
            return false;
        }

        ClearErrors();
        var operation = _retryOperation ?? OperationId.New();
        IsSaving = true;
        IsDirtyClosePromptOpen = false;
        _context.SaveStatus.Begin();
        var developerToken = Developer.Tokens.FirstOrDefault();
        var newDeveloper = developerToken is { IsNew: true } typed ? new NewPerson(new PersonId(typed.Id), typed.Name) : null;
        var draft = new ReviewDraft(Title, CrNumber, TaskNumber, [.. Services.Tokens.Select(t => t.Name)], developerToken is { } d ? new PersonId(d.Id) : null, Comments);
        var result = IsNew
            ? await _context.UseCases.CreateReview.ExecuteAsync(new CreateReviewCommand(operation, _id, draft, newDeveloper, IsDone), CancellationToken.None).ConfigureAwait(true)
            : await _context.UseCases.UpdateReview.ExecuteAsync(new UpdateReviewCommand(operation, _id, _base!.Revision, draft, newDeveloper, IsDone), CancellationToken.None).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            var failure = CommandFailure.From(result.Error!, operation, "Record_Review", _context.Strings);
            _context.SaveStatus.Report(failure);

            // T-05: only an unknown outcome is retried under the same operation; anything else is a new intent.
            _retryOperation = failure.RequiresSameOperationOnRetry ? operation : null;
            IsSaving = false;
            ShowFailure(failure);
            return false;
        }

        _retryOperation = null;
        _context.SaveStatus.Complete();
        await _context.WhenDisplayedAsync(result.Value.CommitSequence, DisplayTimeout).ConfigureAwait(true);
        IsSaving = false;
        _context.Announce(_context.Strings.Format(IsNew ? "Reviews_Created" : "Reviews_Saved", Title.Trim()));
        Approve();
        return true;
    }

    /// <summary>"Open CR CR-1042" beside the CR number (B-17.3): the editor closes (after the dirty prompt), then the CR opens.</summary>
    public void OpenMatchingCr()
    {
        if (HasMatchingCr)
        {
            RequestFollowUp(ReviewEditorFollowUp.OpenCr);
        }
    }

    /// <summary>More › Delete…: the editor closes first, then the confirmation runs (design §5.4, §5.7).</summary>
    public void RequestDelete()
    {
        if (CanDelete)
        {
            RequestFollowUp(ReviewEditorFollowUp.Delete);
        }
    }

    /// <summary>Esc, Cancel, an outside click or a follow-up: a dirty draft asks for the Save / Discard / Continue editing confirmation (design §5.8).</summary>
    public bool TryClose()
    {
        if (_closeApproved)
        {
            return true;
        }

        if (IsSaving)
        {
            return false;
        }

        if (IsDirty && !IsDeletedElsewhere)
        {
            IsDirtyClosePromptOpen = true;
            return false;
        }

        _closeApproved = true;
        return true;
    }

    public async Task<bool> SaveFromPromptAsync()
    {
        IsDirtyClosePromptOpen = false;
        var saved = await SaveAsync().ConfigureAwait(true);
        if (!saved)
        {
            // A failed save keeps the editor open; the window close the prompt answered is abandoned.
            IsWindowClosePending = false;
        }

        return saved;
    }

    /// <inheritdoc/>
    public bool IsWindowClosePending { get; private set; }

    /// <inheritdoc/>
    public bool TryCloseForWindow()
    {
        if (TryClose())
        {
            return true;
        }

        IsWindowClosePending = IsDirtyClosePromptOpen;
        return false;
    }

    public void DiscardChanges()
    {
        IsDirtyClosePromptOpen = false;
        Approve();
    }

    public void ContinueEditing()
    {
        IsDirtyClosePromptOpen = false;
        IsWindowClosePending = false;
        FollowUp = ReviewEditorFollowUp.None;
    }

    /// <summary>After a conflict: replaces the draft with the saved review (the user chose to discard their draft).</summary>
    public void Reload()
    {
        if (_context.Store.ReviewItems.Lookup(_id) is { HasValue: true } latest)
        {
            Load(latest.Value);
            _baseSignature = Signature();
            RecomputeDirty();
        }

        IsConflict = false;
        SaveError = null;
    }

    public void Dispose()
    {
        _commits.Dispose();
        Developer.Changed -= OnPickerChanged;
        Services.Changed -= OnPickerChanged;
    }

    partial void OnTitleChanged(string value) => RecomputeDirty();

    partial void OnCrNumberChanged(string value) => RecomputeDirty();

    partial void OnTaskNumberChanged(string value) => RecomputeDirty();

    partial void OnCommentsChanged(string value) => RecomputeDirty();

    partial void OnIsDoneChanged(bool value) => RecomputeDirty();

    partial void OnIsSavingChanged(bool value)
    {
        Developer.IsReadOnly = value || IsDeletedElsewhere;
        Services.IsReadOnly = value || IsDeletedElsewhere;
    }

    private void OnPickerChanged(object? sender, EventArgs e) => RecomputeDirty();

    private void OnCommitted()
    {
        if (IsNew || IsSaving)
        {
            return;
        }

        var latest = _context.Store.ReviewItems.Lookup(_id);
        if (!latest.HasValue)
        {
            IsDeletedElsewhere = true;
            SaveError = _context.Strings.GetString("Reviews_DeletedElsewhere");
            return;
        }

        if (_base is not null && latest.Value.Revision != _base.Revision && !IsDirty)
        {
            // Follows committed changes while the draft is clean (design §7.7).
            Load(latest.Value);
            _baseSignature = Signature();
        }

        OnPropertyChanged(nameof(MatchingCrId));
        OnPropertyChanged(nameof(HasMatchingCr));
        OnPropertyChanged(nameof(OpenCrText));
    }

    private void Load(ReviewItem review)
    {
        _isLoading = true;
        _base = review;
        Title = review.Content.Title.Value;
        CrNumber = review.Content.CrNumberText?.Value ?? string.Empty;
        TaskNumber = review.Content.TaskNumberText?.Value ?? string.Empty;
        Comments = review.Content.Comments?.Value ?? string.Empty;
        IsDone = review.IsDone;
        Services.Load(review.Content.Services.Select(s => (_services.IdFor(s.Value), s.Value, false)));
        var workflow = _context.Store.CurrentWorkflow;
        Developer.Load(review.Content.DeveloperId is { } id && workflow?.FindPerson(id) is { } person
            ? [(person.Id.Value, person.Name.Display, false)]
            : []);
        _isLoading = false;
        OnPropertyChanged(nameof(Caption));
        OnPropertyChanged(nameof(DoneCaption));
    }

    private string Signature() => string.Join(
        "\u001F",
        Title,
        CrNumber,
        TaskNumber,
        Comments,
        IsDone,
        string.Join("\u001E", Services.Tokens.Select(t => t.Name)),
        string.Join("\u001E", Developer.Tokens.Select(t => t.Id)));

    private void RecomputeDirty()
    {
        if (!_isLoading)
        {
            IsDirty = Signature() != _baseSignature;
        }
    }

    private void ClearErrors()
    {
        TitleError = CrNumberError = TaskNumberError = ServicesError = DeveloperError = CommentsError = null;
        ValidationSummary = null;
        SaveError = null;
    }

    // Design §5.0: errors sit under their fields, the summary says how many, and focus goes to the first one.
    private void ShowFailure(CommandFailure failure)
    {
        if (failure.Error is ValidationFailedError validation)
        {
            string? first = null;
            foreach (var error in validation.Errors)
            {
                var text = CommandErrorText.Describe(error, _context.Strings);
                switch (error.Field)
                {
                    case "Title":
                        TitleError ??= text;
                        break;
                    case "CrNumber":
                        CrNumberError ??= text;
                        break;
                    case "TaskNumber":
                        TaskNumberError ??= text;
                        break;
                    case "Services":
                        ServicesError ??= text;
                        break;
                    case "Developer":
                        DeveloperError ??= text;
                        break;
                    case "Comments":
                        CommentsError ??= text;
                        break;
                    default:
                        SaveError ??= text;
                        break;
                }

                first ??= error.Field;
            }

            var count = validation.Errors.Select(e => e.Field).Distinct(StringComparer.Ordinal).Count();
            ValidationSummary = count == 1 ? _context.Strings.GetString("TaskEditor_FixField") : _context.Strings.Format("TaskEditor_FixFields", count);
            _context.Announce(ValidationSummary, isFailure: true);
            FocusRequested?.Invoke(this, first ?? "Title");
            return;
        }

        IsConflict = failure.IsConflict;
        SaveError = failure.Message;
        _context.Announce(failure.Message, isFailure: true);
    }

    private void RequestFollowUp(ReviewEditorFollowUp followUp)
    {
        if (IsSaving)
        {
            return;
        }

        FollowUp = followUp;
        if (TryClose())
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Approve()
    {
        _closeApproved = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
