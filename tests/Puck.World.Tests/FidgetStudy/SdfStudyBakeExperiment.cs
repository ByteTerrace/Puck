using System.Globalization;
using System.Numerics;
using System.Text;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.SignedDistance.Baking;
using Puck.SignedDistance.Queries;
using Puck.World.Authoring;
using Xunit;

namespace Puck.World.Tests.FidgetStudy;

/// <summary>
/// Measures two baker questions against prior art. Manifold dual contouring (Fidget's mesher splits a cell's vertex
/// per connected corner component): a thin plate's mesh is checked for edges shared by more than two triangles, which
/// one vertex per cell produces when a plate's two faces cross the same cell. Interval-culled octrees: the lattice's
/// sign resolution today proves flat four-cell blocks by their Lipschitz centers; a hierarchical octree is counted
/// against it over the studied worlds' prototypes. Run on request with <c>-- xUnit.Explicit=on</c>.
/// </summary>
public sealed class SdfStudyBakeExperiment {
    /// <summary>
    /// THE LAW (study): one vertex per cell meshes a plate at least two cells thick as a closed two-manifold, and
    /// does not once the plate is about a cell thick, where both faces cross the same cells.
    /// </summary>
    [Fact]
    public void OneVertexPerCellPinchesAPlateAboutACellThick() {
        var thick = Plate(thicknessCells: 3.0);
        var thin = Plate(thicknessCells: 1.0);

        Assert.Equal(actual: thick.NonManifold, expected: 0);
        Assert.Equal(actual: thick.Boundary, expected: 0);
        Assert.True(condition: (thin.NonManifold > 0), userMessage: $"a cell-thick plate meshed with {thin.NonManifold} non-manifold edges");
    }
    [Fact(Explicit = true)]
    public void ThinFeaturesAndOctreeCulling() {
        var report = new StringBuilder();

        report.AppendLine(value: "thin plate (Standard tier, 32 cells): thickness in cells -> triangles, edges shared by >2 triangles, boundary edges, vertices");
        foreach (var cells in ((double[])[0.4, 0.7, 1.0, 1.3, 1.6, 2.0, 3.0])) {
            var plate = Plate(thicknessCells: cells);

            report.AppendLine(value: $"  {cells.ToString(provider: CultureInfo.InvariantCulture),4}: {plate.Triangles} triangles, {plate.NonManifold} non-manifold, {plate.Boundary} boundary, {plate.Vertices} vertices");
        }
        report.AppendLine();
        report.AppendLine(value: "sign resolution per prototype (Standard tier): mesh evaluations of the real bake; flat 4-cell blocks (today's scheme, replicated) vs an octree from the whole lattice down to single cells");

        var flatTotal = 0L;
        var octreeTotal = 0L;
        var meshTotal = 0L;
        var bakeTotal = 0L;

        foreach (var (name, path) in SdfStudyScene.Worlds) {
            var definition = AuthoredGameFixtures.Load(relativePath: path);

            foreach (var prototype in definition.Creations) {
                if (!TryProgram(document: prototype.Document, program: out var program, reach: out var reach)) {
                    continue;
                }

                SdfBake bake;

                try {
                    bake = SdfBaker.Bake(center: Vector3.Zero, materials: [new SdfMaterial(Albedo: Vector3.One)], program: program, reach: reach, tier: SdfBakeTier.For(quality: SdfBakeQuality.Standard));
                } catch (ArgumentException) {
                    continue;
                }

                var field = new SdfBakeField(evaluator: new SdfFieldEvaluator(program: program));
                var flat = Flat(block: 4, cells: 32, field: field, reach: reach);
                var octree = Octree(field: new SdfBakeField(evaluator: new SdfFieldEvaluator(program: program)), reach: reach, cells: 32);

                flatTotal += flat;
                octreeTotal += octree;
                meshTotal += bake.Work.MeshEvaluations;
                bakeTotal += bake.Work.FieldEvaluations;
                report.AppendLine(value: $"  {name}/{prototype.Id}: mesh {bake.Work.MeshEvaluations}, textures {bake.Work.TextureEvaluations}, impostor {bake.Work.ImpostorEvaluations}; signs flat {flat}, octree {octree}");
            }
        }
        report.AppendLine(value: $"  total: mesh {meshTotal}, whole bake {bakeTotal}, signs flat {flatTotal}, octree {octreeTotal}");
        SdfStudyReport.Write(name: "bake.txt", text: report.ToString());
    }

    private static (int Triangles, int NonManifold, int Boundary, int Vertices) Plate(double thicknessCells) {
        // A square plate tilted off the lattice axes; the lattice's cell is 2 * reach / 30.
        const float Reach = 1.5f;
        var cell = ((2.0 * Reach) / 30.0);
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        builder.Rotate(rotation: Quaternion.CreateFromYawPitchRoll(pitch: 0.2f, roll: 0.1f, yaw: 0.3f));
        builder.Box(halfExtents: new Vector3(x: 1f, y: ((float)((0.5 * thicknessCells) * cell)), z: 1f), round: 0f, material: material);

        var bake = SdfBaker.Bake(center: Vector3.Zero, materials: [new SdfMaterial(Albedo: Vector3.One)], program: builder.Build(buildInstanceGrid: false), reach: Reach, tier: SdfBakeTier.For(quality: SdfBakeQuality.Standard));
        var mesh = bake.Mesh;
        // Baked quads carry their own four vertices, so edges are keyed by quantized position.
        var edges = new Dictionary<(long, long, long, long, long, long), int>();

        for (var index = 0; (index < mesh.Indices.Length); index += 3) {
            for (var side = 0; (side < 3); side++) {
                var a = Key(p: mesh.Vertices[mesh.Indices[(index + side)]].Position);
                var b = Key(p: mesh.Vertices[mesh.Indices[(index + ((side + 1) % 3))]].Position);
                var edge = ((a.CompareTo(other: b) < 0) ? (a.Item1, a.Item2, a.Item3, b.Item1, b.Item2, b.Item3) : (b.Item1, b.Item2, b.Item3, a.Item1, a.Item2, a.Item3));

                edges[edge] = (edges.GetValueOrDefault(key: edge) + 1);
            }
        }

        var positions = mesh.Vertices.Select(selector: vertex => Key(p: vertex.Position)).Distinct().Count();

        return (mesh.Triangles, edges.Values.Count(predicate: count => (count > 2)), edges.Values.Count(predicate: count => (count == 1)), positions);

        static (long, long, long) Key(Vector3 p) => (((long)Math.Round(a: (p.X * 1e5))), ((long)Math.Round(a: (p.Y * 1e5))), ((long)Math.Round(a: (p.Z * 1e5))));
    }
    private static bool TryProgram(CreationDocument document, out SdfProgram program, out float reach) {
        var engine = CreationFrame.ToEngine(document: document);
        var builder = new SdfProgramBuilder();

        _ = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        try {
            CreationStampEmitter.EmitFixed(
                builder: builder,
                document: engine,
                materialFor: _ => 0,
                transform: new FixedCreationStampTransform(Origin: FixedVector3.Zero, ReflectionNormal: null, Rotation: FixedQuaternion.Identity, Scale: FixedQ4816.One)
            );
            program = builder.Build(buildInstanceGrid: false);
            reach = CreationStampEmitter.RenderReach(document: engine, fontFor: null, scale: 1f);

            return (float.IsFinite(f: reach) && (reach > 0f));
        } catch (Exception exception) when ((exception is ArgumentException or SdfProgramCapacityException or InvalidOperationException)) {
            program = null!;
            reach = 0f;

            return false;
        }
    }
    // Today's scheme (SdfBakeGrid.ResolveSigns): one center per block; an unproven block evaluates its corners.
    private static long Flat(SdfBakeField field, float reach, int cells, int block) {
        var lattice = new Lattice(cells: cells, field: field, reach: reach);
        var blocks = ((cells + (block - 1)) / block);

        for (var bz = 0; (bz < blocks); bz++) {
            for (var by = 0; (by < blocks); by++) {
                for (var bx = 0; (bx < blocks); bx++) {
                    lattice.Resolve(recurse: false, size: block, x0: (bx * block), y0: (by * block), z0: (bz * block));
                }
            }
        }

        return field.Evaluations;
    }
    private static long Octree(SdfBakeField field, float reach, int cells) {
        new Lattice(cells: cells, field: field, reach: reach).Resolve(recurse: true, size: cells, x0: 0, y0: 0, z0: 0);

        return field.Evaluations;
    }

    private sealed class Lattice(SdfBakeField field, float reach, int cells) {
        private readonly double m_cell = ((2.0 * reach) / (cells - 2));
        private readonly double m_origin = -((cells * ((2.0 * reach) / (cells - 2))) / 2.0);
        private readonly HashSet<int> m_known = [];
        private readonly double m_stepScale = ((double)field.StepScale);

        public void Resolve(int x0, int y0, int z0, int size, bool recurse) {
            var x1 = Math.Min(val1: cells, val2: (x0 + size));
            var y1 = Math.Min(val1: cells, val2: (y0 + size));
            var z1 = Math.Min(val1: cells, val2: (z0 + size));

            if ((x0 >= x1) || (y0 >= y1) || (z0 >= z1)) {
                return;
            }

            var distance = Distance(x: (0.5 * (x0 + x1)), y: (0.5 * (y0 + y1)), z: (0.5 * (z0 + z1)));
            var halfDiagonal = ((Math.Sqrt(d: 3.0) * (0.5 * size)) * m_cell);

            if ((Math.Abs(value: distance) * m_stepScale) > halfDiagonal) {
                // A proven block's corners take its center's sign unevaluated, as SdfBakeGrid marks them known.
                for (var z = z0; (z <= z1); z++) {
                    for (var y = y0; (y <= y1); y++) {
                        for (var x = x0; (x <= x1); x++) {
                            _ = m_known.Add(item: (x + ((cells + 1) * (y + ((cells + 1) * z)))));
                        }
                    }
                }

                return;
            }
            if (recurse && (size > 1)) {
                var half = (size / 2);

                for (var child = 0; (child < 8); child++) {
                    Resolve(recurse: true, size: half, x0: (x0 + (((child & 1) != 0) ? half : 0)), y0: (y0 + (((child & 2) != 0) ? half : 0)), z0: (z0 + (((child & 4) != 0) ? half : 0)));
                }

                return;
            }
            for (var z = z0; (z <= z1); z++) {
                for (var y = y0; (y <= y1); y++) {
                    for (var x = x0; (x <= x1); x++) {
                        if (m_known.Add(item: (x + ((cells + 1) * (y + ((cells + 1) * z)))))) {
                            _ = Distance(x: x, y: y, z: z);
                        }
                    }
                }
            }
        }

        private double Distance(double x, double y, double z) {
            _ = field.TryDistance(
                distance: out var distance,
                material: out _,
                point: new FixedVector3(
                    X: FixedQ4816.FromDouble(value: (m_origin + (x * m_cell))),
                    Y: FixedQ4816.FromDouble(value: (m_origin + (y * m_cell))),
                    Z: FixedQ4816.FromDouble(value: (m_origin + (z * m_cell)))
                )
            );

            return ((double)distance);
        }
    }
}
