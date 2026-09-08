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
    private readonly ActivitySource m_activities = new(RemoteMcpInstrumentation.Name);
    private readonly Meter m_meter = new(RemoteMcpInstrumentation.Name);
    private readonly Histogram<double> m_duration;
    public RemoteMcpDiagnostics(ILogger<RemoteMcpDiagnostics> logger) {
        m_logger = logger;
        m_duration = m_meter.CreateHistogram<double>("puck.mcp.operation.duration", "ms");
    }
    internal Activity? Start(string tool) => m_activities.StartActivity(tool, ActivityKind.Internal);
    internal void Completed(string subject, string tool, string outcome, long start, string? requestId) {
        var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        m_duration.Record(elapsed, new KeyValuePair<string, object?>("tool", tool), new("outcome", outcome));
        Operation(m_logger, subject, tool, outcome, requestId, elapsed);
    }
    [LoggerMessage(Level = LogLevel.Information, Message = "MCP subject {Subject} tool {Tool} outcome {Outcome} host request {RequestId} elapsed {ElapsedMilliseconds} ms")]
    private static partial void Operation(ILogger logger, string subject, string tool, string outcome, string? requestId, double elapsedMilliseconds);
    public void Dispose() { m_activities.Dispose(); m_meter.Dispose(); }
}
