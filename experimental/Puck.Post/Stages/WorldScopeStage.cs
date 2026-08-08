using System.Numerics;
using System.Runtime.Versioning;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-C stage. Cross-backend parity for the SCOPED FIELD ACCUMULATOR (<see cref="SdfOp.PushField"/>/
/// <see cref="SdfOp.PopField"/>): a scene whose accumulator-reading ops are SCOPED, so each acts on its own subtree and
/// composes back far-neutrally — the exact geometry the flat accumulator got wrong.
/// <list type="bullet">
///   <item>A SCOPED INTERSECTION (two boxes intersected inside a <c>PushField</c>/<c>PopField</c>, composed back with a
///   Union) renders as the intersection of its OWN two members and leaves the ground plane and the other clusters intact
///   — under the flat model the trailing intersection annihilated everything it did not overlap (the whole point of the
///   scope). If the scope leaked, the floor would vanish and the cross-backend diff would spike.</item>
///   <item>A SCOPED ONION (a sphere shelled inside a scope) hollows only ITS OWN sphere, not the floor — the flat model's
///   "a field op shells the whole scene" bug.</item>
///   <item>A scope whose FIRST member is a <see cref="SdfBlendOp.SmoothUnion"/> exercises
///   <c>blendSmoothUnion(SDF_FAR_DISTANCE, b, k)</c> — the NEAR-endpoint select that must return <c>b</c> exactly, or the
///   scope detonates at the march origin. Rendering the shell as a shell (not a point at the camera) is the endpoint
///   proof this scene bakes in.</item>
/// </list>
/// The scope introduces sharp intersection/subtraction material seams under emissive lighting, so a boundary pixel can
/// legitimately flip its winning material between backends — the <c>WorldHighContrast</c> posture the other hard-edged
/// world scenes earn. A scoped instance's <c>instanced == flat</c> maskability (the culling payoff — a scoped
/// intersection/onion instance is cullable again) is the instancing system's own invariant, gated where instancing is
/// gated (<c>world-instanced</c>); this stage gates the scope's RENDER.
/// </summary>
internal sealed class WorldScopeStage : IPostStage {
    private const uint WorldHeight = 600;
    private const uint WorldWidth = 960;

    /// <inheritdoc/>
    public string Name => "world-scope";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            return PostStageOutcome.Skip(detail: "the cross-backend tier (Direct3D 12) requires Windows 10.0.10240+");
        }

        return RunCore(context: context);
    }

    // Three clusters over a ground plane, each exercising one facet of the scope. The ground plane is emitted FIRST as a
    // plain Union (world set); every scoped cluster then composes on top of it far-neutrally, so — unlike the flat
    // WorldChamferStage, which had to reorder its intersection to FIRST — a scoped intersection can sit ANYWHERE without
    // deleting the floor. That freedom is the scope's whole product.
    internal static SdfProgram BuildScopeScene() {
        var builder = new SdfProgramBuilder();
        var ground = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.42f, y: 0.46f, z: 0.52f)));
        var copper = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.85f, y: 0.45f, z: 0.15f), Emissive: 0.3f));
        var jade = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.2f, y: 0.8f, z: 0.5f), Emissive: 0.3f));
        var violet = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.6f, y: 0.35f, z: 0.9f), Emissive: 0.3f));

        return builder
            .Plane(normal: Vector3.UnitY, offset: 0f, material: ground)
            // A SCOPED INTERSECTION, emitted AFTER the floor. The scope reseeds the accumulator, so the two boxes
            // intersect ONLY each other (a rounded-cube wedge), and the Union compose melds that wedge onto the scene
            // without touching the floor — the flat model would have annihilated the plane here.
            .ResetPoint()
            .PushField(compose: SdfBlendOp.Union)
            .Translate(offset: new Vector3(x: 1.7f, y: 0.55f, z: 0f))
            .Box(halfExtents: new Vector3(x: 0.45f, y: 0.45f, z: 0.45f), round: 0f, material: violet)
            .ResetPoint()
            .Translate(offset: new Vector3(x: 1.7f, y: 0.55f, z: 0f))
            .Rotate(rotation: Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: (MathF.PI / 4f)))
            .Box(halfExtents: new Vector3(x: 0.45f, y: 0.45f, z: 0.45f), round: 0f, material: violet, blend: SdfBlendOp.Intersection)
            .PopField()
            // A SCOPED ONION whose FIRST member is a SmoothUnion — exercising blendSmoothUnion(FAR, b, k)'s near-endpoint
            // (it must return the sphere, not detonate) — then Onion shells that sphere ALONE. The floor stays solid.
            .ResetPoint()
            .PushField(compose: SdfBlendOp.Union)
            .Translate(offset: new Vector3(x: -1.5f, y: 0.55f, z: 0f))
            .Sphere(radius: 0.5f, material: copper, blend: SdfBlendOp.SmoothUnion, smooth: 0.2f)
            .Onion(thickness: 0.07f)
            .PopField()
            // A SCOPED SUBTRACTION: a box with a cylinder carved out of it inside a scope, composed Union onto the scene.
            .ResetPoint()
            .PushField(compose: SdfBlendOp.Union)
            .Translate(offset: new Vector3(x: 0.1f, y: 0.55f, z: 0f))
            .Box(halfExtents: new Vector3(x: 0.45f, y: 0.4f, z: 0.45f), round: 0.05f, material: jade)
            .ResetPoint()
            .Translate(offset: new Vector3(x: 0.1f, y: 0.55f, z: 0f))
            .Rotate(rotation: Quaternion.CreateFromAxisAngle(axis: Vector3.UnitX, angle: (MathF.PI / 2f)))
            .Cylinder(radius: 0.18f, halfHeight: 0.7f, material: jade, blend: SdfBlendOp.Subtraction)
            .PopField()
            .Build();
    }

    [SupportedOSPlatform("windows10.0.10240")]
    private static PostStageOutcome RunCore(PostContext context) {
        return WorldStage.RunSceneParity(
            context: context,
            prefix: "world-scope",
            program: BuildScopeScene(),
            thresholds: ParityThresholds.WorldHighContrast,
            passLabel: $"{WorldWidth}x{WorldHeight} scoped intersection/onion/subtraction over an intact floor | Vulkan (SPIR-V) vs Direct3D 12 (DXIL) within WorldHighContrast thresholds"
        );
    }
}
