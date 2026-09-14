using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Puck.Hosting;

namespace Puck.Mcp;

/// <summary>Opt-in MCP composition over an application's existing host lifetime and Console targets.</summary>
public static class McpHostingExtensions {
    /// <summary>Registers the authenticated HTTP listener as a host extension. Requires an IControlSessionHost for a named target.</summary>
    /// <param name="builder">The host being composed.</param>
    /// <param name="options">Validated remote endpoint and authorization settings.</param>
    /// <param name="configurationPath">The deployment JSON monitored for grant changes.</param>
    /// <param name="hostFactory">Optional trusted host adapter, including separately authorized service tools. The extension owns and disposes it.</param>
    /// <returns>The same builder.</returns>
    public static IHostApplicationBuilder AddPuckMcp(this IHostApplicationBuilder builder, RemoteMcpOptions options, string configurationPath,
        Func<IServiceProvider, RemoteMcpHost>? hostFactory = null) {
        ArgumentNullException.ThrowIfNull(builder);
        options = options.Validate();
        if (options.Target is null) {
            throw new ArgumentException(
            message: "An in-process MCP extension requires a target.",
            paramName: nameof(options)
        );
        }
        if (
            (options.Services is not null) &&
            (hostFactory is null)
        ) {
            throw new ArgumentException(
            message: "Service settings require an explicitly installed host adapter.",
            paramName: nameof(hostFactory)
        );
        }
        var path = Path.GetFullPath(path: configurationPath);

        builder.Services.AddHostedService(implementationFactory: sp => new McpHostedService(
            path,
            options,
            (hostFactory?.Invoke(sp) ?? new HostedControlHost(
                host: sp.GetRequiredService<IControlSessionHost>(),
                target: options.Target!
            ))
        ));
        return builder;
    }

    internal sealed class McpHostedService(string configurationPath, RemoteMcpOptions options,
        RemoteMcpHost host) : BackgroundService, IHostedLifecycleService, Puck.Abstractions.IPuckHostedService {
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

            try { await app.RunAsync(token: stoppingToken).ConfigureAwait(continueOnCapturedContext: false); } finally { await stop.CancelAsync().ConfigureAwait(continueOnCapturedContext: false); await monitor.ConfigureAwait(continueOnCapturedContext: false); m_app = null; }
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
    }
    internal sealed class HostedControlHost(IControlSessionHost host, string target) : RemoteMcpHost {
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

        public override bool IsReady => host.IsReady(target: target);
    }

    private sealed class NestedLifetime : IHostLifetime {
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
