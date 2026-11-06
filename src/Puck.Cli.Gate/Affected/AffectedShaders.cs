using System.Xml.Linq;
using Microsoft.Extensions.FileSystemGlobbing;
using Puck.Shaders;

namespace Puck.Cli.Affected;

/// <summary>One stage source the shader build compiles: its path and every file its include closure reaches.</summary>
/// <param name="Path">The stage source, repository-relative with forward slashes.</param>
/// <param name="Closure">The stage source and every include it reaches, repository-relative with forward slashes.</param>
/// <param name="Projects">Where the C# that names it may live: the directory of the project whose build declares it, and of
/// every project that build references, transitively, repository-relative.</param>
public sealed record AffectedKernel(string Path, IReadOnlyList<string> Closure, IReadOnlyList<string> Projects);
/// <summary>One post-process package: its id, its fragment stage source and every file it is built from.</summary>
/// <param name="Id">The package's id, which a world document's <c>views.post</c> row names.</param>
/// <param name="Source">The fragment stage source, the one file only the package builds, repository-relative with forward
/// slashes.</param>
/// <param name="Files">Each stage source the package names with that source's include closure, and the frame interface
/// include generated for it beside its fragment stage, repository-relative with forward slashes.</param>
public sealed record AffectedPostPackage(string Id, string Source, IReadOnlyList<string> Files);

/// <summary>The shaders one tree builds, read once for document reach: every stage source the projects' shader items
/// compile with its include closure (<see cref="ShaderSourceClosure"/>), and every post-process package among them. The
/// packages are the catalog this build declares, since a package is declared in C#, found among the tree's own stage
/// sources.</summary>
public sealed class AffectedShaders {
    private static readonly string[] StageItems = ["VertexShaderSource", "FragmentShaderSource", "ComputeShaderSource", "Direct3D11KernelSource"];

    private readonly Lazy<IReadOnlyList<AffectedKernel>> m_kernels;
    private readonly Lazy<IReadOnlyList<AffectedPostPackage>> m_packages;

    /// <summary>Initializes a new instance of the <see cref="AffectedShaders"/> class.</summary>
    /// <param name="tree">The tree the shaders are read from.</param>
    /// <param name="projects">Every project.</param>
    /// <param name="packages">The catalog whose post-process packages are found, or <see langword="null"/> for
    /// <see cref="RenderGraphPackageCatalog.Engine"/>.</param>
    public AffectedShaders(IAffectedTree tree, IReadOnlyList<AffectedProject> projects, RenderGraphPackageCatalog? packages = null) {
        m_kernels = new(valueFactory: () => ReadKernels(projects: projects, tree: tree));
        m_packages = new(valueFactory: () => FindPostPackages(kernels: m_kernels.Value, packages: (packages ?? RenderGraphPackageCatalog.Engine)));
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

    // Reads every stage source a project's shader items declare, with its include closure, from one tree. A stage source
    // whose closure cannot be collected is left out.
    internal static IReadOnlyList<AffectedKernel> ReadKernels(IAffectedTree tree, IReadOnlyList<AffectedProject> projects) {
        var kernels = new List<AffectedKernel>();
        var byName = projects.ToDictionary(comparer: StringComparer.OrdinalIgnoreCase, keySelector: static project => project.Name);

        // The project's directory and the directory of every project its build references, transitively.
        IReadOnlyList<string> ProjectsOf(AffectedProject project) {
            var reached = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<AffectedProject>(collection: [project]);

            while (pending.TryPop(result: out var next)) {
                if (!reached.Add(item: next.Name)) {
                    continue;
                }

                foreach (var reference in next.References) {
                    if (byName.TryGetValue(key: reference, value: out var referenced)) {
                        pending.Push(item: referenced);
                    }
                }
            }

            return [.. reached.Select(selector: name => byName[name].Directory).Order(comparer: StringComparer.Ordinal)];
        }

        string? ReadFull(string path) => tree.ReadText(path: Relative(path: path, repositoryRoot: tree.Root));

        foreach (var project in projects.Where(predicate: static project => !project.IsSuite)) {
            if (tree.ReadText(path: $"{project.Directory}/{project.Name}.csproj") is not { } projectText) {
                continue;
            }

            var matcher = new Matcher(comparisonType: StringComparison.OrdinalIgnoreCase);
            var patterns = XDocument.Parse(text: projectText).Descendants()
                .Where(predicate: static element => StageItems.Contains(value: element.Name.LocalName, comparer: StringComparer.Ordinal))
                .Select(selector: static element => ((string?)element.Attribute(name: "Include")))
                .OfType<string>()
                .SelectMany(selector: static include => include.Split(options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries, separator: ';'))
                .ToArray();

            if (patterns.Length == 0) {
                continue;
            }

            matcher.AddIncludePatterns(patterns.Select(selector: static pattern => pattern.Replace(newChar: '/', oldChar: '\\')));

            var directory = Path.Combine(path1: tree.Root, path2: project.Directory);
            var matched = matcher.Match(
                files: tree.Files(directory: project.Directory).Select(selector: file => Path.Combine(path1: tree.Root, path2: file)),
                rootDir: directory
            );

            foreach (var file in matched.Files.Select(selector: match => Path.GetFullPath(path: Path.Combine(path1: directory, path2: match.Path))).Order(comparer: StringComparer.Ordinal)) {
                try {
                    var closure = ShaderSourceClosure.Collect(
                        limits: ShaderSourceLimits.Default,
                        readInclude: ReadFull,
                        sources: [(file, (ReadFull(path: file) ?? string.Empty))]
                    );

                    kernels.Add(item: new AffectedKernel(
                        Closure: [.. closure.Sources.Concat(second: closure.Includes).Select(selector: dependency => Relative(path: dependency.Path, repositoryRoot: tree.Root))],
                        Path: Relative(path: file, repositoryRoot: tree.Root),
                        Projects: ProjectsOf(project: project)
                    ));
                } catch (ShaderClosureRefusedException) {
                    // A closure the build itself would refuse reaches nothing a canary could observe.
                }
            }
        }

        return kernels;
    }
    // Finds every post-process package a catalog declares among the stage sources a tree builds: each stage
    // (RenderGraphPackage.Stages) is the kernel whose path ends with its deployed directory and stem, with its include
    // closure, and the frame interface include generated for the package (ShaderPipelineParameterLayout.ForPackage) lies
    // beside its fragment stage.
    internal static IReadOnlyList<AffectedPostPackage> FindPostPackages(RenderGraphPackageCatalog packages, IReadOnlyList<AffectedKernel> kernels) {
        var found = new List<AffectedPostPackage>();

        foreach (var package in packages.Packages.Where(predicate: static package => package.IsPostProcess)) {
            var stages = package.Stages!;

            AffectedKernel? KernelOf(string stem) => kernels.FirstOrDefault(predicate: kernel => kernel.Path.EndsWith(
                comparisonType: StringComparison.Ordinal,
                value: $"/{stages.Directory}/{stem}.hlsl"
            ));

            if (KernelOf(stem: stages.Fragment) is not { } fragment) {
                continue;
            }

            var files = new SortedSet<string>(comparer: StringComparer.Ordinal);

            files.UnionWith(other: fragment.Closure);
            files.UnionWith(other: (KernelOf(stem: stages.Vertex)?.Closure ?? []));
            _ = files.Add(item: (fragment.Path[..(fragment.Path.LastIndexOf(value: '/') + 1)] + ShaderFrameInterface.IncludeFileName(interfaceName: ShaderPipelineParameterLayout.ForPackage(
                config: package.Config,
                members: package.Members,
                package: package.Id
            ).Interface.Name)));
            found.Add(item: new AffectedPostPackage(
                Files: [.. files],
                Id: package.Id,
                Source: fragment.Path
            ));
        }

        return found;
    }

    private static string Relative(string path, string repositoryRoot) => Path.GetRelativePath(
        path: Path.GetFullPath(path: path),
        relativeTo: repositoryRoot
    ).Replace(newChar: '/', oldChar: '\\');
}
