using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;

namespace Puck.Testing;

/// <summary>
/// A device-free GPU that models memory for the one kernel whose effect a host law can predict: the table uploader,
/// <c>sdf-frame-upload.comp</c>, whose ABI <see cref="GpuRegion"/>'s staged copy speaks. Every buffer is backed by bytes
/// and carries its own handle; host writes land in those bytes and are tallied per buffer; descriptor sets remember
/// which buffer each binding names; and a dispatch recorded while the pipeline built from
/// <see cref="FrameUploadBytecode"/> is bound runs that kernel's copy at record time, from the set's binding 0 into its
/// binding 1: the push carries <c>(count, runCount, offset, tableBase)</c> in uints, and thread <c>i</c> below the
/// count copies <c>destination[word] = source[tableBase + word]</c>, where <c>word</c> is <c>offset + i</c> for one run
/// or, for two or more, found in the source's leading <c>(table offset, prefix)</c> run table (a linear search here; the
/// kernel's binary search finds the same run). Recording order is execution order here, as it is on one queue behind
/// the barriers the caller records. A disposed buffer is forgotten. Everything else is <see cref="FakeGpuDevice"/>.
/// <para>Shader modules and pipelines are created on the thread pool, several at once
/// (<c>SdfWorldPipelines.BuildConcurrency</c>), so handles come from an interlocked counter and the upload kernel is
/// identified by the handles of the modules built from its bytecode and the pipelines built from those modules, each a
/// concurrent set, never by one shared field a concurrent build could overwrite.</para>
/// </summary>
internal sealed class UploadModelGpu :
    IGpuDeviceContext,
    IGpuPipelineFactory,
    IGpuRecorder,
    IGpuBindings,
    IGpuShaderModuleFactory,
    IGpuBufferFactory {
    /// <summary>The first bytecode byte that marks the frame-upload kernel.</summary>
    public const byte FrameUploadBytecode = 0xF7;

    private readonly Dictionary<(nint Set, uint Binding), nint> m_bindings = [];
    private readonly Dictionary<nint, MemoryBuffer> m_buffers = [];

    private readonly FakeGpuDevice m_inner;

    private readonly byte[] m_push = new byte[16];
    private readonly ConcurrentDictionary<nint, byte> m_uploadModules = new();
    private readonly ConcurrentDictionary<nint, byte> m_uploadPipelines = new();

    private nint m_boundPipeline;
    private nint m_boundSet;

    private long m_nextHandle = 0x1000;

    /// <summary>Initializes a new instance of the <see cref="UploadModelGpu"/> class.</summary>
    /// <param name="reportVersion">The ISA version a 1×1 readback reports.</param>
    public UploadModelGpu(byte reportVersion) {
        m_inner = new FakeGpuDevice(reportVersion: reportVersion);
        Services = new GpuDeviceServices {
            Bindings = this,
            BufferFactory = this,
            CommandPoolFactory = m_inner.Services.CommandPoolFactory,
            ImageFactory = m_inner.Services.ImageFactory,
            PipelineFactory = this,
            QueueSubmitter = m_inner.Services.QueueSubmitter,
            Recorder = this,
            RenderPassFactory = m_inner.Services.RenderPassFactory,
            ShaderModuleFactory = this,
            SurfaceTransferFactory = m_inner.Services.SurfaceTransferFactory,
        };
    }

    /// <summary>Gets every descriptor pool created, in creation order, as its creation sized it.</summary>
    public IReadOnlyList<GpuDescriptorPoolSizes> PoolsCreated => m_inner.PoolsCreated;
    public long AdapterLuid => 0L;
    public GpuDeviceCapabilities? Capabilities => null;
    public GpuDeviceIdentity? Identity => null;
    public GpuMemoryProfile MemoryProfile => default;
    /// <summary>Gets the model's buffers, bindings, pipelines, shader modules and recorder, and the
    /// <see cref="FakeGpuDevice"/>'s other services.</summary>
    public GpuDeviceServices Services { get; }
    /// <summary>Gets the table-upload copies recorded since the last <see cref="ResetTallies"/>.</summary>
    public int UploadCopies { get; private set; }

    /// <summary>Gets the bytes of the one device-local buffer of <paramref name="sizeBytes"/>.</summary>
    /// <param name="sizeBytes">The buffer's size; exactly one device-local buffer must have it.</param>
    /// <returns>The buffer's current contents.</returns>
    public byte[] DeviceLocal(ulong sizeBytes) => Single(
        hostVisible: false,
        sizeBytes: sizeBytes
    ).Memory;
    /// <summary>Gets the bytes of the one host-visible buffer of <paramref name="sizeBytes"/>.</summary>
    /// <param name="sizeBytes">The buffer's size; exactly one host-visible buffer must have it.</param>
    /// <returns>The buffer's current contents.</returns>
    public byte[] HostVisible(ulong sizeBytes) => Single(
        hostVisible: true,
        sizeBytes: sizeBytes
    ).Memory;
    /// <summary>Gets the bytes of the live buffer whose handle is <paramref name="bufferHandle"/>.</summary>
    /// <param name="bufferHandle">The buffer's handle.</param>
    /// <returns>The buffer's current contents.</returns>
    public byte[] Memory(nint bufferHandle) => m_buffers[bufferHandle].Memory;
    /// <summary>Gets the host-visible bytes written since the last <see cref="ResetTallies"/>, per buffer size, for
    /// every buffer that received any.</summary>
    /// <returns>Each written buffer's size and the bytes written to it.</returns>
    public (ulong SizeBytes, long Written)[] HostWrites() => [.. m_buffers.Values
        .Where(predicate: buffer => (buffer.Written > 0L))
        .Select(selector: buffer => (buffer.SizeBytes, buffer.Written))];
    /// <summary>Gets the host-visible bytes written since the last <see cref="ResetTallies"/>.</summary>
    /// <returns>The byte count.</returns>
    public long HostBytes() => m_buffers.Values.Sum(selector: buffer => buffer.Written);
    /// <summary>Zeroes the host-write and upload-copy tallies.</summary>
    public void ResetTallies() {
        foreach (var buffer in m_buffers.Values) {
            buffer.Written = 0L;
        }

        UploadCopies = 0;
    }
    public void WaitIdle() { }

    IGpuComputePipeline IGpuPipelineFactory.Create(IGpuShaderModule computeShaderModule, GpuComputePipelineDescription description) {
        var pipeline = new Handles(handle: NextHandle(), layout: NextHandle(), setLayout: NextHandle());

        if (m_uploadModules.ContainsKey(key: computeShaderModule.Handle)) {
            _ = m_uploadPipelines.TryAdd(
                key: pipeline.Handle,
                value: 0
            );
        }

        return pipeline;
    }
    IGpuShaderModule IGpuShaderModuleFactory.Create(GpuShaderStage stage, ReadOnlyMemory<byte> bytecode) {
        var module = new Handles(handle: NextHandle(), layout: 0, setLayout: 0);

        if (
            !bytecode.IsEmpty &&
            (bytecode.Span[0] == FrameUploadBytecode)
        ) {
            _ = m_uploadModules.TryAdd(
                key: module.Handle,
                value: 0
            );
        }

        return module;
    }
    IGpuStorageBuffer IGpuBufferFactory.CreateHostVisible(ulong sizeBytes, GpuBufferUsage usage) => Buffer(hostVisible: true, sizeBytes: sizeBytes);
    IGpuBuffer IGpuBufferFactory.CreateDeviceLocal(ulong sizeBytes, GpuBufferUsage usage) => Buffer(hostVisible: false, sizeBytes: sizeBytes);
    IGpuStorageBuffer IGpuBufferFactory.CreateHostVisible(ReadOnlySpan<byte> data, GpuBufferUsage usage) => throw new NotSupportedException();
    IGpuPipeline IGpuPipelineFactory.Create(IGpuRenderPass renderPass, IGpuShaderModule vertexShaderModule, IGpuShaderModule fragmentShaderModule, GpuGraphicsPipelineDescription description) => throw new NotSupportedException();

    long IGpuBindings.HeapReleaseRevision => m_inner.Services.Bindings.HeapReleaseRevision;

    nint IGpuBindings.AllocateSet(nint poolHandle, nint descriptorSetLayoutHandle) => NextHandle();
    bool IGpuBindings.CanAdmit(string owner, IReadOnlyList<GpuDescriptorPoolSizes> pools, out string refusal) => m_inner.Services.Bindings.CanAdmit(owner: owner, pools: pools, refusal: out refusal);
    nint IGpuBindings.CreatePool(in GpuDescriptorPoolSizes sizes) => m_inner.Services.Bindings.CreatePool(sizes: sizes);
    nint IGpuBindings.CreateSampler(GpuSamplerFilter filter) => m_inner.Services.Bindings.CreateSampler(filter: filter);
    void IGpuBindings.DestroyPool(nint poolHandle) { }
    void IGpuBindings.DestroySampler(nint samplerHandle) { }
    void IGpuBindings.WriteCombinedImageSampler(nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle, nint samplerHandle) { }
    void IGpuBindings.WriteBuffer(nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize, GpuBindingKind kind, uint elementStride) => m_bindings[(descriptorSetHandle, binding)] = bufferHandle;
    void IGpuBindings.WriteConstantBuffer(nint descriptorSetHandle, uint binding, uint arrayElement, nint bufferHandle, ulong bufferSize) { }
    void IGpuBindings.WriteSampledImage(nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle) { }
    void IGpuBindings.WriteSampler(nint descriptorSetHandle, uint binding, uint arrayElement, nint samplerHandle) { }
    void IGpuBindings.WriteStorageImage(nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle) { }
    void IGpuRecorder.BeginCommandBuffer(nint commandBufferHandle) { }
    void IGpuRecorder.EndCommandBuffer(nint commandBufferHandle) { }
    void IGpuRecorder.BeginDebugGroup(nint commandBufferHandle, string label) { }
    void IGpuRecorder.EndDebugGroup(nint commandBufferHandle) { }
    void IGpuRecorder.BeginRenderPass(nint commandBufferHandle, IGpuFramebuffer framebuffer, GpuPixelRect? area) { }
    void IGpuRecorder.EndRenderPass(nint commandBufferHandle) { }
    void IGpuRecorder.BindVertexBuffer(nint commandBufferHandle, nint bufferHandle, ulong sizeBytes, uint strideBytes) { }
    void IGpuRecorder.BindIndexBuffer(nint commandBufferHandle, nint bufferHandle, ulong offsetBytes, ulong sizeBytes, GpuIndexFormat format) { }
    void IGpuRecorder.SetScissor(nint commandBufferHandle, GpuPixelRect rect) { }
    void IGpuRecorder.Draw(nint commandBufferHandle, in GpuDrawParameters parameters) { }
    void IGpuRecorder.DrawIndexed(nint commandBufferHandle, uint indexCount) { }
    void IGpuRecorder.DispatchIndirect(nint commandBufferHandle, nint argumentBufferHandle, ulong argumentBufferOffset) { }
    void IGpuRecorder.ClearStorageImage(nint commandBufferHandle, nint imageHandle, GpuPixelFormat format) { }
    void IGpuRecorder.ClearStorageBuffer(nint commandBufferHandle, nint bufferHandle, ulong sizeBytes) { }
    void IGpuRecorder.TransitionImageLayout(nint commandBufferHandle, nint imageHandle, GpuImageLayout oldLayout, GpuImageLayout newLayout, GpuAccess sourceAccessMask, GpuAccess destinationAccessMask, GpuStage sourceStageMask, GpuStage destinationStageMask) { }
    void IGpuRecorder.MemoryBarrier(nint commandBufferHandle, GpuAccess sourceAccessMask, GpuAccess destinationAccessMask, GpuStage sourceStageMask, GpuStage destinationStageMask) { }
    void IGpuRecorder.TransitionBuffer(nint commandBufferHandle, nint bufferHandle, GpuAccess sourceAccessMask, GpuAccess destinationAccessMask, GpuStage sourceStageMask, GpuStage destinationStageMask) { }
    void IGpuRecorder.BindDescriptorSet(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineLayoutHandle, uint group, nint descriptorSetHandle) => m_boundSet = descriptorSetHandle;
    void IGpuRecorder.BindPipeline(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineHandle) => m_boundPipeline = pipelineHandle;
    void IGpuRecorder.Dispatch(nint commandBufferHandle, uint groupCountX, uint groupCountY, uint groupCountZ) {
        if (!m_uploadPipelines.ContainsKey(key: m_boundPipeline)) {
            return;
        }

        var push = MemoryMarshal.Cast<byte, uint>(span: m_push.AsSpan());
        var source = MemoryMarshal.Cast<byte, uint>(span: m_buffers[m_bindings[(m_boundSet, 0u)]].Memory.AsSpan());
        var destination = MemoryMarshal.Cast<byte, uint>(span: m_buffers[m_bindings[(m_boundSet, 1u)]].Memory.AsSpan());

        var (count, runCount, offset, tableBase) = (push[0], push[1], push[2], push[3]);

        for (var thread = 0u; (thread < count); thread++) {
            var word = (offset + thread);

            if (runCount > 1u) {
                var run = (runCount - 1u);

                while (source[((int)((run * 2u) + 1u))] > thread) {
                    run--;
                }

                word = (source[((int)(run * 2u))] + (thread - source[((int)((run * 2u) + 1u))]));
            }

            destination[((int)word)] = source[((int)(tableBase + word))];
        }

        UploadCopies++;
    }
    void IGpuRecorder.PushConstants(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineLayoutHandle, GpuShaderStage stageFlags, uint offset, ReadOnlySpan<byte> data) {
        if (m_uploadPipelines.ContainsKey(key: m_boundPipeline)) {
            data.CopyTo(destination: m_push.AsSpan(start: ((int)offset)));
        }
    }

    private MemoryBuffer Buffer(bool hostVisible, ulong sizeBytes) {
        var buffer = new MemoryBuffer(
            handle: NextHandle(),
            hostVisible: hostVisible,
            owner: m_buffers,
            sizeBytes: sizeBytes
        );

        m_buffers[buffer.BufferHandle] = buffer;

        return buffer;
    }
    private nint NextHandle() => ((nint)Interlocked.Increment(location: ref m_nextHandle));
    private MemoryBuffer Single(bool hostVisible, ulong sizeBytes) => m_buffers.Values.Single(predicate: buffer =>
        ((buffer.HostVisible == hostVisible) &&
        (buffer.SizeBytes == sizeBytes))
    );

    // A shader module or pipeline: its own handle, plus the layout handles a pipeline carries.
    private sealed class Handles(nint handle, nint layout, nint setLayout) : IGpuComputePipeline, IGpuShaderModule {
        public nint DescriptorSetLayoutHandle => setLayout;
        public IReadOnlyList<nint> GroupLayoutHandles => [];
        public nint Handle => handle;
        public nint LayoutHandle => layout;

        public void Dispose() { }
    }
    private sealed class MemoryBuffer(nint handle, bool hostVisible, Dictionary<nint, MemoryBuffer> owner, ulong sizeBytes) : IGpuStorageBuffer {
        public nint BufferHandle => handle;
        public bool HostVisible => hostVisible;
        public byte[] Memory { get; } = new byte[checked((int)sizeBytes)];
        public ulong SizeBytes => sizeBytes;
        public long Written { get; set; }

        public void Dispose() => _ = owner.Remove(key: handle);
        public void Write<T>(ReadOnlySpan<T> data) where T : unmanaged => Write(
            data: data,
            destinationOffsetBytes: 0UL
        );
        public void Write<T>(ReadOnlySpan<T> data, ulong destinationOffsetBytes) where T : unmanaged {
            var bytes = MemoryMarshal.AsBytes(span: data);

            bytes.CopyTo(destination: Memory.AsSpan(start: checked((int)destinationOffsetBytes)));
            Written += bytes.Length;
        }
    }
}
