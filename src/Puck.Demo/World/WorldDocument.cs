using System.Numerics;

namespace Puck.Demo.World;

/// <summary>The authored world bounds — the town lot the walls emit at and the walk grid spans.</summary>
/// <param name="MinX">The lot's minimum X (world units).</param>
/// <param name="MinZ">The lot's minimum Z.</param>
/// <param name="MaxX">The lot's maximum X.</param>
/// <param name="MaxZ">The lot's maximum Z.</param>
/// <param name="FloorY">The floor plane's height.</param>
public sealed record WorldBoundsDocument(float MinX, float MinZ, float MaxX, float MaxZ, float FloorY);

/// <summary>One terrain patch — a road/plaza slab authored directly into the world (no creation reference).</summary>
/// <param name="Kind">The patch kind name (null = <c>slab</c>).</param>
/// <param name="Center">The patch center.</param>
/// <param name="HalfExtents">The patch half extents.</param>
/// <param name="Material">The world palette slot (null = 0).</param>
public sealed record TerrainPatchDocument(string? Kind, Vector3 Center, Vector3 HalfExtents, int? Material);

/// <summary>A placement's repeat block — a road IS a repeat; rows longer than the auto-split bound land as
/// several placements so instance bounds stay tight.</summary>
/// <param name="SpacingX">The per-copy X spacing.</param>
/// <param name="SpacingZ">The per-copy Z spacing.</param>
/// <param name="CountX">The copy count along X (1 = no repeat on the axis).</param>
/// <param name="CountZ">The copy count along Z.</param>
public sealed record PlacementRepeatDocument(float SpacingX, float SpacingZ, int CountX, int CountZ);

/// <summary>A placement's wallpaper pattern block — the <c>WallpaperFold</c> domain op as data: the whole
/// assembly folds through one of the seventeen wallpaper groups, with an optional per-cell material stride for
/// free stamped variation. Console-assist authored (<c>world.pattern</c>).</summary>
/// <param name="Group">The wallpaper group name (an <c>SdfWallpaperGroup</c> member; null = <c>P1</c>).</param>
/// <param name="CellWidth">The fold cell width (world units, X).</param>
/// <param name="CellHeight">The fold cell height (world units, Z).</param>
/// <param name="LimitX">The fold's cell-count limit along X (null = unlimited).</param>
/// <param name="LimitZ">The fold's cell-count limit along Z (null = unlimited).</param>
/// <param name="MaterialStride">The per-cell material stride (null = 0, no variation).</param>
public sealed record PlacementPatternDocument(
    string? Group,
    float CellWidth,
    float CellHeight,
    float? LimitX,
    float? LimitZ,
    int? MaterialStride
);

/// <summary>
/// One stamped assembly: a saved creation placed into the world. The <paramref name="Source"/> content hash is
/// the placement's identity (load refuses loudly when the store lacks it — bit-for-bit doctrine forbids partial
/// worlds); the name is display + rebind convenience only.
/// </summary>
/// <param name="Id">The placement's stable id.</param>
/// <param name="Name">The referenced creation's ref name (display/rebind; the hash is the identity).</param>
/// <param name="Source">The referenced creation's content hash (<c>sha256/&lt;hex64&gt;</c>).</param>
/// <param name="Position">The stamp position (world space, floor-snapped by the authoring UX).</param>
/// <param name="YawDegrees">The stamp yaw (null = 0).</param>
/// <param name="Scale">The uniform stamp scale (null = 1).</param>
/// <param name="Repeat">The repeat block (null = a single copy).</param>
/// <param name="Role">The anchor role (null = decoration; <c>cabinet:&lt;n&gt;</c> re-homes a console stand).
/// PERSISTENCE SEAM (unwired — USER DECISION: no persistence for now, cloud saves near-future): a re-forged
/// win/reveal condition (the live <c>condition.*</c> verbs) WOULD serialize onto THIS <c>cabinet:&lt;n&gt;</c>
/// placement — its exit + victory conditions carried alongside the role — the same serializable seam a cloud save
/// syncs. It is deliberately NOT wired this stage: <c>world.save</c> does not yet carry conditions, so a live re-forge
/// is session-only. The run-document schema is UNCHANGED — conditions already exist on <c>GamingBrickSource</c>; live
/// editing changes no schema.</param>
/// <param name="Mirror">The symmetry fold axis (<c>x</c> or <c>z</c> in the placement's local frame; null = none).
/// A mirrored/patterned chain forgoes the whole-chain instance skip (the settled cull contract) — the per-shape
/// skip inside the evaluated segment survives.</param>
/// <param name="Pattern">The wallpaper pattern block (null = none).</param>
public sealed record PlacementDocument(
    int Id,
    string? Name,
    string? Source,
    Vector3 Position,
    float? YawDegrees,
    float? Scale,
    PlacementRepeatDocument? Repeat,
    string? Role,
    string? Mirror = null,
    PlacementPatternDocument? Pattern = null
);

/// <summary>One authored light emitter (street lamps, windows — presentation-side).</summary>
/// <param name="Position">The emitter position.</param>
/// <param name="Color">The emitter color.</param>
/// <param name="Intensity">The emitter strength (null = 1).</param>
public sealed record WorldLightDocument(Vector3 Position, Vector3 Color, float? Intensity);

/// <summary>
/// One placed camera EYE — a posed viewpoint dropped into the world (mirrors <see cref="CameraEye"/> with
/// document-doctrine nullability). A standalone eye poses directly in world space (<paramref name="Anchor"/> null or
/// <c>world</c>); an anchored eye rides a placement's transform (<paramref name="Anchor"/> <c>placement</c> +
/// <paramref name="AnchorId"/>), so a camera on a placed prop moves when the prop is dragged. Its feed shows on any
/// screen that wires it — pure data, edited by <c>world.camera</c>/<c>world.wire</c>. (Shape-anchored eyes live in
/// <c>puck.creation.v1</c> — a creation's own lens — not here; the world document only holds world-space and
/// placement-anchored eyes.)
/// </summary>
/// <param name="Id">The eye's stable id (unique within the world; survives deletes — console selection keys on it).</param>
/// <param name="Position">The eye position (world space when unanchored, else the offset from the anchored stamp).</param>
/// <param name="Yaw">The eye's heading, degrees (null = 0).</param>
/// <param name="Pitch">The eye's tilt, degrees (null = 0; positive looks up).</param>
/// <param name="Fov">The vertical field of view, degrees (null = the engine default).</param>
/// <param name="Focus">The look-at target distance ahead (null = 1).</param>
/// <param name="Anchor">The anchor kind (null or <c>world</c> = standalone; <c>placement</c> = ride a stamp).</param>
/// <param name="AnchorId">The anchored placement id (ignored for a standalone eye).</param>
public sealed record CameraDocument(
    int Id,
    Vector3 Position,
    float? Yaw,
    float? Pitch,
    float? Fov,
    float? Focus,
    string? Anchor,
    int? AnchorId
);

/// <summary>One wiring-table entry: the source a screen surface index displays (mirrors
/// <see cref="ScreenWire"/>/<see cref="ScreenWireSource"/> as normalized data). Wiring is pure data, edited by
/// <c>world.wire</c> — never a heuristic. A source is a brick viewport by index, a camera feed by index, a named host
/// feed by name, or nothing.</summary>
/// <param name="Screen">The screen-surface slot this entry wires (0..<c>SdfProgramBuilder.MaxScreenSurfaces</c> - 1).</param>
/// <param name="Kind">The source family (<c>brick</c>, <c>feed</c>, <c>named</c>, or <c>none</c>; null = none).</param>
/// <param name="Index">The brick console index or camera feed index (null = 0; ignored for <c>named</c>/<c>none</c>).</param>
/// <param name="Name">The named host feed's name (only for <c>named</c>; else null).</param>
public sealed record ScreenWireDocument(
    int Screen,
    string? Kind,
    int? Index,
    string? Name
);

/// <summary>One author-placed walkability override — applied AFTER derivation, in document order (blockers add,
/// walkables carve).</summary>
/// <param name="Kind">The override kind (<c>blocker</c> or <c>walkable</c>; null = blocker).</param>
/// <param name="MinX">The override rectangle's minimum X.</param>
/// <param name="MinZ">The override rectangle's minimum Z.</param>
/// <param name="MaxX">The override rectangle's maximum X.</param>
/// <param name="MaxZ">The override rectangle's maximum Z.</param>
public sealed record WalkOverrideDocument(string? Kind, float MinX, float MinZ, float MaxX, float MaxZ);

/// <summary>
/// The baked fixed-point walk grid — derived at save time (save = make-it-real), shipped INSIDE the saved world
/// bytes so a reloaded world walks bit-for-bit. Origin/cell size are raw Q48.16 so the sim never touches floats.
/// Two tessellations: <c>square</c> (the default) and <c>hex</c> — pointy-top hexagonal cells realized as the
/// Voronoi regions of staggered row centers (odd rows offset by half a cell width), so point-location is an exact
/// nearest-center compare on raw fixed-point deltas. The irrational √3 in a regular hexagon's row spacing exists
/// only float-side at BAKE time, when <paramref name="RowStrideRaw"/> is chosen and rounded; every query
/// thereafter is integer math against the stored raws.
/// </summary>
/// <param name="OriginXRaw">The grid origin X (raw Q48.16).</param>
/// <param name="OriginZRaw">The grid origin Z (raw Q48.16).</param>
/// <param name="CellSizeRaw">The cell edge length along X — square edge, or hex horizontal center-to-center
/// spacing (raw Q48.16; the design bakes 0.25 world units = 16384).</param>
/// <param name="Width">The cell count along X (per row).</param>
/// <param name="Height">The cell count along Z (the row count).</param>
/// <param name="Cells">The blocked-cell bitmap: 1 bit per cell, row-major, packed little-endian ulongs, base64.</param>
/// <param name="Kind">The tessellation (<c>square</c> or <c>hex</c>; null = square).</param>
/// <param name="RowStrideRaw">The vertical center-to-center row spacing (raw Q48.16; null = <paramref name="CellSizeRaw"/>,
/// the square case. A hex bake stores ≈ CellSize × √3⁄2, rounded once).</param>
public sealed record WalkGridDocument(
    long OriginXRaw,
    long OriginZRaw,
    long CellSizeRaw,
    int Width,
    int Height,
    string? Cells,
    string? Kind = null,
    long? RowStrideRaw = null
);

/// <summary>
/// The <c>puck.world.v1</c> document — the sculpted world as data: bounds, terrain, stamped placements
/// (creations referenced by content hash), lights, walk overrides, and the baked walk grid. Sibling of
/// <c>puck.creation.v1</c>/<c>puck.audio.v1</c>; the full document doctrine applies (every optional member
/// nullable, normalized at load, loud throw on an unrecognized schema tag, <c>IncludeFields = true</c> —
/// load-bearing for the <see cref="Vector3"/> members). The store/normalization half lives with the CAS
/// workstream (<c>WorldDocumentStore</c>); these records are the frozen shapes the scene/renderer and walk-grid
/// workstreams code against.
/// </summary>
/// <param name="Schema">The document version tag (<c>puck.world.v1</c>).</param>
/// <param name="Name">The world's save/load handle.</param>
/// <param name="Bounds">The authored lot (null = the code-built default room's 16×16).</param>
/// <param name="Terrain">The terrain patches (null = none).</param>
/// <param name="Placements">The stamped assemblies (null = none).</param>
/// <param name="Lights">The authored light emitters (null = none).</param>
/// <param name="WalkOverrides">The walkability overrides (null = none).</param>
/// <param name="WalkGrid">The baked walk grid (null = derive on next save; the sim falls back to walls-only).</param>
/// <param name="MovementLock">The world's movement direction lock (<c>free</c>, <c>four</c>, <c>eight</c>, or
/// <c>hex</c>; null = free — today's analog movement, the untouched code path). Applied to the sim on world load
/// at a tick boundary. Like the collision surfaces, the lock is sim CONFIG, not sim state — it is never folded
/// into <c>StateHash</c>. Hex uses the pointy-top convention shared with the hex walk grid: pure E/W plus four
/// 60° diagonals, no vertical neighbor.</param>
/// <param name="Cameras">The placed camera eyes (null = none). Each produces a diegetic feed a screen can wire.</param>
/// <param name="Wiring">The screen-surface wiring table (null = none — every screen falls back to its default
/// behavior). At most one entry per screen index; normalization drops duplicates keeping the last.</param>
public sealed record WorldDocument(
    string? Schema,
    string? Name,
    WorldBoundsDocument? Bounds,
    IReadOnlyList<TerrainPatchDocument>? Terrain,
    IReadOnlyList<PlacementDocument>? Placements,
    IReadOnlyList<WorldLightDocument>? Lights,
    IReadOnlyList<WalkOverrideDocument>? WalkOverrides,
    WalkGridDocument? WalkGrid,
    string? MovementLock = null,
    IReadOnlyList<CameraDocument>? Cameras = null,
    IReadOnlyList<ScreenWireDocument>? Wiring = null
) {
    /// <summary>The version tag every saved document carries.</summary>
    public const string CurrentSchema = "puck.world.v1";
}
