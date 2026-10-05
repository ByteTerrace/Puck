using System.Text.Json;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public sealed class IndirectControlsLawTests {
    [Fact]
    public void IndirectSourcesDepthAndApplicationRetainTheSharedDocumentRoundTrip() {
        var document = WorldSources.LowerClean(body: """
            render {
              indirect {
                tier: off
                sources { lights: 0.125, emission: 0.25, screens: 0, sky: 0.5, feedback: 0.75 }
                bounces: 1
                apply { intensity: 0.5, tint: "#FF0000", contact: 0.25 }
                bodies: receive
              }
            }
            """);
        using var wire = JsonDocument.Parse(document.ToJsonString());
        var indirect = wire.RootElement.GetProperty(propertyName: "render").GetProperty(propertyName: "indirect");

        Assert.Equal("off", indirect.GetProperty(propertyName: "tier").GetString());
        Assert.Equal(.125d, indirect.GetProperty(propertyName: "sources").GetProperty(propertyName: "lights").GetDouble());
        Assert.Equal(0, indirect.GetProperty(propertyName: "sources").GetProperty(propertyName: "screens").GetInt32());
        Assert.Equal(1, indirect.GetProperty(propertyName: "bounces").GetInt32());
        Assert.Equal("#FF0000", indirect.GetProperty(propertyName: "apply").GetProperty(propertyName: "tint").GetString());
        var printed = WorldSources.AssertRoundTrips(document);

        Assert.Contains(actualString: printed, expectedSubstring: "indirect");
        Assert.Contains(actualString: printed, expectedSubstring: "feedback");
    }
}
