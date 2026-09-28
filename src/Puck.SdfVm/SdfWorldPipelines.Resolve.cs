using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Puck.Hosting;

namespace Puck.SdfVm;

public sealed partial class SdfWorldPipelines {
    /// <summary>Builds an optional reconstruction pipeline into the set's existing reloadable slot table. Native views
    /// never call this. The set owns the slot until disposal; repeated requests join the same slot and cache lease.</summary>
    /// <param name="kernel">The optional kernel to acquire.</param>
    /// <param name="cache">The composition's pass-pipeline cache.</param>
    /// <param name="device">The device this set was acquired on.</param>
    /// <param name="reflector">The reflector holding the optional kernel to its declared interface.</param>
    /// <param name="cancellationToken">Cancels this background wait, leaving the set's lease intact.</param>
    /// <remarks>Call on a package's background build after its residency is ready. A reload that races activation also
    /// prepares changed optional bytecode, so its existing atomic swap cannot leave an active slot on stale bytecode.</remarks>
    public void BuildOptional(SdfKernel kernel, GpuPassPipelineCache cache, IGpuDeviceContext device, ShaderBytecodeReflector reflector, CancellationToken cancellationToken) {
        if (!SdfKernelSet.IsOptional(kernel)) { throw new ArgumentOutOfRangeException(nameof(kernel)); }
        ArgumentNullException.ThrowIfNull(argument: cache);
        ArgumentNullException.ThrowIfNull(argument: device);
        ArgumentNullException.ThrowIfNull(argument: reflector);
        GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline> wait;
        Slot slot;

        lock (m_gate) {
            ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
            if (m_slots[((int)kernel)] is { } existing) {
                slot = existing;
            } else {
                var bytes = m_kernels[kernel];

                if (SdfKernelSet.InterfaceMismatch(kernel: kernel, reflected: reflector.Read(bytecode: bytes.Span)) is { } mismatch) {
                    throw new InvalidOperationException(message: $"'{SdfKernelSet.StemOf(kernel)}': {mismatch}");
                }
                var description = SdfWorldTables.PipelineLayouts.Specs[((int)kernel)].Description;

                slot = new Slot(description: description, lease: cache.Acquire(device: device,
                    key: GpuPassPipelineKey.OfCompute(bytecode: bytes, description: description)));
                m_slots[((int)kernel)] = slot;
            }
            // Keep a temporary lease while the package build waits. A concurrent reload may retire the slot's
            // lease, but cannot cancel or dispose the build this waiter still holds. Replacements publish ready.
            wait = cache.Acquire(device: device, key: slot.Lease.Key);
        }
        try {
            _ = wait.Wait(cancellationToken: cancellationToken);
        } finally {
            wait.Release();
        }
    }
}
