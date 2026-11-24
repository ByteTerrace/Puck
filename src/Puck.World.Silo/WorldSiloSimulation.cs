using Puck.Abstractions.Waiting;
using Puck.Commands;
using Puck.Hosting;

namespace Puck.World.Silo;

/// <summary>The silo's one <see cref="IFixedStepSimulation"/> — a master cadence at the fastest active row's rate,
/// draining activation work, outbound peer traffic, every row's own step, and every row's extension runtime in that
/// order.</summary>
public sealed class WorldSiloSimulation(WorldSiloHost host) : IFixedStepSimulation {
    private bool m_resumingRows;

    /// <inheritdoc/>
    public Puck.Abstractions.Pacing.IHostTickBoundary TickBoundary => host.Instances.Boundary;
    /// <inheritdoc/>
    public uint RatePerSecond => host.MasterRateHz;
    /// <inheritdoc/>
    public bool AwaitsFrame => false;

    /// <inheritdoc/>
    public bool HoldsClock(ulong withheldTicks) => false;
    /// <inheritdoc/>
    public void SettleOwedFrames() { }
    /// <inheritdoc/>
    public void Step(in FixedStepContext context, in CommandSnapshot commands) {
        if (!TryStep(in context, in commands)) { throw new InvalidOperationException("The silo must resume the pending step."); }
    }

    /// <inheritdoc/>
    /// <remarks>An exception from the step is recorded on the host, whose health names it until the host loop it
    /// faulted stops the process.</remarks>
    public bool TryStep(in FixedStepContext context, in CommandSnapshot commands) {
        try {
            return TryStepCore(in context);
        } catch (Exception fault) {
            host.NotePumpFault(fault: fault);
            throw;
        }
    }

    private bool TryStepCore(in FixedStepContext context) {
        // Activation work (checkpoints, releases, deactivations) runs only at a tick boundary, never against a row
        // whose tick is held across wait slices.
        if (!m_resumingRows) {
            host.DrainActivationMailbox();
            if (host.IsDraining) { return true; }
            if (!host.ReleaseAdmissionOpen) {
                // A candidate receives pump heartbeats for private health while the durable group barrier is closed.
                host.NoteMasterStep(stepTicks: context.StepTicks);
                return true;
            }
            host.Instances.DrainPendingTransfers();
        }
        var deadline = WaitDeadline.In(HostWaits.PumpAllowance ?? TimeSpan.FromSeconds(30));
        var held = host.Instances.TryStepInstances(context.StepTicks, deadline);

        host.NotePumpWait(reason: held);
        m_resumingRows = held is not null;
        if (m_resumingRows) { return false; }
        host.PumpExtensions();
        host.NoteMasterStep(stepTicks: context.StepTicks);
        return true;
    }
}
