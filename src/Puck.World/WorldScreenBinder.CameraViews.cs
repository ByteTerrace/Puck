using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.Maths;
using Puck.SdfVm;
using Puck.SdfVm.Views;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.World.Client;

namespace Puck.World;

// Camera views: each camera a screen, a HUD frame or a probe export shows is a registration here, which the render graph
// runs as an sdf.world instance of that name. The instance renders a view of the world's own frame from the world's
// residency: the presentation's dress films each registration's camera into the frame after its own views (FilmViews).
internal sealed partial class WorldScreenBinder : IWorldViewScenes {
    // The camera each view last rendered from, which a hit on a screen showing it continues through.
    private readonly Dictionary<string, CameraSnapshot> m_viewCameras = new(comparer: StringComparer.Ordinal);
    // Each camera view's index in the world's frame this frame, and how many views the presentation dressed before them.
    private readonly Dictionary<string, int> m_cameraViewIndices = new(comparer: StringComparer.Ordinal);

    private int m_hostViewCount;

    /// <summary>Gets the restrictions a camera view adds to the world's quality: a low-resolution diegetic display skips
    /// ambient occlusion and soft shadows.</summary>
    public static SdfViewQuality CameraViewQuality { get; } = new() {
        DisableAmbientOcclusion = true,
        DisableSoftShadows = true,
    };

    // A same-kind pose/aim/FOV/rig/anchor/extent edit re-wires the live registration in place (a freshly compiled rig plus
    // its anchor sources); its instance and every wired slot survive untouched. The registration's row snapshot advances
    // so the next reconcile diffs against what the view now embodies.
    private void ApplyCameraPose(CameraRegistration registration, WorldCamera camera) {
        ConfigureCameraView(
            camera: camera,
            registration: registration,
            seat: registration.Seat
        );

        registration.Row = camera;
    }
    // The reconcile-side View bind: a failed bind (unknown camera, unconfigured views) still releases the PRIOR view —
    // the declared source no longer names it — and records the fault so screen.state reads honestly.
    private (bool Ok, string Message) ApplyViewChange(int index, ScreenSlot slot, WorldScreenSource.View view) {
        var outcome = TryView(
            index: index,
            cameraName: view.CameraName
        );

        if (!outcome.Ok) {
            ReleaseSlotView(slot: slot);
            slot.DeclaredFault = outcome.Message;
        }

        return outcome;
    }
    // Compiles the camera axes and wires their reference-frame source. A ranked anchor list or a seat-relative anchor
    // resolves every frame through RankedAnchorSource for the registration's seat; the bare kinds keep their
    // configure-time sources.
    private void ConfigureCameraView(CameraRegistration registration, WorldCamera camera, int seat) {
        if (
            (camera.Anchors is not null) ||
            WorldSeatAnchors.IsSeatRelative(anchor: camera.Anchor)
        ) {
            registration.AnchorSource = new RankedAnchorSource(
                owner: this,
                camera: camera,
                slot: PlayerRoster.SlotFromDisplay(number: seat)
            );
            registration.AnchorIdSource = static () => 0;
            CompileCameraRig(
                camera: camera,
                registration: registration
            );

            return;
        }

        switch (camera.Anchor) {
            case null:
                registration.AnchorSource = null;
                registration.AnchorIdSource = null;

                break;
            case WorldAnchor.Entity entity:
                registration.AnchorSource = m_anchors;
                registration.AnchorIdSource = () => entity.Index;

                break;
            case WorldAnchor.EntityPart part:
                registration.AnchorSource = new EntityPartAnchorSource(
                    owner: this,
                    part: part
                );
                registration.AnchorIdSource = static () => 0;

                break;
            case WorldAnchor.Placement placement:
                registration.AnchorSource = new FixedAnchorSource(anchor: new SdfAnchor(
                    Position: StaticAnchorPosition(placement: placement),
                    Orientation: Quaternion.Identity
                ));
                registration.AnchorIdSource = static () => 0;

                break;
            case WorldAnchor.Group group:
                registration.AnchorSource = new FixedAnchorSource(anchor: new SdfAnchor(
                    Position: GroupCentroid(group: group),
                    Orientation: Quaternion.Identity
                ));
                registration.AnchorIdSource = static () => 0;

                break;
        }

        CompileCameraRig(
            camera: camera,
            registration: registration
        );
    }
    // A camera program's state bindings, placement subjects, and blend names all resolve against the live document,
    // which only the client anchor source carries (the same seam StaticAnchorPosition/GroupCentroid read). Without
    // one there is no document to compile against and the view frames the world origin.
    private void CompileCameraRig(CameraRegistration registration, WorldCamera camera) {
        if (m_anchors is WorldClient client) {
            registration.Rig = WorldCameraRigCompiler.Compile(
                definition: client.Definition,
                mirror: client.StateMirror,
                program: camera.Rig
            );
        }
    }    // The one-shot centroid of a group anchor. A filmed/offscreen view bakes only this raw centroid: it DROPS the group
    // Chase.SpreadPullback widening entirely (not merely its per-frame smoothing), so an establishing shot filmed onto a
    // diegetic screen frames the centroid without widening for the group's spread. The main-window composer applies and
    // smooths the spread; documented so authors don't expect spread-widening on a filmed establishing shot.
    private Vector3 GroupCentroid(WorldAnchor.Group group) =>
        ((m_anchors is WorldClient client)
            ? WorldGroupAnchors.ComputeRaw(
                client: client,
                group: group,
                maxPopulation: WorldClient.EntityCapacity
            ).Centroid
            : Vector3.Zero
        );
    // Registers (or finds, idempotent per name) one camera view: fixed cameras carry their own world-space look-at;
    // anchored cameras resolve their WorldAnchor each frame and pose their rig at the resolved anchor. The render graph
    // runs it as an instance of the registration's name from its next reconciliation. A camera FILMS an already-lit
    // world, so it lights nothing.
    private void RegisterCameraView(WorldCamera camera, int seat) {
        var name = m_registrationNames.Of(
            camera: camera,
            seat: seat
        );

        if (!m_cameraViews.ContainsKey(key: name)) {
            var registration = new CameraRegistration { Row = camera, Seat = seat };

            ConfigureCameraView(
                camera: camera,
                registration: registration,
                seat: seat
            );
            m_cameraViews[name] = registration;
        }

        // A parked view keeps its instance and its last image but is demanded by nothing: a hidden HUD frame or a
        // candidate that stopped winning parks rather than tearing down, so showing it again costs nothing.
        _ = m_parkedViews.Remove(item: name);
        ReconcileViews();
    }
    // A removed camera row: every slot filming it unbinds (a slot whose DECLARED source still names it — possible only
    // transiently inside one delivery, the validator rejects a durable dangling reference — keeps a visible fault), and
    // the registration is released so its instance leaves the render graph.
    private void ReleaseCameraRow(string name) {
        foreach (var slot in m_slots.Values) {
            if (
                (slot.View is { } view) &&
                string.Equals(
                a: view.Name,
                b: name,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                slot.View = null;

                if (slot.DeclaredSource is WorldScreenSource.View) {
                    slot.DeclaredFault = $"camera '{name}' not declared";
                }
            }
        }

        if (m_cameraViews.TryGetValue(
            key: name,
            value: out var registration
        )) {
            RetireViewExportForRecreation(cameraName: registration.Row.Name);
        }

        ReleaseView(name: name);
        Console.Error.WriteLine(value: $"[world.camera: view '{name}' released — camera removed]");
    }
    // After a slot stops filming a camera (a screen removal OR any source transition away from it), a camera no screen,
    // export or retained HUD frame names any more is released, so its instance leaves the render graph and a later
    // screen.source <index> view registers it afresh.
    private void ReleaseOrphanedCameraView(string name) {
        // A probe export holds the seat-1 registration of its camera (the only one it ever opens).
        var exported = (
            m_cameraViews.TryGetValue(
            key: name,
            value: out var registration
        ) &&
            (registration.Seat == DefaultViewSeat) &&
            HasViewExportReferences(cameraName: registration.Row.Name)
        );

        if (
            (WiredScreensFor(name: name).Count == 0) &&
            !exported &&
            !HasRetainedView(registrationName: name) &&
            m_cameraViews.ContainsKey(key: name)
        ) {
            ReleaseView(name: name);
            Console.Error.WriteLine(value: $"[world.screen: camera view '{name}' released — no remaining screen references it]");
        } else {
            ReconcileViews();
        }
    }
    private void ReleaseOrphanedCameraViews(HashSet<string> candidates) {
        foreach (var name in candidates) {
            ReleaseOrphanedCameraView(name: name);
        }
    }
    // Drops a slot's camera view reference and releases the registration when nothing else shows it — the symmetric half
    // of TryView's acquire, run whenever the slot stops filming that camera.
    private void ReleaseSlotView(ScreenSlot slot) {
        if (slot.View is not { } view) {
            return;
        }

        slot.View = null;
        ReleaseOrphanedCameraView(name: view.Name);
    }
    // Releases a camera registration: its instance leaves the render graph, which disposes its passes and output once
    // the device has finished every submission that may sample them.
    private void ReleaseView(string name) {
        _ = m_cameraViews.Remove(key: name);
        _ = m_parkedViews.Remove(item: name);
        _ = m_viewCameras.Remove(key: name);
        ReconcileViews();
    }    // Resolves a placeable-camera name against the world's declared cameras (ordinal), or null when none matches.
    private WorldCamera? ResolveCamera(string name) {
        for (var index = 0; (index < m_cameras.Count); index++) {
            var camera = m_cameras[index];

            if (string.Equals(
                a: camera.Name,
                b: name,
                comparisonType: StringComparison.Ordinal
            )) {
                return camera;
            }
        }

        return null;
    }
    // The stamped world position of a placement anchor (the same WorldAnchorGeometry math speakers read) — needs the live
    // definition, which the anchor source carries in practice (the client).
    private Vector3 StaticAnchorPosition(WorldAnchor.Placement placement) =>
        ((m_anchors is WorldClient client)
            ? WorldAnchorGeometry.StaticPlacementPosition(
                definition: client.Definition,
                placementId: placement.PlacementId,
                shapeId: placement.ShapeId
            )
            : Vector3.Zero
        );
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
                        speech: m_facts().Speech,
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
    // The set of screen indices currently wired to a camera name.
    private HashSet<int> WiredScreensFor(string name) {
        var indices = new HashSet<int>();

        foreach (var slot in m_slots.Values) {
            if (
                (slot.View is { } view) &&
                string.Equals(
                a: view.Name,
                b: name,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                _ = indices.Add(item: slot.Index);
            }
        }

        return indices;
    }

    /// <summary>Reconciles the live camera views to a mutated camera list — the live-application half of an
    /// <c>UpsertCamera</c>/<c>RemoveCamera</c> world mutation, called by the frame source when the definition revision
    /// moves (before <see cref="ReconcileScreens"/>, so a same-delivery View source change resolves the new rows). The
    /// stored row list is replaced (later resolves read live data); then, for each registered camera view: a pose, aim,
    /// FOV, rig or extent edit writes the live registration in place (its instance survives, and an extent edit moves its
    /// footprint), a change that renames the registration (a camera becoming or ceasing to be seat-relative) releases and
    /// registers it again, and a removed row releases the view and unbinds every slot that filmed it. A declared View slot
    /// that faulted at boot (its camera did not exist yet) self-heals when the camera row arrives. Not migrated onto
    /// <c>Puck.World.Client.KeyedReconciler</c> — its in-place vs. release-and-register split reads a per-field diff the
    /// generic shape cannot express.</summary>
    /// <param name="cameras">The mutated camera list (the live definition's cameras).</param>
    public void ReconcileCameras(IReadOnlyList<WorldCamera> cameras) {
        if (m_disposed) {
            return;
        }

        m_cameras = cameras;

        // Walk a snapshot of the registered names (the release/register paths mutate m_cameraViews).
        m_cameraReconcileScratch.Clear();
        m_cameraReconcileScratch.AddRange(collection: m_cameraViews.Keys);

        foreach (var name in m_cameraReconcileScratch) {
            var registration = m_cameraViews[name];

            if (ResolveCamera(name: registration.Row.Name) is not { } next) {
                ReleaseCameraRow(name: name);

                continue;
            }

            if (Equals(
                objA: next,
                objB: registration.Row
            )) {
                continue;
            }

            if (!string.Equals(
                a: m_registrationNames.Of(
                    camera: next,
                    seat: registration.Seat
                ),
                b: name,
                comparisonType: StringComparison.Ordinal
            )) {
                RetireViewExportForRecreation(cameraName: registration.Row.Name);
                ReleaseView(name: name);
                RegisterCameraView(
                    camera: next,
                    seat: registration.Seat
                );
                Console.Error.WriteLine(value: $"[world.camera: '{name}' registered again]");
            } else {
                // An exported image has the camera's extent, so an extent edit makes the export again at the new one.
                if (
                    (next.RenderWidth != registration.Row.RenderWidth) ||
                    (next.RenderHeight != registration.Row.RenderHeight)
                ) {
                    RetireViewExportForRecreation(cameraName: registration.Row.Name);
                }

                ApplyCameraPose(
                    camera: next,
                    registration: registration
                );
                Console.Error.WriteLine(value: $"[world.camera: '{name}' updated live]");
            }
        }

        // Self-heal: a declared View slot left faulted (its camera name was undeclared at bind time) binds now that
        // the row exists — the same TryView machinery a screen.source <index> view runs. A live runtime producer (an inserted
        // machine overlaying the declared view) is never displaced.
        foreach (var slot in m_slots.Values) {
            if (
                (slot.View is null) &&
                !m_live.ContainsKey(key: slot.Index) &&
                (slot.DeclaredSource is WorldScreenSource.View declared) &&
                (ResolveCamera(name: declared.CameraName) is not null) &&
                (m_viewPipelines is not null)
            ) {
                var outcome = TryView(
                    index: slot.Index,
                    cameraName: declared.CameraName
                );

                // The view the row names, not a live bind over it.
                ShowRow(index: slot.Index);
                Console.Error.WriteLine(value: $"[world.camera: {outcome.Message}]");
            }
        }

        ReconcileMappings();
    }
    /// <summary>Points a declared screen at a placeable camera — the runtime <c>screen.source &lt;index&gt; view</c> path. Any existing
    /// producer on the slot is cleared first. Requires the views to have been configured (they are, at startup); fails
    /// loudly for an undeclared screen, an unknown camera name, or unconfigured views.</summary>
    /// <param name="index">The engine screen-surface index (must be a declared screen).</param>
    /// <param name="cameraName">The placeable camera to film from.</param>
    /// <returns>Whether the bind succeeded, and a message describing the outcome.</returns>
    public (bool Ok, string Message) TryView(int index, string cameraName) {
        if (m_disposed) {
            return (Ok: false, Message: "binder disposed");
        }

        if (m_slots.TryGetValue(
            key: index,
            value: out var slot
        ) is false) {
            return (Ok: false, Message: $"no screen {index} declared");
        }

        if (m_viewPipelines is null) {
            return (Ok: false, Message: "the views are not configured");
        }

        if (ResolveCamera(name: cameraName) is not { } camera) {
            return (Ok: false, Message: $"camera '{cameraName}' not declared");
        }

        var previousView = slot.View;
        var registrationName = m_registrationNames.Of(
            camera: camera,
            seat: DefaultViewSeat
        );

        slot.View = new ViewFeed(Name: registrationName);
        slot.DeclaredFault = null;
        RegisterCameraView(
            camera: camera,
            seat: DefaultViewSeat
        );

        // A re-point away from another camera releases the superseded registration AFTER the new bind, so a view no slot
        // films stops rendering (the View A → View B case).
        if (
            (previousView is { } previous) &&
            !string.Equals(
            a: previous.Name,
            b: registrationName,
            comparisonType: StringComparison.Ordinal
        )
        ) {
            ReleaseOrphanedCameraView(name: previous.Name);
        }

        ShowLive(
            index: index,
            source: new WorldScreenSource.View(CameraName: cameraName)
        );

        return (Ok: true, Message: $"screen {index} showing camera '{camera.Name}'");
    }
    /// <inheritdoc/>
    /// <remarks>A camera view's camera is the one it last filmed from; a session's, the one its last frame rendered from
    /// in the destination's space (<see cref="WorldSessionSceneEmitter.TryCamera"/>), so a hit on a portal's window
    /// continues into the destination along the ray the window rendered.</remarks>
    public bool TryCamera(string view, out CameraSnapshot camera) {
        if (m_viewCameras.TryGetValue(
            key: view,
            value: out camera
        )) {
            return true;
        }

        if (SessionFeedOf(name: view) is { } feed) {
            if (RoutedWindowOf(feed: feed) is { } window) {
                return window.Scene.TryCamera(
                    camera: out camera,
                    view: window.Index
                );
            }
            if (feed.Emitter is { } session) {
                return session.TryCamera(camera: out camera);
            }
        }

        camera = default;

        return false;
    }
    /// <inheritdoc/>
    /// <remarks>A session answers from the program its last frame rendered (<see cref="WorldSessionSceneEmitter.TrySurface(Commands.SourceRay, out Maths.FixedVector3)"/>),
    /// so a pick through a portal lands on the destination's surface it shows; a camera view answers nothing.</remarks>
    public bool TrySurface(string view, SourceRay ray, out FixedVector3 point) {
        if (SessionFeedOf(name: view) is { } feed) {
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
        }

        point = default;

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
                Quality = quality,
            });

            if (Runtime?.NodeOf(instance: name) is { } node) {
                node.Export = registration.Export;
            }
        }
    }
    /// <summary>Returns the view of the world's frame an instance numbered for one of the presentation's own views renders:
    /// the numbered view, or the last of the presentation's own views when the frame has fewer, never a camera view's.</summary>
    /// <param name="view">The view the instance's name numbers.</param>
    /// <returns>The view's index in the world's frame.</returns>
    public int HostView(int view) => Math.Clamp(
        max: Math.Max(
            val1: 0,
            val2: (m_hostViewCount - 1)
        ),
        min: 0,
        value: view
    );

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
    // One camera view's registration: the WorldCamera row it embodies (advanced by edits in place) — the diff baseline
    // ReconcileCameras works against — the 1-based seat a seat-relative registration resolves for (1 for a shared
    // registration), the rig and anchor that pose it, and the probe export its view's node renders into.
    private sealed class CameraRegistration {
        public Func<int>? AnchorIdSource { get; set; }
        public ISdfAnchorSource? AnchorSource { get; set; }
        public IShaderPipelineOutputExport? Export { get; set; }
        public ISdfCameraRig? Rig { get; set; }
        public required WorldCamera Row { get; set; }
        public required int Seat { get; init; }
    }
    // One screen's camera view: the registration name its instance runs under.
    private sealed record ViewFeed(string Name);
}
