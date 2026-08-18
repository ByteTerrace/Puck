using System.Numerics;
using Puck.Abstractions.Presentation;
using Puck.SdfVm;
using Puck.SdfVm.Views;
using Puck.World.Client;

namespace Puck.World;

internal sealed partial class WorldScreenBinder {
    // A same-dimensions pose/aim/FOV/rig/anchor edit re-wires the LIVE view in place (a freshly compiled rig plus its
    // anchor sources) — the offscreen engine, its ViewStack budget entry, and every wired slot survive untouched. The
    // registration's row snapshot advances so the next reconcile diffs against what the view now embodies.
    private void ApplyCameraPose(CameraRegistration registration, WorldCamera camera) {
        ConfigureCameraView(
            view: registration.View,
            camera: camera
        );

        registration.Row = camera;
    }
    // The reconcile-side View bind: a failed bind (unknown camera, unconfigured pool) still releases the PRIOR view —
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
    // Compiles the camera axes and wires their reference-frame source.
    private void ConfigureCameraView(SdfCameraView view, WorldCamera camera) {
        var referenceOffset = Vector3.Zero;

        switch (camera.Anchor) {
            case null:
                view.AnchorSource = null;
                view.AnchorIdSource = null;

                break;
            case WorldAnchor.Entity entity:
                view.AnchorSource = m_anchors;
                view.AnchorIdSource = () => entity.Index;

                break;
            case WorldAnchor.EntityPart part:
                view.AnchorSource = new EntityPartAnchorSource(
                    owner: this,
                    part: part
                );
                view.AnchorIdSource = static () => 0;

                break;
            case WorldAnchor.Placement placement:
                view.AnchorSource = new FixedAnchorSource(anchor: new SdfAnchor(
                    Position: StaticAnchorPosition(placement: placement),
                    Orientation: Quaternion.Identity
                ));
                view.AnchorIdSource = static () => 0;

                break;
            case WorldAnchor.Group group:
                view.AnchorSource = new FixedAnchorSource(anchor: new SdfAnchor(
                    Position: GroupCentroid(group: group),
                    Orientation: Quaternion.Identity
                ));
                view.AnchorIdSource = static () => 0;

                break;
        }

        view.Rig = WorldCameraRigCompiler.Compile(
            rig: camera.Rig,
            referenceOffset: referenceOffset
        );
    }
    // The one-shot centroid of a group anchor. A filmed/offscreen view bakes only this raw centroid: it DROPS the group
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
    // Creates the view pool on first need and registers (or updates in place, idempotent per name) one persistent
    // SdfCameraView for a camera. Fixed cameras carry their own world-space look-at; anchored cameras resolve their
    // WorldAnchor's entity each frame and pose a FirstPersonRig at the resolved anchor-local offset. A camera FILMS
    // an already-lit world, so it is a budgeted offscreen render with no room glow of its own.
    private void RegisterCameraView(WorldCamera camera) {
        m_viewStack ??= new ViewStack();

        if (!m_cameraViews.TryGetValue(
            key: camera.Name,
            value: out var registration
        )) {
            var view = new SdfCameraView(
                services: m_viewServices!,
                hostsOnDirectX: m_viewHostsOnDirectX,
                programWordCapacity: m_viewProgramWordCapacity,
                instanceCapacity: m_viewInstanceCapacity,
                dynamicTransformCapacity: m_viewDynamicTransformCapacity,
                width: camera.RenderWidth,
                height: camera.RenderHeight
            ) {
                // The result is sampled by a 160x144 diegetic panel. Re-marching full soft shadows and AO here cost
                // almost as much as the main view's lighting despite contributing only a tiny screen-space image.
                DisableAmbientOcclusion = true,
                DisableSoftShadows = true,
            };

            ConfigureCameraView(
                camera: camera,
                view: view
            );

            registration = new CameraRegistration { Row = camera, View = view };
            m_cameraViews[camera.Name] = registration;
        }

        _ = m_viewStack.Register(
            name: camera.Name,
            content: registration.View,
            band: ScreenSlotPriority.Ambient
        );
    }
    // A removed camera row: every slot filming it unbinds (a slot whose DECLARED source still names it — possible only
    // transiently inside one delivery, the validator rejects a durable dangling reference — keeps a visible fault), and
    // the registration is released so its offscreen engine stops spending budget.
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

        m_viewStack?.Release(name: name);
        _ = m_cameraViews.Remove(key: name);
        Console.Error.WriteLine(value: $"[world.camera: view '{name}' released — camera removed]");
    }
    // After a slot stops filming a camera (a screen removal OR any source transition away from it),
    // recompute the surviving wired set: an empty set RELEASES the view (ViewStack.Release disposes the SdfCameraView,
    // freeing its offscreen SdfWorldEngine) and drops the cached registration so a later screen.source <index> view rebuilds it
    // fresh; a non-empty set (another jumbotron still films this camera) only re-narrows the self-reference set to the
    // survivors. The boot-sized ViewStack pool itself stays alive — only this camera's registration ends.
    private void ReleaseOrphanedCameraView(string name) {
        if (m_viewStack is not { } stack) {
            return;
        }

        var wired = WiredScreensFor(name: name);

        if (wired.Count == 0) {
            stack.Release(name: name);
            _ = m_cameraViews.Remove(key: name);
            Console.Error.WriteLine(value: $"[world.screen: camera view '{name}' released — no remaining screen references it]");
        } else {
            stack.SetWiredScreens(
                name: name,
                screenIndices: wired
            );
        }
    }
    private void ReleaseOrphanedCameraViews(HashSet<string> candidates) {
        foreach (var name in candidates) {
            ReleaseOrphanedCameraView(name: name);
        }
    }
    // Drops a slot's jumbotron view reference and releases (or re-narrows) its camera registration — the symmetric
    // half of TryView's acquire, run whenever the slot stops filming that camera.
    private void ReleaseSlotView(ScreenSlot slot) {
        if (slot.View is not { } view) {
            return;
        }

        slot.View = null;
        ReleaseOrphanedCameraView(name: view.Name);
    }
    // Resolves a placeable-camera name against the world's declared cameras (ordinal), or null when none matches.
    private WorldCamera? ResolveCamera(string name) {
        foreach (var camera in m_cameras) {
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
    // The set of screen indices currently wired to a camera name — the self-reference set the ViewStack zeroes inside
    // that view's own render.
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

    /// <summary>Reconciles the live camera-view machinery to a mutated camera list — the live-application half of an
    /// <c>UpsertCamera</c>/<c>RemoveCamera</c> world mutation, called by the frame source when the definition revision
    /// moves (before <see cref="ReconcileScreens"/>, so a same-delivery View source change resolves the new rows). The
    /// stored row list is replaced (later resolves read live data); then, for each camera with a registered offscreen
    /// view: a pose/aim/FOV edit of the same kind writes the live rig's properties in place (the offscreen engine and
    /// its budget entry survive), a dimension or kind change releases and recreates the view (an offscreen render
    /// target cannot resize), and a removed row releases the view and unbinds every slot that filmed it. A declared
    /// View slot that faulted at boot (its camera did not exist yet) self-heals when the camera row arrives. Bounded by
    /// <see cref="OffscreenRenderBudget.RegisteredViews"/> and the refresh-divisor budget; dimensions are validator-capped.</summary>
    /// <param name="cameras">The mutated camera list (the live definition's cameras).</param>
    public void ReconcileCameras(IReadOnlyList<WorldCamera> cameras) {
        if (m_disposed) {
            return;
        }

        m_cameras = cameras;

        // Walk a snapshot of the registered names (the release/recreate paths mutate m_cameraViews).
        m_cameraReconcileScratch.Clear();
        m_cameraReconcileScratch.AddRange(collection: m_cameraViews.Keys);

        foreach (var name in m_cameraReconcileScratch) {
            var registration = m_cameraViews[name];

            if (ResolveCamera(name: name) is not { } next) {
                ReleaseCameraRow(name: name);

                continue;
            }

            if (Equals(
                objA: next,
                objB: registration.Row
            )) {
                continue;
            }

            if (
                (next.RenderWidth != registration.Row.RenderWidth) ||
                (next.RenderHeight != registration.Row.RenderHeight)
            ) {
                // The offscreen render target is sized (and the rig shaped) at construction: release the registration
                // (ViewStack.Release disposes the SdfCameraView and its engine) and rebuild fresh from the new row,
                // re-narrowing the survivors' self-reference set.
                m_viewStack?.Release(name: name);
                _ = m_cameraViews.Remove(key: name);
                RegisterCameraView(camera: next);
                m_viewStack?.SetWiredScreens(
                    name: name,
                    screenIndices: WiredScreensFor(name: name)
                );
                Console.Error.WriteLine(value: $"[world.camera: '{name}' recreated live ({next.RenderWidth}x{next.RenderHeight})]");
            } else {
                ApplyCameraPose(
                    camera: next,
                    registration: registration
                );
                Console.Error.WriteLine(value: $"[world.camera: '{name}' pose updated live]");
            }
        }

        // Self-heal: a declared View slot left faulted (its camera name was undeclared at bind time) binds now that
        // the row exists — the same TryView machinery a screen.source <index> view runs. A live runtime producer (an inserted
        // machine overlaying the declared view) is never displaced.
        foreach (var slot in m_slots.Values) {
            if (
                (slot.View is null) &&
                !slot.HasLive &&
                (slot.DeclaredSource is WorldScreenSource.View declared) &&
                (ResolveCamera(name: declared.CameraName) is not null) &&
                (m_viewServices is not null)
            ) {
                var outcome = TryView(
                    index: slot.Index,
                    cameraName: declared.CameraName
                );

                Console.Error.WriteLine(value: $"[world.camera: {outcome.Message}]");
            }
        }
    }
    /// <summary>Points a declared screen at a placeable camera — the runtime <c>screen.source &lt;index&gt; view</c> path. Any existing
    /// producer on the slot is cleared first. Requires the view pool to have been configured (it is, at startup); fails
    /// loudly for an undeclared screen, an unknown camera name, or an unconfigured pool.</summary>
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

        if (m_viewServices is null) {
            return (Ok: false, Message: "the view pool is not configured");
        }

        if (ResolveCamera(name: cameraName) is not { } camera) {
            return (Ok: false, Message: $"camera '{cameraName}' not declared");
        }

        var previousView = slot.View;

        RegisterCameraView(camera: camera);
        slot.ClearLive();
        slot.View = new ViewFeed(name: camera.Name) { Stack = m_viewStack };
        slot.DeclaredFault = null;
        m_viewStack!.SetWiredScreens(
            name: camera.Name,
            screenIndices: WiredScreensFor(name: camera.Name)
        );

        // A re-point away from another camera releases (or re-narrows) the superseded registration AFTER the new bind,
        // so a view no slot films stops rendering (the View A → View B case).
        if (
            (previousView is { } previous) &&
            !string.Equals(
            a: previous.Name,
            b: camera.Name,
            comparisonType: StringComparison.Ordinal
        )
        ) {
            ReleaseOrphanedCameraView(name: previous.Name);
        }

        return (Ok: true, Message: $"screen {index} showing camera '{camera.Name}'");
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
    // One persistent camera-view registration: the live SdfCameraView plus the WorldCamera row it currently embodies
    // (advanced by pose edits, replaced wholesale on recreate) — the diff baseline ReconcileCameras works against.
    private sealed class CameraRegistration {
        public required WorldCamera Row { get; set; }
        public required SdfCameraView View { get; init; }
    }
    // One named jumbotron view a screen samples: the shared ViewStack (set at ConfigureViews) and the camera name to
    // resolve against it. A camera FILMS an already-lit world, so its glow is the ViewStack's own (zero for a camera).
    private sealed class ViewFeed(string name) {
        public string Name { get; } = name;
        public ViewStack? Stack { get; set; }

        public nint Handle() => (Stack?.Resolve(name: Name) ?? 0);
        public Vector3 Light() => (Stack?.ResolveGlow(name: Name) ?? Vector3.Zero);
    }
}
