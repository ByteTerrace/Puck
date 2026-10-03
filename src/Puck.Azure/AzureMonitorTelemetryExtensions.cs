using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Puck.Azure;

/// <summary>Azure Monitor export for an application host whose telemetry must leave the process before it exits.
/// With the exporter's <c>DisablePersistOnShutdown</c> switch set in the host's runtime configuration, a provider's
/// shutdown sends its final batch rather than writing it to disk, but its disposal waits only five seconds for that
/// send. So once every hosted service has stopped, the host shuts each provider down itself, waiting up to
/// <see cref="ShutdownBudget"/> for the send in flight, and every transmission fails after
/// <see cref="NetworkTimeout"/>, so the wait always ends inside the budget: the final batch is sent, or its failure is
/// logged.</summary>
public static class AzureMonitorTelemetryExtensions {
    /// <summary>The longest one transmission to the ingestion endpoint may take before it fails. The exporter sends each
    /// batch exactly once (it sets the client's retry count to zero), so this bounds a whole export. The client default
    /// is a hundred seconds.</summary>
    public static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(seconds: 10);
    /// <summary>How long a stopped host waits for its providers to send what they hold. It exceeds
    /// <see cref="NetworkTimeout"/>, so a stalled send fails before the budget ends, and it fits inside the generic
    /// host's thirty-second default shutdown timeout, which bounds the whole stop, and far inside the ten minutes Flex
    /// Consumption gives an instance to drain before a platform update.</summary>
    public static readonly TimeSpan ShutdownBudget = TimeSpan.FromSeconds(seconds: 20);

    /// <summary>Exports this builder's telemetry to Azure Monitor, every transmission bounded by
    /// <see cref="NetworkTimeout"/>, and shuts the providers down within <see cref="ShutdownBudget"/> once the host's
    /// services have stopped. Telemetry recorded after that, while the container is disposed, is not exported.</summary>
    /// <typeparam name="TBuilder">The builder's type, kept so the caller's chain continues on it.</typeparam>
    /// <param name="builder">The host's OpenTelemetry builder.</param>
    /// <param name="configure">Configures the exporter (credential, connection string, transport). It runs before the
    /// network timeout is set, so it cannot loosen it.</param>
    /// <returns>The same builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="configure"/> is
    /// <see langword="null"/>.</exception>
    public static TBuilder UseAzureMonitorExporterWithBoundedShutdown<TBuilder>(this TBuilder builder, Action<AzureMonitorExporterOptions> configure) where TBuilder : IOpenTelemetryBuilder {
        ArgumentNullException.ThrowIfNull(argument: builder);
        ArgumentNullException.ThrowIfNull(argument: configure);

        builder.UseAzureMonitorExporter(configureAzureMonitor: options => {
            configure(obj: options);
            options.Retry.NetworkTimeout = NetworkTimeout;
        });
        builder.Services.AddHostedService<TelemetryShutdown>();

        return builder;
    }

    // A provider's ForceFlush returns once its batch has left the buffer, before the send, so only Shutdown, which joins
    // the export thread, waits for delivery. It runs in StoppedAsync, after every hosted service's StopAsync, so the
    // telemetry those services record while stopping is in the batches it waits on.
    private sealed class TelemetryShutdown(IServiceProvider services, ILogger<TelemetryShutdown> logger) : IHostedLifecycleService {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public async Task StoppedAsync(CancellationToken cancellationToken) {
            var budget = ((int)ShutdownBudget.TotalMilliseconds);
            var shutdowns = new List<Task<(string Signal, bool Settled)>>(capacity: 3);

            // Each provider has its own exporter, so the three shut down side by side and the whole wait takes one budget.
            if (services.GetService<TracerProvider>() is { } tracer) {
                shutdowns.Add(item: Task.Run(function: () => ("traces", tracer.Shutdown(timeoutMilliseconds: budget)), cancellationToken: CancellationToken.None));
            }
            if (services.GetService<MeterProvider>() is { } meter) {
                shutdowns.Add(item: Task.Run(function: () => ("metrics", meter.Shutdown(timeoutMilliseconds: budget)), cancellationToken: CancellationToken.None));
            }
            if (services.GetService<LoggerProvider>() is { } logs) {
                shutdowns.Add(item: Task.Run(function: () => ("logs", logs.Shutdown(timeoutMilliseconds: budget)), cancellationToken: CancellationToken.None));
            }

            try {
                foreach (var (signal, settled) in await Task.WhenAll(tasks: shutdowns).WaitAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false)) {
                    if (!settled) {
                        logger.LogWarning(message: "Azure Monitor {Signal} did not finish exporting within the {Budget} shutdown budget.", signal, ShutdownBudget);
                    }
                }
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                logger.LogWarning(message: "The host's shutdown timeout ended the Azure Monitor shutdown before its {Budget} budget.", ShutdownBudget);
            }
        }
        public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
