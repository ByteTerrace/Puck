using System.Numerics;
using Puck.Abstractions.Sources;
using Puck.Assets.Textures;
using Puck.Maths;

namespace Puck.SignedDistance.Baking;

/// <summary>
/// An octahedral impostor: <see cref="Views"/> by <see cref="Views"/> orthographic views of a prototype, each
/// <see cref="ViewTexels"/> texels square, laid out in one atlas. View <c>(i, j)</c> looks at the bounding sphere's
/// center from the direction the octahedral map decodes at <c>((i + 0.5) / Views, (j + 0.5) / Views)</c>, with +Y as
/// the octahedron's pole, so the views cover the whole sphere of directions and a renderer blends the three nearest to
/// a camera. A view spans the sphere's diameter; its right axis is the world up crossed with the view direction (world
/// +Z for a view straight along Y), and its rows run top to bottom. Each view is one tile of the atlas's mip chains, so
/// the chains end where a view is one texel, and the normal, depth and emission mips are weighted by the albedo's
/// coverage, so the empty texels around a silhouette never bend its normals, pull its depth or dim its glow.
/// </summary>
/// <param name="Center">The bounding sphere's center, in the prototype's engine frame.</param>
/// <param name="Radius">The bounding sphere's radius, in world units.</param>
/// <param name="Views">The views along each side of the view grid.</param>
/// <param name="ViewTexels">The texels along each side of one view.</param>
/// <param name="Albedo">The sRGB albedo, with coverage in alpha: 255 where a view's ray hit the surface, 0 elsewhere.</param>
/// <param name="Normal">The outward normal in the prototype's frame, as an octahedral pair; a miss holds the direction
/// toward the view's camera.</param>
/// <param name="Depth">The hit's depth: 0 at the sphere's near side, 255 at its far side and where the ray
/// missed.</param>
/// <param name="Emission">The light the hit's material emits, in linear light as the surface textures hold it: zero where
/// the ray missed or the material emits none.</param>
public sealed record SdfBakedImpostor(Vector3 Center, float Radius, int Views, int ViewTexels, SdfBakedTexture Albedo, SdfBakedTexture Normal, SdfBakedTexture Depth, SdfBakedTexture Emission) {
    /// <summary>Returns the unit direction from the center toward the camera of view <c>(i, j)</c>.</summary>
    /// <param name="i">The view's column.</param>
    /// <param name="j">The view's row.</param>
    /// <param name="views">The views along each side of the grid.</param>
    /// <returns>The view direction.</returns>
    public static Vector3 ViewDirection(int i, int j, int views) {
        var (x, y, z) = Direction(i: i, j: j, views: views);

        return new Vector3(
            x: ((float)x),
            y: ((float)y),
            z: ((float)z)
        );
    }

    // The unit view direction in doubles. Every step is one IEEE operation in a written order (Math.Sqrt is correctly
    // rounded), so the direction, and the view basis built from it, are the same bits on every machine.
    private static (double X, double Y, double Z) Direction(int i, int j, int views) {
        var x = ((((i + 0.5) / views) * 2.0) - 1.0);
        var z = ((((j + 0.5) / views) * 2.0) - 1.0);
        var y = ((1.0 - Math.Abs(value: x)) - Math.Abs(value: z));

        if (y < 0.0) {
            var folded = ((1.0 - Math.Abs(value: z)) * Math.CopySign(x: 1.0, y: x));

            z = ((1.0 - Math.Abs(value: x)) * Math.CopySign(x: 1.0, y: z));
            x = folded;
        }

        var length = Math.Sqrt(d: (((x * x) + (y * y)) + (z * z)));

        return ((x / length), (y / length), (z / length));
    }
    // a x b, written out rather than through Vector3.Cross, which may fuse its multiply-add.
    private static (double X, double Y, double Z) Cross((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        (((a.Y * b.Z) - (a.Z * b.Y)), ((a.Z * b.X) - (a.X * b.Z)), ((a.X * b.Y) - (a.Y * b.X)));

    /// <summary>Bakes an impostor by marching one ray per texel through the field and reading each hit's material and
    /// normal.</summary>
    internal static SdfBakedImpostor Bake(SdfBakeField field, Vector3 center, float radius, int views, int viewTexels, (byte R, byte G, byte B)[] palette, (ushort R, ushort G, ushort B)[] glow) {
        var side = (views * viewTexels);
        var albedo = new byte[((side * side) * 4)];
        var normals = new byte[((side * side) * 2)];
        var depth = new byte[(side * side)];
        var emission = new byte[((side * side) * 8)];
        var r = ((double)radius);
        var epsilon = FixedQ4816.FromDouble(value: ((r * 2.0) / (viewTexels * 4)));
        var travel = FixedQ4816.FromDouble(value: (r * 4.0));

        for (var j = 0; (j < views); j++) {
            for (var i = 0; (i < views); i++) {
                var direction = Direction(i: i, j: j, views: views);
                var reference = ((Math.Abs(value: direction.Y) > 0.999) ? (0.0, 0.0, 1.0) : (0.0, 1.0, 0.0));
                var perpendicular = Cross(a: reference, b: direction);
                var span = Math.Sqrt(d: (((perpendicular.X * perpendicular.X) + (perpendicular.Y * perpendicular.Y)) + (perpendicular.Z * perpendicular.Z)));
                var right = (X: (perpendicular.X / span), Y: (perpendicular.Y / span), Z: (perpendicular.Z / span));
                var up = Cross(a: direction, b: right);
                var toward = SdfSurfaceTextures.Fixed(x: -direction.X, y: -direction.Y, z: -direction.Z);

                var (facingU, facingV) = OctahedralNormal.Encode(x: direction.X, y: direction.Y, z: direction.Z);

                for (var b = 0; (b < viewTexels); b++) {
                    for (var a = 0; (a < viewTexels); a++) {
                        var across = (((((a + 0.5) / viewTexels) * 2.0) - 1.0) * r);
                        var down = ((1.0 - (((b + 0.5) / viewTexels) * 2.0)) * r);
                        var origin = SdfSurfaceTextures.Fixed(
                            x: ((center.X + ((direction.X * r) * 2.0)) + ((right.X * across) + (up.X * down))),
                            y: ((center.Y + ((direction.Y * r) * 2.0)) + ((right.Y * across) + (up.Y * down))),
                            z: ((center.Z + ((direction.Z * r) * 2.0)) + ((right.Z * across) + (up.Z * down)))
                        );
                        var texel = (((((j * viewTexels) + b) * side) + (i * viewTexels)) + a);
                        var at = (texel * 4);

                        normals[(texel * 2)] = facingU;
                        normals[((texel * 2) + 1)] = facingV;
                        depth[texel] = 255;
                        SdfSurfaceTextures.WriteEmission(glow: glow, level: emission, material: -1, texel: texel);

                        if (
                            !field.TryRay(direction: toward, hit: out var hit, maxDistance: travel, origin: origin) ||
                            !hit.Point.TryDelta(delta: out var point, origin: FixedPosition.Zero)
                        ) {
                            continue;
                        }

                        if (!field.TryNormal(epsilon: epsilon, normal: out var normal, point: point)) {
                            normal = SdfSurfaceTextures.Fixed(x: direction.X, y: direction.Y, z: direction.Z);
                        }

                        var color = ((((uint)hit.Material) < ((uint)palette.Length)) ? palette[hit.Material] : (((byte)0), ((byte)0), ((byte)0)));

                        albedo[at] = color.Item1;
                        albedo[(at + 1)] = color.Item2;
                        albedo[(at + 2)] = color.Item3;
                        albedo[(at + 3)] = 255;
                        (normals[(texel * 2)], normals[((texel * 2) + 1)]) = OctahedralNormal.Encode(x: ((double)normal.X), y: ((double)normal.Y), z: ((double)normal.Z));
                        depth[texel] = ImageSourceConversion.ToUnorm8(value: ((((double)hit.Distance) - r) / (2.0 * r)));
                        SdfSurfaceTextures.WriteEmission(glow: glow, level: emission, material: hit.Material, texel: texel);
                    }
                }
            }
        }

        var albedoChain = SdfBakedTexture.Chain(height: side, level0: albedo, tileTexels: viewTexels, usage: SdfBakeTextureUsage.Albedo, width: side);
        var coverage = TextureMipChain.Channel(channel: 3, channels: 4, levels: albedoChain);

        return new SdfBakedImpostor(
            Albedo: SdfBakedTexture.Compress(chain: albedoChain, height: side, tileTexels: viewTexels, usage: SdfBakeTextureUsage.Albedo, width: side),
            Center: center,
            Depth: SdfBakedTexture.Store(coverage: coverage, height: side, level0: depth, tileTexels: viewTexels, usage: SdfBakeTextureUsage.Depth, width: side),
            Emission: SdfBakedTexture.Store(coverage: coverage, height: side, level0: emission, tileTexels: viewTexels, usage: SdfBakeTextureUsage.Emission, width: side),
            Normal: SdfBakedTexture.Store(coverage: coverage, height: side, level0: normals, tileTexels: viewTexels, usage: SdfBakeTextureUsage.Normal, width: side),
            Radius: radius,
            Views: views,
            ViewTexels: viewTexels
        );
    }
}
