using System.Diagnostics;
using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Puck.SdfVm;
using Puck.SdfVm.Views;

namespace Puck.World.Client;

/// <summary>
/// Traveler-follow stage 1's away-render content: <see cref="WorldSessionSceneEmitter"/>'s composition (static
/// placement geometry + mirrored avatars, over the identical <see cref="WorldSessionMirror"/> shape) with one
/// difference — the camera resolves through the destination's own native seat-camera path
/// (<see cref="WorldSeatCameraResolver"/>) against the tracked entity's interpolated pose, instead of a named/
/// default camera row, so the image frames the destination exactly as one of its own local seats would.
/// </summary>
/// <remarks>
/// <para><b>The camera.</b> Resolves the destination's own authored <c>views.seatRig</c> (<see cref="WorldCameraRig"/>)
/// and <c>playerDefaults.seatLook</c> (<see cref="WorldSeatLook"/>) via <see cref="WorldSeatCameraResolver"/> — the
/// same shared path <c>WorldFrameSource.ResolveCamera</c> uses for a local boot seat, and the same rig-structure
/// half <see cref="WorldSeatCameraResolver.ResolveSeatLook"/> names — composing the tracked
/// follower's own live orbit-drag offset (<see cref="WorldCameraOrbit"/>, keyed by that follower's local seat slot,
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
    private readonly WorldCameraOrbit m_cameraOrbit;
    private readonly Func<TrackedTarget> m_trackedTarget;
    private readonly Func<string?> m_cameraOverride;
    private readonly WorldSeatCameraResolver.LiveOrbitCache m_orbitCache = new();
    private readonly WorldSeatCameraResolver.SmoothingState m_smoothState = new();
    private ISdfCameraRig? m_chaseRig;
    private int m_chaseRigDefinitionRevision = int.MinValue;
    private float m_elapsedSeconds;
    private SdfProgram? m_lastProgram;
    private readonly float[] m_avatarGaitPhases = new float[WorldAvatarCatalog.Capacity];
    private readonly Vector3[] m_avatarPreviousPositions = new Vector3[WorldAvatarCatalog.Capacity];
    private readonly bool[] m_avatarPoseSeeded = new bool[WorldAvatarCatalog.Capacity];

    /// <summary>Initializes the emitter over a resolved away-instance mirror, the shared live-orbit accumulator, and
    /// a live tracked-entity resolver.</summary>
    /// <param name="mirror">The followed instance's client-side mirror this emitter reads static geometry and
    /// mirrored avatars from.</param>
    /// <param name="cameraOrbit">Every local seat's live camera-orbit accumulator — the same store the boot-instance
    /// camera path reads, keyed by the tracked follower's own local seat slot so a seat's live mouse drag keeps
    /// steering its view across a crossing.</param>
    /// <param name="trackedTarget">Resolves this call's tracked entity — a delegate, not a fixed value, because
    /// <see cref="WorldAwaySeatViews"/> may re-point a shared tracked instance to a different follower's own slot as
    /// seats arrive/depart it.</param>
    /// <param name="cameraOverride">Resolves the live composition camera override. An away view consumes the same
    /// override as the boot frame so crossing cannot freeze the visible camera on the native chase rig.</param>
    public AwaySeatSceneEmitter(WorldSessionMirror mirror, WorldCameraOrbit cameraOrbit, Func<TrackedTarget> trackedTarget, Func<string?> cameraOverride) {
        ArgumentNullException.ThrowIfNull(argument: mirror);
        ArgumentNullException.ThrowIfNull(argument: cameraOrbit);
        ArgumentNullException.ThrowIfNull(argument: trackedTarget);
        ArgumentNullException.ThrowIfNull(argument: cameraOverride);

        m_mirror = mirror;
        m_cameraOrbit = cameraOrbit;
        m_trackedTarget = trackedTarget;
        m_cameraOverride = cameraOverride;
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
        m_elapsedSeconds += deltaSeconds;

        var camera = (ResolveOverrideCamera(width: width, height: height) ?? ResolveChaseCamera(width: width, height: height, deltaSeconds: deltaSeconds));

        return new SdfFrame(
            Program: program,
            ProgramChanged: programChanged,
            Views: [new SdfViewSnapshot(Camera: camera, Region: new NormalizedRect(X: 0f, Y: 0f, Width: 1f, Height: 1f))],
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

    // The global view.override camera lane, resolved against the FOLLOWED instance's own live definition. Before
    // this seam the command state kept changing after a crossing while the pixels came from this emitter's chase
    // camera forever. A name absent from this destination falls back to the ordinary chase rather than freezing a
    // stale row from the previous world.
    private CameraSnapshot? ResolveOverrideCamera(uint width, uint height) {
        if (m_cameraOverride() is not { } wanted) {
            return null;
        }

        var definition = m_mirror.Definition;
        WorldCamera? found = null;

        foreach (var camera in definition.Cameras) {
            if (string.Equals(a: camera.Name, b: wanted, comparisonType: StringComparison.Ordinal)) {
                found = camera;

                break;
            }
        }

        if (found is not { } row) {
            return null;
        }

        var (position, orientation) = ResolveOverrideAnchor(definition: definition, anchor: row.Anchor);
        var rig = WorldCameraRigCompiler.Compile(rig: row.Rig);
        var anchor = new SdfAnchor(Position: position, Orientation: orientation);
        var clock = new SdfCameraClock(PresentationSeconds: m_elapsedSeconds, AuthoritativeTick: m_mirror.Tick);
        var (eye, target, fieldOfView) = rig.Resolve(anchor: in anchor, clock: in clock);

        return CameraSnapshot.LookAt(
            position: eye,
            target: target,
            fieldOfViewRadians: fieldOfView,
            viewportWidth: Math.Max(val1: 1u, val2: width),
            viewportHeight: Math.Max(val1: 1u, val2: height)
        );
    }

    private (Vector3 Position, Quaternion Orientation) ResolveOverrideAnchor(WorldDefinition definition, WorldAnchor? anchor) {
        switch (anchor) {
            case WorldAnchor.Entity entity when ((uint)entity.Index < WorldAvatarCatalog.Capacity) && m_mirror.IsActive(index: entity.Index): {
                    var alpha = ResolveInterpolationAlpha();

                    return (
                        Vector3.Lerp(value1: m_mirror.PreviousPosition(index: entity.Index), value2: m_mirror.CurrentPosition(index: entity.Index), amount: alpha),
                        Quaternion.Lerp(quaternion1: m_mirror.PreviousOrientation(index: entity.Index), quaternion2: m_mirror.CurrentOrientation(index: entity.Index), amount: alpha)
                    );
                }
            case WorldAnchor.Placement placement:
                return (WorldAnchorGeometry.StaticPlacementPosition(definition: definition, placementId: placement.PlacementId, shapeId: placement.ShapeId), Quaternion.Identity);
            default:
                return (Vector3.Zero, Quaternion.Identity);
        }
    }

    // The destination's own native seat-camera path (see this type's own remarks): compiles the destination's
    // authored views.seatRig, composes the tracked follower's live orbit-drag offset against its own
    // playerDefaults.seatLook exactly like WorldFrameSource.ResolveCamera's boot-seat path, and eases through the
    // same rig-level SmoothRate. WorldAxes below is destination rig structure per
    // WorldSeatCameraResolver.ResolveSeatLook's split — always this destination's own document, never the
    // traveler's profile — the same split WorldFrameSource.ResolveCamera now applies for a boot seat, so the two
    // paths agree on where structure comes from. The live yaw/pitch offset itself (m_cameraOrbit, read below) was
    // accumulated by WorldCameraOrbitDrag against the local seat's own control feel at drag time; that consumer now
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
            return ResolveOverviewCamera(width: width, height: height);
        }

        var alpha = ResolveInterpolationAlpha();
        var position = Vector3.Lerp(value1: m_mirror.PreviousPosition(index: tracked.InstanceSlot), value2: m_mirror.CurrentPosition(index: tracked.InstanceSlot), amount: alpha);
        var orientation = Quaternion.Lerp(quaternion1: m_mirror.PreviousOrientation(index: tracked.InstanceSlot), quaternion2: m_mirror.CurrentOrientation(index: tracked.InstanceSlot), amount: alpha);
        var definition = m_mirror.Definition;
        var authoredRig = definition.Views.SeatRig;

        if (m_chaseRigDefinitionRevision != m_mirror.DefinitionRevision) {
            m_chaseRigDefinitionRevision = m_mirror.DefinitionRevision;
            m_chaseRig = WorldCameraRigCompiler.Compile(rig: authoredRig);
        }

        var chase = WorldSeatCameraResolver.ResolveChase(
            authoredRig: authoredRig,
            compiledChase: m_chaseRig!,
            seatLookWorldAxes: definition.PlayerDefaults.SeatLook.WorldAxes,
            bodyOrientation: orientation,
            liveYaw: m_cameraOrbit.Yaw(slot: tracked.LocalSlot),
            livePitch: m_cameraOrbit.Pitch(slot: tracked.LocalSlot),
            cache: m_orbitCache
        );

        var anchor = new SdfAnchor(Position: position, Orientation: orientation);
        var clock = new SdfCameraClock(PresentationSeconds: m_elapsedSeconds, AuthoritativeTick: m_mirror.Tick);
        var (eye, target, fieldOfView) = chase.Resolve(anchor: in anchor, clock: in clock);

        WorldSeatCameraResolver.Smooth(state: m_smoothState, smoothRate: authoredRig.SmoothRate, isPlainChase: true, deltaSeconds: deltaSeconds, eye: ref eye, target: ref target);

        return CameraSnapshot.LookAt(
            position: eye,
            target: target,
            fieldOfViewRadians: fieldOfView,
            viewportWidth: Math.Max(val1: 1u, val2: width),
            viewportHeight: Math.Max(val1: 1u, val2: height)
        );
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
