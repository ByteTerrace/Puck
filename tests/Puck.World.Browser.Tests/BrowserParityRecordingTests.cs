using System.Text;
using System.Text.Json;

using Puck.Testing;
using Xunit;

namespace Puck.World.Browser.Tests;

/// <summary>The determinism canary's native half: computes <see cref="StateArena.ComputeHash"/> over a fixed
/// document, tick, and write sequence, and compares it against the recorded baseline in <c>Fixtures/browser-parity/expected.json</c>
/// — the same file the Node harness (<c>engine-wasm.test.cjs</c>) reads to prove the wasm build folds the identical
/// bytes. The test writes the freshly computed baseline beside its assembly (<see cref="TestRecords"/>) before comparing,
/// and <c>puck baselines browser-parity</c> promotes that copy over the committed one.</summary>
/// <remarks>Only <c>games/tictactoe.world.json</c> composes standalone under <c>standard.world.json</c> among the
/// fragments this suite sampled (bowling, billiards, poker, chess, dominoes, freecell, hexlines, klondike, mancala
/// all refuse — each names a host register, a look, or a body motion program the island's own body supplies, never
/// the bare basis alone); the two fixtures below are two independent scripted-write cases over that one document
/// rather than two different fragments, and <c>puck.world.json</c> itself refuses standalone (its <c>modules/
/// arcade.world.json</c> import authors real gaming-brick screens this engine cannot register — see
/// <see cref="Puck.World.Browser.Engine.BrowserExtensionVocabulary"/>).</remarks>
public sealed class BrowserParityRecordingTests {
    private static byte[] ComposedTicTacToeBytes() {
        var basisBytes = File.ReadAllBytes(path: RepositoryPaths.Resolve(relativePath: "src/Puck.World/Assets/worlds/standard.world.json"));
        var fragmentBytes = ShippedWorldDocuments.Read(path: RepositoryPaths.Resolve(relativePath: "src/Puck.World/Assets/worlds/games/tictactoe.puck"));

        Assert.True(
            condition: WorldDefinitionFileSource.TryComposeFragmentBytes(
                alias: "a",
                composed: out var composed,
                fragmentBytes: fragmentBytes,
                hostBytes: basisBytes,
                reason: out var reason
            ),
            userMessage: reason
        );

        return Encoding.UTF8.GetBytes(s: composed!.ToJsonString());
    }
    private static string ExpectedPath() => RepositoryPaths.Resolve(relativePath: "tests/Puck.World.Browser.Tests/Fixtures/browser-parity/expected.json");
    private static BrowserSession NewSession() {
        var errors = new List<string>();
        var deferred = new List<string>();

        Assert.True(
            condition: BrowserParser.TryParseAndValidate(
                utf8Json: ComposedTicTacToeBytes(),
                errors: errors,
                deferred: deferred,
                definition: out var definition
            ),
            userMessage: string.Join(
                separator: "; ",
                values: errors
            )
        );

        return new BrowserSession(definition: definition!);
    }
    // The fixed scripted sequence each fixture name reproduces exactly — a name change here moves the hash it
    // records, on purpose: the two cases are named apart so the Node harness runs the identical steps and compares
    // against the same file.
    private static ulong RunFixture(string name) {
        var session = NewSession();

        switch (name) {
            case "tictactoe-write-then-judge": {
                    var row = session.Definition.State.First(predicate: candidate => ((candidate.Kind == CellKind.Int) && !candidate.IsKeyed && (candidate.Field is null)));

                    Assert.True(
                        condition: session.TryWriteRow(
                            row: row.Name.Value,
                            key: StateRow.SlotKey.Value,
                            value: 1L,
                            add: false,
                            reason: out var reason
                        ),
                        userMessage: reason
                    );

                    session.Judge(tick: 1UL);

                    return session.StateHash();
                }
            case "tictactoe-judge-twice": {
                    session.Judge(tick: 1UL);
                    session.Judge(tick: 2UL);

                    return session.StateHash();
                }
            default:
                throw new ArgumentOutOfRangeException(
                    paramName: nameof(name),
                    actualValue: name,
                    message: "no scripted sequence carries this name."
                );
        }
    }

    // The scripted sequences, in the order the committed baseline lists them.
    private static readonly string[] FixtureNames = ["tictactoe-write-then-judge", "tictactoe-judge-twice"];

    [Fact]
    public void StateHashes_match_the_recorded_baseline() {
        var computed = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        foreach (var fixture in FixtureNames) {
            computed[fixture] = RunFixture(name: fixture).ToString(provider: System.Globalization.CultureInfo.InvariantCulture);
        }

        var path = ExpectedPath();
        var rendered = (JsonSerializer.Serialize(
            value: computed,
            options: new JsonSerializerOptions { WriteIndented = true }
        ).ReplaceLineEndings(replacementText: "\n") + "\n");

        _ = TestRecords.Write(
            artifact: "browser-parity",
            bytes: Encoding.UTF8.GetBytes(s: rendered),
            fileName: Path.GetFileName(path: path)
        );
        Assert.True(
            condition: File.Exists(path: path),
            userMessage: $"no recorded baseline at {path}; record it with puck baselines browser-parity."
        );
        Assert.Equal(
            actual: rendered,
            expected: File.ReadAllText(path: path).ReplaceLineEndings(replacementText: "\n")
        );
    }
}
