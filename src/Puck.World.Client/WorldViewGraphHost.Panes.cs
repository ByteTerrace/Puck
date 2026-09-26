using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.Hosting;
using Puck.Maths;

namespace Puck.World.Client;

public sealed partial class WorldViewGraphHost : IRenderGraphHitScene {
    private readonly Dictionary<string, CameraSnapshot> m_cameras = new(comparer: StringComparer.Ordinal);
    // The mapping last published for each pass of the root, kept while its region, extent and source hold so a steady
    // frame publishes without allocating.
    private readonly Dictionary<string, SourceMapping> m_mappings = new(comparer: StringComparer.Ordinal);
    private readonly List<SourceMapping> m_panes = [];
    private int m_displayHeight = 1;
    private int m_displayWidth = 1;

    /// <summary>Gets the display's height, in pixels, as the panes were last published against.</summary>
    public int DisplayHeight => m_displayHeight;
    /// <summary>Gets the display's width, in pixels, as the panes were last published against.</summary>
    public int DisplayWidth => m_displayWidth;
    /// <summary>Gets the mapping of every pane the root's <c>place</c> passes draw, in drawing order (the views, then the
    /// <c>views.graphs</c> panes), as <see cref="PublishPanes"/> last published it. The host rewrites the list in
    /// place.</summary>
    public IReadOnlyList<SourceMapping> Panes => m_panes;

    /// <summary>Gets the presentation destination's CPU picker, answered from the panes <see cref="PublishPanes"/> last
    /// published.</summary>
    public SourcePanePicker Picker { get; } = new();

    /// <summary>Publishes the panes this frame's placements show, to <see cref="Panes"/> and <see cref="Picker"/>: one
    /// <see cref="SourceMapping"/> per placement the root's <c>place</c> passes draw, in drawing order, naming its
    /// instance by <see cref="RenderGraphInstance.Handle"/> and showing its whole image at the extent the runtime's
    /// latest schedule renders it at. A placement whose instance has not rendered yet, or that covers no area, publishes
    /// nothing; neither does a view the root stands for, which no <c>place</c> pass draws. A frame whose placements,
    /// extents and instances hold publishes the mappings it published before, allocating nothing.</summary>
    /// <param name="displayWidth">The display's width, in pixels.</param>
    /// <param name="displayHeight">The display's height, in pixels.</param>
    public void PublishPanes(uint displayWidth, uint displayHeight) {
        m_displayWidth = ((int)Math.Clamp(max: int.MaxValue, min: 1u, value: displayWidth));
        m_displayHeight = ((int)Math.Clamp(max: int.MaxValue, min: 1u, value: displayHeight));
        m_panes.Clear();

        if (
            (m_runtime is { Latest: { } latest } runtime) &&
            (m_synthesized is { Plan: not null } synthesized)
        ) {
            var set = runtime.Instances;
            var views = Math.Min(
                val1: synthesized.ViewPasses.Count,
                val2: synthesized.Producers.Count
            );

            for (var view = 0; (view < views); view++) {
                PublishPane(
                    instance: synthesized.Producers[view].Name,
                    latest: latest,
                    pass: synthesized.ViewPasses[view],
                    set: set
                );
            }
            for (var pane = 0; (pane < synthesized.Panes.Count); pane++) {
                PublishPane(
                    instance: synthesized.Panes[pane],
                    latest: latest,
                    pass: synthesized.Panes[pane],
                    set: set
                );
            }
        }

        Picker.Publish(
            displayHeight: m_displayHeight,
            displayWidth: m_displayWidth,
            panes: m_panes
        );
    }
    /// <summary>Records the camera an instance renders from this frame, which a hit on its image continues through.</summary>
    /// <param name="instance">The instance's name.</param>
    /// <param name="camera">The camera.</param>
    public void SetCamera(string instance, in CameraSnapshot camera) {
        ArgumentNullException.ThrowIfNull(argument: instance);

        m_cameras[instance] = camera;
    }
    /// <summary>Finds the published mapping of the pane showing an instance.</summary>
    /// <param name="instance">The instance's name.</param>
    /// <param name="mapping">The mapping when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when a published pane shows the instance.</returns>
    public bool TryGetPane(string instance, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out SourceMapping? mapping) {
        for (var index = 0; (index < m_panes.Count); index++) {
            if (string.Equals(
                a: m_panes[index].Source.Name,
                b: instance,
                comparisonType: StringComparison.Ordinal
            )) {
                mapping = m_panes[index];

                return true;
            }
        }

        mapping = null;

        return false;
    }
    /// <summary>Returns the name of an instance of the live set, as a <see cref="RenderGraphHitPath"/> indexes it.</summary>
    /// <param name="index">The instance's index in the set.</param>
    /// <returns>The name, or <see langword="null"/> before a runtime is attached or for an index outside the set.</returns>
    public string? InstanceName(int index) => (((m_runtime is { } runtime) && (((uint)index) < ((uint)runtime.Instances.Instances.Count)))
        ? runtime.Instances.Instances[index].Name
        : null);
    /// <summary>Walks a display point through the live instance set (<see cref="RenderGraphHitWalk.WalkDisplay"/>): the
    /// topmost published pane under it, then, when that pane shows an instance, a ray through the camera it renders from
    /// into its world, up to the set's <see cref="RenderGraphInstanceSet.NestingDepth"/>.</summary>
    /// <param name="point">The point, in display pixels from the display's top-left corner.</param>
    /// <returns>The path, or <see langword="null"/> before a runtime is attached.</returns>
    public RenderGraphHitPath? Walk(FixedVector2 point) {
        if (m_runtime is not { } runtime) {
            return null;
        }

        var set = runtime.Instances;

        return RenderGraphHitWalk.WalkDisplay(
            displayHeight: m_displayHeight,
            displayWidth: m_displayWidth,
            maxDepth: set.NestingDepth,
            panes: m_panes,
            point: point,
            scene: this,
            set: set
        );
    }

    /// <inheritdoc/>
    /// <remarks>No instance publishes the surfaces standing in its world yet, so every list is empty and a ray cast into
    /// an instance ends on its world.</remarks>
    IReadOnlyList<SourceMapping> IRenderGraphHitScene.Placements(int instance) => [];
    /// <inheritdoc/>
    /// <remarks>A view's camera is the one its seat rendered from in the frame the panes were published for, and a pane's
    /// the named camera its row pairs, recorded by <see cref="SetCamera"/>.</remarks>
    bool IRenderGraphHitScene.TryCamera(int instance, out CameraSnapshot camera) {
        if (
            (m_runtime is { } runtime) &&
            (((uint)instance) < ((uint)runtime.Instances.Instances.Count))
        ) {
            return m_cameras.TryGetValue(
                key: runtime.Instances.Instances[instance].Name,
                value: out camera
            );
        }

        camera = default;

        return false;
    }

    // Publishes one pass's placement when the root draws it and its instance has rendered at an extent.
    private void PublishPane(RenderGraphInstanceSet set, RenderGraphSchedule latest, string pass, string instance) {
        if (
            !m_placements.TryGetValue(
                key: pass,
                value: out var placement
            ) ||
            !placement.Shown
        ) {
            return;
        }

        var index = set.IndexOf(name: instance);

        if (
            (((uint)index) >= ((uint)latest.Instances.Count)) ||
            (latest.Instances[index] is not { Width: > 0, Height: > 0 } row) ||
            !string.Equals(
                a: row.Instance,
                b: instance,
                comparisonType: StringComparison.Ordinal
            )
        ) {
            return;
        }

        var region = new NormalizedRect(
            Height: placement.Height,
            Width: placement.Width,
            X: placement.Left,
            Y: placement.Top
        );
        var source = set.Instances[index].Handle;

        if (
            !m_mappings.TryGetValue(
                key: pass,
                value: out var mapping
            ) ||
            (mapping.Placement is not SourcePlacement.Pane { Region: var shown }) ||
            (shown != region) ||
            (mapping.SourceWidth != row.Width) ||
            (mapping.SourceHeight != row.Height) ||
            (mapping.Source != source)
        ) {
            mapping = SourceMapping.WholePane(
                height: row.Height,
                region: region,
                source: source,
                width: row.Width
            );

            // A pane with no area mid-transition shows nothing to map.
            if (!mapping.TryValidate(refusal: out _)) {
                _ = m_mappings.Remove(key: pass);

                return;
            }

            m_mappings[pass] = mapping;
        }

        m_panes.Add(item: mapping);
    }
}
