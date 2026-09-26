using System.Numerics;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Protocol;

namespace Puck.World.Client;

public sealed partial class WorldStampPool {
    private readonly WorldTransformOwners m_owners = new(capacity: WorldPlacementPolicy.MaxStampRegistrations);
    // The registration each pool entry was last packed for: a swapped registration repacks and parks its unused slots.
    private readonly Registration?[] m_packedRegistrations = new Registration?[WorldPlacementPolicy.MaxStampRegistrations];
    // A registration's slots as they stood before its repack, which the moved set compares the repack against.
    private readonly DynamicTransform[] m_previousSlots = new DynamicTransform[SlotsPerPlacement];

    /// <summary>Packs the pool's moved transforms: each live registration's root rides its placement pose (animated),
    /// the client's interpolated body pose (body-rooted), or that pose composed with the attach facet's local offset
    /// (attached), and each shape holds its current frame's snapshot (composed root ∘ per-shape pose, positions scaled by
    /// the registration scale); unused slots — and an attached row whose target body is not live this frame — hide below
    /// the floor.
    /// <para>
    /// A registration repacks only while restless: its root pose, pose epoch, body facts or replay cursor moved, a
    /// state mirror slot its reads hold (lanes, drivers, poses, effectors) changed, or its last repack still changed
    /// its slots. Every live registration's volumes are appended each frame.
    /// </para></summary>
    /// <param name="transforms">The unified dynamic-transform table (the pool writes its own slot range).</param>
    /// <param name="client">The client whose interpolated body poses root the body-rooted and attached stamps.</param>
    /// <param name="moved">The frame's moved set, which every repacked or vacated registration reports to.</param>
    /// <param name="slotBase">The pool's first dynamic-transform slot in <paramref name="transforms"/>, supplied by the
    /// emitter that owns the pool (see <see cref="Emit"/>).</param>
    /// <param name="parkPosition">Where an unused slot — or an attached row whose target body is not live this
    /// frame — parks, hidden below the floor (<see cref="SdfEmitContext.ParkPosition"/>).</param>
    public void PackTransforms(Span<DynamicTransform> transforms, WorldClient client, SdfMovedTransforms moved, int slotBase, Vector3 parkPosition) {
        ArgumentNullException.ThrowIfNull(argument: client);
        ArgumentNullException.ThrowIfNull(argument: moved);

        m_packedSlotBase = slotBase;
        m_volumes.Clear();

        var deltaSeconds = m_pendingDeltaSeconds;

        m_pendingDeltaSeconds = 0f;

        for (var index = 0; (index < m_pool.Length); index++) {
            var rootSlot = (slotBase + (index * SlotsPerPlacement));
            var live = m_pool[index];

            // An attached row whose body is not active contributes nothing this frame — the presentation mirror of
            // WorldPlacementAttachment.TryResolve's inactive-body verdict (which world.attachments echoes by reason).
            // The registration keeps its slot: occupancy changes tick to tick and a rebuild is not owed for one. The
            // range test is a belt-and-braces guard, not a live gap: WorldBodiesLimits.CapacityCeiling is
            // WorldClient.EntityCapacity, so the document validator's bound on population.capacity already keeps
            // every body index inside this client's view.
            if (
                (live is { Row.Attach: { } parked }) &&
                ((((uint)parked.BodyIndex) >= ((uint)WorldClient.EntityCapacity)) || !client.IsActive(index: parked.BodyIndex))
            ) {
                live = null;
            }

            if (live is null) {
                m_packedRegistrations[index] = null;

                if (m_owners.Vacate(
                    moved: moved,
                    owner: index
                )) {
                    WorldTransformOwners.Park(
                        count: SlotsPerPlacement,
                        moved: moved,
                        parkPosition: parkPosition,
                        start: rootSlot,
                        table: transforms
                    );
                }

                continue;
            }

            var (rootPosition, rootRotation, placementScale) = RootPose(
                client: client,
                live: live
            );
            var document = live.Creation.EngineDocument;
            var shapeCount = Math.Min(
                val1: (document.Shapes?.Count ?? 0),
                val2: WorldPlacementPolicy.MaxAnimatedStampShapes
            );

            if (
                !moved.Everything &&
                !ReferenceEquals(
                objA: m_packedRegistrations[index],
                objB: live
            )
            ) {
                WorldTransformOwners.Park(
                    count: ((SlotsPerPlacement - 1) - shapeCount),
                    moved: moved,
                    parkPosition: parkPosition,
                    start: ((rootSlot + 1) + shapeCount),
                    table: transforms
                );
            }

            if (Wake(
                client: client,
                index: index,
                live: live,
                moved: moved,
                rootPosition: rootPosition,
                rootRotation: rootRotation
            )) {
                var previous = m_previousSlots.AsSpan(
                    length: (1 + shapeCount),
                    start: 0
                );

                transforms.Slice(
                    length: previous.Length,
                    start: rootSlot
                ).CopyTo(destination: previous);
                PackRegistration(
                    client: client,
                    deltaSeconds: deltaSeconds,
                    live: live,
                    parkPosition: parkPosition,
                    placementScale: placementScale,
                    rootPosition: rootPosition,
                    rootRotation: rootRotation,
                    rootSlot: rootSlot,
                    shapeCount: shapeCount,
                    transforms: transforms
                );
                m_owners.Settle(
                    deltaSeconds: deltaSeconds,
                    moved: moved.Commit(
                    previous: previous,
                    slots: transforms,
                    start: rootSlot
                ),
                    owner: index
                );
            }

            AppendVolumes(
                document: document,
                placementScale: placementScale,
                rootSlot: rootSlot,
                shapeCount: shapeCount
            );
        }
    }

    // Decides whether a live registration repacks this frame; one new to its pool entry always does.
    private bool Wake(WorldClient client, int index, Registration live, SdfMovedTransforms moved, Vector3 rootPosition, Quaternion rootRotation) {
        var swapped = !ReferenceEquals(
            objA: m_packedRegistrations[index],
            objB: live
        );
        var discontinuity = swapped;
        var facts = 0;

        if (live.BodyIndex is { } watched) {
            discontinuity |= (
                (live.RootEpoch != client.PoseEpoch(index: watched)) ||
                (live.RootAddress != client.EntityAddress(index: watched))
            );
            facts = ((int)client.Facts(index: watched));
        }

        m_packedRegistrations[index] = live;
        live.Reads.Bind(
            bodyIndex: (live.BodyIndex ?? -1),
            mirror: client.StateMirror
        );
        live.Reads.Arrive(
            first: live.Creation,
            second: live.Look
        );

        return m_owners.Wake(
            castsSoftShadow: false,
            discontinuity: discontinuity,
            moved: moved,
            orientation: rootRotation,
            owner: index,
            position: rootPosition,
            version: HashCode.Combine(
            value1: live.EffectiveCursor,
            value2: facts,
            value3: live.Reads.Changed
        )
        );
    }
    // Packs one live registration's root and shape slots, stepping its followers, drivers and effectors by
    // deltaSeconds.
    private void PackRegistration(Span<DynamicTransform> transforms, WorldClient client, Registration live, int rootSlot, int shapeCount, float deltaSeconds, Vector3 rootPosition, Quaternion rootRotation, float placementScale, Vector3 parkPosition) {
        // The pose-continuity watch is read for every body-rooted registration, not only a follower-bearing one: a
        // teleport or a reused body slot invalidates a latched contact point the same way it invalidates a follower —
        // the world point a foot was planted at belongs to where the body was.
        if (live.BodyIndex is { } watched) {
            var epoch = client.PoseEpoch(index: watched);
            var address = client.EntityAddress(index: watched);

            if ((live.RootEpoch != epoch) || (live.RootAddress != address)) {
                live.RootPositionFollower.Reseed();
                live.RootOrientationFollower.Reseed();
                live.RootEpoch = epoch;
                live.RootAddress = address;

                for (var shapeSlot = 0; (shapeSlot < WorldPlacementPolicy.MaxAnimatedStampShapes); shapeSlot++) {
                    live.PartFollower[shapeSlot].Reseed();
                }

                Array.Clear(array: live.EffectorPlanted);
            }
        }

        if (live.HasRootDynamics) {
            StepRootFollower(
                deltaSeconds: deltaSeconds,
                live: live,
                targetPosition: rootPosition,
                targetRotation: rootRotation
            );
            (rootPosition, rootRotation) = (live.FollowedPosition, live.FollowedOrientation);
        } else {
            live.RootPositionFollower.Reseed();
            live.RootOrientationFollower.Reseed();
            live.FollowedPosition = rootPosition;
            live.FollowedOrientation = rootRotation;
        }

        // The look's anonymous render lanes (WorldLookMotion.Lanes), evaluated against live state and carried on every
        // dynamic slot this registration owns — root and every shape — so whichever slot a shape's own erode/wear reads
        // (SDF_OP_LANE_ERODE's TransformDynamic, shade-wear.hlsli) sees the current value regardless of which slot it
        // rides.
        var lanes = WorldLookLaneEvaluator.EvaluateLanes(
            expressions: live.Lanes,
            reads: live.Reads
        );

        transforms[rootSlot] = new DynamicTransform(
            Lanes: lanes,
            Orientation: rootRotation,
            Position: rootPosition
        );

        var document = live.Creation.EngineDocument;
        var drivers = document.Drivers;

        live.PoseFrame = SelectPose(live: live);

        WorldGaitDrivers.Advance(
            address: ((live.BodyIndex is { } drivenBody) ? client.EntityAddress(index: drivenBody) : new WorldEntityAddress(Authority: string.Empty, Generation: 0, Index: -1)),
            deltaSeconds: deltaSeconds,
            drivers: drivers,
            facts: ((live.BodyIndex is { } factBody) ? client.Facts(index: factBody) : default),
            easedSpeed: ref live.DriverSpeed,
            lastAddress: ref live.DriverAddress,
            lastOrientation: ref live.DriverOrientation,
            lastPosition: ref live.DriverPosition,
            orientation: rootRotation,
            phases: live.DriverPhase,
            position: rootPosition,
            seeded: ref live.DriverSeeded,
            weights: live.DriverWeight,
            reads: live.Reads
        );

        var shapes = (document.Shapes ?? []);
        var poses = FramePoses(
            frameCursor: live.EffectiveCursor,
            live: live
        );

        // The animated facets compose in the creation's own space, on top of whichever rest/frame pose the write pass
        // chooses — a uniform placement scale commutes with a rotation about a scaled pivot, so scaling there is the
        // same pose either way. A shape's own delta chains under its parent's (already composed: a parent is validated
        // to precede its children), and is kept for the children that follow.
        if (!live.PartParentsResolved) {
            ResolvePartParents(
                live: live,
                shapes: shapes
            );
        }

        for (var shapeIndex = 0; (shapeIndex < shapeCount); shapeIndex++) {
            WorldGaitDrivers.ComposeDelta(
                drivers: drivers,
                phases: live.DriverPhase,
                rotation: out var ownRotation,
                shape: shapes[shapeIndex],
                translation: out var ownTranslation,
                weights: live.DriverWeight,
                definition: client.Definition
            );

            live.PartOwnRotation[shapeIndex] = ownRotation;
            live.PartOwnTranslation[shapeIndex] = ownTranslation;
        }

        ChainPartDeltas(
            live: live,
            shapeCount: shapeCount
        );
        // The effectors correct the driver-posed chain, then everything downstream of a corrected bone re-chains off
        // the corrected own delta — so a hand parented to a solved forearm rides the solve with no effector of its own.
        if (ApplyEffectors(
            client: client,
            deltaSeconds: deltaSeconds,
            document: document,
            live: live,
            placementScale: placementScale,
            poses: poses,
            rootPosition: rootPosition,
            rootRotation: rootRotation,
            shapeCount: shapeCount,
            shapes: shapes
        )) {
            ChainPartDeltas(
                live: live,
                shapeCount: shapeCount
            );
        }

        for (var shapeIndex = 0; (shapeIndex < shapeCount); shapeIndex++) {
            var slot = ((rootSlot + 1) + shapeIndex);
            var shape = shapes[shapeIndex];

            // A domain-bearing shape's own slot carries the rigid delta its parent chain imparts to creation space
            // (identity when it has no parent) rather than a composed pose — EmitShape rides this slot for its
            // TransformDynamic, applies its domain ops against it, and bakes its own rest-pose local translate/rotate
            // afterward. The canonicalizer refuses an own swing/slide or a named frame pose on a domain-bearing shape,
            // so PartOwnRotation/Translation is always identity/zero here and PartDeltaRotation/Translation[shapeIndex]
            // is exactly the parent's chained delta (ChainPartDeltas). The delta's translation is in creation units,
            // like every other shape's, so it takes the placement scale here — the same product the ordinary path
            // folds into `position * placementScale` below.
            if (shape.Domain is { Count: > 0 }) {
                transforms[slot] = new DynamicTransform(
                    Lanes: lanes,
                    Orientation: Quaternion.Normalize(value: (rootRotation * live.PartDeltaRotation[shapeIndex])),
                    Position: (rootPosition + Vector3.Transform(
                        rotation: rootRotation,
                        value: (live.PartDeltaTranslation[shapeIndex] * placementScale)
                    ))
                );

                continue;
            }

            var (position, rotation) = BasePose(
                poses: poses,
                shape: shape
            );

            WorldGaitDrivers.Apply(
                deltaRotation: live.PartDeltaRotation[shapeIndex],
                deltaTranslation: live.PartDeltaTranslation[shapeIndex],
                position: ref position,
                rotation: ref rotation
            );

            var worldPosition = (rootPosition + Vector3.Transform(
                rotation: rootRotation,
                value: (position * placementScale)
            ));

            if (live.PartFollows[shapeIndex]) {
                worldPosition = StepPartFollower(
                    deltaSeconds: deltaSeconds,
                    live: live,
                    shapeSlot: shapeIndex,
                    target: worldPosition
                );
            }

            transforms[slot] = new DynamicTransform(
                Lanes: lanes,
                Orientation: Quaternion.Normalize(value: (rootRotation * rotation)),
                Position: worldPosition
            );
        }
    }
}
