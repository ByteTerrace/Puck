using Puck.Maths;

namespace Puck.Physics.Tests;

/// <summary>
/// <see cref="FixedGyroscopicStep"/> in free flight. A body spun about an axis that is not principal is advanced the
/// way the rigid integrator advances it: the gyroscopic step on the world angular velocity, then the orientation
/// rotated by <c>exp(ω·h/2)</c> and renormalized, 300 steps at 60 Hz. Its world angular momentum <c>R·I_b·Rᵀ·ω</c>,
/// whose true value is constant, and its kinetic energy are measured in double precision. Three runs are compared:
/// the fixed-point integrator with the step, the same scheme in double precision (an independent reference with its
/// own quaternion and Cramer arithmetic), and the integrator without the step, which holds world angular velocity
/// constant. A running World cannot show this: conservation is a property of the integrator over many steps.
/// </summary>
/// <remarks>The implicit Euler step's own truncation is first order and dissipative, so momentum is not conserved
/// to the fixed-point resolution at 60 Hz; it is conserved to the scheme's truncation, and the fixed-point run
/// tracks the exact-arithmetic scheme to rounding. The laws below pin those two facts separately.</remarks>
public sealed class GyroscopicStepLawTests {
    private const int InverseInertiaFractionBits = 40;
    private const int Steps = 300;
    private const double StepSeconds = (1d / 60d);

    public static TheoryData<string, double, double, double> Bodies => new() {
        // An oblate disc (a Go stone or a reversi disc): the symmetry-axis moment twice the transverse ones.
        { "oblate disc", 1d, 2d, 1d },
        // A triaxial block, slender along Y (a chess piece's proportions).
        { "triaxial block", 1d, 0.4d, 1.3d },
    };

    [Theory]
    [MemberData(nameof(Bodies))]
    public void FreeFlightTracksTheExactSchemeAndNeverGainsEnergyOrMomentum(string body, double ixx, double iyy, double izz) {
        var fixedRun = FlyFixed(ixx: ixx, iyy: iyy, izz: izz, applyGyroscopicStep: true);
        var held = FlyFixed(ixx: ixx, iyy: iyy, izz: izz, applyGyroscopicStep: false);
        var exact = FlyExact(ixx: ixx, iyy: iyy, izz: izz);
        var summary = $"{body}: fixed {fixedRun.MaximumMomentumError:P3}, exact scheme {exact.MaximumMomentumError:P3}, held {held.MaximumMomentumError:P3}";

        Assert.True(fixedRun.Refusals == 0, $"{summary}; {fixedRun.Refusals} steps refused");
        // The fixed-point run is the exact scheme to rounding: its final momentum lies within 0.1% of the
        // double-precision scheme's.
        Assert.True(
            (Distance(fixedRun.FinalMomentum, exact.FinalMomentum) / Length(exact.FinalMomentum)) <= 1e-3d,
            $"{summary}; final momentum {fixedRun.FinalMomentum} against the exact scheme's {exact.FinalMomentum}"
        );
        // The step corrects the precession that constant world angular velocity produces, by a wide margin.
        Assert.True((4d * fixedRun.MaximumMomentumError) <= held.MaximumMomentumError, summary);
        // Implicit Euler on the gyroscopic term is dissipative: neither |L| nor the kinetic energy rises in a step by
        // more than the step's rounding can add.
        Assert.True(
            fixedRun.MaximumMomentumRise <= fixedRun.RoundingSlack,
            $"{summary}; |L| rose by {fixedRun.MaximumMomentumRise:E3} in one step (slack {fixedRun.RoundingSlack:E3})"
        );
        Assert.True(
            fixedRun.MaximumEnergyRise <= fixedRun.RoundingSlack,
            $"{summary}; energy rose by {fixedRun.MaximumEnergyRise:E3} in one step (slack {fixedRun.RoundingSlack:E3})"
        );
    }

    [Fact]
    public void SphericalInertiaStillnessAndDegenerateInertiaLeaveVelocityUnchanged() {
        var omega = new FixedVector3(X: FixedQ4816.FromDouble(value: 1.5d), Y: FixedQ4816.FromDouble(value: -2d), Z: FixedQ4816.FromDouble(value: 0.25d));
        var orientation = FixedQuaternion.Exp(bivector: new FixedVector3(X: FixedQ4816.FromDouble(value: 0.2d), Y: FixedQ4816.Zero, Z: FixedQ4816.FromDouble(value: 0.1d))).Normalize();
        var h = FixedQ4816.FromDouble(value: StepSeconds);
        const long One = (1L << InverseInertiaFractionBits);

        Assert.Equal(GyroscopicStepOutcome.Unneeded, FixedGyroscopicStep.Advance(angularVelocity: omega, orientation: orientation, inverseInertiaXX: One, inverseInertiaYY: One, inverseInertiaZZ: One, stepSeconds: h, advanced: out var sphere));
        Assert.Equal(omega, sphere);
        Assert.Equal(GyroscopicStepOutcome.Refused, FixedGyroscopicStep.Advance(angularVelocity: omega, orientation: orientation, inverseInertiaXX: 0L, inverseInertiaYY: One, inverseInertiaZZ: One, stepSeconds: h, advanced: out var degenerate));
        Assert.Equal(omega, degenerate);
        Assert.Equal(GyroscopicStepOutcome.Unneeded, FixedGyroscopicStep.Advance(angularVelocity: FixedVector3.Zero, orientation: orientation, inverseInertiaXX: One, inverseInertiaYY: (2L * One), inverseInertiaZZ: (3L * One), stepSeconds: h, advanced: out var still));
        Assert.Equal(FixedVector3.Zero, still);
    }

    private readonly record struct Flight(
        double MaximumMomentumError,
        double MaximumMomentumRise,
        double MaximumEnergyRise,
        double RoundingSlack,
        (double X, double Y, double Z) FinalMomentum,
        int Refusals
    );

    private static readonly (double X, double Y, double Z) InitialOmega = (1d, 4d, 0.5d);
    private static readonly (double X, double Y, double Z) InitialAttitude = (0.15d, 0d, 0.1d);

    private static Flight FlyFixed(double ixx, double iyy, double izz, bool applyGyroscopicStep) {
        static long Inverse(double moment) => ((long)Math.Round((((double)(1L << InverseInertiaFractionBits)) / moment)));
        static FixedVector3 ToFixed((double X, double Y, double Z) v) => new(X: FixedQ4816.FromDouble(value: v.X), Y: FixedQ4816.FromDouble(value: v.Y), Z: FixedQ4816.FromDouble(value: v.Z));
        static (double X, double Y, double Z, double W) ToDouble(FixedQuaternion q) => (((double)q.X), ((double)q.Y), ((double)q.Z), ((double)q.W));
        static (double X, double Y, double Z) ToDoubleVector(FixedVector3 v) => (((double)v.X), ((double)v.Y), ((double)v.Z));

        var (a, b, c) = (Inverse(moment: ixx), Inverse(moment: iyy), Inverse(moment: izz));
        var h = FixedQ4816.FromDouble(value: StepSeconds);
        var half = FixedQ4816.FromDouble(value: 0.5d);
        var omega = ToFixed(v: InitialOmega);
        var orientation = FixedQuaternion.Exp(bivector: ToFixed(v: InitialAttitude)).Normalize();
        var tracker = new Tracker(ixx: ixx, iyy: iyy, izz: izz, orientation: ToDouble(q: orientation), omega: ToDoubleVector(v: omega));
        var refusals = 0;

        for (var step = 0; (step < Steps); ++step) {
            if (applyGyroscopicStep) {
                if (FixedGyroscopicStep.Advance(angularVelocity: omega, orientation: orientation, inverseInertiaXX: a, inverseInertiaYY: b, inverseInertiaZZ: c, stepSeconds: h, advanced: out var advanced) == GyroscopicStepOutcome.Refused) { ++refusals; }

                omega = advanced;
            }

            orientation = (FixedQuaternion.Exp(bivector: (omega * (h * half))) * orientation).Normalize();
            tracker.Observe(orientation: ToDouble(q: orientation), omega: ToDoubleVector(v: omega));
        }

        // One step rounds each ω component to 2^-16 rad/s and each attitude component to 2^-16; the momentum or energy
        // that rounding can add is a small multiple of the largest moment times |ω| at that resolution.
        var slack = (16d * Math.Max(ixx, Math.Max(iyy, izz)) * Length(InitialOmega) * Math.Pow(2d, -16d));

        return tracker.Finish(roundingSlack: slack, refusals: refusals);
    }
    private static Flight FlyExact(double ixx, double iyy, double izz) {
        var omega = InitialOmega;
        var orientation = Exp(v: InitialAttitude);
        var tracker = new Tracker(ixx: ixx, iyy: iyy, izz: izz, orientation: orientation, omega: omega);
        var (dx, dy, dz) = ((izz - iyy), (ixx - izz), (iyy - ixx));

        for (var step = 0; (step < Steps); ++step) {
            var w = Rotate(q: Conjugate(q: orientation), v: omega);
            var f = Scale(Cross(w, (ixx * w.X, iyy * w.Y, izz * w.Z)), StepSeconds);
            var h = StepSeconds;
            var correction = Cramer(
                m: [ixx, h * w.Z * dx, h * w.Y * dx, h * w.Z * dy, iyy, h * w.X * dy, h * w.Y * dz, h * w.X * dz, izz],
                rhs: f
            );

            omega = Subtract(omega, Rotate(q: orientation, v: correction));
            orientation = Normalize(q: Multiply(Exp(v: Scale(omega, (0.5d * h))), orientation));
            tracker.Observe(orientation: orientation, omega: omega);
        }

        return tracker.Finish(roundingSlack: 0d, refusals: 0);
    }

    private sealed class Tracker((double X, double Y, double Z, double W) orientation, (double X, double Y, double Z) omega, double ixx, double iyy, double izz) {
        private readonly (double X, double Y, double Z) m_initial = Momentum(orientation, omega, ixx, iyy, izz);
        private double m_magnitude = Length(Momentum(orientation, omega, ixx, iyy, izz));
        private double m_energy = Energy(orientation, omega, ixx, iyy, izz);
        private double m_maximumError;
        private double m_maximumMomentumRise = double.NegativeInfinity;
        private double m_maximumEnergyRise = double.NegativeInfinity;
        private (double X, double Y, double Z) m_final = Momentum(orientation, omega, ixx, iyy, izz);

        public void Observe((double X, double Y, double Z, double W) orientation, (double X, double Y, double Z) omega) {
            var momentum = Momentum(orientation, omega, ixx, iyy, izz);
            var magnitude = Length(momentum);
            var energy = Energy(orientation, omega, ixx, iyy, izz);

            m_maximumError = Math.Max(m_maximumError, (Distance(momentum, m_initial) / Length(m_initial)));
            m_maximumMomentumRise = Math.Max(m_maximumMomentumRise, (magnitude - m_magnitude));
            m_maximumEnergyRise = Math.Max(m_maximumEnergyRise, (energy - m_energy));
            m_magnitude = magnitude;
            m_energy = energy;
            m_final = momentum;
        }
        public Flight Finish(double roundingSlack, int refusals) =>
            new(
                FinalMomentum: m_final,
                MaximumEnergyRise: m_maximumEnergyRise,
                MaximumMomentumError: m_maximumError,
                MaximumMomentumRise: m_maximumMomentumRise,
                Refusals: refusals,
                RoundingSlack: roundingSlack
            );
    }

    private static (double X, double Y, double Z) Momentum((double X, double Y, double Z, double W) q, (double X, double Y, double Z) omega, double ixx, double iyy, double izz) {
        var w = Rotate(q: Conjugate(q: q), v: omega);

        return Rotate(q: q, v: ((ixx * w.X), (iyy * w.Y), (izz * w.Z)));
    }
    private static double Energy((double X, double Y, double Z, double W) q, (double X, double Y, double Z) omega, double ixx, double iyy, double izz) {
        var w = Rotate(q: Conjugate(q: q), v: omega);

        return (0.5d * ((((ixx * w.X) * w.X) + ((iyy * w.Y) * w.Y)) + ((izz * w.Z) * w.Z)));
    }
    private static (double X, double Y, double Z) Cross((double X, double Y, double Z) l, (double X, double Y, double Z) r) =>
        (((l.Y * r.Z) - (l.Z * r.Y)), ((l.Z * r.X) - (l.X * r.Z)), ((l.X * r.Y) - (l.Y * r.X)));
    private static (double X, double Y, double Z) Scale((double X, double Y, double Z) v, double s) => ((v.X * s), (v.Y * s), (v.Z * s));
    private static (double X, double Y, double Z) Subtract((double X, double Y, double Z) l, (double X, double Y, double Z) r) => ((l.X - r.X), (l.Y - r.Y), (l.Z - r.Z));
    private static double Length((double X, double Y, double Z) v) => Math.Sqrt((((v.X * v.X) + (v.Y * v.Y)) + (v.Z * v.Z)));
    private static double Distance((double X, double Y, double Z) l, (double X, double Y, double Z) r) => Length(Subtract(l, r));
    private static (double X, double Y, double Z, double W) Conjugate((double X, double Y, double Z, double W) q) => (-q.X, -q.Y, -q.Z, q.W);
    private static (double X, double Y, double Z, double W) Normalize((double X, double Y, double Z, double W) q) {
        var n = Math.Sqrt(((((q.X * q.X) + (q.Y * q.Y)) + (q.Z * q.Z)) + (q.W * q.W)));

        return ((q.X / n), (q.Y / n), (q.Z / n), (q.W / n));
    }
    private static (double X, double Y, double Z, double W) Multiply((double X, double Y, double Z, double W) a, (double X, double Y, double Z, double W) b) => (
        ((((a.W * b.X) + (a.X * b.W)) + (a.Y * b.Z)) - (a.Z * b.Y)),
        ((((a.W * b.Y) - (a.X * b.Z)) + (a.Y * b.W)) + (a.Z * b.X)),
        ((((a.W * b.Z) + (a.X * b.Y)) - (a.Y * b.X)) + (a.Z * b.W)),
        ((((a.W * b.W) - (a.X * b.X)) - (a.Y * b.Y)) - (a.Z * b.Z))
    );
    private static (double X, double Y, double Z, double W) Exp((double X, double Y, double Z) v) {
        var angle = Length(v);

        if (angle == 0d) { return (0d, 0d, 0d, 1d); }

        var s = (Math.Sin(angle) / angle);

        return ((v.X * s), (v.Y * s), (v.Z * s), Math.Cos(angle));
    }
    // Rotates v by the unit quaternion q (normalized here): v + w·t + u × t with t = 2·u × v.
    private static (double X, double Y, double Z) Rotate((double X, double Y, double Z, double W) q, (double X, double Y, double Z) v) {
        q = Normalize(q: q);

        var t = Scale(Cross((q.X, q.Y, q.Z), v), 2d);
        var u = Cross((q.X, q.Y, q.Z), t);

        return (((v.X + (q.W * t.X)) + u.X), ((v.Y + (q.W * t.Y)) + u.Y), ((v.Z + (q.W * t.Z)) + u.Z));
    }
    private static (double X, double Y, double Z) Cramer(double[] m, (double X, double Y, double Z) rhs) {
        static double Determinant(double[] e) =>
            (((e[0] * ((e[4] * e[8]) - (e[5] * e[7]))) - (e[1] * ((e[3] * e[8]) - (e[5] * e[6])))) + (e[2] * ((e[3] * e[7]) - (e[4] * e[6]))));

        var det = Determinant(e: m);
        double[] r = [rhs.X, rhs.Y, rhs.Z];
        var x = new double[3];

        for (var column = 0; (column < 3); ++column) {
            var replaced = (double[])m.Clone();

            for (var row = 0; (row < 3); ++row) { replaced[((row * 3) + column)] = r[row]; }

            x[column] = (Determinant(e: replaced) / det);
        }

        return (x[0], x[1], x[2]);
    }
}
