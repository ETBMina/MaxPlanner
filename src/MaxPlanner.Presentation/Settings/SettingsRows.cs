using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Workflow;

namespace MaxPlanner.Presentation.Settings;

/// <summary>
/// One configurable lookup value in Settings (design §4.6 workflow rows): its name, flag chips as text, usage
/// count, and the state of the inline rename. Rows are updated in place when the workflow changes, so a focused
/// row keeps its container (T-07).
/// </summary>
public sealed partial class LookupRowViewModel : ObservableObject
{
    public LookupRowViewModel(LookupKind kind, Guid id, string automationKey)
    {
        Kind = kind;
        Id = id;
        AutomationId = $"Settings_{automationKey}Row_{id:N}";
        MenuAutomationId = $"Settings_{automationKey}RowMenu_{id:N}";
        RenameAutomationId = $"Settings_{automationKey}RenameBox_{id:N}";
    }

    public LookupKind Kind { get; }

    /// <summary>A People row shows the person's avatar before the name.</summary>
    public bool IsPerson => Kind == LookupKind.Person;

    public Guid Id { get; }

    public string AutomationId { get; }

    public string MenuAutomationId { get; }

    public string RenameAutomationId { get; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    /// <summary>"Default", "Backlog", "Sets start date", … shown as text chips (design §4.6: never color alone).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> Flags { get; set; } = [];

    /// <summary>"12 tasks · 3 Subtask cards", or "Not used".</summary>
    [ObservableProperty]
    public partial string UsageText { get; set; } = string.Empty;

    /// <summary>The row's accessible name: name, flags, and usage.</summary>
    [ObservableProperty]
    public partial string AccessibleName { get; set; } = string.Empty;

    /// <summary>"More actions for Review" (design §8.2).</summary>
    [ObservableProperty]
    public partial string MenuName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsCompleted { get; set; }

    [ObservableProperty]
    public partial bool SetsStartDate { get; set; }

    [ObservableProperty]
    public partial bool IsBacklog { get; set; }

    /// <summary>The default for new records of its kind (task status, CR status, or priority).</summary>
    [ObservableProperty]
    public partial bool IsDefault { get; set; }

    [ObservableProperty]
    public partial bool CanMoveUp { get; set; }

    [ObservableProperty]
    public partial bool CanMoveDown { get; set; }

    /// <summary>The concurrency stamp of the value as shown (ADR 0022).</summary>
    public string Stamp { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotRenaming))]
    public partial bool IsRenaming { get; set; }

    public bool IsNotRenaming => !IsRenaming;

    [ObservableProperty]
    public partial string RenameText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? RenameError { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>Rename (inline, design §4.6). A rename changes the label only, never the identity (B-04).</summary>
    public void BeginRename()
    {
        RenameText = Name;
        RenameError = null;
        IsRenaming = true;
    }

    /// <summary>Esc: closes the inline rename without saving; ignored while the rename command runs.</summary>
    public void CancelRename()
    {
        if (!IsBusy)
        {
            IsRenaming = false;
            RenameError = null;
        }
    }
}

/// <summary>One Settings expander of lookup values: task statuses, CR statuses, priorities, squads, people, or tags.</summary>
public sealed partial class LookupSectionViewModel : ObservableObject
{
    public LookupSectionViewModel(LookupKind kind, string automationKey, string title, string glyph)
    {
        Kind = kind;
        AutomationKey = automationKey;
        Title = title;
        Glyph = glyph;
        Rows = new ReadOnlyObservableCollection<LookupRowViewModel>(MutableRows);
    }

    public LookupKind Kind { get; }

    /// <summary>The design §8.1 key, for example <c>TaskStatuses</c>.</summary>
    public string AutomationKey { get; }

    public string ExpanderAutomationId => $"Settings_{AutomationKey}Expander";

    public string AddBoxAutomationId => $"Settings_{AutomationKey}AddBox";

    public string AddButtonAutomationId => $"Settings_{AutomationKey}AddButton";

    public string Title { get; }

    public string Glyph { get; }

    public bool IsOrdered => Application.Settings.WorkflowLookups.IsOrdered(Kind);

    public bool HasStatusFlags => Kind is LookupKind.TaskStatus or LookupKind.CrStatus;

    public ReadOnlyObservableCollection<LookupRowViewModel> Rows { get; }

    internal ObservableCollection<LookupRowViewModel> MutableRows { get; } = [];

    /// <summary>"8 statuses · names must be unique".</summary>
    [ObservableProperty]
    public partial string Description { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AddText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? AddError { get; set; }

    [ObservableProperty]
    public partial bool IsAdding { get; set; }

    /// <summary>The configured-order stamp of the list as shown (ADR 0022).</summary>
    public string OrderStamp { get; set; } = string.Empty;
}

/// <summary>A custom field row (design §4.6): label, kind, required and default chips, and its ⋯ menu.</summary>
public sealed partial class FieldRowViewModel : ObservableObject
{
    public FieldRowViewModel(FieldId id, string automationKey)
    {
        Id = id;
        AutomationId = $"Settings_{automationKey}Row_{id.Value:N}";
        MenuAutomationId = $"Settings_{automationKey}RowMenu_{id.Value:N}";
    }

    public FieldId Id { get; }

    public string AutomationId { get; }

    public string MenuAutomationId { get; }

    [ObservableProperty]
    public partial string Label { get; set; } = string.Empty;

    /// <summary>"Number · key risk_score".</summary>
    [ObservableProperty]
    public partial string Details { get; set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<string> Flags { get; set; } = [];

    [ObservableProperty]
    public partial string AccessibleName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MenuName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool CanMoveUp { get; set; }

    [ObservableProperty]
    public partial bool CanMoveDown { get; set; }

    public string Stamp { get; set; } = string.Empty;
}

/// <summary>The custom fields expander of one target (tasks or CRs).</summary>
public sealed partial class FieldSectionViewModel : ObservableObject
{
    public FieldSectionViewModel(FieldTarget target, string automationKey, string title)
    {
        Target = target;
        AutomationKey = automationKey;
        Title = title;
        Rows = new ReadOnlyObservableCollection<FieldRowViewModel>(MutableRows);
    }

    public FieldTarget Target { get; }

    public string AutomationKey { get; }

    public string ExpanderAutomationId => $"Settings_{AutomationKey}Expander";

    public string AddButtonAutomationId => $"Settings_{AutomationKey}AddButton";

    public string Title { get; }

    public ReadOnlyObservableCollection<FieldRowViewModel> Rows { get; }

    internal ObservableCollection<FieldRowViewModel> MutableRows { get; } = [];

    [ObservableProperty]
    public partial string Description { get; set; } = string.Empty;

    public string OrderStamp { get; set; } = string.Empty;
}

/// <summary>A ComboBox choice in Settings › Defaults; a null <see cref="Id"/> is "None".</summary>
public sealed record SettingsChoice(Guid? Id, string Name)
{
    /// <summary>A person choice (not "None"); the owner picker shows its avatar.</summary>
    public bool IsPerson => Id is not null;

    public override string ToString() => Name;
}
