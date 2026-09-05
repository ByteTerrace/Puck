using System.Diagnostics;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Tier-B stage: drive the shared SM83 core through every vector in the SingleStepTests/sm83 corpus
/// (<see href="https://github.com/SingleStepTests/sm83"/>) — 500 opcode families &#215; 1000 hand-generated per
/// -instruction cases, each carrying initial/final registers, the flat-RAM bytes touched, and the M-cycle bus-pin
/// trace. It validates the one-shared-SM83-core doctrine instruction-by-instruction, off-ROM, independent of the
/// conformance- and acceptance-ROM style of coverage. It skips cleanly when the corpus is absent. Families are
/// independent, so each runs on its own harness and the families run on every processor at once.
/// <para>
/// Two opcode families are excluded from pass/fail as documented oracle conflicts (see
/// <see cref="ConflictSkippedFamilies"/>): where this corpus's reference disagrees with the oracle that pins our
/// behavior, the conflict is reported with vector counts and a reason naming both oracles, never failed and never
/// silently dropped.
/// </para>
/// </summary>
internal sealed class Sm83SstStage : IPostStage<PostContext> {
    // Opcode families excluded from pass/fail as documented oracle conflicts — never as a way to launder a genuine
    // correctness finding. A family is only listed here with evidence from a real run, and each reason states the
    // disagreement in evidence-class terms: where this corpus's reference disagrees with the oracle that pins our
    // behavior, the conflict is reported, not failed.
    //
    // "10" (STOP): this corpus's reference models STOP as a flat one-byte opcode for every vector, always PC+1 —
    // including cases that carry ie=1/IF=0 (no interrupt pending), where hardware-derived behavior (SameBoy
    // sm83_cpu.c stop(): IE & IF & 0x1F, independent of IME) takes PC+2 (the pad byte is consumed). ExecuteStop
    // (Sm83.Decode.cs) implements that exact IE/IF condition: PC+2 with no interrupt pending, PC+1 (pad byte left to
    // execute as the next instruction) with one already latched. The corpus's flat model agrees with this core on the
    // pending edge (both PC+1) only because the corpus never models the pad-consumption difference either way, and
    // disagrees on every no-pending vector, which is every vector the shipped v1/10.json carries. The pending edge
    // itself is covered directly by Sm83StopPendingInterruptStage (Tier A, self-contained), not by this corpus.
    //
    // "fb" (EI): this corpus's reference always arms the EI-delay countdown, even when IME is already set (485/1000
    // vectors carry initial ime=1, and all 485 assert final ei=1). This core treats EI as a no-op when IME is already
    // enabled or an enable is already in flight (Sm83.Decode.cs case 0xFB) — matching hardware-derived acceptance
    // timing, which the acceptance suite's ei_sequence test independently pins: 18 back-to-back EIs must still service
    // on the first EI's schedule (asserts B=$01, C=$A2), which an always-re-arm model fails.
    private static readonly IReadOnlyDictionary<string, string> ConflictSkippedFamilies = new Dictionary<string, string>(comparer: StringComparer.OrdinalIgnoreCase) {
        ["10"] = "oracle conflict: this corpus models STOP as a flat 1-byte opcode (PC+1) even on its no-interrupt-pending vectors; hardware (and this core) consumes the pad byte (PC+2) exactly when no interrupt is pending and leaves it unconsumed (PC+1) only when one is already latched — the corpus's flat model happens to agree on the pending edge but is the outlier on every no-pending vector it actually carries; the pending edge itself is covered by Sm83StopPendingInterruptStage",
        ["fb"] = "oracle conflict: this corpus re-arms EI's internal delay even with IME already set (485/1000 vectors, all that edge); hardware-derived acceptance timing (the acceptance suite's ei_sequence) pins EI-as-no-op-when-enabled, matched by this core",
    };

    private sealed record FamilyResult(string Name, int Vectors, int Failed, string? FirstFailure, string? SkipReason, TimeSpan Duration);

    /// <inheritdoc/>
    public string Name =>
        "sst-sm83";
    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.B;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var families = Sm83SstCorpus.Families(root: context.SstRoot);

        if (families.Count == 0) {
            return PostStageOutcome.Skip(detail: "no SingleStepTests/sm83 corpus (pass --sst; https://github.com/SingleStepTests/sm83)");
        }

        var results = new FamilyResult[families.Count];

        _ = Parallel.For(
            body: (index, _, harness) => {
                results[index] = RunFamily(
                    harness: harness,
                    path: families[index]
                );

                return harness;
            },
            fromInclusive: 0,
            localFinally: static harness => harness.Dispose(),
            localInit: static () => new Sm83SstHarness(),
            parallelOptions: new ParallelOptions {
                MaxDegreeOfParallelism = context.Parallelism,
            },
            toExclusive: families.Count
        );

        var vectorsRun = 0;
        var vectorsPassed = 0;
        var familiesRun = 0;
        var failures = new List<string>();
        var skippedFamilies = new List<string>();
        var cases = new List<PostCaseResult>(capacity: results.Length);

        foreach (var result in results) {
            if (result.SkipReason is not null) {
                skippedFamilies.Add(item: $"{result.Name} [{result.Vectors} vectors] ({result.SkipReason})");
                cases.Add(item: new PostCaseResult(
                    Detail: result.SkipReason,
                    Duration: result.Duration,
                    Name: result.Name,
                    Verdict: PostCaseVerdict.Skip
                ));

                continue;
            }

            ++familiesRun;
            vectorsRun += result.Vectors;
            vectorsPassed += (result.Vectors - result.Failed);

            if (result.Failed > 0) {
                failures.Add(item: $"{result.Name} ({result.Failed}/{result.Vectors} failed; first: {result.FirstFailure})");
            }

            cases.Add(item: new PostCaseResult(
                Detail: ((result.Failed == 0)
                    ? $"{result.Vectors} vectors"
                    : $"{result.Failed}/{result.Vectors} failed; first: {result.FirstFailure}"),
                Duration: result.Duration,
                Name: result.Name,
                Verdict: ((result.Failed == 0)
                    ? PostCaseVerdict.Pass
                    : PostCaseVerdict.Mismatch)
            ));
        }

        var skipNote = ((skippedFamilies.Count > 0)
            ? $"; {skippedFamilies.Count} documented oracle-conflict skips: {string.Join(
            separator: ", ",
            values: skippedFamilies
        )}"
            : string.Empty);
        var detail = $"{vectorsPassed}/{vectorsRun} vectors passed across {familiesRun} families{skipNote}";

        return ((failures.Count == 0)
            ? PostStageOutcome.Pass(
                cases: cases,
                detail: detail
            )
            : PostStageOutcome.Fail(
                cases: cases,
                detail: $"{detail}; failed families: {string.Join(
                    separator: ", ",
                    values: failures
                )}"
            ));
    }

    private static FamilyResult RunFamily(Sm83SstHarness harness, string path) {
        var start = Stopwatch.GetTimestamp();
        var familyName = Path.GetFileNameWithoutExtension(path: path);
        var vectors = Sm83SstVectorFile.Load(path: path);

        if (ConflictSkippedFamilies.TryGetValue(
            key: familyName,
            value: out var reason
        )) {
            return new FamilyResult(
                Duration: Stopwatch.GetElapsedTime(startingTimestamp: start),
                Failed: 0,
                FirstFailure: null,
                Name: familyName,
                SkipReason: reason,
                Vectors: vectors.Count
            );
        }

        var failed = 0;
        string? firstFailure = null;

        foreach (var vector in vectors) {
            var result = harness.Run(vector: vector);

            if (!result.Passed) {
                ++failed;
                firstFailure ??= $"{vector.Name}: {result.Detail}";
            }
        }

        return new FamilyResult(
            Duration: Stopwatch.GetElapsedTime(startingTimestamp: start),
            Failed: failed,
            FirstFailure: firstFailure,
            Name: familyName,
            SkipReason: null,
            Vectors: vectors.Count
        );
    }
}
