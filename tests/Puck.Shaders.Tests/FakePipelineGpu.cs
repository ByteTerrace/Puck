using Puck.Abstractions.Gpu;

namespace Puck.Shaders.Tests;

/// <summary>
/// A GPU with no device behind it, for driving <see cref="ShaderPipelineRenderNode"/> through its real factory seams
/// (the device context's <see cref="IGpuDeviceContext.Services"/>, every one of them this fake). Every object a factory creates is a
/// <see cref="Created"/> entry with a unique nonzero handle and a disposal count, descriptor pools and samplers included,
/// so ownership is checked by counting. <see cref="FailAtCreation"/> makes the Nth creation throw, which is how a
/// candidate's allocation is made to fail partway. Creations, fence waits, device drains and disposals can be recorded in
/// order in <see cref="Events"/>, and barriers in <see cref="Barriers"/>, when <see cref="Recording"/> is on. Recording commands, submitting and waiting allocate nothing,
/// so a steady-state frame's managed allocation belongs to the node alone. The node builds a candidate's modules and
/// pipelines on a pool thread, so creation is serialized and each object records its creating thread;
/// <see cref="PipelineGate"/> holds that compiler work and <see cref="QueueHeld"/> holds the queue unfinished.
/// </summary>
internal sealed class FakePipelineGpu : IGpuDeviceContext,
    IGpuCommandPoolFactory, IGpuPipelineFactory, IGpuRecorder, IGpuBindings, IGpuQueueSubmitter, IGpuShaderModuleFactory,
    IGpuBufferFactory, IGpuImageFactory, IGpuSurfaceTransferFactory, IGpuRenderPassFactory {
    // Where descriptor set handles start, far above every object handle the fake hands out.
    private const long SetHandleBase = 0x4000_0000;

    private readonly Dictionary<nint, Created> m_byHandle = [];
    private readonly Dictionary<nint, GpuDescriptorAdmission> m_poolRanges = [];
    private readonly Lock m_gate = new();
    private readonly Dictionary<(nint Set, uint Binding), nint> m_constantBuffers = [];
    private readonly Dictionary<nint, FakeHostBuffer> m_hostBuffers = [];

    private long m_sets;
    private int m_gateThread;

    private long m_nextHandle = 0x1000;

    /// <summary>Gets every object created so far, in creation order.</summary>
    public List<Created> CreatedObjects { get; } = [];
    /// <summary>Gets each direct dispatch's group counts, in recording order, while <see cref="Recording"/> is on.</summary>
    public List<(uint X, uint Y, uint Z)> Dispatches { get; } = [];
    /// <summary>Gets every descriptor pool created so far, in creation order, as its creation sized it.</summary>
    public List<GpuDescriptorPoolSizes> DescriptorPools { get; } = [];

    /// <summary>Gets or sets the device descriptor heap every pool is a range of, as on Direct3D 12: a pool is admitted
    /// into it at creation and returns its range when destroyed, and <see cref="IGpuBindings.CanAdmit"/> checks a
    /// candidate against it. <see langword="null"/> admits every pool, as on Vulkan.</summary>
    public GpuDescriptorHeapBudget? DescriptorHeap { get; set; }
    /// <summary>Gets the number of creations so far.</summary>
    public int CreationCount => CreatedObjects.Count;
    /// <summary>Gets the bytes of every image and buffer created and not yet disposed: an image is its width times its
    /// height times its format's texel size, a buffer its size.</summary>
    public ulong LiveBytes { get; private set; }
    /// <summary>Gets the most <see cref="LiveBytes"/> has been since the fake was created or
    /// <see cref="ResetPeakBytes"/> last ran.</summary>
    public ulong PeakLiveBytes { get; private set; }

    /// <summary>Gets the handles of the images cleared to zero, in recording order.</summary>
    public List<nint> ClearedImages { get; } = [];
    /// <summary>Gets every barrier recorded while <see cref="Recording"/> is on, with the image or buffer handle it names
    /// (zero for a memory barrier), in recording order.</summary>
    public List<(ShaderPipelineBarrier Barrier, nint Handle)> Barriers { get; } = [];
    /// <summary>Gets every render pass begun while <see cref="Recording"/> is on: its description, and the handles of the
    /// color images and depth image (zero for none) its framebuffer binds, in recording order.</summary>
    public List<(GpuRenderPassDescription Pass, nint[] Colors, nint Depth)> RenderPasses { get; } = [];
    /// <summary>Gets every geometry bind and draw recorded while <see cref="Recording"/> is on, in recording order:
    /// <c>vertices</c> (buffer, size, stride as the count), <c>indices16</c> or <c>indices32</c> (buffer, offset, size),
    /// <c>draw</c> (vertex count) and <c>draw-indexed</c> (index count).</summary>
    public List<(string Command, nint Buffer, ulong OffsetBytes, ulong SizeBytes, uint Count)> GraphicsCommands { get; } = [];
    /// <summary>Gets every geometry buffer created, with its usages and the bytes it was filled with.</summary>
    public List<(nint Handle, GpuBufferUsage Usage, byte[] Data)> GeometryBuffers { get; } = [];
    /// <summary>Gets every graphics pipeline created, with the render pass and description it was created for.</summary>
    public List<(GpuRenderPassDescription Pass, GpuGraphicsPipelineDescription Description)> GraphicsPipelines { get; } = [];
    /// <summary>Gets the ordered creations, fence waits, device drains and disposals, while <see cref="Recording"/> is on.</summary>
    public List<string> Events { get; } = [];
    /// <summary>Gets every descriptor write while <see cref="Recording"/> is on, in writing order: the set, the binding,
    /// and the image view or buffer handle written.</summary>
    public List<(nint Set, uint Binding, nint Handle)> DescriptorWrites { get; } = [];
    /// <summary>Gets every readback, in reading order: the image read and the layout it was read in.</summary>
    public List<(nint Image, GpuImageLayout Layout)> Readbacks { get; } = [];
    /// <summary>Gets every push-constant write recorded while <see cref="Recording"/>: its bind point, stages and bytes.</summary>
    public List<(GpuBindPoint BindPoint, GpuShaderStage Stages, byte[] Data)> PushedConstants { get; } = [];
    /// <summary>Gets every descriptor set bound while <see cref="Recording"/> is on, in binding order: the group and the
    /// set.</summary>
    public List<(uint Group, nint Set)> BoundSets { get; } = [];

    /// <summary>Gets or sets the one-based creation number that throws instead of creating; 0 never throws.</summary>
    public int FailAtCreation { get; set; }
    /// <summary>Gets or sets whether <see cref="Events"/> and <see cref="Barriers"/> record.</summary>
    public bool Recording { get; set; }
    /// <summary>Gets or sets whether the queue holds every submission unfinished: while set, no fence reads as
    /// signaled, though a wait still returns at once.</summary>
    public bool QueueHeld { get; set; }
    /// <summary>Gets or sets whether <see cref="CreateReadback"/> creates a readback. Unset, it throws, so a capture
    /// that reaches the published image fails there. Set, each read creates a staging buffer of the read's width times
    /// its height times its texel size, counted in <see cref="LiveBytes"/>, and replaces it when a read's size differs.</summary>
    public bool ReadbackSupported { get; set; }
    /// <summary>Gets or sets a gate every shader module and pipeline creation waits on before it creates, or
    /// <see langword="null"/> for none: a held gate is a driver compiling on a cold cache. The thread that sets it is the
    /// one producing frames; a creation on that thread while the gate is held would wait forever, so it throws instead.</summary>
    public ManualResetEventSlim? PipelineGate {
        get;
        set {
            field = value;
            m_gateThread = Environment.CurrentManagedThreadId;
        }
    }

    /// <summary>Gets the event set when a creation first reaches a held <see cref="PipelineGate"/>.</summary>
    public ManualResetEventSlim PipelineGateEntered { get; } = new(initialState: false);

    /// <summary>Gets the number of raw (byte-address) buffer descriptor writes.</summary>
    public int RawBufferWrites { get; private set; }
    /// <summary>Gets the number of structured storage-buffer descriptor writes.</summary>
    public int StructuredBufferWrites { get; private set; }
    /// <summary>Gets the number of queue submissions.</summary>
    public int Submissions { get; private set; }
    /// <summary>Gets the number of whole-device drains.</summary>
    public int WaitIdleCount { get; private set; }
    public long AdapterLuid => 1L;
    public GpuDeviceCapabilities? Capabilities => null;
    public GpuDeviceIdentity? Identity => null;
    /// <summary>Gets or sets the memory profile the device reports; the default reports nothing.</summary>
    public GpuMemoryProfile MemoryProfile { get; set; }
    /// <summary>Gets this fake as every one of its own services.</summary>
    public GpuDeviceServices Services => field ??= new GpuDeviceServices {
        Bindings = this,
        BufferFactory = this,
        CommandPoolFactory = this,
        ImageFactory = this,
        PipelineFactory = this,
        QueueSubmitter = this,
        Recorder = this,
        RenderPassFactory = this,
        ShaderModuleFactory = this,
        SurfaceTransferFactory = this,
    };

    // The texel size of a storage image format, stated here independently of the node's own table.
    private static ulong TexelBytes(GpuPixelFormat format) => format switch {
        GpuPixelFormat.R8G8B8A8Unorm or GpuPixelFormat.B8G8R8A8Unorm or GpuPixelFormat.D32Float => 4UL,
        GpuPixelFormat.R16G16B16A16Float => 8UL,
        GpuPixelFormat.R32G32B32A32Float => 16UL,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "The fake has no texel size for this format."),
    };
    // A node creates its pipeline and module set on a pool thread, so creation is serialized here; each object records
    // the thread that created it and the bytes it occupies.
    private Created Create(string kind, ulong bytes = 0UL) {
        lock (m_gate) {
            if (
                (FailAtCreation != 0) &&
                ((CreationCount + 1) == FailAtCreation)
            ) {
                FailAtCreation = 0;

                throw new InvalidOperationException(message: $"Injected allocation failure at creation {(CreationCount + 1)} ({kind}).");
            }

            var created = new Created(
                bytes: bytes,
                gpu: this,
                handle: ((nint)(m_nextHandle += 0x10)),
                kind: kind,
                number: (CreationCount + 1)
            );

            LiveBytes += bytes;
            PeakLiveBytes = Math.Max(
                val1: PeakLiveBytes,
                val2: LiveBytes
            );
            CreatedObjects.Add(item: created);
            m_byHandle.Add(
                key: created.Handle,
                value: created
            );
            Record(text: $"create {kind} #{created.Number}");

            return created;
        }
    }
    // A shader module or pipeline: the driver's compiler work, which waits on the pipeline gate when one is held.
    private Created CreateCompiled(string kind) {
        if (PipelineGate is { IsSet: false } gate) {
            if (Environment.CurrentManagedThreadId == m_gateThread) {
                throw new InvalidOperationException(message: $"A {kind} was created on the thread producing frames while the driver was held.");
            }

            PipelineGateEntered.Set();
            gate.Wait();
        }

        return Create(kind: kind);
    }
    private void Destroy(nint handle) {
        Created created;

        lock (m_gate) {
            created = m_byHandle[handle];
        }

        created.Dispose();
    }
    private void RecordGraphics(string command, nint buffer, ulong offsetBytes, ulong sizeBytes, uint count) {
        if (Recording) {
            GraphicsCommands.Add(item: (command, buffer, offsetBytes, sizeBytes, count));
        }
    }
    private void RecordBarrier(ShaderPipelineBarrier barrier, nint handle) {
        if (Recording) {
            Barriers.Add(item: (barrier, handle));
        }
    }
    private void Record(string text) {
        if (Recording) {
            Events.Add(item: text);
        }
    }

    // Each set is its own handle, above every object handle, so a law can tell apart the sets it binds.
    public nint AllocateSet(nint poolHandle, nint descriptorSetLayoutHandle, in GpuObjectName name) => ((nint)(SetHandleBase + Interlocked.Increment(location: ref m_sets)));
    public void BeginCommandBuffer(nint commandBufferHandle) {
        lock (m_gate) {
            if (m_byHandle.TryGetValue(
                key: commandBufferHandle,
                value: out var pool
            )) {
                pool.Use();
            }
        }
    }
    public void BeginDebugGroup(nint commandBufferHandle, string label) { }
    public void BeginRenderPass(nint commandBufferHandle, IGpuFramebuffer framebuffer, GpuPixelRect? area = null) {
        if (Recording) {
            var fake = ((FakeFramebuffer)framebuffer);

            RenderPasses.Add(item: (fake.RenderPass.Description, fake.Colors, fake.Depth));
        }
    }
    public void BindDescriptorSet(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineLayoutHandle, uint group, nint descriptorSetHandle) {
        if (Recording) {
            BoundSets.Add(item: (group, descriptorSetHandle));
        }
    }
    public void BindIndexBuffer(nint commandBufferHandle, nint bufferHandle, ulong offsetBytes, ulong sizeBytes, GpuIndexFormat format) => RecordGraphics(buffer: bufferHandle, command: ((format == GpuIndexFormat.UInt16) ? "indices16" : "indices32"), count: 0, offsetBytes: offsetBytes, sizeBytes: sizeBytes);
    public void BindPipeline(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineHandle) { }
    public void BindVertexBuffer(nint commandBufferHandle, nint bufferHandle, ulong sizeBytes, uint strideBytes) => RecordGraphics(buffer: bufferHandle, command: "vertices", count: strideBytes, offsetBytes: 0, sizeBytes: sizeBytes);
    public void ClearStorageBuffer(nint commandBufferHandle, nint bufferHandle, ulong sizeBytes) { }
    public void ClearStorageImage(nint commandBufferHandle, nint imageHandle, GpuPixelFormat format) => ClearedImages.Add(item: imageHandle);
    public IGpuCommandPool Create(in GpuObjectName name) => new FakeCommandPool(created: Create(kind: "command pool"));
    public IGpuComputePipeline Create(IGpuShaderModule computeShaderModule, GpuComputePipelineDescription description, in GpuObjectName name) => new FakePipeline(
        created: CreateCompiled(kind: "compute pipeline"),
        layout: description.Layout
    );
    public IGpuShaderModule Create(GpuShaderStage stage, ReadOnlyMemory<byte> bytecode) => new FakeModule(created: CreateCompiled(kind: $"{stage} module"));
    public IGpuStorageBuffer CreateHostVisible(ulong sizeBytes, GpuBufferUsage usage, in GpuObjectName name) {
        GpuBufferUsages.Validate(
            sizeBytes: sizeBytes,
            usage: usage
        );

        var buffer = new FakeHostBuffer(
            created: Create(
                bytes: sizeBytes,
                kind: $"host {usage} buffer"
            ),
            sizeBytes: sizeBytes
        );

        lock (m_gate) {
            m_hostBuffers[buffer.BufferHandle] = buffer;
        }

        return buffer;
    }
    // The fake has one memory, so a host-visible device-local buffer is a host-visible one.
    public IGpuStorageBuffer CreateHostVisibleDeviceLocal(ulong sizeBytes, GpuBufferUsage usage, in GpuObjectName name) => CreateHostVisible(
        name: in name,
        sizeBytes: sizeBytes,
        usage: usage
    );
    public IGpuImage Create(GpuPixelFormat format, uint width, uint height, GpuImageUsage usage, in GpuObjectName name) => new FakeImage(
        created: Create(
            bytes: ((((ulong)width) * height) * TexelBytes(format: format)),
            kind: $"{format} image"
        ),
        format: format,
        height: height,
        usage: usage,
        width: width
    );
    public IGpuPipeline Create(IGpuRenderPass renderPass, IGpuShaderModule vertexShaderModule, IGpuShaderModule fragmentShaderModule, GpuGraphicsPipelineDescription description, in GpuObjectName name) {
        description.ValidateAgainst(renderPass: renderPass);

        var created = CreateCompiled(kind: "graphics pipeline");

        lock (m_gate) {
            GraphicsPipelines.Add(item: (renderPass.Description, description));
        }

        return new FakePipeline(
            created: created,
            layout: description.Layout
        );
    }
    public IGpuStorageBuffer CreateHostVisible(ReadOnlySpan<byte> data, GpuBufferUsage usage, in GpuObjectName name) {
        GpuBufferUsages.Validate(
            sizeBytes: ((ulong)data.Length),
            usage: usage
        );

        var created = Create(
            bytes: ((ulong)data.Length),
            kind: $"{usage} buffer"
        );

        GeometryBuffers.Add(item: (created.Handle, usage, data.ToArray()));

        return new FakeBuffer(
            created: created,
            sizeBytes: ((ulong)data.Length)
        );
    }
    public IGpuRenderPass Create(GpuRenderPassDescription description, in GpuObjectName name) => new FakeRenderPass(
        created: Create(kind: "render pass"),
        description: description
    );
    public IGpuFramebuffer CreateFramebuffer(IGpuRenderPass renderPass, IReadOnlyList<IGpuImage> colors, IGpuImage? depth) {
        var (width, height) = GpuFramebuffers.Validate(
            colors: colors,
            depth: depth,
            description: renderPass.Description
        );

        return new FakeFramebuffer(
            colors: [.. colors.Select(selector: static image => image.ImageHandle)],
            created: Create(kind: "framebuffer"),
            depth: (depth?.ImageHandle ?? 0),
            height: height,
            renderPass: renderPass,
            width: width
        );
    }
    public IGpuBuffer CreateDeviceLocal(ulong sizeBytes, GpuBufferUsage usage, in GpuObjectName name) => new FakeBuffer(
        created: Create(
            bytes: sizeBytes,
            kind: "buffer"
        ),
        sizeBytes: sizeBytes
    );
    public IGpuSurfaceImport CreateImport() => throw new NotSupportedException();
    public bool CanAdmit(string owner, IReadOnlyList<GpuDescriptorPoolSizes> pools, out string refusal) {
        lock (m_gate) {
            if (DescriptorHeap is { } heap) {
                return heap.CanAdmit(
                    owner: owner,
                    pools: pools,
                    refusal: out refusal
                );
            }
        }

        refusal = string.Empty;

        return true;
    }

    public long HeapReleaseRevision => (DescriptorHeap?.ReleaseRevision ?? 0L);

    public nint CreatePool(in GpuDescriptorPoolSizes sizes, in GpuObjectName name) {
        GpuDescriptorAdmission? admission = null;

        lock (m_gate) {
            if (
                (DescriptorHeap is { } heap) &&
                !heap.TryAdmit(
                    admission: out admission,
                    owner: "descriptor pool",
                    pools: [sizes],
                    refusal: out var refusal
                )
            ) {
                throw new GpuDescriptorHeapRefusalException(message: refusal);
            }
        }

        nint handle;

        try {
            handle = Create(kind: "descriptor pool").Handle;
        } catch {
            if (admission is not null) {
                lock (m_gate) {
                    DescriptorHeap!.Release(admission: admission);
                }
            }

            throw;
        }

        DescriptorPools.Add(item: sizes);

        if (admission is not null) {
            lock (m_gate) {
                m_poolRanges.Add(
                    key: handle,
                    value: admission
                );
            }
        }

        return handle;
    }
    public IGpuSurfaceReadback CreateReadback() => (ReadbackSupported
        ? new FakeReadback(gpu: this)
        : throw new NotSupportedException());
    public nint CreateSampler(GpuSamplerFilter filter = GpuSamplerFilter.Linear) => Create(kind: "sampler").Handle;
    public IGpuSubmissionFence CreateSubmissionFence() => new FakeFence(
        created: Create(kind: "fence"),
        gpu: this
    );
    public IGpuSurfaceUpload CreateUpload() => throw new NotSupportedException();
    public bool TryImportFence(nint sharedHandle, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IGpuSharedFence? fence, out string refusal) {
        fence = null;
        refusal = "the fake device imports no fence";

        return false;
    }
    public void AddExternalWait(GpuExternalWait wait) => throw new NotSupportedException();
    public void DestroyPool(nint poolHandle) {
        if (0 == poolHandle) {
            return;
        }

        lock (m_gate) {
            if (m_poolRanges.Remove(
                key: poolHandle,
                value: out var admission
            )) {
                DescriptorHeap!.Release(admission: admission);
            }
        }

        Destroy(handle: poolHandle);
    }
    public void DestroySampler(nint samplerHandle) => Destroy(handle: samplerHandle);
    public void Dispatch(nint commandBufferHandle, uint groupCountX, uint groupCountY, uint groupCountZ) {
        if (Recording) {
            Dispatches.Add(item: (groupCountX, groupCountY, groupCountZ));
        }
    }
    public void DispatchIndirect(nint commandBufferHandle, nint argumentBufferHandle, ulong argumentBufferOffset) { }
    public void Draw(nint commandBufferHandle, in GpuDrawParameters parameters) => RecordGraphics(command: "draw", buffer: 0, offsetBytes: 0, sizeBytes: 0, count: parameters.VertexCount);
    public void DrawIndexed(nint commandBufferHandle, uint indexCount) => RecordGraphics(buffer: 0, command: "draw-indexed", count: indexCount, offsetBytes: 0, sizeBytes: 0);
    public void EndCommandBuffer(nint commandBufferHandle) { }
    public void EndDebugGroup(nint commandBufferHandle) { }
    public void EndRenderPass(nint commandBufferHandle) { }
    public void MemoryBarrier(nint commandBufferHandle, GpuAccess sourceAccessMask, GpuAccess destinationAccessMask, GpuStage sourceStageMask, GpuStage destinationStageMask) => RecordBarrier(barrier: new ShaderPipelineBarrier(DestinationAccess: destinationAccessMask, DestinationStage: destinationStageMask, Kind: ShaderPipelineBarrierKind.Memory, NewLayout: GpuImageLayout.Undefined, OldLayout: GpuImageLayout.Undefined, SourceAccess: sourceAccessMask, SourceStage: sourceStageMask), handle: 0);
    /// <summary>Starts a new <see cref="PeakLiveBytes"/> window at the current <see cref="LiveBytes"/>.</summary>
    public void ResetPeakBytes() {
        lock (m_gate) {
            PeakLiveBytes = LiveBytes;
        }
    }
    public void PushConstants(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineLayoutHandle, GpuShaderStage stageFlags, uint offset, ReadOnlySpan<byte> data) {
        if (Recording) {
            PushedConstants.Add(item: (bindPoint, stageFlags, data.ToArray()));
        }
    }
    public void SetScissor(nint commandBufferHandle, GpuPixelRect rect) { }
    public void Submit(ReadOnlySpan<nint> commandBufferHandles) => Submissions++;
    public void Submit(ReadOnlySpan<nint> commandBufferHandles, IGpuSubmissionFence fence) => Submissions++;
    public void SubmitAndWait(ReadOnlySpan<nint> commandBufferHandles) => Submissions++;
    public void TransitionBuffer(nint commandBufferHandle, nint bufferHandle, GpuAccess sourceAccessMask, GpuAccess destinationAccessMask, GpuStage sourceStageMask, GpuStage destinationStageMask) => RecordBarrier(barrier: new ShaderPipelineBarrier(DestinationAccess: destinationAccessMask, DestinationStage: destinationStageMask, Kind: ShaderPipelineBarrierKind.Buffer, NewLayout: GpuImageLayout.Undefined, OldLayout: GpuImageLayout.Undefined, SourceAccess: sourceAccessMask, SourceStage: sourceStageMask), handle: bufferHandle);
    public void TransitionImageLayout(nint commandBufferHandle, nint imageHandle, GpuImageLayout oldLayout, GpuImageLayout newLayout, GpuAccess sourceAccessMask, GpuAccess destinationAccessMask, GpuStage sourceStageMask, GpuStage destinationStageMask) => RecordBarrier(barrier: new ShaderPipelineBarrier(DestinationAccess: destinationAccessMask, DestinationStage: destinationStageMask, Kind: ShaderPipelineBarrierKind.Image, NewLayout: newLayout, OldLayout: oldLayout, SourceAccess: sourceAccessMask, SourceStage: sourceStageMask), handle: imageHandle);
    public void WaitIdle() {
        WaitIdleCount++;
        Record(text: "device drain");
    }
    public void WriteCombinedImageSampler(nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle, nint samplerHandle) {
        if (Recording) {
            DescriptorWrites.Add(item: (descriptorSetHandle, binding, imageViewHandle));
        }
    }
    public void WriteBuffer(nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize, GpuBindingKind kind, uint elementStride) {
        if (Recording) {
            DescriptorWrites.Add(item: (descriptorSetHandle, binding, bufferHandle));
        }
        if (0 == elementStride) {
            RawBufferWrites++;
        } else {
            StructuredBufferWrites++;
        }
    }
    /// <summary>Returns the first <paramref name="sizeBytes"/> bytes of the host-visible buffer last written as the constant
    /// buffer at binding 0 of <paramref name="set"/>: the block a draw or dispatch binding that set reads.</summary>
    /// <param name="set">The descriptor set.</param>
    /// <param name="sizeBytes">The block's size.</param>
    /// <returns>The block's bytes.</returns>
    public byte[] ConstantBlock(nint set, int sizeBytes) {
        lock (m_gate) {
            return m_hostBuffers[m_constantBuffers[(set, 0U)]].Contents[..sizeBytes];
        }
    }
    public void WriteConstantBuffer(nint descriptorSetHandle, uint binding, uint arrayElement, nint bufferHandle, ulong bufferSize) {
        lock (m_gate) {
            m_constantBuffers[(descriptorSetHandle, binding)] = bufferHandle;
        }
    }
    public void WriteSampledImage(nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle) {
        if (Recording) {
            DescriptorWrites.Add(item: (descriptorSetHandle, binding, imageViewHandle));
        }
    }
    public void WriteSampler(nint descriptorSetHandle, uint binding, uint arrayElement, nint samplerHandle) { }
    public void WriteStorageImage(nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle) {
        if (Recording) {
            DescriptorWrites.Add(item: (descriptorSetHandle, binding, imageViewHandle));
        }
    }

    /// <summary>One created object: its creation number, kind, handle, the bytes it occupies, and how often it was
    /// disposed.</summary>
    internal sealed class Created(FakePipelineGpu gpu, nint handle, string kind, int number, ulong bytes) {
        public ulong Bytes { get; } = bytes;

        public int DisposeCount { get; private set; }

        public nint Handle { get; } = handle;
        public string Kind { get; } = kind;
        public int Number { get; } = number;
        /// <summary>Gets the managed thread that created the object.</summary>
        public int ThreadId { get; } = Environment.CurrentManagedThreadId;

        /// <summary>Gets how often the object was used: a fence waited on, or a command pool's buffer begun.</summary>
        public int UseCount { get; private set; }

        public void Dispose() {
            lock (gpu.m_gate) {
                DisposeCount++;

                if (DisposeCount == 1) {
                    gpu.LiveBytes -= Bytes;
                }

                if (gpu.Recording) {
                    gpu.Record(text: $"dispose {Kind} #{Number}");
                }
            }
        }
        public void Use() => UseCount++;
        public void Wait() {
            UseCount++;

            if (gpu.Recording) {
                gpu.Record(text: $"wait {Kind} #{Number}");
            }
        }
        public override string ToString() => $"#{Number} {Kind} (disposed {DisposeCount}x)";
    }

    private sealed class FakeBuffer(Created created, ulong sizeBytes) : IGpuStorageBuffer {
        public nint BufferHandle => created.Handle;
        public ulong SizeBytes => sizeBytes;

        public void Dispose() => created.Dispose();
        public void Write<T>(ReadOnlySpan<T> data) where T : unmanaged => throw new NotSupportedException();
        public void Write<T>(ReadOnlySpan<T> data, ulong destinationOffsetBytes) where T : unmanaged => throw new NotSupportedException();
    }
    // A host-visible buffer: the bytes a host write puts in it, which a law reads back as the GPU would.
    private sealed class FakeHostBuffer(Created created, ulong sizeBytes) : IGpuStorageBuffer {
        public nint BufferHandle => created.Handle;
        public byte[] Contents { get; } = new byte[sizeBytes];
        public ulong SizeBytes => sizeBytes;

        public void Dispose() => created.Dispose();
        public void Write<T>(ReadOnlySpan<T> data) where T : unmanaged => Write(
            data: data,
            destinationOffsetBytes: 0
        );
        public void Write<T>(ReadOnlySpan<T> data, ulong destinationOffsetBytes) where T : unmanaged =>
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(span: data).CopyTo(destination: Contents.AsSpan(start: checked((int)destinationOffsetBytes)));
    }
    private sealed class FakeCommandPool(Created created) : IGpuCommandPool {
        public nint CommandBufferHandle => created.Handle;

        public void Dispose() => created.Dispose();
    }
    private sealed class FakeFence(FakePipelineGpu gpu, Created created) : IGpuSubmissionFence {
        // The fake queue finishes every submission as it is made, unless the law holds it.
        public bool IsSignaled => !gpu.QueueHeld;

        public void Dispose() => created.Dispose();
        public void Wait() => created.Wait();
    }
    private sealed class FakeImage(Created created, GpuPixelFormat format, uint width, uint height, GpuImageUsage usage) : IGpuImage {
        public GpuPixelFormat Format => format;
        public uint Height => height;
        public nint ImageHandle => created.Handle;
        public nint ImageViewHandle => (created.Handle + 1);
        public GpuImageUsage Usage => usage;
        public uint Width => width;

        public void Dispose() => created.Dispose();
    }
    private sealed class FakeModule(Created created) : IGpuShaderModule {
        public nint Handle => created.Handle;

        public void Dispose() => created.Dispose();
    }
    // A pipeline created from a layout has a set layout per group it binds, at its handle plus four plus the group's
    // ordinal, and zero where it binds none.
    private sealed class FakePipeline(Created created, GpuPipelineLayoutDescription? layout) : IGpuComputePipeline, IGpuPipeline {
        public nint DescriptorSetLayoutHandle => (created.Handle + 1);
        public IReadOnlyList<nint> GroupLayoutHandles { get; } = ((layout is null)
            ? []
            : [.. Enumerable.Range(
                count: ((int)(layout.Groups.Max(selector: static group => group.Ordinal) + 1)),
                start: 0
            ).Select(selector: ordinal => (layout.Groups.Any(predicate: group => (group.Ordinal == ordinal))
                ? ((created.Handle + 4) + ordinal)
                : 0))]);
        public nint Handle => created.Handle;
        public nint LayoutHandle => (created.Handle + 2);

        public void Dispose() => created.Dispose();
    }
    private sealed class FakeReadback(FakePipelineGpu gpu) : IGpuSurfaceReadback {
        private byte[] m_pixels = [];
        private Created? m_staging;

        public ulong StagingBytes => ((m_staging is { DisposeCount: 0 } staging)
            ? staging.Bytes
            : 0UL);

        public void Dispose() => m_staging?.Dispose();
        public ReadOnlyMemory<byte> Read(nint sourceImageHandle, GpuPixelFormat format, uint width, uint height, uint bytesPerPixel, GpuImageLayout sourceLayout) {
            var bytes = ((((ulong)width) * height) * bytesPerPixel);

            gpu.Readbacks.Add(item: (sourceImageHandle, sourceLayout));

            if (StagingBytes != bytes) {
                m_staging?.Dispose();
                m_staging = gpu.Create(
                    bytes: bytes,
                    kind: "readback staging"
                );
                m_pixels = new byte[bytes];
            }

            return m_pixels;
        }
    }
    private sealed class FakeFramebuffer(Created created, IGpuRenderPass renderPass, uint width, uint height, nint[] colors, nint depth) : IGpuFramebuffer {
        public nint[] Colors => colors;
        public nint Depth => depth;
        public uint Height => height;
        public IGpuRenderPass RenderPass => renderPass;
        public uint Width => width;

        public void Dispose() => created.Dispose();
    }
    private sealed class FakeRenderPass(Created created, GpuRenderPassDescription description) : IGpuRenderPass {
        public GpuRenderPassDescription Description => description;

        public void Dispose() => created.Dispose();
    }
}
