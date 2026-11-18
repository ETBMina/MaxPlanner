using System.Reflection;
using MaxPlanner.Presentation.Common.Services;

namespace MaxPlanner.Presentation.Shell;

/// <summary>
/// The app version as the startup splash shows it (design §2.1). The build stamps the informational version
/// (<c>-p:Version=…</c> in <c>publish-portable.ps1</c>, otherwise <c>Directory.Build.props</c>) and the SDK appends
/// <c>+commit</c>; the splash shows only the version itself.
/// </summary>
internal static class AppVersionText
{
    /// <summary>The informational version of the running app (the Desktop EXE), or null when it has none.</summary>
    public static string? EntryInformationalVersion =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    /// <summary>"0.1.0+abc123" becomes "0.1.0"; a pre-release label stays; a missing version is empty.</summary>
    public static string ToDisplay(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return string.Empty;
        }

        var metadataStart = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        var version = metadataStart >= 0 ? informationalVersion[..metadataStart] : informationalVersion;
        return version.Trim();
    }

    /// <summary>"Version 0.1.0" from the resources, or empty when there is no version to show.</summary>
    public static string ToVersionLine(ILocalizedStrings strings, string? informationalVersion)
    {
        ArgumentNullException.ThrowIfNull(strings);
        var version = ToDisplay(informationalVersion);
        return version.Length == 0 ? string.Empty : strings.Format("Shell_StartupVersion", version);
    }
}
