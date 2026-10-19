using Puck.Maths;

namespace Puck.SignedDistance.Queries;

public sealed partial class SdfFieldEvaluator {
    // The bisection steps the ellipse's projection root takes at most: its bracket spans at most the point's normalized
    // reach, under 2^48 in Q48.16, so this many halvings reach the format's resolution and the loop ends there.
    private const int EllipseRootSteps = 96;
    // The rounding a fixed-point ellipse distance may carry, which its interval bound widens by: the bisection's last
    // step and the normalizing divisions, far under this.
    private static readonly FixedQ4816 EllipseBoundSlack = (FixedQ4816.One / FixedQ4816.FromInteger(value: 4096));

    // An ellipse lifted from 2D (KEEP IN SYNC with sdfEllipseSolid in field/sdf-shapes.hlsli): extruded along Z to
    // ±liftAmount with its cap bevel, or revolved about Y at liftAmount, exactly as the other lifted profiles.
    private static FixedQ4816 SdfEllipse(FixedVector3 p, FixedQ4816 semiX, FixedQ4816 semiY, FixedQ4816 liftAmount, FixedQ4816 lift, FixedQ4816 capChamfer) =>
        ApplyLift(
            capChamfer: capChamfer,
            distance2D: SdfEllipse2D(
                p: ProjectLiftPoint(
                    lift: lift,
                    liftAmount: liftAmount,
                    p: p
                ),
                semiX: semiX,
                semiY: semiY
            ),
            lift: lift,
            liftAmount: liftAmount,
            p: p
        );
    // The signed Euclidean distance from p to the ellipse of semi-axes (semiX, semiY), negative inside: Eberly's robust
    // projection, whose parameter is the one root of F(s) = (r·z0 / (s + r))² + (z1 / (s + 1))² − 1 on its bracket, bisected
    // to the format's resolution, so the answer is exact to Q48.16 and the same on every machine. The shader answers the
    // same distance in closed form (sdfEllipse2D in field/sdf-ellipse.hlsli).
    private static FixedQ4816 SdfEllipse2D(FixedVector2 p, FixedQ4816 semiX, FixedQ4816 semiY) {
        var y0 = FixedQ4816.Abs(value: p.X);
        var y1 = FixedQ4816.Abs(value: p.Y);
        var e0 = semiX;
        var e1 = semiY;

        // The major axis first.
        if (e0 < e1) {
            (y0, y1) = (y1, y0);
            (e0, e1) = (e1, e0);
        }
        if (y1 > FixedQ4816.Zero) {
            if (y0 <= FixedQ4816.Zero) {
                return (y1 - e1);
            }

            var z0 = (y0 / e0);
            var z1 = (y1 / e1);
            var g = (((z0 * z0) + (z1 * z1)) - FixedQ4816.One);

            if (g == FixedQ4816.Zero) {
                return FixedQ4816.Zero;
            }

            var ratio = (e0 / e1);
            var r0 = (ratio * ratio);
            var s = EllipseRoot(g: g, r0: r0, z0: z0, z1: z1);
            var x0 = ((r0 * y0) / (s + r0));
            var x1 = (y1 / (s + FixedQ4816.One));
            var distance = new FixedVector2(X: (x0 - y0), Y: (x1 - y1)).Length;

            return ((g < FixedQ4816.Zero) ? -distance : distance);
        }

        // On the major axis: inside, nearest a point off it while the axis point lies within its evolute's cusp;
        // otherwise nearest the vertex.
        var numerator = (e0 * y0);
        var denominator = ((e0 * e0) - (e1 * e1));

        if (numerator < denominator) {
            var t = (numerator / denominator);
            var nearest = new FixedVector2(
                X: (e0 * t),
                Y: (e1 * FixedQ4816.Sqrt(value: FixedQ4816.Max(x: FixedQ4816.Zero, y: (FixedQ4816.One - (t * t)))))
            );

            return -new FixedVector2(X: (nearest.X - y0), Y: nearest.Y).Length;
        }

        return (y0 - e0);
    }
    // The projection parameter: F falls across [z1 − 1, |(r0·z0, z1)| − 1] (or up to zero inside), so each halving keeps
    // the side whose sign brackets the root, until the midpoint no longer moves.
    private static FixedQ4816 EllipseRoot(FixedQ4816 g, FixedQ4816 r0, FixedQ4816 z0, FixedQ4816 z1) {
        var n0 = (r0 * z0);
        var low = (z1 - FixedQ4816.One);
        var high = ((g < FixedQ4816.Zero) ? FixedQ4816.Zero : (new FixedVector2(X: n0, Y: z1).Length - FixedQ4816.One));
        var s = low;

        for (var step = 0; (step < EllipseRootSteps); step++) {
            s = ((low + high) / FixedQ4816.FromInteger(value: 2));
            if ((s == low) || (s == high)) {
                break;
            }

            var ratio0 = (n0 / (s + r0));
            var ratio1 = (z1 / (s + FixedQ4816.One));
            var value = (((ratio0 * ratio0) + (ratio1 * ratio1)) - FixedQ4816.One);

            if (value > FixedQ4816.Zero) {
                low = s;
            } else if (value < FixedQ4816.Zero) {
                high = s;
            } else {
                break;
            }
        }

        return s;
    }
    // The ellipse's distance over a box of points: the distance is 1-Lipschitz, so it lies within the box's half diagonal
    // of its value at the centre, widened by the fixed-point rounding.
    private static FixedInterval Ellipse2DBounds(IntervalVector2 p, FixedQ4816 semiX, FixedQ4816 semiY) {
        if (p.X.IsUnbounded || p.Y.IsUnbounded) {
            return FixedInterval.Entire;
        }

        var two = FixedQ4816.FromInteger(value: 2);
        var centre = new FixedVector2(X: ((p.X.Lower + p.X.Upper) / two), Y: ((p.Y.Lower + p.Y.Upper) / two));
        var reach = (new FixedVector2(X: ((p.X.Upper - p.X.Lower) / two), Y: ((p.Y.Upper - p.Y.Lower) / two)).Length + EllipseBoundSlack);
        var distance = SdfEllipse2D(p: centre, semiX: semiX, semiY: semiY);

        return FixedInterval.Hull(first: (distance - reach), second: (distance + reach));
    }
}
