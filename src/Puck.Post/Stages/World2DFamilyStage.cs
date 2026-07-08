using System.Numerics;
using System.Runtime.Versioning;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-C stage. Cross-backend parity + render proof for the 2D-primitive family — the exact IQ 2D SDFs
/// (RoundedRectangle, RegularPolygon, Star, Trapezoid, Ellipse) lifted to 3D by REVOLVE (a lathe around Y) and
/// EXTRUDE (a prism along Z). One deterministic scene exercises every new shape in BOTH lift modes, an offset revolve
/// (a polygonal ring), and a Subtraction of one new primitive from another (a star punched through a plaque) — so the
/// new lift/decode path, the shared 2D cores, and the cull bounds all render through the identical
/// <see cref="SdfWorldEngine"/> on both backends and agree within the calibrated thresholds.
/// </summary>
internal sealed class World2DFamilyStage : IPostStage {
    private const uint WorldHeight = 600;
    private const uint WorldWidth = 960;

    /// <inheritdoc/>
    public string Name => "world-2d-family";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            return PostStageOutcome.Skip(detail: "the cross-backend tier (Direct3D 12) requires Windows 10.0.10240+");
        }

        return RunCore(context: context);
    }

    // Front row = EXTRUDED prisms (the 2D face turned toward the camera); back row = REVOLVED solids of revolution.
    // Every one of the five 2D cores appears at least once, both lift modes appear, an offset revolve appears (the
    // hex ring), and a Subtraction carves a Star out of a RoundedRectangle (a new primitive as BOTH base and cutter).
    internal static SdfProgram BuildFamilyScene() {
        var builder = new SdfProgramBuilder();
        var ground = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.5f, 0.55f, 0.6f)));
        var slate = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.45f, 0.5f, 0.72f)));
        var brass = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.82f, 0.62f, 0.26f), Specular: 0.5f, Shininess: 48f));
        var gold = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.95f, 0.8f, 0.24f)));
        var coral = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.9f, 0.45f, 0.32f)));
        var lime = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.45f, 0.8f, 0.3f)));
        var violet = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.55f, 0.35f, 0.85f), Specular: 0.6f, Shininess: 64f));
        var cream = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.9f, 0.85f, 0.7f), Emissive: 0.6f));
        var teal = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.2f, 0.7f, 0.68f)));

        return builder
            .Plane(normal: Vector3.UnitY, offset: 0f, material: ground)
            // --- Extruded prisms (front row) ---
            // A plaque with a star punched through it (Subtraction between two new primitives, same chain/centre).
            .ResetPoint()
            .Translate(offset: new Vector3(-2.0f, 0.75f, 0.2f))
            .RoundedRectangle(halfWidth: 0.5f, halfHeight: 0.6f, cornerRadius: 0.12f, lift: SdfLift.Extrude, liftAmount: 0.25f, material: slate)
            .Star(points: 5, radius: 0.34f, sharpness: 2.6f, lift: SdfLift.Extrude, liftAmount: 0.35f, material: cream, blend: SdfBlendOp.Subtraction)
            // A hexagonal nut (regular-polygon prism).
            .ResetPoint()
            .Translate(offset: new Vector3(-0.8f, 0.6f, 0.2f))
            .RegularPolygon(sides: 6, radius: 0.5f, lift: SdfLift.Extrude, liftAmount: 0.28f, material: brass)
            // A star badge.
            .ResetPoint()
            .Translate(offset: new Vector3(0.5f, 0.66f, 0.2f))
            .Star(points: 5, radius: 0.55f, sharpness: 2.6f, lift: SdfLift.Extrude, liftAmount: 0.25f, material: gold)
            // A keystone (trapezoid prism).
            .ResetPoint()
            .Translate(offset: new Vector3(1.75f, 0.6f, 0.2f))
            .Trapezoid(bottomHalfWidth: 0.5f, topHalfWidth: 0.28f, halfHeight: 0.5f, lift: SdfLift.Extrude, liftAmount: 0.28f, material: coral)
            // --- Revolved solids (back row) ---
            // A spheroid (ellipse revolved) — the exact one that earns a cull bound the approximate Ellipsoid forfeits.
            .ResetPoint()
            .Translate(offset: new Vector3(-1.7f, 1.4f, -1.7f))
            .Ellipse(semiX: 0.55f, semiY: 0.82f, lift: SdfLift.Revolve, liftAmount: 0f, material: lime)
            // A cup / frustum (trapezoid revolved).
            .ResetPoint()
            .Translate(offset: new Vector3(-0.3f, 1.35f, -1.7f))
            .Trapezoid(bottomHalfWidth: 0.6f, topHalfWidth: 0.32f, halfHeight: 0.5f, lift: SdfLift.Revolve, liftAmount: 0f, material: violet)
            // A puck (rounded rectangle revolved).
            .ResetPoint()
            .Translate(offset: new Vector3(1.1f, 1.35f, -1.7f))
            .RoundedRectangle(halfWidth: 0.55f, halfHeight: 0.3f, cornerRadius: 0.18f, lift: SdfLift.Revolve, liftAmount: 0f, material: cream)
            // A faceted ring: a hexagon revolved at an OFFSET → a polygonal torus (exercises revolve with offset > 0).
            .ResetPoint()
            .Translate(offset: new Vector3(2.4f, 1.4f, -1.7f))
            .RegularPolygon(sides: 6, radius: 0.24f, lift: SdfLift.Revolve, liftAmount: 0.5f, material: teal)
            .Build();
    }

    [SupportedOSPlatform("windows10.0.10240")]
    private static PostStageOutcome RunCore(PostContext context) {
        return WorldStage.RunSceneParity(
            context: context,
            prefix: "world-2d-family",
            program: BuildFamilyScene(),
            thresholds: ParityThresholds.WorldHighContrast,
            passLabel: $"{WorldWidth}x{WorldHeight} 2D-primitive family (rounded-rect/n-gon/star/trapezoid/ellipse × revolve+extrude) | Vulkan (SPIR-V) vs Direct3D 12 (DXIL) within WorldHighContrast thresholds"
        );
    }
}
