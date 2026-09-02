namespace Puck.Maths;

/// <summary>
/// A deterministic, perfectly looping rotation driven by a tick: four rotation planes that each return to the identity
/// together after exactly <see cref="Period"/> ticks, with no accumulated drift and bit-identical results on every
/// machine and GPU backend. Each plane advances a whole number of 12° steps per tick — the plane speeds are {1, 7, 11,
/// 13} — so the whole system realigns every thirty ticks. Use it for a looping signed-distance spin, a light-phase
/// cycle, a colour wheel, or any fixed-period animation that must be deterministic.
/// </summary>
/// <remarks>
/// This is the Coxeter element of the exceptional Lie algebra E₈ acting on its Coxeter plane (see
/// <see cref="SymmetryLattice"/>), which is why the period is thirty and the plane speeds are the reduced residue
/// system of 30. Each rotation is a <see cref="FixedComplex"/> read from a baked table of the thirty 30th roots of
/// unity, indexed by <c>tick</c> reduced modulo <see cref="Period"/> — never composed step by step — so the cycle
/// closes exactly with no rounding drift. The four planes are the conjugate-pair representatives of the eight speeds
/// (each step count <c>m</c> and its partner <c>30 − m</c> share a plane with opposite orientation), so four planes
/// carry the full eight-dimensional rotation.
/// </remarks>
public static class CyclicRotation {
    // round(2π · 2^16): the full turn in Q48.16 radians, the domain of the baked table.
    private const long TurnRawQ16 = 411775L;

    /// <summary>The number of ticks after which every plane simultaneously returns to the identity rotation.</summary>
    public const int Period = 30;
    /// <summary>The number of independent rotation planes.</summary>
    public const int PlaneCount = 4;

    // The number of 12° steps each plane advances per tick — the E₈ exponents that are at most fifteen. A field, not a
    // collection-expression property, so reading it never re-materializes the array (the hot path allocates nothing).
    private static readonly int[] PlaneSpeeds = [1, 7, 11, 13];
    // The thirty 30th roots of unity as unit rotations, baked once. Index k is the rotation of k steps (12k°).
    private static readonly FixedComplex[] Rotors = BuildRotors();

    /// <summary>Bakes the thirty 30th roots of unity, one per step.</summary>
    /// <returns>The rotation table consumed by <see cref="At(int, long)"/>, index <c>k</c> holding <c>exp(2πi·k/30)</c>.</returns>
    private static FixedComplex[] BuildRotors() {
        var rotors = new FixedComplex[Period];

        for (var step = 0; (step < Period); ++step) {
            rotors[step] = FixedComplex.FromAngle(angle: FixedQ4816.FromRawBits(value: ((TurnRawQ16 * step) / Period)));
        }

        return rotors;
    }

    /// <summary>Returns the unit rotation a plane has reached at a tick.</summary>
    /// <param name="plane">The rotation plane, in <c>[0, <see cref="PlaneCount"/>)</c>.</param>
    /// <param name="tick">The tick; any value, positive or negative, reduces modulo <see cref="Period"/>.</param>
    /// <returns>The baked unit <see cref="FixedComplex"/> for the plane's current step; the identity at every multiple of <see cref="Period"/>.</returns>
    public static FixedComplex At(int plane, long tick) =>
        Rotor(step: Step(
            plane: plane,
            tick: tick
        ));
    /// <summary>Returns the unit rotation of a whole number of 12° steps, the table entry <see cref="At(int, long)"/>
    /// selects once a plane's step count is known.</summary>
    /// <param name="step">The step count; any value, positive or negative, reduces modulo <see cref="Period"/>.</param>
    /// <returns>The baked unit <see cref="FixedComplex"/> <c>exp(2πi·step/30)</c>; the identity at every multiple of <see cref="Period"/>.</returns>
    public static FixedComplex Rotor(long step) =>
        Rotors[(int)step.FloorModulo(modulus: ((long)Period))];
    /// <summary>Returns the unit rotation of a whole number of steps around a loop of any order: <c>exp(2πi·step/order)</c>,
    /// formed exactly as the baked table forms its thirty entries, so an order that divides <see cref="Period"/> reads
    /// the same bits <see cref="Rotor(long)"/> holds at the matching step.</summary>
    /// <param name="step">The step count; any value, positive or negative, reduces modulo <paramref name="order"/>.</param>
    /// <param name="order">The number of steps in one loop, at least one.</param>
    /// <returns>The unit <see cref="FixedComplex"/> for that step; the identity at every multiple of <paramref name="order"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="order"/> is less than one.</exception>
    public static FixedComplex Rotor(long step, int order) {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            value: order,
            other: 1,
            paramName: nameof(order)
        );

        var index = step.FloorModulo(modulus: ((long)order));

        return ((order == Period)
            ? Rotors[(int)index]
            : FixedComplex.FromAngle(angle: FixedQ4816.FromRawBits(value: ((TurnRawQ16 * index) / order))));
    }
    /// <summary>Rotates a vector by a plane's rotation at a tick.</summary>
    /// <param name="plane">The rotation plane, in <c>[0, <see cref="PlaneCount"/>)</c>.</param>
    /// <param name="tick">The tick; any value, positive or negative, reduces modulo <see cref="Period"/>.</param>
    /// <param name="vector">The vector to rotate.</param>
    /// <returns>The vector turned by the plane's current rotation; unchanged (bit-identical) at every multiple of <see cref="Period"/>.</returns>
    public static FixedVector2 Rotate(int plane, long tick, FixedVector2 vector) =>
        At(
            plane: plane,
            tick: tick
        ).Rotate(vector: vector);
    /// <summary>Returns how many 12° steps a plane has advanced at a tick.</summary>
    /// <param name="plane">The rotation plane, in <c>[0, <see cref="PlaneCount"/>)</c>.</param>
    /// <param name="tick">The tick; any value, positive or negative, reduces modulo <see cref="Period"/>.</param>
    /// <returns>The step count in <c>[0, <see cref="Period"/>)</c>, exact integer arithmetic with no table lookup.</returns>
    public static int Step(int plane, long tick) {
        var phase = ((int)tick.FloorModulo(modulus: ((long)Period)));

        return ((PlaneSpeeds[plane] * phase) % Period);
    }
}
