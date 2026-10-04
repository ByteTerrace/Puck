using Puck.Abstractions.Cameras;
using Puck.Maths;
using Puck.SignedDistance;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class SdfTapePredictionLawTests {
    private struct RadiusTotals {
        public long RadiusOnlyLive, ZeroRadiusDropped, RemainingOverlap, ZeroRadiusRetainedOnly;

        public void Add(RadiusTotals other) {
            RadiusOnlyLive += other.RadiusOnlyLive;
            ZeroRadiusDropped += other.ZeroRadiusDropped;
            RemainingOverlap += other.RemainingOverlap;
            ZeroRadiusRetainedOnly += other.ZeroRadiusRetainedOnly;
        }
    }

    private static RadiusTotals CompareRadius(Entry[] entries, bool[] retained, bool[] zeroRadiusRetained,
        Sample normal, Sample zeroRadius) {
        var radiusOnly = 0L;
        var pointOnly = 0L;

        foreach (var entry in entries) {
            if (entry.Candidate is null) { continue; }
            radiusOnly += ((retained[entry.Index] && !zeroRadiusRetained[entry.Index]) ? 1 : 0);
            pointOnly += ((!retained[entry.Index] && zeroRadiusRetained[entry.Index]) ? 1 : 0);
        }
        Assert.Equal(expected: normal.Considered, actual: zeroRadius.Considered);
        Assert.Equal(expected: normal.UnsupportedLive, actual: zeroRadius.UnsupportedLive);
        var remaining = (normal.OverlapLive - radiusOnly);

        Assert.InRange(actual: remaining, low: 0, high: normal.OverlapLive);
        Assert.Equal(expected: ((normal.Dropped + radiusOnly) - pointOnly), actual: zeroRadius.Dropped);
        Assert.Equal(expected: (remaining + pointOnly), actual: zeroRadius.OverlapLive);
        return new RadiusTotals {
            RadiusOnlyLive = radiusOnly,
            RemainingOverlap = remaining,
            ZeroRadiusDropped = zeroRadius.Dropped,
            ZeroRadiusRetainedOnly = pointOnly,
        };
    }
    private void ReportRadius(string label, Totals normal, RadiusTotals radius) {
        output.WriteLine(message: $"{label} radiusOnlyLive={radius.RadiusOnlyLive} zeroRadiusDropped={radius.ZeroRadiusDropped} remainingOverlap={radius.RemainingOverlap} zeroRadiusRetainedOnly={radius.ZeroRadiusRetainedOnly}");
        Assert.Equal(actual: (((normal.Dropped + normal.UnsupportedLive) + radius.RadiusOnlyLive) + radius.RemainingOverlap),
            expected: normal.Considered);
        Assert.Equal(actual: ((normal.Dropped + radius.RadiusOnlyLive) - radius.ZeroRadiusRetainedOnly),
            expected: radius.ZeroRadiusDropped);
    }

    private sealed partial record Candidate {
        public FixedInterval Tighten(BallDomain ball, FixedInterval centred) {
            var direct = DirectBounds(ball: ball);

            if (direct.IsUnbounded) { return centred; }
            if (centred.IsUnbounded) { return direct; }
            var lower = FixedQ4816.Max(x: centred.Lower, y: direct.Lower);
            var upper = FixedQ4816.Min(x: centred.Upper, y: direct.Upper);

            Assert.True(condition: (lower <= upper), userMessage: "The two certified enclosures must contain a common field value.");
            return new FixedInterval(lower: lower, upper: upper);
        }

        private FixedInterval DirectBounds(BallDomain ball) {
            if (!Domain(ball: ball, magnitude: out var magnitude, radius: out var radius, rejection: out _)) {
                return FixedInterval.Entire;
            }
            var centre = V.Of(value: ball.Centre);
            var extent = I(value: ball.Radius);
            var box = new V(X: Around(centre: centre.X, extent: extent), Y: Around(centre: centre.Y, extent: extent),
                Z: Around(centre: centre.Z, extent: extent));
            var primitive = Primitive(point: box);
            var error = Error(magnitude: magnitude, radius: radius);

            return Around(centre: primitive, extent: error);
        }

        public string Budget(BallDomain ball) {
            if (!Domain(ball: ball, magnitude: out var magnitude, radius: out var radius, rejection: out var rejection)) {
                return $"rejected={rejection}";
            }
            var centre = Primitive(point: V.Of(value: ball.Centre));
            var reach = (I(value: Certificate.Lipschitz) * I(value: radius));
            var slope = (I(value: Certificate.ErrorSlope) * (I(value: magnitude) + I(value: radius)));
            var offset = I(value: Certificate.ErrorOffset);
            var error = Error(magnitude: magnitude, radius: radius);
            var normAllowance = NormAllowance(error: error, ideal: centre);

            return (((string)$"primitive={Show(value: centre)} L={Certificate.Lipschitz:R} radius={radius:R} Lr={Show(value: reach)} errorSlope={Show(value: slope)} errorOffset={Show(value: offset)} threeBaseError={Show(value: (error * I(value: 3)))}")
                + $" normAllowance={Show(value: normAllowance)} aabb={Show(value: DirectBounds(ball: ball))}");
        }
    }

    private static FixedInterval Around(FixedInterval centre, FixedInterval extent) =>
        FixedInterval.Union(first: (centre - extent), second: (centre + extent));
    private static string Show(FixedInterval value) => (value.IsUnbounded ? "entire"
        : $"[{((double)value.Lower):R},{((double)value.Upper):R}]");
    private static string Show(Range value) =>
        $"[{(value.Lower.HasValue ? ((double)value.Lower.Value).ToString(format: "R") : "-inf")},{(value.Upper.HasValue ? ((double)value.Upper.Value).ToString(format: "R") : "+inf")}]";
    private void DiagnoseTiles(CameraSnapshot camera, float far, Entry[] entries, InstanceBound[] bounds, int instructions) {
        output.WriteLine(message: "Diagnostic only: fixed quartic slab0 centres, ordinary radius versus zero radius and direct certified ball-AABB intersection; these rows do not change the exhaustive production-model counts above.");
        var retained = new bool[instructions];
        var mask = new bool[instructions];

        foreach (var (x, y) in new[] { (0, 0), (1424, 0), (720, 400), (0, 800), (1424, 800) }) {
            var cone = Cone(camera: camera, x: x, y: y);

            Array.Fill(array: mask, value: true);
            foreach (var bound in bounds) {
                if (!Visible(bound: bound, origin: camera.Position, cone: cone)) {
                    Array.Fill(array: mask, value: false, startIndex: bound.First, count: (bound.End - bound.First));
                }
            }
            var selected = entries.Where(predicate: entry => mask[entry.Index]).ToArray();
            var ball = Ball(camera: camera, cone: cone, depthPower: 4, far: far, slab: 0);
            var ordinary = Walk(entries: selected, ball: ball, retained: retained);
            var point = Walk(entries: selected, ball: ball with { Radius = 0 }, retained: retained);
            var box = Walk(entries: selected, ball: ball, retained: retained, tighten: true);

            output.WriteLine(message: $"diagnostic tile=({x},{y}) centre={ball.Centre} radius={ball.Radius:R} considered={ordinary.Considered} dropped=(ordinary:{ordinary.Dropped},zeroRadius:{point.Dropped},aabb:{box.Dropped})");
            foreach (var zeroRadius in new[] { false, true }) {
                var count = 0;
                var domain = (zeroRadius ? ball with { Radius = 0 } : ball);

                Walk(entries: selected, ball: domain, retained: retained, trace: (entry, current, candidate) => {
                    if ((entry.Candidate is not { } shape) || (shape.Instruction.Shape != ((uint)SdfShapeType.Superellipsoid))
                        || (shape.Instruction.Data0.W != 2.05f) || (count >= 3)) { return; }
                    count++;
                    output.WriteLine(message: $"budget tile=({x},{y}) zeroRadius={zeroRadius} instruction={entry.Index} incumbent={Show(value: current)} candidate={Show(value: candidate)} {shape.Budget(ball: domain)}");
                });
            }
        }
    }
}
