using Puck.Hosting;
using Puck.SdfVm;
using Puck.Shaders;

namespace Puck.World.Client;

/// <summary>How the render graph demands a view instance each frame: every way something shows it, combined.</summary>
[Flags]
public enum WorldViewDemand : byte {
    /// <summary>Nothing shows the view: it renders nothing and keeps its last image, as a HUD frame's parked camera
    /// does.</summary>
    None = 0,
    /// <summary>A screen shows the view: the instance the world renders through reads it within the frame, at a
    /// footprint of its declared extent, and every camera view reads it at its previous frame.</summary>
    Screen = 1,
    /// <summary>The view is shown outside the world, by a HUD frame or a probe export: the display shows it directly, at
    /// its declared extent, whether or not anything schedules the world.</summary>
    Root = 2,
    /// <summary>The sky of a world the display shows has the view, an infinity view (<c>sky$&lt;layer&gt;</c>), whether or
    /// not it is seen this frame: a viewer that films the world reads it within the frame, so its composite samples the
    /// view's latest image, a stale one while the view is not rendering.</summary>
    Sky = 4,
    /// <summary>A viewer's previous frame showed the infinity view on an uncovered pixel, and its region lies in the viewer's
    /// frustum now, so the graph shows it this frame at the view's footprint and renders it when its refresh is due. Without
    /// the flag the view renders nothing and keeps its last image.</summary>
    SkySeen = 8,
}
/// <summary>One view a world renders beside its own: a camera it films itself from, another world's session, or a camera
/// of a world shown through a screen.</summary>
/// <param name="Name">The instance's name, which a screen's mapping names: a camera's registration or a session's view
/// name.</param>
/// <param name="FilmsWorld">Whether the view films this world, whose screens it shows as the world does; a session, or a
/// camera of the world a session shows, renders another world, whose own screens show what <see cref="WorldView.Reads"/>
/// and <see cref="WorldView.PreviousReads"/> name.</param>
/// <param name="Demand">How the render graph demands it.</param>
/// <param name="Width">The fraction of the display's width its declared extent covers, which its footprint or root
/// asks (<see cref="WorldViewInstances.Fit"/>).</param>
/// <param name="Height">The fraction of the display's height its declared extent covers.</param>
/// <param name="Refresh">How often it refreshes.</param>
public readonly record struct WorldView(string Name, bool FilmsWorld, WorldViewDemand Demand, double Width, double Height, RenderGraphRefresh Refresh) {
    /// <summary>The instance whose fitted sky samples this view. Each consuming camera owns its fitted image;
    /// a view with no sky demand does not read this value.</summary>
    public string SkyConsumer { get; init; } = WorldViewGraphs.WorldInstance;
    /// <summary>The camera or session's authored pixel dimensions, retained independently of its visible footprint.</summary>
    public RenderGraphPixelExtent? OutputExtent { get; init; }
    /// <summary>The session view whose world's screen shows this one, or <see langword="null"/> for a view a screen of a
    /// world the display shows directly shows (the boot world, or a world a seat is presented in), which every view of
    /// those worlds reads.</summary>
    public string? Parent { get; init; }
    /// <summary>What the screens of the world a view renders read within the frame, when that world is another: the
    /// sessions they show one level deeper, the camera views of that world a session reads, and the source instances
    /// they show, or <see langword="null"/> for none. A list kept while it holds, since a view whose list is replaced is
    /// a changed view.</summary>
    public IReadOnlyList<string>? Reads { get; init; }
    /// <summary>What the screens of the world a camera view of another world renders read at their previous frame: the
    /// camera views of that world, itself included, so a camera filming a screen that shows a camera never reads it
    /// within one frame; <see langword="null"/> for none.</summary>
    public IReadOnlyList<string>? PreviousReads { get; init; }
}
/// <summary>
/// The view instances a world renders beside its own: each camera a screen, a HUD frame or a probe export shows, and
/// each session a screen shows, each an external <c>sdf.world</c> instance, and every session a session's own screens
/// show, to the presentation's nesting depth. Cameras share the world's <see cref="SdfWorldResidency"/>; a session renders
/// its destination's endpoint residency when its session discloses everything, which every seat presented there and
/// every other such session shares, and a residency of its own otherwise. A view filming this world reads every source
/// instance within the frame, as the world's screens show them, and every view a screen of this world shows, its own
/// included, at its previous frame, so a mirror shows the frame before and two cameras filming each other never read
/// within one frame. A session reads, within the frame, what its own world's screens show (<see cref="WorldView.Reads"/>):
/// the sessions one level deeper and the source instances its world shows, the latter beside the world's own
/// (<see cref="NestedSources"/>). The world's instance reads every view a screen of a world the display shows directly
/// shows within the frame, so a screen shows the view's image of this frame. A view only a HUD frame or a probe export
/// shows is read by nothing: the display shows it directly.
/// </summary>
public sealed class WorldViewInstances {
    /// <summary>The height, in pixels, a session renders at when its screen authors no resolution.</summary>
    public const int DefaultSessionHeight = 144;
    /// <summary>The width, in pixels, a session renders at when its screen authors no resolution.</summary>
    public const int DefaultSessionWidth = 160;

    private WorldViewInstances(IReadOnlyList<WorldView> views, IReadOnlyList<RenderGraphInstance> nestedSources) {
        Views = views;
        NestedSources = nestedSources;
    }

    /// <summary>Gets a set with no view.</summary>
    public static WorldViewInstances Empty { get; } = new(
        nestedSources: [],
        views: []
    );
    /// <summary>Gets the source instances only a session's own screens show, which no view of a world the display shows
    /// reads: the render graph runs them, and each session whose screens show one reads it.</summary>
    public IReadOnlyList<RenderGraphInstance> NestedSources { get; }
    /// <summary>Gets the views, cameras before sessions.</summary>
    public IReadOnlyList<WorldView> Views { get; }

    /// <summary>Creates a set over a list of views.</summary>
    /// <param name="views">The views, each name unique.</param>
    /// <param name="nestedSources">The source instances only sessions' own screens show
    /// (<see cref="NestedSources"/>), or <see langword="null"/> for none.</param>
    /// <returns>The set.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="views"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Two views share a name.</exception>
    public static WorldViewInstances Of(IReadOnlyList<WorldView> views, IReadOnlyList<RenderGraphInstance>? nestedSources = null) {
        ArgumentNullException.ThrowIfNull(argument: views);

        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var view in views) {
            if (!names.Add(item: view.Name)) {
                throw new ArgumentException(
                    message: $"Two views are named '{view.Name}'.",
                    paramName: nameof(views)
                );
            }
        }

        return new WorldViewInstances(
            nestedSources: [.. (nestedSources ?? [])],
            views: [.. views]
        );
    }
    /// <summary>Returns whether a view has a name.</summary>
    /// <param name="name">The instance name.</param>
    /// <returns><see langword="true"/> when a view is named <paramref name="name"/>.</returns>
    public bool Contains(string name) {
        foreach (var view in Views) {
            if (string.Equals(
                a: view.Name,
                b: name,
                comparisonType: StringComparison.Ordinal
            )) {
                return true;
            }
        }

        return false;
    }
    /// <summary>Returns the fraction of each display axis a view of a declared extent asks: the extent over the display's,
    /// scaled by one factor on both axes when it is larger than the display on either, so the visible footprint stays within the display; the instance retains its authored pixels.</summary>
    /// <param name="width">The declared width, in pixels.</param>
    /// <param name="height">The declared height, in pixels.</param>
    /// <param name="displayWidth">The display's width, in pixels.</param>
    /// <param name="displayHeight">The display's height, in pixels.</param>
    /// <returns>The fractions, each in (0, 1].</returns>
    public static (double Width, double Height) Fit(int width, int height, int displayWidth, int displayHeight) {
        var declaredWidth = ((double)Math.Max(val1: 1, val2: width));
        var declaredHeight = ((double)Math.Max(val1: 1, val2: height));
        var shownWidth = ((double)Math.Max(val1: 1, val2: displayWidth));
        var shownHeight = ((double)Math.Max(val1: 1, val2: displayHeight));
        var scale = Math.Min(
            val1: 1.0,
            val2: Math.Min(
                val1: (shownWidth / declaredWidth),
                val2: (shownHeight / declaredHeight)
            )
        );

        return (
            Width: ((declaredWidth * scale) / shownWidth),
            Height: ((declaredHeight * scale) / shownHeight)
        );
    }
    /// <summary>Returns whether a view is shown by a screen of a world the display shows directly: shown by a screen
    /// (<see cref="WorldViewDemand.Screen"/>) and by no session's own (<see cref="WorldView.Parent"/>).</summary>
    /// <param name="view">The view.</param>
    /// <returns><see langword="true"/> when every view of the worlds the display shows reads it.</returns>
    public static bool IsShownDirectly(in WorldView view) => (
        view.Demand.HasFlag(flag: WorldViewDemand.Screen) &&
        (view.Parent is null)
    );
    /// <summary>Returns whether a view is the fitted infinity image of a particular consuming instance.</summary>
    /// <param name="view">The view.</param>
    /// <param name="consumer">The instance whose camera the image was fitted to.</param>
    /// <returns><see langword="true"/> when the consumer reads the view within the frame.</returns>
    public static bool IsShownBySky(in WorldView view, string consumer) => (
        view.Demand.HasFlag(flag: WorldViewDemand.Sky) &&
        string.Equals(a: view.SkyConsumer, b: consumer, comparisonType: StringComparison.Ordinal)
    );
    /// <summary>Returns the instances the views render as, priced at the SDF engine's passes, after the source instances
    /// only sessions' screens show (<see cref="NestedSources"/>): each camera reading every source within the frame and
    /// every view a screen of its world shows at its previous frame, each session reading what its own screens show within
    /// the frame (<see cref="WorldView.Reads"/>).</summary>
    /// <param name="sources">The source instances the world's screens read.</param>
    /// <returns>The nested source instances <paramref name="sources"/> lacks, then the views' instances in the order of
    /// <see cref="Views"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sources"/> is <see langword="null"/>.</exception>
    public IReadOnlyList<RenderGraphInstance> Instances(IReadOnlyList<RenderGraphInstance> sources) {
        ArgumentNullException.ThrowIfNull(argument: sources);

        // A nested source the world's own screens came to show since is already among the sources.
        var instances = new List<RenderGraphInstance>(capacity: (NestedSources.Count + Views.Count));

        foreach (var nested in NestedSources) {
            if (!WorldSourceInstances.Holds(
                instances: sources,
                name: nested.Name
            )) {
                instances.Add(item: nested);
            }
        }

        for (var index = 0; (index < Views.Count); index++) {
            var view = Views[index];

            instances.Add(item: new RenderGraphInstance(
                ExternalPackage: RenderGraphPackageCatalog.SdfWorld,
                Name: view.Name,
                Passes: SdfWorldPackage.NativeFragment.Passes.Count,
                Reads: (view.FilmsWorld
                    ? [
                        .. sources.Select(selector: static source => new RenderGraphRead(Producer: source.Name)),
                        .. Views.Where(predicate: static read => IsShownDirectly(view: in read)).Select(selector: static read => new RenderGraphRead(
                            PreviousFrame: true,
                            Producer: read.Name
                        )),
                        .. Views.Where(predicate: read => IsShownBySky(view: in read, consumer: view.Name)).Select(selector: static read => new RenderGraphRead(Producer: read.Name)),
                    ]
                    : [
                        .. (view.Reads ?? []).Concat(second: Views.Where(predicate: read => IsShownBySky(view: in read, consumer: view.Name))
                            .Select(selector: static read => read.Name)).Distinct(comparer: StringComparer.Ordinal)
                            .Select(selector: static read => new RenderGraphRead(Producer: read)),
                        .. (view.PreviousReads ?? []).Select(selector: static read => new RenderGraphRead(
                            PreviousFrame: true,
                            Producer: read
                        )),
                    ]),
                Refresh: view.Refresh
            ) { OutputExtent = view.OutputExtent });
        }

        return instances;
    }
}
/// <summary>
/// The views a world renders beside its own as a presentation keeps them from frame to frame: each set once per frame from
/// the state that shows it, and published as a new <see cref="WorldViewInstances"/> only when one was added, removed or
/// changed. A steady frame, one that sets every view as it was, allocates nothing and publishes nothing. Views are kept in
/// the order <see cref="WorldViewInstances"/> states: those filming the world first, then the rest, each group by name.
/// </summary>
public sealed class WorldViewSet {
    private readonly List<Entry> m_entries = [];

    private bool m_changed;

    // The source instances only sessions' own screens show, as SetNestedSources last set them.
    private IReadOnlyList<RenderGraphInstance> m_nestedSources = [];

    /// <summary>Gets the views last published.</summary>
    public WorldViewInstances Instances { get; private set; } = WorldViewInstances.Empty;

    // Whether a view sorts before another: those filming the world first, then by name.
    private static bool Precedes(in WorldView view, in WorldView other) => ((view.FilmsWorld != other.FilmsWorld)
        ? view.FilmsWorld
        : (string.CompareOrdinal(
            strA: view.Name,
            strB: other.Name
        ) < 0));

    /// <summary>Starts a frame's update: every view not set again before <see cref="TryPublish"/> is removed.</summary>
    public void Begin() {
        foreach (var entry in m_entries) {
            entry.Seen = false;
        }
    }
    /// <summary>Sets one view for this frame, adding it when no view has its name.</summary>
    /// <param name="view">The view.</param>
    /// <exception cref="ArgumentException">The view has no name.</exception>
    public void Set(in WorldView view) {
        ArgumentException.ThrowIfNullOrEmpty(argument: view.Name);

        var insertAt = m_entries.Count;

        for (var index = 0; (index < m_entries.Count); index++) {
            var entry = m_entries[index];

            if (string.Equals(
                a: entry.View.Name,
                b: view.Name,
                comparisonType: StringComparison.Ordinal
            )) {
                entry.Seen = true;

                if (entry.View != view) {
                    m_entries.RemoveAt(index: index);
                    Set(view: in view);
                }

                return;
            }
            if (
                (insertAt == m_entries.Count) &&
                Precedes(
                    other: entry.View,
                    view: in view
                )
            ) {
                insertAt = index;
            }
        }

        m_entries.Insert(
            index: insertAt,
            item: new Entry { Seen = true, View = view }
        );
        m_changed = true;
    }
    /// <summary>Sets the source instances only sessions' own screens show this frame
    /// (<see cref="WorldViewInstances.NestedSources"/>); a list naming the same instances in order changes nothing.</summary>
    /// <param name="sources">The instances.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sources"/> is <see langword="null"/>.</exception>
    public void SetNestedSources(IReadOnlyList<RenderGraphInstance> sources) {
        ArgumentNullException.ThrowIfNull(argument: sources);

        if (WorldScreenMappingSet.SameNames(
            left: sources,
            right: m_nestedSources
        )) {
            return;
        }

        m_nestedSources = sources;
        m_changed = true;
    }
    /// <summary>Ends a frame's update: removes every view the frame did not set, and publishes the views when any was
    /// added, removed or changed since the last publication.</summary>
    /// <param name="instances">The views published, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the views changed and were published.</returns>
    public bool TryPublish([System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out WorldViewInstances? instances) {
        for (var index = (m_entries.Count - 1); (index >= 0); index--) {
            if (!m_entries[index].Seen) {
                m_entries.RemoveAt(index: index);
                m_changed = true;
            }
        }

        if (!m_changed) {
            instances = null;

            return false;
        }

        m_changed = false;
        Instances = WorldViewInstances.Of(
            nestedSources: m_nestedSources,
            views: [.. m_entries.Select(selector: static entry => entry.View)]
        );
        instances = Instances;

        return true;
    }

    // One view and whether the frame being updated set it.
    private sealed class Entry {
        public bool Seen { get; set; }
        public required WorldView View { get; init; }
    }
}
