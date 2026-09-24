namespace Puck.Maths.Tests;

/// <summary>The shipped Cayley-Dickson doubling tower as a witness: unit basis elements at every floor it ships and the
/// lane readout of each floor, in the basis order the presented Cayley-Dickson algebras use (lane <c>k</c> is the unit
/// whose index is <c>k</c>). Nothing here multiplies: a claim multiplies the units out through
/// <see cref="DoublingAlgebra{TInner}"/> itself, which shares no code with the presented product it is compared
/// against.</summary>
internal static class DoublingTower {
    /// <summary>Builds the scalar lane <paramref name="offset"/> of the unit with basis index <paramref name="index"/>.</summary>
    /// <param name="index">The unit's basis index.</param>
    /// <param name="offset">The basis index this lane holds.</param>
    /// <returns>One when the lane is the unit's, zero otherwise.</returns>
    public static FixedScalarRing UnitScalarAt(int index, int offset = 0) =>
        new(Value: ((offset == index)
            ? FixedQ4816.One
            : FixedQ4816.Zero));
    /// <summary>Builds the complex floor of the unit with basis index <paramref name="index"/>, spanning basis indices
    /// <paramref name="offset"/> and one past it.</summary>
    /// <param name="index">The unit's basis index.</param>
    /// <param name="offset">The first basis index this floor holds.</param>
    /// <returns>The floor.</returns>
    public static DoublingAlgebra<FixedScalarRing> UnitComplexAt(int index, int offset = 0) =>
        new(
            Left: UnitScalarAt(
                index: index,
                offset: offset
            ),
            Right: UnitScalarAt(
                index: index,
                offset: (offset + 1)
            )
        );
    /// <summary>Builds the quaternion floor of the unit with basis index <paramref name="index"/>, spanning four basis
    /// indices from <paramref name="offset"/>.</summary>
    /// <param name="index">The unit's basis index.</param>
    /// <param name="offset">The first basis index this floor holds.</param>
    /// <returns>The floor.</returns>
    public static DoublingAlgebra<DoublingAlgebra<FixedScalarRing>> UnitQuaternionAt(int index, int offset = 0) =>
        new(
            Left: UnitComplexAt(
                index: index,
                offset: offset
            ),
            Right: UnitComplexAt(
                index: index,
                offset: (offset + 2)
            )
        );
    /// <summary>Builds the octonion floor of the unit with basis index <paramref name="index"/>, spanning eight basis
    /// indices from <paramref name="offset"/>.</summary>
    /// <param name="index">The unit's basis index.</param>
    /// <param name="offset">The first basis index this floor holds.</param>
    /// <returns>The floor.</returns>
    public static DoublingAlgebra<DoublingAlgebra<DoublingAlgebra<FixedScalarRing>>> UnitOctonionAt(int index, int offset = 0) =>
        new(
            Left: UnitQuaternionAt(
                index: index,
                offset: offset
            ),
            Right: UnitQuaternionAt(
                index: index,
                offset: (offset + 4)
            )
        );
    /// <summary>Builds the sedenion floor of the unit with basis index <paramref name="index"/>, spanning sixteen basis
    /// indices from <paramref name="offset"/>.</summary>
    /// <param name="index">The unit's basis index.</param>
    /// <param name="offset">The first basis index this floor holds.</param>
    /// <returns>The floor.</returns>
    public static DoublingAlgebra<DoublingAlgebra<DoublingAlgebra<DoublingAlgebra<FixedScalarRing>>>> UnitSedenionAt(int index, int offset = 0) =>
        new(
            Left: UnitOctonionAt(
                index: index,
                offset: offset
            ),
            Right: UnitOctonionAt(
                index: index,
                offset: (offset + 8)
            )
        );
    /// <summary>Reads a complex floor's two raw lanes into <paramref name="lanes"/> from <paramref name="offset"/>.</summary>
    /// <param name="value">The floor.</param>
    /// <param name="lanes">The destination lanes.</param>
    /// <param name="offset">The first lane written.</param>
    public static void WriteComplexLanes(DoublingAlgebra<FixedScalarRing> value, Span<long> lanes, int offset = 0) {
        lanes[offset] = value.Left.Value.Value;
        lanes[(offset + 1)] = value.Right.Value.Value;
    }
    /// <summary>Reads a quaternion floor's four raw lanes into <paramref name="lanes"/> from
    /// <paramref name="offset"/>.</summary>
    /// <param name="value">The floor.</param>
    /// <param name="lanes">The destination lanes.</param>
    /// <param name="offset">The first lane written.</param>
    public static void WriteQuaternionLanes(DoublingAlgebra<DoublingAlgebra<FixedScalarRing>> value, Span<long> lanes, int offset = 0) {
        WriteComplexLanes(
            value: value.Left,
            lanes: lanes,
            offset: offset
        );
        WriteComplexLanes(
            value: value.Right,
            lanes: lanes,
            offset: (offset + 2)
        );
    }
    /// <summary>Reads an octonion floor's eight raw lanes into <paramref name="lanes"/> from
    /// <paramref name="offset"/>.</summary>
    /// <param name="value">The floor.</param>
    /// <param name="lanes">The destination lanes.</param>
    /// <param name="offset">The first lane written.</param>
    public static void WriteOctonionLanes(DoublingAlgebra<DoublingAlgebra<DoublingAlgebra<FixedScalarRing>>> value, Span<long> lanes, int offset = 0) {
        WriteQuaternionLanes(
            value: value.Left,
            lanes: lanes,
            offset: offset
        );
        WriteQuaternionLanes(
            value: value.Right,
            lanes: lanes,
            offset: (offset + 4)
        );
    }
    /// <summary>Reads a sedenion floor's sixteen raw lanes into <paramref name="lanes"/> from
    /// <paramref name="offset"/>.</summary>
    /// <param name="value">The floor.</param>
    /// <param name="lanes">The destination lanes.</param>
    /// <param name="offset">The first lane written.</param>
    public static void WriteSedenionLanes(DoublingAlgebra<DoublingAlgebra<DoublingAlgebra<DoublingAlgebra<FixedScalarRing>>>> value, Span<long> lanes, int offset = 0) {
        WriteOctonionLanes(
            value: value.Left,
            lanes: lanes,
            offset: offset
        );
        WriteOctonionLanes(
            value: value.Right,
            lanes: lanes,
            offset: (offset + 8)
        );
    }
}
