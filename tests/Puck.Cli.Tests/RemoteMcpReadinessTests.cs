using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Puck.Hosting;
using Puck.Mcp;
using Xunit;

namespace Puck.Cli.Tests;

public sealed class RemoteMcpReadinessTests {
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private const string Claims = """{"access_token":{"acrs":{"essential":true,"value":"c1"}}}""";

    private static void AssertChallenge(HttpResponseMessage response, bool claimsRequired) {
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            response.StatusCode
        );
        var header = response.Headers.WwwAuthenticate.ToString();

        Assert.Contains(
            actualString: header,
            expectedSubstring: "resource_metadata=\"https://mcp.example.test/.well-known/oauth-protected-resource/mcp\""
        );
        Assert.Contains(
            actualString: header,
            expectedSubstring: "scope=\"user_impersonation\""
        );
        Assert.DoesNotContain(
            actualString: header,
            expectedSubstring: "management.azure.com"
        );
        Assert.Contains(
            actualString: header,
            expectedSubstring: (claimsRequired
                ? $"error=\"insufficient_claims\", claims=\"{Convert.ToBase64String(inArray: Encoding.UTF8.GetBytes(s: Claims))}\""
                : "error=\"invalid_token\"")
        );
    }
    private static HttpRequestMessage McpRequest(string? token = null, string method = "tools/call", string name = "test_service") {
        var request = new HttpRequestMessage(
            method: HttpMethod.Post,
            requestUri: "/mcp"
        );
        var call = (method == "tools/call");

        request.Headers.Accept.ParseAdd(input: "application/json, text/event-stream");
        if (token is not null) {
            request.Headers.Authorization = new(
                parameter: token,
                scheme: "Bearer"
            );
        }
        request.Headers.Add(
            name: "MCP-Protocol-Version",
            value: "2026-07-28"
        );
        request.Headers.Add(
            name: "Mcp-Method",
            value: method
        );
        if (call) {
            request.Headers.Add(
                name: "Mcp-Name",
                value: name
            );
        }
        request.Content = new StringContent(
            content: """{"jsonrpc":"2.0","id":1,"method":"METHOD","params":{NAME"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientInfo":{"name":"raw-http","version":"1"},"io.modelcontextprotocol/clientCapabilities":{}}}}""".Replace(
                comparisonType: StringComparison.Ordinal,
                newValue: method,
                oldValue: "METHOD"
            ).Replace(
                comparisonType: StringComparison.Ordinal,
                newValue: (call
                    ? $"\"name\":\"{name}\","
                    : ""),
                oldValue: "NAME"
            ),
            encoding: Encoding.UTF8,
            mediaType: "application/json"
        );
        return request;
    }
    private static async Task<HttpResponseMessage> SendAsync(HttpClient http, string token, string method = "tools/call") {
        using var request = McpRequest(
            method: method,
            token: token
        );

        return await http.SendAsync(
            request,
            Token
        );
    }

    // Law: a delegated challenge reaches HTTP and a newly issued token recovers, whether the downstream refuses the
    // caller before dispatch (the token exchange) or after it (continuous access evaluation on revocation). A refusal
    // after dispatch fails that call and challenges the subject's next request, of any method, that presents the
    // refused token or an older one, without dispatching it; another subject is unaffected, and a token issued since
    // the refusal is served.
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Theory]
    public async Task DelegatedChallengesReachHttpAndANewlyIssuedTokenRecovers(bool afterDispatch, bool claimsRequired) {
        await using var fixture = new RemoteMcpFixture();
        var revoked = fixture.Token(ageSeconds: 120);
        var older = fixture.Token(ageSeconds: 180);
        var host = new ChallengeHost(
            afterDispatch: afterDispatch,
            claims: (claimsRequired
                ? Claims
                : null)
        ) { Revoked = [revoked, older] };

        await fixture.StartAsync(
            Token,
            configure: builder => builder.Services.AddSingleton<RemoteMcpHost>(implementationInstance: host)
        );
        using var http = fixture.Http();

        if (afterDispatch) {
            using var refused = await SendAsync(
                http: http,
                token: revoked
            );

            Assert.Equal(
                HttpStatusCode.OK,
                refused.StatusCode
            );
            var body = await refused.Content.ReadAsStringAsync(cancellationToken: Token);

            Assert.Contains(
                actualString: body,
                expectedSubstring: "\"isError\":true"
            );
            Assert.Contains(
                actualString: body,
                expectedSubstring: "Sign in again"
            );
        }
        foreach (var (stale, method) in new[] { (revoked, "tools/call"), (older, "tools/call"), (revoked, "tools/list") }) {
            if (
                !afterDispatch &&
                (method != "tools/call")
            ) { continue; }
            using var challenged = await SendAsync(
                http: http,
                method: method,
                token: stale
            );

            AssertChallenge(
                claimsRequired: claimsRequired,
                response: challenged
            );
        }
        Assert.Equal(
            actual: host.Dispatched,
            expected: (afterDispatch
                ? 1
                : 0)
        );
        Assert.Equal(
            actual: host.Authorizations,
            expected: (afterDispatch
                ? 1
                : 2)
        );
        foreach (var fresh in new[] { fixture.Token(subject: "bob"), fixture.Token(ageSeconds: 60) }) {
            using var served = await SendAsync(
                http: http,
                token: fresh
            );

            Assert.Equal(
                HttpStatusCode.OK,
                served.StatusCode
            );
            Assert.Contains(
                "ready",
                await served.Content.ReadAsStringAsync(cancellationToken: Token)
            );
        }
        Assert.Equal(
            actual: host.Completed,
            expected: 2
        );
    }
    // Law: a delegated challenge is decided before dispatch, so it reaches HTTP however long the token exchange takes,
    // while a dispatched long call still gets its headers promptly. The exchange is held on a gate until a concurrent
    // dispatched call has received the headers the MCP transport flushes after its grace period; that call was sent
    // after the exchange began, so the exchange provably outlasted the grace period.
    [Fact]
    public async Task AChallengeFromAnExchangeThatOutlastsTheHeaderFlushStillReachesHttp() {
        await using var fixture = new RemoteMcpFixture();
        var token = fixture.Token();
        var host = new ChallengeHost(claims: Claims) { Gated = true, Revoked = [token] };

        await fixture.StartAsync(
            Token,
            configure: builder => builder.Services.AddSingleton<RemoteMcpHost>(implementationInstance: host)
        );
        using var http = fixture.Http(token: token);
        using var challengeRequest = McpRequest();
        var challenged = http.SendAsync(
            challengeRequest,
            HttpCompletionOption.ResponseHeadersRead,
            Token
        );

        await host.ExchangeEntered.Task.WaitAsync(cancellationToken: Token);
        using var longRequest = McpRequest(name: "long_service");
        using var running = await http.SendAsync(
            longRequest,
            HttpCompletionOption.ResponseHeadersRead,
            Token
        );

        Assert.Equal(
            HttpStatusCode.OK,
            running.StatusCode
        );
        await host.LongEntered.Task.WaitAsync(cancellationToken: Token);
        Assert.False(condition: challenged.IsCompleted);
        host.ReleaseExchange.SetResult();
        using var response = await challenged;

        AssertChallenge(
            claimsRequired: true,
            response: response
        );
        Assert.Equal(
            actual: host.Completed,
            expected: 0
        );
        host.ReleaseLong.SetResult();
        Assert.Contains(
            "finished",
            await running.Content.ReadAsStringAsync(cancellationToken: Token)
        );
    }
    [Fact]
    public async Task HeadlessDiscoveryUsesHostVocabularyAndRefusesUnadvertisedCapture() {
        await using var fixture = new RemoteMcpFixture();

        await fixture.StartAsync(
            Token,
            configure: builder => builder.Services.AddSingleton<RemoteMcpHost>(implementationInstance: new ChallengeHost(claims: null))
        );
        using var http = fixture.Http(token: fixture.Token());
        await using var client = await fixture.ClientAsync(
            http: http,
            revision: "2026-07-28",
            token: Token
        );
        var tools = await client.ListToolsAsync(cancellationToken: Token);

        Assert.DoesNotContain(
            collection: tools,
            filter: tool => (tool.Name == "puck_capture_frame")
        );
        var exec = Assert.Single(
            collection: tools,
            predicate: tool => (tool.Name == "puck_exec")
        );

        Assert.Contains(
            "world.state",
            exec.Description
        );
        Assert.DoesNotContain(
            "Operator",
            exec.Description
        );
        Assert.DoesNotContain(
            "Use help",
            exec.Description
        );
        Assert.NotNull(@object: await Record.ExceptionAsync(testCode: async () => await client.CallToolAsync(
            "puck_capture_frame",
            cancellationToken: Token
        )));
        Assert.Equal(
            actual: fixture.Opened,
            expected: 0
        );
    }
    [Fact]
    public async Task OneCallersSlowRequestsLeaveCapacityForAnotherCaller() {
        await using var fixture = new RemoteMcpFixture();

        await fixture.StartAsync(Token);
        using var aliceHttp = fixture.Http(token: fixture.Token());
        using var bobHttp = fixture.Http(token: fixture.Token("bob"));
        await using var alice = await fixture.ClientAsync(
            http: aliceHttp,
            revision: "2026-07-28",
            token: Token
        );
        await using var bob = await fixture.ClientAsync(
            http: bobHttp,
            revision: "2026-07-28",
            token: Token
        );
        var first = (await alice.CallToolAsync(
            "puck_attach",
            cancellationToken: Token
        )).StructuredContent!.Value.GetProperty(propertyName: "attachmentId").GetString();
        var second = (await alice.CallToolAsync(
            "puck_attach",
            cancellationToken: Token
        )).StructuredContent!.Value.GetProperty(propertyName: "attachmentId").GetString();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token: Token);

        // A client reads a call's result from the event stream before the server's request has left the pipeline,
        // so the attach calls above may still hold their per-subject leases; the two slow calls must find both free.
        await fixture.WhenInFlightAsync(
            count: 0,
            ct: Token
        );
        var pending = new[] { first, second }.Select(selector: id => alice.CallToolAsync(
            "puck_exec",
            new Dictionary<string, object?> { ["attachmentId"] = id, ["command"] = "wait" },
            cancellationToken: cancellation.Token
        ).AsTask()).ToArray();

        try {
            for (var i = 0; (i < 2); i++) {
                var entered = fixture.Entered.Reader.ReadAsync(cancellationToken: Token).AsTask();

                // A slow call that ends before it enters was refused; its own failure is the one to report.
                if (await Task.WhenAny(
                    task1: entered,
                    task2: Task.WhenAny(tasks: pending)
                ) != entered) { await await Task.WhenAny(tasks: pending); }
                await entered;
            }
            using var excess = McpRequest();
            using var refused = await aliceHttp.SendAsync(
                excess,
                Token
            );

            Assert.Equal(
                HttpStatusCode.TooManyRequests,
                refused.StatusCode
            );
            Assert.NotNull(@object: refused.Headers.RetryAfter);
            // Only the two slow calls may hold a lease of the host's four when another caller arrives.
            await fixture.WhenInFlightAsync(
                count: 2,
                ct: Token
            );
            Assert.False(condition: (await bob.CallToolAsync(
                "puck_attach",
                cancellationToken: Token
            )).IsError);
            await fixture.WhenInFlightAsync(
                count: 2,
                ct: Token
            );
            using var health = await bobHttp.GetAsync(
                "/healthz",
                Token
            );

            Assert.Equal(
                HttpStatusCode.OK,
                health.StatusCode
            );
        } finally {
            await cancellation.CancelAsync();
            foreach (var operation in pending) { await Record.ExceptionAsync(testCode: async () => await operation); }
        }
    }

    // Stands in for a downstream service that refuses the Revoked tokens: at the token exchange before dispatch, or,
    // with afterDispatch, only once the dispatched call presents the exchanged token, as revocation does.
    private sealed class ChallengeHost(string? claims, bool afterDispatch = false) : RemoteMcpHost {
        internal int Authorizations;
        internal int Completed;
        internal int Dispatched;
        internal bool Gated;

        internal HashSet<string> Revoked { get; init; } = [];
        internal TaskCompletionSource ExchangeEntered { get; } = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource LongEntered { get; } = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseExchange { get; } = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseLong { get; } = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool IsReady => true;
        public override IReadOnlyList<Tool> ServiceTools => [
            new() { Name = "test_service", InputSchema = JsonElement.Parse("""{"type":"object"}""") },
            new() { Name = "long_service", InputSchema = JsonElement.Parse("""{"type":"object"}""") },
        ];

        public override ValueTask<IControlSession> AttachAsync(RemoteMcpCaller caller, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public override async ValueTask<object?> AuthorizeAsync(RemoteMcpCaller caller, CallToolRequestParams request, CancellationToken cancellationToken) {
            if (request.Name != "test_service") { return null; }
            Interlocked.Increment(location: ref Authorizations);
            if (Gated) {
                ExchangeEntered.TrySetResult();
                await ReleaseExchange.Task.WaitAsync(cancellationToken: cancellationToken);
            }
            if (
                !afterDispatch &&
                Revoked.Contains(item: caller.UserAssertion)
            ) { throw new RemoteMcpAuthorizationException(claims: claims); }
            return null;
        }
        public override async ValueTask<CallToolResult> CallServiceAsync(RemoteMcpCaller caller, CallToolRequestParams request, CancellationToken cancellationToken) {
            if (request.Name == "long_service") {
                LongEntered.TrySetResult();
                await ReleaseLong.Task.WaitAsync(cancellationToken: cancellationToken);
                return new CallToolResult { Content = [new TextContentBlock { Text = "finished" }] };
            }
            Interlocked.Increment(location: ref Dispatched);
            if (
                afterDispatch &&
                Revoked.Contains(item: caller.UserAssertion)
            ) { throw new RemoteMcpAuthorizationException(claims: claims); }
            Interlocked.Increment(location: ref Completed);
            return new CallToolResult { Content = [new TextContentBlock { Text = "ready" }] };
        }
        public override ValueTask<ControlCapabilities> DescribeControlAsync(RemoteMcpCaller caller, CancellationToken cancellationToken) => ValueTask.FromResult(result: new ControlCapabilities("world.state - Read disclosed World state."));
    }
}
