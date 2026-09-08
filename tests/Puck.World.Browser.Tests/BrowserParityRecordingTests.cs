using System.Text;
using System.Text.Json;

using Xunit;

namespace Puck.World.Browser.Tests;

/// <summary>The determinism canary's native half: computes <see cref="StateFrameHash"/> over a fixed document, tick,
/// and write sequence, and compares it against the recorded baseline in <c>Fixtures/browser-parity/expected.json</c>
/// — the same file the Node harness (<c>engine-wasm.test.cjs</c>) reads to prove the wasm build folds the identical
/// bytes. Set <c>PUCK_BROWSER_PARITY_RECORD=1</c> to overwrite the baseline with freshly computed hashes instead of
/// comparing against them.</summary>
/// <remarks>Only <c>games/tictactoe.world.json</c> composes standalone under <c>standard.basis.json</c> among the
/// fragments this suite sampled (bowling, billiards, poker, chess, dominoes, freecell, hexlines, klondike, mancala
/// all refuse — each names a host register, a look, or a body motion program the island's own body supplies, never
/// the bare basis alone); the two fixtures below are two independent scripted-write cases over that one document
/// rather than two different fragments, and <c>puck.world.json</c> itself refuses standalone (its <c>modules/
/// arcade.world.json</c> import authors real gaming-brick screens this engine cannot register — see
/// <see cref="Puck.World.Browser.Engine.BrowserExtensionVocabulary"/>).</remarks>
public sealed class BrowserParityRecordingTests {
    private static string RepositoryRoot() {
        var directory = new DirectoryInfo(path: AppContext.BaseDirectory);

        while ((directory is not null) && !File.Exists(path: Path.Combine(path1: directory.FullName, path2: "Puck.slnx"))) {
            directory = directory.Parent;
        }

        Assert.NotNull(@object: directory);

        return directory!.FullName;
    }
    private static string ExpectedPath() => Path.Combine(RepositoryRoot(), "tests", "Puck.World.Browser.Tests", "Fixtures", "browser-parity", "expected.json");
    private static bool RecordMode => (Environment.GetEnvironmentVariable(variable: "PUCK_BROWSER_PARITY_RECORD") == "1");

    private static byte[] ComposedTicTacToeBytes() {
        var basisBytes = File.ReadAllBytes(path: Path.Combine(RepositoryRoot(), "src", "Puck.World", "Assets", "worlds", "standard.basis.json"));
        var fragmentBytes = File.ReadAllBytes(path: Path.Combine(RepositoryRoot(), "src", "Puck.World", "Assets", "worlds", "games", "tictactoe.world.json"));

        Assert.True(condition: WorldDefinitionFileSource.TryComposeFragmentBytes(alias: "a", composed: out var composed, fragmentBytes: fragmentBytes, hostBytes: basisBytes, reason: out var reason), userMessage: reason);

        return Encoding.UTF8.GetBytes(s: composed!.ToJsonString());
    }
    private static BrowserSession NewSession() {
        var errors = new List<string>();
        var deferred = new List<string>();

        Assert.True(condition: BrowserParser.TryParseAndValidate(utf8Json: ComposedTicTacToeBytes(), errors: errors, deferred: deferred, definition: out var definition, compilation: out var compilation), userMessage: string.Join(separator: "; ", values: errors));

        return new BrowserSession(definition: definition!, compilation: compilation!);
    }
    // The fixed scripted sequence each fixture name reproduces exactly — a name change here moves the hash it
    // records, on purpose: the two cases are named apart so the Node harness runs the identical steps and compares
    // against the same file.
    private static ulong RunFixture(string name) {
        var session = NewSession();

        switch (name) {
            case "tictactoe-write-then-judge": {
                var row = session.Definition.State.First(predicate: candidate => (candidate.Kind == CellKind.Int) && !candidate.IsKeyed && (candidate.Field is null));

                Assert.True(condition: session.TryWriteRow(row: row.Name.Value, key: StateRow.SlotKey.Value, value: 1L, add: false, reason: out var reason), userMessage: reason);

                session.Judge(tick: 1UL);

                return session.StateHash();
            }
            case "tictactoe-judge-twice": {
                session.Judge(tick: 1UL);
                session.Judge(tick: 2UL);

                return session.StateHash();
            }
            default:
                throw new ArgumentOutOfRangeException(paramName: nameof(name), actualValue: name, message: "no scripted sequence carries this name.");
        }
    }

    [Theory]
    [InlineData("tictactoe-write-then-judge")]
    [InlineData("tictactoe-judge-twice")]
    public void StateHash_matches_the_recorded_baseline(string fixture) {
        var hash = RunFixture(name: fixture).ToString(provider: System.Globalization.CultureInfo.InvariantCulture);
        var path = ExpectedPath();
        var recorded = (File.Exists(path: path)
            ? (JsonSerializer.Deserialize<Dictionary<string, string>>(json: File.ReadAllText(path: path)) ?? [])
            : []);

        if (RecordMode) {
            recorded[fixture] = hash;

            Directory.CreateDirectory(path: Path.GetDirectoryName(path: path)!);
            File.WriteAllText(path: path, contents: (JsonSerializer.Serialize(value: recorded, options: new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine));

            return;
        }

        Assert.True(condition: recorded.TryGetValue(key: fixture, value: out var expected), userMessage: $"no recorded baseline for '{fixture}' in {path} — run with PUCK_BROWSER_PARITY_RECORD=1 to record it.");
        Assert.Equal(expected: expected, actual: hash);
    }
}
