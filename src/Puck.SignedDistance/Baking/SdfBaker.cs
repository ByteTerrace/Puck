using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance.Queries;

namespace Puck.SignedDistance.Baking;

/// <summary>The deterministic work one bake did, counted rather than timed. An evaluation is one run of the program at
/// one point: a distance, one probe of a gradient, or one sample of a march.</summary>
/// <param name="MeshEvaluations">The evaluations the mesh's extraction made: lattice corners, culling-block centers,
/// and vertex normals.</param>
/// <param name="TextureEvaluations">The evaluations the surface textures made.</param>
/// <param name="ImpostorEvaluations">The evaluations the impostor made.</param>
/// <param name="Rays">The rays marched for the impostor.</param>
/// <param name="Vertices">The mesh's vertices.</param>
/// <param name="Triangles">The mesh's triangles.</param>
public readonly record struct SdfBakeWork(long MeshEvaluations, long TextureEvaluations, long ImpostorEvaluations, long Rays, int Vertices, int Triangles) {
    /// <summary>Gets every evaluation the bake made.</summary>
    public long FieldEvaluations => ((MeshEvaluations + TextureEvaluations) + ImpostorEvaluations);
}
/// <summary>One prototype's presentation assets: its mesh, the surface textures its quads tile, and its impostor.</summary>
/// <param name="Mesh">The indexed mesh.</param>
/// <param name="Textures">The surface textures, in <see cref="SdfBakeTextureUsage"/> order: albedo, normal, occlusion,
/// material, emission. Each is a tile-aware mip chain in its usage's stored format
/// (<see cref="SdfBakedTexture.PlanFor"/>).</param>
/// <param name="Impostor">The octahedral impostor.</param>
/// <param name="Work">The work the bake did.</param>
public sealed record SdfBake(SdfBakedMesh Mesh, IReadOnlyList<SdfBakedTexture> Textures, SdfBakedImpostor Impostor, SdfBakeWork Work);
/// <summary>
/// Bakes a signed-distance program into presentation assets, reading the field only through
/// <see cref="SdfFieldEvaluator"/>, the fixed-point interpreter contact and queries read. The mesh is extracted by dual
/// contouring over a lattice around the program's reach; the surface textures and the impostor sample the same
/// field.
/// <para>A bake is the same bytes on every machine. Everything it reads from the field is fixed point, and every float
/// it writes is rounded from scalar IEEE arithmetic (addition, subtraction, multiplication, division and square root,
/// each correctly rounded) in a written order: no transcendental function, no <see cref="Vector3"/> operation whose
/// multiply-add may be fused or whose lanes sum in an order the instruction set picks, and an sRGB encode that compares
/// against exact thresholds (<c>ImageSourceConversion.LinearToSrgb8</c>). Its textures' mip chains and block
/// compression (<c>Puck.Assets.Textures</c>) hold to the same rules. The field it reads is the program's, so the
/// bake is exactly as portable as the words of that program. A bake is presentation only: nothing in it reaches
/// simulation state.</para>
/// </summary>
public static class SdfBaker {
    /// <summary>The baker's version. It moves whenever a change to the baker changes the bytes a bake produces, and it is
    /// part of every bake's key and the version of the compiled-world chunk that names bakes.</summary>
    public const uint Version = 3;

    /// <summary>Bakes <paramref name="program"/> at <paramref name="tier"/>.</summary>
    /// <param name="program">The program.</param>
    /// <param name="materials">The materials the program's material ids index, in id order; each id bakes to its
    /// material's albedo.</param>
    /// <param name="center">The center of a sphere holding every surface of the program, in world units.</param>
    /// <param name="reach">The sphere's radius, in world units.</param>
    /// <param name="tier">The resolution.</param>
    /// <returns>The bake.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="program"/> or <paramref name="materials"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The fixed-point interpreter refuses an instruction of the program, the
    /// program has no shape that reaches the field, or <paramref name="reach"/> is not a positive finite value.</exception>
    public static SdfBake Bake(SdfProgram program, IReadOnlyList<SdfMaterial> materials, Vector3 center, float reach, SdfBakeTier tier) {
        ArgumentNullException.ThrowIfNull(argument: program);
        ArgumentNullException.ThrowIfNull(argument: materials);

        if (!float.IsFinite(f: reach) || (reach <= 0f)) {
            throw new ArgumentException(message: "A bake's reach must be a positive finite value.", paramName: nameof(reach));
        }

        var evaluator = new SdfFieldEvaluator(program: program);

        if (!evaluator.HasShape) {
            throw new ArgumentException(message: "The program has no shape that reaches the field, so it has no surface to bake.", paramName: nameof(program));
        }

        var field = new SdfBakeField(evaluator: evaluator);
        var grid = SdfBakeGrid.Create(
            cells: tier.Cells,
            center: SdfSurfaceTextures.Fixed(x: center.X, y: center.Y, z: center.Z),
            field: field,
            reach: FixedQ4816.FromDouble(value: reach)
        );
        var surface = SdfDualContouring.Extract(field: field, grid: grid);
        var meshEvaluations = field.Evaluations;

        var (mesh, textures) = SdfSurfaceTextures.Bake(field: field, materials: materials, surface: surface);
        var textureEvaluations = (field.Evaluations - meshEvaluations);
        var impostor = SdfBakedImpostor.Bake(
            center: center,
            field: field,
            palette: SdfSurfaceTextures.Palette(materials: materials),
            radius: reach,
            views: tier.ImpostorViews,
            viewTexels: tier.ImpostorTexels
        );

        return new SdfBake(
            Impostor: impostor,
            Mesh: mesh,
            Textures: textures,
            Work: new SdfBakeWork(
                ImpostorEvaluations: ((field.Evaluations - meshEvaluations) - textureEvaluations),
                MeshEvaluations: meshEvaluations,
                Rays: field.Rays,
                TextureEvaluations: textureEvaluations,
                Triangles: mesh.Triangles,
                Vertices: mesh.Vertices.Length
            )
        );
    }
    /// <summary>Measures how far a baked mesh strays from the field it was baked from: the largest absolute field value
    /// at its vertices, its edge midpoints, and its triangle centroids, in world units. For an exact signed distance
    /// the value is the distance to the surface.</summary>
    /// <param name="program">The program the mesh was baked from.</param>
    /// <param name="mesh">The mesh.</param>
    /// <returns>The largest absolute field value over the sampled points, and the evaluations the measure made.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="program"/> or <paramref name="mesh"/> is
    /// <see langword="null"/>.</exception>
    public static (double Error, long Evaluations) MeasureError(SdfProgram program, SdfBakedMesh mesh) {
        ArgumentNullException.ThrowIfNull(argument: program);
        ArgumentNullException.ThrowIfNull(argument: mesh);

        var field = new SdfBakeField(evaluator: new SdfFieldEvaluator(program: program));
        var error = 0.0;

        double At(Vector3 point) {
            _ = field.TryDistance(
                distance: out var distance,
                material: out _,
                point: SdfSurfaceTextures.Fixed(x: point.X, y: point.Y, z: point.Z)
            );

            return Math.Abs(value: ((double)distance));
        }

        for (var index = 0; (index < mesh.Indices.Length); index += 3) {
            var a = mesh.Vertices[mesh.Indices[index]].Position;
            var b = mesh.Vertices[mesh.Indices[(index + 1)]].Position;
            var c = mesh.Vertices[mesh.Indices[(index + 2)]].Position;

            error = Math.Max(val1: error, val2: At(point: a));
            error = Math.Max(val1: error, val2: At(point: ((a + b) * 0.5f)));
            error = Math.Max(val1: error, val2: At(point: (((a + b) + c) / 3f)));
        }

        return (error, field.Evaluations);
    }
}
