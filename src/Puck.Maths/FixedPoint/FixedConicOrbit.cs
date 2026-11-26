namespace Puck.Maths;

/// <summary>A closed elliptic orbit about a focus, evaluated in closed form in fixed point: the position at any phase
/// is computed directly, never by stepping from an earlier one.</summary>
/// <remarks>
/// <para>Phase is the mean anomaly as a binary fraction of one turn scaled by 2^64, so whole turns wrap for free and
/// <see cref="Phase(ulong, ulong)"/> derives it from an elapsed tick count and a period by one exact integer division: the same
/// tick gives the same phase however many ticks were evaluated before it.</para>
/// <para>Kepler's equation is solved by a fixed number of Newton steps in turns at 60 fraction bits
/// (<see cref="EccentricAnomaly"/>), over the sine and cosine <see cref="FixedQ4816.SinCosCore"/> answers from a
/// turn fraction without rounding through radians. The step count never depends on the operands, so the result is
/// the same on every machine.</para>
/// <para>The plane is carried as two unit vectors, so no trigonometry is inverted at evaluation: a position is the
/// periapsis direction times the along-axis coordinate plus the transverse direction times the cross-axis one.</para>
/// </remarks>
/// <param name="SemiMajorAxis">The positive semi-major axis, in the caller's length unit.</param>
/// <param name="Eccentricity">The eccentricity, zero for a circle and below <see cref="MaxEccentricity"/>.</param>
/// <param name="Periapsis">The unit direction from the focus to the periapsis.</param>
/// <param name="Transverse">The unit direction, in the orbit's plane, a quarter turn ahead of
/// <paramref name="Periapsis"/> in the direction of travel.</param>
public readonly record struct FixedConicOrbit(FixedQ4816 SemiMajorAxis, FixedQ4816 Eccentricity, FixedVector3 Periapsis, FixedVector3 Transverse) {
    /// <summary>The Newton steps <see cref="EccentricAnomaly"/> takes, whatever its operands.</summary>
    public const int KeplerIterations = 12;

    private const int AnomalyFractionBits = 60;
    private const long AnomalyOne = (1L << AnomalyFractionBits);
    // An eccentricity under this is a circle to the fit: its periapsis direction is lost in rounding, so the fit
    // names the body's own direction as the axis and zero phase.
    private const long CircularRaw = 64L;

    // round(2^64 / 2π): a radian measure times this is a turn fraction scaled by 2^64.
    private static readonly Int128 TurnsPerRadianQ64 = ((Int128)(FixedQ4816.SinCosInvTwoPiQ96 >> 32));

    /// <summary>Gets the eccentricity at and above which an orbit is refused. Below it the Newton solve settles
    /// within <see cref="KeplerIterations"/> steps at every phase; nearer one the steps it needs grow without bound.</summary>
    public static FixedQ4816 MaxEccentricity => FixedQ4816.FromRawBits(value: 58982L);

    /// <summary>Gets the semi-minor axis, the semi-major one shortened by the eccentricity.</summary>
    public FixedQ4816 SemiMinorAxis => (SemiMajorAxis * FixedQ4816.Sqrt(value: (FixedQ4816.One - (Eccentricity * Eccentricity))));

    private static long NarrowProduct(long anomalyValue, FixedQ4816 length) {
        var product = (((Int128)anomalyValue) * length.Value);

        return ((long)((product + (Int128.One << (AnomalyFractionBits - 1))) >> AnomalyFractionBits));
    }
    private static ulong TurnsOfRadians(FixedQ4816 radians) =>
        unchecked((ulong)((long)((((Int128)radians.Value) * TurnsPerRadianQ64) >> FixedQ4816.FractionBitCount)));

    /// <summary>Returns the position at a phase, relative to the focus.</summary>
    /// <param name="phase">The mean anomaly as a fraction of one turn scaled by 2^64; zero is the periapsis.</param>
    /// <returns>The position, in the unit of <see cref="SemiMajorAxis"/>.</returns>
    public FixedVector3 PositionAt(ulong phase) {
        var (sin, cos) = FixedQ4816.SinCosCore(fractionalTurns: unchecked((long)EccentricAnomaly(eccentricity: Eccentricity, phase: phase)));
        var along = FixedQ4816.FromRawBits(value: NarrowProduct(anomalyValue: (cos - (Eccentricity.Value << (AnomalyFractionBits - FixedQ4816.FractionBitCount))), length: SemiMajorAxis));
        var across = FixedQ4816.FromRawBits(value: NarrowProduct(anomalyValue: sin, length: SemiMinorAxis));

        return ((Periapsis * along) + (Transverse * across));
    }

    /// <summary>Solves Kepler's equation for the eccentric anomaly.</summary>
    /// <param name="phase">The mean anomaly as a fraction of one turn scaled by 2^64.</param>
    /// <param name="eccentricity">The eccentricity, zero through <see cref="MaxEccentricity"/>.</param>
    /// <returns>The eccentric anomaly as a fraction of one turn scaled by 2^64. A circle's is its mean anomaly.</returns>
    public static ulong EccentricAnomaly(ulong phase, FixedQ4816 eccentricity) {
        if (eccentricity <= FixedQ4816.Zero) {
            return phase;
        }

        // Both anomalies are read as signed turns in [-1/2, 1/2): the equation is odd about the periapsis and the
        // root shares the mean anomaly's sign, so the solve never crosses the half turn it clamps to.
        var mean = unchecked((long)phase);
        var eccentricityValue = (eccentricity.Value << (AnomalyFractionBits - FixedQ4816.FractionBitCount));
        var anomaly = mean;

        for (var step = 0; (step < KeplerIterations); step++) {
            var (sin, cos) = FixedQ4816.SinCosCore(fractionalTurns: anomaly);
            var sineTerm = (((((Int128)eccentricityValue) * sin) >> AnomalyFractionBits) * TurnsPerRadianQ64) >> AnomalyFractionBits;
            var residual = ((((Int128)anomaly) - mean) - sineTerm);
            var slope = (AnomalyOne - ((long)((((Int128)eccentricityValue) * cos) >> AnomalyFractionBits)));
            var next = (((Int128)anomaly) - ((residual << AnomalyFractionBits) / slope));

            anomaly = ((next <= long.MinValue)
                ? long.MinValue
                : ((next >= long.MaxValue) ? long.MaxValue : ((long)next))
            );
        }

        return unchecked((ulong)anomaly);
    }
    /// <summary>Builds an orbit from its shape and the three angles that place its plane, about a Y-up reference:
    /// the plane's normal is +Y at zero inclination, and travel is about that normal by the right-hand rule.</summary>
    /// <param name="semiMajorAxis">The positive semi-major axis.</param>
    /// <param name="eccentricity">The eccentricity, zero through <see cref="MaxEccentricity"/>.</param>
    /// <param name="inclination">The tilt of the plane about its line of nodes, in radians.</param>
    /// <param name="ascendingNode">The heading of the line of nodes about +Y from +X, in radians.</param>
    /// <param name="argumentOfPeriapsis">The angle in the plane from the line of nodes to the periapsis, in radians.</param>
    /// <returns>The orbit.</returns>
    public static FixedConicOrbit FromAngles(FixedQ4816 semiMajorAxis, FixedQ4816 eccentricity, FixedQ4816 inclination, FixedQ4816 ascendingNode, FixedQ4816 argumentOfPeriapsis) {
        var plane = ((FixedQuaternion.FromAxisAngle(angle: ascendingNode, axis: FixedVector3.UnitY) *
            FixedQuaternion.FromAxisAngle(angle: inclination, axis: FixedVector3.UnitX)) *
            FixedQuaternion.FromAxisAngle(angle: argumentOfPeriapsis, axis: FixedVector3.UnitY));

        return new FixedConicOrbit(
            Eccentricity: eccentricity,
            Periapsis: plane.Rotate(vector: FixedVector3.UnitX).Normalize(),
            SemiMajorAxis: semiMajorAxis,
            Transverse: plane.Rotate(vector: -FixedVector3.UnitZ).Normalize()
        );
    }
    /// <summary>Returns the phase a period has reached after an elapsed tick count: the elapsed ticks modulo the
    /// period, over the period, by one exact integer division.</summary>
    /// <param name="elapsedTicks">The ticks elapsed since the phase was zero.</param>
    /// <param name="periodTicks">The positive period in the same ticks.</param>
    /// <returns>The fraction of one turn scaled by 2^64, rounded toward zero.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="periodTicks"/> is zero.</exception>
    public static ulong Phase(ulong elapsedTicks, ulong periodTicks) => Phase(elapsedTicks: elapsedTicks, periodTicks: periodTicks, turns: 1UL);
    /// <summary>Returns the phase reached after an elapsed tick count at a rate of <paramref name="turns"/> turns every
    /// <paramref name="periodTicks"/> ticks: the fractional part of <c>elapsed · turns / period</c>, by one exact
    /// 128-bit product, remainder and division, so a clock running at any rational rate keeps its phase to a 2^-64 of a
    /// turn however long it has run.</summary>
    /// <param name="elapsedTicks">The ticks elapsed since the phase was zero.</param>
    /// <param name="turns">The turns the phase makes in <paramref name="periodTicks"/>.</param>
    /// <param name="periodTicks">The positive tick count <paramref name="turns"/> are made in.</param>
    /// <returns>The fraction of one turn scaled by 2^64, rounded toward zero.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="periodTicks"/> is zero.</exception>
    public static ulong Phase(ulong elapsedTicks, ulong periodTicks, ulong turns) {
        ArgumentOutOfRangeException.ThrowIfZero(value: periodTicks);

        return ((ulong)(((((UInt128)elapsedTicks) * turns) % periodTicks << 64) / periodTicks));
    }
    /// <summary>Returns the period of an orbit whose semi-major axis is a ratio of a reference orbit's about the
    /// same focus: the reference period times the ratio raised to three halves, Kepler's third law.</summary>
    /// <param name="referencePeriodTicks">The reference orbit's period in ticks.</param>
    /// <param name="semiMajorRatio">The orbit's semi-major axis over the reference orbit's.</param>
    /// <param name="periodTicks">The period on return, at least one tick.</param>
    /// <returns><see langword="true"/> when the period is representable.</returns>
    public static bool TryScalePeriod(ulong referencePeriodTicks, FixedQ4816 semiMajorRatio, out ulong periodTicks) {
        periodTicks = 0UL;

        if (semiMajorRatio <= FixedQ4816.Zero) {
            return false;
        }

        try {
            var power = FixedQ4816.Sqrt(value: checked(((semiMajorRatio * semiMajorRatio) * semiMajorRatio)));
            var scaled = ((((UInt128)referencePeriodTicks) * ((ulong)power.Value)) >> FixedQ4816.FractionBitCount);

            if (scaled > ulong.MaxValue) {
                return false;
            }

            periodTicks = Math.Max(val1: ((ulong)scaled), val2: 1UL);

            return true;
        } catch (OverflowException) {
            return false;
        }
    }
    /// <summary>Fits the orbit a body at a position with a velocity is on, in canonical units: lengths in units of a
    /// reference orbit's semi-major axis and time in units of that orbit's period over a full turn in radians, so
    /// the focus's gravitational parameter is one and every intermediate is of order one.</summary>
    /// <param name="position">The body's position from the focus, in reference semi-major axes.</param>
    /// <param name="velocity">The body's velocity, in reference semi-major axes per canonical time unit.</param>
    /// <param name="orbit">The fitted orbit on return, its semi-major axis as a ratio of the reference one.</param>
    /// <param name="phase">The body's phase on that orbit, as a fraction of one turn scaled by 2^64.</param>
    /// <returns><see langword="true"/> when the body is on a closed orbit below <see cref="MaxEccentricity"/>;
    /// <see langword="false"/> when it escapes, falls straight at the focus, stands at it, or an intermediate leaves
    /// the grid.</returns>
    public static bool TryFit(FixedVector3 position, FixedVector3 velocity, out FixedConicOrbit orbit, out ulong phase) {
        orbit = default;
        phase = 0UL;

        try {
            var distance = position.Length;

            if (distance <= FixedQ4816.Zero) {
                return false;
            }

            // Vis-viva with a unit gravitational parameter: 1/a = 2/r - v².
            var inverseAxis = checked(((FixedQ4816.FromInteger(value: 2L) / distance) - FixedVector3.Dot(left: velocity, right: velocity)));

            if (inverseAxis <= FixedQ4816.Zero) {
                return false;
            }

            var axis = (FixedQ4816.One / inverseAxis);
            var momentum = FixedVector3.Cross(left: position, right: velocity);
            var normal = momentum.Normalize();

            if (normal == FixedVector3.Zero) {
                return false;
            }

            var outward = position.Normalize();
            var eccentricityVector = (FixedVector3.Cross(left: velocity, right: momentum) - outward);
            var eccentricity = eccentricityVector.Length;

            if (eccentricity >= MaxEccentricity) {
                return false;
            }

            if (eccentricity.Value < CircularRaw) {
                orbit = new FixedConicOrbit(
                    Eccentricity: FixedQ4816.Zero,
                    Periapsis: outward,
                    SemiMajorAxis: axis,
                    Transverse: FixedVector3.Cross(left: normal, right: outward).Normalize()
                );

                return true;
            }

            var periapsis = eccentricityVector.Normalize();
            var cosine = FixedQ4816.Clamp(
                maximum: FixedQ4816.One,
                minimum: -FixedQ4816.One,
                value: ((FixedQ4816.One - (distance / axis)) / eccentricity)
            );
            var sine = (FixedVector3.Dot(left: position, right: velocity) / (eccentricity * FixedQ4816.Sqrt(value: axis)));
            var anomaly = FixedQ4816.Atan2(x: cosine, y: sine);

            orbit = new FixedConicOrbit(
                Eccentricity: eccentricity,
                Periapsis: periapsis,
                SemiMajorAxis: axis,
                Transverse: FixedVector3.Cross(left: normal, right: periapsis).Normalize()
            );
            // Kepler's equation forward: the mean anomaly is the eccentric one less the eccentricity times its sine.
            phase = TurnsOfRadians(radians: (anomaly - (eccentricity * FixedQ4816.Sin(angle: anomaly))));

            return true;
        } catch (OverflowException) {
            orbit = default;
            phase = 0UL;

            return false;
        } catch (DivideByZeroException) {
            orbit = default;
            phase = 0UL;

            return false;
        }
    }
}
