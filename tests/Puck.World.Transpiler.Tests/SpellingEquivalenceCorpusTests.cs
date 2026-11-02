using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>The spelling-equivalence corpus: the owner's ruling that ergonomic is optimal, held as data. Each pair under
/// <c>SpellingEquivalence/&lt;pair&gt;/</c> is two spellings that mean the same thing (<c>a.puck</c> names the claim
/// in its first line), and the pair holds when both compile to the identical canonical document and the identical
/// generated test worlds, so every identity and every price derived from the document is identical too. A pair that
/// fails is a compiler defect: fixed, or listed in <see cref="Open"/> against its entry in the open items.</summary>
public class SpellingEquivalenceCorpusTests {
    // A pair the compiler does not yet hold, with the open item that owns it (docs/plans/open-items.md).
    private static readonly Dictionary<string, string> Open = new(comparer: StringComparer.Ordinal) {
        ["chamfer-default"] = "state-and-language.md G13: a zero chamfer is the chamfer omitted",
        ["float-spelling"] = "open items: canonical float rounding for creation scalars",
        ["profile-start"] = "open items: canonical start vertex for a closed profile",
        ["ternary-fold"] = "state-and-language.md G14: constant expressions fold before lowering",
    };

    private static string CorpusDirectory() {
        var directory = AppContext.BaseDirectory;

        while (!Directory.Exists(path: Path.Combine(path1: directory, path2: "tests", path3: "Puck.World.Transpiler.Tests", path4: "SpellingEquivalence"))) {
            directory = (Path.GetDirectoryName(path: directory) ?? throw new DirectoryNotFoundException(message: "SpellingEquivalence corpus not found above the test assembly."));
        }

        return Path.Combine(path1: directory, path2: "tests", path3: "Puck.World.Transpiler.Tests", path4: "SpellingEquivalence");
    }
    private static (string Document, string[] Tests) Compile(string path) {
        var compilation = WorldCompiler.CompileFile(cancellationToken: TestContext.Current.CancellationToken, path: path);

        Assert.False(condition: compilation.Diagnostics.HasErrors, userMessage: compilation.Diagnostics.FormatReport());

        // A generated test world is named for the source's file stem, which is the one thing the two spellings'
        // files must not share.
        var stem = $"{Path.GetFileNameWithoutExtension(path: path)}~";

        return (
            compilation.Json!.ToJsonString(),
            [.. compilation.TestWorlds.Select(selector: world => $"{world.Name}\n{world.Json.ToJsonString()}".Replace(oldValue: stem, newValue: "~", comparisonType: StringComparison.Ordinal))]
        );
    }

    /// <summary>Gets every pair in the corpus.</summary>
    public static TheoryData<string> Pairs => [.. Directory.GetDirectories(path: CorpusDirectory()).Select(selector: static directory => Path.GetFileName(path: directory)).Order(comparer: StringComparer.Ordinal)];

    [Theory]
    [MemberData(memberName: nameof(Pairs))]
    public void BothSpellingsCompileToOneDocument(string pair) {
        var directory = Path.Combine(path1: CorpusDirectory(), path2: pair);
        var a = Compile(path: Path.Combine(path1: directory, path2: "a.puck"));
        var b = Compile(path: Path.Combine(path1: directory, path2: "b.puck"));
        var holds = (string.Equals(a: a.Document, b: b.Document, comparisonType: StringComparison.Ordinal) && a.Tests.SequenceEqual(second: b.Tests, comparer: StringComparer.Ordinal));

        if (Open.TryGetValue(key: pair, value: out var owner)) {
            Assert.False(condition: holds, userMessage: $"'{pair}' now holds: delete it from Open and its entry from the open items ({owner}).");

            return;
        }

        Assert.Equal(expected: a.Document, actual: b.Document);
        Assert.Equal(expected: a.Tests, actual: b.Tests);
    }
}
