using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Puck.Hosting;

namespace Puck.SdfVm;

public sealed partial class SdfWorldPipelines {
    /// <summary>Builds the optional reconstruction pipeline into the set's existing reloadable slot table. Native views
    /// never call this. The set owns the slot until disposal; repeated requests join the same slot and cache lease.</summary>
    /// <param name="cache">The composition's pass-pipeline cache.</param>
    /// <param name="device">The device this set was acquired on.</param>
    /// <param name="cancellationToken">Cancels this background wait, leaving the set's lease intact.</param>
    /// <returns>A task that completes once the pipeline is built; the wait holds no thread.</returns>
    /// <remarks>Call on a package's background build after its residency is ready. The kernel is the set's own: the
    /// deployed tree's, which is the host's own build and is reflected by nobody, as boot reflects none of the others,
    /// or one a committed reload installed, which <see cref="PrepareReload"/> reflected and held to the host's interface
    /// before it leased anything. So building it needs no shader toolchain. A reload that races activation also
    /// prepares changed optional bytecode, so its existing atomic swap cannot leave an active slot on stale
    /// bytecode.</remarks>
    public async Task BuildResolveAsync(GpuPassPipelineCache cache, IGpuDeviceContext device, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(argument: cache);
        ArgumentNullException.ThrowIfNull(argument: device);
        GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline> wait;

        lock (m_gate) {
            ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
            if (m_slots[((int)SdfKernel.Resolve)] is not { } slot) {
                var description = SdfWorldTables.PipelineLayouts.Specs[((int)SdfKernel.Resolve)].Description;

                slot = new Slot(description: description, lease: cache.Acquire(device: device,
                    key: GpuPassPipelineKey.OfCompute(bytecode: m_kernels[SdfKernel.Resolve], description: description)));
                m_slots[((int)SdfKernel.Resolve)] = slot;
            }
            // Keep a temporary lease while the package build waits. A concurrent reload may retire the slot's
            // lease, but cannot cancel or dispose the build this waiter still holds. Replacements publish ready.
            wait = cache.Acquire(device: device, key: slot.Lease.Key);
        }
        try {
            _ = await wait.WaitAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        } finally {
            wait.Release();
        }
    }
}
