using System.Numerics;
using Puck.Authoring;
using Puck.Demo.Forge;
using Puck.SdfVm;

namespace Puck.Demo.Creator;

/// <summary>
/// Emits the room's live companions into the world's SDF program and packs their per-frame dynamic transforms —
/// mirrors <see cref="CreatorSceneRenderer.EmitPool"/>'s pattern EXACTLY (per companion: one root dynamic-transform
/// slot + one dynamic instance per ungrouped shape and one per blend group, grouped via
/// <see cref="SdfProgramBuilder.BeginInstanceDynamic"/> anchored on the companion's ROOT slot). The pool is emitted
/// on EVERY rebuild with a CONSTANT slot count (<see cref="CompanionState.MaxCompanions"/> × (1 root + the full
/// shape-pool capacity)); an unused companion slot draws a default sphere hidden below the floor, exactly like the
/// creator pool's own unused-slot story. <see cref="EmitCompanions"/>'s <c>probeWorstCase</c> path emits the
/// synthetic worst case (every companion slot, every shape slot, a full palette registration each) — the frame
/// source measures this ONCE at construction and sizes the engine's program/instance buffers against it, so any
/// REAL emission (fewer companions, fewer shapes) always fits by construction. THE BINDING RULE: any real emission
/// must be ≤ the probe by construction — growing this emission (a new per-shape modifier, a new archetype feature)
/// MUST grow the probe in the same change (see <see cref="CreatorSceneRenderer"/>'s remarks for the identical
/// discipline it settled first).
/// <para>
/// UNLIKE the creator workbench's STATIC group bound (fixed to the authoring region, since the workbench never
/// moves), a companion's group instance bound TRAVELS with its root: <see cref="SdfProgramBuilder.BeginInstanceDynamic"/>
/// tracks the ROOT slot's per-frame position, and the bound radius is the creation's reach × scale (see
/// <see cref="CompanionState.Reach"/>) — a companion ambling across the room keeps its blended geometry inside its
/// own moving bound rather than needing a room-spanning static one.
/// </para>
/// </summary>
public sealed class CompanionRenderer {
    // The shape-slot pool a single companion reserves, worst-case: mirrors CreatorScene.Capacity so a companion
    // built from the biggest creation the editor could ever produce still fits. In practice a companion's document
    // is expected to carry far fewer shapes (a small creature, not a full 64-shape sculpt), but the probe must cover
    // the ceiling, not the common case.
    private const int ShapeSlotCapacity = CreatorScene.Capacity;
    // A generous per-shape bound AT UNIT SCALE — mirrors CreatorSceneRenderer.InstanceRadiusUnitScale so an unused
    // slot's hidden instance culls to nothing and a real shape's bound never clips at its own tile boundary.
    private const float InstanceRadiusUnitScale = 0.9f;
    // The companion root's own travelling bound margin — added past the creation's reach so the root slot's identity
    // shape emission (currently none — the root carries no geometry of its own, only the group anchor) never clips.
    private const float RootBoundMargin = 0.25f;

    private readonly CompanionRoster m_roster;
    private readonly int m_slotBase;
    // The last-packed per-shape world transform, indexed (companionIndex * ShapeSlotCapacity) + shapeIndex — a
    // small side cache alongside the real dynamic-transform buffer so a caller (a companion's diegetic camera-feed
    // anchor — see CompanionEmitter.TryGetShapeTransform) can read a shape's live pose without holding its
    // own copy of the shared per-frame transforms buffer. Mirrors what it just wrote into `transforms`, one frame
    // stale by construction (read before the NEXT PackTransforms call, exactly like every other diegetic-feed anchor).
    private readonly DynamicTransform[] m_lastShapeTransforms = new DynamicTransform[(CompanionState.MaxCompanions * ShapeSlotCapacity)];

    /// <summary>Initializes the renderer over a roster at its dynamic-transform slot base.</summary>
    /// <param name="roster">The live companion roster.</param>
    /// <param name="slotBase">The pool's first dynamic-transform slot: companion i's ROOT rides
    /// <paramref name="slotBase"/> + i * <see cref="SlotsPerCompanion"/>, and its shape j rides that root slot + 1 + j.</param>
    public CompanionRenderer(CompanionRoster roster, int slotBase) {
        ArgumentNullException.ThrowIfNull(roster);

        m_roster = roster;
        m_slotBase = slotBase;
    }

    /// <summary>How many dynamic-transform slots ONE companion reserves: its root + its full shape-slot pool.</summary>
    public static int SlotsPerCompanion => (1 + ShapeSlotCapacity);

    /// <summary>How many dynamic-transform slots the WHOLE companion pool occupies — the frame source's slot-base
    /// arithmetic consumes this directly (mirrors <see cref="CreatorSceneRenderer.DynamicSlotCount"/>).</summary>
    public static int DynamicSlotCount => (CompanionState.MaxCompanions * SlotsPerCompanion);

    /// <summary>Emits the companion pool into the program under construction: one root + shape-slot pool per
    /// reserved companion slot (<see cref="CompanionState.MaxCompanions"/> of them, regardless of how many are
    /// actually loaded), a fresh palette per companion (so each creation's authored materials survive intact), and
    /// the robot archetype's face slab when a companion is both screen-faced AND has a granted screen index. With
    /// <paramref name="probeWorstCase"/> the emission takes its LARGEST possible form: every companion slot filled,
    /// every shape slot occupied, and (to size the screen-surface-adjacent word count identically to a real
    /// screen-faced companion) a face slab emitted at a placeholder index — measured once at construction, never
    /// rendered.</summary>
    /// <param name="builder">The program builder (the room + creator content is already emitted).</param>
    /// <param name="probeWorstCase">Emit the worst-case form for capacity measurement (never rendered).</param>
    /// <param name="faceSlotResolver">Resolves a screen-faced companion's ledger-granted screen-surface slot this
    /// pass, or -1 when the <see cref="Overworld.ScreenSlotLedger"/> granted it none (or it declares no face). The
    /// ledger is the SOLE owner of that resolved slot — the renderer reads it through here rather than off a mirror
    /// field on <see cref="CompanionState"/>. Null (or omitted, e.g. the worst-case probe) means no face slab emits
    /// for any real companion (the probe emits its own placeholder-index slab unconditionally instead).</param>
    public void EmitCompanions(SdfProgramBuilder builder, bool probeWorstCase = false, Func<CompanionState, int>? faceSlotResolver = null) {
        ArgumentNullException.ThrowIfNull(builder);

        var companions = m_roster.Companions;

        for (var companionIndex = 0; (companionIndex < CompanionState.MaxCompanions); companionIndex++) {
            var companion = ((companionIndex < companions.Count) ? companions[companionIndex] : null);
            var rootSlot = (m_slotBase + (companionIndex * SlotsPerCompanion));
            // The ledger-granted face slot (SOLE owner: the screen-slot ledger), resolved by the host per pass — -1
            // for an unloaded slot, a companion with no face, or one the ledger seated nowhere this pass.
            var faceSlot = ((companion is not null) ? (faceSlotResolver?.Invoke(arg: companion) ?? -1) : -1);

            EmitOneCompanion(builder: builder, companion: companion, faceSlot: faceSlot, probeWorstCase: probeWorstCase, rootSlot: rootSlot);
        }
    }

    /// <summary>Packs the pool's per-frame transforms: each loaded companion's root rides its live wander
    /// position/orientation and each of its shapes sits at the CURRENT timeline frame's transform (composed
    /// companionRoot ∘ per-shape frame transform — the IK'd/authored <see cref="CreationDocument.Frames"/> are plain
    /// per-shape transforms, so this is a REPLAY, never a re-solve); an unused companion slot (and every shape slot
    /// past a loaded companion's own shape count) hides below the floor, exactly like the creator pool's unused-slot
    /// story.</summary>
    /// <param name="transforms">The unified dynamic-transform buffer (the pool writes its own slot range).</param>
    /// <param name="hiddenPosition">Where hidden slots park (far below the floor).</param>
    public void PackTransforms(Span<DynamicTransform> transforms, Vector3 hiddenPosition) {
        var companions = m_roster.Companions;

        for (var companionIndex = 0; (companionIndex < CompanionState.MaxCompanions); companionIndex++) {
            var companion = ((companionIndex < companions.Count) ? companions[companionIndex] : null);
            var rootSlot = (m_slotBase + (companionIndex * SlotsPerCompanion));

            if (companion is null) {
                transforms[rootSlot] = new DynamicTransform(Orientation: Quaternion.Identity, Position: hiddenPosition);

                for (var shapeIndex = 0; (shapeIndex < ShapeSlotCapacity); shapeIndex++) {
                    var hidden = new DynamicTransform(Orientation: Quaternion.Identity, Position: hiddenPosition);

                    transforms[((rootSlot + 1) + shapeIndex)] = hidden;
                    m_lastShapeTransforms[((companionIndex * ShapeSlotCapacity) + shapeIndex)] = hidden;
                }

                continue;
            }

            transforms[rootSlot] = new DynamicTransform(Orientation: companion.Rotation, Position: companion.Position);

            var document = companion.Document;
            var shapes = (document.Shapes ?? []);
            // The two frames the render clock straddles, plus how far between them it is — the companion INTERPOLATES
            // (lerp position, slerp rotation) rather than hard-cutting between snapshots, so an 8-frame swim or a
            // 4-frame walk reads as one continuous motion instead of a flip-book (see CompanionState.FrameBlend).
            var currentFramePoses = CurrentFramePoses(document: document, frameCursor: companion.FrameCursor);
            var nextFramePoses = CurrentFramePoses(document: document, frameCursor: companion.NextFrameCursor);
            var blend = companion.FrameBlend;

            for (var shapeIndex = 0; (shapeIndex < ShapeSlotCapacity); shapeIndex++) {
                var slot = ((rootSlot + 1) + shapeIndex);

                if (shapeIndex >= shapes.Count) {
                    var hidden = new DynamicTransform(Orientation: Quaternion.Identity, Position: hiddenPosition);

                    transforms[slot] = hidden;
                    m_lastShapeTransforms[((companionIndex * ShapeSlotCapacity) + shapeIndex)] = hidden;

                    continue;
                }

                var shape = shapes[shapeIndex];

                var (position, rotation) = ResolvePose(currentFramePoses: currentFramePoses, nextFramePoses: nextFramePoses, blend: blend, shape: shape);
                // Composed companionRoot ∘ per-shape frame transform: the shape's authored/posed position and
                // orientation are expressed in the companion's OWN local frame, so rotating the root turns the whole
                // creation together while each shape still replays its individual frame pose within it.
                var worldPosition = (companion.Position + Vector3.Transform(value: position, rotation: companion.Rotation));
                var worldRotation = Quaternion.Normalize(value: (companion.Rotation * rotation));
                var packed = new DynamicTransform(Orientation: worldRotation, Position: worldPosition);

                transforms[slot] = packed;
                m_lastShapeTransforms[((companionIndex * ShapeSlotCapacity) + shapeIndex)] = packed;
            }
        }
    }

    /// <summary>The last <see cref="PackTransforms"/> call's world transform for companion
    /// <paramref name="companionIndex"/>'s shape <paramref name="shapeIndex"/> (see
    /// <see cref="CompanionEmitter.TryGetShapeTransform"/>'s remarks on the staleness this carries).
    /// Returns <see langword="false"/> for an out-of-range index or before the first pack.</summary>
    public bool TryGetLastShapeTransform(int companionIndex, int shapeIndex, out DynamicTransform transform) {
        if ((companionIndex < 0) || (companionIndex >= CompanionState.MaxCompanions) || (shapeIndex < 0) || (shapeIndex >= ShapeSlotCapacity)) {
            transform = default;

            return false;
        }

        transform = m_lastShapeTransforms[((companionIndex * ShapeSlotCapacity) + shapeIndex)];

        return true;
    }

    private void EmitOneCompanion(SdfProgramBuilder builder, CompanionState? companion, int faceSlot, bool probeWorstCase, int rootSlot) {
        var document = companion?.Document;
        var shapes = (document?.Shapes ?? []);
        var paletteIds = EmitPalette(builder: builder, document: document);

        // The companion's ROOT slot carries no geometry of its own — it exists purely as the group instance's
        // travelling anchor (BeginInstanceDynamic below) and the shapes' composition frame (PackTransforms). A
        // reach-scaled bound on the root itself is still declared so the beam prepass has a cheap early reject for a
        // companion with zero shapes (an edge case the probe never hits, but a real partial load could).
        var reach = ((probeWorstCase || (companion is null)) ? CreatorSceneRenderer.MaxShapeReach : companion.Reach);

        // Pass 1 — UNGROUPED shapes and unused slots: one tight dynamic instance per slot, anchored on the ROOT slot
        // (not its own — TransformDynamic composes the shape's own per-shape pose ONTO the root's live transform via
        // PackTransforms' pre-composition, so the instruction stream itself just references rootSlot's bound and the
        // shape's OWN slot for its point transform). Mirrors CreatorSceneRenderer's ungrouped pass exactly.
        for (var index = 0; (index < ShapeSlotCapacity); index++) {
            var placed = ((index < shapes.Count) ? shapes[index] : (ShapeDocument?)null);

            if (placed is { Group: not null and not 0 }) {
                continue; // pass 2 — the shape emits inside its group's instance.
            }

            // Resolve the whole per-shape modifier envelope in ONE call (ResolveShape) rather than a `??` per field
            // inline here — keeps EmitOneCompanion's own decision-point count flat as the envelope grows (CA1502).
            var resolved = ResolveShape(placed: placed);
            var slot = ((rootSlot + 1) + index);
            var material = paletteIds[((placed?.Material ?? 0) % paletteIds.Length)];
            // A shape slot past this companion's live shape count — and EVERY slot of an unused companion slot — is a
            // hidden placeholder; PARK it (Active=false) so the beam cull skips it instead of testing MaxCompanions x 64
            // hidden spheres per tile every frame. The reserved slots still exist (buffers unchanged); the probe stays
            // fully active so it still measures the true worst case.
            var active = (probeWorstCase || (placed is not null));

            _ = builder.BeginInstanceDynamic(slot: slot, boundOffset: Vector3.Zero, boundRadius: BoundRadius(scale: resolved.Scale), active: active);
            EmitShape(
                bend: resolved.Bend,
                builder: builder,
                dilate: resolved.Dilate,
                material: material,
                mirror: resolved.Mirror,
                onion: resolved.Onion,
                probeWorstCase: probeWorstCase,
                scale: resolved.Scale,
                slot: slot,
                twist: resolved.Twist,
                type: resolved.Type
            );
            _ = builder.EndInstance();
        }

        // Pass 2 — composition GROUPS, in first-appearance order: each group is ONE DYNAMIC instance anchored on the
        // ROOT slot, bounded by the creation's OWN reach × scale (the travelling bound — see the type's remarks),
        // its members emitted in document order with their authored blend/smooth.
        Span<int> emittedGroups = stackalloc int[ShapeSlotCapacity];
        var emittedCount = 0;

        for (var index = 0; (index < shapes.Count); index++) {
            var groupId = (shapes[index].Group ?? 0);

            if ((groupId == 0) || emittedGroups[..emittedCount].Contains(value: groupId)) {
                continue;
            }

            emittedGroups[emittedCount++] = groupId;
            EmitGroup(builder: builder, fromIndex: index, groupId: groupId, paletteIds: paletteIds, probeWorstCase: probeWorstCase, reach: reach, rootSlot: rootSlot, shapes: shapes);
        }

        // The robot archetype's face slab: emitted only when the host has marked this companion as screen-faced AND
        // a screen index was granted this pass (the ledger's per-frame claim), OR unconditionally under the probe
        // (the worst case must cover the largest word count a real screen-faced companion could ever produce). No
        // index (never marked, or marked but not yet granted a slot) degrades to nothing here — same "no index, no
        // diegetic screen" story cabinets/the preview easel follow; the renderer never draws a flat stand-in shape
        // for a face that has nowhere to shine (the companion's own body shapes already read fine without one).
        if (probeWorstCase || ((companion is not null) && (faceSlot >= 0))) {
            var faceMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.10f, y: 0.11f, z: 0.13f)));
            var screenIndex = (probeWorstCase ? 0 : faceSlot);
            var faceHalfExtents = new Vector3(x: 0.34f, y: 0.30f, z: 0.05f);
            var faceLocalOffset = new Vector3(x: 0f, y: 0.2f, z: 0.4f);

            _ = builder.BeginInstanceDynamic(slot: rootSlot, boundOffset: faceLocalOffset, boundRadius: (faceHalfExtents.Length() + 0.1f));
            _ = builder.ResetPoint().TransformDynamic(slot: rootSlot).Translate(offset: faceLocalOffset).ScreenSlab(
                halfExtents: faceHalfExtents,
                round: 0.02f,
                screenIndex: screenIndex,
                worldOrigin: faceLocalOffset,
                worldRight: Vector3.UnitX,
                worldUp: Vector3.UnitY
            );
            _ = builder.EndInstance();

            if (faceMaterial < 0) {
                // Unreachable: AddMaterial never returns a negative index. Keeps the analyzer's unused-result rule
                // happy without discarding the material outright (a future flat-degrade path may reference it).
                throw new InvalidOperationException(message: "AddMaterial returned an invalid index.");
            }
        }
    }

    // Emits ONE of a companion's composition groups as a single dynamic instance anchored on the ROOT slot: the
    // members in document order with their authored blend/smooth, wrapped in a PushField(Union)/PopField field scope
    // when the group needs one (GroupNeedsScope) so each member composes against its GROUP-MATES, not the whole scene
    // — the Intersection-wipe fix (mirrors CreatorSceneRenderer.EmitGroup). A pure-Union, no-onion group takes no
    // scope and emits flat (byte-identical for a union-only creation).
    private static void EmitGroup(SdfProgramBuilder builder, IReadOnlyList<ShapeDocument> shapes, int groupId, int fromIndex, int rootSlot, int[] paletteIds, bool probeWorstCase, float reach) {
        var groupNeedsScope = GroupNeedsScope(fromIndex: fromIndex, groupId: groupId, shapes: shapes);

        _ = builder.BeginInstanceDynamic(slot: rootSlot, boundOffset: Vector3.Zero, boundRadius: (reach + RootBoundMargin));

        if (groupNeedsScope) {
            _ = builder.PushField(compose: SdfBlendOp.Union);
        }

        for (var member = fromIndex; (member < shapes.Count); member++) {
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
                scale: shape.Scale,
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

    // Emits one companion's palette: a real companion's OWN authored palette (so its creation reads with its
    // authored colors), or a placeholder single-entry table for an unused/probe slot — mirrors
    // CreatorSceneRenderer's "constant material count across rebuilds" discipline, just scoped per companion instead
    // of once for the whole pool.
    private static int[] EmitPalette(SdfProgramBuilder builder, CreationDocument? document) {
        var palette = (document?.Palette ?? []);
        var count = Math.Max(val1: palette.Count, val2: CreatorScene.PaletteSize);
        var ids = new int[count];

        for (var index = 0; (index < count); index++) {
            var entry = ((index < palette.Count) ? palette[index] : (PaletteEntryDocument?)null);
            var material = new SdfMaterial(Albedo: (entry?.Albedo ?? new Vector3(x: 0.6f, y: 0.6f, z: 0.65f)));

            ids[index] = builder.AddMaterial(material: (material with {
                Emissive = (entry?.Emissive ?? material.Emissive),
                Shininess = (entry?.Shininess ?? material.Shininess),
                Specular = (entry?.Specular ?? material.Specular),
            }));
        }

        return ids;
    }

    // Memoized per-frame shape-id → pose indices, keyed by (document, frameCursor). PackTransforms calls
    // CurrentFramePoses twice per loaded companion every produced frame (current + next cursor for the interpolation
    // blend), and a cursor changes only every few frames, so without this each call rebuilt a fresh Dictionary from
    // frame.Transforms — up to MaxCompanions × 2 dictionary allocations per frame. The index is pure derivation of an
    // immutable document, so it is cached indefinitely (a document is replaced wholesale on reload, not mutated).
    private readonly Dictionary<(CreationDocument, int), IReadOnlyDictionary<int, FrameTransformDocument>> m_framePoseCache = [];

    // Resolves the current timeline frame's per-shape poses (keyed by shape id), or null at frame cursor 0 (the rest
    // pose — the shape's own authored Position/Rotation/Scale, no override). Memoized per (document, frameCursor).
    private IReadOnlyDictionary<int, FrameTransformDocument>? CurrentFramePoses(CreationDocument document, int frameCursor) {
        if ((frameCursor <= 0) || (document.Frames is not { Count: > 0 } frames) || (frameCursor > frames.Count)) {
            return null;
        }

        var key = (document, frameCursor);

        if (m_framePoseCache.TryGetValue(key: key, value: out var cached)) {
            return cached;
        }

        var frame = frames[(frameCursor - 1)];
        var poses = new Dictionary<int, FrameTransformDocument>(capacity: frame.Transforms.Count);

        foreach (var pose in frame.Transforms) {
            poses[pose.Id] = pose;
        }

        // Bound the cache so a long session that reloads companions many times cannot pin an unbounded set of dead
        // document keys: a live roster's working set is tiny (MaxCompanions documents × their frame counts), so a
        // generous cap that a live set never approaches simply drops stale entries wholesale.
        if (m_framePoseCache.Count >= (CompanionState.MaxCompanions * 256)) {
            m_framePoseCache.Clear();
        }

        m_framePoseCache[key] = poses;

        return poses;
    }

    // Resolves a shape's pose for THIS render instant: the shape's pose in the current frame lerped/slerped toward its
    // pose in the next frame by the blend factor. A frame that doesn't override a shape falls back to the shape's own
    // authored pose (so a shape absent from the timeline holds its rest transform on both sides of the blend).
    private static (Vector3 Position, Quaternion Rotation) ResolvePose(IReadOnlyDictionary<int, FrameTransformDocument>? currentFramePoses, IReadOnlyDictionary<int, FrameTransformDocument>? nextFramePoses, float blend, ShapeDocument shape) {
        var (currentPosition, currentRotation) = FramePose(framePoses: currentFramePoses, shape: shape);

        if (blend <= 0f) {
            return (currentPosition, currentRotation);
        }

        var (nextPosition, nextRotation) = FramePose(framePoses: nextFramePoses, shape: shape);

        return (
            Vector3.Lerp(value1: currentPosition, value2: nextPosition, amount: blend),
            Quaternion.Slerp(quaternion1: currentRotation, quaternion2: nextRotation, amount: blend)
        );
    }

    // One shape's pose in a single frame's snapshot (or its authored rest pose when the frame doesn't override it).
    private static (Vector3 Position, Quaternion Rotation) FramePose(IReadOnlyDictionary<int, FrameTransformDocument>? framePoses, ShapeDocument shape) {
        if ((framePoses is not null) && framePoses.TryGetValue(key: shape.Id, value: out var pose)) {
            return (pose.Position, pose.Rotation);
        }

        return (shape.Position, shape.Rotation);
    }

    // One shape's emission: ResetPoint + TransformDynamic + Scale + [mirror/twist/bend: POINT ops, before the shape,
    // in that order] + the primitive (blend/smooth ride the primitive instruction) + [dilate/onion: FIELD ops, AFTER
    // the shape, dilate then onion] — IDENTICAL op sequence to CreatorSceneRenderer.EmitShape (the SAME shared
    // canonical dimensions via AvatarDefinition, so a companion's shape is byte-for-byte the geometry the creator
    // previewed and the forge could bake). probeWorstCase emits ALL per-shape modifier ops unconditionally, matching
    // CreatorSceneRenderer's binding rule.
    //
    // SCOPING mirrors CreatorSceneRenderer.EmitShape exactly (see its remarks + the accumulator rule on SdfBlendOp
    // + the sdf-world skill): a non-group shape (Pass 1 — always plain Union) wraps its dilate/onion field ops in a
    // PushField(Union)/PopField scope so they apply to THIS shape, not the whole scene; a shape with neither field
    // op stays flat (byte-identical). A group member passes inGroupScope: true and emits flat — it already sits
    // inside its group's single PushField scope (EmitOneCompanion Pass 2), which the depth-1 cap forbids nesting.
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
            var scoped = AvatarDefinition.AppendPrimitive(blend: SdfBlendOp.Union, chain: chain.PushField(compose: blend, smooth: smooth), material: material, smooth: 0f, type: type);

            if (wantsDilate) {
                scoped = scoped.Dilate(radius: (probeWorstCase ? CreatorScene.MaxDilate : dilate));
            }

            if (wantsOnion) {
                scoped = scoped.Onion(thickness: (probeWorstCase ? CreatorScene.MaxOnion : onion));
            }

            _ = scoped.PopField();

            return;
        }

        var afterShape = AvatarDefinition.AppendPrimitive(blend: blend, chain: chain, material: material, smooth: smooth, type: type);

        if (wantsDilate) {
            afterShape = afterShape.Dilate(radius: (probeWorstCase ? CreatorScene.MaxDilate : dilate));
        }

        if (wantsOnion) {
            _ = afterShape.Onion(thickness: (probeWorstCase ? CreatorScene.MaxOnion : onion));
        }
    }

    // Resolves an absent/present shape slot's per-modifier envelope in ONE call — factored out (rather than a `??`
    // per field inline at the Pass 1 call site) so EmitOneCompanion's own decision-point count stays flat as the
    // envelope grows (CA1502).
    private static ResolvedShape ResolveShape(ShapeDocument? placed) {
        return new ResolvedShape(
            Bend: (placed?.Bend ?? 0f),
            Dilate: (placed?.Dilate ?? 0f),
            Mirror: (placed?.Mirror ?? false),
            Onion: (placed?.Onion ?? 0f),
            Scale: (placed?.Scale ?? Vector3.One),
            Twist: (placed?.Twist ?? 0f),
            Type: (placed?.Type ?? AvatarPrimitive.Sphere)
        );
    }

    private readonly record struct ResolvedShape(AvatarPrimitive Type, Vector3 Scale, bool Mirror, float Twist, float Bend, float Dilate, float Onion);

    // Whether a companion's composition group must emit inside a PushField/PopField field scope — mirrors
    // CreatorSceneRenderer.GroupNeedsScope: true when any member carries a non-Union blend (the Intersection family
    // wipes the whole accumulated scene otherwise) or a dilate/onion field op. A pure-Union, no-field-op group stays
    // flat, so a union-only creation loads byte-identically. See the accumulator rule on SdfBlendOp + the sdf-world
    // skill.
    private static bool GroupNeedsScope(IReadOnlyList<ShapeDocument> shapes, int groupId, int fromIndex) {
        for (var member = fromIndex; (member < shapes.Count); member++) {
            var shape = shapes[member];

            if (((shape.Group ?? 0) == groupId) && (((shape.Blend ?? SdfBlendOp.Union) != SdfBlendOp.Union) || ((shape.Onion ?? 0f) != 0f) || ((shape.Dilate ?? 0f) != 0f))) {
                return true;
            }
        }

        return false;
    }

    // A scaled shape's dynamic instance bound: the unit-scale bound grown by the largest scale component — mirrors
    // CreatorSceneRenderer.BoundRadius exactly.
    private static float BoundRadius(Vector3 scale) =>
        (InstanceRadiusUnitScale * MathF.Max(x: scale.X, y: MathF.Max(x: scale.Y, y: scale.Z)));
}
