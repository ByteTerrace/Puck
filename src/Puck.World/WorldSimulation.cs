using Puck.Commands;
using Puck.Hosting;

namespace Puck.World;

/// <summary>The one fixed-step shell for every boot shape. The composition supplies its post-step.</summary>
internal sealed class WorldSimulation(WorldHostStep step, Action afterInstances) : IFixedStepSimulation {
    /// <inheritdoc/>
    public bool AwaitsFrame => step.AwaitsFrame;

    /// <inheritdoc/>
    public bool HoldsClock(ulong withheldTicks) => step.HoldsClock(withheldTicks: withheldTicks);
    /// <inheritdoc/>
    public void SettleOwedFrames() => step.SettleOwedFrames();

    /// <inheritdoc/>
    public uint RatePerSecond => step.RatePerSecond;

    /// <inheritdoc/>
    public void Step(in FixedStepContext context, in CommandSnapshot commands) => step.Step(afterInstances: afterInstances, context: in context);
}
