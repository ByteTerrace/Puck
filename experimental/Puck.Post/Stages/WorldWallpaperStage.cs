using System.Numerics;
using System.Runtime.Versioning;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-C stage. Cross-backend parity for the WALLPAPER FOLD (<see cref="SdfOp.WallpaperFold"/>): one square-lattice
/// chain (P4G — quarter-turns + off-center mirrors, the branch-heaviest square group) and one hex-lattice chain
/// (P6M — the full hex kaleidoscope), each folding a deliberately ASYMMETRIC motif with a parity-material stride, so
/// a single flipped mirror, mis-rotated turn, or trunc-vs-floor modulo mis-coloring changes pixels on one backend
/// and trips the diff. Motifs are authored well clear of cell boundaries and rotation seams (the same authoring rule
/// as <c>Repeat</c>), so every fold branch is an exact isometry and the diff stays within the ±1 signature.
/// <para>This stage gates that the two backends AGREE, NOT that a group is the one named (two equally-wrong renders
/// agree — the blindness that once hid P4G rendering as p4). The p4g GROUP-IDENTITY correctness tooth is the sibling
/// <see cref="WorldWallpaperP4gStage"/> (single-cell translation invariance: period-1 p4g, not period-2 p4).</para>
/// </summary>
internal sealed class WorldWallpaperStage : IPostStage {
    private const uint WorldHeight = 600;
    private const uint WorldWidth = 960;

    /// <inheritdoc/>
    public string Name => "world-wallpaper";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            return PostStageOutcome.Skip(detail: "the cross-backend tier (Direct3D 12) requires Windows 10.0.10240+");
        }

        return RunCore(context: context);
    }

    // Two lattice families side by side. Each motif: a round-cone + off-center sphere pair — no rotational or mirror
    // self-symmetry, so a wrong fold VISIBLY relocates it. The stride recolors cells by parity (square: checker over
    // 2 palette rows; hex: the 3-coloring over 3 rows), so the floor-mod cell keys are also under test.
    internal static SdfProgram BuildWallpaperScene() {
        var builder = new SdfProgramBuilder();
        var ground = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.45f, y: 0.5f, z: 0.55f)));
        var rose = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.9f, y: 0.35f, z: 0.45f)));
        // The sky row is reached ONLY through the square chain's checker stride (rose + key 1) — never named.
        _ = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.3f, y: 0.6f, z: 0.9f)));
        var gold = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.9f, y: 0.75f, z: 0.25f)));
        // The mint/plum rows are reached ONLY through the hex chain's parity stride (gold + key 1/2) — never named.
        _ = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.4f, y: 0.85f, z: 0.6f)));
        _ = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.65f, y: 0.35f, z: 0.8f)));

        return builder
            .Plane(normal: Vector3.UnitY, offset: 0f, material: ground)
            // Square-lattice chain: P4G over a 0.8-unit cell, 2-cell limit, checker stride over rose/sky. The motif
            // (an off-center leaning cone + a smaller sphere) sits inside the p4g fundamental wedge {x>=0, z>=0,
            // x+z <= cell/2}, clear of the quadrant seams (x=0, z=0) and the offset-diagonal mirror.
            .Translate(offset: new Vector3(x: -2.2f, y: 0f, z: 0.6f))
            .WallpaperFold(group: SdfWallpaperGroup.P4G, cell: new Vector2(x: 0.8f, y: 0.8f), limit: new Vector2(x: 2f, y: 2f), materialStride: 1)
            .Translate(offset: new Vector3(x: 0.13f, y: -0.02f, z: 0.13f))
            .RoundCone(lowerRadius: 0.1f, upperRadius: 0.03f, height: 0.24f, material: rose)
            .ResetPoint()
            .Translate(offset: new Vector3(x: -2.2f, y: 0f, z: 0.6f))
            .WallpaperFold(group: SdfWallpaperGroup.P4G, cell: new Vector2(x: 0.8f, y: 0.8f), limit: new Vector2(x: 2f, y: 2f), materialStride: 1)
            .Translate(offset: new Vector3(x: 0.07f, y: 0.08f, z: 0.18f))
            .Sphere(radius: 0.06f, material: rose)
            // Hex-lattice chain: P6M over a 0.9-unit pitch, 2-axial-cell limit, 3-coloring stride over gold/mint/plum.
            .ResetPoint()
            .Translate(offset: new Vector3(x: 1.9f, y: 0f, z: -0.2f))
            .WallpaperFold(group: SdfWallpaperGroup.P6M, cell: new Vector2(x: 0.9f, y: 0.9f), limit: new Vector2(x: 2f, y: 2f), materialStride: 1)
            .Translate(offset: new Vector3(x: 0.05f, y: 0f, z: 0.12f))
            .RoundCone(lowerRadius: 0.12f, upperRadius: 0.04f, height: 0.26f, material: gold)
            .ResetPoint()
            .Translate(offset: new Vector3(x: 1.9f, y: 0f, z: -0.2f))
            .WallpaperFold(group: SdfWallpaperGroup.P6M, cell: new Vector2(x: 0.9f, y: 0.9f), limit: new Vector2(x: 2f, y: 2f), materialStride: 1)
            .Translate(offset: new Vector3(x: -0.08f, y: 0.05f, z: 0.02f))
            .Sphere(radius: 0.07f, material: gold)
            .Build();
    }

    [SupportedOSPlatform("windows10.0.10240")]
    private static PostStageOutcome RunCore(PostContext context) {
        return WorldStage.RunSceneParity(
            context: context,
            prefix: "world-wallpaper",
            program: BuildWallpaperScene(),
            thresholds: ParityThresholds.WorldComposite,
            passLabel: $"{WorldWidth}x{WorldHeight} P4G square + P6M hex wallpaper lattices with parity-stride recoloring | Vulkan (SPIR-V) vs Direct3D 12 (DXIL) within WorldComposite thresholds"
        );
    }
}
