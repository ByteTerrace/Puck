using Puck.Cli.Transpiler;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Formatting;
using Puck.Transpiler.Rewriting;
using Puck.World.Transpiler;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>The printer's own layout for a fixture source.</summary>
/// <remarks><c>puck migrate</c> prints every source it writes, so a fixture already in this layout isolates the one
/// change a migration itself makes; a fixture in any other layout counts as changed on its first run.</remarks>
internal static class PuckSourceText {
    /// <summary>Returns <paramref name="source"/> as the printer writes it.</summary>
    /// <param name="source">The fixture source to print.</param>
    /// <returns>The printed source.</returns>
    public static string Formatted(string source) {
        var result = PuckPrinter.Format(
            source: source,
            vocabulary: CliVocabularyResolver.Instance.Resolve(source: source)
        );

        Assert.NotNull(@object: result.Value);

        return result.Value!;
    }
}

/// <summary>What <c>puck migrate</c> does with the run it was handed: an unknown name is refused against the
/// registry, a source it cannot parse refuses the whole run rather than half of it, <c>--check</c> reports the work
/// and writes nothing, a cartridge source is compiled and compared through the cartridge door, and a run with
/// nothing left to do succeeds.</summary>
/// <remarks>The migration here is the suite's own, so the case does not move with the shipped registry, which
/// carries only whatever reshape is mid-flight.</remarks>
public sealed class PuckMigrateCommandTests {
    private sealed class Retitle : PuckMigration {
        private sealed class Rewriter : PuckSyntaxRewriter {
            protected override StatementNode? RewriteStatement(StatementNode statement) => (base.RewriteStatement(statement: statement) switch {
                PropertyNode property when string.Equals(
                    a: property.Name,
                    b: "documentId",
                    comparisonType: StringComparison.Ordinal
                ) => (property with { Value = new LiteralExpressionNode(Value: Retitled) { Trivia = property.Value.Trivia } }),
                var other => other,
            });
        }

        public override string Name => "retitle";
        public override IReadOnlyList<string> ReshapedMembers => ["documentId"];
        public override string Summary => "renames the document itself";

        public override DocumentNode Apply(DocumentNode document) => new Rewriter().Rewrite(document: document);
    }
    // A cartridge carries `title` where a world carries `documentId`, so reaching the cartridge door needs its own
    // rewrite. The two arms differ only in what they declare.
    private class RetitleCartridge : PuckMigration {
        private sealed class Rewriter : PuckSyntaxRewriter {
            protected override StatementNode? RewriteStatement(StatementNode statement) => (base.RewriteStatement(statement: statement) switch {
                PropertyNode property when string.Equals(
                    a: property.Name,
                    b: "title",
                    comparisonType: StringComparison.Ordinal
                ) => (property with { Value = new LiteralExpressionNode(Value: RetitledCartridge) { Trivia = property.Value.Trivia } }),
                var other => other,
            });
        }

        public override string Name => "retitle-cartridge";
        public override IReadOnlyList<string> ReshapedMembers => ["title"];
        public override string Summary => "renames a cartridge";

        public override DocumentNode Apply(DocumentNode document) => new Rewriter().Rewrite(document: document);
    }
    private sealed class SneakCartridge : RetitleCartridge {
        public override string Name => "sneak-cartridge";
        public override IReadOnlyList<string> ReshapedMembers => [];
        public override string Summary => "renames a cartridge without saying so";
    }

    private const string Broken = "host {\n  when\n";
    // A source whose own `schema:` names the other vocabulary, so the before-and-after compile goes through the
    // cartridge door rather than the world one.
    private const string Cartridge = "schema: \"puck.cartridge.v1\"\n\n// The cartridge the run rewrites.\ntarget: \"cgb\"\n\ntitle: \"MIGRATE\"\n";
    // Parses, and carries nothing the migration matches, but names a row kind the world vocabulary refuses.
    private const string Incompatible = "schema: \"puck.world.definition.v1\"\n\nstate {\n  world {\n    slot hp : Nonesuch = 1\n  }\n}\n";
    private const string Retitled = "migrate-verb-v2";
    private const string RetitledCartridge = "MIGRATE-ROM";
    private const string World = "schema: \"puck.world.definition.v1\"\n\ndocumentId: \"migrate-verb-v1\"\n\nhost {\n  height: 720\n}\n";

    private static readonly IReadOnlyList<PuckMigration> Migrations = [new Retitle(), new RetitleCartridge(), new SneakCartridge()];

    private static string Fixture() {
        var directory = Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"puck-migrate-verb-{Guid.NewGuid():N}"
        );

        Directory.CreateDirectory(path: directory);
        File.WriteAllText(
            contents: World,
            path: Path.Combine(
                path1: directory,
                path2: "world.puck"
            )
        );

        return directory;
    }
    private static int Migrate(string directory, bool check = false, string name = "retitle") => PuckMigrateCommand.Execute(
        check: check,
        migrations: Migrations,
        name: name,
        path: directory
    );
    private static string Read(string directory) => File.ReadAllText(path: Path.Combine(
        path1: directory,
        path2: "world.puck"
    ));
    private static bool Refuses(string sourceText, string sourcePath) {
        var diagnostics = new DiagnosticBag();

        CompileCommand.CompileSource(
            diagnostics: diagnostics,
            imports: ImportHandling.Validate,
            sourceMap: new SourceMap(),
            sourcePath: sourcePath,
            sourceText: sourceText,
            vocabulary: CliVocabularyResolver.Instance.Resolve(source: sourceText)
        );

        return diagnostics.HasErrors;
    }

    [Fact]
    public void AnUnknownNameIsRefused() {
        var directory = Fixture();

        try {
            Assert.Equal(
                actual: Migrate(
                    directory: directory,
                    name: "no-such-migration"
                ),
                expected: 2
            );
            Assert.Equal(
                actual: Read(directory: directory),
                expected: World
            );
            // This suite's own name is not in the shipped registry, whatever that registry carries.
            Assert.Equal(
                actual: PuckMigrateCommand.Execute(
                    check: false,
                    name: "retitle",
                    path: directory
                ),
                expected: 2
            );
        } finally {
            Directory.Delete(
                path: directory,
                recursive: true
            );
        }
    }
    [Fact]
    public void AMissingPathIsAUsageError() => Assert.Equal(
        actual: PuckMigrateCommand.Execute(
            check: false,
            migrations: Migrations,
            name: "retitle",
            path: Path.Combine(
                path1: Path.GetTempPath(),
                path2: $"puck-migrate-absent-{Guid.NewGuid():N}"
            )
        ),
        expected: 2
    );
    // A run that rewrote one source and could not read another has migrated nothing, so it writes nothing.
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void ASourceThatDoesNotParseRefusesTheWholeRun(bool check) {
        var directory = Fixture();

        try {
            File.WriteAllText(
                contents: Broken,
                path: Path.Combine(
                    path1: directory,
                    path2: "broken.puck"
                )
            );

            Assert.Equal(
                actual: Migrate(
                    check: check,
                    directory: directory
                ),
                expected: 2
            );
            Assert.Equal(
                actual: Read(directory: directory),
                expected: World
            );
        } finally {
            Directory.Delete(
                path: directory,
                recursive: true
            );
        }
    }
    [Fact]
    public void CheckReportsTheWorkAndWritesNothing() {
        var directory = Fixture();

        try {
            Assert.Equal(
                actual: Migrate(
                    check: true,
                    directory: directory
                ),
                expected: 1
            );
            Assert.Equal(
                actual: Read(directory: directory),
                expected: World
            );
        } finally {
            Directory.Delete(
                path: directory,
                recursive: true
            );
        }
    }
    [Fact]
    public void ARunWithNothingLeftToDoSucceeds() {
        var directory = Fixture();

        try {
            Assert.Equal(
                actual: Migrate(directory: directory),
                expected: 0
            );
            Assert.Contains(
                actualString: Read(directory: directory),
                expectedSubstring: $"documentId: \"{Retitled}\""
            );
            Assert.Equal(
                actual: Migrate(directory: directory),
                expected: 0
            );
            Assert.Equal(
                actual: Migrate(
                    check: true,
                    directory: directory
                ),
                expected: 0
            );
        } finally {
            Directory.Delete(
                path: directory,
                recursive: true
            );
        }
    }
    // Only a source the run would change owes the before-and-after verdict, so a source the migration leaves
    // alone is never compiled and cannot refuse a sweep it has no part in.
    [Fact]
    public void ASourceTheMigrationLeavesAloneIsNeverCompiled() {
        var directory = Fixture();
        var incompatible = Path.Combine(
            path1: directory,
            path2: "incompatible.puck"
        );

        var printed = PuckSourceText.Formatted(source: Incompatible);

        try {
            File.WriteAllText(
                contents: printed,
                path: incompatible
            );

            Assert.True(condition: Refuses(
                sourcePath: incompatible,
                sourceText: printed
            ));
            Assert.Equal(
                actual: Migrate(directory: directory),
                expected: 0
            );
            Assert.Contains(
                actualString: Read(directory: directory),
                expectedSubstring: $"documentId: \"{Retitled}\""
            );
            Assert.Equal(
                actual: File.ReadAllText(path: incompatible),
                expected: printed
            );
        } finally {
            Directory.Delete(
                path: directory,
                recursive: true
            );
        }
    }
    // The two arms of one rewrite over a cartridge source. Declaring nothing must refuse, which it can only do if
    // the cartridge document was actually built and compared; declaring `title` must then write. A cartridge door
    // that produced no document would refuse both arms, so the second assertion is the discriminating one.
    [Fact]
    public void ACartridgeSourceIsCompiledThroughItsOwnDoor() {
        var directory = Fixture();
        var cartridge = Path.Combine(
            path1: directory,
            path2: "cart.puck"
        );

        try {
            File.WriteAllText(
                contents: Cartridge,
                path: cartridge
            );

            Assert.Equal(
                actual: PuckMigrateCommand.Execute(
                    check: false,
                    migrations: Migrations,
                    name: "sneak-cartridge",
                    path: cartridge
                ),
                expected: 2
            );
            Assert.Equal(
                actual: File.ReadAllText(path: cartridge),
                expected: Cartridge
            );
            Assert.Equal(
                actual: PuckMigrateCommand.Execute(
                    check: false,
                    migrations: Migrations,
                    name: "retitle-cartridge",
                    path: cartridge
                ),
                expected: 0
            );
            Assert.Contains(
                actualString: File.ReadAllText(path: cartridge),
                expectedSubstring: $"title: \"{RetitledCartridge}\""
            );
        } finally {
            Directory.Delete(
                path: directory,
                recursive: true
            );
        }
    }
    [Fact]
    public void OneFileMayBeMigratedOnItsOwn() {
        var directory = Fixture();
        var file = Path.Combine(
            path1: directory,
            path2: "world.puck"
        );

        try {
            Assert.Equal(
                actual: PuckMigrateCommand.Execute(
                    check: false,
                    migrations: Migrations,
                    name: "retitle",
                    path: file
                ),
                expected: 0
            );
            Assert.Contains(
                actualString: Read(directory: directory),
                expectedSubstring: $"documentId: \"{Retitled}\""
            );
        } finally {
            Directory.Delete(
                path: directory,
                recursive: true
            );
        }
    }
}
