using System.Numerics;
using Puck.Authoring;
using Puck.SdfVm;

namespace Puck.World.Client;

/// <summary>
/// The creation-STAMP pool: the reserved dynamic-transform pool a creation renders through as per-shape dynamic
/// instances, presentation-only (never simulation state). Two root sources share the ONE pool and the ONE reserved
/// slot budget:
/// <list type="bullet">
/// <item><description>an ANIMATED placement (a creation carrying timeline frames) roots on the placement's static
/// stamped transform and replays its frames HOLD-STYLE at the fixed cadence
/// (<see cref="WorldPlacementPolicy.TimelineSecondsPerFrame"/>);</description></item>
/// <item><description>a BODY-ROOTED stamp (an inhabited placement's body, or a crowd body wearing a creation look)
/// roots on the client's interpolated body pose, so an inhabited creation walks its authored walk cycle while its body
/// moves — that is the entire visual change over a static stamp.</description></item>
/// </list>
/// Reconciliation diffs delivered registrations by stable key against live ones (the same pattern camera reconciliation
/// uses): a pose/scale edit is a cheap property write (the replay clock survives), a creation-content change releases +
/// recreates (the clock resets), and a departed registration releases its pool slot at the delivery boundary (the
/// symmetric-release rule).
/// </summary>
/// <remarks>The pool is emitted on EVERY rebuild with a CONSTANT slot count
/// (<see cref="WorldPlacementPolicy.MaxStampRegistrations"/> × <see cref="SlotsPerPlacement"/>); an unused slot draws a
/// parked placeholder hidden below the floor, exactly like the avatar catalog's inactive-slot story. The probe path
/// emits every slot in its worst-case form (full modifier envelope, worst placement scale) — the frame source measures
/// it once at construction, so a body-rooted stamp never grows the frozen floor. Single-threaded on the window-pump
/// thread, like every editor/render type here.</remarks>
internal sealed class WorldStampPool {
    // Per-shape dynamic-instance bound at unit scale — a cull contract, not a policy: too tight clips a shape at
    // its own tile boundary.
    private const float InstanceRadiusUnitScale = 0.9f;
    private const float GroupBoundMargin = 0.4f;
    private static readonly Vector3 s_hiddenPosition = new(x: 0f, y: -1000f, z: 0f);

    /// <summary>One body-rooted creation stamp the frame source requests: a population body index and the creation whose
    /// geometry rides that body's live pose (an inhabited placement's creature, or a crowd body wearing a creation
    /// look).</summary>
    /// <param name="BodyIndex">The population entity index whose interpolated pose roots the stamp.</param>
    /// <param name="Creation">The creation whose geometry the body wears.</param>
    /// <param name="Scale">The uniform render scale (a placement's scale, or a look's scale).</param>
    public readonly record struct BodyStamp(int BodyIndex, WorldCreation Creation, float Scale);

    // One live registration: the resolved creation, its root source (a static placement OR a body index), and the
    // replay cursor state.
    private sealed class Registration {
        public required string Key;
        public required WorldCreation Creation;
        // The static-root placement (an ANIMATED placement), or null for a body-rooted stamp.
        public WorldPlacement? Row;
        // The body-rooted stamp's population index, or null for a static-root animated placement.
        public int? BodyIndex;
        public float Scale = 1f;
        public float Clock;
        public int FrameCursor;
        // Memoized per-frame shape-id → pose index (a pure derivation of the immutable document).
        public Dictionary<int, FrameTransformDocument>?[] FramePoses = [];
    }

    private readonly Registration?[] m_pool = new Registration?[WorldPlacementPolicy.MaxStampRegistrations];
    private readonly int m_slotBase;

    /// <summary>Initializes a new instance of the <see cref="WorldStampPool"/> class at its dynamic-transform slot base
    /// (the avatar catalog's frozen capacity — the pool sits immediately after it).</summary>
    /// <param name="slotBase">The pool's first dynamic-transform slot.</param>
    public WorldStampPool(int slotBase) => m_slotBase = slotBase;

    /// <summary>The dynamic-transform slots ONE registration reserves: its root + its full shape-slot pool.</summary>
    public static int SlotsPerPlacement => (1 + WorldPlacementPolicy.MaxAnimatedStampShapes);

    /// <summary>The whole pool's reserved dynamic-transform slot count — the frame source adds this onto the avatar
    /// catalog's frozen capacity.</summary>
    public static int DynamicSlotCount => (WorldPlacementPolicy.MaxStampRegistrations * SlotsPerPlacement);

    /// <summary>Reconciles the pool against a delivered definition (call at the delivery boundary, BEFORE the program
    /// rebuild): the ANIMATED placements root statically; the BODY stamps root on a population body. Diff-by-stable-key,
    /// cheap pose edits in place, release+recreate on creation-content change, symmetric release on removal. Animated
    /// placements are admitted first; body stamps fill the remaining free slots.</summary>
    /// <param name="placements">The delivered placement rows.</param>
    /// <param name="creations">The delivered creation rows.</param>
    /// <param name="bodyStamps">The resolved body-rooted stamps (inhabitants + crowd creation-looks) this frame.</param>
    public void Reconcile(IReadOnlyList<WorldPlacement> placements, IReadOnlyList<WorldCreation> creations, IReadOnlyList<BodyStamp> bodyStamps) {
        // Pass 1 — retire: a registration whose backing row/stamp vanished, went static, or changed creation content
        // releases its slot here; a same-content edit updates in place (clock preserved).
        for (var index = 0; (index < m_pool.Length); index++) {
            if (m_pool[index] is not { } live) {
                continue;
            }

            if (live.BodyIndex is { } bodyIndex) {
                var stamp = FindBodyStamp(bodyStamps: bodyStamps, bodyIndex: bodyIndex);

                if (stamp is not { } present) {
                    m_pool[index] = null;
                } else if (!string.Equals(a: present.Creation.Hash, b: live.Creation.Hash, comparisonType: StringComparison.Ordinal)) {
                    m_pool[index] = RegisterBody(stamp: present);
                } else {
                    live.Creation = present.Creation;
                    live.Scale = present.Scale;
                }

                continue;
            }

            var row = FindPlacement(placements: placements, id: live.Row!.Id);
            var creation = ((row is { } presentRow) ? WorldPlacementStamper.FindCreation(creations: creations, id: presentRow.CreationId) : null);

            if ((row is null) || (creation is null) || !WorldPlacementStamper.IsAnimated(creation: creation)) {
                m_pool[index] = null;

                continue;
            }

            if (!string.Equals(a: creation.Hash, b: live.Creation.Hash, comparisonType: StringComparison.Ordinal)) {
                m_pool[index] = RegisterRow(row: row, creation: creation);

                continue;
            }

            live.Row = row;
            live.Creation = creation;
        }

        // Pass 2 — admit new animated rows into free slots (the validator holds the ceiling; a race past it skips
        // loudly rather than corrupting a neighbor's slot).
        foreach (var placement in placements) {
            if ((WorldPlacementStamper.FindCreation(creations: creations, id: placement.CreationId) is not { } creation) ||
                !WorldPlacementStamper.IsAnimated(creation: creation) ||
                (FindRow(id: placement.Id) is not null)) {
                continue;
            }

            var slot = FreeSlot();

            if (slot < 0) {
                Console.Error.WriteLine(value: $"[world.placement: animated '{placement.Id}' has no free stamp slot — the {WorldPlacementPolicy.MaxStampRegistrations}-slot pool is full]");

                continue;
            }

            m_pool[slot] = RegisterRow(row: placement, creation: creation);
        }

        // Pass 3 — admit new body-rooted stamps into the remaining free slots.
        foreach (var stamp in bodyStamps) {
            if (FindBody(bodyIndex: stamp.BodyIndex) is not null) {
                continue;
            }

            var slot = FreeSlot();

            if (slot < 0) {
                Console.Error.WriteLine(value: $"[world.placement: creation-stamp body {stamp.BodyIndex} has no free stamp slot — the {WorldPlacementPolicy.MaxStampRegistrations}-slot pool is full; it renders as a catalog avatar]");

                continue;
            }

            m_pool[slot] = RegisterBody(stamp: stamp);
        }
    }

    /// <summary>Advances every live replay cursor on the render clock (hold-style: each frame holds
    /// <see cref="WorldPlacementPolicy.TimelineSecondsPerFrame"/>, looping 1..N; whole crossed frames subtract so a
    /// hitch lands on the right frame).</summary>
    /// <param name="deltaSeconds">Seconds advanced since the previous produced frame.</param>
    public void Tick(float deltaSeconds) {
        foreach (var live in m_pool) {
            if (live is null) {
                continue;
            }

            var frames = (live.Creation.Document.Frames ?? []);

            if (frames.Count == 0) {
                continue;
            }

            live.Clock += deltaSeconds;

            while (live.Clock >= WorldPlacementPolicy.TimelineSecondsPerFrame) {
                live.Clock -= WorldPlacementPolicy.TimelineSecondsPerFrame;
                live.FrameCursor = ((live.FrameCursor % frames.Count) + 1);
            }
        }
    }

    /// <summary>Packs the pool's per-frame transforms: each live registration's root rides its placement pose (animated)
    /// or the client's interpolated body pose (body-rooted) and each shape holds its CURRENT frame's snapshot (composed
    /// root ∘ per-shape pose, positions scaled by the registration scale); unused slots hide below the floor.</summary>
    /// <param name="transforms">The unified dynamic-transform buffer (the pool writes its own slot range).</param>
    /// <param name="client">The client whose interpolated body poses root the body-rooted stamps.</param>
    public void PackTransforms(Span<DynamicTransform> transforms, WorldClient client) {
        for (var index = 0; (index < m_pool.Length); index++) {
            var rootSlot = (m_slotBase + (index * SlotsPerPlacement));

            if (m_pool[index] is not { } live) {
                var hidden = new DynamicTransform(Orientation: Quaternion.Identity, Position: s_hiddenPosition);

                for (var slot = rootSlot; (slot < (rootSlot + SlotsPerPlacement)); slot++) {
                    transforms[slot] = hidden;
                }

                continue;
            }

            var (rootPosition, rootRotation, placementScale) = RootPose(live: live, client: client);

            transforms[rootSlot] = new DynamicTransform(Orientation: rootRotation, Position: rootPosition);

            var shapes = (live.Creation.Document.Shapes ?? []);
            var poses = FramePoses(live: live, frameCursor: live.FrameCursor);

            for (var shapeIndex = 0; (shapeIndex < WorldPlacementPolicy.MaxAnimatedStampShapes); shapeIndex++) {
                var slot = ((rootSlot + 1) + shapeIndex);

                if (shapeIndex >= shapes.Count) {
                    transforms[slot] = new DynamicTransform(Orientation: Quaternion.Identity, Position: s_hiddenPosition);

                    continue;
                }

                var shape = shapes[shapeIndex];
                var (position, rotation) = (((poses is not null) && poses.TryGetValue(key: shape.Id, value: out var pose))
                    ? (pose.Position, pose.Rotation)
                    : (shape.Position, shape.Rotation));

                transforms[slot] = new DynamicTransform(
                    Orientation: Quaternion.Normalize(value: (rootRotation * rotation)),
                    Position: (rootPosition + Vector3.Transform(value: (position * placementScale), rotation: rootRotation))
                );
            }
        }
    }

    /// <summary>Emits the whole pool (constant slot count): per live registration its palette, ungrouped shapes as
    /// per-slot dynamic instances, and blend groups as root-anchored scoped instances (a traveling bound for the
    /// group); parked placeholders elsewhere. The probe path takes the largest legal form.</summary>
    /// <param name="builder">The program builder.</param>
    /// <param name="probeWorstCase">Emit the worst-case form for capacity measurement (never rendered).</param>
    /// <param name="maxPlacementScale">LIVE-CONSUMED: the placement scale envelope's ceiling
    /// (<see cref="WorldAuthoringDefaults.MaxPlacementScale"/>), read fresh at every call — it only feeds spatial-cull
    /// bound radii here, never a word-capacity term, so re-reading it live cannot desync the frozen probe.</param>
    public void Emit(SdfProgramBuilder builder, bool probeWorstCase, float maxPlacementScale) {
        for (var index = 0; (index < m_pool.Length); index++) {
            var live = (probeWorstCase ? null : m_pool[index]);
            var rootSlot = (m_slotBase + (index * SlotsPerPlacement));

            EmitOne(builder: builder, live: live, probeWorstCase: probeWorstCase, rootSlot: rootSlot, maxPlacementScale: maxPlacementScale);
        }
    }

    /// <summary>Resolves a live registration's current-frame world position for one of its shapes (or its root when
    /// <paramref name="shapeId"/> is null) — the placement-anchor seam the audio director rides. Returns
    /// <see langword="false"/> when no live registration holds the placement (a static placement resolves through the
    /// stamp math instead).</summary>
    /// <param name="placementId">The placement row id.</param>
    /// <param name="shapeId">The creation shape id to ride, or <see langword="null"/> for the stamped root.</param>
    /// <param name="client">The client whose interpolated body poses root the body-rooted stamps.</param>
    /// <param name="position">The resolved world position.</param>
    public bool TryShapePosition(string placementId, int? shapeId, WorldClient client, out Vector3 position) {
        var live = FindRow(id: placementId);

        // An inhabited placement (a body-rooted stamp) resolves through the client's body pose, keyed by placement id.
        if ((live is null) && client.TryInhabitantBody(placementId: placementId, index: out var bodyIndex)) {
            live = FindBody(bodyIndex: bodyIndex);
        }

        if (live is null) {
            position = default;

            return false;
        }

        var (rootPosition, rootRotation, placementScale) = RootPose(live: live, client: client);

        if (shapeId is not { } targetShapeId) {
            position = rootPosition;

            return true;
        }

        var poses = FramePoses(live: live, frameCursor: live.FrameCursor);

        foreach (var shape in (live.Creation.Document.Shapes ?? [])) {
            if (shape.Id != targetShapeId) {
                continue;
            }

            var local = (((poses is not null) && poses.TryGetValue(key: targetShapeId, value: out var pose)) ? pose.Position : shape.Position);

            position = (rootPosition + Vector3.Transform(value: (local * placementScale), rotation: rootRotation));

            return true;
        }

        position = rootPosition;

        return true;
    }

    // The root pose of a live registration: a body-rooted stamp reads the client's interpolated body pose; an animated
    // placement reads its static stamped transform.
    private static (Vector3 Position, Quaternion Rotation, float Scale) RootPose(Registration live, WorldClient client) {
        if (live.BodyIndex is { } bodyIndex) {
            return (client.Position(index: bodyIndex), client.Orientation(index: bodyIndex), live.Scale);
        }

        var row = live.Row!;
        var rotation = Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: (row.YawDegrees * (MathF.PI / 180f)));

        return (row.Position, rotation, row.Scale);
    }

    private static Registration RegisterRow(WorldPlacement row, WorldCreation creation) => new() {
        Key = row.Id,
        Row = row,
        Creation = creation,
        Scale = row.Scale,
        FramePoses = new Dictionary<int, FrameTransformDocument>?[((creation.Document.Frames?.Count ?? 0) + 1)],
    };

    private static Registration RegisterBody(BodyStamp stamp) => new() {
        Key = $"body:{stamp.BodyIndex}",
        BodyIndex = stamp.BodyIndex,
        Creation = stamp.Creation,
        Scale = stamp.Scale,
        FramePoses = new Dictionary<int, FrameTransformDocument>?[((stamp.Creation.Document.Frames?.Count ?? 0) + 1)],
    };

    private Registration? FindRow(string id) {
        foreach (var live in m_pool) {
            if ((live is { BodyIndex: null }) && string.Equals(a: live.Row!.Id, b: id, comparisonType: StringComparison.Ordinal)) {
                return live;
            }
        }

        return null;
    }

    private Registration? FindBody(int bodyIndex) {
        foreach (var live in m_pool) {
            if (live is { BodyIndex: { } index } && (index == bodyIndex)) {
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

    private int FreeSlot() {
        for (var index = 0; (index < m_pool.Length); index++) {
            if (m_pool[index] is null) {
                return index;
            }
        }

        return -1;
    }

    private static WorldPlacement? FindPlacement(IReadOnlyList<WorldPlacement> placements, string id) {
        foreach (var placement in placements) {
            if (string.Equals(a: placement.Id, b: id, comparisonType: StringComparison.Ordinal)) {
                return placement;
            }
        }

        return null;
    }

    // The memoized shape-id → pose index for one frame cursor (null at cursor 0 — the rest pose).
    private static Dictionary<int, FrameTransformDocument>? FramePoses(Registration live, int frameCursor) {
        if ((frameCursor <= 0) || (live.Creation.Document.Frames is not { Count: > 0 } frames) || (frameCursor > frames.Count)) {
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

    // One pool slot's emission: palette, Pass 1 ungrouped shapes / parked placeholders, Pass 2 blend groups.
    private static void EmitOne(SdfProgramBuilder builder, Registration? live, bool probeWorstCase, int rootSlot, float maxPlacementScale) {
        var document = live?.Creation.Document;
        var shapes = (document?.Shapes ?? []);
        // The probe reserves a FULL distinct palette per pool slot (the conservative material bound); a live slot
        // registers its creation's real palette, an unused one a single placeholder entry — both within the probe.
        var paletteIds = (probeWorstCase
            ? ProbePalette(builder: builder)
            : WorldPlacementStamper.RegisterPalette(builder: builder, document: (document ?? EmptyDocument), tint: null));
        var placementScale = (probeWorstCase ? maxPlacementScale : (live?.Scale ?? 1f));
        var reach = ((probeWorstCase || (document is null)) ? (2.5f * maxPlacementScale) : (CreationGeometry.Reach(document: document!) * placementScale));

        // Pass 1 — ungrouped shapes and unused slots: one tight dynamic instance per shape slot; parked when absent
        // (the beam cull skips it with one branch). The probe stays fully active with the full modifier envelope.
        for (var index = 0; (index < WorldPlacementPolicy.MaxAnimatedStampShapes); index++) {
            var placed = ((index < shapes.Count) ? shapes[index] : null);

            if (placed is { Group: not null and not 0 }) {
                continue; // Pass 2 — the shape emits inside its group's instance.
            }

            var slot = ((rootSlot + 1) + index);
            var scale = ((placed?.Scale ?? Vector3.One) * placementScale);
            var material = paletteIds[((placed?.Material ?? 0) % paletteIds.Length)];
            var active = (probeWorstCase || (placed is not null));

            _ = builder.BeginInstanceDynamic(slot: slot, boundOffset: Vector3.Zero, boundRadius: (InstanceRadiusUnitScale * MaxComponent(scale: scale)), active: active);
            EmitShape(
                bend: (placed?.Bend ?? 0f),
                builder: builder,
                dilate: (placed?.Dilate ?? 0f),
                material: material,
                mirror: (placed?.Mirror ?? false),
                onion: (placed?.Onion ?? 0f),
                probeWorstCase: probeWorstCase,
                scale: scale,
                slot: slot,
                twist: (placed?.Twist ?? 0f),
                type: (placed?.Type ?? AvatarPrimitive.Sphere)
            );
            _ = builder.EndInstance();
        }

        // Pass 2 — blend groups, first-appearance order: ONE dynamic instance anchored on the ROOT slot (the
        // travelling bound), members in document order, wrapped in a field scope when the group needs one (the
        // Intersection-wipe fix; see the accumulator rule on SdfBlendOp).
        Span<int> emittedGroups = stackalloc int[WorldPlacementPolicy.MaxAnimatedStampShapes];
        var emittedCount = 0;

        for (var index = 0; ((index < shapes.Count) && (index < WorldPlacementPolicy.MaxAnimatedStampShapes)); index++) {
            var groupId = (shapes[index].Group ?? 0);

            if ((groupId == 0) || emittedGroups[..emittedCount].Contains(value: groupId)) {
                continue;
            }

            emittedGroups[emittedCount++] = groupId;
            EmitGroup(builder: builder, fromIndex: index, groupId: groupId, paletteIds: paletteIds, placementScale: placementScale, probeWorstCase: probeWorstCase, reach: reach, rootSlot: rootSlot, shapes: shapes);
        }
    }

    private static void EmitGroup(SdfProgramBuilder builder, IReadOnlyList<ShapeDocument> shapes, int groupId, int fromIndex, int rootSlot, int[] paletteIds, float placementScale, bool probeWorstCase, float reach) {
        var groupNeedsScope = GroupNeedsScope(fromIndex: fromIndex, groupId: groupId, shapes: shapes);

        _ = builder.BeginInstanceDynamic(slot: rootSlot, boundOffset: Vector3.Zero, boundRadius: (reach + GroupBoundMargin));

        if (groupNeedsScope) {
            _ = builder.PushField(compose: SdfBlendOp.Union);
        }

        for (var member = fromIndex; ((member < shapes.Count) && (member < WorldPlacementPolicy.MaxAnimatedStampShapes)); member++) {
            var shape = shapes[member];

            if ((shape.Group ?? 0) != groupId) {
                continue;
            }

            EmitShape(
                bend: (shape.Bend ?? 0f),
                blend: (shape.Blend ?? SdfBlendOp.Union),
                builder: builder,
                dilate: (shape.Dilate ?? 0f),
                inGroupScope: true,
                material: paletteIds[((shape.Material ?? 0) % paletteIds.Length)],
                mirror: (shape.Mirror ?? false),
                onion: (shape.Onion ?? 0f),
                probeWorstCase: probeWorstCase,
                scale: (shape.Scale * placementScale),
                slot: ((rootSlot + 1) + member),
                smooth: (shape.Smooth ?? 0f),
                twist: (shape.Twist ?? 0f),
                type: shape.Type
            );
        }

        if (groupNeedsScope) {
            _ = builder.PopField();
        }

        _ = builder.EndInstance();
    }

    // One shape's emission: ResetPoint + TransformDynamic + Scale + [mirror/twist/bend point ops] + the primitive +
    // [dilate/onion field ops, scoped outside a group] — the fixed op sequence over the canonical
    // CreationGeometry dimensions. probeWorstCase emits EVERY op unconditionally (the probe binding rule).
    private static void EmitShape(SdfProgramBuilder builder, int slot, AvatarPrimitive type, int material, Vector3 scale, bool probeWorstCase, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f, bool mirror = false, float twist = 0f, float bend = 0f, float dilate = 0f, float onion = 0f, bool inGroupScope = false) {
        var chain = builder.ResetPoint().TransformDynamic(slot: slot).Scale(scale: scale);

        if (probeWorstCase || mirror) {
            chain = chain.SymmetryX();
        }

        if (probeWorstCase || (twist != 0f)) {
            chain = chain.TwistY(rate: (probeWorstCase ? 1f : twist));
        }

        if (probeWorstCase || (bend != 0f)) {
            chain = chain.BendY(rate: (probeWorstCase ? 1f : bend));
        }

        var wantsDilate = (probeWorstCase || (dilate != 0f));
        var wantsOnion = (probeWorstCase || (onion != 0f));

        if ((wantsDilate || wantsOnion) && !inGroupScope) {
            var scoped = CreationGeometry.AppendPrimitive(blend: SdfBlendOp.Union, chain: chain.PushField(compose: blend, smooth: smooth), material: material, smooth: 0f, type: type);

            if (wantsDilate) {
                scoped = scoped.Dilate(radius: (probeWorstCase ? ShapeDocument.MaxDilate : dilate));
            }

            if (wantsOnion) {
                scoped = scoped.Onion(thickness: (probeWorstCase ? ShapeDocument.MaxOnion : onion));
            }

            _ = scoped.PopField();

            return;
        }

        var afterShape = CreationGeometry.AppendPrimitive(blend: blend, chain: chain, material: material, smooth: smooth, type: type);

        if (wantsDilate) {
            afterShape = afterShape.Dilate(radius: (probeWorstCase ? ShapeDocument.MaxDilate : dilate));
        }

        if (wantsOnion) {
            _ = afterShape.Onion(thickness: (probeWorstCase ? ShapeDocument.MaxOnion : onion));
        }
    }

    private static bool GroupNeedsScope(IReadOnlyList<ShapeDocument> shapes, int groupId, int fromIndex) {
        for (var member = fromIndex; (member < shapes.Count); member++) {
            var shape = shapes[member];

            if (((shape.Group ?? 0) == groupId) && (((shape.Blend ?? SdfBlendOp.Union) != SdfBlendOp.Union) || ((shape.Onion ?? 0f) != 0f) || ((shape.Dilate ?? 0f) != 0f))) {
                return true;
            }
        }

        return false;
    }

    private static float MaxComponent(Vector3 scale) => MathF.Max(x: scale.X, y: MathF.Max(x: scale.Y, y: scale.Z));

    private static int[] ProbePalette(SdfProgramBuilder builder) {
        var ids = new int[CreationDocument.PaletteSize];

        for (var index = 0; (index < ids.Length); index++) {
            ids[index] = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(value: 0.5f)));
        }

        return ids;
    }

    // The placeholder document an unused/probe slot registers its constant-shape palette against.
    private static readonly CreationDocument EmptyDocument = new(
        Schema: CreationDocument.CurrentSchema,
        Name: "empty",
        Intent: null,
        BakeStyle: null,
        Palette: null,
        Shapes: null,
        Frames: null
    );
}
