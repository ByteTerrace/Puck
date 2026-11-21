using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Puck.Azure.Functions.HttpTriggers;
using Xunit;

namespace Puck.Azure.Tests;

public sealed class HealthCheckRedactionTests {
    private static readonly JsonSerializerOptions WebJson = new(defaults: JsonSerializerDefaults.Web);

    [InlineData(true)]
    [InlineData(false)]
    [Theory]
    public async Task AnonymousHealthCheckReturnsAggregateStatusWithoutLeakingExceptionsOrComponentMetadata(bool healthy) {
        var services = new ServiceCollection();
        var logger = new RecordingLogger();

        services.AddLogging();
        var builder = services.AddHealthChecks();

        if (healthy) {
            builder.AddCheck(
                name: "RedisCache:Default",
                check: static () => HealthCheckResult.Healthy(
                    description: "redis://internal-cache.redis.cache.windows.net:6380 ready",
                    data: new Dictionary<string, object> { ["endpoint"] = "10.2.0.5:6380" }
                )
            );
        } else {
            builder.AddCheck(
                name: "RedisCache:Default",
                check: static () => HealthCheckResult.Unhealthy(
                    description: "redis://internal-cache.redis.cache.windows.net:6380 failed",
                    exception: new InvalidOperationException(message: "AUTH password rejected on internal-redis-node-0"),
                    data: new Dictionary<string, object> { ["endpoint"] = "10.2.0.5:6380" }
                )
            );
        }

        await using var provider = services.BuildServiceProvider();
        var trigger = new HealthCheck(
            healthCheckService: provider.GetRequiredService<HealthCheckService>(),
            logger: logger
        );
        var context = new DefaultHttpContext { RequestAborted = TestContext.Current.CancellationToken };
        var actionResult = await trigger.Run(request: context.Request, functionContext: null!);
        var result = (healthy
            ? Assert.IsType<OkObjectResult>(@object: actionResult)
            : Assert.IsType<ObjectResult>(@object: actionResult)
        );

        Assert.Equal(expected: (healthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable), actual: result.StatusCode.GetValueOrDefault(defaultValue: StatusCodes.Status200OK));
        var json = JsonSerializer.Serialize(value: result.Value, options: WebJson);
        var parsed = JsonNode.Parse(json: json)!.AsObject();

        Assert.Equal(expected: (healthy ? "Healthy" : "Unhealthy"), actual: ((string?)parsed["status"]));
        Assert.Single(collection: parsed);
        Assert.DoesNotContain(expectedSubstring: "RedisCache", actualString: json);
        Assert.DoesNotContain(expectedSubstring: "internal-cache", actualString: json);
        Assert.DoesNotContain(expectedSubstring: "AUTH password rejected", actualString: json);
        Assert.DoesNotContain(expectedSubstring: "10.2.0.5", actualString: json);
        Assert.Equal(expected: (healthy ? 0 : 1), actual: logger.Entries.Count);
        if (!healthy) {
            var logged = Assert.Single(collection: logger.Entries);

            Assert.Equal(expected: LogLevel.Error, actual: logged.Level);
            Assert.IsType<InvalidOperationException>(@object: logged.Exception);
            Assert.Contains(expectedSubstring: "RedisCache:Default", actualString: logged.Message);
            Assert.Contains(expectedSubstring: "Unhealthy", actualString: logged.Message);
            Assert.Contains(expectedSubstring: "internal-cache.redis.cache.windows.net", actualString: logged.Message);
        }
    }

    [Fact]
    public async Task DegradedEntryIsLoggedByNameAndTheProbeStillSucceeds() {
        var services = new ServiceCollection();
        var logger = new RecordingLogger();

        services.AddLogging();
        _ = services.AddHealthChecks().AddCheck(
            name: "RedisCache:Default",
            check: static () => HealthCheckResult.Degraded(description: "slow replica")
        );
        await using var provider = services.BuildServiceProvider();
        var trigger = new HealthCheck(
            healthCheckService: provider.GetRequiredService<HealthCheckService>(),
            logger: logger
        );
        var context = new DefaultHttpContext { RequestAborted = TestContext.Current.CancellationToken };
        var actionResult = await trigger.Run(request: context.Request, functionContext: null!);

        _ = Assert.IsType<OkObjectResult>(@object: actionResult);
        var logged = Assert.Single(collection: logger.Entries);

        Assert.Equal(expected: LogLevel.Warning, actual: logged.Level);
        Assert.Contains(expectedSubstring: "RedisCache:Default", actualString: logged.Message);
        Assert.Contains(expectedSubstring: "Degraded", actualString: logged.Message);
    }

    private sealed class RecordingLogger : ILogger<HealthCheck> {
        public List<(LogLevel Level, Exception? Exception, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => (logLevel >= LogLevel.Warning);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add(item: (logLevel, exception, formatter(state, exception)));
    }
}
