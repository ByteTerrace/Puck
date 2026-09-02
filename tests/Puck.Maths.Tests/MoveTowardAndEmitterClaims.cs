using System.Globalization;

namespace Puck.Maths.Tests;

/// <summary>
/// Claims for members the ratchet found classified nowhere: <see cref="FixedVector3.MoveToward"/>, whose statement
/// is a set of exact boundary identities plus bounded interpolation; its scalar twin <see cref="FixedQ4816.MoveToward"/>,
/// whose interpolation identities are EXACT rather than tolerance-bounded because a scalar step has no divide or
/// normalize in its path; and the two <see cref="FixedQ4816RustPort"/> emitters, whose statement is purity and live
/// transcription rather than the ported algorithm's agreement with its host. <see cref="LawRegistry"/> invokes each
/// claim below as a Default-tier law.
/// </summary>
internal static class MoveTowardAndEmitterClaims {
    /// <summary>The endpoints the move sweep runs between — axis-aligned, diagonal, negative and mixed, plus a pair
    /// whose separation is a single raw unit.</summary>
    private static readonly (long CurrentX, long CurrentY, long CurrentZ, long TargetX, long TargetY, long TargetZ)[] Segments = [
        (0, 0, 0, 10, 0, 0),
        (0, 0, 0, 0, 7, 0),
        (0, 0, 0, 0, 0, 3),
        (0, 0, 0, 3, 4, 0),
        (1, 2, 3, 9, -6, 15),
        (-5, -5, -5, 5, 5, 5),
        (100, -40, 7, -100, 40, -7),
        (2, 2, 2, 3, 2, 2),
    ];
    /// <summary>The fractions of each segment's own length the sweep steps by.</summary>
    private static readonly (int Numerator, int Denominator)[] StepFractions = [(1, 8), (1, 4), (1, 3), (1, 2), (3, 4), (7, 8)];

    /// <summary>Builds a vector from three integer components.</summary>
    /// <param name="x">The first component.</param>
    /// <param name="y">The second component.</param>
    /// <param name="z">The third component.</param>
    /// <returns>The vector.</returns>
    private static FixedVector3 Vector(long x, long y, long z) =>
        new(X: FixedQ4816.FromInteger(value: x), Y: FixedQ4816.FromInteger(value: y), Z: FixedQ4816.FromInteger(value: z));
    /// <summary>Builds a vector from three raw components, bypassing <see cref="FixedQ4816.FromInteger"/>'s own
    /// scale — the carrier-extreme endpoints below are raws, not integers <see cref="FixedQ4816.FromInteger"/> could
    /// represent without itself overflowing.</summary>
    /// <param name="x">The first component's raw.</param>
    /// <param name="y">The second component's raw.</param>
    /// <param name="z">The third component's raw.</param>
    /// <returns>The vector.</returns>
    private static FixedVector3 VectorRaw(long x, long y, long z) =>
        new(X: FixedQ4816.FromRawBits(value: x), Y: FixedQ4816.FromRawBits(value: y), Z: FixedQ4816.FromRawBits(value: z));

    /// <summary>The current/target raw pairs the carrier-extreme sweep runs between: each axis alone at the opposing
    /// carrier extremes with the other two held at zero, and all three at the opposing extremes together.</summary>
    private static readonly ((long X, long Y, long Z) Current, (long X, long Y, long Z) Target)[] CarrierExtremeVectorSegments = [
        ((long.MinValue, 0, 0), (long.MaxValue, 0, 0)),
        ((long.MaxValue, 0, 0), (long.MinValue, 0, 0)),
        ((0, long.MinValue, 0), (0, long.MaxValue, 0)),
        ((0, long.MaxValue, 0), (0, long.MinValue, 0)),
        ((0, 0, long.MinValue), (0, 0, long.MaxValue)),
        ((0, 0, long.MaxValue), (0, 0, long.MinValue)),
        ((long.MinValue, long.MinValue, long.MinValue), (long.MaxValue, long.MaxValue, long.MaxValue)),
        ((long.MaxValue, long.MaxValue, long.MaxValue), (long.MinValue, long.MinValue, long.MinValue)),
    ];
    /// <summary>The scalar current/target raw pairs the carrier-extreme sweep runs between: both orderings of the
    /// opposing carrier extremes.</summary>
    private static readonly (long Current, long Target)[] CarrierExtremeScalarSegments = [
        (long.MinValue, long.MaxValue),
        (long.MaxValue, long.MinValue),
    ];
    /// <summary>The step raws the carrier-extreme sweep tries at every segment: zero, a single raw unit (the exact
    /// failure scenario a wrapping <c>target − current</c> teleports on), a few small steps, and the largest
    /// representable step.</summary>
    private static readonly long[] CarrierExtremeSteps = [0L, 1L, 2L, 1000L, long.MaxValue];

    /// <summary>Proves <see cref="FixedVector3.MoveToward"/>'s boundary identities EXACTLY — a zero step is a no-op, a
    /// step at or past the remaining distance lands on the target itself, and a degenerate segment answers the target —
    /// and that an intermediate step stays on the segment, covers about the distance it was asked to cover, and never
    /// overshoots. The refusal is asserted by type AND parameter name.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? MoveTowardSurface() {
        // A negative step is refused, and the refusal names the method's own parameter rather than a property
        // expression on it.
        try {
            _ = FixedVector3.MoveToward(current: Vector(x: 0, y: 0, z: 0), target: Vector(x: 1, y: 0, z: 0), maxDelta: FixedQ4816.FromRawBits(value: -1));

            return "MoveToward accepted a negative maxDelta";
        } catch (ArgumentOutOfRangeException refusal) {
            if ("maxDelta" != refusal.ParamName) {
                return $"MoveToward's negative-step refusal names '{refusal.ParamName}' rather than 'maxDelta'";
            }
        }

        foreach (var (currentX, currentY, currentZ, targetX, targetY, targetZ) in Segments) {
            var current = Vector(x: currentX, y: currentY, z: currentZ);
            var target = Vector(x: targetX, y: targetY, z: targetZ);
            var separation = (target - current).Length;

            // A degenerate segment answers the target, which is also the current point, at every step size.
            if (current != FixedVector3.MoveToward(current: current, maxDelta: separation, target: current)) {
                return $"MoveToward from {current} to itself did not answer that point";
            }

            // A zero step never moves.
            var held = FixedVector3.MoveToward(current: current, target: target, maxDelta: FixedQ4816.Zero);

            if (held != current) {
                return $"a zero step from {current} toward {target} moved to {held}";
            }

            // A step at the separation, and any step past it, lands on the target EXACTLY rather than near it.
            foreach (var reach in new[] { separation, (separation + FixedQ4816.One), FixedQ4816.FromInteger(value: 1000), }) {
                if (reach < separation) { continue; }

                var landed = FixedVector3.MoveToward(current: current, maxDelta: reach, target: target);

                if (landed != target) {
                    return $"a step of {reach} from {current} toward {target} (separated by {separation}) landed on {landed} rather than the target";
                }
            }

            foreach (var (numerator, denominator) in StepFractions) {
                var step = ((separation * FixedQ4816.FromInteger(value: numerator)) / FixedQ4816.FromInteger(value: denominator));

                if ((step <= FixedQ4816.Zero) || (step >= separation)) { continue; }

                var moved = FixedVector3.MoveToward(current: current, maxDelta: step, target: target);
                var travelled = (moved - current).Length;
                var remaining = (target - moved).Length;

                // Never past the target: what is left of the segment cannot have grown.
                if (remaining > separation) {
                    return string.Create(
                        provider: CultureInfo.InvariantCulture,
                        handler: $"a step of {step} from {current} toward {target} left {remaining} to go, more than the {separation} it started with"
                    );
                }

                // The distance covered is the distance asked for, up to the resolution the fixed-point divide, scale and
                // norm can carry. The tolerance is stated rather than tuned: one part in 256 of the separation plus a
                // whole raw unit, which is far tighter than any transposition, sign flip or dropped normalization.
                var tolerance = ((separation / FixedQ4816.FromInteger(value: 256)) + FixedQ4816.FromRawBits(value: 64));
                var drift = ((travelled > step) ? (travelled - step) : (step - travelled));

                if (drift > tolerance) {
                    return string.Create(
                        provider: CultureInfo.InvariantCulture,
                        handler: $"a step of {step} from {current} toward {target} travelled {travelled}, off by {drift}, past the tolerance {tolerance}"
                    );
                }

                // On the segment: the travelled vector is parallel to the whole one, so their cross product vanishes up
                // to the same resolution.
                var cross = FixedVector3.Cross(left: (moved - current), right: (target - current));
                var strayed = cross.Length;
                var crossTolerance = ((separation / FixedQ4816.FromInteger(value: 64)) + FixedQ4816.FromRawBits(value: 256));

                if (strayed > crossTolerance) {
                    return string.Create(
                        provider: CultureInfo.InvariantCulture,
                        handler: $"a step of {step} from {current} toward {target} left the segment; the cross product's length is {strayed}, past the tolerance {crossTolerance}"
                    );
                }
            }
        }

        return null;
    }

    /// <summary>The scalar segments the scalar move sweep runs between.</summary>
    private static readonly (long Current, long Target)[] ScalarSegments = [
        (0, 10), (0, -10), (-5, 5), (100, -100), (2, 3), (-2, -3), (7, 7),
    ];

    /// <summary>Proves <see cref="FixedQ4816.MoveToward"/>'s boundary identities EXACTLY — the same shape as
    /// <see cref="MoveTowardSurface"/>, but with EXACT rather than tolerance-bounded intermediate identities: a
    /// scalar step is a plain add/subtract with no divide or normalize in its path, so <c>Abs(moved − current)</c>
    /// must equal the step and <c>Abs(target − moved) + step</c> must equal the separation to the bit. The direction
    /// leg cross-checks against <see cref="FixedQ4816.Sign"/> — a different code path than the implementation's own
    /// comparison — so a transposed operand or a negated step reddens it independently of the distance identities.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? ScalarMoveTowardSurface() {
        try {
            _ = FixedQ4816.MoveToward(current: FixedQ4816.Zero, target: FixedQ4816.One, maxDelta: FixedQ4816.FromRawBits(value: -1));

            return "MoveToward accepted a negative maxDelta";
        } catch (ArgumentOutOfRangeException refusal) {
            if ("maxDelta" != refusal.ParamName) {
                return $"MoveToward's negative-step refusal names '{refusal.ParamName}' rather than 'maxDelta'";
            }
        }

        foreach (var (currentRaw, targetRaw) in ScalarSegments) {
            var current = FixedQ4816.FromInteger(value: currentRaw);
            var target = FixedQ4816.FromInteger(value: targetRaw);
            var separation = FixedQ4816.Abs(value: (target - current));

            if (current != FixedQ4816.MoveToward(current: current, maxDelta: separation, target: current)) {
                return $"MoveToward from {current} to itself did not answer that point";
            }

            var held = FixedQ4816.MoveToward(current: current, target: target, maxDelta: FixedQ4816.Zero);

            if (held != current) {
                return $"a zero step from {current} toward {target} moved to {held}";
            }

            foreach (var reach in new[] { separation, (separation + FixedQ4816.One), FixedQ4816.FromInteger(value: 1000) }) {
                if (reach < separation) { continue; }

                var landed = FixedQ4816.MoveToward(current: current, maxDelta: reach, target: target);

                if (landed != target) {
                    return $"a step of {reach} from {current} toward {target} (separated by {separation}) landed on {landed} rather than the target";
                }
            }

            foreach (var (numerator, denominator) in StepFractions) {
                var step = ((separation * FixedQ4816.FromInteger(value: numerator)) / FixedQ4816.FromInteger(value: denominator));

                if ((step <= FixedQ4816.Zero) || (step >= separation)) { continue; }

                var moved = FixedQ4816.MoveToward(current: current, maxDelta: step, target: target);
                var travelled = FixedQ4816.Abs(value: (moved - current));

                if (travelled != step) {
                    return $"a step of {step} from {current} toward {target} travelled {travelled} rather than the step exactly";
                }

                var remaining = FixedQ4816.Abs(value: (target - moved));

                if ((remaining + step) != separation) {
                    return $"a step of {step} from {current} toward {target} left {remaining} to go; remaining+step is {(remaining + step)} rather than the separation {separation}";
                }

                if (target != current) {
                    var expectedDirection = FixedQ4816.Sign(value: (target - current));
                    var actualDirection = FixedQ4816.Sign(value: (moved - current));

                    if (actualDirection != expectedDirection) {
                        return $"a step of {step} from {current} toward {target} moved in the wrong direction (sign {actualDirection} vs {expectedDirection})";
                    }
                }
            }
        }

        return null;
    }
    /// <summary>Proves <see cref="FixedVector3.MoveToward"/> does not teleport across the carrier at the opposing raw
    /// extremes, where a componentwise <c>target − current</c> wraps: at every carrier-extreme segment and step, the
    /// landing decision matches an independent <c>BigInteger</c> displacement oracle exactly, a non-landing step
    /// never lands on the target, moves no axis against the oracle's own exact sign, and never travels past
    /// <c>maxDelta</c> by more than the fixed-point divide/normalize this path runs through can legitimately
    /// round.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? MoveTowardCarrierExtremesSurface() {
        foreach (var (currentRaw, targetRaw) in CarrierExtremeVectorSegments) {
            var current = VectorRaw(x: currentRaw.X, y: currentRaw.Y, z: currentRaw.Z);
            var target = VectorRaw(x: targetRaw.X, y: targetRaw.Y, z: targetRaw.Z);

            foreach (var maxDeltaRaw in CarrierExtremeSteps) {
                var maxDelta = FixedQ4816.FromRawBits(value: maxDeltaRaw);

                var (landing, signX, signY, signZ) = Oracles.MoveTowardVerdict(
                    currentX: currentRaw.X, currentY: currentRaw.Y, currentZ: currentRaw.Z,
                    maxDeltaRaw: maxDeltaRaw, targetX: targetRaw.X, targetY: targetRaw.Y,
                    targetZ: targetRaw.Z
                );
                var actual = FixedVector3.MoveToward(current: current, maxDelta: maxDelta, target: target);

                if (landing) {
                    if (actual != target) {
                        return $"MoveToward({current}, {target}, maxDelta raw {maxDeltaRaw}) should have landed on the target per the independent oracle, but returned {actual}";
                    }

                    continue;
                }

                if (actual == target) {
                    return $"MoveToward({current}, {target}, maxDelta raw {maxDeltaRaw}) landed on the target, but the independent oracle says the true separation exceeds maxDelta";
                }

                if (
                    !AxisMovedTowardOracleSign(before: currentRaw.X, after: actual.X.Value, expectedSign: signX) ||
                    !AxisMovedTowardOracleSign(before: currentRaw.Y, after: actual.Y.Value, expectedSign: signY) ||
                    !AxisMovedTowardOracleSign(before: currentRaw.Z, after: actual.Z.Value, expectedSign: signZ)
                ) {
                    return $"MoveToward({current}, {target}, maxDelta raw {maxDeltaRaw}) moved to {actual}, against the oracle's own exact displacement sign on at least one axis";
                }

                // Never overshoot: the tolerance mirrors the segment law's own — the fixed-point divide, scale and
                // norm this non-landing branch runs through round, but by no more than a raw handful. Subtracting the
                // tolerance from travelled, rather than adding it to maxDelta, avoids wrapping when maxDelta is
                // itself already at the carrier's own maximum.
                var travelled = (actual - current).Length;
                var tolerance = FixedQ4816.FromRawBits(value: 4);

                if ((travelled - tolerance) > maxDelta) {
                    return $"MoveToward({current}, {target}, maxDelta raw {maxDeltaRaw}) travelled {travelled}, past maxDelta {maxDelta} by more than the tolerance {tolerance}";
                }
            }
        }

        return null;
    }

    /// <summary>Whether a moved axis is consistent with the independent oracle's exact displacement sign: either the
    /// axis did not move at all (legitimate when the fixed-point direction rounds a tiny per-axis share to zero), or
    /// it moved in exactly the sign the oracle reports.</summary>
    /// <param name="before">The raw before the move.</param>
    /// <param name="after">The raw after the move.</param>
    /// <param name="expectedSign">The oracle's exact displacement sign for this axis.</param>
    /// <returns><see langword="true"/> when the axis's movement is consistent with the oracle.</returns>
    private static bool AxisMovedTowardOracleSign(long before, long after, int expectedSign) {
        if (0 == expectedSign) { return (after == before); }

        var actualSign = ((after > before) ? 1 : ((after < before) ? -1 : 0));

        return ((0 == actualSign) || (actualSign == expectedSign));
    }

    /// <summary>Proves <see cref="FixedQ4816.MoveToward"/> does not teleport across the carrier at the opposing raw
    /// extremes — the exact scalar counterpart of <see cref="MoveTowardCarrierExtremesSurface"/>: every result matches
    /// an independent <c>BigInteger</c> displacement oracle EXACTLY, to the raw.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? ScalarMoveTowardCarrierExtremesSurface() {
        foreach (var (currentRaw, targetRaw) in CarrierExtremeScalarSegments) {
            var current = FixedQ4816.FromRawBits(value: currentRaw);
            var target = FixedQ4816.FromRawBits(value: targetRaw);

            foreach (var maxDeltaRaw in CarrierExtremeSteps) {
                var maxDelta = FixedQ4816.FromRawBits(value: maxDeltaRaw);
                var expectedRaw = Oracles.MoveTowardRaw(currentRaw: currentRaw, maxDeltaRaw: maxDeltaRaw, targetRaw: targetRaw);
                var actual = FixedQ4816.MoveToward(current: current, maxDelta: maxDelta, target: target);

                if (actual.Value != expectedRaw) {
                    return $"MoveToward({current}, {target}, maxDelta raw {maxDeltaRaw}) returned raw {actual.Value}, expected {expectedRaw} from the independent BigInteger displacement oracle";
                }
            }
        }

        return null;
    }
    /// <summary>Proves both <see cref="FixedQ4816RustPort"/> emitters are PURE — two calls produce byte-identical text,
    /// and neither disturbs the other — and that the numeric constants they claim to read from the live
    /// <see cref="FixedQ4816"/> type are the values that type currently holds, rather than literals transcribed once and
    /// left to drift.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? RustPortEmitterSurface() {
        var generated = FixedQ4816RustPort.EmitGenerated();
        var vectors = FixedQ4816RustPort.EmitVectors();

        if (0 == generated.Length) { return "EmitGenerated produced no text"; }

        if (0 == vectors.Length) { return "EmitVectors produced no text"; }

        // Purity, in both orders: an emitter that consumed a shared generator, read a clock or drew from an unseeded
        // source would differ between two calls, or would be disturbed by its sibling running in between.
        if (!string.Equals(a: generated, b: FixedQ4816RustPort.EmitGenerated(), comparisonType: StringComparison.Ordinal)) {
            return "two calls to EmitGenerated produced different text";
        }

        if (!string.Equals(a: vectors, b: FixedQ4816RustPort.EmitVectors(), comparisonType: StringComparison.Ordinal)) {
            return "two calls to EmitVectors produced different text";
        }

        _ = FixedQ4816RustPort.EmitVectors();

        if (!string.Equals(a: generated, b: FixedQ4816RustPort.EmitGenerated(), comparisonType: StringComparison.Ordinal)) {
            return "EmitGenerated's text changed after EmitVectors ran, so the two share mutable state";
        }

        // Live transcription: each constant is looked up on the type and searched for in the emitted text as the
        // invariant decimal the emitter writes. A renamed or mis-referenced source constant stops appearing.
        (string Name, long Value)[] constants = [
            (nameof(FixedQ4816.Atan2HalfPiQ61), FixedQ4816.Atan2HalfPiQ61),
            (nameof(FixedQ4816.PiQ61), FixedQ4816.PiQ61),
        ];

        foreach (var (name, value) in constants) {
            var text = value.ToString(provider: CultureInfo.InvariantCulture);

            if (!generated.Contains(comparisonType: StringComparison.Ordinal, value: text)) {
                return $"the emitted Rust does not carry {name}'s live value {text}";
            }
        }

        // The emitted vectors are computed by calling the live type at generation time, so the digest of a known input
        // must appear there too. A vectors file that emitted its inputs but not the host's answers would pass every
        // check above and none of this one.
        var probe = FixedQ4816.FromInteger(value: 1);
        var probeSin = FixedQ4816.Sin(angle: probe).Value.ToString(provider: CultureInfo.InvariantCulture);

        return (vectors.Contains(comparisonType: StringComparison.Ordinal, value: probeSin)
            ? null
            : $"the emitted vectors do not carry the live Sin(1) raw value {probeSin}");
    }
}
