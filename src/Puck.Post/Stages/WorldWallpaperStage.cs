using Puck.Capture;
using System.Numerics;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Gpu;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-C stage. Cross-backend parity for the WALLPAPER FOLD (<see cref="SdfOp.WallpaperFold"/>): one square-lattice
/// chain (P4G — quarter-turns + off-center mirrors, the branch-heaviest square group) and one hex-lattice chain
/// (P6M — the full hex kaleidoscope), each folding a deliberately ASYMMETRIC motif with a parity-material stride, so
/// a single flipped mirror, mis-rotated turn, or trunc-vs-floor modulo mis-coloring changes pixels on one backend
/// and trips the diff. Motifs are authored well clear of cell boundaries and rotation seams (the same authoring rule
/// as <c>Repeat</c>), so every fold branch is an exact isometry and the diff stays within the ±1 signature.
/// </summary>
internal sealed class WorldWallpaperStage : IPostStage {
    private const uint WorldHeight = 600;
    private const uint WorldWidth = 960;

    /// <inheritdoc/>
    public string Name => "world-wallpaper";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            return PostStageOutcome.Skip(detail: "the cross-backend tier (Direct3D 12) requires Windows 10.0.10240+");
        }

        return RunCore(context: context);
    }

    // Two lattice families side by side. Each motif: a round-cone + off-center sphere pair — no rotational or mirror
    // self-symmetry, so a wrong fold VISIBLY relocates it. The stride recolors cells by parity (square: checker over
    // 2 palette rows; hex: the 3-coloring over 3 rows), so the floor-mod cell keys are also under test.
    internal static SdfProgram BuildWallpaperScene() {
        var builder = new SdfProgramBuilder();
        var ground = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.45f, 0.5f, 0.55f)));
        var rose = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.9f, 0.35f, 0.45f)));
        // The sky row is reached ONLY through the square chain's checker stride (rose + key 1) — never named.
        _ = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.3f, 0.6f, 0.9f)));
        var gold = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.9f, 0.75f, 0.25f)));
        // The mint/plum rows are reached ONLY through the hex chain's parity stride (gold + key 1/2) — never named.
        _ = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.4f, 0.85f, 0.6f)));
        _ = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.65f, 0.35f, 0.8f)));

        return builder
            .Plane(normal: Vector3.UnitY, offset: 0f, material: ground)
            // Square-lattice chain: P4G over a 0.8-unit cell, 2-cell limit, checker stride over rose/sky. The motif
            // (an off-center leaning cone + a smaller sphere) sits ~0.2 units from every boundary/seam.
            .Translate(offset: new Vector3(-2.2f, 0f, 0.6f))
            .WallpaperFold(group: SdfWallpaperGroup.P4G, cell: new Vector2(0.8f, 0.8f), limit: new Vector2(2f, 2f), materialStride: 1)
            .Translate(offset: new Vector3(0.06f, -0.02f, 0.1f))
            .RoundCone(lowerRadius: 0.14f, upperRadius: 0.05f, height: 0.3f, material: rose)
            .ResetPoint()
            .Translate(offset: new Vector3(-2.2f, 0f, 0.6f))
            .WallpaperFold(group: SdfWallpaperGroup.P4G, cell: new Vector2(0.8f, 0.8f), limit: new Vector2(2f, 2f), materialStride: 1)
            .Translate(offset: new Vector3(-0.1f, 0.08f, -0.06f))
            .Sphere(radius: 0.08f, material: rose)
            // Hex-lattice chain: P6M over a 0.9-unit pitch, 2-axial-cell limit, 3-coloring stride over gold/mint/plum.
            .ResetPoint()
            .Translate(offset: new Vector3(1.9f, 0f, -0.2f))
            .WallpaperFold(group: SdfWallpaperGroup.P6M, cell: new Vector2(0.9f, 0.9f), limit: new Vector2(2f, 2f), materialStride: 1)
            .Translate(offset: new Vector3(0.05f, 0f, 0.12f))
            .RoundCone(lowerRadius: 0.12f, upperRadius: 0.04f, height: 0.26f, material: gold)
            .ResetPoint()
            .Translate(offset: new Vector3(1.9f, 0f, -0.2f))
            .WallpaperFold(group: SdfWallpaperGroup.P6M, cell: new Vector2(0.9f, 0.9f), limit: new Vector2(2f, 2f), materialStride: 1)
            .Translate(offset: new Vector3(-0.08f, 0.05f, 0.02f))
            .Sphere(radius: 0.07f, material: gold)
            .Build();
    }

    [SupportedOSPlatform("windows10.0.10240")]
    private static PostStageOutcome RunCore(PostContext context) {
        var program = BuildWallpaperScene();
        var frame = WorldStage.BuildHeroFrame(program: program, width: WorldWidth, height: WorldHeight);

        // Vulkan reference: the host device + the host's neutral compute services, SPIR-V kernels.
        byte[] vulkanPixels;

        using (var vulkanRenderer = new SdfWorldEngine(
            device: context.RequireGpuDevice(),
            gpu: context.Resolve<IGpuComputeServices>(),
            height: WorldHeight,
            kernels: SdfWorldKernels.Load(bytecodeExtension: ".spv"),
            options: new SdfWorldEngineOptions(Program: program),
            width: WorldWidth
        )) {
            vulkanPixels = vulkanRenderer.RenderFrame(frame: frame);
        }

        // Direct3D 12 comparand: the SHARED Tier-C device + its neutral compute services, DXIL kernels.
        var directX = context.RequireDirectXDevice();
        var directXPixels = WorldStage.RenderDirectXDiagnosed(directX: directX, render: () => {
            using var directXRenderer = new SdfWorldEngine(
                device: directX.DeviceContext,
                gpu: directX.Services.GetRequiredService<IGpuComputeServices>(),
                height: WorldHeight,
                kernels: SdfWorldKernels.Load(bytecodeExtension: ".dxil"),
                options: new SdfWorldEngineOptions(Program: program),
                width: WorldWidth
            );

            return directXRenderer.RenderFrame(frame: frame);
        });

        _ = Directory.CreateDirectory(path: context.ArtifactsDirectory);

        var diffPath = Path.Combine(context.ArtifactsDirectory, "world-wallpaper-diff.png");

        PngEncoder.Write(height: (int)WorldHeight, path: Path.Combine(context.ArtifactsDirectory, "world-wallpaper-vulkan.png"), rgba: vulkanPixels, width: (int)WorldWidth);
        PngEncoder.Write(height: (int)WorldHeight, path: Path.Combine(context.ArtifactsDirectory, "world-wallpaper-directx.png"), rgba: directXPixels, width: (int)WorldWidth);
        ParityCheck.WriteDiffImage(comparand: directXPixels, height: (int)WorldHeight, path: diffPath, reference: vulkanPixels, width: (int)WorldWidth);

        var metrics = ParityMetrics.Compute(reference: vulkanPixels, comparand: directXPixels, width: (int)WorldWidth, height: (int)WorldHeight);
        var failures = ParityThresholds.WorldComposite.Evaluate(metrics: metrics);

        if (failures.Count != 0) {
            return PostStageOutcome.Fail(artifactPath: diffPath, detail: $"{ParityCheck.Describe(metrics: metrics)} — {string.Join(separator: "; ", values: failures)}");
        }

        return PostStageOutcome.Pass(artifactPath: diffPath, detail: $"{WorldWidth}x{WorldHeight} P4G square + P6M hex wallpaper lattices with parity-stride recoloring | Vulkan (SPIR-V) vs Direct3D 12 (DXIL) within WorldComposite thresholds | {ParityCheck.Describe(metrics: metrics)}");
    }
}
