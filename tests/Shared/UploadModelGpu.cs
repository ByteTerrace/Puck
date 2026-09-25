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
/// </summary>
internal sealed class UploadModelGpu :
    IGpuPipelineFactory,
    IGpuRecorder,
    IGpuComputeServices,
    IGpuBindings,
    IGpuShaderModuleFactory,
    IGpuBufferFactory {
    /// <summary>The first bytecode byte that marks the frame-upload kernel.</summary>
    public const byte FrameUploadBytecode = 0xF7;

    private readonly Dictionary<(nint Set, uint Binding), nint> m_bindings = [];
    private readonly Dictionary<nint, MemoryBuffer> m_buffers = [];
    private readonly FakeGpuDevice m_inner;
    private readonly byte[] m_push = new byte[16];

    private nint m_boundPipeline;
    private nint m_boundSet;
    private nint m_nextHandle = 0x1000;
    private nint m_uploadModule;
    private nint m_uploadPipeline;

    /// <summary>Initializes a new instance of the <see cref="UploadModelGpu"/> class.</summary>
    /// <param name="reportVersion">The ISA version a 1×1 readback reports.</param>
    public UploadModelGpu(byte reportVersion) {
        m_inner = new FakeGpuDevice(reportVersion: reportVersion);
    }

    public IGpuBindings Bindings => this;
    public IGpuComputeCommandPoolFactory CommandPoolFactory => m_inner.CommandPoolFactory;
    public IGpuPipelineFactory PipelineFactory => this;
    public IGpuRecorder Recorder => this;
    /// <summary>Gets the device the engine renders on.</summary>
    public IGpuDeviceContext Device => m_inner;
    public IGpuBufferFactory BufferFactory => this;
    public IGpuImageFactory ImageFactory => m_inner.ImageFactory;
    public IGpuQueueSubmitter QueueSubmitter => m_inner.QueueSubmitter;
    public IGpuShaderModuleFactory ShaderModuleFactory => this;
    public IGpuSurfaceTransferFactory SurfaceTransferFactory => m_inner.SurfaceTransferFactory;
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

    IGpuComputePipeline IGpuPipelineFactory.Create(IGpuShaderModule computeShaderModule, GpuComputePipelineDescription description) {
        var pipeline = new Handles(handle: NextHandle(), layout: NextHandle(), setLayout: NextHandle());

        if (computeShaderModule.Handle == m_uploadModule) {
            m_uploadPipeline = pipeline.Handle;
        }

        return pipeline;
    }
    IGpuShaderModule IGpuShaderModuleFactory.Create(GpuShaderStage stage, ReadOnlyMemory<byte> bytecode) {
        var module = new Handles(handle: NextHandle(), layout: 0, setLayout: 0);

        if (
            !bytecode.IsEmpty &&
            (bytecode.Span[0] == FrameUploadBytecode)
        ) {
            m_uploadModule = module.Handle;
        }

        return module;
    }
    IGpuStorageBuffer IGpuBufferFactory.CreateHostVisible(ulong sizeBytes, GpuBufferUsage usage) => Buffer(hostVisible: true, sizeBytes: sizeBytes);
    IGpuBuffer IGpuBufferFactory.CreateDeviceLocal(ulong sizeBytes, GpuBufferUsage usage) => Buffer(hostVisible: false, sizeBytes: sizeBytes);
    IGpuStorageBuffer IGpuBufferFactory.CreateHostVisible(ReadOnlySpan<byte> data, GpuBufferUsage usage) => throw new NotSupportedException();
    IGpuPipeline IGpuPipelineFactory.Create(IGpuRenderPass renderPass, IGpuShaderModule vertexShaderModule, IGpuShaderModule fragmentShaderModule, GpuGraphicsPipelineDescription description) => throw new NotSupportedException();
    nint IGpuBindings.AllocateSet(nint poolHandle, nint descriptorSetLayoutHandle) => NextHandle();
    nint IGpuBindings.CreatePool(in GpuDescriptorPoolSizes sizes) => m_inner.Bindings.CreatePool(sizes: sizes);
    nint IGpuBindings.CreateSampler(GpuSamplerFilter filter) => m_inner.Bindings.CreateSampler(filter: filter);
    void IGpuBindings.DestroyPool(nint poolHandle) { }
    void IGpuBindings.DestroySampler(nint samplerHandle) { }
    void IGpuBindings.WriteCombinedImageSampler(nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle, nint samplerHandle) { }
    void IGpuBindings.WriteBuffer(nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize, GpuBufferAccess access, uint elementStride) => m_bindings[(descriptorSetHandle, binding)] = bufferHandle;
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
    void IGpuRecorder.TransitionImageLayout(nint commandBufferHandle, nint imageHandle, GpuImageLayout oldLayout, GpuImageLayout newLayout, GpuComputeAccess sourceAccessMask, GpuComputeAccess destinationAccessMask, GpuComputeStage sourceStageMask, GpuComputeStage destinationStageMask) { }
    void IGpuRecorder.MemoryBarrier(nint commandBufferHandle, GpuComputeAccess sourceAccessMask, GpuComputeAccess destinationAccessMask, GpuComputeStage sourceStageMask, GpuComputeStage destinationStageMask) { }
    void IGpuRecorder.TransitionBuffer(nint commandBufferHandle, nint bufferHandle, GpuComputeAccess sourceAccessMask, GpuComputeAccess destinationAccessMask, GpuComputeStage sourceStageMask, GpuComputeStage destinationStageMask) { }
    void IGpuRecorder.BindDescriptorSet(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineLayoutHandle, nint descriptorSetHandle) => m_boundSet = descriptorSetHandle;
    void IGpuRecorder.BindPipeline(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineHandle) => m_boundPipeline = pipelineHandle;
    void IGpuRecorder.Dispatch(nint commandBufferHandle, uint groupCountX, uint groupCountY, uint groupCountZ) {
        if (
            (0 == m_uploadPipeline) ||
            (m_boundPipeline != m_uploadPipeline)
        ) {
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
        if (
            (0 != m_uploadPipeline) &&
            (m_boundPipeline == m_uploadPipeline)
        ) {
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
    private nint NextHandle() => m_nextHandle++;
    private MemoryBuffer Single(bool hostVisible, ulong sizeBytes) => m_buffers.Values.Single(predicate: buffer =>
        ((buffer.HostVisible == hostVisible) &&
        (buffer.SizeBytes == sizeBytes))
    );

    // A shader module or pipeline: its own handle, plus the layout handles a pipeline carries.
    private sealed class Handles(nint handle, nint layout, nint setLayout) : IGpuComputePipeline, IGpuShaderModule {
        public nint DescriptorSetLayoutHandle => setLayout;
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
