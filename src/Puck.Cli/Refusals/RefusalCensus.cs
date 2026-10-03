using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Puck.Cli.Refusals;

/// <summary>Counts the refusals <c>world.refusals</c> lists from source, independently of the World's runtime
/// reflection: every enum member tagged <c>[Refusal(…)]</c> in the projects whose assemblies the World's catalog
/// anchors, and the distinct doors they name. The census canary holds the running World's scan to it, so a door whose
/// assembly drops out of either side shows as a disagreement rather than a changed number.</summary>
internal static class RefusalCensus {
    /// <summary>The token a canary line expectation spells in place of the census header the runner computes.</summary>
    public const string Token = "{refusal-census}";

    /// <summary>Gets the projects counted, one per assembly the World's refusal catalog anchors.</summary>
    public static IReadOnlyList<string> Projects { get; } = [
        "Puck.State",
        "Puck.State.Rules",
        "Puck.World.Addons",
        "Puck.World.Client",
        "Puck.World.Schema",
        "Puck.World.Server",
    ];

    /// <summary>Counts the tagged refusal members and their distinct doors across <see cref="Projects"/>.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <returns>The member count and the door count.</returns>
    /// <exception cref="InvalidDataException">A tag names its door with something other than a string literal.</exception>
    public static (int Refusals, int Doors) Count(string repositoryRoot) {
        var refusals = 0;
        var doors = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var project in Projects) {
            foreach (var file in Directory.EnumerateFiles(path: Path.Combine(path1: repositoryRoot, path2: "src", path3: project), searchPattern: "*.cs", searchOption: SearchOption.AllDirectories)) {
                var relative = file.Replace(oldChar: '\\', newChar: '/');

                if (relative.Contains(value: "/obj/", comparisonType: StringComparison.Ordinal) || relative.Contains(value: "/bin/", comparisonType: StringComparison.Ordinal)) {
                    continue;
                }

                var root = CSharpSyntaxTree.ParseText(text: File.ReadAllText(path: file), path: file).GetRoot();

                foreach (var member in root.DescendantNodes().OfType<EnumMemberDeclarationSyntax>()) {
                    foreach (var attribute in member.AttributeLists.SelectMany(selector: static list => list.Attributes)) {
                        if (attribute.Name.ToString() is not ("Refusal" or "RefusalAttribute")) {
                            continue;
                        }

                        var door = (attribute.ArgumentList?.Arguments.FirstOrDefault(predicate: static argument => (argument.NameColon?.Name.Identifier.Text == "door"))
                            ?? attribute.ArgumentList?.Arguments.FirstOrDefault());

                        if (door?.Expression is not LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal) {
                            throw new InvalidDataException(message: $"{file}: '{member.Identifier.Text}' names its refusal door with no string literal.");
                        }

                        refusals++;
                        _ = doors.Add(item: literal.Token.ValueText);
                    }
                }
            }
        }

        return (refusals, doors.Count);
    }
    /// <summary>Spells the census as the header <c>world.refusals</c> prints over the whole catalog.</summary>
    /// <param name="census">The census.</param>
    /// <returns>The header.</returns>
    public static string Header((int Refusals, int Doors) census) => $"[world.refusals: {census.Refusals} across {census.Doors} door(s)";
}
