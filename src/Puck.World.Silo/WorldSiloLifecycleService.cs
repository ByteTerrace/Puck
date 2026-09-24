using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions;
using Puck.Networking;
using Puck.World.Server;

namespace Puck.World.Silo;

/// <summary>Composes health and provider-neutral host retirement around one silo drain. Every deadline it puts on a
/// request, a retirement, or a shutdown runs on the silo's one clock, <see cref="WorldSiloHost.Clock"/>.</summary>
/// <param name="silo">The silo whose drain, reload, and health the routes drive.</param>
/// <param name="lifetime">The application lifetime a completed drain or retirement stops.</param>
/// <param name="extensions">The installed extensions a health route may select.</param>
/// <param name="observer">The installed retirement observer, or <see langword="null"/> for none.</param>
public sealed class WorldSiloLifecycleService(WorldSiloHost silo, IHostApplicationLifetime lifetime, PuckExtensionSet extensions,
    IWorldHostRetirementObserver? observer = null) : BackgroundService, IHostedLifecycleService {
    private readonly WorldSiloLifecycle m_options = silo.Definition.Lifecycle!;
    private readonly WorldSiloReleaseControl m_releaseControl = new(silo: silo);

    /// <summary>Handles one request on the lifecycle listener: loopback drain, reload, and release control, and the
    /// public and private health routes. A failure answers 503 rather than propagating.</summary>
    /// <param name="context">The request.</param>
    /// <returns>The handled request.</returns>
    public async Task HandleAsync(HttpContext context) {
        try {
            if (await m_releaseControl.HandleAsync(context: context)) { return; }
            if (
                (context.Request.Method == "POST") &&
                (context.Request.Path == "/drain") &&
                (context.Connection.RemoteIpAddress is { } address) &&
                IPAddress.IsLoopback(address: address)
            ) {
                using var deadline = new CancellationTokenSource(
                    delay: TimeSpan.FromSeconds(seconds: m_options.ShutdownSeconds),
                    timeProvider: silo.Clock
                );

                await silo.DrainAsync(ct: deadline.Token);
                context.Response.StatusCode = 200;
                try { await context.Response.CompleteAsync(); } finally { lifetime.StopApplication(); }
            } else if (
                (context.Request.Method == "POST") &&
                (context.Request.Path == "/reload") &&
                (context.Connection.RemoteIpAddress is { } reloadAddress) &&
                IPAddress.IsLoopback(address: reloadAddress)
            ) {
                using var deadline = new OperationDeadline(
                    caller: context.RequestAborted,
                    timeout: TimeSpan.FromSeconds(seconds: m_options.ShutdownSeconds),
                    timeProvider: silo.Clock
                );
                var receipts = new List<string>();

                foreach (var world in silo.Definition.Worlds.Where(predicate: static row => row.Pinned)) {
                    var hash = await silo.ReloadAsync(
                        new(
                            Owner: world.Owner,
                            World: world.World
                        ),
                        deadline.Token
                    );

                    receipts.Add(item: $"{world.World} {hash}");
                }
                await context.Response.WriteAsync(
                    string.Join(
                        separator: '\n',
                        values: receipts
                    ),
                    deadline.Token
                );
            } else if (
                (context.Request.Method == "GET") &&
                (context.Request.Path == "/healthz")
            ) {
                using var deadline = new OperationDeadline(
                    caller: context.RequestAborted,
                    timeout: TimeSpan.FromSeconds(seconds: m_options.ProgressTimeoutSeconds),
                    timeProvider: silo.Clock
                );
                var reason = await silo.CheckHealthAsync(cancellationToken: deadline.Token);

                context.Response.StatusCode = ((reason.Length == 0)
                    ? 200
                    : 503
                );
                await context.Response.WriteAsync(
                    ((reason.Length == 0)
                    ? "ready"
                    : reason),
                    context.RequestAborted
                );
            } else if (
                (context.Request.Method == "GET") &&
                (context.Request.Path == "/private-healthz") &&
                (context.Connection.RemoteIpAddress is { } privateAddress) &&
                IPAddress.IsLoopback(address: privateAddress)
            ) {
                using var deadline = new OperationDeadline(
                    caller: context.RequestAborted,
                    timeout: TimeSpan.FromSeconds(seconds: m_options.ProgressTimeoutSeconds),
                    timeProvider: silo.Clock
                );
                var reason = await silo.CheckPrivateHealthAsync(cancellationToken: deadline.Token);

                context.Response.StatusCode = ((reason.Length == 0)
                    ? 200
                    : 503
                );
                await context.Response.WriteAsync(
                    ((reason.Length == 0)
                    ? "private-ready"
                    : reason),
                    context.RequestAborted
                );
            } else if (
                (context.Request.Method == "GET") &&
                (context.Request.Path == "/livez")
            ) {
                context.Response.StatusCode = (silo.Live
                    ? 200
                    : 503
                );
            } else if (
                (context.Request.Method == "GET") &&
                extensions.TryGet<WorldHealthCheck>(
                contribution: out var healthCheck,
                key: (context.Request.Path.Value ?? "")
            )
            ) {
                var (contentType, body) = healthCheck.Respond(silo.Live);

                context.Response.ContentType = contentType;
                context.Response.StatusCode = 200;
                await context.Response.WriteAsync(
                    text: body,
                    cancellationToken: context.RequestAborted
                );
            } else { context.Response.StatusCode = 404; }
        } catch (Exception ex) {
            if (!context.Response.HasStarted) { context.Response.StatusCode = 503; }
            Console.Error.WriteLine(value: $"[silo.lifecycle: {ex.Message}]");
        }
    }
    /// <summary>Drains the silo by <paramref name="notBefore"/> on the silo's clock and then stops the application,
    /// whether or not the drain completed — the retirement callback an observer invokes.</summary>
    /// <param name="notBefore">The UTC instant, on <see cref="WorldSiloHost.Clock"/>, the drain must finish by; an instant
    /// already past leaves no time.</param>
    /// <param name="ct">The observation's own cancellation.</param>
    /// <returns>The retirement; a drain that missed its instant faults it.</returns>
    public async Task RetireAsync(DateTimeOffset notBefore, CancellationToken ct) {
        var available = (notBefore - silo.Clock.GetUtcNow());
        using var deadline = new OperationDeadline(
            caller: ct,
            timeout: ((available > TimeSpan.Zero)
            ? available
            : TimeSpan.Zero),
            timeProvider: silo.Clock
        );

        try { await silo.DrainAsync(ct: deadline.Token); } finally { lifetime.StopApplication(); }
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        var builder = WebApplication.CreateSlimBuilder();

        builder.WebHost.UseKestrel(options: options => options.ListenAnyIP(port: m_options.HealthPort));
        builder.Services.AddSingleton<IHostLifetime, HealthLifetime>();
        await using var web = builder.Build();

        web.Run(handler: HandleAsync);
        try {
            await web.StartAsync(cancellationToken: stoppingToken);
        } catch (Exception failure) when ((ListenEndpointUnavailableException.Classify(
            endpoint: $"*:{m_options.HealthPort}",
            failure: failure,
            transport: "http"
        ) is { } unavailable)) {
            throw unavailable;
        }
        if (observer is not null) {
            await observer.RunAsync(
            cancellationToken: stoppingToken,
            retire: RetireAsync
        );
        } else {
            await Task.Delay(
            cancellationToken: stoppingToken,
            delay: Timeout.InfiniteTimeSpan
        );
        }
    }

    /// <inheritdoc/>
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    /// <inheritdoc/>
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    /// <inheritdoc/>
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    /// <inheritdoc/>
    /// <remarks>Drains the silo within <c>lifecycle.shutdownSeconds</c> on <see cref="WorldSiloHost.Clock"/>; a failed
    /// drain sets a failing process exit code.</remarks>
    public async Task StoppingAsync(CancellationToken cancellationToken) {
        // Runs before services stop, while the simulation mailbox still has a pump.
        using var deadline = new OperationDeadline(
            caller: cancellationToken,
            timeout: TimeSpan.FromSeconds(seconds: m_options.ShutdownSeconds),
            timeProvider: silo.Clock
        );

        try { await silo.DrainAsync(ct: deadline.Token); } catch (Exception ex) { Environment.ExitCode = 1; Console.Error.WriteLine(value: $"[silo.drain: failed ({ex.Message})]"); }
    }

    private sealed class HealthLifetime : IHostLifetime {
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
