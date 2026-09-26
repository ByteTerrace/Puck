using System.Globalization;

namespace Puck.Cli.Canary;

internal static partial class CanaryCommand {
    // The quit answer (TerminalCommandModule) a World writes to stdout once the runner-owned quit that closes every
    // leg's script has run: the observation that a leg ended at its script's end rather than at a ceiling.
    private const string QuitAnswer = "[quit: exiting]";

    /// <summary>What one proof costs, counted from its manifest alone.</summary>
    /// <param name="Proof">The proof.</param>
    /// <param name="WorldBoots">The World processes its two legs boot, including those a stub launcher starts.</param>
    /// <param name="WorldProcesses">The World processes the runner starts itself.</param>
    /// <param name="StubLaunches">The <c>Puck.Launcher.Stub</c> processes the runner starts.</param>
    /// <param name="Exclusive">Whether its legs hold every <c>--jobs</c> slot, and so run strictly one at a time.</param>
    /// <param name="BudgetSeconds">The whole timeout its two legs may spend, counting every boot.</param>
    internal sealed record CanaryProofPlan(CanaryProof Proof, int WorldBoots, int WorldProcesses, int StubLaunches, bool Exclusive, int BudgetSeconds);
    /// <summary>What a selection costs, counted from its manifests without building or running anything.</summary>
    /// <param name="Proofs">Every proof, in authored order.</param>
    /// <param name="PackageSpawns">The <c>shaders package</c> processes the run starts: one per distinct package
    /// source a selected leg names, however many legs share it (CanaryPackages).</param>
    /// <param name="WorldBuilds">The <c>Puck.World</c> builds the run may make: at most one, none when the store
    /// already holds the checkout's source state or <c>--world-artifact</c> names the World.</param>
    /// <param name="StubBuilds">The <c>Puck.Launcher.Stub</c> builds the run makes.</param>
    internal sealed record CanaryPlan(IReadOnlyList<CanaryProofPlan> Proofs, int PackageSpawns, int WorldBuilds, int StubBuilds) {
        public int BudgetSeconds => Proofs.Sum(selector: static proof => proof.BudgetSeconds);
        public int ExclusiveLegs => Proofs.Where(predicate: static proof => proof.Exclusive).Sum(selector: static _ => 2);
        public int LegSpawns => ((WorldProcesses + StubLaunches) + PackageSpawns);
        public int Legs => (Proofs.Count * 2);
        public int StubLaunches => Proofs.Sum(selector: static proof => proof.StubLaunches);
        public int WorldBoots => Proofs.Sum(selector: static proof => proof.WorldBoots);
        public int WorldProcesses => Proofs.Sum(selector: static proof => proof.WorldProcesses);
    }

    /// <summary>Counts what running <paramref name="manifests"/> costs.</summary>
    /// <param name="manifests">The selected manifests, in authored order.</param>
    /// <param name="namedWorldArtifact">Whether <c>--world-artifact</c> names the World, so nothing builds it.</param>
    /// <param name="backends">The backends the run boots its backend-declaring proofs on
    /// (<see cref="SelectBackends"/>).</param>
    /// <returns>The plan.</returns>
    internal static CanaryPlan Plan(IReadOnlyList<CanaryManifest> manifests, bool namedWorldArtifact, IReadOnlyList<string> backends) {
        var proofs = ExpandProofs(
            backends: backends,
            manifests: manifests
        ).Select(selector: static proof => {
            var manifest = proof.Manifest;
            var legs = ((CanaryLeg[])[manifest.Positive, manifest.Discriminating]);
            var stub = (manifest.BootShape == CanaryBootShape.Stub);

            return new CanaryProofPlan(
                BudgetSeconds: ProofBudgetSeconds(manifest: manifest),
                Exclusive: IsExclusive(manifest: manifest),
                Proof: proof,
                // A stub leg's second launch always boots a World; the positive leg's first launch boots the
                // baseline, and the discriminating leg's is refused before any World starts (RunStubLeg).
                StubLaunches: (stub
                    ? 4
                    : 0),
                WorldBoots: (stub
                    ? 3
                    : legs.Sum(selector: LegWorldProcesses)),
                WorldProcesses: (stub
                    ? 0
                    : legs.Sum(selector: LegWorldProcesses))
            );
        }).ToArray();

        return new CanaryPlan(
            PackageSpawns: manifests.SelectMany(selector: static manifest => ((CanaryLeg[])[manifest.Positive, manifest.Discriminating]))
                .Where(predicate: static leg => (leg.Package is not null))
                .Select(selector: static leg => Path.GetFullPath(path: leg.Package!.SourcePath))
                .Distinct(comparer: Puck.Abstractions.PuckPaths.Comparer)
                .Count(),
            Proofs: proofs,
            StubBuilds: (manifests.Any(predicate: static manifest => (manifest.BootShape == CanaryBootShape.Stub))
                ? 1
                : 0),
            WorldBuilds: (namedWorldArtifact
                ? 0
                : 1)
        );
    }

    // The World processes one non-stub leg starts: every listener of an authorities leg, or its own World plus a
    // companion authority and a relaunch when it declares them.
    private static int LegWorldProcesses(CanaryLeg leg) => ((leg.Authorities.Count != 0)
        ? leg.Authorities.Count
        : ((1 + ((leg.AuthorityWorldPath is null)
            ? 0
            : 1)) + ((leg.Relaunch is null)
            ? 0
            : 1))
    );

    // Every leg of an exclusive manifest holds every --jobs slot: its GPU, window, audio device, or input hardware is
    // shared by the whole machine, so no other leg may run beside it. Every environmental requirement names such a
    // device; a headless or stub proof with none runs beside others, each in its own run and state directories.
    internal static bool IsExclusive(CanaryManifest manifest) =>
        ((manifest.BootShape is CanaryBootShape.Windowed or CanaryBootShape.Offscreen) || (manifest.Requirements.Count != 0));

    // A leg runs under the whole timeout its manifest declares for every boot: a stub leg observes two launches, a
    // relaunching leg boots twice, and when either leg relaunches both are counted as relaunching.
    private static int ProofBudgetSeconds(CanaryManifest manifest) =>
        ((((manifest.BootShape == CanaryBootShape.Stub)
            ? 2
            : 1) * (RelaunchesLeg(manifest: manifest)
            ? 4
            : 2)) * manifest.TimeoutSeconds);
    // The ceiling a selection is held to, or null when it names its proofs explicitly or is not a gate.
    private static CanaryCeiling? CeilingOf(CanarySelection selection) => selection.Kind switch {
        CanarySelectionKind.Automatic => CanaryCeilings.Automatic,
        CanarySelectionKind.Capability when (selection.Capability == "automatic") => CanaryCeilings.Automatic,
        CanarySelectionKind.Merge => CanaryCeilings.Merge,
        _ => null,
    };
    private static string CeilingName(CanarySelection selection) => ((selection.Kind == CanarySelectionKind.Merge)
        ? nameof(CanaryCeilings.Merge)
        : nameof(CanaryCeilings.Automatic)
    );

    /// <summary>Names how <paramref name="plan"/> exceeds <paramref name="ceiling"/>, or returns <see langword="null"/>
    /// when it fits.</summary>
    /// <param name="plan">The selection's plan.</param>
    /// <param name="ceiling">The ceiling it is held to.</param>
    /// <param name="name">The ceiling's member of <see cref="CanaryCeilings"/>.</param>
    /// <returns>The refusal, or <see langword="null"/>.</returns>
    internal static string? CeilingRefusal(CanaryPlan plan, CanaryCeiling ceiling, string name) {
        var over = new List<string>(capacity: 2);

        if (plan.WorldBoots > ceiling.WorldBoots) {
            over.Add(item: $"{plan.WorldBoots} World boots against {ceiling.WorldBoots}");
        }
        if (plan.BudgetSeconds > ceiling.LegBudgetSeconds) {
            over.Add(item: $"a {plan.BudgetSeconds}-second leg budget against {ceiling.LegBudgetSeconds}");
        }

        return ((over.Count == 0)
            ? null
            : $"the selection plans {string.Join(separator: " and ", values: over)}, over its declared ceiling CanaryCeilings.{name} (src/Puck.Cli/Canary/CanaryCeilings.cs). Cut the set, or raise the ceiling in a reviewed change that states the new --plan counts.");
    }

    // Prints the plan on stdout, one proof per line in authored order, then the totals. Every value is a count of the
    // manifests, so two runs over the same selection print the same text on any machine.
    private static void PrintPlan(CanaryPlan plan, CanaryCeiling? ceiling, string? ceilingName) {
        foreach (var proof in plan.Proofs) {
            Console.WriteLine(value: $"canary plan {proof.Proof.Label}: legs=2 world-boots={proof.WorldBoots} spawns={(proof.WorldProcesses + proof.StubLaunches)} budget={proof.BudgetSeconds}s {(proof.Exclusive ? "serial" : "parallel")}");
        }

        Console.WriteLine(value: $"canary plan: {plan.Proofs.Count} proof(s), {plan.Legs} leg(s): {plan.ExclusiveLegs} serial, {(plan.Legs - plan.ExclusiveLegs)} parallel up to --jobs.");

        if (BackendScope(proofs: [.. plan.Proofs.Select(selector: static proof => proof.Proof)]) is { } scope) {
            Console.WriteLine(value: $"canary plan: backend-declaring proofs run {scope}.");
        }

        Console.WriteLine(value: $"canary plan: {plan.WorldBoots} World boot(s); {plan.LegSpawns} leg process spawn(s): {plan.WorldProcesses} World, {plan.StubLaunches} Puck.Launcher.Stub, {plan.PackageSpawns} shaders package.");
        Console.WriteLine(value: $"canary plan: build(s): Puck.World at most {plan.WorldBuilds} (none when the store holds this source state), Puck.Launcher.Stub {plan.StubBuilds}.");
        Console.WriteLine(value: $"canary plan: every leg ends at the quit the runner appends to its script; {plan.BudgetSeconds.ToString(provider: CultureInfo.InvariantCulture)}s of summed per-leg timeouts is the kill ceiling, not the length.");

        if (ceiling is not null) {
            Console.WriteLine(value: $"canary plan: ceiling CanaryCeilings.{ceilingName}: {ceiling.WorldBoots} World boot(s), {ceiling.LegBudgetSeconds}s leg budget.");
        }
    }

    /// <summary>What a run actually started and how its legs ended, counted as it goes; every member is updated from
    /// the legs' own threads.</summary>
    internal sealed class CanaryTally {
        private int m_packageSpawns;
        private int m_stubLaunches;
        private int m_stubWorldBoots;
        private int m_worldProcesses;

        public int PackageSpawns => Volatile.Read(location: ref m_packageSpawns);
        public int StubLaunches => Volatile.Read(location: ref m_stubLaunches);
        public int StubWorldBoots => Volatile.Read(location: ref m_stubWorldBoots);
        public int WorldProcesses => Volatile.Read(location: ref m_worldProcesses);

        public void PackageSpawned() => Interlocked.Increment(location: ref m_packageSpawns);
        // A stub launch counts as a World boot too when the World it started announced its --world origin.
        public void StubLaunched(CliProcessResult result) {
            Interlocked.Increment(location: ref m_stubLaunches);
            if (SplitLines(text: result.Stderr).Any(predicate: static line => line.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "[world] definition: "
            ))) {
                Interlocked.Increment(location: ref m_stubWorldBoots);
            }
        }
        public void WorldStarted() => Interlocked.Increment(location: ref m_worldProcesses);
    }

    // How one reported leg ended: at its script's runner-owned quit, killed at its timeout, or neither (it never ran,
    // or its process ended before its script did).
    private enum CanaryLegEnding {
        ScriptEnd,
        Timeout,
        Other,
    }

    private static CanaryLegEnding EndingOf(CanaryLegRun leg) => (leg.TimedOut
        ? CanaryLegEnding.Timeout
        : (leg.Transcript.Stdout.Any(predicate: static line => string.Equals(
            a: line,
            b: QuitAnswer,
            comparisonType: StringComparison.Ordinal
        ))
            ? CanaryLegEnding.ScriptEnd
            : CanaryLegEnding.Other)
    );
    // The run's measured counts, in the same terms PrintPlan uses, so the two outputs compare line for line.
    private static void PrintTally(CanaryPlan plan, CanaryTally tally, bool built, IReadOnlyList<CanaryLegEnding> endings) {
        var boots = (tally.WorldProcesses + tally.StubWorldBoots);
        var spawns = ((tally.WorldProcesses + tally.StubLaunches) + tally.PackageSpawns);

        Console.WriteLine(value: $"canary counts: {boots} World boot(s) (planned {plan.WorldBoots}); {spawns} leg process spawn(s) (planned {plan.LegSpawns}): {tally.WorldProcesses} World, {tally.StubLaunches} Puck.Launcher.Stub, {tally.PackageSpawns} shaders package; build(s): Puck.World {(built ? 1 : 0)}, Puck.Launcher.Stub {plan.StubBuilds}.");
        Console.WriteLine(value: $"canary counts: {endings.Count} leg(s) reported: {endings.Count(predicate: static ending => (ending == CanaryLegEnding.ScriptEnd))} ended at their script's quit, {endings.Count(predicate: static ending => (ending == CanaryLegEnding.Timeout))} at their timeout, {endings.Count(predicate: static ending => (ending == CanaryLegEnding.Other))} otherwise.");
    }
}
