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

    [Fact]
    public async Task ServiceToolsReceiveOnlyTheValidatedCurrentCallerAndCancelOnGrantRemoval() {
        await using var fixture = new RemoteMcpFixture();
        var host = new ServiceHost();
        await fixture.StartAsync(Token, configure: builder => builder.Services.AddSingleton<RemoteMcpHost>(host));
        var assertion = fixture.Token();
        using var http = fixture.Http(assertion);
        await using var client = await fixture.ClientAsync(http, "2026-07-28", Token);
        Assert.Contains(await client.ListToolsAsync(cancellationToken: Token), tool => tool.Name == "test_service");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = client.CallToolAsync("test_service", cancellationToken: deadline.Token).AsTask();
        var caller = await host.Entered.Task.WaitAsync(deadline.Token);
        Assert.Equal("alice", caller.Subject);
        Assert.Equal(RemoteMcpFixture.Issuer, caller.Issuer);
        Assert.Equal(RemoteMcpFixture.Tenant, caller.TenantId);
        Assert.Equal(assertion, caller.UserAssertion);
        Assert.DoesNotContain(assertion, caller.ToString()!);
        fixture.App.Services.GetRequiredService<RemoteMcpAccessPolicy>().Replace([]);
        await host.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3), Token);
        try { await pending.WaitAsync(TimeSpan.FromSeconds(3), Token); } catch (Exception) when (!deadline.IsCancellationRequested) { }
        Assert.True(pending.IsCompleted);
    }

    private sealed class ServiceHost : RemoteMcpHost {
        internal readonly TaskCompletionSource<RemoteMcpCaller> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool IsReady => true;
        public override ValueTask<IControlSession> AttachAsync(string subject, CancellationToken cancellationToken) => throw new InvalidOperationException("Service calls do not open Console sessions.");
        public override IReadOnlyList<Tool> ServiceTools => [new() { Name = "test_service", InputSchema = JsonElement.Parse("""{"type":"object"}""") }];
        public override async ValueTask<CallToolResult> CallServiceAsync(RemoteMcpCaller caller, CallToolRequestParams request, CancellationToken cancellationToken) {
            Entered.TrySetResult(caller);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return new(); }
            finally { Cancelled.TrySetResult(); }
        }
    }

    [Fact]
    public async Task InvalidConfigurationRevokesGrantsAndRecoveryDoesNotRestoreOldHandles() {
        await using var fixture = new RemoteMcpFixture();
        await fixture.StartAsync(Token);
        using var http = fixture.Http(fixture.Token());
        await using var client = await fixture.ClientAsync(http, "2026-07-28", Token);
        var attached = await client.CallToolAsync("puck_attach", cancellationToken: Token);
        var id = attached.StructuredContent!.Value.GetProperty("attachmentId").GetString();
        var path = Path.GetTempFileName();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Task? monitor = null;
        var policy = fixture.App.Services.GetRequiredService<RemoteMcpAccessPolicy>();
        try {
            var json = JsonSerializer.Serialize(fixture.Options, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await File.WriteAllTextAsync(path, json, Token);
            var initial = await RemoteMcpServer.ReadOptionsAsync(path, Token);
            monitor = RemoteMcpServer.WatchConfigurationAsync(fixture.App, path, initial, stop.Token);
            await File.WriteAllTextAsync(path, "{", Token);
            await Until(() => !policy.Allows("alice"));
            await File.WriteAllTextAsync(path, json, Token);
            await Until(() => policy.Allows("alice"));
            var result = await client.CallToolAsync("puck_exec", new Dictionary<string, object?> { ["attachmentId"] = id, ["command"] = "read" }, cancellationToken: Token);
            Assert.True(result.IsError);
            // An issuer change must fail closed until a process restart with that identity.
            await File.WriteAllTextAsync(path, json.Replace(RemoteMcpFixture.Issuer, "https://other.example.test"), Token);
            await Until(() => !policy.Allows("alice"));
        } finally {
            await stop.CancelAsync();
            if (monitor is not null) { await monitor; }
            File.Delete(path);
        }
    }

    private static async Task Until(Func<bool> predicate) {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        while (!predicate()) { await Task.Delay(20, deadline.Token); }
    }

    [Fact]
    public async Task RevocationClosesNoncooperativeHostAndRegrantCannotReviveItsHandle() {
        await using var fixture = new RemoteMcpFixture();
        var host = new ProbeHost();
        await fixture.StartAsync(Token, configure: builder => builder.Services.AddSingleton<RemoteMcpHost>(host));
        using var http = fixture.Http(fixture.Token());
        await using var client = await fixture.ClientAsync(http, "2026-07-28", Token);
        var attached = await client.CallToolAsync("puck_attach", cancellationToken: Token);
        var id = attached.StructuredContent!.Value.GetProperty("attachmentId").GetString();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var work = client.CallToolAsync("puck_exec", new Dictionary<string, object?> { ["attachmentId"] = id, ["command"] = "hold" }, cancellationToken: deadline.Token).AsTask();
        await host.Entered.Task.WaitAsync(deadline.Token);
        var policy = fixture.App.Services.GetRequiredService<RemoteMcpAccessPolicy>();
        policy.Replace([]);
        try { await work.WaitAsync(TimeSpan.FromSeconds(3), Token); } catch (Exception) when (!deadline.IsCancellationRequested) { }
        Assert.True(work.IsCompleted);
        Assert.True(host.Closed);
        policy.Replace(["alice"]);
        var stale = await client.CallToolAsync("puck_exec", new Dictionary<string, object?> { ["attachmentId"] = id, ["command"] = "read" }, cancellationToken: Token);
        Assert.True(stale.IsError);
        host.Late.TrySetException(new IOException("late host failure"));
    }

    [Fact]
    public async Task ReadinessUsesInjectedHostWithoutOpeningAnAttachment() {
        await using var fixture = new RemoteMcpFixture();
        var host = new ProbeHost();
        await fixture.StartAsync(Token, configure: builder => builder.Services.AddSingleton<RemoteMcpHost>(host));
        using var http = fixture.Http();
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/healthz", Token)).StatusCode);
        Assert.Equal(0, host.Opened);
        host.Ready = false;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await http.GetAsync("/healthz", Token)).StatusCode);
        Assert.Equal(0, host.Opened);
    }

    private sealed class ProbeHost : RemoteMcpHost {
        internal bool Ready = true;
        internal bool Closed;
        internal int Opened;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<ControlResponse> Late = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool IsReady => Ready;
        public override ValueTask<IControlSession> AttachAsync(string subject, CancellationToken cancellationToken) {
            Opened++;
            return ValueTask.FromResult<IControlSession>(new Session(this));
        }
        private sealed class Session(ProbeHost host) : IControlSession {
            public Task<ControlResponse> ExecuteAsync(ControlRequest request, CancellationToken cancellationToken) {
                host.Entered.TrySetResult();
                return host.Late.Task;
            }
            public void Dispose() => host.Closed = true;
        }
    }
}
