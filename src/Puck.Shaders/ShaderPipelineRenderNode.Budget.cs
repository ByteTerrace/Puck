using Puck.Abstractions.Gpu;
using Puck.Abstractions.Memory;
using Puck.Hosting;
using Puck.Hosting.Jobs;

namespace Puck.Shaders;

// The node's memory account. Every byte count the node reports or refuses by comes from the plan through Footprint,
// GraphBytes and RegionBytesOf, which mirror what Allocate and the build create: one image or buffer per frame slot of
// each storage the node owns (the images a graphics pass draws into among them), and one only of a transient storage,
// each buffer at its fixed size or its count resolved against the graph's counts, one geometry buffer per geometry pass
// and per fullscreen pass that reads the Position input, one constant buffer per frame slot for the frame group's block
// and for each pass's pass block (ConstantBytes), the buffers of every host-written region the graph reads (each package
// pass's regions, its row regions under the rows bound when it is built, and each host buffer port's, GpuRegion.BytesOf
// under the device's residency choice, a port's whether or not a host has bound it yet), the kernel counters of a graph
// whose kernels count their own work (KernelCounterBytes), and one preview image per frame slot.
// ShaderPipelineRenderNode.Retirement.cs's LiveBytes counts the same kinds from a replaced graph's objects. The capture
// readback is the node's, not a graph's: it creates its staging buffer on the first capture, sized to the published
// surface, and OwnedBytes counts it from then on, so every later replacement's peak includes it. Like every other count
// here it is the logical size, not the backend's allocation, which may pad rows.
// Package readback rings belong to their installed recorder, are counted at their actual lazy allocation sizes, and
// remain in a retired graph's LiveBytes until that recorder is disposed.
public sealed partial class ShaderPipelineRenderNode {
    private const ulong FullscreenVertexBytes = (FullscreenTriangle.StrideBytes * FullscreenTriangle.VertexCount);

    private ulong? m_budgetCapBytes;
    // The installed graph's region bytes (RegionBytesOf), which m_allocationBytes includes.
    private ulong m_regionBytes;
    // The capture readback's staging buffer, counted like every other resource at its logical size: the published RGBA8
    // surface's width times its height times four. Zero until the first capture creates it.
    private ulong m_readbackBytes;
    // The instance's reservation in the process's memory account, made for the owner its resources are named under.
    // Fixed storage: a replacement that finds no room is refused here, before anything is allocated, and the installed
    // graph's own allocations are never refused.
    private MemoryAccount.DeviceReservation? m_reservation;

    /// <summary>Gets the account the instance reserves its replacements in: the process's.</summary>
    public MemoryAccount MemoryAccount { get; init; } = MemoryAccount.Shared;

    private MemoryAccount.DeviceReservation Reservation => (m_reservation ??= MemoryAccount.ReserveDevice(
        memoryClass: MemoryClass.Fixed,
        owner: new GpuObjectName(owner: m_name, part: "graph").MemoryOwner(),
        pool: GpuMemorySegments.ExpectedPool(role: GpuMemoryRole.DeviceLocal, unifiedMemory: m_device.MemoryProfile.UnifiedMemory)
    ));

    // The bytes promised to the graph build in flight: what its candidate adds to what the instance owns, from the
    // build's admission until the build is taken, whether it installs, is refused or is discarded.
    private ulong m_buildPromise;

    // The bytes promised to the preview the pending selection waits on.
    private ulong PreviewPromise => ((m_previewRequest is { } request)
        ? PreviewBytes(extent: (request.Width, request.Height), inFlight: m_inFlight)
        : 0UL
    );

    // What the reservation holds: everything the instance owns and every allocation it has been promised and not made.
    // The reservation is always this sum, so an event of one generation (a retired graph released) never lowers it
    // under another's promise (a build still running).
    private long ReservedPeak(ulong more) => ((long)Math.Min(val1: checked((((OwnedBytes + m_buildPromise) + PreviewPromise) + more)), val2: ((ulong)long.MaxValue)));
    // Reserves more bytes beside everything owned and promised, before anything is allocated for them; false refuses
    // them. With no graph installed, or for the replacement of one that cannot render (least), the bytes are the
    // instance's least, reserved whatever the pool holds.
    private bool TryReserveMore(ulong bytes, bool least = false) {
        var peak = ReservedPeak(more: bytes);

        return Reservation.TryReserve(floor: ((m_ready && !least) ? 0L : peak), peak: peak);
    }
    // Sets the reservation to what the instance owns and has been promised, after a promise ended or objects were
    // released. It restates what was already admitted, so it is stated as the floor and is never refused.
    private void SettleReservation() => _ = m_reservation?.TryReserve(floor: ReservedPeak(more: 0UL), peak: ReservedPeak(more: 0UL));
    // Ends the graph build's promise as the build is taken.
    private void EndBuildPromise() {
        m_buildPromise = 0UL;
        SettleReservation();
    }
    // Releases the reservation with the instance's objects.
    private void ReleaseReservation() {
        m_buildPromise = 0UL;
        _ = m_reservation?.TryReserve(peak: 0L);
    }
    // The refusal of a candidate the account or the device refused while it allocated: its reserved peak counts what
    // its objects hold, and the device allocates more (a whole block for the first object of its kind, padding), which
    // a full pool does not have. The installed graph keeps running, as it does for a peak over the budget.
    private static InvalidDataException AllocationRefusal(GpuMemoryRefusedException refusal) => new(
        innerException: refusal,
        message: $"[{ShaderPipelineMemoryAccount.RefusalCode}] The candidate was refused while it allocated: {refusal.Message} The installed graph keeps running."
    );

    /// <summary>Gets the budget every replacement's peak is refused against, in bytes: the account's
    /// (<see cref="DeviceBudgetBytes"/>), lowered to <see cref="BudgetCapBytes"/> when that is smaller.</summary>
    public ulong BudgetBytes => ((m_budgetCapBytes is { } cap)
        ? Math.Min(
            val1: cap,
            val2: DeviceBudgetBytes
        )
        : DeviceBudgetBytes
    );
    /// <summary>Gets or sets a cap, in bytes, that lowers <see cref="BudgetBytes"/> below the device's budget, or
    /// <see langword="null"/> for none. A cap never raises the budget, and lowering it never frees the installed graph:
    /// it refuses the next replacement whose peak does not fit.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero.</exception>
    public ulong? BudgetCapBytes {
        get => m_budgetCapBytes;
        set {
            if (value == 0UL) {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    "A pipeline budget cap must be positive."
                );
            }

            m_budgetCapBytes = value;
        }
    }
    /// <summary>Gets the bytes of the installed graph's host-written regions: every package pass's regions
    /// (<see cref="IRenderGraphPackageFactory.Regions"/>), its row regions (<see cref="RowRegionCount"/>) and every host
    /// buffer port's (<see cref="ShaderPipelineInitialization.Host"/>), each <see cref="GpuRegion.BytesOf"/> under the policy
    /// <see cref="GpuResidency.Select"/> picks with a reader in flight, a port's whether or not a host has bound it yet.
    /// <see cref="OwnedBytes"/> and the installed graph's steady-state bytes include them; zero when no graph is
    /// installed.</summary>
    public ulong RegionBytes => m_regionBytes;
    /// <summary>Gets the most bytes the process's memory account would let the instance's peak reach now: what the
    /// instance owns or has reserved, and the room its pool has left (<see cref="MemoryAccount.DeviceReservation.Room"/>);
    /// <see cref="ulong.MaxValue"/> where nothing bounds the pool. A replacement is admitted by reserving its peak
    /// (<see cref="MemoryAccount.DeviceReservation.TryReserve"/>), so two instances cannot be promised the same room.</summary>
    public ulong DeviceBudgetBytes {
        get {
            var reservation = Reservation;
            var room = reservation.Room;

            return ((room == long.MaxValue) ? ulong.MaxValue : checked((Math.Max(val1: OwnedBytes, val2: ((ulong)reservation.Peak)) + ((ulong)room))));
        }
    }
    /// <summary>Gets the installed graph's account: the bytes it owns, and the peak a reload of it at its extent and
    /// selection would reach from what the node owns now. Both are zero when no graph is installed.</summary>
    public ShaderPipelineMemoryAccount InstalledAccount => (((m_pipeline is { } pipeline) && m_ready)
        ? Account(
            counts: m_installedCounts,
            plan: pipeline.Plan,
            preview: ((m_preview is { } preview)
                ? (preview.Width, preview.Height)
                : null
            ),
            rows: m_installedRows
        )
        : new ShaderPipelineMemoryAccount(
            BudgetBytes: BudgetBytes,
            PeakBytes: 0UL,
            SteadyBytes: 0UL
        )
    );

    // The bytes of the preview's targets, one per frame slot, at an extent, and its one encode block.
    private static ulong PreviewBytes((uint Width, uint Height)? extent, uint inFlight) =>
        ((extent is { } preview)
            ? checked(((((((ulong)preview.Width) * preview.Height) * 4UL) * inFlight) + IGpuBindings.ConstantBufferAlignment))
            : 0UL
        );
    // One storage's extent and the bytes its instances occupy; a host-owned storage occupies nothing. An image is
    // allocated at its declared extent and format, which the planner requires, whichever passes write it, and a buffer at
    // its fixed size or its count resolved against the graph's counts.
    private static (uint Width, uint Height, ulong Bytes) Footprint(ShaderPipelinePlannedStorage storage, ShaderPipelineStorageCounts counts, int count) {
        var declaration = storage.Declaration;

        if (declaration.Kind == ShaderPipelineResourceKind.Buffer) {
            return (0U, 0U, (declaration.IsExternal
                ? 0UL
                : checked((declaration.ResolveSizeBytes(counts: counts) * ((ulong)count)))
            ));
        }

        var frame = (counts.Width, counts.Height);
        var extent = (declaration.Dimensions?.Resolve(
            frameHeight: frame.Height,
            frameWidth: frame.Width,
            renderWidth: counts.RenderWidth,
            renderHeight: counts.RenderHeight
        ) ?? frame);

        return (extent.Width, extent.Height, (declaration.IsExternal
            ? 0UL
            : checked((ImageBytes(
                format: declaration.Format,
                height: extent.Height,
                width: extent.Width
            ) * ((ulong)count)))
        ));
    }
    // The bytes a graph planned at the counts' frame extent owns, before its preview: every storage's instances, and
    // the geometry buffer of each pass that has one.
    private ulong GraphBytes(ShaderPipelinePlan plan, ShaderPipelineStorageCounts counts, uint inFlight) {
        var bytes = 0UL;

        foreach (var storage in plan.Storages) {
            if (m_packages.OwnsBuffer(plan: plan, storage: storage)) { continue; }
            bytes = checked((bytes + Footprint(
                count: InstancesOf(
                    inFlight: inFlight,
                    plan: plan,
                    storage: storage
                ),
                counts: counts,
                storage: storage
            ).Bytes));
        }
        foreach (var pass in plan.Passes) {
            if (pass.Declaration is { } declaration) {
                bytes = checked((bytes + GeometryBytes(pass: declaration)));
            }
        }

        return checked(((bytes + ConstantBytes(
            inFlight: inFlight,
            plan: plan
        )) + KernelCounterBytes(
            inFlight: inFlight,
            plan: plan
        )));
    }
    // The bytes of the kernel counters of a graph a pass of which counts its kernels' work: per frame slot a counter
    // buffer and a readback buffer, one row a pass each (GpuKernelCounters).
    private static ulong KernelCounterBytes(ShaderPipelinePlan plan, uint inFlight) =>
        (plan.CountsKernelWork
            ? checked(((((ulong)inFlight) * 2UL) * GpuKernelCounters.SlotBytesOf(rows: plan.Passes.Count)))
            : 0UL);
    // The bytes of the constant buffers a graph's passes bind, one per frame slot: the frame group's block, which every
    // pass shares, and each pass's own pass block, each in whole constant-buffer views.
    private static ulong ConstantBytes(ShaderPipelinePlan plan, uint inFlight) {
        var grouped = plan.Passes;

        if (grouped.Count == 0) {
            return 0UL;
        }

        var views = ((ulong)IGpuBindings.ConstantBufferBytes(blockBytes: grouped[0].Parameters.FrameBlockSizeBytes));

        foreach (var pass in grouped) {
            views = checked((views + ((ulong)IGpuBindings.ConstantBufferBytes(blockBytes: pass.Parameters.SizeBytes))));
        }

        return checked((views * inFlight));
    }
    // The bytes of a pass's one geometry buffer: a geometry pass's vertices and indices, the shared fullscreen triangle
    // of a fullscreen pass that reads the Position input, and nothing for any other pass.
    private static ulong GeometryBytes(ShaderPipelinePass pass) =>
        ((pass.Geometry is { } geometry)
            ? geometry.SizeBytes
            : (((pass.Kind == ShaderPipelineDocumentPassKind.Fullscreen) && (pass.Vertex == ShaderPipelineVertexInput.Position))
                ? FullscreenVertexBytes
                : 0UL));
    // The bytes of one image of a declared format, as the image factories allocate it.
    private static ulong ImageBytes(string? format, uint width, uint height) => GpuPixelFormats.ImageByteLength(
        format: ParseFormat(format: format),
        height: height,
        width: width
    );
    // What installing a graph planned at an extent, with its counted buffers resolved against counts at that extent, the
    // preview its selection needs and its arrays bound to rows, costs from what the node owns now. Storage the graph
    // carries from the installed one is moved, not allocated, so the peak holds its bytes once.
    private ShaderPipelineMemoryAccount Account(ShaderPipelinePlan plan, ShaderPipelineStorageCounts counts, (uint Width, uint Height)? preview, RowBindings rows) {
        var extent = (counts.Width, counts.Height);
        var steady = checked((((GraphBytes(
            counts: counts,
            inFlight: m_inFlight,
            plan: plan
        ) + ExportBytes(plan: plan)) + RegionBytesOf(
            extent: extent,
            plan: plan,
            rows: rows
        )) + PreviewBytes(
            extent: preview,
            inFlight: m_inFlight
        )));

        if (ReferenceEquals(objA: plan, objB: m_pipeline?.Plan) && (m_passes.Length > 0) && (m_passes[0].KernelCounters is { } counters)) {
            steady = checked(((steady + counters.TotalBytes) - KernelCounterBytes(inFlight: m_inFlight, plan: plan)));
        }
        if (ReferenceEquals(objA: plan, objB: m_pipeline?.Plan)) { steady = checked((steady + PackageReadbackBytes(passes: m_passes))); }
        var carried = 0UL;

        foreach (var index in CarriedStoragesOf(
            extent: extent,
            plan: plan
        ).Keys) {
            carried = checked((carried + Footprint(
                count: InstancesOf(inFlight: m_inFlight, plan: plan, storage: plan.Storages[index]),
                counts: counts,
                storage: plan.Storages[index]
            ).Bytes));
        }

        return new ShaderPipelineMemoryAccount(
            BudgetBytes: BudgetBytes,
            PeakBytes: checked(((OwnedBytes + steady) - carried)),
            SteadyBytes: steady
        );
    }
    // The bytes of the host-written regions a graph planned at an extent reads: each package pass's regions, which its
    // package's factory states for the pass's context, its row regions under a binding of its arrays to rows, and each
    // host buffer port's, every one under the policy the device picks for its size with a reader in flight.
    private ulong RegionBytesOf(ShaderPipelinePlan plan, (uint Width, uint Height) extent, RowBindings rows) {
        var bytes = RowRegionBytesOf(
            plan: plan,
            rows: rows
        );
        Dictionary<string, ShaderPipelineResource>? specs = null;
        BuildRequest? request = null;

        foreach (var planned in plan.Passes) {
            if (planned.Package is not { } step) {
                continue;
            }

            specs ??= VersionSpecs(plan: plan);
            request ??= new BuildRequest(
                Carried: [],
                Device: m_device,
                DirectX: m_directX,
                Gpu: m_gpu,
                InFlight: m_inFlight,
                Instance: m_name,
                Key: default,
                Packages: m_packages,
                Pipelines: m_pipelines
            );

            var factory = m_packages.FactoryFor(
                instance: m_name,
                package: step.Package,
                pass: planned.Name
            );

            foreach (var region in factory.Regions(context: PackageContextOf(
                extent: planned.ResolveExtent(
                    frameHeight: extent.Height,
                    frameWidth: extent.Width
                ),
                planned: planned,
                request: request,
                specs: specs
            ))) {
                bytes = checked((bytes + RegionBytesOf(byteCount: ((ulong)region.ByteCount))));
            }
        }
        foreach (var port in HostBufferPorts(plan: plan)) {
            bytes = checked((bytes + RegionBytesOf(byteCount: port.SizeBytes!.Value)));
        }

        return bytes;
    }
    // The bytes of one region of byteCount bytes the node creates: a ring or a staged copy, as Staged decides.
    private ulong RegionBytesOf(ulong byteCount) => GpuRegion.BytesOf(
        byteCount: checked((int)byteCount),
        policy: (Staged(
            byteCount: byteCount,
            device: m_device
        )
            ? GpuResidencyPolicy.Staged
            : GpuResidencyPolicy.Ring),
        slotCount: ((int)m_inFlight)
    );
    // The storages a graph planned at an extent takes over from the installed graph, by the index of the storage that
    // receives them: a storage of the same name, shape, resolved extent or size, instance count and initialization, whose
    // instances were created with every usage the graph gives them (a depth image for the same attachment). A carried
    // storage allocates nothing, so a replacement's peak holds only what it changes beside the installed graph rather
    // than two whole graphs. History keeps its contents. Any other storage is scratch its new graph clears or rewrites,
    // and only one the installed graph alone reads is carried: never an external, package-owned, borrowed or exported
    // storage, nor one either graph publishes, whose instances a consumer may still read. The installed graph keeps
    // every carried instance until it installs on the frame thread, and its submissions run before the new graph's on
    // the one queue.
    private Dictionary<int, CarriedStorage> CarriedStoragesOf(ShaderPipelinePlan plan, (uint Width, uint Height) extent) {
        var carried = new Dictionary<int, CarriedStorage>();

        if (
            (m_pipeline is not { } installed) ||
            !m_ready
        ) {
            return carried;
        }

        var counts = CountsAt(extent: extent, plan: plan);

        foreach (var storage in plan.Storages) {
            if (storage.IsExternal || (!storage.History && !Carriable(plan: plan, storage: storage))) {
                continue;
            }

            foreach (var old in m_resources) {
                if (
                    !string.Equals(
                        a: old.Spec.Name,
                        b: storage.Name,
                        comparisonType: StringComparison.Ordinal
                    ) ||
                    (old.History != storage.History) ||
                    (old.Count != InstancesOf(inFlight: m_inFlight, plan: plan, storage: storage)) ||
                    (!old.History && (old.Borrowed || (old.Export is not null) || !Carriable(plan: installed.Plan, storage: old.Storage) || HoldsPublication(resource: old))) ||
                    !CompatibleStorage(
                        current: storage.Declaration,
                        counts: counts,
                        old: old.Spec,
                        previousCounts: m_installedCounts
                    )
                ) {
                    continue;
                }

                // The instances themselves are what the candidate binds, so their own extent or size is what must match,
                // whatever the declarations resolved to.
                var (width, height, _) = Footprint(storage: storage, counts: counts, count: 1);

                if (old.Images is { } images) {
                    var usage = UsageOf(
                        plan: plan,
                        storage: storage
                    );

                    if (
                        !EveryImageFits(images: images, usage: usage, width: width, height: height) ||
                        (((usage & GpuImageUsage.DepthAttachment) != 0) && (DepthOf(plan: plan, storage: storage, format: ParseFormat(format: storage.Declaration.Format)) != DepthOf(plan: installed.Plan, storage: old.Storage, format: ParseFormat(format: old.Spec.Format))))
                    ) {
                        continue;
                    }
                } else if (
                    (old.Buffers is not { } buffers) ||
                    !EveryBufferFits(buffers: buffers, sizeBytes: storage.Declaration.ResolveSizeBytes(counts: counts)) ||
                    ((BufferUsageOf(plan: installed.Plan, storage: old.Storage) & BufferUsageOf(plan: plan, storage: storage)) != BufferUsageOf(plan: plan, storage: storage))
                ) {
                    continue;
                }

                carried.Add(
                    key: storage.Index,
                    value: new CarriedStorage(Old: old)
                );
            }
        }

        return carried;
    }
    // Whether every carried image exists with every usage the candidate gives it, at the candidate's extent.
    private static bool EveryImageFits(IReadOnlyList<IGpuImage?> images, GpuImageUsage usage, uint width, uint height) {
        for (var index = 0; (index < images.Count); index++) {
            if ((images[index] is not { } image) || ((image.Usage & usage) != usage) || (image.Width != width) || (image.Height != height)) { return false; }
        }
        return true;
    }
    // Whether every carried buffer exists at the candidate's resolved size.
    private static bool EveryBufferFits(IReadOnlyList<IGpuBuffer?> buffers, ulong sizeBytes) {
        for (var index = 0; (index < buffers.Count); index++) {
            if ((buffers[index] is not { } buffer) || (buffer.SizeBytes != sizeBytes)) { return false; }
        }
        return true;
    }
    // Whether a plan's storage is one only its own graph reads, which a replacement may take over: not package-owned and
    // not published.
    private bool Carriable(ShaderPipelinePlan plan, ShaderPipelinePlannedStorage storage) =>
        (!m_packages.OwnsBuffer(plan: plan, storage: storage) && !Publishes(plan: plan, storage: storage));
    // Whether any instance of an installed storage is a publication a consumer may still read.
    private bool HoldsPublication(RuntimeResource resource) {
        foreach (var image in (resource.Images ?? [])) {
            if ((image is not null) && IsPublished(imageHandle: image.ImageHandle)) { return true; }
        }
        foreach (var buffer in (resource.Buffers ?? [])) {
            if ((buffer is not null) && IsPublishedBuffer(handle: buffer.BufferHandle)) { return true; }
        }

        return false;
    }

    // The usages every instance of an image storage is created with. A depth storage is a depth attachment and always
    // sampled: a package samples depths it draws inside its own recording, where the plan sees no access (a two-phase
    // selection's late step reads the target it drew, a far cascade's static layer among them), and a depth created
    // without Sampled has no shader resource view to bind (creating one removes a Direct3D 12 device). A color storage is
    // sampled and a storage image, which compute writes, zero clears and a publication in General need, and a color
    // attachment too when a planned access draws into it: a graphics pass's output or a package's color-attachment port.
    public static GpuImageUsage UsageOf(ShaderPipelinePlan plan, ShaderPipelinePlannedStorage storage) =>
        ((storage.Declaration.Kind == ShaderPipelineResourceKind.Depth)
            ? (GpuImageUsage.DepthAttachment | GpuImageUsage.Sampled)
            : GpuImageUsage.Sampled | GpuImageUsage.Storage | (Accessed(layout: GpuImageLayout.RenderTarget, plan: plan, storage: storage)
                ? GpuImageUsage.ColorAttachment
                : GpuImageUsage.None));

    // Whether a planned access takes a storage in a layout.
    private static bool Accessed(ShaderPipelinePlan plan, ShaderPipelinePlannedStorage storage, GpuImageLayout layout) =>
        plan.Passes.Any(predicate: pass => pass.Accesses.Any(predicate: access => ((access.Storage == storage.Index) && (access.Use.Layout == layout))));

    // The usages every instance of a buffer storage is created with: a storage buffer, and an indirect-argument buffer too
    // when a planned access reads it as an indirect dispatch's arguments.
    internal static GpuBufferUsage BufferUsageOf(ShaderPipelinePlan plan, ShaderPipelinePlannedStorage storage) =>
        GpuBufferUsage.Storage | (plan.Passes.Any(predicate: pass => pass.Accesses.Any(predicate: access => (
            (access.Storage == storage.Index) &&
            ((access.Use.Access & GpuAccess.IndirectCommandRead) != 0)
        )))
            ? GpuBufferUsage.Indirect
            : GpuBufferUsage.None);

    // A storage an installing graph takes from the installed one: the storage whose instances move into it.
    private readonly record struct CarriedStorage(RuntimeResource Old);

    private ShaderPipelineResourceStatus StatusOf(RuntimeResource resource) {
        var (width, height, bytes) = Footprint(
            count: resource.Count,
            counts: m_installedCounts,
            storage: resource.Storage
        );

        return new ShaderPipelineResourceStatus(
            resource.Spec.Name,
            resource.Spec.Kind,
            (resource.Borrowed ? 0UL : bytes),
            width,
            height,
            resource.Spec.IsExternal,
            resource.History
        );
    }

    /// <summary>Accounts installing a compiled pipeline now: at the requested extent, with the preview the current
    /// selection needs and its arrays bound to the rows <see cref="BindRows"/> bound, from everything the node owns. It
    /// is the account a candidate is refused by.</summary>
    /// <param name="pipeline">The compiled pipeline.</param>
    /// <returns>The pipeline's steady-state bytes and the replacement peak, against <see cref="BudgetBytes"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pipeline"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The pipeline publishes no output the selection or its default names, or
    /// declares an unsupported format.</exception>
    public ShaderPipelineMemoryAccount Account(CompiledShaderPipeline pipeline) {
        ArgumentNullException.ThrowIfNull(pipeline);

        var extent = (m_requestedWidth, m_requestedHeight);

        return Account(
            counts: CountsAt(
                extent: extent,
                plan: pipeline.Plan
            ),
            plan: pipeline.Plan,
            preview: PreviewFor(
                extent: extent,
                plan: pipeline.Plan
            ),
            rows: m_rows
        );
    }
}
