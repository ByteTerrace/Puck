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

    /// <summary>Gets the restrictions a camera view adds to the world's quality: a low-resolution diegetic display skips
    /// ambient occlusion and soft shadows.</summary>
    public static SdfViewQuality CameraViewQuality { get; } = new() {
        DisableAmbientOcclusion = true,
        DisableSoftShadows = true,
    };
    private bool TryResolveRankedAnchor(WorldCamera camera, int slot, out SdfAnchor anchor) {
        var selected = WorldSeatAnchors.SelectAnchor(
            camera: camera,
            candidateIndex: out _,
            evaluator: m_facts(),
            slot: slot
        );

        switch (selected) {
            case null:
                anchor = new SdfAnchor(
                    Position: Vector3.Zero,
                    Orientation: Quaternion.Identity
                );

                return true;
            case WorldAnchor.Entity entity:
                return m_anchors.TryResolveAnchor(
                    anchor: out anchor,
                    anchorId: entity.Index
                );
            case WorldAnchor.EntityPart part:
                return TryResolveEntityPart(
                    anchor: out anchor,
                    part: part
                );
            case WorldAnchor.Placement placement:
                anchor = new SdfAnchor(
                    Position: (((m_anchors is WorldClient client) && m_stamps.TryShapePosition(
                        client: client,
                        placementId: placement.PlacementId,
                        position: out var live,
                        shapeId: placement.ShapeId
                    ))
                    ? live
                    : StaticAnchorPosition(placement: placement)),
                    Orientation: Quaternion.Identity
                );

                return true;
            case WorldAnchor.Group group:
                anchor = new SdfAnchor(
                    Position: GroupCentroid(group: group),
                    Orientation: Quaternion.Identity
                );

                return true;
            default:
                if (m_anchors is WorldClient seatClient) {
                    return WorldSeatAnchors.TryResolve(
                        anchor: selected,
                        client: seatClient,
                        perception: m_perception,
                        pose: out anchor,
                        slot: slot,
                        speech: m_speech,
                        stamps: m_stamps,
                        transforms: m_viewTransforms
                    );
                }

                anchor = default;

                return false;
        }
    }
    private bool TryResolveEntityPart(WorldAnchor.EntityPart part, out SdfAnchor anchor) {
        if (m_anchors is WorldClient client) {
            return WorldEntityPartResolver.TryPackedPose(
                client: client,
                stamps: m_stamps,
                entityIndex: part.Index,
                partId: part.PartId,
                transforms: m_viewTransforms,
                pose: out anchor
            );
        }

        anchor = default;

        return false;
    }
    /// <inheritdoc/>
    /// <remarks>Each camera view is a view of the frame after the presentation's own, at the quality of its first view
    /// with ambient occlusion and soft shadows off (<see cref="CameraViewQuality"/>): a low-resolution diegetic display
    /// may add restrictions to the world's quality but never lift one. The view's instance renders it from the world's
    /// residency (<see cref="TryResolveView"/>), and the registration's export is set on its node.</remarks>
    public void FilmViews(DynamicTransform[] transforms, ulong authoritativeTick, float presentationSeconds, List<SdfViewSnapshot> views) {
        ArgumentNullException.ThrowIfNull(argument: transforms);
        ArgumentNullException.ThrowIfNull(argument: views);

        m_viewTransforms = transforms;
        m_viewAuthoritativeTick = authoritativeTick;
        m_hostViewCount = views.Count;
        m_cameraViewIndices.Clear();
        m_hostLevels.Clear();

        var quality = ((views.Count > 0)
            ? views[0].Quality.Restrict(other: CameraViewQuality)
            : CameraViewQuality
        );

        foreach (var (name, registration) in m_cameraViews) {
            m_cameraViewIndices[name] = views.Count;
            views.Add(item: new SdfViewSnapshot(
                Camera: FilmCamera(
                    name: name,
                    presentationSeconds: presentationSeconds,
                    registration: registration
                ),
                Region: new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f)
            ) {
                CutRevision = registration.CutRevision,
                Quality = quality,
            });

            views[^1] = ResolveResolution(views[^1], name, registration.Row.RenderWidth, registration.Row.RenderHeight);
            m_hostLevels.Add(item: null);

            if (Runtime?.NodeOf(instance: name) is { } node) {
                node.Export = registration.Export;
            }
        }

        FilmHomeViews(views: views);
    }
    // Films every session of the boot world, at any depth, as a view of the world's own frame after its camera views
    // (SettleSessionRoutes), then the cameras of each level those sessions show: the world is resident once, however many
    // windows show it. Each view records the level whose screens it shows (ReadOf).
    private void FilmHomeViews(List<SdfViewSnapshot> views) {
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
        var anchor = default(SdfAnchor);
        var resolved = (
            (registration.AnchorSource is not { } source) ||
            (
                (registration.AnchorIdSource?.Invoke() is { } anchorId) &&
                source.TryResolveAnchor(
                    anchor: out anchor,
                    anchorId: anchorId
                )
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

    private sealed class EntityPartAnchorSource(WorldScreenBinder owner, WorldAnchor.EntityPart part) : ISdfAnchorSource {
        public bool TryResolveAnchor(int anchorId, out SdfAnchor anchor) {
            _ = anchorId;

            return owner.TryResolveEntityPart(
                anchor: out anchor,
                part: part
            );
        }
    }
    // Resolves a camera's anchor every frame: the winning ranked candidate (or the bare seat-relative anchor) for the
    // registration's seat, then that anchor's pose through the same resolvers the configure-time sources use. A
    // frame with no holding candidate rides the world frame.
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
    }}
