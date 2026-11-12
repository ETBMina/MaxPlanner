namespace MaxPlanner.Presentation.Common.Pickers;

/// <summary>A suggestion row: an existing name, or "Add 'name'" for a new one. A person row shows the avatar.</summary>
public sealed record TokenSuggestion(Guid? ExistingId, string Name, string Display, bool IsPerson = false, bool IsTag = false)
{
    /// <summary>An existing person: the row shows their avatar ("Add 'name'" rows show the Add glyph instead).</summary>
    public bool ShowsAvatar => IsPerson && ExistingId is not null;

    /// <summary>An existing tag: the row shows it as a pill in the tag's color ("Add 'name'" rows stay text).</summary>
    public bool ShowsPill => IsTag && ExistingId is not null;

    /// <summary>The pill's palette key; empty for a row without a pill.</summary>
    public Guid PillId => ExistingId ?? Guid.Empty;

    public bool IsAddNew => ExistingId is null;

    public override string ToString() => Display;
}
