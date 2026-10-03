using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.World.Client;

namespace Puck.World;

// The cameras of the worlds the presentation shows other than the boot world: each camera a screen of such a world shows
// is a view of that world (WorldNestedScreens.Cameras), named under the level that shows it (WorldViewNames.NestedCamera)
// and rendered from the residency that level's world renders through: a routed world's scene, a window joined to its
// destination's scene, or a session's own residency. The dresser of that residency films each camera into its frame
// after its own views, through the world's own mirror (WorldSessionSceneEmitter.ResolveCamera), and the binder records
// the index each one landed at, which the camera's instance renders.
internal sealed partial class WorldScreenBinder {
    // Where each camera view of another world rendered at its residency's last dress, by its view name: a dress drops
    // the views of its residency it no longer films, and a nesting move drops those no live level shows
    // (PruneNestedFilms).
    private readonly WorldFilmedViews<WorldNestedScreens<SessionFeed>> m_nestedFilms = new();

    private Func<string, bool>? m_showsNestedCamera;

    // Drops every camera view of another world no live level shows, whatever residency filmed it last.
    private void PruneNestedFilms() => m_nestedFilms.Retain(shows: (m_showsNestedCamera ??= name => (NestedCameraLevel(name: name) is not null)));
    // Sets the camera views of every level the presentation renders: a routed world's, read by every view of the worlds
    // the display shows, and a session's, read by the session whose world's screens show it. Each reads what its world's
    // screens show within the frame, and the world's camera views at their previous frame.
    private void SetNestedCameraViews(RenderGraphRefresh refresh) {
        foreach (var routed in m_routedScreens.Values) {
            SetNestedCameraViews(
                level: routed,
                parent: null,
                refresh: refresh
            );
        }

        foreach (var feed in m_feeds) {
            if (
                (feed.FrameSource is not null) &&
                (feed.Nested is { } level)
            ) {
                SetNestedCameraViews(
                    level: level,
                    parent: feed.RegistrationName,
                    refresh: refresh
                );
            }
        }
    }
    private void SetNestedCameraViews(WorldNestedScreens<SessionFeed> level, string? parent, RenderGraphRefresh refresh) {
        foreach (var camera in level.Cameras) {
            var (width, height) = WorldViewInstances.Fit(
                displayHeight: m_viewDisplayHeight,
                displayWidth: m_viewDisplayWidth,
                height: ((int)camera.Camera.RenderHeight),
                width: ((int)camera.Camera.RenderWidth)
            );

            m_views.Set(view: new WorldView(
                Demand: WorldViewDemand.Screen,
                FilmsWorld: false,
                Height: height,
                Name: camera.Name,
                Refresh: refresh,
                Width: width
            ) {
                OutputExtent = new RenderGraphPixelExtent(Width: ((int)camera.Camera.RenderWidth), Height: ((int)camera.Camera.RenderHeight)),
                Parent = parent,
                PreviousReads = level.CameraReads,
                Reads = level.FilmReads,
            });
        }
    }
    // The level whose screens show a camera view of another world, by the view's name, or null.
    private WorldNestedScreens<SessionFeed>? NestedCameraLevel(string name) {
        foreach (var routed in m_routedScreens.Values) {
            if (routed.TryCamera(
                camera: out _,
                name: name
            )) {
                return routed;
            }
        }

        EnsureFeeds();

        foreach (var feed in m_feeds) {
            if (
                (feed.Nested is { } level) &&
                level.TryCamera(
                    camera: out _,
                    name: name
                )
            ) {
                return level;
            }
        }

        return null;
    }
    // Resolves a camera view of another world: the view its residency's last dress filmed it at, in the residency the
    // level's world renders through; the first view of that residency until a dress has filmed it.
    private bool TryResolveNestedCameraView(string name, out SdfWorldView view) {
        view = default;

        if (
            (NestedCameraLevel(name: name) is not { } level) ||
            !m_nestedOwners.TryGetValue(
                key: level,
                value: out var owner
            )
        ) {
            return false;
        }

        object target;
        SdfWorldResidency residency;

        if (owner.Scene is { } scene) {
            if (ViewHost is not { } host) {
                return false;
            }

            target = scene;
            residency = RoutedResidencyOf(
                host: host,
                scene: scene
            );
        } else if (owner.Feed is { } feed) {
            if (
                (RoutedWindowOf(feed: feed) is { } window) &&
                (ViewHost is { } host)
            ) {
                target = window.Scene;
                residency = RoutedResidencyOf(
                    host: host,
                    scene: window.Scene
                );
            } else if (SessionResidencyOf(feed: feed) is { } own) {
                target = feed;
                residency = own;
            } else {
                return false;
            }
        } else {
            return false;
        }

        view = new SdfWorldView(
            Residency: residency,
            View: ((m_nestedFilms.TryGet(
                film: out var film,
                name: name
            ) && ReferenceEquals(
                objA: film.Target,
                objB: target
            ))
                ? film.Index
                : 0)
        );

        return true;
    }
    // Films the cameras of every level a routed scene's residency renders: the routed world's own, and the destination's
    // of every window joined to the scene, which is the same world.
    private void FilmScene(WorldRoutedScene scene, List<SdfViewSnapshot> views) {
        m_nestedFilms.Begin(target: scene);

        if (m_routedScreens.TryGetValue(
            key: scene,
            value: out var routed
        )) {
            FilmLevel(
                level: routed,
                mirror: scene.Endpoint.Mirror,
                target: scene,
                views: views
            );
        }

        foreach (var feed in m_feeds) {
            if (
                (feed.Nested is { } level) &&
                (RoutedWindowOf(feed: feed) is { } window) &&
                ReferenceEquals(
                    objA: window.Scene,
                    objB: scene
                )
            ) {
                FilmLevel(
                    level: level,
                    mirror: scene.Endpoint.Mirror,
                    target: scene,
                    views: views
                );
            }
        }

        m_nestedFilms.End(target: scene);
    }
    // Films the cameras of a session's own level into its own residency's frame.
    private void FilmFeed(SessionFeed feed, List<SdfViewSnapshot> views) {
        m_nestedFilms.Begin(target: feed);

        if (feed.Nested is { } level) {
            FilmLevel(
                level: level,
                mirror: feed.Mirror,
                target: feed,
                views: views
            );
        }

        m_nestedFilms.End(target: feed);
    }
    // Films each camera of a level into a frame after the views already in it, at the quality of the frame's first view
    // restricted as a camera view is (CameraViewQuality), and records where each landed.
    private void FilmLevel(WorldNestedScreens<SessionFeed> level, WorldSessionMirror mirror, object target, List<SdfViewSnapshot> views) {
        if (level.Cameras.Count == 0) {
            return;
        }

        var quality = ((views.Count > 0)
            ? views[0].Quality.Restrict(other: CameraViewQuality)
            : CameraViewQuality);

        foreach (var camera in level.Cameras) {
            var snapshot = WorldSessionSceneEmitter.ResolveCamera(
                cameraName: camera.Camera.Name,
                domains: m_domains,
                height: camera.Camera.RenderHeight,
                mirror: mirror,
                width: camera.Camera.RenderWidth
            );

            m_nestedFilms.Record(
                camera: in snapshot,
                index: views.Count,
                level: level,
                name: camera.Name,
                target: target
            );
            views.Add(item: new SdfViewSnapshot(
                Camera: snapshot,
                Region: new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f)
            ) {
                Quality = quality,
            });
            views[^1] = ResolveResolution(views[^1], camera.Name, camera.Camera.RenderWidth, camera.Camera.RenderHeight);
        }
    }
    // The level a view of a routed scene's frame past its seats and windows films: a camera view's.
    private WorldNestedScreens<SessionFeed>? FilmedLevelOf(WorldRoutedScene scene, int view) => m_nestedFilms.LevelAt(
        index: view,
        target: scene
    );
    // The camera a camera view of another world last filmed from, in that world's space.
    private bool TryNestedCamera(string name, out CameraSnapshot camera) {
        if (
            m_nestedFilms.TryGet(
                film: out var film,
                name: name
            ) &&
            (NestedCameraLevel(name: name) is not null)
        ) {
            camera = film.Camera;

            return true;
        }

        camera = default;

        return false;
    }

}
