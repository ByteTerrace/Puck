using Puck.Maths;

namespace Puck.Audio.Simulation;

/// <summary>
/// The deterministic tick schedule behind synthesized voice babble: given an estimated syllable count, an
/// identity's authored inter-syllable cadence, and a caller-supplied deterministic seed, computes the trigger tick
/// of each syllable's short pitched voice — one trigger per syllable, cadence-spaced with bounded per-syllable
/// jitter, never one sustained tone for a whole utterance. A pure function of its arguments: no wall clock, no
/// mutation, no document parsing (estimating a syllable count from text, and choosing the <c>VoicePatch</c> each
/// trigger voices, are both the caller's job — this type stays document- and text-agnostic, matching every other
/// <c>Puck.Audio.Simulation</c> type). Identical inputs always produce a bit-identical schedule, on every machine,
/// on replay.
/// </summary>
/// <remarks>
/// Jitter follows the engine's established per-entity seeding split — state = a per-utterance ordinal, stream = a
/// per-identity seed, the same split <c>WorldStampPool</c>'s look-cue rest draw uses — rather than inventing a new
/// one: <see cref="ComputeTriggerTicks"/> seeds one <see cref="Pcg32XshRr"/> stream via
/// <c>Pcg32XshRr.Create(state: utteranceOrdinal, stream: identitySeed)</c>, so the same identity babbling a fresh
/// utterance draws a fresh jitter sequence, while replaying the same utterance draws the identical one. Each
/// syllable's jitter is a forward-only uniform draw in <c>[0, cadenceTicks / <see cref="JitterCeilingDivisor"/>]</c>
/// (floor division — a cadence under the divisor jitters not at all) added past that syllable's exact cadence-grid
/// tick, never subtracted before it; the ceiling stays under a quarter of the cadence, so two consecutive trigger
/// ticks can never collide or reorder regardless of the draws.
/// </remarks>
public static class VoiceBabbler {
    /// <summary>The jitter-ceiling divisor: a syllable's forward jitter is bounded to
    /// <c>cadenceTicks / JitterCeilingDivisor</c> ticks (floor division), always under a quarter of the cadence, so
    /// two consecutive trigger ticks can never collide or reorder.</summary>
    public const int JitterCeilingDivisor = 4;

    /// <summary>Computes the trigger tick of each syllable in a babbled utterance.</summary>
    /// <param name="syllableCount">The estimated syllable count (non-negative; the caller's text-estimation
    /// result). Zero writes nothing.</param>
    /// <param name="cadenceTicks">The identity's authored base inter-syllable tick spacing (positive).</param>
    /// <param name="identitySeed">The babbling identity's own deterministic seed — the <see cref="Pcg32XshRr"/>
    /// stream id, so distinct identities draw uncorrelated jitter (see <see cref="Pcg32XshRr"/>'s stream-
    /// correlation caveat for keeping derived ids small).</param>
    /// <param name="utteranceOrdinal">This utterance's ordinal within the identity's own babble history — the
    /// <see cref="Pcg32XshRr"/> seed state, so each new utterance from the same identity draws a fresh sequence.</param>
    /// <param name="baseTick">The tick the utterance's first syllable is scheduled from.</param>
    /// <param name="destination">Receives each syllable's trigger tick in increasing syllable order; must be at
    /// least <paramref name="syllableCount"/> long.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="syllableCount"/> is negative, or
    /// <paramref name="cadenceTicks"/> is not positive.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than
    /// <paramref name="syllableCount"/>.</exception>
    public static void ComputeTriggerTicks(int syllableCount, long cadenceTicks, ulong identitySeed, ulong utteranceOrdinal, ulong baseTick, Span<ulong> destination) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: syllableCount);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value: cadenceTicks, other: 0L);

        if (destination.Length < syllableCount) {
            throw new ArgumentException(
                message: $"destination (length {destination.Length}) is shorter than syllableCount ({syllableCount}).",
                paramName: nameof(destination)
            );
        }

        if (syllableCount == 0) {
            return;
        }

        var rng = Pcg32XshRr.Create(
            state: utteranceOrdinal,
            stream: identitySeed
        );
        var jitterCeiling = ((uint)Math.Min(val1: (cadenceTicks / JitterCeilingDivisor), val2: ((long)uint.MaxValue)));

        for (var i = 0; (i < syllableCount); i++) {
            var jitter = rng.NextUInt32(
                maximum: jitterCeiling,
                minimum: 0U
            );

            destination[i] = ((baseTick + ((ulong)(((long)i) * cadenceTicks))) + jitter);
        }
    }
}
