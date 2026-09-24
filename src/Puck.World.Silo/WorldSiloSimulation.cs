using Puck.Commands;
using Puck.Hosting;

namespace Puck.World.Silo;

/// <summary>The silo's one <see cref="IFixedStepSimulation"/> — a master cadence at the fastest active row's rate,
/// draining activation work, outbound peer traffic, every row's own step, and every row's extension runtime in that
/// order.</summary>
public sealed class WorldSiloSimulation(WorldSiloHost host) : IFixedStepSimulation {
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
        host.DrainActivationMailbox();
        if (host.IsDraining) { return; }
        if (!host.ReleaseAdmissionOpen) {
            // A candidate still receives pump heartbeats for private health, but no simulation step, transfer drain,
            // forwarding, or external effect may run before the durable group barrier opens.
            host.NoteMasterStep(stepTicks: context.StepTicks);
            return;
        }
        // A managed candidate may pump privately while its durable group remains closed. Pending transfers and
        // forwarding are external effects, so they stay frozen until the same publication gate opens the row door.
        if (host.ReleaseAdmissionOpen) { host.Instances.DrainPendingTransfers(); }
        host.Instances.StepInstances(masterDeltaTicks: context.StepTicks);
        host.PumpExtensions();
        host.NoteMasterStep(stepTicks: context.StepTicks);
    }
}
