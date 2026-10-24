using System.Buffers.Binary;
using System.IO.Hashing;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Puck.SdfVm;

/// <summary>
/// The baked meshes a machine keeps between runs, one file an entry under <see cref="Directory"/>, so a World that boots
/// a program it baked before takes each group's mesh from disk instead of baking it again
/// (<see cref="SdfMeshLibrary.TryLoad"/>), and a changed creation bakes only its own groups. Presentation alone reads it:
/// a mesh is drawn, never simulated, so nothing a run computes depends on what the store holds.
/// <para>An entry is keyed by the bake's identity (<see cref="IdentityOf"/>: the bake kernels' bytecode and the code that
/// samples, contours and meshes them, <see cref="SdfMeshBakeClosure"/>) and the group's geometry
/// (<see cref="SdfMeshGeometryBuilder"/>), so a change to either reads no entry the other made. An entry names its key and
/// ends with a checksum of its bytes; one that is truncated, damaged, another key's or not a mesh
/// (<see cref="SdfMesh"/> refuses it) is refused by name and deleted, and its group bakes. Writes replace an entry
/// atomically, so concurrent Worlds may share a directory.</para>
/// <para>Each identity's entries are one set, kept in a directory named for the identity, and the store holds at most
/// <see cref="CapacityBytes"/> bytes a set at a time, under Puck's one cache retention
/// (<see cref="Puck.Abstractions.CacheRetention.EnforceSetsOnWrite"/>): a read stamps an entry, a set's last use is its
/// most recently used entry's, and a write removes whole sets, least recently used first, keeping the writer's own. So a
/// set a World is baking or loading is never cut short by another's write, and every set the store keeps is whole. A
/// file directly in the directory, which no identity's set holds, is a set of its own and ages out first once nothing
/// reads it. Its members are thread-safe.</para>
/// </summary>
public sealed class SdfMeshStore {
    /// <summary>The bytes a store holds at most unless its owner names another bound: eight gibibytes. The debug world's
    /// meshes fill about 1.26 GB in about 1,150 entries under one identity and backend, and one per-user store serves
    /// every World and worktree on a machine, so this keeps six such sets whole; a set larger than the bound alone keeps
    /// its most recently used entries.</summary>
    public const long DefaultCapacityBytes = (8L << 30);
    /// <summary>The extension of an entry's file.</summary>
    public const string Extension = ".mesh";

    // "PUCKMSH3": counts followed by the contour cell. Retired cache entries are derived again.
    private const ulong Magic = 0x3348534D4B435550ul;
    // The magic, the key, and seven counts: vertices, normals, indices, triangle materials, the group's materials, members
    // and vertex members.
    private const int HeaderBytes = (sizeof(ulong) + 16 + (7 * sizeof(int)) + (3 * sizeof(float)));
    // A member's bytes: its slot offset, its position and its orientation.
    private const int MemberBytes = (sizeof(int) + (7 * sizeof(float)));
    private const int ChecksumBytes = 16;
    // The most sets a store keeps, past any machine's builds many times over: its bytes bound it first.
    private const int MaxSets = 4_096;

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

    /// <summary>Returns the identity of this process's bake on a kernel set: the code it runs
    /// (<see cref="SdfMeshBakeClosure.Current"/>) and its bake kernels' bytecode.</summary>
    /// <param name="kernels">The kernel set the bake dispatches.</param>
    /// <returns>The identity.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="kernels"/> is <see langword="null"/>.</exception>
    public static SdfMeshBakeIdentity IdentityOf(SdfKernelSet kernels) => SdfMeshBakeIdentity.Of(code: SdfMeshBakeClosure.Current, kernels: kernels);
    /// <summary>Returns an identity's value from its parts, as <see cref="SdfMeshBakeIdentity.Of"/> combines them.</summary>
    /// <param name="code">The code identity (<see cref="SdfMeshBakeClosure.Hash"/>).</param>
    /// <param name="maskKernel">The hash of the mask kernel's bytecode.</param>
    /// <param name="bakeKernel">The hash of the bake kernel's bytecode.</param>
    /// <returns>The identity's value.</returns>
    public static UInt128 BuildIdentityOf(UInt128 code, UInt128 maskKernel, UInt128 bakeKernel) {
        Span<byte> bytes = stackalloc byte[48];

        BinaryPrimitives.WriteUInt128LittleEndian(destination: bytes, value: code);
        BinaryPrimitives.WriteUInt128LittleEndian(destination: bytes[16..], value: maskKernel);
        BinaryPrimitives.WriteUInt128LittleEndian(destination: bytes[32..], value: bakeKernel);

        return XxHash128.HashToUInt128(source: bytes);
    }
    /// <summary>Returns whether the store holds an entry for a geometry under a bake's identity, without reading it.</summary>
    /// <param name="identity">The bake's identity.</param>
    /// <param name="geometry">The group's geometry.</param>
    /// <returns><see langword="true"/> when an entry's file exists.</returns>
    public bool Contains(UInt128 identity, SdfMeshGeometry geometry) => File.Exists(path: PathOf(identity: identity, key: KeyOf(geometry: geometry, identity: identity)));
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
        var path = PathOf(identity: identity, key: KeyOf(geometry: geometry, identity: identity));

        if (!TryReadEntry(damaged: out var damaged, geometry: geometry, identity: identity, materials: out materials, mesh: out mesh, refusal: out refusal)) {
            if (damaged) { Delete(path: path); }

            return false;
        }

        Puck.Abstractions.CacheRetention.Stamp(path: path);

        return true;
    }
    /// <summary>Reads a geometry's mesh under a bake's identity as <see cref="TryRead"/> does, leaving the store as it
    /// is: an entry it refuses stays, and a read stamps nothing, so a tool may read a store a running World owns.</summary>
    /// <param name="identity">The bake's identity.</param>
    /// <param name="geometry">The group's geometry.</param>
    /// <param name="mesh">The mesh, when this returns <see langword="true"/>.</param>
    /// <param name="materials">The group's material ids, when this returns <see langword="true"/>.</param>
    /// <param name="refusal">Why a held entry was refused, or <see langword="null"/> when there was none to read.</param>
    /// <returns><see langword="true"/> when the store held a sound entry.</returns>
    public bool TryPeek(UInt128 identity, SdfMeshGeometry geometry, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out SdfMesh? mesh, out int[] materials, out string? refusal) =>
        TryReadEntry(damaged: out _, geometry: geometry, identity: identity, materials: out materials, mesh: out mesh, refusal: out refusal);

    /// <summary>Returns the files of the entries the store holds, whatever bake identity wrote them, in ordinal order of
    /// their paths.</summary>
    /// <returns>The entries' paths; none when the directory does not exist.</returns>
    public IReadOnlyList<string> EntryPaths() {
        if (!System.IO.Directory.Exists(path: Directory)) { return []; }

        var paths = System.IO.Directory.GetFiles(path: Directory, searchOption: SearchOption.AllDirectories, searchPattern: ("*" + Extension));

        Array.Sort(array: paths, comparer: StringComparer.Ordinal);

        return paths;
    }
    /// <summary>Returns the directory an identity's set of entries is kept in: <see cref="Directory"/> and the identity
    /// as 32 hexadecimal digits.</summary>
    /// <param name="identity">The bake's identity.</param>
    /// <returns>The directory's absolute path; it exists only once the identity's first entry is written.</returns>
    public string SetDirectoryOf(UInt128 identity) => Path.Combine(path1: Directory, path2: identity.ToString(format: "x32", provider: null));
    /// <summary>Reads an entry's file under whatever key it names, leaving the store as it is: a tool that compares the
    /// meshes several builds stored for one geometry reads them this way, since no key reaches another build's
    /// entries.</summary>
    /// <param name="path">The entry's file (<see cref="EntryPaths"/>).</param>
    /// <param name="mesh">The mesh, when this returns <see langword="true"/>.</param>
    /// <param name="materials">The group's material ids, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the file holds a sound entry.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/>, empty or white
    /// space.</exception>
    public static bool TryReadFile(string path, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out SdfMesh? mesh, out int[] materials) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: path);

        mesh = null;
        materials = [];

        byte[] bytes;

        try {
            bytes = File.ReadAllBytes(path: path);
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            return false;
        }

        return ((bytes.Length >= (HeaderBytes + ChecksumBytes)) && TryDecode(bytes: bytes, key: BinaryPrimitives.ReadUInt128LittleEndian(source: bytes.AsSpan(start: 8)), materials: out materials, mesh: out mesh, refusal: out _));
    }
    // Reads an entry, answering whether a held entry's bytes were refused (damaged) rather than unreadable.
    private bool TryReadEntry(UInt128 identity, SdfMeshGeometry geometry, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out SdfMesh? mesh, out int[] materials, out string? refusal, out bool damaged) {
        var key = KeyOf(geometry: geometry, identity: identity);
        var path = PathOf(identity: identity, key: key);
        byte[] bytes;

        mesh = null;
        materials = [];
        refusal = null;
        damaged = false;

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
            damaged = true;

            return false;
        }

        return true;
    }
    /// <summary>Writes a geometry's mesh into its identity's set (<see cref="SetDirectoryOf"/>), replacing any entry it
    /// had, then holds the store to <see cref="CapacityBytes"/> a set at a time, keeping this entry's set whole. A write
    /// the file system refuses leaves the store as it was: the store is a cache.</summary>
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
        var path = PathOf(identity: identity, key: key);
        var bytes = Encode(key: key, materials: materials, mesh: mesh);
        if (!Quietly(action: () => Puck.Assets.AtomicFile.WriteAllBytes(bytes: bytes, path: path))) { return false; }
        _ = Puck.Abstractions.CacheRetention.EnforceSetsOnWrite(
            bound: new Puck.Abstractions.CacheBound(MaxBytes: CapacityBytes, MaxEntries: MaxSets),
            directory: Directory,
            inUse: path,
            isEntry: static file => file.Name.EndsWith(value: Extension, comparisonType: StringComparison.Ordinal)
        );

        return true;
    }
    /// <summary>Records a bake's identity beside the entries: its parts and its code's members, written once
    /// (<see cref="IdentityExtension"/>) and stamped on each later record, so a later bake that misses can name what
    /// changed since (<see cref="ExplainMisses"/>). The most recent <see cref="RecordedIdentities"/> are kept. An identity
    /// that carries its value alone, or a write the file system refuses, records nothing.</summary>
    /// <param name="identity">The bake's identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="identity"/> is <see langword="null"/>.</exception>
    public void Record(SdfMeshBakeIdentity identity) {
        ArgumentNullException.ThrowIfNull(argument: identity);

        if (!identity.HasParts) { return; }

        var path = IdentityPathOf(value: identity.Value);

        if (File.Exists(path: path)) {
            Puck.Abstractions.CacheRetention.Stamp(path: path);
            return;
        }

        var text = new System.Text.StringBuilder();

        text.Append(value: IdentityFormat).Append(value: '\n');
        text.Append(value: "code ").Append(value: identity.Code.ToString(format: "x32", provider: null)).Append(value: '\n');
        text.Append(value: "mask ").Append(value: identity.MaskKernel.ToString(format: "x32", provider: null)).Append(value: '\n');
        text.Append(value: "bake ").Append(value: identity.BakeKernel.ToString(format: "x32", provider: null)).Append(value: '\n');
        foreach (var (member, hash) in identity.Members) {
            text.Append(value: hash.ToString(format: "x32", provider: null)).Append(value: ' ').Append(value: member).Append(value: '\n');
        }
        if (!Quietly(action: () => Puck.Assets.AtomicFile.WriteAllBytes(bytes: System.Text.Encoding.UTF8.GetBytes(s: text.ToString()), path: path))) { return; }
        _ = Puck.Abstractions.CacheRetention.Enforce(
            bound: Puck.Abstractions.CacheBound.Entries(maxEntries: RecordedIdentities),
            directory: Directory,
            inUse: path,
            isEntry: static file => file.Name.EndsWith(value: IdentityExtension, comparisonType: StringComparison.Ordinal)
        );
    }
    /// <summary>Returns why a bake under an identity found no entry for some of its groups, in one line, and records the
    /// identity (<see cref="Record"/>). Under an identity the store recorded before, the groups' geometry is new to it, or
    /// its entries were evicted, and the line says how full the store is. Under a new identity, the line names the parts
    /// that differ from the nearest identity the store recorded (the one with the fewest parts changed, the most recently
    /// used among them): the mask or bake kernel's bytecode, or the code, with how many of its members changed in each
    /// assembly and the first of them by name.</summary>
    /// <param name="identity">The bake's identity.</param>
    /// <returns>The reason.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="identity"/> is <see langword="null"/>.</exception>
    public string ExplainMisses(SdfMeshBakeIdentity identity) {
        ArgumentNullException.ThrowIfNull(argument: identity);

        var value = identity.Value.ToString(format: "x32", provider: null);
        string reason;

        if (File.Exists(path: IdentityPathOf(value: identity.Value))) {
            var entries = (System.IO.Directory.Exists(path: Directory) ? new DirectoryInfo(path: Directory).GetFiles(searchOption: SearchOption.AllDirectories, searchPattern: ("*" + Extension)) : []);
            var set = SetDirectoryOf(identity: identity.Value);
            var own = entries.Count(predicate: file => string.Equals(a: file.DirectoryName, b: set, comparisonType: StringComparison.OrdinalIgnoreCase));
            var sets = entries.Select(selector: static file => file.DirectoryName).Distinct(comparer: StringComparer.OrdinalIgnoreCase).Count();

            reason = ((own == 0)
                ? $"identity {value} unchanged: its set was evicted, or it never stored one ({entries.Length} entries in {sets} sets, {entries.Sum(selector: static file => file.Length)} of {CapacityBytes} bytes)"
                : $"identity {value} unchanged: geometry new to its set of {own} entries ({entries.Length} entries in {sets} sets, {entries.Sum(selector: static file => file.Length)} of {CapacityBytes} bytes)");
        } else if (!identity.HasParts) {
            reason = $"identity {value} new, its parts unknown";
        } else if (NearestRecorded(identity: identity) is not { } nearest) {
            reason = $"identity {value} new: the store records no earlier identity";
        } else {
            var parts = new List<string>();

            if (nearest.Mask != identity.MaskKernel) { parts.Add(item: "the mask kernel's bytecode"); }
            if (nearest.Bake != identity.BakeKernel) { parts.Add(item: "the bake kernel's bytecode"); }
            if (nearest.Code != identity.Code) {
                var changed = ChangedMembers(identity: identity, path: nearest.Path);
                var byAssembly = changed.GroupBy(keySelector: static member => member[..Math.Max(0, member.IndexOf(value: '!', comparisonType: StringComparison.Ordinal))], comparer: StringComparer.Ordinal).Select(selector: static group => $"{group.Key} {group.Count()}");

                parts.Add(item: $"the code, {changed.Count} member{((changed.Count == 1) ? string.Empty : "s")} ({string.Join(separator: ", ", values: byAssembly)}: {string.Join(separator: ", ", values: changed.Take(count: 3).Select(selector: static member => member[(member.IndexOf(value: '!', comparisonType: StringComparison.Ordinal) + 1)..].Split(separator: '`')[0]))}{((changed.Count > 3) ? ", ..." : string.Empty)})");
            }
            reason = $"identity {value} new since {Path.GetFileNameWithoutExtension(path: nearest.Path)}: {string.Join(separator: "; ", values: parts)} changed";
        }

        Record(identity: identity);

        return reason;
    }

    /// <summary>The extension of a recorded identity's file (<see cref="Record"/>).</summary>
    public const string IdentityExtension = ".identity";
    /// <summary>The most recorded identities a store keeps, least recently used out.</summary>
    public const int RecordedIdentities = 64;
    // The first line of a recorded identity's file: its format.
    private const string IdentityFormat = "puck.mesh-identity.v1";

    private string IdentityPathOf(UInt128 value) => Path.Combine(path1: Directory, path2: (value.ToString(format: "x32", provider: null) + IdentityExtension));
    // The recorded identity other than this one with the fewest parts changed from it, the most recently used among them;
    // null when the store records none.
    private (string Path, UInt128 Code, UInt128 Mask, UInt128 Bake)? NearestRecorded(SdfMeshBakeIdentity identity) {
        if (!System.IO.Directory.Exists(path: Directory)) { return null; }

        var own = IdentityPathOf(value: identity.Value);
        (string Path, UInt128 Code, UInt128 Mask, UInt128 Bake)? nearest = null;
        var nearestChanged = int.MaxValue;
        var nearestUsed = DateTime.MinValue;

        foreach (var file in new DirectoryInfo(path: Directory).GetFiles(searchPattern: ("*" + IdentityExtension))) {
            if (string.Equals(a: file.FullName, b: own, comparisonType: StringComparison.OrdinalIgnoreCase) || (ReadParts(path: file.FullName) is not { } parts)) { continue; }

            var changed = (((parts.Code != identity.Code) ? 1 : 0) + ((parts.Mask != identity.MaskKernel) ? 1 : 0) + ((parts.Bake != identity.BakeKernel) ? 1 : 0));

            if ((changed < nearestChanged) || ((changed == nearestChanged) && (file.LastWriteTimeUtc > nearestUsed))) {
                nearest = (file.FullName, parts.Code, parts.Mask, parts.Bake);
                nearestChanged = changed;
                nearestUsed = file.LastWriteTimeUtc;
            }
        }

        return nearest;
    }
    // A recorded identity's parts, from its file's first lines; null for a file of another format or none.
    private static (UInt128 Code, UInt128 Mask, UInt128 Bake)? ReadParts(string path) {
        string[] head = [];

        _ = Quietly(action: () => head = File.ReadLines(path: path).Take(count: 4).ToArray());

        return ((head.Length == 4) && (head[0] == IdentityFormat) && TryPart(line: head[1], name: "code ", value: out var code) && TryPart(line: head[2], name: "mask ", value: out var mask) && TryPart(line: head[3], name: "bake ", value: out var bake)) ? (code, mask, bake) : null;
    }
    private static bool TryPart(string line, string name, out UInt128 value) {
        value = default;

        return (line.StartsWith(value: name, comparisonType: StringComparison.Ordinal) && UInt128.TryParse(s: line.AsSpan(start: name.Length), style: System.Globalization.NumberStyles.HexNumber, provider: null, result: out value));
    }
    // The code members an identity changed, added or removed against a recorded identity's file; every member when the
    // file is gone.
    private static IReadOnlyList<string> ChangedMembers(SdfMeshBakeIdentity identity, string path) {
        var earlier = new Dictionary<string, UInt128>(comparer: StringComparer.Ordinal);
        string[] lines = [];

        _ = Quietly(action: () => lines = File.ReadAllLines(path: path));
        foreach (var line in lines.Skip(count: 4)) {
            if ((line.Length > 33) && UInt128.TryParse(s: line.AsSpan(length: 32, start: 0), style: System.Globalization.NumberStyles.HexNumber, provider: null, result: out var hash)) { earlier[line[33..]] = hash; }
        }

        return SdfMeshBakeClosure.Changed(earlier: earlier, members: identity.Members);
    }
    // The key of an entry: the bake's identity and the geometry, its surface and frame.
    private static UInt128 KeyOf(UInt128 identity, SdfMeshGeometry geometry) {
        Span<byte> key = stackalloc byte[33];

        BinaryPrimitives.WriteUInt128LittleEndian(destination: key, value: identity);
        BinaryPrimitives.WriteUInt128LittleEndian(destination: key[16..], value: geometry.Hash);
        key[32] = (byte)(geometry.Dynamic ? 1 : 0);

        return XxHash128.HashToUInt128(source: key);
    }
    private string PathOf(UInt128 identity, UInt128 key) => Path.Combine(path1: SetDirectoryOf(identity: identity), path2: (key.ToString(format: "x32", provider: null) + Extension));
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
        BinaryPrimitives.WriteSingleLittleEndian(destination: at[52..], value: mesh.BakeCell.X);
        BinaryPrimitives.WriteSingleLittleEndian(destination: at[56..], value: mesh.BakeCell.Y);
        BinaryPrimitives.WriteSingleLittleEndian(destination: at[60..], value: mesh.BakeCell.Z);
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
            mesh = new SdfMesh(indices: indexData, members: members, normals: normalData, positions: positionData, triangleMaterials: materialData, vertexMembers: vertexMemberData) {
                BakeCell = new Vector3(BinaryPrimitives.ReadSingleLittleEndian(body[52..]),
                    BinaryPrimitives.ReadSingleLittleEndian(body[56..]), BinaryPrimitives.ReadSingleLittleEndian(body[60..])),
            };
            if (!float.IsFinite(mesh.BakeCell.LengthSquared()) || Vector3.Min(mesh.BakeCell, Vector3.Zero) != Vector3.Zero) {
                throw new ArgumentException("Invalid contour cell.");
            }
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
