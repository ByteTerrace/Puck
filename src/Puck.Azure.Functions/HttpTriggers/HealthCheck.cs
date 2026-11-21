using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Puck.Azure.Functions.Middleware;

namespace Puck.Azure.Functions.HttpTriggers;

/// <summary>Public dependency readiness summary returned to anonymous probes without backend diagnostic metadata.</summary>
/// <param name="Status">Aggregate health status (<c>Healthy</c>, <c>Degraded</c>, or <c>Unhealthy</c>).</param>
public sealed record class HealthCheckResponse(
    string Status
);
public sealed class HealthCheck(HealthCheckService healthCheckService, ILogger<HealthCheck> logger) {
    [FeatureGate(features: nameof(HealthCheck))]
    [Function(name: nameof(HealthCheck))]
    public async Task<IActionResult> Run(
        [HttpTrigger(
            authLevel: AuthorizationLevel.Anonymous,
            methods: "get",
            Route = "health-check"
        )] HttpRequest request,
        FunctionContext functionContext
    ) {
        var cancellationToken = (functionContext?.CancellationToken ?? request.HttpContext.RequestAborted);
        var healthReport = await healthCheckService.CheckHealthAsync(cancellationToken: cancellationToken);

        // The diagnosis stays server-side: every entry that is not healthy is logged with its name, status, description
        // and exception, while the anonymous body carries only the aggregate status.
        if (logger.IsEnabled(logLevel: LogLevel.Warning)) {
            foreach (var (name, entry) in healthReport.Entries) {
                if (entry.Status == HealthStatus.Healthy) { continue; }
                logger.Log(
                    logLevel: ((entry.Status == HealthStatus.Unhealthy) ? LogLevel.Error : LogLevel.Warning),
                    exception: entry.Exception,
                    message: "Health check entry {Entry} is {Status}: {Description}",
                    args: [name, entry.Status, entry.Description]
                );
            }
        }

        var response = new HealthCheckResponse(Status: healthReport.Status.ToString());

        // An origin probe reads the status code, so an unhealthy report must not answer 200.
        return ((healthReport.Status == HealthStatus.Unhealthy)
            ? new ObjectResult(value: response) { StatusCode = StatusCodes.Status503ServiceUnavailable }
            : new OkObjectResult(value: response)
        );
    }
}

