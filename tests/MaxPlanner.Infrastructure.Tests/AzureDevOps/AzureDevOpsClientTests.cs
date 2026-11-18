using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using MaxPlanner.Application.PullRequests;
using MaxPlanner.Infrastructure.AzureDevOps;
using Microsoft.Extensions.Logging;

namespace MaxPlanner.Infrastructure.Tests.AzureDevOps;

/// <summary>
/// ADR 0031: the REST calls against a stub handler, with the answers Azure DevOps Server 2020 Update 1.2 actually gave.
/// Nothing secret (token, address, branch, server or exception message) reaches the log.
/// </summary>
[Trait("Req", "B-18")]
public sealed class AzureDevOpsClientTests : IDisposable
{
    private const string Token = "s3cr3t-pat-value";

    // The server's real answers (scripts/ado-pr-test.py, Nov 2026).
    private const string DuplicateBody =
        """{"$id":"1","innerException":null,"message":"TF401179: An active pull request for the source and target branch already exists.","typeName":"Microsoft.TeamFoundation.Git.Server.GitPullRequestExistsException, Microsoft.TeamFoundation.Git.Server","typeKey":"GitPullRequestExistsException","errorCode":0,"eventId":3000}""";

    private const string CommitsBody = """{"aheadCount":0,"behindCount":7918,"allChangesIncluded":true,"changeCounts":{},"changes":[],"commonCommit":"abc"}""";

    private static readonly AzureDevOpsConnection Connection = new(new Uri("https://ado.example.test/tfs/value"), "ace bank", "ace_bank", Token);

    private readonly StubHandler _handler = new();
    private readonly RecordingLogger _logger = new();
    private readonly AzureDevOpsClient _client;

    public AzureDevOpsClientTests() => _client = new AzureDevOpsClient(_logger, _handler, TimeSpan.FromMilliseconds(500));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _client.Dispose();
        _handler.Dispose();
    }

    [Fact]
    public async Task Every_request_sends_basic_authentication_with_an_empty_user_name()
    {
        _handler.Reply(HttpStatusCode.OK, """{"authenticatedUser":{"id":"6c3b-user","providerDisplayName":"Max Planner"}}""");

        var user = await _client.GetUserAsync(Connection, Ct);

        user.Value.Should().Be(new AzureDevOpsUser("6c3b-user", "Max Planner"));
        var request = _handler.Requests.Single();
        request.Uri.Should().Be("https://ado.example.test/tfs/value/_apis/connectionData");
        request.Authorization.Should().Be("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + Token)));
        request.Accept.Should().Contain("application/json");
    }

    [Fact]
    public async Task A_branch_exists_only_when_its_exact_name_is_listed()
    {
        _handler.Reply(HttpStatusCode.OK, """{"value":[{"name":"refs/heads/feature/12345-fix","objectId":"1"}],"count":1}""");
        _handler.Reply(HttpStatusCode.OK, """{"value":[{"name":"refs/heads/feature/12345","objectId":"1"},{"name":"refs/heads/feature/12345-fix","objectId":"2"}],"count":2}""");

        (await _client.BranchExistsAsync(Connection, "feature/12345", Ct)).Value.Should().BeFalse("the refs filter is a prefix match");
        (await _client.BranchExistsAsync(Connection, "refs/heads/feature/12345", Ct)).Value.Should().BeTrue();

        _handler.Requests[0].Uri.Should().Be(
            "https://ado.example.test/tfs/value/ace%20bank/_apis/git/repositories/ace_bank/refs?filter=heads/feature/12345&api-version=6.0");
    }

    [Fact]
    public async Task Commits_ahead_come_from_the_diffs_answer()
    {
        _handler.Reply(HttpStatusCode.OK, CommitsBody);

        var ahead = await _client.GetCommitsAheadAsync(Connection, "12345", "uat", Ct);

        ahead.Should().Be(AzureDevOpsResult.Success(0));
        _handler.Requests.Single().Uri.Should().Be(
            "https://ado.example.test/tfs/value/ace%20bank/_apis/git/repositories/ace_bank/diffs/commits"
            + "?baseVersion=uat&baseVersionType=branch&targetVersion=12345&targetVersionType=branch&diffCommonCommit=true&$top=1&api-version=6.0");
    }

    [Fact]
    public async Task The_pull_request_is_posted_with_full_branch_names_and_no_description()
    {
        _handler.Reply(HttpStatusCode.Created, """{"pullRequestId":1187,"status":"active"}""");

        var created = await _client.CreatePullRequestAsync(Connection, "12345", "refs/heads/uat", "PR CR-1 12345 into uat", Ct);

        created.Should().Be(AzureDevOpsResult.Success(1187));
        var request = _handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.Should().EndWith("/ace_bank/pullrequests?api-version=6.0");
        using var body = JsonDocument.Parse(request.Body!);
        body.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal("sourceRefName", "targetRefName", "title");
        body.RootElement.GetProperty("sourceRefName").GetString().Should().Be("refs/heads/12345");
        body.RootElement.GetProperty("targetRefName").GetString().Should().Be("refs/heads/uat");
    }

    [Fact]
    public async Task A_duplicate_is_recognized_by_status_and_type_key()
    {
        _handler.Reply(HttpStatusCode.Conflict, DuplicateBody);
        _handler.Reply(HttpStatusCode.Conflict, DuplicateBody.Replace("GitPullRequestExistsException\",\"errorCode", "GitSomethingElseException\",\"errorCode", StringComparison.Ordinal));

        var duplicate = await _client.CreatePullRequestAsync(Connection, "12345", "uat", "T", Ct);
        var other = await _client.CreatePullRequestAsync(Connection, "12345", "uat", "T", Ct);

        duplicate.Failure.Should().Be(new AzureDevOpsFailure(
            AzureDevOpsFailureKind.AlreadyExists, 409, "TF401179: An active pull request for the source and target branch already exists."));
        other.Failure!.Kind.Should().Be(AzureDevOpsFailureKind.Server, "the message wording alone never counts");
        _handler.Requests.Should().HaveCount(2, "nothing is retried automatically");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, AzureDevOpsFailureKind.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, AzureDevOpsFailureKind.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound, AzureDevOpsFailureKind.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError, AzureDevOpsFailureKind.Server)]
    public async Task Error_statuses_map_to_their_failure(HttpStatusCode status, AzureDevOpsFailureKind kind)
    {
        _handler.Reply(status, "<html>Sign in to ado.example.test</html>");

        var result = await _client.GetRepositoryAsync(Connection, Ct);

        result.Failure.Should().Be(new AzureDevOpsFailure(kind, (int)status));
    }

    [Fact]
    public async Task An_unreadable_success_answer_is_a_server_failure()
    {
        _handler.Reply(HttpStatusCode.OK, "<html>proxy login</html>");

        var result = await _client.GetCommitsAheadAsync(Connection, "12345", "uat", Ct);

        result.Failure.Should().Be(new AzureDevOpsFailure(AzureDevOpsFailureKind.Server, 200));
    }

    [Fact]
    public async Task No_answer_within_the_timeout_is_a_timeout()
    {
        _handler.Delay = TimeSpan.FromSeconds(30);

        var result = await _client.GetUserAsync(Connection, Ct);

        result.Failure.Should().Be(new AzureDevOpsFailure(AzureDevOpsFailureKind.Timeout));
    }

    [Fact]
    public async Task The_callers_cancellation_is_not_a_timeout()
    {
        _handler.Delay = TimeSpan.FromSeconds(30);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancel.CancelAfter(50);

        var act = () => _client.GetUserAsync(Connection, cancel.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData(HttpRequestError.SecureConnectionError, AzureDevOpsNetworkReason.SecureConnection)]
    [InlineData(HttpRequestError.NameResolutionError, AzureDevOpsNetworkReason.NameResolution)]
    [InlineData(HttpRequestError.ConnectionError, AzureDevOpsNetworkReason.Connection)]
    [InlineData(HttpRequestError.ProxyTunnelError, AzureDevOpsNetworkReason.Proxy)]
    [InlineData(HttpRequestError.Unknown, AzureDevOpsNetworkReason.Unknown)]
    public async Task A_network_failure_keeps_only_its_category(HttpRequestError error, AzureDevOpsNetworkReason reason)
    {
        _handler.Throw = new HttpRequestException(error, "The SSL connection to ado.example.test could not be established.");

        var result = await _client.GetUserAsync(Connection, Ct);

        result.Failure.Should().Be(new AzureDevOpsFailure(AzureDevOpsFailureKind.Network, NetworkReason: reason));
    }

    [Fact]
    public async Task Auto_complete_is_set_by_the_user_and_keeps_the_source_branch()
    {
        _handler.Reply(HttpStatusCode.OK, """{"pullRequestId":1187}""");

        var result = await _client.SetAutoCompleteAsync(Connection, 1187, "6c3b-user", Ct);

        result.IsSuccess.Should().BeTrue();
        var request = _handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Patch);
        request.Uri.Should().EndWith("/pullrequests/1187?api-version=6.0");
        using var body = JsonDocument.Parse(request.Body!);
        body.RootElement.GetProperty("autoCompleteSetBy").GetProperty("id").GetString().Should().Be("6c3b-user");
        body.RootElement.GetProperty("completionOptions").GetProperty("deleteSourceBranch").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task The_active_pull_request_is_found_by_source_and_target()
    {
        _handler.Reply(HttpStatusCode.OK, """{"value":[{"pullRequestId":1180,"status":"active"}],"count":1}""");
        _handler.Reply(HttpStatusCode.OK, """{"value":[],"count":0}""");

        (await _client.FindActivePullRequestAsync(Connection, "12345", "uat", Ct)).Value.Should().Be(1180);
        (await _client.FindActivePullRequestAsync(Connection, "12345", "uat", Ct)).Value.Should().BeNull();

        _handler.Requests[0].Uri.Should().Contain("searchCriteria.sourceRefName=refs%2Fheads%2F12345")
            .And.Contain("searchCriteria.targetRefName=refs%2Fheads%2Fuat")
            .And.Contain("searchCriteria.status=active");
    }

    [Fact]
    public async Task The_log_never_carries_the_token_an_address_a_branch_or_a_message()
    {
        _handler.Reply(HttpStatusCode.Conflict, DuplicateBody);
        _handler.Reply(HttpStatusCode.Unauthorized, string.Empty);
        await _client.CreatePullRequestAsync(Connection, "secret-branch-12345", "uat", "Secret title", Ct);
        await _client.BranchExistsAsync(Connection, "secret-branch-12345", Ct);
        _handler.Throw = new HttpRequestException(HttpRequestError.SecureConnectionError, "The SSL connection to ado.example.test failed.");
        await _client.GetUserAsync(Connection, Ct);

        _logger.Entries.Should().HaveCount(3);
        foreach (var entry in _logger.Entries)
        {
            entry.Message.Should().NotContain(Token).And.NotContain("ado.example.test").And.NotContain("secret-branch")
                .And.NotContain("TF401179").And.NotContain("Secret title").And.NotContain("SSL");
            entry.Exception.Should().BeNull();
        }

        _logger.Entries.Select(e => e.EventId).Should().Equal(1701, 1701, 1702);
    }

    [Theory]
    [InlineData("""{"pullRequestId":null}""")]
    [InlineData("""{"pullRequestId":"12"}""")]
    [InlineData("""{"pullRequestId":12.5}""")]
    [InlineData("""{"pullRequestId":99999999999}""")]
    public async Task A_created_answer_without_a_numeric_id_is_a_failure_not_an_exception(string body)
    {
        _handler.Reply(HttpStatusCode.Created, body);

        var result = await _client.CreatePullRequestAsync(Connection, "12345", "uat", "T", Ct);

        result.Failure.Should().Be(new AzureDevOpsFailure(AzureDevOpsFailureKind.Server, 201));
    }

    [Theory]
    [InlineData("""{"aheadCount":"3"}""")]
    [InlineData("""{"aheadCount":null}""")]
    [InlineData("""{"aheadCount":true}""")]
    public async Task A_non_numeric_ahead_count_is_a_failure_not_an_exception(string body)
    {
        _handler.Reply(HttpStatusCode.OK, body);

        var result = await _client.GetCommitsAheadAsync(Connection, "12345", "uat", Ct);

        result.Failure.Should().Be(new AzureDevOpsFailure(AzureDevOpsFailureKind.Server, 200));
    }

    [Fact]
    public async Task A_non_numeric_id_in_the_pull_request_list_is_skipped()
    {
        _handler.Reply(HttpStatusCode.OK, """{"value":[{"pullRequestId":"x"},{"pullRequestId":1181}],"count":2}""");

        (await _client.FindActivePullRequestAsync(Connection, "12345", "uat", Ct)).Value.Should().Be(1181);
    }

    [Fact]
    public async Task A_203_answer_is_a_sign_in_page_and_means_the_token_was_not_accepted()
    {
        _handler.Reply(HttpStatusCode.NonAuthoritativeInformation, """{"authenticatedUser":{"id":"anonymous"}}""");

        var result = await _client.GetUserAsync(Connection, Ct);

        result.Failure.Should().Be(new AzureDevOpsFailure(AzureDevOpsFailureKind.Unauthorized, 203));
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData(null)]
    public async Task A_200_answer_that_is_not_JSON_is_a_sign_in_page(string? mediaType)
    {
        _handler.Reply(HttpStatusCode.OK, () =>
        {
            var content = new StringContent("<html>Sign in</html>", Encoding.UTF8);
            content.Headers.ContentType = mediaType is null ? null : new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
            return content;
        });

        var result = await _client.GetRepositoryAsync(Connection, Ct);

        result.Failure.Should().Be(new AzureDevOpsFailure(AzureDevOpsFailureKind.Unauthorized, 200));
    }

    [Fact]
    public async Task A_JSON_answer_with_a_vendor_media_type_is_read()
    {
        _handler.Reply(HttpStatusCode.OK, () => new StringContent("""{"name":"ace_bank"}""", Encoding.UTF8, "application/vnd.api+json"));

        (await _client.GetRepositoryAsync(Connection, Ct)).Value.Should().Be("ace_bank");
    }

    [Fact]
    public async Task A_chunked_answer_over_the_cap_stops_being_read_at_the_cap()
    {
        using var endless = new EndlessStream();
        _handler.Reply(HttpStatusCode.OK, () =>
        {
            var content = new StreamContent(endless);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return content;
        });

        var result = await _client.GetRepositoryAsync(Connection, Ct);

        result.Failure.Should().Be(new AzureDevOpsFailure(AzureDevOpsFailureKind.Server, 200));
        endless.BytesRead.Should().BeLessThanOrEqualTo((4 * 1024 * 1024) + (64 * 1024), "reading stops once the answer passes 4 MiB");
    }

    [Fact]
    public async Task A_connection_lost_while_the_answer_is_read_is_a_network_failure()
    {
        _handler.Reply(HttpStatusCode.Created, () => new StreamContent(new BrokenStream()));

        var result = await _client.CreatePullRequestAsync(Connection, "12345", "uat", "T", Ct);

        result.Failure.Should().Be(new AzureDevOpsFailure(AzureDevOpsFailureKind.Network, NetworkReason: AzureDevOpsNetworkReason.Unknown));
    }

    private sealed record SentRequest(HttpMethod Method, string Uri, string? Authorization, string Accept, string? Body);

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, Func<HttpContent> Content)> _replies = new();

        public List<SentRequest> Requests { get; } = [];

        public TimeSpan Delay { get; set; }

        public Exception? Throw { get; set; }

        public void Reply(HttpStatusCode status, string body) => Reply(status, () => new StringContent(body, Encoding.UTF8, "application/json"));

        public void Reply(HttpStatusCode status, Func<HttpContent> content) => _replies.Enqueue((status, content));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new SentRequest(
                request.Method,
                request.RequestUri!.AbsoluteUri,
                request.Headers.Authorization?.ToString(),
                request.Headers.Accept.ToString(),
                body));
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            if (Throw is { } exception)
            {
                throw exception;
            }

            var (status, content) = _replies.Dequeue();
            return new HttpResponseMessage(status) { Content = content() };
        }
    }

    /// <summary>A body with no length that never ends, like an oversized chunked answer.</summary>
    private sealed class EndlessStream : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)'x', offset, count);
            BytesRead += count;
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A body whose connection drops while it is read.</summary>
    private sealed class BrokenStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("The response ended prematurely.");

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed record LogEntry(int EventId, string Message, Exception? Exception);

    private sealed class RecordingLogger : ILogger<AzureDevOpsClient>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add(new LogEntry(eventId.Id, formatter(state, exception), exception));
    }
}
