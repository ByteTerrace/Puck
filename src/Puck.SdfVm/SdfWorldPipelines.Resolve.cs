using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Puck.Hosting;

namespace Puck.SdfVm;

public sealed partial class SdfWorldPipelines {
    /// <summary>Joins ready optional pipelines a followed residency already owns, without building, reflecting or
    /// waiting. A missing or differently compiled source slot stays absent and takes the ordinary package-build path.</summary>
    /// <param name="source">The set whose installed passes are about to follow this set.</param>
    /// <remarks>Both sets must belong to the same cache and device. Each joined slot gets its own lease before the
    /// source can release it, and only matching current bytecode is installed, including across a concurrent reload.</remarks>
    public void JoinReadyOptional(SdfWorldPipelines source) {
        ArgumentNullException.ThrowIfNull(argument: source);
        if (ReferenceEquals(objA: source, objB: this) || !ReferenceEquals(objA: source.m_cache, objB: m_cache) ||
            !ReferenceEquals(objA: source.m_device, objB: m_device)) { return; }
        foreach (var kernel in SdfKernelSet.Kernels) {
            if (!SdfKernelSet.IsOptional(kernel: kernel)) { continue; }
            var index = ((int)kernel);
            ReadOnlyMemory<byte> bytes;

            lock (m_gate) {
                if (m_disposed || (m_slots[index] is not null)) { continue; }
                bytes = m_kernels[kernel];
            }
            GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline> lease;

            lock (source.m_gate) {
                if (source.m_disposed || (source.m_slots[index] is not { } slot) || (slot.Lease.Current is null) ||
                    !bytes.Span.SequenceEqual(other: slot.Lease.Key.Primary.Span)) { continue; }
                // The ready source still holds this exact key in this cache on this device. Acquire can only join it.
                lease = m_cache.Acquire(device: m_device, key: slot.Lease.Key);
            }
            var installed = false;

            lock (m_gate) {
                if (!m_disposed && (m_slots[index] is null) && bytes.Span.SequenceEqual(other: m_kernels[kernel].Span)) {
                    m_slots[index] = new Slot(description: SdfWorldTables.PipelineLayouts.Specs[index].Description, lease: lease);
                    installed = true;
                }
            }
            if (!installed) { lease.Release(); }
        }
    }

    private void RequireOwnership(GpuPassPipelineCache cache, IGpuDeviceContext device) {
        if (!ReferenceEquals(objA: cache, objB: m_cache)) {
            throw new ArgumentException(message: "The pipeline set belongs to a different cache.", paramName: nameof(cache));
        }
        if (!ReferenceEquals(objA: device, objB: m_device)) {
            throw new ArgumentException(message: "The pipeline set belongs to a different device.", paramName: nameof(device));
        }
    }

    /// <summary>Builds an optional reconstruction pipeline into the set's existing reloadable slot table. Fragments
    /// without optional kernels never call this. The set owns the slot until disposal; repeated requests join the same
    /// slot and cache lease.</summary>
    /// <param name="kernel">The optional kernel to acquire.</param>
    /// <param name="cache">The composition's pass-pipeline cache.</param>
    /// <param name="device">The device this set was acquired on.</param>
    /// <param name="reflector">The reflector holding the optional kernel to its declared interface.</param>
    /// <param name="cancellationToken">Cancels this background wait, leaving the set's lease intact.</param>
    /// <exception cref="ArgumentException">The cache or device is not the one the set was acquired from.</exception>
    /// <remarks>Call on a package's background build after its residency is ready. A reload that races activation also
    /// prepares changed optional bytecode, so its existing atomic swap cannot leave an active slot on stale bytecode.</remarks>
    public void BuildOptional(SdfKernel kernel, GpuPassPipelineCache cache, IGpuDeviceContext device, ShaderBytecodeReflector reflector, CancellationToken cancellationToken) {
        if (!SdfKernelSet.IsOptional(kernel: kernel)) { throw new ArgumentOutOfRangeException(paramName: nameof(kernel)); }
        ArgumentNullException.ThrowIfNull(argument: cache);
        ArgumentNullException.ThrowIfNull(argument: device);
        ArgumentNullException.ThrowIfNull(argument: reflector);
        RequireOwnership(cache: cache, device: device);
        GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline> wait;
        Slot slot;

        lock (m_gate) {
            ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
            if (m_slots[((int)kernel)] is { } existing) {
                slot = existing;
            } else {
                var bytes = m_kernels[kernel];

                if (SdfKernelSet.InterfaceMismatch(kernel: kernel, reflected: reflector.Read(bytecode: bytes.Span)) is { } mismatch) {
                    throw new InvalidOperationException(message: $"'{SdfKernelSet.StemOf(kernel: kernel)}': {mismatch}");
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
