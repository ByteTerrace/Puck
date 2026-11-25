using System.Numerics;
using System.Runtime.CompilerServices;
using Puck.Maths;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>
/// What a world shows of itself to a far viewer: its settled static scene, each mesh at the coarsest level it carries,
/// placed in the world and gathered into the product's own cells (<see cref="SdfMeshCluster"/>), so its triangles are
/// bounded by the surface's area over the cell squared, then chunked and levelled (<see cref="SdfMeshChunks.Bake"/>),
/// with the materials its triangles shade with. A view that shows a world from far
/// away draws this product instead of a residency of the whole world, so it bakes nothing, holds none of the world's
/// bodies, creations or screens, and holds only the product's triangles.
/// <para>It is a derived product, a cache: built from what a residency of the world already drew
/// (<see cref="Take"/> on the frame thread, <see cref="Build"/> on a worker), named by a digest of exactly those inputs
/// (<see cref="Key"/>), never recorded, and built again when its inputs change. Dynamic content (a moving body, a
/// skinned or displaced mesh, glass) is absent: a far viewer sees the world's ground and standing masses, never its
/// motion.</para>
/// <para>Invariant: every triangle of the product reads a material the product carries. Enforced in <see cref="Build"/>,
/// the one door that makes a product: each source triangle's program material is renumbered into
/// <see cref="Materials"/>, and a triangle whose material the source program does not hold (a screen's) is left out.</para>
/// </summary>
public sealed class SdfFarProduct {
    /// <summary>The product's cell when its options state none, in metres: the side of the cells its vertices are
    /// gathered into, and the cell its levels are measured against (<see cref="SdfMesh.BakeCell"/>).</summary>
    public const float DefaultCell = 1f;

    private SdfFarProduct(SdfMesh mesh, IReadOnlyList<SdfMaterial> materials, ulong key, int sourceDraws, long sourceTriangles) {
        Mesh = mesh;
        Materials = materials;
        Key = key;
        SourceDraws = sourceDraws;
        SourceTriangles = sourceTriangles;
    }

    /// <summary>Gets the product's mesh, in the world's coordinates, its pieces each drawn by one draw
    /// (<see cref="DrawsAt"/>).</summary>
    public SdfMesh Mesh { get; }
    /// <summary>Gets the materials the mesh's triangles name, in the order its triangle materials count them.</summary>
    public IReadOnlyList<SdfMaterial> Materials { get; }
    /// <summary>Gets the digest of the inputs the product was built from: each static draw's mesh, piece, placement and
    /// material, and the options. Equal keys built equal products within one process.</summary>
    public ulong Key { get; }
    /// <summary>Gets how many static draws the product was built from.</summary>
    public int SourceDraws { get; }
    /// <summary>Gets how many triangles the source draws' coarsest levels held before the product's own merge.</summary>
    public long SourceTriangles { get; }
    /// <summary>Gets the product's triangles at its finest level.</summary>
    public int Triangles => Mesh.TriangleCount;
    /// <summary>Gets the bytes the product's mesh holds.</summary>
    public long Bytes => Mesh.StorageBytes;

    /// <summary>Returns the draws that draw the product: one per piece of its mesh, static, in the world's coordinates,
    /// shading with the materials from <paramref name="materialBase"/> on.</summary>
    /// <param name="materialBase">The index of the product's first material in the program that draws it.</param>
    /// <param name="identity">The draws' picking identity.</param>
    /// <returns>The draws.</returns>
    public SdfMeshDraw[] DrawsAt(int materialBase, object identity) {
        ArgumentNullException.ThrowIfNull(argument: identity);

        var draws = new SdfMeshDraw[Mesh.PieceCount];

        for (var piece = 0; (piece < draws.Length); piece++) {
            draws[piece] = new SdfMeshDraw(identity: identity, material: materialBase, mesh: Mesh, objectToWorld: Matrix4x4.Identity) { Piece = piece };
        }
        return draws;
    }
    /// <summary>Describes the product as <c>world.residency</c> prints it.</summary>
    /// <returns>One line.</returns>
    public override string ToString() => $"far-product key={Key:x16} source-draws={SourceDraws} source-triangles={SourceTriangles} triangles={Triangles} pieces={Mesh.PieceCount} materials={Materials.Count} bytes={Bytes}";

    /// <summary>Takes what a product is built from out of a residency's frame, on the thread that owns the frame: its
    /// static scene's draws (<see cref="SdfMeshDraw.InStaticScene"/>) with a claim on each mesh they name, so the meshes
    /// outlive the frame's next capture while a worker reads them, and the program's materials.</summary>
    /// <param name="frame">The residency's dressed frame (<see cref="SdfWorldResidency.Frame"/>).</param>
    /// <param name="options">What the product keeps.</param>
    /// <returns>The inputs, which <see cref="Build"/> consumes and releases; <see langword="null"/> when the frame
    /// holds no static draw.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    public static Inputs? Take(SdfFrame frame, SdfFarProductOptions options) {
        ArgumentNullException.ThrowIfNull(argument: frame);
        ArgumentNullException.ThrowIfNull(argument: options);

        var rows = frame.DrawRows;
        var draws = new List<SdfMeshDraw>();

        for (var row = 0; (row < rows.Count); row++) {
            var draw = rows[row];

            if (draw.InStaticScene) { draws.Add(item: draw); }
        }
        if (draws.Count == 0) { return null; }
        return new Inputs(draws: draws, materials: frame.Program.Appearance.Materials, options: options);
    }
    /// <summary>Builds the product from its inputs, on a worker: each draw's coarsest level placed in the world, its
    /// triangles' materials renumbered into the product's own, gathered into the product's cells and chunked and levelled at the
    /// product's cell. Releases the inputs' claims whatever happens.</summary>
    /// <param name="inputs">What <see cref="Take"/> took.</param>
    /// <returns>The product, or <see langword="null"/> when no triangle survives.</returns>
    public static SdfFarProduct? Build(Inputs inputs) {
        ArgumentNullException.ThrowIfNull(argument: inputs);

        using (inputs) {
            var options = inputs.Options;
            var sourceMaterials = inputs.Materials;
            var cluster = new SdfMeshCluster(cell: options.Cell);
            var renumbered = new Dictionary<int, uint>();
            var kept = new List<SdfMaterial>();
            var remap = Array.Empty<int>();
            var digest = MultisetDigest.Empty.Add(element: BitConverter.SingleToUInt32Bits(value: options.Cell)).Add(element: BitConverter.SingleToUInt32Bits(value: options.MinimumSize));
            var drawn = 0;
            var sourceTriangles = 0L;

            foreach (var draw in inputs.Draws) {
                var placement = draw.FixedPlacement;

                digest = digest.Add(element: DrawElement(draw: in draw, placement: in placement));

                var piece = draw.Mesh.Piece(index: draw.Piece);

                using var held = piece.Hold();

                if (!held.Readable) { continue; }

                var level = piece.Levels.Count;
                var pieceIndices = held.LevelIndices(level: level).Span;
                var pieceMaterials = held.LevelMaterials(level: level).Span;
                var piecePositions = held.Positions.Span;
                var pieceNormals = held.Normals.Span;

                if (pieceIndices.IsEmpty) { continue; }
                if (!SpansAtLeast(indices: pieceIndices, placement: in placement, positions: piecePositions, size: options.MinimumSize)) { continue; }

                drawn++;
                sourceTriangles += (pieceIndices.Length / 3);
                if (remap.Length < piecePositions.Length) { remap = new int[piecePositions.Length]; }
                remap.AsSpan(start: 0, length: piecePositions.Length).Fill(value: -1);

                for (var corner = 0; (corner < pieceIndices.Length); corner += 3) {
                    var program = (draw.Material + (pieceMaterials.IsEmpty ? 0 : ((int)pieceMaterials[(corner / 3)])));

                    // A triangle whose material the program does not hold (a screen's face) has nothing to shade with here.
                    if ((program < 0) || (program >= sourceMaterials.Count)) { continue; }
                    if (!renumbered.TryGetValue(key: program, value: out var material)) {
                        material = ((uint)kept.Count);
                        renumbered.Add(key: program, value: material);
                        kept.Add(item: sourceMaterials[program]);
                    }

                    int a, b, c;

                    if (pieceNormals.IsEmpty) {
                        // A mesh without normals (an authored sheet) takes its face's normal at each corner.
                        var pa = Vector3.Transform(position: piecePositions[((int)pieceIndices[corner])], matrix: placement);
                        var pb = Vector3.Transform(position: piecePositions[((int)pieceIndices[(corner + 1)])], matrix: placement);
                        var pc = Vector3.Transform(position: piecePositions[((int)pieceIndices[(corner + 2)])], matrix: placement);
                        var face = SdfMeshAudit.FaceNormal(a: pa, b: pb, c: pc);

                        if (face == Vector3.Zero) { continue; }
                        (a, b, c) = (cluster.Add(normal: face, position: pa), cluster.Add(normal: face, position: pb), cluster.Add(normal: face, position: pc));
                    } else {
                        a = ClusterOf(cluster: cluster, normals: pieceNormals, placement: in placement, positions: piecePositions, remap: remap, vertex: ((int)pieceIndices[corner]));
                        b = ClusterOf(cluster: cluster, normals: pieceNormals, placement: in placement, positions: piecePositions, remap: remap, vertex: ((int)pieceIndices[(corner + 1)]));
                        c = ClusterOf(cluster: cluster, normals: pieceNormals, placement: in placement, positions: piecePositions, remap: remap, vertex: ((int)pieceIndices[(corner + 2)]));
                    }
                    cluster.AddTriangle(a: a, b: b, c: c, material: material);
                }
            }
            if (cluster.TriangleCount == 0) { return null; }

            return new SdfFarProduct(key: digest.Value, materials: kept.ToArray(), mesh: SdfMeshChunks.Bake(mesh: cluster.Build()), sourceDraws: drawn, sourceTriangles: sourceTriangles);
        }
    }

    // Whether the triangles' world-space box reaches the size on its longest side: a smaller part is below what a far
    // viewer resolves, and the product leaves it out.
    private static bool SpansAtLeast(ReadOnlySpan<uint> indices, in Matrix4x4 placement, ReadOnlySpan<Vector3> positions, float size) {
        if (!(size > 0f)) { return true; }

        var low = new Vector3(value: float.PositiveInfinity);
        var high = new Vector3(value: float.NegativeInfinity);

        foreach (var index in indices) {
            var point = Vector3.Transform(position: positions[((int)index)], matrix: placement);

            low = Vector3.Min(value1: low, value2: point);
            high = Vector3.Max(value1: high, value2: point);
        }

        var extent = (high - low);

        return (MathF.Max(x: extent.X, y: MathF.Max(x: extent.Y, y: extent.Z)) >= size);
    }
    // The cluster a piece's vertex joins, placed in the world: each vertex joins once, however many triangles name it.
    private static int ClusterOf(SdfMeshCluster cluster, int[] remap, int vertex, ReadOnlySpan<Vector3> positions, ReadOnlySpan<Vector3> normals, in Matrix4x4 placement) {
        if (remap[vertex] < 0) {
            remap[vertex] = cluster.Add(
                normal: Normalized(normal: Vector3.TransformNormal(normal: normals[vertex], matrix: placement)),
                position: Vector3.Transform(position: positions[vertex], matrix: placement));
        }
        return remap[vertex];
    }
    private static Vector3 Normalized(Vector3 normal) {
        var length = normal.Length();

        return ((length > 0f) ? (normal / length) : Vector3.UnitY);
    }
    // One draw's element of the inputs' digest: the mesh it names (by identity, within this process), its piece, material
    // and placement.
    private static ulong DrawElement(in SdfMeshDraw draw, in Matrix4x4 placement) {
        var hash = new HashCode();

        hash.Add(value: RuntimeHelpers.GetHashCode(o: draw.Mesh));
        hash.Add(value: draw.Piece);
        hash.Add(value: draw.Material);
        hash.Add(value: placement);
        return ((ulong)((uint)hash.ToHashCode()) | (((ulong)((uint)draw.Mesh.TriangleCount)) << 32));
    }

    /// <summary>What a product is built from: a residency's static draws, each mesh claimed until the build releases
    /// it, and the program's materials. Only <see cref="Take"/> makes one.</summary>
    public sealed class Inputs : IDisposable {
        private readonly SdfMeshClaims m_claims = new();

        internal Inputs(List<SdfMeshDraw> draws, IReadOnlyList<SdfMaterial> materials, SdfFarProductOptions options) {
            m_claims.Update(draws: draws);
            Draws = draws;
            Materials = materials;
            Options = options;
        }

        internal IReadOnlyList<SdfMeshDraw> Draws { get; }
        internal IReadOnlyList<SdfMaterial> Materials { get; }
        internal SdfFarProductOptions Options { get; }
        /// <summary>Gets how many static draws were taken.</summary>
        public int Count => Draws.Count;

        /// <summary>Releases the claims on the inputs' meshes.</summary>
        public void Dispose() => m_claims.Dispose();
    }
}
/// <summary>What a world's far product keeps (<see cref="SdfFarProduct"/>).</summary>
/// <param name="Cell">The side, in metres, of the cells the product gathers its vertices into, and the cell its levels are
/// measured against: its coarser levels stay
/// within multiples of it (<see cref="SdfMeshCollapse.CellShares"/>), which the far view's draws choose between by the
/// pixels they cover. Positive.</param>
/// <param name="MinimumSize">The least size, in metres, a static draw's longest side reaches to enter the product: a far
/// viewer resolves nothing smaller. Zero keeps every static draw.</param>
/// <remarks>A class, so <c>new SdfFarProductOptions()</c> takes the defaults above: a struct's parameterless
/// <c>new</c> is its zero value, a cell of zero metres, which no product can be gathered into.</remarks>
public sealed record SdfFarProductOptions(float Cell = SdfFarProduct.DefaultCell, float MinimumSize = 0f);
