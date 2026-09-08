using Microsoft.Extensions.Azure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Puck.Azure.Functions.HealthChecks;

public sealed class RedisCacheHealthCheckOptions
{
    public string? ClientName { get; set; }
}

public sealed class RedisCacheHealthCheck(IServiceProvider serviceProvider) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    ) {
        try {
            var options = serviceProvider.GetRequiredService<IOptions<RedisCacheHealthCheckOptions>>();
            var connectionMultiplexer = (options.Value.ClientName is null)
                ? serviceProvider.GetRequiredService<IConnectionMultiplexer>()
                : serviceProvider
                    .GetRequiredService<IAzureClientFactory<IConnectionMultiplexer>>()
                    .CreateClient(name: options.Value.ClientName);

            await Task
                .WhenAll(tasks: connectionMultiplexer
                    .GetEndPoints(configuredOnly: true)
                    .Select(selector: async endpoint => await connectionMultiplexer
                        .GetServer(endpoint: endpoint)
                        .PingAsync()
                        .ConfigureAwait(continueOnCapturedContext: false)
                    )
                    .Append(element: connectionMultiplexer
                        .GetDatabase()
                        .PingAsync()
                    )
                )
                .WaitAsync(cancellationToken: cancellationToken);

            return HealthCheckResult.Healthy();
        }
        catch (Exception e) {
            return new HealthCheckResult(
                exception: e,
                status: context.Registration.FailureStatus
            );
        }
    }
}

