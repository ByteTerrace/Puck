using Puck.Abstractions;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Puck.Cli.Automation;
using Puck.World.Server;

namespace Puck.Cli.Azure;

public static partial class AzureCommand {
    /// <summary>The address a deployed world container binds its QUIC door and its health listener to: every interface.
    /// The load balancer probes the health port and forwards QUIC to the VM's own address, and Docker forwards a
    /// published port to the container's interface, so neither reaches a loopback bind. Only these deployment documents
    /// name it; a local World or silo listens on loopback.</summary>
    public const string DeploymentListenAddress = "0.0.0.0";
    /// <summary>The asset package of the primary world the release prepares and the silo hosts, repository-relative.
    /// The silo image prepares the same package (<c>src/Puck.World.Silo/Dockerfile</c>), whose manifest's entry is
    /// <see cref="HostedWorldName"/>.</summary>
    public const string HostedWorldSource = "worlds/debug";
    /// <summary>The document name of the entry of <see cref="HostedWorldSource"/>: the world the silo hosts as its
    /// primary, the official build's composed root, and the deployment's configured <c>worldName</c>
    /// (<c>src/Puck.Azure.Resources/main.bicepparam</c>).</summary>
    public const string HostedWorldName = "debug";

    /// <summary>Gets this checkout's copy of the asset layout the world-silo image ships at
    /// <see cref="WorldSiloImageLayout.WorldsDirectory"/>, which the image copies from <see cref="HostedWorldSource"/>: the directory a
    /// step on this machine validates the composed documents beside, so their relative assets resolve against the files
    /// the deployed silo resolves them against. Resolved against the current directory, the checkout's root.</summary>
    public static string LocalWorldsDirectory => Puck.Abstractions.PuckPaths.Normalize(path: Path.GetFullPath(path: HostedWorldSource));

    private static JsonObject SiloDocument(string keyFile, string owner, JsonObject store, string world, JsonNode? lifecycle = null, string? documentDirectory = null) {
        var result = new JsonObject {
            ["schema"] = Puck.World.WorldSiloDefinition.SchemaVersion,
            ["worlds"] = new JsonArray(new JsonObject { ["owner"] = owner, ["world"] = world, ["pinned"] = true, ["federation"] = new JsonObject { ["keyFile"] = keyFile } }),
            ["doors"] = new JsonObject { ["budget"] = 1 },
            ["store"] = store,
            ["stateDir"] = "/state",
            ["clustering"] = new JsonObject { ["kind"] = "Localhost" },
        };

        if (lifecycle is not null) { result["lifecycle"] = lifecycle.DeepClone(); }
        if (documentDirectory is not null) { result["documentDirectory"] = documentDirectory; }
        return result;
    }
    private static JsonNode McpTlsConfiguration(string hostname) {
        // Stock Caddy terminates only the known host, renews over the LB's existing 443 -> 8443 rule,
        // and keeps its account/certificate state outside the application and release documents.
        if (
            (Uri.CheckHostName(name: hostname) != UriHostNameType.Dns) ||
            hostname.Any(predicate: c => (!char.IsAsciiLetterOrDigit(c: c) && (c is not ('.' or '-'))))
        ) {
            throw new InvalidDataException(message: "MCP requires a public DNS hostname.");
        }
        return JsonNode.Parse($$$$"""
            {
              "admin":{"disabled":true},
              "apps":{
                "http":{"https_port":8443,"servers":{"mcp":{
                  "listen":[":8443"],"protocols":["h1","h2"],
                  "automatic_https":{"disable_redirects":true},
                  "read_header_timeout":"10s","idle_timeout":"120s",
                  "max_header_bytes":16384,
                  "routes":[{"match":[{"host":["{{{{hostname}}}}"]}],"handle":[{
                    "handler":"reverse_proxy","flush_interval":-1,
                    "headers":{"request":{"delete":["ClientAuthorization"]}},
                    "upstreams":[{"dial":"127.0.0.1:8082"}]
                  }]}]
                }}},
                "tls":{"automation":{"policies":[{"subjects":["{{{{hostname}}}}"],"issuers":[{
                  "module":"acme","ca":"https://acme-v02.api.letsencrypt.org/directory",
                  "challenges":{"http":{"disabled":true},"tls-alpn":{"alternate_port":8443}}
                }]}]}}
              }
            }
            """)!;
    }
    /// <summary>The mounted file name of a hosted world's federation signing key: the configured primary world's is
    /// <c>federation.pk8</c>, every other world's is named by its identity's digest.</summary>
    private static string WorldReleaseKeyFile(string identity, string primaryIdentity) => ((identity == primaryIdentity)
        ? "federation.pk8"
        : (("federation-" + ContentDigest.HexOfText(text: identity)) + ".pk8")
    );
    private static async Task<WorldReleaseRetainedDeployment> PrepareWorldReleaseDeploymentAsync(WorldReleaseManifest manifest, string? group, TimeProvider clock) {
        var outputs = Outputs();
        var configuration = Value(
            key: "worldSiloConfiguration",
            outputs: outputs
        );

        group ??= Text(value: Value(
            key: "deploymentResourceGroupName",
            outputs: outputs
        ));
        var owner = Text(value: Value(
            key: "worldSiloOwner",
            outputs: outputs
        ));
        var vault = Text(value: Value(
            key: "deploymentKeyVaultName",
            outputs: outputs
        ));
        var primaryIdentity = ((owner + "/") + Text(value: configuration["worldName"]));
        var scaleSet = Text(value: configuration["name"]);
        JsonNode? secrets = null;

        await RetryAsync(action: async () => {
            secrets = await AzJsonAsync(
                "keyvault",
                "secret",
                "list",
                "--vault-name",
                vault,
                "--query",
                "[].name",
                "-o",
                "json"
            );
        }, clock: clock);
        // The silo's scratch holds the signing key, so it is deleted however the step ends.
        using (var scratch = RunDirectory.Create(
            keepOnFailure: false,
            prefix: "puck-silo-"
        )) {
            var temporary = scratch.Path;
            var keys = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

            foreach (var identity in manifest.Definitions.Keys.Order(comparer: StringComparer.Ordinal)) {
                keys[WorldReleaseKeyFile(
                    identity: identity,
                    primaryIdentity: primaryIdentity
                )] = await LoadSigningKeyAsync(
                    vault,
                    ((identity == primaryIdentity)
                    ? Text(value: configuration["federationKeySecretName"])
                    : ("puck-world-key-" + ContentDigest.HexOfText(text: identity))),
                    secrets!.AsArray(),
                    temporary
                ).ConfigureAwait(continueOnCapturedContext: false);
            }
            if (!keys.TryGetValue(
                key: "federation.pk8",
                value: out var encodedKey
            )) { throw new InvalidDataException(message: "release inventory is missing the configured primary world"); }
            using var key = ECDsa.Create();

            key.ImportPkcs8PrivateKey(
                bytesRead: out _,
                source: Convert.FromBase64String(s: encodedKey)
            );
            File.WriteAllBytes(
                bytes: key.ExportSubjectPublicKeyInfo(),
                path: "artifacts/world-silo.public-key"
            );
            var authentication = configuration["authentication"]!.DeepClone();

            authentication["settings"]!["remoteKeyHash"] = ContentDigest.HexOf(content: key.ExportSubjectPublicKeyInfo());
            CliFiles.WriteJson(
                path: "artifacts/world-authentication.json",
                value: authentication
            );
            var workers = await StableWorkersAsync(
                clock: clock,
                group: group,
                scaleSet: scaleSet
            );

            if (workers.Length > 1) { throw new InvalidOperationException(message: "This release requires one authoritative worker; reconcile existing placement before deployment."); }
            string ssh;

            if (workers.Length == 1) { ssh = Text(value: workers[0]!["osProfile"]!["linuxConfiguration"]!["ssh"]!["publicKeys"]![0]!["keyData"]); } else {
                await RunAsync(
                    arguments: ["-q", "-t", "ed25519", "-N", "", "-f", Path.Combine(
                            path1: temporary,
                            path2: "ssh"
                        )],
                    executable: "ssh-keygen"
                );
                ssh = File.ReadAllText(path: Path.Combine(
                    path1: temporary,
                    path2: "ssh.pub"
                )).Trim();
            }
            var templatePath = "artifacts/production-world-compute.json";

            if (!File.Exists(path: templatePath)) {
                if (CliGitHub.IsActions) { throw new FileNotFoundException(message: "CI release preparation requires its compiled compute template"); }
                templatePath = Path.Combine(
                    path1: temporary,
                    path2: "compute-template.json"
                );
                await AzAsync(
                    "bicep",
                    "build",
                    "--file",
                    "src/Puck.Azure.Resources/worldSiloCompute.bicep",
                    "--outfile",
                    templatePath
                );
            }
            return new(
                Configuration: RenderWorldReleaseDeployment(
                    outputs: outputs,
                    manifest: manifest,
                    image: File.ReadAllText(path: "artifacts/world-silo.digest").Trim(),
                    publicKey: Convert.ToBase64String(inArray: key.ExportSubjectPublicKeyInfo()),
                    sshPublicKey: ssh,
                    computeTemplate: CliFiles.ReadJson(path: templatePath).AsObject(),
                    bootstrapScript: File.ReadAllText(path: "build/Start-WorldSilo.sh"),
                    guardScript: File.ReadAllBytes(path: "build/Guard-WorldRelease.py"),
                    launcherScript: File.ReadAllText(path: "build/Boot-WorldSilo.py")
                ),
                Inputs: "",
                Keys: keys
            );
        }
    }
    /// <summary>Renders a release's deployment inputs from the deployment's outputs and the checkout's bootstrap
    /// files. The inputs name each hosted world's signing key by its mounted file and hold no key material: the keys
    /// reach the worker only through the protected bootstrap command (<see cref="WorldReleaseBootstrapCommand"/>).</summary>
    /// <param name="outputs">The deployment's outputs.</param>
    /// <param name="manifest">The release the inputs deploy; its inventory belongs to the configured owner and holds the
    /// configured primary world.</param>
    /// <param name="image">The immutable registry reference of the world-silo image; it ends with the manifest's
    /// engine image digest.</param>
    /// <param name="publicKey">The base64 SubjectPublicKeyInfo of the primary world's federation key.</param>
    /// <param name="sshPublicKey">The worker's SSH public key.</param>
    /// <param name="computeTemplate">The compiled compute template.</param>
    /// <param name="bootstrapScript">The text of <c>build/Start-WorldSilo.sh</c>.</param>
    /// <param name="guardScript">The bytes of <c>build/Guard-WorldRelease.py</c>.</param>
    /// <param name="launcherScript">The text of <c>build/Boot-WorldSilo.py</c>.</param>
    /// <returns>The deployment inputs.</returns>
    /// <exception cref="InvalidDataException">The inventory does not belong to the configured owner or lacks its
    /// primary world, the image is not the manifest's immutable digest, or a bootstrap placeholder stays unresolved.</exception>
    public static WorldReleaseDeploymentConfiguration RenderWorldReleaseDeployment(JsonNode outputs, WorldReleaseManifest manifest, string image,
        string publicKey, string sshPublicKey, JsonObject computeTemplate, string bootstrapScript, byte[] guardScript, string launcherScript) {
        var configuration = Value(
            key: "worldSiloConfiguration",
            outputs: outputs
        );
        var owner = Text(value: Value(
            key: "worldSiloOwner",
            outputs: outputs
        ));
        var endpoint = Text(value: Value(
            key: "worldSiloStorageEndpoint",
            outputs: outputs
        )).TrimEnd(trimChar: '/');
        var clientId = Text(value: Value(
            key: "worldSiloClientId",
            outputs: outputs
        ));
        var host = $"{configuration["dns"]!["recordName"]}.{configuration["dns"]!["zoneName"]}";
        var port = Text(value: configuration["port"]);
        var scaleSet = Text(value: configuration["name"]);
        var primaryWorld = Text(value: configuration["worldName"]);
        var primaryIdentity = ((owner + "/") + primaryWorld);
        var siloLifecycle = (configuration["lifecycle"] ?? throw new InvalidDataException(message: "Missing world lifecycle configuration."));
        var lifecycle = new JsonObject {
            ["healthPort"] = siloLifecycle["healthPort"]!.DeepClone(),
            ["healthAddress"] = DeploymentListenAddress,
            ["shutdownSeconds"] = siloLifecycle["shutdownSeconds"]!.DeepClone(),
            ["progressTimeoutSeconds"] = siloLifecycle["progressTimeoutSeconds"]!.DeepClone(),
            ["checkpointTimeoutSeconds"] = siloLifecycle["checkpointTimeoutSeconds"]!.DeepClone(),
            ["journalTimeoutSeconds"] = siloLifecycle["journalTimeoutSeconds"]!.DeepClone(),
            ["journalBacklogLimit"] = siloLifecycle["journalBacklogLimit"]!.DeepClone(),
        };

        if (siloLifecycle["azureScheduledEvents"]!.GetValue<bool>()) {
            lifecycle["observer"] = new JsonObject {
                ["type"] = "azure.scheduled-events",
                ["settings"] = new JsonObject { ["pollSeconds"] = siloLifecycle["pollSeconds"]!.DeepClone() },
            };
        }
        var silo = SiloDocument(
            documentDirectory: WorldSiloImageLayout.WorldsDirectory,
            keyFile: "/configuration/federation.pk8",
            lifecycle: lifecycle,
            owner: owner,
            store: new JsonObject { ["type"] = "azure.blob", ["settings"] = new JsonObject { ["accountUrl"] = endpoint } },
            world: primaryWorld
        );
        var worlds = silo["worlds"]!.AsArray();

        silo["doors"]!["budget"] = manifest.Definitions.Count;
        var prototype = worlds[0]!.DeepClone();

        if (!manifest.Definitions.ContainsKey(key: primaryIdentity)) { throw new InvalidDataException(message: "release inventory is missing the configured primary world"); }
        worlds.Clear();
        var authentication = configuration["authentication"]!;

        foreach (var identity in manifest.Definitions.Keys.Order(comparer: StringComparer.Ordinal)) {
            var parts = identity.Split('/');

            if (
                (parts.Length != 2) ||
                (parts[0] != owner)
            ) { throw new InvalidDataException(message: "release inventory does not belong to the configured owner"); }
            var row = prototype.DeepClone();

            row["world"] = parts[1];
            row["federation"]!["keyFile"] = ("/configuration/" + WorldReleaseKeyFile(
                identity: identity,
                primaryIdentity: primaryIdentity
            ));
            row["federation"]!["authentication"] = authentication.DeepClone();
            worlds.Add(item: row);
        }
        silo["release"] = new JsonObject {
            ["group"] = scaleSet,
            ["owner"] = owner,
            ["expectedRelease"] = manifest.Identity,
            ["closedGroupRewind"] = true,
        };
        if (!Regex.IsMatch(
            input: image,
            pattern: "\\A[a-z0-9.-]+/[a-z0-9/_-]+@sha256:[a-f0-9]{64}\\z"
        )) { throw new InvalidDataException(message: "World image must be an immutable registry digest."); }
        if (!image.EndsWith(
            ("@" + manifest.EngineImageDigest),
            StringComparison.Ordinal
        )) { throw new InvalidDataException(message: "release manifest and published image differ"); }
        var bootGuard = WorldReleaseGuestGuard(
            request: new JsonObject {
                ["endpoint"] = endpoint,
                ["owner"] = owner,
                ["group"] = scaleSet,
                ["clientId"] = clientId,
                ["bootRelease"] = manifest.Identity,
            },
            script: guardScript
        );
        var mcpOptions = outputs["worldMcpConfiguration"]?["value"]?["options"];
        var bootstrap = Resolve(
            text: bootstrapScript,
            replacements: new Dictionary<string, string> {
                ["__RELEASE_GUARD__"] = bootGuard,
                ["__RELEASE_ID__"] = manifest.Identity,
                ["__MCP_ENABLED__"] = ((mcpOptions is null)
                ? "0"
                : "1"),
                ["__MCP_DOCUMENT__"] = ((mcpOptions is null)
                ? ""
                : Convert.ToBase64String(inArray: Encoding.UTF8.GetBytes(s: mcpOptions.ToJsonString()))),
                ["__MCP_TLS_DOCUMENT__"] = ((mcpOptions is null)
                ? ""
                : Convert.ToBase64String(inArray: Encoding.UTF8.GetBytes(s: McpTlsConfiguration(hostname: host).ToJsonString()))),
                ["__MCP_HOST__"] = host,
                ["__MCP_ENTRYPOINT__"] = ((mcpOptions is null)
                ? ""
                : "--entrypoint dotnet"),
                ["__MCP_ARGUMENTS__"] = ((mcpOptions is null)
                ? "--silo /configuration/silo.json"
                : $"{WorldSiloImageLayout.CliAssembly} mcp --silo /configuration/silo.json --http /configuration/mcp.json"),
                ["__SILO_DOCUMENT__"] = Convert.ToBase64String(inArray: Encoding.UTF8.GetBytes(s: silo.ToJsonString())),
                ["__CLIENT_ID__"] = clientId,
                ["__REGISTRY__"] = image.Split(separator: '/')[0],
                ["__IMAGE__"] = image,
                ["__HEALTH_PORT__"] = Text(value: siloLifecycle["healthPort"]),
                ["__QUIC_PORT__"] = port,
                ["__SHUTDOWN_SECONDS__"] = Text(value: siloLifecycle["shutdownSeconds"]),
                ["__STOP_SECONDS__"] = (((int)siloLifecycle["shutdownSeconds"]!) + 10).ToString(provider: CultureInfo.InvariantCulture),
                ["__READY_SECONDS__"] = WorldReleaseReadySeconds(
                    lifecycle: siloLifecycle,
                    rows: manifest.Definitions.Count
                ).ToString(provider: CultureInfo.InvariantCulture),
                ["__CPU__"] = Text(value: configuration["container"]!["cpu"]),
                ["__MEMORY__"] = Text(value: configuration["container"]!["memoryInGB"]),
            }
        );
        var launcher = Resolve(
            text: launcherScript,
            replacements: new Dictionary<string, string> {
                ["__CONTENT_URL__"] = $"{endpoint}/{owner}/{WorldReleaseArchive.ContentKeyPrefix}",
                ["__CLIENT_ID__"] = clientId,
            }
        );
        var parameters = new JsonObject {
            ["configuration"] = configuration.DeepClone(),
            ["location"] = Value(
            key: "deploymentLocation",
            outputs: outputs
        ).DeepClone(),
            ["tags"] = Value(
            key: "deploymentTags",
            outputs: outputs
        ).DeepClone(),
            ["identityResourceId"] = Value(
            key: "worldSiloIdentityResourceId",
            outputs: outputs
        ).DeepClone(),
            ["mcpEnabled"] = (mcpOptions is not null),
            ["release"] = image,
            ["sshPublicKey"] = sshPublicKey,
        };

        return new WorldReleaseDeploymentConfiguration(
            manifest.Identity,
            scaleSet,
            parameters,
            publicKey,
            computeTemplate,
            bootstrap,
            launcher
        ) { ClosedGroupRewind = true };

        static string Resolve(string text, Dictionary<string, string> replacements) {
            foreach (var replacement in replacements) {
                text = text.Replace(
                    comparisonType: StringComparison.Ordinal,
                    newValue: replacement.Value,
                    oldValue: replacement.Key
                );
            }
            if (Regex.IsMatch(
                input: text,
                pattern: "__[A-Z_]+__"
            )) { throw new InvalidDataException(message: "Unresolved VM bootstrap placeholder."); }
            return text;
        }
    }
    /// <summary>The seconds a booting worker's bootstrap waits for private readiness. The silo activates its rows in
    /// series, each bounded by a first checkpoint and a progress step, so the deadline is that bound per row plus the
    /// container's own start.</summary>
    private static int WorldReleaseReadySeconds(JsonNode lifecycle, int rows) =>
        (WorldContainerStartSeconds + (rows * (lifecycle["checkpointTimeoutSeconds"]!.GetValue<int>() + lifecycle["progressTimeoutSeconds"]!.GetValue<int>())));
    // The allowance for the container to start and open its health listener before any row activates.
    private const int WorldContainerStartSeconds = 120;
    /// <summary>Builds the protected command the worker's VM extension runs for a retained deployment: it carries the
    /// launcher, the pin of the retained inputs and the signing keys, and nothing else. The launcher reads the inputs
    /// from the release content store with the worker's identity and refuses bytes that do not match the pin.</summary>
    /// <param name="deployment">The retained deployment.</param>
    /// <returns>The shell command for the extension's protected settings.</returns>
    public static string WorldReleaseBootstrapCommand(WorldReleaseRetainedDeployment deployment) {
        ArgumentNullException.ThrowIfNull(argument: deployment);
        var request = new JsonObject {
            ["inputs"] = deployment.Inputs,
            ["keys"] = new JsonObject(deployment.Keys.OrderBy(
                keySelector: pair => pair.Key,
                comparer: StringComparer.Ordinal
            ).Select(selector: pair => KeyValuePair.Create<string, JsonNode?>(
                key: pair.Key,
                value: pair.Value
            ))),
        };

        return $"PUCK_RELEASE_INPUTS='{Convert.ToBase64String(inArray: Encoding.UTF8.GetBytes(s: request.ToJsonString()))}' python3 -c 'import base64,sys; exec(base64.b64decode(sys.argv[1], validate=True))' '{Convert.ToBase64String(inArray: Encoding.UTF8.GetBytes(s: deployment.Configuration.Launcher))}'";
    }
    /// <summary>Deploys the world compute template, first recreating the scale set when its priority differs from the declared <c>Regular</c> or <c>Spot</c>.</summary>
    /// <remarks>Callers reach this only after the source worker has drained and stopped.</remarks>
    private static async Task ApplyWorldComputeAsync(string group, string parameters, string scaleSet, string priority, TimeProvider clock, string? retainedTemplate = null) {
        const string CompiledCompute = "artifacts/production-world-compute.json";

        if (
            (retainedTemplate is null) &&
            CliGitHub.IsActions &&
            !File.Exists(path: CompiledCompute)
        ) { throw new FileNotFoundException(message: "CI world deployment requires its compiled compute template."); }
        var existing = (await AzJsonAsync(
            "vmss",
            "list",
            "-g",
            group,
            "--query",
            $"[?name=='{scaleSet}'].virtualMachineProfile.priority",
            "-o",
            "json"
        )).AsArray();

        // Azure refuses to change priority in place. Drained state lives in blob storage, so the scale set is disposable.
        if (
            (existing.Count == 1) &&
            !string.Equals(
                a: Text(value: existing[0]),
                b: priority,
                comparisonType: StringComparison.OrdinalIgnoreCase
            )
        ) {
            Console.WriteLine(value: $"Recreating {scaleSet} to change its priority from {Text(value: existing[0])} to {priority}.");
            await AzAsync(
                "vmss",
                "delete",
                "-g",
                group,
                "--name",
                scaleSet,
                "-o",
                "none"
            );
            if ((await StableWorkersAsync(
                clock: clock,
                group: group,
                scaleSet: scaleSet
            )).Length != 0) { throw new InvalidOperationException(message: "Deleting the scale set left workers behind; no release was applied."); }
        }
        await AzAsync(
            "deployment",
            "group",
            "create",
            "-g",
            group,
            "--name",
            (scaleSet + "-application"),
            "--template-file",
            (retainedTemplate ?? (File.Exists(path: CompiledCompute)
            ? CompiledCompute
            : "src/Puck.Azure.Resources/worldSiloCompute.bicep")),
            "--parameters",
            ("@" + parameters),
            "-o",
            "none"
        );
        await AzAsync(
            "vmss",
            "update-instances",
            "-g",
            group,
            "--name",
            scaleSet,
            "--instance-ids",
            "*",
            "-o",
            "none"
        );
    }
    private static async Task WorldGuestAsync(string group, string script, string worker) {
        var marker = ("puck-operation-" + Guid.NewGuid().ToString(format: "N"));
        var path = Path.Combine(
            path1: Path.GetTempPath(),
            path2: (marker + ".sh")
        );

        try {
            File.WriteAllText(
                contents: $"set -eu\n{script}\nprintf '%s\\n' '{marker}'\n",
                path: path
            );
            var result = await AzJsonAsync(
                "vm",
                "run-command",
                "invoke",
                "-g",
                group,
                "-n",
                worker,
                "--command-id",
                "RunShellScript",
                "--scripts",
                ("@" + path),
                "-o",
                "json"
            );

            if (!result["value"]!.AsArray().Any(predicate: row => Text(value: row?["message"]).Contains(
                comparisonType: StringComparison.Ordinal,
                value: marker
            ))) {
                throw new InvalidOperationException(message: "World guest operation did not confirm completion.");
            }
        } finally { File.Delete(path: path); }
    }
    /// <summary>Returns the Azure priority a world compute configuration declares.</summary>
    /// <returns><c>Spot</c> when <c>compute.spot</c> is present; otherwise <c>Regular</c>.</returns>
    private static string WorldPriority(JsonNode compute) => ((compute["spot"] is null)
        ? "Regular"
        : "Spot"
    );
    private static async Task<JsonNode?[]> WorkersAsync(string group, string scaleSet) {
        var workers = await AzJsonAsync(
            "vm",
            "list",
            "-g",
            group,
            "-o",
            "json"
        );

        return workers.AsArray().Where(predicate: worker => (((string?)worker?["virtualMachineScaleSet"]?["id"])?.EndsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: ("/" + scaleSet)
        ) == true)).ToArray();
    }
    // Waits, on clock, for Azure's own worker replacement to settle, polling every WorkerSettlePoll and refusing after
    // WorkerSettleTimeout.
    private static async Task<JsonNode?[]> StableWorkersAsync(string group, string scaleSet, TimeProvider clock) {
        var started = clock.GetTimestamp();
        var announced = false;

        while (true) {
            var workers = await WorkersAsync(
                group: group,
                scaleSet: scaleSet
            );
            var transitioning = workers.Any(predicate: worker => (((string?)worker?["provisioningState"]) is "Creating" or "Updating" or "Deleting"));

            if (!transitioning) { return workers; }
            if (clock.GetElapsedTime(startingTimestamp: started) >= WorkerSettleTimeout) {
                throw new InvalidOperationException(message: "Azure worker replacement did not settle within ten minutes; no release was applied.");
            }
            if (!announced) { Console.WriteLine(value: "Waiting for Azure's existing worker replacement to settle before selecting the authoritative worker."); announced = true; }
            await Task.Delay(
                delay: WorkerSettlePoll,
                timeProvider: clock
            );
        }
    }

    private static readonly TimeSpan WorkerSettlePoll = TimeSpan.FromSeconds(seconds: 5);
    private static readonly TimeSpan WorkerSettleTimeout = TimeSpan.FromMinutes(minutes: 10);
}
