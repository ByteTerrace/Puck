using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.FileSystemGlobbing;
using Puck.Cli.Schema;
using Puck.Shaders;

namespace Puck.Cli.Affected;

/// <summary>One stage source the shader build compiles: its path and every file its include closure reaches.</summary>
/// <param name="Path">The stage source, repository-relative with forward slashes.</param>
/// <param name="Closure">The stage source and every include it reaches, repository-relative with forward slashes.</param>
/// <param name="Project">The directory of the project whose build declares it, repository-relative.</param>
internal sealed record AffectedKernel(string Path, IReadOnlyList<string> Closure, string Project);
/// <summary>
/// Maps a changed file the coverage index cannot know, because no canary executes it, to the indexed C# sources it
/// stands for, following an edge the build already states. A project's own build inputs (its project file, its restore
/// lock, the method list its source generator reads) stand for every indexed source of that project. A shader source or
/// include stands for the C# that loads each kernel whose include closure reaches it: the stage sources come from the
/// projects' shader items and their closures from <see cref="ShaderSourceClosure"/>, and a loader names its kernel by
/// a string literal. A file <c>puck schema</c> writes stands for the files that declare the types it is generated from.
/// A file none of these edges reaches has no stand-in and stays unmapped.
/// </summary>
internal static partial class AffectedStandIns {
    private static readonly string[] ProjectInputNames = ["packages.lock.json", "NativeMethods.txt", "NativeMethods.json"];
    private static readonly string[] StageItems = ["VertexShaderSource", "FragmentShaderSource", "ComputeShaderSource"];
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
    /// <summary>The C# that loads each kernel whose closure reaches a changed shader file: the indexed sources of the
    /// kernel's own project that name it by a string literal.</summary>
    /// <param name="path">The changed shader file.</param>
    /// <param name="kernels">Every stage source the build compiles.</param>
    /// <param name="indexed">Every indexed source.</param>
    /// <param name="read">Reads an indexed source's text.</param>
    /// <returns>The loaders, ordinal order.</returns>
    internal static IReadOnlyList<string> ShaderLoaders(string path, IEnumerable<AffectedKernel> kernels, IReadOnlyCollection<string> indexed, Func<string, string> read) {
        var loaders = new SortedSet<string>(comparer: StringComparer.Ordinal);

        foreach (var kernel in kernels.Where(predicate: kernel => kernel.Closure.Contains(value: path, comparer: StringComparer.Ordinal))) {
            var literals = KernelNames(kernelPath: kernel.Path).Select(selector: static name => $"\"{name}\"").ToArray();

            foreach (var source in indexed.Where(predicate: source => IsUnder(directory: kernel.Project, path: source))) {
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
    /// <summary>Reads every stage source a project's shader items declare, with its include closure.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <param name="projects">Every project.</param>
    /// <returns>The kernels. A stage source whose closure cannot be collected is left out.</returns>
    internal static IReadOnlyList<AffectedKernel> Kernels(string repositoryRoot, IReadOnlyList<AffectedProject> projects) {
        var kernels = new List<AffectedKernel>();

        foreach (var project in projects.Where(predicate: static project => !project.IsSuite)) {
            var directory = Path.Combine(path1: repositoryRoot, path2: project.Directory);
            var projectFile = Path.Combine(path1: directory, path2: $"{project.Name}.csproj");

            if (!File.Exists(path: projectFile)) {
                continue;
            }

            var matcher = new Matcher(comparisonType: StringComparison.OrdinalIgnoreCase);
            var patterns = XDocument.Load(uri: projectFile).Descendants()
                .Where(predicate: static element => StageItems.Contains(value: element.Name.LocalName, comparer: StringComparer.Ordinal))
                .Select(selector: static element => ((string?)element.Attribute(name: "Include")))
                .OfType<string>()
                .SelectMany(selector: static include => include.Split(options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries, separator: ';'))
                .ToArray();

            if (patterns.Length == 0) {
                continue;
            }

            matcher.AddIncludePatterns(patterns.Select(selector: static pattern => pattern.Replace(newChar: '/', oldChar: '\\')));

            foreach (var file in matcher.GetResultsInFullPath(directoryPath: directory).Order(comparer: StringComparer.Ordinal)) {
                try {
                    var closure = ShaderSourceClosure.Collect(
                        limits: ShaderSourceLimits.Default,
                        sources: [(file, File.ReadAllText(path: file))]
                    );

                    kernels.Add(item: new AffectedKernel(
                        Closure: [.. closure.Sources.Concat(second: closure.Includes).Select(selector: dependency => Relative(path: dependency.Path, repositoryRoot: repositoryRoot))],
                        Path: Relative(path: file, repositoryRoot: repositoryRoot),
                        Project: project.Directory
                    ));
                } catch (ShaderClosureRefusedException) {
                    // A closure the build itself would refuse reaches nothing a canary could observe.
                }
            }
        }

        return kernels;
    }

    /// <summary>Creates the stand-in map for the repository.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <param name="projects">Every project.</param>
    /// <param name="indexed">Every indexed source.</param>
    /// <returns>The indexed sources a changed file stands for, or none.</returns>
    public static Func<string, IReadOnlyList<string>> Create(string repositoryRoot, IReadOnlyList<AffectedProject> projects, IReadOnlyCollection<string> indexed) {
        var owners = projects.OrderByDescending(keySelector: static project => project.Directory.Length).ToArray();
        var kernels = new Lazy<IReadOnlyList<AffectedKernel>>(valueFactory: () => Kernels(projects: projects, repositoryRoot: repositoryRoot));
        var texts = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        string Read(string source) {
            if (!texts.TryGetValue(key: source, value: out var text)) {
                var path = Path.Combine(path1: repositoryRoot, path2: source);

                text = (File.Exists(path: path)
                    ? File.ReadAllText(path: path)
                    : string.Empty);
                texts[source] = text;
            }

            return text;
        }

        return path => {
            if (IsProjectInput(path: path)) {
                return ((owners.FirstOrDefault(predicate: project => IsUnder(directory: project.Directory, path: path)) is { } owner)
                    ? ProjectSources(directory: owner.Directory, indexed: indexed)
                    : []);
            }
            if (
                path.EndsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: ".hlsl") ||
                path.EndsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: ".hlsli")
            ) {
                return ShaderLoaders(
                    indexed: indexed,
                    kernels: kernels.Value,
                    path: path,
                    read: Read
                );
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
