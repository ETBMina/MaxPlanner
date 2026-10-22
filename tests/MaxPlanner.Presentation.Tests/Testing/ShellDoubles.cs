using System.Globalization;
using MaxPlanner.Application.Preferences;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Shell;

namespace MaxPlanner.Presentation.Tests.Testing;

/// <summary>Returns each key as its own text, or a template the test registered for it.</summary>
internal sealed class FakeStrings : ILocalizedStrings
{
    private readonly Dictionary<string, string> _templates = new(StringComparer.Ordinal);

    public FakeStrings With(string key, string template)
    {
        _templates[key] = template;
        return this;
    }

    public string GetString(string key) => _templates.GetValueOrDefault(key, key);

    public static string Format(string template, params object?[] args) => string.Format(CultureInfo.InvariantCulture, template, args);
}

internal sealed class FakeDataFolder : IDataFolderLauncher
{
    public string DataDirectory => @"C:\Max Planner Tëst\data";

    public int OpenCount { get; private set; }

    public void OpenDataFolder() => OpenCount++;
}

internal sealed class FakeRecoveryActions : IRecoveryActions
{
    public CreateDatasetOutcome NextCreateOutcome { get; set; } = CreateDatasetOutcome.Created;

    public int CreateCount { get; private set; }

    public int RetryCount { get; private set; }

    public int CloseCount { get; private set; }

    public Task<CreateDatasetOutcome> CreateNewDatasetAsync()
    {
        CreateCount++;
        return Task.FromResult(NextCreateOutcome);
    }

    public Task RetryOpenAsync()
    {
        RetryCount++;
        return Task.CompletedTask;
    }

    public void CloseApp() => CloseCount++;
}

/// <summary>Preferences kept in memory; <see cref="Updates"/> counts saves the app would schedule.</summary>
internal sealed class FakePreferencesStore(UiPreferences? initial = null) : IUiPreferencesStore
{
    public UiPreferences Current { get; private set; } = initial ?? UiPreferences.Default;

    public int Updates { get; private set; }

    public PreferencesLoadResult Load() => new(Current, PreferencesRecovery.None);

    public void Update(Func<UiPreferences, UiPreferences> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Current = change(Current);
        Updates++;
    }

    public Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
