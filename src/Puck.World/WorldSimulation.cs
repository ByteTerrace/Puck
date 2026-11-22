using Puck.Abstractions.Pacing;
using Puck.Commands;
using Puck.Hosting;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>The one fixed-step shell for every boot shape. The composition supplies its post-step and its collect.</summary>
internal sealed class WorldSimulation(WorldHostStep step, Action afterInstances, WorldLinkBoundary boundary, Action collect) : IFixedStepSimulation {
    private readonly WorldLinkBoundary m_boundary = Collecting(boundary: boundary, collect: collect, step: step);

    /// <inheritdoc/>
    public IHostTickBoundary TickBoundary => m_boundary;

    // The boundary window the loop opens around each control phase and each step call. Closing it with no step in
    // progress runs the collect first, still inside the window: whatever the window let the pump change (a console
    // grant or revoke, a load, a completed step) is copied for the frame before any frame reads it, so a withdrawn
    // admission never shows for a frame after the authority withdrew it. That holds while a tick executes beside the
    // frame too: the console runs only at a tick boundary, so its window closes, and collects, before the step that
    // follows hands its tick over. While a step is in progress the frame keeps the values of the boundary before it,
    // and the call that completes the step collects.
    private static WorldLinkBoundary Collecting(WorldLinkBoundary boundary, WorldHostStep step, Action collect) {
        boundary.PublishOnClose(publish: () => {
            if (!step.StepInProgress) { collect(); }
        });

        return boundary;
    }

    /// <inheritdoc/>
    public bool AwaitsFrame => step.AwaitsFrame;
    /// <inheritdoc/>
    public bool NextStepOwesFrame => step.NextStepOwesFrame;

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
    public bool TryStep(in FixedStepContext context, in CommandSnapshot commands) => step.TryStep(in context, afterInstances, collectBy: null);

    /// <inheritdoc/>
    public bool TryStep(in FixedStepContext context, in CommandSnapshot commands, Puck.Abstractions.Waiting.WaitDeadline collectBy) =>
        step.TryStep(in context, afterInstances, collectBy: collectBy);

    /// <inheritdoc/>
    public void WakeOnStepProgress(Action? wake) => step.WakeOnStepProgress(wake: wake);
}
