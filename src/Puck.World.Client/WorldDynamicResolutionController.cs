using System.Diagnostics;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Shaders;

namespace Puck.World.Client;

/// <summary>The fresh observation used by a view's dynamic-resolution controller.</summary>
public enum WorldResolutionSignal {
    /// <summary>No new observation was applied.</summary>
    None,
    /// <summary>The interval between confirmed presents supplied the load.</summary>
    PresentTiming,
    /// <summary>A completed primary march supplied the counted load.</summary>
    MarchSteps,
}
/// <summary>One view's presentation-only load controller. Confirmed presents take precedence; unavailable timing
/// falls back to the newest completed march. Repeated observations never move demand twice. Demand moves by at most
/// 1/16 down or 1/32 up per fresh observation, outside a ten-percent dead band. The shared extent quantizer resolves
/// demand with its existing shrink hysteresis; a resolved step may collect several bounded demand changes.</summary>
public sealed class WorldDynamicResolutionController {
    private static readonly int MarchColumn = GpuWork.SubmissionKinds.IndexOf(value: GpuWork.MarchSteps);
    private readonly GpuWorkSample m_sample = new();
    private IPresentTimingFeedback? m_timing;
    private PresentTimingSample m_previousPresent;
    private IGpuWorkSource? m_work;
    private long m_submission;
    private bool m_initialized;

    /// <summary>Gets the continuous demand, bounded before quantization.</summary>
    public float Demand { get; private set; }
    /// <summary>Gets the quantized render scale, held inside this view's floor and ceiling.</summary>
    public float Scale { get; private set; }
    /// <summary>Gets the fresh signal applied by the latest update, or none when demand was held.</summary>
    public WorldResolutionSignal Signal { get; private set; }
    /// <summary>Gets the load divided by its target for the last applied signal.</summary>
    public double Load { get; private set; }

    /// <summary>Forgets prior observations and starts the next update at its ceiling, without touching GPU resources.</summary>
    public void Reset() {
        m_initialized = false;
        m_timing = null;
        m_previousPresent = default;
        m_work = null;
        m_submission = 0;
        Signal = WorldResolutionSignal.None;
        Load = 0;
    }
    /// <summary>Consumes at most one fresh load observation and returns the view's resolved render scale.</summary>
    /// <param name="timing">An injectable confirmed-present source, or null when unavailable.</param>
    /// <param name="work">This view instance's completed GPU work, or null before installation.</param>
    /// <param name="displayHertz">The current display target, or zero when unknown; unknown uses counted work.</param>
    /// <param name="stepBudget">The view's budget derived from the committed floor-device ceilings.</param>
    /// <param name="floor">The minimum scale selected from the existing render-scale vocabulary.</param>
    /// <param name="ceiling">The authored maximum scale in (0, 1]. A lower ceiling also lowers the effective floor.</param>
    /// <param name="enabled">Whether the controller is enabled. False holds the ceiling and clears observations.</param>
    /// <returns>The scale for this frame, requiring no resource recreation or temporal reset.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A scale or budget is non-positive or not finite, or a scale exceeds one.</exception>
    public float Update(IPresentTimingFeedback? timing, IGpuWorkSource? work, int displayHertz, double stepBudget,
        float floor, float ceiling, bool enabled = true) {
        RequireScale(value: floor, name: nameof(floor));
        RequireScale(value: ceiling, name: nameof(ceiling));
        if (!double.IsFinite(d: stepBudget) || (stepBudget <= 0)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(stepBudget));
        }
        floor = MathF.Min(x: floor, y: ceiling);
        var resolvedFloor = ((float)RenderGraphExtent.Quantize(fraction: floor));
        var resolvedCeiling = ((float)RenderGraphExtent.Quantize(fraction: ceiling));
        Signal = WorldResolutionSignal.None;
        if (!enabled) {
            Reset();
            Demand = ceiling;
            Scale = resolvedCeiling;
            return Scale;
        }
        if (!m_initialized) {
            Demand = ceiling;
            Scale = resolvedCeiling;
            m_initialized = true;
        }
        Demand = Math.Clamp(value: Demand, min: floor, max: ceiling);
        Scale = Math.Clamp(value: Scale, min: resolvedFloor, max: resolvedCeiling);
        // Consume the work identity even while timing wins, so losing timing cannot replay an old completion.
        var hasSteps = TrySteps(work: work, steps: out var steps);
        if (!ReferenceEquals(objA: m_timing, objB: timing)) {
            m_timing = timing;
            m_previousPresent = default;
        }
        var present = timing?.LastPresentTiming ?? PresentTimingSample.Unavailable;
        if (present.IsAvailable && (displayHertz > 0)) {
            var previous = m_previousPresent;
            m_previousPresent = present;
            if (!previous.IsAvailable || (present.PresentCount <= previous.PresentCount) ||
                (present.PresentTimestampTicks <= previous.PresentTimestampTicks)) {
                return Scale;
            }
            Load = (((double)(present.PresentTimestampTicks - previous.PresentTimestampTicks)) * displayHertz /
                (Stopwatch.Frequency * ((double)(present.PresentCount - previous.PresentCount))));
            Signal = WorldResolutionSignal.PresentTiming;
        } else {
            m_previousPresent = default;
            if (!hasSteps) {
                return Scale;
            }
            Load = (steps / stepBudget);
            Signal = WorldResolutionSignal.MarchSteps;
        }
        if ((Load < 0.90) || (Load > 1.10)) {
            var target = ((Load <= 0) ? ceiling : (Demand / Math.Sqrt(d: Load)));
            Demand = ((float)Math.Clamp(value: target,
                min: Math.Max(val1: floor, val2: (Demand - (1f / 16f))),
                max: Math.Min(val1: ceiling, val2: (Demand + (1f / 32f)))));
            // A saturated demand must reach its bound; hysteresis cannot demand a value below the legal floor.
            Scale = ((Demand <= floor) ? resolvedFloor : ((Demand >= ceiling) ? resolvedCeiling :
                Math.Clamp(value: ((float)RenderGraphExtent.Quantize(fraction: Demand, allocated: Scale)), min: resolvedFloor, max: resolvedCeiling)));
        }
        return Scale;
    }
    private bool TrySteps(IGpuWorkSource? work, out long steps) {
        steps = 0;
        if (!ReferenceEquals(objA: m_work, objB: work)) {
            m_work = work;
            m_submission = 0;
        }
        if ((work is null) || !work.TryReadCompleted(sample: m_sample) || (m_sample.Submission <= m_submission)) {
            return false;
        }
        m_submission = m_sample.Submission;
        var marched = false;
        for (var pass = 0; (pass < m_sample.PassCount); pass++) {
            if (!m_sample.TryGetPassCount(pass: pass, column: MarchColumn, value: out var count)) {
                continue;
            }
            var label = m_sample.PassLabels[pass];
            marched |= (label.Equals(value: SdfWorldPackage.Parts.Primary, comparisonType: StringComparison.Ordinal) ||
                label.EndsWith(value: ("$" + SdfWorldPackage.Parts.Primary), comparisonType: StringComparison.Ordinal));
            steps = checked(steps + count);
        }
        // A retained-frame composite has no march observation; skipped work is not a measured zero.
        return marched;
    }
    private static void RequireScale(float value, string name) {
        if (!float.IsFinite(f: value) || (value <= 0) || (value > 1)) {
            throw new ArgumentOutOfRangeException(paramName: name);
        }
    }
}
