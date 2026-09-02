using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Puck.Cli.Canary;

/// <summary><c>puck canary</c> — bounded, two-leg behavioral proofs against the real Puck.World executable.</summary>
internal static partial class CanaryCommand {
    // The one-time Release builds run against their own allowance on their own clock, so build time can never be
    // spent out of a proof's. Leg time is a separate budget derived from the selection itself; see LegBudget.
    private static readonly TimeSpan BuildBudget = TimeSpan.FromSeconds(value: 600);

    private const string ScratchPrefix = "puck-canary-";

    public static int Run(string[] args) {
        if ((Array.IndexOf(array: args, value: "-h") >= 0) || (Array.IndexOf(array: args, value: "--help") >= 0)) {
            return Usage();
        }

        if (!TryParse(args: args, error: out var parseError, selection: out var selection)) {
            Console.Error.WriteLine(value: $"ERROR: {parseError}");

            return 2;
        }
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return 2;
        }
        if (!CanaryManifestLoader.TryLoadAll(error: out var manifestError, manifests: out var manifests, repositoryRoot: repositoryRoot)) {
            Console.Error.WriteLine(value: $"ERROR: {manifestError}");

            return 2;
        }

        if (selection.Kind == CanarySelectionKind.List) {
            List(manifests: manifests, repositoryRoot: repositoryRoot);

            return 0;
        }

        if (!TrySelect(error: out var selectionError, manifests: manifests, selected: out var selected, selection: selection)) {
            Console.Error.WriteLine(value: $"ERROR: {selectionError}");

            return 2;
        }

        return RunSelected(manifests: selected, repositoryRoot: repositoryRoot, explicitAll: (selection.Kind == CanarySelectionKind.All));
    }

    private static int RunSelected(IReadOnlyList<CanaryManifest> manifests, string repositoryRoot, bool explicitAll) {
        var buildClock = Stopwatch.StartNew();

        CliScratchDirectories.SweepScratch(scratchPrefix: ScratchPrefix);

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
                timeout: CliProcess.RemainingBudget(budget: BuildBudget, clock: buildClock)
            );
        } catch (Exception exception) when ((exception is InvalidOperationException or System.ComponentModel.Win32Exception)) {
            Console.Error.WriteLine(value: $"ERROR: could not start the one Puck.World build: {exception.Message.ReplaceLineEndings(replacementText: " ")}");

            return 2;
        }

        if (build.TimedOut || (build.ExitCode != 0)) {
            Console.Error.WriteLine(value: (build.TimedOut
                ? $"ERROR: the one Puck.World build exceeded the {BuildBudget.TotalSeconds:0}-second build budget."
                : $"ERROR: the one Puck.World build exited {build.ExitCode}."));
            PrintCaptured(result: build);

            return 2;
        }
        if (!File.Exists(path: artifact)) {
            Console.Error.WriteLine(value: $"ERROR: the Puck.World build exited 0 but did not produce the exact artifact {artifact}.");

            return 2;
        }

        Console.WriteLine(value: $"canary: built artifact {artifact}");

        // A stub-shaped leg launches Puck.Launcher.Stub, never Puck.World.dll directly, from a leg-private install
        // tree — built once here, exactly like Puck.World above, only when a selected manifest actually needs it.
        string? stubArtifact = null;

        if (manifests.Any(predicate: static manifest => (manifest.BootShape == CanaryBootShape.Stub))) {
            var stubProject = Path.Combine(path1: repositoryRoot, path2: "src", path3: "Puck.Launcher.Stub", path4: "Puck.Launcher.Stub.csproj");

            stubArtifact = Path.Combine(paths: [repositoryRoot, "src", "Puck.Launcher.Stub", "bin", "Release", "net10.0", "Puck.Launcher.Stub.exe"]);

            Console.WriteLine(value: "canary: building Puck.Launcher.Stub once (Release).");

            var stubBuild = CliProcess.RunCaptured(
                fileName: "dotnet",
                arguments: ["build", stubProject, "-c", "Release", "--nologo", "--no-restore", "-p:NuGetAudit=false"],
                input: string.Empty,
                timeout: CliProcess.RemainingBudget(budget: BuildBudget, clock: buildClock)
            );

            if (stubBuild.TimedOut || (stubBuild.ExitCode != 0)) {
                Console.Error.WriteLine(value: (stubBuild.TimedOut
                    ? $"ERROR: the one Puck.Launcher.Stub build exceeded the {BuildBudget.TotalSeconds:0}-second build budget."
                    : $"ERROR: the one Puck.Launcher.Stub build exited {stubBuild.ExitCode}."));
                PrintCaptured(result: stubBuild);

                return 2;
            }
            if (!File.Exists(path: stubArtifact)) {
                Console.Error.WriteLine(value: $"ERROR: the Puck.Launcher.Stub build exited 0 but did not produce the exact artifact {stubArtifact}.");

                return 2;
            }

            Console.WriteLine(value: $"canary: built artifact {stubArtifact}");
        }

        var failed = false;
        var infrastructureFailed = false;
        var budget = new CanaryBudget(Clock: Stopwatch.StartNew(), Total: LegBudget(manifests: manifests));

        Console.WriteLine(value: $"canary: leg budget {budget.Total.TotalSeconds:0}s, derived from the selected manifests' own declared per-leg timeouts.");

        foreach (var manifest in manifests) {
            CanaryLegRun positive;
            CanaryLegRun discriminating;

            if (manifest.BootShape == CanaryBootShape.Stub) {
                positive = RunStubLeg(budget: budget, discriminating: false, manifest: manifest, stubArtifact: stubArtifact!, worldArtifact: artifact);
                discriminating = RunStubLeg(budget: budget, discriminating: true, manifest: manifest, stubArtifact: stubArtifact!, worldArtifact: artifact);
            } else {
                positive = RunEitherLeg(artifact: artifact, budget: budget, leg: manifest.Positive, manifest: manifest);
                discriminating = RunEitherLeg(artifact: artifact, budget: budget, leg: manifest.Discriminating, manifest: manifest);
            }

            ReportLeg(id: manifest.Id, result: positive);
            ReportLeg(id: manifest.Id, result: discriminating);

            var positivePassed = positive.Passed;
            var discriminatingPassed = discriminating.Passed;

            infrastructureFailed |= ((positive.InfrastructureError is not null) || (discriminating.InfrastructureError is not null));
            var positiveOnDiscriminating = CanaryAssertions.Evaluate(authorityEndpoint: discriminating.AuthorityEndpoint, authorityTranscripts: discriminating.AuthorityTranscripts, leg: manifest.Positive, primaryTranscript: discriminating.Transcript);
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

        Console.WriteLine(value: $"PASS: all {manifests.Count} selected canary proof(s) held within the {budget.Total.TotalSeconds:0}-second leg budget.");

        return 0;
    }

    /// <summary>The suite's leg clock and the ceiling it is measured against.</summary>
    private readonly record struct CanaryBudget(Stopwatch Clock, TimeSpan Total) {
        public TimeSpan Remaining => CliProcess.RemainingBudget(budget: Total, clock: Clock);
    }

    // A leg may run only under the whole timeout its manifest declares, never a clamped remainder: a child killed by
    // a short timeout reports exit -1 with empty streams, which reads as a failure to launch rather than as the
    // budget refusal it is. The total below is therefore the exact sum of what every selected leg may spend, so a
    // leg is refused only when an earlier one overran its own declared ceiling.
    private static TimeSpan LegBudget(IReadOnlyList<CanaryManifest> manifests) =>
        TimeSpan.FromSeconds(value: manifests.Sum(selector: static manifest => ((BootsPerLeg(shape: manifest.BootShape) * 2) * manifest.TimeoutSeconds)));
    // A stub-shaped leg observes two successive launches against one declared timeout each; every other shape is one.
    private static int BootsPerLeg(CanaryBootShape shape) => ((shape == CanaryBootShape.Stub) ? 2 : 1);
    private static CanaryLegRun RunEitherLeg(string artifact, CanaryBudget budget, CanaryLeg leg, CanaryManifest manifest) =>
        ((leg.Authorities.Count != 0)
            ? RunFederatedMeshLeg(artifact: artifact, budget: budget, leg: leg, manifest: manifest)
            : RunLeg(artifact: artifact, budget: budget, leg: leg, manifest: manifest));
    private static CanaryLegRun RunLeg(CanaryManifest manifest, CanaryLeg leg, string artifact, CanaryBudget budget) {
        try {
            return RunLegCore(artifact: artifact, budget: budget, leg: leg, manifest: manifest);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or ArgumentException)) {
            return CanaryLegRun.InfrastructureFailure(
                leg: leg,
                runDirectory: "not-created",
                reason: exception.Message.ReplaceLineEndings(replacementText: " ")
            );
        }
    }
    private static CanaryLegRun RunLegCore(CanaryManifest manifest, CanaryLeg leg, string artifact, CanaryBudget budget) {
        var runDirectory = CreateRunDirectory(id: manifest.Id, leg: leg.Name);
        var stateDirectory = Path.Combine(path1: runDirectory, path2: "state");
        var stdoutPath = Path.Combine(path1: runDirectory, path2: "stdout.log");
        var stderrPath = Path.Combine(path1: runDirectory, path2: "stderr.log");
        // {run} names the per-leg ephemeral scratch directory (a script-written output path); {fixtures} names
        // the manifest's OWN checked-in directory (a script-read input path, e.g. a document a script loads by
        // name) — the process this line's text feeds does not inherit the repository root as its own working
        // directory, so a script cannot address either by a bare relative path.
        var input = File.ReadAllText(path: leg.ScriptPath)
            .Replace(oldValue: "{fixtures}", newValue: manifest.DirectoryPath.Replace(newChar: '/', oldChar: '\\'), comparisonType: StringComparison.Ordinal)
            .Replace(oldValue: "{run}", newValue: runDirectory.Replace(newChar: '/', oldChar: '\\'), comparisonType: StringComparison.Ordinal);
        var executionWorld = leg.WorldPath;
        var authorityExecutionWorld = leg.AuthorityWorldPath;
        string? clientFederationKeyPath = null;
        string? authorityFederationKeyPath = null;
        // Bound to a free loopback port per leg, exactly as a mesh leg's authorities are: several manifests carry a
        // companion authority, and a fixed port shared between them cannot be rebound while the previous leg's
        // sockets are still draining through TIME_WAIT — the companion then never reports a listener and the leg
        // fails as infrastructure, later in a run than the identical leg selected on its own.
        var federationEndpoint = string.Empty;

        if (leg.AuthorityWorldPath is not null) {
            // Each side gets its OWN ECDSA identity, pinned into the OTHER side's admission rows —
            // WorldAttestedAuthenticator verifies a signed claim against the reading world's own trust list, never a
            // shared bearer secret one process could sign the other's namespace with.
            var clientIdentity = GenerateFederationIdentity();
            var authorityIdentity = GenerateFederationIdentity();

            federationEndpoint = $"127.0.0.1:{GetFreeLoopbackPort()}";
            (executionWorld, authorityExecutionWorld) = PrepareFederatedWorlds(authorityIdentity: authorityIdentity, clientIdentity: clientIdentity, endpoint: federationEndpoint, leg: leg, runDirectory: runDirectory);
            clientFederationKeyPath = Path.Combine(path1: runDirectory, path2: "client-federation.key");
            authorityFederationKeyPath = Path.Combine(path1: runDirectory, path2: "authority-federation.key");
            File.WriteAllBytes(path: clientFederationKeyPath, bytes: clientIdentity.Pkcs8);
            File.WriteAllBytes(path: authorityFederationKeyPath, bytes: authorityIdentity.Pkcs8);
        }

        if (!input.EndsWith(value: '\n')) {
            input += Environment.NewLine;
        }

        // Runner-owned completion: this line follows every authored byte, stdin is closed immediately after it, and
        // the exact response count is checked. A process that merely lived until --exit-after-seconds cannot satisfy it.
        input += $"wire.errors{Environment.NewLine}";

        var timeout = TimeSpan.FromSeconds(value: manifest.TimeoutSeconds);

        if (budget.Remaining < timeout) {
            return CanaryLegRun.BudgetExpired(budget: budget, leg: leg, runDirectory: runDirectory);
        }

        CliProcessResult process;
        AuthorityCompanion? authority = null;

        try {
            if (authorityExecutionWorld is { } authorityWorld) {
                authority = AuthorityCompanion.Start(artifact: artifact, world: authorityWorld, stateDirectory: Path.Combine(path1: runDirectory, path2: "authority-state"), seconds: (manifest.Seconds + 2), federationKeyPath: authorityFederationKeyPath!);
                if (!authority.WaitUntilListening(timeout: TimeSpan.FromSeconds(seconds: 5))) {
                    return CanaryLegRun.InfrastructureFailure(leg: leg, reason: "companion authority did not report a bound listener within 5 seconds", runDirectory: runDirectory);
                }
            }

            process = CliProcess.RunCaptured(
                fileName: "dotnet",
                arguments: [
                    artifact,
                    "--world", executionWorld,
                            .. (leg.Connect ? new[] { "--connect", federationEndpoint } : []),
                    .. ((clientFederationKeyPath is null) ? [] : new[] { "--federation-key-file", clientFederationKeyPath }),
                    "--exit-after-seconds", manifest.Seconds.ToString(provider: CultureInfo.InvariantCulture),
                    "--state-dir", stateDirectory,
                    "--headless", ((manifest.BootShape == CanaryBootShape.Headless) ? "true" : "false"),
                ],
                input: input,
                timeout: timeout
            );
        } catch (Exception exception) when ((exception is InvalidOperationException or System.ComponentModel.Win32Exception)) {
            return CanaryLegRun.InfrastructureFailure(leg: leg, runDirectory: runDirectory, reason: exception.Message.ReplaceLineEndings(replacementText: " "));
        } finally {
            // Disposed exactly once here — an earlier early-return above must never also dispose, or the second
            // Dispose() throws on the already-released Process handle.
            if (authority is not null) {
                File.WriteAllText(path: Path.Combine(path1: runDirectory, path2: "authority-stdout.log"), contents: authority.Stdout, encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                File.WriteAllText(path: Path.Combine(path1: runDirectory, path2: "authority-stderr.log"), contents: authority.Stderr, encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                authority.Dispose();
            }
        }

        File.WriteAllText(path: stdoutPath, contents: process.Stdout, encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(path: stderrPath, contents: process.Stderr, encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var transcript = new CanaryTranscript(RunDirectory: runDirectory, Stderr: SplitLines(text: process.Stderr), Stdout: SplitLines(text: process.Stdout));
        var assertions = CanaryAssertions.Evaluate(authorityEndpoint: federationEndpoint, leg: leg, primaryTranscript: transcript);
        var invariants = EvaluateRunnerInvariants(executionWorld: executionWorld, leg: leg, manifest: manifest, process: process, transcript: transcript);

        return new CanaryLegRun(
            Assertions: assertions,
            AuthorityEndpoint: federationEndpoint,
            AuthorityTranscripts: ImmutableEmptyAuthorityTranscripts,
            ExitCode: process.ExitCode,
            InfrastructureError: null,
            Invariants: invariants,
            Leg: leg,
            RunDirectory: runDirectory,
            TimedOut: process.TimedOut,
            Transcript: transcript
        );
    }

    private static readonly IReadOnlyDictionary<string, CanaryTranscript> ImmutableEmptyAuthorityTranscripts = new Dictionary<string, CanaryTranscript>(comparer: StringComparer.Ordinal);

    /// <summary>One authority's throwaway federation-identity keypair — a fresh ECDSA P-256 key, its self-certifying
    /// domain fingerprint, and its SPKI bytes ready to pin into a peer's admission row.</summary>
    private readonly record struct FederationIdentity(string Domain, string PublicKeyBase64, byte[] Pkcs8);

    /// <summary>The subject the canary's connecting-out process signs its claims as. It authors no host.authority
    /// of its own (it never listens for federation in this fixture), so Puck.World's own boot-instance fallback
    /// names it — see Program.cs's own remarks on why that fallback exists.</summary>
    private const string CanaryClientAuthoritySubject = Puck.World.WorldDefinitionLoader.BootInstanceName;

    private static FederationIdentity GenerateFederationIdentity() {
        using var ecdsa = System.Security.Cryptography.ECDsa.Create(curve: System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var spki = ecdsa.ExportSubjectPublicKeyInfo();
        var fingerprint = System.Security.Cryptography.SHA256.HashData(source: spki);

        return new FederationIdentity(
            Domain: Convert.ToHexStringLower(bytes: fingerprint),
            PublicKeyBase64: Convert.ToBase64String(inArray: spki),
            Pkcs8: ecdsa.ExportPkcs8PrivateKey()
        );
    }
    private static JsonObject AdmissionRow(FederationIdentity peer, string peerSubject) => new() {
        ["domain"] = peer.Domain,
        ["subject"] = peerSubject,
        ["mode"] = "SignsDirectly",
        ["algorithm"] = "ecdsa-p256-sha256",
        ["publicKey"] = peer.PublicKeyBase64,
        ["grants"] = new JsonArray(),
    };
    private static (string ClientWorld, string AuthorityWorld) PrepareFederatedWorlds(CanaryLeg leg, string runDirectory, string endpoint, FederationIdentity clientIdentity, FederationIdentity authorityIdentity) {
        var sourceDirectory = Path.GetDirectoryName(path: leg.WorldPath)!;
        var federatedDirectory = Path.Combine(path1: runDirectory, path2: "federated-worlds");

        Directory.CreateDirectory(path: federatedDirectory);

        foreach (var source in Directory.GetFiles(path: sourceDirectory, searchOption: SearchOption.TopDirectoryOnly, searchPattern: "*.world.json")) {
            File.Copy(sourceFileName: source, destFileName: Path.Combine(path1: federatedDirectory, path2: Path.GetFileName(path: source)), overwrite: true);
        }

        var authoritySource = leg.AuthorityWorldPath!;
        var authorityTarget = Path.Combine(path1: federatedDirectory, path2: Path.GetFileName(path: authoritySource));

        if (!File.Exists(path: authorityTarget)) {
            File.Copy(destFileName: authorityTarget, overwrite: true, sourceFileName: authoritySource);
        }

        var root = (JsonNode.Parse(json: File.ReadAllText(path: authorityTarget))?.AsObject()
            ?? throw new InvalidOperationException(message: "authority world is not a JSON object"));

        // The authority world may be a delta over a `basis` template (the quilt documents are), so an edited member
        // is created when absent — an added member deep-merges over the template at load — and the population floor
        // reads the composed document, never the delta alone, so a template-authored capacity above the floor is
        // kept rather than clamped back down. The same composed read carries forward whatever admission rows the
        // basis already authors (a wildcard FederatedAuthority arrival row, typically) — this door's own key-bearing
        // row is ADDED to that set, never a replacement of it.
        var composedCapacity = 8;
        var composedNetworkPlayers = 4;
        var composedAdmission = new JsonArray();

        if (Puck.World.WorldDefinitionFileSource.TryComposeDocumentTree(path: authorityTarget, reason: out _, tree: out var composed)) {
            if (composed!["population"] is JsonObject composedPopulation) {
                composedCapacity = Math.Max(val1: composedCapacity, val2: (composedPopulation["capacity"]?.GetValue<int>() ?? composedCapacity));
                composedNetworkPlayers = Math.Max(val1: composedNetworkPlayers, val2: (composedPopulation["networkPlayers"]?.GetValue<int>() ?? composedNetworkPlayers));
            }
            if (composed["admission"] is JsonArray composedRows) {
                foreach (var row in composedRows) {
                    composedAdmission.Add(item: row?.DeepClone());
                }
            }
        }

        if (root["host"] is not JsonObject host) {
            host = new JsonObject();
            root["host"] = host;
        }

        host["listen"] = endpoint;
        host["authority"] = endpoint;

        if (root["population"] is not JsonObject population) {
            population = new JsonObject();
            root["population"] = population;
        }

        population["capacity"] = composedCapacity;
        population["networkPlayers"] = composedNetworkPlayers;
        // The federation door verifies a claim against THIS document's own admission rows — pin the client's key
        // (proving as CanaryClientAuthoritySubject, since it authors no host.authority of its own) alongside
        // whatever the basis already authors, so WorldAttestedAuthenticator has a key-bearing row to build a trust
        // list from AND the pre-existing FederatedAuthority row still decides what an admitted arrival is minted.
        composedAdmission.Add(value: AdmissionRow(peer: clientIdentity, peerSubject: CanaryClientAuthoritySubject));
        root["admission"] = composedAdmission;
        File.WriteAllText(path: authorityTarget, contents: root.ToJsonString(options: new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // The client side never needs editing: it authors no host.authority (so it signs as the boot-instance
        // fallback, see CanaryClientAuthoritySubject's own remarks) and it never listens for federation in this
        // fixture, so it never verifies an inbound claim and needs no admission rows of its own. A --connect leg
        // therefore still boots from the pristine, unmodified checked-in asset.
        return ((leg.Connect ? leg.WorldPath : Path.Combine(path1: federatedDirectory, path2: Path.GetFileName(path: leg.WorldPath))), authorityTarget);
    }
    // A federated mesh leg (leg.Authorities.Count != 0, CANARY-SHAPE.md's N-ary shape): every authority is a
    // listener bound to its own dynamic loopback port, none dials out, and neighbours resolve each other by reading
    // a sibling document's own host.authority — the same adjacency/references mechanism a two-authority leg already
    // relies on, generalized from one companion to N. All N processes launch concurrently and run to completion
    // before any assertion reads a transcript.
    private static CanaryLegRun RunFederatedMeshLeg(CanaryManifest manifest, CanaryLeg leg, string artifact, CanaryBudget budget) {
        var runDirectory = CreateRunDirectory(id: manifest.Id, leg: leg.Name);
        var federatedDirectory = Path.Combine(path1: runDirectory, path2: "federated-worlds");

        Directory.CreateDirectory(path: federatedDirectory);

        // Every authority's world.json lives beside the leg's own (the quilt documents are siblings), so one sweep
        // of the leg's own directory seeds every authority's copy before any of them is individually patched.
        var sourceDirectory = Path.GetDirectoryName(path: leg.WorldPath)!;

        foreach (var source in Directory.GetFiles(path: sourceDirectory, searchOption: SearchOption.TopDirectoryOnly, searchPattern: "*.world.json")) {
            File.Copy(sourceFileName: source, destFileName: Path.Combine(path1: federatedDirectory, path2: Path.GetFileName(path: source)), overwrite: true);
        }

        var identities = new Dictionary<string, FederationIdentity>(comparer: StringComparer.Ordinal);
        var endpoints = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
        var patchedWorldPaths = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
        var keyPaths = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        foreach (var role in leg.Authorities) {
            identities[role.Id] = GenerateFederationIdentity();
            endpoints[role.Id] = $"127.0.0.1:{GetFreeLoopbackPort()}";
        }

        foreach (var role in leg.Authorities) {
            var target = Path.Combine(path1: federatedDirectory, path2: Path.GetFileName(path: role.WorldPath));

            if (!File.Exists(path: target)) {
                File.Copy(sourceFileName: role.WorldPath, destFileName: target, overwrite: true);
            }

            PatchMeshAuthorityDocument(endpoints: endpoints, identities: identities, selfId: role.Id, targetPath: target);
            patchedWorldPaths[role.Id] = target;

            var keyPath = Path.Combine(path1: runDirectory, path2: $"{role.Id}-federation.key");

            File.WriteAllBytes(path: keyPath, bytes: identities[role.Id].Pkcs8);
            keyPaths[role.Id] = keyPath;
        }

        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var scriptTexts = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        foreach (var role in leg.Authorities) {
            var input = File.ReadAllText(path: role.ScriptPath)
                .Replace(oldValue: "{fixtures}", newValue: manifest.DirectoryPath.Replace(newChar: '\\', oldChar: '/'), comparisonType: StringComparison.Ordinal)
                .Replace(oldValue: "{run}", newValue: runDirectory.Replace(newChar: '\\', oldChar: '/'), comparisonType: StringComparison.Ordinal);

            if (!input.EndsWith(value: '\n')) {
                input += Environment.NewLine;
            }

            // Every authority carries the SAME runner-owned terminal observation a single-process leg does — see
            // EvaluateFederatedInvariants below, which checks each authority's own zero-rejected tally independently.
            input += $"wire.errors{Environment.NewLine}";
            scriptTexts[role.Id] = input;
        }

        var timeout = TimeSpan.FromSeconds(value: manifest.TimeoutSeconds);

        if (budget.Remaining < timeout) {
            return CanaryLegRun.BudgetExpired(budget: budget, leg: leg, runDirectory: runDirectory);
        }

        var tasks = new Dictionary<string, Task<CliProcessResult>>(comparer: StringComparer.Ordinal);

        foreach (var role in leg.Authorities) {
            var stateDirectory = Path.Combine(path1: runDirectory, path2: $"state-{role.Id}");
            var arguments = new List<string> {
                artifact,
                "--world", patchedWorldPaths[role.Id],
                "--federation-key-file", keyPaths[role.Id],
                "--exit-after-seconds", manifest.Seconds.ToString(provider: CultureInfo.InvariantCulture),
                "--state-dir", stateDirectory,
                "--headless", ((manifest.BootShape == CanaryBootShape.Headless) ? "true" : "false"),
            };
            var script = scriptTexts[role.Id];

            tasks[role.Id] = Task.Run(function: () => CliProcess.RunCaptured(arguments: arguments, fileName: "dotnet", input: script, timeout: timeout));
        }

        try {
            Task.WaitAll(tasks: [.. tasks.Values]);
        } catch (AggregateException exception) {
            return CanaryLegRun.InfrastructureFailure(leg: leg, reason: (exception.InnerException?.Message.ReplaceLineEndings(replacementText: " ") ?? exception.Message), runDirectory: runDirectory);
        }

        var transcripts = new Dictionary<string, CanaryTranscript>(comparer: StringComparer.Ordinal);

        foreach (var (authorityId, task) in tasks) {
            var result = task.Result;

            File.WriteAllText(path: Path.Combine(path1: runDirectory, path2: $"{authorityId}-stdout.log"), contents: result.Stdout, encoding: utf8NoBom);
            File.WriteAllText(path: Path.Combine(path1: runDirectory, path2: $"{authorityId}-stderr.log"), contents: result.Stderr, encoding: utf8NoBom);
            transcripts[authorityId] = new CanaryTranscript(RunDirectory: runDirectory, Stderr: SplitLines(text: result.Stderr), Stdout: SplitLines(text: result.Stdout));
        }

        var primaryId = leg.Authorities.First(predicate: role => (PathsEqual(left: role.WorldPath, right: leg.WorldPath) && PathsEqual(left: role.ScriptPath, right: leg.ScriptPath))).Id;
        var primaryTranscript = transcripts[primaryId];
        var assertions = CanaryAssertions.Evaluate(authorityTranscripts: transcripts, leg: leg, primaryTranscript: primaryTranscript);
        var invariants = EvaluateFederatedInvariants(manifestTimeoutSeconds: manifest.TimeoutSeconds, patchedWorldPaths: patchedWorldPaths, primaryId: primaryId, leg: leg, tasks: tasks, transcripts: transcripts);
        var anyTimedOut = tasks.Values.Any(predicate: static task => task.Result.TimedOut);
        var anyNonZeroExit = tasks.Values.Any(predicate: static task => (task.Result.ExitCode != 0));

        return new CanaryLegRun(
            Assertions: assertions,
            AuthorityEndpoint: string.Empty,
            AuthorityTranscripts: transcripts,
            ExitCode: (anyNonZeroExit ? tasks.Values.First(predicate: static task => (task.Result.ExitCode != 0)).Result.ExitCode : 0),
            InfrastructureError: null,
            Invariants: invariants,
            Leg: leg,
            RunDirectory: runDirectory,
            TimedOut: anyTimedOut,
            Transcript: primaryTranscript
        );
    }
    private static IReadOnlyList<CanaryAssertionResult> EvaluateFederatedInvariants(
        CanaryLeg leg,
        int manifestTimeoutSeconds,
        string primaryId,
        IReadOnlyDictionary<string, string> patchedWorldPaths,
        IReadOnlyDictionary<string, Task<CliProcessResult>> tasks,
        IReadOnlyDictionary<string, CanaryTranscript> transcripts
    ) {
        var results = new List<CanaryAssertionResult>(capacity: (leg.Authorities.Count * 3));

        // The manifest's commands[] claims are authored against the leg's own (primary) script, so full per-verb,
        // per-occurrence, per-stream accounting only applies to that one authority — a non-primary authority's
        // script is unclaimed and gets only the health checks below.
        results.AddRange(collection: EvaluateCommandAccounting(commands: leg.Commands, outputLines: tasks[primaryId].Result.OutputLines));

        foreach (var role in leg.Authorities) {
            var result = tasks[role.Id].Result;
            var transcript = transcripts[role.Id];

            results.Add(item: new CanaryAssertionResult(Detail: $"{role.Id} process exited 0 (actual {result.ExitCode})", Passed: (!result.TimedOut && (result.ExitCode == 0))));
            results.Add(item: new CanaryAssertionResult(Detail: $"{role.Id} completed before {manifestTimeoutSeconds}s timeout", Passed: !result.TimedOut));

            var origin = $"[world] definition: {patchedWorldPaths[role.Id]} (--world)";

            results.Add(item: new CanaryAssertionResult(Detail: $"{role.Id} stderr names the exact absolute --world origin", Passed: transcript.Stderr.Any(predicate: line => string.Equals(a: line, b: origin, comparisonType: StringComparison.Ordinal))));

            var terminal = transcript.Stdout.Where(predicate: static line => line.StartsWith(comparisonType: StringComparison.Ordinal, value: "[wire.errors:")).ToArray();

            results.Add(item: new CanaryAssertionResult(
                Detail: $"{role.Id} runner-owned terminal observation is exact '[wire.errors: 0 rejected]'",
                Passed: ((terminal.Length != 0) && string.Equals(a: terminal[^1], b: "[wire.errors: 0 rejected]", comparisonType: StringComparison.Ordinal))
            ));
        }

        return results;
    }
    private static int GetFreeLoopbackPort() {
        var listener = new System.Net.Sockets.TcpListener(localaddr: System.Net.IPAddress.Loopback, port: 0);

        try {
            listener.Start();

            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        } finally {
            listener.Stop();
        }
    }
    // Patches one authority's copied world document in place: its own listen/authority endpoint, and one
    // SignsDirectly admission row per OTHER authority in the mesh so WorldAttestedAuthenticator can verify every
    // peer's signed claim against a key pinned by this document — the N-ary generalization of PrepareFederatedWorlds'
    // single companion row above. Reads the pristine composed document (basis + this authority's own delta) BEFORE
    // any of these edits land, so an authored capacity/admission floor the basis already carries is kept, not lost.
    private static void PatchMeshAuthorityDocument(string targetPath, string selfId, IReadOnlyDictionary<string, string> endpoints, IReadOnlyDictionary<string, FederationIdentity> identities) {
        var root = (JsonNode.Parse(json: File.ReadAllText(path: targetPath))?.AsObject()
            ?? throw new InvalidOperationException(message: $"authority world '{targetPath}' is not a JSON object"));
        var composedCapacity = 8;
        var composedNetworkPlayers = 4;
        var composedAdmission = new JsonArray();

        if (Puck.World.WorldDefinitionFileSource.TryComposeDocumentTree(path: targetPath, reason: out _, tree: out var composed)) {
            if (composed!["population"] is JsonObject composedPopulation) {
                composedCapacity = Math.Max(val1: composedCapacity, val2: (composedPopulation["capacity"]?.GetValue<int>() ?? composedCapacity));
                composedNetworkPlayers = Math.Max(val1: composedNetworkPlayers, val2: (composedPopulation["networkPlayers"]?.GetValue<int>() ?? composedNetworkPlayers));
            }
            if (composed["admission"] is JsonArray composedRows) {
                foreach (var row in composedRows) {
                    composedAdmission.Add(item: row?.DeepClone());
                }
            }
        }

        if (root["host"] is not JsonObject host) {
            host = new JsonObject();
            root["host"] = host;
        }

        host["listen"] = endpoints[selfId];
        host["authority"] = endpoints[selfId];

        if (root["population"] is not JsonObject population) {
            population = new JsonObject();
            root["population"] = population;
        }

        population["capacity"] = composedCapacity;
        population["networkPlayers"] = composedNetworkPlayers;

        foreach (var (peerId, peerEndpoint) in endpoints) {
            if (peerId == selfId) {
                continue;
            }

            composedAdmission.Add(value: AdmissionRow(peer: identities[peerId], peerSubject: peerEndpoint));
        }

        root["admission"] = composedAdmission;
        File.WriteAllText(path: targetPath, contents: root.ToJsonString(options: new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private sealed class AuthorityCompanion : IDisposable {
        private readonly Process m_process;

        private readonly StringBuilder m_stdout = new();
        private readonly StringBuilder m_stderr = new();
        private readonly Lock m_gate = new();

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

        public string Stderr { get { lock (m_gate) { return m_stderr.ToString(); } } }
        public string Stdout { get { lock (m_gate) { return m_stdout.ToString(); } } }

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
            return new AuthorityCompanion(process: (Process.Start(startInfo: startInfo) ?? throw new InvalidOperationException(message: "failed to start companion authority")));
        }
        public bool WaitUntilListening(TimeSpan timeout) {
            var clock = Stopwatch.StartNew();

            while (clock.Elapsed < timeout) {
                lock (m_gate) {
                    if (m_stderr.ToString().Contains(comparisonType: StringComparison.Ordinal, value: "[world.listen: bound ")) {
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

        results.AddRange(collection: EvaluateCommandAccounting(commands: leg.Commands, outputLines: process.OutputLines));

        return results;
    }
    // Per-verb command-claim accounting shared by a single-process leg and a federated mesh leg's primary
    // authority: every authored occurrence's response is found by its exact "[verb:" prefix (either stream —
    // OutputLines carries both, sequence-ordered) and checked against its declared stream (accepted implies
    // stdout unless StreamOverride says otherwise; refused always implies stderr). wire.errors additionally
    // carries the runner-owned terminal call, one beyond whatever occurrences the script itself authored.
    private static IReadOnlyList<CanaryAssertionResult> EvaluateCommandAccounting(IReadOnlyList<CanaryCommandClaim> commands, IReadOnlyList<CliProcessOutputLine> outputLines) {
        var results = new List<CanaryAssertionResult>();
        var expectedRefusals = commands.Count(predicate: static claim => (claim.Outcome == CanaryCommandOutcome.Refused));
        var byVerb = commands.GroupBy(keySelector: static claim => claim.Verb, comparer: StringComparer.Ordinal);

        foreach (var group in byVerb) {
            var responseEvents = outputLines
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
                    var expectedStream = (((claims[index].StreamOverride ?? ((claims[index].Outcome == CanaryCommandOutcome.Accepted) ? CanaryStream.Stdout : CanaryStream.Stderr)) == CanaryStream.Stdout)
                        ? CliProcessOutputStream.Stdout
                        : CliProcessOutputStream.Stderr);

                    results.Add(item: new CanaryAssertionResult(
                        Detail: $"{group.Key} occurrence {claims[index].Occurrence} was {claims[index].Outcome.ToString().ToLowerInvariant()}",
                        Passed: (responseEvents[index].Stream == expectedStream)
                    ));
                }
            }
        }

        var terminalResponses = outputLines
            .Where(predicate: static line => line.Line.StartsWith(comparisonType: StringComparison.Ordinal, value: "[wire.errors:"))
            .ToArray();
        var expectedTerminal = $"[wire.errors: {expectedRefusals} rejected]";
        var terminalPassed = ((terminalResponses.Length != 0) && (terminalResponses[^1].Stream == CliProcessOutputStream.Stdout) && string.Equals(a: terminalResponses[^1].Line, b: expectedTerminal, comparisonType: StringComparison.Ordinal));

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
            var valid = ((capability is "automatic" or "headless" or "windowed" or "gpu" or "audio-output") || (capability.StartsWith(comparisonType: StringComparison.Ordinal, value: "input:") && (capability.Length > 6)));

            if (!valid) {
                error = $"unknown capability filter '{capability}'; use automatic, headless, windowed, gpu, audio-output, or input:<hardware-name>.";

                return false;
            }

            selection = new CanarySelection(Capability: capability, Ids: [], Kind: CanarySelectionKind.Capability);

            return true;
        }
        if (args.Any(predicate: static argument => argument.StartsWith(comparisonType: StringComparison.Ordinal, value: "-"))) {
            error = "ids, --all, --list, and --capability <class> are mutually exclusive selection forms.";

            return false;
        }

        var duplicate = args.GroupBy(keySelector: static id => id, comparer: StringComparer.Ordinal).FirstOrDefault(predicate: static group => (group.Count() > 1));

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
    private static bool PathsEqual(string left, string right) =>
        (OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Equals(x: left, y: right);
    private static IReadOnlyList<string> SplitLines(string text) =>
        text.ReplaceLineEndings(replacementText: "\n").Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '\n');
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
        public bool Passed => ((InfrastructureError is null) && !TimedOut && (ExitCode == 0) && Invariants.All(predicate: static result => result.Passed) && Assertions.Passed);

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
            Transcript: new CanaryTranscript(RunDirectory: runDirectory, Stderr: [], Stdout: [])
        );
    }
}
