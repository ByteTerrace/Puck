using Puck.Cli.Formats;

using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// Laws for <c>FormatVersions.json</c> and <c>puck formats</c> (<see cref="FormatVersionsLedger"/>): discovery reads
/// the tokens the source declares; recording round-trips; a bumped, reshaped, unrecorded, stale, or moved format is
/// drift; the shipped ledger equals the shipped source; and two branches that bump one format to the same new token
/// with different codecs collide in the ledger even though their token lines merge cleanly.
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
        Assert.Null(@object: Entry(
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
    public void ABumpedFormatIsDriftNamingItsOldAndNewToken() {
        var problems = Check(
            current: Sources(key: "0x364445464B435550UL"),
            recordedFrom: Sources()
        );

        Assert.Contains(
            collection: problems,
            filter: static problem => (problem.StartsWith(comparisonType: StringComparison.Ordinal, value: "bumped: 'DemoCodec.WireKey'") && problem.Contains(comparisonType: StringComparison.Ordinal, value: "declares PUCKFED6 but the ledger records PUCKFED5"))
        );
    }
    [Fact]
    public void ACodecChangedWithoutABumpIsDriftAskingWhetherTheTokenShouldMove() {
        var problems = Check(
            current: Sources(body: "return 2;"),
            recordedFrom: Sources()
        );

        Assert.Equal(
            expected: ["reshaped: the source of 'DemoCodec.Magic'", "reshaped: the source of 'DemoCodec.WireKey'"],
            actual: problems.Select(selector: static problem => problem[..problem.IndexOf(
                comparisonType: StringComparison.Ordinal,
                value: " (src")]).Order(comparer: StringComparer.Ordinal)
        );
        Assert.Contains(
            collection: problems,
            filter: static problem => problem.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "if the encoding changed, bump the token"
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
    [Fact]
    public void TwoBranchesThatBumpOneFormatToTheSameTokenWithDifferentCodecsCollideInTheLedger() {
        var baseText = Records(sources: Sources());
        var ours = Records(sources: Sources(
            body: "return 2;",
            key: "0x364445464B435550UL"
        ));
        var theirs = Records(sources: Sources(
            body: "return 3;",
            key: "0x364445464B435550UL"
        ));

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

        // The token lines alone would have merged: both branches wrote PUCKFED6.
        var (tokenOnly, _) = LedgerMergeProbe.Merge(
            baseText: baseText,
            ours: baseText.Replace(
                newValue: "PUCKFED6",
                oldValue: "PUCKFED5"
            ),
            theirs: baseText.Replace(
                newValue: "PUCKFED6",
                oldValue: "PUCKFED5"
            )
        );

        Assert.Equal(
            actual: tokenOnly,
            expected: 0
        );
    }
}
