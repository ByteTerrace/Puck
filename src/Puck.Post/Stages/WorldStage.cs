using System.Numerics;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Gpu;
using Puck.Cameras;
using Puck.Compositing;
using Puck.DirectX.Apis;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-C stage C5. Cross-backend SDF world parity, hero view — the POST's central "one engine, two APIs" check.
/// The SAME deterministic scene — a ground
/// plane and several primitives with distinct materials and blend variety (smooth-union, plain union, subtraction) —
/// renders through the identical <see cref="Puck.SdfVm.SdfWorldEngine"/> twice: once on the Vulkan host device
/// (SPIR-V kernels) and once on the shared LUID-matched Tier-C Direct3D 12 device (DXIL kernels). Because the engine
/// and the frame are identical, the two composited readbacks must agree within the calibrated <c>WorldComposite</c>
/// thresholds (values copied from the demo's <c>ParityThresholds</c>): the residual is isolated ±1-LSB driver FP
/// codegen noise, while a real divergence spreads, clumps, or exceeds ±1. Artifacts: both backend renders and an
/// amplified diff heatmap.
/// </summary>
internal sealed class WorldStage : IPostStage {
    private const float FieldOfViewRadians = (55f * (MathF.PI / 180f));
    private const uint WorldHeight = 600;
    private const uint WorldWidth = 960;

    /// <inheritdoc/>
    public string Name => "world";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            return PostStageOutcome.Skip(detail: "the cross-backend tier (Direct3D 12) requires Windows 10.0.10240+");
        }

        return RunCore(context: context);
    }

    /// <summary>Adds the hero material palette the cross-backend world stages share — the ground plus the
    /// crimson/azure/amber/jade object albedos, in that fixed declaration order — to the builder and returns the
    /// material ids. The ONE statement of the palette; <c>world</c>, <c>world-instanced</c>, and <c>world-swarm</c>
    /// all build their scenes from it.</summary>
    /// <param name="builder">The scene's program builder.</param>
    /// <returns>The five material ids, in declaration order.</returns>
    internal static (int Ground, int Crimson, int Azure, int Amber, int Jade) AddHeroPalette(SdfProgramBuilder builder) {
        return (
            Ground: builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.55f, 0.6f, 0.65f))),
            Crimson: builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.85f, 0.25f, 0.2f))),
            Azure: builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.2f, 0.45f, 0.85f))),
            Amber: builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.9f, 0.7f, 0.2f))),
            Jade: builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.2f, 0.7f, 0.45f)))
        );
    }

    /// <summary>Builds the deterministic hero scene the cross-backend world stages share: a ground plane plus four
    /// primitives with distinct materials and blend variety — a sphere smooth-blended into the ground, a rounded box
    /// (plain union) with a sphere SUBTRACTED from its top, and a torus — so the parity diff covers the material
    /// palette, the blend ops, and both hard and soft silhouettes.</summary>
    /// <returns>The scene program.</returns>
    internal static SdfProgram BuildHeroScene() {
        var builder = new SdfProgramBuilder();
        var (ground, crimson, azure, amber, jade) = AddHeroPalette(builder: builder);

        return builder
            .Plane(normal: Vector3.UnitY, offset: 0f, material: ground)
            .Translate(offset: new Vector3(-1.9f, 0.9f, -0.6f))
            .Sphere(radius: 0.9f, material: crimson, blend: SdfBlendOp.SmoothUnion, smooth: 0.4f)
            .ResetPoint()
            .Translate(offset: new Vector3(1.6f, 0.7f, 0.4f))
            .Box(halfExtents: new Vector3(0.7f, 0.7f, 0.7f), round: 0.08f, material: azure)
            .ResetPoint()
            .Translate(offset: new Vector3(1.6f, 1.5f, 0.4f))
            .Sphere(radius: 0.55f, material: amber, blend: SdfBlendOp.Subtraction)
            .ResetPoint()
            .Translate(offset: new Vector3(-0.2f, 0.35f, 1.7f))
            .Torus(majorRadius: 0.8f, minorRadius: 0.22f, material: jade)
            .Build();
    }

    /// <summary>Builds the fixed hero frame (a single full-region viewport, time 0, no dynamic entities) both
    /// backends render, at the given extent.</summary>
    /// <param name="program">The scene program.</param>
    /// <param name="width">The viewport width in pixels.</param>
    /// <param name="height">The viewport height in pixels.</param>
    /// <returns>The frame.</returns>
    internal static SdfFrame BuildHeroFrame(SdfProgram program, uint width, uint height) {
        var camera = CameraSnapshot.LookAt(
            position: new Vector3(0.4f, 3.2f, 8.5f),
            target: new Vector3(0f, 0.9f, 0f),
            fieldOfViewRadians: FieldOfViewRadians,
            viewportWidth: width,
            viewportHeight: height
        );

        return new SdfFrame(
            Program: program,
            ProgramChanged: false,
            Views: [new SdfViewSnapshot(Camera: camera, Region: new NormalizedRect(X: 0f, Y: 0f, Width: 1f, Height: 1f))],
            Time: 0f,
            WarpAmount: 0f
        );
    }

    /// <summary>Renders the frame once on the shared Tier-C Direct3D 12 device through the given harness factory,
    /// annotating a device removal with the SPECIFIC removed reason (DEVICE_HUNG, DRIVER_INTERNAL_ERROR, ...) so a
    /// cross-backend world regression stays diagnosable from the battery output. Shared with the world-child stage.</summary>
    /// <param name="directX">The shared Tier-C Direct3D 12 device bundle.</param>
    /// <param name="render">Renders on the Direct3D 12 device and returns the readback.</param>
    /// <returns>The rendered pixels.</returns>
    [SupportedOSPlatform("windows10.0.10240")]
    internal static byte[] RenderDirectXDiagnosed(PostDirectXDevice directX, Func<byte[]> render) {
        try {
            return render();
        } catch (Exception exception) {
            // A Direct3D 12 device removal surfaces only as the bare DEVICE_REMOVED HRESULT; query the SPECIFIC reason
            // so the failure is diagnosable, then rethrow into the battery's Infra catch.
            var removedReason = new DirectXNativeDeviceApi().GetDeviceRemovedReason(deviceHandle: directX.DeviceContext.DeviceHandle);

            if (0 != removedReason) {
                throw new InvalidOperationException(message: $"Direct3D 12 device removed | reason 0x{removedReason:X8}", innerException: exception);
            }

            throw;
        }
    }

    /// <summary>Renders one frame of a program through <see cref="SdfWorldEngine"/> on a single backend's device and
    /// returns the RGBA readback — the inner render the cross-backend world-parity stages share. The only per-backend
    /// difference is the device, its neutral compute services, and the kernel bytecode extension.</summary>
    /// <param name="device">The backend device to render on.</param>
    /// <param name="gpu">That device's neutral compute services.</param>
    /// <param name="bytecodeExtension">The kernel bytecode extension (<c>.spv</c> for Vulkan, <c>.dxil</c> for Direct3D 12).</param>
    /// <param name="frame">The frame to render (its <see cref="SdfFrame.Program"/> is the scene).</param>
    /// <param name="width">The render width in pixels.</param>
    /// <param name="height">The render height in pixels.</param>
    /// <returns>The rendered RGBA pixels.</returns>
    internal static byte[] RenderWorldFrame(IGpuDeviceContext device, IGpuComputeServices gpu, string bytecodeExtension, SdfFrame frame, uint width, uint height) {
        using var renderer = new SdfWorldEngine(
            device: device,
            gpu: gpu,
            height: height,
            kernels: SdfWorldKernels.Load(bytecodeExtension: bytecodeExtension),
            options: new SdfWorldEngineOptions(Program: frame.Program),
            width: width
        );

        return renderer.RenderFrame(frame: frame);
    }

    /// <summary>The cross-backend scene-parity dance the simple world stages share: render <paramref name="program"/>
    /// on the Vulkan host (SPIR-V) and on the shared Tier-C Direct3D 12 device (DXIL) through the identical
    /// <see cref="SdfWorldEngine"/> and the shared hero frame, then diff the two readbacks against
    /// <paramref name="thresholds"/> (writing <c>&lt;prefix&gt;-vulkan/-directx/-diff.png</c>). Each caller supplies only
    /// its scene, threshold set, artifact prefix, and pass label.</summary>
    /// <param name="context">The run context.</param>
    /// <param name="prefix">The artifact filename prefix (e.g. <c>world-warp</c>).</param>
    /// <param name="program">The scene to render on both backends.</param>
    /// <param name="thresholds">The calibrated PASS thresholds for this scene.</param>
    /// <param name="passLabel">The pass-detail prefix; the parity digest is appended.</param>
    /// <param name="width">The render width in pixels.</param>
    /// <param name="height">The render height in pixels.</param>
    /// <returns>The stage outcome.</returns>
    [SupportedOSPlatform("windows10.0.10240")]
    internal static PostStageOutcome RunSceneParity(PostContext context, string prefix, SdfProgram program, ParityThresholdSet thresholds, string passLabel, uint width = WorldWidth, uint height = WorldHeight) {
        var frame = BuildHeroFrame(program: program, width: width, height: height);

        // Vulkan reference: the host device + the host's neutral compute services, SPIR-V kernels.
        var vulkanPixels = RenderWorldFrame(device: context.RequireGpuDevice(), gpu: context.Resolve<IGpuComputeServices>(), bytecodeExtension: ".spv", frame: frame, width: width, height: height);

        // Direct3D 12 comparand: the SHARED Tier-C device (LUID-matched to the Vulkan host) + its neutral compute
        // services, DXIL kernels — the identical engine, only the backend differs.
        var directX = context.RequireDirectXDevice();
        var directXPixels = RenderDirectXDiagnosed(directX: directX, render: () => RenderWorldFrame(device: directX.DeviceContext, gpu: directX.Services.GetRequiredService<IGpuComputeServices>(), bytecodeExtension: ".dxil", frame: frame, width: width, height: height));

        return ParityCheck.WriteEvaluateReport(artifactsDirectory: context.ArtifactsDirectory, prefix: prefix, referencePixels: vulkanPixels, comparandPixels: directXPixels, width: (int)width, height: (int)height, thresholds: thresholds, passLabel: passLabel);
    }

    [SupportedOSPlatform("windows10.0.10240")]
    private static PostStageOutcome RunCore(PostContext context) {
        // The world composite has a richer benign-noise baseline than the flat debug views, so it judges against its
        // own calibrated set (WorldComposite).
        return RunSceneParity(
            context: context,
            prefix: "world",
            program: BuildHeroScene(),
            thresholds: ParityThresholds.WorldComposite,
            passLabel: $"{WorldWidth}x{WorldHeight} hero view | Vulkan (SPIR-V) vs Direct3D 12 (DXIL) within WorldComposite thresholds"
        );
    }
}
