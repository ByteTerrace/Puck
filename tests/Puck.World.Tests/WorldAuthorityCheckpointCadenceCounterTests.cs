using Xunit;

using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>Laws for <see cref="WorldAuthorityCheckpointCadenceCounter"/> — its arming decision is a pure function
/// of the cumulative engine-tick total it has been fed, never of how that total was chunked into master steps
/// (the checkpoint-cadence-invariance claim, scoped to this counter's own contract).</summary>
public sealed class WorldAuthorityCheckpointCadenceCounterTests {
    // Splits total into a pseudo-random sequence of positive deltas that sums to EXACTLY total, via sorted cut
    // points — deterministic (a fixed seed, no wall clock), so the test itself stays reproducible.
    private static ulong[] Chunk(ulong total, int pieceCount, int seed) {
        var random = new Random(Seed: seed);
        var cuts = new ulong[(pieceCount - 1)];

        for (var index = 0; (index < cuts.Length); index++) {
            cuts[index] = ((ulong)random.NextInt64(
                maxValue: ((long)total),
                minValue: 0
            ));
        }

        Array.Sort(array: cuts);

        var deltas = new ulong[pieceCount];
        var previous = 0UL;

        for (var index = 0; (index < cuts.Length); index++) {
            deltas[index] = (cuts[index] - previous);
            previous = cuts[index];
        }

        deltas[^1] = (total - previous);

        return deltas;
    }
    private static bool Replay(ulong[] chunking) {
        var counter = new WorldAuthorityCheckpointCadenceCounter();

        foreach (var delta in chunking) {
            counter.NoteMasterStep(stepTicks: delta);
        }

        return counter.IsArmed;
    }

    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void ArmingIsInvariantAcrossHowTheSameTotalTicksAreChunked(bool crossesTheCadence) {
        var cadence = WorldAuthorityCheckpointCadence.EngineTicks;
        var total = (crossesTheCadence
            ? ((2UL * cadence) + 1UL)
            : (cadence - 1UL)
        );

        var single = Replay(chunking: [total]);
        var sevenPieces = Replay(chunking: Chunk(
            pieceCount: 7,
            seed: 12345,
            total: total
        ));
        var oddPieces = Replay(chunking: Chunk(
            pieceCount: 41,
            seed: 67890,
            total: total
        ));

        Assert.Equal(
            actual: single,
            expected: crossesTheCadence
        );
        Assert.Equal(
            actual: sevenPieces,
            expected: crossesTheCadence
        );
        Assert.Equal(
            actual: oddPieces,
            expected: crossesTheCadence
        );
    }
    [Fact]
    public void ClearResetsBothTheArmedFlagAndTheAccumulator() {
        var counter = new WorldAuthorityCheckpointCadenceCounter();

        counter.NoteMasterStep(stepTicks: WorldAuthorityCheckpointCadence.EngineTicks);

        Assert.True(condition: counter.IsArmed);

        counter.Clear();

        Assert.False(condition: counter.IsArmed);

        // A single tick short of the cadence must NOT re-arm immediately — proves Clear() reset the accumulator to
        // zero rather than leaving the overshoot in place.
        counter.NoteMasterStep(stepTicks: (WorldAuthorityCheckpointCadence.EngineTicks - 1UL));

        Assert.False(condition: counter.IsArmed);
    }
    [Fact]
    public void ExactlyAtTheCadenceArms() {
        var counter = new WorldAuthorityCheckpointCadenceCounter();

        Assert.False(condition: counter.IsArmed);

        counter.NoteMasterStep(stepTicks: (WorldAuthorityCheckpointCadence.EngineTicks - 1UL));

        Assert.False(condition: counter.IsArmed);

        counter.NoteMasterStep(stepTicks: 1UL);

        Assert.True(condition: counter.IsArmed);
    }
    [Fact]
    public void RequestNowArmsWithoutTouchingTheAccumulator() {
        var counter = new WorldAuthorityCheckpointCadenceCounter();

        counter.NoteMasterStep(stepTicks: (WorldAuthorityCheckpointCadence.EngineTicks / 2UL));
        counter.RequestNow();

        Assert.True(condition: counter.IsArmed);

        counter.Clear();

        // The accumulator was untouched by RequestNow — after Clear() (which zeroed it), feeding the SAME half-
        // cadence again must not arm on its own; only reaching the full cadence a second time does.
        counter.NoteMasterStep(stepTicks: (WorldAuthorityCheckpointCadence.EngineTicks / 2UL));

        Assert.False(condition: counter.IsArmed);
    }
}
