using System.Runtime.ExceptionServices;
using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders;

// A candidate graph in two halves, the way SdfWorldPipelineSource builds the SDF engine's pipelines. Its pipelines —
// every pass's compute or graphics pipeline with its shader modules and the render pass it is created for, and the float
// preview's — are leased from the pass-pipeline cache on the thread pool through BackgroundBuild, which a pass another
// node or an earlier install already leases joins without creating anything, so a cold driver cache delays the install
// instead of freezing the pump. Its resources — images and buffers, the geometry buffer, framebuffers, descriptor pools,
// sets and samplers, command pools and fences — are allocated on the frame thread when the finished set is taken, and
// the graph installs there. Until then every frame presents the installed graph.
//
// A build starts at a frame boundary, never in Swap, Resize or SelectOutput: like SdfWorldPipelineSource.Poll, the
// produced frame starts it, so every request made between two frames — a compiled candidate, the host's resize of the
// slot showing it, a selection — is built once, together. One build runs at a time. A request that changes what should
// be built while a build runs does not cancel it: the finished set no longer matches, so it is discarded when it is
// taken and the next build starts. A device loss or disposal waits a build out and discards it, because it creates
// objects on the device being released. The replaced graph is never drained on the frame thread; see
// ShaderPipelineRenderNode.Retirement.cs.
//
// A refused candidate is tried again only when something it was refused on changes, as SdfWorldPipelineSource retries
// a refused build: the operator's GPU faults (GpuCreationFaults.Revision: an arm, a disarm, or a fault firing
// elsewhere, never the one that refused it, since the revision is read after the refusal), and, for a candidate the
// device's descriptor heap refused (GpuDescriptorHeapRefusalException), the heap's release revision
// (IGpuBindings.HeapReleaseRevision), which moves as another owner returns its pools. A frame that changes neither tries
// nothing, so a persistent refusal is attempted once per change, never once per frame and never on a clock. A new
// request, a swap or a resize, replaces the refused candidate.
public sealed partial class ShaderPipelineRenderNode {
    // A planned depth attachment as its render pass declares it, which its storage's images are also created for, so the
    // depth they are cleared to has one statement.
    internal static GpuDepthAttachment DepthAttachmentOf(ShaderPipelineAttachment attachment, GpuPixelFormat format) =>
        new(
            ClearDepth: attachment.ClearDepth,
            Format: format,
            Load: attachment.Load,
            Store: attachment.Store
        );

    // The depth attachment a depth storage's images are created for: the planned attachment of the graphics pass that
    // draws it, or, for one a package pass draws through its own render pass, a clear to the depth the storage declares,
    // which that render pass clears to as well.
    private static GpuDepthAttachment DepthOf(ShaderPipelinePlan plan, ShaderPipelinePlannedStorage storage, GpuPixelFormat format) {
        foreach (var pass in plan.Passes) {
            foreach (var attachment in pass.Attachments) {
                if (attachment.Depth && (attachment.Storage == storage.Index)) {
                    return DepthAttachmentOf(
                        attachment: attachment,
                        format: format
                    );
                }
            }
        }

        return new GpuDepthAttachment(
            ClearDepth: (storage.Declaration.ClearDepth ?? GpuDepthAttachment.DefaultClearDepth),
            Format: format,
            Load: GpuAttachmentLoad.Clear,
            Store: GpuAttachmentStore.Discard
        );
    }

    private readonly BackgroundBuild<GraphBuild> m_build = new();

    // The refused candidate: its pipeline, or null for a refused resize of the installed one, and the revisions it was
    // refused under.
    private bool m_hasRefusal;
    private bool m_refusedByHeap;
    private long m_refusedFaultsRevision;
    private long m_refusedHeapRevision;
    private CompiledShaderPipeline? m_refusedPending;
    private bool m_refusedResize;
    private BuildKey m_buildKey;
    // Each advance of the node's builds (a produced frame, or a region bind that advances them as one does) is one more,
    // and the advance the running build started in, so a build is taken only by a later advance.
    private long m_advances;

    private long m_buildAdvance = -1;

    /// <summary>Gets whether a candidate's pipelines and shader modules are being built on the thread pool. Frames
    /// produced meanwhile present the installed graph; the build installs at the first frame boundary after it finishes,
    /// never in the frame that started it, paused or running.</summary>
    public bool IsBuildingCandidate => (m_build.IsPending && !m_build.IsCompleted);

    // Every version name mapped to the declaration of the storage that holds it, which fixes its kind, format and extent.
    private static Dictionary<string, ShaderPipelineResource> VersionSpecs(ShaderPipelinePlan plan) {
        var specs = new Dictionary<string, ShaderPipelineResource>(comparer: StringComparer.Ordinal);

        foreach (var storage in plan.Storages) {
            foreach (var version in storage.Versions) {
                specs.Add(
                    key: version,
                    value: storage.Declaration
                );
            }
        }

        return specs;
    }
    // Starts building what the node should install next, unless a build is already running. A paused instance builds
    // too, since a replacement is not a step. A candidate whose replacement peak exceeds the budget, or whose selected
    // output cannot be resolved, is refused here, before anything is created. The installed pipeline rebuilt after a
    // device loss is not a replacement and is never refused by the budget: nothing else is owned then, and the graph it
    // restores was accepted when it installed.
    private void EnsureBuild() {
        RetryRefusal();
        if (
            m_disposed ||
            m_build.IsPending ||
            ResizeBacksOff ||
            !TryDesiredCandidate(
                candidate: out var candidate,
                extent: out var extent,
                pipeline: out var pipeline
            )
        ) {
            return;
        }

        BuildKey key;

        try {
            key = KeyFor(
                candidate: candidate,
                extent: extent,
                pipeline: pipeline
            );
            if (
                candidate &&
                (Account(
                    counts: key.Counts,
                    plan: pipeline.Plan,
                    preview: key.Preview,
                    rows: key.Rows
                ) is { Fits: false } account)
            ) {
                throw account.Refusal();
            }
        } catch (Exception error) {
            Refuse(
                candidate: candidate,
                error: error
            );

            return;
        }

        StartBuild(key: key);
    }
    // Takes a finished build and installs it, refuses it, or discards it when what should be built has changed since it
    // started. A failed rebuild of the installed pipeline after a device loss rethrows here, on the frame thread, so the
    // loss reaches the host's recovery; the next frame starts a fresh build. A build is never taken in the advance that
    // started it, however fast it finished (a candidate whose every pipeline the pass-pipeline cache already holds
    // finishes at once), so the frame an install lands in never depends on the thread pool's timing.
    private void InstallPending() {
        if (m_buildAdvance == m_advances) {
            return;
        }

        if (!m_build.TryTake(
            error: out var error,
            result: out var built
        )) {
            return;
        }

        var key = m_buildKey;

        m_buildKey = default;

        if (
            !TryDesiredKey(key: out var desired) ||
            !key.Matches(other: desired)
        ) {
            if (m_buildResizes) {
                m_resizeBackoff = Math.Clamp(value: (2 * m_resizeBackoff), min: 1, max: MaxResizeBackoff);
            }

            built?.Discard(later: DisposeLater);
            EnsureBuild();

            return;
        }

        m_resizeBackoff = 0;

        if (error is not null) {
            if (!key.Candidate) {
                ExceptionDispatchInfo.Throw(source: error);
            }

            Refuse(
                candidate: true,
                error: error
            );

            return;
        }

        Install(
            built: built!,
            key: key
        );
    }

    // The selection the next graph installs with: one still waiting for its own preview, else the published one.
    // A graph build carries the preview for it, and its install supersedes the waiting preview build.
    private string? DesiredSelection => (m_previewRequest?.Name ?? m_selectedOutput);

    // The preview extent the selected output of a graph planned at this extent needs, or null for one that publishes itself.
    private (uint Width, uint Height)? PreviewFor(ShaderPipelinePlan plan, (uint Width, uint Height) extent) {
        var desired = DesiredSelection;
        var selected = (IsDeclaredImageOutput(
            plan: plan,
            resourceName: desired
        )
            ? desired!
            : plan.DefaultOutput
        );
        var version = (plan.FindResource(name: selected) ?? throw new InvalidDataException(message: $"The shader pipeline publishes no output named '{selected}'."));
        var spec = plan.Storages[version.Storage].Declaration;
        var counts = CountsAt(extent: extent, plan: plan);

        return (NeedsPreview(spec: spec)
            ? (spec.Dimensions?.Resolve(
                frameHeight: extent.Height,
                frameWidth: extent.Width,
                renderWidth: counts.RenderWidth,
                renderHeight: counts.RenderHeight
            ) ?? extent)
            : null
        );
    }
    private void Refuse(bool candidate, Exception error) {
        if (candidate) {
            m_hasRefusal = ((m_pending is not null) || m_resizePending || m_rebindPending || m_recountPending);
            m_refusedByHeap = (error is GpuDescriptorHeapRefusalException);
            m_refusedFaultsRevision = FaultsRevision;
            m_refusedHeapRevision = m_device.Services.Bindings.HeapReleaseRevision;
            m_refusedPending = m_pending;
            m_refusedResize = (m_resizePending || m_rebindPending || m_recountPending);
            m_pending = null;
            m_resizePending = false;
            m_rebindPending = false;
            m_recountPending = false;
        }

        m_lastSwapError = error;
    }

    // The operator's GPU faults' revision, or zero on a device that passes its creations through none.
    private long FaultsRevision => (m_device.Services.Faults?.Revision ?? 0L);

    // Forgets the refused candidate, which a new request replaces.
    private void ForgetRefusal() {
        m_hasRefusal = false;
        m_refusedPending = null;
        m_refusedResize = false;
    }
    // Queues the refused candidate again once the faults or, for a heap refusal, the heap's release revision moves,
    // unless a newer request is queued.
    private void RetryRefusal() {
        if (
            !m_hasRefusal ||
            (m_pending is not null) ||
            m_resizePending ||
            m_rebindPending ||
            m_recountPending ||
            (
                (FaultsRevision == m_refusedFaultsRevision) &&
                (
                    !m_refusedByHeap ||
                    (m_device.Services.Bindings.HeapReleaseRevision == m_refusedHeapRevision)
                )
            )
        ) {
            return;
        }

        m_pending = m_refusedPending;
        m_resizePending = m_refusedResize;
        m_lastSwapError = null;
        ForgetRefusal();
    }
    // The most advances a resize waits after a discarded one before it builds again.
    private const int MaxResizeBackoff = 32;

    // Whether the running build only resizes the installed graph, the advance the latest such build started in, and the
    // advances the next one waits after it. A resize build discarded because the requested extent moved again before it
    // finished doubles the wait, and a build that installs clears it: a request swinging between extents every few
    // frames (a window a falling camera crosses) builds a graph a few times a second rather than every frame, while one
    // that settles builds at once.
    private bool m_buildResizes;
    private long m_resizeBuildAdvance;
    private int m_resizeBackoff;

    // Whether the next build would only resize the installed graph and waits out its backoff.
    private bool ResizeBacksOff => (
        (m_resizeBackoff > 0) &&
        OnlyResizes &&
        ((m_advances - m_resizeBuildAdvance) < m_resizeBackoff)
    );
    private bool OnlyResizes => (
        m_ready &&
        (m_pipeline is not null) &&
        (m_pending is null) &&
        m_resizePending &&
        !m_rebindPending &&
        !m_recountPending
    );

    // Kept apart from EnsureBuild so the build's closure is allocated only when a build starts, never on a polled frame.
    private void StartBuild(BuildKey key) {
        m_buildResizes = OnlyResizes;

        if (m_buildResizes) {
            m_resizeBuildAdvance = m_advances;
        }

        var request = new BuildRequest(
            // The storages whose history the install carries over keep their own images, so the build makes none for them.
            Carried: [.. CarriedHistoryOf(extent: (key.Width, key.Height), plan: key.Pipeline!.Plan).Keys],
            Device: m_device,
            DirectX: m_directX,
            Gpu: m_gpu,
            InFlight: m_inFlight,
            Instance: m_name,
            Key: key,
            KernelCounterRows: (m_passes.FirstOrDefault()?.KernelCounters?.Rows ?? 0),
            BoundPorts: [.. m_externalRegions.Keys],
            SlotObjects: m_slots.Any(predicate: static slot => ((slot.Fence is null) || (slot.Commands is null))),
            Packages: m_packages,
            Pipelines: m_pipelines
        );

        m_buildKey = key;
        m_buildAdvance = m_advances;
        m_build.Start(build: token => GraphBuild.CreateAsync(
            cancellationToken: token,
            request: request
        ));
    }
    // What the node should install next: a queued candidate, or the installed pipeline again at a requested extent or
    // with its arrays bound to other rows, both at the requested extent; or, after a device loss released the installed
    // graph, the installed pipeline at its own.
    private bool TryDesiredCandidate(out CompiledShaderPipeline pipeline, out (uint Width, uint Height) extent, out bool candidate) {
        if (m_pending is { } pending) {
            (pipeline, extent, candidate) = (pending, (m_requestedWidth, m_requestedHeight), true);
        } else if (
            (m_resizePending || m_rebindPending || m_recountPending) &&
            (m_pipeline is { } resized)
        ) {
            (pipeline, extent, candidate) = (resized, (m_requestedWidth, m_requestedHeight), true);
        } else if (
            !m_ready &&
            (m_pipeline is { } lost)
        ) {
            (pipeline, extent, candidate) = (lost, (m_width, m_height), false);
        } else {
            (pipeline, extent, candidate) = (null!, default, false);

            return false;
        }

        return true;
    }
    private bool TryDesiredKey(out BuildKey key) {
        key = default;

        if (!TryDesiredCandidate(
            candidate: out var candidate,
            extent: out var extent,
            pipeline: out var pipeline
        )) {
            return false;
        }

        try {
            key = KeyFor(
                candidate: candidate,
                extent: extent,
                pipeline: pipeline
            );
        } catch (InvalidDataException) {
            return false;
        }

        return true;
    }
    // The build of a pipeline at an extent, with the preview its selected output needs and the rows bound now.
    private BuildKey KeyFor(CompiledShaderPipeline pipeline, (uint Width, uint Height) extent, bool candidate) =>
        new(
            Candidate: candidate,
            Height: extent.Height,
            Pipeline: pipeline,
            Preview: PreviewFor(
                extent: extent,
                plan: pipeline.Plan
            ),
            Counts: CountsAt(
                extent: extent,
                plan: pipeline.Plan
            ),
            CountRevision: (CounterOf(plan: pipeline.Plan)?.Revision ?? 0L),
            RenderRevision: (RenderExtentOf(plan: pipeline.Plan)?.Revision ?? 0L),
            Export: m_export,
            Rows: m_rows,
            Width: extent.Width
        );

    // What one build makes: the pipeline, the extent it is planned at, whether it is a candidate (a queued pipeline, a
    // resize or a rebinding) rather than the installed pipeline rebuilt after a device loss, the preview it needs,
    // the rows its arrays read, which BindRows replaces whole whenever they change, and the counts its counted buffers
    // are allocated by, with the counter revision they were resolved at.
    private readonly record struct BuildKey(CompiledShaderPipeline? Pipeline, uint Width, uint Height, bool Candidate, (uint Width, uint Height)? Preview, RowBindings Rows, ShaderPipelineStorageCounts Counts, long CountRevision, long RenderRevision, IShaderPipelineOutputExport? Export) {
        public bool Matches(BuildKey other) =>
            (
                ReferenceEquals(
                    objA: Pipeline,
                    objB: other.Pipeline
                ) &&
                (Width == other.Width) &&
                (Height == other.Height) &&
                (Candidate == other.Candidate) &&
                (Preview == other.Preview) &&
                ReferenceEquals(
                    objA: Rows,
                    objB: other.Rows
                ) &&
                (Counts == other.Counts) &&
                (CountRevision == other.CountRevision) &&
                (RenderRevision == other.RenderRevision) &&
                ReferenceEquals(
                    objA: Export,
                    objB: other.Export
                )
            );
    }
    // Everything a build reads, captured on the frame thread when it starts; a build never touches the node.
    // KernelCounterRows is the rows the installed counters reached, BoundPorts the host buffer ports already bound,
    // which keep their regions across installs, and SlotObjects whether a frame slot still lacks its fence or commands.
    private sealed record BuildRequest(BuildKey Key, GpuDeviceServices Gpu, IGpuDeviceContext Device, GpuPassPipelineCache Pipelines, bool DirectX, uint InFlight, string Instance, RenderGraphPackageRecorders Packages, int[] Carried, int KernelCounterRows = 0, string[]? BoundPorts = null, bool SlotObjects = false);
    // The pipeline and module set of one candidate, built on the thread pool. The install takes each object into the
    // runtime graph and clears it here, so disposing a build releases exactly what was never taken.
    private sealed class GraphBuild : IDisposable {
        private GraphBuild(int passCount) =>
            Passes = new PassObjects?[passCount];

        public PassObjects?[] Passes { get; }
        public PreviewObjects? Preview { get; set; }
        // The graph's staged regions, its package passes' in pass order, then its row regions, then its host buffer ports,
        // whose copy sets the graph's one copy pool reserves (DescriptorPools' stagedRegions). When there is one, the build holds a lease
        // on the device's region-copy pipeline, taken ready.
        public int StagedRegions { get; private set; }
        public IGpuComputePipeline? CopyPipeline { get; private set; }
        public GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline>? RegionCopy { get; set; }

        // Builds every pass's modules and pipelines, then the preview's. Safe on any thread: it only creates objects on
        // the device, counted through the node's wrapped services, and awaits every pipeline lease and package build, so
        // it holds no thread while it waits. The token is checked before each pass and the preview, and a build that
        // fails or is canceled releases what it created.
        public static async Task<GraphBuild> CreateAsync(BuildRequest request, CancellationToken cancellationToken) {
            var pipeline = request.Key.Pipeline!;
            var plan = pipeline.Plan;
            var specs = VersionSpecs(plan: plan);
            var build = new GraphBuild(passCount: plan.Passes.Count);

            try {
                foreach (var planned in plan.Passes) {
                    cancellationToken.ThrowIfCancellationRequested();

                    var objects = new PassObjects();

                    build.Passes[planned.Index] = objects;
                    await objects.CreateAsync(
                        cancellationToken: cancellationToken,
                        compiled: pipeline.Shaders.GetValueOrDefault(key: planned.Name),
                        planned: planned,
                        request: request,
                        specs: specs
                    ).ConfigureAwait(continueOnCapturedContext: false);
                }

                await build.StateRegionsAsync(
                    cancellationToken: cancellationToken,
                    plan: plan,
                    request: request
                ).ConfigureAwait(continueOnCapturedContext: false);
                build.CreatePassRegions(cancellationToken: cancellationToken, plan: plan, request: request);
                build.CreateGraphRegions(cancellationToken: cancellationToken, plan: plan, request: request);
                build.CreateImages(
                    cancellationToken: cancellationToken,
                    plan: plan,
                    request: request
                );

                if (request.Key.Preview is { } preview) {
                    cancellationToken.ThrowIfCancellationRequested();
                    build.Preview = await PreviewObjects.CreateAsync(
                        cancellationToken: cancellationToken,
                        device: request.Device,
                        owner: request.Instance,
                        gpu: request.Gpu,
                        pipelines: request.Pipelines,
                        directX: request.DirectX,
                        height: preview.Height,
                        inFlight: request.InFlight,
                        width: preview.Width
                    ).ConfigureAwait(continueOnCapturedContext: false);
                }
            } catch {
                build.Dispose();

                throw;
            }

            return build;
        }
        public void Dispose() {
            foreach (var pass in Passes) {
                pass?.Dispose();
            }

            DisposeUntaken(later: static disposable => disposable?.Dispose());
            Preview?.Dispose();
            Preview = null;
            RegionCopy?.Release();
            RegionCopy = null;
            CopyPipeline = null;
        }

        // States the graph's staged regions under the device's residency choice, its package regions', its row regions' and
        // its host buffer ports', and, when one stages, takes the region-copy pipeline ready, so the install on the frame
        // thread never waits for it.
        private async Task StateRegionsAsync(ShaderPipelinePlan plan, BuildRequest request, CancellationToken cancellationToken) {
            var staged = 0;

            foreach (var pass in Passes) {
                foreach (var region in (pass?.Regions ?? [])) {
                    if (Staged(device: request.Device, byteCount: ((ulong)region.ByteCount))) {
                        staged++;
                    }
                }
            }
            foreach (var region in RowPlan.Of(plan: plan, rows: request.Key.Rows).Regions) {
                if (Staged(device: request.Device, byteCount: region.ByteCount)) {
                    staged++;
                }
            }
            foreach (var port in HostBufferPorts(plan: plan)) {
                if (Staged(device: request.Device, byteCount: port.SizeBytes!.Value)) {
                    staged++;
                }
            }

            StagedRegions = staged;

            if (staged == 0) {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            RegionCopy = RegionCopyOf(
                device: request.Device,
                instance: request.Instance,
                packages: request.Packages
            ).Acquire(device: request.Device);
            CopyPipeline = (await RegionCopy.WaitAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false)).Compute;
        }

        // Creates every owned image storage's images at the build's extent, so the install takes them rather than
        // creating them on the frame thread, where creating a view's targets cost a frame 10 to 40 ms. Object creation is
        // free-threaded on both backends, and the node's wrapped services count it interlocked.
        private void CreateImages(ShaderPipelinePlan plan, BuildRequest request, CancellationToken cancellationToken) {
            m_images = new IGpuImage[]?[plan.Storages.Count];
            m_buffers = new IGpuBuffer[]?[plan.Storages.Count];

            foreach (var planned in plan.Storages) {
                if (planned.Declaration.IsExternal || request.Carried.Contains(value: planned.Index)) {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();

                // A node-owned storage buffer; a package that owns a buffer lends its own at the install.
                if (planned.Declaration.Kind == ShaderPipelineResourceKind.Buffer) {
                    var sizeBytes = planned.Declaration.ResolveSizeBytes(counts: request.Key.Counts);

                    if (
                        (sizeBytes == 0UL) ||
                        request.Packages.OwnsBuffer(plan: plan, storage: planned)
                    ) {
                        continue;
                    }

                    var buffers = new IGpuBuffer[InstancesOf(
                        inFlight: request.InFlight,
                        storage: planned
                    )];
                    var usage = BufferUsageOf(
                        plan: plan,
                        storage: planned
                    );

                    m_buffers[planned.Index] = buffers;

                    for (var i = 0; (i < buffers.Length); i++) {
                        buffers[i] = request.Gpu.BufferFactory.CreateDeviceLocal(
                            name: new GpuObjectName(
                                index: i,
                                owner: request.Instance,
                                part: planned.Declaration.Name
                            ),
                            sizeBytes: sizeBytes,
                            usage: usage
                        );
                    }

                    continue;
                }

                var images = new IGpuImage[InstancesOf(
                    inFlight: request.InFlight,
                    storage: planned
                )];

                m_images[planned.Index] = images;
                CreateStorageImages(
                    counts: request.Key.Counts,
                    frame: (request.Key.Width, request.Key.Height),
                    gpu: request.Gpu,
                    images: images,
                    owner: request.Instance,
                    plan: plan,
                    planned: planned
                );
            }
        }
        // Creates every pass's ring of pass blocks, which the install takes as it allocates the pass's slot objects.
        private void CreatePassRegions(ShaderPipelinePlan plan, BuildRequest request, CancellationToken cancellationToken) {
            m_passRegions = new GpuRegion?[plan.Passes.Count];

            foreach (var planned in plan.Passes) {
                cancellationToken.ThrowIfCancellationRequested();
                m_passRegions[planned.Index] = PassRegionOf(
                    device: request.Device,
                    gpu: request.Gpu,
                    inFlight: request.InFlight,
                    owner: request.Instance,
                    parameters: planned.Parameters,
                    planned: planned.Name
                );
            }
        }
        // Creates what the install would otherwise create on the frame thread: the frame block ring every pass binds, the
        // row regions and host buffer ports that do not stage (a staged one takes copy sets from the pool the install
        // creates), the kernel counters, as many rows as the node's installed counters reached, so they are not grown on
        // the frame either, and for a node's first graph its frame slots' fences and command pools.
        private void CreateGraphRegions(ShaderPipelinePlan plan, BuildRequest request, CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();

            if (plan.Passes.FirstOrDefault()?.Parameters is { } frameLayout) {
                m_frameRegion = FrameRegionOf(
                    blockBytes: frameLayout.FrameBlockSizeBytes,
                    device: request.Device,
                    gpu: request.Gpu,
                    inFlight: request.InFlight,
                    owner: request.Instance
                );
            }

            var rows = RowPlan.Of(plan: plan, rows: request.Key.Rows).Regions;

            m_rowRegions = new GpuRegion?[rows.Length];

            for (var index = 0; (index < rows.Length); index++) {
                if (!Staged(device: request.Device, byteCount: rows[index].ByteCount)) {
                    m_rowRegions[index] = RingRegionOf(
                        byteCount: ((int)rows[index].ByteCount),
                        device: request.Device,
                        gpu: request.Gpu,
                        inFlight: request.InFlight,
                        name: RowRegionName(owner: request.Instance, spec: rows[index])
                    );
                }
            }
            foreach (var port in HostBufferPorts(plan: plan)) {
                if (
                    !(request.BoundPorts ?? []).Contains(value: port.Name) &&
                    (port.SizeBytes is { } sizeBytes and > 0UL and <= int.MaxValue) &&
                    !Staged(device: request.Device, byteCount: sizeBytes)
                ) {
                    m_portRegions[port.Name] = RingRegionOf(
                        byteCount: ((int)sizeBytes),
                        device: request.Device,
                        gpu: request.Gpu,
                        inFlight: request.InFlight,
                        name: new GpuObjectName(
                            owner: request.Instance,
                            part: port.Name
                        )
                    );
                }
            }
            if (plan.CountsKernelWork) {
                m_kernelCounters = KernelCountersOf(
                    buffers: request.Gpu.BufferFactory,
                    inFlight: request.InFlight,
                    owner: request.Instance,
                    rows: Math.Max(val1: plan.Passes.Count, val2: request.KernelCounterRows)
                );
            }
            if (request.SlotObjects) {
                m_slotFences = new IGpuSubmissionFence?[request.InFlight];
                m_slotCommands = new IGpuCommandPool?[request.InFlight];

                for (var index = 0; (index < request.InFlight); index++) {
                    m_slotFences[index] = request.Gpu.QueueSubmitter.CreateSubmissionFence();
                    m_slotCommands[index] = CommandsOf(
                        gpu: request.Gpu,
                        owner: request.Instance,
                        slot: index
                    );
                }
            }
        }
        // Takes a frame slot's fence the build made, or null when it made none.
        public IGpuSubmissionFence? TakeFence(int slot) {
            if (slot >= m_slotFences.Length) {
                return null;
            }

            var fence = m_slotFences[slot];

            m_slotFences[slot] = null;

            return fence;
        }
        // Takes a frame slot's command pool the build made, or null when it made none.
        public IGpuCommandPool? TakeCommands(int slot) {
            if (slot >= m_slotCommands.Length) {
                return null;
            }

            var commands = m_slotCommands[slot];

            m_slotCommands[slot] = null;

            return commands;
        }

        private IGpuSubmissionFence?[] m_slotFences = [];
        private IGpuCommandPool?[] m_slotCommands = [];
        // Takes the frame block ring, or null when the build made none.
        public GpuRegion? TakeFrameRegion() {
            var region = m_frameRegion;

            m_frameRegion = null;

            return region;
        }
        // Takes a row region the build made, or null when it stages: the install's row plan is the build's.
        public GpuRegion? TakeRowRegion(int index) {
            var region = m_rowRegions[index];

            m_rowRegions[index] = null;

            return region;
        }
        // Takes every host buffer port's region the build made, by port name.
        public Dictionary<string, GpuRegion> TakePortRegions() {
            var regions = m_portRegions;

            m_portRegions = new Dictionary<string, GpuRegion>(comparer: StringComparer.Ordinal);

            return regions;
        }
        // Takes the kernel counters, or null when the build made none.
        public GpuKernelCounters? TakeKernelCounters() {
            var counters = m_kernelCounters;

            m_kernelCounters = null;

            return counters;
        }

        private GpuRegion? m_frameRegion;
        private GpuRegion?[] m_rowRegions = [];
        private Dictionary<string, GpuRegion> m_portRegions = new(comparer: StringComparer.Ordinal);
        private GpuKernelCounters? m_kernelCounters;

        // Takes a pass's ring of pass blocks, or null when the build made none for it.
        public GpuRegion? TakePassRegion(int pass) {
            if (pass >= m_passRegions.Length) {
                return null;
            }

            var region = m_passRegions[pass];

            m_passRegions[pass] = null;

            return region;
        }

        private GpuRegion?[] m_passRegions = [];
        private IGpuBuffer[]?[] m_buffers = [];

        // Takes a storage's buffers, or null when the build made none for it.
        public IGpuBuffer[]? TakeBuffers(int storage) {
            if (storage >= m_buffers.Length) {
                return null;
            }

            var buffers = m_buffers[storage];

            m_buffers[storage] = null;

            return buffers;
        }

        // Takes a storage's images, or null when the build made none for it.
        public IGpuImage[]? TakeImages(int storage) {
            if (storage >= m_images.Length) {
                return null;
            }

            var images = m_images[storage];

            m_images[storage] = null;

            return images;
        }
        // Disposes a build nothing installs, handing its pass regions to the node's disposal (images go through the
        // image table either way): none was ever submitted, so a discarded candidate costs the frame thread only its
        // pipelines' leases.
        public void Discard(Action<IDisposable?> later) {
            DisposeUntaken(later: later);
            Dispose();
        }
        // Disposes every object the install did not take: a storage whose history it carried keeps its own images.
        public void DisposeUntaken(Action<IDisposable?> later) {
            foreach (var images in m_images) {
                foreach (var image in (images ?? [])) {
                    image?.Dispose();
                }
            }
            foreach (var region in m_passRegions) {
                later(obj: region);
            }
            foreach (var buffers in m_buffers) {
                foreach (var buffer in (buffers ?? [])) {
                    later(obj: buffer);
                }
            }
            foreach (var region in m_rowRegions) {
                later(obj: region);
            }
            foreach (var region in m_portRegions.Values) { later(obj: region); }
            foreach (var fence in m_slotFences) { later(obj: fence); }
            foreach (var commands in m_slotCommands) { later(obj: commands); }

            later(obj: m_frameRegion);
            later(obj: m_kernelCounters);
            m_frameRegion = null;
            m_kernelCounters = null;
            m_rowRegions = [];
            m_portRegions.Clear();
            m_slotFences = [];
            m_slotCommands = [];
            m_buffers = [];

            m_images = [];
            m_passRegions = [];
        }

        private IGpuImage[]?[] m_images = [];

        // Takes the build's lease: the node's own when it holds none, and otherwise released, since both share the one
        // pipeline the device's cache keeps.
        public GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline>? TakeRegionCopy() {
            var lease = RegionCopy;

            RegionCopy = null;

            return lease;
        }
        public PassObjects TakePass(int index) {
            var objects = (Passes[index] ?? throw new InvalidOperationException(message: $"The build has no objects for pass {index}."));

            Passes[index] = null;

            return objects;
        }
    }
    // One pass's pipeline: for a document pass its lease on the pass-pipeline cache's entry, a compute pipeline and its
    // module or a graphics pipeline, its two modules and the render pass it is created for; for a package pass what its
    // package's factory builds. The images it draws into are the graph's, allocated on the frame thread.
    private sealed class PassObjects : IDisposable {
        public (uint Width, uint Height) Extent;
        public IRenderGraphPackageFactory? PackageFactory;
        public RenderGraphPackageRecorderContext? PackageContext;
        public IDisposable? PackageBuilt;
        public GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline>? Pipeline;
        public RenderGraphPackageRegion[]? Regions;

        public async Task CreateAsync(ShaderPipelinePlannedPass planned, CompiledShader? compiled, BuildRequest request, IReadOnlyDictionary<string, ShaderPipelineResource> specs, CancellationToken cancellationToken) {
            // A package pass's objects are whatever its factory builds; its recorder binds its own descriptors.
            if (planned.Declaration is not { } declaration) {
                var step = planned.Package!;

                Extent = PassExtent(pass: planned, counts: request.Key.Counts);
                PackageFactory = request.Packages.FactoryFor(
                    instance: request.Instance,
                    package: step.Package,
                    pass: planned.Name
                );
                PackageContext = PackageContextOf(
                    extent: Extent,
                    planned: planned,
                    request: request,
                    specs: specs
                );
                Regions = [.. PackageFactory.Regions(context: PackageContext)];
                PackageBuilt = await PackageFactory.BuildAsync(
                    cancellationToken: cancellationToken,
                    context: PackageContext
                ).ConfigureAwait(continueOnCapturedContext: false);

                return;
            }
            if (compiled is null) {
                throw new InvalidDataException(message: $"Pass '{declaration.Name}' has no compiled shader.");
            }

            // A document pass binds its frame and pass groups and pushes nothing.
            var layout = GroupLayoutOf(planned: planned);
            var primary = (request.DirectX
                ? compiled.DxilByStage
                : compiled.SpirvByStage
            );
            GpuPassPipelineKey key;

            Extent = PassExtent(pass: planned, counts: request.Key.Counts);
            if (declaration.Kind == ShaderPipelineDocumentPassKind.Compute) {
                if (
                    !primary.TryGetValue(
                    key: ShaderStage.Compute,
                    value: out var bytes
                ) ||
                    bytes.IsEmpty
                ) {
                    throw new InvalidDataException(message: $"Pass '{declaration.Name}' has no compute bytecode.");
                }

                key = GpuPassPipelineKey.OfCompute(
                    bytecode: bytes,
                    description: new GpuComputePipelineDescription(
                        declaration.Name,
                        [],
                        null,
                        Layout: layout
                    )
                );
            } else {
                if (
                    !primary.TryGetValue(
                    key: ShaderStage.Vertex,
                    value: out var vertex
                ) ||
                    !primary.TryGetValue(
                    key: ShaderStage.Fragment,
                    value: out var fragment
                )
                ) {
                    throw new InvalidDataException(message: $"Fullscreen pass '{declaration.Name}' needs vertex and fragment bytecode.");
                }

                var vertexInput = ((declaration.Geometry is { } geometry)
                    ? new GpuVertexInputLayout(
                        Attributes: [.. geometry.Attributes.Select(selector: static attribute => new GpuVertexAttribute(
                            Format: Enum.Parse<GpuVertexFormat>(
                                ignoreCase: true,
                                value: attribute.Format
                            ),
                            Location: attribute.Location,
                            OffsetBytes: attribute.OffsetBytes
                        ))],
                        StrideBytes: geometry.StrideBytes
                    )
                    : ((declaration.Vertex == ShaderPipelineVertexInput.Position)
                        ? new GpuVertexInputLayout(
                            FullscreenTriangle.StrideBytes,
                            [new GpuVertexAttribute(
                                Format: GpuVertexFormat.R32G32Float,
                                Location: 0,
                                OffsetBytes: 0
                            )]
                        )
                        : new GpuVertexInputLayout(
                            Attributes: [],
                            StrideBytes: 0
                        ))
                );

                key = GpuPassPipelineKey.OfGraphics(
                    description: new GpuGraphicsPipelineDescription(
                        declaration.Name,
                        vertexInput,
                        layout,
                        DepthCompareOf(pass: planned)
                    ),
                    fragment: fragment,
                    renderPass: RenderPassOf(
                        planned: planned,
                        specs: specs
                    ),
                    vertex: vertex
                );
            }

            // The lease is stored before the wait, so a build that fails or is canceled while it waits releases it.
            Pipeline = request.Pipelines.Acquire(
                device: request.Device,
                key: key
            );
            _ = await Pipeline.WaitAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        }

        // A pass with a depth attachment tests by its declared comparison, less when it declares none; any other pass has
        // no depth test.
        private static GpuDepthCompare? DepthCompareOf(ShaderPipelinePlannedPass pass) {
            if (!pass.Attachments.Any(predicate: static attachment => attachment.Depth)) {
                return null;
            }

            return (pass.Declaration!.DepthCompare ?? ShaderPipelineDepthCompare.Less) switch {
                ShaderPipelineDepthCompare.Less => GpuDepthCompare.Less,
                ShaderPipelineDepthCompare.LessOrEqual => GpuDepthCompare.LessOrEqual,
                ShaderPipelineDepthCompare.Greater => GpuDepthCompare.Greater,
                ShaderPipelineDepthCompare.GreaterOrEqual => GpuDepthCompare.GreaterOrEqual,
                ShaderPipelineDepthCompare.Equal => GpuDepthCompare.Equal,
                ShaderPipelineDepthCompare.Always => GpuDepthCompare.Always,
                var compare => throw new InvalidDataException(message: $"Pass '{pass.Name}' has an unknown depth comparison '{compare}'."),
            };
        }
        // A graphics pass's render pass, exactly as its planned attachments declare it: each attachment cleared or loaded,
        // stored or discarded, and left in its attachment layout, which is the state the pass's planned access leaves.
        private static GpuRenderPassDescription RenderPassOf(ShaderPipelinePlannedPass planned, IReadOnlyDictionary<string, ShaderPipelineResource> specs) {
            var colors = new List<GpuColorAttachment>();
            GpuDepthAttachment? depth = null;

            foreach (var attachment in planned.Attachments) {
                var format = ParseFormat(format: specs[attachment.Version].Format);

                if (attachment.Depth) {
                    depth = DepthAttachmentOf(
                        attachment: attachment,
                        format: format
                    );
                } else {
                    colors.Add(item: new GpuColorAttachment(
                        FinalLayout: GpuImageLayout.RenderTarget,
                        Format: format,
                        Load: attachment.Load,
                        Store: attachment.Store
                    ));
                }
            }

            return new GpuRenderPassDescription(
                Colors: colors,
                Depth: depth
            );
        }

        public void Dispose() {
            PackageBuilt?.Dispose();
            PackageBuilt = null;
            Pipeline?.Release();
            Pipeline = null;
        }
    }
}
