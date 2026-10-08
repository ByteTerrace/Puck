using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Puck.Hosting;

namespace Puck.SdfVm;

public sealed partial class SdfWorldPipelines {
    /// <summary>Builds the cache's optional kernels in the existing reloadable slot table.</summary>
    public async Task BuildIndirectAsync(GpuPassPipelineCache cache, IGpuDeviceContext device, CancellationToken cancellationToken) {
        await BuildOptionalAsync(cache: cache, cancellationToken: cancellationToken, device: device, kernels: [SdfKernel.IndirectClassify, SdfKernel.IndirectTrace, SdfKernel.IndirectShade]).ConfigureAwait(continueOnCapturedContext: false);
    }
    /// <summary>Builds the depth-only camera kernels through the same optional reloadable slots.</summary>
    public async Task BuildLightViewsAsync(GpuPassPipelineCache cache, IGpuDeviceContext device, CancellationToken cancellationToken) {
        await BuildOptionalAsync(cache: cache, cancellationToken: cancellationToken, device: device, kernels: [SdfKernel.LightPrimary, SdfKernel.LightDepth]).ConfigureAwait(continueOnCapturedContext: false);
    }

    /// <summary>Leases the comparison receiver (<see cref="SdfKernel.ReceiverComparison"/>) into the set's slot table without
    /// waiting for it, while a view selects a comparison method. Repeated requests join the same slot; the set keeps it
    /// until disposal.</summary>
    /// <param name="cache">The composition's pass-pipeline cache.</param>
    /// <param name="device">The device this set was acquired on.</param>
    public void RequestComparison(GpuPassPipelineCache cache, IGpuDeviceContext device) {
        ArgumentNullException.ThrowIfNull(argument: cache);
        ArgumentNullException.ThrowIfNull(argument: device);

        lock (m_gate) {
            ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
            if (m_slots[((int)SdfKernel.ReceiverComparison)] is null) {
                var description = SdfWorldTables.PipelineLayouts.Specs[((int)SdfKernel.ReceiverComparison)].Description;

                m_slots[((int)SdfKernel.ReceiverComparison)] = new Slot(description: description, lease: cache.Acquire(device: device,
                    key: GpuPassPipelineKey.OfCompute(bytecode: m_kernels[SdfKernel.ReceiverComparison], description: description)));
            }
        }
    }

    private async Task BuildOptionalAsync(IReadOnlyList<SdfKernel> kernels, GpuPassPipelineCache cache, IGpuDeviceContext device, CancellationToken cancellationToken) {
        foreach (var kernel in kernels) {
            GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline> wait;

            lock (m_gate) {
                ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
                if (m_slots[((int)kernel)] is not { } slot) {
                    var description = SdfWorldTables.PipelineLayouts.Specs[((int)kernel)].Description;

                    slot = new Slot(description: description, lease: cache.Acquire(device: device, key: GpuPassPipelineKey.OfCompute(bytecode: m_kernels[kernel], description: description)));
                    m_slots[((int)kernel)] = slot;
                }
                wait = cache.Acquire(device: device, key: slot.Lease.Key);
            }
            try { await wait.WaitAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false); } finally { wait.Release(); }
        }
    }
}
