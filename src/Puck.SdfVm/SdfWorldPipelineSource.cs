using System.Runtime.ExceptionServices;
using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Shaders;

namespace Puck.SdfVm;

// One node's or view's lease on the pipeline set its engines record with, from its composition's
// SdfWorldPipelineCache. Taking the lease — loading the deployed kernels when the holder supplies none, hashing them,
// and starting the shared build — runs on the thread pool, and the set itself builds there too, so a cold driver
// cache delays the first frame instead of freezing the pump that drains the console. The lease outlives the engines
// built from its set (a capacity or export rebuild reuses it) and is released on device loss and disposal. The holder
// builds its engines here (TryBuild), which refuses a failed build by name instead of throwing it, and tries it again only
// when an input it was built from changes.
//
// The holder also leases its device's region-copy pipeline from the cache's GpuRegionCopyPass and its mesh pass pipeline
// from the cache's SdfMeshRasterPass in the same background acquire, and the set is ready only once both pipelines are
// too: an engine records its table upload and mesh region with the first and its mesh pass with the second.
internal sealed class SdfWorldPipelineSource(SdfWorldPipelineCache cache) {
    private readonly BackgroundBuild<Leases> m_acquire = new();

    private GpuBuildLease<SdfWorldPipelineKey, SdfWorldPipelines>? m_lease;
    private GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline>? m_regionCopy;
    private GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline>? m_meshRaster;
    // The latest refused engine build and what it was built from, until a build succeeds or the lease is released.
    private Exception? m_refusal;
    private long m_refusedHeapRevision;
    private object? m_refusedInputs;
    private BuildKey m_refusedKey;

    // The ready set, or null before the lease's set has built.
    public SdfWorldPipelines? Current => m_lease?.Current;
    // The device's ready region-copy pipeline, or null before it has built.
    public IGpuComputePipeline? RegionCopy => m_regionCopy?.Current?.Compute;
    // The device's ready mesh pass pipeline, or null before it has built.
    public GpuPassPipeline? MeshRaster => m_meshRaster?.Current;

    // Returns the ready set once the region-copy and mesh pass pipelines are ready too; the first call starts taking both leases and
    // every call until both have built returns null. A lease or build that failed rethrows its exception here, on the
    // frame thread, so a device loss reaches the host's recovery; the next call starts again.
    public SdfWorldPipelines? Poll(IGpuDeviceContext device, SdfWorldKernels? kernels, bool hostsOnDirectX, bool includeBrickPipelines) {
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

        var set = m_lease.Poll();

        return (((m_regionCopy!.Poll() is null) || (m_meshRaster!.Poll() is null))
            ? null
            : set
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
            ? $"the engine's pipeline set is {lease.Key.Progress.Describe()}"
            : (m_acquire.IsPending
                ? "the engine's pipeline set is queued behind loading its kernels"
                : "the engine's pipeline set has not been requested: no frame has been produced"
            )
        )
    );
    // Waits out a lease still being taken and gives up the lease, which disposes the set when no other holder leases
    // it, and forgets a refused build: the next build is tried afresh. Call after disposing every engine built from the
    // set and before the device goes away.
    public void Release() {
        m_acquire.CancelAndWait(discard: static leases => leases.Release());
        m_lease?.Release();
        m_lease = null;
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
    public SdfWorldEngine? TryBuild<TState, TInputs>(IGpuDeviceContext device, SdfWorldKernels? kernels, bool hostsOnDirectX, bool includeBrickPipelines, string label, TState state, Func<TState, TInputs> inputsOf, Func<SdfWorldPipelines, SdfWorldPassPipelines, TInputs, SdfWorldEngine> construct) where TInputs : IEquatable<TInputs> {
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
    // Takes the set out of sharing when this is its only holder, so a reload may replace its pipelines in place.
    public bool TryMakePrivate() =>
        (m_lease?.TryMakePrivate() ?? false);

    // What a build is made from besides the holder's own inputs; a refused build is tried again when any of it changes.
    private readonly record struct BuildKey(IGpuDeviceContext? Device, long FaultsRevision, SdfWorldKernels? Kernels, bool HostsOnDirectX, bool IncludeBrickPipelines, GpuPassPipeline? MeshRaster, IGpuComputePipeline? RegionCopy, SdfWorldPipelines? Set, SdfWorldKernels? SetKernels);

    // Kept apart from Poll so the closure is allocated only when a lease is taken, never on a polled frame. A pass-pipeline
    // acquire that throws releases the leases taken before it.
    private void Start(IGpuDeviceContext device, SdfWorldKernels? kernels, bool hostsOnDirectX, bool includeBrickPipelines) =>
        m_acquire.Start(build: _ => {
            var set = cache.Acquire(
                device: device,
                includeBrickPipelines: includeBrickPipelines,
                kernels: (kernels ?? cache.LoadDeployed(bytecodeExtension: SdfWorldRenderBuilder.BytecodeExtension(hostsOnDirectX: hostsOnDirectX)))
            );

            GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline>? regionCopy = null;

            try {
                regionCopy = cache.RegionCopy.Acquire(device: device);

                return new Leases(
                    MeshRaster: cache.MeshRaster.Acquire(device: device),
                    RegionCopy: regionCopy,
                    Set: set
                );
            } catch {
                regionCopy?.Release();
                set.Release();
                throw;
            }
        });

    // The three leases one background acquire takes.
    private sealed record Leases(GpuBuildLease<SdfWorldPipelineKey, SdfWorldPipelines> Set, GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline> RegionCopy, GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline> MeshRaster) {
        public void Release() {
            Set.Release();
            RegionCopy.Release();
            MeshRaster.Release();
        }
    }
}
// The pass pipelines an engine takes beside its set: the region copy its uploads record with and its mesh pass.
internal readonly record struct SdfWorldPassPipelines(IGpuComputePipeline RegionCopy, GpuPassPipeline MeshRaster);
