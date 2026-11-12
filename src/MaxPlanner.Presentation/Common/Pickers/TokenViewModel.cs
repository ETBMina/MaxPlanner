namespace MaxPlanner.Presentation.Common.Pickers;

/// <summary>A chip in a token picker (design §5.0); <see cref="IsNew"/> names one that is created only on Save (B-02.6).</summary>
public sealed class TokenViewModel(Guid id, string name, bool isNew, string removeName, bool isPerson = false, bool isTag = false)
{
    public Guid Id { get; } = id;

    public string Name { get; } = name;

    public bool IsNew { get; } = isNew;

    /// <summary>"Remove Alice": the chip button's accessible name.</summary>
    public string RemoveName { get; } = removeName;

    /// <summary>A person chip shows the person's avatar before the name.</summary>
    public bool IsPerson { get; } = isPerson;

    /// <summary>A tag chip is a pill in the tag's palette color (design §6.1).</summary>
    public bool IsTag { get; } = isTag;
}
