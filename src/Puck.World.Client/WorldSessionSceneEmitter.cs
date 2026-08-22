using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Puck.SdfVm;
using Puck.SdfVm.Views;
using Puck.SignedDistance;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>
/// The session projection's content half: composes a destination world's static authored placement geometry plus its
/// live mirrored avatars (see <see cref="WorldSessionMirror"/>'s own remarks) into an <see cref="SdfProgramBuilder"/>,
/// and dresses the result into one <see cref="SdfFrame"/> framed through the destination's chosen camera — the
/// <see cref="ISdfSceneEmitter"/>/<see cref="ISdfFrameDresser"/> split <c>WorldFramePresenter</c> and
/// <c>WorldSceneEmitter</c> already establish, collapsed into one type here because a session
/// projection has exactly one content source and needs no second host to own presentation separately.
/// </summary>
/// <remarks>
/// <para>
/// Reuses <see cref="WorldPlacementStamper"/> directly — the same static-stamp compiler
/// <c>WorldSceneEmitter</c> calls for the boot world's own decoration placements — and
/// <see cref="WorldAvatarCatalog"/> directly for avatars, rather than a second implementation of either. No screens,
/// no editor overlay: a session mirror does not process the destination's own <c>screens</c> section at all, which is
/// what closes recursion structurally (a destination naming its own session screen has no path this type ever walks
/// into) — <c>WorldScreenBinder</c> still narrates the depth-1 policy by name when it detects that shape, so
/// the refusal is observable even though nothing here could recurse regardless. Vehicles (a body-rooted creation
/// stamp riding an inhabited placement) are out of this type's scope: <c>WorldStampPool</c>'s
/// <c>PackTransforms</c>/<c>RootPose</c> are hard-typed against a concrete <see cref="WorldClient"/> instance, not an
/// abstraction a <see cref="WorldSessionMirror"/> could satisfy without widening that pool's contract — a driven body
/// mirrors as its catalog avatar, never the vehicle's own geometry, until that seam is opened.
/// </para>
/// <para>
/// <b>The interpolation timebase.</b> <see cref="WorldSessionView"/> calls <c>ISdfFrameSource.CaptureFrame</c> with a
/// fixed zero delta/alpha on every resolve (see that type's own remarks: "never through the host's own clock") — so
/// neither <see cref="Dress"/>'s <c>interpolationAlpha</c> parameter nor <see cref="SdfEmitContext.InterpolationAlpha"/>
/// ever carries anything but 0 for this emitter. <see cref="WorldSessionMirror.InterpolationAlpha"/> derives its own honest
/// fraction instead, purely from the mirror's (tick, pose) pairs plus this call's own wall-clock arrival: real elapsed
/// time since <see cref="WorldSessionMirror.SnapshotArrivalTimestamp"/>, normalized by
/// <see cref="WorldSessionMirror.StepSeconds"/>. The destination's tick thread and the thread that calls
/// <see cref="PackDynamicTransforms"/> are the same process (frequently the same thread — see
/// <see cref="WorldSessionMirror"/>'s own threading remarks), so a real-time read between them is an honest
/// presentation-only measure, not a cross-machine clock assumption; it is the floor the task brief calls out
/// explicitly: interpolating between the two most recent mirrored snapshots is the honest ceiling of what a session
/// view — which is handed no simulation clock at all — can ever do.
/// </para>
/// </remarks>
public sealed class WorldSessionSceneEmitter : ISdfSceneEmitter, ISdfFrameDresser {
    // The BIND-time resolved camera choice: a validated, currently-present camera NAME, or null for "use the
    // destination's default projection" (its first declared camera, else the spawn-centroid overview) — see this
    // type's own construction site in WorldScreenBinder.TrySession, which is where the "unknown camera refuses at
    // bind with a loud note, falling back to the default projection" decision is made and narrated.
    private readonly string? m_effectiveCameraName;
    private readonly float m_fieldOfViewRadians;
    private readonly WorldSessionMirror m_mirror;

    private SdfProgram? m_lastProgram;
    // The WINDOW projection's per-produced-frame override — set by WorldScreenBinder.RenderViews (the one place with
    // access to both the local eye and the border pair's two face rows) immediately before this view's Resolve.
    // Null (the default, and every non-window session's steady state) leaves Dress on the ordinary camera path below.
    private (CameraSnapshot Camera, Vector2 Offset)? m_windowOverride;

    // Per-avatar movement-driven gait state, scratch reused across frames to keep packing allocation-free — the SAME
    // distance-driven approach Client.WorldSceneEmitter.PackDynamicTransforms uses, over this emitter's own
    // interpolated (not host-supplied) positions.
    private readonly float[] m_avatarGaitPhases = new float[WorldAvatarCatalog.Capacity];
    private readonly Vector3[] m_avatarPreviousPositions = new Vector3[WorldAvatarCatalog.Capacity];
    private readonly bool[] m_avatarPoseSeeded = new bool[WorldAvatarCatalog.Capacity];
    private readonly WorldEntityAddress[] m_avatarMotionAddresses = new WorldEntityAddress[WorldAvatarCatalog.Capacity];
    private readonly int[] m_emittedRigs = new int[WorldAvatarCatalog.Capacity];
    private readonly float[] m_emittedScales = new float[WorldAvatarCatalog.Capacity];
    private readonly float[] m_emittedGaitAmplitudes = new float[WorldAvatarCatalog.Capacity];

    /// <summary>Initializes the emitter over a resolved session mirror and a bind-time-resolved camera choice.</summary>
    /// <param name="mirror">The destination's client-side mirror this emitter reads static geometry from.</param>
    /// <param name="effectiveCameraName">A validated, currently-declared camera name, or <see langword="null"/> for
    /// the destination's default projection.</param>
    /// <param name="fieldOfViewRadians">The vertical field of view used only by the spawn-centroid overview fallback
    /// (a named camera row carries its own lens).</param>
    public WorldSessionSceneEmitter(WorldSessionMirror mirror, string? effectiveCameraName, float fieldOfViewRadians = (MathF.PI / 3f)) {
        ArgumentNullException.ThrowIfNull(argument: mirror);

        m_mirror = mirror;
        m_effectiveCameraName = effectiveCameraName;
        m_fieldOfViewRadians = fieldOfViewRadians;
    }

    // Registers the avatar palette and emits the catalog range — the probe branch (worst-case, every rig at unit
    // scale) and the live branch (only mirrored-active avatars, each sourcing its LOOK's pinned rig and uniform
    // scale) both flow through the ONE WorldAvatarCatalog.Emit call, exactly like Client.WorldSceneEmitter.Compose's
    // own avatar block.
    private void EmitAvatars(SdfProgramBuilder builder, bool probeWorstCase, int slotBase) {
        var bodyMaterials = new int[WorldAvatarCatalog.Capacity];
        var accentMaterials = new int[WorldAvatarCatalog.Capacity];
        var noseFactor = m_mirror.Definition.PlayerDefaults.NoseFactor;

        for (var index = 0; (index < WorldAvatarCatalog.Capacity); index++) {
            var bodyColor = m_mirror.BodyColor(index: index);
            var look = m_mirror.Look(index: index);

            m_emittedRigs[index] = LookRig(
                look: look,
                catalogRig: m_mirror.CatalogRig(index: index)
            );
            m_emittedScales[index] = look.Scale;
            m_emittedGaitAmplitudes[index] = look.Motion.GaitAmplitude;

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
            rigFor: (probeWorstCase
            ? null
            : index => m_emittedRigs[index]),
            scaleFor: (probeWorstCase
            ? null
            : index => m_emittedScales[index])
        );
    }
    // The catalog geometry-source rig for a look: an authored Catalog(Index) pin, or the occupant-owned carried rig
    // for an unpinned catalog OR a Creation look — the identical selector Client.WorldSceneEmitter.LookRig applies.
    // A Creation look's body would render through the stamp pool on the boot path; this emitter has no stamp-pool
    // seam (see this type's own remarks), so a mirrored Creation-look body still renders as its catalog avatar.
    private static int LookRig(WorldLook look, byte catalogRig) => ((look.Source is WorldLookSource.Catalog { Index: { } pinned })
        ? pinned
        : catalogRig
    );
    // The camera row's anchor pose, restricted to what a STATIC-geometry-only mirror can resolve: a Placement anchor
    // reads the destination's own authored transform (real data, no pose mirror needed); Entity/EntityPart/Group
    // anchors have no live body pose to read this wave (see WorldSessionMirror's own staged-boundary remarks) and
    // resolve to the world origin rather than reaching into state that was never mirrored.
    private static (Vector3 Position, Quaternion Orientation) ResolveAnchorPose(WorldDefinition definition, WorldAnchor? anchor) {
        if (anchor is WorldAnchor.Placement placement) {
            return (WorldAnchorGeometry.StaticPlacementPosition(
                definition: definition,
                placementId: placement.PlacementId,
                shapeId: placement.ShapeId
            ), Quaternion.Identity);
        }

        return (Vector3.Zero, Quaternion.Identity);
    }
    // Resolves this frame's camera: the bind-time-effective named camera if it still exists, else the destination's
    // first declared camera, else a fixed overview derived from its spawn points.
    // Re-resolved every frame from the LIVE mirrored definition (never cached past a name lookup) so a
    // live pose/aim/lens edit on the destination's own camera row is visible without rebinding the session face.
    private CameraSnapshot ResolveCamera(uint width, uint height) {
        var definition = m_mirror.Definition;
        var row = ResolveCameraRow(definition: definition);

        if (row is { } cameraRow) {
            var (position, orientation) = ResolveAnchorPose(
                definition: definition,
                anchor: cameraRow.Anchor
            );
            var rig = WorldCameraRigCompiler.Compile(
                definition: definition,
                program: cameraRow.Rig
            );
            var anchor = new SdfAnchor(
                Orientation: orientation,
                Position: position
            );
            var clock = new SdfCameraClock(
                PresentationSeconds: 0f,
                AuthoritativeTick: m_mirror.Tick
            );

            var (eye, target, fieldOfView) = rig.Resolve(
                anchor: in anchor,
                clock: in clock
            );

            return CameraSnapshot.LookAt(
                position: eye,
                target: target,
                fieldOfViewRadians: fieldOfView,
                viewportWidth: Math.Max(
                    val1: 1u,
                    val2: width
                ),
                viewportHeight: Math.Max(
                    val1: 1u,
                    val2: height
                )
            );
        }

        return ResolveOverviewCamera(
            definition: definition,
            height: height,
            width: width
        );
    }
    private WorldCamera? ResolveCameraRow(WorldDefinition definition) {
        if (m_effectiveCameraName is { } name) {
            foreach (var camera in definition.Cameras) {
                if (string.Equals(
                    a: camera.Name,
                    b: name,
                    comparisonType: StringComparison.Ordinal
                )) {
                    return camera;
                }
            }

            // The named camera vanished from a LATER destination mutation — fall through to the destination's
            // default projection rather than freezing on a dangling reference.
            return ((definition.Cameras.Count > 0)
                ? definition.Cameras[0]
                : null
            );
        }

        return ((definition.Cameras.Count > 0)
            ? definition.Cameras[0]
            : null
        );
    }
    // The spawn-centroid overview — the SAME construction WorldFramePresenter.ResolveSpectatorCamera uses for the
    // boot world's own no-local-seats fallback, applied here to a destination with no declared camera at all: a
    // pulled-back, elevated look-at over the centroid of its authored local-seat spawn points.
    private static CameraSnapshot ResolveOverviewCamera(WorldDefinition definition, uint width, uint height) {
        var centroid = Vector3.Zero;
        var resolved = 0;

        foreach (var name in definition.Population.SeatSpawns) {
            if (WorldDefinitionRows.FindSpawnPoint(
                spawnPoints: definition.SpawnPoints,
                id: name
            ) is { } spawn) {
                centroid += spawn.Position;
                resolved++;
            }
        }

        if (resolved > 0) {
            centroid /= resolved;
        }

        var target = (centroid + new Vector3(
            x: 0f,
            y: 1f,
            z: 0f
        ));
        var eye = (centroid + new Vector3(
            x: 0f,
            y: 14f,
            z: 18f
        ));

        return CameraSnapshot.LookAt(
            position: eye,
            target: target,
            fieldOfViewRadians: (MathF.PI / 3f),
            viewportWidth: Math.Max(
                val1: 1u,
                val2: width
            ),
            viewportHeight: Math.Max(
                val1: 1u,
                val2: height
            )
        );
    }

    /// <inheritdoc/>
    public SdfFrame Dress(SdfProgram program, DynamicTransform[] transforms, uint width, uint height, float deltaSeconds, float interpolationAlpha) {
        var programChanged = !ReferenceEquals(
            objA: program,
            objB: m_lastProgram
        );

        m_lastProgram = program;

        var (camera, offset) = ((m_windowOverride is { } window)
            ? (window.Camera, window.Offset)
            : (ResolveCamera(
                height: height,
                width: width
            ), Vector2.Zero)
        );

        return new SdfFrame(
            Program: program,
            ProgramChanged: programChanged,
            Views: [new SdfViewSnapshot(
                    Camera: camera,
                    Region: new NormalizedRect(
                        Height: 1f,
                        Width: 1f,
                        X: 0f,
                        Y: 0f
                    )
                ) { AsymmetricFrustumOffset = offset }],
            Time: 0f,
            WarpAmount: 0f
        ) {
            DynamicTransforms = transforms,
            // A budgeted 160x144-class panel image: re-marching full soft shadows/AO/far-bound/shadow-escape/
            // shadow-accumulation here costs real GPU time for a tiny screen-space result no player is closely
            // scrutinizing — the same cost posture SdfCameraView's own jumbotron rig already takes.
            DisableAmbientOcclusion = true,
            DisableSoftShadows = true,
            DisableFarBound = true,
            DisableShadowEscapeExit = true,
            DisableShadowAccumulation = true,
        };
    }
    /// <inheritdoc/>
    /// <remarks>The placement branch is unchanged (static reservation vs. static emission). The avatar branch is
    /// appended after it, in both the probe and the live arm: <see cref="WorldAvatarCatalog.Emit"/> already owns its
    /// own probe-vs-live split internally (see <see cref="EmitAvatars"/>), so this call site never branches on
    /// <see cref="SdfEmitContext.Probe"/> a second time for it.</remarks>
    public void Emit(SdfProgramBuilder builder, in SdfEmitContext context) {
        var definition = m_mirror.Definition;

        if (context.Probe) {
            WorldSessionRenderEnvelope.EmitProbe(
                builder: builder,
                candidate: definition,
                bodyColor: m_mirror.BodyColor,
                slotBase: context.SlotBase
            );

            return;
        } else {
            // A remote session mirror carries its document but no font asset origin/bytes. Its creation text stays
            // omitted until session delivery transports pinned assets and this view can share the merged glyph atlas.
            WorldPlacementStamper.EmitStatic(
                builder: builder,
                definition: definition,
                creations: definition.Creations,
                placements: definition.Placements
            );
        }

        EmitAvatars(
            builder: builder,
            probeWorstCase: false,
            slotBase: context.SlotBase
        );
    }
    /// <summary>Measures a proposed destination definition against this view's frozen render envelope.</summary>
    public (int Words, int Instances) MeasureCandidate(WorldDefinition candidate) =>
        WorldSessionRenderEnvelope.MeasureCandidate(
            candidate: candidate,
            bodyColor: m_mirror.BodyColor
        );
    /// <inheritdoc/>
    /// <remarks>Packs every mirrored-active avatar's interpolated pose into its frozen catalog leaf slots (see
    /// <see cref="WorldSessionMirror.InterpolationAlpha"/> for the timebase) plus a distance-driven gait phase; every other slot
    /// is left untouched, which the composition host already parks at <see cref="SdfEmitContext.ParkPosition"/> before
    /// any emitter's <see cref="PackDynamicTransforms"/> runs (see <c>WorldSceneEmitter</c>'s identical
    /// remark).</remarks>
    public void PackDynamicTransforms(Span<DynamicTransform> slots, in SdfEmitContext context) {
        var avatars = slots.Slice(
            start: context.SlotBase,
            length: WorldAvatarCatalog.DynamicTransformCapacity
        );
        var alpha = m_mirror.InterpolationAlpha;

        for (var index = 0; (index < WorldAvatarCatalog.Capacity); index++) {
            if (!m_mirror.IsActive(index: index)) {
                m_avatarPoseSeeded[index] = false;

                continue;
            }

            var position = Vector3.Lerp(
                value1: m_mirror.PreviousPosition(index: index),
                value2: m_mirror.CurrentPosition(index: index),
                amount: alpha
            );
            // Quaternion.Lerp is the nlerp: shortest-path dot-sign flip and renormalize — the SAME formula
            // Client.WorldClient.UpdateRenderPoses uses.
            var orientation = Quaternion.Lerp(
                quaternion1: m_mirror.PreviousOrientation(index: index),
                quaternion2: m_mirror.CurrentOrientation(index: index),
                amount: alpha
            );

            var address = m_mirror.Address(index: index);

            if (
                m_avatarPoseSeeded[index] &&
                (m_avatarMotionAddresses[index] == address)
            ) {
                var travelled = MathF.Min(
                    x: Vector3.Distance(
                        value1: position,
                        value2: m_avatarPreviousPositions[index]
                    ),
                    y: 0.25f
                );

                m_avatarGaitPhases[index] += (travelled * 8.0f);
            } else {
                m_avatarPoseSeeded[index] = true;
                m_avatarGaitPhases[index] = 0f;
                m_avatarMotionAddresses[index] = address;
            }

            m_avatarPreviousPositions[index] = position;

            WorldAvatarCatalog.PackTransforms(
                avatar: index,
                rootPosition: position,
                rootOrientation: orientation,
                gaitPhase: (m_avatarGaitPhases[index] * m_emittedGaitAmplitudes[index]),
                // A session view disables soft shadows entirely (see Dress below), so crowd-radius participation has
                // no observer — false is exact, not an approximation.
                castsSoftShadow: false,
                transforms: avatars,
                rig: m_emittedRigs[index],
                scale: m_emittedScales[index]
            );
        }
    }
    /// <summary>Sets (or clears) this frame's window camera override — the off-axis frustum
    /// <c>WorldWindowFrustumFit.TryFitWindow</c> fit against the border pair's two face rows and the local
    /// viewer's eye. Called once per produced frame by <c>WorldScreenBinder.RenderViews</c>, immediately
    /// before this session's <see cref="Puck.SdfVm.Views.WorldSessionView.Resolve"/>; <see langword="null"/> (no
    /// eye/aperture available yet, or the fit refused — see <c>SdfAsymmetricFrustum.TryFit</c>) falls back to
    /// <see cref="ResolveCamera"/>'s ordinary named/default projection for that one frame.</summary>
    /// <param name="camera">The fitted camera apexed at the mapped eye, or <see langword="null"/> to use the
    /// ordinary projection.</param>
    /// <param name="offset">The fitted frustum's tangent-space center offset — ignored when <paramref name="camera"/>
    /// is <see langword="null"/>.</param>
    public void SetWindowCamera(CameraSnapshot? camera, Vector2 offset) {
        m_windowOverride = ((camera is { } resolved)
            ? (resolved, offset)
            : null
        );
    }
    /// <summary>Writes two components, never their sum: the definition-delivery revision, and the mirrored
    /// snapshot's declared-set/palette revision (<see cref="WorldSessionMirror.SnapshotRevision"/>, assigned from the
    /// wire and able to move down) — the same non-summing rule <see cref="WorldClient.WriteRevision"/> documents for
    /// the identical reason: a rebuild must never be maskable by two counters moving in opposite directions.</summary>
    public void WriteRevision(Span<int> destination) {
        destination[0] = m_mirror.DefinitionRevision;
        destination[1] = m_mirror.SnapshotRevision;
    }

    /// <summary>The frozen transform-slot count this emitter declares: the all-128-rig avatar catalog's leaf capacity
    /// — the same frozen worst case <c>WorldSceneEmitter.DynamicSlotCount</c> reserves for its own
    /// avatar range, sized off the destination's population capacity (<see cref="WorldAvatarCatalog.Capacity"/> and
    /// <see cref="WorldPopulationLimits.CapacityCeiling"/> are single-sourced today), so a full destination can never
    /// outgrow this emitter's own probe.</summary>
    public int DynamicSlotCount => WorldAvatarCatalog.DynamicTransformCapacity;
    /// <inheritdoc/>
    public int RevisionComponentCount => 2;
}
