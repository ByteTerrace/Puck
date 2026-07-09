using System.Numerics;
using System.Runtime.Versioning;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-C stage. Cross-backend parity for the two bend ops WorldWarpStage does NOT cover — <see cref="SdfOp.BendY"/> and
/// <see cref="SdfOp.BendZ"/> (that stage gates <see cref="SdfOp.BendX"/> + <see cref="SdfOp.TwistY"/>). The three bends
/// are DISTINCT ops with DISTINCT keyed axes, not a symmetric family, so each needs its own coverage: BendY keys on y and
/// rotates the XY plane, while BendZ keys on y (the deliberately-kept QUIRK — it does NOT key on z) and rotates the YZ
/// plane. The scene makes the distinction load-bearing:
/// <list type="bullet">
///   <item>A BendY box column (tall in Y): the XY-plane rotation bends it sideways in X as height increases.</item>
///   <item>A BendZ box column (also tall in Y): because BendZ keys on y, the SAME vertical extent drives a YZ-plane
///   rotation, bending it front-to-back in Z. A build that mis-keyed BendZ on z would leave this Y-tall column
///   dead straight — the render would visibly disagree, and the cross-backend diff would still have to match whatever
///   each backend produced, so a keyed-axis divergence between SPIR-V and DXIL codegen is exactly what this gates.</item>
///   <item>A BendY capsule arch (the thin companion): a vertical capsule bent in X, whose <c>1 + rate</c> bend
///   operator norm (<c>SdfProgram.BendOperatorNorm</c>) is the largest here — if that Lipschitz factor were wrong the
///   thin capsule would be the first to hole, so its clean silhouette on both backends is the norm's witness.</item>
/// </list>
/// The bends put sin/cos into the differential path and are not isometries (exactly like WorldWarpStage's twist/bend),
/// so the diff judges under <c>WorldLsbExact</c> — the every-delta-exactly-±1 signature that survives codegen
/// redistribution as the VM's opcode set grows.
/// </summary>
internal sealed class WorldBendStage : IPostStage {
    private const uint WorldHeight = 600;
    private const uint WorldWidth = 960;

    /// <inheritdoc/>
    public string Name => "world-bend";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            return PostStageOutcome.Skip(detail: "the cross-backend tier (Direct3D 12) requires Windows 10.0.10240+");
        }

        return RunCore(context: context);
    }

    // Every cluster unions onto the accumulator (bends are POINT ops applied to the shape that follows, not accumulator
    // reads), so emission order is free — unlike the intersection scenes there is no annihilation to sequence around.
    internal static SdfProgram BuildBendScene() {
        var builder = new SdfProgramBuilder();
        var ground = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.5f, 0.52f, 0.58f)));
        var brick = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.8f, 0.35f, 0.25f)));
        var teal = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.2f, 0.7f, 0.7f)));
        var honey = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.9f, 0.7f, 0.3f)));

        return builder
            .Plane(normal: Vector3.UnitY, offset: 0f, material: ground)
            // A BendY box column, tall in Y: the XY-plane rotation (rate · y) bends it sideways in X up its height.
            .ResetPoint()
            .Translate(offset: new Vector3(-2.4f, 1.0f, 0f))
            .BendY(rate: 1.1f)
            .Box(halfExtents: new Vector3(0.4f, 1.0f, 0.4f), round: 0.06f, material: brick)
            // A BendZ box column, ALSO tall in Y: BendZ keys on y (the quirk), so the YZ-plane rotation bends this
            // Y-tall column front-to-back in Z — the render a z-keyed mis-implementation would leave straight.
            .ResetPoint()
            .Translate(offset: new Vector3(0.1f, 1.0f, 0f))
            .BendZ(rate: 1.1f)
            .Box(halfExtents: new Vector3(0.4f, 1.0f, 0.4f), round: 0.06f, material: teal)
            // A BendY capsule arch: a vertical capsule bent in X — the thin companion whose 1 + rate operator norm is
            // the largest of the three shapes here, so a wrong bend Lipschitz factor would hole it first.
            .ResetPoint()
            .Translate(offset: new Vector3(2.3f, 0.35f, -0.6f))
            .BendY(rate: 0.9f)
            .Capsule(endpoint: new Vector3(0f, 1.2f, 0f), radius: 0.28f, material: honey)
            .Build();
    }

    [SupportedOSPlatform("windows10.0.10240")]
    private static PostStageOutcome RunCore(PostContext context) {
        // The bends put sin/cos into the differential path and are not isometries, so the diff judges under WorldLsbExact
        // — the every-delta-exactly-±1 signature that survives codegen redistribution (the same posture WorldWarpStage's
        // twist/bend earn).
        return WorldStage.RunSceneParity(
            context: context,
            prefix: "world-bend",
            program: BuildBendScene(),
            thresholds: ParityThresholds.WorldLsbExact,
            passLabel: $"{WorldWidth}x{WorldHeight} BendY + BendZ (y-keyed quirk) box columns + a bent capsule | Vulkan (SPIR-V) vs Direct3D 12 (DXIL) within WorldLsbExact thresholds"
        );
    }
}
