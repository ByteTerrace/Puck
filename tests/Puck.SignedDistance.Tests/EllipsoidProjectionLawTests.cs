using Puck.Maths;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// THE LAW: <see cref="FixedEllipsoidProjection"/> answers the distance from a point to an ellipse or an ellipsoid as an
/// interval that holds it. Over ellipses and ellipsoids from a circle to a hundredfold thin, with tied axes and radii past
/// the carrier's solved width, at the centre, on each axis and plane inside and out, at the evolute's cusp, one raw off
/// each plane, beside the surface and far from it, the distance found in double precision by dense sampling and local
/// refinement lies inside the signed interval, the side agrees with it, the interval is a few raws wide where the search
/// ended, and the scalar lies within the interval.
/// </summary>
public sealed class EllipsoidProjectionLawTests {
    private const double Raw = (1.0 / 65536.0);
    // The reference's own error: its sampling and golden-section refinement settle far under a raw.
    private const double ReferenceSlack = 1e-9;
    // The widest interval a finished search leaves, in raws, at radii the carrier solves unscaled.
    private const double FinishedRaws = 8.0;

    [Fact]
    public void AnEllipseIntervalHoldsItsDistance() {
        (double X, double Y)[] ellipses = [(2.0, 1.0), (46.0, 17.0), (0.5, 0.2), (0.2, 0.5), (0.3, 0.3), (1.2, 0.012), (1.0, (1.0 - Math.Pow(2.0, -15.0))), (100000.0, 40000.0)];
        var failures = new List<string>();

        foreach (var (ex, ey) in ellipses) {
            var semiX = Fixed(value: ex);
            var semiY = Fixed(value: ey);
            var (rx, ry) = ((double)semiX, (double)semiY);

            foreach (var (px, py) in Points2(semiX: rx, semiY: ry)) {
                var point = new FixedVector2(X: Fixed(value: px), Y: Fixed(value: py));
                var answer = FixedEllipsoidProjection.Project(point: point, semiX: semiX, semiY: semiY);
                var expected = Reference2(px: (double)point.X, py: (double)point.Y, semiX: rx, semiY: ry);

                Judge(answer: answer, expected: expected, scale: Math.Max(val1: Math.Max(val1: rx, val2: ry), val2: Math.Max(val1: Math.Abs(value: px), val2: Math.Abs(value: py))), failures: failures, where: $"ellipse ({ex}, {ey}) at ({(double)point.X}, {(double)point.Y})");
            }
        }

        Assert.True(condition: (failures.Count == 0), userMessage: string.Join(separator: " | ", values: failures));
    }
    [Fact]
    public void AnEllipsoidIntervalHoldsItsDistance() {
        (double X, double Y, double Z)[] ellipsoids = [(2.0, 1.0, 3.0), (46.0, 17.0, 46.0), (1.0, 1.0, 1.0), (0.5, 0.3, 0.2), (0.4, 0.4, 0.05)];
        var failures = new List<string>();

        foreach (var (ex, ey, ez) in ellipsoids) {
            var radii = new FixedVector3(X: Fixed(value: ex), Y: Fixed(value: ey), Z: Fixed(value: ez));
            var (rx, ry, rz) = ((double)radii.X, (double)radii.Y, (double)radii.Z);

            foreach (var (px, py, pz) in Points3(rx: rx, ry: ry, rz: rz)) {
                var point = new FixedVector3(X: Fixed(value: px), Y: Fixed(value: py), Z: Fixed(value: pz));
                var answer = FixedEllipsoidProjection.Project(point: point, radii: radii);
                var expected = Reference3(px: (double)point.X, py: (double)point.Y, pz: (double)point.Z, rx: rx, ry: ry, rz: rz);

                Judge(answer: answer, expected: expected, scale: Math.Max(val1: rx, val2: Math.Max(val1: ry, val2: rz)), failures: failures, where: $"ellipsoid ({ex}, {ey}, {ez}) at ({(double)point.X}, {(double)point.Y}, {(double)point.Z})");
            }
        }

        Assert.True(condition: (failures.Count == 0), userMessage: string.Join(separator: " | ", values: failures));
    }

    private static void Judge(FixedEllipsoidProjection answer, double expected, double scale, List<string> failures, string where) {
        var signed = answer.Signed;
        var lower = (double)signed.Lower;
        var upper = (double)signed.Upper;
        // A radius past 2^31 raws is solved scaled down by a power of two, its interval widened by that rounding.
        var finished = (FinishedRaws * Raw * Math.Max(val1: 1.0, val2: Math.Pow(2.0, Math.Ceiling(Math.Log2(Math.Max(1.0, (scale * 4.0) / 32768.0))))));

        if (!((expected >= (lower - ReferenceSlack)) && (expected <= (upper + ReferenceSlack)))) {
            failures.Add(item: $"{where}: {expected} outside [{lower}, {upper}]");
        }
        if ((Math.Abs(value: expected) > Raw) && (answer.Side != Math.Sign(value: expected)) && !((answer.Side == 0) && (Math.Abs(value: expected) <= (2.0 * Raw)))) {
            failures.Add(item: $"{where}: side {answer.Side}, expected {Math.Sign(value: expected)}");
        }
        if (!(((double)answer.Error) <= finished)) {
            failures.Add(item: $"{where}: interval {(double)answer.Error} wide");
        }
        if (!((((double)answer.Distance) >= lower) && (((double)answer.Distance) <= upper))) {
            failures.Add(item: $"{where}: scalar {(double)answer.Distance} outside [{lower}, {upper}]");
        }
    }
    private static FixedQ4816 Fixed(double value) => FixedQ4816.FromRawBits(value: (long)Math.Round(a: (value * 65536.0)));

    // The centre; each axis at a quarter, at the evolute's cusp, just inside the vertex, at it and outside; one raw and a
    // millimetre off the major axis inside; a ring beside the curve, inside and far out.
    private static IEnumerable<(double X, double Y)> Points2(double semiX, double semiY) {
        var (major, minor) = ((semiX >= semiY) ? (semiX, semiY) : (semiY, semiX));
        var cusp = (((major * major) - (minor * minor)) / major);
        var alongX = (semiX >= semiY);

        (double X, double Y) Major(double along, double across) => (alongX ? (along, across) : (across, along));

        yield return (0.0, 0.0);
        foreach (var fraction in new[] { 0.25, 0.9, 1.0, 1.1, 3.0 }) {
            yield return ((semiX * fraction), 0.0);
            yield return (0.0, (semiY * fraction));
        }
        yield return Major(along: cusp, across: 0.0);
        yield return Major(along: (0.5 * major), across: Raw);
        yield return Major(along: (0.5 * major), across: 0.001);
        yield return Major(along: (0.999 * cusp), across: Raw);
        for (var step = 0; (step < 16); step++) {
            var angle = (((step * Math.Tau) / 16.0) + 0.13);

            foreach (var scale in new[] { 0.5, 0.97, 1.03, 8.0 }) {
                yield return ((semiX * Math.Cos(d: angle) * scale), (semiY * Math.Sin(a: angle) * scale));
            }
        }
    }
    // The centre, each axis inside and out, the planes, one raw off a plane beside the least axis's pole, and points
    // in general position near and far.
    private static IEnumerable<(double X, double Y, double Z)> Points3(double rx, double ry, double rz) {
        yield return (0.0, 0.0, 0.0);
        yield return (0.5, Raw, 0.0);
        foreach (var fraction in new[] { 0.3, 0.95, 1.2 }) {
            yield return ((rx * fraction), 0.0, 0.0);
            yield return (0.0, (ry * fraction), 0.0);
            yield return (0.0, 0.0, (rz * fraction));
            yield return ((rx * fraction * 0.6), (ry * fraction * 0.6), 0.0);
            yield return (0.0, (ry * fraction * 0.6), (rz * fraction * 0.6));
            yield return ((rx * fraction * 0.6), Raw, (rz * fraction * 0.5));
        }
        for (var step = 0; (step < 10); step++) {
            var theta = ((step * 0.29) + 0.17);
            var phi = ((step * 0.71) + 0.05);

            foreach (var scale in new[] { 0.6, 0.99, 1.02, 5.0 }) {
                yield return ((rx * Math.Sin(a: theta) * Math.Cos(d: phi) * scale), (ry * Math.Cos(d: theta) * scale), (rz * Math.Sin(a: theta) * Math.Sin(a: phi) * scale));
            }
        }
    }

    // The distance to the nearest point of the curve: the best of many samples of its parameter, refined by golden-section
    // search about it, signed by the implicit test.
    private static double Reference2(double px, double py, double semiX, double semiY) {
        const int Samples = 40000;
        var best = 0.0;
        var bestDistance = double.MaxValue;

        for (var sample = 0; (sample < Samples); sample++) {
            var angle = ((sample * Math.Tau) / Samples);
            var distance = Separation(angle: angle);

            if (distance < bestDistance) {
                (best, bestDistance) = (angle, distance);
            }
        }

        var nearest = Golden(low: (best - (Math.Tau / Samples)), high: (best + (Math.Tau / Samples)), measure: Separation);
        var test = (((px * px) / (semiX * semiX)) + ((py * py) / (semiY * semiY)));

        return ((test < 1.0) ? -nearest.Value : nearest.Value);

        double Separation(double angle) => Math.Sqrt(d: (Square(value: ((semiX * Math.Cos(d: angle)) - px)) + Square(value: ((semiY * Math.Sin(a: angle)) - py))));
    }
    // The distance to the nearest point of the surface: the best of a grid over its two angles, refined by alternating
    // golden-section searches along each, signed by the implicit test.
    private static double Reference3(double px, double py, double pz, double rx, double ry, double rz) {
        const int Rows = 300;
        const int Columns = 600;
        var (bestTheta, bestPhi, bestDistance) = (0.0, 0.0, double.MaxValue);

        for (var row = 0; (row <= Rows); row++) {
            var theta = ((row * Math.PI) / Rows);

            for (var column = 0; (column < Columns); column++) {
                var phi = ((column * Math.Tau) / Columns);
                var distance = Separation(theta: theta, phi: phi);

                if (distance < bestDistance) {
                    (bestTheta, bestPhi, bestDistance) = (theta, phi, distance);
                }
            }
        }

        var (thetaStep, phiStep) = ((Math.PI / Rows), (Math.Tau / Columns));

        for (var round = 0; (round < 60); round++) {
            var phi = bestPhi;
            var theta = Golden(low: (bestTheta - thetaStep), high: (bestTheta + thetaStep), measure: t => Separation(theta: t, phi: phi)).At;

            bestTheta = theta;
            bestPhi = Golden(low: (bestPhi - phiStep), high: (bestPhi + phiStep), measure: f => Separation(theta: theta, phi: f)).At;
            (thetaStep, phiStep) = ((thetaStep * 0.7), (phiStep * 0.7));
        }

        var nearest = Math.Min(val1: Separation(theta: bestTheta, phi: bestPhi), val2: Lagrange(px: px, py: py, pz: pz, rx: rx, ry: ry, rz: rz));
        var test = (((px * px) / (rx * rx)) + ((py * py) / (ry * ry)) + ((pz * pz) / (rz * rz)));

        return ((test < 1.0) ? -nearest : nearest);

        double Separation(double theta, double phi) =>
            Math.Sqrt(d: (Square(value: ((rx * Math.Sin(a: theta) * Math.Cos(d: phi)) - px)) + Square(value: ((ry * Math.Cos(d: theta)) - py)) + Square(value: ((rz * Math.Sin(a: theta) * Math.Sin(a: phi)) - pz))));
    }
    // A second witness the grid can miss on a flat shape: the classical foot x_i = r_i² p_i / (t + r_i²) at the root of
    // sum((x_i / r_i)²) = 1, bisected in double over t above -min(r)², then scaled radially onto the surface, so its
    // distance is a surface point's whichever root it found. Infinity where the root has no bracket (a point on a plane
    // through a least axis inside its evolute), which the grid answers.
    private static double Lagrange(double px, double py, double pz, double rx, double ry, double rz) {
        double[] r = [rx, ry, rz];
        double[] p = [Math.Abs(value: px), Math.Abs(value: py), Math.Abs(value: pz)];
        var floor = -Math.Pow(Math.Min(val1: rx, val2: Math.Min(val1: ry, val2: rz)), 2.0);
        var (low, high) = (floor, Math.Sqrt(d: ((Square(value: (rx * p[0])) + Square(value: (ry * p[1]))) + Square(value: (rz * p[2])))));

        double Measure(double t) {
            var sum = 0.0;

            for (var axis = 0; (axis < 3); axis++) { sum += Square(value: ((r[axis] * p[axis]) / (t + (r[axis] * r[axis])))); }
            return (sum - 1.0);
        }

        if (!(Measure(t: (low + (1e-300 + (Math.Abs(value: low) * 1e-15)))) > 0.0)) { return double.PositiveInfinity; }
        for (var step = 0; (step < 400); step++) {
            var middle = ((low + high) / 2.0);

            if ((middle <= low) || (middle >= high)) { break; }
            if (Measure(t: middle) > 0.0) { low = middle; } else { high = middle; }
        }

        var t = ((low + high) / 2.0);
        double[] foot = [((r[0] * r[0] * p[0]) / (t + (r[0] * r[0]))), ((r[1] * r[1] * p[1]) / (t + (r[1] * r[1]))), ((r[2] * r[2] * p[2]) / (t + (r[2] * r[2])))];
        var scale = Math.Sqrt(d: ((Square(value: (foot[0] / r[0])) + Square(value: (foot[1] / r[1]))) + Square(value: (foot[2] / r[2]))));

        return Math.Sqrt(d: ((Square(value: ((foot[0] / scale) - p[0])) + Square(value: ((foot[1] / scale) - p[1]))) + Square(value: ((foot[2] / scale) - p[2]))));
    }
    private static (double At, double Value) Golden(double low, double high, Func<double, double> measure) {
        var ratio = ((Math.Sqrt(d: 5.0) - 1.0) / 2.0);

        for (var step = 0; (step < 100); step++) {
            var left = (high - (ratio * (high - low)));
            var right = (low + (ratio * (high - low)));

            if (measure(arg: left) < measure(arg: right)) {
                high = right;
            } else {
                low = left;
            }
        }

        var at = ((low + high) / 2.0);

        return (at, measure(arg: at));
    }
    private static double Square(double value) => (value * value);
}
