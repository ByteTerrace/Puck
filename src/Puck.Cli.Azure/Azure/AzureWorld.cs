using Puck.Abstractions;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Puck.Cli.Automation;
using Puck.World.Server;

namespace Puck.Cli.Azure;

public static partial class AzureCommand {
    /// <summary>The address a deployed world host binds its QUIC door and its health listener to: every interface. The
    /// load balancer probes the health port and forwards QUIC to the VM's own address, which a loopback bind does not
    /// reach. Only these deployment documents name it; a local World or silo listens on loopback.</summary>
    public const string DeploymentListenAddress = "0.0.0.0";
    /// <summary>The asset package of the primary world the release prepares and the silo hosts, repository-relative.
    /// The host release composes the same package (<c>puck world host build</c>), whose manifest's entry is
    /// <see cref="HostedWorldName"/>.</summary>
    public const string HostedWorldSource = "worlds/debug";
    /// <summary>The document name of the entry of <see cref="HostedWorldSource"/>: the world the silo hosts as its
    /// primary, the official build's composed root, and the deployment's configured <c>worldName</c>
    /// (<c>src/Puck.Azure.Resources/main.bicepparam</c>).</summary>
    public const string HostedWorldName = "debug";

    /// <summary>Gets this checkout's copy of the asset layout a host release ships at
    /// <see cref="WorldHostLayout.WorldsDirectory"/>, which the release copies from <see cref="HostedWorldSource"/>: the directory a
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
    /// <summary>The key prefix, inside the world owner's private container, under which the MCP front's TLS state for
    /// one hostname sits: <c>state.json</c> (Caddy's certificate, key and ACME account) and <c>lock</c> (the blob whose
    /// lease grants issuance). The worker's identity already owns that container; the TLS state keeper
    /// (<c>puck world host tls</c>) reads and writes both.</summary>
    private static string McpTlsStatePrefix(string hostname) =>
        $"{WorldOwnedWorldSync.HostedPrivateNamespace}/tls/{hostname.ToLowerInvariant()}/";
    private static JsonNode McpTlsConfiguration(string hostname) {
        // Stock Caddy terminates only the known host and renews over the LB's existing 443 -> 8443 rule. Its
        // account/certificate state stays outside the application and release documents, and the TLS state keeper
        // carries it between workers through the owner's private store.
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
    /// <summary>The mounted file name of the deployment issuer's root key, which vouches for every signed-in player's
    /// own key; it reaches the worker beside the worlds' signing keys and signs as no world.</summary>
    private const string WorldIssuerKeyFile = "issuer.pk8";
    /// <summary>The deployment vault secret holding the issuer's root key, generated on the first release that finds it
    /// absent. Rotating it re-vouches every player at their next sign-in and changes no world document.</summary>
    private const string WorldIssuerKeySecret = "puck-world-issuer-root";
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
            keys[WorldIssuerKeyFile] = await LoadSigningKeyAsync(
                vault,
                WorldIssuerKeySecret,
                secrets!.AsArray(),
                temporary
            ).ConfigureAwait(continueOnCapturedContext: false);
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
            // A player's sign-in configuration: the provider the deployment's issuer checks, and the primary world's
            // transport key, the one the client presents its credential to and joins.
            var authentication = configuration["authentication"]!.DeepClone().AsObject();

            authentication["remoteKeyHash"] = ContentDigest.HexOf(content: key.ExportSubjectPublicKeyInfo());
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
                    install: HostRelease(outputs: outputs),
                    publicKey: Convert.ToBase64String(inArray: key.ExportSubjectPublicKeyInfo()),
                    sshPublicKey: ssh,
                    computeTemplate: CliFiles.ReadJson(path: templatePath).AsObject()
                ),
                Inputs: "",
                Keys: keys
            );
        }
    }
    /// <summary>The server channel's tree as the deploy pushed it, signed: the host release a deployment installs.</summary>
    private const string HostReleaseTree = "artifacts/source/azure-server-channel";
    /// <summary>Reads the signed host release this deploy pushed (<see cref="HostReleaseTree"/>), addressed at the
    /// deployment's public release channels, which its machines install from.</summary>
    /// <param name="outputs">The deployment's outputs.</param>
    /// <returns>The release.</returns>
    private static WorldHostRelease HostRelease(JsonNode outputs) => WorldHostRelease.FromTree(
        source: $"{Text(value: Value(key: "officialContentBaseUrl", outputs: outputs)).TrimEnd('/')}/releases",
        tree: HostReleaseTree
    );
    /// <summary>Renders a release's deployment inputs from the deployment's outputs: the hosting machine's typed inputs
    /// and the signed host release it installs. The inputs name each hosted world's signing key by its credential file and
    /// hold no key material: the keys reach the machine only through the protected bootstrap
    /// (<see cref="WorldReleaseBootstrapCommand"/>).</summary>
    /// <param name="outputs">The deployment's outputs.</param>
    /// <param name="manifest">The release the inputs deploy; its inventory belongs to the configured owner and holds the
    /// configured primary world, and its engine is <paramref name="install"/>'s.</param>
    /// <param name="install">The signed host release the machine installs.</param>
    /// <param name="publicKey">The base64 SubjectPublicKeyInfo of the primary world's federation key.</param>
    /// <param name="sshPublicKey">The worker's SSH public key.</param>
    /// <param name="computeTemplate">The compiled compute template.</param>
    /// <returns>The deployment inputs.</returns>
    /// <exception cref="InvalidDataException">The inventory does not belong to the configured owner or lacks its
    /// primary world, or the release's engine is not the host release's.</exception>
    public static WorldReleaseDeploymentConfiguration RenderWorldReleaseDeployment(JsonNode outputs, WorldReleaseManifest manifest, WorldHostRelease install,
        string publicKey, string sshPublicKey, JsonObject computeTemplate) {
        ArgumentNullException.ThrowIfNull(argument: manifest);
        ArgumentNullException.ThrowIfNull(argument: install);
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
        var host = $"{configuration["dns"]!["recordName"]}.{configuration["dns"]!["zoneName"]}";
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

        if (manifest.Engine != install.Engine) { throw new InvalidDataException(message: "release manifest and host release name different engines"); }
        if (siloLifecycle["azureScheduledEvents"]!.GetValue<bool>()) {
            lifecycle["observer"] = new JsonObject {
                ["type"] = "azure.scheduled-events",
                ["settings"] = new JsonObject { ["pollSeconds"] = siloLifecycle["pollSeconds"]!.DeepClone() },
            };
        }
        // A worker the deploy deletes after it fails takes its journal with it; its logs and narration also reach the
        // deployment's Application Insights, which the silo identity publishes to.
        lifecycle["telemetry"] = new JsonObject {
            ["type"] = "azure.monitor",
            ["settings"] = new JsonObject {
                ["connectionString"] = Text(value: Value(
                    key: "worldSiloTelemetryConnectionString",
                    outputs: outputs
                )),
            },
        };
        // Key files are credential names: the machine's bootstrap resolves them into the silo unit's credentials.
        var silo = SiloDocument(
            documentDirectory: WorldHostLayout.WorldsDirectory,
            keyFile: "federation.pk8",
            lifecycle: lifecycle,
            owner: owner,
            store: WorldStoreSelection(endpoint: endpoint),
            world: primaryWorld
        );
        var worlds = silo["worlds"]!.AsArray();

        silo["doors"]!["budget"] = manifest.Definitions.Count;
        var prototype = worlds[0]!.DeepClone();

        if (!manifest.Definitions.ContainsKey(key: primaryIdentity)) { throw new InvalidDataException(message: "release inventory is missing the configured primary world"); }
        worlds.Clear();
        // One issuer for the silo: every row trusts its root, and players sign in through the deployment's provider.
        silo["issuer"] = new JsonObject {
            ["keyFile"] = WorldIssuerKeyFile,
            ["signIn"] = configuration["authentication"]!.DeepClone(),
        };

        foreach (var identity in manifest.Definitions.Keys.Order(comparer: StringComparer.Ordinal)) {
            var parts = identity.Split('/');

            if (
                (parts.Length != 2) ||
                (parts[0] != owner)
            ) { throw new InvalidDataException(message: "release inventory does not belong to the configured owner"); }
            var row = prototype.DeepClone();

            row["world"] = parts[1];
            row["federation"]!["keyFile"] = WorldReleaseKeyFile(
                identity: identity,
                primaryIdentity: primaryIdentity
            );
            worlds.Add(item: row);
        }
        silo["release"] = new JsonObject {
            ["group"] = scaleSet,
            ["owner"] = owner,
            ["expectedRelease"] = manifest.Identity,
            ["closedGroupRewind"] = true,
        };
        var mcpOptions = outputs["worldMcpConfiguration"]?["value"]?["options"];
        var inputs = new WorldHostInputs(
            Cpu: (int)configuration["service"]!["cpu"]!,
            Engine: install.Engine,
            Guard: new WorldReleaseGuestGuardRequest(BootRelease: manifest.Identity, Group: scaleSet, Owner: Guid.Parse(input: owner)),
            HealthPort: (int)siloLifecycle["healthPort"]!,
            Mcp: ((mcpOptions is null)
                ? null
                : new WorldHostMcp(Host: host, Options: mcpOptions.DeepClone().AsObject(), Tls: McpTlsConfiguration(hostname: host).AsObject(), TlsState: McpTlsStatePrefix(hostname: host))),
            MemoryInGB: (int)configuration["service"]!["memoryInGB"]!,
            QuicPort: (int)configuration["port"]!,
            ReadySeconds: WorldReleaseReadySeconds(lifecycle: siloLifecycle, rows: manifest.Definitions.Count),
            Release: manifest.Identity,
            ShutdownSeconds: (int)siloLifecycle["shutdownSeconds"]!,
            Silo: silo
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
            ["release"] = install.Engine,
            ["sshPublicKey"] = sshPublicKey,
        };

        return new WorldReleaseDeploymentConfiguration(
            manifest.Identity,
            scaleSet,
            parameters,
            publicKey,
            computeTemplate,
            inputs,
            install
        ) { ClosedGroupRewind = true };
    }
    // The owner's store as the silo and the machine's verbs name it: the provider's blob store at the deployment's
    // storage endpoint, authenticated by the machine's ambient identity.
    private static JsonObject WorldStoreSelection(string endpoint) => new() { ["type"] = "azure.blob", ["settings"] = new JsonObject { ["accountUrl"] = endpoint } };
    /// <summary>The seconds a booting worker's bootstrap waits for private readiness. The silo activates its rows in
    /// series, each bounded by a first checkpoint and a progress step, so the deadline is that bound per row plus the
    /// host's own start.</summary>
    private static int WorldReleaseReadySeconds(JsonNode lifecycle, int rows) =>
        (WorldHostStartSeconds + (rows * (lifecycle["checkpointTimeoutSeconds"]!.GetValue<int>() + lifecycle["progressTimeoutSeconds"]!.GetValue<int>())));
    // The allowance for the host to start and open its health listener before any row activates.
    private const int WorldHostStartSeconds = 120;
    /// <summary>Builds the protected settings the worker's VM extension runs for a retained deployment. The extension
    /// downloads the host release's installer by its content pin into its own directory (the platform's file fetch, so the
    /// machine needs no download tool), the command checks it against that pin, installs exactly the release's signed
    /// manifest through it, and hands the protected bootstrap (the inputs' pin and the signing keys) to the installed CLI's
    /// <c>world host install</c> on standard input, so the keys appear in no process's arguments or environment.</summary>
    /// <param name="deployment">The retained deployment.</param>
    /// <returns>The extension's protected settings: <c>fileUris</c> and <c>commandToExecute</c>.</returns>
    public static JsonObject WorldReleaseBootstrapCommand(WorldReleaseRetainedDeployment deployment) {
        ArgumentNullException.ThrowIfNull(argument: deployment);
        var install = deployment.Configuration.Install;
        var inputs = deployment.Configuration.Host;
        var bootstrap = new WorldHostBootstrap(
            Environment: new Dictionary<string, string>(comparer: StringComparer.Ordinal) { ["AZURE_CLIENT_ID"] = Text(value: Value(key: "worldSiloClientId", outputs: Outputs())) },
            Inputs: deployment.Inputs,
            Keys: new SortedDictionary<string, string>(dictionary: deployment.Keys.ToDictionary(), comparer: StringComparer.Ordinal),
            Owner: inputs.Guard.Owner,
            Store: WorldStoreSelection(endpoint: Text(value: inputs.Silo["store"]!["settings"]!["accountUrl"]))
        );
        var installer = install.InstallerFileName;
        var arguments = string.Join(separator: ' ', values: install.InstallerArguments(installRoot: WorldHostLayout.InstallRoot).Select(selector: static argument => $"'{argument}'"));

        return new JsonObject {
            ["fileUris"] = new JsonArray(install.InstallerAddress()),
            ["commandToExecute"] = $"echo '{installer}  {installer}' | sha256sum -c - && chmod 0700 '{installer}' && './{installer}' {arguments} && echo '{bootstrap.Encode()}' | '{WorldHostLayout.InstallRoot}/Puck.Launcher.Stub' world host install",
        };
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
            await DeleteWorldComputeAsync(
                clock: clock,
                group: group,
                scaleSet: scaleSet
            );
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
    /// <summary>Deletes the world's workers and then its scale set, when it exists, and proves no worker remains. The
    /// world's state lives in blob storage, so the scale set is disposable: the compute template recreates it.</summary>
    /// <remarks>Callers reach this only when no release runs on the scale set: after its source drained, or in a group
    /// that has never committed a release. Azure refuses to delete a Flexible scale set that still holds a VM, and the
    /// scale set's autoscale floor can add one back between the two deletes, so a refused scale-set delete is retried
    /// after its workers are deleted again. A worker is force-deleted: its guest holds nothing to shut down gracefully,
    /// and a graceful delete spends minutes on a guest shutdown no caller needs.</remarks>
    private static async Task DeleteWorldComputeAsync(string group, string scaleSet, TimeProvider clock) {
        for (var attempt = 1; ; attempt++) {
            foreach (var worker in await StableWorkersAsync(
                clock: clock,
                group: group,
                scaleSet: scaleSet
            )) {
                await AzAsync(
                    "vm",
                    "delete",
                    "-g",
                    group,
                    "--name",
                    Text(value: worker!["name"]),
                    "--force-deletion",
                    "true",
                    "--yes",
                    "-o",
                    "none"
                );
            }
            if ((await AzJsonAsync(
                "vmss",
                "list",
                "-g",
                group,
                "--query",
                $"[?name=='{scaleSet}'].name",
                "-o",
                "json"
            )).AsArray().Count == 0) { break; }
            try {
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
                break;
            } catch (InvalidOperationException) when (attempt < WorldComputeDeleteAttempts) { }
        }
        if ((await StableWorkersAsync(
            clock: clock,
            group: group,
            scaleSet: scaleSet
        )).Length != 0) { throw new InvalidOperationException(message: $"Deleting {scaleSet} left workers behind; no release was applied."); }
    }
    private const int WorldComputeDeleteAttempts = 3;
    private static async Task WorldGuestAsync(string group, string script, string worker) {
        var marker = ("puck-operation-" + Guid.NewGuid().ToString(format: "N"));
        var output = await WorldGuestOutputAsync(
            contents: $"set -eu\n{script}\nprintf '%s\\n' '{marker}'\n",
            group: group,
            worker: worker
        );

        if (!output.Contains(
            comparisonType: StringComparison.Ordinal,
            value: marker
        )) {
            throw new InvalidOperationException(message: $"World guest operation on {worker} did not confirm completion: {output.Trim()}");
        }
    }
    // Asks the worker's lifecycle listener for one health route on loopback. --fail-with-body keeps the reason a withheld
    // readiness writes, so a failure names it.
    private static Task WorldHealthAsync(string group, string worker, int port, string route) => WorldGuestAsync(
        group: group,
        script: $"curl --fail-with-body --silent --show-error --max-time 30 http://127.0.0.1:{port}{route}",
        worker: worker
    );
    /// <summary>Runs a shell script on a world worker through the VM agent and returns its combined output. The agent
    /// reports success whatever the script's exit status, so a caller proves completion by a marker the script prints
    /// last and, when it is missing, reports this output: it carries the script's stderr, the failure's cause.</summary>
    private static async Task<string> WorldGuestOutputAsync(string group, string worker, string contents) {
        var path = Path.Combine(
            path1: Path.GetTempPath(),
            path2: (("puck-release-guest-" + Guid.NewGuid().ToString(format: "N")) + ".sh")
        );

        try {
            File.WriteAllText(
                contents: contents,
                path: path
            );
            var response = await AzJsonAsync(
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
            ).ConfigureAwait(continueOnCapturedContext: false);

            return string.Join(
                separator: "\n",
                values: response["value"]!.AsArray().Select(selector: row => Text(value: row?["message"]))
            );
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
