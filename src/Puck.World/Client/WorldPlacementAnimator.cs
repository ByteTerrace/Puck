using System.Numerics;
using Puck.Authoring;
using Puck.SdfVm;

namespace Puck.World.Client;

/// <summary>
/// The ANIMATED-placement replay pool: a placement whose creation carries timeline frames renders as per-shape
/// dynamic instances riding a reserved dynamic-transform pool, replaying the frames HOLD-STYLE on the render clock at
/// the fixed 8-tick cadence (<see cref="WorldPlacementPolicy.TimelineSecondsPerFrame"/>), presentation-only (never
/// simulation state). Reconciliation diffs delivered placement rows by stable id against live registrations, the
/// same pattern camera reconciliation uses — a pose/scale edit is a cheap property write (the
/// replay clock survives), a creation-content change releases + recreates (the clock resets), and a removed row
/// releases its pool slot at the delivery boundary (the symmetric-release rule).
/// </summary>
/// <remarks>The pool is emitted on EVERY rebuild with a CONSTANT slot count
/// (<see cref="WorldPlacementPolicy.MaxAnimatedPlacements"/> × <see cref="SlotsPerPlacement"/>); an unused slot draws
/// a parked placeholder hidden below the floor, exactly like the avatar catalog's inactive-slot story. The probe path
/// emits every slot in its worst-case form (full modifier envelope, worst placement scale) — the frame source
/// measures it once at construction. Single-threaded on the window-pump thread, like every editor/render type here.</remarks>
internal sealed class WorldPlacementAnimator {
    // Per-shape dynamic-instance bound at unit scale — a cull contract, not a policy: too tight clips a shape at
    // its own tile boundary.
    private const float InstanceRadiusUnitScale = 0.9f;
    private const float GroupBoundMargin = 0.4f;
    private static readonly Vector3 s_hiddenPosition = new(x: 0f, y: -1000f, z: 0f);

    // One live registration: the delivered row + resolved creation and the replay cursor state.
    private sealed class Registration {
        public required WorldPlacement Row;
        public required WorldCreation Creation;
        public float Clock;
        public int FrameCursor;
        // Memoized per-frame shape-id → pose index (a pure derivation of the immutable document).
        public Dictionary<int, FrameTransformDocument>?[] FramePoses = [];
    }

    private readonly Registration?[] m_pool = new Registration?[WorldPlacementPolicy.MaxAnimatedPlacements];
    private readonly int m_slotBase;

    /// <summary>Initializes a new instance of the <see cref="WorldPlacementAnimator"/> class at its dynamic-transform
    /// slot base (the avatar catalog's frozen capacity — the pool sits immediately after it).</summary>
    /// <param name="slotBase">The pool's first dynamic-transform slot.</param>
    public WorldPlacementAnimator(int slotBase) => m_slotBase = slotBase;

    /// <summary>The dynamic-transform slots ONE animated placement reserves: its root + its full shape-slot pool.</summary>
    public static int SlotsPerPlacement => (1 + WorldPlacementPolicy.MaxAnimatedStampShapes);

    /// <summary>The whole pool's reserved dynamic-transform slot count — the frame source adds this onto the avatar
    /// catalog's frozen capacity.</summary>
    public static int DynamicSlotCount => (WorldPlacementPolicy.MaxAnimatedPlacements * SlotsPerPlacement);

    /// <summary>Reconciles the pool against a delivered definition (call at the delivery boundary, BEFORE the program
    /// rebuild): diff-by-stable-id, cheap pose edits in place, release+recreate on creation-content change, symmetric
    /// release on removal.</summary>
    /// <param name="placements">The delivered placement rows.</param>
    /// <param name="creations">The delivered creation rows.</param>
    public void Reconcile(IReadOnlyList<WorldPlacement> placements, IReadOnlyList<WorldCreation> creations) {
        // Pass 1 — retire: a registration whose row vanished, went static (its creation lost its frames), or changed
        // creation content releases its slot here; a same-content pose edit updates in place (clock preserved).
        for (var index = 0; (index < m_pool.Length); index++) {
            if (m_pool[index] is not { } live) {
                continue;
            }

            var row = FindPlacement(placements: placements, id: live.Row.Id);
            var creation = ((row is { } presentRow) ? WorldPlacementStamper.FindCreation(creations: creations, id: presentRow.CreationId) : null);

            if ((row is null) || (creation is null) || !WorldPlacementStamper.IsAnimated(creation: creation)) {
                m_pool[index] = null;

                continue;
            }

            if (!string.Equals(a: creation.Hash, b: live.Creation.Hash, comparisonType: StringComparison.Ordinal)) {
                // Structural: the creation's content changed — release and recreate (the replay restarts honestly).
                m_pool[index] = Register(row: row, creation: creation);

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
                (FindRegistration(id: placement.Id) is not null)) {
                continue;
            }

            var slot = FreeSlot();

            if (slot < 0) {
                Console.Error.WriteLine(value: $"[world.placement: animated '{placement.Id}' has no free replay slot — the {WorldPlacementPolicy.MaxAnimatedPlacements}-slot pool is full]");

                continue;
            }

            m_pool[slot] = Register(row: placement, creation: creation);
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

    /// <summary>Packs the pool's per-frame transforms: each live registration's root rides its placement pose and each
    /// shape holds its CURRENT frame's snapshot (composed root ∘ per-shape pose, positions scaled by the placement
    /// scale); unused slots hide below the floor.</summary>
    /// <param name="transforms">The unified dynamic-transform buffer (the pool writes its own slot range).</param>
    public void PackTransforms(Span<DynamicTransform> transforms) {
        for (var index = 0; (index < m_pool.Length); index++) {
            var rootSlot = (m_slotBase + (index * SlotsPerPlacement));

            if (m_pool[index] is not { } live) {
                var hidden = new DynamicTransform(Orientation: Quaternion.Identity, Position: s_hiddenPosition);

                for (var slot = rootSlot; (slot < (rootSlot + SlotsPerPlacement)); slot++) {
                    transforms[slot] = hidden;
                }

                continue;
            }

            var rootRotation = Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: (live.Row.YawDegrees * (MathF.PI / 180f)));

            transforms[rootSlot] = new DynamicTransform(Orientation: rootRotation, Position: live.Row.Position);

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
                    Position: (live.Row.Position + Vector3.Transform(value: (position * live.Row.Scale), rotation: rootRotation))
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

    /// <summary>Resolves a live ANIMATED placement's current-frame world position for one of its shapes (or its root
    /// when <paramref name="shapeId"/> is null) — the placement-anchor seam the audio director rides. Returns
    /// <see langword="false"/> when the placement holds no replay slot (a static placement resolves through the
    /// stamp math instead).</summary>
    /// <param name="placementId">The placement row id.</param>
    /// <param name="shapeId">The creation shape id to ride, or <see langword="null"/> for the stamped root.</param>
    /// <param name="position">The resolved world position.</param>
    public bool TryShapePosition(string placementId, int? shapeId, out Vector3 position) {
        if (FindRegistration(id: placementId) is not { } live) {
            position = default;

            return false;
        }

        if (shapeId is not { } targetShapeId) {
            position = live.Row.Position;

            return true;
        }

        var rootRotation = Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: (live.Row.YawDegrees * (MathF.PI / 180f)));
        var poses = FramePoses(live: live, frameCursor: live.FrameCursor);

        foreach (var shape in (live.Creation.Document.Shapes ?? [])) {
            if (shape.Id != targetShapeId) {
                continue;
            }

            var local = (((poses is not null) && poses.TryGetValue(key: targetShapeId, value: out var pose)) ? pose.Position : shape.Position);

            position = (live.Row.Position + Vector3.Transform(value: (local * live.Row.Scale), rotation: rootRotation));

            return true;
        }

        position = live.Row.Position;

        return true;
    }

    private static Registration Register(WorldPlacement row, WorldCreation creation) => new() {
        Row = row,
        Creation = creation,
        FramePoses = new Dictionary<int, FrameTransformDocument>?[((creation.Document.Frames?.Count ?? 0) + 1)],
    };

    private Registration? FindRegistration(string id) {
        foreach (var live in m_pool) {
            if ((live is not null) && string.Equals(a: live.Row.Id, b: id, comparisonType: StringComparison.Ordinal)) {
                return live;
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
        var placementScale = (probeWorstCase ? maxPlacementScale : (live?.Row.Scale ?? 1f));
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
