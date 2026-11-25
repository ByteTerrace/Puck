using System.CommandLine;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions;
using Puck.Storage;
using Puck.World;
using Puck.World.Server;
using Puck.World.Silo;

namespace Puck.Cli.Automation;

/// <summary>Rehearses a release package's deployment on this machine, in the mode a deployment group's hosted-data policy
/// selects (<see cref="Mode"/>), over a local directory store and the release's own silo started as a worker starts it,
/// with a managed release section. Every operation runs through <see cref="WorldReleaseCoordinator"/> from its Drain phase
/// on, so the roots and the group record are exactly what a deployment writes. Qualification is not rehearsed: it runs
/// the installed engines' legs before any deployment already.
/// <para>Scratch mode rehearses the replace deployment: a first deployment's bootstrap, after which the silo opens
/// admission, answers public health while stepping, and drains cleanly.</para>
/// <para>Durable mode rehearses the preservation path on top of that bootstrap: a reset deployment to a second release
/// (the source drained and captured through its own <c>/release/drain</c> route, the candidate started over the reset
/// roots and admitted), then a third release whose candidate refuses activation, which pre-commit recovery must answer by
/// restoring the protected roots and readmitting the source.</para></summary>
public static class WorldReleaseRehearsal {
    /// <summary>The path a rehearsal proves, named as the deployment group's hosted-data policy.</summary>
    public enum Mode {
        /// <summary>A first deployment's bootstrap, the path a scratch deployment runs.</summary>
        Scratch,
        /// <summary>The bootstrap, then a reset deployment and a recovered failed deployment, the paths a durable deployment
        /// runs.</summary>
        Durable,
    }

    // The deployment group every rehearsal store holds.
    private const string Group = "rehearsal";
    // Public health must still answer this long after admission opened, which a silo whose pump stopped cannot do: its
    // progress window (ProgressSeconds) runs out first.
    private const int ProgressSeconds = 10;
    private static readonly TimeSpan SteppingWindow = TimeSpan.FromSeconds(seconds: (ProgressSeconds + 2));
    private static readonly JsonSerializerOptions Json = new(defaults: JsonSerializerDefaults.Web);

    /// <summary>Creates the <c>rehearse</c> verb under <c>world release</c>.</summary>
    /// <param name="clock">The CLI host's clock, which every deadline runs on.</param>
    /// <returns>The verb.</returns>
    public static Command Create(TimeProvider clock) {
        var package = new Argument<string?>(name: "package-directory") { Arity = ArgumentArity.ZeroOrOne, Description = "The release package (release.json and its composed worlds) a deployment would deploy, or a directory 'puck world prepare' wrote, which is packaged first. With --install it may be omitted: the install's own composed worlds are rehearsed." };
        var silo = new Option<string?>("--silo") { Description = "The world silo to run: Puck.World.Silo.dll (run with dotnet), its executable, or a host release's puck (run as `puck world host serve`). Required without --install." };
        var install = new Option<string?>("--install") { Description = "An install root holding a host release, installed from its channel as a hosting machine installs it: the rehearsal runs through that release's own CLI, silo, extensions and asset layout." };
        var extensions = new Option<string?>("--extensions-dir") { Description = "The extensions directory the silo searches; absent, the silo's default directories." };
        var documents = new Option<string?>("--document-directory") { Description = "The asset layout the release's documents resolve their relative paths beside, as the deployed silo's documentDirectory; absent, the current directory." };
        var mode = new Option<Mode>("--mode") { DefaultValueFactory = static _ => Mode.Scratch, Description = "The hosted-data policy whose deployment path is rehearsed: scratch (a first deployment's bootstrap) or durable (the bootstrap, a reset deployment through the source's drain, and a failed deployment the source is recovered from)." };
        var output = CliOptions.Output(description: "A fresh directory for the rehearsal's store, silo documents, keys and logs; absent, a new directory under artifacts/world-release-rehearsals.");
        var timeout = CliOptions.Count(defaultValue: 900, description: "Seconds each silo may take to answer private health before the rehearsal fails.", maximum: 7200, minimum: 10, name: "--timeout");
        var command = new Command(
            description: "Rehearse a release package's deployment locally, as a scratch or durable deployment group runs it, against a local store and the release's own silo.",
            name: "rehearse"
        ) { package, silo, install, extensions, documents, mode, output, timeout };

        command.Detail(detail: """
            Exit codes: 0 every operation of the mode ended as it should (admitted, or recovered with the source
            readmitted) and the last silo drained; 1 the rehearsal ran and a silo or a transaction failed (its own lines
            and the private health reason are printed); 2 a refusal (an unreadable or unverified package, a missing
            --silo or --install, or an output directory that already exists).
            """);
        command.SetAction(action: (parse, token) => {
            var directory = Path.GetFullPath(path: (parse.GetValue(option: output) ?? Path.Combine(
                path1: "artifacts",
                path2: "world-release-rehearsals",
                path3: Guid.NewGuid().ToString(format: "N")
            )));
            var packageDirectory = ((parse.GetValue(argument: package) is { } given) ? Path.GetFullPath(path: given) : null);
            var timeoutSeconds = parse.GetValue(option: timeout);
            var selected = parse.GetValue(option: mode);

            if (parse.GetValue(option: install) is { } root) {
                return RunInstalledAsync(
                    installRoot: Path.GetFullPath(path: root),
                    mode: selected,
                    outputDirectory: directory,
                    packageDirectory: packageDirectory,
                    timeoutSeconds: timeoutSeconds,
                    token: token
                );
            }
            if ((parse.GetValue(option: silo) is not { } siloPath) || (packageDirectory is null)) {
                return Task.FromResult(result: CliExit.Refuse(verb: "world release rehearse", what: "arguments", why: "a rehearsal without --install needs a package directory and --silo"));
            }
            return RunAsync(
                clock: clock,
                documentDirectory: Path.GetFullPath(path: (parse.GetValue(option: documents) ?? Environment.CurrentDirectory)),
                extensionsDirectory: parse.GetValue(option: extensions),
                mode: selected,
                outputDirectory: directory,
                packageDirectory: packageDirectory,
                readyTimeout: TimeSpan.FromSeconds(seconds: timeoutSeconds),
                silo: Path.GetFullPath(path: siloPath),
                token: token
            );
        });
        return command;
    }

    // Runs the rehearsal through the installed host release's own CLI, so the silo, extensions and asset layout are
    // exactly those a hosting machine runs from the same install path.
    private static async Task<int> RunInstalledAsync(string installRoot, string? packageDirectory, string outputDirectory, Mode mode, int timeoutSeconds, CancellationToken token) {
        if (Directory.Exists(path: outputDirectory) || File.Exists(path: outputDirectory)) { throw new IOException(message: "a rehearsal requires a fresh output directory"); }
        var (version, engine) = (WorldHostEngine.Current(installRoot: installRoot) ?? throw new InvalidDataException(message: $"{installRoot} holds no installed host release"));
        var executable = Path.Combine(path1: version, path2: Puck.Confinement.InstallLayout.ExecutableFileName(name: WorldHostLayout.Executable));
        var worlds = Path.Combine(path1: version, path2: WorldHostLayout.WorldsDirectory);

        Console.WriteLine(value: $"Rehearsing through the installed engine {engine} ({version}).");
        Directory.CreateDirectory(path: outputDirectory);
        var run = await CliProcess.RunAsync(
            arguments: [
                "world", "release", "rehearse", (packageDirectory ?? worlds), "--silo", executable, "--document-directory", worlds,
                "--output", Path.Combine(path1: outputDirectory, path2: "run"), "--mode", mode.ToString().ToLowerInvariant(),
                "--timeout", timeoutSeconds.ToString(provider: System.Globalization.CultureInfo.InvariantCulture),
            ],
            cancellationToken: token,
            capture: false,
            fileName: executable,
            workingDirectory: version
        ).ConfigureAwait(continueOnCapturedContext: false);

        return run.ExitCode;
    }
    private static async Task<int> RunAsync(string packageDirectory, string silo, string? extensionsDirectory, string documentDirectory, string outputDirectory,
        Mode mode, TimeSpan readyTimeout, TimeProvider clock, CancellationToken token) {
        var directory = outputDirectory;

        if (Directory.Exists(path: directory) || File.Exists(path: directory)) { throw new IOException(message: "a rehearsal requires a fresh output directory"); }
        Directory.CreateDirectory(path: directory);
        var keys = Directory.CreateDirectory(path: Path.Combine(path1: directory, path2: "keys")).FullName;
        // A directory 'puck world prepare' wrote, holding composed documents but no manifest, is packaged here first.
        if (!File.Exists(path: Path.Combine(path1: packageDirectory, path2: "release.json"))) {
            packageDirectory = PreparePackage(
                directory: directory,
                documentDirectory: documentDirectory,
                keys: keys,
                name: "source",
                owner: Guid.NewGuid(),
                worldsDirectory: packageDirectory
            );
        }
        var manifest = WorldReleaseManifest.ReadVerified(packageDirectory: packageDirectory);
        var owners = manifest.Definitions.Keys.Select(selector: static key => key.Split('/')[0]).Distinct(comparer: StringComparer.Ordinal).ToArray();

        if (
            (owners.Length != 1) ||
            !Guid.TryParseExact(input: owners[0], format: "D", result: out var owner)
        ) { throw new InvalidDataException(message: "a rehearsal requires a release whose worlds share one owner"); }
        var services = new ServiceCollection();

        Puck.Storage.DependencyInjection.PuckStorageServiceRegistration.AddCore(services: services);
        using var provider = services.BuildServiceProvider();
        var blobs = provider.GetRequiredService<IObjectBlobStore>();
        var target = WorldAuthorityBlobStore.DirectoryTarget(rootPath: Path.Combine(
            path1: directory,
            path2: "store"
        ));
        var archive = new WorldReleaseArchive(blobs, target, owner);
        var groups = new WorldReleaseGroupStore(
            owner: owner,
            store: blobs,
            target: target
        );
        await using var rehearsal = new Rehearsal(
            archive: archive,
            authority: new WorldAuthorityBlobStore(
                documentDirectory: documentDirectory,
                store: blobs,
                target: target,
                timeProvider: clock
            ),
            clock: clock,
            directory: directory,
            documentDirectory: documentDirectory,
            extensionsDirectory: extensionsDirectory,
            identities: [.. manifest.Definitions.Keys.Order(comparer: StringComparer.Ordinal).Select(selector: static key => WorldAuthorityIdentity.ParseRootKey(rootKey: key))],
            keys: keys,
            owner: owner,
            readyTimeout: readyTimeout,
            silo: silo
        );

        await archive.SaveAsync(cancellationToken: token, manifest: manifest, packageDirectory: packageDirectory).ConfigureAwait(continueOnCapturedContext: false);
        var created = await groups.CreateAsync(
            activeRelease: null,
            cancellationToken: token,
            deploymentGroup: Group
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (!created.Ok) { throw new InvalidOperationException(message: created.Detail); }
        Console.WriteLine(value: $"Rehearsing a {mode.ToString().ToLowerInvariant()} deployment of release {manifest.Identity} ({manifest.Definitions.Count} worlds) in {directory}");
        // Qualification is the containers' (WorldReleaseQualificationRunner); every operation claims the group as the
        // coordinator does after it, and the coordinator drives the rest.
        var bootstrap = new Runtime(rehearsal: rehearsal, refuseCandidate: false, source: null, target: manifest);
        var admitted = await rehearsal.OperateAsync(
            groups: groups,
            runtime: bootstrap,
            stateReset: null,
            token: token
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (admitted is not null) {
            rehearsal.Report(headline: $"Rehearsal failed: the bootstrap did not open admission ({admitted}).");
            return CliExit.Failed;
        }
        var serving = bootstrap.Candidate!;

        if (await serving.ProveSteppingAsync(token: token).ConfigureAwait(continueOnCapturedContext: false) is { } stepping) {
            rehearsal.Report(headline: $"Rehearsal failed: the bootstrapped silo did not keep stepping ({stepping}).");
            return CliExit.Failed;
        }
        if (mode == Mode.Durable) {
            var reset = await PrepareSuccessorAsync(archive: archive, directory: directory, documentDirectory: documentDirectory, keys: keys, name: "reset", owner: owner, sourcePackage: packageDirectory, token: token).ConfigureAwait(continueOnCapturedContext: false);
            var deployment = new Runtime(rehearsal: rehearsal, refuseCandidate: false, source: (manifest, serving), target: reset);

            if (await rehearsal.OperateAsync(groups: groups, runtime: deployment, stateReset: "rehearsal: the durable reset path", token: token).ConfigureAwait(continueOnCapturedContext: false) is { } failed) {
                rehearsal.Report(headline: $"Rehearsal failed: the reset deployment did not open admission ({failed}).");
                return CliExit.Failed;
            }
            serving = deployment.Candidate!;
            if (await serving.ProveSteppingAsync(token: token).ConfigureAwait(continueOnCapturedContext: false) is { } candidateStepping) {
                rehearsal.Report(headline: $"Rehearsal failed: the reset deployment's silo did not keep stepping ({candidateStepping}).");
                return CliExit.Failed;
            }
            var refused = await PrepareSuccessorAsync(archive: archive, directory: directory, documentDirectory: documentDirectory, keys: keys, name: "refused", owner: owner, sourcePackage: packageDirectory, token: token).ConfigureAwait(continueOnCapturedContext: false);
            var recovery = new Runtime(rehearsal: rehearsal, refuseCandidate: true, source: (reset, serving), target: refused);

            if (await rehearsal.RecoverAsync(groups: groups, runtime: recovery, token: token).ConfigureAwait(continueOnCapturedContext: false) is { } unrecovered) {
                rehearsal.Report(headline: $"Rehearsal failed: the failed deployment's source was not recovered ({unrecovered}).");
                return CliExit.Failed;
            }
            serving = recovery.Recovered!;
            if (await serving.ProveSteppingAsync(token: token).ConfigureAwait(continueOnCapturedContext: false) is { } recoveredStepping) {
                rehearsal.Report(headline: $"Rehearsal failed: the recovered source did not keep stepping ({recoveredStepping}).");
                return CliExit.Failed;
            }
        }
        if (await serving.RetireAsync(token: token).ConfigureAwait(continueOnCapturedContext: false) is { } drained) {
            rehearsal.Report(headline: $"Rehearsal failed: the silo did not drain cleanly ({drained}).");
            return CliExit.Failed;
        }
        if (rehearsal.ProveTelemetry() is { } telemetry) {
            rehearsal.Report(headline: $"Rehearsal failed: {telemetry}.");
            return CliExit.Failed;
        }
        Console.WriteLine(value: (mode == Mode.Durable)
            ? $"Rehearsal passed: {manifest.Definitions.Count} worlds were bootstrapped, reset through the source's drain into a second release that opened admission, and recovered to that release when a third release's candidate refused activation; every admitted silo stepped for {SteppingWindow.TotalSeconds:0} s and the last drained."
            : $"Rehearsal passed: the silo activated {manifest.Definitions.Count} worlds from a first deployment's roots, opened admission, answered public health while stepping for {SteppingWindow.TotalSeconds:0} s, and drained.");
        return CliExit.Success;
    }

    // A release the durable rehearsal deploys after the source: the source package's documents under the same owner and
    // another label, so its identity differs and the operation is a real deployment between two releases.
    private static async Task<WorldReleaseManifest> PrepareSuccessorAsync(WorldReleaseArchive archive, string sourcePackage, string directory, string documentDirectory,
        string keys, Guid owner, string name, CancellationToken token) {
        var package = PreparePackage(
            directory: directory,
            documentDirectory: documentDirectory,
            keys: keys,
            name: name,
            owner: owner,
            worldsDirectory: sourcePackage
        );
        var manifest = WorldReleaseManifest.ReadVerified(packageDirectory: package);

        await archive.SaveAsync(cancellationToken: token, manifest: manifest, packageDirectory: package).ConfigureAwait(continueOnCapturedContext: false);
        return manifest;
    }
    // A silo document over a local directory store: every world pinned under its owner with the rehearsal's signing key
    // for that world (created under keys on first use, so every silo of one rehearsal signs as the same world), its
    // state under <directory>/<name>-state, and the deployment's issuer with the engine's own account sign-in standing in
    // for the deployment's provider, so every rehearsed silo starts the issuer its rows trust.
    private static JsonObject SiloDocument(string directory, string name, string store, string documentDirectory, string keys, IReadOnlyList<WorldAuthorityIdentity> identities) {
        var rows = new JsonArray();
        var issuerKey = PuckPaths.Slashed(path: Path.Combine(path1: keys, path2: "issuer.pk8"));
        var accountKey = PuckPaths.Slashed(path: Path.Combine(path1: keys, path2: "rehearsal-account.pk8"));

        foreach (var path in new[] { issuerKey, accountKey }) {
            if (!File.Exists(path: path)) {
                using var key = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);

                File.WriteAllBytes(path: path, bytes: key.ExportPkcs8PrivateKey());
            }
        }
        using var account = ECDsa.Create();

        account.ImportPkcs8PrivateKey(source: File.ReadAllBytes(path: accountKey), bytesRead: out _);

        foreach (var identity in identities) {
            var keyPath = PuckPaths.Slashed(path: Path.Combine(path1: keys, path2: $"{identity.World}.pk8"));

            if (!File.Exists(path: keyPath)) {
                using var key = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);

                File.WriteAllBytes(path: keyPath, bytes: key.ExportPkcs8PrivateKey());
            }
            rows.Add(item: new JsonObject {
                ["owner"] = identity.Owner.ToString(format: "D"),
                ["world"] = identity.World.Value,
                ["pinned"] = true,
                ["federation"] = new JsonObject { ["keyFile"] = keyPath },
            });
        }
        return new JsonObject {
            ["schema"] = Puck.World.WorldSiloDefinition.SchemaVersion,
            ["worlds"] = rows,
            ["doors"] = new JsonObject { ["budget"] = rows.Count },
            ["store"] = new JsonObject { ["type"] = "directory", ["settings"] = new JsonObject { ["path"] = PuckPaths.Slashed(path: store) } },
            ["stateDir"] = PuckPaths.Slashed(path: Path.Combine(path1: directory, path2: $"{name}-state")),
            ["documentDirectory"] = PuckPaths.Slashed(path: documentDirectory),
            ["clustering"] = new JsonObject { ["kind"] = "Localhost" },
            ["issuer"] = new JsonObject {
                ["keyFile"] = issuerKey,
                ["signIn"] = new JsonObject {
                    ["type"] = Puck.World.Server.WorldAccountSignIn.Provider.Type,
                    ["settings"] = new JsonObject {
                        ["accounts"] = new JsonObject { ["rehearsal"] = Convert.ToBase64String(inArray: account.ExportSubjectPublicKeyInfo()) },
                    },
                },
            },
        };
    }
    // Packages composed documents (a 'puck world prepare' output, or a release package's own) as the release a deployment
    // would carry: the given owner, each document's own host, a label naming the package, and placeholder source and
    // engine pins, since the rehearsal runs whichever engine it is handed rather than one a channel names.
    private static string PreparePackage(string worldsDirectory, string directory, string documentDirectory, string keys, Guid owner, string name) {
        var package = Directory.CreateDirectory(path: Path.Combine(path1: directory, path2: $"{name}-package")).FullName;
        var identities = new List<WorldAuthorityIdentity>();

        foreach (var file in Directory.EnumerateFiles(path: worldsDirectory, searchPattern: ("*" + WorldDocumentName.DocumentSuffix)).Order(comparer: StringComparer.Ordinal)) {
            File.Copy(sourceFileName: file, destFileName: Path.Combine(path1: package, path2: Path.GetFileName(path: file)));
            identities.Add(item: new(Owner: owner, World: SafeName.Parse(candidate: WorldDocumentName.OfDocumentFile(path: Path.GetFileName(path: file)))));
        }
        if (identities.Count == 0) {
            throw new InvalidDataException(message: $"'{worldsDirectory}' holds neither a release.json nor composed world documents");
        }
        var siloPath = Path.Combine(path1: directory, path2: $"{name}-package-silo.json");

        CliFiles.WriteJson(
            path: siloPath,
            value: SiloDocument(
                directory: directory,
                documentDirectory: documentDirectory,
                identities: identities,
                keys: keys,
                name: $"{name}-package",
                store: Path.Combine(path1: directory, path2: $"{name}-package-store")
            )
        );
        _ = WorldReleaseCommand.Prepare(
            engine: ("sha256/" + new string(c: '0', count: 64)),
            label: $"rehearsal-{name}",
            outputPath: null,
            packageDirectory: package,
            peerProtocolContract: WorldReleaseManifest.CurrentPeerProtocolContract,
            persistenceContract: WorldReleaseManifest.CurrentPersistenceContract,
            siloPath: siloPath,
            sourceRevision: new string(c: '0', count: 40)
        );
        return package;
    }

    // What every operation of one rehearsal shares: the local store, the release archive, the keys and the silo binary.
    private sealed class Rehearsal(WorldReleaseArchive archive, WorldAuthorityBlobStore authority, Guid owner, IReadOnlyList<WorldAuthorityIdentity> identities,
        string directory, string documentDirectory, string keys, string silo, string? extensionsDirectory, TimeSpan readyTimeout, TimeProvider clock) : IAsyncDisposable {
        private readonly List<SiloProcess> m_silos = [];

        public WorldReleaseArchive Archive => archive;
        public WorldAuthorityBlobStore Authority => authority;
        public TimeProvider Clock => clock;
        public string DocumentDirectory => documentDirectory;
        public IReadOnlyList<WorldAuthorityIdentity> Identities => identities;
        public Guid Owner => owner;
        public TimeSpan ReadyTimeout => readyTimeout;
        private string TelemetryPath => Path.Combine(path1: directory, path2: "telemetry.jsonl");

        // Claims the group for the runtime's target, as a deployment does once qualification passed, and drives the
        // operation; returns why it did not end admitted, or null.
        public async Task<string?> OperateAsync(WorldReleaseGroupStore groups, Runtime runtime, string? stateReset, CancellationToken token) {
            var result = await RunOperationAsync(groups: groups, runtime: runtime, stateReset: stateReset, token: token).ConfigureAwait(continueOnCapturedContext: false);

            return (result.Completed && !result.CandidatePrivate) ? null : result.Detail;
        }
        // Runs an operation whose candidate refuses activation and checks that pre-commit recovery restored and readmitted
        // its source: the coordinator reports the source recovered, the group serves the source again with no pending
        // operation, and the restarted source reports that release with admission open. Returns why not, or null.
        public async Task<string?> RecoverAsync(WorldReleaseGroupStore groups, Runtime runtime, CancellationToken token) {
            var source = runtime.SourceRelease!;
            var result = await RunOperationAsync(groups: groups, runtime: runtime, stateReset: "rehearsal: a failed candidate the source is recovered from", token: token).ConfigureAwait(continueOnCapturedContext: false);

            if (result.Completed || !result.SourceRecovered) { return $"the coordinator did not report the source recovered: {result.Detail}"; }
            var record = (await groups.LoadAsync(deploymentGroup: Group, cancellationToken: token).ConfigureAwait(continueOnCapturedContext: false))?.Record;

            if (
                (record is null) ||
                (record.ActiveRelease != source.Identity) ||
                (record.PendingOperationId is not null) ||
                (record.Admission != WorldReleaseAdmissionState.Open)
            ) {
                return $"the group does not serve the recovered source: active {record?.ActiveRelease ?? "(none)"}, pending {record?.PendingOperationId?.ToString(format: "D") ?? "(none)"}, admission {record?.Admission.ToString() ?? "(none)"}";
            }
            var status = await runtime.Recovered!.StatusAsync(token: token).ConfigureAwait(continueOnCapturedContext: false);

            return ((status?.Release == source.Identity) && status.AdmissionOpen)
                ? null
                : $"the restarted source reports another release or closed admission: {JsonSerializer.Serialize(value: status, options: Json)}";
        }
        private async Task<WorldReleaseRunResult> RunOperationAsync(WorldReleaseGroupStore groups, Runtime runtime, string? stateReset, CancellationToken token) {
            var current = (await groups.LoadAsync(deploymentGroup: Group, cancellationToken: token).ConfigureAwait(continueOnCapturedContext: false))
                ?? throw new InvalidOperationException(message: "the rehearsal's deployment group disappeared");
            var begun = await groups.BeginAsync(
                current,
                Guid.NewGuid(),
                runtime.Target.Identity,
                token,
                stateReset
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (!begun.Ok) { throw new InvalidOperationException(message: begun.Detail); }
            Console.WriteLine(value: $"Operation {begun.Snapshot!.Value.Record.PendingOperationId:D}: {runtime.SourceRelease?.Identity ?? "(first deployment)"} to {runtime.Target.Identity}{((stateReset is null) ? "" : " (reset)")}");
            return await new WorldReleaseCoordinator(groups: groups).ResumeAsync(
                begun.Snapshot!.Value,
                runtime.Target,
                runtime,
                token
            ).ConfigureAwait(continueOnCapturedContext: false);
        }
        // Writes the managed silo document a worker runs (a release section naming this group and the release it serves,
        // every world pinned with its signing key, the lifecycle listener on loopback) and starts the silo on it.
        public SiloProcess Start(string name, string expectedRelease) {
            var port = FreeLoopbackPort();
            var document = SiloDocument(
                directory: directory,
                documentDirectory: documentDirectory,
                identities: identities,
                keys: keys,
                name: name,
                store: Path.Combine(path1: directory, path2: "store")
            );

            document["lifecycle"] = new JsonObject {
                ["healthPort"] = port,
                ["healthAddress"] = "127.0.0.1",
                ["shutdownSeconds"] = 120,
                ["progressTimeoutSeconds"] = ProgressSeconds,
                ["checkpointTimeoutSeconds"] = 180,
                ["journalTimeoutSeconds"] = 30,
                ["journalBacklogLimit"] = 1024,
                // The provider seam a deployment's telemetry goes through, with the built-in local sink.
                ["telemetry"] = new JsonObject { ["type"] = WorldFileTelemetrySink.Type, ["settings"] = new JsonObject { ["path"] = PuckPaths.Slashed(path: TelemetryPath) } },
            };
            document["release"] = new JsonObject {
                ["group"] = Group,
                ["owner"] = owner.ToString(format: "D"),
                ["expectedRelease"] = expectedRelease,
                ["closedGroupRewind"] = true,
            };
            var siloPath = Path.Combine(path1: directory, path2: $"{name}.json");

            CliFiles.WriteJson(path: siloPath, value: document);
            string[] arguments = ["--silo", siloPath, .. ((extensionsDirectory is null) ? [] : (string[])["--extensions-dir", Path.GetFullPath(path: extensionsDirectory)])];
            var process = new SiloProcess(clock: clock, healthPort: port, log: Path.Combine(path1: directory, path2: $"{name}.log"), name: name);

            process.Start(arguments: arguments, silo: silo, workingDirectory: documentDirectory);
            m_silos.Add(item: process);
            return process;
        }
        // The silo's telemetry sink must have received activation narration, which proves the seam a deployment's
        // provider is selected through carries the evidence a deleted worker would otherwise lose.
        public string? ProveTelemetry() {
            var lines = File.Exists(path: TelemetryPath) ? File.ReadAllLines(path: TelemetryPath) : [];
            var narration = lines.Count(predicate: static line => line.Contains(value: "silo.activate.timing", comparisonType: StringComparison.Ordinal));

            return (narration > 0) ? null : $"its telemetry sink received {lines.Length} records and no activation narration ({TelemetryPath})";
        }
        // Reports a failure against the silo started last, whose lines name it.
        public void Report(string headline) {
            if (m_silos.Count == 0) {
                Console.Error.WriteLine(value: headline);
                return;
            }
            m_silos[^1].Report(headline: headline);
        }
        private static int FreeLoopbackPort() {
            using var listener = new TcpListener(localaddr: IPAddress.Loopback, port: 0);

            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        public async ValueTask DisposeAsync() {
            foreach (var process in m_silos) { await process.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false); }
        }
    }

    // One operation's release runtime: the storage steps over the local store, and silos as child processes reached
    // through the same loopback release-control routes a deployment reaches on its worker. A first deployment has no
    // source; any other drains its source through the source's /release/drain route. A refusing candidate is started
    // with a release section naming the source release as if it were the target, which the silo refuses at activation
    // (its expected release is not the pending candidate) and exits, as a candidate that cannot start does.
    private sealed class Runtime(Rehearsal rehearsal, (WorldReleaseManifest Release, SiloProcess Silo)? source, WorldReleaseManifest target, bool refuseCandidate)
        : IWorldReleaseRuntime {
        public SiloProcess? Candidate { get; private set; }
        public SiloProcess? Recovered { get; private set; }
        public WorldReleaseManifest? SourceRelease => source?.Release;
        public WorldReleaseManifest Target => target;

        public async Task<WorldReleaseRuntimeResult> DrainSourceAndCaptureAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            if (source is not { } serving) {
                await WorldReleaseBootstrap.PublishAsync(
                    archive: rehearsal.Archive,
                    authority: rehearsal.Authority,
                    cancellationToken: cancellationToken,
                    documentDirectory: rehearsal.DocumentDirectory,
                    history: operation.History,
                    manifest: target,
                    owner: rehearsal.Owner
                ).ConfigureAwait(continueOnCapturedContext: false);
                return new(
                    Detail: "bootstrap definitions published and their unowned roots captured",
                    RecoveryRoots: await WorldReleaseBootstrap.CaptureRootsAsync(
                        authority: rehearsal.Authority,
                        cancellationToken: cancellationToken,
                        identities: rehearsal.Identities,
                        operationId: operation.PendingOperationId!.Value
                    ).ConfigureAwait(continueOnCapturedContext: false),
                    Succeeded: true
                );
            }
            var roots = await serving.Silo.ReleaseDrainAsync(operationId: operation.PendingOperationId!.Value, token: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            if (roots is null) { return new(false, serving.Silo.LastAnswer); }
            WorldReleaseRoots.Validate(identities: [.. rehearsal.Identities], roots: roots);
            // The drained host stays frozen for retries; it is stopped only once its immutable roots exist.
            await serving.Silo.KillAsync().ConfigureAwait(continueOnCapturedContext: false);
            return new(
                Detail: "source drained through its release route and stopped; protected roots retained",
                RecoveryRoots: roots,
                Succeeded: true
            );
        }
        public async Task<WorldReleaseRuntimeResult> StartCandidatePrivatelyAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            if (Candidate is null) {
                if ((operation.PendingPhase == WorldReleaseOperationPhase.Activate) && (source is { } serving)) {
                    if (await WorldReleaseRoots.PrepareCandidateAsync(
                        archive: rehearsal.Archive,
                        authority: rehearsal.Authority,
                        cancellationToken: cancellationToken,
                        identities: rehearsal.Identities,
                        operation: operation,
                        source: serving.Release,
                        target: target
                    ).ConfigureAwait(continueOnCapturedContext: false) is { } refused) {
                        return new(false, refused);
                    }
                }
                Candidate = rehearsal.Start(
                    expectedRelease: ((refuseCandidate && (source is { } named)) ? named.Release.Identity : target.Identity),
                    name: (refuseCandidate ? "refused-candidate" : ((source is null) ? "source" : "candidate"))
                );
            }
            return await Candidate.WaitPrivatelyHealthyAsync(timeout: rehearsal.ReadyTimeout, token: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        }
        public async Task<WorldReleaseRuntimeResult> VerifyCandidatePrivatelyAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) =>
            await Candidate!.VerifyAsync(release: target.Identity, token: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        public Task<IReadOnlyList<(WorldAuthorityIdentity Identity, WorldAuthorityFence Fence)>> ReadFenceCensusAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) =>
            Candidate!.FenceCensusAsync(operationId: operation.PendingOperationId!.Value, token: cancellationToken);
        public Task<WorldReleaseRuntimePublicationResult> PublishCandidateAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) =>
            Candidate!.PublishAsync(operationId: operation.PendingOperationId!.Value, release: target.Identity, token: cancellationToken);
        public async Task<WorldReleaseRuntimeResult> StopCandidateAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            if (Candidate is not null) { await Candidate.KillAsync().ConfigureAwait(continueOnCapturedContext: false); }
            return new(true, "the rehearsal's candidate silo was stopped");
        }
        public async Task<WorldReleaseRuntimeResult> RecoverSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            // A first deployment has no source to restore: its store is disposable, and the rehearsal stops at its failure.
            if (source is null) { return new(true, "a rehearsal's store is disposable; nothing is restored"); }
            await WorldReleaseRoots.RestoreSourceAsync(
                authority: rehearsal.Authority,
                cancellationToken: cancellationToken,
                guard: static _ => Task.CompletedTask,
                identities: [.. rehearsal.Identities],
                operation: operation
            ).ConfigureAwait(continueOnCapturedContext: false);
            return new(true, "protected source roots restored under fresh fences");
        }
        public async Task<WorldReleaseRuntimeResult> StartRecoveredSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            if (source is not { } serving) { return new(false, "a rehearsed first deployment has no source to restart"); }
            Recovered ??= rehearsal.Start(expectedRelease: serving.Release.Identity, name: "recovered-source");
            return await Recovered.WaitPrivatelyHealthyAsync(timeout: rehearsal.ReadyTimeout, token: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        }
        public async Task<WorldReleaseRuntimePublicationResult> PublishRecoveredSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) =>
            (source is { } serving) && (Recovered is not null)
                ? await Recovered.PublishAsync(operationId: operation.PendingOperationId!.Value, release: serving.Release.Identity, token: cancellationToken).ConfigureAwait(continueOnCapturedContext: false)
                : new(WorldReleaseRuntimePublication.Refused, "a rehearsed first deployment has no source to publish");
    }

    // One silo child process and the loopback routes a deployment reaches on its worker: private and public health,
    // release control, and the retirement drain.
    private sealed class SiloProcess(string name, string log, int healthPort, TimeProvider clock) : IAsyncDisposable {
        private readonly HttpClient m_http = new() { Timeout = TimeSpan.FromSeconds(seconds: 30) };
        private Process? m_process;
        private Task? m_pumps;

        // The last private health answer or refusal, which a failure report names.
        public string LastAnswer { get; private set; } = "private health has not answered";

        private Uri Endpoint(string path) => new(uriString: $"http://127.0.0.1:{healthPort}{path}");

        public void Start(string silo, string[] arguments, string workingDirectory) {
            var assembly = silo.EndsWith(value: ".dll", comparisonType: StringComparison.OrdinalIgnoreCase);
            // A host release's CLI carries the silo and runs it as its service does.
            var host = (Path.GetFileNameWithoutExtension(path: silo) == WorldHostLayout.Executable);

            m_process = ChildProcess.StartRedirected(
                arguments: (assembly ? [silo, .. arguments] : (host ? ["world", "host", "serve", .. arguments] : arguments)),
                fileName: (assembly ? "dotnet" : silo),
                workingDirectory: workingDirectory
            );
            var writer = new StreamWriter(path: log) { AutoFlush = true };
            var gate = new Lock();

            async Task Copy(StreamReader reader) {
                while (await reader.ReadLineAsync().ConfigureAwait(continueOnCapturedContext: false) is { } line) {
                    lock (gate) { writer.WriteLine(value: line); }
                }
            }
            m_pumps = Task.WhenAll(Copy(reader: m_process.StandardOutput), Copy(reader: m_process.StandardError)).ContinueWith(
                continuationAction: _ => writer.Dispose(),
                scheduler: TaskScheduler.Default
            );
            m_process.StandardInput.Close();
            Console.WriteLine(value: $"Started the {name} silo (process {m_process.Id}); its lines go to {log}");
        }
        public async Task<WorldReleaseRuntimeResult> WaitPrivatelyHealthyAsync(TimeSpan timeout, CancellationToken token) {
            var deadline = (clock.GetTimestamp() + (long)(timeout.TotalSeconds * clock.TimestampFrequency));

            while (clock.GetTimestamp() < deadline) {
                token.ThrowIfCancellationRequested();
                if (m_process!.HasExited) {
                    return new(false, $"the {name} silo exited with code {m_process.ExitCode} before private health answered");
                }
                if (await PrivateHealthAsync(token: token).ConfigureAwait(continueOnCapturedContext: false)) {
                    return new(true, $"the {name} silo is running and privately healthy");
                }
                await Task.Delay(delay: TimeSpan.FromSeconds(seconds: 1), timeProvider: clock, cancellationToken: token).ConfigureAwait(continueOnCapturedContext: false);
            }
            return new(false, $"the {name} silo did not answer private health within {timeout.TotalSeconds:0} s: {LastAnswer}");
        }
        public Task<WorldReleaseWorkerStatus?> StatusAsync(CancellationToken token) =>
            m_http.GetFromJsonAsync<WorldReleaseWorkerStatus>(cancellationToken: token, options: Json, requestUri: Endpoint(path: "/release/status"));
        public async Task<WorldReleaseRuntimeResult> VerifyAsync(string release, CancellationToken token) {
            var status = await StatusAsync(token: token).ConfigureAwait(continueOnCapturedContext: false);

            if ((status?.Release != release) || (status.Group != Group)) {
                return new(false, $"the {name} silo reports another release or group: {JsonSerializer.Serialize(value: status, options: Json)}");
            }
            return (await PrivateHealthAsync(token: token).ConfigureAwait(continueOnCapturedContext: false))
                ? new(true, "exact candidate identity and private health verified")
                : new(false, LastAnswer);
        }
        public async Task<IReadOnlyList<(WorldAuthorityIdentity Identity, WorldAuthorityFence Fence)>> FenceCensusAsync(Guid operationId, CancellationToken token) {
            var census = (await m_http.GetFromJsonAsync<WorldReleaseWorkerFence[]>(
                cancellationToken: token,
                options: Json,
                requestUri: Endpoint(path: $"/release/fences/{operationId:D}")
            ).ConfigureAwait(continueOnCapturedContext: false)) ?? throw new InvalidDataException(message: $"the {name} silo returned no fence census");

            return [.. census.Select(selector: static row => row.ToCensusRow())];
        }
        // Publishes admission for the committed target or the restored source, as the worker's release route decides.
        public async Task<WorldReleaseRuntimePublicationResult> PublishAsync(Guid operationId, string release, CancellationToken token) {
            using var response = await m_http.PostAsync(
                cancellationToken: token,
                content: null,
                requestUri: Endpoint(path: $"/release/publish/{operationId:D}")
            ).ConfigureAwait(continueOnCapturedContext: false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken: token).ConfigureAwait(continueOnCapturedContext: false);

            if (!response.IsSuccessStatusCode) { return new(WorldReleaseRuntimePublication.Refused, $"the {name} silo refused publication ({(int)response.StatusCode}): {body}"); }
            var status = JsonSerializer.Deserialize<WorldReleaseWorkerStatus>(json: body, options: Json);

            return (status is { AdmissionOpen: true } && (status.Release == release) && (status.OperationId == operationId))
                ? new(WorldReleaseRuntimePublication.Opened, $"the {name} silo opened admission")
                : new(WorldReleaseRuntimePublication.Refused, $"the {name} silo's status after publication is not open admission: {body}");
        }
        // The source's release drain: the silo freezes and saves every world and answers the protected roots, or null
        // with the refusal kept as the last answer.
        public async Task<Dictionary<string, string>?> ReleaseDrainAsync(Guid operationId, CancellationToken token) {
            using var response = await m_http.PostAsync(
                cancellationToken: token,
                content: null,
                requestUri: Endpoint(path: $"/release/drain/{operationId:D}")
            ).ConfigureAwait(continueOnCapturedContext: false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken: token).ConfigureAwait(continueOnCapturedContext: false);

            if (!response.IsSuccessStatusCode) {
                LastAnswer = $"the {name} silo refused its release drain ({(int)response.StatusCode}): {body}";
                return null;
            }
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json: body, options: Json)
                ?? throw new InvalidDataException(message: $"the {name} silo's release drain returned no protected roots");
        }
        // Public health must keep answering ready through more than one progress window after admission opened.
        public async Task<string?> ProveSteppingAsync(CancellationToken token) {
            for (var read = 0; (read < 2); read++) {
                if (read > 0) { await Task.Delay(delay: SteppingWindow, timeProvider: clock, cancellationToken: token).ConfigureAwait(continueOnCapturedContext: false); }
                if (m_process!.HasExited) { return $"the {name} silo exited with code {m_process.ExitCode}"; }
                using var response = await m_http.GetAsync(requestUri: Endpoint(path: "/healthz"), cancellationToken: token).ConfigureAwait(continueOnCapturedContext: false);
                var body = await response.Content.ReadAsStringAsync(cancellationToken: token).ConfigureAwait(continueOnCapturedContext: false);

                if (!response.IsSuccessStatusCode) { return $"public health answered {(int)response.StatusCode}: {body}"; }
            }
            return null;
        }
        // The worker's own retirement route: freeze, save and stop, which must end the process with a success code.
        public async Task<string?> RetireAsync(CancellationToken token) {
            using var response = await m_http.PostAsync(requestUri: Endpoint(path: "/drain"), content: null, cancellationToken: token).ConfigureAwait(continueOnCapturedContext: false);

            if (!response.IsSuccessStatusCode) { return $"the drain route answered {(int)response.StatusCode}"; }
            using var exit = new CancellationTokenSource(delay: TimeSpan.FromSeconds(seconds: 120), timeProvider: clock);

            await m_process!.WaitForExitAsync(cancellationToken: exit.Token).ConfigureAwait(continueOnCapturedContext: false);
            await m_pumps!.ConfigureAwait(continueOnCapturedContext: false);
            return (m_process.ExitCode == 0) ? null : $"the {name} silo exited with code {m_process.ExitCode}";
        }
        // Prints the failure's headline, the last private health answer, and the silo's own tagged lines and last lines.
        public void Report(string headline) {
            var lines = File.Exists(path: log) ? File.ReadAllLines(path: log) : [];

            Console.Error.WriteLine(value: headline);
            Console.Error.WriteLine(value: $"{name} silo, last answer: {LastAnswer}");
            Console.Error.WriteLine(value: $"{name} silo (its tagged lines):");
            foreach (var line in lines.Where(predicate: static line => line.Contains(value: "[silo", comparisonType: StringComparison.Ordinal) || line.Contains(value: "[world", comparisonType: StringComparison.Ordinal)).TakeLast(count: 20)) {
                Console.Error.WriteLine(value: line);
            }
            Console.Error.WriteLine(value: $"{name} silo (last lines):");
            foreach (var line in lines.TakeLast(count: 15)) { Console.Error.WriteLine(value: line); }
            Console.Error.WriteLine(value: $"{name} silo log: {log}");
        }
        public async Task KillAsync() {
            if (m_process is { HasExited: false } running) {
                running.Kill(entireProcessTree: true);
                await running.WaitForExitAsync().ConfigureAwait(continueOnCapturedContext: false);
            }
            if (m_pumps is not null) { await m_pumps.ConfigureAwait(continueOnCapturedContext: false); }
        }

        private async Task<bool> PrivateHealthAsync(CancellationToken token) {
            try {
                using var response = await m_http.GetAsync(requestUri: Endpoint(path: "/private-healthz"), cancellationToken: token).ConfigureAwait(continueOnCapturedContext: false);

                LastAnswer = await response.Content.ReadAsStringAsync(cancellationToken: token).ConfigureAwait(continueOnCapturedContext: false);
                return response.IsSuccessStatusCode;
            } catch (HttpRequestException error) {
                LastAnswer = $"private health did not answer: {error.Message}";
                return false;
            }
        }

        public async ValueTask DisposeAsync() {
            await KillAsync().ConfigureAwait(continueOnCapturedContext: false);
            m_process?.Dispose();
            m_http.Dispose();
        }
    }
}
