using System.Text.Json;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Shaders;
using Puck.World.Client;

namespace Puck.World;

/// <summary>The load the dynamic-resolution controller reads in a presentation with a render graph: the world's own views'
/// (<c>world</c>, <c>world$2</c> on) GPU frame time, from the pass timestamps <see cref="WorldGpuTiming"/> records while
/// the controller asks; the presenter's present timing, when the host's presenter reports it (a windowed World's
/// swapchain; an offscreen World presents nothing, so it reports none); the counted march steps of the views' completed
/// submissions; and their budget per output pixel from the counters ceilings the floor tier committed
/// (<c>tests/Puck.Counters/counters.ceilings.json</c>, compiled in as <see cref="CeilingsResource"/>) for the device's
/// backend. A timed or counted reading sums each view's newest submission not read before, through a
/// <see cref="WorldFrameLoadAggregate"/> per signal, and names the grid each submission's node recorded it at
/// (<see cref="ShaderPipelineRenderNode.TryGetRenderGrid"/>). Reading allocates nothing.</summary>
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
    private readonly WorldFrameLoadAggregate m_gpu = new();
    private readonly GpuWorkSample m_sample = new();
    private readonly WorldFrameLoadAggregate m_steps = new();

    private double m_budget;
    private bool m_budgeted;
    private IPresentTimingFeedback? m_presentTiming;
    private bool m_resolved;
    // The instance set the views were found in, and each instance's node when it is one of the world's own views, or
    // null for any other instance.
    private RenderGraphInstanceSet? m_set;

    private ShaderPipelineRenderNode?[] m_nodes = [];

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
    /// <remarks>A view whose node has no timed submission yet adds nothing; the views are timed while any one is. A
    /// submission whose grid its node no longer records adds nothing either.</remarks>
    public bool TryReadGpuFrame(out WorldFrameLoadReading reading) {
        reading = default;

        if (probe.Root?.Runtime is not { } runtime) {
            return false;
        }

        FindViews(runtime: runtime);
        m_gpu.Begin();
        var timed = false;

        for (var view = 0; (view < m_nodes.Length); view++) {
            if (m_nodes[view] is not { } node) {
                continue;
            }

            var submission = node.LatestTimingSubmission;

            if (submission == 0L) {
                continue;
            }

            timed = true;

            if (node.TryGetRenderGrid(grid: out var grid, submission: submission)) {
                m_gpu.Add(grid: grid, load: (node.LatestTimingMilliseconds / 1000d), render: submission, view: view);
            }
        }

        reading = m_gpu.End();

        return timed;
    }
    /// <inheritdoc/>
    /// <remarks>A completed submission whose grid its node does not record, one that rendered no passes or is no longer
    /// recorded, adds nothing.</remarks>
    public bool TryReadMarchSteps(out WorldFrameLoadReading reading) {
        reading = default;

        if (probe.Root?.Runtime is not { } runtime) {
            return false;
        }

        FindViews(runtime: runtime);
        m_steps.Begin();
        var counted = false;

        for (var view = 0; (view < m_nodes.Length); view++) {
            if ((m_nodes[view] is not { } node) || !node.TryReadCompleted(sample: m_sample)) {
                continue;
            }

            counted = true;

            if (!node.TryGetRenderGrid(grid: out var grid, submission: m_sample.Submission)) {
                continue;
            }

            var steps = 0L;

            for (var pass = 0; (pass < m_sample.PassCount); pass++) {
                if (m_sample.TryGetPassCount(column: MarchStepsColumn, pass: pass, value: out var count)) {
                    steps += count;
                }
            }

            m_steps.Add(grid: grid, load: steps, render: m_sample.Submission, view: view);
        }

        reading = m_steps.End();

        return counted;
    }
    /// <inheritdoc/>
    /// <remarks>The views that left the graph since the previous read hand their completed renders over through the
    /// runtime (<see cref="RenderGraphRuntime.TakeRetiredCompletions"/>), so a removed view's render still counts.</remarks>
    public ShaderPipelineCompletions TakeCompletions() {
        var completions = default(ShaderPipelineCompletions);

        if (probe.Root?.Runtime is not { } runtime) {
            return completions;
        }

        FindViews(runtime: runtime);
        completions = runtime.TakeRetiredCompletions(instance: IsView);

        foreach (var node in m_nodes) {
            if (node is not null) {
                completions = completions.Then(later: node.TakeCompletions());
            }
        }

        return completions;
    }

    // Finds each instance's view again only when the runtime runs another instance set or another node for a view. A
    // view whose node survives keeps the submissions it counted, since a node numbers its submissions on; any other view
    // counts afresh.
    private void FindViews(RenderGraphRuntime runtime) {
        var set = runtime.Instances;

        if (ReferenceEquals(objA: set, objB: m_set) && NodesHold(runtime: runtime)) {
            return;
        }

        var instances = set.Instances;
        var nodes = new ShaderPipelineRenderNode?[instances.Count];
        var survivors = new int[instances.Count];

        for (var index = 0; (index < instances.Count); index++) {
            var name = instances[index].Name;

            nodes[index] = ((IsView(instance: name) && (runtime.Producer(instance: index) is null)) ? runtime.Node(instance: index) : null);
            survivors[index] = ((nodes[index] is { } node) ? Array.FindIndex(array: m_nodes, match: previous => ReferenceEquals(objA: previous, objB: node)) : -1);
        }

        m_set = set;
        m_nodes = nodes;
        m_gpu.Reset(survivors: survivors);
        m_steps.Reset(survivors: survivors);
    }
    // Whether an instance is one of the world's own views: world, or world$2 on.
    private static bool IsView(string instance) =>
        (string.Equals(a: instance, b: WorldViewGraphs.WorldInstance, comparisonType: StringComparison.Ordinal) || (WorldViewNames.ViewOf(instance: instance) is not null));
    private bool NodesHold(RenderGraphRuntime runtime) {
        for (var index = 0; (index < m_nodes.Length); index++) {
            if ((m_nodes[index] is { } node) && !ReferenceEquals(objA: node, objB: runtime.Node(instance: index))) {
                return false;
            }
        }

        return true;
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
