using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Shaders;
using Puck.World.Client;

namespace Puck.World;

/// <summary>The frame's GPU load in a presentation with a render graph, from the pass timestamps
/// <see cref="WorldGpuTiming"/> records while a reader asks: one view's (<paramref name="view"/>, the world's own by
/// default), which the dynamic-resolution controller reads, or every node's (no view), the frame's own GPU work, nested
/// views among it, which the background allowance reads (<see cref="WorldBackgroundAllowance"/>); and the presenter's
/// present timing, when the host's presenter reports it (a windowed World's swapchain; an offscreen World presents
/// nothing, so it reports none). A timed reading sums the newest submissions not read before, through a
/// <see cref="WorldFrameLoadAggregate"/>, and names the grid each submission's node recorded it at
/// (<see cref="ShaderPipelineRenderNode.TryGetRenderGrid"/>). Background GPU work is timed apart and never in it.
/// Reading allocates nothing.</summary>
/// <param name="presentTiming">Resolves the presenter's present timing, on the first read, or <see langword="null"/> for a
/// host whose presenter reports none.</param>
/// <param name="timing">The timestamp demand the views' GPU frame time is recorded under.</param>
/// <param name="probe">The render probe, whose root runtime holds the views' work.</param>
/// <param name="view">The view whose fresh submissions this source reads, or <see langword="null"/> for every node the
/// runtime runs.</param>
internal sealed class WorldFrameLoadSource(Func<IPresentTimingFeedback?> presentTiming, WorldGpuTiming timing, WorldRenderProbe probe, string? view = WorldViewGraphs.WorldInstance) : IWorldFrameLoadSource {
    private readonly WorldFrameLoadAggregate m_gpu = new();

    private IPresentTimingFeedback? m_presentTiming;
    private bool m_resolved;
    // The instance set the views were found in, and each instance's node when it is one of the world's own views, or
    // null for any other instance.
    private RenderGraphInstanceSet? m_set;

    private ShaderPipelineRenderNode?[] m_nodes = [];

    private bool m_accounting;

    private int m_accountedViews = -1;

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
    public void RequireGpuTiming(bool required) => timing.Require(required: required);
    /// <inheritdoc/>
    /// <remarks>While asked, the runtime accounts for exactly the world's own views of the set it runs
    /// (<see cref="RenderGraphRuntime.AccountFor"/>), declared again whenever that set changes; once not, it accounts for
    /// none.</remarks>
    public void RequireCompletions(bool required) {
        if (view != WorldViewGraphs.WorldInstance) {
            return;
        }
        if (required == m_accounting) {
            if (required && (probe.Root?.Runtime is { } runtime)) {
                FindViews(runtime: runtime);
            }
            return;
        }

        m_accounting = required;

        if (required) {
            // The next read finds the views again and declares them.
            m_set = null;
            if (probe.Root?.Runtime is { } runtime) {
                FindViews(runtime: runtime);
            }
        } else {
            probe.Root?.Runtime?.AccountFor(instances: []);
        }
    }
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
    /// <remarks>The views that left the graph since the previous read hand their completed renders over through the
    /// runtime (<see cref="RenderGraphRuntime.TakeRetiredCompletions"/>), so a removed view's render still counts.</remarks>
    public ShaderPipelineCompletions TakeCompletions() {
        var completions = default(ShaderPipelineCompletions);

        if (probe.Root?.Runtime is not { } runtime) {
            return completions;
        }

        FindViews(runtime: runtime);
        if (view is { } name) { completions = runtime.TakeRetiredCompletions(instance: name); }

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

        var viewCount = (probe.Settings?.ResolutionViewCount ?? 0);

        if (ReferenceEquals(objA: set, objB: m_set) && NodesHold(runtime: runtime) && (!m_accounting || (m_accountedViews == viewCount))) {
            return;
        }

        var instances = set.Instances;
        var nodes = new ShaderPipelineRenderNode?[instances.Count];
        var survivors = new int[instances.Count];

        for (var index = 0; (index < instances.Count); index++) {
            nodes[index] = NodeAt(index: index, runtime: runtime);
            survivors[index] = ((nodes[index] is { } node) ? Array.FindIndex(array: m_nodes, match: previous => ReferenceEquals(objA: previous, objB: node)) : -1);
        }

        m_set = set;
        m_nodes = nodes;
        m_gpu.Reset(survivors: survivors);

        if (m_accounting) {
            m_accountedViews = viewCount;
            runtime.AccountFor(instances: instances.Select(selector: static instance => instance.Name)
                .Where(predicate: name => (IsView(instance: name) || (probe.Settings?.HasView(view: name) ?? false))));
        }
    }
    // Whether an instance is one of the world's own views: world, or world$2 on.
    private static bool IsView(string instance) =>
        (string.Equals(a: instance, b: WorldViewGraphs.WorldInstance, comparisonType: StringComparison.Ordinal) || (WorldViewNames.ViewOf(instance: instance) is not null));
    // The node of an instance this source reads, or null: one with a node of its own (no producer) that is the view, or
    // any such when the source reads every node.
    private ShaderPipelineRenderNode? NodeAt(RenderGraphRuntime runtime, int index) =>
        ((((view is null) || (runtime.Instances.Instances[index].Name == view)) && (runtime.Producer(instance: index) is null))
            ? runtime.Node(instance: index) : null);
    private bool NodesHold(RenderGraphRuntime runtime) {
        for (var index = 0; (index < m_nodes.Length); index++) {
            var current = NodeAt(index: index, runtime: runtime);

            if (!ReferenceEquals(objA: m_nodes[index], objB: current)) {
                return false;
            }
        }

        return true;
    }
}
