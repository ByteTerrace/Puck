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
        var capabilityOption = new Option<string>(name: "--capability") { Description = "Filter by automatic, headless, windowed, offscreen, gpu, audio-output, or input:<hardware-name>." };
        var idsArgument = new Argument<string[]>(name: "id") { Arity = ArgumentArity.ZeroOrMore, DefaultValueFactory = static _ => [], Description = "Run the named proofs explicitly, regardless of their declared requirements." };
        var listOption = new Option<bool>(name: "--list") { Description = "Strictly load and list every manifest without building or running." };
        var mergeOption = new Option<bool>(name: "--merge") { Description = "Run what a merge needs: the automatic set plus every proof requiring gpu." };
        var jobsOption = new Option<int>(name: "--jobs") {
            DefaultValueFactory = static _ => DefaultJobs,
            Description = "Maximum World processes to run at once. A federated leg holds one per process, and a windowed, offscreen, or requirement-declaring leg holds all of them. Output remains in authored order.",
        };
        var planOption = new Option<bool>(name: "--plan") { Description = "Print what the selection would run — proofs, legs, World boots, process spawns, builds, and the summed leg budget against its ceiling — without building or running." };
        var worldArtifactOption = new Option<string?>(name: "--world-artifact") { Description = "Run every leg on this Puck.World entry assembly, such as a producer-built package's, instead of the build of the checkout's sources. Never builds." };
        var debugLayersOption = new Option<bool>(name: WorldOffscreenLeg.DebugLayersFlag) { Description = "Boot every leg that names a backend with --debug-layers, the validation layer of that backend." };
        var command = new Command(
            description: "Run bounded, two-leg behavioral proofs against the real Puck.World executable.",
            name: "canary"
        ) { idsArgument, allOption, capabilityOption, debugLayersOption, jobsOption, listOption, mergeOption, planOption, worldArtifactOption };

        command.Detail(detail: """
              no selection           run the automatic set: headless shape and no environmental requirements
              <id> ...               run the named proofs explicitly, regardless of requirements
              --all                  explicitly run every proof; does not promote any proof into the automatic set
              --list                 strictly load and list every manifest without building or running
              --capability <class>   filter by automatic, headless, windowed, offscreen, gpu, audio-output, or input:<name>
              --merge                run the merge gate: the automatic set plus every proof requiring gpu
              --debug-layers         boot every offscreen leg with its backend's validation layer
              --plan                 print the selection's counts and ceiling without building or running

            The five selection forms are mutually exclusive. Every execution refuses an empty selection,
            and a gate selection (the automatic set or --merge) whose planned World boots or summed leg
            budget exceed its ceiling in CanaryCeilings.cs. It builds Puck.World once (or takes
            --world-artifact as given), then runs every positive and discriminating leg from fresh state,
            up to --jobs World processes at once; a windowed, offscreen, or requirement-declaring leg runs
            alone. A leg ends when its script does: the runner closes each script with wire.errors and
            quit, and kills a leg only at its manifest's timeoutSeconds. An offscreen
            proof runs every leg once per backend, Vulkan then Direct3D 12, and holds only when both did.

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
                : 1)) + ((ids.Length == 0)
                ? 0
                : 1)) + (result.GetValue(option: listOption)
                ? 1
                : 0)) + (result.GetValue(option: mergeOption)
                ? 1
                : 0));

            if (forms > 1) {
                result.AddError(errorMessage: "ids, --all, --list, --merge, and --capability <class> are mutually exclusive selection forms.");

                return;
            }
            if (
                result.GetValue(option: planOption) &&
                result.GetValue(option: listOption)
            ) {
                result.AddError(errorMessage: "--plan counts a run and --list runs nothing; name a selection to plan instead.");

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
            capability: parseResult.GetValue(option: capabilityOption),
            debugLayers: (parseResult.GetValue(option: debugLayersOption)
                ? WorldOffscreenLeg.Backends
                : []),
            ids: (parseResult.GetValue(argument: idsArgument) ?? []),
            jobs: parseResult.GetValue(option: jobsOption),
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
    /// <param name="debugLayers">The backends whose offscreen legs boot their World with <c>--debug-layers</c>.</param>
    /// <returns>The canary exit code: 0 every proof held, 1 a proof failed, 2 a refusal, an unknown id, or an
    /// environment that could not exercise a proof.</returns>
    internal static int RunNamed(IReadOnlyList<string> ids, string worldArtifact, IReadOnlyCollection<string> debugLayers) =>
        Run(
            all: false,
            capability: null,
            debugLayers: debugLayers,
            ids: [.. ids],
            jobs: DefaultJobs,
            list: false,
            merge: false,
            plan: false,
            worldArtifact: worldArtifact
        );

    // Half the processors, at most eight: every World process runs a pump, a stdin reader and its own worker threads,
    // and the federated legs keep live sockets whose peers must stay responsive.
    private static int DefaultJobs => Math.Min(
        val1: 8,
        val2: Math.Max(
            val1: 1,
            val2: (Environment.ProcessorCount / 2)
        )
    );

    // The selection the landing gate runs: no ids, no filter, no --all — the automatic set alone.
    internal static int RunAutomatic() =>
        Run(
            all: false,
            capability: null,
            debugLayers: [],
            ids: [],
            jobs: DefaultJobs,
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
    private static int Run(bool all, string? capability, IReadOnlyCollection<string> debugLayers, string[] ids, int jobs, bool list, bool merge, bool plan, string? worldArtifact) {
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
            manifests: selected,
            namedWorldArtifact: (worldArtifact is not null)
        );
        var ceiling = CeilingOf(selection: selection);
        var ceilingName = CeilingName(selection: selection);

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
                name: ceilingName,
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
                    jobs: jobs,
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

    private static int RunSelected(IReadOnlyList<CanaryManifest> manifests, CanaryPlan plan, string repositoryRoot, bool explicitAll, int jobs, string? worldArtifact, IReadOnlyCollection<string> debugLayers) {
        CliScratchDirectories.SweepScratch(scratchPrefix: ScratchPrefix);

        var buildClock = Stopwatch.StartNew();

        if (explicitAll) {
            Console.WriteLine(value: $"canary: explicit --all selected {manifests.Count} proof(s), including any declared environmental requirements; it does not change the automatic set.");
        } else {
            Console.WriteLine(value: $"canary: selected {manifests.Count} proof(s).");
        }

        // Every leg launches one World: the --world-artifact named, or the build keyed by this checkout's sources, reused
        // when an earlier run built it and leased until the last leg has exited (see WorldArtifactBuild).
        if (!WorldArtifactBuild.TryResolveNamed(
            build: out var build,
            error: out var buildError,
            lease: out var world,
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

            if (build is not null) {
                PrintCaptured(result: build);
            }

            return CliExit.Refused;
        }

        using var lease = world;

        Console.Error.WriteLine(value: $"canary: World artifact {artifact}");

        // A stub-shaped leg launches Puck.Launcher.Stub, never Puck.World.dll directly, from a leg-private install
        // tree — built once here, exactly like Puck.World above, only when a selected manifest actually needs it.
        string? stubArtifact = null;

        if (manifests.Any(predicate: static manifest => (manifest.BootShape == CanaryBootShape.Stub))) {
            var stubProject = Path.Combine(
                path1: repositoryRoot,
                path2: "src",
                path3: "Puck.Launcher.Stub",
                path4: "Puck.Launcher.Stub.csproj"
            );

            stubArtifact = Path.Combine(paths: [repositoryRoot, "src", "Puck.Launcher.Stub", "bin", "Release", "net10.0", "Puck.Launcher.Stub.exe"]);

            Console.Error.WriteLine(value: "canary: building Puck.Launcher.Stub once (Release).");

            var stubBuild = CliProcess.RunCaptured(
                fileName: "dotnet",
                arguments: ["build", stubProject, "-c", "Release", "--nologo", "--no-restore", "-p:NuGetAudit=false"],
                input: string.Empty,
                timeout: CliProcess.RemainingBudget(
                    budget: BuildBudget,
                    clock: buildClock
                )
            );

            if (
                stubBuild.TimedOut ||
                (stubBuild.ExitCode != 0)
            ) {
                Console.Error.WriteLine(value: (stubBuild.TimedOut
                    ? $"ERROR: the one Puck.Launcher.Stub build exceeded the {BuildBudget.TotalSeconds:0}-second build budget."
                    : $"ERROR: the one Puck.Launcher.Stub build exited {stubBuild.ExitCode}."));
                PrintCaptured(result: stubBuild);

                return CliExit.Refused;
            }
            if (!File.Exists(path: stubArtifact)) {
                Console.Error.WriteLine(value: $"ERROR: the Puck.Launcher.Stub build exited 0 but did not produce the exact artifact {stubArtifact}.");

                return CliExit.Refused;
            }

            Console.Error.WriteLine(value: $"canary: built artifact {stubArtifact}");
        }

        var failed = false;
        var infrastructureFailed = false;
        var unsupported = new List<string>();
        IReadOnlyList<CanaryProof> proofs = [.. plan.Proofs.Select(selector: static proof => proof.Proof)];
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
            Tally: new CanaryTally(),
            Total: TimeSpan.FromSeconds(value: plan.BudgetSeconds)
        );

        Console.WriteLine(value: $"canary: leg budget {budget.Total.TotalSeconds:0}s, derived from the selected manifests' own declared per-leg timeouts; up to {jobs} World process(es) at once.");

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
                    ManifestIndex = manifestIndex,
                    Run = () => results[manifestIndex, side] = run(),
                    Weight = LegWeight(
                        jobs: jobs,
                        leg: leg,
                        manifest: manifest
                    ),
                });
            }
        }

        var reported = 0;

        Console.CancelKeyPress += OnCancelKeyPress;

        try {
            RunLegsConcurrently(
                cancellation: cancellation,
                completed: item => {
                    var side = (item.Discriminating
                        ? 1
                        : 0);

                    legs[item.ManifestIndex, side] = results[item.ManifestIndex, side]!;

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
                        failed |= !verdict.Passed;
                        infrastructureFailed |= verdict.InfrastructureFailed;
                        if (verdict.Unsupported is { } reason) {
                            unsupported.Add(item: $"{proofs[reported].Label}: {reason}");
                        }
                        reported++;
                    }
                },
                jobs: jobs,
                work: work
            );
        } catch (Exception exception) {
            // Every leg has ended by now and its children are dead; what threw is an infrastructure failure.
            Console.Error.WriteLine(value: $"ERROR: a canary leg failed before it could report, and the run was stopped: {exception}");

            return CliExit.Refused;
        } finally {
            Console.CancelKeyPress -= OnCancelKeyPress;
        }

        PrintTally(
            built: (build is not null),
            endings: endings,
            plan: plan,
            tally: budget.Tally
        );

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
            Console.Error.WriteLine(value: "ERROR: this environment could not exercise every selected proof; an offscreen proof holds only when every declared backend ran, so the selection is not green.");

            return CliExit.Refused;
        }
        if (failed) {
            Console.Error.WriteLine(value: "FAIL: one or more selected canaries did not prove both their green leg and executable red leg.");

            return CliExit.Failed;
        }

        Console.WriteLine(value: $"PASS: all {manifests.Count} selected canary proof(s) held within the {budget.Total.TotalSeconds:0}-second leg budget.");

        return CliExit.Success;
    }

    // One leg waiting to run: which proof and side it answers, how many --jobs slots it holds while it runs, and the
    // run itself, which stores its own result where the completed callback reads it once the leg's thread has ended.
    internal sealed class CanaryLegWork {
        public required bool Discriminating { get; init; }
        public required int ManifestIndex { get; init; }
        public required Action Run { get; init; }
        public required int Weight { get; init; }
    }

    // How many of the --jobs slots a leg holds: one per World process it runs at once. An exclusive leg (IsExclusive)
    // holds them all, so its GPU, window, or device never shares the machine with another leg.
    internal static int LegWeight(CanaryManifest manifest, CanaryLeg leg, int jobs) {
        var processes = (IsExclusive(manifest: manifest)
            ? jobs
            : ((leg.Authorities.Count != 0)
                ? leg.Authorities.Count
                : ((leg.AuthorityWorldPath is null)
                    ? 1
                    : 2
                )
            )
        );

        return Math.Clamp(
            max: jobs,
            min: 1,
            value: processes
        );
    }
    // Starts legs strictly in authored order whenever enough slots are free, each on its own thread, and hands every
    // finished leg to completed on the calling thread. In-order starts mean a heavy leg is never passed over by lighter
    // ones behind it. Once cancellation fires no further leg starts, and no leg that finishes afterwards is reported.
    // Nothing leaves this method while a leg still runs: a leg that throws, or a completed callback that throws,
    // cancels the rest, which kills their children, and the first failure is rethrown only after every leg has ended.
    internal static void RunLegsConcurrently(IReadOnlyList<CanaryLegWork> work, int jobs, Action<CanaryLegWork> completed, CancellationTokenSource cancellation) {
        var running = new List<(Task Task, CanaryLegWork Item)>(capacity: jobs);
        var next = 0;
        var free = jobs;
        ExceptionDispatchInfo? failure = null;

        try {
            while (true) {
                while (
                    !cancellation.IsCancellationRequested &&
                    (next < work.Count) &&
                    (work[next].Weight <= free)
                ) {
                    var item = work[next++];

                    free -= item.Weight;
                    running.Add(item: (Task.Factory.StartNew(
                        action: item.Run,
                        cancellationToken: CancellationToken.None,
                        creationOptions: TaskCreationOptions.LongRunning,
                        scheduler: TaskScheduler.Default
                    ), item));
                }

                if (running.Count == 0) {
                    break;
                }

                var finished = Task.WaitAny(tasks: [.. running.Select(selector: static entry => entry.Task)]);

                var (task, done) = running[finished];

                running.RemoveAt(index: finished);
                free += done.Weight;

                if (task.Exception?.InnerException is { } exception) {
                    // A leg the cancellation stopped ends in OperationCanceledException; only another failure is one.
                    if (!((exception is OperationCanceledException) && cancellation.IsCancellationRequested)) {
                        failure ??= ExceptionDispatchInfo.Capture(source: exception);
                        cancellation.Cancel();
                    }

                    continue;
                }

                if (!cancellation.IsCancellationRequested) {
                    completed(obj: done);
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
    // that true: a leg waits only while legs started before it run, so the time spent before it starts never exceeds
    // their timeouts. Packages holds the run's shader packages, and Tally counts what the run starts.
    private readonly record struct CanaryBudget(Stopwatch Clock, TimeSpan Total, CancellationToken Cancellation, CanaryPackages Packages, CanaryTally Tally) {
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

        // The relaunch boots only after a first boot that ran its script to the end, on the document that boot wrote.
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

            var relaunchWorld = Path.Combine(
                path1: runDirectory,
                path2: relaunch.WorldFileName
            );
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
        string executionWorld
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
    // The run directory names its canary and leg after the prefix SweepScratch finds.
    private static string CreateRunDirectory(string id, string leg) =>
        Directory.CreateTempSubdirectory(prefix: $"{ScratchPrefix}{id}-{leg}-").FullName;
    private static IReadOnlyList<string> SplitLines(string text) =>
        text.ReplaceLineEndings(replacementText: "\n").Split(
            options: StringSplitOptions.RemoveEmptyEntries,
            separator: '\n'
        );
    private static void PrintCaptured(CliProcessResult result) {
        if (result.Stdout.Length != 0) {
            Console.Error.WriteLine(value: "--- build stdout ---");
            Console.Error.Write(value: result.Stdout);
        }
        if (result.Stderr.Length != 0) {
            Console.Error.WriteLine(value: "--- build stderr ---");
            Console.Error.Write(value: result.Stderr);
        }
    }
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
