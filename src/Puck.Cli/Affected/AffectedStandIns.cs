using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.FileSystemGlobbing;
using Puck.Cli.Schema;
using Puck.Shaders;

namespace Puck.Cli.Affected;

/// <summary>One stage source the shader build compiles: its path and every file its include closure reaches.</summary>
/// <param name="Path">The stage source, repository-relative with forward slashes.</param>
/// <param name="Closure">The stage source and every include it reaches, repository-relative with forward slashes.</param>
/// <param name="Projects">Where the C# that names it may live: the directory of the project whose build declares it, and of
/// every project that build references, transitively, repository-relative.</param>
internal sealed record AffectedKernel(string Path, IReadOnlyList<string> Closure, IReadOnlyList<string> Projects);
/// <summary>One shader set: its manifest and every file the set is built from.</summary>
/// <param name="Manifest">The manifest, repository-relative with forward slashes.</param>
/// <param name="Files">The manifest, each stage source it names beside it with that source's include closure, and the
/// frame interface include generated for it, repository-relative with forward slashes.</param>
internal sealed record AffectedShaderSet(string Manifest, IReadOnlyList<string> Files);
/// <summary>
/// Maps a changed file the coverage index cannot know, because no canary executes it, to the indexed C# sources it
/// stands for, following an edge the build already states. A project's own build inputs (its project file, its restore
/// lock, the method list its source generator reads) stand for every indexed source of that project. A shader source or
/// include stands for the C# that loads each kernel whose include closure reaches it: the stage sources come from the
/// projects' shader items and their closures from <see cref="ShaderSourceClosure"/>, and a loader names its kernel by
/// a string literal in the kernel's project or any project its build references, such as a conversion pass named by a
/// constant. A shader-set manifest, the stage sources it names with their closures, and the frame interface generated
/// for it stand for the manifest's owner: the C# declaring <see cref="ShaderSetManifest"/>, the model it is read into. A
/// file <c>puck schema</c> writes stands for the files that declare the types it is generated from. A file none of these
/// edges reaches has no stand-in and stays unmapped. Every file is read through one
/// <see cref="IAffectedTree"/>, the working tree or the tree the base recorded, so a file deleted since the base stands
/// for what the base's own projects, shaders and index said.
/// </summary>
internal static partial class AffectedStandIns {
    private static readonly string[] ProjectInputNames = ["packages.lock.json", "NativeMethods.txt", "NativeMethods.json"];
    private static readonly string[] StageItems = ["VertexShaderSource", "FragmentShaderSource", "ComputeShaderSource", "Direct3D11KernelSource"];
    private static readonly string[] StageSuffixes = [".vert", ".frag", ".comp"];

    [GeneratedRegex(pattern: @"\b(?:class|struct|record|interface|enum)\s+(?:class\s+|struct\s+)?(?<name>[A-Za-z_][A-Za-z0-9_]*)\b")]
    private static partial Regex DeclarationPattern();
    private static bool IsUnder(string path, string directory) => path.StartsWith(
        comparisonType: StringComparison.Ordinal,
        value: (directory + "/")
    );

    /// <summary>Returns whether a file is one of a project's own build inputs rather than a source it compiles.</summary>
    /// <param name="path">The file, repository-relative with forward slashes.</param>
    /// <returns>Whether it is a project file, a restore lock, or a source generator's method list.</returns>
    internal static bool IsProjectInput(string path) => (
        path.EndsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: ".csproj") ||
        ProjectInputNames.Contains(value: Path.GetFileName(path: path), comparer: StringComparer.OrdinalIgnoreCase)
    );
    /// <summary>The indexed sources of a project, which its build inputs reach.</summary>
    /// <param name="directory">The project's directory.</param>
    /// <param name="indexed">Every indexed source.</param>
    /// <returns>The project's indexed sources, ordinal order.</returns>
    internal static IReadOnlyList<string> ProjectSources(string directory, IEnumerable<string> indexed) => [.. indexed
        .Where(predicate: path => IsUnder(directory: directory, path: path))
        .Order(comparer: StringComparer.Ordinal)];
    /// <summary>The names a loader may give a kernel: its file name without <c>.hlsl</c>, and that without its stage.</summary>
    /// <param name="kernelPath">The stage source's path.</param>
    /// <returns>The names, such as <c>sdf-beam.comp</c> and <c>sdf-beam</c>.</returns>
    internal static IReadOnlyList<string> KernelNames(string kernelPath) {
        var name = Path.GetFileNameWithoutExtension(path: kernelPath);
        var stage = StageSuffixes.FirstOrDefault(predicate: suffix => name.EndsWith(comparisonType: StringComparison.Ordinal, value: suffix));

        return ((stage is null)
            ? [name]
            : [name, name[..^stage.Length]]
        );
    }
    /// <summary>The C# that loads each kernel whose closure reaches a changed shader file: the indexed sources that name it
    /// by a string literal, in the kernel's own project or any project its build references, where a name the loader
    /// reads may be declared as a constant.</summary>
    /// <param name="path">The changed shader file.</param>
    /// <param name="kernels">Every stage source the build compiles.</param>
    /// <param name="indexed">Every indexed source.</param>
    /// <param name="read">Reads an indexed source's text.</param>
    /// <returns>The loaders, ordinal order.</returns>
    internal static IReadOnlyList<string> ShaderLoaders(string path, IEnumerable<AffectedKernel> kernels, IReadOnlyCollection<string> indexed, Func<string, string> read) {
        var loaders = new SortedSet<string>(comparer: StringComparer.Ordinal);

        foreach (var kernel in kernels.Where(predicate: kernel => kernel.Closure.Contains(value: path, comparer: StringComparer.Ordinal))) {
            var literals = KernelNames(kernelPath: kernel.Path).Select(selector: static name => $"\"{name}\"").ToArray();

            foreach (var source in indexed.Where(predicate: source => kernel.Projects.Any(predicate: project => IsUnder(directory: project, path: source)))) {
                var text = read(arg: source);

                if (literals.Any(predicate: literal => text.Contains(comparisonType: StringComparison.Ordinal, value: literal))) {
                    _ = loaders.Add(item: source);
                }
            }
        }

        return [.. loaders];
    }
    /// <summary>The indexed sources that declare any of the given types.</summary>
    /// <param name="types">The types.</param>
    /// <param name="indexed">Every indexed source.</param>
    /// <param name="read">Reads an indexed source's text.</param>
    /// <returns>The declaring sources, ordinal order.</returns>
    internal static IReadOnlyList<string> Declaring(IReadOnlyList<Type> types, IEnumerable<string> indexed, Func<string, string> read) {
        var names = types.Select(selector: static type => type.Name.Split(separator: '`')[0]).ToHashSet(comparer: StringComparer.Ordinal);

        if (names.Count == 0) {
            return [];
        }

        return [.. indexed
            .Where(predicate: source => source.EndsWith(comparisonType: StringComparison.Ordinal, value: ".cs"))
            .Where(predicate: source => DeclarationPattern().Matches(input: read(arg: source)).Any(predicate: match => names.Contains(item: match.Groups["name"].Value)))
            .Order(comparer: StringComparer.Ordinal)];
    }
    /// <summary>Reads every stage source a project's shader items declare, with its include closure, from one tree.</summary>
    /// <param name="tree">The tree the projects, stage sources and includes are read from.</param>
    /// <param name="projects">Every project.</param>
    /// <returns>The kernels. A stage source whose closure cannot be collected is left out.</returns>
    internal static IReadOnlyList<AffectedKernel> Kernels(IAffectedTree tree, IReadOnlyList<AffectedProject> projects) {
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
    /// <summary>Reads every shader set the projects ship, from one tree: each shader-set manifest under a project, read
    /// with its own reader (<see cref="ShaderSetManifest.ReadDeclaration"/>), with the stage sources it names beside it
    /// (<c>&lt;stage&gt;.hlsl</c>) and their include closures, and the frame interface include generated for it
    /// (<see cref="ShaderSetManifest.ReadFrameInterface"/>).</summary>
    /// <param name="tree">The tree the manifests are read from.</param>
    /// <param name="projects">Every project.</param>
    /// <param name="kernels">Every stage source the build compiles, whose closures a set's stage sources reach.</param>
    /// <returns>The sets. A manifest its reader refuses is a set of itself alone.</returns>
    internal static IReadOnlyList<AffectedShaderSet> ShaderSets(IAffectedTree tree, IReadOnlyList<AffectedProject> projects, IReadOnlyList<AffectedKernel> kernels) {
        var sets = new List<AffectedShaderSet>();

        foreach (var project in projects.Where(predicate: static project => !project.IsSuite)) {
            foreach (var manifest in tree.Files(directory: project.Directory).Where(predicate: static file => file.EndsWith(comparisonType: StringComparison.Ordinal, value: ShaderSetManifest.FileSuffix))) {
                var files = new SortedSet<string>(comparer: StringComparer.Ordinal) { manifest };
                var directory = manifest[..(manifest.LastIndexOf(value: '/') + 1)];
                var full = Path.Combine(path1: tree.Root, path2: manifest);
                var text = (tree.ReadText(path: manifest) ?? string.Empty);

                try {
                    var stages = ShaderSetManifest.ReadDeclaration(manifestPath: full, text: text).Stages;

                    foreach (var stage in new[] { stages.Vertex, stages.Fragment, stages.Compute }.OfType<string>()) {
                        var source = $"{directory}{stage}.hlsl";

                        files.UnionWith(other: (kernels.FirstOrDefault(predicate: kernel => (kernel.Path == source))?.Closure ?? [source]));
                    }

                    _ = files.Add(item: (directory + ShaderFrameInterface.IncludeFileName(interfaceName: ShaderSetManifest.ReadFrameInterface(manifestPath: full, text: text).Name)));
                } catch (InvalidDataException) {
                    // A manifest its reader refuses builds nothing but itself.
                }

                sets.Add(item: new AffectedShaderSet(
                    Files: [.. files],
                    Manifest: manifest
                ));
            }
        }

        return sets;
    }

    /// <summary>Creates the stand-in map for one tree of the repository: the working tree for a changed file, or the tree
    /// the base recorded for a file deleted since, whose stand-ins are what the base's index knows.</summary>
    /// <param name="tree">The tree every project, shader and indexed source is read from.</param>
    /// <param name="projects">Every project.</param>
    /// <param name="indexed">Every indexed source.</param>
    /// <returns>The indexed sources a changed file stands for, or none.</returns>
    public static Func<string, IReadOnlyList<string>> Create(IAffectedTree tree, IReadOnlyList<AffectedProject> projects, IReadOnlyCollection<string> indexed) {
        var owners = projects.OrderByDescending(keySelector: static project => project.Directory.Length).ToArray();
        var kernels = new Lazy<IReadOnlyList<AffectedKernel>>(valueFactory: () => Kernels(projects: projects, tree: tree));
        var texts = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        string Read(string source) {
            if (!texts.TryGetValue(key: source, value: out var text)) {
                text = (tree.ReadText(path: source) ?? string.Empty);
                texts[source] = text;
            }

            return text;
        }

        var sets = new Lazy<IReadOnlyList<AffectedShaderSet>>(valueFactory: () => ShaderSets(kernels: kernels.Value, projects: projects, tree: tree));
        // A shader set's owner: the C# that declares the model its manifest is read into.
        var setOwners = new Lazy<IReadOnlyList<string>>(valueFactory: () => Declaring(indexed: indexed, read: Read, types: [typeof(ShaderSetManifest)]));

        return path => {
            if (IsProjectInput(path: path)) {
                return ((owners.FirstOrDefault(predicate: project => IsUnder(directory: project.Directory, path: path)) is { } owner)
                    ? ProjectSources(directory: owner.Directory, indexed: indexed)
                    : []);
            }

            var isShader = (
                path.EndsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: ".hlsl") ||
                path.EndsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: ".hlsli")
            );

            if (
                isShader ||
                path.EndsWith(comparisonType: StringComparison.Ordinal, value: ShaderSetManifest.FileSuffix)
            ) {
                var standIns = new SortedSet<string>(comparer: StringComparer.Ordinal);

                if (isShader) {
                    standIns.UnionWith(other: ShaderLoaders(
                        indexed: indexed,
                        kernels: kernels.Value,
                        path: path,
                        read: Read
                    ));
                }
                if (sets.Value.Any(predicate: set => set.Files.Contains(value: path, comparer: StringComparer.Ordinal))) {
                    standIns.UnionWith(other: setOwners.Value);
                }

                return [.. standIns];
            }

            return Declaring(
                indexed: indexed,
                read: Read,
                types: SchemaCommand.SourceTypesOf(relativePath: path)
            );
        };
    }

    private static string Relative(string path, string repositoryRoot) => Path.GetRelativePath(
        path: Path.GetFullPath(path: path),
        relativeTo: repositoryRoot
    ).Replace(newChar: '/', oldChar: '\\');
}
