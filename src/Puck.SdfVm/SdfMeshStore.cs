using System.Buffers.Binary;
using System.IO.Hashing;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>
/// The baked meshes a machine keeps between runs, one file an entry under <see cref="Directory"/>, so a World that boots
/// a program it baked before takes each group's mesh from disk instead of baking it again
/// (<see cref="SdfMeshLibrary.TryLoad"/>), and a changed creation bakes only its own groups. Presentation alone reads it:
/// a mesh is drawn, never simulated, so nothing a run computes depends on what the store holds.
/// <para>An entry is keyed by the bake's identity (<see cref="IdentityOf"/>: the bake kernels' bytecode and the code that
/// samples, contours and meshes them, <see cref="CodeIdentity"/>) and the group's geometry
/// (<see cref="SdfMeshGeometryBuilder"/>), so a change to either reads no entry the other made. An entry names its key and
/// ends with a checksum of its bytes; one that is truncated, damaged, another key's or not a mesh
/// (<see cref="SdfMesh"/> refuses it) is refused by name and deleted, and its group bakes. Writes replace an entry
/// atomically, so concurrent Worlds may share a directory.</para>
/// <para>The store holds at most <see cref="CapacityBytes"/> bytes, under Puck's one cache retention: a read stamps an
/// entry and a write enforces the bound, least recently used out (<see cref="Puck.Abstractions.CacheRetention"/>). Its
/// members are thread-safe.</para>
/// </summary>
public sealed class SdfMeshStore {
    /// <summary>The bytes a store holds at most unless its owner names another bound: a gibibyte, about fifteen boots of
    /// the debug world's meshes.</summary>
    public const long DefaultCapacityBytes = (1L << 30);
    /// <summary>The extension of an entry's file.</summary>
    public const string Extension = ".mesh";

    // "PUCKMSH2": the entry format. Another layout is another magic, so an older entry is refused rather than misread.
    private const ulong Magic = 0x3248534D4B435550ul;
    // The magic, the key, and seven counts: vertices, normals, indices, triangle materials, the group's materials, members
    // and vertex members.
    private const int HeaderBytes = (sizeof(ulong) + 16 + (7 * sizeof(int)));
    // A member's bytes: its slot offset, its position and its orientation.
    private const int MemberBytes = (sizeof(int) + (7 * sizeof(float)));
    private const int ChecksumBytes = 16;
    // The most entries a store keeps, past any world's groups many times over: its bytes bound it first.
    private const int MaxEntries = 65_536;

    /// <summary>Initializes a new instance of the <see cref="SdfMeshStore"/> class. Nothing is created until an entry is
    /// written.</summary>
    /// <param name="directory">The directory the entries are kept in, made absolute here.</param>
    /// <param name="capacityBytes">The bytes the entries hold at most.</param>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is <see langword="null"/>, empty or white
    /// space.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacityBytes"/> is not positive.</exception>
    public SdfMeshStore(string directory, long capacityBytes = DefaultCapacityBytes) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: directory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: capacityBytes);

        CapacityBytes = capacityBytes;
        Directory = Path.GetFullPath(path: directory);
    }

    /// <summary>Gets the bytes the entries hold at most.</summary>
    public long CapacityBytes { get; }
    /// <summary>Gets the absolute directory the entries are kept in.</summary>
    public string Directory { get; }

    /// <summary>Gets the identity of the code a bake runs on the CPU: the build of <c>Puck.SignedDistance</c>, whose lattice
    /// and contouring mesh a group, and the hash of <c>Puck.SdfVm</c>'s bake sources its build records (the
    /// <c>PuckMeshBakeSources</c> assembly metadata, <c>Puck.SdfVm.csproj</c>), so an edit to the rest of the renderer keeps
    /// every entry while an edit to the bake reads none.</summary>
    public static UInt128 CodeIdentity { get; } = Identity(
        mvid: typeof(SdfProgram).Assembly.ManifestModule.ModuleVersionId,
        sources: (typeof(SdfMeshStore).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(predicate: static attribute => (attribute.Key == "PuckMeshBakeSources"))?.Value ?? string.Empty)
    );

    /// <summary>Returns the identity of a bake on a kernel set: <see cref="CodeIdentity"/> and the bytecode of the kernels
    /// that sample a group's field, <see cref="SdfKernel.MeshBakeMask"/> and <see cref="SdfKernel.MeshBake"/>, which hold the
    /// field interpreter and differ by backend.</summary>
    /// <param name="kernels">The kernel set the bake dispatches.</param>
    /// <returns>The identity.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="kernels"/> is <see langword="null"/>.</exception>
    public static UInt128 IdentityOf(SdfKernelSet kernels) {
        ArgumentNullException.ThrowIfNull(argument: kernels);

        var hash = new XxHash128();
        Span<byte> code = stackalloc byte[16];

        BinaryPrimitives.WriteUInt128LittleEndian(destination: code, value: CodeIdentity);
        hash.Append(source: code);
        hash.Append(source: kernels[SdfKernel.MeshBakeMask].Span);
        hash.Append(source: kernels[SdfKernel.MeshBake].Span);

        return hash.GetCurrentHashAsUInt128();
    }
    /// <summary>Returns whether the store holds an entry for a geometry under a bake's identity, without reading it.</summary>
    /// <param name="identity">The bake's identity.</param>
    /// <param name="geometry">The group's geometry.</param>
    /// <returns><see langword="true"/> when an entry's file exists.</returns>
    public bool Contains(UInt128 identity, SdfMeshGeometry geometry) => File.Exists(path: PathOf(key: KeyOf(geometry: geometry, identity: identity)));
    /// <summary>Reads a geometry's mesh under a bake's identity: the mesh and the material ids its group's shapes carried,
    /// in the order the geometry numbers them (<see cref="SdfMeshLibrary.Store"/>). An entry the store refuses is
    /// deleted.</summary>
    /// <param name="identity">The bake's identity.</param>
    /// <param name="geometry">The group's geometry.</param>
    /// <param name="mesh">The mesh, when this returns <see langword="true"/>.</param>
    /// <param name="materials">The group's material ids, when this returns <see langword="true"/>.</param>
    /// <param name="refusal">Why a held entry was refused, or <see langword="null"/> when there was none to read.</param>
    /// <returns><see langword="true"/> when the store held a sound entry.</returns>
    public bool TryRead(UInt128 identity, SdfMeshGeometry geometry, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out SdfMesh? mesh, out int[] materials, out string? refusal) {
        var key = KeyOf(geometry: geometry, identity: identity);
        var path = PathOf(key: key);
        byte[] bytes;

        mesh = null;
        materials = [];
        refusal = null;

        try {
            bytes = File.ReadAllBytes(path: path);
        } catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) {
            return false;
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            refusal = $"cannot read {Path.GetFileName(path: path)}: {exception.Message}";

            return false;
        }

        if (!TryDecode(bytes: bytes, key: key, materials: out materials, mesh: out mesh, refusal: out var why)) {
            refusal = $"{Path.GetFileName(path: path)} {why}";
            Delete(path: path);

            return false;
        }

        Puck.Abstractions.CacheRetention.Stamp(path: path);

        return true;
    }
    /// <summary>Writes a geometry's mesh under a bake's identity, replacing any entry it had, then holds the store to
    /// <see cref="CapacityBytes"/>, keeping this entry. A write the file system refuses leaves the store as it was: the store is a cache.</summary>
    /// <param name="identity">The bake's identity.</param>
    /// <param name="geometry">The group's geometry.</param>
    /// <param name="mesh">The mesh.</param>
    /// <param name="materials">The group's material ids, in the order the geometry numbers them.</param>
    /// <returns><see langword="true"/> when the entry was written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mesh"/> or <paramref name="materials"/> is
    /// <see langword="null"/>.</exception>
    public bool Write(UInt128 identity, SdfMeshGeometry geometry, SdfMesh mesh, int[] materials) {
        ArgumentNullException.ThrowIfNull(argument: mesh);
        ArgumentNullException.ThrowIfNull(argument: materials);

        var key = KeyOf(geometry: geometry, identity: identity);
        var path = PathOf(key: key);
        var bytes = Encode(key: key, materials: materials, mesh: mesh);
        if (!Quietly(action: () => Puck.Assets.AtomicFile.WriteAllBytes(bytes: bytes, path: path))) { return false; }
        _ = Puck.Abstractions.CacheRetention.EnforceOnWrite(
            bound: new Puck.Abstractions.CacheBound(MaxBytes: CapacityBytes, MaxEntries: MaxEntries),
            directory: Directory,
            inUse: path,
            isEntry: static file => file.Name.EndsWith(value: Extension, comparisonType: StringComparison.Ordinal)
        );

        return true;
    }

    // The key of an entry: the bake's identity and the geometry, its surface and frame.
    private static UInt128 KeyOf(UInt128 identity, SdfMeshGeometry geometry) {
        Span<byte> key = stackalloc byte[33];

        BinaryPrimitives.WriteUInt128LittleEndian(destination: key, value: identity);
        BinaryPrimitives.WriteUInt128LittleEndian(destination: key[16..], value: geometry.Hash);
        key[32] = (byte)(geometry.Dynamic ? 1 : 0);

        return XxHash128.HashToUInt128(source: key);
    }
    private static UInt128 Identity(Guid mvid, string sources) {
        var hash = new XxHash128();
        Span<byte> module = stackalloc byte[16];

        _ = mvid.TryWriteBytes(destination: module);
        hash.Append(source: module);
        hash.Append(source: Encoding.UTF8.GetBytes(s: sources));

        return hash.GetCurrentHashAsUInt128();
    }
    private string PathOf(UInt128 key) => Path.Combine(path1: Directory, path2: (key.ToString(format: "x32", provider: null) + Extension));
    private static byte[] Encode(UInt128 key, SdfMesh mesh, int[] materials) {
        var positions = MemoryMarshal.AsBytes(span: mesh.Positions.Span);
        var normals = MemoryMarshal.AsBytes(span: mesh.Normals.Span);
        var indices = MemoryMarshal.AsBytes(span: mesh.Indices.Span);
        var triangleMaterials = MemoryMarshal.AsBytes(span: mesh.TriangleMaterials.Span);
        var groupMaterials = MemoryMarshal.AsBytes(span: materials.AsSpan());
        var members = mesh.Members.Span;
        var vertexMembers = mesh.VertexMembers.Span;
        var bytes = new byte[(((((((HeaderBytes + groupMaterials.Length) + positions.Length) + normals.Length) + indices.Length) + triangleMaterials.Length) + (members.Length * MemberBytes)) + vertexMembers.Length) + ChecksumBytes];
        var at = bytes.AsSpan();

        BinaryPrimitives.WriteUInt64LittleEndian(destination: at, value: Magic);
        BinaryPrimitives.WriteUInt128LittleEndian(destination: at[8..], value: key);
        BinaryPrimitives.WriteInt32LittleEndian(destination: at[24..], value: mesh.Positions.Length);
        BinaryPrimitives.WriteInt32LittleEndian(destination: at[28..], value: mesh.Normals.Length);
        BinaryPrimitives.WriteInt32LittleEndian(destination: at[32..], value: mesh.Indices.Length);
        BinaryPrimitives.WriteInt32LittleEndian(destination: at[36..], value: mesh.TriangleMaterials.Length);
        BinaryPrimitives.WriteInt32LittleEndian(destination: at[40..], value: materials.Length);
        BinaryPrimitives.WriteInt32LittleEndian(destination: at[44..], value: members.Length);
        BinaryPrimitives.WriteInt32LittleEndian(destination: at[48..], value: vertexMembers.Length);
        var offset = HeaderBytes;

        groupMaterials.CopyTo(destination: at[offset..]);
        offset += groupMaterials.Length;
        positions.CopyTo(destination: at[offset..]);
        offset += positions.Length;
        normals.CopyTo(destination: at[offset..]);
        offset += normals.Length;
        indices.CopyTo(destination: at[offset..]);
        offset += indices.Length;
        triangleMaterials.CopyTo(destination: at[offset..]);
        offset += triangleMaterials.Length;
        foreach (var (slotOffset, position, orientation) in members) {
            var member = at.Slice(length: MemberBytes, start: offset);

            BinaryPrimitives.WriteInt32LittleEndian(destination: member, value: slotOffset);
            BinaryPrimitives.WriteSingleLittleEndian(destination: member[4..], value: position.X);
            BinaryPrimitives.WriteSingleLittleEndian(destination: member[8..], value: position.Y);
            BinaryPrimitives.WriteSingleLittleEndian(destination: member[12..], value: position.Z);
            BinaryPrimitives.WriteSingleLittleEndian(destination: member[16..], value: orientation.X);
            BinaryPrimitives.WriteSingleLittleEndian(destination: member[20..], value: orientation.Y);
            BinaryPrimitives.WriteSingleLittleEndian(destination: member[24..], value: orientation.Z);
            BinaryPrimitives.WriteSingleLittleEndian(destination: member[28..], value: orientation.W);
            offset += MemberBytes;
        }
        vertexMembers.CopyTo(destination: at[offset..]);
        offset += vertexMembers.Length;
        BinaryPrimitives.WriteUInt128LittleEndian(destination: at[offset..], value: XxHash128.HashToUInt128(source: bytes.AsSpan(length: (bytes.Length - ChecksumBytes), start: 0)));

        return bytes;
    }
    // An entry's mesh and its group's materials, or why its bytes are no sound entry for the key.
    private static bool TryDecode(byte[] bytes, UInt128 key, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out SdfMesh? mesh, out int[] materials, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: false)] out string? refusal) {
        refusal = Refusal(bytes: bytes, key: key);
        mesh = null;
        materials = [];
        if (refusal is not null) { return false; }

        var body = bytes.AsSpan(length: (bytes.Length - ChecksumBytes), start: 0);
        var groupMaterials = BinaryPrimitives.ReadInt32LittleEndian(source: body[40..]);
        var vertices = BinaryPrimitives.ReadInt32LittleEndian(source: body[24..]);
        var normals = BinaryPrimitives.ReadInt32LittleEndian(source: body[28..]);
        var indices = BinaryPrimitives.ReadInt32LittleEndian(source: body[32..]);
        var triangleMaterials = BinaryPrimitives.ReadInt32LittleEndian(source: body[36..]);
        var memberCount = BinaryPrimitives.ReadInt32LittleEndian(source: body[44..]);
        var vertexMemberCount = BinaryPrimitives.ReadInt32LittleEndian(source: body[48..]);
        var offset = HeaderBytes;

        materials = MemoryMarshal.Cast<byte, int>(span: body.Slice(length: (4 * groupMaterials), start: offset)).ToArray();
        offset += (4 * groupMaterials);

        var positionData = MemoryMarshal.Cast<byte, Vector3>(span: body.Slice(length: (12 * vertices), start: offset)).ToArray();

        offset += (12 * vertices);

        var normalData = MemoryMarshal.Cast<byte, Vector3>(span: body.Slice(length: (12 * normals), start: offset)).ToArray();

        offset += (12 * normals);

        var indexData = MemoryMarshal.Cast<byte, uint>(span: body.Slice(length: (4 * indices), start: offset)).ToArray();

        offset += (4 * indices);

        var materialData = MemoryMarshal.Cast<byte, uint>(span: body.Slice(length: (4 * triangleMaterials), start: offset)).ToArray();

        offset += (4 * triangleMaterials);

        var members = new SdfMeshMember[memberCount];

        for (var index = 0; (index < memberCount); index++) {
            var member = body.Slice(length: MemberBytes, start: offset);

            members[index] = new SdfMeshMember(
                Orientation: new Quaternion(x: BinaryPrimitives.ReadSingleLittleEndian(source: member[16..]), y: BinaryPrimitives.ReadSingleLittleEndian(source: member[20..]), z: BinaryPrimitives.ReadSingleLittleEndian(source: member[24..]), w: BinaryPrimitives.ReadSingleLittleEndian(source: member[28..])),
                Position: new Vector3(x: BinaryPrimitives.ReadSingleLittleEndian(source: member[4..]), y: BinaryPrimitives.ReadSingleLittleEndian(source: member[8..]), z: BinaryPrimitives.ReadSingleLittleEndian(source: member[12..])),
                SlotOffset: BinaryPrimitives.ReadInt32LittleEndian(source: member)
            );
            offset += MemberBytes;
        }

        var vertexMemberData = body.Slice(length: vertexMemberCount, start: offset).ToArray();

        try {
            mesh = new SdfMesh(indices: indexData, members: members, normals: normalData, positions: positionData, triangleMaterials: materialData, vertexMembers: vertexMemberData);
        } catch (ArgumentException exception) {
            refusal = $"is no mesh: {exception.Message}";

            return false;
        }

        return true;
    }
    // Why an entry's bytes are no sound entry for the key, judged by its length, checksum, format, key and counts; null for
    // bytes whose parts its counts lay out exactly.
    private static string? Refusal(byte[] bytes, UInt128 key) {
        if (bytes.Length < (HeaderBytes + ChecksumBytes)) { return "is truncated"; }

        var body = bytes.AsSpan(length: (bytes.Length - ChecksumBytes), start: 0);

        if (XxHash128.HashToUInt128(source: body) != BinaryPrimitives.ReadUInt128LittleEndian(source: bytes.AsSpan(start: body.Length))) { return "fails its checksum"; }
        if (BinaryPrimitives.ReadUInt64LittleEndian(source: body) != Magic) { return "is not a mesh entry of this format"; }
        if (BinaryPrimitives.ReadUInt128LittleEndian(source: body[8..]) != key) { return "holds another geometry's mesh"; }

        var vertices = BinaryPrimitives.ReadInt32LittleEndian(source: body[24..]);
        var normals = BinaryPrimitives.ReadInt32LittleEndian(source: body[28..]);
        var indices = BinaryPrimitives.ReadInt32LittleEndian(source: body[32..]);
        var triangleMaterials = BinaryPrimitives.ReadInt32LittleEndian(source: body[36..]);
        var groupMaterials = BinaryPrimitives.ReadInt32LittleEndian(source: body[40..]);
        var members = BinaryPrimitives.ReadInt32LittleEndian(source: body[44..]);
        var vertexMembers = BinaryPrimitives.ReadInt32LittleEndian(source: body[48..]);
        var expected = ((((((((long)HeaderBytes + (4L * groupMaterials)) + (12L * vertices)) + (12L * normals)) + (4L * indices)) + (4L * triangleMaterials)) + (((long)MemberBytes) * members)) + vertexMembers);

        return (((vertices < 0) || (normals < 0) || (indices < 0) || (triangleMaterials < 0) || (groupMaterials < 0) || (members < 0) || (vertexMembers < 0) || (expected != body.Length)) ? "holds counts its length does not" : null);
    }
    // Deletes a file the store owns, leaving one another World holds open for a later trim or refusal to remove.
    private static void Delete(string path) => _ = Quietly(action: () => File.Delete(path: path));
    // Runs a file-system action, answering whether the file system allowed it.
    private static bool Quietly(Action action) {
        try {
            action();

            return true;
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            return false;
        }
    }
}
