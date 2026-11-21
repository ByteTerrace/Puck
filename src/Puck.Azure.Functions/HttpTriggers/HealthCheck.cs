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
    private const string UnhandledExceptionMessage = "Unhandled exception while executing health check.";

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

        if (logger.IsEnabled(logLevel: LogLevel.Error)) {
            foreach (var entry in healthReport.Entries) {
                if (entry.Value.Exception is not null) {
                    logger.LogError(
                        exception: entry.Value.Exception,
                        message: UnhandledExceptionMessage
                    );
                }
            }
        }

        return new OkObjectResult(value: new HealthCheckResponse(
            Status: healthReport.Status.ToString()
        ));
    }
}

