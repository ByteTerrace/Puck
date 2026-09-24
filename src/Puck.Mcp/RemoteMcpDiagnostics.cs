using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace Puck.Mcp;

/// <summary>Instrumentation names used by the deployment's existing OpenTelemetry exporters.</summary>
public static class RemoteMcpInstrumentation {
    /// <summary>The ActivitySource and Meter name for MCP operations.</summary>
    public const string Name = "Puck.Mcp";
}

internal sealed partial class RemoteMcpDiagnostics : IDisposable {
    private readonly ILogger<RemoteMcpDiagnostics> m_logger;

    private readonly ActivitySource m_activities = new(name: RemoteMcpInstrumentation.Name);
    private readonly Meter m_meter = new(name: RemoteMcpInstrumentation.Name);

    private readonly Histogram<double> m_duration;
    private readonly TimeProvider m_clock;

    public RemoteMcpDiagnostics(ILogger<RemoteMcpDiagnostics> logger, TimeProvider clock) {
        m_clock = clock;
        m_logger = logger;
        m_duration = m_meter.CreateHistogram<double>(
            "puck.mcp.operation.duration",
            "ms"
        );
    }

    // A timestamp on the gateway clock for a later Completed.
    internal long Timestamp() => m_clock.GetTimestamp();
    internal Activity? Start(string tool) => m_activities.StartActivity(
        kind: ActivityKind.Internal,
        name: tool
    );
    internal void Completed(string subject, string tool, string outcome, long start, string? requestId) {
        var elapsed = m_clock.GetElapsedTime(startingTimestamp: start).TotalMilliseconds;

        m_duration.Record(
            elapsed,
            new KeyValuePair<string, object?>(
                key: "tool",
                value: tool
            ),
            new(
                key: "outcome",
                value: outcome
            )
        );
        Operation(
            elapsedMilliseconds: elapsed,
            logger: m_logger,
            outcome: outcome,
            requestId: requestId,
            subject: subject,
            tool: tool
        );
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP subject {Subject} tool {Tool} outcome {Outcome} host request {RequestId} elapsed {ElapsedMilliseconds} ms")]
    private static partial void Operation(ILogger logger, string subject, string tool, string outcome, string? requestId, double elapsedMilliseconds);

    public void Dispose() { m_activities.Dispose(); m_meter.Dispose(); }
}
