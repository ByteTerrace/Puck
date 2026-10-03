using System.Text.Json;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the Functions worker transmits its final telemetry before it exits. Azure Monitor's exporter
/// otherwise persists a shutting-down provider's batch to local offline storage for a later process to upload, which a
/// retiring Flex worker never runs, so the built runtime configuration the host reads must carry the exporter's
/// <c>DisablePersistOnShutdown</c> switch.
/// </summary>
public sealed class FunctionsTelemetryShutdownLawTests {
    [Fact]
    public void TheFunctionsWorkerRuntimeConfigTurnsOffPersistOnShutdown() {
        var path = Path.Combine(
            path1: RepositoryPaths.RequireRoot(),
            path2: "src/Puck.Azure.Functions/bin/Release/net10.0/Puck.Azure.Functions.runtimeconfig.json"
        );

        using var document = JsonDocument.Parse(utf8Json: File.ReadAllBytes(path: path));
        var properties = document.RootElement.GetProperty(propertyName: "runtimeOptions").GetProperty(propertyName: "configProperties");

        Assert.True(
            condition: properties.TryGetProperty(propertyName: "Azure.Monitor.OpenTelemetry.Exporter.DisablePersistOnShutdown", value: out var value),
            userMessage: "the Functions worker's runtimeconfig.json does not carry Azure.Monitor.OpenTelemetry.Exporter.DisablePersistOnShutdown"
        );
        Assert.Equal(expected: JsonValueKind.True, actual: value.ValueKind);
    }
}
