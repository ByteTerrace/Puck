using System.Text;

using Xunit;

namespace Puck.World.Browser.Tests;

/// <summary>Exercises the pure <c>Engine/</c> core directly (linked as source — see the project's own remarks on why
/// a browser-wasm exe cannot be referenced), over real shipped documents: <c>standard.basis.json</c> composed with
/// <c>games/tictactoe.world.json</c> as the primary "does the whole pipeline run" fixture, and the flagship
/// <c>puck.world.json</c> to pin the one verified, honest scope boundary this engine has today (a
/// <c>screens[].source.machine</c> engine key never resolves to a real catalog, so its registration is DEFERRED
/// rather than refused or silently admitted — see <see cref="BrowserExtensionVocabulary"/>'s own remarks; nothing
/// here carries an emulator core).</summary>
public sealed class BrowserEngineTests {
    private static string RepositoryRoot() {
        var directory = new DirectoryInfo(path: AppContext.BaseDirectory);

        while ((directory is not null) && !File.Exists(path: Path.Combine(path1: directory.FullName, path2: "Puck.slnx"))) {
            directory = directory.Parent;
        }

        Assert.NotNull(@object: directory);

        return directory!.FullName;
    }
    private static byte[] ComposedPuckWorldBytes() {
        var path = Path.Combine(RepositoryRoot(), "src", "Puck.World", "Assets", "worlds", "puck.world.json");

        Assert.True(condition: WorldDefinitionFileSource.TryComposeDocumentTree(path: path, tree: out var tree, reason: out var reason), userMessage: reason);

        return Encoding.UTF8.GetBytes(s: tree!.ToJsonString());
    }
    // standard.basis.json composed with the tictactoe fragment — a real shipped document pair with no
    // screens[].source.machine engine to trip the extension-vocabulary boundary ComposedPuckWorldBytes hits.
    private static byte[] ComposedTicTacToeBytes() {
        var basisBytes = File.ReadAllBytes(path: Path.Combine(RepositoryRoot(), "src", "Puck.World", "Assets", "worlds", "standard.basis.json"));
        var fragmentBytes = File.ReadAllBytes(path: Path.Combine(RepositoryRoot(), "src", "Puck.World", "Assets", "worlds", "games", "tictactoe.world.json"));

        Assert.True(condition: WorldDefinitionFileSource.TryComposeFragmentBytes(alias: "a", composed: out var composed, fragmentBytes: fragmentBytes, hostBytes: basisBytes, reason: out var reason), userMessage: reason);

        return Encoding.UTF8.GetBytes(s: composed!.ToJsonString());
    }

    [Fact]
    public void Parse_composed_tictactoe_fragment_succeeds() {
        var result = BrowserParser.Parse(utf8Json: ComposedTicTacToeBytes());

        Assert.True(condition: result.Ok, userMessage: string.Join(separator: "; ", values: (result.Errors ?? []).Select(selector: error => error.Message)));
        Assert.NotNull(@object: result.Document);
        Assert.Null(@object: result.Errors);
    }
    [Fact]
    public void Parse_composed_puck_world_defers_its_unregistered_machine_engines() {
        // The verified, honest boundary: Puck.World.Browser carries no screen-machine engine catalog at all, so a
        // document whose screens author real gaming-brick engines DEFERS the answer (never refuses, never silently
        // passes) rather than either booting a slot nothing can run or rejecting a document a cataloged host admits.
        var result = BrowserParser.Parse(utf8Json: ComposedPuckWorldBytes());

        Assert.True(condition: result.Ok, userMessage: string.Join(separator: "; ", values: (result.Errors ?? []).Select(selector: error => error.Message)));
        Assert.NotNull(@object: result.Document);
        Assert.Contains(collection: result.Deferred!, filter: message => message.Contains(value: "screen-machine engine 'gaming-brick' registration deferred", comparisonType: StringComparison.Ordinal));
        Assert.Contains(collection: result.Deferred!, filter: message => message.Contains(value: "screen-machine engine 'advanced-gaming-brick' registration deferred", comparisonType: StringComparison.Ordinal));
    }
    [Fact]
    public void Parse_wrong_schema_refuses_by_name() {
        var result = BrowserParser.Parse(utf8Json: Encoding.UTF8.GetBytes(s: /*lang=json*/ """{"schema":"not.a.real.schema"}"""));

        Assert.False(condition: result.Ok);
        Assert.Null(@object: result.Document);
        Assert.Contains(collection: result.Errors!, filter: error => error.Message.Contains(value: "not.a.real.schema", comparisonType: StringComparison.Ordinal));
    }
    [Fact]
    public void Parse_malformed_json_refuses_without_throwing() {
        var result = BrowserParser.Parse(utf8Json: Encoding.UTF8.GetBytes(s: "{ not json"));

        Assert.False(condition: result.Ok);
        Assert.NotEmpty(collection: result.Errors!);
    }
    [Fact]
    public void Canonicalize_reproduces_serialization_byte_for_byte() {
        var bytes = ComposedTicTacToeBytes();
        var parsed = BrowserParser.Parse(utf8Json: bytes);
        var canonicalized = BrowserParser.Canonicalize(utf8Json: bytes);

        Assert.True(condition: parsed.Ok);
        Assert.True(condition: canonicalized.Ok);
        Assert.Equal(expected: parsed.Document, actual: canonicalized.Document);

        // Re-parsing the canonical text must reproduce the identical canonical text — the ouroboros round-trip
        // WorldDefinitionSerialization's own remarks describe, reached through the browser's own pipeline.
        var reparsed = BrowserParser.Parse(utf8Json: Encoding.UTF8.GetBytes(s: canonicalized.Document!));

        Assert.True(condition: reparsed.Ok);
        Assert.Equal(expected: canonicalized.Document, actual: reparsed.Document);
    }

    [Fact]
    public void Compile_and_judge_one_tick_reports_no_refusals() {
        var errors = new List<string>();
        var deferred = new List<string>();

        Assert.True(
            condition: BrowserParser.TryParseAndValidate(utf8Json: ComposedTicTacToeBytes(), errors: errors, deferred: deferred, definition: out var definition, compilation: out var compilation),
            userMessage: string.Join(separator: "; ", values: errors)
        );

        var session = new BrowserSession(definition: definition!, compilation: compilation!);
        var trace = session.Judge(tick: 1UL);

        Assert.Empty(collection: trace.Refusals);
        Assert.NotNull(@object: trace.Rules);
        Assert.NotNull(@object: trace.Writes);
    }
    [Fact]
    public void ReadRow_and_WriteRow_round_trip_through_the_frame() {
        var errors = new List<string>();
        var deferred = new List<string>();

        Assert.True(condition: BrowserParser.TryParseAndValidate(utf8Json: ComposedTicTacToeBytes(), errors: errors, deferred: deferred, definition: out var definition, compilation: out var compilation));

        var session = new BrowserSession(definition: definition!, compilation: compilation!);
        var scalarRow = definition!.State.FirstOrDefault(predicate: row => (row.Kind == CellKind.Int) && !row.IsKeyed && (row.Field is null));

        Assert.NotNull(@object: scalarRow);

        var before = session.ReadRow(row: scalarRow!.Name.Value, key: StateRow.SlotKey.Value);

        Assert.True(condition: session.TryWriteRow(row: scalarRow.Name.Value, key: StateRow.SlotKey.Value, value: (before.Value + 1), add: false, reason: out var reason), userMessage: reason);

        var after = session.ReadRow(row: scalarRow.Name.Value, key: StateRow.SlotKey.Value);

        Assert.Equal(expected: (before.Value + 1), actual: after.Value);
    }
    [Fact]
    public void StateHash_is_stable_across_repeated_reads() {
        var errors = new List<string>();
        var deferred = new List<string>();

        Assert.True(condition: BrowserParser.TryParseAndValidate(utf8Json: ComposedTicTacToeBytes(), errors: errors, deferred: deferred, definition: out var definition, compilation: out var compilation));

        var session = new BrowserSession(definition: definition!, compilation: compilation!);
        var first = session.StateHash();
        var second = session.StateHash();

        Assert.Equal(expected: first, actual: second);
    }
    [Fact]
    public void Rows_reports_every_authored_cell() {
        var errors = new List<string>();
        var deferred = new List<string>();

        Assert.True(condition: BrowserParser.TryParseAndValidate(utf8Json: ComposedTicTacToeBytes(), errors: errors, deferred: deferred, definition: out var definition, compilation: out var compilation));

        var session = new BrowserSession(definition: definition!, compilation: compilation!);
        var rows = session.Rows();

        Assert.Equal(expected: definition!.State.Count, actual: rows.Count);
    }

    [Fact]
    public void ParseFragment_composes_a_game_module_under_the_standard_basis() {
        var basisPath = Path.Combine(RepositoryRoot(), "src", "Puck.World", "Assets", "worlds", "standard.basis.json");
        var fragmentPath = Path.Combine(RepositoryRoot(), "src", "Puck.World", "Assets", "worlds", "games", "tictactoe.world.json");
        var hostBytes = File.ReadAllBytes(path: basisPath);
        var fragmentBytes = File.ReadAllBytes(path: fragmentPath);

        var result = BrowserParser.ParseFragment(fragmentUtf8: fragmentBytes, hostUtf8: hostBytes, alias: "a");

        Assert.True(condition: result.Ok, userMessage: string.Join(separator: "; ", values: (result.Errors ?? []).Select(selector: error => error.Message)));
        Assert.DoesNotContain(expectedSubstring: "a_", actualString: (result.Errors ?? []).Select(selector: error => error.Message).FirstOrDefault() ?? "");
    }

    [Theory]
    [InlineData("bodies.localSeats -1 is outside 0..4 (the host's seat ceiling).", "bodies.localSeats")]
    [InlineData("an addon requires a name.", null)]
    [InlineData("screens[0].index 5 is duplicated.", "screens[0].index")]
    public void BrowserErrorPaths_splits_the_leading_path_token_when_present(string message, string? expectedPath) {
        var split = BrowserErrorPaths.Split(message: message);

        Assert.Equal(expected: expectedPath, actual: split.Path);
    }

    [Fact]
    public void Cells_compiles_a_standalone_grid_topology() {
        const string TopologyJson = /*lang=json*/ """
            { "$type": "grid", "name": "g", "width": 4, "depth": 4, "cellSize": 1, "band": 0.3, "origin": [0, 0, 0] }
            """;

        var ok = BrowserTopology.TryCells(topologyJson: TopologyJson, cells: out var cells, reason: out var reason);

        Assert.True(condition: ok, userMessage: reason);
        Assert.Equal(expected: 16, actual: cells.Count);
        Assert.Equal(expected: "0", actual: cells[0].Key);
    }
}
