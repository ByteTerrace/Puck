using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Puck.HumbleGamingDeck.Post;

/// <summary>Compares every instruction vector with its complete register, memory, and bus trace.</summary>
internal sealed class Nes6502SstStage : IPostStage<PostContext> {
    /// <inheritdoc/>
    public string Name => "nes6502-sst";
    /// <inheritdoc/>
    public PostTier Tier => PostTier.B;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (context.SstRoot is null) {
            return PostStageOutcome.Skip(detail: "no nes6502-sst corpus; use --fetch-corpora or --sst");
        }
        var cases = new List<PostCaseResult>(capacity: 256);
        var csv = new StringBuilder(value: "opcode,vectors,passed,failed,oracle_conflict\n");
        var harness = new Nes6502SstHarness();
        var passed = 0;
        var failed = 0;

        for (var opcode = 0; (opcode < 256); ++opcode) {
            var family = opcode.ToString(format: "x2", provider: CultureInfo.InvariantCulture);
            var path = Path.Combine(path1: context.SstRoot, path2: (family + ".json"));

            if (!File.Exists(path: path)) {
                return PostStageOutcome.Infra(detail: $"incomplete nes6502-sst corpus: missing {family}.json");
            }
            var start = Stopwatch.GetTimestamp();
            using var stream = File.OpenRead(path: path);
            using var document = JsonDocument.Parse(utf8Json: stream);
            var vectors = document.RootElement;

            if (vectors.GetArrayLength() != 10_000) {
                return PostStageOutcome.Infra(detail: $"{family}: expected 10,000 vectors, found {vectors.GetArrayLength()}");
            }
            var familyFailures = 0;
            string? firstFailure = null;

            foreach (var vector in vectors.EnumerateArray()) {
                var failure = harness.Run(vector: vector);

                if (failure is not null) {
                    ++familyFailures;
                    firstFailure ??= $"{vector.GetProperty(propertyName: "name").GetString()}: {failure}";
                }
            }
            var familyPassed = (10_000 - familyFailures);

            passed += familyPassed;
            failed += familyFailures;
            _ = csv.AppendLine(value: $"{family},10000,{familyPassed},{familyFailures},");
            cases.Add(item: new(Name: family, Verdict: ((familyFailures == 0) ? PostCaseVerdict.Pass : PostCaseVerdict.Mismatch),
                Detail: ($"{familyPassed}/10000 passed" + ((firstFailure is null) ? "" : $"; first: {firstFailure}")),
                Duration: Stopwatch.GetElapsedTime(startingTimestamp: start)));
            Console.Out.WriteLine(value: $"nes6502 {family}: {familyPassed}/10000 passed");
        }
        _ = Directory.CreateDirectory(path: context.ArtifactsDirectory);
        File.WriteAllText(path: Path.Combine(path1: context.ArtifactsDirectory, path2: "nes6502-opcodes.csv"), contents: csv.ToString());

        return ((failed == 0)
            ? PostStageOutcome.Pass(cases: cases, detail: $"{passed}/2560000 vectors; 256 opcode families; full register, RAM, and bus traces; no oracle-conflict skips")
            : PostStageOutcome.Fail(cases: cases, detail: $"{passed}/2560000 passed, {failed} failed; see per-opcode cases and nes6502-opcodes.csv"));
    }
}
