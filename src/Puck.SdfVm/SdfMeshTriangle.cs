using System.Numerics;

namespace Puck.SdfVm;

// Shared nearest-point primitive for the offline bake audit and the retained live scene.
internal static class SdfMeshTriangle {
    // The point of a triangle nearest p, by the triangle's Voronoi regions.
    public static Vector3 Closest(Vector3 p, Vector3 a, Vector3 b, Vector3 c) {
        var ab = (b - a);
        var ac = (c - a);
        var ap = (p - a);
        var d1 = Vector3.Dot(vector1: ab, vector2: ap);
        var d2 = Vector3.Dot(vector1: ac, vector2: ap);

        if ((d1 <= 0f) && (d2 <= 0f)) { return a; }

        var bp = (p - b);
        var d3 = Vector3.Dot(vector1: ab, vector2: bp);
        var d4 = Vector3.Dot(vector1: ac, vector2: bp);

        if ((d3 >= 0f) && (d4 <= d3)) { return b; }

        var vc = ((d1 * d4) - (d3 * d2));

        if ((vc <= 0f) && (d1 >= 0f) && (d3 <= 0f)) { return (a + (ab * (d1 / (d1 - d3)))); }

        var cp = (p - c);
        var d5 = Vector3.Dot(vector1: ab, vector2: cp);
        var d6 = Vector3.Dot(vector1: ac, vector2: cp);

        if ((d6 >= 0f) && (d5 <= d6)) { return c; }

        var vb = ((d5 * d2) - (d1 * d6));

        if ((vb <= 0f) && (d2 >= 0f) && (d6 <= 0f)) { return (a + (ac * (d2 / (d2 - d6)))); }

        var va = ((d3 * d6) - (d5 * d4));

        if ((va <= 0f) && ((d4 - d3) >= 0f) && ((d5 - d6) >= 0f)) { return (b + ((c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6))))); }

        var denominator = (1f / ((va + vb) + vc));

        return ((a + (ab * (vb * denominator))) + (ac * (vc * denominator)));
    }
}
