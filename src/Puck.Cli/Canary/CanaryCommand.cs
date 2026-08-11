using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Puck.Cli.Canary;

/// <summary><c>puck canary</c> — bounded, two-leg behavioral proofs against the real Puck.World executable.</summary>
internal static class CanaryCommand {
    private static readonly TimeSpan SuiteBudget = TimeSpan.FromSeconds(value: 420);
    private const string ScratchPrefix = "puck-canary-";

    public static int Run(string[] args) {
        if ((Array.IndexOf(array: args, value: "-h") >= 0) || (Array.IndexOf(array: args, value: "--help") >= 0)) {
            return Usage();
        }

        if (!TryParse(args: args, selection: out var selection, error: out var parseError)) {
            Console.Error.WriteLine(value: $"ERROR: {parseError}");

            return 2;
        }
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return 2;
        }
        if (!CanaryManifestLoader.TryLoadAll(repositoryRoot: repositoryRoot, manifests: out var manifests, error: out var manifestError)) {
            Console.Error.WriteLine(value: $"ERROR: {manifestError}");

            return 2;
        }

        if (selection.Kind == CanarySelectionKind.List) {
            List(manifests: manifests, repositoryRoot: repositoryRoot);

            return 0;
        }

        if (!TrySelect(selection: selection, manifests: manifests, selected: out var selected, error: out var selectionError)) {
            Console.Error.WriteLine(value: $"ERROR: {selectionError}");

            return 2;
        }

        return RunSelected(manifests: selected, repositoryRoot: repositoryRoot, explicitAll: (selection.Kind == CanarySelectionKind.All));
    }

    private static int RunSelected(IReadOnlyList<CanaryManifest> manifests, string repositoryRoot, bool explicitAll) {
        var suiteClock = Stopwatch.StartNew();

        SweepScratch();

        if (explicitAll) {
            Console.WriteLine(value: $"canary: explicit --all selected {manifests.Count} proof(s), including any declared environmental requirements; it does not change the automatic set.");
        } else {
            Console.WriteLine(value: $"canary: selected {manifests.Count} proof(s).");
        }

        var worldProject = Path.Combine(path1: repositoryRoot, path2: "src", path3: "Puck.World", path4: "Puck.World.csproj");
        var artifact = Path.Combine(paths: [repositoryRoot, "src", "Puck.World", "bin", "Release", "net10.0", "Puck.World.dll"]);

        Console.WriteLine(value: "canary: building Puck.World once (Release).");

        CliProcessResult build;
        try {
            build = CliProcess.RunCaptured(
                fileName: "dotnet",
                arguments: ["build", worldProject, "-c", "Release", "--nologo", "--no-restore", "-p:NuGetAudit=false"],
                input: string.Empty,
                timeout: RemainingBudget(clock: suiteClock)
            );
        } catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) {
            Console.Error.WriteLine(value: $"ERROR: could not start the one Puck.World build: {exception.Message.ReplaceLineEndings(replacementText: " ")}");

            return 2;
        }

        if (build.TimedOut || (build.ExitCode != 0)) {
            Console.Error.WriteLine(value: build.TimedOut
                ? $"ERROR: the one Puck.World build exceeded the {SuiteBudget.TotalSeconds:0}-second whole-suite budget."
                : $"ERROR: the one Puck.World build exited {build.ExitCode}.");
            PrintCaptured(result: build);

            return 2;
        }
        if (!File.Exists(path: artifact)) {
            Console.Error.WriteLine(value: $"ERROR: the Puck.World build exited 0 but did not produce the exact artifact {artifact}.");

            return 2;
        }

        Console.WriteLine(value: $"canary: built artifact {artifact}");

        var failed = false;
        var infrastructureFailed = false;
        foreach (var manifest in manifests) {
            var positive = RunLeg(manifest: manifest, leg: manifest.Positive, artifact: artifact, suiteClock: suiteClock);
            var discriminating = RunLeg(manifest: manifest, leg: manifest.Discriminating, artifact: artifact, suiteClock: suiteClock);

            ReportLeg(id: manifest.Id, result: positive);
            ReportLeg(id: manifest.Id, result: discriminating);

            var positivePassed = positive.Passed;
            var discriminatingPassed = discriminating.Passed;
            infrastructureFailed |= ((positive.InfrastructureError is not null) || (discriminating.InfrastructureError is not null));
            var positiveOnDiscriminating = CanaryAssertions.Evaluate(leg: manifest.Positive, transcript: discriminating.Transcript);
            var turnedRed = !positiveOnDiscriminating.Passed;

            if (turnedRed) {
                Console.WriteLine(value: $"canary {manifest.Id} discriminating: FAIL (expected) — the positive observation turned red under the alternate authored world/input.");
                foreach (var result in positiveOnDiscriminating.Results.Where(predicate: static result => !result.Passed)) {
                    Console.WriteLine(value: $"  expected-red: {result.Detail}");
                }
            } else {
                Console.Error.WriteLine(value: $"canary {manifest.Id} discriminating: UNEXPECTED PASS — the positive observation survived the declared discriminator, so the proof is not sensitive.");
            }

            if (positivePassed && discriminatingPassed && turnedRed) {
                Console.WriteLine(value: $"PASS: canary {manifest.Id} — positive held, opposite observation held, and the positive observation failed in the discriminating leg.");
            } else {
                Console.Error.WriteLine(value: $"FAIL: canary {manifest.Id} — positive={Verdict(value: positivePassed)}, opposite={Verdict(value: discriminatingPassed)}, discriminator-turned-red={Verdict(value: turnedRed)}.");
                failed = true;
            }
        }

        if (infrastructureFailed) {
            Console.Error.WriteLine(value: "ERROR: one or more selected canary legs could not run because infrastructure refused.");

            return 2;
        }
        if (failed) {
            Console.Error.WriteLine(value: "FAIL: one or more selected canaries did not prove both their green leg and executable red leg.");

            return 1;
        }

        Console.WriteLine(value: $"PASS: all {manifests.Count} selected canary proof(s) held within the {SuiteBudget.TotalSeconds:0}-second suite budget.");

        return 0;
    }

    private static CanaryLegRun RunLeg(CanaryManifest manifest, CanaryLeg leg, string artifact, Stopwatch suiteClock) {
        try {
            return RunLegCore(manifest: manifest, leg: leg, artifact: artifact, suiteClock: suiteClock);
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) {
            return CanaryLegRun.InfrastructureFailure(
                leg: leg,
                runDirectory: "not-created",
                reason: exception.Message.ReplaceLineEndings(replacementText: " ")
            );
        }
    }

    private static CanaryLegRun RunLegCore(CanaryManifest manifest, CanaryLeg leg, string artifact, Stopwatch suiteClock) {
        var runDirectory = CreateRunDirectory(id: manifest.Id, leg: leg.Name);
        var stateDirectory = Path.Combine(path1: runDirectory, path2: "state");
        var stdoutPath = Path.Combine(path1: runDirectory, path2: "stdout.log");
        var stderrPath = Path.Combine(path1: runDirectory, path2: "stderr.log");
        var input = File.ReadAllText(path: leg.ScriptPath).Replace(oldValue: "{run}", newValue: runDirectory.Replace(oldChar: '\\', newChar: '/'), comparisonType: StringComparison.Ordinal);
        var executionWorld = leg.WorldPath;
        var authorityExecutionWorld = leg.AuthorityWorldPath;
        string? federationKeyPath = null;

        if (leg.AuthorityWorldPath is not null) {
            (executionWorld, authorityExecutionWorld) = PrepareFederatedWorlds(leg: leg, runDirectory: runDirectory);
            federationKeyPath = Path.Combine(path1: runDirectory, path2: "federation.key");
            var federationKey = new byte[32];
            System.Security.Cryptography.RandomNumberGenerator.Fill(data: federationKey);
            File.WriteAllBytes(path: federationKeyPath, bytes: federationKey);
        }

        if (!input.EndsWith(value: '\n')) {
            input += Environment.NewLine;
        }

        // Runner-owned completion: this line follows every authored byte, stdin is closed immediately after it, and
        // the exact response count is checked. A process that merely lived until --exit-after-seconds cannot satisfy it.
        input += $"wire.errors{Environment.NewLine}";

        var remaining = RemainingBudget(clock: suiteClock);
        var timeout = TimeSpan.FromSeconds(value: Math.Min(val1: manifest.TimeoutSeconds, val2: remaining.TotalSeconds));
        if (timeout <= TimeSpan.Zero) {
            return CanaryLegRun.BudgetExpired(leg: leg, runDirectory: runDirectory);
        }

        CliProcessResult process;
        AuthorityCompanion? authority = null;
        try {
            if (authorityExecutionWorld is { } authorityWorld) {
                authority = AuthorityCompanion.Start(artifact: artifact, world: authorityWorld, stateDirectory: Path.Combine(path1: runDirectory, path2: "authority-state"), seconds: (manifest.Seconds + 2), federationKeyPath: federationKeyPath!);
                if (!authority.WaitUntilListening(timeout: TimeSpan.FromSeconds(5))) {
                    authority.Dispose();
                    return CanaryLegRun.InfrastructureFailure(leg: leg, runDirectory: runDirectory, reason: "companion authority did not report a bound listener within 5 seconds");
                }
            }

            process = CliProcess.RunCaptured(
                fileName: "dotnet",
                arguments: [
                    artifact,
                    "--world", executionWorld,
                    .. (leg.Connect ? new[] { "--connect", "127.0.0.1:38473" } : []),
                    .. (federationKeyPath is null ? [] : new[] { "--federation-key-file", federationKeyPath }),
                    "--exit-after-seconds", manifest.Seconds.ToString(provider: CultureInfo.InvariantCulture),
                    "--state-dir", stateDirectory,
                    "--headless", ((manifest.BootShape == CanaryBootShape.Headless) ? "true" : "false"),
                ],
                input: input,
                timeout: timeout
            );
        } catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) {
            return CanaryLegRun.InfrastructureFailure(leg: leg, runDirectory: runDirectory, reason: exception.Message.ReplaceLineEndings(replacementText: " "));
        } finally {
            if (authority is not null) {
                File.WriteAllText(path: Path.Combine(path1: runDirectory, path2: "authority-stdout.log"), contents: authority.Stdout, encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                File.WriteAllText(path: Path.Combine(path1: runDirectory, path2: "authority-stderr.log"), contents: authority.Stderr, encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                authority.Dispose();
            }
        }

        File.WriteAllText(path: stdoutPath, contents: process.Stdout, encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(path: stderrPath, contents: process.Stderr, encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var transcript = new CanaryTranscript(RunDirectory: runDirectory, Stderr: SplitLines(text: process.Stderr), Stdout: SplitLines(text: process.Stdout));
        var assertions = CanaryAssertions.Evaluate(leg: leg, transcript: transcript);
        var invariants = EvaluateRunnerInvariants(manifest: manifest, leg: leg, process: process, transcript: transcript, executionWorld: executionWorld);

        return new CanaryLegRun(
            Assertions: assertions,
            ExitCode: process.ExitCode,
            InfrastructureError: null,
            Invariants: invariants,
            Leg: leg,
            RunDirectory: runDirectory,
            TimedOut: process.TimedOut,
            Transcript: transcript
        );
    }

    private static (string ClientWorld, string AuthorityWorld) PrepareFederatedWorlds(CanaryLeg leg, string runDirectory) {
        const string endpoint = "127.0.0.1:38473";
        var sourceDirectory = Path.GetDirectoryName(path: leg.WorldPath)!;
        var federatedDirectory = Path.Combine(path1: runDirectory, path2: "federated-worlds");
        Directory.CreateDirectory(path: federatedDirectory);

        foreach (var source in Directory.GetFiles(path: sourceDirectory, searchPattern: "*.world.json", searchOption: SearchOption.TopDirectoryOnly)) {
            File.Copy(sourceFileName: source, destFileName: Path.Combine(path1: federatedDirectory, path2: Path.GetFileName(path: source)), overwrite: true);
        }

        var authoritySource = leg.AuthorityWorldPath!;
        var authorityTarget = Path.Combine(path1: federatedDirectory, path2: Path.GetFileName(path: authoritySource));
        if (!File.Exists(path: authorityTarget)) {
            File.Copy(sourceFileName: authoritySource, destFileName: authorityTarget, overwrite: true);
        }

        var root = (JsonNode.Parse(json: File.ReadAllText(path: authorityTarget))?.AsObject()
            ?? throw new InvalidOperationException("authority world is not a JSON object"));
        var host = (root["host"]?.AsObject() ?? throw new InvalidOperationException("authority world has no host object"));
        host["listen"] = endpoint;
        host["authority"] = endpoint;
        if (root["population"] is JsonObject population) {
            population["capacity"] = Math.Max(8, population["capacity"]?.GetValue<int>() ?? 8);
            population["networkPlayers"] = Math.Max(4, population["networkPlayers"]?.GetValue<int>() ?? 4);
        }
        File.WriteAllText(path: authorityTarget, contents: root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return (leg.Connect ? leg.WorldPath : Path.Combine(path1: federatedDirectory, path2: Path.GetFileName(path: leg.WorldPath)), authorityTarget);
    }

    private sealed class AuthorityCompanion : IDisposable {
        private readonly Process m_process;
        private readonly StringBuilder m_stdout = new();
        private readonly StringBuilder m_stderr = new();
        private readonly object m_gate = new();

        private AuthorityCompanion(Process process) {
            m_process = process;
            process.OutputDataReceived += (_, args) => {
                if (args.Data is { } line) {
                    lock (m_gate) {
                        m_stdout.AppendLine(value: line);
                    }
                }
            };
            process.ErrorDataReceived += (_, args) => {
                if (args.Data is { } line) {
                    lock (m_gate) {
                        m_stderr.AppendLine(value: line);
                    }
                }
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.StandardInput.Close();
        }

        public string Stdout { get { lock (m_gate) { return m_stdout.ToString(); } } }
        public string Stderr { get { lock (m_gate) { return m_stderr.ToString(); } } }

        public static AuthorityCompanion Start(string artifact, string world, string stateDirectory, int seconds, string federationKeyPath) {
            var startInfo = new ProcessStartInfo {
                CreateNoWindow = true,
                FileName = "dotnet",
                RedirectStandardError = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            foreach (var argument in new[] { artifact, "--world", world, "--exit-after-seconds", seconds.ToString(provider: CultureInfo.InvariantCulture), "--state-dir", stateDirectory, "--headless", "true", "--federation-key-file", federationKeyPath }) {
                startInfo.ArgumentList.Add(item: argument);
            }
            return new AuthorityCompanion(process: Process.Start(startInfo: startInfo) ?? throw new InvalidOperationException("failed to start companion authority"));
        }

        public bool WaitUntilListening(TimeSpan timeout) {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < timeout) {
                lock (m_gate) {
                    if (m_stderr.ToString().Contains(value: "[world.listen: bound ", comparisonType: StringComparison.Ordinal)) {
                        return true;
                    }
                }
                if (m_process.HasExited) {
                    return false;
                }
                _ = m_process.WaitForExit(milliseconds: 25);
            }
            return false;
        }

        public void Dispose() {
            if (!m_process.HasExited) {
                try { m_process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                m_process.WaitForExit();
            }
            m_process.Dispose();
        }
    }

    private static IReadOnlyList<CanaryAssertionResult> EvaluateRunnerInvariants(
        CanaryManifest manifest,
        CanaryLeg leg,
        CliProcessResult process,
        CanaryTranscript transcript,
        string executionWorld
    ) {
        var results = new List<CanaryAssertionResult> {
            new(Detail: $"process exited 0 (actual {process.ExitCode})", Passed: (!process.TimedOut && (process.ExitCode == 0))),
            new(Detail: $"leg completed before {manifest.TimeoutSeconds}s timeout", Passed: !process.TimedOut),
        };
        var origin = $"[world] definition: {executionWorld} (--world)";

        results.Add(item: new CanaryAssertionResult(
            Detail: "stderr names the exact absolute --world origin",
            Passed: transcript.Stderr.Any(predicate: line => string.Equals(a: line, b: origin, comparisonType: StringComparison.Ordinal))
        ));

        var expectedRefusals = leg.Commands.Count(predicate: static claim => (claim.Outcome == CanaryCommandOutcome.Refused));
        var byVerb = leg.Commands.GroupBy(keySelector: static claim => claim.Verb, comparer: StringComparer.Ordinal);
        foreach (var group in byVerb) {
            var responseEvents = process.OutputLines
                .Where(predicate: line => line.Line.StartsWith(value: $"[{group.Key}:", comparisonType: StringComparison.Ordinal))
                .ToList();
            var claims = group.OrderBy(keySelector: static claim => claim.Occurrence).ToList();
            var terminalAdjustment = ((group.Key == "wire.errors") ? 1 : 0);
            var countPassed = (responseEvents.Count == (claims.Count + terminalAdjustment));

            results.Add(item: new CanaryAssertionResult(
                Detail: $"accounted {group.Key}: {responseEvents.Count} response(s) for {claims.Count} authored occurrence(s){((terminalAdjustment == 1) ? " plus terminal observation" : string.Empty)}",
                Passed: countPassed
            ));

            if (countPassed) {
                for (var index = 0; (index < claims.Count); index++) {
                    var expectedStream = ((claims[index].Outcome == CanaryCommandOutcome.Accepted) ? CliProcessOutputStream.Stdout : CliProcessOutputStream.Stderr);

                    results.Add(item: new CanaryAssertionResult(
                        Detail: $"{group.Key} occurrence {claims[index].Occurrence} was {claims[index].Outcome.ToString().ToLowerInvariant()}",
                        Passed: (responseEvents[index].Stream == expectedStream)
                    ));
                }
            }
        }

        var terminalResponses = process.OutputLines
            .Where(predicate: static line => line.Line.StartsWith(value: "[wire.errors:", comparisonType: StringComparison.Ordinal))
            .ToArray();
        var expectedTerminal = $"[wire.errors: {expectedRefusals} rejected]";
        var terminalPassed = (terminalResponses.Length != 0) && (terminalResponses[^1].Stream == CliProcessOutputStream.Stdout) && string.Equals(a: terminalResponses[^1].Line, b: expectedTerminal, comparisonType: StringComparison.Ordinal);

        results.Add(item: new CanaryAssertionResult(
            Detail: $"runner-owned terminal observation is exact '{expectedTerminal}' after all authored commands",
            Passed: terminalPassed
        ));

        return results;
    }

    private static void ReportLeg(string id, CanaryLegRun result) {
        Console.WriteLine(value: $"canary {id} {result.Leg.Name}: transcripts {result.RunDirectory}");

        if (result.InfrastructureError is { } infrastructureError) {
            Console.Error.WriteLine(value: $"  FAIL: infrastructure: {infrastructureError}");

            return;
        }

        foreach (var invariant in result.Invariants) {
            Console.WriteLine(value: $"  {(invariant.Passed ? "PASS" : "FAIL")}: {invariant.Detail}");
        }
        foreach (var assertion in result.Assertions.Results) {
            Console.WriteLine(value: $"  {(assertion.Passed ? "PASS" : "FAIL")}: {assertion.Detail}");
        }
    }

    private static void List(IReadOnlyList<CanaryManifest> manifests, string repositoryRoot) {
        foreach (var manifest in manifests) {
            var requirements = ((manifest.Requirements.Count == 0) ? "none" : string.Join(separator: ',', values: manifest.Requirements));

            Console.WriteLine(value: $"{manifest.Id} shape={manifest.BootShape.ToString().ToLowerInvariant()} requirements={requirements} automatic={manifest.IsAutomatic.ToString().ToLowerInvariant()}");
            Console.WriteLine(value: $"  world: {CliPaths.ToDisplay(relativeTo: repositoryRoot, fullPath: manifest.Positive.WorldPath)}");
            Console.WriteLine(value: $"  binding: {manifest.Binding}");
        }
    }

    private static bool TrySelect(CanarySelection selection, IReadOnlyList<CanaryManifest> manifests, out IReadOnlyList<CanaryManifest> selected, out string error) {
        error = string.Empty;
        selected = selection.Kind switch {
            CanarySelectionKind.Automatic => manifests.Where(predicate: static manifest => manifest.IsAutomatic).ToArray(),
            CanarySelectionKind.All => manifests,
            CanarySelectionKind.Capability => FilterCapability(manifests: manifests, capability: selection.Capability!),
            CanarySelectionKind.Ids => SelectIds(manifests: manifests, ids: selection.Ids),
            _ => [],
        };

        if (selection.Kind == CanarySelectionKind.Ids) {
            var known = manifests.Select(selector: static manifest => manifest.Id).ToHashSet(comparer: StringComparer.Ordinal);
            var unknown = selection.Ids.Where(predicate: id => !known.Contains(item: id)).ToArray();
            if (unknown.Length != 0) {
                error = $"unknown canary id(s): {string.Join(separator: ", ", values: unknown)}; an unknown selection cannot report green.";

                return false;
            }
        }

        if (selected.Count == 0) {
            error = selection.Kind switch {
                CanarySelectionKind.Automatic => "the automatic set is empty; landing cannot report green without a headless, requirement-free proof.",
                CanarySelectionKind.Capability => $"capability filter '{selection.Capability}' selected zero canaries; an empty selection cannot report green.",
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
        _ => manifests.Where(predicate: manifest => manifest.Requirements.Contains(value: capability, comparer: StringComparer.Ordinal)).ToArray(),
    };

    private static IReadOnlyList<CanaryManifest> SelectIds(IReadOnlyList<CanaryManifest> manifests, IReadOnlyList<string> ids) {
        var byId = manifests.ToDictionary(keySelector: static manifest => manifest.Id, comparer: StringComparer.Ordinal);

        return ids.Where(predicate: byId.ContainsKey).Select(selector: id => byId[id]).ToArray();
    }

    private static bool TryParse(string[] args, out CanarySelection selection, out string error) {
        selection = new CanarySelection(Capability: null, Ids: [], Kind: CanarySelectionKind.Automatic);
        error = string.Empty;

        if (args.Length == 0) {
            return true;
        }

        if ((args.Length == 1) && (args[0] == "--list")) {
            selection = selection with { Kind = CanarySelectionKind.List };

            return true;
        }
        if ((args.Length == 1) && (args[0] == "--all")) {
            selection = selection with { Kind = CanarySelectionKind.All };

            return true;
        }
        if ((args.Length == 2) && (args[0] == "--capability")) {
            var capability = args[1];
            var valid = (capability is "automatic" or "headless" or "windowed" or "gpu" or "audio-output") || (capability.StartsWith(value: "input:", comparisonType: StringComparison.Ordinal) && (capability.Length > 6));
            if (!valid) {
                error = $"unknown capability filter '{capability}'; use automatic, headless, windowed, gpu, audio-output, or input:<hardware-name>.";

                return false;
            }

            selection = new CanarySelection(Capability: capability, Ids: [], Kind: CanarySelectionKind.Capability);

            return true;
        }
        if (args.Any(predicate: static argument => argument.StartsWith(value: "-", comparisonType: StringComparison.Ordinal))) {
            error = "ids, --all, --list, and --capability <class> are mutually exclusive selection forms.";

            return false;
        }

        var duplicate = args.GroupBy(keySelector: static id => id, comparer: StringComparer.Ordinal).FirstOrDefault(predicate: static group => group.Count() > 1);
        if (duplicate is not null) {
            error = $"duplicate canary id '{duplicate.Key}' in the selection; one proof must not be counted twice.";

            return false;
        }

        selection = new CanarySelection(Capability: null, Ids: args, Kind: CanarySelectionKind.Ids);

        return true;
    }

    private static string CreateRunDirectory(string id, string leg) {
        var temp = Path.GetTempPath();
        for (var attempt = 0; (attempt < 8); attempt++) {
            var path = Path.Combine(path1: temp, path2: $"{ScratchPrefix}{Guid.NewGuid():N}-{id}-{leg}");
            if (Directory.Exists(path: path)) {
                continue;
            }

            Directory.CreateDirectory(path: path);

            return path;
        }

        throw new IOException(message: "Could not create a fresh random canary run directory after 8 attempts.");
    }

    private static void SweepScratch() {
        var threshold = DateTime.UtcNow.AddHours(value: -6);

        try {
            foreach (var directory in Directory.EnumerateDirectories(path: Path.GetTempPath(), searchPattern: $"{ScratchPrefix}*", searchOption: SearchOption.TopDirectoryOnly)) {
                try {
                    if (Directory.GetCreationTimeUtc(path: directory) < threshold) {
                        Directory.Delete(path: directory, recursive: true);
                    }
                } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                    // A best-effort age-bounded sweep never makes this run fail or touches a fresh sibling run.
                }
            }
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            // Enumerating temp is best-effort for the same reason as deleting an old entry.
        }
    }

    private static TimeSpan RemainingBudget(Stopwatch clock) {
        var remaining = (SuiteBudget - clock.Elapsed);

        return ((remaining > TimeSpan.Zero) ? remaining : TimeSpan.FromMilliseconds(value: 1));
    }

    private static IReadOnlyList<string> SplitLines(string text) =>
        text.ReplaceLineEndings(replacementText: "\n").Split(separator: '\n', options: StringSplitOptions.RemoveEmptyEntries);

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

    private static string Verdict(bool value) => (value ? "PASS" : "FAIL");

    private static int Usage() {
        Console.Error.WriteLine(
            value:
                """
                canary [<id> ...] | --all | --list | --capability <class>

                  no selection           run the automatic set: headless shape and no environmental requirements
                  <id> ...               run the named proofs explicitly, regardless of requirements
                  --all                  explicitly run every proof; does not promote any proof into the automatic set
                  --list                 strictly load and list every manifest without building or running
                  --capability <class>   filter by automatic, headless, windowed, gpu, audio-output, or input:<name>
                  -h / --help            this text

                The four selection forms are mutually exclusive. Every execution refuses an empty selection,
                builds Puck.World once, then runs each positive and discriminating leg sequentially from fresh state.

                Exit codes: 0 all proofs held, 1 an observed proof failed, 2 refusal/infrastructure/usage.
                """);

        return 2;
    }

    private enum CanarySelectionKind {
        Automatic,
        All,
        Capability,
        Ids,
        List,
    }

    private sealed record CanarySelection(string? Capability, IReadOnlyList<string> Ids, CanarySelectionKind Kind);

    private sealed record CanaryLegRun(
        CanaryEvaluation Assertions,
        int ExitCode,
        string? InfrastructureError,
        IReadOnlyList<CanaryAssertionResult> Invariants,
        CanaryLeg Leg,
        string RunDirectory,
        bool TimedOut,
        CanaryTranscript Transcript
    ) {
        public bool Passed => (InfrastructureError is null) && !TimedOut && (ExitCode == 0) && Invariants.All(predicate: static result => result.Passed) && Assertions.Passed;

        public static CanaryLegRun BudgetExpired(CanaryLeg leg, string runDirectory) =>
            InfrastructureFailure(leg: leg, runDirectory: runDirectory, reason: $"the {SuiteBudget.TotalSeconds:0}-second whole-suite budget was exhausted before this leg started");

        public static CanaryLegRun InfrastructureFailure(CanaryLeg leg, string runDirectory, string reason) => new(
            Assertions: new CanaryEvaluation(Results: []),
            ExitCode: -1,
            InfrastructureError: reason,
            Invariants: [],
            Leg: leg,
            RunDirectory: runDirectory,
            TimedOut: false,
            Transcript: new CanaryTranscript(RunDirectory: runDirectory, Stderr: [], Stdout: [])
        );
    }
}
