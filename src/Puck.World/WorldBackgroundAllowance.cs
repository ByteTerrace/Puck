using Puck.Hosting;
using Puck.SdfVm;
using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// Feeds the presentation's background GPU budget (<see cref="SdfGpuWorkBudget"/>) the frame's reading, once a frame on
/// the frame thread: the display's period (<see cref="FrameContext.DisplayHertz"/>) and the GPU time every node of the
/// render graph took, read back from their timestamps (a <see cref="WorldFrameLoadSource"/> over every node), so the
/// budget's share follows what the frame's own work leaves free (<see cref="SdfGpuWorkBudget.Observe"/>), with the
/// frame's span on the queue's timestamp clock, which the budget judges against the background jobs that ran ahead of it
/// (<see cref="SdfGpuWorkBudget.FramesMissedBehind"/>). It keeps the
/// timestamp demand standing while a display period is known (<see cref="WorldGpuTiming.RequireAllowance"/>). A frame
/// with no display period (an offscreen World) or no timed submission (a device without timestamps, or none read back
/// yet) leaves the budget at its fixed share and names why (<see cref="SdfGpuWorkBudget.Unread"/>).
/// </summary>
/// <param name="timing">The timestamp demand the nodes' GPU time is recorded under.</param>
/// <param name="load">The load of every node of the render graph.</param>
/// <param name="budget">Resolves the budget the presentation's background work draws on, on the first frame, once the
/// device is up.</param>
internal sealed class WorldBackgroundAllowance(WorldGpuTiming timing, IWorldFrameLoadSource load, Func<SdfGpuWorkBudget> budget) {
    private SdfGpuWorkBudget? m_budget;

    /// <summary>Reads one frame and hands it to the budget.</summary>
    /// <param name="context">The frame's context, whose display rate gives the period.</param>
    public void Advance(in FrameContext context) {
        var target = (m_budget ??= budget());

        if (!(context.DisplayHertz > 0)) {
            timing.RequireAllowance(required: false);
            target.Unread(periodSeconds: 0.0, reason: "no-display");
            return;
        }

        var period = (1.0 / context.DisplayHertz);

        timing.RequireAllowance(required: true);
        if (load.TryReadGpuFrame(reading: out var reading)) {
            target.Observe(frameSeconds: reading.Load, periodSeconds: period, queueEndSeconds: reading.QueueEnd, queueStartSeconds: reading.QueueStart);
        } else {
            target.Unread(periodSeconds: period, reason: "untimed");
        }
    }
}
