using Puck.Capture;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Gpu;
using Puck.Cameras;
using Puck.Compositing;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-C stage. Cross-backend parity for a DIEGETIC SCREEN: a <see cref="SdfShapeType.ScreenSlab"/> declared as a
/// screen surface (<see cref="SdfProgramBuilder"/>'s screen-surface <c>ScreenSlab</c> overload), with a synthetic
/// <c>sdf-child</c> pattern bound as its screen source (<see cref="SdfWorldEngine.SetScreenSource"/>) — the same
/// synthetic-source pattern <see cref="WorldChildStage"/> uses for a hosted CHILD viewport, here proving the
/// SHADING-only screen seam instead: the slab's distance field is an ordinary box on both backends; only whether its
/// lit face samples the bound source or shades the flat/procedural material differs. The SAME scene renders through
/// the identical <see cref="SdfWorldEngine"/> on Vulkan (SPIR-V) and Direct3D 12 (DXIL) and must agree within the
/// calibrated <c>WorldComposite</c> thresholds — AND a Vulkan-only "no source bound" control render of the identical
/// scene must clearly DIFFER inside the screen's on-screen footprint, so the feature cannot silently no-op (a broken
/// binding that always falls back to the material would otherwise still pass the cross-backend diff, since both
/// backends would agree on the WRONG picture). A THIRD check declares+binds the identical panel at the LAST screen
/// slot (<c>SdfProgramBuilder.MaxScreenSurfaces - 1</c>) and requires it to render byte-for-byte like slot 0 — proving
/// the full 32-surface combined-image-sampler binding run (screenSource0..31 / t5..t36) and the sampler-switch's last
/// case, not just slot 0's descriptor.
/// </summary>
internal sealed class WorldScreenStage : IPostStage {
    private const int ChildPushByteLength = 16; // ChildParams { uint2 extent; float time; uint pad; }
    private const GpuPixelFormat Format = GpuPixelFormat.R8G8B8A8Unorm;
    private const float ScreenHalfHeight = 0.9f;
    private const float ScreenHalfWidth = 1.4f;
    private const uint SourceSize = 64; // the synthetic screen-source texture (sdf-child fills it)
    private const uint WorkgroupEdge = 8;
    private const uint WorldHeight = 600;
    private const uint WorldWidth = 960;

    private static readonly Vector3 ScreenOrigin = new(x: 0f, y: 1.2f, z: 0f);

    /// <inheritdoc/>
    public string Name => "world-screen";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            return PostStageOutcome.Skip(detail: "the cross-backend tier (Direct3D 12) requires Windows 10.0.10240+");
        }

        return RunCore(context: context);
    }

    // A ground plane plus a screen slab centered at ScreenOrigin, front face toward +Z (the camera side): no
    // rotation is applied before the ScreenSlab, so its local axes ARE the world axes, and worldRight/worldUp below
    // (+X, +Y) are exactly its actual local X/Y — the simplest frame that keeps geometry and shading trivially in
    // agreement. halfExtents.z gives the slab some depth so its silhouette reads against the ground. The screen index
    // is a parameter so the HIGH-slot equivalence check can declare the identical panel at the last slot (proving the
    // 32-surface binding run, not just slot 0).
    internal static SdfProgram BuildScreenScene(int screenIndex = 0) {
        var builder = new SdfProgramBuilder();
        var ground = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.35f, y: 0.4f, z: 0.45f)));
        var bezel = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.08f, y: 0.08f, z: 0.1f)));

        return builder
            .Plane(normal: Vector3.UnitY, offset: 0f, material: ground)
            // The bezel: a slightly larger, inset box behind the screen slab so the slab reads as a "panel" and not
            // a bare rectangle floating in space — never sampled (a plain material box, no screen surface).
            .Translate(offset: ScreenOrigin)
            .Box(halfExtents: new Vector3(x: (ScreenHalfWidth + 0.08f), y: (ScreenHalfHeight + 0.08f), z: 0.08f), round: 0.02f, material: bezel)
            .ResetPoint()
            .Translate(offset: ScreenOrigin)
            .ScreenSlab(
                halfExtents: new Vector3(x: ScreenHalfWidth, y: ScreenHalfHeight, z: 0.1f),
                round: 0f,
                worldOrigin: ScreenOrigin,
                worldRight: Vector3.UnitX,
                worldUp: Vector3.UnitY,
                screenIndex: screenIndex
            )
            .Build();
    }

    // The quaternion-authoring equivalence scene: the SAME panel, yawed 0.35 rad about Y (geometry via Rotate(q)),
    // its sampled surface authored EITHER through the explicit-frame overload (right/up derived by THIS test from q)
    // or through the quaternion overload (right/up derived by the BUILDER from q). The two programs must be
    // indistinguishable at the pixels — proving the quaternion convenience derives exactly the frame the explicit
    // contract documents (axes not swapped/flipped, normalization a no-op for a unit quaternion).
    internal static SdfProgram BuildRotatedScreenScene(bool quaternionAuthored) {
        var builder = new SdfProgramBuilder();
        var ground = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.35f, y: 0.4f, z: 0.45f)));
        var bezel = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.08f, y: 0.08f, z: 0.1f)));
        var orientation = Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: 0.35f);
        var halfExtents = new Vector3(x: ScreenHalfWidth, y: ScreenHalfHeight, z: 0.1f);

        _ = builder
            .Plane(normal: Vector3.UnitY, offset: 0f, material: ground)
            .Translate(offset: ScreenOrigin)
            .Rotate(rotation: orientation)
            .Box(halfExtents: new Vector3(x: (ScreenHalfWidth + 0.08f), y: (ScreenHalfHeight + 0.08f), z: 0.08f), round: 0.02f, material: bezel)
            .ResetPoint()
            .Translate(offset: ScreenOrigin)
            .Rotate(rotation: orientation);

        _ = (quaternionAuthored
            ? builder.ScreenSlab(halfExtents: halfExtents, round: 0f, worldOrigin: ScreenOrigin, worldOrientation: orientation, screenIndex: 0)
            : builder.ScreenSlab(
                halfExtents: halfExtents,
                round: 0f,
                worldOrigin: ScreenOrigin,
                worldRight: Vector3.Transform(value: Vector3.UnitX, rotation: orientation),
                worldUp: Vector3.Transform(value: Vector3.UnitY, rotation: orientation),
                screenIndex: 0
            ));

        return builder.Build();
    }

    /// <summary>Builds the fixed camera frame both backends (and the no-source control render) share: a single
    /// full-region viewport looking squarely at the screen slab.</summary>
    internal static SdfFrame BuildScreenFrame(SdfProgram program, uint width, uint height) {
        var camera = CameraSnapshot.LookAt(
            position: new Vector3(x: 0f, y: 1.4f, z: 5.5f),
            target: ScreenOrigin,
            fieldOfViewRadians: (50f * (MathF.PI / 180f)),
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

    [SupportedOSPlatform("windows10.0.10240")]
    private static PostStageOutcome RunCore(PostContext context) {
        var program = BuildScreenScene();
        var frame = BuildScreenFrame(program: program, width: WorldWidth, height: WorldHeight);

        // Vulkan reference: the host device + the host's neutral compute services, SPIR-V kernels.
        var vulkanGpu = context.Resolve<IGpuComputeServices>();
        var vulkanDevice = context.RequireGpuDevice();
        byte[] vulkanPixels;
        byte[] vulkanNoSourcePixels;

        using (var vulkanSource = RenderScreenSource(device: vulkanDevice, gpu: vulkanGpu, bytecodeExtension: ".spv"))
        using (var vulkanRenderer = new SdfWorldEngine(
            device: vulkanDevice,
            gpu: vulkanGpu,
            height: WorldHeight,
            kernels: SdfWorldKernels.Load(bytecodeExtension: ".spv"),
            options: new SdfWorldEngineOptions(Program: program),
            width: WorldWidth
        )) {
            vulkanRenderer.SetScreenSource(screenIndex: 0, imageViewHandle: vulkanSource.ImageViewHandle);
            vulkanPixels = vulkanRenderer.RenderFrame(frame: frame);
        }

        // The no-op control: the IDENTICAL scene/frame/engine, Vulkan-only, with NO screen source ever bound — proves
        // the feature isn't silently inert (a binding that never reached the shader would make this render identical
        // to the bound one, and the cross-backend diff below would still pass since both backends would agree on the
        // wrong picture).
        using (var vulkanNoSourceRenderer = new SdfWorldEngine(
            device: vulkanDevice,
            gpu: vulkanGpu,
            height: WorldHeight,
            kernels: SdfWorldKernels.Load(bytecodeExtension: ".spv"),
            options: new SdfWorldEngineOptions(Program: program),
            width: WorldWidth
        )) {
            vulkanNoSourcePixels = vulkanNoSourceRenderer.RenderFrame(frame: frame);
        }

        // Direct3D 12 comparand: the SHARED Tier-C device + its neutral compute services, DXIL kernels, the SAME
        // synthetic source bound.
        var directX = context.RequireDirectXDevice();
        var directXGpu = directX.Services.GetRequiredService<IGpuComputeServices>();
        var directXPixels = WorldStage.RenderDirectXDiagnosed(directX: directX, render: () => {
            using var directXSource = RenderScreenSource(device: directX.DeviceContext, gpu: directXGpu, bytecodeExtension: ".dxil");
            using var directXRenderer = new SdfWorldEngine(
                device: directX.DeviceContext,
                gpu: directXGpu,
                height: WorldHeight,
                kernels: SdfWorldKernels.Load(bytecodeExtension: ".dxil"),
                options: new SdfWorldEngineOptions(Program: program),
                width: WorldWidth
            );

            directXRenderer.SetScreenSource(screenIndex: 0, imageViewHandle: directXSource.ImageViewHandle);

            return directXRenderer.RenderFrame(frame: frame);
        });

        _ = Directory.CreateDirectory(path: context.ArtifactsDirectory);

        var diffPath = Path.Combine(path1: context.ArtifactsDirectory, path2: "world-screen-diff.png");

        PngEncoder.Write(height: (int)WorldHeight, path: Path.Combine(path1: context.ArtifactsDirectory, path2: "world-screen-vulkan.png"), rgba: vulkanPixels, width: (int)WorldWidth);
        PngEncoder.Write(height: (int)WorldHeight, path: Path.Combine(path1: context.ArtifactsDirectory, path2: "world-screen-directx.png"), rgba: directXPixels, width: (int)WorldWidth);
        PngEncoder.Write(height: (int)WorldHeight, path: Path.Combine(path1: context.ArtifactsDirectory, path2: "world-screen-nosource.png"), rgba: vulkanNoSourcePixels, width: (int)WorldWidth);
        ParityCheck.WriteDiffImage(comparand: directXPixels, height: (int)WorldHeight, path: diffPath, reference: vulkanPixels, width: (int)WorldWidth);

        var metrics = ParityMetrics.Compute(reference: vulkanPixels, comparand: directXPixels, width: (int)WorldWidth, height: (int)WorldHeight);
        var failures = ParityThresholds.WorldComposite.Evaluate(metrics: metrics).ToList();

        // The no-op guard: sample a pixel in the middle of the screen's known on-screen footprint (the slab fills
        // most of the frame at this camera) and require it differs MEANINGFULLY (not ±1 LSB noise) between the
        // bound-source render and the no-source control — proving the sampled pattern actually reached the pixel.
        var centerX = (int)(WorldWidth * 0.5f);
        var centerY = (int)(WorldHeight * 0.5f);
        var boundColor = ReadPixel(pixels: vulkanPixels, width: (int)WorldWidth, x: centerX, y: centerY);
        var noSourceColor = ReadPixel(pixels: vulkanNoSourcePixels, width: (int)WorldWidth, x: centerX, y: centerY);
        var centerDelta = MaxChannelDelta(a: boundColor, b: noSourceColor);

        if (centerDelta < 24) {
            failures.Add(item: $"the screen's center pixel barely changed between the bound-source render and the no-source control (Δ{centerDelta}) — the sampled pattern did not reach the pixel (silent no-op)");
        }

        // The quaternion-authoring equivalence: the yawed panel authored via the explicit frame vs the quaternion
        // overload, rendered on the SAME Vulkan device with the SAME bound source, must match byte-for-byte — the
        // authoring convenience may not move a single pixel.
        using (var equivalenceSource = RenderScreenSource(device: vulkanDevice, gpu: vulkanGpu, bytecodeExtension: ".spv")) {
            var explicitPixels = RenderRotatedScene(device: vulkanDevice, gpu: vulkanGpu, program: BuildRotatedScreenScene(quaternionAuthored: false), sourceView: equivalenceSource.ImageViewHandle);
            var quaternionPixels = RenderRotatedScene(device: vulkanDevice, gpu: vulkanGpu, program: BuildRotatedScreenScene(quaternionAuthored: true), sourceView: equivalenceSource.ImageViewHandle);

            if (!explicitPixels.AsSpan().SequenceEqual(other: quaternionPixels)) {
                PngEncoder.Write(height: (int)WorldHeight, path: Path.Combine(path1: context.ArtifactsDirectory, path2: "world-screen-quaternion-explicit.png"), rgba: explicitPixels, width: (int)WorldWidth);
                PngEncoder.Write(height: (int)WorldHeight, path: Path.Combine(path1: context.ArtifactsDirectory, path2: "world-screen-quaternion-authored.png"), rgba: quaternionPixels, width: (int)WorldWidth);
                failures.Add(item: "the quaternion-authored screen slab rendered differently from the equivalent explicit-frame slab (same device, same source) — the authoring convenience moved pixels");
            }
        }

        // The HIGH-SLOT binding equivalence (the 32-surface run's proof): the IDENTICAL panel declared at the LAST
        // screen slot (SdfProgramBuilder.MaxScreenSurfaces - 1) with the SAME synthetic source bound there must render
        // byte-for-byte identically to the slot-0 render on the same Vulkan device — the screen index only selects which
        // of the 32 combined-image-sampler bindings (screenSource0..31 / registers t5..t36) the source rides, so with
        // the same pattern bound it may not move a pixel. This exercises the high end of the binding run AND the
        // sampleScreenSource switch's last case; a broken high-slot binding (a mis-shifted register, a filler bound
        // instead of the source) would diverge here even though slot 0 still passes. A no-op-guard is unnecessary — the
        // slot-0 render this compares against already carries one.
        var highSlot = (SdfProgramBuilder.MaxScreenSurfaces - 1);

        using (var highSlotSource = RenderScreenSource(device: vulkanDevice, gpu: vulkanGpu, bytecodeExtension: ".spv")) {
            var highSlotProgram = BuildScreenScene(screenIndex: highSlot);
            byte[] highSlotPixels;

            using (var highSlotRenderer = new SdfWorldEngine(
                device: vulkanDevice,
                gpu: vulkanGpu,
                height: WorldHeight,
                kernels: SdfWorldKernels.Load(bytecodeExtension: ".spv"),
                options: new SdfWorldEngineOptions(Program: highSlotProgram),
                width: WorldWidth
            )) {
                highSlotRenderer.SetScreenSource(screenIndex: highSlot, imageViewHandle: highSlotSource.ImageViewHandle);
                highSlotPixels = highSlotRenderer.RenderFrame(frame: BuildScreenFrame(program: highSlotProgram, width: WorldWidth, height: WorldHeight));
            }

            if (!highSlotPixels.AsSpan().SequenceEqual(other: vulkanPixels)) {
                PngEncoder.Write(height: (int)WorldHeight, path: Path.Combine(path1: context.ArtifactsDirectory, path2: "world-screen-highslot.png"), rgba: highSlotPixels, width: (int)WorldWidth);
                failures.Add(item: $"the screen slab declared+bound at slot {highSlot} rendered differently from the identical slot-0 panel (same device, same source) — the high end of the 32-surface binding run is mis-wired");
            }
        }

        if (failures.Count != 0) {
            return PostStageOutcome.Fail(artifactPath: diffPath, detail: $"{ParityCheck.Describe(metrics: metrics)} | center Δ{centerDelta} — {string.Join(separator: "; ", values: failures)}");
        }

        return PostStageOutcome.Pass(artifactPath: diffPath, detail: $"{WorldWidth}x{WorldHeight} diegetic screen (synthetic sdf-child source) | Vulkan (SPIR-V) vs Direct3D 12 (DXIL) within WorldComposite thresholds | center pixel Δ{centerDelta} vs no-source control | quaternion-authored slab == explicit-frame slab bit-identical | slot {(SdfProgramBuilder.MaxScreenSurfaces - 1)} == slot 0 bit-identical (32-surface binding run) | {ParityCheck.Describe(metrics: metrics)}");
    }

    // One Vulkan render of a rotated-panel program with the synthetic source bound at screen index 0 — the
    // quaternion-equivalence comparand path.
    private static byte[] RenderRotatedScene(IGpuDeviceContext device, IGpuComputeServices gpu, SdfProgram program, nint sourceView) {
        using var renderer = new SdfWorldEngine(
            device: device,
            gpu: gpu,
            height: WorldHeight,
            kernels: SdfWorldKernels.Load(bytecodeExtension: ".spv"),
            options: new SdfWorldEngineOptions(Program: program),
            width: WorldWidth
        );

        renderer.SetScreenSource(screenIndex: 0, imageViewHandle: sourceView);

        return renderer.RenderFrame(frame: BuildScreenFrame(program: program, width: WorldWidth, height: WorldHeight));
    }

    // Fills a SourceSize x SourceSize storage image with the deterministic sdf-child test pattern and leaves it
    // ShaderReadOnly — the synthetic "screen source" a host would otherwise hand in from a child node or an emulator's
    // native framebuffer. Caller disposes.
    private static IGpuStorageImage RenderScreenSource(IGpuDeviceContext device, IGpuComputeServices gpu, string bytecodeExtension) {
        var deviceHandle = device.DeviceHandle;
        var childPush = new byte[ChildPushByteLength];
        var childWords = MemoryMarshal.Cast<byte, uint>(span: childPush.AsSpan());

        childWords[0] = SourceSize;
        childWords[1] = SourceSize; // ChildParams.extent; time (word 2) and pad (word 3) stay 0.

        GpuComputeBinding[] childBindings = [new GpuComputeBinding(Binding: 0, Kind: GpuComputeBindingKind.StorageImage)];

        using var childModule = gpu.ShaderModuleFactory.Create(deviceContext: device, stage: GpuShaderStage.Compute, bytecode: PostShaders.Read(folder: "Sdf", file: $"sdf-child.comp{bytecodeExtension}"));
        using var childPipeline = gpu.ComputePipelineFactory.Create(bindings: childBindings, computeShaderModule: childModule, deviceContext: device, pushConstantBinding: new GpuPushConstantBinding(data: childPush, offset: 0, stageFlags: GpuShaderStage.Compute));
        using var commandPool = gpu.CommandPoolFactory.Create(deviceContext: device);

        var source = gpu.StorageImageFactory.Create(deviceContext: device, format: Format, height: SourceSize, width: SourceSize);
        var poolSizes = GpuDescriptorPoolSizes.ForSets(childBindings);
        var pool = gpu.DescriptorAllocator.CreatePool(deviceHandle: deviceHandle, sizes: poolSizes);

        try {
            var childSet = gpu.DescriptorAllocator.AllocateSet(descriptorSetLayoutHandle: childPipeline.DescriptorSetLayoutHandle, deviceHandle: deviceHandle, poolHandle: pool);

            gpu.DescriptorAllocator.WriteStorageImage(arrayElement: 0, binding: 0, descriptorSetHandle: childSet, deviceHandle: deviceHandle, imageViewHandle: source.ImageViewHandle);

            var recorder = gpu.ComputeRecorder;
            var commandBuffer = commandPool.CommandBufferHandle;
            var groups = ((SourceSize + (WorkgroupEdge - 1)) / WorkgroupEdge);

            recorder.BeginCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle);
            recorder.TransitionImageLayout(commandBufferHandle: commandBuffer, destinationAccessMask: GpuComputeAccess.ShaderWrite, destinationStageMask: GpuComputeStage.ComputeShader, deviceHandle: deviceHandle, imageHandle: source.ImageHandle, newLayout: GpuImageLayout.General, oldLayout: GpuImageLayout.Undefined, sourceAccessMask: GpuComputeAccess.None, sourceStageMask: GpuComputeStage.TopOfPipe);
            recorder.BindComputePipeline(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle, pipelineHandle: childPipeline.Handle);
            recorder.BindComputeDescriptorSet(commandBufferHandle: commandBuffer, descriptorSetHandle: childSet, deviceHandle: deviceHandle, pipelineLayoutHandle: childPipeline.LayoutHandle);
            recorder.PushConstants(commandBufferHandle: commandBuffer, data: childPush, deviceHandle: deviceHandle, offset: 0, pipelineLayoutHandle: childPipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
            recorder.Dispatch(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle, groupCountX: groups, groupCountY: groups, groupCountZ: 1);
            // ShaderReadOnly: the world renderer's screen-source seam SAMPLES this image (a combined-image-sampler
            // read), unlike a hosted child's General-layout integer copy.
            recorder.TransitionImageLayout(commandBufferHandle: commandBuffer, destinationAccessMask: GpuComputeAccess.ShaderRead, destinationStageMask: GpuComputeStage.ComputeShader, deviceHandle: deviceHandle, imageHandle: source.ImageHandle, newLayout: GpuImageLayout.ShaderReadOnly, oldLayout: GpuImageLayout.General, sourceAccessMask: GpuComputeAccess.ShaderWrite, sourceStageMask: GpuComputeStage.ComputeShader);
            recorder.EndCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle);

            gpu.QueueSubmitter.SubmitAndWait(commandBufferHandles: [commandBuffer], deviceContext: device);

            return source;
        } catch {
            source.Dispose();

            throw;
        } finally {
            gpu.DescriptorAllocator.DestroyPool(deviceHandle: deviceHandle, poolHandle: pool);
        }
    }
    private static (byte R, byte G, byte B) ReadPixel(byte[] pixels, int width, int x, int y) {
        var offset = (((y * width) + x) * 4);

        return (pixels[offset], pixels[(offset + 1)], pixels[(offset + 2)]);
    }
    private static int MaxChannelDelta((byte R, byte G, byte B) a, (byte R, byte G, byte B) b) {
        return Math.Max(val1: Math.Abs(value: (a.R - b.R)), val2: Math.Max(val1: Math.Abs(value: (a.G - b.G)), val2: Math.Abs(value: (a.B - b.B))));
    }
}
