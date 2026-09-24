using System.Collections.Concurrent;
using System.Text;

[assembly: Xunit.AssemblyFixture(typeof(Puck.Maths.Tests.LedgerFixture))]

namespace Puck.Maths.Tests;

/// <summary>What this session actually executed, and whether its laws held. Every ledger write is gated on one of these
/// signals, so a filtered or single-tier run can neither refresh nor silence a check it never ran — and a run whose laws
/// failed advances no frontier.</summary>
internal static class LedgerState {
    private static readonly ConcurrentDictionary<Tier, int> Executed = new();

    private static volatile bool LawFailureObserved;
    private static volatile bool LawLegGateExecuted;
    private static volatile bool RatchetExecuted;

    /// <summary>Gets whether every law case this session ran passed — the GREEN gate on
    /// <see cref="Frontier.AdvanceAndPersist"/>. The flag is monotonic: it starts clear and is only ever set, from
    /// whichever runner thread saw the failure, and is read once after the last test has finished, so
    /// <see langword="volatile"/> is the whole synchronization it needs.</summary>
    /// <remarks>NON-law gates are deliberately outside this signal. Only <see cref="Laws"/>'s combinators call
    /// <see cref="Frontier.Consume"/> — the ratchet gate and both leg gates consume no operand at all — so
    /// their verdicts are pure functions of the reflected member surface, the declaration text and the tool files. A red
    /// there reproduces identically on the next run whatever the frontier counters say: it has no sweep to be masked by,
    /// and so nothing to gate.</remarks>
    public static bool LawsPassed =>
        !LawFailureObserved;
    /// <summary>Gets whether the leg gate ran this session. A run that did not execute it must leave the artifact alone
    /// rather than publish it from nothing.</summary>
    public static bool LegGatesRan =>
        LawLegGateExecuted;
    /// <summary>Gets whether the coverage ratchet gate ran this session.</summary>
    public static bool RatchetRan =>
        RatchetExecuted;

    /// <summary>The number of law cases executed for a tier this session.</summary>
    /// <param name="tier">The tier.</param>
    /// <returns>The count.</returns>
    public static int Count(Tier tier) =>
        Executed.GetValueOrDefault(key: tier);
    /// <summary>Records that a law case of the given tier ran.</summary>
    /// <param name="tier">The case tier.</param>
    public static void RecordLaw(Tier tier) =>
        Executed.AddOrUpdate(
            addValue: 1,
            key: tier,
            updateValueFactory: static (_, current) => (current + 1)
        );
    /// <summary>Records that a law case failed this session, so the ledger persists NO frontier advance for ANY
    /// key.</summary>
    public static void RecordLawFailure() =>
        LawFailureObserved = true;
    /// <summary>Records that the law-side leg gate ran.</summary>
    public static void RecordLawLegGate() =>
        LawLegGateExecuted = true;
    /// <summary>Records that the coverage ratchet gate ran.</summary>
    public static void RecordRatchet() =>
        RatchetExecuted = true;
}

/// <summary>
/// The assembly-level ledger. Disposed once, after the session's last test, it persists exactly what that session
/// exercised: the rolling
/// <see cref="Frontier"/> when a GREEN run consumed domains, the coverage <see cref="Manifest"/> when the ratchet gate
/// ran, and the <c>RESULTS.md</c> blocks the run owns — one per tier that executed law cases, plus coverage, legs, and
/// frontier. A block no one exercised keeps the text its own last run left, so alternating tiers never overwrites one
/// tier's record with another tier's zero. Writes are update-on-change on deterministic content: the last-run dates are
/// the only volatile ones and are excluded from the comparison, so an unchanged run leaves the tree alone. Every write
/// goes to <see cref="TestPaths.Output"/>: the run's own ledger directory in the build output, which
/// <see cref="TestPaths.RecordCommand"/> promotes over the committed files.
/// </summary>
/// <remarks>
/// EVERY figure here is machine-independent BY CONSTRUCTION — executed case counts, coverage counts, leg counts,
/// frontier indices — so the same commit produces the same numbers on every machine this engine is developed against,
/// and a difference is a real difference rather than a difference of hardware. The ledger records NO duration,
/// deliberately, and three properties of this class are why one could not mean anything here. It would carry no
/// machine identity, so each machine's run would overwrite the last one's and two consecutive readings would compare
/// two different computers. The fixture spans the whole SESSION rather than one tier, so a figure stamped under a
/// block could not be that block's cost even on one machine. And nothing here measures the environment, so a figure
/// taken on a loaded machine would be committed as fact. Cost is measured outside the suite, by <c>puck bench</c>.
/// </remarks>
public sealed class LedgerFixture : IDisposable {
    private const string NotRecorded = "No run has recorded this block.";
    private const int StampDateLength = 10;
    private const string StampPrefix = "- last run: ";

    private static readonly Tier[] ReportedTiers = [Tier.Smoke, Tier.Default, Tier.Deep, Tier.Exhaustive];
    private static readonly string[] SectionOrder = ["Invocations", "Smoke", "Default", "Deep", "Exhaustive", "Coverage", "Legs", "Frontier"];

    // A block this run does NOT own, brought forward from the previous file — with its last-run line normalized to the
    // date alone. A carried block keeps whatever text its own last run wrote, which is the whole point of the merge,
    // but the file must never render a line shape this ledger does not produce, or a rarely-run tier could publish one
    // indefinitely under a header that denies it. The block's own date survives untouched, so the staleness it reports
    // is still its own.
    private static string? CarryForward(string? body) {
        if (body is null) {
            return null;
        }

        var lines = body.ReplaceLineEndings(replacementText: "\n").Split(separator: '\n');

        for (var index = 0; (index < lines.Length); ++index) {
            var line = lines[index];

            if (
                line.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: StampPrefix
            ) &&
                (line.Length > (StampPrefix.Length + StampDateLength))
            ) {
                lines[index] = line[..(StampPrefix.Length + StampDateLength)];
            }
        }

        return string.Join(
            separator: "\n",
            values: lines
        );
    }
    private static void Flush(Dictionary<string, string> sections, string name, StringBuilder body) {
        if (name.Length > 0) {
            sections[name] = body.ToString();
        }
    }
    private static string FrontierTable(IReadOnlyList<(string Key, int Block, long Index)> frontier) {
        var builder = new StringBuilder();

        _ = builder.Append(value: "| domain | block | index |\n");
        _ = builder.Append(value: "| --- | --- | --- |\n");

        foreach (var (key, block, index) in frontier) {
            _ = builder.Append(value: $"| {key} | {block} | {index} |\n");
        }

        return (builder.ToString() + "\n");
    }
    private static string Invocations() {
        var builder = new StringBuilder();

        _ = builder.Append(value: "| tier | command |\n");
        _ = builder.Append(value: "| --- | --- |\n");
        _ = builder.Append(value: "| Default (Smoke+Default) | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release` |\n");
        _ = builder.Append(value: "| Smoke | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --settings tests/Puck.Maths.Tests/smoke.runsettings` |\n");
        _ = builder.Append(value: "| Deep | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --settings tests/Puck.Maths.Tests/deep.runsettings` |\n");
        _ = builder.Append(value: "| Exhaustive | `dotnet test tests/Puck.Maths.Tests/Puck.Maths.Tests.csproj -c Release --settings tests/Puck.Maths.Tests/exhaustive.runsettings` |\n");

        return builder.ToString();
    }
    private static string LegTable(IReadOnlyList<LegRow> legs) {
        var builder = new StringBuilder();
        var all = legs.SelectMany(selector: static row => row.Legs).ToList();

        _ = builder.Append(value: "| leg kind | legs |\n");
        _ = builder.Append(value: "| --- | --- |\n");

        foreach (var group in all.GroupBy(keySelector: static leg => (leg.KindToken + ((leg.FlavorToken.Length > 0)
            ? (":" + leg.FlavorToken)
            : ""))).OrderBy(
            keySelector: static group => group.Key,
            comparer: StringComparer.Ordinal
        )) {
            _ = builder.Append(value: $"| {group.Key} | {group.Count()} |\n");
        }

        _ = builder.Append(value: $"| **total** | **{all.Count}** |\n\n");
        _ = builder.Append(value: $"- statements: {legs.Count}\n");
        _ = builder.Append(value: $"- statements with no independent leg: {legs.Count(predicate: static row => !row.Legs.Any(predicate: static leg => leg.IsIndependent))}\n");

        return builder.ToString();
    }
    // Rebuilds the ledger from the blocks this run owns, falling back to the previous file's text for every other
    // block; a block neither owned nor previously recorded says so.
    private static string Merge(IReadOnlyDictionary<string, string> existing, IReadOnlyDictionary<string, string> fresh) {
        var builder = new StringBuilder();

        _ = builder.Append(value: "# Puck.Maths.Tests — RESULTS\n\n");
        _ = builder.Append(value: "Machine-written by the assembly ledger at run end. Each block records the last run that exercised it — a tier\n");
        _ = builder.Append(value: "block only when that tier ran law cases, coverage only when the ratchet gate ran, the frontier only when a run\n");
        _ = builder.Append(value: "consumed domains AND every law it ran passed — so every other block keeps the text its own last run left. The\n");
        _ = builder.Append(value: "last-run dates are the only volatile content; they do not by themselves trigger a rewrite.\n\n");
        _ = builder.Append(value: "Every figure below is MACHINE-INDEPENDENT by construction: the same commit produces the same counts and the same\n");
        _ = builder.Append(value: "frontier indices on every machine, so a difference here is a real difference and never a difference of hardware.\n");
        _ = builder.Append(value: "No duration is recorded, deliberately. One here would carry no machine identity, would span the whole session\n");
        _ = builder.Append(value: "rather than the block it sits under, and would be taken without a busy-machine guard — so it could not answer\n");
        _ = builder.Append(value: "any question asked of it. Cost is measured outside the suite, by `puck bench`.\n\n");

        foreach (var name in SectionOrder) {
            var owned = fresh.GetValueOrDefault(key: name);
            var body = (owned ?? (CarryForward(body: existing.GetValueOrDefault(key: name)) ?? NotRecorded));

            _ = builder.Append(value: $"## {name}\n\n{body.Trim()}\n\n");
        }

        return (builder.ToString().TrimEnd() + "\n");
    }
    // The blocks this session owns. Each ownership test is the execution signal of the check that produced the block.
    private static Dictionary<string, string> OwnedSections(IReadOnlyList<(string Key, int Block, long Index)>? frontier, (int Covered, int Waived, int Uncovered)? coverage, IReadOnlyList<LegRow>? legs, string stamp) {
        var sections = new Dictionary<string, string>(comparer: StringComparer.Ordinal) { ["Invocations"] = Invocations() };

        foreach (var tier in ReportedTiers) {
            var count = LedgerState.Count(tier: tier);

            if (count > 0) {
                sections[tier.ToString()] = $"- law cases executed: {count}\n{stamp}";
            }
        }

        if (coverage is { } counts) {
            sections["Coverage"] = $"- covered: {counts.Covered}\n- waived: {counts.Waived}\n- uncovered: {counts.Uncovered}\n- total public members: {((counts.Covered + counts.Waived) + counts.Uncovered)}\n{stamp}";
        }

        if (legs is not null) {
            sections["Legs"] = (LegTable(legs: legs) + stamp);
        }

        if (frontier is not null) {
            sections["Frontier"] = (FrontierTable(frontier: frontier) + stamp);
        }

        return sections;
    }
    // Splits a previously written ledger into its `## ` blocks; anything before the first heading is the preamble,
    // which is regenerated rather than retained.
    private static Dictionary<string, string> ParseSections(string text) {
        var sections = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
        var body = new StringBuilder();
        var name = "";

        foreach (var line in text.ReplaceLineEndings(replacementText: "\n").Split(separator: '\n')) {
            if (line.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "## "
            )) {
                Flush(
                    body: body,
                    name: name,
                    sections: sections
                );

                name = line[3..].Trim();

                _ = body.Clear();
            } else if (name.Length > 0) {
                _ = body.Append(value: line).Append(value: '\n');
            }
        }

        Flush(
            body: body,
            name: name,
            sections: sections
        );

        return sections;
    }
    // Regenerates and persists the leg ledger, but only when the leg gate ran: a run that never read the declarations
    // must not restate them from nothing.
    private static IReadOnlyList<LegRow>? PersistLegLedger() {
        if (!LedgerState.LegGatesRan) {
            return null;
        }

        var lawRows = LegLedger.LawRows();

        _ = ArtifactJson.WriteIfChanged(
            path: TestPaths.Output(fileName: "leg-ledger.md"),
            content: LegLedger.Render(lawRows: lawRows)
        );

        return lawRows;
    }
    // Regenerates and persists the coverage manifest, but only when the ratchet gate ran: the manifest is that gate's
    // artifact, so a run that never executed it must not restate the coverage surface.
    private static (int Covered, int Waived, int Uncovered)? PersistManifest() {
        if (!LedgerState.RatchetRan) {
            return null;
        }

        // Generated from the committed manifest, the one the ratchet gate reads, whichever path the run writes.
        var manifest = Coverage.Generate(existing: ArtifactJson.ReadOrDefault<Manifest>(path: TestPaths.Artifact(fileName: "coverage-manifest.json")));

        _ = ArtifactJson.WriteIfChanged(
            path: TestPaths.Output(fileName: "coverage-manifest.json"),
            content: ArtifactJson.Serialize(value: manifest)
        );

        return Coverage.Counts(manifest: manifest);
    }
    // The date alone — the block's staleness, and nothing that a different machine would render differently. See this
    // type's remarks for why no duration rides along.
    private static string Stamp() =>
        $"{StampPrefix}{DateOnly.FromDateTime(dateTime: DateTime.UtcNow):yyyy-MM-dd}\n";
    private static string StripVolatile(string text) =>
        string.Join(
            separator: "\n",
            values: text
                .ReplaceLineEndings(replacementText: "\n")
                .Split(separator: '\n')
                .Where(predicate: static line => !line.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "- last run:"
            ))
        );
    private static void WriteResults(IReadOnlyList<(string Key, int Block, long Index)>? frontier, (int Covered, int Waived, int Uncovered)? coverage, IReadOnlyList<LegRow>? legs) {
        var previous = TestPaths.Previous(fileName: "RESULTS.md");
        var path = TestPaths.Output(fileName: "RESULTS.md");
        var existing = (File.Exists(path: previous)
            ? File.ReadAllText(path: previous)
            : ""
        );
        var content = Merge(
            existing: ParseSections(text: existing),
            fresh: OwnedSections(
                frontier: frontier,
                coverage: coverage,
                legs: legs,
                stamp: Stamp()
            )
        );

        // Change detection ignores the volatile last-run lines: rewrite (refreshing them) only when the deterministic
        // content moved.
        if (StripVolatile(text: existing) == StripVolatile(text: content)) {
            return;
        }

        _ = ArtifactJson.WriteIfChanged(
            content: content,
            path: path
        );
    }

    /// <summary>Finalizes the session: persists the artifacts this run owns, then merges its blocks into the ledger.</summary>
    public void Dispose() {
        var frontier = Frontier.AdvanceAndPersist(lawsPassed: LedgerState.LawsPassed);
        var coverage = PersistManifest();
        var legs = PersistLegLedger();

        WriteResults(
            coverage: coverage,
            frontier: frontier,
            legs: legs
        );
    }
}
