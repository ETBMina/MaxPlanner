namespace PlatformControls.Settings;

/// <summary>Sample lookup row for the Task statuses expander (design.md §4.6 workflow rows).</summary>
public sealed class StatusRow(string key, string name, IReadOnlyList<string> flags, string countText)
{
    public string Key { get; } = key;

    public string Name { get; } = name;

    public IReadOnlyList<string> Flags { get; } = flags;

    public string CountText { get; } = countText;

    public string RowAutomationId => $"Settings_TaskStatusesRow_{Key}";

    public string MenuAutomationId => $"Settings_TaskStatusesRowMenu_{Key}";

    public string MenuName => $"More actions for status {Name}";

    public static IReadOnlyList<StatusRow> CreateSample() =>
    [
        new("backlog", "Backlog", ["Default", "Backlog"], "41 tasks · 0 Subtask cards"),
        new("todo", "To do", [], "18 tasks · 2 Subtask cards"),
        new("inprogress", "In progress", ["Sets start date"], "12 tasks · 3 Subtask cards"),
        new("blocked", "Blocked", [], "2 tasks · 0 Subtask cards"),
        new("review", "Review", [], "6 tasks · 1 Subtask card"),
        new("qa", "QA", [], "4 tasks · 0 Subtask cards"),
        new("done", "Done", ["Completed · sets end date"], "137 tasks · 22 Subtask cards"),
        new("cancelled", "Cancelled", ["Completed · sets end date"], "9 tasks · 0 Subtask cards"),
    ];
}
