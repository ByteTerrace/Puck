using Puck.Audio.Simulation;

namespace Puck.Audio.Tests;

public sealed class VoiceBabblerTests {
    [Fact]
    public void SyllableCountProducesExactlyNStrictlyIncreasingTriggerTicks() {
        var destination = new ulong[6];

        VoiceBabbler.ComputeTriggerTicks(baseTick: 1_000UL, cadenceTicks: 600, destination: destination, identitySeed: 11UL, syllableCount: 6, utteranceOrdinal: 3UL);

        Assert.True(condition: (destination[0] >= 1_000UL));

        for (var i = 1; (i < destination.Length); i++) {
            Assert.True(condition: (destination[i] > destination[(i - 1)]));
        }
    }
    [Fact]
    public void ZeroSyllableCountWritesNothingAndDoesNotThrow() {
        var destination = Array.Empty<ulong>();

        VoiceBabbler.ComputeTriggerTicks(baseTick: 0UL, cadenceTicks: 100, destination: destination, identitySeed: 0UL, syllableCount: 0, utteranceOrdinal: 0UL);
    }
    [Fact]
    public void NegativeSyllableCountThrows() {
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => VoiceBabbler.ComputeTriggerTicks(
            baseTick: 0UL, cadenceTicks: 100, destination: new ulong[4], identitySeed: 0UL, syllableCount: -1, utteranceOrdinal: 0UL));
    }
    [Fact]
    public void NonPositiveCadenceTicksThrows() {
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => VoiceBabbler.ComputeTriggerTicks(
            baseTick: 0UL, cadenceTicks: 0, destination: new ulong[4], identitySeed: 0UL, syllableCount: 1, utteranceOrdinal: 0UL));
    }
    [Fact]
    public void DestinationShorterThanSyllableCountThrows() {
        Assert.Throws<ArgumentException>(testCode: () => VoiceBabbler.ComputeTriggerTicks(
            baseTick: 0UL, cadenceTicks: 100, destination: new ulong[2], identitySeed: 0UL, syllableCount: 5, utteranceOrdinal: 0UL));
    }
    [Fact]
    public void TwoFreshRunsWithTheIdenticalSeedStayBitIdentical() {
        var first = new ulong[10];
        var second = new ulong[10];

        VoiceBabbler.ComputeTriggerTicks(baseTick: 0UL, cadenceTicks: 500, destination: first, identitySeed: 42UL, syllableCount: 10, utteranceOrdinal: 7UL);
        VoiceBabbler.ComputeTriggerTicks(baseTick: 0UL, cadenceTicks: 500, destination: second, identitySeed: 42UL, syllableCount: 10, utteranceOrdinal: 7UL);

        Assert.True(condition: first.AsSpan().SequenceEqual(other: second));
    }
    [Fact]
    public void ADifferentUtteranceOrdinalDrawsADifferentSequenceFromTheSameIdentity() {
        var first = new ulong[10];
        var second = new ulong[10];

        VoiceBabbler.ComputeTriggerTicks(baseTick: 0UL, cadenceTicks: 500, destination: first, identitySeed: 42UL, syllableCount: 10, utteranceOrdinal: 7UL);
        VoiceBabbler.ComputeTriggerTicks(baseTick: 0UL, cadenceTicks: 500, destination: second, identitySeed: 42UL, syllableCount: 10, utteranceOrdinal: 8UL);

        Assert.False(condition: first.AsSpan().SequenceEqual(other: second));
    }
    [Fact]
    public void ZeroJitterCadenceProducesExactCadenceGridTicks() {
        // cadenceTicks=3 floors below JitterCeilingDivisor (4), so the jitter ceiling is 0 and every trigger lands
        // exactly on the cadence grid — the "zero jitter" leg of the discriminating pair below.
        var destination = new ulong[5];

        VoiceBabbler.ComputeTriggerTicks(baseTick: 100UL, cadenceTicks: 3, destination: destination, identitySeed: 1UL, syllableCount: 5, utteranceOrdinal: 1UL);

        for (var i = 0; (i < destination.Length); i++) {
            Assert.Equal(expected: (100UL + ((ulong)(i * 3))), actual: destination[i]);
        }
    }
    [Fact]
    public void AuthoredJitterCadenceStaysWithinItsBoundAndRealDeviatesFromTheGrid() {
        // The "authored jitter" leg of the pair: a cadence well above the divisor produces a nonzero ceiling, and
        // the resulting schedule must both respect that bound and actually differ from the exact grid — a jitter
        // implementation that silently collapsed to zero-jitter behavior would fail sawNonZeroJitter here.
        const long cadenceTicks = 800;
        const ulong ceiling = (cadenceTicks / VoiceBabbler.JitterCeilingDivisor);
        var destination = new ulong[8];

        VoiceBabbler.ComputeTriggerTicks(baseTick: 0UL, cadenceTicks: cadenceTicks, destination: destination, identitySeed: 9UL, syllableCount: 8, utteranceOrdinal: 2UL);

        var sawNonZeroJitter = false;

        for (var i = 0; (i < destination.Length); i++) {
            var grid = ((ulong)(i * cadenceTicks));
            var deviation = (destination[i] - grid);

            Assert.True(condition: (deviation <= ceiling));

            if (deviation > 0UL) {
                sawNonZeroJitter = true;
            }
        }

        Assert.True(condition: sawNonZeroJitter);
    }
}
