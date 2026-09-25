using System.CommandLine;

using Puck.World.Transpiler.Vocabulary;

namespace Puck.Cli.Vocabulary;

// The `puck vocabulary` verb: writes docs/reference/world-vocabulary.md from the one construct table the parser's
// embedded-language test, the printer's sugar guards, and the language server's completion and hover already read,
// or under --check regenerates it in memory and compares — the same drift-detection shape `puck schema --check`,
// `puck registry --check` and `puck architecture --map` establish.
// Exit 0 wrote or matched, 1 check found drift, 2 usage error or missing repository root.
internal static class VocabularyCommand {
    private const string RelativePath = "docs/reference/world-vocabulary.md";

    private static int Run(bool check) {
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return 2;
        }

        var table = WorldConstructs.Table;

        return (CliGeneratedFile.WriteOrCheck(
            check: check,
            detail: $" ({table.Constructs.Count} constructs)",
            relativePath: RelativePath,
            repositoryRoot: repositoryRoot,
            source: "the table",
            text: table.Render(),
            verb: "vocabulary"
        )
            ? 0
            : 1);
    }

    public static Command Create() => CliOptions.CheckVerb(
        checkDescription: "Regenerate in memory and compare against docs/reference/world-vocabulary.md; write nothing, and exit 1 naming the first differing line.",
        description: """
        The world authoring vocabulary, generated and checked.

        Generated from Puck.World.Transpiler.Vocabulary.WorldConstructs.Table: every construct
        of puck.world.definition.v1's surface, its members with their kinds and defaults, the
        document member it lowers to, and what the printer requires before it may print a
        document node back as that construct. The parser's embedded-language test, the
        decompiler's sugar guards, and the language server's completion and hover read the same
        table, so this file cannot describe a construct the code spells differently.

        What a document field means is `puck schema`'s and which fields carry names is
        `puck registry`'s; this table is the surface and its mapping onto them.

        Written to: docs/reference/world-vocabulary.md.
        """,
        name: "vocabulary",
        run: Run
    );
}
