using System.Numerics;
using Puck.Demo.Forge;
using Puck.SdfVm;

namespace Puck.Demo.Creator;

/// <summary>
/// Emits the creator scene into the world's SDF program and packs its per-frame dynamic transforms — the render half
/// of the editor, extracted from the overworld frame source. The pool is emitted on EVERY rebuild with a constant
/// slot count (1 ghost + <see cref="CreatorScene.Capacity"/> placed slots; an unused slot draws a default sphere
/// hidden below the floor), and the frame source sizes the engine's program/instance buffers against
/// <see cref="EmitPool"/>'s WORST-CASE form (the probe) — so any authored rebuild fits the once-sized buffers by
/// construction. Rebuilds may vary in size freely below the probed ceiling, and <c>UploadProgram</c> throws loudly if a
/// future emission change forgets to grow the probe with it.
/// </summary>
public sealed class CreatorSceneRenderer {
    /// <summary>The engine screen-surface slot the preview easel BORROWS while creator mode is active. The surface
    /// table caps at eight (<c>SdfProgramBuilder.MaxScreenSurfaces</c>); cabinets own slots 0–3, and slot 3 is the
    /// easel's SETTLED borrow slot: while editing, cabinet 3's diegetic screen degrades to its lit flat material and
    /// this slab samples the bake preview instead; exit restores it. The suppression (frame source's stand emission)
    /// and the provider mux (render node) both gate on the SAME CreatorScene.Active flag, and the mode toggle
    /// rebuilds the program, so the surface table and the source providers can never disagree mid-frame.</summary>
    public const int PreviewScreenIndex = 3;

    // A generous per-shape bound AT UNIT SCALE (each primitive's worst-case reach is well under this) — a fat bound
    // only costs a rare extra evaluation, and an unused slot's hidden instance culls to nothing. A scaled shape
    // multiplies this by its largest scale component so the beam prepass never clips a shape the player grew.
    private const float InstanceRadiusUnitScale = 0.9f;
    // The preview easel's screen keeps the brick panel's native 10:9 aspect so baked pixels sample unstretched.
    private const float PreviewAspect = (160f / 144f);
    // The ghost's preview accent — a bright cyan so the not-yet-placed shape reads as a hologram, never authored art.
    private static readonly Vector3 GhostAlbedo = new(x: 0.35f, y: 0.92f, z: 1.0f);
    // The chain-goal marker's accent — a warm amber, distinct from the ghost's cyan, so a goal never reads as an
    // about-to-be-placed shape.
    private static readonly Vector3 GoalAlbedo = new(x: 1.0f, y: 0.65f, z: 0.15f);
    // The goal marker's fixed radius (a small sphere — a HUD-like handle, not authored geometry).
    private const float GoalMarkerRadius = 0.12f;
    // The canonical "nothing placed here" shape — Pass 1 resolves an absent slot against this ONE default (every
    // per-shape modifier at its off value) instead of a `??` per field, so EmitPool's own decision-point count
    // stays flat as the modifier envelope grows (see EmitPool's CA1502 ceiling).
    private static readonly CreatorShapeState s_emptySlot = new(
        Blend: SdfBlendOp.Union,
        GroupId: 0,
        Id: -1,
        MaterialIndex: 0,
        Name: null,
        Position: Vector3.Zero,
        Rotation: Quaternion.Identity,
        Scale: Vector3.One,
        Smooth: 0f,
        Type: AvatarPrimitive.Sphere
    );
    private readonly CreatorScene m_scene;
    private readonly int m_slotBase;
    private readonly int m_goalSlotBase;

    /// <summary>Initializes the renderer over a scene at its dynamic-transform slot base.</summary>
    /// <param name="scene">The authored scene to emit.</param>
    /// <param name="slotBase">The pool's first dynamic-transform slot: the GHOST rides <paramref name="slotBase"/>,
    /// and placed shape i rides <paramref name="slotBase"/> + 1 + i. The reserved GOAL-marker slots (see
    /// <see cref="GoalSlotCount"/>) immediately follow the shape pool.</param>
    public CreatorSceneRenderer(CreatorScene scene, int slotBase) {
        ArgumentNullException.ThrowIfNull(scene);

        m_scene = scene;
        m_slotBase = slotBase;
        m_goalSlotBase = ((slotBase + 1) + CreatorScene.Capacity);
    }

    /// <summary>How many reserved dynamic-transform slots the rig's goal markers occupy (a fixed budget —
    /// <see cref="CreatorScene.MaxChains"/> — so the probe stays bounded regardless of how many chains a player
    /// actually defines).</summary>
    public static int GoalSlotCount => CreatorScene.MaxChains;

    /// <summary>How many dynamic-transform slots the pool occupies (the ghost + every placed slot + the reserved
    /// goal-marker slots).</summary>
    public static int DynamicSlotCount => ((1 + CreatorScene.Capacity) + GoalSlotCount);

    /// <summary>The worst-case reach of a maximally-grown shape from its center — the margin a composition group's
    /// workbench bound adds past the region's own extent.</summary>
    public static float MaxShapeReach => (InstanceRadiusUnitScale * CreatorScene.MaxScale);

    /// <summary>Emits the creator pool into the program under construction: the palette + ghost materials (constant
    /// count), the ghost's instance, and one instance per placed-shape slot. With <paramref name="probeWorstCase"/>
    /// the emission takes its LARGEST possible form — every slot carries the reserved per-shape modifier envelope
    /// (mirror + twist + bend point ops AND the dilate + onion field ops WRAPPED IN ONE PushField/PopField scope —
    /// seven extra words) — which is what the frame source measures at construction to size the engine's
    /// program-word and instance-count floors. Growing this emission (a new per-shape op) MUST grow the probe in the
    /// same change.</summary>
    /// <param name="builder">The program builder (the room content is already emitted).</param>
    /// <param name="probeWorstCase">Emit the worst-case form for capacity measurement (never rendered).</param>
    /// <param name="suppressEasel">Drop the preview easel (the post + bake-preview screen slab) — the studio review
    /// shows the creation alone, not the authoring scaffold. Probe-overridden (the worst case always carries it).</param>
    /// <param name="suppressAdornments">Drop EVERY creator-mode adornment — the placement GHOST, the RIG's goal
    /// markers, and the selection highlight (the selected shape falls back to its plain palette material) — so a studio
    /// review shows the CREATURE alone, never a floating cursor/marker photobombing the shot. The placed shapes (the
    /// creation itself) are unaffected. Suppression is by EMISSION only (the scene state is never mutated, so a
    /// placement's sticky ghost/selection fields stay intact for interactive use). Probe-overridden: the worst-case
    /// probe always emits the ghost + goal markers, so the reserved buffer ceiling is unchanged and a studio program is
    /// a strict subset.</param>
    public void EmitPool(SdfProgramBuilder builder, bool probeWorstCase = false, bool suppressEasel = false, bool suppressAdornments = false) {
        ArgumentNullException.ThrowIfNull(builder);

        // The palette is added to EVERY program (constant material count across rebuilds); shapes reference entries
        // by palette slot. The ghost's accent and the selection highlight (the selected shape's palette entry with
        // an emissive lift; a dummy when nothing is selected) ride alongside — three constants past the palette.
        var paletteIds = new int[CreatorScene.PaletteSize];

        for (var index = 0; (index < CreatorScene.PaletteSize); index++) {
            paletteIds[index] = builder.AddMaterial(material: m_scene.Palette[index]);
        }

        var ghostMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: GhostAlbedo));
        // Studio suppresses the selection HIGHLIGHT (an adornment): the material table stays constant (the entry is
        // still added — a strict-subset program never removes a material), but no shape references it, so the selected
        // creature shape reads in its true palette color instead of an emissive-lifted marker.
        var selectedIndex = ((!suppressAdornments && (m_scene.SelectedShape is { } selectedShape)) ? m_scene.SelectionIndex : -1);
        var highlightSource = m_scene.Palette[((selectedIndex >= 0) ? m_scene.Shapes[selectedIndex].MaterialIndex : 0)];
        var highlightMaterial = builder.AddMaterial(material: (highlightSource with { Emissive = (highlightSource.Emissive + 0.6f) }));
        var backdropMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.24f, y: 0.25f, z: 0.28f)));

        // The PREVIEW EASEL: a post + diegetic screen slab at the workbench's +X edge, facing the default authoring
        // camera — the live bake preview renders on it (the sculpt-with-the-quantized-result-in-view loop). Emitted
        // only while the mode is up (the probe always carries it); its slab borrows PreviewScreenIndex (see the
        // constant's remarks for the cabinet-3 handshake). A 0 source handle falls back to the dark flat material,
        // so the easel reads as a powered-off panel until the bake pipeline publishes its first image.
        if (probeWorstCase || (m_scene.Active && !suppressEasel)) {
            var region = m_scene.Workbench;
            // Just INSIDE the +X edge: the default authoring orbit (head-on from +Z at ~6.5 units) spans roughly a
            // ±33° horizontal half-angle, so an easel past the edge sits out of frame — tucked in at edge − 0.7 it
            // reads at ~26° off-axis, screen face toward the camera.
            var easelX = ((region.Center.X + region.HalfExtent) - 0.7f);
            var postCenter = new Vector3(x: easelX, y: (region.Center.Y + 0.75f), z: region.Center.Z);
            var slabHalfExtents = new Vector3(x: 0.9f, y: (0.9f / PreviewAspect), z: 0.06f);
            var slabCenter = new Vector3(x: easelX, y: ((region.Center.Y + 1.5f) + slabHalfExtents.Y), z: region.Center.Z);
            var easelMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.18f, y: 0.19f, z: 0.22f)));

            _ = builder.BeginInstance(boundCenter: slabCenter, boundRadius: 3.2f);
            _ = builder.ResetPoint().Translate(offset: postCenter).Box(halfExtents: new Vector3(x: 0.16f, y: 0.75f, z: 0.16f), round: 0.03f, material: easelMaterial);
            _ = builder.ResetPoint().Translate(offset: slabCenter).ScreenSlab(
                halfExtents: slabHalfExtents,
                round: 0.03f,
                screenIndex: PreviewScreenIndex,
                worldOrigin: (slabCenter + new Vector3(x: 0f, y: 0f, z: slabHalfExtents.Z)),
                worldRight: Vector3.UnitX,
                worldUp: Vector3.UnitY
            );
            _ = builder.EndInstance();
        }

        // The SPRITE-intent backdrop: a neutral matte slab across the workbench's -Z edge, so authored silhouettes
        // read against the same flat field the bake's background sampling sees. Emitted only while authoring sprite
        // art (the probe always carries it — the worst case includes every optional emission).
        if (probeWorstCase || (m_scene.Active && (m_scene.Intent == CreatorIntent.Sprite))) {
            var workbenchRegion = m_scene.Workbench;
            var backdropCenter = new Vector3(
                x: workbenchRegion.Center.X,
                y: (0.5f * (workbenchRegion.MinY + workbenchRegion.MaxY)),
                z: ((workbenchRegion.Center.Z - workbenchRegion.HalfExtent) - 0.4f)
            );
            var backdropHalfExtents = new Vector3(x: (workbenchRegion.HalfExtent + 1f), y: ((0.5f * (workbenchRegion.MaxY - workbenchRegion.MinY)) + 1f), z: 0.06f);

            _ = builder.BeginInstance(boundCenter: backdropCenter, boundRadius: (backdropHalfExtents.Length() + 0.5f));
            _ = builder.ResetPoint().Translate(offset: backdropCenter).Box(halfExtents: backdropHalfExtents, round: 0.04f, material: backdropMaterial);
            _ = builder.EndInstance();
        }

        // The ghost: one dynamic instance on the pool's first slot, previewing the primitive the next place commits.
        // Studio suppresses it (an adornment — the placement cursor that would photobomb a review shot). The probe
        // still emits it (probeWorstCase wins), so the reserved buffer ceiling is unchanged and studio is a subset.
        // The ghost + goal markers are creator-mode adornments; when the mode is inactive their hidden placeholders are
        // PARKED (Active=false) so the beam cull skips them, exactly like the unused shape slots below. The probe keeps
        // them active (worst case), and an active session emits them live so the placement cursor/rig markers render.
        var adornmentsActive = (probeWorstCase || m_scene.Active);

        if (probeWorstCase || !suppressAdornments) {
            _ = builder.BeginInstanceDynamic(slot: m_slotBase, boundOffset: Vector3.Zero, boundRadius: BoundRadius(scale: m_scene.GhostScale), active: adornmentsActive);
            EmitShape(
                bend: m_scene.GhostBend,
                builder: builder,
                dilate: m_scene.GhostDilate,
                material: ghostMaterial,
                mirror: m_scene.GhostMirror,
                onion: m_scene.GhostOnion,
                probeWorstCase: probeWorstCase,
                scale: m_scene.GhostScale,
                slot: m_slotBase,
                twist: m_scene.GhostTwist,
                type: m_scene.GhostType
            );
            _ = builder.EndInstance();
        }

        // THE RIG's goal markers: one small ghost-material sphere per reserved goal slot (a HUD-like handle, never
        // authored geometry). Emitted for EVERY reserved slot every rebuild (probe or not) — same "constant slot
        // count, an unused slot hides below the floor" shape as the shape pool, so the goal-marker emission is a
        // NEW optional addition that still joins the probe's worst case (every slot present, none skipped). Studio
        // suppresses them too (adornments — amber markers that would photobomb a review); the probe still emits them.
        var goalMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: GoalAlbedo, Emissive: 0.4f));

        if (probeWorstCase || !suppressAdornments) {
            for (var index = 0; (index < GoalSlotCount); index++) {
                var slot = (m_goalSlotBase + index);

                _ = builder.BeginInstanceDynamic(slot: slot, boundOffset: Vector3.Zero, boundRadius: GoalMarkerRadius, active: adornmentsActive);
                _ = builder.ResetPoint().TransformDynamic(slot: slot).Sphere(material: goalMaterial, radius: GoalMarkerRadius);
                _ = builder.EndInstance();
            }
        }

        // Pass 1 — UNGROUPED shapes and unused slots: one tight dynamic instance per slot (cheap, cullable; every
        // slot emits every rebuild so the buffers cover the full pool from frame 0). Ungrouped shapes are plain
        // Union by construction (the scene coerces any non-Union blend into a group-of-one), so a per-shape
        // instance never blends across its own boundary.
        var shapes = m_scene.Shapes;

        for (var index = 0; (index < CreatorScene.Capacity); index++) {
            var placed = ((index < shapes.Count) ? shapes[index] : (CreatorShapeState?)null);

            if (placed is { GroupId: not 0 }) {
                continue; // pass 2 — the shape emits inside its group's instance.
            }

            // Resolve the slot's state ONCE against the shared "nothing placed here" default (s_emptySlot already
            // carries every per-shape modifier at its off value) rather than a `?? default` per field — keeps this
            // loop's own decision-point count flat as the modifier envelope grows (CA1502 on EmitPool).
            var resolved = (placed ?? s_emptySlot);
            var slot = ((m_slotBase + 1) + index);
            var material = ((index == selectedIndex) ? highlightMaterial : paletteIds[(placed?.MaterialIndex ?? (index % CreatorScene.PaletteSize))]);
            // An unused slot's hidden placeholder is PARKED (Active=false) so the beam cull skips it with one branch
            // instead of testing 64 hidden spheres per tile every frame — the reserved slot still exists (buffers
            // unchanged), it just costs nothing. The probe stays fully active so it still measures the true worst case.
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

        // Pass 2 — composition GROUPS, in first-appearance order: each group is ONE STATIC instance bounded by the
        // whole workbench region, its members emitted in document order with their authored blend/smooth. The fat
        // static bound is the instance-cull contract made structural: members ride dynamic-transform slots but the
        // workbench clamp keeps them inside the bound, so a smooth blend never reaches across a maskable instance
        // boundary and never pops at a tile edge.
        var workbench = m_scene.Workbench;
        var groupBound = workbench.GroupBoundRadius(maxShapeReach: MaxShapeReach);
        Span<int> emittedGroups = stackalloc int[CreatorScene.Capacity];
        var emittedCount = 0;

        for (var index = 0; (index < shapes.Count); index++) {
            var groupId = shapes[index].GroupId;

            if ((groupId == 0) || emittedGroups[..emittedCount].Contains(value: groupId)) {
                continue;
            }

            emittedGroups[emittedCount++] = groupId;
            EmitGroup(builder: builder, fromIndex: index, groupBound: groupBound, groupId: groupId, highlightMaterial: highlightMaterial, paletteIds: paletteIds, probeWorstCase: probeWorstCase, selectedIndex: selectedIndex, shapes: shapes, workbenchMid: workbench.MidPoint);
        }
    }

    // Emits ONE composition group as a single static instance: the members in document order with their authored
    // blend/smooth, wrapped in a PushField(Union)/PopField field scope when the group needs one (GroupNeedsScope).
    // The scope is what makes a member's blend compose against its GROUP-MATES (the fresh FAR-seeded accumulator),
    // not against the whole workbench — so an Intersection member intersects its group instead of wiping the room
    // (the documented creator defect). Members pass inGroupScope: true and never nest their own scope (the depth-1
    // cap allows exactly the one the group opened). A pure-Union, no-onion group takes no scope and emits flat.
    private void EmitGroup(SdfProgramBuilder builder, IReadOnlyList<CreatorShapeState> shapes, int groupId, int fromIndex, int selectedIndex, int[] paletteIds, int highlightMaterial, bool probeWorstCase, Vector3 workbenchMid, float groupBound) {
        var groupNeedsScope = GroupNeedsScope(fromIndex: fromIndex, groupId: groupId, shapes: shapes);

        _ = builder.BeginInstance(boundCenter: workbenchMid, boundRadius: groupBound);

        if (groupNeedsScope) {
            _ = builder.PushField(compose: SdfBlendOp.Union);
        }

        for (var member = fromIndex; (member < shapes.Count); member++) {
            var shape = shapes[member];

            if (shape.GroupId != groupId) {
                continue;
            }

            EmitShape(
                bend: shape.Bend,
                blend: shape.Blend,
                builder: builder,
                dilate: shape.Dilate,
                inGroupScope: true,
                material: ((member == selectedIndex) ? highlightMaterial : paletteIds[shape.MaterialIndex]),
                mirror: shape.Mirror,
                onion: shape.Onion,
                probeWorstCase: probeWorstCase,
                scale: shape.Scale,
                slot: ((m_slotBase + 1) + member),
                smooth: shape.Smooth,
                twist: shape.Twist,
                type: shape.Type
            );
        }

        if (groupNeedsScope) {
            _ = builder.PopField();
        }

        _ = builder.EndInstance();
    }

    /// <summary>Packs the pool's per-frame transforms: the ghost rides its live position/orientation while the mode
    /// is active (hidden below the floor otherwise), each placed shape sits at its authored transform, and unused
    /// slots hide. The GEOMETRY each slot draws is baked into the program — here only the positions move.</summary>
    /// <param name="transforms">The unified dynamic-transform buffer (the pool writes its own slot range).</param>
    /// <param name="hiddenPosition">Where hidden slots park (far below the floor).</param>
    public void PackTransforms(Span<DynamicTransform> transforms, Vector3 hiddenPosition) {
        var active = m_scene.Active;

        transforms[m_slotBase] = new DynamicTransform(
            Orientation: (active ? m_scene.GhostRotation : Quaternion.Identity),
            Position: (active ? m_scene.GhostPosition : hiddenPosition)
        );

        var shapes = m_scene.Shapes;

        for (var index = 0; (index < CreatorScene.Capacity); index++) {
            var placed = ((index < shapes.Count) ? shapes[index] : (CreatorShapeState?)null);

            transforms[((m_slotBase + 1) + index)] = new DynamicTransform(
                Orientation: (placed?.Rotation ?? Quaternion.Identity),
                Position: (placed?.Position ?? hiddenPosition)
            );
        }

        // The RIG's goal markers: an active slot rides its chain's live goal; a slot past the defined chain count
        // (or the whole reserved budget while the mode is down) hides below the floor exactly like an unused shape
        // slot — never a special case for the beam prepass.
        var chains = m_scene.Chains;

        for (var index = 0; (index < GoalSlotCount); index++) {
            var chain = ((active && (index < chains.Count)) ? chains[index] : (CreatorChainState?)null);

            transforms[(m_goalSlotBase + index)] = new DynamicTransform(
                Orientation: Quaternion.Identity,
                Position: (chain?.Goal ?? hiddenPosition)
            );
        }
    }

    // One shape's emission: ResetPoint + TransformDynamic + Scale + [mirror/twist/bend: POINT ops, before the shape,
    // in that order] + the primitive (blend/smooth ride the primitive instruction — zero extra words) +
    // [dilate/onion: FIELD ops, AFTER the shape, dilate then onion — see SdfOp.Dilate/SdfOp.Onion's remarks: they
    // grow/shell the ACCUMULATED FIELD, not the point]. Position/orientation come from the slot's per-frame dynamic
    // transform; scale is baked; the primitive dimensions come from the SHARED canonical source (AvatarDefinition),
    // so the shape the player previews is byte-for-byte the geometry the forge later bakes.
    //
    // SCOPING (the accumulator fix — see the accumulator rule on SdfBlendOp + the sdf-world skill): dilate/onion are
    // FIELD ops, so in the flat accumulator they'd grow/shell EVERY shape emitted before this one (the floor and
    // every earlier shape), not this shape alone. A NON-GROUP shape (Pass 1 + the ghost — always plain Union)
    // therefore wraps its shape + dilate/onion in a one-deep PushField(Union)/PopField scope, so the field op(s)
    // apply to THIS shape alone and then union into the workbench, matching SceneObject.Emit's scoped field-operation
    // behavior. A shape with NEITHER field op takes the flat path. A GROUP member passes
    // inGroupScope: true and does NOT open its own scope — it already sits inside its group's single PushField scope
    // (EmitPool Pass 2), and the depth-1 field-scope cap (SdfProgramBuilder.MaxFieldScopeDepth) forbids nesting; a
    // grouped member's dilate/onion apply to the group-so-far, the best depth 1 allows. Mirror/twist/bend are POINT
    // ops, so they never need scoping (a group member's own point transform is always local to itself regardless of
    // the shared field accumulator).
    //
    // THE BINDING RULE: probeWorstCase emits EVERY op unconditionally (mirror on, twist/bend/dilate/onion nonzero,
    // AND the field scope's Push/Pop) so the probe measures the true worst case — any FUTURE per-shape modifier must
    // join this same probe path (the capacity envelope is derived from it; see EmitPool's remarks).
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

        // A non-group shape scopes its own dilate/onion: Push its Union compose, emit the shape as Union INSIDE the
        // fresh FAR-seeded accumulator (against which it IS the shape), inflate/shell it, then Pop it into the
        // workbench. A group member never nests (the enclosing group already opened the one allowed scope) and
        // emits flat.
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

    // Whether a composition group must be emitted inside a PushField/PopField field scope: true when any member
    // carries a non-Union blend (the Intersection family wipes the whole accumulated workbench otherwise — the
    // documented creator defect) or a dilate/onion field op. A pure-Union, no-field-op group stays flat
    // (byte-identical), so a union-only creation loads bit-for-bit. See the accumulator rule on SdfBlendOp / the
    // sdf-world skill. Bend/mirror/twist are POINT ops and never factor in — they never need scoping.
    private static bool GroupNeedsScope(IReadOnlyList<CreatorShapeState> shapes, int groupId, int fromIndex) {
        for (var member = fromIndex; (member < shapes.Count); member++) {
            var shape = shapes[member];

            if ((shape.GroupId == groupId) && ((shape.Blend != SdfBlendOp.Union) || (shape.Onion != 0f) || (shape.Dilate != 0f))) {
                return true;
            }
        }

        return false;
    }

    // A scaled shape's dynamic instance bound: the unit-scale bound grown by the largest scale component, so the beam
    // prepass's tile cull never clips a shape the player enlarged.
    private static float BoundRadius(Vector3 scale) =>
        (InstanceRadiusUnitScale * MathF.Max(x: scale.X, y: MathF.Max(x: scale.Y, y: scale.Z)));
}
