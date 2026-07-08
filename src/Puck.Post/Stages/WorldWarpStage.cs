using System.Numerics;
using System.Runtime.Versioning;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-C stage. Cross-backend parity for the WARP ops (twist/bend/elongate), the FIELD op (onion), and the
/// three newer blends (xor, smooth-intersection, smooth-subtraction): a twisted box column, a bent capsule arch, an
/// elongated sphere (the capsule-by-elongation classic), an onioned sphere cut open by a subtraction box (showing the
/// shell), an xor'd sphere pair (hollow where they overlap), and a smooth-subtraction carve. The warps put sin/cos
/// into the differential path and are not isometries, so the diff judges under <c>WorldLsbExact</c> — the every-delta-
/// exactly-±1 signature that survives codegen redistribution.
/// </summary>
internal sealed class WorldWarpStage : IPostStage {
    private const uint WorldHeight = 600;
    private const uint WorldWidth = 960;

    /// <inheritdoc/>
    public string Name => "world-warp";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            return PostStageOutcome.Skip(detail: "the cross-backend tier (Direct3D 12) requires Windows 10.0.10240+");
        }

        return RunCore(context: context);
    }

    internal static SdfProgram BuildWarpScene() {
        var builder = new SdfProgramBuilder();
        var ground = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.5f, 0.52f, 0.58f)));
        var brick = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.8f, 0.35f, 0.25f)));
        var teal = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.2f, 0.7f, 0.7f)));
        var honey = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.9f, 0.7f, 0.3f)));
        var slate = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.4f, 0.45f, 0.6f)));

        return builder
            // The ONIONED SPHERE GOES FIRST, against the empty accumulator. Onion is a FIELD op: it rewrites the running
            // distance as abs(d) - t, so it shells EVERY solid accumulated so far, not just the shape before it. Emitted
            // where it used to sit (after the plane and two other solids) it moved every outer surface out by 0.06 and
            // hollowed the lot — the ground plane became a 0.12-thick slab, the twisted column and the bent capsule grew
            // 0.06 fatter. Nothing looked wrong, because an onion's failure mode is INFLATION, not deletion. Emitted
            // first, it shells exactly its own sphere; the Subtraction box then carves an opening that reveals the shell.
            .Translate(offset: new Vector3(2.6f, 0.8f, -0.4f))
            .Sphere(radius: 0.65f, material: slate)
            .Onion(thickness: 0.06f)
            .ResetPoint()
            .Translate(offset: new Vector3(2.6f, 1.3f, -0.4f))
            .Box(halfExtents: new Vector3(0.8f, 0.5f, 0.8f), round: 0f, material: honey, blend: SdfBlendOp.Subtraction)
            // Everything below unions on top of the finished shell.
            .ResetPoint()
            .Plane(normal: Vector3.UnitY, offset: 0f, material: ground)
            // A twisted box column (the twist rotates its cross-section along Y).
            .ResetPoint()
            .Translate(offset: new Vector3(-2.6f, 1.0f, 0.0f))
            .TwistY(rate: 1.5f)
            .Box(halfExtents: new Vector3(0.45f, 1.0f, 0.45f), round: 0.06f, material: brick)
            // A bent capsule arch.
            .ResetPoint()
            .Translate(offset: new Vector3(-0.9f, 0.35f, -0.9f))
            .BendX(rate: 0.9f)
            .Capsule(endpoint: new Vector3(1.2f, 0.0f, 0.0f), radius: 0.28f, material: teal)
            // The classic: a sphere elongated into a rounded bar.
            .ResetPoint()
            .Translate(offset: new Vector3(0.9f, 0.32f, 1.1f))
            .Elongate(extents: new Vector3(0.55f, 0.0f, 0.15f))
            .Sphere(radius: 0.3f, material: honey)
            // An XOR pair: solid where exactly one sphere is, hollow in the lens where both are. Xor also composes
            // against the WHOLE accumulator, not the sphere before it — only ONE cluster in a flat-accumulator program
            // can own a clean accumulator, and here that is the onion. This pair reads correctly anyway because the teal
            // sphere (y = 1.9, r = 0.42) overlaps nothing but its brick partner; slide it into the ground and it would
            // punch a hole there. That placement dependence is the cost of a global op, not a property of Xor.
            .ResetPoint()
            .Translate(offset: new Vector3(-0.2f, 1.9f, 0.6f))
            .Sphere(radius: 0.42f, material: brick)
            .ResetPoint()
            .Translate(offset: new Vector3(0.25f, 1.9f, 0.6f))
            .Sphere(radius: 0.42f, material: teal, blend: SdfBlendOp.Xor)
            // A smooth-subtraction carve: a filleted scoop out of a box. Subtraction composes against the whole
            // accumulator too, but it is LOCAL by construction — max(acc, -candidate) only bites inside the subtrahend —
            // so it may sit anywhere.
            .ResetPoint()
            .Translate(offset: new Vector3(1.0f, 0.45f, -1.6f))
            .Box(halfExtents: new Vector3(0.55f, 0.45f, 0.55f), round: 0.05f, material: slate)
            .ResetPoint()
            .Translate(offset: new Vector3(1.0f, 1.1f, -1.6f))
            .Sphere(radius: 0.5f, material: honey, blend: SdfBlendOp.SmoothSubtraction, smooth: 0.15f)
            .Build();
    }

    [SupportedOSPlatform("windows10.0.10240")]
    private static PostStageOutcome RunCore(PostContext context) {
        // The warps put sin/cos into the differential path and are not isometries, so the diff judges under
        // WorldLsbExact — the every-delta-exactly-±1 signature that survives codegen redistribution.
        return WorldStage.RunSceneParity(
            context: context,
            prefix: "world-warp",
            program: BuildWarpScene(),
            thresholds: ParityThresholds.WorldLsbExact,
            passLabel: $"{WorldWidth}x{WorldHeight} twist/bend/elongate warps + onion field ops + xor/smooth blends | Vulkan (SPIR-V) vs Direct3D 12 (DXIL) within WorldLsbExact thresholds"
        );
    }
}
