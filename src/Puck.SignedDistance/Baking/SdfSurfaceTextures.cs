using System.Buffers.Binary;
using System.Numerics;
using Puck.Abstractions.Sources;
using Puck.Assets.Textures;
using Puck.Maths;

namespace Puck.SignedDistance.Baking;

/// <summary>
/// Lays a <see cref="SdfSurface"/> out as a <see cref="SdfBakedMesh"/> and samples its surface textures from the field.
/// Each quad owns one <see cref="SdfBakeTier.TileTexels"/>-square tile, filled row by row across the atlas in quad
/// order. A quad's corners sit on the centers of its tile's corner texels, so bilinear filtering inside a quad never
/// reads another quad's tile, and the texels of a tile are the quad's bilinear lattice: its four corners, two points on
/// each edge, and four inside. A texel shared by two quads (a corner, or a point on a shared edge) is sampled once and
/// written to both tiles.
/// </summary>
internal static class SdfSurfaceTextures {
    private const int Tile = SdfBakeTier.TileTexels;
    // Half-precision zero and one, as bits.
    private const ushort Zero = 0;
    private const ushort One = 0x3C00;
    // The ambient occlusion probes along the normal: probe i sits i cells out, and its weight halves per step.
    private const int OcclusionProbes = 4;

    private readonly record struct Texel(byte R, byte G, byte B, byte NormalU, byte NormalV, byte Occlusion, byte Material);

    public static (SdfBakedMesh Mesh, SdfBakedTexture[] Textures) Bake(SdfBakeField field, SdfSurface surface, IReadOnlyList<SdfMaterial> materials) {
        var quads = surface.QuadCount;
        var columns = SdfBakedMesh.AtlasColumns(quads: quads);
        var width = (columns * Tile);
        var height = (SdfBakedMesh.AtlasRows(columns: columns, quads: quads) * Tile);
        var vertices = new SdfBakedVertex[(quads * 4)];
        var indices = new uint[(quads * 6)];
        var albedo = new byte[((width * height) * 4)];
        var normals = new byte[((width * height) * 2)];
        var occlusion = new byte[(width * height)];
        var material = new byte[(width * height)];
        var emission = new byte[((width * height) * 8)];
        var shared = new Dictionary<(int Low, int High, int Step), Texel>();
        var palette = Palette(materials: materials);
        var glow = Emission(materials: materials);

        for (var quad = 0; (quad < quads); quad++) {
            var tileX = ((quad % columns) * Tile);
            var tileY = ((quad / columns) * Tile);
            Span<int> corner = [
                surface.Quads[(quad * 4)],
                surface.Quads[((quad * 4) + 1)],
                surface.Quads[((quad * 4) + 2)],
                surface.Quads[((quad * 4) + 3)],
            ];

            for (var index = 0; (index < 4); index++) {
                var s = (((index == 1) || (index == 2)) ? 1 : 0);
                var t = ((index >= 2) ? 1 : 0);

                vertices[((quad * 4) + index)] = new SdfBakedVertex(
                    Normal: Vector(values: surface.Normals, vertex: corner[index]),
                    Position: Vector(values: surface.Positions, vertex: corner[index]),
                    Uv: new Vector2(
                        x: ((float)(((tileX + 0.5) + (s * (Tile - 1))) / width)),
                        y: ((float)(((tileY + 0.5) + (t * (Tile - 1))) / height))
                    )
                );
            }

            WriteTriangles(indices: indices, quad: quad, vertices: vertices);

            for (var t = 0; (t < Tile); t++) {
                for (var s = 0; (s < Tile); s++) {
                    var texel = Sample(
                        corner: corner,
                        field: field,
                        palette: palette,
                        s: s,
                        shared: shared,
                        surface: surface,
                        t: t
                    );
                    var at = (((tileY + t) * width) + (tileX + s));

                    albedo[(at * 4)] = texel.R;
                    albedo[((at * 4) + 1)] = texel.G;
                    albedo[((at * 4) + 2)] = texel.B;
                    albedo[((at * 4) + 3)] = 255;
                    normals[(at * 2)] = texel.NormalU;
                    normals[((at * 2) + 1)] = texel.NormalV;
                    occlusion[at] = texel.Occlusion;
                    material[at] = texel.Material;

                    WriteEmission(glow: glow, level: emission, material: texel.Material, texel: at);
                }
            }
        }

        return (
            new SdfBakedMesh(
                CellSize: ((float)surface.CellSize),
                Indices: indices,
                TileColumns: columns,
                Vertices: vertices
            ),
            [
                SdfBakedTexture.Store(height: height, level0: albedo, tileTexels: Tile, usage: SdfBakeTextureUsage.Albedo, width: width),
                SdfBakedTexture.Store(height: height, level0: normals, tileTexels: Tile, usage: SdfBakeTextureUsage.Normal, width: width),
                SdfBakedTexture.Store(height: height, level0: occlusion, tileTexels: Tile, usage: SdfBakeTextureUsage.Occlusion, width: width),
                SdfBakedTexture.Store(height: height, level0: material, tileTexels: Tile, usage: SdfBakeTextureUsage.Material, width: width),
                SdfBakedTexture.Store(height: height, level0: emission, tileTexels: Tile, usage: SdfBakeTextureUsage.Emission, width: width),
            ]
        );
    }
    /// <summary>Returns each material's albedo as sRGB-encoded bytes, through the exact encode
    /// (<see cref="ImageSourceConversion.LinearToSrgb8"/>).</summary>
    public static (byte R, byte G, byte B)[] Palette(IReadOnlyList<SdfMaterial> materials) {
        var palette = new (byte R, byte G, byte B)[materials.Count];

        for (var index = 0; (index < palette.Length); index++) {
            var albedo = materials[index].Albedo;

            palette[index] = (
                ImageSourceConversion.LinearToSrgb8(value: albedo.X),
                ImageSourceConversion.LinearToSrgb8(value: albedo.Y),
                ImageSourceConversion.LinearToSrgb8(value: albedo.Z)
            );
        }

        return palette;
    }
    /// <summary>Returns each material's emitted light as half bits: its linear albedo times its emissive strength, each
    /// channel one exact double product of two floats rounded once to a half.</summary>
    public static (ushort R, ushort G, ushort B)[] Emission(IReadOnlyList<SdfMaterial> materials) {
        var emission = new (ushort R, ushort G, ushort B)[materials.Count];

        for (var index = 0; (index < emission.Length); index++) {
            var material = materials[index];
            var strength = Math.Max(val1: 0.0, val2: ((double)material.Emissive));

            emission[index] = (
                BitConverter.HalfToUInt16Bits(value: ((Half)(material.Albedo.X * strength))),
                BitConverter.HalfToUInt16Bits(value: ((Half)(material.Albedo.Y * strength))),
                BitConverter.HalfToUInt16Bits(value: ((Half)(material.Albedo.Z * strength)))
            );
        }

        return emission;
    }
    /// <summary>Writes one emission texel in the usage's source format (<see cref="SdfBakeTextureUsage.Emission"/>):
    /// <paramref name="material"/>'s emitted light, or none for a material outside <paramref name="glow"/> (a miss
    /// passes -1), and an alpha of one.</summary>
    public static void WriteEmission(byte[] level, int texel, (ushort R, ushort G, ushort B)[] glow, int material) {
        (ushort R, ushort G, ushort B) light = ((((uint)material) < ((uint)glow.Length)) ? glow[material] : (Zero, Zero, Zero));
        var at = level.AsSpan(start: (texel * 8));

        BinaryPrimitives.WriteUInt16LittleEndian(destination: at, value: light.R);
        BinaryPrimitives.WriteUInt16LittleEndian(destination: at[2..], value: light.G);
        BinaryPrimitives.WriteUInt16LittleEndian(destination: at[4..], value: light.B);
        BinaryPrimitives.WriteUInt16LittleEndian(destination: at[6..], value: One);
    }

    private static Vector3 Vector(double[] values, int vertex) =>
        new(
            x: ((float)values[(vertex * 3)]),
            y: ((float)values[((vertex * 3) + 1)]),
            z: ((float)values[((vertex * 3) + 2)])
        );
    // Splits a quad along the diagonal whose two triangles both face the way the field's normals at its corners point;
    // when both diagonals do or neither does, along the shorter one, the first winning a tie. A quad dual contouring
    // folds across a thin feature faces out only when split across the fold. The tests are scalar double arithmetic over
    // the stored floats in a written order, never Vector3's cross or dot product, whose multiply-add may be fused and
    // whose lane sum follows the instruction set, so the split is the same on every machine.
    private static void WriteTriangles(uint[] indices, int quad, SdfBakedVertex[] vertices) {
        var first = ((uint)(quad * 4));
        var v0 = vertices[first].Position;
        var v1 = vertices[(first + 1)].Position;
        var v2 = vertices[(first + 2)].Position;
        var v3 = vertices[(first + 3)].Position;

        var (n0, n1, n2, n3) = (vertices[first].Normal, vertices[(first + 1)].Normal, vertices[(first + 2)].Normal, vertices[(first + 3)].Normal);
        var outward = (
            X: (((((double)n0.X) + n1.X) + n2.X) + n3.X),
            Y: (((((double)n0.Y) + n1.Y) + n2.Y) + n3.Y),
            Z: (((((double)n0.Z) + n1.Z) + n2.Z) + n3.Z)
        );
        var at = (quad * 6);

        var alongFirst = (Faces(a: v0, b: v1, c: v2, outward: outward) && Faces(a: v0, b: v2, c: v3, outward: outward));
        var alongSecond = (Faces(a: v0, b: v1, c: v3, outward: outward) && Faces(a: v1, b: v2, c: v3, outward: outward));

        if ((alongFirst == alongSecond)
            ? (DistanceSquared(a: v0, b: v2) <= DistanceSquared(a: v1, b: v3))
            : alongFirst) {
            indices[at] = first; indices[(at + 1)] = (first + 1); indices[(at + 2)] = (first + 2);
            indices[(at + 3)] = first; indices[(at + 4)] = (first + 2); indices[(at + 5)] = (first + 3);
        } else {
            indices[at] = first; indices[(at + 1)] = (first + 1); indices[(at + 2)] = (first + 3);
            indices[(at + 3)] = (first + 1); indices[(at + 4)] = (first + 2); indices[(at + 5)] = (first + 3);
        }
    }
    // Whether triangle (a, b, c), wound counter-clockwise, faces the way `outward` points: (b - a) x (c - a) . outward.
    private static bool Faces(Vector3 a, Vector3 b, Vector3 c, (double X, double Y, double Z) outward) {
        var ux = (((double)b.X) - a.X);
        var uy = (((double)b.Y) - a.Y);
        var uz = (((double)b.Z) - a.Z);
        var vx = (((double)c.X) - a.X);
        var vy = (((double)c.Y) - a.Y);
        var vz = (((double)c.Z) - a.Z);
        var nx = ((uy * vz) - (uz * vy));
        var ny = ((uz * vx) - (ux * vz));
        var nz = ((ux * vy) - (uy * vx));

        return ((((nx * outward.X) + (ny * outward.Y)) + (nz * outward.Z)) >= 0.0);
    }
    private static double DistanceSquared(Vector3 a, Vector3 b) {
        var dx = (((double)b.X) - a.X);
        var dy = (((double)b.Y) - a.Y);
        var dz = (((double)b.Z) - a.Z);

        return (((dx * dx) + (dy * dy)) + (dz * dz));
    }
    // Texel (s, t) of a tile is the quad's bilinear point at (s, t) / (Tile - 1) over corners 0, 1, 2, 3 at (0, 0),
    // (1, 0), (1, 1), (0, 1). A point on a quad edge is interpolated from the edge's lower-numbered vertex, so the two
    // quads sharing the edge compute the same point bit for bit and share one sample.
    private static Texel Sample(ReadOnlySpan<int> corner, SdfBakeField field, (byte R, byte G, byte B)[] palette, int s, int t, Dictionary<(int Low, int High, int Step), Texel> shared, SdfSurface surface) {
        var last = (Tile - 1);
        var onEdge = ((s == 0) || (s == last) || (t == 0) || (t == last));

        if (!onEdge) {
            var u = (s / ((double)last));
            var v = (t / ((double)last));
            var w0 = ((1.0 - u) * (1.0 - v));
            var w1 = (u * (1.0 - v));
            var w2 = (u * v);
            var w3 = ((1.0 - u) * v);
            var positions = surface.Positions;

            var (i0, i1, i2, i3) = ((corner[0] * 3), (corner[1] * 3), (corner[2] * 3), (corner[3] * 3));

            double Blend(int axis) =>
                (((w0 * positions[(i0 + axis)]) + (w1 * positions[(i1 + axis)])) + ((w2 * positions[(i2 + axis)]) + (w3 * positions[(i3 + axis)])));

            return Measure(field: field, palette: palette, surface: surface, x: Blend(axis: 0), y: Blend(axis: 1), z: Blend(axis: 2));
        }

        // Walk the edge the texel sits on: bottom 0 to 1, right 1 to 2, top 3 to 2, left 0 to 3.
        var (from, to, step) = ((t == 0)
            ? (corner[0], corner[1], s)
            : ((s == last)
                ? (corner[1], corner[2], t)
                : ((t == last)
                    ? (corner[3], corner[2], s)
                    : (corner[0], corner[3], t))));
        var low = Math.Min(val1: from, val2: to);
        var high = Math.Max(val1: from, val2: to);
        var fromLow = ((from == low) ? step : (last - step));
        var key = ((fromLow == 0)
            ? (low, low, 0)
            : ((fromLow == last)
                ? (high, high, 0)
                : (low, high, fromLow)));

        if (shared.TryGetValue(key: key, value: out var cached)) {
            return cached;
        }

        var fraction = (key.Item3 / ((double)last));
        var a = key.Item1;
        var b = key.Item2;
        var texel = Measure(
            field: field,
            palette: palette,
            surface: surface,
            x: (surface.Positions[(a * 3)] + (fraction * (surface.Positions[(b * 3)] - surface.Positions[(a * 3)]))),
            y: (surface.Positions[((a * 3) + 1)] + (fraction * (surface.Positions[((b * 3) + 1)] - surface.Positions[((a * 3) + 1)]))),
            z: (surface.Positions[((a * 3) + 2)] + (fraction * (surface.Positions[((b * 3) + 2)] - surface.Positions[((a * 3) + 2)])))
        );

        shared.Add(key: key, value: texel);

        return texel;
    }
    // One texel: the field's value and gradient at the mesh point, one step along the gradient onto the surface, the
    // winning material there, and ambient occlusion from probes along the normal. An unanswerable probe reads as open.
    private static Texel Measure(SdfBakeField field, (byte R, byte G, byte B)[] palette, SdfSurface surface, double x, double y, double z) {
        var point = Fixed(x: x, y: y, z: z);
        var epsilon = FixedQ4816.FromDouble(value: (surface.CellSize * 0.25));

        _ = field.TryDistance(distance: out var distance, material: out var material, point: point);

        if (!field.TryNormal(epsilon: epsilon, normal: out var gradient, point: point)) {
            gradient = new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero);
        }

        var nx = ((double)gradient.X);
        var ny = ((double)gradient.Y);
        var nz = ((double)gradient.Z);
        var d = ((double)distance);
        var sx = (x - (d * nx));
        var sy = (y - (d * ny));
        var sz = (z - (d * nz));
        var occluded = 0.0;
        var weights = 0.0;

        for (var probe = 1; (probe <= OcclusionProbes); probe++) {
            var reach = (probe * surface.CellSize);
            var weight = (1.0 / (1 << probe));

            weights += weight;

            if (field.TryDistance(
                distance: out var open,
                material: out _,
                point: Fixed(x: (sx + (reach * nx)), y: (sy + (reach * ny)), z: (sz + (reach * nz)))
            )) {
                occluded += (weight * Math.Clamp(max: 1.0, min: 0.0, value: ((reach - ((double)open)) / reach)));
            }
        }

        var color = ((((uint)material) < ((uint)palette.Length)) ? palette[material] : (((byte)0), ((byte)0), ((byte)0)));

        var (u, v) = OctahedralNormal.Encode(x: nx, y: ny, z: nz);

        return new Texel(
            B: color.Item3,
            G: color.Item2,
            Material: ((byte)Math.Clamp(max: 255, min: 0, value: material)),
            NormalU: u,
            NormalV: v,
            Occlusion: ImageSourceConversion.ToUnorm8(value: (1.0 - (occluded / weights))),
            R: color.Item1
        );
    }

    /// <summary>Returns a world-space point in fixed point, each component rounded to nearest.</summary>
    public static FixedVector3 Fixed(double x, double y, double z) =>
        new(
            X: FixedQ4816.FromDouble(value: x),
            Y: FixedQ4816.FromDouble(value: y),
            Z: FixedQ4816.FromDouble(value: z)
        );
}
