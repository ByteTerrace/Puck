using System.Diagnostics;
using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Puck.SdfVm;
using Puck.SdfVm.Queries;
using Puck.SdfVm.Views;

namespace Puck.World.Client;

/// <summary>
/// Traveler-follow stage 1's away-render content: <see cref="WorldSessionSceneEmitter"/>'s composition (static
/// placement geometry + mirrored avatars, over the identical <see cref="WorldSessionMirror"/> shape) with one
/// difference — its seat view follows the arrived body's interpolated destination pose. Authored named-camera and
/// layout overrides compose exactly as they do at a direct boot; an ordinary seat slot resolves through the
/// destination's native <see cref="WorldSeatCameraResolver"/> path.
/// </summary>
/// <remarks>
/// <para><b>The camera.</b> Resolves the destination's own authored <c>views.seatRig</c> (<see cref="WorldCameraRig"/>)
/// and <c>playerDefaults.seatLook</c> (<see cref="WorldSeatLook"/>) via <see cref="WorldSeatCameraResolver"/> for an
/// ordinary seat view — the
/// same shared path <c>WorldFrameSource.ResolveCamera</c> uses for a local boot seat, and the same rig-structure
/// structure and the tracked follower's seat-owned live view offset, keyed by that follower's local seat slot,
/// since the physical mouse/pointer travels with the seat across a crossing) against the tracked mirrored entity's
/// interpolated pose. There is no editor-rig override here (editing is structurally boot-instance-only — see
/// <c>WorldEditorSession</c>'s own remarks), so the resolved chase is always the plain path <see cref="WorldSeatCameraResolver.Smooth"/>
/// eases. Falls back to a spawn-centroid overview (mirroring <see cref="WorldSessionSceneEmitter.ResolveOverviewCamera"/>)
/// when the tracked index is out of range or not currently active in the mirror.</para>
/// <para>Reuses <see cref="WorldPlacementStamper"/>/<see cref="WorldAvatarCatalog"/> directly, exactly like
/// <see cref="WorldSessionSceneEmitter"/> — no second implementation of either.</para>
/// </remarks>
internal sealed class AwaySeatSceneEmitter : ISdfSceneEmitter, ISdfFrameDresser {
    /// <summary>The tracked entity resolved this call: the mirrored avatar index the chase camera follows
    /// (<see cref="InstanceSlot"/>) and the local physical seat slot supplying its own live orbit-drag input
    /// (<see cref="LocalSlot"/>) — usually the same seat that crossed in, but re-pointed by
    /// <see cref="WorldAwaySeatViews.Reconcile"/> to whichever follower is lowest-slotted when several share one
    /// destination.</summary>
    public readonly record struct TrackedTarget(int InstanceSlot, int LocalSlot);

    private readonly WorldSessionMirror m_mirror;
    private readonly Func<WorldSeatViewState?> m_viewState;
    private readonly Func<TrackedTarget> m_trackedTarget;
    private readonly Func<string?> m_layoutOverride;
    private readonly Func<string?> m_cameraOverride;
    private readonly WorldBorderMarginSceneEmitter? m_borderMargin;
    private readonly WorldViewComposer m_composer = new();
    private readonly List<SdfViewSnapshot> m_views = new();
    private readonly Dictionary<string, GroupAnchorState> m_groupAnchors = new(comparer: StringComparer.Ordinal);
    private SdfFieldEvaluator? m_cameraClearanceField;
    private int m_cameraClearanceDefinitionRevision = int.MinValue;
    private float m_elapsedSeconds;
    private SdfProgram? m_lastProgram;
    private readonly float[] m_avatarGaitPhases = new float[WorldAvatarCatalog.Capacity];
    private readonly Vector3[] m_avatarPreviousPositions = new Vector3[WorldAvatarCatalog.Capacity];
    private readonly bool[] m_avatarPoseSeeded = new bool[WorldAvatarCatalog.Capacity];
    private bool m_missingTrackedTargetNarrated;
    private int m_avatarSlotBase = -1;

    private struct GroupAnchorState {
        public Vector3 Centroid;
        public float Spread;
        public bool Seeded;
    }

    /// <summary>Initializes the emitter over a resolved away-instance mirror, the shared live-orbit accumulator, and
    /// a live tracked-entity resolver.</summary>
    /// <param name="mirror">The followed instance's client-side mirror this emitter reads static geometry and
    /// mirrored avatars from.</param>
    /// <param name="viewState">Resolves the tracked local seat's view state.</param>
    /// <param name="trackedTarget">Resolves this call's tracked entity — a delegate, not a fixed value, because
    /// <see cref="WorldAwaySeatViews"/> may re-point a shared tracked instance to a different follower's own slot as
    /// seats arrive/depart it.</param>
    /// <param name="layoutOverride">Resolves the live composition layout override.</param>
    /// <param name="cameraOverride">Resolves the live composition camera override. Away composition consumes the
    /// same overrides as a direct boot in the destination.</param>
    /// <param name="borderMargin">The destination's live stitched-border renderer, when it has one, so camera
    /// clearance evaluates the same neighbour geometry the offscreen composition shows.</param>
    public AwaySeatSceneEmitter(WorldSessionMirror mirror, Func<WorldSeatViewState?> viewState, Func<TrackedTarget> trackedTarget, Func<string?> layoutOverride, Func<string?> cameraOverride, WorldBorderMarginSceneEmitter? borderMargin = null) {
        ArgumentNullException.ThrowIfNull(argument: mirror);
        ArgumentNullException.ThrowIfNull(argument: viewState);
        ArgumentNullException.ThrowIfNull(argument: trackedTarget);
        ArgumentNullException.ThrowIfNull(argument: layoutOverride);
        ArgumentNullException.ThrowIfNull(argument: cameraOverride);

        m_mirror = mirror;
        m_viewState = viewState;
        m_trackedTarget = trackedTarget;
        m_layoutOverride = layoutOverride;
        m_cameraOverride = cameraOverride;
        m_borderMargin = borderMargin;
    }

    /// <inheritdoc/>
    public void Emit(SdfProgramBuilder builder, in SdfEmitContext context) {
        var definition = m_mirror.Definition;

        if (context.Probe) {
            WorldSessionRenderEnvelope.EmitProbe(builder: builder, candidate: definition, bodyColor: m_mirror.BodyColor, slotBase: context.SlotBase, includeScreens: true);

            return;
        } else {
            WorldPlacementStamper.EmitStatic(builder: builder, creations: definition.Creations, placements: definition.Placements);
            EmitScreens(builder: builder, definition: definition);
        }

        EmitAvatars(builder: builder, probeWorstCase: false, slotBase: context.SlotBase);
    }

    /// <summary>Measures a proposed destination definition against this view's frozen render envelope.</summary>
    public (int Words, int Instances) MeasureCandidate(WorldDefinition candidate) =>
        WorldSessionRenderEnvelope.MeasureCandidate(candidate: candidate, bodyColor: m_mirror.BodyColor, includeScreens: true, includeBorderMargins: true);

    private static void EmitScreens(SdfProgramBuilder builder, WorldDefinition definition) {
        var facets = WorldCreationFacets.Derive(
            definition: definition,
            derivedFaceBase: WorldCreationFacets.DerivedFaceBase,
            derivedFaceScreens: definition.Authoring.DerivedFaceScreens
        );

        foreach (var screen in definition.Screens) {
            WorldScreenStamper.Emit(builder: builder, screen: screen);
        }

        foreach (var face in facets.Faces) {
            WorldScreenStamper.Emit(builder: builder, screen: face);
        }
    }

    /// <summary>The frozen transform-slot count — the same all-128-rig avatar catalog leaf capacity
    /// <see cref="WorldSessionSceneEmitter.DynamicSlotCount"/> reserves.</summary>
    public int DynamicSlotCount => WorldAvatarCatalog.DynamicTransformCapacity;

    /// <inheritdoc/>
    public int RevisionComponentCount => 2;

    /// <inheritdoc/>
    public void WriteRevision(Span<int> destination) {
        destination[0] = m_mirror.DefinitionRevision;
        destination[1] = m_mirror.SnapshotRevision;
    }

    /// <inheritdoc/>
    public void PackDynamicTransforms(Span<DynamicTransform> slots, in SdfEmitContext context) {
        m_avatarSlotBase = context.SlotBase;
        var avatars = slots.Slice(start: context.SlotBase, length: WorldAvatarCatalog.DynamicTransformCapacity);
        var alpha = ResolveInterpolationAlpha();

        for (var index = 0; (index < WorldAvatarCatalog.Capacity); index++) {
            if (!m_mirror.IsActive(index: index)) {
                m_avatarPoseSeeded[index] = false;

                continue;
            }

            var position = Vector3.Lerp(value1: m_mirror.PreviousPosition(index: index), value2: m_mirror.CurrentPosition(index: index), amount: alpha);
            var orientation = Quaternion.Lerp(quaternion1: m_mirror.PreviousOrientation(index: index), quaternion2: m_mirror.CurrentOrientation(index: index), amount: alpha);

            if (m_avatarPoseSeeded[index]) {
                var travelled = MathF.Min(x: Vector3.Distance(value1: position, value2: m_avatarPreviousPositions[index]), y: 0.25f);

                m_avatarGaitPhases[index] += (travelled * 8.0f);
            } else {
                m_avatarPoseSeeded[index] = true;
            }

            m_avatarPreviousPositions[index] = position;

            var look = m_mirror.Look(index: index);

            WorldAvatarCatalog.PackTransforms(
                avatar: index,
                rootPosition: position,
                rootOrientation: orientation,
                gaitPhase: (m_avatarGaitPhases[index] * look.Motion.GaitAmplitude),
                castsSoftShadow: false,
                transforms: avatars,
                rig: LookRig(look: look),
                scale: look.Scale
            );
        }
    }

    private void EmitAvatars(SdfProgramBuilder builder, bool probeWorstCase, int slotBase) {
        var bodyMaterials = new int[WorldAvatarCatalog.Capacity];
        var accentMaterials = new int[WorldAvatarCatalog.Capacity];
        var noseFactor = m_mirror.Definition.PlayerDefaults.NoseFactor;

        for (var index = 0; (index < WorldAvatarCatalog.Capacity); index++) {
            var bodyColor = m_mirror.BodyColor(index: index);

            bodyMaterials[index] = builder.AddMaterial(material: new SdfMaterial(Albedo: bodyColor));
            accentMaterials[index] = builder.AddMaterial(material: new SdfMaterial(Albedo: (bodyColor * noseFactor)));
        }

        WorldAvatarCatalog.Emit(
            builder: builder,
            isActive: m_mirror.IsActive,
            bodyMaterials: bodyMaterials,
            accentMaterials: accentMaterials,
            probeWorstCase: probeWorstCase,
            slotBase: slotBase,
            rigFor: (probeWorstCase ? null : index => LookRig(look: m_mirror.Look(index: index))),
            scaleFor: (probeWorstCase ? null : index => m_mirror.Look(index: index).Scale)
        );
    }

    // The honest render alpha — identical derivation to WorldSessionSceneEmitter's own (see that type's remarks):
    // real elapsed wall time since the mirror's current snapshot arrived, normalized by the destination's own step
    // duration.
    private float ResolveInterpolationAlpha() {
        var stepSeconds = m_mirror.StepSeconds;

        if (stepSeconds <= 0f) {
            return 1f;
        }

        var elapsedSeconds = (float)Stopwatch.GetElapsedTime(startingTimestamp: m_mirror.SnapshotArrivalTimestamp).TotalSeconds;

        return Math.Clamp(value: (elapsedSeconds / stepSeconds), min: 0f, max: 1f);
    }

    private static int LookRig(WorldLook look) => ((look.Source is WorldLookSource.Catalog { Index: { } pinned }) ? pinned : -1);

    /// <inheritdoc/>
    public SdfFrame Dress(SdfProgram program, DynamicTransform[] transforms, uint width, uint height, float deltaSeconds, float interpolationAlpha) {
        var programChanged = !ReferenceEquals(objA: program, objB: m_lastProgram);

        m_lastProgram = program;
        if (programChanged) {
            // A margin-neighbour revision rebuilds the composed program without changing the destination definition.
            m_cameraClearanceDefinitionRevision = int.MinValue;
        }
        m_elapsedSeconds += deltaSeconds;

        var definition = m_mirror.Definition;

        m_composer.Compose(
            joinedCount: 1,
            soleEditorIndex: -1,
            workbenchFraction: definition.Authoring.WorkbenchFraction,
            views: definition.Views,
            layoutOverride: m_layoutOverride(),
            cameraOverride: m_cameraOverride(),
            elapsedSeconds: m_elapsedSeconds
        );

        m_views.Clear();

        foreach (var composed in m_composer.Slots) {
            var region = composed.Region;

            if (composed.Camera is { } cameraName) {
                if (TryResolveComposedCamera(name: cameraName, region: region, width: width, height: height, deltaSeconds: deltaSeconds, transforms: transforms, camera: out var camera)) {
                    m_views.Add(item: new SdfViewSnapshot(Camera: camera, Region: region) {
                        RenderScale = m_composer.CurrentRenderScale,
                    });
                }

                continue;
            }

            if (composed.SeatOrder == 0) {
                m_views.Add(item: new SdfViewSnapshot(
                    Camera: ResolveChaseCamera(
                        width: Math.Max(val1: 1u, val2: (uint)(region.Width * width)),
                        height: Math.Max(val1: 1u, val2: (uint)(region.Height * height)),
                        deltaSeconds: deltaSeconds
                    ),
                    Region: region
                ) {
                    RenderScale = m_composer.CurrentRenderScale,
                });
            }
        }

        if (m_views.Count == 0) {
            m_views.Add(item: new SdfViewSnapshot(
                Camera: ResolveChaseCamera(width: width, height: height, deltaSeconds: deltaSeconds),
                Region: new NormalizedRect(X: 0f, Y: 0f, Width: 1f, Height: 1f)
            ));
        }

        return new SdfFrame(
            Program: program,
            ProgramChanged: programChanged,
            Views: m_views,
            Time: 0f,
            WarpAmount: 0f
        ) {
            DynamicTransforms = transforms,
            DisableAmbientOcclusion = true,
            DisableSoftShadows = true,
            DisableFarBound = true,
            DisableShadowEscapeExit = true,
            DisableShadowAccumulation = true,
        };
    }

    private bool TryResolveComposedCamera(string name, NormalizedRect region, uint width, uint height, float deltaSeconds, DynamicTransform[] transforms, out CameraSnapshot camera) {
        camera = default;
        var definition = m_mirror.Definition;
        WorldCamera? found = null;

        foreach (var row in definition.Cameras) {
            if (string.Equals(a: row.Name, b: name, comparisonType: StringComparison.Ordinal)) {
                found = row;

                break;
            }
        }

        if (found is not { } cameraRow) {
            return false;
        }

        var (position, orientation, spread) = ResolveComposedAnchor(name: name, definition: definition, anchor: cameraRow.Anchor, deltaSeconds: deltaSeconds, transforms: transforms);
        var rig = WorldCameraRigCompiler.Compile(rig: cameraRow.Rig, spread: spread);
        var anchor = new SdfAnchor(Position: position, Orientation: orientation);
        var clock = new SdfCameraClock(PresentationSeconds: m_elapsedSeconds, AuthoritativeTick: m_mirror.Tick);
        var (eye, target, fieldOfView) = rig.Resolve(anchor: in anchor, clock: in clock);
        camera = CameraSnapshot.LookAt(
            position: eye,
            target: target,
            fieldOfViewRadians: fieldOfView,
            viewportWidth: Math.Max(val1: 1u, val2: (uint)(region.Width * width)),
            viewportHeight: Math.Max(val1: 1u, val2: (uint)(region.Height * height))
        );

        return true;
    }

    private (Vector3 Position, Quaternion Orientation, float Spread) ResolveComposedAnchor(string name, WorldDefinition definition, WorldAnchor? anchor, float deltaSeconds, DynamicTransform[] transforms) {
        switch (anchor) {
            case WorldAnchor.Entity entity: {
                    var entityIndex = entity.Index;

                    if (((uint)entityIndex >= WorldAvatarCatalog.Capacity) || !m_mirror.IsActive(index: entityIndex)) {
                        return (Vector3.Zero, Quaternion.Identity, 0f);
                    }

                    var alpha = ResolveInterpolationAlpha();

                    return (
                        Vector3.Lerp(value1: m_mirror.PreviousPosition(index: entityIndex), value2: m_mirror.CurrentPosition(index: entityIndex), amount: alpha),
                        Quaternion.Lerp(quaternion1: m_mirror.PreviousOrientation(index: entityIndex), quaternion2: m_mirror.CurrentOrientation(index: entityIndex), amount: alpha),
                        0f
                    );
                }
            case WorldAnchor.EntityPart part: {
                    var entityIndex = part.Index;

                    if (((uint)entityIndex >= WorldAvatarCatalog.Capacity) || !m_mirror.IsActive(index: entityIndex)
                        || (m_avatarSlotBase < 0) || ((m_avatarSlotBase + WorldAvatarCatalog.DynamicTransformCapacity) > transforms.Length)) {
                        return (Vector3.Zero, Quaternion.Identity, 0f);
                    }

                    var look = m_mirror.Look(index: entityIndex);
                    var avatarTransforms = transforms.AsSpan(start: m_avatarSlotBase, length: WorldAvatarCatalog.DynamicTransformCapacity);

                    return (WorldAvatarCatalog.TryPartPose(avatar: entityIndex, partId: part.PartId, rig: LookRig(look: look), transforms: avatarTransforms, pose: out var pose)
                        ? (pose.Position, pose.Orientation, 0f)
                        : (Vector3.Zero, Quaternion.Identity, 0f));
                }
            case WorldAnchor.Placement placement:
                return (WorldAnchorGeometry.StaticPlacementPosition(definition: definition, placementId: placement.PlacementId, shapeId: placement.ShapeId), Quaternion.Identity, 0f);
            case WorldAnchor.Group group: {
                    var sum = Vector3.Zero;
                    var count = 0;
                    var alpha = ResolveInterpolationAlpha();

                    void Add(int index) {
                        if (((uint)index >= WorldAvatarCatalog.Capacity) || !m_mirror.IsActive(index: index)) {
                            return;
                        }

                        sum += Vector3.Lerp(value1: m_mirror.PreviousPosition(index: index), value2: m_mirror.CurrentPosition(index: index), amount: alpha);
                        count++;
                    }

                    if (group.Indices is { } indices) {
                        foreach (var index in indices) { Add(index: index); }
                    } else {
                        for (var index = 0; index < Math.Min(definition.Population.Capacity, WorldAvatarCatalog.Capacity); index++) { Add(index: index); }
                    }

                    var centroid = ((count == 0) ? Vector3.Zero : (sum / count));
                    var spread = 0f;

                    void AddSpread(int index) {
                        if (((uint)index < WorldAvatarCatalog.Capacity) && m_mirror.IsActive(index: index)) {
                            var position = Vector3.Lerp(value1: m_mirror.PreviousPosition(index: index), value2: m_mirror.CurrentPosition(index: index), amount: alpha);
                            spread += Vector3.Distance(value1: position, value2: centroid);
                        }
                    }

                    if (group.Indices is { } members) {
                        foreach (var index in members) { AddSpread(index: index); }
                    } else {
                        for (var index = 0; index < Math.Min(definition.Population.Capacity, WorldAvatarCatalog.Capacity); index++) { AddSpread(index: index); }
                    }

                    spread = ((count == 0) ? 0f : (spread / count));
                    var state = m_groupAnchors.GetValueOrDefault(key: name);

                    if (!state.Seeded) {
                        state = new GroupAnchorState { Centroid = centroid, Spread = spread, Seeded = true };
                    } else {
                        var ease = (1f - MathF.Exp(x: (-MathF.Max(x: group.SmoothRate, y: 0f) * MathF.Max(x: deltaSeconds, y: 0f))));
                        state.Centroid = Vector3.Lerp(value1: state.Centroid, value2: centroid, amount: ease);
                        state.Spread += ((spread - state.Spread) * ease);
                    }

                    m_groupAnchors[name] = state;

                    return (state.Centroid, Quaternion.Identity, state.Spread);
                }
            default:
                return (Vector3.Zero, Quaternion.Identity, 0f);
        }
    }

    // The destination's own native seat-camera path (see this type's own remarks): compiles the destination's
    // authored views.seatRig, composes the tracked follower's live orbit-drag offset against its own
    // playerDefaults.seatLook exactly like WorldFrameSource.ResolveCamera's boot-seat path, and eases through the
    // same rig-level SmoothRate. WorldAxes below is destination rig structure per
    // WorldSeatCameraResolver.ResolveSeatLook's split — always this destination's own document, never the
    // traveler's profile — the same split WorldFrameSource.ResolveCamera now applies for a boot seat, so the two
    // paths agree on where structure comes from. The live yaw/pitch offset itself (m_cameraOrbit, read below) was
    // accumulated by WorldSeatViewInput against the local seat's own control feel at drag time; that consumer now
    // resolves rig structure through the seat's own live route (WorldInstanceHost.ResolveRoutedDefinition) exactly
    // like this call does, and reclamps a carried orbit the instant WorldSeatInstanceRouter reports the route
    // itself changed — so a traveling seat's live pitch is already clamped against THIS destination's own range
    // by the time it reaches here, not wherever the seat sat when the drag happened. Falls back to a spawn-centroid
    // overview (mirroring
    // WorldSessionSceneEmitter.ResolveOverviewCamera) when the tracked index is out of range or not currently active
    // in the mirror.
    private CameraSnapshot ResolveChaseCamera(uint width, uint height, float deltaSeconds) {
        var tracked = m_trackedTarget();

        if (((uint)tracked.InstanceSlot >= WorldAvatarCatalog.Capacity) || !m_mirror.IsActive(index: tracked.InstanceSlot)) {
            if (!m_missingTrackedTargetNarrated) {
                m_missingTrackedTargetNarrated = true;
                Console.Error.WriteLine(value: $"[world.projection: tracked body {tracked.InstanceSlot} for local seat {(tracked.LocalSlot + 1)} is absent from snapshot tick {m_mirror.Tick}; using overview until it arrives]");
            }
            return ResolveOverviewCamera(width: width, height: height);
        }

        m_missingTrackedTargetNarrated = false;

        var alpha = ResolveInterpolationAlpha();
        var position = Vector3.Lerp(value1: m_mirror.PreviousPosition(index: tracked.InstanceSlot), value2: m_mirror.CurrentPosition(index: tracked.InstanceSlot), amount: alpha);
        var orientation = Quaternion.Lerp(quaternion1: m_mirror.PreviousOrientation(index: tracked.InstanceSlot), quaternion2: m_mirror.CurrentOrientation(index: tracked.InstanceSlot), amount: alpha);
        var definition = m_mirror.Definition;
        var authoredRig = definition.Views.SeatRig;
        var view = m_viewState();
        if (view is null) {
            return ResolveOverviewCamera(width: width, height: height);
        }
        var chase = view.ResolveChase(views: definition.Views, bodyOrientation: orientation);

        var anchor = new SdfAnchor(Position: position, Orientation: orientation);
        var clock = new SdfCameraClock(PresentationSeconds: m_elapsedSeconds, AuthoritativeTick: m_mirror.Tick);
        var (eye, target, fieldOfView) = chase.Resolve(anchor: in anchor, clock: in clock);

        view.Smooth(rate: authoredRig.SmoothRate, enabled: true, deltaSeconds: deltaSeconds, eye: ref eye, target: ref target);
        EnsureCameraClearanceField();
        eye = WorldCameraClearance.Resolve(field: m_cameraClearanceField, desiredEye: eye, target: target);

        return CameraSnapshot.LookAt(
            position: eye,
            target: target,
            fieldOfViewRadians: fieldOfView,
            viewportWidth: Math.Max(val1: 1u, val2: width),
            viewportHeight: Math.Max(val1: 1u, val2: height)
        );
    }

    private void EnsureCameraClearanceField() {
        var revision = m_mirror.DefinitionRevision;

        if (revision == m_cameraClearanceDefinitionRevision) {
            return;
        }

        m_cameraClearanceDefinitionRevision = revision;
        m_cameraClearanceField = null;

        try {
            var definition = m_mirror.Definition;
            var builder = new SdfProgramBuilder();

            WorldPlacementStamper.EmitStatic(builder: builder, creations: definition.Creations, placements: definition.Placements);
            EmitScreens(builder: builder, definition: definition);
            m_borderMargin?.EmitCurrent(builder: builder);
            m_cameraClearanceField = new SdfFieldEvaluator(program: builder.Build(buildInstanceGrid: false));
        } catch (ArgumentException) {
            // Some authored render-only operations intentionally sit outside the deterministic query evaluator. Such
            // a world keeps its exact authored eye; supported worlds still receive the clearance correction above.
        }
    }

    // The spawn-centroid overview — the same construction WorldSessionSceneEmitter.ResolveOverviewCamera uses.
    private CameraSnapshot ResolveOverviewCamera(uint width, uint height) {
        var definition = m_mirror.Definition;
        var centroid = Vector3.Zero;
        var resolved = 0;

        foreach (var name in definition.Population.SeatSpawns) {
            if (WorldDefinitionRows.FindSpawnPoint(spawnPoints: definition.SpawnPoints, id: name) is { } spawn) {
                centroid += spawn.Position;
                resolved++;
            }
        }

        if (resolved > 0) {
            centroid /= resolved;
        }

        var target = (centroid + new Vector3(x: 0f, y: 1f, z: 0f));
        var eye = (centroid + new Vector3(x: 0f, y: 14f, z: 18f));

        return CameraSnapshot.LookAt(
            position: eye,
            target: target,
            fieldOfViewRadians: (MathF.PI / 3f),
            viewportWidth: Math.Max(val1: 1u, val2: width),
            viewportHeight: Math.Max(val1: 1u, val2: height)
        );
    }
}
