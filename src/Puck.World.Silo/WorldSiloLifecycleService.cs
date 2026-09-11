using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Puck.World.Server;

namespace Puck.World.Silo;

/// <summary>Composes health and provider-neutral host retirement around one silo drain.</summary>
internal sealed class WorldSiloLifecycleService(WorldSiloHost silo, IHostApplicationLifetime lifetime,
    IWorldHostRetirementObserver? observer = null) : BackgroundService, IHostedLifecycleService {
    private readonly WorldSiloLifecycle m_options = silo.Definition.Lifecycle!;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        var builder = WebApplication.CreateSlimBuilder();

        builder.WebHost.UseKestrel(options: options => options.ListenAnyIP(m_options.HealthPort));
        builder.Services.AddSingleton<IHostLifetime, HealthLifetime>();
        await using var web = builder.Build();

        web.Run(handler: HandleAsync);
        await web.StartAsync(cancellationToken: stoppingToken);
        if (observer is not null) { await observer.RunAsync(RetireAsync, stoppingToken); } else { await Task.Delay(cancellationToken: stoppingToken, delay: Timeout.InfiniteTimeSpan); }
    }

    private async Task HandleAsync(HttpContext context) {
        try {
            if ((context.Request.Method == "POST") && (context.Request.Path == "/drain") && (context.Connection.RemoteIpAddress is { } address) && IPAddress.IsLoopback(address: address)) {
                using var deadline = new CancellationTokenSource(delay: TimeSpan.FromSeconds(m_options.ShutdownSeconds));

                await silo.DrainAsync(ct: deadline.Token);
                context.Response.StatusCode = 200;
                try { await context.Response.CompleteAsync(); } finally { lifetime.StopApplication(); }
            } else if ((context.Request.Method == "POST") && (context.Request.Path == "/reload") && (context.Connection.RemoteIpAddress is { } reloadAddress) && IPAddress.IsLoopback(address: reloadAddress)) {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token: context.RequestAborted);

                deadline.CancelAfter(delay: TimeSpan.FromSeconds(m_options.ShutdownSeconds));
                var receipts = new List<string>();

                foreach (var world in silo.Definition.Worlds.Where(predicate: static row => row.Pinned)) {
                    var hash = await silo.ReloadAsync(new(Owner: world.Owner, World: world.World), deadline.Token);

                    receipts.Add(item: $"{world.World} {hash}");
                }
                await context.Response.WriteAsync(string.Join(separator: '\n', values: receipts), deadline.Token);
            } else if ((context.Request.Method == "GET") && (context.Request.Path == "/healthz")) {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token: context.RequestAborted);

                deadline.CancelAfter(delay: TimeSpan.FromSeconds(m_options.ProgressTimeoutSeconds));
                var reason = await silo.CheckHealthAsync(cancellationToken: deadline.Token);

                context.Response.StatusCode = ((reason.Length == 0) ? 200 : 503);
                await context.Response.WriteAsync(((reason.Length == 0) ? "ready" : reason), context.RequestAborted);
            } else if ((context.Request.Method == "GET") && (context.Request.Path == "/livez")) {
                context.Response.StatusCode = (silo.Live ? 200 : 503);
            } else if ((context.Request.Method == "GET") && WorldSiloExtensions.TryGetHealthCheck(path: context.Request.Path, handler: out var healthHandler) && (healthHandler is not null)) {
                var (contentType, body) = healthHandler(silo.Live);

                context.Response.ContentType = contentType;
                context.Response.StatusCode = 200;
                await context.Response.WriteAsync(text: body, cancellationToken: context.RequestAborted);
            } else { context.Response.StatusCode = 404; }
        } catch (Exception ex) {
            if (!context.Response.HasStarted) { context.Response.StatusCode = 503; }
            Console.Error.WriteLine(value: $"[silo.lifecycle: {ex.Message}]");
        }
    }
    private async Task RetireAsync(DateTimeOffset notBefore, CancellationToken ct) {
        var available = (notBefore - DateTimeOffset.UtcNow);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token: ct);

        deadline.CancelAfter(delay: ((available > TimeSpan.Zero) ? available : TimeSpan.Zero));
        try { await silo.DrainAsync(ct: deadline.Token); } finally { lifetime.StopApplication(); }
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public async Task StoppingAsync(CancellationToken cancellationToken) {
        // Runs before services stop, while the simulation mailbox still has a pump.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token: cancellationToken);

        deadline.CancelAfter(delay: TimeSpan.FromSeconds(m_options.ShutdownSeconds));
        try { await silo.DrainAsync(ct: deadline.Token); } catch (Exception ex) { Environment.ExitCode = 1; Console.Error.WriteLine(value: $"[silo.drain: failed ({ex.Message})]"); }
    }

    private sealed class HealthLifetime : IHostLifetime {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
