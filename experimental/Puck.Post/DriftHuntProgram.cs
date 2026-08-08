using System.Numerics;
using Puck.Scene;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// The fuzz HUNT's program generator — a deliberate SUPERSET of the gate's <see cref="Puck.Scene.FuzzSdfProgram"/>,
/// grown with the exotic, parity-amplifying ops that generator lacks. It is a SEPARATE generator on purpose:
/// <see cref="Puck.Scene.FuzzSdfProgram"/> carries a byte-stable seed contract (the same seed must reproduce the
/// identical program in BOTH the demo's <c>--validate-world --fuzz-seed</c> gate and the POST's <c>fuzz</c> stage), so
/// growing ITS vocabulary would silently break every existing gate seed's reproduction. The hunt is a MAXIMIZER, not a
/// gate — it wants the nastiest scenes, not a stable cross-harness seed — so it forks and keeps its own vocabulary
/// free to evolve. Determinism is preserved the same way: a single <see cref="Random"/> seeded from the candidate seed
/// drives every choice, so a seed always reproduces its program (the repro contract the leaderboard relies on).
/// <para>What it adds over the gate generator, each targeting a known drift amplifier
/// (docs/sdf-bench-notes.md, the parity-family docs):
/// <list type="bullet">
///   <item>an ALWAYS-present <b>near-tie material seam</b> — two equal-radius EMISSIVE spheres of distinct
///   high-contrast materials smooth-unioned so their distances tie within ~1 ulp along a strip, flipping the winning
///   material backend-to-backend (the material-winner-flip class);</item>
///   <item>a seed-gated <b>LogSphere Droste region</b> — transcendental fold + discontinuous shell boundaries;</item>
///   <item>a seed-gated <b>wallpaper fold region</b> — fold-branch + parity-stride cell-key discontinuities;</item>
///   <item>a seed-gated <b>deep smooth/chamfer chain</b> — nested smin/√2-chamfer LSB accumulation;</item>
///   <item>base primitives from the gate vocabulary, but with blends drawn from the UNION/subtraction/chamfer set
///   (the intersection family is skipped: the accumulator rule makes it annihilate the amplifier regions, producing
///   sparse zero-drift frames — the opposite of what the hunt wants).</item>
/// </list></para>
/// </summary>
internal static class DriftHuntProgram {
    /// <summary>Generates the hunt program for a candidate seed.</summary>
    /// <param name="seed">The candidate seed; the same seed always reproduces the same program.</param>
    /// <returns>A valid, renderable program stacking randomized parity amplifiers.</returns>
    public static SdfProgram Generate(int seed) {
        var random = new Random(Seed: seed);
        var builder = new SdfProgramBuilder();
        var bounds = ShapeBounds.Default;

        var ground = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.5f, y: 0.52f, z: 0.58f)));
        var palette = new int[4];

        for (var index = 0; (index < palette.Length); index++) {
            palette[index] = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(
                x: random.NextSingle(),
                y: random.NextSingle(),
                z: random.NextSingle()
            )));
        }

        // The two seam materials are high-contrast and EMISSIVE, so a winner flip along the tie strip is a large delta.
        var seamA = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.96f, y: 0.9f, z: 0.72f), Emissive: 0.85f));
        var seamB = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.25f, y: 0.5f, z: 0.96f), Emissive: 0.85f));

        _ = builder.ResetPoint().Plane(normal: new Vector3(x: 0f, y: 1f, z: 0f), offset: 1f, material: ground);

        // Base primitives FIRST (so the amplifier regions, emitted after, are never annihilated by a subtraction carve).
        var primitiveCount = random.Next(minValue: 1, maxValue: (bounds.MaxPrimitives + 1));

        for (var index = 0; (index < primitiveCount); index++) {
            EmitBasePrimitive(builder: builder, random: random, bounds: bounds, palette: palette);
        }

        // Seed-gated exotic regions — the search explores their combinations. Each is authored clear of cell/shell
        // boundaries so its folds stay exact isometries (the parity signal comes from codegen, not authoring error).
        if (random.NextDouble() < 0.7) {
            EmitLogSphereDroste(builder: builder, random: random, palette: palette);
        }

        if (random.NextDouble() < 0.6) {
            EmitWallpaper(builder: builder, random: random, palette: palette);
        }

        if (random.NextDouble() < 0.8) {
            EmitSmoothChamferChain(builder: builder, random: random, palette: palette);
        }

        // The material seam is emitted LAST and is ALWAYS present — the single most reliable amplifier.
        EmitMaterialSeam(builder: builder, random: random, materialA: seamA, materialB: seamB);

        return builder.Build();
    }

    // A near-tie material seam: two equal-radius spheres centered symmetrically about a hashed midpoint along a hashed
    // in-plane axis, smooth-unioned so their distances tie within ~1 ulp along the bisector strip and the strict-<
    // material tie-break flips the winner backend-to-backend across the whole strip.
    private static void EmitMaterialSeam(SdfProgramBuilder builder, Random random, int materialA, int materialB) {
        var center = new Vector3(
            x: Range(random: random, minimum: -1.4f, maximum: 1.4f),
            y: Range(random: random, minimum: 0.9f, maximum: 1.35f),
            z: Range(random: random, minimum: -0.4f, maximum: 0.9f)
        );
        var radius = Range(random: random, minimum: 0.6f, maximum: 0.95f);
        var separation = (radius * Range(random: random, minimum: 0.5f, maximum: 0.72f));
        // A unit in-plane (XZ) axis so the tie strip stands vertical and faces the camera.
        var angle = Range(random: random, minimum: 0f, maximum: 6.2831853f);
        var axis = new Vector3(x: MathF.Cos(x: angle), y: 0f, z: MathF.Sin(x: angle));
        var smooth = (radius * 0.7f);

        _ = builder
            .ResetPoint()
            .Translate(offset: (center - (axis * separation)))
            .Sphere(radius: radius, material: materialA, blend: SdfBlendOp.SmoothUnion, smooth: smooth)
            .ResetPoint()
            .Translate(offset: (center + (axis * separation)))
            .Sphere(radius: radius, material: materialB, blend: SdfBlendOp.SmoothUnion, smooth: smooth);
    }

    // A LogSphere Droste region: one prototype (torus or box) tiled by a log-spherical fold into self-similar shells,
    // with a hashed per-shell Z-spin. Transcendental fold + discontinuous shell boundaries. The prototype stays within
    // one shell cell (radii within a factor of shellRatio) so no shell overshoots.
    private static void EmitLogSphereDroste(SdfProgramBuilder builder, Random random, int[] palette) {
        var origin = new Vector3(
            x: Range(random: random, minimum: -2.9f, maximum: -1.9f),
            y: Range(random: random, minimum: 1.1f, maximum: 1.7f),
            z: Range(random: random, minimum: -0.5f, maximum: 0.4f)
        );
        var shellRatio = Range(random: random, minimum: 1.8f, maximum: 2.2f);
        var twist = Range(random: random, minimum: 0f, maximum: 0.8f);
        var material = palette[random.Next(minValue: 0, maxValue: palette.Length)];

        _ = builder.ResetPoint().Translate(offset: origin).LogSphere(shellRatio: shellRatio, twist: twist);

        if (random.NextDouble() < 0.5) {
            _ = builder.Torus(majorRadius: 0.62f, minorRadius: 0.15f, material: material);
        } else {
            _ = builder.Box(halfExtents: new Vector3(x: 0.5f, y: 0.18f, z: 0.5f), round: 0.05f, material: material);
        }
    }

    // A wallpaper fold region: an asymmetric motif (cone + off-center sphere) folded under a hashed group with a
    // parity-material stride, so a flipped mirror or a trunc-vs-floor cell key changes pixels on one backend. Only the
    // square/hex groups whose folds are exact for a well-centered motif are drawn.
    private static void EmitWallpaper(SdfProgramBuilder builder, Random random, int[] palette) {
        SdfWallpaperGroup[] groups = [SdfWallpaperGroup.P2, SdfWallpaperGroup.Pmm, SdfWallpaperGroup.Cmm, SdfWallpaperGroup.P4G, SdfWallpaperGroup.P6M];
        var group = groups[random.Next(minValue: 0, maxValue: groups.Length)];
        var origin = new Vector3(
            x: Range(random: random, minimum: 1.9f, maximum: 2.9f),
            y: 0f,
            z: Range(random: random, minimum: -0.4f, maximum: 0.4f)
        );
        var cellEdge = Range(random: random, minimum: 0.72f, maximum: 0.95f);
        var cell = new Vector2(x: cellEdge, y: cellEdge);
        var material = palette[random.Next(minValue: 0, maxValue: palette.Length)];

        _ = builder
            .ResetPoint()
            .Translate(offset: origin)
            .WallpaperFold(group: group, cell: cell, limit: new Vector2(x: 2f, y: 2f), materialStride: 1)
            .Translate(offset: new Vector3(x: 0.05f, y: 0f, z: 0.1f))
            .RoundCone(lowerRadius: 0.13f, upperRadius: 0.04f, height: 0.28f, material: material)
            .ResetPoint()
            .Translate(offset: origin)
            .WallpaperFold(group: group, cell: cell, limit: new Vector2(x: 2f, y: 2f), materialStride: 1)
            .Translate(offset: new Vector3(x: -0.07f, y: 0.05f, z: 0.02f))
            .Sphere(radius: 0.07f, material: material);
    }

    // A deep smooth/chamfer chain: a row of overlapping spheres, the first a plain union, each subsequent one blended
    // into the running field with an alternating smooth-min / √2-chamfer seam. Deep nesting so the smin/chamfer
    // arithmetic accumulates the LSB noise the two codegens contract differently.
    private static void EmitSmoothChamferChain(SdfProgramBuilder builder, Random random, int[] palette) {
        var links = random.Next(minValue: 4, maxValue: 8);
        var baseX = Range(random: random, minimum: -1.3f, maximum: -0.6f);
        var baseY = Range(random: random, minimum: 0.5f, maximum: 0.7f);
        var baseZ = Range(random: random, minimum: 1.8f, maximum: 2.7f);
        var material = palette[random.Next(minValue: 0, maxValue: palette.Length)];

        _ = builder.ResetPoint().Translate(offset: new Vector3(x: baseX, y: baseY, z: baseZ)).Sphere(radius: 0.4f, material: material);

        for (var link = 1; (link < links); link++) {
            var chamfer = ((link & 1) == 0);
            var blend = (chamfer ? SdfBlendOp.ChamferUnion : SdfBlendOp.SmoothUnion);

            _ = builder
                .ResetPoint()
                .Translate(offset: new Vector3(x: (baseX + (link * 0.34f)), y: (baseY + (chamfer ? 0.06f : -0.03f)), z: baseZ))
                .Sphere(radius: 0.38f, material: material, blend: blend, smooth: 0.3f);
        }
    }

    // One base primitive from the gate vocabulary — a placed, optionally rotated/scaled shape with a blend drawn from
    // the NON-annihilating set (union / smooth / subtraction / chamfer). Intersection blends are deliberately excluded:
    // the accumulator rule makes them delete every earlier shape they do not overlap, which would erase the amplifier
    // regions and produce sparse zero-drift frames.
    private static void EmitBasePrimitive(SdfProgramBuilder builder, Random random, ShapeBounds bounds, int[] palette) {
        _ = builder.ResetPoint().Translate(offset: RandomVector(random: random, range: bounds.Translation));

        if (random.NextDouble() < 0.3) {
            _ = builder.Rotate(rotation: RandomRotation(random: random));
        }

        if (random.NextDouble() < 0.2) {
            _ = builder.Scale(scale: RandomVector(random: random, range: bounds.Scale));
        }

        var blend = s_baseBlends[random.Next(minValue: 0, maxValue: s_baseBlends.Length)];
        var smooth = Range(random: random, range: bounds.Smooth);
        var material = palette[random.Next(minValue: 0, maxValue: palette.Length)];

        switch (random.Next(minValue: 0, maxValue: 5)) {
            case 0: {
                    _ = builder.Sphere(blend: blend, material: material, radius: Range(random: random, range: bounds.SphereRadius), smooth: smooth);

                    break;
                }
            case 1: {
                    _ = builder.Box(blend: blend, halfExtents: RandomVector(random: random, range: bounds.BoxHalfExtent), material: material, round: 0.06f, smooth: smooth);

                    break;
                }
            case 2: {
                    _ = builder.Torus(blend: blend, majorRadius: Range(random: random, range: bounds.TorusMajorRadius), material: material, minorRadius: Range(random: random, range: bounds.TorusMinorRadius), smooth: smooth);

                    break;
                }
            case 3: {
                    _ = builder.RoundCone(blend: blend, height: 0.5f, lowerRadius: 0.32f, material: material, smooth: smooth, upperRadius: 0.12f);

                    break;
                }
            default: {
                    _ = builder.Cylinder(blend: blend, halfHeight: 0.5f, material: material, radius: 0.34f, smooth: smooth);

                    break;
                }
        }
    }
    private static float Range(Random random, float minimum, float maximum) =>
        (minimum + ((maximum - minimum) * random.NextSingle()));
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
            yaw: Range(random: random, minimum: -3.14159f, maximum: 3.14159f),
            pitch: Range(random: random, minimum: -3.14159f, maximum: 3.14159f),
            roll: Range(random: random, minimum: -3.14159f, maximum: 3.14159f)
        );

    // The non-annihilating blend set (see EmitBasePrimitive).
    private static readonly SdfBlendOp[] s_baseBlends = [
        SdfBlendOp.Union,
        SdfBlendOp.SmoothUnion,
        SdfBlendOp.Subtraction,
        SdfBlendOp.SmoothSubtraction,
        SdfBlendOp.ChamferUnion,
        SdfBlendOp.ChamferSubtraction,
    ];
}
