using System.Numerics;
using System.Runtime.Versioning;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-C stage. Cross-backend parity for the ARBITRARY-PLANE REFLECTION fold (<see cref="SdfOp.SymmetryPlane"/>,
/// the general-normal superset of <c>SymmetryX</c>/<c>SymmetryY</c>/<c>SymmetryZ</c>): a NON-axis-aligned plane
/// (normal <c>(1, 0, 1)</c> normalized, a nonzero offset) mirrors an off-center, asymmetric cluster of three
/// distinct emissive shapes (a rounded box, a cone, and a sphere, each at a different offset from the others) from
/// the plane's positive side onto its negative side, so a wrong normal, an unnormalized reflection, or a mis-signed
/// offset visibly relocates or de-mirrors the reflected trio on one backend and not the other. The fold is a
/// REFLECTION — an ISOMETRY, exactly like <c>SymmetryX/Y/Z</c>, <c>WallpaperFold</c>, and <c>RepeatPolar</c> — so it
/// is 1-Lipschitz (factor 1, no step clamp) and needs no solidity gate. The prototype is authored well clear of the
/// plane (comfortably inside the positive half), so the fold stays an exact reflection with no boundary crossing;
/// the emissive high-contrast palette means a seam pixel can legitimately flip its winning material between
/// backends, hence the <c>WorldHighContrast</c> threshold family (the same posture <c>RepeatPolar</c>'s rainbow
/// chain and <c>CellJitter</c>'s scattered field earn).
/// </summary>
internal sealed class WorldSymmetryPlaneStage : IPostStage {
    private const uint WorldHeight = 600;
    private const uint WorldWidth = 960;

    /// <inheritdoc/>
    public string Name => "world-symmetry-plane";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            return PostStageOutcome.Skip(detail: "the cross-backend tier (Direct3D 12) requires Windows 10.0.10240+");
        }

        return RunCore(context: context);
    }

    // A single diagonal-plane fold (normal (1,0,1), offset 0.2) mirroring an asymmetric box+cone+sphere cluster
    // authored on the plane's positive side, comfortably clear of the seam.
    internal static SdfProgram BuildSymmetryPlaneScene() {
        var builder = new SdfProgramBuilder();
        var ground = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.4f, 0.45f, 0.5f)));
        var crimson = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.95f, 0.25f, 0.3f), Emissive: 0.35f));
        var teal = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.25f, 0.85f, 0.8f), Emissive: 0.35f));
        var amber = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.95f, 0.75f, 0.2f), Emissive: 0.35f));

        return builder
            .Plane(normal: Vector3.UnitY, offset: 0f, material: ground)
            // Fold across the diagonal plane normal (1,0,1)/sqrt2, offset 0.2 — a clearly non-axis-aligned plane.
            .SymmetryPlane(normal: new Vector3(1f, 0f, 1f), offset: 0.2f)
            // The asymmetric prototype: a rounded box, a cone, and a sphere at three different offsets, all
            // comfortably on the plane's positive (kept) side — the nearest shape sits ~1.2 units from the plane,
            // far outside its own ~0.2-unit reach.
            .Translate(offset: new Vector3(0.95f, 0.15f, 0.85f))
            .Box(halfExtents: new Vector3(0.18f, 0.1f, 0.12f), round: 0.02f, material: crimson)
            .ResetPoint()
            .SymmetryPlane(normal: new Vector3(1f, 0f, 1f), offset: 0.2f)
            .Translate(offset: new Vector3(1.1f, 0.05f, 0.65f))
            .RoundCone(lowerRadius: 0.1f, upperRadius: 0.03f, height: 0.24f, material: teal)
            .ResetPoint()
            .SymmetryPlane(normal: new Vector3(1f, 0f, 1f), offset: 0.2f)
            .Translate(offset: new Vector3(0.7f, 0.08f, 1.05f))
            .Sphere(radius: 0.1f, material: amber)
            .Build();
    }

    [SupportedOSPlatform("windows10.0.10240")]
    private static PostStageOutcome RunCore(PostContext context) {
        // The emissive box/cone/sphere trio can legitimately flip its winning material at the mirror seam between
        // backends — an isolated large delta that stays ±1-mass, the same WorldHighContrast posture RepeatPolar's
        // rainbow chain and CellJitter's scattered field earn.
        return WorldStage.RunSceneParity(
            context: context,
            prefix: "world-symmetry-plane",
            program: BuildSymmetryPlaneScene(),
            thresholds: ParityThresholds.WorldHighContrast,
            passLabel: $"{WorldWidth}x{WorldHeight} SymmetryPlane diagonal-plane reflection of a box/cone/sphere cluster | Vulkan (SPIR-V) vs Direct3D 12 (DXIL) within WorldHighContrast thresholds"
        );
    }
}
