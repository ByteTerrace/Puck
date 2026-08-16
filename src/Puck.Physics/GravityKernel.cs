using Puck.Maths;

namespace Puck.Physics;

internal readonly record struct PreparedGravityParameters(
    FixedQ4816 GravitationalConstant,
    FixedQ4816 SofteningSquared
);
internal readonly record struct PreparedGravityInteraction(
    FixedVector3 Delta,
    FixedQ4816 SoftenedDistanceSquared,
    FixedQ4816 SoftenedDistance
);
internal readonly record struct PreparedGravityDisplacement(
    FixedVector3 Delta,
    FixedQ4816 DistanceSquared
);
internal static class GravityKernel {
    private static FixedVector3 ScaleChecked(FixedVector3 vector, FixedQ4816 scalar) =>
        new(
            X: checked((vector.X * scalar)),
            Y: checked((vector.Y * scalar)),
            Z: checked((vector.Z * scalar))
        );

    public static FixedVector3 Acceleration(in PreparedGravityInteraction interaction, FixedQ4816 sourceMass, FixedQ4816 gravitationalConstant) {
        if (
            (sourceMass <= FixedQ4816.Zero) ||
            (gravitationalConstant <= FixedQ4816.Zero) ||
            (interaction.Delta == FixedVector3.Zero)
        ) {
            return FixedVector3.Zero;
        }

        // a = delta * Gm / (r^2 + epsilon^2)^(3/2). Divide in two stages instead of forming r^3 in Q16:
        // a small but representable softening length can have a representable square while its rounded cube is zero.
        var numerator = checked((gravitationalConstant * sourceMass));
        var inverseSquareStrength = checked((numerator / interaction.SoftenedDistanceSquared));
        var scale = checked((inverseSquareStrength / interaction.SoftenedDistance));

        return ScaleChecked(
            vector: interaction.Delta,
            scalar: scale
        );
    }
    public static void AccumulatePair(
        ReadOnlySpan<GravityBody> bodies,
        int firstIndex,
        int secondIndex,
        Span<FixedVector3> accelerations,
        in PreparedGravityParameters parameters,
        ref long exactSourceEvaluations
    ) {
        ref readonly var first = ref bodies[firstIndex];
        ref readonly var second = ref bodies[secondIndex];

        if (
            (first.Mass <= FixedQ4816.Zero) &&
            (second.Mass <= FixedQ4816.Zero)
        ) {
            return;
        }

        var interaction = PrepareInteraction(
            target: first.Position,
            source: second.Position,
            softeningSquared: parameters.SofteningSquared
        );

        if (second.Mass > FixedQ4816.Zero) {
            var acceleration = Acceleration(
                interaction: in interaction,
                sourceMass: second.Mass,
                gravitationalConstant: parameters.GravitationalConstant
            );

            accelerations[firstIndex] = AddChecked(
                left: accelerations[firstIndex],
                right: acceleration
            );
            exactSourceEvaluations++;
        }

        if (first.Mass > FixedQ4816.Zero) {
            var reverseInteraction = interaction with { Delta = Reverse(vector: interaction.Delta) };
            var acceleration = Acceleration(
                interaction: in reverseInteraction,
                sourceMass: first.Mass,
                gravitationalConstant: parameters.GravitationalConstant
            );

            accelerations[secondIndex] = AddChecked(
                left: accelerations[secondIndex],
                right: acceleration
            );
            exactSourceEvaluations++;
        }
    }
    public static FixedVector3 AddChecked(FixedVector3 left, FixedVector3 right) =>
        new(
            X: checked((left.X + right.X)),
            Y: checked((left.Y + right.Y)),
            Z: checked((left.Z + right.Z))
        );
    public static PreparedGravityDisplacement PrepareDisplacement(FixedVector3 target, FixedVector3 source) {
        var delta = new FixedVector3(
            X: checked((source.X - target.X)),
            Y: checked((source.Y - target.Y)),
            Z: checked((source.Z - target.Z))
        );

        if (!delta.TryLengthSquared(squaredLength: out var distanceSquared)) {
            throw new OverflowException(message: "A source-to-target squared distance exceeds Q48.16 range.");
        }

        return new PreparedGravityDisplacement(
            Delta: delta,
            DistanceSquared: distanceSquared
        );
    }
    public static PreparedGravityInteraction PrepareInteraction(FixedVector3 target, FixedVector3 source, FixedQ4816 softeningSquared) {
        var displacement = PrepareDisplacement(
            source: source,
            target: target
        );

        return PrepareInteraction(
            displacement: in displacement,
            softeningSquared: softeningSquared
        );
    }
    public static PreparedGravityInteraction PrepareInteraction(in PreparedGravityDisplacement displacement, FixedQ4816 softeningSquared) {
        var softenedDistanceSquared = checked((displacement.DistanceSquared + softeningSquared));
        var softenedDistance = FixedQ4816.Sqrt(value: softenedDistanceSquared);

        if (softenedDistance <= FixedQ4816.Zero) {
            throw new OverflowException(message: "A softened source-to-target distance collapsed to zero in Q48.16.");
        }

        return new PreparedGravityInteraction(
            Delta: displacement.Delta,
            SoftenedDistanceSquared: softenedDistanceSquared,
            SoftenedDistance: softenedDistance
        );
    }
    public static FixedVector3 Reverse(FixedVector3 vector) =>
        new(
            X: checked(-vector.X),
            Y: checked(-vector.Y),
            Z: checked(-vector.Z)
        );
    /// <summary>Rounds <c>numerator / positiveDenominator</c> to the nearest <see cref="long"/>, ties to even, the
    /// rounding itself owned by <see cref="FusedArithmetic.TryDivideMagnitudeRounded"/>.</summary>
    public static long RoundDivide(Int128 numerator, ulong positiveDenominator) {
        var negative = (numerator < Int128.Zero);
        var magnitude = (negative
            ? (unchecked((UInt128)(-(numerator + Int128.One))) + UInt128.One)
            : unchecked((UInt128)numerator)
        );

        if (!FusedArithmetic.TryDivideMagnitudeRounded(
            denominatorMagnitude: positiveDenominator,
            fractionBitCount: 0,
            numeratorMagnitude: magnitude,
            quotient: out var quotient
        )) {
            throw new DivideByZeroException();
        }

        if (negative) {
            var minimumMagnitude = (((UInt128)long.MaxValue) + UInt128.One);

            if (quotient > minimumMagnitude) {
                throw new OverflowException(message: "A rounded fixed-point intermediate is below Int64 range.");
            }

            return ((quotient == minimumMagnitude)
                ? long.MinValue
                : unchecked(-((long)quotient))
            );
        }

        if (quotient > long.MaxValue) {
            throw new OverflowException(message: "A rounded fixed-point intermediate is above Int64 range.");
        }

        return unchecked((long)quotient);
    }
    public static PreparedGravityParameters Validate(
        ReadOnlySpan<GravityBody> bodies,
        Span<FixedVector3> accelerations,
        GravityParameters parameters
    ) {
        if (accelerations.Length < bodies.Length) {
            throw new ArgumentException(
                message: "The acceleration destination is shorter than the body source.",
                paramName: nameof(accelerations)
            );
        }

        ArgumentOutOfRangeException.ThrowIfNegative(
            value: parameters.GravitationalConstant.Value,
            paramName: nameof(parameters)
        );
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            value: parameters.SofteningLength.Value,
            other: 0L,
            paramName: nameof(parameters)
        );

        FixedQ4816 softeningSquared;

        try {
            softeningSquared = checked((parameters.SofteningLength * parameters.SofteningLength));
        } catch (OverflowException) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(parameters),
                actualValue: parameters.SofteningLength,
                message: "The softening length's square exceeds Q48.16 range."
            );
        }

        if (softeningSquared <= FixedQ4816.Zero) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(parameters),
                actualValue: parameters.SofteningLength,
                message: "The softening length is too small for its square to remain non-zero in Q48.16."
            );
        }

        for (var index = 0; (index < bodies.Length); index++) {
            if (bodies[index].Mass < FixedQ4816.Zero) {
                throw new ArgumentOutOfRangeException(
                    paramName: nameof(bodies),
                    actualValue: bodies[index].Mass,
                    message: $"Body {index} has negative mass."
                );
            }
        }

        accelerations[..bodies.Length].Clear();

        return new PreparedGravityParameters(
            GravitationalConstant: parameters.GravitationalConstant,
            SofteningSquared: softeningSquared
        );
    }
}
