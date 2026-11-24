using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.Maths;
using Puck.SdfVm;
using Puck.SdfVm.Views;
using Puck.SignedDistance;
using Puck.World.Client;

namespace Puck.World;

public sealed partial class WorldScreenBinder {
    // The quality a nested view renders at: the frame's first view's, or full quality in a frame with none, with what the
    // world's nested-view table gives up at the view's presented size added (WorldRenderSettings.NestedRestriction). It may
    // add restrictions to the world's quality but never lift one.
    private SdfViewQuality NestedQuality(List<SdfViewSnapshot> views, string name) {
        var restriction = (Settings?.NestedRestriction(view: name) ?? default);

        return ((views.Count > 0) ? views[0].Quality.Restrict(other: restriction) : restriction);
    }

    // The pose a camera's anchor holds this frame for a seat: the winning ranked candidate (or the bare anchor) resolved
    // through the one resolver every presentation consumer reads. A presentation whose anchor source is no world pose
    // source (no client) resolves an entity through that source alone, and the world frame when nothing is anchored.
    private bool TryResolveRankedAnchor(WorldCamera camera, int slot, out SdfAnchor anchor) {
        var selected = WorldSeatAnchors.SelectAnchor(
            camera: camera,
            candidateIndex: out _,
            evaluator: m_facts(),
            slot: slot
        );

        if (m_anchors is IWorldStampSource source) {
            return WorldAnchorPoses.TryResolve(
                anchor: selected,
                pose: out anchor,
                scope: new WorldAnchorScope(
                    Perception: m_perception,
                    Slot: slot,
                    Source: source,
                    Speech: m_speech,
                    Stamps: m_stamps,
                    Transforms: m_viewTransforms
                )
            );
        }

        switch (selected) {
            case null:
                anchor = new SdfAnchor(Position: Vector3.Zero, Orientation: Quaternion.Identity);

                return true;
            case WorldAnchor.Entity entity:
                return m_anchors.TryResolveAnchor(anchor: out anchor, anchorId: entity.Index);
            default:
                anchor = default;

                return false;
        }
    }

    /// <inheritdoc/>
    /// <remarks>Each camera view is a view of the frame after the presentation's own, at the quality of its first view
    /// with what the world's nested-view table gives up at its presented size added
    /// (<see cref="WorldRenderSettings.NestedRestriction(string?)"/>). The view's instance renders it from the world's
    /// residency (<see cref="TryResolveView"/>), and the registration's export is set on its node.</remarks>
    public void FilmViews(DynamicTransform[] transforms, ulong authoritativeTick, float presentationSeconds, List<SdfViewSnapshot> views, List<string?> names) {
        ArgumentNullException.ThrowIfNull(argument: transforms);
        ArgumentNullException.ThrowIfNull(argument: views);
        ArgumentNullException.ThrowIfNull(argument: names);

        m_viewTransforms = transforms;
        m_viewAuthoritativeTick = authoritativeTick;
        m_hostViewCount = views.Count;
        m_cameraViewIndices.Clear();
        m_hostLevels.Clear();

        foreach (var (name, registration) in m_cameraViews) {
            m_cameraViewIndices[name] = views.Count;
            names.Add(item: name);
            views.Add(item: new SdfViewSnapshot(
                Camera: FilmCamera(
                    name: name,
                    presentationSeconds: presentationSeconds,
                    registration: registration
                ),
                Region: new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f)
            ) {
                CutRevision = registration.CutRevision,
                Source = registration,
                Bloom = Settings?.ResolveBloom((BootDefinition()?.Render ?? WorldRenderDefaults.Absent), registration.Row.Rig.Bloom),
                BloomQuality = (Settings?.BloomQualityOf(view: name) ?? Puck.Hosting.BloomQuality.Off),
                Quality = NestedQuality(views: views, name: name),
            });

            views[^1] = ResolveResolution(views[^1], name, registration.Row.RenderWidth, registration.Row.RenderHeight);
            m_hostLevels.Add(item: null);

            if (Runtime?.NodeOf(instance: name) is { } node) {
                node.Export = registration.Export;
            }
        }

        FilmHomeViews(names: names, views: views);

        // The cameras of the levels those sessions show render as no instance of their own.
        while (names.Count < (views.Count - m_hostViewCount)) {
            names.Add(item: null);
        }
    }

    // Films every session of the boot world, at any depth, as a view of the world's own frame after its camera views
    // (SettleSessionRoutes), then the cameras of each level those sessions show: the world is resident once, however many
    // windows show it. Each view records the level whose screens it shows (ReadOf).
    private void FilmHomeViews(List<SdfViewSnapshot> views, List<string?> names) {
        m_homeViewIndices.Clear();
        m_homeCameras.Clear();
        EnsureFeeds();

        foreach (var feed in m_feeds) {
            if (!(feed.Home && feed.Registered)) {
                continue;
            }

            var view = SharedViewOf(feed: feed);

            m_homeViewIndices[feed.RegistrationName] = views.Count;
            m_homeCameras[feed.RegistrationName] = view.Camera;
            m_hostLevels.Add(item: feed.Nested);
            views.Add(item: view);
            names.Add(item: feed.RegistrationName);
        }

        m_nestedFilms.Begin(target: m_homeFilms);

        foreach (var feed in m_feeds) {
            if (!(feed.Home && feed.Registered) || (feed.Nested is not { } level)) {
                continue;
            }

            var first = views.Count;

            FilmLevel(
                level: level,
                mirror: feed.Mirror,
                target: m_homeFilms,
                views: views
            );

            for (var index = first; (index < views.Count); index++) {
                // A camera the session's level shows films the World's residency for the session's disclosure too.
                views[index] = (views[index] with { Audience = feed.Audience });
                m_hostLevels.Add(item: level);
            }
        }

        m_nestedFilms.End(target: m_homeFilms);
    }
    // The camera a registration films from this frame: its anchor, then its rig, at the frame's presentation time and
    // authoritative tick. A camera whose anchor does not resolve this frame (a companion shape not yet packed, a placement
    // that just despawned) keeps the camera it last filmed from; one that has never filmed frames from its rig at the
    // default anchor, and one with no rig (no client to compile against) from the world origin.
    private CameraSnapshot FilmCamera(string name, CameraRegistration registration, float presentationSeconds) {
        // An unanchored camera rides the world frame, as the one resolver answers a null anchor.
        var anchor = new SdfAnchor(Position: Vector3.Zero, Orientation: Quaternion.Identity);
        var resolved = (
            (registration.AnchorSource is not { } source) ||
            source.TryResolveAnchor(
                anchor: out anchor,
                anchorId: 0
            )
        );

        if (
            !resolved &&
            m_viewCameras.TryGetValue(
                key: name,
                value: out var last
            )
        ) {
            return last;
        }

        var (eye, target, fovRadians) = ((registration.Rig is { } rig)
            ? rig.Resolve(
                anchor: in anchor,
                clock: new SdfCameraClock(
                    AuthoritativeTick: m_viewAuthoritativeTick,
                    PresentationSeconds: presentationSeconds
                )
            )
            : (Vector3.Zero, -Vector3.UnitZ, (MathF.PI / 3f))
        );
        // The declared extent sets the aspect, whatever extent the render graph schedules the view at.
        var camera = CameraSnapshot.LookAt(
            fieldOfViewRadians: fovRadians,
            position: eye,
            target: target,
            viewportHeight: registration.Row.RenderHeight,
            viewportWidth: registration.Row.RenderWidth
        );

        m_viewCameras[name] = camera;

        return camera;
    }

    // Resolves a camera's anchor every frame: the winning ranked candidate (or the bare anchor) for the registration's
    // seat, then that anchor's pose (TryResolveRankedAnchor). A frame with no holding candidate rides the world frame.
    private sealed class RankedAnchorSource(WorldScreenBinder owner, WorldCamera camera, int slot) : ISdfAnchorSource {
        public bool TryResolveAnchor(int anchorId, out SdfAnchor anchor) {
            _ = anchorId;

            return owner.TryResolveRankedAnchor(
                anchor: out anchor,
                camera: camera,
                slot: slot
            );
        }
    }

    // The camera a session the presentation routed rendered its last frame from: a session of the boot world's, filmed
    // into the world's frame, or the routed window's.
    private bool TryPresentedSessionCamera(SessionFeed feed, string view, out CameraSnapshot camera) => (feed.Home
        ? m_homeCameras.TryGetValue(
            key: view,
            value: out camera
        )
        : RoutedWindowOf(feed: feed)!.Scene.TryCamera(
            camera: out camera,
            view: RoutedWindowOf(feed: feed)!.Index
        ));
    // The surface a pick through a session the presentation routed lands on: the boot world's own, through the camera it
    // filmed the session with, or the routed window's.
    private bool TryPresentedSessionSurface(SessionFeed feed, string view, SourceRay ray, out FixedVector3 point) {
        if (
            feed.Home &&
            m_homeCameras.TryGetValue(
                key: view,
                value: out var camera
            ) &&
            (Presenter is { } presenter)
        ) {
            return presenter.TrySurface(
                from: camera.Position,
                point: out point,
                ray: ray
            );
        }
        if (RoutedWindowOf(feed: feed) is { } window) {
            return window.Scene.TrySurface(
                point: out point,
                ray: ray,
                view: window.Index
            );
        }
        if (feed.Emitter is { } session) {
            return session.TrySurface(
                point: out point,
                ray: ray
            );
        }

        point = default;

        return false;
    }
}
