using System.Numerics;
using System.Text.Json;
using Puck.Assets;
using Puck.Demo.Creator;
using Puck.Demo.Editing;
using Puck.Demo.Forge;

namespace Puck.Demo.World;

/// <summary>One stamped assembly in the live model: a placed creation (by content hash), its transform, optional
/// repeat, and anchor role. Mirrors <see cref="PlacementDocument"/> with resolved (non-nullable) authoring state.</summary>
/// <param name="Id">The placement's stable id (unique within the scene; survives deletes — console selection keys
/// on it).</param>
/// <param name="RefName">The referenced creation's ref name (display/rebind convenience; the hash is the identity).</param>
/// <param name="SourceHash">The referenced creation's content hash (<c>sha256/&lt;hex64&gt;</c>).</param>
/// <param name="Position">The stamp position (world space, floor-snapped by the authoring UX).</param>
/// <param name="YawDegrees">The stamp yaw.</param>
/// <param name="Scale">The uniform stamp scale.</param>
/// <param name="Repeat">The repeat block (null = a single copy).</param>
/// <param name="Role">The anchor role (null = decoration; <c>cabinet:&lt;n&gt;</c> re-homes a console stand).</param>
/// <param name="Mirror">The symmetry fold axis (<c>x</c> or <c>z</c>, the placement's LOCAL frame; null = none).</param>
/// <param name="Pattern">The wallpaper pattern block (null = none).</param>
public readonly record struct WorldPlacement(
    int Id,
    string RefName,
    string SourceHash,
    Vector3 Position,
    float YawDegrees,
    float Scale,
    WorldPlacementRepeat? Repeat,
    string? Role,
    string? Mirror,
    WorldPlacementPattern? Pattern
);

/// <summary>A placement's repeat block (mirrors <see cref="PlacementRepeatDocument"/>).</summary>
/// <param name="SpacingX">The per-copy X spacing.</param>
/// <param name="SpacingZ">The per-copy Z spacing.</param>
/// <param name="CountX">The copy count along X (1 = no repeat on the axis).</param>
/// <param name="CountZ">The copy count along Z.</param>
public readonly record struct WorldPlacementRepeat(float SpacingX, float SpacingZ, int CountX, int CountZ) {
    /// <summary>The total copy count (X × Z).</summary>
    public int TotalCount => (CountX * CountZ);
}

/// <summary>A placement's wallpaper pattern block (mirrors <see cref="PlacementPatternDocument"/> field-for-field —
/// the group stays a canonical member-name STRING (null = P1) so a load→save round-trip is byte-identical; the
/// renderer parses it at emission).</summary>
/// <param name="Group">The wallpaper group name (a canonical <see cref="Puck.SdfVm.SdfWallpaperGroup"/> member
/// name; null = P1).</param>
/// <param name="CellWidth">The fold cell width (world units, X).</param>
/// <param name="CellHeight">The fold cell height (world units, Z).</param>
/// <param name="LimitX">The fold's cell-count limit along X (null = unlimited).</param>
/// <param name="LimitZ">The fold's cell-count limit along Z (null = unlimited).</param>
/// <param name="MaterialStride">The per-cell material stride (null = 0, no variation).</param>
public readonly record struct WorldPlacementPattern(
    string? Group,
    float CellWidth,
    float CellHeight,
    float? LimitX,
    float? LimitZ,
    int? MaterialStride
);

/// <summary>One authored terrain patch (mirrors <see cref="TerrainPatchDocument"/>).</summary>
/// <param name="Id">The patch's stable id.</param>
/// <param name="Kind">The patch kind (<c>slab</c> or <c>plaza</c>).</param>
/// <param name="Center">The patch center.</param>
/// <param name="HalfExtents">The patch half extents.</param>
/// <param name="Material">The world palette slot.</param>
public readonly record struct WorldTerrainPatch(int Id, string Kind, Vector3 Center, Vector3 HalfExtents, int Material);

/// <summary>One authored light emitter (mirrors <see cref="WorldLightDocument"/>).</summary>
/// <param name="Id">The light's stable id.</param>
/// <param name="Position">The emitter position.</param>
/// <param name="Color">The emitter color.</param>
/// <param name="Intensity">The emitter strength.</param>
public readonly record struct WorldLight(int Id, Vector3 Position, Vector3 Color, float Intensity);

/// <summary>One authored walkability override (mirrors <see cref="WalkOverrideDocument"/>). Sim-side data — the
/// renderer draws it only as a ghost outline while the overrides page is active.</summary>
/// <param name="Id">The override's stable id.</param>
/// <param name="Kind"><c>blocker</c> or <c>walkable</c>.</param>
/// <param name="MinX">The rectangle's minimum X.</param>
/// <param name="MinZ">The rectangle's minimum Z.</param>
/// <param name="MaxX">The rectangle's maximum X.</param>
/// <param name="MaxZ">The rectangle's maximum Z.</param>
public readonly record struct WorldWalkOverride(int Id, string Kind, float MinX, float MinZ, float MaxX, float MaxZ);

/// <summary>The authored lot bounds (mirrors <see cref="WorldBoundsDocument"/>).</summary>
/// <param name="MinX">The lot's minimum X.</param>
/// <param name="MinZ">The lot's minimum Z.</param>
/// <param name="MaxX">The lot's maximum X.</param>
/// <param name="MaxZ">The lot's maximum Z.</param>
/// <param name="FloorY">The floor plane's height.</param>
public readonly record struct WorldBounds(float MinX, float MinZ, float MaxX, float MaxZ, float FloorY) {
    /// <summary>The default 16×16 room the code-built world starts with.</summary>
    public static WorldBounds Default => new(MinX: -8f, MinZ: -8f, MaxX: 8f, MaxZ: 8f, FloorY: 0f);

    /// <summary>Clamps a position's X/Z inside the lot (Y untouched).</summary>
    /// <param name="position">The candidate position.</param>
    /// <returns>The clamped position.</returns>
    public Vector3 Clamp(Vector3 position) =>
        new(Math.Clamp(value: position.X, max: MaxX, min: MinX), position.Y, Math.Clamp(value: position.Z, max: MaxZ, min: MinZ));

    /// <summary>The lot's planar center.</summary>
    public Vector3 Center => new((0.5f * (MinX + MaxX)), FloorY, (0.5f * (MinZ + MaxZ)));
}

/// <summary>
/// The authored world scene — the in-memory model for <c>puck.world.v1</c>. Owns placements (stamped creations),
/// terrain patches, lights, walk overrides, and the authored bounds. Every mutation verb lives here; the pad
/// controller (<see cref="WorldSculptController"/>) and the console verbs (<see cref="WorldCommands"/>) both drive
/// this one model — the same TARGET pattern <c>Puck.Demo.Creator.CreatorScene</c> established (edits act on the
/// selected placement when there is one, else the ghost stamp).
///
/// Two counters expose change, mirroring <c>CreatorScene</c>'s settled split: <see cref="Revision"/> bumps on EVERY
/// mutation (a cheap poll seam); <see cref="ProgramRevision"/> bumps only on a STRUCTURAL edit (place/delete/
/// repeat-change/bake-back-on-release) — the frame source rebuilds the SDF program only then. A move/rotate/scale
/// mid-drag rides the two DYNAMIC slots (the ghost stamp + the selected stamp while dragging) and bumps Revision
/// only, per the drag contract: <see cref="BeginDrag"/> snapshots the undo baseline on the drag's START edge, and
/// the in-progress drag itself never pushes another snapshot.
/// </summary>
public sealed class WorldScene {
    /// <summary>The maximum number of placements a world may hold. The engine's program/instance buffers reserve
    /// capacity for the full budget up front (the frame source probes the worst case at construction).</summary>
    public const int MaxPlacements = 128;
    /// <summary>The maximum shape count a single stamped creation's chain may replay. <c>world.place</c> dry-measures
    /// a candidate creation against this and refuses an oversized assembly with a friendly narration rather than
    /// building a program that could exceed the probed envelope.</summary>
    public const int MaxShapesPerStamp = 24;
    /// <summary>A placement row (repeat count on one axis) beyond this auto-splits into several placements so each
    /// segment's instance bound stays tight (a full-length row's enclosing sphere would defeat the tile cull).
    /// <see cref="MaxPlacements"/> budgets the SEGMENT total (not the logical placement count), so an auto-split can
    /// never push the emission past the probe's instance reservation — refusals happen at authoring time
    /// (<see cref="Place"/>/<see cref="SetSelectedRepeat"/>), by construction.</summary>
    public const int MaxRepeatPerSegment = 8;
    /// <summary>The terrain patch budget (the probe reserves exactly this many world-set slabs).</summary>
    public const int MaxTerrainPatches = 128;
    /// <summary>The light emitter budget (the probe reserves exactly this many emissive spheres).</summary>
    public const int MaxLights = 128;
    /// <summary>The walk-override budget (the probe reserves exactly this many ghost outlines).</summary>
    public const int MaxWalkOverrides = 128;
    /// <summary>The placed-camera budget. Cameras are cheap DATA (a posed eye marker); the number that can produce a
    /// live FEED at once is the far smaller <c>CameraFeedPool.MaxCameraFeeds</c> (each feed is a render pass) — the
    /// wiring model is what decides which eyes get a feed, so a world may hold many eyes and light only a few.</summary>
    public const int MaxCameras = 64;
    /// <summary>The uniform-scale envelope a placement may be grown/shrunk to.</summary>
    public const float MinScale = 0.2f;
    public const float MaxScale = 5.0f;

    private readonly List<WorldPlacement> m_placements = new(capacity: MaxPlacements);
    private readonly List<WorldTerrainPatch> m_terrain = [];
    private readonly List<WorldLight> m_lights = [];
    private readonly List<WorldWalkOverride> m_walkOverrides = [];
    private readonly List<CameraEye> m_cameras = [];
    // The screen-surface wiring table: at most one source per screen index. A dictionary keeps the set-semantics the
    // world.wire verb wants (wiring a taken screen replaces its source); the render node reads it to route each screen.
    private readonly Dictionary<int, ScreenWireSource> m_wiring = [];
    private int m_nextCameraId;
    private readonly Dictionary<string, CreationDocumentCacheEntry> m_creationCache = new(comparer: StringComparer.Ordinal);
    private WorldBounds m_bounds = WorldBounds.Default;
    private string m_name = "world";
    private int m_nextPlacementId;
    private int m_nextTerrainId;
    private int m_nextLightId;
    private int m_nextWalkOverrideId;
    private int m_selectionIndex = -1;
    private bool m_dragging;
    // TotalSegmentCount's memo — null means "dirty, recompute on next access" (see MarkProgramChanged).
    private int? m_totalSegmentCount;
    // The two sim-config knobs the document carries beside the authored content: neither renders nor rebuilds
    // (no Revision/ProgramRevision impact) — they take effect at save/load, applied by the host's save hook.
    private string m_walkGridKind = "square";
    private string m_movementLock = "free";
    // Grid-locking (session-only authoring state — the resolved F6 rule; never persisted, saves stay byte-identical).
    // m_snap carries the enable/pitch/rotation/reference; m_snapIntent is the retained pre-snap integrated cursor
    // (the resolved F3 magnetize-while-dragging source of truth) and m_snapLastCommitted is the last value the snap
    // path wrote — an external mutation (select/exact-set/undo) leaves current != last, reseeding the intent.
    private SnapConfig m_snap = SnapConfig.WorldDefault;
    private Vector3 m_snapIntent;
    private Vector3 m_snapLastCommitted;
    private bool m_snapGridVisible;

    // The ghost stamp — the ready-to-place creation, anchor-relative to the player.
    private string? m_ghostRefName;
    private string? m_ghostSourceHash;
    private Vector3 m_ghostPosition;
    private float m_ghostYawDegrees;
    private float m_ghostScale = 1f;

    /// <summary>One resolved creation's palette registration + shape chain, cached by content hash so 128
    /// placements referencing the same creation register its materials exactly once per program build.</summary>
    private sealed record CreationDocumentCacheEntry(CreationDocument Document, int ShapeCount);

    /// <summary>Bumps on EVERY mutation.</summary>
    public int Revision { get; private set; }
    /// <summary>Bumps only on a STRUCTURAL edit (the SDF program must rebuild).</summary>
    public int ProgramRevision { get; private set; }

    /// <summary>The stamped placements, in document order.</summary>
    public IReadOnlyList<WorldPlacement> Placements => m_placements;
    /// <summary>The authored terrain patches.</summary>
    public IReadOnlyList<WorldTerrainPatch> Terrain => m_terrain;
    /// <summary>The authored light emitters.</summary>
    public IReadOnlyList<WorldLight> Lights => m_lights;
    /// <summary>The authored walkability overrides.</summary>
    public IReadOnlyList<WorldWalkOverride> WalkOverrides => m_walkOverrides;
    /// <summary>The placed camera eyes, in insertion order.</summary>
    public IReadOnlyList<CameraEye> Cameras => m_cameras;

    /// <summary>Resolves a placement's LIVE world pose by id — position plus heading in radians — so a camera eye
    /// anchored to that placement rides it as it is dragged/rotated (Move/Rotate mutate <see cref="m_placements"/> in
    /// place). Returns <see langword="false"/> when no placement has the id (the caller keeps the zero pose).</summary>
    /// <param name="placementId">The placement id the eye is anchored to (<see cref="CameraEye.AnchorId"/>).</param>
    /// <param name="position">The placement's live world-space origin, or zero when unresolved.</param>
    /// <param name="yawRadians">The placement's live heading in radians, or zero when unresolved.</param>
    /// <returns>Whether a placement with the id exists.</returns>
    public bool TryResolvePlacementPose(int placementId, out Vector3 position, out float yawRadians) {
        foreach (var placement in m_placements) {
            if (placement.Id == placementId) {
                position = placement.Position;
                yawRadians = float.DegreesToRadians(degrees: placement.YawDegrees);

                return true;
            }
        }

        position = Vector3.Zero;
        yawRadians = 0f;

        return false;
    }
    /// <summary>The screen-surface wiring table (screen index → the source it displays). At most one entry per screen
    /// index; a screen with no entry falls back to its default behavior (its brick viewport / flat material).</summary>
    public IReadOnlyDictionary<int, ScreenWireSource> Wiring => m_wiring;
    /// <summary>The authored lot bounds.</summary>
    public WorldBounds Bounds => m_bounds;
    /// <summary>The world's save/load handle.</summary>
    public string Name => m_name;
    /// <summary>The walk grid tessellation the NEXT save bakes (<c>square</c> or <c>hex</c>). Sim config, not
    /// content: no Revision/ProgramRevision impact (nothing renders), but snapshot-covered so undo restores it.</summary>
    public string WalkGridKind => m_walkGridKind;
    /// <summary>The world's movement direction lock (<c>free</c>, <c>four</c>, <c>eight</c>, or <c>hex</c>) —
    /// applied to the sim on save/load, same posture as <see cref="WalkGridKind"/>.</summary>
    public string MovementLock => m_movementLock;
    /// <summary>The emission SEGMENT total across every placement (a repeat row splits into
    /// ceil(count/<see cref="MaxRepeatPerSegment"/>) segments per axis) — the quantity
    /// <see cref="MaxPlacements"/> budgets, so the probe's instance reservation holds by construction. Memoized
    /// (invalidated in <see cref="MarkProgramChanged"/>, the same edge every placement add/remove/repeat-change
    /// already crosses) rather than rescanned every access.</summary>
    public int TotalSegmentCount {
        get {
            if (m_totalSegmentCount is { } cached) {
                return cached;
            }

            var total = 0;

            foreach (var placement in m_placements) {
                total += SegmentCountOf(repeat: placement.Repeat);
            }

            m_totalSegmentCount = total;

            return total;
        }
    }
    /// <summary>The selected placement's index (-1 = none; the ghost stamp is then the target).</summary>
    public int SelectionIndex => m_selectionIndex;
    /// <summary>Whether edit verbs currently target the ghost stamp (no placement is selected).</summary>
    public bool TargetIsGhost => ((m_selectionIndex < 0) || (m_selectionIndex >= m_placements.Count));
    /// <summary>The selected placement, when there is one.</summary>
    public WorldPlacement? SelectedPlacement => (TargetIsGhost ? null : m_placements[m_selectionIndex]);
    /// <summary>Whether a drag is in progress (the selected stamp or the ghost rides its dynamic slot).</summary>
    public bool Dragging => m_dragging;

    /// <summary>The ghost stamp's referenced creation ref name (null = nothing loaded to place yet).</summary>
    public string? GhostRefName => m_ghostRefName;
    /// <summary>The ghost stamp's referenced creation source hash.</summary>
    public string? GhostSourceHash => m_ghostSourceHash;
    /// <summary>The ghost stamp's live position.</summary>
    public Vector3 GhostPosition => m_ghostPosition;
    /// <summary>The ghost stamp's live yaw, in degrees.</summary>
    public float GhostYawDegrees => m_ghostYawDegrees;
    /// <summary>The ghost stamp's live uniform scale.</summary>
    public float GhostScale => m_ghostScale;
    /// <summary>Whether the ghost stamp has a creation loaded and can be placed.</summary>
    public bool GhostReady => (m_ghostSourceHash is { Length: > 0 });

    /// <summary>The TARGET's live position (the selected placement's, else the ghost's).</summary>
    public Vector3 TargetPosition => (TargetIsGhost ? m_ghostPosition : m_placements[m_selectionIndex].Position);
    /// <summary>The TARGET's live yaw, in degrees.</summary>
    public float TargetYawDegrees => (TargetIsGhost ? m_ghostYawDegrees : m_placements[m_selectionIndex].YawDegrees);
    /// <summary>The TARGET's live uniform scale.</summary>
    public float TargetScale => (TargetIsGhost ? m_ghostScale : m_placements[m_selectionIndex].Scale);

    /// <summary>Raised after a successful <see cref="Save"/> — the integration point a host wires to trigger the
    /// sim's walk-grid hot-reload (bake happens at ORCHESTRATOR integration, not here). Carries the save handle and
    /// the content hash (null when saved without a <see cref="Puck.Assets.ContentAddressedStore"/>).</summary>
    public event Action<string, string?>? SaveCompleted;

    /// <summary>Loads (and caches) the creation a content hash resolves to, deserializing store bytes with
    /// <see cref="CreationStore"/>'s own JSON discipline (IncludeFields, camelCase, enum-as-string) so a byte-
    /// identical document round-trips regardless of entry point.</summary>
    /// <param name="hash">The content hash (<c>sha256/&lt;hex64&gt;</c>).</param>
    /// <param name="store">The content-addressed store to resolve against.</param>
    /// <returns>The normalized document, or null when the hash does not resolve.</returns>
    public CreationDocument? ResolveCreation(string hash, ContentAddressedStore store) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: hash);
        ArgumentNullException.ThrowIfNull(store);

        if (m_creationCache.TryGetValue(key: hash, value: out var cached)) {
            return cached.Document;
        }

        if (!store.TryGet(hash: hash, content: out var bytes)) {
            return null;
        }

        var document = CreationDocumentBytes.Deserialize(bytes: bytes);

        if (document is null) {
            return null;
        }

        m_creationCache[hash] = new CreationDocumentCacheEntry(Document: document, ShapeCount: (document.Shapes?.Count ?? 0));

        return document;
    }

    /// <summary>Dry-measures a candidate creation's shape count without caching it as the ghost — the refusal path
    /// <c>world.place</c> uses before committing to an oversized assembly.</summary>
    /// <param name="hash">The candidate creation's content hash.</param>
    /// <param name="store">The content-addressed store to resolve against.</param>
    /// <param name="shapeCount">The resolved shape count (0 when unresolved).</param>
    /// <returns>Whether the hash resolved to a creation at all.</returns>
    public bool TryMeasureCreation(string hash, ContentAddressedStore store, out int shapeCount) {
        var document = ResolveCreation(hash: hash, store: store);

        shapeCount = (document?.Shapes?.Count ?? 0);

        return (document is not null);
    }

    /// <summary>Sets the ghost stamp's referenced creation (the console <c>world.place</c> preflight, or a rebind).
    /// Refuses an oversized creation (over <see cref="MaxShapesPerStamp"/> shapes) with a friendly message rather
    /// than accepting a ghost that could never legally place.</summary>
    /// <param name="refName">The creation's ref name (display/rebind convenience).</param>
    /// <param name="hash">The creation's content hash.</param>
    /// <param name="store">The content-addressed store to resolve/measure against.</param>
    /// <param name="refusal">A friendly refusal message when the creation is oversized or unresolved.</param>
    /// <returns>Whether the ghost was armed.</returns>
    public bool TryArmGhost(string refName, string hash, ContentAddressedStore store, out string? refusal) {
        if (!TryMeasureCreation(hash: hash, store: store, shapeCount: out var shapeCount)) {
            refusal = $"'{refName}' did not resolve in the store — creations save it first.";

            return false;
        }

        if (shapeCount > MaxShapesPerStamp) {
            refusal = $"'{refName}' has {shapeCount} shapes — the budget per stamp is {MaxShapesPerStamp}. Simplify it before placing.";

            return false;
        }

        m_ghostRefName = refName;
        m_ghostSourceHash = hash;
        refusal = null;
        Revision++;

        return true;
    }

    /// <summary>Moves the TARGET this frame — planar on the floor plus a vertical (yaw) nudge is handled separately
    /// — clamped inside the authored bounds. A pure transform update (Revision only).</summary>
    /// <param name="planar">The X/Z move.</param>
    /// <param name="deltaSeconds">The frame delta.</param>
    public void Move(Vector2 planar, float deltaSeconds) {
        const float moveSpeed = 4.0f;
        var step = (new Vector3(planar.X, 0f, planar.Y) * (moveSpeed * deltaSeconds));

        if (step == Vector3.Zero) {
            return;
        }

        if (TargetIsGhost) {
            m_ghostPosition = SnapAndClampMove(current: m_ghostPosition, step: step);
        } else {
            var placement = m_placements[m_selectionIndex];

            m_placements[m_selectionIndex] = placement with { Position = SnapAndClampMove(current: placement.Position, step: step) };
        }

        Revision++;
    }

    // The one interception point both the pad path and the console exact-set path funnel through (WorldCommands
    // synthesizes a delta and calls Move). When snap is off this is exactly today's clamp — so a verb lands where
    // asked; when snap is on the integrated INTENT (retained un-snapped so sub-pitch aiming survives) is snapped to
    // the lattice/reference before the bounds clamp, so the pad ghost and the verb both land on the grid.
    private Vector3 SnapAndClampMove(Vector3 current, Vector3 step) {
        if (!m_snap.Enabled) {
            return m_bounds.Clamp(position: (current + step));
        }

        // An external mutation moved the target out from under the last snap write — reseed the intent from it.
        if (current != m_snapLastCommitted) {
            m_snapIntent = current;
        }

        m_snapIntent += step;

        var snapped = GridSnap.Apply(intent: m_snapIntent, config: in m_snap, candidateLocalHalfExtents: TargetSnapHalfExtents(), previousSnapped: current);
        var clamped = m_bounds.Clamp(position: snapped);

        m_snapLastCommitted = clamped;

        return clamped;
    }

    /// <summary>Spins the TARGET this frame about world up. A pure transform update (Revision only).</summary>
    /// <param name="rate">The yaw rate, −1..1.</param>
    /// <param name="deltaSeconds">The frame delta.</param>
    public void Rotate(float rate, float deltaSeconds) {
        if (rate == 0f) {
            return;
        }

        const float rotateSpeed = 90f; // degrees/second at full deflection
        var deltaDegrees = (rate * rotateSpeed * deltaSeconds);

        if (TargetIsGhost) {
            m_ghostYawDegrees = SnapYaw(yawDegrees: WrapDegrees(degrees: (m_ghostYawDegrees + deltaDegrees)));
        } else {
            var placement = m_placements[m_selectionIndex];

            m_placements[m_selectionIndex] = placement with { YawDegrees = SnapYaw(yawDegrees: WrapDegrees(degrees: (placement.YawDegrees + deltaDegrees))) };
        }

        Revision++;
    }

    /// <summary>Sets the TARGET's position to an exact requested value (the console <c>world.move</c> exact-set) —
    /// path-INDEPENDENT: with snap on it snaps the REQUESTED absolute position to the nearest lattice/reference node
    /// with no magnetize band (unlike the pad drag, which retains a sub-pitch intent), then resyncs the pad
    /// accumulator so a following drag continues from the set node. Y is left at the target's current height
    /// (floor-rest), exactly as the analog <see cref="Move"/> path does.</summary>
    /// <param name="requested">The requested world position (Y ignored — floor-rest).</param>
    public void SetTargetPositionExact(Vector3 requested) {
        var planar = new Vector3(requested.X, TargetPosition.Y, requested.Z);
        var result = (m_snap.Enabled
            ? m_bounds.Clamp(position: GridSnap.Apply(intent: planar, config: in m_snap, candidateLocalHalfExtents: TargetSnapHalfExtents(), previousSnapped: new Vector3(float.NaN)))
            : m_bounds.Clamp(position: planar));

        m_snapIntent = result;
        m_snapLastCommitted = result;

        if (TargetIsGhost) {
            m_ghostPosition = result;
        } else {
            m_placements[m_selectionIndex] = m_placements[m_selectionIndex] with { Position = result };
        }

        Revision++;
    }

    // World-sculpt's rotation snap is the scalar yaw-only path (proposal §1d): round the wrapped yaw to the
    // increment when snapping is on and a rotation increment is set; otherwise the yaw is left exact.
    private float SnapYaw(float yawDegrees) =>
        ((m_snap.Enabled && (m_snap.Rotation != RotationSnap.Off)) ? GridSnap.SnapYawDegrees(yawDegrees: yawDegrees, mode: m_snap.Rotation) : yawDegrees);

    /// <summary>The live grid-lock configuration (session-only; the frame source reads it each frame for the grid
    /// overlay, and the verbs echo it). Internal — the <see cref="SnapConfig"/> type is authoring-side Demo state.</summary>
    internal SnapConfig Snap => m_snap;
    /// <summary>Whether the grid overlay should draw in world-sculpt (session-only). Defaults to following snap
    /// on/off; <c>world.snap grid show|hide</c> overrides it for the session.</summary>
    public bool SnapGridVisible => m_snapGridVisible;

    /// <summary>Toggles grid-snapping. Also reseeds the magnetize intent from the current target and defaults the grid
    /// overlay to follow (shows while on, hides while off — an explicit <c>snap grid show|hide</c> overrides after).</summary>
    /// <param name="enabled">Whether snapping is on.</param>
    public void SetSnapEnabled(bool enabled) {
        m_snap = m_snap with { Enabled = enabled };
        m_snapGridVisible = enabled;
        m_snapIntent = TargetPosition;
        m_snapLastCommitted = TargetPosition;
        Revision++;
    }

    /// <summary>Sets the per-axis world-lattice pitch (a component &lt;= 0 leaves that axis free — the world default
    /// keeps Y at 0 for floor-rest).</summary>
    /// <param name="pitch">The per-axis pitch.</param>
    public void SetSnapPitch(Vector3 pitch) {
        m_snap = m_snap with { Pitch = pitch };
        Revision++;
    }

    /// <summary>Sets the rotation-snap increment (yaw-only in world-sculpt).</summary>
    /// <param name="rotation">The increment (Off/Deg90/Deg45).</param>
    internal void SetSnapRotation(RotationSnap rotation) {
        m_snap = m_snap with { Rotation = rotation };
        Revision++;
    }

    /// <summary>Overrides the grid overlay's visibility for the session (independent of snap on/off).</summary>
    /// <param name="visible">Whether the overlay draws.</param>
    public void SetSnapGridVisible(bool visible) {
        m_snapGridVisible = visible;
        Revision++;
    }

    /// <summary>Captures the SELECTED placement as the frozen align-to reference (the resolved F4 rule). Refuses when
    /// nothing is selected or the creation is unresolved.</summary>
    /// <param name="echo">A human echo of what was captured (or why it was refused).</param>
    /// <returns>Whether a reference was captured.</returns>
    public bool TrySetSnapReferenceSelected(out string echo) {
        if (TargetIsGhost) {
            echo = "nothing selected — world.select <id> first, or world.snap ref <id>";

            return false;
        }

        var placement = m_placements[m_selectionIndex];

        return TrySetSnapReferenceFrom(hash: placement.SourceHash, origin: placement.Position, yawDegrees: placement.YawDegrees, scale: placement.Scale, id: placement.Id, echo: out echo);
    }

    /// <summary>Captures the placement with the given id as the frozen align-to reference WITHOUT disturbing the
    /// current selection (so the moved shape can stay selected while the reference stays fixed — the common case).</summary>
    /// <param name="id">The reference placement's id.</param>
    /// <param name="echo">A human echo of what was captured (or why it was refused).</param>
    /// <returns>Whether a reference was captured.</returns>
    public bool TrySetSnapReferenceById(int id, out string echo) {
        foreach (var placement in m_placements) {
            if (placement.Id == id) {
                return TrySetSnapReferenceFrom(hash: placement.SourceHash, origin: placement.Position, yawDegrees: placement.YawDegrees, scale: placement.Scale, id: placement.Id, echo: out echo);
            }
        }

        echo = $"no placement with id {id}";

        return false;
    }

    /// <summary>Clears the align-to reference (back to world-lattice-only).</summary>
    public void ClearSnapReference() {
        m_snap = m_snap with { Reference = null };
        Revision++;
    }

    /// <summary>Writes the grid-lock overlay channel (grid-locking §4) as primitives — the frame source threads these
    /// into <c>SdfFrame</c> without naming <see cref="GridOverlayState"/> (staying under its coupling ceiling; the
    /// scene names the facade instead). All out values are 0/identity when the grid is hidden.</summary>
    /// <param name="floorY">The room's floor plane height.</param>
    /// <param name="flags">Overlay flags (bit0 world floor grid, bit1 object grid).</param>
    /// <param name="worldPitch">The world floor grid's X/Z pitch.</param>
    /// <param name="objectOrigin">The object grid's reference origin.</param>
    /// <param name="objectFrame">The object grid's reference frame.</param>
    /// <param name="objectPitch">The object grid's in-plane pitch (X/Z).</param>
    /// <param name="objectPatchRadius">The object grid's finite-patch radius.</param>
    public void WriteGridOverlay(float floorY, out uint flags, out Vector2 worldPitch, out Vector3 objectOrigin, out Quaternion objectFrame, out Vector2 objectPitch, out float objectPatchRadius) {
        var overlay = GridOverlayState.From(snap: m_snap, gridVisible: m_snapGridVisible, floorY: floorY);

        flags = overlay.Flags;
        objectFrame = overlay.ObjectFrame;
        objectOrigin = overlay.ObjectOrigin;
        objectPatchRadius = overlay.ObjectPatchRadius;
        objectPitch = overlay.ObjectPitch;
        worldPitch = overlay.WorldPitch;
    }

    private bool TrySetSnapReferenceFrom(string hash, Vector3 origin, float yawDegrees, float scale, int id, out string echo) {
        if (!m_creationCache.TryGetValue(key: hash, value: out var entry)) {
            echo = $"#{id}'s creation is not resolved in the store — load or place it first";

            return false;
        }

        var half = LocalHalfExtentsAboutOrigin(creation: entry.Document, scale: scale);
        var frame = Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: float.DegreesToRadians(degrees: yawDegrees));

        m_snap = m_snap with {
            Reference = new SnapReference(
                FaceRadius: FaceRadiusFor(pitch: m_snap.Pitch),
                Frame: frame,
                LocalHalfExtents: half,
                Origin: origin,
                Pitch: m_snap.Pitch
            ),
        };
        echo = $"reference = #{id} @ ({origin.X:F2}, {origin.Y:F2}, {origin.Z:F2}) half-extent ({half.X:F2}, {half.Y:F2}, {half.Z:F2})";
        Revision++;

        return true;
    }

    // The moving target's half-extents along its own axes (about its origin) — the candidate bound the face-to-face
    // butt-join needs (proposal §1c/§1e). Resolved from the creation cache the ghost/placement already populated;
    // Zero (center-on-face) when the creation is not cached.
    private Vector3 TargetSnapHalfExtents() {
        var hash = (TargetIsGhost ? m_ghostSourceHash : m_placements[m_selectionIndex].SourceHash);

        if ((hash is null) || !m_creationCache.TryGetValue(key: hash, value: out var entry)) {
            return Vector3.Zero;
        }

        return LocalHalfExtentsAboutOrigin(creation: entry.Document, scale: TargetScale);
    }

    // A creation's conservative half-extents about its LOCAL origin (per axis, the outer face distance): each shape's
    // scaled local center ± the primitive's worst-case reach (the SAME reach WorldFootprintDerivation trusts), no
    // placement rotation (the reference frame carries the rotation). max(|min|, |max|) per axis reaches the outer face.
    private static Vector3 LocalHalfExtentsAboutOrigin(CreationDocument creation, float scale) {
        if (creation.Shapes is not { Count: > 0 } shapes) {
            return Vector3.Zero;
        }

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        foreach (var shape in shapes) {
            var shapeScale = ((shape.Scale == default) ? Vector3.One : shape.Scale);
            var center = (shape.Position * scale);
            var shapeMaxScale = MathF.Max(shapeScale.X, MathF.Max(shapeScale.Y, shapeScale.Z));
            var reach = (AvatarDefinition.Reach(type: shape.Type, scale: Vector3.One) * shapeMaxScale * scale);

            min = Vector3.Min(value1: min, value2: (center - new Vector3(reach)));
            max = Vector3.Max(value1: max, value2: (center + new Vector3(reach)));
        }

        return new Vector3(
            MathF.Max(MathF.Abs(min.X), MathF.Abs(max.X)),
            MathF.Max(MathF.Abs(min.Y), MathF.Abs(max.Y)),
            MathF.Max(MathF.Abs(min.Z), MathF.Abs(max.Z))
        );
    }

    private static float FaceRadiusFor(Vector3 pitch) {
        var min = float.MaxValue;

        if (pitch.X > 0f) { min = MathF.Min(x: min, y: pitch.X); }
        if (pitch.Y > 0f) { min = MathF.Min(x: min, y: pitch.Y); }
        if (pitch.Z > 0f) { min = MathF.Min(x: min, y: pitch.Z); }

        return (0.5f * ((min == float.MaxValue) ? 0.25f : min));
    }

    /// <summary>Grows or shrinks the TARGET's uniform scale this frame, clamped to the scale envelope. Scale is
    /// baked into the program, so a change flags a rebuild.</summary>
    /// <param name="rate">The scale rate, −1..1.</param>
    /// <param name="deltaSeconds">The frame delta.</param>
    public void ScaleTarget(float rate, float deltaSeconds) {
        if (rate == 0f) {
            return;
        }

        var factor = MathF.Exp((rate * 1.2f * deltaSeconds));

        if (TargetIsGhost) {
            var next = Math.Clamp(value: (m_ghostScale * factor), max: MaxScale, min: MinScale);

            if (next != m_ghostScale) {
                m_ghostScale = next;
                MarkProgramChanged();
            }
        } else {
            var placement = m_placements[m_selectionIndex];
            var next = Math.Clamp(value: (placement.Scale * factor), max: MaxScale, min: MinScale);

            if (next != placement.Scale) {
                m_placements[m_selectionIndex] = placement with { Scale = next };
                MarkProgramChanged();
            }
        }
    }

    /// <summary>Marks the drag's START edge: the undo baseline for the in-progress drag — call once when a drag
    /// begins (South-held move/rotate/scale on a selected stamp). The caller pushes the RETURNED snapshot into the
    /// <c>EditHistory</c> BEFORE any drag delta applies, and pushes the post-drag snapshot again on release (the
    /// bake-back-on-release structural edit).</summary>
    public void BeginDrag() {
        m_dragging = true;
    }

    /// <summary>Ends the drag: bakes the dynamic-slot transform back into the placement's baked state — a
    /// structural edit (bumps <see cref="ProgramRevision"/>) even though no field actually changes value, because
    /// the dynamic slot that was carrying the live transform during the drag reverts to hidden/identity and the
    /// static instance chain must re-emit at the settled transform.</summary>
    public void EndDrag() {
        if (!m_dragging) {
            return;
        }

        m_dragging = false;
        MarkProgramChanged();
    }

    /// <summary>Places the ghost stamp at its current position/yaw/scale (a no-op when the ghost is not armed or the
    /// segment budget is full — see <see cref="TotalSegmentCount"/>). Structural edit.</summary>
    /// <returns>The placed placement's id, or null when nothing was placed.</returns>
    public int? Place() {
        if (!GhostReady || ((TotalSegmentCount + 1) > MaxPlacements)) {
            return null;
        }

        var id = m_nextPlacementId++;

        m_placements.Add(item: new WorldPlacement(
            Id: id,
            Mirror: null,
            Pattern: null,
            Position: m_ghostPosition,
            RefName: m_ghostRefName!,
            Repeat: null,
            Role: null,
            Scale: m_ghostScale,
            SourceHash: m_ghostSourceHash!,
            YawDegrees: m_ghostYawDegrees
        ));
        MarkProgramChanged();

        return id;
    }

    /// <summary>Deletes the SELECTED placement (a no-op when nothing is selected). Structural edit.</summary>
    /// <returns>Whether a placement was removed.</returns>
    public bool DeleteSelected() {
        if (TargetIsGhost) {
            return false;
        }

        m_placements.RemoveAt(index: m_selectionIndex);
        m_selectionIndex = -1;
        MarkProgramChanged();

        return true;
    }

    /// <summary>Cycles the selection through the placements (wrapping through "none", where the target reverts to
    /// the ghost). Structural edit (the selected stamp renders with a highlight and rides the drag slot).</summary>
    /// <param name="direction">+1 for the next placement, -1 for the previous.</param>
    public void CycleSelection(int direction) {
        if (m_placements.Count == 0) {
            return;
        }

        var next = (m_selectionIndex + direction);

        m_selectionIndex = ((next >= m_placements.Count) ? -1 : ((next < -1) ? (m_placements.Count - 1) : next));
        MarkProgramChanged();
    }

    /// <summary>Clears the selection (the target reverts to the ghost). Structural edit.</summary>
    public void Deselect() {
        if (TargetIsGhost) {
            return;
        }

        m_selectionIndex = -1;
        MarkProgramChanged();
    }

    /// <summary>Sets or clears the SELECTED placement's repeat block directly (the console <c>world.repeat</c> verb;
    /// the pad's REPEAT page nudges counts/spacing through this same seam). A row longer than
    /// <see cref="MaxRepeatPerSegment"/> on an axis is the RENDERER's concern (auto-split at emission) — the model
    /// stores the authored intent as one logical repeat, but budgets the SEGMENTS the split will produce (see
    /// <see cref="TotalSegmentCount"/>) and refuses a repeat that would outgrow the probe's instance reservation.
    /// Structural edit.</summary>
    /// <param name="repeat">The repeat block (null clears it — a single copy).</param>
    /// <param name="refusal">A friendly refusal message on failure (no selection, or the segment budget).</param>
    /// <returns>Whether the repeat applied.</returns>
    public bool SetSelectedRepeat(WorldPlacementRepeat? repeat, out string? refusal) {
        if (TargetIsGhost) {
            refusal = "nothing selected — a repeat needs a placed stamp";

            return false;
        }

        var current = SegmentCountOf(repeat: m_placements[m_selectionIndex].Repeat);
        var next = SegmentCountOf(repeat: repeat);
        var total = ((TotalSegmentCount - current) + next);

        if (total > MaxPlacements) {
            refusal = $"that repeat needs {next} segment(s), pushing the world to {total} — the budget is {MaxPlacements}";

            return false;
        }

        m_placements[m_selectionIndex] = m_placements[m_selectionIndex] with { Repeat = repeat };
        refusal = null;
        MarkProgramChanged();

        return true;
    }

    /// <summary>Sets or clears the SELECTED placement's mirror fold axis (the console <c>world.mirror</c> verb).
    /// Structural edit — the fold is baked into the placement's instance chain.</summary>
    /// <param name="axis"><c>x</c>, <c>z</c>, or null (off).</param>
    /// <returns>Whether a placement was targeted.</returns>
    public bool SetSelectedMirror(string? axis) {
        if (TargetIsGhost) {
            return false;
        }

        m_placements[m_selectionIndex] = m_placements[m_selectionIndex] with { Mirror = axis };
        MarkProgramChanged();

        return true;
    }

    /// <summary>Sets or clears the SELECTED placement's wallpaper pattern (the console <c>world.pattern</c> verb).
    /// Structural edit — the fold is baked into the placement's instance chain.</summary>
    /// <param name="pattern">The pattern block (null clears it).</param>
    /// <returns>Whether a placement was targeted.</returns>
    public bool SetSelectedPattern(WorldPlacementPattern? pattern) {
        if (TargetIsGhost) {
            return false;
        }

        m_placements[m_selectionIndex] = m_placements[m_selectionIndex] with { Pattern = pattern };
        MarkProgramChanged();

        return true;
    }

    /// <summary>Sets the walk grid tessellation the next save bakes. No Revision impact (sim config, nothing
    /// renders); snapshot-covered, so undo restores it.</summary>
    /// <param name="kind"><c>square</c> or <c>hex</c>.</param>
    /// <returns>Whether the kind was recognized and applied.</returns>
    public bool SetWalkGridKind(string kind) {
        if (string.Equals(a: kind, b: "square", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            m_walkGridKind = "square";

            return true;
        }

        if (string.Equals(a: kind, b: "hex", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            m_walkGridKind = "hex";

            return true;
        }

        return false;
    }

    /// <summary>Sets the world's movement direction lock. No Revision impact (sim config, applied on save/load);
    /// snapshot-covered, so undo restores it.</summary>
    /// <param name="mode"><c>free</c>, <c>four</c>, <c>eight</c>, or <c>hex</c>.</param>
    /// <returns>Whether the mode was recognized and applied.</returns>
    public bool SetMovementLock(string mode) {
        foreach (var legal in (ReadOnlySpan<string>)["free", "four", "eight", "hex"]) {
            if (string.Equals(a: mode, b: legal, comparisonType: StringComparison.OrdinalIgnoreCase)) {
                m_movementLock = legal;

                return true;
            }
        }

        return false;
    }

    /// <summary>Adds a placed camera EYE, returning its new stable id. A camera is pure data — a posed viewpoint marker,
    /// not SDF geometry — so this bumps <see cref="Revision"/> only (the host re-polls the eye set), NOT
    /// <see cref="ProgramRevision"/> (no SDF program rebuild). Snapshot-covered, so undo removes it. Refuses (returns
    /// null) once <see cref="MaxCameras"/> eyes exist.</summary>
    /// <param name="position">The eye position (world space when unanchored, else the offset from the anchored stamp).</param>
    /// <param name="yaw">The eye's heading, radians.</param>
    /// <param name="pitch">The eye's tilt, radians.</param>
    /// <param name="fieldOfViewRadians">The vertical field of view (null = the engine default).</param>
    /// <param name="focusDistance">The look-at target distance (null = 1).</param>
    /// <param name="anchor">The anchor kind (only <see cref="CameraAnchorKind.World"/> and
    /// <see cref="CameraAnchorKind.Placement"/> are valid in the WORLD document; a shape anchor belongs to a creation).</param>
    /// <param name="anchorId">The anchored placement id (ignored for a standalone eye).</param>
    /// <returns>The new eye's id, or null when the camera budget is full.</returns>
    public int? AddCamera(Vector3 position, float yaw = 0f, float pitch = 0f, float? fieldOfViewRadians = null, float? focusDistance = null, CameraAnchorKind anchor = CameraAnchorKind.World, int anchorId = 0) {
        if (m_cameras.Count >= MaxCameras) {
            return null;
        }

        // A world-document eye rides a world-space frame or a placement; a shape anchor (a creation's own lens) has no
        // meaning here (no shape ids in a world), so it coerces to a standalone world eye.
        var effectiveAnchor = ((anchor == CameraAnchorKind.Placement) ? CameraAnchorKind.Placement : CameraAnchorKind.World);
        var id = m_nextCameraId++;

        m_cameras.Add(item: new CameraEye(
            Anchor: effectiveAnchor,
            AnchorId: ((effectiveAnchor == CameraAnchorKind.Placement) ? anchorId : 0),
            FieldOfViewRadians: fieldOfViewRadians,
            FocusDistance: focusDistance,
            Id: id,
            Pitch: pitch,
            Position: position,
            Yaw: yaw
        ));
        MarkRevision();

        return id;
    }

    /// <summary>Deletes the camera eye with the given id (a no-op — returns false — when no such eye exists). Also
    /// clears any wiring entry pointing at that eye's feed index, so a dangling wire never survives its camera. Bumps
    /// <see cref="Revision"/> only; snapshot-covered.</summary>
    /// <param name="id">The eye id to delete.</param>
    /// <returns>Whether an eye was removed.</returns>
    public bool DeleteCamera(int id) {
        var index = m_cameras.FindIndex(match: eye => (eye.Id == id));

        if (index < 0) {
            return false;
        }

        m_cameras.RemoveAt(index: index);
        MarkRevision();

        return true;
    }

    /// <summary>Wires a screen surface to display a source (the <c>world.wire</c> verb). Set-semantics: wiring a screen
    /// that already has a source REPLACES it. A <see cref="ScreenWireKind.None"/> source CLEARS the screen's wire (it
    /// falls back to its default behavior). Bumps <see cref="Revision"/> only (the host re-polls the table each frame);
    /// snapshot-covered. Refuses (returns false) an out-of-range screen index.</summary>
    /// <param name="screenIndex">The screen-surface slot to wire (0..<c>SdfProgramBuilder.MaxScreenSurfaces</c> - 1).</param>
    /// <param name="source">The source to display (<see cref="ScreenWireSource.None"/> clears the wire).</param>
    /// <returns>Whether the screen index was in range and the wire applied.</returns>
    public bool WireScreen(int screenIndex, ScreenWireSource source) {
        if ((screenIndex < 0) || (screenIndex >= Puck.SdfVm.SdfProgramBuilder.MaxScreenSurfaces)) {
            return false;
        }

        if (source.Kind == ScreenWireKind.None) {
            _ = m_wiring.Remove(key: screenIndex);
        } else {
            m_wiring[screenIndex] = source;
        }

        MarkRevision();

        return true;
    }

    /// <summary>Clears a screen surface's wire (the <c>world.wire clear</c> verb) — it falls back to its default
    /// behavior. Bumps <see cref="Revision"/> only; snapshot-covered.</summary>
    /// <param name="screenIndex">The screen-surface slot to clear.</param>
    /// <returns>Whether a wire was present and removed.</returns>
    public bool ClearWire(int screenIndex) {
        if (!m_wiring.Remove(key: screenIndex)) {
            return false;
        }

        MarkRevision();

        return true;
    }

    /// <summary>How many emission segments a repeat block splits into (1 for no repeat) — the auto-split contract's
    /// one arithmetic statement, shared by the model's budget and the renderer's split loop.</summary>
    /// <param name="repeat">The repeat block (null = a single copy).</param>
    /// <returns>The segment count.</returns>
    public static int SegmentCountOf(WorldPlacementRepeat? repeat) {
        if (repeat is not { } value) {
            return 1;
        }

        var segmentsX = Math.Max(val1: 1, val2: ((value.CountX + MaxRepeatPerSegment - 1) / MaxRepeatPerSegment));
        var segmentsZ = Math.Max(val1: 1, val2: ((value.CountZ + MaxRepeatPerSegment - 1) / MaxRepeatPerSegment));

        return (segmentsX * segmentsZ);
    }

    /// <summary>Sets the SELECTED placement's anchor role (<c>cabinet:&lt;n&gt;</c> re-homes a console stand; null =
    /// plain decoration). Structural edit (the role is presentation/sim metadata only, but carried on the same
    /// program-content basis as everything else this scene stores).</summary>
    /// <param name="role">The role string, or null to clear it.</param>
    /// <returns>Whether a placement was targeted.</returns>
    public bool SetSelectedRole(string? role) {
        if (TargetIsGhost) {
            return false;
        }

        m_placements[m_selectionIndex] = m_placements[m_selectionIndex] with { Role = role };
        MarkProgramChanged();

        return true;
    }

    /// <summary>Rebinds the SELECTED placement to a different (compatible) creation by name/hash — the console
    /// <c>world.rebind</c> verb; keeps position/yaw/scale/repeat, only the source changes. Structural edit.</summary>
    /// <param name="refName">The new creation's ref name.</param>
    /// <param name="hash">The new creation's content hash.</param>
    /// <param name="store">The store to measure the candidate against.</param>
    /// <param name="refusal">A friendly refusal message on failure.</param>
    /// <returns>Whether the rebind applied.</returns>
    public bool RebindSelected(string refName, string hash, ContentAddressedStore store, out string? refusal) {
        if (TargetIsGhost) {
            refusal = "nothing is selected to rebind — world.select first";

            return false;
        }

        if (!TryMeasureCreation(hash: hash, store: store, shapeCount: out var shapeCount)) {
            refusal = $"'{refName}' did not resolve in the store";

            return false;
        }

        if (shapeCount > MaxShapesPerStamp) {
            refusal = $"'{refName}' has {shapeCount} shapes — the budget per stamp is {MaxShapesPerStamp}";

            return false;
        }

        m_placements[m_selectionIndex] = m_placements[m_selectionIndex] with { RefName = refName, SourceHash = hash };
        refusal = null;
        MarkProgramChanged();

        return true;
    }

    /// <summary>Adds a terrain slab/plaza patch centered on the TARGET's current ground position (refused at the
    /// <see cref="MaxTerrainPatches"/> budget — the probe reserves exactly that many). Structural edit.</summary>
    /// <param name="kind"><c>slab</c> or <c>plaza</c>.</param>
    /// <param name="halfExtents">The patch half extents.</param>
    /// <param name="material">The world palette slot.</param>
    /// <returns>The new patch's id, or -1 when the budget is full.</returns>
    public int AddTerrain(string kind, Vector3 halfExtents, int material) {
        if (m_terrain.Count >= MaxTerrainPatches) {
            return -1;
        }

        var id = m_nextTerrainId++;

        m_terrain.Add(item: new WorldTerrainPatch(
            Center: (TargetPosition with { Y = m_bounds.FloorY }),
            HalfExtents: halfExtents,
            Id: id,
            Kind: (string.Equals(a: kind, b: "plaza", comparisonType: StringComparison.OrdinalIgnoreCase) ? "plaza" : "slab"),
            Material: Math.Max(val1: material, val2: 0)
        ));
        MarkProgramChanged();

        return id;
    }

    /// <summary>Removes a terrain patch by id.</summary>
    /// <param name="id">The patch id.</param>
    /// <returns>Whether a patch was removed.</returns>
    public bool RemoveTerrain(int id) {
        var index = m_terrain.FindIndex(match: patch => (patch.Id == id));

        if (index < 0) {
            return false;
        }

        m_terrain.RemoveAt(index: index);
        MarkProgramChanged();

        return true;
    }

    /// <summary>Adds a light emitter at the TARGET's current position (refused at the <see cref="MaxLights"/>
    /// budget). Structural edit.</summary>
    /// <param name="color">The emitter color.</param>
    /// <param name="intensity">The emitter strength.</param>
    /// <returns>The new light's id, or -1 when the budget is full.</returns>
    public int AddLight(Vector3 color, float intensity) {
        if (m_lights.Count >= MaxLights) {
            return -1;
        }

        var id = m_nextLightId++;

        m_lights.Add(item: new WorldLight(Color: color, Id: id, Intensity: Math.Max(val1: intensity, val2: 0f), Position: TargetPosition));
        MarkProgramChanged();

        return id;
    }

    /// <summary>Removes a light by id.</summary>
    /// <param name="id">The light id.</param>
    /// <returns>Whether a light was removed.</returns>
    public bool RemoveLight(int id) {
        var index = m_lights.FindIndex(match: light => (light.Id == id));

        if (index < 0) {
            return false;
        }

        m_lights.RemoveAt(index: index);
        MarkProgramChanged();

        return true;
    }

    /// <summary>Paints a walkability override rectangle centered on the TARGET's current ground position (refused
    /// at the <see cref="MaxWalkOverrides"/> budget). Structural edit (renders as a ghost outline while the
    /// overrides page is active — sim-side data otherwise).</summary>
    /// <param name="kind"><c>blocker</c> or <c>walkable</c>.</param>
    /// <param name="halfExtents">The rectangle's planar half extents.</param>
    /// <returns>The new override's id, or -1 when the budget is full.</returns>
    public int AddWalkOverride(string kind, Vector2 halfExtents) {
        if (m_walkOverrides.Count >= MaxWalkOverrides) {
            return -1;
        }

        var id = m_nextWalkOverrideId++;
        var center = TargetPosition;

        m_walkOverrides.Add(item: new WorldWalkOverride(
            Id: id,
            Kind: (string.Equals(a: kind, b: "walkable", comparisonType: StringComparison.OrdinalIgnoreCase) ? "walkable" : "blocker"),
            MaxX: (center.X + halfExtents.X),
            MaxZ: (center.Z + halfExtents.Y),
            MinX: (center.X - halfExtents.X),
            MinZ: (center.Z - halfExtents.Y)
        ));
        MarkProgramChanged();

        return id;
    }

    /// <summary>Removes a walk override by id.</summary>
    /// <param name="id">The override id.</param>
    /// <returns>Whether an override was removed.</returns>
    public bool RemoveWalkOverride(int id) {
        var index = m_walkOverrides.FindIndex(match: entry => (entry.Id == id));

        if (index < 0) {
            return false;
        }

        m_walkOverrides.RemoveAt(index: index);
        MarkProgramChanged();

        return true;
    }

    /// <summary>Grows the authored bounds by a signed delta on each axis (the console/pad <c>world.bounds</c> verb —
    /// negative shrinks, clamped so the lot never inverts). Structural edit (walls/terrain re-derive from it at
    /// integration).</summary>
    /// <param name="deltaX">The signed growth applied to both MinX (subtracted) and MaxX (added), halved per side.</param>
    /// <param name="deltaZ">The signed growth applied to both MinZ and MaxZ, halved per side.</param>
    /// <returns>The applied bounds.</returns>
    public WorldBounds GrowBounds(float deltaX, float deltaZ) {
        const float minHalfSpan = 1f;
        var halfDeltaX = (deltaX * 0.5f);
        var halfDeltaZ = (deltaZ * 0.5f);
        var minX = Math.Min(val1: (m_bounds.MinX - halfDeltaX), val2: (m_bounds.MaxX - minHalfSpan));
        var maxX = Math.Max(val1: (m_bounds.MaxX + halfDeltaX), val2: (m_bounds.MinX + minHalfSpan));
        var minZ = Math.Min(val1: (m_bounds.MinZ - halfDeltaZ), val2: (m_bounds.MaxZ - minHalfSpan));
        var maxZ = Math.Max(val1: (m_bounds.MaxZ + halfDeltaZ), val2: (m_bounds.MinZ + minHalfSpan));

        m_bounds = new WorldBounds(FloorY: m_bounds.FloorY, MaxX: maxX, MaxZ: maxZ, MinX: minX, MinZ: minZ);
        MarkProgramChanged();

        return m_bounds;
    }

    /// <summary>Renames the world (the save/load handle).</summary>
    /// <param name="name">The new name.</param>
    public void SetName(string name) {
        m_name = (string.IsNullOrWhiteSpace(value: name) ? "world" : name);
        Revision++;
    }

    /// <summary>An immutable full-model snapshot for <see cref="Puck.Demo.EditHistory{T}"/> — every field needed to
    /// restore the scene wholesale on undo/redo, including the transient selection/ghost/target state (so undo also
    /// restores "what you were editing", matching <c>CreatorScene</c>'s snapshot contract).</summary>
    /// <param name="Placements">The placements.</param>
    /// <param name="Terrain">The terrain patches.</param>
    /// <param name="Lights">The lights.</param>
    /// <param name="WalkOverrides">The walk overrides.</param>
    /// <param name="Bounds">The authored bounds.</param>
    /// <param name="Name">The save/load handle.</param>
    /// <param name="SelectionIndex">The selected placement's index (-1 = none).</param>
    /// <param name="GhostRefName">The ghost stamp's ref name.</param>
    /// <param name="GhostSourceHash">The ghost stamp's source hash.</param>
    /// <param name="GhostPosition">The ghost stamp's position.</param>
    /// <param name="GhostYawDegrees">The ghost stamp's yaw.</param>
    /// <param name="GhostScale">The ghost stamp's scale.</param>
    /// <param name="NextPlacementId">The next placement id counter.</param>
    /// <param name="NextTerrainId">The next terrain id counter.</param>
    /// <param name="NextLightId">The next light id counter.</param>
    /// <param name="NextWalkOverrideId">The next walk-override id counter.</param>
    /// <param name="WalkGridKind">The walk grid tessellation knob.</param>
    /// <param name="MovementLock">The movement direction lock knob.</param>
    public sealed record Snapshot(
        IReadOnlyList<WorldPlacement> Placements,
        IReadOnlyList<WorldTerrainPatch> Terrain,
        IReadOnlyList<WorldLight> Lights,
        IReadOnlyList<WorldWalkOverride> WalkOverrides,
        WorldBounds Bounds,
        string Name,
        int SelectionIndex,
        string? GhostRefName,
        string? GhostSourceHash,
        Vector3 GhostPosition,
        float GhostYawDegrees,
        float GhostScale,
        int NextPlacementId,
        int NextTerrainId,
        int NextLightId,
        int NextWalkOverrideId,
        string WalkGridKind,
        string MovementLock,
        IReadOnlyList<CameraEye> Cameras,
        IReadOnlyList<ScreenWire> Wiring,
        int NextCameraId
    );

    /// <summary>Captures the current model as an immutable snapshot for <see cref="Puck.Demo.EditHistory{T}"/>.</summary>
    /// <returns>The snapshot.</returns>
    public Snapshot CaptureSnapshot() {
        return new Snapshot(
            Bounds: m_bounds,
            Cameras: [.. m_cameras],
            GhostPosition: m_ghostPosition,
            GhostRefName: m_ghostRefName,
            GhostScale: m_ghostScale,
            GhostSourceHash: m_ghostSourceHash,
            GhostYawDegrees: m_ghostYawDegrees,
            Lights: [.. m_lights],
            MovementLock: m_movementLock,
            Name: m_name,
            NextCameraId: m_nextCameraId,
            NextLightId: m_nextLightId,
            NextPlacementId: m_nextPlacementId,
            NextTerrainId: m_nextTerrainId,
            NextWalkOverrideId: m_nextWalkOverrideId,
            Placements: [.. m_placements],
            SelectionIndex: m_selectionIndex,
            Terrain: [.. m_terrain],
            WalkGridKind: m_walkGridKind,
            WalkOverrides: [.. m_walkOverrides],
            Wiring: [.. m_wiring.Select(selector: static pair => new ScreenWire(ScreenIndex: pair.Key, Source: pair.Value))]
        );
    }

    /// <summary>Restores the model from a snapshot (the undo/redo apply path). Always a structural edit — the
    /// restored state may differ arbitrarily from the live program.</summary>
    /// <param name="snapshot">The snapshot to restore.</param>
    public void RestoreSnapshot(Snapshot snapshot) {
        ArgumentNullException.ThrowIfNull(snapshot);

        m_placements.Clear();
        m_placements.AddRange(collection: snapshot.Placements);
        m_terrain.Clear();
        m_terrain.AddRange(collection: snapshot.Terrain);
        m_lights.Clear();
        m_lights.AddRange(collection: snapshot.Lights);
        m_walkOverrides.Clear();
        m_walkOverrides.AddRange(collection: snapshot.WalkOverrides);
        m_cameras.Clear();
        m_cameras.AddRange(collection: snapshot.Cameras);
        m_wiring.Clear();

        foreach (var wire in snapshot.Wiring) {
            m_wiring[wire.ScreenIndex] = wire.Source;
        }

        m_nextCameraId = snapshot.NextCameraId;
        m_bounds = snapshot.Bounds;
        m_name = snapshot.Name;
        m_selectionIndex = snapshot.SelectionIndex;
        m_ghostRefName = snapshot.GhostRefName;
        m_ghostSourceHash = snapshot.GhostSourceHash;
        m_ghostPosition = snapshot.GhostPosition;
        m_ghostYawDegrees = snapshot.GhostYawDegrees;
        m_ghostScale = snapshot.GhostScale;
        m_nextPlacementId = snapshot.NextPlacementId;
        m_nextTerrainId = snapshot.NextTerrainId;
        m_nextLightId = snapshot.NextLightId;
        m_nextWalkOverrideId = snapshot.NextWalkOverrideId;
        m_walkGridKind = snapshot.WalkGridKind;
        m_movementLock = snapshot.MovementLock;
        m_dragging = false;
        MarkProgramChanged();
    }

    /// <summary>Lifts the scene into its <c>puck.world.v1</c> document. The walk grid is left null — baking it is
    /// the sim's job at ORCHESTRATOR integration (see <see cref="SaveCompleted"/>); a document saved here without
    /// one simply asks the loader to derive walls-only walkability until the next full save.</summary>
    /// <returns>The document, ready for <see cref="WorldDocumentStore.Save"/>.</returns>
    public WorldDocument ToDocument() {
        var placements = new List<PlacementDocument>(capacity: m_placements.Count);

        foreach (var placement in m_placements) {
            placements.Add(item: new PlacementDocument(
                Id: placement.Id,
                Mirror: placement.Mirror,
                Name: placement.RefName,
                Pattern: ((placement.Pattern is { } pattern)
                    ? new PlacementPatternDocument(
                        CellHeight: pattern.CellHeight,
                        CellWidth: pattern.CellWidth,
                        Group: pattern.Group,
                        LimitX: pattern.LimitX,
                        LimitZ: pattern.LimitZ,
                        MaterialStride: pattern.MaterialStride
                    )
                    : null),
                Position: placement.Position,
                Repeat: ((placement.Repeat is { } repeat)
                    ? new PlacementRepeatDocument(CountX: repeat.CountX, CountZ: repeat.CountZ, SpacingX: repeat.SpacingX, SpacingZ: repeat.SpacingZ)
                    : null),
                Role: placement.Role,
                Scale: placement.Scale,
                Source: placement.SourceHash,
                YawDegrees: placement.YawDegrees
            ));
        }

        var terrain = new List<TerrainPatchDocument>(capacity: m_terrain.Count);

        foreach (var patch in m_terrain) {
            terrain.Add(item: new TerrainPatchDocument(Center: patch.Center, HalfExtents: patch.HalfExtents, Kind: patch.Kind, Material: patch.Material));
        }

        var lights = new List<WorldLightDocument>(capacity: m_lights.Count);

        foreach (var light in m_lights) {
            lights.Add(item: new WorldLightDocument(Color: light.Color, Intensity: light.Intensity, Position: light.Position));
        }

        var walkOverrides = new List<WalkOverrideDocument>(capacity: m_walkOverrides.Count);

        foreach (var entry in m_walkOverrides) {
            walkOverrides.Add(item: new WalkOverrideDocument(Kind: entry.Kind, MaxX: entry.MaxX, MaxZ: entry.MaxZ, MinX: entry.MinX, MinZ: entry.MinZ));
        }

        var cameras = new List<CameraDocument>(capacity: m_cameras.Count);

        foreach (var eye in m_cameras) {
            // The model carries radians (the engine's unit); the document carries DEGREES (the authoring/verb unit) so a
            // saved world reads human. FOV null in the model stays null in the document (the engine default).
            cameras.Add(item: new CameraDocument(
                Anchor: ((eye.Anchor == CameraAnchorKind.Placement) ? "placement" : "world"),
                AnchorId: ((eye.Anchor == CameraAnchorKind.Placement) ? (int?)eye.AnchorId : null),
                Focus: eye.FocusDistance,
                Fov: ((eye.FieldOfViewRadians is { } fov) ? (float?)(fov * (180f / MathF.PI)) : null),
                Id: eye.Id,
                Pitch: (eye.Pitch * (180f / MathF.PI)),
                Position: eye.Position,
                Yaw: (eye.Yaw * (180f / MathF.PI))
            ));
        }

        var wiring = new List<ScreenWireDocument>(capacity: m_wiring.Count);

        // Emit the wiring table in ascending screen-index order so the saved bytes are stable regardless of the
        // dictionary's insertion order (a load→save round-trip is byte-identical).
        foreach (var screenIndex in m_wiring.Keys.Order()) {
            var source = m_wiring[screenIndex];

            wiring.Add(item: new ScreenWireDocument(
                Index: ((source.Kind is ScreenWireKind.Brick or ScreenWireKind.Feed) ? (int?)source.Index : null),
                Kind: (source.Kind switch {
                    ScreenWireKind.Brick => "brick",
                    ScreenWireKind.Feed => "feed",
                    ScreenWireKind.Named => "named",
                    _ => "none",
                }),
                Name: ((source.Kind == ScreenWireKind.Named) ? source.Name : null),
                Screen: screenIndex
            ));
        }

        return new WorldDocument(
            Bounds: new WorldBoundsDocument(FloorY: m_bounds.FloorY, MaxX: m_bounds.MaxX, MaxZ: m_bounds.MaxZ, MinX: m_bounds.MinX, MinZ: m_bounds.MinZ),
            Cameras: cameras,
            Lights: lights,
            MovementLock: (string.Equals(a: m_movementLock, b: "free", comparisonType: StringComparison.Ordinal) ? null : m_movementLock),
            Name: m_name,
            Placements: placements,
            Schema: WorldDocument.CurrentSchema,
            Terrain: terrain,
            // A HEX knob rides a zero-size stub (Width/Height 0, no cells) so the tessellation choice survives a
            // save→load round-trip made before the host's save hook bakes the real grid — the bake replaces this
            // stub wholesale, carrying the same Kind. Square (the default) stays null, exactly as before.
            WalkGrid: (string.Equals(a: m_walkGridKind, b: "hex", comparisonType: StringComparison.Ordinal)
                ? new WalkGridDocument(CellSizeRaw: 0, Cells: null, Height: 0, Kind: "hex", OriginXRaw: 0, OriginZRaw: 0, RowStrideRaw: null, Width: 0)
                : null),
            WalkOverrides: walkOverrides,
            Wiring: wiring
        );
    }

    /// <summary>Replaces the scene's content from a NORMALIZED document (see <see cref="WorldDocumentStore.Load"/>):
    /// placements/terrain/lights/overrides load verbatim (already normalized), ids resequence their counters, the
    /// selection clears. A placement whose source hash does not resolve in the store is DROPPED — bit-for-bit
    /// doctrine forbids a partially-resolved world; callers should check
    /// <see cref="WorldDocumentStore.TryResolvePlacementSources"/> first and surface the missing list before calling this.
    /// The emission budgets hold on load too (by construction): a placement that would exceed the SEGMENT budget or
    /// whose creation exceeds <see cref="MaxShapesPerStamp"/> is skipped, and the terrain/light/override lists
    /// truncate at their caps.</summary>
    /// <param name="document">The normalized document.</param>
    /// <param name="store">The content-addressed store to resolve placement sources against.</param>
    /// <returns>How many placements loaded (a placement whose source fails to resolve, or that exceeds a budget,
    /// is skipped).</returns>
    public int LoadDocument(WorldDocument document, ContentAddressedStore store) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(store);

        m_placements.Clear();
        m_terrain.Clear();
        m_lights.Clear();
        m_walkOverrides.Clear();
        m_cameras.Clear();
        m_wiring.Clear();
        m_selectionIndex = -1;
        m_creationCache.Clear();
        m_name = (document.Name ?? "world");
        m_bounds = ((document.Bounds is { } bounds)
            ? new WorldBounds(FloorY: bounds.FloorY, MaxX: bounds.MaxX, MaxZ: bounds.MaxZ, MinX: bounds.MinX, MinZ: bounds.MinZ)
            : WorldBounds.Default);
        m_walkGridKind = (string.Equals(a: document.WalkGrid?.Kind, b: "hex", comparisonType: StringComparison.OrdinalIgnoreCase) ? "hex" : "square");
        m_movementLock = (document.MovementLock?.ToLowerInvariant() switch {
            "four" => "four",
            "eight" => "eight",
            "hex" => "hex",
            _ => "free",
        });

        var maxPlacementId = -1;
        var segmentTotal = 0;

        foreach (var placement in (document.Placements ?? [])) {
            if ((placement.Source is not { Length: > 0 } source) || !store.Contains(hash: source)) {
                continue;
            }

            var repeat = ((placement.Repeat is { } repeatDocument)
                ? new WorldPlacementRepeat(CountX: repeatDocument.CountX, CountZ: repeatDocument.CountZ, SpacingX: repeatDocument.SpacingX, SpacingZ: repeatDocument.SpacingZ)
                : (WorldPlacementRepeat?)null);
            var segments = SegmentCountOf(repeat: repeat);

            if ((segmentTotal + segments) > MaxPlacements) {
                continue;
            }

            // The per-stamp shape budget holds on load exactly as at arm time — a document referencing an
            // oversized creation cannot smuggle it past the probe's per-instance reservation.
            if (!TryMeasureCreation(hash: source, store: store, shapeCount: out var shapeCount) || (shapeCount > MaxShapesPerStamp)) {
                continue;
            }

            m_placements.Add(item: new WorldPlacement(
                Id: placement.Id,
                Mirror: placement.Mirror,
                Pattern: ((placement.Pattern is { } patternDocument)
                    ? new WorldPlacementPattern(
                        CellHeight: patternDocument.CellHeight,
                        CellWidth: patternDocument.CellWidth,
                        Group: patternDocument.Group,
                        LimitX: patternDocument.LimitX,
                        LimitZ: patternDocument.LimitZ,
                        MaterialStride: patternDocument.MaterialStride
                    )
                    : null),
                Position: placement.Position,
                Repeat: repeat,
                RefName: (placement.Name ?? "creation"),
                Role: placement.Role,
                Scale: (placement.Scale ?? 1f),
                SourceHash: source,
                YawDegrees: (placement.YawDegrees ?? 0f)
            ));
            maxPlacementId = Math.Max(val1: maxPlacementId, val2: placement.Id);
            segmentTotal += segments;
        }

        var maxTerrainId = -1;

        foreach (var patch in (document.Terrain ?? [])) {
            if (m_terrain.Count >= MaxTerrainPatches) {
                break;
            }

            var id = maxTerrainId + 1;

            m_terrain.Add(item: new WorldTerrainPatch(Center: patch.Center, HalfExtents: patch.HalfExtents, Id: id, Kind: (patch.Kind ?? "slab"), Material: (patch.Material ?? 0)));
            maxTerrainId = id;
        }

        var maxLightId = -1;

        foreach (var light in (document.Lights ?? [])) {
            if (m_lights.Count >= MaxLights) {
                break;
            }

            var id = maxLightId + 1;

            m_lights.Add(item: new WorldLight(Color: light.Color, Id: id, Intensity: (light.Intensity ?? 1f), Position: light.Position));
            maxLightId = id;
        }

        var maxWalkOverrideId = -1;

        foreach (var entry in (document.WalkOverrides ?? [])) {
            if (m_walkOverrides.Count >= MaxWalkOverrides) {
                break;
            }

            var id = maxWalkOverrideId + 1;

            m_walkOverrides.Add(item: new WorldWalkOverride(Id: id, Kind: (entry.Kind ?? "blocker"), MaxX: entry.MaxX, MaxZ: entry.MaxZ, MinX: entry.MinX, MinZ: entry.MinZ));
            maxWalkOverrideId = id;
        }

        m_nextPlacementId = (maxPlacementId + 1);
        m_nextTerrainId = (maxTerrainId + 1);
        m_nextLightId = (maxLightId + 1);
        m_nextWalkOverrideId = (maxWalkOverrideId + 1);
        m_nextCameraId = (LoadCameras(cameras: document.Cameras) + 1);
        LoadWiring(wiring: document.Wiring);
        MarkProgramChanged();

        return m_placements.Count;
    }

    // Loads the placed camera eyes (document DEGREES → model RADIANS), returning the max id seen (-1 for none) so the
    // caller resequences the id counter. Extracted from LoadDocument to keep its cyclomatic complexity in bound.
    private int LoadCameras(IReadOnlyList<CameraDocument>? cameras) {
        var maxCameraId = -1;

        foreach (var camera in (cameras ?? [])) {
            if (m_cameras.Count >= MaxCameras) {
                break;
            }

            var anchor = (string.Equals(a: camera.Anchor, b: "placement", comparisonType: StringComparison.OrdinalIgnoreCase) ? CameraAnchorKind.Placement : CameraAnchorKind.World);

            m_cameras.Add(item: new CameraEye(
                Anchor: anchor,
                AnchorId: ((anchor == CameraAnchorKind.Placement) ? (camera.AnchorId ?? 0) : 0),
                FieldOfViewRadians: ((camera.Fov is { } fov) ? (float?)(fov * (MathF.PI / 180f)) : null),
                FocusDistance: camera.Focus,
                Id: camera.Id,
                Pitch: ((camera.Pitch ?? 0f) * (MathF.PI / 180f)),
                Position: camera.Position,
                Yaw: ((camera.Yaw ?? 0f) * (MathF.PI / 180f))
            ));
            maxCameraId = Math.Max(val1: maxCameraId, val2: camera.Id);
        }

        return maxCameraId;
    }

    // Loads the screen wiring table (a "none" entry is the absence of a wire and does not seat). Extracted from
    // LoadDocument to keep its cyclomatic complexity in bound.
    private void LoadWiring(IReadOnlyList<ScreenWireDocument>? wiring) {
        foreach (var wire in (wiring ?? [])) {
            if ((wire.Screen < 0) || (wire.Screen >= Puck.SdfVm.SdfProgramBuilder.MaxScreenSurfaces)) {
                continue;
            }

            var source = (wire.Kind?.ToLowerInvariant() switch {
                "brick" => ScreenWireSource.Brick(consoleIndex: (wire.Index ?? 0)),
                "feed" => ScreenWireSource.Feed(feedIndex: (wire.Index ?? 0)),
                "named" => ((wire.Name is { Length: > 0 } name) ? ScreenWireSource.Named(name: name) : ScreenWireSource.None),
                _ => ScreenWireSource.None,
            });

            if (source.Kind != ScreenWireKind.None) {
                m_wiring[wire.Screen] = source;
            }
        }
    }

    /// <summary>The DAYLIGHT dial (1 = full day, down to dusk at ~0.2) — pure presentation, never saved, never
    /// snapshotted: the room's ambient/sun scale multiplies by it, so authored emissive lights (street lamps,
    /// windows) READ once the world dims. The first night in your town is one <c>world.dusk</c> away.</summary>
    public float Daylight { get; private set; } = 1f;

    /// <summary>Sets the daylight dial, clamped into [0.15, 1].</summary>
    /// <param name="value">The new dial value.</param>
    public void SetDaylight(float value) {
        Daylight = Math.Clamp(value: value, min: 0.15f, max: 1f);
    }

    /// <summary>The deliberate-save transformation seam: the host installs the walk-grid bake here (footprints from
    /// the resolved placements + the room's own stands, the grid-kind knob, the player envelope), so the BAKED grid
    /// ships inside the saved bytes — save = make-it-real. Null = the document saves with its stub grid (headless
    /// tools that never walk the world).</summary>
    public Func<WorldDocument, WorldDocument>? PrepareForSave { get; set; }

    /// <summary>The most recently saved or loaded COMMITTED document (the bytes on disk, bake included) — the host's
    /// hot-reload consumes it. Null before the first deliberate save/load.</summary>
    public WorldDocument? CommittedDocument { get; private set; }

    /// <summary>Saves the live model as a <c>puck.world.v1</c> document (through <see cref="PrepareForSave"/> when
    /// installed), raising <see cref="SaveCompleted"/> on success — the integration point a host wires for the sim's
    /// walk-grid hot-reload.</summary>
    /// <param name="store">The content-addressed store to also land the canonical bytes in.</param>
    /// <returns>The written path and content hash (see <see cref="WorldDocumentStore.Save"/>).</returns>
    public (string Path, string? Hash) Save(ContentAddressedStore store) {
        var document = ToDocument();

        if (PrepareForSave is { } prepare) {
            document = prepare(arg: document);
        }

        var result = WorldDocumentStore.Save(document: document, name: m_name, store: store);

        CommittedDocument = document;
        SaveCompleted?.Invoke(m_name, result.Hash);

        return result;
    }

    /// <summary>Records a freshly LOADED document as the committed copy — the load path's half of
    /// <see cref="CommittedDocument"/> (the host's hot-reload consumes both halves identically).</summary>
    /// <param name="document">The document just loaded.</param>
    public void MarkCommitted(WorldDocument document) {
        CommittedDocument = document;
    }

    // Every program-content change also counts as a plain revision (mirrors CreatorScene.MarkProgramChanged).
    // Every placement add/remove/repeat-change (the only edits that can move TotalSegmentCount) routes through here,
    // so invalidating the memo on this one edge is exact — never stale, never rescanned on an unrelated edit.
    private void MarkProgramChanged() {
        ProgramRevision++;
        Revision++;
        m_totalSegmentCount = null;
    }

    // A change the host re-polls but the SDF program does NOT rebuild for (a camera eye, a wiring entry): bump the
    // cheap poll seam only, leaving ProgramRevision untouched so no world-program rebuild is triggered. The camera eyes
    // and wiring table are host-side data (a feed pose, a screen route), never emitted into the SDF instruction stream.
    private void MarkRevision() {
        Revision++;
    }

    private static float WrapDegrees(float degrees) {
        var wrapped = (degrees % 360f);

        return ((wrapped < 0f) ? (wrapped + 360f) : wrapped);
    }
}

// A tiny byte-level deserialize path for CreationDocument, matching CreationStore's JSON options exactly (see the
// class remarks) — CreationStore.Load only takes paths, and the world store's resolved placements arrive as bytes
// from a ContentAddressedStore, not files.
internal static class CreationDocumentBytes {
    private static readonly JsonSerializerOptions JsonOptions = new() {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>Deserializes and normalizes creation bytes the same way <see cref="CreationStore.Load"/> would for
    /// the same JSON text, without requiring a file on disk.</summary>
    /// <param name="bytes">The stored object's bytes.</param>
    /// <returns>The normalized document, or null on a malformed payload.</returns>
    public static CreationDocument? Deserialize(byte[] bytes) {
        ArgumentNullException.ThrowIfNull(bytes);

        try {
            var document = JsonSerializer.Deserialize<CreationDocument>(utf8Json: bytes, options: JsonOptions);

            return ((document is null) ? null : Normalize(document: document));
        } catch (JsonException) {
            return null;
        }
    }

    // Mirrors CreationStore's private Normalize exactly (kept in lockstep intentionally — see that type's remarks);
    // duplicated here because CreationStore's normalize path is private and file-path-only.
    private static CreationDocument Normalize(CreationDocument document) {
        var shapes = new List<ShapeDocument>(capacity: (document.Shapes?.Count ?? 0));

        foreach (var shape in (document.Shapes ?? [])) {
            shapes.Add(item: shape with {
                Blend = (shape.Blend ?? SdfVm.SdfBlendOp.Union),
                Group = Math.Max(val1: (shape.Group ?? 0), val2: 0),
                Material = Math.Clamp(value: (shape.Material ?? 0), max: (CreatorScene.PaletteSize - 1), min: 0),
                Rotation = ((shape.Rotation == default) ? Quaternion.Identity : Quaternion.Normalize(value: shape.Rotation)),
                Scale = ((shape.Scale == default) ? Vector3.One : shape.Scale),
                Smooth = Math.Clamp(value: (shape.Smooth ?? 0f), max: CreatorScene.MaxSmooth, min: 0f),
            });
        }

        return (document with {
            BakeStyle = (string.Equals(a: document.BakeStyle, b: "bold", comparisonType: StringComparison.OrdinalIgnoreCase) ? "bold" : "classic"),
            Intent = (document.Intent ?? CreatorIntent.Object),
            Name = (document.Name ?? "creation"),
            Schema = CreationDocument.CurrentSchema,
            Shapes = shapes,
        });
    }
}
