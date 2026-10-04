using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Puck.Hosting;

namespace Puck.SdfVm;

public sealed partial class SdfWorldPipelines {
    /// <summary>Builds the cache's optional kernels in the existing reloadable slot table.</summary>
    public async Task BuildIndirectAsync(GpuPassPipelineCache cache, IGpuDeviceContext device, CancellationToken cancellationToken) {
        await BuildOptionalAsync(kernels: [SdfKernel.IndirectClassify, SdfKernel.IndirectTrace, SdfKernel.IndirectShade], cache: cache, device: device, cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
    }
    /// <summary>Builds the depth-only camera kernels through the same optional reloadable slots.</summary>
    public async Task BuildLightViewsAsync(GpuPassPipelineCache cache, IGpuDeviceContext device, CancellationToken cancellationToken) {
        await BuildOptionalAsync(kernels: [SdfKernel.LightPrimary, SdfKernel.LightDepth], cache: cache, device: device, cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
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
