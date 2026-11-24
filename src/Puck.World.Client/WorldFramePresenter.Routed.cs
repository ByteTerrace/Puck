using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Puck.SdfVm;

namespace Puck.World.Client;

// The views of seats presented elsewhere (WorldContinuum.PresentedElsewhere). Each such seat keeps its view's place in
// the boot frame, so every view keeps its index, and the view is latched into the scene of the world the seat is
// presented in (WorldRoutedScene), which renders it instead: one scene per endpoint, shared by every seat presented
// there, latched afresh by every Dress, and by every window attached to it (AttachWindow).
public sealed partial class WorldFramePresenter {
    // Each endpoint's scene, the scenes a Dress found no seat or window in, each view's routed scene and its index
    // among that scene's views (a view no seat routes elsewhere has none), and the frame the Dress returned, whose clock
    // a routed scene's frame takes.
    private readonly Dictionary<object, WorldRoutedScene> m_routedScenes = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly List<WorldRoutedScene> m_retiredScenes = [];
    private readonly List<(WorldRoutedScene? Scene, int Index)> m_viewRoutes = new(capacity: PlayerRoster.MaxSlots);
    // Each seat's routed scene and its view's index there, as the last Dress latched it; none for a seat presented
    // here.
    private readonly (WorldRoutedScene? Scene, int Index)[] m_seatRoutes = new (WorldRoutedScene?, int)[PlayerRoster.MaxSlots];

    private SdfFrame? m_dressedFrame;
    private readonly SdfFrame.Writer m_frameWriter = new();
    // The dressed frame's views as demand points for a second scene of the same world: every view this presentation
    // renders here, each with the height its sizes are measured in, since its region places it in this frame's output.
    private readonly List<SdfViewSnapshot> m_hostDemandViews = [];
    private Func<IReadOnlyList<SdfViewSnapshot>>? m_hostDemand;
    private Func<IReadOnlyList<SdfViewSnapshot>> HostDemand => (m_hostDemand ??= () => m_hostDemandViews);

    // Latches the dressed frame's views as demand points (m_hostDemandViews), at the output height the frame is dressed
    // for.
    private void LatchHostDemand(IReadOnlyList<SdfViewSnapshot> views, uint height) {
        m_hostDemandViews.Clear();
        foreach (var view in views) {
            if (view.Elsewhere || !(view.Region.Height > 0f)) { continue; }
            m_hostDemandViews.Add(item: ((view.DemandHeight > 0f) ? view : (view with { DemandHeight = MathF.Max(x: 1f, y: (view.Region.Height * height)) })));
        }
    }

    /// <summary>Returns the scene of the world a seat was presented in as the last Dress latched it, and its view's index
    /// among that scene's views, so a query through the seat's view (a surface under its pointer) reads the world the
    /// seat sees.</summary>
    /// <param name="slot">The seat, zero-based.</param>
    /// <param name="scene">The routed world's scene, when this returns <see langword="true"/>.</param>
    /// <param name="index">The seat's view index in the scene's frames, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the seat was presented elsewhere in the last dressed frame.</returns>
    public bool TrySeatScene(int slot, [NotNullWhen(returnValue: true)] out WorldRoutedScene? scene, out int index) {
        if (
            (((uint)slot) < ((uint)m_seatRoutes.Length)) &&
            (m_seatRoutes[slot] is { Scene: { } routed } route)
        ) {
            scene = routed;
            index = route.Index;

            return true;
        }

        scene = null;
        index = 0;

        return false;
    }
    /// <summary>Returns the scene of the world a view of the boot frame is presented in, as the frame's Dress latched
    /// it: a seat presented elsewhere (<see cref="WorldContinuum.PresentedElsewhere"/>) keeps its view's place, and the
    /// routed world's scene renders the view at its own index.</summary>
    /// <param name="view">The view's index in the boot frame's views.</param>
    /// <param name="scene">The routed world's scene, when this returns <see langword="true"/>.</param>
    /// <param name="index">The view's index in the scene's frames, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the view's seat is presented elsewhere.</returns>
    public bool TryRoutedView(int view, [NotNullWhen(returnValue: true)] out WorldRoutedScene? scene, out int index) {
        if (
            (((uint)view) < ((uint)m_viewRoutes.Count)) &&
            (m_viewRoutes[view] is { Scene: { } routed } route)
        ) {
            scene = routed;
            index = route.Index;

            return true;
        }

        scene = null;
        index = 0;

        return false;
    }
    /// <summary>Attaches a window onto an endpoint's world to the scene this presentation renders that world with, the
    /// one door a window's view enters it through: the window is a view of the scene's frame after its seats' views, so
    /// a window and every seat presented in the same world render from one residency, each at its own quality. The scene
    /// is created if no seat is presented there, and stays while a seat is presented there or a window is
    /// attached.</summary>
    /// <param name="endpoint">The authority whose world the window shows.</param>
    /// <returns>The window, which its holder frames each frame (<see cref="WorldRoutedWindow.View"/>) and disposes when it
    /// closes.</returns>
    /// <remarks>Call it on the frame thread. It enters the next presenter latch; until then its index is -1.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="endpoint"/> is <see langword="null"/>.</exception>
    public WorldRoutedWindow AttachWindow(WorldAuthorityEndpoint endpoint) {
        ArgumentNullException.ThrowIfNull(argument: endpoint);

        return SceneOf(endpoint: endpoint).Attach();
    }
    /// <summary>Attaches a session screen's window to the scene several session screens share: the scene drawn from one
    /// session's mirror, which every session whose stream its authority proved is handed the same scene inputs renders
    /// from, each at its own quality. The scene is created by the first window and leaves the table once no window is
    /// attached.</summary>
    /// <param name="mirror">The mirror of the session whose stream the scene draws.</param>
    /// <param name="authority">The identity of the world the session observes.</param>
    /// <param name="name">The scene's own name, unique among the presentation's scenes.</param>
    /// <param name="presented">Whether the world the session observes is the one this presentation's own frame draws,
    /// so the scene plans its meshes at this frame's demand too (<see cref="WorldRoutedScene.HostDemand"/>).</param>
    /// <returns>The window, which its holder frames each frame and disposes when it closes.</returns>
    /// <remarks>Call it on the frame thread. It enters the next presenter latch; until then its index is -1.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="mirror"/> is <see langword="null"/>.</exception>
    public WorldRoutedWindow AttachSharedWindow(WorldSessionMirror mirror, string authority, string name, bool presented = false) {
        ArgumentNullException.ThrowIfNull(argument: mirror);

        if (!m_routedScenes.TryGetValue(key: mirror, value: out var scene)) {
            scene = NewScene(authority: authority, bodyColor: mirror.BodyColor, endpoint: null, key: mirror, mirror: mirror, name: name);
            m_routedScenes.Add(key: mirror, value: scene);
        }
        scene.HostDemand = (presented ? HostDemand : null);
        scene.HostWorld = (presented ? this : null);

        return scene.Attach();
    }

    /// <summary>Gets the scene of every world a seat is presented in or a window is attached to, as the last Dress left
    /// them, enumerated without allocating.</summary>
    public Dictionary<object, WorldRoutedScene>.ValueCollection RoutedScenes => m_routedScenes.Values;

    /// <summary>Returns whether a routed scene is still the scene of a world a seat is presented in or a window is
    /// attached to, as the last Dress latched it.</summary>
    /// <param name="scene">The scene.</param>
    /// <returns><see langword="true"/> while a seat is presented in the scene's world through it or a window is attached
    /// to it.</returns>
    public bool Presents(WorldRoutedScene scene) => (
        m_routedScenes.TryGetValue(
            key: scene.Key,
            value: out var current
        ) &&
        ReferenceEquals(
            objA: current,
            objB: scene
        )
    );

    // Clears the latch before a Dress latches its views.
    private void BeginRoutedViews() {
        m_viewRoutes.Clear();

        foreach (var scene in m_routedScenes.Values) {
            scene.BeginViews();
        }
    }
    // A world no seat is presented in any longer, and no window is attached to, leaves the table; the residency rendering
    // it goes once no view resolves to it.
    private void RetireRoutedScenes() {
        foreach (var scene in m_routedScenes.Values) {
            scene.EndViews();
            if ((scene.ViewCount == 0) && (scene.WindowCount == 0)) {
                m_retiredScenes.Add(item: scene);
            }
        }
        foreach (var scene in m_retiredScenes) {
            _ = m_routedScenes.Remove(key: scene.Key);
            scene.Dispose();
        }

        m_retiredScenes.Clear();
    }
    // A local seat is painted with its roster's color in the world it is presented in, as it is in the boot world; every
    // other body with the color its authority delivered.
    private Vector3 RoutedBodyColor(WorldAuthorityEndpoint endpoint, int index) {
        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            if (
                (m_roster.Seat(slot: slot) is not null) &&
                (m_continuum.Route(slot: slot) is { } route) &&
                ReferenceEquals(
                    objA: route.Endpoint,
                    objB: endpoint
                ) &&
                (route.Entity == endpoint.Mirror.Address(index: index))
            ) {
                return m_client.BodyColor(index: slot);
            }
        }

        return endpoint.Mirror.BodyColor(index: index);
    }
    // Latches a view into the scene of the world its seat is presented in, creating the scene the first frame a seat is
    // presented there.
    private void RouteView(WorldAuthorityEndpoint endpoint, int view, int seat) {
        var scene = SceneOf(endpoint: endpoint);

        while (m_viewRoutes.Count < view) {
            m_viewRoutes.Add(item: (null, 0));
        }

        m_viewRoutes.Add(item: (scene, scene.AddView(seat: seat, view: m_views[view])));
    }
    // The scene of an endpoint's world, created the first time a seat is presented there or a window attaches to it.
    private WorldRoutedScene SceneOf(WorldAuthorityEndpoint endpoint) {
        if (!m_routedScenes.TryGetValue(
            key: endpoint,
            value: out var scene
        )) {
            scene = CreateRoutedScene(endpoint: endpoint);
            m_routedScenes.Add(
                key: endpoint,
                value: scene
            );
        }

        return scene;
    }
    // A seat may be presented in an endpoint's scene, so the scene is offered the client's guess for that endpoint, which
    // its pool poses into a layer only that seat's own view reads.
    private WorldRoutedScene CreateRoutedScene(WorldAuthorityEndpoint endpoint) {
        var scene = NewScene(
            authority: endpoint.Identity,
            bodyColor: index => RoutedBodyColor(
                endpoint: endpoint,
                index: index
            ),
            endpoint: endpoint,
            key: endpoint,
            mirror: endpoint.Mirror,
            name: endpoint.Identity
        );

        scene.SeatGuess = () => m_client.SeatGuessAt(endpoint: endpoint);

        return scene;
    }
    // A scene under this presentation's levers: its programs build in the background, it takes the dressed frame's
    // clock, and it follows the presentation's shadow slots, sky audition and render levers.
    private WorldRoutedScene NewScene(object key, WorldAuthorityEndpoint? endpoint, WorldSessionMirror mirror, string authority, string name, Func<int, Vector3> bodyColor) => new(
        authority: authority,
        bodyColor: bodyColor,
        buildsInBackground: true,
        domains: m_domains,
        endpoint: endpoint,
        hostFrame: () => m_dressedFrame,
        jobs: m_animator.Jobs,
        key: key,
        mirror: mirror,
        name: name,
        session: m_settings,
        shadowSettings: () => m_settings.ShadowSlots,
        skyLayers: m_settings.SkyLayers
    );
}
