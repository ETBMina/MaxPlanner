using System.Globalization;
using MaxPlanner.Application.PullRequests;
using MaxPlanner.Presentation.Common.Services;

namespace MaxPlanner.Presentation.Common.PullRequests;

/// <summary>
/// The user-facing text of Azure DevOps failures (design §5.11), shared by the pull request dialog and Settings › Test
/// connection. A server's own message is shown as it came; an exception message never is.
/// </summary>
public static class AzureDevOpsText
{
    /// <summary>"{reason}" of "Couldn't reach {server}: {reason}." for a timeout or a network failure.</summary>
    public static string Reason(AzureDevOpsFailure failure, ILocalizedStrings strings)
    {
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentNullException.ThrowIfNull(strings);
        return strings.GetString(failure.Kind == AzureDevOpsFailureKind.Timeout ? "AzureDevOps_ReasonTimeout" : failure.NetworkReason switch
        {
            AzureDevOpsNetworkReason.NameResolution => "AzureDevOps_ReasonNameResolution",
            AzureDevOpsNetworkReason.Connection => "AzureDevOps_ReasonConnection",
            AzureDevOpsNetworkReason.SecureConnection => "AzureDevOps_ReasonSecureConnection",
            AzureDevOpsNetworkReason.Proxy => "AzureDevOps_ReasonProxy",
            _ => "AzureDevOps_ReasonUnknown",
        });
    }

    /// <summary>"{server message}": the server's own message, else its HTTP status, else the network reason.</summary>
    public static string ServerMessage(AzureDevOpsFailure? failure, ILocalizedStrings strings)
    {
        ArgumentNullException.ThrowIfNull(strings);
        return failure switch
        {
            { ServerMessage: { Length: > 0 } message } => message.Trim().TrimEnd('.'),
            { StatusCode: { } status } => strings.Format("AzureDevOps_HttpStatus", status.ToString(CultureInfo.InvariantCulture)),
            { Kind: AzureDevOpsFailureKind.Timeout or AzureDevOpsFailureKind.Network } => Reason(failure, strings),
            _ => strings.GetString("AzureDevOps_ReasonUnknown"),
        };
    }

    /// <summary>"Collection URL, Project, Token" for "Missing: {items}".</summary>
    public static string MissingItems(IEnumerable<AzureDevOpsSetupItem> items, ILocalizedStrings strings)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(strings);
        return string.Join(", ", items.Select(item => strings.GetString("AzureDevOps_Missing" + item)));
    }

    /// <summary>The server's name for "Couldn't reach {server}".</summary>
    public static string ServerName(Uri? collection) => collection?.Host ?? string.Empty;
}
