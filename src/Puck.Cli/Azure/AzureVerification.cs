using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Puck.Cli.Azure;

internal static partial class AzureCommand {
    private static async Task TestWorldReleaseAsync(string? group, string? image, string? scaleSet) {
        var outputs = (((group is not null) && (scaleSet is not null)) ? null : Outputs());

        group ??= Text(value: Value(key: "deploymentResourceGroupName", outputs: outputs!));
        scaleSet ??= Text(value: Value(key: "worldSiloConfiguration", outputs: outputs!)["name"]);
        image ??= File.ReadAllText(path: "artifacts/world-silo.digest").Trim();
        var workers = await WorkersAsync(group: group, scaleSet: scaleSet);

        if (workers.Length != 1) { throw new InvalidOperationException(message: "This release requires exactly one authoritative world worker."); }
        if (outputs is not null) {
            var expected = Value(key: "worldSiloConfiguration", outputs: outputs)["compute"]!["imageReference"]!;
            var actual = workers[0]!["storageProfile"]!["imageReference"]!;

            foreach (var field in new[] { "publisher", "offer", "sku", "version" }) {
                if (!Text(value: expected[field]).Equals(comparisonType: StringComparison.OrdinalIgnoreCase, value: Text(value: actual[field]))) {
                    throw new InvalidDataException(message: $"World worker OS image {field} differs from the declared release. Drain and replace the worker before completing the host image change.");
                }
            }
        }
        var worker = Text(value: workers[0]!["name"]);
        var result = await AzJsonAsync("vm", "run-command", "invoke", "-g", group, "-n", worker, "--command-id", "RunShellScript", "--scripts", "cat /etc/puck/release; docker inspect --format '{{.Config.Image}} {{.State.Running}}' puck-world", "-o", "json");
        var message = string.Join(separator: "\n", values: result["value"]!.AsArray().Select(selector: value => Text(value: value!["message"])));

        if (!message.Contains(comparisonType: StringComparison.Ordinal, value: (image + " true")) || !message.Contains(comparisonType: StringComparison.Ordinal, value: $"\n{image}\n")) { throw new InvalidDataException(message: "The running world container does not match the requested release digest."); }
        await RetryAsync(action: async () => {
            var view = await AzJsonAsync("vm", "get-instance-view", "-g", group, "-n", worker, "-o", "json");
            var state = Text(value: view["instanceView"]?["vmHealth"]?["status"]?["code"]);

            if (!state.Equals(comparisonType: StringComparison.OrdinalIgnoreCase, value: "HealthState/healthy")) {
                throw new InvalidDataException(message: $"Azure reports world application health '{state}'; the release requires Healthy.");
            }
        }, attempts: 12, seconds: 5);
        if (outputs?["worldMcpConfiguration"]?["value"]?["options"] is { } mcp) {
            await RetryAsync(action: async () => {
                using var deadline = new CancellationTokenSource(delay: TimeSpan.FromSeconds(seconds: 30));
                using var response = await Http.GetAsync(cancellationToken: deadline.Token, requestUri: new Uri(baseUri: new Uri(uriString: Text(value: mcp["publicUrl"])), relativeUri: "/healthz"));

                response.EnsureSuccessStatusCode();
            }, attempts: 6, seconds: 5);
        }
    }
    private static async Task TestWorldMcpAsync() {
        const string Protocol = "2026-07-28";
        var options = Value(key: "worldMcpConfiguration", outputs: Outputs())["options"]!;
        var address = new Uri(uriString: Text(value: options["publicUrl"]));
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var client = new HttpClient(handler: handler) { Timeout = TimeSpan.FromSeconds(seconds: 125) };

        using (var health = await client.GetAsync(requestUri: new Uri(baseUri: address, relativeUri: "/healthz"))) { health.EnsureSuccessStatusCode(); }
        var metadata = JsonNode.Parse(json: await client.GetStringAsync(requestUri: new Uri(baseUri: address, relativeUri: "/.well-known/oauth-protected-resource/mcp")))!;

        if ((Text(value: metadata["resource"]) != address.AbsoluteUri) || !metadata["scopes_supported"]!.AsArray().Any(predicate: scope => (Text(value: scope) == Text(value: options["authorizationScope"])))) {
            throw new InvalidDataException(message: "Protected-resource discovery differs from deployment policy.");
        }
        var assertion = await TokenAsync(resource: ("api://" + Text(value: options["audience"])));
        var sequence = 0;

        async Task<JsonNode> RequestAsync(string method, string? tool = null, JsonObject? arguments = null, bool authenticated = true) {
            var parameters = new JsonObject {
                ["_meta"] = new JsonObject {
                    ["io.modelcontextprotocol/protocolVersion"] = Protocol,
                    ["io.modelcontextprotocol/clientInfo"] = new JsonObject { ["name"] = "puck-production-verification", ["version"] = "1" },
                    ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject(),
                },
            };

            if (tool is not null) { parameters["name"] = tool; parameters["arguments"] = (arguments ?? new JsonObject()); }
            using var request = new HttpRequestMessage(method: HttpMethod.Post, requestUri: address);

            if (authenticated) { request.Headers.Authorization = new(parameter: assertion, scheme: "Bearer"); }
            request.Headers.Accept.ParseAdd(input: "application/json, text/event-stream");
            request.Headers.Add(name: "MCP-Protocol-Version", value: Protocol);
            request.Headers.Add(name: "Mcp-Method", value: method);
            if (tool is not null) { request.Headers.Add(name: "Mcp-Name", value: tool); }
            request.Content = new StringContent(content: new JsonObject { ["jsonrpc"] = "2.0", ["id"] = ++sequence, ["method"] = method, ["params"] = parameters }.ToJsonString(), encoding: Encoding.UTF8, mediaType: "application/json");
            using var response = await client.SendAsync(request: request);

            if (!authenticated) {
                if ((response.StatusCode != HttpStatusCode.Unauthorized) || !response.Headers.WwwAuthenticate.Any()) { throw new InvalidDataException(message: "Anonymous MCP access was not challenged."); }
                return new JsonObject();
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized) { throw new InvalidOperationException(message: "Live delegated authorization needs fresh sign-in, consent or claims satisfaction; credentials were not printed or substituted."); }
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync();

            if (body.Length > (1024 * 1024)) { throw new InvalidDataException(message: "MCP verification response exceeded its budget."); }
            var reply = ((response.Content.Headers.ContentType?.MediaType == "text/event-stream")
                ? body.Split(separator: '\n').Where(predicate: line => line.StartsWith(comparisonType: StringComparison.Ordinal, value: "data:")).Select(selector: line => JsonNode.Parse(json: line[5..])).Last(predicate: node => (node?["id"]?.GetValue<int>() == sequence))!
                : JsonNode.Parse(json: body)!);

            if ((reply["error"] is not null) || (reply["result"] is null)) { throw new InvalidDataException(message: "Live MCP returned a protocol error."); }
            var result = reply["result"]!;

            if (result["isError"]?.GetValue<bool>() == true) {
                throw new InvalidDataException(message: $"Live MCP {tool} was refused: {result["content"]?[0]?["text"]}");
            }
            return result;
        }
        await RequestAsync(authenticated: false, method: "tools/list");
        var tools = (await RequestAsync(method: "tools/list"))["tools"]!.AsArray();

        if (tools.Any(predicate: tool => (Text(value: tool!["name"]) == "puck_capture_frame"))) { throw new InvalidDataException(message: "The headless silo advertised capture."); }
        if (!tools.Any(predicate: tool => ((Text(value: tool!["name"]) == "puck_exec") && Text(value: tool["description"]).Contains(comparisonType: StringComparison.Ordinal, value: "world.state")))) { throw new InvalidDataException(message: "Admitted command discovery is absent."); }
        Console.WriteLine(value: "Public TLS, readiness, discovery, authentication and headless capability checks passed.");
        var onboard = await RequestAsync(method: "tools/call", tool: "puck_onboard");
        var state = Text(value: onboard["structuredContent"]?["state"]);

        if (state is not ("Ready" or "Migrating")) { throw new InvalidOperationException(message: "Account onboarding is still running; retry this verification explicitly."); }
        Console.WriteLine(value: "Function onboarding through user OBO passed.");
        if (tools.SingleOrDefault(predicate: tool => (Text(value: tool!["name"]) == "puck_service_observe")) is { } observe) {
            foreach (var name in observe["inputSchema"]!["properties"]!["observation"]!["enum"]!.AsArray()) {
                await RequestAsync(arguments: new() { ["observation"] = Text(value: name) }, method: "tools/call", tool: "puck_service_observe");
            }
            Console.WriteLine(value: "Granted ARM observations through user OBO passed.");
        }
        var attached = await RequestAsync(method: "tools/call", tool: "puck_attach");
        var attachment = Text(value: attached["structuredContent"]?["attachmentId"]);

        try {
            var samples = new double[30];

            for (var index = 0; (index < (samples.Length + 3)); index++) {
                var started = System.Diagnostics.Stopwatch.GetTimestamp();

                await RequestAsync(arguments: new() { ["attachmentId"] = attachment, ["command"] = "world.state" }, method: "tools/call", tool: "puck_exec");
                if (index >= 3) { samples[(index - 3)] = System.Diagnostics.Stopwatch.GetElapsedTime(startingTimestamp: started).TotalMilliseconds; }
            }
            Array.Sort(array: samples);
            Console.WriteLine(value: $"Delegated World reads passed: 30 samples after 3 warmups; p50={samples[14]:F1} ms, p95={samples[28]:F1} ms. This measures this caller and network, not a service SLO.");
        } finally { await RequestAsync(arguments: new() { ["attachmentId"] = attachment }, method: "tools/call", tool: "puck_detach"); }
    }
    private static async Task TestProductionAsync(bool beforeStaticPublication, string commit) {
        var outputs = Outputs();
        var configuration = Value(key: "worldSiloConfiguration", outputs: outputs);
        var group = Text(value: Value(key: "deploymentResourceGroupName", outputs: outputs));
        var address = WebsiteAddress(outputs: outputs);

        await RetryAsync(action: async () => {
            var app = await AzJsonAsync("containerapp", "show", "--name", Text(value: Value(key: "actorsName", outputs: outputs)), "-g", group, "-o", "json");
            var properties = app["properties"]!;

            if ((Text(value: properties["latestReadyRevisionName"]) != Text(value: properties["latestRevisionName"])) || (((string?)properties["provisioningState"]) != "Succeeded")) { throw new InvalidOperationException(message: "Actors has not made the release revision ready."); }
            if (Text(value: properties["template"]!["containers"]![0]!["image"]) != File.ReadAllText(path: "artifacts/web-actors.digest").Trim()) { throw new InvalidDataException(message: "Actors image digest differs from the release."); }
            await TestWorldReleaseAsync(group: null, image: null, scaleSet: null);
            await PuckAsync("world", "probe", $"{configuration["dns"]!["recordName"]}.{configuration["dns"]!["zoneName"]}", Text(value: configuration["port"]), "artifacts/world-silo.public-key");
            if (((string?)(await GetJsonAsync(uri: (address + "/api/health-check")))["status"]) != "Healthy") { throw new InvalidOperationException(message: "Production API dependency health is not Healthy."); }
            if (beforeStaticPublication) { return; }
            if (((string?)(await GetJsonAsync(uri: (address + "/release.json")))["commit"]) != commit) { throw new InvalidDataException(message: "Front Door dashboard release commit differs."); }
            await ExpectRepresentationAsync(cache: control => control.NoStore, cached: false, mediaTypes: ["application/json"], uri: (address + "/release.json"));
            await ExpectRepresentationAsync(cache: control => control.NoCache, cached: false, mediaTypes: ["text/html"], uri: (address + "/"));
            await ExpectRepresentationAsync(cache: control => control.NoCache, cached: false, mediaTypes: ["text/javascript", "application/javascript"], uri: (address + "/host-entry.js"));
            await ExpectRepresentationAsync(cache: control => control.NoCache, cached: false, mediaTypes: ["application/json"], uri: (address + "/portal/mf-manifest.json"));
            var chunks = Regex.Matches(input: await Http.GetStringAsync(requestUri: (address + "/")), pattern: "\"(/assets/[^\"]+\\.js)\"").Select(selector: match => match.Groups[1].Value).Distinct(comparer: StringComparer.Ordinal).ToArray();

            if (chunks.Length == 0) { throw new InvalidDataException(message: "The website shell references no hashed script."); }
            foreach (var chunk in chunks) {
                await ExpectRepresentationAsync(cache: control => (control.Public && (control.MaxAge == TimeSpan.FromDays(days: 365)) && control.Extensions.Any(predicate: extension => (extension.Name == "immutable"))), cached: true, mediaTypes: ["text/javascript", "application/javascript"], uri: (address + chunk));
            }
            foreach (var host in Value(key: "websiteHostNames", outputs: outputs).AsArray()) {
                using var home = await Http.GetAsync(requestUri: $"https://{host}/"); home.EnsureSuccessStatusCode();
                if (!(await home.Content.ReadAsStringAsync()).Contains(comparisonType: StringComparison.OrdinalIgnoreCase, value: "<html")) { throw new InvalidDataException(message: $"Website entry point is missing at {host}."); }
                using var docs = await Http.GetAsync(requestUri: $"https://{host}/reference/index.html"); docs.EnsureSuccessStatusCode();
                if (!(await docs.Content.ReadAsStringAsync()).Contains(comparisonType: StringComparison.OrdinalIgnoreCase, value: "<html") || (docs.Headers.TryGetValues(name: "X-Frame-Options", values: out var frames) && frames.Any(predicate: value => value.Equals(comparisonType: StringComparison.OrdinalIgnoreCase, value: "DENY")))) { throw new InvalidDataException(message: $"Embedded documentation is unavailable at {host}."); }
                // Front Door only admits configuration requests explicitly limited to the public label.
                using var config = await Http.GetAsync(requestUri: $"https://{host}/configuration?api-version=1.0&key=*&label=public"); config.EnsureSuccessStatusCode();
                if (config.Content.Headers.ContentType?.MediaType?.Contains(comparisonType: StringComparison.OrdinalIgnoreCase, value: "json") != true) { throw new InvalidDataException(message: $"Configuration route is missing at {host}."); }
            }
            var manifest = await GetJsonAsync(uri: (address + "/official/stable/manifest.json"));

            if (((string?)manifest["build"]!["commit"]) != commit) { throw new InvalidDataException(message: "Front Door official manifest commit differs."); }
            foreach (var file in manifest["engine"]!["files"]!.AsArray()) {
                using var request = new HttpRequestMessage(method: HttpMethod.Head, requestUri: $"{address}/official/{file!["path"]}");
                using var response = await Http.SendAsync(request: request); response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentType?.MediaType != Text(value: file["contentType"])) { throw new InvalidDataException(message: $"Incorrect engine response type for {file["name"]}."); }
            }
        }, attempts: 30, seconds: 10);
        Console.WriteLine(value: $"PASS: production readiness for {commit}.");
    }
    private static async Task ExpectRepresentationAsync(Func<CacheControlHeaderValue, bool> cache, bool cached, string[] mediaTypes, string uri) {
        // The second request shows whether Front Door served it from the edge: X-Cache ends in HIT.
        using var first = await Http.GetAsync(requestUri: uri); first.EnsureSuccessStatusCode();
        using var response = await Http.GetAsync(requestUri: uri); response.EnsureSuccessStatusCode();
        var edge = (response.Headers.TryGetValues(name: "X-Cache", values: out var values) ? string.Join(separator: ',', values: values) : "");

        if (!mediaTypes.Contains(value: (response.Content.Headers.ContentType?.MediaType ?? "")) || (response.Headers.CacheControl is not { } control) || !cache(control) || (edge.EndsWith(comparisonType: StringComparison.Ordinal, value: "HIT") != cached)) {
            throw new InvalidDataException(message: $"Unexpected representation at {uri}: {response.Content.Headers.ContentType} with Cache-Control {response.Headers.CacheControl} and X-Cache {edge}.");
        }
    }
    private static async Task TestWorldContainerAsync(string image) {
        await DockerAsync("run", "--rm", "--user", "1655:1655", "--read-only", "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges", "--entrypoint", "/usr/bin/caddy", image, "version");
        var fixture = Path.GetFullPath(path: "artifacts/silo-smoke");

        if (Directory.Exists(path: fixture)) { throw new IOException(message: "Use a fresh silo smoke-test directory."); }
        const string Owner = "c3cba5cd-41c9-477e-a9de-15e1f4a4d1ec";
        const string Port = "7825";

        Directory.CreateDirectory(path: Path.Combine(path1: fixture, path2: "worlds"));
        using (var key = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256)) {
            File.WriteAllBytes(bytes: key.ExportPkcs8PrivateKey(), path: Path.Combine(path1: fixture, path2: "federation.pk8"));
            File.WriteAllBytes(bytes: key.ExportSubjectPublicKeyInfo(), path: Path.Combine(path1: fixture, path2: "public-key"));
        }
        await DockerAsync("create", "--name", "silo-source", image);
        try { await DockerAsync("cp", "silo-source:/app/worlds/.", Path.Combine(path1: fixture, path2: "worlds")); } finally { await DockerAsync("rm", "silo-source"); }
        foreach (var source in Directory.EnumerateFiles(path: Path.Combine(path1: fixture, path2: "worlds"))) {
            var name = Path.GetFileName(path: source).Replace(comparisonType: StringComparison.Ordinal, newValue: "", oldValue: ".world.json");
            var world = CliFiles.ReadJson(path: source);

            if (name == "puck") { world["host"]!["authority"] = $"localhost:{Port}"; world["host"]!["listen"] = $"0.0.0.0:{Port}"; }
            CliFiles.WriteJson(path: Path.Combine(path1: fixture, path2: $"store/{Owner}/private/puck/hosted/{name}/definition.json"), value: world);
        }
        var silo = SiloDocument(keyFile: "/fixture/federation.pk8", owner: Owner, store: new JsonObject { ["type"] = "directory", ["settings"] = new JsonObject { ["path"] = "/fixture/store" } }, world: "puck");

        silo["stateDir"] = "/fixture/state";
        silo["lifecycle"] = new JsonObject {
            ["healthPort"] = 8081,
            ["shutdownSeconds"] = 120,
            ["progressTimeoutSeconds"] = 30,
            ["checkpointTimeoutSeconds"] = 180,
            ["journalTimeoutSeconds"] = 30,
            ["journalBacklogLimit"] = 1024,
        };
        CliFiles.WriteJson(path: Path.Combine(path1: fixture, path2: "silo.json"), value: silo);
        await DockerAsync("run", "--rm", "--user", "0", "--entrypoint", "chown",
            "--mount", $"type=bind,source={fixture},target=/fixture", image, "-R", "1654:1654", "/fixture");
        var pointer = $"/fixture/store/{Owner}/puck/hosted/puck/checkpoints/latest";
        var previous = "";

        for (var boot = 1; (boot <= 2); boot++) {
            await DockerAsync("run", "-d", "--name", "silo-smoke", "--user", "1654:1654", "--read-only",
                "--tmpfs", "/tmp:rw,noexec,nosuid,size=64m", "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
                "--pids-limit", "512", "--log-driver", "local", "--log-opt", "max-size=10m", "--log-opt", "max-file=3",
                "--mount", $"type=bind,source={fixture},target=/fixture", "-p", $"127.0.0.1:{Port}:{Port}/udp", "-p", "127.0.0.1:8081:8081",
                image, "--silo", "/fixture/silo.json");
            try {
                var checkpoint = "";
                var ready = false;
                var healthReason = "readiness has not answered";

                for (var attempt = 0; (attempt < 90); attempt++) {
                    if (await DockerAsync("inspect", "silo-smoke", "--format", "{{.State.Running}}") != "true") { throw new InvalidOperationException(message: "Silo exited before checkpointing the primary world."); }
                    checkpoint = await DockerAsync("exec", "silo-smoke", "sh", "-c", "if [ -f \"$1\" ]; then sha256sum \"$1\"; fi", "probe", pointer);
                    if ((checkpoint.Length != 0) && (checkpoint != previous)) {
                        try {
                            using var health = await Http.GetAsync(requestUri: "http://127.0.0.1:8081/healthz");

                            healthReason = await health.Content.ReadAsStringAsync();
                            if (healthReason.Length > 4096) { healthReason = healthReason[..4096]; }
                            if (health.IsSuccessStatusCode) { ready = true; break; }
                        } catch (HttpRequestException error) { healthReason = error.Message; }
                    }
                    await Task.Delay(delay: TimeSpan.FromSeconds(seconds: 2));
                }
                if ((checkpoint.Length == 0) || (checkpoint == previous)) { throw new InvalidOperationException(message: $"Silo boot {boot} did not activate and checkpoint Puck."); }
                if (!ready) { throw new InvalidOperationException(message: $"Silo boot {boot} checkpointed but did not become ready: {healthReason}"); }
                using var liveness = await Http.GetAsync(requestUri: "http://127.0.0.1:8081/livez");

                liveness.EnsureSuccessStatusCode();
                using var azureHealth = await Http.GetAsync(requestUri: "http://127.0.0.1:8081/livez/azure");

                azureHealth.EnsureSuccessStatusCode();
                var richHealth = JsonNode.Parse(json: await azureHealth.Content.ReadAsStringAsync());

                if ((azureHealth.Content.Headers.ContentType?.MediaType != "application/json") || (Text(value: richHealth?["ApplicationHealthState"]) != "Healthy")) {
                    throw new InvalidDataException(message: "The world container does not satisfy Azure's rich application health contract.");
                }
                await PuckAsync("world", "probe", "127.0.0.1", Port, Path.Combine(path1: fixture, path2: "public-key"));
                previous = checkpoint;
                Console.WriteLine(value: $"PASS: Puck boot {boot} activated, checkpointed, and accepted an authenticated QUIC connection.");
            } finally {
                try { Console.WriteLine(value: await DockerAsync("logs", "silo-smoke")); } finally { await DockerAsync("rm", "-f", "silo-smoke"); }
            }
        }
    }
}
