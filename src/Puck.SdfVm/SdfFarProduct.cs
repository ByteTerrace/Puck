using System.Numerics;
using System.Runtime.CompilerServices;
using Puck.Maths;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>
/// What a world shows of itself to a far viewer: its settled static scene merged into one world-space mesh at the
/// coarsest level each of its meshes carries, chunked and levelled again at the product's own cell
/// (<see cref="SdfMeshChunks.Bake"/>), with the materials its triangles shade with. A view that shows a world from far
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
    /// <summary>The product's cell when its options state none: the largest error, in metres, the product's finest level
    /// is levelled against (<see cref="SdfMesh.BakeCell"/>).</summary>
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
    public static Inputs? Take(SdfFrame frame, SdfFarProductOptions options) {
        ArgumentNullException.ThrowIfNull(argument: frame);

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
    /// triangles' materials renumbered into the product's own, merged into one mesh and chunked and levelled at the
    /// product's cell. Releases the inputs' claims whatever happens.</summary>
    /// <param name="inputs">What <see cref="Take"/> took.</param>
    /// <returns>The product, or <see langword="null"/> when no triangle survives.</returns>
    public static SdfFarProduct? Build(Inputs inputs) {
        ArgumentNullException.ThrowIfNull(argument: inputs);

        using (inputs) {
            var options = inputs.Options;
            var sourceMaterials = inputs.Materials;
            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var indices = new List<uint>();
            var materials = new List<uint>();
            var renumbered = new Dictionary<int, uint>();
            var kept = new List<SdfMaterial>();
            var remap = Array.Empty<int>();
            var digest = MultisetDigest.Empty.Add(element: BitConverter.SingleToUInt32Bits(value: options.Cell)).Add(element: BitConverter.SingleToUInt32Bits(value: options.MinimumSize));
            var drawn = 0;
            var sourceTriangles = 0L;

            foreach (var draw in inputs.Draws) {
                var placement = draw.FixedPlacement;

                digest = digest.Add(element: DrawElement(draw: in draw, placement: in placement));

                using var held = draw.Mesh.Hold();

                if (!held.Readable) { continue; }

                var piece = draw.Mesh.Piece(index: draw.Piece);
                var level = piece.Levels.Count;
                var pieceIndices = ((level == 0) ? piece.Indices : piece.Levels[(level - 1)].Indices).Span;
                var pieceMaterials = ((level == 0) ? piece.TriangleMaterials : piece.Levels[(level - 1)].TriangleMaterials).Span;
                var piecePositions = piece.Positions.Span;
                var pieceNormals = piece.Normals.Span;

                if (pieceIndices.IsEmpty) { continue; }
                if (!SpansAtLeast(indices: pieceIndices, placement: in placement, positions: piecePositions, size: options.MinimumSize)) { continue; }

                drawn++;
                sourceTriangles += (pieceIndices.Length / 3);
                if (remap.Length < piecePositions.Length) { remap = new int[piecePositions.Length]; }
                remap.AsSpan(start: 0, length: piecePositions.Length).Fill(value: -1);

                for (var corner = 0; (corner < pieceIndices.Length); corner += 3) {
                    var triangle = (corner / 3);
                    var program = (draw.Material + (pieceMaterials.IsEmpty ? 0 : ((int)pieceMaterials[triangle])));

                    // A triangle whose material the program does not hold (a screen's face) has nothing to shade with here.
                    if ((program < 0) || (program >= sourceMaterials.Count)) { continue; }
                    if (!renumbered.TryGetValue(key: program, value: out var material)) {
                        material = ((uint)kept.Count);
                        renumbered.Add(key: program, value: material);
                        kept.Add(item: sourceMaterials[program]);
                    }
                    if (pieceNormals.IsEmpty) {
                        // A mesh without normals (an authored sheet) takes its face's normal at three corners of its own.
                        var a = Vector3.Transform(position: piecePositions[((int)pieceIndices[corner])], matrix: placement);
                        var b = Vector3.Transform(position: piecePositions[((int)pieceIndices[(corner + 1)])], matrix: placement);
                        var c = Vector3.Transform(position: piecePositions[((int)pieceIndices[(corner + 2)])], matrix: placement);
                        var face = SdfMeshAudit.FaceNormal(a: a, b: b, c: c);

                        if (face == Vector3.Zero) { continue; }
                        foreach (var point in (ReadOnlySpan<Vector3>)[a, b, c]) {
                            indices.Add(item: ((uint)positions.Count));
                            positions.Add(item: point);
                            normals.Add(item: face);
                        }
                    } else {
                        for (var at = 0; (at < 3); at++) {
                            var vertex = ((int)pieceIndices[(corner + at)]);

                            if (remap[vertex] < 0) {
                                remap[vertex] = positions.Count;
                                positions.Add(item: Vector3.Transform(position: piecePositions[vertex], matrix: placement));
                                normals.Add(item: Normalized(normal: Vector3.TransformNormal(normal: pieceNormals[vertex], matrix: placement)));
                            }
                            indices.Add(item: ((uint)remap[vertex]));
                        }
                    }
                    materials.Add(item: material);
                }
            }
            if (indices.Count == 0) { return null; }

            var merged = new SdfMesh(indices: indices.ToArray(), normals: normals.ToArray(), positions: positions.ToArray(), triangleMaterials: materials.ToArray()) {
                BakeCell = new Vector3(value: options.Cell),
            };

            return new SdfFarProduct(key: digest.Value, materials: kept.ToArray(), mesh: SdfMeshChunks.Bake(mesh: merged), sourceDraws: drawn, sourceTriangles: sourceTriangles);
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
/// <param name="Cell">The error, in metres, the product's finest level is levelled against: its coarser levels stay
/// within multiples of it (<see cref="SdfMeshCollapse.CellShares"/>), which the far view's draws choose between by the
/// pixels they cover. Positive.</param>
/// <param name="MinimumSize">The least size, in metres, a static draw's longest side reaches to enter the product: a far
/// viewer resolves nothing smaller. Zero keeps every static draw.</param>
public readonly record struct SdfFarProductOptions(float Cell = SdfFarProduct.DefaultCell, float MinimumSize = 0f);
