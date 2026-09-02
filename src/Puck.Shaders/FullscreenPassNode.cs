using System.Buffers.Binary;
using System.Runtime.CompilerServices;

using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Assets;
using Puck.Hosting;

namespace Puck.Shaders;

/// <summary>
/// A shader set run as one fullscreen-triangle graphics pass over an inner render node's output, driven entirely
/// by the set's <see cref="ShaderSetManifest"/>: the pipeline is built from the manifest's stages and bindings, the
/// inner surface is bound at the manifest's one <c>sampledImage</c> binding, and the push-constant block is filled
/// field by field from the manifest's declared sources — a bound config value, the fixed-step simulation clock
/// (quantized to an authored rate, so a frame carries the value of the period its <c>ElapsedTicks</c> falls in on
/// every run, machine, and backend), the pass's resolution, or its own frame counter. The pass owns its render
/// target, fence, pipeline, and descriptor set, and disposes the inner node with itself.
/// </summary>
public sealed class FullscreenPassNode : IRenderNode, ICaptureRequestTarget {
    private readonly IGpuCommandRecorder m_commandRecorder;
    private readonly Func<uint, uint, IGpuRenderTarget> m_createRenderTarget;
    private readonly NodeDescriptor m_descriptor;
    private readonly IGpuDescriptorAllocator m_descriptorAllocator;
    private readonly IGpuDeviceContext m_deviceContext;
    private readonly ReadOnlyMemory<byte> m_fragmentBytecode;
    private readonly uint m_height;
    private readonly IRenderNode m_inner;
    private readonly ShaderSetManifest m_manifest;
    private readonly IGpuPipelineFactory m_pipelineFactory;
    private readonly byte[] m_pushConstantData;
    private readonly ShaderPushConstantLayout? m_pushConstantLayout;
    private readonly IGpuQueueSubmitter m_queueSubmitter;
    private readonly uint m_sampledImageBinding;
    private readonly IGpuShaderModuleFactory m_shaderModuleFactory;
    private readonly IGpuSurfaceTransferFactory m_surfaceTransferFactory;
    private readonly IGpuVertexBufferFactory m_vertexBufferFactory;
    private readonly ReadOnlyMemory<byte> m_vertexBytecode;
    private readonly uint m_width;

    private bool m_captureUnavailable;
    private ShaderConfigValues m_config;
    private nint m_descriptorPool;
    private nint m_descriptorSet;
    private bool m_disposed;
    private uint m_frameCounter;
    private IGpuSubmissionFence? m_frameFence;
    private IGpuShaderModule? m_fragmentShader;
    private nint m_lastImageViewHandle;
    private Dictionary<string, ShaderConfigValue>? m_liveConfig;
    private Dictionary<string, byte[]>? m_liveConfigBytes;
    private string? m_pendingCapturePath;
    private IGpuPipeline? m_pipeline;
    private IGpuSurfaceReadback? m_readback;
    private IGpuRenderTarget? m_renderTarget;
    private bool m_resourcesReady;
    private nint m_sampler;
    private IGpuVertexBuffer? m_vertexBuffer;
    private IGpuShaderModule? m_vertexShader;

    /// <summary>Initializes a new instance of the <see cref="FullscreenPassNode"/> class.</summary>
    /// <param name="inner">The producer whose output this pass samples; disposed with this node.</param>
    /// <param name="manifest">The loaded shader set: a graphics set with exactly one <c>sampledImage</c> binding and no
    /// other bindings.</param>
    /// <param name="config">The set's bound configuration (<see cref="ShaderSetManifest.BindConfig"/>).</param>
    /// <param name="services">The GPU services, on the same device as <paramref name="inner"/>.</param>
    /// <param name="hostsOnDirectX">Whether the resolved host backend is Direct3D 12 — selects the bytecode.</param>
    /// <param name="width">The pass width in pixels, fixed for the node's life.</param>
    /// <param name="height">The pass height in pixels.</param>
    /// <exception cref="InvalidDataException"><paramref name="manifest"/> is not a graphics set, or its bindings are
    /// not exactly one <c>sampledImage</c>.</exception>
    public FullscreenPassNode(IRenderNode inner, ShaderSetManifest manifest, ShaderConfigValues config, IFullscreenPassServices services, bool hostsOnDirectX, uint width, uint height) {
        ArgumentNullException.ThrowIfNull(argument: inner);
        ArgumentNullException.ThrowIfNull(argument: manifest);
        ArgumentNullException.ThrowIfNull(argument: config);
        ArgumentNullException.ThrowIfNull(argument: services);

        if (!manifest.IsGraphics) {
            throw new InvalidDataException(message: $"'{manifest.Name}' is a compute set; a fullscreen pass needs a vertex+fragment set.");
        }
        if ((manifest.Bindings.Count != 1) || (manifest.Bindings[0].Kind != ShaderSetManifestBindingKind.SampledImage) || (manifest.Bindings[0].Count != 1)) {
            throw new InvalidDataException(message: $"'{manifest.Name}' must declare exactly one sampledImage binding (the inner surface) and nothing else to run as a fullscreen pass.");
        }

        var bytecodeExtension = ShaderBytecode.FileExtension(hostsOnDirectX: hostsOnDirectX);

        m_commandRecorder = services.CommandRecorder;
        m_config = config;
        m_createRenderTarget = services.CreateRenderTarget;
        m_descriptor = new NodeDescriptor(Name: manifest.Name, SurfaceId: SurfaceId.New());
        m_descriptorAllocator = services.DescriptorAllocator;
        m_deviceContext = services.DeviceContext;
        m_fragmentBytecode = File.ReadAllBytes(path: manifest.BytecodePath(stem: manifest.Stages.Fragment!, bytecodeExtension: bytecodeExtension));
        m_height = height;
        m_inner = inner;
        m_manifest = manifest;
        m_pipelineFactory = services.PipelineFactory;
        m_pushConstantLayout = manifest.PushConstantLayout;
        m_pushConstantData = new byte[(m_pushConstantLayout?.SizeBytes ?? 0)];
        m_queueSubmitter = services.QueueSubmitter;
        m_sampledImageBinding = manifest.Bindings[0].VulkanBinding;
        m_shaderModuleFactory = services.ShaderModuleFactory;
        m_surfaceTransferFactory = services.SurfaceTransferFactory;
        m_vertexBufferFactory = services.VertexBufferFactory;
        m_vertexBytecode = File.ReadAllBytes(path: manifest.BytecodePath(stem: manifest.Stages.Vertex!, bytecodeExtension: bytecodeExtension));
        m_width = width;

        FillStaticPushConstants();
    }

    /// <summary>Gets the pass's live config values — the manifest's bound config, as overwritten by any
    /// <see cref="TrySetConfig"/> call since.</summary>
    public ShaderConfigValues Config => m_config;
    /// <inheritdoc/>
    public NodeDescriptor Descriptor => m_descriptor;
    /// <inheritdoc/>
    public string? PendingCapturePath => (m_pendingCapturePath ?? (m_inner as ICaptureRequestTarget)?.PendingCapturePath);

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        ReleaseGpuResources();
        m_inner.Dispose();
    }
    /// <inheritdoc/>
    public void OnDeviceLost() {
        ReleaseGpuResources();
        m_inner.OnDeviceLost();
    }
    /// <inheritdoc/>
    public Surface ProduceFrame(in FrameContext context) {
        if (m_disposed) {
            return default;
        }

        // Same queue, same device: the inner producer's output is already shader-readable for the fragment stage
        // before its submit, so this pass samples it with no CPU wait.
        var inner = m_inner.ProduceFrame(context: context);

        if (inner.IsEmpty || (0 == inner.ImageViewHandle)) {
            ForwardPendingCapture();

            return inner;
        }

        EnsureResources();
        m_frameFence!.Wait();

        if (inner.ImageViewHandle != m_lastImageViewHandle) {
            m_descriptorAllocator.WriteCombinedImageSampler(
                arrayElement: 0,
                binding: m_sampledImageBinding,
                descriptorSetHandle: m_descriptorSet,
                deviceHandle: m_deviceContext.DeviceHandle,
                imageViewHandle: inner.ImageViewHandle,
                samplerHandle: m_sampler
            );

            m_lastImageViewHandle = inner.ImageViewHandle;
        }

        FillPerFramePushConstants(elapsedTicks: context.ElapsedTicks);
        m_frameCounter++;

        Span<nint> commandBuffers = [RecordPass()];

        m_queueSubmitter.Submit(commandBufferHandles: commandBuffers, deviceContext: m_deviceContext, fence: m_frameFence!);
        CaptureIfPending();

        return Surface.SameDeviceImage(
            imageHandle: m_renderTarget!.ImageHandle,
            imageViewHandle: m_renderTarget!.ImageViewHandle,
            width: m_width,
            height: m_height,
            format: SurfaceFormat.R8G8B8A8Unorm
        );
    }
    /// <inheritdoc/>
    public void RequestCapture(string path) {
        m_pendingCapturePath = path;
    }
    /// <summary>Overwrites one scalar-float config field's live value, and — when a push-constant slot sources it —
    /// the slot's bytes for the next frame. The write a presentation binding drives per frame; the manifest's
    /// originally bound config is unaffected. Allocates only on a field's first write; later writes to the same
    /// field update the live bytes in place.</summary>
    /// <param name="field">The config field's name.</param>
    /// <param name="value">The new value; must be finite and inside the field's declared range.</param>
    /// <returns><see langword="true"/> when <paramref name="field"/> names a <c>float</c>-typed config field of this
    /// pass's manifest and <paramref name="value"/> satisfies its schema; <see langword="false"/> for an unknown
    /// field, any other type (a vector, <c>uint</c>, or <c>int</c> field), or a value the field's own
    /// <c>min</c>/<c>max</c> would refuse at bind time.</returns>
    public bool TrySetConfig(string field, float value) {
        if ((m_manifest.Config is not { } schema) || !schema.TryGetValue(key: field, value: out var declared) || (declared.Type != ShaderValueType.Float)) {
            return false;
        }
        if (!float.IsFinite(f: value) || !ShaderConfigBinding.InRange(field: declared, value: value)) {
            return false;
        }

        if (m_liveConfig is not { } live) {
            live = new Dictionary<string, ShaderConfigValue>(comparer: StringComparer.Ordinal);

            foreach (var name in m_config.Names) {
                live[name] = m_config[name];
            }

            m_liveConfig = live;
            m_liveConfigBytes = new Dictionary<string, byte[]>(comparer: StringComparer.Ordinal);
            m_config = new ShaderConfigValues(values: live);
        }

        if (!m_liveConfigBytes!.TryGetValue(key: field, value: out var bytes)) {
            bytes = new byte[ShaderValueTypes.ComponentBytes];
            m_liveConfigBytes[field] = bytes;
            live[field] = new ShaderConfigValue(Bytes: bytes, Type: ShaderValueType.Float);
        }

        BinaryPrimitives.WriteSingleLittleEndian(destination: bytes, value: value);

        if (m_pushConstantLayout is { } layout) {
            foreach (var slot in layout.Slots) {
                if ((slot.Kind == ShaderPushConstantSourceKind.Config) && string.Equals(a: slot.ConfigField, b: field, comparisonType: StringComparison.Ordinal)) {
                    bytes.CopyTo(destination: m_pushConstantData.AsSpan(start: ((int)slot.Offset)));
                }
            }
        }

        return true;
    }

    // Reads back this pass's own render target (the composed result — what the player sees when nothing draws over
    // it) and writes it as a PNG.
    private void CaptureIfPending() {
        if (m_pendingCapturePath is not { } path) {
            return;
        }

        m_pendingCapturePath = null;

        if (m_captureUnavailable) {
            Console.Error.WriteLine(value: $"[capture] skipped, Puck.Assets is unavailable — no file written to {path}");

            return;
        }

        m_readback ??= m_surfaceTransferFactory.CreateReadback(deviceContext: m_deviceContext);

        var pixels = m_readback.Read(
            bytesPerPixel: 4,
            deviceContext: m_deviceContext,
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: m_height,
            sourceImageHandle: m_renderTarget!.ImageHandle,
            sourceLayout: GpuImageLayout.ShaderReadOnly,
            width: m_width
        );

        if (TryWriteCapturePng(
            height: ((int)m_height),
            path: path,
            rgba: pixels,
            width: ((int)m_width)
        )) {
            Console.Error.WriteLine(value: $"[capture] {m_manifest.Name} -> {path}");
        } else {
            m_captureUnavailable = true;
        }
    }
    // Passing the inner frame through untouched: hand a pending capture down so the readback lands on whatever
    // actually produced the shown frame. Keeping it armed when the inner cannot serve it is what stops a request
    // from vanishing silently — PendingCapturePath keeps reporting it until some node writes the file.
    private void ForwardPendingCapture() {
        if (m_pendingCapturePath is not { } path) {
            return;
        }

        if (m_inner is ICaptureRequestTarget target) {
            m_pendingCapturePath = null;
            target.RequestCapture(path: path);
        }
    }
    private void EnsureResources() {
        if (m_resourcesReady) {
            return;
        }

        m_renderTarget = m_createRenderTarget(m_width, m_height);
        m_frameFence = m_queueSubmitter.CreateSubmissionFence(deviceContext: m_deviceContext);
        m_vertexShader = m_shaderModuleFactory.Create(bytecode: m_vertexBytecode, deviceContext: m_deviceContext, stage: GpuShaderStage.Vertex);
        m_fragmentShader = m_shaderModuleFactory.Create(bytecode: m_fragmentBytecode, deviceContext: m_deviceContext, stage: GpuShaderStage.Fragment);
        m_vertexBuffer = m_vertexBufferFactory.Create(deviceContext: m_deviceContext, strideBytes: FullscreenTriangle.StrideBytes, vertexData: FullscreenTriangle.CreateVertexData());

        var description = new GpuGraphicsPipelineDescription(
            Name: m_manifest.Name,
            VertexInput: new GpuVertexInputLayout(
                StrideBytes: FullscreenTriangle.StrideBytes,
                Attributes: [new GpuVertexAttribute(Format: GpuVertexFormat.R32G32Float, Location: 0, OffsetBytes: 0)]
            ),
            TextureSamplerCount: 1,
            EnableStorageBuffer: false,
            PushConstantBinding: ((m_pushConstantLayout is { } layout)
                ? new GpuPushConstantBinding(data: new byte[layout.SizeBytes], offset: 0, stageFlags: layout.Stages)
                : null)
        );

        m_manifest.ValidateBindings(description: description);
        m_pipeline = m_pipelineFactory.Create(
            description: description,
            deviceContext: m_deviceContext,
            fragmentShaderModule: m_fragmentShader,
            height: m_height,
            renderTarget: m_renderTarget,
            vertexShaderModule: m_vertexShader,
            width: m_width
        );

        var deviceHandle = m_deviceContext.DeviceHandle;

        m_descriptorPool = m_descriptorAllocator.CreatePool(
            deviceHandle: deviceHandle,
            sizes: new GpuDescriptorPoolSizes(AccelerationStructureCount: 0, CombinedImageSamplerCount: 1, MaxSets: 1, StorageBufferCount: 0, StorageImageCount: 0)
        );
        m_descriptorSet = m_descriptorAllocator.AllocateSet(descriptorSetLayoutHandle: m_pipeline.DescriptorSetLayoutHandle, deviceHandle: deviceHandle, poolHandle: m_descriptorPool);
        m_sampler = m_descriptorAllocator.CreateSampler(deviceHandle: deviceHandle);
        m_resourcesReady = true;
    }
    private void FillPerFramePushConstants(ulong elapsedTicks) {
        if (m_pushConstantLayout is not { } layout) {
            return;
        }

        foreach (var slot in layout.Slots) {
            var destination = m_pushConstantData.AsSpan(start: ((int)slot.Offset), length: ((int)slot.Type.SizeBytes()));

            switch (slot.Kind) {
                case ShaderPushConstantSourceKind.Tick: {
                        var period = QuantizationPeriodTicks(slot: slot);
                        var value = (elapsedTicks / period);

                        BinaryPrimitives.WriteUInt32LittleEndian(destination: destination, value: ((uint)value));

                        if (slot.Type == ShaderValueType.Uint2) {
                            BinaryPrimitives.WriteUInt32LittleEndian(destination: destination[4..], value: ((uint)(value >> 32)));
                        }

                        break;
                    }
                case ShaderPushConstantSourceKind.Frame:
                    BinaryPrimitives.WriteUInt32LittleEndian(destination: destination, value: m_frameCounter);

                    break;
                default:
                    break;
            }
        }
    }
    private void FillStaticPushConstants() {
        if (m_pushConstantLayout is not { } layout) {
            return;
        }

        foreach (var slot in layout.Slots) {
            var destination = m_pushConstantData.AsSpan(start: ((int)slot.Offset), length: ((int)slot.Type.SizeBytes()));

            switch (slot.Kind) {
                case ShaderPushConstantSourceKind.Config:
                    m_config[slot.ConfigField!].Bytes.Span.CopyTo(destination: destination);

                    break;
                case ShaderPushConstantSourceKind.Resolution:
                    if (slot.Type == ShaderValueType.Float2) {
                        BinaryPrimitives.WriteSingleLittleEndian(destination: destination, value: m_width);
                        BinaryPrimitives.WriteSingleLittleEndian(destination: destination[4..], value: m_height);
                    } else {
                        BinaryPrimitives.WriteUInt32LittleEndian(destination: destination, value: m_width);
                        BinaryPrimitives.WriteUInt32LittleEndian(destination: destination[4..], value: m_height);
                    }

                    break;
                default:
                    break;
            }
        }
    }
    private ulong QuantizationPeriodTicks(ShaderPushConstantSlot slot) {
        if (slot.QuantizeHzLiteral is { } literal) {
            return EngineTicks.PerRate(ratePerSecond: literal);
        }
        if (slot.QuantizeHzConfigField is { } configField) {
            return EngineTicks.PerRate(ratePerSecond: m_config[configField].ComponentBits(index: 0));
        }

        return 1;
    }
    // Attempts one capture write, surviving (and loudly reporting) an environment that refuses to load Puck.Assets.
    // Returns false so the caller can latch m_captureUnavailable and stop retrying a doomed load.
    private static bool TryWriteCapturePng(string path, ReadOnlyMemory<byte> rgba, int width, int height) =>
        CapturePngWriteGuard.TryWrite(
            state: (Path: path, Rgba: rgba, Width: width, Height: height),
            writeCore: static state => WriteCapturePngCore(
                height: state.Height,
                path: state.Path,
                rgba: state.Rgba,
                width: state.Width
            )
        );
    // The ONLY member touching the Puck.Assets-typed PngEncoder call, kept non-inlined so the CLR resolves and loads
    // Puck.Assets.dll on the first actual capture rather than on every produced frame. CapturePngWriteGuard's
    // try/catch wraps the call one frame up, where a failure to load the assembly is observable.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WriteCapturePngCore(string path, ReadOnlyMemory<byte> rgba, int width, int height) {
        PngEncoder.Write(
            height: height,
            path: path,
            rgba: rgba.Span,
            width: width
        );
    }
    private nint RecordPass() {
        var deviceHandle = m_deviceContext.DeviceHandle;
        var commandBufferHandle = m_renderTarget!.CommandBufferHandle;

        m_commandRecorder.BeginCommandBuffer(commandBufferHandle: commandBufferHandle, deviceHandle: deviceHandle);
        m_commandRecorder.BeginDebugGroup(commandBufferHandle: commandBufferHandle, deviceHandle: deviceHandle, label: m_manifest.Name);
        m_commandRecorder.BeginRenderPass(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle,
            framebufferHandle: m_renderTarget.FramebufferHandle,
            height: m_renderTarget.Height,
            renderPassHandle: m_renderTarget.RenderPassHandle,
            width: m_renderTarget.Width
        );
        m_commandRecorder.SetScissor(commandBufferHandle: commandBufferHandle, deviceHandle: deviceHandle, height: m_renderTarget.Height, width: m_renderTarget.Width, x: 0, y: 0);
        m_commandRecorder.BindGraphicsPipeline(commandBufferHandle: commandBufferHandle, deviceHandle: deviceHandle, pipelineHandle: m_pipeline!.Handle);
        m_commandRecorder.BindVertexBuffer(commandBufferHandle: commandBufferHandle, deviceHandle: deviceHandle, vertexBufferHandle: m_vertexBuffer!.BufferHandle);

        if (m_pushConstantLayout is { } layout) {
            m_commandRecorder.PushConstants(
                commandBufferHandle: commandBufferHandle,
                data: m_pushConstantData,
                deviceHandle: deviceHandle,
                offset: 0,
                pipelineLayoutHandle: m_pipeline.LayoutHandle,
                stageFlags: layout.Stages
            );
        }

        m_commandRecorder.BindDescriptorSet(commandBufferHandle: commandBufferHandle, descriptorSetHandle: m_descriptorSet, deviceHandle: deviceHandle, pipelineLayoutHandle: m_pipeline.LayoutHandle);
        m_commandRecorder.Draw(commandBufferHandle: commandBufferHandle, deviceHandle: deviceHandle, parameters: new GpuDrawParameters(vertexCount: FullscreenTriangle.VertexCount, instanceCount: 1));
        m_commandRecorder.EndRenderPass(commandBufferHandle: commandBufferHandle, deviceHandle: deviceHandle);
        m_commandRecorder.EndDebugGroup(commandBufferHandle: commandBufferHandle, deviceHandle: deviceHandle);
        m_commandRecorder.EndCommandBuffer(commandBufferHandle: commandBufferHandle, deviceHandle: deviceHandle);

        return commandBufferHandle;
    }
    private void ReleaseGpuResources() {
        if (!m_resourcesReady) {
            return;
        }

        m_frameFence?.Wait();
        m_readback?.Dispose();
        m_readback = null;
        m_vertexBuffer?.Dispose();
        m_vertexBuffer = null;
        m_pipeline?.Dispose();
        m_pipeline = null;
        m_vertexShader?.Dispose();
        m_vertexShader = null;
        m_fragmentShader?.Dispose();
        m_fragmentShader = null;
        m_frameFence?.Dispose();
        m_frameFence = null;
        m_renderTarget?.Dispose();
        m_renderTarget = null;
        m_lastImageViewHandle = 0;
        m_resourcesReady = false;
    }
}
