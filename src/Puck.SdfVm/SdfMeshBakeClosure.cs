namespace Puck.SdfVm;

/// <summary>
/// The C# a mesh bake executes, read from a build's assemblies: every method the bake's own code reaches through calls,
/// delegates, the overrides of the types it constructs and the implementations of the abstract members it calls, across
/// the <c>Puck</c> assemblies, each hashed by its IL with every token written as the member it names rather than its
/// number, with its locals, its exception regions and the field data it loads; and the layout of every type that code
/// names. Its <see cref="Hash"/> is the code half of the bake's identity (<see cref="SdfMeshBakeIdentity.Code"/>): an
/// edit anywhere else (the rest of the renderer, a new public member of a referenced assembly, a comment, the checkout's
/// path) leaves it as it was, and an edit to anything the bake runs changes it.
/// <para>The bake's roots (<see cref="BakeRoots"/>) are its planning, sampling, contouring and meshing types, the
/// frame-block rows its kernel reads, the program's derivation of the field words the kernel walks (never its appearance), and the store's entry format.
/// The walk stops at the library and the store, which hold meshes rather than make them; at the GPU region and its owed
/// runs and bake-input transport, which allocate and carry a table's words rather than choose them, as the driver does; at the kernels' interfaces and
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
        // Input values and addresses are semantic; allocation, upload and dispatch transport are not.
        ("Puck.SdfVm", "Puck.SdfVm.SdfMeshBakeBatch", "PointRuns"),
        ("Puck.SdfVm", "Puck.SdfVm.SdfMeshBakeBatch", "get_InputRows"),
        ("Puck.SdfVm", "Puck.SdfVm.SdfMeshBakeBatch", "PoseBase"),
        ("Puck.SdfVm", "Puck.SdfVm.SdfMeshPathBake", "Complete"),
        ("Puck.SdfVm", "Puck.SdfVm.SdfMeshBakeBlock", "Write"),
        // The library is a cache boundary, but normalization runs before it persists the product.
        ("Puck.SdfVm", "Puck.SdfVm.SdfMesh", "NativeCopy"),
        ("Puck.SdfVm", "Puck.SdfVm.SdfMeshStore", "Encode"),
        ("Puck.SdfVm", "Puck.SdfVm.SdfMeshStore", "TryDecode"),
        // The program's only constructor derives the field words the bake kernel walks, from the field's inputs and the
        // appearance's outline alone; SdfProgram.Of packs the appearance after it, outside this closure. A group's
        // geometry hashes its instructions, not their packing.
        ("Puck.SignedDistance", "Puck.SignedDistance.SdfProgram", ".ctor"),
    ];
    private static readonly HashSet<string> StopTypes = new(collection: [
        "Puck.Abstractions.Gpu.GpuRegion",
        "Puck.Abstractions.Gpu.GpuUploadRuns",
        "Puck.SdfVm.SdfKernelInterfaces",
        "Puck.SdfVm.SdfMeshBakeClosure",
        "Puck.SdfVm.SdfMeshBakeIdentity",
        "Puck.SdfVm.SdfMeshLibrary",
        "Puck.SdfVm.SdfMeshStore",
        "Puck.SdfVm.SdfWorldInterfaces",
        "Puck.Shaders.SdfWorldPackage",
    ], comparer: StringComparer.Ordinal);
    private static readonly Lazy<SdfMeshBakeClosure> Running = new(valueFactory: static () => Of(directory: AppContext.BaseDirectory));

    private readonly Puck.Assets.CodeClosure m_code;
    private SdfMeshBakeClosure(Puck.Assets.CodeClosure code, string entryVersion) { m_code = code; EntryVersion = entryVersion; }
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
    public bool Reads(Type type) => m_code.Reads(type: type);
    /// <summary>Walks the bake's production roots.</summary>
    /// <param name="directory">The deployed build directory.</param>
    /// <returns>The bake closure.</returns>
    public static SdfMeshBakeClosure Of(string directory) => Of(directory: directory, roots: BakeRoots);
    /// <summary>Walks explicit roots with the bake's transport boundaries.</summary>
    /// <param name="directory">The deployed build directory.</param>
    /// <param name="roots">Assembly, type and method roots.</param>
    /// <returns>The closure.</returns>
    public static SdfMeshBakeClosure Of(string directory, IReadOnlyList<(string Assembly, string Type, string? Method)> roots) => new(
        code: Puck.Assets.CodeClosure.Of(directory: directory, roots: roots, stopTypes: StopTypes),
        entryVersion: SdfMeshStore.VersionText(magic: Puck.Assets.CodeClosure.ReadUInt64Constant(directory: directory, assembly: "Puck.SdfVm", type: "Puck.SdfVm.SdfMeshStore", field: "Magic")));
    /// <summary>Lists added, changed and removed members in ordinal order.</summary>
    /// <param name="members">The current members.</param>
    /// <param name="earlier">The earlier members.</param>
    /// <returns>The changed names.</returns>
    public static IReadOnlyList<string> Changed(IReadOnlyDictionary<string, UInt128> members, IReadOnlyDictionary<string, UInt128> earlier) => Puck.Assets.CodeClosure.Changed(members: members, earlier: earlier);
}
