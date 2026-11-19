using System.Numerics;

using Puck.Commands;
using Puck.World.Authoring;
using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>A driver's <c>lead</c> is presentation only: authoring it changes nothing the authority computes, so the same
/// run gives the same authority hashes at every tick with the lead authored and removed.</summary>
public sealed class CreationLeadAuthorityLawTests {
    private const string Channel = "vent";
    private const string Row = "vent";

    private static WorldDefinition World(string? lead) {
        var creation = CreationFixtures.Rig(
            drivers: [new CreationDriverDocument(Cadence: 1f, Lead: lead, Name: "press", Signal: $"state.{Row}", When: [CreationDriverDocument.WhenAlways])],
            CreationFixtures.SwingingLimb(swing: new ShapeSwingDocument(Amplitude: 0.5f, Axis: Vector3.UnitX, Driver: "press", Pivot: Vector3.Zero, Wave: CreationWave.Linear))
        );
        var world = CreationFixtures.RigWorld(creation: creation, state: [StateFixtures.IntSlot(name: Row)]);

        return world with { ChannelsRaw = [.. world.Channels, new WorldChannel(Composition: true, Name: Channel, Shape: ChannelShape.Binary)] };
    }

    [Fact]
    public void AuthorityHashesAreEqualWithLeadAuthoredAndRemoved() {
        Assert.Equal(expected: string.Empty, actual: (WorldDefinitionValidator.TryValidateLocally(definition: World(lead: Channel), reason: out var reason) ? string.Empty : reason));
        using var led = Fixtures.FreshServer(definition: World(lead: Channel));
        using var plain = Fixtures.FreshServer(definition: World(lead: null));

        for (var tick = 0; (tick < 60); tick++) {
            if (tick is 10 or 40) {
                foreach (var fixture in new[] { led, plain }) {
                    fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(Principal.Console, Row: Row, Key: StateRow.SlotKey.Value, Value: ((tick == 10) ? 1 : 0), Kind: WorldDocumentWriteKind.Set));
                }
            }
            led.Step();
            plain.Step();
            Assert.Equal(
                expected: WorldStateHashComposition.Hash(scope: WorldStateHashScope.World, server: plain.Server, tick: ((ulong)tick)),
                actual: WorldStateHashComposition.Hash(scope: WorldStateHashScope.World, server: led.Server, tick: ((ulong)tick))
            );
        }
    }
}
