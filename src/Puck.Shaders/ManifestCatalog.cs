namespace Puck.Shaders;

/// <summary>
/// The manifests of one document kind shipped under a directory tree, indexed by the id their file name carries:
/// every <c>&lt;id&gt;&lt;suffix&gt;</c> anywhere below the root. Enumeration reads file names only; a manifest is
/// parsed and validated on <see cref="Load"/>, so shipping a kind is exactly shipping its manifest beside the rest
/// of a deploy's tree — no code registration.
/// </summary>
/// <remarks>A derived catalog supplies its file suffix, the noun its diagnostics read with, and the call into its
/// manifest type's loader.</remarks>
/// <typeparam name="TManifest">The manifest type this catalog loads.</typeparam>
public abstract class ManifestCatalog<TManifest> {
    private readonly string m_description;
    private readonly Dictionary<string, string> m_pathsById;

    /// <summary>Adopts an already-scanned id-to-path index.</summary>
    /// <param name="description">The singular, lower-case noun a diagnostic names this kind with (for example <c>shader set</c>).</param>
    /// <param name="pathsById">The index, ordinally keyed, produced by <see cref="ScanPaths"/>.</param>
    protected ManifestCatalog(string description, Dictionary<string, string> pathsById) {
        ArgumentException.ThrowIfNullOrEmpty(argument: description);
        ArgumentNullException.ThrowIfNull(argument: pathsById);

        m_description = description;
        m_pathsById = pathsById;
    }

    /// <summary>Gets the shipped ids, sorted ordinally.</summary>
    public IReadOnlyList<string> Ids => m_pathsById.Keys.Order(comparer: StringComparer.Ordinal).ToList();

    /// <summary>Scans a directory tree for the manifests carrying one file suffix. A missing root yields an empty index.</summary>
    /// <param name="description">The singular, lower-case noun a collision diagnostic names this kind with.</param>
    /// <param name="fileSuffix">The manifest file suffix, including its leading dot.</param>
    /// <param name="rootDirectory">The root to scan.</param>
    /// <returns>The id-to-path index a derived catalog hands to its base constructor.</returns>
    /// <exception cref="InvalidDataException">Two manifests below the root share an id.</exception>
    protected static Dictionary<string, string> ScanPaths(string description, string fileSuffix, string rootDirectory) {
        ArgumentException.ThrowIfNullOrEmpty(argument: rootDirectory);

        var pathsById = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        if (Directory.Exists(path: rootDirectory)) {
            foreach (var path in Directory.EnumerateFiles(path: rootDirectory, searchOption: SearchOption.AllDirectories, searchPattern: $"*{fileSuffix}")) {
                var id = Path.GetFileName(path: path)[..^fileSuffix.Length];

                if (!pathsById.TryAdd(key: id, value: path)) {
                    throw new InvalidDataException(message: $"The {description} '{id}' is shipped twice under '{rootDirectory}': '{pathsById[id]}' and '{path}'.");
                }
            }
        }

        return pathsById;
    }

    /// <summary>Determines whether a manifest with an id is shipped.</summary>
    /// <param name="id">The id.</param>
    /// <returns><see langword="true"/> when a manifest with that id was found.</returns>
    public bool Contains(string id) => m_pathsById.ContainsKey(key: id);
    /// <summary>Loads and validates a shipped manifest.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The manifest.</returns>
    /// <exception cref="KeyNotFoundException">No manifest with that id was found.</exception>
    public TManifest Load(string id) {
        return (m_pathsById.TryGetValue(key: id, value: out var path)
            ? LoadManifest(manifestPath: path)
            : throw new KeyNotFoundException(message: $"No {m_description} '{id}' is shipped; the shipped ids are: {string.Join(separator: ", ", values: Ids)}.")
        );
    }

    /// <summary>Parses and validates one manifest file — the call into the manifest type's own loader.</summary>
    /// <param name="manifestPath">The manifest's path.</param>
    /// <returns>The manifest.</returns>
    protected abstract TManifest LoadManifest(string manifestPath);

    /// <summary>Gets a shipped manifest's path.</summary>
    /// <param name="id">The id.</param>
    /// <param name="path">The manifest path, when found.</param>
    /// <returns><see langword="true"/> when found.</returns>
    public bool TryGetPath(string id, out string path) => m_pathsById.TryGetValue(key: id, value: out path!);
}
