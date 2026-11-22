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
    public bool TryStep(in FixedStepContext context, in CommandSnapshot commands) {
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
        m_resumingRows = host.Instances.TryStepInstances(context.StepTicks, deadline) is not null;
        if (m_resumingRows) { return false; }
        host.PumpExtensions();
        host.NoteMasterStep(stepTicks: context.StepTicks);
        return true;
    }
}
