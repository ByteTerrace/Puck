using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// THE LAW: <see cref="SdfFieldEvaluator"/> answers an <see cref="SdfShapeType.Ellipse"/> with its Euclidean distance.
/// Over ellipses from near-circular to a hundredfold thin, either axis the major, at points inside, on both axes, beside
/// the boundary and far from it, the evaluator's distance is within <see cref="Tolerance"/> of the distance to the
/// nearest point of the curve found in double precision by dense sampling and local refinement, signed negative inside.
/// The extruded and revolved lifts answer the 2D distance where their lift does not bind.
/// </summary>
public sealed class EllipseDistanceLawTests {
    // Q48.16 resolves 2^-16 of a unit; the bisection and the normalizing divisions round within a few of those.
    private const double Tolerance = 2e-4;
    private static readonly (float SemiX, float SemiY)[] Ellipses = [
        (0.5f, 0.2f),
        (0.2f, 0.5f),
        (0.3f, 0.3001f),
        (0.05f, 0.6f),
        (1.2f, 0.012f),
        (0.75f, 0.4f),
    ];

    [Fact]
    public void TheExtrudedEllipseAnswersItsDistance() {
        var failures = new List<string>();

        foreach (var (semiX, semiY) in Ellipses) {
            var evaluator = Evaluator(semiX: semiX, semiY: semiY, lift: SdfLift.Extrude, liftAmount: 100f);

            foreach (var point in Points(semiX: semiX, semiY: semiY)) {
                var expected = Reference(px: point.X, py: point.Y, semiX: semiX, semiY: semiY);
                var actual = Distance(evaluator: evaluator, point: new Vector3(x: point.X, y: point.Y, z: 0f));

                if (!(Math.Abs(value: (actual - expected)) <= Tolerance)) {
                    failures.Add(item: $"({semiX}, {semiY}) at {point}: {actual}, expected {expected}");
                }
            }
        }

        Assert.Empty(collection: failures);
    }
    [Fact]
    public void TheRevolvedEllipseAnswersItsDistanceAboutItsOffset() {
        const float Offset = 2f;
        var failures = new List<string>();

        foreach (var (semiX, semiY) in Ellipses) {
            var evaluator = Evaluator(semiX: semiX, semiY: semiY, lift: SdfLift.Revolve, liftAmount: Offset);

            foreach (var point in Points(semiX: semiX, semiY: semiY)) {
                if ((point.X + Offset) <= 0f) {
                    continue;
                }

                // The revolve's profile plane holds the radial distance less the offset and the height: a point on +X at
                // radius offset + x is the profile's point (x, y).
                var expected = Reference(px: point.X, py: point.Y, semiX: semiX, semiY: semiY);
                var actual = Distance(evaluator: evaluator, point: new Vector3(x: (Offset + point.X), y: point.Y, z: 0f));

                if (!(Math.Abs(value: (actual - expected)) <= Tolerance)) {
                    failures.Add(item: $"({semiX}, {semiY}) at {point}: {actual}, expected {expected}");
                }
            }
        }

        Assert.Empty(collection: failures);
    }

    private static SdfFieldEvaluator Evaluator(float semiX, float semiY, SdfLift lift, float liftAmount) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.ResetPoint().Ellipse(lift: lift, liftAmount: liftAmount, material: material, semiX: semiX, semiY: semiY);

        return new SdfFieldEvaluator(program: builder.Build());
    }
    private static double Distance(SdfFieldEvaluator evaluator, Vector3 point) {
        Assert.True(condition: evaluator.TryDistance(
            distance: out var distance,
            material: out _,
            position: FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: point))
        ));

        return ((double)distance);
    }
    // Points at the origin, along both axes inside and out, on a ring beside the curve, inside it, and far from it.
    private static IEnumerable<Vector2> Points(float semiX, float semiY) {
        var major = Math.Max(val1: semiX, val2: semiY);

        yield return Vector2.Zero;
        foreach (var fraction in new[] { 0.25f, 0.9f, 1.1f, 3f }) {
            yield return new Vector2(x: (semiX * fraction), y: 0f);
            yield return new Vector2(x: 0f, y: (semiY * fraction));
        }
        for (var step = 0; (step < 24); step++) {
            var angle = ((step * MathF.Tau) / 24f) + 0.13f;
            var (sin, cos) = MathF.SinCos(x: angle);

            foreach (var scale in new[] { 0.5f, 0.97f, 1.03f, 1.6f }) {
                yield return new Vector2(x: (semiX * cos * scale), y: (semiY * sin * scale));
            }

            yield return new Vector2(x: (8f * major * cos), y: (8f * major * sin));
        }
    }
    // The distance to the nearest point of the curve: the best of many samples of its parameter, refined by
    // golden-section search on the distance about it, signed by whether the point lies inside.
    internal static double Reference(double px, double py, double semiX, double semiY) {
        const int Samples = 20000;
        var best = 0.0;
        var bestDistance = double.MaxValue;

        for (var sample = 0; (sample < Samples); sample++) {
            var angle = ((sample * Math.Tau) / Samples);
            var distance = Separation(angle: angle);

            if (distance < bestDistance) {
                bestDistance = distance;
                best = angle;
            }
        }

        var low = (best - (Math.Tau / Samples));
        var high = (best + (Math.Tau / Samples));
        var ratio = ((Math.Sqrt(d: 5.0) - 1.0) / 2.0);

        for (var step = 0; (step < 80); step++) {
            var left = (high - (ratio * (high - low)));
            var right = (low + (ratio * (high - low)));

            if (Separation(angle: left) < Separation(angle: right)) {
                high = right;
            } else {
                low = left;
            }
        }

        var nearest = Separation(angle: ((low + high) / 2.0));
        var inside = ((((px * px) / (semiX * semiX)) + ((py * py) / (semiY * semiY))) < 1.0);

        return (inside ? -nearest : nearest);

        double Separation(double angle) {
            var dx = ((semiX * Math.Cos(d: angle)) - px);
            var dy = ((semiY * Math.Sin(a: angle)) - py);

            return Math.Sqrt(d: ((dx * dx) + (dy * dy)));
        }
    }
}
