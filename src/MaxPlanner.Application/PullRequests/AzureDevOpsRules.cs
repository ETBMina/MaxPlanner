using MaxPlanner.Application.Preferences;

namespace MaxPlanner.Application.PullRequests;

/// <summary>What is wrong with a typed collection URL (design §4.6); <see cref="None"/> means it can be used.</summary>
public enum CollectionUrlProblem
{
    None,
    Empty,

    /// <summary>Not an absolute address, or not <c>https://</c>.</summary>
    NotHttps,

    /// <summary>A repository or API address (<c>/_git/</c>, <c>/_apis/</c>) instead of the collection.</summary>
    NotCollection,

    /// <summary>A user name or password (<c>https://user:secret@server/…</c>), a query (<c>?…</c>) or a fragment (<c>#…</c>).</summary>
    ExtraParts,
}

/// <summary>The collection URL rules (ADR 0031): <c>https://server/collection</c>, certificate checks always on.</summary>
public static class CollectionUrls
{
    public static CollectionUrlProblem Check(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return CollectionUrlProblem.Empty;
        }

        var text = value.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host))
        {
            return CollectionUrlProblem.NotHttps;
        }

        // The token is the only credential; a user name or password in the address would be sent and could be logged
        // by a proxy. A bare "?" or "#", or an "@" with no user name, still counts.
        var authority = text[(text.IndexOf("://", StringComparison.Ordinal) + 3)..].Split('/')[0];
        if (uri.UserInfo.Length > 0 || authority.Contains('@', StringComparison.Ordinal) || text.Contains('?', StringComparison.Ordinal) || text.Contains('#', StringComparison.Ordinal))
        {
            return CollectionUrlProblem.ExtraParts;
        }

        var path = uri.AbsolutePath;
        return path.Contains("/_git/", StringComparison.OrdinalIgnoreCase) || path.Contains("/_apis/", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/_git", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/_apis", StringComparison.OrdinalIgnoreCase)
            ? CollectionUrlProblem.NotCollection
            : CollectionUrlProblem.None;
    }

    /// <summary>The usable collection address without a trailing slash, or null when <see cref="Check"/> finds a problem.</summary>
    public static Uri? Parse(string? value) =>
        Check(value) == CollectionUrlProblem.None ? new Uri(value!.Trim().TrimEnd('/'), UriKind.Absolute) : null;
}

/// <summary>What is wrong with a typed branch name (design §5.11).</summary>
public enum BranchNameProblem
{
    None,
    Empty,

    /// <summary>Breaks a git reference-name rule (spaces, <c>~ ^ : ? * [ \</c>, <c>..</c>, a trailing <c>/</c> or <c>.lock</c>, …).</summary>
    Invalid,
}

/// <summary>
/// Branch names as the pull request dialog takes them (design §5.11): a short name is sent as <c>refs/heads/{name}</c>, and
/// a pasted <c>refs/heads/…</c> is accepted. The rules follow <c>git check-ref-format</c> for a branch.
/// </summary>
public static class BranchNames
{
    public const string HeadsPrefix = "refs/heads/";

    /// <summary>The short name: trimmed, without a leading <c>refs/heads/</c>.</summary>
    public static string ToShortName(string? value)
    {
        var name = (value ?? string.Empty).Trim();
        return name.StartsWith(HeadsPrefix, StringComparison.OrdinalIgnoreCase) ? name[HeadsPrefix.Length..] : name;
    }

    public static string ToRefName(string? value) => HeadsPrefix + ToShortName(value);

    /// <summary>Azure DevOps compares branch names without case.</summary>
    public static bool AreSame(string? first, string? second) =>
        string.Equals(ToShortName(first), ToShortName(second), StringComparison.OrdinalIgnoreCase);

    public static BranchNameProblem Check(string? value)
    {
        var name = ToShortName(value);
        if (name.Length == 0)
        {
            return BranchNameProblem.Empty;
        }

        return IsValid(name) ? BranchNameProblem.None : BranchNameProblem.Invalid;
    }

    private static bool IsValid(string name)
    {
        if (name == "@" || name.StartsWith('/') || name.StartsWith('-') || name.EndsWith('/') || name.EndsWith('.')
            || name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
            || name.Contains("..", StringComparison.Ordinal) || name.Contains("//", StringComparison.Ordinal) || name.Contains("@{", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in name)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c) || c is '~' or '^' or ':' or '?' or '*' or '[' or '\\')
            {
                return false;
            }
        }

        // No path component may start with a dot or end with ".lock".
        return name.Split('/').All(part => !part.StartsWith('.') && !part.EndsWith(".lock", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>A setting the pull request dialog needs that is missing or unusable (design §5.11 "Setup incomplete").</summary>
public enum AzureDevOpsSetupItem
{
    CollectionUrl,
    Project,
    Repository,
    Token,
}

/// <summary>Whether the Azure DevOps settings are complete enough to create a pull request (ADR 0031).</summary>
public static class AzureDevOpsSetup
{
    /// <param name="hasToken">A token is saved in Credential Manager; a missing entry counts as missing.</param>
    /// <returns>The missing items in Settings order; empty when the setup is complete.</returns>
    public static IReadOnlyList<AzureDevOpsSetupItem> Missing(AzureDevOpsPreferences? preferences, bool hasToken)
    {
        var missing = new List<AzureDevOpsSetupItem>();
        if (CollectionUrls.Parse(preferences?.CollectionUrl) is null)
        {
            missing.Add(AzureDevOpsSetupItem.CollectionUrl);
        }

        if (string.IsNullOrWhiteSpace(preferences?.Project))
        {
            missing.Add(AzureDevOpsSetupItem.Project);
        }

        if (string.IsNullOrWhiteSpace(preferences?.Repository))
        {
            missing.Add(AzureDevOpsSetupItem.Repository);
        }

        if (!hasToken)
        {
            missing.Add(AzureDevOpsSetupItem.Token);
        }

        return missing;
    }

    /// <summary>The connection for complete settings, or null while something is missing.</summary>
    public static AzureDevOpsConnection? Connect(AzureDevOpsPreferences? preferences, string? token) =>
        preferences is not null && !string.IsNullOrEmpty(token) && Missing(preferences, hasToken: true).Count == 0
            ? new AzureDevOpsConnection(CollectionUrls.Parse(preferences.CollectionUrl)!, preferences.Project!.Trim(), preferences.Repository!.Trim(), token)
            : null;
}

/// <summary>Pull request web addresses (design §5.11): <c>{collection}/{project}/_git/{repo}/pullrequest/{id}</c>.</summary>
public static class PullRequestLinks
{
    public static Uri WebUrl(Uri collection, string project, string repository, int pullRequestId)
    {
        ArgumentNullException.ThrowIfNull(collection);
        return new Uri(
            $"{collection.AbsoluteUri.TrimEnd('/')}/{Uri.EscapeDataString(project.Trim())}/_git/{Uri.EscapeDataString(repository.Trim())}/pullrequest/{pullRequestId}",
            UriKind.Absolute);
    }
}

/// <summary>
/// The built-in Azure DevOps simulator (ADR 0031): Debug builds only, used when the collection URL's host is
/// <c>simulator</c> (for example <c>https://simulator/coll</c>). Release builds have no simulator, so
/// <see cref="IsInUse"/> is always false there and no "Simulator" label shows.
/// </summary>
public static class AzureDevOpsSimulator
{
    public const string Host = "simulator";

    /// <summary>True in Debug builds, where the composition routes simulator addresses to the simulator.</summary>
    public static bool IsBuiltIn =>
#if DEBUG
        true;
#else
        false;
#endif

    /// <summary>The address's host is <see cref="Host"/>, whatever the build.</summary>
    public static bool IsSimulatorHost(Uri? collectionUrl) =>
        collectionUrl is not null && string.Equals(collectionUrl.Host, Host, StringComparison.OrdinalIgnoreCase);

    /// <summary>A usable collection URL (<see cref="CollectionUrls.Parse"/>) whose host is <see cref="Host"/>; null and empty are false.</summary>
    public static bool IsSimulatorUrl(string? collectionUrl) => IsSimulatorHost(CollectionUrls.Parse(collectionUrl));

    /// <summary>Calls with this collection URL go to the simulator, so the "Simulator" label shows.</summary>
    public static bool IsInUse(string? collectionUrl) => IsBuiltIn && IsSimulatorUrl(collectionUrl);
}
