using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;

namespace Puck.Cli.Canary;

/// <summary><c>puck canary</c> — bounded, two-leg behavioral proofs against the real Puck.World executable.</summary>
internal static partial class CanaryCommand {
    // The one-time Release builds run against their own allowance on their own clock, so build time can never be
    // spent out of a proof's. Leg time is a separate budget derived from the selection itself; see CanaryBudget.
    private static readonly TimeSpan BuildBudget = TimeSpan.FromSeconds(value: 600);

    private const string ScratchPrefix = "puck-canary-";

    // Every leg's script ends with the runner's own two lines: wire.errors, the exact-count terminal observation, and
    // quit, which ends the session once everything queued ahead of it has run. A leg therefore lasts as long as its
    // script and no longer; its manifest's timeoutSeconds is only the ceiling a hung leg is killed at.
    private static readonly string RunnerQuit = $"quit{Environment.NewLine}";

    public static Command Create() {
        var allOption = new Option<bool>(name: "--all") { Description = "Explicitly run every proof, including any declared environmental requirements; it does not promote any proof into the automatic set." };
        var backendOption = new Option<string?>(name: "--backend") { Description = "Run every backend-declaring proof on this backend only: vulkan or directx. Both run when omitted, and the verdict names the backends that ran." };
        var capabilityOption = new Option<string>(name: "--capability") { Description = "Filter by automatic, headless, windowed, offscreen, gpu, audio-output, or input:<hardware-name>." };
        var idsArgument = new Argument<string[]>(name: "id") { Arity = ArgumentArity.ZeroOrMore, DefaultValueFactory = static _ => [], Description = "Run the named proofs explicitly, regardless of their declared requirements." };
        var listOption = new Option<bool>(name: "--list") { Description = "Strictly load and list the named manifests, or every manifest when no id is given, without building or running." };
        var mergeOption = new Option<bool>(name: "--merge") { Description = "Run what a merge needs: the automatic set plus every proof requiring gpu." };
        var jobsOption = new Option<int>(name: "--jobs") {
            DefaultValueFactory = static _ => DefaultJobs,
            Description = "Maximum World processes to run at once (default: the logical processor count). A federated leg holds one per process. Proof reports remain in authored order.",
        };
        var gpuJobsOption = GpuJobs();
        var planOption = new Option<bool>(name: "--plan") { Description = "Print what the selection would run — proofs, legs, World boots, process spawns, builds, and the summed leg budget against its ceiling — without building or running." };
        var worldArtifactOption = new Option<string?>(name: "--world-artifact") { Description = "Run every leg on this Puck.World entry assembly, such as a producer-built package's, instead of the build of the checkout's sources. Never builds." };
        var debugLayersOption = new Option<bool>(name: DebugLayerOutput.Flag) { Description = "Boot every leg that names a backend with --debug-layers, the validation layer of that backend, and fail any such leg whose stderr has a validation message or says the layer never loaded." };
        var keepTranscriptsOption = new Option<bool>(name: "--keep-transcripts") { Description = "Keep every leg's run directory whatever its verdict, for a caller that reads the transcripts afterwards and owns their removal; by default a held proof's legs are deleted and a failed proof's are kept." };
        var command = new Command(
            description: "Run bounded, two-leg behavioral proofs against the real Puck.World executable.",
            name: "canary"
        ) { idsArgument, allOption, backendOption, capabilityOption, debugLayersOption, gpuJobsOption, jobsOption, keepTranscriptsOption, listOption, mergeOption, planOption, worldArtifactOption };

        backendOption.AcceptOnlyFromAmong(values: [.. WorldOffscreenLeg.Backends]);

        command.Detail(detail: $"""
              no selection           run the automatic set: headless shape and no environmental requirements
              <id> ...               run the named proofs explicitly, regardless of requirements
              --all                  explicitly run every proof; does not promote any proof into the automatic set
              --list [id ...]        strictly load and list named manifests, or all when unnamed, without building or running
              --capability <class>   filter by automatic, headless, windowed, offscreen, gpu, audio-output, or input:<name>
              --merge                run the merge gate: the automatic set plus every proof requiring gpu
              --backend <name>       run every backend-declaring proof on vulkan or directx only
              --debug-layers         boot every leg that names a backend, windowed or offscreen, with that
                                     backend's validation layer, and fail a leg on any validation message
                                     or an unloaded layer
              --plan                 print the selection's counts and ceiling without building or running
              --keep-transcripts     keep every leg's run directory whatever its verdict
              --jobs <n>             run at most n World processes at once (default: logical processors)
              --gpu-jobs <n>         run at most n legs on the GPU at once (default {DefaultGpuJobs})

            The five selection forms are mutually exclusive. Every execution refuses an empty selection,
            and a gate selection (the automatic set or --merge) whose planned World boots or summed leg
            budget exceed the cost recorded in CanaryCeilings.json (`puck canary-ceilings`). It builds Puck.World once (or takes
            --world-artifact as given), then runs every positive and discriminating leg from fresh state
            in its own run directory, state directory and loopback endpoints: up to --jobs World processes
            at once, of which at most --gpu-jobs legs boot a windowed or offscreen World or require gpu.
            A proof whose manifest declares exclusive runs each leg alone, before every other leg. Each
            leg prints one line with its wall time as it ends; each proof's report prints whole, in
            authored order, and the closing FAIL line lists failed proofs in authored order.
            A leg ends when its script does: the runner closes each script with wire.errors and
            quit, and kills a leg only at its manifest's timeoutSeconds. An offscreen
            proof runs every leg once per backend, Vulkan then Direct3D 12, and holds only when both did.
            --backend runs those legs on the one backend it names, and the plan, the selection line and
            the verdict name the backends that ran; --merge refuses it, since that gate holds both.

            Exit codes: 0 all proofs held, 1 an observed proof failed, 2 refusal/infrastructure, including
            an environment that cannot run a selected proof (UNSUPPORTED: no usable GPU device for a
            backend, or an absent shader compiler).
            """);

        jobsOption.Validators.Add(item: static result => {
            if (result.GetValueOrDefault<int>() < 1) {
                result.AddError(errorMessage: "--jobs must be at least 1.");
            }
        });

        command.Validators.Add(item: result => {
            var capability = result.GetValue(option: capabilityOption);
            var ids = (result.GetValue(argument: idsArgument) ?? []);
            var forms = (((((result.GetValue(option: allOption)
                ? 1
                : 0) + ((capability is null)
                ? 0
                : 1)) + (((ids.Length == 0) || result.GetValue(option: listOption))
                ? 0
                : 1)) + (result.GetValue(option: listOption)
                ? 1
                : 0)) + (result.GetValue(option: mergeOption)
                ? 1
                : 0));

            if (forms > 1) {
                result.AddError(errorMessage: "ids, --all, --list [id ...], --merge, and --capability <class> are mutually exclusive selection forms.");

                return;
            }
            if (
                result.GetValue(option: planOption) &&
                result.GetValue(option: listOption)
            ) {
                result.AddError(errorMessage: "--plan counts a run and --list runs nothing; name a selection to plan instead.");

                return;
            }
            // The token, never GetValue: a value AcceptOnlyFromAmong refused throws when read. An unknown value is left to
            // that refusal, which names it; this check speaks only for a backend that exists.
            if (
                (result.GetValue(option: mergeOption) || result.GetValue(option: listOption)) &&
                (result.GetResult(option: backendOption) is { Tokens.Count: > 0 } backendResult) &&
                (backendResult.Tokens[0].Value is var backend) &&
                WorldOffscreenLeg.Backends.Contains(
                    comparer: StringComparer.Ordinal,
                    value: backend
                )
            ) {

                result.AddError(errorMessage: (result.GetValue(option: mergeOption)
                    ? $"--backend {backend} narrows a run to one backend and --merge is the gate that holds both; select --capability gpu --backend {backend} instead."
                    : "--backend narrows a run and --list runs nothing; name a selection to run on one backend instead."));

                return;
            }
            if (
                (capability is not null) &&
                !IsKnownCapability(capability: capability)
            ) {
                result.AddError(errorMessage: $"unknown capability filter '{capability}'; use automatic, headless, windowed, offscreen, gpu, audio-output, or input:<hardware-name>.");

                return;
            }

            var duplicate = ids.GroupBy(
                keySelector: static id => id,
                comparer: StringComparer.Ordinal
            ).FirstOrDefault(predicate: static group => (group.Count() > 1));

            if (duplicate is not null) {
                result.AddError(errorMessage: $"duplicate canary id '{duplicate.Key}' in the selection; one proof must not be counted twice.");
            }

        });
        command.SetAction(action: parseResult => Run(
            all: parseResult.GetValue(option: allOption),
            backends: SelectBackends(backend: parseResult.GetValue(option: backendOption)),
            capability: parseResult.GetValue(option: capabilityOption),
            debugLayers: (parseResult.GetValue(option: debugLayersOption)
                ? WorldOffscreenLeg.Backends
                : []),
            ids: (parseResult.GetValue(argument: idsArgument) ?? []),
            capacity: new CanaryCapacity(
                GpuLegs: parseResult.GetValue(option: gpuJobsOption),
                Processes: parseResult.GetValue(option: jobsOption)
            ),
            keepTranscripts: parseResult.GetValue(option: keepTranscriptsOption),
            list: parseResult.GetValue(option: listOption),
            merge: parseResult.GetValue(option: mergeOption),
            plan: parseResult.GetValue(option: planOption),
            worldArtifact: parseResult.GetValue(option: worldArtifactOption)
        ));
        return command;
    }

    /// <summary>Runs the named proofs on a given World entry assembly, as <c>puck canary --world-artifact</c> does.</summary>
    /// <param name="ids">The proofs to run.</param>
    /// <param name="worldArtifact">The World entry assembly every leg launches.</param>
    /// <param name="debugLayers">The backends whose legs, windowed or offscreen, boot their World with
    /// <c>--debug-layers</c>.</param>
    /// <returns>The canary exit code: 0 every proof held, 1 a proof failed, 2 a refusal, an unknown id, or an
    /// environment that could not exercise a proof.</returns>
    internal static int RunNamed(IReadOnlyList<string> ids, string worldArtifact, IReadOnlyCollection<string> debugLayers) =>
        Run(
            all: false,
            backends: WorldOffscreenLeg.Backends,
            capability: null,
            debugLayers: debugLayers,
            ids: [.. ids],
            capacity: CanaryCapacity.Default,
            keepTranscripts: false,
            list: false,
            merge: false,
            plan: false,
            worldArtifact: worldArtifact
        );

    // One World process per logical processor: a headless leg is a paced simulation that spends most of its life
    // waiting on its tick, so the machine's threads, not a fixed fraction of them, bound how many share it.
    private static int DefaultJobs => Math.Max(
        val1: 1,
        val2: Environment.ProcessorCount
    );

    /// <summary>The legs a run keeps on the GPU at once unless <c>--gpu-jobs</c> says otherwise.</summary>
    internal const int DefaultGpuJobs = 4;

    /// <summary>Creates <c>--gpu-jobs</c>, the most canary legs on the GPU at once; <c>puck canary</c>,
    /// <c>puck affected</c> and <c>puck gate</c> share it.</summary>
    /// <returns>The option.</returns>
    internal static Option<int> GpuJobs() {
        var option = new Option<int>(name: "--gpu-jobs") {
            DefaultValueFactory = static _ => DefaultGpuJobs,
            Description = $"Maximum canary legs on the GPU at once: a windowed or offscreen World, or a leg requiring gpu (default {DefaultGpuJobs}). Each still holds its World processes of --jobs.",
        };

        option.Validators.Add(item: static result => {
            if (result.GetValueOrDefault<int>() < 1) {
                result.AddError(errorMessage: "--gpu-jobs must be at least 1.");
            }
        });

        return option;
    }

    /// <summary>The most a run holds at once: World processes (<c>--jobs</c>) and legs on the GPU
    /// (<c>--gpu-jobs</c>).</summary>
    /// <param name="Processes">The World processes running at once, across every leg.</param>
    /// <param name="GpuLegs">The legs holding the GPU at once.</param>
    internal readonly record struct CanaryCapacity(int Processes, int GpuLegs) {
        /// <summary>The capacity a run takes when no flag names one.</summary>
        public static CanaryCapacity Default => new(
            GpuLegs: DefaultGpuJobs,
            Processes: DefaultJobs
        );
    }

    // The selection the landing gate runs: no ids, no filter, no --all — the automatic set alone.
    internal static int RunAutomatic() =>
        Run(
            all: false,
            backends: WorldOffscreenLeg.Backends,
            capability: null,
            debugLayers: [],
            ids: [],
            capacity: CanaryCapacity.Default,
            keepTranscripts: false,
            list: false,
            merge: false,
            plan: false,
            worldArtifact: null
        );

    private static bool IsKnownCapability(string capability) => (
        (capability is "automatic" or "headless" or "windowed" or "offscreen" or "gpu" or "audio-output")
        || (capability.StartsWith(
        comparisonType: StringComparison.Ordinal,
        value: "input:"
    ) && (capability.Length > 6))
    );
    private static int Run(bool all, IReadOnlyList<string> backends, string? capability, IReadOnlyCollection<string> debugLayers, string[] ids, CanaryCapacity capacity, bool keepTranscripts, bool list, bool merge, bool plan, string? worldArtifact) {
        var selection = ToSelection(
            all: all,
            capability: capability,
            ids: ids,
            list: list,
            merge: merge
        );

        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return CliExit.Refused;
        }
        // --list stays strict (a single, unambiguous refusal for an author curating manifests); every running shape
        // tolerates a rotten manifest instead of letting it block every other proof — see TryLoadAll's own remarks.
        var strict = (selection.Kind == CanarySelectionKind.List);

        if (!CanaryManifestLoader.TryLoadAll(
            error: out var manifestError,
            manifests: out var manifests,
            refused: out var refusedManifests,
            repositoryRoot: repositoryRoot,
            only: ((strict && (ids.Length > 0)) ? ids.ToHashSet(comparer: StringComparer.Ordinal) : null),
            strict: strict
        )) {
            Console.Error.WriteLine(value: $"ERROR: {manifestError}");

            return CliExit.Refused;
        }

        foreach (var (directory, reason) in refusedManifests) {
            Console.Error.WriteLine(value: $"canary: SKIPPED manifest '{directory}' — {reason}");
        }

        if (selection.Kind == CanarySelectionKind.List) {
            List(
                manifests: manifests,
                repositoryRoot: repositoryRoot
            );

            return CliExit.Success;
        }

        if (!TrySelect(
            error: out var selectionError,
            manifests: manifests,
            selected: out var selected,
            selection: selection
        )) {
            Console.Error.WriteLine(value: $"ERROR: {selectionError}");

            return CliExit.Refused;
        }

        var costs = Plan(
            backends: backends,
            manifests: selected,
            namedWorldArtifact: (worldArtifact is not null)
        );
        var ceilingName = CeilingName(selection: selection);
        CanaryCeiling? ceiling = null;

        if (ceilingName is not null) {
            if (!CanaryCeilingsLedger.TryRead(
                error: out var ledgerError,
                ledger: out var ledger,
                repositoryRoot: repositoryRoot,
                text: out _
            )) {
                Console.Error.WriteLine(value: $"ERROR: {CanaryCeilingsLedger.FileName}: {ledgerError}");

                return CliExit.Refused;
            }

            ceiling = ((ceilingName == "merge")
                ? ledger!.Merge
                : ledger!.Automatic);
        }

        if (plan) {
            PrintPlan(
                ceiling: ceiling,
                ceilingName: ceilingName,
                plan: costs
            );
        }

        // Checked before anything builds, so a gate that outgrew its ceiling costs nothing to refuse.
        if (
            (ceiling is not null) &&
            (CeilingRefusal(
                ceiling: ceiling,
                name: ceilingName!,
                plan: costs
            ) is { } overCeiling)
        ) {
            Console.Error.WriteLine(value: $"ERROR: {overCeiling}");

            return CliExit.Refused;
        }

        var exit = SuiteExit(
            kind: selection.Kind,
            refusedCount: refusedManifests.Count,
            runExit: (plan
                ? CliExit.Success
                : RunSelected(
                    debugLayers: debugLayers,
                    explicitAll: (selection.Kind == CanarySelectionKind.All),
                    capacity: capacity,
                    keepTranscripts: keepTranscripts,
                    manifests: selected,
                    plan: costs,
                    repositoryRoot: repositoryRoot,
                    worldArtifact: worldArtifact
                ))
        );

        if (
            (exit != 0) &&
            (refusedManifests.Count > 0)
        ) {
            Console.Error.WriteLine(value: $"FAIL: {refusedManifests.Count} manifest(s) were skipped and never ran — a suite with an unread proof in it is not green.");
        }

        return exit;
    }

    // Tolerance is about letting the other proofs run, never about calling the gate green while a manifest went
    // unread. A selection that stands for a whole suite therefore still fails when one was skipped — with every
    // surviving proof's verdict already printed, which is the whole difference from refusing the discovery
    // outright. A selection that named its proofs is answered on those proofs alone.
    internal static int SuiteExit(int runExit, int refusedCount, CanarySelectionKind kind) => (
        ((refusedCount > 0) && (kind is CanarySelectionKind.Automatic or CanarySelectionKind.All or CanarySelectionKind.Capability or CanarySelectionKind.Merge))
        ? Math.Max(
            val1: runExit,
            val2: CliExit.Failed
        )
        : runExit
    );

    private static int RunSelected(IReadOnlyList<CanaryManifest> manifests, CanaryPlan plan, string repositoryRoot, bool explicitAll, CanaryCapacity capacity, bool keepTranscripts, string? worldArtifact, IReadOnlyCollection<string> debugLayers) {
        RunDirectory.Sweep(
            age: RunDirectory.StaleAge,
            prefix: ScratchPrefix
        );

        var buildClock = Stopwatch.StartNew();
        IReadOnlyList<CanaryProof> proofs = [.. plan.Proofs.Select(selector: static proof => proof.Proof)];
        var scope = BackendScope(proofs: proofs);
        var scoped = ((scope is null)
            ? string.Empty
            : $" {scope}");

        if (explicitAll) {
            Console.WriteLine(value: $"canary: explicit --all selected {manifests.Count} proof(s){scoped}, including any declared environmental requirements; it does not change the automatic set.");
        } else {
            Console.WriteLine(value: $"canary: selected {manifests.Count} proof(s){scoped}.");
        }

        // The builds' directories hold their logs and the stub's output; a run whose proofs all held deletes them.
        var worldBuildDirectory = BuildRunDirectory(id: "world");
        string? stubBuildDirectory = null;

        // Every leg launches one World: the --world-artifact named, or the build keyed by this checkout's sources, reused
        // when an earlier run built it and leased until the last leg has exited (see WorldArtifactBuild).
        if (!WorldArtifactBuild.TryResolveNamed(
            error: out var buildError,
            lease: out var world,
            logDirectory: worldBuildDirectory,
            named: worldArtifact,
            path: out var artifact,
            repositoryRoot: repositoryRoot,
            timeout: CliProcess.RemainingBudget(
                budget: BuildBudget,
                clock: buildClock
            ),
            verb: "canary"
        )) {
            Console.Error.WriteLine(value: $"ERROR: {buildError}");

            return CliExit.Refused;
        }

        using var lease = world;

        Console.Error.WriteLine(value: $"canary: World artifact {artifact}");

        // A stub-shaped leg launches Puck.Launcher.Stub, never Puck.World.dll directly, from a leg-private install
        // tree — built once here, exactly like Puck.World above, only when a selected manifest actually needs it.
        string? stubArtifact = null;

        if (manifests.Any(predicate: static manifest => (manifest.BootShape == CanaryBootShape.Stub))) {
            const string StubProject = "src/Puck.Launcher.Stub/Puck.Launcher.Stub.csproj";
            var stubDirectory = BuildRunDirectory(id: "stub");

            stubBuildDirectory = stubDirectory;
            var stubOutput = Path.Combine(path1: stubDirectory, path2: "output");

            stubArtifact = Path.Combine(path1: stubOutput, path2: "Puck.Launcher.Stub.exe");

            Console.Error.WriteLine(value: "canary: building Puck.Launcher.Stub once (Release).");

            if (!CliProjectBuild.TryBuild(
                artifactName: Path.GetFileName(path: stubArtifact),
                build: out _,
                error: out var stubError,
                project: StubProject,
                repositoryRoot: repositoryRoot,
                outputDirectory: stubOutput,
                logDirectory: stubDirectory,
                timeout: CliProcess.RemainingBudget(
                    budget: BuildBudget,
                    clock: buildClock
                )
            )) {
                Console.Error.WriteLine(value: $"ERROR: {stubError}");

                return CliExit.Refused;
            }
            Console.Error.WriteLine(value: $"canary: built artifact {stubArtifact}");
        }

        var failed = false;
        var infrastructureFailed = false;
        var unsupported = new List<string>();
        var endings = new List<CanaryLegEnding>(capacity: plan.Legs);

        using var cancellation = new CancellationTokenSource();

        // The first Ctrl+C stops the run and kills every child it started; a second one ends the runner outright.
        void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs args) {
            if (!cancellation.IsCancellationRequested) {
                args.Cancel = true;
                cancellation.Cancel();
            }
        }

        var budget = new CanaryBudget(
            Cancellation: cancellation.Token,
            Clock: Stopwatch.StartNew(),
            Packages: new CanaryPackages(),
            Seed: new CanaryPipelineCacheSeed(),
            Tally: new CanaryTally(),
            Total: TimeSpan.FromSeconds(value: plan.BudgetSeconds)
        );

        Console.WriteLine(value: $"canary: leg budget {budget.Total.TotalSeconds:0}s, derived from the selected manifests' own declared per-leg timeouts; up to {capacity.Processes} World process(es) and {capacity.GpuLegs} GPU leg(s) at once.");

        // results is written by each leg's own thread; legs holds only what completed has taken from it, on this thread.
        var results = new CanaryLegRun?[proofs.Count, 2];
        var legs = new CanaryLegRun[proofs.Count, 2];
        var work = new List<CanaryLegWork>(capacity: (proofs.Count * 2));

        for (var index = 0; (index < proofs.Count); index++) {
            var manifest = proofs[index].Manifest;
            var backend = proofs[index].Backend;
            var manifestIndex = index;

            foreach (var discriminating in ((bool[])[false, true])) {
                var leg = (discriminating
                    ? manifest.Discriminating
                    : manifest.Positive
                );
                var side = (discriminating
                    ? 1
                    : 0);
                Func<CanaryLegRun> run = ((manifest.BootShape == CanaryBootShape.Stub)
                    ? () => RunStubLeg(
                        budget: budget,
                        discriminating: discriminating,
                        manifest: manifest,
                        stubArtifact: stubArtifact!,
                        worldArtifact: artifact
                    )
                    : () => RunEitherLeg(
                        artifact: artifact,
                        backend: backend,
                        budget: budget,
                        debugLayers: ((backend is not null) && debugLayers.Contains(value: backend)),
                        leg: leg,
                        manifest: manifest
                    ));

                work.Add(item: new CanaryLegWork {
                    Discriminating = discriminating,
                    Exclusive = manifest.Exclusive,
                    Gpu = manifest.UsesGpu,
                    ManifestIndex = manifestIndex,
                    Processes = LegProcesses(leg: leg),
                    Run = () => results[manifestIndex, side] = run(),
                });
            }
        }

        var reported = 0;
        var ended = 0;
        var legTime = TimeSpan.Zero;
        var failedProofs = new List<string>();

        Console.CancelKeyPress += OnCancelKeyPress;

        try {
            if (
                (plan.Warm is { } warm) &&
                (WarmPipelineCache(
                    artifact: artifact,
                    budget: budget,
                    warm: warm
                ) is { } warmRefusal)
            ) {
                Console.Error.WriteLine(value: $"ERROR: {warmRefusal}. The selection fails without starting a leg.");

                return CliExit.Refused;
            }

            RunLegsConcurrently(
                cancellation: cancellation,
                completed: (item, elapsed) => {
                    var side = (item.Discriminating
                        ? 1
                        : 0);
                    var leg = results[item.ManifestIndex, side]!;

                    legs[item.ManifestIndex, side] = leg;
                    legTime += elapsed;
                    ended++;
                    // One line a leg the moment it ends, so a long run shows its progress and where its time went; the
                    // whole proof reports below keep authored order.
                    Console.WriteLine(value: $"canary: [{ended.ToString(provider: CultureInfo.InvariantCulture)}/{work.Count.ToString(provider: CultureInfo.InvariantCulture)}] {proofs[item.ManifestIndex].Label} {leg.Leg.Name} {LegOutcome(leg: leg)} in {elapsed.TotalSeconds.ToString(format: "0.0", provider: CultureInfo.InvariantCulture)}s");

                    // Reports print in authored order, each proof whole, as soon as it and every proof before it are in.
                    while (
                        (reported < proofs.Count) &&
                        (legs[reported, 0] is not null) &&
                        (legs[reported, 1] is not null)
                    ) {
                        var verdict = ReportProof(
                            discriminating: legs[reported, 1],
                            positive: legs[reported, 0],
                            proof: proofs[reported]
                        );

                        endings.Add(item: EndingOf(leg: legs[reported, 0]));
                        endings.Add(item: EndingOf(leg: legs[reported, 1]));
                        ConcludeLegs(
                            keepTranscripts: keepTranscripts,
                            legs: [legs[reported, 0], legs[reported, 1]],
                            passed: verdict.Passed
                        );
                        failed |= !verdict.Passed;
                        if (!verdict.Passed && (verdict.Unsupported is null)) {
                            failedProofs.Add(item: proofs[reported].Label);
                        }
                        infrastructureFailed |= verdict.InfrastructureFailed;
                        if (verdict.Unsupported is { } reason) {
                            unsupported.Add(item: $"{proofs[reported].Label}: {reason}");
                        }
                        reported++;
                    }
                },
                capacity: capacity,
                work: work
            );
        } catch (Exception exception) {
            // Every leg has ended by now and its children are dead; what threw is an infrastructure failure.
            Console.Error.WriteLine(value: $"ERROR: a canary leg failed before it could report, and the run was stopped: {exception}");

            return CliExit.Refused;
        } finally {
            Console.CancelKeyPress -= OnCancelKeyPress;

            // A leg whose proof never reported belongs to a run that stopped; its evidence is kept.
            for (var index = reported; (index < proofs.Count); index++) {
                ConcludeLegs(
                    keepTranscripts: keepTranscripts,
                    legs: [results[index, 0], results[index, 1]],
                    passed: false
                );
            }

            // The run's shader packages and build directories are evidence only when a proof did not hold.
            var held = ((reported == proofs.Count) && !failed && !infrastructureFailed && (unsupported.Count == 0) && !cancellation.IsCancellationRequested);

            budget.Packages.Conclude(passed: held);

            foreach (var directory in ((string?[])[worldBuildDirectory, stubBuildDirectory])) {
                if ((directory is { }) && Directory.Exists(path: directory)) {
                    RunDirectory.Conclude(
                        passed: held,
                        path: directory,
                        report: Console.Error
                    );
                }
            }
        }

        PrintTally(
            built: (world is { Reused: false }),
            endings: endings,
            plan: plan,
            seed: budget.Seed,
            tally: budget.Tally
        );
        Console.WriteLine(value: $"canary counts: {ended.ToString(provider: CultureInfo.InvariantCulture)} leg(s) ran for {legTime.TotalSeconds.ToString(format: "0", provider: CultureInfo.InvariantCulture)}s of leg time in {budget.Clock.Elapsed.TotalSeconds.ToString(format: "0", provider: CultureInfo.InvariantCulture)}s of wall time, up to {capacity.Processes.ToString(provider: CultureInfo.InvariantCulture)} World process(es) and {capacity.GpuLegs.ToString(provider: CultureInfo.InvariantCulture)} GPU leg(s) at once.");

        if (cancellation.IsCancellationRequested) {
            Console.Error.WriteLine(value: $"ERROR: the run was cancelled after {reported} of {proofs.Count} proof(s); every World process it started has been stopped.");

            return CliExit.Refused;
        }
        if (infrastructureFailed) {
            Console.Error.WriteLine(value: "ERROR: one or more selected canary legs could not run because infrastructure refused.");

            return CliExit.Refused;
        }
        if (unsupported.Count != 0) {
            foreach (var reason in unsupported) {
                Console.Error.WriteLine(value: $"UNSUPPORTED: {reason}");
            }
            Console.Error.WriteLine(value: "ERROR: this environment could not exercise every selected proof; an offscreen proof holds only when it ran on every selected backend, so the selection is not green.");

            return CliExit.Refused;
        }
        if (failed) {
            Console.Error.WriteLine(value: $"FAIL: {failedProofs.Count.ToString(provider: CultureInfo.InvariantCulture)} selected canar{((failedProofs.Count == 1) ? "y" : "ies")}{scoped} did not prove both their green leg and executable red leg: {string.Join(separator: ", ", values: failedProofs)}.");

            return CliExit.Failed;
        }

        Console.WriteLine(value: $"PASS: all {manifests.Count} selected canary proof(s) held{scoped} within the {budget.Total.TotalSeconds:0}-second leg budget.");

        return CliExit.Success;
    }

    // One leg waiting to run: which proof and side it answers, what it holds while it runs, and the run itself, which
    // stores its own result where the completed callback reads it once the leg's thread has ended.
    internal sealed class CanaryLegWork {
        public required bool Discriminating { get; init; }
        /// <summary>Whether the leg runs alone on the machine (<see cref="CanaryManifest.Exclusive"/>).</summary>
        public required bool Exclusive { get; init; }
        /// <summary>Whether the leg holds one of the run's GPU slots (<see cref="CanaryManifest.UsesGpu"/>).</summary>
        public required bool Gpu { get; init; }
        public required int ManifestIndex { get; init; }
        /// <summary>The World processes the leg runs at once (<see cref="LegProcesses"/>).</summary>
        public required int Processes { get; init; }
        public required Action Run { get; init; }
    }

    // The World processes one leg runs at once: every listener of an authorities leg, its own World and a companion
    // authority, or its one World. A relaunch boots after the first process has exited, so it adds none.
    internal static int LegProcesses(CanaryLeg leg) => ((leg.Authorities.Count != 0)
        ? leg.Authorities.Count
        : ((leg.AuthorityWorldPath is null)
            ? 1
            : 2
        )
    );

    /// <summary>What the legs running at once hold against a run's <see cref="CanaryCapacity"/>, and which waiting leg
    /// may start next. A leg holds its World processes, clamped to the capacity so a leg wider than the machine still
    /// runs (alone), and one GPU slot when it uses the GPU. An exclusive leg starts only once nothing runs, and while it
    /// runs nothing else starts. Every rule the runner schedules by is here, so the laws hold the rules without
    /// starting a process.</summary>
    internal sealed class CanaryLegSlots(CanaryCapacity capacity) {
        private readonly int m_gpuCapacity = Math.Max(
            val1: 1,
            val2: capacity.GpuLegs
        );
        private readonly int m_processCapacity = Math.Max(
            val1: 1,
            val2: capacity.Processes
        );

        private bool m_exclusive;

        /// <summary>The GPU slots the running legs hold.</summary>
        public int Gpu { get; private set; }
        /// <summary>Whether an exclusive leg is running.</summary>
        public bool ExclusiveRunning => m_exclusive;
        /// <summary>The World processes the running legs hold.</summary>
        public int Processes { get; private set; }
        /// <summary>The legs running.</summary>
        public int Running { get; private set; }

        private int ProcessesOf(CanaryLegWork item) => Math.Clamp(
            max: m_processCapacity,
            min: 1,
            value: item.Processes
        );

        /// <summary>Returns the index of the first leg in <paramref name="waiting"/> that may start now, or -1. Legs are
        /// considered in order and the first that fits starts, so a leg that does not fit yet never holds back a smaller
        /// one behind it; an exclusive leg does, since every slot it needs is one a running leg must first give back.</summary>
        /// <param name="waiting">The legs not yet started, in the order they should start.</param>
        /// <returns>The index, or -1 when no waiting leg may start.</returns>
        public int Next(IReadOnlyList<CanaryLegWork> waiting) {
            if (m_exclusive) {
                return -1;
            }

            for (var index = 0; (index < waiting.Count); index++) {
                var item = waiting[index];

                if (item.Exclusive) {
                    return ((Running == 0)
                        ? index
                        : -1);
                }
                if (
                    ((Processes + ProcessesOf(item: item)) <= m_processCapacity) &&
                    (!item.Gpu || (Gpu < m_gpuCapacity))
                ) {
                    return index;
                }
            }

            return -1;
        }
        /// <summary>Records that <paramref name="item"/> started.</summary>
        /// <param name="item">The leg <see cref="Next"/> chose.</param>
        public void Take(CanaryLegWork item) {
            Running++;
            Processes += ProcessesOf(item: item);
            Gpu += (item.Gpu
                ? 1
                : 0);
            m_exclusive |= item.Exclusive;
        }
        /// <summary>Records that <paramref name="item"/> ended.</summary>
        /// <param name="item">A leg <see cref="Take"/> recorded.</param>
        public void Release(CanaryLegWork item) {
            Running--;
            Processes -= ProcessesOf(item: item);
            Gpu -= (item.Gpu
                ? 1
                : 0);
            if (item.Exclusive) {
                m_exclusive = false;
            }
        }
    }

    // The order legs are offered to the slots in: every exclusive leg first, each alone before the run widens, then the
    // rest in authored order.
    internal static List<CanaryLegWork> StartOrder(IReadOnlyList<CanaryLegWork> work) => [
        .. work.Where(predicate: static item => item.Exclusive),
        .. work.Where(predicate: static item => !item.Exclusive),
    ];
    // Starts every leg the slots admit (CanaryLegSlots), each on its own thread, and hands every finished leg, with its
    // wall time, to completed on the calling thread. Once cancellation fires no further leg starts, and no leg that
    // finishes afterwards is reported. Nothing leaves this method while a leg still runs: a leg that throws, or a
    // completed callback that throws, cancels the rest, which kills their children, and the first failure is rethrown
    // only after every leg has ended.
    internal static void RunLegsConcurrently(IReadOnlyList<CanaryLegWork> work, CanaryCapacity capacity, Action<CanaryLegWork, TimeSpan> completed, CancellationTokenSource cancellation) {
        var slots = new CanaryLegSlots(capacity: capacity);
        var waiting = StartOrder(work: work);
        var running = new List<(Task<TimeSpan> Task, CanaryLegWork Item)>(capacity: work.Count);
        ExceptionDispatchInfo? failure = null;

        try {
            while (true) {
                while (
                    !cancellation.IsCancellationRequested &&
                    (slots.Next(waiting: waiting) is var next and >= 0)
                ) {
                    var item = waiting[next];

                    waiting.RemoveAt(index: next);
                    slots.Take(item: item);
                    running.Add(item: (Task.Factory.StartNew(
                        cancellationToken: CancellationToken.None,
                        creationOptions: TaskCreationOptions.LongRunning,
                        function: () => {
                            var clock = Stopwatch.StartNew();

                            item.Run();

                            return clock.Elapsed;
                        },
                        scheduler: TaskScheduler.Default
                    ), item));
                }

                if (running.Count == 0) {
                    break;
                }

                var finished = Task.WaitAny(tasks: [.. running.Select(selector: static entry => entry.Task)]);

                var (task, done) = running[finished];

                running.RemoveAt(index: finished);
                slots.Release(item: done);

                if (task.Exception?.InnerException is { } exception) {
                    // A leg the cancellation stopped ends in OperationCanceledException; only another failure is one.
                    if (!((exception is OperationCanceledException) && cancellation.IsCancellationRequested)) {
                        failure ??= ExceptionDispatchInfo.Capture(source: exception);
                        cancellation.Cancel();
                    }

                    continue;
                }

                if (!cancellation.IsCancellationRequested) {
                    completed(arg1: done, arg2: task.Result);
                }
            }
        } catch (Exception exception) {
            failure ??= ExceptionDispatchInfo.Capture(source: exception);
            cancellation.Cancel();
        } finally {
            foreach (var (task, _) in running) {
                try {
                    task.Wait();
                } catch (AggregateException) {
                    // The run is already failing or cancelled; this leg's own outcome adds nothing.
                }
            }
        }

        failure?.Throw();
    }

    // How one leg ended, in the words its progress line uses.
    private static string LegOutcome(CanaryLegRun leg) => ((leg.Unsupported is not null)
        ? "unsupported"
        : ((leg.InfrastructureError is not null)
            ? "infrastructure failure"
            : (leg.TimedOut
                ? "timed out"
                : (leg.Passed
                    ? "held"
                    : "did not hold"))));
    // Prints one proof's whole report — both legs, then the discriminator verdict — and answers whether it held. A leg
    // that could not exercise its environment makes the proof unsupported rather than judged: neither its observations
    // nor the discriminator mean anything about the code under test.
    private static (bool Passed, bool InfrastructureFailed, string? Unsupported) ReportProof(CanaryProof proof, CanaryLegRun positive, CanaryLegRun discriminating) {
        var manifest = proof.Manifest;

        ReportLeg(
            id: proof.Label,
            result: positive
        );
        ReportLeg(
            id: proof.Label,
            result: discriminating
        );

        if ((positive.Unsupported ?? discriminating.Unsupported) is { } unsupported) {
            Console.Error.WriteLine(value: $"UNSUPPORTED: canary {proof.Label} — {unsupported}");

            return (false, false, unsupported);
        }

        var positiveOnDiscriminating = CanaryAssertions.Evaluate(
            authorityEndpoint: discriminating.AuthorityEndpoint,
            authorityTranscripts: discriminating.AuthorityTranscripts,
            leg: manifest.Positive,
            primaryTranscript: discriminating.Transcript
        );
        var turnedRed = !positiveOnDiscriminating.Passed;

        if (turnedRed) {
            Console.WriteLine(value: $"canary {proof.Label} discriminating: FAIL (expected) — the positive observation turned red under the alternate authored world/input.");
            foreach (var result in positiveOnDiscriminating.Results.Where(predicate: static result => !result.Passed)) {
                Console.WriteLine(value: $"  expected-red: {result.Detail}");
            }
        } else {
            Console.Error.WriteLine(value: $"canary {proof.Label} discriminating: UNEXPECTED PASS — the positive observation survived the declared discriminator, so the proof is not sensitive.");
        }

        var passed = (positive.Passed && discriminating.Passed && turnedRed);

        if (passed) {
            Console.WriteLine(value: $"PASS: canary {proof.Label} — positive held, opposite observation held, and the positive observation failed in the discriminating leg.");
        } else {
            Console.Error.WriteLine(value: $"FAIL: canary {proof.Label} — positive={Verdict(value: positive.Passed)}, opposite={Verdict(value: discriminating.Passed)}, discriminator-turned-red={Verdict(value: turnedRed)}.");
        }

        return (passed, ((positive.InfrastructureError is not null) || (discriminating.InfrastructureError is not null)), null);
    }

    /// <summary>The suite's leg clock and the ceiling it is measured against.</summary>
    // Cancellation ends the whole run: Ctrl+C, or a leg that threw. Every child a leg starts is killed when it fires.
    // A leg may run only under the whole timeout its manifest declares, never a clamped remainder: a child killed by
    // a short timeout reports exit -1 with empty streams, which reads as a failure to launch rather than as the
    // budget refusal it is. Total is therefore the exact sum of what every selected leg may spend
    // (CanaryPlan.BudgetSeconds), so a leg is refused only when an earlier one overran its own declared ceiling. Concurrency keeps
    // that true whatever order legs start in: a leg waits only while some other leg runs, so the time spent before it
    // starts never exceeds the timeouts of the legs that started before it; the warm boots run first, under their own
    // summed timeouts. Packages holds the run's shader
    // packages, Seed the pipeline cache the warm boots left, and Tally counts what the run starts.
    private readonly record struct CanaryBudget(Stopwatch Clock, TimeSpan Total, CancellationToken Cancellation, CanaryPackages Packages, CanaryPipelineCacheSeed Seed, CanaryTally Tally) {
        public TimeSpan Remaining => CliProcess.RemainingBudget(
            budget: Total,
            clock: Clock
        );
    }

    // A leg that relaunches boots twice, each boot under the whole declared timeout.
    private static bool RelaunchesLeg(CanaryManifest manifest) => ((manifest.Positive.Relaunch is not null) || (manifest.Discriminating.Relaunch is not null));
    private static CanaryLegRun RunEitherLeg(string artifact, string? backend, CanaryBudget budget, bool debugLayers, CanaryLeg leg, CanaryManifest manifest) =>
        ((leg.Authorities.Count != 0)
            ? RunFederatedMeshLeg(
                artifact: artifact,
                budget: budget,
                leg: leg,
                manifest: manifest
            )
            : RunLeg(
                artifact: artifact,
                backend: backend,
                budget: budget,
                debugLayers: debugLayers,
                leg: leg,
                manifest: manifest
            )
        );
    private static CanaryLegRun RunLeg(CanaryManifest manifest, CanaryLeg leg, string artifact, string? backend, CanaryBudget budget, bool debugLayers) {
        try {
            return RunLegCore(
                artifact: artifact,
                backend: backend,
                budget: budget,
                debugLayers: debugLayers,
                leg: leg,
                manifest: manifest
            );
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or ArgumentException)) {
            return CanaryLegRun.InfrastructureFailure(
                leg: leg,
                runDirectory: "not-created",
                reason: exception.Message.ReplaceLineEndings(replacementText: " ")
            );
        }
    }
    private static CanaryLegRun RunLegCore(CanaryManifest manifest, CanaryLeg leg, string artifact, string? backend, CanaryBudget budget, bool debugLayers) {
        var runDirectory = CreateRunDirectory(
            id: manifest.Id,
            leg: ((backend is null)
                ? leg.Name
                : $"{leg.Name}-{backend}")
        );
        var stateDirectory = Path.Combine(
            path1: runDirectory,
            path2: "state"
        );
        // An offscreen or windowed leg starts from the pipeline cache the run's warm boots left.
        var seeded = (
            (manifest.BootShape is CanaryBootShape.Offscreen or CanaryBootShape.Windowed) &&
            budget.Seed.TrySeed(stateDirectory: stateDirectory)
        );
        var stdoutPath = Path.Combine(
            path1: runDirectory,
            path2: "stdout.log"
        );
        var stderrPath = Path.Combine(
            path1: runDirectory,
            path2: "stderr.log"
        );
        // {run} names the per-leg ephemeral scratch directory (a script-written output path); {fixtures} names
        // the manifest's OWN checked-in directory (a script-read input path, e.g. a document a script loads by
        // name) — the process this line's text feeds does not inherit the repository root as its own working
        // directory, so a script cannot address either by a bare relative path.
        var input = File.ReadAllText(path: leg.ScriptPath)
            .Replace(
            oldValue: "{fixtures}",
            newValue: manifest.DirectoryPath.Replace(
                newChar: '/',
                oldChar: '\\'
            ),
            comparisonType: StringComparison.Ordinal
        )
            .Replace(
            oldValue: "{run}",
            newValue: runDirectory.Replace(
                newChar: '/',
                oldChar: '\\'
            ),
            comparisonType: StringComparison.Ordinal
        );
        var executionWorld = leg.WorldPath;
        var authorityExecutionWorld = leg.AuthorityWorldPath;
        string? clientFederationKeyPath = null;
        string? authorityFederationKeyPath = null;
        // Bound to a free loopback UDP port per leg, exactly as a mesh leg's authorities are, so no two companions in a
        // run share an endpoint; a companion that still cannot bind never reports a listener, and the leg fails as
        // infrastructure.
        var federationEndpoint = string.Empty;

        if (leg.AuthorityWorldPath is not null) {
            // Each side gets its OWN ECDSA identity, pinned into the OTHER side's admission rows —
            // WorldAttestedAuthenticator verifies a signed claim against the reading world's own trust list, never a
            // shared bearer secret one process could sign the other's namespace with.
            var clientIdentity = GenerateFederationIdentity();
            var authorityIdentity = GenerateFederationIdentity();

            federationEndpoint = $"127.0.0.1:{GetFreeLoopbackPort()}";
            (executionWorld, authorityExecutionWorld) = PrepareFederatedWorlds(
                authorityIdentity: authorityIdentity,
                clientIdentity: clientIdentity,
                endpoint: federationEndpoint,
                leg: leg,
                runDirectory: runDirectory
            );
            clientFederationKeyPath = Path.Combine(
                path1: runDirectory,
                path2: "client-federation.key"
            );
            authorityFederationKeyPath = Path.Combine(
                path1: runDirectory,
                path2: "authority-federation.key"
            );
            File.WriteAllBytes(
                path: clientFederationKeyPath,
                bytes: clientIdentity.Pkcs8
            );
            File.WriteAllBytes(
                path: authorityFederationKeyPath,
                bytes: authorityIdentity.Pkcs8
            );
        }

        if (!input.EndsWith(value: '\n')) {
            input += Environment.NewLine;
        }

        // Runner-owned completion: wire.errors follows every authored byte and its exact response count is checked,
        // then quit ends the session. A process that ended any other way cannot satisfy the count.
        input += $"wire.errors{Environment.NewLine}{RunnerQuit}";

        var timeout = TimeSpan.FromSeconds(value: manifest.TimeoutSeconds);

        if (
            (leg.Package is { } package) &&
            (PreparePackage(
                budget: budget,
                leg: leg,
                package: package,
                runDirectory: runDirectory,
                timeout: timeout
            ) is { } unprepared)
        ) {
            return unprepared;
        }
        if (budget.Remaining < timeout) {
            return CanaryLegRun.BudgetExpired(
                budget: budget,
                leg: leg,
                runDirectory: runDirectory
            );
        }

        CliProcessResult process;
        AuthorityCompanion? authority = null;

        try {
            if (authorityExecutionWorld is { } authorityWorld) {
                budget.Tally.WorldStarted();
                authority = AuthorityCompanion.Start(
                    quitInput: RunnerQuit,
                    artifact: artifact,
                    cancellationToken: budget.Cancellation,
                    exitAfterSeconds: ((manifest.TimeoutSeconds + AuthorityCompanion.ListenSeconds) + AuthorityCompanion.QuitGraceSeconds),
                    world: authorityWorld,
                    stateDirectory: Path.Combine(
                        path1: runDirectory,
                        path2: "authority-state"
                    ),
                    federationKeyPath: authorityFederationKeyPath!
                );
                if (!authority.WaitUntilListening(timeout: TimeSpan.FromSeconds(seconds: AuthorityCompanion.ListenSeconds))) {
                    budget.Cancellation.ThrowIfCancellationRequested();

                    return CanaryLegRun.InfrastructureFailure(
                        leg: leg,
                        reason: ((ListenerRefusal(stderr: SplitLines(text: authority.Stderr)) is { } refused)
                            ? $"companion authority could not bind {federationEndpoint}: {refused}"
                            : $"companion authority did not report a bound listener within {AuthorityCompanion.ListenSeconds} seconds"),
                        runDirectory: runDirectory
                    );
                }
            }

            budget.Tally.WorldStarted();
            process = CliProcess.RunCaptured(
                fileName: "dotnet",
                arguments: [
                    artifact,
                    "--world", executionWorld,
                    .. ((leg.Entry is null)
                ? []
                : new[] { "--entry", leg.Entry }),
                            .. (leg.Connect
                ? new[] { "--connect", federationEndpoint }
                : []),
                    .. ((clientFederationKeyPath is null)
                ? []
                : new[] { "--federation-key-file", clientFederationKeyPath }),
                    "--state-dir", stateDirectory,
                    .. (leg.RunSchedule ? new[] { "--schedule-dir", Path.Combine(path1: runDirectory, path2: "schedule") } : []),
                    "--exit-after-seconds", manifest.TimeoutSeconds.ToString(provider: CultureInfo.InvariantCulture),
                    .. BootShapeArguments(
                    backend: backend,
                    debugLayers: debugLayers,
                    manifest: manifest
                ),
                ],
                cancellationToken: budget.Cancellation,
                environment: (leg.HideShaderCompiler
                    ? Qualification.QualificationPackage.LegEnvironment(
                        compiler: Qualification.ReleaseCompilerDiscovery.None,
                        holdsCompiler: Qualification.QualificationPackage.HoldsCompiler,
                        searchPath: Environment.GetEnvironmentVariable(variable: "PATH")
                    )
                    : null),
                input: input,
                timeout: timeout
            );
        } catch (Exception exception) when ((exception is InvalidOperationException or System.ComponentModel.Win32Exception)) {
            return CanaryLegRun.InfrastructureFailure(
                leg: leg,
                runDirectory: runDirectory,
                reason: exception.Message.ReplaceLineEndings(replacementText: " ")
            );
        } finally {
            // Ended exactly once here — an earlier early-return above must never also dispose, or the second
            // Dispose() throws on the already-released Process handle. The companion is asked to quit and is only
            // killed if it does not, so its transcript ends the way a session ends.
            if (authority is not null) {
                authority.Dispose();
                File.WriteAllText(
                    path: Path.Combine(
                        path1: runDirectory,
                        path2: "authority-stdout.log"
                    ),
                    contents: authority.Stdout,
                    encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                );
                File.WriteAllText(
                    path: Path.Combine(
                        path1: runDirectory,
                        path2: "authority-stderr.log"
                    ),
                    contents: authority.Stderr,
                    encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                );
            }
        }

        if (seeded) {
            budget.Seed.Observe(stateDirectory: stateDirectory);
        }

        File.WriteAllText(
            path: stdoutPath,
            contents: process.Stdout,
            encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        );
        File.WriteAllText(
            path: stderrPath,
            contents: process.Stderr,
            encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        );

        var transcript = new CanaryTranscript(
            RunDirectory: runDirectory,
            Stderr: SplitLines(text: process.Stderr),
            Stdout: SplitLines(text: process.Stdout)
        );
        var invariants = EvaluateRunnerInvariants(
            backend: backend,
            debugLayers: debugLayers,
            executionWorld: executionWorld,
            leg: leg,
            manifest: manifest,
            process: process,
            transcript: transcript
        );
        var unsupported = (process.TimedOut
            ? null
            : (UnsupportedReason(
                compilerHidden: leg.HideShaderCompiler,
                exitCode: process.ExitCode,
                transcript: transcript
            ) ?? AudioUnsupportedReason(
                manifest: manifest,
                transcript: transcript
            )));
        var exitCode = process.ExitCode;
        var timedOut = process.TimedOut;

        // The relaunch boots only after a first boot that ran its script to the end, on the document that boot wrote or on
        // the leg's own world.
        if (
            (leg.Relaunch is { } relaunch) &&
            (unsupported is null) &&
            !timedOut
        ) {
            if (budget.Remaining < timeout) {
                return CanaryLegRun.BudgetExpired(
                    budget: budget,
                    leg: leg,
                    runDirectory: runDirectory
                );
            }

            var relaunchWorld = ((relaunch.WorldFileName is { } relaunchFile)
                ? Path.Combine(
                    path1: runDirectory,
                    path2: relaunchFile
                )
                : executionWorld);
            var relaunchInput = File.ReadAllText(path: relaunch.ScriptPath)
                .Replace(
                oldValue: "{fixtures}",
                newValue: manifest.DirectoryPath.Replace(
                    newChar: '/',
                    oldChar: '\\'
                ),
                comparisonType: StringComparison.Ordinal
            )
                .Replace(
                oldValue: "{run}",
                newValue: runDirectory.Replace(
                    newChar: '/',
                    oldChar: '\\'
                ),
                comparisonType: StringComparison.Ordinal
            );

            if (!relaunchInput.EndsWith(value: '\n')) {
                relaunchInput += Environment.NewLine;
            }
            relaunchInput += $"wire.errors{Environment.NewLine}{RunnerQuit}";

            CliProcessResult second;

            try {
                budget.Tally.WorldStarted();
                second = CliProcess.RunCaptured(
                    fileName: "dotnet",
                    arguments: [
                        artifact,
                        "--world", relaunchWorld,
                        "--state-dir", stateDirectory,
                        "--exit-after-seconds", manifest.TimeoutSeconds.ToString(provider: CultureInfo.InvariantCulture),
                        .. BootShapeArguments(
                        backend: backend,
                        debugLayers: debugLayers,
                        manifest: manifest
                    ),
                    ],
                    cancellationToken: budget.Cancellation,
                    environment: (leg.HideShaderCompiler
                        ? Qualification.QualificationPackage.LegEnvironment(
                            compiler: Qualification.ReleaseCompilerDiscovery.None,
                            holdsCompiler: Qualification.QualificationPackage.HoldsCompiler,
                            searchPath: Environment.GetEnvironmentVariable(variable: "PATH")
                        )
                        : null),
                    input: relaunchInput,
                    timeout: timeout
                );
            } catch (Exception exception) when ((exception is InvalidOperationException or System.ComponentModel.Win32Exception)) {
                return CanaryLegRun.InfrastructureFailure(
                    leg: leg,
                    runDirectory: runDirectory,
                    reason: exception.Message.ReplaceLineEndings(replacementText: " ")
                );
            }

            File.WriteAllText(
                path: Path.Combine(
                    path1: runDirectory,
                    path2: "relaunch-stdout.log"
                ),
                contents: second.Stdout,
                encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            );
            File.WriteAllText(
                path: Path.Combine(
                    path1: runDirectory,
                    path2: "relaunch-stderr.log"
                ),
                contents: second.Stderr,
                encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            );

            var secondTranscript = new CanaryTranscript(
                RunDirectory: runDirectory,
                Stderr: SplitLines(text: second.Stderr),
                Stdout: SplitLines(text: second.Stdout)
            );

            invariants = [
                .. invariants,
                .. EvaluateRunnerInvariants(
                    backend: backend,
                    debugLayers: debugLayers,
                    executionWorld: relaunchWorld,
                    leg: (leg with { Commands = relaunch.Commands }),
                    manifest: manifest,
                    process: second,
                    transcript: secondTranscript
                ).Select(selector: static result => (result with { Detail = $"relaunch: {result.Detail}" })),
            ];
            unsupported = (second.TimedOut
                ? null
                : (UnsupportedReason(
                    compilerHidden: leg.HideShaderCompiler,
                    exitCode: second.ExitCode,
                    transcript: secondTranscript
                ) ?? AudioUnsupportedReason(
                    manifest: manifest,
                    transcript: secondTranscript
                )));
            exitCode = ((exitCode != 0)
                ? exitCode
                : second.ExitCode);
            timedOut = second.TimedOut;
            transcript = new CanaryTranscript(
                RunDirectory: runDirectory,
                Stderr: [.. transcript.Stderr, .. secondTranscript.Stderr],
                Stdout: [.. transcript.Stdout, .. secondTranscript.Stdout]
            );
        }

        var assertions = CanaryAssertions.Evaluate(
            authorityEndpoint: federationEndpoint,
            leg: leg,
            primaryTranscript: transcript
        );

        return new CanaryLegRun(
            Assertions: assertions,
            AuthorityEndpoint: federationEndpoint,
            AuthorityTranscripts: ImmutableEmptyAuthorityTranscripts,
            ExitCode: exitCode,
            InfrastructureError: ListenerRefusal(stderr: transcript.Stderr),
            Invariants: invariants,
            Leg: leg,
            RunDirectory: runDirectory,
            TimedOut: timedOut,
            Transcript: transcript
        ) {
            Unsupported = unsupported,
        };
    }
    private static IReadOnlyList<CanaryAssertionResult> EvaluateRunnerInvariants(
        CanaryManifest manifest,
        CanaryLeg leg,
        CliProcessResult process,
        CanaryTranscript transcript,
        string executionWorld,
        string? backend,
        bool debugLayers
    ) {
        var results = new List<CanaryAssertionResult> {
            new(
            Detail: $"process exited 0 (actual {process.ExitCode})",
            Passed: (!process.TimedOut && (process.ExitCode == 0))
        ),
            new(
            Detail: $"leg completed before {manifest.TimeoutSeconds}s timeout",
            Passed: !process.TimedOut
        ),
        };
        var origin = $"[world] definition: {executionWorld} (--world)";

        results.Add(item: new CanaryAssertionResult(
            Detail: "stderr names the exact absolute --world origin",
            Passed: transcript.Stderr.Any(predicate: line => string.Equals(
                a: line,
                b: origin,
                comparisonType: StringComparison.Ordinal
            ))
        ));

        results.AddRange(collection: EvaluateCommandAccounting(
            commands: leg.Commands,
            outputLines: process.OutputLines
        ));
        results.AddRange(collection: PipelineWaitInvariants(transcript: transcript));

        // A leg boots under the layer only when it names a backend.
        if ((backend is not null) && (DebugLayerOutput.Verdict(backend: backend, debugLayers: debugLayers, stderr: transcript.Stderr) is { } validation)) {
            results.Add(item: new CanaryAssertionResult(Detail: validation.Detail, Passed: validation.Passed));
        }

        return results;
    }
    private static void ReportLeg(string id, CanaryLegRun result) {
        Console.WriteLine(value: $"canary {id} {result.Leg.Name}: transcripts {result.RunDirectory}");

        if (result.InfrastructureError is { } infrastructureError) {
            Console.Error.WriteLine(value: $"  FAIL: infrastructure: {infrastructureError}");

            return;
        }

        foreach (var invariant in result.Invariants) {
            Console.WriteLine(value: $"  {(invariant.Passed
                ? "PASS"
                : "FAIL")}: {invariant.Detail}");
        }
        foreach (var assertion in result.Assertions.Results) {
            Console.WriteLine(value: $"  {(assertion.Passed
                ? "PASS"
                : "FAIL")}: {assertion.Detail}");
        }
    }
    private static void List(IReadOnlyList<CanaryManifest> manifests, string repositoryRoot) {
        foreach (var manifest in manifests) {
            var requirements = ((manifest.Requirements.Count == 0)
                ? "none"
                : string.Join(
                    separator: ',',
                    values: manifest.Requirements
                )
            );

            Console.WriteLine(value: $"{manifest.Id} shape={manifest.BootShape.ToString().ToLowerInvariant()}{((manifest.Backends.Count == 0)
                ? string.Empty
                : $" backends={string.Join(
                    separator: ',',
                    values: manifest.Backends
                )}")} requirements={requirements} automatic={manifest.IsAutomatic.ToString().ToLowerInvariant()}");
            Console.WriteLine(value: $"  world: {CliPaths.ToDisplay(
                relativeTo: repositoryRoot,
                fullPath: manifest.Positive.WorldPath
            )}");
            Console.WriteLine(value: $"  binding: {manifest.Binding}");
        }
    }
    private static bool TrySelect(CanarySelection selection, IReadOnlyList<CanaryManifest> manifests, out IReadOnlyList<CanaryManifest> selected, out string error) {
        error = string.Empty;
        selected = selection.Kind switch {
            CanarySelectionKind.Automatic => manifests.Where(predicate: static manifest => manifest.IsAutomatic).ToArray(),
            CanarySelectionKind.All => manifests,
            CanarySelectionKind.Capability => FilterCapability(
            manifests: manifests,
            capability: selection.Capability!
        ),
            CanarySelectionKind.Ids => SelectIds(
            manifests: manifests,
            ids: selection.Ids
        ),
            CanarySelectionKind.Merge => SelectMerge(manifests: manifests),
            _ => [],
        };

        if (selection.Kind == CanarySelectionKind.Ids) {
            var known = manifests.Select(selector: static manifest => manifest.Id).ToHashSet(comparer: StringComparer.Ordinal);
            var unknown = selection.Ids.Where(predicate: id => !known.Contains(item: id)).ToArray();

            if (unknown.Length != 0) {
                error = $"unknown canary id(s): {string.Join(
                    separator: ", ",
                    values: unknown
                )}; an unknown selection cannot report green.";

                return false;
            }
        }

        if (selected.Count == 0) {
            error = selection.Kind switch {
                CanarySelectionKind.Automatic => "the automatic set is empty; landing cannot report green without a headless, requirement-free proof.",
                CanarySelectionKind.Capability => $"capability filter '{selection.Capability}' selected zero canaries; an empty selection cannot report green.",
                CanarySelectionKind.Merge => "the merge set is empty; a merge cannot report green without a proof to run.",
                _ => "selection contains zero canaries; an empty run cannot report green.",
            };

            return false;
        }

        return true;
    }
    private static IReadOnlyList<CanaryManifest> FilterCapability(IReadOnlyList<CanaryManifest> manifests, string capability) => capability switch {
        "automatic" => manifests.Where(predicate: static manifest => manifest.IsAutomatic).ToArray(),
        "headless" => manifests.Where(predicate: static manifest => (manifest.BootShape == CanaryBootShape.Headless)).ToArray(),
        "windowed" => manifests.Where(predicate: static manifest => (manifest.BootShape == CanaryBootShape.Windowed)).ToArray(),
        "offscreen" => manifests.Where(predicate: static manifest => (manifest.BootShape == CanaryBootShape.Offscreen)).ToArray(),
        _ => manifests.Where(predicate: manifest => manifest.Requirements.Contains(
        value: capability,
        comparer: StringComparer.Ordinal
    )).ToArray(),
    };

    // The merge gate: every automatic proof and every proof requiring gpu, once each, in authored order.
    internal static IReadOnlyList<CanaryManifest> SelectMerge(IReadOnlyList<CanaryManifest> manifests) {
        var gpu = FilterCapability(
            capability: "gpu",
            manifests: manifests
        );

        return manifests.Where(predicate: manifest => (manifest.IsAutomatic || gpu.Contains(value: manifest))).ToArray();
    }

    private static IReadOnlyList<CanaryManifest> SelectIds(IReadOnlyList<CanaryManifest> manifests, IReadOnlyList<string> ids) {
        var byId = manifests.ToDictionary(
            keySelector: static manifest => manifest.Id,
            comparer: StringComparer.Ordinal
        );

        return ids.Where(predicate: byId.ContainsKey).Select(selector: id => byId[id]).ToArray();
    }
    // The five selection forms are mutually exclusive at parse time, so at most one of these can be set.
    private static CanarySelection ToSelection(bool all, string? capability, string[] ids, bool list, bool merge) {
        if (list) {
            return new CanarySelection(
                Capability: null,
                Ids: [],
                Kind: CanarySelectionKind.List
            );
        }
        if (all) {
            return new CanarySelection(
                Capability: null,
                Ids: [],
                Kind: CanarySelectionKind.All
            );
        }
        if (merge) {
            return new CanarySelection(
                Capability: null,
                Ids: [],
                Kind: CanarySelectionKind.Merge
            );
        }
        if (capability is not null) {
            return new CanarySelection(
                Capability: capability,
                Ids: [],
                Kind: CanarySelectionKind.Capability
            );
        }
        if (ids.Length != 0) {
            return new CanarySelection(
                Capability: null,
                Ids: ids,
                Kind: CanarySelectionKind.Ids
            );
        }

        return new CanarySelection(
            Capability: null,
            Ids: [],
            Kind: CanarySelectionKind.Automatic
        );
    }

    /// <summary>Names a build run directory without creating it, so a reused or named artifact leaves no directory.</summary>
    /// <param name="id">The build's name.</param>
    /// <returns>A unique path under the canary scratch prefix, created when the build writes its output or log.</returns>
    public static string BuildRunDirectory(string id) =>
        Path.Combine(path1: Path.GetTempPath(), path2: $"{ScratchPrefix}{id}-build-{Guid.NewGuid():N}");

    // The run directory names its canary and leg after the prefix RunSelected sweeps. Each call creates a directory of
    // its own, however many legs of one proof run at once: a leg's state directory, captures and keys live under it.
    internal static string CreateRunDirectory(string id, string leg) =>
        RunDirectory.CreatePath(prefix: $"{ScratchPrefix}{id}-{leg}-");

    // Concludes a reported proof's legs with its verdict: a held proof's are deleted, a failed one's are kept and named,
    // and --keep-transcripts leaves every one to its caller. A leg that never ran, or never created its directory, has
    // nothing to conclude.
    private static void ConcludeLegs(IEnumerable<CanaryLegRun?> legs, bool passed, bool keepTranscripts) {
        if (keepTranscripts) {
            return;
        }

        foreach (var leg in legs) {
            if (leg is not null) {
                RunDirectory.Conclude(
                    passed: passed,
                    path: leg.RunDirectory,
                    report: Console.Error
                );
            }
        }
    }
    private static IReadOnlyList<string> SplitLines(string text) =>
        text.ReplaceLineEndings(replacementText: "\n").Split(
            options: StringSplitOptions.RemoveEmptyEntries,
            separator: '\n'
        );
    private static string Verdict(bool value) => (value
        ? "PASS"
        : "FAIL"
    );

    internal enum CanarySelectionKind {
        Automatic,
        All,
        Capability,
        Ids,
        List,
        Merge,
    }

    private sealed record CanarySelection(string? Capability, IReadOnlyList<string> Ids, CanarySelectionKind Kind);
    private sealed record CanaryLegRun(
        CanaryEvaluation Assertions,
        string AuthorityEndpoint,
        IReadOnlyDictionary<string, CanaryTranscript> AuthorityTranscripts,
        int ExitCode,
        string? InfrastructureError,
        IReadOnlyList<CanaryAssertionResult> Invariants,
        CanaryLeg Leg,
        string RunDirectory,
        bool TimedOut,
        CanaryTranscript Transcript
    ) {
        public bool Passed => ((InfrastructureError is null) && (Unsupported is null) && !TimedOut && (ExitCode == 0) && Invariants.All(predicate: static result => result.Passed) && Assertions.Passed);
        // The line that announced this leg's environment could not run it, or null when it could.
        public string? Unsupported { get; init; }

        public static CanaryLegRun BudgetExpired(CanaryBudget budget, CanaryLeg leg, string runDirectory) => InfrastructureFailure(
            leg: leg,
            runDirectory: runDirectory,
            reason: $"only {budget.Remaining.TotalSeconds:0.0}s of the {budget.Total.TotalSeconds:0}-second leg budget remained, too little to run this leg under the whole timeout its manifest declares"
        );
        public static CanaryLegRun InfrastructureFailure(CanaryLeg leg, string runDirectory, string reason) => new(
            Assertions: new CanaryEvaluation(Results: []),
            AuthorityEndpoint: string.Empty,
            AuthorityTranscripts: ImmutableEmptyAuthorityTranscripts,
            ExitCode: -1,
            InfrastructureError: reason,
            Invariants: [],
            Leg: leg,
            RunDirectory: runDirectory,
            TimedOut: false,
            Transcript: new CanaryTranscript(
                RunDirectory: runDirectory,
                Stderr: [],
                Stdout: []
            )
        );
    }
}
