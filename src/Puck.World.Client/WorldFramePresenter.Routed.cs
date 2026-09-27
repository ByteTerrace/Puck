using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Puck.SdfVm;

namespace Puck.World.Client;

// The views of seats presented elsewhere (WorldContinuum.PresentedElsewhere). Each such seat keeps its view's place in
// the boot frame, so every view keeps its index, and the view is latched into the scene of the world the seat is
// presented in (WorldRoutedScene), which renders it instead: one scene per routed endpoint, shared by every seat
// presented there, latched afresh by every Dress.
public sealed partial class WorldFramePresenter {
    // Each routed endpoint's scene, the scenes a Dress found no seat in, each view's routed scene and its index among
    // that scene's views (a view no seat routes elsewhere has none), and the frame the Dress returned, whose levers a
    // routed scene's frame takes.
    private readonly Dictionary<WorldAuthorityEndpoint, WorldRoutedScene> m_routedScenes = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly List<WorldRoutedScene> m_retiredScenes = [];
    private readonly List<(WorldRoutedScene? Scene, int Index)> m_viewRoutes = new(capacity: PlayerRoster.MaxSlots);

    private SdfFrame? m_dressedFrame;

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
    /// <summary>Returns whether a routed scene is still the scene of a world a seat is presented in, as the last Dress
    /// latched it.</summary>
    /// <param name="scene">The scene.</param>
    /// <returns><see langword="true"/> while a seat is presented in the scene's world through it.</returns>
    public bool Presents(WorldRoutedScene scene) => (
        m_routedScenes.TryGetValue(
            key: scene.Endpoint,
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
    // A world no seat is presented in any longer leaves the table; the residency rendering it goes once no view resolves
    // to it.
    private void RetireRoutedScenes() {
        foreach (var scene in m_routedScenes.Values) {
            if (scene.ViewCount == 0) {
                m_retiredScenes.Add(item: scene);
            }
        }
        foreach (var scene in m_retiredScenes) {
            _ = m_routedScenes.Remove(key: scene.Endpoint);
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
    private void RouteView(WorldAuthorityEndpoint endpoint, int view) {
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

        while (m_viewRoutes.Count < view) {
            m_viewRoutes.Add(item: (null, 0));
        }

        m_viewRoutes.Add(item: (scene, scene.AddView(view: m_views[view])));
    }
    private WorldRoutedScene CreateRoutedScene(WorldAuthorityEndpoint endpoint) => new(
        bodyColor: index => RoutedBodyColor(
            endpoint: endpoint,
            index: index
        ),
        endpoint: endpoint,
        hostFrame: () => m_dressedFrame
    );
}
