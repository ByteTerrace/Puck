using System.Numerics;

using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Shaders.Study;

/// <summary>
/// A generator render node that dispatches one compiled Shadertoy-dialect study as a compute kernel over its own
/// <see cref="SurfaceFormat.R8G8B8A8Unorm"/> storage image — the SDF-pane peer that has no inner producer and samples
/// nothing, since a study's whole picture comes from its own kernel reading <see cref="StudyPushConstants"/>. The
/// image is exactly what a hosted child owes the world compositor (a same-device storage image left in the general
/// layout — see <c>SdfEngineNode</c>'s <c>children</c>), sized by the last <see cref="Resize"/> call (never by
/// <see cref="FrameContext"/>, so a host that wants a different size than the frame's target extent — a split-screen
/// slot smaller than the whole frame — calls <see cref="Resize"/> itself before <see cref="ProduceFrame"/>). Before the
/// first successful <see cref="Swap(StudyProgram)"/> the node dispatches <see cref="StudyPlaceholderShader"/>'s flat
/// 18% grey, so a study slot is never a black pane while a compile is still pending.
/// </summary>
public sealed class StudyPassNode : IRenderNode, ICaptureRequestTarget {
    private const uint StorageImageBinding = 0;

    private readonly IGpuComputeServices m_gpu;
    private readonly NodeDescriptor m_descriptor;
    private readonly IGpuDeviceContext m_deviceContext;
    private readonly bool m_hostsOnDirectX;
    private readonly byte[] m_pushConstantData = new byte[StudyPushConstants.SizeBytes];

    private readonly CaptureRequestSlot m_capture = new();
    private readonly CapturePngWriter m_capturePng = new();

    private IGpuComputeCommandPool? m_commandPool;
    private nint m_descriptorPool;
    private nint m_descriptorSet;
    private bool m_disposed;
    private uint m_frameCounter;
    private IGpuSubmissionFence? m_frameFence;
    private uint m_height;
    private IGpuStorageImage? m_image;
    private bool m_imageInitialized;
    private IGpuShaderModule? m_kernel;
    private ReadOnlyMemory<byte> m_kernelBytecode;
    private IGpuComputePipeline? m_pipeline;
    private IGpuSurfaceReadback? m_readback;
    private bool m_resourcesReady;
    private uint m_width;

    /// <summary>Initializes a new instance of the <see cref="StudyPassNode"/> class, dispatching the flat-grey
    /// placeholder until the first successful <see cref="Swap(StudyProgram)"/>.</summary>
    /// <param name="name">The node's diagnostics name — the study's own name, so multiple studies in one frame
    /// (a split-screen layout) are distinguishable in a GPU capture tool.</param>
    /// <param name="gpu">The compute services, on the same device as the rest of the host's render tree.</param>
    /// <param name="deviceContext">That device.</param>
    /// <param name="hostsOnDirectX">Whether the resolved host backend is Direct3D 12 — selects the placeholder
    /// bytecode and, on every later <see cref="Swap(StudyProgram)"/>, which of a compiled program's two bytecode blobs is used.</param>
    /// <param name="width">The pass width in pixels; changeable later through <see cref="Resize"/>.</param>
    /// <param name="height">The pass height in pixels; changeable later through <see cref="Resize"/>.</param>
    public StudyPassNode(string name, IGpuComputeServices gpu, IGpuDeviceContext deviceContext, bool hostsOnDirectX, uint width, uint height) {
        ArgumentException.ThrowIfNullOrEmpty(argument: name);
        ArgumentNullException.ThrowIfNull(argument: gpu);
        ArgumentNullException.ThrowIfNull(argument: deviceContext);
        ArgumentOutOfRangeException.ThrowIfZero(value: width);
        ArgumentOutOfRangeException.ThrowIfZero(value: height);

        m_descriptor = new NodeDescriptor(Name: name, SurfaceId: SurfaceId.New());
        m_deviceContext = deviceContext;
        m_gpu = gpu;
        m_height = height;
        m_hostsOnDirectX = hostsOnDirectX;
        m_kernelBytecode = StudyPlaceholderShader.Kernel(hostsOnDirectX: hostsOnDirectX);
        m_width = width;
    }

    /// <inheritdoc/>
    public NodeDescriptor Descriptor => m_descriptor;
    /// <summary>Gets or sets the per-frame values <see cref="ProduceFrame"/> fills into the push-constant block
    /// beyond its own tracked resolution and frame counter. The host writes this before calling
    /// <see cref="ProduceFrame"/>.</summary>
    public StudyFrameInput Input { get; set; }
    /// <inheritdoc/>
    public string? PendingCapturePath => m_capture.PendingPath;

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_capture.Refuse(error: new ObjectDisposedException(objectName: GetType().Name));
        ReleaseGpuResources();
    }
    /// <inheritdoc/>
    public void OnDeviceLost() => ReleaseGpuResources();
    /// <inheritdoc/>
    public Surface ProduceFrame(in FrameContext context) {
        if (m_disposed) {
            return default;
        }

        EnsureResources();
        m_frameFence!.Wait();
        FillPushConstants();
        m_frameCounter++;

        Span<nint> commandBuffers = [RecordDispatch()];

        m_gpu.QueueSubmitter.Submit(commandBufferHandles: commandBuffers, deviceContext: m_deviceContext, fence: m_frameFence!);
        CaptureIfPending();

        return Surface.SameDeviceImage(
            imageHandle: m_image!.ImageHandle,
            imageViewHandle: m_image!.ImageViewHandle,
            width: m_width,
            height: m_height,
            format: SurfaceFormat.R8G8B8A8Unorm
        );
    }
    /// <inheritdoc/>
    public void RequestCapture(FrameCaptureRequest request) {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
        m_capture.Arm(pendingPath: PendingCapturePath, request: request);
    }
    /// <summary>Resizes the pass's storage image — the world's layout slot a study renders into can change size (a
    /// split-screen resize, a device-lost recovery at a new window size). The pipeline is untouched (a compute
    /// dispatch bakes no viewport). A no-op when the size is unchanged. Safe to call before resources are built (the
    /// new size takes effect the first time <see cref="ProduceFrame"/> runs) and between produced frames (it waits for
    /// the last submission to retire before touching anything the GPU might still be reading).</summary>
    /// <param name="width">The new pass width in pixels.</param>
    /// <param name="height">The new pass height in pixels.</param>
    public void Resize(uint width, uint height) {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
        ArgumentOutOfRangeException.ThrowIfZero(value: width);
        ArgumentOutOfRangeException.ThrowIfZero(value: height);

        if ((width == m_width) && (height == m_height)) {
            return;
        }

        m_width = width;
        m_height = height;

        if (!m_resourcesReady) {
            return;
        }

        m_frameFence!.Wait();

        var image = m_gpu.StorageImageFactory.Create(deviceContext: m_deviceContext, format: GpuPixelFormat.R8G8B8A8Unorm, height: m_height, width: m_width);
        var previousImage = m_image;

        m_image = image;
        m_imageInitialized = false;
        WriteImageDescriptor();
        previousImage?.Dispose();
    }
    /// <summary>Installs a <see cref="StudyShaderCompiler"/> result, using whichever of its two bytecode blobs this
    /// node's own backend reads. See the <see cref="Swap(ReadOnlyMemory{byte}, ReadOnlyMemory{byte})"/> overload for
    /// the install discipline; a failed compile (<see cref="StudyProgram.IsError"/>) is ignored the same way an
    /// empty blob is.</summary>
    /// <param name="program">The compiled study.</param>
    public void Swap(StudyProgram program) {
        ArgumentNullException.ThrowIfNull(argument: program);

        Swap(spirv: program.Spirv, dxil: program.Dxil);
    }
    /// <summary>Installs a newly compiled study kernel, replacing the pipeline currently dispatching. A program whose
    /// bytecode for this host's backend is empty (a failed compile) is ignored — the last good pipeline keeps
    /// rendering, including the flat-grey placeholder if nothing has ever swapped in successfully. The new pipeline
    /// is built first, then installed only once the last submission has retired, so a rebuild never races a
    /// still-in-flight frame — the old pipeline is disposed only after that wait, never before. The descriptor set
    /// survives a swap: every study pipeline declares the identical one-storage-image layout.</summary>
    /// <param name="spirv">The compiled study's SPIR-V, or empty for a failed compile.</param>
    /// <param name="dxil">The compiled study's DXIL, or empty for a failed compile.</param>
    public void Swap(ReadOnlyMemory<byte> spirv, ReadOnlyMemory<byte> dxil) {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);

        var bytecode = (m_hostsOnDirectX ? dxil : spirv);

        if (bytecode.IsEmpty) {
            return;
        }

        m_kernelBytecode = bytecode;

        if (!m_resourcesReady) {
            return;
        }

        var kernel = m_gpu.ShaderModuleFactory.Create(deviceContext: m_deviceContext, stage: GpuShaderStage.Compute, bytecode: bytecode);
        IGpuComputePipeline pipeline;

        try {
            pipeline = BuildPipeline(kernel: kernel);
        } catch {
            kernel.Dispose();

            throw;
        }

        m_frameFence!.Wait();

        var previousPipeline = m_pipeline;
        var previousKernel = m_kernel;

        m_pipeline = pipeline;
        m_kernel = kernel;

        previousPipeline?.Dispose();
        previousKernel?.Dispose();
    }

    private static uint GroupCount(uint extent) =>
        ((extent + (StudyPrelude.WorkgroupSize - 1)) / StudyPrelude.WorkgroupSize);
    private IGpuComputePipeline BuildPipeline(IGpuShaderModule kernel) =>
        m_gpu.ComputePipelineFactory.Create(
            computeShaderModule: kernel,
            description: new GpuComputePipelineDescription(
                Name: m_descriptor.Name,
                Bindings: [new GpuComputeBinding(Binding: StorageImageBinding, Kind: GpuComputeBindingKind.StorageImage)],
                PushConstantBinding: new GpuPushConstantBinding(data: new byte[StudyPushConstants.SizeBytes], offset: 0, stageFlags: GpuShaderStage.Compute)
            ),
            deviceContext: m_deviceContext
        );
    // Reads back this pass's own storage image and writes it as a PNG — the same discipline as FullscreenPassNode's
    // own capture path, from the general layout the image rests in between frames.
    private void CaptureIfPending() =>
        m_capture.Serve(
            failureLabel: "[capture] failed",
            writer: WriteCapture
        );
    private void EnsureResources() {
        if (m_resourcesReady) {
            return;
        }

        m_image = m_gpu.StorageImageFactory.Create(deviceContext: m_deviceContext, format: GpuPixelFormat.R8G8B8A8Unorm, height: m_height, width: m_width);
        m_imageInitialized = false;
        m_frameFence = m_gpu.QueueSubmitter.CreateSubmissionFence(deviceContext: m_deviceContext);
        m_commandPool = m_gpu.CommandPoolFactory.Create(deviceContext: m_deviceContext);
        m_kernel = m_gpu.ShaderModuleFactory.Create(bytecode: m_kernelBytecode, deviceContext: m_deviceContext, stage: GpuShaderStage.Compute);
        m_pipeline = BuildPipeline(kernel: m_kernel);
        m_descriptorPool = m_gpu.DescriptorAllocator.CreatePool(
            deviceHandle: m_deviceContext.DeviceHandle,
            sizes: GpuDescriptorPoolSizes.ForSets([[new GpuComputeBinding(Binding: StorageImageBinding, Kind: GpuComputeBindingKind.StorageImage)]])
        );
        m_descriptorSet = m_gpu.DescriptorAllocator.AllocateSet(
            descriptorSetLayoutHandle: m_pipeline.DescriptorSetLayoutHandle,
            deviceHandle: m_deviceContext.DeviceHandle,
            poolHandle: m_descriptorPool
        );
        WriteImageDescriptor();
        m_resourcesReady = true;
    }
    private void FillPushConstants() {
        var input = Input;
        var constants = new StudyPushConstants(
            IResolution: new Vector3(m_width, m_height, 1f),
            ITime: ((float)input.Seconds),
            ITimeDelta: ((float)input.DeltaSeconds),
            IFrame: unchecked((int)m_frameCounter),
            Pad0: default,
            IMouse: input.Mouse,
            IDate: input.Date,
            ICameraPos: input.CameraPos,
            ICameraFov: input.CameraFov,
            ICameraTarget: input.CameraTarget,
            Pad1: 0f,
            ICameraUp: input.CameraUp,
            Pad2: 0f
        );

        constants.CopyTo(destination: m_pushConstantData);
    }
    private nint RecordDispatch() {
        var deviceHandle = m_deviceContext.DeviceHandle;
        var commandBufferHandle = m_commandPool!.CommandBufferHandle;
        var recorder = m_gpu.ComputeRecorder;

        recorder.BeginCommandBuffer(commandBufferHandle: commandBufferHandle, deviceHandle: deviceHandle);
        recorder.BeginDebugGroup(commandBufferHandle: commandBufferHandle, deviceHandle: deviceHandle, label: m_descriptor.Name);
        // The image rests in the general layout between frames (the compositor reads it there): the first frame
        // after creation defines that layout; every later frame orders this dispatch's write after the compositor's
        // previous read of the same image.
        recorder.TransitionImageLayout(
            commandBufferHandle: commandBufferHandle,
            destinationAccessMask: GpuComputeAccess.ShaderWrite,
            destinationStageMask: GpuComputeStage.ComputeShader,
            deviceHandle: deviceHandle,
            imageHandle: m_image!.ImageHandle,
            newLayout: GpuImageLayout.General,
            oldLayout: (m_imageInitialized ? GpuImageLayout.General : GpuImageLayout.Undefined),
            sourceAccessMask: (m_imageInitialized ? GpuComputeAccess.ShaderRead : GpuComputeAccess.None),
            sourceStageMask: (m_imageInitialized ? GpuComputeStage.ComputeShader : GpuComputeStage.TopOfPipe)
        );
        m_imageInitialized = true;
        recorder.BindComputePipeline(commandBufferHandle: commandBufferHandle, deviceHandle: deviceHandle, pipelineHandle: m_pipeline!.Handle);
        recorder.BindComputeDescriptorSet(commandBufferHandle: commandBufferHandle, descriptorSetHandle: m_descriptorSet, deviceHandle: deviceHandle, pipelineLayoutHandle: m_pipeline.LayoutHandle);
        recorder.PushConstants(
            commandBufferHandle: commandBufferHandle,
            data: m_pushConstantData,
            deviceHandle: deviceHandle,
            offset: 0,
            pipelineLayoutHandle: m_pipeline.LayoutHandle,
            stageFlags: GpuShaderStage.Compute
        );
        recorder.Dispatch(commandBufferHandle: commandBufferHandle, deviceHandle: deviceHandle, groupCountX: GroupCount(extent: m_width), groupCountY: GroupCount(extent: m_height), groupCountZ: 1);
        recorder.EndDebugGroup(commandBufferHandle: commandBufferHandle, deviceHandle: deviceHandle);
        recorder.EndCommandBuffer(commandBufferHandle: commandBufferHandle, deviceHandle: deviceHandle);

        return commandBufferHandle;
    }
    private void ReleaseGpuResources() {
        if (!m_resourcesReady) {
            return;
        }

        m_frameFence?.Wait();
        m_readback?.Dispose();
        m_readback = null;
        m_pipeline?.Dispose();
        m_pipeline = null;
        m_kernel?.Dispose();
        m_kernel = null;

        if (m_descriptorPool != 0) {
            m_gpu.DescriptorAllocator.DestroyPool(deviceHandle: m_deviceContext.DeviceHandle, poolHandle: m_descriptorPool);
            m_descriptorPool = 0;
            m_descriptorSet = 0;
        }

        m_commandPool?.Dispose();
        m_commandPool = null;
        m_frameFence?.Dispose();
        m_frameFence = null;
        m_image?.Dispose();
        m_image = null;
        m_imageInitialized = false;
        m_resourcesReady = false;
    }
    private void WriteCapture(string path) {
        m_capturePng.ThrowIfUnavailable(path: path);

        m_readback ??= m_gpu.SurfaceTransferFactory.CreateReadback(deviceContext: m_deviceContext);

        var pixels = m_readback.Read(
            bytesPerPixel: 4,
            deviceContext: m_deviceContext,
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: m_height,
            sourceImageHandle: m_image!.ImageHandle,
            sourceLayout: GpuImageLayout.General,
            width: m_width
        );

        if (!m_capturePng.TryWrite(
            height: ((int)m_height),
            path: path,
            rgba: pixels,
            width: ((int)m_width)
        )) {
            throw new NotSupportedException(message: "PNG capture is unavailable.");
        }

        Console.Error.WriteLine(value: $"[capture] {m_descriptor.Name} -> {path}");
    }
    private void WriteImageDescriptor() =>
        m_gpu.DescriptorAllocator.WriteStorageImage(
            arrayElement: 0,
            binding: StorageImageBinding,
            descriptorSetHandle: m_descriptorSet,
            deviceHandle: m_deviceContext.DeviceHandle,
            imageViewHandle: m_image!.ImageViewHandle
        );
}
