namespace Puck.Shaders;

/// <summary>
/// The shader sets shipped under a directory tree, found by their manifests: every <c>&lt;id&gt;.puck.shader.json</c>
/// anywhere below the root, indexed by id. A document selects a set by id, so shipping one is exactly shipping its
/// manifest beside its bytecode — no code registration.
/// </summary>
public sealed class ShaderSetCatalog : ManifestCatalog<ShaderSetManifest> {
    private const string Description = "shader set";

    private ShaderSetCatalog(Dictionary<string, string> pathsById) : base(description: Description, pathsById: pathsById) { }

    /// <summary>Scans a directory tree for shader-set manifests. A missing root yields an empty catalog.</summary>
    /// <param name="rootDirectory">The root to scan.</param>
    /// <returns>The catalog.</returns>
    /// <exception cref="InvalidDataException">Two manifests below the root share an id.</exception>
    public static ShaderSetCatalog Scan(string rootDirectory) =>
        new(pathsById: ScanPaths(
            description: Description,
            fileSuffix: ShaderSetManifest.FileSuffix,
            rootDirectory: rootDirectory
        ));

    /// <inheritdoc/>
    protected override ShaderSetManifest LoadManifest(string manifestPath) => ShaderSetManifest.Load(manifestPath: manifestPath);
}
