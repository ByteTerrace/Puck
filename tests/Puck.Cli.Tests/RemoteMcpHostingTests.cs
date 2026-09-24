using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Puck.Hosting;
using Puck.Mcp;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Puck.Cli.Tests;

public sealed class RemoteMcpHostingTests {
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // Re-reads the subject's admission each time the policy changes, so the wait ends on the change itself.
    private static async Task WhenAllowedAsync(RemoteMcpAccessPolicy policy, bool allowed) {
        var changed = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        void Changed() => changed.TrySetResult();

        policy.Changed += Changed;
        try {
            while (policy.Allows(subject: "alice") != allowed) {
                await changed.Task.WaitAsync(cancellationToken: Token);
                changed = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
            }
        } finally { policy.Changed -= Changed; }
    }

    [Fact]
    public async Task InvalidConfigurationRevokesGrantsAndRecoveryDoesNotRestoreOldHandles() {
        await using var fixture = new RemoteMcpFixture();

        await fixture.StartAsync(Token);
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
        var path = Path.GetTempFileName();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token: Token);
        Task? monitor = null;
        var policy = fixture.App.Services.GetRequiredService<RemoteMcpAccessPolicy>();

        try {
            var json = JsonSerializer.Serialize(
                fixture.Options,
                new JsonSerializerOptions(defaults: JsonSerializerDefaults.Web)
            );

            await File.WriteAllTextAsync(
                path,
                json,
                Token
            );
            var initial = await RemoteMcpServer.ReadOptionsAsync(
                path,
                Token
            );

            monitor = RemoteMcpServer.WatchConfigurationAsync(
                fixture.App,
                path,
                initial,
                stop.Token
            );
            await File.WriteAllTextAsync(
                path,
                "{",
                Token
            );
            fixture.Clock.Advance(by: RemoteMcpServer.ConfigurationReloadInterval);
            await WhenAllowedAsync(
                allowed: false,
                policy: policy
            );
            await File.WriteAllTextAsync(
                path,
                json,
                Token
            );
            fixture.Clock.Advance(by: RemoteMcpServer.ConfigurationReloadInterval);
            await WhenAllowedAsync(
                allowed: true,
                policy: policy
            );
            var result = await client.CallToolAsync(
                "puck_exec",
                new Dictionary<string, object?> { ["attachmentId"] = id, ["command"] = "read" },
                cancellationToken: Token
            );

            Assert.True(condition: result.IsError);
            // An issuer change must fail closed until a process restart with that identity.
            await File.WriteAllTextAsync(
                path,
                json.Replace(
                    newValue: "https://other.example.test",
                    oldValue: RemoteMcpFixture.Issuer
                ),
                Token
            );
            fixture.Clock.Advance(by: RemoteMcpServer.ConfigurationReloadInterval);
            await WhenAllowedAsync(
                allowed: false,
                policy: policy
            );
        } finally {
            await stop.CancelAsync();
            if (monitor is not null) { await monitor; }
            File.Delete(path: path);
        }
    }
    [Fact]
    public async Task MinimalTargetConfigurationPreservesDeploymentDefaults() {
        var path = Path.GetTempFileName();

        try {
            const string Json = """{"target":"row","publicUrl":"https://mcp.example.test/mcp","listenUrl":"http://127.0.0.1:8080","issuer":"https://issuer.example.test","audience":"api","scope":"user_impersonation","allowedSubjects":[]}""";

            await File.WriteAllTextAsync(
                path,
                Json,
                Token
            );
            var options = await RemoteMcpServer.ReadOptionsAsync(
                path,
                Token
            );

            Assert.Equal(
                "row",
                options.Target
            );
            Assert.Equal(
                "sub",
                options.SubjectClaim
            );
            Assert.Empty(collection: options.AllowedOrigins);
            Assert.Equal(
                300,
                options.IdleTimeoutSeconds
            );
            await File.WriteAllTextAsync(
                path,
                Json.Replace(
                    newValue: "\"target\":\"row\",\"idleTimeoutSeconds\":0",
                    oldValue: "\"target\":\"row\""
                ),
                Token
            );
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(testCode: () => RemoteMcpServer.ReadOptionsAsync(
                path,
                Token
            ));
        } finally { File.Delete(path: path); }
    }
    [Fact]
    public async Task ReadinessUsesInjectedHostWithoutOpeningAnAttachment() {
        await using var fixture = new RemoteMcpFixture();
        var host = new ProbeHost();

        await fixture.StartAsync(
            Token,
            configure: builder => builder.Services.AddSingleton<RemoteMcpHost>(implementationInstance: host)
        );
        using var http = fixture.Http();

        Assert.Equal(
            HttpStatusCode.OK,
            (await http.GetAsync(
                "/healthz",
                Token
            )).StatusCode
        );
        Assert.Equal(
            actual: host.Opened,
            expected: 0
        );
        host.Ready = false;
        Assert.Equal(
            HttpStatusCode.ServiceUnavailable,
            (await http.GetAsync(
                "/healthz",
                Token
            )).StatusCode
        );
        Assert.Equal(
            actual: host.Opened,
            expected: 0
        );
    }
    [Fact]
    public async Task RevocationClosesNoncooperativeHostAndRegrantCannotReviveItsHandle() {
        await using var fixture = new RemoteMcpFixture();
        var host = new ProbeHost();

        await fixture.StartAsync(
            Token,
            configure: builder => builder.Services.AddSingleton<RemoteMcpHost>(implementationInstance: host)
        );
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
        var work = client.CallToolAsync(
            "puck_exec",
            new Dictionary<string, object?> { ["attachmentId"] = id, ["command"] = "hold" },
            cancellationToken: Token
        ).AsTask();

        await host.Entered.Task.WaitAsync(cancellationToken: Token);
        var policy = fixture.App.Services.GetRequiredService<RemoteMcpAccessPolicy>();

        policy.Replace(subjects: []);
        // Revocation is what ends the held call; nothing else would.
        _ = await Record.ExceptionAsync(testCode: () => work);
        Assert.True(condition: host.Closed);
        policy.Replace(subjects: ["alice"]);
        var stale = await client.CallToolAsync(
            "puck_exec",
            new Dictionary<string, object?> { ["attachmentId"] = id, ["command"] = "read" },
            cancellationToken: Token
        );

        Assert.True(condition: stale.IsError);
        host.Late.TrySetException(exception: new IOException(message: "late host failure"));
    }
    [Fact]
    public async Task ServiceToolsReceiveOnlyTheValidatedCurrentCallerAndCancelOnGrantRemoval() {
        await using var fixture = new RemoteMcpFixture();
        var host = new ServiceHost();

        await fixture.StartAsync(
            Token,
            configure: builder => builder.Services.AddSingleton<RemoteMcpHost>(implementationInstance: host)
        );
        var assertion = fixture.Token();
        using var http = fixture.Http(token: assertion);
        await using var client = await fixture.ClientAsync(
            http: http,
            revision: "2026-07-28",
            token: Token
        );

        Assert.Contains(
            collection: await client.ListToolsAsync(cancellationToken: Token),
            filter: tool => (tool.Name == "test_service")
        );
        var pending = client.CallToolAsync(
            "test_service",
            cancellationToken: Token
        ).AsTask();
        var caller = await host.Entered.Task.WaitAsync(cancellationToken: Token);

        Assert.Equal(
            "alice",
            caller.Subject
        );
        Assert.Equal(
            RemoteMcpFixture.Issuer,
            caller.Issuer
        );
        Assert.Equal(
            RemoteMcpFixture.Tenant,
            caller.TenantId
        );
        Assert.Equal(
            assertion,
            caller.UserAssertion
        );
        Assert.DoesNotContain(
            assertion,
            caller.ToString()!
        );
        fixture.App.Services.GetRequiredService<RemoteMcpAccessPolicy>().Replace(subjects: []);
        await host.Cancelled.Task.WaitAsync(cancellationToken: Token);
        // Grant removal is what ends the pending call; nothing else would.
        _ = await Record.ExceptionAsync(testCode: () => pending);
    }

    private sealed class ServiceHost : RemoteMcpHost {
        internal readonly TaskCompletionSource<RemoteMcpCaller> Entered = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Cancelled = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool IsReady => true;
        public override IReadOnlyList<Tool> ServiceTools => [new() { Name = "test_service", InputSchema = JsonElement.Parse("""{"type":"object"}""") }];

        public override ValueTask<IControlSession> AttachAsync(RemoteMcpCaller caller, CancellationToken cancellationToken) => throw new InvalidOperationException(message: "Service calls do not open Console sessions.");
        public override async ValueTask<CallToolResult> CallServiceAsync(RemoteMcpCaller caller, CallToolRequestParams request, CancellationToken cancellationToken) {
            Entered.TrySetResult(result: caller);
            try {
                await Task.Delay(
                cancellationToken: cancellationToken,
                delay: Timeout.InfiniteTimeSpan
            ); return new();
            } finally { Cancelled.TrySetResult(); }
        }
    }
    private sealed class ProbeHost : RemoteMcpHost {
        internal bool Ready = true;
        internal readonly TaskCompletionSource Entered = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<ControlResponse> Late = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool Closed;
        internal int Opened;

        public override bool IsReady => Ready;

        public override ValueTask<IControlSession> AttachAsync(RemoteMcpCaller caller, CancellationToken cancellationToken) {
            Opened++;
            return ValueTask.FromResult<IControlSession>(result: new Session(host: this));
        }

        private sealed class Session(ProbeHost host) : IControlSession {
            public void Dispose() => host.Closed = true;
            public Task<ControlResponse> ExecuteAsync(ControlRequest request, CancellationToken cancellationToken) {
                host.Entered.TrySetResult();
                return host.Late.Task;
            }
        }
    }
}
