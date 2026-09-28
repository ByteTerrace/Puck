using Puck.Shaders;

namespace Puck.Cli.Affected;

/// <summary>The shaders one tree builds, each read once and shared by document reach and the stand-in map: every stage
/// source the projects' shader items compile (<see cref="AffectedStandIns.Kernels"/>), and every post-process package
/// among them (<see cref="AffectedStandIns.PostPackages"/>). The packages are the catalog this build declares, since a
/// package is declared in C#, found among the tree's own stage sources.</summary>
internal sealed class AffectedShaders {
    private readonly Lazy<IReadOnlyList<AffectedKernel>> m_kernels;
    private readonly Lazy<IReadOnlyList<AffectedPostPackage>> m_packages;

    /// <summary>Initializes a new instance of the <see cref="AffectedShaders"/> class.</summary>
    /// <param name="tree">The tree the shaders are read from.</param>
    /// <param name="projects">Every project.</param>
    /// <param name="packages">The catalog whose post-process packages are found, or <see langword="null"/> for
    /// <see cref="RenderGraphPackageCatalog.Engine"/>.</param>
    public AffectedShaders(IAffectedTree tree, IReadOnlyList<AffectedProject> projects, RenderGraphPackageCatalog? packages = null) {
        m_kernels = new(valueFactory: () => AffectedStandIns.Kernels(projects: projects, tree: tree));
        m_packages = new(valueFactory: () => AffectedStandIns.PostPackages(kernels: m_kernels.Value, packages: (packages ?? RenderGraphPackageCatalog.Engine)));
    }

    /// <summary>Gets every stage source the tree's projects compile.</summary>
    public IReadOnlyList<AffectedKernel> Kernels => m_kernels.Value;
    /// <summary>Gets every post-process package whose fragment stage the tree builds.</summary>
    public IReadOnlyList<AffectedPostPackage> PostPackages => m_packages.Value;

    /// <summary>Returns the files of the post-process package an id names, as a world document's <c>views.post</c> row
    /// names one.</summary>
    /// <param name="id">The package id.</param>
    /// <returns>The files, repository-relative, or none when no package has the id.</returns>
    public IReadOnlyList<string> FilesOf(string id) => [.. PostPackages
        .Where(predicate: package => string.Equals(a: package.Id, b: id, comparisonType: StringComparison.Ordinal))
        .SelectMany(selector: static package => package.Files)
        .Distinct(comparer: StringComparer.Ordinal)];
}
