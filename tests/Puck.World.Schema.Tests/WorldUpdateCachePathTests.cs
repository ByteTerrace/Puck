using Xunit;

namespace Puck.World.Schema.Tests;

public sealed class WorldUpdateCachePathTests {
    [InlineData("../outside")]
    [InlineData("one/../../outside")]
    [InlineData("/tmp/updates")]
    [InlineData("C:/updates")]
    [InlineData("one\\two")]
    [InlineData("one/.. /two")]
    [InlineData(".")]
    [InlineData("")]
    [Theory]
    public void AuthoredUpdateCacheCannotEscapeTheHostDirectory(string path) {
        var definition = new WorldDefinition(Simulation: new WorldSimulationDefaults(RateHz: 240), Update: new WorldUpdateDefaults(CacheRoot: path));

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var reason));
        Assert.Contains(actualString: reason, expectedSubstring: "update.cacheRoot");
    }
    [InlineData(null)]
    [InlineData("local")]
    [InlineData("local/preview")]
    [Theory]
    public void RelativeUpdateCacheAndHostDefaultAreAccepted(string? path) {
        var definition = new WorldDefinition(Simulation: new WorldSimulationDefaults(RateHz: 240), Update: new WorldUpdateDefaults(CacheRoot: path));

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var reason), userMessage: reason);
    }
}
