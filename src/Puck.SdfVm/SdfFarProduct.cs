using System.IO.Hashing;
using System.Numerics;
using System.Runtime.InteropServices;
using Puck.Abstractions.Memory;
using Puck.Hosting.Jobs;
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
/// <para>It is a derived product, a cache: named by a digest of what it is derived from (<see cref="Key"/>), kept in
/// the mesh library's store under that name, and derived only when the store holds none (<see cref="Build"/>, which
/// counts which it did in <see cref="Derived"/>). The name reads each static draw's bake group by its shape, which no
/// tier changes (<see cref="SdfMeshBakeGroup.Shape"/>), with its placement and the material ids its triangles carry, so
/// the residency a product is derived from may stand at any tiers: a product derived under one tier set serves every
/// other, since the tiers differ by less than the product's cell. Dynamic content (a moving body, a skinned or displaced
/// mesh, glass) is absent: a far viewer sees the world's ground and standing masses, never its motion.</para>
/// <para>Invariant: every triangle of the product reads a material the product carries. Enforced in <see cref="Build"/>,
/// the one door that makes a product: its materials are the ids its inputs' groups carry, in ascending order
/// (<see cref="Inputs"/>), a derived triangle is numbered by its id's place among them, and a triangle whose id is not
/// among them (a screen's) is left out; a stored product was derived under the same ids, which its name digests.</para>
/// </summary>
public sealed class SdfFarProduct : IDisposable {
    /// <summary>The product's cell when its options state none, in metres: the side of the cells its vertices are
    /// gathered into, and the cell its levels are measured against (<see cref="SdfMesh.BakeCell"/>).</summary>
    public const float DefaultCell = 1f;

    // What the name digests besides the inputs: the product's kind, so no bake group's geometry shares a name with one.
    private static ReadOnlySpan<byte> Kind => "puck.far-product"u8;

    private SdfFarProduct(SdfMesh mesh, IReadOnlyList<SdfMaterial> materials, UInt128 key, bool derived, int sourceDraws, long sourceTriangles) {
        Mesh = mesh;
        Materials = materials;
        Key = key;
        Derived = derived;
        SourceDraws = sourceDraws;
        SourceTriangles = sourceTriangles;
    }

    /// <summary>Gets the product's mesh, in the world's coordinates, its pieces each drawn by one draw
    /// (<see cref="DrawsAt"/>). The product holds one claim on it until it is disposed.</summary>
    public SdfMesh Mesh { get; }
    /// <summary>Gets the materials the mesh's triangles name, in the order its triangle materials count them.</summary>
    public IReadOnlyList<SdfMaterial> Materials { get; }
    /// <summary>Gets the digest of what the product is derived from: each static draw's shape, placement and material
    /// ids, and the options. It names the product in the mesh store, on every machine and run.</summary>
    public UInt128 Key { get; }
    /// <summary>Gets whether this process derived the product (clustered and levelled it), rather than read it from the
    /// store.</summary>
    public bool Derived { get; }
    /// <summary>Gets how many static draws the product was derived from; zero for a product read from the store.</summary>
    public int SourceDraws { get; }
    /// <summary>Gets how many triangles the source draws' coarsest levels held before the product's own merge; zero for
    /// a product read from the store.</summary>
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
    public override string ToString() => $"far-product key={Key:x32} {(Derived ? $"derived source-draws={SourceDraws} source-triangles={SourceTriangles}" : "stored")} triangles={Triangles} pieces={Mesh.PieceCount} materials={Materials.Count} bytes={Bytes}";
    /// <summary>Releases the product's claim on its mesh. A draw that still names the mesh keeps it through its own
    /// rows' claims.</summary>
    public void Dispose() {
        if (Interlocked.Exchange(location1: ref m_disposed, value: 1) == 0) { Mesh.Release(); }
    }
    private int m_disposed;

    /// <summary>Takes what a product is built from out of a residency's frame, on the thread that owns the frame: its
    /// static scene's draws (<see cref="SdfMeshDraw.InStaticScene"/>) with a claim on each mesh they name, so the meshes
    /// outlive the frame's next capture while a worker reads them, the program's materials, and for each draw of a bake
    /// group the group's shape and material ids (<see cref="SdfMeshPathBake.DescribeShown"/>).</summary>
    /// <param name="frame">The residency's dressed frame (<see cref="SdfWorldResidency.Frame"/>).</param>
    /// <param name="options">What the product keeps.</param>
    /// <param name="bake">The residency's bake, which names its groups' meshes; <see langword="null"/> for a frame of
    /// authored meshes alone, each then named by its content.</param>
    /// <returns>The inputs, which <see cref="Build"/> consumes and releases; <see langword="null"/> when the frame
    /// holds no static draw.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    public static Inputs? Take(SdfFrame frame, SdfFarProductOptions options, SdfMeshPathBake? bake = null) {
        ArgumentNullException.ThrowIfNull(argument: frame);
        ArgumentNullException.ThrowIfNull(argument: options);

        var rows = frame.DrawRows;
        var draws = new List<SdfMeshDraw>();

        for (var row = 0; (row < rows.Count); row++) {
            var draw = rows[row];

            if (draw.InStaticScene) { draws.Add(item: draw); }
        }
        if (draws.Count == 0) { return null; }

        var shown = new Dictionary<SdfMesh, SdfFarSource>(comparer: ReferenceEqualityComparer.Instance);

        bake?.DescribeShown(into: shown);
        return new Inputs(draws: draws, materials: frame.Program.Appearance.Materials, options: options, shown: shown);
    }
    /// <summary>Answers the product of its inputs, on a worker: the stored product when the library's store holds one
    /// under the inputs' name, and otherwise one derived here (each draw's coarsest level placed in the world, gathered
    /// into the product's cells, chunked and levelled at the product's cell) and kept in the store under that name.
    /// Releases the inputs' claims whatever happens.</summary>
    /// <param name="inputs">What <see cref="Take"/> took.</param>
    /// <param name="library">The library whose store keeps the product and whose allocator and account hold it, or
    /// <see langword="null"/> to derive a product held in managed arrays and kept nowhere.</param>
    /// <param name="identity">The identity of the bake whose code derives the product
    /// (<see cref="SdfMeshBakeIdentity.Value"/>), which names the store's set.</param>
    /// <returns>The product, which the caller disposes, or <see langword="null"/> when no triangle survives.</returns>
    /// <exception cref="HostMemoryRefusedException">The account had no room for the derivation's workspace; nothing is
    /// kept, and a later call may derive it.</exception>
    public static SdfFarProduct? Build(Inputs inputs, SdfMeshLibrary? library = null, UInt128 identity = default) {
        ArgumentNullException.ThrowIfNull(argument: inputs);

        using (inputs) {
            var (key, ids) = Name(inputs: inputs);
            var geometry = new SdfMeshGeometry(Dynamic: false, Hash: key);
            var materials = new SdfMaterial[ids.Length];
            // The product's triangles count its own materials, so the store's material places are its indices.
            var places = new int[ids.Length];

            for (var place = 0; (place < ids.Length); place++) {
                materials[place] = inputs.Materials[ids[place]];
                places[place] = place;
            }
            if ((library is not null) && library.Holds(geometry: geometry, identity: identity)) {
                if (library.TryLoad(declined: out var declined, geometry: geometry, identity: identity, materials: places, mesh: out var stored, refusal: out _)) {
                    return ((stored is null) ? null : new SdfFarProduct(derived: false, key: key, materials: materials, mesh: stored, sourceDraws: 0, sourceTriangles: 0L));
                }
                // A stored product the account has no room to read is never derived instead: deriving it needs more.
                if (declined) { throw new HostMemoryRefusedException(bytes: 0L, owner: "far-product"); }
            }

            var derived = Derive(ids: ids, inputs: inputs, library: library, sourceDraws: out var drawn, sourceTriangles: out var sourceTriangles);

            if (derived is null) {
                library?.StoreEmpty(geometry: geometry, identity: identity, persist: true);
                return null;
            }
            return new SdfFarProduct(derived: true, key: key, materials: materials, sourceDraws: drawn, sourceTriangles: sourceTriangles,
                mesh: ((library is null) ? derived : library.Store(geometry: geometry, identity: identity, materials: places, mesh: derived)));
        }
    }

    // The inputs' name and the material ids the product carries, ascending. Each draw adds one element to an unordered
    // digest, since a frame's rows come in no fixed order: its source's name (a bake group's shape, or an authored mesh's
    // positions), its placement, its material base and the ids its triangles carry. A group's mesh is counted at its
    // first piece alone, since how many pieces it has is its tier's.
    private static (UInt128 Key, int[] Ids) Name(Inputs inputs) {
        var count = inputs.Materials.Count;
        var ids = new SortedSet<int>();
        var digest = MultisetDigest.Empty;
        var element = new XxHash3();
        var counted = 0;

        foreach (var draw in inputs.Draws) {
            UInt128 name;

            element.Reset();
            if (inputs.Shown.TryGetValue(key: draw.Mesh, value: out var group)) {
                foreach (var id in group.Materials) { Carry(id: (draw.Material + id)); }
                if (draw.Piece != 0) { continue; }
                name = group.Shape;
                element.Append(source: MemoryMarshal.AsBytes(span: group.Materials.AsSpan()));
            } else {
                var piece = draw.Mesh.Piece(index: draw.Piece);

                using var held = piece.Hold();

                if (!held.Readable) { continue; }

                var pieceMaterials = held.TriangleMaterials.Span;

                if (pieceMaterials.IsEmpty) { Carry(id: draw.Material); }
                foreach (var carried in pieceMaterials) { Carry(id: (draw.Material + ((int)carried))); }
                name = XxHash128.HashToUInt128(source: MemoryMarshal.AsBytes(span: held.Positions.Span), seed: piece.TriangleCount);
                element.Append(source: MemoryMarshal.AsBytes(span: pieceMaterials));
            }

            var placement = draw.FixedPlacement;
            var material = draw.Material;

            element.Append(source: MemoryMarshal.AsBytes(span: new ReadOnlySpan<UInt128>(reference: in name)));
            element.Append(source: MemoryMarshal.AsBytes(span: new ReadOnlySpan<Matrix4x4>(reference: in placement)));
            element.Append(source: MemoryMarshal.AsBytes(span: new ReadOnlySpan<int>(reference: in material)));
            digest = digest.Add(element: element.GetCurrentHashAsUInt64());
            counted++;
        }

        var kept = new int[ids.Count];

        ids.CopyTo(array: kept);

        var whole = new XxHash128();
        var cell = inputs.Options.Cell;
        var minimum = inputs.Options.MinimumSize;
        var value = digest.Value;

        whole.Append(source: Kind);
        whole.Append(source: MemoryMarshal.AsBytes(span: new ReadOnlySpan<float>(reference: in cell)));
        whole.Append(source: MemoryMarshal.AsBytes(span: new ReadOnlySpan<float>(reference: in minimum)));
        whole.Append(source: MemoryMarshal.AsBytes(span: new ReadOnlySpan<ulong>(reference: in value)));
        whole.Append(source: MemoryMarshal.AsBytes(span: new ReadOnlySpan<int>(reference: in counted)));
        whole.Append(source: MemoryMarshal.AsBytes(span: kept.AsSpan()));
        return (whole.GetCurrentHashAsUInt128(), kept);

        // An id the program does not hold (a screen's face) has nothing to shade with in the product.
        void Carry(int id) {
            if ((id >= 0) && (id < count)) { _ = ids.Add(item: id); }
        }
    }
    // Derives the product's mesh: the cluster of every draw's coarsest level, chunked and levelled, its workspace native
    // and charged to one owner in the library's account, so a refusal ends the derivation with nothing held.
    private static SdfMesh? Derive(Inputs inputs, int[] ids, SdfMeshLibrary? library, out int sourceDraws, out long sourceTriangles) {
        var options = inputs.Options;
        using var owner = library?.Account.CreateOwner(allowance: MemoryAllowance.Authoring, memoryClass: MemoryClass.Workspace, name: "far-product");
        using var cluster = new SdfMeshCluster(allocator: library?.Allocator, cell: options.Cell, owner: owner);
        var remap = Array.Empty<int>();

        sourceDraws = 0;
        sourceTriangles = 0L;
        foreach (var draw in inputs.Draws) {
            var placement = draw.FixedPlacement;
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

            sourceDraws++;
            sourceTriangles += (pieceIndices.Length / 3);
            if (remap.Length < piecePositions.Length) { remap = new int[piecePositions.Length]; }
            remap.AsSpan(start: 0, length: piecePositions.Length).Fill(value: -1);

            for (var corner = 0; (corner < pieceIndices.Length); corner += 3) {
                var material = Array.BinarySearch(array: ids, value: (draw.Material + (pieceMaterials.IsEmpty ? 0 : ((int)pieceMaterials[(corner / 3)]))));

                // A triangle whose material the product does not carry (a screen's face) has nothing to shade with here.
                if (material < 0) { continue; }

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
                cluster.AddTriangle(a: a, b: b, c: c, material: ((uint)material));
            }
        }
        return ((cluster.TriangleCount == 0) ? null : SdfMeshChunks.Bake(allocator: library?.Allocator, mesh: cluster.Build(), owner: owner));
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

    /// <summary>What a product is built from: a residency's static draws, each mesh claimed until the build releases
    /// it, the program's materials, and the shape and material ids of each bake group whose mesh a draw names. Only
    /// <see cref="Take"/> makes one.</summary>
    public sealed class Inputs : IDisposable {
        private readonly SdfMeshClaims m_claims = new();

        internal Inputs(List<SdfMeshDraw> draws, IReadOnlyList<SdfMaterial> materials, SdfFarProductOptions options, Dictionary<SdfMesh, SdfFarSource> shown) {
            m_claims.Update(draws: draws);
            Draws = draws;
            Materials = materials;
            Options = options;
            Shown = shown;
        }

        internal IReadOnlyList<SdfMeshDraw> Draws { get; }
        internal IReadOnlyList<SdfMaterial> Materials { get; }
        internal SdfFarProductOptions Options { get; }
        // By mesh reference: a draw whose mesh is absent names an authored mesh.
        internal Dictionary<SdfMesh, SdfFarSource> Shown { get; }
        /// <summary>Gets how many static draws were taken.</summary>
        public int Count => Draws.Count;

        /// <summary>Releases the claims on the inputs' meshes.</summary>
        public void Dispose() => m_claims.Dispose();
    }
}
/// <summary>What names a bake group's mesh in a far product's inputs, whatever tier the mesh was baked at.</summary>
/// <param name="Shape">The group's shape (<see cref="SdfMeshBakeGroup.Shape"/>).</param>
/// <param name="Materials">The material ids the mesh's triangles carry (<see cref="SdfMeshBakeGroup.MaterialOrder"/>).</param>
internal readonly record struct SdfFarSource(UInt128 Shape, int[] Materials);
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
