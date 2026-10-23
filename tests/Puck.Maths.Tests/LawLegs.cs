namespace Puck.Maths.Tests;

/// <summary>One gate statement's legs.</summary>
/// <param name="Statement">The statement's stable name: a law id.</param>
/// <param name="Legs">The declared legs, in declaration order.</param>
internal sealed record LegRow(string Statement, IReadOnlyList<Leg> Legs);
/// <summary>
/// Checks law evidence declarations against their bindings and observed combinators.
/// </summary>
/// <remarks>The law registry is the only surface this reads: every gate statement in the project is a law case, so the
/// declarations are taken from <see cref="LawCase.Legs"/> directly rather than parsed out of any other file.</remarks>
internal static class LawLegs {
    // The reference token is everything before the first dash separator; the rest is prose for a reader. Both dash
    // spellings are accepted so a label reads naturally in either register.
    private static string ReferenceToken(string absolute) {
        var separator = absolute.IndexOf(
            comparisonType: StringComparison.Ordinal,
            value: " — "
        );
        var plain = absolute.IndexOf(
            comparisonType: StringComparison.Ordinal,
            value: " - "
        );

        if (
            (separator < 0) ||
            ((plain >= 0) && (plain < separator))
        ) {
            separator = plain;
        }

        return ((separator < 0)
            ? absolute
            : absolute[..separator]).Trim();
    }
    private static bool Resolves(string token) {
        if (LawRegistry.ById.ContainsKey(key: token)) {
            return true;
        }

        // The honest spelling for a canary with NO absolute sibling anywhere. It resolves so the gate stays a floor
        // rather than a wall, and the declaration names it as owed work.
        if (token.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: "owed:"
        )) {
            return (token.Length > "owed:".Length);
        }

        // A law id and owed:<text> are the only forms that resolve. Anything else fails rather than greening a
        // citation to something this project cannot name, so a leg reaching for one has to be repointed at the law
        // that carries the statement.
        return false;
    }

    /// <summary>Every declaration defect a machine can see, as human-readable lines. Empty means the declarations are
    /// well formed — which is NOT the same as true; see <see cref="LawLegTests"/>.</summary>
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
                var where = $"{row.Statement} leg {(i + 1)} ({leg.KindToken}{((leg.FlavorToken.Length > 0)
                    ? (":" + leg.FlavorToken)
                    : "")})";

                if (string.IsNullOrWhiteSpace(value: leg.Subject)) {
                    violations.Add(item: $"{where}: empty subject.");
                }

                if (
                    leg.IsAgreement &&
                    string.IsNullOrWhiteSpace(value: leg.Against)
                ) {
                    violations.Add(item: $"{where}: an agreement leg names nothing to stand against.");
                }

                if (
                    (leg.Kind == LegKind.SharedSubstrate) &&
                    string.IsNullOrWhiteSpace(value: leg.Shared)
                ) {
                    violations.Add(item: $"{where}: a shared-substrate leg must name what the two sides share.");
                }

                if (
                    (leg.Flavor is ShareFlavor.DelegationTwin or ShareFlavor.SharedExactKernel) &&
                    string.IsNullOrWhiteSpace(value: leg.Citation)
                ) {
                    violations.Add(item: $"{where}: must cite where the shared kernel is independently pinned, or the classical pin it is owed.");
                }

                if (
                    leg.IsTranscription &&
                    string.IsNullOrWhiteSpace(value: leg.Citation)
                ) {
                    violations.Add(item: $"{where}: a transcription must name the independent witness beside it, or say in those words that none stands.");
                }

                if (
                    (leg.Kind == LegKind.InTreeIndependent) &&
                    string.IsNullOrWhiteSpace(value: leg.Citation)
                ) {
                    violations.Add(item: $"{where}: must cite the envelope the second implementation itself rests on.");
                }

                if (
                    leg.IsRelativeCanary &&
                    string.IsNullOrWhiteSpace(value: leg.Absolute)
                ) {
                    violations.Add(item: $"{where}: a relative canary must name an absolute sibling.");
                }

                // A doc gap is a statement about ONE computation and its own documentation; spelling it on an agreement
                // leg would hide it from the authored structural declarations.
                if (
                    leg.IsDocGap &&
                    (leg.Kind != LegKind.Structural)
                ) {
                    violations.Add(item: $"{where}: a doc-gap citation belongs on a structural leg.");
                }

                if (
                    leg.IsDocGap &&
                    string.IsNullOrWhiteSpace(value: leg.Documented)
                ) {
                    violations.Add(item: $"{where}: a doc-gap leg must say what the XML doc claims.");
                }
            }
        }

        return violations;
    }
    /// <summary>Gets the law-side rows, one per declared case, in ordinal id order.</summary>
    /// <returns>The rows.</returns>
    public static IReadOnlyList<LegRow> LawRows() =>
        [.. LawRegistry.All
            .OrderBy(
                keySelector: static lawCase => lawCase.Id,
                comparer: StringComparer.Ordinal
            )
            .Select(selector: static lawCase => new LegRow(
                Statement: lawCase.Id,
                Legs: lawCase.Legs
            ))];
    public static string? ShapeViolation(LawCase lawCase, int observed) {
        if (
            (LawShapes.Contains(
            observed: observed,
            shape: LawShape.Twin
        ) || LawShapes.Contains(
            observed: observed,
            shape: LawShape.OracleAgreement
        )) &&
            !lawCase.Legs.Any(predicate: static leg => leg.IsAgreement)
        ) {
            return "ran a twin or oracle-agreement combinator but declares no agreement leg.";
        }

        // A twin handed a non-null witness ran a third leg on the same operand stream, so the declaration must carry an
        // independent leg. This is the direction with teeth for the shared-substrate registers: it cannot be satisfied
        // by a shared-substrate or structural declaration, and it cannot be claimed by a case that ran no witness.
        if (
            LawShapes.Contains(
            observed: observed,
            shape: LawShape.Witnessed
        ) &&
            !lawCase.Legs.Any(predicate: static leg => leg.IsIndependent)
        ) {
            return "ran a twin with an independent witness but declares no independent leg.";
        }

        // One direction only: a claim body may legitimately make a relative statement of its own, so a declared canary
        // does not imply the combinator. Running the combinator DOES imply the declaration.
        if (
            LawShapes.Contains(
            observed: observed,
            shape: LawShape.Divergence
        ) &&
            !lawCase.Legs.Any(predicate: static leg => leg.IsRelativeCanary)
        ) {
            return "ran Laws.DivergenceCanary but declares no relative-canary leg.";
        }

        return null;
    }
    /// <summary>Every relative canary whose named absolute sibling does not RESOLVE to a statement the registry knows.
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
}
