using System.Text.Json.Serialization;
using Puck.Maths;

namespace Puck.World;

/// <summary>A deterministic index-to-sample declaration shared by distributions, row assignment, color, and
/// population variation. The document selects the sequence and its phase; the engine owns its exact arithmetic.</summary>
/// <param name="Name">The sequence name: <see cref="None"/>, <see cref="Index"/>, <see cref="Additive"/>,
/// <see cref="R1"/>, or <see cref="R2"/>.</param>
/// <param name="Offset">The signed phase added to the caller's stable index before sampling.</param>
/// <param name="Step">The turn-sized increment for <see cref="Additive"/>; zero for every other sequence.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldSequence(string Name, int Offset, float Step) {
    /// <summary>No fill sequence; the region supplies its own finite enumeration.</summary>
    public const string None = "none";
    /// <summary>The adjusted integer index, used for authored cycles.</summary>
    public const string Index = "index";
    /// <summary>An authored additive recurrence, fractionalized where a unit sample is needed.</summary>
    public const string Additive = "additive";
    /// <summary>The exact one-dimensional golden-ratio low-discrepancy sequence.</summary>
    public const string R1 = "r1";
    /// <summary>The exact two-dimensional plastic-number low-discrepancy sequence.</summary>
    public const string R2 = "r2";

    /// <summary>Gets the inert sequence every absent sequence-typed field resolves to — <see cref="Index"/> at
    /// zero offset and step, selecting row 0 of whatever it addresses.</summary>
    public static WorldSequence IndexDefault { get; } = new(
        Name: Index,
        Offset: 0,
        Step: 0f
    );
    /// <summary>Gets the inert sequence for a fill site that requires <see cref="Additive"/> — zero offset and a
    /// unit step (the validator refuses a zero step; a whole-turn step samples the identical zero phase for every
    /// index, the additive-sequence equivalent of no variation).</summary>
    public static WorldSequence AdditiveDefault { get; } = new(
        Name: Additive,
        Offset: 0,
        Step: 1f
    );
}
/// <summary>The three independently authored sequences that seed a body's producer state.</summary>
/// <param name="Phase">The angular phase sequence.</param>
/// <param name="Weave">The scalar weave-frequency variation sequence.</param>
/// <param name="Activity">The paired activity-rate and altitude variation sequence.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldPopulationVariation(WorldSequence Phase, WorldSequence Weave, WorldSequence Activity) {
    /// <summary>Gets the inert variation — every axis samples the same value regardless of index.</summary>
    public static WorldPopulationVariation Default { get; } = new(
        Phase: WorldSequence.AdditiveDefault,
        Weave: WorldSequence.AdditiveDefault,
        Activity: new WorldSequence(
            Name: WorldSequence.R2,
            Offset: 0,
            Step: 0f
        )
    );
}
/// <summary>Exact runtime sampling for <see cref="WorldSequence"/> declarations.</summary>
public static class WorldSequenceSampling {
    private const double TwoPi = (2.0 * Math.PI);

    private static ulong AdjustedIndex(WorldSequence sequence, int index) {
        ArgumentNullException.ThrowIfNull(argument: sequence);
        ArgumentOutOfRangeException.ThrowIfNegative(value: index);
        var adjusted = checked((((long)index) + sequence.Offset));

        return ((adjusted >= 0L)
            ? (ulong)adjusted
            : throw new InvalidOperationException(message: $"Sequence '{sequence.Name}' offset {sequence.Offset} makes index {index} negative.")
        );
    }

    /// <summary>Maps an index or scalar sequence into one of <paramref name="count"/> equal buckets.</summary>
    public static int Bucket(WorldSequence sequence, int index, int count) {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            value: count,
            other: 1
        );
        var adjusted = AdjustedIndex(
            index: index,
            sequence: sequence
        );

        if (string.Equals(
            a: sequence.Name,
            b: WorldSequence.Index,
            comparisonType: StringComparison.Ordinal
        )) {
            return ((int)(adjusted % ((uint)count)));
        }

        if (string.Equals(
            a: sequence.Name,
            b: WorldSequence.R1,
            comparisonType: StringComparison.Ordinal
        )) {
            return ((int)((((ulong)LowDiscrepancy.R1(index: adjusted).Value) * ((uint)count)) >> 32));
        }

        if (string.Equals(
            a: sequence.Name,
            b: WorldSequence.Additive,
            comparisonType: StringComparison.Ordinal
        )) {
            return Math.Min(
                val1: (count - 1),
                val2: ((int)(Scalar(
                    index: index,
                    sequence: sequence
                ) * count))
            );
        }

        throw new InvalidOperationException(message: $"Sequence '{sequence.Name}' cannot select a row.");
    }
    /// <summary>Returns an additive sequence's unwrapped angular phase in fixed-point radians.</summary>
    public static FixedQ4816 FixedAngle(WorldSequence sequence, int index) {
        if (!string.Equals(
            a: sequence.Name,
            b: WorldSequence.Additive,
            comparisonType: StringComparison.Ordinal
        )) {
            throw new InvalidOperationException(message: $"Sequence '{sequence.Name}' does not produce an angular phase.");
        }

        return (FixedQ4816.FromInteger(value: checked((long)AdjustedIndex(
            index: index,
            sequence: sequence
        ))) *
            FixedQ4816.FromDouble(value: (sequence.Step * TwoPi)));
    }
    /// <summary>Returns an R2 sample as deterministic fixed point.</summary>
    public static (FixedQ4816 X, FixedQ4816 Y) FixedPair(WorldSequence sequence, int index) {
        if (!string.Equals(
            a: sequence.Name,
            b: WorldSequence.R2,
            comparisonType: StringComparison.Ordinal
        )) {
            throw new InvalidOperationException(message: $"Sequence '{sequence.Name}' does not produce a paired sample.");
        }

        var (x, y) = LowDiscrepancy.R2(index: AdjustedIndex(
            index: index,
            sequence: sequence
        ));

        return (
            UnitInterval32.FromUnitFraction32(value: x).ToFixedQ4816(),
            UnitInterval32.FromUnitFraction32(value: y).ToFixedQ4816()
        );
    }
    /// <summary>Returns an additive or R1 scalar as deterministic fixed point.</summary>
    public static FixedQ4816 FixedScalar(WorldSequence sequence, int index) {
        var adjusted = AdjustedIndex(
            index: index,
            sequence: sequence
        );

        if (string.Equals(
            a: sequence.Name,
            b: WorldSequence.Additive,
            comparisonType: StringComparison.Ordinal
        )) {
            return FixedQ4816.Fractional(value: (FixedQ4816.FromInteger(value: checked((long)adjusted)) * FixedQ4816.FromDouble(value: sequence.Step)));
        }

        if (string.Equals(
            a: sequence.Name,
            b: WorldSequence.R1,
            comparisonType: StringComparison.Ordinal
        )) {
            return UnitInterval32.FromUnitFraction32(value: LowDiscrepancy.R1(index: adjusted)).ToFixedQ4816();
        }

        throw new InvalidOperationException(message: $"Sequence '{sequence.Name}' does not produce a scalar sample.");
    }
    /// <summary>Returns an additive or R1 scalar in <c>[0, 1)</c>.</summary>
    public static float Scalar(WorldSequence sequence, int index) {
        var adjusted = AdjustedIndex(
            index: index,
            sequence: sequence
        );

        if (string.Equals(
            a: sequence.Name,
            b: WorldSequence.Additive,
            comparisonType: StringComparison.Ordinal
        )) {
            var value = (((float)adjusted) * sequence.Step);

            return (value - MathF.Floor(x: value));
        }

        if (string.Equals(
            a: sequence.Name,
            b: WorldSequence.R1,
            comparisonType: StringComparison.Ordinal
        )) {
            return ((float)(LowDiscrepancy.R1(index: adjusted).Value / 4294967296.0));
        }

        throw new InvalidOperationException(message: $"Sequence '{sequence.Name}' does not produce a scalar sample.");
    }
}
