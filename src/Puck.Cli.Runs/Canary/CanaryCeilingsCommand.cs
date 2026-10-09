using System.CommandLine;
using System.Text;

namespace Puck.Cli.Canary;

/// <summary><c>puck canary-ceilings</c>: writes <c>CanaryCeilings.json</c> from the plans of the checked-in canary
/// manifests, or under <c>--check</c> fails when a plan differs from the recorded ledger.</summary>
public static class CanaryCeilingsCommand {
    private const string Verb = "canary-ceilings";

    /// <summary>Plans the two gate selections of the checked-in manifests.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <param name="planned">The ledger recording exactly those plans.</param>
    /// <param name="error">Why the manifests cannot be planned, or empty.</param>
    /// <returns><see langword="true"/> when every manifest loaded, so the plans are whole.</returns>
    public static bool TryPlan(string repositoryRoot, out CanaryCeilingsLedger? planned, out string error) {
        planned = null;

        if (!CanaryManifestLoader.TryLoadAll(
            error: out error,
            manifests: out var manifests,
            refused: out var refused,
            repositoryRoot: repositoryRoot,
            strict: false
        )) {
            return false;
        }
        if (refused.Count != 0) {
            error = $"{refused.Count} manifest(s) were refused ({string.Join(separator: ", ", values: refused.Select(selector: static pair => pair.Directory))}); a plan over a partial set would record a falsely low cost.";

            return false;
        }

        var automatic = CanaryCommand.Plan(
            backends: WorldOffscreenLeg.Backends,
            manifests: [.. manifests.Where(predicate: static manifest => manifest.IsAutomatic)],
            namedWorldArtifact: false
        );
        var merge = CanaryCommand.Plan(
            backends: WorldOffscreenLeg.Backends,
            manifests: CanaryCommand.SelectMerge(manifests: manifests),
            namedWorldArtifact: false
        );

        planned = CanaryCeilingsLedger.Of(
            automatic: automatic,
            merge: merge
        );

        return true;
    }

    private static int Run(bool check) {
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return CliExit.Refused;
        }
        if (!TryPlan(
            error: out var planError,
            planned: out var planned,
            repositoryRoot: repositoryRoot
        )) {
            return CliExit.Refuse(
                verb: Verb,
                what: "the canary manifests",
                why: planError
            );
        }

        if (!check) {
            File.WriteAllText(
                contents: planned!.Render(),
                encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                path: Path.Combine(
                    path1: repositoryRoot,
                    path2: CanaryCeilingsLedger.FileName
                )
            );
            Console.WriteLine(value: $"{Verb}: wrote {CanaryCeilingsLedger.FileName} — automatic {planned.Automatic.WorldBoots} World boot(s), {planned.Automatic.LegBudgetSeconds}s; merge {planned.Merge.WorldBoots} World boot(s), {planned.Merge.LegBudgetSeconds}s.");

            return CliExit.Success;
        }
        if (!CanaryCeilingsLedger.TryRead(
            error: out var readError,
            ledger: out var recorded,
            repositoryRoot: repositoryRoot,
            text: out var text
        )) {
            return CliExit.Refuse(
                verb: Verb,
                what: CanaryCeilingsLedger.FileName,
                why: readError
            );
        }

        var problems = recorded!.Check(
            ledgerText: text,
            planned: planned!
        );

        foreach (var problem in problems) {
            Console.Error.WriteLine(value: $"{Verb}: {problem}");
        }

        if (problems.Count != 0) {
            Console.Error.WriteLine(value: $"{Verb}: run 'puck {Verb}' and commit {CanaryCeilingsLedger.FileName}; a rise is a deliberate change, so state the new 'puck canary --merge --plan' counts in the commit.");
        }

        Console.WriteLine(value: $"{Verb}: {problems.Count} problem(s); recorded automatic {recorded.Automatic.WorldBoots}/{recorded.Automatic.LegBudgetSeconds}s, merge {recorded.Merge.WorldBoots}/{recorded.Merge.LegBudgetSeconds}s.");

        return ((problems.Count == 0)
            ? CliExit.Success
            : CliExit.Failed);
    }

    /// <summary>Creates <c>puck canary-ceilings</c>.</summary>
    /// <returns>The command.</returns>
    public static Command Create() {
        var command = CliOptions.CheckVerb(
            checkDescription: "Write nothing; exit 1 when the recorded ceilings are not exactly what the manifests plan.",
            description: "Record the cost of the canary gate selections in CanaryCeilings.json, or check it.",
            name: Verb,
            run: Run
        );

        command.Detail(detail: """
            Records the World boots and summed leg budget of the two gate selections of `puck
            canary` (the automatic set and --merge), as `puck canary --plan` counts them over
            the checked-in manifests, in CanaryCeilings.json at the repository root. A run or
            --plan of a gate selection whose plan exceeds its recorded cost is refused before
            anything builds.

            By default the ledger is written from the plans. --check writes nothing and exits 1
            when a recorded count differs from its plan in either direction, or when the ledger's
            bytes differ from what the verb writes. A rise is a deliberate recorded change; a
            fall is recorded too, so freed headroom is not spendable by another lane without a
            reviewed change, and a clean textual merge of two lanes' identical records still
            fails the check because the merged manifests plan more than either recorded.

            Exit codes: 0 written, or the ledger holds; 1 drift under --check; 2 an unreadable
            ledger or manifest set.
            """);

        return command;
    }
}
