using System.Numerics;
using System.Runtime.Versioning;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-C stage. Cross-backend parity for the ANGULAR DOMAIN-REPEAT fold (<see cref="SdfOp.RepeatPolar"/>, the
/// rotational sibling of <c>Repeat</c>/<c>WallpaperFold</c>): a "rainbow rotunda" chain (10 sectors, mirror OFF,
/// materialStride 1) recolors each sector from its own emissive row, and a "kaleidoscope" chain (8 sectors, mirror
/// ON, geometric only) reflects a single off-bisector cone into a mirrored pair per sector — so a wrong sector
/// count/reciprocal relocates a column, a flipped mirror bit collapses a pair down to one cone, and a mis-strided
/// material recolors the wrong sector, all on one backend and not the other. Both fold branches (the rotation into
/// the base sector and the optional bisector reflection) are ISOMETRIES — like <c>Repeat</c>/<c>WallpaperFold</c>,
/// there is no step clamp and no solidity gate. atan2/floor are floats, so a sector-seam pixel carries the usual
/// ±1-LSB warp noise, and — exactly as <c>WallpaperFold</c>'s cell parity can — the per-sector material can flip
/// fully at a seam; the rainbow chain's emissive palette makes that flip a high-contrast winner swap, hence the
/// <c>WorldHighContrast</c> threshold family. Both prototypes are authored well clear of their sector walls (the
/// same authoring rule as <c>Repeat</c>), so every fold branch stays an exact isometry with no boundary crossing.
/// </summary>
internal sealed class WorldRepeatPolarStage : IPostStage {
    private const uint WorldHeight = 600;
    private const uint WorldWidth = 960;

    /// <inheritdoc/>
    public string Name => "world-repeat-polar";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            return PostStageOutcome.Skip(detail: "the cross-backend tier (Direct3D 12) requires Windows 10.0.10240+");
        }

        return RunCore(context: context);
    }

    // Two independent rings side by side, exercising RepeatPolar's default axis (Y, the XZ ground plane), both the
    // mirrored and unmirrored fold, and both the geometric-only and material-strided recolor paths.
    internal static SdfProgram BuildRepeatPolarScene() {
        var builder = new SdfProgramBuilder();
        var ground = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.42f, y: 0.46f, z: 0.52f)));

        // Chain 1's 10 CONTIGUOUS emissive rows: the shape names only the first (rainbowBase); materialStride 1
        // reaches the 9 that follow — the only path to them, exactly as CellJitter's hashed variants reach lime/azure.
        const int RainbowCount = 10;
        var rainbowBase = 0;

        for (var i = 0; (i < RainbowCount); i++) {
            var hue = (i * ((2f * MathF.PI) / RainbowCount));
            var color = new Vector3(
                x: (0.5f + (0.45f * MathF.Cos(x: hue))),
                y: (0.5f + (0.45f * MathF.Cos(x: (hue - 2.0944f)))),
                z: (0.5f + (0.45f * MathF.Cos(x: (hue - 4.1888f))))
            );
            var row = builder.AddMaterial(material: new SdfMaterial(Albedo: color, Emissive: 0.3f));

            if (i == 0) { rainbowBase = row; }
        }

        var gold = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.9f, y: 0.75f, z: 0.25f), Emissive: 0.3f));

        return builder
            .Plane(normal: Vector3.UnitY, offset: 0f, material: ground)
            // Chain 1: ring center at world (-1.8, 0, 0). The asymmetric round box sits ON the bisector (a = 0) at
            // radius 1.0; its 0.07 tangential half-extent subtends ~5° there, far inside the 10-sector 18° half-angle.
            .Translate(offset: new Vector3(x: -1.8f, y: 0f, z: 0f))
            .RepeatPolar(count: RainbowCount, axis: SdfPolarAxis.Y, mirror: false, materialStride: 1)
            .Translate(offset: new Vector3(x: 1.0f, y: 0.1f, z: 0f))
            .Box(halfExtents: new Vector3(x: 0.16f, y: 0.09f, z: 0.07f), round: 0.02f, material: rainbowBase)
            // Chain 2: ring center at world (1.8, 0, 0). The cone sits OFF the bisector (~9° at radius ~0.87) so its
            // mirrored twin (the fold reflects it back across a = 0) lands ~9° to the other side of the bisector —
            // both wells inside the 8-sector 22.5° half-angle, so the mirrored pair never approaches a wall.
            .ResetPoint()
            .Translate(offset: new Vector3(x: 1.8f, y: 0f, z: 0f))
            .RepeatPolar(count: 8, axis: SdfPolarAxis.Y, mirror: true)
            .Translate(offset: new Vector3(x: 0.86f, y: 0.02f, z: 0.135f))
            .RoundCone(lowerRadius: 0.12f, upperRadius: 0.04f, height: 0.28f, material: gold)
            .Build();
    }

    [SupportedOSPlatform("windows10.0.10240")]
    private static PostStageOutcome RunCore(PostContext context) {
        // The rainbow chain recolors every sector from its own emissive row, so a sector-seam pixel can legitimately
        // flip its winning material between backends — an isolated large delta that stays ±1-mass, the same
        // WorldHighContrast posture CellJitter's scattered emissive field earns.
        return WorldStage.RunSceneParity(
            context: context,
            prefix: "world-repeat-polar",
            program: BuildRepeatPolarScene(),
            thresholds: ParityThresholds.WorldHighContrast,
            passLabel: $"{WorldWidth}x{WorldHeight} RepeatPolar rainbow rotunda (10 sectors, materialStride 1) + mirrored kaleidoscope (8 sectors) | Vulkan (SPIR-V) vs Direct3D 12 (DXIL) within WorldHighContrast thresholds"
        );
    }
}
