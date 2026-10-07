using Puck.Abstractions.Cameras;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.SdfVm.Views;

namespace Puck.World.Client;

/// <summary>
/// The infinity views one presentation keeps from frame to frame: each planned view fitted to its viewer
/// (<see cref="InfinityViewFit"/>), demanded only while a viewer's previous frame showed it
/// (<see cref="InfinityViewDemand"/>), and published as a <see cref="WorldView"/> the render graph schedules. A view the
/// viewer's sky shows is fitted to the viewer's camera, and a view a deeper world's sky shows to the camera of the view
/// that renders that world, so each level turns with the one above and is never translated by it.
/// <para>Every available planned view is published with <see cref="WorldViewDemand.Sky"/>, so the viewer always reads its latest image. An
/// unavailable observation retains its fitted fallback and publishes no instance or dependency. A
/// demanded view in the viewer's frustum is also published with <see cref="WorldViewDemand.SkySeen"/>, which the graph host
/// turns into a footprint, so the graph renders it; any other has no footprint, which the graph reads as unread
/// (<see cref="RenderGraphInstanceStatus.Unread"/>), so it renders nothing and keeps its last image. Its footprint is the
/// share of the consumer's extent its frame covers at its scale, rounded up to <see cref="FootprintSteps"/> steps so a viewer
/// turning slowly does not republish the views every frame.</para>
/// </summary>
public sealed class WorldInfinityViews {
    /// <summary>The steps a footprint fraction is rounded up to: sixteenths.</summary>
    public const int FootprintSteps = 16;

    private readonly Dictionary<string, InfinityViewFrame> m_frames = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, RenderGraphPixelExtent> m_extents = new(comparer: StringComparer.Ordinal);
    // Each view's reads, kept as one list while the plan stands: a view whose list is replaced is a changed view.
    private readonly Dictionary<string, IReadOnlyList<string>> m_reads = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, List<SdfSkyViewBinding>> m_bindings = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, string> m_producers = new(comparer: StringComparer.Ordinal);
    private readonly HashSet<string> m_unavailable = new(comparer: StringComparer.Ordinal);
    private readonly List<string> m_readScratch = [];
    private WorldInfinityViewPlan m_plan = WorldInfinityViewPlan.Empty;

    /// <summary>Initializes the fit set for one consuming camera. Planned layer identities stay shared across
    /// cameras, while their graph outputs and demand histories belong to this consumer.</summary>
    /// <param name="consumer">The consuming render-graph instance; the first world view by default.</param>
    public WorldInfinityViews(string consumer = WorldViewGraphs.WorldInstance) {
        ArgumentException.ThrowIfNullOrEmpty(argument: consumer);
        Consumer = consumer;
    }

    /// <summary>Gets the instance whose camera this set fits.</summary>
    public string Consumer { get; }

    /// <summary>Returns the graph output of a planned layer for this consumer.</summary>
    /// <param name="name">The complete planned layer path.</param>
    /// <returns>The layer path for the first world view, or that path under the consuming instance.</returns>
    public string ProducerOf(string name) => m_producers[name];

    /// <summary>Gets what schedules the views.</summary>
    public InfinityViewDemand Demand { get; } = new();

    /// <summary>Gets the plan the views follow.</summary>
    public WorldInfinityViewPlan Plan => m_plan;

    /// <summary>Returns the fraction a footprint is rounded up to a step of: never zero, since a footprint of zero means
    /// the consumer does not show the view.</summary>
    /// <param name="fraction">The share of the consumer's axis.</param>
    /// <returns>The fraction rounded up to a multiple of 1/<see cref="FootprintSteps"/>, from one step to 1.</returns>
    public static double Quantize(double fraction) => Math.Clamp(
        value: (Math.Ceiling(a: (Math.Clamp(max: 1.0, min: 0.0, value: fraction) * FootprintSteps)) / FootprintSteps),
        min: (1.0 / FootprintSteps),
        max: 1.0
    );
    /// <summary>Replaces the plan; a view the new plan lacks is forgotten, so one later added under its name starts
    /// undemanded.</summary>
    /// <param name="plan">The plan.</param>
    public void Apply(WorldInfinityViewPlan plan) {
        ArgumentNullException.ThrowIfNull(argument: plan);

        var kept = new HashSet<string>(collection: plan.Views.Select(selector: static view => view.Name), comparer: StringComparer.Ordinal);

        foreach (var gone in m_frames.Keys.Where(predicate: name => !kept.Contains(item: name)).ToArray()) {
            _ = m_frames.Remove(key: gone);
            _ = m_extents.Remove(key: gone);
            Demand.Forget(view: gone);
        }

        m_plan = plan;
        m_reads.Clear();
        m_bindings.Clear();
        m_producers.Clear();
        m_unavailable.Clear();
        foreach (var view in plan.Views) {
            m_producers[view.Name] = ((Consumer == WorldViewGraphs.WorldInstance)
                ? view.Name
                : ((Consumer + Puck.State.GeneratedName.Joiner) + view.Name));
        }

        foreach (var view in plan.Views) {
            m_reads[view.Name] = [];
        }
    }
    /// <summary>Returns the frame a view last rendered with, for the dresser of its instance to take its camera from.</summary>
    /// <param name="name">The instance name.</param>
    /// <returns>The frame, or one that renders nothing for a view the plan lacks.</returns>
    public InfinityViewFrame FrameOf(string name) => (m_frames.TryGetValue(key: name, value: out var frame)
        ? frame
        : default);
    /// <summary>Updates demand from the completed composite of one viewer. Only executed detail rows report a new
    /// visibility result; a standing or skipped composite keeps the previous result, and other passes cannot demand a view.</summary>
    /// <param name="parent">The planned parent view, or null for the root consumer.</param>
    /// <param name="sample">The completed work of that viewer, including its immutable layer labels.</param>
    public void Report(string? parent, GpuWorkSample sample) {
        ArgumentNullException.ThrowIfNull(sample);
        var column = GpuWork.SubmissionKinds.IndexOf(GpuWork.SkyEvaluations);

        foreach (var view in m_plan.Views) {
            if (!string.Equals(a: view.Parent, b: parent, comparisonType: StringComparison.Ordinal)) { continue; }
            for (var row = 0; (row < sample.Details.Length); row++) {
                var detail = sample.Details[row];

                if (string.Equals(a: detail.Detail, b: view.Spec.Name, comparisonType: StringComparison.Ordinal)
                    && sample.PassLabels[detail.Pass].EndsWith(comparisonType: StringComparison.Ordinal, value: "$composite")
                    && sample.TryGetDetailCount(column: column, detail: row, value: out var count)) {
                    Demand.Report(view.Name, count);
                    break;
                }
            }
        }
    }
    /// <summary>Fits the sky sampling records of the root viewer or one nested viewer. Layers beyond the nesting
    /// or residency cap still carry their fitted fallback, while inactive cones carry no binding.</summary>
    /// <param name="parent">The planned parent view, or null for the root consumer.</param>
    /// <param name="viewer">The camera whose sky samples the bindings.</param>
    /// <param name="viewerWidth">The viewer's pixel width.</param>
    /// <param name="viewerHeight">The viewer's pixel height.</param>
    /// <param name="tier">The viewer's sky tier.</param>
    /// <returns>A retained list updated by the next call for this parent.</returns>
    public IReadOnlyList<SdfSkyViewBinding> BindingsOf(string? parent, CameraSnapshot viewer, uint viewerWidth, uint viewerHeight, QualityTier tier) {
        var key = (parent ?? string.Empty);

        if (!m_bindings.TryGetValue(key: key, value: out var bindings)) {
            bindings = [];
            m_bindings.Add(key: key, value: bindings);
        }
        bindings.Clear();
        foreach (var view in m_plan.Views) {
            if (!string.Equals(a: view.Parent, b: parent, comparisonType: StringComparison.Ordinal)) { continue; }
            Add(spec: view.Spec, frame: FrameOf(name: view.Name), producer: (m_unavailable.Contains(item: view.Name) ? null : ProducerOf(name: view.Name)));
        }
        foreach (var fallback in m_plan.Fallbacks) {
            if (!string.Equals(a: fallback.Parent, b: parent, comparisonType: StringComparison.Ordinal)) { continue; }
            Add(spec: fallback.Spec, frame: InfinityViewFit.Fit(spec: fallback.Spec, tier: tier, viewer: viewer,
                viewerWidth: viewerWidth, viewerHeight: viewerHeight), producer: null);
        }
        return bindings;

        void Add(InfinityViewSpec spec, InfinityViewFrame frame, string? producer) {
            if (!frame.Visible) { return; }
            bindings.Add(item: new SdfSkyViewBinding(Layer: spec.Name, Producer: producer,
                Parameters: InfinityViewSampling.Describe(frame: frame, imageSlot: -1, spec: spec, viewer: viewer)));
        }
    }
    /// <summary>Fits every planned view and publishes the available ones for a frame.</summary>
    /// <param name="viewer">The viewer's camera.</param>
    /// <param name="viewerWidth">The pixels across the viewer renders.</param>
    /// <param name="viewerHeight">The pixels down the viewer renders.</param>
    /// <param name="tier">The quality tier the viewer draws at.</param>
    /// <param name="views">The set the views publish into, between its <see cref="WorldViewSet.Begin"/> and
    /// <see cref="WorldViewSet.TryPublish"/>.</param>
    /// <param name="available">Whether an authority-owned observation or far scene is ready. An unavailable view keeps
    /// its fitted fallback and publishes no graph instance. Null makes every planned view available.</param>
    /// <param name="readsOf">The existing screen dependencies of each shown world, or null for none.</param>
    /// <param name="tierOf">The resolved sky tier of a nested viewer. Null uses the root viewer's tier throughout.</param>
    public void Update(CameraSnapshot viewer, uint viewerWidth, uint viewerHeight, QualityTier tier, WorldViewSet views,
        Func<string, bool>? available = null, Func<string, IReadOnlyList<string>?>? readsOf = null,
        Func<string, QualityTier>? tierOf = null) {
        ArgumentNullException.ThrowIfNull(argument: views);

        m_unavailable.Clear();
        foreach (var view in m_plan.Views) {
            if (((view.Parent is not null) && m_unavailable.Contains(item: view.Parent)) || (available?.Invoke(view.Name) == false)) {
                _ = m_unavailable.Add(item: view.Name);
            }
        }

        foreach (var view in m_plan.Views) {
            CameraSnapshot consumer;
            uint consumerWidth, consumerHeight;
            var shownByParent = true;

            if (view.Parent is null) {
                consumer = viewer;
                consumerWidth = viewerWidth;
                consumerHeight = viewerHeight;
            } else {
                var parent = FrameOf(name: view.Parent);

                shownByParent = parent.Visible;
                consumer = parent.Camera;
                // The extent the parent's instance is allocated at, in steps, not the exact one its camera fits.
                consumerWidth = (m_extents.TryGetValue(key: view.Parent, value: out var allocated)
                    ? ((uint)allocated.Width)
                    : parent.Width);
                consumerHeight = ((allocated.Height > 0)
                    ? ((uint)allocated.Height)
                    : parent.Height);
            }

            var frame = ((shownByParent && (consumerWidth != 0u) && (consumerHeight != 0u))
                ? InfinityViewFit.Fit(spec: view.Spec, tier: ((view.Parent is null) ? tier : (tierOf?.Invoke(view.Parent) ?? tier)), viewer: consumer, viewerHeight: consumerHeight, viewerWidth: consumerWidth)
                : default);

            m_frames[view.Name] = frame;

            if (m_unavailable.Contains(item: view.Name)) { continue; }

            if (frame.Visible) {
                // The extent is the footprint's step of the consumer's, so it moves only when the footprint does.
                m_extents[view.Name] = new RenderGraphPixelExtent(
                    Height: ((int)Math.Ceiling(a: (consumerHeight * Quantize(fraction: (((double)frame.Height) / consumerHeight))))),
                    Width: ((int)Math.Ceiling(a: (consumerWidth * Quantize(fraction: (((double)frame.Width) / consumerWidth)))))
                );
            }

            var extent = (m_extents.TryGetValue(key: view.Name, value: out var last)
                ? last
                : new RenderGraphPixelExtent(Height: 1, Width: 1));
            var demanded = (frame.Visible && Demand.IsDemanded(view: view.Name));

            views.Set(view: new WorldView(
                Demand: (demanded ? WorldViewDemand.Sky | WorldViewDemand.SkySeen : WorldViewDemand.Sky),
                FilmsWorld: false,
                Height: Quantize(fraction: (((double)extent.Height) / Math.Max(val1: 1u, val2: consumerHeight))),
                Name: ProducerOf(name: view.Name),
                Refresh: RenderGraphRefresh.Every(divisor: view.Spec.Refresh),
                Width: Quantize(fraction: (((double)extent.Width) / Math.Max(val1: 1u, val2: consumerWidth)))
            ) {
                OutputExtent = extent,
                Parent = ((view.Parent is null) ? null : ProducerOf(name: view.Parent)),
                Reads = Reads(name: view.Name, screens: readsOf?.Invoke(view.Name)),
                SkyConsumer = ((view.Parent is null) ? Consumer : ProducerOf(name: view.Parent)),
            });
        }
    }

    private IReadOnlyList<string> Reads(string name, IReadOnlyList<string>? screens) {
        m_readScratch.Clear();
        foreach (var child in m_plan.Views) {
            if (string.Equals(a: child.Parent, b: name, comparisonType: StringComparison.Ordinal) && !m_unavailable.Contains(item: child.Name)) {
                m_readScratch.Add(item: ProducerOf(name: child.Name));
            }
        }
        if (screens is not null) {
            foreach (var screen in screens) {
                if (!m_readScratch.Contains(screen, StringComparer.Ordinal)) { m_readScratch.Add(item: screen); }
            }
        }
        if (!m_readScratch.SequenceEqual(m_reads[name], StringComparer.Ordinal)) { m_reads[name] = m_readScratch.ToArray(); }
        return m_reads[name];
    }
}
