using System.Numerics;
using Puck.Assets.Documents;
using Xunit;

namespace Puck.World.Schema.Tests;

public sealed class MissingSpatialInputLawTests {
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [Theory]
    public void MissingScreenFrameVectorIsRefused(int component) {
        var screen = new WorldScreen(
            Index: 0,
            Origin: ((component == 0) ? null! : new DocumentVector3(value: Vector3.Zero)),
            Right: ((component == 1) ? null! : new DocumentVector3(value: Vector3.UnitX)),
            Up: ((component == 2) ? null! : new DocumentVector3(value: Vector3.UnitY)),
            HalfWidth: 1f, HalfHeight: 1f, HalfDepth: .1f, Round: 0f,
            Source: new WorldScreenSource.None(), Route: default
        );

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: new WorldDefinition(ScreensRaw: [screen]), reason: out var reason));
        Assert.Contains(actualString: reason, expectedSubstring: "frame vectors");
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void MissingSpeakerPositionIsRefused(bool bed) {
        var feed = new WorldSpeakerFeed(new WorldSpeakerSource.None(), WorldSpeakerFeed.ChannelMix, 1f);
        WorldSpeaker speaker = (bed
            ? new WorldSpeaker.Bed("speaker", null!, 2f, 1f, feed)
            : new WorldSpeaker.Fixed("speaker", null!, feed));

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: new WorldDefinition(SpeakersRaw: [speaker]), reason: out var reason));
        Assert.Contains(actualString: reason, expectedSubstring: (bed ? ".center" : ".position"));
    }
}
