using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Puck.HumbleGamingDeck.Post;

/// <summary>
/// Runs every AccuracyCoin test the way a player does, with Start on the first page's header, and reads each test's
/// result byte from RAM page 4 once the run finishes. AccuracyCoin writes 1, or a variant number shifted left two with
/// bit 0 set, for a pass, and an error code shifted left two with bit 1 set for a failure; its test table in ROM names
/// each result address, which the report uses. $FF means skipped. The ROM targets the RP2A03G and RP2C02G.
/// <para>
/// The stage is a ratchet: <c>AccuracyCoinExpectations.json</c> in the checkout records every test's outcome, and
/// any test whose outcome differs, better or worse, fails the stage. A run writes its outcomes to
/// <c>accuracy-coin.candidate.json</c> under the artifacts directory; a fix that turns a failure into a pass is landed
/// by copying that candidate over the recorded ledger in the same change.
/// </para>
/// https://github.com/100thCoin/AccuracyCoin
/// </summary>
internal sealed class AccuracyCoinStage : IPostStage<PostContext> {
    private const int FrameLimit = 60_000;
    private const ulong FrameTicks = 357_366UL;
    private const ushort TestTally = 0x0037;
    // The pinned ROM installs JMP PressStartToContinue at $0700 only after drawing the complete result table.
    private const ushort ResultMenuNmi = 0x9308;

    /// <inheritdoc/>
    public string Name => "accuracy-coin";
    /// <inheritdoc/>
    public PostTier Tier => PostTier.B;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var root = context.ResolveAccuracyCoinRoot();

        if (root is null) {
            return PostStageOutcome.Infra(detail: "no accuracy-coin corpus; use --fetch-corpora or --accuracy-coin");
        }

        var imagePath = Path.Combine(path1: root, path2: "AccuracyCoin.nes");

        if (!File.Exists(path: imagePath)) {
            return PostStageOutcome.Infra(detail: "incomplete accuracy-coin corpus: requires AccuracyCoin.nes");
        }

        var image = File.ReadAllBytes(path: imagePath);
        var names = TestNames(image: image);
        var expected = LoadExpectations();
        using var instance = HgdMachineFactory.Create(configuration: new HgdMachineConfiguration(
            cartridge: HgdCartridge.Load(image: image)));
        var machine = instance.Machine;

        machine.RunCycles(masterTicks: (60 * FrameTicks));
        machine.Controllers.SetButtons(buttons: HgdButtons.Start, port: 0);
        machine.RunCycles(masterTicks: (4 * FrameTicks));
        machine.Controllers.SetButtons(buttons: HgdButtons.None, port: 0);

        // An idle started-test count also describes a hung test. Require the result menu's NMI handler and full tally.
        // $35, the ROM's own running flag, is not a reliable end marker because some tests overwrite it.
        var frames = 0;
        var tally = machine.Bus.Peek(address: TestTally);

        for (; (frames < FrameLimit); ++frames) {
            machine.RunCycles(masterTicks: FrameTicks);

            tally = machine.Bus.Peek(address: TestTally);

            if ((tally == names.Count) &&
                (machine.Bus.Peek(address: 0x0700) == 0x4C) &&
                (machine.Bus.Peek(address: 0x0701) == (ResultMenuNmi & 0xFF)) &&
                (machine.Bus.Peek(address: 0x0702) == (ResultMenuNmi >> 8))) {
                break;
            }
        }
        if (tally == 0) {
            return PostStageOutcome.Fail(detail: "Start on the menu header never began the all-test run");
        }
        if (frames == FrameLimit) {
            return PostStageOutcome.Fail(detail: $"the all-test run had not finished after {FrameLimit} frames");
        }

        var outcomes = new SortedDictionary<string, string>(comparer: StringComparer.Ordinal);
        var cases = new List<PostCaseResult>(capacity: names.Count);
        var changes = new StringBuilder();
        var passed = 0;

        foreach (var (address, name) in names.OrderBy(keySelector: static pair => pair.Key)) {
            var result = machine.Bus.Peek(address: address);
            var outcome = ((result & 3) switch {
                1 => (((result >> 2) == 0) ? "PASS" : $"PASS variant {(result >> 2)}"),
                2 => $"FAIL {(result >> 2)}",
                _ => ((result == 0xFF) ? "SKIP" : $"NOT RUN ${result:X2}"),
            });
            var recorded = expected.GetValueOrDefault(key: name);
            var matches = string.Equals(a: recorded, b: outcome, comparisonType: StringComparison.Ordinal);

            outcomes[name] = outcome;
            if (outcome.StartsWith(comparisonType: StringComparison.Ordinal, value: "PASS")) {
                ++passed;
            }
            if (!matches) {
                _ = changes.Append(provider: CultureInfo.InvariantCulture, handler: $"{name}: {(recorded ?? "unrecorded")} -> {outcome}; ");
            }
            cases.Add(item: new(Detail: $"${address:X4}: {outcome}", Duration: TimeSpan.Zero,
                Name: name, Verdict: (!matches ? PostCaseVerdict.Mismatch
                    : (((result & 3) == 1) ? PostCaseVerdict.Pass : PostCaseVerdict.ExpectedFail))));
        }
        foreach (var name in expected.Keys.Where(predicate: name => !outcomes.ContainsKey(key: name))) {
            _ = changes.Append(provider: CultureInfo.InvariantCulture, handler: $"{name}: recorded but absent; ");
            cases.Add(item: new(Detail: "recorded but absent from the ROM test table", Duration: TimeSpan.Zero,
                Name: name, Verdict: PostCaseVerdict.Mismatch));
        }
        _ = Directory.CreateDirectory(path: context.ArtifactsDirectory);
        File.WriteAllText(
            contents: (JsonSerializer.Serialize(
                jsonTypeInfo: AccuracyCoinJsonContext.Default.DictionaryStringString,
                value: new Dictionary<string, string>(collection: outcomes)
            ) + "\n"),
            path: Path.Combine(path1: context.ArtifactsDirectory, path2: "accuracy-coin.candidate.json")
        );

        var summary = $"{passed} of {names.Count} tests pass after {frames} frames";

        return ((changes.Length == 0)
            ? PostStageOutcome.Pass(cases: cases, detail: $"{summary}; every outcome matches the recorded ledger")
            : PostStageOutcome.Fail(cases: cases, detail: $"{summary}; outcomes differ from the recorded ledger: {changes}"));
    }

    private static Dictionary<string, string> LoadExpectations() {
        var path = RepositoryPaths.Resolve(relativePath: "src/Puck.HumbleGamingDeck.Post/AccuracyCoinExpectations.json");

        return (JsonSerializer.Deserialize(json: File.ReadAllText(path: path), jsonTypeInfo: AccuracyCoinJsonContext.Default.DictionaryStringString)
            ?? throw new InvalidDataException(message: "AccuracyCoinExpectations.json must contain an outcome ledger."));
    }
    // Each entry of AccuracyCoin's test table is the test's name, an $FF terminator, the little-endian result address on
    // page 4, and the test's entry point. DRAW entries keep their results on page 3 and are not tests.
    private static Dictionary<ushort, string> TestNames(ReadOnlySpan<byte> image) {
        var names = new Dictionary<ushort, string>();

        for (var index = 4; (index < (image.Length - 4)); ++index) {
            if ((image[index] != 0xFF) || (image[(index + 2)] != 0x04) || (image[(index + 4)] < 0x80)) {
                continue;
            }

            var start = index;

            while ((start > 0) && (image[(start - 1)] >= 0x20) && (image[(start - 1)] < 0x7F)) {
                --start;
            }
            if (((index - start) < 3) || !image[start..index].ToArray().Any(predicate: static value => char.IsAsciiLetter(c: ((char)value)))) {
                continue;
            }

            var address = ((ushort)(0x0400 | image[(index + 1)]));

            names.TryAdd(key: address, value: Encoding.ASCII.GetString(bytes: image[start..index]));
        }

        return names;
    }
}
/// <summary>The source-generated serializer for the AccuracyCoin outcome ledger.</summary>
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class AccuracyCoinJsonContext : JsonSerializerContext;
