using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Puck.Assets.Documents;
using Puck.Maths;
using Puck.SignedDistance;

namespace Puck.Forge.Authoring;

/// <summary>
/// The sculpt model — a <c>puck.creation.v1</c> document being edited at frame rate. The <see cref="Document"/> IS
/// the model: every edit is <c>document with {...}</c> (records are immutable and value-equal), and
/// <see cref="EditHistory{T}"/> undoes/redoes the document itself. Everything that is NOT document-representable —
/// selection, the brush (the next add's style), drag state, the timeline cursor/clock, the chain-rig cursor, and the
/// derived IK rest-geometry cache — lives beside it as ordinary session fields, never serialized, never a parallel
/// copy of a document section.
/// </summary>
/// <remarks><b>The target model.</b> Every edit verb acts on the selected shape when one exists, else on the brush.
/// When a chain goal is the target (<see cref="TargetIsGoal"/>), movement drives the goal and the chain re-solves
/// live. Single-threaded like every input-fold type: mutators run in the command pump's apply window,
/// <see cref="TickPlayback"/>/<see cref="EndInputFrame"/> on the produce path — one window-pump thread.</remarks>
public sealed class SculptModel {
    /// <summary>The blend cycle in authoring order (hard/smooth pairs adjacent), not raw enum order.</summary>
    public static readonly SdfBlendOp[] BlendCycle = [
        SdfBlendOp.Union,
        SdfBlendOp.SmoothUnion,
        SdfBlendOp.Subtraction,
        SdfBlendOp.SmoothSubtraction,
        SdfBlendOp.Intersection,
        SdfBlendOp.SmoothIntersection,
        SdfBlendOp.Xor,
    ];
    /// <summary>The primitive count the cycle wraps over (the <see cref="AvatarPrimitive"/> set).</summary>
    public static int PrimitiveCount { get; } = Enum.GetValues<AvatarPrimitive>().Length;

    // Where a brand-new shape lands when the caller names no position: just above the workbench origin, so the
    // first Add is visible on the bench rather than buried in the ground plane.
    private static readonly Vector3 SpawnPosition = new(
        x: 0f,
        y: 0.7f,
        z: 0f
    );

    // The workbench-local authoring bound every position clamps into — the preview/stamp path's instance-bound
    // contract (reach is data-derived; a shape flung far from the origin would blow the stamp evaluation up), and
    // the orbit camera frames this envelope. Persisted creations were authored inside a bound of this scale, so the
    // clamp never bites a legitimate load.
    private const float BoundHalfExtent = 6f;
    private const float BoundMaxY = 10f;
    private const float BoundMinY = -1f;

    /// <summary>The undo ring's bounded snapshot count.</summary>
    public const int HistoryCapacity = 64;
    /// <summary>The most chains a model defines.</summary>
    public const int MaxChains = 16;
    /// <summary>The largest per-axis scale an authored shape clamps to.</summary>
    public const float MaxScale = 3.0f;
    /// <summary>The smallest per-axis scale an authored shape clamps to.</summary>
    public const float MinScale = 0.2f;
    // The hold-style playback cadence (engine ticks at 60/s) — a fixed session constant; the timeline authors NO
    // per-creation playback-speed field (it is a local preview convenience, never part of the document).
    private const float SecondsPerFrame = (8f / 60f);

    // THE DOCUMENT — the whole model. Every mutator replaces this with `with {...}`; EditHistory<CreationDocument>
    // undoes/redoes it directly. Shapes is ALWAYS a concrete (possibly empty) list during a live session (never
    // null) so a bare generic-set path can always navigate/append into it; Palette/Chains/Frames stay exactly as
    // loaded (including null/short) so a load with no edits round-trips byte-identically — a generic set touching
    // palette[n] pads it to n+1 slots with the default sweep first (see AcceptCandidate/PadPaletteForWrite), never
    // eagerly.
    private CreationDocument m_document;
    private readonly EditHistory<CreationDocument> m_history;
    private readonly int m_shapeCapacity;

    // THE RIG CACHE — rest geometry ChainDocument itself does not (and never will) carry, keyed by chain id. Ids are
    // minted by m_nextChainId and NEVER reused within a session, so once a rig is captured for an id it is valid for
    // that id's entire remaining lifetime: undo/redo change which ids are VISIBLE in Document.Chains, never what a
    // visible id's rest geometry means. See ChainRig's remarks.
    private readonly Dictionary<int, ChainRig> m_rig = [];
    private (Vector3 Position, Quaternion Rotation)[] m_solveScratch = [];

    // THE BRUSH — a ShapeDocument-shaped template: the style/transform the next AddShape inherits, and the generic
    // set path's target when nothing is selected (`.<field>` against the brush, via TrySetBrushField). Being a real
    // ShapeDocument means the brush never special-cases a field name — whatever the document record declares, the
    // brush carries the same way.
    private ShapeDocument m_brush;

    // Session-only id counters — monotonic for the model's whole lifetime (NOT restored by undo; only Load rebases
    // them), which is what makes the rig cache above valid forever once populated.
    private int m_nextChainId = 1;
    private int m_nextGroupId = 1;
    private int m_nextShapeId = 1;

    // Selection/target state, by id (never index — a removed/undone id just stops resolving, no stale-index cleanup
    // needed anywhere).
    private int? m_chainCursorId;
    private int? m_goalChainId;
    private int? m_previousSelectedShapeId;
    private int? m_selectedShapeId;

    // Drag coalescing (a held gesture pushes ONE undo step, on the drag's start edge).
    private bool m_dragOpen;
    private bool m_dragTouchedThisFrame;

    // The timeline: cursor 0 is the rest pose (the live authored model), captured implicitly the first time the
    // timeline steps away from it and restored on return/stop.
    private int m_currentFrame;
    private float m_playClock;
    private int m_playCursor;
    private bool m_playing;
    private IReadOnlyList<FrameTransformDocument>? m_restPose;

    /// <summary>Initializes an empty model under a shape budget.</summary>
    /// <param name="shapeCapacity">The consumer's per-creation shape budget — <see cref="CreationDocument.StampShapeCount"/>
    /// never exceeds it; <see cref="AddShape"/>/<see cref="DuplicateTarget"/> refuse past it.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="shapeCapacity"/> is not positive.</exception>
    public SculptModel(int shapeCapacity) {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            other: 1,
            value: shapeCapacity
        );

        m_shapeCapacity = shapeCapacity;
        m_brush = DefaultBrush();
        m_document = new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: "creation",
            Palette: null,
            Shapes: [],
            Frames: null
        );
        m_history = new EditHistory<CreationDocument>(
            capacity: HistoryCapacity,
            initial: m_document
        );
    }

    /// <summary>Whether a redo step is available on the local ring.</summary>
    public bool CanRedo => m_history.CanRedo;
    /// <summary>Whether an undo step is available on the local ring.</summary>
    public bool CanUndo => m_history.CanUndo;
    /// <summary>The chains currently defined, in definition order.</summary>
    public IReadOnlyList<ChainDocument> Chains => (m_document.Chains ?? []);
    /// <summary>The rig-page chain cursor's chain (see <see cref="CycleChainCursor"/>); null = none.</summary>
    public ChainDocument? CurrentChain {
        get {
            if (m_chainCursorId is not { } id) {
                return null;
            }

            foreach (var chain in Chains) {
                if (chain.Id == id) {
                    return chain;
                }
            }

            return null;
        }
    }
    /// <summary>The timeline cursor: 0 = the rest pose (the live model), 1..<see cref="FrameCount"/> = saved frames.</summary>
    public int CurrentFrame => m_currentFrame;
    /// <summary>The model's whole document — the source of truth. Every mutation replaces this wholesale.</summary>
    public CreationDocument Document => m_document;
    /// <summary>How many frames are saved (past the always-present rest pose).</summary>
    public int FrameCount => (m_document.Frames?.Count ?? 0);
    /// <summary>The local ring's retained snapshot count (the HUD's ring readout).</summary>
    public int HistoryCount => m_history.Count;
    /// <summary>The creation's name (the document handle; the committed row id names the world asset).</summary>
    public string Name => (m_document.Name ?? "creation");
    /// <summary>Whether the frame loop is playing.</summary>
    public bool Playing => m_playing;
    /// <summary>Bumps on every visible mutation — a preview consumer re-emits when it moves.</summary>
    public int Revision { get; private set; }
    /// <summary>The selected shape, when there is one (null on the brush or a chain goal).</summary>
    public ShapeDocument? SelectedShape => (TargetIsBrush ? null : ResolveSelectedShape()?.Shape);
    /// <summary>The shape budget passed at construction (the consumer's per-stamp cap).</summary>
    public int ShapeCapacity => m_shapeCapacity;
    /// <summary>The model's current stamp cost — see <see cref="CreationDocument.StampShapeCount"/>.</summary>
    public int StampShapeCount => m_document.StampShapeCount();
    /// <summary>Whether style/transform gestures currently target the brush (no shape and no chain goal selected).</summary>
    public bool TargetIsBrush => (!TargetIsGoal && (ResolveSelectedShape() is null));
    /// <summary>Whether the selected target is a chain goal rather than a shape or the brush — movement then drives
    /// the goal and the chain re-solves live.</summary>
    public bool TargetIsGoal => (ResolveGoalChainIndex() is not null);
    /// <summary>The chain whose goal is the current target, when <see cref="TargetIsGoal"/> — a resolved, non-null
    /// view (a fresh chain's goal/pole default to its rest tip / above its root until moved).</summary>
    public ChainTarget? TargetGoalChain {
        get {
            if (ResolveGoalChainIndex() is not { } index) {
                return null;
            }

            var chain = Chains[index];
            var rig = RigFor(chainId: chain.Id);

            return new ChainTarget(
                Goal: (chain.Goal ?? rig.RestGoal),
                Id: chain.Id,
                Kind: (chain.Kind ?? ChainDocument.KindSpine),
                Name: chain.Name,
                Pole: (chain.Pole ?? rig.RestPole)
            );
        }
    }

    /// <summary>A resolved chain-goal view (see <see cref="TargetGoalChain"/>) — Goal/Pole are always concrete,
    /// falling back to the chain's rest geometry when unset.</summary>
    public readonly record struct ChainTarget(int Id, string? Name, string Kind, Vector3 Goal, Vector3 Pole);
    /// <summary>One generic-edit outcome: whether the patch (and its validation) succeeded, and a human-readable
    /// detail — the applied field on success, the refusal reason on failure.</summary>
    public readonly record struct EditOutcome(bool Success, string Message);

    // Applies a recorded/rest frame's poses by shape id (a pose whose shape was deleted skips harmlessly).
    private void ApplyPoses(IReadOnlyList<FrameTransformDocument>? poses) {
        if (poses is null) {
            return;
        }

        var shapes = new List<ShapeDocument>(collection: (m_document.Shapes ?? []));

        foreach (var pose in poses) {
            var index = shapes.FindIndex(match: s => (s.Id == pose.Id));

            if (index < 0) {
                continue;
            }

            shapes[index] = shapes[index] with {
                Position = ClampLocal(position: pose.Position),
                Rotation = pose.Rotation,
                Scale = pose.Scale,
            };
        }

        m_document = m_document with { Shapes = shapes };
        Revision++;
    }
    // The shared style-target mutator: brush edits apply in place; a selected shape's edit replaces its slot and
    // (when discreet=true) pushes one undo step. A no-op on a chain goal (nothing shape-shaped to edit).
    private void ApplyToTarget(Func<ShapeDocument, ShapeDocument> mutate, bool pushUndo) {
        if (TargetIsGoal) {
            return;
        }

        if (TargetIsBrush) {
            m_brush = ClampBrush(shape: mutate(m_brush));
            Revision++;

            return;
        }

        var (index, shape) = ResolveSelectedShape()!.Value;
        var shapes = new List<ShapeDocument>(collection: m_document.Shapes!) {
            [index] = mutate(shape),
        };

        m_document = (m_document with { Shapes = shapes });
        Revision++;

        if (pushUndo) {
            PushUndo();
        }
    }
    private static ShapeDocument ClampBrush(ShapeDocument shape) => (shape with {
        Bend = Math.Clamp(
            value: (shape.Bend ?? 0f),
            max: ShapeDocument.MaxBend,
            min: -ShapeDocument.MaxBend
        ),
        Blend = (shape.Blend ?? SdfBlendOp.Union),
        Dilate = Math.Clamp(
            value: (shape.Dilate ?? 0f),
            max: ShapeDocument.MaxDilate,
            min: 0f
        ),
        Group = 0,
        Material = Math.Clamp(
            value: (shape.Material ?? 0),
            max: (CreationDocument.PaletteSize - 1),
            min: 0
        ),
        Onion = Math.Clamp(
            value: (shape.Onion ?? 0f),
            max: ShapeDocument.MaxOnion,
            min: 0f
        ),
        Rotation = ((shape.Rotation == default) ? Quaternion.Identity : Quaternion.Normalize(value: shape.Rotation)),
        Scale = ClampScale(scale: ((shape.Scale == default) ? Vector3.One : shape.Scale)),
        Smooth = Math.Clamp(
            value: (shape.Smooth ?? 0f),
            max: ShapeDocument.MaxSmooth,
            min: 0f
        ),
        Twist = Math.Clamp(
            value: (shape.Twist ?? 0f),
            max: ShapeDocument.MaxTwist,
            min: -ShapeDocument.MaxTwist
        ),
    });
    private static Vector3 ClampLocal(Vector3 position) =>
        new(
            x: Math.Clamp(
                max: BoundHalfExtent,
                min: -BoundHalfExtent,
                value: position.X
            ),
            y: Math.Clamp(
                max: BoundMaxY,
                min: BoundMinY,
                value: position.Y
            ),
            z: Math.Clamp(
                max: BoundHalfExtent,
                min: -BoundHalfExtent,
                value: position.Z
            )
        );
    private static Vector3 ClampScale(Vector3 scale) =>
        new(
            x: Math.Clamp(
                value: scale.X,
                max: MaxScale,
                min: MinScale
            ),
            y: Math.Clamp(
                value: scale.Y,
                max: MaxScale,
                min: MinScale
            ),
            z: Math.Clamp(
                value: scale.Z,
                max: MaxScale,
                min: MinScale
            )
        );
    private static ShapeDocument DefaultBrush() =>
        new(
            Bend: 0f,
            Blend: SdfBlendOp.Union,
            Dilate: 0f,
            Group: 0,
            Id: 0,
            Material: 0,
            Name: null,
            Onion: 0f,
            Position: Vector3.Zero,
            Rotation: Quaternion.Identity,
            Scale: Vector3.One,
            Smooth: 0f,
            Twist: 0f,
            Type: default
        );
    /// <summary>A fresh copy of the default 16-slot palette: a golden-ratio hue sweep — well-separated hues for
    /// small index counts, deterministic, editable per slot. The schema's documented "null palette = the default
    /// sweep" behavior, value-for-value.</summary>
    /// <returns>The default palette (one list per call — callers may mutate their copy).</returns>
    public static List<PaletteEntryDocument> DefaultPalette() {
        var palette = new List<PaletteEntryDocument>(capacity: CreationDocument.PaletteSize);

        for (var index = 0; (index < CreationDocument.PaletteSize); index++) {
            palette.Add(item: new PaletteEntryDocument(
                Color: HexColor.Format(rgb: PaletteHue(index: index)),
                Emissive: null,
                Specular: null,
                Shininess: null
            ));
        }

        return palette;
    }
    private static Vector3 PaletteHue(int index) {
        var hue = ((index * 0.61803399f) % 1f);
        var h6 = (hue * 6f);
        var x = (1f - MathF.Abs(x: ((h6 % 2f) - 1f)));

        var (r, g, b) = (((int)h6) switch {
            0 => (1f, x, 0f),
            1 => (x, 1f, 0f),
            2 => (0f, 1f, x),
            3 => (0f, x, 1f),
            4 => (x, 0f, 1f),
            _ => (1f, 0f, x),
        });

        return new Vector3(
            x: (0.35f + (0.5f * r)),
            y: (0.35f + (0.5f * g)),
            z: (0.35f + (0.5f * b))
        );
    }
    // Pushes a snapshot for a DISCRETE edit (one push per call, unconditionally) — call AFTER mutating. Also closes
    // any open drag first, so a discrete edit mid-drag does not merge into it.
    private void PushUndo() {
        m_dragOpen = false;
        m_history.Push(snapshot: m_document);
    }
    // Completes a TouchDrag pair: call AFTER mutating, passing back what TouchDrag returned.
    private void PushIfDragStarted(bool dragStarted) {
        if (dragStarted) {
            m_history.Push(snapshot: m_document);
        }
    }
    // Resolves a chain by id (digits) or (case-insensitive) name to its index in Chains, or -1 when nothing matches.
    private int ResolveChainIndex(string idOrName) {
        var chains = Chains;

        for (var index = 0; (index < chains.Count); index++) {
            var chain = chains[index];
            var matches = (int.TryParse(
                result: out var id,
                s: idOrName
            )
                ? (chain.Id == id)
                : string.Equals(
                    a: chain.Name,
                    b: idOrName,
                    comparisonType: StringComparison.OrdinalIgnoreCase
                )
            );

            if (matches) {
                return index;
            }
        }

        return -1;
    }
    private int? ResolveGoalChainIndex() {
        if (m_goalChainId is not { } id) {
            return null;
        }

        var chains = Chains;

        for (var index = 0; (index < chains.Count); index++) {
            if (chains[index].Id == id) {
                return index;
            }
        }

        return null;
    }
    // Resolves a shape by id (digits) or (case-insensitive) name to its id, or null when nothing matches.
    private int? ResolveShapeId(string idOrName) {
        var shapes = (m_document.Shapes ?? []);

        if (int.TryParse(
            result: out var id,
            s: idOrName
        )) {
            foreach (var shape in shapes) {
                if (shape.Id == id) {
                    return id;
                }
            }

            return null;
        }

        foreach (var shape in shapes) {
            if (string.Equals(
                a: shape.Name?.Value,
                b: idOrName,
                comparisonType: StringComparison.OrdinalIgnoreCase
            )) {
                return shape.Id;
            }
        }

        return null;
    }
    private (int Index, ShapeDocument Shape)? ResolveSelectedShape() {
        if (m_selectedShapeId is not { } id) {
            return null;
        }

        var shapes = (m_document.Shapes ?? []);

        for (var index = 0; (index < shapes.Count); index++) {
            if (shapes[index].Id == id) {
                return (index, shapes[index]);
            }
        }

        return null;
    }
    // The rig cache lookup every solve/goal-target read uses — a chain visible in Document.Chains always has a
    // cached rig (captured at definition/load, or resynced by AcceptCandidate for a chain a generic edit added
    // directly); the fallback recapture is defensive only.
    private ChainRig RigFor(int chainId) {
        if (m_rig.TryGetValue(
            key: chainId,
            value: out var cached
        )) {
            return cached;
        }

        foreach (var chain in Chains) {
            if ((chain.Id == chainId) && TryCaptureRig(
                shapeIds: chain.Shapes,
                shapes: (m_document.Shapes ?? []),
                rig: out var recaptured
            )) {
                m_rig[chainId] = recaptured;

                return recaptured;
            }
        }

        return ChainRig.Capture(positions: [], rotations: []);
    }
    private void SelectShapeById(int? id) {
        if (m_selectedShapeId is { } current) {
            m_previousSelectedShapeId = current;
        }

        m_selectedShapeId = id;
        m_goalChainId = null;
        Revision++;
    }
    private IReadOnlyList<FrameTransformDocument> Snapshot() {
        var shapes = (m_document.Shapes ?? []);
        var poses = new List<FrameTransformDocument>(capacity: shapes.Count);

        foreach (var shape in shapes) {
            poses.Add(item: new FrameTransformDocument(
                Id: shape.Id,
                Position: shape.Position,
                Rotation: shape.Rotation,
                Scale: shape.Scale
            ));
        }

        return poses;
    }
    private void StopPlayback() {
        m_playing = false;
        m_currentFrame = 0;
        ApplyPoses(poses: m_restPose);
    }
    // Completes a TouchDrag pair for a chain-goal/pole drive: call BEFORE mutating (see TouchDrag).
    private bool TouchDrag() {
        var isDragStart = !m_dragOpen;

        m_dragOpen = true;
        m_dragTouchedThisFrame = true;

        return isDragStart;
    }
    // Captures a chain's rest geometry from the CURRENT positions of the named shapes (root→tip order); false when
    // fewer than 2 resolved or any named shape is missing.
    private static bool TryCaptureRig(IReadOnlyList<int> shapeIds, IReadOnlyList<ShapeDocument> shapes, out ChainRig rig) {
        if (shapeIds.Count < 2) {
            rig = null!;

            return false;
        }

        var positions = new List<Vector3>(capacity: shapeIds.Count);
        var rotations = new List<Quaternion>(capacity: shapeIds.Count);

        foreach (var shapeId in shapeIds) {
            var found = false;

            foreach (var shape in shapes) {
                if (shape.Id == shapeId) {
                    positions.Add(item: shape.Position);
                    rotations.Add(item: shape.Rotation);
                    found = true;

                    break;
                }
            }

            if (!found) {
                rig = null!;

                return false;
            }
        }

        rig = ChainRig.Capture(
            positions: positions,
            rotations: rotations
        );

        return true;
    }
    private ShapeDocument TargetShape() => (TargetIsBrush ? m_brush : ResolveSelectedShape()!.Value.Shape);

    /// <summary>Adds a shape: the brush's primitive (or an explicit one) with the brush's style, at an explicit
    /// position or the spawn point, then selects it. The brush's palette slot advances so consecutive adds read as
    /// distinct siblings.</summary>
    /// <param name="type">The primitive, or null for the brush's.</param>
    /// <param name="position">The position (clamped into the workbench bound), or null for the spawn point.</param>
    /// <returns>The added shape, or null when the shape budget is spent.</returns>
    public ShapeDocument? AddShape(AvatarPrimitive? type = null, Vector3? position = null) {
        if (StampShapeCount >= m_shapeCapacity) {
            return null;
        }

        var shape = (m_brush with {
            Group = (((m_brush.Blend ?? SdfBlendOp.Union) != SdfBlendOp.Union) ? m_nextGroupId++ : 0),
            Id = m_nextShapeId++,
            Name = null,
            Position = ClampLocal(position: (position ?? SpawnPosition)),
            Type = (type ?? m_brush.Type),
        });
        var shapes = new List<ShapeDocument>(collection: (m_document.Shapes ?? [])) { shape };

        m_document = (m_document with { Shapes = shapes });

        if (type is { } explicitType) {
            m_brush = (m_brush with { Type = explicitType });
        }

        // The next add reads as a sibling: advance the brush's palette slot so consecutive adds stay visually
        // distinct without any palette work by the player.
        m_brush = (m_brush with { Material = (((m_brush.Material ?? 0) + 1) % CreationDocument.PaletteSize) });
        SelectShapeById(id: shape.Id);
        PushUndo();

        return shape;
    }
    /// <summary>Cycles the target's blend op through the authoring order. A non-Union blend on an ungrouped shape
    /// coerces it into its own group-of-one (the structural invariant that keeps blends inside instance bounds).</summary>
    /// <param name="direction">+1 forward through the cycle, -1 back.</param>
    /// <returns>The target's new blend op.</returns>
    public SdfBlendOp CycleBlend(int direction) {
        if (TargetIsGoal) {
            return SdfBlendOp.Union;
        }

        var current = Array.IndexOf(
            array: BlendCycle,
            value: (TargetShape().Blend ?? SdfBlendOp.Union)
        );
        var next = BlendCycle[(current + direction).FloorModulo(modulus: BlendCycle.Length)];

        if (TargetIsBrush) {
            m_brush = (m_brush with { Blend = next });
            Revision++;
        } else {
            var (index, shape) = ResolveSelectedShape()!.Value;
            var newGroup = (((next != SdfBlendOp.Union) && ((shape.Group ?? 0) == 0)) ? m_nextGroupId++ : (shape.Group ?? 0));
            var shapes = new List<ShapeDocument>(collection: m_document.Shapes!) {
                [index] = (shape with { Blend = next, Group = newGroup }),
            };

            m_document = (m_document with { Shapes = shapes });
            Revision++;
            PushUndo();
        }

        return next;
    }
    /// <summary>Cycles the rig-page current-chain cursor (which chain kind/delete verbs act on) — separate from the
    /// goal-target selection. Wraps through "none".</summary>
    /// <param name="direction">+1 for the next chain, -1 for the previous.</param>
    /// <returns>The cursor's chain, or null (none).</returns>
    public ChainDocument? CycleChainCursor(int direction) {
        var chains = Chains;

        if (chains.Count == 0) {
            m_chainCursorId = null;

            return null;
        }

        var current = ((m_chainCursorId is { } id) ? chains.ToList().FindIndex(match: c => (c.Id == id)) : -1);
        var next = (current + direction);

        next = ((next >= chains.Count)
            ? -1
            : ((next < -1)
                ? (chains.Count - 1)
                : next
        ));
        m_chainCursorId = ((next >= 0) ? chains[next].Id : null);
        Revision++;

        return ((next >= 0) ? chains[next] : null);
    }
    /// <summary>Cycles the target's material through the palette (wraps).</summary>
    /// <param name="direction">+1 for the next palette slot, -1 for the previous.</param>
    /// <returns>The target's new palette slot.</returns>
    public int CycleMaterial(int direction) {
        if (TargetIsGoal) {
            return 0;
        }

        var next = ((TargetShape().Material ?? 0) + direction).FloorModulo(modulus: CreationDocument.PaletteSize);

        ApplyToTarget(
            mutate: s => (s with { Material = next }),
            pushUndo: true
        );

        return next;
    }
    /// <summary>Cycles the target's primitive (wraps both directions): the brush's when nothing is selected (the
    /// next add changes), the selected shape's otherwise (re-primitive in place).</summary>
    /// <param name="direction">+1 for the next primitive, -1 for the previous.</param>
    /// <returns>The target's new primitive.</returns>
    public AvatarPrimitive CyclePrimitive(int direction) {
        if (TargetIsGoal) {
            return m_brush.Type;
        }

        var next = ((AvatarPrimitive)((((int)TargetShape().Type) + direction).FloorModulo(modulus: PrimitiveCount)));

        ApplyToTarget(
            mutate: s => (s with { Type = next }),
            pushUndo: true
        );

        return next;
    }
    /// <summary>Cycles the selection through the shapes, then past them into the defined chains' goals, wrapping
    /// through "none" (where the target reverts to the brush) at either end.</summary>
    /// <param name="direction">+1 for the next shape/goal, -1 for the previous.</param>
    public void CycleSelection(int direction) {
        var shapes = (m_document.Shapes ?? []);
        var chains = Chains;

        if ((shapes.Count == 0) && (chains.Count == 0)) {
            return;
        }

        var combined = (TargetIsGoal
            ? (shapes.Count + ResolveGoalChainIndex()!.Value)
            : (ResolveSelectedShape()?.Index ?? -1)
        );
        var span = (shapes.Count + chains.Count);
        var next = (combined + direction);

        next = ((next >= span)
            ? -1
            : ((next < -1)
                ? (span - 1)
                : next
        ));

        if (m_selectedShapeId is { } current) {
            m_previousSelectedShapeId = current;
        }

        if (next >= shapes.Count) {
            m_selectedShapeId = null;
            m_goalChainId = chains[(next - shapes.Count)].Id;
        } else if (next >= 0) {
            m_selectedShapeId = shapes[next].Id;
            m_goalChainId = null;
        } else {
            m_selectedShapeId = null;
            m_goalChainId = null;
        }

        Revision++;
    }
    // ---- the rig (chains + IK) ----------------------------------------------------------------------------------

    /// <summary>Defines a new chain from the given shapes (root→tip order), capturing their current positions as the
    /// rest geometry.</summary>
    /// <param name="name">The player-given name (the goal-cycling/console handle); null for unnamed.</param>
    /// <param name="shapeIdsOrNames">The member shape ids or names, root→tip order (at least 2).</param>
    /// <param name="kind"><c>limb</c> or <c>spine</c> (null infers limb for exactly 3 shapes, else spine).</param>
    /// <returns>The defined chain, or null when fewer than 2 shapes resolved or <see cref="MaxChains"/> is reached.</returns>
    public ChainDocument? DefineChain(string? name, IReadOnlyList<string> shapeIdsOrNames, string? kind = null) {
        ArgumentNullException.ThrowIfNull(shapeIdsOrNames);

        var ids = new List<int>(capacity: shapeIdsOrNames.Count);

        foreach (var token in shapeIdsOrNames) {
            if (ResolveShapeId(idOrName: token) is { } id) {
                ids.Add(item: id);
            }
        }

        return DefineChainCore(
            kind: kind,
            name: name,
            shapeIds: ids
        );
    }
    /// <summary>Defines a limb chain seeded from the selection: the selected shape as root, walking forward through
    /// the next 2 shapes in document order (the pad-friendly stand-in for the console verb's arbitrary list).</summary>
    /// <returns>The defined chain, or null when there was no valid 3-shape run or <see cref="MaxChains"/> is reached.</returns>
    public ChainDocument? DefineChainFromSelection() {
        if (
            TargetIsBrush ||
            TargetIsGoal ||
            (ResolveSelectedShape() is not { } selection) ||
            ((selection.Index + 2) >= (m_document.Shapes?.Count ?? 0))
        ) {
            return null;
        }

        var shapes = m_document.Shapes!;
        var ids = new[] { shapes[selection.Index].Id, shapes[(selection.Index + 1)].Id, shapes[(selection.Index + 2)].Id };

        return DefineChainCore(
            kind: ChainDocument.KindLimb,
            name: null,
            shapeIds: ids
        );
    }
    private ChainDocument? DefineChainCore(string? name, IReadOnlyList<int> shapeIds, string? kind) {
        var chains = Chains;

        if (
            (chains.Count >= MaxChains) ||
            !TryCaptureRig(
                shapeIds: shapeIds,
                shapes: (m_document.Shapes ?? []),
                rig: out var rig
            )
        ) {
            return null;
        }

        var resolvedKind = (kind ?? ((shapeIds.Count == 3) ? ChainDocument.KindLimb : ChainDocument.KindSpine));
        var newId = m_nextChainId++;
        var chain = new ChainDocument(
            Goal: rig.RestGoal,
            Id: newId,
            Kind: resolvedKind,
            Name: name,
            Pole: rig.RestPole,
            Shapes: shapeIds
        );
        var list = new List<ChainDocument>(collection: chains) { chain };

        m_document = (m_document with { Chains = list });
        m_rig[newId] = rig;
        Revision++;
        PushUndo();

        return chain;
    }
    /// <summary>Deletes a chain by id or name; a no-op when nothing matches.</summary>
    /// <param name="idOrName">The chain's id (digits) or player-given name.</param>
    /// <returns>Whether a chain was removed.</returns>
    public bool DeleteChain(string idOrName) {
        var index = ResolveChainIndex(idOrName: idOrName);

        if (index < 0) {
            return false;
        }

        var list = new List<ChainDocument>(collection: Chains);
        var removedId = list[index].Id;

        list.RemoveAt(index: index);
        m_document = (m_document with { Chains = ((list.Count > 0) ? list : null) });

        if (m_goalChainId == removedId) {
            m_selectedShapeId = null;
            m_goalChainId = null;
        }

        if (m_chainCursorId == removedId) {
            m_chainCursorId = null;
        }

        Revision++;
        PushUndo();

        return true;
    }
    /// <summary>Deletes the current saved frame (rest is protected); later frames renumber.</summary>
    /// <returns>Whether a frame was removed.</returns>
    public bool DeleteCurrentFrame() {
        if (m_currentFrame == 0) {
            return false;
        }

        var frames = new List<FrameDocument>(collection: m_document.Frames!);

        frames.RemoveAt(index: (m_currentFrame - 1));

        for (var index = 0; (index < frames.Count); index++) {
            var expected = $"f{(index + 1)}";

            if (frames[index].Name.StartsWith(value: 'f') && (frames[index].Name != expected)) {
                frames[index] = (frames[index] with { Name = expected });
            }
        }

        m_document = (m_document with { Frames = ((frames.Count > 0) ? frames : null) });
        m_currentFrame = Math.Min(
            val1: m_currentFrame,
            val2: frames.Count
        );
        ApplyPoses(poses: ((m_currentFrame == 0) ? m_restPose : frames[(m_currentFrame - 1)].Transforms));
        PushUndo();

        return true;
    }
    /// <summary>Deletes the selected shape (a no-op when nothing is selected). The selection clears.</summary>
    /// <returns>Whether a shape was removed.</returns>
    public bool DeleteSelected() {
        if (ResolveSelectedShape() is not { } selection) {
            return false;
        }

        var shapes = new List<ShapeDocument>(collection: m_document.Shapes!);

        shapes.RemoveAt(index: selection.Index);
        m_document = (m_document with { Shapes = shapes });
        m_selectedShapeId = null;
        m_previousSelectedShapeId = null;
        Revision++;
        PushUndo();

        return true;
    }
    /// <summary>Clears the selection (the target reverts to the brush).</summary>
    public void Deselect() {
        if (TargetIsBrush) {
            return;
        }

        SelectShapeById(id: null);
    }
    /// <summary>Duplicates the selected shape in place (nudged aside so the twin reads) and selects the twin. A
    /// duplicate of a grouped member joins the same group.</summary>
    /// <returns>Whether a shape was added (false with no selection or a spent budget).</returns>
    public bool DuplicateTarget() {
        if ((ResolveSelectedShape() is not { } selection) || (StampShapeCount >= m_shapeCapacity)) {
            return false;
        }

        var twin = (selection.Shape with {
            Id = m_nextShapeId++,
            Name = null,
            Position = ClampLocal(position: (selection.Shape.Position + new Vector3(
                x: 0.35f,
                y: 0f,
                z: 0f
            ))),
        });
        var shapes = new List<ShapeDocument>(collection: m_document.Shapes!) { twin };

        m_document = (m_document with { Shapes = shapes });
        SelectShapeById(id: twin.Id);
        PushUndo();

        return true;
    }
    /// <summary>Closes a drag whose continuous verb did not fire this frame (the stick returned to center) — call
    /// once per produced frame after every input verb has run. A drag still being touched stays open.</summary>
    public void EndInputFrame() {
        if (m_dragOpen && !m_dragTouchedThisFrame) {
            m_dragOpen = false;
        }

        m_dragTouchedThisFrame = false;
    }
    /// <summary>Links the selected shape with the previously selected one into a composition group (select A, then
    /// B, then link). Groups merge when both shapes already belong to one.</summary>
    /// <returns>The joined group id, or null when there was no valid pair to link.</returns>
    public int? LinkWithPrevious() {
        if (
            (ResolveSelectedShape() is not { } current) ||
            (m_previousSelectedShapeId is not { } previousId) ||
            (previousId == current.Shape.Id)
        ) {
            return null;
        }

        var shapes = new List<ShapeDocument>(collection: m_document.Shapes!);
        var previousIndex = shapes.FindIndex(match: s => (s.Id == previousId));

        if (previousIndex < 0) {
            return null;
        }

        var previous = shapes[previousIndex];
        var groupId = (((current.Shape.Group ?? 0) != 0)
            ? current.Shape.Group!.Value
            : (((previous.Group ?? 0) != 0)
                ? previous.Group!.Value
                : m_nextGroupId++
        ));
        var migrating = ((((previous.Group ?? 0) != 0) && (previous.Group != groupId)) ? previous.Group!.Value : 0);

        for (var index = 0; (index < shapes.Count); index++) {
            if (
                (index == current.Index) ||
                (index == previousIndex) ||
                ((migrating != 0) && ((shapes[index].Group ?? 0) == migrating))
            ) {
                shapes[index] = (shapes[index] with { Group = groupId });
            }
        }

        m_document = (m_document with { Shapes = shapes });
        Revision++;
        PushUndo();

        return groupId;
    }
    /// <summary>Replaces the model's content from a document (crossed through <see cref="CreationCanonicalizer.Normalize"/>
    /// first). Chains recapture rest geometry from the just-loaded shape positions — never trusting persisted rest
    /// data. The undo ring re-baselines (a load is a boundary; a save is not).</summary>
    /// <param name="document">The document to load.</param>
    /// <returns>How many shapes loaded (the shape budget truncates a larger document).</returns>
    public int Load(CreationDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        var normalized = CreationCanonicalizer.Normalize(document: document);
        var carriedGlyphs = 0;

        foreach (var run in (normalized.TextRuns ?? [])) {
            carriedGlyphs += run.GlyphCount;
        }

        var shapes = new List<ShapeDocument>();
        var maxShapeId = -1;
        var maxGroup = 0;

        foreach (var shape in (normalized.Shapes ?? [])) {
            if ((shapes.Count + carriedGlyphs) >= m_shapeCapacity) {
                break;
            }

            shapes.Add(item: shape);
            maxShapeId = Math.Max(val1: maxShapeId, val2: shape.Id);
            maxGroup = Math.Max(val1: maxGroup, val2: (shape.Group ?? 0));
        }

        m_rig.Clear();

        var chains = new List<ChainDocument>();
        var maxChainId = 0;

        foreach (var chain in (normalized.Chains ?? [])) {
            if (
                (chains.Count >= MaxChains) ||
                !TryCaptureRig(
                    shapeIds: chain.Shapes,
                    shapes: shapes,
                    rig: out var rig
                )
            ) {
                continue;
            }

            var captured = (chain with {
                Goal = (chain.Goal ?? rig.RestGoal),
                Pole = (chain.Pole ?? rig.RestPole),
            });

            chains.Add(item: captured);
            m_rig[chain.Id] = rig;
            maxChainId = Math.Max(val1: maxChainId, val2: chain.Id);
        }

        m_document = (normalized with {
            Chains = ((chains.Count > 0) ? chains : null),
            Name = (normalized.Name ?? "creation"),
            Shapes = shapes,
        });
        m_nextChainId = (maxChainId + 1);
        m_nextGroupId = (maxGroup + 1);
        m_nextShapeId = (maxShapeId + 1);
        m_brush = DefaultBrush();
        m_chainCursorId = null;
        m_currentFrame = 0;
        m_goalChainId = null;
        m_playing = false;
        m_previousSelectedShapeId = null;
        m_restPose = null;
        m_selectedShapeId = null;
        Revision++;
        m_history.Reset(initial: m_document);

        return shapes.Count;
    }
    /// <summary>Moves the target this frame — planar on the floor plane plus a vertical nudge — clamped inside the
    /// workbench bound. A chain-goal target moves the goal and re-solves the chain live. A no-op on the brush (there
    /// is nothing to move). Coalesces onto one undo step per drag.</summary>
    /// <param name="planar">The X/Z move (+Y of the vector is +Z).</param>
    /// <param name="vertical">The up/down nudge (+ up).</param>
    /// <param name="deltaSeconds">The frame delta.</param>
    public void Move(Vector2 planar, float vertical, float deltaSeconds) {
        const float MoveSpeed = 3.2f;
        var step = (new Vector3(
            x: planar.X,
            y: vertical,
            z: planar.Y
        ) * (MoveSpeed * deltaSeconds));

        if (step == Vector3.Zero) {
            return;
        }

        var pushAfter = TouchDrag();

        if (TargetIsGoal) {
            var index = ResolveGoalChainIndex()!.Value;
            var chains = new List<ChainDocument>(collection: Chains);
            var chain = chains[index];
            var rig = RigFor(chainId: chain.Id);

            chains[index] = (chain with { Goal = ClampLocal(position: ((chain.Goal ?? rig.RestGoal) + step)) });
            m_document = (m_document with { Chains = chains });
            SolveChains();
            PushIfDragStarted(dragStarted: pushAfter);

            return;
        }

        if (TargetIsBrush) {
            return;
        }

        var (shapeIndex, shape) = ResolveSelectedShape()!.Value;
        var shapes = new List<ShapeDocument>(collection: m_document.Shapes!) {
            [shapeIndex] = (shape with { Position = ClampLocal(position: (shape.Position + step)) }),
        };

        m_document = (m_document with { Shapes = shapes });
        Revision++;
        PushIfDragStarted(dragStarted: pushAfter);
    }
    /// <summary>Nudges the cursor chain's pole this frame (planar) — the rig page's d-pad channel.</summary>
    /// <param name="planar">The X/Z nudge.</param>
    /// <param name="deltaSeconds">The frame delta.</param>
    public void NudgePole(Vector2 planar, float deltaSeconds) {
        if ((m_chainCursorId is not { } cursorId) || (planar == Vector2.Zero)) {
            return;
        }

        var chains = Chains;
        var index = chains.ToList().FindIndex(match: c => (c.Id == cursorId));

        if (index < 0) {
            return;
        }

        const float PoleSpeed = 3.2f;
        var list = new List<ChainDocument>(collection: chains);
        var chain = list[index];
        var rig = RigFor(chainId: chain.Id);

        list[index] = (chain with {
            Pole = ((chain.Pole ?? rig.RestPole) + (new Vector3(
                x: planar.X,
                y: 0f,
                z: planar.Y
            ) * (PoleSpeed * deltaSeconds))),
        });
        m_document = (m_document with { Chains = list });
        SolveChains();
    }
    // A path targeting palette[n] needs a real JSON array of at least n+1 entries to navigate into — pads with the
    // default-sweep hue for any slot the document did not yet author, LAZILY (only when a write actually reaches
    // that far), so a load with no edits never grows the palette and stays byte-round-trippable. Untouched entries
    // pass through verbatim (no re-parse/re-format), so an already-valid but non-canonical color is never rewritten
    // by a write to some OTHER slot.
    private static List<PaletteEntryDocument> PadPaletteForWrite(IReadOnlyList<PaletteEntryDocument>? source, int minLength) {
        var target = Math.Min(val1: Math.Max(val1: minLength, val2: (source?.Count ?? 0)), val2: CreationDocument.PaletteSize);
        var padded = new List<PaletteEntryDocument>(capacity: target);

        for (var index = 0; (index < target); index++) {
            padded.Add(item: (((source is { } provided) && (index < provided.Count))
                ? provided[index]
                : new PaletteEntryDocument(
                    Color: HexColor.Format(rgb: PaletteHue(index: index)),
                    Emissive: null,
                    Specular: null,
                    Shininess: null
            )));
        }

        return padded;
    }
    /// <summary>Records the current pose: at rest a new frame appends and becomes current; on a saved frame the
    /// snapshot overwrites it.</summary>
    /// <returns>The recorded frame's display index (1-based).</returns>
    public int RecordFrame() {
        var frames = new List<FrameDocument>(collection: (m_document.Frames ?? []));

        if (m_currentFrame == 0) {
            m_restPose ??= Snapshot();
            frames.Add(item: new FrameDocument(
                Name: $"f{(frames.Count + 1)}",
                Transforms: Snapshot()
            ));
            m_currentFrame = frames.Count;
        } else {
            frames[(m_currentFrame - 1)] = (frames[(m_currentFrame - 1)] with { Transforms = Snapshot() });
        }

        m_document = (m_document with { Frames = frames });
        Revision++;
        PushUndo();

        return m_currentFrame;
    }
    /// <summary>Steps the local undo ring forward one edit.</summary>
    /// <returns>Whether a redo step was applied.</returns>
    public bool Redo() {
        if (!m_history.TryRedo(snapshot: out var snapshot)) {
            return false;
        }

        RestoreDocument(document: snapshot);

        return true;
    }
    private void RestoreDocument(CreationDocument document) {
        m_document = document;
        m_chainCursorId = null;
        m_goalChainId = null;
        m_playing = false;
        m_currentFrame = Math.Min(
            val1: m_currentFrame,
            val2: (document.Frames?.Count ?? 0)
        );
        Revision++;
    }
    /// <summary>Spins the target this frame — yaw about world up (stick X), pitch about world right (stick Y), roll
    /// about world forward — composed onto its live orientation. Coalesces onto one undo step per drag.</summary>
    /// <param name="stick">The stick vector: X yaws, Y pitches.</param>
    /// <param name="roll">The roll rate (−1 rolls left, +1 rolls right).</param>
    /// <param name="deltaSeconds">The frame delta.</param>
    public void Rotate(Vector2 stick, float roll, float deltaSeconds) {
        if (((stick == Vector2.Zero) && (roll == 0f)) || TargetIsGoal || TargetIsBrush) {
            return;
        }

        var pushAfter = TouchDrag();
        const float RotateSpeed = 2.2f; // radians/second at full deflection
        var step = (RotateSpeed * deltaSeconds);
        var delta = ((Quaternion.CreateFromAxisAngle(
            axis: Vector3.UnitY,
            angle: (stick.X * step)
        )
            * Quaternion.CreateFromAxisAngle(
            axis: Vector3.UnitX,
            angle: (-stick.Y * step)
        ))
            * Quaternion.CreateFromAxisAngle(
            axis: Vector3.UnitZ,
            angle: (roll * step)
        ));
        var (index, shape) = ResolveSelectedShape()!.Value;
        var shapes = new List<ShapeDocument>(collection: m_document.Shapes!) {
            [index] = (shape with { Rotation = Quaternion.Normalize(value: (delta * shape.Rotation)) }),
        };

        m_document = (m_document with { Shapes = shapes });
        Revision++;
        PushIfDragStarted(dragStarted: pushAfter);
    }
    /// <summary>Grows or shrinks the target this frame (uniform, multiplicative), clamped to the scale envelope. The
    /// continuous stick-driven scale gesture — <see cref="StepScale"/> is the discrete chord twin. Coalesces onto
    /// one undo step per drag.</summary>
    /// <param name="delta">The scale rate (−1 shrinks, +1 grows).</param>
    /// <param name="deltaSeconds">The frame delta.</param>
    public void ScaleUniform(float delta, float deltaSeconds) {
        if ((delta == 0f) || TargetIsGoal || TargetIsBrush) {
            return;
        }

        var pushAfter = TouchDrag();
        var factor = MathF.Exp(x: ((delta * 1.6f) * deltaSeconds));
        var (index, shape) = ResolveSelectedShape()!.Value;
        var next = Math.Clamp(
            value: (shape.Scale.X * factor),
            max: MaxScale,
            min: MinScale
        );

        if (next != shape.Scale.X) {
            var shapes = new List<ShapeDocument>(collection: m_document.Shapes!) {
                [index] = (shape with { Scale = new Vector3(value: next) }),
            };

            m_document = (m_document with { Shapes = shapes });
            Revision++;
        }

        PushIfDragStarted(dragStarted: pushAfter);
    }
    /// <summary>Selects a shape by id or (case-insensitive) name.</summary>
    /// <param name="idOrName">The shape's id (digits) or player-given name.</param>
    /// <returns>The selected shape, or null when nothing matched.</returns>
    public ShapeDocument? Select(string idOrName) {
        ArgumentNullException.ThrowIfNull(idOrName);

        if (ResolveShapeId(idOrName: idOrName) is not { } id) {
            return null;
        }

        SelectShapeById(id: id);

        return ResolveSelectedShape()!.Value.Shape;
    }
    /// <summary>Targets a chain's goal for movement, by id or name (the verb twin of cycling into goals).</summary>
    /// <param name="idOrName">The chain's id or name.</param>
    /// <returns>The targeted chain, or null when nothing matched.</returns>
    public ChainTarget? SelectGoal(string idOrName) {
        var index = ResolveChainIndex(idOrName: idOrName);

        if (index < 0) {
            return null;
        }

        if (m_selectedShapeId is { } current) {
            m_previousSelectedShapeId = current;
        }

        m_selectedShapeId = null;
        m_goalChainId = Chains[index].Id;
        Revision++;

        return TargetGoalChain;
    }
    /// <summary>Sets a chain's goal directly and re-solves (the numeric twin of a goal drag). One discrete undo step.</summary>
    /// <param name="idOrName">The chain's id or name.</param>
    /// <param name="goal">The new goal position (clamped into the workbench bound).</param>
    /// <returns>Whether a chain was found and re-solved.</returns>
    public bool SetGoal(string idOrName, Vector3 goal) {
        var index = ResolveChainIndex(idOrName: idOrName);

        if (index < 0) {
            return false;
        }

        var chains = new List<ChainDocument>(collection: Chains);

        chains[index] = (chains[index] with { Goal = ClampLocal(position: goal) });
        m_document = (m_document with { Chains = chains });
        SolveChains();
        PushUndo();

        return true;
    }
    /// <summary>Sets a chain's kind by id or name (<c>limb</c> demotes to <c>spine</c> unless it has exactly 3 shapes).</summary>
    /// <param name="idOrName">The chain's id or name.</param>
    /// <param name="kind"><c>limb</c> or <c>spine</c>.</param>
    /// <returns>The applied kind, or null when no chain matched.</returns>
    public string? SetKind(string idOrName, string kind) {
        var index = ResolveChainIndex(idOrName: idOrName);

        if (index < 0) {
            return null;
        }

        var chains = new List<ChainDocument>(collection: Chains);
        var resolved = (string.Equals(
            a: kind,
            b: ChainDocument.KindLimb,
            comparisonType: StringComparison.OrdinalIgnoreCase
        )
            ? ChainDocument.KindLimb
            : ChainDocument.KindSpine
        );

        if (
            string.Equals(
            a: resolved,
            b: ChainDocument.KindLimb,
            comparisonType: StringComparison.OrdinalIgnoreCase
        ) &&
            (chains[index].Shapes.Count != 3)
        ) {
            resolved = ChainDocument.KindSpine;
        }

        chains[index] = (chains[index] with { Kind = resolved });
        m_document = (m_document with { Chains = chains });
        SolveChains();
        PushUndo();

        return resolved;
    }
    /// <summary>Renames the creation (the document handle).</summary>
    /// <param name="name">The new name.</param>
    public void SetName(string name) {
        ArgumentException.ThrowIfNullOrEmpty(argument: name);

        m_document = (m_document with { Name = name });
        Revision++;
    }
    /// <summary>Sets a chain's pole (bend-direction hint) by id or name and re-solves.</summary>
    /// <param name="idOrName">The chain's id or name.</param>
    /// <param name="pole">The new pole position.</param>
    /// <returns>Whether a chain was found and updated.</returns>
    public bool SetPole(string idOrName, Vector3 pole) {
        var index = ResolveChainIndex(idOrName: idOrName);

        if (index < 0) {
            return false;
        }

        var chains = new List<ChainDocument>(collection: Chains);

        chains[index] = (chains[index] with { Pole = pole });
        m_document = (m_document with { Chains = chains });
        SolveChains();

        return true;
    }
    /// <summary>Moves the timeline cursor to an exact frame and applies it (see <see cref="StepFrame"/>).</summary>
    /// <param name="index">The frame (clamped to [0, <see cref="FrameCount"/>]).</param>
    public void SetFrame(int index) {
        var target = Math.Clamp(
            value: index,
            max: FrameCount,
            min: 0
        );

        if (target == m_currentFrame) {
            return;
        }

        if ((m_currentFrame == 0) && (m_restPose is null)) {
            m_restPose = Snapshot();
        }

        m_currentFrame = target;
        ApplyPoses(poses: ((target == 0) ? m_restPose : m_document.Frames![(target - 1)].Transforms));
    }
    /// <summary>Re-solves every defined chain against its live goal/pole and writes the result into its member
    /// shapes' ordinary transforms — solver output lands in the same transforms <see cref="RecordFrame"/> snapshots,
    /// which is what lets a recorded pose inherit IK with zero consumer changes.</summary>
    public void SolveChains() {
        var chains = Chains;

        if (chains.Count == 0) {
            Revision++;

            return;
        }

        var shapes = new List<ShapeDocument>(collection: (m_document.Shapes ?? []));
        var shapeIndexById = new Dictionary<int, int>(capacity: shapes.Count);

        for (var index = 0; (index < shapes.Count); index++) {
            shapeIndexById[shapes[index].Id] = index;
        }

        foreach (var chain in chains) {
            var rig = RigFor(chainId: chain.Id);
            var count = chain.Shapes.Count;

            if (m_solveScratch.Length < count) {
                m_solveScratch = new (Vector3, Quaternion)[count];
            }

            rig.Solve(
                kind: (chain.Kind ?? ChainDocument.KindSpine),
                goal: (chain.Goal ?? rig.RestGoal),
                pole: (chain.Pole ?? rig.RestPole),
                destination: m_solveScratch.AsSpan(
                    length: count,
                    start: 0
                )
            );

            for (var member = 0; (member < count); member++) {
                if (!shapeIndexById.TryGetValue(
                    key: chain.Shapes[member],
                    value: out var shapeIndex
                )) {
                    continue;
                }

                var (position, rotation) = m_solveScratch[member];

                shapes[shapeIndex] = (shapes[shapeIndex] with {
                    Position = ClampLocal(position: position),
                    Rotation = rotation,
                });
            }
        }

        m_document = (m_document with { Shapes = shapes });
        Revision++;
    }
    /// <summary>Grows or shrinks the target ~15% (a deliberate act-scale step; the continuous drag is
    /// <see cref="ScaleUniform"/>'s job). One discrete undo step.</summary>
    /// <param name="direction">+1 grows, -1 shrinks.</param>
    /// <returns>The target's new per-axis scale.</returns>
    public Vector3 StepScale(int direction) {
        if (TargetIsGoal) {
            return Vector3.One;
        }

        const float StepFactor = 1.15f;
        var factor = ((direction > 0) ? StepFactor : (1f / StepFactor));
        var next = ClampScale(scale: (TargetShape().Scale * factor));

        ApplyToTarget(
            mutate: s => (s with { Scale = next }),
            pushUndo: true
        );

        return next;
    }
    /// <summary>Steps the timeline cursor and applies the destination frame's poses (0 restores the rest pose).</summary>
    /// <param name="direction">+1 forward, -1 back (clamped to [0, <see cref="FrameCount"/>]).</param>
    /// <returns>The new cursor.</returns>
    public int StepFrame(int direction) {
        SetFrame(index: (m_currentFrame + direction));

        return m_currentFrame;
    }
    /// <summary>Advances playback (call once per frame with the frame delta): holds each saved frame for the fixed
    /// cadence, looping 1..<see cref="FrameCount"/>.</summary>
    /// <param name="deltaSeconds">The frame delta.</param>
    public void TickPlayback(float deltaSeconds) {
        if (!m_playing || (FrameCount == 0)) {
            return;
        }

        m_playClock += deltaSeconds;

        if (m_playClock < SecondsPerFrame) {
            return;
        }

        m_playClock = 0f;
        m_playCursor = ((m_playCursor + 1) % FrameCount);
        m_currentFrame = (m_playCursor + 1);
        ApplyPoses(poses: m_document.Frames![m_playCursor].Transforms);
    }
    /// <summary>Toggles the cursor chain's kind (the rig page's chord act).</summary>
    /// <returns>The applied kind, or null when no chain is cursored.</returns>
    public string? ToggleCurrentChainKind() {
        if (CurrentChain is not { } chain) {
            return null;
        }

        var next = (string.Equals(
            a: chain.Kind,
            b: ChainDocument.KindLimb,
            comparisonType: StringComparison.OrdinalIgnoreCase
        )
            ? ChainDocument.KindSpine
            : ChainDocument.KindLimb
        );

        return SetKind(
            idOrName: chain.Id.ToString(provider: CultureInfo.InvariantCulture),
            kind: next
        );
    }
    /// <summary>Toggles the frame-loop playback (needs at least one saved frame). Stopping restores rest.</summary>
    /// <returns>Whether playback is now running.</returns>
    public bool TogglePlayback() {
        if (FrameCount == 0) {
            return false;
        }

        if (m_playing) {
            StopPlayback();
        } else {
            if ((m_currentFrame == 0) && (m_restPose is null)) {
                m_restPose = Snapshot();
            }

            m_playing = true;
            m_playClock = 0f;
            m_playCursor = 0;
        }

        return m_playing;
    }
    // ---- the generic document-member-path door — TrySet/TryRemove ------------------------------------------------

    /// <summary>Sets a document member by path (the creation-scoped twin of <c>world.row.set</c>). A LEADING DOT
    /// targets the current selection (<c>.scale</c> on the selected shape, <c>.goal</c> on a targeted chain, or a
    /// flat brush field when nothing is selected — the brush is itself a <see cref="ShapeDocument"/>, so a brush
    /// field is never special-cased). A leading <c>@</c> is deliberately NOT this sugar — a console line containing
    /// one is rejected upstream as a System.CommandLine response-file token before this method ever sees it. Any
    /// other path addresses the document directly (<c>shapes[3].scale</c>, <c>palette[0].color</c>, <c>name</c>).
    /// Validated through <see cref="CreationCanonicalizer.Validate"/> before acceptance — a refused edit leaves the
    /// document untouched.</summary>
    /// <param name="path">The target path.</param>
    /// <param name="json">The payload, in the document's own wire shape.</param>
    /// <returns>The outcome.</returns>
    public EditOutcome TrySet(string path, string json) {
        ArgumentException.ThrowIfNullOrEmpty(argument: path);

        if (!path.StartsWith(value: '.')) {
            return ApplyDocumentPatch(
                json: json,
                path: path
            );
        }

        var field = path[1..];

        if (field.Length == 0) {
            return new EditOutcome(Success: false, Message: $"'{path}': a leading '.' needs a trailing field — e.g. .scale");
        }

        if (TargetIsGoal) {
            return ApplyDocumentPatch(
                json: json,
                path: $"chains[{ResolveGoalChainIndex()!.Value}].{field}"
            );
        }

        if (TargetIsBrush) {
            return TrySetBrushField(
                field: field,
                json: json
            );
        }

        return ApplyDocumentPatch(
            json: json,
            path: $"shapes[{ResolveSelectedShape()!.Value.Index}].{field}"
        );
    }
    /// <summary>Removes one array row by path (<c>shapes[3]</c>) — always by index, never a bare list or scalar
    /// field. The selection-scoped twin lives on the bare <c>editor.sculpt.remove</c> verb (no path — deletes the
    /// selected shape).</summary>
    /// <param name="path">The element's path — must end in <c>[n]</c>.</param>
    /// <returns>The outcome.</returns>
    public EditOutcome TryRemove(string path) {
        ArgumentException.ThrowIfNullOrEmpty(argument: path);

        if (path.StartsWith(value: '.')) {
            return new EditOutcome(Success: false, Message: "a leading '.' is not valid for remove — editor.sculpt.remove with no path deletes the selected shape");
        }

        var outcome = CreationDocumentPatcher.TryRemove(
            document: m_document,
            path: path
        );

        return (outcome.Ok
            ? AcceptCandidate(candidate: outcome.Document!, path: path)
            : new EditOutcome(Success: false, Message: outcome.Error!)
        );
    }
    private EditOutcome AcceptCandidate(CreationDocument candidate, string path) {
        var errors = CreationCanonicalizer.Validate(document: candidate);

        if (errors.Count > 0) {
            return new EditOutcome(Success: false, Message: DocumentCanonicalizer.FormatErrors(
                errors: errors,
                source: null
            ));
        }

        var normalized = CreationCanonicalizer.Normalize(document: candidate);
        var shapes = (normalized.Shapes ?? []);
        var maxShapeId = -1;
        var maxGroup = 0;

        foreach (var shape in shapes) {
            maxShapeId = Math.Max(val1: maxShapeId, val2: shape.Id);
            maxGroup = Math.Max(val1: maxGroup, val2: (shape.Group ?? 0));
        }

        m_nextShapeId = Math.Max(val1: m_nextShapeId, val2: (maxShapeId + 1));
        m_nextGroupId = Math.Max(val1: m_nextGroupId, val2: (maxGroup + 1));

        var maxChainId = 0;

        foreach (var chain in (normalized.Chains ?? [])) {
            maxChainId = Math.Max(val1: maxChainId, val2: chain.Id);

            // A chain a raw path edit introduced directly (never through DefineChain) has no cached rig yet —
            // capture it now, from current positions, exactly like a fresh definition would.
            if (
                !m_rig.ContainsKey(key: chain.Id) &&
                TryCaptureRig(
                    shapeIds: chain.Shapes,
                    shapes: shapes,
                    rig: out var rig
                )
            ) {
                m_rig[chain.Id] = rig;
            }
        }

        m_nextChainId = Math.Max(val1: m_nextChainId, val2: (maxChainId + 1));
        m_document = (normalized with { Shapes = shapes });
        m_currentFrame = Math.Min(
            val1: m_currentFrame,
            val2: (m_document.Frames?.Count ?? 0)
        );
        Revision++;
        PushUndo();

        return new EditOutcome(Success: true, Message: $"'{path}' set");
    }
    private EditOutcome ApplyDocumentPatch(string path, string json) {
        // A path reaching into palette[n] needs a real array of at least n+1 entries to navigate into — pad it
        // (default-sweep hue for any slot not yet authored) on the CANDIDATE only, so a load with no edits round-
        // trips byte-identically and only a write into the palette section ever grows it.
        var basis = (TryPaletteWriteMinLength(
            minLength: out var minLength,
            path: path
        )
            ? (m_document with { Palette = PadPaletteForWrite(minLength: minLength, source: m_document.Palette) })
            : m_document
        );
        var outcome = CreationDocumentPatcher.TrySet(
            document: basis,
            json: json,
            path: path
        );

        return (outcome.Ok
            ? AcceptCandidate(candidate: outcome.Document!, path: path)
            : new EditOutcome(Success: false, Message: outcome.Error!)
        );
    }
    // Recognizes a leading "palette" or "palette[n]" path segment and the array length a write there needs.
    private static bool TryPaletteWriteMinLength(string path, out int minLength) {
        var firstDot = path.IndexOf(value: '.');
        var head = ((firstDot < 0) ? path : path[..firstDot]);

        if (!head.StartsWith(
            value: "palette",
            comparisonType: StringComparison.Ordinal
        )) {
            minLength = 0;

            return false;
        }

        if (head.Length == "palette".Length) {
            minLength = 0;

            return true;
        }

        var bracket = head.IndexOf(value: '[');

        if (
            (bracket != "palette".Length) ||
            !head.EndsWith(value: ']') ||
            !int.TryParse(
                s: head[(bracket + 1)..^1],
                result: out var index
            ) ||
            (index < 0)
        ) {
            minLength = 0;

            return false;
        }

        minLength = (index + 1);

        return true;
    }
    // The brush is a flat ShapeDocument — one property deep, never nested/indexed — so its own patch path is a
    // direct property-node replace rather than the full CreationDocumentPatcher machinery.
    private EditOutcome TrySetBrushField(string field, string json) {
        if (field.Contains(value: '.') || field.Contains(value: '[')) {
            return new EditOutcome(Success: false, Message: $"'.{field}': brush fields are flat — e.g. .scale, not a nested path");
        }

        if (!CreationDocumentPatcher.TryParseJsonLenient(
            error: out var parseError,
            json: json,
            payload: out var payload
        )) {
            return new EditOutcome(Success: false, Message: $"'.{field}': {parseError}");
        }

        var node = (JsonSerializer.SerializeToNode(
            value: m_brush,
            options: DocumentJsonOptions.Shared
        )?.AsObject() ?? throw new InvalidOperationException(message: "the brush serialized to a null node"));

        if (!node.ContainsKey(propertyName: field)) {
            return new EditOutcome(Success: false, Message: $"'.{field}': unknown brush field '{field}'");
        }

        node[field] = payload;

        try {
            var candidate = JsonSerializer.Deserialize<ShapeDocument>(
                node: node,
                options: DocumentJsonOptions.Shared
            );

            if (candidate is null) {
                return new EditOutcome(Success: false, Message: $"'.{field}': patched brush parsed to null");
            }

            m_brush = ClampBrush(shape: candidate);
            Revision++;

            return new EditOutcome(Success: true, Message: $"brush '{field}' set");
        } catch (JsonException exception) {
            return new EditOutcome(Success: false, Message: $"'.{field}': {exception.Message.ReplaceLineEndings(replacementText: " ")}");
        }
    }
    /// <summary>Steps the local undo ring back one edit, restoring the whole document.</summary>
    /// <returns>Whether an undo step was applied.</returns>
    public bool Undo() {
        if (!m_history.TryUndo(snapshot: out var snapshot)) {
            return false;
        }

        RestoreDocument(document: snapshot);

        return true;
    }
    /// <summary>Dissolves the target's group: every member returns to ungrouped, and — the structural invariant —
    /// every member's blend returns to plain Union (an ungrouped shape may not carry a blend).</summary>
    /// <returns>How many shapes left the group (0 when the target was ungrouped, the brush, or a chain goal).</returns>
    public int UngroupTarget() {
        if ((ResolveSelectedShape() is not { } selection) || ((selection.Shape.Group ?? 0) == 0)) {
            return 0;
        }

        var groupId = selection.Shape.Group!.Value;
        var shapes = new List<ShapeDocument>(collection: m_document.Shapes!);
        var released = 0;

        for (var index = 0; (index < shapes.Count); index++) {
            if ((shapes[index].Group ?? 0) == groupId) {
                shapes[index] = (shapes[index] with { Blend = SdfBlendOp.Union, Group = 0, Smooth = 0f });
                released++;
            }
        }

        m_document = (m_document with { Shapes = shapes });
        Revision++;
        PushUndo();

        return released;
    }
}
