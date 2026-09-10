using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Puck.Cli.Azure;

internal static partial class AzureCommand {
    private static JsonObject SiloDocument(string keyFile, string owner, JsonObject store, string world, JsonNode? lifecycle = null) {
        var result = new JsonObject {
            ["schema"] = "puck.silo.def.v1",
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
    private static async Task DeployWorldAsync(string? group) {
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
            if (!secrets!.AsArray().Any(predicate: value => (Text(value: value) == secret))) {
                using var generated = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
                var encoded = Convert.ToBase64String(inArray: generated.ExportPkcs8PrivateKey());

                CliGitHub.Mask(value: encoded);
                File.WriteAllText(contents: encoded, path: Path.Combine(path1: temporary, path2: "key.txt"));
                await AzAsync("keyvault", "secret", "set", "--vault-name", vault, "--name", secret, "--file", Path.Combine(path1: temporary, path2: "key.txt"), "-o", "none");
            }
            var encodedKey = await AzAsync("keyvault", "secret", "show", "--vault-name", vault, "--name", secret, "--query", "value", "-o", "tsv");

            CliGitHub.Mask(value: encodedKey);
            using var key = ECDsa.Create();

            key.ImportPkcs8PrivateKey(bytesRead: out _, source: Convert.FromBase64String(s: encodedKey));
            File.WriteAllBytes(bytes: key.ExportSubjectPublicKeyInfo(), path: "artifacts/world-silo.public-key");
            var token = await TokenAsync(resource: "https://storage.azure.com/");

            async Task PublishWorldsAsync() {
                foreach (var file in Directory.EnumerateFiles(path: "artifacts/azure/silo-worlds")) {
                    var world = CliFiles.ReadJson(path: file);
                    var name = Path.GetFileName(path: file).Replace(comparisonType: StringComparison.Ordinal, newValue: "", oldValue: ".world.json");

                    if (name == Text(value: configuration["worldName"])) {
                        world["host"]!["authority"] = $"{host}:{port}";
                        world["host"]!["listen"] = $"0.0.0.0:{port}";
                        if (outputs["worldMcpConfiguration"]?["value"]?["admission"] is JsonArray delegated) {
                            var admission = ((world["admission"] as JsonArray) ?? new JsonArray());

                            if (world["admission"] is null) { world["admission"] = admission; }
                            foreach (var participant in delegated) { admission.Add(item: participant!.DeepClone()); }
                        }
                    }
                    var path = Path.Combine(path1: temporary, path2: Path.GetFileName(path: file));

                    CliFiles.WriteJson(path: path, value: world);
                    await SendBlobAsync(container: owner, contentType: "application/json", endpoint: endpoint, file: path, name: $"private/puck/hosted/{name}/definition.json", token: token);
                }
            }
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
            var authentication = configuration["authentication"]!.DeepClone();

            silo["worlds"]![0]!["federation"]!["authentication"] = authentication.DeepClone();
            authentication["settings"]!["remoteKeyHash"] = Convert.ToHexStringLower(inArray: SHA256.HashData(source: key.ExportSubjectPublicKeyInfo()));
            CliFiles.WriteJson(path: "artifacts/world-authentication.json", value: authentication);
            var image = File.ReadAllText(path: "artifacts/world-silo.digest").Trim();

            if (!Regex.IsMatch(input: image, pattern: "\\A[a-z0-9.-]+/[a-z0-9/_-]+@sha256:[a-f0-9]{64}\\z")) { throw new InvalidDataException(message: "World image must be an immutable registry digest."); }
            var script = File.ReadAllText(path: "build/Start-WorldSilo.sh");
            var mcpOptions = outputs["worldMcpConfiguration"]?["value"]?["options"];
            var replacements = new Dictionary<string, string> {
                ["__MCP_ENABLED__"] = ((mcpOptions is null) ? "0" : "1"),
                ["__MCP_DOCUMENT__"] = ((mcpOptions is null) ? "" : Convert.ToBase64String(inArray: Encoding.UTF8.GetBytes(s: mcpOptions.ToJsonString()))),
                ["__MCP_TLS_DOCUMENT__"] = ((mcpOptions is null) ? "" : Convert.ToBase64String(inArray: Encoding.UTF8.GetBytes(s: McpTlsConfiguration(hostname: host).ToJsonString()))),
                ["__MCP_HOST__"] = host,
                ["__MCP_ENTRYPOINT__"] = ((mcpOptions is null) ? "" : "--entrypoint dotnet"),
                ["__MCP_ARGUMENTS__"] = ((mcpOptions is null) ? "--silo /configuration/silo.json" : "/puck-cli/Puck.Cli.dll mcp --silo /configuration/silo.json --http /configuration/mcp.json"),
                ["__SILO_DOCUMENT__"] = Convert.ToBase64String(inArray: Encoding.UTF8.GetBytes(s: silo.ToJsonString())),
                ["__FEDERATION_KEY__"] = encodedKey,
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
            var parameterPath = Path.Combine(path1: temporary, path2: "parameters.json");

            WriteParameters(path: parameterPath, values: parameters);
            using var releaseBuffer = new MemoryStream();

            using (var gzip = new GZipStream(compressionLevel: CompressionLevel.SmallestSize, leaveOpen: true, stream: releaseBuffer)) {
                var saved = new JsonObject { ["parameters"] = parameters.DeepClone(), ["publicKey"] = Convert.ToBase64String(inArray: key.ExportSubjectPublicKeyInfo()) };

                await gzip.WriteAsync(buffer: Encoding.UTF8.GetBytes(s: saved.ToJsonString()));
            }
            var state = Convert.ToBase64String(inArray: releaseBuffer.ToArray());

            if (Encoding.UTF8.GetByteCount(s: state) > 25000) { throw new InvalidDataException(message: "Compressed release state exceeds the vault secret limit."); }
            var statePath = Path.Combine(path1: temporary, path2: "release-state.txt");

            File.WriteAllText(contents: state, path: statePath);
            var stateSecret = Text(value: configuration["releaseStateSecretName"]);
            JsonNode? previousRelease = null;

            if (secrets!.AsArray().Any(predicate: value => (Text(value: value) == stateSecret))) {
                var encodedState = await AzAsync("keyvault", "secret", "show", "--vault-name", vault, "--name", stateSecret, "--query", "value", "-o", "tsv");

                CliGitHub.Mask(value: encodedState);
                using var compressed = new MemoryStream(buffer: Convert.FromBase64String(s: encodedState));
                using var gzip = new GZipStream(mode: CompressionMode.Decompress, stream: compressed);

                previousRelease = await JsonNode.ParseAsync(utf8Json: gzip);
            }
            if ((workers.Length != 0) && (previousRelease is null)) { throw new InvalidOperationException(message: "The existing worker needs its known-good release state adopted into the configured vault secret before transactional deployment."); }
            var snapshot = new JsonArray();

            if (workers.Length != 0) {
                await WorldGuestAsync(group: group, script: $"curl --fail --silent --show-error --max-time {siloLifecycle["shutdownSeconds"]} -X POST http://127.0.0.1:{siloLifecycle["healthPort"]}/drain\nsystemctl stop puck-world.service", worker: Text(value: workers[0]!["name"]));
            }
            try { snapshot = await SnapshotWorldStoreAsync(endpoint: endpoint, owner: owner, token: token); } catch {
                if (workers.Length != 0) { await WorldGuestAsync(group: group, script: "systemctl start puck-world.service", worker: Text(value: workers[0]!["name"])); }
                throw;
            }
            CliFiles.WriteJson(path: "artifacts/world-rollback.json", value: new JsonObject { ["endpoint"] = endpoint, ["owner"] = owner, ["blobs"] = snapshot.DeepClone() });
            try {
                await PublishWorldsAsync();
                await ApplyWorldComputeAsync(group: group, parameters: parameterPath, scaleSet: scaleSet);
                await RetryAsync(action: async () => { await PuckAsync("world", "probe", host, port, "artifacts/world-silo.public-key"); }, attempts: 6, seconds: 5);
                await TestWorldReleaseAsync(group: group, image: null, scaleSet: null);
                await AzAsync("keyvault", "secret", "set", "--vault-name", vault, "--name", stateSecret, "--file", statePath, "-o", "none");
            } catch (Exception failure) {
                Console.Error.WriteLine(value: $"World deployment failed; restoring pre-release persistence and the last verified worker, when present: {failure.Message}");
                foreach (var worker in await WorkersAsync(group: group, scaleSet: scaleSet)) {
                    await WorldGuestAsync(group: group, script: "if systemctl cat puck-world.service >/dev/null 2>&1; then systemctl stop puck-world.service; fi\nif docker inspect puck-world >/dev/null 2>&1; then docker rm -f puck-world; fi", worker: Text(value: worker!["name"]));
                }
                await RestoreWorldStoreAsync(endpoint: endpoint, owner: owner, snapshot: snapshot, temporary: temporary, token: await TokenAsync(resource: "https://storage.azure.com/"));
                if (previousRelease is null) {
                    await AzAsync("vmss", "scale", "-g", group, "--name", scaleSet, "--new-capacity", "0", "-o", "none");
                    throw new InvalidOperationException(innerException: failure, message: "The first world release failed; pre-release persistence was restored and the scale set is empty for a clean retry.");
                }
                WriteParameters(path: parameterPath, values: previousRelease["parameters"]!.AsObject());
                await ApplyWorldComputeAsync(group: group, parameters: parameterPath, scaleSet: scaleSet);
                var previousKey = Path.Combine(path1: temporary, path2: "previous.public-key");

                File.WriteAllBytes(bytes: Convert.FromBase64String(s: Text(value: previousRelease["publicKey"])), path: previousKey);
                var previousConfiguration = previousRelease["parameters"]!["configuration"]!;
                var previousHost = $"{previousConfiguration["dns"]!["recordName"]}.{previousConfiguration["dns"]!["zoneName"]}";

                await RetryAsync(action: async () => { await PuckAsync("world", "probe", previousHost, Text(value: previousConfiguration["port"]), previousKey); }, attempts: 12, seconds: 5);
                throw new InvalidOperationException(innerException: failure, message: "World release failed; the previous release and its persistence versions were restored.");
            }
        } finally {
            // This freshly generated path is never derived from arguments or deployment documents.
            Directory.Delete(path: temporary, recursive: true);
        }
    }
    private static async Task ApplyWorldComputeAsync(string group, string parameters, string scaleSet) {
        const string CompiledCompute = "artifacts/production-world-compute.json";

        if (CliGitHub.IsActions && !File.Exists(path: CompiledCompute)) { throw new FileNotFoundException(message: "CI world deployment requires its compiled compute template."); }
        await AzAsync("deployment", "group", "create", "-g", group, "--name", (scaleSet + "-application"), "--template-file", (File.Exists(path: CompiledCompute) ? CompiledCompute : "src/Puck.Azure.Resources/worldSiloCompute.bicep"), "--parameters", ("@" + parameters), "-o", "none");
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
    private static bool MutableWorldBlob(string name) =>
        ((name.StartsWith(comparisonType: StringComparison.Ordinal, value: "puck/hosted/") || name.StartsWith(comparisonType: StringComparison.Ordinal, value: "private/puck/hosted/")) &&
        !name.EndsWith(comparisonType: StringComparison.Ordinal, value: ".pckp"));
    private static async Task<string[]> WorldBlobNamesAsync(string endpoint, string owner) {
        var blobs = await AzJsonAsync("storage", "blob", "list", "--account-name", new Uri(uriString: endpoint).Host.Split(separator: '.')[0],
            "--container-name", owner, "--auth-mode", "login", "--num-results", "*", "--query", "[].name", "-o", "json");

        return blobs.AsArray().Select(selector: Text).Where(predicate: MutableWorldBlob).ToArray();
    }
    private static HttpRequestMessage BlobRequest(string container, string endpoint, HttpMethod method, string name, string token, string? version = null) {
        var uri = $"{endpoint.TrimEnd(trimChar: '/')}/{Uri.EscapeDataString(stringToEscape: container)}/{string.Join(separator: '/', values: name.Split(separator: '/').Select(selector: Uri.EscapeDataString))}";

        if (version is not null) { uri += ("?versionid=" + Uri.EscapeDataString(stringToEscape: version)); }
        var request = new HttpRequestMessage(method: method, requestUri: uri);

        request.Headers.Authorization = new AuthenticationHeaderValue(parameter: token, scheme: "Bearer");
        request.Headers.Add(name: "x-ms-version", value: "2025-11-05");
        request.Headers.Add(name: "x-ms-date", value: DateTime.UtcNow.ToString(format: "R", provider: CultureInfo.InvariantCulture));
        return request;
    }
    private static async Task SendBlobAsync(string container, string contentType, string endpoint, string file, string name, string token) {
        await RetryAsync(action: async () => {
            using var request = BlobRequest(container: container, endpoint: endpoint, method: HttpMethod.Put, name: name, token: token);
            using var stream = File.OpenRead(path: file);

            request.Headers.Add(name: "x-ms-blob-type", value: "BlockBlob");
            request.Headers.Add(name: "x-ms-blob-cache-control", value: "no-cache");
            request.Headers.Add(name: "x-ms-meta-sha256", value: Convert.ToHexStringLower(inArray: SHA256.HashData(source: stream)));
            stream.Position = 0;
            request.Content = new StreamContent(content: stream);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType: contentType);
            using var response = await Http.SendAsync(request: request);

            response.EnsureSuccessStatusCode();
        }, attempts: 4);
    }
    private static async Task<JsonArray> SnapshotWorldStoreAsync(string endpoint, string owner, string token) {
        var snapshot = new JsonArray();

        foreach (var name in await WorldBlobNamesAsync(endpoint: endpoint, owner: owner)) {
            using var request = BlobRequest(container: owner, endpoint: endpoint, method: HttpMethod.Head, name: name, token: token);
            using var response = await Http.SendAsync(request: request);

            response.EnsureSuccessStatusCode();
            if (!response.Headers.TryGetValues(name: "x-ms-version-id", values: out var versions)) { throw new InvalidDataException(message: "World rollback requires Blob versioning before deployment."); }
            snapshot.Add(item: ((JsonNode)new JsonObject { ["name"] = name, ["version"] = versions.Single() }));
        }
        return snapshot;
    }
    private static async Task RestoreWorldStoreAsync(string endpoint, string owner, JsonArray snapshot, string temporary, string token) {
        var names = snapshot.Select(selector: row => Text(value: row!["name"])).ToHashSet(comparer: StringComparer.Ordinal);

        foreach (var name in await WorldBlobNamesAsync(endpoint: endpoint, owner: owner)) {
            if (names.Contains(item: name)) { continue; }
            using var request = BlobRequest(container: owner, endpoint: endpoint, method: HttpMethod.Delete, name: name, token: token);
            using var response = await Http.SendAsync(request: request);

            response.EnsureSuccessStatusCode();
        }
        foreach (var row in snapshot) {
            var name = Text(value: row!["name"]);

            if (!MutableWorldBlob(name: name)) { throw new InvalidDataException(message: "Rollback manifest contains a blob outside hosted-world mutable state."); }
            using var request = BlobRequest(container: owner, endpoint: endpoint, method: HttpMethod.Get, name: name, token: token, version: Text(value: row["version"]));
            using var response = await Http.SendAsync(request: request);

            response.EnsureSuccessStatusCode();
            var file = Path.Combine(path1: temporary, path2: "restore-blob");

            await File.WriteAllBytesAsync(bytes: await response.Content.ReadAsByteArrayAsync(), path: file);
            await SendBlobAsync(container: owner, contentType: (response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream"), endpoint: endpoint, file: file, name: name, token: token);
        }
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
