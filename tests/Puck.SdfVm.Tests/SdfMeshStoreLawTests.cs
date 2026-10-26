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
        var mesh = Mesh() with { BakeCell = new Vector3(0.01f, 0.02f, 0.03f), Levels = [Level()] };

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
        Assert.Equal(mesh.BakeCell, read.BakeCell);

        var level = Assert.Single(collection: read.Levels);

        Assert.Equal(expected: Level().Indices.ToArray(), actual: level.Indices.ToArray());
        Assert.Equal(expected: Level().TriangleMaterials.ToArray(), actual: level.TriangleMaterials.ToArray());
        Assert.Equal(expected: Level().Error, actual: level.Error);

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
            ("holds invalid compressed mesh data", Resealed(bytes: sound, edit: static body => BinaryPrimitives.WriteInt32LittleEndian(destination: body[24..], value: -1))),
            ("holds invalid compressed mesh data", Resealed(bytes: sound, edit: static body => BinaryPrimitives.WriteInt32LittleEndian(destination: body[24..], value: int.MaxValue))),
            ("holds invalid compressed mesh data", Resealed(bytes: sound, edit: static body => body[28..].Clear())),
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
    public void CompactStreamsPreserveTopologySkinAndBakeErrorAcrossChunks() {
        using var scratch = new TemporaryDirectory(prefix: "puck-mesh-store-");
        var store = new SdfMeshStore(directory: scratch.PathOf(name: "meshes"));
        var positions = Enumerable.Range(0, 300).Select(index => new Vector3(index * 0.017123f, MathF.Sin(index) * 0.43f, MathF.Cos(index) * 0.37f)).ToArray();
        var normals = positions.Select(position => Vector3.Normalize(position + new Vector3(0.2f))).ToArray();
        var indices = Enumerable.Range(0, 300).Select(index => (uint)index).ToArray();
        var palette = Enumerable.Range(0, 100).Select(index => (uint)(index % 3)).ToArray();
        var mesh = new SdfMesh(positions, indices, normals, palette,
            new SdfMeshMember[] { new(2, new Vector3(0.3f), Quaternion.Identity) },
            Enumerable.Range(0, 300).Select(index => index % 2 == 0 ? (byte)0 : SdfMesh.NoMember).ToArray()) {
            BakeCell = new Vector3(0.1f),
            Levels = [new SdfMeshLevel(indices.AsMemory(0, 30), palette.AsMemory(0, 10), 0.2f)],
        };
        foreach (var cell in new[] { new Vector3(0.1f), new Vector3(0.0001f), Vector3.Zero }) {
            var source = mesh with { BakeCell = cell };
            Assert.True(store.Write(Identity, Geometry, source, [3, 7]));
            Assert.True(store.TryRead(Identity, Geometry, out var decoded, out _, out _));
            for (var index = 0; index < positions.Length; index++) {
                Assert.InRange(Vector3.Distance(positions[index], decoded.Positions.Span[index]), 0f, cell.Length() / 1024f);
                Assert.InRange(Vector3.Distance(normals[index], decoded.Normals.Span[index]), 0f, 0.0001f);
            }
            Assert.Equal(indices, decoded.Indices.ToArray());
            Assert.Equal(palette, decoded.TriangleMaterials.ToArray());
            Assert.Equal(mesh.Members.ToArray(), decoded.Members.ToArray());
            Assert.Equal(mesh.VertexMembers.ToArray(), decoded.VertexMembers.ToArray());
            Assert.Equal(mesh.Levels[0].Indices.ToArray(), decoded.Levels[0].Indices.ToArray());
        }
        var authored = new SdfMesh(positions.AsMemory(0, 3), new uint[] { 0, 2, 1 },
            new Vector3[] { Vector3.Zero, new(2, 3, 4), Vector3.UnitX }, new uint[] { uint.MaxValue });
        var chunks = SdfMeshChunks.Join([authored, authored]);
        Assert.True(store.Write(Identity, Geometry, chunks, []));
        Assert.True(store.TryRead(Identity, Geometry, out var joined, out _, out _));
        Assert.Equal(2, joined.Chunks.Count);
        Assert.Equal(authored.Normals.ToArray(), joined.Chunks[0].Normals.ToArray());
        Assert.Equal(authored.TriangleMaterials.ToArray(), joined.Chunks[1].TriangleMaterials.ToArray());
    }
    [Theory]
    [InlineData(0.1f)]
    [InlineData(0.0001f)]
    [InlineData(0f)]
    public void ChunkSeamsUseOneGridWithinEveryPiecesBudget(float secondCell) {
        using var scratch = new TemporaryDirectory(prefix: "puck-mesh-store-");
        var store = new SdfMeshStore(directory: scratch.PathOf(name: "meshes"));
        var shared = new Vector3(0.371234f, -0.219876f, 0.517293f);
        // The common vertex is not a bound on either piece. Independently rounded grids disagree here.
        var first = new SdfMesh(new Vector3[] { new(-1f), new(2f), shared }, new uint[] { 0, 1, 2 }) { BakeCell = new Vector3(0.1f) };
        var second = new SdfMesh(new Vector3[] { new(-2f), new(3f), shared }, new uint[] { 0, 1, 2 }) { BakeCell = new Vector3(secondCell) };
        var source = SdfMeshChunks.Join([first, second]);
        Assert.True(store.Write(Identity, Geometry, source, []));
        Assert.True(store.TryRead(Identity, Geometry, out var decoded, out _, out _));
        Assert.Equal(decoded.Chunks[0].Positions.Span[2], decoded.Chunks[1].Positions.Span[2]);
        for (var chunk = 0; chunk < source.Chunks.Count; chunk++) {
            var original = source.Chunks[chunk];
            var read = decoded.Chunks[chunk];
            Assert.Equal(original.Indices.ToArray(), read.Indices.ToArray());
            for (var vertex = 0; vertex < original.Positions.Length; vertex++) {
                Assert.InRange(Vector3.Distance(original.Positions.Span[vertex], read.Positions.Span[vertex]), 0f, original.BakeCell.Length() / 1024f);
            }
        }
    }
    [Theory]
    [InlineData(65536)]
    [InlineData(65537)]
    public void IndexWidthsPreserveTheirBoundaryVertex(int vertices) {
        using var scratch = new TemporaryDirectory(prefix: "puck-mesh-store-");
        var store = new SdfMeshStore(directory: scratch.PathOf(name: "meshes"));
        var positions = new Vector3[vertices];
        positions[^1] = Vector3.UnitY;
        var mesh = new SdfMesh(positions, new uint[] { 0, 1, (uint)(vertices - 1) });
        Assert.True(store.Write(Identity, Geometry, mesh, []));
        Assert.True(store.TryRead(Identity, Geometry, out var read, out _, out _));
        Assert.Equal(mesh.Indices.ToArray(), read.Indices.ToArray());
        Assert.Equal(positions, read.Positions.ToArray());
        Assert.Empty(read.Normals.ToArray());
        Assert.Empty(read.TriangleMaterials.ToArray());
    }
    [Fact]
    public void AWritePastTheCapacityRemovesTheLeastRecentlyUsedEntries() {
        using var scratch = new TemporaryDirectory(prefix: "puck-mesh-store-");
        var directory = scratch.PathOf(name: "meshes");
        var entry = File.ReadAllBytes(path: WrittenEntry(directory: scratch.PathOf(name: "sizing")));
        var old = DateTime.UtcNow.AddHours(value: -1);

        // Two entries a build that kept them in no set left, the older first, each a set of its own, and a file that is
        // no entry.
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
    // Each identity's entries are one set, removed whole, least recently used first, whose last use is its most recently
    // used entry's: a set another World is loading or baking (an entry of it stamped just now) stays whole beside a write,
    // where entry by entry its older entries would go, and the set that goes, goes entire.
    [Fact]
    public void AWritePastTheCapacityRemovesWholeSetsLeastRecentlyUsedFirst() {
        using var scratch = new TemporaryDirectory(prefix: "puck-mesh-store-");
        var directory = scratch.PathOf(name: "meshes");
        var entry = File.ReadAllBytes(path: WrittenEntry(directory: scratch.PathOf(name: "sizing")));
        var store = new SdfMeshStore(capacityBytes: (3 * entry.Length), directory: directory);
        var now = DateTime.UtcNow;
        var active = store.SetDirectoryOf(identity: 1);
        var idle = store.SetDirectoryOf(identity: 2);

        void Place(string set, string name, int minutesAgo) {
            var path = Path.Combine(path1: set, path2: name);

            _ = Directory.CreateDirectory(path: set);
            File.WriteAllBytes(bytes: entry, path: path);
            File.SetLastWriteTimeUtc(lastWriteTimeUtc: now.AddMinutes(value: -minutesAgo), path: path);
        }

        // The active set's oldest entry is older than either of the idle set's, but one of its entries was just read.
        Place(set: active, name: "a.mesh", minutesAgo: 60);
        Place(set: active, name: "b.mesh", minutesAgo: 1);
        Place(set: idle, name: "c.mesh", minutesAgo: 30);
        Place(set: idle, name: "d.mesh", minutesAgo: 20);

        Assert.True(condition: store.Write(geometry: Geometry, identity: Identity, materials: [], mesh: Mesh()));
        Assert.True(condition: store.Contains(geometry: Geometry, identity: Identity));
        Assert.True(condition: File.Exists(path: Path.Combine(path1: active, path2: "a.mesh")));
        Assert.True(condition: File.Exists(path: Path.Combine(path1: active, path2: "b.mesh")));
        Assert.False(condition: Directory.Exists(path: idle));
        Assert.Equal(expected: 3, actual: store.EntryPaths().Count);
    }
    // The set a write belongs to stays whatever its stamps; when it alone holds more than the capacity, its own least
    // recently used entries go, and the one just written stays.
    [Fact]
    public void ASetLargerThanTheCapacityKeepsItsMostRecentlyUsedEntries() {
        using var scratch = new TemporaryDirectory(prefix: "puck-mesh-store-");
        var directory = scratch.PathOf(name: "meshes");
        var entry = File.ReadAllBytes(path: WrittenEntry(directory: scratch.PathOf(name: "sizing")));
        var store = new SdfMeshStore(capacityBytes: (2 * entry.Length), directory: directory);
        var own = store.SetDirectoryOf(identity: Identity);
        var now = DateTime.UtcNow;

        _ = Directory.CreateDirectory(path: own);
        foreach (var (name, minutesAgo) in ((ReadOnlySpan<(string, int)>)[("older.mesh", 60), ("newer.mesh", 30)])) {
            File.WriteAllBytes(bytes: entry, path: Path.Combine(path1: own, path2: name));
            File.SetLastWriteTimeUtc(lastWriteTimeUtc: now.AddMinutes(value: -minutesAgo), path: Path.Combine(path1: own, path2: name));
        }

        Assert.True(condition: store.Write(geometry: Geometry, identity: Identity, materials: [], mesh: Mesh()));
        Assert.True(condition: store.Contains(geometry: Geometry, identity: Identity));
        Assert.False(condition: File.Exists(path: Path.Combine(path1: own, path2: "older.mesh")));
        Assert.True(condition: File.Exists(path: Path.Combine(path1: own, path2: "newer.mesh")));
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
        Assert.Equal(expected: "PUCKMSH8", actual: SdfMeshStore.EntryVersion);
        Assert.Equal(expected: SdfMeshStore.EntryVersion, actual: identity.EntryVersion);
        Assert.Equal(expected: string.Empty, actual: new SdfMeshBakeIdentity(value: 5).EntryVersion);
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
        var kernels = new SdfKernelSet(bytecode: bytes);
        var first = SdfMeshStore.IdentityOf(kernels: kernels);

        // Nothing recorded: the store names no earlier identity, then records this one.
        Assert.Contains(expectedSubstring: "records no earlier identity", actualString: store.ExplainMisses(identity: first));
        Assert.Contains(expectedSubstring: "unchanged: its set was evicted, or it never stored one (0 entries in 0 sets", actualString: store.ExplainMisses(identity: first));
        Assert.True(condition: store.Write(geometry: Geometry, identity: first.Value, materials: [], mesh: Mesh()));
        Assert.Contains(expectedSubstring: "unchanged: geometry new to its set of 1 entries (1 entries in 1 sets", actualString: store.ExplainMisses(identity: first));
        Assert.Contains(expectedSubstring: "parts unknown", actualString: store.ExplainMisses(identity: new SdfMeshBakeIdentity(value: 5)));
        store.Record(identity: new SdfMeshBakeIdentity(value: 5));
        Assert.Single(collection: Directory.GetFiles(path: store.Directory, searchPattern: ("*" + SdfMeshStore.IdentityExtension)));

        // An entry-version bump moves the identity to a new key, misses cleanly without refusing or overwriting the earlier
        // entry, and names the version bump as the miss's cause.
        var bumpedBuild = scratch.PathOf(name: "bumped");

        _ = Directory.CreateDirectory(path: bumpedBuild);
        foreach (var dll in Directory.GetFiles(path: AppContext.BaseDirectory, searchPattern: "Puck.*.dll")) {
            var destination = Path.Combine(path1: bumpedBuild, path2: Path.GetFileName(path: dll));

            if (string.Equals(a: Path.GetFileName(path: dll), b: "Puck.SdfVm.dll", comparisonType: StringComparison.Ordinal)) {
                var image = File.ReadAllBytes(path: dll);
                var oldVersion = System.Text.Encoding.ASCII.GetBytes(s: SdfMeshStore.EntryVersion);
                var newVersion = System.Text.Encoding.ASCII.GetBytes(s: "PUCKMSH9");
                var replaced = 0;

                for (var index = 0; (index <= (image.Length - oldVersion.Length)); index++) {
                    if (image.AsSpan(start: index, length: oldVersion.Length).SequenceEqual(other: oldVersion)) {
                        newVersion.CopyTo(destination: image.AsSpan(start: index, length: newVersion.Length));
                        replaced++;
                    }
                }
                Assert.True(condition: (replaced >= 1));
                File.WriteAllBytes(bytes: image, path: destination);
            } else {
                File.Copy(destFileName: destination, sourceFileName: dll);
            }
        }

        var bumped = SdfMeshBakeIdentity.Of(code: SdfMeshBakeClosure.Of(directory: bumpedBuild), kernels: kernels);

        Assert.Equal(expected: "PUCKMSH9", actual: bumped.EntryVersion);
        Assert.NotEqual(expected: first.Value, actual: bumped.Value);
        Assert.False(condition: store.TryRead(geometry: Geometry, identity: bumped.Value, materials: out _, mesh: out _, refusal: out var bumpedRefusal));
        Assert.Null(@object: bumpedRefusal);
        Assert.True(condition: store.Contains(geometry: Geometry, identity: first.Value));
        Assert.Equal(
            expected: $"identity {bumped.Value:x32} new since {first.Value:x32}: the entry version ({first.EntryVersion} -> PUCKMSH9) changed",
            actual: store.ExplainMisses(identity: bumped)
        );
        File.Delete(path: Path.Combine(path1: store.Directory, path2: (bumped.Value.ToString(format: "x32", provider: null) + SdfMeshStore.IdentityExtension)));

        // Another bake kernel is named as the bake kernel, against the identity it shares the code and mask with.
        var rebaked = bytes.ToArray();

        rebaked[(int)SdfKernel.MeshBake] = new byte[] { 200 };

        var kernelChange = store.ExplainMisses(identity: SdfMeshStore.IdentityOf(kernels: new SdfKernelSet(bytecode: rebaked)));

        Assert.Contains(expectedSubstring: "the bake kernel's bytecode changed", actualString: kernelChange);
        Assert.DoesNotContain(expectedSubstring: "mask", actualString: kernelChange);

        // A recorded identity whose code differs names the code's changed members by assembly: here every member, since
        // the earlier record holds none of them, and a file of another format is passed over.
        var path = Path.Combine(path1: store.Directory, path2: ("0".PadLeft(totalWidth: 32, paddingChar: '0') + SdfMeshStore.IdentityExtension));

        File.WriteAllText(contents: $"puck.mesh-identity.v1\nversion {first.EntryVersion}\ncode {(first.Code + 1):x32}\nmask {first.MaskKernel:x32}\nbake {first.BakeKernel:x32}\n{0:x32} Puck.Gone!Gone.Member`0()Void\n", path: path);
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
    private static SdfMeshLevel Level() => new(Error: 0.25f, Indices: new uint[] { 0, 1, 2 }, TriangleMaterials: new uint[] { 9u });
    // The file an entry is kept in: the name a write of it to a store of its own takes.
    private static string Entry(SdfMeshStore store, SdfMeshGeometry geometry) {
        var probe = new SdfMeshStore(directory: Path.Combine(path1: store.Directory, path2: "probe"));

        _ = probe.Write(geometry: geometry, identity: Identity, materials: [], mesh: Mesh());

        var name = Path.GetFileName(path: Directory.GetFiles(path: probe.Directory, searchOption: SearchOption.AllDirectories, searchPattern: "*").Single());

        Directory.Delete(path: probe.Directory, recursive: true);

        return Path.Combine(path1: store.SetDirectoryOf(identity: Identity), path2: name);
    }
    private static string WrittenEntry(string directory) {
        var store = new SdfMeshStore(directory: directory);

        _ = store.Write(geometry: Geometry, identity: Identity, materials: [], mesh: Mesh());

        return Directory.GetFiles(path: directory, searchOption: SearchOption.AllDirectories, searchPattern: "*").Single();
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
