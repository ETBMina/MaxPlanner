using CommunityToolkit.Mvvm.ComponentModel;

namespace MaxPlanner.Presentation.Common.Dialogs;

/// <summary>One row of the conflict review step (design §5.10): Field | Your draft | Saved now, with Use mine.</summary>
public sealed partial class ConflictRowViewModel : ObservableObject
{
    public ConflictRowViewModel(string key, string field, string mine, string saved)
    {
        Key = key;
        Field = field;
        Mine = mine;
        Saved = saved;
    }

    /// <summary>The draft field, for example <c>Title</c>; stable, never display text.</summary>
    public string Key { get; }

    public string Field { get; }

    public string Mine { get; }

    public string Saved { get; }

    /// <summary>Keeps the draft's value when the draft is refreshed; off takes the saved value.</summary>
    [ObservableProperty]
    public partial bool UseMine { get; set; } = true;
}
