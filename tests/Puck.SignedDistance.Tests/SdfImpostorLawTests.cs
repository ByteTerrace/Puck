using System.Numerics;
using Puck.SignedDistance.Baking;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// THE LAW: an impostor's views, searched as the card's fragments search them (<see cref="SdfImpostorOracle"/>, the CPU
/// twin of <c>sdfImpostorTrace</c>), show the surface the field has. For rays from every direction of a Fibonacci sphere,
/// across the sphere's disc, the oracle and the analytic surface of a sphere and of a box agree on whether the ray hits
/// except within a stated share of the rays near the silhouette, and where both hit, the distance along the ray stays within
/// a stated share of the bounding radius. The view basis the oracle reads is the baker's: a mutant that exchanges the
/// basis' right and up exceeds both bounds, so the bounds discriminate. The direction selection is the octahedral map's
/// inverse of the baker's decode, and the three views' weights are a partition of one.
/// </summary>
public sealed class SdfImpostorLawTests(ITestOutputHelper output) {
    // The stated bounds, at the standard tier: the share of rays whose hit/miss differs more than a silhouette's tolerance (see
    // Compare), and the mean and ninety-fifth percentile distance
    // errors of those they agree on, in bounding radii (a ray grazing the silhouette moves a hit far for a texel of depth).
    private const double UnexplainedShare = 0.005;
    // How far a ray may be moved across the silhouette for the surface to show what the views show, in view texels.
    private const float SilhouetteTexels = 1.5f;
    private const double MeanError = 0.04;
    private const double TailError = 0.12;

    private static readonly SdfMaterial[] Materials = [new(Albedo: new Vector3(x: 0.8f, y: 0.2f, z: 0.1f))];

    private static SdfBake Bake(SdfProgram program, float reach) =>
        SdfBaker.Bake(
            center: Vector3.Zero,
            materials: Materials,
            program: program,
            reach: reach,
            tier: SdfBakeTier.For(quality: SdfBakeQuality.Standard)
        );
    private static SdfProgram Make(Action<SdfProgramBuilder> emit) {
        var builder = new SdfProgramBuilder();

        _ = builder.AddMaterial(material: Materials[0]);
        emit(obj: builder);

        return builder.Build(buildInstanceGrid: false);
    }
    // The first parameter at which a ray meets a sphere centered at the origin, or none.
    private static double? Sphere(Vector3 origin, Vector3 direction, float radius) {
        var b = Vector3.Dot(vector1: origin, vector2: direction);
        var c = (Vector3.Dot(vector1: origin, vector2: origin) - (radius * radius));
        var discriminant = ((b * b) - c);

        return ((discriminant <= 0f) ? null : ((((-b - MathF.Sqrt(x: discriminant)) is var t) && (t > 0f)) ? t : null));
    }
    // The first parameter at which a ray meets a box centered at the origin, or none.
    private static double? Box(Vector3 origin, Vector3 direction, Vector3 half) {
        var near = double.NegativeInfinity;
        var far = double.PositiveInfinity;

        for (var axis = 0; (axis < 3); axis++) {
            var o = origin[axis];
            var d = direction[axis];
            var h = half[axis];

            if (Math.Abs(value: d) < 1e-9) {
                if (Math.Abs(value: o) > h) {
                    return null;
                }

                continue;
            }

            var (t0, t1) = (((-h - o) / d), ((h - o) / d));

            near = Math.Max(val1: near, val2: Math.Min(val1: t0, val2: t1));
            far = Math.Min(val1: far, val2: Math.Max(val1: t0, val2: t1));
        }

        return (((near <= far) && (near > 0.0)) ? near : null);
    }
    private static IEnumerable<(Vector3 Direction, Vector3 Origin)> Rays(SdfBakedImpostor impostor) {
        const int Directions = 64;
        const int Grid = 7;
        var golden = (MathF.PI * (3f - MathF.Sqrt(x: 5f)));

        for (var index = 0; (index < Directions); index++) {
            var y = (1f - ((2f * (index + 0.5f)) / Directions));
            var ring = MathF.Sqrt(x: (1f - (y * y)));
            var direction = new Vector3(x: (ring * MathF.Cos(x: (golden * index))), y: y, z: (ring * MathF.Sin(x: (golden * index))));
            var reference = ((MathF.Abs(x: direction.Y) > 0.9f) ? Vector3.UnitX : Vector3.UnitY);
            var right = Vector3.Normalize(value: Vector3.Cross(vector1: reference, vector2: direction));
            var up = Vector3.Cross(vector1: direction, vector2: right);

            for (var j = 0; (j < Grid); j++) {
                for (var i = 0; (i < Grid); i++) {
                    var across = ((((i + 0.5f) / Grid) * 2f) - 1f);
                    var down = ((((j + 0.5f) / Grid) * 2f) - 1f);

                    if (((across * across) + (down * down)) > 0.94f) {
                        continue;
                    }

                    yield return (direction, ((impostor.Center + (impostor.Radius * ((across * right) + (down * up)))) - (direction * (3f * impostor.Radius))));
                }
            }
        }
    }
    // Casts the rays at the oracle. A ray whose hit or miss differs from the surface's is explained when a ray moved
    // SilhouetteTexels across the view (in any of eight directions) has the outcome the views gave, which is the silhouette
    // a texel of depth bends. The rest are unexplained.
    private (int Total, int Unexplained, double Mean, double Tail) Compare(SdfBakedImpostor impostor, Func<Vector3, Vector3, double?> surface, bool swapBasis) {
        var oracle = new SdfImpostorOracle(impostor: impostor);

        var (total, unexplained, sum) = (0, 0, 0.0);
        var errors = new List<double>();
        var radius = ((double)impostor.Radius);

        foreach (var (direction, origin) in Rays(impostor: impostor)) {
            total++;

            var truth = surface((origin - impostor.Center), direction);
            var start = ((origin - impostor.Center) / impostor.Radius);
            var travel = (direction / impostor.Radius);
            var hit = oracle.Trace(
                start: (((double)start.X), ((double)start.Y), ((double)start.Z)),
                swapBasis: swapBasis,
                t: out var t,
                travel: (((double)travel.X), ((double)travel.Y), ((double)travel.Z))
            );

            if (hit != truth.HasValue) {
                var texel = ((2f * impostor.Radius) / impostor.ViewTexels);
                var reference = ((MathF.Abs(x: direction.Y) > 0.9f) ? Vector3.UnitX : Vector3.UnitY);
                var right = Vector3.Normalize(value: Vector3.Cross(vector1: reference, vector2: direction));
                var up = Vector3.Cross(vector1: direction, vector2: right);
                var explained = false;

                for (var around = 0; ((around < 8) && !explained); around++) {
                    var angle = (around * (MathF.PI / 4f));
                    var moved = (origin + ((SilhouetteTexels * texel) * ((MathF.Cos(x: angle) * right) + (MathF.Sin(x: angle) * up))));

                    explained = (surface((moved - impostor.Center), direction).HasValue == hit);
                }

                unexplained += (explained ? 0 : 1);
                continue;
            }
            if (hit) {
                var error = (Math.Abs(value: (t - truth!.Value)) / radius);

                errors.Add(item: error);
                sum += error;
            }
        }

        errors.Sort();

        return (total, unexplained, (sum / Math.Max(val1: errors.Count, val2: 1)), ((errors.Count == 0) ? 0.0 : errors[((int)(0.95 * (errors.Count - 1)))]));
    }

    [Fact]
    public void TheViewsShowTheSphereAndTheBoxTheFieldHas() {
        var sphere = Bake(program: Make(emit: static builder => builder.ResetPoint().Sphere(material: 0, radius: 0.8f)), reach: 0.85f).Impostor;
        var box = Bake(program: Make(emit: static builder => builder.ResetPoint().Box(halfExtents: new Vector3(x: 0.6f, y: 0.4f, z: 0.5f), material: 0, round: 0f)), reach: 0.9f).Impostor;

        foreach (var (name, impostor, surface) in new (string, SdfBakedImpostor, Func<Vector3, Vector3, double?>)[] {
            ("sphere", sphere, static (origin, direction) => Sphere(direction: direction, origin: origin, radius: 0.8f)),
            ("box", box, static (origin, direction) => Box(origin: origin, direction: direction, half: new Vector3(x: 0.6f, y: 0.4f, z: 0.5f))),
        }) {
            var (total, unexplained, mean, tail) = Compare(impostor: impostor, surface: surface, swapBasis: false);

            output.WriteLine(message: $"{name}: {total} rays, {unexplained} unexplained disagreements, mean error {mean:F4}, 95th percentile {tail:F4} radii");
            Assert.True(condition: (unexplained <= (UnexplainedShare * total)), userMessage: $"{name}: {unexplained} of {total} rays disagree on a hit by more than {SilhouetteTexels} texels");
            Assert.True(condition: (mean <= MeanError), userMessage: $"{name}: mean distance error {mean} radii");
            Assert.True(condition: (tail <= TailError), userMessage: $"{name}: 95th percentile distance error {tail} radii");
        }
    }
    [Fact]
    public void ExchangingTheViewBasisBreaksTheBoundsSoTheyDiscriminate() {
        var box = Bake(program: Make(emit: static builder => builder.ResetPoint().Box(halfExtents: new Vector3(x: 0.6f, y: 0.4f, z: 0.5f), material: 0, round: 0f)), reach: 0.9f).Impostor;

        var (total, unexplained, mean, tail) = Compare(
            impostor: box,
            surface: static (origin, direction) => Box(origin: origin, direction: direction, half: new Vector3(x: 0.6f, y: 0.4f, z: 0.5f)),
            swapBasis: true
        );

        output.WriteLine(message: $"mutant: {total} rays, {unexplained} unexplained disagreements, mean error {mean:F4}, 95th percentile {tail:F4} radii");
        Assert.True(condition: ((unexplained > (UnexplainedShare * total)) || (mean > MeanError) || (tail > TailError)), userMessage: "the exchanged basis stays within every bound");
    }
    [Fact]
    public void TheSelectedViewsArePartOfTheGridAndTheirWeightsSumToOne() {
        for (var index = 0; (index < 200); index++) {
            var y = (1.0 - ((2.0 * (index + 0.5)) / 200.0));
            var ring = Math.Sqrt(d: (1.0 - (y * y)));
            var chosen = SdfImpostorOracle.Views(toward: ((ring * Math.Cos(d: (index * 2.399963))), y, (ring * Math.Sin(a: (index * 2.399963)))), views: 8);

            Assert.InRange(actual: ((chosen.Wa + chosen.Wb) + chosen.Wc), high: 1.0000001, low: 0.9999999);
            Assert.All(collection: new[] { chosen.Wa, chosen.Wb, chosen.Wc }, action: static weight => Assert.InRange(actual: weight, high: 1.0000001, low: -0.0000001));

            foreach (var (i, j) in new[] { chosen.A, chosen.B, chosen.C }) {
                Assert.InRange(actual: i, high: 7, low: 0);
                Assert.InRange(actual: j, high: 7, low: 0);
            }
        }
    }
    [Fact]
    public void ADirectionAtAViewCenterSelectsThatViewAlone() {
        for (var j = 0; (j < 8); j++) {
            for (var i = 0; (i < 8); i++) {
                var view = SdfBakedImpostor.ViewDirection(i: i, j: j, views: 8);
                var chosen = SdfImpostorOracle.Views(toward: (((double)view.X), ((double)view.Y), ((double)view.Z)), views: 8);
                var weights = new[] { (chosen.A, chosen.Wa), (chosen.B, chosen.Wb), (chosen.C, chosen.Wc) };
                var heaviest = weights.OrderByDescending(keySelector: static pair => pair.Item2).First();

                Assert.Equal(actual: heaviest.Item1, expected: (i, j));
                Assert.InRange(actual: heaviest.Item2, high: 1.0001, low: 0.99);
            }
        }
    }
}
