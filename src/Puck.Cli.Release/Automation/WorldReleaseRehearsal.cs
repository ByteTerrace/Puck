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

/// <summary>Rehearses a release package's replace deployment on this machine: a local directory store, the bootstrap
/// transaction a deployment over scratch hosted data runs (<see cref="WorldReleaseCoordinator"/> from its Drain phase on,
/// so the roots and the group record are exactly what a deployment writes), and the release's own silo started as a
/// worker starts it, with a managed release section, against those roots. It passes once the silo has opened admission,
/// answers public health while stepping, and drains cleanly. Qualification is not rehearsed: it runs packaged legs in
/// containers before any deployment already.</summary>
public static class WorldReleaseRehearsal {
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
        var package = new Argument<string?>(name: "package-directory") { Arity = ArgumentArity.ZeroOrOne, Description = "The release package (release.json and its composed worlds) a deployment would deploy, or a directory 'puck world prepare' wrote, which is packaged first. With --image it may be omitted: the image's own composed worlds are rehearsed." };
        var silo = new Option<string?>("--silo") { Description = "The world silo to run: Puck.World.Silo.dll (run with dotnet) or its executable. Required without --image." };
        var image = new Option<string?>("--image") { Description = "A world-silo image to rehearse in: the rehearsal runs inside it with the image's own CLI, silo, extensions and asset layout, isolated from the network." };
        var extensions = new Option<string?>("--extensions-dir") { Description = "The extensions directory the silo searches; absent, the silo's default directories." };
        var documents = new Option<string?>("--document-directory") { Description = "The asset layout the release's documents resolve their relative paths beside, as the deployed silo's documentDirectory; absent, the current directory, which is that layout in the world-silo image." };
        var output = CliOptions.Output(description: "A fresh directory for the rehearsal's store, silo document, keys and log; absent, a new directory under artifacts/world-release-rehearsals.");
        var timeout = CliOptions.Count(defaultValue: 900, description: "Seconds the silo may take to answer private health before the rehearsal fails.", maximum: 7200, minimum: 10, name: "--timeout");
        var command = new Command(
            description: "Rehearse a release package's replace deployment locally: the bootstrap transaction's roots and the release's silo started against them as a worker starts it.",
            name: "rehearse"
        ) { package, silo, image, extensions, documents, output, timeout };

        command.Detail(detail: """
            Exit codes: 0 the silo opened admission, stepped and drained; 1 the rehearsal ran and the silo or the
            transaction failed (its own lines and the private health reason are printed); 2 a refusal (an unreadable or
            unverified package, a missing --silo or --image, or an output directory that already exists).
            """);
        command.SetAction(action: (parse, token) => {
            var directory = Path.GetFullPath(path: (parse.GetValue(option: output) ?? Path.Combine(
                path1: "artifacts",
                path2: "world-release-rehearsals",
                path3: Guid.NewGuid().ToString(format: "N")
            )));
            var packageDirectory = ((parse.GetValue(argument: package) is { } given) ? Path.GetFullPath(path: given) : null);
            var timeoutSeconds = parse.GetValue(option: timeout);

            if (parse.GetValue(option: image) is { } reference) {
                return RunInImageAsync(
                    image: reference,
                    outputDirectory: directory,
                    packageDirectory: packageDirectory,
                    timeoutSeconds: timeoutSeconds,
                    token: token
                );
            }
            if ((parse.GetValue(option: silo) is not { } siloPath) || (packageDirectory is null)) {
                return Task.FromResult(result: CliExit.Refuse(verb: "world release rehearse", what: "arguments", why: "a rehearsal without --image needs a package directory and --silo"));
            }
            return RunAsync(
                clock: clock,
                documentDirectory: Path.GetFullPath(path: (parse.GetValue(option: documents) ?? Environment.CurrentDirectory)),
                extensionsDirectory: parse.GetValue(option: extensions),
                outputDirectory: directory,
                packageDirectory: packageDirectory,
                readyTimeout: TimeSpan.FromSeconds(seconds: timeoutSeconds),
                silo: Path.GetFullPath(path: siloPath),
                token: token
            );
        });
        return command;
    }

    // Runs the rehearsal inside the image with its own CLI, so the silo, extensions and asset layout are exactly those
    // a worker runs. The container has no network (the silo's listeners are its own loopback), a read-only root, and the
    // output directory as its one writable mount; a package from the host is mounted read-only.
    private static async Task<int> RunInImageAsync(string image, string? packageDirectory, string outputDirectory, int timeoutSeconds, CancellationToken token) {
        if (Directory.Exists(path: outputDirectory) || File.Exists(path: outputDirectory)) { throw new IOException(message: "a rehearsal requires a fresh output directory"); }
        Directory.CreateDirectory(path: outputDirectory);
        string[] user = ((ProcessIdentity.LinuxUserAndGroup() is { } identity) ? ["--user", identity] : []);
        string[] packageMount = ((packageDirectory is null) ? [] : ["--mount", $"type=bind,source={packageDirectory},target=/package,readonly"]);
        var run = await CliProcess.RunAsync(
            arguments: [
                "run", "--rm", .. user, "--network", "none", "--read-only", "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
                "--tmpfs", "/tmp:rw,nosuid,nodev,size=256m", "--env", "ASPNETCORE_HTTP_PORTS=",
                "--mount", $"type=bind,source={outputDirectory},target=/rehearsal", .. packageMount,
                "--entrypoint", "dotnet", image, WorldSiloImageLayout.CliAssembly, "world", "release", "rehearse",
                ((packageDirectory is null) ? WorldSiloImageLayout.WorldsDirectory : "/package"),
                "--silo", WorldSiloImageLayout.SiloAssembly, "--output", "/rehearsal/run",
                "--timeout", timeoutSeconds.ToString(provider: System.Globalization.CultureInfo.InvariantCulture),
            ],
            cancellationToken: token,
            capture: false,
            fileName: "docker",
            workingDirectory: Environment.CurrentDirectory
        ).ConfigureAwait(continueOnCapturedContext: false);

        return run.ExitCode;
    }

    private static async Task<int> RunAsync(string packageDirectory, string silo, string? extensionsDirectory, string documentDirectory, string outputDirectory,
        TimeSpan readyTimeout, TimeProvider clock, CancellationToken token) {
        var directory = outputDirectory;

        if (Directory.Exists(path: directory) || File.Exists(path: directory)) { throw new IOException(message: "a rehearsal requires a fresh output directory"); }
        Directory.CreateDirectory(path: directory);
        // A directory 'puck world prepare' wrote, holding composed documents but no manifest, is packaged here first.
        if (!File.Exists(path: Path.Combine(path1: packageDirectory, path2: "release.json"))) {
            packageDirectory = PreparePackage(
                directory: directory,
                documentDirectory: documentDirectory,
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

        await archive.SaveAsync(
            cancellationToken: token,
            manifest: manifest,
            packageDirectory: packageDirectory
        ).ConfigureAwait(continueOnCapturedContext: false);
        var groups = new WorldReleaseGroupStore(
            owner: owner,
            store: blobs,
            target: target
        );
        var created = await groups.CreateAsync(
            activeRelease: null,
            cancellationToken: token,
            deploymentGroup: Group
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (!created.Ok) { throw new InvalidOperationException(message: created.Detail); }
        var operation = Guid.NewGuid();
        // Qualification is the containers' (WorldReleaseQualificationRunner); the rehearsal claims the group as the
        // coordinator does after it, and drives the rest of the transaction itself.
        var begun = await groups.BeginAsync(
            created.Snapshot!.Value,
            operation,
            manifest.Identity,
            token
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (!begun.Ok) { throw new InvalidOperationException(message: begun.Detail); }
        await using var runtime = new Runtime(
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
            manifest: manifest,
            owner: owner,
            readyTimeout: readyTimeout,
            silo: silo
        );

        Console.WriteLine(value: $"Rehearsing release {manifest.Identity} ({manifest.Definitions.Count} worlds) in {directory}");
        var result = await new WorldReleaseCoordinator(groups: groups).ResumeAsync(
            begun.Snapshot!.Value,
            manifest,
            runtime,
            token
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (!result.Completed || result.CandidatePrivate) {
            runtime.Report(headline: $"Rehearsal failed: the transaction did not open admission ({result.Detail}).");
            return CliExit.Failed;
        }
        var stepping = await runtime.ProveSteppingAsync(token: token).ConfigureAwait(continueOnCapturedContext: false);

        if (stepping is not null) {
            runtime.Report(headline: $"Rehearsal failed: the admitted silo did not keep stepping ({stepping}).");
            return CliExit.Failed;
        }
        var drained = await runtime.DrainAsync(token: token).ConfigureAwait(continueOnCapturedContext: false);

        if (drained is not null) {
            runtime.Report(headline: $"Rehearsal failed: the silo did not drain cleanly ({drained}).");
            return CliExit.Failed;
        }
        if (runtime.ProveTelemetry() is { } telemetry) {
            runtime.Report(headline: $"Rehearsal failed: {telemetry}.");
            return CliExit.Failed;
        }
        Console.WriteLine(value: $"Rehearsal passed: the silo activated {manifest.Definitions.Count} worlds from a first deployment's roots, opened admission, answered public health while stepping for {SteppingWindow.TotalSeconds:0} s, and drained.");
        return CliExit.Success;
    }

    // A silo document over a local directory store: every world pinned under its owner with a signing key generated
    // under <directory>/<name>-keys, and its state under <directory>/<name>-state.
    private static JsonObject SiloDocument(string directory, string name, string store, string documentDirectory, IReadOnlyList<WorldAuthorityIdentity> identities) {
        var keys = Directory.CreateDirectory(path: Path.Combine(path1: directory, path2: $"{name}-keys"));
        var rows = new JsonArray();

        foreach (var identity in identities) {
            using var key = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
            var keyPath = PuckPaths.Slashed(path: Path.Combine(path1: keys.FullName, path2: $"{identity.World}.pk8"));

            File.WriteAllBytes(path: keyPath, bytes: key.ExportPkcs8PrivateKey());
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
        };
    }
    // Packages the composed documents a 'puck world prepare' output holds as the release a deployment would carry: one
    // generated owner, each document's own host, and placeholder source and engine pins, since no image is involved.
    private static string PreparePackage(string worldsDirectory, string directory, string documentDirectory) {
        var package = Directory.CreateDirectory(path: Path.Combine(path1: directory, path2: "package")).FullName;
        var owner = Guid.NewGuid();
        var identities = new List<WorldAuthorityIdentity>();

        foreach (var file in Directory.EnumerateFiles(path: worldsDirectory, searchPattern: ("*" + WorldDocumentName.DocumentSuffix)).Order(comparer: StringComparer.Ordinal)) {
            File.Copy(sourceFileName: file, destFileName: Path.Combine(path1: package, path2: Path.GetFileName(path: file)));
            identities.Add(item: new(Owner: owner, World: SafeName.Parse(candidate: WorldDocumentName.OfDocumentFile(path: Path.GetFileName(path: file)))));
        }
        if (identities.Count == 0) {
            throw new InvalidDataException(message: $"'{worldsDirectory}' holds neither a release.json nor composed world documents");
        }
        var siloPath = Path.Combine(path1: directory, path2: "package-silo.json");

        CliFiles.WriteJson(
            path: siloPath,
            value: SiloDocument(
                directory: directory,
                documentDirectory: documentDirectory,
                identities: identities,
                name: "package",
                store: Path.Combine(path1: directory, path2: "package-store")
            )
        );
        _ = WorldReleaseCommand.Prepare(
            engineImageDigest: ("sha256:" + new string(c: '0', count: 64)),
            label: "rehearsal",
            outputPath: null,
            packageDirectory: package,
            peerProtocolContract: WorldReleaseManifest.CurrentPeerProtocolContract,
            persistenceContract: WorldReleaseManifest.CurrentPersistenceContract,
            siloPath: siloPath,
            sourceRevision: new string(c: '0', count: 40)
        );
        return package;
    }

    // The rehearsal's release runtime: the bootstrap's storage steps over the local store, and the release's silo as a
    // child process reached through the same loopback release-control routes a deployment reaches on its worker.
    private sealed class Runtime(WorldReleaseManifest manifest, WorldReleaseArchive archive, WorldAuthorityBlobStore authority, Guid owner,
        string directory, string documentDirectory, string silo, string? extensionsDirectory, TimeSpan readyTimeout, TimeProvider clock)
        : IWorldReleaseRuntime, IAsyncDisposable {
        private readonly HttpClient m_http = new() { Timeout = TimeSpan.FromSeconds(seconds: 30) };
        private readonly WorldAuthorityIdentity[] m_identities = [.. manifest.Definitions.Keys.Order(comparer: StringComparer.Ordinal).Select(selector: static key => WorldAuthorityIdentity.ParseRootKey(rootKey: key))];
        private readonly string m_log = Path.Combine(path1: directory, path2: "silo.log");
        private Process? m_process;
        private Task? m_pumps;
        private int m_healthPort;
        private string m_lastHealth = "private health has not answered";

        private Uri Endpoint(string path) => new(uriString: $"http://127.0.0.1:{m_healthPort}{path}");
        private string TelemetryPath => Path.Combine(path1: directory, path2: "telemetry.jsonl");

        // The silo's telemetry sink must have received its activation narration, which proves the seam a deployment's
        // provider is selected through carries the evidence a deleted worker would otherwise lose.
        public string? ProveTelemetry() {
            var lines = File.Exists(path: TelemetryPath) ? File.ReadAllLines(path: TelemetryPath) : [];
            var narration = lines.Count(predicate: static line => line.Contains(value: "silo.activate.timing", comparisonType: StringComparison.Ordinal));

            return (narration > 0) ? null : $"its telemetry sink received {lines.Length} records and no activation narration ({TelemetryPath})";
        }

        public async Task<WorldReleaseRuntimeResult> DrainSourceAndCaptureAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            await WorldReleaseBootstrap.PublishAsync(
                archive: archive,
                authority: authority,
                cancellationToken: cancellationToken,
                documentDirectory: documentDirectory,
                history: operation.History,
                manifest: manifest,
                owner: owner
            ).ConfigureAwait(continueOnCapturedContext: false);
            return new(
                Detail: "bootstrap definitions published and their unowned roots captured",
                RecoveryRoots: await WorldReleaseBootstrap.CaptureRootsAsync(
                    authority: authority,
                    cancellationToken: cancellationToken,
                    identities: m_identities,
                    operationId: operation.PendingOperationId!.Value
                ).ConfigureAwait(continueOnCapturedContext: false),
                Succeeded: true
            );
        }
        public async Task<WorldReleaseRuntimeResult> StartCandidatePrivatelyAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            if (m_process is null) { Start(); }
            var deadline = (clock.GetTimestamp() + (long)(readyTimeout.TotalSeconds * clock.TimestampFrequency));

            while (clock.GetTimestamp() < deadline) {
                cancellationToken.ThrowIfCancellationRequested();
                if (m_process!.HasExited) {
                    return new(false, $"the silo exited with code {m_process.ExitCode} before private health answered");
                }
                if (await PrivateHealthAsync(token: cancellationToken).ConfigureAwait(continueOnCapturedContext: false)) {
                    return new(true, "the release's silo is running and privately healthy");
                }
                await Task.Delay(delay: TimeSpan.FromSeconds(seconds: 1), timeProvider: clock, cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            }
            return new(false, $"the silo did not answer private health within {readyTimeout.TotalSeconds:0} s: {m_lastHealth}");
        }
        public async Task<WorldReleaseRuntimeResult> VerifyCandidatePrivatelyAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            var status = await m_http.GetFromJsonAsync<WorldReleaseWorkerStatus>(
                cancellationToken: cancellationToken,
                options: Json,
                requestUri: Endpoint(path: "/release/status")
            ).ConfigureAwait(continueOnCapturedContext: false);

            if ((status?.Release != manifest.Identity) || (status.Group != Group)) {
                return new(false, $"the silo reports another release or group: {JsonSerializer.Serialize(value: status, options: Json)}");
            }
            return (await PrivateHealthAsync(token: cancellationToken).ConfigureAwait(continueOnCapturedContext: false))
                ? new(true, "exact candidate identity and private health verified")
                : new(false, m_lastHealth);
        }
        public async Task<IReadOnlyList<(WorldAuthorityIdentity Identity, WorldAuthorityFence Fence)>> ReadFenceCensusAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            var census = (await m_http.GetFromJsonAsync<WorldReleaseWorkerFence[]>(
                cancellationToken: cancellationToken,
                options: Json,
                requestUri: Endpoint(path: $"/release/fences/{operation.PendingOperationId:D}")
            ).ConfigureAwait(continueOnCapturedContext: false)) ?? throw new InvalidDataException(message: "the silo returned no fence census");

            return [.. census.Select(selector: static row => row.ToCensusRow())];
        }
        public async Task<WorldReleaseRuntimePublicationResult> PublishCandidateAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            using var response = await m_http.PostAsync(
                cancellationToken: cancellationToken,
                content: null,
                requestUri: Endpoint(path: $"/release/publish/{operation.PendingOperationId:D}")
            ).ConfigureAwait(continueOnCapturedContext: false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            if (!response.IsSuccessStatusCode) { return new(WorldReleaseRuntimePublication.Refused, $"the silo refused publication ({(int)response.StatusCode}): {body}"); }
            var status = JsonSerializer.Deserialize<WorldReleaseWorkerStatus>(json: body, options: Json);

            return (status is { AdmissionOpen: true } && (status.Release == manifest.Identity) && (status.OperationId == operation.PendingOperationId))
                ? new(WorldReleaseRuntimePublication.Opened, "the silo opened admission")
                : new(WorldReleaseRuntimePublication.Refused, $"the silo's status after publication is not open admission: {body}");
        }
        public async Task<WorldReleaseRuntimeResult> StopCandidateAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            await KillAsync().ConfigureAwait(continueOnCapturedContext: false);
            return new(true, "the rehearsal's silo was stopped");
        }
        // A first deployment has no source to restore: the rehearsal stops at the failure it reports.
        public Task<WorldReleaseRuntimeResult> RecoverSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) =>
            Task.FromResult(result: new WorldReleaseRuntimeResult(true, "a rehearsal's store is disposable; nothing is restored"));
        public Task<WorldReleaseRuntimeResult> StartRecoveredSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) =>
            Task.FromResult(result: new WorldReleaseRuntimeResult(false, "a rehearsed first deployment has no source to restart"));
        public Task<WorldReleaseRuntimePublicationResult> PublishRecoveredSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) =>
            Task.FromResult(result: new WorldReleaseRuntimePublicationResult(WorldReleaseRuntimePublication.Refused, "a rehearsed first deployment has no source to publish"));

        // Public health must keep answering ready through more than one progress window after admission opened.
        public async Task<string?> ProveSteppingAsync(CancellationToken token) {
            for (var read = 0; (read < 2); read++) {
                if (read > 0) { await Task.Delay(delay: SteppingWindow, timeProvider: clock, cancellationToken: token).ConfigureAwait(continueOnCapturedContext: false); }
                if (m_process!.HasExited) { return $"the silo exited with code {m_process.ExitCode}"; }
                using var response = await m_http.GetAsync(requestUri: Endpoint(path: "/healthz"), cancellationToken: token).ConfigureAwait(continueOnCapturedContext: false);
                var body = await response.Content.ReadAsStringAsync(cancellationToken: token).ConfigureAwait(continueOnCapturedContext: false);

                if (!response.IsSuccessStatusCode) { return $"public health answered {(int)response.StatusCode}: {body}"; }
            }
            return null;
        }
        // The worker's own retirement route: freeze, save and stop, which must end the process with a success code.
        public async Task<string?> DrainAsync(CancellationToken token) {
            using var response = await m_http.PostAsync(requestUri: Endpoint(path: "/drain"), content: null, cancellationToken: token).ConfigureAwait(continueOnCapturedContext: false);

            if (!response.IsSuccessStatusCode) { return $"the drain route answered {(int)response.StatusCode}"; }
            using var exit = new CancellationTokenSource(delay: TimeSpan.FromSeconds(seconds: 120), timeProvider: clock);

            await m_process!.WaitForExitAsync(cancellationToken: exit.Token).ConfigureAwait(continueOnCapturedContext: false);
            await m_pumps!.ConfigureAwait(continueOnCapturedContext: false);
            return (m_process.ExitCode == 0) ? null : $"the silo exited with code {m_process.ExitCode}";
        }
        // Prints the failure's headline, the last private health answer, and the silo's own tagged lines and last lines.
        public void Report(string headline) {
            var lines = File.Exists(path: m_log) ? File.ReadAllLines(path: m_log) : [];

            Console.Error.WriteLine(value: headline);
            Console.Error.WriteLine(value: $"private health: {m_lastHealth}");
            Console.Error.WriteLine(value: "silo (its tagged lines):");
            foreach (var line in lines.Where(predicate: static line => line.Contains(value: "[silo", comparisonType: StringComparison.Ordinal) || line.Contains(value: "[world", comparisonType: StringComparison.Ordinal)).TakeLast(count: 20)) {
                Console.Error.WriteLine(value: line);
            }
            Console.Error.WriteLine(value: "silo (last lines):");
            foreach (var line in lines.TakeLast(count: 15)) { Console.Error.WriteLine(value: line); }
            Console.Error.WriteLine(value: $"silo log: {m_log}");
        }

        private async Task<bool> PrivateHealthAsync(CancellationToken token) {
            try {
                using var response = await m_http.GetAsync(requestUri: Endpoint(path: "/private-healthz"), cancellationToken: token).ConfigureAwait(continueOnCapturedContext: false);

                m_lastHealth = await response.Content.ReadAsStringAsync(cancellationToken: token).ConfigureAwait(continueOnCapturedContext: false);
                return response.IsSuccessStatusCode;
            } catch (HttpRequestException error) {
                m_lastHealth = $"private health did not answer: {error.Message}";
                return false;
            }
        }
        // Writes the managed silo document a worker runs (a release section naming this group and release, every
        // world pinned with its own signing key, the lifecycle listener on loopback) and starts the silo on it.
        private void Start() {
            m_healthPort = FreeLoopbackPort();
            var document = SiloDocument(
                directory: directory,
                documentDirectory: documentDirectory,
                identities: m_identities,
                name: "silo",
                store: Path.Combine(path1: directory, path2: "store")
            );

            document["lifecycle"] = new JsonObject {
                ["healthPort"] = m_healthPort,
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
                ["expectedRelease"] = manifest.Identity,
                ["closedGroupRewind"] = true,
            };
            var siloPath = Path.Combine(path1: directory, path2: "silo.json");

            CliFiles.WriteJson(path: siloPath, value: document);
            string[] arguments = ["--silo", siloPath, .. ((extensionsDirectory is null) ? [] : (string[])["--extensions-dir", Path.GetFullPath(path: extensionsDirectory)])];
            var assembly = silo.EndsWith(value: ".dll", comparisonType: StringComparison.OrdinalIgnoreCase);

            m_process = ChildProcess.StartRedirected(
                arguments: (assembly ? [silo, .. arguments] : arguments),
                fileName: (assembly ? "dotnet" : silo),
                workingDirectory: documentDirectory
            );
            var log = new StreamWriter(path: m_log) { AutoFlush = true };
            var gate = new Lock();

            async Task Copy(StreamReader reader) {
                while (await reader.ReadLineAsync().ConfigureAwait(continueOnCapturedContext: false) is { } line) {
                    lock (gate) { log.WriteLine(value: line); }
                }
            }
            m_pumps = Task.WhenAll(Copy(reader: m_process.StandardOutput), Copy(reader: m_process.StandardError)).ContinueWith(
                continuationAction: _ => log.Dispose(),
                scheduler: TaskScheduler.Default
            );
            m_process.StandardInput.Close();
            Console.WriteLine(value: $"Started the silo (process {m_process.Id}); its lines go to {m_log}");
        }
        private async Task KillAsync() {
            if (m_process is { HasExited: false } running) {
                running.Kill(entireProcessTree: true);
                await running.WaitForExitAsync().ConfigureAwait(continueOnCapturedContext: false);
            }
            if (m_pumps is not null) { await m_pumps.ConfigureAwait(continueOnCapturedContext: false); }
        }
        private static int FreeLoopbackPort() {
            using var listener = new TcpListener(localaddr: IPAddress.Loopback, port: 0);

            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        public async ValueTask DisposeAsync() {
            await KillAsync().ConfigureAwait(continueOnCapturedContext: false);
            m_process?.Dispose();
            m_http.Dispose();
        }
    }
}
