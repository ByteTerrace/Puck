using System.Numerics;
using System.Runtime.Versioning;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-C stage. Cross-backend parity for material blending at seams: a smooth blend eases
/// the two operands' DISTANCE across the seam, but the winning MATERIAL was — until now — a HARD cut at the geometric
/// midpoint. The fix re-reads the winning smooth blend's factor at the confirmed hit and lerps the two operands' albedos
/// by the clamped seam weight (see sdf-vm.hlsli's <c>sdfMaterialBlendWeight</c> channel and renderView's epilogue).
/// <para>The scene is a TWO-MATERIAL SMOOTH UNION SEAM: a red and a blue matte sphere, offset in X so they overlap, with
/// a generous smooth radius so the fillet — and now the ALBEDO cross-fade — spans a visible band; both float above a gray
/// ground plane (a plain Union, so only the sphere-sphere seam is smooth). Distinct low-green albedos make the mixed band
/// a clean red↔blue gradient the <see cref="WorldMaterialSeamStage"/> single-backend tooth also reads.</para>
/// <para>The seam carries a material-ownership boundary where small distance differences can flip the winner (a legitimately large
/// isolated cross-backend delta) and now cross-fades instead — a MORE parity-stable signal (a ±1-ULP field wobble moves
/// the blend weight by ±1 LSB across the band rather than flipping a whole material class). The diff therefore judges
/// under <c>WorldHighContrast</c>, the material-flip threshold family routes the mixed material through — the hard
/// flip and its gradient successor share one tolerance family so the parity reasoning stays coherent.</para>
/// <para>NOTE (calibration, live): the exact WorldHighContrast pass margins were not re-measured on GPU by the authoring
/// session (no GPU). The family is the reasoned choice (material-flip → material-gradient, same class); the lead
/// confirms the live diff sits inside it — the expectation is that the seam gets QUIETER cross-backend, not louder.</para>
/// </summary>
internal sealed class WorldMaterialBlendStage : IPostStage {
    private const uint WorldHeight = 600;
    private const uint WorldWidth = 960;

    /// <inheritdoc/>
    public string Name => "world-material-blend";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            return PostStageOutcome.Skip(detail: "the cross-backend tier (Direct3D 12) requires Windows 10.0.10240+");
        }

        return RunCore(context: context);
    }

    /// <summary>Builds the two-material smooth-union seam scene both material-blend stages share: a red and a blue matte
    /// sphere overlapping (centres 1.1 apart in X, radius 0.9, smooth radius 0.5 → a wide fillet and a wide albedo band),
    /// floating at y = 1.3 above a gray ground plane. The albedos are near-pure red / near-pure blue with a TINY green
    /// component, so the mixed band is the only place BOTH the red and blue channels are strong while green stays low —
    /// the invariant the seam tooth keys on.</summary>
    /// <returns>The scene program.</returns>
    internal static SdfProgram BuildMaterialSeamScene() {
        var builder = new SdfProgramBuilder();
        var ground = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.5f, y: 0.55f, z: 0.6f)));
        var scarlet = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.88f, y: 0.03f, z: 0.05f)));
        var cobalt = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.05f, y: 0.03f, z: 0.9f)));

        return builder
            .Plane(normal: Vector3.UnitY, offset: 0f, material: ground)
            // Scarlet SEEDS the pair (plain Union onto the ground); cobalt SMOOTH-unions into it. Where the two overlap,
            // the distance fillets AND — the feature — the albedo cross-fades red→blue across the smooth band.
            .ResetPoint()
            .Translate(offset: new Vector3(x: -0.55f, y: 1.3f, z: 0f))
            .Sphere(radius: 0.9f, material: scarlet)
            .ResetPoint()
            .Translate(offset: new Vector3(x: 0.55f, y: 1.3f, z: 0f))
            .Sphere(radius: 0.9f, material: cobalt, blend: SdfBlendOp.SmoothUnion, smooth: 0.5f)
            .Build();
    }

    [SupportedOSPlatform("windows10.0.10240")]
    private static PostStageOutcome RunCore(PostContext context) {
        return WorldStage.RunSceneParity(
            context: context,
            prefix: "world-material-blend",
            program: BuildMaterialSeamScene(),
            thresholds: ParityThresholds.WorldHighContrast,
            passLabel: $"{WorldWidth}x{WorldHeight} two-material smooth-union seam (red+blue spheres) | Vulkan (SPIR-V) vs Direct3D 12 (DXIL) within WorldHighContrast thresholds"
        );
    }
}
