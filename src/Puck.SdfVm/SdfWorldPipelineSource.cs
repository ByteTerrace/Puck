using System.Runtime.ExceptionServices;
using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.SdfVm;

// One node's or view's lease on the pipeline set its engines record with, from its composition's
// SdfWorldPipelineCache. Taking the lease — loading the deployed kernels when the holder supplies none, hashing them,
// and starting the shared build — runs on the thread pool, and the set itself builds there too, so a cold driver
// cache delays the first frame instead of freezing the pump that drains the console. The lease outlives the engines
// built from its set (a capacity or export rebuild reuses it) and is released on device loss and disposal.
internal sealed class SdfWorldPipelineSource(SdfWorldPipelineCache cache) {
    private readonly BackgroundBuild<SdfWorldPipelineLease> m_acquire = new();

    private SdfWorldPipelineLease? m_lease;

    // The ready set, or null before the lease's set has built.
    public SdfWorldPipelines? Current => m_lease?.Current;

    // Returns the ready set; the first call starts taking the lease and every call until the set has built returns
    // null. A lease or build that failed rethrows its exception here, on the frame thread, so a device loss reaches the
    // host's recovery; the next call starts again.
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
                result: out var lease
            )) {
                return null;
            }

            if (error is not null) {
                ExceptionDispatchInfo.Throw(source: error);
            }

            m_lease = lease!;
        }

        return m_lease.Poll();
    }
    // Waits out a lease still being taken and gives up the lease, which disposes the set when no other holder leases
    // it. Call after disposing every engine built from the set and before the device goes away.
    public void Release() {
        m_acquire.CancelAndWait(discard: static lease => lease.Release());
        m_lease?.Release();
        m_lease = null;
    }
    // Takes the set out of sharing when this is its only holder, so a reload may replace its pipelines in place.
    public bool TryMakePrivate() =>
        (m_lease?.TryMakePrivate() ?? false);

    // Kept apart from Poll so the closure is allocated only when a lease is taken, never on a polled frame.
    private void Start(IGpuDeviceContext device, SdfWorldKernels? kernels, bool hostsOnDirectX, bool includeBrickPipelines) =>
        m_acquire.Start(build: _ => cache.Acquire(
            device: device,
            includeBrickPipelines: includeBrickPipelines,
            kernels: (kernels ?? cache.LoadDeployed(bytecodeExtension: SdfWorldRenderBuilder.BytecodeExtension(hostsOnDirectX: hostsOnDirectX)))
        ));
}
