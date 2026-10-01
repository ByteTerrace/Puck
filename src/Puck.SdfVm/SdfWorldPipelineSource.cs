using System.Runtime.ExceptionServices;
using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Shaders;

namespace Puck.SdfVm;

// One residency's leases on the pipelines its tables record with, from its composition's SdfWorldPipelineCatalog: the
// kernel variants (SdfWorldPipelines), the region copy and the mesh pass, every one an entry of the pass-pipeline cache.
// A holder supplying kernels takes its leases on the frame thread; one loading deployed kernels takes them on the
// thread pool. Every pipeline builds on the pool, so a cold driver cache delays the first frame instead of freezing
// the pump that drains the console. The leases outlive the tables built from them (a capacity or
// export rebuild reuses them) and are released on device loss and disposal. The holder builds its tables here
// (TryBuild), which refuses a failed build by name instead of throwing it, and tries it again only when an input it was
// built from changes. The set is ready once every pipeline but the views variants is built (SdfWorldPipelines.PollRequired),
// and the region copy and the mesh pass are too: the tables record their
// upload and mesh region with the first and a view's mesh pass draws with the second.
internal sealed class SdfWorldPipelineSource(SdfWorldPipelineCatalog catalog) {
    private readonly BackgroundBuild<Leases> m_acquire = new();

    private SdfWorldPipelines? m_lease;
    private GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline>? m_regionCopy;
    private GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline>? m_meshRaster;
    // Whether every pipeline the tables need has been seen ready: all of the leased set but the views variants, which
    // the tables dispatch only once the program's is built (SdfWorldTables.ViewsWaiting).
    private bool m_ready;
    // The latest refused engine build and what it was built from, until a build succeeds or the lease is released.
    private Exception? m_refusal;
    private long m_refusedHeapRevision;
    private object? m_refusedInputs;
    private BuildKey m_refusedKey;

    // The composition's pipeline catalog, whose pass-pipeline cache a reload leases replacements from.
    public SdfWorldPipelineCatalog Catalog => catalog;
    // The set, once every pipeline the tables need has built, or null before.
    public SdfWorldPipelines? Current => (m_ready ? m_lease : null);
    // The device's ready region-copy pipeline, or null before it has built.
    public IGpuComputePipeline? RegionCopy => m_regionCopy?.Current?.Compute;
    // The device's ready mesh pass pipeline, or null before it has built.
    public GpuPassPipeline? MeshRaster => m_meshRaster?.Current;

    // Returns the ready set once the region-copy and mesh pass pipelines are ready too; the first call starts taking the
    // leases and every call until all have built returns null. A lease or build that failed rethrows its exception here, on the
    // frame thread, so a device loss reaches the host's recovery; the next call starts again. Leases whose kernels are
    // already loaded are taken on this call: taking one joins the cache's entry or starts its build, and never waits,
    // so a holder whose pipelines another holder already built has its set in the frame that first asks.
    public SdfWorldPipelines? Poll(IGpuDeviceContext device, SdfKernelSet? kernels, bool hostsOnDirectX, bool includeBrickPipelines) {
        if (
            (m_lease is null) &&
            (kernels is not null) &&
            !m_acquire.IsPending
        ) {
            var leases = TakeLeases(
                device: device,
                hostsOnDirectX: hostsOnDirectX,
                includeBrickPipelines: includeBrickPipelines,
                kernels: kernels
            );

            m_lease = leases.Set;
            m_regionCopy = leases.RegionCopy;
            m_meshRaster = leases.MeshRaster;
        }
        if (m_lease is null) {
            if (!m_acquire.IsPending) {
                Start(
                    device: device,
                    hostsOnDirectX: hostsOnDirectX,
                    includeBrickPipelines: includeBrickPipelines,
                    kernels: kernels
                );
            }

            if (!m_acquire.TryTake(
                error: out var error,
                result: out var leases
            )) {
                return null;
            }

            if (error is not null) {
                ExceptionDispatchInfo.Throw(source: error);
            }

            m_lease = leases!.Set;
            m_regionCopy = leases.RegionCopy;
            m_meshRaster = leases.MeshRaster;
        }

        m_ready = m_lease.PollRequired();

        return ((!m_ready || (m_regionCopy!.Poll() is null) || (m_meshRaster!.Poll() is null))
            ? null
            : m_lease
        );
    }
    // Names where the holder's engine build stands while it has no engine: refused, not yet asked for, its lease still
    // being taken (the deployed kernels loading), or its set's build and that build's progress. Builds a string, so it
    // is read only to report.
    public string Describe() => ((m_refusal is { } refusal)
        ? $"the engine's build was refused and is retried when its inputs change{((refusal is GpuDescriptorHeapRefusalException)
            ? " or another owner returns descriptor heap space"
            : string.Empty)}: {refusal.Message}"
        : ((m_lease is { } lease)
            ? $"the engine's pipeline set is {lease.Describe()}"
            : (m_acquire.IsPending
                ? "the engine's pipeline set is queued behind loading its kernels"
                : "the engine's pipeline set has not been requested: no frame has been produced"
            )
        )
    );
    // Blocks until the builds the next Poll waits on have finished, taking nothing, so that Poll sees what it would had
    // the holder kept polling: the leases being taken or, once taken, the set's builds and the region copy's and mesh
    // pass's. Returns whether any was pending. Runs on the frame thread between polls.
    public bool WaitFinished(CancellationToken cancellationToken) {
        if (m_lease is null) {
            return m_acquire.WaitFinished(cancellationToken: cancellationToken);
        }

        var waited = m_lease.WaitFinished(cancellationToken: cancellationToken);

        waited |= m_regionCopy!.WaitFinished(cancellationToken: cancellationToken);
        waited |= m_meshRaster!.WaitFinished(cancellationToken: cancellationToken);

        return waited;
    }
    // Waits out leases still being taken and gives up the leases, which disposes each pipeline no other holder leases,
    // and forgets a refused build: the next build is tried afresh. Call after disposing every table built from the set and
    // before the device goes away.
    public void Release() {
        m_acquire.CancelAndWait(discard: static leases => leases.Release());
        m_lease?.Dispose();
        m_lease = null;
        m_ready = false;
        m_regionCopy?.Release();
        m_regionCopy = null;
        m_meshRaster?.Release();
        m_meshRaster = null;
        m_refusal = null;
        m_refusedInputs = null;
    }
    // Builds the holder's engine once its set is ready, or refuses the build. The set's lease, its build and the
    // engine's construction are one build here: while the set builds this returns null, and one that throws is refused
    // rather than thrown, so a creation failure never unwinds the frame that asked. A refused build has released
    // everything it created (the engine's construction owns that), keeps the lease, and is named by Describe and once on
    // the error stream under the holder's label; the holder presents what it already presented.
    //
    // A refused build is tried again only when something it was built from changes: the device, the kernels asked for,
    // the pipeline set or the kernels installed in it, the operator's GPU faults (GpuCreationFaults.Revision: an arm, a
    // disarm, or a fault firing elsewhere, never the one that refused this build), or the holder's own inputs (its engine options, and anything else
    // it names, such as a kernel reload request). A frame that changes none of them tries nothing, so a persistent
    // failure is attempted once per change, never once per frame, and never on a clock. A build the device's descriptor
    // heap refused (GpuDescriptorHeapRefusalException) has one input more, heap space: it is tried again when the heap's
    // release revision (IGpuBindings.HeapReleaseRevision) moves, as another owner returns its pools; a refusal of any
    // other kind never reads it. Release (a device loss or disposal) forgets the refusal. The inputs are read with inputsOf only when a build is due or a refusal is being
    // checked, never while the set builds. A device loss is never refused: it reaches the host's recovery.
    public SdfWorldTables? TryBuild<TState, TInputs>(IGpuDeviceContext device, SdfKernelSet? kernels, bool hostsOnDirectX, bool includeBrickPipelines, string label, TState state, Func<TState, TInputs> inputsOf, Func<SdfWorldPipelines, SdfWorldPassPipelines, TInputs, SdfWorldTables> construct) where TInputs : IEquatable<TInputs> {
        var key = new BuildKey(
            Device: device,
            FaultsRevision: (device.Services.Faults?.Revision ?? 0L),
            HostsOnDirectX: hostsOnDirectX,
            IncludeBrickPipelines: includeBrickPipelines,
            Kernels: kernels,
            MeshRaster: MeshRaster,
            RegionCopy: RegionCopy,
            Set: Current,
            SetKernels: Current?.Kernels
        );
        TInputs inputs = default!;
        var inputsRead = false;
        var heapRevision = device.Services.Bindings.HeapReleaseRevision;

        if (m_refusal is not null) {
            inputs = inputsOf(arg: state);
            inputsRead = true;

            if (
                (m_refusedKey == key) &&
                (m_refusedInputs is TInputs refused) &&
                inputs.Equals(other: refused) &&
                (
                    (m_refusal is not GpuDescriptorHeapRefusalException) ||
                    (heapRevision == m_refusedHeapRevision)
                )
            ) {
                return null;
            }
        }

        try {
            if (Poll(
                device: device,
                hostsOnDirectX: hostsOnDirectX,
                includeBrickPipelines: includeBrickPipelines,
                kernels: kernels
            ) is not { } pipelines) {
                return null;
            }

            if (!inputsRead) {
                inputs = inputsOf(arg: state);
                inputsRead = true;
            }

            var engine = construct(
                arg1: pipelines,
                arg2: new SdfWorldPassPipelines(
                    MeshRaster: MeshRaster!,
                    RegionCopy: RegionCopy!
                ),
                arg3: inputs
            );

            m_refusal = null;
            m_refusedInputs = null;

            return engine;
        } catch (Exception refusal) when ((refusal is not DeviceLostException)) {
            if (!string.Equals(
                a: m_refusal?.Message,
                b: refusal.Message,
                comparisonType: StringComparison.Ordinal
            )) {
                Console.Error.WriteLine(value: $"[{label}] engine build refused, retried when its inputs change: {refusal.Message}");
            }

            // The key is read again: the attempt may have taken the lease or installed the set it failed with, and a
            // creation fault that fired inside it moved the faults' revision, which is no change the build could retry on.
            m_refusal = refusal;
            m_refusedHeapRevision = heapRevision;
            m_refusedKey = (key with {
                FaultsRevision = (device.Services.Faults?.Revision ?? 0L),
                MeshRaster = MeshRaster,
                RegionCopy = RegionCopy,
                Set = Current,
                SetKernels = Current?.Kernels,
            });
            m_refusedInputs = (inputsRead
                ? inputs
                : inputsOf(arg: state)
            );

            return null;
        }
    }

    // What a build is made from besides the holder's own inputs; a refused build is tried again when any of it changes.
    private readonly record struct BuildKey(IGpuDeviceContext? Device, long FaultsRevision, SdfKernelSet? Kernels, bool HostsOnDirectX, bool IncludeBrickPipelines, GpuPassPipeline? MeshRaster, IGpuComputePipeline? RegionCopy, SdfWorldPipelines? Set, SdfKernelSet? SetKernels);

    // Kept apart from Poll so the closure is allocated only when a lease is taken, never on a polled frame. Only loading
    // the deployed kernels reads files, so only a holder with none of its own takes its leases on the thread pool.
    private void Start(IGpuDeviceContext device, SdfKernelSet? kernels, bool hostsOnDirectX, bool includeBrickPipelines) =>
        m_acquire.Start(build: _ => TakeLeases(
            device: device,
            hostsOnDirectX: hostsOnDirectX,
            includeBrickPipelines: includeBrickPipelines,
            kernels: kernels
        ));
    // Takes every lease a holder's engine needs. A pass-pipeline acquire that throws releases the leases taken before it.
    private Leases TakeLeases(IGpuDeviceContext device, SdfKernelSet? kernels, bool hostsOnDirectX, bool includeBrickPipelines) {
        var set = SdfWorldPipelines.Acquire(
            cache: catalog.Pipelines,
            device: device,
            includeBrickPipelines: includeBrickPipelines,
            kernels: (kernels ?? catalog.LoadDeployed(bytecodeExtension: SdfWorldRenderBuilder.BytecodeExtension(hostsOnDirectX: hostsOnDirectX)))
        );

        GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline>? regionCopy = null;

        try {
            regionCopy = catalog.RegionCopy.Acquire(device: device);

            return new Leases(
                MeshRaster: catalog.MeshRaster.Acquire(device: device),
                RegionCopy: regionCopy,
                Set: set
            );
        } catch {
            regionCopy?.Release();
            set.Dispose();
            throw;
        }
    }

    // The leases one acquire takes.
    private sealed record Leases(SdfWorldPipelines Set, GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline> RegionCopy, GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline> MeshRaster) {
        public void Release() {
            Set.Dispose();
            RegionCopy.Release();
            MeshRaster.Release();
        }
    }
}
// The pass pipelines an engine takes beside its set: the region copy its uploads record with and its mesh pass.
internal readonly record struct SdfWorldPassPipelines(IGpuComputePipeline RegionCopy, GpuPassPipeline MeshRaster);
