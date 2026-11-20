using System.Numerics;
using Puck.World.Authoring;
using Puck.Maths;
using Puck.SdfVm;
using Puck.SdfVm.Views;
using Puck.SignedDistance;
using Puck.Text;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>Live creation presentation: articulated instances retain animation state and actual motion-owner transforms; rigid bodies share local geometry assets and own only a root pose. Animated, attached and inhabited placements use the same pose and anchor machinery.</summary>
/// <remarks>Articulated instances allocate their actual root and motion-owner slots. Rigid bodies share local geometry through <see cref="WorldRigidAssets"/>.</remarks>
public sealed partial class WorldCreationInstances : IWorldBodyPoses {
    private int[] m_catalogSlots = [];

    private int m_catalogBase;

    /// <summary>Publishes the catalog looks' actual packed slot map for part anchors.</summary>
    public void SetCatalogSlots(int[] slots, int slotBase) { m_catalogSlots = slots; m_catalogBase = slotBase; }
    /// <summary>Reads a procedural look's part through its dense slot map.</summary>
    public bool TryCatalogPartPose(int body, string part, int rig, float scale, ReadOnlySpan<DynamicTransform> transforms, out SdfAnchor pose) {
        if ((((uint)body) >= ((uint)m_catalogSlots.Length)) || (m_catalogSlots[body] < 0)) { pose = default; return false; }
        return WorldRigCatalog.TryPartPose(0, part, rig, transforms[(m_catalogBase + m_catalogSlots[body])..], out pose, scale);
    }

    private const float GroupBoundMargin = 0.4f;

    private Registration?[] m_pool = [];

    /// <summary>Returns the pose serial of a body's registration (moved each time a full pack changes one of its slots),
    /// which a rig that reads the body hashes (<see cref="WorldRigReads.Revision"/>).</summary>
    /// <param name="body">The body's population index.</param>
    /// <returns>The serial; zero for a body with no registration.</returns>
    public int PoseSerial(int body) => (FindBody(bodyIndex: body)?.PoseSerial ?? 0);

    public WorldRigidAssets Rigid { get; } = new();

    public int ArticulatedSlotCount => RootSlotOffset(index: m_pool.Length);

    private int m_packedSlotBase = -1;

    /// <summary>Gets the operator's live shape isolation (<c>view.isolate</c>), which every live <see cref="Emit"/>
    /// applies to its registrations and the boot scene applies to its static placements.</summary>
    public WorldShapeIsolation Isolation { get; } = new();

    /// <summary>Restarts the presentation-only animation state of every registration a body roots, as for a new
    /// occupant: its gait drivers' phases, weights and eased speed, its foot plants and steps, and its root and part
    /// followers seed again from the next frame's pose. Nothing the simulation reads changes. A teleport of the same
    /// occupant otherwise keeps its gait phases, so a scripted motion repeated from one reset pose shows a different
    /// stride; this is the reset that makes the repeat look the same.</summary>
    /// <param name="bodyIndex">The 0-based entity index of the body.</param>
    /// <returns>The number of registrations restarted, zero when the body roots none.</returns>
    public int Reseed(int bodyIndex) {
        var reseeded = 0;

        foreach (var registration in m_pool) {
            if (
                (registration is not { } live) ||
                (live.BodyIndex != bodyIndex)
            ) {
                continue;
            }

            live.DriverSeeded = false;
            live.RootEpoch = -1;
            reseeded++;
        }

        return reseeded;
    }

    // The bounded volumes every live registration's creation authors (CreationDocument.Volumes), rebuilt by
    // PackTransforms each frame and riding the registration's root or a named shape's dynamic slot.
    private readonly List<SdfVolume> m_volumes = new(capacity: SdfProgramBuilder.MaxVolumes);

    // Latched by Tick, consumed once by the next PackTransforms — the frame delta the pool's root/part followers
    // step by (accumulates across a Tick called more than once before a pack, mirroring the replay clock above).
    private float m_pendingDeltaSeconds;

    /// <summary>One body-rooted creation stamp the frame source requests: a population body index and the creation whose
    /// geometry rides that body's live pose (an inhabited placement's creature, or a crowd body wearing a creation
    /// look).</summary>
    /// <param name="BodyIndex">The population entity index whose interpolated pose roots the stamp.</param>
    /// <param name="Creation">The creation whose geometry the body wears.</param>
    /// <param name="Scale">The uniform render scale (a placement's scale, or a look's scale).</param>
    /// <param name="Look">The look the body wears, whose motion carries the cues, timeline replay, and the root/part
    /// second-order followers (<see cref="WorldLookMotion.Dynamics"/>/<see cref="WorldLookMotion.PartDynamics"/>), and
    /// whose state reads the registration's lease acquires when the body arrives.</param>
    public readonly record struct BodyStamp(int BodyIndex, WorldPrototype Creation, float Scale, WorldLook Look);

    // One live registration: the resolved creation, its root source (a placement row — static or attached — OR a body
    // index), and the replay cursor state.
    private sealed class Registration(CreationRig rig) {
        public CompiledPlacementFrame StaticFrame;
        // The body-rooted stamp's population index, or null for a row-rooted registration. An ATTACHED row leaves this
        // null deliberately: FindBody keys the one-per-body creation-look registration, and an attached row rides a body
        // WITHOUT owning its look or its part namespace.
        public int? BodyIndex;

        private WorldPickTarget? m_pickTarget;

        public WorldPickTarget PickTarget => m_pickTarget ??= new WorldPickTarget(Placement: Row?.Id, BodyIndex: BodyIndex) { CreationSlots = Slots, Prototype = Creation.Id };

        // The rig the registration was made for: its tables are sized from it, and every pose read goes through it.
        public readonly CreationRig Rig = rig;

        // The dynamic-transform slots the registration's pool index reserves (SlotsAt), set when it takes the index.
        public CreationMotionPlan Motion = new(rig: rig);

        public int Slots => Motion.SlotCount;

        // The screen rows this registration last carried, by the authored row (reference), each at the frame it was built for.
        public Dictionary<WorldScreen, WorldScreen>? CarriedScreens;

        public readonly DynamicTransform[] ShapeTransforms = new DynamicTransform[rig.ShapeCount];

        public DynamicTransform? PackedRoot;

        // The registration's reads of the client's state mirror: every lane operand, driver signal and gate, pose and
        // effector reference the look reads, bound to BodyIndex so a $body key names the wearing body. Released when
        // the registration retires, so the mirror stops reading what no body wears any more.
        public readonly WorldStateLease Reads = new();

        private WorldRigReads? m_view;

        // Whether this is a pool's guess layer (WorldCreationInstances.Guess.cs), which no lookup of the pool reaches; only it
        // reads through a guessing view.
        public bool GuessLayer { get; init; }

        // The one view the pose pass reads through: the rig's compiled reads, state cells through Reads.
        public WorldRigReads View => (m_view ??= (GuessLayer ? WorldRigReads.ForGuess(lease: Reads, rig: Rig) : new WorldRigReads(lease: Reads, rig: Rig)));
        // Moves each time a full pack changes a slot, so a registration whose rig reads this body wakes (WorldRigReads.Revision).
        public int PoseSerial;
        // The view's revision at the last full pack, and at this frame's wake test, which tell a wake from a changed read.
        public ulong ReadRevision;
        public ulong WakeRevision;

        public float Clock;
        public required WorldPrototype Creation;
        // The look a body-rooted registration wears, or null for a row-rooted one: with Creation, the document objects
        // whose manifest templates Reads acquires when the body arrives.
        public WorldLook? Look;
        public int FrameCursor;
        // Cue state: the look's cues, each cue's timeline frame (1-based cursor, 0 = unresolved), when each next
        // self-fires on the cue clock, its fire count (the draw's seed), and the cue frame holding now (0 = none).
        public IReadOnlyList<WorldLookCue>? Cues;
        // The look's anonymous render-lane expressions (WorldLookMotion.Lanes), evaluated fresh every frame
        // against live state (WorldLookLaneEvaluator) and written into every dynamic slot this registration owns —
        // null for a row-rooted registration (row placements carry no WorldLookMotion) or an unauthored lane.
        public IReadOnlyList<ExpressionProgram?>? Lanes;

        // Whether the timeline replays on the render clock (an animated row always does; a body look only when its
        // motion says so — a cue-only timeline otherwise rests on frame 0, the live pose).
        public bool Replay = true;
        public int[] CueFrames = [];
        public float[] CueNextSeconds = [];
        public uint[] CueFires = [];

        public float CueClock;
        public int CueFrame;
        public float CueHoldUntil;

        // Pose state: each authored pose's timeline frame (1-based, 0 = unresolved) and state reference, and the pose
        // frame holding now (0 = none), re-read from the live state every PackTransforms.
        public int[] PoseFrames = [];
        public string[] PoseReferences = [];

        public int PoseFrame;

        // The cursor the frame reads: a holding pose overrides a firing cue, which overrides the replay cursor.
        public int EffectiveCursor => ((PoseFrame > 0) ? PoseFrame : ((CueFrame > 0) ? CueFrame : FrameCursor));

        // Memoized per-frame shape-id → pose index (a pure derivation of the immutable document).
        public Dictionary<int, FrameTransformDocument>?[] FramePoses = [];

        // The last text-run layout EmitOne computed for this registration's creation, and the scale it was computed
        // at (see ResolveTextLayouts) — null until the first hasText call. A creation-content change never reuses
        // this: Reconcile swaps in a brand-new Registration rather than mutating this one when the content hash
        // moves, so a stale layout can never survive onto different text.
        public TextLayoutResult[]? CachedTextLayouts;
        public float CachedTextLayoutScale;
        // The catalog the cached layouts' glyph bounds/UVs were resolved against: a definition.Text delivery packs a
        // new catalog while the creation hash (and so this Registration) survives, and a layout against the old
        // atlas must not be served against the new one.
        public object? CachedTextLayoutCatalog;
        public required AuthoredPartTable Parts;
        // The row-rooted placement (an ANIMATED or an ATTACHED one), or null for a body-rooted stamp.
        public WorldPlacement? Row;

        public float Scale = 1f;

        // The creation's mesh (WorldPrototype.Mesh) and the engine material its palette entry registered as, set by
        // each live EmitOne; PackTransforms poses it at the registration's root. Null for a creation without a mesh.
        public SdfMesh? Mesh;
        public int MeshMaterial;
        // The shapes the isolation hid when EmitOne last emitted this registration, null for none: PackTransforms drops
        // the volumes parented to them, so the volumes match the program the frame draws.
        public bool[]? Hidden;
        // The root position/orientation followers — set only for a body-rooted registration whose look names a root
        // Motion.Dynamics row (see ApplyMotion; a row-rooted registration never has this true). FollowedPosition/
        // FollowedOrientation are the values PackTransforms actually rendered this frame: the followers step at most
        // once per frame, there, so TryBodyPartAuthoredPose/TryLivePlacementPose read the latch instead of re-stepping.
        public bool HasRootDynamics;

        // The body's IWorldStampSource.PoseEpoch/EntityAddress this registration's followers last seeded against — -1/
        // default before the first pack. PackTransforms reseeds both root followers (and every part follower riding
        // this root) whenever either moves past this: PoseEpoch for a teleport or an over-threshold correction,
        // EntityAddress for a body index reused by a different inhabitant (a distinct address, even at the SAME
        // index and creation hash, so a same-content edit never inherits a stale follower position across it).
        public int RootEpoch = -1;
        // The body's IWorldStampSource.Carries count this registration's world-space state was last carried to; an
        // in-place crossing past it carries the state on (PackRegistration) instead of reseeding it.
        public int RootCarryEpoch = -1;
        // The creation's reach from its root (CreationGeometry.Reach), measured on the first pack that needs it; -1
        // before.
        public float Reach = -1f;

        // Each shape's reach about its own slot, in creation units, or -1 for a cutter (WorldCreationInstances.PartSpheres); null
        // before the first pack that needs it.
        public float[]? ShapeReaches;
        // The world.rates hold of a body's pose (WorldCreationInstances.Rates): the seconds the frames that held it carried its
        // pose past, which its next full pack advances its drivers, effectors and part followers by, and whether a held
        // frame owes that pack whatever wakes the registration.
        public float RateSeconds;
        public bool RateOwed;

        // The slowest tier the body was rated at since its last full pack, the tier its next interval is recorded under.
        public int RateSlowest = SdfUpdateRateTiers.EveryFrameHertz;
        // The tier the body's phase within its period was chosen at (every frame while unchosen), and the seconds its hold
        // decisions count ahead of its held seconds until its next full pack, so the bodies that take a tier together
        // spread their packs over the period's frames (WorldCreationInstances.Rates).
        public int RatePhaseHertz = SdfUpdateRateTiers.EveryFrameHertz;

        public float RateLead;

        // The pack whose rate decision RateHertz and RatePixelsPerMetre hold (WorldCreationInstances.RateOf), and the name
        // world.rates lists the registration by, made on its first reading.
        public long RatePack = -1L;
        public int RateHertz = SdfUpdateRateTiers.EveryFrameHertz;

        public float RatePixelsPerMetre;
        public string? RateName;

        // A reducing placed creation's measured part speed relative to its root, in creation units a second (infinity
        // until measured), and each shape's root-relative pose at its last full pack, which the next one measures
        // against (WorldCreationInstances.MeasurePartSpeed).
        public float PartSpeed = float.PositiveInfinity;
        public Vector3[] PartLocalPositions = [];
        public Quaternion[] PartLocalOrientations = [];
        // The pack count when something outside the pack last read one of the body's parts (NotePoseRead), and the
        // farthest, in creation units, any full pack has posed one of its parts' geometry from its root.
        public long PoseReadPack = (long.MinValue / 2);

        public float MotionReach;
        // The per-driver animation state of this registration, advanced once per PackTransforms by
        // WorldGaitDrivers. A placement without a body reads its root pose and world state with no body facts;
        // time and ungated/state-driven swings work there too.
        public bool DriverSeeded;
        public WorldGaitDrivers.Activity DriverActivity;

        public float PackedSize = 1f;

        public WorldEntityAddress DriverAddress;
        public Vector3 DriverPosition;

        public Quaternion DriverOrientation = Quaternion.Identity;

        public float DriverSpeed;

        public readonly float[] DriverPhase = new float[rig.Drivers.Count];
        public readonly float[] DriverWeight = new float[rig.Drivers.Count];
        // Whether each driver read the presenting seat's guess on its lead in place of its cell at the last advance.
        public readonly bool[] DriverLed = new bool[rig.Drivers.Count];
        public readonly WorldGaitKinematics Kinematics = new();

        public WorldEntityAddress RootAddress;
        public SecondOrderResponse RootResponse;
        public SecondOrderFollower3 RootPositionFollower;
        public SecondOrderFollower4 RootOrientationFollower;
        public Vector3 FollowedPosition;

        public Quaternion FollowedOrientation = Quaternion.Identity;
        // Per-shape-slot part position followers, resolved from the look's PartDynamics map through Parts —
        // indexed by shape slot (the same index PackTransforms's per-shape loop and Parts.TryResolve's
        // transformSlot use), sized once to the document's shape count.
        public readonly bool[] PartFollows = new bool[rig.ShapeCount];
        // The rigid delta each shape's animation produced this frame, kept so a later shape naming it as `parent`
        // rides it and a pose read (LatchedShapePose) answers with what was drawn; PartParent is the resolved parent
        // index per shape (−1 = the root), filled on first pack. The rotations start at identity, so a read before the
        // first pack answers the rest pose.
        public readonly Quaternion[] PartDeltaRotation = Enumerable.Repeat(
            count: rig.ShapeCount,
            element: Quaternion.Identity
        ).ToArray();
        public readonly Vector3[] PartDeltaTranslation = new Vector3[rig.ShapeCount];
        public readonly int[] PartParent = new int[rig.ShapeCount];

        public bool PartParentsResolved;

        // Each shape's OWN delta before the parent chain — kept because an effector folds its correction into a
        // bone's own delta and the whole chain then re-chains off these.
        public readonly Quaternion[] PartOwnRotation = new Quaternion[rig.ShapeCount];
        public readonly Vector3[] PartOwnTranslation = new Vector3[rig.ShapeCount];
        // The per-effector solve state, one entry an effector of the rig (whose bones and tip it resolved): each
        // effector's eased gate weight and its plant latch.
        public readonly float[] EffectorWeight = new float[rig.Effectors.Count];
        public readonly bool[] EffectorPlanted = new bool[rig.Effectors.Count];
        public readonly Vector3[] EffectorPlantTarget = new Vector3[rig.Effectors.Count];
        public readonly Vector3[] EffectorPlantNormal = new Vector3[rig.Effectors.Count];
        // The world point and contact normal each effector resolved this frame, and the share of the tip's tilt its
        // align took out, latched purely so `body.rig` can echo the decision the solve acted on; a frame that resolved
        // nothing leaves EffectorHasTarget false.
        public readonly Vector3[] EffectorTarget = new Vector3[rig.Effectors.Count];
        public readonly Vector3[] EffectorNormal = new Vector3[rig.Effectors.Count];
        public readonly float[] EffectorAlign = new float[rig.Effectors.Count];
        public readonly bool[] EffectorHasTarget = new bool[rig.Effectors.Count];
        // Each effector's held surface-probe answer, which TryProbeSurface reuses for its bit-identical next question.
        public readonly WorldFieldAnswer<WorldSurfaceQuery, WorldSurfaceAnswer>[] EffectorProbe = new WorldFieldAnswer<WorldSurfaceQuery, WorldSurfaceAnswer>[rig.Effectors.Count];
        // A resting plant's step (CreationReplantDocument): whether the tip is mid-step, the latched point it left,
        // the point it is heading for and that point's contact normal, and how far through the step it is, in [0, 1].
        public readonly bool[] EffectorStepping = new bool[rig.Effectors.Count];
        public readonly Vector3[] EffectorStepFrom = new Vector3[rig.Effectors.Count];
        public readonly Vector3[] EffectorStepTo = new Vector3[rig.Effectors.Count];
        public readonly Vector3[] EffectorStepToNormal = new Vector3[rig.Effectors.Count];
        public readonly float[] EffectorStepProgress = new float[rig.Effectors.Count];
        // Who each effector's target was when it last acted (WorldCreationInstances.TargetIdentity); a change releases its
        // latch. The last goal each drew and its normal, in the creation's own frame, which a frame with no target
        // solves toward while the weight eases out, and whether one was drawn. The weight each last applied (eased
        // level times authored weight times layer share), and its error on the final pose (NaN with no target).
        public readonly EffectorTargetIdentity[] EffectorIdentity = new EffectorTargetIdentity[rig.Effectors.Count];
        public readonly Vector3[] EffectorGoal = new Vector3[rig.Effectors.Count];
        public readonly Vector3[] EffectorGoalNormal = new Vector3[rig.Effectors.Count];
        public readonly bool[] EffectorHasGoal = new bool[rig.Effectors.Count];
        public readonly float[] EffectorApplied = new float[rig.Effectors.Count];
        public readonly float[] EffectorError = Enumerable.Repeat(count: rig.Effectors.Count, element: float.NaN).ToArray();

        public readonly SecondOrderResponse[] PartResponse = new SecondOrderResponse[rig.ShapeCount];
        public readonly SecondOrderFollower3[] PartFollower = new SecondOrderFollower3[rig.ShapeCount];
    }

    /// <summary>The whole pool's reserved dynamic-transform slot count — the frame source adds this onto the avatar
    /// catalog's frozen capacity.</summary>
    public int DynamicSlotCount => (ArticulatedSlotCount + Rigid.SlotCount + m_guessRows);
    public int RegistrationCount => m_pool.Length;

    public int SlotsAt(int index) => (m_pool[index]?.Slots ?? 0);
    // The dynamic-transform slots the registrations before a pool index reserve: a prefix sum over the pool's slot counts,
    // refilled by ReconcileSlots, the only writer of the pool and of a registration's motion plan. Index m_pool.Length
    // is the whole pool's count.
    public int RootSlotOffset(int index) => m_rootSlotOffsets[index];
    private int[] m_rootSlotOffsets = [0];
    private void RefillRootSlotOffsets() {
        if (m_rootSlotOffsets.Length != (m_pool.Length + 1)) {
            m_rootSlotOffsets = new int[(m_pool.Length + 1)];
        }

        var offset = 0;

        for (var entry = 0; (entry < m_pool.Length); entry++) {
            m_rootSlotOffsets[entry] = offset;
            offset = checked((offset + SlotsAt(index: entry)));
        }
        m_rootSlotOffsets[m_pool.Length] = offset;
    }

    // The Registration-cached text layout for hasText's (document, scale): a cache hit when the last layout this
    // registration computed still matches the scale being rendered this call, a fresh CreationStampEmitter.LayoutTextRuns
    // otherwise (also the first call — CachedTextLayouts starts null on every freshly registered/recreated instance).
    private static TextLayoutResult[] ResolveTextLayouts(PackedFontAtlasCatalog catalog, Registration live, CreationDocument document, float scale) {
        if (
            (live.CachedTextLayouts is { } cached) &&
            (live.CachedTextLayoutScale == scale) &&
            ReferenceEquals(
                objA: live.CachedTextLayoutCatalog,
                objB: catalog
            )
        ) {
            return cached;
        }

        var layouts = CreationStampEmitter.LayoutTextRuns(
            document: document,
            fontFor: catalog.Resolve,
            scale: scale
        );

        live.CachedTextLayouts = layouts;
        live.CachedTextLayoutScale = scale;
        live.CachedTextLayoutCatalog = catalog;

        return layouts;
    }

    // A pooled shape's frame for ShapeEmission. Every slot holds the rigid delta its motion imparts to creation space
    // (PackTransforms), and the bake reads it at identity, so a chain starts under the slot that moves the shape (a
    // trim's reference under its own) and then takes the shape's rest pose, in creation units; the slot's pose carries
    // the registration's scale. A relief samples the shape's rigid frame the same way.
    private readonly struct PoolShapeFrame(int slot, int rootSlot, CreationMotionPlan motion, int[] paletteIds) : IShapeEmissionFrame {
        public float FacetScale => 1f;

        public SdfProgramBuilder Prefix(SdfProgramBuilder builder) => builder.ResetPoint().TransformDynamic(slot: slot);
        public SdfProgramBuilder PrefixOf(SdfProgramBuilder builder, int shapeIndex) => builder.ResetPoint().TransformDynamic(slot: (rootSlot + motion.ShapeSlots[shapeIndex]));
        public SdfProgramBuilder Relief(SdfProgramBuilder builder, ShapeDocument shape) => Prefix(builder: builder).Translate(offset: shape.Position).Rotate(rotation: shape.Rotation);
        public int MaterialOf(ShapeDocument shape, int paletteSlot) => paletteIds[(paletteSlot % paletteIds.Length)];
    }

    private Registration? FindBody(int bodyIndex) {
        if (!m_bodySlotsStale) {
            return (((((uint)bodyIndex) < ((uint)m_bodySlots.Length)) && (m_bodySlots[bodyIndex] is var slot and >= 0))
                ? m_pool[slot]
                : null);
        }

        foreach (var live in m_pool) {
            if (
                (live is { BodyIndex: { } index }) &&
                (index == bodyIndex)
            ) {
                return live;
            }
        }

        return null;
    }
    private static BodyStamp? FindBodyStamp(IReadOnlyList<BodyStamp> bodyStamps, int bodyIndex) {
        foreach (var stamp in bodyStamps) {
            if (stamp.BodyIndex == bodyIndex) {
                return stamp;
            }
        }

        return null;
    }
    private bool TryFindRow(string id, out int poolIndex, out Registration live) {
        if (!m_bodySlotsStale) {
            if (m_rowSlots.TryGetValue(key: id, value: out poolIndex) && (m_pool[poolIndex] is { } registration)) {
                live = registration;

                return true;
            }

            poolIndex = -1;
            live = null!;

            return false;
        }

        for (var index = 0; (index < m_pool.Length); index++) {
            if (
                (m_pool[index] is { BodyIndex: null } registration) &&
                string.Equals(
                a: registration.Row!.Id,
                b: id,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                poolIndex = index;
                live = registration;

                return true;
            }
        }

        poolIndex = -1;
        live = null!;

        return false;
    }
    // The memoized shape-id → pose index for one frame cursor (null at cursor 0 — the rest pose).
    private static Dictionary<int, FrameTransformDocument>? FramePoses(Registration live, int frameCursor) {
        if (
            (frameCursor <= 0) ||
            (live.Creation.EngineDocument.Frames is not { Count: > 0 } frames) ||
            (frameCursor > frames.Count)
        ) {
            return null;
        }

        if (live.FramePoses[frameCursor] is { } cached) {
            return cached;
        }

        var frame = frames[(frameCursor - 1)];
        var poses = new Dictionary<int, FrameTransformDocument>(capacity: frame.Transforms.Count);

        foreach (var pose in frame.Transforms) {
            poses[pose.Id] = pose;
        }

        live.FramePoses[frameCursor] = poses;

        return poses;
    }
    // A creation that fits a compact registration takes one first, so small creations never crowd the full ones; any
    // creation then takes a free full registration.
    private int FreeSlot(CreationDocument document) {
        for (var index = 0; (index < m_pool.Length); index++) {
            if (m_pool[index] is null) { return index; }
        }
        var next = m_pool.Length;

        Array.Resize(array: ref m_pool, newSize: (next + 1));
        Array.Resize(array: ref m_packedRegistrations, newSize: (next + 1));
        Array.Resize(array: ref m_meshDraws, newSize: (next + 1));
        m_owners.EnsureCapacity(capacity: (next + 1));
        return next;
    }
    private static bool GroupNeedsScope(IReadOnlyList<ShapeDocument> shapes, int groupId, int fromIndex) {
        for (var member = fromIndex; (member < shapes.Count); member++) {
            var shape = shapes[member];

            // A warp or relief member taxes every march in the frame unless its chain sits inside a scope, whose pop
            // clamps the group's own 1/L onto its candidate instead (SdfProgram.AnalyzeLipschitz).
            if (
                ((shape.Group ?? 0) == groupId) &&
                (((shape.Blend ?? SdfBlendOp.Union) != SdfBlendOp.Union) || ((shape.Onion ?? 0f) != 0f) || ((shape.Dilate ?? 0f) != 0f) || (shape.Flare is not null) || (shape.Shear is not null) || (shape.Bumps is { Count: > 0 }) || (shape.Erode is not null) || (shape.Cells is not null))
            ) {
                return true;
            }
        }

        return false;
    }
    // Whether a placement row renders through THIS pool rather than as a static stamp: an animated creation (a replayed
    // timeline) or an attached row (a live body root). The exact complement of WorldPlacementStamper.IsStaticStamp for a
    // non-inhabited row — an inhabited row roots through the body-stamp census instead.
    private static bool PoolRooted(WorldPlacement row, WorldPrototype creation) =>
        ((row.Inhabit is null) && (WorldPlacementStamper.IsAnimated(creation: creation) || (row.Attach is not null)));
    private static Registration RegisterBody(BodyStamp stamp, bool guessLayer = false) {
        var registration = new Registration(rig: stamp.Creation.Rig) {
            BodyIndex = stamp.BodyIndex,
            GuessLayer = guessLayer,
            Creation = stamp.Creation,
            Parts = CreationPartCompiler.Compile(document: stamp.Creation.Document),
            Scale = stamp.Scale,
            FramePoses = new Dictionary<int, FrameTransformDocument>?[((stamp.Creation.Document.Frames?.Count ?? 0) + 1)],
        };

        LatchLook(
            live: registration,
            stamp: stamp
        );

        return registration;
    }
    // Latches what a body-rooted registration reads from its look: the lanes, the replay switch, the poses' frames and
    // references, and the cues. Called on registration and again on every same-content update, so a live look edit (a
    // retuned lane, a new pose) applies on the next frame; the cue clocks restart only when the cues themselves change.
    private static void LatchLook(Registration live, BodyStamp stamp) {
        var motion = stamp.Look.Motion;

        live.Motion = new CreationMotionPlan(rig: live.Rig.Follow(partDynamics: motion.PartDynamics));
        var frames = (stamp.Creation.Document.Frames ?? []);

        live.Look = stamp.Look;
        live.Replay = motion.ReplayFrames;
        live.Lanes = motion.Lanes;

        if (motion.Poses is { Count: > 0 } poses) {
            ResolvePoses(
                frames: frames,
                poses: poses,
                references: out live.PoseReferences,
                timelineFrames: out live.PoseFrames
            );
        } else {
            live.PoseReferences = [];
            live.PoseFrames = [];
        }

        if (
            (live.CueFrames.Length > 0) &&
            ReferenceEquals(
            objA: live.Cues,
            objB: motion.Cues
        )
        ) {
            return;
        }

        live.Cues = motion.Cues;

        var cues = (motion.Cues ?? []);

        live.CueFrames = new int[cues.Count];
        live.CueNextSeconds = new float[cues.Count];
        live.CueFires = new uint[cues.Count];

        for (var index = 0; (index < cues.Count); index++) {
            for (var frame = 0; (frame < frames.Count); frame++) {
                if (string.Equals(a: frames[frame]?.Name, b: cues[index].Frame, comparisonType: StringComparison.Ordinal)) {
                    live.CueFrames[index] = (frame + 1);

                    break;
                }
            }

            live.CueNextSeconds[index] = CueRest(
                cue: cues[index],
                fires: 0,
                seed: ((uint)stamp.BodyIndex)
            );
        }
    }
    // Resolves a body-rooted registration's follower response state from its look's Motion against the definition's
    // declared dynamics rows: HasRootDynamics/RootResponse from Motion.Dynamics, and PartFollows/PartResponse from
    // Motion.PartDynamics resolved through Parts. A row-rooted registration never calls this (Row-rooted rows carry
    // no WorldLookMotion), so HasRootDynamics/PartFollows stay at their all-false default for every animated/attached
    // placement. A dangling row or part id here — never authored, since the validator refuses one at document scope —
    // simply carries no follower for that entry, mirroring the camera compiler's own dangling-op rule. Called on
    // every Reconcile (both the same-content-edit branch and right after a fresh RegisterBody), so a live dynamics-
    // row retune takes effect on the same rebuild while every follower's Value/Velocity/Seeded state survives.
    private static void ApplyMotion(Registration live, WorldLookMotion motion, IReadOnlyList<DynamicsRow> dynamics) {
        live.HasRootDynamics = WorldDynamicsResponse.TryResolveResponse(
            name: motion.Dynamics,
            response: out live.RootResponse,
            rows: dynamics
        );

        Array.Clear(array: live.PartFollows);

        if (motion.PartDynamics is not { Count: > 0 } partDynamics) {
            return;
        }

        foreach (var (partId, rowName) in partDynamics) {
            if (
                !live.Parts.TryResolve(
                partId: partId,
                transformSlot: out var shapeSlot
            ) ||
                (((uint)shapeSlot) >= ((uint)live.PartFollows.Length)) ||
                !WorldDynamicsResponse.TryResolveResponse(
                name: rowName,
                response: out var response,
                rows: dynamics
            )
            ) {
                continue;
            }

            live.PartFollows[shapeSlot] = true;
            live.PartResponse[shapeSlot] = response;
        }
    }
    // Steps a body-rooted registration's root followers once (position and, hemisphere-matched, orientation) and
    // latches the result into FollowedPosition/FollowedOrientation for every other reader this frame.
    private static void StepRootFollower(Registration live, float deltaSeconds, Vector3 targetPosition, Quaternion targetRotation) {
        (live.FollowedPosition, live.FollowedOrientation) = SecondOrderPoseFollower.StepPose(
            deltaSeconds: deltaSeconds,
            orientation: ref live.RootOrientationFollower,
            position: ref live.RootPositionFollower,
            response: in live.RootResponse,
            targetOrientation: targetRotation,
            targetPosition: targetPosition
        );
    }
    // Steps one part's position follower once, in place, and returns the eased world position.
    private static Vector3 StepPartFollower(Registration live, int shapeSlot, float deltaSeconds, Vector3 target) => live.PartFollower[shapeSlot].Step(
        response: in live.PartResponse[shapeSlot],
        deltaSeconds: deltaSeconds,
        target: target
    );
    // The root pose FollowedRootPose falls back to before the first PackTransforms has ever latched one, or for a
    // registration whose root has no dynamics — the un-followed RootPose, bit for bit.
    private static (Vector3 Position, Quaternion Rotation, float Scale) FollowedRootPose(Registration live, IWorldStampSource client) {
        if (live.PackedRoot is { } packed) { return (packed.Position, packed.Orientation, packed.Scale); }
        var (position, rotation, scale) = RootPose(
            client: client,
            live: live
        );

        if (!live.HasRootDynamics) {
            return (position, rotation, scale);
        }

        return (
            (live.RootPositionFollower.Seeded ? live.FollowedPosition : position),
            (live.RootOrientationFollower.Seeded ? live.FollowedOrientation : rotation),
            scale
        );
    }
    // The rest before a cue's next self-fire: a uniform draw in min..max keyed by (body, fire count) — the same body
    // blinks the same way on every run, and no two bodies in step (each body is its own stream). Infinity for a
    // demand-only cue.
    private static float CueRest(WorldLookCue cue, uint fires, uint seed) {
        if ((cue.MinSeconds is not { } min) || (cue.MaxSeconds is not { } max)) {
            return float.PositiveInfinity;
        }

        var rng = Pcg32XshRr.Create(
            state: fires,
            stream: seed
        );
        var unit = ((float)((double)rng.NextUnitFraction32()));

        return (min + ((max - min) * unit));
    }

    /// <summary>Fires one of a body look's cues now — the door a driver (a face probe reading the player's camera, a
    /// dialogue line) blinks or mouths the avatar through; the cue's self-fire interval re-arms from this fire.</summary>
    /// <param name="bodyIndex">The population entity index.</param>
    /// <param name="frame">The cue's frame name.</param>
    /// <returns><see langword="true"/> when the body wears a creation look with such a cue.</returns>
    public bool TriggerCue(int bodyIndex, string frame) {
        if (!TryFindBody(bodyIndex: bodyIndex, live: out var live, poolIndex: out _) || (live.Cues is not { } cues)) {
            return false;
        }

        for (var index = 0; (index < cues.Count); index++) {
            if (!string.Equals(a: cues[index].Frame, b: frame, comparisonType: StringComparison.Ordinal)) {
                continue;
            }

            FireCue(index: index, live: live);

            return true;
        }

        return false;
    }

    internal void PresentCues(Puck.World.Protocol.WorldPresentationCueFeed feed, IWorldStampSource source) {
        while (feed.TryTake(out var tick)) {
            foreach (var delivered in tick.Cues) {
                var cue = delivered.Cue;
                if (cue.Body is { } body && source.IsActive(body) && source.EntityAddress(body).Generation == cue.Generation) {
                    TriggerCue(body, cue.Name);
                }
            }
        }
    }

    private static void FireCue(Registration live, int index) {
        var cue = live.Cues![index];

        live.CueFrame = live.CueFrames[index];
        live.CueHoldUntil = (live.CueClock + cue.HoldSeconds);
        live.CueFires[index]++;
        live.CueNextSeconds[index] = (live.CueHoldUntil + CueRest(
            cue: cue,
            fires: live.CueFires[index],
            seed: ((uint)(live.BodyIndex ?? 0))
        ));
    }
    private static Registration RegisterRow(WorldPlacement row, WorldPrototype creation, CompiledPlacementFrame frame) => new(rig: creation.Rig) {
        Row = row,
        Creation = creation,
        Parts = CreationPartCompiler.Compile(document: creation.Document),
        Scale = frame.Scale,
        StaticFrame = frame,
        FramePoses = new Dictionary<int, FrameTransformDocument>?[((creation.Document.Frames?.Count ?? 0) + 1)],
    };
    // The root pose of a live registration: a body-rooted stamp reads the client's interpolated body pose; an ATTACHED
    // row reads that same pose composed with its authored local offset/yaw; an animated placement reads its static
    // stamped transform.
    private static (Vector3 Position, Quaternion Rotation, float Scale) RootPose(Registration live, IWorldStampSource client) {
        if (live.BodyIndex is { } bodyIndex) {
            return (client.Position(index: bodyIndex), client.Orientation(index: bodyIndex), live.Scale);
        }

        var row = live.Row!;

        if (row.Attach is { } attach) {
            // PRESENTATION float, deliberately: this rides the client's INTERPOLATED body pose so an attached row is as
            // smooth as the body it sits on. The authoritative answer is the fixed-point one
            // (WorldPlacementAttachment.TryResolve, what world.attachments echoes); this is its render-side image, the
            // same relationship every avatar pose already has to the tick pose it interpolates. Same composition order:
            // rotate the local offset into the body's own frame, then add.
            var bodyOrientation = client.Orientation(index: attach.BodyIndex);

            return (
                (client.Position(index: attach.BodyIndex) + Vector3.Transform(
                value: attach.LocalOffset,
                rotation: bodyOrientation
            )),
                Quaternion.Normalize(value: (bodyOrientation * Quaternion.CreateFromAxisAngle(
                axis: Vector3.UnitY,
                angle: float.DegreesToRadians(degrees: attach.LocalYawDegrees)
            ))),
                row.Scale
            );
        }

        return (live.StaticFrame.Position, live.StaticFrame.Rotation, live.StaticFrame.Scale);
    }
    private bool TryFindBody(int bodyIndex, out int poolIndex, out Registration live) {
        if (!m_bodySlotsStale) {
            if ((((uint)bodyIndex) < ((uint)m_bodySlots.Length)) && (m_bodySlots[bodyIndex] is var slot and >= 0) && (m_pool[slot] is { } registration)) {
                poolIndex = slot;
                live = registration;

                return true;
            }

            poolIndex = -1;
            live = null!;

            return false;
        }

        for (var index = 0; (index < m_pool.Length); index++) {
            if (
                (m_pool[index] is { BodyIndex: { } candidate } registration) &&
                (candidate == bodyIndex)
            ) {
                poolIndex = index;
                live = registration;

                return true;
            }
        }

        poolIndex = -1;
        live = null!;

        return false;
    }

    /// <summary>Emits registered geometry: each live palette, ungrouped shapes as per-slot dynamic instances, and
    /// blend groups as bounded scoped instances. Rigid groups use a traveling member bound; other groups retain
    /// the creation-root envelope. Unused registrations and shape slots emit no instances;
    /// dynamic-transform addresses remain fixed.</summary>
    /// <param name="builder">The program builder.</param>
    /// <param name="colors">The colors the build bakes, which a registration's state-bound palette color resolves
    /// through.</param>
    /// <param name="slotBase">The pool's first dynamic-transform slot — the same value the matching
    /// <see cref="PackTransforms"/> call packs against. Supplied by the owning emitter (which derives it from its own
    /// <see cref="Puck.SdfVm.SdfEmitContext.SlotBase"/>) rather than latched here, so the pool carries no assumption
    /// about where in the composed buffer its owner sits.</param>
    /// <param name="textCatalog">The world's packed font catalog, or <see langword="null"/> when none is resolved (a
    /// remote projection) — a registration's text runs are then omitted, exactly as the static stamper omits
    /// them.</param>
    /// <param name="picks">The presentation identity table populated alongside live emission, or null.</param>
    /// <param name="screens">The world's screen rows, whose carried screens a body's look shows on its box
    /// (<see cref="TryCarriedScreen"/>), or <see langword="null"/> for none.</param>
    public void Emit(SdfProgramBuilder builder, WorldBakedColors colors, int slotBase, PackedFontAtlasCatalog? textCatalog = null, WorldPickMapBuilder? picks = null, IReadOnlyList<WorldScreen>? screens = null) {
        ArgumentNullException.ThrowIfNull(argument: colors);

        for (var index = 0; (index < m_pool.Length); index++) {
            if (m_pool[index] is not { } live) {
                continue;
            }

            var firstInstance = builder.InstanceCount;

            live.Hidden = Isolation.HiddenFor(document: live.Creation.EngineDocument, prototype: live.Creation.Id.Value);
            EmitOne(
                builder: builder,
                colors: colors,
                live: live,
                picks: picks,
                rootSlot: (slotBase + RootSlotOffset(index: index)),
                screenMaterials: CarriedScreenMaterials(live: live, screens: screens),
                textCatalog: textCatalog
            );
            picks?.Instances(first: firstInstance, end: builder.InstanceCount, target: live.PickTarget);
        }
        Rigid.Emit(builder, colors, (slotBase + ArticulatedSlotCount), textCatalog, (picks ?? new WorldPickMapBuilder()), Isolation);
    }
    /// <summary>Whether a live body-rooted creation look owns the entity's part namespace.</summary>
    /// <param name="bodyIndex">The population entity index.</param>
    public bool HasBodyRegistration(int bodyIndex) => (Rigid.ContainsBody(body: bodyIndex) || (FindBody(bodyIndex: bodyIndex) is not null));

    /// <summary>Gets the bounded volumes the pool's live registrations author, as packed by the latest
    /// <see cref="PackTransforms"/> — each riding its registration's root slot or its parent shape's slot. The rigid
    /// assets' volumes are <see cref="RigidVolumes"/>; the frame's volume list takes both without a joined copy of them.</summary>
    public IReadOnlyList<SdfVolume> Volumes => m_volumes;
    /// <summary>Gets the bounded volumes the rigid assets author.</summary>
    public IReadOnlyList<SdfVolume> RigidVolumes => Rigid.Volumes;

    // Volumes follow the root or motion owner with the authored parent frame composed in. Overflow refuses by name.
    private void AppendVolumes(Registration live, CreationDocument document, float placementScale, int rootSlot, bool[]? hidden) {
        if (document.Volumes is not { Count: > 0 } volumes) {
            return;
        }

        // Indexed, since the list is an interface and a foreach over it boxes its enumerator every frame.
        for (var position = 0; (position < volumes.Count); position++) {
            if (!WorldShapeIsolation.TryAdmitVolume(
                creation: document,
                emitted: m_volumes.Count,
                hidden: hidden,
                parentIndex: out var parentIndex,
                position: position,
                volume: out var volume
            )) {
                continue;
            }

            var slot = rootSlot;
            var origin = Vector3.Zero;
            var rotation = Quaternion.Identity;

            if (parentIndex >= 0) {
                var shape = document.Shapes![parentIndex];

                slot = (rootSlot + live.Motion.ShapeSlots[parentIndex]);
                origin = shape.Position.Value;
                rotation = shape.Rotation.Value;
            }

            m_volumes.Add(item: volume.ToVolume(
                dynamicSlot: slot,
                origin: (origin * placementScale),
                rotation: rotation,
                scale: placementScale
            ));
        }
    }
    // The shape's rest pose, or the timeline frame's snapshot of it when the registration's cursor names one.
    private static (Vector3 Position, Quaternion Rotation) BasePose(ShapeDocument shape, Dictionary<int, FrameTransformDocument>? poses) => (((poses is not null) && poses.TryGetValue(
        key: shape.Id,
        value: out var pose
    ))
        ? (pose.Position.Value, pose.Rotation.Value)
        : (shape.Position.Value, shape.Rotation.Value)
    );
    // Chains every shape's own delta under its parent's chained delta, in declaration order — a parent is validated to
    // precede its children, so one forward pass resolves the whole skeleton.
    private static void ChainPartDeltas(Registration live, int shapeCount) {
        for (var shapeIndex = 0; (shapeIndex < shapeCount); shapeIndex++) {
            var rotation = live.PartOwnRotation[shapeIndex];
            var translation = live.PartOwnTranslation[shapeIndex];
            var parent = live.PartParent[shapeIndex];

            if (parent >= 0) {
                WorldGaitDrivers.Chain(
                    parentRotation: live.PartDeltaRotation[parent],
                    parentTranslation: live.PartDeltaTranslation[parent],
                    rotation: ref rotation,
                    translation: ref translation
                );
            }

            live.PartDeltaRotation[shapeIndex] = rotation;
            live.PartDeltaTranslation[shapeIndex] = translation;
        }
    }
    // Resolves each shape's `parent` name to the index of an EARLIER shape (−1 = the root); the canonicalizer refuses a
    // parent that is missing or declared later, so an unresolved name here can only be a bypassed document.
    private static void ResolvePartParents(Registration live, IReadOnlyList<ShapeDocument> shapes) {
        CreationMotion.ResolveParents(parents: live.PartParent, shapes: shapes);
        live.PartParentsResolved = true;
    }

    /// <summary>Reconciles the pool against a delivered definition (call at the delivery boundary, before the program
    /// rebuild): the animated placements root statically, the attached ones root on their target body, and the body
    /// stamps root on a population body. Diff-by-stable-key, cheap pose edits in place, release+recreate on
    /// creation-content change, symmetric release on removal. Row-rooted placements are admitted first; body stamps fill
    /// the remaining free slots.</summary>
    /// <param name="placementFrames">The same delivery's canonical resolved frames, required for every static root.</param>
    /// <param name="placements">The delivered placement rows.</param>
    /// <param name="creations">The delivered creation rows.</param>
    /// <param name="dynamics">The delivered <c>dynamics</c> rows — resolves each body-rooted registration's root/part
    /// followers against its look's <see cref="WorldLookMotion.Dynamics"/>/<see cref="WorldLookMotion.PartDynamics"/>.</param>
    /// <param name="bodyStamps">The resolved body-rooted stamps (inhabitants + crowd creation-looks) this frame.</param>
    /// <param name="looks">The world's declared looks, whose rigid assets are compiled independently of body membership.</param>
    public void Reconcile(IReadOnlyDictionary<string, CompiledPlacementFrame> placementFrames, IReadOnlyList<WorldPlacement> placements, IReadOnlyList<WorldPrototype> creations, IReadOnlyList<DynamicsRow> dynamics, IReadOnlyList<BodyStamp> bodyStamps, IReadOnlyList<WorldLook>? looks = null) {
        Rigid.Reconcile(bodies: bodyStamps, creations: creations, looks: looks, placements: placements);
        bodyStamps = bodyStamps.Where(predicate: stamp => !WorldRigidAssets.IsRigid(creation: stamp.Creation, look: stamp.Look)).ToArray();
        m_bodySlotsStale = true;
        ReconcileSlots(bodyStamps: bodyStamps, creations: creations, dynamics: dynamics, placementFrames: placementFrames, placements: placements);
        IndexBodySlots();
    }

    // Indexes each body-rooted registration by its body, which only a reconcile moves, so a lookup per body costs no
    // scan of the pool.
    private void IndexBodySlots() {
        Array.Fill(array: m_bodySlots, value: -1);
        m_rowSlots.Clear();
        m_bodyReaders.Clear();
        m_selectedBodyReaders.Clear();
        for (var index = 0; (index < m_pool.Length); index++) {
            if ((m_pool[index] is { BodyIndex: { } body }) && (((uint)body) < ((uint)m_bodySlots.Length)) && (m_bodySlots[body] < 0)) {
                m_bodySlots[body] = index;
            }
            if (m_pool[index] is { BodyIndex: null, Row.Id: { } rowId }) { _ = m_rowSlots.TryAdd(key: rowId, value: index); }
            if (m_pool[index] is not { } live) {
                continue;
            }
            foreach (var read in live.Rig.Reads) {
                if (read.Kind != CreationRigReadKind.Body) {
                    continue;
                }
                if (read.Reference is not null) {
                    m_selectedBodyReaders.Add(item: (live, read));

                    continue;
                }
                if (!m_bodyReaders.TryGetValue(key: read.Body, value: out var readers)) {
                    m_bodyReaders.Add(key: read.Body, value: readers = []);
                }

                readers.Add(item: live);
            }
        }

        m_bodySlotsStale = false;
    }

    // The registrations whose rig reads each body (CreationRigReadKind.Body), by the body read, valid while no reconcile
    // is running: the rate decision rates a read body no slower than its readers.
    private readonly Dictionary<int, List<Registration>> m_bodyReaders = [];
    // The registrations whose rig reads a body a state cell selects, with that read: which body it names is resolved
    // when a rate is decided, through the reader's own view.
    private readonly List<(Registration Reader, CreationRigRead Read)> m_selectedBodyReaders = [];

    // The pool slot of each body's registration, or -1, valid while no reconcile is running.
    private readonly int[] m_bodySlots = NewBodySlots();
    // The pool slot of each row-rooted registration by placement id, the first slot of an id winning as the scan does,
    // valid while no reconcile is running.
    private readonly Dictionary<string, int> m_rowSlots = new(comparer: StringComparer.Ordinal);

    private bool m_bodySlotsStale;

    private static int[] NewBodySlots() {
        var slots = new int[WorldBodiesLimits.CapacityCeiling];

        Array.Fill(array: slots, value: -1);

        return slots;
    }
    private void ReconcileSlots(IReadOnlyDictionary<string, CompiledPlacementFrame> placementFrames, IReadOnlyList<WorldPlacement> placements, IReadOnlyList<WorldPrototype> creations, IReadOnlyList<DynamicsRow> dynamics, IReadOnlyList<BodyStamp> bodyStamps) {
        // Diff-by-stable-key, shared by both root kinds a slot can hold (KeyedReconciler.Reconcile): the entry's
        // current row resolves the fate — gone releases the slot, changed content releases+recreates, otherwise the
        // entry updates in place (clock preserved, and the refreshed Row/Creation carries any edited offset).
        BodyStamp? TryFindBodyStamp(Registration entry) => FindBodyStamp(
            bodyIndex: entry.BodyIndex!.Value,
            bodyStamps: bodyStamps
        );
        (WorldPlacement Row, WorldPrototype Creation)? TryFindPoolRootedRow(Registration entry) {
            if (
                (WorldDefinitionRows.FindPlacement(
                placements: placements,
                id: entry.Row!.Id
            ) is not { } presentRow) ||
                (WorldDefinitionRows.FindCreation(
                creations: creations,
                id: presentRow.ShownPrototypeId
            ) is not { } presentCreation) ||
                !PoolRooted(
                creation: presentCreation,
                row: presentRow
            )
            ) {
                return null;
            }

            return (presentRow, presentCreation);
        }

        // Pass 1 — retire: a registration whose backing row/stamp vanished, went static (lost its frames or its attach
        // facet), or changed creation content releases its slot here; a same-content edit updates in place.
        for (var index = 0; (index < m_pool.Length); index++) {
            if (m_pool[index] is not { } live) {
                continue;
            }

            var reconciled = ((live.BodyIndex is not null)
                ? KeyedReconciler.Reconcile(
                    live: live,
                    tryFindRow: TryFindBodyStamp,
                    isRecreateRequired: static (entry, stamp) => !string.Equals(
                        a: stamp.Creation.Hash,
                        b: entry.Creation.Hash,
                        comparisonType: StringComparison.Ordinal
                    ),
                    recreate: stamp => {
                        var fresh = RegisterBody(stamp: stamp);

                        ApplyMotion(
                            live: fresh,
                            motion: stamp.Look.Motion,
                            dynamics: dynamics
                        );

                        return fresh;
                    },
                    update: (entry, stamp) => {
                        entry.Creation = stamp.Creation;
                        entry.Scale = stamp.Scale;
                        LatchLook(
                            live: entry,
                            stamp: stamp
                        );
                        ApplyMotion(
                            live: entry,
                            motion: stamp.Look.Motion,
                            dynamics: dynamics
                        );
                    }
                )
                : KeyedReconciler.Reconcile(
                    live: live,
                    tryFindRow: TryFindPoolRootedRow,
                    isRecreateRequired: static (entry, found) => !string.Equals(
                        a: found.Creation.Hash,
                        b: entry.Creation.Hash,
                        comparisonType: StringComparison.Ordinal
                    ),
                    recreate: found => RegisterRow(
                        frame: placementFrames[found.Row.Id],
                        creation: found.Creation,
                        row: found.Row
                    ),
                    update: (entry, found) => {
                        entry.Row = found.Row;
                        entry.Creation = found.Creation;
                        entry.StaticFrame = placementFrames[found.Row.Id];
                        entry.Scale = entry.StaticFrame.Scale;
                    }
                )
            );

            if (!ReferenceEquals(
                objA: reconciled,
                objB: live
            )) {
                live.Reads.Release();
            }
            m_pool[index] = reconciled;
        }

        // Pass 2 — admit new row-rooted (animated or attached) rows into free slots (the validator holds the ceiling; a
        // race past it skips loudly rather than corrupting a neighbor's slot).
        foreach (var placement in placements) {
            if (
                (WorldDefinitionRows.FindCreation(
                creations: creations,
                id: placement.ShownPrototypeId
            ) is not { } creation) ||
                !PoolRooted(
                creation: creation,
                row: placement
            ) ||
                TryFindRow(id: placement.Id, live: out _, poolIndex: out _)
            ) {
                continue;
            }

            var slot = FreeSlot(document: creation.EngineDocument);



            var registered = RegisterRow(
                frame: placementFrames[placement.Id],
                creation: creation,
                row: placement
            );

            m_pool[slot] = registered;
        }

        // Pass 3 — admit new body-rooted stamps into the remaining free slots.
        foreach (var stamp in bodyStamps) {
            if (FindBody(bodyIndex: stamp.BodyIndex) is not null) {
                continue;
            }

            var slot = FreeSlot(document: stamp.Creation.EngineDocument);



            var fresh = RegisterBody(stamp: stamp);

            m_pool[slot] = fresh;
            ApplyMotion(
                live: fresh,
                motion: stamp.Look.Motion,
                dynamics: dynamics
            );
        }

        RefillRootSlotOffsets();
    }

    /// <summary>Advances every live replay cursor on the render clock (hold-style: each frame holds
    /// <see cref="WorldPlacementPolicy.TimelineSecondsPerFrame"/>, looping 1..N; whole crossed frames subtract so a
    /// hitch lands on the right frame), and latches <paramref name="deltaSeconds"/> for the pool's root/part
    /// followers, stepped once by the next <see cref="PackTransforms"/>.</summary>
    /// <param name="deltaSeconds">Seconds advanced since the previous produced frame.</param>
    public void Tick(float deltaSeconds) {
        m_pendingDeltaSeconds += deltaSeconds;

        foreach (var live in m_pool) {
            if (live is null) {
                continue;
            }

            var frames = (live.Creation.Document.Frames ?? []);

            if (frames.Count == 0) {
                continue;
            }

            if (live.Cues is { Count: > 0 } cues) {
                live.CueClock += deltaSeconds;

                if ((live.CueFrame > 0) && (live.CueClock >= live.CueHoldUntil)) {
                    live.CueFrame = 0;
                }

                for (var index = 0; (index < cues.Count); index++) {
                    if ((live.CueFrames[index] > 0) && (live.CueClock >= live.CueNextSeconds[index])) {
                        FireCue(index: index, live: live);
                    }
                }
            }

            if (!live.Replay) {
                continue;
            }

            live.Clock += deltaSeconds;

            while (live.Clock >= WorldPlacementPolicy.TimelineSecondsPerFrame) {
                live.Clock -= WorldPlacementPolicy.TimelineSecondsPerFrame;
                live.FrameCursor = ((live.FrameCursor % frames.Count) + 1);
            }
        }
    }
    /// <summary>Resolves a body-rooted creation look's current part pose without a packed buffer.</summary>
    /// <param name="bodyIndex">The population entity index.</param>
    /// <param name="partId">The ordinal, case-sensitive authored part identifier.</param>
    /// <param name="client">The client supplying the entity root pose.</param>
    /// <param name="pose">The current part pose, or default when unresolved.</param>
    /// <returns><see langword="true"/> when the live creation look publishes the part.</returns>
    /// <remarks>The pose carries the shape's COMPOSED delta — its own swings and slides, the parent chain, and any
    /// effector correction — read off the latch the last <see cref="PackTransforms"/> left, so an anchor consumer
    /// and the rendered geometry answer with the same pose (<see cref="TryLivePlacementPose"/> reads the same latch). A
    /// shape with no animation has an identity delta, so its anchor is its authored pose.</remarks>
    public bool TryBodyPartAuthoredPose(int bodyIndex, string partId, IWorldStampSource client, out SdfAnchor pose) {
        if (Rigid.ContainsBody(body: bodyIndex)) { return Rigid.TryPartPose(body: bodyIndex, partId: partId, pose: out pose, source: client); }
        if (
            !TryFindBody(
            bodyIndex: bodyIndex,
            live: out var live,
            poolIndex: out _
        ) ||
            !live.Parts.TryResolve(
            partId: partId,
            transformSlot: out var shapeSlot
        ) ||
            (live.Creation.EngineDocument.Shapes is not { } shapes) ||
            (((uint)shapeSlot) >= ((uint)shapes.Count))
        ) {
            pose = default;

            return false;
        }

        NotePoseRead(live: live);
        pose = LatchedShapePose(
            client: client,
            live: live,
            shapeSlot: shapeSlot,
            shapes: shapes
        );

        return true;
    }

    // A live registration's shape as the last PackTransforms drew it: the rest or frame pose, the composed delta (own
    // swings and slides, the parent chain, effector corrections) and the part follower, all read off the latch that
    // pack left. PackRegistration is the one evaluator; this reads its results, because the drivers advance once a
    // frame and a second evaluation would double-advance them or answer at a phase nothing was drawn at. A
    // domain-bearing shape's slot holds only its parent's delta frame, and composing its rest pose onto that delta is
    // what its drawn geometry shows.
    private static SdfAnchor LatchedShapePose(Registration live, int shapeSlot, IReadOnlyList<ShapeDocument> shapes, IWorldStampSource client) {
        // Before the first pack there is no published animation pose; expose the authored rest/frame pose.
        if (live.PackedRoot is null) {
            var (position, rotation) = BasePose(poses: FramePoses(frameCursor: live.EffectiveCursor, live: live), shape: shapes[shapeSlot]);
            var root = RootPose(client: client, live: live);

            return new SdfAnchor(Position: (root.Position + Vector3.Transform(rotation: root.Rotation, value: (position * root.Scale))),
                Orientation: Quaternion.Normalize(value: (root.Rotation * rotation)));
        }
        var pose = PackedShapePose(live: live, shapeIndex: shapeSlot);

        return new SdfAnchor(Position: pose.Position, Orientation: pose.Orientation);
    }

    /// <summary>Reads a packed shape's world pose by authored index, independently of the shared transform layout.</summary>
    public DynamicTransform BodyShapePose(int bodyIndex, int shapeIndex) {
        var live = FindBody(bodyIndex: bodyIndex);

        if ((live is null) || (((uint)shapeIndex) >= ((uint)live.ShapeTransforms.Length))) { throw new ArgumentOutOfRangeException(paramName: nameof(shapeIndex)); }
        NotePoseRead(live: live);
        return PackedShapePose(live: live, shapeIndex: shapeIndex);
    }

    // Anchor and rig readers use exactly the pose the pack published, including uniform size and followers.
    private static DynamicTransform PackedShapePose(Registration live, int shapeIndex) {
        var pose = live.ShapeTransforms[shapeIndex];
        var shape = live.Creation.EngineDocument.Shapes![shapeIndex];

        return ((shape.Domain is { Count: > 0 }) ? new DynamicTransform(
            Position: (pose.Position + Vector3.Transform((shape.Position.Value * pose.Scale), pose.Orientation)),
            Orientation: Quaternion.Normalize(value: (pose.Orientation * shape.Rotation.Value)), Lanes: pose.Lanes) { Scale = pose.Scale } : pose);
    }

    /// <summary>Resolves a body-rooted creation look's authored part pose from the current packed transforms.</summary>
    /// <param name="bodyIndex">The population entity index.</param>
    /// <param name="partId">The ordinal, case-sensitive authored part identifier.</param>
    /// <param name="transforms">The current composed transform buffer.</param>
    /// <param name="pose">The live part pose, or default when unresolved.</param>
    /// <returns><see langword="true"/> when the live creation look publishes a packed part pose.</returns>
    /// <remarks>An ordinary shape's slot IS its composed pose. A domain-bearing shape's slot carries only the
    /// parent's delta frame (see <see cref="PackTransforms"/>), so its rest pose is composed onto that frame here —
    /// the same answer <see cref="TryBodyPartAuthoredPose"/> gives for it, read from the packed buffer instead of
    /// the latch.</remarks>
    public bool TryBodyPartPose(int bodyIndex, string partId, ReadOnlySpan<DynamicTransform> transforms, out SdfAnchor pose) {
        if (Rigid.ContainsBody(body: bodyIndex)) { return Rigid.TryPartPose(body: bodyIndex, partId: partId, pose: out pose, source: null); }
        if (
            (m_packedSlotBase < 0) ||
            !TryFindBody(
            bodyIndex: bodyIndex,
            live: out var live,
            poolIndex: out var poolIndex
        ) ||
            !live.Parts.TryResolve(
            partId: partId,
            transformSlot: out var shapeSlot
        )
        ) {
            pose = default;

            return false;
        }

        var transformSlot = ((m_packedSlotBase + RootSlotOffset(index: poolIndex)) + live.Motion.ShapeSlots[shapeSlot]);

        NotePoseRead(live: live);

        if (((uint)transformSlot) >= ((uint)transforms.Length)) {
            pose = default;

            return false;
        }

        var transform = transforms[transformSlot];
        var shape = live.Creation.EngineDocument.Shapes![shapeSlot];

        pose = new SdfAnchor(Position: (transform.Position + Vector3.Transform((shape.Position.Value * transform.Scale), transform.Orientation)),
            Orientation: Quaternion.Normalize(value: (transform.Orientation * shape.Rotation.Value)));

        return true;
    }
    /// <summary>Resolves a live body's root dynamic transform, or false when its render stamp is absent.</summary>
    public bool TryBodyTransformSlot(int bodyIndex, out int transformSlot) => TryBodyTransformSlot(bodyIndex: bodyIndex, slotCount: out _, transformSlot: out transformSlot);
    /// <summary>Resolves a live body's root dynamic transform and the slots its registration reserves from it, or false
    /// when its render stamp is absent.</summary>
    /// <param name="bodyIndex">The population entity index.</param>
    /// <param name="transformSlot">The absolute composed-buffer root slot, or -1 when unresolved.</param>
    /// <param name="slotCount">The dynamic-transform slots the registration reserves from its root, or 0 when
    /// unresolved.</param>
    /// <returns><see langword="true"/> when the body's stamp is live and its pool range has packed.</returns>
    public bool TryBodyTransformSlot(int bodyIndex, out int transformSlot, out int slotCount) {
        if (Rigid.TryRootSlot(body: bodyIndex, slot: out transformSlot)) { slotCount = 1; return true; }
        if ((m_packedSlotBase < 0) || !TryFindBody(bodyIndex: bodyIndex, live: out _, poolIndex: out var poolIndex)) {
            transformSlot = -1;
            slotCount = 0;
            return false;
        }
        transformSlot = (m_packedSlotBase + RootSlotOffset(index: poolIndex));
        slotCount = SlotsAt(index: poolIndex);
        return true;
    }
    /// <summary>Resolves an entity's drawn root pose: its rigid copy's, or its registration's root as the last
    /// <see cref="PackTransforms"/> drew it (followers included), before the first pack its un-followed root.</summary>
    /// <param name="bodyIndex">The population entity index.</param>
    /// <param name="client">The body poses a registration roots on.</param>
    /// <param name="pose">The pose, or default.</param>
    /// <returns><see langword="false"/> when the body draws no stamp.</returns>
    public bool TryBodyRootPose(int bodyIndex, IWorldStampSource client, out SdfAnchor pose) {
        if (Rigid.ContainsBody(body: bodyIndex)) { return Rigid.TryShapePose(body: bodyIndex, pose: out pose, shapeId: null, source: client); }
        if (!TryFindBody(bodyIndex: bodyIndex, live: out var live, poolIndex: out _)) {
            pose = default;

            return false;
        }

        var (position, rotation, _) = FollowedRootPose(client: client, live: live);

        pose = new SdfAnchor(Position: position, Orientation: rotation);

        return true;
    }
    /// <summary>Resolves a placement's live pose, for one of its shapes or its root, as the last
    /// <see cref="PackTransforms"/> drew it: a shape's pose carries its composed delta (its own swings and slides, the
    /// parent chain, effector corrections) and its part follower, read off the pack's latch with no rig evaluation of
    /// its own; before the first pack it is the rest or frame pose. An inhabited placement rides its lowest live
    /// inhabitant's registration or rigid copy, or the inhabitant's body pose before the pool registers it.</summary>
    /// <param name="placementId">The placement row id.</param>
    /// <param name="shapeId">The creation shape id to ride, or <see langword="null"/> for the root; one the creation
    /// does not name rides the root.</param>
    /// <param name="client">The body poses an inhabited or attached row roots on.</param>
    /// <param name="pose">The pose, or default.</param>
    /// <param name="held">Whether the pool holds a registration for the placement, so that a <see langword="false"/>
    /// answer means it draws nothing this frame rather than that it is not pool-rooted.</param>
    /// <returns><see langword="true"/> when the pool draws the placement this frame; <see langword="false"/> when no
    /// registration holds it, or for an attached row whose carrier is not active (the row contributes nothing, the
    /// same verdict <see cref="PackTransforms"/> renders as a hidden stamp).</returns>
    public bool TryLivePlacementPose(string placementId, int? shapeId, IWorldStampSource client, out SdfAnchor pose, out bool held) {
        var inhabitant = (client.TryInhabitantBody(index: out var body, placementId: placementId) ? body : -1);

        if ((inhabitant >= 0) && Rigid.ContainsBody(body: inhabitant) && Rigid.TryShapePose(body: inhabitant, pose: out pose, shapeId: shapeId, source: client)) {
            held = true;

            return true;
        }

        held = true;

        if (
            !TryFindRow(id: placementId, live: out var live, poolIndex: out _) &&
            ((inhabitant < 0) || !TryFindBody(bodyIndex: inhabitant, live: out live, poolIndex: out _))
        ) {
            // An inhabitant the pool has not registered yet is drawn at its body's pose all the same.
            if (inhabitant >= 0) {
                pose = new SdfAnchor(Position: client.Position(index: inhabitant), Orientation: client.Orientation(index: inhabitant));

                return true;
            }

            pose = default;
            held = false;

            return false;
        }

        // An attached row whose target body is not live resolves no pose: the same inactive-body verdict PackTransforms
        // applies before hiding the render stamp below the floor, so no reader treats a stale carrier pose as the row's.
        if (
            (live.Row?.Attach is { } attach) &&
            ((((uint)attach.BodyIndex) >= ((uint)WorldClient.EntityCapacity)) || !client.IsActive(index: attach.BodyIndex))
        ) {
            pose = default;

            return false;
        }

        if (shapeId is { } targetShapeId) {
            var shapes = (live.Creation.EngineDocument.Shapes ?? []);

            for (var shapeIndex = 0; (shapeIndex < shapes.Count); shapeIndex++) {
                if (shapes[shapeIndex].Id == targetShapeId) {
                    NotePoseRead(live: live);
                    pose = LatchedShapePose(
                        client: client,
                        live: live,
                        shapeSlot: shapeIndex,
                        shapes: shapes
                    );

                    return true;
                }
            }
        }

        var (position, rotation, _) = FollowedRootPose(client: client, live: live);

        pose = new SdfAnchor(Position: position, Orientation: rotation);

        return true;
    }
}
