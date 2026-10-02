namespace Puck.Maths.Tests;

/// <summary>Subject closures binding <see cref="FixedQuaternion"/>'s interpolation and arc constructions to the
/// law suite.</summary>
internal static partial class Subjects {
    // The committed per-lane envelope for Slerp against the ideal arc, in raw Q16 units.
    private const long SlerpLaneEnvelopeRaw = 3L;

    // Four-dimensional arc angles θ in raw Q16 radians: either side of the nlerp seam (dot 65503/65536 is
    // θ ≈ 0.03174, raw ≈ 2080), the brief's 0.01, 0.03, 0.04 and 0.1, wider arcs, the half turn's neighbourhood
    // (θ just under π/2, a rotation of nearly π), and one past it, which Slerp folds by negating the target.
    private static readonly long[] SlerpArcAngles = [655L, 1966L, 2075L, 2086L, 2621L, 6554L, 19661L, 65536L, 102878L, 131072L];

    // The committed lateral envelope for FromTo(a, b).Rotate(â) against b's direction, in raw Q16 units.
    private const long FromToLateralEnvelopeRaw = 6L;

    /// <summary>FromTo carries its start onto the target's direction throughout the approach to antiparallel: the
    /// rotated unit start lies within <see cref="FromToLateralEnvelopeRaw"/> raw of the target's direction, at gaps
    /// from about 45° down to 2⁻⁴⁰ rad and at exact antiparallel.</summary>
    /// <param name="left">Three raws drawn for the start a, folded below 2⁴⁰.</param>
    /// <param name="right">The gap selector s in [0, 40].</param>
    /// <returns>The counterexample, or <see langword="null"/>.</returns>
    public static string? FixedQuaternionFromToNearAntiparallel(long[] left, long[] right) {
        var ax = (left[0] >> 23);
        var ay = (left[1] >> 23);
        var az = (left[2] >> 23);

        if ((ax | ay | az) == 0L) {
            return null;
        }

        // p = a × ê for the axis ê least aligned with a: exactly perpendicular to a, and as long as a's other two
        // lanes; then b = −a + p/2^s has a gap from antiparallel of about 2^−s, exactly as integers.
        long px, py, pz;
        var mx = Math.Abs(value: ax);
        var my = Math.Abs(value: ay);
        var mz = Math.Abs(value: az);

        if ((mx <= my) && (mx <= mz)) {
            (px, py, pz) = (0L, az, -ay);
        } else if (my <= mz) {
            (px, py, pz) = (-az, 0L, ax);
        } else {
            (px, py, pz) = (ay, -ax, 0L);
        }

        var shift = ((int)(unchecked((ulong)right[0]) % 41UL));
        var bx = (-ax + (px >> shift));
        var by = (-ay + (py >> shift));
        var bz = (-az + (pz >> shift));
        var from = new FixedVector3(
            X: Raw(value: ax),
            Y: Raw(value: ay),
            Z: Raw(value: az)
        );
        var to = new FixedVector3(
            X: Raw(value: bx),
            Y: Raw(value: by),
            Z: Raw(value: bz)
        );
        var rotation = FixedQuaternion.FromTo(
            from: from,
            to: to
        );
        var rotated = rotation.Rotate(vector: from.Normalize());

        // Exact alignment: |r × b|·2¹⁶ ≤ k·|r||b| (squared) and r·b > 0, so r lies within k raw of b's direction.
        System.Numerics.BigInteger rx = rotated.X.Value, ry = rotated.Y.Value, rz = rotated.Z.Value;
        System.Numerics.BigInteger tx = bx, ty = by, tz = bz;
        var cx = ((ry * tz) - (rz * ty));
        var cy = ((rz * tx) - (rx * tz));
        var cz = ((rx * ty) - (ry * tx));
        var crossSquared = (((cx * cx) + (cy * cy)) + (cz * cz));
        var dot = (((rx * tx) + (ry * ty)) + (rz * tz));
        var normsSquared = ((((rx * rx) + (ry * ry)) + (rz * rz)) * (((tx * tx) + (ty * ty)) + (tz * tz)));

        if (
            (dot.Sign <= 0) ||
            ((crossSquared << 32) > ((FromToLateralEnvelopeRaw * FromToLateralEnvelopeRaw) * normsSquared))
        ) {
            return $"FromTo(({ax}, {ay}, {az}) → ({bx}, {by}, {bz}), gap ~2^-{shift}).Rotate(â) = ({rx}, {ry}, {rz}) is more than {FromToLateralEnvelopeRaw} raw off the target's direction";
        }

        // The SHORTEST arc is the rotation about a × b: wherever a × b is non-zero, the quaternion's vector part
        // must lie along it, within 4 raw — which tells the true half turn inside the gap from any other half turn
        // that also carries a onto −a.
        System.Numerics.BigInteger wax = ax, way = ay, waz = az;
        var ex = ((way * tz) - (waz * ty));
        var ey = ((waz * tx) - (wax * tz));
        var ez = ((wax * ty) - (way * tx));

        if (!(ex.IsZero && ey.IsZero && ez.IsZero)) {
            System.Numerics.BigInteger vx = rotation.X.Value, vy = rotation.Y.Value, vz = rotation.Z.Value;
            var ux = ((vy * ez) - (vz * ey));
            var uy = ((vz * ex) - (vx * ez));
            var uz = ((vx * ey) - (vy * ex));
            var axisCross = (((ux * ux) + (uy * uy)) + (uz * uz));
            var axisDot = (((vx * ex) + (vy * ey)) + (vz * ez));
            var axisNorms = ((((vx * vx) + (vy * vy)) + (vz * vz)) * (((ex * ex) + (ey * ey)) + (ez * ez)));

            if (
                (axisDot.Sign <= 0) ||
                ((axisCross << 32) > (16 * axisNorms))
            ) {
                return $"FromTo(({ax}, {ay}, {az}) → ({bx}, {by}, {bz}), gap ~2^-{shift}) has axis ({vx}, {vy}, {vz}), not along a × b = ({ex}, {ey}, {ez})";
            }
        }

        return null;
    }
    /// <summary>Slerp lands within <see cref="SlerpLaneEnvelopeRaw"/> raw per lane of the ideal point at angle
    /// <c>t·θ</c> on the great circle through its exact inputs.</summary>
    /// <param name="left">Four raws drawn for the start quaternion, which is normalized before use.</param>
    /// <param name="right">The parameter t (folded onto [0, 1]) and the arc-angle selector.</param>
    /// <returns>The counterexample, or <see langword="null"/>.</returns>
    public static string? FixedQuaternionSlerpFollowsTheArc(long[] left, long[] right) {
        var start = new FixedQuaternion(
            X: Raw(value: left[0]),
            Y: Raw(value: left[1]),
            Z: Raw(value: left[2]),
            W: Raw(value: left[3])
        );

        // A zero draw normalizes to Identity.
        var from = start.Normalize();

        // P = (−y, x, −w, z) is exactly orthogonal to (x, y, z, w) and of the same norm, so A·cos θ + P·sin θ is the
        // point at θ along a great circle; it is normalized, and the oracle reads its lanes exactly as given.
        var orthogonal = new FixedQuaternion(
            X: -from.Y,
            Y: from.X,
            Z: -from.W,
            W: from.Z
        );
        var angle = SlerpArcAngles[((int)(unchecked((ulong)right[1]) % ((ulong)SlerpArcAngles.Length)))];

        var (sin, cos) = FixedQ4816.SinCos(angle: Raw(value: angle));
        var to = new FixedQuaternion(
            X: ((from.X * cos) + (orthogonal.X * sin)),
            Y: ((from.Y * cos) + (orthogonal.Y * sin)),
            Z: ((from.Z * cos) + (orthogonal.Z * sin)),
            W: ((from.W * cos) + (orthogonal.W * sin))
        ).Normalize();
        var amount = ((long)(unchecked((ulong)right[0]) % (((ulong)FixedQ4816.One.Value) + 1UL)));
        var actual = FixedQuaternion.Slerp(
            amount: Raw(value: amount),
            from: from,
            to: to
        );

        if (Oracles.Slerp(
            amountRaw: amount,
            from: [from.X.Value, from.Y.Value, from.Z.Value, from.W.Value],
            to: [to.X.Value, to.Y.Value, to.Z.Value, to.W.Value]
        ) is not { } ideal) {
            return null;
        }

        long[] lanes = [actual.X.Value, actual.Y.Value, actual.Z.Value, actual.W.Value];

        for (var lane = 0; (lane < 4); lane++) {
            var deviation = System.Numerics.BigInteger.Abs(value: ((lanes[lane] * ideal.Denominator) - ideal.Numerators[lane]));

            if (deviation > (SlerpLaneEnvelopeRaw * ideal.Denominator)) {
                return $"Slerp(θ raw {angle}, t raw {amount}) lane {lane} = {lanes[lane]}, the arc's point is {(ideal.Numerators[lane] / ideal.Denominator)} (more than {SlerpLaneEnvelopeRaw} raw away)";
            }
        }

        return null;
    }
}
