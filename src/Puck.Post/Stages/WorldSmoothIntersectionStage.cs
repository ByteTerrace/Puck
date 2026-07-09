using System.Numerics;
using System.Runtime.Versioning;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-C stage. Cross-backend parity for the <see cref="SdfBlendOp.SmoothIntersection"/> blend — the one intersection
/// variant with a soft seam, and (until now) load-bearing NOWHERE in POST. Intersection is the accumulator's most
/// dangerous op: <c>max(accumulator, candidate)</c> returns the candidate everywhere OUTSIDE its own shape, so an
/// unscoped smooth-intersection annihilates every earlier shape it does not overlap (the ground plane included). This
/// scene therefore SCOPES each smooth-intersection (<see cref="SdfProgramBuilder.PushField"/>/
/// <see cref="SdfProgramBuilder.PopField"/>, composed back with a Union) so it acts on its OWN pair alone, exactly the
/// WorldScopeStage pattern:
/// <list type="bullet">
///   <item>A LENS/PILLOW — two spheres of DISTINCT materials smooth-intersected, offset along X so their overlap is a
///   pointed lens with a rounded (filleted) rim rather than the knife edge a plain <see cref="SdfBlendOp.Intersection"/>
///   would leave. The material-ownership seam runs down the lens: a wrong smooth-intersection radius or a sign slip
///   moves that seam and the silhouette on one backend and not the other.</item>
///   <item>A CUSHIONED CUBE — a box smooth-intersected with a sphere of the same material: the sphere rounds the cube
///   down to a superquadric-like cushion, isolating the smooth seam's geometry from any material flip.</item>
/// </list>
/// The lens carries a material-ownership boundary under emissive lighting (a boundary pixel can legitimately flip its
/// winning material between backends), so the diff judges under <c>WorldHighContrast</c> — the same posture the scope,
/// chamfer, and menagerie intersection scenes earn.
/// </summary>
internal sealed class WorldSmoothIntersectionStage : IPostStage {
    private const uint WorldHeight = 600;
    private const uint WorldWidth = 960;

    /// <inheritdoc/>
    public string Name => "world-smooth-intersection";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            return PostStageOutcome.Skip(detail: "the cross-backend tier (Direct3D 12) requires Windows 10.0.10240+");
        }

        return RunCore(context: context);
    }

    // The ground plane is emitted FIRST as a plain Union (world set); each SCOPED smooth-intersection then composes on
    // top of it far-neutrally, so — unlike a flat trailing intersection, which would delete the floor — the pillow and
    // the cushion can sit anywhere without annihilating the scene. That scope freedom is what lets a smooth-intersection
    // be load-bearing at all here.
    internal static SdfProgram BuildSmoothIntersectionScene() {
        var builder = new SdfProgramBuilder();
        var ground = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.42f, 0.46f, 0.52f)));
        var copper = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.85f, 0.45f, 0.15f), Emissive: 0.3f));
        var jade = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.2f, 0.8f, 0.5f), Emissive: 0.3f));
        var violet = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.6f, 0.35f, 0.9f), Emissive: 0.3f));

        return builder
            .Plane(normal: Vector3.UnitY, offset: 0f, material: ground)
            // The LENS/PILLOW: sphere A (copper) SEEDS the scope, sphere B (jade) smooth-intersects it. The two centres
            // are 0.5 apart in X, so the overlap is a pointed lens; the 0.18 smooth radius fillets the rim into a pillow.
            // Distinct materials put an ownership seam down the lens — the feature a wrong seam would move.
            .ResetPoint()
            .PushField(compose: SdfBlendOp.Union)
            .Translate(offset: new Vector3(-1.75f, 0.9f, 0f))
            .Sphere(radius: 0.62f, material: copper)
            .ResetPoint()
            .Translate(offset: new Vector3(-1.25f, 0.9f, 0f))
            .Sphere(radius: 0.62f, material: jade, blend: SdfBlendOp.SmoothIntersection, smooth: 0.18f)
            .PopField()
            // The CUSHIONED CUBE: a box (violet) SEEDS the scope, a sphere smooth-intersects it down to a cushion —
            // same material, so this cluster isolates the smooth seam's GEOMETRY (the rounded-down edges) from any flip.
            .ResetPoint()
            .PushField(compose: SdfBlendOp.Union)
            .Translate(offset: new Vector3(1.6f, 0.62f, 0f))
            .Box(halfExtents: new Vector3(0.5f, 0.5f, 0.5f), round: 0f, material: violet)
            .ResetPoint()
            .Translate(offset: new Vector3(1.6f, 0.62f, 0f))
            .Sphere(radius: 0.62f, material: violet, blend: SdfBlendOp.SmoothIntersection, smooth: 0.2f)
            .PopField()
            .Build();
    }

    [SupportedOSPlatform("windows10.0.10240")]
    private static PostStageOutcome RunCore(PostContext context) {
        // The lens's distinct-material overlap is a sharp material-ownership seam under emissive lighting, so a boundary
        // pixel can legitimately flip its winning material between backends — the WorldHighContrast posture the other
        // intersection-family world scenes (scope, chamfer, menagerie) earn.
        return WorldStage.RunSceneParity(
            context: context,
            prefix: "world-smooth-intersection",
            program: BuildSmoothIntersectionScene(),
            thresholds: ParityThresholds.WorldHighContrast,
            passLabel: $"{WorldWidth}x{WorldHeight} scoped smooth-intersection lens + cushioned cube over an intact floor | Vulkan (SPIR-V) vs Direct3D 12 (DXIL) within WorldHighContrast thresholds"
        );
    }
}
