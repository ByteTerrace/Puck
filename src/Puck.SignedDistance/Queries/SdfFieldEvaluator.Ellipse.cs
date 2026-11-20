using Puck.Maths;

namespace Puck.SignedDistance.Queries;

public sealed partial class SdfFieldEvaluator {
    /// <summary>The projection's halving bound shared by the CPU (<see cref="FixedEllipsoidProjection.RootSteps"/>), the
    /// generated HLSL and the shade work model.</summary>
    public const int EllipseRootSteps = FixedEllipsoidProjection.RootSteps;

    // The farthest box centre, in metres from the ellipse's own origin along either axis, whose bound the projection
    // answers.
    private static readonly FixedQ4816 EllipseBoundCentre = FixedQ4816.FromInteger(value: 65536);

    // The signed Euclidean distance from p to the ellipse of semi-axes (semiX, semiY), negative inside: the shared
    // projection's scalar (FixedEllipsoidProjection), whose root the shader runs in float (sdfEllipse2D in
    // field/sdf-ellipse.hlsli).
    private static FixedQ4816 SdfEllipse2D(FixedVector2 p, FixedQ4816 semiX, FixedQ4816 semiY) =>
        FixedEllipsoidProjection.Project(point: p, semiX: semiX, semiY: semiY).Distance;
    // The ellipse's distance over a box of points: the distance is 1-Lipschitz, so it lies within the box's half diagonal
    // of the interval the projection holds at the centre. A centre past EllipseBoundCentre (the frame search's cubes reach
    // 2^46 m, and a revolve's radial interval centres half way out) is unbounded.
    private static FixedInterval Ellipse2DBounds(IntervalVector2 p, FixedQ4816 semiX, FixedQ4816 semiY) {
        if (p.X.IsUnbounded || p.Y.IsUnbounded) {
            return FixedInterval.Entire;
        }

        var two = FixedQ4816.FromInteger(value: 2);
        var centre = new FixedVector2(X: ((p.X.Lower + p.X.Upper) / two), Y: ((p.Y.Lower + p.Y.Upper) / two));

        if ((FixedQ4816.Abs(value: centre.X) > EllipseBoundCentre) || (FixedQ4816.Abs(value: centre.Y) > EllipseBoundCentre)) {
            return FixedInterval.Entire;
        }

        // The half diagonal rounded up: the centre's own rounding is half a raw on each axis.
        var reach = (new FixedVector2(X: ((p.X.Upper - p.X.Lower) / two), Y: ((p.Y.Upper - p.Y.Lower) / two)).Length + FixedQ4816.FromRawBits(value: 2L));
        var held = FixedEllipsoidProjection.Project(point: centre, semiX: semiX, semiY: semiY).Signed;

        return new FixedInterval(lower: (held.Lower - reach), upper: (held.Upper + reach));
    }
}
