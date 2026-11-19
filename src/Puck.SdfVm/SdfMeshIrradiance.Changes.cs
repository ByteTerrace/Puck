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
    private const int MaxChangeLeaves = (1 << CutDepth);

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

            var lows = new Vector3[boxes.Count];
            var highs = new Vector3[boxes.Count];

            for (var index = 0; (index < boxes.Count); index++) { (lows[index], highs[index]) = (boxes[index].Low, boxes[index].High); }

            var (built, count, order, _, _, _) = SdfMeshIrradianceScene.Hierarchy.Build(braid: 0, count: boxes.Count, highs: highs, leaf: 1, lows: lows, name: "sdf.mesh-gi change", rent: false);

            RentedArrays.Return(array: order);

            var nodes = new List<ChangeNode>(capacity: count);
            var depth = Emit(built: built, depth: 0, index: 0, nodes: nodes);

            return new ChangeTree(nodes: [.. nodes], depth: depth, boxes: boxes.Count);
        }
        // Emits a node and its subtree in depth-first order, an inner node at CutDepth as a leaf of its bounds, and
        // returns the subtree's deepest leaf.
        private static int Emit(SdfMeshIrradianceScene.Node[] built, int index, int depth, List<ChangeNode> nodes) {
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
    // KEEP IN SYNC with sdf-probe-select, which answers the same on the GPU from the rows WriteChanges packs.
    private static bool Affected(in ProbeData probe, (int X, int Y, int Z) cell, Vector3 spacing, Layout layout, in Snapshot snapshot) {
        var changes = snapshot.Changes;

        if ((changes.Length == 0) || (probe.Geometry < (changes[0].Geometry - 1))) { return true; }

        var centre = (new Vector3(x: (cell.X + 0.5f), y: (cell.Y + 0.5f), z: (cell.Z + 0.5f)) * spacing);
        var placement = ((1.5f * spacing) + new Vector3(value: SdfMeshIrradianceProbe.Clearance));
        var origin = (centre + probe.Offset);
        var hits = probe.HitWords;
        var reach = -1f;

        foreach (var (geometry, tree) in changes) {
            if (geometry <= probe.Geometry) { continue; }
            if (tree is null) { return true; }
            if (reach < 0f) { reach = RayReach(hits: hits, limit: layout.Reach); }

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
    // The distance every ray of a probe ends within, the margins a box test grants included: a box farther from the
    // origin meets none of its rays.
    private static float RayReach(ReadOnlySpan<uint> hits, float limit) => Extended(reach: FarthestHit(hits: hits, limit: limit));
    // The farthest any of a probe's rays travelled, a miss to its reach, held to the layout's reach: every hit lies
    // within it of the probe's origin (ProbeData.Reach).
    private static float FarthestHit(ReadOnlySpan<uint> hits, float limit) {
        var far = 0f;

        for (var texel = 0; (texel < SdfMeshProbeDepth.HitCount); texel++) { far = MathF.Max(x: far, y: MathF.Min(x: MathF.Abs(x: BitConverter.UInt32BitsToSingle(value: hits[(2 * texel)])), y: limit)); }
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

            if (Meets(direction: direction, high: high, low: low, origin: origin, reach: Extended(reach: reach))) { return true; }
        }
        return false;
    }
    // Whether a ray from an origin meets a box within a reach (slab test).
    private static bool Meets(Vector3 origin, Vector3 direction, Vector3 low, Vector3 high, float reach) {
        var inverse = (Vector3.One / direction);
        var near = ((low - origin) * inverse);
        var far = ((high - origin) * inverse);
        var enter = Vector3.Min(value1: near, value2: far);
        var leave = Vector3.Max(value1: near, value2: far);
        var start = MathF.Max(x: MathF.Max(x: enter.X, y: enter.Y), y: MathF.Max(x: enter.Z, y: 0f));
        var end = MathF.Min(x: MathF.Min(x: leave.X, y: leave.Y), y: MathF.Min(x: leave.Z, y: reach));

        return (start <= end);
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
    // hierarchy does where one of its leaves overlaps the box. False when the geometry is older than the changes
    // remember, since the caller cannot know what lies between.
    private static bool MeetsChange((int Geometry, ChangeTree? Tree)[] changes, int since, Vector3 low, Vector3 high) {
        if ((changes.Length == 0) || (since < (changes[0].Geometry - 1))) { return true; }
        foreach (var (geometry, tree) in changes) {
            if (geometry <= since) { continue; }
            if (tree is null) { return true; }

            var nodes = tree.Nodes;

            for (var at = 0; (at < nodes.Length);) {
                ref readonly var node = ref nodes[at];

                if (!Overlaps(high: node.High, low: node.Low, otherHigh: high, otherLow: low)) {
                    at = node.Escape;
                    continue;
                }
                if (node.Leaf) { return true; }
                at++;
            }
        }
        return false;
    }
    // Whether a probe relit under an earlier geometry keeps the cube its point and spot lights and emission send, and
    // the visibility of those lights from its centre: it traces no new hits, the local lighting it was relit under
    // stands, and no change since it was relit reaches a segment from one of its hits toward a light whose range covers
    // that hit, nor from its centre toward the light. Every hit lies within the probe's recorded reach of its origin
    // (ProbeData.Reach), so a light's segments lie in the box about that sphere's part inside the light's range, the
    // light and the origin. The one rule for the CPU and the GPU relight.
    private static bool ReusesLocal(in ProbeData previous, bool trace, Lighting lighting, Vector3 origin, (int Geometry, ChangeTree? Tree)[] changes, int geometry) {
        if (trace || (previous.Shading is null) || (previous.LocalVersion != lighting.LocalVersion)) { return false; }
        if (previous.Geometry == geometry) { return true; }

        var reach = previous.Reach;
        var margin = new Vector3(value: SdfMeshIrradianceProbe.BoxMargin);

        foreach (var light in lighting.Points) {
            var range = new Vector3(value: light.Range);
            var low = Vector3.Max(value1: (origin - new Vector3(value: reach)), value2: (light.Position - range));
            var high = Vector3.Min(value1: (origin + new Vector3(value: reach)), value2: (light.Position + range));

            // No hit lies within the light's range: it lights none of the probe's hits.
            if ((low.X > high.X) || (low.Y > high.Y) || (low.Z > high.Z)) { continue; }
            low = Vector3.Min(value1: Vector3.Min(value1: low, value2: light.Position), value2: origin) - margin;
            high = Vector3.Max(value1: Vector3.Max(value1: high, value2: light.Position), value2: origin) + margin;
            if (MeetsChange(changes: changes, high: high, low: low, since: previous.Geometry)) { return false; }
        }
        return true;
    }

    // The changes as sdf-probe-select reads them, appended to the selection's header rows: a row a change (its geometry,
    // its first node's row, its node count, and its flags: bit 0 a change of every probe, the deepest leaf from bit 8),
    // then each change's nodes, two rows a node: its low corner and its escape (an index among the change's nodes), its
    // high corner and whether it is a leaf. Returns the nodes written. KEEP IN SYNC with sdf-probe-select and
    // SdfMeshProbeGpuWork.Select.
    private static int WriteChanges(List<Vector4> rows, (int Geometry, ChangeTree? Tree)[] changes) {
        var first = (rows.Count + changes.Length);
        var nodes = 0;

        foreach (var (geometry, tree) in changes) {
            var count = (tree?.Nodes.Length ?? 0);
            var flags = ((tree is null) ? 1u : (((uint)tree.Depth) << 8));

            rows.Add(item: new Vector4(BitConverter.UInt32BitsToSingle(value: (uint)geometry), BitConverter.UInt32BitsToSingle(value: (uint)(first + (2 * nodes))),
                BitConverter.UInt32BitsToSingle(value: (uint)count), BitConverter.UInt32BitsToSingle(value: flags)));
            nodes += count;
        }
        foreach (var (_, tree) in changes) {
            if (tree is null) { continue; }
            foreach (var node in tree.Nodes) {
                rows.Add(item: new Vector4(node.Low, BitConverter.UInt32BitsToSingle(value: (uint)node.Escape)));
                rows.Add(item: new Vector4(node.High, (node.Leaf ? 1f : 0f)));
            }
        }
        return nodes;
    }
}
