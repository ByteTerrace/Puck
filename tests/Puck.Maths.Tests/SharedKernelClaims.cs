using System.Globalization;
using System.Numerics;

namespace Puck.Maths.Tests;

/// <summary>Claims over the shared kernels every consumer lane routes through: the circle constants and degree
/// conversion, the angle wrap, the directed vector rescale, the exact sphere and cone predicates, the attitude
/// builders, the saturating product, the tick conversions, the directed quotients and double seams, the follower
/// lane replacements, the saturating integer and zigzag helpers, the 64-bit bit mix, the one-shot weighted pick, the
/// scatter point and the rational doors.</summary>
internal static class SharedKernelClaims {
    private const long RawOne = (1L << 16);

    private static FixedQ4816 Raw(long value) =>
        FixedQ4816.FromRawBits(value: value);
    private static FixedVector3 Vector(ReadOnlySpan<long> raws) =>
        new(
            X: Raw(value: raws[0]),
            Y: Raw(value: raws[1]),
            Z: Raw(value: raws[2])
        );
    private static string Render(FixedVector3 vector) =>
        string.Create(
            provider: CultureInfo.InvariantCulture,
            $"({vector.X.Value}, {vector.Y.Value}, {vector.Z.Value})"
        );

    // ---- FixedQ4816: the circle constants, the degree conversion and the angle wrap ----

    /// <summary>Pi, HalfPi and Tau are the nearest raws to the Machin-derived π, and PiQ189 — its three limbs read
    /// back as one integer — is the nearest integer to π·2^189, with its high limb equal to PiQ61.</summary>
    public static string? PiConstantsVsMachin() {
        var enclosure = Oracles.Pi(bitCount: 256);

        static string? Check(string name, long actual, BigInteger low, BigInteger high, BigInteger denominator) {
            var expectedLow = Oracles.RoundRationalTiesToEven(
                denominator: denominator,
                numerator: low
            );
            var expectedHigh = Oracles.RoundRationalTiesToEven(
                denominator: denominator,
                numerator: high
            );

            if (expectedLow != expectedHigh) { return $"{name}: the enclosure does not decide the rounding"; }
            if (expectedLow != actual) { return $"{name} is raw {actual}, not the nearest {expectedLow}"; }

            return null;
        }

        var scale = (BigInteger.One << (256 - 16));

        if (Check(
            actual: FixedQ4816.Pi.Value,
            denominator: scale,
            high: enclosure.High,
            low: enclosure.Low,
            name: "Pi"
        ) is { } piFailure) { return piFailure; }
        if (Check(
            actual: FixedQ4816.HalfPi.Value,
            denominator: (scale << 1),
            high: enclosure.High,
            low: enclosure.Low,
            name: "HalfPi"
        ) is { } halfFailure) { return halfFailure; }
        if (Check(
            actual: FixedQ4816.Tau.Value,
            denominator: (scale >> 1),
            high: enclosure.High,
            low: enclosure.Low,
            name: "Tau"
        ) is { } tauFailure) { return tauFailure; }

        var fractionBits = FixedQ4816.PiQ189FractionBitCount;
        var piQ189 = FixedQ4816.PiQ189;
        var limbs = (((((BigInteger)FixedQ4816.PiQ61) << 128) | (((BigInteger)FixedQ4816.PiQ189MiddleLimb) << 64)) | FixedQ4816.PiQ189LowLimb);

        if (fractionBits != 189) { return $"PiQ189 declares {fractionBits} fraction bits"; }
        if (piQ189 != limbs) { return "PiQ189 is not its three limbs read back as one integer"; }

        var denominator189 = (BigInteger.One << (256 - fractionBits));
        var lowQ189 = Oracles.RoundRationalTiesToEven(
            denominator: denominator189,
            numerator: enclosure.Low
        );
        var highQ189 = Oracles.RoundRationalTiesToEven(
            denominator: denominator189,
            numerator: enclosure.High
        );

        if (lowQ189 != highQ189) { return "the enclosure does not decide PiQ189"; }
        if (lowQ189 != piQ189) { return $"PiQ189 is {piQ189}, not the nearest {lowQ189}"; }

        // Tau's raw is odd, which is the whole reason WrapAngle closes its interval at +Pi.
        var tauRaw = FixedQ4816.Tau.Value;
        var piRaw = FixedQ4816.Pi.Value;

        if ((tauRaw & 1L) == 0L) { return "Tau's raw is even"; }
        if (tauRaw == (piRaw + piRaw)) { return "Tau equals Pi + Pi, so the odd-raw interval argument is void"; }

        return null;
    }
    /// <summary>FromDegrees over a fixed-point degree raw and over the same value as a double: both equal the raw the
    /// 256-bit π enclosure rounds to, and the two overloads agree with each other where the double is exact.</summary>
    /// <param name="deep">Whether to add the whole-and-half-degree ladder over four turns, both overloads.</param>
    /// <param name="left">The degree raw in its first lane.</param>
    /// <param name="right">A second degree raw, swept as a double-exact value in its first lane.</param>
    public static string? FromDegreesVsExactRational(bool deep, long[] left, long[] right) {
        if (CheckFromDegreesRaw(raw: left[0]) is { } rawFailure) { return rawFailure; }
        // A raw below 2^53 is exact as a double, so the double overload must land on the same raw.
        if (CheckFromDegreesExactDouble(raw: (right[0] >> 11)) is { } doubleFailure) { return doubleFailure; }

        if (deep) {
            for (var halfDegrees = -1440L; (halfDegrees <= 1440L); ++halfDegrees) {
                var raw = (halfDegrees * (RawOne / 2L));

                if (CheckFromDegreesRaw(raw: raw) is { } ladderFailure) { return ladderFailure; }
                if (CheckFromDegreesExactDouble(raw: raw) is { } ladderDoubleFailure) { return ladderDoubleFailure; }
            }
        }

        return null;
    }
    private static string? CheckFromDegreesRaw(long raw) {
        if (!Oracles.TryDegreesToRadiansRaw(
            denominatorExponent: 16,
            numerator: raw,
            raw: out var expected
        )) { return $"the enclosure does not decide degrees raw {raw}"; }

        var expectedRaw = ((expected > long.MaxValue)
            ? long.MaxValue
            : ((expected < long.MinValue)
                ? long.MinValue
                : ((long)expected)));
        var fixedResult = FixedQ4816.FromDegrees(degrees: Raw(value: raw)).Value;

        return ((fixedResult != expectedRaw)
            ? $"FromDegrees(FixedQ4816 raw {raw}) = {fixedResult}, expected {expectedRaw}"
            : null);
    }
    private static string? CheckFromDegreesExactDouble(long raw) {
        var asDouble = (((double)raw) / 65536d);

        if (!Oracles.TryDegreesToRadiansRaw(
            denominatorExponent: 16,
            numerator: raw,
            raw: out var expected
        )) { return $"the enclosure does not decide degrees raw {raw}"; }

        var doubleResult = FixedQ4816.FromDegrees(degrees: asDouble).Value;

        if (doubleResult != ((long)expected)) { return $"FromDegrees(double {asDouble:R}) = {doubleResult}, expected {expected}"; }
        if (doubleResult != FixedQ4816.FromDegrees(degrees: Raw(value: raw)).Value) { return $"the two FromDegrees overloads disagree at degrees raw {raw}"; }

        return null;
    }
    /// <summary>FromDegrees(double) at a hand-derived ladder: the IEEE decomposition of each double is rounded
    /// against the π enclosure, the double product form is NOT what it returns, and the non-finite inputs follow
    /// FromDouble's own rule.</summary>
    public static string? FromDegreesDoubleLadder() {
        if (FixedQ4816.FromDegrees(degrees: double.NaN).Value != 0L) { return "NaN degrees did not map to zero"; }
        if (FixedQ4816.FromDegrees(degrees: double.PositiveInfinity) != FixedQ4816.MaxValue) { return "+inf degrees did not saturate"; }
        if (FixedQ4816.FromDegrees(degrees: double.NegativeInfinity) != FixedQ4816.MinValue) { return "-inf degrees did not saturate"; }
        if (FixedQ4816.FromDegrees(degrees: 1e300) != FixedQ4816.MaxValue) { return "1e300 degrees did not saturate"; }
        if (FixedQ4816.FromDegrees(degrees: -1e300) != FixedQ4816.MinValue) { return "-1e300 degrees did not saturate"; }
        if (FixedQ4816.FromDegrees(degrees: 0d).Value != 0L) { return "zero degrees is not zero"; }
        if (FixedQ4816.FromDegrees(degrees: -0d).Value != 0L) { return "negative zero degrees is not zero"; }

        double[] ladder = [
            1d, -1d, 30d, 45d, 60d, 90d, 180d, 270d, 360d, 720d, -90d, -180d, 0.5d, 0.1d, 12.34d, 1e-7d, 1e-30d,
            5e-324d, 179.99999d, 1e6d, 1e12d, 8.0e13d, 1.0e14d, 1.5e14d,
        ];

        foreach (var degrees in ladder) {
            var (numerator, exponent) = Oracles.Binary64Rational(value: degrees);

            if (!Oracles.TryDegreesToRadiansRaw(
                denominatorExponent: exponent,
                numerator: numerator,
                raw: out var expected
            )) { return $"the enclosure does not decide {degrees:R} degrees"; }

            var expectedRaw = ((expected > long.MaxValue)
                ? long.MaxValue
                : ((expected < long.MinValue)
                    ? long.MinValue
                    : ((long)expected)));
            var actual = FixedQ4816.FromDegrees(degrees: degrees).Value;

            if (actual != expectedRaw) { return $"FromDegrees({degrees:R}) = {actual}, expected {expectedRaw}"; }
        }

        // 45° is exactly π/4: 51471.85… raw, and the double product form 45 * (Math.PI / 180) quantizes the same
        // here, so the discriminating witness is 1e12°, where the double product's two roundings drift.
        if (FixedQ4816.FromDegrees(degrees: 45d).Value != 51472L) { return "45 degrees is not raw 51472"; }
        if (FixedQ4816.FromDegrees(degrees: 180d).Value != FixedQ4816.Pi.Value) { return "180 degrees is not Pi"; }
        if (FixedQ4816.FromDegrees(degrees: 360d).Value != FixedQ4816.Tau.Value) { return "360 degrees is not Tau"; }
        if (FixedQ4816.FromDegrees(degrees: 90d).Value != FixedQ4816.HalfPi.Value) { return "90 degrees is not HalfPi"; }

        return null;
    }
    /// <summary>WrapAngle is congruent to its input modulo Tau's raw and lands in the closed raw interval
    /// [−205887, 205887]; it is the identity there, and the two ends map as the half-open interval at −Pi says.</summary>
    /// <param name="deep">Whether to sweep every raw of the band [−2·Tau, 2·Tau] whole.</param>
    /// <param name="left">The angle raw in its first lane.</param>
    /// <param name="right">A second angle raw in its first lane.</param>
    public static string? WrapAngleResidueAndRange(bool deep, long[] left, long[] right) {
        var tau = FixedQ4816.Tau.Value;
        var pi = FixedQ4816.Pi.Value;

        static string? Check(long raw, long tau, long pi) {
            var wrapped = FixedQ4816.WrapAngle(radians: Raw(value: raw)).Value;
            var difference = (((BigInteger)raw) - wrapped);

            if (!(difference % tau).IsZero) { return $"WrapAngle({raw}) = {wrapped} is not congruent modulo {tau}"; }
            if ((wrapped < -pi) || (wrapped > pi)) { return $"WrapAngle({raw}) = {wrapped} left [-{pi}, {pi}]"; }
            if ((raw >= -pi) && (raw <= pi) && (wrapped != raw)) { return $"WrapAngle({raw}) moved an angle already inside the interval"; }

            return null;
        }

        foreach (var raw in (ReadOnlySpan<long>)[left[0], right[0], pi, -pi, (pi + 1L), -(pi + 1L), tau, -tau, (tau + pi), 0L, long.MaxValue, long.MinValue]) {
            if (Check(pi: pi, raw: raw, tau: tau) is { } failure) { return failure; }
        }

        if (deep) {
            for (var raw = -(2L * tau); (raw <= (2L * tau)); ++raw) {
                if (Check(pi: pi, raw: raw, tau: tau) is { } bandFailure) { return bandFailure; }
            }
        }

        if (FixedQ4816.WrapAngle(radians: Raw(value: (pi + 1L))).Value != -pi) { return "one raw past Pi did not land on -Pi, its residue under the odd Tau"; }
        if (FixedQ4816.WrapAngle(radians: Raw(value: -(pi + 1L))).Value != pi) { return "one raw below -Pi did not land on +Pi"; }
        if (FixedQ4816.WrapAngle(radians: FixedQ4816.Tau).Value != 0L) { return "a full turn did not wrap to zero"; }

        return null;
    }
    /// <summary>The explicit float conversion is the double conversion narrowed: bit-identical to (float)(double)x
    /// at every swept raw, and exact below 2^24 raw.</summary>
    /// <param name="left">The raw in its first lane.</param>
    /// <param name="right">A second raw in its first lane.</param>
    public static string? FloatProjectionThroughDouble(long[] left, long[] right) {
        foreach (var raw in (ReadOnlySpan<long>)[left[0], right[0], 0L, 1L, -1L, 65536L, (1L << 24), ((1L << 24) + 1L), long.MaxValue, long.MinValue]) {
            var value = Raw(value: raw);
            var actual = BitConverter.SingleToUInt32Bits(value: ((float)value));
            var expected = BitConverter.SingleToUInt32Bits(value: ((float)((double)value)));

            if (actual != expected) { return $"(float) of raw {raw} is 0x{actual:X8}, not the narrowed double 0x{expected:X8}"; }

            if ((raw > -(1L << 24)) && (raw < (1L << 24))) {
                var exact = BitConverter.SingleToUInt32Bits(value: (((float)raw) / 65536f));

                if (actual != exact) { return $"(float) of raw {raw} is not the exact quotient"; }
            }
        }

        return null;
    }

    // ---- FixedVector2 and FixedVector3: the doors, the directed rescale and the exact predicates ----

    /// <summary>FromVector2 is FromDouble lane by lane and ToVector2 the float projection lane by lane; ClampLength and
    /// WithLength never exceed their bound on the exact sum of squares and shorten each lane by at most one raw plus
    /// 2^-45 of it.</summary>
    /// <param name="left">The vector's two raws.</param>
    /// <param name="right">A bound raw in its first lane and a second vector lane in its second.</param>
    public static string? PlaneDoorsAndClamp(long[] left, long[] right) {
        var vector = new FixedVector2(
            X: Raw(value: left[0]),
            Y: Raw(value: left[1])
        );
        var single = vector.ToVector2();

        if (BitConverter.SingleToUInt32Bits(value: single.X) != BitConverter.SingleToUInt32Bits(value: ((float)vector.X))) { return "ToVector2's X lane is not the float projection"; }
        if (BitConverter.SingleToUInt32Bits(value: single.Y) != BitConverter.SingleToUInt32Bits(value: ((float)vector.Y))) { return "ToVector2's Y lane is not the float projection"; }

        var door = FixedVector2.FromVector2(value: single);

        if (door.X != FixedQ4816.FromDouble(value: single.X)) { return "FromVector2's X lane is not FromDouble"; }
        if (door.Y != FixedQ4816.FromDouble(value: single.Y)) { return "FromVector2's Y lane is not FromDouble"; }

        foreach (var bound in (ReadOnlySpan<long>)[right[0], 0L, -1L, 1L, RawOne, long.MaxValue]) {
            var boundValue = Raw(value: bound);
            var clamped = vector.ClampLength(maximum: boundValue);
            var rescaled = vector.WithLength(length: boundValue);

            if (CheckRescale(
                bound: bound,
                lanes: [left[0], left[1]],
                rescaled: [rescaled.X.Value, rescaled.Y.Value]
            ) is { } rescaleFailure) { return rescaleFailure; }

            var within = (Oracles.SquaredNorm(raws: left) <= (((BigInteger)bound) * bound));

            if ((bound >= 0L) && within && (clamped != vector)) { return $"ClampLength({bound}) moved a vector already within it"; }
            if (((bound < 0L) || !within) && (clamped != rescaled)) { return $"ClampLength({bound}) is not WithLength for a vector outside it"; }
        }

        return null;
    }
    /// <summary>WithLength and ClampLength over three lanes: the exact squared length of the result never exceeds the
    /// bound's square, every lane keeps its sign and is shortened by at most one raw plus 2^-45 of its magnitude,
    /// a zero vector or non-positive length answers Zero, and ClampLength is the identity exactly where IsWithin
    /// admits the vector.</summary>
    /// <param name="deep">Whether to add every power-of-two bound and its two neighbours to the ladder.</param>
    /// <param name="left">The vector's three raws.</param>
    /// <param name="right">Bound raws.</param>
    public static string? WithLengthTowardZero(bool deep, long[] left, long[] right) {
        var vector = Vector(raws: left);
        var within = Oracles.SquaredNorm(raws: left);
        var bounds = new List<long> { right[0], right[1], right[2], 0L, -1L, 1L, 3L, RawOne, (RawOne * 1000L), (1L << 45), long.MaxValue };

        if (deep) {
            for (var bit = 0; (bit < 63); ++bit) {
                bounds.AddRange(collection: [(1L << bit), ((1L << bit) - 1L), ((1L << bit) + 1L)]);
            }
        }

        foreach (var bound in bounds) {
            var boundValue = Raw(value: bound);
            var rescaled = vector.WithLength(length: boundValue);
            var clamped = vector.ClampLength(maximum: boundValue);

            if (CheckRescale(
                bound: bound,
                lanes: left,
                rescaled: [rescaled.X.Value, rescaled.Y.Value, rescaled.Z.Value]
            ) is { } rescaleFailure) { return rescaleFailure; }

            var admitted = ((bound >= 0L) && (within <= (((BigInteger)bound) * bound)));

            if (admitted != vector.IsWithin(radius: boundValue)) { return $"IsWithin({bound}) disagrees with the exact squares at {Render(vector: vector)}"; }
            if (admitted && (clamped != vector)) { return $"ClampLength({bound}) moved {Render(vector: vector)}, which is within it"; }
            if (!admitted && (clamped != rescaled)) { return $"ClampLength({bound}) is not WithLength at {Render(vector: vector)}"; }
        }

        return null;
    }
    // The directed-rescale contract on the exact integers: |w|² ≤ bound², sign agreement per lane, and each lane within
    // one raw plus 2^-45 of its magnitude below the exact lane v_i·bound/|v| — checked as (|w_i| + 1)² · S > (v_i·bound)²
    // scaled by (1 − 2^-45)², which is the squared form of |w_i| + 1 > |exact_i| · (1 − 2^-45).
    private static string? CheckRescale(ReadOnlySpan<long> lanes, long bound, ReadOnlySpan<long> rescaled) {
        var sum = Oracles.SquaredNorm(raws: lanes);

        if (sum.IsZero || (bound <= 0L)) {
            foreach (var lane in rescaled) {
                if (lane != 0L) { return $"WithLength({bound}) of a zero vector or non-positive length is not Zero"; }
            }

            return null;
        }

        var boundSquared = (((BigInteger)bound) * bound);

        if (Oracles.SquaredNorm(raws: rescaled) > boundSquared) { return $"WithLength({bound}) exceeds its bound on the exact squares"; }

        var slack = ((BigInteger.One << 45) - BigInteger.One);
        var slackSquared = (slack * slack);
        var unitSquared = (BigInteger.One << 90);

        for (var lane = 0; (lane < lanes.Length); ++lane) {
            var exactNumerator = (((BigInteger)lanes[lane]) * bound);

            if ((Math.Sign(value: lanes[lane]) != Math.Sign(value: rescaled[lane])) && (rescaled[lane] != 0L)) { return $"WithLength({bound}) flipped lane {lane}"; }

            var magnitude = BigInteger.Abs(value: ((BigInteger)rescaled[lane]));
            var shortened = (magnitude + BigInteger.One);

            // |w_i| ≤ |exact_i|: w_i² · S ≤ (v_i · bound)².
            if (((magnitude * magnitude) * sum) > (exactNumerator * exactNumerator)) { return $"WithLength({bound}) lane {lane} exceeds the exact lane"; }
            // |w_i| + 1 > |exact_i| · (1 − 2^-45): (|w_i| + 1)² · S · 2^90 > (v_i · bound)² · slack².
            if ((((shortened * shortened) * sum) * unitSquared) <= ((exactNumerator * exactNumerator) * slackSquared)) { return $"WithLength({bound}) lane {lane} is more than one raw short of the exact lane"; }
        }

        return null;
    }
    /// <summary>IsWithin is the exact predicate |d|² ≤ r² on the raw sums of squares: at radii drawn freely, at the
    /// exact root's neighbours, at MaxValue (no longer a universal admission), at zero and at negative radii.</summary>
    /// <param name="deep">Whether to add every power-of-two radius and its two neighbours to the ladder.</param>
    /// <param name="left">The vector's three raws.</param>
    /// <param name="right">Three freely drawn radius raws.</param>
    public static string? IsWithinExactSquares(bool deep, long[] left, long[] right) {
        var vector = Vector(raws: left);
        var sum = Oracles.SquaredNorm(raws: left);
        var root = Oracles.IntegerSquareRoot(value: sum);
        var reported = ((root > long.MaxValue)
            ? long.MaxValue
            : ((long)root));
        var radii = new List<long> {
            right[0], right[1], right[2],
            reported, (reported - ((reported == long.MinValue) ? 0L : 1L)), (reported + ((reported == long.MaxValue) ? 0L : 1L)),
            long.MaxValue, (long.MaxValue - 1L), 0L, -1L, long.MinValue,
        };

        if (deep) {
            for (var bit = 0; (bit < 63); ++bit) {
                radii.AddRange(collection: [(1L << bit), ((1L << bit) - 1L), ((1L << bit) + 1L), -(1L << bit)]);
            }
        }

        foreach (var radius in radii) {
            var expected = ((radius >= 0L) && (sum <= (((BigInteger)radius) * radius)));
            var actual = vector.IsWithin(radius: Raw(value: radius));

            if (actual != expected) { return $"{Render(vector: vector)}.IsWithin({radius}) = {actual}, but the exact squares say {expected}"; }
        }

        return null;
    }
    /// <summary>IsWithinCone against the exact rational verdict over the whole signed carrier folded onto lanes below
    /// 2^31 — the band the subject decides exactly — at a ladder of half-angle cosines on both sides of zero,
    /// with the apex, the zero forward, the clamped cosines and the quarter-turn cones pinned by hand.</summary>
    /// <param name="deep">Whether to sweep the half-angle cosine at every 1/64 of the unit on both sides of zero,
    /// and the offset folded onto the narrow band below 2^20 where an exact root is likelier.</param>
    /// <param name="left">The offset's three raws.</param>
    /// <param name="right">The forward's three raws.</param>
    public static string? IsWithinConeVsExactRational(bool deep, long[] left, long[] right) {
        static long Fold(long raw) => (raw % (1L << 31));
        static long FoldNarrow(long raw) => (raw % (1L << 20));

        long[] offset = (deep
            ? [FoldNarrow(raw: left[0]), FoldNarrow(raw: left[1]), FoldNarrow(raw: left[2])]
            : [Fold(raw: left[0]), Fold(raw: left[1]), Fold(raw: left[2])]);
        long[] forward = [Fold(raw: right[0]), Fold(raw: right[1]), Fold(raw: right[2])];
        var offsetVector = Vector(raws: offset);
        var forwardVector = Vector(raws: forward);
        var cosines = new List<long> { RawOne, (RawOne - 1L), 56756L, 46341L, 32768L, 1L, 0L, -1L, -32768L, -46341L, -(RawOne - 1L), -RawOne };

        if (deep) {
            for (var step = -64L; (step <= 64L); ++step) {
                cosines.Add(item: (step * 1024L));
            }
        }

        foreach (var cosine in cosines) {
            var expected = Oracles.ConeContains(
                cosineRaw: cosine,
                forward: forward,
                offset: offset
            );
            var actual = offsetVector.IsWithinCone(
                cosineHalfAngle: Raw(value: cosine),
                forward: forwardVector
            );

            if (actual != expected) { return $"{Render(vector: offsetVector)}.IsWithinCone({Render(vector: forwardVector)}, {cosine}) = {actual}, exact says {expected}"; }
        }

        // The cosine is clamped: beyond ±1 the verdict is that of ±1.
        if (offsetVector.IsWithinCone(cosineHalfAngle: Raw(value: (RawOne + 7L)), forward: forwardVector) != offsetVector.IsWithinCone(cosineHalfAngle: FixedQ4816.One, forward: forwardVector)) { return "a cosine above one is not clamped to one"; }
        if (offsetVector.IsWithinCone(cosineHalfAngle: Raw(value: -(RawOne + 7L)), forward: forwardVector) != offsetVector.IsWithinCone(cosineHalfAngle: FixedQ4816.NegativeOne, forward: forwardVector)) { return "a cosine below minus one is not clamped to minus one"; }

        return null;
    }
    /// <summary>The cone's hand-derived ladder: the apex is inside every cone, a zero forward names none, a cosine of
    /// −1 admits everything, +1 admits the axis ray alone, the quarter-turn cone admits exactly the forward
    /// half-space, and a far offset is judged the same way as a near one along the same ray.</summary>
    public static string? IsWithinConeLadder() {
        var forward = new FixedVector3(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.NegativeOne
        );
        var apex = FixedVector3.Zero;
        var ahead = (forward * FixedQ4816.FromInteger(value: 3));
        var behind = -ahead;
        var beside = (FixedVector3.UnitX * FixedQ4816.FromInteger(value: 3));
        var cosineHalfQuarter = Raw(value: 46341L);

        if (!apex.IsWithinCone(cosineHalfAngle: FixedQ4816.One, forward: forward)) { return "the apex is outside the narrowest cone"; }
        if (!apex.IsWithinCone(cosineHalfAngle: FixedQ4816.NegativeOne, forward: forward)) { return "the apex is outside the widest cone"; }
        if (ahead.IsWithinCone(cosineHalfAngle: FixedQ4816.One, forward: FixedVector3.Zero)) { return "a zero forward admitted an offset"; }
        if (!ahead.IsWithinCone(cosineHalfAngle: FixedQ4816.One, forward: forward)) { return "the axis ray is outside the narrowest cone"; }
        if (beside.IsWithinCone(cosineHalfAngle: FixedQ4816.One, forward: forward)) { return "a perpendicular offset is inside the narrowest cone"; }
        if (!beside.IsWithinCone(cosineHalfAngle: FixedQ4816.Zero, forward: forward)) { return "a perpendicular offset is outside the quarter-turn cone"; }
        if (behind.IsWithinCone(cosineHalfAngle: FixedQ4816.Zero, forward: forward)) { return "the backward ray is inside the quarter-turn cone"; }
        if (!behind.IsWithinCone(cosineHalfAngle: FixedQ4816.NegativeOne, forward: forward)) { return "the backward ray is outside the widest cone"; }
        if (!beside.IsWithinCone(cosineHalfAngle: Raw(value: -46341L), forward: forward)) { return "a perpendicular offset is outside a cone wider than a quarter turn"; }
        if ((ahead + beside).IsWithinCone(cosineHalfAngle: Raw(value: 46342L), forward: forward)) { return "the 45-degree ray is inside a cone narrower than 45 degrees"; }
        if (!(ahead + beside).IsWithinCone(cosineHalfAngle: Raw(value: 46340L), forward: forward)) { return "the 45-degree ray is outside a cone wider than 45 degrees"; }

        // A forward of any length names the same cone: the test is on the normalized forward.
        var longForward = (forward * FixedQ4816.FromInteger(value: 1000));

        if ((ahead + beside).IsWithinCone(cosineHalfAngle: cosineHalfQuarter, forward: longForward) != (ahead + beside).IsWithinCone(cosineHalfAngle: cosineHalfQuarter, forward: forward)) { return "the forward's length changed the verdict"; }

        // Far outside the exact band both vectors scale down with one rounding: the ray verdicts still hold.
        var far = (forward * Raw(value: (1L << 40)));

        if (!far.IsWithinCone(cosineHalfAngle: FixedQ4816.One, forward: forward)) { return "a far offset on the axis is outside the narrowest cone"; }
        if ((-far).IsWithinCone(cosineHalfAngle: FixedQ4816.Zero, forward: forward)) { return "a far backward offset is inside the quarter-turn cone"; }

        return null;
    }
    /// <summary>RejectFrom is bit-identical to the inline v − axis·(v·axis) over the public operators, and for a unit
    /// axis the rejection's component along the axis is within a few raw of zero; TryRejectNormalized's unit has a
    /// Q16 length within one raw of one, its length is the nearest root of the rejection's exact squares, and it
    /// refuses a vector along the axis.</summary>
    /// <param name="left">The vector's three raws.</param>
    /// <param name="right">The axis's three raws, normalized before use.</param>
    public static string? RejectFromAndNormalized(long[] left, long[] right) {
        var vector = Vector(raws: left);
        var axis = Vector(raws: right).Normalize();

        if (axis == FixedVector3.Zero) {
            axis = FixedVector3.UnitY;
        }

        var rejected = vector.RejectFrom(unitAxis: axis);
        var inline = (vector - (axis * FixedVector3.Dot(
            left: vector,
            right: axis
        )));

        if (rejected != inline) { return $"RejectFrom at {Render(vector: vector)} about {Render(vector: axis)} is not the inline rejection"; }

        // The residual along the axis is (v·a)(1 − |a|²) plus a raw or two of lane rounding, and a Q16 unit axis
        // holds |a|² within about 2⁻¹⁵ of one, so the residual is small only while v·a is: the check folds the vector
        // under one unit per lane, where it stays within four raw.
        var folded = new FixedVector3(
            X: Raw(value: (left[0] % RawOne)),
            Y: Raw(value: (left[1] % RawOne)),
            Z: Raw(value: (left[2] % RawOne))
        );
        var residual = FixedVector3.Dot(
            left: folded.RejectFrom(unitAxis: axis),
            right: axis
        ).Value;

        if (Math.Abs(value: residual) > 4L) { return $"the rejection keeps {residual} raw along the axis at {Render(vector: folded)}"; }

        var normalized = vector.TryRejectNormalized(
            length: out var length,
            unit: out var unit,
            unitAxis: axis
        );
        var rejectedRaws = new[] { rejected.X.Value, rejected.Y.Value, rejected.Z.Value };
        var rejectedSum = Oracles.SquaredNorm(raws: rejectedRaws);
        var expectedRoot = Oracles.NearestIntegerRoot(value: rejectedSum);

        if (rejectedSum.IsZero || (expectedRoot > long.MaxValue)) {
            if (normalized || (unit != FixedVector3.Zero) || (length != FixedQ4816.Zero)) { return $"TryRejectNormalized did not refuse at {Render(vector: vector)}"; }

            return null;
        }

        if (!normalized) { return $"TryRejectNormalized refused {Render(vector: vector)} about {Render(vector: axis)}"; }
        if (length.Value != ((long)expectedRoot)) { return $"TryRejectNormalized's length {length.Value} is not the nearest root {expectedRoot}"; }

        var unitSum = Oracles.SquaredNorm(raws: [unit.X.Value, unit.Y.Value, unit.Z.Value]);
        var unitRoot = Oracles.NearestIntegerRoot(value: unitSum);

        if (BigInteger.Abs(value: (unitRoot - RawOne)) > BigInteger.One) { return $"TryRejectNormalized's unit has length raw {unitRoot}"; }

        return null;
    }
    /// <summary>TryAdd and TrySubtract answer the exact lane sums and differences in BigInteger and refuse, leaving
    /// Zero, exactly when a lane leaves the signed 64-bit raw.</summary>
    /// <param name="left">The first vector's three raws.</param>
    /// <param name="right">The second vector's three raws.</param>
    public static string? TryAddSubtractRefuseOutsideCarrier(long[] left, long[] right) {
        var first = Vector(raws: left);
        var second = Vector(raws: right);

        for (var subtract = 0; (subtract < 2); ++subtract) {
            var fits = true;
            var expected = new long[3];

            for (var lane = 0; (lane < 3); ++lane) {
                var exact = ((subtract == 0)
                    ? (((BigInteger)left[lane]) + right[lane])
                    : (((BigInteger)left[lane]) - right[lane]));

                if ((exact < long.MinValue) || (exact > long.MaxValue)) {
                    fits = false;
                } else {
                    expected[lane] = ((long)exact);
                }
            }

            var actual = ((subtract == 0)
                ? FixedVector3.TryAdd(
                    left: first,
                    right: second,
                    sum: out var result
                )
                : FixedVector3.TrySubtract(
                    difference: out result,
                    left: first,
                    right: second
                ));
            var name = ((subtract == 0)
                ? "TryAdd"
                : "TrySubtract");

            if (actual != fits) { return $"{name} answered {actual} where the exact lanes {(fits ? "fit" : "overflow")}"; }
            if (!fits && (result != FixedVector3.Zero)) { return $"{name} refused but left {Render(vector: result)}"; }
            if (fits && (result != Vector(raws: expected))) { return $"{name} returned {Render(vector: result)}, expected {Render(vector: Vector(raws: expected))}"; }
        }

        return null;
    }
    /// <summary>Yaw of a facing is Atan2(−x, −z) and reads the cardinal facings as the −Z-forward convention says;
    /// the attitude's Yaw is the yaw of its rotated −Z.</summary>
    /// <param name="left">A facing's three raws.</param>
    /// <param name="right">Unused.</param>
    public static string? YawOfFacingAndAttitude(long[] left, long[] right) {
        var facing = Vector(raws: left);
        var expected = FixedQ4816.Atan2(
            x: -facing.Z,
            y: -facing.X
        );

        if (facing.Yaw() != expected) { return $"Yaw of {Render(vector: facing)} is not Atan2(-x, -z)"; }

        if ((-FixedVector3.UnitZ).Yaw().Value != 0L) { return "facing -Z is not yaw zero"; }
        if ((-FixedVector3.UnitX).Yaw() != FixedQ4816.HalfPi) { return "facing -X is not a left quarter turn"; }
        if (FixedVector3.UnitX.Yaw() != -FixedQ4816.HalfPi) { return "facing +X is not a right quarter turn"; }
        if (FixedVector3.UnitZ.Yaw() != FixedQ4816.Pi) { return "facing +Z is not a half turn"; }
        if (FixedVector3.UnitY.Yaw().Value != 0L) { return "a facing with no planar part is not yaw zero"; }

        var attitude = FixedQuaternion.FromAxisAngle(
            angle: Raw(value: (right[0] % FixedQ4816.Tau.Value)),
            axis: FixedVector3.UnitY
        );

        if (attitude.Yaw() != attitude.Rotate(vector: -FixedVector3.UnitZ).Yaw()) { return "the attitude's Yaw is not the yaw of its rotated -Z"; }

        return null;
    }

    // ---- FixedQuaternion.FromYawPitchRoll ----

    /// <summary>FromYawPitchRoll is the product (Ry·Rx)·Rz of three axis-angle factors normalized once, its norm is
    /// within a raw of one, a pure yaw's Yaw readback is the wrapped yaw within two raw, and a pure yaw rotates −Z
    /// onto (−sin, 0, −cos) within two raw of the shipped sine and cosine.</summary>
    /// <param name="deep">Whether to walk the yaw over every 1/256 of a turn beside the drawn pitch and roll.</param>
    /// <param name="left">The yaw, pitch and roll raws, each folded onto a turn.</param>
    /// <param name="right">Unused.</param>
    public static string? FromYawPitchRollProductAndReadback(bool deep, long[] left, long[] right) {
        var tau = FixedQ4816.Tau.Value;

        if (CheckYawPitchRoll(
            pitchRaw: (left[1] % tau),
            rollRaw: (left[2] % tau),
            yawRaw: (left[0] % tau)
        ) is { } failure) { return failure; }

        if (deep) {
            for (var step = -256L; (step <= 256L); ++step) {
                if (CheckYawPitchRoll(
                    pitchRaw: (left[1] % tau),
                    rollRaw: (left[2] % tau),
                    yawRaw: ((step * tau) / 256L)
                ) is { } ladderFailure) { return ladderFailure; }
            }
        }

        return null;
    }
    private static string? CheckYawPitchRoll(long yawRaw, long pitchRaw, long rollRaw) {
        var yaw = Raw(value: yawRaw);
        var pitch = Raw(value: pitchRaw);
        var roll = Raw(value: rollRaw);
        var attitude = FixedQuaternion.FromYawPitchRoll(
            pitch: pitch,
            roll: roll,
            yaw: yaw
        );
        var product = ((FixedQuaternion.FromAxisAngle(
            angle: yaw,
            axis: FixedVector3.UnitY
        ) * FixedQuaternion.FromAxisAngle(
            angle: pitch,
            axis: FixedVector3.UnitX
        )) * FixedQuaternion.FromAxisAngle(
            angle: roll,
            axis: FixedVector3.UnitZ
        )).Normalize();

        if (attitude != product) { return $"FromYawPitchRoll({yaw.Value}, {pitch.Value}, {roll.Value}) is not the normalized (Ry·Rx)·Rz product"; }

        var norm = Oracles.NearestIntegerRoot(value: Oracles.SquaredNorm(raws: [attitude.X.Value, attitude.Y.Value, attitude.Z.Value, attitude.W.Value]));

        if (BigInteger.Abs(value: (norm - RawOne)) > BigInteger.One) { return $"FromYawPitchRoll's norm is raw {norm}"; }

        var pureYaw = FixedQuaternion.FromYawPitchRoll(
            pitch: FixedQ4816.Zero,
            roll: FixedQ4816.Zero,
            yaw: yaw
        );
        var readback = pureYaw.Yaw();
        var wrapped = FixedQ4816.WrapAngle(radians: yaw);
        var readbackGap = FixedQ4816.WrapAngle(radians: (readback - wrapped)).Value;

        if (Math.Abs(value: readbackGap) > 6L) { return $"a pure yaw of {yaw.Value} reads back as {readback.Value}, {readbackGap} raw off its wrap {wrapped.Value}"; }

        var forward = pureYaw.Rotate(vector: -FixedVector3.UnitZ);
        var (sin, cos) = FixedQ4816.SinCos(angle: yaw);

        if (Math.Abs(value: (forward.X.Value + sin.Value)) > 4L) { return $"a pure yaw of {yaw.Value} rotates -Z to X {forward.X.Value}, not -sin {-sin.Value}"; }
        if (Math.Abs(value: (forward.Z.Value + cos.Value)) > 4L) { return $"a pure yaw of {yaw.Value} rotates -Z to Z {forward.Z.Value}, not -cos {-cos.Value}"; }
        if (Math.Abs(value: forward.Y.Value) > 2L) { return $"a pure yaw of {yaw.Value} lifted -Z to Y {forward.Y.Value}"; }

        return null;
    }

    // ---- FixedSaturate.Multiply ----

    /// <summary>The saturating product is the one ties-to-even rounding of the exact product where that fits the raw,
    /// and the extreme on the exact product's side where it does not.</summary>
    public static long SaturateMultiplyRaw(long a, long b) =>
        FixedSaturate.Multiply(
            left: Raw(value: a),
            right: Raw(value: b)
        ).Value;
    /// <summary>The oracle: the exact rounded product clamped to the carrier.</summary>
    public static long SaturateMultiplyOracle(long a, long b) {
        var exact = Oracles.ExactRoundedProduct(
            a: a,
            b: b
        );

        return ((exact > long.MaxValue)
            ? long.MaxValue
            : ((exact < long.MinValue)
                ? long.MinValue
                : ((long)exact)));
    }

    // ---- FixedTickConversion: the decimal durations, the rate period and the seconds readback ----

    /// <summary>DurationTicks(decimal) rounds the exact decimal up at a hand-derived ladder and at drawn decimals
    /// against the decimal's own bit decomposition in BigInteger; DurationEngineTicks(decimal) is the same at
    /// 50400; PerRate answers the exact period and refuses zero and a non-divisor; ToSeconds divides exactly where
    /// the quotient is dyadic.</summary>
    /// <param name="deep">Whether to draw sixteen times as many decimals and walk PerRate over every divisor of
    /// 50400.</param>
    public static string? DecimalDurationsAndRatePeriods(bool deep) {
        (decimal Seconds, ulong Rate, ulong Expected)[] ladder = [
            (0.1m, 30UL, 3UL), (0.1m, 50400UL, 5040UL), (1m, 50400UL, 50400UL), (0m, 240UL, 0UL), (-1m, 240UL, 0UL),
            (0.0000001m, 240UL, 1UL), (1m / 3m, 3UL, 1UL), (2.5m, 2UL, 5UL), (2.5m, 0UL, 0UL), (79228162514264337593543950335m, 1UL, 0UL),
        ];

        foreach (var (seconds, rate, expected) in ladder) {
            if ((seconds * rate) > ulong.MaxValue) {
                try {
                    _ = FixedTickConversion.DurationTicks(
                        ratePerSecond: rate,
                        seconds: seconds
                    );

                    return $"DurationTicks({seconds}, {rate}) did not overflow";
                } catch (OverflowException) {
                }

                continue;
            }

            var actual = FixedTickConversion.DurationTicks(
                ratePerSecond: rate,
                seconds: seconds
            );

            if (actual != expected) { return $"DurationTicks({seconds}, {rate}) = {actual}, expected {expected}"; }
        }

        var generator = Pcg32XshRr.Create(
            state: 0x5EC0DD5UL,
            stream: 21UL
        );

        var draws = (deep
            ? 8192
            : 512);

        for (var draw = 0; (draw < draws); ++draw) {
            var unscaled = (((long)generator.NextUInt32()) << 16) | (generator.NextUInt32() >> 16);
            var scale = ((byte)(generator.NextUInt32() % 29U));
            var seconds = new decimal(
                lo: ((int)(unscaled & 0xFFFFFFFFL)),
                mid: ((int)(unscaled >> 32)),
                hi: 0,
                isNegative: ((draw & 7) == 0),
                scale: scale
            );
            var rate = ((draw & 1) == 0)
                ? 50400UL
                : ((ulong)generator.NextUInt32());
            var (numerator, denominator) = Oracles.DecimalRational(value: seconds);
            var expected = ((numerator.Sign <= 0)
                ? BigInteger.Zero
                : Oracles.CeilingQuotient(
                    denominator: denominator,
                    numerator: (numerator * rate)
                ));

            if (expected > ulong.MaxValue) {
                try {
                    _ = FixedTickConversion.DurationTicks(
                        ratePerSecond: rate,
                        seconds: seconds
                    );

                    return $"DurationTicks({seconds}, {rate}) did not overflow";
                } catch (OverflowException) {
                }

                continue;
            }

            var actual = FixedTickConversion.DurationTicks(
                ratePerSecond: rate,
                seconds: seconds
            );

            if (actual != expected) { return $"DurationTicks({seconds}, {rate}) = {actual}, expected {expected}"; }

            if (rate == 50400UL) {
                if (FixedTickConversion.DurationEngineTicks(seconds: seconds) != actual) { return $"DurationEngineTicks({seconds}) is not DurationTicks at 50400"; }
            }
        }

        foreach (var (rate, period) in (ReadOnlySpan<(uint Rate, ulong Period)>)[(240U, 210UL), (60U, 840UL), (24U, 2100UL), (7U, 7200UL), (50400U, 1UL), (1U, 50400UL)]) {
            if (FixedTickConversion.PerRate(ratePerSecond: rate) != period) { return $"PerRate({rate}) is not {period}"; }
        }

        if (deep) {
            for (var rate = 1U; (rate <= 50400U); ++rate) {
                var divides = ((50400U % rate) == 0U);

                try {
                    var period = FixedTickConversion.PerRate(ratePerSecond: rate);

                    if (!divides) { return $"PerRate({rate}) admitted a non-divisor"; }
                    if ((period * rate) != 50400UL) { return $"PerRate({rate}) is not the exact period"; }
                } catch (ArgumentException) when (!divides) {
                }
            }
        }

        try {
            _ = FixedTickConversion.PerRate(ratePerSecond: 0U);

            return "PerRate(0) did not refuse";
        } catch (ArgumentOutOfRangeException exception) when (exception.ParamName == "ratePerSecond") {
        }

        try {
            _ = FixedTickConversion.PerRate(ratePerSecond: 11U);

            return "PerRate(11) did not refuse a non-divisor";
        } catch (ArgumentException exception) when (exception.ParamName == "ratePerSecond") {
        }

        if (FixedTickConversion.ToSeconds(engineTicks: 50400UL) != 1d) { return "ToSeconds(50400) is not one"; }
        if (FixedTickConversion.ToSeconds(engineTicks: 25200UL) != 0.5d) { return "ToSeconds(25200) is not a half"; }
        if (FixedTickConversion.ToSeconds(engineTicks: 0UL) != 0d) { return "ToSeconds(0) is not zero"; }
        if (BitConverter.DoubleToInt64Bits(value: FixedTickConversion.ToSeconds(engineTicks: 7UL)) != BitConverter.DoubleToInt64Bits(value: (7d / 50400d))) { return "ToSeconds(7) is not the double quotient"; }

        return null;
    }
    /// <summary>SecondsOf is the one ties-to-even rounding of ticks·2^16/rate, refuses a zero rate and overflows past
    /// the carrier; at the engine rate a whole second is exactly one.</summary>
    /// <param name="deep">Whether to also take the tick count against every divisor of 50400 as the rate.</param>
    /// <param name="left">The tick count's raw (read unsigned) in its first lane.</param>
    /// <param name="right">The rate's raw (read unsigned, zero substituted by one) in its first lane.</param>
    public static string? SecondsOfVsOracle(bool deep, long[] left, long[] right) {
        var ticks = unchecked((ulong)left[0]);
        var rate = unchecked((ulong)right[0]);

        if (deep) {
            for (var divisor = 1UL; (divisor <= 50400UL); ++divisor) {
                if ((50400UL % divisor) != 0UL) { continue; }

                var narrowTicks = (ticks % (1UL << 40));
                var expectedNarrow = Oracles.RoundRationalTiesToEven(
                    denominator: divisor,
                    numerator: (((BigInteger)narrowTicks) << 16)
                );

                if (FixedTickConversion.SecondsOf(ratePerSecond: divisor, ticks: narrowTicks).Value != ((long)expectedNarrow)) { return $"SecondsOf({narrowTicks}, {divisor}) is not {expectedNarrow}"; }
            }
        }

        if (rate == 0UL) {
            try {
                _ = FixedTickConversion.SecondsOf(
                    ratePerSecond: 0UL,
                    ticks: ticks
                );

                return "SecondsOf did not refuse a zero rate";
            } catch (ArgumentOutOfRangeException exception) when (exception.ParamName == "ratePerSecond") {
            }

            rate = 1UL;
        }

        var expected = Oracles.RoundRationalTiesToEven(
            denominator: rate,
            numerator: (((BigInteger)ticks) << 16)
        );

        if (expected > long.MaxValue) {
            try {
                _ = FixedTickConversion.SecondsOf(
                    ratePerSecond: rate,
                    ticks: ticks
                );

                return $"SecondsOf({ticks}, {rate}) did not overflow";
            } catch (OverflowException) {
            }

            return null;
        }

        var actual = FixedTickConversion.SecondsOf(
            ratePerSecond: rate,
            ticks: ticks
        ).Value;

        if (actual != ((long)expected)) { return $"SecondsOf({ticks}, {rate}) = {actual}, expected {expected}"; }
        if (FixedTickConversion.SecondsOf(ratePerSecond: 50400UL, ticks: 50400UL) != FixedQ4816.One) { return "a whole second is not one"; }
        if (FixedTickConversion.SecondsOf(ratePerSecond: 50400UL, ticks: 25200UL) != Raw(value: 32768L)) { return "a half second is not a half"; }

        return null;
    }

    // ---- FixedDirectedRounding: the signed quotients and the double seams ----

    /// <summary>FloorQuotient and CeilingQuotient against the BigInteger floor and ceiling, over the swept pair, the
    /// pair widened past the long range, the power-of-two divisors, the zero divisor's refusal and the one quotient
    /// outside Int128.</summary>
    /// <param name="deep">Whether to take the numerator against every signed power of two up to 2^126 as well.</param>
    /// <param name="left">The numerator raw in its first lane.</param>
    /// <param name="right">The denominator raw in its first lane; zero is the refusal probe.</param>
    public static string? SignedQuotientsVsOracle(bool deep, long[] left, long[] right) {
        var numerator = left[0];
        var denominator = right[0];

        if (deep) {
            for (var bit = 0; (bit < 127); ++bit) {
                foreach (var sign in (ReadOnlySpan<int>)[1, -1]) {
                    var d = ((Int128.One << bit) * sign);
                    var n = (((Int128)numerator) << (bit / 2));
                    var expectedFloor = Oracles.FloorQuotient(denominator: ((BigInteger)d), numerator: ((BigInteger)n));
                    var expectedCeiling = Oracles.CeilingQuotient(denominator: ((BigInteger)d), numerator: ((BigInteger)n));

                    if (((BigInteger)FixedDirectedRounding.FloorQuotient(denominator: d, numerator: n)) != expectedFloor) { return $"FloorQuotient({n}, {d}) is not {expectedFloor}"; }
                    if (((BigInteger)FixedDirectedRounding.CeilingQuotient(denominator: d, numerator: n)) != expectedCeiling) { return $"CeilingQuotient({n}, {d}) is not {expectedCeiling}"; }
                }
            }
        }

        if (denominator == 0L) {
            try {
                _ = FixedDirectedRounding.FloorQuotient(
                    denominator: Int128.Zero,
                    numerator: numerator
                );

                return "FloorQuotient did not refuse a zero divisor";
            } catch (DivideByZeroException) {
            }

            denominator = 1L;
        }

        Span<(Int128 Numerator, Int128 Denominator)> pairs = [
            (numerator, denominator),
            ((((Int128)numerator) << 16), denominator),
            ((((Int128)numerator) << 60), (((Int128)denominator) << 3)),
            (numerator, (((Int128)denominator) << 64)),
            ((((Int128)numerator) * denominator), denominator),
            (numerator, 1L), (numerator, -1L), (numerator, 65536L), (numerator, 1024L), (long.MinValue, -1L), (long.MinValue, 65536L),
            (Int128.MaxValue, denominator), (Int128.MinValue, 2L), (Int128.MinValue, denominator),
        ];

        foreach (var (n, d) in pairs) {
            // The one quotient outside the carrier is the refusal probe below.
            if ((n == Int128.MinValue) && (d == Int128.NegativeOne)) { continue; }

            var expectedFloor = Oracles.FloorQuotient(
                denominator: ((BigInteger)d),
                numerator: ((BigInteger)n)
            );
            var expectedCeiling = Oracles.CeilingQuotient(
                denominator: ((BigInteger)d),
                numerator: ((BigInteger)n)
            );
            var floor = FixedDirectedRounding.FloorQuotient(
                denominator: d,
                numerator: n
            );
            var ceiling = FixedDirectedRounding.CeilingQuotient(
                denominator: d,
                numerator: n
            );

            if (((BigInteger)floor) != expectedFloor) { return $"FloorQuotient({n}, {d}) = {floor}, expected {expectedFloor}"; }
            if (((BigInteger)ceiling) != expectedCeiling) { return $"CeilingQuotient({n}, {d}) = {ceiling}, expected {expectedCeiling}"; }
        }

        try {
            _ = FixedDirectedRounding.CeilingQuotient(
                denominator: Int128.NegativeOne,
                numerator: Int128.MinValue
            );

            return "the one quotient outside Int128 did not overflow";
        } catch (OverflowException) {
        }

        return null;
    }
    /// <summary>TryFloorFromDouble and TryCeilingFromDouble answer the exact floor and ceiling of the double's own
    /// rational at a hand-derived ladder (tiny positives, negatives, half-raw ties, the carrier seams), refuse the
    /// non-finite inputs and the values past the carrier, and agree with each other and the raw at every double-exact
    /// raw.</summary>
    /// <param name="left">A raw in its first lane, used where it is exact as a double.</param>
    /// <param name="right">Unused.</param>
    public static string? DoubleEnclosureSeams(long[] left, long[] right) {
        static string? Check(double value) {
            var floorAnswered = FixedDirectedRounding.TryFloorFromDouble(
                result: out var floor,
                value: value
            );
            var ceilingAnswered = FixedDirectedRounding.TryCeilingFromDouble(
                result: out var ceiling,
                value: value
            );

            if (!double.IsFinite(d: value)) {
                return ((floorAnswered || ceilingAnswered || (floor.Value != 0L) || (ceiling.Value != 0L))
                    ? $"a non-finite double {value:R} was not refused"
                    : null);
            }

            var (numerator, exponent) = Oracles.Binary64Rational(value: value);
            var scaledNumerator = (numerator << 16);
            var denominator = (BigInteger.One << exponent);
            var expectedFloor = Oracles.FloorQuotient(
                denominator: denominator,
                numerator: scaledNumerator
            );
            var expectedCeiling = Oracles.CeilingQuotient(
                denominator: denominator,
                numerator: scaledNumerator
            );
            var floorFits = ((expectedFloor >= long.MinValue) && (expectedFloor <= long.MaxValue));
            var ceilingFits = ((expectedCeiling >= long.MinValue) && (expectedCeiling <= long.MaxValue));

            if (floorAnswered != floorFits) { return $"TryFloorFromDouble({value:R}) answered {floorAnswered}"; }
            if (ceilingAnswered != ceilingFits) { return $"TryCeilingFromDouble({value:R}) answered {ceilingAnswered}"; }
            if (floorFits && (floor.Value != ((long)expectedFloor))) { return $"TryFloorFromDouble({value:R}) = {floor.Value}, expected {expectedFloor}"; }
            if (ceilingFits && (ceiling.Value != ((long)expectedCeiling))) { return $"TryCeilingFromDouble({value:R}) = {ceiling.Value}, expected {expectedCeiling}"; }
            if (!floorFits && (floor.Value != 0L)) { return $"TryFloorFromDouble({value:R}) refused but left {floor.Value}"; }
            if (!ceilingFits && (ceiling.Value != 0L)) { return $"TryCeilingFromDouble({value:R}) refused but left {ceiling.Value}"; }

            return null;
        }

        foreach (var value in (ReadOnlySpan<double>)[
            double.NaN, double.PositiveInfinity, double.NegativeInfinity, 1e300d, -1e300d,
            0d, -0d, 1d, -1d, 0.5d, (1d / 65536d), (1d / 131072d), (-1d / 131072d), (3d / 131072d), 1e-30d, -1e-30d, 5e-324d, -5e-324d,
            0.1d, -0.1d, 140737488355327.99998d, 140737488355328d, -140737488355328d, -140737488355328.00002d, 1.5d, -1.5d, 2.5d,
        ]) {
            if (Check(value: value) is { } failure) { return failure; }
        }

        var exactRaw = (left[0] >> 11);
        var asDouble = (((double)exactRaw) / 65536d);

        if (!FixedDirectedRounding.TryFloorFromDouble(result: out var exactFloor, value: asDouble) || (exactFloor.Value != exactRaw)) { return $"a double-exact raw {exactRaw} did not floor to itself"; }
        if (!FixedDirectedRounding.TryCeilingFromDouble(result: out var exactCeiling, value: asDouble) || (exactCeiling.Value != exactRaw)) { return $"a double-exact raw {exactRaw} did not ceil to itself"; }

        return null;
    }

    // ---- SecondOrderState lane replacement ----

    /// <summary>WithPosition and WithVelocity shift the Q16 value exactly onto the replaced lane, keep the other
    /// lane's raw bit for bit, refuse a magnitude at or past 2^47 by the parameter's name, and the vector form
    /// applies lane by lane.</summary>
    /// <param name="left">The position and velocity raws in the first two lanes, folded under 2^47.</param>
    /// <param name="right">A replacement raw in its first lane, folded under 2^47.</param>
    public static string? FollowerLaneReplacement(long[] left, long[] right) {
        static long Fold(long raw) => (raw % (1L << 47));

        var state = SecondOrderState.FromRawBits(
            positionRaw: left[0],
            velocityRaw: left[1]
        );
        var replacement = Raw(value: Fold(raw: right[0]));
        var withPosition = state.WithPosition(position: replacement);
        var withVelocity = state.WithVelocity(velocity: replacement);

        if (withPosition.PositionRaw != (replacement.Value << 16)) { return $"WithPosition did not shift {replacement.Value} exactly"; }
        if (withPosition.VelocityRaw != left[1]) { return "WithPosition changed the velocity raw"; }
        if (withVelocity.VelocityRaw != (replacement.Value << 16)) { return $"WithVelocity did not shift {replacement.Value} exactly"; }
        if (withVelocity.PositionRaw != left[0]) { return "WithVelocity changed the position raw"; }
        if (withPosition.Position != replacement) { return "WithPosition's Position does not read back the value"; }

        foreach (var refused in (ReadOnlySpan<long>)[(1L << 47), -((1L << 47) + 1L), long.MaxValue, long.MinValue]) {
            try {
                _ = state.WithPosition(position: Raw(value: refused));

                return $"WithPosition admitted raw {refused}";
            } catch (ArgumentOutOfRangeException exception) when (exception.ParamName == "position") {
            }

            try {
                _ = state.WithVelocity(velocity: Raw(value: refused));

                return $"WithVelocity admitted raw {refused}";
            } catch (ArgumentOutOfRangeException exception) when (exception.ParamName == "velocity") {
            }
        }

        var lanes = new SecondOrderState3(
            X: state,
            Y: SecondOrderState.FromRawBits(
                positionRaw: left[1],
                velocityRaw: left[0]
            ),
            Z: SecondOrderState.FromRawBits(
                positionRaw: Fold(raw: left[2]),
                velocityRaw: Fold(raw: right[1])
            )
        );
        var vector = new FixedVector3(
            X: replacement,
            Y: Raw(value: Fold(raw: right[1])),
            Z: Raw(value: Fold(raw: right[2]))
        );
        var lanesWithPosition = lanes.WithPosition(position: vector);
        var lanesWithVelocity = lanes.WithVelocity(velocity: vector);

        if ((lanesWithPosition.X != lanes.X.WithPosition(position: vector.X)) || (lanesWithPosition.Y != lanes.Y.WithPosition(position: vector.Y)) || (lanesWithPosition.Z != lanes.Z.WithPosition(position: vector.Z))) { return "the vector WithPosition is not lane by lane"; }
        if ((lanesWithVelocity.X != lanes.X.WithVelocity(velocity: vector.X)) || (lanesWithVelocity.Y != lanes.Y.WithVelocity(velocity: vector.Y)) || (lanesWithVelocity.Z != lanes.Z.WithVelocity(velocity: vector.Z))) { return "the vector WithVelocity is not lane by lane"; }

        return null;
    }

    // ---- BinaryIntegerFunctions: the saturating arithmetic and the zigzag fold ----

    /// <summary>The three saturating operations answer the exact BigInteger result clamped to the carrier, at the
    /// swept long pair and at the same pair narrowed to int, short, sbyte, ulong, uint and byte.</summary>
    /// <param name="left">The first operand raw in its first lane.</param>
    /// <param name="right">The second operand raw in its first lane.</param>
    public static string? SaturatingArithmeticVsExact(long[] left, long[] right) {
        if (CheckSaturating<long>(left: left[0], right: right[0]) is { } longFailure) { return longFailure; }
        if (CheckSaturating<int>(left: unchecked((int)left[0]), right: unchecked((int)right[0])) is { } intFailure) { return intFailure; }
        if (CheckSaturating<short>(left: unchecked((short)left[0]), right: unchecked((short)right[0])) is { } shortFailure) { return shortFailure; }
        if (CheckSaturating<sbyte>(left: unchecked((sbyte)left[0]), right: unchecked((sbyte)right[0])) is { } sbyteFailure) { return sbyteFailure; }
        if (CheckSaturating<ulong>(left: unchecked((ulong)left[0]), right: unchecked((ulong)right[0])) is { } ulongFailure) { return ulongFailure; }
        if (CheckSaturating<uint>(left: unchecked((uint)left[0]), right: unchecked((uint)right[0])) is { } uintFailure) { return uintFailure; }
        if (CheckSaturating<byte>(left: unchecked((byte)left[0]), right: unchecked((byte)right[0])) is { } byteFailure) { return byteFailure; }

        return null;
    }
    private static string? CheckSaturating<T>(T left, T right) where T : IBinaryInteger<T>, IMinMaxValue<T> {
        var minimum = BigInteger.CreateChecked(value: T.MinValue);
        var maximum = BigInteger.CreateChecked(value: T.MaxValue);
        var wideLeft = BigInteger.CreateChecked(value: left);
        var wideRight = BigInteger.CreateChecked(value: right);

        BigInteger Clamp(BigInteger exact) => BigInteger.Max(
            left: minimum,
            right: BigInteger.Min(
                left: maximum,
                right: exact
            )
        );

        if (BigInteger.CreateChecked(value: left.AddSaturating(right: right)) != Clamp(exact: (wideLeft + wideRight))) { return $"{typeof(T).Name} AddSaturating({left}, {right}) is not the clamped exact sum"; }
        if (BigInteger.CreateChecked(value: left.SubtractSaturating(right: right)) != Clamp(exact: (wideLeft - wideRight))) { return $"{typeof(T).Name} SubtractSaturating({left}, {right}) is not the clamped exact difference"; }
        if (BigInteger.CreateChecked(value: left.MultiplySaturating(right: right)) != Clamp(exact: (wideLeft * wideRight))) { return $"{typeof(T).Name} MultiplySaturating({left}, {right}) is not the clamped exact product"; }

        return null;
    }
    /// <summary>ZigZagEncode is 2|x| less one for a negative x, read in BigInteger, and ZigZagDecode inverts it, at
    /// the swept raw across the four signed widths and at the signed extremes of each.</summary>
    /// <param name="left">The raw in its first lane.</param>
    /// <param name="right">Unused.</param>
    public static string? ZigZagBijection(long[] left, long[] right) {
        foreach (var raw in (ReadOnlySpan<long>)[left[0], right[0], 0L, -1L, 1L, long.MaxValue, long.MinValue]) {
            if (CheckZigZag<long, ulong>(value: raw) is { } longFailure) { return longFailure; }
            if (CheckZigZag<int, uint>(value: unchecked((int)raw)) is { } intFailure) { return intFailure; }
            if (CheckZigZag<short, ushort>(value: unchecked((short)raw)) is { } shortFailure) { return shortFailure; }
            if (CheckZigZag<sbyte, byte>(value: unchecked((sbyte)raw)) is { } sbyteFailure) { return sbyteFailure; }
        }

        return null;
    }
    private static string? CheckZigZag<TSigned, TUnsigned>(TSigned value)
        where TSigned : IBinaryInteger<TSigned>, ISignedNumber<TSigned>, IMinMaxValue<TSigned>
        where TUnsigned : IBinaryInteger<TUnsigned>, IUnsignedNumber<TUnsigned> {
        var wide = BigInteger.CreateChecked(value: value);
        var expected = ((wide.Sign < 0)
            ? ((-wide << 1) - BigInteger.One)
            : (wide << 1));
        var encoded = value.ZigZagEncode<TSigned, TUnsigned>();

        if (BigInteger.CreateChecked(value: encoded) != expected) { return $"{typeof(TSigned).Name} ZigZagEncode({value}) = {encoded}, expected {expected}"; }
        if (encoded.ZigZagDecode<TSigned, TUnsigned>() != value) { return $"{typeof(TSigned).Name} ZigZagDecode did not invert {value}"; }

        return null;
    }

    // ---- InvertibleBitMix: the 64-bit permutation ----

    /// <summary>The two 64-bit multiplier pairs multiply to one modulo 2^64 and are odd, the shifts are the published
    /// 30, 27 and 31 and vanish as Unmix64's term counts assume, Mix64 is the SplitMix64 finalizer transcribed in
    /// BigInteger at the edge words and the swept raw, and Unmix64 inverts it.</summary>
    /// <param name="deep">Whether to run the round trip over a 2^20-step deterministic spread as well.</param>
    /// <param name="left">A word in its first lane.</param>
    /// <param name="right">A second word in its first lane.</param>
    public static string? BitMix64FinalizerAndInverse(bool deep, long[] left, long[] right) {
        var modulus = (BigInteger.One << 64);
        var firstMultiplier = InvertibleBitMix.FirstMultiplier64;
        var firstInverse = InvertibleBitMix.FirstMultiplier64Inverse;
        var secondMultiplier = InvertibleBitMix.SecondMultiplier64;
        var secondInverse = InvertibleBitMix.SecondMultiplier64Inverse;
        var firstShift = InvertibleBitMix.FirstShift64;
        var middleShift = InvertibleBitMix.MiddleShift64;
        var lastShift = InvertibleBitMix.LastShift64;

        if (((((BigInteger)firstMultiplier) * firstInverse) % modulus) != BigInteger.One) { return "the first 64-bit multiplier pair is not inverse"; }
        if (((((BigInteger)secondMultiplier) * secondInverse) % modulus) != BigInteger.One) { return "the second 64-bit multiplier pair is not inverse"; }
        if (((firstMultiplier & 1UL) == 0UL) || ((secondMultiplier & 1UL) == 0UL)) { return "a 64-bit multiplier is even"; }
        if ((firstMultiplier != 0xBF58476D1CE4E5B9UL) || (secondMultiplier != 0x94D049BB133111EBUL)) { return "the multipliers are not SplitMix64's"; }
        if ((firstShift != 30) || (middleShift != 27) || (lastShift != 31)) { return "the shifts are not SplitMix64's 30, 27 and 31"; }
        if (((3 * firstShift) < 64) || ((3 * middleShift) < 64) || ((3 * lastShift) < 64)) { return "a tripled shift stays inside the word, so a two-term inversion is short"; }
        if (((2 * firstShift) >= 64) || ((2 * middleShift) >= 64) || ((2 * lastShift) >= 64)) { return "a doubled shift already vanishes, so the inversion's second term is dead"; }

        foreach (var word in (ReadOnlySpan<ulong>)[
            unchecked((ulong)left[0]), unchecked((ulong)right[0]),
            0UL, 1UL, ulong.MaxValue, (ulong.MaxValue - 1UL), 0x8000000000000000UL, 0x7FFFFFFFFFFFFFFFUL, 0x9E3779B97F4A7C15UL,
        ]) {
            var mixed = InvertibleBitMix.Mix64(value: word);

            if (mixed != Oracles.SplitMix64Finalizer(value: word)) { return $"Mix64(0x{word:X16}) is not the SplitMix64 finalizer"; }
            if (InvertibleBitMix.Unmix64(value: mixed) != word) { return $"Unmix64 did not invert 0x{word:X16}"; }
            if (InvertibleBitMix.Mix64(value: InvertibleBitMix.Unmix64(value: word)) != word) { return $"Mix64 did not invert Unmix64 at 0x{word:X16}"; }
        }

        // The published first output of SplitMix64 seeded with zero is the finalizer of the first state 0x9E3779B97F4A7C15.
        if (InvertibleBitMix.Mix64(value: 0x9E3779B97F4A7C15UL) != 0xE220A8397B1DCDAFUL) { return "Mix64 disagrees with the published SplitMix64 first output"; }

        if (deep) {
            for (var step = 0UL; (step < (1UL << 20)); ++step) {
                var word = unchecked((step * 0x9E3779B97F4A7C15UL) + 0x2545F4914F6CDD1DUL);

                if (InvertibleBitMix.Unmix64(value: InvertibleBitMix.Mix64(value: word)) != word) { return $"Unmix64 did not invert 0x{word:X16}"; }
            }
        }

        return null;
    }

    // ---- WeightedSampler.SampleOnce ----

    /// <summary>SampleOnce picks the index an AliasTable built from the same weights picks from the same generator
    /// state, consuming exactly two advances, across the stack-storage and pooled-storage column counts, the
    /// zero-weight and single-entry shapes; it refuses an empty span and all-zero weights by the entries name.</summary>
    /// <param name="deep">Whether to draw sixty-four times as often and add a 2^14-entry set.</param>
    public static string? SampleOnceMatchesAliasTable(bool deep) {
        List<ulong[]> weightSets = [
            [1UL], [5UL, 0UL], [1UL, 1UL, 1UL], [0UL, 0UL, 7UL], [3UL, 1UL, 4UL, 1UL, 5UL, 9UL, 2UL, 6UL],
            Enumerable.Range(start: 1, count: 63).Select(selector: i => ((ulong)(i * i))).ToArray(),
            Enumerable.Range(start: 1, count: 64).Select(selector: i => ((ulong)i)).ToArray(),
            Enumerable.Range(start: 1, count: 65).Select(selector: i => ((ulong)(i % 7) + 1UL)).ToArray(),
            Enumerable.Range(start: 0, count: 1000).Select(selector: i => (((i % 3) == 0) ? 0UL : ((ulong)(i + 1)))).ToArray(),
            [ulong.MaxValue, ulong.MaxValue, 1UL],
        ];

        if (deep) {
            weightSets.Add(item: Enumerable.Range(start: 0, count: (1 << 14)).Select(selector: i => ((ulong)((i * 2654435761L) % 1000003L))).ToArray());
        }

        var drawsPerSet = (deep
            ? 4096
            : 64);

        foreach (var weights in weightSets) {
            var entries = weights.Select(selector: (weight, index) => (index, weight)).ToArray();
            var table = WeightedSampler.Create(entries: entries);
            var generator = Pcg32XshRr.Create(
                state: ((ulong)weights.Length),
                stream: 3UL
            );

            for (var draw = 0; (draw < drawsPerSet); ++draw) {
                var tableGenerator = generator;
                var onceGenerator = generator;
                var expected = table.SampleIndex(generator: ref tableGenerator);
                var actual = WeightedSampler.SampleOnce(
                    generator: ref onceGenerator,
                    weights: weights
                );

                if (actual != expected) { return $"SampleOnce over {weights.Length} weights picked {actual} where the table picked {expected} at draw {draw}"; }
                if (weights[actual] == 0UL) { return $"SampleOnce picked the zero-weight entry {actual}"; }
                if (onceGenerator.State != tableGenerator.State) { return $"SampleOnce left the generator in a different state than the table at draw {draw}"; }

                generator.Advance(count: 2UL);

                if (onceGenerator.State != generator.State) { return $"SampleOnce did not consume exactly two advances at draw {draw}"; }
            }
        }

        var probe = Pcg32XshRr.Create(
            state: 1UL,
            stream: 1UL
        );

        try {
            _ = WeightedSampler.SampleOnce(
                generator: ref probe,
                weights: ReadOnlySpan<ulong>.Empty
            );

            return "an empty span was not refused";
        } catch (ArgumentException exception) when (exception.ParamName == "entries") {
        }

        try {
            _ = WeightedSampler.SampleOnce(
                generator: ref probe,
                weights: [0UL, 0UL]
            );

            return "all-zero weights were not refused";
        } catch (ArgumentException exception) when (exception.ParamName == "entries") {
        }

        return null;
    }

    // ---- Pcg3dLatticeNoise.ScatterPoint and the published constants ----

    /// <summary>ScatterPoint equals the block formula over the BigInteger PCG3D reference at the swept block and
    /// parameters, the hash constants are Knuth's LCG pair and the published decorrelation words, and the noise seed
    /// streams are the GPU's.</summary>
    /// <param name="deep">Whether to walk every spacing up to 64 against every radius up to 32 as well.</param>
    /// <param name="left">The block indices in the first two lanes and the seed in the third.</param>
    /// <param name="right">The spacing and radius in the first two lanes.</param>
    public static string? ScatterPointAndHashConstants(bool deep, long[] left, long[] right) {
        var blockX = unchecked((int)left[0]);
        var blockZ = unchecked((int)left[1]);
        var seed = unchecked((uint)left[2]);
        var spacing = ((int)(unchecked((uint)right[0]) % 4096U));
        var radius = ((int)(unchecked((uint)right[1]) % 2048U));
        List<(int Spacing, int Radius)> parameters = [(spacing, radius), (2, 1), (8, 1), (8, 4), (8, 5), (1, 0), (0, 0), (100, 3)];

        if (deep) {
            for (var s = 0; (s <= 64); ++s) {
                for (var r = 0; (r <= 32); ++r) {
                    parameters.Add(item: (s, r));
                }
            }
        }

        foreach (var (s, r) in parameters) {
            foreach (var (x, z) in (ReadOnlySpan<(int, int)>)[(blockX, blockZ), (0, 0), (-1, -1), (int.MaxValue, int.MinValue), (7, -3)]) {
                var expected = Oracles.ScatterPointReference(
                    blockX: x,
                    blockZ: z,
                    radius: r,
                    seed: seed,
                    spacing: s
                );
                var actual = Pcg3dLatticeNoise.ScatterPoint(
                    blockX: x,
                    blockZ: z,
                    radius: r,
                    seed: seed,
                    spacing: s
                );

                // The subject's int arithmetic wraps where the reference does not; compare modulo 2^32.
                if (Oracles.WrapToUInt32(value: expected.X) != unchecked((uint)actual.X)) { return $"ScatterPoint({x}, {z}, {seed}, {s}, {r}).X = {actual.X}, expected {expected.X}"; }
                if (Oracles.WrapToUInt32(value: expected.Z) != unchecked((uint)actual.Z)) { return $"ScatterPoint({x}, {z}, {seed}, {s}, {r}).Z = {actual.Z}, expected {expected.Z}"; }
            }
        }

        var multiplier = Pcg3dLatticeNoise.PcgMultiplier;
        var increment = Pcg3dLatticeNoise.PcgIncrement;
        var streamA = Pcg3dLatticeNoise.HashStreamA;
        var streamB = Pcg3dLatticeNoise.HashStreamB;
        var tumble = Pcg3dLatticeNoise.HashTumble;
        var seedY = Pcg3dLatticeNoise.NoiseSeedStreamY;
        var seedZ = Pcg3dLatticeNoise.NoiseSeedStreamZ;

        if ((multiplier != 1664525U) || (increment != 1013904223U)) { return "the PCG constants are not Knuth's 1664525 and 1013904223"; }
        if ((streamA != 0x9E3779B9U) || (streamB != 0x85EBCA6BU) || (tumble != 0x27D4EB2FU)) { return "the hash stream constants are not the published words"; }
        if ((seedY != 0x9E3779B9U) || (seedZ != 0x85EBCA77U)) { return "the noise seed streams are not the GPU's"; }

        return null;
    }

    // ---- LowDiscrepancy's 32-bit R2 increments ----

    /// <summary>R2AlphaXQ32 and R2AlphaYQ32 are the two plastic Q64 increments rounded to 32 bits — the published
    /// 32-bit R2 alphas — and the R2 recurrence at 32 bits, a wrapping multiply by them, agrees with the Q64
    /// sequence's top bits to within one unit.</summary>
    public static string? R2AlphasAreTheRoundedIncrements() {
        var alphaX = LowDiscrepancy.R2AlphaXQ32;
        var alphaY = LowDiscrepancy.R2AlphaYQ32;
        var plastic = ((BigInteger)13925035116211876495UL);
        var plasticSquared = ((BigInteger)10511698010929265437UL);

        if (((BigInteger)alphaX) != Oracles.RoundToEvenUnits(magnitude: plastic, shift: 32)) { return $"R2AlphaXQ32 is {alphaX}, not the rounded Q64 increment"; }
        if (((BigInteger)alphaY) != Oracles.RoundToEvenUnits(magnitude: plasticSquared, shift: 32)) { return $"R2AlphaYQ32 is {alphaY}, not the rounded Q64 increment"; }
        if ((alphaX != 3242174889U) || (alphaY != 2447445414U)) { return "the alphas are not the published 32-bit R2 increments"; }

        for (var index = 1UL; (index < 4096UL); ++index) {
            var point = LowDiscrepancy.R2(index: index);
            var narrowX = unchecked((uint)(index * alphaX));
            var narrowY = unchecked((uint)(index * alphaY));

            if (Math.Abs(value: (((long)narrowX) - point.X.Value)) > ((long)index)) { return $"the 32-bit X recurrence drifts from the Q64 sequence at index {index}"; }
            if (Math.Abs(value: (((long)narrowY) - point.Y.Value)) > ((long)index)) { return $"the 32-bit Y recurrence drifts from the Q64 sequence at index {index}"; }
        }

        return null;
    }

    // ---- RationalRateAccumulator ----

    /// <summary>The accumulator replayed against an exact BigInteger ledger over a schedule of weights and periods:
    /// the units due and the retained phase at every step, the ledger identity at every prefix, the phase's band,
    /// QuietSteps and Skip, Reset, the atomic overflow and the constructor's refusal.</summary>
    /// <param name="left">A weight base in its first lane and a period base in its second.</param>
    /// <param name="right">A starting phase in its first lane.</param>
    public static string? RationalRateAccumulatorVsLedger(long[] left, long[] right) {
        var period = (Math.Abs(value: (left[1] % (1L << 40))) + 1L);
        var weight = Math.Abs(value: (left[0] % (period * 4L)));
        var initialPhase = Math.Abs(value: (right[0] % period));
        var accumulator = new RationalRateAccumulator(phase: initialPhase);
        var ledgerPhase = ((BigInteger)initialPhase);
        var totalWeight = BigInteger.Zero;
        var totalDue = BigInteger.Zero;

        if (accumulator.Phase != initialPhase) { return "the constructor did not keep its phase"; }

        for (var step = 0; (step < 48); ++step) {
            var stepWeight = (((step % 5) == 4)
                ? 0L
                : ((weight * ((step % 3) + 1L)) % (1L << 50)));
            var due = accumulator.Advance(
                period: period,
                weight: stepWeight
            );

            ledgerPhase += stepWeight;
            totalWeight += stepWeight;

            var expectedDue = ((ledgerPhase >= period)
                ? (ledgerPhase / period)
                : BigInteger.Zero);

            ledgerPhase -= (expectedDue * period);
            totalDue += expectedDue;

            if (((BigInteger)due) != expectedDue) { return $"step {step} brought {due} units due, expected {expectedDue}"; }
            if (((BigInteger)accumulator.Phase) != ledgerPhase) { return $"step {step} left phase {accumulator.Phase}, expected {ledgerPhase}"; }
            if ((accumulator.Phase < 0L) || (accumulator.Phase >= period)) { return $"step {step} left the phase outside [0, period)"; }
            if (((totalDue * period) + ledgerPhase) != (totalWeight + initialPhase)) { return $"the ledger identity failed at step {step}"; }
        }

        var quietWeight = (weight + 1L);
        var quiet = accumulator.QuietSteps(
            period: period,
            weight: quietWeight
        );
        var expectedQuiet = Oracles.FloorQuotient(
            denominator: quietWeight,
            numerator: ((((BigInteger)period) - BigInteger.One) - accumulator.Phase)
        );

        if (((BigInteger)quiet) != expectedQuiet) { return $"QuietSteps answered {quiet}, expected {expectedQuiet}"; }

        var before = accumulator.Phase;

        accumulator.Skip(weight: (quiet * quietWeight));

        if (accumulator.Phase != (before + (quiet * quietWeight))) { return "Skip did not add the weight"; }
        if (accumulator.Phase >= period) { return "the quiet steps overran the period"; }
        var expectedAfterQuiet = ((((BigInteger)accumulator.Phase) + quietWeight) / period);

        if (expectedAfterQuiet.IsZero) { return "the step after the quiet steps is still quiet"; }
        if (((BigInteger)accumulator.Advance(period: period, weight: quietWeight)) != expectedAfterQuiet) { return "the step after the quiet steps did not bring the ledger's units due"; }

        accumulator.Reset();

        if (accumulator.Phase != 0L) { return "Reset did not zero the phase"; }

        var full = new RationalRateAccumulator(phase: (long.MaxValue - 1L));

        try {
            _ = full.Advance(
                period: period,
                weight: 2L
            );

            return "an overflowing step did not throw";
        } catch (OverflowException) {
        }

        if (full.Phase != (long.MaxValue - 1L)) { return "an overflowing step changed the phase"; }

        try {
            _ = new RationalRateAccumulator(phase: -1L);

            return "a negative phase was admitted";
        } catch (ArgumentOutOfRangeException exception) when (exception.ParamName == "phase") {
        }

        return null;
    }

    // ---- Rational.FromDecimal and Rational.FromDouble ----

    /// <summary>FromDecimal and FromDouble equal the exact rationals read off the decimal's bits and the IEEE 754
    /// fields in BigInteger, at the swept raw as a double-exact value, at a hand-derived ladder of both, and refuse
    /// the non-finite doubles.</summary>
    /// <param name="left">A raw in its first lane, read as a double-exact value.</param>
    /// <param name="right">A raw in its first lane, read as a decimal mantissa.</param>
    public static string? RationalDoorsExact(long[] left, long[] right) {
        var exactRaw = (left[0] >> 11);
        var asDouble = (((double)exactRaw) / 65536d);

        foreach (var value in (ReadOnlySpan<double>)[asDouble, 0d, -0d, 1d, -1d, 0.1d, 0.5d, 1e300d, 5e-324d, -2.5e-310d, 3.0000000000000004d, 123456789.125d]) {
            var (numerator, exponent) = Oracles.Binary64Rational(value: value);
            var expected = new Rational(
                Numerator: numerator,
                Denominator: (BigInteger.One << exponent)
            );

            if (Rational.FromDouble(value: value) != expected) { return $"FromDouble({value:R}) is not the exact rational {expected}"; }
        }

        foreach (var value in (ReadOnlySpan<double>)[double.NaN, double.PositiveInfinity, double.NegativeInfinity]) {
            try {
                _ = Rational.FromDouble(value: value);

                return $"FromDouble({value}) was not refused";
            } catch (ArgumentOutOfRangeException exception) when (exception.ParamName == "value") {
            }
        }

        var mantissa = right[0];

        foreach (var value in (ReadOnlySpan<decimal>)[
            new decimal(lo: ((int)(mantissa & 0xFFFFFFFFL)), mid: ((int)((mantissa >> 32) & 0x7FFFFFFFL)), hi: 0, isNegative: (mantissa < 0L), scale: ((byte)(unchecked((ulong)mantissa) % 29UL))),
            0m, 1m, -1m, 0.1m, 0.5m, 1.000m, -0.000m, 79228162514264337593543950335m, -79228162514264337593543950335m, 0.0000000000000000000000000001m, 3.14159m,
        ]) {
            var (numerator, denominator) = Oracles.DecimalRational(value: value);
            var expected = new Rational(
                Numerator: numerator,
                Denominator: denominator
            );

            if (Rational.FromDecimal(value: value) != expected) { return $"FromDecimal({value}) is not the exact rational {expected}"; }
        }

        if (Rational.FromDecimal(value: 0.1m) != new Rational(Numerator: BigInteger.One, Denominator: 10)) { return "FromDecimal(0.1) is not one tenth"; }
        if (Rational.FromDouble(value: 0.1d) == new Rational(Numerator: BigInteger.One, Denominator: 10)) { return "FromDouble(0.1) claims to be one tenth, which no double is"; }

        return null;
    }
}
