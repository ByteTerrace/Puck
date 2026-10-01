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
}
/// <summary>One view a world renders beside its own: a camera it films itself from, or another world's session.</summary>
/// <param name="Name">The instance's name, which a screen's mapping names: a camera's registration or a session's view
/// name.</param>
/// <param name="FilmsWorld">Whether the view films this world, whose screens it shows as the world does; a session
/// renders another world and shows no screen.</param>
/// <param name="Demand">How the render graph demands it.</param>
/// <param name="Width">The fraction of the display's width its declared extent covers, which its footprint or root
/// asks (<see cref="WorldViewInstances.Fit"/>).</param>
/// <param name="Height">The fraction of the display's height its declared extent covers.</param>
/// <param name="Refresh">How often it refreshes.</param>
public readonly record struct WorldView(string Name, bool FilmsWorld, WorldViewDemand Demand, double Width, double Height, RenderGraphRefresh Refresh) {
    /// <summary>The camera or session's authored pixel dimensions, retained independently of its visible footprint.</summary>
    public RenderGraphPixelExtent? OutputExtent { get; init; }
}
/// <summary>
/// The view instances a world renders beside its own: each camera a screen, a HUD frame or a probe export shows, and
/// each session a screen shows, each an external <c>sdf.world</c> instance. Cameras share the world's
/// <see cref="SdfWorldResidency"/>; each session has its own. A view filming this world reads every source instance within
/// the frame, as the world's screens show them, and every view a screen shows, its own included, at its previous frame,
/// so a mirror shows the frame before and two
/// cameras filming each other never read within one frame. A session reads nothing. The world's instance reads every view
/// a screen shows within the frame, so a screen shows the view's image of this frame. A view only a HUD frame or a probe
/// export shows is read by nothing: the display shows it directly.
/// </summary>
public sealed class WorldViewInstances {
    /// <summary>The height, in pixels, a session renders at when its screen authors no resolution.</summary>
    public const int DefaultSessionHeight = 144;
    /// <summary>The width, in pixels, a session renders at when its screen authors no resolution.</summary>
    public const int DefaultSessionWidth = 160;

    private WorldViewInstances(IReadOnlyList<WorldView> views) => Views = views;

    /// <summary>Gets a set with no view.</summary>
    public static WorldViewInstances Empty { get; } = new(views: []);
    /// <summary>Gets the views, cameras before sessions.</summary>
    public IReadOnlyList<WorldView> Views { get; }

    /// <summary>Creates a set over a list of views.</summary>
    /// <param name="views">The views, each name unique.</param>
    /// <returns>The set.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="views"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Two views share a name.</exception>
    public static WorldViewInstances Of(IReadOnlyList<WorldView> views) {
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

        return new WorldViewInstances(views: [.. views]);
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
    /// <summary>Returns the instances the views render as, priced at the SDF engine's passes: each camera reading every
    /// source within the frame and every view a screen shows at its previous frame, each session reading nothing.</summary>
    /// <param name="sources">The source instances the world's screens read.</param>
    /// <returns>The instances, in the order of <see cref="Views"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sources"/> is <see langword="null"/>.</exception>
    public IReadOnlyList<RenderGraphInstance> Instances(IReadOnlyList<RenderGraphInstance> sources) {
        ArgumentNullException.ThrowIfNull(argument: sources);

        var instances = new RenderGraphInstance[Views.Count];

        for (var index = 0; (index < Views.Count); index++) {
            var view = Views[index];

            instances[index] = new RenderGraphInstance(
                ExternalPackage: RenderGraphPackageCatalog.SdfWorld,
                Name: view.Name,
                Passes: SdfWorldPackage.NativeFragment.Passes.Count,
                Reads: (view.FilmsWorld
                    ? [
                        .. sources.Select(selector: static source => new RenderGraphRead(Producer: source.Name)),
                        .. Views.Where(predicate: static read => read.Demand.HasFlag(flag: WorldViewDemand.Screen)).Select(selector: static read => new RenderGraphRead(
                            PreviousFrame: true,
                            Producer: read.Name
                        )),
                    ]
                    : []),
                Refresh: view.Refresh
            ) { OutputExtent = view.OutputExtent };
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
        Instances = WorldViewInstances.Of(views: [.. m_entries.Select(selector: static entry => entry.View)]);
        instances = Instances;

        return true;
    }

    // One view and whether the frame being updated set it.
    private sealed class Entry {
        public bool Seen { get; set; }
        public required WorldView View { get; init; }
    }
}
