using Puck.World.Client;
using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// The laws behind <c>world.history</c>: a seek to any tick of the window, backward or forward, reproduces the
/// authoritative hash the live run reached there, across seeds and worlds; the proof every seek runs fails on a
/// keyframe from the wrong span and on input fed in the wrong order; a branch resumed behind the head is the run that
/// took that path directly; a diff reports exactly the changed rows; a replay-edit names the tick its edit first moves
/// the world; and recording holds a bounded, counted budget without allocating on a steady tick.
/// </summary>
public sealed class InSessionHistoryLawTests {
    public static TheoryData<string, ulong> WorldsAndSeeds() {
        var data = new TheoryData<string, ulong>();

        // Nexus is the shipped world whose tick costs the most, so each of its seeds is a class of its own
        // (NexusSeekingLawTests): classes run side by side, and a theory's rows run one after another.
        foreach (var world in new[] { "snake" }) {
            foreach (var seed in new[] { 3UL, 77UL, 0x5EEDUL }) {
                data.Add(p1: world, p2: seed);
            }
        }

        return data;
    }

    private static WorldDefinition? Definition(string world) => (world switch {
        "nexus" => AuthoredGameFixtures.Nexus,
        "snake" => AuthoredGameFixtures.Program(module: "snake"),
        _ => null,
    });

    [MemberData(nameof(WorldsAndSeeds))]
    [Theory]
    public void SeekingBackThenForwardReproducesEveryRecordedTick(string world, ulong seed) => SeekBackThenForward(
        seed: seed,
        world: world
    );

    /// <summary>The law of <see cref="SeekingBackThenForwardReproducesEveryRecordedTick"/> over one world and seed: the
    /// window of a run of that world, sought back to its oldest tick, forward through every recorded tick, and across a
    /// seeded scatter of jumps, reproduces the authoritative hash the live run reached at each tick it lands on.</summary>
    /// <param name="world">The world: <c>fixture</c>, <c>snake</c> or <c>nexus</c>.</param>
    /// <param name="seed">The seed of the run's input stream and of the scatter.</param>
    internal static void SeekBackThenForward(string world, ulong seed) {
        using var harness = new WorldHistoryHarness(
            definition: Definition(world: world),
            seed: seed
        );
        var ticks = ((world == "nexus") ? 150 : 240);

        harness.Steps(count: ticks);

        var status = harness.History.Status();
        var oldest = status.Oldest!.Value;
        var head = status.Head!.Value;

        Assert.True(
            condition: (status.Keyframes >= 2),
            userMessage: $"the window holds {status.Keyframes} keyframe(s); the law needs a seek that crosses spans"
        );

        // Back to the oldest tick, then forward one tick at a time: every recorded tick is reached and proven.
        _ = harness.SeekAndProve(target: oldest);

        for (var tick = (oldest + 1UL); (tick <= head); tick++) {
            _ = harness.SeekAndProve(target: tick);
        }

        // Then a seeded scatter of jumps in both directions, each restoring whichever keyframe sits nearest.
        var random = seed;

        for (var jump = 0; (jump < 12); jump++) {
            random = ((random * 6364136223846793005UL) + 1442695040888963407UL);

            var target = (oldest + ((random >> 33) % ((head - oldest) + 1UL)));

            _ = harness.SeekAndProve(target: target);
        }

        var counters = harness.History.Status().Counters;

        Assert.True(condition: (counters.InPlaceRestores > 0L));
        Assert.Equal(
            expected: 0L,
            actual: counters.RebuildRestores
        );
    }
}
