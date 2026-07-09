using Puck.Capture;
using System.Numerics;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Gpu;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-C stage. The soft-shadow GRID CULL (sdf-world.hlsli's <c>sdfShadowGather</c>): the world lit path gathers each
/// lit pixel's shadow-ray grid neighbourhood — the SAME frozen <see cref="SdfInstanceGrid"/> the beam cull walks, which
/// is VIEW-INDEPENDENT and so the natural acceleration structure for the SECONDARY (shadow) ray — and marches only those
/// instances instead of every one. This stage proves the culled shadow EQUALS the flat all-instances shadow by rendering
/// ONE program two ways via a pure frame flag: cull ON (<see cref="SdfFrame.DisableShadowCull"/> false) and cull OFF
/// (true, the flat reference), asserting they are pixel-identical.
/// <para>The scene is SHADOW-HEAVY: a floor plus five floating occluder spheres whose soft shadows overlap on the floor
/// (penumbra edges), authored as maskable INSTANCES (the grid bins them, so the gather's cell walk is exercised) plus one
/// DYNAMIC floating sphere the frozen grid cannot bin (it rides the always-tested list). Two of the occluders are the
/// CORRIDOR regression: one sits high and off to the side — OUTSIDE the camera frustum — yet its shadow falls INTO frame
/// (a shadow ray leaves the camera cone, so the old camera-tile-mask shadow missed exactly this; the view-independent
/// grid gather catches it). A no-occluder control confirms those shadows are really cast, so a broken gather that drops
/// an occluder would fail the corridor assertion, not merely match a shadow-less flat.</para>
/// <para>Asserts, per the <c>world-grid-cull</c> conventions: (a) culled == flat PER BACKEND — BIT-IDENTICAL on Vulkan
/// (a masked-out occluder's bound cannot lower any shadow sample below full light, so ANY divergence is a superset bug,
/// not a tolerance), within <see cref="ParityThresholds.WorldLsbExact"/> on Direct3D 12 (the benign DXIL redistribution
/// the flat/instanced contract documents); (b) the culled render holds Vulkan/Direct3D 12 cross-backend parity within
/// <see cref="ParityThresholds.WorldComposite"/>; (c) the corridor occluder's shadow is really present (the culled render
/// is darker than the no-occluder control where that shadow lands).</para>
/// </summary>
internal sealed class WorldShadowCullStage : IPostStage {
    private const uint WorldHeight = 600;
    private const uint WorldWidth = 960;

    // The corridor occluder: high and to +x/+z, OUTSIDE the camera frustum (the camera sits at +z looking at the origin),
    // yet its shadow ray toward the sun (SdfSunDirection, up and +x/+z) traces back to a floor point near the frame centre.
    private static readonly Vector3 CorridorOccluder = new(3.9f, 4.2f, 3.1f);

    /// <inheritdoc/>
    public string Name => "world-shadow-cull";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            return PostStageOutcome.Skip(detail: "the cross-backend tier (Direct3D 12) requires Windows 10.0.10240+");
        }

        return RunCore(context: context);
    }

    // The shadow-heavy scene. `withOccluders` false builds the floor alone (the corridor control): same floor, same
    // camera, so a per-pixel darkening between it and the occluder scene is a cast shadow and nothing else.
    private static SdfProgram BuildScene(bool withOccluders) {
        var builder = new SdfProgramBuilder();
        var (ground, crimson, azure, amber, jade) = WorldStage.AddHeroPalette(builder: builder);

        _ = builder.Plane(normal: Vector3.UnitY, offset: 0f, material: ground);

        if (!withOccluders) {
            return builder.Build();
        }

        // Five maskable floating occluders (the grid bins them → the gather's cell walk). Heights/offsets vary so their
        // soft shadows overlap on the floor with penumbra seams; the last is the off-frustum CORRIDOR caster.
        Span<int> palette = [crimson, azure, amber, jade];
        Span<Vector3> occluders = [
            new Vector3(-1.6f, 1.7f, 0.2f),
            new Vector3(0.7f, 2.2f, 0.7f),
            new Vector3(2.1f, 1.5f, -0.6f),
            new Vector3(-0.4f, 2.9f, -1.1f),
            CorridorOccluder,
        ];

        for (var i = 0; (i < occluders.Length); i++) {
            var centre = occluders[i];

            _ = builder.BeginInstance(boundCenter: centre, boundRadius: 0.62f);
            _ = builder
                .ResetPoint()
                .Translate(offset: centre)
                .Sphere(radius: 0.5f, material: palette[(i % palette.Length)]);
            _ = builder.EndInstance();
        }

        // One DYNAMIC occluder the frozen grid cannot bin — it rides the always-tested list, so the gather's always-list
        // path is exercised alongside the cell walk (both must be superset-correct for a bit-identical shadow).
        _ = builder.BeginInstanceDynamic(slot: 0, boundOffset: Vector3.Zero, boundRadius: 0.62f);
        _ = builder
            .ResetPoint()
            .TransformDynamic(slot: 0)
            .Sphere(radius: 0.5f, material: crimson);

        return builder.EndInstance().Build();
    }

    [SupportedOSPlatform("windows10.0.10240")]
    private static PostStageOutcome RunCore(PostContext context) {
        var program = BuildScene(withOccluders: true);
        var controlProgram = BuildScene(withOccluders: false);
        DynamicTransform[] transforms = [new DynamicTransform(Position: new Vector3(-0.9f, 2.5f, 1.4f), Orientation: Quaternion.Identity)];

        // The SAME program, rendered two ways by the pure frame flag: cull ON (the shipped grid-gathered shadow) and cull
        // OFF (the flat all-instances reference). Bit-identity of the two is the gate.
        var culledFrame = WorldStage.BuildHeroFrame(program: program, width: WorldWidth, height: WorldHeight) with { DynamicTransforms = transforms, DisableShadowCull = false };
        var flatFrame = WorldStage.BuildHeroFrame(program: program, width: WorldWidth, height: WorldHeight) with { DynamicTransforms = transforms, DisableShadowCull = true };
        var controlFrame = WorldStage.BuildHeroFrame(program: controlProgram, width: WorldWidth, height: WorldHeight) with { DisableShadowCull = false };

        var vulkanGpu = context.Resolve<IGpuComputeServices>();
        var vulkanDevice = context.RequireGpuDevice();
        var vulkanCulled = RenderOnce(program: program, frame: culledFrame, device: vulkanDevice, gpu: vulkanGpu, bytecodeExtension: ".spv");
        var vulkanFlat = RenderOnce(program: program, frame: flatFrame, device: vulkanDevice, gpu: vulkanGpu, bytecodeExtension: ".spv");
        var vulkanControl = RenderOnce(program: controlProgram, frame: controlFrame, device: vulkanDevice, gpu: vulkanGpu, bytecodeExtension: ".spv");

        var directX = context.RequireDirectXDevice();
        var directXGpu = directX.Services.GetRequiredService<IGpuComputeServices>();
        var directXCulled = WorldStage.RenderDirectXDiagnosed(directX: directX, render: () => RenderOnce(program: program, frame: culledFrame, device: directX.DeviceContext, gpu: directXGpu, bytecodeExtension: ".dxil"));
        var directXFlat = WorldStage.RenderDirectXDiagnosed(directX: directX, render: () => RenderOnce(program: program, frame: flatFrame, device: directX.DeviceContext, gpu: directXGpu, bytecodeExtension: ".dxil"));

        _ = Directory.CreateDirectory(path: context.ArtifactsDirectory);

        var diffPath = Path.Combine(context.ArtifactsDirectory, "world-shadow-cull-diff.png");

        PngEncoder.Write(height: (int)WorldHeight, path: Path.Combine(context.ArtifactsDirectory, "world-shadow-cull-vulkan.png"), rgba: vulkanCulled, width: (int)WorldWidth);
        PngEncoder.Write(height: (int)WorldHeight, path: Path.Combine(context.ArtifactsDirectory, "world-shadow-cull-directx.png"), rgba: directXCulled, width: (int)WorldWidth);
        ParityCheck.WriteDiffImage(comparand: directXCulled, height: (int)WorldHeight, path: diffPath, reference: vulkanCulled, width: (int)WorldWidth);

        // (a) culled == flat, PER BACKEND (Vulkan bit-identical, Direct3D 12 within WorldLsbExact). A gather that drops
        // an occluder surfaces here as a lighter shadow — a non-identical Vulkan pair.
        var contractFailure = ParityCheck.EvaluateFlatInstancedContract(
            artifactsDirectory: context.ArtifactsDirectory,
            diffPath: diffPath,
            directXFlatPixels: directXFlat,
            directXInstancedPixels: directXCulled,
            height: (int)WorldHeight,
            stageName: "world-shadow-cull",
            vulkanFlatPixels: vulkanFlat,
            vulkanInstancedPixels: vulkanCulled,
            width: (int)WorldWidth
        );

        if (contractFailure is { } culledFlatFailure) {
            return culledFlatFailure;
        }

        // (b) The culled render still holds cross-backend parity within the usual WorldComposite thresholds.
        var metrics = ParityMetrics.Compute(reference: vulkanCulled, comparand: directXCulled, width: (int)WorldWidth, height: (int)WorldHeight);
        var failures = ParityThresholds.WorldComposite.Evaluate(metrics: metrics);

        if (failures.Count != 0) {
            return PostStageOutcome.Fail(artifactPath: diffPath, detail: $"{ParityCheck.Describe(metrics: metrics)} — {string.Join(separator: "; ", values: failures)}");
        }

        // (c) The CORRIDOR proof: the scene really casts shadows (so culled == flat is not the trivial match of two
        // shadow-less renders). Count floor pixels the occluders darkened relative to the no-occluder control — an
        // occluder the gather dropped would shrink this, so a superset-correct gather AND real shadows are both required.
        var shadowedPixels = CountDarkened(scene: vulkanCulled, control: vulkanControl, width: (int)WorldWidth, height: (int)WorldHeight);

        if (shadowedPixels < 2000) {
            return PostStageOutcome.Fail(artifactPath: diffPath, detail: $"the shadow-heavy scene cast only {shadowedPixels} shadowed pixels vs the no-occluder control — expected the five occluders (incl. the off-frustum corridor caster) to shade the floor");
        }

        return PostStageOutcome.Pass(artifactPath: diffPath, detail: $"{WorldWidth}x{WorldHeight} shadow-heavy floor: 5 floating occluders (1 off-frustum corridor caster) + 1 dynamic always-list occluder | grid-culled shadow == flat all-instances shadow bit-identical on Vulkan, within WorldLsbExact on Direct3D 12 | {shadowedPixels} cast-shadow pixels vs the no-occluder control | culled cross-backend within WorldComposite thresholds | {ParityCheck.Describe(metrics: metrics)}");
    }

    // Counts pixels the occluder scene darkened by a clear margin against the no-occluder control (a cast shadow). Both
    // renders share the floor, camera, and lighting, so any per-pixel darkening past the noise floor is an occluder's shadow.
    private static int CountDarkened(byte[] scene, byte[] control, int width, int height) {
        var darkened = 0;

        for (var pixel = 0; (pixel < (width * height)); pixel++) {
            var offset = (pixel * 4);
            var sceneLuma = (scene[offset] + scene[offset + 1] + scene[offset + 2]);
            var controlLuma = (control[offset] + control[offset + 1] + control[offset + 2]);

            // A margin well past the 8-bit dither / cross-backend noise floor, so only a real shadow counts.
            if ((controlLuma - sceneLuma) > 45) {
                darkened++;
            }
        }

        return darkened;
    }

    private static byte[] RenderOnce(SdfProgram program, SdfFrame frame, IGpuDeviceContext device, IGpuComputeServices gpu, string bytecodeExtension) {
        using var renderer = new SdfWorldEngine(
            device: device,
            gpu: gpu,
            height: WorldHeight,
            kernels: SdfWorldKernels.Load(bytecodeExtension: bytecodeExtension),
            options: new SdfWorldEngineOptions(DynamicTransformCapacity: 1, Program: program),
            width: WorldWidth
        );

        return renderer.RenderFrame(frame: frame);
    }
}
