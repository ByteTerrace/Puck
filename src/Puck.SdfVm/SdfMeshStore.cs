using System.Buffers.Binary;
using System.IO.Hashing;
using Puck.Abstractions.Memory;
using Puck.Hosting.Jobs;

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
/// most recently used entry's, and a write that passes the bound removes whole sets, least recently used first, keeping
/// the writer's own, down to an eighth below it (<see cref="Puck.Abstractions.CacheBound.LowWater"/>). A peer's write
/// may evict a set being baked or loaded unless it is pinned by <see cref="Hold"/>. An unpinned writer's set that alone
/// exceeds the bound is trimmed entry by entry, keeping the entry being written. One process's
/// writes never leave the store over its bound; Worlds writing it at once count one another's writes only at their next
/// listing, at least once a minute while they write. A file directly in the directory, which no identity's set holds,
/// is a set of its own and ages out first once nothing reads it. The identity records beside the sets
/// (<see cref="IdentityExtension"/>) are bounded apart, by count (<see cref="RecordedIdentities"/>), and are not counted
/// in <see cref="CapacityBytes"/>. Its members are thread-safe.</para>
/// </summary>
public sealed class SdfMeshStore {
    /// <summary>The bytes a store holds at most unless its owner names another bound: eight gibibytes. One per-user
    /// store serves every World and worktree; it retains compressed mesh sets whole when they fit the bound.</summary>
    public const long DefaultCapacityBytes = (8L << 30);
    /// <summary>The extension of an entry's file.</summary>
    public const string Extension = ".mesh";
    private const string CheckpointExtension = ".bake";
    private const string SampleExtension = ".samples";

    private const int ChecksumBytes = 16;
    private const int HeaderBytes = SdfMeshStoreCodec.HeaderBytes;
    private const ulong Magic = SdfMeshStoreCodec.Magic;
    // The most sets a store keeps, past any machine's builds many times over: its bytes bound it first.
    private const int MaxSets = 4_096;

    private readonly IAllocator? m_allocator;

    /// <summary>Initializes a new instance of the <see cref="SdfMeshStore"/> class. Nothing is created until an entry is
    /// written.</summary>
    /// <param name="directory">The directory the entries are kept in, made absolute here.</param>
    /// <param name="capacityBytes">The bytes the entries hold at most.</param>
    /// <param name="allocator">The host allocator for decode scratch, or null for managed offline decoding.</param>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is <see langword="null"/>, empty or white
    /// space.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacityBytes"/> is not positive.</exception>
    public SdfMeshStore(string directory, long capacityBytes = DefaultCapacityBytes, IAllocator? allocator = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: directory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: capacityBytes);

        CapacityBytes = capacityBytes;
        Directory = Path.GetFullPath(path: directory);
        m_allocator = allocator;
    }

    /// <summary>Gets the account a read reserves its workspace in before it allocates it: the process's.</summary>
    public MemoryAccount Account { get; init; } = MemoryAccount.Shared;
    /// <summary>Gets how long a read waits for <see cref="Account"/> to have room for its workspace before it declines
    /// the entry (<see cref="DefaultWorkspacePatience"/>).</summary>
    public TimeSpan WorkspacePatience { get; init; } = DefaultWorkspacePatience;

    /// <summary>Gets the bytes the entries hold at most.</summary>
    public long CapacityBytes { get; }
    /// <summary>Gets the absolute directory the entries are kept in.</summary>
    public string Directory { get; }

    /// <summary>Gets the entry format's eight-byte version tag (<see cref="SdfMeshStoreCodec.Magic"/>). The identity folds it
    /// (<see cref="BuildIdentityOf"/>), so a bump starts a new set while the decode refuses any entry of another tag;
    /// <see cref="Record"/> writes it beside the identity's parts so <see cref="ExplainMisses"/> can name the bump.</summary>
    public static string EntryVersion { get; } = VersionText(magic: Magic);

    internal static string VersionText(ulong magic) {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];

        BinaryPrimitives.WriteUInt64LittleEndian(destination: bytes, value: magic);

        return System.Text.Encoding.ASCII.GetString(bytes: bytes);
    }

    /// <summary>Returns the identity of this process's bake on a kernel set: the code it runs
    /// (<see cref="SdfMeshBakeClosure.Current"/>, with any hot-reload update applied to it) and its bake kernels'
    /// bytecode.</summary>
    /// <param name="kernels">The kernel set the bake dispatches.</param>
    /// <returns>The identity.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="kernels"/> is <see langword="null"/>.</exception>
    public static SdfMeshBakeIdentity IdentityOf(SdfKernelSet kernels) => SdfMeshBakeIdentity.Of(code: SdfMeshBakeClosure.Current, kernels: kernels);
    /// <summary>Returns an identity's value from its parts, as <see cref="SdfMeshBakeIdentity.Of"/> combines them.</summary>
    /// <param name="code">The code identity (<see cref="SdfMeshBakeClosure.Hash"/>).</param>
    /// <param name="maskKernel">The hash of the mask kernel's bytecode.</param>
    /// <param name="bakeKernel">The hash of the bake kernel's bytecode.</param>
    /// <param name="entryVersion">The entry format's version tag the walked build's codec writes and reads
    /// (<see cref="EntryVersion"/>), eight ASCII characters.</param>
    /// <returns>The identity's value.</returns>
    /// <exception cref="ArgumentException"><paramref name="entryVersion"/> is not eight ASCII characters.</exception>
    public static UInt128 BuildIdentityOf(UInt128 code, UInt128 maskKernel, UInt128 bakeKernel, string entryVersion) {
        if ((entryVersion?.Length != sizeof(ulong)) || !System.Text.Ascii.IsValid(value: entryVersion)) {
            throw new ArgumentException(message: "An entry version is eight ASCII characters.", paramName: nameof(entryVersion));
        }
        Span<byte> bytes = stackalloc byte[56];

        BinaryPrimitives.WriteUInt128LittleEndian(destination: bytes, value: code);
        BinaryPrimitives.WriteUInt128LittleEndian(destination: bytes[16..], value: maskKernel);
        BinaryPrimitives.WriteUInt128LittleEndian(destination: bytes[32..], value: bakeKernel);
        _ = System.Text.Encoding.ASCII.GetBytes(chars: entryVersion, bytes: bytes[48..]);

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
    /// <param name="mesh">The mesh, or null when a sound entry records that the bake found no surface.</param>
    /// <param name="materials">The stored canonical ids by material place; the library maps them into the taking group's palette.</param>
    /// <param name="refusal">Why a held entry was refused, or <see langword="null"/> when there was none to read.</param>
    /// <returns><see langword="true"/> when the store held a sound entry.</returns>
    public bool TryRead(UInt128 identity, SdfMeshGeometry geometry, out SdfMesh? mesh, out int[] materials, out string? refusal) =>
        TryReadInto(declined: out _, geometry: geometry, identity: identity, materials: out materials, mesh: out mesh, refusal: out refusal, storage: new SdfMeshManagedStorage(), taking: null);

    // Reads a geometry's entry into a storage as TryRead does: the entry is stamped when it is sound, and deleted when its
    // bytes are refused. `taking` is the taking group's ids, which the decode writes the triangle materials in when the mesh
    // can be renumbered back by place (SdfMeshStoreDecoder.TryDecode); `declined` is set for a sound entry the storage did not take.
    internal bool TryReadInto(UInt128 identity, SdfMeshGeometry geometry, SdfMeshStorage storage, int[]? taking, out SdfMesh? mesh, out int[] materials, out string? refusal, out bool declined, string extension = Extension) {
        var path = PathOf(identity: identity, key: KeyOf(geometry: geometry, identity: identity), extension: extension);
        var read = TryReadEntry(damaged: out var damaged, declined: out declined, geometry: geometry, identity: identity, materials: out materials, mesh: out mesh, refusal: out refusal, storage: storage, taking: taking, extension: extension);

        if (!read && damaged) { Delete(path: path); }
        if (read || declined) { Puck.Abstractions.CacheRetention.Stamp(path: path); }

        return read;
    }

    /// <summary>Reads a geometry's mesh under a bake's identity as <see cref="TryRead"/> does, leaving the store as it
    /// is: an entry it refuses stays, and a read stamps nothing, so a tool may read a store a running World owns.</summary>
    /// <param name="identity">The bake's identity.</param>
    /// <param name="geometry">The group's geometry.</param>
    /// <param name="mesh">The mesh, or null when a sound entry records that the bake found no surface.</param>
    /// <param name="materials">The stored canonical ids by material place; the library maps them into the taking group's palette.</param>
    /// <param name="refusal">Why a held entry was refused, or <see langword="null"/> when there was none to read.</param>
    /// <returns><see langword="true"/> when the store held a sound entry.</returns>
    public bool TryPeek(UInt128 identity, SdfMeshGeometry geometry, out SdfMesh? mesh, out int[] materials, out string? refusal) =>
        TryReadEntry(damaged: out _, declined: out _, geometry: geometry, identity: identity, materials: out materials, mesh: out mesh, refusal: out refusal, storage: new SdfMeshManagedStorage(), taking: null);
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
    /// <summary>Pins an identity's set against every process's eviction for the rest of this process
    /// (<see cref="Puck.Abstractions.CacheRetention.HoldSet"/>): a library that publishes meshes whose parts it restores
    /// from the set (<see cref="SdfMeshLibrary.TryLoad"/>) holds it, so another World's writes cannot evict what this one
    /// still reads. A successful pin is shared by later calls. A failed pin can be retried.</summary>
    /// <param name="identity">The bake's identity.</param>
    /// <returns><see langword="true"/> when the set is pinned; <see langword="false"/> when its lease could not be
    /// opened, in which case a restore that misses bakes the group again.</returns>
    public bool Hold(UInt128 identity) {
        if (m_holds.ContainsKey(key: identity)) { return true; }

        var hold = Puck.Abstractions.CacheRetention.HoldSet(setDirectory: SetDirectoryOf(identity: identity));

        if (hold is null) { return m_holds.ContainsKey(key: identity); }
        if (!m_holds.TryAdd(key: identity, value: hold)) { hold.Dispose(); }
        return true;
    }

    // The leases this process holds on the sets it publishes from, open until it ends.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<UInt128, FileStream> m_holds = new();

    /// <summary>Reads an entry's file under whatever key it names, leaving the store as it is: a tool that compares the
    /// meshes several builds stored for one geometry reads them this way, since no key reaches another build's
    /// entries.</summary>
    /// <param name="path">The entry's file (<see cref="EntryPaths"/>).</param>
    /// <param name="mesh">The mesh, or null when a sound entry records that the bake found no surface.</param>
    /// <param name="materials">The stored canonical ids by material place; the library maps them into the taking group's palette.</param>
    /// <returns><see langword="true"/> when the file holds a sound entry.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/>, empty or white
    /// space.</exception>
    public static bool TryReadFile(string path, out SdfMesh? mesh, out int[] materials) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: path);

        mesh = null;
        materials = [];

        var workspace = SdfMeshStoreWorkspace.Shared;
        using var owner = workspace.CreateOwner(name: "mesh-entry");
        using var entry = new OwnedBuffer<byte>(allocator: null, observer: owner);

        try {
            if (!TryReadEntryBytes(entry: entry, owner: owner, path: path, workspace: in workspace)) { return false; }
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            return false;
        }

        var bytes = entry.GetSpan();

        return ((bytes.Length >= (HeaderBytes + ChecksumBytes)) && SdfMeshStoreDecoder.TryDecode(bytes: bytes, declined: out _, key: BinaryPrimitives.ReadUInt128LittleEndian(source: bytes[8..]), materials: out materials, mesh: out mesh, refusal: out _, storage: new SdfMeshManagedStorage(), taking: null, owner: owner, workspace: in workspace));
    }

    /// <summary>How long a store read waits for its account to have room for its workspace before it declines the
    /// entry, unless a store says otherwise: two seconds. A read holds a worker while it waits, so the wait is
    /// bounded.</summary>
    public static TimeSpan DefaultWorkspacePatience { get; } = TimeSpan.FromSeconds(seconds: 2);

    /// <summary>Why a sound entry was declined when its account had no room for the workspace its read needs; the
    /// refusal a read reports ends with it.</summary>
    public const string WorkspaceRefusal = "waits for workspace memory";

    private SdfMeshStoreWorkspace Workspace => new(Account: Account, Patience: WorkspacePatience);

    // Reads a file's bytes into a buffer sized to it, reserved before it is allocated; false when the account had no
    // room for it, and nothing was read.
    private static bool TryReadEntryBytes(OwnedBuffer<byte> entry, MemoryAccount.Owner owner, string path, in SdfMeshStoreWorkspace workspace) {
        using var handle = File.OpenHandle(path: path, mode: FileMode.Open, access: FileAccess.Read, share: FileShare.Read, options: FileOptions.SequentialScan);
        var length = RandomAccess.GetLength(handle: handle);

        if (length > int.MaxValue) { throw new IOException(message: "The entry is larger than an array."); }
        if (!workspace.TryReserve(bytes: length, owner: owner)) { return false; }
        entry.Resize(clear: false, length: ((int)length), preserve: false);

        var bytes = entry.GetSpan();
        var read = 0;

        while (read < bytes.Length) {
            var count = RandomAccess.Read(handle: handle, buffer: bytes[read..], fileOffset: read);

            if (count == 0) { break; }
            read += count;
        }
        entry.Resize(clear: false, length: read, preserve: true);

        return true;
    }

    // The entries this store read, their file bytes, and the time its readers spent reading and decoding them, summed over
    // the threads that read.
    private long m_reads;
    private long m_readBytes;
    private long m_readTicks;
    private long m_decodeTicks;

    /// <summary>Describes the entries this store has read: their count, their file bytes, and the seconds its readers
    /// spent reading files and decoding them, each summed over the threads that read concurrently.</summary>
    /// <returns>One line of <c>key=value</c> pairs.</returns>
    public string DescribeReads() => string.Create(
        provider: System.Globalization.CultureInfo.InvariantCulture,
        handler: $"store-reads={Interlocked.Read(location: ref m_reads)} store-read-bytes={Interlocked.Read(location: ref m_readBytes)}B store-read-thread-seconds={(((double)Interlocked.Read(location: ref m_readTicks)) / System.Diagnostics.Stopwatch.Frequency):0.000}s store-decode-thread-seconds={(((double)Interlocked.Read(location: ref m_decodeTicks)) / System.Diagnostics.Stopwatch.Frequency):0.000}s"
    );

    // Reads an entry into a storage, answering whether a held entry's bytes were refused (damaged) rather than unreadable
    // or sound but not taken by the storage (declined). The entry's bytes are read into owned scratch, as its inflated planes
    // are, and nothing of them outlives the decode.
    private bool TryReadEntry(UInt128 identity, SdfMeshGeometry geometry, SdfMeshStorage storage, int[]? taking, out SdfMesh? mesh, out int[] materials, out string? refusal, out bool damaged, out bool declined, string extension = Extension) {
        var key = KeyOf(geometry: geometry, identity: identity);
        var path = PathOf(identity: identity, key: key, extension: extension);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        mesh = null;
        materials = [];
        refusal = null;
        damaged = false;
        declined = false;

        var workspace = Workspace;
        using var owner = workspace.CreateOwner(name: "mesh-entry");
        using var entry = new OwnedBuffer<byte>(allocator: m_allocator, observer: owner);

        try {
            if (!TryReadEntryBytes(entry: entry, owner: owner, path: path, workspace: in workspace)) {
                refusal = $"{Path.GetFileName(path: path)} {WorkspaceRefusal}";
                declined = true;

                return false;
            }
        } catch (Exception exception) when ((exception is FileNotFoundException or DirectoryNotFoundException)) {
            return false;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            refusal = $"cannot read {Path.GetFileName(path: path)}: {exception.Message}";

            return false;
        }

        var read = System.Diagnostics.Stopwatch.GetTimestamp();
        var decoded = SdfMeshStoreDecoder.TryDecode(allocator: m_allocator, bytes: entry.GetSpan(), declined: out declined, key: key, materials: out materials, mesh: out mesh, refusal: out var why, storage: storage, taking: taking, owner: owner, workspace: in workspace);

        _ = Interlocked.Increment(location: ref m_reads);
        _ = Interlocked.Add(location1: ref m_readBytes, value: entry.Length);
        _ = Interlocked.Add(location1: ref m_readTicks, value: (read - started));
        _ = Interlocked.Add(location1: ref m_decodeTicks, value: (System.Diagnostics.Stopwatch.GetTimestamp() - read));
        if (!decoded) {
            refusal = $"{Path.GetFileName(path: path)} {why}";
            damaged = !declined;

            return false;
        }

        return true;
    }

    /// <summary>Writes a geometry's mesh into its identity's set (<see cref="SetDirectoryOf"/>), replacing any entry it
    /// had, then holds the store to <see cref="CapacityBytes"/> a set at a time, keeping this entry's set whole. A write
    /// the file system refuses leaves the store as it was: the store is a cache.</summary>
    /// <param name="identity">The bake's identity.</param>
    /// <param name="geometry">The group's geometry.</param>
    /// <param name="mesh">The mesh, or null for an empty bake.</param>
    /// <param name="materials">The group's material ids, in the order the geometry numbers them.</param>
    /// <returns><see langword="true"/> when the entry was written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="materials"/> is
    /// <see langword="null"/>.</exception>
    public bool Write(UInt128 identity, SdfMeshGeometry geometry, SdfMesh? mesh, int[] materials) {
        ArgumentNullException.ThrowIfNull(argument: materials);
        if ((mesh is null) && (materials.Length != 0)) { throw new ArgumentException(message: "An empty bake carries no materials.", paramName: nameof(materials)); }

        var key = KeyOf(geometry: geometry, identity: identity);
        var path = PathOf(identity: identity, key: key);
        return WriteEntry(path, Encode(key: key, materials: materials, mesh: mesh));
    }

    private bool WriteEntry(string path, byte[] bytes) {
        if (!Quietly(action: () => Puck.Confinement.AtomicFile.WriteAllBytes(bytes: bytes, path: path))) { return false; }
        _ = Puck.Abstractions.CacheRetention.EnforceSetsOnWrite(
            bound: new Puck.Abstractions.CacheBound(MaxBytes: CapacityBytes, MaxEntries: MaxSets),
            directory: Directory,
            inUse: path,
            isEntry: static name => name.EndsWith(Extension, StringComparison.Ordinal)
                || name.EndsWith(CheckpointExtension, StringComparison.Ordinal)
                || name.EndsWith(SampleExtension, StringComparison.Ordinal)
        );

        return true;
    }

    internal bool ContainsCheckpoint(UInt128 identity, SdfMeshGeometry geometry) =>
        File.Exists(PathOf(identity, KeyOf(identity, geometry), CheckpointExtension));

    internal bool TryReadCheckpoint(UInt128 identity, SdfMeshGeometry geometry, out SdfMesh? mesh) {
        try {
            return TryReadInto(identity, geometry, new SdfMeshManagedStorage(), null, out mesh, out _, out _, out _, CheckpointExtension);
        } catch (OutOfMemoryException) {
            mesh = null;
            return false;
        }
    }

    internal void WriteCheckpoint(UInt128 identity, SdfMeshGeometry geometry, SdfMesh? mesh) {
        var key = KeyOf(identity, geometry);
        try {
            _ = WriteEntry(PathOf(identity, key, CheckpointExtension), SdfMeshStoreCodec.EncodeExact(key, mesh));
        } catch (Exception exception) when (exception is OutOfMemoryException or OverflowException) {
            // The surface is still available to the next stage.
        }
    }

    internal bool TryReadSamples(UInt128 identity, UInt128 key, int length, OwnedBuffer<byte> samples) {
        key = KeyOf(identity, new SdfMeshGeometry(Dynamic: false, Hash: key));
        var path = PathOf(identity, key, SampleExtension);
        var workspace = Workspace;
        using var owner = workspace.CreateOwner("mesh-bake-samples");
        using var entry = new OwnedBuffer<byte>(m_allocator, owner);
        try {
            if (!TryReadEntryBytes(entry, owner, path, in workspace)) { return false; }
            if (!SdfMeshStoreDecoder.TryPayload(entry.GetSpan(), key, out var compressed, out var storedLength, out _) || (storedLength != length)) {
                Delete(path);
                return false;
            }
            if (!workspace.TryReserve(owner, checked((long)entry.Length + length))) { return false; }
            samples.Resize(length, clear: false, preserve: false);
            if (!SdfMeshStoreDecoder.TryInflate(compressed, samples.GetSpan())) {
                Delete(path);
                return false;
            }
            Puck.Abstractions.CacheRetention.Stamp(path);
            return true;
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OutOfMemoryException) {
            return false;
        }
    }

    internal void WriteSamples(UInt128 identity, UInt128 key, ReadOnlySpan<byte> samples) {
        key = KeyOf(identity, new SdfMeshGeometry(Dynamic: false, Hash: key));
        try {
            _ = WriteEntry(PathOf(identity, key, SampleExtension), SdfMeshStoreCodec.EncodePayload(key, samples));
        } catch (OutOfMemoryException) {
            // The sample answer is already available to the contour.
        }
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
        text.Append(value: "version ").Append(value: identity.EntryVersion).Append(value: '\n');
        text.Append(value: "code ").Append(value: identity.Code.ToString(format: "x32", provider: null)).Append(value: '\n');
        text.Append(value: "mask ").Append(value: identity.MaskKernel.ToString(format: "x32", provider: null)).Append(value: '\n');
        text.Append(value: "bake ").Append(value: identity.BakeKernel.ToString(format: "x32", provider: null)).Append(value: '\n');
        foreach (var (member, hash) in identity.Members) {
            text.Append(value: hash.ToString(format: "x32", provider: null)).Append(value: ' ').Append(value: member).Append(value: '\n');
        }
        if (!Quietly(action: () => Puck.Confinement.AtomicFile.WriteAllBytes(bytes: System.Text.Encoding.UTF8.GetBytes(s: text.ToString()), path: path))) { return; }
        _ = Puck.Abstractions.CacheRetention.Enforce(
            bound: Puck.Abstractions.CacheBound.Entries(maxEntries: RecordedIdentities),
            directory: Directory,
            inUse: path,
            isEntry: static name => name.EndsWith(comparisonType: StringComparison.Ordinal, value: IdentityExtension)
        );
    }
    /// <summary>Returns why a bake under an identity found no entry for some of its groups, in one line, and records the
    /// identity (<see cref="Record"/>). Under an identity the store recorded before, the groups' geometry is new to it, or
    /// its entries were evicted, and the line says how full the store is. Under a new identity, the line names the parts
    /// that differ from the nearest identity the store recorded (the one with the fewest parts changed, the most recently
    /// used among them): the entry version (<see cref="EntryVersion"/>), the mask or bake kernel's bytecode, or the code,
    /// with how many of its members changed in each assembly and the first of them by name.</summary>
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

            if (nearest.Version != identity.EntryVersion) { parts.Add(item: $"the entry version ({nearest.Version} -> {identity.EntryVersion})"); }
            if (nearest.Mask != identity.MaskKernel) { parts.Add(item: "the mask kernel's bytecode"); }
            if (nearest.Bake != identity.BakeKernel) { parts.Add(item: "the bake kernel's bytecode"); }
            if (identity.Applied.Length > 0) { parts.Add(item: $"the code, by the hot-reload update {identity.Applied}"); }
            if (nearest.Code != identity.Code) {
                var changed = ChangedMembers(identity: identity, path: nearest.Path);

                if (nearest.Version != identity.EntryVersion) {
                    changed = [.. changed.Where(predicate: static member => !(member.StartsWith(comparisonType: StringComparison.Ordinal, value: "Puck.SdfVm!Puck.SdfVm.SdfMeshStore.") || member.StartsWith(comparisonType: StringComparison.Ordinal, value: "Puck.SdfVm!Puck.SdfVm.SdfMeshStoreCodec.")))];
                }
                if (changed.Count > 0) {
                    var byAssembly = changed.GroupBy(keySelector: static member => member[..Math.Max(val1: 0, val2: member.IndexOf(comparisonType: StringComparison.Ordinal, value: '!'))], comparer: StringComparer.Ordinal).Select(selector: static group => $"{group.Key} {group.Count()}");

                    parts.Add(item: $"the code, {changed.Count} member{((changed.Count == 1) ? string.Empty : "s")} ({string.Join(separator: ", ", values: byAssembly)}: {string.Join(separator: ", ", values: changed.Take(count: 3).Select(selector: static member => member[(member.IndexOf(comparisonType: StringComparison.Ordinal, value: '!') + 1)..].Split(separator: '`')[0]))}{((changed.Count > 3) ? ", ..." : string.Empty)})");
                }
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
    private (string Path, string Version, UInt128 Code, UInt128 Mask, UInt128 Bake)? NearestRecorded(SdfMeshBakeIdentity identity) {
        if (!System.IO.Directory.Exists(path: Directory)) { return null; }

        var own = IdentityPathOf(value: identity.Value);
        (string Path, string Version, UInt128 Code, UInt128 Mask, UInt128 Bake)? nearest = null;
        var nearestChanged = int.MaxValue;
        var nearestUsed = DateTime.MinValue;

        foreach (var file in new DirectoryInfo(path: Directory).GetFiles(searchPattern: ("*" + IdentityExtension))) {
            if (string.Equals(a: file.FullName, b: own, comparisonType: StringComparison.OrdinalIgnoreCase) || (ReadParts(path: file.FullName) is not { } parts)) { continue; }

            var changed = (((((parts.Version != identity.EntryVersion) ? 1 : 0) + ((parts.Code != identity.Code) ? 1 : 0)) + ((parts.Mask != identity.MaskKernel) ? 1 : 0)) + ((parts.Bake != identity.BakeKernel) ? 1 : 0));

            if ((changed < nearestChanged) || ((changed == nearestChanged) && (file.LastWriteTimeUtc > nearestUsed))) {
                nearest = (file.FullName, parts.Version, parts.Code, parts.Mask, parts.Bake);
                nearestChanged = changed;
                nearestUsed = file.LastWriteTimeUtc;
            }
        }

        return nearest;
    }
    // A recorded identity's parts, from its file's first lines; null for a file of another format or none.
    private static (string Version, UInt128 Code, UInt128 Mask, UInt128 Bake)? ReadParts(string path) {
        string[] head = [];

        _ = Quietly(action: () => head = File.ReadLines(path: path).Take(count: 5).ToArray());

        return (((head.Length == 5) && (head[0] == IdentityFormat) && head[1].StartsWith(comparisonType: StringComparison.Ordinal, value: "version ") && (head[1].Length > "version ".Length) && TryPart(line: head[2], name: "code ", value: out var code) && TryPart(line: head[3], name: "mask ", value: out var mask) && TryPart(line: head[4], name: "bake ", value: out var bake)) ? (head[1]["version ".Length..], code, mask, bake) : null);
    }
    private static bool TryPart(string line, string name, out UInt128 value) {
        value = default;

        return (line.StartsWith(comparisonType: StringComparison.Ordinal, value: name) && UInt128.TryParse(s: line.AsSpan(start: name.Length), style: System.Globalization.NumberStyles.HexNumber, provider: null, result: out value));
    }
    // The code members an identity changed, added or removed against a recorded identity's file; every member when the
    // file is gone.
    private static IReadOnlyList<string> ChangedMembers(SdfMeshBakeIdentity identity, string path) {
        var earlier = new Dictionary<string, UInt128>(comparer: StringComparer.Ordinal);
        string[] lines = [];

        _ = Quietly(action: () => lines = File.ReadAllLines(path: path));
        foreach (var line in lines.Skip(count: 5)) {
            if ((line.Length > 33) && UInt128.TryParse(s: line.AsSpan(length: 32, start: 0), style: System.Globalization.NumberStyles.HexNumber, provider: null, result: out var hash)) { earlier[line[33..]] = hash; }
        }

        return SdfMeshBakeClosure.Changed(earlier: earlier, members: identity.Members);
    }
    // The key of an entry: the bake's identity and the geometry, its surface and frame.
    private static UInt128 KeyOf(UInt128 identity, SdfMeshGeometry geometry) {
        Span<byte> key = stackalloc byte[33];

        BinaryPrimitives.WriteUInt128LittleEndian(destination: key, value: identity);
        BinaryPrimitives.WriteUInt128LittleEndian(destination: key[16..], value: geometry.Hash);
        key[32] = ((byte)(geometry.Dynamic ? 1 : 0));

        return XxHash128.HashToUInt128(source: key);
    }
    private string PathOf(UInt128 identity, UInt128 key, string extension = Extension) => Path.Combine(path1: SetDirectoryOf(identity: identity), path2: (key.ToString(format: "x32", provider: null) + extension));
    private static byte[] Encode(UInt128 key, SdfMesh? mesh, int[] materials) => SdfMeshStoreCodec.Encode(key: key, materials: materials, mesh: mesh);
    // Deletes a file the store owns, leaving one another World holds open for a later trim or refusal to remove.
    private static void Delete(string path) => _ = Quietly(action: () => File.Delete(path: path));
    // Runs a file-system action, answering whether the file system allowed it.
    private static bool Quietly(Action action) {
        try {
            action();

            return true;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            return false;
        }
    }
}

/// <summary>
/// How a store read gets the scratch it reads and expands an entry in: the account it is reserved in before it is
/// allocated, and how long the read waits for room (<see cref="MemoryAccount.Owner.TryReserveWithin"/>). A read that is
/// not admitted declines the entry, which stays in the store.
/// </summary>
/// <param name="Account">The account.</param>
/// <param name="Patience">The longest wait for room.</param>
internal readonly record struct SdfMeshStoreWorkspace(MemoryAccount Account, TimeSpan Patience) {
    /// <summary>Gets the process account at the default patience.</summary>
    public static SdfMeshStoreWorkspace Shared => new(Account: MemoryAccount.Shared, Patience: SdfMeshStore.DefaultWorkspacePatience);

    /// <summary>Creates the owner one read's scratch is counted under.</summary>
    /// <param name="name">The owner's name.</param>
    /// <returns>The owner.</returns>
    public MemoryAccount.Owner CreateOwner(string name) => Account.CreateOwner(memoryClass: MemoryClass.Workspace, name: name);
    /// <summary>Reserves a read's scratch before it is allocated: admitted when the host pool has room or as the
    /// process's one forced job, and otherwise after a wait for room no longer than the patience.</summary>
    /// <param name="owner">The read's owner.</param>
    /// <param name="bytes">The scratch's bytes.</param>
    /// <returns><see langword="false"/> when the read does without.</returns>
    public bool TryReserve(MemoryAccount.Owner owner, long bytes) =>
        owner.TryReserveWithin(patience: Patience, peak: bytes, reason: new Puck.Abstractions.Waiting.WaitReason(Subsystem: "mesh store", WaitsFor: "room in the memory account for a read's workspace"));
}
