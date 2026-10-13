using System.Numerics;

namespace Puck.SignedDistance.Baking;

/// <summary>A contoured surface in world space: a vertex per surface patch of each crossed cell, with its normal, and a
/// quad per sign-changing lattice edge, wound counter-clockwise seen from outside, with the material the field reported at
/// the edge's inside end.</summary>
/// <param name="Positions">Three floats per vertex.</param>
/// <param name="Normals">Three floats per vertex: the unit outward normal.</param>
/// <param name="Quads">Four vertex indices per quad.</param>
/// <param name="QuadMaterials">One material per quad.</param>
public sealed record SdfContour(float[] Positions, float[] Normals, int[] Quads, int[] QuadMaterials) {
    /// <summary>Gets the quads.</summary>
    public int QuadCount => (Quads.Length / 4);
    /// <summary>Gets the vertices.</summary>
    public int VertexCount => (Positions.Length / 3);
}
/// <summary>
/// A field sampled on a lattice whose spacing may differ per axis, as a GPU bake reads it back: each sample's value, its
/// gradient when the bake measured one, and the material the field reported there. <see cref="Contour"/> extracts its
/// surface with the one manifold dual contouring the prototype baker uses, placing each vertex by the samples' gradients,
/// interpolated along each crossing edge, so a sharp edge between two faces stays sharp at any cell size.
/// <para>Unlike a prototype bake, a sampled lattice's values come from the GPU interpreter in floats, so its surface is
/// presentation data that may differ between devices; nothing in it reaches simulation state.</para>
/// </summary>
public sealed class SdfSampledLattice {
    private readonly Vector3 m_origin;
    private readonly Vector3 m_cell;
    private readonly int m_samplesX;
    private readonly int m_samplesY;
    private readonly int m_samplesZ;
    private readonly float[] m_values;
    private readonly float[]? m_gradients;
    private readonly int[] m_materials;

    /// <summary>Initializes a new instance of the <see cref="SdfSampledLattice"/> class.</summary>
    /// <param name="origin">The world position of sample (0, 0, 0).</param>
    /// <param name="cell">The spacing between samples along each axis, in world units; each positive.</param>
    /// <param name="samplesX">The samples along X, at least two.</param>
    /// <param name="samplesY">The samples along Y, at least two.</param>
    /// <param name="samplesZ">The samples along Z, at least two.</param>
    /// <param name="values">One field value per sample, X fastest: negative inside.</param>
    /// <param name="gradients">Three gradient components per sample, in any consistent scale, or <see langword="null"/>
    /// to take gradients from the values by central differences.</param>
    /// <param name="materials">One material per sample.</param>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> or <paramref name="materials"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An axis has fewer than two samples, a spacing is not positive, or an array's
    /// length does not match the lattice.</exception>
    public SdfSampledLattice(Vector3 origin, Vector3 cell, int samplesX, int samplesY, int samplesZ, float[] values, float[]? gradients, int[] materials) {
        ArgumentNullException.ThrowIfNull(argument: values);
        ArgumentNullException.ThrowIfNull(argument: materials);

        if ((samplesX < 2) || (samplesY < 2) || (samplesZ < 2)) {
            throw new ArgumentException(message: "A lattice holds at least two samples along each axis.");
        }
        if (!((cell.X > 0f) && (cell.Y > 0f) && (cell.Z > 0f))) {
            throw new ArgumentException(message: "A lattice's spacing is positive along each axis.", paramName: nameof(cell));
        }

        var count = ((samplesX * samplesY) * samplesZ);

        if ((values.Length != count) || (materials.Length != count) || ((gradients is not null) && (gradients.Length != (3 * count)))) {
            throw new ArgumentException(message: $"A lattice of {count} samples holds one value and material per sample and, when given, three gradient components each.");
        }

        m_origin = origin;
        m_cell = cell;
        m_samplesX = samplesX;
        m_samplesY = samplesY;
        m_samplesZ = samplesZ;
        m_values = values;
        m_gradients = gradients;
        m_materials = materials;
    }

    /// <summary>Extracts the lattice's surface.</summary>
    /// <returns>The surface; empty when no lattice edge changes sign.</returns>
    public SdfContour Contour() {
        var surface = SdfDualContouring.Extract(grid: new Lattice(owner: this));
        var materials = new int[surface.QuadCount];

        for (var quad = 0; (quad < materials.Length); quad++) {
            materials[quad] = m_materials[surface.QuadCorners[quad]];
        }

        return new SdfContour(
            Normals: [.. surface.Normals.Select(selector: static value => ((float)value))],
            Positions: [.. surface.Positions.Select(selector: static value => ((float)value))],
            QuadMaterials: materials,
            Quads: surface.Quads
        );
    }

    private int Index(int x, int y, int z) => (x + (m_samplesX * (y + (m_samplesY * z))));
    // The gradient at a sample: the bake's, or central differences of the values, one-sided at the lattice's faces.
    private Vector3 GradientAt(int x, int y, int z) {
        var index = Index(x: x, y: y, z: z);

        if (m_gradients is { } gradients) {
            return new Vector3(x: gradients[(3 * index)], y: gradients[((3 * index) + 1)], z: gradients[((3 * index) + 2)]);
        }

        float Difference(int lowIndex, int highIndex, int span, float spacing) => ((m_values[highIndex] - m_values[lowIndex]) / (span * spacing));

        var xl = Math.Max(val1: 0, val2: (x - 1));
        var xh = Math.Min(val1: (m_samplesX - 1), val2: (x + 1));
        var yl = Math.Max(val1: 0, val2: (y - 1));
        var yh = Math.Min(val1: (m_samplesY - 1), val2: (y + 1));
        var zl = Math.Max(val1: 0, val2: (z - 1));
        var zh = Math.Min(val1: (m_samplesZ - 1), val2: (z + 1));

        return new Vector3(
            x: Difference(highIndex: Index(x: xh, y: y, z: z), lowIndex: Index(x: xl, y: y, z: z), spacing: m_cell.X, span: (xh - xl)),
            y: Difference(highIndex: Index(x: x, y: yh, z: z), lowIndex: Index(x: x, y: yl, z: z), spacing: m_cell.Y, span: (yh - yl)),
            z: Difference(highIndex: Index(x: x, y: y, z: zh), lowIndex: Index(x: x, y: y, z: zl), spacing: m_cell.Z, span: (zh - zl))
        );
    }

    // The lattice as the contouring reads it: corner values in world units of field, cell coordinates scaled per axis,
    // and a normal trilinearly interpolated from the eight samples around a point.
    private readonly struct Lattice(SdfSampledLattice owner) : ISdfContourLattice {
        public int CellsX => (owner.m_samplesX - 1);
        public int CellsY => (owner.m_samplesY - 1);
        public int CellsZ => (owner.m_samplesZ - 1);
        public double CellSize => Math.Max(val1: owner.m_cell.X, val2: Math.Max(val1: owner.m_cell.Y, val2: owner.m_cell.Z));
        public (double X, double Y, double Z)? CellExtent => (owner.m_cell.X, owner.m_cell.Y, owner.m_cell.Z);

        public int Corner(int x, int y, int z) => owner.Index(x: x, y: y, z: z);
        public bool Inside(int corner) => (owner.m_values[corner] < 0f);
        public double Value(int corner, int x, int y, int z) => owner.m_values[corner];
        public (double X, double Y, double Z) World(double x, double y, double z) => (
            (owner.m_origin.X + (x * owner.m_cell.X)),
            (owner.m_origin.Y + (y * owner.m_cell.Y)),
            (owner.m_origin.Z + (z * owner.m_cell.Z))
        );
        public bool TryNormal(double x, double y, double z, out double nx, out double ny, out double nz) {
            var lx = Math.Clamp(value: ((x - owner.m_origin.X) / owner.m_cell.X), min: 0.0, max: (owner.m_samplesX - 1));
            var ly = Math.Clamp(value: ((y - owner.m_origin.Y) / owner.m_cell.Y), min: 0.0, max: (owner.m_samplesY - 1));
            var lz = Math.Clamp(value: ((z - owner.m_origin.Z) / owner.m_cell.Z), min: 0.0, max: (owner.m_samplesZ - 1));
            var x0 = Math.Min(val1: ((int)lx), val2: (owner.m_samplesX - 2));
            var y0 = Math.Min(val1: ((int)ly), val2: (owner.m_samplesY - 2));
            var z0 = Math.Min(val1: ((int)lz), val2: (owner.m_samplesZ - 2));
            var fx = (float)(lx - x0);
            var fy = (float)(ly - y0);
            var fz = (float)(lz - z0);
            var sum = Vector3.Zero;

            for (var corner = 0; (corner < 8); corner++) {
                var dx = (corner & 1);
                var dy = ((corner >> 1) & 1);
                var dz = ((corner >> 2) & 1);
                var weight = (((dx == 1) ? fx : (1f - fx)) * ((dy == 1) ? fy : (1f - fy))) * ((dz == 1) ? fz : (1f - fz));

                sum += (owner.GradientAt(x: (x0 + dx), y: (y0 + dy), z: (z0 + dz)) * weight);
            }

            var length = sum.Length();

            if (!(length > 1.0e-12f)) {
                (nx, ny, nz) = (0.0, 0.0, 0.0);

                return false;
            }

            sum /= length;
            (nx, ny, nz) = (sum.X, sum.Y, sum.Z);

            return true;
        }
    }
}
