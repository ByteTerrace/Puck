using System.Text.RegularExpressions;
using Microsoft.Extensions.FileSystemGlobbing;

namespace Puck.Cli.Affected;

/// <summary>
/// The projects a changed file reaches besides the one that owns it. A project links a file into its own build when
/// one of its project file's items includes it, the include resolved from the project's directory, a glob matched
/// below its last literal directory: a linked source under <c>tests/Shared</c>, or content copied from another
/// project's directory. A file the root <c>Directory.Build.targets</c> links into every test project reaches every
/// suite. A file no project owns also reaches the projects whose own files name its directory, spelled by its first two
/// segments (such as <c>tests/Puck.World.Verdicts</c> or <c>worlds/parlor</c>), or by its top-level name for a file one
/// level down.
/// </summary>
internal static partial class AffectedConsumers {
    private const string RootTargets = "Directory.Build.targets";
    private const string ThisFileDirectory = "$(MSBuildThisFileDirectory)";

    // An item's Include attribute: one path or glob, never a list.
    [GeneratedRegex(pattern: "Include=\"(?<path>[^\";]+)\"")]
    private static partial Regex IncludePattern();
    private static IEnumerable<string> Includes(string projectFile) => IncludePattern().Matches(input: File.ReadAllText(path: projectFile))
        .Select(selector: static match => match.Groups["path"].Value.Replace(newChar: '/', oldChar: '\\'));
    private static string Relative(string repositoryRoot, string path) => Path.GetRelativePath(
        path: Path.GetFullPath(path: path),
        relativeTo: repositoryRoot
    ).Replace(newChar: '/', oldChar: '\\');
    // Whether an include, resolved from its project's directory, names the repository-relative path: the literal
    // directories before its first glob segment are resolved, and the rest matched as a glob below them.
    private static bool Names(string repositoryRoot, string projectDirectory, string include, string path) {
        var segments = include.Split(separator: '/');
        var literal = segments.TakeWhile(predicate: static segment => !segment.Contains(value: '*')).Count();

        if (literal == segments.Length) {
            return string.Equals(
                a: Relative(path: Path.Combine(path1: repositoryRoot, path2: projectDirectory, path3: include), repositoryRoot: repositoryRoot),
                b: path,
                comparisonType: StringComparison.Ordinal
            );
        }

        var directory = Relative(
            path: Path.Combine(path1: repositoryRoot, path2: projectDirectory, path3: string.Join(separator: '/', values: segments.Take(count: literal))),
            repositoryRoot: repositoryRoot
        );

        if (!path.StartsWith(comparisonType: StringComparison.Ordinal, value: (directory + "/"))) {
            return false;
        }

        var matcher = new Matcher(comparisonType: StringComparison.Ordinal);

        _ = matcher.AddInclude(pattern: string.Join(separator: '/', values: segments.Skip(count: literal)));

        return matcher.Match(file: path[(directory.Length + 1)..]).HasMatches;
    }
    // Whether the root build targets link the file into every test project.
    private static bool LinkedIntoEverySuite(string repositoryRoot, string path) {
        var targets = Path.Combine(path1: repositoryRoot, path2: RootTargets);

        return (File.Exists(path: targets) && Includes(projectFile: targets).Any(predicate: include => (include.StartsWith(comparisonType: StringComparison.Ordinal, value: ThisFileDirectory) && string.Equals(
            a: include[ThisFileDirectory.Length..],
            b: path,
            comparisonType: StringComparison.Ordinal
        ))));
    }

    /// <summary>Creates the search for the projects that link a file into their own build; each answer is cached.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <param name="projects">Every project in the graph.</param>
    /// <returns>The projects whose project files include a changed file, by name, the file's owner among them when it
    /// includes the file itself.</returns>
    public static Func<string, IReadOnlyList<string>> Linking(string repositoryRoot, IReadOnlyList<AffectedProject> projects) {
        var root = Path.GetFullPath(path: repositoryRoot);
        var includes = new Lazy<(AffectedProject Project, string[] Includes)[]>(valueFactory: () => [.. projects.Select(selector: project => (project, ((string[])[.. Directory.EnumerateFiles(
            path: Path.Combine(path1: root, path2: project.Directory),
            searchOption: SearchOption.TopDirectoryOnly,
            searchPattern: "*.csproj"
        ).SelectMany(selector: Includes)])))]);
        var cache = new Dictionary<string, IReadOnlyList<string>>(comparer: StringComparer.Ordinal);

        return path => {
            if (cache.TryGetValue(key: path, value: out var known)) {
                return known;
            }

            IReadOnlyList<string> consumers = [.. (LinkedIntoEverySuite(path: path, repositoryRoot: root)
                ? projects.Where(predicate: static project => project.IsSuite)
                : includes.Value.Where(predicate: entry => entry.Includes.Any(predicate: include => Names(
                    include: include,
                    path: path,
                    projectDirectory: entry.Project.Directory,
                    repositoryRoot: root
                ))).Select(selector: static entry => entry.Project)
            ).Select(selector: static project => project.Name)];

            cache[path] = consumers;

            return consumers;
        };
    }
    /// <summary>Creates the search for the projects a file no project owns reaches: those that link it, and those whose
    /// own files name its directory; each answer is cached by the key it searched.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <param name="projects">Every project in the graph.</param>
    /// <returns>The projects a changed file no project owns reaches, by name.</returns>
    public static Func<string, IReadOnlyList<string>> Search(string repositoryRoot, IReadOnlyList<AffectedProject> projects) {
        var root = Path.GetFullPath(path: repositoryRoot);
        var cache = new Dictionary<string, IReadOnlyList<string>>(comparer: StringComparer.Ordinal);
        var linking = Linking(projects: projects, repositoryRoot: root);

        IReadOnlyList<string> Named(string path) {
            var segments = path.Split(separator: '/');
            var key = ((segments.Length > 2)
                ? $"{segments[0]}/{segments[1]}"
                : segments[0]
            );

            if (cache.TryGetValue(key: key, value: out var known)) {
                return known;
            }

            var consumers = projects.Where(predicate: project => Directory.EnumerateFiles(
                path: Path.Combine(path1: root, path2: project.Directory),
                searchOption: SearchOption.AllDirectories,
                searchPattern: "*"
            ).Where(predicate: static file => ((file.EndsWith(comparisonType: StringComparison.Ordinal, value: ".cs") ||
                file.EndsWith(comparisonType: StringComparison.Ordinal, value: ".csproj") ||
                file.EndsWith(comparisonType: StringComparison.Ordinal, value: ".targets")) &&
                !file.Contains(comparisonType: StringComparison.Ordinal, value: $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                !file.Contains(comparisonType: StringComparison.Ordinal, value: $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            ).Any(predicate: file => File.ReadAllText(path: file).Contains(comparisonType: StringComparison.Ordinal, value: key))).Select(selector: static project => project.Name).ToArray();

            cache[key] = consumers;

            return consumers;
        }

        return path => [.. Named(path: path).Union(second: linking(arg: path), comparer: StringComparer.Ordinal)];
    }
}
