using System.Runtime.ExceptionServices;
using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.SdfVm;

// One node's or view's lease on the pipeline set its engines record with, from its composition's
// SdfWorldPipelineCache. Taking the lease — loading the deployed kernels when the holder supplies none, hashing them,
// and starting the shared build — runs on the thread pool, and the set itself builds there too, so a cold driver
// cache delays the first frame instead of freezing the pump that drains the console. The lease outlives the engines
// built from its set (a capacity or export rebuild reuses it) and is released on device loss and disposal. The holder
// builds its engines here (TryBuild), which refuses a failed build by name instead of throwing it.
internal sealed class SdfWorldPipelineSource(SdfWorldPipelineCache cache) {
    private readonly BackgroundBuild<SdfWorldPipelineLease> m_acquire = new();

    private SdfWorldPipelineLease? m_lease;
    // The latest refused engine build, until a build succeeds or the lease is released.
    private Exception? m_refusal;

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
    // Names where the holder's engine build stands while it has no engine: refused, not yet asked for, its lease still
    // being taken (the deployed kernels loading), or its set's build and that build's progress. Builds a string, so it
    // is read only to report.
    public string Describe() => ((m_refusal is { } refusal)
        ? $"the engine's build was refused and is retried on the next frame: {refusal.Message}"
        : ((m_lease is { } lease)
            ? $"the engine's pipeline set is {lease.Progress.Describe()}"
            : (m_acquire.IsPending
                ? "the engine's pipeline set is queued behind loading its kernels"
                : "the engine's pipeline set has not been requested: no frame has been produced"
            )
        )
    );
    // Waits out a lease still being taken and gives up the lease, which disposes the set when no other holder leases
    // it. Call after disposing every engine built from the set and before the device goes away.
    public void Release() {
        m_acquire.CancelAndWait(discard: static lease => lease.Release());
        m_lease?.Release();
        m_lease = null;
        m_refusal = null;
    }
    // Builds the holder's engine once its set is ready, or refuses the build. The set's lease, its build and the
    // engine's construction are one build here: while the set builds this returns null, and one that throws is refused
    // rather than thrown, so a creation failure never unwinds the frame that asked. A refused build has released
    // everything it created (the engine's construction owns that), keeps the lease, is named by Describe and once on
    // the error stream under the holder's label, and is tried again by the next call; the holder presents what it
    // already presented. A device loss is never refused: it reaches the host's recovery.
    public SdfWorldEngine? TryBuild<TState>(IGpuDeviceContext device, SdfWorldKernels? kernels, bool hostsOnDirectX, bool includeBrickPipelines, string label, TState state, Func<SdfWorldPipelines, TState, SdfWorldEngine> construct) {
        try {
            if (Poll(
                device: device,
                hostsOnDirectX: hostsOnDirectX,
                includeBrickPipelines: includeBrickPipelines,
                kernels: kernels
            ) is not { } pipelines) {
                return null;
            }

            var engine = construct(
                arg1: pipelines,
                arg2: state
            );

            m_refusal = null;

            return engine;
        } catch (Exception refusal) when ((refusal is not DeviceLostException)) {
            if (!string.Equals(
                a: m_refusal?.Message,
                b: refusal.Message,
                comparisonType: StringComparison.Ordinal
            )) {
                Console.Error.WriteLine(value: $"[{label}] engine build refused, retried on the next frame: {refusal.Message}");
            }

            m_refusal = refusal;

            return null;
        }
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
