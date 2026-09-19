using Puck.Cli.Transpiler;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Rewriting;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>The atomicity law: over five sources that all need the same rewrite, a run refused on the third leaves
/// all five byte-identical and leaves no staging file behind — whether the third does not parse, moves a member the
/// migration does not declare, or cannot be opened for writing.</summary>
/// <remarks>The refusal reasons are checked one per case rather than together, because each enters the plan at a
/// different stage and the guarantee is the same at every one of them.</remarks>
public sealed class PuckMigrateAtomicityLawTests {
    // Rewrites the document's own identifier, which every fixture source carries, and also the host height, which
    // only the odd one out carries. Declaring `documentId` alone makes that second edit an undeclared difference.
    private sealed class Retitle : PuckMigration {
        private sealed class Rewriter : PuckSyntaxRewriter {
            protected override StatementNode? RewriteStatement(StatementNode statement) => (base.RewriteStatement(statement: statement) switch {
                PropertyNode property when string.Equals(
                    a: property.Name,
                    b: "documentId",
                    comparisonType: StringComparison.Ordinal
                ) => (property with { Value = new LiteralExpressionNode(Value: "atomicity-v2") { Trivia = property.Value.Trivia } }),
                PropertyNode property when string.Equals(
                    a: property.Name,
                    b: "height",
                    comparisonType: StringComparison.Ordinal
                ) => (property with { Value = new LiteralExpressionNode(Value: 480) { Trivia = property.Value.Trivia } }),
                var other => other,
            });
        }

        public override string Name => "retitle";
        public override IReadOnlyList<string> ReshapedMembers => ["documentId"];
        public override string Summary => "renames the document itself";

        public override DocumentNode Apply(DocumentNode document) => new Rewriter().Rewrite(document: document);
    }

    private const string Broken = "host {\n  when\n";
    private const string Undeclared = "schema: \"puck.world.definition.v1\"\n\ndocumentId: \"atomicity-v1\"\n\nhost {\n  height: 720\n}\n";
    private const string World = "schema: \"puck.world.definition.v1\"\n\ndocumentId: \"atomicity-v1\"\n";

    private static readonly IReadOnlyList<PuckMigration> Migrations = [new Retitle()];

    private static string Path3(string directory) => Path.Combine(
        path1: directory,
        path2: "f3.puck"
    );
    // Five sources the migration would rewrite, of which the third is whatever the case is about.
    private static string Fixture(string third) {
        var directory = Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"puck-migrate-atomicity-{Guid.NewGuid():N}"
        );

        Directory.CreateDirectory(path: directory);

        for (var index = 1; (index <= 5); ++index) {
            File.WriteAllText(
                contents: ((index == 3)
                    ? third
                    : World),
                path: Path.Combine(
                    path1: directory,
                    path2: $"f{index}.puck"
                )
            );
        }

        return directory;
    }
    private static void AssertRefusedAndUntouched(string directory, string third) {
        Assert.Equal(
            actual: PuckMigrateCommand.Execute(
                check: false,
                migrations: Migrations,
                name: "retitle",
                path: directory
            ),
            expected: 2
        );

        for (var index = 1; (index <= 5); ++index) {
            Assert.Equal(
                actual: File.ReadAllText(path: Path.Combine(
                    path1: directory,
                    path2: $"f{index}.puck"
                )),
                expected: ((index == 3)
                    ? third
                    : World)
            );
        }

        // A staging file left in the tree would be picked up by nothing and would confuse the next run's author.
        Assert.Empty(collection: Directory.GetFiles(
            path: directory,
            searchPattern: "*.migrate-tmp"
        ));
    }

    [Fact]
    public void AThirdSourceThatDoesNotParseLeavesAllFive() {
        var directory = Fixture(third: Broken);

        try {
            AssertRefusedAndUntouched(
                directory: directory,
                third: Broken
            );
        } finally {
            Directory.Delete(
                path: directory,
                recursive: true
            );
        }
    }
    [Fact]
    public void AThirdSourceWithAnUndeclaredDifferenceLeavesAllFive() {
        var directory = Fixture(third: Undeclared);

        try {
            AssertRefusedAndUntouched(
                directory: directory,
                third: Undeclared
            );
        } finally {
            Directory.Delete(
                path: directory,
                recursive: true
            );
        }
    }
    // The destination that cannot be replaced is refused while the run can still refuse for free, rather than at
    // its own turn in the write phase with earlier destinations already replaced.
    [Fact]
    public void AThirdSourceThatCannotBeWrittenLeavesAllFive() {
        var directory = Fixture(third: World);

        try {
            File.SetAttributes(
                fileAttributes: FileAttributes.ReadOnly,
                path: Path3(directory: directory)
            );
            AssertRefusedAndUntouched(
                directory: directory,
                third: World
            );
        } finally {
            File.SetAttributes(
                fileAttributes: FileAttributes.Normal,
                path: Path3(directory: directory)
            );
            Directory.Delete(
                path: directory,
                recursive: true
            );
        }
    }
    // The control: without the odd one out, the same run writes all five.
    [Fact]
    public void FiveWritableSourcesAreAllMigrated() {
        var directory = Fixture(third: World);

        try {
            Assert.Equal(
                actual: PuckMigrateCommand.Execute(
                    check: false,
                    migrations: Migrations,
                    name: "retitle",
                    path: directory
                ),
                expected: 0
            );

            for (var index = 1; (index <= 5); ++index) {
                Assert.Contains(
                    actualString: File.ReadAllText(path: Path.Combine(
                        path1: directory,
                        path2: $"f{index}.puck"
                    )),
                    expectedSubstring: "documentId: \"atomicity-v2\""
                );
            }

            Assert.Empty(collection: Directory.GetFiles(
                path: directory,
                searchPattern: "*.migrate-tmp"
            ));
        } finally {
            Directory.Delete(
                path: directory,
                recursive: true
            );
        }
    }
}
