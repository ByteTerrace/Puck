using Puck.Cli.Formats;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// The shipped <c>FormatVersions.json</c> and the generated <c>FormatShapes.g.cs</c> files equal what the shipped source
/// declares. A format's shape compiles the sources against the assemblies its host process trusts, and the puck tool
/// records the ledger, so these laws run here, in the suite whose host loads the composed tool's assemblies; the rest of
/// the ledger's laws live in <c>Puck.Cli.Format.Tests</c>.
/// </summary>
public sealed class ShippedFormatLedgerLawTests {
    // The shipped source is read and closed once for both laws; closing it is the slow step.
    private static readonly Lazy<(string Root, Dictionary<string, string> Sources, IReadOnlyList<FormatEntry> Entries)> Shipped = new(valueFactory: static () => {
        Assert.True(condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot));

        var sources = FormatsCommand.ReadSources(repositoryRoot: repositoryRoot);

        return (repositoryRoot, sources, FormatVersionsLedger.Discover(files: sources));
    });

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
}
