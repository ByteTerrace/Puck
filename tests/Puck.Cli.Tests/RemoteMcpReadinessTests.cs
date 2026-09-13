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

    private static HttpRequestMessage ServiceRequest() {
        var request = new HttpRequestMessage(
            method: HttpMethod.Post,
            requestUri: "/mcp"
        );

        request.Headers.Accept.ParseAdd(input: "application/json, text/event-stream");
        request.Headers.Add(
            name: "MCP-Protocol-Version",
            value: "2026-07-28"
        );
        request.Headers.Add(
            name: "Mcp-Method",
            value: "tools/call"
        );
        request.Headers.Add(
            name: "Mcp-Name",
            value: "test_service"
        );
        request.Content = new StringContent(
            content: """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"test_service","_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientInfo":{"name":"raw-http","version":"1"},"io.modelcontextprotocol/clientCapabilities":{}}}}""",
            encoding: Encoding.UTF8,
            mediaType: "application/json"
        );
        return request;
    }

    [InlineData(true)]
    [InlineData(false)]
    [Theory]
    public async Task DelegatedChallengesReachHttpAndFreshAuthorizationCanRecover(bool claimsRequired) {
        await using var fixture = new RemoteMcpFixture();
        var host = new ChallengeHost(claims: (claimsRequired
            ? Claims
            : null));

        await fixture.StartAsync(
            Token,
            configure: builder => builder.Services.AddSingleton<RemoteMcpHost>(implementationInstance: host)
        );
        using var http = fixture.Http(token: fixture.Token());
        using var request = ServiceRequest();
        using var response = await http.SendAsync(
            request,
            Token
        );

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
        if (claimsRequired) { Assert.Contains(
            Convert.ToBase64String(inArray: Encoding.UTF8.GetBytes(s: Claims)),
            header
        ); }
        host.Authorized = true;
        http.DefaultRequestHeaders.Authorization = new(
            "Bearer",
            fixture.Token()
        );
        using var retry = ServiceRequest();
        using var recovered = await http.SendAsync(
            retry,
            Token
        );

        Assert.Equal(
            HttpStatusCode.OK,
            recovered.StatusCode
        );
        Assert.Equal(
            actual: host.Completed,
            expected: 1
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
        var pending = new[] { first, second }.Select(selector: id => alice.CallToolAsync(
            "puck_exec",
            new Dictionary<string, object?> { ["attachmentId"] = id, ["command"] = "wait" },
            cancellationToken: cancellation.Token
        ).AsTask()).ToArray();

        try {
            for (var i = 0; (i < 2); i++) { await fixture.Entered.Reader.ReadAsync(cancellationToken: Token).AsTask().WaitAsync(
                TimeSpan.FromSeconds(seconds: 5),
                Token
            ); }
            using var excess = ServiceRequest();
            using var refused = await aliceHttp.SendAsync(
                excess,
                Token
            );

            Assert.Equal(
                HttpStatusCode.TooManyRequests,
                refused.StatusCode
            );
            Assert.NotNull(@object: refused.Headers.RetryAfter);
            Assert.False(condition: (await bob.CallToolAsync(
                "puck_attach",
                cancellationToken: Token
            )).IsError);
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

    private sealed class ChallengeHost(string? claims) : RemoteMcpHost {
        internal bool Authorized;
        internal int Completed;

        public override bool IsReady => true;
        public override IReadOnlyList<Tool> ServiceTools => [new() { Name = "test_service", InputSchema = JsonElement.Parse("""{"type":"object"}""") }];

        public override ValueTask<IControlSession> AttachAsync(RemoteMcpCaller caller, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public override ValueTask<CallToolResult> CallServiceAsync(RemoteMcpCaller caller, CallToolRequestParams request, CancellationToken cancellationToken) {
            if (!Authorized) { throw new RemoteMcpAuthorizationException(claims: claims); }
            Completed++;
            return ValueTask.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = "ready" }] });
        }
        public override ValueTask<ControlCapabilities> DescribeControlAsync(RemoteMcpCaller caller, CancellationToken cancellationToken) => ValueTask.FromResult(new ControlCapabilities("world.state - Read disclosed World state."));
    }
}
