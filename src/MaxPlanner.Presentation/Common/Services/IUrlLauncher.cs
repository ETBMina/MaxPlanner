namespace MaxPlanner.Presentation.Common.Services;

/// <summary>
/// Opens a user-activated design link in the default browser (B-02.11). Implementations open only absolute
/// http/https URIs and refuse every other scheme, so an imported value can never launch a program or file.
/// </summary>
public interface IUrlLauncher
{
    /// <returns>False when the URI was refused or could not be opened.</returns>
    Task<bool> OpenAsync(Uri uri);
}

/// <summary>Used where no window can open a browser (tests, closed sessions); it opens nothing.</summary>
public sealed class NoUrlLauncher : IUrlLauncher
{
    public static NoUrlLauncher Instance { get; } = new();

    public Task<bool> OpenAsync(Uri uri) => Task.FromResult(false);
}
