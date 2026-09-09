using Puck.Commands;
using Puck.Hosting;

namespace Puck.World.Silo;

/// <summary>The silo's one <see cref="IFixedStepSimulation"/> — a master cadence at the fastest active row's rate,
/// draining activation work, outbound peer traffic, and every row's own step in that order.</summary>
public sealed class WorldSiloSimulation(WorldSiloHost host) : IFixedStepSimulation {
    /// <inheritdoc/>
    public uint RatePerSecond => host.MasterRateHz;

    /// <inheritdoc/>
    public void Step(in FixedStepContext context, in CommandSnapshot commands) {
        host.DrainActivationMailbox();
        host.Instances.DrainPendingTransfers();
        host.Instances.StepInstances(masterDeltaTicks: context.StepTicks);
        host.NoteMasterStep(stepTicks: context.StepTicks);
    }
}
