using System.Numerics;
using Puck.SdfVm;
using Puck.SignedDistance.Baking;
using Puck.SignedDistance.Queries;

namespace Puck.Testing;

/// <summary>
/// Contours a field on the CPU into a mesh the way the baked-mesh path's contour does (<see cref="SdfSampledLattice"/>),
/// sampling the field through <see cref="SdfFieldEvaluator"/> where the bake samples it on the GPU: the stand-in for a
/// stored bake a mesh audit law measures.
/// </summary>
internal static class SdfMeshContour {
    /// <summary>Contours a field over a box.</summary>
    /// <param name="field">The field.</param>
    /// <param name="low">The box's low corner.</param>
    /// <param name="high">The box's high corner.</param>
    /// <param name="samples">The samples along each axis.</param>
    /// <returns>The mesh, each quad split into two triangles.</returns>
    public static SdfMesh Bake(SdfFieldEvaluator field, Vector3 low, Vector3 high, int samples) {
        var cell = ((high - low) / (samples - 1));
        var count = ((samples * samples) * samples);
        var values = new float[count];

        for (var index = 0; (index < count); index++) {
            values[index] = SdfMeshAudit.Distance(field: field, point: (low + (new Vector3(x: (index % samples), y: ((index / samples) % samples), z: (index / (samples * samples))) * cell)));
        }

        var lattice = new SdfSampledLattice(cell: cell, materials: new int[count], origin: low, samplesX: samples, samplesY: samples, samplesZ: samples, values: values);
        var points = lattice.GradientPoints();
        var gradients = new float[3 * points.Length];

        for (var index = 0; (index < points.Length); index++) {
            var gradient = SdfMeshAudit.Gradient(field: field, point: points[index], step: 0.001f);

            gradients[3 * index] = gradient.X;
            gradients[(3 * index) + 1] = gradient.Y;
            gradients[(3 * index) + 2] = gradient.Z;
        }
        lattice.AnswerGradients(first: 0, gradients: gradients, values: [.. points.Select(selector: point => SdfMeshAudit.Distance(field: field, point: point))]);

        var contour = lattice.Contour();
        var positions = new Vector3[contour.VertexCount];
        var normals = new Vector3[contour.VertexCount];
        var indices = new List<uint>();

        for (var vertex = 0; (vertex < positions.Length); vertex++) {
            positions[vertex] = new Vector3(x: contour.Positions[3 * vertex], y: contour.Positions[(3 * vertex) + 1], z: contour.Positions[(3 * vertex) + 2]);
            normals[vertex] = new Vector3(x: contour.Normals[3 * vertex], y: contour.Normals[(3 * vertex) + 1], z: contour.Normals[(3 * vertex) + 2]);
        }
        for (var quad = 0; (quad < contour.QuadCount); quad++) {
            var (a, b, c, d) = ((uint)contour.Quads[4 * quad], (uint)contour.Quads[(4 * quad) + 1], (uint)contour.Quads[(4 * quad) + 2], (uint)contour.Quads[(4 * quad) + 3]);

            indices.AddRange(collection: [a, b, c]);
            if (c != d) { indices.AddRange(collection: [a, c, d]); }
        }

        return new SdfMesh(indices: indices.ToArray(), normals: normals, positions: positions, triangleMaterials: new uint[indices.Count / 3]);
    }
}
