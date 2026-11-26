using System.Numerics;
using Puck.Abstractions.Memory;

namespace Puck.SdfVm;

// The geometry changes the volume remembers (m_changes) and the questions asked of them: whether a probe's hits may
// differ under the current geometry (Affected, and sdf-probe-select on the GPU), and whether a region (the shadow
// segments of a probe's local cube) meets anything a change added or removed (MeetsChange).
//
// A change is a box hierarchy over the world boxes of the static draws it added or removed (ChangeTree), or none for a
// change that reaches every probe (a layout or reach change, or a draw with no box). A wave of thousands of changed
// draws stays a hierarchy of their own boxes, so a swap beside the hall names the hall's probes and not the valley's;
// past MaxChangeLeaves its deepest subtrees stand as their bounds, which name more probes but never fewer, and keep a
// selection's walk bounded.
public sealed partial class SdfMeshIrradiance {
    // The most leaves one change's hierarchy keeps: a subtree below CutDepth stands as one leaf of its bounds.
    private const int CutDepth = 12;

    // A change's hierarchy in depth-first order: a node's subtree is the nodes from it to its Escape, an inner node's
    // first child follows it, and a leaf stands for the boxes under it by its bounds. The walk needs no stack.
    private sealed class ChangeTree(ChangeNode[] nodes, int depth, int boxes) {
        public ChangeNode[] Nodes { get; } = nodes;
        // The deepest leaf, the root at zero: what one ray's walk descends at most.
        public int Depth { get; } = depth;
        // The changed draws' boxes the hierarchy holds.
        public int Boxes { get; } = boxes;

        // A hierarchy over a change's boxes, built with the scene's own box hierarchy (SdfMeshIrradianceScene.Hierarchy);
        // an empty one for a change that named no box.
        public static ChangeTree Of(List<Box> boxes) {
            if (boxes.Count == 0) { return new ChangeTree(nodes: [], depth: 0, boxes: 0); }

            // The build's workspace, native and freed on return: the tree keeps only its emitted nodes.
            using var lows = OwnedBuffer<Vector3>.OfLength(allocator: null, clear: false, length: boxes.Count);
            using var highs = OwnedBuffer<Vector3>.OfLength(allocator: null, clear: false, length: boxes.Count);
            using var built = new OwnedBuffer<SdfMeshIrradianceScene.Node>(allocator: null);
            var lowAt = lows.GetSpan();
            var highAt = highs.GetSpan();

            for (var index = 0; (index < boxes.Count); index++) { (lowAt[index], highAt[index]) = (boxes[index].Low, boxes[index].High); }

            var (count, order, _, _) = SdfMeshIrradianceScene.Hierarchy.Build(braid: 0, count: boxes.Count, highs: highs, leaf: 1, lows: lows, name: "sdf.mesh-gi change", nodes: built);

            order.Dispose();

            var nodes = new List<ChangeNode>(capacity: count);
            var depth = Emit(built: built.GetSpan(), depth: 0, index: 0, nodes: nodes);

            return new ChangeTree(nodes: [.. nodes], depth: depth, boxes: boxes.Count);
        }
        // Emits a node and its subtree in depth-first order, an inner node at CutDepth as a leaf of its bounds, and
        // returns the subtree's deepest leaf.
        private static int Emit(ReadOnlySpan<SdfMeshIrradianceScene.Node> built, int index, int depth, List<ChangeNode> nodes) {
            var node = built[index];
            var at = nodes.Count;
            var leaf = ((node.Count > 0) || (depth >= CutDepth));

            nodes.Add(item: new ChangeNode(Escape: 0, High: node.High, Leaf: leaf, Low: node.Low));

            var deepest = depth;

            if (!leaf) {
                deepest = Math.Max(val1: Emit(built: built, depth: (depth + 1), index: node.First, nodes: nodes), val2: Emit(built: built, depth: (depth + 1), index: (node.First + 1), nodes: nodes));
            }
            nodes[at] = (nodes[at] with { Escape = nodes.Count });
            return deepest;
        }
    }
    private readonly record struct ChangeNode(Vector3 Low, Vector3 High, int Escape, bool Leaf);

    // Whether a probe traced against an earlier geometry may have other hits or another placement under the current
    // one: some draw a change since added or removed lies in the region its placement searches, or meets one of its
    // rays before the hit the ray recorded. A probe no change can reach keeps its hits, which a trace would reproduce.
    // KEEP IN SYNC with sdf-probe-select, which answers the same on the GPU from the rows SelectionRows packs.
    private static bool Affected(in ProbeData probe, (int X, int Y, int Z) cell, Vector3 spacing, Layout layout, in Snapshot snapshot) {
        var changes = snapshot.Changes;

        if ((changes.Length == 0) || (probe.Geometry < (changes[0].Geometry - 1))) { return true; }

        var centre = (new Vector3(x: (cell.X + 0.5f), y: (cell.Y + 0.5f), z: (cell.Z + 0.5f)) * spacing);
        var placement = ((1.5f * spacing) + new Vector3(value: SdfMeshIrradianceProbe.Clearance));
        var origin = (centre + probe.Offset);
        var hits = probe.HitWords;
        var reach = Extended(probe.Reach);

        foreach (var (geometry, tree) in changes) {
            if (geometry <= probe.Geometry) { continue; }
            if (tree is null) { return true; }

            var nodes = tree.Nodes;

            for (var at = 0; (at < nodes.Length);) {
                ref readonly var node = ref nodes[at];
                var placed = Overlaps(high: node.High, low: node.Low, otherHigh: (centre + placement), otherLow: (centre - placement));

                if (!placed && !Near(high: node.High, low: node.Low, origin: origin, radius: reach)) {
                    at = node.Escape;
                    continue;
                }
                if (!node.Leaf) {
                    at++;
                    continue;
                }
                if (placed || RaysMeet(high: node.High, hits: hits, limit: layout.Reach, low: node.Low, origin: origin)) { return true; }
                at = node.Escape;
            }
        }
        return false;
    }
    // The farthest any of a probe's rays travelled, a miss to its reach, held to the layout's reach: every hit lies
    // within it of the probe's origin (ProbeData.Reach).
    private static float FarthestHit(ReadOnlySpan<uint> hits, float limit, out bool frontHit, out ulong openSkyGroups) {
        var far = 0f;
        frontHit = false;
        openSkyGroups = 0;
        for (var texel = 0; texel < SdfMeshProbeDepth.HitCount; texel++) {
            var distance = BitConverter.UInt32BitsToSingle(hits[2 * texel]);
            if (float.IsPositiveInfinity(distance)) { openSkyGroups |= SkyGroup(texel); }
            frontHit |= float.IsFinite(distance) && distance >= 0f;
            far = MathF.Max(far, MathF.Min(MathF.Abs(distance), limit));
        }
        return far;
    }
    // A recorded hit's reach with its margins: one on the box's face lies at its entry, and rounding must not pass it
    // by; the sheet test reads the scene up to a sheet's thickness behind the hit (SdfMeshProbeDepth.Surfaces).
    private static float Extended(float reach) =>
        ((reach + MathF.Max(x: SdfMeshIrradianceProbe.BoxMargin, y: (reach * SdfMeshIrradianceProbe.BoxMarginShare))) + SdfMeshIrradianceProbe.DepthSheetThickness);
    // Whether a box meets one of a probe's rays before the hit it recorded: the box's bounding cone from the origin
    // selects the rays, and each one's slab test decides. An origin inside the box's bounding sphere is met.
    private static bool RaysMeet(Vector3 origin, Vector3 low, Vector3 high, ReadOnlySpan<uint> hits, float limit) {
        var directions = SdfMeshProbeDepth.RayDirections;
        var toward = (((low + high) * 0.5f) - origin);
        var radius = ((high - low) * 0.5f).Length();
        var distance = toward.Length();

        if (distance <= radius) { return true; }

        var axis = (toward / distance);
        var cone = MathF.Sqrt(x: (1f - ((radius * radius) / (distance * distance))));

        for (var texel = 0; (texel < SdfMeshProbeDepth.HitCount); texel++) {
            var direction = directions[texel];

            if (Vector3.Dot(vector1: direction, vector2: axis) < cone) { continue; }

            var reach = MathF.Min(x: MathF.Abs(x: BitConverter.UInt32BitsToSingle(value: hits[(2 * texel)])), y: limit);

            if (SdfMeshIrradianceScene.Slab(entry: out _, high: high, inverse: (Vector3.One / direction), low: low, maximum: Extended(reach: reach), origin: origin)) { return true; }
        }
        return false;
    }
    private static bool Overlaps(Vector3 low, Vector3 high, Vector3 otherLow, Vector3 otherHigh) {
        var from = Vector3.Max(value1: low, value2: otherLow);
        var to = Vector3.Min(value1: high, value2: otherHigh);

        return ((from.X <= to.X) && (from.Y <= to.Y) && (from.Z <= to.Z));
    }
    // Whether a box comes within a radius of a point.
    private static bool Near(Vector3 low, Vector3 high, Vector3 origin, float radius) {
        var outside = Vector3.Max(value1: Vector3.Max(value1: (low - origin), value2: (origin - high)), value2: Vector3.Zero);

        return (outside.LengthSquared() <= (radius * radius));
    }
    // Whether any change newer than a geometry reaches a world box: a change of every probe does, and a change's
    // hierarchy does where one of its leaves overlaps the box. True when the geometry is older than the changes
    // remember, since the caller cannot know what lies between.
    private static bool MeetsChange((int Geometry, ChangeTree? Tree)[] changes, int since, Vector3 low, Vector3 high) =>
        Meets(changes: changes, region: new BoxRegion(High: high, Low: low), since: since);
    // Whether any change newer than a geometry comes within a margin of a segment: a change's hierarchy does where one of
    // its leaves, grown by the margin, meets the segment. Answers as MeetsChange does otherwise.
    private static bool MeetsSegment((int Geometry, ChangeTree? Tree)[] changes, int since, Vector3 from, Vector3 to, float margin) {
        var toward = (to - from);
        var length = toward.Length();

        return (length > 0f)
            ? Meets(changes: changes, region: new SegmentRegion(From: from, Grow: new Vector3(value: margin), Inverse: (Vector3.One / (toward / length)), Length: length), since: since)
            : MeetsChange(changes: changes, high: (from + new Vector3(value: margin)), low: (from - new Vector3(value: margin)), since: since);
    }
    // The one walk of the remembered changes newer than a geometry: true for a change of every probe, a leaf the region
    // meets, or a geometry older than the changes remember; a node the region misses skips its subtree.
    private static bool Meets<TRegion>((int Geometry, ChangeTree? Tree)[] changes, int since, TRegion region) where TRegion : struct, IChangeRegion {
        if ((changes.Length == 0) || (since < (changes[0].Geometry - 1))) { return true; }
        foreach (var (geometry, tree) in changes) {
            if (geometry <= since) { continue; }
            if (tree is null) { return true; }

            var nodes = tree.Nodes;

            for (var at = 0; (at < nodes.Length);) {
                ref readonly var node = ref nodes[at];

                if (!region.Meets(high: node.High, low: node.Low)) {
                    at = node.Escape;
                    continue;
                }
                if (node.Leaf) { return true; }
                at++;
            }
        }
        return false;
    }
    // A region a change's box may meet: a box, or a segment against boxes grown by a margin.
    private interface IChangeRegion {
        bool Meets(Vector3 low, Vector3 high);
    }
    private readonly record struct BoxRegion(Vector3 Low, Vector3 High) : IChangeRegion {
        public bool Meets(Vector3 low, Vector3 high) => Overlaps(high: high, low: low, otherHigh: High, otherLow: Low);
    }
    // A segment from a point along a unit direction, given by its reciprocal, for a length.
    private readonly record struct SegmentRegion(Vector3 From, Vector3 Inverse, float Length, Vector3 Grow) : IChangeRegion {
        public bool Meets(Vector3 low, Vector3 high) => SdfMeshIrradianceScene.Slab(entry: out _, high: (high + Grow), inverse: Inverse, low: (low - Grow), maximum: Length, origin: From);
    }
    // Whether a probe relit under an earlier geometry keeps the cube its point and spot lights and emission send, and
    // the visibility of those lights from its centre: it traces no new hits, the local lighting it was relit under
    // stands, and no change since it was relit reaches a segment from one of its hits toward a light whose range covers
    // that hit, nor the segment from its centre toward any light, at any distance (its visibility bit is the geometry's,
    // never the range's). Every hit lies within the probe's recorded reach of its origin (ProbeData.Reach), so a light's
    // hit segments lie in the box about that sphere's part inside the light's range, the light and the origin, which
    // holds the centre's segment too. The one rule for the CPU and the GPU relight.
    private static bool ReusesLocal(in ProbeData previous, bool trace, Lighting lighting, Vector3 origin, (int Geometry, ChangeTree? Tree)[] changes, int geometry) {
        if (trace || (previous.Shading is null) || (previous.LocalVersion != lighting.LocalVersion)) { return false; }
        if (previous.Geometry == geometry) { return true; }

        var reach = previous.Reach;
        var margin = new Vector3(value: SdfMeshIrradianceProbe.BoxMargin);

        foreach (var light in lighting.Points) {
            var range = new Vector3(value: light.Range);
            var low = Vector3.Max(value1: (origin - new Vector3(value: reach)), value2: (light.Position - range));
            var high = Vector3.Min(value1: (origin + new Vector3(value: reach)), value2: (light.Position + range));

            if ((light.Range > 0f) && (low.X <= high.X) && (low.Y <= high.Y) && (low.Z <= high.Z)) {
                low = Vector3.Min(value1: Vector3.Min(value1: low, value2: light.Position), value2: origin) - margin;
                high = Vector3.Max(value1: Vector3.Max(value1: high, value2: light.Position), value2: origin) + margin;
                if (MeetsChange(changes: changes, high: high, low: low, since: previous.Geometry)) { return false; }
            } else if (MeetsSegment(changes: changes, from: origin, margin: SdfMeshIrradianceProbe.BoxMargin, since: previous.Geometry, to: light.Position)) {
                // No hit lies within the light's range, so only the centre's visibility of it can change.
                return false;
            }
        }
        return true;
    }
    // A selection's rows as sdf-probe-select reads them, exactly sized: the header rows (the volume's geometry; the
    // change count, node count, oldest change's geometry and reach; each level's spacing), then a row a change (its
    // geometry, its first node's row, its node count, and its flags: bit 0 a change of every probe, the deepest leaf from
    // bit 8), then each change's nodes, two rows a node: its low corner and its escape (an index among the change's
    // nodes), its high corner and whether it is a leaf. The array `rows` holds is rewritten and returned when it already
    // has the length, so selections under one set of changes share one array: the probe service answers or cancels every
    // job of a selection before Select returns. KEEP IN SYNC with sdf-probe-select and SdfMeshProbeGpuWork.Select.
    private static Vector4[] SelectionRows(int geometry, (int Geometry, ChangeTree? Tree)[] changes, Layout layout, Vector4[] rows) {
        var nodes = 0;

        foreach (var (_, tree) in changes) { nodes += (tree?.Nodes.Length ?? 0); }

        var first = (SdfMeshProbeRelight.SelectionHeaderRows + changes.Length);

        if (rows.Length != (first + (2 * nodes))) { rows = new Vector4[first + (2 * nodes)]; }
        rows[0] = new Vector4(BitConverter.Int32BitsToSingle(value: geometry), 0f, 0f, 0f);
        rows[1] = new Vector4(changes.Length, nodes, BitConverter.Int32BitsToSingle(value: ((changes.Length == 0) ? 0 : changes[0].Geometry)), layout.Reach);
        for (var level = 0; (level < SdfMeshIrradianceProbe.Levels); level++) { rows[2 + level] = new Vector4(layout.SpacingOf(level: level), 0f); }

        var head = SdfMeshProbeRelight.SelectionHeaderRows;
        var at = first;
        var written = 0;

        foreach (var (changed, tree) in changes) {
            var count = (tree?.Nodes.Length ?? 0);
            var flags = ((tree is null) ? 1u : (((uint)tree.Depth) << 8));

            rows[head++] = new Vector4(BitConverter.UInt32BitsToSingle(value: (uint)changed), BitConverter.UInt32BitsToSingle(value: (uint)(first + (2 * written))),
                BitConverter.UInt32BitsToSingle(value: (uint)count), BitConverter.UInt32BitsToSingle(value: flags));
            written += count;
        }
        foreach (var (_, tree) in changes) {
            if (tree is null) { continue; }
            foreach (var node in tree.Nodes) {
                rows[at++] = new Vector4(node.Low, BitConverter.UInt32BitsToSingle(value: (uint)node.Escape));
                rows[at++] = new Vector4(node.High, (node.Leaf ? 1f : 0f));
            }
        }
        return rows;
    }
}
