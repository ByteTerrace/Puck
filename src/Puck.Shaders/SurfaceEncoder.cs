using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Shaders;

/// <summary>
/// The display encode (<c>Assets/Runtime/display-encode.frag.hlsl</c>), the one shader that turns a working image into
/// the pixels a display or a capture holds: every swapchain compositor draws it into its back buffer in the output's
/// color space, and a render node's preview draws it in SDR. An instance encodes a working image in SDR into an
/// RGBA8 image of its own and reads that back, which is how a capture of a float output, and a presenter's readback of
/// one, becomes PNG pixels. Its pipeline is an entry of the device's <see cref="GpuPassPipelineCache"/>, leased when the
/// instance is created and built on the thread pool; its target, framebuffer and readback are created at the first read
/// and again when the extent changes.
/// </summary>
public sealed class SurfaceEncoder : IDisposable {
    /// <summary>The format the encode writes an SDR capture in.</summary>
    public const GpuPixelFormat CaptureFormat = GpuPixelFormat.R8G8B8A8Unorm;

    // The target's usages: drawn into, then copied out by the readback.
    private const GpuImageUsage TargetUsage = GpuImageUsage.ColorAttachment | GpuImageUsage.Sampled;
    private const string FragmentStem = "display-encode.frag";
    // The writes a published image may have had before a read, and the stages that made them.
    private const GpuAccess SourceWrites = GpuAccess.ShaderWrite | GpuAccess.ColorAttachmentWrite | GpuAccess.TransferWrite;
    private const GpuStage SourceStages = GpuStage.ComputeShader | GpuStage.FragmentShader | GpuStage.ColorAttachmentOutput | GpuStage.Transfer;
    private const string VertexStem = "display.vert";

    private readonly IGpuDeviceContext m_device;
    private readonly GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline> m_lease;
    private readonly string m_owner;

    private IGpuStorageBuffer? m_block;
    private IGpuFramebuffer? m_framebuffer;
    private IGpuCommandPool? m_commands;
    private nint m_pool;
    private IGpuSurfaceReadback? m_readback;
    private nint m_sampler;
    private nint m_set;
    private IGpuImage? m_target;
    private IGpuSurfaceUpload? m_upload;

    /// <summary>Initializes a new instance of the <see cref="SurfaceEncoder"/> class, leasing its pipeline, which builds
    /// on the thread pool.</summary>
    /// <param name="device">The device the encoded images live on.</param>
    /// <param name="pipelines">The device's pass pipelines.</param>
    /// <param name="directX">Whether the device is a Direct3D 12 device, which reads the encode's DXIL rather than its
    /// SPIR-V.</param>
    /// <param name="owner">The name every object the encoder creates is named under.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device"/>, <paramref name="pipelines"/> or
    /// <paramref name="owner"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">The encode's deployed bytecode is missing or cannot be read.</exception>
    public SurfaceEncoder(IGpuDeviceContext device, GpuPassPipelineCache pipelines, bool directX, string owner) {
        ArgumentNullException.ThrowIfNull(argument: device);
        ArgumentNullException.ThrowIfNull(argument: pipelines);
        ArgumentNullException.ThrowIfNull(argument: owner);

        m_device = device;
        m_owner = owner;
        m_lease = pipelines.Acquire(
            device: device,
            key: Key(
                directX: directX,
                renderPass: CaptureRenderPass
            )
        );
    }

    /// <summary>Gets the render pass an SDR capture draws in: one RGBA8 color attachment, cleared, stored and left
    /// shader-readable.</summary>
    public static GpuRenderPassDescription CaptureRenderPass { get; } = new(Colors: [new GpuColorAttachment(
        FinalLayout: GpuImageLayout.ShaderReadOnly,
        Format: CaptureFormat,
        Load: GpuAttachmentLoad.Clear,
        Store: GpuAttachmentStore.Store
    )]);
    /// <summary>Gets the encode's graphics pipeline description: no vertex input, and the one group of
    /// <see cref="DisplayEncodeLayout"/>.</summary>
    public static GpuGraphicsPipelineDescription Description { get; } = new(
        "display-encode",
        new GpuVertexInputLayout(
            Attributes: [],
            StrideBytes: 0
        ),
        Layout: DisplayEncodeLayout.Layout
    );

    /// <summary>Gets the bytes the encoder owns on the device: its target and the readback's staging buffer, each
    /// four bytes a pixel, once a read has created them.</summary>
    public ulong OwnedBytes => ((m_target is { } target)
        ? checked(((((ulong)target.Width) * target.Height) * 8UL))
        : 0UL);
    /// <summary>Gets whether the encode's pipeline is built, so a read records at once.</summary>
    public bool IsReady => (m_lease.Poll() is not null);

    /// <summary>Reads the encode's deployed bytecode for one stage.</summary>
    /// <param name="directX">Whether to read the DXIL rather than the SPIR-V.</param>
    /// <param name="fragment">Whether to read the fragment stage rather than the vertex stage.</param>
    /// <returns>The validated bytecode.</returns>
    /// <exception cref="IOException">The bytecode is missing or cannot be read.</exception>
    /// <exception cref="InvalidDataException">The file is not bytecode of the backend's format.</exception>
    public static byte[] Bytecode(bool directX, bool fragment) {
        var path = Path.Combine(
            path1: AppContext.BaseDirectory,
            path2: "Assets",
            path3: "Runtime",
            path4: ((fragment
                ? FragmentStem
                : VertexStem) + (directX
                ? ".dxil"
                : ".spv"))
        );
        var bytecode = File.ReadAllBytes(path: path);

        ShaderBytecode.ValidateFormat(bytecode: bytecode);

        return bytecode;
    }
    /// <summary>Returns the pass-pipeline key of the encode drawn in a render pass, which every writer leases from the
    /// device's <see cref="GpuPassPipelineCache"/>.</summary>
    /// <param name="directX">Whether the device is a Direct3D 12 device.</param>
    /// <param name="renderPass">The render pass the encode draws in: a swapchain's, or <see cref="CaptureRenderPass"/>.</param>
    /// <returns>The key.</returns>
    /// <exception cref="IOException">The encode's deployed bytecode is missing or cannot be read.</exception>
    public static GpuPassPipelineKey Key(bool directX, GpuRenderPassDescription renderPass) => GpuPassPipelineKey.OfGraphics(
        description: Description,
        fragment: Bytecode(
            directX: directX,
            fragment: true
        ),
        renderPass: renderPass,
        vertex: Bytecode(
            directX: directX,
            fragment: false
        )
    );
    /// <summary>Encodes a same-device image in SDR and reads the encoded pixels back, blocking until the device has
    /// written them; the image's writes must complete in an earlier submission on the device's queue. An image in any
    /// layout but <see cref="GpuImageLayout.ShaderReadOnly"/> moves into it for the draw, which samples it there, and
    /// back into its own layout after, in the same submission.</summary>
    /// <param name="image">The native handle used for layout transitions; zero is sufficient for an uploaded image
    /// already in <see cref="GpuImageLayout.ShaderReadOnly"/>.</param>
    /// <param name="imageView">The native handle of the view of the image to encode.</param>
    /// <param name="width">The image's width, in pixels.</param>
    /// <param name="height">The image's height, in pixels.</param>
    /// <param name="layout">The layout the image is in: <see cref="GpuImageLayout.ShaderReadOnly"/>,
    /// <see cref="GpuImageLayout.General"/> or <see cref="GpuImageLayout.External"/>, the layouts a node publishes
    /// in.</param>
    /// <returns>Tightly packed <see cref="CaptureFormat"/> pixels, four bytes each.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="layout"/> is none of the layouts a node publishes
    /// in.</exception>
    /// <exception cref="InvalidOperationException">The device's descriptor heaps cannot admit the encoder's pool.</exception>
    /// <exception cref="DeviceLostException">The device was lost.</exception>
    public ReadOnlyMemory<byte> ReadSdr(nint image, nint imageView, uint width, uint height, GpuImageLayout layout) {
        if (layout is not (GpuImageLayout.ShaderReadOnly or GpuImageLayout.General or GpuImageLayout.External)) {
            throw new ArgumentOutOfRangeException(
                actualValue: layout,
                message: "The display encode reads an image published in ShaderReadOnly, General or External.",
                paramName: nameof(layout)
            );
        }

        var pass = (m_lease.Poll() ?? m_lease.Wait(cancellationToken: CancellationToken.None));
        var gpu = m_device.Services;

        EnsureObjects(
            gpu: gpu,
            height: height,
            pass: pass,
            width: width
        );
        gpu.Bindings.WriteSampledImage(
            arrayElement: 0,
            binding: DisplayEncodeLayout.SourceImageBinding,
            descriptorSetHandle: m_set,
            imageViewHandle: imageView
        );

        var recorder = gpu.Recorder;
        var command = m_commands!.CommandBufferHandle;
        var target = m_target!;

        recorder.BeginCommandBuffer(commandBufferHandle: command);
        // Every write the image may have had becomes visible to the draw's sampling, and an image published in another
        // layout moves into the one a sampled image is read in.
        if (layout == GpuImageLayout.ShaderReadOnly) {
            recorder.MemoryBarrier(
                commandBufferHandle: command,
                destinationAccessMask: GpuAccess.ShaderRead,
                destinationStageMask: GpuStage.FragmentShader,
                sourceAccessMask: SourceWrites,
                sourceStageMask: SourceStages
            );
        } else {
            recorder.TransitionImageLayout(
                commandBufferHandle: command,
                destinationAccessMask: GpuAccess.ShaderRead,
                destinationStageMask: GpuStage.FragmentShader,
                imageHandle: image,
                newLayout: GpuImageLayout.ShaderReadOnly,
                oldLayout: layout,
                sourceAccessMask: SourceWrites,
                sourceStageMask: SourceStages
            );
        }
        recorder.TransitionImageLayout(
            command,
            target.ImageHandle,
            GpuImageLayout.Undefined,
            GpuImageLayout.RenderTarget,
            GpuAccess.None,
            GpuAccess.ColorAttachmentWrite,
            GpuStage.TopOfPipe,
            GpuStage.ColorAttachmentOutput
        );
        recorder.BeginRenderPass(
            command,
            m_framebuffer!
        );
        recorder.BindPipeline(
            bindPoint: GpuBindPoint.Graphics,
            commandBufferHandle: command,
            pipelineHandle: pass.Handle
        );
        recorder.BindDescriptorSet(
            bindPoint: GpuBindPoint.Graphics,
            commandBufferHandle: command,
            descriptorSetHandle: m_set,
            group: DisplayEncodeLayout.Group,
            pipelineLayoutHandle: pass.LayoutHandle
        );
        recorder.Draw(
            commandBufferHandle: command,
            parameters: new GpuDrawParameters(
                3,
                1
            )
        );
        recorder.EndRenderPass(commandBufferHandle: command);
        // The image goes back to the layout its producer left it in, which its next frame's barriers name.
        if (layout != GpuImageLayout.ShaderReadOnly) {
            recorder.TransitionImageLayout(
                commandBufferHandle: command,
                destinationAccessMask: GpuAccess.ShaderRead | GpuAccess.ShaderWrite,
                destinationStageMask: SourceStages,
                imageHandle: image,
                newLayout: layout,
                oldLayout: GpuImageLayout.ShaderReadOnly,
                sourceAccessMask: GpuAccess.ShaderRead,
                sourceStageMask: GpuStage.FragmentShader
            );
        }
        recorder.EndCommandBuffer(commandBufferHandle: command);
        gpu.QueueSubmitter.SubmitAndWait(commandBufferHandles: [command]);

        return m_readback!.Read(
            bytesPerPixel: 4,
            format: CaptureFormat,
            height: height,
            sourceImageHandle: target.ImageHandle,
            sourceLayout: GpuImageLayout.ShaderReadOnly,
            width: width
        );
    }
    /// <summary>Encodes a presented same-device image or CPU-pixel surface in SDR, as <see cref="ReadSdr"/> does. A
    /// presented surface is in <see cref="GpuImageLayout.ShaderReadOnly"/>, the layout a compositor samples it
    /// in.</summary>
    /// <param name="surface">The surface; a same-device image or CPU pixels in working values.</param>
    /// <returns>A CPU-pixel surface of the same extent in <see cref="CaptureFormat"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="surface"/> is neither a same-device image nor CPU pixels.</exception>
    /// <exception cref="InvalidOperationException">The device's descriptor heaps cannot admit the encoder's pool.</exception>
    /// <exception cref="DeviceLostException">The device was lost.</exception>
    public Surface ReadSurface(Surface surface) {
        if (!surface.IsSameDeviceImage && !surface.IsCpuPixels) {
            throw new ArgumentException(
                message: "Only a same-device image or CPU-pixel surface is encoded.",
                paramName: nameof(surface)
            );
        }

        var imageView = surface.ImageViewHandle;

        if (surface.IsCpuPixels) {
            m_upload ??= m_device.Services.SurfaceTransferFactory.CreateUpload();
            imageView = m_upload.Upload(
                format: surface.Format,
                height: surface.Height,
                pixels: surface.Pixels,
                width: surface.Width
            );
        }

        return Surface.CpuPixels(
            format: CaptureFormat,
            height: surface.Height,
            pixels: ReadSdr(
                height: surface.Height,
                image: surface.ImageHandle,
                imageView: imageView,
                layout: GpuImageLayout.ShaderReadOnly,
                width: surface.Width
            ),
            width: surface.Width
        );
    }
    /// <summary>Releases every object the encoder created and its lease on the pipeline; call it once nothing it
    /// recorded is in flight.</summary>
    public void Dispose() {
        ReleaseTarget();
        m_upload?.Dispose();
        m_upload = null;
        m_readback?.Dispose();
        m_readback = null;
        m_commands?.Dispose();
        m_commands = null;

        var bindings = m_device.Services.Bindings;

        if (m_sampler != 0) {
            bindings.DestroySampler(samplerHandle: m_sampler);
            m_sampler = 0;
        }
        if (m_pool != 0) {
            bindings.DestroyPool(poolHandle: m_pool);
            m_pool = 0;
            m_set = 0;
        }

        m_block?.Dispose();
        m_block = null;
        m_lease.Release();
    }

    // Creates what a read records with: once, the pool, its set, the sampler, the SDR block, the command pool and the
    // readback; per extent, the target and its framebuffer.
    private void EnsureObjects(GpuDeviceServices gpu, GpuPassPipeline pass, uint width, uint height) {
        if (m_pool == 0) {
            var sizes = GpuDescriptorPoolSizes.ForGroups(groups: DisplayEncodeLayout.Layout.Groups);
            var bindings = gpu.Bindings;

            if (!bindings.CanAdmit(
                owner: m_owner,
                pools: [sizes],
                refusal: out var refusal
            )) {
                throw new InvalidOperationException(message: $"The display encode for '{m_owner}' is refused: {refusal}");
            }

            m_pool = bindings.CreatePool(
                name: new GpuObjectName(
                    owner: m_owner,
                    part: "encode"
                ),
                sizes: sizes
            );
            m_set = bindings.AllocateSet(
                m_pool,
                pass.GroupLayoutHandles[((int)DisplayEncodeLayout.Group)],
                name: new GpuObjectName(
                    owner: m_owner,
                    part: "encode"
                )
            );
            m_sampler = bindings.CreateSampler();
            bindings.WriteSampler(
                arrayElement: 0,
                binding: DisplayEncodeLayout.SamplerBinding,
                descriptorSetHandle: m_set,
                samplerHandle: m_sampler
            );
            m_block = CreateBlock(
                gpu: gpu,
                name: new GpuObjectName(
                    owner: m_owner,
                    part: "encode-block"
                ),
                output: DisplayOutput.Sdr(format: CaptureFormat),
                paperWhiteNits: DisplayOutput.SdrWhiteNits
            );
            bindings.WriteConstantBuffer(
                arrayElement: 0,
                binding: DisplayEncodeLayout.BlockBinding,
                bufferHandle: m_block.BufferHandle,
                bufferSize: m_block.SizeBytes,
                descriptorSetHandle: m_set
            );
            m_commands = gpu.CommandPoolFactory.Create(name: new GpuObjectName(
                owner: m_owner,
                part: "encode-commands"
            ));
            m_readback = gpu.SurfaceTransferFactory.CreateReadback();
        }
        if (
            (m_target is { } current) &&
            (current.Width == width) &&
            (current.Height == height)
        ) {
            return;
        }

        ReleaseTarget();
        m_target = gpu.ImageFactory.Create(
            format: CaptureFormat,
            height: height,
            name: new GpuObjectName(
                owner: m_owner,
                part: "encode-target"
            ),
            usage: TargetUsage,
            width: width
        );
        m_framebuffer = gpu.RenderPassFactory.CreateFramebuffer(
            pass.RenderPass!,
            [m_target],
            null
        );
    }
    private void ReleaseTarget() {
        m_framebuffer?.Dispose();
        m_framebuffer = null;
        m_target?.Dispose();
        m_target = null;
    }

    /// <summary>Creates a constant buffer holding the encode block for a display output
    /// (<see cref="DisplayEncodeLayout.WriteBlock"/>), sized to one constant-buffer view.</summary>
    /// <param name="gpu">The device's services.</param>
    /// <param name="name">The buffer's name.</param>
    /// <param name="output">The output the encode writes for.</param>
    /// <param name="paperWhiteNits">The paper-white level, in nits.</param>
    /// <returns>The buffer, which the caller owns.</returns>
    public static IGpuStorageBuffer CreateBlock(GpuDeviceServices gpu, in GpuObjectName name, DisplayOutput output, double paperWhiteNits) {
        ArgumentNullException.ThrowIfNull(argument: gpu);

        Span<byte> block = stackalloc byte[((int)IGpuBindings.ConstantBufferAlignment)];

        DisplayEncodeLayout.WriteBlock(
            block: block,
            output: output,
            paperWhiteNits: paperWhiteNits
        );

        return gpu.BufferFactory.CreateHostVisible(
            data: block,
            name: name,
            usage: GpuBufferUsage.Uniform
        );
    }
}
