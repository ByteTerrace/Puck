using System.Text.Json;
using Xunit;

namespace Puck.Cli.Release.Tests;

/// <summary>
/// CONTRACT UNDER TEST: each Azure host that exports to Azure Monitor (the Functions worker and the Actors silo)
/// transmits its final telemetry before it exits. Azure Monitor's exporter otherwise persists a shutting-down
/// provider's batch to local offline storage for a later process to upload, which a retiring Flex worker or a replaced
/// instance never runs, so the built runtime configuration each host reads must carry the exporter's
/// <c>DisablePersistOnShutdown</c> switch.
/// </summary>
public sealed class AzureHostTelemetryShutdownLawTests {
    [InlineData("Puck.Azure.Functions")]
    [InlineData("Puck.Actors")]
    [Theory]
    public void TheHostsRuntimeConfigTurnsOffPersistOnShutdown(string host) {
        var path = Path.Combine(
            path1: RepositoryPaths.RequireRoot(),
            path2: $"src/{host}/bin/Release/net10.0/{host}.runtimeconfig.json"
        );

        using var document = JsonDocument.Parse(utf8Json: File.ReadAllBytes(path: path));
        var properties = document.RootElement.GetProperty(propertyName: "runtimeOptions").GetProperty(propertyName: "configProperties");

        Assert.True(
            condition: properties.TryGetProperty(propertyName: "Azure.Monitor.OpenTelemetry.Exporter.DisablePersistOnShutdown", value: out var value),
            userMessage: $"{host}'s runtimeconfig.json does not carry Azure.Monitor.OpenTelemetry.Exporter.DisablePersistOnShutdown"
        );
        Assert.Equal(expected: JsonValueKind.True, actual: value.ValueKind);
    }
}
