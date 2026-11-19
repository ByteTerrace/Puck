namespace Puck.SdfVm;

/// <summary>
/// The C# a mesh bake executes, read from a build's assemblies: every method the bake's own code reaches through calls,
/// delegates, the overrides of the types it constructs and the implementations of the abstract members it calls, across
/// the <c>Puck</c> assemblies, each hashed by its IL with every token written as the member it names rather than its
/// number, with its locals, its exception regions and the field data it loads; and the layout of every type that code
/// names. Its <see cref="Hash"/> is the code half of the final bake identity
/// (<see cref="SdfMeshBakeIdentity.Code"/>). Sampling, contouring and simplification also have their own reachable code
/// identities. Each walk starts at the actual producing or restoring calls.
/// <para>The contour walk ends at <see cref="SdfMeshPathBake.SimplificationStage"/>. The simplification walk starts
/// there, and the landing identity covers the complete production path and entry codec.
/// The walk stops at the library and the store, which hold meshes rather than make them; at a mesh's claims and the parts
/// its store can restore (<see cref="SdfMesh"/>'s payload and restorable storage), which own a mesh's memory once it is made; at the bake's report
/// (<see cref="SdfMeshBakeReport"/>), which words, counts and writes what the bake says about itself and so changes no
/// mesh, however its text or counters change; at the GPU region and its owed
/// runs and bake-input transport, which allocate and carry a table's words rather than choose them, as the driver does; at
/// memory accounting and storage (the process memory account and the machine's free memory, which pace when a group is
/// admitted to hold its final lattice, never what it meshes, and the allocators and owned buffers that hold the words the
/// bake's own code writes and reads), as the runtime's arrays are; at the kernels' interfaces and
/// the render graph's package, whose declarations reach the bake through the kernels' bytecode; and at assemblies outside
/// <c>Puck</c>: the runtime is the platform the bake runs on, as the driver is for its kernels. A type a parameter, a
/// local or a field merely carries joins the closure only where code makes, converts or calls into it, and a record's
/// synthesized printing and equality follow its layout. Code the bake reaches only through reflection, or through a
/// runtime callback into a type it never constructs, is outside the walk and needs a root. Immutable.</para>
/// </summary>
public sealed class SdfMeshBakeClosure {
    /// <summary>The roots of the bake's closure: each an assembly, a type's full name, and one method of it by name (every
    /// overload), or <see langword="null"/> for every method of the type and of the types nested in it.</summary>
    public static readonly IReadOnlyList<(string Assembly, string Type, string? Method)> BakeRoots = [
        // Positive production entries, including defaults, frame reconciliation and external GPU readback. The call
        // graph reaches planning, geometry, contours, material cuts, skin and levels; drawing is a separate type.
        ("Puck.SdfVm", "Puck.SdfVm.SdfMeshPathBake", ".ctor"),
        ("Puck.SdfVm", "Puck.SdfVm.SdfMeshPathBake", "PrepareFrame"),
        ("Puck.SdfVm", "Puck.SdfVm.SdfMeshPathBake", "PlanAhead"),
        ("Puck.SdfVm", "Puck.SdfVm.SdfMeshPathBake", "TryTake"),
        // Input values and addresses are semantic; allocation, upload and dispatch transport are not. The GPU client
        // (SdfMeshBakeGpuWork) calls these and nothing else reaches them.
        ("Puck.SdfVm", "Puck.SdfVm.SdfMeshBakeBatch", "PointRuns"),
        ("Puck.SdfVm", "Puck.SdfVm.SdfMeshBakeBatch", "get_InputRows"),
        ("Puck.SdfVm", "Puck.SdfVm.SdfMeshBakeBatch", "PoseBase"),
        ("Puck.SdfVm", "Puck.SdfVm.SdfMeshBakeBatch", "WriteSampled"),
        ("Puck.SdfVm", "Puck.SdfVm.SdfMeshPathBake", "Complete"),
        ("Puck.SdfVm", "Puck.SdfVm.SdfMeshBakeBlock", "Write"),
        // The encoder, decoder and format tag govern the mesh every later run draws.
        ("Puck.SdfVm", "Puck.SdfVm.SdfMeshStoreCodec", nameof(SdfMeshStoreCodec.Encode)),
        ("Puck.SdfVm", "Puck.SdfVm.SdfMeshStoreCodec", nameof(SdfMeshStoreCodec.TryDecode)),
        ("Puck.SdfVm", "Puck.SdfVm.SdfMeshStoreCodec", nameof(SdfMeshStoreCodec.Conform)),
        // The program's only constructor derives the field words the bake kernel walks, from the field's inputs and the
        // appearance's outline alone; SdfProgram.Of packs the appearance after it, outside this closure. A group's
        // geometry hashes its instructions, not their packing.
        ("Puck.SignedDistance", "Puck.SignedDistance.SdfProgram", ".ctor"),
    ];
    private static readonly HashSet<string> StopTypes = new(collection: [
        "Puck.Abstractions.CacheRetention",
        "Puck.Abstractions.Gpu.GpuRegion",
        "Puck.Abstractions.Gpu.GpuUploadRuns",
        // Memory accounting and storage: admission paces when a group holds its final lattice, never what it meshes, and a
        // buffer allocates and holds the words the bake's code writes and reads through it.
        "Puck.Abstractions.Memory.GuardedAllocator",
        "Puck.Abstractions.Memory.IAllocationObserver",
        "Puck.Abstractions.Memory.IAllocator",
        "Puck.Abstractions.Memory.OwnedBuffer`1",
        "Puck.Hosting.HostMemory",
        "Puck.Hosting.Jobs.MemoryAccount",
        "Puck.SdfVm.SdfKernelInterfaces",
        "Puck.SdfVm.SdfMesh/Payload",
        "Puck.SdfVm.SdfMesh/Restorable",
        "Puck.SdfVm.SdfMeshBakeClosure",
        "Puck.SdfVm.SdfMeshBakeIdentity",
        "Puck.SdfVm.SdfMeshBakeReport",
        "Puck.SdfVm.SdfMeshLibrary",
        "Puck.SdfVm.SdfMeshStore",
        "Puck.SdfVm.SdfWorldInterfaces",
        "Puck.Shaders.SdfWorldPackage",
    ], comparer: StringComparer.Ordinal);
    private static readonly Lazy<SdfMeshBakeClosure> Running = new(valueFactory: static () => Of(directory: AppContext.BaseDirectory));

    private readonly Puck.Assets.CodeClosure m_code;
    private readonly Puck.Assets.CodeClosure? m_sampling;
    private readonly Puck.Assets.CodeClosure? m_contouring;
    private readonly Puck.Assets.CodeClosure? m_simplification;
    private readonly Puck.Assets.CodeClosure? m_checkpoints;
    private SdfMeshBakeClosure(Puck.Assets.CodeClosure code, string entryVersion,
        Puck.Assets.CodeClosure? sampling = null, Puck.Assets.CodeClosure? contouring = null,
        Puck.Assets.CodeClosure? simplification = null, Puck.Assets.CodeClosure? checkpoints = null) {
        m_code = code;
        EntryVersion = entryVersion;
        m_sampling = sampling;
        m_contouring = contouring;
        m_simplification = simplification;
        m_checkpoints = checkpoints;
    }

    internal UInt128 Sampling => RunningHash(m_sampling);
    internal UInt128 Contouring => RunningHash(m_contouring);
    internal UInt128 Simplification => RunningHash(m_simplification);
    internal UInt128 Checkpoints => RunningHash(m_checkpoints);

    private static UInt128 RunningHash(Puck.Assets.CodeClosure? closure) => closure is null ? 0
        : SdfMeshBakeIdentity.Fold(closure.Hash, Puck.Abstractions.CodeRevision.AppliedTo(closure.Reads));
    /// <summary>Gets the running build's bake closure.</summary>
    public static SdfMeshBakeClosure Current => Running.Value;
    /// <summary>Gets the store entry format tag in the walked build.</summary>
    public string EntryVersion { get; }
    /// <summary>Gets the reachable code identity.</summary>
    public UInt128 Hash => m_code.Hash;
    /// <summary>Gets normalized members and their hashes.</summary>
    public IReadOnlyDictionary<string, UInt128> Members => m_code.Members;
    /// <summary>Gets the first member reaching each member.</summary>
    public IReadOnlyDictionary<string, string> Reasons => m_code.Reasons;
    /// <summary>Whether the closure reads an updated type.</summary>
    /// <param name="type">The updated type.</param>
    /// <returns>Whether it is read.</returns>
    public bool Reads(Type type) => m_code.Reads(type: type) || (m_sampling?.Reads(type) ?? false) ||
        (m_contouring?.Reads(type) ?? false) || (m_simplification?.Reads(type) ?? false) || (m_checkpoints?.Reads(type) ?? false);
    /// <summary>Walks the bake's production roots.</summary>
    /// <param name="directory">The deployed build directory.</param>
    /// <returns>The bake closure.</returns>
    public static SdfMeshBakeClosure Of(string directory) {
        var codec = typeof(SdfMeshStoreCodec).FullName!;
        var simplification = typeof(SdfMeshPathBake.SimplificationStage).FullName!.Replace('+', '/');
        var contourStops = new HashSet<string>(StopTypes, StringComparer.Ordinal) { simplification };
        var contourRoots = BakeRoots.Where(root => root.Type != codec).ToArray();
        (string Assembly, string Type, string? Method)[] sampleRoots = [
            ("Puck.SdfVm", typeof(SdfMeshBakeGpuWork).FullName!, nameof(SdfMeshBakeGpuWork.Record)),
            ("Puck.SdfVm", typeof(SdfWorldTables).FullName!, "WriteProgramWords"),
            ("Puck.SdfVm", typeof(SdfWorldTables).FullName!, nameof(SdfWorldTables.SetGlyphAtlas)),
            ("Puck.SdfVm", typeof(SdfMeshPathBake).FullName!, "SampleInput"),
            ("Puck.SdfVm", typeof(SdfMeshPathBake).FullName!, "CheckpointInput"),
            ("Puck.SdfVm", typeof(SdfMeshStore).FullName!, nameof(SdfMeshStore.TryReadSamples)),
            ("Puck.SdfVm", typeof(SdfMeshStore).FullName!, nameof(SdfMeshStore.WriteSamples)),
            ("Puck.SignedDistance", typeof(Puck.SignedDistance.SdfProgram).FullName!, ".ctor"),
        ];
        var sampling = Puck.Assets.CodeClosure.Of(directory, stopTypes: StopTypes, roots: sampleRoots);
        var contouring = Puck.Assets.CodeClosure.Of(directory, roots: contourRoots, stopTypes: contourStops);
        var meshCheckpoints = Puck.Assets.CodeClosure.Of(directory, stopTypes: StopTypes, roots: [
            ("Puck.SdfVm", codec, nameof(SdfMeshStoreCodec.EncodeExact)),
            ("Puck.SdfVm", codec, nameof(SdfMeshStoreCodec.TryDecode)),
        ]);
        var simplified = Puck.Assets.CodeClosure.Of(directory, stopTypes: StopTypes, roots: [
            ("Puck.SdfVm", simplification, nameof(SdfMeshPathBake.SimplificationStage.Run)),
        ]);
        return new SdfMeshBakeClosure(
            Puck.Assets.CodeClosure.Of(directory, roots: [.. BakeRoots, .. sampleRoots], stopTypes: StopTypes), ReadEntryVersion(directory),
            sampling: sampling, contouring: contouring, simplification: simplified, checkpoints: meshCheckpoints);
    }
    /// <summary>Walks explicit roots with the bake's transport boundaries.</summary>
    /// <param name="directory">The deployed build directory.</param>
    /// <param name="roots">Assembly, type and method roots.</param>
    /// <returns>The closure.</returns>
    public static SdfMeshBakeClosure Of(string directory, IReadOnlyList<(string Assembly, string Type, string? Method)> roots) => new(
        code: Puck.Assets.CodeClosure.Of(directory: directory, roots: roots, stopTypes: StopTypes),
        entryVersion: ReadEntryVersion(directory));
    private static string ReadEntryVersion(string directory) => SdfMeshStore.VersionText(magic: Puck.Assets.CodeClosure.ReadUInt64Constant(directory: directory, assembly: "Puck.SdfVm", type: "Puck.SdfVm.SdfMeshStore", field: "Magic"));
    /// <summary>Lists added, changed and removed members in ordinal order.</summary>
    /// <param name="members">The current members.</param>
    /// <param name="earlier">The earlier members.</param>
    /// <returns>The changed names.</returns>
    public static IReadOnlyList<string> Changed(IReadOnlyDictionary<string, UInt128> members, IReadOnlyDictionary<string, UInt128> earlier) => Puck.Assets.CodeClosure.Changed(members: members, earlier: earlier);
}
