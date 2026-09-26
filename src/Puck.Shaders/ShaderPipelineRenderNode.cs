using System.Text.Json;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Shaders;

/// <summary>Describes a host-owned same-device image supplied to a pipeline.</summary>
public readonly record struct ShaderPipelineExternalImage(
    nint ImageHandle,
    nint ImageViewHandle,
    uint Width,
    uint Height,
    GpuPixelFormat Format,
    GpuImageLayout Layout = GpuImageLayout.ShaderReadOnly);
/// <summary>
/// One render node for an ordered multi-pass shader graph. All passes are recorded before one queue submission;
/// frame-slot fences protect command buffers and descriptors, and every inter-pass image transition is explicit.
/// A compiled candidate's pipelines and shader modules are built on the thread pool, never on the frame thread; its
/// resources are allocated and it installs at the first frame boundary after. The graph it replaces retires once the
/// node's latest submission has completed, at once when it already has; the images behind the two most recently
/// published surfaces are held until a newer publication displaces them and the node's second submission after that
/// has completed. An install neither waits for a build nor drains the device.
/// </summary>
public sealed partial class ShaderPipelineRenderNode : IRenderNode, ICaptureRequestTarget {
    private readonly NodeDescriptor m_descriptor;
    private readonly IGpuDeviceContext m_device;
    private readonly bool m_directX;
    private readonly GpuDeviceServices m_gpu;
    private readonly uint m_inFlight;
    private readonly GpuImageLayout m_outputLayout;

    // The layout the published image is in between submissions: the output layout, or an external input's own when a
    // package that drew nothing publishes it in its output's place.
    private GpuImageLayout m_publishedLayout;

    private readonly FrameSlot[] m_slots;

    private ulong m_allocationBytes;
    private bool m_disposed;
    private ulong m_frame;
    private uint m_height;
    // Whether the installed graph has rendered no frame since it installed. A paused instance installs a replacement
    // without rendering, so until that graph renders, the replaced graph's last image stays published.
    private bool m_installedUnrendered;
    private Surface m_lastSurface;
    private Exception? m_lastSwapError;
    // Whether a device loss destroyed the published image. Nothing is published then, but no initialization frame is
    // owed: a paused node presents nothing, and refuses a capture, until its next step, resume or reset renders.
    private bool m_publicationLost;
    private bool m_outputRefreshRequested;
    private CompiledShaderPipeline? m_pending;
    private CompiledShaderPipeline? m_pipeline;
    private FloatPreviewPass? m_preview;
    private IGpuSurfaceReadback? m_readback;
    private bool m_ready;
    private uint m_requestedHeight;
    private uint m_requestedWidth;
    private bool m_resizePending;
    private string? m_selectedOutput;
    private int m_steps;
    private uint m_width;

    private readonly Dictionary<string, ShaderPipelineExternalImage> m_externalImages = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, IGpuBuffer> m_externalBuffers = new(comparer: StringComparer.Ordinal);
    private RuntimeResource[] m_resources = [];
    private IReadOnlyDictionary<string, RuntimeResource> m_resourceLookup = new Dictionary<string, RuntimeResource>(comparer: StringComparer.Ordinal);
    private RuntimePass[] m_passes = [];

    // The installed graph's frame group layout and region, null when no pass binds groups, and the region while it waits
    // for the grouped pass that owns it to install.
    private ShaderPipelineParameterLayout? m_frameLayout;
    private GpuRegion? m_frameRegion;
    private GpuRegion? m_frameRegionOwner;

    private readonly CaptureRequestSlot m_capture = new();
    private readonly CapturePngWriter m_capturePng = new();
    private bool m_initializationPending = true;
    private string[] m_passLabels = [];
    private readonly List<nint> m_commands = [];

    /// <summary>Creates an initially empty node that records through <paramref name="deviceContext"/>'s services. The
    /// first valid <see cref="Swap"/> installs a graph. A graph's package passes are recorded by
    /// <paramref name="packages"/>' recorders, one per pass, created when the graph installs and disposed with it; a
    /// node without recorders refuses a graph that has any.</summary>
    public ShaderPipelineRenderNode(string name, IGpuDeviceContext deviceContext, bool hostsOnDirectX, uint width, uint height, uint inFlightFrames = 3, GpuImageLayout outputLayout = GpuImageLayout.General, RenderGraphPackageRecorders? packages = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(deviceContext);
        ArgumentOutOfRangeException.ThrowIfZero(width);
        ArgumentOutOfRangeException.ThrowIfZero(height);
        ArgumentOutOfRangeException.ThrowIfZero(inFlightFrames);
        m_descriptor = new NodeDescriptor(
            Name: name,
            SurfaceId: SurfaceId.New()
        );
        m_work = new GpuWorkLedger(
            framesInFlight: ((int)inFlightFrames),
            name: "gpu.shader-pipeline"
        );
        m_gpu = GpuWorkCounting.Wrap(
            ledger: m_work,
            services: deviceContext.Services
        );
        m_device = deviceContext;
        m_directX = hostsOnDirectX;
        m_inFlight = inFlightFrames;
        if (outputLayout is not GpuImageLayout.General and not GpuImageLayout.ShaderReadOnly) {
            throw new ArgumentOutOfRangeException(
                nameof(outputLayout),
                outputLayout,
                "Pipeline outputs must be General or ShaderReadOnly."
            );
        }
        m_outputLayout = outputLayout;
        m_publishedLayout = outputLayout;
        m_packages = (packages ?? new RenderGraphPackageRecorders());
        m_slots = new FrameSlot[inFlightFrames];
        for (var i = 0; (i < m_slots.Length); i++) { m_slots[i] = new FrameSlot(); }
        m_width = width;
        m_height = height;
        m_requestedWidth = width;
        m_requestedHeight = height;
    }
    /// <summary>Creates a node with an already compiled candidate.</summary>
    public ShaderPipelineRenderNode(CompiledShaderPipeline pipeline, IGpuDeviceContext deviceContext, bool hostsOnDirectX, uint width, uint height, uint inFlightFrames = 3, GpuImageLayout outputLayout = GpuImageLayout.General)
        : this(
        pipeline.Plan.Definition.Name,
        deviceContext,
        hostsOnDirectX,
        width,
        height,
        inFlightFrames,
        outputLayout
    ) => Swap(pipeline: pipeline);

    /// <summary>Gets the bytes the installed graph owns: every frame slot's resources and its float preview. It is the
    /// installed graph's <see cref="ShaderPipelineMemoryAccount.SteadyBytes"/>.</summary>
    public ulong AllocationBytes => checked((m_allocationBytes + PreviewBytes(
        extent: ((m_preview is { } preview)
            ? (preview.Width, preview.Height)
            : null
        ),
        inFlight: m_inFlight
    )));
    /// <inheritdoc/>
    public NodeDescriptor Descriptor => m_descriptor;
    /// <summary>Gets the frame extent, in pixels, the installed graph's frame-relative resources were built for.</summary>
    public (uint Width, uint Height) Extent => (m_width, m_height);
    /// <summary>Gets whether a compiled candidate is queued and not yet installed.</summary>
    public bool HasPendingCandidate => (m_pending is not null);
    /// <summary>Gets the frame extent, in pixels, most recently requested through <see cref="Resize"/>. It differs from
    /// <see cref="Extent"/> while a resize builds, paused or running, and after a resize whose candidate was
    /// refused.</summary>
    public (uint Width, uint Height) RequestedExtent => (m_requestedWidth, m_requestedHeight);
    /// <summary>Gets the submitted frame count.</summary>
    public ulong FrameCounter => m_frame;
    /// <summary>Gets or sets the frame values the host supplies to every pass's frame block.</summary>
    public ShaderFrameValues Frame { get; set; }
    /// <summary>Gets whether a compiled graph has allocated all of its GPU resources.</summary>
    public bool IsReady => ((m_pipeline is not null) && m_ready);
    /// <summary>Gets the last deferred candidate creation failure, if any.</summary>
    public Exception? LastSwapError => m_lastSwapError;
    /// <summary>Gets the number of passes in the installed graph.</summary>
    public int PassCount => m_passes.Length;
    /// <summary>Gets the installed graph's pass names, in execution order: the labels its work counts are reported under.</summary>
    public ReadOnlySpan<string> PassLabels => m_passLabels;
    /// <summary>Gets or sets whether rendering is paused.</summary>
    public bool Paused { get; set; }
    /// <summary>Gets or sets whether every pass's frame block holds its echo sentinels
    /// (<see cref="ShaderInterfaceEcho.WriteSentinels"/>) in place of the frame values and config, so an echo pass can
    /// hold what it reads to what was written.</summary>
    public bool Sentinels { get; set; }
    /// <inheritdoc/>
    public string? PendingCapturePath => m_capture.PendingPath;
    /// <summary>Gets the active immutable execution plan.</summary>
    public ShaderPipelinePlan? Plan => m_pipeline?.Plan;
    /// <summary>Gets the layout the published image is in between the node's submissions: the node's output layout, or
    /// an external input's own layout while a package pass that drew nothing
    /// (<see cref="RenderGraphPackageOutcome.DrewNothing"/>) publishes that input in its output's place.</summary>
    public GpuImageLayout PublishedLayout => m_publishedLayout;
    /// <summary>Gets resource allocation and extent information for the active graph.</summary>
    public IReadOnlyList<ShaderPipelineResourceStatus> ResourceStatus => m_resources.Select(selector: StatusOf).ToArray();
    /// <summary>Gets or sets whether a step is pending.</summary>
    public bool StepRequested {
        get => (m_steps != 0); set => m_steps = (value
        ? Math.Max(
            val1: 1,
            val2: m_steps
        )
        : 0
    );
    }

    // A fullscreen pass records its barriers in their own command buffer before its render pass; the render pass leaves
    // its color attachment shader-readable, which the plan already accounts for, so nothing follows it.
    private void RecordPreBarriers(RuntimePass pass, int slot, nint command, List<nint> commands) {
        var recorder = m_gpu.Recorder;

        recorder.BeginCommandBuffer(
            commandBufferHandle: command
        );
        InitializeResources(
            command: command,
            recorder: recorder,
            slot: slot
        );
        RecordAccesses(
            command: command,
            pass: pass,
            recorder: recorder,
            slot: slot
        );
        recorder.EndCommandBuffer(
            commandBufferHandle: command
        );
        commands.Add(item: command);
    }
    // Puts one pass of a finished build into service: takes its modules, pipeline and render pass from the build, then
    // allocates what the frame thread owns — the geometry buffer, the framebuffer binding each frame slot's attachments,
    // and the per-slot descriptor and command objects, its sets from the graph's one descriptor pool.
    private void InstallPass(ShaderPipelinePlannedPass planned, IReadOnlyDictionary<string, RuntimeResource> map, GraphBuild built, IReadOnlyDictionary<int, CarriedHistory> carried, GpuDescriptorPoolSizes? graphPool, ref nint descriptorPool) {
        var declaration = planned.Declaration;
        var runtime = new RuntimePass(
            planned,
            m_pipeline!.Shaders.GetValueOrDefault(key: planned.Name),
            ((int)m_inFlight),
            built.Passes[planned.Index]!.Extent
        );

        m_passes[planned.Index] = runtime;

        if (declaration is not null) {
            runtime.PortBindings = PortBindingsOf(planned: planned);
            runtime.FrameSets = new nint[m_inFlight];
        }
        if (m_frameRegionOwner is { } frameRegion) {
            runtime.FrameRegion = frameRegion;
            m_frameRegionOwner = null;
        }

        var objects = built.TakePass(index: planned.Index);

        runtime.Compute = objects.Compute;
        runtime.Graphics = objects.Graphics;
        runtime.Primary = objects.Primary;
        runtime.RenderPass = objects.RenderPass;
        runtime.Secondary = objects.Secondary;

        if (declaration is null or { Kind: ShaderPipelineDocumentPassKind.Compute }) {
            runtime.Pools = new IGpuCommandPool[m_inFlight];
        } else {
            // Geometry buffers are created through the device's buffer factory, not the node's counted one, so they count
            // as no created storage buffer and no written bytes.
            if (declaration.Geometry is { } geometry) {
                runtime.GeometryBuffer = m_device.Services.BufferFactory.CreateHostVisible(
                    data: geometry.BufferData(),
                    name: new GpuObjectName(
                        detail: "geometry",
                        owner: m_descriptor.Name,
                        part: runtime.Name
                    ),
                    usage: GpuBufferUsage.Vertex | GpuBufferUsage.Index
                );
            } else if (declaration.Vertex == ShaderPipelineVertexInput.Position) {
                runtime.GeometryBuffer = m_device.Services.BufferFactory.CreateHostVisible(
                    data: FullscreenTriangle.CreateVertexData(),
                    name: new GpuObjectName(
                        detail: "geometry",
                        owner: m_descriptor.Name,
                        part: runtime.Name
                    ),
                    usage: GpuBufferUsage.Vertex
                );
            }
            runtime.Pre = new IGpuCommandPool[m_inFlight];
            runtime.Draw = new IGpuCommandPool[m_inFlight];
            runtime.Framebuffers = new IGpuFramebuffer[m_inFlight];

            // A framebuffer owns no image, so one over history the install carries binds the replaced graph's instances,
            // which move into this graph only once nothing else can fail.
            IGpuImage[] ImagesOf(ShaderPipelineAttachment attachment) {
                var resource = map[attachment.Version];

                return (carried.TryGetValue(
                    key: resource.Storage.Index,
                    value: out var carry
                )
                    ? carry.Old.Images
                    : resource.Images)!;
            }
            var colors = planned.Attachments.Where(predicate: static attachment => !attachment.Depth).Select(selector: ImagesOf).ToArray();
            var depth = ((planned.Attachments.FirstOrDefault(predicate: static attachment => attachment.Depth) is { } depthAttachment)
                ? ImagesOf(attachment: depthAttachment)
                : null);

            for (var slot = 0; (slot < m_inFlight); slot++) {
                runtime.Framebuffers[slot] = m_gpu.RenderPassFactory.CreateFramebuffer(
                    runtime.RenderPass!,
                    [.. colors.Select(selector: images => images[slot])],
                    depth?[slot]
                );
            }
        }
        runtime.Sets = new nint[m_inFlight];
        runtime.Samplers = new nint[m_inFlight];
        try {
            AllocateSlotObjects(
                descriptorPool: ref descriptorPool,
                graphPool: graphPool,
                pass: runtime
            );
            InstallPackage(
                descriptorPool: descriptorPool,
                objects: objects,
                planned: planned,
                runtime: runtime
            );
        } catch {
            // A package's build belongs to no pass until its recorder takes it.
            objects.PackageBuilt?.Dispose();
            objects.PackageBuilt = null;

            throw;
        }
    }
    // A capture armed after a selection reads that selection: while its float preview builds, the published image is still
    // the previous selection's, so the capture waits for the frame that publishes the new one.
    private void CaptureIfPending() {
        if (m_previewRequest is not null) {
            return;
        }

        m_capture.Serve(
            failureLabel: "[capture] failed",
            writer: (m_captureWriter ??= path => {
                m_capturePng.ThrowIfUnavailable(path: path);
                if (
                    m_lastSurface.IsEmpty ||
                    !m_lastSurface.IsSameDeviceImage
                ) {
                    throw new InvalidOperationException(message: "A completed same-device output is required for capture.");
                }
                var format = m_lastSurface.Format switch {
                    SurfaceFormat.R8G8B8A8Unorm => GpuPixelFormat.R8G8B8A8Unorm,
                    SurfaceFormat.B8G8R8A8Unorm => GpuPixelFormat.B8G8R8A8Unorm,
                    _ => throw new NotSupportedException(message: $"Capture does not support surface format {m_lastSurface.Format}.")
                };

                m_readback ??= m_gpu.SurfaceTransferFactory.CreateReadback();
                var sourceLayout = m_publishedLayout;

                // The readback sizes its staging buffer to the surface it reads, replacing one of another size.
                m_readbackBytes = ReadbackBytes(
                    height: m_lastSurface.Height,
                    width: m_lastSurface.Width
                );
                var pixels = m_readback.Read(
                    m_lastSurface.ImageHandle,
                    format,
                    m_lastSurface.Width,
                    m_lastSurface.Height,
                    4,
                    sourceLayout
                );

                if (!m_capturePng.TryWrite(
                    height: ((int)m_lastSurface.Height),
                    path: path,
                    rgba: pixels,
                    width: ((int)m_lastSurface.Width)
                )) {
                    throw new NotSupportedException(message: "PNG capture is unavailable.");
                }

                Console.Error.WriteLine(value: $"[capture] {m_descriptor.Name} -> {path}");
            })
        );
    }
    // Whether history built for the installed graph's frame extent carries into a candidate planned at another unchanged:
    // same declaration shape, and, for an image, the same resolved extent, each resolved against the frame extent it was
    // built for.
    private static bool CompatibleHistory(ShaderPipelineResource old, ShaderPipelineResource current, (uint Width, uint Height) previousExtent, (uint Width, uint Height) extent) {
        if (
            (old.Kind != current.Kind) ||
            !string.Equals(
            a: old.Format,
            b: current.Format,
            comparisonType: StringComparison.OrdinalIgnoreCase
        ) ||
            (old.History != current.History) ||
            (
                (current.Kind == ShaderPipelineResourceKind.Image) &&
                ((old.Dimensions?.Resolve(
                    frameHeight: previousExtent.Height,
                    frameWidth: previousExtent.Width
                ) ?? previousExtent) != (current.Dimensions?.Resolve(
                    frameHeight: extent.Height,
                    frameWidth: extent.Width
                ) ?? extent))
            ) ||
            (old.SizeBytes != current.SizeBytes) ||
            (old.Initialization != current.Initialization)
        ) {
            return false;
        }
        return true;
    }
    private void DisposeGraph(RuntimePass[] passes, RuntimeResource[] resources) {
        foreach (var pass in passes) {
            if (pass is not null) {
                pass.Dispose(
                    device: m_device,
                    gpu: m_gpu
                );
            }
        }
        foreach (var resource in resources) {
            if (resource is not null) {
                resource.Dispose();
            }
        }
    }
    // Allocates the installed pipeline's resources on the frame thread and takes the built pipeline and module set into
    // them. The budget was checked against the plan before the build started. A storage whose instances carry over from
    // the replaced graph gets none of its own; PreserveCompatibleHistory moves the carried ones in.
    private void Allocate(GraphBuild built, IReadOnlyDictionary<int, CarriedHistory> carried) {
        var plan = m_pipeline!.Plan;
        var map = new Dictionary<string, RuntimeResource>(comparer: StringComparer.Ordinal);
        FloatPreviewPass? preview = null;

        var storages = new RuntimeResource[plan.Storages.Count];

        try {
            foreach (var planned in plan.Storages) {
                var declaration = planned.Declaration;
                var resource = new RuntimeResource(
                    count: ((int)m_inFlight),
                    storage: planned
                );

                storages[planned.Index] = resource;
                foreach (var version in planned.Versions) {
                    map.Add(
                        key: version,
                        value: resource
                    );
                }
                if (carried.ContainsKey(key: planned.Index)) {
                    continue;
                }
                if (
                    (declaration.Kind != ShaderPipelineResourceKind.Buffer) &&
                    !declaration.IsExternal
                ) {
                    resource.Images = new IGpuImage[m_inFlight];
                    var extent = (declaration.Dimensions?.Resolve(
                        frameHeight: m_height,
                        frameWidth: m_width
                    ) ?? (m_width, m_height));
                    var usage = UsageOf(
                        plan: plan,
                        storage: planned
                    );

                    for (var i = 0; (i < m_inFlight); i++) {
                        resource.Images[i] = m_gpu.ImageFactory.Create(
                            ParseFormat(format: declaration.Format),
                            extent.Width,
                            extent.Height,
                            usage,
                            name: new GpuObjectName(
                                index: i,
                                owner: m_descriptor.Name,
                                part: declaration.Name
                            )
                        );
                    }
                } else if (
                    (declaration.Kind == ShaderPipelineResourceKind.Buffer) &&
                    !declaration.IsExternal
                ) {
                    if (
                        !declaration.SizeBytes.HasValue ||
                        (declaration.SizeBytes.Value == 0)
                    ) {
                        throw new InvalidDataException(message: $"Buffer '{declaration.Name}' has no positive size.");
                    }
                    var sizeBytes = declaration.SizeBytes.Value;

                    resource.Buffers = new IGpuBuffer[m_inFlight];
                    for (var i = 0; (i < m_inFlight); i++) {
                        resource.Buffers[i] = m_gpu.BufferFactory.CreateDeviceLocal(
                            name: new GpuObjectName(
                                index: i,
                                owner: m_descriptor.Name,
                                part: declaration.Name
                            ),
                            sizeBytes: sizeBytes,
                            usage: GpuBufferUsage.Storage
                        );
                    }
                }
            }
            m_allocationBytes = GraphBytes(
                extent: (m_width, m_height),
                inFlight: m_inFlight,
                plan: plan
            );
            m_resourceLookup = map;
            m_resources = storages;
            m_passLabels = plan.Passes.Select(selector: static pass => pass.Name).ToArray();
            m_passes = new RuntimePass[plan.Passes.Count];

            var graphPool = GraphDescriptorPool(
                inFlight: m_inFlight,
                plan: plan
            );
            var descriptorPool = ((nint)0);

            // The frame group block every pass binds at set 0, held by the graph's leading pass.
            m_frameLayout = plan.Passes.FirstOrDefault()?.Parameters;
            m_frameRegion = ((m_frameLayout is { } frameLayout)
                ? new GpuRegion(
                    bindings: m_gpu.Bindings,
                    buffers: m_gpu.BufferFactory,
                    byteCount: UniformBytes(blockBytes: frameLayout.FrameBlockSizeBytes),
                    copyPipeline: null,
                    memory: GpuResidency.RingMemory(profile: m_device.MemoryProfile),
                    name: new GpuObjectName(
                        owner: m_descriptor.Name,
                        part: "frame block"
                    ),
                    policy: GpuResidencyPolicy.Ring,
                    recorder: m_gpu.Recorder,
                    slotCount: ((int)m_inFlight),
                    usage: GpuBufferUsage.Uniform
                )
                : null);
            m_frameRegionOwner = m_frameRegion;

            for (var i = 0; (i < plan.Passes.Count); i++) {
                InstallPass(
                    built: built,
                    carried: carried,
                    descriptorPool: ref descriptorPool,
                    graphPool: graphPool,
                    map: map,
                    planned: plan.Passes[i]
                );
            }
            // The preview the selected output publishes through is part of the graph: a float or external selection
            // was built with its targets and pipelines, and gets its descriptors and command pools here, before the
            // installed graph retires.
            var selected = (map.TryGetValue(
                key: (m_selectedOutput ?? plan.DefaultOutput),
                value: out var chosen
            )
                ? chosen
                : map[plan.DefaultOutput]
            );

            if (NeedsPreview(spec: selected.Spec)) {
                var objects = (built.Preview ?? throw new InvalidOperationException(message: "The candidate was built without the float preview its selected output needs."));

                built.Preview = null;
                preview = CreatePreview(objects: objects);
            }
            for (var index = 0; (index < m_slots.Length); index++) {
                var slot = m_slots[index];

                slot.Fence ??= m_gpu.QueueSubmitter.CreateSubmissionFence();
                slot.Final ??= m_gpu.CommandPoolFactory.Create(name: new GpuObjectName(
                    index: index,
                    owner: m_descriptor.Name,
                    part: "final"
                ));
            }
            m_preview = preview;
            m_initializationPending = true;
            m_ready = true;
        } catch {
            preview?.Dispose();
            DisposeGraph(
                passes: m_passes,
                resources: m_resources
            );
            // A frame region no grouped pass took yet is the node's alone to dispose.
            m_frameRegionOwner?.Dispose();
            m_frameRegionOwner = null;
            m_frameRegion = null;
            m_frameLayout = null;
            if (m_resources.Length == 0) {
                foreach (var resource in storages) {
                    resource?.Dispose();
                }
            }
            m_passes = [];
            m_resources = [];
            m_resourceLookup = new Dictionary<string, RuntimeResource>(comparer: StringComparer.Ordinal);
            m_allocationBytes = 0;
            throw;
        }
    }
    private void FinalizeOutputs(int slot, List<nint> commands) {
        var command = m_slots[slot].Final!.CommandBufferHandle;
        var recorder = m_gpu.Recorder;

        recorder.BeginCommandBuffer(
            commandBufferHandle: command
        );
        RecordPresentation(
            command: command,
            recorder: recorder,
            selected: m_resourceLookup[(m_selectedOutput ?? m_pipeline!.Plan.DefaultOutput)],
            slot: slot
        );
        recorder.EndCommandBuffer(
            commandBufferHandle: command
        );
        commands.Add(item: command);
    }
    // Writes this frame's ports into the pass group's set for the slot. The sets, samplers and constant buffers were
    // allocated with the graph, from its one pool (AllocateSlotObjects).
    private void WritePorts(RuntimePass pass, int slot) {
        var set = pass.Sets![slot];

        var ports = pass.PortBindings!;
        var port = 0;

        foreach (var input in pass.Inputs) {
            var resource = m_resourceLookup[input.Name];
            var index = HistoryIndex(
                resource,
                slot,
                input.PreviousFrame
            );
            var binding = ports[port].Binding;

            port++;

            if (resource.Spec.Kind == ShaderPipelineResourceKind.Image) {
                var view = ResolveImage(
                    resource,
                    input.Name,
                    index
                ).ImageViewHandle;

                m_gpu.Bindings.WriteSampledImage(
                    arrayElement: 0,
                    binding: binding,
                    descriptorSetHandle: set,
                    imageViewHandle: view
                );
            } else {
                // Pipeline buffers are raw (ByteAddressBuffer), so the element stride is zero.
                m_gpu.Bindings.WriteBuffer(
                    binding: binding,
                    bufferHandle: ResolveBuffer(
                        resource,
                        input.Name,
                        index
                    ).BufferHandle,
                    bufferSize: (resource.Spec.SizeBytes ?? 0),
                    descriptorSetHandle: set,
                    elementStride: 0,
                    kind: GpuBindingKind.ReadOnlyBuffer
                );
            }
        }
        if (pass.Kind != ShaderPipelinePassKind.Compute) {
            return;
        }
        foreach (var output in pass.Outputs) {
            var resource = m_resourceLookup[output.Name];
            var binding = ports[port++].Binding;

            if (resource.Spec.Kind == ShaderPipelineResourceKind.Image) {
                m_gpu.Bindings.WriteStorageImage(
                    arrayElement: 0,
                    binding: binding,
                    descriptorSetHandle: set,
                    imageViewHandle: ResolveImage(
                        resource,
                        output.Name,
                        slot
                    ).ImageViewHandle
                );
            } else {
                m_gpu.Bindings.WriteBuffer(
                    binding: binding,
                    bufferHandle: ResolveBuffer(
                        resource,
                        output.Name,
                        slot
                    ).BufferHandle,
                    bufferSize: (resource.Spec.SizeBytes ?? 0),
                    descriptorSetHandle: set,
                    elementStride: 0,
                    kind: GpuBindingKind.ReadWriteBuffer
                );
            }
        }
    }
    private static int HistoryIndex(RuntimeResource resource, int slot, bool previous) {
        if (!previous) {
            return slot;
        }
        if (!resource.History) {
            throw new InvalidDataException(message: $"Resource '{resource.Spec.Name}' is not declared as history.");
        }
        return (((slot + resource.Count) - 1) % resource.Count);
    }
    // Installs a finished build beside the installed graph: allocates its resources, carries compatible history and live
    // parameters over, and retires the replaced graph once its readers' submissions complete. An allocation
    // failure leaves the installed graph, its extent, its preview and its feedback images intact, and releases exactly
    // what the candidate created.
    private void Install(GraphBuild built, BuildKey key) {
        var next = key.Pipeline!;

        // A candidate's descriptor pools are admitted into the device's heaps before it allocates anything; one that
        // does not fit is refused by name, the installed graph keeps presenting, and nothing grows.
        if (
            key.Candidate &&
            !m_gpu.Bindings.CanAdmit(
                owner: $"shader pipeline {m_descriptor.Name}",
                pools: DescriptorPools(
                    inFlight: m_inFlight,
                    plan: next.Plan,
                    preview: key.Preview.HasValue
                ),
                refusal: out var refusal
            )
        ) {
            built.Dispose();
            Refuse(
                candidate: true,
                error: new InvalidDataException(message: $"{refusal}; the installed graph keeps running.")
            );

            return;
        }

        var previousPipeline = m_pipeline;
        var previousResources = m_resources;
        var previousLookup = m_resourceLookup;
        var previousAllocation = m_allocationBytes;
        var previousPasses = m_passes;
        var previousLabels = m_passLabels;
        var previousReady = m_ready;
        var previousPreview = m_preview;
        var previousSelectedOutput = m_selectedOutput;
        var nextSelectedOutput = DesiredSelection;
        var previousExtent = (m_width, m_height);
        var previousFrameLayout = m_frameLayout;
        var previousFrameRegion = m_frameRegion;
        var hadFences = m_slots.Any(predicate: static slot => (slot.Fence is not null));
        var carried = CarriedHistoryOf(
            extent: (key.Width, key.Height),
            plan: next.Plan
        );

        m_pipeline = next;
        m_width = key.Width;
        m_height = key.Height;
        m_selectedOutput = (IsDeclaredImageOutput(
            plan: next.Plan,
            resourceName: nextSelectedOutput
        )
            ? nextSelectedOutput
            : null
        );
        m_preview = null;
        m_resources = [];
        m_resourceLookup = new Dictionary<string, RuntimeResource>(comparer: StringComparer.Ordinal);
        m_passes = [];
        m_ready = false;
        try {
            Allocate(
                built: built,
                carried: carried
            );
            PreserveCompatibleHistory(
                carried: carried,
                previousPlan: previousPipeline?.Plan
            );
            PreserveLiveParameters(
                next: m_passes,
                previous: previousPasses
            );
            SeedPassRegions();
            // What an install counts belongs to no submission, whether it installs, fails partway or rebuilds after a
            // device loss.
            m_work.Discard();
            m_installedUnrendered = true;
            // A rebuild of the installed pipeline after a device loss keeps its revision: the loss already withdrew the
            // sample, and the passes are the ones it had.
            if (key.Candidate) {
                m_pending = null;
                m_resizePending = false;
                ConfigureWork();
            }
        } catch (Exception error) {
            m_work.Discard();
            m_preview?.Dispose();
            DisposeGraph(
                passes: m_passes,
                resources: m_resources
            );
            built.Dispose();
            m_pipeline = previousPipeline;
            m_resources = previousResources;
            m_resourceLookup = previousLookup;
            m_passes = previousPasses;
            m_passLabels = previousLabels;
            m_ready = previousReady;
            m_allocationBytes = previousAllocation;
            m_preview = previousPreview;
            m_selectedOutput = previousSelectedOutput;
            (m_width, m_height) = previousExtent;
            m_frameLayout = previousFrameLayout;
            m_frameRegion = previousFrameRegion;
            if (!hadFences) {
                foreach (var slot in m_slots) { slot.Fence?.Dispose(); slot.Fence = null; slot.Final?.Dispose(); slot.Final = null; }
            }
            if (!key.Candidate) {
                throw;
            }
            Refuse(
                candidate: true,
                error: error
            );

            return;
        }
        // The graph installed with the desired selection's preview, so no separate preview is wanted; a preview build
        // still running for it is disposed when it is taken.
        m_previewRequest = null;
        Retire(
            passes: previousPasses,
            preview: previousPreview,
            resources: previousResources
        );
    }
    private static bool IsDeclaredImageOutput(ShaderPipelinePlan plan, string? resourceName) {
        if (resourceName is null) {
            return false;
        }
        return (plan.FindResource(name: resourceName) is { Declaration.Kind: ShaderPipelineResourceKind.Image, IsConsumed: false });
    }
    private static bool IsFloatFormat(GpuPixelFormat format) => (format is GpuPixelFormat.R16G16B16A16Float or GpuPixelFormat.R32G32B32A32Float);
    private static bool NeedsPreview(ShaderPipelineResource spec) => ((spec.Kind == ShaderPipelineResourceKind.Image) && (spec.IsExternal || IsFloatFormat(format: ParseFormat(format: spec.Format))));
    private Surface Output(int slot) {
        var selectedName = (m_selectedOutput ?? m_pipeline!.Plan.DefaultOutput);
        var selectedResource = m_resourceLookup[selectedName];

        // A buffer output publishes no image; a graph instance's consumers bind the buffer itself (LatestOutputBuffer).
        if (selectedResource.Spec.Kind == ShaderPipelineResourceKind.Buffer) {
            return default;
        }
        if (
            (selectedResource.Alias.Target is null) &&
            NeedsPreview(spec: selectedResource.Spec)
        ) {
            var target = (m_preview?.GetTarget(slot: slot) ?? throw new InvalidOperationException(message: "The float preview target is not ready."));

            return Surface.SameDeviceImage(
                target.ImageHandle,
                target.ImageViewHandle,
                target.Width,
                target.Height,
                SurfaceFormat.R8G8B8A8Unorm
            );
        }
        var (published, publishedName, instance) = PublicationOf(
            selected: PresentationResource(selected: selectedResource),
            slot: slot
        );
        var resolved = ResolveImage(
            index: instance,
            name: publishedName,
            resource: published
        );
        var imageHandle = resolved.ImageHandle;
        var imageView = resolved.ImageViewHandle;
        var width = resolved.Width;
        var height = resolved.Height;
        var format = resolved.Format;

        if (format == GpuPixelFormat.R8G8B8A8Unorm) {
            return Surface.SameDeviceImage(
                format: SurfaceFormat.R8G8B8A8Unorm,
                height: height,
                imageHandle: imageHandle,
                imageViewHandle: imageView,
                width: width
            );
        }
        if (format == GpuPixelFormat.B8G8R8A8Unorm) {
            return Surface.SameDeviceImage(
                format: SurfaceFormat.B8G8R8A8Unorm,
                height: height,
                imageHandle: imageHandle,
                imageViewHandle: imageView,
                width: width
            );
        }
        throw new InvalidDataException(message: "The selected output must use an RGBA8 format.");
    }

    // The format of the image a node publishes for an image output: a float or external output through the RGBA8
    // float preview, any other in its own format (Output).
    internal static GpuPixelFormat PublishedFormat(ShaderPipelineResource output) => (NeedsPreview(spec: output)
        ? GpuPixelFormat.R8G8B8A8Unorm
        : ParseFormat(format: output.Format));

    /// <summary>Parses a resource declaration's format, as every graph image is created and bound by.</summary>
    /// <param name="format">The declared format's name, such as <c>R8G8B8A8Unorm</c>, in any case.</param>
    /// <returns>The format.</returns>
    /// <exception cref="InvalidDataException"><paramref name="format"/> names no <see cref="GpuPixelFormat"/>.</exception>
    public static GpuPixelFormat ParseFormat(string? format) {
        if (Enum.TryParse<GpuPixelFormat>(
            ignoreCase: true,
            result: out var parsed,
            value: format
        )) {
            return parsed;
        }
        throw new InvalidDataException(message: $"Unknown shader pipeline format '{format}'.");
    }

    private void PresentSelectedOutput() {
        WaitAll();
        HoldLeases();
        var slot = ((int)((m_frame - 1) % m_inFlight));
        var selected = m_resourceLookup[(m_selectedOutput ?? m_pipeline!.Plan.DefaultOutput)];
        var commands = m_commands;

        commands.Clear();
        if (NeedsPreview(spec: selected.Spec)) {
            m_preview!.Record(
                PreviewSource(
                    selected: selected,
                    slot: slot
                ),
                ResolveImage(
                    selected,
                    selected.Spec.Name,
                    slot
                ),
                slot,
                commands
            );
        }
        FinalizeOutputs(
            commands: commands,
            slot: slot
        );
        SubmitCounted(
            commands: commands,
            fence: m_slots[slot].Fence!
        );
        m_frameLeases.MoveTo(destination: m_slots[slot].Leases);
        Publish(surface: Output(slot: slot));
        m_outputRefreshRequested = false;
    }
    private RuntimeResource PresentationResource(RuntimeResource selected) {
        var selectedFormat = ParseFormat(format: selected.Spec.Format);

        if (
            (selectedFormat == GpuPixelFormat.R8G8B8A8Unorm) ||
            (selectedFormat == GpuPixelFormat.B8G8R8A8Unorm)
        ) {
            return selected;
        }
        throw new InvalidDataException(message: $"Selected output '{selected.Spec.Name}' must be RGBA8 or a float image with preview conversion.");
    }
    // Moves the carried history's instances out of the replaced graph into the installed one. The carried instances keep
    // their contents and the states the replaced graph left them in.
    private void PreserveCompatibleHistory(IReadOnlyDictionary<int, CarriedHistory> carried, ShaderPipelinePlan? previousPlan) {
        foreach (var (index, carry) in carried) {
            var current = m_resources[index];
            var old = carry.Old;

            Array.Copy(
                destinationArray: current.Initialized,
                length: current.Count,
                sourceArray: old.Initialized
            );
            CarryStates(
                current: current,
                old: old,
                oldPlan: previousPlan!
            );
            if (old.Images is not null) {
                current.Images = old.Images;
                old.Images = null;
            } else {
                current.Buffers = old.Buffers;
                old.Buffers = null;
            }
        }
    }
    private static void PreserveLiveParameters(RuntimePass[] previous, RuntimePass[] next) {
        var oldByName = previous.ToDictionary(
            pass => pass.Name,
            StringComparer.Ordinal
        );

        foreach (var current in next) {
            if (
                !oldByName.TryGetValue(
                key: current.Name,
                value: out var old
            ) ||
                (old.ParametersLayout.SizeBytes != current.ParametersLayout.SizeBytes) ||
                (old.ParametersLayout.Slots.Count != current.ParametersLayout.Slots.Count)
            ) {
                continue;
            }
            var compatible = true;

            for (var i = 0; (i < current.ParametersLayout.Slots.Count); i++) {
                var oldSlot = old.ParametersLayout.Slots[i];
                var currentSlot = current.ParametersLayout.Slots[i];

                if (
                    (oldSlot.Name != currentSlot.Name) ||
                    (oldSlot.Type != currentSlot.Type) ||
                    (oldSlot.Offset != currentSlot.Offset)
                ) {
                    compatible = false;
                    break;
                }
            }
            if (
                compatible &&
                System.Text.Json.Nodes.JsonNode.DeepEquals(
                node1: old.ParametersLayout.JsonSchema(),
                node2: current.ParametersLayout.JsonSchema()
            )
            ) {
                current.Parameters = new ShaderPipelineParameterValues(
                    old.Parameters.Config,
                    old.Parameters.Bytes.ToArray()
                );
            }
        }
    }
    private void Record(RuntimePass pass, int slot, in FrameContext context, List<nint> commands) {
        if (pass.Package is not null) {
            RecordPackage(
                commands: commands,
                context: in context,
                pass: pass,
                slot: slot
            );
            return;
        }
        WritePorts(
            pass: pass,
            slot: slot
        );

        var spec = pass.Spec!;

        if (pass.Kind == ShaderPipelinePassKind.Compute) {
            var handle = pass.Pools![slot].CommandBufferHandle;
            var recorder = m_gpu.Recorder;

            recorder.BeginCommandBuffer(
                commandBufferHandle: handle
            );
            InitializeResources(
                command: handle,
                recorder: recorder,
                slot: slot
            );
            RecordAccesses(
                command: handle,
                pass: pass,
                recorder: recorder,
                slot: slot
            );
            recorder.BindPipeline(
                bindPoint: GpuBindPoint.Compute,
                commandBufferHandle: handle,
                pipelineHandle: pass.Compute!.Handle
            );
            var extent = (pass.Width, pass.Height);

            BindGroupSets(
                bindPoint: GpuBindPoint.Compute,
                command: handle,
                layout: pass.Compute.LayoutHandle,
                pass: pass,
                slot: slot
            );
            recorder.Dispatch(
                commandBufferHandle: handle,
                groupCountX: (((extent.Width + spec.GroupSizeX) - 1) / spec.GroupSizeX),
                groupCountY: (((extent.Height + spec.GroupSizeY) - 1) / spec.GroupSizeY),
                groupCountZ: (((1u + spec.GroupSizeZ) - 1) / spec.GroupSizeZ)
            );
            recorder.EndCommandBuffer(
                commandBufferHandle: handle
            );
            commands.Add(item: handle);
            return;
        }

        RecordPreBarriers(
            command: pass.Pre![slot].CommandBufferHandle,
            commands: commands,
            pass: pass,
            slot: slot
        );
        var framebuffer = pass.Framebuffers![slot];
        var command = pass.Draw![slot].CommandBufferHandle;
        var recorderGraphics = m_gpu.Recorder;
        var pipeline = pass.Graphics!;

        recorderGraphics.BeginCommandBuffer(
            commandBufferHandle: command
        );
        recorderGraphics.BeginRenderPass(
            command,
            framebuffer
        );
        recorderGraphics.BindPipeline(
            bindPoint: GpuBindPoint.Graphics,
            commandBufferHandle: command,
            pipelineHandle: pipeline.Handle
        );
        var geometry = spec.Geometry;

        if (pass.GeometryBuffer is { } buffer) {
            recorderGraphics.BindVertexBuffer(
                command,
                buffer.BufferHandle,
                (geometry?.VertexBytes ?? buffer.SizeBytes),
                (geometry?.StrideBytes ?? FullscreenTriangle.StrideBytes)
            );
            if (geometry is not null) {
                recorderGraphics.BindIndexBuffer(
                    command,
                    buffer.BufferHandle,
                    geometry.VertexBytes,
                    (geometry.SizeBytes - geometry.VertexBytes),
                    ((geometry.IndexFormat == ShaderPipelineIndexFormat.UInt32)
                        ? GpuIndexFormat.UInt32
                        : GpuIndexFormat.UInt16)
                );
            }
        }
        BindGroupSets(
            bindPoint: GpuBindPoint.Graphics,
            command: command,
            layout: pipeline.LayoutHandle,
            pass: pass,
            slot: slot
        );
        if (geometry is not null) {
            recorderGraphics.DrawIndexed(
                commandBufferHandle: command,
                indexCount: ((uint)geometry.Indices.Count)
            );
        } else {
            recorderGraphics.Draw(
                commandBufferHandle: command,
                parameters: new GpuDrawParameters(
                    3,
                    1
                )
            );
        }
        recorderGraphics.EndRenderPass(
            commandBufferHandle: command
        );
        recorderGraphics.EndCommandBuffer(
            commandBufferHandle: command
        );
        commands.Add(item: command);
    }
    private void Release(bool wait) {
        // A build in flight creates objects on the device being released, so it is waited out and discarded first.
        m_build.CancelAndWait(discard: static built => built.Dispose());
        m_buildKey = default;
        CancelPreviewBuild();
        // A node that never allocated submitted nothing, so it drains nothing and touches no device context.
        if (
            wait &&
            HoldsDeviceObjects
        ) {
            WaitAll();
            m_device.WaitIdle();
        }
        DisposeGraph(
            passes: m_passes,
            resources: m_resources
        );
        ReleaseRetired();
        RetireAllLeases();
        // The published images were the released graph's or held from one, so nothing stays published.
        m_lastSurface = default;
        m_previousSurface = default;
        m_preview?.Dispose();
        m_preview = null;
        m_readback?.Dispose();
        m_readback = null;
        m_readbackBytes = 0UL;
        foreach (var slot in m_slots) {
            slot.Final?.Dispose();
            slot.Final = null;
            slot.Fence?.Dispose();
            slot.Fence = null;
        }
        m_passes = [];
        m_resources = [];
        m_resourceLookup = new Dictionary<string, RuntimeResource>(comparer: StringComparer.Ordinal);
        m_allocationBytes = 0;
        m_passLabels = [];
        m_frameLayout = null;
        m_frameRegion = null;
        m_ready = false;
        m_work.Invalidate();
    }
    private IGpuBuffer ResolveBuffer(RuntimeResource resource, string name, int index) {
        if (resource.Buffers is not null) {
            return resource.Buffers[index];
        }
        if (m_externalBuffers.TryGetValue(
            key: name,
            value: out var buffer
        )) {
            return buffer;
        }
        throw new InvalidDataException(message: $"External buffer '{name}' is not bound.");
    }
    private ShaderPipelineExternalImage ResolveImage(RuntimeResource resource, string name, int index) {
        if (
            resource.Spec.IsExternal &&
            m_externalImages.TryGetValue(
            key: name,
            value: out var external
        )
        ) {
            return external;
        }
        if (resource.Images is not null) {
            var image = resource.Images[index];

            return new ShaderPipelineExternalImage(
                image.ImageHandle,
                image.ImageViewHandle,
                image.Width,
                image.Height,
                ParseFormat(format: resource.Spec.Format),
                GpuImageLayout.Undefined
            );
        }
        throw new InvalidDataException(message: $"External image '{name}' is not bound.");
    }
    private void ValidateExternalBinding(string name, ShaderPipelineResourceKind kind) {
        var plan = (m_pending?.Plan ?? m_pipeline?.Plan);

        if (plan is null) {
            return;
        }
        // An indexed loop, not a predicate or an interface enumerator: a graph instance binds its inputs on every frame,
        // and either would allocate.
        var resources = plan.Resources;

        for (var index = 0; (index < resources.Count); index++) {
            var resource = resources[index];

            if (
                string.Equals(
                    a: resource.Name,
                    b: name,
                    comparisonType: StringComparison.Ordinal
                ) &&
                resource.Declaration.IsExternal &&
                (resource.Declaration.Kind == kind)
            ) {
                return;
            }
        }

        throw new ArgumentException(
            message: $"Resource '{name}' is not a declared external {kind} in the candidate graph.",
            paramName: nameof(name)
        );
    }
    private void ValidateExternalResources() {
        foreach (var resource in m_resources) {
            var declaration = resource.Spec;

            if (!declaration.IsExternal) {
                continue;
            }
            if (declaration.Kind == ShaderPipelineResourceKind.Image) {
                if (!m_externalImages.TryGetValue(
                    key: declaration.Name,
                    value: out var image
                )) {
                    throw new InvalidDataException(message: $"External image '{declaration.Name}' has not been bound.");
                }
                // A bound image carries its binder's extent, such as another graph instance's output rendered at its own
                // footprint's extent: a pass samples it whole, and its declared dimensions only size the passes that
                // resolve their extent from it.
                if (image.Format != ParseFormat(format: declaration.Format)) {
                    throw new InvalidDataException(message: $"External image '{declaration.Name}' has format {image.Format}; expected {declaration.Format}.");
                }
            } else if (!m_externalBuffers.TryGetValue(
                key: declaration.Name,
                value: out var buffer
            )) {
                throw new InvalidDataException(message: $"External buffer '{declaration.Name}' has not been bound.");
            } else if (buffer.SizeBytes < declaration.SizeBytes.GetValueOrDefault()) {
                throw new InvalidDataException(message: $"External buffer '{declaration.Name}' is {buffer.SizeBytes} bytes; expected at least {declaration.SizeBytes}.");
            }
        }
    }
    private static void ValidatePlan(ShaderPipelinePlan plan) {
        if (
            (plan.Passes.Count == 0) ||
            (plan.Resources.Count == 0)
        ) {
            throw new InvalidDataException(message: "A shader pipeline needs resources and passes.");
        }
        var resources = plan.Resources.ToDictionary(
            item => item.Name,
            StringComparer.Ordinal
        );

        foreach (var pass in plan.Passes.Where(predicate: static pass => (pass.Declaration?.IsGraphics == true))) {
            if (pass.Declaration!.InputReferences.Any(predicate: input => (resources[input.Name].Declaration.Kind == ShaderPipelineResourceKind.Buffer))) {
                throw new InvalidDataException(message: $"Graphics pass '{pass.Name}' cannot consume a storage buffer through the graphics binding contract.");
            }
        }
    }
    private void WaitAll() {
        foreach (var slot in m_slots) {
            slot.Fence?.Wait();
            slot.Leases.RetireAll();
        }
    }

    /// <summary>Binds a host-owned buffer for a named external resource. The node never disposes it.</summary>
    public void BindBuffer(string name, IGpuBuffer buffer) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(buffer);
        ValidateExternalBinding(
            kind: ShaderPipelineResourceKind.Buffer,
            name: name
        );
        m_externalBuffers[name] = buffer;
    }
    /// <summary>Binds a host-owned image for a named external resource. The node never disposes it. The image must have
    /// the declared format and may have any extent: a pass samples it whole.</summary>
    public void BindImage(string name, ShaderPipelineExternalImage image) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfZero(image.ImageHandle);
        ArgumentOutOfRangeException.ThrowIfZero(image.ImageViewHandle);
        ValidateExternalBinding(
            kind: ShaderPipelineResourceKind.Image,
            name: name
        );
        ClearLease(name: name);
        m_externalImages[name] = image;
    }
    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }
        m_disposed = true;
        m_capture.Refuse(error: new ObjectDisposedException(objectName: nameof(ShaderPipelineRenderNode)));
        Release(wait: true);
    }
    /// <inheritdoc/>
    /// <remarks>The published image is destroyed with the device, and a capture armed at the loss is refused
    /// (<see cref="CaptureRequestSlot.RefuseForDeviceLoss"/>). A paused node presents nothing after the rebuild, and a
    /// capture of it reports that no completed output exists, until a step, resume or reset renders.</remarks>
    public void OnDeviceLost() {
        Release(wait: false);
        m_publicationLost = true;
        m_capture.RefuseForDeviceLoss();
    }
    /// <inheritdoc/>
    /// <remarks>A lease bound for this frame that no submission of it samples is retired before this returns.</remarks>
    public Surface ProduceFrame(in FrameContext context) {
        try {
            return Produce(context: in context);
        } finally {
            ReleaseUnheldLeases();
        }
    }

    private Surface Produce(in FrameContext context) {
        if (m_disposed) {
            return default;
        }
        // Publishes the newest finished submission's counts, paused or not, so a held frame still completes them.
        m_work.Poll();
        // A node with nothing published (never rendered, or reset) owes an initialization frame. That frame is its own
        // obligation: a step requested before it renders stays pending and advances one submission beyond it. A device
        // loss unpublishes the image without owing one.
        var published = (!m_lastSurface.IsEmpty || m_publicationLost);
        var stepping = (
            Paused &&
            (m_steps != 0) &&
            published
        );

        RetireCompleted();
        // A reload, a row upsert or a resize replaces the graph; it is not a step, so a paused frame builds and installs
        // it too, rendering nothing.
        EnsureBuild();
        InstallPending();
        InstallPendingPreview();
        // Nothing is installed yet: the first graph, or the installed pipeline rebuilt after a device loss, is building.
        if (
            (m_pipeline is null) ||
            !m_ready
        ) {
            return default;
        }
        // A step renders the candidate it was requested after, so it waits while that candidate builds.
        if (
            stepping &&
            ((m_pending is not null) || m_resizePending)
        ) {
            if (m_frame != 0) {
                CaptureIfPending();
            }
            return m_lastSurface;
        }
        // A paused frame with no step owed presents the last image. A selection re-publishes the last rendered frame,
        // so on a graph that has not rendered since a paused install it waits for that graph's first frame. After a device
        // loss the last image is empty, and a capture served here reports that no completed output exists.
        if (
            Paused &&
            (m_steps == 0) &&
            m_ready &&
            published &&
            (!m_outputRefreshRequested || m_installedUnrendered)
        ) {
            if (m_frame != 0) {
                CaptureIfPending();
            }
            return m_lastSurface;
        }
        if (
            Paused &&
            (m_steps == 0) &&
            m_ready &&
            m_outputRefreshRequested &&
            (m_frame != 0)
        ) {
            PresentSelectedOutput();
            CaptureIfPending();
            return m_lastSurface;
        }
        if (stepping) {
            m_steps--;
        }
        ValidateExternalResources();
        ValidateLeases();
        var selectedResource = m_resourceLookup[(m_selectedOutput ?? m_pipeline.Plan.DefaultOutput)];
        var slotIndex = ((int)(m_frame % m_inFlight));
        var slot = m_slots[slotIndex];

        slot.Fence!.Wait();
        slot.Leases.RetireAll();
        HoldLeases();
        var commands = m_commands;

        commands.Clear();
        RecordPasses(
            commands: commands,
            context: context,
            slot: slotIndex
        );
        if (NeedsPreview(spec: selectedResource.Spec)) {
            m_preview!.Record(
                PreviewSource(
                    selected: selectedResource,
                    slot: slotIndex
                ),
                ResolveImage(
                    selectedResource,
                    selectedResource.Spec.Name,
                    slotIndex
                ),
                slotIndex,
                commands
            );
        }
        FinalizeOutputs(
            commands: commands,
            slot: slotIndex
        );
        if (commands.Count == 0) {
            return m_lastSurface;
        }
        SubmitCounted(
            commands: commands,
            fence: slot.Fence!
        );
        m_frameLeases.MoveTo(destination: slot.Leases);
        Publish(surface: Output(slot: slotIndex));
        m_outputRefreshRequested = false;
        m_installedUnrendered = false;
        m_frame++;
        CaptureIfPending();
        return m_lastSurface;
    }

    /// <summary>Requests a capture of the next completed RGBA8 output frame.</summary>
    public void RequestCapture(FrameCaptureRequest request) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        m_capture.Arm(
            request,
            PendingCapturePath
        );
    }
    /// <summary>Clears all history state and resets the presentation counter. Submissions in flight complete first; the
    /// completed work sample is then withdrawn, and <see cref="ResetSubmission"/> marks where counting since this reset
    /// begins. Every slot holds each pass's current block again, so what the first frame after a reset uploads
    /// never depends on how many frames ran before it.</summary>
    public void Reset() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        WaitAll();
        DiscardInstances();
        m_frame = 0;
        m_steps = 0;
        m_outputRefreshRequested = false;
        Publish(surface: default);
        SeedPassRegions();
        ResetWork();
    }
    /// <summary>Requests a new frame extent. The pipeline is rebuilt at the new extent as a candidate beside the
    /// installed graph, exactly like a reload: its pipelines build on the thread pool while the installed graph keeps
    /// producing at its current extent, and it installs at the first frame boundary after. History whose resolved
    /// extent is unchanged carries over with its contents, history whose extent changes starts again from its declared
    /// initialization, and the old graph retires like any replaced graph. A resize replaces the graph and is not a step:
    /// a paused instance builds and installs it too, renders nothing, and keeps publishing its last image until its next
    /// step, resume or reset. A refused candidate leaves the installed graph at its old extent, records
    /// <see cref="LastSwapError"/>, and is not retried until a different extent is
    /// requested. Requesting the extent already requested does nothing.</summary>
    /// <param name="width">The requested frame width, in pixels.</param>
    /// <param name="height">The requested frame height, in pixels.</param>
    /// <exception cref="ObjectDisposedException">The node is disposed.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="height"/> is zero.</exception>
    public void Resize(uint width, uint height) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        ArgumentOutOfRangeException.ThrowIfZero(width);
        ArgumentOutOfRangeException.ThrowIfZero(height);
        if (
            (width == m_requestedWidth) &&
            (height == m_requestedHeight)
        ) {
            return;
        }
        m_requestedWidth = width;
        m_requestedHeight = height;
        if (
            !m_ready ||
            (m_pipeline is null)
        ) {
            // Nothing is allocated, so the next graph built is simply built at the new extent.
            m_width = width;
            m_height = height;
            m_resizePending = false;
            return;
        }
        m_resizePending = (
            (width != m_width) ||
            (height != m_height)
        );
    }
    /// <summary>Publishes a declared image output by name. A float or external output is published through a float
    /// preview, which is allocated here for an installed graph (or built with the graph that installs next), so a frame
    /// never creates one; the previous preview retires like a replaced graph. A selection is neither a replacement nor a
    /// step: a paused instance re-publishes its last rendered frame through the new selection without rendering, and on
    /// a graph installed while paused that has not rendered yet, the selection is published with that graph's first
    /// frame.</summary>
    /// <param name="name">The name of a live image version: a public output or an intermediate one.</param>
    /// <exception cref="ObjectDisposedException">The node is disposed.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> names no live image version, or names a version its
    /// successor overwrites within the frame, whose contents are discarded before publication.</exception>
    /// <exception cref="InvalidOperationException">No pipeline is installed or queued, or the preview for the selection
    /// could not be allocated or would take the node's owned bytes past <see cref="BudgetBytes"/>; the previous selection
    /// stays published.</exception>
    public void SelectOutput(string name) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var pipeline = (m_pipeline ?? (m_pending ?? throw new InvalidOperationException(message: "No shader pipeline is installed.")));
        var resourceName = name;
        var version = pipeline.Plan.FindResource(name: name);

        if (version is not { Declaration.Kind: ShaderPipelineResourceKind.Image }) {
            throw new ArgumentException(
                message: $"Output {name} is not a declared image resource.",
                paramName: nameof(name)
            );
        }
        if (version.IsConsumed) {
            throw new ArgumentException(
                message: $"Output {name} is overwritten by '{version.Successor}' within the frame, so its contents are discarded before publication.",
                paramName: nameof(name)
            );
        }
        if (
            m_ready &&
            ReferenceEquals(
            objA: pipeline,
            objB: m_pipeline
        ) &&
            !RequestPreview(
            name: resourceName,
            selected: m_resourceLookup[resourceName]
        )
        ) {
            return;
        }
        m_selectedOutput = resourceName;
        m_outputRefreshRequested = true;
    }
    /// <summary>Requests one render while <see cref="Paused"/>. The initialization frame a paused node owes after a
    /// <see cref="Reset"/> never consumes a step, so a step requested before that frame renders one frame beyond it. A
    /// step taken while a candidate or resize builds waits until it installs, then renders once through it.</summary>
    public void Step() => m_steps = checked((m_steps + 1));
    /// <summary>Queues an atomic compiled candidate. The next produced frame starts building its pipelines and shader
    /// modules on the thread pool, so a resize or selection requested before that frame is built with it. The installed
    /// graph keeps presenting until the build finishes; the candidate's resources are then allocated and it installs at
    /// that frame boundary. A swap replaces the graph and is not a step: a paused instance builds and installs the
    /// candidate too, without rendering or consuming a step, and keeps publishing the replaced graph's last image until
    /// its next step, resume or reset renders the candidate. A newer candidate replaces a queued one; one whose build is
    /// already running is discarded when that build is taken.</summary>
    public void Swap(CompiledShaderPipeline pipeline) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        ArgumentNullException.ThrowIfNull(pipeline);
        if (!pipeline.IsSuccess) {
            throw new InvalidDataException(message: "A failed shader compilation cannot be installed.");
        }
        ValidatePlan(plan: pipeline.Plan);
        ValidatePackages(plan: pipeline.Plan);
        if (
            (m_inFlight < 2) &&
            pipeline.Plan.Resources.Any(predicate: static resource => resource.Declaration.History)
        ) {
            throw new InvalidDataException(message: "History requires at least two frame slots.");
        }
        m_lastSwapError = null;
        m_pending = pipeline;
    }
    /// <summary>Copies one pass's live packed parameter block for inspection or persistence.</summary>
    public bool TryGetConfigSnapshot(string passName, out byte[] bytes) {
        ArgumentException.ThrowIfNullOrWhiteSpace(passName);
        var pass = m_passes.FirstOrDefault(predicate: item => (item.Name == passName));

        if (pass is null) {
            bytes = [];
            return false;
        }
        bytes = pass.Parameters.Bytes.ToArray();
        return true;
    }
    /// <summary>Rebinds a complete JSON object to one pass's authored parameter schema.</summary>
    public bool TrySetConfig(string passName, JsonElement? config, out string reason) {
        ArgumentException.ThrowIfNullOrWhiteSpace(passName);
        if (
            (m_pipeline is null) ||
            !m_ready
        ) {
            reason = "The shader pipeline has not allocated its GPU resources yet.";
            return false;
        }
        var pass = m_passes.FirstOrDefault(predicate: item => (item.Name == passName));

        if (pass is null) {
            reason = $"Unknown shader pass '{passName}'.";
            return false;
        }
        if (!pass.ParametersLayout.TryBind(
            config: config,
            reason: out reason,
            values: out var values
        )) {
            return false;
        }
        pass.Parameters = values;
        return true;
    }
}
