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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DelegatedChallengesReachHttpAndFreshAuthorizationCanRecover(bool claimsRequired) {
        await using var fixture = new RemoteMcpFixture();
        var host = new ChallengeHost(claimsRequired ? Claims : null);
        await fixture.StartAsync(Token, configure: builder => builder.Services.AddSingleton<RemoteMcpHost>(host));
        using var http = fixture.Http(fixture.Token());
        using var request = ServiceRequest();
        using var response = await http.SendAsync(request, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var header = response.Headers.WwwAuthenticate.ToString();
        Assert.Contains("resource_metadata=\"https://mcp.example.test/.well-known/oauth-protected-resource/mcp\"", header);
        Assert.Contains("scope=\"user_impersonation\"", header);
        Assert.DoesNotContain("management.azure.com", header);
        if (claimsRequired) { Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(Claims)), header); }
        host.Authorized = true;
        http.DefaultRequestHeaders.Authorization = new("Bearer", fixture.Token());
        using var retry = ServiceRequest();
        using var recovered = await http.SendAsync(retry, Token);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(1, host.Completed);
    }

    [Fact]
    public async Task HeadlessDiscoveryUsesHostVocabularyAndRefusesUnadvertisedCapture() {
        await using var fixture = new RemoteMcpFixture();
        await fixture.StartAsync(Token, configure: builder => builder.Services.AddSingleton<RemoteMcpHost>(new ChallengeHost(null)));
        using var http = fixture.Http(fixture.Token());
        await using var client = await fixture.ClientAsync(http, "2026-07-28", Token);
        var tools = await client.ListToolsAsync(cancellationToken: Token);
        Assert.DoesNotContain(tools, tool => tool.Name == "puck_capture_frame");
        var exec = Assert.Single(tools, tool => tool.Name == "puck_exec");
        Assert.Contains("world.state", exec.Description);
        Assert.DoesNotContain("Operator", exec.Description);
        Assert.DoesNotContain("Use help", exec.Description);
        Assert.NotNull(await Record.ExceptionAsync(async () => await client.CallToolAsync("puck_capture_frame", cancellationToken: Token)));
        Assert.Equal(0, fixture.Opened);
    }

    [Fact]
    public async Task OneCallersSlowRequestsLeaveCapacityForAnotherCaller() {
        await using var fixture = new RemoteMcpFixture();
        await fixture.StartAsync(Token);
        using var aliceHttp = fixture.Http(fixture.Token());
        using var bobHttp = fixture.Http(fixture.Token("bob"));
        await using var alice = await fixture.ClientAsync(aliceHttp, "2026-07-28", Token);
        await using var bob = await fixture.ClientAsync(bobHttp, "2026-07-28", Token);
        var first = (await alice.CallToolAsync("puck_attach", cancellationToken: Token)).StructuredContent!.Value.GetProperty("attachmentId").GetString();
        var second = (await alice.CallToolAsync("puck_attach", cancellationToken: Token)).StructuredContent!.Value.GetProperty("attachmentId").GetString();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var pending = new[] { first, second }.Select(id => alice.CallToolAsync("puck_exec", new Dictionary<string, object?> { ["attachmentId"] = id, ["command"] = "wait" }, cancellationToken: cancellation.Token).AsTask()).ToArray();
        try {
            for (var i = 0; i < 2; i++) { await fixture.Entered.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token); }
            using var excess = ServiceRequest();
            using var refused = await aliceHttp.SendAsync(excess, Token);
            Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
            Assert.NotNull(refused.Headers.RetryAfter);
            Assert.False((await bob.CallToolAsync("puck_attach", cancellationToken: Token)).IsError);
            using var health = await bobHttp.GetAsync("/healthz", Token);
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        } finally {
            await cancellation.CancelAsync();
            foreach (var operation in pending) { await Record.ExceptionAsync(async () => await operation); }
        }
    }

    private static HttpRequestMessage ServiceRequest() {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
        request.Headers.Add("Mcp-Method", "tools/call");
        request.Headers.Add("Mcp-Name", "test_service");
        request.Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"test_service","_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientInfo":{"name":"raw-http","version":"1"},"io.modelcontextprotocol/clientCapabilities":{}}}}""", Encoding.UTF8, "application/json");
        return request;
    }
    private sealed class ChallengeHost(string? claims) : RemoteMcpHost {
        internal bool Authorized;
        internal int Completed;
        public override bool IsReady => true;
        public override ValueTask<ControlCapabilities> DescribeControlAsync(RemoteMcpCaller caller, CancellationToken cancellationToken) => ValueTask.FromResult(new ControlCapabilities("world.state - Read disclosed World state."));
        public override ValueTask<IControlSession> AttachAsync(RemoteMcpCaller caller, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public override IReadOnlyList<Tool> ServiceTools => [new() { Name = "test_service", InputSchema = JsonElement.Parse("""{"type":"object"}""") }];
        public override ValueTask<CallToolResult> CallServiceAsync(RemoteMcpCaller caller, CallToolRequestParams request, CancellationToken cancellationToken) {
            if (!Authorized) { throw new RemoteMcpAuthorizationException(claims); }
            Completed++;
            return ValueTask.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = "ready" }] });
        }
    }
}
