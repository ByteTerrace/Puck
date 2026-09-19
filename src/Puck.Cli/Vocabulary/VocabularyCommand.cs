using System.CommandLine;
using System.Text;

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

        var path = Path.Combine(
            path1: repositoryRoot,
            path2: RelativePath
        );
        var table = WorldConstructs.Table;
        var text = table.Render();

        if (!check) {
            File.WriteAllText(
                contents: text,
                encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                path: path
            );
            Console.Out.WriteLine(value: $"vocabulary: wrote {RelativePath} ({table.Constructs.Count} constructs).");

            return 0;
        }

        if (!File.Exists(path: path)) {
            Console.Error.WriteLine(value: $"vocabulary: {RelativePath} is missing; run `puck vocabulary` to write it.");

            return 1;
        }

        var onDisk = File.ReadAllText(path: path).ReplaceLineEndings(replacementText: "\n");

        if (string.Equals(
            a: onDisk,
            b: text,
            comparisonType: StringComparison.Ordinal
        )) {
            Console.Out.WriteLine(value: $"vocabulary: {RelativePath} matches the table ({table.Constructs.Count} constructs).");

            return 0;
        }

        var expected = text.Split(separator: '\n');
        var actual = onDisk.Split(separator: '\n');
        var line = 0;

        while (
            (line < expected.Length) &&
            (line < actual.Length) &&
            string.Equals(
            a: expected[line],
            b: actual[line],
            comparisonType: StringComparison.Ordinal
        )
        ) {
            line++;
        }

        Console.Error.WriteLine(value: $"vocabulary: {RelativePath} disagrees with the table at line {(line + 1)}; run `puck vocabulary` to rewrite it.");
        Console.Error.WriteLine(value: $"  on disk:   {((line < actual.Length)
            ? actual[line]
            : "(end of file)")}");
        Console.Error.WriteLine(value: $"  generated: {((line < expected.Length)
            ? expected[line]
            : "(end of file)")}");

        return 1;
    }

    public static Command Create() {
        var checkOption = new Option<bool>(name: "--check") {
            Description = "Regenerate in memory and compare against docs/reference/world-vocabulary.md; write nothing, and exit 1 naming the first differing line.",
        };
        var command = new Command(
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
            name: "vocabulary"
        ) { checkOption };

        command.SetAction(action: parseResult => Run(check: parseResult.GetValue(option: checkOption)));

        return command;
    }
}
