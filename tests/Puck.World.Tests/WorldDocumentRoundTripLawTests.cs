using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>CONTRACT UNDER TEST: the world document's writer never writes what its reader refuses. Every world a
/// shipped document, a fixture this suite boots, or a canary manifest boots is loaded through the door the game boots
/// it through, written by <see cref="WorldDefinitionSerialization.Serialize"/> (and its compact form, the one a
/// compiled world stores), and read back through <see cref="WorldDefinitionSerialization.Deserialize"/>, the door a
/// replay, checkpoint, or compiled world rehydrates through; what is read must write the same bytes again. Every
/// expression the document carries is also held to its one written form: the IR it is written as reads back as the
/// same program, and the infix text that program prints as (what a decompile, a trace, or a diagnostic shows) parses
/// back to the same IR.</summary>
/// <remarks>The corpus is enumerated, never listed (<see cref="WorldDocumentCorpus"/>). A document that does not boot
/// on its own is a module library or a fragment, whose rows reach the documents that compose it. <c>world.save</c>
/// itself, the file door, and a save's basis-preserving delta are held by <see cref="WorldSaveAuthoredDocumentLawTests"/>.</remarks>
public sealed class WorldDocumentRoundTripLawTests(WorldDocumentRoundTripLawTests.Staging staging) : IClassFixture<WorldDocumentRoundTripLawTests.Staging> {
    private const string InstructionsMember = "instructions";
    private const string SubprogramsMember = "subprograms";

    public static TheoryData<string> Corpus() => [.. WorldDocumentCorpus.ShippedDocuments()
        .Concat(second: WorldDocumentCorpus.FixtureDocuments())
        .Union(
            comparer: StringComparer.Ordinal,
            second: WorldDocumentCorpus.CanaryWorlds()
        )
        .Order(comparer: StringComparer.Ordinal)];
    [MemberData(nameof(Corpus))]
    [Theory]
    public void EveryWorldReadsBackAsTheDocumentItWrote(string relativePath) {
        if (!TryBoot(
            relativePath: relativePath,
            worlds: out var worlds
        )) {
            return;
        }

        var failures = new List<string>();

        foreach (var (source, definition) in worlds) {
            var world = Path.GetFileName(path: source);
            var written = WorldDefinitionSerialization.Serialize(definition: definition);

            ReadBack(
                definition: definition,
                failures: failures,
                route: $"{world}: the canonical form",
                utf8Json: written,
                written: written
            );
            ReadBack(
                definition: definition,
                failures: failures,
                route: $"{world}: the compact form",
                utf8Json: WorldDefinitionSerialization.SerializeCompact(definition: definition),
                written: written
            );
        }

        Assert.True(
            condition: (failures.Count == 0),
            userMessage: string.Join(
                separator: Environment.NewLine,
                values: failures
            )
        );
    }
    [MemberData(nameof(Corpus))]
    [Theory]
    public void EveryExpressionIsWrittenInTheOneSpellingItsReaderReads(string relativePath) {
        if (!TryBoot(
            relativePath: relativePath,
            worlds: out var worlds
        )) {
            return;
        }

        var failures = new List<string>();

        foreach (var (source, definition) in worlds) {
            var world = Path.GetFileName(path: source);
            var tree = JsonNode.Parse(utf8Json: WorldDefinitionSerialization.Serialize(definition: definition));

            foreach (var (path, node) in Programs(node: tree, path: "$")) {
                CheckProgram(
                    failures: failures,
                    node: node,
                    route: $"{world} {path}"
                );
            }
        }

        Assert.True(
            condition: (failures.Count == 0),
            userMessage: string.Join(
                separator: Environment.NewLine,
                values: failures
            )
        );
    }
    /// <summary>The expression law is not vacuous: the corpus carries expressions, among them a subprogram table and
    /// a reserved channel read, the two shapes a program writes beyond a flat postfix list.</summary>
    [Fact]
    public void TheCorpusCarriesExpressionsOfEveryWrittenShape() {
        var programs = 0;
        var subprograms = false;
        var channels = false;

        foreach (var relativePath in Corpus().Select(selector: static row => ((string)row.Data))) {
            if (!TryBoot(
                relativePath: relativePath,
                worlds: out var worlds
            )) {
                continue;
            }

            foreach (var (_, definition) in worlds) {
                var tree = JsonNode.Parse(utf8Json: WorldDefinitionSerialization.Serialize(definition: definition));

                foreach (var (_, node) in Programs(node: tree, path: "$")) {
                    programs++;
                    subprograms |= node.ContainsKey(propertyName: SubprogramsMember);
                    channels |= node.ToJsonString().Contains(
                        comparisonType: StringComparison.Ordinal,
                        value: "\"channel\""
                    );
                }
            }

            if (subprograms && channels) {
                break;
            }
        }

        Assert.True(
            condition: ((programs > 0) && subprograms && channels),
            userMessage: $"the corpus carries {programs} expression(s), a subprogram table: {subprograms}, a channel read: {channels}"
        );
    }
    /// <summary>A search job's score is stored in the one expression form and nowhere else: the reader refuses the
    /// score a corpus world writes once it is respelled as the infix text it prints as, and names the member it
    /// refused, while the same document with the written IR reads back. Every world whose source names a search
    /// section is held, and the corpus must carry at least one scored job.</summary>
    [Fact]
    public void ASearchScoreWrittenAsTextIsRefusedByName() {
        var scored = 0;
        var failures = new List<string>();

        foreach (var relativePath in Corpus().Select(selector: static row => ((string)row.Data))) {
            if (
                !File.ReadAllText(path: RepositoryPaths.Resolve(relativePath: relativePath)).Contains(
                    comparisonType: StringComparison.Ordinal,
                    value: "search"
                ) ||
                !TryBoot(
                    relativePath: relativePath,
                    worlds: out var worlds
                )
            ) {
                continue;
            }

            foreach (var (source, definition) in worlds) {
                var world = Path.GetFileName(path: source);
                var tree = JsonNode.Parse(utf8Json: WorldDefinitionSerialization.Serialize(definition: definition))!;

                if (tree["search"]?["jobs"] is not JsonArray jobs) {
                    continue;
                }

                for (var index = 0; (index < jobs.Count); index++) {
                    if (
                        (jobs[index] is not JsonObject job) ||
                        (job["score"] is not JsonObject program)
                    ) {
                        continue;
                    }

                    scored++;

                    var member = $"jobs[{index}].score";
                    var ir = program.DeepClone();

                    Assert.True(
                        condition: ExpressionSpelling.TryPrint(
                            program: ExpressionProgramJsonConverter.FromNode(node: ir),
                            text: out var text
                        ),
                        userMessage: $"{world} {member} prints as no infix text"
                    );
                    job["score"] = text;

                    if (Refusal(definition: definition, tree: tree) is not { } refusal) {
                        failures.Add(item: $"{world} {member}: the text \"{text}\" is read as a score");
                    } else if (!refusal.Contains(
                        comparisonType: StringComparison.Ordinal,
                        value: member
                    )) {
                        failures.Add(item: $"{world} {member}: the text \"{text}\" is refused without naming the member: {refusal}");
                    }

                    // The control: the same document, carrying the IR it wrote, reads back.
                    job["score"] = ir;

                    if (Refusal(definition: definition, tree: tree) is { } control) {
                        failures.Add(item: $"{world} {member}: the written IR is refused: {control}");
                    }
                }
            }
        }

        Assert.True(
            condition: ((scored > 0) && (failures.Count == 0)),
            userMessage: $"{scored} scored search job(s){Environment.NewLine}{string.Join(separator: Environment.NewLine, values: failures)}"
        );
    }

    // Why the reader refuses a document tree, or null when it reads the tree.
    private static string? Refusal(WorldDefinition definition, JsonNode tree) {
        try {
            _ = WorldDefinitionSerialization.Deserialize(
                documentDirectory: definition.DocumentDirectory,
                utf8Json: Encoding.UTF8.GetBytes(s: tree.ToJsonString())
            );

            return null;
        } catch (InvalidDataException exception) {
            return exception.Message;
        }
    }
    // A program's IR reads back as itself, and the infix text it prints as parses back to the same IR.
    private static void CheckProgram(JsonObject node, string route, List<string> failures) {
        ExpressionProgram program;

        try {
            program = ExpressionProgramJsonConverter.FromNode(node: node);
        } catch (JsonException exception) {
            failures.Add(item: $"{route}: the written IR is refused by its reader: {exception.Message} in {node.ToJsonString()}");

            return;
        }

        var rewritten = ExpressionProgramJsonConverter.ToNode(program: program);

        if (!JsonNode.DeepEquals(node1: node, node2: rewritten)) {
            failures.Add(item: $"{route}: the IR read back writes {rewritten.ToJsonString()}, not {node.ToJsonString()}");

            return;
        }

        if (!ExpressionSpelling.TryPrint(
            program: program,
            text: out var text
        )) {
            failures.Add(item: $"{route}: the program prints as no infix text: {node.ToJsonString()}");

            return;
        }

        if (!ExpressionSpelling.TryParse(
            error: out var error,
            program: out var parsed,
            text: text
        )) {
            failures.Add(item: $"{route}: the program prints as \"{text}\", which its reader refuses: {error}");

            return;
        }

        var reparsed = ExpressionProgramJsonConverter.ToNode(program: parsed);

        if (!JsonNode.DeepEquals(node1: node, node2: reparsed)) {
            failures.Add(item: $"{route}: the program prints as \"{text}\", which reads back as {reparsed.ToJsonString()}, not {node.ToJsonString()}");
        }
    }
    // Every expression program a written document carries: an object whose postfix list is its own, never an entry of
    // a program's subprogram table, which is read and printed with the program that owns it. The walk still descends
    // into a subprogram's instructions, where a channel argument can carry a program of its own.
    private static IEnumerable<(string Path, JsonObject Node)> Programs(JsonNode? node, string path, bool subprogram = false) {
        switch (node) {
            case JsonObject obj:
                var program = (obj[InstructionsMember] is JsonArray);

                if (program && !subprogram) {
                    yield return (path, obj);
                }

                foreach (var (member, child) in obj) {
                    if (
                        program &&
                        (member == SubprogramsMember) &&
                        (child is JsonArray table)
                    ) {
                        for (var index = 0; (index < table.Count); index++) {
                            foreach (var nested in Programs(node: table[index], path: $"{path}.{member}[{index}]", subprogram: true)) {
                                yield return nested;
                            }
                        }

                        continue;
                    }

                    foreach (var nested in Programs(node: child, path: $"{path}.{member}")) {
                        yield return nested;
                    }
                }

                break;
            case JsonArray array:
                for (var index = 0; (index < array.Count); index++) {
                    foreach (var nested in Programs(node: array[index], path: $"{path}[{index}]")) {
                        yield return nested;
                    }
                }

                break;
        }
    }
    private static void ReadBack(WorldDefinition definition, byte[] utf8Json, byte[] written, string route, List<string> failures) {
        WorldDefinition read;

        try {
            read = WorldDefinitionSerialization.Deserialize(
                documentDirectory: definition.DocumentDirectory,
                utf8Json: utf8Json
            );
        } catch (InvalidDataException exception) {
            failures.Add(item: $"{route} is refused by its reader: {exception.Message}");

            return;
        }

        var again = WorldDefinitionSerialization.Serialize(definition: read);

        if (!written.AsSpan().SequenceEqual(other: again)) {
            var at = written.AsSpan().CommonPrefixLength(other: again);
            var start = Math.Max(
                val1: 0,
                val2: (at - 64)
            );

            string Window(byte[] bytes) => Encoding.UTF8.GetString(bytes: bytes.AsSpan(start: start, length: Math.Min(
                val1: 160,
                val2: (bytes.Length - start)
            )));

            failures.Add(item: $"{route} reads back as a different document: '{Window(bytes: written)}' became '{Window(bytes: again)}'");
        }
    }
    private bool TryBoot(string relativePath, out List<(string Source, WorldDefinition Definition)> worlds) {
        if (WorldDocumentCorpus.TryBoot(
            path: RepositoryPaths.Resolve(relativePath: relativePath),
            reason: out var refusal,
            stagingDirectory: staging.For(relativePath: relativePath),
            worlds: out worlds
        )) {
            return true;
        }

        Assert.True(
            condition: (
                (refusal == WorldDocumentCorpus.ModuleLibrary) ||
                (WorldDocumentCorpus.ShippedDocuments().Contains(value: relativePath) && WorldDocumentCorpus.IsFragment(relativePath: relativePath))
            ),
            userMessage: $"{relativePath} does not boot, and it is neither a module library nor a fragment: {refusal}"
        );

        return false;
    }

    /// <summary>The directory composition sources stage their worlds into, deleted on dispose whatever the laws'
    /// outcome.</summary>
    public sealed class Staging : IDisposable {
        private readonly TemporaryDirectory m_directory = new();

        /// <inheritdoc/>
        public void Dispose() => m_directory.Dispose();
        /// <summary>Returns a fresh directory one boot of a composition source stages its worlds into, so the laws
        /// booting the same source never share one.</summary>
        /// <param name="relativePath">The repository-relative path of the composition source.</param>
        /// <returns>The full path of the staging directory, created.</returns>
        public string For(string relativePath) => Directory.CreateDirectory(path: m_directory.PathOf(name: $"{relativePath}/{Guid.NewGuid():N}")).FullName;
    }
}
