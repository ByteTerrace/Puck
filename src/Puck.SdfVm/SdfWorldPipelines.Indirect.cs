using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Puck.Hosting;

namespace Puck.SdfVm;

public sealed partial class SdfWorldPipelines {
    /// <summary>Builds the cache's optional kernels in the existing reloadable slot table.</summary>
    public async Task BuildIndirectAsync(GpuPassPipelineCache cache, IGpuDeviceContext device, CancellationToken cancellationToken) {
        foreach (var kernel in new[] { SdfKernel.IndirectClassify, SdfKernel.IndirectTrace }) {
            GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline> wait;

            lock (m_gate) {
                ObjectDisposedException.ThrowIf(m_disposed, this);
                if (m_slots[((int)kernel)] is not { } slot) {
                    var description = SdfWorldTables.PipelineLayouts.Specs[((int)kernel)].Description;

                    slot = new Slot(description, cache.Acquire(device: device, key: GpuPassPipelineKey.OfCompute(bytecode: m_kernels[kernel], description: description)));
                    m_slots[((int)kernel)] = slot;
                }
                wait = cache.Acquire(device: device, key: slot.Lease.Key);
            }
            try { await wait.WaitAsync(cancellationToken).ConfigureAwait(false); } finally { wait.Release(); }
        }
    }
}
