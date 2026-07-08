using System.Numerics;
using System.Runtime.Versioning;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-C stage. Cross-backend parity for the CHAMFER blends (<see cref="SdfBlendOp.ChamferUnion"/>,
/// <see cref="SdfBlendOp.ChamferIntersection"/>, <see cref="SdfBlendOp.ChamferSubtraction"/>): a flat 45° bevel plane
/// (hg_sdf's <c>(a ± r + b) * sqrt(0.5)</c>) instead of the smooth blends' round fillet. A compact three-shape cluster
/// exercises all three in one scene — a box <c>ChamferUnion</c> a sphere (a beveled weld collar), a box with a
/// perpendicular cylinder <c>ChamferSubtraction</c>-carved through it (a beveled trench, not a rounded scoop), and two
/// boxes (one rotated 45° about Y) <c>ChamferIntersection</c>'d (a hard-chamfered wedge, not a rounded lens) — so a
/// wrong bevel-plane sign or a missing √2 Lipschitz factor shows as a flat-seam silhouette or gradient error on one
/// backend and not the other. The bevel is MILDLY non-1-Lipschitz (a clamped √2 factor — <c>SdfProgram.AnalyzeLipschitz</c>),
/// so a sharp material seam under emissive lighting is this scene's signature, hence the <c>WorldHighContrast</c>
/// threshold family (the same posture the other hard-edged/high-contrast world scenes earn).
/// </summary>
internal sealed class WorldChamferStage : IPostStage {
    private const uint WorldHeight = 600;
    private const uint WorldWidth = 960;

    /// <inheritdoc/>
    public string Name => "world-chamfer";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            return PostStageOutcome.Skip(detail: "the cross-backend tier (Direct3D 12) requires Windows 10.0.10240+");
        }

        return RunCore(context: context);
    }

    // Three clusters, each isolating one chamfer blend so its flat bevel plane is unambiguous against the round fillet
    // a SmoothUnion/-Intersection/-Subtraction would produce at the same radius.
    //
    // EMISSION ORDER IS LOAD-BEARING. mapCore carries ONE running accumulator for the whole program — ResetPoint resets
    // the evaluation POINT, never result.distance — so every blend composes against everything emitted before it. Union
    // (a min) and subtraction (a max against the NEGATED candidate, which only bites inside the subtrahend) are LOCAL and
    // may appear anywhere. An INTERSECTION is not: max(accumulator, candidate) returns the candidate wherever the
    // candidate is farther, i.e. everywhere outside its own shape, so it annihilates every earlier shape it does not
    // overlap. Emitted LAST — as this scene originally was — the violet ChamferIntersection deleted the ground plane, the
    // copper weld and the jade trench, and the stage rendered a lone wedge on an empty background while its own summary
    // claimed three clusters (the giveaway was a 2-pixel cross-backend diff, an order of magnitude below every other
    // world stage). The intersection cluster therefore goes FIRST, against the empty SDF_FAR_DISTANCE accumulator, so it
    // intersects exactly its own two boxes; the plane and the remaining clusters then union on top of the finished wedge.
    internal static SdfProgram BuildChamferScene() {
        var builder = new SdfProgramBuilder();
        var ground = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.42f, 0.46f, 0.52f)));
        var copper = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.85f, 0.45f, 0.15f), Emissive: 0.3f));
        var jade = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.2f, 0.8f, 0.5f), Emissive: 0.3f));
        var violet = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.6f, 0.35f, 0.9f), Emissive: 0.3f));

        return builder
            // The beveled wedge FIRST: two boxes (the second rotated 45° about Y) ChamferIntersection'd. Against the
            // empty accumulator the intersection sees only its own pair.
            .Translate(offset: new Vector3(1.7f, 0.5f, 0f))
            .Box(halfExtents: new Vector3(0.42f, 0.42f, 0.42f), round: 0f, material: violet)
            .ResetPoint()
            .Translate(offset: new Vector3(1.7f, 0.5f, 0f))
            .Rotate(rotation: Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: (MathF.PI / 4f)))
            .Box(halfExtents: new Vector3(0.42f, 0.42f, 0.42f), round: 0f, material: violet, blend: SdfBlendOp.ChamferIntersection, smooth: 0.16f)
            .ResetPoint()
            .Plane(normal: Vector3.UnitY, offset: 0f, material: ground)
            // The beveled weld: a box ChamferUnion a sphere — a flat 45° collar where the round meets the cube.
            .ResetPoint()
            .Translate(offset: new Vector3(-1.5f, 0.5f, 0f))
            .Box(halfExtents: new Vector3(0.4f, 0.4f, 0.4f), round: 0f, material: copper)
            .Sphere(radius: 0.38f, material: copper, blend: SdfBlendOp.ChamferUnion, smooth: 0.2f)
            // The beveled trench: a cylinder ChamferSubtraction-carved through a box. Its axis is rotated onto world Z
            // (front-to-back) rather than X, so the bevelled rim of the bore FACES the camera — a gate must be able to
            // see the feature it names.
            .ResetPoint()
            .Translate(offset: new Vector3(0.1f, 0.5f, 0f))
            .Box(halfExtents: new Vector3(0.45f, 0.35f, 0.45f), round: 0f, material: jade)
            .ResetPoint()
            .Translate(offset: new Vector3(0.1f, 0.5f, 0f))
            .Rotate(rotation: Quaternion.CreateFromAxisAngle(axis: Vector3.UnitX, angle: (MathF.PI / 2f)))
            .Cylinder(radius: 0.18f, halfHeight: 0.6f, material: jade, blend: SdfBlendOp.ChamferSubtraction, smooth: 0.14f)
            .Build();
    }

    [SupportedOSPlatform("windows10.0.10240")]
    private static PostStageOutcome RunCore(PostContext context) {
        // Every chamfer is a sharp material boundary (a flat bevel plane, not a soft fillet), so a boundary pixel can
        // legitimately flip its winning material between backends — the same WorldHighContrast posture the other
        // hard-edged/emissive world scenes earn.
        return WorldStage.RunSceneParity(
            context: context,
            prefix: "world-chamfer",
            program: BuildChamferScene(),
            thresholds: ParityThresholds.WorldHighContrast,
            passLabel: $"{WorldWidth}x{WorldHeight} ChamferUnion/ChamferIntersection/ChamferSubtraction cluster | Vulkan (SPIR-V) vs Direct3D 12 (DXIL) within WorldHighContrast thresholds"
        );
    }
}
