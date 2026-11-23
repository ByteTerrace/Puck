using System.Numerics;
using Puck.Hosting.Jobs;
using Puck.Maths;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Protocol;

namespace Puck.World.Client;

public sealed partial class WorldCreationInstances {
    /// <summary>Initializes a new instance of the <see cref="WorldCreationInstances"/> class.</summary>
    /// <param name="jobs">The pool each <see cref="PackTransforms"/> forks its registrations' full poses across, or
    /// <see langword="null"/> to pose them on the calling thread.</param>
    public WorldCreationInstances(JobPool? jobs = null) {
        Jobs = jobs;
        m_posePartitions = new PosePartitions(owner: this);
    }

    /// <summary>Gets the pool the pose pack forks across, or <see langword="null"/> when it poses on the calling thread;
    /// a presentation that builds pools of its own (a session's, a routed scene's) hands them this one.</summary>
    public JobPool? Jobs { get; }

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
    // Each drawing pool entry's place in m_meshDrawList, the entries whose draw the pack in progress changed, and
    // whether the entries that draw (or the registrations behind them) may differ from the list's.
    private int[] m_meshDrawAt = [];
    private readonly List<int> m_meshDrawsMoved = [];
    private bool m_meshDrawMembers = true;

    /// <summary>Gets the mesh draws the last <see cref="PackTransforms"/> posed: each live registration whose creation
    /// carries a mesh (<see cref="WorldPrototype.Mesh"/>), at its packed root and scale, in pool order: always the same
    /// list, rewritten in place when a draw moved, which <see cref="MeshDrawsRevision"/> then counts.</summary>
    public IReadOnlyList<SdfMeshDraw> MeshDraws => m_meshDrawList;
    /// <summary>Gets the revision of <see cref="MeshDraws"/>' content: one more for each pack that rewrote it.</summary>
    public long MeshDrawsRevision => m_meshDrawsRevision;
    /// <summary>Gets the journal of the rows of <see cref="MeshDraws"/> each pack rewrote: one frame per revision, naming
    /// the draws a pack rewrote in place, or owing the whole list where it composed the list again.</summary>
    public Puck.Abstractions.Gpu.GpuRowChanges MeshDrawChanges => m_meshDrawChanges.Reader;
    /// <summary>Gets the journal endpoint captured with <see cref="MeshDraws"/>.</summary>
    public long MeshDrawChangesSerial => m_meshDrawChanges.Serial;

    private readonly Puck.Abstractions.Gpu.GpuChangedRows m_meshDrawChanges = new();

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
            if (!held[index].SameStagedContent(other: packed[index])) {
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

            reaches[index] = (((shape.Blend is { } blend) && SdfBoundAlgebra.Carves(blend: blend))
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
    /// its slots. The pack visits only the registrations one of those woke (<see cref="VisitCount"/>), and each keeps
    /// what its last visit found (its volumes, door clip and mesh draw) until the next.
    /// </para>
    /// <para>
    /// The pack runs in three passes over the registrations it visits. A serial pass decides, in pool order, which
    /// park, retain, hold or pose in full; the full poses then run across <see cref="Jobs"/> as one
    /// <see cref="JobClass.Frame"/> fork, a partition a registration, each writing only its own registration's state and
    /// slot range; a last serial pass settles each in pool order. A registration whose rig reads another body
    /// (<see cref="CreationRigReadKind.Body"/>) is decided and posed in that last pass, so it reads every body it may read
    /// as this frame posed it.
    /// </para></summary>
    /// <param name="transforms">The unified dynamic-transform table (the pool writes its own slot range).</param>
    /// <param name="client">The client whose interpolated body poses root the body-rooted and attached stamps.</param>
    /// <param name="moved">The frame's moved set, which every repacked or vacated registration reports to.</param>
    /// <param name="slotBase">The pool's first dynamic-transform slot in <paramref name="transforms"/>, supplied by the
    /// emitter that owns the pool (see <see cref="Emit"/>).</param>
    /// <param name="parkPosition">Where an unused slot — or an attached row whose target body is not live this
    /// frame — parks, hidden below the floor (<see cref="SdfEmitContext.ParkPosition"/>).</param>
    /// <exception cref="ArgumentNullException"><paramref name="transforms"/>, <paramref name="client"/> or
    /// <paramref name="moved"/> is <see langword="null"/>.</exception>
    public void PackTransforms(DynamicTransform[] transforms, IWorldStampSource client, SdfMovedTransforms moved, int slotBase, Vector3 parkPosition) {
        ArgumentNullException.ThrowIfNull(argument: transforms);
        ArgumentNullException.ThrowIfNull(argument: client);
        ArgumentNullException.ThrowIfNull(argument: moved);

        // Every entry's slots, source or reads may have moved under it; any other pack visits only what woke.
        var everything = (moved.Everything || m_wakeAll || (slotBase != m_packedSlotBase) || !ReferenceEquals(objA: client, objB: m_packedSource));

        m_packedSlotBase = slotBase;
        m_packedSource = client;
        m_wakeAll = false;
        var volumesNeedRebuild = (m_volumesDirty || (slotBase != m_volumesSlotBase));
        if (m_hasAnyVolumes && (m_volumeActive.Length < m_poolCount)) {
            m_volumeActive = new bool[m_poolCount];
            m_volumeScale = new float[m_poolCount];
            m_volumeHidden = new bool[]?[m_poolCount];
            volumesNeedRebuild = true;
            // The new arrays hold no registration's volumes until it is visited.
            everything = true;
        }
        m_packVolumesChanged = false;
        m_packPoseMoved = false;
        m_packCount++;
        BeginRateCodes(slotBase: slotBase);

        var doors = WorldPortalStraddle.Doors(definition: client.Definition);

        // Other doors clip other bodies.
        everything |= !ReferenceEquals(objA: doors, objB: m_packedDoors);
        m_packedDoors = doors;
        var deltaSeconds = m_pendingDeltaSeconds;

        m_pendingDeltaSeconds = 0f;
        if (m_steps.Length < m_poolCount) {
            Array.Resize(array: ref m_steps, newSize: m_poolCount);
            Array.Resize(array: ref m_poseOrder, newSize: m_poolCount);
        }

        (m_packHeldBodies, m_packHeldPlacements, m_packHeldSlots) = (0, 0, 0);
        everything = CollectVisits(client: client, everything: everything);

        var visits = m_visits.AsSpan(start: 0, length: m_visitCount);
        var poses = 0;

        foreach (var index in visits) {
            if (m_pool[index] is { ReadsBodies: true }) {
                m_steps[index].Stage = PackStage.Deferred;

                continue;
            }

            BeginStep(client: client, deltaSeconds: deltaSeconds, index: index, moved: moved, parkPosition: parkPosition, slotBase: slotBase, transforms: transforms);
            if (m_steps[index].Stage == PackStage.Posed) {
                m_poseOrder[poses++] = index;
            }
        }

        m_posePartitions.Begin(client: client, deltaSeconds: deltaSeconds, parkPosition: parkPosition, transforms: transforms);
        try {
            if (Jobs is { } jobs) {
                jobs.Fork(body: m_posePartitions, jobClass: JobClass.Frame, partitions: poses);
            } else {
                for (var partition = 0; (partition < poses); partition++) {
                    m_posePartitions.Run(partition: partition);
                }
            }
        } finally {
            m_posePartitions.End();
        }
        WorldCreationWork.ProcessWork.Add(amount: poses, kind: WorldCreationWork.Forked);

        foreach (var index in visits) {
            if (m_steps[index].Stage == PackStage.Deferred) {
                BeginStep(client: client, deltaSeconds: deltaSeconds, index: index, moved: moved, parkPosition: parkPosition, slotBase: slotBase, transforms: transforms);
                if (m_steps[index].Stage == PackStage.Posed) {
                    Pose(client: client, deltaSeconds: deltaSeconds, index: index, parkPosition: parkPosition, transforms: transforms);
                }
            }

            EndStep(deltaSeconds: deltaSeconds, doors: doors, index: index, moved: moved, transforms: transforms);
        }

        CountUnvisitedHolds();

        if (volumesNeedRebuild || m_packVolumesChanged) {
            if (m_hasAnyVolumes || (m_volumes.Count > 0)) {
                m_volumes.Clear();
                if (m_hasAnyVolumes) {
                    for (var index = 0; (index < m_poolCount); index++) {
                        if (!m_volumeActive[index] || (m_pool[index] is not { } live) || (live.PackedRoot is null)) {
                            continue;
                        }

                        var rootSlot = (slotBase + RootSlotOffset(index: index));

                        AppendVolumes(
                            document: live.Creation.EngineDocument,
                            hidden: live.Hidden,
                            live: live,
                            placementScale: m_volumeScale[index],
                            rootSlot: rootSlot
                        );
                    }
                }
                VolumesRevision++;
            }
            m_volumesSlotBase = slotBase;
            m_volumesDirty = false;
        }

        var rigidPoseBefore = Rigid.PoseRevision;

        Rigid.Pack(deltaSeconds: deltaSeconds, everything: moved.Everything, slotBase: (slotBase + ArticulatedSlotCount), source: client, transforms: transforms);
        if (m_packPoseMoved || (Rigid.PoseRevision != rigidPoseBefore)) {
            PoseRevision++;
        }
        PackGuess(client: client, deltaSeconds: deltaSeconds, moved: moved, parkPosition: parkPosition, slotBase: slotBase, transforms: transforms);
        WorldCreationWork.ProcessWork.Count(kind: WorldCreationWork.Packs);
        PublishRates(client: client, slotBase: slotBase);

        // The runs are composed again, in pool order, only in a pack that changed a registration's clip. Keep unchanged
        // runs by value; the row writer compares them in order and visits only changed slots.
        if (m_clipsChanged) {
            m_clipsChanged = false;
            m_slotClipRun.Clear();
            m_slotClipBodies.Clear();
            foreach (var index in m_clipped) {
                if (m_pool[index] is { Clip: { } clip, BodyIndex: { } body }) {
                    m_slotClipRun.Add(item: clip);
                    m_slotClipBodies.Add(item: body);
                }
            }
        }
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

        // A pack that only moved draws rewrites each at its place in the list; one that changed which entries draw, or
        // follows a reconcile, composes the list again in pool order.
        if (m_meshDrawsChanged && !m_meshDrawMembers) {
            m_meshDrawChanges.Begin(everything: false, rows: m_meshDrawList.Count);
            foreach (var poolIndex in m_meshDrawsMoved) {
                m_meshDrawList[m_meshDrawAt[poolIndex]] = m_meshDraws[poolIndex]!.Value;
                m_meshDrawChanges.Owe(count: 1, start: m_meshDrawAt[poolIndex]);
            }

            m_meshDrawsRevision++;
            m_meshDrawsChanged = false;
        }
        m_meshDrawsMoved.Clear();
        if (m_meshDrawsChanged) {
            m_meshDrawList.Clear();
            m_meshDrawMembers = false;

            var identitiesChanged = false;
            var drawIndex = 0;

            for (var poolIndex = 0; (poolIndex < m_poolCount); poolIndex++) {
                if (m_meshDraws[poolIndex] is { } posed) {
                    m_meshDrawAt[poolIndex] = drawIndex;
                    m_meshDrawList.Add(item: posed);
                    identitiesChanged |= ((drawIndex >= m_pickMeshTargets.Length) ||
                        !ReferenceEquals(objA: m_pickMeshTargets[drawIndex], objB: m_pool[poolIndex]?.PickTarget));
                    drawIndex++;
                }
            }
            if (identitiesChanged || (drawIndex != m_pickMeshTargets.Length)) {
                m_pickMeshTargets = new WorldPickTarget?[drawIndex];
                drawIndex = 0;
                for (var poolIndex = 0; (poolIndex < m_poolCount); poolIndex++) {
                    if (m_meshDraws[poolIndex] is not null) {
                        m_pickMeshTargets[drawIndex++] = m_pool[poolIndex]?.PickTarget;
                    }
                }
            }

            m_meshDrawChanges.Begin(everything: true, rows: m_meshDrawList.Count);
            m_meshDrawsRevision++;
            m_meshDrawsChanged = false;
        }
    }

    // What the first serial pass decided for a pool entry this pack.
    private enum PackStage : byte {
        // No live registration: its slots were vacated.
        Vacant,
        // Nothing it reads moved and it owes no rated pack: its slots stand.
        Retained,
        // Its tier holds its pose: its slots rode its root.
        Held,
        // It poses in full, in the fork or, deferred, in the last pass.
        Posed,
        // Its rig reads another body, so the last pass decides it.
        Deferred,
    }
    // One pool entry's pack between its passes: what the first pass decided and read, and what its pose changed.
    private struct PackStep {
        public PackStage Stage;
        public int RootSlot;
        public int Slots;
        public int PackedSlots;
        public int ShapeCount;
        public Vector3 RootPosition;
        public Quaternion RootRotation;
        public float PlacementScale;
        // Whether the pose could have held (the cut test MeasurePartSpeed reads) and whether it resumes an unseen hold.
        public bool MayHold;
        public bool Unseen;
        // A held pose whose root stood still, which owes nothing.
        public bool Still;
        public bool SlotsMoved;
        // Whether the full pose switched the registration's active pose frame (CarryRevision).
        public bool PoseFrameChanged;
        // The seconds the full pose advances its drivers by, and the seconds since its previous full pose.
        public float PoseSeconds;
        public float ElapsedSeconds;
    }

    // What the serial passes learned this pack, folded into the volume list and PoseRevision after them. Only BeginStep
    // and EndStep write them, never a pose partition.
    private bool m_packVolumesChanged;
    private bool m_packPoseMoved;

    private PackStep[] m_steps = [];
    // The pool entries the first pass found posing in full, in pool order: partition i poses m_poseOrder[i].
    private int[] m_poseOrder = [];

    // The pose fork's partitions: one registration's full pose each. Every partition writes only its registration
    // (its fields, its effectors' held answers, its pack step) and that registration's slot range of the table, and
    // reads the client, the definition and the state mirror, which nothing writes during the fork but a lease's first
    // acquisition of a slot, which the mirror serializes (WorldStateMirror.Acquire). A rig that reads another body is
    // never in the fork, so no partition reads another registration's pose.
    private sealed class PosePartitions(WorldCreationInstances owner) : Puck.Hosting.Jobs.IJobPartitions {
        private IWorldStampSource? m_client;
        private DynamicTransform[] m_transforms = [];
        private float m_deltaSeconds;
        private Vector3 m_parkPosition;

        public void Begin(IWorldStampSource client, float deltaSeconds, Vector3 parkPosition, DynamicTransform[] transforms) {
            m_client = client;
            m_deltaSeconds = deltaSeconds;
            m_parkPosition = parkPosition;
            m_transforms = transforms;
        }
        public void End() {
            m_client = null;
            m_transforms = [];
        }
        public void Run(int partition) => owner.Pose(
            client: m_client!,
            deltaSeconds: m_deltaSeconds,
            index: owner.m_poseOrder[partition],
            parkPosition: m_parkPosition,
            transforms: m_transforms
        );
    }

    private readonly PosePartitions m_posePartitions;

    // The first pass for one pool entry: vacates an empty entry, and for a live registration reads its root, parks the
    // slots a swapped registration leaves, wakes it, and decides whether it retains, holds (carrying its slots with its
    // root) or poses in full.
    private void BeginStep(IWorldStampSource client, float deltaSeconds, int index, SdfMovedTransforms moved, Vector3 parkPosition, int slotBase, Span<DynamicTransform> transforms) {
        ref var step = ref m_steps[index];
        var rootSlot = (slotBase + RootSlotOffset(index: index));
        var slots = SlotsAt(index: index);
        var live = m_pool[index];

        step = new PackStep { RootSlot = rootSlot, Slots = slots };

        // An attached row whose body is not active contributes nothing this frame — the presentation mirror of
        // WorldPlacementAttachment.TryResolve's inactive-body verdict (which world.attachments echoes by reason).
        // The registration keeps its slot: occupancy changes tick to tick and a rebuild is not owed for one. A body
        // the client's view has not grown to yet reads inactive.
        if (
            (live is { Row.Attach: { } parked }) &&
            (!WorldBodiesLimits.IsBodyIndex(index: parked.BodyIndex) || !client.IsActive(index: parked.BodyIndex))
        ) {
            // A parked row is drawn nowhere, so it rates no body it reads (ReaderHertz reads PackedRoot).
            live.PackedRoot = null;
            live = null;
        }

        if (live is null) {
            if (m_hasAnyVolumes && m_volumeActive[index]) {
                m_volumeActive[index] = false;
                m_packVolumesChanged = true;
            }
            m_packedRegistrations[index] = null;
            SetMeshDraw(
                draw: null,
                index: index
            );
            Unhold(index: index);
            m_stateWatch.Forget(holder: index);
            if (m_clipped.Remove(item: index)) {
                m_clipsChanged = true;
            }

            if (m_owners.Vacate(
                moved: moved,
                owner: index
            )) {
                m_packPoseMoved = true;
                WorldTransformOwners.Park(
                    count: slots,
                    moved: moved,
                    parkPosition: parkPosition,
                    start: rootSlot,
                    table: transforms
                );
            }

            step.Stage = PackStage.Vacant;

            return;
        }

        WorldCreationWork.ProcessWork.Count(kind: WorldCreationWork.Visited);
        // The packs that skipped the registration left its clocks behind (WorldCreationInstances.Awake).
        AdvanceClock(live: live);
        CatchUpHold(deltaSeconds: deltaSeconds, index: index, live: live);
        var (rootPosition, rootRotation, placementScale) = RootPose(
            client: client,
            live: live
        );
        var document = live.Creation.EngineDocument;
        var shapeCount = (document.Shapes?.Count ?? 0);

        if (m_partSpheres.Length < shapeCount) { Array.Resize(array: ref m_partSpheres, newSize: shapeCount); }
        // The root, the shapes, and the slot text baked apart rides (TextApart).
        var packedSlots = live.Slots;

        step.RootPosition = rootPosition;
        step.RootRotation = rootRotation;
        step.PlacementScale = placementScale;
        step.PackedSlots = packedSlots;
        step.ShapeCount = shapeCount;

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
        var woken = Wake(
            client: client,
            deltaSeconds: deltaSeconds,
            index: index,
            live: live,
            moved: moved,
            rootPosition: rootPosition,
            rootRotation: rootRotation
        );

        NoteMotion(live: live, rootPosition: rootPosition, rootRotation: rootRotation);
        if (!woken && !live.RateOwed) {
            WorldCreationWork.ProcessWork.Count(kind: WorldCreationWork.Retained);
            WorldCreationWork.ProcessWork.Add(amount: packedSlots, kind: WorldCreationWork.SlotsRetained);
            step.Stage = PackStage.Retained;

            return;
        }

        // A pose holds between its rated packs, except on the frame its slots change owner or the whole table is
        // owed (WorldCreationInstances.Rates, whose one decision gives a placed creation only the held tier).
        var mayHold = (
            packedBefore &&
            (document.SizeBy is null) &&
            !moved.Everything &&
            !m_owners.Reseats(owner: index) &&
            // A held pose carries its slots rigidly, so a registration whose scale changed packs in full.
            (transforms[rootSlot].Scale == placementScale) &&
            // A rig with no finite reach cannot be judged unseen by its sphere, so a changed read poses it in
            // full: a part the read moves may be the one a view would see.
            (float.IsFinite(f: live.Rig.Reach) || (live.WakeRevision == live.ReadRevision))
        );

        if (!mayHold) {
            ForgetPhase(live: live);
        }

        step.MayHold = mayHold;

        // A body that starts moving poses on the frame it starts, whatever tier its rest held it at (StartsMoving).
        if (mayHold && !StartsMoving(live: live) && HoldsPose(center: rootPosition, deltaSeconds: deltaSeconds, live: live, placementScale: placementScale, radius: RateReach(live: live, placementScale: placementScale))) {
            var (heldPosition, heldRotation) = FollowHeldRoot(deltaSeconds: deltaSeconds, live: live, rootPosition: rootPosition, rootRotation: rootRotation);

            // A held pose whose root has not moved moves no slot, so nothing is carried or compared. Every write goes
            // through SdfMovedTransforms.Write, which compares as it writes, so the pack keeps no copy of the slots.
            step.Still = HeldRootStill(root: in transforms[rootSlot], rootPosition: heldPosition, rootRotation: heldRotation);
            if (!step.Still) {
                step.SlotsMoved = CarryPose(
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
            step.Stage = PackStage.Held;

            return;
        }

        // A pose held while nothing could show it resumes as a continuity cut: its followers, plants and drivers
        // relatch where the body is now, as after a teleport, rather than advancing over every second it held.
        step.Unseen = (live.RateSlowest == SdfUpdateRateTiers.HeldHertz);
        if (step.Unseen) {
            live.RootEpoch = -1;
        }

        WorldCreationWork.ProcessWork.Count(kind: WorldCreationWork.Posed);
        WorldCreationWork.ProcessWork.Add(amount: packedSlots, kind: WorldCreationWork.SlotsPosed);
        step.ElapsedSeconds = (live.RateSeconds + deltaSeconds);
        step.PoseSeconds = (step.Unseen ? deltaSeconds : step.ElapsedSeconds);
        step.Stage = PackStage.Posed;
    }
    // One registration's full pose, in a partition of the pose fork or, for a rig that reads another body, in the last
    // pass: writes only the registration and its own slot range.
    private void Pose(IWorldStampSource client, float deltaSeconds, int index, Vector3 parkPosition, Span<DynamicTransform> transforms) {
        ref var step = ref m_steps[index];
        var live = m_pool[index]!;
        var poseFrameBefore = live.PoseFrame;

        step.SlotsMoved = PackRegistration(
            client: client,
            deltaSeconds: step.PoseSeconds,
            live: live,
            parkPosition: parkPosition,
            placementScale: step.PlacementScale,
            rootDeltaSeconds: deltaSeconds,
            rootPosition: step.RootPosition,
            rootRotation: step.RootRotation,
            rootSlot: step.RootSlot,
            shapeCount: step.ShapeCount,
            transforms: transforms
        );
        step.PoseFrameChanged = (live.PoseFrame != poseFrameBefore);
        if (step.SlotsMoved) {
            live.PoseSerial++;
        }
    }
    // The last pass for one pool entry: settles a full or held pose with the moved set and its rate records, then notes
    // the registration's volumes, door straddle and mesh draw, which stand until its next visit, and keeps it awake or lets it sleep.
    private void EndStep(float deltaSeconds, IReadOnlyList<WorldPortalStraddle.Door> doors, int index, SdfMovedTransforms moved, Span<DynamicTransform> transforms) {
        ref var step = ref m_steps[index];

        if (step.Stage == PackStage.Vacant) {
            return;
        }

        var live = m_pool[index]!;
        var rootSlot = step.RootSlot;
        var posed = (step.Stage == PackStage.Posed);

        if (posed) {
            RecordPose(deltaSeconds: deltaSeconds, live: live);
            live.ReadRevision = live.WakeRevision;
            WidenMotionReach(live: live, placementScale: step.PlacementScale, rootPosition: transforms[rootSlot].Position, shapeCount: step.ShapeCount, transforms: transforms, rootSlot: rootSlot);
            // A pack that may not hold (a new owner, a reseat, a scale change, the whole table owed) or that resumes an
            // unseen hold is a cut the part speed is not measured across.
            MeasurePartSpeed(continuous: (step.MayHold && !step.Unseen), elapsedSeconds: step.ElapsedSeconds, live: live, root: in transforms[rootSlot], shapeCount: step.ShapeCount);
            PosedMotion(live: live, rootPosition: step.RootPosition, rootRotation: step.RootRotation);
            m_rateCounts?.Count(kind: ((live.BodyIndex is not null) ? SdfUpdateRateCounts.BodiesPosed : SdfUpdateRateCounts.PlacementsPosed));
            live.RateSeconds = 0f;
            live.RateOwed = false;
            live.PosedPack = m_packCount;
        }
        if (step.PoseFrameChanged) {
            CarryRevision++;
        }
        if (posed || (step.Stage == PackStage.Held)) {
            if (step.SlotsMoved) {
                m_packPoseMoved = true;
            }
            m_owners.Settle(
                deltaSeconds: deltaSeconds,
                moved: (!step.Still && moved.Commit(
                count: step.PackedSlots,
                moved: step.SlotsMoved,
                reseat: m_owners.Reseats(owner: index),
                start: rootSlot
            )),
                owner: index
            );
        }

        live.PackedRoot = transforms[rootSlot];

        var placementScale = transforms[rootSlot].Scale;

        // The volume list is rebuilt (PackTransforms) only when a volume-bearing registration's membership, scale or
        // hidden set changed, so each registration only compares itself here.
        if (m_hasAnyVolumes && (live.Creation.EngineDocument.Volumes is { Count: > 0 })) {
            if (!m_volumeActive[index] || (m_volumeScale[index] != placementScale) || !ReferenceEquals(objA: m_volumeHidden[index], objB: live.Hidden)) {
                m_volumeActive[index] = true;
                m_volumeScale[index] = placementScale;
                m_volumeHidden[index] = live.Hidden;
                m_packVolumesChanged = true;
            }
        }

        // A body straddling a portal whose counterpart stands in this world is drawn on both sides of it. The rest
        // bound screens out the bodies nowhere near a door; the posed parts decide. The clip stands with the slots it was
        // found over, so only a visit finds another.
        SetClip(
            clip: (((live.BodyIndex is not null) &&
                (doors.Count > 0) &&
                (WorldPortalStraddle.Clip(
                    center: transforms[rootSlot].Position,
                    doors: doors,
                    firstSlot: rootSlot,
                    radius: (ReachOf(live: live) * placementScale),
                    slotCount: step.Slots
                ) is not null))
                ? WorldPortalStraddle.Clip(
                    doors: doors,
                    firstSlot: rootSlot,
                    slotCount: step.Slots,
                    spheres: m_partSpheres.AsSpan(start: 0, length: PartSpheres(live: live, placementScale: placementScale, rootSlot: rootSlot, shapeCount: step.ShapeCount, transforms: transforms))
                )
                : null),
            index: index,
            live: live
        );

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
                ) with { IsDynamic = true, FrameSlot = rootSlot, UsesRootMaterialFrame = false }
                : null),
            index: index
        );

        live.VisitPack = m_packCount;
        m_stateWatch.Index(holder: index, lease: live.Reads);

        var held = (step.Stage == PackStage.Held);

        if (held) {
            Hold(center: step.RootPosition, index: index, live: live, radius: RateReach(live: live, placementScale: step.PlacementScale));
        } else {
            Unhold(index: index);
        }

        // The next pack visits the registration again by itself only while its work continues: a held pose is owed
        // whatever wakes it, so only a root follower still settling brings it back, and any other pose comes back while
        // its slots have not come to rest or a driver still advances. One that sleeps is scheduled for its next cursor
        // or cue change; a held one needs none until it poses.
        if (held ? (live.HasRootDynamics && !step.Still) : (m_owners.Restless(owner: index) || live.DriverActivity.Continues)) {
            WakeEntry(index: index);
        } else if (!held) {
            Schedule(index: index, live: live);
        }
    }

    private static float SizeOf(Registration live, IWorldStampSource client) =>
        ((live.Row is { Solid: not null } solidRow)
            ? (client.SolidScale(placementId: solidRow.Id.ToString()) ?? 0f)
            : (float)WorldCreationSize.Of(value: live.View.Sample(read: live.Rig.SizeBy!).Value));
    // Records one pool entry's draw, noting whether it changed.
    private void SetMeshDraw(int index, SdfMeshDraw? draw) {
        if (m_meshDraws[index] != draw) {
            m_meshDrawMembers |= ((m_meshDraws[index] is null) != (draw is null));
            m_meshDraws[index] = draw;
            m_meshDrawsChanged = true;
            m_meshDrawsMoved.Add(item: index);
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
        live.View.Bind(
            body: (live.BodyIndex ?? -1),
            client: client,
            poses: this
        );
        live.WakeRevision = live.View.Revision;

        // The pack's other inputs as one 64-bit digest: an unchanged version stands for unchanged inputs.
        var version = Fnv1aHash.Create();

        version.Add(value: unchecked((uint)live.EffectiveCursor));
        version.Add(value: unchecked((uint)facts));
        version.Add(value: live.WakeRevision);
        live.InputVersion = version.Value;

        var wake = m_owners.Wake(
            deltaSeconds: deltaSeconds,
            discontinuity: discontinuity,
            moved: moved,
            orientation: rootRotation,
            owner: index,
            position: rootPosition,
            version: version.Value
        );

        // Owner output equality cannot cancel an authored clock or a follower's remaining motion.
        return (wake || ((live.Creation.EngineDocument.SizeBy is not null) && (SizeOf(client: client, live: live) != live.PackedSize)) || ((deltaSeconds > 0f) && live.DriverActivity.Continues));
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

                for (var effector = 0; (effector < live.EffectorPlanted.Length); effector++) { ReleaseLatch(index: effector, live: live); }
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

        live.PoseFrame = SelectPose(live: live);
        live.Kinematics.Resolve(
            drivers: live.Rig.Drivers,
            rows: client.Definition.Dynamics
        );

        if (!cut) {
            live.DriverActivity = WorldGaitDrivers.Advance(
                address: ((live.BodyIndex is { } drivenBody) ? client.EntityAddress(index: drivenBody) : new WorldEntityAddress(Authority: string.Empty, Generation: 0, Index: -1)),
                deltaSeconds: deltaSeconds,
                rig: live.Rig,
                facts: live.View.Facts,
                easedSpeed: ref live.DriverSpeed,
                lastAddress: ref live.DriverAddress,
                lastOrientation: ref live.DriverOrientation,
                lastPosition: ref live.DriverPosition,
                orientation: rootRotation,
                phases: live.DriverPhase,
                position: rootPosition,
                seeded: ref live.DriverSeeded,
                weights: live.DriverWeight,
                reads: live.View,
                kinematics: live.Kinematics,
                definition: client.Definition,
                led: live.DriverLed
            );
            var activity = live.DriverActivity;

            if (activity.Initialized) { WorldCreationWork.ProcessWork.Count(kind: WorldCreationWork.DriverInitializations); }
            WorldCreationWork.ProcessWork.Add(kind: WorldCreationWork.DriverEvaluations, amount: activity.Evaluations);
            WorldCreationWork.ProcessWork.Add(kind: WorldCreationWork.DriverIntegrations, amount: activity.Integrations);
            WorldCreationWork.ProcessWork.Add(kind: WorldCreationWork.DriverPhaseChanges, amount: activity.PhaseChanges);
            if (activity.Continues) { WorldCreationWork.ProcessWork.Count(kind: WorldCreationWork.DriverContinuations); }
        }

        if (document.SizeBy is not null) {
            var size = SizeOf(client: client, live: live);

            live.PackedSize = size;
            if (size == 0f) { rootPosition = parkPosition; } else { placementScale *= size; }
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

        var terms = 0;

        for (var shapeIndex = 0; (shapeIndex < shapeCount); shapeIndex++) {
            terms += WorldGaitDrivers.ComposeDelta(
                phases: live.DriverPhase,
                rig: live.Rig,
                rotation: out var ownRotation,
                shape: shapeIndex,
                translation: out var ownTranslation,
                weights: live.DriverWeight,
                definition: client.Definition
            );

            live.PartOwnRotation[shapeIndex] = ownRotation;
            live.PartOwnTranslation[shapeIndex] = ownTranslation;
        }
        WorldCreationWork.ProcessWork.Add(amount: terms, kind: WorldCreationWork.TermsEvaluated);

        ChainPartDeltas(
            live: live,
            shapeCount: shapeCount
        );
        // The effectors correct the driver-posed chain, then everything downstream of a corrected bone re-chains off
        // the corrected own delta — so a hand parented to a solved forearm rides the solve with no effector of its own.
        if (ApplyEffectors(
            definition: client.Definition,
            deltaSeconds: deltaSeconds,
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
            // (identity when it has no parent) rather than a composed pose — its emission (ShapeEmission.Emit) rides this slot for its
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
        MeasureEffectorErrors(live: live);

        return slotsMoved;
    }
}
