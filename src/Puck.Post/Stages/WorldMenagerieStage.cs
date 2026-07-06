using System.Numerics;
using System.Runtime.Versioning;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-C stage. Cross-backend parity for the NEWER shape primitives — capsule, cylinder, and ellipsoid — which the
/// hero scene (deliberately frozen; the gpu-budget stage times it) never exercises. The SAME deterministic scene — a
/// ground plane, a leaning capsule, an upright cylinder with a capsule SUBTRACTED through it, an eccentric ellipsoid
/// smooth-blended into the ground, and a small sphere-cylinder smooth pair — renders through the identical
/// <see cref="SdfWorldEngine"/> on both backends and must agree within the calibrated <c>WorldComposite</c>
/// thresholds. Together with the fuzz stage's widened 7-shape generator this makes every ISA shape data-verified.
/// </summary>
internal sealed class WorldMenagerieStage : IPostStage {
    private const uint WorldHeight = 600;
    private const uint WorldWidth = 960;

    /// <inheritdoc/>
    public string Name => "world-menagerie";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            return PostStageOutcome.Skip(detail: "the cross-backend tier (Direct3D 12) requires Windows 10.0.10240+");
        }

        return RunCore(context: context);
    }

    // The menagerie: every shape the hero scene lacks, with blend variety over them (smooth-union into the ground,
    // a hard subtraction THROUGH a cylinder, a smooth capsule-sphere pair) so the parity diff covers the new SDFs'
    // whole silhouette + gradient space, not just their happy path. It is ALSO the materials-v2 gate: the violet
    // cylinder carries a Blinn-Phong highlight (pow enters the cross-backend diff path) and the cream pair glows
    // (the emissive lift), so both new material channels render on both backends under the same thresholds.
    internal static SdfProgram BuildMenagerieScene() {
        var builder = new SdfProgramBuilder();
        var ground = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.5f, 0.55f, 0.6f)));
        var coral = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.9f, 0.4f, 0.3f)));
        var violet = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.5f, 0.3f, 0.85f), Specular: 0.6f, Shininess: 64f));
        var lime = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.45f, 0.8f, 0.25f)));
        var cream = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.9f, 0.85f, 0.7f), Emissive: 1.2f));

        return builder
            .Plane(normal: Vector3.UnitY, offset: 0f, material: ground)
            // A leaning capsule: the segment endpoint exercises the off-axis distance math.
            .Translate(offset: new Vector3(-2.0f, 0.35f, 0.2f))
            .Capsule(endpoint: new Vector3(0.7f, 1.3f, -0.3f), radius: 0.3f, material: coral)
            // An upright cylinder with a capsule bored THROUGH it (hard subtraction: interior surfaces + rim edges).
            .ResetPoint()
            .Translate(offset: new Vector3(0.1f, 0.8f, 0.5f))
            .Cylinder(radius: 0.65f, halfHeight: 0.8f, material: violet)
            .ResetPoint()
            .Translate(offset: new Vector3(0.1f, 0.8f, 0.5f))
            .Capsule(endpoint: new Vector3(0f, 1.8f, 0f), radius: 0.28f, material: cream, blend: SdfBlendOp.Subtraction)
            // An eccentric ellipsoid smooth-blended into the ground (the first-order approximation's soft limbs).
            .ResetPoint()
            .Translate(offset: new Vector3(2.1f, 0.4f, -0.4f))
            .Ellipsoid(radii: new Vector3(1.0f, 0.45f, 0.7f), material: lime, blend: SdfBlendOp.SmoothUnion, smooth: 0.3f)
            // A small smooth capsule-sphere pair floating behind (soft blend between two curved fields).
            .ResetPoint()
            .Translate(offset: new Vector3(-0.3f, 1.9f, -1.5f))
            .Sphere(radius: 0.35f, material: cream)
            .ResetPoint()
            .Translate(offset: new Vector3(-0.6f, 1.6f, -1.5f))
            .Capsule(endpoint: new Vector3(0.7f, 0.5f, 0f), radius: 0.2f, material: coral, blend: SdfBlendOp.SmoothUnion, smooth: 0.25f)
            .Build();
    }

    [SupportedOSPlatform("windows10.0.10240")]
    private static PostStageOutcome RunCore(PostContext context) {
        // WorldHighContrast, not WorldComposite: the emissive pair's brightness step makes benign boundary-winner
        // flips (isolated, multi-LSB) part of this scene's signature — see the threshold set's doc.
        return WorldStage.RunSceneParity(
            context: context,
            prefix: "world-menagerie",
            program: BuildMenagerieScene(),
            thresholds: ParityThresholds.WorldHighContrast,
            passLabel: $"{WorldWidth}x{WorldHeight} capsule/cylinder/ellipsoid menagerie + emissive/specular materials | Vulkan (SPIR-V) vs Direct3D 12 (DXIL) within WorldHighContrast thresholds"
        );
    }
}
