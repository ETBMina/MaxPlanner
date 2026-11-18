using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MaxPlanner.Application.PullRequests;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaxPlanner.Infrastructure.AzureDevOps;

/// <summary>
/// The Azure DevOps Server 2020 REST client (ADR 0031): api-version 6.0, Basic authentication with an empty user name and
/// the PAT, one long-lived <see cref="HttpClient"/> on the default handler (Schannel, the system proxy, certificate
/// checks on), a 30 s timeout per request, and no automatic retries. The token is put on each request, never on the
/// client. Logs carry only the call, the failure category and the status code: never the token, an address, a branch
/// name, a server message or an exception message.
/// </summary>
public sealed partial class AzureDevOpsClient : IAzureDevOpsClient, IDisposable
{
    public const string ApiVersion = "6.0";

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    // A real answer is a few KiB; anything far larger is not read.
    private const int MaxResponseBytes = 4 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly TimeSpan _timeout;

    /// <param name="handler">Tests pass a stub; null uses the default handler.</param>
    /// <param name="timeout">Per request; null is <see cref="DefaultTimeout"/>.</param>
    public AzureDevOpsClient(ILogger<AzureDevOpsClient>? logger = null, HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        _logger = logger ?? NullLogger<AzureDevOpsClient>.Instance;
        _timeout = timeout ?? DefaultTimeout;

        // The timeout is applied per request below, so a timeout can be told apart from the caller's cancellation.
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    public void Dispose() => _http.Dispose();

    public async Task<AzureDevOpsResult<AzureDevOpsUser>> GetUserAsync(AzureDevOpsConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // connectionData is a location API; the server answers it without an api-version.
        var response = await SendAsync(Call.ConnectionData, HttpMethod.Get, new Uri(Collection(connection) + "/_apis/connectionData"), connection, null, cancellationToken).ConfigureAwait(false);
        if (response.Failure is { } failure)
        {
            return AzureDevOpsResult.Failed<AzureDevOpsUser>(failure);
        }

        return Read(Call.ConnectionData, response, root =>
            root.TryGetProperty("authenticatedUser", out var user) && Text(user, "id") is { Length: > 0 } id
                ? new AzureDevOpsUser(id, Text(user, "providerDisplayName") ?? Text(user, "customDisplayName") ?? string.Empty)
                : null);
    }

    public async Task<AzureDevOpsResult<string>> GetRepositoryAsync(AzureDevOpsConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var response = await SendAsync(Call.Repository, HttpMethod.Get, Api(connection, string.Empty, null), connection, null, cancellationToken).ConfigureAwait(false);
        return response.Failure is { } failure
            ? AzureDevOpsResult.Failed<string>(failure)
            : Read(Call.Repository, response, root => Text(root, "name"));
    }

    public async Task<AzureDevOpsResult<bool>> BranchExistsAsync(AzureDevOpsConnection connection, string branch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var refName = BranchNames.ToRefName(branch);

        // The filter is a prefix ("heads/12" also lists "heads/123"), so the exact name is matched here.
        var uri = Api(connection, "/refs", "filter=" + Uri.EscapeDataString(refName["refs/".Length..]).Replace("%2F", "/", StringComparison.Ordinal));
        var response = await SendAsync(Call.Refs, HttpMethod.Get, uri, connection, null, cancellationToken).ConfigureAwait(false);
        if (response.Failure is { } failure)
        {
            return AzureDevOpsResult.Failed<bool>(failure);
        }

        return Read<bool?>(Call.Refs, response, root =>
            root.TryGetProperty("value", out var refs) && refs.ValueKind == JsonValueKind.Array
                ? refs.EnumerateArray().Any(r => string.Equals(Text(r, "name"), refName, StringComparison.OrdinalIgnoreCase))
                : null) is { IsSuccess: true, Value: { } exists }
            ? AzureDevOpsResult.Success<bool>(exists)
            : AzureDevOpsResult.Failed<bool>(new AzureDevOpsFailure(AzureDevOpsFailureKind.Server, response.StatusCode));
    }

    public async Task<AzureDevOpsResult<int>> GetCommitsAheadAsync(AzureDevOpsConnection connection, string source, string target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var query = "baseVersion=" + Uri.EscapeDataString(BranchNames.ToShortName(target)) + "&baseVersionType=branch"
            + "&targetVersion=" + Uri.EscapeDataString(BranchNames.ToShortName(source)) + "&targetVersionType=branch"
            + "&diffCommonCommit=true&$top=1";
        var response = await SendAsync(Call.CommitsAhead, HttpMethod.Get, Api(connection, "/diffs/commits", query), connection, null, cancellationToken).ConfigureAwait(false);
        if (response.Failure is { } failure)
        {
            return AzureDevOpsResult.Failed<int>(failure);
        }

        return Read<int?>(Call.CommitsAhead, response, root => Int(root, "aheadCount"))
            is { IsSuccess: true, Value: { } value }
            ? AzureDevOpsResult.Success<int>(value)
            : AzureDevOpsResult.Failed<int>(new AzureDevOpsFailure(AzureDevOpsFailureKind.Server, response.StatusCode));
    }

    public async Task<AzureDevOpsResult<int>> CreatePullRequestAsync(AzureDevOpsConnection connection, string source, string target, string title, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(title);
        var body = Json(writer =>
        {
            writer.WriteString("sourceRefName", BranchNames.ToRefName(source));
            writer.WriteString("targetRefName", BranchNames.ToRefName(target));
            writer.WriteString("title", title);
        });
        var response = await SendAsync(Call.CreatePullRequest, HttpMethod.Post, Api(connection, "/pullrequests", null), connection, body, cancellationToken).ConfigureAwait(false);
        if (response.Failure is { } failure)
        {
            return AzureDevOpsResult.Failed<int>(failure);
        }

        return Read<int?>(Call.CreatePullRequest, response, root => Int(root, "pullRequestId"))
            is { IsSuccess: true, Value: { } created }
            ? AzureDevOpsResult.Success<int>(created)
            : AzureDevOpsResult.Failed<int>(new AzureDevOpsFailure(AzureDevOpsFailureKind.Server, response.StatusCode));
    }

    public async Task<AzureDevOpsResult<bool>> SetAutoCompleteAsync(AzureDevOpsConnection connection, int pullRequestId, string userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(userId);
        var body = Json(writer =>
        {
            writer.WriteStartObject("autoCompleteSetBy");
            writer.WriteString("id", userId);
            writer.WriteEndObject();
            writer.WriteStartObject("completionOptions");
            writer.WriteBoolean("deleteSourceBranch", false);
            writer.WriteEndObject();
        });
        var uri = Api(connection, "/pullrequests/" + pullRequestId.ToString(System.Globalization.CultureInfo.InvariantCulture), null);
        var response = await SendAsync(Call.AutoComplete, HttpMethod.Patch, uri, connection, body, cancellationToken).ConfigureAwait(false);
        return response.Failure is { } failure ? AzureDevOpsResult.Failed<bool>(failure) : AzureDevOpsResult.Success<bool>(true);
    }

    public async Task<AzureDevOpsResult<int?>> FindActivePullRequestAsync(AzureDevOpsConnection connection, string source, string target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var query = "searchCriteria.sourceRefName=" + Uri.EscapeDataString(BranchNames.ToRefName(source))
            + "&searchCriteria.targetRefName=" + Uri.EscapeDataString(BranchNames.ToRefName(target))
            + "&searchCriteria.status=active&$top=1";
        var response = await SendAsync(Call.FindPullRequest, HttpMethod.Get, Api(connection, "/pullrequests", query), connection, null, cancellationToken).ConfigureAwait(false);
        if (response.Failure is { } failure)
        {
            return AzureDevOpsResult.Failed<int?>(failure);
        }

        int? found = null;
        var read = Read<bool?>(Call.FindPullRequest, response, root =>
        {
            if (!root.TryGetProperty("value", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object && Int(item, "pullRequestId") is { } value)
                {
                    found = value;
                    break;
                }
            }

            return true;
        });
        return read.IsSuccess ? AzureDevOpsResult.Success<int?>(found) : AzureDevOpsResult.Failed<int?>(read.Failure!);
    }

    private static string Collection(AzureDevOpsConnection connection) => connection.CollectionUrl.AbsoluteUri.TrimEnd('/');

    // {collection}/{project}/_apis/git/repositories/{repo}{path}?{query}&api-version=6.0
    private static Uri Api(AzureDevOpsConnection connection, string path, string? query) => new(
        $"{Collection(connection)}/{Uri.EscapeDataString(connection.Project)}/_apis/git/repositories/{Uri.EscapeDataString(connection.Repository)}{path}"
            + "?" + (query is null ? string.Empty : query + "&") + "api-version=" + ApiVersion,
        UriKind.Absolute);

    private static byte[] Json(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private async Task<Response> SendAsync(Call call, HttpMethod method, Uri uri, AzureDevOpsConnection connection, byte[]? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + connection.Token)));
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            var text = await ReadBodyAsync(response, timeout.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode && !IsSignInPage(response))
            {
                return new Response(status, text, null);
            }

            var (message, typeKey) = ErrorOf(text);
            var kind = response.StatusCode switch
            {
                _ when response.IsSuccessStatusCode => AzureDevOpsFailureKind.Unauthorized, // A sign-in page (IsSignInPage).
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => AzureDevOpsFailureKind.Unauthorized,
                HttpStatusCode.NotFound => AzureDevOpsFailureKind.NotFound,

                // Matched by status and type key, never by the message wording (ADR 0031).
                HttpStatusCode.Conflict when string.Equals(typeKey, "GitPullRequestExistsException", StringComparison.Ordinal) => AzureDevOpsFailureKind.AlreadyExists,
                _ => AzureDevOpsFailureKind.Server,
            };
            Log.CallFailed(_logger, call, kind, status);
            return new Response(status, null, new AzureDevOpsFailure(kind, status, message));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Log.CallFailed(_logger, call, AzureDevOpsFailureKind.Timeout, null);
            return new Response(null, null, new AzureDevOpsFailure(AzureDevOpsFailureKind.Timeout));
        }
        catch (HttpRequestException ex)
        {
            // The exception's message can name the server; only its category is kept.
            var reason = ex.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError => AzureDevOpsNetworkReason.NameResolution,
                HttpRequestError.ConnectionError => AzureDevOpsNetworkReason.Connection,
                HttpRequestError.SecureConnectionError => AzureDevOpsNetworkReason.SecureConnection,
                HttpRequestError.ProxyTunnelError => AzureDevOpsNetworkReason.Proxy,
                _ => AzureDevOpsNetworkReason.Unknown,
            };
            Log.Unreachable(_logger, call, reason);
            return new Response(null, null, new AzureDevOpsFailure(AzureDevOpsFailureKind.Network, NetworkReason: reason));
        }
        catch (IOException)
        {
            // The connection dropped while the answer was read; the request may have been handled.
            Log.Unreachable(_logger, call, AzureDevOpsNetworkReason.Unknown);
            return new Response(null, null, new AzureDevOpsFailure(AzureDevOpsFailureKind.Network, NetworkReason: AzureDevOpsNetworkReason.Unknown));
        }
    }

    // A success status that is not the REST answer: 203 (a sign-in page served in place of the answer), or a 200 whose
    // body is not JSON (an HTML sign-in page from the server or a proxy). The token was not accepted for this call. Other
    // success statuses (201 Created) are never a sign-in page; an unreadable one stays a server failure, so a create that
    // may have worked is not reported as refused.
    private static bool IsSignInPage(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.NonAuthoritativeInformation)
        {
            return true;
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        return response.StatusCode == HttpStatusCode.OK
            && (mediaType is null
                || !(mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)));
    }

    // Reads at most MaxResponseBytes, stopping as soon as the answer is larger (also for a chunked answer with no
    // length); an oversized answer reads as empty, which no caller can parse. The answer is UTF-8 (api-version 6.0).
    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
        {
            return string.Empty;
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxResponseBytes)
                {
                    return string.Empty;
                }

                buffer.Write(chunk, 0, read);
            }

            return Utf8Text(buffer);
        }
    }

    private static string Utf8Text(MemoryStream buffer)
    {
        var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        var preamble = Encoding.UTF8.Preamble;
        return Encoding.UTF8.GetString(bytes.StartsWith(preamble) ? bytes[preamble.Length..] : bytes);
    }

    // Azure DevOps errors: {"message": "TF401179: …", "typeKey": "GitPullRequestExistsException", …}.
    private static (string? Message, string? TypeKey) ErrorOf(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? (Text(document.RootElement, "message"), Text(document.RootElement, "typeKey"))
                : (null, null);
        }
        catch (JsonException)
        {
            // An HTML error page from a proxy or the server itself: no message to show.
            return (null, null);
        }
    }

    // An answer of an unexpected shape (wrong value kinds, a number out of range) is a failure, never an exception.
    private AzureDevOpsResult<T> Read<T>(Call call, Response response, Func<JsonElement, T?> read)
    {
        try
        {
            using var document = JsonDocument.Parse(response.Body ?? string.Empty);
            if (document.RootElement.ValueKind == JsonValueKind.Object && read(document.RootElement) is { } value)
            {
                return AzureDevOpsResult.Success<T>(value);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
        }

        Log.Unreadable(_logger, call, response.StatusCode);
        return AzureDevOpsResult.Failed<T>(new AzureDevOpsFailure(AzureDevOpsFailureKind.Server, response.StatusCode));
    }

    // Only a JSON number counts: TryGetInt32 throws on a string or null.
    private static int? Int(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Which REST call failed, for the log.</summary>
    private enum Call
    {
        ConnectionData,
        Repository,
        Refs,
        CommitsAhead,
        CreatePullRequest,
        AutoComplete,
        FindPullRequest,
    }

    private sealed record Response(int? StatusCode, string? Body, AzureDevOpsFailure? Failure);

    private static partial class Log
    {
        [LoggerMessage(EventId = 1701, Level = LogLevel.Warning, Message = "Azure DevOps call {Call} failed: {Kind} (HTTP {StatusCode}).")]
        public static partial void CallFailed(ILogger logger, Call call, AzureDevOpsFailureKind kind, int? statusCode);

        [LoggerMessage(EventId = 1702, Level = LogLevel.Warning, Message = "Azure DevOps call {Call} could not reach the server ({Reason}).")]
        public static partial void Unreachable(ILogger logger, Call call, AzureDevOpsNetworkReason reason);

        [LoggerMessage(EventId = 1703, Level = LogLevel.Warning, Message = "Azure DevOps call {Call} returned an answer the app cannot read (HTTP {StatusCode}).")]
        public static partial void Unreadable(ILogger logger, Call call, int? statusCode);
    }
}
