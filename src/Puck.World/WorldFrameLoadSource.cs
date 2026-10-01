using System.Text.Json;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Shaders;
using Puck.World.Client;

namespace Puck.World;

/// <summary>The load the dynamic-resolution controller reads in a presentation with a render graph: the world's own views'
/// (<c>world</c>, <c>world$2</c> on) GPU frame time, the latest timed frame's summed pass time from the pass timestamps
/// <see cref="WorldGpuTiming"/> records while the controller asks; the presenter's present timing, when the host's
/// presenter reports it (a windowed World's swapchain; an offscreen World presents nothing, so it reports none); the
/// counted march steps the views recorded in their latest completed submissions; and their budget per output pixel from
/// the counters ceilings the floor tier committed (<c>tests/Puck.Counters/counters.ceilings.json</c>, compiled in as
/// <see cref="CeilingsResource"/>) for the device's backend. A reading's frame is the first view's. Reading allocates
/// nothing.</summary>
/// <param name="presentTiming">Resolves the presenter's present timing, on the first read, or <see langword="null"/> for a
/// host whose presenter reports none.</param>
/// <param name="backend">Resolves the backend the device runs, on the first budget read, or <see langword="null"/> before
/// a device is up.</param>
/// <param name="timing">The timestamp demand the views' GPU frame time is recorded under.</param>
/// <param name="probe">The render probe, whose root runtime holds the views' work.</param>
internal sealed class WorldFrameLoadSource(Func<IPresentTimingFeedback?> presentTiming, Func<string?> backend, WorldGpuTiming timing, WorldRenderProbe probe) : IWorldFrameLoadSource {
    /// <summary>The name the committed counters ceilings are compiled into the World under.</summary>
    public const string CeilingsResource = "counters.ceilings.json";

    private static readonly int MarchStepsColumn = ColumnOf(kind: GpuWork.MarchSteps);
    private static readonly Lazy<WorldCountersCeilings?> Ceilings = new(valueFactory: ReadCeilings);
    private readonly GpuWorkSample m_sample = new();

    private double m_budget;
    private bool m_budgeted;
    private IPresentTimingFeedback? m_presentTiming;
    private bool m_resolved;
    // The instance set the view indices were found in, and each instance's 0-based view, or -1 for any other instance:
    // found again only when the runtime runs another set.
    private RenderGraphInstanceSet? m_set;

    private int[] m_views = [];

    /// <inheritdoc/>
    public PresentTimingSample LastPresentTiming {
        get {
            if (!m_resolved) {
                m_resolved = true;
                m_presentTiming = presentTiming();
            }

            return (m_presentTiming?.LastPresentTiming ?? PresentTimingSample.Unavailable);
        }
    }
    /// <inheritdoc/>
    public double MarchStepBudgetPerPixel {
        get {
            if (!m_budgeted && (backend() is { } name)) {
                m_budgeted = true;
                m_budget = ((Ceilings.Value is { } ceilings)
                    ? WorldDynamicResolution.StepBudgetPerPixel(backend: name, ceilings: ceilings)
                    : 0d);
            }

            return m_budget;
        }
    }

    /// <inheritdoc/>
    public void RequireGpuTiming(bool required) => timing.Require(required: required);
    /// <inheritdoc/>
    public bool TryReadGpuFrame(out long frame, out double seconds) {
        frame = 0L;
        seconds = 0d;

        if (probe.Root?.Runtime is not { } runtime) {
            return false;
        }

        FindViews(runtime: runtime);

        for (var index = 0; (index < m_views.Length); index++) {
            var view = m_views[index];

            if ((view < 0) || (runtime.Producer(instance: index) is not null)) {
                continue;
            }

            var node = runtime.Node(instance: index);

            // The first view's timed frames are the reading's identity: while it is untimed, nothing is.
            if (node.Timings.IsEmpty) {
                if (view == 0) {
                    return false;
                }

                continue;
            }
            if (view == 0) {
                frame = node.TimingFrames;
            }

            seconds += (node.LatestTimingMilliseconds / 1000d);
        }

        return (frame != 0L);
    }
    /// <inheritdoc/>
    public bool TryReadMarchSteps(out long frame, out long steps) {
        frame = 0L;
        steps = 0L;

        if (probe.Root?.Runtime is not { } runtime) {
            return false;
        }

        FindViews(runtime: runtime);

        for (var index = 0; (index < m_views.Length); index++) {
            var view = m_views[index];

            if (
                (view < 0) ||
                (runtime.Producer(instance: index) is not null) ||
                !runtime.Work(instance: index).TryReadCompleted(sample: m_sample)
            ) {
                continue;
            }

            if (view == 0) {
                frame = m_sample.Submission;
            }

            for (var pass = 0; (pass < m_sample.PassCount); pass++) {
                if (m_sample.TryGetPassCount(column: MarchStepsColumn, pass: pass, value: out var count)) {
                    steps += count;
                }
            }
        }

        return (frame != 0L);
    }

    // Finds each instance's view again only when the runtime runs another instance set.
    private void FindViews(RenderGraphRuntime runtime) {
        var set = runtime.Instances;

        if (!ReferenceEquals(objA: set, objB: m_set)) {
            m_set = set;
            m_views = [.. set.Instances.Select(selector: static instance => (string.Equals(a: instance.Name, b: WorldViewGraphs.WorldInstance, comparisonType: StringComparison.Ordinal)
                ? 0
                : (WorldViewNames.ViewOf(instance: instance.Name) ?? -1)))];
        }
    }
    private static WorldCountersCeilings? ReadCeilings() {
        using var stream = typeof(WorldFrameLoadSource).Assembly.GetManifestResourceStream(name: CeilingsResource);

        return ((stream is null)
            ? null
            : JsonSerializer.Deserialize(jsonTypeInfo: WorldJsonContext.Default.WorldCountersCeilings, utf8Json: stream));
    }
    private static int ColumnOf(WorkKind kind) {
        var kinds = GpuWork.SubmissionKinds;

        for (var column = 0; (column < kinds.Length); column++) {
            if (ReferenceEquals(objA: kinds[column], objB: kind)) {
                return column;
            }
        }

        throw new InvalidOperationException(message: $"'{kind.Name}' is no submission kind.");
    }
}
