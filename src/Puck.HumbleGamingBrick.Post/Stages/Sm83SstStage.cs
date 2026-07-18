namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Tier-B stage: drive the shared SM83 core through every vector in the SingleStepTests/sm83 corpus
/// (<see href="https://github.com/SingleStepTests/sm83"/>) — 500 opcode families &#215; 1000 hand-generated per
/// -instruction cases, each carrying initial/final registers, the flat-RAM bytes touched, and the M-cycle bus-pin
/// trace. It validates the one-shared-SM83-core doctrine instruction-by-instruction, off-ROM, independent of the
/// conformance- and acceptance-ROM style of coverage. The corpus is evidence, never a gate: it skips cleanly when
/// <c>PUCK_GB_SST</c> is unset or the directory is missing.
/// <para>
/// Two opcode families are excluded from pass/fail as documented ORACLE CONFLICTS (see
/// <see cref="ConflictSkippedFamilies"/>): where this corpus's reference disagrees with the oracle that pins OUR
/// behavior, the conflict is reported with vector counts and a reason naming both oracles, never failed and never
/// silently dropped.
/// </para>
/// </summary>
internal sealed class Sm83SstStage : IPostStage {
    // Opcode families excluded from pass/fail as DOCUMENTED ORACLE CONFLICTS — never as a way to launder a genuine
    // correctness finding. A family is only listed here with evidence from a real run (not speculatively), and each
    // reason states the disagreement in evidence-class terms. External suites are evidence, never gates (repo doctrine):
    // where this corpus's reference disagrees with the oracle that pins OUR behavior, the conflict is reported, not failed.
    //
    // "10" (STOP): this corpus's reference models STOP as a FLAT one-byte opcode for every vector, always PC+1 (verified
    // against the shipped v1/10.json: every case's final PC is initial+1 with exactly one 'r-m' entry) — including cases
    // that carry ie=1/IF=0 (no interrupt pending), where hardware-derived behavior (SameBoy sm83_cpu.c stop(), ~line
    // 397/405 — IE & IF & 0x1F, independent of IME) takes PC+2 (the pad byte is consumed). This core's ExecuteStop
    // (Sm83.Decode.cs) now implements that exact IE/IF condition: PC+2 with no interrupt pending, PC+1 (pad byte left to
    // execute as the next instruction) with one already latched. So the corpus's flat model now agrees with this core on
    // the PENDING edge (both PC+1) by coincidence of the corpus never modeling the pad-consumption difference either way,
    // but still disagrees on every NO-PENDING vector, which is every vector the shipped v1/10.json actually carries (all
    // ie=1/IF=0 cases assert PC+1; hardware takes PC+2). The corpus's flat 1-byte model remains the outlier on its own
    // majority case; conflict recorded, not failed. The pending-interrupt edge itself is covered directly by
    // Sm83StopPendingInterruptStage (Tier A, self-contained), not by this corpus.
    //
    // "fb" (EI): this corpus's reference always arms the EI-delay countdown, even when IME is already set (485/1000
    // vectors carry initial ime=1, and all 485 assert final ei=1). This core treats EI as a no-op when IME is already
    // enabled or an enable is already in flight (Sm83.Decode.cs case 0xFB) — matching hardware-derived acceptance
    // timing, which the acceptance suite's ei_sequence test independently pins: 18 back-to-back EIs must still service on
    // the first EI's schedule (asserts B=$01, C=$A2), which an always-re-arm model fails. The corpus's re-arm is an
    // internal ei-pending representation unobservable in this single-instruction corpus as actual dispatch and provably
    // wrong for the multi-EI window the acceptance suite pins. Core behavior unchanged (correct); conflict recorded.
    private static readonly IReadOnlyDictionary<string, string> ConflictSkippedFamilies = new Dictionary<string, string>(comparer: StringComparer.OrdinalIgnoreCase) {
        ["10"] = "oracle conflict: this corpus models STOP as a flat 1-byte opcode (PC+1) even on its no-interrupt-pending vectors; hardware (and this core, since the M-05 fix) consumes the pad byte (PC+2) exactly when no interrupt is pending and leaves it unconsumed (PC+1) only when one is already latched — the corpus's flat model happens to agree on the pending edge but is the outlier on every no-pending vector it actually carries; the pending edge itself is covered by Sm83StopPendingInterruptStage",
        ["fb"] = "oracle conflict: this corpus re-arms EI's internal delay even with IME already set (485/1000 vectors, all that edge); hardware-derived acceptance timing (the acceptance suite's ei_sequence) pins EI-as-no-op-when-enabled, matched by this core",
    };

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
            return PostStageOutcome.Skip(detail: "no SingleStepTests/sm83 corpus (set PUCK_GB_SST; clone https://github.com/SingleStepTests/sm83)");
        }

        using var harness = new Sm83SstHarness();

        var vectorsRun = 0;
        var vectorsPassed = 0;
        var familiesRun = 0;
        var failures = new List<string>();
        var skippedFamilies = new List<string>();

        foreach (var familyPath in families) {
            var familyName = Path.GetFileNameWithoutExtension(path: familyPath);

            if (ConflictSkippedFamilies.TryGetValue(key: familyName, value: out var reason)) {
                skippedFamilies.Add(item: $"{familyName} [{Sm83SstVectorFile.Load(path: familyPath).Count} vectors] ({reason})");

                continue;
            }

            ++familiesRun;

            var vectors = Sm83SstVectorFile.Load(path: familyPath);
            var familyFailures = 0;
            string? firstFailureDetail = null;

            foreach (var vector in vectors) {
                ++vectorsRun;

                var result = harness.Run(vector: vector);

                if (result.Passed) {
                    ++vectorsPassed;
                } else {
                    ++familyFailures;
                    firstFailureDetail ??= $"{vector.Name}: {result.Detail}";
                }
            }

            if (familyFailures > 0) {
                failures.Add(item: $"{familyName} ({familyFailures}/{vectors.Count} failed; first: {firstFailureDetail})");
            }
        }

        var skipNote = ((skippedFamilies.Count > 0) ? $"; {skippedFamilies.Count} documented oracle-conflict skips: {string.Join(separator: ", ", values: skippedFamilies)}" : string.Empty);
        var detail = $"{vectorsPassed}/{vectorsRun} vectors passed across {familiesRun} families{skipNote}";

        return ((failures.Count == 0)
            ? PostStageOutcome.Pass(detail: detail)
            : PostStageOutcome.Fail(detail: $"{detail}; failed families: {string.Join(separator: ", ", values: failures)}"));
    }
}
