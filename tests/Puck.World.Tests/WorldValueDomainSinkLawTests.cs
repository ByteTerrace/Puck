using System.Text.RegularExpressions;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: every site that resolves a bound presentation value hands the guard that holds its last valid
/// value and reports its transitions (<c>WorldValueDomainGuard</c>) to the resolver it builds or calls. A site that
/// omits it maps the value without either, so a binding that leaves its domain is neither held nor heard of.
/// </summary>
public sealed partial class WorldValueDomainSinkLawTests {
    // The calls that take a guard, each recognized by an argument no other overload of its name carries.
    [GeneratedRegex(@"(?<name>new WorldSessionSceneEmitter|new WorldRoutedScene|new WorldEnvironmentResolve|new WorldThemeResolve|WorldMarkerAlphas\.Resolve|WorldCameraRigCompiler\.Compile|ResolveChase|\.Resolve|ResolveCamera|\bCompile)\(", RegexOptions.CultureInvariant)]
    private static partial Regex Call();
    // The argument text of the call whose opening parenthesis ends at `open`, to its matching close.
    private static string Arguments(string text, int open) {
        var depth = 1;
        var index = open;

        while ((index < text.Length) && (depth > 0)) {
            depth += text[index] switch {
                '(' => 1,
                ')' => -1,
                _ => 0,
            };
            index++;
        }

        return text[open..index];
    }
    private static bool TakesAGuard(string name, string arguments) => (name.Trim() switch {
        "new WorldSessionSceneEmitter" => arguments.Contains(comparisonType: StringComparison.Ordinal, value: "mirror:"),
        "new WorldRoutedScene" => arguments.Contains(comparisonType: StringComparison.Ordinal, value: "endpoint:"),
        "new WorldEnvironmentResolve" or "new WorldThemeResolve" => true,
        "WorldMarkerAlphas.Resolve" => true,
        "ResolveChase" => arguments.Contains(comparisonType: StringComparison.Ordinal, value: "bodyOrientation:"),
        "ResolveCamera" => arguments.Contains(comparisonType: StringComparison.Ordinal, value: "cameraName:"),
        // The camera rig compiler and its cache: the call that names a program, a document and a mirror.
        "WorldCameraRigCompiler.Compile" or "Compile" or ".Resolve" => (
            arguments.Contains(comparisonType: StringComparison.Ordinal, value: "program:") &&
            arguments.Contains(comparisonType: StringComparison.Ordinal, value: "definition:") &&
            arguments.Contains(comparisonType: StringComparison.Ordinal, value: "mirror:")
        ),
        _ => false,
    });

    [Fact]
    public void Every_site_that_resolves_a_bound_value_hands_it_the_guard() {
        var root = Path.Combine(path1: AuthoredGameFixtures.Root, path2: "src");
        var checkedCalls = 0;
        var missing = new List<string>();

        foreach (var path in Directory.EnumerateFiles(path: root, searchOption: SearchOption.AllDirectories, searchPattern: "*.cs")) {
            if (path.Contains(comparisonType: StringComparison.Ordinal, value: $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || path.Contains(comparisonType: StringComparison.Ordinal, value: $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) {
                continue;
            }

            var text = File.ReadAllText(path: path);

            foreach (Match call in Call().Matches(input: text)) {
                var arguments = Arguments(text: text, open: (call.Index + call.Length));

                if (!TakesAGuard(name: call.Groups["name"].Value, arguments: arguments)) {
                    continue;
                }

                checkedCalls++;

                if (!arguments.Contains(comparisonType: StringComparison.Ordinal, value: "domains:")) {
                    missing.Add(item: $"{Path.GetRelativePath(path: path, relativeTo: root)}: {call.Groups["name"].Value.Trim()}(...) at offset {call.Index}");
                }
            }
        }

        // The scan reaches the sites it names: the presenter, binder, seat view and emitters, among others.
        Assert.True(condition: (checkedCalls >= 12), userMessage: $"the scan recognized only {checkedCalls} guarded calls");
        Assert.Empty(collection: missing);
    }
}
