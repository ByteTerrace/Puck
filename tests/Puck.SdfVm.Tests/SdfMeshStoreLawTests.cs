using System.Buffers.Binary;
using System.IO.Hashing;
using System.Numerics;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// Laws over <see cref="SdfMeshStore"/>, the baked meshes kept between runs: an entry reads back exactly under its key
/// alone, a damaged entry is refused by name and deleted, the store keeps within its capacity, and a bake's identity moves
/// with its kernels.
/// </summary>
public sealed class SdfMeshStoreLawTests {
    private static readonly SdfMeshGeometry Geometry = new(Dynamic: false, Hash: 0x0123_4567_89AB_CDEFul);
    private static readonly UInt128 Identity = 7;

    [Fact]
    public void AWrittenMeshReadsBackExactlyUnderItsIdentityAndGeometryAlone() {
        using var scratch = new TemporaryDirectory(prefix: "puck-mesh-store-");
        var store = new SdfMeshStore(directory: scratch.PathOf(name: "meshes"));
        var mesh = Mesh();

        // An empty store holds nothing and has made no directory.
        Assert.False(condition: store.Contains(geometry: Geometry, identity: Identity));
        Assert.False(condition: store.TryRead(geometry: Geometry, identity: Identity, materials: out _, mesh: out _, refusal: out var absent));
        Assert.Null(@object: absent);
        Assert.False(condition: Directory.Exists(path: store.Directory));

        Assert.True(condition: store.Write(geometry: Geometry, identity: Identity, materials: [4, 9], mesh: mesh));
        Assert.True(condition: store.Contains(geometry: Geometry, identity: Identity));
        Assert.True(condition: store.TryRead(geometry: Geometry, identity: Identity, materials: out var materials, mesh: out var read, refusal: out _));
        Assert.Equal(expected: [4, 9], actual: materials);
        Assert.Equal(expected: mesh.Positions.ToArray(), actual: read.Positions.ToArray());
        Assert.Equal(expected: mesh.Normals.ToArray(), actual: read.Normals.ToArray());
        Assert.Equal(expected: mesh.Indices.ToArray(), actual: read.Indices.ToArray());
        Assert.Equal(expected: mesh.TriangleMaterials.ToArray(), actual: read.TriangleMaterials.ToArray());

        // Another bake, another surface, or the same surface in a slot's frame reads nothing.
        Assert.False(condition: store.Contains(geometry: Geometry, identity: (Identity + 1)));
        Assert.False(condition: store.Contains(geometry: (Geometry with { Hash = (Geometry.Hash + 1) }), identity: Identity));
        Assert.False(condition: store.Contains(geometry: (Geometry with { Dynamic = true }), identity: Identity));
    }
    [Fact]
    public void ADamagedEntryIsRefusedByNameAndDeleted() {
        using var scratch = new TemporaryDirectory(prefix: "puck-mesh-store-");
        var store = new SdfMeshStore(directory: scratch.PathOf(name: "meshes"));
        var other = (Geometry with { Hash = 99 });

        Assert.True(condition: store.Write(geometry: Geometry, identity: Identity, materials: [4, 9], mesh: Mesh()));
        Assert.True(condition: store.Write(geometry: other, identity: Identity, materials: [], mesh: Mesh()));

        var path = Entry(store: store, geometry: Geometry);
        var sound = File.ReadAllBytes(path: path);

        // Each damage the store meets, with the refusal it names.
        (string Refusal, byte[] Bytes)[] damages = [
            ("is truncated", sound[..20]),
            ("fails its checksum", Flip(bytes: sound, at: 60)),
            ("is not a mesh entry of this format", Resealed(bytes: sound, edit: static body => body[0] ^= 1)),
            ("holds another geometry's mesh", File.ReadAllBytes(path: Entry(store: store, geometry: other))),
            ("holds counts its length does not", Resealed(bytes: sound, edit: static body => BinaryPrimitives.WriteInt32LittleEndian(destination: body[24..], value: 4))),
            ("holds counts its length does not", Resealed(bytes: sound, edit: static body => BinaryPrimitives.WriteInt32LittleEndian(destination: body[40..], value: -1))),
            // The index list names a fourth vertex of three.
            ("is no mesh", Resealed(bytes: sound, edit: static body => BinaryPrimitives.WriteUInt32LittleEndian(destination: body[(body.Length - 16)..], value: 3u))),
        ];

        foreach (var (refusal, bytes) in damages) {
            File.WriteAllBytes(bytes: bytes, path: path);

            Assert.False(condition: store.TryRead(geometry: Geometry, identity: Identity, materials: out _, mesh: out _, refusal: out var why));
            Assert.Contains(expectedSubstring: refusal, actualString: why);
            Assert.False(condition: File.Exists(path: path));
        }

        // An entry the file system will not read is refused and kept: a directory stands where it would be.
        _ = Directory.CreateDirectory(path: path);

        Assert.False(condition: store.TryRead(geometry: Geometry, identity: Identity, materials: out _, mesh: out _, refusal: out var unreadable));
        Assert.Contains(expectedSubstring: "cannot read", actualString: unreadable);
        // Nor will it write there; the store is a cache, and says so rather than throwing.
        Assert.False(condition: store.Write(geometry: Geometry, identity: Identity, materials: [], mesh: Mesh()));
        Assert.True(condition: Directory.Exists(path: path));
    }
    [Fact]
    public void AStorePastItsCapacityKeepsItsMostRecentlyUsedEntries() {
        using var scratch = new TemporaryDirectory(prefix: "puck-mesh-store-");
        var directory = scratch.PathOf(name: "meshes");
        var entry = File.ReadAllBytes(path: WrittenEntry(directory: scratch.PathOf(name: "sizing")));
        // Room for three entries.
        var store = new SdfMeshStore(capacityBytes: ((3 * entry.Length) + 1), directory: directory);
        var old = DateTime.UtcNow.AddHours(value: -1);

        // A foreign file is no entry and stays; a temporary file an interrupted write left an hour ago goes, a fresh one
        // stays.
        _ = Directory.CreateDirectory(path: directory);
        File.WriteAllText(contents: "notes", path: Path.Combine(path1: directory, path2: "notes.txt"));
        File.WriteAllText(contents: "partial", path: Path.Combine(path1: directory, path2: "abandoned.mesh.0.tmp"));
        File.SetLastWriteTimeUtc(lastWriteTimeUtc: old, path: Path.Combine(path1: directory, path2: "abandoned.mesh.0.tmp"));
        File.WriteAllText(contents: "partial", path: Path.Combine(path1: directory, path2: "writing.mesh.1.tmp"));

        for (var hash = 0; (hash < 3); hash++) {
            Assert.True(condition: store.Write(geometry: (Geometry with { Hash = (UInt128)hash }), identity: Identity, materials: [], mesh: Mesh()));
            File.SetLastWriteTimeUtc(lastWriteTimeUtc: old.AddMinutes(value: hash), path: Entry(store: store, geometry: (Geometry with { Hash = (UInt128)hash })));
        }

        // Reading the oldest makes it the most recent, so the fourth write evicts the second and third, leaving three
        // quarters of the capacity.
        Assert.True(condition: store.TryRead(geometry: (Geometry with { Hash = 0 }), identity: Identity, materials: out _, mesh: out _, refusal: out _));
        Assert.True(condition: store.Write(geometry: (Geometry with { Hash = 3 }), identity: Identity, materials: [], mesh: Mesh()));

        Assert.True(condition: store.Contains(geometry: (Geometry with { Hash = 0 }), identity: Identity));
        Assert.False(condition: store.Contains(geometry: (Geometry with { Hash = 1 }), identity: Identity));
        Assert.False(condition: store.Contains(geometry: (Geometry with { Hash = 2 }), identity: Identity));
        Assert.True(condition: store.Contains(geometry: (Geometry with { Hash = 3 }), identity: Identity));
        Assert.True(condition: File.Exists(path: Path.Combine(path1: directory, path2: "notes.txt")));
        Assert.False(condition: File.Exists(path: Path.Combine(path1: directory, path2: "abandoned.mesh.0.tmp")));
        Assert.True(condition: File.Exists(path: Path.Combine(path1: directory, path2: "writing.mesh.1.tmp")));

        // Under the capacity, a write counts its bytes without looking at the directory.
        Assert.True(condition: store.Write(geometry: (Geometry with { Hash = 4 }), identity: Identity, materials: [], mesh: Mesh()));
        Assert.True(condition: store.Contains(geometry: (Geometry with { Hash = 0 }), identity: Identity));
    }
    [Fact]
    public void ABakesIdentityMovesWithItsKernelsAndNothingElse() {
        var bytes = SdfKernelSet.Kernels.Select(selector: static kernel => new ReadOnlyMemory<byte>(array: [(byte)kernel])).ToArray();
        var identity = SdfMeshStore.IdentityOf(kernels: new SdfKernelSet(bytecode: bytes));
        var views = bytes.ToArray();
        var bake = bytes.ToArray();

        views[(int)SdfKernel.Views] = new byte[] { 200 };
        bake[(int)SdfKernel.MeshBake] = new byte[] { 200 };

        Assert.NotEqual(expected: UInt128.Zero, actual: SdfMeshStore.CodeIdentity);
        Assert.Equal(expected: identity, actual: SdfMeshStore.IdentityOf(kernels: new SdfKernelSet(bytecode: views)));
        Assert.NotEqual(expected: identity, actual: SdfMeshStore.IdentityOf(kernels: new SdfKernelSet(bytecode: bake)));
        Assert.Throws<ArgumentNullException>(testCode: static () => SdfMeshStore.IdentityOf(kernels: null!));
        Assert.Throws<ArgumentOutOfRangeException>(testCode: static () => new SdfMeshStore(capacityBytes: 0, directory: "meshes"));
    }

    // A two-triangle mesh with normals and triangle materials, as a bake meshes one.
    internal static SdfMesh Mesh() => new(
        indices: new uint[] { 0, 1, 2, 0, 2, 1 },
        normals: new Vector3[] { Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ },
        positions: new Vector3[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY },
        triangleMaterials: new uint[] { 4u, 9u }
    );
    // The file an entry is kept in: the name a write of it to a store of its own takes.
    private static string Entry(SdfMeshStore store, SdfMeshGeometry geometry) {
        var probe = new SdfMeshStore(directory: Path.Combine(path1: store.Directory, path2: "probe"));

        _ = probe.Write(geometry: geometry, identity: Identity, materials: [], mesh: Mesh());

        var name = Path.GetFileName(path: Directory.GetFiles(path: probe.Directory).Single());

        Directory.Delete(path: probe.Directory, recursive: true);

        return Path.Combine(path1: store.Directory, path2: name);
    }
    private static string WrittenEntry(string directory) {
        var store = new SdfMeshStore(directory: directory);

        _ = store.Write(geometry: Geometry, identity: Identity, materials: [], mesh: Mesh());

        return Directory.GetFiles(path: directory).Single();
    }
    private static byte[] Flip(byte[] bytes, int at) {
        var flipped = bytes.ToArray();

        flipped[at] ^= 0x40;

        return flipped;
    }
    // The entry with its body edited and its checksum made good again, so the edit itself is what the store judges.
    private static byte[] Resealed(byte[] bytes, SpanAction edit) {
        var resealed = bytes.ToArray();
        var body = resealed.AsSpan(length: (resealed.Length - 16), start: 0);

        edit(body: body);
        BinaryPrimitives.WriteUInt128LittleEndian(destination: resealed.AsSpan(start: body.Length), value: XxHash128.HashToUInt128(source: body));

        return resealed;
    }
    private delegate void SpanAction(Span<byte> body);
}
