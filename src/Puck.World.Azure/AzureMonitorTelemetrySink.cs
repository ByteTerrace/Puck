using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Puck.Azure;
using Puck.Storage.Azure;
using Puck.World.Server;

namespace Puck.World.Azure;

/// <summary>The <c>azure.monitor</c> telemetry sink: a host's records exported as Application Insights traces through
/// <see cref="AzureMonitorLogging"/>, authenticated by the ambient credential (<see cref="AzureAmbientCredential"/>,
/// the worker's managed identity, which needs Monitoring Metrics Publisher on the component). Settings hold exactly one
/// <c>connectionString</c>. Each record keeps its category, level and message; an exception travels as its text,
/// since the record carries no exception object. Disposal sends what the exporter still holds, within the export
/// bounds <see cref="AzureMonitorLogging"/> documents.</summary>
public sealed class AzureMonitorTelemetrySink : IWorldTelemetrySink {
    /// <summary>The contribution key that selects this sink.</summary>
    public const string Type = "azure.monitor";

    private readonly AzureMonitorLogging m_logging;
    private readonly ConcurrentDictionary<string, ILogger> m_loggers = new(comparer: StringComparer.Ordinal);

    /// <summary>Gets the provider the Azure world extension registers under <see cref="Type"/>.</summary>
    public static WorldSiloTelemetryProvider Provider { get; } = new(
        Type,
        static settings => {
            var value = WorldExtensionSettings.OnlySetting(name: "connectionString", settings: settings);

            if ((value.ValueKind != JsonValueKind.String) || string.IsNullOrWhiteSpace(value: value.GetString())) {
                throw new ArgumentException(message: "azure.monitor telemetry requires exactly one nonempty connectionString setting.");
            }
            return new AzureMonitorTelemetrySink(connectionString: value.GetString()!);
        }
    );

    private AzureMonitorTelemetrySink(string connectionString) => m_logging = new AzureMonitorLogging(
        connectionString: connectionString,
        credential: AzureAmbientCredential.Create()
    );

    /// <inheritdoc/>
    public void Write(WorldTelemetryRecord record) {
        ArgumentNullException.ThrowIfNull(argument: record);
        var logger = m_loggers.GetOrAdd(key: record.Category, valueFactory: m_logging.Factory.CreateLogger);

        if (record.Exception is { } exception) {
            logger.Log(logLevel: (LogLevel)record.Level, message: "{Message}\n{Exception}", record.Message, exception);
        } else {
            logger.Log(logLevel: (LogLevel)record.Level, message: "{Message}", record.Message);
        }
    }
    /// <inheritdoc/>
    public void Dispose() => m_logging.Dispose();
}
