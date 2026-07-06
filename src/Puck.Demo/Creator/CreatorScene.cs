using System.Numerics;
using Puck.Demo.Forge;
using Puck.SdfVm;

namespace Puck.Demo.Creator;

/// <summary>
/// The authored creator scene — the editor's model, extracted from the overworld frame source so the authoring surface
/// can grow without swelling the render plumbing. Owns the placed shapes (with material/blend/smooth/group state), the
/// 16-slot material palette, the live ghost (the shape being placed), and the selection. Every mutation verb lives
/// here; the pad state machine (<see cref="CreatorController"/>) and the console verbs both drive this one model.
/// All of it is HOST-SIDE PRESENTATION — the deterministic world/hash never sees a creator shape.
///
/// Two counters expose change: <see cref="Revision"/> bumps on EVERY mutation (the bake-preview poll seam), and
/// <see cref="ProgramRevision"/> bumps only when the SDF program's content changed (a place/undo/primitive/scale/
/// material/blend/group edit — the frame source rebuilds when it moves). Moves and rotations ride the per-frame
/// dynamic-transform buffer and never rebuild.
///
/// The TARGET model: every edit verb acts on the SELECTED placed shape when there is one, else on the ghost — so the
/// sticks/triggers/d-pad keep one physical meaning everywhere and pages only remap the discrete buttons.
/// </summary>
public sealed class CreatorScene {
    /// <summary>The maximum number of placed shapes. The engine's program/instance buffers reserve capacity for the
    /// full pool up front (the frame source probes the worst case at construction), so authoring up to here never
    /// grows a GPU buffer.</summary>
    public const int Capacity = 64;
    /// <summary>The material palette's slot count.</summary>
    public const int PaletteSize = 16;
    /// <summary>The scale envelope a single shape may be grown/shrunk to — clamps keep a shape from vanishing to a
    /// point or ballooning past its instance's bound headroom.</summary>
    public const float MinScale = 0.2f;
    public const float MaxScale = 3.0f;
    /// <summary>The largest smooth-blend radius (the STYLE page's d-pad sweep clamps here).</summary>
    public const float MaxSmooth = 0.5f;
    /// <summary>The largest twist rate, in radians per unit of local Y (the STYLE page's d-pad sweep clamps here;
    /// NOT an isometry, so this stays moderate — see <see cref="Puck.SdfVm.SdfProgramBuilder.TwistY"/>).</summary>
    public const float MaxTwist = 3.0f;
    /// <summary>The largest onion shell thickness (the STYLE page's d-pad sweep clamps here).</summary>
    public const float MaxOnion = 0.2f;
    /// <summary>The number of primitives in the cycle (the <see cref="AvatarPrimitive"/> set).</summary>
    public const int PrimitiveCount = 7;
    /// <summary>The bounded undo/redo history's snapshot capacity (see <see cref="EditHistory{T}"/>).</summary>
    public const int HistoryCapacity = 64;
    /// <summary>The most chains a scene may define — bounds the renderer's reserved goal-marker dynamic-transform
    /// slots (see <see cref="CreatorSceneRenderer.GoalSlotCount"/>) to a fixed, probe-safe budget.</summary>
    public const int MaxChains = 16;

    // The blend cycle in AUTHORING order (each hard op followed by its smooth variant, the exotic Xor last) — the
    // STYLE page sweeps this, not the raw enum order.
    private static readonly SdfBlendOp[] BlendCycle = [
        SdfBlendOp.Union,
        SdfBlendOp.SmoothUnion,
        SdfBlendOp.Subtraction,
        SdfBlendOp.SmoothSubtraction,
        SdfBlendOp.Intersection,
        SdfBlendOp.SmoothIntersection,
        SdfBlendOp.Xor,
    ];

    private readonly WorkbenchRegion m_workbench;
    private readonly List<CreatorShapeState> m_shapes = new(capacity: Capacity);
    // Shape id -> current index in m_shapes, kept in sync with every ADD/REMOVE/CLEAR of m_shapes (a plain in-place
    // `m_shapes[i] = shape with {...}` never touches this — the id/position pairing doesn't change). Lookups by id
    // (Select/ResolveShapeId/SolveChains/TryCaptureChain/ApplyPoses) are otherwise an O(Capacity) linear scan repeated
    // every pose/playback frame — this turns them into an O(1) dictionary hit.
    private readonly Dictionary<int, int> m_shapeIndexById = new(capacity: Capacity);
    private readonly SdfMaterial[] m_palette = DefaultPalette();
    private bool m_active;
    private int m_nextShapeId;
    private int m_nextGroupId = 1;
    private int m_selectionIndex = -1;
    // The selection BEFORE the current one — the chain-link partner the SELECT page's link verb groups with.
    private int m_previousSelectionIndex = -1;
    // Whether transform verbs apply to the target's whole GROUP (about its centroid) instead of the single shape.
    private bool m_groupScope;
    // The per-cart bake style knob ("classic" or "bold") — carried on the scene/document; the preview seam consumes it.
    private string m_bakeStyle = "classic";
    // The bake preview's hardware-target and overlay knobs (scene-side so the console verbs reach them without
    // knowing the bake service; a change bumps Revision, which re-bakes).
    private string m_bakeTarget = "cgb";
    private int m_bakeOverlay;
    // The authoring intent (a 3D world object vs 2D sprite art destined for the bake) — drives the workpiece camera
    // framing and the preview's composition.
    private CreatorIntent m_intent = CreatorIntent.Object;
    // The creation's player-given name (the save/load handle).
    private string m_name = "creation";
    // THE TIMELINE (the minimal frame-snapshot model): saved frames display as 1..Count; index 0 is the REST pose —
    // the live authored scene, captured implicitly the first time the timeline steps away from it and restored on
    // return/stop. Playback holds each frame for m_secondsPerFrame (no interpolation, by design).
    private readonly List<CreatorFrameState> m_frames = [];
    private int m_currentFrame;
    private CreatorFrameState? m_restPose;
    private bool m_playing;
    private float m_playClock;
    private int m_playCursor;
    private float m_secondsPerFrame = (8f / 60f);
    // The ghost — the shape being placed. Its transform AND style are what the NEXT placed shape inherits.
    private AvatarPrimitive m_ghostType;
    private Vector3 m_ghostPosition;
    private Quaternion m_ghostRotation = Quaternion.Identity;
    private Vector3 m_ghostScale = Vector3.One;
    private int m_ghostMaterial;
    private SdfBlendOp m_ghostBlend = SdfBlendOp.Union;
    private float m_ghostSmooth;
    private bool m_ghostMirror;
    private float m_ghostTwist;
    private float m_ghostOnion;
    // THE RIG (chains + IK — see CreatorChainState/CreatorIk): defined chains, keyed by their stable id.
    private readonly List<CreatorChainState> m_chains = [];
    private int m_nextChainId = 1;
    // The selection also cycles PAST placed shapes into chain goals (the TARGET model's extension for the RIG page):
    // -1 = none/ghost, >=0 an index into m_chains whose GOAL is the target (see TargetIsGoal).
    private int m_goalChainIndex = -1;
    // The RIG pad page's SEPARATE current-chain cursor (which chain the pole-nudge/kind-toggle/delete verbs act on)
    // — distinct from m_goalChainIndex, since tuning a chain's pole/kind does not require its goal to be the target.
    private int m_chainCursor = -1;
    // Real undo/redo: a bounded ring of immutable whole-scene snapshots. Continuous edits (move/rotate/scale/smooth/
    // twist/onion sweeps) coalesce onto ONE snapshot per drag (pushed on the drag's START edge, not per frame) via
    // m_dragOpen/m_dragTouchedThisFrame; discrete edits (place/delete/duplicate/material/blend/mirror/group/frame
    // record/chain edits) each push their own snapshot before mutating.
    private readonly EditHistory<CreatorSnapshot> m_history;
    private bool m_dragOpen;
    private bool m_dragTouchedThisFrame;

    /// <summary>Initializes an empty scene clamped to the given workbench region.</summary>
    /// <param name="workbench">The authoring region (also the ghost spawn + group bound source).</param>
    public CreatorScene(WorkbenchRegion workbench) {
        m_workbench = workbench;
        m_ghostPosition = workbench.SpawnPosition;
        m_history = new EditHistory<CreatorSnapshot>(capacity: HistoryCapacity, initial: CaptureSnapshot());
    }

    /// <summary>Bumps on EVERY mutation — the bake-preview service polls it to notice edits.</summary>
    public int Revision { get; private set; }
    /// <summary>Bumps when the SDF PROGRAM's content changed (primitive/scale/material/blend/group/count edits) — the
    /// frame source rebuilds when this moves; a move/rotate leaves it alone.</summary>
    public int ProgramRevision { get; private set; }

    /// <summary>Whether creator mode is active (the ghost is visible and the player edits shapes).</summary>
    public bool Active => m_active;
    /// <summary>The placed shapes, in document order (the order group blends evaluate in).</summary>
    public IReadOnlyList<CreatorShapeState> Shapes => m_shapes;
    /// <summary>How many shapes have been placed so far.</summary>
    public int PlacedCount => m_shapes.Count;
    /// <summary>The material palette (16 slots); shapes reference entries by index.</summary>
    public IReadOnlyList<SdfMaterial> Palette => m_palette;
    /// <summary>The authoring region (the ghost clamp, the orbit target, the group bound source).</summary>
    public WorkbenchRegion Workbench => m_workbench;
    /// <summary>The selected placed shape's index (-1 = none; the ghost is then the target, UNLESS a chain goal is
    /// selected — see <see cref="TargetIsGoal"/>).</summary>
    public int SelectionIndex => m_selectionIndex;
    /// <summary>Whether edit verbs currently target the ghost (no placed shape AND no chain goal is selected).</summary>
    public bool TargetIsGhost => (!TargetIsGoal && ((m_selectionIndex < 0) || (m_selectionIndex >= m_shapes.Count)));
    /// <summary>The selected placed shape, when there is one.</summary>
    public CreatorShapeState? SelectedShape => (TargetIsGhost ? null : m_shapes[m_selectionIndex]);
    /// <summary>Whether transform verbs apply to the target's whole group (see <see cref="ToggleGroupScope"/>).</summary>
    public bool GroupScope => m_groupScope;
    /// <summary>The per-cart bake style knob (<c>classic</c> or <c>bold</c>) the preview seam consumes.</summary>
    public string BakeStyle => m_bakeStyle;
    /// <summary>The authoring intent (3D world object vs 2D sprite art) — drives camera framing + preview.</summary>
    public CreatorIntent Intent => m_intent;
    /// <summary>The creation's player-given name (the save/load handle).</summary>
    public string Name => m_name;
    /// <summary>The TARGET's blend op (the selected shape's, else the ghost's — what the STYLE page edits).</summary>
    public SdfBlendOp TargetBlend => (TargetIsGhost ? m_ghostBlend : m_shapes[m_selectionIndex].Blend);
    /// <summary>The TARGET's smooth radius.</summary>
    public float TargetSmooth => (TargetIsGhost ? m_ghostSmooth : m_shapes[m_selectionIndex].Smooth);
    /// <summary>The TARGET's palette slot.</summary>
    public int TargetMaterialIndex => (TargetIsGhost ? m_ghostMaterial : m_shapes[m_selectionIndex].MaterialIndex);
    /// <summary>The TARGET's mirror flag (<see cref="Puck.SdfVm.SdfOp.SymmetryX"/>).</summary>
    public bool TargetMirror => (TargetIsGhost ? m_ghostMirror : m_shapes[m_selectionIndex].Mirror);
    /// <summary>The TARGET's twist rate (<see cref="Puck.SdfVm.SdfOp.TwistY"/>).</summary>
    public float TargetTwist => (TargetIsGhost ? m_ghostTwist : m_shapes[m_selectionIndex].Twist);
    /// <summary>The TARGET's onion shell thickness (<see cref="Puck.SdfVm.SdfOp.Onion"/>).</summary>
    public float TargetOnion => (TargetIsGhost ? m_ghostOnion : m_shapes[m_selectionIndex].Onion);
    /// <summary>The ghost's blend op (what the next placed shape inherits).</summary>
    public SdfBlendOp GhostBlend => m_ghostBlend;
    /// <summary>The ghost's smooth radius (inherited on place).</summary>
    public float GhostSmooth => m_ghostSmooth;
    /// <summary>The ghost's mirror flag (inherited on place).</summary>
    public bool GhostMirror => m_ghostMirror;
    /// <summary>The ghost's twist rate (inherited on place).</summary>
    public float GhostTwist => m_ghostTwist;
    /// <summary>The ghost's onion shell thickness (inherited on place).</summary>
    public float GhostOnion => m_ghostOnion;
    /// <summary>Whether the SELECTED target is a chain GOAL rather than a shape or the ghost (see
    /// <see cref="CycleSelection"/>'s extension into chain goals). The global Move verbs then drive the goal and
    /// <see cref="SolveChains"/> re-poses the chain live.</summary>
    public bool TargetIsGoal => ((m_goalChainIndex >= 0) && (m_goalChainIndex < m_chains.Count));
    /// <summary>The chains currently defined on this scene, in definition order.</summary>
    public IReadOnlyList<CreatorChainState> Chains => m_chains;
    /// <summary>The chain whose goal is the current target, when <see cref="TargetIsGoal"/>.</summary>
    public CreatorChainState? TargetGoalChain => (TargetIsGoal ? m_chains[m_goalChainIndex] : null);
    /// <summary>Whether an undo step is available.</summary>
    public bool CanUndo => m_history.CanUndo;
    /// <summary>Whether a redo step is available.</summary>
    public bool CanRedo => m_history.CanRedo;

    /// <summary>The ghost's primitive.</summary>
    public AvatarPrimitive GhostType => m_ghostType;
    /// <summary>The ghost's live position (render-relative).</summary>
    public Vector3 GhostPosition => m_ghostPosition;
    /// <summary>The ghost's live orientation.</summary>
    public Quaternion GhostRotation => m_ghostRotation;
    /// <summary>The ghost's live scale (baked into the program).</summary>
    public Vector3 GhostScale => m_ghostScale;
    /// <summary>The ghost's palette slot (the material the next placed shape inherits).</summary>
    public int GhostMaterial => m_ghostMaterial;
    /// <summary>The TARGET's primitive name (the selected shape's, else the ghost's) — for the console/HUD readout.</summary>
    public string TargetShapeName => (TargetIsGhost ? m_ghostType : m_shapes[m_selectionIndex].Type).ToString();

    /// <summary>Enters or leaves creator mode. Entering re-seats the ghost at the workbench spawn; leaving keeps every
    /// placed shape and clears the selection.</summary>
    /// <param name="active">The desired state.</param>
    public void SetActive(bool active) {
        if (m_active == active) {
            return;
        }

        m_active = active;
        m_selectionIndex = -1;

        if (active) {
            m_ghostPosition = m_workbench.SpawnPosition;
        }

        // The mode toggle changes what the renderer emits around the pool (the sprite backdrop, the preview easel),
        // so it rebuilds — one envelope-safe rebuild per toggle.
        MarkProgramChanged();
    }

    /// <summary>Cycles the TARGET's primitive (wraps both directions): the ghost's when nothing is selected, the
    /// selected shape's otherwise (re-primitive in place). Rebuilds the program.</summary>
    /// <param name="direction">+1 for the next primitive, -1 for the previous.</param>
    public void CyclePrimitive(int direction) {
        if (!m_active) {
            return;
        }

        if (TargetIsGhost) {
            m_ghostType = (AvatarPrimitive)((((int)m_ghostType + direction) % PrimitiveCount + PrimitiveCount) % PrimitiveCount);
        } else {
            var shape = m_shapes[m_selectionIndex];

            m_shapes[m_selectionIndex] = shape with { Type = (AvatarPrimitive)((((int)shape.Type + direction) % PrimitiveCount + PrimitiveCount) % PrimitiveCount) };
        }

        MarkProgramChanged();
    }

    /// <summary>Moves the TARGET this frame — planar on the floor plane plus a vertical nudge — clamped inside the
    /// workbench region. When the target is a chain GOAL (see <see cref="TargetIsGoal"/>) this moves the goal
    /// instead and re-solves the chain LIVE. A pure transform update (no rebuild) — a goal move bumps
    /// <see cref="Revision"/> ONLY, never <see cref="ProgramRevision"/> (the settled IK contract).</summary>
    /// <param name="planar">The X/Z move (already in render space: +Y of the vector is +Z).</param>
    /// <param name="vertical">The up/down nudge (+ up).</param>
    /// <param name="deltaSeconds">The frame delta.</param>
    public void Move(Vector2 planar, float vertical, float deltaSeconds) {
        if (!m_active) {
            return;
        }

        const float moveSpeed = 3.2f;
        var step = (new Vector3(planar.X, vertical, planar.Y) * (moveSpeed * deltaSeconds));

        if (step == Vector3.Zero) {
            return;
        }

        var pushAfter = TouchDrag();

        if (TargetIsGoal) {
            var chain = m_chains[m_goalChainIndex];

            m_chains[m_goalChainIndex] = (chain with { Goal = m_workbench.Clamp(position: (chain.Goal + step)) });
            SolveChains();
            PushIfDragStarted(pushAfter);

            return;
        }

        if (TargetIsGhost) {
            m_ghostPosition = m_workbench.Clamp(position: (m_ghostPosition + step));
        } else if (GroupScopeApplies()) {
            var groupId = m_shapes[m_selectionIndex].GroupId;

            for (var index = 0; (index < m_shapes.Count); index++) {
                if (m_shapes[index].GroupId == groupId) {
                    m_shapes[index] = m_shapes[index] with { Position = m_workbench.Clamp(position: (m_shapes[index].Position + step)) };
                }
            }
        } else {
            var shape = m_shapes[m_selectionIndex];

            m_shapes[m_selectionIndex] = shape with { Position = m_workbench.Clamp(position: (shape.Position + step)) };
        }

        Revision++;
        PushIfDragStarted(pushAfter);
    }

    /// <summary>Spins the TARGET this frame — yaw about world up (stick X), pitch about world right (stick Y), roll
    /// about world forward — composed onto its live orientation. A pure dynamic-transform update (no rebuild).</summary>
    /// <param name="stick">The right-stick vector: X yaws, Y pitches (up on the stick pitches the top away).</param>
    /// <param name="roll">The roll rate (−1 rolls left, +1 rolls right), typically the d-pad's left/right axis.</param>
    /// <param name="deltaSeconds">The frame delta.</param>
    public void Rotate(Vector2 stick, float roll, float deltaSeconds) {
        if (!m_active || ((stick == Vector2.Zero) && (roll == 0f)) || TargetIsGoal) {
            return;
        }

        var pushAfter = TouchDrag();

        const float rotateSpeed = 2.2f; // radians/second at full deflection

        var step = (rotateSpeed * deltaSeconds);
        // World-space axis deltas (premultiplied onto the current orientation) — intuitive under the creator camera:
        // yaw and roll read the same regardless of how far the shape has already turned.
        var delta = (Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: (stick.X * step))
            * Quaternion.CreateFromAxisAngle(axis: Vector3.UnitX, angle: (-stick.Y * step))
            * Quaternion.CreateFromAxisAngle(axis: Vector3.UnitZ, angle: (roll * step)));

        if (TargetIsGhost) {
            m_ghostRotation = Quaternion.Normalize(value: (delta * m_ghostRotation));
        } else if (GroupScopeApplies()) {
            // The whole group turns about its centroid: every member's position orbits it and every member's
            // orientation composes the same delta — the group reads as one rigid body.
            var groupId = m_shapes[m_selectionIndex].GroupId;
            var centroid = GroupCentroid(groupId: groupId);

            for (var index = 0; (index < m_shapes.Count); index++) {
                if (m_shapes[index].GroupId != groupId) {
                    continue;
                }

                var member = m_shapes[index];

                m_shapes[index] = member with {
                    Position = m_workbench.Clamp(position: (centroid + Vector3.Transform(value: (member.Position - centroid), rotation: delta))),
                    Rotation = Quaternion.Normalize(value: (delta * member.Rotation)),
                };
            }
        } else {
            var shape = m_shapes[m_selectionIndex];

            m_shapes[m_selectionIndex] = shape with { Rotation = Quaternion.Normalize(value: (delta * shape.Rotation)) };
        }

        Revision++;
        PushIfDragStarted(pushAfter);
    }

    /// <summary>Grows or shrinks the TARGET this frame (uniform), clamped to the scale envelope. Scale is baked into
    /// the program, so a change flags a rebuild.</summary>
    /// <param name="delta">The scale rate (−1 shrinks, +1 grows), typically the d-pad's up/down axis.</param>
    /// <param name="deltaSeconds">The frame delta.</param>
    public void ScaleUniform(float delta, float deltaSeconds) {
        if (!m_active || (delta == 0f) || TargetIsGoal) {
            return;
        }

        var pushAfter = TouchDrag();

        // Multiplicative growth reads evenly across the range (a second of "up" always ~doubles, of "down" ~halves).
        var factor = MathF.Exp((delta * 1.6f * deltaSeconds));

        if (TargetIsGhost) {
            var next = Math.Clamp(value: (m_ghostScale.X * factor), max: MaxScale, min: MinScale);

            if (next != m_ghostScale.X) {
                m_ghostScale = new Vector3(next);
                MarkProgramChanged();
            }
        } else if (GroupScopeApplies()) {
            // The whole group grows about its centroid: member positions spread/contract from it and every member's
            // scale multiplies (each clamped to the envelope), so the composite keeps its proportions.
            var groupId = m_shapes[m_selectionIndex].GroupId;
            var centroid = GroupCentroid(groupId: groupId);
            var changed = false;

            for (var index = 0; (index < m_shapes.Count); index++) {
                if (m_shapes[index].GroupId != groupId) {
                    continue;
                }

                var member = m_shapes[index];
                var next = Math.Clamp(value: (member.Scale.X * factor), max: MaxScale, min: MinScale);

                if (next != member.Scale.X) {
                    m_shapes[index] = member with {
                        Position = m_workbench.Clamp(position: (centroid + ((member.Position - centroid) * factor))),
                        Scale = new Vector3(next),
                    };
                    changed = true;
                }
            }

            if (changed) {
                MarkProgramChanged();
            }
        } else {
            var shape = m_shapes[m_selectionIndex];
            var next = Math.Clamp(value: (shape.Scale.X * factor), max: MaxScale, min: MinScale);

            if (next != shape.Scale.X) {
                m_shapes[m_selectionIndex] = shape with { Scale = new Vector3(next) };
                MarkProgramChanged();
            }
        }

        PushIfDragStarted(pushAfter);
    }

    /// <summary>Resets the TARGET's orientation to identity and its scale to unit — the quick "start this shape over"
    /// affordance (a no-op on a chain goal, which has neither). Rebuilds the program when the scale changed.</summary>
    public void ResetTransform() {
        if (!m_active || TargetIsGoal) {
            return;
        }

        if (TargetIsGhost) {
            m_ghostRotation = Quaternion.Identity;

            if (m_ghostScale != Vector3.One) {
                m_ghostScale = Vector3.One;
                MarkProgramChanged();
            } else {
                Revision++;
            }
        } else {
            var shape = m_shapes[m_selectionIndex];
            var scaleChanged = (shape.Scale != Vector3.One);

            m_shapes[m_selectionIndex] = shape with { Rotation = Quaternion.Identity, Scale = Vector3.One };

            if (scaleChanged) {
                MarkProgramChanged();
            } else {
                Revision++;
            }
        }

        PushUndo();
    }

    /// <summary>Places the ghost's current primitive at its current position, orientation, scale, material, blend,
    /// and smooth radius (a no-op when the pool is full). A non-Union blend coerces the new shape into its own
    /// group-of-one (the structural invariant: blends only ever act within a group, so the emission's instance-cull
    /// contract holds by construction). Rebuilds the program.</summary>
    public void Place() {
        if (!m_active || (m_shapes.Count >= Capacity)) {
            return;
        }

        m_shapes.Add(item: new CreatorShapeState(
            Blend: m_ghostBlend,
            GroupId: ((m_ghostBlend != SdfBlendOp.Union) ? m_nextGroupId++ : 0),
            Id: m_nextShapeId++,
            MaterialIndex: m_ghostMaterial,
            Mirror: m_ghostMirror,
            Name: null,
            Onion: m_ghostOnion,
            Position: m_ghostPosition,
            Rotation: m_ghostRotation,
            Scale: m_ghostScale,
            Smooth: m_ghostSmooth,
            Twist: m_ghostTwist,
            Type: m_ghostType
        ));
        RebuildShapeIndex();
        // The next placement reads as a sibling: advance the ghost's palette slot the way the classic pool swept its
        // per-slot hues, so consecutive placements stay visually distinct without any palette work by the player.
        m_ghostMaterial = ((m_ghostMaterial + 1) % PaletteSize);
        MarkProgramChanged();
        PushUndo();
    }

    /// <summary>Steps the REAL undo history back one edit, restoring the whole scene (shapes, palette, frames,
    /// chains) to its state before the most recent completed edit (a no-op when there is nothing to undo).</summary>
    /// <returns>Whether an undo step was applied.</returns>
    public bool Undo() {
        if (!m_active || !m_history.TryUndo(snapshot: out var snapshot)) {
            return false;
        }

        RestoreSnapshot(snapshot: snapshot);

        return true;
    }

    /// <summary>Steps the REAL undo history forward one edit (a no-op when there is nothing to redo, or after a new
    /// edit truncated the redo tail).</summary>
    /// <returns>Whether a redo step was applied.</returns>
    public bool Redo() {
        if (!m_active || !m_history.TryRedo(snapshot: out var snapshot)) {
            return false;
        }

        RestoreSnapshot(snapshot: snapshot);

        return true;
    }

    /// <summary>Cycles the selection through the placed shapes, THEN past them into the defined chains' GOALS (the
    /// TARGET model's extension for the RIG page — see <see cref="TargetIsGoal"/>), wrapping through "none" at
    /// either end (where the target reverts to the ghost). Rebuilds the program (a shape/ghost target renders with
    /// a highlight material; a goal selection is a pure Revision bump — see <see cref="Move"/>).</summary>
    /// <param name="direction">+1 for the next shape/goal, -1 for the previous.</param>
    public void CycleSelection(int direction) {
        if (!m_active || ((m_shapes.Count == 0) && (m_chains.Count == 0))) {
            return;
        }

        if (!TargetIsGhost) {
            m_previousSelectionIndex = m_selectionIndex;
        }

        // The combined cursor space: -1 (ghost), 0..shapes.Count-1 (shapes), shapes.Count..+chains.Count-1 (goals).
        var combined = (TargetIsGoal ? (m_shapes.Count + m_goalChainIndex) : m_selectionIndex);
        var span = (m_shapes.Count + m_chains.Count);
        var next = (combined + direction);

        next = ((next >= span) ? -1 : ((next < -1) ? (span - 1) : next));

        if (next >= m_shapes.Count) {
            m_selectionIndex = -1;
            m_goalChainIndex = (next - m_shapes.Count);
        } else {
            m_selectionIndex = next;
            m_goalChainIndex = -1;
        }

        MarkProgramChanged();
    }

    /// <summary>Clears the selection (the target reverts to the ghost). Rebuilds the program (the highlight clears).</summary>
    public void Deselect() {
        if (!m_active || TargetIsGhost) {
            return;
        }

        m_previousSelectionIndex = m_selectionIndex;
        m_selectionIndex = -1;
        m_goalChainIndex = -1;
        MarkProgramChanged();
    }

    /// <summary>Duplicates the TARGET: a selected shape copies in place (nudged aside so the twin reads) and becomes
    /// the selection; with no selection this is a plain place. Rebuilds the program.</summary>
    /// <returns>Whether a shape was added.</returns>
    public bool DuplicateTarget() {
        if (!m_active || (m_shapes.Count >= Capacity) || TargetIsGoal) {
            return false;
        }

        if (TargetIsGhost) {
            Place();

            return true;
        }

        var source = m_shapes[m_selectionIndex];

        m_shapes.Add(item: source with {
            Id = m_nextShapeId++,
            Name = null,
            Position = m_workbench.Clamp(position: (source.Position + new Vector3(0.35f, 0f, 0f))),
            // A duplicate of a grouped member joins the SAME group (the twin composes the same way); an ungrouped
            // twin stays ungrouped.
        });
        RebuildShapeIndex();
        m_previousSelectionIndex = m_selectionIndex;
        m_selectionIndex = (m_shapes.Count - 1);
        MarkProgramChanged();
        PushUndo();

        return true;
    }

    /// <summary>Deletes the SELECTED shape (a no-op when nothing is selected — the ghost and a chain goal cannot be
    /// deleted). The selection clears. Rebuilds the program.</summary>
    /// <returns>Whether a shape was removed.</returns>
    public bool DeleteSelected() {
        if (!m_active || TargetIsGhost || TargetIsGoal) {
            return false;
        }

        m_shapes.RemoveAt(index: m_selectionIndex);
        RebuildShapeIndex();
        m_selectionIndex = -1;
        m_previousSelectionIndex = -1;
        MarkProgramChanged();
        PushUndo();

        return true;
    }

    /// <summary>Links the SELECTED shape with the PREVIOUSLY selected one into a composition group (chain-link
    /// grouping: select A, then select B, then link). Groups merge when both shapes already belong to one. Blends
    /// act within a group in document order. Rebuilds the program (grouped shapes emit as one instance).</summary>
    /// <returns>The joined group id, or null when there was no valid pair to link.</returns>
    public int? LinkWithPrevious() {
        if (!m_active || TargetIsGhost || TargetIsGoal ||
            (m_previousSelectionIndex < 0) || (m_previousSelectionIndex >= m_shapes.Count) ||
            (m_previousSelectionIndex == m_selectionIndex)) {
            return null;
        }

        var current = m_shapes[m_selectionIndex];
        var previous = m_shapes[m_previousSelectionIndex];
        // Resolve the joined group: reuse either member's existing group, else mint a new one; when BOTH have
        // (different) groups, the previous shape's whole group migrates into the current's.
        var groupId = ((current.GroupId != 0) ? current.GroupId : ((previous.GroupId != 0) ? previous.GroupId : m_nextGroupId++));
        var migrating = ((previous.GroupId != 0) && (previous.GroupId != groupId) ? previous.GroupId : 0);

        for (var index = 0; (index < m_shapes.Count); index++) {
            if ((index == m_selectionIndex) || (index == m_previousSelectionIndex) ||
                ((migrating != 0) && (m_shapes[index].GroupId == migrating))) {
                m_shapes[index] = m_shapes[index] with { GroupId = groupId };
            }
        }

        MarkProgramChanged();
        PushUndo();

        return groupId;
    }

    /// <summary>Cycles the TARGET's blend op through the authoring order (Union → SmoothUnion → Subtraction →
    /// SmoothSubtraction → Intersection → SmoothIntersection → Xor). A non-Union blend on an UNGROUPED placed shape
    /// coerces it into its own group-of-one — the structural invariant that keeps blends inside instance bounds.
    /// Rebuilds the program.</summary>
    /// <param name="direction">+1 forward through the cycle, -1 back.</param>
    /// <returns>The target's new blend op.</returns>
    public SdfBlendOp CycleBlend(int direction) {
        if (!m_active || TargetIsGoal) {
            return TargetBlend;
        }

        var current = Array.IndexOf(array: BlendCycle, value: TargetBlend);
        var next = BlendCycle[(((current + direction) % BlendCycle.Length) + BlendCycle.Length) % BlendCycle.Length];

        if (TargetIsGhost) {
            m_ghostBlend = next;
            Revision++;
        } else {
            var shape = m_shapes[m_selectionIndex];

            m_shapes[m_selectionIndex] = shape with {
                Blend = next,
                GroupId = (((next != SdfBlendOp.Union) && (shape.GroupId == 0)) ? m_nextGroupId++ : shape.GroupId),
            };
            MarkProgramChanged();
            PushUndo();
        }

        return next;
    }

    /// <summary>Sweeps the TARGET's smooth-blend radius this frame (held, continuous), clamped to
    /// [0, <see cref="MaxSmooth"/>]. Rebuilds the program when it changes.</summary>
    /// <param name="delta">The sweep rate (−1 shrinks, +1 grows), typically the STYLE page's d-pad up/down axis.</param>
    /// <param name="deltaSeconds">The frame delta.</param>
    public void AdjustSmooth(float delta, float deltaSeconds) {
        if (!m_active || (delta == 0f) || TargetIsGoal) {
            return;
        }

        var step = (delta * 0.35f * deltaSeconds);

        if (TargetIsGhost) {
            var next = Math.Clamp(value: (m_ghostSmooth + step), max: MaxSmooth, min: 0f);

            if (next != m_ghostSmooth) {
                m_ghostSmooth = next;
                Revision++;
            }
        } else {
            var shape = m_shapes[m_selectionIndex];
            var next = Math.Clamp(value: (shape.Smooth + step), max: MaxSmooth, min: 0f);

            if (next != shape.Smooth) {
                var pushAfter = TouchDrag();

                m_shapes[m_selectionIndex] = shape with { Smooth = next };
                MarkProgramChanged();
                PushIfDragStarted(pushAfter);
            }
        }
    }

    /// <summary>Cycles the TARGET's material through the palette (wraps). Rebuilds the program.</summary>
    /// <param name="direction">+1 for the next palette slot, -1 for the previous.</param>
    /// <returns>The target's new palette slot.</returns>
    public int CycleMaterial(int direction) {
        if (!m_active || TargetIsGoal) {
            return TargetMaterialIndex;
        }

        var next = ((((TargetMaterialIndex + direction) % PaletteSize) + PaletteSize) % PaletteSize);

        if (TargetIsGhost) {
            m_ghostMaterial = next;
        } else {
            m_shapes[m_selectionIndex] = m_shapes[m_selectionIndex] with { MaterialIndex = next };
        }

        MarkProgramChanged();

        if (!TargetIsGhost) {
            PushUndo();
        }

        return next;
    }

    /// <summary>Toggles the TARGET's mirror flag (<see cref="Puck.SdfVm.SdfOp.SymmetryX"/> — a no-op on a chain
    /// goal). Rebuilds the program.</summary>
    /// <returns>The target's new mirror flag.</returns>
    public bool ToggleMirror() {
        if (!m_active || TargetIsGoal) {
            return TargetMirror;
        }

        var next = !TargetMirror;

        if (TargetIsGhost) {
            m_ghostMirror = next;
            MarkProgramChanged();
        } else {
            m_shapes[m_selectionIndex] = m_shapes[m_selectionIndex] with { Mirror = next };
            MarkProgramChanged();
            PushUndo();
        }

        return next;
    }

    /// <summary>Sweeps the TARGET's twist rate this frame (held, continuous), clamped to
    /// ±<see cref="MaxTwist"/>. Rebuilds the program when it changes (a no-op on a chain goal).</summary>
    /// <param name="delta">The sweep rate (−1/+1), typically the STYLE page's d-pad left/right axis.</param>
    /// <param name="deltaSeconds">The frame delta.</param>
    public void AdjustTwist(float delta, float deltaSeconds) {
        if (!m_active || (delta == 0f) || TargetIsGoal) {
            return;
        }

        var step = (delta * 1.6f * deltaSeconds);

        if (TargetIsGhost) {
            var next = Math.Clamp(value: (m_ghostTwist + step), max: MaxTwist, min: -MaxTwist);

            if (next != m_ghostTwist) {
                m_ghostTwist = next;
                MarkProgramChanged();
            }
        } else {
            var shape = m_shapes[m_selectionIndex];
            var next = Math.Clamp(value: (shape.Twist + step), max: MaxTwist, min: -MaxTwist);

            if (next != shape.Twist) {
                var pushAfter = TouchDrag();

                m_shapes[m_selectionIndex] = shape with { Twist = next };
                MarkProgramChanged();
                PushIfDragStarted(pushAfter);
            }
        }
    }

    /// <summary>Sweeps the TARGET's onion shell thickness this frame (held, continuous), clamped to
    /// [0, <see cref="MaxOnion"/>]. Rebuilds the program when it changes (a no-op on a chain goal).</summary>
    /// <param name="delta">The sweep rate (−1 thins/solidifies, +1 shells), typically the STYLE page's d-pad
    /// up/down axis.</param>
    /// <param name="deltaSeconds">The frame delta.</param>
    public void AdjustOnion(float delta, float deltaSeconds) {
        if (!m_active || (delta == 0f) || TargetIsGoal) {
            return;
        }

        var step = (delta * 0.14f * deltaSeconds);

        if (TargetIsGhost) {
            var next = Math.Clamp(value: (m_ghostOnion + step), max: MaxOnion, min: 0f);

            if (next != m_ghostOnion) {
                m_ghostOnion = next;
                MarkProgramChanged();
            }
        } else {
            var shape = m_shapes[m_selectionIndex];
            var next = Math.Clamp(value: (shape.Onion + step), max: MaxOnion, min: 0f);

            if (next != shape.Onion) {
                var pushAfter = TouchDrag();

                m_shapes[m_selectionIndex] = shape with { Onion = next };
                MarkProgramChanged();
                PushIfDragStarted(pushAfter);
            }
        }
    }

    /// <summary>Toggles whether transform verbs act on the target's whole GROUP (about its centroid) or the single
    /// shape.</summary>
    /// <returns>The new scope (true = group).</returns>
    public bool ToggleGroupScope() {
        m_groupScope = !m_groupScope;
        Revision++;

        return m_groupScope;
    }

    /// <summary>Toggles the per-cart bake style knob between <c>classic</c> and <c>bold</c>.</summary>
    /// <returns>The new style name.</returns>
    public string ToggleBakeStyle() {
        m_bakeStyle = (string.Equals(a: m_bakeStyle, b: "classic", comparisonType: StringComparison.OrdinalIgnoreCase) ? "bold" : "classic");
        Revision++;

        return m_bakeStyle;
    }

    // ---- console-assist verbs (the precise/named half of the pad-first + console-assist input model) -------------

    /// <summary>Clears the scene to empty (the console <c>creator.new</c> verb). Rebuilds the program.</summary>
    /// <returns>How many shapes were discarded.</returns>
    public int Clear() {
        var discarded = m_shapes.Count;

        m_shapes.Clear();
        RebuildShapeIndex();
        m_selectionIndex = -1;
        m_previousSelectionIndex = -1;
        m_goalChainIndex = -1;
        m_chains.Clear();
        m_name = "creation";
        MarkProgramChanged();
        PushUndo();

        return discarded;
    }

    /// <summary>Selects a placed shape by id or (case-insensitive) name. Rebuilds the program (highlight).</summary>
    /// <param name="idOrName">The shape's id (digits) or its player-given name.</param>
    /// <returns>The selected shape, or null when nothing matched.</returns>
    public CreatorShapeState? Select(string idOrName) {
        // A digit token resolves via the id->index map (O(1)); a name token still scans (names are an optional,
        // rarely-used handle, and there is no name index to maintain).
        if (int.TryParse(s: idOrName, result: out var id)) {
            if (!m_shapeIndexById.TryGetValue(key: id, value: out var mappedIndex)) {
                return null;
            }

            var mappedShape = m_shapes[mappedIndex];

            if (!TargetIsGhost) {
                m_previousSelectionIndex = m_selectionIndex;
            }

            m_selectionIndex = mappedIndex;
            m_goalChainIndex = -1;
            MarkProgramChanged();

            return mappedShape;
        }

        for (var index = 0; (index < m_shapes.Count); index++) {
            var shape = m_shapes[index];

            if (string.Equals(a: shape.Name, b: idOrName, comparisonType: StringComparison.OrdinalIgnoreCase)) {
                if (!TargetIsGhost) {
                    m_previousSelectionIndex = m_selectionIndex;
                }

                m_selectionIndex = index;
                m_goalChainIndex = -1;
                MarkProgramChanged();

                return shape;
            }
        }

        return null;
    }

    /// <summary>Renames the SELECTED shape (a no-op without a selection, or on a chain goal).</summary>
    /// <param name="name">The new name.</param>
    /// <returns>Whether a shape was renamed.</returns>
    public bool RenameSelected(string name) {
        if (TargetIsGhost || TargetIsGoal) {
            return false;
        }

        m_shapes[m_selectionIndex] = m_shapes[m_selectionIndex] with { Name = name };
        Revision++;

        return true;
    }

    /// <summary>Renames the whole creation (the save/load handle).</summary>
    /// <param name="name">The new creation name.</param>
    public void SetName(string name) {
        m_name = name;
        Revision++;
    }

    /// <summary>Assigns the TARGET's palette slot directly. Rebuilds the program (a no-op on a chain goal).</summary>
    /// <param name="index">The palette slot (clamped into range).</param>
    /// <returns>The applied slot.</returns>
    public int SetMaterialIndex(int index) {
        if (TargetIsGoal) {
            return TargetMaterialIndex;
        }

        var clamped = Math.Clamp(value: index, max: (PaletteSize - 1), min: 0);

        if (TargetIsGhost) {
            m_ghostMaterial = clamped;
            MarkProgramChanged();
        } else {
            m_shapes[m_selectionIndex] = m_shapes[m_selectionIndex] with { MaterialIndex = clamped };
            MarkProgramChanged();
            PushUndo();
        }

        return clamped;
    }

    /// <summary>Edits a palette entry (every shape referencing the slot re-colors). Rebuilds the program.</summary>
    /// <param name="index">The palette slot (clamped into range).</param>
    /// <param name="material">The new material.</param>
    public void SetPaletteEntry(int index, SdfMaterial material) {
        m_palette[Math.Clamp(value: index, max: (PaletteSize - 1), min: 0)] = material;
        MarkProgramChanged();
        PushUndo();
    }

    /// <summary>Sets the TARGET's blend op directly (same group-of-one coercion as <see cref="CycleBlend"/>; a
    /// no-op on a chain goal). Rebuilds the program.</summary>
    /// <param name="blend">The blend op.</param>
    public void SetBlend(SdfBlendOp blend) {
        if (TargetIsGoal) {
            return;
        }

        if (TargetIsGhost) {
            m_ghostBlend = blend;
            Revision++;
        } else {
            var shape = m_shapes[m_selectionIndex];

            m_shapes[m_selectionIndex] = shape with {
                Blend = blend,
                GroupId = (((blend != SdfBlendOp.Union) && (shape.GroupId == 0)) ? m_nextGroupId++ : shape.GroupId),
            };
            MarkProgramChanged();
            PushUndo();
        }
    }

    /// <summary>Sets the TARGET's smooth-blend radius directly (clamped; a no-op on a chain goal). Rebuilds the
    /// program.</summary>
    /// <param name="value">The radius (clamped to [0, <see cref="MaxSmooth"/>]).</param>
    /// <returns>The applied radius.</returns>
    public float SetSmooth(float value) {
        if (TargetIsGoal) {
            return TargetSmooth;
        }

        var clamped = Math.Clamp(value: value, max: MaxSmooth, min: 0f);

        if (TargetIsGhost) {
            m_ghostSmooth = clamped;
            Revision++;
        } else {
            m_shapes[m_selectionIndex] = m_shapes[m_selectionIndex] with { Smooth = clamped };
            MarkProgramChanged();
            PushUndo();
        }

        return clamped;
    }

    /// <summary>Sets the TARGET's twist rate directly (clamped; a no-op on a chain goal). Rebuilds the program.</summary>
    /// <param name="value">The rate (clamped to ±<see cref="MaxTwist"/>).</param>
    /// <returns>The applied rate.</returns>
    public float SetTwist(float value) {
        if (TargetIsGoal) {
            return TargetTwist;
        }

        var clamped = Math.Clamp(value: value, max: MaxTwist, min: -MaxTwist);

        if (TargetIsGhost) {
            m_ghostTwist = clamped;
            MarkProgramChanged();
        } else {
            m_shapes[m_selectionIndex] = m_shapes[m_selectionIndex] with { Twist = clamped };
            MarkProgramChanged();
            PushUndo();
        }

        return clamped;
    }

    /// <summary>Sets the TARGET's onion shell thickness directly (clamped; a no-op on a chain goal). Rebuilds the
    /// program.</summary>
    /// <param name="value">The thickness (clamped to [0, <see cref="MaxOnion"/>]).</param>
    /// <returns>The applied thickness.</returns>
    public float SetOnion(float value) {
        if (TargetIsGoal) {
            return TargetOnion;
        }

        var clamped = Math.Clamp(value: value, max: MaxOnion, min: 0f);

        if (TargetIsGhost) {
            m_ghostOnion = clamped;
            MarkProgramChanged();
        } else {
            m_shapes[m_selectionIndex] = m_shapes[m_selectionIndex] with { Onion = clamped };
            MarkProgramChanged();
            PushUndo();
        }

        return clamped;
    }

    /// <summary>Places the TARGET at an exact position (clamped to the workbench; a goal target moves the goal and
    /// re-solves — see <see cref="Move"/>). A transform update (no rebuild).</summary>
    /// <param name="position">The desired position.</param>
    /// <returns>The clamped position actually applied.</returns>
    public Vector3 SetTargetPosition(Vector3 position) {
        var clamped = m_workbench.Clamp(position: position);

        if (TargetIsGoal) {
            var chain = m_chains[m_goalChainIndex];

            m_chains[m_goalChainIndex] = (chain with { Goal = clamped });
            SolveChains();

            return clamped;
        }

        if (TargetIsGhost) {
            m_ghostPosition = clamped;
        } else {
            m_shapes[m_selectionIndex] = m_shapes[m_selectionIndex] with { Position = clamped };
        }

        Revision++;
        PushUndo();

        return clamped;
    }

    /// <summary>Sets the TARGET's orientation from Tait-Bryan degrees (yaw about +Y, pitch about +X, roll about +Z).
    /// A transform update (no rebuild).</summary>
    /// <param name="yawDegrees">The yaw in degrees.</param>
    /// <param name="pitchDegrees">The pitch in degrees.</param>
    /// <param name="rollDegrees">The roll in degrees.</param>
    public void SetTargetRotation(float yawDegrees, float pitchDegrees, float rollDegrees) {
        if (TargetIsGoal) {
            return;
        }

        const float toRadians = (MathF.PI / 180f);
        var rotation = Quaternion.Normalize(value: (
            Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: (yawDegrees * toRadians))
            * Quaternion.CreateFromAxisAngle(axis: Vector3.UnitX, angle: (pitchDegrees * toRadians))
            * Quaternion.CreateFromAxisAngle(axis: Vector3.UnitZ, angle: (rollDegrees * toRadians))));

        if (TargetIsGhost) {
            m_ghostRotation = rotation;
        } else {
            m_shapes[m_selectionIndex] = m_shapes[m_selectionIndex] with { Rotation = rotation };
        }

        Revision++;
        PushUndo();
    }

    /// <summary>Sets the TARGET's PER-AXIS scale directly (console-only precision — the pad scales uniformly), each
    /// axis clamped to the envelope; a no-op on a chain goal. Rebuilds the program.</summary>
    /// <param name="scale">The desired per-axis scale.</param>
    /// <returns>The clamped scale actually applied.</returns>
    public Vector3 SetTargetScale(Vector3 scale) {
        if (TargetIsGoal) {
            return scale;
        }

        var clamped = new Vector3(
            Math.Clamp(value: scale.X, max: MaxScale, min: MinScale),
            Math.Clamp(value: scale.Y, max: MaxScale, min: MinScale),
            Math.Clamp(value: scale.Z, max: MaxScale, min: MinScale)
        );

        if (TargetIsGhost) {
            m_ghostScale = clamped;
        } else {
            m_shapes[m_selectionIndex] = m_shapes[m_selectionIndex] with { Scale = clamped };
        }

        MarkProgramChanged();
        PushUndo();

        return clamped;
    }

    /// <summary>Dissolves the TARGET's group: every member returns to ungrouped, and — the structural invariant —
    /// every member's blend returns to plain Union (an ungrouped shape may not carry a blend). Rebuilds the program.</summary>
    /// <returns>How many shapes left the group (0 when the target was ungrouped, the ghost, or a chain goal).</returns>
    public int UngroupTarget() {
        if (TargetIsGhost || TargetIsGoal || (m_shapes[m_selectionIndex].GroupId == 0)) {
            return 0;
        }

        var groupId = m_shapes[m_selectionIndex].GroupId;
        var released = 0;

        for (var index = 0; (index < m_shapes.Count); index++) {
            if (m_shapes[index].GroupId == groupId) {
                m_shapes[index] = m_shapes[index] with { Blend = SdfBlendOp.Union, GroupId = 0, Smooth = 0f };
                released++;
            }
        }

        MarkProgramChanged();
        PushUndo();

        return released;
    }

    /// <summary>Sets the authoring intent (3D object vs 2D sprite). Rebuilds the program (the sprite intent emits a
    /// matte backdrop behind the workbench).</summary>
    /// <param name="intent">The intent.</param>
    public void SetIntent(CreatorIntent intent) {
        if (m_intent == intent) {
            return;
        }

        m_intent = intent;
        MarkProgramChanged();
    }

    /// <summary>Sets the bake style knob by name (<c>classic</c>/<c>bold</c> — unknown names fall back to classic).</summary>
    /// <param name="style">The style name.</param>
    /// <returns>The applied style name.</returns>
    public string SetBakeStyle(string style) {
        m_bakeStyle = (string.Equals(a: style, b: "bold", comparisonType: StringComparison.OrdinalIgnoreCase) ? "bold" : "classic");
        Revision++;

        return m_bakeStyle;
    }

    /// <summary>The bake preview's hardware target name (<c>dmg</c> or <c>cgb</c>).</summary>
    public string BakeTargetName => m_bakeTarget;
    /// <summary>The bake preview's overlay mode (0 = bare, 1 = palette strip + warning ticks, 2 = + tile grid).</summary>
    public int BakeOverlay => m_bakeOverlay;

    /// <summary>Sets the bake preview's hardware target (<c>dmg</c>/<c>cgb</c> — unknown names fall back to cgb).
    /// Bumps the revision, so the preview re-bakes.</summary>
    /// <param name="target">The target name.</param>
    /// <returns>The applied target name.</returns>
    public string SetBakeTarget(string target) {
        m_bakeTarget = (string.Equals(a: target, b: "dmg", comparisonType: StringComparison.OrdinalIgnoreCase) ? "dmg" : "cgb");
        Revision++;

        return m_bakeTarget;
    }

    /// <summary>Sets the bake preview's overlay mode (clamped to 0..2). Bumps the revision, so the preview re-bakes.</summary>
    /// <param name="mode">The overlay mode.</param>
    /// <returns>The applied mode.</returns>
    public int SetBakeOverlay(int mode) {
        m_bakeOverlay = Math.Clamp(value: mode, max: 2, min: 0);
        Revision++;

        return m_bakeOverlay;
    }

    // ---- the timeline (frame snapshots — the minimal animation model the bake consumes) --------------------------

    /// <summary>The timeline cursor: 0 = the REST pose (the live scene), 1..<see cref="FrameCount"/> = saved frames.</summary>
    public int CurrentFrame => m_currentFrame;
    /// <summary>How many frames are saved (past the always-present rest pose).</summary>
    public int FrameCount => m_frames.Count;
    /// <summary>Whether the frame loop is playing.</summary>
    public bool Playing => m_playing;
    /// <summary>The saved frames, in playback order (the bake's animation frame set).</summary>
    public IReadOnlyList<CreatorFrameState> Frames => m_frames;

    /// <summary>Steps the timeline cursor and APPLIES the landed frame's poses to the scene (0 restores the rest
    /// pose). Stepping away from rest captures it first, so the authored pose is never lost.</summary>
    /// <param name="direction">+1 forward, -1 back (clamped to [0, <see cref="FrameCount"/>]).</param>
    /// <returns>The new cursor.</returns>
    public int StepFrame(int direction) {
        if (!m_active) {
            return m_currentFrame;
        }

        SetFrame(index: (m_currentFrame + direction));

        return m_currentFrame;
    }

    /// <summary>Moves the timeline cursor to an exact frame and applies it (see <see cref="StepFrame"/>).</summary>
    /// <param name="index">The frame (clamped to [0, <see cref="FrameCount"/>]).</param>
    public void SetFrame(int index) {
        var target = Math.Clamp(value: index, max: m_frames.Count, min: 0);

        if (target == m_currentFrame) {
            return;
        }

        if ((m_currentFrame == 0) && (m_restPose is null)) {
            m_restPose = Snapshot(name: "rest");
        }

        m_currentFrame = target;
        ApplyPoses(frame: ((target == 0) ? m_restPose : m_frames[target - 1]));
    }

    /// <summary>RECORDS the current pose: at rest (or past the end) a new frame appends and becomes current; on a
    /// saved frame the snapshot overwrites it.</summary>
    /// <returns>The recorded frame's display index (1-based).</returns>
    public int RecordFrame() {
        if (!m_active) {
            return m_currentFrame;
        }

        if (m_currentFrame == 0) {
            // Recording FROM rest: the rest pose is the frame — capture it as both.
            m_restPose ??= Snapshot(name: "rest");
            m_frames.Add(item: Snapshot(name: $"f{m_frames.Count + 1}"));
            m_currentFrame = m_frames.Count;
        } else {
            m_frames[m_currentFrame - 1] = (Snapshot(name: m_frames[m_currentFrame - 1].Name));
        }

        Revision++;
        PushUndo();

        return m_currentFrame;
    }

    /// <summary>Deletes the CURRENT saved frame (rest is protected).</summary>
    /// <returns>Whether a frame was removed.</returns>
    public bool DeleteCurrentFrame() {
        if (!m_active || (m_currentFrame == 0)) {
            return false;
        }

        m_frames.RemoveAt(index: (m_currentFrame - 1));
        RenumberFrames();
        m_currentFrame = Math.Min(val1: m_currentFrame, val2: m_frames.Count);
        ApplyPoses(frame: ((m_currentFrame == 0) ? m_restPose : m_frames[m_currentFrame - 1]));
        Revision++;
        PushUndo();

        return true;
    }

    /// <summary>Toggles the frame-loop playback (needs at least one saved frame). Stopping restores rest.</summary>
    /// <returns>Whether playback is now running.</returns>
    public bool TogglePlayback() {
        if (!m_active || (m_frames.Count == 0)) {
            return false;
        }

        if (m_playing) {
            StopPlayback();
        } else {
            if ((m_currentFrame == 0) && (m_restPose is null)) {
                m_restPose = Snapshot(name: "rest");
            }

            m_playing = true;
            m_playClock = 0f;
            m_playCursor = 0;
        }

        return m_playing;
    }

    /// <summary>Stops playback and restores the rest pose (also the ANIMATE page's North verb).</summary>
    public void StopPlayback() {
        m_playing = false;
        m_currentFrame = 0;
        ApplyPoses(frame: m_restPose);
    }

    /// <summary>Advances playback (call once per frame with the frame delta): holds each saved frame for the
    /// configured duration, looping 1..<see cref="FrameCount"/>.</summary>
    /// <param name="deltaSeconds">The frame delta.</param>
    public void TickPlayback(float deltaSeconds) {
        if (!m_playing || (m_frames.Count == 0)) {
            return;
        }

        m_playClock += deltaSeconds;

        if (m_playClock < m_secondsPerFrame) {
            return;
        }

        m_playClock = 0f;
        m_playCursor = ((m_playCursor + 1) % m_frames.Count);
        m_currentFrame = (m_playCursor + 1);
        ApplyPoses(frame: m_frames[m_playCursor]);
    }

    /// <summary>Sets the playback hold per frame, in engine ticks at 60/s (the document's <c>frameTicks</c>).</summary>
    /// <param name="ticks">The hold (clamped 1..60).</param>
    /// <returns>The applied tick count.</returns>
    public int SetFrameTicks(int ticks) {
        var clamped = Math.Clamp(value: ticks, max: 60, min: 1);

        m_secondsPerFrame = (clamped / 60f);
        Revision++;

        return clamped;
    }

    private CreatorFrameState Snapshot(string name) {
        var poses = new List<CreatorFramePose>(capacity: m_shapes.Count);

        foreach (var shape in m_shapes) {
            poses.Add(item: new CreatorFramePose(Id: shape.Id, Position: shape.Position, Rotation: shape.Rotation, Scale: shape.Scale));
        }

        return new CreatorFrameState(Name: name, Poses: poses);
    }

    // Applies a frame's poses by shape id (missing shapes skip; scale differences rebuild — envelope-safe).
    private void ApplyPoses(CreatorFrameState? frame) {
        if (frame is null) {
            return;
        }

        var scaleChanged = false;

        foreach (var pose in frame.Poses) {
            if (!m_shapeIndexById.TryGetValue(key: pose.Id, value: out var index)) {
                continue;
            }

            scaleChanged |= (m_shapes[index].Scale != pose.Scale);
            m_shapes[index] = m_shapes[index] with {
                Position = m_workbench.Clamp(position: pose.Position),
                Rotation = pose.Rotation,
                Scale = pose.Scale,
            };
        }

        if (scaleChanged) {
            MarkProgramChanged();
        } else {
            Revision++;
        }
    }

    private void RenumberFrames() {
        for (var index = 0; (index < m_frames.Count); index++) {
            var expected = $"f{index + 1}";

            if (m_frames[index].Name.StartsWith(value: 'f') && (m_frames[index].Name != expected)) {
                m_frames[index] = (m_frames[index] with { Name = expected });
            }
        }
    }

    // ---- THE RIG (chains + IK — console verbs creator.chain/creator.pole/creator.kind/creator.gait; the RIG pad
    // page's cycle/define/delete/kind/gait/pole-nudge) ---------------------------------------------------------------

    /// <summary>Defines a new chain from the given shapes (root→tip order), capturing their CURRENT positions as the
    /// rest geometry. Rebuilds the program (a ghost marker for the new goal joins the scene).</summary>
    /// <param name="name">The player-given name (the goal-cycling/console handle); null for unnamed.</param>
    /// <param name="shapeIdsOrNames">The member shape ids or names, root→tip order (at least 2).</param>
    /// <param name="kind">"limb" or "spine" (null infers "limb" for exactly 3 shapes, else "spine").</param>
    /// <returns>The defined chain, or null when fewer than 2 shapes resolved or <see cref="MaxChains"/> is reached.</returns>
    public CreatorChainState? DefineChain(string? name, IReadOnlyList<string> shapeIdsOrNames, string? kind = null) {
        ArgumentNullException.ThrowIfNull(shapeIdsOrNames);

        if (!m_active || (m_chains.Count >= MaxChains)) {
            return null;
        }

        var ids = new List<int>(capacity: shapeIdsOrNames.Count);

        foreach (var token in shapeIdsOrNames) {
            if (ResolveShapeId(idOrName: token) is { } id) {
                ids.Add(item: id);
            }
        }

        if (TryCaptureChain(id: m_nextChainId, name: name, shapeIds: ids, kind: kind) is not { } captured) {
            return null;
        }

        m_nextChainId++;
        m_chains.Add(item: captured);
        MarkProgramChanged();
        PushUndo();

        return captured;
    }

    /// <summary>Defines a "limb" chain seeded from the pad: the SELECTED shape as root, walking forward through the
    /// next 2 placed shapes in document order (a pad-friendly stand-in for the console verb's arbitrary shape
    /// list). A no-op without a selection or without 2 further shapes to walk to.</summary>
    /// <returns>The defined chain, or null when there was no valid 3-shape run or <see cref="MaxChains"/> is reached.</returns>
    public CreatorChainState? DefineChainFromSelection() {
        if (!m_active || TargetIsGhost || TargetIsGoal || ((m_selectionIndex + 2) >= m_shapes.Count) || (m_chains.Count >= MaxChains)) {
            return null;
        }

        var ids = new[] { m_shapes[m_selectionIndex].Id, m_shapes[m_selectionIndex + 1].Id, m_shapes[m_selectionIndex + 2].Id };

        if (TryCaptureChain(id: m_nextChainId, name: null, shapeIds: ids, kind: CreatorChainState.KindLimb) is not { } captured) {
            return null;
        }

        m_nextChainId++;
        m_chains.Add(item: captured);
        MarkProgramChanged();
        PushUndo();

        return captured;
    }

    /// <summary>Deletes a chain by id or name (the console <c>creator.chain del</c> half); a no-op when nothing
    /// matches.</summary>
    /// <param name="idOrName">The chain's id (digits) or player-given name.</param>
    /// <returns>Whether a chain was removed.</returns>
    public bool DeleteChain(string idOrName) {
        var index = FindChainIndex(idOrName: idOrName);

        if (index < 0) {
            return false;
        }

        m_chains.RemoveAt(index: index);

        if (m_goalChainIndex == index) {
            m_selectionIndex = -1;
            m_goalChainIndex = -1;
        } else if (m_goalChainIndex > index) {
            m_goalChainIndex--;
        }

        MarkProgramChanged();
        PushUndo();

        return true;
    }

    /// <summary>Deletes the CURRENTLY CYCLED-TO chain on the RIG pad page (a no-op when no chain is selected).</summary>
    /// <returns>Whether a chain was removed.</returns>
    public bool DeleteCurrentChain() {
        if ((m_chainCursor < 0) || (m_chainCursor >= m_chains.Count)) {
            return false;
        }

        return DeleteChain(idOrName: m_chains[m_chainCursor].Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Cycles the RIG pad page's CURRENT-CHAIN cursor (which chain <see cref="NudgePole"/>/
    /// <see cref="ToggleCurrentChainKind"/>/<see cref="DeleteCurrentChain"/> act on) — SEPARATE from the TARGET
    /// model's goal selection (a chain can be tuned without its goal being the movement target). Wraps through
    /// "none".</summary>
    /// <param name="direction">+1 for the next chain, -1 for the previous.</param>
    /// <returns>The cursor's chain, or null (none).</returns>
    public CreatorChainState? CycleChainCursor(int direction) {
        if (m_chains.Count == 0) {
            m_chainCursor = -1;

            return null;
        }

        var next = (m_chainCursor + direction);

        m_chainCursor = ((next >= m_chains.Count) ? -1 : ((next < -1) ? (m_chains.Count - 1) : next));
        Revision++;

        return ((m_chainCursor >= 0) ? m_chains[m_chainCursor] : null);
    }

    /// <summary>The RIG pad page's current-chain cursor (see <see cref="CycleChainCursor"/>); null = none.</summary>
    public CreatorChainState? CurrentChain => (((m_chainCursor >= 0) && (m_chainCursor < m_chains.Count)) ? m_chains[m_chainCursor] : null);

    /// <summary>Sets a chain's pole (bend-direction hint) by id or name.</summary>
    /// <param name="idOrName">The chain's id or name.</param>
    /// <param name="pole">The new pole position.</param>
    /// <returns>Whether a chain was found and updated.</returns>
    public bool SetPole(string idOrName, Vector3 pole) {
        var index = FindChainIndex(idOrName: idOrName);

        if (index < 0) {
            return false;
        }

        m_chains[index] = (m_chains[index] with { Pole = pole });
        SolveChains();

        return true;
    }

    /// <summary>Nudges the CURSOR chain's pole this frame (planar, like <see cref="Move"/>) — the RIG pad page's
    /// d-pad binding.</summary>
    /// <param name="planar">The X/Z nudge.</param>
    /// <param name="deltaSeconds">The frame delta.</param>
    public void NudgePole(Vector2 planar, float deltaSeconds) {
        if ((m_chainCursor < 0) || (m_chainCursor >= m_chains.Count) || (planar == Vector2.Zero)) {
            return;
        }

        const float poleSpeed = 3.2f;
        var chain = m_chains[m_chainCursor];

        m_chains[m_chainCursor] = (chain with { Pole = (chain.Pole + (new Vector3(planar.X, 0f, planar.Y) * (poleSpeed * deltaSeconds))) });
        SolveChains();
    }

    /// <summary>Sets a chain's kind by id or name ("limb" demotes to "spine" unless it has exactly 3 shapes).</summary>
    /// <param name="idOrName">The chain's id or name.</param>
    /// <param name="kind">"limb" or "spine".</param>
    /// <returns>The applied kind, or null when no chain matched.</returns>
    public string? SetKind(string idOrName, string kind) {
        var index = FindChainIndex(idOrName: idOrName);

        if (index < 0) {
            return null;
        }

        var resolved = (string.Equals(a: kind, b: CreatorChainState.KindLimb, comparisonType: StringComparison.OrdinalIgnoreCase)
            ? CreatorChainState.KindLimb
            : CreatorChainState.KindSpine);

        if (string.Equals(a: resolved, b: CreatorChainState.KindLimb, comparisonType: StringComparison.OrdinalIgnoreCase) && (m_chains[index].ShapeIds.Count != 3)) {
            resolved = CreatorChainState.KindSpine;
        }

        m_chains[index] = (m_chains[index] with { Kind = resolved });
        SolveChains();
        PushUndo();

        return resolved;
    }

    /// <summary>Toggles the CURSOR chain's kind (the RIG pad page's West binding).</summary>
    /// <returns>The applied kind, or null when no chain is cursored.</returns>
    public string? ToggleCurrentChainKind() {
        if ((m_chainCursor < 0) || (m_chainCursor >= m_chains.Count)) {
            return null;
        }

        var chain = m_chains[m_chainCursor];
        var next = (string.Equals(a: chain.Kind, b: CreatorChainState.KindLimb, comparisonType: StringComparison.OrdinalIgnoreCase) ? CreatorChainState.KindSpine : CreatorChainState.KindLimb);

        return SetKind(idOrName: chain.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), kind: next);
    }

    /// <summary>Sweeps every chain whose NAME starts with <paramref name="prefix"/> (case-insensitive) through a
    /// flat ellipse — a planted-foot walk-cycle whimsy: chains sharing the prefix get phase offsets (the first
    /// half gets one phase, the rest the opposite half-cycle, so e.g. "leftLeg"/"rightLeg" alternate). Each swept
    /// frame is RECORDED via <see cref="RecordFrame"/>, so <c>frames 1..N</c> land exactly like any other authored
    /// animation — stride keys at frames 1-2 are the bake's walk-pair convention.</summary>
    /// <param name="prefix">The chain-name prefix selecting which chains sweep together.</param>
    /// <param name="frameCount">How many frames to record (at least 1).</param>
    /// <param name="stride">The ellipse's half-width (the step length); the height is a fixed fraction of it.</param>
    /// <returns>How many frames were recorded (0 when no chain matched the prefix).</returns>
    public int Gait(string prefix, int frameCount, float stride = 0.4f) {
        ArgumentNullException.ThrowIfNull(prefix);

        var matches = new List<int>();

        for (var index = 0; (index < m_chains.Count); index++) {
            if (m_chains[index].Name is { Length: > 0 } name && name.StartsWith(value: prefix, comparisonType: StringComparison.OrdinalIgnoreCase)) {
                matches.Add(item: index);
            }
        }

        if ((matches.Count == 0) || (frameCount < 1)) {
            return 0;
        }

        // No PushUndo here: each swept frame's RecordFrame() call below already pushes its own snapshot (one per
        // recorded frame — undo-able frame by frame, which reads better for a multi-frame authored sweep than one
        // giant all-or-nothing undo step).
        var frames = Math.Clamp(value: frameCount, max: 60, min: 1);
        var restGoals = new Vector3[matches.Count];

        for (var member = 0; (member < matches.Count); member++) {
            restGoals[member] = m_chains[matches[member]].Goal;
        }

        for (var frame = 0; (frame < frames); frame++) {
            var phase = ((frame / (float)frames) * MathF.Tau);

            for (var member = 0; (member < matches.Count); member++) {
                // Alternating half-cycle phase offset by list position: the FIRST half of matches leads, the rest
                // trail by half a cycle — "leftLeg"/"rightLeg" (2 matches) alternate cleanly; larger groups fan out
                // in the same first-half/second-half split.
                var offset = ((member < ((matches.Count + 1) / 2)) ? 0f : MathF.PI);
                var localPhase = (phase + offset);
                var chain = m_chains[matches[member]];
                var rest = restGoals[member];
                // A flat ellipse in the chain's own goal-plane (X/Z), height a quarter of the stride so the "step"
                // reads as a lift rather than a drag — planted at the bottom (cos ≈ -1) of the cycle.
                var sweep = new Vector3((MathF.Sin(localPhase) * stride), (MathF.Max(0f, MathF.Cos(localPhase)) * (stride * 0.25f)), 0f);

                m_chains[matches[member]] = (chain with { Goal = m_workbench.Clamp(position: (rest + sweep)) });
            }

            SolveChains();
            RecordFrame();
            // RecordFrame appends a NEW frame only when the cursor is at rest (m_currentFrame == 0); after
            // recording it lands ON the just-appended frame, so the cursor is walked back to rest (WITHOUT
            // restoring rest's transforms — a plain field reset, not SetFrame/ApplyPoses) so the NEXT iteration
            // appends frame 2, 3, ... instead of repeatedly overwriting frame 1.
            m_currentFrame = 0;
        }

        // Restore every swept chain's goal to its pre-gait rest (the gait AUTHORS frames; it does not leave the
        // live scene mid-stride).
        for (var member = 0; (member < matches.Count); member++) {
            m_chains[matches[member]] = (m_chains[matches[member]] with { Goal = restGoals[member] });
        }

        SolveChains();

        return frames;
    }

    /// <summary>Re-solves every defined chain against its LIVE goal/pole and writes the result into its member
    /// shapes' ordinary transforms — the settled contract: a goal move bumps <see cref="Revision"/> ONLY, never
    /// <see cref="ProgramRevision"/>, so live IK rides the dynamic-transform buffer with zero program rebuilds.
    /// Solver output lands in the SAME shape transforms <see cref="RecordFrame"/> already snapshots, which is what
    /// lets a baked pose inherit IK with zero forge changes.</summary>
    public void SolveChains() {
        var scaleChanged = false;

        foreach (var chain in m_chains) {
            var poses = chain.Solve();

            for (var member = 0; (member < chain.ShapeIds.Count); member++) {
                var shapeId = chain.ShapeIds[member];

                if (!m_shapeIndexById.TryGetValue(key: shapeId, value: out var index)) {
                    continue;
                }

                var (position, rotation) = poses[member];

                m_shapes[index] = m_shapes[index] with {
                    Position = m_workbench.Clamp(position: position),
                    Rotation = rotation,
                };
            }
        }

        if (scaleChanged) {
            MarkProgramChanged();
        } else {
            Revision++;
        }
    }

    // Resolves a chain by id (digits) or (case-insensitive) name to its index, or -1 when nothing matches.
    private int FindChainIndex(string idOrName) {
        for (var index = 0; (index < m_chains.Count); index++) {
            var chain = m_chains[index];
            var matches = (int.TryParse(s: idOrName, result: out var id) ? (chain.Id == id) : string.Equals(a: chain.Name, b: idOrName, comparisonType: StringComparison.OrdinalIgnoreCase));

            if (matches) {
                return index;
            }
        }

        return -1;
    }

    // Resolves a shape by id (digits) or (case-insensitive) name to its id, or null when nothing matches.
    private int? ResolveShapeId(string idOrName) {
        if (int.TryParse(s: idOrName, result: out var id)) {
            return (m_shapeIndexById.ContainsKey(key: id) ? id : null);
        }

        foreach (var shape in m_shapes) {
            if (string.Equals(a: shape.Name, b: idOrName, comparisonType: StringComparison.OrdinalIgnoreCase)) {
                return shape.Id;
            }
        }

        return null;
    }

    // Captures a chain's rest geometry from the CURRENT positions of the named shapes (root→tip order); returns
    // null when fewer than 2 shapes resolved (a chain needs at least one bone).
    private CreatorChainState? TryCaptureChain(int id, string? name, IReadOnlyList<int> shapeIds, string? kind) {
        if (shapeIds.Count < 2) {
            return null;
        }

        var positions = new List<Vector3>(capacity: shapeIds.Count);
        var rotations = new List<Quaternion>(capacity: shapeIds.Count);

        foreach (var shapeId in shapeIds) {
            if (!m_shapeIndexById.TryGetValue(key: shapeId, value: out var index)) {
                return null;
            }

            var shape = m_shapes[index];

            positions.Add(item: shape.Position);
            rotations.Add(item: shape.Rotation);
        }

        return CreatorChainState.Capture(id: id, kind: kind, name: name, positions: positions, rotations: rotations, shapeIds: shapeIds);
    }

    /// <summary>Lifts the scene into its <c>puck.creation.v1</c> document (the everything-as-data seam: name, intent,
    /// style, palette, and every shape with its full authored state).</summary>
    /// <returns>The document, ready for <see cref="CreationStore.Save"/> or the bake.</returns>
    public CreationDocument ToDocument() {
        var palette = new List<PaletteEntryDocument>(capacity: PaletteSize);

        foreach (var entry in m_palette) {
            palette.Add(item: new PaletteEntryDocument(Albedo: entry.Albedo, Emissive: entry.Emissive, Shininess: entry.Shininess, Specular: entry.Specular));
        }

        var shapes = new List<ShapeDocument>(capacity: m_shapes.Count);

        foreach (var shape in m_shapes) {
            shapes.Add(item: new ShapeDocument(
                Blend: shape.Blend,
                Group: shape.GroupId,
                Id: shape.Id,
                Material: shape.MaterialIndex,
                Mirror: shape.Mirror,
                Name: shape.Name,
                Onion: shape.Onion,
                Position: shape.Position,
                Rotation: shape.Rotation,
                Scale: shape.Scale,
                Smooth: shape.Smooth,
                Twist: shape.Twist,
                Type: shape.Type
            ));
        }

        List<FrameDocument>? frames = null;

        if (m_frames.Count > 0) {
            frames = new List<FrameDocument>(capacity: m_frames.Count);

            foreach (var frame in m_frames) {
                var transforms = new List<FrameTransformDocument>(capacity: frame.Poses.Count);

                foreach (var pose in frame.Poses) {
                    transforms.Add(item: new FrameTransformDocument(Id: pose.Id, Position: pose.Position, Rotation: pose.Rotation, Scale: pose.Scale));
                }

                frames.Add(item: new FrameDocument(Name: frame.Name, Transforms: transforms));
            }
        }

        List<ChainDocument>? chains = null;

        if (m_chains.Count > 0) {
            chains = new List<ChainDocument>(capacity: m_chains.Count);

            foreach (var chain in m_chains) {
                chains.Add(item: new ChainDocument(Goal: chain.Goal, Id: chain.Id, Kind: chain.Kind, Name: chain.Name, Pole: chain.Pole, Shapes: chain.ShapeIds));
            }
        }

        return new CreationDocument(
            BakeStyle: m_bakeStyle,
            Chains: chains,
            Frames: frames,
            Intent: m_intent,
            Name: m_name,
            Palette: palette,
            Schema: CreationDocument.CurrentSchema,
            Shapes: shapes
        );
    }

    /// <summary>Replaces the scene's content from a NORMALIZED document (see <see cref="CreationStore.Load"/>):
    /// shapes clamp into the workbench, ids/groups resequence their counters, the selection clears. Rebuilds the
    /// program.</summary>
    /// <param name="document">The normalized document.</param>
    /// <returns>How many shapes loaded (the pool capacity truncates a larger document).</returns>
    public int LoadDocument(CreationDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        m_shapes.Clear();
        m_selectionIndex = -1;
        m_previousSelectionIndex = -1;
        m_name = (document.Name ?? "creation");
        m_bakeStyle = (document.BakeStyle ?? "classic");
        m_intent = (document.Intent ?? CreatorIntent.Object);

        if (document.Palette is { } palette) {
            for (var index = 0; ((index < palette.Count) && (index < PaletteSize)); index++) {
                var entry = palette[index];
                var defaults = new SdfMaterial(Albedo: entry.Albedo);

                m_palette[index] = (defaults with {
                    Emissive = (entry.Emissive ?? defaults.Emissive),
                    Shininess = (entry.Shininess ?? defaults.Shininess),
                    Specular = (entry.Specular ?? defaults.Specular),
                });
            }
        }

        var maxId = -1;
        var maxGroup = 0;

        foreach (var shape in (document.Shapes ?? [])) {
            if (m_shapes.Count >= Capacity) {
                break;
            }

            m_shapes.Add(item: new CreatorShapeState(
                Blend: (shape.Blend ?? SdfBlendOp.Union),
                GroupId: (shape.Group ?? 0),
                Id: shape.Id,
                MaterialIndex: (shape.Material ?? 0),
                Mirror: (shape.Mirror ?? false),
                Name: shape.Name,
                Onion: (shape.Onion ?? 0f),
                Position: m_workbench.Clamp(position: shape.Position),
                Rotation: shape.Rotation,
                Scale: shape.Scale,
                Smooth: (shape.Smooth ?? 0f),
                Twist: (shape.Twist ?? 0f),
                Type: shape.Type
            ));
            maxId = Math.Max(val1: maxId, val2: shape.Id);
            maxGroup = Math.Max(val1: maxGroup, val2: (shape.Group ?? 0));
        }

        RebuildShapeIndex();
        m_nextShapeId = (maxId + 1);
        m_nextGroupId = (maxGroup + 1);

        // The timeline reloads with the shapes (the cursor resets to rest; the rest pose is the loaded live scene).
        m_frames.Clear();
        m_currentFrame = 0;
        m_restPose = null;
        m_playing = false;

        foreach (var frame in (document.Frames ?? [])) {
            var poses = new List<CreatorFramePose>(capacity: frame.Transforms.Count);

            foreach (var transform in frame.Transforms) {
                poses.Add(item: new CreatorFramePose(Id: transform.Id, Position: transform.Position, Rotation: transform.Rotation, Scale: transform.Scale));
            }

            m_frames.Add(item: new CreatorFrameState(Name: frame.Name, Poses: poses));
        }

        // Chains RECAPTURE their rest geometry from the just-loaded shapes' current positions (see ChainDocument's
        // remarks) — never trust persisted rest data, only the shape ids + kind + live goal/pole.
        m_chains.Clear();
        m_goalChainIndex = -1;

        var maxChainId = 0;

        foreach (var chain in (document.Chains ?? [])) {
            if (m_chains.Count >= MaxChains) {
                break;
            }

            if (TryCaptureChain(id: chain.Id, name: chain.Name, shapeIds: chain.Shapes, kind: chain.Kind) is { } captured) {
                m_chains.Add(item: (captured with {
                    Goal = (chain.Goal ?? captured.Goal),
                    Pole = (chain.Pole ?? captured.Pole),
                }));
                maxChainId = Math.Max(val1: maxChainId, val2: chain.Id);
            }
        }

        m_nextChainId = (maxChainId + 1);

        // Deliberately NOT re-solved here: the loaded shape transforms already ARE the correct, exact pose (either
        // a freshly recaptured chain's untouched rest, or whatever a live-edited goal had posed before saving) —
        // solving again would run the loaded goal back through the IK math for no behavioral gain and, since a
        // two-bone solve is not perfectly idempotent at floating-point precision, could drift a byte-identical
        // save→load→save round-trip by a few ULPs. The NEXT live goal move re-solves normally.
        MarkProgramChanged();
        m_history.Reset(initial: CaptureSnapshot());

        return m_shapes.Count;
    }

    // Group-scope transforms apply only when the scope is on AND the selected shape actually belongs to a group.
    private bool GroupScopeApplies() =>
        (m_groupScope && !TargetIsGhost && (m_shapes[m_selectionIndex].GroupId != 0));

    private Vector3 GroupCentroid(int groupId) {
        var sum = Vector3.Zero;
        var count = 0;

        foreach (var shape in m_shapes) {
            if (shape.GroupId == groupId) {
                sum += shape.Position;
                count++;
            }
        }

        return ((count > 0) ? (sum / count) : m_workbench.MidPoint);
    }

    /// <summary>Lifts the currently PLACED shapes into a self-contained, recentered <see cref="AvatarDefinition"/> —
    /// the seam the forge consumes to bake a spritesheet and a playable ROM from the player's creation. The live
    /// ghost (not yet placed) is excluded; an empty scene yields a single unit sphere so the forge never renders
    /// nothing.</summary>
    /// <returns>The player's avatar in its own local frame.</returns>
    public AvatarDefinition ExportAvatar() {
        var shapes = new List<AvatarShape>(capacity: m_shapes.Count);

        foreach (var placed in m_shapes) {
            shapes.Add(item: new AvatarShape(
                Position: placed.Position,
                Rotation: placed.Rotation,
                Scale: placed.Scale,
                Type: placed.Type
            ));
        }

        return AvatarDefinition.FromPlacedShapes(shapes: shapes);
    }

    // Every program-content change also counts as a plain revision (the preview re-bakes on either).
    private void MarkProgramChanged() {
        ProgramRevision++;
        Revision++;
    }

    // Resyncs the shape id -> index map with the current m_shapes contents — call after ANY add/remove/clear (an
    // in-place `m_shapes[i] = shape with {...}` never needs this, since ids and positions don't move). Cheap enough
    // to call unconditionally on every structural edit: those are rare (place/delete/undo/load), never per-frame.
    private void RebuildShapeIndex() {
        m_shapeIndexById.Clear();

        for (var index = 0; (index < m_shapes.Count); index++) {
            m_shapeIndexById[m_shapes[index].Id] = index;
        }
    }

    // ---- undo/redo (EditHistory<CreatorSnapshot> over the whole authored state) --------------------------------
    //
    // EditHistory<T>.Push's contract (see its own remarks) is "the snapshot AFTER the completed edit" — the pushed
    // value becomes the new undo-stack top, and a later TryUndo steps back to whatever was pushed BEFORE it. So
    // every push in this class happens AFTER its mutation, never before (pushing before would duplicate the
    // constructor's baseline into the first edit's "before" state and shift every subsequent undo off by one — a
    // bug caught by the scripted verification session, see the report).

    /// <summary>Marks a continuous edit's (a drag's) touch for this frame: on the drag's FIRST touch (the start
    /// edge) this returns <see langword="true"/> so the caller pushes the post-mutation snapshot exactly once;
    /// every subsequent frame of the SAME drag returns <see langword="false"/> (the drag stays open across frames
    /// until <see cref="EndInputFrame"/> notices it went untouched, coalescing the whole drag onto ONE undo step).
    /// Call this BEFORE mutating (it only tracks drag state); push the AFTER snapshot only when it returns true.</summary>
    /// <returns>Whether the caller must push a snapshot once its mutation completes.</returns>
    private bool TouchDrag() {
        var isDragStart = !m_dragOpen;

        m_dragOpen = true;
        m_dragTouchedThisFrame = true;

        return isDragStart;
    }

    /// <summary>Pushes a snapshot for a DISCRETE edit (one push per call, unconditionally) — call AFTER mutating.
    /// Also closes any open drag first, so a discrete edit mid-drag does not merge into it.</summary>
    private void PushUndo() {
        m_dragOpen = false;
        m_history.Push(snapshot: CaptureSnapshot());
    }

    /// <summary>Completes a <see cref="TouchDrag"/> pair: call AFTER mutating, passing back what <see cref="TouchDrag"/>
    /// returned. Pushes the post-mutation snapshot only when that drag frame was the START edge — every later frame
    /// of the same drag is a no-op here, which is what coalesces the whole drag onto one undo step.</summary>
    /// <param name="dragStarted">The value <see cref="TouchDrag"/> returned for this same edit.</param>
    private void PushIfDragStarted(bool dragStarted) {
        if (dragStarted) {
            m_history.Push(snapshot: CaptureSnapshot());
        }
    }

    /// <summary>Closes a drag whose continuous verb did not fire this frame (the stick returned to center, the
    /// button released) — call ONCE per frame after every input verb for the frame has run (see
    /// <see cref="CreatorController.Advance"/>). A drag that IS still being touched stays open across the call.</summary>
    public void EndInputFrame() {
        if (m_dragOpen && !m_dragTouchedThisFrame) {
            m_dragOpen = false;
        }

        m_dragTouchedThisFrame = false;
    }

    // Lifts the WHOLE authored state (shapes, palette, frames, chains, name/style/intent — everything ToDocument
    // would persist, plus the selection/cursor state so undo/redo feels seamless rather than just content-correct)
    // into one immutable snapshot value.
    private CreatorSnapshot CaptureSnapshot() {
        return new CreatorSnapshot(
            BakeStyle: m_bakeStyle,
            Chains: [.. m_chains],
            CurrentFrame: m_currentFrame,
            Frames: [.. m_frames],
            GhostBlend: m_ghostBlend,
            GhostMaterial: m_ghostMaterial,
            GhostMirror: m_ghostMirror,
            GhostOnion: m_ghostOnion,
            GhostPosition: m_ghostPosition,
            GhostRotation: m_ghostRotation,
            GhostScale: m_ghostScale,
            GhostSmooth: m_ghostSmooth,
            GhostTwist: m_ghostTwist,
            GhostType: m_ghostType,
            Intent: m_intent,
            Name: m_name,
            NextChainId: m_nextChainId,
            NextGroupId: m_nextGroupId,
            NextShapeId: m_nextShapeId,
            Palette: [.. m_palette],
            PreviousSelectionIndex: m_previousSelectionIndex,
            RestPose: m_restPose,
            SelectionIndex: m_selectionIndex,
            Shapes: [.. m_shapes]
        );
    }

    // Restores a previously captured snapshot wholesale (undo/redo's shared apply path) and rebuilds the program —
    // any of shapes/palette/frames/chains may have changed, so this always treats the program as dirty.
    private void RestoreSnapshot(CreatorSnapshot snapshot) {
        m_shapes.Clear();
        m_shapes.AddRange(collection: snapshot.Shapes);
        RebuildShapeIndex();

        for (var index = 0; ((index < snapshot.Palette.Count) && (index < PaletteSize)); index++) {
            m_palette[index] = snapshot.Palette[index];
        }

        m_chains.Clear();
        m_chains.AddRange(collection: snapshot.Chains);
        m_frames.Clear();
        m_frames.AddRange(collection: snapshot.Frames);
        m_currentFrame = snapshot.CurrentFrame;
        m_restPose = snapshot.RestPose;
        m_selectionIndex = snapshot.SelectionIndex;
        m_previousSelectionIndex = snapshot.PreviousSelectionIndex;
        m_nextShapeId = snapshot.NextShapeId;
        m_nextGroupId = snapshot.NextGroupId;
        m_nextChainId = snapshot.NextChainId;
        m_name = snapshot.Name;
        m_bakeStyle = snapshot.BakeStyle;
        m_intent = snapshot.Intent;
        m_ghostType = snapshot.GhostType;
        m_ghostPosition = snapshot.GhostPosition;
        m_ghostRotation = snapshot.GhostRotation;
        m_ghostScale = snapshot.GhostScale;
        m_ghostMaterial = snapshot.GhostMaterial;
        m_ghostBlend = snapshot.GhostBlend;
        m_ghostSmooth = snapshot.GhostSmooth;
        m_ghostMirror = snapshot.GhostMirror;
        m_ghostTwist = snapshot.GhostTwist;
        m_ghostOnion = snapshot.GhostOnion;
        m_goalChainIndex = -1;
        m_chainCursor = -1;
        MarkProgramChanged();
    }

    // The complete authored-state snapshot the undo/redo ring stores — every field ToDocument would persist plus
    // enough cursor state (selection, next-id counters, the rest pose) that restoring one feels like a true
    // time-travel rather than a content-only revert. Immutable by construction (record + IReadOnlyList members
    // populated from array/list COPIES at capture time), so a later live edit can never corrupt a stored snapshot.
    private sealed record CreatorSnapshot(
        IReadOnlyList<CreatorShapeState> Shapes,
        IReadOnlyList<SdfMaterial> Palette,
        IReadOnlyList<CreatorFrameState> Frames,
        IReadOnlyList<CreatorChainState> Chains,
        int CurrentFrame,
        CreatorFrameState? RestPose,
        int SelectionIndex,
        int PreviousSelectionIndex,
        int NextShapeId,
        int NextGroupId,
        int NextChainId,
        string Name,
        string BakeStyle,
        CreatorIntent Intent,
        AvatarPrimitive GhostType,
        Vector3 GhostPosition,
        Quaternion GhostRotation,
        Vector3 GhostScale,
        int GhostMaterial,
        SdfBlendOp GhostBlend,
        float GhostSmooth,
        bool GhostMirror,
        float GhostTwist,
        float GhostOnion
    );

    /// <summary>A fresh copy of the default 16-slot palette: a golden-ratio hue sweep (the classic placed-pool
    /// look) — well-separated hues for small index counts, deterministic, and editable via the console's palette
    /// verb. Shared with the bake planner so a document that carries no palette bakes the same materials the live
    /// scene shows.</summary>
    /// <returns>The default palette (one array per call — callers may mutate their copy).</returns>
    public static SdfMaterial[] DefaultPalette() {
        var palette = new SdfMaterial[PaletteSize];

        for (var index = 0; (index < PaletteSize); index++) {
            palette[index] = new SdfMaterial(Albedo: PaletteHue(index: index));
        }

        return palette;
    }

    private static Vector3 PaletteHue(int index) {
        var hue = ((index * 0.61803399f) % 1f);
        var h6 = (hue * 6f);
        var x = (1f - MathF.Abs(((h6 % 2f) - 1f)));
        var (r, g, b) = ((int)h6 switch {
            0 => (1f, x, 0f),
            1 => (x, 1f, 0f),
            2 => (0f, 1f, x),
            3 => (0f, x, 1f),
            4 => (x, 0f, 1f),
            _ => (1f, 0f, x),
        });

        return new Vector3((0.35f + (0.5f * r)), (0.35f + (0.5f * g)), (0.35f + (0.5f * b)));
    }
}
