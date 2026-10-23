using System.CommandLine;

namespace Puck.Cli.Landing;

/// <summary>
/// <c>puck landing</c> — refuses a commit that deletes content its author never worked from.
/// </summary>
/// <remarks>The check compares deletions against an explicitly supplied authoring base. A merge or squash
/// can have valid ancestry while dropping content; the diff, not ancestry alone, exposes that loss.</remarks>
public static class LandingCommand {
    private static string Clip(string text) {
        var trimmed = text.Trim();

        return ((trimmed.Length <= 100)
            ? trimmed
            : (trimmed[..100] + "…")
        );
    }
    // The refusal names the COMMITS whose content is being dropped, not just the lines: "you deleted 306 lines" is a
    // puzzle, "you are dropping <hash> The mouse learns to pick things up" is an instruction.
    private static int Refuse(string tip, string authoringBase, IReadOnlyDictionary<string, List<string>> unaccounted) {
        var lines = 0;

        Console.Error.WriteLine(value: $"landing: HEAD onto {tip[..12]}, authored from {authoringBase[..12]}.");
        Console.Error.WriteLine(value: string.Empty);

        foreach (var entry in unaccounted.OrderBy(
            keySelector: static entry => entry.Key,
            comparer: StringComparer.Ordinal
        )) {
            lines += entry.Value.Count;

            Console.Error.WriteLine(value: $"  {entry.Key}: {entry.Value.Count} unaccounted deletion(s)");

            foreach (var commit in LandingDiff.CommitsBetween(
                from: authoringBase,
                to: tip,
                path: entry.Key
            )) {
                Console.Error.WriteLine(value: $"      dropping work from {commit}");
            }

            foreach (var line in entry.Value.Take(count: 3)) {
                Console.Error.WriteLine(value: $"      - {Clip(text: line)}");
            }

            if (entry.Value.Count > 3) {
                Console.Error.WriteLine(value: $"      … and {(entry.Value.Count - 3)} more");
            }
        }

        Console.Error.WriteLine(value: string.Empty);
        Console.Error.WriteLine(value: $"FAIL: {lines} line(s) exist on the tip, are not deleted by your own change set, and would be lost.");
        Console.Error.WriteLine(value: "      Preserve the authoring base, merge the integration tip, and resolve the lost content.");
        Console.Error.WriteLine(value: "      For a re-parented tree, reconstruct from the tip and the intended change; inspect every deletion.");

        return 1;
    }
    // A base cannot be GUESSED. The obvious automatic answer — merge-base(tip, HEAD) — is worse than none: for the
    // failure this verb exists to catch it returns the tip itself, so the two diffs become identical, every deletion
    // reads as intended, and the verb blesses the exact bug with a green light on top. The caller must preserve
    // the authoring base or establish it from branch history and reflog; this verb never guesses it.
    private static int RefuseMissingBase(string tip) {
        Console.Error.WriteLine(value: "ERROR: --base is required.");
        Console.Error.WriteLine(value: string.Empty);
        Console.Error.WriteLine(value: "  --base is the commit your work was AUTHORED FROM. Preserve it across integration merges.");
        Console.Error.WriteLine(value: "  Do not substitute the new parent of a reconstructed or squashed tree.");
        Console.Error.WriteLine(value: "  merge-base(tip, HEAD) cannot recover the authoring base of a re-parented tree.");
        Console.Error.WriteLine(value: $"  Inspect branch history and reflog, then run puck landing --against {tip[..12]} --base <authoring-base>.");

        return 2;
    }
    private static void ReportGitAccept(string tip, string authoringBase, IReadOnlyDictionary<string, List<string>> intended) {
        var files = 0;
        var lines = 0;

        foreach (var entry in intended) {
            files++;
            lines += entry.Value.Count;
        }

        Console.WriteLine(value: $"landing: HEAD onto {tip[..12]}, authored from {authoringBase[..12]}.");
        Console.WriteLine(value: $"landing: {lines} deleted line(s) across {files} file(s) — every one accounted for by this landing's own change set.");
        Console.WriteLine(value: $"landing: clean given authoring base {authoringBase[..12]}.");
    }
    private static int Run(string against, string? baseRef) {
        if (!CliGit.TryResolveCommit(
            repository: ".",
            resolved: out var tip,
            revision: against
        )) {
            Console.Error.WriteLine(value: $"ERROR: --against '{against}': unknown revision (not a commit this repository carries).");

            return 2;
        }

        if (baseRef is null) {
            return RefuseMissingBase(tip: tip);
        }

        if (!CliGit.TryResolveCommit(
            repository: ".",
            resolved: out var authoringBase,
            revision: baseRef
        )) {
            Console.Error.WriteLine(value: $"ERROR: --base '{baseRef}': unknown revision (not a commit this repository carries).");

            return 2;
        }

        // Equal endpoints compare the same deletion set and would measure nothing.
        if (string.Equals(
            a: authoringBase,
            b: tip,
            comparisonType: StringComparison.Ordinal
        )) {
            Console.Error.WriteLine(value: $"ERROR: --base and --against are the same commit ({tip[..12]}).");
            Console.Error.WriteLine(value: string.Empty);
            Console.Error.WriteLine(value: "  That comparison is vacuous: both deletion sets come from the same range, so their");
            Console.Error.WriteLine(value: "  difference is empty and the result would be an unconditional PASS measuring nothing.");
            Console.Error.WriteLine(value: string.Empty);
            Console.Error.WriteLine(value: "  --base is the commit your work was AUTHORED FROM. Preserve it across integration merges;");
            Console.Error.WriteLine(value: "  a new parent after reconstruction or squash does not establish that authoring base.");
            Console.Error.WriteLine(value: "  If the tip genuinely has not moved since you branched, there is nothing to check:");
            Console.Error.WriteLine(value: "  no landing arrived that your work could drop.");

            return 2;
        }

        // A base HEAD does not descend from is not a tree this work was built on — a typo, a stale hash, or a
        // sibling branch's tip. The two diffs would then be measuring unrelated histories and the verdict would be
        // noise rather than an answer.
        //
        // DO NOT OVERCLAIM THIS CHECK. It would NOT have caught the failure this verb was written for: re-parenting
        // makes the landing tip a genuine ancestor of the defective commit, so a base of "the tip itself" passes
        // ancestry and still blesses the bug (see RefuseMissingBase). This catches wrong bases, not dishonest ones.
        // The verdict remains conditional on an honestly-supplied base, which is inherent — the tool cannot know
        // what you built on except by asking, and that conditionality is stated in the PASS line rather than hidden.
        if (!CliGit.IsAncestor(
            candidate: authoringBase,
            descendant: "HEAD",
            repository: "."
        )) {
            Console.Error.WriteLine(value: $"ERROR: --base {authoringBase[..12]} is not an ancestor of HEAD.");
            Console.Error.WriteLine(value: string.Empty);
            Console.Error.WriteLine(value: "  Your work cannot have been built on a commit it does not descend from, so the two");
            Console.Error.WriteLine(value: "  diffs would compare unrelated histories. Check for a typo or a stale hash.");

            return 2;
        }

        // The whole check, in two diffs. What the author MEANT to delete is what their work deletes relative to the
        // tree they actually worked from; anything else the landing removes came from a commit they never had.
        var againstTip = LandingDiff.DeletedLines(
            from: tip,
            to: "HEAD"
        );
        var againstBase = LandingDiff.DeletedLines(
            from: authoringBase,
            to: "HEAD"
        );
        var unaccounted = LandingDiff.Subtract(
            left: againstTip,
            right: againstBase
        );

        if (unaccounted.Count != 0) {
            return Refuse(
                authoringBase: authoringBase,
                tip: tip,
                unaccounted: unaccounted
            );
        }

        ReportGitAccept(
            authoringBase: authoringBase,
            intended: againstBase,
            tip: tip
        );

        Console.WriteLine(value: $"PASS: landing accepted — every deletion accounted for given base {authoringBase[..12]}.");
        Console.WriteLine(value: "      This proves the landing only relative to what you say you built on.");
        return 0;
    }

    public static Command Create() {
        var againstOption = new Option<string>(name: "--against") { Description = "The commit you are landing ONTO (e.g. origin/features/x).", Required = true };
        // Optional at the parser, refused with authoring-base guidance at the verb: the refusal needs the resolved tip
        // to print a runnable command, which only exists after --against resolves.
        var baseOption = new Option<string>(name: "--base") { Description = "The commit your work was AUTHORED FROM, preserved across integration merges. A new parent after squash or reconstruction is not that base. Equal --base and --against are refused." };
        var command = new Command(
            description: "Refuse a commit that drops someone else's landing.",
            name: "landing"
        ) { againstOption, baseOption };

        command.Detail(detail: """
            Compares the lines HEAD deletes relative to --against (the tip you land onto) with the
            lines it deletes relative to --base (the commit your work was authored from). The first
            set is what the push would remove; the second is what your own work removes. Anything
            in the first and not the second arrived on the tip while you were working and would be
            silently lost.

            Preserve the actual authoring base across merges; never substitute a moved remote tip.
            Merge the integration tip and inspect every deletion. If the tree was re-parented,
            reconstruct the intended change from its original base before landing it.

            --base is required. It cannot be guessed: merge-base(tip, HEAD) returns the tip itself
            for a re-parented tree, which would make every deletion look intended. A --base equal
            to --against, or one HEAD does not descend from, is refused for the same reason: both
            would report a PASS that measured nothing.

            This command reads Git state only; it builds nothing and boots no World.

            Exit codes: 0 deletions accounted for; 1 unaccounted deletions; 2 invalid input or Git refusal.
            """);

        command.SetAction(action: parseResult => Run(
            against: parseResult.GetRequiredValue(option: againstOption),
            baseRef: parseResult.GetValue(option: baseOption)
        ));
        return command;
    }
}
