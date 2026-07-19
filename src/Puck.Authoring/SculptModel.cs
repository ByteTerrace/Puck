using System.Numerics;
using System.Text.Json;
using Puck.SdfVm;

namespace Puck.Authoring;

/// <summary>One authored shape in a sculpt model — the live, non-nullable twin of <see cref="ShapeDocument"/>.
/// Every field is baked into whatever program a consumer emits, so any change is a preview-rebuild-scale event.
/// One revision counter drives all consumers; there is no transform/program split.</summary>
/// <param name="Id">The shape's stable id (unique within the model; survives deletes/reorders — names and
/// selection key on it).</param>
/// <param name="Name">An optional player-given name; null until named.</param>
/// <param name="Type">The primitive this shape draws (the canonical dimensions live in <see cref="CreationGeometry"/>).</param>
/// <param name="Position">The shape's position (workbench-local space).</param>
/// <param name="Rotation">The shape's orientation.</param>
/// <param name="Scale">The shape's per-axis scale.</param>
/// <param name="MaterialIndex">The palette slot this shape colors from (0..<see cref="CreationDocument.PaletteSize"/>-1).</param>
/// <param name="Blend">How this shape combines with the shapes before it; non-Union blends are honored only within
/// a group (group id != 0) — the structural invariant that keeps blends inside instance bounds.</param>
/// <param name="Smooth">The blend radius for the smooth blend variants (0 for the hard ops).</param>
/// <param name="GroupId">The composition group this shape belongs to (0 = ungrouped).</param>
/// <param name="Mirror">Whether the shape's local field mirrors across its local X=0 plane.</param>
/// <param name="Twist">The shape's local twist rate about Y (clamped to ±<see cref="ShapeDocument.MaxTwist"/>).</param>
/// <param name="Bend">The shape's local bend rate about Y (clamped to ±<see cref="ShapeDocument.MaxBend"/>).</param>
/// <param name="Dilate">The shape's inflation radius (clamped to [0, <see cref="ShapeDocument.MaxDilate"/>]).</param>
/// <param name="Onion">The shape's shell thickness (clamped to [0, <see cref="ShapeDocument.MaxOnion"/>]; 0 = solid).</param>
public readonly record struct SculptShape(
    int Id,
    string? Name,
    AvatarPrimitive Type,
    Vector3 Position,
    Quaternion Rotation,
    Vector3 Scale,
    int MaterialIndex,
    SdfBlendOp Blend,
    float Smooth,
    int GroupId,
    bool Mirror = false,
    float Twist = 0f,
    float Bend = 0f,
    float Dilate = 0f,
    float Onion = 0f
);

/// <summary>One shape's pose inside a timeline frame (matched back to the shape by id on apply — a pose whose shape
/// was deleted is skipped harmlessly).</summary>
/// <param name="Id">The shape id the pose belongs to.</param>
/// <param name="Position">The pose position.</param>
/// <param name="Rotation">The pose orientation.</param>
/// <param name="Scale">The pose scale.</param>
public readonly record struct SculptPose(int Id, Vector3 Position, Quaternion Rotation, Vector3 Scale);

/// <summary>One timeline frame: a named FULL snapshot of every shape's transform. The deliberate minimal animation
/// model: hold-style playback, no per-shape keys, no interpolation, by design.</summary>
/// <param name="Name">The frame's name (auto-named <c>f1</c>, <c>f2</c>… on record).</param>
/// <param name="Poses">Every shape's pose at record time.</param>
public sealed record SculptFrame(string Name, IReadOnlyList<SculptPose> Poses);

/// <summary>
/// The sculpt model — a <c>puck.creation.v1</c> document being edited at frame rate: shapes, a 16-slot palette,
/// hold-style timeline frames, and an IK chain rig, with a bounded local <see cref="EditHistory{T}"/> undo ring.
/// Presentation-pure and renderer-blind: no GPU types, no program revisions — consumers watch <see cref="Revision"/>
/// and re-emit through <see cref="CreationGeometry"/> (so what a preview draws IS what a committed stamp draws,
/// byte-for-byte). The behavioral contract (edit rates, clamps, blend-group coercion, timeline and IK semantics,
/// the push-after undo/drag-coalescing protocol) is preserved deliberately: a persisted creation's pose must
/// re-derive identically.
/// </summary>
/// <remarks>The TARGET model: every edit verb acts on the SELECTED shape when one exists, else on the BRUSH — the
/// style/transform state the NEXT added shape inherits. The ghost/brush mechanism drives placement previews; the
/// brush itself never renders. When a chain GOAL is the target (<see cref="TargetIsGoal"/>), movement drives the goal and
/// the chain re-solves live. Single-threaded like every input-fold type: mutators run in the command pump's apply
/// window, <see cref="TickPlayback"/>/<see cref="EndInputFrame"/> on the produce path — one window-pump thread.</remarks>
public sealed class SculptModel {
    /// <summary>The blend cycle in AUTHORING order (hard/smooth pairs adjacent), not raw enum order.</summary>
    public static readonly SdfBlendOp[] BlendCycle = [
        SdfBlendOp.Union,
        SdfBlendOp.SmoothUnion,
        SdfBlendOp.Subtraction,
        SdfBlendOp.SmoothSubtraction,
        SdfBlendOp.Intersection,
        SdfBlendOp.SmoothIntersection,
        SdfBlendOp.Xor,
    ];

    /// <summary>The smallest per-axis scale an authored shape clamps to.</summary>
    public const float MinScale = 0.2f;
    /// <summary>The largest per-axis scale an authored shape clamps to.</summary>
    public const float MaxScale = 3.0f;
    /// <summary>The primitive count the cycle wraps over (the <see cref="AvatarPrimitive"/> set).</summary>
    public const int PrimitiveCount = 7;
    /// <summary>The undo ring's bounded snapshot count.</summary>
    public const int HistoryCapacity = 64;
    /// <summary>The most chains a model defines.</summary>
    public const int MaxChains = 16;

    // The workbench-local authoring bound every position clamps into. A CONTRACT of the preview/stamp path, not a
    // preference: a shape flung far from the origin would blow the stamp's instance bound (reach is data-derived)
    // into a screen-filling evaluation, and the orbit camera frames this envelope. Persisted creations were
    // authored inside a bound of this scale, so the clamp never bites a legitimate load.
    private const float BoundHalfExtent = 6f;
    private const float BoundMinY = -1f;
    private const float BoundMaxY = 10f;
    // Where a brand-new shape lands when the caller names no position: just above the workbench origin, so the
    // first Add is visible on the bench rather than buried in the ground plane.
    private static readonly Vector3 s_spawnPosition = new(x: 0f, y: 0.7f, z: 0f);

    private readonly int m_shapeCapacity;
    private readonly List<SculptShape> m_shapes = [];
    private readonly Dictionary<int, int> m_shapeIndexById = [];
    private readonly SdfMaterial[] m_palette = DefaultPalette();
    private int m_nextShapeId = 1;
    private int m_nextGroupId = 1;
    private int m_selectionIndex = -1;
    private int m_previousSelectionIndex = -1;
    private string m_name = "creation";
    private string m_bakeStyle = "classic";
    private CreatorIntent m_intent = CreatorIntent.Object;
    // THE TIMELINE: saved frames display as 1..Count; cursor 0 is the REST pose — the live authored model, captured
    // implicitly the first time the timeline steps away from it and restored on return/stop. Playback holds each
    // frame for m_secondsPerFrame (no interpolation, by design).
    private readonly List<SculptFrame> m_frames = [];
    private int m_currentFrame;
    private SculptFrame? m_restPose;
    private bool m_playing;
    private float m_playClock;
    private int m_playCursor;
    private float m_secondsPerFrame = (8f / 60f);
    // THE BRUSH — the style/transform the next added shape inherits, and the target of style verbs while nothing is
    // selected. The ghost mechanism drives placement previews; the brush itself never renders.
    private AvatarPrimitive m_brushType;
    private Quaternion m_brushRotation = Quaternion.Identity;
    private Vector3 m_brushScale = Vector3.One;
    private int m_brushMaterial;
    private SdfBlendOp m_brushBlend = SdfBlendOp.Union;
    private float m_brushSmooth;
    private bool m_brushMirror;
    private float m_brushTwist;
    private float m_brushBend;
    private float m_brushDilate;
    private float m_brushOnion;
    // THE RIG (chains + IK): defined chains plus the two cursors — the goal-target index (movement drives that
    // chain's goal) and the rig-page chain cursor (pole/kind/delete act on it) — deliberately separate: a chain
    // tunes without its goal being the movement target.
    private readonly List<SculptChain> m_chains = [];
    private int m_nextChainId = 1;
    private int m_goalChainIndex = -1;
    private int m_chainCursor = -1;
    // Per-solve pose scratch, grown to the longest chain and reused (a held goal drag re-solves every frame).
    private (Vector3 Position, Quaternion Rotation)[] m_solveScratch = [];
    // Undo/redo: a bounded ring of immutable whole-model snapshots. Continuous edits coalesce onto ONE snapshot per
    // drag (pushed on the drag's START edge) via m_dragOpen/m_dragTouchedThisFrame; discrete edits each push.
    private readonly EditHistory<SculptSnapshot> m_history;
    private bool m_dragOpen;
    private bool m_dragTouchedThisFrame;
    // Carried, editor-opaque document members: the model authors none of these, so it stashes whatever the loaded
    // document carried and hands it straight back on save, byte-for-byte. NEVER part of SculptSnapshot — undo must
    // not touch what no verb here can edit.
    private IReadOnlyList<CreationCameraDocument>? m_loadedCameras;
    private CreationBehaviorDocument? m_loadedBehavior;
    private IReadOnlyList<TextRunDocument>? m_loadedTextRuns;
    private IDictionary<string, JsonElement>? m_loadedExtensions;

    /// <summary>Initializes an empty model under a shape budget.</summary>
    /// <param name="shapeCapacity">The consumer's per-creation shape budget — <see cref="StampShapeCount"/> (authored
    /// shapes plus any carried text runs' glyphs) never exceeds it; <see cref="AddShape"/>/<see cref="DuplicateTarget"/>
    /// refuse past it.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="shapeCapacity"/> is not positive.</exception>
    public SculptModel(int shapeCapacity) {
        ArgumentOutOfRangeException.ThrowIfLessThan(other: 1, value: shapeCapacity);

        m_shapeCapacity = shapeCapacity;
        m_history = new EditHistory<SculptSnapshot>(capacity: HistoryCapacity, initial: CaptureSnapshot());
    }

    /// <summary>Bumps on EVERY visible mutation — a preview consumer re-emits when it moves.</summary>
    public int Revision { get; private set; }

    /// <summary>The authored shapes, in document order (the order group blends evaluate in).</summary>
    public IReadOnlyList<SculptShape> Shapes => m_shapes;
    /// <summary>The material palette (16 slots); shapes reference entries by index.</summary>
    public IReadOnlyList<SdfMaterial> Palette => m_palette;
    /// <summary>The shape budget passed at construction (the consumer's per-stamp cap).</summary>
    public int ShapeCapacity => m_shapeCapacity;
    /// <summary>The model's current stamp cost: authored shapes plus every carried text run's glyph count — the
    /// number a placement of the committed creation emits (the HUD's budget readout).</summary>
    public int StampShapeCount {
        get {
            var count = m_shapes.Count;

            foreach (var run in (m_loadedTextRuns ?? [])) {
                count += run.GlyphCount;
            }

            return count;
        }
    }
    /// <summary>The selected shape's index (-1 = none; the brush is then the style target, UNLESS a chain goal is
    /// selected — see <see cref="TargetIsGoal"/>).</summary>
    public int SelectionIndex => m_selectionIndex;
    /// <summary>Whether style/transform verbs currently target the brush (no shape AND no chain goal selected).</summary>
    public bool TargetIsBrush => (!TargetIsGoal && ((m_selectionIndex < 0) || (m_selectionIndex >= m_shapes.Count)));
    /// <summary>The selected shape, when there is one.</summary>
    public SculptShape? SelectedShape => (TargetIsBrush ? null : m_shapes[m_selectionIndex]);
    /// <summary>Whether the SELECTED target is a chain GOAL rather than a shape or the brush — movement then drives
    /// the goal and the chain re-solves live.</summary>
    public bool TargetIsGoal => ((m_goalChainIndex >= 0) && (m_goalChainIndex < m_chains.Count));
    /// <summary>The chain whose goal is the current target, when <see cref="TargetIsGoal"/>.</summary>
    public SculptChain? TargetGoalChain => (TargetIsGoal ? m_chains[m_goalChainIndex] : null);
    /// <summary>The chains currently defined, in definition order.</summary>
    public IReadOnlyList<SculptChain> Chains => m_chains;
    /// <summary>The rig-page chain cursor's chain (see <see cref="CycleChainCursor"/>); null = none.</summary>
    public SculptChain? CurrentChain => (((m_chainCursor >= 0) && (m_chainCursor < m_chains.Count)) ? m_chains[m_chainCursor] : null);
    /// <summary>The creation's name (the document handle; the committed row id names the world asset).</summary>
    public string Name => m_name;
    /// <summary>The carried bake-style knob (<c>classic</c>/<c>bold</c>).</summary>
    public string BakeStyle => m_bakeStyle;
    /// <summary>The authoring intent (carried; a world stamp ignores it, a bake consumes it).</summary>
    public CreatorIntent Intent => m_intent;
    /// <summary>Whether an undo step is available on the local ring.</summary>
    public bool CanUndo => m_history.CanUndo;
    /// <summary>Whether a redo step is available on the local ring.</summary>
    public bool CanRedo => m_history.CanRedo;
    /// <summary>The local ring's retained snapshot count (the HUD's ring readout).</summary>
    public int HistoryCount => m_history.Count;
    /// <summary>The timeline cursor: 0 = the REST pose (the live model), 1..<see cref="FrameCount"/> = saved frames.</summary>
    public int CurrentFrame => m_currentFrame;
    /// <summary>How many frames are saved (past the always-present rest pose).</summary>
    public int FrameCount => m_frames.Count;
    /// <summary>Whether the frame loop is playing.</summary>
    public bool Playing => m_playing;
    /// <summary>The saved frames, in playback order.</summary>
    public IReadOnlyList<SculptFrame> Frames => m_frames;
    /// <summary>The TARGET's blend op (the selected shape's, else the brush's).</summary>
    public SdfBlendOp TargetBlend => (TargetIsBrush ? m_brushBlend : m_shapes[m_selectionIndex].Blend);
    /// <summary>The TARGET's palette slot.</summary>
    public int TargetMaterialIndex => (TargetIsBrush ? m_brushMaterial : m_shapes[m_selectionIndex].MaterialIndex);
    /// <summary>The TARGET's primitive (the selected shape's, else the brush's — what the next Add draws).</summary>
    public AvatarPrimitive TargetType => (TargetIsBrush ? m_brushType : m_shapes[m_selectionIndex].Type);
    /// <summary>The TARGET's mirror flag.</summary>
    public bool TargetMirror => (TargetIsBrush ? m_brushMirror : m_shapes[m_selectionIndex].Mirror);
    /// <summary>The TARGET's smooth-blend radius.</summary>
    public float TargetSmooth => (TargetIsBrush ? m_brushSmooth : m_shapes[m_selectionIndex].Smooth);
    /// <summary>The TARGET's per-axis scale (the selected shape's, else the brush's — what the next add inherits).</summary>
    public Vector3 TargetScale => (TargetIsBrush ? m_brushScale : m_shapes[m_selectionIndex].Scale);
    /// <summary>The TARGET's position: the selected shape's, the targeted chain goal's, or null on the brush (which
    /// has no place until it becomes a shape).</summary>
    public Vector3? TargetPosition => (TargetIsGoal ? m_chains[m_goalChainIndex].Goal : (TargetIsBrush ? null : m_shapes[m_selectionIndex].Position));

    /// <summary>Adds a shape: the brush's primitive (or an explicit one) with the brush's style, at an explicit
    /// position or the spawn point, then selects it. The brush's palette slot advances so consecutive adds read as
    /// distinct siblings; a non-Union brush blend coerces the new shape into its own group-of-one (the structural
    /// invariant: blends only ever act within a group).</summary>
    /// <param name="type">The primitive, or null for the brush's.</param>
    /// <param name="position">The position (clamped into the workbench bound), or null for the spawn point.</param>
    /// <returns>The added shape, or null when the shape budget is spent.</returns>
    public SculptShape? AddShape(AvatarPrimitive? type = null, Vector3? position = null) {
        if (StampShapeCount >= m_shapeCapacity) {
            return null;
        }

        var shape = new SculptShape(
            Bend: m_brushBend,
            Blend: m_brushBlend,
            Dilate: m_brushDilate,
            GroupId: ((m_brushBlend != SdfBlendOp.Union) ? m_nextGroupId++ : 0),
            Id: m_nextShapeId++,
            MaterialIndex: m_brushMaterial,
            Mirror: m_brushMirror,
            Name: null,
            Onion: m_brushOnion,
            Position: ClampLocal(position: (position ?? s_spawnPosition)),
            Rotation: m_brushRotation,
            Scale: m_brushScale,
            Smooth: m_brushSmooth,
            Twist: m_brushTwist,
            Type: (type ?? m_brushType)
        );

        m_shapes.Add(item: shape);
        RebuildShapeIndex();

        if (type is { } explicitType) {
            m_brushType = explicitType;
        }

        // The next add reads as a sibling: advance the brush's palette slot so consecutive adds stay visually
        // distinct without any palette work by the player.
        m_brushMaterial = ((m_brushMaterial + 1) % CreationDocument.PaletteSize);
        m_previousSelectionIndex = m_selectionIndex;
        m_selectionIndex = (m_shapes.Count - 1);
        m_goalChainIndex = -1;
        Revision++;
        PushUndo();

        return shape;
    }

    /// <summary>Duplicates the SELECTED shape in place (nudged aside so the twin reads) and selects the twin. A
    /// duplicate of a grouped member joins the SAME group.</summary>
    /// <returns>Whether a shape was added (false with no selection or a spent budget).</returns>
    public bool DuplicateTarget() {
        if (TargetIsBrush || TargetIsGoal || (StampShapeCount >= m_shapeCapacity)) {
            return false;
        }

        var source = m_shapes[m_selectionIndex];

        m_shapes.Add(item: source with {
            Id = m_nextShapeId++,
            Name = null,
            Position = ClampLocal(position: (source.Position + new Vector3(x: 0.35f, y: 0f, z: 0f))),
        });
        RebuildShapeIndex();
        m_previousSelectionIndex = m_selectionIndex;
        m_selectionIndex = (m_shapes.Count - 1);
        Revision++;
        PushUndo();

        return true;
    }

    /// <summary>Deletes the SELECTED shape (a no-op when nothing is selected). The selection clears.</summary>
    /// <returns>Whether a shape was removed.</returns>
    public bool DeleteSelected() {
        if (TargetIsBrush || TargetIsGoal) {
            return false;
        }

        m_shapes.RemoveAt(index: m_selectionIndex);
        RebuildShapeIndex();
        m_selectionIndex = -1;
        m_previousSelectionIndex = -1;
        Revision++;
        PushUndo();

        return true;
    }

    /// <summary>Selects a shape by id or (case-insensitive) name.</summary>
    /// <param name="idOrName">The shape's id (digits) or player-given name.</param>
    /// <returns>The selected shape, or null when nothing matched.</returns>
    public SculptShape? Select(string idOrName) {
        ArgumentNullException.ThrowIfNull(idOrName);

        if (int.TryParse(s: idOrName, result: out var id)) {
            if (!m_shapeIndexById.TryGetValue(key: id, value: out var mappedIndex)) {
                return null;
            }

            SelectIndex(index: mappedIndex);

            return m_shapes[mappedIndex];
        }

        for (var index = 0; (index < m_shapes.Count); index++) {
            if (string.Equals(a: m_shapes[index].Name, b: idOrName, comparisonType: StringComparison.OrdinalIgnoreCase)) {
                SelectIndex(index: index);

                return m_shapes[index];
            }
        }

        return null;
    }

    /// <summary>Cycles the selection through the shapes, THEN past them into the defined chains' GOALS, wrapping
    /// through "none" (where the target reverts to the brush) at either end.</summary>
    /// <param name="direction">+1 for the next shape/goal, -1 for the previous.</param>
    public void CycleSelection(int direction) {
        if ((m_shapes.Count == 0) && (m_chains.Count == 0)) {
            return;
        }

        if (!TargetIsBrush) {
            m_previousSelectionIndex = m_selectionIndex;
        }

        // The combined cursor space: -1 (brush), 0..shapes-1 (shapes), shapes..+chains-1 (goals).
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

        Revision++;
    }

    /// <summary>Clears the selection (the target reverts to the brush).</summary>
    public void Deselect() {
        if (TargetIsBrush) {
            return;
        }

        m_previousSelectionIndex = m_selectionIndex;
        m_selectionIndex = -1;
        m_goalChainIndex = -1;
        Revision++;
    }

    /// <summary>Moves the TARGET this frame — planar on the floor plane plus a vertical nudge — clamped inside the
    /// workbench bound. A chain-goal target moves the goal and re-solves the chain live. A no-op on the brush (there
    /// is nothing to move). Coalesces onto one undo step per drag.</summary>
    /// <param name="planar">The X/Z move (+Y of the vector is +Z).</param>
    /// <param name="vertical">The up/down nudge (+ up).</param>
    /// <param name="deltaSeconds">The frame delta.</param>
    public void Move(Vector2 planar, float vertical, float deltaSeconds) {
        const float moveSpeed = 3.2f;
        var step = (new Vector3(x: planar.X, y: vertical, z: planar.Y) * (moveSpeed * deltaSeconds));

        if (step == Vector3.Zero) {
            return;
        }

        var pushAfter = TouchDrag();

        if (TargetIsGoal) {
            var chain = m_chains[m_goalChainIndex];

            m_chains[m_goalChainIndex] = (chain with { Goal = ClampLocal(position: (chain.Goal + step)) });
            SolveChains();
            PushIfDragStarted(dragStarted: pushAfter);

            return;
        }

        if (TargetIsBrush) {
            return;
        }

        var shape = m_shapes[m_selectionIndex];

        m_shapes[m_selectionIndex] = shape with { Position = ClampLocal(position: (shape.Position + step)) };
        Revision++;
        PushIfDragStarted(dragStarted: pushAfter);
    }

    /// <summary>Spins the TARGET this frame — yaw about world up (stick X), pitch about world right (stick Y), roll
    /// about world forward — composed onto its live orientation (world-space axis deltas premultiplied, so yaw reads
    /// the same regardless of how far the shape has turned). Coalesces onto one undo step per drag.</summary>
    /// <param name="stick">The stick vector: X yaws, Y pitches.</param>
    /// <param name="roll">The roll rate (−1 rolls left, +1 rolls right).</param>
    /// <param name="deltaSeconds">The frame delta.</param>
    public void Rotate(Vector2 stick, float roll, float deltaSeconds) {
        if (((stick == Vector2.Zero) && (roll == 0f)) || TargetIsGoal || TargetIsBrush) {
            return;
        }

        var pushAfter = TouchDrag();

        const float rotateSpeed = 2.2f; // radians/second at full deflection

        var step = (rotateSpeed * deltaSeconds);
        var delta = ((Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: (stick.X * step))
            * Quaternion.CreateFromAxisAngle(axis: Vector3.UnitX, angle: (-stick.Y * step)))
            * Quaternion.CreateFromAxisAngle(axis: Vector3.UnitZ, angle: (roll * step)));
        var shape = m_shapes[m_selectionIndex];

        m_shapes[m_selectionIndex] = shape with { Rotation = Quaternion.Normalize(value: (delta * shape.Rotation)) };
        Revision++;
        PushIfDragStarted(dragStarted: pushAfter);
    }

    /// <summary>Grows or shrinks the TARGET this frame (uniform, multiplicative — a second of "up" always ~doubles),
    /// clamped to the scale envelope. Coalesces onto one undo step per drag.</summary>
    /// <param name="delta">The scale rate (−1 shrinks, +1 grows).</param>
    /// <param name="deltaSeconds">The frame delta.</param>
    public void ScaleUniform(float delta, float deltaSeconds) {
        if ((delta == 0f) || TargetIsGoal || TargetIsBrush) {
            return;
        }

        var pushAfter = TouchDrag();
        var factor = MathF.Exp(x: ((delta * 1.6f) * deltaSeconds));
        var shape = m_shapes[m_selectionIndex];
        var next = Math.Clamp(value: (shape.Scale.X * factor), max: MaxScale, min: MinScale);

        if (next != shape.Scale.X) {
            m_shapes[m_selectionIndex] = shape with { Scale = new Vector3(value: next) };
            Revision++;
        }

        PushIfDragStarted(dragStarted: pushAfter);
    }

    /// <summary>Places the TARGET at an exact position (clamped; a goal target moves the goal and re-solves) — the
    /// console twin of stick movement. One discrete undo step.</summary>
    /// <param name="position">The desired position.</param>
    /// <returns>The clamped position actually applied, or null on the brush (nothing to place).</returns>
    public Vector3? SetTargetPosition(Vector3 position) {
        var clamped = ClampLocal(position: position);

        if (TargetIsGoal) {
            var chain = m_chains[m_goalChainIndex];

            m_chains[m_goalChainIndex] = (chain with { Goal = clamped });
            SolveChains();
            PushUndo();

            return clamped;
        }

        if (TargetIsBrush) {
            return null;
        }

        m_shapes[m_selectionIndex] = m_shapes[m_selectionIndex] with { Position = clamped };
        Revision++;
        PushUndo();

        return clamped;
    }

    /// <summary>Sets the TARGET's orientation from Tait-Bryan degrees (yaw about +Y, pitch about +X, roll about +Z).
    /// A no-op on a chain goal or the brush.</summary>
    /// <param name="yawDegrees">The yaw in degrees.</param>
    /// <param name="pitchDegrees">The pitch in degrees.</param>
    /// <param name="rollDegrees">The roll in degrees.</param>
    /// <returns>Whether a shape's orientation was set.</returns>
    public bool SetTargetRotation(float yawDegrees, float pitchDegrees, float rollDegrees) {
        if (TargetIsGoal || TargetIsBrush) {
            return false;
        }

        const float toRadians = (MathF.PI / 180f);
        var rotation = Quaternion.Normalize(value: (
            (Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: (yawDegrees * toRadians))
            * Quaternion.CreateFromAxisAngle(axis: Vector3.UnitX, angle: (pitchDegrees * toRadians)))
            * Quaternion.CreateFromAxisAngle(axis: Vector3.UnitZ, angle: (rollDegrees * toRadians))));

        m_shapes[m_selectionIndex] = m_shapes[m_selectionIndex] with { Rotation = rotation };
        Revision++;
        PushUndo();

        return true;
    }

    /// <summary>Sets the TARGET's PER-AXIS scale directly, each axis clamped to the envelope; the brush takes it as
    /// the next add's scale.</summary>
    /// <param name="scale">The desired per-axis scale.</param>
    /// <returns>The clamped scale actually applied.</returns>
    public Vector3 SetTargetScale(Vector3 scale) {
        var clamped = new Vector3(
            x: Math.Clamp(value: scale.X, max: MaxScale, min: MinScale),
            y: Math.Clamp(value: scale.Y, max: MaxScale, min: MinScale),
            z: Math.Clamp(value: scale.Z, max: MaxScale, min: MinScale)
        );

        if (TargetIsGoal) {
            return clamped;
        }

        if (TargetIsBrush) {
            m_brushScale = clamped;
            Revision++;
        } else {
            m_shapes[m_selectionIndex] = m_shapes[m_selectionIndex] with { Scale = clamped };
            Revision++;
            PushUndo();
        }

        return clamped;
    }

    /// <summary>Cycles the TARGET's primitive (wraps both directions): the brush's when nothing is selected (the
    /// next add changes), the selected shape's otherwise (re-primitive in place).</summary>
    /// <param name="direction">+1 for the next primitive, -1 for the previous.</param>
    /// <returns>The target's new primitive.</returns>
    public AvatarPrimitive CyclePrimitive(int direction) {
        if (TargetIsGoal) {
            return TargetType;
        }

        if (TargetIsBrush) {
            m_brushType = (AvatarPrimitive)(((((int)m_brushType + direction) % PrimitiveCount) + PrimitiveCount) % PrimitiveCount);
            Revision++;

            return m_brushType;
        }

        var shape = m_shapes[m_selectionIndex];
        var next = (AvatarPrimitive)(((((int)shape.Type + direction) % PrimitiveCount) + PrimitiveCount) % PrimitiveCount);

        m_shapes[m_selectionIndex] = shape with { Type = next };
        Revision++;
        PushUndo();

        return next;
    }

    /// <summary>Sets the TARGET's primitive directly (the named twin of <see cref="CyclePrimitive"/>).</summary>
    /// <param name="type">The primitive.</param>
    public void SetPrimitive(AvatarPrimitive type) {
        if (TargetIsGoal) {
            return;
        }

        if (TargetIsBrush) {
            m_brushType = type;
            Revision++;
        } else {
            m_shapes[m_selectionIndex] = m_shapes[m_selectionIndex] with { Type = type };
            Revision++;
            PushUndo();
        }
    }

    /// <summary>Cycles the TARGET's blend op through the authoring order. A non-Union blend on an UNGROUPED shape
    /// coerces it into its own group-of-one (the structural invariant that keeps blends inside instance bounds).</summary>
    /// <param name="direction">+1 forward through the cycle, -1 back.</param>
    /// <returns>The target's new blend op.</returns>
    public SdfBlendOp CycleBlend(int direction) {
        if (TargetIsGoal) {
            return TargetBlend;
        }

        var current = Array.IndexOf(array: BlendCycle, value: TargetBlend);
        var next = BlendCycle[((((current + direction) % BlendCycle.Length) + BlendCycle.Length) % BlendCycle.Length)];

        SetBlend(blend: next);

        return next;
    }

    /// <summary>Sets the TARGET's blend op directly (same group-of-one coercion as <see cref="CycleBlend"/>).</summary>
    /// <param name="blend">The blend op.</param>
    public void SetBlend(SdfBlendOp blend) {
        if (TargetIsGoal) {
            return;
        }

        if (TargetIsBrush) {
            m_brushBlend = blend;
            Revision++;
        } else {
            var shape = m_shapes[m_selectionIndex];

            m_shapes[m_selectionIndex] = shape with {
                Blend = blend,
                GroupId = (((blend != SdfBlendOp.Union) && (shape.GroupId == 0)) ? m_nextGroupId++ : shape.GroupId),
            };
            Revision++;
            PushUndo();
        }
    }

    /// <summary>Sets the TARGET's smooth-blend radius directly (clamped to [0, <see cref="ShapeDocument.MaxSmooth"/>]).</summary>
    /// <param name="value">The radius.</param>
    /// <returns>The applied radius.</returns>
    public float SetSmooth(float value) {
        if (TargetIsGoal) {
            return TargetSmooth;
        }

        var clamped = Math.Clamp(value: value, max: ShapeDocument.MaxSmooth, min: 0f);

        if (TargetIsBrush) {
            m_brushSmooth = clamped;
            Revision++;
        } else {
            m_shapes[m_selectionIndex] = m_shapes[m_selectionIndex] with { Smooth = clamped };
            Revision++;
            PushUndo();
        }

        return clamped;
    }

    /// <summary>Cycles the TARGET's material through the palette (wraps).</summary>
    /// <param name="direction">+1 for the next palette slot, -1 for the previous.</param>
    /// <returns>The target's new palette slot.</returns>
    public int CycleMaterial(int direction) {
        if (TargetIsGoal) {
            return TargetMaterialIndex;
        }

        return SetMaterialIndex(index: ((((TargetMaterialIndex + direction) % CreationDocument.PaletteSize) + CreationDocument.PaletteSize) % CreationDocument.PaletteSize));
    }

    /// <summary>Assigns the TARGET's palette slot directly (clamped into range).</summary>
    /// <param name="index">The palette slot.</param>
    /// <returns>The applied slot.</returns>
    public int SetMaterialIndex(int index) {
        if (TargetIsGoal) {
            return TargetMaterialIndex;
        }

        var clamped = Math.Clamp(value: index, max: (CreationDocument.PaletteSize - 1), min: 0);

        if (TargetIsBrush) {
            m_brushMaterial = clamped;
            Revision++;
        } else {
            m_shapes[m_selectionIndex] = m_shapes[m_selectionIndex] with { MaterialIndex = clamped };
            Revision++;
            PushUndo();
        }

        return clamped;
    }

    /// <summary>Edits a palette entry (every shape referencing the slot re-colors).</summary>
    /// <param name="index">The palette slot (clamped into range).</param>
    /// <param name="material">The new material.</param>
    public void SetPaletteEntry(int index, SdfMaterial material) {
        m_palette[Math.Clamp(value: index, max: (CreationDocument.PaletteSize - 1), min: 0)] = material;
        Revision++;
        PushUndo();
    }

    /// <summary>Toggles the TARGET's mirror flag (the local X=0 symmetry fold).</summary>
    /// <returns>The target's new mirror flag.</returns>
    public bool ToggleMirror() {
        if (TargetIsGoal) {
            return TargetMirror;
        }

        var next = !TargetMirror;

        if (TargetIsBrush) {
            m_brushMirror = next;
            Revision++;
        } else {
            m_shapes[m_selectionIndex] = m_shapes[m_selectionIndex] with { Mirror = next };
            Revision++;
            PushUndo();
        }

        return next;
    }

    /// <summary>Sets the TARGET's twist rate (clamped to ±<see cref="ShapeDocument.MaxTwist"/>).</summary>
    /// <param name="value">The rate.</param>
    /// <returns>The applied rate.</returns>
    public float SetTwist(float value) => SetStyleField(value: value, max: ShapeDocument.MaxTwist, min: -ShapeDocument.MaxTwist, field: StyleField.Twist);

    /// <summary>Sets the TARGET's bend rate (clamped to ±<see cref="ShapeDocument.MaxBend"/>).</summary>
    /// <param name="value">The rate.</param>
    /// <returns>The applied rate.</returns>
    public float SetBend(float value) => SetStyleField(value: value, max: ShapeDocument.MaxBend, min: -ShapeDocument.MaxBend, field: StyleField.Bend);

    /// <summary>Sets the TARGET's dilate (inflation) radius (clamped to [0, <see cref="ShapeDocument.MaxDilate"/>]).</summary>
    /// <param name="value">The radius.</param>
    /// <returns>The applied radius.</returns>
    public float SetDilate(float value) => SetStyleField(value: value, max: ShapeDocument.MaxDilate, min: 0f, field: StyleField.Dilate);

    /// <summary>Sets the TARGET's onion shell thickness (clamped to [0, <see cref="ShapeDocument.MaxOnion"/>]).</summary>
    /// <param name="value">The thickness.</param>
    /// <returns>The applied thickness.</returns>
    public float SetOnion(float value) => SetStyleField(value: value, max: ShapeDocument.MaxOnion, min: 0f, field: StyleField.Onion);

    /// <summary>Links the SELECTED shape with the PREVIOUSLY selected one into a composition group (select A, then
    /// B, then link). Groups merge when both shapes already belong to one.</summary>
    /// <returns>The joined group id, or null when there was no valid pair to link.</returns>
    public int? LinkWithPrevious() {
        if (TargetIsBrush || TargetIsGoal ||
            (m_previousSelectionIndex < 0) || (m_previousSelectionIndex >= m_shapes.Count) ||
            (m_previousSelectionIndex == m_selectionIndex)) {
            return null;
        }

        var current = m_shapes[m_selectionIndex];
        var previous = m_shapes[m_previousSelectionIndex];
        // Resolve the joined group: reuse either member's existing group, else mint a new one; when BOTH have
        // (different) groups, the previous shape's whole group migrates into the current's.
        var groupId = ((current.GroupId != 0) ? current.GroupId : ((previous.GroupId != 0) ? previous.GroupId : m_nextGroupId++));
        var migrating = (((previous.GroupId != 0) && (previous.GroupId != groupId)) ? previous.GroupId : 0);

        for (var index = 0; (index < m_shapes.Count); index++) {
            if ((index == m_selectionIndex) || (index == m_previousSelectionIndex) ||
                ((migrating != 0) && (m_shapes[index].GroupId == migrating))) {
                m_shapes[index] = m_shapes[index] with { GroupId = groupId };
            }
        }

        Revision++;
        PushUndo();

        return groupId;
    }

    /// <summary>Dissolves the TARGET's group: every member returns to ungrouped, and — the structural invariant —
    /// every member's blend returns to plain Union (an ungrouped shape may not carry a blend).</summary>
    /// <returns>How many shapes left the group (0 when the target was ungrouped, the brush, or a chain goal).</returns>
    public int UngroupTarget() {
        if (TargetIsBrush || TargetIsGoal || (m_shapes[m_selectionIndex].GroupId == 0)) {
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

        Revision++;
        PushUndo();

        return released;
    }

    /// <summary>Renames the SELECTED shape (a no-op without one).</summary>
    /// <param name="name">The new name.</param>
    /// <returns>Whether a shape was renamed.</returns>
    public bool RenameSelected(string name) {
        if (TargetIsBrush || TargetIsGoal) {
            return false;
        }

        m_shapes[m_selectionIndex] = m_shapes[m_selectionIndex] with { Name = name };
        Revision++;

        return true;
    }

    /// <summary>Renames the creation (the document handle).</summary>
    /// <param name="name">The new name.</param>
    public void SetName(string name) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        m_name = name;
        Revision++;
    }

    // ---- the timeline (frame snapshots — the minimal hold-style animation model) --------------------------------

    /// <summary>Steps the timeline cursor and APPLIES the destination frame's poses (0 restores the rest pose).
    /// Stepping away from rest captures it first, so the authored pose is never lost.</summary>
    /// <param name="direction">+1 forward, -1 back (clamped to [0, <see cref="FrameCount"/>]).</param>
    /// <returns>The new cursor.</returns>
    public int StepFrame(int direction) {
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
        ApplyPoses(frame: ((target == 0) ? m_restPose : m_frames[(target - 1)]));
    }

    /// <summary>RECORDS the current pose: at rest a new frame appends and becomes current; on a saved frame the
    /// snapshot overwrites it.</summary>
    /// <returns>The recorded frame's display index (1-based).</returns>
    public int RecordFrame() {
        if (m_currentFrame == 0) {
            // Recording FROM rest: the rest pose is the frame — capture it as both.
            m_restPose ??= Snapshot(name: "rest");
            m_frames.Add(item: Snapshot(name: $"f{(m_frames.Count + 1)}"));
            m_currentFrame = m_frames.Count;
        } else {
            m_frames[(m_currentFrame - 1)] = (Snapshot(name: m_frames[(m_currentFrame - 1)].Name));
        }

        Revision++;
        PushUndo();

        return m_currentFrame;
    }

    /// <summary>Deletes the CURRENT saved frame (rest is protected); later frames renumber.</summary>
    /// <returns>Whether a frame was removed.</returns>
    public bool DeleteCurrentFrame() {
        if (m_currentFrame == 0) {
            return false;
        }

        m_frames.RemoveAt(index: (m_currentFrame - 1));
        RenumberFrames();
        m_currentFrame = Math.Min(val1: m_currentFrame, val2: m_frames.Count);
        ApplyPoses(frame: ((m_currentFrame == 0) ? m_restPose : m_frames[(m_currentFrame - 1)]));
        Revision++;
        PushUndo();

        return true;
    }

    /// <summary>Toggles the frame-loop playback (needs at least one saved frame). Stopping restores rest.</summary>
    /// <returns>Whether playback is now running.</returns>
    public bool TogglePlayback() {
        if (m_frames.Count == 0) {
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

    /// <summary>Stops playback and restores the rest pose.</summary>
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

    /// <summary>Sets the playback hold per frame, in engine ticks at 60/s.</summary>
    /// <param name="ticks">The hold (clamped 1..60).</param>
    /// <returns>The applied tick count.</returns>
    public int SetFrameTicks(int ticks) {
        var clamped = Math.Clamp(value: ticks, max: 60, min: 1);

        m_secondsPerFrame = (clamped / 60f);
        Revision++;

        return clamped;
    }

    // ---- the rig (chains + IK) ----------------------------------------------------------------------------------

    /// <summary>Defines a new chain from the given shapes (root→tip order), capturing their CURRENT positions as the
    /// rest geometry.</summary>
    /// <param name="name">The player-given name (the goal-cycling/console handle); null for unnamed.</param>
    /// <param name="shapeIdsOrNames">The member shape ids or names, root→tip order (at least 2).</param>
    /// <param name="kind"><c>limb</c> or <c>spine</c> (null infers limb for exactly 3 shapes, else spine).</param>
    /// <returns>The defined chain, or null when fewer than 2 shapes resolved or <see cref="MaxChains"/> is reached.</returns>
    public SculptChain? DefineChain(string? name, IReadOnlyList<string> shapeIdsOrNames, string? kind = null) {
        ArgumentNullException.ThrowIfNull(shapeIdsOrNames);

        if (m_chains.Count >= MaxChains) {
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
        Revision++;
        PushUndo();

        return captured;
    }

    /// <summary>Defines a limb chain seeded from the selection: the SELECTED shape as root, walking forward through
    /// the next 2 shapes in document order (the pad-friendly stand-in for the console verb's arbitrary list).</summary>
    /// <returns>The defined chain, or null when there was no valid 3-shape run or <see cref="MaxChains"/> is reached.</returns>
    public SculptChain? DefineChainFromSelection() {
        if (TargetIsBrush || TargetIsGoal || ((m_selectionIndex + 2) >= m_shapes.Count) || (m_chains.Count >= MaxChains)) {
            return null;
        }

        var ids = new[] { m_shapes[m_selectionIndex].Id, m_shapes[(m_selectionIndex + 1)].Id, m_shapes[(m_selectionIndex + 2)].Id };

        if (TryCaptureChain(id: m_nextChainId, name: null, shapeIds: ids, kind: ChainDocument.KindLimb) is not { } captured) {
            return null;
        }

        m_nextChainId++;
        m_chains.Add(item: captured);
        Revision++;
        PushUndo();

        return captured;
    }

    /// <summary>Deletes a chain by id or name; a no-op when nothing matches.</summary>
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

        if (m_chainCursor >= m_chains.Count) {
            m_chainCursor = -1;
        }

        Revision++;
        PushUndo();

        return true;
    }

    /// <summary>Cycles the rig-page CURRENT-CHAIN cursor (which chain pole/kind/delete verbs act on) — SEPARATE from
    /// the goal-target selection (a chain tunes without its goal being the movement target). Wraps through "none".</summary>
    /// <param name="direction">+1 for the next chain, -1 for the previous.</param>
    /// <returns>The cursor's chain, or null (none).</returns>
    public SculptChain? CycleChainCursor(int direction) {
        if (m_chains.Count == 0) {
            m_chainCursor = -1;

            return null;
        }

        var next = (m_chainCursor + direction);

        m_chainCursor = ((next >= m_chains.Count) ? -1 : ((next < -1) ? (m_chains.Count - 1) : next));
        Revision++;

        return ((m_chainCursor >= 0) ? m_chains[m_chainCursor] : null);
    }

    /// <summary>Targets a chain's GOAL for movement, by id or name (the verb twin of cycling into goals).</summary>
    /// <param name="idOrName">The chain's id or name.</param>
    /// <returns>The targeted chain, or null when nothing matched.</returns>
    public SculptChain? SelectGoal(string idOrName) {
        var index = FindChainIndex(idOrName: idOrName);

        if (index < 0) {
            return null;
        }

        if (!TargetIsBrush) {
            m_previousSelectionIndex = m_selectionIndex;
        }

        m_selectionIndex = -1;
        m_goalChainIndex = index;
        Revision++;

        return m_chains[index];
    }

    /// <summary>Sets a chain's GOAL directly and re-solves (the numeric twin of a goal drag). One discrete undo step.</summary>
    /// <param name="idOrName">The chain's id or name.</param>
    /// <param name="goal">The new goal position (clamped into the workbench bound).</param>
    /// <returns>Whether a chain was found and re-solved.</returns>
    public bool SetGoal(string idOrName, Vector3 goal) {
        var index = FindChainIndex(idOrName: idOrName);

        if (index < 0) {
            return false;
        }

        m_chains[index] = (m_chains[index] with { Goal = ClampLocal(position: goal) });
        SolveChains();
        PushUndo();

        return true;
    }

    /// <summary>Sets a chain's pole (bend-direction hint) by id or name and re-solves.</summary>
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

    /// <summary>Nudges the CURSOR chain's pole this frame (planar) — the rig page's d-pad channel.</summary>
    /// <param name="planar">The X/Z nudge.</param>
    /// <param name="deltaSeconds">The frame delta.</param>
    public void NudgePole(Vector2 planar, float deltaSeconds) {
        if ((m_chainCursor < 0) || (m_chainCursor >= m_chains.Count) || (planar == Vector2.Zero)) {
            return;
        }

        const float poleSpeed = 3.2f;
        var chain = m_chains[m_chainCursor];

        m_chains[m_chainCursor] = (chain with { Pole = (chain.Pole + (new Vector3(x: planar.X, y: 0f, z: planar.Y) * (poleSpeed * deltaSeconds))) });
        SolveChains();
    }

    /// <summary>Sets a chain's kind by id or name (<c>limb</c> demotes to <c>spine</c> unless it has exactly 3 shapes).</summary>
    /// <param name="idOrName">The chain's id or name.</param>
    /// <param name="kind"><c>limb</c> or <c>spine</c>.</param>
    /// <returns>The applied kind, or null when no chain matched.</returns>
    public string? SetKind(string idOrName, string kind) {
        var index = FindChainIndex(idOrName: idOrName);

        if (index < 0) {
            return null;
        }

        var resolved = (string.Equals(a: kind, b: ChainDocument.KindLimb, comparisonType: StringComparison.OrdinalIgnoreCase)
            ? ChainDocument.KindLimb
            : ChainDocument.KindSpine);

        if (string.Equals(a: resolved, b: ChainDocument.KindLimb, comparisonType: StringComparison.OrdinalIgnoreCase) && (m_chains[index].ShapeIds.Count != 3)) {
            resolved = ChainDocument.KindSpine;
        }

        m_chains[index] = (m_chains[index] with { Kind = resolved });
        SolveChains();
        PushUndo();

        return resolved;
    }

    /// <summary>Toggles the CURSOR chain's kind (the rig page's chord act).</summary>
    /// <returns>The applied kind, or null when no chain is cursored.</returns>
    public string? ToggleCurrentChainKind() {
        if ((m_chainCursor < 0) || (m_chainCursor >= m_chains.Count)) {
            return null;
        }

        var chain = m_chains[m_chainCursor];
        var next = (string.Equals(a: chain.Kind, b: ChainDocument.KindLimb, comparisonType: StringComparison.OrdinalIgnoreCase) ? ChainDocument.KindSpine : ChainDocument.KindLimb);

        return SetKind(idOrName: chain.Id.ToString(provider: System.Globalization.CultureInfo.InvariantCulture), kind: next);
    }

    /// <summary>Re-solves every defined chain against its LIVE goal/pole and writes the result into its member
    /// shapes' ordinary transforms — solver output lands in the SAME transforms <see cref="RecordFrame"/> snapshots,
    /// which is what lets a recorded pose inherit IK with zero consumer changes.</summary>
    public void SolveChains() {
        foreach (var chain in m_chains) {
            var count = chain.ShapeIds.Count;

            if (m_solveScratch.Length < count) {
                m_solveScratch = new (Vector3, Quaternion)[count];
            }

            chain.Solve(destination: m_solveScratch.AsSpan(start: 0, length: count));

            for (var member = 0; (member < count); member++) {
                if (!m_shapeIndexById.TryGetValue(key: chain.ShapeIds[member], value: out var index)) {
                    continue;
                }

                var (position, rotation) = m_solveScratch[member];

                m_shapes[index] = m_shapes[index] with {
                    Position = ClampLocal(position: position),
                    Rotation = rotation,
                };
            }
        }

        Revision++;
    }

    // ---- undo/redo (the push-after / drag-coalescing protocol) --------------------------------------------------

    /// <summary>Steps the local undo ring back one edit, restoring the whole model.</summary>
    /// <returns>Whether an undo step was applied.</returns>
    public bool Undo() {
        if (!m_history.TryUndo(snapshot: out var snapshot)) {
            return false;
        }

        RestoreSnapshot(snapshot: snapshot);

        return true;
    }

    /// <summary>Steps the local undo ring forward one edit.</summary>
    /// <returns>Whether a redo step was applied.</returns>
    public bool Redo() {
        if (!m_history.TryRedo(snapshot: out var snapshot)) {
            return false;
        }

        RestoreSnapshot(snapshot: snapshot);

        return true;
    }

    /// <summary>Closes a drag whose continuous verb did not fire this frame (the stick returned to center) — call
    /// ONCE per produced frame after every input verb has run. A drag still being touched stays open.</summary>
    public void EndInputFrame() {
        if (m_dragOpen && !m_dragTouchedThisFrame) {
            m_dragOpen = false;
        }

        m_dragTouchedThisFrame = false;
    }

    // ---- the document seam --------------------------------------------------------------------------------------

    /// <summary>Lifts the model into its <c>puck.creation.v1</c> document — the payload
    /// <see cref="CreationCanonicalizer.Canonicalize"/> pins for a commit. Carried members
    /// (cameras/behavior/text-runs/extensions) hand back byte-for-byte.</summary>
    /// <returns>The document.</returns>
    public CreationDocument ToDocument() {
        var palette = new List<PaletteEntryDocument>(capacity: CreationDocument.PaletteSize);

        foreach (var entry in m_palette) {
            palette.Add(item: new PaletteEntryDocument(Albedo: entry.Albedo, Emissive: entry.Emissive, Shininess: entry.Shininess, Specular: entry.Specular));
        }

        var shapes = new List<ShapeDocument>(capacity: m_shapes.Count);

        foreach (var shape in m_shapes) {
            shapes.Add(item: new ShapeDocument(
                Bend: shape.Bend,
                Blend: shape.Blend,
                Dilate: shape.Dilate,
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
            Behavior: m_loadedBehavior,
            Cameras: m_loadedCameras,
            Chains: chains,
            Frames: frames,
            Intent: m_intent,
            Name: m_name,
            Palette: palette,
            Schema: CreationDocument.CurrentSchema,
            Shapes: shapes,
            TextRuns: m_loadedTextRuns
        ) {
            Extensions = m_loadedExtensions,
        };
    }

    /// <summary>Replaces the model's content from a NORMALIZED document (cross <see cref="CreationCanonicalizer"/>
    /// first): ids/groups resequence their counters, chains RECAPTURE rest geometry from the just-loaded shape
    /// positions (never trusting persisted rest data), and the pose is deliberately NOT re-solved — the loaded
    /// transforms already ARE the exact pose, and a two-bone solve is not perfectly idempotent at float precision
    /// (re-solving could drift a byte-identical round-trip by ULPs). Editor-opaque members stash verbatim. The undo
    /// ring re-baselines (a load is a boundary; a save is not).</summary>
    /// <param name="document">The normalized document.</param>
    /// <returns>How many shapes loaded (the shape budget truncates a larger document).</returns>
    public int LoadDocument(CreationDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        m_shapes.Clear();
        m_selectionIndex = -1;
        m_previousSelectionIndex = -1;
        m_name = (document.Name ?? "creation");
        m_bakeStyle = (document.BakeStyle ?? "classic");
        m_intent = (document.Intent ?? CreatorIntent.Object);
        m_loadedCameras = document.Cameras;
        m_loadedBehavior = document.Behavior;
        m_loadedTextRuns = document.TextRuns;
        m_loadedExtensions = document.Extensions;

        if (document.Palette is { } palette) {
            for (var index = 0; ((index < palette.Count) && (index < CreationDocument.PaletteSize)); index++) {
                var entry = palette[index];
                var defaults = new SdfMaterial(Albedo: entry.Albedo);

                m_palette[index] = (defaults with {
                    Emissive = (entry.Emissive ?? defaults.Emissive),
                    Shininess = (entry.Shininess ?? defaults.Shininess),
                    Specular = (entry.Specular ?? defaults.Specular),
                });
            }
        }

        var carriedGlyphs = 0;

        foreach (var run in (m_loadedTextRuns ?? [])) {
            carriedGlyphs += run.GlyphCount;
        }

        var maxId = -1;
        var maxGroup = 0;

        foreach (var shape in (document.Shapes ?? [])) {
            if ((m_shapes.Count + carriedGlyphs) >= m_shapeCapacity) {
                break;
            }

            m_shapes.Add(item: new SculptShape(
                Bend: (shape.Bend ?? 0f),
                Blend: (shape.Blend ?? SdfBlendOp.Union),
                Dilate: (shape.Dilate ?? 0f),
                GroupId: (shape.Group ?? 0),
                Id: shape.Id,
                MaterialIndex: (shape.Material ?? 0),
                Mirror: (shape.Mirror ?? false),
                Name: shape.Name,
                Onion: (shape.Onion ?? 0f),
                Position: ClampLocal(position: shape.Position),
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

        // The timeline reloads with the shapes (the cursor resets to rest; the rest pose is the loaded live model).
        m_frames.Clear();
        m_currentFrame = 0;
        m_restPose = null;
        m_playing = false;

        foreach (var frame in (document.Frames ?? [])) {
            var poses = new List<SculptPose>(capacity: frame.Transforms.Count);

            foreach (var transform in frame.Transforms) {
                poses.Add(item: new SculptPose(Id: transform.Id, Position: transform.Position, Rotation: transform.Rotation, Scale: transform.Scale));
            }

            m_frames.Add(item: new SculptFrame(Name: frame.Name, Poses: poses));
        }

        m_chains.Clear();
        m_goalChainIndex = -1;
        m_chainCursor = -1;

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
        Revision++;
        m_history.Reset(initial: CaptureSnapshot());

        return m_shapes.Count;
    }

    /// <summary>A fresh copy of the default 16-slot palette: a golden-ratio hue sweep — well-separated hues for
    /// small index counts, deterministic, editable per slot. The schema's documented "null palette = the default
    /// sweep" behavior, value-for-value.</summary>
    /// <returns>The default palette (one array per call — callers may mutate their copy).</returns>
    public static SdfMaterial[] DefaultPalette() {
        var palette = new SdfMaterial[CreationDocument.PaletteSize];

        for (var index = 0; (index < CreationDocument.PaletteSize); index++) {
            palette[index] = new SdfMaterial(Albedo: PaletteHue(index: index));
        }

        return palette;
    }

    private enum StyleField {
        Twist,
        Bend,
        Dilate,
        Onion,
    }

    // The shared clamped-style setter (twist/bend/dilate/onion have identical target dispatch, differing only in
    // clamp envelope and field).
    private float SetStyleField(float value, float max, float min, StyleField field) {
        if (TargetIsGoal) {
            return 0f;
        }

        var clamped = Math.Clamp(value: value, max: max, min: min);

        if (TargetIsBrush) {
            _ = field switch {
                StyleField.Twist => m_brushTwist = clamped,
                StyleField.Bend => m_brushBend = clamped,
                StyleField.Dilate => m_brushDilate = clamped,
                _ => m_brushOnion = clamped,
            };
            Revision++;
        } else {
            var shape = m_shapes[m_selectionIndex];

            m_shapes[m_selectionIndex] = field switch {
                StyleField.Twist => shape with { Twist = clamped },
                StyleField.Bend => shape with { Bend = clamped },
                StyleField.Dilate => shape with { Dilate = clamped },
                _ => shape with { Onion = clamped },
            };
            Revision++;
            PushUndo();
        }

        return clamped;
    }

    private void SelectIndex(int index) {
        if (!TargetIsBrush) {
            m_previousSelectionIndex = m_selectionIndex;
        }

        m_selectionIndex = index;
        m_goalChainIndex = -1;
        Revision++;
    }

    private SculptFrame Snapshot(string name) {
        var poses = new List<SculptPose>(capacity: m_shapes.Count);

        foreach (var shape in m_shapes) {
            poses.Add(item: new SculptPose(Id: shape.Id, Position: shape.Position, Rotation: shape.Rotation, Scale: shape.Scale));
        }

        return new SculptFrame(Name: name, Poses: poses);
    }

    // Applies a frame's poses by shape id (missing shapes skip harmlessly).
    private void ApplyPoses(SculptFrame? frame) {
        if (frame is null) {
            return;
        }

        foreach (var pose in frame.Poses) {
            if (!m_shapeIndexById.TryGetValue(key: pose.Id, value: out var index)) {
                continue;
            }

            m_shapes[index] = m_shapes[index] with {
                Position = ClampLocal(position: pose.Position),
                Rotation = pose.Rotation,
                Scale = pose.Scale,
            };
        }

        Revision++;
    }

    private void RenumberFrames() {
        for (var index = 0; (index < m_frames.Count); index++) {
            var expected = $"f{(index + 1)}";

            if (m_frames[index].Name.StartsWith(value: 'f') && (m_frames[index].Name != expected)) {
                m_frames[index] = (m_frames[index] with { Name = expected });
            }
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
    private SculptChain? TryCaptureChain(int id, string? name, IReadOnlyList<int> shapeIds, string? kind) {
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

        return SculptChain.Capture(id: id, kind: kind, name: name, positions: positions, rotations: rotations, shapeIds: shapeIds);
    }

    private static Vector3 ClampLocal(Vector3 position) =>
        new(
            x: Math.Clamp(value: position.X, min: -BoundHalfExtent, max: BoundHalfExtent),
            y: Math.Clamp(value: position.Y, min: BoundMinY, max: BoundMaxY),
            z: Math.Clamp(value: position.Z, min: -BoundHalfExtent, max: BoundHalfExtent)
        );

    // Resyncs the shape id -> index map with the current m_shapes contents — call after ANY add/remove/clear (an
    // in-place `m_shapes[i] = shape with {...}` never needs this). Structural edits are act-scale, never per-frame.
    private void RebuildShapeIndex() {
        m_shapeIndexById.Clear();

        for (var index = 0; (index < m_shapes.Count); index++) {
            m_shapeIndexById[m_shapes[index].Id] = index;
        }
    }

    // ---- undo internals (EditHistory<SculptSnapshot> over the whole authored state) ------------------------------
    //
    // EditHistory<T>.Push's contract is "the snapshot AFTER the completed edit" — the pushed value becomes the new
    // undo-stack top, and a later TryUndo steps back to whatever was pushed BEFORE it. So every push here happens
    // AFTER its mutation, never before (pushing before would duplicate the constructor's baseline into the first
    // edit's "before" state and shift every subsequent undo off by one).

    // Marks a continuous edit's (a drag's) touch for this frame: true only on the drag's START edge, so the caller
    // pushes the post-mutation snapshot exactly once; the drag stays open across frames until EndInputFrame notices
    // it went untouched, coalescing the whole drag onto ONE undo step. Call BEFORE mutating.
    private bool TouchDrag() {
        var isDragStart = !m_dragOpen;

        m_dragOpen = true;
        m_dragTouchedThisFrame = true;

        return isDragStart;
    }

    // Pushes a snapshot for a DISCRETE edit (one push per call, unconditionally) — call AFTER mutating. Also closes
    // any open drag first, so a discrete edit mid-drag does not merge into it.
    private void PushUndo() {
        m_dragOpen = false;
        m_history.Push(snapshot: CaptureSnapshot());
    }

    // Completes a TouchDrag pair: call AFTER mutating, passing back what TouchDrag returned.
    private void PushIfDragStarted(bool dragStarted) {
        if (dragStarted) {
            m_history.Push(snapshot: CaptureSnapshot());
        }
    }

    // Lifts the WHOLE authored state (everything ToDocument would persist, plus the selection/cursor state and the
    // brush so undo/redo feels like true time travel) into one immutable snapshot value. Carried opaque members are
    // deliberately NOT captured — no verb edits them, so undo must not touch them.
    private SculptSnapshot CaptureSnapshot() {
        return new SculptSnapshot(
            BakeStyle: m_bakeStyle,
            BrushBend: m_brushBend,
            BrushBlend: m_brushBlend,
            BrushDilate: m_brushDilate,
            BrushMaterial: m_brushMaterial,
            BrushMirror: m_brushMirror,
            BrushOnion: m_brushOnion,
            BrushRotation: m_brushRotation,
            BrushScale: m_brushScale,
            BrushSmooth: m_brushSmooth,
            BrushTwist: m_brushTwist,
            BrushType: m_brushType,
            Chains: [.. m_chains],
            CurrentFrame: m_currentFrame,
            Frames: [.. m_frames],
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

    // Restores a previously captured snapshot wholesale (undo/redo's shared apply path).
    private void RestoreSnapshot(SculptSnapshot snapshot) {
        m_shapes.Clear();
        m_shapes.AddRange(collection: snapshot.Shapes);
        RebuildShapeIndex();

        for (var index = 0; ((index < snapshot.Palette.Count) && (index < CreationDocument.PaletteSize)); index++) {
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
        m_brushType = snapshot.BrushType;
        m_brushRotation = snapshot.BrushRotation;
        m_brushScale = snapshot.BrushScale;
        m_brushMaterial = snapshot.BrushMaterial;
        m_brushBlend = snapshot.BrushBlend;
        m_brushSmooth = snapshot.BrushSmooth;
        m_brushMirror = snapshot.BrushMirror;
        m_brushTwist = snapshot.BrushTwist;
        m_brushBend = snapshot.BrushBend;
        m_brushDilate = snapshot.BrushDilate;
        m_brushOnion = snapshot.BrushOnion;
        m_goalChainIndex = -1;
        m_chainCursor = -1;
        m_playing = false;
        Revision++;
    }

    // The complete authored-state snapshot the undo/redo ring stores. Immutable by construction (record +
    // IReadOnlyList members populated from array/list COPIES at capture time).
    private sealed record SculptSnapshot(
        IReadOnlyList<SculptShape> Shapes,
        IReadOnlyList<SdfMaterial> Palette,
        IReadOnlyList<SculptFrame> Frames,
        IReadOnlyList<SculptChain> Chains,
        int CurrentFrame,
        SculptFrame? RestPose,
        int SelectionIndex,
        int PreviousSelectionIndex,
        int NextShapeId,
        int NextGroupId,
        int NextChainId,
        string Name,
        string BakeStyle,
        CreatorIntent Intent,
        AvatarPrimitive BrushType,
        Quaternion BrushRotation,
        Vector3 BrushScale,
        int BrushMaterial,
        SdfBlendOp BrushBlend,
        float BrushSmooth,
        bool BrushMirror,
        float BrushTwist,
        float BrushBend,
        float BrushDilate,
        float BrushOnion
    );

    private static Vector3 PaletteHue(int index) {
        var hue = ((index * 0.61803399f) % 1f);
        var h6 = (hue * 6f);
        var x = (1f - MathF.Abs(x: ((h6 % 2f) - 1f)));

        var (r, g, b) = ((int)h6 switch {
            0 => (1f, x, 0f),
            1 => (x, 1f, 0f),
            2 => (0f, 1f, x),
            3 => (0f, x, 1f),
            4 => (x, 0f, 1f),
            _ => (1f, 0f, x),
        });

        return new Vector3(x: (0.35f + (0.5f * r)), y: (0.35f + (0.5f * g)), z: (0.35f + (0.5f * b)));
    }
}
