using System.Net;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Puck.Mcp;
using Xunit;

namespace Puck.Cli.Tests;

public sealed class RemoteMcpTests {
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static bool SupportedPlatform() { if ((OperatingSystem.IsWindows() || OperatingSystem.IsLinux() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64)) { return true; } Assert.Skip("Local capability checks support Windows and Linux x64."); return false; }

    [Fact]
    public async Task OfficialHttpClientPreservesIdentityAttachmentsAndImages() {
        if (!SupportedPlatform()) { return; }
        await using var fixture = new RemoteMcpFixture(); await fixture.StartAsync(Token);
        using var aliceHttp = fixture.Http(fixture.Token());
        using var bobHttp = fixture.Http(fixture.Token("bob"));
        await using var alice = await fixture.ClientAsync(aliceHttp, "2026-07-28", Token);
        await using var bob = await fixture.ClientAsync(bobHttp, "2026-07-28", Token);
        var tools = await alice.ListToolsAsync(cancellationToken: Token);
        Assert.Equal(["puck_attach", "puck_capture_frame", "puck_detach", "puck_exec"], tools.Select(tool => tool.Name).Order());
        var first = await Attach(alice); var second = await Attach(alice); var other = await Attach(bob);
        Assert.False((await Exec(alice, first, "set secret", Token)).IsError);
        Assert.Equal("secret", Output(await Exec(alice, first, "read", Token)));
        Assert.Equal("fresh", Output(await Exec(alice, second, "read", Token)));
        Assert.Equal("fresh", Output(await Exec(bob, other, "read", Token)));
        Assert.True((await Exec(bob, first, "read", Token)).IsError);
        Assert.True((await bob.CallToolAsync("puck_detach", new Dictionary<string, object?> { ["attachmentId"] = first }, cancellationToken: Token)).IsError);
        var image = await alice.CallToolAsync("puck_capture_frame", new Dictionary<string, object?> { ["attachmentId"] = first }, cancellationToken: Token);
        Assert.Single(image.Content.OfType<ImageContentBlock>());
        Assert.False((await alice.CallToolAsync("puck_detach", new Dictionary<string, object?> { ["attachmentId"] = first }, cancellationToken: Token)).IsError);
        Assert.True((await Exec(alice, first, "read", Token)).IsError);
        Assert.True(fixture.MetadataReads > 0); Assert.True(fixture.KeyReads > 0);
    }

    [Theory]
    [InlineData("missing", 401)] [InlineData("signature", 401)] [InlineData("issuer", 401)]
    [InlineData("audience", 401)] [InlineData("expired", 401)] [InlineData("future", 401)]
    [InlineData("scope", 403)] [InlineData("app-only", 403)] [InlineData("subject", 403)]
    [InlineData("tenant", 403)] [InlineData("duplicate-subject", 401)]
    public async Task InvalidTokensNeverReachWorld(string failure, int status) {
        if (!SupportedPlatform()) { return; }
        await using var fixture = new RemoteMcpFixture(); await fixture.StartAsync(Token);
        using var http = fixture.Http(failure == "missing" ? null : fixture.Token(failure == "subject" ? "mallory" : "alice", failure));
        using var response = await http.PostAsync("/mcp", Json("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""), Token);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(0, fixture.Opened);
        if (status == 401) { Assert.Contains("resource_metadata=\"https://mcp.example.test/.well-known/oauth-protected-resource/mcp\"", response.Headers.WwwAuthenticate.ToString()); }
        if (failure == "scope") { Assert.Contains("insufficient_scope", response.Headers.WwwAuthenticate.ToString()); }
    }

    [Fact]
    public async Task DiscoveryAndOriginHostChecksUseCanonicalPublicIdentity() {
        if (!SupportedPlatform()) { return; }
        await using var fixture = new RemoteMcpFixture(); await fixture.StartAsync(Token);
        using var http = fixture.Http();
        using var response = await http.GetAsync("/.well-known/oauth-protected-resource/mcp", Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var metadata = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        Assert.Equal(RemoteMcpFixture.Audience, metadata.RootElement.GetProperty("resource").GetString());
        Assert.Equal(RemoteMcpFixture.Issuer, metadata.RootElement.GetProperty("authorization_servers")[0].GetString());
        http.DefaultRequestHeaders.Host = "attacker.example.test";
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/.well-known/oauth-protected-resource/mcp", Token)).StatusCode);
        http.DefaultRequestHeaders.Host = "mcp.example.test";
        http.DefaultRequestHeaders.Add("Origin", "https://attacker.example.test");
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/.well-known/oauth-protected-resource/mcp", Token)).StatusCode);
        http.DefaultRequestHeaders.Remove("Origin");
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/mcp");
        preflight.Headers.Add("Origin", "https://mcp.example.test");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "authorization,mcp-protocol-version");
        using var allowed = await http.SendAsync(preflight, Token);
        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
        Assert.Equal("https://mcp.example.test", allowed.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task OversizedBodiesDoNotAllocateAnAttachment() {
        if (!SupportedPlatform()) { return; }
        await using var fixture = new RemoteMcpFixture(); await fixture.StartAsync(Token);
        using var http = fixture.Http(fixture.Token());
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
        using var response = await http.PostAsync("/mcp", Json(new string(' ', 65537)), Token);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, fixture.Opened);
    }

    [Fact]
    public async Task BusyCancellationAndIdleExpiryDoNotCrossAttachments() {
        if (!SupportedPlatform()) { return; }
        await using var fixture = new RemoteMcpFixture(); await fixture.StartAsync(Token);
        using var http = fixture.Http(fixture.Token());
        await using var client = await fixture.ClientAsync(http, "2026-07-28", Token);
        var first = await Attach(client); var second = await Attach(client);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var wait = Exec(client, first, "wait", cancel.Token);
        Assert.Equal("wait", await fixture.Entered.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token));
        Assert.True((await Exec(client, first, "read", Token)).IsError);
        Assert.False((await Exec(client, second, "read", Token)).IsError);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        await Eventually(() => Volatile.Read(ref fixture.Active) == 1);
        Assert.True((await Exec(client, first, "read", Token)).IsError);
        fixture.Clock.Advance(TimeSpan.FromSeconds(20));
        await Eventually(() => Volatile.Read(ref fixture.Active) == 0);
        Assert.True((await Exec(client, second, "read", Token)).IsError);
        Assert.False((await Exec(client, await Attach(client), "read", Token)).IsError);
    }

    [Fact]
    public async Task FourAttachmentCapacityAndGatewayShutdownReleaseWorldSlots() {
        if (!SupportedPlatform()) { return; }
        await using var fixture = new RemoteMcpFixture(); await fixture.StartAsync(Token);
        using var http = fixture.Http(fixture.Token());
        await using var client = await fixture.ClientAsync(http, "2026-07-28", Token);
        for (var i = 0; i < 4; i++) { await Attach(client); }
        Assert.True((await client.CallToolAsync("puck_attach", cancellationToken: Token)).IsError);
        Assert.Equal(4, fixture.Opened);
        await fixture.StopGatewayAsync(Token);
        await Eventually(() => Volatile.Read(ref fixture.Active) == 0);
    }

    [Fact]
    public async Task FourActiveHttpRequestsRefuseAdditionalWorkWithoutQueueing() {
        if (!SupportedPlatform()) { return; }
        await using var fixture = new RemoteMcpFixture(); await fixture.StartAsync(Token);
        using var http = fixture.Http(fixture.Token());
        await using var client = await fixture.ClientAsync(http, "2026-07-28", Token);
        var ids = new List<string>();
        for (var i = 0; i < 4; i++) { ids.Add(await Attach(client)); }
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var pending = ids.Select(id => Exec(client, id, "wait", cancel.Token)).ToArray();
        try {
            for (var i = 0; i < 4; i++) { await fixture.Entered.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token); }
            using var refused = await http.GetAsync("/.well-known/oauth-protected-resource/mcp", Token);
            Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
            Assert.Equal(4, fixture.Opened);
        } finally {
            cancel.Cancel();
            foreach (var request in pending) { await Record.ExceptionAsync(() => request); }
        }
        await Eventually(() => Volatile.Read(ref fixture.Active) == 0);
        using var recovered = await http.GetAsync("/.well-known/oauth-protected-resource/mcp", Token);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    [Fact]
    public void UnsafeDeploymentConfigurationFailsBeforeListening() {
        if (!SupportedPlatform()) { return; }
        // No server starts: validation precedes every builder/customization callback.
        var options = new RemoteMcpOptions { Target = "row", PublicUrl = RemoteMcpFixture.Audience, ListenUrl = "http://127.0.0.1:8080", Issuer = RemoteMcpFixture.Issuer, Audience = "api", Scope = "user_impersonation", AllowedSubjects = ["alice"] };
        Assert.Throws<ArgumentException>(() => RemoteMcpServer.Build(options with { ListenUrl = "http://0.0.0.0:8080" }));
        Assert.Throws<ArgumentException>(() => RemoteMcpServer.Build(options with { PublicUrl = "http://mcp.example.test/mcp" }));
        Assert.Throws<ArgumentException>(() => RemoteMcpServer.Build(options with { Issuer = "http://issuer.example.test" }));
        Assert.Throws<ArgumentException>(() => RemoteMcpServer.Build(options with { AllowedSubjects = [" "] }));
        Assert.Throws<ArgumentException>(() => RemoteMcpServer.Build(options with { SubjectClaim = "oid" }));
        Assert.Throws<ArgumentException>(() => RemoteMcpServer.Build(options with { AllowedOrigins = ["*"] }));
    }

    private static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");
    private static async Task<string> Attach(McpClient client) {
        var result = await client.CallToolAsync("puck_attach", cancellationToken: Token);
        Assert.False(result.IsError, result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text);
        return result.StructuredContent!.Value.GetProperty("attachmentId").GetString()!;
    }
    private static Task<CallToolResult> Exec(McpClient client, string id, string command) => Exec(client, id, command, Token);
    private static Task<CallToolResult> Exec(McpClient client, string id, string command, CancellationToken token) => client.CallToolAsync("puck_exec", new Dictionary<string, object?> { ["attachmentId"] = id, ["command"] = command }, cancellationToken: token).AsTask();
    private static string? Output(CallToolResult result) => result.StructuredContent!.Value.GetProperty("output").GetString();
    private static async Task Eventually(Func<bool> predicate) {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token); stop.CancelAfter(TimeSpan.FromSeconds(5));
        while (!predicate()) { await Task.Delay(10, stop.Token); }
    }
}
