namespace Puck.Shaders;

/// <summary>
/// The shader sets shipped under a directory tree, found by their manifests: every <c>&lt;id&gt;.puck.shader.json</c>
/// anywhere below the root, indexed by id. Enumeration reads file names only; a manifest is parsed and validated on
/// <see cref="Load"/>. A document selects a set by id, so shipping one is exactly shipping its manifest beside its
/// bytecode — no code registration.
/// </summary>
public sealed class ShaderSetCatalog {
    private readonly Dictionary<string, string> m_pathsById;

    private ShaderSetCatalog(Dictionary<string, string> pathsById) {
        m_pathsById = pathsById;
    }

    /// <summary>Gets the shipped ids, sorted ordinally.</summary>
    public IReadOnlyList<string> Ids => m_pathsById.Keys.Order(comparer: StringComparer.Ordinal).ToList();

    /// <summary>Scans a directory tree for manifests. A missing root yields an empty catalog.</summary>
    /// <param name="rootDirectory">The root to scan.</param>
    /// <returns>The catalog.</returns>
    /// <exception cref="InvalidDataException">Two manifests below the root share an id.</exception>
    public static ShaderSetCatalog Scan(string rootDirectory) {
        ArgumentException.ThrowIfNullOrEmpty(argument: rootDirectory);

        var pathsById = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        if (Directory.Exists(path: rootDirectory)) {
            foreach (var path in Directory.EnumerateFiles(path: rootDirectory, searchOption: SearchOption.AllDirectories, searchPattern: $"*{ShaderSetManifest.FileSuffix}")) {
                var id = Path.GetFileName(path: path)[..^ShaderSetManifest.FileSuffix.Length];

                if (!pathsById.TryAdd(key: id, value: path)) {
                    throw new InvalidDataException(message: $"Shader set '{id}' is shipped twice under '{rootDirectory}': '{pathsById[id]}' and '{path}'.");
                }
            }
        }

        return new ShaderSetCatalog(pathsById: pathsById);
    }
    /// <summary>Determines whether a set is shipped.</summary>
    /// <param name="id">The set's id.</param>
    /// <returns><see langword="true"/> when a manifest with that id was found.</returns>
    public bool Contains(string id) => m_pathsById.ContainsKey(key: id);
    /// <summary>Loads and validates a shipped set's manifest.</summary>
    /// <param name="id">The set's id.</param>
    /// <returns>The manifest.</returns>
    /// <exception cref="KeyNotFoundException">No manifest with that id was found.</exception>
    public ShaderSetManifest Load(string id) {
        return (m_pathsById.TryGetValue(key: id, value: out var path)
            ? ShaderSetManifest.Load(manifestPath: path)
            : throw new KeyNotFoundException(message: $"No shader set '{id}' is shipped; the shipped ids are: {string.Join(separator: ", ", values: Ids)}.")
        );
    }
    /// <summary>Gets a shipped manifest's path.</summary>
    /// <param name="id">The set's id.</param>
    /// <param name="path">The manifest path, when found.</param>
    /// <returns><see langword="true"/> when found.</returns>
    public bool TryGetPath(string id, out string path) => m_pathsById.TryGetValue(key: id, value: out path!);
}
