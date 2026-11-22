using MaxPlanner.Presentation.Common;

namespace MaxPlanner.Presentation.Settings;

/// <summary>
/// The values of Settings › About (design §4.6). They are facts about the app, not text to translate; the labels
/// beside them are in the resources.
/// </summary>
public static class AboutInfo
{
    /// <summary>The running app's version without build metadata, such as "0.1.0"; empty shows "None".</summary>
    public static string Version { get; } = AppVersionText.ToDisplay(AppVersionText.EntryInformationalVersion);

    public static string Author => "Mina Talaat Hakim";

    /// <summary>The repository the GitHub link opens in the default browser.</summary>
    public static Uri GitHubUri { get; } = new("https://github.com/ETBMina/MaxPlanner");

    /// <summary>The license the app is released under (ADR 0032); the full text is the LICENSE file beside the EXE.</summary>
    public static string LicenseName => "PolyForm Internal Use 1.0.0";

    /// <summary>The LICENSE file beside the EXE.</summary>
    public static string LicensePath { get; } = FilePath(AppContext.BaseDirectory, "LICENSE");

    /// <summary>The third-party notices beside the EXE: every bundled component with its license text.</summary>
    public static string NoticesPath { get; } = FilePath(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");

    public static bool HasLicenseFile { get; } = CanOpenFile(LicensePath);

    public static bool HasNoticesFile { get; } = CanOpenFile(NoticesPath);

    /// <summary>Whether the links row has anything to show; with neither file there is no row at all.</summary>
    public static bool HasAnyDocument => HasLicenseFile || HasNoticesFile;

    /// <summary>The path of an app-owned file in the app folder (AGENTS.md: paths resolve from the app base directory).</summary>
    public static string FilePath(string baseDirectory, string fileName) => Path.Combine(baseDirectory, fileName);

    /// <summary>Whether a link to this file is offered: a null and an empty path both mean no (x:Bind turns null into empty).</summary>
    public static bool CanOpenFile(string? path) => !string.IsNullOrEmpty(path) && File.Exists(path);

    /// <summary>The link text: the repository address without the scheme, "github.com/ETBMina/MaxPlanner".</summary>
    public static string GitHubText => ToLinkText(GitHubUri);

    /// <summary>An address as a link shows it: host and path, without the scheme or a trailing slash.</summary>
    public static string ToLinkText(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return (uri.Host + uri.AbsolutePath).TrimEnd('/');
    }
}
