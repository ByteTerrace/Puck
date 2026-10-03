using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// The bounds walk pays only for what a box can reach and widens a box no more than its geometry does. A rotation
/// bounds a box as tightly as the unrotated program bounds the box's image, so a half turn, which the creation emitter
/// writes, never inflates a certified sweep's clearance into a premature stop. The box form of the instance cull skips
/// every instance the box cannot reach, so a bounds query near one object of many walks a fraction of the program, and
/// its interval still holds every point answer and is never wider than the unculled walk's.
/// </summary>
public sealed class SdfBoundsTightnessLawTests {
    // A point box's interval spans its one answer plus the outward raws of each rounded step, as SdfFieldBoundsLawTests
    // measures; a half turn's linear enclosure adds its slack on each axis.
    private const long TurnSlackRaw = 96L;
    private const int InstanceCount = 12;

    private static readonly Vector3 HalfExtents = new(x: 1f, y: 0.6f, z: 0.4f);
    private static readonly Vector3 Offset = new(x: 0.3f, y: -0.7f, z: 0.5f);

    public static TheoryData<string> HalfTurns() => ["x", "y", "z"];
    [MemberData(memberName: nameof(HalfTurns))]
    [Theory]
    public void AHalfTurnBoundsABoxAsTightlyAsTheUnturnedProgramBoundsItsImage(string axis) {
        var (turn, image) = axis switch {
            "x" => (new Quaternion(w: 0f, x: 1f, y: 0f, z: 0f), new Vector3(x: 1f, y: -1f, z: -1f)),
            "y" => (new Quaternion(w: 0f, x: 0f, y: 1f, z: 0f), new Vector3(x: -1f, y: 1f, z: -1f)),
            _ => (new Quaternion(w: 0f, x: 0f, y: 0f, z: 1f), new Vector3(x: -1f, y: -1f, z: 1f)),
        };
        var turned = Box(rotation: turn);
        var unturned = Box(rotation: null);
        var random = new Random(Seed: (17 + axis[0]));

        for (var trial = 0; (trial < 64); trial++) {
            var center = new Vector3(x: Next(random: random, scale: 3f), y: Next(random: random, scale: 3f), z: Next(random: random, scale: 3f));
            var half = new Vector3(x: (MathF.Abs(x: Next(random: random, scale: 1f)) + 0.05f), y: (MathF.Abs(x: Next(random: random, scale: 1f)) + 0.05f), z: (MathF.Abs(x: Next(random: random, scale: 1f)) + 0.05f));
            var lower = Fixed(value: (center - half));
            var upper = Fixed(value: (center + half));
            // A half turn about an axis negates the other two, so it maps an axis-aligned box onto an axis-aligned box.
            var imageLower = Fixed(value: Vector3.Min(value1: ((center - half) * image), value2: ((center + half) * image)));
            var imageUpper = Fixed(value: Vector3.Max(value1: ((center - half) * image), value2: ((center + half) * image)));

            Assert.True(condition: turned.TryDistanceBounds(distance: out var bounds, lower: FixedPosition.FromLocal(local: lower), upper: FixedPosition.FromLocal(local: upper)));
            Assert.True(condition: unturned.TryDistanceBounds(distance: out var reference, lower: FixedPosition.FromLocal(local: imageLower), upper: FixedPosition.FromLocal(local: imageUpper)));
            Assert.True(
                condition: ((bounds.Lower.Value >= (reference.Lower.Value - TurnSlackRaw)) && (bounds.Upper.Value <= (reference.Upper.Value + TurnSlackRaw))),
                userMessage: $"half turn about {axis}, trial {trial}: {bounds} over [{lower}, {upper}] is looser than {reference}, the unturned bounds of its image"
            );
        }
    }
    [Fact]
    public void TheBoxCullWalksOnlyWhatABoxCanReachAndStillEnclosesEveryAnswer() {
        var culled = Row(instanced: true);
        var unculled = Row(instanced: false);
        var random = new Random(Seed: 4093);
        var culledWalk = 0L;
        var unculledWalk = 0L;

        for (var trial = 0; (trial < 96); trial++) {
            var near = (-55f + (random.Next(maxValue: InstanceCount) * 10f));
            var center = new Vector3(x: (near + Next(random: random, scale: 2f)), y: Next(random: random, scale: 1.5f), z: Next(random: random, scale: 2f));
            var half = new Vector3(x: (MathF.Abs(x: Next(random: random, scale: 1f)) * 0.5f), y: (MathF.Abs(x: Next(random: random, scale: 1f)) * 0.5f), z: (MathF.Abs(x: Next(random: random, scale: 1f)) * 0.5f));
            var lower = Fixed(value: (center - half));
            var upper = Fixed(value: (center + half));

            Assert.True(condition: culled.TryDistanceBounds(distance: out var bounds, instructionsWalked: out var walked, lower: FixedPosition.FromLocal(local: lower), upper: FixedPosition.FromLocal(local: upper)));
            Assert.True(condition: unculled.TryDistanceBounds(distance: out var reference, instructionsWalked: out var referenceWalked, lower: FixedPosition.FromLocal(local: lower), upper: FixedPosition.FromLocal(local: upper)));
            Assert.True(
                condition: ((bounds.Lower >= reference.Lower) && (bounds.Upper <= reference.Upper)),
                userMessage: $"trial {trial}: the culled bounds {bounds} are wider than the unculled {reference}"
            );

            for (var sample = 0; (sample < 12); sample++) {
                var point = new FixedVector3(
                    X: Between(lower: lower.X, random: random, upper: upper.X),
                    Y: Between(lower: lower.Y, random: random, upper: upper.Y),
                    Z: Between(lower: lower.Z, random: random, upper: upper.Z)
                );

                Assert.True(condition: culled.TryDistance(distance: out var distance, material: out _, position: FixedPosition.FromLocal(local: point)));
                Assert.True(condition: bounds.Contains(value: distance), userMessage: $"trial {trial}: the point answer {distance} at {point} lies outside {bounds}");
            }

            culledWalk += walked;
            unculledWalk += referenceWalked;
        }

        // A box near one sphere of twelve reaches one or two of them: the walk falls to well under half.
        Assert.True(condition: ((culledWalk * 2L) < unculledWalk), userMessage: $"the culled walk visited {culledWalk} instructions, the unculled {unculledWalk}");
    }

    private static SdfFieldEvaluator Box(Quaternion? rotation) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        if (rotation is { } turn) {
            _ = builder.Rotate(rotation: turn);
        }

        _ = builder.Translate(offset: Offset).Box(halfExtents: HalfExtents, material: material, round: 0f);

        return new SdfFieldEvaluator(program: builder.Build());
    }
    // A floor and a row of spheres ten units apart, each wrapped in an instance bound or emitted bare: the bare row
    // declares no instance, so its walk skips nothing.
    private static SdfFieldEvaluator Row(bool instanced) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Plane(material: material, normal: Vector3.UnitY, offset: 2f);

        for (var index = 0; (index < InstanceCount); index++) {
            var center = new Vector3(x: (-55f + (index * 10f)), y: 0f, z: 0f);

            void Emit(SdfProgramBuilder target) =>
                _ = target.ResetPoint().Translate(offset: center).Sphere(blend: SdfBlendOp.Union, material: material, radius: 0.75f);

            if (instanced) {
                _ = builder.Instance(boundCenter: center, boundRadius: 0.76f, emit: Emit);
            } else {
                Emit(target: builder);
            }
        }

        return new SdfFieldEvaluator(program: builder.Build());
    }
    private static FixedQ4816 Between(FixedQ4816 lower, FixedQ4816 upper, Random random) =>
        FixedQ4816.FromRawBits(value: (lower.Value + random.NextInt64(minValue: 0L, maxValue: ((upper.Value - lower.Value) + 1L))));
    private static FixedVector3 Fixed(Vector3 value) =>
        new(X: FixedQ4816.FromDouble(value: value.X), Y: FixedQ4816.FromDouble(value: value.Y), Z: FixedQ4816.FromDouble(value: value.Z));
    private static float Next(Random random, float scale) =>
        (((((float)random.NextDouble()) * 2f) - 1f) * scale);
}
