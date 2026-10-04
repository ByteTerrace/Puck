using System.Text.Json;
using Xunit;

namespace Puck.World.Tests;

[Collection(AllocationCollection.Name)]
public sealed partial class TemporalShadowCanaryLawTests {
    [Fact]
    public void TheUnamortizedReferenceUsesTheWorldViewInItsOwnScheduledBoot() {
        var directory = RepositoryPaths.Resolve(relativePath: "tests/Puck.World.Canaries/temporal-shadows");
        var path = Path.Combine(path1: directory, path2: "reference.world.json");

        Assert.True(condition: File.Exists(path: path), userMessage: "The unamortized seat reference is present.");
        Assert.True(condition: WorldDefinitionLoader.TryLoadFile(path, out var reference, out var reason), userMessage: reason);
        Assert.False(condition: reference!.Render.ShadowAmortize);
        Assert.True(condition: reference.Render.Temporal);
        Assert.Equal(expected: 2, actual: reference.Render.ShadowLights);
        Assert.Equal(expected: 6, actual: reference.Captures!.Rows.Count);
        Assert.All(collection: reference.Captures.Rows, action: row => {
            Assert.Equal(expected: "world", actual: row.Instance);
            Assert.EndsWith(expectedEndString: "-reference", actualString: row.Station.ToString());
        });
        using var manifest = JsonDocument.Parse(File.ReadAllText(path: Path.Combine(path1: directory, path2: "canary.json")));

        foreach (var name in new[] { "positive", "discriminating" }) {
            var leg = manifest.RootElement.GetProperty(propertyName: name);

            Assert.True(condition: leg.GetProperty(propertyName: "runSchedule").GetBoolean());
            Assert.Equal(expected: "tests/Puck.World.Canaries/temporal-shadows/reference.world.json", actual: leg.GetProperty(propertyName: "relaunch").GetProperty(propertyName: "sourceWorld").GetString());
            foreach (var assertion in leg.GetProperty(propertyName: "expect").EnumerateArray().Where(predicate: row => (row.GetProperty(propertyName: "type").GetString() == "imageDifference"))) {
                Assert.Equal(expected: 2, actual: assertion.GetProperty(propertyName: "maximumMeanCodes").GetInt32());
            }
        }
    }
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

        foreach (var station in new[] { "occluder", "departure", "return", "light", "owner" }) {
            var row = rows.Single(predicate: row => (row.GetProperty(propertyName: "station").GetString() == station));
            // A converge capture starts at jitter index zero and would force a full receiver rejection, hiding a
            // broken motion rule. Transition captures must read the ordinary rejection frame instead.
            Assert.False(condition: row.TryGetProperty(propertyName: "converge", value: out _));
            Assert.Equal("world", row.GetProperty(propertyName: "instance").GetString());
        }
        var placements = json.RootElement.GetProperty(propertyName: "placements").GetProperty(propertyName: "rows").EnumerateArray().ToArray();

        Assert.Contains(collection: placements, filter: static row => (row.GetProperty(propertyName: "id").GetString() == "receiver"));
        Assert.Contains(collection: placements, filter: static row => (row.GetProperty(propertyName: "id").GetString() == "occluder"));
        Assert.Equal(expected: 8, actual: placements.Count(predicate: static row => row.GetProperty(propertyName: "id").GetString()!.StartsWith(comparisonType: StringComparison.Ordinal, value: "grid-")));
        var schedule = json.RootElement.GetProperty(propertyName: "schedule").GetProperty(propertyName: "rows").EnumerateArray().ToArray();

        Assert.Contains(collection: schedule, filter: static row => (row.GetProperty(propertyName: "command").GetString() == "world.state.cell.set shadowX $value 0"));
        Assert.DoesNotContain(collection: schedule, filter: static row => row.GetProperty(propertyName: "command").GetString()!.StartsWith(comparisonType: StringComparison.Ordinal, value: "world.row.set placements"));
        Assert.DoesNotContain(collection: schedule, filter: static row => row.GetProperty(propertyName: "command").GetString()!.Contains(comparisonType: StringComparison.Ordinal, value: "placements receiver"));
        var delay = (amortize ? 0 : 2);

        Assert.Contains(collection: schedule, filter: row => ((row.GetProperty(propertyName: "tick").GetInt32() == (110 + delay))
            && (row.GetProperty(propertyName: "command").GetString() == "world.state.cell.set shadowZ $value 64")));
        Assert.Contains(collection: schedule, filter: row => ((row.GetProperty(propertyName: "tick").GetInt32() == (120 + delay))
            && (row.GetProperty(propertyName: "command").GetString() == "world.state.cell.set shadowZ $value 0")));
    }
}
