using System.Numerics;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.SignedDistance.Illumination;

namespace Puck.SdfVm;

/// <summary>The exact uploaded geometry revisions that invalidate a residency's light maps.</summary>
/// <param name="Program">Program and shader revision.</param>
/// <param name="Poses">Revision of uploaded transforms that change an indirect caster.</param>
/// <param name="Mesh">Uploaded mesh revision.</param>
/// <param name="Decals">Glyph geometry revision.</param>
/// <param name="Bodies">The resolved dynamic indirect participation; changing it changes the caster field.</param>
public readonly record struct SdfLightGeometry(ulong Program, ulong Poses, long Mesh, ulong Decals,
    SdfIndirectParticipation Bodies = SdfIndirectParticipation.Default);
/// <summary>A finite receiver box from the allocated bricks of one cache level.</summary>
/// <param name="Min">Least corner.</param>
/// <param name="Max">Greatest corner.</param>
public readonly record struct SdfLightRegion(Double3 Min, Double3 Max);
/// <summary>One residency's bounded light-view schedule. It retains exact owner names and revision tuples, plans one
/// rectangle per frame, and publishes validity only after every rectangle submits. An absent finite caster volume
/// or unnamed owner never authorizes a map lookup.</summary>
public sealed class SdfIndirectLightViews {
    private readonly Map[] m_maps = [.. Enumerable.Range(count: SdfIndirectLightLayout.MaxMaps, start: 0).Select(selector: static _ => new Map())];

    private int m_cursor;

    private long m_frame = -1;

    private object? m_geometryOwner;
    private SdfLightGeometry m_geometry;
    private ulong m_geometryGeneration;

    /// <summary>Gets the region scheduled this frame, or -1 when all usable regions stand.</summary>
    public int Pending { get; private set; } = -1;

    /// <summary>Gets the first row of the scheduled interval.</summary>
    public int FirstRow { get; private set; }
    /// <summary>Gets the scheduled interval's number of rows.</summary>
    public int RowCount { get; private set; }
    /// <summary>Gets the first column of the scheduled rectangle.</summary>
    public int FirstColumn { get; private set; }
    /// <summary>Gets the scheduled rectangle's number of columns.</summary>
    public int ColumnCount { get; private set; }
    /// <summary>Gets the scheduled primary and intersecting beam field-query bound.</summary>
    public long EstimatedQueries => ((Pending < 0) ? 0 : Queries(FirstColumn, FirstRow, ColumnCount, RowCount));
    /// <summary>Gets the packed map and row interval, or zero when nothing is scheduled.</summary>
    public uint Slice => ((Pending >= 0) ? SdfIndirectLightLayout.PackSlice(Pending, FirstRow, RowCount, FirstColumn, ColumnCount) : 0u);

    /// <summary>Bounds the primary field queries by the existing tier ceiling in whole workgroup rows.</summary>
    /// <param name="layout">The cache whose light view is being produced.</param>
    /// <returns>The maximum rows in one submission.</returns>
    public static int RowsPerSubmission(SdfIndirectLayout layout) {
        ArgumentNullException.ThrowIfNull(layout);
        const int GroupEvaluations = ((SdfIndirectLightLayout.Resolution * SdfIndirectLightLayout.MarchSteps) * SdfIndirectLightLayout.SliceRowEdge);

        return Math.Min(val1: SdfIndirectLightLayout.Resolution, val2: ((layout.TraceEvaluationCeiling / GroupEvaluations) * SdfIndirectLightLayout.SliceRowEdge));
    }

    /// <summary>Gets the package frame already planned, or -1 after its storage is invalidated.</summary>
    public long Frame => m_frame;
    /// <summary>Gets the allocation's region capacity, including inactive incoming slots.</summary>
    public int MapCount { get; private set; }
    /// <summary>Gets the revision of the desired map set and successful publications.</summary>
    public ulong Revision { get; private set; }
    /// <summary>Gets the number of successful region publications.</summary>
    public ulong Publications { get; private set; }
    /// <summary>Gets the projection selected for the sole depth camera.</summary>
    public IrradianceLightProjection? Projection => ((Pending >= 0) ? m_maps[Pending].Projection : null);

    /// <summary>Reports whether these maps describe the exact table allocation and uploaded geometry a reader pinned.</summary>
    /// <param name="owner">The reader's captured table allocation.</param>
    /// <param name="geometry">The reader's captured geometry revisions.</param>
    /// <returns>True when a submitted region may be used by that geometry snapshot.</returns>
    public bool MatchesGeometry(object owner, SdfLightGeometry geometry) =>
        (ReferenceEquals(objA: m_geometryOwner, objB: owner) && (m_geometry == geometry));
    /// <summary>Invalidates publications when the graph releases the depth-bank allocation they describe.</summary>
    public void InvalidateStorage() {
        foreach (var map in m_maps) { map.Valid = false; map.NextRow = 0; map.NextColumn = 0; }
        Pending = -1;
        FirstRow = 0;
        RowCount = 0;
        FirstColumn = 0;
        ColumnCount = 0;
        m_frame = -1;
        Revision++;
    }
    /// <summary>Plans against the current uploaded geometry, selected light identities and allocated region bounds.</summary>
    /// <param name="frame">The package frame, used to admit only one region.</param>
    /// <param name="geometryOwner">The table allocation; a replacement invalidates equal numeric revisions.</param>
    /// <param name="geometry">Exact revisions of that allocation.</param>
    /// <param name="lights">Current held and incoming owners.</param>
    /// <param name="regions">Finest and coarsest allocated boxes, absent when no bricks are allocated.</param>
    /// <param name="casters">The finite box enclosing every caster, or null for unbounded geometry.</param>
    /// <param name="forceGeometry">Whether mutable geometry has no stable revision this frame.</param>
    /// <param name="layout">The cache's field evaluation ceiling.</param>
    /// <param name="instructionCount">The complete field program's instruction count.</param>
    public void Plan(long frame, object geometryOwner, SdfLightGeometry geometry, SdfLights lights,
        IReadOnlyList<SdfLightRegion?> regions, SdfLightRegion? casters, bool forceGeometry, SdfIndirectLayout layout, int instructionCount = 1) {
        if (m_frame == frame) { return; }
        m_frame = frame;
        Pending = -1;
        FirstRow = 0;
        RowCount = 0;
        FirstColumn = 0;
        ColumnCount = 0;
        if (!ReferenceEquals(objA: m_geometryOwner, objB: geometryOwner) || (m_geometry != geometry) || forceGeometry) {
            m_geometryOwner = geometryOwner;
            m_geometry = geometry;
            m_geometryGeneration++;
        }
        var slots = lights.ShadowSlots;

        MapCount = ((slots.SlotCount + slots.FadeCapacity) * SdfIndirectLightLayout.RegionsPerLight);
        for (var index = 0; (index < m_maps.Length); index++) {
            var map = m_maps[index];
            var channel = (index / SdfIndirectLightLayout.RegionsPerLight);
            var region = (index % SdfIndirectLightLayout.RegionsPerLight);
            var incoming = (channel - slots.SlotCount);
            var lightIndex = ((channel < slots.SlotCount) ? slots[channel]
                : ((incoming < slots.FadeCount) ? slots.Handoffs[incoming].Incoming : -1));
            var owner = ((channel < slots.SlotCount) ? slots.Owner(slot: channel)
                : ((incoming < slots.FadeCount) ? slots.IncomingOwner(channel: incoming) : null));
            var bounds = ((region < regions.Count) ? regions[region] : null);

            if ((index >= MapCount) || (lightIndex < 0) || (lightIndex >= lights.Count) || (owner is null) ||
                (bounds is not { } receiver) || (casters is not { } caster) ||
                (lights[lightIndex] is not { Kind: SdfLightKind.Directional, Param: > 0f } light)) {
                if (map.Projection is not null) { map.Projection = null; map.Valid = false; map.NextRow = 0; map.NextColumn = 0; Revision++; }
                continue;
            }
            var changed = ((map.Projection is null) || !StringComparer.Ordinal.Equals(x: owner, y: map.Owner) ||
                (map.GeometryGeneration != m_geometryGeneration) || (map.Receiver != receiver) || (map.Casters != caster) ||
                SdfLightMotion.Changed(anchorDirection: map.Direction, anchorPenumbra: map.Penumbra, direction: light.Direction, penumbra: light.Param));

            map.LightIndex = lightIndex;
            if (!changed) { continue; }
            map.Owner = owner;
            map.Direction = SdfLights.UnitDirection(direction: light.Direction);
            map.Penumbra = light.Param;
            map.Receiver = receiver;
            map.Casters = caster;
            map.GeometryGeneration = m_geometryGeneration;
            map.LightGeneration++;
            map.Valid = false;
            map.NextRow = 0;
            map.NextColumn = 0;
            map.Projection = IrradianceLightProjection.Create(receiverMin: receiver.Min, receiverMax: receiver.Max,
                casterMin: caster.Min, casterMax: caster.Max, towardLight: Point(value: map.Direction), penumbraSlope: light.Param,
                resolution: SdfIndirectLightLayout.Resolution);
            Revision++;
        }
        var budget = RowsPerSubmission(layout: layout);

        if (budget == 0) { return; }
        for (var offset = 0; (offset < MapCount); offset++) {
            var index = ((m_cursor + offset) % MapCount);

            if ((m_maps[index].Projection is not null) && !m_maps[index].Valid) {
                Pending = index;
                FirstRow = m_maps[index].NextRow;
                FirstColumn = m_maps[index].NextColumn;
                var queryLimit = Math.Min(val1: layout.TraceEvaluationCeiling,
                    val2: (SdfIndirectCost.SubmissionCostLimit / Math.Max(val1: 1, val2: instructionCount)));

                // Prefer existing whole rows; expensive fields split their eight-row band horizontally.
                if (FirstColumn == 0) {
                    for (var rows = Math.Min(val1: budget, val2: (SdfIndirectLightLayout.Resolution - FirstRow)); (rows >= SdfIndirectLightLayout.SliceRowEdge); rows -= SdfIndirectLightLayout.SliceRowEdge) {
                        if (Queries(0, FirstRow, SdfIndirectLightLayout.Resolution, rows) > queryLimit) { continue; }
                        RowCount = rows;
                        ColumnCount = SdfIndirectLightLayout.Resolution;
                        return;
                    }
                }
                RowCount = SdfIndirectLightLayout.SliceRowEdge;
                for (var columns = (SdfIndirectLightLayout.Resolution - FirstColumn); (columns >= SdfIndirectLightLayout.SliceRowEdge); columns -= SdfIndirectLightLayout.SliceRowEdge) {
                    if (Queries(FirstColumn, FirstRow, columns, RowCount) > queryLimit) { continue; }
                    ColumnCount = columns;
                    return;
                }
                throw new InvalidOperationException(message: $"Indirect light field with {instructionCount} instructions exceeds the submission cost limit for one workgroup.");
            }
        }
    }
    /// <summary>Commits submitted texels; only a complete region gains validity.</summary>
    /// <returns>Whether the submission completes a region.</returns>
    public bool Submitted() {
        if (Pending < 0) { return false; }
        var map = m_maps[Pending];

        map.NextColumn = (FirstColumn + ColumnCount);
        if (map.NextColumn == SdfIndirectLightLayout.Resolution) {
            map.NextColumn = 0;
            map.NextRow = (FirstRow + RowCount);
        }
        var complete = (map.NextRow == SdfIndirectLightLayout.Resolution);

        if (complete) {
            map.Valid = true;
            m_cursor = ((Pending + 1) % MapCount);
            Publications++;
        }
        Pending = -1;
        FirstRow = 0;
        RowCount = 0;
        FirstColumn = 0;
        ColumnCount = 0;
        Revision++;
        return complete;
    }
    /// <summary>Returns a valid map's metadata; invalid or unrendered maps require a bounded per-hit ray.</summary>
    /// <param name="index">The allocated map index.</param>
    /// <returns>The map's immutable snapshot.</returns>
    public SdfLightMap Snapshot(int index) {
        var map = m_maps[index];

        return new SdfLightMap(GeometryGeneration: map.GeometryGeneration, LightGeneration: map.LightGeneration, LightIndex: map.LightIndex,
            Projection: map.Projection, Receiver: map.Receiver, Valid: map.Valid);
    }

    private static Double3 Point(Vector3 value) => new(X: value.X, Y: value.Y, Z: value.Z);
    private static long Queries(int firstColumn, int firstRow, int columns, int rows) {
        var edge = ((int)SdfWorldPackage.TileSize);
        var tilesX = (((((firstColumn + columns) + edge) - 1) / edge) - (firstColumn / edge));
        var tilesY = (((((firstRow + rows) + edge) - 1) / edge) - (firstRow / edge));

        return (((((long)columns) * rows) * SdfIndirectLightLayout.MarchSteps) + ((((long)tilesX) * tilesY) * SdfIndirectLightLayout.BeamSteps));
    }

    private sealed class Map {
        public string? Owner;
        public Vector3 Direction;
        public float Penumbra;
        public int LightIndex;
        public SdfLightRegion Receiver;
        public SdfLightRegion Casters;
        public IrradianceLightProjection? Projection;
        public ulong GeometryGeneration;
        public ulong LightGeneration;
        public bool Valid;
        public int NextRow;
        public int NextColumn;
    }
}
/// <summary>The immutable identity and projection of a depth-map region.</summary>
/// <param name="Projection">The camera, absent when the region cannot be represented finitely.</param>
/// <param name="Receiver">The allocated receiver bounds.</param>
/// <param name="LightIndex">The current frame's light-table index, never its identity.</param>
/// <param name="Valid">Whether the current snapshot has a submitted map.</param>
/// <param name="LightGeneration">The exact light/region generation.</param>
/// <param name="GeometryGeneration">The exact uploaded-geometry generation.</param>
public readonly record struct SdfLightMap(IrradianceLightProjection? Projection, SdfLightRegion Receiver,
    int LightIndex, bool Valid, ulong LightGeneration, ulong GeometryGeneration);
