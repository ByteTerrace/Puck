using System.Numerics;
using System.Runtime.Versioning;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-C stage. Cross-backend parity for the STOCHASTIC DOMAIN-REPEAT fold (<see cref="SdfOp.CellJitter"/>): a single
/// asymmetric prototype box scattered into a floating band of cells that are each displaced by a hashed offset
/// (<c>jitter</c>), rotated by a hashed tumble, and recolored by a hashed material variant — so ONE instruction drives
/// all three sub-effects at once. The per-cell decisions are INTEGER-ONLY (PCG3D on the two's-complement cell index),
/// so they must be bit-identical across both backends; a single hash-lane, round()-vs-floor cell-key, or material-stride
/// divergence relocates or recolors a box on one backend and trips the diff. The prototype is authored comfortably
/// inside its cell (jitter/2 + bounding radius &lt; min(spacing)/2) so every jittered/tumbled placement is an exact
/// isometry with no boundary crossing, and the scattered palette makes each cell a high-contrast island whose winning
/// material can legitimately flip at a boundary pixel — hence the <c>WorldHighContrast</c> threshold family.
/// </summary>
internal sealed class WorldCellJitterStage : IPostStage {
    private const uint WorldHeight = 600;
    private const uint WorldWidth = 960;

    /// <inheritdoc/>
    public string Name => "world-cell-jitter";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            return PostStageOutcome.Skip(detail: "the cross-backend tier (Direct3D 12) requires Windows 10.0.10240+");
        }

        return RunCore(context: context);
    }

    // A single floating band of cells (Y spacing is large, so only the y=0 row is populated — sky stays above and the
    // ground plane below). Each cell displaces (jitter 0.3), tumbles (0.4 → up to ~±0.4π about a hashed axis), and
    // recolors (3 hashed material rows) an asymmetric round box, so all three CellJitter sub-effects are under test and
    // the tumble is VISIBLE (an asymmetric prototype relocates AND reorients per cell). In-cell budget: jitter/2 (0.15)
    // + prototype bounding radius (~0.214) = ~0.36 < min(spacing)/2 (0.5), so no placement crosses a cell boundary.
    internal static SdfProgram BuildCellJitterScene() {
        var builder = new SdfProgramBuilder();
        var ground = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.42f, 0.46f, 0.52f)));
        // Three CONTIGUOUS material rows: the shape names the first (rose); the hashed variant (0..2) reaches the two
        // that follow (lime, azure). All are never-named except rose — the stride is the only path to lime/azure.
        var rose = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.95f, 0.3f, 0.4f), Emissive: 0.25f));
        _ = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.35f, 0.9f, 0.35f), Emissive: 0.25f));
        _ = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.3f, 0.55f, 0.95f), Emissive: 0.25f));

        return builder
            .Plane(normal: Vector3.UnitY, offset: 0f, material: ground)
            // Lift the lattice so the single populated row floats at ~y=1.0 above the ground plane.
            .Translate(offset: new Vector3(0f, 1.0f, 0f))
            .CellJitter(spacing: new Vector3(1.0f, 6.0f, 1.0f), jitter: 0.3f, seed: 1337u, tumble: 0.4f, materialVariants: 3)
            // The asymmetric round box: distinct half-extents on every axis so a hashed tumble visibly reorients it.
            .Box(halfExtents: new Vector3(0.16f, 0.1f, 0.1f), round: 0.02f, material: rose)
            .Build();
    }

    [SupportedOSPlatform("windows10.0.10240")]
    private static PostStageOutcome RunCore(PostContext context) {
        // A scattered high-contrast field: neighbouring cells carry different (emissive) palettes, so a boundary pixel
        // can legitimately flip which cell/material wins between backends — an isolated large delta that stays ±1-mass.
        // That is exactly the WorldHighContrast posture (the same family the emissive/high-contrast world scenes use).
        return WorldStage.RunSceneParity(
            context: context,
            prefix: "world-cell-jitter",
            program: BuildCellJitterScene(),
            thresholds: ParityThresholds.WorldHighContrast,
            passLabel: $"{WorldWidth}x{WorldHeight} CellJitter scattered band (jitter 0.3 + tumble 0.4 + 3 material variants) | Vulkan (SPIR-V) vs Direct3D 12 (DXIL) within WorldHighContrast thresholds"
        );
    }
}
