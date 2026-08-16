using System.Text;

namespace Puck.Maths.Tests;

/// <summary>One gate statement's legs.</summary>
/// <param name="Surface">The surface: <c>law: Smoke</c>, <c>law: Default</c>, <c>law: Deep</c> or
/// <c>law: Exhaustive</c>.</param>
/// <param name="Statement">The statement's stable name: a law id.</param>
/// <param name="Legs">The declared legs, in declaration order.</param>
internal sealed record LegRow(string Surface, string Statement, IReadOnlyList<Leg> Legs);
/// <summary>
/// Module 5 — the leg ledger. Reads the leg declarations from the law cases in <see cref="LawRegistry"/>, checks what
/// a machine honestly can about them, and renders the committed <c>leg-ledger.md</c>. Where legs share code, agreement
/// proves everything EXCEPT the shared part; the ledger's registers are that gap, enumerated and regenerated on every
/// run so they cannot go stale.
/// </summary>
/// <remarks>The law registry is the only surface this reads: every gate statement in the project is a law case, so the
/// declarations are taken from <see cref="LawCase.Legs"/> directly rather than parsed out of any other file.</remarks>
internal static class LegLedger {
    /// <summary>The prefix a leg opens its citation with to declare that the evidence it names is not there yet. It is
    /// the one spelling the owed-marker register keys on, so a gap named in any other words stays invisible.</summary>
    private const string OwedMarker = "OWED:";

    /// <summary>Gets the law-side rows, one per declared case, in ordinal id order.</summary>
    /// <returns>The rows.</returns>
    public static IReadOnlyList<LegRow> LawRows() =>
        [.. LawRegistry.All
            .OrderBy(keySelector: static lawCase => lawCase.Id, comparer: StringComparer.Ordinal)
            .Select(selector: static lawCase => new LegRow(Surface: $"law: {lawCase.Tier}", Statement: lawCase.Id, Legs: lawCase.Legs))];
    /// <summary>Every declaration defect a machine can see, as human-readable lines. Empty means the declarations are
    /// well formed — which is NOT the same as true; see <see cref="LegLedgerTests"/>.</summary>
    /// <param name="rows">The rows to check.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> DeclarationViolations(IReadOnlyList<LegRow> rows) {
        var violations = new List<string>();

        foreach (var row in rows) {
            if (row.Legs.Count == 0) {
                violations.Add(item: $"{row.Statement}: declares no leg.");

                continue;
            }

            for (var i = 0; (i < row.Legs.Count); ++i) {
                var leg = row.Legs[i];
                var where = $"{row.Statement} leg {(i + 1)} ({leg.KindToken}{((leg.FlavorToken.Length > 0) ? (":" + leg.FlavorToken) : "")})";

                if (string.IsNullOrWhiteSpace(value: leg.Subject)) {
                    violations.Add(item: $"{where}: empty subject.");
                }

                if (leg.IsAgreement && string.IsNullOrWhiteSpace(value: leg.Against)) {
                    violations.Add(item: $"{where}: an agreement leg names nothing to stand against.");
                }

                if ((leg.Kind == LegKind.SharedSubstrate) && string.IsNullOrWhiteSpace(value: leg.Shared)) {
                    violations.Add(item: $"{where}: a shared-substrate leg must name what the two sides share.");
                }

                if ((leg.Flavor is ShareFlavor.DelegationTwin or ShareFlavor.SharedExactKernel) && string.IsNullOrWhiteSpace(value: leg.Citation)) {
                    violations.Add(item: $"{where}: must cite where the shared kernel is independently pinned, or the classical pin it is owed.");
                }

                if (leg.IsTranscription && string.IsNullOrWhiteSpace(value: leg.Citation)) {
                    violations.Add(item: $"{where}: a transcription must name the independent witness beside it, or say in those words that none stands.");
                }

                if ((leg.Kind == LegKind.InTreeIndependent) && string.IsNullOrWhiteSpace(value: leg.Citation)) {
                    violations.Add(item: $"{where}: must cite the envelope the second implementation itself rests on.");
                }

                if (leg.IsRelativeCanary && string.IsNullOrWhiteSpace(value: leg.Absolute)) {
                    violations.Add(item: $"{where}: a relative canary must name an absolute sibling.");
                }

                // A doc gap is a statement about ONE computation and its own documentation; spelling it on an agreement
                // leg would hide it from the register, which is the only place the divergence is carried forward.
                if (leg.IsDocGap && (leg.Kind != LegKind.Structural)) {
                    violations.Add(item: $"{where}: a doc-gap citation belongs on a structural leg, where Leg.PinnedAsObserved puts it.");
                }

                if (leg.IsDocGap && string.IsNullOrWhiteSpace(value: leg.Documented)) {
                    violations.Add(item: $"{where}: a doc-gap leg must say what the XML doc claims.");
                }
            }
        }

        return violations;
    }
    /// <summary>Every relative canary whose named absolute sibling does not RESOLVE to a statement this ledger knows.
    /// Resolution is a floor, never sufficiency: that a sibling exists says nothing about whether it discriminates.</summary>
    /// <param name="rows">The rows to check.</param>
    /// <returns>The violations.</returns>
    public static IReadOnlyList<string> UnresolvedSiblings(IReadOnlyList<LegRow> rows) {
        var violations = new List<string>();

        foreach (var row in rows) {
            foreach (var leg in row.Legs.Where(predicate: static leg => leg.IsRelativeCanary)) {
                var token = ReferenceToken(absolute: leg.Absolute);

                if (!Resolves(token: token)) {
                    violations.Add(item: $"{row.Statement}: the absolute sibling opens with '{token}', which is neither a law id nor an owed:<text>.");
                }
            }
        }

        return violations;
    }
    /// <summary>The shapes a declaration must satisfy for the combinators a case ACTUALLY ran.</summary>
    /// <param name="lawCase">The case.</param>
    /// <param name="observed">The shape set <see cref="LawShapes.Observe"/> returned.</param>
    /// <returns>The violation, or <see langword="null"/> when the declaration matches what ran.</returns>
    public static string? ShapeViolation(LawCase lawCase, int observed) {
        if (
            (LawShapes.Contains(observed: observed, shape: LawShape.Twin) || LawShapes.Contains(observed: observed, shape: LawShape.OracleAgreement)) &&
            !lawCase.Legs.Any(predicate: static leg => leg.IsAgreement)
        ) {
            return "ran a twin or oracle-agreement combinator but declares no agreement leg.";
        }

        // A twin handed a non-null witness ran a third leg on the same operand stream, so the declaration must carry an
        // independent leg. This is the direction with teeth for the shared-substrate registers: it cannot be satisfied
        // by a shared-substrate or structural declaration, and it cannot be claimed by a case that ran no witness.
        if (LawShapes.Contains(observed: observed, shape: LawShape.Witnessed) && !lawCase.Legs.Any(predicate: static leg => leg.IsIndependent)) {
            return "ran a twin with an independent witness but declares no independent leg.";
        }

        // One direction only: a claim body may legitimately make a relative statement of its own, so a declared canary
        // does not imply the combinator. Running the combinator DOES imply the declaration.
        if (LawShapes.Contains(observed: observed, shape: LawShape.Divergence) && !lawCase.Legs.Any(predicate: static leg => leg.IsRelativeCanary)) {
            return "ran Laws.DivergenceCanary but declares no relative-canary leg.";
        }

        return null;
    }

    // The reference token is everything before the first dash separator; the rest is prose for a reader. Both dash
    // spellings are accepted so a label reads naturally in either register.
    private static string ReferenceToken(string absolute) {
        var separator = absolute.IndexOf(comparisonType: StringComparison.Ordinal, value: " — ");
        var plain = absolute.IndexOf(comparisonType: StringComparison.Ordinal, value: " - ");

        if ((separator < 0) || ((plain >= 0) && (plain < separator))) {
            separator = plain;
        }

        return ((separator < 0) ? absolute : absolute[..separator]).Trim();
    }
    private static bool Resolves(string token) {
        if (LawRegistry.ById.ContainsKey(key: token)) {
            return true;
        }

        // The honest spelling for a canary with NO absolute sibling anywhere. It resolves so the gate stays a floor
        // rather than a wall, and the canary register prints it as owed work instead of hiding it.
        if (token.StartsWith(comparisonType: StringComparison.Ordinal, value: "owed:")) {
            return (token.Length > "owed:".Length);
        }

        // A law id and owed:<text> are the only forms that resolve. Anything else fails rather than greening a
        // citation to something this project cannot name, so a leg reaching for one has to be repointed at the law
        // that carries the statement.
        return false;
    }

    /// <summary>Renders the committed ledger from every surface's rows. The text carries no date and no wall time, so
    /// two identical runs leave the file byte-identical; the volatile stamp lives in the RESULTS.md Legs block.</summary>
    /// <param name="lawRows">The law-side rows.</param>
    /// <returns>The ledger text.</returns>
    public static string Render(IReadOnlyList<LegRow> lawRows) {
        var all = new List<LegRow>(collection: lawRows);
        var builder = new StringBuilder();

        _ = builder.Append(value: "# Puck.Maths.Tests — LEG LEDGER\n\n");
        _ = builder.Append(value: "Machine-written by the assembly ledger when the leg gates ran. Every gate statement declares the legs it stands\n");
        _ = builder.Append(value: "on — every one of them a law case in LawRegistry.cs — and this file is those declarations read back. Never\n");
        _ = builder.Append(value: "hand-edited. Where legs share code, agreement proves everything EXCEPT the shared part; the registers below are\n");
        _ = builder.Append(value: "that gap, enumerated.\n\n");
        _ = builder.Append(value: "The gates check that the declarations are well formed and that a case's declaration matches the combinator it\n");
        _ = builder.Append(value: "ran. They cannot read the bodies the text describes: whether a leg called classical really shares no code is the\n");
        _ = builder.Append(value: "adversarial review's job, not this file's.\n\n");

        AppendKindCounts(builder: builder, rows: all);
        AppendSurfaceCounts(builder: builder, rows: all);
        AppendRows(builder: builder, heading: "Law cases", rows: lawRows);
        AppendOwedRegister(builder: builder, rows: all);
        AppendOwedMarkers(builder: builder, rows: all);
        AppendDocGapRegister(builder: builder, rows: all);
        AppendTranscriptionRegister(builder: builder, rows: all);
        AppendCanaryRegister(builder: builder, rows: all);
        AppendStructuralRegister(builder: builder, rows: all);

        return (builder.ToString().TrimEnd() + "\n");
    }

    // Grouped on the SAME token pair the row table below and the RESULTS.md leg table print, so the three can never
    // disagree: a relative canary is its own kind here rather than folded into structural, which is what let this
    // summary claim 694 structural legs while its own rows carried 676 structural and 18 relative-canary. The order is
    // declared, and a pair the declaration does not rank still prints — at the end — so a newly spelled kind cannot drop
    // out of the summary the way a fixed row list would let it.
    private static void AppendKindCounts(StringBuilder builder, IReadOnlyList<LegRow> rows) {
        var legs = rows.SelectMany(selector: static row => row.Legs).ToList();

        _ = builder.Append(value: "## Counts by leg kind\n\n| leg kind | flavor | legs | statements |\n| --- | --- | --- | --- |\n");

        foreach (var group in legs
            .GroupBy(keySelector: static leg => (leg.KindToken, leg.FlavorToken))
            .OrderBy(keySelector: static group => KindRank(kind: group.Key.KindToken, flavor: group.Key.FlavorToken))
            .ThenBy(keySelector: static group => ((group.Key.KindToken + ":") + group.Key.FlavorToken), comparer: StringComparer.Ordinal)) {
            var (kind, flavor) = group.Key;
            var statements = rows.Count(predicate: row => row.Legs.Any(predicate: leg => ((leg.KindToken == kind) && (leg.FlavorToken == flavor))));

            _ = builder.Append(value: $"| {kind} | {((flavor.Length > 0) ? flavor : "—")} | {group.Count()} | {statements} |\n");
        }

        _ = builder.Append(value: $"| **total** | | **{legs.Count}** | **{rows.Count}** |\n\n");
    }
    private static void AppendSurfaceCounts(StringBuilder builder, IReadOnlyList<LegRow> rows) {
        _ = builder.Append(value: "## Counts by surface\n\n| surface | statements | agreement legs | structural legs | statements with no independent leg |\n| --- | --- | --- | --- | --- |\n");

        foreach (var surface in rows.Select(selector: static row => row.Surface).Distinct(comparer: StringComparer.Ordinal).OrderBy(keySelector: static surface => surface, comparer: StringComparer.Ordinal)) {
            var block = rows.Where(predicate: row => (row.Surface == surface)).ToList();

            _ = builder.Append(value: $"| {surface} | {block.Count} | {block.Sum(selector: static row => row.Legs.Count(predicate: static leg => leg.IsAgreement))} | {block.Sum(selector: static row => row.Legs.Count(predicate: static leg => !leg.IsAgreement))} | {block.Count(predicate: static row => !row.Legs.Any(predicate: static leg => leg.IsIndependent))} |\n");
        }

        _ = builder.Append(value: "\n");
    }
    private static void AppendRows(StringBuilder builder, string heading, IReadOnlyList<LegRow> rows) {
        _ = builder.Append(value: $"## {heading}\n\n| statement | surface | leg kind | flavor | subject | against | shared | cites |\n| --- | --- | --- | --- | --- | --- | --- | --- |\n");

        foreach (var row in rows) {
            foreach (var leg in row.Legs) {
                _ = builder.Append(value: $"| {Cell(text: row.Statement)} | {Cell(text: row.Surface)} | {leg.KindToken} | {((leg.FlavorToken.Length > 0) ? leg.FlavorToken : "—")} | {Cell(text: leg.Subject)} | {Cell(text: ((leg.Absolute.Length > 0) ? $"{leg.Against} [absolute: {leg.Absolute}]" : leg.Against))} | {Cell(text: leg.Shared)} | {Cell(text: leg.Citation)} |\n");
            }
        }

        _ = builder.Append(value: "\n");
    }
    private static void AppendOwedRegister(StringBuilder builder, IReadOnlyList<LegRow> rows) {
        _ = builder.Append(value: "## Register: shared-substrate statements with no independent leg beside them\n\n");
        _ = builder.Append(value: "What the filter selects, exactly: every shared-substrate leg on a statement that declares no independent leg. It is\n");
        _ = builder.Append(value: "NOT a list of owed third legs, and reading it as one contradicts two of this contract's own rules. A\n");
        _ = builder.Append(value: "**delegation-twin** or a **transcription** owes no third leg at all — one body read twice cannot be separated by an\n");
        _ = builder.Append(value: "operand sweep — and is discharged by its CITATION instead. A **fused-substrate** row is the one that wants an exact\n");
        _ = builder.Append(value: "recomputation of the rounding, at operands where the two disciplines genuinely differ. Some rows also say in their\n");
        _ = builder.Append(value: "OWN text that the gap is inert: unit operands only, an interior gap that cannot exist, two routes that cannot\n");
        _ = builder.Append(value: "differ. No machine can read that, so they are listed anyway and their leg text is the ruling. The gap column keeps\n");
        _ = builder.Append(value: "the two fix queues apart.\n\n");
        _ = builder.Append(value: "| statement | surface | flavor | gap | shared |\n| --- | --- | --- | --- | --- |\n");

        foreach (var row in rows.Where(predicate: static row => (row.Legs.Any(predicate: static leg => (leg.Kind == LegKind.SharedSubstrate)) && !row.Legs.Any(predicate: static leg => leg.IsIndependent)))) {
            foreach (var leg in row.Legs.Where(predicate: static leg => (leg.Kind == LegKind.SharedSubstrate))) {
                _ = builder.Append(value: $"| {Cell(text: row.Statement)} | {Cell(text: row.Surface)} | {leg.FlavorToken} | {((leg.Flavor == ShareFlavor.FusedSubstrate) ? "(B)" : "(A)/(C)")} | {Cell(text: leg.Shared)} |\n");
            }
        }

        _ = builder.Append(value: "\n");
    }
    private static void AppendOwedMarkers(StringBuilder builder, IReadOnlyList<LegRow> rows) {
        _ = builder.Append(value: "## Register: legs that mark their own owed work\n\n");
        _ = builder.Append(value: $"Every leg whose citation opens `{OwedMarker}`, of any kind. The register above keys on the SHAPE of a declaration, so\n");
        _ = builder.Append(value: "it cannot see a leg that names a hole in its own envelope: an INDEPENDENT leg admitting one is invisible to it.\n");
        _ = builder.Append(value: "This register keys on the marker instead, which is why the marker is the spelling: a leg that opens its citation\n");
        _ = builder.Append(value: $"`{OwedMarker}` lands here whatever kind it is.\n\n");
        _ = builder.Append(value: "| statement | surface | leg kind | subject | the marked gap |\n| --- | --- | --- | --- | --- |\n");

        foreach (var row in rows) {
            foreach (var leg in row.Legs.Where(predicate: static leg => leg.Citation.StartsWith(comparisonType: StringComparison.Ordinal, value: OwedMarker))) {
                _ = builder.Append(value: $"| {Cell(text: row.Statement)} | {Cell(text: row.Surface)} | {leg.KindToken}{((leg.FlavorToken.Length > 0) ? (":" + leg.FlavorToken) : "")} | {Cell(text: leg.Subject)} | {Cell(text: leg.Citation[OwedMarker.Length..].Trim())} |\n");
            }
        }

        _ = builder.Append(value: "\n");
    }
    private static void AppendDocGapRegister(StringBuilder builder, IReadOnlyList<LegRow> rows) {
        _ = builder.Append(value: "## Register: behaviour pinned as observed against its own XML doc\n\n");
        _ = builder.Append(value: $"Every leg spelled `Leg.PinnedAsObserved`, whose citation opens `{Leg.DocGapMarker}`. A law that pins what a kernel DOES\n");
        _ = builder.Append(value: "where its own documentation says otherwise is legitimate — the alternative is a law tuned to prose — but only while\n");
        _ = builder.Append(value: "the divergence is carried somewhere a reader can act on it. This register is that somewhere, derived from the\n");
        _ = builder.Append(value: "declarations rather than written beside them, so a leg can neither cite a register that does not exist nor sit in\n");
        _ = builder.Append(value: "one that has gone stale. Each row is a decision owed to whoever owns the kernel: correct the code, or correct the\n");
        _ = builder.Append(value: "doc. Neither the value nor the doc is blessed by appearing here.\n\n");
        _ = builder.Append(value: "| statement | surface | what the run pins | what the doc claims |\n| --- | --- | --- | --- |\n");

        foreach (var row in rows) {
            foreach (var leg in row.Legs.Where(predicate: static leg => leg.IsDocGap)) {
                _ = builder.Append(value: $"| {Cell(text: row.Statement)} | {Cell(text: row.Surface)} | {Cell(text: leg.Subject)} | {Cell(text: leg.Documented)} |\n");
            }
        }

        _ = builder.Append(value: "\n");
    }
    private static void AppendTranscriptionRegister(StringBuilder builder, IReadOnlyList<LegRow> rows) {
        _ = builder.Append(value: "## Register: transcriptions and the witnesses beside them\n\n");
        _ = builder.Append(value: "A transcription carries a rule the subject's own code already carries, so agreement proves FAITHFUL CARRIAGE and\n");
        _ = builder.Append(value: "never that the rule is right — a shared error cancels. Condition (C) forbids counting one as independent evidence,\n");
        _ = builder.Append(value: "and the type system enforces it: `Leg.FaithfulCarriage` is shared-substrate, so no transcription can satisfy a\n");
        _ = builder.Append(value: "check that demands an independent leg. The witness column is what DOES stand beside it; where it opens `NONE`,\n");
        _ = builder.Append(value: "the row is owed one and says so.\n\n");
        _ = builder.Append(value: "| statement | surface | subject | transcribed reference | what is transcribed | the independent witness |\n| --- | --- | --- | --- | --- | --- |\n");

        foreach (var row in rows) {
            foreach (var leg in row.Legs.Where(predicate: static leg => leg.IsTranscription)) {
                _ = builder.Append(value: $"| {Cell(text: row.Statement)} | {Cell(text: row.Surface)} | {Cell(text: leg.Subject)} | {Cell(text: leg.Against)} | {Cell(text: leg.Shared)} | {Cell(text: leg.Citation)} |\n");
            }
        }

        _ = builder.Append(value: "\n");
    }
    private static void AppendCanaryRegister(StringBuilder builder, IReadOnlyList<LegRow> rows) {
        _ = builder.Append(value: "## Register: relative canaries and their absolute siblings\n\n");
        _ = builder.Append(value: "Keyed on the declared RELATIVE-CANARY leg, never on the id — so a measured absolute floor spelled `-canary` is\n");
        _ = builder.Append(value: "not a row here, while a relative statement made inside a claim body is. Every case running\n");
        _ = builder.Append(value: "`Laws.DivergenceCanary` is in this register: the runner asserts that combinator implies the declaration. The\n");
        _ = builder.Append(value: "sibling column reports only that the named statement RESOLVES; whether it discriminates is the review's call.\n\n");
        _ = builder.Append(value: "| canary | surface | subject | against | absolute sibling |\n| --- | --- | --- | --- | --- |\n");

        foreach (var row in rows) {
            foreach (var leg in row.Legs.Where(predicate: static leg => leg.IsRelativeCanary)) {
                _ = builder.Append(value: $"| {Cell(text: row.Statement)} | {Cell(text: row.Surface)} | {Cell(text: leg.Subject)} | {Cell(text: leg.Against)} | {Cell(text: leg.Absolute)} |\n");
            }
        }

        _ = builder.Append(value: "\n");
    }
    private static void AppendStructuralRegister(StringBuilder builder, IReadOnlyList<LegRow> rows) {
        _ = builder.Append(value: "## Register: structural-only statements\n\n");
        _ = builder.Append(value: "Every leg self-standing — listed so no summary can read them as twins. Rows carrying a relative canary are\n");
        _ = builder.Append(value: "EXCLUDED and live in the canary register above.\n\n");
        _ = builder.Append(value: "| statement | surface | legs |\n| --- | --- | --- |\n");

        foreach (var row in rows.Where(predicate: static row => (!row.Legs.Any(predicate: static leg => leg.IsAgreement) && !row.Legs.Any(predicate: static leg => leg.IsRelativeCanary)))) {
            _ = builder.Append(value: $"| {Cell(text: row.Statement)} | {Cell(text: row.Surface)} | {row.Legs.Count} |\n");
        }

        _ = builder.Append(value: "\n");
    }
    // The declared print order for the kind summary, as the token pairs the row table uses. An unranked pair sorts last
    // rather than vanishing.
    private static int KindRank(string kind, string flavor) =>
        (kind, flavor) switch {
            ("classical", "") => 0,
            ("presented-twin", "") => 1,
            ("in-tree-independent", "") => 2,
            ("shared-substrate", "fused-substrate") => 3,
            ("shared-substrate", "shared-exact-kernel") => 4,
            ("shared-substrate", "delegation-twin") => 5,
            ("shared-substrate", "transcription") => 6,
            ("shared-substrate", "intra-presented") => 7,
            ("shared-substrate", "shared-upstream") => 8,
            ("relative-canary", "") => 9,
            ("structural", "") => 10,
            _ => 11,
        };
    // Markdown table cells: the pipe is the column separator and a newline would end the row, so both are neutralized.
    private static string Cell(string text) =>
        (string.IsNullOrEmpty(value: text) ? "—" : text.Replace(newValue: "\\|", oldValue: "|").ReplaceLineEndings(replacementText: " "));
}
