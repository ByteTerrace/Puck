using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Puck.Testing;

/// <summary>The repository's own answer to which assemblies declare the refusals <c>world.refusals</c> lists: the
/// <c>src/</c> projects in <c>Puck.World</c>'s project-reference closure, itself included, whose sources tag an enum member
/// with <c>[Refusal(…)]</c>. Both sides of the refusal census are held to it: the World's reflective scan
/// (<c>RefusalCatalog.AnchoredAssemblies</c>) and the CLI's source census (<c>RefusalCensus.Projects</c>).</summary>
internal static partial class RefusalDeclaringProjects {
    [GeneratedRegex(pattern: @"^\s*\[Refusal\(", options: RegexOptions.Multiline)]
    private static partial Regex RefusalTag();

    /// <summary>Lists the declaring projects by name, ordinally sorted.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <returns>The project names.</returns>
    public static IReadOnlyList<string> Of(string repositoryRoot) {
        var closure = new SortedSet<string>(comparer: StringComparer.Ordinal);
        var pending = new Stack<string>();

        pending.Push(item: Path.Combine(path1: repositoryRoot, path2: "src", path3: "Puck.World", path4: "Puck.World.csproj"));
        while (pending.TryPop(result: out var project)) {
            if (!closure.Add(item: Path.GetFileNameWithoutExtension(path: project))) {
                continue;
            }
            foreach (var reference in XDocument.Load(uri: project).Descendants(name: "ProjectReference")) {
                pending.Push(item: Path.GetFullPath(path: Path.Combine(path1: Path.GetDirectoryName(path: project)!, path2: reference.Attribute(name: "Include")!.Value)));
            }
        }

        return [.. closure.Where(predicate: name => Directory
            .EnumerateFiles(path: Path.Combine(path1: repositoryRoot, path2: "src", path3: name), searchPattern: "*.cs", searchOption: SearchOption.AllDirectories)
            .Where(predicate: static file => (!file.Replace(newChar: '/', oldChar: '\\').Contains(comparisonType: StringComparison.Ordinal, value: "/obj/") && !file.Replace(newChar: '/', oldChar: '\\').Contains(comparisonType: StringComparison.Ordinal, value: "/bin/")))
            .Any(predicate: static file => RefusalTag().IsMatch(input: File.ReadAllText(path: file))))];
    }
}
