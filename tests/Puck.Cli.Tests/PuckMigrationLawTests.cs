using Puck.Cli.Transpiler;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Rewriting;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>The migration law: over a fixture carrying a comment in every position the grammar admits one, a
/// blank-line run, an <c>import</c>, a <c>template</c>, a <c>for</c>, and an <c>sql { }</c> block, a named rewrite
/// changes the one thing it names and nothing else — every comment and blank line comes back where its author put
/// it, the compile-time layer is still source rather than its expansion, the compiled document is unchanged apart
/// from the members the migration declares, and a second run is a fixed point. A rewrite that moves a member it
/// did not declare, or that loses a comment without declaring that it reshapes comments, is refused with nothing
/// written.</summary>
/// <remarks>The migrations here are the suite's own: the shipped registry is empty, because a migration lands with
/// the reshape it serves and is deleted once it has run.</remarks>
public sealed class PuckMigrationLawTests {
    // Renames one compile-time constant and every read of it. The constant is evaluated away before the document
    // exists, so the rewrite declares nothing and the compiled document may not move at all.
    private class RenameConstant : PuckMigration {
        private sealed class Rewriter : PuckSyntaxRewriter {
            public required bool Shorten { get; init; }

            protected override ExpressionNode RewriteExpression(ExpressionNode expression) => (base.RewriteExpression(expression: expression) switch {
                IdentifierExpressionNode identifier when Named(candidate: identifier.Name) => (identifier with { Name = Renamed }),
                var other => other,
            });
            protected override StatementNode? RewriteStatement(StatementNode statement) => (base.RewriteStatement(statement: statement) switch {
                LetNode declaration when Named(candidate: declaration.Name) => (declaration with { Name = Renamed }),
                PropertyNode property when (this.Shorten && string.Equals(
                    a: property.Name,
                    b: "height",
                    comparisonType: StringComparison.Ordinal
                )) => (property with { Value = new LiteralExpressionNode(Value: ShortHeight) { Trivia = property.Value.Trivia } }),
                var other => other,
            });
        }

        public override string Name => "rename-constant";
        public override IReadOnlyList<string> ReshapedMembers => [];
        public override string Summary => "renames one compile-time constant and every read of it";
        // Whether the rewrite also moves the host's height, which is a document member and not compile-time at all.
        protected virtual bool Shorten => false;

        public override DocumentNode Apply(DocumentNode document) => new Rewriter { Shorten = this.Shorten }.Rewrite(document: document);
    }
    private sealed class RenameAndDeclare : RenameConstant {
        public override string Name => "rename-and-declare";
        public override IReadOnlyList<string> ReshapedMembers => ["host/height"];
        public override string Summary => "renames the constant and shortens the host, saying so";
        protected override bool Shorten => true;
    }
    private sealed class RenameAndSneak : RenameConstant {
        public override string Name => "rename-and-sneak";
        public override IReadOnlyList<string> ReshapedMembers => [];
        public override string Summary => "renames the constant and shortens the host without saying so";
        protected override bool Shorten => true;
    }
    // Drops the trivia of one property, which moves no document member at all: the whole effect is the loss of the
    // comment above it.
    private class DropAComment : PuckMigration {
        private sealed class Rewriter : PuckSyntaxRewriter {
            protected override StatementNode? RewriteStatement(StatementNode statement) => (base.RewriteStatement(statement: statement) switch {
                PropertyNode property when string.Equals(
                    a: property.Name,
                    b: "height",
                    comparisonType: StringComparison.Ordinal
                ) => (property with { Trivia = SyntaxTrivia.None }),
                var other => other,
            });
        }

        public override string Name => "drop-a-comment";
        public override IReadOnlyList<string> ReshapedMembers => [];
        public override string Summary => "drops one node's trivia without saying so";

        public override DocumentNode Apply(DocumentNode document) => new Rewriter().Rewrite(document: document);
    }
    private sealed class DropACommentDeclared : DropAComment {
        public override string Name => "drop-a-comment-declared";
        public override bool ReshapesComments => true;
        public override string Summary => "drops one node's trivia, saying so";
    }

    private const string Constant = "speed";
    private const string Renamed = "pace";
    private const int ShortHeight = 480;
    // Every comment position the grammar admits, a two-line blank run, and one of each compile-time construct. The
    // word the proof migration renames appears only as an identifier, so the law can state the whole expected file
    // as one textual substitution.
    private const string Root = """
        schema: "puck.world.definition.v1"

        // The document's own comment, above everything.
        documentId: "migrate-law-v1" // and one at the end of a line

        // A compile-time constant: evaluated away, so renaming it moves no member.
        let speed = 4


        // Two blank lines stand above this one.
        let lanes = [1, 2, 3]

        import "shared.puck"

        // A template stays a template rather than its expansion.
        template lane(index, width = 2) {
          // inside the template body
          layout index {
            seatCount: width
          }
        }

        sql {
          DECLARE gold INT DEFAULT 10 CHECK (gold >= 0);
        }

        host {
          // inside a block
          height: 720
          /* a delimited comment on its own line */
          targetHertz: 60hz
          width: speed * 160 // and one at the end of a line
        }

        state {
          world {
            // above a row the constant sizes
            slot hp = speed
          }
        }

        views {
          // above a compile-time loop
          for index in lanes {
            // inside the loop body
            layout $"lane-{index}" {
              seatCount: 2
            }
          }
        }
        """;
    private const string Shared = """
        // The module the root imports; it declares no schema of its own.

        host {
          // and a comment inside it
          journalDepth: 2000
        }
        """;

    private static readonly IReadOnlyList<PuckMigration> Migrations = [new DropAComment(), new DropACommentDeclared(), new RenameAndDeclare(), new RenameAndSneak(), new RenameConstant()];

    private static bool Named(string candidate) => string.Equals(
        a: candidate,
        b: Constant,
        comparisonType: StringComparison.Ordinal
    );
    private static string Formatted(string source) => PuckSourceText.Formatted(source: source);
    private static string Fixture() {
        var directory = Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"puck-migrate-law-{Guid.NewGuid():N}"
        );

        Directory.CreateDirectory(path: directory);
        File.WriteAllText(
            contents: Formatted(source: Root),
            path: Path.Combine(
                path1: directory,
                path2: "world.puck"
            )
        );
        File.WriteAllText(
            contents: Formatted(source: Shared),
            path: Path.Combine(
                path1: directory,
                path2: "shared.puck"
            )
        );

        return directory;
    }
    private static int Migrate(string directory, string name = "rename-constant") => PuckMigrateCommand.Execute(
        check: false,
        migrations: Migrations,
        name: name,
        path: directory
    );
    private static string Read(string directory, string file) => File.ReadAllText(path: Path.Combine(
        path1: directory,
        path2: file
    ));

    [Fact]
    public void TheProofMigrationChangesTheConstantAndNothingElse() {
        var directory = Fixture();

        try {
            var expected = Formatted(source: Root).Replace(
                comparisonType: StringComparison.Ordinal,
                newValue: Renamed,
                oldValue: Constant
            );
            var shared = Read(
                directory: directory,
                file: "shared.puck"
            );

            Assert.Equal(
                actual: Migrate(directory: directory),
                expected: 0
            );

            var migrated = Read(
                directory: directory,
                file: "world.puck"
            );

            // One substitution accounts for the whole file, so every comment, every blank line, and every line
            // break came back exactly as its author wrote it.
            Assert.Equal(
                actual: migrated,
                expected: expected
            );
            // A source the migration had nothing to do in is left as it was rather than reprinted.
            Assert.Equal(
                actual: Read(
                    directory: directory,
                    file: "shared.puck"
                ),
                expected: shared
            );

            // The compile-time layer is still source: the rewrite ran on the tree, never on a lowered document.
            foreach (var construct in new[] {
                "let pace = 4",
                "let lanes = [1, 2, 3]",
                "import \"shared.puck\"",
                "template lane(index, width = 2)",
                "sql {",
                "for index in lanes {",
            }) {
                Assert.Contains(
                    actualString: migrated,
                    expectedSubstring: construct
                );
            }
        } finally {
            Directory.Delete(
                path: directory,
                recursive: true
            );
        }
    }
    [Fact]
    public void TheProofMigrationRunTwiceIsAFixedPoint() {
        var directory = Fixture();

        try {
            Assert.Equal(
                actual: Migrate(directory: directory),
                expected: 0
            );

            var once = Read(
                directory: directory,
                file: "world.puck"
            );

            Assert.Equal(
                actual: Migrate(directory: directory),
                expected: 0
            );
            Assert.Equal(
                actual: Read(
                    directory: directory,
                    file: "world.puck"
                ),
                expected: once
            );
        } finally {
            Directory.Delete(
                path: directory,
                recursive: true
            );
        }
    }
    [Fact]
    public void ADeclaredMemberMayMove() {
        var directory = Fixture();

        try {
            Assert.Equal(
                actual: Migrate(
                    directory: directory,
                    name: "rename-and-declare"
                ),
                expected: 0
            );
            Assert.Contains(
                actualString: Read(
                    directory: directory,
                    file: "world.puck"
                ),
                expectedSubstring: $"height: {ShortHeight}"
            );
        } finally {
            Directory.Delete(
                path: directory,
                recursive: true
            );
        }
    }
    // A comment is not in the document, so the member declaration cannot speak for one and the member comparison
    // cannot see it go.
    [Fact]
    public void AnUndeclaredCommentChangeIsRefusedAndNothingIsWritten() {
        var directory = Fixture();

        try {
            var before = Read(
                directory: directory,
                file: "world.puck"
            );

            Assert.Contains(
                actualString: before,
                expectedSubstring: "// inside a block"
            );
            Assert.Equal(
                actual: Migrate(
                    directory: directory,
                    name: "drop-a-comment"
                ),
                expected: 2
            );
            Assert.Equal(
                actual: Read(
                    directory: directory,
                    file: "world.puck"
                ),
                expected: before
            );
        } finally {
            Directory.Delete(
                path: directory,
                recursive: true
            );
        }
    }
    // The same rewrite, declaring it: the verdict is about the declaration, not about the edit.
    [Fact]
    public void ADeclaredCommentChangeMayLandDroppingTheComment() {
        var directory = Fixture();

        try {
            Assert.Equal(
                actual: Migrate(
                    directory: directory,
                    name: "drop-a-comment-declared"
                ),
                expected: 0
            );
            Assert.DoesNotContain(
                actualString: Read(
                    directory: directory,
                    file: "world.puck"
                ),
                expectedSubstring: "// inside a block"
            );
        } finally {
            Directory.Delete(
                path: directory,
                recursive: true
            );
        }
    }
    // The same rewrite, declaring nothing: the verdict is about the declaration, not about the edit.
    [Fact]
    public void AnUndeclaredMemberIsRefusedAndNothingIsWritten() {
        var directory = Fixture();

        try {
            var before = Read(
                directory: directory,
                file: "world.puck"
            );

            Assert.Equal(
                actual: Migrate(
                    directory: directory,
                    name: "rename-and-sneak"
                ),
                expected: 2
            );
            Assert.Equal(
                actual: Read(
                    directory: directory,
                    file: "world.puck"
                ),
                expected: before
            );
        } finally {
            Directory.Delete(
                path: directory,
                recursive: true
            );
        }
    }
}
