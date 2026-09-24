using Puck.Cli.Format;

using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// Pins phase 0's scope to the files the verb enumerated. <c>dotnet format whitespace</c> takes those files as
/// <c>--include</c> arguments, batched to fit a command line, and its matcher is unforgiving: a path that does not match
/// a document matches nothing, and matching nothing is silent, so a check would report a clean tree it never
/// opened. A project this repository
/// builds links <c>build/VerifiedCodeAttribute.cs</c> in from two directories above it, and a run over that project's
/// root must still leave that file alone.
/// </summary>
public sealed class FormatWhitespaceScopeTests {
    [Fact]
    public void ATreeWideCorpusIsSplitIntoOrderedBatchesThatEachFitACommandLine() {
        var workspace = Path.Combine(
            path1: Path.GetTempPath(),
            path2: "workspace"
        );
        // Nine thousand files at a realistic depth is several times the 32767-character command line one invocation
        // of the old single-call phase needed room for.
        var files = Enumerable.Range(
            count: 9000,
            start: 0
        ).Select(selector: index => Path.Combine(
            path1: workspace,
            path2: $"Puck.Project{(index / 100):D3}",
            path3: $"Some Folder/File{index:D5}.cs"
        )).ToArray();
        var includes = WhitespacePhase.Includes(
            files: files,
            workspace: workspace
        );

        Assert.True(condition: (includes.Sum(selector: static include => (include.Length + 3)) > (4 * 32767)));

        var batches = WhitespacePhase.Batches(includes: includes);

        Assert.True(condition: (batches.Count > 4));
        Assert.All(
            action: static batch => Assert.True(condition: (batch.Sum(selector: static include => (include.Length + 3)) <= WhitespacePhase.CommandLineBudget)),
            collection: batches
        );
        // The same files, once each, in the order they were enumerated.
        Assert.Equal(
            actual: batches.SelectMany(selector: static batch => batch),
            expected: includes
        );
        Assert.Equal(
            actual: includes[0],
            expected: "Puck.Project000/Some Folder/File00000.cs"
        );
    }
    [Fact]
    public void ABatchNeverDropsAPathOrEmitsAnEmptyInvocation() {
        Assert.Empty(collection: WhitespacePhase.Batches(includes: []));
        Assert.Equal(
            actual: WhitespacePhase.Batches(
                budget: 10,
                includes: ["a.cs", "bb.cs", "a-path-longer-than-the-budget.cs", "c.cs"]
            ),
            expected: [["a.cs"], ["bb.cs"], ["a-path-longer-than-the-budget.cs"], ["c.cs"]]
        );
        Assert.Equal(
            actual: WhitespacePhase.Batches(
                budget: 14,
                includes: ["a.cs", "b.cs", "c.cs"]
            ),
            expected: [["a.cs", "b.cs"], ["c.cs"]]
        );
    }
    [Fact]
    public void ARootIsFormattedWithoutReachingASourceItsProjectLinksFromAbove() {
        var outer = Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"puck-format-whitespace-scope-{Guid.NewGuid():N}"
        );
        var root = Path.Combine(
            path1: outer,
            path2: "Project"
        );
        var linked = Path.Combine(
            path1: outer,
            path2: "Linked.cs"
        );
        var owned = Path.Combine(
            path1: root,
            path2: "with space",
            path3: "Owned.cs"
        );
        const string Unformatted = "internal class   Sample { }\n";

        Directory.CreateDirectory(path: Path.GetDirectoryName(path: owned)!);

        try {
            // The scratch tree sits outside the checkout, so it carries its own line-ending policy.
            File.WriteAllText(
                contents: "root = true\n\n[*.cs]\nend_of_line = lf\ninsert_final_newline = true\n",
                path: Path.Combine(
                    path1: outer,
                    path2: ".editorconfig"
                )
            );
            File.WriteAllText(
                contents: "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><Compile Include=\"../Linked.cs\" Link=\"Linked.cs\" /></ItemGroup></Project>",
                path: Path.Combine(
                    path1: root,
                    path2: "Project.csproj"
                )
            );
            File.WriteAllText(
                contents: Unformatted,
                path: linked
            );
            File.WriteAllText(
                contents: Unformatted,
                path: owned
            );

            Assert.Equal(
                actual: WhitespacePhase.Run(
                    rootArgument: root,
                    check: true
                ),
                expected: 1
            );
            Assert.Equal(
                actual: WhitespacePhase.Run(
                    rootArgument: root,
                    check: false
                ),
                expected: 0
            );
            Assert.Equal(
                actual: File.ReadAllText(path: owned),
                expected: "internal class Sample { }\n"
            );
            Assert.Equal(
                actual: File.ReadAllText(path: linked),
                expected: Unformatted
            );
        } finally {
            Directory.Delete(
                path: outer,
                recursive: true
            );
        }
    }
}
