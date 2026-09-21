using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>CONTRACT UNDER TEST: the authority checkpoint wire carries pool snapshots and both generation tags of
/// a pool-backed interaction latch, and restore reinstalls them as one coherent simulation state.</summary>
public sealed class WorldPoolCheckpointLawTests {
    [Fact]
    public void PoolValuesAndGenerationTaggedInteractionLatchRoundTripTogether() {
        using var fixture = Fixtures.FreshServer(definition: WorldPoolCarrierLawTests.Document());

        WorldPoolCarrierLawTests.JoinAttachedSeats(fixture: fixture);
        fixture.Step();
        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(
            checkpoint: out var captured,
            hostRow: WorldAuthorityHostRowCheckpoint.Empty,
            reason: out var captureReason
        ), userMessage: captureReason);

        var capturedEntries = captured!.Server.InteractionGateHeld.OrderBy(keySelector: entry => entry.Left).ThenBy(keySelector: entry => entry.Right).ToArray();

        Assert.Equal(expected: 2, actual: capturedEntries.Length);
        Assert.All(collection: capturedEntries, action: entry => {
            Assert.True(condition: entry.Held);
            Assert.Equal(expected: 0L, actual: entry.LeftGeneration);
            Assert.Equal(expected: 0L, actual: entry.RightGeneration);
        });
        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(
            bytes: WorldAuthorityCheckpointCodec.Encode(checkpoint: captured),
            checkpoint: out var decoded,
            reason: out var decodeReason
        ), userMessage: decodeReason);

        Assert.Equal(expected: capturedEntries, actual: decoded!.Server.InteractionGateHeld.OrderBy(keySelector: entry => entry.Left).ThenBy(keySelector: entry => entry.Right).ToArray());
        Assert.True(condition: fixture.Server.Arena.Catalog.TryGetPool(name: WorldPoolCarrierLawTests.Name(value: "fighters"), pool: out var pool));
        var original = fixture.Server.Arena.Catalog.CreateInstanceHandle(poolOrdinal: pool!.Ordinal, slot: 0, generation: 0L);

        Assert.True(condition: fixture.Server.Arena.TryRelease(handle: original, reason: out _));

        fixture.Server.RestoreCheckpoint(checkpoint: decoded);

        var restored = fixture.Server.Arena.Catalog.CreateInstanceHandle(poolOrdinal: pool.Ordinal, slot: 0, generation: 0L);

        Assert.True(condition: fixture.Server.Arena.TryRead(fieldOrdinal: 0, handle: restored, value: out var hits));
        Assert.Equal(expected: 11L, actual: hits.AsInt);
        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(
            checkpoint: out var recaptured,
            hostRow: WorldAuthorityHostRowCheckpoint.Empty,
            reason: out var recaptureReason
        ), userMessage: recaptureReason);
        Assert.Equal(
            expected: capturedEntries,
            actual: recaptured!.Server.InteractionGateHeld.OrderBy(keySelector: entry => entry.Left).ThenBy(keySelector: entry => entry.Right).ToArray()
        );
    }
}
