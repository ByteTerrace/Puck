using System.Numerics;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.SignedDistance.Illumination;

namespace Puck.SdfVm;

/// <summary>The exact uploaded geometry revisions that invalidate a residency's light maps.</summary>
/// <param name="Program">Program and shader revision.</param>
/// <param name="Poses">Uploaded dynamic-transform revision.</param>
/// <param name="Mesh">Uploaded mesh revision.</param>
/// <param name="Decals">Glyph geometry revision.</param>
public readonly record struct SdfLightGeometry(ulong Program, ulong Poses, long Mesh, ulong Decals);

/// <summary>A finite receiver box from the allocated bricks of one cache level.</summary>
/// <param name="Min">Least corner.</param>
/// <param name="Max">Greatest corner.</param>
public readonly record struct SdfLightRegion(Double3 Min, Double3 Max);

/// <summary>One residency's bounded light-view schedule. It retains exact owner names and revision tuples, plans one
/// region per frame, and publishes its validity only after the depth writer submits. An absent finite caster volume
/// or unnamed owner never authorizes a map lookup.</summary>
public sealed class SdfIndirectLightViews {
    private readonly Map[] m_maps = [.. Enumerable.Range(start: 0, count: SdfIndirectLightLayout.MaxMaps).Select(selector: static _ => new Map())];
    private int m_cursor;
    private long m_frame = -1;
    private object? m_geometryOwner;
    private SdfLightGeometry m_geometry;
    private ulong m_geometryGeneration;

    /// <summary>Gets the region scheduled this frame, or -1 when all usable regions stand.</summary>
    public int Pending { get; private set; } = -1;
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

    /// <summary>Invalidates publications when the graph releases the depth-bank allocation they describe.</summary>
    public void InvalidateStorage() {
        foreach (var map in m_maps) { map.Valid = false; }
        Pending = -1;
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
    public void Plan(long frame, object geometryOwner, SdfLightGeometry geometry, SdfLights lights,
        IReadOnlyList<SdfLightRegion?> regions, SdfLightRegion? casters, bool forceGeometry) {
        if (m_frame == frame) { return; }
        m_frame = frame;
        Pending = -1;
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
                if (map.Projection is not null) { map.Projection = null; map.Valid = false; Revision++; }
                continue;
            }
            var changed = (map.Projection is null) || !StringComparer.Ordinal.Equals(x: owner, y: map.Owner) ||
                (map.GeometryGeneration != m_geometryGeneration) || (map.Receiver != receiver) || (map.Casters != caster) ||
                SdfLightMotion.Changed(direction: light.Direction, penumbra: light.Param, anchorDirection: map.Direction, anchorPenumbra: map.Penumbra);
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
            map.Projection = IrradianceLightProjection.Create(receiverMin: receiver.Min, receiverMax: receiver.Max,
                casterMin: caster.Min, casterMax: caster.Max, towardLight: Point(value: map.Direction), penumbraSlope: light.Param,
                resolution: SdfIndirectLightLayout.Resolution);
            Revision++;
        }
        for (var offset = 0; (offset < MapCount); offset++) {
            var index = ((m_cursor + offset) % MapCount);
            if ((m_maps[index].Projection is not null) && !m_maps[index].Valid) {
                Pending = index;
                return;
            }
        }
    }

    /// <summary>Commits the scheduled region after its depth writer submits; failed recordings retain no validity.</summary>
    public void Submitted() {
        if (Pending < 0) { return; }
        m_maps[Pending].Valid = true;
        m_cursor = ((Pending + 1) % MapCount);
        Pending = -1;
        Publications++;
        Revision++;
    }
    /// <summary>Returns a valid map's metadata; invalid or unrendered maps require a bounded per-hit ray.</summary>
    /// <param name="index">The allocated map index.</param>
    /// <returns>The map's immutable snapshot.</returns>
    public SdfLightMap Snapshot(int index) {
        var map = m_maps[index];
        return new SdfLightMap(Projection: map.Projection, Receiver: map.Receiver, LightIndex: map.LightIndex,
            Valid: map.Valid, LightGeneration: map.LightGeneration, GeometryGeneration: map.GeometryGeneration);
    }
    private static Double3 Point(Vector3 value) => new(X: value.X, Y: value.Y, Z: value.Z);
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
