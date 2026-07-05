using Puck.Capture;
using System.Numerics;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Gpu;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-C stage. Cross-backend parity for the WARP ops (twist/bend/elongate), the FIELD ops (onion/dilate), and the
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
            .Plane(normal: Vector3.UnitY, offset: 0f, material: ground)
            // A twisted box column (the twist rotates its cross-section along Y).
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
            // An onioned sphere with a box CUT through it — the hollow shell shows in the opening. The onion is a
            // FIELD op, so this pair closes the chain: shell first, then carve the shell.
            .ResetPoint()
            .Translate(offset: new Vector3(2.6f, 0.8f, -0.4f))
            .Sphere(radius: 0.65f, material: slate)
            .Onion(thickness: 0.06f)
            .ResetPoint()
            .Translate(offset: new Vector3(2.6f, 1.3f, -0.4f))
            .Box(halfExtents: new Vector3(0.8f, 0.5f, 0.8f), round: 0f, material: honey, blend: SdfBlendOp.Subtraction)
            // An XOR pair: solid where exactly one sphere is, hollow in the lens where both are.
            .ResetPoint()
            .Translate(offset: new Vector3(-0.2f, 1.9f, 0.6f))
            .Sphere(radius: 0.42f, material: brick)
            .ResetPoint()
            .Translate(offset: new Vector3(0.25f, 1.9f, 0.6f))
            .Sphere(radius: 0.42f, material: teal, blend: SdfBlendOp.Xor)
            // A smooth-subtraction carve: a filleted scoop out of a box.
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
        var program = BuildWarpScene();
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

        var diffPath = Path.Combine(context.ArtifactsDirectory, "world-warp-diff.png");

        PngEncoder.Write(height: (int)WorldHeight, path: Path.Combine(context.ArtifactsDirectory, "world-warp-vulkan.png"), rgba: vulkanPixels, width: (int)WorldWidth);
        PngEncoder.Write(height: (int)WorldHeight, path: Path.Combine(context.ArtifactsDirectory, "world-warp-directx.png"), rgba: directXPixels, width: (int)WorldWidth);
        ParityCheck.WriteDiffImage(comparand: directXPixels, height: (int)WorldHeight, path: diffPath, reference: vulkanPixels, width: (int)WorldWidth);

        var metrics = ParityMetrics.Compute(reference: vulkanPixels, comparand: directXPixels, width: (int)WorldWidth, height: (int)WorldHeight);
        var failures = ParityThresholds.WorldLsbExact.Evaluate(metrics: metrics);

        if (failures.Count != 0) {
            return PostStageOutcome.Fail(artifactPath: diffPath, detail: $"{ParityCheck.Describe(metrics: metrics)} — {string.Join(separator: "; ", values: failures)}");
        }

        return PostStageOutcome.Pass(artifactPath: diffPath, detail: $"{WorldWidth}x{WorldHeight} twist/bend/elongate warps + onion/dilate field ops + xor/smooth blends | Vulkan (SPIR-V) vs Direct3D 12 (DXIL) within WorldLsbExact thresholds | {ParityCheck.Describe(metrics: metrics)}");
    }
}
