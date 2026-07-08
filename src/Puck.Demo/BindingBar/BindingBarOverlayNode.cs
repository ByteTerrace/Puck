using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Assets;
using Puck.Compositing;
using Puck.Hosting;

namespace Puck.Demo.BindingBar;

/// <summary>
/// The binding-bar overlay: wraps any same-device inner producer whose surface exposes a sampleable image view,
/// samples it in one fullscreen fragment pass, and draws the action-bar cluster on top — slot plates with
/// procedural SDF action icons and gamepad-glyph badges, positioned by <see cref="BindingBarLayout"/>. Per-slot
/// data rides a host-visible storage buffer rewritten each frame (12-60 slots would blow any push-constant
/// budget); only the scalar style knobs ride push constants. The binding-bar concept lives entirely in the demo —
/// the reusable engine is untouched.
/// </summary>
internal sealed class BindingBarOverlayNode : IRenderNode {
    // 60 layout slots (5 bars) + up to 4 modifier pips.
    private const int MaxSlots = 64;
    // Two float4s per slot: (center.xy, plateHalf, glyphHalf) + (packed glyph<<16|icon, glyphOffset.xy, packed alpha|flags).
    private const int SlotFloatCount = 8;
    private const int SlotStrideBytes = (SlotFloatCount * sizeof(float));
    // Header float4 (slotCount, cornerRadiusRatio, globalAlpha, pressedBoost) + style float4 (plateDarkness, outlineWidth, aaWidth, reserved).
    private const int PushConstantByteLength = ((sizeof(float) * 4) * 2);
    private const uint SamplerBinding = 0;
    private const uint VertexCount = 3;
    private const uint VertexStrideBytes = (sizeof(float) * 2);

    private static readonly byte[] FullscreenTriangleVertexData = CreateFullscreenTriangleVertexData();
    private readonly GpuCompositor m_compositor;
    private readonly Func<IGpuRenderTarget> m_createRenderTarget;
    private readonly IGpuDescriptorAllocator m_descriptorAllocator;
    private readonly NodeDescriptor m_descriptor;
    private readonly IGpuDeviceContext m_deviceContext;
    private readonly ReadOnlyMemory<byte> m_fragmentBytecode;
    private readonly uint m_height;
    private readonly IRenderNode m_inner;
    private readonly BindingBarLayoutOptions m_layoutOptions;
    private readonly IGpuPipelineFactory m_pipelineFactory;
    // The push-constant payload is constant except for floats[0] (the slot count), so its backing array and binding
    // are allocated once and the count is rewritten in place each frame rather than reallocated.
    private readonly byte[] m_pushConstantData = new byte[PushConstantByteLength];
    private readonly IGpuQueueSubmitter m_queueSubmitter;
    private readonly IGpuShaderModuleFactory m_shaderModuleFactory;
    private readonly Func<NormalizedRect>? m_regionSource;
    private readonly float[] m_slotScratch = new float[MaxSlots * SlotFloatCount];
    private readonly IBindingBarSource m_source;
    private readonly uint m_storageBufferBinding;
    private readonly IGpuStorageBufferFactory m_storageBufferFactory;
    private readonly IGpuVertexBufferFactory m_vertexBufferFactory;
    private readonly ReadOnlyMemory<byte> m_vertexBytecode;
    private readonly uint m_width;
    private nint m_descriptorPool;
    private nint m_descriptorSet;
    private bool m_disposed;
    private GpuDrawCommand[]? m_drawCommands;
    private IGpuShaderModule? m_fragmentShader;
    private nint m_lastImageViewHandle;
    private IGpuPipeline? m_pipeline;
    private AssetContentHash m_pipelineId;
    private IReadOnlyDictionary<AssetContentHash, IGpuPipeline>? m_pipelines;
    private GpuPushConstantBinding? m_pushConstants;
    private IGpuRenderTarget? m_renderTarget;
    private bool m_resourcesReady;
    private nint m_sampler;
    private IGpuStorageBuffer? m_slotBuffer;
    private IGpuShaderModule? m_vertexShader;
    private IGpuVertexBuffer? m_vertexBuffer;

    /// <summary>Initializes a new instance of the <see cref="BindingBarOverlayNode"/> class.</summary>
    /// <param name="inner">The producer whose render the bar is drawn over (its surface must be sampleable on this device).</param>
    /// <param name="source">The per-frame bar state to draw.</param>
    /// <param name="services">The producer's neutral GPU service bundle (same device).</param>
    /// <param name="vertexBytecode">The fullscreen vertex shader.</param>
    /// <param name="fragmentBytecode">The binding-bar fragment shader.</param>
    /// <param name="width">The render width in pixels.</param>
    /// <param name="height">The render height in pixels.</param>
    /// <param name="layoutOptions">The layout tuning; <see langword="null"/> uses <see cref="BindingBarLayoutOptions.Default"/>.</param>
    /// <param name="regionSource">The normalized frame region the bar is CONFINED to, sampled each frame (the overworld
    /// passes its live room-view rect so the controls hug the room pane, not the console panes). <see langword="null"/>
    /// anchors to the full frame. The cluster scales with the region's height, so it shrinks with its pane through
    /// the staged layout transitions.</param>
    public BindingBarOverlayNode(
        IRenderNode inner,
        IBindingBarSource source,
        SdfProducerServices services,
        ReadOnlyMemory<byte> vertexBytecode,
        ReadOnlyMemory<byte> fragmentBytecode,
        uint width,
        uint height,
        BindingBarLayoutOptions? layoutOptions = null,
        Func<NormalizedRect>? regionSource = null
    ) {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(source);

        m_compositor = new GpuCompositor(commandRecorder: services.CommandRecorder);
        m_createRenderTarget = services.CreateRenderTarget;
        m_descriptor = new NodeDescriptor(Name: "binding-bar-overlay", SurfaceId: SurfaceId.New());
        m_descriptorAllocator = services.DescriptorAllocator;
        m_deviceContext = services.DeviceContext;
        m_fragmentBytecode = fragmentBytecode;
        m_height = height;
        m_inner = inner;
        m_layoutOptions = (layoutOptions ?? BindingBarLayoutOptions.Default);
        m_pipelineFactory = services.PipelineFactory;
        m_queueSubmitter = services.QueueSubmitter;
        m_regionSource = regionSource;
        m_shaderModuleFactory = services.ShaderModuleFactory;
        m_source = source;
        m_storageBufferBinding = services.StorageBufferBinding;
        m_storageBufferFactory = services.StorageBufferFactory;
        m_vertexBufferFactory = services.VertexBufferFactory;
        m_vertexBytecode = vertexBytecode;
        m_width = width;
    }

    /// <inheritdoc/>
    public NodeDescriptor Descriptor => m_descriptor;

    /// <inheritdoc/>
    public Surface ProduceFrame(in FrameContext context) {
        if (m_disposed) {
            return default;
        }

        // The inner producer's same-device output is already transitioned shader-readable for the fragment stage
        // before its submit, so this same-queue pass samples it with no CPU wait.
        var inner = m_inner.ProduceFrame(context: context);

        if (inner.IsEmpty || (0 == inner.ImageViewHandle)) {
            return inner;
        }

        if (!m_source.TrySnapshot(frame: out var frame)) {
            // Nothing published yet (no input frame has run): pass the world through untouched.
            return inner;
        }

        EnsureResources();

        if (inner.ImageViewHandle != m_lastImageViewHandle) {
            m_descriptorAllocator.WriteCombinedImageSampler(
                arrayElement: 0,
                binding: SamplerBinding,
                descriptorSetHandle: m_descriptorSet,
                deviceHandle: m_deviceContext.DeviceHandle,
                imageViewHandle: inner.ImageViewHandle,
                samplerHandle: m_sampler
            );

            m_lastImageViewHandle = inner.ImageViewHandle;
        }

        var slotCount = PackSlots(frame: in frame);

        m_slotBuffer!.Write<float>(data: m_slotScratch);
        // Only the slot count varies per frame; the style knobs were written once in EnsureResources. Overwrite that
        // one float in the reusable buffer the draw command's cached binding already views — Record copies it into the
        // command buffer synchronously (before Submit), so reusing the backing array across frames is safe.
        MemoryMarshal.Cast<byte, float>(span: m_pushConstantData.AsSpan())[0] = slotCount;

        var commandBufferHandle = m_compositor.Record(
            deviceContext: m_deviceContext,
            drawCommands: m_drawCommands!,
            pipelines: m_pipelines!,
            target: m_renderTarget!
        );

        Span<nint> commandBuffers = [commandBufferHandle];

        m_queueSubmitter.Submit(
            commandBufferHandles: commandBuffers,
            deviceContext: m_deviceContext
        );

        return new Surface(
            Format: SurfaceFormat.R8G8B8A8Unorm,
            Height: m_height,
            ImageViewHandle: m_renderTarget!.ImageViewHandle,
            Width: m_width
        );
    }

    // Packs the frame's visible slots (layout buttons, then modifier pips) into the scratch buffer.
    // KEEP IN SYNC with the shader's BindingSlot struct.
    //
    // CONFINEMENT: the layout runs in the CONFINING REGION's local space (its own aspect, its own bottom-center
    // anchor), then every placement maps into the frame's aspect units — position offset by the region origin,
    // every length scaled by the region height. With no region source the region IS the full frame and the
    // transform is the identity, so the bare-room path draws exactly as before.
    private int PackSlots(in BindingBarFrame frame) {
        var frameAspect = ((float)m_width / m_height);
        var region = (m_regionSource?.Invoke() ?? new NormalizedRect(X: 0f, Y: 0f, Width: 1f, Height: 1f));

        // A degenerate region (a pane eased to nothing mid-transition) has nowhere to draw a bar.
        if ((region.Width < 0.05f) || (region.Height < 0.05f)) {
            return 0;
        }

        // The region in frame aspect units: x spans [0, frameAspect], y spans [0, 1].
        var regionOriginX = (region.X * frameAspect);
        var regionOriginY = region.Y;
        var regionHeight = region.Height;
        var regionAspect = ((region.Width * frameAspect) / regionHeight);

        var count = 0;
        var slots = frame.Slots.Span;
        // The frame's actual bar count drives the layout (quadrant placement kicks in for 2+ players); the base options
        // otherwise carry only style tuning.
        var layoutOptions = (m_layoutOptions with { BarCount = Math.Max(1, frame.BarCount) });

        for (var index = 0; ((index < slots.Length) && (count < MaxSlots)); index++) {
            var slot = slots[index];

            if (!slot.Visible) {
                continue;
            }

            var placement = BindingBarLayout.Place(aspect: regionAspect, index: index, options: in layoutOptions);
            var center = new System.Numerics.Vector2(
                x: (regionOriginX + (placement.Center.X * regionHeight)),
                y: (regionOriginY + (placement.Center.Y * regionHeight))
            );

            Pack(
                alpha: slot.Alpha,
                center: center,
                glyph: slot.Glyph,
                glyphCenter: new System.Numerics.Vector2(
                    x: (regionOriginX + (placement.GlyphCenter.X * regionHeight)),
                    y: (regionOriginY + (placement.GlyphCenter.Y * regionHeight))
                ),
                glyphHalf: (placement.GlyphHalfSize * regionHeight),
                icon: slot.Icon,
                plateHalf: (placement.HalfSize * regionHeight),
                pressed: slot.Pressed,
                slot: count++
            );
        }

        // The modifier pips sit between a bar's clusters on its anchor line, lit while held — rendered PER BAR so they
        // ride each player's quadrant cluster in the multiplayer layout instead of floating at frame center. The
        // multiplayer publish carries one 12-slot bar per active player; a single-player publish keeps one bar.
        var modifiers = frame.Modifiers.Span;
        var barCount = Math.Max(1, frame.BarCount);
        var pipScale = BindingBarLayout.BarScale(barCount: barCount);
        var pipHalf = (((m_layoutOptions.ButtonSize * 0.35f) * pipScale) * regionHeight);
        var pipSpacing = (((m_layoutOptions.ButtonSize * 1.1f) * pipScale) * regionHeight);

        for (var bar = 0; ((bar < barCount) && (count < MaxSlots)); bar++) {
            var barAnchor = BindingBarLayout.BarAnchor(bar: bar, barCount: barCount, aspect: regionAspect, anchorOffsetY: m_layoutOptions.AnchorOffsetY);
            var anchorX = (regionOriginX + (barAnchor.X * regionHeight));
            var anchorY = (regionOriginY + (barAnchor.Y * regionHeight));

            for (var index = 0; ((index < modifiers.Length) && (count < MaxSlots)); index++) {
                var modifier = modifiers[index];
                var center = new System.Numerics.Vector2(
                    x: (anchorX + ((index - ((modifiers.Length - 1) * 0.5f)) * pipSpacing)),
                    y: anchorY
                );

                Pack(
                    alpha: (modifier.Held ? 1f : 0.35f),
                    center: center,
                    glyph: modifier.Glyph,
                    glyphCenter: center,
                    glyphHalf: (pipHalf * 0.8f),
                    icon: BindingIconId.None,
                    plateHalf: pipHalf,
                    pressed: modifier.Held,
                    slot: count++
                );
            }
        }

        return count;
    }

    private void Pack(int slot, System.Numerics.Vector2 center, System.Numerics.Vector2 glyphCenter, float plateHalf, float glyphHalf, BindingGlyphId glyph, BindingIconId icon, float alpha, bool pressed) {
        var offset = (slot * SlotFloatCount);
        var packedIds = ((((uint)glyph) << 16) | ((uint)icon));
        var packedState = ((uint)(Math.Clamp(value: alpha, max: 1f, min: 0f) * 255f) | (pressed ? (1u << 8) : 0u));

        m_slotScratch[offset] = center.X;
        m_slotScratch[offset + 1] = center.Y;
        m_slotScratch[offset + 2] = plateHalf;
        m_slotScratch[offset + 3] = glyphHalf;
        m_slotScratch[offset + 4] = BitConverter.UInt32BitsToSingle(value: packedIds);
        m_slotScratch[offset + 5] = (glyphCenter.X - center.X);
        m_slotScratch[offset + 6] = (glyphCenter.Y - center.Y);
        m_slotScratch[offset + 7] = BitConverter.UInt32BitsToSingle(value: packedState);
    }

    // Writes the constant style knobs into the reusable push-constant buffer once. floats[0] (the slot count) is the
    // only value that varies per frame; ProduceFrame overwrites it in place, leaving these untouched.
    private void InitializePushConstants() {
        var floats = MemoryMarshal.Cast<byte, float>(span: m_pushConstantData.AsSpan());

        floats[1] = 0.18f;  // plate corner radius, as a fraction of the plate half-size
        floats[2] = 1f;     // global alpha (a future fade knob)
        floats[3] = 0.35f;  // pressed brightness boost
        floats[4] = 0.62f;  // plate darkness (background dim under a slot)
        floats[5] = 0.08f;  // outline width, as a fraction of the plate half-size
        floats[6] = 0.06f;  // anti-alias ramp, as a fraction of the plate half-size
        floats[7] = 0f;     // reserved
    }

    private void EnsureResources() {
        if (m_resourcesReady) {
            return;
        }

        m_renderTarget = m_createRenderTarget();
        m_pipelineId = AssetContentHash.Compute(content: m_fragmentBytecode.Span);
        m_vertexShader = m_shaderModuleFactory.Create(bytecode: m_vertexBytecode, deviceContext: m_deviceContext, stage: GpuShaderStage.Vertex);
        m_fragmentShader = m_shaderModuleFactory.Create(bytecode: m_fragmentBytecode, deviceContext: m_deviceContext, stage: GpuShaderStage.Fragment);
        m_vertexBuffer = m_vertexBufferFactory.Create(deviceContext: m_deviceContext, strideBytes: VertexStrideBytes, vertexData: FullscreenTriangleVertexData);
        m_slotBuffer = m_storageBufferFactory.Create(deviceContext: m_deviceContext, sizeBytes: (MaxSlots * SlotStrideBytes));
        m_pipeline = m_pipelineFactory.Create(
            deviceContext: m_deviceContext,
            enableStorageBuffer: true,
            fragmentShaderModule: m_fragmentShader,
            height: m_height,
            pushConstantBinding: new GpuPushConstantBinding(data: new byte[PushConstantByteLength], offset: 0, stageFlags: GpuShaderStage.Fragment),
            renderTarget: m_renderTarget,
            textureSamplerCount: 1,
            vertexShaderModule: m_vertexShader,
            width: m_width
        );

        var deviceHandle = m_deviceContext.DeviceHandle;

        m_descriptorPool = m_descriptorAllocator.CreatePool(
            deviceHandle: deviceHandle,
            sizes: new GpuDescriptorPoolSizes(
                MaxSets: 1,
                CombinedImageSamplerCount: 1,
                StorageBufferCount: 1,
                StorageImageCount: 0,
                AccelerationStructureCount: 0
            )
        );
        m_descriptorSet = m_descriptorAllocator.AllocateSet(descriptorSetLayoutHandle: m_pipeline.DescriptorSetLayoutHandle, deviceHandle: deviceHandle, poolHandle: m_descriptorPool);
        m_sampler = m_descriptorAllocator.CreateSampler(deviceHandle: deviceHandle);
        m_descriptorAllocator.WriteStorageBuffer(binding: m_storageBufferBinding, bufferHandle: m_slotBuffer.BufferHandle, bufferSize: (MaxSlots * SlotStrideBytes), descriptorSetHandle: m_descriptorSet, deviceHandle: deviceHandle);
        m_pipelines = new Dictionary<AssetContentHash, IGpuPipeline> {
            [m_pipelineId] = m_pipeline,
        };
        InitializePushConstants();
        m_pushConstants = new GpuPushConstantBinding(data: m_pushConstantData, offset: 0, stageFlags: GpuShaderStage.Fragment);
        m_drawCommands = [
            new GpuDrawCommand(
                DescriptorSetHandle: m_descriptorSet,
                DrawParameters: new GpuDrawParameters(instanceCount: 1, vertexCount: VertexCount),
                PipelineId: m_pipelineId,
                PushConstants: m_pushConstants,
                VertexBufferHandle: m_vertexBuffer.BufferHandle
            ),
        ];
        m_resourcesReady = true;
    }

    private static byte[] CreateFullscreenTriangleVertexData() {
        var vertices = new (float X, float Y)[]
        {
            (-1f, -1f),
            (3f, -1f),
            (-1f, 3f),
        };
        var vertexData = new byte[(int)(VertexStrideBytes * vertices.Length)];

        for (var index = 0; (index < vertices.Length); index++) {
            var offset = (index * (int)VertexStrideBytes);

            _ = BitConverter.TryWriteBytes(destination: vertexData.AsSpan(length: sizeof(float), start: offset), value: vertices[index].X);
            _ = BitConverter.TryWriteBytes(destination: vertexData.AsSpan(length: sizeof(float), start: (offset + sizeof(float))), value: vertices[index].Y);
        }

        return vertexData;
    }

    /// <inheritdoc/>
    public void OnDeviceLost() {
        // Tear down this node's GPU resources so the next frame recreates them on the recovered device, then
        // forward — the inner producer owns its own resources.
        ReleaseGpuResources();
        m_inner.OnDeviceLost();
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        ReleaseGpuResources();
        m_inner.Dispose();
    }

    private void ReleaseGpuResources() {
        var deviceHandle = m_deviceContext.DeviceHandle;

        if (0 != m_sampler) {
            m_descriptorAllocator.DestroySampler(deviceHandle: deviceHandle, samplerHandle: m_sampler);
            m_sampler = 0;
        }

        if (0 != m_descriptorPool) {
            m_descriptorAllocator.DestroyPool(deviceHandle: deviceHandle, poolHandle: m_descriptorPool);
            m_descriptorPool = 0;
            m_descriptorSet = 0;
        }

        m_pipeline?.Dispose();
        m_pipeline = null;
        m_pipelines = null;
        m_drawCommands = null;
        m_slotBuffer?.Dispose();
        m_slotBuffer = null;
        m_vertexBuffer?.Dispose();
        m_vertexBuffer = null;
        m_fragmentShader?.Dispose();
        m_fragmentShader = null;
        m_vertexShader?.Dispose();
        m_vertexShader = null;
        m_renderTarget?.Dispose();
        m_renderTarget = null;
        m_lastImageViewHandle = 0;
        m_resourcesReady = false;
    }
}
