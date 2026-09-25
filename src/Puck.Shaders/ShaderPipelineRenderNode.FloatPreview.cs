using System.Runtime.ExceptionServices;
using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders;

// The float-preview pass: converts a selected float or external image into the RGBA8 surface the node publishes.
public sealed partial class ShaderPipelineRenderNode {
    private readonly BackgroundBuild<PreviewObjects> m_previewBuild = new();

    private PreviewRequest? m_previewBuilding;
    private PreviewRequest? m_previewRequest;

    /// <summary>Gets whether a selection's float preview is being built on the thread pool. The current selection stays
    /// published meanwhile; the new one takes effect at the first frame boundary after the build finishes.</summary>
    public bool IsBuildingPreview => (m_previewBuild.IsPending && !m_previewBuild.IsCompleted);

    // Puts built preview objects into service with the descriptor and command objects the frame thread owns.
    private FloatPreviewPass CreatePreview(PreviewObjects objects) => new(
        device: m_device,
        gpu: m_gpu,
        graphics: (m_graphics ?? throw new InvalidOperationException(message: "Float preview requires graphics services.")),
        inFlight: m_inFlight,
        objects: objects,
        outputLayout: m_outputLayout
    );
    private (uint Width, uint Height) PreviewExtent(RuntimeResource selected) => (selected.Spec.Dimensions?.Resolve(
        frameHeight: m_height,
        frameWidth: m_width
    ) ?? (m_width, m_height));
    // Makes the published preview match a newly selected output of the installed graph, and says whether the selection
    // takes effect now. A selection needing no preview, or one the current preview already fits, takes effect at once
    // (the old preview retires like a replaced graph). Otherwise the new preview's modules, pipelines and targets build on
    // the thread pool, the current selection stays published meanwhile, and the selection takes effect at the frame
    // boundary that takes the finished build. A preview whose targets would take the node past its budget, beside
    // everything it owns, is refused here, before anything is created.
    private bool RequestPreview(string name, RuntimeResource selected) {
        m_previewRequest = null;

        if (!NeedsPreview(spec: selected.Spec)) {
            if (m_preview is not null) {
                Retire(
                    passes: [],
                    preview: m_preview,
                    resources: []
                );
                m_preview = null;
            }

            return true;
        }

        var extent = PreviewExtent(selected: selected);

        if (
            (m_preview is { } current) &&
            (current.Width == extent.Width) &&
            (current.Height == extent.Height)
        ) {
            return true;
        }

        var previewBytes = PreviewBytes(
            extent: extent,
            inFlight: m_inFlight
        );
        var account = new ShaderPipelineMemoryAccount(
            BudgetBytes: BudgetBytes,
            PeakBytes: checked((OwnedBytes + previewBytes)),
            SteadyBytes: checked((m_allocationBytes + previewBytes))
        );

        if (!account.Fits) {
            var refusal = account.Refusal();

            throw new InvalidOperationException(
                innerException: refusal,
                message: $"The float preview for '{selected.Spec.Name}' is refused: {refusal.Message}"
            );
        }

        m_previewRequest = new PreviewRequest(
            Height: extent.Height,
            Name: name,
            Width: extent.Width
        );
        if (!m_previewBuild.IsPending) {
            StartPreviewBuild(request: m_previewRequest);
        }

        return false;
    }
    // Kept apart from RequestPreview so the build's closure is allocated only when a build starts.
    private void StartPreviewBuild(PreviewRequest request) {
        var device = m_device;
        var gpu = m_gpu;
        var directX = m_directX;
        var graphics = (m_graphics ?? throw new InvalidOperationException(message: "Float preview requires graphics services."));
        var inFlight = m_inFlight;

        m_previewBuilding = request;
        m_previewBuild.Start(build: _ => PreviewObjects.Create(
            device: device,
            gpu: gpu,
            directX: directX,
            graphics: graphics,
            height: request.Height,
            inFlight: inFlight,
            width: request.Width
        ));
    }
    // Takes a finished preview build at a frame boundary. It installs when it still matches the latest selection request
    // against the installed graph; otherwise it is discarded and the latest request, if any, builds next. A failed build
    // leaves the current selection published and reports the failure where a refused swap reports its own.
    private void InstallPendingPreview() {
        if (!m_previewBuild.TryTake(
            error: out var error,
            result: out var built
        )) {
            return;
        }

        var building = m_previewBuilding;

        m_previewBuilding = null;

        if (
            (m_previewRequest is not { } request) ||
            (request != building) ||
            !m_ready
        ) {
            built?.Dispose();
            if (m_previewRequest is { } latest) {
                StartPreviewBuild(request: latest);
            }

            return;
        }

        m_previewRequest = null;

        FloatPreviewPass next;

        try {
            if (error is not null) {
                ExceptionDispatchInfo.Throw(source: error);
            }
            // Its descriptors and command pools are the frame thread's; a failure there disposes the built objects too.
            next = CreatePreview(objects: built!);
        } catch (Exception failure) {
            m_lastSwapError = new InvalidOperationException(
                innerException: failure,
                message: $"The float preview for '{request.Name}' could not be allocated: {failure.Message}"
            );

            return;
        }

        if (m_preview is not null) {
            Retire(
                passes: [],
                preview: m_preview,
                resources: []
            );
        }
        m_preview = next;
        m_selectedOutput = request.Name;
        m_outputRefreshRequested = true;
    }
    // Drops a pending preview selection and waits out its build, which creates objects on the device being released.
    private void CancelPreviewBuild() {
        m_previewRequest = null;
        m_previewBuilding = null;
        m_previewBuild.CancelAndWait(discard: static built => built.Dispose());
    }

    // A selection whose float preview is building: the output it selects and the preview's extent.
    private sealed record PreviewRequest(string Name, uint Width, uint Height);
    /// <summary>
    /// The float preview's pipeline and module set: its two shader modules, read from the deployed preview bytecode, the
    /// render pass it draws in and the graphics pipeline created for it, and per frame slot the RGBA8 image it draws into
    /// with the framebuffer that binds it. An install builds it on the thread pool with the rest of the candidate. Each
    /// object is stored as soon as it exists, so a failure partway disposes exactly what was created.
    /// </summary>
    private sealed class PreviewObjects : IDisposable {
        // The usages of a preview image: drawn into, then published and sampled downstream, in General as a storage image.
        private const GpuImageUsage TargetUsage = GpuImageUsage.ColorAttachment | GpuImageUsage.Sampled | GpuImageUsage.Storage;

        private PreviewObjects(uint width, uint height, uint inFlight) {
            Width = width;
            Height = height;
            Framebuffers = new IGpuFramebuffer[inFlight];
            Targets = new IGpuImage[inFlight];
        }

        public IGpuShaderModule? Fragment { get; private set; }
        public IGpuFramebuffer[] Framebuffers { get; }
        public uint Height { get; }
        public IGpuPipeline? Pipeline { get; private set; }
        public IGpuRenderPass? RenderPass { get; private set; }
        public IGpuImage[] Targets { get; }
        public IGpuShaderModule? Vertex { get; private set; }
        public uint Width { get; }

        public static PreviewObjects Create(IGpuComputeServices gpu, IFullscreenPassServices graphics, IGpuDeviceContext device, bool directX, uint width, uint height, uint inFlight) {
            var objects = new PreviewObjects(
                height: height,
                inFlight: inFlight,
                width: width
            );
            var extension = (directX
                ? ".dxil"
                : ".spv"
            );
            var root = Path.Combine(
                path1: AppContext.BaseDirectory,
                path2: "Assets",
                path3: "Runtime",
                path4: "pipeline-preview"
            );

            try {
                objects.Vertex = gpu.ShaderModuleFactory.Create(
                    device,
                    GpuShaderStage.Vertex,
                    File.ReadAllBytes(path: ((root + ".vert") + extension))
                );
                objects.Fragment = gpu.ShaderModuleFactory.Create(
                    device,
                    GpuShaderStage.Fragment,
                    File.ReadAllBytes(path: ((root + ".frag") + extension))
                );
                var description = new GpuGraphicsPipelineDescription(
                    "pipeline-float-preview",
                    new GpuVertexInputLayout(
                        Attributes: [],
                        StrideBytes: 0
                    ),
                    1,
                    false,
                    null
                );

                objects.RenderPass = graphics.RenderPassFactory.Create(
                    deviceContext: device,
                    description: new GpuRenderPassDescription(Colors: [new GpuColorAttachment(
                        FinalLayout: GpuImageLayout.ShaderReadOnly,
                        Format: GpuPixelFormat.R8G8B8A8Unorm,
                        Load: GpuAttachmentLoad.Clear,
                        Store: GpuAttachmentStore.Store
                    )])
                );
                objects.Pipeline = graphics.PipelineFactory.Create(
                    device,
                    objects.RenderPass,
                    objects.Vertex,
                    objects.Fragment,
                    description
                );

                for (var i = 0; (i < inFlight); i++) {
                    objects.Targets[i] = gpu.ImageFactory.Create(
                        deviceContext: device,
                        format: GpuPixelFormat.R8G8B8A8Unorm,
                        height: height,
                        usage: TargetUsage,
                        width: width
                    );
                    objects.Framebuffers[i] = graphics.RenderPassFactory.CreateFramebuffer(
                        device,
                        objects.RenderPass,
                        [objects.Targets[i]],
                        null
                    );
                }
            } catch {
                objects.Dispose();

                throw;
            }

            return objects;
        }
        public void Dispose() {
            foreach (var framebuffer in Framebuffers) { framebuffer?.Dispose(); }
            Pipeline?.Dispose();
            RenderPass?.Dispose();
            Vertex?.Dispose();
            Fragment?.Dispose();
            foreach (var target in Targets) { target?.Dispose(); }
        }
    }
    /// <summary>
    /// Draws a selected float or external image into RGBA8 targets, one per frame slot. It owns built
    /// <see cref="PreviewObjects"/>, and its constructor creates the rest on the frame thread — per slot a descriptor
    /// pool, set and sampler, and the pre-barrier, draw and post-barrier command pools — so recording a frame creates
    /// nothing. Each
    /// object is stored as soon as it exists, so a failure partway disposes exactly what was created, the built objects
    /// included.
    /// </summary>
    private sealed class FloatPreviewPass : IDisposable {
        private const GpuComputeAccess PriorAccess = GpuComputeAccess.ShaderRead | GpuComputeAccess.ShaderWrite | GpuComputeAccess.TransferWrite | GpuComputeAccess.ColorAttachmentWrite;
        private const GpuComputeStage PriorStages = GpuComputeStage.ComputeShader | GpuComputeStage.FragmentShader | GpuComputeStage.Transfer | GpuComputeStage.ColorAttachmentOutput;

        private readonly nint[] m_descriptorPools;
        private readonly nint[] m_descriptorSets;
        private readonly IGpuDeviceContext m_device;
        private readonly IGpuComputeServices m_gpu;
        private readonly IFullscreenPassServices m_graphics;
        private readonly PreviewObjects m_objects;
        private readonly GpuImageLayout m_outputLayout;
        private readonly IGpuComputeCommandPool[] m_draw;
        private readonly IGpuPipeline m_pipeline;
        private readonly IGpuComputeCommandPool[] m_post;
        private readonly IGpuComputeCommandPool[] m_pre;
        private readonly nint[] m_samplers;
        private readonly bool[] m_targetInitialized;
        private readonly IGpuImage[] m_targets;

        public FloatPreviewPass(PreviewObjects objects, IGpuComputeServices gpu, IFullscreenPassServices graphics, IGpuDeviceContext device, uint inFlight, GpuImageLayout outputLayout) {
            m_objects = objects;
            m_gpu = gpu;
            m_graphics = graphics;
            m_device = device;
            m_outputLayout = outputLayout;
            m_targets = objects.Targets;
            m_pipeline = objects.Pipeline!;
            m_draw = new IGpuComputeCommandPool[inFlight];
            m_pre = new IGpuComputeCommandPool[inFlight];
            m_post = new IGpuComputeCommandPool[inFlight];
            m_descriptorPools = new nint[inFlight];
            m_descriptorSets = new nint[inFlight];
            m_samplers = new nint[inFlight];
            m_targetInitialized = new bool[inFlight];
            try {
                var allocator = gpu.DescriptorAllocator;
                var poolSizes = new GpuDescriptorPoolSizes(
                    CombinedImageSamplerCount: 1,
                    MaxSets: 1,
                    StorageBufferCount: 0,
                    StorageImageCount: 0
                );

                for (var i = 0; (i < inFlight); i++) {
                    m_descriptorPools[i] = allocator.CreatePool(
                        deviceHandle: device.DeviceHandle,
                        sizes: poolSizes
                    );
                    m_descriptorSets[i] = allocator.AllocateSet(
                        device.DeviceHandle,
                        m_descriptorPools[i],
                        m_pipeline.DescriptorSetLayoutHandle
                    );
                    m_samplers[i] = allocator.CreateSampler(deviceHandle: device.DeviceHandle);
                    m_pre[i] = gpu.CommandPoolFactory.Create(deviceContext: device);
                    m_draw[i] = gpu.CommandPoolFactory.Create(deviceContext: device);
                    m_post[i] = gpu.CommandPoolFactory.Create(deviceContext: device);
                }
            } catch { Dispose(); throw; }
        }

        public uint Height => m_objects.Height;
        public uint Width => m_objects.Width;

        public void Dispose() {
            m_objects.Dispose();
            foreach (var pool in m_pre) { pool?.Dispose(); }
            foreach (var pool in m_draw) { pool?.Dispose(); }
            foreach (var pool in m_post) { pool?.Dispose(); }
            foreach (var sampler in m_samplers) {
                if (sampler != 0) {
                    m_gpu.DescriptorAllocator.DestroySampler(
                        deviceHandle: m_device.DeviceHandle,
                        samplerHandle: sampler
                    );
                }
            }
            foreach (var pool in m_descriptorPools) {
                if (pool != 0) {
                    m_gpu.DescriptorAllocator.DestroyPool(
                        deviceHandle: m_device.DeviceHandle,
                        poolHandle: pool
                    );
                }
            }
        }
        public IGpuImage GetTarget(int slot) => m_targets[slot];
        // The bytes of the targets the preview still owns.
        public ulong LiveBytes() {
            var bytes = 0UL;

            foreach (var target in m_targets) {
                if (target is not null) {
                    bytes = checked((bytes + ((((ulong)target.Width) * target.Height) * 4UL)));
                }
            }

            return bytes;
        }
        // Hands over the target whose image is the given one, so it outlives the preview; null when none is.
        public IGpuImage? TakeTarget(nint imageHandle) {
            for (var slot = 0; (slot < m_targets.Length); slot++) {
                if ((m_targets[slot] is { } target) && (target.ImageHandle == imageHandle)) {
                    m_targets[slot] = null!;

                    return target;
                }
            }

            return null;
        }
        // Records the draw of one slot. sourceBarrier is the node's barrier before the source is sampled; the preview's
        // own targets are private to it and are tracked here.
        public void Record(ShaderPipelineBarrier sourceBarrier, ShaderPipelineExternalImage image, int slot, List<nint> commands) {
            var sourceImageHandle = image.ImageHandle;
            var sourceImageView = image.ImageViewHandle;

            m_gpu.DescriptorAllocator.WriteCombinedImageSampler(
                m_device.DeviceHandle,
                m_descriptorSets[slot],
                0,
                0,
                sourceImageView,
                m_samplers[slot]
            );
            var recorder = m_gpu.Recorder;
            var pre = m_pre[slot];

            recorder.BeginCommandBuffer(
                commandBufferHandle: pre.CommandBufferHandle
            );
            if (sourceBarrier.Kind == ShaderPipelineBarrierKind.Image) {
                recorder.TransitionImageLayout(
                    pre.CommandBufferHandle,
                    sourceImageHandle,
                    sourceBarrier.OldLayout,
                    sourceBarrier.NewLayout,
                    sourceBarrier.SourceAccess,
                    sourceBarrier.DestinationAccess,
                    sourceBarrier.SourceStage,
                    sourceBarrier.DestinationStage
                );
            } else if (sourceBarrier.Kind == ShaderPipelineBarrierKind.Memory) {
                recorder.MemoryBarrier(
                    pre.CommandBufferHandle,
                    sourceBarrier.SourceAccess,
                    sourceBarrier.DestinationAccess,
                    sourceBarrier.SourceStage,
                    sourceBarrier.DestinationStage
                );
            }
            var target = m_targets[slot];
            var targetOld = (m_targetInitialized[slot]
                ? m_outputLayout
                : GpuImageLayout.Undefined
            );

            recorder.TransitionImageLayout(
                pre.CommandBufferHandle,
                target.ImageHandle,
                targetOld,
                GpuImageLayout.RenderTarget,
                ((targetOld == GpuImageLayout.Undefined)
                ? GpuComputeAccess.None
                : PriorAccess),
                GpuComputeAccess.ColorAttachmentWrite,
                ((targetOld == GpuImageLayout.Undefined)
                ? GpuComputeStage.TopOfPipe
                : PriorStages),
                GpuComputeStage.ColorAttachmentOutput
            );
            recorder.EndCommandBuffer(
                commandBufferHandle: pre.CommandBufferHandle
            );
            commands.Add(item: pre.CommandBufferHandle);
            var draw = m_draw[slot].CommandBufferHandle;
            var graphics = m_graphics.Recorder;

            graphics.BeginCommandBuffer(
                commandBufferHandle: draw
            );
            graphics.BeginRenderPass(
                draw,
                m_objects.Framebuffers[slot]
            );

            graphics.BindPipeline(
                bindPoint: GpuBindPoint.Graphics,
                commandBufferHandle: draw,
                pipelineHandle: m_pipeline.Handle
            );
            graphics.BindDescriptorSet(
                bindPoint: GpuBindPoint.Graphics,
                commandBufferHandle: draw,
                descriptorSetHandle: m_descriptorSets[slot],
                pipelineLayoutHandle: m_pipeline.LayoutHandle
            );
            graphics.Draw(
                commandBufferHandle: draw,
                parameters: new GpuDrawParameters(
                    3,
                    1
                )
            );
            graphics.EndRenderPass(
                commandBufferHandle: draw
            );
            graphics.EndCommandBuffer(
                commandBufferHandle: draw
            );
            commands.Add(item: draw);
            var post = m_post[slot];

            recorder.BeginCommandBuffer(
                commandBufferHandle: post.CommandBufferHandle
            );
            recorder.TransitionImageLayout(
                post.CommandBufferHandle,
                target.ImageHandle,
                GpuImageLayout.ShaderReadOnly,
                m_outputLayout,
                GpuComputeAccess.ColorAttachmentWrite,
                GpuComputeAccess.ShaderRead,
                GpuComputeStage.ColorAttachmentOutput,
                GpuComputeStage.ComputeShader | GpuComputeStage.FragmentShader
            );
            recorder.EndCommandBuffer(
                commandBufferHandle: post.CommandBufferHandle
            );
            commands.Add(item: post.CommandBufferHandle);
            m_targetInitialized[slot] = true;
        }
    }
}
