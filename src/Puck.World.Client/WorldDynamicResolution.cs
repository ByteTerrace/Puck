using System.Diagnostics;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;

namespace Puck.World.Client;

/// <summary>What moved a view's render grid on the latest frame (<see cref="WorldDynamicResolution.Signal"/>).</summary>
public enum WorldDynamicResolutionSignal {
    /// <summary>Dynamic resolution is off: the grid is the ceiling.</summary>
    Off,
    /// <summary>The GPU's own frame time: each newly read timed frame of the views against the display period.</summary>
    Gpu,
    /// <summary>The present timing, where the GPU's frame time is unavailable: each new present's interval against the
    /// display period.</summary>
    Present,
    /// <summary>The counted march steps against the step budget, where neither timing is available.</summary>
    Steps,
    /// <summary>A forced grid (<c>world.dynamic-resolution &lt;fraction&gt;</c>), which no load moves.</summary>
    Forced,
}
/// <summary>The load the dynamic-resolution controller reads each frame: the GPU's time for the world's views' latest timed
/// frame, the presenter's latest confirmed present, the counted march steps of the views' latest completed frame, and
/// the march steps an output pixel is budgeted. A host reports what it can read and nothing else.</summary>
public interface IWorldFrameLoadSource {
    /// <summary>Asks for the views' GPU frame time to be measured, or stops asking: timestamps cost a pool and a readback
    /// per frame in flight, so they are recorded only while a reader asks.</summary>
    /// <param name="required">Whether the controller reads the GPU's frame time.</param>
    void RequireGpuTiming(bool required);
    /// <summary>Reads the GPU's time for the world's views' latest timed frame.</summary>
    /// <param name="frame">The identity of the timed frame, which moves with each one.</param>
    /// <param name="seconds">The views' summed pass time on the GPU, in seconds.</param>
    /// <returns><see langword="true"/> when the device times the views and a timed frame was read.</returns>
    bool TryReadGpuFrame(out long frame, out double seconds);

    /// <summary>Gets the latest confirmed present, or <see cref="PresentTimingSample.Unavailable"/>.</summary>
    PresentTimingSample LastPresentTiming { get; }
    /// <summary>Gets the march steps a frame is budgeted per output pixel where present timing is unavailable, or zero for
    /// none.</summary>
    double MarchStepBudgetPerPixel { get; }

    /// <summary>Reads the counted march steps of the world's views' latest completed frame.</summary>
    /// <param name="frame">The identity of the completed frame the steps belong to, which moves with each one.</param>
    /// <param name="steps">The march steps the frame's views counted, summed over them.</param>
    /// <returns><see langword="true"/> when a completed frame was counted.</returns>
    bool TryReadMarchSteps(out long frame, out long steps);
}
/// <summary>
/// The one dynamic-resolution controller (rendering plan P15-6): each frame it chooses the render grid, a fraction of
/// the output on each axis, that every one of the presenter's own views renders at, between a floor and the
/// render-scale ceiling. The grid moves inside the allocation the ceiling sized, so no frame reallocates, rebuilds or
/// resets history; the presenter carries it as <c>SdfViewSnapshot.ResolvedRenderScale</c>, composed with a layout
/// transition's dip. Presentation only: it reads no simulation state and writes none.
/// <para>
/// It reads one load signal, and each fresh sample of it moves the grid through one response
/// (<see cref="Respond"/>). Where the display rate is known, the signal is the GPU's own frame time, a newly timed
/// frame's views' pass time against the display period; a present-paced display reads every kept present as exactly
/// its period, so only the GPU's time can show the headroom to raise the grid again. Where the device does not time the
/// views, it is the present timing, a new present's interval against the display period. Where neither is available,
/// it is the counted march steps: a newly completed frame's steps against the step budget, the floor-tier ceiling rows'
/// steps per output pixel (<see cref="StepBudgetPerPixel"/>) times the output's pixels, and without a budget the step
/// path holds the ceiling. A forced grid overrides all three.
/// </para>
/// </summary>
public sealed class WorldDynamicResolution {
    /// <summary>The relative distance from the budget inside which a sample leaves the grid where it is.</summary>
    public const double Deadband = 0.1;
    /// <summary>The largest share of the grid one sample lowers it by.</summary>
    public const double MaximumFall = (1d / 16d);
    /// <summary>The largest share of the grid one sample raises it by.</summary>
    public const double MaximumRise = (1d / 32d);
    /// <summary>The render node whose passes' counted march steps the step budget is derived from.</summary>
    public const string BudgetNode = "world";

    private long m_lastGpuFrame;
    private uint m_lastPresentCount;
    private long m_lastPresentTicks;
    private long m_lastStepsFrame;
    private float m_scale;

    /// <summary>Gets the grid the latest <see cref="Advance"/> chose, or zero before the first.</summary>
    public float Scale => m_scale;
    /// <summary>Gets what moved the grid on the latest <see cref="Advance"/>.</summary>
    public WorldDynamicResolutionSignal Signal { get; private set; }
    /// <summary>Gets the march-step budget the latest <see cref="Advance"/> held the frame to, or zero for none.</summary>
    public double StepBudget { get; private set; }

    /// <summary>Moves a grid by one fresh load sample. Within <see cref="Deadband"/> of the budget the grid holds; outside
    /// it the grid moves toward the scale whose area meets the budget, since the load scales with the grid's area, by at
    /// most <see cref="MaximumFall"/> or <see cref="MaximumRise"/> of itself, clamped between the floor and the
    /// ceiling. A sample of no load at all raises it by the most.</summary>
    /// <param name="scale">The grid the sample was taken at.</param>
    /// <param name="load">The sample: a present interval or a frame's march steps.</param>
    /// <param name="budget">What the sample is held to, in the load's unit.</param>
    /// <param name="floor">The lowest grid.</param>
    /// <param name="ceiling">The highest grid.</param>
    /// <returns>The next grid.</returns>
    public static float Respond(float scale, double load, double budget, float floor, float ceiling) {
        var low = Math.Min(val1: floor, val2: ceiling);
        var share = (load / budget);

        if (!double.IsFinite(d: share) || (share < 0d) || ((share >= (1d - Deadband)) && (share <= (1d + Deadband)))) {
            return Math.Clamp(max: ceiling, min: low, value: scale);
        }

        var next = Math.Clamp(
            max: (scale * (1d + MaximumRise)),
            min: (scale * (1d - MaximumFall)),
            value: (scale / Math.Sqrt(d: share))
        );

        return Math.Clamp(max: ceiling, min: low, value: ((float)next));
    }
    /// <summary>The march steps a frame is budgeted per output pixel: the ceilings the counters workload's run on
    /// <paramref name="backend"/> (or its first run, when none ran there) records for every pass of the
    /// <see cref="BudgetNode"/> node, summed, over the run's output pixels. Recording new floor evidence moves it.</summary>
    /// <param name="ceilings">The committed counters ceilings.</param>
    /// <param name="backend">The backend the presentation runs on.</param>
    /// <returns>The budget per output pixel, or zero for a document with no run.</returns>
    public static double StepBudgetPerPixel(WorldCountersCeilings ceilings, string? backend) {
        var run = (ceilings.Runs.FirstOrDefault(predicate: run => string.Equals(a: run.Backend, b: backend, comparisonType: StringComparison.Ordinal)) ?? ceilings.Runs.FirstOrDefault());

        if ((run is null) || (run.Width <= 0) || (run.Height <= 0)) {
            return 0d;
        }

        var steps = 0L;

        foreach (var ceiling in run.Ceilings) {
            if (
                string.Equals(a: ceiling.Node, b: BudgetNode, comparisonType: StringComparison.Ordinal) &&
                string.Equals(a: ceiling.Kind, b: GpuWork.MarchSteps.Name, comparisonType: StringComparison.Ordinal)
            ) {
                steps += ceiling.Ceiling;
            }
        }

        return (((double)steps) / (((long)run.Width) * run.Height));
    }
    /// <summary>Chooses this frame's render grid.</summary>
    /// <param name="load">The frame's load, or <see langword="null"/> for none, which holds the grid.</param>
    /// <param name="displayHertz">The display's presented frames a second, or zero when unknown.</param>
    /// <param name="ceiling">The render-scale ceiling, in (0, 1]: the grid never exceeds it.</param>
    /// <param name="floor">The lowest grid the load may move to; the ceiling when it is higher.</param>
    /// <param name="outputPixels">The output's pixels, which scale the step budget.</param>
    /// <param name="forced">A forced grid in (0, 1], which the load does not move, or zero for none.</param>
    /// <returns>The grid, in (0, <paramref name="ceiling"/>].</returns>
    public float Advance(IWorldFrameLoadSource? load, int displayHertz, float ceiling, float floor, long outputPixels, float forced) {
        if (forced > 0f) {
            Signal = WorldDynamicResolutionSignal.Forced;
            StepBudget = 0d;
            m_scale = Math.Min(val1: forced, val2: ceiling);

            return m_scale;
        }

        var scale = ((m_scale > 0f) ? Math.Clamp(value: m_scale, min: Math.Min(val1: floor, val2: ceiling), max: ceiling) : ceiling);
        var present = (load?.LastPresentTiming ?? PresentTimingSample.Unavailable);

        if ((displayHertz > 0) && (load is not null) && load.TryReadGpuFrame(frame: out var gpuFrame, seconds: out var gpuSeconds)) {
            Signal = WorldDynamicResolutionSignal.Gpu;
            StepBudget = 0d;

            if (gpuFrame != m_lastGpuFrame) {
                m_lastGpuFrame = gpuFrame;
                scale = Respond(budget: (1d / displayHertz), ceiling: ceiling, floor: floor, load: gpuSeconds, scale: scale);
            }
        } else if (present.IsAvailable && (displayHertz > 0)) {
            Signal = WorldDynamicResolutionSignal.Present;
            StepBudget = 0d;
            scale = AdvancePresent(ceiling: ceiling, displayHertz: displayHertz, floor: floor, present: present, scale: scale);
        } else {
            Signal = WorldDynamicResolutionSignal.Steps;
            StepBudget = ((load?.MarchStepBudgetPerPixel ?? 0d) * outputPixels);
            scale = AdvanceSteps(budget: StepBudget, ceiling: ceiling, floor: floor, load: load, scale: scale);
        }

        m_scale = scale;

        return scale;
    }

    // A new present's interval, per present since the last, against the display period; the first present only starts
    // the clock.
    private float AdvancePresent(PresentTimingSample present, int displayHertz, float ceiling, float floor, float scale) {
        var count = present.PresentCount;
        var ticks = present.PresentTimestampTicks;

        if (count == m_lastPresentCount) {
            return scale;
        }

        var started = (m_lastPresentTicks != 0L);
        var presents = unchecked((count - m_lastPresentCount));
        var interval = (((double)(ticks - m_lastPresentTicks)) / (presents * ((double)Stopwatch.Frequency)));

        m_lastPresentCount = count;
        m_lastPresentTicks = ticks;

        return (started
            ? Respond(budget: (1d / displayHertz), ceiling: ceiling, floor: floor, load: interval, scale: scale)
            : scale);
    }
    // A newly completed frame's counted steps against the budget; no budget holds the ceiling.
    private float AdvanceSteps(IWorldFrameLoadSource? load, double budget, float ceiling, float floor, float scale) {
        if (budget <= 0d) {
            return ceiling;
        }
        if (
            (load is null) ||
            !load.TryReadMarchSteps(frame: out var frame, steps: out var steps) ||
            (frame == m_lastStepsFrame)
        ) {
            return scale;
        }

        m_lastStepsFrame = frame;

        return Respond(budget: budget, ceiling: ceiling, floor: floor, load: steps, scale: scale);
    }
}
