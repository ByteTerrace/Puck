using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Puck.Hosting;

namespace Puck.SdfVm;

public sealed partial class SdfWorldPipelines {
    /// <summary>Returns whether a kernel's pipeline joins a set only when a view needs it, rather than up front: the
    /// reconstruction kernel, which only reduced or temporal views run, and the binned views kernels, which only a view
    /// of a frame running authored programs runs.</summary>
    /// <param name="kernel">The kernel.</param>
    /// <returns><see langword="true"/> for a kernel a view acquires on demand.</returns>
    public static bool AcquiredOnDemand(SdfKernel kernel) =>
        (kernel is SdfKernel.Resolve or SdfKernel.ViewsFilter or SdfKernel.ViewsBins or SdfKernel.ViewsShade or SdfKernel.ViewsShadePlain);

    /// <summary>Builds on-demand pipelines (<see cref="AcquiredOnDemand"/>) into the set's existing reloadable slot table.
    /// The set owns each slot until disposal; repeated requests join the same slot and cache lease.</summary>
    /// <param name="cache">The composition's pass-pipeline cache.</param>
    /// <param name="device">The device this set was acquired on.</param>
    /// <param name="kernels">The on-demand kernels to build.</param>
    /// <param name="cancellationToken">Cancels this background wait, leaving the set's leases intact.</param>
    /// <returns>A task that completes once every pipeline is built; the wait holds no thread.</returns>
    /// <remarks>Call on a package's background build after its residency is ready. Each kernel is the set's own: the
    /// deployed tree's, which is the host's own build and is reflected by nobody, as boot reflects none of the others,
    /// or one a committed reload installed, which <see cref="PrepareReload"/> reflected and held to the host's interface
    /// before it leased anything. So building one needs no shader toolchain. A reload that races activation also
    /// prepares changed on-demand bytecode, so its existing atomic swap cannot leave an active slot on stale
    /// bytecode.</remarks>
    /// <exception cref="ArgumentException">A kernel of <paramref name="kernels"/> is leased up front.</exception>
    public async Task BuildOnDemandAsync(GpuPassPipelineCache cache, IGpuDeviceContext device, IReadOnlyList<SdfKernel> kernels, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(argument: cache);
        ArgumentNullException.ThrowIfNull(argument: device);
        ArgumentNullException.ThrowIfNull(argument: kernels);
        var waits = new List<GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline>>(capacity: kernels.Count);

        try {
            lock (m_gate) {
                // Keep a temporary lease while the package build waits. A concurrent reload may retire a slot's lease,
                // but cannot cancel or dispose the build this waiter still holds. Replacements publish ready.
                foreach (var kernel in kernels) {
                    waits.Add(item: cache.Acquire(device: device, key: SlotOf(cache: cache, device: device, kernel: kernel).Lease.Key));
                }
            }
            foreach (var wait in waits) {
                _ = await wait.WaitAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            }
        } finally {
            GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline>.Release(leases: [.. waits]);
        }
    }
    /// <summary>Leases the on-demand pipelines a view needs into the set's slot table without waiting for them: a set a
    /// view follows into in place from another set that has them (<see cref="SdfWorldPasses"/>) takes the same
    /// pass-pipeline cache entries, already built, so the slots are ready at once. A kernel whose slot the set holds is
    /// left as it is.</summary>
    /// <param name="cache">The composition's pass-pipeline cache.</param>
    /// <param name="device">The device this set was acquired on.</param>
    /// <param name="kernels">The on-demand kernels to lease.</param>
    /// <exception cref="ArgumentException">A kernel of <paramref name="kernels"/> is leased up front.</exception>
    public void RequestOnDemand(GpuPassPipelineCache cache, IGpuDeviceContext device, IReadOnlyList<SdfKernel> kernels) {
        ArgumentNullException.ThrowIfNull(argument: cache);
        ArgumentNullException.ThrowIfNull(argument: device);
        ArgumentNullException.ThrowIfNull(argument: kernels);

        lock (m_gate) {
            foreach (var kernel in kernels) { _ = SlotOf(cache: cache, device: device, kernel: kernel); }
        }
    }
    /// <summary>Returns the on-demand kernels whose pipelines the set holds built.</summary>
    /// <returns>The kernels, in <see cref="SdfKernel"/> order.</returns>
    public IReadOnlyList<SdfKernel> OnDemandBuilt() => [.. SdfKernelSet.Kernels.Where(predicate: kernel => (AcquiredOnDemand(kernel: kernel) && (OptionalPipeline(kernel: kernel) is not null)))];

    // An on-demand kernel's slot, leased from the cache the first time it is asked for. Called under the gate.
    private Slot SlotOf(GpuPassPipelineCache cache, IGpuDeviceContext device, SdfKernel kernel) {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
        if (!AcquiredOnDemand(kernel: kernel)) {
            throw new ArgumentException(message: $"The '{SdfKernelSet.StemOf(kernel: kernel)}' pipeline is leased up front.", paramName: nameof(kernel));
        }
        if (m_slots[((int)kernel)] is not { } slot) {
            var description = SdfWorldTables.PipelineLayouts.Specs[((int)kernel)].Description;

            slot = new Slot(description: description, lease: cache.Acquire(device: device,
                key: GpuPassPipelineKey.OfCompute(bytecode: m_kernels[kernel], description: description)));
            m_slots[((int)kernel)] = slot;
        }
        return slot;
    }
}
