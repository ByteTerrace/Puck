using Puck.Cli.Formats;

using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// Laws for <c>FormatVersions.json</c> and <c>puck formats</c> (<see cref="FormatVersionsLedger"/>): discovery reads
/// the tokens the source declares; recording round-trips; a retokened, reshaped, unrecorded, stale, or moved format is
/// drift, and none of it demands a token bump; the shipped ledger and the generated <c>FormatShapes.g.cs</c> files equal
/// the shipped source; and two branches that edit one codec differently collide in the ledger on its shape line.
/// </summary>
public sealed class FormatVersionsLedgerLawTests {
    private const string CodecPath = "src/Puck.Demo/DemoCodec.cs";
    private const string PartialPath = "src/Puck.Demo/DemoCodec.Body.cs";

    private static Dictionary<string, string> Sources(string key = "0x354445464B435550UL", string body = "return 1;") => new(comparer: StringComparer.Ordinal) {
        [CodecPath] = $$"""
            namespace Puck.Demo;
            public static partial class DemoCodec {
                public const ulong WireKey = {{key}}; // "PUCKFED5"
                public const int MaxVersion = 9;
                public const string SchemaVersion = "puck.demo.document.v2";
                public static ReadOnlySpan<byte> Magic => "DEMO"u8;
            }
            """,
        [PartialPath] = $$"""
            namespace Puck.Demo;
            public static partial class DemoCodec {
                public static int Encode() { {{body}} }
            }
            """,
        ["src/Puck.Demo.Post/Stage.cs"] = """
            namespace Puck.Demo.Post;
            public static class Stage { private const uint Magic = 0x68736D53; }
            """,
    };
    private static FormatEntry Entry(IReadOnlyList<FormatEntry> entries, string id) => entries.Single(predicate: entry => (entry.Id == id));
    private static string Records(Dictionary<string, string> sources) => FormatVersionsLedger.Render(entries: FormatVersionsLedger.Discover(files: sources));
    private static IReadOnlyList<string> Check(Dictionary<string, string> recordedFrom, Dictionary<string, string> current) {
        var text = Records(sources: recordedFrom);

        Assert.True(
            condition: FormatVersionsLedger.TryParse(
                entries: out var recorded,
                error: out var error,
                json: text
            ),
            userMessage: error
        );

        return FormatVersionsLedger.Check(
            current: FormatVersionsLedger.Discover(files: current),
            recorded: recorded,
            recordedText: text
        );
    }

    [Fact]
    public void DiscoveryReadsTheTokensTheSourceDeclaresAndNothingElse() {
        var entries = FormatVersionsLedger.Discover(files: Sources());

        Assert.Equal(
            expected: ["DemoCodec.Magic", "DemoCodec.SchemaVersion", "DemoCodec.WireKey"],
            actual: entries.Select(selector: static entry => entry.Id)
        );
        Assert.Equal(
            expected: "PUCKFED5",
            actual: Entry(
                entries: entries,
                id: "DemoCodec.WireKey"
            ).Token
        );
        Assert.Equal(
            expected: "DEMO",
            actual: Entry(
                entries: entries,
                id: "DemoCodec.Magic"
            ).Token
        );
        Assert.Equal(
            expected: "puck.demo.document.v2",
            actual: Entry(
                entries: entries,
                id: "DemoCodec.SchemaVersion"
            ).Token
        );
        Assert.NotNull(@object: Entry(
            entries: entries,
            id: "DemoCodec.SchemaVersion"
        ).Shape);
        Assert.NotNull(@object: Entry(
            entries: entries,
            id: "DemoCodec.WireKey"
        ).Shape);
        Assert.All(
            action: static entry => Assert.Equal(
                actual: entry.Source,
                expected: CodecPath
            ),
            collection: entries
        );
    }
    [Fact]
    public void ARecordedLedgerRoundTripsThroughItsOneSpelling() {
        var text = Records(sources: Sources());

        Assert.True(
            condition: FormatVersionsLedger.TryParse(
                entries: out var parsed,
                error: out var error,
                json: text
            ),
            userMessage: error
        );
        Assert.Equal(
            actual: FormatVersionsLedger.Discover(files: Sources()),
            expected: parsed
        );
        Assert.Equal(
            actual: text,
            expected: FormatVersionsLedger.Render(entries: parsed)
        );
        Assert.Empty(collection: Check(
            current: Sources(),
            recordedFrom: Sources()
        ));
    }
    [Fact]
    public void ACommentOrBlankLineNeverMovesAShapeButACodeChangeInAPartialSiblingDoes() {
        var baseline = Records(sources: Sources());
        var commented = Sources();

        commented[PartialPath] = ("// a note\n\n" + commented[PartialPath]);

        Assert.Equal(
            actual: baseline,
            expected: Records(sources: commented)
        );
        Assert.NotEqual(
            actual: baseline,
            expected: Records(sources: Sources(body: "return 2;"))
        );
    }
    [Fact]
    public void AChangedTokenIsDriftNamingItsOldAndNewTokenAndNeverDemandsABump() {
        var problems = Check(
            current: Sources(key: "0x364445464B435550UL"),
            recordedFrom: Sources()
        );

        Assert.Contains(
            collection: problems,
            filter: static problem => (problem.StartsWith(comparisonType: StringComparison.Ordinal, value: "retokened: 'DemoCodec.WireKey'") && problem.Contains(comparisonType: StringComparison.Ordinal, value: "declares PUCKFED6 but the ledger records PUCKFED5"))
        );
        Assert.DoesNotContain(
            collection: problems,
            filter: static problem => problem.Contains(
                comparisonType: StringComparison.OrdinalIgnoreCase,
                value: "bump the token"
            )
        );
    }
    [Fact]
    public void ACodecChangeIsDriftNamingBothShapesAndNeverDemandsABump() {
        var recorded = Sources();
        var current = Sources(body: "return 2;");
        var problems = Check(
            current: current,
            recordedFrom: recorded
        );

        Assert.Equal(
            expected: ["reshaped: the shape of 'DemoCodec.Magic'", "reshaped: the shape of 'DemoCodec.SchemaVersion'", "reshaped: the shape of 'DemoCodec.WireKey'"],
            actual: problems.Select(selector: static problem => problem[..problem.IndexOf(
                comparisonType: StringComparison.Ordinal,
                value: " (src")]).Order(comparer: StringComparer.Ordinal)
        );

        var was = Entry(entries: FormatVersionsLedger.Discover(files: recorded), id: "DemoCodec.WireKey").Shape;
        var now = Entry(entries: FormatVersionsLedger.Discover(files: current), id: "DemoCodec.WireKey").Shape;

        Assert.Contains(
            collection: problems,
            filter: problem => (problem.Contains(comparisonType: StringComparison.Ordinal, value: $"is now {now} and the ledger records {was}") && problem.Contains(comparisonType: StringComparison.Ordinal, value: "no token bump is owed"))
        );
        Assert.DoesNotContain(
            collection: problems,
            filter: static problem => problem.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "bump the token"
            )
        );
    }
    [Fact]
    public void AnUnrecordedStaleOrMovedFormatIsDrift() {
        var added = Sources();

        added["src/Puck.Demo/Other.cs"] = "namespace Puck.Demo; public static class Other { public const int FormatVersion = 3; }";

        Assert.Contains(
            collection: Check(
                current: added,
                recordedFrom: Sources()
            ),
            filter: static problem => problem.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "unrecorded: 'Other.FormatVersion'"
            )
        );
        Assert.Contains(
            collection: Check(
                current: Sources(),
                recordedFrom: added
            ),
            filter: static problem => problem.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "stale: 'Other.FormatVersion'"
            )
        );

        var moved = Sources();

        moved["src/Puck.Demo/Elsewhere.cs"] = moved[CodecPath];
        moved.Remove(key: CodecPath);

        Assert.Contains(
            collection: Check(
                current: moved,
                recordedFrom: Sources()
            ),
            filter: static problem => (problem.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "moved:"
            ) || problem.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "reshaped:"
            ))
        );
    }
    [Fact]
    public void ALedgerWhoseBytesDifferFromTheWriterIsDriftEvenWhenEveryFormatHolds() {
        var text = Records(sources: Sources());

        Assert.True(condition: FormatVersionsLedger.TryParse(
            entries: out var recorded,
            error: out _,
            json: text
        ));
        Assert.Contains(
            collection: FormatVersionsLedger.Check(
                current: recorded,
                recorded: recorded,
                recordedText: text.Replace(
                    newValue: "\"token\":  \"",
                    oldValue: "\"token\": \""
                )
            ),
            filter: static problem => problem.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "not canonical"
            )
        );
    }
    [Fact]
    public void TheShippedLedgerIsExactlyWhatTheShippedSourceDeclares() {
        Assert.True(condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot));

        var current = FormatVersionsLedger.Discover(files: FormatsCommand.ReadSources(repositoryRoot: repositoryRoot));
        var text = File.ReadAllText(path: Path.Combine(
            path1: repositoryRoot,
            path2: FormatVersionsLedger.FileName
        ));

        Assert.True(
            condition: FormatVersionsLedger.TryParse(
                entries: out var recorded,
                error: out var error,
                json: text
            ),
            userMessage: error
        );
        Assert.Empty(collection: FormatVersionsLedger.Check(
            current: current,
            recorded: recorded,
            recordedText: text
        ));

        foreach (var id in new[] { "SdfBaker.Version", "WorldAuthorityCheckpointCodec.SupportedVersion", "WorldFederationCodec.WireKey", "WorldProtocol.WireProtocolKey", "PeerWireProtocol.ProtocolKey", "WorldReplaySnapshot.ShapeToken", "LocalEndpointCapability.Revision", "RatchetLedger.Format" }) {
            Assert.Contains(
                collection: current,
                filter: entry => (entry.Id == id)
            );
        }
    }
    [InlineData("Document.cs", "int Value", "long Value")]
    [InlineData("Model.cs", "int Value", "long Value")]
    [InlineData("Model.cs", "int Value", "int Renamed")]
    [Theory]
    public void ADocumentFieldChangeIsDriftUntilItsShapeIsRecorded(string file, string before, string after) {
        var sources = new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
            ["src/Puck.Demo/Document.cs"] = "public sealed class Document { public const string Schema = \"puck.demo.v1\"; public int Value { get; init; } public Model Child { get; init; } }",
            ["src/Puck.Demo/Model.cs"] = "public sealed record Model(int Value);",
        };
        var changed = new Dictionary<string, string>(sources, StringComparer.Ordinal);

        changed[$"src/Puck.Demo/{file}"] = changed[$"src/Puck.Demo/{file}"].Replace(newValue: after, oldValue: before);
        Assert.Contains(collection: Check(current: changed, recordedFrom: sources), filter: problem => problem.StartsWith(comparisonType: StringComparison.Ordinal, value: "reshaped:"));
    }
    [Fact]
    public void ConcurrentDocumentEditsWithDifferentFieldsConflictOnTheShapeLine() {
        var sources = new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
            ["src/Puck.Demo/Document.cs"] = "public sealed record Document(int Value) { public const string Schema = \"puck.demo.v1\"; }",
        };
        var ours = new Dictionary<string, string>(sources, StringComparer.Ordinal);
        var theirs = new Dictionary<string, string>(sources, StringComparer.Ordinal);

        ours["src/Puck.Demo/Document.cs"] = sources["src/Puck.Demo/Document.cs"].Replace(newValue: ".v2", oldValue: ".v1").Replace(newValue: "long Value", oldValue: "int Value");
        theirs["src/Puck.Demo/Document.cs"] = sources["src/Puck.Demo/Document.cs"].Replace(newValue: ".v2", oldValue: ".v1").Replace(newValue: "string Value", oldValue: "int Value");
        var (conflicts, _) = LedgerMergeProbe.Merge(baseText: Records(sources: sources), ours: Records(sources: ours), theirs: Records(sources: theirs));
        Assert.True(condition: (conflicts > 0));
    }
    [Fact]
    public void AVersionShapedObjectIdentityIsNotAFormatToken() {
        var sources = Sources();

        sources["src/Puck.Demo/Profile.cs"] = "public static class Profile { public static object Default { get; } = new Model(\"puck.cost.portable-model.v1\"); }";
        sources["src/Puck.Demo/Document.cs"] = "public static class Document { public const string CurrentSchema = (\"puck.demo.v1\"); }";
        Assert.DoesNotContain(collection: FormatVersionsLedger.Discover(files: sources), filter: entry => (entry.Id == "Profile.Default"));
        Assert.Contains(collection: FormatVersionsLedger.Discover(files: sources), filter: entry => (entry.Id == "Document.CurrentSchema"));
    }
    [InlineData("Puck.HumbleGamingBrick", "MachineSnapshot", "MachineIdentity")]
    [InlineData("Puck.AdvancedGamingBrick", "AgbMachineSnapshot", "AgbMachineIdentity")]
    [InlineData("Puck.HumbleGamingDeck", "HgdMachineSnapshot", "HgdMachineIdentity")]
    [Theory]
    public void ASnapshotLayoutChangeMovesItsIdentityDigest(string project, string file, string identity) {
        var sources = new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
            [$"src/{project}/{file}.cs"] = $"public static class {identity} {{ public const int CurrentVersion = 1; }}",
            [$"src/{project}/Device.cs"] = "public static class Device { public static int SaveState() => 1; }",
        };
        var changed = new Dictionary<string, string>(sources, StringComparer.Ordinal);

        changed[$"src/{project}/Device.cs"] = "public static class Device { public static int SaveState() => 2; }";
        Assert.Contains(collection: Check(current: changed, recordedFrom: sources), filter: problem => problem.StartsWith(comparisonType: StringComparison.Ordinal, value: $"reshaped: the shape of '{identity}.CurrentVersion'"));
    }
    [Fact]
    public void FormattingAndLocalRenamesPreserveTheShape() {
        var original = Sources(body: "int value = 1; return Combine(value, 2 + 3);");

        original[PartialPath] += "\npublic static partial class DemoCodec { private static int Combine(int z, int a) => z + a; private static int Next() => 1; }";
        var formatted = new Dictionary<string, string>(original, StringComparer.Ordinal);

        formatted[PartialPath] = formatted[PartialPath].Replace(newValue: "Combine(a: (2 + 3), z: value)", oldValue: "Combine(value, 2 + 3)");
        Assert.Equal(expected: Records(sources: original), actual: Records(sources: formatted));
        var nullEquality = Sources(body: "string value = null; return value == null ? 1 : 2;");
        var nullPattern = Sources(body: "string value = null; return value is null ? 1 : 2;");

        Assert.Equal(expected: Records(sources: nullEquality), actual: Records(sources: nullPattern));
        formatted[PartialPath] = formatted[PartialPath].Replace(newValue: "renamed", oldValue: "value");
        Assert.Equal(expected: Records(sources: original), actual: Records(sources: formatted));
    }
    [Fact]
    public void GroupingArgumentBindingAndEvaluationOrderRemainPartOfTheShape() {
        Assert.NotEqual(expected: Records(sources: Sources(body: "return '\\uD800';")), actual: Records(sources: Sources(body: "return '\\uD801';")));
        Assert.NotEqual(expected: Records(sources: Sources(body: "var value = 1; return nameof(value).Length;")),
            actual: Records(sources: Sources(body: "var renamed = 1; return nameof(renamed).Length;")));
        Assert.NotEqual(expected: Records(sources: Sources(body: "return (1 + 2) * 3;")), actual: Records(sources: Sources(body: "return 1 + (2 * 3);")));
        var original = Sources(body: "return Combine(z: 1, a: 2);");

        original[PartialPath] += "\npublic static partial class DemoCodec { private static int Combine(int z, int a) => z + a; private static int Next() => 1; }";
        var changed = new Dictionary<string, string>(original, StringComparer.Ordinal);

        changed[PartialPath] = changed[PartialPath].Replace(newValue: "a: 1, z: 2", oldValue: "z: 1, a: 2");
        Assert.NotEqual(expected: Records(sources: original), actual: Records(sources: changed));
        original[PartialPath] = original[PartialPath].Replace(newValue: "z: Next(), a: Next()", oldValue: "z: 1, a: 2");
        changed[PartialPath] = original[PartialPath].Replace(newValue: "a: Next(), z: Next()", oldValue: "z: Next(), a: Next()");
        Assert.NotEqual(expected: Records(sources: original), actual: Records(sources: changed));
    }
    [Fact]
    public void AWorldPayloadChangeMovesTheWireContractAndReplayDigests() {
        var sources = new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
            ["src/Puck.World.Protocol/Protocol/WorldProtocol.cs"] = "public static class WorldProtocol { public const ulong WireProtocolKey = 1234; }",
            ["src/Puck.World.Protocol/Protocol/WorldWireCodec.cs"] = "public static class WorldWireCodec { public static int Encode() => 1; }",
            ["src/Puck.World.Server/WorldReplaySnapshot.cs"] = "public static class WorldReplaySnapshot { public const uint ShapeToken = 4; }",
        };
        var changed = new Dictionary<string, string>(sources, StringComparer.Ordinal);

        changed["src/Puck.World.Protocol/Protocol/WorldWireCodec.cs"] = changed["src/Puck.World.Protocol/Protocol/WorldWireCodec.cs"].Replace(newValue: "=> 2", oldValue: "=> 1");
        var problems = Check(current: changed, recordedFrom: sources);

        Assert.Contains(collection: problems, filter: problem => problem.StartsWith(comparisonType: StringComparison.Ordinal, value: "reshaped: the shape of 'WorldProtocol.WireProtocolKey'"));
        Assert.Contains(collection: problems, filter: problem => problem.StartsWith(comparisonType: StringComparison.Ordinal, value: "reshaped: the shape of 'WorldReplaySnapshot.ShapeToken'"));
    }
    [Fact]
    public void TheStrictExtensionConfigurationTokenIsDiscovered() {
        Assert.True(condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot));
        Assert.Contains(collection: FormatVersionsLedger.Discover(files: FormatsCommand.ReadSources(repositoryRoot: repositoryRoot)),
            filter: entry => ((entry.Id == "WorldExtensionConfiguration.CurrentSchema") && (entry.Token == "puck.world.extensions.v1")));
    }
    [Fact]
    public void TwoBranchesThatEditOneCodecDifferentlyCollideInTheLedgerWithNoTokenInvolved() {
        var baseText = Records(sources: Sources());
        var ours = Records(sources: Sources(body: "return 2;"));
        var theirs = Records(sources: Sources(body: "return 3;"));

        var (conflicts, merged) = LedgerMergeProbe.Merge(
            baseText: baseText,
            ours: ours,
            theirs: theirs
        );

        Assert.True(condition: (conflicts > 0));
        Assert.Contains(
            actualString: merged,
            expectedSubstring: "<<<<<<<"
        );
        Assert.Equal(
            actual: ours.Split(separator: '\n').Count(predicate: static line => line.Contains(value: "\"token\"")),
            expected: baseText.Split(separator: '\n').Count(predicate: static line => line.Contains(value: "\"token\""))
        );
    }
    // Every format's fingerprint is generated, once, into the namespace of the file that declares it: a codec reads the
    // constant unqualified, in one spelling, and the constant is the ledger's own digest.
    [Fact]
    public void EachFormatsShapeIsGeneratedIntoTheNamespaceThatDeclaresItInOneSpelling() {
        var sources = Sources();

        sources["src/Puck.Demo/Other.cs"] = "namespace Puck.Demo.Wire;\npublic static class Other { public const int FormatVersion = 3; }";
        sources["src/Puck.Other/Elsewhere.cs"] = "namespace Puck.Other;\npublic static class Elsewhere { public const int FormatVersion = 7; }";

        var entries = FormatVersionsLedger.Discover(files: sources);
        var plan = FormatShapesFiles.Plan(
            entries: entries,
            projectOf: source => (source[..(source.LastIndexOf(value: '/') + 1)], System.Text.RegularExpressions.Regex.Match(input: sources[source], pattern: @"^namespace\s+([A-Za-z0-9_.]+)\s*;", options: System.Text.RegularExpressions.RegexOptions.Multiline).Groups[1].Value)
        );

        Assert.Equal(
            expected: ["src/Puck.Demo/FormatShapes.g.cs", "src/Puck.Other/FormatShapes.g.cs"],
            actual: plan.Keys
        );

        var demo = plan["src/Puck.Demo/FormatShapes.g.cs"];

        Assert.Contains(actualString: demo, expectedSubstring: "namespace Puck.Demo {");
        Assert.Contains(actualString: demo, expectedSubstring: "namespace Puck.Demo.Wire {");
        Assert.Contains(actualString: demo, expectedSubstring: "// <auto-generated/>");

        foreach (var entry in entries.Where(predicate: static entry => !entry.Source.Contains(value: "Puck.Other"))) {
            Assert.Contains(
                actualString: demo,
                expectedSubstring: $"public const string {FormatShapesFiles.ConstantOf(id: entry.Id)} = \"{entry.Shape}\";"
            );
        }

        Assert.Equal(
            expected: "DemoCodecWireKey",
            actual: FormatShapesFiles.ConstantOf(id: "DemoCodec.WireKey")
        );
    }
    [Fact]
    public void TwoFormatsOfOneNamespaceThatSpellOneConstantAreRefused() {
        var entries = new[] {
            new FormatEntry(Id: "A.BC", Shape: "0000000000000001", Source: "src/Puck.Demo/A.cs", Token: "1"),
            new FormatEntry(Id: "AB.C", Shape: "0000000000000002", Source: "src/Puck.Demo/B.cs", Token: "1"),
        };

        var refusal = Assert.Throws<InvalidOperationException>(testCode: () => FormatShapesFiles.Plan(
            entries: entries,
            projectOf: static _ => ("src/Puck.Demo/", "Puck.Demo")
        ));

        Assert.Contains(actualString: refusal.Message, expectedSubstring: "ABC");
    }
    [Fact]
    public void AMissingStaleOrExtraShapeFileIsDriftAndAnIdenticalSetHolds() {
        var plan = new Dictionary<string, string>(comparer: StringComparer.Ordinal) { ["src/Puck.Demo/FormatShapes.g.cs"] = "text" };

        Assert.Empty(collection: FormatShapesFiles.Check(existing: plan, plan: plan));
        Assert.Contains(
            collection: FormatShapesFiles.Check(existing: new Dictionary<string, string>(), plan: plan),
            filter: static problem => problem.StartsWith(comparisonType: StringComparison.Ordinal, value: "unrecorded: src/Puck.Demo/FormatShapes.g.cs")
        );
        Assert.Contains(
            collection: FormatShapesFiles.Check(existing: new Dictionary<string, string> { ["src/Puck.Demo/FormatShapes.g.cs"] = "other" }, plan: plan),
            filter: static problem => problem.StartsWith(comparisonType: StringComparison.Ordinal, value: "stale: src/Puck.Demo/FormatShapes.g.cs differs")
        );
        Assert.Contains(
            collection: FormatShapesFiles.Check(existing: new Dictionary<string, string> { ["src/Puck.Demo/FormatShapes.g.cs"] = "text", ["src/Puck.Gone/FormatShapes.g.cs"] = "x" }, plan: plan),
            filter: static problem => problem.StartsWith(comparisonType: StringComparison.Ordinal, value: "stale: src/Puck.Gone/FormatShapes.g.cs")
        );
    }
    [Fact]
    public void TheShippedShapeFilesAreExactlyWhatTheShippedLedgerPlans() {
        Assert.True(condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot));

        var sources = FormatsCommand.ReadSources(repositoryRoot: repositoryRoot);
        var plan = FormatsCommand.ShapeFiles(
            entries: FormatVersionsLedger.Discover(files: sources),
            repositoryRoot: repositoryRoot,
            sources: sources
        );

        Assert.NotEmpty(collection: plan);

        foreach (var (path, text) in plan) {
            Assert.Equal(
                actual: File.ReadAllText(path: Path.Combine(path1: repositoryRoot, path2: path)),
                expected: text
            );
        }
    }
}
