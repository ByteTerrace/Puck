using Puck.Abstractions.Pacing;
using Puck.Commands;
using Puck.Hosting;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>The one fixed-step shell for every boot shape. The composition supplies its post-step.</summary>
internal sealed class WorldSimulation(WorldHostStep step, Action afterInstances, WorldLinkBoundary boundary) : IFixedStepSimulation {
    /// <inheritdoc/>
    public IHostTickBoundary TickBoundary => boundary;

    /// <inheritdoc/>
    public bool AwaitsFrame => step.AwaitsFrame;

    /// <inheritdoc/>
    public bool HoldsClock(ulong withheldTicks) => step.HoldsClock(withheldTicks: withheldTicks);
    /// <inheritdoc/>
    public void SettleOwedFrames() => step.SettleOwedFrames();

    /// <inheritdoc/>
    public uint RatePerSecond => step.RatePerSecond;

    /// <inheritdoc/>
    public bool StepInterrupted => step.StepInterrupted;

    /// <inheritdoc/>
    public void Step(in FixedStepContext context, in CommandSnapshot commands) {
        if (!TryStep(in context, in commands)) { throw new InvalidOperationException("The host must resume the pending step."); }
    }

    /// <inheritdoc/>
    public bool TryStep(in FixedStepContext context, in CommandSnapshot commands) => step.TryStep(in context, afterInstances);
}
