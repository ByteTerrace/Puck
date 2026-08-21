namespace Puck.Shaders;

/// <summary>
/// The probe kinds shipped under a directory tree, found by their manifests: every
/// <c>&lt;id&gt;.puck.probe.json</c> anywhere below the root, indexed by id. Enumeration reads file names only; a
/// manifest is parsed and validated on <see cref="Load"/>. A document's <c>probes[].kind</c> selects a
/// kind by id, so shipping one is exactly shipping its manifest (and, for a kernel-class kind, its HLSL source)
/// beside the rest of a deploy's <c>Assets/Probes</c> tree — no code registration.
/// </summary>
public sealed class ProbeKindCatalog {
    private readonly Dictionary<string, string> m_pathsById;

    private ProbeKindCatalog(Dictionary<string, string> pathsById) {
        m_pathsById = pathsById;
    }

    /// <summary>Gets the shipped ids, sorted ordinally.</summary>
    public IReadOnlyList<string> Ids => m_pathsById.Keys.Order(comparer: StringComparer.Ordinal).ToList();

    /// <summary>Scans a directory tree for manifests. A missing root yields an empty catalog.</summary>
    /// <param name="rootDirectory">The root to scan.</param>
    /// <returns>The catalog.</returns>
    /// <exception cref="InvalidDataException">Two manifests below the root share an id.</exception>
    public static ProbeKindCatalog Scan(string rootDirectory) {
        ArgumentException.ThrowIfNullOrEmpty(argument: rootDirectory);

        var pathsById = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        if (Directory.Exists(path: rootDirectory)) {
            foreach (var path in Directory.EnumerateFiles(path: rootDirectory, searchOption: SearchOption.AllDirectories, searchPattern: $"*{ProbeKindManifest.FileSuffix}")) {
                var id = Path.GetFileName(path: path)[..^ProbeKindManifest.FileSuffix.Length];

                if (!pathsById.TryAdd(key: id, value: path)) {
                    throw new InvalidDataException(message: $"Probe kind '{id}' is shipped twice under '{rootDirectory}': '{pathsById[id]}' and '{path}'.");
                }
            }
        }

        return new ProbeKindCatalog(pathsById: pathsById);
    }
    /// <summary>Determines whether a kind is shipped.</summary>
    /// <param name="id">The kind's id.</param>
    /// <returns><see langword="true"/> when a manifest with that id was found.</returns>
    public bool Contains(string id) => m_pathsById.ContainsKey(key: id);
    /// <summary>Loads and validates a shipped kind's manifest.</summary>
    /// <param name="id">The kind's id.</param>
    /// <returns>The manifest.</returns>
    /// <exception cref="KeyNotFoundException">No manifest with that id was found.</exception>
    public ProbeKindManifest Load(string id) {
        return (m_pathsById.TryGetValue(key: id, value: out var path)
            ? ProbeKindManifest.Load(manifestPath: path)
            : throw new KeyNotFoundException(message: $"No probe kind '{id}' is shipped; the shipped ids are: {string.Join(separator: ", ", values: Ids)}.")
        );
    }
    /// <summary>Gets a shipped manifest's path.</summary>
    /// <param name="id">The kind's id.</param>
    /// <param name="path">The manifest path, when found.</param>
    /// <returns><see langword="true"/> when found.</returns>
    public bool TryGetPath(string id, out string path) => m_pathsById.TryGetValue(key: id, value: out path!);
}
