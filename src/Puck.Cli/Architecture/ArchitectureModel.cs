using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;

namespace Puck.Cli.Architecture;

/// <summary>One project as the architecture model sees it: its declaration and its declared out-edges.</summary>
/// <param name="Edges">Names of the projects this one declares a real assembly reference to.</param>
/// <param name="File">The project file's full path.</param>
/// <param name="Kind">The declared <c>&lt;PuckKind&gt;</c>, empty when the project declares none.</param>
/// <param name="Layer">The declared <c>&lt;PuckLayer&gt;</c>, empty for a terminal kind.</param>
/// <param name="Name">The project's name.</param>
internal sealed record ArchitectureProject(
    IReadOnlyList<string> Edges,
    string File,
    string Kind,
    string Layer,
    string Name);
/// <summary>
/// The repository's architecture as read off disk: the policy from <c>build/Architecture.props</c> and the
/// per-project declarations from the csproj files themselves.
/// </summary>
/// <remarks>
/// This model is built from DECLARED references walked transitively. The build-time gate is the authority
/// and reads something subtly different — the RESOLVED reference set, which is what the compiler is actually
/// handed. The two agree on the settled graph, and where they could diverge is worth naming rather than
/// smoothing over: a reference that arrives from a targets file, a package, or an SDK would appear in the
/// resolved set and never in any csproj. That is precisely why the gate does not read this model.
/// </remarks>
internal sealed class ArchitectureModel {
    private ArchitectureModel(
        IReadOnlyDictionary<string, string> backendExceptions,
        IReadOnlyDictionary<string, bool> kinds,
        IReadOnlyList<string> layers,
        IReadOnlyDictionary<string, IReadOnlyList<string>> friends,
        IReadOnlyDictionary<string, IReadOnlyList<string>> profiles,
        IReadOnlyDictionary<string, ArchitectureProject> projects,
        string repositoryRoot) {
        BackendExceptions = backendExceptions;
        Friends = friends;
        Kinds = kinds;
        Layers = layers;
        Profiles = profiles;
        Projects = projects;
        RepositoryRoot = repositoryRoot;
    }

    /// <summary>Named backend-quarantine exceptions, project name to the recorded reason.</summary>
    public IReadOnlyDictionary<string, string> BackendExceptions { get; }
    /// <summary>Declared friend sets, project name to the assemblies it grants internals access.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Friends { get; }
    /// <summary>The kind taxonomy: kind name to whether it is ranked.</summary>
    public IReadOnlyDictionary<string, bool> Kinds { get; }
    /// <summary>The layer rows, top first — the index is the rank.</summary>
    public IReadOnlyList<string> Layers { get; }
    /// <summary>Exact-equality lane profiles, project name to its permitted closure.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; }
    /// <summary>Every project in scope, by name.</summary>
    public IReadOnlyDictionary<string, ArchitectureProject> Projects { get; }
    /// <summary>The repository root every path in this model is anchored at.</summary>
    public string RepositoryRoot { get; }

    private static IEnumerable<XElement> Items(XDocument ledger, string name) =>
        ledger.Descendants().Where(predicate: e => (e.Name.LocalName == name));
    private static ArchitectureProject ReadProject(string file) {
        var document = XDocument.Load(uri: file);
        var edges = new List<string>();

        foreach (var element in document.Descendants().Where(predicate: e => (e.Name.LocalName == "ProjectReference"))) {
            var include = (element.Attribute(name: "Include") ?? element.Attribute(name: "Update"));

            if (include is null) {
                continue;
            }

            var outputAssembly =
                (element.Attribute(name: "ReferenceOutputAssembly")?.Value
                ?? element.Elements().FirstOrDefault(predicate: e => (e.Name.LocalName == "ReferenceOutputAssembly"))?.Value);

            if (string.Equals(
                a: outputAssembly,
                b: "false",
                comparisonType: StringComparison.OrdinalIgnoreCase
            )) {
                continue;
            }

            edges.Add(item: Path.GetFileNameWithoutExtension(path: include.Value.Replace(
                newChar: Path.DirectorySeparatorChar,
                oldChar: '\\'
            )));
        }

        edges.Sort(comparer: StringComparer.OrdinalIgnoreCase);

        return new ArchitectureProject(
            Edges: edges,
            File: file,
            Kind: (document.Descendants().FirstOrDefault(predicate: e => (e.Name.LocalName == "PuckKind"))?.Value.Trim() ?? ""),
            Layer: (document.Descendants().FirstOrDefault(predicate: e => (e.Name.LocalName == "PuckLayer"))?.Value.Trim() ?? ""),
            Name: Path.GetFileNameWithoutExtension(path: file)
        );
    }
    private static IReadOnlyList<string> Split(string? value) =>
        [.. (value ?? "").Split(
                options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries,
                separator: ';'
            )];

    /// <summary>The transitive closure of a project's declared references, excluding the project itself.</summary>
    /// <param name="name">The project to walk from.</param>
    /// <returns>Every project reachable by declared references, sorted.</returns>
    public IReadOnlyList<string> Closure(string name) {
        var queue = new Queue<string>();
        var seen = new SortedSet<string>(comparer: StringComparer.OrdinalIgnoreCase);

        queue.Enqueue(item: name);

        while (queue.Count != 0) {
            if (!Projects.TryGetValue(
                key: queue.Dequeue(),
                value: out var project
            )) {
                continue;
            }

            foreach (var edge in project.Edges) {
                if (seen.Add(item: edge)) {
                    queue.Enqueue(item: edge);
                }
            }
        }

        return [.. seen];
    }
    /// <summary>Reads the policy ledger and every in-scope project declaration.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <returns>The assembled model.</returns>
    public static ArchitectureModel Load(string repositoryRoot) {
        var ledger = XDocument.Load(uri: Path.Combine(
            path1: repositoryRoot,
            path2: "build",
            path3: "Architecture.props"
        ));
        var backendExceptions = new Dictionary<string, string>(comparer: StringComparer.OrdinalIgnoreCase);
        var friends = new Dictionary<string, IReadOnlyList<string>>(comparer: StringComparer.OrdinalIgnoreCase);
        var kinds = new Dictionary<string, bool>(comparer: StringComparer.OrdinalIgnoreCase);
        var layers = new List<string>();
        var profiles = new Dictionary<string, IReadOnlyList<string>>(comparer: StringComparer.OrdinalIgnoreCase);
        var projects = new Dictionary<string, ArchitectureProject>(comparer: StringComparer.OrdinalIgnoreCase);

        foreach (var item in Items(
            ledger: ledger,
            name: "PuckArchitectureLayer"
        ).OrderBy(keySelector: e => int.Parse(s: (e.Attribute(name: "Rank")?.Value ?? "0")))) {
            layers.Add(item: item.Attribute(name: "Include")!.Value);
        }

        foreach (var item in Items(
            ledger: ledger,
            name: "PuckArchitectureKind"
        )) {
            kinds[item.Attribute(name: "Include")!.Value] = string.Equals(
                a: item.Attribute(name: "Ranked")?.Value,
                b: "true",
                comparisonType: StringComparison.OrdinalIgnoreCase
            );
        }

        foreach (var item in Items(
            ledger: ledger,
            name: "PuckArchitectureBackendException"
        )) {
            backendExceptions[item.Attribute(name: "Include")!.Value] = (item.Attribute(name: "Reason")?.Value ?? "");
        }

        foreach (var item in Items(
            ledger: ledger,
            name: "PuckArchitectureProfile"
        )) {
            profiles[item.Attribute(name: "Include")!.Value] = Split(value: item.Attribute(name: "Closure")?.Value);
        }

        foreach (var item in Items(
            ledger: ledger,
            name: "PuckArchitectureFriends"
        )) {
            friends[item.Attribute(name: "Include")!.Value] = Split(value: item.Attribute(name: "Friends")?.Value);
        }

        // The scope predicate the ledger states in prose, applied: src/ and tests/ only. A quarantined tree
        // is outside the gate because quarantine means ungated, and experimental/ is excluded here for the
        // same reason it is excluded there rather than by a second, quietly-different rule. src/Web.Functions
        // is excluded for the reason Architecture.props' PuckArchitectureGateEnabled predicate states beside
        // its own matching exclusion: a separately built platform app that shares this tree, gitignored and
        // untracked, never meant to join the Puck project graph — not quarantined code awaiting retirement.
        foreach (var root in new[] { "src", "tests" }) {
            var directory = Path.Combine(
                path1: repositoryRoot,
                path2: root
            );

            if (!Directory.Exists(path: directory)) {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(
                path: directory,
                searchOption: SearchOption.AllDirectories,
                searchPattern: "*.csproj"
            )) {
                if (
                    file.Contains(value: $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                    file.Contains(value: $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                    file.Contains(
                    comparisonType: StringComparison.OrdinalIgnoreCase,
                    value: $"{Path.DirectorySeparatorChar}src{Path.DirectorySeparatorChar}Web.Functions{Path.DirectorySeparatorChar}"
                )
                ) {
                    continue;
                }

                var project = ReadProject(file: file);

                projects[project.Name] = project;
            }
        }

        return new ArchitectureModel(
            backendExceptions: backendExceptions,
            friends: friends,
            kinds: kinds,
            layers: layers,
            profiles: profiles,
            projects: projects,
            repositoryRoot: repositoryRoot
        );
    }
    /// <summary>The rank of a project: its layer's index, or -1 when its kind is terminal.</summary>
    /// <param name="project">The project to rank.</param>
    /// <returns>The rank, or <see cref="int.MaxValue"/> when the declaration cannot be resolved.</returns>
    public int RankOf(ArchitectureProject project) {
        if (
            Kinds.TryGetValue(
            key: project.Kind,
            value: out var ranked
        ) &&
            !ranked
        ) {
            return -1;
        }

        var index = Layers.ToList().FindIndex(match: l => string.Equals(
            a: l,
            b: project.Layer,
            comparisonType: StringComparison.OrdinalIgnoreCase
        ));

        return ((index < 0)
            ? int.MaxValue
            : index
        );
    }
    /// <summary>
    /// The friend set recorded in a compiled assembly, read from the PE's metadata rather than from any
    /// declaration.
    /// </summary>
    /// <remarks>
    /// Scanning declarations trusts the thing being checked, and it is not even complete here: this
    /// repository declares friends BOTH as <c>[assembly: InternalsVisibleTo]</c> in
    /// <c>Properties/AssemblyInfo.cs</c> and as <c>&lt;InternalsVisibleTo&gt;</c> csproj items. Only the
    /// compiled assembly sees both, because the csproj form is code-generated into the same attribute.
    /// </remarks>
    /// <param name="assemblyPath">The compiled assembly to read.</param>
    /// <returns>The friend assembly names, sorted; empty when the assembly has none.</returns>
    public static IReadOnlyList<string> ReadFriendsFromAssembly(string assemblyPath) {
        using var stream = File.OpenRead(path: assemblyPath);
        using var peReader = new PEReader(peStream: stream);

        var metadata = peReader.GetMetadataReader();
        var found = new List<string>();

        foreach (var handle in metadata.CustomAttributes) {
            var attribute = metadata.GetCustomAttribute(handle: handle);

            if (attribute.Constructor.Kind != HandleKind.MemberReference) {
                continue;
            }

            var constructor = metadata.GetMemberReference(handle: ((MemberReferenceHandle)attribute.Constructor));

            if (constructor.Parent.Kind != HandleKind.TypeReference) {
                continue;
            }

            if (metadata.GetString(handle: metadata.GetTypeReference(handle: ((TypeReferenceHandle)constructor.Parent)).Name) != "InternalsVisibleToAttribute") {
                continue;
            }

            var value = attribute.DecodeValue(provider: new StringOnlyAttributeTypeProvider());

            if (
                (value.FixedArguments.Length != 0) &&
                (value.FixedArguments[0].Value is string argument)
            ) {
                // An IVT grant may carry a public key after a comma; the assembly name is what identifies
                // the friend.
                found.Add(item: argument.Split(',')[0].Trim());
            }
        }

        found.Sort(comparer: StringComparer.OrdinalIgnoreCase);

        return found;
    }

    /// <summary>
    /// Decodes only the <see cref="string"/> arguments an <c>InternalsVisibleTo</c> attribute carries; every
    /// other shape throws, because encountering one would mean this reader matched the wrong attribute.
    /// </summary>
    private sealed class StringOnlyAttributeTypeProvider : ICustomAttributeTypeProvider<string> {
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) =>
            ((typeCode == PrimitiveTypeCode.String)
                ? "string"
                : throw new NotSupportedException(message: $"unexpected primitive {typeCode} on an InternalsVisibleTo attribute")
            );
        public string GetSZArrayType(string elementType) =>
            throw new NotSupportedException(message: "InternalsVisibleTo carries no array argument");
        public string GetSystemType() =>
            throw new NotSupportedException(message: "InternalsVisibleTo carries no System.Type argument");
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            throw new NotSupportedException(message: "InternalsVisibleTo names no defined type");
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
            throw new NotSupportedException(message: "InternalsVisibleTo names no referenced type");
        public string GetTypeFromSerializedName(string name) =>
            throw new NotSupportedException(message: "InternalsVisibleTo names no serialized type");
        public PrimitiveTypeCode GetUnderlyingEnumType(string type) =>
            throw new NotSupportedException(message: "InternalsVisibleTo carries no enum argument");
        public bool IsSystemType(string type) =>
            false;
    }
}
