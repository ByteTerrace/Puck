using System.Numerics;
using Puck.Scene;
using Puck.SdfVm;

namespace Puck.Demo;

/// <summary>
/// Deterministically generates a random-but-renderable <see cref="SdfProgram"/> from a seed, for cross-backend
/// differential fuzzing of the SDF VM. The same seed (and bounds) always yields the same program (so a finding is
/// reproducible), and the program is fed identically to both backends — any divergence in the rendered result is a
/// backend bug (typically shader codegen of a shape SDF, a smooth blend, or a transcendental).
/// <para>
/// It generates VALID programs (a ground plane plus a handful of placed, blended primitives with randomized
/// parameters, occasionally rotated/scaled) rather than raw malformed words: valid programs actually render, so the
/// differential oracle has signal, and they exercise the real shape/blend code paths across their parameter space.
/// Every parameter range — counts, placement, scale, smoothing, and each shape's dimensions — comes from a
/// <see cref="ShapeBounds"/>, the SAME envelope the scene validator gates authoring against, so a document's
/// <c>fuzzing.bounds</c> widens or narrows the generated and the authored space together.
/// </para>
/// </summary>
internal static class FuzzSdfProgram {
    /// <summary>Generates the program for a seed within an envelope.</summary>
    /// <param name="seed">The fuzz seed; the same seed (and bounds) always produces the same program.</param>
    /// <param name="bounds">The generation envelope (counts + parameter ranges), shared with the scene validator.</param>
    /// <returns>A valid, renderable randomized SDF program.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="bounds"/> is <see langword="null"/>.</exception>
    public static SdfProgram Generate(int seed, ShapeBounds bounds) {
        ArgumentNullException.ThrowIfNull(bounds);

        var random = new Random(Seed: seed);
        var builder = new SdfProgramBuilder();
        var materialCount = random.Next(minValue: 1, maxValue: (bounds.MaxMaterials + 1));
        var materials = new int[materialCount];

        for (var index = 0; (index < materialCount); index++) {
            materials[index] = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(
                x: random.NextSingle(),
                y: random.NextSingle(),
                z: random.NextSingle()
            )));
        }

        // A ground plane gives every frame a floor — real signal for the differential oracle and the content-sanity check.
        _ = builder.ResetPoint().Plane(normal: new Vector3(0f, 1f, 0f), offset: 1f, material: materials[0]);

        var primitiveCount = random.Next(minValue: 1, maxValue: (bounds.MaxPrimitives + 1));

        for (var index = 0; (index < primitiveCount); index++) {
            _ = builder.ResetPoint().Translate(offset: RandomVector(random: random, range: bounds.Translation));

            if (random.NextDouble() < 0.3) {
                _ = builder.Rotate(rotation: RandomRotation(random: random));
            }

            if (random.NextDouble() < 0.2) {
                _ = builder.Scale(scale: RandomVector(random: random, range: bounds.Scale));
            }

            var blend = (SdfBlendOp)random.Next(minValue: 0, maxValue: 4);
            var smooth = Range(random: random, range: bounds.Smooth);
            var material = materials[random.Next(minValue: 0, maxValue: materialCount)];

            switch (random.Next(minValue: 0, maxValue: 4)) {
                case 0: {
                        _ = builder.Sphere(blend: blend, material: material, radius: Range(random: random, range: bounds.SphereRadius), smooth: smooth);

                        break;
                    }
                case 1: {
                        _ = builder.Box(blend: blend, halfExtents: RandomVector(random: random, range: bounds.BoxHalfExtent), material: material, round: Range(random: random, range: bounds.BoxRound), smooth: smooth);

                        break;
                    }
                case 2: {
                        _ = builder.Torus(blend: blend, majorRadius: Range(random: random, range: bounds.TorusMajorRadius), material: material, minorRadius: Range(random: random, range: bounds.TorusMinorRadius), smooth: smooth);

                        break;
                    }
                default: {
                        _ = builder.RoundCone(blend: blend, height: Range(random: random, range: bounds.RoundConeHeight), lowerRadius: Range(random: random, range: bounds.RoundConeLowerRadius), material: material, smooth: smooth, upperRadius: Range(random: random, range: bounds.RoundConeUpperRadius));

                        break;
                    }
            }
        }

        return builder.Build();
    }

    private static float Range(Random random, FloatRange range) =>
        (range.Minimum + ((range.Maximum - range.Minimum) * random.NextSingle()));
    private static Vector3 RandomVector(Random random, FloatRange range) =>
        new(
            x: Range(random: random, range: range),
            y: Range(random: random, range: range),
            z: Range(random: random, range: range)
        );
    private static Quaternion RandomRotation(Random random) =>
        Quaternion.CreateFromYawPitchRoll(
            yaw: Range(random: random, range: s_rotation),
            pitch: Range(random: random, range: s_rotation),
            roll: Range(random: random, range: s_rotation)
        );

    private static readonly FloatRange s_rotation = new(Maximum: 3.14159f, Minimum: -3.14159f);
}
