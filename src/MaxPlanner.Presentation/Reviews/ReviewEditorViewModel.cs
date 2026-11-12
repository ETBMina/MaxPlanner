using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Lookups;
using MaxPlanner.Application.Reviews;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Reviews;
using MaxPlanner.Domain.Values;
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
/// <remarks>
/// Unlike the other editors it has no inline steps: a conflict offers <see cref="Reload"/> directly, and its failure
/// display is its own (<see cref="ShowFailure"/>).
/// </remarks>
public sealed partial class ReviewEditorViewModel : EditorViewModelBase<ReviewEditorFollowUp>
{
    private readonly NewNameRegistry _newPeople = new();
    private readonly NewNameRegistry _services = new();
    private readonly ReviewItemId _id;
    private ReviewItem? _base;
    private string _baseSignature = string.Empty;

    /// <param name="reviewId">The review to edit, or null for a new review.</param>
    public ReviewEditorViewModel(WorkspaceContext context, ReviewItemId? reviewId)
        : base(context, isNew: reviewId is null, "Record_Review")
    {
        var workflow = context.Store.CurrentWorkflow
            ?? throw new InvalidOperationException("The review editor needs the loaded workflow; editing is blocked until the store is ready.");
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
        ObserveCommits();
    }

    public TokenPickerViewModel Developer { get; }

    public TokenPickerViewModel Services { get; }

    public string DialogTitle => Context.Strings.GetString(IsNew ? "ReviewEditor_TitleNew" : "ReviewEditor_Title");

    public string PrimaryButtonText => Context.Strings.GetString(IsNew ? "ReviewEditor_CreateButton" : "TaskEditor_SaveButton");

    public string Caption => _base is { } b ? Context.Strings.Format("TaskEditor_CreatedCaption", b.CreatedAtUtc.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)) : string.Empty;

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

    public override bool CanSave => !IsSaving && !IsDeletedElsewhere;

    public bool CanEditFields => !IsSaving && !IsDeletedElsewhere;

    public bool CanDelete => !IsNew && !IsSaving && !IsDeletedElsewhere;

    /// <summary>"Completed on Oct 20, 2026" while Done is checked (design §5.4); today for a newly checked box.</summary>
    public string DoneCaption => !IsDone
        ? string.Empty
        : Context.Strings.Format("ReviewEditor_CompletedOn", (_base?.CompletedOn ?? Context.Store.Today).ToString("D", CultureInfo.CurrentCulture));

    /// <summary>
    /// B-17.3: the CR whose normalized number equals the typed text, for the "Open CR" affordance only. The review
    /// never stores a link or identity.
    /// </summary>
    public ChangeRequestId? MatchingCrId =>
        Domain.Values.CrNumber.Create(CrNumber) is { IsSuccess: true, Value: { } number }
            ? Context.Store.ChangeRequests.Items.FirstOrDefault(cr => cr.NormalizedNumber == number.Normalized)?.Id
            : null;

    public bool HasMatchingCr => MatchingCrId is not null;

    public string OpenCrText => MatchingCrId is { } id && Context.Store.ChangeRequests.Lookup(id) is { HasValue: true } cr
        ? Context.Strings.Format("CrEditor_OpenDuplicate", cr.Value.Number)
        : string.Empty;

    protected override bool NeedsClosePrompt => IsDirty && !IsDeletedElsewhere;

    /// <summary>"Open CR CR-1042" beside the CR number (B-17.3): the editor closes (after the dirty prompt), then the CR opens.</summary>
    [RelayCommand]
    public void OpenMatchingCr()
    {
        if (HasMatchingCr)
        {
            CloseForFollowUp(ReviewEditorFollowUp.OpenCr);
        }
    }

    /// <summary>More › Delete…: the editor closes first, then the confirmation runs (design §5.4, §5.7).</summary>
    [RelayCommand]
    public void RequestDelete()
    {
        if (CanDelete)
        {
            CloseForFollowUp(ReviewEditorFollowUp.Delete);
        }
    }

    /// <summary>After a conflict: replaces the draft with the saved review (the user chose to discard their draft).</summary>
    [RelayCommand]
    public void Reload()
    {
        if (Context.Store.ReviewItems.Lookup(_id) is { HasValue: true } latest)
        {
            Load(latest.Value);
            _baseSignature = Signature();
            RecomputeDirty();
        }

        EndConflict();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            Developer.Changed -= OnPickerChanged;
            Services.Changed -= OnPickerChanged;
        }
    }

    // Text typed in the Developer box counts even when Save comes from Ctrl+Enter, before the box loses focus.
    protected override void CommitPendingInput() => Developer.CommitText();

    protected override async Task<(AppError? Error, long Sequence)> ExecuteSaveAsync(OperationId operation)
    {
        var developerToken = Developer.Tokens.FirstOrDefault();
        var newDeveloper = developerToken is { IsNew: true } typed ? new NewPerson(new PersonId(typed.Id), typed.Name) : null;
        var draft = new ReviewDraft(Title, CrNumber, TaskNumber, [.. Services.Tokens.Select(t => t.Name)], developerToken is { } d ? new PersonId(d.Id) : null, Comments);
        var result = IsNew
            ? await Context.UseCases.CreateReview.ExecuteAsync(new CreateReviewCommand(operation, _id, draft, newDeveloper, IsDone), CancellationToken.None).ConfigureAwait(true)
            : await Context.UseCases.UpdateReview.ExecuteAsync(new UpdateReviewCommand(operation, _id, _base!.Revision, draft, newDeveloper, IsDone), CancellationToken.None).ConfigureAwait(true);
        return result.IsSuccess ? (null, result.Value.CommitSequence) : (result.Error, 0);
    }

    protected override void OnSaved() => Context.Announce(Context.Strings.Format(IsNew ? "Reviews_Created" : "Reviews_Saved", Title.Trim()));

    protected override void ClearFieldErrors() => TitleError = CrNumberError = TaskNumberError = ServicesError = DeveloperError = CommentsError = null;

    /// <summary>
    /// The review's own failure display: validation announces the summary rather than the failure text, and any other
    /// failure sets <see cref="EditorViewModelBase{TFollowUp}.IsConflict"/> from the failure (so a later non-conflict
    /// failure clears it) and shows the failure text.
    /// </summary>
    protected override void ShowFailure(CommandFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (failure.Error is ValidationFailedError validation)
        {
            ShowFieldErrors(validation.Errors);
            return;
        }

        IsConflict = failure.IsConflict;
        SaveError = failure.Message;
        Context.Announce(failure.Message, isFailure: true);
    }

    // Design §5.0: errors sit under their fields, the summary says how many, and focus goes to the first one.
    protected override void ShowFieldErrors(IReadOnlyList<DomainError> errors)
    {
        string? first = null;
        foreach (var error in errors)
        {
            var text = CommandErrorText.Describe(error, Context.Strings);
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

        var count = errors.Select(e => e.Field).Distinct(StringComparer.Ordinal).Count();
        ValidationSummary = count == 1 ? Context.Strings.GetString("TaskEditor_FixField") : Context.Strings.Format("TaskEditor_FixFields", count);
        Context.Announce(ValidationSummary, isFailure: true);
        RequestFocus(first ?? "Title");
    }

    protected override bool ComputeIsDirty() => Signature() != _baseSignature;

    protected override void OnSavingChanged()
    {
        Developer.IsReadOnly = IsSaving || IsDeletedElsewhere;
        Services.IsReadOnly = IsSaving || IsDeletedElsewhere;
        RaiseCommandState();
    }

    protected override void OnDeletedElsewhereChanged() => RaiseCommandState();

    protected override void OnCommitted()
    {
        if (IsNew || IsSaving)
        {
            return;
        }

        var latest = Context.Store.ReviewItems.Lookup(_id);
        if (!latest.HasValue)
        {
            IsDeletedElsewhere = true;
            SaveError = Context.Strings.GetString("Reviews_DeletedElsewhere");
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

    partial void OnTitleChanged(string value) => RecomputeDirty();

    partial void OnCrNumberChanged(string value) => RecomputeDirty();

    partial void OnTaskNumberChanged(string value) => RecomputeDirty();

    partial void OnCommentsChanged(string value) => RecomputeDirty();

    partial void OnIsDoneChanged(bool value) => RecomputeDirty();

    private void OnPickerChanged(object? sender, EventArgs e) => RecomputeDirty();

    private void RaiseCommandState()
    {
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanEditFields));
        OnPropertyChanged(nameof(CanDelete));
    }

    private void Load(ReviewItem review)
    {
        IsLoading = true;
        _base = review;
        Title = review.Content.Title.Value;
        CrNumber = review.Content.CrNumberText?.Value ?? string.Empty;
        TaskNumber = review.Content.TaskNumberText?.Value ?? string.Empty;
        Comments = review.Content.Comments?.Value ?? string.Empty;
        IsDone = review.IsDone;
        Services.Load(review.Content.Services.Select(s => (_services.IdFor(s.Value), s.Value, false)));
        var workflow = Context.Store.CurrentWorkflow;
        Developer.Load(review.Content.DeveloperId is { } id && workflow?.FindPerson(id) is { } person
            ? [(person.Id.Value, person.Name.Display, false)]
            : []);
        IsLoading = false;
        OnPropertyChanged(nameof(Caption));
        OnPropertyChanged(nameof(DoneCaption));
    }

    private string Signature() => string.Join(
        "\u001F",
        Title,
        CrNumber,
        TaskNumber,
        LineBreaks.Normalize(Comments),
        IsDone,
        string.Join("\u001E", Services.Tokens.Select(t => t.Name)),
        string.Join("\u001E", Developer.Tokens.Select(t => t.Id)));
}
