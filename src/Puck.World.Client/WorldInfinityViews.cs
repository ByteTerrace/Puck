using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.SdfVm.Views;

namespace Puck.World.Client;

/// <summary>
/// The infinity views one presentation keeps from frame to frame: each planned view fitted to its viewer
/// (<see cref="InfinityViewFit"/>), demanded only while a viewer's previous frame showed it
/// (<see cref="InfinityViewDemand"/>), and published as a <see cref="WorldView"/> the render graph schedules. A view the
/// viewer's sky shows is fitted to the viewer's camera, and a view a deeper world's sky shows to the camera of the view
/// that renders that world, so each level turns with the one above and is never translated by it.
/// <para>Every planned view is published with <see cref="WorldViewDemand.Sky"/>, so the viewer always reads its latest image. A
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
    private WorldInfinityViewPlan m_plan = WorldInfinityViewPlan.Empty;

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

        foreach (var view in plan.Views) {
            m_reads[view.Name] = [.. plan.Views.Where(predicate: child => string.Equals(a: child.Parent, b: view.Name, comparisonType: StringComparison.Ordinal)).Select(selector: static child => child.Name)];
        }
    }
    /// <summary>Returns the frame a view last rendered with, for the dresser of its instance to take its camera from.</summary>
    /// <param name="name">The instance name.</param>
    /// <returns>The frame, or one that renders nothing for a view the plan lacks.</returns>
    public InfinityViewFrame FrameOf(string name) => (m_frames.TryGetValue(key: name, value: out var frame)
        ? frame
        : default);
    /// <summary>Fits and publishes every planned view for a frame.</summary>
    /// <param name="viewer">The viewer's camera.</param>
    /// <param name="viewerWidth">The pixels across the viewer renders.</param>
    /// <param name="viewerHeight">The pixels down the viewer renders.</param>
    /// <param name="tier">The quality tier the viewer draws at.</param>
    /// <param name="views">The set the views publish into, between its <see cref="WorldViewSet.Begin"/> and
    /// <see cref="WorldViewSet.TryPublish"/>.</param>
    public void Update(CameraSnapshot viewer, uint viewerWidth, uint viewerHeight, QualityTier tier, WorldViewSet views) {
        ArgumentNullException.ThrowIfNull(argument: views);

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
                ? InfinityViewFit.Fit(spec: view.Spec, tier: tier, viewer: consumer, viewerHeight: consumerHeight, viewerWidth: consumerWidth)
                : default);

            m_frames[view.Name] = frame;

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
                Name: view.Name,
                Refresh: RenderGraphRefresh.Every(divisor: view.Spec.Refresh),
                Width: Quantize(fraction: (((double)extent.Width) / Math.Max(val1: 1u, val2: consumerWidth)))
            ) {
                OutputExtent = extent,
                Parent = view.Parent,
                Reads = m_reads[view.Name],
            });
        }
    }
}
