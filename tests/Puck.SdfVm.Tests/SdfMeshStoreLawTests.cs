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
    public void AWritePastTheCapacityRemovesTheLeastRecentlyUsedEntries() {
        using var scratch = new TemporaryDirectory(prefix: "puck-mesh-store-");
        var directory = scratch.PathOf(name: "meshes");
        var entry = File.ReadAllBytes(path: WrittenEntry(directory: scratch.PathOf(name: "sizing")));
        var old = DateTime.UtcNow.AddHours(value: -1);

        // Two entries an earlier run left, the older first, and a file that is no entry.
        _ = Directory.CreateDirectory(path: directory);
        File.WriteAllBytes(bytes: entry, path: Path.Combine(path1: directory, path2: "older.mesh"));
        File.SetLastWriteTimeUtc(lastWriteTimeUtc: old, path: Path.Combine(path1: directory, path2: "older.mesh"));
        File.WriteAllBytes(bytes: entry, path: Path.Combine(path1: directory, path2: "newer.mesh"));
        File.SetLastWriteTimeUtc(lastWriteTimeUtc: old.AddMinutes(value: 1), path: Path.Combine(path1: directory, path2: "newer.mesh"));
        File.WriteAllText(contents: "notes", path: Path.Combine(path1: directory, path2: "notes.txt"));

        // Room for two entries: the write keeps itself and the more recent of the two.
        var store = new SdfMeshStore(capacityBytes: (2 * entry.Length), directory: directory);

        Assert.True(condition: store.Write(geometry: Geometry, identity: Identity, materials: [], mesh: Mesh()));
        Assert.True(condition: store.Contains(geometry: Geometry, identity: Identity));
        Assert.False(condition: File.Exists(path: Path.Combine(path1: directory, path2: "older.mesh")));
        Assert.True(condition: File.Exists(path: Path.Combine(path1: directory, path2: "newer.mesh")));
        Assert.True(condition: File.Exists(path: Path.Combine(path1: directory, path2: "notes.txt")));
    }
    [Fact]
    public void ABakesIdentityMovesWithItsKernelsAndNothingElse() {
        var bytes = SdfKernelSet.Kernels.Select(selector: static kernel => new ReadOnlyMemory<byte>(array: [(byte)kernel])).ToArray();
        var identity = SdfMeshStore.IdentityOf(kernels: new SdfKernelSet(bytecode: bytes));
        var views = bytes.ToArray();
        var bake = bytes.ToArray();
        var mask = bytes.ToArray();

        views[(int)SdfKernel.Views] = new byte[] { 200 };
        bake[(int)SdfKernel.MeshBake] = new byte[] { 200 };
        mask[(int)SdfKernel.MeshBakeMask] = new byte[] { 200 };

        Assert.True(condition: identity.HasParts);
        Assert.Equal(expected: SdfMeshBakeClosure.Current.Hash, actual: identity.Code);
        Assert.Equal(expected: identity.Value, actual: SdfMeshStore.IdentityOf(kernels: new SdfKernelSet(bytecode: views)).Value);
        Assert.NotEqual(expected: identity.Value, actual: SdfMeshStore.IdentityOf(kernels: new SdfKernelSet(bytecode: bake)).Value);
        Assert.NotEqual(expected: identity.Value, actual: SdfMeshStore.IdentityOf(kernels: new SdfKernelSet(bytecode: mask)).Value);
        Assert.Equal(expected: identity.Value, actual: SdfMeshStore.BuildIdentityOf(bakeKernel: identity.BakeKernel, code: identity.Code, maskKernel: identity.MaskKernel));
        Assert.Throws<ArgumentNullException>(testCode: static () => SdfMeshStore.IdentityOf(kernels: null!));
        Assert.Throws<ArgumentNullException>(testCode: static () => SdfMeshBakeIdentity.Of(code: null!, kernels: null!));
        Assert.Throws<ArgumentOutOfRangeException>(testCode: static () => new SdfMeshStore(capacityBytes: 0, directory: "meshes"));
    }
    [Fact]
    public void AMissNamesThePartOfTheIdentityThatChanged() {
        using var scratch = new TemporaryDirectory(prefix: "puck-mesh-store-");
        var store = new SdfMeshStore(directory: scratch.PathOf(name: "meshes"));
        var bytes = SdfKernelSet.Kernels.Select(selector: static kernel => new ReadOnlyMemory<byte>(array: [(byte)kernel])).ToArray();
        var first = SdfMeshStore.IdentityOf(kernels: new SdfKernelSet(bytecode: bytes));

        // Nothing recorded: the store names no earlier identity, then records this one.
        Assert.Contains(expectedSubstring: "records no earlier identity", actualString: store.ExplainMisses(identity: first));
        Assert.Contains(expectedSubstring: "unchanged", actualString: store.ExplainMisses(identity: first));
        Assert.Contains(expectedSubstring: "parts unknown", actualString: store.ExplainMisses(identity: new SdfMeshBakeIdentity(value: 5)));
        store.Record(identity: new SdfMeshBakeIdentity(value: 5));
        Assert.Single(collection: Directory.GetFiles(path: store.Directory, searchPattern: ("*" + SdfMeshStore.IdentityExtension)));

        // Another bake kernel is named as the bake kernel, against the identity it shares the code and mask with.
        var rebaked = bytes.ToArray();

        rebaked[(int)SdfKernel.MeshBake] = new byte[] { 200 };

        var kernelChange = store.ExplainMisses(identity: SdfMeshStore.IdentityOf(kernels: new SdfKernelSet(bytecode: rebaked)));

        Assert.Contains(expectedSubstring: "the bake kernel's bytecode changed", actualString: kernelChange);
        Assert.DoesNotContain(expectedSubstring: "mask", actualString: kernelChange);

        // A recorded identity whose code differs names the code's changed members by assembly: here every member, since
        // the earlier record holds none of them, and a file of another format is passed over.
        var path = Path.Combine(path1: store.Directory, path2: ("0".PadLeft(totalWidth: 32, paddingChar: '0') + SdfMeshStore.IdentityExtension));

        File.WriteAllText(contents: $"puck.mesh-identity.v1\ncode {(first.Code + 1):x32}\nmask {first.MaskKernel:x32}\nbake {first.BakeKernel:x32}\n{0:x32} Puck.Gone!Gone.Member`0()Void\n", path: path);
        File.WriteAllText(contents: "another format\n", path: Path.Combine(path1: store.Directory, path2: ("1".PadLeft(totalWidth: 32, paddingChar: '0') + SdfMeshStore.IdentityExtension)));
        File.Delete(path: Path.Combine(path1: store.Directory, path2: (first.Value.ToString(format: "x32", provider: null) + SdfMeshStore.IdentityExtension)));

        var codeChange = store.ExplainMisses(identity: first);

        Assert.Contains(expectedSubstring: "the code,", actualString: codeChange);
        Assert.Contains(expectedSubstring: "Puck.SdfVm", actualString: codeChange);
        Assert.Contains(expectedSubstring: "Puck.Gone 1", actualString: codeChange);
    }

    [Fact]
    public void APeekLeavesTheStoreAsItIsAndAnEntryReadsUnderAnyKey() {
        using var scratch = new TemporaryDirectory(prefix: "puck-mesh-store-");
        var store = new SdfMeshStore(directory: scratch.PathOf(name: "meshes"));

        Assert.Empty(collection: store.EntryPaths());
        Assert.True(condition: store.Write(geometry: Geometry, identity: Identity, materials: [4, 9], mesh: Mesh()));

        var path = Assert.Single(collection: store.EntryPaths());
        var stamped = DateTime.UtcNow.AddHours(value: -1);

        File.SetLastWriteTimeUtc(lastWriteTimeUtc: stamped, path: path);
        Assert.True(condition: store.TryPeek(geometry: Geometry, identity: Identity, materials: out var materials, mesh: out var mesh, refusal: out _));
        Assert.Equal(expected: [4, 9], actual: materials);
        Assert.Equal(expected: Mesh().Positions.ToArray(), actual: mesh.Positions.ToArray());
        Assert.Equal(expected: stamped, actual: File.GetLastWriteTimeUtc(path: path));
        Assert.True(condition: SdfMeshStore.TryReadFile(materials: out var any, mesh: out _, path: path));
        Assert.Equal(expected: [4, 9], actual: any);

        // A damaged entry is refused and stays where it was, and a file that is no entry reads as none.
        File.WriteAllBytes(bytes: File.ReadAllBytes(path: path)[..20], path: path);
        Assert.False(condition: store.TryPeek(geometry: Geometry, identity: Identity, materials: out _, mesh: out _, refusal: out var why));
        Assert.Contains(expectedSubstring: "is truncated", actualString: why);
        Assert.True(condition: File.Exists(path: path));
        Assert.False(condition: SdfMeshStore.TryReadFile(materials: out _, mesh: out _, path: path));
        Assert.False(condition: SdfMeshStore.TryReadFile(materials: out _, mesh: out _, path: scratch.PathOf(name: "absent.mesh")));
    }
    [Fact]
    public void ABakesCodeIsTheClosureOfWhatItRunsReadFromItsBuild() {
        var closure = SdfMeshBakeClosure.Of(directory: AppContext.BaseDirectory);
        var members = closure.Members.Keys;

        Assert.Equal(expected: SdfMeshBakeClosure.Current.Hash, actual: closure.Hash);
        // The bake's own code, the contouring it calls, and the maths that calls into: each a member, and its layouts.
        Assert.Contains(collection: members, filter: static member => member.StartsWith(value: "Puck.SdfVm!Puck.SdfVm.SdfMeshPathBake.", comparisonType: StringComparison.Ordinal));
        Assert.Contains(collection: members, filter: static member => member.StartsWith(value: "Puck.SignedDistance!Puck.SignedDistance.Baking.SdfDualContouring.", comparisonType: StringComparison.Ordinal));
        Assert.Contains(collection: members, filter: static member => member.EndsWith(value: " [layout]", comparisonType: StringComparison.Ordinal));
        // What holds meshes rather than makes them, and the renderer's other passes, are not.
        Assert.DoesNotContain(collection: members, filter: static member => member.StartsWith(value: "Puck.SdfVm!Puck.SdfVm.SdfMeshLibrary", comparisonType: StringComparison.Ordinal));
        Assert.DoesNotContain(collection: members, filter: static member => member.StartsWith(value: "Puck.SdfVm!Puck.SdfVm.SdfMeshIrradiance", comparisonType: StringComparison.Ordinal));
        Assert.DoesNotContain(collection: members, filter: static member => member.StartsWith(value: "Puck.Abstractions!Puck.Abstractions.CacheRetention", comparisonType: StringComparison.Ordinal));

        var edited = closure.Members.ToDictionary();
        var first = members.First();

        edited[first] += 1;
        edited.Remove(key: members.Last());
        edited["Puck.Gone!Gone.Member"] = 1;
        Assert.Equal(expected: new[] { first, members.Last(), "Puck.Gone!Gone.Member" }.Order(comparer: StringComparer.Ordinal), actual: SdfMeshBakeClosure.Changed(earlier: edited, members: closure.Members));
        Assert.Empty(collection: SdfMeshBakeClosure.Changed(earlier: closure.Members, members: closure.Members));

        using var scratch = new TemporaryDirectory(prefix: "puck-mesh-store-");

        _ = Assert.Throws<FileNotFoundException>(testCode: () => SdfMeshBakeClosure.Of(directory: scratch.RootPath));
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
