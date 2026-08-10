using Puck.Cli.Canary;

namespace Puck.Cli.Landing;

/// <summary>
/// <c>puck landing</c> — refuses a commit that deletes content its author never worked from.
/// </summary>
/// <remarks>
/// <para>A landing is pushed onto a tip that has moved since the author last rebased. Everything that landed in
/// that window is content the author's tree has never seen, and several ordinary git operations will drop it
/// SILENTLY — most sharply <c>git reset --soft origin/&lt;branch&gt;</c> before a squash, which re-parents the
/// author's OWN tree onto a newer tip: the resulting commit descends from that tip while its tree predates it,
/// which is exactly a revert of everything the tip added.</para>
/// <para>Nothing else in the repository can see this. A build passes, tests pass, and the batteries pass, because
/// they measure the behavior they cover and "did I drop somebody else's landing" is not behavior. Ancestry looks
/// right too — a later <c>git rebase</c> reports "up to date", correctly. The damage exists only in the diff
/// against the tip being landed on, which is the one thing nobody was running.</para>
/// </remarks>
internal static class LandingCommand {
    public static int Run(string[] args) {
        if ((args.Length == 0) || (Array.IndexOf(array: args, value: "-h") >= 0) || (Array.IndexOf(array: args, value: "--help") >= 0)) {
            return Usage();
        }

        if (!TryParse(args: args, against: out var against, baseRef: out var baseRef, error: out var parseError)) {
            Console.Error.WriteLine(value: $"ERROR: {parseError}");

            return 2;
        }

        if (!Git.TryResolve(revision: against, resolved: out var tip, error: out var tipError)) {
            Console.Error.WriteLine(value: $"ERROR: --against '{against}': {tipError}");

            return 2;
        }

        if (baseRef is null) {
            return RefuseMissingBase(tip: tip);
        }

        if (!Git.TryResolve(revision: baseRef, resolved: out var authoringBase, error: out var baseError)) {
            Console.Error.WriteLine(value: $"ERROR: --base '{baseRef}': {baseError}");

            return 2;
        }

        // base == tip is VACUOUS BY ARITHMETIC, not merely unhelpful: the two deletion sets are computed from the
        // same range, so their difference is empty and the verdict is an unconditional PASS that measured nothing.
        // It is also the easiest mistake to make, because a rebase moves your parent TO the tip — so "the commit I
        // rebased onto" and "the commit I am landing onto" become the same hash exactly when the check matters most
        // (post-rebase, looking for work a conflict resolution dropped). Refused rather than documented: a vacuous
        // pass is worse than no check, since it reports success.
        //
        // DO NOT OVERCLAIM THIS EITHER. It does not catch the re-parenting shape this verb was written for — there
        // the base is a genuine distinct ancestor, so the pairing is unequal and this refusal never fires. It closes
        // one arithmetic hole; the required-and-refused base design is what closes the other.
        if (string.Equals(a: authoringBase, b: tip, comparisonType: StringComparison.Ordinal)) {
            Console.Error.WriteLine(value: $"ERROR: --base and --against are the same commit ({tip[..12]}).");
            Console.Error.WriteLine(value: string.Empty);
            Console.Error.WriteLine(value: "  That comparison is vacuous: both deletion sets come from the same range, so their");
            Console.Error.WriteLine(value: "  difference is empty and the result would be an unconditional PASS measuring nothing.");
            Console.Error.WriteLine(value: string.Empty);
            Console.Error.WriteLine(value: "  --base is the commit your work was AUTHORED FROM, which after a rebase is NOT your new");
            Console.Error.WriteLine(value: "  parent — the rebase moved that to the tip. Use the ORIGINAL base you branched from.");
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
        if (!Git.IsAncestor(candidate: authoringBase, descendant: "HEAD")) {
            Console.Error.WriteLine(value: $"ERROR: --base {authoringBase[..12]} is not an ancestor of HEAD.");
            Console.Error.WriteLine(value: string.Empty);
            Console.Error.WriteLine(value: "  Your work cannot have been built on a commit it does not descend from, so the two");
            Console.Error.WriteLine(value: "  diffs would compare unrelated histories. Check for a typo or a stale hash.");

            return 2;
        }

        // The whole check, in two diffs. What the author MEANT to delete is what their work deletes relative to the
        // tree they actually worked from; anything else the landing removes came from a commit they never had.
        var againstTip = LandingDiff.DeletedLines(from: tip, to: "HEAD");
        var againstBase = LandingDiff.DeletedLines(from: authoringBase, to: "HEAD");
        var unaccounted = LandingDiff.Subtract(left: againstTip, right: againstBase);

        if (unaccounted.Count != 0) {
            return Refuse(tip: tip, authoringBase: authoringBase, unaccounted: unaccounted);
        }

        ReportGitAccept(tip: tip, authoringBase: authoringBase, intended: againstBase);

        // HEAD-BINDING HOLE, deliberately not solved here: this source integration cannot prove the invoked puck
        // assembly was built from the checkout's HEAD. A stale published puck.exe can retain the old git-only
        // landing command and never enter this code, so external enforcement must bind the executable to HEAD.
        // Recording that limit is in scope; adding a second bootstrap/update mechanism is not.
        var canaryExit = CanaryCommand.Run(args: []);
        if (canaryExit == 0) {
            Console.WriteLine(value: $"PASS: landing accepted — git-loss check and automatic canaries passed given base {authoringBase[..12]}.");
            Console.WriteLine(value: "      The git component proves the landing only relative to what you say you built on.");

            return 0;
        }
        if (canaryExit == 1) {
            Console.Error.WriteLine(value: "FAIL: landing rejected by the automatic canary component after the git-loss check passed.");

            return 1;
        }

        Console.Error.WriteLine(value: "ERROR: landing refused by the automatic canary component after the git-loss check passed.");

        return 2;
    }

    // A base cannot be GUESSED. The obvious automatic answer — merge-base(tip, HEAD) — is worse than none: for the
    // failure this verb exists to catch it returns the tip itself, so the two diffs become identical, every deletion
    // reads as intended, and the verb blesses the exact bug with a green light on top. The reflog knows the real
    // answer, so it is offered as a SUGGESTION the human confirms, never as a default the tool assumes.
    private static int RefuseMissingBase(string tip) {
        Console.Error.WriteLine(value: "ERROR: --base is required.");
        Console.Error.WriteLine(value: string.Empty);
        Console.Error.WriteLine(value: "  The base is the commit your work was AUTHORED FROM — for rebased work the ORIGINAL base,");
        Console.Error.WriteLine(value: "  never the new parent a rebase gave you (that pairing is vacuous and is refused).");
        Console.Error.WriteLine(value: "  It is NOT derivable: merge-base(tip, HEAD) returns the tip for a re-parented tree, which");
        Console.Error.WriteLine(value: "  makes every deletion look intended and hides precisely the failure this verb catches.");
        Console.Error.WriteLine(value: string.Empty);

        if (LandingReflog.TrySuggestBase(suggestion: out var suggestion, when: out var when)) {
            Console.Error.WriteLine(value: $"  Your reflog's most recent rebase base is {suggestion} ({when}).");
            Console.Error.WriteLine(value: $"  If that is the tree you worked from:  puck landing --against {tip[..12]} --base {suggestion}");
        } else {
            Console.Error.WriteLine(value: "  No rebase entry was found in this branch's reflog; supply the base yourself.");
        }

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
        Console.WriteLine(value: $"landing git component: clean given base {authoringBase[..12]}; running the automatic canary set next.");
    }

    // The refusal names the COMMITS whose content is being dropped, not just the lines: "you deleted 306 lines" is a
    // puzzle, "you are dropping <hash> The mouse learns to pick things up" is an instruction.
    private static int Refuse(string tip, string authoringBase, IReadOnlyDictionary<string, List<string>> unaccounted) {
        var lines = 0;

        Console.Error.WriteLine(value: $"landing: HEAD onto {tip[..12]}, authored from {authoringBase[..12]}.");
        Console.Error.WriteLine(value: string.Empty);

        foreach (var entry in unaccounted.OrderBy(keySelector: static entry => entry.Key, comparer: StringComparer.Ordinal)) {
            lines += entry.Value.Count;

            Console.Error.WriteLine(value: $"  {entry.Key}: {entry.Value.Count} unaccounted deletion(s)");

            foreach (var commit in LandingDiff.CommitsBetween(from: authoringBase, to: tip, path: entry.Key)) {
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
        Console.Error.WriteLine(value: "      Rebase onto the tip and re-apply your work, or (if the tree was re-parented) rebuild it:");
        Console.Error.WriteLine(value: "      base a tree on the tip, apply diff(base..HEAD), and commit that.");

        return 1;
    }

    private static string Clip(string text) {
        var trimmed = text.Trim();

        return ((trimmed.Length <= 100) ? trimmed : (trimmed[..100] + "…"));
    }

    private static bool TryParse(string[] args, out string against, out string? baseRef, out string error) {
        against = string.Empty;
        baseRef = null;
        error = string.Empty;

        for (var index = 0; (index < args.Length); index++) {
            switch (args[index]) {
                case "--against":
                case "--base": {
                    var name = args[index];

                    if ((index + 1) >= args.Length) {
                        error = $"{name} needs a value.";

                        return false;
                    }

                    if (name == "--against") {
                        against = args[++index];
                    } else {
                        baseRef = args[++index];
                    }

                    break;
                }
                default:
                    error = $"unknown argument '{args[index]}'.";

                    return false;
            }
        }

        if (against.Length == 0) {
            error = "--against <tip> is required (the commit you are landing onto, e.g. origin/main).";

            return false;
        }

        return true;
    }

    private static int Usage() {
        Console.Error.WriteLine(
            value:
                """
                landing --against <tip> --base <ref>   refuse a commit that drops someone else's landing

                  --against <tip>   the commit you are landing ONTO (e.g. origin/features/x)
                  --base <ref>      the commit your work was AUTHORED FROM. For rebased work this is the
                                    ORIGINAL base, never the new parent: a rebase moves your parent to
                                    the tip, so passing the parent makes --base and --against equal and
                                    the check vacuous. That pairing is refused.
                  -h / --help       this text

                Compares the lines HEAD deletes relative to <tip> against the lines it deletes relative
                to <base>. The first set is what the push would remove; the second is what your own work
                removes. Anything in the first and not the second arrived on the tip while you were
                working and would be silently lost.

                There is no ignore list and no override, deliberately: deleting someone's landing ON
                PURPOSE means having rebased onto it, which puts it in <base>, which accounts for the
                deletion automatically. Intent is derived, never declared.

                <base> is required. It cannot be guessed — merge-base(tip, HEAD) returns the tip itself
                for a re-parented tree, which would make every deletion look intended. A <base> equal
                to <tip>, or one HEAD does not descend from, is refused for the same reason: both would
                report a PASS that measured nothing.

                After every git check passes, runs the nonempty automatic canary set. There is no skip flag.

                Exit codes: 0 both components passed, 1 unaccounted deletions or observed canary failure,
                2 usage, manifest, build, or canary infrastructure refusal.
                """);

        return 2;
    }
}
