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
                public static int Encode(byte[] bytes) { {{body}} }
            }
            """,
        ["src/Puck.Demo.Post/Stage.cs"] = """
            namespace Puck.Demo.Post;
            public static class Stage { private const uint Magic = 0x68736D53; }
            """,
    };

    // The shipped source is read and closed once for every law that judges it; closing it is the slow step.
    private static readonly Lazy<(string Root, Dictionary<string, string> Sources, IReadOnlyList<FormatEntry> Entries)> Shipped = new(valueFactory: static () => {
        Assert.True(condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot));

        var sources = FormatsCommand.ReadSources(repositoryRoot: repositoryRoot);

        return (repositoryRoot, sources, FormatVersionsLedger.Discover(files: sources));
    });

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
        var (repositoryRoot, _, current) = Shipped.Value;
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

        foreach (var id in new[] { "WorldAuthorityCheckpointCodec.SupportedVersion", "WorldFederationCodec.WireKey", "WorldProtocol.WireProtocolKey", "PeerWireProtocol.ProtocolKey", "WorldReplaySnapshot.ShapeToken", "LocalEndpointCapability.Revision", "RatchetLedger.Format" }) {
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
            [$"src/{project}/{file}.cs"] = $"public static class {identity} {{ public const int CurrentVersion = 1; public static int Save() => Device.SaveState(); }}",
            [$"src/{project}/Device.cs"] = "[FormatLeaf] public static class Device { public static int SaveState() => 1; }",
        };
        var changed = new Dictionary<string, string>(sources, StringComparer.Ordinal);

        changed[$"src/{project}/Device.cs"] = changed[$"src/{project}/Device.cs"].Replace(newValue: "=> 2", oldValue: "=> 1");
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
            ["src/Puck.World.Protocol/Protocol/WorldProtocol.cs"] = "public static class WorldProtocol { public const ulong WireProtocolKey = 1234; public static int Encode() => WorldWireCodec.Encode(); }",
            ["src/Puck.World.Protocol/Protocol/WorldWireCodec.cs"] = "[FormatLeaf] public static class WorldWireCodec { public static int Encode() => 1; }",
            ["src/Puck.World.Server/WorldReplaySnapshot.cs"] = "public static class WorldReplaySnapshot { public const uint ShapeToken = 4; public static int Encode() => WorldWireCodec.Encode(); }",
        };
        var changed = new Dictionary<string, string>(sources, StringComparer.Ordinal);

        changed["src/Puck.World.Protocol/Protocol/WorldWireCodec.cs"] = changed["src/Puck.World.Protocol/Protocol/WorldWireCodec.cs"].Replace(newValue: "=> 2", oldValue: "=> 1");
        var problems = Check(current: changed, recordedFrom: sources);

        Assert.Contains(collection: problems, filter: problem => problem.StartsWith(comparisonType: StringComparison.Ordinal, value: "reshaped: the shape of 'WorldProtocol.WireProtocolKey'"));
        Assert.Contains(collection: problems, filter: problem => problem.StartsWith(comparisonType: StringComparison.Ordinal, value: "reshaped: the shape of 'WorldReplaySnapshot.ShapeToken'"));
    }
    [Fact]
    public void TheStrictExtensionConfigurationTokenIsDiscovered() {
        Assert.Contains(collection: Shipped.Value.Entries,
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
            new FormatEntry(Id: "A.BC", Open: [], Shape: "0000000000000001", Source: "src/Puck.Demo/A.cs", Token: "1"),
            new FormatEntry(Id: "AB.C", Open: [], Shape: "0000000000000002", Source: "src/Puck.Demo/B.cs", Token: "1"),
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
        var (repositoryRoot, sources, entries) = Shipped.Value;
        var plan = FormatsCommand.ShapeFiles(
            entries: entries,
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

    // The boundary: what a codec's shape covers beyond its own files.
    private static Dictionary<string, string> Boundary(string leaf = "var a = reader.ReadString(); var b = reader.ReadString(); return a + b;", string order = "A, B", string unmarked = "return 1;", string seam = "[FormatSeam(\"its behaviour sets no byte because it only counts\")]") => new(comparer: StringComparer.Ordinal) {
        ["src/Puck.Demo/Wire.cs"] = $$"""
            namespace Puck.Demo;
            public static class Wire {
                public const int FormatVersion = 3;
                public static string Read(Reader reader) => Leaves.ReadPair(reader) + (Family)reader.ReadByte() + Helper.Normalize() + Counter.Count();
                public static byte Write(Family family) => (byte)family;
            }
            """,
        ["src/Puck.Demo/Leaves.cs"] = $$"""
            namespace Puck.Demo;
            [FormatLeaf]
            public static class Leaves {
                public static string ReadPair(Reader reader) { {{leaf}} }
                public static string Unrelated() => "x";
            }
            """,
        ["src/Puck.Demo/Family.cs"] = $$"""
            namespace Puck.Demo;
            public enum Family : byte { {{order}} }
            """,
        ["src/Puck.Demo/Helper.cs"] = $$"""
            namespace Puck.Demo;
            public static class Helper { public static int Normalize() { {{unmarked}} } }
            public static class Uncalled { public static int Elsewhere() => 7; }
            {{seam}}
            public static class Counter { public static int Count() => 1; }
            """,
        ["src/Puck.Demo/Reader.cs"] = "namespace Puck.Demo; [FormatLeaf] public sealed class Reader { public string ReadString() => \"\"; public byte ReadByte() => 0; }",
    };
    private static string ShapeOf(Dictionary<string, string> sources) => Entry(entries: FormatVersionsLedger.Discover(files: sources), id: "Wire.FormatVersion").Shape;

    [Fact]
    public void AMarkedHelperTheCodecCallsMovesTheShapeWhenItsReadOrderChanges() {
        var swapped = Boundary(leaf: "var b = reader.ReadString(); var a = reader.ReadString(); return a + b;");

        Assert.NotEqual(expected: ShapeOf(sources: Boundary()), actual: ShapeOf(sources: swapped));
    }
    [Fact]
    public void AnEnumTheCodecCastsMovesTheShapeWhenItsMembersReorder() {
        Assert.NotEqual(expected: ShapeOf(sources: Boundary()), actual: ShapeOf(sources: Boundary(order: "B, A")));
    }
    [Fact]
    public void AMemberNothingReachableCallsNeverMovesTheShape() {
        var sources = Boundary();
        var edited = Boundary();

        edited["src/Puck.Demo/Helper.cs"] = edited["src/Puck.Demo/Helper.cs"].Replace(newValue: "=> 8", oldValue: "=> 7");
        edited["src/Puck.Demo/Leaves.cs"] = edited["src/Puck.Demo/Leaves.cs"].Replace(newValue: "=> \"y\"", oldValue: "=> \"x\"");
        Assert.Equal(expected: ShapeOf(sources: sources), actual: ShapeOf(sources: edited));
    }
    [Fact]
    public void AnUnmarkedCallIsOpenNotCoveredAndDriftUntilRecorded() {
        var sources = Boundary();
        var entry = Entry(entries: FormatVersionsLedger.Discover(files: sources), id: "Wire.FormatVersion");

        Assert.Equal(expected: ["M:Puck.Demo.Helper.Normalize"], actual: entry.Open);
        Assert.Equal(expected: entry.Shape, actual: ShapeOf(sources: Boundary(unmarked: "return 2;")));

        var unrecorded = entry with { Open = [] };
        var problems = FormatVersionsLedger.Check(current: [entry], recorded: [unrecorded], recordedText: FormatVersionsLedger.Render(entries: [unrecorded]));

        Assert.Single(collection: problems);
        Assert.StartsWith(expectedStartString: "open: 'Wire.FormatVersion'", actualString: problems[0]);
        Assert.Contains(actualString: problems[0], expectedSubstring: "M:Puck.Demo.Helper.Normalize");
        Assert.Contains(actualString: problems[0], expectedSubstring: "[FormatLeaf]");
        Assert.DoesNotContain(actualString: problems[0], expectedSubstring: "refused");
        Assert.Empty(collection: FormatVersionsLedger.Check(current: [entry], recorded: [entry], recordedText: FormatVersionsLedger.Render(entries: [entry])));

        var tighter = FormatVersionsLedger.Check(current: [unrecorded], recorded: [entry], recordedText: FormatVersionsLedger.Render(entries: [entry]));

        Assert.Single(collection: tighter);
        Assert.Contains(actualString: tighter[0], expectedSubstring: "no longer reaches 1 it records (M:Puck.Demo.Helper.Normalize)");
    }
    [Fact]
    public void AMarkedCalleeIsCoveredNotOpenAndASeamIsNeitherAndNeedsAReason() {
        var marked = Boundary(unmarked: "return Leaves.Unrelated().Length;");
        var entry = Entry(entries: FormatVersionsLedger.Discover(files: marked), id: "Wire.FormatVersion");

        Assert.Equal(expected: ["M:Puck.Demo.Helper.Normalize"], actual: entry.Open);
        Assert.DoesNotContain(collection: entry.Open, filter: static call => call.Contains(comparisonType: StringComparison.Ordinal, value: "Counter"));

        var edited = Boundary();

        edited["src/Puck.Demo/Helper.cs"] = edited["src/Puck.Demo/Helper.cs"].Replace(newValue: "Count() => 2", oldValue: "Count() => 1");
        Assert.Equal(expected: ShapeOf(sources: Boundary()), actual: ShapeOf(sources: edited));

        var refusal = Assert.Throws<FormatBoundaryException>(testCode: () => FormatVersionsLedger.Discover(files: Boundary(seam: "[FormatSeam(\"\")]")));

        Assert.Contains(actualString: refusal.Message, expectedSubstring: "seam without a reason");
        Assert.Throws<FormatBoundaryException>(testCode: () => FormatVersionsLedger.Discover(files: Boundary(seam: "[FormatSeam]")));
    }
    [Fact]
    public void TheLedgerRecordsOpenCallsInOneSpellingAndRoundTripsThem() {
        var entries = FormatVersionsLedger.Discover(files: Boundary());
        var text = FormatVersionsLedger.Render(entries: entries);

        Assert.Contains(actualString: text, expectedSubstring: "\"open\": [\n                \"M:Puck.Demo.Helper.Normalize\"\n            ],");
        Assert.True(condition: FormatVersionsLedger.TryParse(entries: out var parsed, error: out var error, json: text), userMessage: error);
        Assert.Equal(expected: text, actual: FormatVersionsLedger.Render(entries: parsed));
    }
    [Fact]
    public void TwoFormatsSharingATypeAndMemberGenerateConstantsThatCompile() {
        var sources = new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
            ["src/Puck.Demo/A.cs"] = "namespace Puck.Demo.A;\npublic static class Codec { public const int FormatVersion = 1; }",
            ["src/Puck.Demo/B.cs"] = "namespace Puck.Demo.B;\npublic static class Codec { public const int FormatVersion = 2; }",
        };
        var entries = FormatVersionsLedger.Discover(files: sources);

        Assert.Equal(expected: ["Codec.FormatVersion@src/Puck.Demo/A.cs", "Codec.FormatVersion@src/Puck.Demo/B.cs"], actual: entries.Select(selector: static entry => entry.Id));

        var plan = FormatShapesFiles.Plan(
            entries: entries,
            projectOf: source => ("src/Puck.Demo/", System.Text.RegularExpressions.Regex.Match(input: sources[source], pattern: @"^namespace\s+([A-Za-z0-9_.]+)\s*;", options: System.Text.RegularExpressions.RegexOptions.Multiline).Groups[1].Value)
        );
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
            assemblyName: "ShapesCompile",
            options: new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(outputKind: Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary),
            references: [Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(path: typeof(object).Assembly.Location)],
            syntaxTrees: [.. plan.Values.Select(selector: static text => Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(text: text))]
        );

        Assert.Empty(collection: compilation.GetDiagnostics(cancellationToken: TestContext.Current.CancellationToken).Where(predicate: static diagnostic => (diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)));
        Assert.DoesNotContain(actualString: string.Concat(values: plan.Values), expectedSubstring: "@");
        Assert.Contains(actualString: plan["src/Puck.Demo/FormatShapes.g.cs"], expectedSubstring: "namespace Puck.Demo.A {");
        Assert.Contains(actualString: plan["src/Puck.Demo/FormatShapes.g.cs"], expectedSubstring: "namespace Puck.Demo.B {");
    }
    // The shipped codecs: the shape of each pilot moves with the leaves its read and write paths call and the enums they cast.
    [Fact]
    public void TheShippedPilotsMoveWhenAWireLeafSwapsItsReadsOrACastEnumReorders() {
        var (_, sources, _) = Shipped.Value;
        var swapped = new Dictionary<string, string>(sources, StringComparer.Ordinal);
        var reordered = new Dictionary<string, string>(sources, StringComparer.Ordinal);
        const string Leaves = "src/Puck.World.Server/WorldWireLeaves.cs";
        const string Feed = "src/Puck.World.Server/WorldEventFeed.cs";
        const string Domain = "        var identityDomain = reader.ReadString(field: \"peer identity domain\");\n";
        const string Subject = "        var identitySubject = reader.ReadString(field: \"peer identity subject\");\n";

        Assert.Contains(actualString: sources[Leaves], expectedSubstring: (Domain + Subject));
        Assert.Contains(actualString: sources[Feed], expectedSubstring: "RegionEnter,\n    /// <summary>A body left a named region.</summary>\n    RegionExit,");

        swapped[Leaves] = sources[Leaves].Replace(newValue: (Subject + Domain), oldValue: (Domain + Subject));
        reordered[Feed] = sources[Feed].Replace(newValue: "RegionExit,\n    /// <summary>A body left a named region.</summary>\n    RegionEnter,", oldValue: "RegionEnter,\n    /// <summary>A body left a named region.</summary>\n    RegionExit,");

        foreach (var id in new[] { "WorldAuthorityCheckpointCodec.SupportedVersion", "WorldReplaySnapshot.ShapeToken" }) {
            var shape = Shipped.Value.Entries.Single(predicate: entry => (entry.Id == id)).Shape;

            Assert.NotEqual(expected: shape, actual: FormatVersionsLedger.Explain(files: swapped, id: id)!.Value.Entry.Shape);
        }

        Assert.NotEqual(
            expected: Shipped.Value.Entries.Single(predicate: static entry => (entry.Id == "WorldAuthorityCheckpointCodec.SupportedVersion")).Shape,
            actual: FormatVersionsLedger.Explain(files: reordered, id: "WorldAuthorityCheckpointCodec.SupportedVersion")!.Value.Entry.Shape
        );
    }

    // The invariant: whatever the codec's read and write paths can reach is covered by the shape or recorded open. Each input
    // below once escaped both.
    private static Dictionary<string, string> Codec(string wire, params (string Path, string Text)[] files) {
        var sources = new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
            ["src/Puck.Demo/Wire.cs"] = $$"""
                namespace Puck.Demo;
                public static class Wire {
                    public const int FormatVersion = 3;
                    {{wire}}
                }
                """,
        };

        foreach (var (path, text) in files) { sources[$"src/Puck.Demo/{path}"] = ("namespace Puck.Demo;\n" + text); }

        return sources;
    }
    private static FormatEntry Format(Dictionary<string, string> sources) => Entry(entries: FormatVersionsLedger.Discover(files: sources), id: "Wire.FormatVersion");
    private static Dictionary<string, string> Edited(Dictionary<string, string> sources, string path, string from, string to) {
        var edited = new Dictionary<string, string>(sources, StringComparer.Ordinal);

        Assert.Contains(actualString: edited[$"src/Puck.Demo/{path}"], expectedSubstring: from);
        edited[$"src/Puck.Demo/{path}"] = edited[$"src/Puck.Demo/{path}"].Replace(newValue: to, oldValue: from);

        return edited;
    }

    [InlineData("")]
    [InlineData("[FormatLeaf]")]
    [Theory]
    public void ADispatchThroughABodylessPropertyIsCoveredWhenMarkedAndOpenWhenNot(string mark) {
        var sources = Codec(
            wire: "public static int Read(byte[] bytes, Slot slot) => slot.Width;",
            ("Slot.cs", $"{mark} public abstract class Slot {{ public abstract int Width {{ get; }} }}"),
            ("Impl.cs", "public sealed class Impl : Slot { public override int Width => 1; }")
        );
        var entry = Format(sources: sources);

        if (mark.Length == 0) {
            Assert.Contains(collection: entry.Open, filter: static call => call.StartsWith(comparisonType: StringComparison.Ordinal, value: "P:Puck.Demo.Slot.Width"));
        } else {
            Assert.Empty(collection: entry.Open);
            Assert.NotEqual(expected: entry.Shape, actual: Format(sources: Edited(from: "=> 1", path: "Impl.cs", sources: sources, to: "=> 2")).Shape);
        }
    }
    [Fact]
    public void AStaticAbstractInterfaceSlotReachesItsImplementationsThroughAGenericCodec() {
        var sources = Codec(
            wire: "public static byte[] Encode<T>(byte[] bytes) where T : IWidth => bytes[..T.Size()];",
            ("IWidth.cs", "[FormatLeaf] public interface IWidth { static abstract int Size(); }"),
            ("Shape.cs", "public sealed class Shape : IWidth { public static int Size() => 1; }")
        );
        var entry = Format(sources: sources);

        Assert.Empty(collection: entry.Open);
        Assert.NotEqual(expected: entry.Shape, actual: Format(sources: Edited(from: "=> 1", path: "Shape.cs", sources: sources, to: "=> 2")).Shape);
    }
    [Fact]
    public void AConstantChainBehindACastEnumMovesTheShapeWhereverItEnds() {
        var sources = Codec(
            wire: "public static byte Write(byte[] bytes, Tag tag) => (byte)tag;",
            ("Tag.cs", "public enum Tag : byte { Record = Sizes.Record }"),
            ("Sizes.cs", "public static class Sizes { public const int Record = Limits.Record; }"),
            ("Limits.cs", "public static class Limits { public const int Record = 1; }")
        );

        Assert.NotEqual(expected: Format(sources: sources).Shape, actual: Format(sources: Edited(from: "= 1", path: "Limits.cs", sources: sources, to: "= 2")).Shape);
    }
    [InlineData("")]
    [InlineData("[FormatLeaf]")]
    [Theory]
    public void ADelegateAStaticInitializerBindsIsCoveredWhenMarkedAndOpenWhenNot(string mark) {
        var sources = Codec(
            wire: "public static int Read(byte[] bytes) => Widths.Width();",
            ("Widths.cs", "public static class Widths { public static readonly System.Func<int> Width = Helpers.Width; }"),
            ("Helpers.cs", $"{mark} public static class Helpers {{ public static int Width() => 1; }}")
        );
        var entry = Format(sources: sources);

        if (mark.Length == 0) {
            Assert.Contains(collection: entry.Open, filter: static call => call.StartsWith(comparisonType: StringComparison.Ordinal, value: "M:Puck.Demo.Helpers.Width"));
        } else {
            Assert.Empty(collection: entry.Open);
            Assert.NotEqual(expected: entry.Shape, actual: Format(sources: Edited(from: "=> 1", path: "Helpers.cs", sources: sources, to: "=> 2")).Shape);
        }
    }
    [Fact]
    public void AForeachsImplicitEnumeratorCallsAreCoveredWhenTheCollectionIsMarked() {
        var sources = Codec(
            wire: "public static int Sum(Bytes bytes) { var total = 0; foreach (var value in bytes) { total += value; } return total; }",
            ("Bytes.cs", """
                [FormatLeaf]
                public sealed class Bytes {
                    public Enumerator GetEnumerator() => new();
                    public struct Enumerator {
                        private int m_index;
                        public bool MoveNext() => (m_index++ < 1);
                        public int Current => 0;
                    }
                }
                """)
        );
        var entry = Format(sources: sources);

        Assert.Empty(collection: entry.Open);
        Assert.NotEqual(expected: entry.Shape, actual: Format(sources: Edited(from: "< 1", path: "Bytes.cs", sources: sources, to: "< 2")).Shape);
    }
    [Fact]
    public void AForeachOverAnUnmarkedCollectionLeavesItsEnumeratorOpen() {
        var sources = Codec(
            wire: "public static int Sum(byte[] raw, Bytes bytes) { var total = 0; foreach (var value in bytes) { total += value; } return total; }",
            ("Bytes.cs", "public sealed class Bytes { public Enumerator GetEnumerator() => new(); public struct Enumerator { public bool MoveNext() => false; public int Current => 0; } }")
        );

        Assert.Contains(collection: Format(sources: sources).Open, filter: static call => call.StartsWith(comparisonType: StringComparison.Ordinal, value: "M:Puck.Demo.Bytes.Enumerator.MoveNext"));
    }
    [Fact]
    public void ACodecFilesEngineDrivingMembersAreNeitherItsShapeNorOpenUntilAnEncodingMemberCallsThem() {
        var sources = Codec(
            wire: """
                public static int Encode(byte[] bytes) => Helper(bytes.Length);
                private static int Helper(int length) => (length + 1);
                public static void Apply(Engine engine) { engine.Advance(); }
                """,
            ("Engine.cs", "public sealed class Engine { public void Advance() { } }")
        );
        var entry = Format(sources: sources);

        Assert.Empty(collection: entry.Open);
        // The same-file helper an encoding member calls is the codec's shape.
        Assert.NotEqual(expected: entry.Shape, actual: Format(sources: Edited(from: "(length + 1)", path: "Wire.cs", sources: sources, to: "(length + 2)")).Shape);
        // The member that only drives the engine is neither covered nor open.
        Assert.Equal(expected: entry.Shape, actual: Format(sources: Edited(from: "engine.Advance();", path: "Wire.cs", sources: sources, to: "engine.Advance(); engine.Advance();")).Shape);
    }
    [Fact]
    public void ADuplicateKeyLedgerIsRefusedAndTheWriterNeverWritesOne() {
        var entries = FormatVersionsLedger.Discover(files: Boundary());
        var text = FormatVersionsLedger.Render(entries: entries);
        var open = "            \"open\": [\n                \"M:Puck.Demo.Helper.Normalize\"\n            ],\n";

        Assert.Equal(expected: 1, actual: (text.Split(separator: "\"open\"").Length - 1));
        Assert.True(condition: FormatVersionsLedger.TryParse(entries: out _, error: out var error, json: text), userMessage: error);

        var doubled = text.Replace(newValue: (open + open), oldValue: open);

        Assert.NotEqual(actual: doubled, expected: text);
        Assert.False(condition: FormatVersionsLedger.TryParse(entries: out _, error: out var refusal, json: doubled));
        Assert.Contains(actualString: refusal, comparisonType: StringComparison.OrdinalIgnoreCase, expectedSubstring: "open");
    }
    [InlineData("Wire.FormatVersion", true)]
    [InlineData("Other.FormatVersion", false)]
    [Theory]
    public void ACodecDeclaredPartOfAFormatMovesItsShapeThoughNoCodeOfTheFormatCallsIt(string format, bool moves) {
        var sources = Codec(
            wire: "public static int Version() => 3;",
            ("Payload.cs", $$"""
                [FormatPart("{{format}}")]
                public static class Payload {
                    public static int Write(byte[] bytes) => (bytes.Length + 1);
                }
                """)
        );
        var shape = Format(sources: sources).Shape;
        var edited = Format(sources: Edited(from: "+ 1", path: "Payload.cs", sources: sources, to: "+ 2")).Shape;

        Assert.Equal(actual: (shape != edited), expected: moves);
    }
}
