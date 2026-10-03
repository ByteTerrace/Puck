using System.Text.Json;
using Xunit;

namespace Puck.World.Tests;

public sealed class TemporalShadowCanaryLawTests {
    [InlineData("fixture.world.json", true)]
    [InlineData("discriminating.world.json", false)]
    [Theory]
    public void TheShadowFixtureKeepsReceiversStillAndCapturesTransitionsWithoutRestartingJitter(string file, bool amortize) {
        var path = RepositoryPaths.Resolve(relativePath: $"tests/Puck.World.Canaries/temporal-shadows/{file}");

        Assert.True(condition: File.Exists(path: path), userMessage: $"The temporal-shadows rejection fixture '{file}' is present.");
        Assert.True(condition: WorldDefinitionLoader.TryLoad(File.ReadAllBytes(path: path), path, out var definition, out var reason), userMessage: reason);
        Assert.True(condition: definition!.Render.Temporal);
        Assert.Equal(amortize, definition.Render.ShadowAmortize);
        using var json = JsonDocument.Parse(File.ReadAllBytes(path: path));
        var rows = json.RootElement.GetProperty(propertyName: "captures").GetProperty(propertyName: "rows").EnumerateArray().ToArray();

        foreach (var station in new[] { "occluder", "light", "owner" }) {
            var row = rows.Single(predicate: row => (row.GetProperty(propertyName: "station").GetString() == station));
            // A converge capture starts at jitter index zero and would force a full receiver rejection, hiding a
            // broken motion rule. Transition captures must read the ordinary rejection frame instead.
            Assert.False(condition: row.TryGetProperty(propertyName: "converge", value: out _));
            Assert.Equal("world", row.GetProperty(propertyName: "instance").GetString());
        }
        var placements = json.RootElement.GetProperty(propertyName: "placements").GetProperty(propertyName: "rows").EnumerateArray().ToArray();

        Assert.Contains(collection: placements, filter: static row => (row.GetProperty(propertyName: "id").GetString() == "receiver"));
        Assert.Contains(collection: placements, filter: static row => (row.GetProperty(propertyName: "id").GetString() == "occluder"));
        var schedule = json.RootElement.GetProperty(propertyName: "schedule").GetProperty(propertyName: "rows").EnumerateArray().ToArray();

        Assert.Contains(collection: schedule, filter: static row => row.GetProperty(propertyName: "command").GetString()!.StartsWith(comparisonType: StringComparison.Ordinal, value: "world.row.set placements occluder position"));
        Assert.DoesNotContain(collection: schedule, filter: static row => row.GetProperty(propertyName: "command").GetString()!.Contains(comparisonType: StringComparison.Ordinal, value: "placements receiver"));
    }
}
