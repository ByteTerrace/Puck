using System.Runtime.ExceptionServices;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.SdfVm;

/// <summary>
/// The <see cref="SdfWorldPipelines"/> sets the SDF engine nodes and views of one composition share: one set per
/// device, kernel set (<see cref="SdfWorldKernels.ContentKey"/>) and brick-pipeline choice, however many nodes and views
/// render with it. A holder takes an <see cref="SdfWorldPipelineLease"/>; the first lease on a key starts the set's
/// build on the thread pool (<see cref="BackgroundBuild{T}"/>), every lease polls the same build from the frame thread,
/// and the set is disposed when its last lease is released, after any build still in flight has returned.
/// <para>
/// The cache counts the shader modules and pipelines it creates into <see cref="Work"/>, a
/// <see cref="GpuWorkLedger"/> named <see cref="WorkSourceName"/>, including those a reload creates for a set; the
/// holders' own ledgers count none of them. It also reads each backend's deployed kernel set once
/// (<see cref="LoadDeployed"/>) and keeps it for its whole life.
/// </para>
/// <para>
/// Acquiring and loading are safe on any thread; the holders poll and release on the thread that renders. A device
/// recreated in place after a loss keeps its identity, so every holder releases its lease on device loss, and a set
/// is never handed to a lease on the recreated device.
/// </para>
/// </summary>
public sealed class SdfWorldPipelineCache {
    /// <summary>The name a counters report heads <see cref="Work"/>'s section with.</summary>
    public const string WorkSourceName = "gpu.sdf-pipelines";

    private readonly Dictionary<string, SdfWorldKernels> m_deployed = new(comparer: StringComparer.Ordinal);
    private readonly Lock m_deployedGate = new();
    private readonly List<SdfWorldPipelineLease.Entry> m_entries = [];
    private readonly Lock m_gate = new();
    private readonly GpuWorkLedger m_work = new(
        framesInFlight: 1,
        name: WorkSourceName
    );

    /// <summary>Gets the number of sets a new lease can join: every set with a lease, less any a reload made private
    /// to its node.</summary>
    public int SharedSets {
        get {
            lock (m_gate) {
                return m_entries.Count;
            }
        }
    }
    /// <summary>Gets the shader modules and pipelines the cache's sets have created, over the cache's whole life.</summary>
    public IWorkCounterSource Work => m_work;

    private void StartBuild(SdfWorldPipelineLease.Entry entry) =>
        entry.Build.Start(build: token => SdfWorldPipelines.Build(
            cancellationToken: token,
            device: entry.Device,
            includeBrickPipelines: entry.IncludesBrickPipelines,
            kernels: entry.Kernels,
            ledger: m_work
        ));

    /// <summary>Takes a lease on the set for <paramref name="kernels"/> on <paramref name="device"/>, joining the set
    /// another holder already leases or starting its build on the thread pool. Safe on any thread; it hashes the kernel
    /// set, so a holder on the frame thread calls it from its own background work.</summary>
    /// <param name="device">The device the set is created on, through its services.</param>
    /// <param name="kernels">The compiled kernel set for the device's backend.</param>
    /// <param name="includeBrickPipelines">Whether the set includes the brick bake and upload pipelines, for an engine
    /// with a brick pool. A set with them and one without are different sets.</param>
    /// <returns>The lease, which the caller releases once no engine records with its set.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> is <see langword="null"/>.</exception>
    public SdfWorldPipelineLease Acquire(IGpuDeviceContext device, SdfWorldKernels kernels, bool includeBrickPipelines) {
        ArgumentNullException.ThrowIfNull(device);

        var key = kernels.ContentKey();

        lock (m_gate) {
            foreach (var entry in m_entries) {
                if (
                    ReferenceEquals(
                        objA: entry.Device,
                        objB: device
                    ) &&
                    (entry.IncludesBrickPipelines == includeBrickPipelines) &&
                    string.Equals(
                        a: entry.Key,
                        b: key,
                        comparisonType: StringComparison.Ordinal
                    )
                ) {
                    entry.Holders++;

                    return new SdfWorldPipelineLease(
                        cache: this,
                        entry: entry
                    );
                }
            }

            var created = new SdfWorldPipelineLease.Entry(
                device: device,
                includesBrickPipelines: includeBrickPipelines,
                kernels: kernels,
                key: key
            );

            m_entries.Add(item: created);
            StartBuild(entry: created);

            return new SdfWorldPipelineLease(
                cache: this,
                entry: created
            );
        }
    }
    /// <summary>Returns the deployed kernel set for a backend, reading it from <see cref="SdfWorldKernels.DefaultDirectory"/>
    /// on the first call for that backend and returning the same set on every later call. Safe on any thread; the first
    /// call reads files, so a holder on the frame thread calls it from its own background work.</summary>
    /// <param name="bytecodeExtension">The compiled-kernel extension (<c>".spv"</c> for Vulkan, <c>".dxil"</c> for Direct3D 12).</param>
    /// <returns>The deployed kernel set.</returns>
    /// <exception cref="ArgumentException"><paramref name="bytecodeExtension"/> is empty.</exception>
    /// <exception cref="IOException">A kernel file is missing or cannot be read.</exception>
    public SdfWorldKernels LoadDeployed(string bytecodeExtension) {
        ArgumentException.ThrowIfNullOrEmpty(bytecodeExtension);

        lock (m_deployedGate) {
            if (!m_deployed.TryGetValue(
                key: bytecodeExtension,
                value: out var kernels
            )) {
                kernels = SdfWorldKernels.Load(bytecodeExtension: bytecodeExtension);
                m_deployed.Add(
                    key: bytecodeExtension,
                    value: kernels
                );
            }

            return kernels;
        }
    }

    internal SdfWorldPipelines? Poll(SdfWorldPipelineLease.Entry entry) {
        lock (m_gate) {
            if (entry.Pipelines is { } ready) {
                return ready;
            }

            if (!entry.Build.IsPending) {
                StartBuild(entry: entry);
            }

            if (!entry.Build.TryTake(
                error: out var error,
                result: out var built
            )) {
                return null;
            }

            // A failed build leaves nothing pending, so the next poll by any holder starts a fresh one.
            if (error is not null) {
                ExceptionDispatchInfo.Throw(source: error);
            }

            entry.Pipelines = built;

            return built;
        }
    }
    internal void Release(SdfWorldPipelineLease.Entry entry) {
        lock (m_gate) {
            if (--entry.Holders > 0) {
                return;
            }

            _ = m_entries.Remove(item: entry);
            entry.Build.CancelAndWait(discard: static pipelines => pipelines.Dispose());
            entry.Pipelines?.Dispose();
            entry.Pipelines = null;
        }
    }
    internal bool TryMakePrivate(SdfWorldPipelineLease.Entry entry) {
        lock (m_gate) {
            if (entry.Holders != 1) {
                return false;
            }

            _ = m_entries.Remove(item: entry);

            return true;
        }
    }
}
/// <summary>
/// One holder's share of an <see cref="SdfWorldPipelineCache"/> set. The holder polls it from the frame thread until
/// the set is ready, keeps it across engine rebuilds, and releases it on device loss and disposal after disposing
/// every engine built from the set.
/// </summary>
public sealed class SdfWorldPipelineLease {
    private SdfWorldPipelineCache? m_cache;

    private readonly Entry m_entry;

    internal SdfWorldPipelineLease(SdfWorldPipelineCache cache, Entry entry) {
        m_cache = cache;
        m_entry = entry;
    }

    /// <summary>Gets the ready set, or <see langword="null"/> before its build has completed and after the lease is
    /// released.</summary>
    public SdfWorldPipelines? Current => ((m_cache is null)
        ? null
        : m_entry.Pipelines
    );
    /// <summary>Gets whether the lease has been released.</summary>
    public bool IsReleased => (m_cache is null);

    /// <summary>Returns the ready set, or <see langword="null"/> while its build runs. A build that failed rethrows its
    /// exception here, on the frame thread, so a device loss reaches the host's recovery; the next poll starts a fresh
    /// build. Allocates nothing while the build runs or once the set is ready.</summary>
    /// <returns>The ready set, or <see langword="null"/> while it builds.</returns>
    /// <exception cref="ObjectDisposedException">The lease has been released.</exception>
    public SdfWorldPipelines? Poll() {
        var cache = m_cache;

        ObjectDisposedException.ThrowIf(
            condition: (cache is null),
            instance: this
        );

        return cache!.Poll(entry: m_entry);
    }
    /// <summary>Gives up the lease. The last lease on a set waits out a build still in flight, discarding its result,
    /// and disposes the set; call it after disposing every engine built from the set and before the device goes away.
    /// Releasing twice does nothing.</summary>
    public void Release() {
        if (Interlocked.Exchange(
            location1: ref m_cache,
            value: null
        ) is { } cache) {
            cache.Release(entry: m_entry);
        }
    }

    // Takes the set out of sharing when this lease is its only holder, so a reload may replace its pipelines in place:
    // no other engine records with it, and no later lease joins it.
    internal bool TryMakePrivate() =>
        ((m_cache is { } cache) && cache.TryMakePrivate(entry: m_entry));

    // One set and its holders. Every field but the immutable key is read and written under the cache's gate.
    internal sealed class Entry(IGpuDeviceContext device, SdfWorldKernels kernels, string key, bool includesBrickPipelines) {
        public BackgroundBuild<SdfWorldPipelines> Build { get; } = new();
        public IGpuDeviceContext Device { get; } = device;
        public int Holders { get; set; } = 1;
        public bool IncludesBrickPipelines { get; } = includesBrickPipelines;
        public string Key { get; } = key;
        public SdfWorldKernels Kernels { get; } = kernels;

        public SdfWorldPipelines? Pipelines { get; set; }
    }
}
