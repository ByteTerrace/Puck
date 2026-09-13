using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Puck.World.Server;

namespace Puck.Cli.Azure;

internal static partial class AzureCommand {
    private static JsonObject SiloDocument(string keyFile, string owner, JsonObject store, string world, JsonNode? lifecycle = null) {
        var result = new JsonObject {
            ["schema"] = "puck.silo.configuration.v1",
            ["worlds"] = new JsonArray(new JsonObject { ["owner"] = owner, ["world"] = world, ["pinned"] = true, ["federation"] = new JsonObject { ["keyFile"] = keyFile } }),
            ["doors"] = new JsonObject { ["budget"] = 1 },
            ["store"] = store,
            ["stateDir"] = "/state",
            ["clustering"] = new JsonObject { ["kind"] = "Localhost" },
        };

        if (lifecycle is not null) { result["lifecycle"] = lifecycle.DeepClone(); }
        return result;
    }
    private static JsonNode McpTlsConfiguration(string hostname) {
        // Stock Caddy terminates only the known host, renews over the LB's existing 443 -> 8443 rule,
        // and keeps its account/certificate state outside the application and release documents.
        if ((Uri.CheckHostName(name: hostname) != UriHostNameType.Dns) || hostname.Any(predicate: c => (!char.IsAsciiLetterOrDigit(c: c) && (c is not ('.' or '-'))))) {
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
    private static async Task<WorldReleaseDeploymentConfiguration> PrepareWorldReleaseDeploymentAsync(WorldReleaseManifest manifest, string? group) {
        var outputs = Outputs();
        var configuration = Value(key: "worldSiloConfiguration", outputs: outputs);

        group ??= Text(value: Value(key: "deploymentResourceGroupName", outputs: outputs));
        var owner = Text(value: Value(key: "worldSiloOwner", outputs: outputs));
        var endpoint = Text(value: Value(key: "worldSiloStorageEndpoint", outputs: outputs)).TrimEnd(trimChar: '/');
        var vault = Text(value: Value(key: "deploymentKeyVaultName", outputs: outputs));
        var secret = Text(value: configuration["federationKeySecretName"]);
        var host = $"{configuration["dns"]!["recordName"]}.{configuration["dns"]!["zoneName"]}";
        var port = Text(value: configuration["port"]);
        var scaleSet = Text(value: configuration["name"]);
        JsonNode? secrets = null;

        await RetryAsync(action: async () => { secrets = await AzJsonAsync("keyvault", "secret", "list", "--vault-name", vault, "--query", "[].name", "-o", "json"); });
        var temporary = Path.Combine(path1: Path.GetTempPath(), path2: $"puck-silo-{Guid.NewGuid():N}");

        Directory.CreateDirectory(path: temporary);
        try {
            var encodedKey = await LoadWorldSigningKeyAsync(vault, secret, secrets!.AsArray(), temporary).ConfigureAwait(false);
            using var key = ECDsa.Create();

            key.ImportPkcs8PrivateKey(bytesRead: out _, source: Convert.FromBase64String(s: encodedKey));
            File.WriteAllBytes(bytes: key.ExportSubjectPublicKeyInfo(), path: "artifacts/world-silo.public-key");
            var siloLifecycle = (configuration["lifecycle"] ?? throw new InvalidDataException(message: "Missing world lifecycle configuration."));
            var lifecycle = new JsonObject {
                ["healthPort"] = siloLifecycle["healthPort"]!.DeepClone(),
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
            var silo = SiloDocument(keyFile: "/configuration/federation.pk8", lifecycle: lifecycle, owner: owner, store: new JsonObject { ["type"] = "azure.blob", ["settings"] = new JsonObject { ["accountUrl"] = endpoint } }, world: Text(value: configuration["worldName"]));
            var worlds = silo["worlds"]!.AsArray();
            silo["doors"]!["budget"] = manifest.Definitions.Count;
            var prototype = worlds[0]!.DeepClone();
            var keyFiles = new JsonObject { ["federation.pk8"] = encodedKey };
            var primaryWorld = Text(configuration["worldName"]);
            if (!manifest.Definitions.ContainsKey(owner + "/" + primaryWorld)) { throw new InvalidDataException("release inventory is missing the configured primary world"); }
            worlds.Clear();
            foreach (var identity in manifest.Definitions.Keys.Order(StringComparer.Ordinal)) {
                var parts = identity.Split('/');
                if (parts.Length != 2 || parts[0] != owner) { throw new InvalidDataException("release inventory does not belong to the configured owner"); }
                var row = prototype.DeepClone();
                row["world"] = parts[1];
                if (parts[1] != primaryWorld) {
                    var identityHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
                    var filename = "federation-" + identityHash + ".pk8";
                    keyFiles[filename] = await LoadWorldSigningKeyAsync(vault, "puck-world-key-" + identityHash, secrets.AsArray(), temporary).ConfigureAwait(false);
                    row["federation"]!["keyFile"] = "/configuration/" + filename;
                }
                worlds.Add(row);
            }
            silo["release"] = new JsonObject { ["group"] = scaleSet, ["owner"] = owner, ["expectedRelease"] = manifest.Identity };
            var authentication = configuration["authentication"]!.DeepClone();

            foreach (var row in worlds) { row!["federation"]!["authentication"] = authentication.DeepClone(); }
            authentication["settings"]!["remoteKeyHash"] = Convert.ToHexStringLower(inArray: SHA256.HashData(source: key.ExportSubjectPublicKeyInfo()));
            CliFiles.WriteJson(path: "artifacts/world-authentication.json", value: authentication);
            var image = File.ReadAllText(path: "artifacts/world-silo.digest").Trim();

            if (!Regex.IsMatch(input: image, pattern: "\\A[a-z0-9.-]+/[a-z0-9/_-]+@sha256:[a-f0-9]{64}\\z")) { throw new InvalidDataException(message: "World image must be an immutable registry digest."); }
            if (!image.EndsWith("@" + manifest.EngineImageDigest, StringComparison.Ordinal)) { throw new InvalidDataException("release manifest and published image differ"); }
            var script = File.ReadAllText(path: "build/Start-WorldSilo.sh");
            var bootGuard = WorldReleaseGuestGuard(new JsonObject {
                ["endpoint"] = endpoint, ["owner"] = owner, ["group"] = scaleSet,
                ["clientId"] = Text(Value(outputs, "worldSiloClientId")), ["bootRelease"] = manifest.Identity,
            });
            var mcpOptions = outputs["worldMcpConfiguration"]?["value"]?["options"];
            var replacements = new Dictionary<string, string> {
                ["__RELEASE_GUARD__"] = bootGuard,
                ["__RELEASE_ID__"] = manifest.Identity,
                ["__MCP_ENABLED__"] = ((mcpOptions is null) ? "0" : "1"),
                ["__MCP_DOCUMENT__"] = ((mcpOptions is null) ? "" : Convert.ToBase64String(inArray: Encoding.UTF8.GetBytes(s: mcpOptions.ToJsonString()))),
                ["__MCP_TLS_DOCUMENT__"] = ((mcpOptions is null) ? "" : Convert.ToBase64String(inArray: Encoding.UTF8.GetBytes(s: McpTlsConfiguration(hostname: host).ToJsonString()))),
                ["__MCP_HOST__"] = host,
                ["__MCP_ENTRYPOINT__"] = ((mcpOptions is null) ? "" : "--entrypoint dotnet"),
                ["__MCP_ARGUMENTS__"] = ((mcpOptions is null) ? "--silo /configuration/silo.json" : "/puck-cli/Puck.Cli.dll mcp --silo /configuration/silo.json --http /configuration/mcp.json"),
                ["__SILO_DOCUMENT__"] = Convert.ToBase64String(inArray: Encoding.UTF8.GetBytes(s: silo.ToJsonString())),
                ["__FEDERATION_KEYS__"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(keyFiles.ToJsonString())),
                ["__CLIENT_ID__"] = Text(value: Value(key: "worldSiloClientId", outputs: outputs)),
                ["__REGISTRY__"] = image.Split(separator: '/')[0],
                ["__IMAGE__"] = image,
                ["__HEALTH_PORT__"] = Text(value: siloLifecycle["healthPort"]),
                ["__QUIC_PORT__"] = port,
                ["__SHUTDOWN_SECONDS__"] = Text(value: siloLifecycle["shutdownSeconds"]),
                ["__STOP_SECONDS__"] = (((int)siloLifecycle["shutdownSeconds"]!) + 10).ToString(provider: CultureInfo.InvariantCulture),
                ["__CPU__"] = Text(value: configuration["container"]!["cpu"]),
                ["__MEMORY__"] = Text(value: configuration["container"]!["memoryInGB"]),
            };

            foreach (var replacement in replacements) { script = script.Replace(comparisonType: StringComparison.Ordinal, newValue: replacement.Value, oldValue: replacement.Key); }
            if (Regex.IsMatch(input: script, pattern: "__[A-Z_]+__")) { throw new InvalidDataException(message: "Unresolved VM bootstrap placeholder."); }
            var workers = await StableWorkersAsync(group: group, scaleSet: scaleSet);

            if (workers.Length > 1) { throw new InvalidOperationException(message: "This release requires one authoritative worker; reconcile existing placement before deployment."); }
            string ssh;

            if (workers.Length == 1) { ssh = Text(value: workers[0]!["osProfile"]!["linuxConfiguration"]!["ssh"]!["publicKeys"]![0]!["keyData"]); } else {
                await RunAsync(arguments: ["-q", "-t", "ed25519", "-N", "", "-f", Path.Combine(path1: temporary, path2: "ssh")], executable: "ssh-keygen");
                ssh = File.ReadAllText(path: Path.Combine(path1: temporary, path2: "ssh.pub")).Trim();
            }
            var command = $"printf '%s' '{Convert.ToBase64String(inArray: Encoding.UTF8.GetBytes(s: script))}' | base64 -d | bash";

            CliGitHub.Mask(value: command);
            var parameters = new JsonObject {
                ["configuration"] = configuration.DeepClone(),
                ["location"] = Value(key: "deploymentLocation", outputs: outputs).DeepClone(),
                ["tags"] = Value(key: "deploymentTags", outputs: outputs).DeepClone(),
                ["identityResourceId"] = Value(key: "worldSiloIdentityResourceId", outputs: outputs).DeepClone(),
                ["bootstrapCommand"] = command,
                ["mcpEnabled"] = (mcpOptions is not null),
                ["release"] = image,
                ["sshPublicKey"] = ssh,
            };
            var templatePath = "artifacts/production-world-compute.json";
            if (!File.Exists(templatePath)) {
                if (CliGitHub.IsActions) { throw new FileNotFoundException("CI release preparation requires its compiled compute template"); }
                templatePath = Path.Combine(temporary, "compute-template.json");
                await AzAsync("bicep", "build", "--file", "src/Puck.Azure.Resources/worldSiloCompute.bicep", "--outfile", templatePath);
            }
            return new WorldReleaseDeploymentConfiguration(manifest.Identity, scaleSet, parameters,
                Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), CliFiles.ReadJson(templatePath).AsObject());
        } finally {
            // This freshly generated path is never derived from arguments or deployment documents.
            Directory.Delete(path: temporary, recursive: true);
        }
    }
    private static async Task ApplyWorldComputeAsync(string group, string parameters, string scaleSet, string? retainedTemplate = null) {
        const string CompiledCompute = "artifacts/production-world-compute.json";

        if (retainedTemplate is null && CliGitHub.IsActions && !File.Exists(path: CompiledCompute)) { throw new FileNotFoundException(message: "CI world deployment requires its compiled compute template."); }
        await AzAsync("deployment", "group", "create", "-g", group, "--name", (scaleSet + "-application"), "--template-file", retainedTemplate ?? (File.Exists(path: CompiledCompute) ? CompiledCompute : "src/Puck.Azure.Resources/worldSiloCompute.bicep"), "--parameters", ("@" + parameters), "-o", "none");
        await AzAsync("vmss", "update-instances", "-g", group, "--name", scaleSet, "--instance-ids", "*", "-o", "none");
    }
    private static async Task WorldGuestAsync(string group, string script, string worker) {
        var marker = ("puck-operation-" + Guid.NewGuid().ToString(format: "N"));
        var path = Path.Combine(path1: Path.GetTempPath(), path2: (marker + ".sh"));

        try {
            File.WriteAllText(contents: $"set -eu\n{script}\nprintf '%s\\n' '{marker}'\n", path: path);
            var result = await AzJsonAsync("vm", "run-command", "invoke", "-g", group, "-n", worker, "--command-id", "RunShellScript", "--scripts", ("@" + path), "-o", "json");

            if (!result["value"]!.AsArray().Any(predicate: row => Text(value: row?["message"]).Contains(comparisonType: StringComparison.Ordinal, value: marker))) {
                throw new InvalidOperationException(message: "World guest operation did not confirm completion.");
            }
        } finally { File.Delete(path: path); }
    }
    private static async Task<JsonNode?[]> WorkersAsync(string group, string scaleSet) {
        var workers = await AzJsonAsync("vm", "list", "-g", group, "-o", "json");

        return workers.AsArray().Where(predicate: worker => (((string?)worker?["virtualMachineScaleSet"]?["id"])?.EndsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: ("/" + scaleSet)) == true)).ToArray();
    }
    private static async Task<JsonNode?[]> StableWorkersAsync(string group, string scaleSet) {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var announced = false;

        while (true) {
            var workers = await WorkersAsync(group: group, scaleSet: scaleSet);
            var transitioning = workers.Any(predicate: worker => (((string?)worker?["provisioningState"]) is "Creating" or "Updating" or "Deleting"));

            if (!transitioning) { return workers; }
            if (System.Diagnostics.Stopwatch.GetElapsedTime(startingTimestamp: started) >= TimeSpan.FromMinutes(minutes: 10)) {
                throw new InvalidOperationException(message: "Azure worker replacement did not settle within ten minutes; no release was applied.");
            }
            if (!announced) { Console.WriteLine(value: "Waiting for Azure's existing worker replacement to settle before selecting the authoritative worker."); announced = true; }
            await Task.Delay(delay: TimeSpan.FromSeconds(seconds: 5));
        }
    }
}
