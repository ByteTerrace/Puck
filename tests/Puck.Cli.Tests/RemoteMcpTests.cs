using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Puck.Mcp;
using Xunit;

namespace Puck.Cli.Tests;

public sealed class RemoteMcpTests {
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<string> Attach(McpClient client) {
        var result = await client.CallToolAsync(
            "puck_attach",
            cancellationToken: Token
        );

        Assert.False(
            condition: result.IsError,
            userMessage: result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text
        );
        return result.StructuredContent!.Value.GetProperty(propertyName: "attachmentId").GetString()!;
    }
    private static async Task Eventually(Func<bool> predicate) {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token: Token); stop.CancelAfter(delay: TimeSpan.FromSeconds(seconds: 5));
        while (!predicate()) { await Task.Delay(
            10,
            stop.Token
        ); }
    }
    private static Task<CallToolResult> Exec(McpClient client, string id, string command) => Exec(
        client,
        id,
        command,
        Token
    );
    private static Task<CallToolResult> Exec(McpClient client, string id, string command, CancellationToken token) => client.CallToolAsync(
        "puck_exec",
        new Dictionary<string, object?> { ["attachmentId"] = id, ["command"] = command },
        cancellationToken: token
    ).AsTask();
    private static StringContent Json(string value) => new(
        content: value,
        encoding: Encoding.UTF8,
        mediaType: "application/json"
    );
    private static string? Output(CallToolResult result) => result.StructuredContent!.Value.GetProperty(propertyName: "output").GetString();
    private static bool SupportedPlatform() { if ((OperatingSystem.IsWindows() || (OperatingSystem.IsLinux() && (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64)))) { return true; } Assert.Skip(reason: "Local capability checks support Windows and Linux x64."); return false; }

    [Fact]
    public async Task BusyCancellationAndIdleExpiryDoNotCrossAttachments() {
        if (!SupportedPlatform()) { return; }
        await using var fixture = new RemoteMcpFixture(); await fixture.StartAsync(Token);
        using var http = fixture.Http(token: fixture.Token());
        await using var client = await fixture.ClientAsync(
            http: http,
            revision: "2026-07-28",
            token: Token
        );
        var first = await Attach(client: client); var second = await Attach(client: client);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token: Token);
        var wait = Exec(
            client,
            first,
            "wait",
            cancel.Token
        );

        Assert.Equal(
            "wait",
            await fixture.Entered.Reader.ReadAsync(cancellationToken: Token).AsTask().WaitAsync(
                TimeSpan.FromSeconds(seconds: 5),
                Token
            )
        );
        Assert.True(condition: (await Exec(
            client,
            first,
            "read",
            Token
        )).IsError);
        Assert.False(condition: (await Exec(
            client,
            second,
            "read",
            Token
        )).IsError);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(testCode: () => wait);
        await Eventually(predicate: () => (Volatile.Read(location: ref fixture.Active) == 1));
        Assert.True(condition: (await Exec(
            client,
            first,
            "read",
            Token
        )).IsError);
        fixture.Clock.Advance(time: TimeSpan.FromSeconds(seconds: 20));
        await Eventually(predicate: () => (Volatile.Read(location: ref fixture.Active) == 0));
        Assert.True(condition: (await Exec(
            client,
            second,
            "read",
            Token
        )).IsError);
        Assert.False(condition: (await Exec(
            client,
            await Attach(client: client),
            "read",
            Token
        )).IsError);
    }
    [Fact]
    public async Task DiscoveryAndOriginHostChecksUseCanonicalPublicIdentity() {
        if (!SupportedPlatform()) { return; }
        await using var fixture = new RemoteMcpFixture(); await fixture.StartAsync(Token);
        using var http = fixture.Http();
        using var response = await http.GetAsync(
            "/.well-known/oauth-protected-resource/mcp",
            Token
        );

        Assert.Equal(
            HttpStatusCode.OK,
            response.StatusCode
        );
        using var metadata = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken: Token));

        Assert.Equal(
            RemoteMcpFixture.Audience,
            metadata.RootElement.GetProperty(propertyName: "resource").GetString()
        );
        Assert.Equal(
            RemoteMcpFixture.Issuer,
            metadata.RootElement.GetProperty(propertyName: "authorization_servers")[0].GetString()
        );
        http.DefaultRequestHeaders.Host = "attacker.example.test";
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await http.GetAsync(
                "/.well-known/oauth-protected-resource/mcp",
                Token
            )).StatusCode
        );
        http.DefaultRequestHeaders.Host = "mcp.example.test";
        http.DefaultRequestHeaders.Add(
            name: "Origin",
            value: "https://attacker.example.test"
        );
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await http.GetAsync(
                "/.well-known/oauth-protected-resource/mcp",
                Token
            )).StatusCode
        );
        http.DefaultRequestHeaders.Remove(name: "Origin");
        using var preflight = new HttpRequestMessage(
            method: HttpMethod.Options,
            requestUri: "/mcp"
        );

        preflight.Headers.Add(
            name: "Origin",
            value: "https://mcp.example.test"
        );
        preflight.Headers.Add(
            name: "Access-Control-Request-Method",
            value: "POST"
        );
        preflight.Headers.Add(
            name: "Access-Control-Request-Headers",
            value: "authorization,mcp-protocol-version"
        );
        using var allowed = await http.SendAsync(
            preflight,
            Token
        );

        Assert.Equal(
            HttpStatusCode.NoContent,
            allowed.StatusCode
        );
        Assert.Equal(
            "https://mcp.example.test",
            allowed.Headers.GetValues(name: "Access-Control-Allow-Origin").Single()
        );
    }
    [Fact]
    public async Task FourActiveHttpRequestsRefuseAdditionalWorkWithoutQueueing() {
        if (!SupportedPlatform()) { return; }
        await using var fixture = new RemoteMcpFixture(); await fixture.StartAsync(Token);
        using var http = fixture.Http(token: fixture.Token());
        await using var client = await fixture.ClientAsync(
            http: http,
            revision: "2026-07-28",
            token: Token
        );
        var ids = new List<string>();

        for (var i = 0; (i < 2); i++) { ids.Add(item: await Attach(client: client)); }
        using var bobHttp = fixture.Http(token: fixture.Token("bob"));
        await using var bob = await fixture.ClientAsync(
            http: bobHttp,
            revision: "2026-07-28",
            token: Token
        );
        var bobIds = new[] { await Attach(client: bob), await Attach(client: bob) };
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token: Token);
        var pending = ids.Select(selector: id => Exec(
            client,
            id,
            "wait",
            cancel.Token
        )).Concat(second: bobIds.Select(selector: id => Exec(
            bob,
            id,
            "wait",
            cancel.Token
        ))).ToArray();

        try {
            for (var i = 0; (i < 4); i++) { await fixture.Entered.Reader.ReadAsync(cancellationToken: Token).AsTask().WaitAsync(
                TimeSpan.FromSeconds(seconds: 5),
                Token
            ); }
            using var refused = await http.GetAsync(
                "/.well-known/oauth-protected-resource/mcp",
                Token
            );

            Assert.Equal(
                HttpStatusCode.TooManyRequests,
                refused.StatusCode
            );
            Assert.Equal(
                actual: fixture.Opened,
                expected: 4
            );
        } finally {
            cancel.Cancel();
            foreach (var request in pending) { await Record.ExceptionAsync(testCode: () => request); }
        }
        await Eventually(predicate: () => (Volatile.Read(location: ref fixture.Active) == 0));
        await Eventually(predicate: () => (fixture.App.Services.GetRequiredKeyedService<System.Threading.RateLimiting.ConcurrencyLimiter>(serviceKey: "PuckMcp").GetStatistics()!.CurrentAvailablePermits == 4));
        using var recovered = await http.GetAsync(
            "/.well-known/oauth-protected-resource/mcp",
            Token
        );

        Assert.Equal(
            HttpStatusCode.OK,
            recovered.StatusCode
        );
    }
    [Fact]
    public async Task FourAttachmentCapacityAndGatewayShutdownReleaseWorldSlots() {
        if (!SupportedPlatform()) { return; }
        await using var fixture = new RemoteMcpFixture(); await fixture.StartAsync(Token);
        using var http = fixture.Http(token: fixture.Token());
        await using var client = await fixture.ClientAsync(
            http: http,
            revision: "2026-07-28",
            token: Token
        );

        for (var i = 0; (i < 2); i++) { await Attach(client: client); }
        Assert.True(condition: (await client.CallToolAsync(
            "puck_attach",
            cancellationToken: Token
        )).IsError);
        using var bobHttp = fixture.Http(token: fixture.Token("bob"));
        await using var bob = await fixture.ClientAsync(
            http: bobHttp,
            revision: "2026-07-28",
            token: Token
        );

        for (var i = 0; (i < 2); i++) { await Attach(client: bob); }
        Assert.True(condition: (await client.CallToolAsync(
            "puck_attach",
            cancellationToken: Token
        )).IsError);
        Assert.Equal(
            actual: fixture.Opened,
            expected: 4
        );
        await fixture.StopGatewayAsync(token: Token);
        await Eventually(predicate: () => (Volatile.Read(location: ref fixture.Active) == 0));
    }
    [InlineData("missing", 401)]
    [InlineData("signature", 401)]
    [InlineData("issuer", 401)]
    [InlineData("audience", 401)]
    [InlineData("expired", 401)]
    [InlineData("future", 401)]
    [InlineData("scope", 403)]
    [InlineData("app-only", 403)]
    [InlineData("subject", 403)]
    [InlineData("tenant", 403)]
    [InlineData("duplicate-subject", 401)]
    [Theory]
    public async Task InvalidTokensNeverReachWorld(string failure, int status) {
        if (!SupportedPlatform()) { return; }
        await using var fixture = new RemoteMcpFixture(); await fixture.StartAsync(Token);
        using var http = fixture.Http(token: ((failure == "missing")
            ? null
            : fixture.Token(
                ((failure == "subject")
                ? "mallory"
                : "alice"),
                failure
            )));
        using var response = await http.PostAsync(
            "/mcp",
            Json(value: """{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""),
            Token
        );

        Assert.Equal(
            status,
            ((int)response.StatusCode)
        );
        Assert.Equal(
            actual: fixture.Opened,
            expected: 0
        );
        if (status == 401) { Assert.Contains(
            "resource_metadata=\"https://mcp.example.test/.well-known/oauth-protected-resource/mcp\"",
            response.Headers.WwwAuthenticate.ToString()
        ); }
        if (failure == "scope") { Assert.Contains(
            "insufficient_scope",
            response.Headers.WwwAuthenticate.ToString()
        ); }
    }
    [Fact]
    public async Task OfficialHttpClientPreservesIdentityAttachmentsAndImages() {
        if (!SupportedPlatform()) { return; }
        await using var fixture = new RemoteMcpFixture(); await fixture.StartAsync(Token);
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
        var tools = await alice.ListToolsAsync(cancellationToken: Token);

        Assert.Equal(
            ["puck_attach", "puck_capture_frame", "puck_detach", "puck_exec"],
            tools.Select(selector: tool => tool.Name).Order()
        );
        var first = await Attach(client: alice); var second = await Attach(client: alice); var other = await Attach(client: bob);

        Assert.False(condition: (await Exec(
            alice,
            first,
            "set secret",
            Token
        )).IsError);
        Assert.Equal(
            "secret",
            Output(result: await Exec(
                alice,
                first,
                "read",
                Token
            ))
        );
        Assert.Equal(
            "fresh",
            Output(result: await Exec(
                alice,
                second,
                "read",
                Token
            ))
        );
        Assert.Equal(
            "fresh",
            Output(result: await Exec(
                bob,
                other,
                "read",
                Token
            ))
        );
        Assert.True(condition: (await Exec(
            bob,
            first,
            "read",
            Token
        )).IsError);
        Assert.True(condition: (await bob.CallToolAsync(
            "puck_detach",
            new Dictionary<string, object?> { ["attachmentId"] = first },
            cancellationToken: Token
        )).IsError);
        var image = await alice.CallToolAsync(
            "puck_capture_frame",
            new Dictionary<string, object?> { ["attachmentId"] = first },
            cancellationToken: Token
        );

        Assert.Single(collection: image.Content.OfType<ImageContentBlock>());
        Assert.False(condition: (await alice.CallToolAsync(
            "puck_detach",
            new Dictionary<string, object?> { ["attachmentId"] = first },
            cancellationToken: Token
        )).IsError);
        Assert.True(condition: (await Exec(
            alice,
            first,
            "read",
            Token
        )).IsError);
        Assert.True(condition: (fixture.MetadataReads > 0)); Assert.True(condition: (fixture.KeyReads > 0));
    }
    [Fact]
    public async Task OversizedBodiesDoNotAllocateAnAttachment() {
        if (!SupportedPlatform()) { return; }
        await using var fixture = new RemoteMcpFixture(); await fixture.StartAsync(Token);
        using var http = fixture.Http(token: fixture.Token());

        http.DefaultRequestHeaders.Accept.ParseAdd(input: "application/json, text/event-stream");
        using var response = await http.PostAsync(
            "/mcp",
            Json(value: new string(
                c: ' ',
                count: 65537
            )),
            Token
        );

        Assert.Equal(
            HttpStatusCode.RequestEntityTooLarge,
            response.StatusCode
        );
        Assert.Equal(
            actual: fixture.Opened,
            expected: 0
        );
    }
    [Fact]
    public void UnsafeDeploymentConfigurationFailsBeforeListening() {
        if (!SupportedPlatform()) { return; }
        // No server starts: validation precedes every builder/customization callback.
        var options = new RemoteMcpOptions { AllowedSubjects = ["alice"], Audience = "api", Issuer = RemoteMcpFixture.Issuer, ListenUrl = "http://127.0.0.1:8080", PublicUrl = RemoteMcpFixture.Audience, Scope = "user_impersonation", Target = "row" };

        Assert.Throws<ArgumentException>(testCode: () => RemoteMcpServer.Build(options with { ListenUrl = "http://0.0.0.0:8080" }));
        Assert.Throws<ArgumentException>(testCode: () => RemoteMcpServer.Build(options with { PublicUrl = "http://mcp.example.test/mcp" }));
        Assert.Throws<ArgumentException>(testCode: () => RemoteMcpServer.Build(options with { Issuer = "http://issuer.example.test" }));
        Assert.Throws<ArgumentException>(testCode: () => RemoteMcpServer.Build(options with { AllowedSubjects = [" "] }));
        Assert.Throws<ArgumentException>(testCode: () => RemoteMcpServer.Build(options with { SubjectClaim = "oid" }));
        Assert.Throws<ArgumentException>(testCode: () => RemoteMcpServer.Build(options with { AllowedOrigins = ["*"] }));
    }
    [Fact]
    public async Task StateVectorWrite_GrantedPrincipalSucceedsAndUngrantedIsRefused() {
        if (!SupportedPlatform()) { return; }
        await using var fixture = new RemoteMcpFixture();
        fixture.CommandHelp = "read; set <value>; wait; world.state.cell.set <row> <key> <value>";
        fixture.GrantedPrincipals = ["alice"];
        await fixture.StartAsync(Token);

        using var aliceHttp = fixture.Http(token: fixture.Token(subject: "alice"));
        using var bobHttp = fixture.Http(token: fixture.Token(subject: "bob"));
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

        var tools = await alice.ListToolsAsync(cancellationToken: Token);
        Assert.Contains("puck_state_vector_write", tools.Select(tool => tool.Name));

        var aliceAttachment = await Attach(client: alice);
        var bobAttachment = await Attach(client: bob);

        var aliceResult = await alice.CallToolAsync(
            "puck_state_vector_write",
            new Dictionary<string, object?> {
                ["attachmentId"] = aliceAttachment,
                ["row"] = "embedding",
                ["key"] = "cell1",
                ["vector"] = "b64u:AQID"
            },
            cancellationToken: Token
        );
        Assert.False(condition: aliceResult.IsError, userMessage: aliceResult.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text);
        Assert.Equal(
            expected: "world.state.cell.set embedding cell1 b64u:AQID",
            actual: Output(result: aliceResult)
        );

        var aliceSlotResult = await alice.CallToolAsync(
            "puck_state_vector_write",
            new Dictionary<string, object?> {
                ["attachmentId"] = aliceAttachment,
                ["row"] = "slot_embedding",
                ["vector"] = "b64u:AQID"
            },
            cancellationToken: Token
        );
        Assert.False(condition: aliceSlotResult.IsError);
        Assert.Equal(
            expected: "world.state.cell.set slot_embedding $value b64u:AQID",
            actual: Output(result: aliceSlotResult)
        );

        var bobResult = await bob.CallToolAsync(
            "puck_state_vector_write",
            new Dictionary<string, object?> {
                ["attachmentId"] = bobAttachment,
                ["row"] = "embedding",
                ["key"] = "cell1",
                ["vector"] = "b64u:AQID"
            },
            cancellationToken: Token
        );
        Assert.True(condition: bobResult.IsError);
        Assert.Contains(
            expectedSubstring: "Refused: principal 'bob' is not granted",
            actualString: Output(result: bobResult)
        );

        var crossResult = await bob.CallToolAsync(
            "puck_state_vector_write",
            new Dictionary<string, object?> {
                ["attachmentId"] = aliceAttachment,
                ["row"] = "embedding",
                ["key"] = "cell1",
                ["vector"] = "b64u:AQID"
            },
            cancellationToken: Token
        );
        Assert.True(condition: crossResult.IsError);
    }
}

