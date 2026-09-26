using Puck.Hosting;
using Puck.SdfVm;
using Puck.Shaders;

namespace Puck.World.Client;

/// <summary>How the render graph demands a view instance each frame.</summary>
public enum WorldViewDemand : byte {
    /// <summary>Nothing shows the view: it renders nothing and keeps its last image, as a HUD frame's parked camera
    /// does.</summary>
    None = 0,
    /// <summary>A screen shows the view: the instance the world renders through reads it within the frame, at a
    /// footprint of its declared extent.</summary>
    Screen = 1,
    /// <summary>The view is shown outside the world, by a HUD frame or a probe export: the display shows it directly, at
    /// its declared extent.</summary>
    Root = 2,
}
/// <summary>One view a world renders beside its own: a camera it films itself from, or another world's session.</summary>
/// <param name="Name">The instance's name, which a screen's mapping names: a camera's registration or a session's view
/// name.</param>
/// <param name="FilmsWorld">Whether the view films this world, whose screens it shows as the world does; a session
/// renders another world and shows no screen.</param>
/// <param name="Demand">How the render graph demands it.</param>
/// <param name="Width">The fraction of the display's width its declared extent covers, which its footprint or root
/// asks.</param>
/// <param name="Height">The fraction of the display's height its declared extent covers.</param>
/// <param name="Refresh">How often it refreshes.</param>
public sealed record WorldView(string Name, bool FilmsWorld, WorldViewDemand Demand, double Width, double Height, RenderGraphRefresh Refresh);
/// <summary>
/// The view instances a world renders beside its own: each camera a screen, a HUD frame or a probe export shows, and
/// each session a screen shows, each an external <c>sdf.world</c> instance rendered by an <see cref="SdfEngineNode"/> of
/// its own. A view filming this world reads every source instance within the frame, as the world's screens show them,
/// and every view, its own included, at its previous frame, so a mirror shows the frame before and two cameras filming
/// each other never read within one frame. A session reads nothing. The world's instance reads every view within the
/// frame, so a screen shows the view's image of this frame.
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
    /// <summary>Returns the instances the views render as, priced at the SDF engine's passes: each camera reading every
    /// source within the frame and every view at its previous frame, each session reading nothing.</summary>
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
                Passes: SdfEngineNode.PassLabels.Length,
                Reads: (view.FilmsWorld
                    ? [
                        .. sources.Select(selector: static source => new RenderGraphRead(Producer: source.Name)),
                        .. Views.Select(selector: static read => new RenderGraphRead(
                            PreviousFrame: true,
                            Producer: read.Name
                        )),
                    ]
                    : []),
                Refresh: view.Refresh
            );
        }

        return instances;
    }
}
