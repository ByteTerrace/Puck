namespace Puck.Cli.Affected;

/// <summary>The shaders one tree builds, each read once and shared by document reach and the stand-in map: every stage
/// source the projects' shader items compile (<see cref="AffectedStandIns.Kernels"/>), and every shader set
/// (<see cref="AffectedStandIns.ShaderSets"/>).</summary>
internal sealed class AffectedShaders {
    private readonly Lazy<IReadOnlyList<AffectedKernel>> m_kernels;
    private readonly Lazy<IReadOnlyList<AffectedShaderSet>> m_sets;

    /// <summary>Initializes a new instance of the <see cref="AffectedShaders"/> class.</summary>
    /// <param name="tree">The tree the shaders are read from.</param>
    /// <param name="projects">Every project.</param>
    public AffectedShaders(IAffectedTree tree, IReadOnlyList<AffectedProject> projects) {
        m_kernels = new(valueFactory: () => AffectedStandIns.Kernels(projects: projects, tree: tree));
        m_sets = new(valueFactory: () => AffectedStandIns.ShaderSets(kernels: m_kernels.Value, projects: projects, tree: tree));
    }

    /// <summary>Gets every stage source the tree's projects compile.</summary>
    public IReadOnlyList<AffectedKernel> Kernels => m_kernels.Value;
    /// <summary>Gets every shader set the tree's projects ship.</summary>
    public IReadOnlyList<AffectedShaderSet> Sets => m_sets.Value;

    /// <summary>Returns the files of the shader sets an id names, as a world document's <c>render.extensions</c> entry
    /// names one.</summary>
    /// <param name="id">The set id.</param>
    /// <returns>The files, repository-relative, or none when no set has the id.</returns>
    public IReadOnlyList<string> FilesOf(string id) => [.. Sets
        .Where(predicate: set => string.Equals(a: set.Id, b: id, comparisonType: StringComparison.Ordinal))
        .SelectMany(selector: static set => set.Files)
        .Distinct(comparer: StringComparer.Ordinal)];
}
