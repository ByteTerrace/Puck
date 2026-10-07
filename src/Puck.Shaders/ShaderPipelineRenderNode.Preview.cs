using System.Runtime.ExceptionServices;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Shaders;

// The preview pass: publishes a selected external image through the display encode's SDR, into an RGBA8 image the node
// owns.
public sealed partial class ShaderPipelineRenderNode {
    private readonly BackgroundBuild<PreviewObjects> m_previewBuild = new();

    private PreviewRequest? m_previewBuilding;
    private PreviewRequest? m_previewRequest;

    /// <summary>Gets whether a selection's preview is being built on the thread pool. The current selection stays
    /// published meanwhile; the new one takes effect at the first frame boundary after the build finishes.</summary>
    public bool IsBuildingPreview => (m_previewBuild.IsPending && !m_previewBuild.IsCompleted);

    /// <summary>Returns the preview's one descriptor pool: a pass-group set per in-flight frame, each holding the
    /// display encode's group (<see cref="DisplayEncodeLayout"/>).</summary>
    /// <param name="inFlight">The node's frames in flight.</param>
    /// <returns>The pool's sizes.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="inFlight"/> is zero.</exception>
    public static GpuDescriptorPoolSizes PreviewDescriptorPool(uint inFlight) {
        ArgumentOutOfRangeException.ThrowIfZero(value: inFlight);

        var sizes = default(GpuDescriptorPoolSizes);

        for (var slot = 0u; (slot < inFlight); slot++) {
            sizes += GpuDescriptorPoolSizes.ForGroups(groups: DisplayEncodeLayout.Layout.Groups);
        }

        return sizes;
    }

    // Puts built preview objects into service with the descriptor and command objects the frame thread owns.
    private PreviewPass CreatePreview(PreviewObjects objects) => new(
        device: m_device,
        gpu: m_gpu,
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
    // everything it owns, or whose descriptor pool the device's heaps cannot admit, is refused here, before anything is
    // created.
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
                message: $"The preview for '{selected.Spec.Name}' is refused: {refusal.Message}"
            );
        }
        if (!m_gpu.Bindings.CanAdmit(
            owner: $"preview for '{selected.Spec.Name}'",
            pools: [PreviewDescriptorPool(inFlight: m_inFlight)],
            refusal: out var descriptorRefusal
        )) {
            throw new InvalidOperationException(message: $"The preview for '{selected.Spec.Name}' is refused: {descriptorRefusal}");
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
        var inFlight = m_inFlight;
        var pipelines = m_pipelines;

        m_previewBuilding = request;
        var owner = m_name;

        m_previewBuild.Start(build: token => PreviewObjects.CreateAsync(
            cancellationToken: token,
            device: device,
            owner: owner,
            gpu: gpu,
            pipelines: pipelines,
            directX: directX,
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

        PreviewPass next;

        try {
            if (error is not null) {
                ExceptionDispatchInfo.Throw(source: error);
            }
            // Its descriptors and command pools are the frame thread's; a failure there disposes the built objects too.
            next = CreatePreview(objects: built!);
        } catch (Exception failure) {
            m_lastSwapError = new InvalidOperationException(
                innerException: failure,
                message: $"The preview for '{request.Name}' could not be allocated: {failure.Message}"
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

    // A selection whose preview is building: the output it selects and the preview's extent.
    private sealed record PreviewRequest(string Name, uint Width, uint Height);
    /// <summary>
    /// The preview's pipeline and targets: its lease on the pass-pipeline cache's entry for the display encode's
    /// deployed bytecode (two shader modules, the render pass it draws in and the graphics pipeline created for it), and per frame
    /// slot the RGBA8 image it draws into with the framebuffer that binds it. An install builds it on the thread pool with
    /// the rest of the candidate. Each object is stored as soon as it exists, so a failure partway releases exactly what
    /// was taken.
    /// </summary>
    private sealed class PreviewObjects : IDisposable {
        // The usages of a preview image: drawn into, then published and sampled downstream, in General as a storage image.
        private const GpuImageUsage TargetUsage = GpuImageUsage.ColorAttachment | GpuImageUsage.Sampled | GpuImageUsage.Storage;

        private PreviewObjects(string owner, uint width, uint height, uint inFlight) {
            Owner = owner;
            Width = width;
            Height = height;
            Framebuffers = new IGpuFramebuffer[inFlight];
            Targets = new IGpuImage[inFlight];
        }

        // The owning instance's name, which every preview object's debug name starts with.
        public string Owner { get; }
        public IGpuFramebuffer[] Framebuffers { get; }
        public uint Height { get; }
        public GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline>? Lease { get; private set; }
        public IGpuPipeline? Pipeline => Lease?.Current?.Graphics;
        public IGpuImage[] Targets { get; }
        public uint Width { get; }

        public static async Task<PreviewObjects> CreateAsync(GpuDeviceServices gpu, IGpuDeviceContext device, GpuPassPipelineCache pipelines, bool directX, uint width, uint height, uint inFlight, string owner, CancellationToken cancellationToken) {
            var objects = new PreviewObjects(
                height: height,
                inFlight: inFlight,
                owner: owner,
                width: width
            );

            try {
                objects.Lease = pipelines.Acquire(
                    device: device,
                    key: SurfaceEncoder.Key(
                        directX: directX,
                        renderPass: SurfaceEncoder.CaptureRenderPass
                    )
                );

                var renderPass = (await objects.Lease.WaitAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false)).RenderPass!;

                for (var i = 0; (i < inFlight); i++) {
                    objects.Targets[i] = gpu.ImageFactory.Create(
                        format: SurfaceEncoder.CaptureFormat,
                        name: new GpuObjectName(
                            index: ((int)i),
                            owner: owner,
                            part: "preview"
                        ),
                        height: height,
                        usage: TargetUsage,
                        width: width
                    );
                    objects.Framebuffers[i] = gpu.RenderPassFactory.CreateFramebuffer(
                        renderPass,
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
            foreach (var target in Targets) { target?.Dispose(); }
            Lease?.Release();
            Lease = null;
        }
    }
    /// <summary>
    /// Draws a selected external image through the display encode's SDR into RGBA8 targets, one per frame slot. It owns
    /// built <see cref="PreviewObjects"/>, and its constructor creates the rest on the frame thread — its encode block,
    /// and per slot a descriptor pool, set and sampler, and the pre-barrier, draw and post-barrier command pools — so recording a frame creates
    /// nothing. Each
    /// object is stored as soon as it exists, so a failure partway disposes exactly what was created, the built objects
    /// included.
    /// </summary>
    private sealed class PreviewPass : IDisposable {
        private const GpuAccess PriorAccess = GpuAccess.ShaderRead | GpuAccess.ShaderWrite | GpuAccess.TransferWrite | GpuAccess.ColorAttachmentWrite;
        private const GpuStage PriorStages = GpuStage.ComputeShader | GpuStage.FragmentShader | GpuStage.Transfer | GpuStage.ColorAttachmentOutput;

        private readonly nint[] m_descriptorSets;
        private readonly IGpuDeviceContext m_device;
        private readonly GpuDeviceServices m_gpu;
        private readonly PreviewObjects m_objects;
        private readonly GpuImageLayout m_outputLayout;
        private readonly IGpuPipeline m_pipeline;
        private readonly nint[] m_samplers;
        private readonly bool[] m_targetInitialized;
        private readonly IGpuImage[] m_targets;

        private IGpuStorageBuffer? m_block;
        private nint m_descriptorPool;

        public PreviewPass(PreviewObjects objects, GpuDeviceServices gpu, IGpuDeviceContext device, uint inFlight, GpuImageLayout outputLayout) {
            m_objects = objects;
            m_gpu = gpu;
            m_device = device;
            m_outputLayout = outputLayout;
            m_targets = objects.Targets;
            m_pipeline = objects.Pipeline!;
            m_descriptorSets = new nint[inFlight];
            m_samplers = new nint[inFlight];
            m_targetInitialized = new bool[inFlight];
            try {
                var bindings = gpu.Bindings;

                m_block = SurfaceEncoder.CreateBlock(
                    gpu: gpu,
                    name: new GpuObjectName(
                        owner: objects.Owner,
                        part: "preview-block"
                    ),
                    output: DisplayOutput.Sdr(format: SurfaceEncoder.CaptureFormat),
                    paperWhiteNits: DisplayOutput.SdrWhiteNits
                );
                m_descriptorPool = bindings.CreatePool(
                    name: new GpuObjectName(
                        owner: objects.Owner,
                        part: "preview"
                    ),
                    sizes: PreviewDescriptorPool(inFlight: inFlight)
                );
                for (var i = 0; (i < inFlight); i++) {
                    m_descriptorSets[i] = bindings.AllocateSet(
                        m_descriptorPool,
                        m_pipeline.GroupLayoutHandles[((int)PassGroup)],
                        name: new GpuObjectName(
                            index: ((int)i),
                            owner: objects.Owner,
                            part: "preview"
                        )
                    );
                    m_samplers[i] = bindings.CreateSampler();
                    bindings.WriteSampler(
                        arrayElement: 0,
                        binding: DisplayEncodeLayout.SamplerBinding,
                        descriptorSetHandle: m_descriptorSets[i],
                        samplerHandle: m_samplers[i]
                    );
                    bindings.WriteConstantBuffer(
                        arrayElement: 0,
                        binding: DisplayEncodeLayout.BlockBinding,
                        bufferHandle: m_block.BufferHandle,
                        bufferSize: m_block.SizeBytes,
                        descriptorSetHandle: m_descriptorSets[i]
                    );
                }
            } catch { Dispose(); throw; }
        }

        public uint Height => m_objects.Height;
        public uint Width => m_objects.Width;

        public void Dispose() {
            m_objects.Dispose();
            foreach (var sampler in m_samplers) {
                if (sampler != 0) {
                    m_gpu.Bindings.DestroySampler(
                        samplerHandle: sampler
                    );
                }
            }
            m_gpu.Bindings.DestroyPool(poolHandle: m_descriptorPool);
            m_descriptorPool = 0;
            m_block?.Dispose();
            m_block = null;
        }
        public IGpuImage GetTarget(int slot) => m_targets[slot];
        // The bytes of the targets the preview still owns and of its encode block.
        public ulong LiveBytes() {
            var bytes = (m_block?.SizeBytes ?? 0UL);

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
        // Records the draw of one slot into the frame's list. sourceBarrier is the node's barrier before the source is
        // sampled; the preview's own targets are private to it and are tracked here.
        public void Record(ShaderPipelineBarrier sourceBarrier, ShaderPipelineExternalImage image, int slot, nint command) {
            var sourceImageHandle = image.ImageHandle;
            var sourceImageView = image.ImageViewHandle;

            m_gpu.Bindings.WriteSampledImage(
                arrayElement: 0,
                binding: DisplayEncodeLayout.SourceImageBinding,
                descriptorSetHandle: m_descriptorSets[slot],
                imageViewHandle: sourceImageView
            );
            var recorder = m_gpu.Recorder;

            if (sourceBarrier.Kind == ShaderPipelineBarrierKind.Image) {
                recorder.TransitionImageLayout(
                    command,
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
                    command,
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
                command,
                target.ImageHandle,
                targetOld,
                GpuImageLayout.RenderTarget,
                ((targetOld == GpuImageLayout.Undefined)
                ? GpuAccess.None
                : PriorAccess),
                GpuAccess.ColorAttachmentWrite,
                ((targetOld == GpuImageLayout.Undefined)
                ? GpuStage.TopOfPipe
                : PriorStages),
                GpuStage.ColorAttachmentOutput
            );
            recorder.BeginRenderPass(
                command,
                m_objects.Framebuffers[slot]
            );
            recorder.BindPipeline(
                bindPoint: GpuBindPoint.Graphics,
                commandBufferHandle: command,
                pipelineHandle: m_pipeline.Handle
            );
            recorder.BindDescriptorSet(
                bindPoint: GpuBindPoint.Graphics,
                commandBufferHandle: command,
                descriptorSetHandle: m_descriptorSets[slot],
                group: PassGroup,
                pipelineLayoutHandle: m_pipeline.LayoutHandle
            );
            recorder.Draw(
                commandBufferHandle: command,
                parameters: new GpuDrawParameters(
                    3,
                    1
                )
            );
            recorder.EndRenderPass(
                commandBufferHandle: command
            );
            recorder.TransitionImageLayout(
                command,
                target.ImageHandle,
                GpuImageLayout.ShaderReadOnly,
                m_outputLayout,
                GpuAccess.ColorAttachmentWrite,
                GpuAccess.ShaderRead,
                GpuStage.ColorAttachmentOutput,
                GpuStage.ComputeShader | GpuStage.FragmentShader
            );
            m_targetInitialized[slot] = true;
        }
    }
}
