namespace MaxPlanner.Domain.Tasks;

/// <summary>
/// Content kept for lossless export and backup but never shown or edited: the task's own title from
/// before it was linked (ADR 0005), CR references not chosen during import (B-13.6), and unknown
/// imported fields as raw JSON (T-05). Tasks and CRs both carry it; a CR is never linked, so its
/// <see cref="PreLinkTitle"/> is always null.
/// </summary>
public sealed record PreservedMetadata(string? PreLinkTitle, IReadOnlyList<string> UnselectedCrReferences, string? LegacyJson)
{
    public static PreservedMetadata Empty { get; } = new(null, [], null);
}
