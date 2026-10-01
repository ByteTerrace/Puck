using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.Hosting;
using Puck.Maths;

namespace Puck.World.Client;

/// <summary>What the views a world renders beside its own show, as a hit on a screen showing a view reads it: the camera
/// each last rendered from, which the hit continues through (a camera view's, and a session's in its destination's
/// space), and the surface a ray meets in the world a view renders.</summary>
public interface IWorldViewScenes {
    /// <summary>Finds the camera a view last rendered from.</summary>
    /// <param name="view">The view's instance name.</param>
    /// <param name="camera">The camera when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the view has rendered from a camera.</returns>
    bool TryCamera(string view, out CameraSnapshot camera);
    /// <summary>Finds the surface a ray meets in the world a view last rendered, in that world's space.</summary>
    /// <param name="view">The view's instance name.</param>
    /// <param name="ray">The ray, in the view's world.</param>
    /// <param name="point">The point, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the view answers for its world and the ray meets a surface of it.</returns>
    bool TrySurface(string view, SourceRay ray, out FixedVector3 point);
    /// <summary>Finds the screens standing in the world a view renders, when that world is not the one the host's
    /// <see cref="WorldViewGraphHost.Screens"/> publishes: a session's destination, or the world a seat's view is
    /// presented in.</summary>
    /// <param name="view">The view's instance name.</param>
    /// <param name="placements">The screens' mappings, each named by the instance it shows, when this returns
    /// <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the view renders another world than the boot world's screens stand in.</returns>
    bool TryPlacements(string view, out IReadOnlyList<SourceMapping> placements);
    /// <summary>Finds the glass a session view shows on, in the world a consumer that reads it renders.</summary>
    /// <param name="consumer">The reading instance's name.</param>
    /// <param name="producer">The read instance's name.</param>
    /// <param name="glass">The screen row the session shows on, when this returns <see cref="WorldPortalGlass.Found"/>.</param>
    /// <returns>Whether the producer is a session the consumer's world shows, and where.</returns>
    WorldPortalGlass PortalGlass(string consumer, string producer, out WorldScreen? glass);
}
/// <summary>Where a session view stands in the world of an instance that reads it.</summary>
public enum WorldPortalGlass : byte {
    /// <summary>The read instance is no session view.</summary>
    None = 0,
    /// <summary>A screen of the consumer's world shows the session, on the glass found.</summary>
    Found = 1,
    /// <summary>The consumer renders a world none of whose screens shows the session.</summary>
    Elsewhere = 2,
}
public sealed partial class WorldViewGraphHost : IRenderGraphHitScene {
    private readonly Dictionary<string, CameraSnapshot> m_cameras = new(comparer: StringComparer.Ordinal);
    // The mapping last published for each pass of the root, kept while its region, extent and source hold so a steady
    // frame publishes without allocating.
    private readonly Dictionary<string, SourceMapping> m_mappings = new(comparer: StringComparer.Ordinal);
    private readonly List<SourceMapping> m_panes = [];
    // The instances the local user opened as passthrough sources.
    private readonly HashSet<string> m_passthrough = new(comparer: StringComparer.Ordinal);
    private int m_displayHeight = 1;
    private int m_displayWidth = 1;

    // The lone whole-display view's mapping, which is no pane, and the one last built, kept while it holds.
    private SourceMapping? m_display;
    private SourceMapping? m_displayMapping;
    private SourceMapping? m_hovered;
    private SourcePick m_hoveredPick;

    /// <summary>Gets the display's height, in pixels, as the panes were last published against.</summary>
    public int DisplayHeight => m_displayHeight;
    /// <summary>Gets the display's width, in pixels, as the panes were last published against.</summary>
    public int DisplayWidth => m_displayWidth;
    /// <summary>Gets the published mapping of the pane under the pointer, as <see cref="Hover"/> last resolved it
    /// against the panes <see cref="PublishPanes"/> last published, or <see langword="null"/> when the pointer is on no
    /// pane's source or nothing asked since the panes were published.</summary>
    public SourceMapping? HoveredPane => m_hovered;
    /// <summary>Gets the picker's answer for the hovered pane: its source and the source pixel under the pointer;
    /// default while <see cref="HoveredPane"/> is <see langword="null"/>.</summary>
    public SourcePick HoveredPick => m_hoveredPick;
    /// <summary>Gets the mapping of every pane the root's <c>place</c> passes draw, in drawing order (the views, then the
    /// <c>views.graphs</c> panes), as <see cref="PublishPanes"/> last published it. The host rewrites the list in
    /// place.</summary>
    public IReadOnlyList<SourceMapping> Panes => m_panes;
    /// <summary>Gets the whole-display mapping of the lone view covering the whole display, which is no pane (the display
    /// is that view) and so is never hovered or outlined, but which <see cref="Walk"/> starts from where no pane holds the
    /// point; <see langword="null"/> when the last composed frame showed no such view or it has not rendered.</summary>
    public SourceMapping? DisplayView => m_display;

    /// <summary>Gets the presentation destination's CPU picker, answered from the panes <see cref="PublishPanes"/> last
    /// published.</summary>
    public SourcePanePicker Picker { get; } = new();

    /// <summary>Gets or sets the screens standing in the world, whose published mappings every world producer's instance
    /// reports as its placements, so a hit walk continues from a view through a screen into its source;
    /// <see langword="null"/> reports none.</summary>
    public WorldScreenMappingSet? Screens { get; set; }
    /// <summary>Gets or sets what the views the world renders beside its own show — the cameras they last rendered
    /// from, sessions included, and the surfaces of their worlds — or <see langword="null"/> for none.</summary>
    public IWorldViewScenes? ViewScenes { get; set; }

    /// <summary>Publishes the panes this frame's placements show, to <see cref="Panes"/> and <see cref="Picker"/>: one
    /// <see cref="SourceMapping"/> per placement the root's <c>place</c> passes draw, in drawing order, naming its
    /// instance by <see cref="RenderGraphInstance.Handle"/> and showing its whole image at the extent the runtime's
    /// latest schedule renders it at. A placement whose instance has not rendered yet, or that covers no area, publishes
    /// nothing; neither does a view the root stands for, which no <c>place</c> pass draws, nor a lone whole-display view whose
    /// pass draws it only to tonemap it, so the panes and every pick through them are the same with a tonemap or without. A frame whose placements,
    /// extents and instances hold publishes the mappings it published before, allocating nothing. Publishing clears the
    /// hovered pane until <see cref="Hover"/> asks again, so no hover outlives the panes it was picked from. A lone view
    /// covering the whole display, tonemapped or not, publishes its whole-display mapping as <see cref="DisplayView"/>
    /// instead.</summary>
    /// <param name="displayWidth">The display's width, in pixels.</param>
    /// <param name="displayHeight">The display's height, in pixels.</param>
    public void PublishPanes(uint displayWidth, uint displayHeight) {
        m_displayWidth = ((int)Math.Clamp(max: int.MaxValue, min: 1u, value: displayWidth));
        m_displayHeight = ((int)Math.Clamp(max: int.MaxValue, min: 1u, value: displayHeight));
        m_panes.Clear();
        m_display = null;
        ClearHover();

        if (
            (m_runtime is { Latest: { } latest } runtime) &&
            (m_synthesized is { Plan: not null } synthesized)
        ) {
            var set = runtime.Instances;
            var views = Math.Min(
                val1: synthesized.ViewPasses.Count,
                val2: synthesized.Producers.Count
            );

            if (
                m_lone &&
                (views > 0)
            ) {
                m_display = MappingOf(
                    cached: m_displayMapping,
                    instance: synthesized.Producers[0].Name,
                    latest: latest,
                    region: new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f),
                    set: set
                );
                m_displayMapping = (m_display ?? m_displayMapping);
            }

            // A lone whole-display view shown only for its tonemap publishes no pane, as the view the root stands for
            // does: the display shows the world itself either way.
            for (var view = (m_loneTonemapped ? 1 : 0); (view < views); view++) {
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
    /// <summary>Marks an instance as a passthrough source the local user opened: from the next
    /// <see cref="PublishPanes"/>, the mapping of each pane showing it takes <see cref="SourceDestination.Passthrough"/>
    /// with the <see cref="SourceOpener.LocalUser"/> opener. The host's local-user door is the only caller; nothing a
    /// world document declares reaches it.</summary>
    /// <param name="instance">The instance's name.</param>
    /// <exception cref="ArgumentException"><paramref name="instance"/> is <see langword="null"/>, empty or white
    /// space.</exception>
    public void OpenPassthrough(string instance) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: instance);

        _ = m_passthrough.Add(item: instance);
    }
    /// <summary>Returns an instance's panes to the presentation destination from the next <see cref="PublishPanes"/>.</summary>
    /// <param name="instance">The instance's name.</param>
    /// <returns><see langword="true"/> when the instance was a passthrough source.</returns>
    public bool ClosePassthrough(string instance) => m_passthrough.Remove(item: instance);
    /// <summary>Forgets the hovered pane: the pointer left the display, or its presentation shows none.</summary>
    public void ClearHover() {
        m_hovered = null;
        m_hoveredPick = default;
    }
    /// <summary>Resolves the pane under the pointer, the presentation destination's hover: asks <see cref="Picker"/> for
    /// the topmost published pane whose source holds the point and keeps its mapping as <see cref="HoveredPane"/>. A point
    /// on a letterbox bar or bezel, or on no pane, hovers nothing, and a pane the layout does not show was never
    /// published, so it is never hovered. Allocates nothing.</summary>
    /// <param name="point">The pointer, in display pixels from the display's top-left corner.</param>
    /// <returns>The hovered pane's mapping, or <see langword="null"/>.</returns>
    public SourceMapping? Hover(Vector2 point) {
        ClearHover();

        if (!Picker.TryPick(
            pick: out var pick,
            point: point
        )) {
            return null;
        }

        // The picker names the pane's source; panes are listed in drawing order and the topmost wins, so the last
        // pane showing that source is the one picked.
        for (var index = (m_panes.Count - 1); (index >= 0); index--) {
            if (m_panes[index].Source == pick.Source) {
                m_hovered = m_panes[index];
                m_hoveredPick = pick;

                break;
            }
        }

        return m_hovered;
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
    /// topmost published pane under it, or the <see cref="DisplayView"/> beneath every pane, then, when that shows an
    /// instance, a ray through the camera it renders from into its world, up to the set's
    /// <see cref="RenderGraphInstanceSet.NestingDepth"/>.</summary>
    /// <param name="point">The point, in display pixels from the display's top-left corner.</param>
    /// <returns>The path, or <see langword="null"/> before a runtime is attached.</returns>
    public RenderGraphHitPath? Walk(FixedVector2 point) {
        if (m_runtime is not { } runtime) {
            return null;
        }

        var set = runtime.Instances;

        return RenderGraphHitWalk.WalkDisplay(
            display: m_display,
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
    /// <remarks>Each world is tested against its own screens. A view that renders another world than the boot world
    /// reports that world's screens (<see cref="IWorldViewScenes.TryPlacements"/>): a session reports its destination's,
    /// each named by the session one level deeper it shows, and a seat's view presented in another world that world's.
    /// Every other view's world producer (<c>world</c>, <c>world$&lt;view&gt;</c>) and every camera view renders the boot
    /// world, so each reports the mappings <see cref="Screens"/> last published; any other instance reports none, so a
    /// ray cast into it ends on its world. A walk through a portal therefore continues through the portals inside its
    /// destination, to the set's nesting depth.</remarks>
    IReadOnlyList<SourceMapping> IRenderGraphHitScene.Placements(int instance) {
        if (
            (InstanceName(index: instance) is { } routed) &&
            (ViewScenes is { } scenes) &&
            scenes.TryPlacements(
                placements: out var placements,
                view: routed
            )
        ) {
            return placements;
        }
        if (
            (Screens is { } screens) &&
            (InstanceName(index: instance) is { } name) &&
            (
                string.Equals(
                    a: name,
                    b: WorldViewGraphs.WorldInstance,
                    comparisonType: StringComparison.Ordinal
                ) ||
                (WorldViewNames.ViewOf(instance: name) is not null) ||
                FilmsWorld(views: screens.Views, name: name)
            )
        ) {
            return screens.Mappings;
        }

        return [];
    }
    /// <inheritdoc/>
    /// <remarks>A view's camera is the one its seat rendered from in the frame the panes were published for, a pane's
    /// the named camera its row pairs, recorded by <see cref="SetCamera"/>, and a camera view's or a session's the one it
    /// last rendered from (<see cref="ViewScenes"/>), a session's in its destination's space.</remarks>
    bool IRenderGraphHitScene.TryCamera(int instance, out CameraSnapshot camera) {
        if (
            (m_runtime is { } runtime) &&
            (((uint)instance) < ((uint)runtime.Instances.Instances.Count))
        ) {
            var name = runtime.Instances.Instances[instance].Name;

            if (m_cameras.TryGetValue(
                key: name,
                value: out camera
            )) {
                return true;
            }

            if (ViewScenes is { } scenes) {
                return scenes.TryCamera(
                    camera: out camera,
                    view: name
                );
            }
        }

        camera = default;

        return false;
    }
    /// <inheritdoc/>
    /// <remarks>A view answers through <see cref="ViewScenes"/>: a session finds the surface in its destination's
    /// world, which is where a pick through a portal lands.</remarks>
    bool IRenderGraphHitScene.TrySurface(int instance, SourceRay ray, out FixedVector3 point) {
        if (
            (ViewScenes is { } scenes) &&
            (InstanceName(index: instance) is { } name)
        ) {
            return scenes.TrySurface(
                point: out point,
                ray: ray,
                view: name
            );
        }

        point = default;

        return false;
    }

    // Whether a view films the world the screens stand in.
    private static bool FilmsWorld(WorldViewInstances views, string name) {
        foreach (var view in views.Views) {
            if (string.Equals(
                a: view.Name,
                b: name,
                comparisonType: StringComparison.Ordinal
            )) {
                return view.FilmsWorld;
            }
        }

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

        var mapping = MappingOf(
            cached: m_mappings.GetValueOrDefault(key: pass),
            instance: instance,
            latest: latest,
            region: new NormalizedRect(
                Height: placement.Height,
                Width: placement.Width,
                X: placement.Left,
                Y: placement.Top
            ),
            set: set
        );

        if (mapping is null) {
            _ = m_mappings.Remove(key: pass);

            return;
        }

        m_mappings[pass] = mapping;
        m_panes.Add(item: mapping);
    }
    // The mapping of an instance's whole image over a region of the display once the instance has rendered at an extent:
    // the cached one while its region, extent, source and destination hold, so a steady frame allocates nothing.
    private SourceMapping? MappingOf(RenderGraphInstanceSet set, RenderGraphSchedule latest, string instance, NormalizedRect region, SourceMapping? cached) {
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
            return null;
        }

        var source = set.Instances[index].Handle;
        var destination = (m_passthrough.Contains(item: instance)
            ? SourceDestination.Passthrough
            : SourceDestination.Presentation
        );
        var mapping = cached;

        if (
            (mapping is null) ||
            (mapping.Placement is not SourcePlacement.Pane { Region: var shown }) ||
            (shown != region) ||
            (mapping.SourceWidth != row.Width) ||
            (mapping.SourceHeight != row.Height) ||
            (mapping.Source != source) ||
            (mapping.Destination != destination)
        ) {
            mapping = SourceMapping.WholePane(
                height: row.Height,
                region: region,
                source: source,
                width: row.Width
            ) with {
                Destination = destination,
                Opener = ((destination == SourceDestination.Passthrough)
                    ? SourceOpener.LocalUser
                    : SourceOpener.Document
                ),
            };

            // A pane with no area mid-transition shows nothing to map.
            if (!mapping.TryValidate(refusal: out _)) {
                return null;
            }
        }

        return mapping;
    }
}
