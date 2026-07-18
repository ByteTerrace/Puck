using System.Numerics;

namespace Puck.SdfVm.Debug;

/// <summary>The DRIFT MONOLITH — the one hand-authored scene that stacks every known cross-backend parity
/// amplifier into a single frame (LogSphere Droste + P6M wallpaper fold + near-tie emissive material seam +
/// deep smooth/chamfer chain + far grazing wall). Shared verbatim by the Post drift-ceiling stage and the
/// demo gallery's monolith exhibit, so both paths exercise identical geometry.</summary>
public static class SdfDriftMonolith {
    /// <summary>Emits the monolith into an existing builder (the gallery path). Every region composes through the
    /// UNION family (smooth/chamfer union, plain union) so nothing annihilates its neighbours (the accumulator
    /// rule); the emissive seam is emitted LAST so its material-winner flip is always present regardless of what
    /// precedes it. SUBTLETY: the two hex-stride materials are reached POSITIONALLY through the wallpaper
    /// <c>materialStride</c> — call this into a builder holding none of the caller's own materials yet.</summary>
    /// <param name="builder">The program builder to emit into (fresh, or holding only this scene's materials so far).</param>
    public static void Emit(SdfProgramBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        var ground = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.5f, y: 0.52f, z: 0.58f)));
        var brick = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.8f, y: 0.35f, z: 0.25f)));
        var teal = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.2f, y: 0.7f, z: 0.7f)));
        var rose = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.9f, y: 0.35f, z: 0.45f)));
        // The two hex-stride rows are reached ONLY through the wallpaper chain's 3-coloring — never named directly.
        _ = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.4f, y: 0.85f, z: 0.6f)));
        _ = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.65f, y: 0.35f, z: 0.8f)));
        var jade = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.2f, y: 0.7f, z: 0.45f)));
        var wall = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.45f, y: 0.48f, z: 0.52f)));
        // The seam pair: two DISTINCT high-contrast EMISSIVE materials, so a winner flip along the tie strip is a large,
        // obvious cross-backend delta (the WorldHighContrast phenomenon, escalated to a whole strip).
        var seamCream = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.96f, y: 0.9f, z: 0.72f), Emissive: 0.85f));
        var seamAzure = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.25f, y: 0.5f, z: 0.96f), Emissive: 0.85f));

        _ = builder.Plane(normal: Vector3.UnitY, offset: 0f, material: ground);

        // LEFT — LogSphere Droste: a torus tiled into a spinning spiral of self-similar shells (per-shell Z-spin) and a
        // box tiled into concentric shells. Transcendental fold + discontinuous shell boundaries.
        _ = builder
            .ResetPoint()
            .Translate(offset: new Vector3(x: -2.7f, y: 1.5f, z: -0.3f))
            .LogSphere(shellRatio: 2.0f, twist: 0.6f)
            .Torus(majorRadius: 0.62f, minorRadius: 0.15f, material: brick)
            .ResetPoint()
            .Translate(offset: new Vector3(x: -2.7f, y: 1.5f, z: -0.3f))
            .LogSphere(shellRatio: 1.9f)
            .Box(halfExtents: new Vector3(x: 0.5f, y: 0.18f, z: 0.5f), round: 0.05f, material: teal);

        // RIGHT — wallpaper fold: a P6M hex kaleidoscope over an ASYMMETRIC motif (cone + off-center sphere) with a
        // 3-coloring parity stride, so a flipped mirror or a trunc-vs-floor cell key changes pixels on one backend.
        // The motif sits well clear of cell boundaries/seams so every fold branch is an exact isometry.
        _ = builder
            .ResetPoint()
            .Translate(offset: new Vector3(x: 2.7f, y: 0f, z: 0.2f))
            .WallpaperFold(group: SdfWallpaperGroup.P6M, cell: new Vector2(x: 0.85f, y: 0.85f), limit: new Vector2(x: 2f, y: 2f), materialStride: 1)
            .Translate(offset: new Vector3(x: 0.05f, y: 0f, z: 0.1f))
            .RoundCone(lowerRadius: 0.13f, upperRadius: 0.04f, height: 0.28f, material: rose)
            .ResetPoint()
            .Translate(offset: new Vector3(x: 2.7f, y: 0f, z: 0.2f))
            .WallpaperFold(group: SdfWallpaperGroup.P6M, cell: new Vector2(x: 0.85f, y: 0.85f), limit: new Vector2(x: 2f, y: 2f), materialStride: 1)
            .Translate(offset: new Vector3(x: -0.07f, y: 0.05f, z: 0.02f))
            .Sphere(radius: 0.07f, material: rose);

        // FRONT — deep smooth/chamfer chain: a row of overlapping spheres, the first a plain union, each subsequent one
        // blended into the running field with an ALTERNATING smooth-min / √2-chamfer seam. Deep nesting so the smin and
        // chamfer arithmetic accumulates the LSB noise the two codegens contract differently.
        _ = builder.ResetPoint().Translate(offset: new Vector3(x: -1.05f, y: 0.55f, z: 2.5f)).Sphere(radius: 0.4f, material: jade);

        for (var link = 1; (link < 7); link++) {
            var blend = (((link & 1) == 0) ? SdfBlendOp.ChamferUnion : SdfBlendOp.SmoothUnion);

            _ = builder
                .ResetPoint()
                .Translate(offset: new Vector3(x: (-1.05f + (link * 0.34f)), y: (0.55f + (((link & 1) == 0) ? 0.06f : -0.03f)), z: 2.5f))
                .Sphere(radius: 0.38f, material: jade, blend: blend, smooth: 0.3f);
        }

        // BACK — thin far grazing wall: a wide, tall, razor-thin slab set far behind the origin so its top and side
        // silhouettes graze near MaxDistance under footprint-adaptive termination (the ground horizon does the same).
        _ = builder.ResetPoint().Translate(offset: new Vector3(x: 0f, y: 2f, z: -6.5f)).Box(halfExtents: new Vector3(x: 7f, y: 2f, z: 0.03f), round: 0f, material: wall);

        // CENTER (emitted LAST) — near-tie material seam: two equal-radius emissive spheres centered symmetrically about
        // x = 0, smooth-unioned so along the x = 0 strip their distances tie within ~1 ulp and the strict-< material
        // tie-break flips the winner backend-to-backend across the whole strip.
        _ = builder
            .ResetPoint()
            .Translate(offset: new Vector3(x: -0.62f, y: 1.05f, z: 0.7f))
            .Sphere(radius: 0.92f, material: seamCream, blend: SdfBlendOp.SmoothUnion, smooth: 0.5f)
            .ResetPoint()
            .Translate(offset: new Vector3(x: 0.62f, y: 1.05f, z: 0.7f))
            .Sphere(radius: 0.92f, material: seamAzure, blend: SdfBlendOp.SmoothUnion, smooth: 0.5f);
    }

    /// <summary>Builds the standalone drift-monolith program.</summary>
    /// <returns>The scene program.</returns>
    public static SdfProgram Build() {
        var builder = new SdfProgramBuilder();

        Emit(builder: builder);

        return builder.Build();
    }
}
