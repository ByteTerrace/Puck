using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Puck.Abstractions;
using Puck.World;
using Puck.World.Transpiler.Composition;

namespace Puck.Cli.Canary;

public static partial class CanaryCommand {
    private static readonly IReadOnlyDictionary<string, CanaryTranscript> ImmutableEmptyAuthorityTranscripts = new Dictionary<string, CanaryTranscript>(comparer: StringComparer.Ordinal);

    /// <summary>One authority's throwaway federation-identity keypair — a fresh ECDSA P-256 key, its self-certifying
    /// domain fingerprint, and its SPKI bytes ready to pin into a peer's admission row.</summary>
    public readonly record struct FederationIdentity(string Domain, string PublicKeyBase64, byte[] Pkcs8);

    /// <summary>The subject the canary's connecting-out process signs its claims as. It authors no host.authority
    /// of its own (it never listens for federation in this fixture), so Puck.World's own boot-instance fallback
    /// names it — see Program.cs's own remarks on why that fallback exists.</summary>
    private const string CanaryClientAuthoritySubject = Puck.World.WorldDefinitionLoader.BootInstanceName;
    // The census section a federated fixture raises so an arrival from a peer has a slot to land in. KEEP IN SYNC
    // with WorldBodiesDefaults' own JSON name: bodies.networkPlayers defaults to 0, so a fixture that raises the
    // wrong member reads as a world that admits nobody, and every crossing is refused on arrival rather than
    // failing to compose.
    private const string WorldBodiesSectionName = "bodies";

    public static FederationIdentity GenerateFederationIdentity() {
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
    // Stages every world a federated leg boots into the run's own tree, mirrored whole from the nearest directory
    // holding all of them, and answers the staged path for each source path. A leg's worlds are patched copies —
    // each carries its own endpoint and admission rows — so every reference they resolve must land on a copy rather
    // than on the shipped asset, which means the staged tree has to have the shipped tree's shape, not just its
    // booted files: a shard names its basis one directory up, that basis names imports one directory down again,
    // and an adjacency `references` row names a sibling. Anything short of the mirror leaves one of those three
    // resolving to nothing, and the process refuses its own definition before it ever listens.
    private static Dictionary<string, string> StageFederatedWorlds(IReadOnlyCollection<string> worldPaths, string federatedDirectory) {
        var full = new List<(string Given, string Resolved)>(capacity: worldPaths.Count);

        foreach (var path in worldPaths) {
            full.Add(item: (path, Path.GetFullPath(path: path)));
        }

        var root = Path.GetDirectoryName(path: full[0].Resolved)!;

        for (var index = 1; (index < full.Count); index++) {
            var candidate = Path.GetDirectoryName(path: full[index].Resolved)!;

            while (Path.GetRelativePath(
                path: candidate,
                relativeTo: root
            ).StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: ".."
            )) {
                var parent = Path.GetDirectoryName(path: root);

                if (string.IsNullOrEmpty(value: parent)) {
                    throw new InvalidOperationException(message: $"federated worlds '{full[0].Resolved}' and '{full[index].Resolved}' share no directory to stage from.");
                }

                root = parent;
            }
        }

        // A document's machine content lives beside `worlds` in the same `Assets` tree (a cabinet reaches
        // `../../cartridges/…`), so when the worlds sit inside a content root the whole root is staged, never the
        // worlds' own directory alone — the relative paths the documents carry then resolve in the copy too.
        for (var ancestor = root; !string.IsNullOrEmpty(value: ancestor); ancestor = Path.GetDirectoryName(path: ancestor)) {
            if (string.Equals(
                a: Path.GetFileName(path: ancestor),
                b: "Assets",
                comparisonType: StringComparison.Ordinal
            )) {
                root = ancestor;

                break;
            }
        }

        CopyDirectory(
            source: root,
            target: federatedDirectory
        );

        var staged = new Dictionary<string, string>(comparer: StringComparer.OrdinalIgnoreCase);

        foreach (var (given, resolved) in full) {
            staged[given] = Path.Combine(
                path1: federatedDirectory,
                path2: Path.GetRelativePath(
                    path: resolved,
                    relativeTo: root
                )
            );
            RootEscapingDocuments(
                mirror: federatedDirectory,
                source: resolved,
                staged: staged[given]
            );
        }

        return staged;
    }

    // A staged document's basis and imports resolve against its own directory. One that names a document outside the
    // mirrored tree, as a canary's own delta over a shipped world does, would name nothing from the copy, so it is rooted
    // where the source's resolves; one inside the tree keeps naming the staged copy, which is what a patched sibling is.
    public static void RootEscapingDocuments(string staged, string source, string mirror) {
        if (JsonNode.Parse(json: File.ReadAllText(path: staged)) is not JsonObject world) {
            return;
        }

        var stagedDirectory = Path.GetDirectoryName(path: staged)!;
        var sourceDirectory = Path.GetDirectoryName(path: source)!;
        var mirrorRoot = (Path.GetFullPath(path: mirror).TrimEnd(trimChar: Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
        var changed = false;

        string? Rooted(string name) {
            if (
                Path.IsPathRooted(path: name) ||
                Path.GetFullPath(path: Path.Combine(
                    path1: stagedDirectory,
                    path2: name
                )).StartsWith(
                    comparisonType: StringComparison.OrdinalIgnoreCase,
                    value: mirrorRoot
                )
            ) {
                return null;
            }

            changed = true;

            return Path.GetFullPath(path: Path.Combine(
                path1: sourceDirectory,
                path2: name
            )).Replace(
                newChar: '/',
                oldChar: '\\'
            );
        }

        if (
            (world[propertyName: WorldDocumentBasis.BasisMemberName]?.GetValue<string>() is { } basis) &&
            (Rooted(name: basis) is { } rootedBasis)
        ) {
            world[propertyName: WorldDocumentBasis.BasisMemberName] = rootedBasis;
        }

        foreach (var entry in ((world[propertyName: WorldDocumentBasis.ImportsMemberName] as JsonArray) ?? [])) {
            if (
                (entry is JsonObject import) &&
                (import[propertyName: WorldImport.DocumentMemberName]?.GetValue<string>() is { } document) &&
                (Rooted(name: document) is { } rootedDocument)
            ) {
                import[propertyName: WorldImport.DocumentMemberName] = rootedDocument;
            }
        }

        if (changed) {
            File.WriteAllText(
                contents: world.ToJsonString(options: new System.Text.Json.JsonSerializerOptions { NewLine = "\n", WriteIndented = true }),
                encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                path: staged
            );
        }
    }

    private static void CopyDirectory(string source, string target) {
        Directory.CreateDirectory(path: target);

        foreach (var file in Directory.GetFiles(path: source)) {
            File.Copy(
                destFileName: Path.Combine(
                    path1: target,
                    path2: Path.GetFileName(path: file)
                ),
                overwrite: true,
                sourceFileName: file
            );
        }

        foreach (var directory in Directory.GetDirectories(path: source)) {
            CopyDirectory(
                source: directory,
                target: Path.Combine(
                    path1: target,
                    path2: Path.GetFileName(path: directory)
                )
            );
        }
    }

    // A composition source declares several worlds that reach each other across their borders by document name, and
    // the authority boots one document the runner patches. The source is compiled in place and every world it
    // declares is staged together, through the staging a composition boot does for itself, and the world the leg
    // enters — its `entry`, else the source's declared entry — is the document the authority boots.
    internal static string StageFederatedComposition(string source, string? entry, string directory) {
        _ = WorldCompileCache.Shared.TryCompile(
            compiled: out var compiled,
            failure: out var failure,
            path: source
        );

        if (failure is not null) {
            throw new InvalidOperationException(message: $"authority composition '{source}' does not compile:{Environment.NewLine}{failure.Diagnostics.FormatReport(
                filePath: source,
                sourceText: File.ReadAllText(path: source)
            )}");
        }
        if (compiled!.Worlds.Count == 0) {
            throw new InvalidOperationException(message: $"authority source '{source}' declares no worlds; a federated leg stages a composition's worlds or boots a document.");
        }
        if (!WorldStaging.TryStageComposition(
            catalog: CliWorldVocabulary.EnsureInstalled(),
            directory: directory,
            entry: entry,
            entryName: out _,
            entryPath: out var entryPath,
            path: source,
            reason: out var reason,
            worlds: compiled.Worlds
        )) {
            throw new InvalidOperationException(message: $"authority composition refused: {reason}");
        }

        return entryPath;
    }

    public static (string ClientWorld, string AuthorityWorld) PrepareFederatedWorlds(CanaryLeg leg, string runDirectory, string endpoint, FederationIdentity clientIdentity, FederationIdentity authorityIdentity) {
        var machineCatalog = CliWorldVocabulary.EnsureInstalled();
        var catalogFingerprint = CliWorldVocabulary.Fingerprint(catalog: machineCatalog);
        var federatedDirectory = Path.Combine(
            path1: runDirectory,
            path2: "federated-worlds"
        );
        // A composition source is compiled where it stands, so its imports resolve, and its worlds are staged
        // together into the run's own directory; only documents are mirrored.
        var documents = ((string[])[leg.WorldPath, leg.AuthorityWorldPath!]).Where(predicate: static path => !WorldDocumentName.IsSourceFile(path: path)).ToArray();
        var staged = ((documents.Length == 0)
            ? new Dictionary<string, string>(comparer: StringComparer.OrdinalIgnoreCase)
            : StageFederatedWorlds(
                federatedDirectory: federatedDirectory,
                worldPaths: documents
            ));
        var authorityTarget = (WorldDocumentName.IsSourceFile(path: leg.AuthorityWorldPath!)
            ? StageFederatedComposition(
                directory: Path.Combine(
                    path1: runDirectory,
                    path2: "federated-composition"
                ),
                entry: leg.Entry,
                source: leg.AuthorityWorldPath!
            )
            : staged[leg.AuthorityWorldPath!]);

        if (!leg.Connect && !staged.ContainsKey(key: leg.WorldPath)) {
            throw new InvalidOperationException(message: $"federated leg '{leg.Name}' boots the composition source '{leg.WorldPath}' beside its authority without connect; a staged client reaches the patched authority through its own staged references, which a source does not carry, so a composition client connects.");
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

        if (Puck.World.WorldDefinitionFileSource.TryComposeDocumentTree(
            catalog: machineCatalog,
            catalogFingerprint: catalogFingerprint,
            path: authorityTarget,
            reason: out _,
            tree: out var composed
        )) {
            if (composed![WorldBodiesSectionName] is JsonObject composedPopulation) {
                composedCapacity = Math.Max(
                    val1: composedCapacity,
                    val2: (composedPopulation["capacity"]?.GetValue<int>() ?? composedCapacity)
                );
                composedNetworkPlayers = Math.Max(
                    val1: composedNetworkPlayers,
                    val2: (composedPopulation["networkPlayers"]?.GetValue<int>() ?? composedNetworkPlayers)
                );
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

        if (root[WorldBodiesSectionName] is not JsonObject population) {
            population = new JsonObject();
            root[WorldBodiesSectionName] = population;
        }

        population["capacity"] = composedCapacity;
        population["networkPlayers"] = composedNetworkPlayers;
        // The federation door verifies a claim against THIS document's own admission rows — pin the client's key
        // (proving as CanaryClientAuthoritySubject, since it authors no host.authority of its own) alongside
        // whatever the basis already authors, so WorldAttestedAuthenticator has a key-bearing row to build a trust
        // list from AND the pre-existing FederatedAuthority row still decides what an admitted arrival is minted.
        composedAdmission.Add(value: AdmissionRow(
            peer: clientIdentity,
            peerSubject: CanaryClientAuthoritySubject
        ));
        root["admission"] = composedAdmission;
        File.WriteAllText(
            path: authorityTarget,
            contents: root.ToJsonString(options: new System.Text.Json.JsonSerializerOptions { NewLine = "\n", WriteIndented = true }),
            encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        );

        // The client side never needs editing: it authors no host.authority (so it signs as the boot-instance
        // fallback, see CanaryClientAuthoritySubject's own remarks) and it never listens for federation in this
        // fixture, so it never verifies an inbound claim and needs no admission rows of its own. A --connect leg
        // therefore still boots from the pristine, unmodified checked-in asset.
        return ((leg.Connect
            ? leg.WorldPath
            : staged[leg.WorldPath]), authorityTarget);
    }

    // A federated mesh leg (leg.Authorities.Count != 0, CANARY-SHAPE.md's N-ary shape): every authority is a
    // listener bound to its own dynamic loopback port, none dials out, and neighbours resolve each other by reading
    // a sibling document's own host.authority — the same adjacency/references mechanism a two-authority leg already
    // relies on, generalized from one companion to N. All N processes launch concurrently and run to completion
    // before any assertion reads a transcript.
    private static CanaryLegRun RunFederatedMeshLeg(CanaryManifest manifest, CanaryLeg leg, string artifact, CanaryBudget budget) {
        var runDirectory = CreateRunDirectory(
            id: manifest.Id,
            leg: leg.Name
        );
        var federatedDirectory = Path.Combine(
            path1: runDirectory,
            path2: "federated-worlds"
        );
        var stagedWorlds = StageFederatedWorlds(
            federatedDirectory: federatedDirectory,
            worldPaths: [leg.WorldPath, .. leg.Authorities.Select(selector: static role => role.WorldPath)]
        );
        var identities = new Dictionary<string, FederationIdentity>(comparer: StringComparer.Ordinal);
        var endpoints = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
        var patchedWorldPaths = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
        var keyPaths = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        foreach (var role in leg.Authorities) {
            identities[role.Id] = GenerateFederationIdentity();
            endpoints[role.Id] = $"127.0.0.1:{GetFreeLoopbackPort()}";
        }

        foreach (var role in leg.Authorities) {
            var target = stagedWorlds[role.WorldPath];

            PatchMeshAuthorityDocument(
                endpoints: endpoints,
                identities: identities,
                selfId: role.Id,
                targetPath: target
            );
            patchedWorldPaths[role.Id] = target;

            var keyPath = Path.Combine(
                path1: runDirectory,
                path2: $"{role.Id}-federation.key"
            );

            File.WriteAllBytes(
                path: keyPath,
                bytes: identities[role.Id].Pkcs8
            );
            keyPaths[role.Id] = keyPath;
        }

        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var scriptTexts = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        foreach (var role in leg.Authorities) {
            var input = File.ReadAllText(path: role.ScriptPath)
                .Replace(
                oldValue: "{fixtures}",
                newValue: manifest.DirectoryPath.Replace(
                    newChar: '\\',
                    oldChar: '/'
                ),
                comparisonType: StringComparison.Ordinal
            )
                .Replace(
                oldValue: "{run}",
                newValue: runDirectory.Replace(
                    newChar: '\\',
                    oldChar: '/'
                ),
                comparisonType: StringComparison.Ordinal
            );

            if (!input.EndsWith(value: '\n')) {
                input += Environment.NewLine;
            }

            // Every authority carries the SAME runner-owned terminal observation a single-process leg does — see
            // EvaluateFederatedInvariants below, which checks each authority's own zero-rejected tally independently.
            // Its quit is held back until every authority's script has ended; see the release below.
            input += $"wire.errors{Environment.NewLine}";
            scriptTexts[role.Id] = input;
        }

        var timeout = TimeSpan.FromSeconds(value: manifest.TimeoutSeconds);

        if (budget.Remaining < timeout) {
            return CanaryLegRun.BudgetExpired(
                budget: budget,
                leg: leg,
                runDirectory: runDirectory
            );
        }

        var tasks = new Dictionary<string, Task<CliProcessResult>>(comparer: StringComparer.Ordinal);
        // Every authority serves the others while any script is still running, so none is asked to quit until every
        // script has reached its runner-owned terminal wire.errors or its process has ended. Then all quit together.
        var scriptsRunning = leg.Authorities.Count;
        var everyScriptEnded = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        foreach (var role in leg.Authorities) {
            var stateDirectory = Path.Combine(
                path1: runDirectory,
                path2: $"state-{role.Id}"
            );
            var arguments = new List<string> {
                artifact,
                "--world", patchedWorldPaths[role.Id],
                "--federation-key-file", keyPaths[role.Id],
                "--state-dir", stateDirectory,
                "--exit-after-seconds", manifest.TimeoutSeconds.ToString(provider: CultureInfo.InvariantCulture),
                "--headless", ((manifest.BootShape == CanaryBootShape.Headless)
                ? "true"
                : "false"),
            };
            var script = scriptTexts[role.Id];
            var terminalOrdinal = CountVerbLines(
                script: script,
                verb: "wire.errors"
            );
            var ended = 0;
            var observedResponses = 0;

            void End() {
                if (
                    (Interlocked.Exchange(
                    location1: ref ended,
                    value: 1
                ) == 0) &&
                    (Interlocked.Decrement(location: ref scriptsRunning) == 0)
                ) {
                    everyScriptEnded.TrySetResult();
                }
            }

            tasks[role.Id] = Task.Run(function: () => {
                try {
                    budget.Tally.WorldStarted();

                    return CliProcess.RunCaptured(
                        arguments: arguments,
                        cancellationToken: budget.Cancellation,
                        continuationGate: everyScriptEnded.Task,
                        continuationInput: RunnerQuit,
                        // CliProcess calls this serially for one process, so the count needs no lock.
                        continueWhen: line => {
                            if (
                                line.Line.StartsWith(
                                comparisonType: StringComparison.Ordinal,
                                value: "[wire.errors:"
                            ) &&
                                (++observedResponses == terminalOrdinal)
                            ) {
                                End();

                                return true;
                            }

                            return false;
                        },
                        fileName: "dotnet",
                        input: script,
                        timeout: timeout
                    );
                } finally {
                    End();
                }
            });
        }

        try {
            Task.WaitAll(tasks: [.. tasks.Values]);
        } catch (AggregateException exception) {
            return CanaryLegRun.InfrastructureFailure(
                leg: leg,
                reason: (exception.InnerException?.Message.ReplaceLineEndings(replacementText: " ") ?? exception.Message),
                runDirectory: runDirectory
            );
        }

        var transcripts = new Dictionary<string, CanaryTranscript>(comparer: StringComparer.Ordinal);

        foreach (var (authorityId, task) in tasks) {
            var result = task.Result;

            File.WriteAllText(
                path: Path.Combine(
                    path1: runDirectory,
                    path2: $"{authorityId}-stdout.log"
                ),
                contents: result.Stdout,
                encoding: utf8NoBom
            );
            File.WriteAllText(
                path: Path.Combine(
                    path1: runDirectory,
                    path2: $"{authorityId}-stderr.log"
                ),
                contents: result.Stderr,
                encoding: utf8NoBom
            );
            transcripts[authorityId] = new CanaryTranscript(
                RunDirectory: runDirectory,
                Stderr: SplitLines(text: result.Stderr),
                Stdout: SplitLines(text: result.Stdout)
            );
        }

        var primaryId = leg.Authorities.First(predicate: role => (PuckPaths.Comparer.Equals(
            x: role.WorldPath,
            y: leg.WorldPath
        ) && PuckPaths.Comparer.Equals(
            x: role.ScriptPath,
            y: leg.ScriptPath
        ))).Id;
        var primaryTranscript = transcripts[primaryId];
        var assertions = CanaryAssertions.Evaluate(
            authorityTranscripts: transcripts,
            leg: leg,
            primaryTranscript: primaryTranscript
        );
        var invariants = EvaluateFederatedInvariants(
            manifestTimeoutSeconds: manifest.TimeoutSeconds,
            patchedWorldPaths: patchedWorldPaths,
            primaryId: primaryId,
            leg: leg,
            tasks: tasks,
            transcripts: transcripts
        );
        var anyTimedOut = tasks.Values.Any(predicate: static task => task.Result.TimedOut);
        var anyNonZeroExit = tasks.Values.Any(predicate: static task => (task.Result.ExitCode != 0));

        return new CanaryLegRun(
            Assertions: assertions,
            AuthorityEndpoint: string.Empty,
            AuthorityTranscripts: transcripts,
            ExitCode: (anyNonZeroExit
            ? tasks.Values.First(predicate: static task => (task.Result.ExitCode != 0)).Result.ExitCode
            : 0),
            InfrastructureError: transcripts.Values.Select(selector: static transcript => ListenerRefusal(stderr: transcript.Stderr)).FirstOrDefault(predicate: static refused => (refused is not null)),
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
        results.AddRange(collection: EvaluateCommandAccounting(
            commands: leg.Commands,
            outputLines: tasks[primaryId].Result.OutputLines
        ));

        foreach (var role in leg.Authorities) {
            var result = tasks[role.Id].Result;
            var transcript = transcripts[role.Id];

            results.Add(item: new CanaryAssertionResult(
                Detail: $"{role.Id} process exited 0 (actual {result.ExitCode})",
                Passed: (!result.TimedOut && (result.ExitCode == 0))
            ));
            results.Add(item: new CanaryAssertionResult(
                Detail: $"{role.Id} completed before {manifestTimeoutSeconds}s timeout",
                Passed: !result.TimedOut
            ));

            var origin = $"[world] definition: {patchedWorldPaths[role.Id]} (--world)";

            results.Add(item: new CanaryAssertionResult(
                Detail: $"{role.Id} stderr names the exact absolute --world origin",
                Passed: transcript.Stderr.Any(predicate: line => string.Equals(
                    a: line,
                    b: origin,
                    comparisonType: StringComparison.Ordinal
                ))
            ));

            var terminal = transcript.Stdout.Where(predicate: static line => line.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "[wire.errors:"
            )).ToArray();

            results.Add(item: new CanaryAssertionResult(
                Detail: $"{role.Id} runner-owned terminal observation is exact '[wire.errors: 0 rejected]'",
                Passed: ((terminal.Length != 0) && string.Equals(
                    a: terminal[^1],
                    b: "[wire.errors: 0 rejected]",
                    comparisonType: StringComparison.Ordinal
                ))
            ));
        }

        return results;
    }
    // How many lines of a script open with the verb: the response ordinal its last occurrence answers at. Blank and
    // comment lines never match, exactly as the console skips them.
    private static int CountVerbLines(string script, string verb) =>
        SplitLines(text: script).Count(predicate: line => {
            var content = line.AsSpan().Trim();
            var end = content.IndexOfAny(
                value0: ' ',
                value1: '\t'
            );

            return ((end < 0)
                ? content
                : content[..end]
            ).SequenceEqual(other: verb);
        });

    // A port is free only until its leg binds it, and legs run concurrently, so no port is handed out twice in one run.
    private static readonly HashSet<int> IssuedLoopbackPorts = [];

    // The World listens over QUIC, which is UDP, so the probe binds a UDP socket: a free TCP port says nothing about
    // whether the same UDP port is taken. The port is picked before the World boots because an authority's document
    // names its own endpoint (host.authority is its signing subject and the address its peers dial), which a port
    // bound after boot could not be written into. A port lost between this probe and the World's bind ends the leg as
    // an infrastructure failure (see ListenerRefusal).
    public static int GetFreeLoopbackPort() => IssueLoopbackPort(probe: static () => {
        using var probe = new System.Net.Sockets.Socket(
            addressFamily: System.Net.Sockets.AddressFamily.InterNetwork,
            protocolType: System.Net.Sockets.ProtocolType.Udp,
            socketType: System.Net.Sockets.SocketType.Dgram
        );

        probe.Bind(localEP: new System.Net.IPEndPoint(
            address: System.Net.IPAddress.Loopback,
            port: 0
        ));

        return ((System.Net.IPEndPoint)probe.LocalEndPoint!).Port;
    });
    /// <summary>Returns the first port <paramref name="probe"/> finds that this process has not handed out before, so
    /// no two legs, however many run at once, are given one endpoint: the probe's socket is closed by the time the port
    /// returns, and the operating system may offer the same free port to the next probe.</summary>
    /// <param name="probe">Finds a port free on the machine now.</param>
    /// <returns>The port, recorded as handed out.</returns>
    public static int IssueLoopbackPort(Func<int> probe) {
        while (true) {
            var port = probe();

            lock (IssuedLoopbackPorts) {
                if (IssuedLoopbackPorts.Add(item: port)) {
                    return port;
                }
            }
        }
    }

    // Patches one authority's copied world document in place: its own listen/authority endpoint, and one
    // SignsDirectly admission row per OTHER authority in the mesh so WorldAttestedAuthenticator can verify every
    // peer's signed claim against a key pinned by this document — the N-ary generalization of PrepareFederatedWorlds'
    // single companion row above. Reads the pristine composed document (basis + this authority's own delta) BEFORE
    // any of these edits land, so an authored capacity/admission floor the basis already carries is kept, not lost.
    private static void PatchMeshAuthorityDocument(string targetPath, string selfId, IReadOnlyDictionary<string, string> endpoints, IReadOnlyDictionary<string, FederationIdentity> identities) {
        var machineCatalog = CliWorldVocabulary.EnsureInstalled();
        var catalogFingerprint = CliWorldVocabulary.Fingerprint(catalog: machineCatalog);
        var root = (JsonNode.Parse(json: File.ReadAllText(path: targetPath))?.AsObject()
            ?? throw new InvalidOperationException(message: $"authority world '{targetPath}' is not a JSON object"));
        var composedCapacity = 8;
        var composedNetworkPlayers = 4;
        var composedAdmission = new JsonArray();

        if (Puck.World.WorldDefinitionFileSource.TryComposeDocumentTree(
            catalog: machineCatalog,
            catalogFingerprint: catalogFingerprint,
            path: targetPath,
            reason: out _,
            tree: out var composed
        )) {
            if (composed![WorldBodiesSectionName] is JsonObject composedPopulation) {
                composedCapacity = Math.Max(
                    val1: composedCapacity,
                    val2: (composedPopulation["capacity"]?.GetValue<int>() ?? composedCapacity)
                );
                composedNetworkPlayers = Math.Max(
                    val1: composedNetworkPlayers,
                    val2: (composedPopulation["networkPlayers"]?.GetValue<int>() ?? composedNetworkPlayers)
                );
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

        if (root[WorldBodiesSectionName] is not JsonObject population) {
            population = new JsonObject();
            root[WorldBodiesSectionName] = population;
        }

        population["capacity"] = composedCapacity;
        population["networkPlayers"] = composedNetworkPlayers;

        foreach (var (peerId, peerEndpoint) in endpoints) {
            if (peerId == selfId) {
                continue;
            }

            composedAdmission.Add(value: AdmissionRow(
                peer: identities[peerId],
                peerSubject: peerEndpoint
            ));
        }

        root["admission"] = composedAdmission;
        File.WriteAllText(
            path: targetPath,
            contents: root.ToJsonString(options: new System.Text.Json.JsonSerializerOptions { NewLine = "\n", WriteIndented = true }),
            encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        );
    }
}
