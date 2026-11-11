using MaxPlanner.Application.Preferences;

namespace MaxPlanner.Presentation.Workspace;

/// <summary>UI preferences kept in memory only, for a workspace without a preferences file (tests).</summary>
internal sealed class InMemoryUiPreferences : IUiPreferencesStore
{
    public UiPreferences Current { get; private set; } = UiPreferences.Default;

    public PreferencesLoadResult Load() => new(Current, PreferencesRecovery.None);

    public void Update(Func<UiPreferences, UiPreferences> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Current = change(Current);
    }

    public Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
