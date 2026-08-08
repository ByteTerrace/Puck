using System.Numerics;
using System.Runtime.Versioning;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-C stage. Cross-backend parity for the CellJitter noise FLAVORS (<see cref="SdfNoiseFlavor"/>) — the two NEW
/// per-cell position-offset paths that <c>world-cell-jitter</c> (which uses the default White) does not exercise. It
/// renders the SAME scattered band as <c>world-cell-jitter</c> but with <see cref="SdfNoiseFlavor.Blue"/>: the R3
/// low-discrepancy lattice is INTEGER-ONLY (asuint(cell)+seed, uint mul-add wrapping mod 2^32), so it must be
/// bit-identical across Vulkan (SPIR-V) and Direct3D 12 (DXIL) — a single lattice-constant, rotation, or seed-fold
/// divergence relocates a box on one backend and trips the diff. (Gaussian's float-averaged offset is the standard
/// ±1-LSB float path already covered by the general shade parity; Blue's integer determinism is the one worth pinning.)
/// The offset stays within ±jitter/2 like White, so the in-cell rule and the AnalyzeLipschitz clamp are unchanged, and
/// the scattered emissive palette makes each cell a high-contrast island — the <c>WorldHighContrast</c> family.
/// </summary>
internal sealed class WorldCellJitterFlavorsStage : IPostStage {
    private const uint WorldHeight = 600;
    private const uint WorldWidth = 960;

    /// <inheritdoc/>
    public string Name => "world-cell-jitter-flavors";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            return PostStageOutcome.Skip(detail: "the cross-backend tier (Direct3D 12) requires Windows 10.0.10240+");
        }

        return RunCore(context: context);
    }

    // The world-cell-jitter band, re-flavored Blue: a single floating row of jittered/tumbled boxes whose per-cell
    // OFFSET comes from the integer R3 low-discrepancy lattice instead of the White PCG3D hash. In-cell budget is
    // identical to world-cell-jitter (jitter/2 0.15 + box radius ~0.214 = ~0.36 < min(spacing)/2 0.5).
    internal static SdfProgram BuildBlueFlavorScene() {
        var builder = new SdfProgramBuilder();
        var ground = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.42f, y: 0.46f, z: 0.52f)));
        var rose = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.95f, y: 0.3f, z: 0.4f), Emissive: 0.25f));

        _ = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.35f, y: 0.9f, z: 0.35f), Emissive: 0.25f));
        _ = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.3f, y: 0.55f, z: 0.95f), Emissive: 0.25f));

        return builder
            .Plane(normal: Vector3.UnitY, offset: 0f, material: ground)
            .Translate(offset: new Vector3(x: 0f, y: 1.0f, z: 0f))
            .CellJitter(spacing: new Vector3(x: 1.0f, y: 6.0f, z: 1.0f), jitter: 0.3f, seed: 1337u, tumble: 0.4f, materialVariants: 3, flavor: SdfNoiseFlavor.Blue)
            .Box(halfExtents: new Vector3(x: 0.16f, y: 0.1f, z: 0.1f), round: 0.02f, material: rose)
            .Build();
    }

    [SupportedOSPlatform("windows10.0.10240")]
    private static PostStageOutcome RunCore(PostContext context) {
        return WorldStage.RunSceneParity(
            context: context,
            prefix: "world-cell-jitter-flavors",
            program: BuildBlueFlavorScene(),
            thresholds: ParityThresholds.WorldHighContrast,
            passLabel: $"{WorldWidth}x{WorldHeight} CellJitter Blue-flavor band (R3 integer low-discrepancy offset) | Vulkan (SPIR-V) vs Direct3D 12 (DXIL) within WorldHighContrast thresholds — the integer lattice is bit-identical cross-backend"
        );
    }
}
