using System.Diagnostics;
using System.Text;

using Xunit;

namespace Puck.World.Browser.Tests;

/// <summary>Native (JIT, not the wasm interpreter) Stopwatch measurements of the same calls
/// <c>src/Puck.Dashboard/src/portal/tests/engine-timing.test.cjs</c> times under Node against the AppBundle — over
/// the identical shipped island (<c>puck.world.json</c> + <c>standard.basis.json</c> + all 16 imports). Splits
/// <see cref="BrowserComposer.ComposeTree"/> from <see cref="BrowserParser.TryParseAndValidate"/> so a reader can
/// see how much of the wasm cost is composition (the <c>WorldDefinitionFileSource</c>/<c>WorldDocumentBasis</c>
/// <c>JsonNode</c> merge, in <c>Puck.World.Schema</c> — outside this project's own boundary) versus validation/rule
/// compilation (also <c>Puck.World.Schema</c>/<c>Puck.State</c>) versus this project's own <see cref="BrowserSession"/>
/// construction. Prints via <see cref="ITestOutputHelper"/>; asserts only a generous ceiling (native JIT code has no
/// business taking multiple seconds for this island) — never a tight budget a healthy machine could miss.</summary>
public sealed class EngineTimingTests(ITestOutputHelper output) {
    private static string RepositoryRoot() {
        var directory = new DirectoryInfo(path: AppContext.BaseDirectory);

        while ((directory is not null) && !File.Exists(path: Path.Combine(path1: directory.FullName, path2: "Puck.slnx"))) {
            directory = directory.Parent;
        }

        Assert.NotNull(@object: directory);

        return directory!.FullName;
    }

    private static Dictionary<string, byte[]> WorldsDirectoryDocuments() {
        var root = Path.Combine(RepositoryRoot(), "src", "Puck.World", "Assets", "worlds");
        var result = new Dictionary<string, byte[]>(comparer: StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(path: root, searchPattern: "*.json", searchOption: SearchOption.AllDirectories)) {
            var relative = Path.GetRelativePath(relativeTo: root, path: file).Replace(oldChar: '\\', newChar: '/');

            result[relative] = File.ReadAllBytes(path: file);
        }

        return result;
    }

    [Fact]
    public void Native_Parse_ComposeTree_and_Compile_over_the_shipped_island_finish_in_well_under_a_second_each() {
        var documents = WorldsDirectoryDocuments();
        var rows = new List<(string Label, TimeSpan Elapsed)>();

        TimeSpan Time(string label, Action action) {
            var stopwatch = Stopwatch.StartNew();

            action();
            stopwatch.Stop();
            rows.Add(item: (label, stopwatch.Elapsed));

            return stopwatch.Elapsed;
        }

        // Parse: tictactoe under standard.basis.json, matching the wasm harness's own first timed call.
        var basisBytes = documents["standard.basis.json"];
        var fragmentBytes = documents["games/tictactoe.world.json"];

        BrowserParseResult parsed = null!;

        Time(label: "Parse (tictactoe fragment under standard.basis.json)", action: () => parsed = BrowserParser.ParseFragment(fragmentUtf8: fragmentBytes, hostUtf8: basisBytes, alias: "a"));
        Assert.True(condition: parsed.Ok, userMessage: string.Join(separator: "; ", values: parsed.Errors?.Select(selector: e => e.Message) ?? []));

        // ComposeTree: the whole island — BrowserComposer's own tree merge (WorldDefinitionFileSource, Puck.World.Schema)
        // plus one TryParseAndValidate pass over the merged ~428 KB result.
        BrowserComposeResult composed = null!;

        Time(label: "ComposeTree (full island: puck.world.json + basis + 16 imports)", action: () => composed = BrowserComposer.ComposeTree(rootName: "puck.world.json", documents: documents, editedName: null, editedUtf8: null));
        Assert.True(condition: composed.Ok, userMessage: string.Join(separator: "; ", values: composed.Errors?.Select(selector: e => e.Message) ?? []));

        var composedUtf8 = Encoding.UTF8.GetBytes(s: composed.Composed!);

        // TryParseAndValidate alone, over the same composed bytes ComposeTree already produced — isolates the
        // parse/migrate/draw-resolve/validate pass (a second, independent run of the same pipeline ComposeTree's
        // own TryParseAndValidate call already paid once) from the JsonNode tree-merge phase above.
        List<string> reparseErrors = [];
        List<string> reparseDeferred = [];

        Time(label: "  -> TryParseAndValidate alone, over the already-composed island", action: () => {
            var ok = BrowserParser.TryParseAndValidate(utf8Json: composedUtf8, errors: reparseErrors, deferred: reparseDeferred, definition: out _, compilation: out _);

            Assert.True(condition: ok, userMessage: string.Join(separator: "; ", values: reparseErrors));
        });

        // Compile: BrowserExports.Compile's own core — TryParseAndValidate over the composed island a THIRD time
        // (once inside ComposeTree, once standalone above, once here) plus this project's own BrowserSession
        // construction (FrameLayout, CompiledPatterns.TryCompileAll, FrameHost, LoadRows/DerivedBoards.Compose).
        List<string> compileErrors = [];
        List<string> compileDeferred = [];

        Time(label: "Compile (composed island: TryParseAndValidate + BrowserSession construction)", action: () => {
            var ok = BrowserParser.TryParseAndValidate(utf8Json: composedUtf8, errors: compileErrors, deferred: compileDeferred, definition: out var definition, compilation: out var compilation);

            Assert.True(condition: ok, userMessage: string.Join(separator: "; ", values: compileErrors));
            Assert.NotNull(@object: new BrowserSession(definition: definition!, compilation: compilation!));
        });

        // A second pass over the identical bytes, in the same process — separates one-time JIT/static-init cost
        // (tier-0 compilation of WorldDefinitionValidator/WorldDocumentBasis/RuleEvaluator's own call graph, first
        // touch of source-generated JsonSerializer contexts) from steady-state per-call cost, the way a browser tab
        // that stays open across several edits experiences it.
        var warmRows = new List<(string Label, TimeSpan Elapsed)>();

        TimeSpan TimeWarm(string label, Action action) {
            var stopwatch = Stopwatch.StartNew();

            action();
            stopwatch.Stop();
            warmRows.Add(item: (label, stopwatch.Elapsed));

            return stopwatch.Elapsed;
        }

        TimeWarm(label: "Parse (tictactoe fragment under standard.basis.json)", action: () => parsed = BrowserParser.ParseFragment(fragmentUtf8: fragmentBytes, hostUtf8: basisBytes, alias: "a"));
        TimeWarm(label: "ComposeTree (full island: puck.world.json + basis + 16 imports)", action: () => composed = BrowserComposer.ComposeTree(rootName: "puck.world.json", documents: documents, editedName: null, editedUtf8: null));
        TimeWarm(label: "Compile (composed island: TryParseAndValidate + BrowserSession construction)", action: () => {
            var ok = BrowserParser.TryParseAndValidate(utf8Json: composedUtf8, errors: [], deferred: [], definition: out var definition, compilation: out var compilation);

            Assert.True(condition: ok);
            Assert.NotNull(@object: new BrowserSession(definition: definition!, compilation: compilation!));
        });

        output.WriteLine(message: "");
        output.WriteLine(message: "--- native engine timing (Stopwatch, JIT, no wasm interpreter) — first pass in this process ---");

        foreach (var (label, elapsed) in rows) {
            output.WriteLine(message: $"{label,-70} {elapsed.TotalMilliseconds,8:F1} ms");
        }

        output.WriteLine(message: "--- second pass, same process, same bytes — isolates steady-state cost from JIT/static-init ---");

        foreach (var (label, elapsed) in warmRows) {
            output.WriteLine(message: $"{label,-70} {elapsed.TotalMilliseconds,8:F1} ms");
        }

        output.WriteLine(message: $"documents payload (ComposeTree input)                                  {documents.Sum(selector: static kv => kv.Value.Length):N0} bytes");
        output.WriteLine(message: $"composed island payload (ComposeTree output .composed)                 {composedUtf8.Length:N0} bytes");

        // A generous ceiling on the WARM pass only — first-call JIT/static-init cost is real but not what this
        // ceiling exists to catch (see the two-pass measurement above): native JIT code has no business taking
        // multiple seconds per call once its call graph is already tiered up, on any machine this repository builds
        // on — see this project's README's own "Performance" section for the wasm comparison.
        foreach (var (label, elapsed) in warmRows) {
            Assert.True(condition: elapsed < TimeSpan.FromSeconds(value: 3), userMessage: $"{label} took {elapsed.TotalMilliseconds:F1} ms natively on a warm second pass — investigate before blaming the wasm build alone.");
        }
    }
}
