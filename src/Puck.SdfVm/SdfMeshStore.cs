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
/// atomically, so concurrent Worlds may share a directory, and without a flush to stable storage, since the checksum
/// refuses whatever a power loss leaves behind a name. The writes no bake waits for (a stage's checkpoint, a lattice's
/// samples, a bound's enforcement) run on the store's writer, beside the bake rather than ahead of it, holding at most
/// <see cref="MostPendingWriteBytes"/>.</para>
/// <para>Each identity's entries are one set, kept in a directory named for the identity, and the store holds at most
/// <see cref="CapacityBytes"/> bytes a set at a time, under Puck's one cache retention
/// (<see cref="Puck.Abstractions.CacheRetention.EnforceSetsOnWrite"/>): a read stamps an entry, a set's last use is its
/// most recently used entry's, and a write that passes the bound removes whole sets, least recently used first, keeping
/// the writer's own, down to an eighth below it (<see cref="Puck.Abstractions.CacheBound.LowWater"/>). A peer's write
/// may evict a set being baked or loaded unless it is pinned by <see cref="Hold"/>. An unpinned writer's set that alone
/// exceeds the bound is trimmed entry by entry, keeping the entry being written. One process's
/// writes leave the store over its bound only until its writer enforces it after each, which a process that ends waits
/// for; Worlds writing it at once count one another's writes only at their next
/// listing, at least once a minute while they write. A file directly in the directory, which no identity's set holds,
/// is a set of its own and ages out first once nothing reads it. A bake's intermediates (its stage checkpoints and
/// sample sets, which let a later bake resume past a stage whose code did not change) are kept apart, in
/// <see cref="IntermediateDirectory"/> under their own bound (<see cref="IntermediateCapacityBytes"/>), so a bake that
/// writes gibibytes of samples never evicts another identity's finished meshes, and a store shipped to a consumer holds
/// meshes alone. The identity records beside the sets
/// (<see cref="IdentityExtension"/>) are bounded apart, by count (<see cref="RecordedIdentities"/>), and are not counted
/// in <see cref="CapacityBytes"/>. Its members are thread-safe.</para>
/// </summary>
public sealed class SdfMeshStore {
    /// <summary>The bytes a store holds at most unless its owner names another bound: eight gibibytes. One per-user
    /// store serves every World and worktree; it retains compressed mesh sets whole when they fit the bound.</summary>
    public const long DefaultCapacityBytes = (8L << 30);
    /// <summary>The bytes a store's intermediates hold at most unless its owner names another bound: eight gibibytes,
    /// counted apart from the finished meshes (<see cref="DefaultCapacityBytes"/>).</summary>
    public const long DefaultIntermediateCapacityBytes = (8L << 30);
    /// <summary>The suffix that names the intermediates' directory beside <see cref="Directory"/>.</summary>
    public const string IntermediateSuffix = "-intermediates";
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
    /// <param name="intermediateCapacityBytes">The bytes the intermediates hold at most.</param>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is <see langword="null"/>, empty or white
    /// space.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacityBytes"/> or
    /// <paramref name="intermediateCapacityBytes"/> is not positive.</exception>
    public SdfMeshStore(string directory, long capacityBytes = DefaultCapacityBytes, IAllocator? allocator = null, long intermediateCapacityBytes = DefaultIntermediateCapacityBytes) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: directory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: capacityBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: intermediateCapacityBytes);

        CapacityBytes = capacityBytes;
        IntermediateCapacityBytes = intermediateCapacityBytes;
        Directory = Path.GetFullPath(path: directory);
        IntermediateDirectory = (Path.TrimEndingDirectorySeparator(path: Directory) + IntermediateSuffix);
        m_allocator = allocator;
    }

    /// <summary>Gets the account a read reserves its workspace in before it allocates it: the process's.</summary>
    public MemoryAccount Account { get; init; } = MemoryAccount.Shared;
    /// <summary>Gets how long a read waits for <see cref="Account"/> to have room for its workspace before it declines
    /// the entry (<see cref="DefaultWorkspacePatience"/>).</summary>
    public TimeSpan WorkspacePatience { get; init; } = DefaultWorkspacePatience;

    /// <summary>Gets the bytes the entries hold at most.</summary>
    public long CapacityBytes { get; }
    /// <summary>Gets the bytes the intermediates hold at most, counted apart from <see cref="CapacityBytes"/>.</summary>
    public long IntermediateCapacityBytes { get; }
    /// <summary>Gets the absolute directory the entries are kept in.</summary>
    public string Directory { get; }
    /// <summary>Gets the absolute directory a bake's intermediates (stage checkpoints and sample sets) are kept in, beside
    /// <see cref="Directory"/>: its name with <see cref="IntermediateSuffix"/>. A store shipped to a consumer is
    /// <see cref="Directory"/> alone.</summary>
    public string IntermediateDirectory { get; }

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
    /// <summary>Returns whether the store may hold an entry for a geometry under a bake's identity, as
    /// <see cref="MayHoldSamples(UInt128, UInt128)"/> answers for a sample set: false only when the identity's directory
    /// has been listed (or found absent) and the entry is not in it nor written since by this process, with no file
    /// read. An entry another process wrote after the listing reads as absent, so its geometry bakes again; one the listing
    /// named that is gone reads as present, and its read misses. A caller that must know asks <see cref="Contains"/>.</summary>
    /// <param name="identity">The bake's identity.</param>
    /// <param name="geometry">The group's geometry.</param>
    /// <returns><see langword="false"/> when the entry is known to be absent.</returns>
    public bool MayHold(UInt128 identity, SdfMeshGeometry geometry) => EntrySetOf(identity, Extension).MayHold(KeyOf(identity, geometry));
    /// <summary>Returns whether the store holds an entry for a geometry under a bake's identity, as this process knows
    /// it: the identity's listed entries with this process's writes and deletes once the listing stands, and the file
    /// system's own answer (<see cref="Contains"/>) until then. Unlike <see cref="MayHold"/> it never answers for an
    /// entry it has not seen, so a caller that decides what to draw from what is stored asks this.</summary>
    /// <param name="identity">The bake's identity.</param>
    /// <param name="geometry">The group's geometry.</param>
    /// <returns><see langword="true"/> when the store holds the entry.</returns>
    public bool Holds(UInt128 identity, SdfMeshGeometry geometry) =>
        (EntrySetOf(identity, Extension).Holds(KeyOf(identity, geometry)) ?? Contains(identity: identity, geometry: geometry));
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

        if (!read && damaged) {
            Delete(path: path);
            Forget(identity, extension, KeyOf(identity: identity, geometry: geometry));
        }
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
    /// spent reading files and decoding them, each summed over the threads that read concurrently; and the bytes its
    /// writer holds for writes it has not finished, with the sample sets it kept none of for want of room
    /// (<see cref="MostPendingWriteBytes"/>).</summary>
    /// <returns>One line of <c>key=value</c> pairs.</returns>
    public string DescribeReads() => string.Create(
        provider: System.Globalization.CultureInfo.InvariantCulture,
        handler: $"store-reads={Interlocked.Read(location: ref m_reads)} store-read-bytes={Interlocked.Read(location: ref m_readBytes)}B store-read-thread-seconds={(((double)Interlocked.Read(location: ref m_readTicks)) / System.Diagnostics.Stopwatch.Frequency):0.000}s store-decode-thread-seconds={(((double)Interlocked.Read(location: ref m_decodeTicks)) / System.Diagnostics.Stopwatch.Frequency):0.000}s store-writes-pending={Interlocked.Read(location: ref m_pendingWriteBytes)}B store-writes-dropped={Interlocked.Read(location: ref m_droppedWrites)}"
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
            Forget(identity, extension, key);

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
    public bool Write(UInt128 identity, SdfMeshGeometry geometry, SdfMesh? mesh, int[] materials) =>
        Write(identity, geometry, mesh, materials, out _);

    // As the public Write, also answering the position planes the entry stored (SdfMeshStoreCodec.Encode), which a
    // written mesh's native copy conforms to; null when the encode never ran.
    internal bool Write(UInt128 identity, SdfMeshGeometry geometry, SdfMesh? mesh, int[] materials, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out SdfMeshStoreCodec.Positions? stored) {
        ArgumentNullException.ThrowIfNull(argument: materials);
        if ((mesh is null) && (materials.Length != 0)) { throw new ArgumentException(message: "An empty bake carries no materials.", paramName: nameof(materials)); }

        var key = KeyOf(geometry: geometry, identity: identity);
        var path = PathOf(identity: identity, key: key);

        stored = null;

        var written = WriteEntry(path, new MeshPayload(key, mesh, materials, ref stored, m_allocator), static (output, entry) => entry.Stored = SdfMeshStoreCodec.Encode(output, entry.Key, entry.Mesh, entry.Materials, entry.Allocator), queued: false);

        KeepIfWritten(written, identity, Extension, key);

        return written;
    }

    // Writes an entry's file, encoded straight into the temporary: atomically, so a reader finds the old entry or the
    // whole new one, and without a flush to stable storage, since every entry ends in a checksum its read refuses and the
    // store derives again on (the store is a cache). A finished mesh and an intermediate are bounded apart, each in its
    // own directory: PathOf puts every entry under the root its extension names, and Enforce, which every written entry
    // passes, is the one place either bound is enforced. A write the bake waits for (`queued` false, a finished mesh)
    // leaves the enforcement to the store's writer, so it never lists the directory; a write already on the writer
    // enforces at once.
    private bool WriteEntry<TState>(string path, TState state, Action<Stream, TState> encode, bool queued) where TState : allows ref struct {
        try {
            Puck.Confinement.AtomicFile.WriteVerifiedCache(path: path, state: state, write: encode);
        } catch (Exception exception) when (Refused(exception: exception)) {
            return false;
        }
        if (queued) {
            Enforce(path: path);
        } else {
            _ = WriteLater(bytes: 0L, behind: Behind.Inline, write: () => Enforce(path: path));
        }
        return true;
    }
    private void Enforce(string path) {
        var finished = IsMeshEntry(name: path);

        _ = Puck.Abstractions.CacheRetention.EnforceSetsOnWrite(
            bound: new Puck.Abstractions.CacheBound(MaxBytes: (finished ? CapacityBytes : IntermediateCapacityBytes), MaxEntries: MaxSets),
            directory: (finished ? Directory : IntermediateDirectory),
            inUse: path,
            isEntry: (finished ? IsMeshEntry : IsIntermediateEntry)
        );
    }

    /// <summary>The bytes the store's writer holds at most for writes it has not finished (their samples and meshes);
    /// a write past them waits for no writer: a checkpoint or a bound's enforcement runs on its caller, and a sample set
    /// is not kept.</summary>
    public const long MostPendingWriteBytes = (512L << 20);
    // How long a process that is ending waits for the store's writer to finish what is queued.
    private static readonly TimeSpan ExitDrain = TimeSpan.FromSeconds(seconds: 10);

    /// <summary>Waits until every write the process's stores queued on their writer (checkpoints, sample sets and the
    /// bounds' enforcement) has finished, those queued meanwhile included, or the timeout passes: a tool that reads the
    /// store a bake just wrote, or holds it to its bound, waits here first. A process that ends waits by itself.</summary>
    /// <param name="timeout">The longest wait.</param>
    /// <returns><see langword="true"/> when no write is left unfinished.</returns>
    public static bool WaitForWrites(TimeSpan timeout) => Writes.WaitIdle(timeout: timeout);
    // The writes no bake waits for (checkpoints, sample sets and the bounds' enforcement), run off the bake's workers on
    // at most two pool items, in the order they came, for every store of the process.
    private static readonly BoundedWorkQueue<Action> Writes = BoundedWorkQueue<Action>.DrainedAtExit(concurrency: 2, exitDrain: ExitDrain, run: static write => write());

    private enum Behind { Inline, Drop }

    private long m_pendingWriteBytes;
    private long m_droppedWrites;
    private MemoryAccount.Owner? m_writeOwner;

    // The owner a queued sample set's copy is charged to while it waits.
    private MemoryAccount.Owner WriteOwner {
        get {
            if (Volatile.Read(location: ref m_writeOwner) is { } owner) { return owner; }
            var created = Account.CreateOwner(allowance: MemoryAllowance.Authoring, memoryClass: MemoryClass.Workspace, name: "mesh-store-writes");

            if (Interlocked.CompareExchange(comparand: null, location1: ref m_writeOwner, value: created) is { } raced) {
                created.Dispose();
                return raced;
            }
            return created;
        }
    }

    // Runs a write on the store's writer, holding `bytes` while it waits; when the writer already holds
    // MostPendingWriteBytes, the write runs on the caller (Inline) or not at all (Drop), and false says it did not run.
    // A failing write leaves the store as it was: the store is a cache.
    private bool WriteLater(long bytes, Behind behind, Action write) {
        if (Interlocked.Add(location1: ref m_pendingWriteBytes, value: bytes) > MostPendingWriteBytes) {
            _ = Interlocked.Add(location1: ref m_pendingWriteBytes, value: -bytes);
            if (behind == Behind.Drop) {
                _ = Interlocked.Increment(location: ref m_droppedWrites);
                return false;
            }
            _ = Quietly(action: write);
            return true;
        }
        Writes.Enqueue(item: () => {
            try {
                _ = Quietly(action: write);
            } finally {
                _ = Interlocked.Add(location1: ref m_pendingWriteBytes, value: -bytes);
            }
        });
        return true;
    }
    private static bool IsMeshEntry(string name) => name.EndsWith(value: Extension, comparisonType: StringComparison.Ordinal);
    private static bool IsIntermediateEntry(string name) =>
        name.EndsWith(value: CheckpointExtension, comparisonType: StringComparison.Ordinal) || name.EndsWith(value: SampleExtension, comparisonType: StringComparison.Ordinal);

    internal bool MayHoldCheckpoint(UInt128 identity, SdfMeshGeometry geometry) => EntrySetOf(identity, CheckpointExtension).MayHold(KeyOf(identity, geometry));

    internal bool TryReadCheckpoint(UInt128 identity, SdfMeshGeometry geometry, out SdfMesh? mesh) {
        try {
            return TryReadInto(identity, geometry, new SdfMeshManagedStorage(), null, out mesh, out _, out _, out _, CheckpointExtension);
        } catch (OutOfMemoryException) {
            mesh = null;
            return false;
        }
    }

    // Keeps a stage's mesh on the store's writer, encoded there straight into the entry's file. A managed mesh is
    // immutable once built and lives while the queued write holds it; a native one may let its parts go once its claims
    // end, so it is encoded here into a buffer the queued write copies to the file. A writer already holding
    // MostPendingWriteBytes writes on the caller, as the bake did before it had one.
    internal void WriteCheckpoint(UInt128 identity, SdfMeshGeometry geometry, SdfMesh? mesh) {
        var key = KeyOf(identity, geometry);
        var path = PathOf(identity, key, CheckpointExtension);

        if (mesh is { OwnsNativeStorage: true }) {
            var encoded = new MemoryStream();

            if (Quietly(action: () => SdfMeshStoreCodec.EncodeExact(encoded, key, mesh, m_allocator))) {
                _ = WriteLater(bytes: encoded.Length, behind: Behind.Inline, write: () => KeepIfWritten(WriteEntry(path, encoded, static (output, buffer) => buffer.WriteTo(stream: output), queued: true), identity, CheckpointExtension, key));
            }
            return;
        }
        _ = WriteLater(bytes: (mesh?.StorageBytes ?? 0L), behind: Behind.Inline, write: () => KeepIfWritten(WriteEntry(path, (key, mesh, allocator: m_allocator), static (output, entry) => SdfMeshStoreCodec.EncodeExact(output, entry.key, entry.mesh, entry.allocator), queued: true), identity, CheckpointExtension, key));
    }

    internal bool TryReadSamples(UInt128 identity, UInt128 key, int length, OwnedBuffer<byte> samples) {
        key = SampleKeyOf(identity, key);
        var path = PathOf(identity, key, SampleExtension);
        var workspace = Workspace;
        using var owner = workspace.CreateOwner(allowance: MemoryAllowance.Authoring, name: "mesh-bake-samples");
        using var entry = new OwnedBuffer<byte>(m_allocator, owner);
        try {
            if (!TryReadEntryBytes(entry, owner, path, in workspace)) { return false; }
            if (!SdfMeshStoreDecoder.TryPayload(entry.GetSpan(), key, out var compressed, out var storedLength, out _) || (storedLength != length)) {
                DeleteSamples(identity, key, path);
                return false;
            }
            if (!workspace.TryReserve(owner, checked((long)entry.Length + length))) { return false; }
            samples.Resize(length, clear: false, preserve: false);
            if (!SdfMeshStoreDecoder.TryInflate(compressed, samples.GetSpan())) {
                DeleteSamples(identity, key, path);
                return false;
            }
            Puck.Abstractions.CacheRetention.Stamp(path);
            return true;
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OutOfMemoryException) {
            // A file the listing named that is gone (evicted, or another World's set) is a miss like any other.
            Forget(identity, SampleExtension, key);
            return false;
        } catch (HostMemoryRefusedException) {
            // A read the host memory account has no room for is a miss; the entry stays known for a later probe.
            return false;
        }
    }

    // Keeps a lattice's samples, copied now while the caller's span lives, on the store's writer: `key` is the sample
    // input the probe asks by (SdfMeshPathBake.SampleInput), computed by the caller while it still holds the lattice's
    // request. A writer already holding MostPendingWriteBytes keeps nothing: a later bake samples that lattice again.
    internal void KeepSamples(UInt128 identity, ReadOnlySpan<byte> samples, UInt128 key) {
        if ((Interlocked.Read(location: ref m_pendingWriteBytes) + samples.Length) > MostPendingWriteBytes) {
            _ = Interlocked.Increment(location: ref m_droppedWrites);
            return;
        }

        var copy = new OwnedBuffer<byte>(allocator: m_allocator, observer: WriteOwner);

        try {
            copy.CopyFrom(samples);
        } catch (Exception exception) when (exception is OutOfMemoryException or HostMemoryRefusedException) {
            // A copy the host memory account has no room for is not kept, as one the writer has no room for is not: the
            // store is a cache, and a later bake samples the lattice again.
            copy.Dispose();
            _ = Interlocked.Increment(location: ref m_droppedWrites);
            return;
        }
        if (!WriteLater(bytes: copy.Length, behind: Behind.Drop, write: () => {
            using (copy) { WriteSamples(identity, key, copy.GetSpan()); }
        })) {
            copy.Dispose();
        }
    }

    // Writes a sample set on the store's writer (KeepSamples); a root of the sampling stage's code (SdfMeshBakeClosure).
    internal void WriteSamples(UInt128 identity, UInt128 key, ReadOnlySpan<byte> samples) {
        key = SampleKeyOf(identity, key);
        KeepIfWritten(WriteEntry(PathOf(identity, key, SampleExtension), new SamplePayload(key, samples), static (output, entry) => SdfMeshStoreCodec.EncodePayload(output, entry.Key, entry.Samples), queued: true), identity, SampleExtension, key);
    }

    /// <summary>Returns whether the store may hold a sample set under a sampling identity: false only when it knows the
    /// identity's directory holds none, which a store that has listed it, or found no directory, answers without reading
    /// a file.</summary>
    /// <param name="identity">The sampling stage's identity.</param>
    /// <returns><see langword="false"/> when no sample set of the identity is held.</returns>
    internal bool MayHoldSamples(UInt128 identity) => EntrySetOf(identity, SampleExtension).MayHoldAny;
    /// <summary>Returns whether the store may hold one sample set: false only when it has listed the identity's directory
    /// (or found none) and the set is not in it nor written since by this process. A set another World wrote after the
    /// listing reads as absent, and its lattice is sampled again; one the listing named that is gone reads as present,
    /// and its read misses.</summary>
    /// <param name="identity">The sampling stage's identity.</param>
    /// <param name="key">The sample input (<c>SdfMeshPathBake.SampleInput</c>).</param>
    /// <returns><see langword="false"/> when the set is known to be absent.</returns>
    internal bool MayHoldSamples(UInt128 identity, UInt128 key) => EntrySetOf(identity, SampleExtension).MayHold(SampleKeyOf(identity, key));

    // The entry keys this store knows, by identity and entry kind (a finished mesh's, a checkpoint's or a sample set's).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(UInt128 Identity, string Extension), EntrySet> m_entrySets = new();

    private EntrySet EntrySetOf(UInt128 identity, string extension) {
        if (m_entrySets.TryGetValue((identity, extension), out var known)) { return known; }
        var set = new EntrySet();

        if (!m_entrySets.TryAdd((identity, extension), set)) { return m_entrySets[(identity, extension)]; }
        var directory = Path.Combine(string.Equals(a: extension, b: Extension, comparisonType: StringComparison.Ordinal) ? Directory : IntermediateDirectory, identity.ToString(format: "x32", provider: null));

        // A cold store has no directory, which one look settles; a directory is listed on the pool, and the set answers
        // "may hold" until the listing stands.
        if (!System.IO.Directory.Exists(directory)) {
            set.Listed([]);
        } else {
            _ = ThreadPool.UnsafeQueueUserWorkItem(callBack: static state => state.Set.Listed(ListEntries(state.Directory, state.Extension)), preferLocal: false, state: (Set: set, Directory: directory, Extension: extension));
        }
        return set;
    }
    // Records an entry this process wrote in the known entries of its identity and kind, when they exist.
    private void KeepIfWritten(bool written, UInt128 identity, string extension, UInt128 key) {
        if (written) { m_entrySets.GetValueOrDefault((identity, extension))?.Add(key); }
    }
    // Records that an entry is gone or refused, so no start asks for it again.
    private void Forget(UInt128 identity, string extension, UInt128 key) => m_entrySets.GetValueOrDefault((identity, extension))?.Remove(key);
    private static List<UInt128> ListEntries(string directory, string extension) {
        var keys = new List<UInt128>();

        try {
            foreach (var path in System.IO.Directory.EnumerateFiles(directory, "*" + extension)) {
                var name = Path.GetFileNameWithoutExtension(path.AsSpan());

                if ((name.Length == 32) && UInt128.TryParse(name, System.Globalization.NumberStyles.HexNumber, null, out var key)) { keys.Add(key); }
            }
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            // A directory that cannot be listed holds nothing this store knows of.
        }
        return keys;
    }
    private void DeleteSamples(UInt128 identity, UInt128 key, string path) {
        Forget(identity, SampleExtension, key);
        Delete(path);
    }
    // A sample set's file key: the sampling identity and the sample input, as a geometry's entry key is formed.
    private static UInt128 SampleKeyOf(UInt128 identity, UInt128 key) => KeyOf(identity, new SdfMeshGeometry(Dynamic: false, Hash: key));

    // The file keys of one identity's entries of one kind: those its directory held when listed, and those this process
    // wrote or deleted since. Until the listing stands, every entry may be held.
    private sealed class EntrySet {
        private readonly Lock m_gate = new();
        private readonly HashSet<UInt128> m_keys = [];
        private readonly HashSet<UInt128> m_removed = [];

        private bool m_listed;

        public bool MayHoldAny {
            get { lock (m_gate) { return (!m_listed || (m_keys.Count > 0)); } }
        }
        // The entries the set holds, or null until its listing stands.
        public int? Count {
            get { lock (m_gate) { return (m_listed ? m_keys.Count : null); } }
        }

        public bool MayHold(UInt128 key) {
            lock (m_gate) { return (!m_listed || m_keys.Contains(key)); }
        }
        // Whether the set holds a key; null until the listing stands, when only the file system knows.
        public bool? Holds(UInt128 key) {
            lock (m_gate) { return (m_listed ? m_keys.Contains(key) : (m_keys.Contains(key) ? true : null)); }
        }
        public void Listed(List<UInt128> keys) {
            lock (m_gate) {
                foreach (var key in keys) {
                    if (!m_removed.Contains(key)) { _ = m_keys.Add(key); }
                }
                m_removed.Clear();
                m_listed = true;
            }
        }
        public void Add(UInt128 key) {
            lock (m_gate) {
                _ = m_keys.Add(key);
                _ = m_removed.Remove(key);
            }
        }
        public void Remove(UInt128 key) {
            lock (m_gate) {
                _ = m_keys.Remove(key);
                if (!m_listed) { _ = m_removed.Add(key); }
            }
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
            // The set's own listing, which the store keeps as it reads and writes, answers without walking the store: a
            // bake restarts often while a world plays, and each start that misses asks.
            var own = EntrySetOf(identity: identity.Value, extension: Extension).Count;

            reason = own switch {
                null => $"identity {value} unchanged: geometry new to its set, which is still being listed",
                0 => $"identity {value} unchanged: its set was evicted, or it never stored one (a store of {CapacityBytes} bytes)",
                _ => $"identity {value} unchanged: geometry new to its set of {own} entries (a store of {CapacityBytes} bytes)",
            };
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
    // A finished mesh lives in its identity's set under Directory (the directory SetDirectoryOf names), an intermediate in
    // its stage identity's set under IntermediateDirectory.
    private string PathOf(UInt128 identity, UInt128 key, string extension = Extension) {
        Span<char> set = stackalloc char[32];
        Span<char> name = stackalloc char[32 + MaxExtensionLength];

        _ = identity.TryFormat(destination: set, charsWritten: out _, format: "x32", provider: null);
        _ = key.TryFormat(destination: name, charsWritten: out _, format: "x32", provider: null);
        extension.CopyTo(destination: name[32..]);

        return Path.Join(
            path1: (string.Equals(a: extension, b: Extension, comparisonType: StringComparison.Ordinal) ? Directory : IntermediateDirectory),
            path2: set,
            path3: name[..(32 + extension.Length)]);
    }
    // The longest extension an entry carries, which PathOf formats into a stack buffer.
    private const int MaxExtensionLength = 32;
    private readonly ref struct SamplePayload(UInt128 key, ReadOnlySpan<byte> samples) {
        public UInt128 Key { get; } = key;
        public ReadOnlySpan<byte> Samples { get; } = samples;
    }
    // A mesh entry's inputs, and where the encode answers the position planes it stored.
    private readonly ref struct MeshPayload {
        public MeshPayload(UInt128 key, SdfMesh? mesh, int[] materials, ref SdfMeshStoreCodec.Positions? stored, IAllocator? allocator) {
            Key = key;
            Mesh = mesh;
            Materials = materials;
            Stored = ref stored;
            Allocator = allocator;
        }

        public IAllocator? Allocator { get; }
        public UInt128 Key { get; }
        public SdfMesh? Mesh { get; }
        public int[] Materials { get; }
        public readonly ref SdfMeshStoreCodec.Positions? Stored;
    }
    // Deletes a file the store owns, leaving one another World holds open for a later trim or refusal to remove.
    private static void Delete(string path) => _ = Quietly(action: () => File.Delete(path: path));
    // Runs a file-system action, or an entry's encode and write, answering whether it ran: one the file system refused,
    // or whose encode found no memory or overflowed an entry's counts, leaves the entry as it was, and a later bake
    // derives it again (the store is a cache).
    private static bool Quietly(Action action) {
        try {
            action();

            return true;
        } catch (Exception exception) when (Refused(exception: exception)) {
            return false;
        }
    }
    // The failures Quietly and WriteEntry answer as a refusal rather than throw.
    private static bool Refused(Exception exception) => (exception is IOException or UnauthorizedAccessException or OutOfMemoryException or OverflowException);
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
    /// <param name="allowance">The allowance the read draws on: play for a read a player's boot makes, authoring for one
    /// only a bake makes.</param>
    /// <returns>The owner.</returns>
    public MemoryAccount.Owner CreateOwner(string name, MemoryAllowance allowance = MemoryAllowance.Play) => Account.CreateOwner(allowance: allowance, memoryClass: MemoryClass.Workspace, name: name);
    /// <summary>Reserves a read's scratch before it is allocated: admitted when the host pool has room or as the
    /// process's one forced job, and otherwise after a wait for room no longer than the patience.</summary>
    /// <param name="owner">The read's owner.</param>
    /// <param name="bytes">The scratch's bytes.</param>
    /// <returns><see langword="false"/> when the read does without.</returns>
    public bool TryReserve(MemoryAccount.Owner owner, long bytes) =>
        owner.TryReserveWithin(patience: Patience, peak: bytes, reason: new Puck.Abstractions.Waiting.WaitReason(Subsystem: "mesh store", WaitsFor: "room in the memory account for a read's workspace"));
}
