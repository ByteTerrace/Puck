using Azure.Core;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Puck.Azure;

/// <summary>A logger factory whose records leave for Azure Monitor (Application Insights traces), for a component that
/// owns no OpenTelemetry host builder of its own, such as a telemetry sink an installed extension contributes. It
/// holds the same bounds <see cref="AzureMonitorTelemetryExtensions"/> gives a host: every transmission fails after
/// <see cref="AzureMonitorTelemetryExtensions.NetworkTimeout"/>, and <see cref="Dispose"/> waits up to
/// <see cref="AzureMonitorTelemetryExtensions.ShutdownBudget"/> for the batch in flight. Nothing is written to disk:
/// the process it serves may be deleted, disk and all, right after it fails, so a batch that cannot leave is
/// dropped rather than kept for a restart that never comes.</summary>
public sealed class AzureMonitorLogging : IDisposable {
    private readonly ServiceProvider m_services;

    /// <summary>Initializes a new instance of the <see cref="AzureMonitorLogging"/> class. Nothing reaches the network
    /// until the first batch is exported.</summary>
    /// <param name="connectionString">The Application Insights connection string.</param>
    /// <param name="credential">The Entra credential every transmission authenticates with.</param>
    /// <exception cref="ArgumentException"><paramref name="connectionString"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="credential"/> is <see langword="null"/>.</exception>
    public AzureMonitorLogging(string connectionString, TokenCredential credential) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: connectionString);
        ArgumentNullException.ThrowIfNull(argument: credential);

        var services = new ServiceCollection();

        _ = services.AddLogging(configure: logging => logging
            .SetMinimumLevel(level: LogLevel.Trace)
            .AddOpenTelemetry(configure: options => options.AddAzureMonitorLogExporter(
                configure: exporter => {
                    exporter.ConnectionString = connectionString;
                    exporter.DisableOfflineStorage = true;
                    exporter.Retry.NetworkTimeout = AzureMonitorTelemetryExtensions.NetworkTimeout;
                },
                credential: credential
            )));
        m_services = services.BuildServiceProvider();
        Factory = m_services.GetRequiredService<ILoggerFactory>();
    }

    /// <summary>Gets the factory whose loggers export.</summary>
    public ILoggerFactory Factory { get; }

    /// <summary>Sends what the exporter still holds, waiting up to
    /// <see cref="AzureMonitorTelemetryExtensions.ShutdownBudget"/>, then releases the factory.</summary>
    public void Dispose() {
        _ = AzureMonitorTelemetryExtensions.ShutdownAsync(cancellationToken: CancellationToken.None, services: m_services).GetAwaiter().GetResult();
        m_services.Dispose();
    }
}
