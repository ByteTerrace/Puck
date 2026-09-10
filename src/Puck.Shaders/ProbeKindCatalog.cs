namespace Puck.Shaders;

/// <summary>
/// The probe kinds shipped under a directory tree, found by their manifests: every
/// <c>&lt;id&gt;.puck.probe.json</c> anywhere below the root, indexed by id. A document's <c>probes[].kind</c>
/// selects a kind by id, so shipping one is exactly shipping its manifest (and, for a kernel-class kind, its HLSL
/// source) beside the rest of a deploy's <c>Assets/Probes</c> tree — no code registration.
/// </summary>
public sealed class ProbeKindCatalog : ManifestCatalog<ProbeKindManifest> {
    private const string Description = "probe kind";

    private ProbeKindCatalog(Dictionary<string, string> pathsById) : base(description: Description, pathsById: pathsById) { }

    /// <summary>Scans a directory tree for probe-kind manifests. A missing root yields an empty catalog.</summary>
    /// <param name="rootDirectory">The root to scan.</param>
    /// <returns>The catalog.</returns>
    /// <exception cref="InvalidDataException">Two manifests below the root share an id.</exception>
    public static ProbeKindCatalog Scan(string rootDirectory) =>
        new(pathsById: ScanPaths(
            description: Description,
            fileSuffix: ProbeKindManifest.FileSuffix,
            rootDirectory: rootDirectory
        ));

    /// <inheritdoc/>
    protected override ProbeKindManifest LoadManifest(string manifestPath) => ProbeKindManifest.Load(manifestPath: manifestPath);
}
