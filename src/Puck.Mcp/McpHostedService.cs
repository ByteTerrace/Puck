using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Puck.Hosting;

namespace Puck.Mcp;

/// <summary>The one remote MCP server a host runs: an authenticated HTTP listener over the selected
/// <see cref="RemoteMcpHost"/>, nested in the host's own lifetime and watching its configuration for grant changes. It
/// owns and disposes the <see cref="RemoteMcpHost"/>.</summary>
internal sealed class McpHostedService(string configurationPath, RemoteMcpOptions options, RemoteMcpHost host) : BackgroundService, IHostedLifecycleService, Puck.Abstractions.IPuckHostedService {
    private WebApplication? m_app;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        await using var app = RemoteMcpServer.Build(
            options,
            builder => {
                builder.Services.AddSingleton(implementationInstance: host);
                builder.Services.AddSingleton<IHostLifetime, NestedLifetime>();
            }
        );

        m_app = app;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(
            token1: stoppingToken,
            token2: app.Lifetime.ApplicationStopping
        );
        var monitor = RemoteMcpServer.WatchConfigurationAsync(
            app,
            configurationPath,
            options,
            stop.Token
        );

        try {
            try {
                await app.StartAsync(cancellationToken: stoppingToken).ConfigureAwait(continueOnCapturedContext: false);
            } catch (Exception failure) when ((Puck.Abstractions.ListenEndpointUnavailableException.Classify(
                endpoint: options.ListenUrl!,
                failure: failure,
                transport: "http"
            ) is { } unavailable)) {
                throw unavailable;
            }
            await app.WaitForShutdownAsync(token: stoppingToken).ConfigureAwait(continueOnCapturedContext: false);
        } finally { await stop.CancelAsync().ConfigureAwait(continueOnCapturedContext: false); await monitor.ConfigureAwait(continueOnCapturedContext: false); m_app = null; }
    }

    public override void Dispose() { base.Dispose(); (host as IDisposable)?.Dispose(); }
    public async ValueTask DisposeAsync() {
        Dispose();
        if (m_app is not null) {
            await m_app.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);
            m_app = null;
        }
    }
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) {
        m_app?.Services.GetRequiredService<RemoteMcpAccessPolicy>().Replace(subjects: []);
        m_app?.Lifetime.StopApplication();
        return Task.CompletedTask;
    }

    private sealed class NestedLifetime : IHostLifetime {
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
/// <summary>Attaches every caller to the configuration's fixed Console target through the host's
/// <see cref="IControlSessionHost"/>.</summary>
internal sealed class ConsoleTargetHost(IControlSessionHost host, string target) : RemoteMcpHost {
    public override bool IsReady => host.IsReady(target: target);

    public override ValueTask<IControlSession> AttachAsync(RemoteMcpCaller caller, CancellationToken cancellationToken) => host.AttachAsync(
        target,
        new(
            Issuer: caller.Issuer,
            Subject: caller.Subject
        ),
        cancellationToken
    );
    public override ValueTask<ControlCapabilities> DescribeControlAsync(RemoteMcpCaller caller, CancellationToken cancellationToken) => host.DescribeAsync(
        target,
        new(
            Issuer: caller.Issuer,
            Subject: caller.Subject
        ),
        cancellationToken
    );
}
