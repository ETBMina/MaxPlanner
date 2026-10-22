using Microsoft.Windows.ApplicationModel.Resources;

namespace MaxPlanner.Presentation.Common.Services;

/// <summary>The production <see cref="ILocalizedStrings"/> over the Windows App SDK resource loader.</summary>
public sealed class ResourceStrings : ILocalizedStrings
{
    // ADR 0014: this library's Resources.resw is merged into the app's PRI under its own resource map.
    private const string ResourceMap = "MaxPlanner.Presentation/Resources";

    private readonly ResourceLoader _loader = new(ResourceLoader.GetDefaultResourceFilePath(), ResourceMap);

    public string GetString(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var value = _loader.GetString(key);
        return string.IsNullOrEmpty(value) ? key : value;
    }
}
