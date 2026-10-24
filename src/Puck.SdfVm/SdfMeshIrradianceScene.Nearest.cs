using System.Numerics;

namespace Puck.SdfVm;

public sealed partial class SdfMeshIrradianceScene {
    /// <summary>Finds the closest point on the retained static opaque triangles inside a world-space search radius.
    /// The radius limits coverage, not acceptable error. A miss is unknown, never evidence of an empty neighbourhood.
    /// Unlike a normal ray this includes edges, vertices and zero-distance witnesses. All distances and triangle tests
    /// use world space, including nonuniformly scaled draws.</summary>
    /// <param name="point">World-space witness.</param>
    /// <param name="radius">Maximum correspondence search distance in metres.</param>
    /// <param name="surface">Certified nearest surface point inside the radius.</param>
    /// <param name="normal">The nearest triangle's wound world-space unit normal.</param>
    /// <param name="bakeHalfDiagonal">The nearest triangle's own contour precision.</param>
    /// <returns>Whether a finite, nondegenerate nearest triangle was found.</returns>
    public bool TryNearestSurface(Vector3 point, float radius, out Vector3 surface, out Vector3 normal, out float bakeHalfDiagonal) {
        var nearest = new NearestSurface { Squared = radius * radius, Instance = -1, Triangle = -1 };
        if (float.IsFinite(nearest.Squared) && radius >= 0 && float.IsFinite(point.LengthSquared())) {
            NearestNode(m_nodes, 0, -1, point, ref nearest);
        }
        surface = nearest.Point;
        normal = default;
        bakeHalfDiagonal = 0;
        if (nearest.Instance < 0) { return false; }
        ref readonly var instance = ref m_instances[nearest.Instance];
        normal = instance.NormalOf(nearest.Triangle);
        bakeHalfDiagonal = instance.BakeHalfDiagonal;
        return true;
    }

    private struct NearestSurface {
        public float Squared;
        public Vector3 Point;
        public int Instance;
        public int Triangle;
    }

    // Reuse both levels of the ray scene's hierarchy. Transform node boxes conservatively, then test triangles in world
    // space: an object-space closest point is not the world-space closest point under a nonuniform scale or shear.
    private void NearestNode(Node[] nodes, int index, int instanceIndex, Vector3 point, ref NearestSurface nearest) {
        ref readonly var node = ref nodes[index];
        if (NearestBox(node, instanceIndex, point) > nearest.Squared) { return; }
        if (node.Count == 0) {
            var left = node.First;
            var right = left + 1;
            if (NearestBox(nodes[left], instanceIndex, point) > NearestBox(nodes[right], instanceIndex, point)) {
                (left, right) = (right, left);
            }
            NearestNode(nodes, left, instanceIndex, point, ref nearest);
            NearestNode(nodes, right, instanceIndex, point, ref nearest);
            return;
        }
        for (var i = node.First; i < node.First + node.Count; i++) {
            if (instanceIndex < 0) {
                var entry = m_entries[i];
                NearestNode(m_instances[entry.Instance].Geometry.Nodes, entry.Node, entry.Instance, point, ref nearest);
                continue;
            }
            ref readonly var instance = ref m_instances[instanceIndex];
            var geometry = instance.Geometry;
            var a = Vector3.Transform(geometry.V0[i], instance.ObjectToWorld);
            var b = Vector3.Transform(geometry.V0[i] + geometry.E1[i], instance.ObjectToWorld);
            var c = Vector3.Transform(geometry.V0[i] + geometry.E2[i], instance.ObjectToWorld);
            if (Vector3.Cross(b - a, c - a).LengthSquared() == 0) { continue; }
            var closest = SdfMeshTriangle.Closest(point, a, b, c);
            var squared = Vector3.DistanceSquared(point, closest);
            if (!float.IsFinite(squared) || squared > nearest.Squared) { continue; }
            nearest = new NearestSurface { Squared = squared, Point = closest, Instance = instanceIndex, Triangle = i };
        }
    }

    private float NearestBox(in Node node, int instanceIndex, Vector3 point) {
        var (low, high) = instanceIndex < 0 || m_instances[instanceIndex].Identity
            ? (node.Low, node.High) : Bounds(m_instances[instanceIndex].ObjectToWorld, node.Low, node.High);
        return Vector3.DistanceSquared(point, Vector3.Clamp(point, low, high));
    }
}
