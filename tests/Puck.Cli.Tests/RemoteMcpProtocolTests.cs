using System.Net;
using System.Text;
using System.Text.Json;
using Puck.Hosting;
using Xunit;

namespace Puck.Cli.Tests;

public sealed class RemoteMcpProtocolTests {
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static bool SupportedPlatform() { if ((OperatingSystem.IsWindows() || (OperatingSystem.IsLinux() && (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64)))) { return true; } Assert.Skip(reason: "Local capability checks support Windows and Linux x64."); return false; }

    [Fact]
    public async Task DirectTlsAndEntraClaimsUseQualifiedAuthorizationScope() {
        if (!SupportedPlatform()) { return; }
        await using var fixture = new RemoteMcpFixture(); await fixture.StartAsync(
            Token,
            tls: true,
            entra: true
        );
        using var http = fixture.Http(token: fixture.Token(failure: "entra"));
        await using var client = await fixture.ClientAsync(
            http: http,
            revision: "2026-07-28",
            token: Token
        );

        Assert.False(condition: (await client.CallToolAsync(
            "puck_attach",
            cancellationToken: Token
        )).IsError);
        using var response = await http.GetAsync(
            "/.well-known/oauth-protected-resource/mcp",
            Token
        );
        using var metadata = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken: Token));

        Assert.Equal(
            "api://test-api/user_impersonation",
            metadata.RootElement.GetProperty(propertyName: "scopes_supported")[0].GetString()
        );
    }
    [InlineData("method", 400)]
    [InlineData("version", 400)]
    [InlineData("missing", 400)]
    [InlineData("unknown", 404)]
    [Theory]
    public async Task LatestProtocolValidatesMetadataBeforeDispatch(string failure, int status) {
        if (!SupportedPlatform()) { return; }
        await using var fixture = new RemoteMcpFixture(); await fixture.StartAsync(Token);
        using var http = fixture.Http(token: fixture.Token());
        var method = ((failure == "unknown")
            ? "absent/method"
            : "tools/list"
        );
        using var request = new HttpRequestMessage(
            method: HttpMethod.Post,
            requestUri: "/mcp"
        );

        request.Headers.Accept.ParseAdd(input: "application/json, text/event-stream");
        if (failure != "missing") {
            request.Headers.Add(
            name: "MCP-Protocol-Version",
            value: ((failure == "version")
            ? "2099-01-01"
            : "2026-07-28")
        );
        }
        request.Headers.Add(
            name: "Mcp-Method",
            value: ((failure == "method")
            ? "tools/call"
            : method)
        );
        var body = """{"jsonrpc":"2.0","id":1,"method":"METHOD","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientInfo":{"name":"raw-http","version":"1"},"io.modelcontextprotocol/clientCapabilities":{}}}}""".Replace(
            comparisonType: StringComparison.Ordinal,
            newValue: method,
            oldValue: "METHOD"
        );

        request.Content = new StringContent(
            content: body,
            encoding: Encoding.UTF8,
            mediaType: "application/json"
        );
        using var response = await http.SendAsync(
            request,
            Token
        );

        Assert.Equal(
            status,
            ((int)response.StatusCode)
        );
        Assert.False(condition: response.Headers.Contains(name: "Mcp-Session-Id"));
        var payload = await response.Content.ReadAsStringAsync(cancellationToken: Token);
        // The protocol permits both JSON and request-scoped SSE responses, including RPC errors.
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream") {
            payload = payload.Split('\n').Single(predicate: line => line.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: "data:"
        ))[5..];
        }
        using var result = JsonDocument.Parse(payload);

        Assert.True(condition: result.RootElement.TryGetProperty(
            propertyName: "error",
            value: out _
        ));
        Assert.Equal(
            actual: fixture.Opened,
            expected: 0
        );
        using var get = await http.GetAsync(
            "/mcp",
            Token
        );

        Assert.Equal(
            HttpStatusCode.MethodNotAllowed,
            get.StatusCode
        );
        using var delete = await http.DeleteAsync(
            "/mcp",
            Token
        );

        Assert.Equal(
            HttpStatusCode.MethodNotAllowed,
            delete.StatusCode
        );
    }
    [Fact]
    public async Task TokenExpiryCancelsActiveWorkAndReauthenticationCannotReviveHandle() {
        if (!SupportedPlatform()) { return; }
        await using var fixture = new RemoteMcpFixture(); await fixture.StartAsync(Token);
        using var http = fixture.Http(token: fixture.Token());
        await using var client = await fixture.ClientAsync(
            http: http,
            revision: "2026-07-28",
            token: Token
        );
        var attached = await client.CallToolAsync(
            "puck_attach",
            cancellationToken: Token
        );
        var id = attached.StructuredContent!.Value.GetProperty(propertyName: "attachmentId").GetString();

        // Shorter than every other bound the request carries — the exec timeout it names and the gateway's request
        // deadline — so the token's own remaining lifetime is the deadline that ends it.
        var lifetime = TimeSpan.FromSeconds(seconds: 110);

        http.DefaultRequestHeaders.Authorization = new(
            "Bearer",
            fixture.Token(lifetimeSeconds: ((int)lifetime.TotalSeconds))
        );
        var arguments = new Dictionary<string, object?> { ["attachmentId"] = id, ["command"] = "wait", ["timeoutMs"] = ControlLimits.TimeoutMilliseconds };
        var wait = client.CallToolAsync(
            "puck_exec",
            arguments,
            cancellationToken: Token
        ).AsTask();

        await fixture.Entered.Reader.ReadAsync(cancellationToken: Token);
        await fixture.Clock.ExpireAsync(
            ct: Token,
            dueTime: lifetime,
            pending: wait
        );
        Assert.NotNull(@object: await Record.ExceptionAsync(testCode: () => wait));
        await fixture.WhenAsync(
            condition: () => (fixture.Active == 0),
            ct: Token
        );
        http.DefaultRequestHeaders.Authorization = new(
            "Bearer",
            fixture.Token()
        );
        arguments["command"] = "read";
        Assert.True(condition: (await client.CallToolAsync(
            "puck_exec",
            arguments,
            cancellationToken: Token
        )).IsError);
    }
}
