using Puck.Cli.Formats;
using Xunit;

namespace Puck.Cli.Format.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a format's closure compiles each file with the usings its own project compiles it with (its
/// <c>Using</c> items, implicit usings and the <c>global using</c> directives its files state) and nowhere else, binds the
/// files its project links in and the generated <c>FormatShapes</c> constants, and so binds every repository type a
/// shipped format's units name.
/// </summary>
public sealed class FormatClosureScopeLawTests {
    private static FormatShapeSources Projects() => new(
        Files: new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
            ["src/Puck.Model/Row.cs"] = "namespace Puck.Model;\npublic sealed class Row { public int Value; }",
            ["src/Puck.Codec/Aliases.cs"] = "global using Cell = Puck.Model.Row;",
            ["src/Puck.Codec/Codec.cs"] = """
                namespace Puck.Codec;
                public static class Codec {
                    public const int FormatVersion = 1;
                    public static string Shape => FormatShapes.CodecFormatVersion;
                    public static void Write(Row row, Cell cell, byte[] bytes) { bytes[0] = (byte)Leaf(value: row.Value + cell.Value); }
                    [FormatLeaf]
                    private static int Leaf(int value) => value;
                }
                """,
            ["src/Puck.Other/Aliases.cs"] = "global using Cell = Puck.Other.Thing;",
            ["src/Puck.Other/OtherCodec.cs"] = """
                namespace Puck.Other;
                public sealed class Thing { public long Value; }
                public static class OtherCodec {
                    public const int FormatVersion = 2;
                    public static void Write(Cell cell, Row row, byte[] bytes) { bytes[0] = (byte)cell.Value; }
                }
                """,
        },
        Linked: new Dictionary<string, (string, string)>(comparer: StringComparer.Ordinal) {
            ["build/FormatLeafAttribute.cs"] = ("namespace Puck;\ninternal sealed class FormatLeafAttribute : Attribute { }", "src/Puck.Codec/"),
        },
        Projects: new Dictionary<string, IReadOnlyList<ProjectUsing>>(comparer: StringComparer.Ordinal) {
            ["src/Puck.Codec/"] = [new(Name: "System"), new(Name: "Puck.Model")],
            ["src/Puck.Model/"] = [new(Name: "System")],
            ["src/Puck.Other/"] = [new(Name: "System")],
        }
    );
    private static IEnumerable<string> Layouts(FormatShapeSources sources, string id) => FormatVersionsLedger.Explain(id: id, sources: sources)!.Value.Closure.Units.Where(predicate: static unit => (unit.Kind == FormatShapeClosure.UnitKind.Layout)).Select(selector: static unit => unit.Key);

    [Fact]
    public void AProjectsUsingsReachItsOwnFilesAndNoOtherProjects() {
        var sources = Projects();
        var unbound = FormatVersionsLedger.Unbound(sources: sources).ToDictionary(keySelector: static pair => pair.Id, elementSelector: static pair => pair.Unbound, comparer: StringComparer.Ordinal);

        // The codec's project imports Puck.Model and aliases Cell to its Row; the linked marker and the generated constant bind.
        Assert.Empty(collection: unbound["Codec.FormatVersion"].Repository);
        Assert.Empty(collection: unbound["Codec.FormatVersion"].Outside);
        Assert.Contains(collection: Layouts(id: "Codec.FormatVersion", sources: sources), expected: "T:Puck.Model.Row");
        // The other project imports neither: its Cell is its own Thing, and Row binds to nothing there.
        Assert.Equal(expected: ["Row"], actual: unbound["OtherCodec.FormatVersion"].Outside);
        Assert.Contains(collection: Layouts(id: "OtherCodec.FormatVersion", sources: sources), expected: "T:Puck.Other.Thing");
        Assert.DoesNotContain(collection: Layouts(id: "OtherCodec.FormatVersion", sources: sources), expected: "T:Puck.Model.Row");

        // A type a project using brings into a codec's closure is its shape.
        var widened = sources with { Files = new Dictionary<string, string>(collection: sources.Files, comparer: StringComparer.Ordinal) { ["src/Puck.Model/Row.cs"] = "namespace Puck.Model;\npublic sealed class Row { public long Value; }" } };

        Assert.NotEqual(
            expected: FormatVersionsLedger.Explain(id: "Codec.FormatVersion", sources: sources)!.Value.Entry.Shape,
            actual: FormatVersionsLedger.Explain(id: "Codec.FormatVersion", sources: widened)!.Value.Entry.Shape
        );
    }
    [Fact]
    public void EveryRepositoryTypeAShippedClosureNamesBinds() {
        Assert.True(condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot));

        var blind = FormatVersionsLedger.Unbound(sources: FormatsCommand.ReadSources(repositoryRoot: repositoryRoot)).Where(predicate: static pair => (pair.Unbound.Repository.Count != 0)).ToArray();

        Assert.True(
            condition: (blind.Length == 0),
            userMessage: $"{blind.Length} format closure(s) name repository types they cannot bind:\n{string.Join(separator: '\n', values: blind.Select(selector: static pair => $"{pair.Id}: {string.Join(separator: ", ", values: pair.Unbound.Repository)}"))}"
        );
    }
}
