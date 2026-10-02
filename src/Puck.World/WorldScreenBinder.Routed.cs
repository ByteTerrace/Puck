using Puck.SdfVm;
using Puck.World.Client;

namespace Puck.World;

// The worlds seats are presented in elsewhere (WorldContinuum.PresentedElsewhere): a view of the boot frame whose seat is
// presented in another world renders the scene of that world (WorldRoutedScene) instead, from a residency of its own that
// every seat presented there shares, and every window attached to that world (TryResolveWindowView). The presenter
// latches which views route where in its Dress, which the package starts before any instance resolves, so a frame's views
// and its residencies agree. A routed residency films the boot frame
// before it captures its own, so the cameras it frames with are this frame's.
internal sealed partial class WorldScreenBinder {
    // Each routed scene's residency, and the scenes gone from the presenter's table, released after Dress.
    private readonly Dictionary<WorldRoutedScene, SdfWorldResidency> m_routedResidencies = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly List<WorldRoutedScene> m_retiredRoutedScenes = [];
    // The residencies made and released for each endpoint's scene, by the endpoint's identity: its counters row.
    private readonly Dictionary<string, (long Created, long Released)> m_routedResidencyCounts = new(comparer: StringComparer.Ordinal);

    /// <summary>Gets or sets the presenter whose views route a seat presented elsewhere into its world's scene;
    /// <see langword="null"/> in a presentation with no render graph.</summary>
    public WorldFramePresenter? Presenter { get; set; }
    /// <summary>Gets or sets the capture armed for a seat's crossing, which each presented frame asks whether the seat
    /// has crossed on it; <see langword="null"/> in a presentation with no render graph.</summary>
    public WorldCrossingCapture? CrossingCapture { get; set; }

    /// <summary>Returns the view a world view instance renders when its seat is presented elsewhere: the view's index in
    /// the residency of the scene of the world the seat is presented in, created the first time a view resolves to
    /// it.</summary>
    /// <param name="name">The instance's name.</param>
    /// <param name="view">The view, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the instance's view is presented in another world and the views are
    /// configured.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    public bool TryResolveRoutedView(string name, out SdfWorldView view) {
        ArgumentNullException.ThrowIfNull(argument: name);

        view = default;

        if (
            (m_viewPipelines is null) ||
            (ViewHost is not { } host) ||
            (Presenter is not { } presenter) ||
            !presenter.TryRoutedView(
                index: out var index,
                scene: out var scene,
                view: (WorldViewNames.ViewOf(instance: name) ?? 0)
            )
        ) {
            return false;
        }

        view = new SdfWorldView(
            Residency: RoutedResidencyOf(
                host: host,
                scene: scene
            ),
            View: index
        );

        return true;
    }
    /// <summary>Returns the view a window onto an endpoint's world renders (<see cref="WorldFramePresenter.AttachWindow"/>):
    /// the window's index in the residency of its scene, the one every seat presented in that world renders from,
    /// created the first time a view resolves to it.</summary>
    /// <param name="window">The window.</param>
    /// <param name="view">The view, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when this presenter's latch includes the window and the views are configured.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="window"/> is <see langword="null"/>.</exception>
    public bool TryResolveWindowView(WorldRoutedWindow window, out SdfWorldView view) {
        ArgumentNullException.ThrowIfNull(argument: window);

        view = default;

        var index = window.Index;

        if (
            (m_viewPipelines is null) ||
            (ViewHost is not { } host) ||
            (index < 0) ||
            (Presenter is not { } presenter) ||
            !presenter.Presents(scene: window.Scene)
        ) {
            return false;
        }

        view = new SdfWorldView(
            Residency: RoutedResidencyOf(
                host: host,
                scene: window.Scene
            ),
            View: index
        );

        return true;
    }

    // A scene's residency, created the first time a view resolves to it.
    private SdfWorldResidency RoutedResidencyOf(SdfWorldResidency host, WorldRoutedScene scene) {
        if (!m_routedResidencies.TryGetValue(
            key: scene,
            value: out var residency
        )) {
            residency = CreateRoutedResidency(
                host: host,
                scene: scene
            );
            m_routedResidencies.Add(
                key: scene,
                value: residency
            );
        }

        return residency;
    }
    // A routed scene's residency: built like the world's own, with its brick pool and the full quality its frame carries,
    // and its work counted under the scene's name in world.counters. Its instances are sized to the world's own (the count
    // a view's scratch is sized by), so the view the seat renders through follows it in place at the crossing, with no
    // frame held, whenever the scene fits; a scene that needs more grows its tables, and the view rebuilds against it.
    // Its tables build in the frame that creates it, from the pipelines the world's own residency already holds.
    private SdfWorldResidency CreateRoutedResidency(SdfWorldResidency host, WorldRoutedScene scene) {
        var source = scene.FrameSource;
        var name = RoutedViewName(scene: scene);
        var residency = new SdfWorldResidency(
            dynamicTransformCapacity: Math.Max(
                val1: source.WorstCaseDynamicTransformCapacity,
                val2: m_viewDynamicTransformCapacity
            ),
            film: context => {
                if (host.HostFrame(context: in context) is null) {
                    return false;
                }

                FitRoutedWindows(scene: scene);

                return true;
            },
            frameSource: source,
            height: ((uint)m_viewDisplayHeight),
            instanceCapacity: ((int)host.CapacityRevision),
            kernels: ViewKernels(),
            name: name,
            pipelines: m_viewPipelines!,
            programWordCapacity: Math.Max(
                val1: source.WorstCaseProgramWordCapacity,
                val2: m_viewProgramWordCapacity
            ),
            screenSources: new RoutedScreenSources(
                binder: this,
                scene: scene
            ),
            width: ((uint)m_viewDisplayWidth)
        );

        var identity = scene.Endpoint.Identity;

        CountRoutedResidency(
            created: 1,
            identity: identity,
            released: 0
        );
        RegisterViewWork(
            lifetime: new WorldRoutedResidencyCounts(
                lifetime: residency.WorkLifetime,
                residencies: () => m_routedResidencyCounts.GetValueOrDefault(key: identity),
                tables: () => residency.TableBytes
            ),
            name: name,
            transforms: source.MovedTransforms,
            work: residency.Work
        );
        Console.Error.WriteLine(value: $"[world.view: '{scene.Endpoint.Identity}' is shown here; its seats and windows render that world's scene]");

        return residency;
    }
    // Releases the residency of every scene no seat is presented in and no window is attached to any longer: its views
    // have left the render graph's resolves, whose passes give back their holds once the device has finished with them.
    private void ReconcileRoutedResidencies() {
        if (Presenter is not { } presenter) {
            return;
        }

        foreach (var scene in m_routedResidencies.Keys) {
            if (!presenter.Presents(scene: scene)) {
                m_retiredRoutedScenes.Add(item: scene);
            }
        }
        foreach (var scene in m_retiredRoutedScenes) {
            if (m_routedResidencies.Remove(
                key: scene,
                value: out var residency
            )) {
                UnregisterViewWork(name: RoutedViewName(scene: scene));
                residency.Dispose();
                CountRoutedResidency(
                    created: 0,
                    identity: scene.Endpoint.Identity,
                    released: 1
                );
            }
        }

        m_retiredRoutedScenes.Clear();
    }
    private void ReleaseRoutedResidencies() {
        foreach (var (scene, residency) in m_routedResidencies) {
            residency.Dispose();
            CountRoutedResidency(
                created: 0,
                identity: scene.Endpoint.Identity,
                released: 1
            );
        }

        m_routedResidencies.Clear();
    }
    private static string RoutedViewName(WorldRoutedScene scene) => $"routed${scene.Endpoint.Identity}";
    // Counts residencies made or released for an endpoint's scene.
    private void CountRoutedResidency(string identity, long created, long released) {
        var (made, gone) = m_routedResidencyCounts.GetValueOrDefault(key: identity);

        m_routedResidencyCounts[identity] = ((made + created), (gone + released));
    }
}
