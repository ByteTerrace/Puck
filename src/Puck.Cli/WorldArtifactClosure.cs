using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Puck.Cli;

/// <summary>
/// The paths a Release build of <c>src/Puck.World</c> reads from the checkout, found by reading the project files
/// themselves rather than by asking MSBuild, so that computing them costs no evaluation.
/// <para>
/// The walk starts at <c>src/Puck.World/Puck.World.csproj</c> and follows every <c>ProjectReference</c>, including
/// the ones that carry no assembly (the CLI that compiles the shipped worlds, the analyzers). For each project it
/// reads the project file, the nearest <c>Directory.Build.props</c> and <c>Directory.Build.targets</c> above it, and
/// every file those import from inside the checkout. A project contributes its whole directory. Any other path a
/// walked file names through <c>$(MSBuildThisFileDirectory)</c> or <c>$(MSBuildProjectDirectory)</c>, or through a
/// plain relative item <c>Include</c>, contributes itself: the linked analyzer ledgers, the mimalloc binaries under
/// <c>lib/</c>, the package icon under <c>branding/</c>. Every file directly in the repository root is added as well,
/// because the compiler also reads files no project names, such as <c>.editorconfig</c>.
/// </para>
/// <para>
/// Conditions are ignored, so the set can only be wider than what one build reads. A path spelled through any
/// other property is not followed; <c>WorldArtifactClosureLawTests</c> evaluates the World's whole project graph with
/// MSBuild and fails when an input falls outside these roots, which is how a new kind of reference gets noticed.
/// </para>
/// </summary>
internal static partial class WorldArtifactClosure {
    /// <summary>The project whose build the closure describes, relative to the repository root.</summary>
    public const string WorldProject = "src/Puck.World/Puck.World.csproj";

    // A path rooted at one of the two directory properties whose value the walk knows without evaluating anything:
    // everything up to the first delimiter, wildcard, or further property, item, or metadata reference.
    [GeneratedRegex(pattern: @"\$\((?<property>MSBuildThisFileDirectory|MSBuildProjectDirectory)\)(?<path>[^;""'\s$@%*?]*)")]
    private static partial Regex AnchoredPath();
    private static string? Canonical(string repositoryRoot, string fullPath) {
        // A project file may spell a path in a different case from the one git stores, and git compares paths by
        // exact case even where the file system does not, so each segment takes the spelling the directory holds.
        var relative = Path.GetRelativePath(
            path: fullPath,
            relativeTo: repositoryRoot
        );

        if (
            relative.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: ".."
        ) ||
            Path.IsPathRooted(path: relative)
        ) {
            return null;
        }
        if (relative == ".") {
            return string.Empty;
        }

        var current = repositoryRoot;
        var segments = new List<string>();

        foreach (var segment in relative.Split(options: StringSplitOptions.RemoveEmptyEntries, separator: [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar])) {
            string? actual = null;

            try {
                actual = Directory.EnumerateFileSystemEntries(
                    path: current,
                    searchPattern: segment
                ).Select(selector: Path.GetFileName).FirstOrDefault(predicate: name => string.Equals(
                    a: name,
                    b: segment,
                    comparisonType: StringComparison.OrdinalIgnoreCase
                ));
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                // An unreadable directory keeps the spelling as written.
            }

            actual ??= segment;
            segments.Add(item: actual);
            current = Path.Combine(
                path1: current,
                path2: actual
            );
        }

        return string.Join(
            separator: '/',
            values: segments
        );
    }
    private static string? Nearest(string repositoryRoot, string projectDirectory, string fileName) {
        for (var directory = projectDirectory; ; directory = Path.GetDirectoryName(path: directory)!) {
            var candidate = Path.Combine(
                path1: directory,
                path2: fileName
            );

            if (File.Exists(path: candidate)) {
                return candidate;
            }
            if (
                string.Equals(
                a: Path.TrimEndingDirectorySeparator(path: directory),
                b: Path.TrimEndingDirectorySeparator(path: repositoryRoot),
                comparisonType: StringComparison.OrdinalIgnoreCase
            ) ||
                (Path.GetDirectoryName(path: directory) is null)
            ) {
                return null;
            }
        }
    }
    // The file-system path one token names: the anchored form resolves against the file or the project directory, a
    // plain relative Include against the project directory. A token naming anything the walk cannot know is null.
    private static IEnumerable<string> Resolve(string text, string fileDirectory, string projectDirectory, bool plainIsProjectRelative) {
        var anchored = false;

        foreach (Match match in AnchoredPath().Matches(input: text)) {
            anchored = true;

            var path = match.Groups["path"].Value.TrimStart('\\', '/');

            yield return Path.GetFullPath(path: Path.Combine(
                path1: ((match.Groups["property"].Value == "MSBuildThisFileDirectory")
                    ? fileDirectory
                    : projectDirectory),
                path2: path
            ));
        }

        if (
            anchored ||
            !plainIsProjectRelative
        ) {
            yield break;
        }

        foreach (var token in text.Split(
            options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries,
            separator: ';'
        )) {
            if (token.IndexOfAny(anyOf: ['$', '@', '%']) >= 0) {
                continue;
            }

            var wildcard = token.IndexOfAny(anyOf: ['*', '?']);
            var literal = ((wildcard < 0)
                ? token
                : token[..wildcard]);

            if (literal.Length == 0) {
                continue;
            }

            yield return Path.GetFullPath(path: Path.Combine(
                path1: projectDirectory,
                path2: literal
            ));
        }
    }

    /// <summary>Walks the World's project graph and returns every path its build reads.</summary>
    /// <param name="repositoryRoot">The checkout the walk reads.</param>
    /// <returns>The roots, as repository-relative paths with forward slashes, sorted ordinally, none inside
    /// another; and the project files the walk reached.</returns>
    public static (IReadOnlyList<string> Roots, IReadOnlyList<string> Projects) Walk(string repositoryRoot) {
        repositoryRoot = Path.GetFullPath(path: repositoryRoot);

        var found = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);
        var projects = new SortedSet<string>(comparer: StringComparer.Ordinal);
        var visitedProjects = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>();

        pending.Enqueue(item: Path.GetFullPath(path: Path.Combine(
            path1: repositoryRoot,
            path2: WorldProject
        )));

        while (pending.TryDequeue(result: out var project)) {
            if (
                !visitedProjects.Add(item: project) ||
                !File.Exists(path: project)
            ) {
                continue;
            }

            var projectDirectory = Path.GetDirectoryName(path: project)!;

            if (Canonical(
                fullPath: project,
                repositoryRoot: repositoryRoot
            ) is { } projectRelative) {
                _ = projects.Add(item: projectRelative);
            }

            _ = found.Add(item: projectDirectory);

            var files = new Queue<string>();
            var visitedFiles = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);

            foreach (var automatic in ((string?[])[
                Nearest(
                    fileName: "Directory.Build.props",
                    projectDirectory: projectDirectory,
                    repositoryRoot: repositoryRoot
                ),
                project,
                Nearest(
                    fileName: "Directory.Build.targets",
                    projectDirectory: projectDirectory,
                    repositoryRoot: repositoryRoot
                ),
            ])) {
                if (automatic is not null) {
                    files.Enqueue(item: automatic);
                }
            }

            while (files.TryDequeue(result: out var file)) {
                if (!visitedFiles.Add(item: file)) {
                    continue;
                }

                _ = found.Add(item: file);

                XDocument document;

                try {
                    document = XDocument.Load(uri: file);
                } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)) {
                    continue;
                }

                var fileDirectory = Path.GetDirectoryName(path: file)!;

                foreach (var element in document.Descendants()) {
                    foreach (var attribute in element.Attributes()) {
                        var isImport = ((element.Name.LocalName == "Import") && (attribute.Name.LocalName == "Project"));
                        var isInclude = (attribute.Name.LocalName == "Include");

                        foreach (var path in Resolve(
                            fileDirectory: fileDirectory,
                            plainIsProjectRelative: isInclude,
                            projectDirectory: projectDirectory,
                            text: ((isImport && !attribute.Value.Contains(comparisonType: StringComparison.Ordinal, value: '$'))
                                ? $"$(MSBuildThisFileDirectory){attribute.Value}"
                                : attribute.Value)
                        )) {
                            if (
                                (element.Name.LocalName == "ProjectReference") &&
                                isInclude
                            ) {
                                pending.Enqueue(item: path);
                            } else if (isImport) {
                                files.Enqueue(item: path);
                            } else {
                                _ = found.Add(item: path);
                            }
                        }
                    }
                    foreach (var text in element.Nodes().OfType<XText>()) {
                        foreach (var path in Resolve(
                            fileDirectory: fileDirectory,
                            plainIsProjectRelative: false,
                            projectDirectory: projectDirectory,
                            text: text.Value
                        )) {
                            _ = found.Add(item: path);
                        }
                    }
                }
            }
        }

        foreach (var file in Directory.EnumerateFiles(path: repositoryRoot)) {
            _ = found.Add(item: file);
        }

        var roots = new SortedSet<string>(comparer: StringComparer.Ordinal);

        foreach (var path in found) {
            if (
                (File.Exists(path: path) || Directory.Exists(path: path)) &&
                (Canonical(
                    fullPath: Path.TrimEndingDirectorySeparator(path: path),
                    repositoryRoot: repositoryRoot
                ) is { Length: > 0 } relative)
            ) {
                _ = roots.Add(item: relative);
            }
        }

        // A path inside a directory already listed adds nothing a git query over the directory does not already cover.
        var minimal = new List<string>(capacity: roots.Count);

        foreach (var root in roots) {
            if (!minimal.Any(predicate: kept => root.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: $"{kept}/"
            ))) {
                minimal.Add(item: root);
            }
        }

        return (minimal, [.. projects]);
    }
}
