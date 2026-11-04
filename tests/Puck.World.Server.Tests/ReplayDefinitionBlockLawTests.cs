using Puck.Networking;
using Xunit;

namespace Puck.World.Server.Tests;

public sealed class ReplayDefinitionBlockLawTests {
    [Fact]
    public void ALocalTapeRoundTripsADefinitionLargerThanANetworkDocument() {
        var definition = LargeDefinition();
        var encoded = WorldReplaySnapshot.Encode(Snapshot(definition));
        using var stream = new MemoryStream(encoded, writable: false);
        var restored = WorldReplaySnapshot.Read(stream);

        Assert.Equal(definition.Length, restored.DefinitionJson.Length);
        Assert.True(definition.AsSpan().SequenceEqual(restored.DefinitionJson));
        Assert.Equal(stream.Length, stream.Position);
    }

    [Fact]
    public void ALargeDeclaredDefinitionStillRequiresItsBytesBeforeAllocatingTheBlock() {
        var definition = LargeDefinition();
        var encoded = WorldReplaySnapshot.Encode(Snapshot(definition));
        using var stream = new MemoryStream(encoded, 0, encoded.Length - (definition.Length / 2), writable: false);

        var refusal = Assert.Throws<InvalidDataException>(() => WorldReplaySnapshot.Read(stream));
        Assert.Contains(nameof(WireRefusal.PayloadTruncated), refusal.Message);
    }

    private static byte[] LargeDefinition() {
        var json = WorldDefinitionSerialization.Serialize(Fixtures.BuildDocument());
        var definition = new byte[WireLimits.MaxDocumentBytes + json.Length];
        definition.AsSpan().Fill((byte)' ');
        json.CopyTo(definition, 0);
        return definition;
    }

    private static WorldReplaySnapshot Snapshot(byte[] definition) => new() {
        Authority = "boot",
        Instance = "boot",
        DefinitionJson = definition,
        MountedAddons = [],
        RecordedHashes = [],
        RecordedAuthoritativeHashes = [],
        Seats = [],
        SimulationRate = 30,
        Ticks = [],
    };
}