using System.Diagnostics;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Shaders;

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
/// <summary>The load the dynamic-resolution controller reads each frame, from the world's views: the GPU's time for their
/// renders timed since the last reading, the presenter's latest confirmed present, the counted march steps of their
/// renders completed since the last reading, and the march steps an output pixel is budgeted. Each reading names the
/// grid its renders recorded their passes at. A host reports what it can read and nothing else.</summary>
public interface IWorldFrameLoadSource {
    /// <summary>Asks for the views' GPU frame time to be measured, or stops asking: timestamps cost a pool and a readback
    /// per frame in flight, so they are recorded only while a reader asks.</summary>
    /// <param name="required">Whether the controller reads the GPU's frame time.</param>
    void RequireGpuTiming(bool required);
    /// <summary>Reads the GPU's time for the views' renders timed since the previous reading: each view's newest timed
    /// render not read before, its summed pass time in seconds, summed over the views.</summary>
    /// <param name="reading">The reading, which is not fresh when no view was timed since the previous one.</param>
    /// <returns><see langword="true"/> when the device times the views.</returns>
    bool TryReadGpuFrame(out WorldFrameLoadReading reading);

    /// <summary>Gets the latest confirmed present, or <see cref="PresentTimingSample.Unavailable"/>.</summary>
    PresentTimingSample LastPresentTiming { get; }
    /// <summary>Gets the march steps a frame is budgeted per output pixel where present timing is unavailable, or zero for
    /// none.</summary>
    double MarchStepBudgetPerPixel { get; }

    /// <summary>Reads the counted march steps of the views' renders completed since the previous reading: each view's
    /// newest completed render not read before, summed over the views.</summary>
    /// <param name="reading">The reading, which is not fresh when no view completed a render since the previous one.</param>
    /// <returns><see langword="true"/> when a view's completed render was counted.</returns>
    bool TryReadMarchSteps(out WorldFrameLoadReading reading);
    /// <summary>Reads and clears every view's renders completed since the previous read, summarized over all of them
    /// (<see cref="ShaderPipelineCompletions.Then"/>): every render, not only each view's newest, so the summary names a
    /// grid only when every one of them recorded it.</summary>
    /// <returns>The completions since the previous read; none when the views' completions cannot be read.</returns>
    ShaderPipelineCompletions TakeCompletions();
}
/// <summary>
/// The one dynamic-resolution controller: each frame it chooses the render grid, a fraction of the output on each axis,
/// that every one of the presenter's own views renders at, between a floor and the render-scale ceiling. The grid moves
/// inside the allocation the ceiling sized, so no frame reallocates, rebuilds or resets history; the presenter carries
/// it as <c>SdfViewSnapshot.ResolvedRenderScale</c>, composed with a layout transition's dip. Presentation only: it
/// reads no simulation state and writes none.
/// <para>
/// It reads one load signal, and each fresh sample of it moves the grid through one response (<see cref="Respond"/>).
/// Where the display rate is known, the signal is the GPU's own frame time, the views' newly timed renders' pass time
/// against the display period; a present-paced display reads every kept present as exactly its period, so only the
/// GPU's time can show the headroom to raise the grid again. Where the device does not time the views, it is the
/// present timing, a new present's interval against the display period. Where neither is available, it is the counted
/// march steps: the views' newly completed renders' steps against the step budget, the floor-tier ceiling rows' steps
/// per output pixel (<see cref="StepBudgetPerPixel"/>) times the output's pixels, and without a budget the step path
/// holds the ceiling. A forced grid overrides all three.
/// </para>
/// <para>
/// The views render the grid quantized (<see cref="GridOf"/>), so a sample is taken only at the grid the views are
/// rendering now: a reading whose renders recorded another grid, as a reading delayed past a grid move does, moves
/// nothing. A present names no frame, so a present interval is a sample only when the views' completed renders were
/// all at the current grid from the present that started it to the one that ends it, as their completion summaries
/// (<see cref="IWorldFrameLoadSource.TakeCompletions"/>) state. When the budget falls between two
/// adjacent grids the controller settles on the cheaper one: a sample over the budget marks its grid
/// (<see cref="OverGrid"/>), and a rise stops below the marked grid until a sample predicts the marked grid within the
/// budget, its share scaled by the two grids' area ratio, which clears the mark. The grids are dyadic, so that
/// prediction is compared exactly, inclusive at the budget.
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

    private double m_completedGrid;
    private uint m_lastPresentCount;
    private long m_lastPresentTicks;
    private double m_overGrid;
    private double m_presentGrid;
    private float m_scale;

    /// <summary>Gets the scale the latest <see cref="Advance"/> chose, or zero before the first.</summary>
    public float Scale => m_scale;
    /// <summary>Gets the grid the views render the latest <see cref="Advance"/>'s scale at (<see cref="GridOf"/>), or zero
    /// before the first.</summary>
    public double Grid { get; private set; }
    /// <summary>Gets the lowest grid a sample measured over the budget, which no rise reaches until a sample predicts it
    /// within the budget, or zero for none.</summary>
    public double OverGrid => m_overGrid;
    /// <summary>Gets what moved the grid on the latest <see cref="Advance"/>.</summary>
    public WorldDynamicResolutionSignal Signal { get; private set; }
    /// <summary>Gets the march-step budget the latest <see cref="Advance"/> held the frame to, or zero for none.</summary>
    public double StepBudget { get; private set; }

    /// <summary>Returns the grid the views render a scale at: the scale quantized as the render graph quantizes an
    /// extent (<see cref="RenderGraphExtent.Quantize(double)"/>), no larger than the quantized ceiling.</summary>
    /// <param name="scale">The scale, in (0, 1].</param>
    /// <param name="ceiling">The render-scale ceiling, in (0, 1].</param>
    /// <returns>The grid, as a fraction of the output on each axis.</returns>
    public static double GridOf(float scale, float ceiling) => Math.Min(
        val1: RenderGraphExtent.Quantize(fraction: scale),
        val2: RenderGraphExtent.Quantize(fraction: ceiling)
    );
    /// <summary>Moves a grid by one fresh load sample. Within <see cref="Deadband"/> of the budget, both bounds included,
    /// the grid holds; outside it the grid moves toward the scale whose area meets the budget, since the load scales with
    /// the grid's area, by at most <see cref="MaximumFall"/> or <see cref="MaximumRise"/> of itself, clamped between the
    /// floor and the ceiling. A sample of no load at all raises it by the most.</summary>
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
    /// <returns>The scale, in (0, <paramref name="ceiling"/>], which the views render at its grid
    /// (<see cref="GridOf"/>).</returns>
    public float Advance(IWorldFrameLoadSource? load, int displayHertz, float ceiling, float floor, long outputPixels, float forced) {
        if (forced > 0f) {
            Signal = WorldDynamicResolutionSignal.Forced;
            StepBudget = 0d;
            m_overGrid = 0d;
            m_presentGrid = 0d;

            return Choose(ceiling: ceiling, scale: Math.Min(val1: forced, val2: ceiling));
        }

        var scale = ((m_scale > 0f) ? Math.Clamp(value: m_scale, min: Math.Min(val1: floor, val2: ceiling), max: ceiling) : ceiling);
        var present = (load?.LastPresentTiming ?? PresentTimingSample.Unavailable);

        if ((displayHertz > 0) && (load is not null) && load.TryReadGpuFrame(reading: out var frame)) {
            Signal = WorldDynamicResolutionSignal.Gpu;
            StepBudget = 0d;
            m_presentGrid = 0d;
            scale = Take(budget: (1d / displayHertz), ceiling: ceiling, floor: floor, reading: frame, scale: scale);
        } else if (present.IsAvailable && (displayHertz > 0)) {
            Signal = WorldDynamicResolutionSignal.Present;
            StepBudget = 0d;
            scale = AdvancePresent(ceiling: ceiling, displayHertz: displayHertz, floor: floor, load: load, present: present, scale: scale);
        } else {
            Signal = WorldDynamicResolutionSignal.Steps;
            StepBudget = ((load?.MarchStepBudgetPerPixel ?? 0d) * outputPixels);
            m_presentGrid = 0d;
            scale = AdvanceSteps(budget: StepBudget, ceiling: ceiling, floor: floor, load: load, scale: scale);
        }

        return Choose(ceiling: ceiling, scale: scale);
    }

    private float Choose(float scale, float ceiling) {
        m_scale = scale;
        Grid = GridOf(ceiling: ceiling, scale: scale);

        return scale;
    }
    // A new present's interval, per present since the last, against the display period. The views' completed renders
    // name the grid: an interval is a sample only when every render completed from its start to its end was at the
    // current grid, and otherwise only restarts the clock.
    private float AdvancePresent(IWorldFrameLoadSource? load, PresentTimingSample present, int displayHertz, float ceiling, float floor, float scale) {
        if ((load?.TakeCompletions() is { Renders: > 0 } completed)) {
            m_completedGrid = completed.Grid;

            if (completed.Grid != m_presentGrid) {
                m_presentGrid = 0d;
            }
        }

        var count = present.PresentCount;

        if (count == m_lastPresentCount) {
            return scale;
        }

        var current = GridOf(ceiling: ceiling, scale: scale);
        var sampled = ((m_presentGrid != 0d) && (m_presentGrid == current));
        var presents = unchecked((count - m_lastPresentCount));
        var interval = (((double)(present.PresentTimestampTicks - m_lastPresentTicks)) / (presents * ((double)Stopwatch.Frequency)));

        m_lastPresentCount = count;
        m_lastPresentTicks = present.PresentTimestampTicks;
        m_presentGrid = ((m_completedGrid == current) ? current : 0d);

        return (sampled
            ? Take(budget: (1d / displayHertz), ceiling: ceiling, floor: floor, reading: new WorldFrameLoadReading(Grid: current, Load: interval, Renders: 1), scale: scale)
            : scale);
    }
    // The views' newly completed renders' counted steps against the budget; no budget holds the ceiling.
    private float AdvanceSteps(IWorldFrameLoadSource? load, double budget, float ceiling, float floor, float scale) {
        if (budget <= 0d) {
            return ceiling;
        }
        if ((load is null) || !load.TryReadMarchSteps(reading: out var steps)) {
            return scale;
        }

        return Take(budget: budget, ceiling: ceiling, floor: floor, reading: steps, scale: scale);
    }
    // One sample. Only a fresh reading at the grid the views render now is one; an over-budget sample marks its grid,
    // and a rise onto the marked grid stops at the current grid unless the sample, scaled by the grids' area ratio,
    // predicts the marked grid within the budget.
    private float Take(WorldFrameLoadReading reading, double budget, float ceiling, float floor, float scale) {
        var current = GridOf(ceiling: ceiling, scale: scale);

        if (!reading.IsFresh || (reading.Grid != current)) {
            return scale;
        }

        var share = (reading.Load / budget);
        var next = Respond(budget: budget, ceiling: ceiling, floor: floor, load: reading.Load, scale: scale);

        if (double.IsFinite(d: share) && (share > (1d + Deadband))) {
            m_overGrid = current;
        } else if ((next > scale) && (m_overGrid > 0d)) {
            // The grids are dyadic, so their areas are exact; the comparison of the products is exact too.
            if (ProductAtMost(a: reading.Load, b: (m_overGrid * m_overGrid), c: budget, d: (current * current))) {
                m_overGrid = 0d;
            } else if (GridOf(ceiling: ceiling, scale: next) >= m_overGrid) {
                next = Math.Max(val1: scale, val2: Math.Min(val1: ((float)current), val2: ceiling));
            }
        }

        return next;
    }
    // Whether a * b <= c * d exactly, for finite non-negative doubles: each product of two 53-bit significands is exact
    // in 106 bits, and the two are compared at their binary exponents without rounding.
    private static bool ProductAtMost(double a, double b, double c, double d) {
        var (left, leftExponent) = Product(x: a, y: b);
        var (right, rightExponent) = Product(x: c, y: d);

        if (left == UInt128.Zero) {
            return true;
        }
        if (right == UInt128.Zero) {
            return false;
        }

        // The product with the higher top bit is the larger; with equal top bits, align the exponents and compare.
        var leftTop = ((128 - ((int)UInt128.LeadingZeroCount(value: left))) + leftExponent);
        var rightTop = ((128 - ((int)UInt128.LeadingZeroCount(value: right))) + rightExponent);

        if (leftTop != rightTop) {
            return (leftTop < rightTop);
        }

        return ((leftExponent >= rightExponent)
            ? ((left << (leftExponent - rightExponent)) <= right)
            : (left <= (right << (rightExponent - leftExponent))));
    }
    // A product of two finite non-negative doubles as an exact significand and binary exponent.
    private static (UInt128 Significand, int Exponent) Product(double x, double y) {
        var (xs, xe) = Decompose(value: x);
        var (ys, ye) = Decompose(value: y);

        return ((((UInt128)xs) * ys), (xe + ye));
    }
    private static (ulong Significand, int Exponent) Decompose(double value) {
        var bits = BitConverter.DoubleToUInt64Bits(value: value);
        var exponent = ((int)((bits >> 52) & 0x7FFUL));
        var fraction = bits & 0xFFFFFFFFFFFFFUL;

        return ((exponent == 0)
            ? (fraction, -1074)
            : (fraction | (1UL << 52), (exponent - 1075)));
    }
}
