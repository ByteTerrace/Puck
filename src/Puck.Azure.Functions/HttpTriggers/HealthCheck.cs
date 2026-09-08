using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Puck.Azure.Functions.Middleware;

namespace Puck.Azure.Functions.HttpTriggers;

public sealed record class HealthCheckEntry(
    IReadOnlyDictionary<string, object> Data,
    string? Description,
    TimeSpan Duration,
    string? Exception,
    string Status,
    IEnumerable<string> Tags
);
public sealed record class HealthCheckResponse(
    IDictionary<string, HealthCheckEntry> Entries,
    string Status,
    TimeSpan TotalDuration
);

public sealed class HealthCheck(HealthCheckService healthCheckService, ILogger<HealthCheck> logger)
{
    private const string UnhandledExceptionMessage = "Unhandled exception while executing health check.";

    [FeatureGate(features: nameof(HealthCheck))]
    [Function(name: nameof(HealthCheck))]
    public async Task<IActionResult> Run(
        [HttpTrigger(
            authLevel: AuthorizationLevel.Anonymous,
            methods: "get",
            Route = "health-check"
        )] HttpRequest _,
        FunctionContext functionContext
    ) {
        var cancellationToken = functionContext.CancellationToken;
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
            Entries: healthReport.Entries.ToDictionary(
                elementSelector: entry => new HealthCheckEntry(
                    Data: entry.Value.Data,
                    Description: entry.Value.Description,
                    Duration: entry.Value.Duration,
                    Exception: entry.Value.Exception?.Message,
                    Status: entry.Value.Status.ToString(),
                    Tags: entry.Value.Tags
                ),
                keySelector: entry => entry.Key
            ),
            Status: healthReport.Status.ToString(),
            TotalDuration: healthReport.TotalDuration
        ));
    }
}

