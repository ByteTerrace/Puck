using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Puck.Cli.Tests;

public sealed class RemoteMcpProtocolTests {
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static bool SupportedPlatform() { if ((OperatingSystem.IsWindows() || OperatingSystem.IsLinux() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64)) { return true; } Assert.Skip("Local capability checks support Windows and Linux x64."); return false; }

    [Fact]
    public async Task DirectTlsAndEntraClaimsUseQualifiedAuthorizationScope() {
        if (!SupportedPlatform()) { return; }
        await using var fixture = new RemoteMcpFixture(); await fixture.StartAsync(Token, tls: true, entra: true);
        using var http = fixture.Http(fixture.Token(failure: "entra"));
        await using var client = await fixture.ClientAsync(http, "2026-07-28", Token);
        Assert.False((await client.CallToolAsync("puck_attach", cancellationToken: Token)).IsError);
        using var response = await http.GetAsync("/.well-known/oauth-protected-resource/mcp", Token);
        using var metadata = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        Assert.Equal("api://test-api/user_impersonation", metadata.RootElement.GetProperty("scopes_supported")[0].GetString());
    }

    [Fact]
    public async Task TokenExpiryCancelsActiveWorkAndReauthenticationCannotReviveHandle() {
        if (!SupportedPlatform()) { return; }
        await using var fixture = new RemoteMcpFixture(); await fixture.StartAsync(Token);
        using var http = fixture.Http(fixture.Token());
        await using var client = await fixture.ClientAsync(http, "2026-07-28", Token);
        var attached = await client.CallToolAsync("puck_attach", cancellationToken: Token);
        var id = attached.StructuredContent!.Value.GetProperty("attachmentId").GetString();
        http.DefaultRequestHeaders.Authorization = new("Bearer", fixture.Token(lifetimeSeconds: 3));
        var arguments = new Dictionary<string, object?> { ["attachmentId"] = id, ["command"] = "wait" };
        var wait = client.CallToolAsync("puck_exec", arguments, cancellationToken: Token).AsTask();
        await fixture.Entered.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.NotNull(await Record.ExceptionAsync(() => wait.WaitAsync(TimeSpan.FromSeconds(8), Token)));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        while (Volatile.Read(ref fixture.Active) != 0) { await Task.Delay(10, deadline.Token); }
        http.DefaultRequestHeaders.Authorization = new("Bearer", fixture.Token());
        arguments["command"] = "read";
        Assert.True((await client.CallToolAsync("puck_exec", arguments, cancellationToken: Token)).IsError);
    }

    [Theory]
    [InlineData("method", 400)] [InlineData("version", 400)] [InlineData("missing", 400)]
    [InlineData("unknown", 404)]
    public async Task LatestProtocolValidatesMetadataBeforeDispatch(string failure, int status) {
        if (!SupportedPlatform()) { return; }
        await using var fixture = new RemoteMcpFixture(); await fixture.StartAsync(Token);
        using var http = fixture.Http(fixture.Token());
        var method = failure == "unknown" ? "absent/method" : "tools/list";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        if (failure != "missing") { request.Headers.Add("MCP-Protocol-Version", failure == "version" ? "2099-01-01" : "2026-07-28"); }
        request.Headers.Add("Mcp-Method", failure == "method" ? "tools/call" : method);
        var body = """{"jsonrpc":"2.0","id":1,"method":"METHOD","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientInfo":{"name":"raw-http","version":"1"},"io.modelcontextprotocol/clientCapabilities":{}}}}""".Replace("METHOD", method, StringComparison.Ordinal);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, Token);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.False(response.Headers.Contains("Mcp-Session-Id"));
        var payload = await response.Content.ReadAsStringAsync(Token);
        // The protocol permits both JSON and request-scoped SSE responses, including RPC errors.
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream") { payload = payload.Split('\n').Single(line => line.StartsWith("data:", StringComparison.Ordinal))[5..]; }
        using var result = JsonDocument.Parse(payload);
        Assert.True(result.RootElement.TryGetProperty("error", out _));
        Assert.Equal(0, fixture.Opened);
        using var get = await http.GetAsync("/mcp", Token);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
        using var delete = await http.DeleteAsync("/mcp", Token);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, delete.StatusCode);
    }
}
