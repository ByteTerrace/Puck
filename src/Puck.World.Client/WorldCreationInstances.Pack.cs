using System.Numerics;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Protocol;

namespace Puck.World.Client;

public sealed partial class WorldCreationInstances {
    private readonly WorldTransformOwners m_owners = new(capacity: 0);
    // The registration each pool entry was last packed for: a swapped registration repacks and parks its unused slots.
    private Registration?[] m_packedRegistrations = [];
    // Each pool entry's mesh draw as the last pack posed it (null for none), and the one list they compose, rewritten
    // in place under a new revision only in a pack that changed an entry: a pool at rest hands the engine what it already
    // packed, and a moving one allocates nothing once the list has grown to its stamps.
    private SdfMeshDraw?[] m_meshDraws = [];
    private readonly List<SdfMeshDraw> m_meshDrawList = new(capacity: 0);
    private WorldPickTarget?[] m_pickMeshTargets = [];

    /// <summary>Gets the immutable mesh identities in draw order, replaced only when membership changes.</summary>
    public IReadOnlyList<WorldPickTarget?> PickMeshTargets => m_pickMeshTargets;

    private bool m_meshDrawsChanged;
    private long m_meshDrawsRevision;

    /// <summary>Gets the mesh draws the last <see cref="PackTransforms"/> posed: each live registration whose creation
    /// carries a mesh (<see cref="WorldPrototype.Mesh"/>), at its packed root and scale, in pool order: always the same
    /// list, rewritten in place when a draw moved, which <see cref="MeshDrawsRevision"/> then counts.</summary>
    public IReadOnlyList<SdfMeshDraw> MeshDraws => m_meshDrawList;
    /// <summary>Gets the revision of <see cref="MeshDraws"/>' content: one more for each pack that rewrote it.</summary>
    public long MeshDrawsRevision => m_meshDrawsRevision;

    /// <summary>Gets the slot runs of the body-rooted registrations the last <see cref="PackTransforms"/> found
    /// straddling a portal whose counterpart stands in the same world (<see cref="WorldPortalStraddle"/>): the same list
    /// for as long as the runs are unchanged, a new one when they change, the one empty list when there are none.</summary>
    public IReadOnlyList<SdfSlotClip> SlotClips { get; private set; } = [];

    private readonly List<SdfSlotClip> m_slotClipRun = [];
    // Each packed run's body, beside m_slotClipRun, for the line a changed run list logs.
    private readonly List<int> m_slotClipBodies = [];
    // The spheres holding the registration being tested against the doors, one a shape (PartSpheres).
    private Vector4[] m_partSpheres = [];

    // Whether this pack found exactly the runs the held list names, in order.
    private static bool SameRuns(IReadOnlyList<SdfSlotClip> held, List<SdfSlotClip> packed) {
        if (held.Count != packed.Count) {
            return false;
        }

        for (var index = 0; (index < packed.Count); index++) {
            if (held[index] != packed[index]) {
                return false;
            }
        }

        return true;
    }
    // Widens a body's MotionReach to the farthest part sphere this full pack posed from its root: driver swings and
    // slides, parent chains, effectors and followers can carry a part past the creation's rest reach, and the bodies
    // lever must not judge a body unseen by a bound its posed parts exceed.
    private void WidenMotionReach(Registration live, ReadOnlySpan<DynamicTransform> transforms, int rootSlot, int shapeCount, float placementScale, Vector3 rootPosition) {
        if (placementScale <= 0f) {
            return;
        }

        var count = PartSpheres(live: live, placementScale: placementScale, rootSlot: rootSlot, shapeCount: shapeCount, transforms: transforms);

        for (var index = 0; (index < count); index++) {
            var sphere = m_partSpheres[index];
            var reach = ((Vector3.Distance(value1: new Vector3(x: sphere.X, y: sphere.Y, z: sphere.Z), value2: rootPosition) + sphere.W) / placementScale);

            live.MotionReach = MathF.Max(x: live.MotionReach, y: reach);
        }
    }
    // A registration's creation's reach from its root, in creation units, measured once.
    private static float ReachOf(Registration live) {
        if (live.Reach < 0f) {
            live.Reach = CreationGeometry.Reach(document: live.Creation.EngineDocument);
        }

        return live.Reach;
    }
    // Each shape's reach about its own slot, in creation units, or -1 for a cutter, measured once (PartSpheres says why).
    private static float[] ShapeReachesOf(Registration live) {
        if (live.ShapeReaches is { } known) {
            return known;
        }

        var shapes = (live.Creation.EngineDocument.Shapes ?? []);
        var reaches = new float[shapes.Count];

        for (var index = 0; (index < shapes.Count); index++) {
            var shape = shapes[index];

            reaches[index] = (((shape.Blend is { } blend) && (SdfBoundAlgebra.IsIntersection(blend: blend) || SdfBoundAlgebra.IsSubtraction(blend: blend)))
                ? -1f
                : (CreationStampEmitter.ShapeReach(shape: shape) - ((shape.Domain is { Count: > 0 }) ? 0f : shape.Position.Length())));
        }

        live.ShapeReaches = reaches;

        return reaches;
    }
    // The spheres holding a registration's geometry as posed this frame, one a shape: its slot and its reach about that
    // slot, in metres. A domain-bearing shape's slot holds only its parent chain's delta and its rest offset is baked
    // after it, so its whole reach counts. An intersecting or subtracting shape only removes (SdfBoundAlgebra.Compose),
    // and a cutter is often far larger than what it trims, so it holds nothing. The creation's rest bound (ReachOf) is
    // one sphere about the root charging every offset and cutter, metres wide on a rigged body: a door straddle tested
    // against it clips a body standing clear of the glass out of sight.
    private int PartSpheres(Registration live, ReadOnlySpan<DynamicTransform> transforms, int rootSlot, int shapeCount, float placementScale) {
        var reaches = ShapeReachesOf(live: live);
        var count = 0;

        for (var index = 0; (index < shapeCount); index++) {
            if (reaches[index] >= 0f) {
                m_partSpheres[count++] = new Vector4(value: live.ShapeTransforms[index].Position, w: (reaches[index] * placementScale));
            }
        }

        return count;
    }

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
    public void PackTransforms(Span<DynamicTransform> transforms, IWorldStampSource client, SdfMovedTransforms moved, int slotBase, Vector3 parkPosition) {
        ArgumentNullException.ThrowIfNull(argument: client);
        ArgumentNullException.ThrowIfNull(argument: moved);

        m_packedSlotBase = slotBase;
        m_volumes.Clear();
        m_slotClipRun.Clear();
        m_slotClipBodies.Clear();
        m_packCount++;
        BeginRateCodes(slotBase: slotBase);

        var doors = WorldPortalStraddle.Doors(definition: client.Definition);

        var deltaSeconds = m_pendingDeltaSeconds;

        m_rateFrameSeconds = deltaSeconds;

        m_pendingDeltaSeconds = 0f;

        for (var index = 0; (index < m_pool.Length); index++) {
            var rootSlot = (slotBase + RootSlotOffset(index: index));
            var slots = SlotsAt(index: index);
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
                SetMeshDraw(
                    draw: null,
                    index: index
                );

                if (m_owners.Vacate(
                    moved: moved,
                    owner: index
                )) {
                    WorldTransformOwners.Park(
                        count: slots,
                        moved: moved,
                        parkPosition: parkPosition,
                        start: rootSlot,
                        table: transforms
                    );
                }

                continue;
            }

            WorldCreationWork.ProcessWork.Count(kind: WorldCreationWork.Visited);
            var (rootPosition, rootRotation, placementScale) = RootPose(
                client: client,
                live: live
            );
            var document = live.Creation.EngineDocument;
            var shapeCount = (document.Shapes?.Count ?? 0);

            if (m_partSpheres.Length < shapeCount) { Array.Resize(array: ref m_partSpheres, newSize: shapeCount); }
            // The root, the shapes, and the slot text baked apart rides (TextApart).
            var packedSlots = live.Slots;

            if (
                !moved.Everything &&
                !ReferenceEquals(
                objA: m_packedRegistrations[index],
                objB: live
            )
            ) {
                WorldTransformOwners.Park(
                    count: (slots - packedSlots),
                    moved: moved,
                    parkPosition: parkPosition,
                    start: (rootSlot + packedSlots),
                    table: transforms
                );
            }

            var packedBefore = ReferenceEquals(objA: m_packedRegistrations[index], objB: live);
            var posed = false;

            if (Wake(
                client: client,
                deltaSeconds: deltaSeconds,
                index: index,
                live: live,
                moved: moved,
                rootPosition: rootPosition,
                rootRotation: rootRotation
            ) || live.RateOwed) {
                // A pose holds between its rated packs, except on the frame its slots change owner or the whole table is
                // owed (WorldCreationInstances.Rates, whose one decision gives a placed creation only the held tier).
                var mayHold = (
                    packedBefore &&
                    (document.SizeBy is null) &&
                    !moved.Everything &&
                    !m_owners.Reseats(owner: index) &&
                    // A held pose carries its slots rigidly, so a registration whose scale changed packs in full.
                    (transforms[rootSlot].Scale == placementScale)
                );

                if (!mayHold) {
                    ForgetPhase(live: live);
                }

                var holds = (mayHold && HoldsPose(center: rootPosition, deltaSeconds: deltaSeconds, live: live, placementScale: placementScale, radius: RateReach(live: live, placementScale: placementScale)));
                // A held pose whose root has not moved moves no slot, so nothing is carried or compared.
                var (heldPosition, heldRotation) = (rootPosition, rootRotation);
                var still = false;
                // Whether this pack changed a slot: every write goes through SdfMovedTransforms.Write, which compares as
                // it writes, so the pack keeps no copy of the slots it rewrites.
                var slotsMoved = false;

                if (holds) {
                    (heldPosition, heldRotation) = FollowHeldRoot(deltaSeconds: deltaSeconds, live: live, rootPosition: rootPosition, rootRotation: rootRotation);
                    still = HeldRootStill(root: in transforms[rootSlot], rootPosition: heldPosition, rootRotation: heldRotation);
                }

                if (holds) {
                    if (!still) {
                        slotsMoved = CarryPose(
                            live: live,
                            packedSlots: packedSlots,
                            rootPosition: heldPosition,
                            rootRotation: heldRotation,
                            rootSlot: rootSlot,
                            transforms: transforms
                        );
                    }
                    live.RateSeconds += deltaSeconds;
                    live.RateOwed = true;
                    m_rateCounts?.Count(kind: ((live.BodyIndex is not null) ? SdfUpdateRateCounts.BodiesHeld : SdfUpdateRateCounts.PlacementsHeld));
                    WorldCreationWork.ProcessWork.Count(kind: WorldCreationWork.RateHeld);
                    WorldCreationWork.ProcessWork.Add(amount: packedSlots, kind: WorldCreationWork.SlotsRetained);
                } else {
                    // A pose held while nothing could show it resumes as a continuity cut: its followers, plants and
                    // drivers relatch where the body is now, as after a teleport, rather than advancing over every
                    // second it held.
                    var unseen = (live.RateSlowest == SdfUpdateRateTiers.HeldHertz);

                    if (unseen) {
                        live.RootEpoch = -1;
                    }

                    WorldCreationWork.ProcessWork.Count(kind: WorldCreationWork.Posed);
                    WorldCreationWork.ProcessWork.Add(amount: packedSlots, kind: WorldCreationWork.SlotsPosed);
                    var elapsedSeconds = (live.RateSeconds + deltaSeconds);
                    slotsMoved = PackRegistration(
                        client: client,
                        deltaSeconds: (unseen ? deltaSeconds : (live.RateSeconds + deltaSeconds)),
                        live: live,
                        parkPosition: parkPosition,
                        placementScale: placementScale,
                        rootDeltaSeconds: deltaSeconds,
                        rootPosition: rootPosition,
                        rootRotation: rootRotation,
                        rootSlot: rootSlot,
                        shapeCount: shapeCount,
                        transforms: transforms
                    );
                    RecordPose(deltaSeconds: deltaSeconds, live: live);
                    WidenMotionReach(live: live, placementScale: placementScale, rootPosition: transforms[rootSlot].Position, shapeCount: shapeCount, transforms: transforms, rootSlot: rootSlot);
                    // A pack that may not hold (a new owner, a reseat, a scale change, the whole table owed) or that
                    // resumes an unseen hold is a cut the part speed is not measured across.
                    MeasurePartSpeed(continuous: (mayHold && !unseen), elapsedSeconds: elapsedSeconds, live: live, root: in transforms[rootSlot], shapeCount: shapeCount);
                    m_rateCounts?.Count(kind: ((live.BodyIndex is not null) ? SdfUpdateRateCounts.BodiesPosed : SdfUpdateRateCounts.PlacementsPosed));
                    posed = true;
                    live.RateSeconds = 0f;
                    live.RateOwed = false;
                }
                m_owners.Settle(
                    deltaSeconds: deltaSeconds,
                    moved: (!still && moved.Commit(
                    count: packedSlots,
                    moved: slotsMoved,
                    reseat: m_owners.Reseats(owner: index),
                    start: rootSlot
                )),
                    owner: index
                );
            } else {
                WorldCreationWork.ProcessWork.Count(kind: WorldCreationWork.Retained);
                WorldCreationWork.ProcessWork.Add(amount: packedSlots, kind: WorldCreationWork.SlotsRetained);
            }

            NoteRate(center: rootPosition, live: live, packedSlots: packedSlots, placementScale: placementScale, posed: posed, radius: RateReach(live: live, placementScale: placementScale), rootSlot: rootSlot);
            live.PackedRoot = transforms[rootSlot];
            placementScale = transforms[rootSlot].Scale;
            AppendVolumes(
                document: document,
                hidden: live.Hidden,
                live: live,
                placementScale: placementScale,
                rootSlot: rootSlot,
                shapeCount: shapeCount
            );

            // A body straddling a portal whose counterpart stands in this world is drawn on both sides of it. The rest
            // bound screens out the bodies nowhere near a door; the posed parts decide.
            if (
                (live.BodyIndex is { } straddler) &&
                (doors.Count > 0) &&
                (WorldPortalStraddle.Clip(
                    center: transforms[rootSlot].Position,
                    doors: doors,
                    firstSlot: rootSlot,
                    radius: (ReachOf(live: live) * placementScale),
                    slotCount: slots
                ) is not null) &&
                (WorldPortalStraddle.Clip(
                    doors: doors,
                    firstSlot: rootSlot,
                    slotCount: slots,
                    spheres: m_partSpheres.AsSpan(start: 0, length: PartSpheres(live: live, placementScale: placementScale, rootSlot: rootSlot, shapeCount: shapeCount, transforms: transforms))
                ) is { } clip)
            ) {
                m_slotClipRun.Add(item: clip);
                m_slotClipBodies.Add(item: straddler);
            }

            // The root slot holds the pose the shapes were packed under, followers included, whether or not this frame
            // repacked it.
            SetMeshDraw(
                draw: ((live.Mesh is { } mesh)
                    ? WorldPlacementStamper.MeshDrawOf(
                        identity: live,
                        material: live.MeshMaterial,
                        mesh: mesh,
                        origin: transforms[rootSlot].Position,
                        rotation: transforms[rootSlot].Orientation,
                        scale: placementScale
                    ) with { IsDynamic = true }
                    : null),
                index: index
            );
        }

        Rigid.Pack(transforms, client, (slotBase + ArticulatedSlotCount), deltaSeconds);
        WorldCreationWork.ProcessWork.Count(kind: WorldCreationWork.Packs);
        EndRateCodes();

        // The list is replaced only when a run changed: the mesh dressing redraws and reuploads every draw record when it
        // sees a new list, so a body holding still across a door keeps the one it was first clipped by.
        if (!SameRuns(held: SlotClips, packed: m_slotClipRun)) {
            // A body drawn clipped at a door shows only past the glass, so every change is logged: a body that vanishes
            // away from any door is a straddle test that should not have matched.
            WorldClientDiagnostics.Report(channel: "world.straddle", text: ((m_slotClipBodies.Count == 0)
                ? "none"
                : $"{string.Join(separator: ' ', values: m_slotClipRun.Select(selector: (clip, index) => $"body:{m_slotClipBodies[index]} plane=({clip.Plane.X:0.##},{clip.Plane.Y:0.##},{clip.Plane.Z:0.##},{clip.Plane.W:0.##})"))} clipped"));
            SlotClips = ((m_slotClipRun.Count == 0)
                ? []
                : [.. m_slotClipRun]
            );
        }

        if (m_meshDrawsChanged) {
            m_meshDrawList.Clear();

            var identitiesChanged = false;
            var drawIndex = 0;

            for (var poolIndex = 0; (poolIndex < m_meshDraws.Length); poolIndex++) {
                if (m_meshDraws[poolIndex] is { } posed) {
                    m_meshDrawList.Add(item: posed);
                    identitiesChanged |= ((drawIndex >= m_pickMeshTargets.Length) ||
                        !ReferenceEquals(objA: m_pickMeshTargets[drawIndex], objB: m_pool[poolIndex]?.PickTarget));
                    drawIndex++;
                }
            }
            if (identitiesChanged || (drawIndex != m_pickMeshTargets.Length)) {
                m_pickMeshTargets = new WorldPickTarget?[drawIndex];
                drawIndex = 0;
                for (var poolIndex = 0; (poolIndex < m_meshDraws.Length); poolIndex++) {
                    if (m_meshDraws[poolIndex] is not null) {
                        m_pickMeshTargets[drawIndex++] = m_pool[poolIndex]?.PickTarget;
                    }
                }
            }

            m_meshDrawsRevision++;
            m_meshDrawsChanged = false;
        }
    }

    // Records one pool entry's draw, noting whether it changed.
    private void SetMeshDraw(int index, SdfMeshDraw? draw) {
        if (m_meshDraws[index] != draw) {
            m_meshDraws[index] = draw;
            m_meshDrawsChanged = true;
        }
    }
    // Decides whether a live registration repacks this frame; one new to its pool entry always does.
    private bool Wake(IWorldStampSource client, int index, Registration live, SdfMovedTransforms moved, Vector3 rootPosition, Quaternion rootRotation, float deltaSeconds) {
        var swapped = !ReferenceEquals(
            objA: m_packedRegistrations[index],
            objB: live
        );
        var discontinuity = swapped;
        var facts = 0;

        if (live.BodyIndex is { } watched) {
            // An in-place crossing carries the body continuously, but its slots jump through the portal pair: the frame
            // that carries them reseats their history, so reprojection never reads the far side as motion.
            discontinuity |= (
                (live.RootEpoch != client.PoseEpoch(index: watched)) ||
                (live.RootAddress != client.EntityAddress(index: watched)) ||
                (live.RootCarryEpoch != client.Carries.Epoch(index: watched))
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

        var wake = m_owners.Wake(
            deltaSeconds: deltaSeconds,
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

        // Owner output equality cannot cancel an authored clock or a follower's remaining motion.
        return (wake || ((deltaSeconds > 0f) && live.DriverActivity.Continues));
    }
    // Carries everything a body-rooted registration latched in world space through an in-place crossing's rigid
    // motion: the root and part followers, the drivers' last pose and eased world velocity, and every effector's
    // planted, stepping and resolved points and normals. Body-local state (driver phases, weights, own deltas) is
    // unchanged by it.
    private static void Carry(Registration live, WorldPoseCarry carry) {
        live.RootPositionFollower.Carry(
            rotation: carry.Rotation,
            translation: carry.Translation
        );
        live.RootOrientationFollower.Carry(rotation: carry.Rotation);
        live.FollowedPosition = carry.Point(point: live.FollowedPosition);
        live.FollowedOrientation = carry.Attitude(attitude: live.FollowedOrientation);
        live.DriverPosition = carry.Point(point: live.DriverPosition);
        live.DriverOrientation = carry.Attitude(attitude: live.DriverOrientation);
        live.Kinematics.Carry(carry: carry);

        for (var shapeSlot = 0; (shapeSlot < live.PartFollower.Length); shapeSlot++) {
            live.PartFollower[shapeSlot].Carry(
                rotation: carry.Rotation,
                translation: carry.Translation
            );
        }

        for (var effector = 0; (effector < live.EffectorPlantTarget.Length); effector++) {
            live.EffectorPlantTarget[effector] = carry.Point(point: live.EffectorPlantTarget[effector]);
            live.EffectorPlantNormal[effector] = carry.Direction(direction: live.EffectorPlantNormal[effector]);
            live.EffectorTarget[effector] = carry.Point(point: live.EffectorTarget[effector]);
            live.EffectorNormal[effector] = carry.Direction(direction: live.EffectorNormal[effector]);
            live.EffectorStepFrom[effector] = carry.Point(point: live.EffectorStepFrom[effector]);
            live.EffectorStepTo[effector] = carry.Point(point: live.EffectorStepTo[effector]);
            live.EffectorStepToNormal[effector] = carry.Direction(direction: live.EffectorStepToNormal[effector]);
        }
    }
    // Packs one live registration's root and shape slots, stepping its root follower by rootDeltaSeconds, this frame's,
    // and its drivers, effectors and part followers by deltaSeconds, which spans every frame since the registration's
    // last full pack (a held pose's frames included, WorldCreationInstances.Rates). Returns whether it changed a slot.
    private bool PackRegistration(Span<DynamicTransform> transforms, IWorldStampSource client, Registration live, int rootSlot, int shapeCount, float deltaSeconds, float rootDeltaSeconds, Vector3 rootPosition, Quaternion rootRotation, float placementScale, Vector3 parkPosition) {
        // The pose-continuity watch is read for every body-rooted registration, not only a follower-bearing one: a
        // teleport or a reused body slot invalidates a latched contact point the same way it invalidates a follower —
        // the world point a foot was planted at belongs to where the body was.
        var cut = false;

        if (live.BodyIndex is { } watched) {
            var epoch = client.PoseEpoch(index: watched);
            var address = client.EntityAddress(index: watched);
            var crossings = client.Carries.Epoch(index: watched);
            var continuous = ((live.RootEpoch == epoch) && (live.RootAddress == address));

            // An in-place crossing moved the body continuously through a rigid motion: everything this registration
            // latched in world space rides the same motion. A history too short to carry it is a cut.
            if (
                continuous &&
                (live.RootCarryEpoch != crossings)
            ) {
                if (client.Carries.TrySince(
                    carry: out var carry,
                    index: watched,
                    since: live.RootCarryEpoch
                )) {
                    Carry(
                        carry: carry,
                        live: live
                    );
                } else {
                    live.RootEpoch = -1;
                }
            }

            live.RootCarryEpoch = crossings;

            if ((live.RootEpoch != epoch) || (live.RootAddress != address)) {
                live.RootPositionFollower.Reseed();
                live.RootOrientationFollower.Reseed();
                live.RootEpoch = epoch;
                live.RootAddress = address;

                for (var shapeSlot = 0; (shapeSlot < live.PartFollower.Length); shapeSlot++) {
                    live.PartFollower[shapeSlot].Reseed();
                }

                Array.Clear(array: live.EffectorPlanted);
                Array.Clear(array: live.EffectorStepping);
                // The drivers measure travel from the pose they last saw, which a teleport of the same occupant has
                // left behind: they hold their phases and weights through the jump's frame and relatch there, and
                // their eased motion and followers restart, so the next frame reads the body's own motion and seeds
                // on it. A teleport or a respawn shows no one-frame spike and no pop. A new occupant reseeds them all
                // in WorldGaitDrivers.Advance.
                cut = (live.DriverSeeded && (live.DriverAddress == address));

                if (cut) {
                    live.DriverPosition = rootPosition;
                    live.DriverOrientation = rootRotation;
                    live.Kinematics.Reset();
                }
            }
        }

        if (live.HasRootDynamics) {
            StepRootFollower(
                deltaSeconds: rootDeltaSeconds,
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

        // Every slot carries the registration's scale: the creation's geometry is emitted in its slots' own units
        // (EmitOne), and the bake and the draws read the scale off the slot.
        var document = live.Creation.EngineDocument;
        var drivers = document.Drivers;

        live.PoseFrame = SelectPose(live: live);
        live.Kinematics.Resolve(
            drivers: drivers,
            rows: client.Definition.Dynamics
        );

        if (!cut) {
            live.DriverActivity = WorldGaitDrivers.Advance(
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
                reads: live.Reads,
                kinematics: live.Kinematics,
                definition: client.Definition
            );
            var activity = live.DriverActivity;

            if (activity.Initialized) { WorldCreationWork.ProcessWork.Count(kind: WorldCreationWork.DriverInitializations); }
            WorldCreationWork.ProcessWork.Add(kind: WorldCreationWork.DriverEvaluations, amount: activity.Evaluations);
            WorldCreationWork.ProcessWork.Add(kind: WorldCreationWork.DriverIntegrations, amount: activity.Integrations);
            WorldCreationWork.ProcessWork.Add(kind: WorldCreationWork.DriverPhaseChanges, amount: activity.PhaseChanges);
            if (activity.Continues) { WorldCreationWork.ProcessWork.Count(kind: WorldCreationWork.DriverContinuations); }
        }

        if (document.SizeBy is { } sizeBy && WorldGaitDrivers.TryDriver(
            rows: drivers ?? [], driver: sizeBy, phases: live.DriverPhase, weights: live.DriverWeight,
            phase: out var sizePhase, weight: out var sizeWeight)) {
            var size = float.Lerp(1f, Math.Clamp(sizePhase, 0f, 1f), sizeWeight);
            if (size == 0f) {
                rootPosition = parkPosition;
            } else {
                placementScale *= size;
            }
        }
        var slotsMoved = SdfMovedTransforms.Write(slot: rootSlot, table: transforms, value: new DynamicTransform(Lanes: lanes, Orientation: rootRotation, Position: rootPosition) { Scale = placementScale });

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
                live.ShapeTransforms[shapeIndex] = new DynamicTransform(
                    Lanes: lanes,
                    Orientation: Quaternion.Normalize(value: (rootRotation * live.PartDeltaRotation[shapeIndex])),
                    Position: (rootPosition + Vector3.Transform(
                        rotation: rootRotation,
                        value: (live.PartDeltaTranslation[shapeIndex] * placementScale)
                    ))
                ) { Scale = placementScale };

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

            live.ShapeTransforms[shapeIndex] = new DynamicTransform(
                Lanes: lanes,
                Orientation: Quaternion.Normalize(value: (rootRotation * rotation)),
                Position: worldPosition
            ) { Scale = placementScale };
        }
        for (var owner = 0; (owner < live.Motion.Owners.Length); owner++) {
            var shapeIndex = live.Motion.Owners[owner];
            var shape = shapes[shapeIndex];
            var posed = live.ShapeTransforms[shapeIndex];

            if (shape.Domain is { Count: > 0 }) { slotsMoved |= SdfMovedTransforms.Write(slot: ((rootSlot + owner) + 1), table: transforms, value: posed); continue; }
            var turn = Quaternion.Normalize(value: (posed.Orientation * Quaternion.Inverse(value: shape.Rotation.Value)));

            slotsMoved |= SdfMovedTransforms.Write(slot: ((rootSlot + owner) + 1), table: transforms, value: new DynamicTransform(
                Position: (posed.Position - Vector3.Transform((shape.Position.Value * placementScale), turn)),
                Orientation: turn, Lanes: lanes) { Scale = placementScale });
        }

        return slotsMoved;
    }
}
