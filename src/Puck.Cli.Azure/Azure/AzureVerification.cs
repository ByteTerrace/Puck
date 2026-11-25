using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Puck.Cli.Automation;
using Puck.Storage;
using Puck.World;
using Puck.World.Server;

namespace Puck.Cli.Azure;

public static partial class AzureCommand {
    private static async Task TestWorldReleaseAsync(string? group, string? engine, string? scaleSet, TimeProvider clock) {
        var outputs = (((group is not null) && (scaleSet is not null))
            ? null
            : Outputs()
        );

        group ??= Text(value: Value(
            key: "deploymentResourceGroupName",
            outputs: outputs!
        ));
        scaleSet ??= Text(value: Value(
            key: "worldSiloConfiguration",
            outputs: outputs!
        )["name"]);
        engine ??= File.ReadAllText(path: WorldHostEnginePath).Trim();
        var workers = await WorkersAsync(
            group: group,
            scaleSet: scaleSet
        );

        if (workers.Length != 1) { throw new InvalidOperationException(message: "This release requires exactly one authoritative world worker."); }
        if (outputs is not null) {
            var compute = Value(
                key: "worldSiloConfiguration",
                outputs: outputs
            )["compute"]!;
            var priority = WorldPriority(compute: compute);

            if (!priority.Equals(
                comparisonType: StringComparison.OrdinalIgnoreCase,
                value: (((string?)workers[0]!["priority"]) ?? "Regular")
            )) {
                throw new InvalidDataException(message: $"World worker priority differs from the declared {priority} release. Redeploy the world to recreate its scale set.");
            }
            var expected = compute["imageReference"]!;
            var actual = workers[0]!["storageProfile"]!["imageReference"]!;

            foreach (var field in new[] { "publisher", "offer", "sku", "version" }) {
                if (!Text(value: expected[field]).Equals(
                    comparisonType: StringComparison.OrdinalIgnoreCase,
                    value: Text(value: actual[field])
                )) {
                    throw new InvalidDataException(message: $"World worker OS image {field} differs from the declared release. Drain and replace the worker before completing the host image change.");
                }
            }
        }
        var worker = Text(value: workers[0]!["name"]);
        var process = (await WorldGuestJsonAsync(
            group: group,
            script: WorldProbeScript,
            worker: worker
        ))["process"]!;

        if ((process["engine"]?.GetValue<string>() != engine) || (process["running"]?.GetValue<bool>() != true)) {
            throw new InvalidDataException(message: $"The running world host does not run the requested engine {engine}: {process.ToJsonString()}");
        }
        await RetryAsync(
            action: async () => {
                var view = await AzJsonAsync(
                    "vm",
                    "get-instance-view",
                    "-g",
                    group,
                    "-n",
                    worker,
                    "-o",
                    "json"
                );
                var state = Text(value: view["instanceView"]?["vmHealth"]?["status"]?["code"]);

                if (!state.Equals(
                    comparisonType: StringComparison.OrdinalIgnoreCase,
                    value: "HealthState/healthy"
                )) {
                    throw new InvalidDataException(message: $"Azure reports world application health '{state}'; the release requires Healthy.");
                }
            },
            attempts: 12,
            clock: clock,
            seconds: 5
        );
        if (outputs?["worldMcpConfiguration"]?["value"]?["options"] is { } mcp) {
            await RetryAsync(
                action: async () => {
                    using var deadline = new CancellationTokenSource(
                        delay: TimeSpan.FromSeconds(seconds: 30),
                        timeProvider: clock
                    );
                    using var response = await Http.GetAsync(
                        cancellationToken: deadline.Token,
                        requestUri: new Uri(
                            baseUri: new Uri(uriString: Text(value: mcp["publicUrl"])),
                            relativeUri: "/healthz"
                        )
                    );

                    response.EnsureSuccessStatusCode();
                },
                attempts: 6,
                clock: clock,
                seconds: 5
            );
        }
    }
    private static async Task TestWorldMcpAsync() {
        const string Protocol = "2026-07-28";
        var options = Value(
            key: "worldMcpConfiguration",
            outputs: Outputs()
        )["options"]!;
        var address = new Uri(uriString: Text(value: options["publicUrl"]));
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var client = new HttpClient(handler: handler) { Timeout = TimeSpan.FromSeconds(seconds: 125) };

        using (var health = await client.GetAsync(requestUri: new Uri(
            baseUri: address,
            relativeUri: "/healthz"
        ))) { health.EnsureSuccessStatusCode(); }
        var metadata = JsonNode.Parse(json: await client.GetStringAsync(requestUri: new Uri(
            baseUri: address,
            relativeUri: "/.well-known/oauth-protected-resource/mcp"
        )))!;

        if (
            (Text(value: metadata["resource"]) != address.AbsoluteUri) ||
            !metadata["scopes_supported"]!.AsArray().Any(predicate: scope => (Text(value: scope) == Text(value: options["authorizationScope"])))
        ) {
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
            using var request = new HttpRequestMessage(
                method: HttpMethod.Post,
                requestUri: address
            );

            if (authenticated) {
                request.Headers.Authorization = new(
                    parameter: assertion,
                    scheme: "Bearer"
                );
            }
            request.Headers.Accept.ParseAdd(input: "application/json, text/event-stream");
            request.Headers.Add(
                name: "MCP-Protocol-Version",
                value: Protocol
            );
            request.Headers.Add(
                name: "Mcp-Method",
                value: method
            );
            if (tool is not null) {
                request.Headers.Add(
                    name: "Mcp-Name",
                    value: tool
                );
            }
            request.Content = new StringContent(
                content: new JsonObject { ["jsonrpc"] = "2.0", ["id"] = ++sequence, ["method"] = method, ["params"] = parameters }.ToJsonString(),
                encoding: Encoding.UTF8,
                mediaType: "application/json"
            );
            using var response = await client.SendAsync(request: request);

            if (!authenticated) {
                if (
                    (response.StatusCode != HttpStatusCode.Unauthorized) ||
                    !response.Headers.WwwAuthenticate.Any()
                ) { throw new InvalidDataException(message: "Anonymous MCP access was not challenged."); }
                return new JsonObject();
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized) { throw new InvalidOperationException(message: "Live delegated authorization needs fresh sign-in, consent or claims satisfaction; credentials were not printed or substituted."); }
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync();

            if (body.Length > (1024 * 1024)) { throw new InvalidDataException(message: "MCP verification response exceeded its budget."); }
            var reply = ((response.Content.Headers.ContentType?.MediaType == "text/event-stream")
                ? body.Split(separator: '\n').Where(predicate: line => line.StartsWith(
                    comparisonType: StringComparison.Ordinal,
                    value: "data:"
                )).Select(selector: line => JsonNode.Parse(json: line[5..])).Last(predicate: node => (node?["id"]?.GetValue<int>() == sequence))!
                : JsonNode.Parse(json: body)!
            );

            if (
                (reply["error"] is not null) ||
                (reply["result"] is null)
            ) { throw new InvalidDataException(message: "Live MCP returned a protocol error."); }
            var result = reply["result"]!;

            if (result["isError"]?.GetValue<bool>() == true) {
                throw new InvalidDataException(message: $"Live MCP {tool} was refused: {result["content"]?[0]?["text"]}");
            }
            return result;
        }
        await RequestAsync(
            authenticated: false,
            method: "tools/list"
        );
        var tools = (await RequestAsync(method: "tools/list"))["tools"]!.AsArray();

        if (tools.Any(predicate: tool => (Text(value: tool!["name"]) == "puck_capture_frame"))) { throw new InvalidDataException(message: "The headless silo advertised capture."); }
        if (!tools.Any(predicate: tool => ((Text(value: tool!["name"]) == "puck_exec") && Text(value: tool["description"]).Contains(
            comparisonType: StringComparison.Ordinal,
            value: "world.state"
        )))) { throw new InvalidDataException(message: "Admitted command discovery is absent."); }
        Console.WriteLine(value: "Public TLS, readiness, discovery, authentication and headless capability checks passed.");
        var onboard = await RequestAsync(
            method: "tools/call",
            tool: "puck_onboard"
        );
        var state = Text(value: onboard["structuredContent"]?["state"]);

        if (state is not ("Ready" or "Migrating")) { throw new InvalidOperationException(message: "Account onboarding is still running; retry this verification explicitly."); }
        Console.WriteLine(value: "Function onboarding through user OBO passed.");
        if (tools.SingleOrDefault(predicate: tool => (Text(value: tool!["name"]) == "puck_service_observe")) is { } observe) {
            foreach (var name in observe["inputSchema"]!["properties"]!["observation"]!["enum"]!.AsArray()) {
                await RequestAsync(
                    arguments: new() { ["observation"] = Text(value: name) },
                    method: "tools/call",
                    tool: "puck_service_observe"
                );
            }
            Console.WriteLine(value: "Granted ARM observations through user OBO passed.");
        }
        var attached = await RequestAsync(
            method: "tools/call",
            tool: "puck_attach"
        );
        var attachment = Text(value: attached["structuredContent"]?["attachmentId"]);

        try {
            var samples = new double[30];

            for (var index = 0; (index < (samples.Length + 3)); index++) {
                var started = System.Diagnostics.Stopwatch.GetTimestamp();

                await RequestAsync(
                    arguments: new() { ["attachmentId"] = attachment, ["command"] = "world.state" },
                    method: "tools/call",
                    tool: "puck_exec"
                );
                if (index >= 3) { samples[(index - 3)] = System.Diagnostics.Stopwatch.GetElapsedTime(startingTimestamp: started).TotalMilliseconds; }
            }
            Array.Sort(array: samples);
            Console.WriteLine(value: $"Delegated World reads passed: 30 samples after 3 warmups; p50={samples[14]:F1} ms, p95={samples[28]:F1} ms. This measures this caller and network, not a service SLO.");
        } finally {
            await RequestAsync(
                arguments: new() { ["attachmentId"] = attachment },
                method: "tools/call",
                tool: "puck_detach"
            );
        }
    }
    private static async Task TestProductionAsync(bool beforeStaticPublication, string commit, TimeProvider clock) {
        var outputs = Outputs();
        var configuration = Value(
            key: "worldSiloConfiguration",
            outputs: outputs
        );
        var group = Text(value: Value(
            key: "deploymentResourceGroupName",
            outputs: outputs
        ));
        var address = WebsiteAddress(outputs: outputs);

        await RetryAsync(
            action: async () => {
                var app = await AzJsonAsync(
                    "containerapp",
                    "show",
                    "--name",
                    Text(value: Value(
                        key: "actorsName",
                        outputs: outputs
                    )),
                    "-g",
                    group,
                    "-o",
                    "json"
                );
                var properties = app["properties"]!;

                if (
                    (Text(value: properties["latestReadyRevisionName"]) != Text(value: properties["latestRevisionName"])) ||
                    (((string?)properties["provisioningState"]) != "Succeeded")
                ) { throw new InvalidOperationException(message: "Actors has not made the release revision ready."); }
                if (Text(value: properties["template"]!["containers"]![0]!["image"]) != File.ReadAllText(path: "artifacts/web-actors.digest").Trim()) { throw new InvalidDataException(message: "Actors image digest differs from the release."); }
                await TestWorldReleaseAsync(
                    clock: clock,
                    group: null,
                    engine: null,
                    scaleSet: null
                );
                // The load balancer forwards QUIC only to a worker whose /healthz answers 200, so the worker's own answer
                // goes first: a withheld readiness then names its reason instead of the probe timing out.
                await WorldHealthAsync(
                    group: group,
                    port: configuration["lifecycle"]!["healthPort"]!.GetValue<int>(),
                    route: "/healthz",
                    worker: Text(value: (await WorkersAsync(
                        group: group,
                        scaleSet: Text(value: configuration["name"])
                    ))[0]!["name"])
                );
                await PuckAsync(
                    "world",
                    "probe",
                    $"{configuration["dns"]!["recordName"]}.{configuration["dns"]!["zoneName"]}",
                    Text(value: configuration["port"]),
                    "artifacts/world-silo.public-key"
                );
                if (((string?)(await GetJsonAsync(uri: (address + "/api/health-check")))["status"]) != "Healthy") { throw new InvalidOperationException(message: "Production API dependency health is not Healthy."); }
                if (beforeStaticPublication) { return; }
                if (((string?)(await GetJsonAsync(uri: (address + "/release.json")))["commit"]) != commit) { throw new InvalidDataException(message: "Front Door dashboard release commit differs."); }
                await ExpectRepresentationAsync(
                    cache: control => control.NoStore,
                    cached: false,
                    mediaTypes: ["application/json"],
                    uri: (address + "/release.json")
                );
                await ExpectRepresentationAsync(
                    cache: control => control.NoCache,
                    cached: false,
                    mediaTypes: ["text/html"],
                    uri: (address + "/")
                );
                await ExpectRepresentationAsync(
                    cache: control => control.NoCache,
                    cached: false,
                    mediaTypes: ["text/javascript", "application/javascript"],
                    uri: (address + "/host-entry.js")
                );
                await ExpectRepresentationAsync(
                    cache: control => control.NoCache,
                    cached: false,
                    mediaTypes: ["application/json"],
                    uri: (address + "/portal/mf-manifest.json")
                );
                var chunks = Regex.Matches(
                    input: await Http.GetStringAsync(requestUri: (address + "/")),
                    pattern: "\"(/assets/[^\"]+\\.js)\""
                ).Select(selector: match => match.Groups[1].Value).Distinct(comparer: StringComparer.Ordinal).ToArray();

                if (chunks.Length == 0) { throw new InvalidDataException(message: "The website shell references no hashed script."); }
                foreach (var chunk in chunks) {
                    await ExpectRepresentationAsync(
                        cache: control => (control.Public && (control.MaxAge == TimeSpan.FromDays(days: 365)) && control.Extensions.Any(predicate: extension => (extension.Name == "immutable"))),
                        cached: true,
                        mediaTypes: ["text/javascript", "application/javascript"],
                        uri: (address + chunk)
                    );
                }
                foreach (var host in Value(
                    key: "websiteHostNames",
                    outputs: outputs
                ).AsArray()) {
                    using var home = await Http.GetAsync(requestUri: $"https://{host}/"); home.EnsureSuccessStatusCode();
                    if (!(await home.Content.ReadAsStringAsync()).Contains(
                        comparisonType: StringComparison.OrdinalIgnoreCase,
                        value: "<html"
                    )) { throw new InvalidDataException(message: $"Website entry point is missing at {host}."); }
                    using var docs = await Http.GetAsync(requestUri: $"https://{host}/reference/index.html"); docs.EnsureSuccessStatusCode();
                    if (
                        !(await docs.Content.ReadAsStringAsync()).Contains(
                        comparisonType: StringComparison.OrdinalIgnoreCase,
                        value: "<html"
                    ) ||
                        (docs.Headers.TryGetValues(
                        name: "X-Frame-Options",
                        values: out var frames
                    ) && frames.Any(predicate: value => value.Equals(
                        comparisonType: StringComparison.OrdinalIgnoreCase,
                        value: "DENY"
                    )))
                    ) { throw new InvalidDataException(message: $"Embedded documentation is unavailable at {host}."); }
                    // Front Door only admits configuration requests explicitly limited to the public label.
                    using var config = await Http.GetAsync(requestUri: $"https://{host}/configuration?api-version=1.0&key=*&label=public"); config.EnsureSuccessStatusCode();
                    if (config.Content.Headers.ContentType?.MediaType?.Contains(
                        comparisonType: StringComparison.OrdinalIgnoreCase,
                        value: "json"
                    ) != true) { throw new InvalidDataException(message: $"Configuration route is missing at {host}."); }
                }
                var manifest = await GetJsonAsync(uri: (address + "/official/stable/manifest.json"));

                if (((string?)manifest["build"]!["commit"]) != commit) { throw new InvalidDataException(message: "Front Door official manifest commit differs."); }
                foreach (var file in manifest["engine"]!["files"]!.AsArray()) {
                    using var request = new HttpRequestMessage(
                        method: HttpMethod.Head,
                        requestUri: $"{address}/official/{file!["path"]}"
                    );
                    using var response = await Http.SendAsync(request: request); response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentType?.MediaType != Text(value: file["contentType"])) { throw new InvalidDataException(message: $"Incorrect engine response type for {file["name"]}."); }
                }
            },
            attempts: 30,
            clock: clock,
            seconds: 10
        );
        Console.WriteLine(value: $"PASS: production readiness for {commit}.");
    }
    private static async Task ExpectRepresentationAsync(Func<CacheControlHeaderValue, bool> cache, bool cached, string[] mediaTypes, string uri) {
        // The second request shows whether Front Door served it from the edge: X-Cache ends in HIT.
        using var first = await Http.GetAsync(requestUri: uri); first.EnsureSuccessStatusCode();
        using var response = await Http.GetAsync(requestUri: uri); response.EnsureSuccessStatusCode();
        var edge = (response.Headers.TryGetValues(
            name: "X-Cache",
            values: out var values
        )
            ? string.Join(
                separator: ',',
                values: values
            )
            : ""
        );

        if (
            !mediaTypes.Contains(value: (response.Content.Headers.ContentType?.MediaType ?? "")) ||
            (response.Headers.CacheControl is not { } control) ||
            !cache(control) ||
            (edge.EndsWith(
            comparisonType: StringComparison.Ordinal,
            value: "HIT"
        ) != cached)
        ) {
            throw new InvalidDataException(message: $"Unexpected representation at {uri}: {response.Content.Headers.ContentType} with Cache-Control {response.Headers.CacheControl} and X-Cache {edge}.");
        }
    }
    // A checkpoint root's ordinal, which advances with each checkpoint a boot writes.
    [GeneratedRegex(pattern: "\"checkpointOrdinal\":[0-9]+", options: RegexOptions.CultureInvariant)]
    private static partial Regex CheckpointOrdinal();
    private static async Task TestWorldHostAsync(string installRoot, TimeProvider clock) {
        var (version, engine) = (WorldHostEngine.Current(installRoot: installRoot) ?? throw new InvalidDataException(message: $"{installRoot} holds no installed host release"));
        var executable = Path.Combine(path1: version, path2: Puck.Confinement.InstallLayout.ExecutableFileName(name: WorldHostLayout.Executable));
        var worldsDirectory = Path.Combine(path1: version, path2: WorldHostLayout.WorldsDirectory);
        var tlsFront = Path.Combine(path1: version, path2: WorldHostLayout.TlsFrontExecutable);

        Console.WriteLine(value: $"Verifying the installed engine {engine} ({version}).");
        if (File.Exists(path: tlsFront)) {
            await RunAsync(arguments: ["version"], executable: tlsFront);
        }
        var fixture = Path.GetFullPath(path: "artifacts/silo-smoke");

        if (Directory.Exists(path: fixture)) { throw new IOException(message: "Use a fresh silo smoke-test directory."); }
        const string Owner = "c3cba5cd-41c9-477e-a9de-15e1f4a4d1ec";
        const string Port = "7825";

        Directory.CreateDirectory(path: Path.Combine(
            path1: fixture,
            path2: "worlds"
        ));
        using (var key = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256)) {
            File.WriteAllBytes(
                bytes: key.ExportPkcs8PrivateKey(),
                path: Path.Combine(
                    path1: fixture,
                    path2: "federation.pk8"
                )
            );
            File.WriteAllBytes(
                bytes: key.ExportSubjectPublicKeyInfo(),
                path: Path.Combine(
                    path1: fixture,
                    path2: "public-key"
                )
            );
        }
        if (CliFiles.CopyDirectory(destination: Path.Combine(path1: fixture, path2: "worlds"), source: worldsDirectory) != 0) {
            throw new IOException(message: $"the installed worlds at {worldsDirectory} could not be copied");
        }
        var services = new ServiceCollection();

        Puck.Storage.DependencyInjection.PuckStorageServiceRegistration.AddCore(services: services);
        using (var provider = services.BuildServiceProvider()) {
            var authority = new WorldAuthorityBlobStore(
                documentDirectory: Puck.Abstractions.PuckPaths.Normalize(path: Path.GetFullPath(path: Path.Combine(
                    path1: fixture,
                    path2: "worlds"
                ))),
                store: provider.GetRequiredService<IObjectBlobStore>(),
                target: WorldAuthorityBlobStore.DirectoryTarget(rootPath: Path.Combine(
                    path1: fixture,
                    path2: "store"
                ))
            );

            foreach (var source in HostedWorldFiles(directory: Path.Combine(
                path1: fixture,
                path2: "worlds"
            ))) {
                var name = WorldDocumentName.OfDocumentFile(path: Path.GetFileName(path: source));
                var world = CliFiles.ReadJson(path: source);

                if (name == HostedWorldName) { world["host"]!["authority"] = $"localhost:{Port}"; world["host"]!["listen"] = $"{DeploymentListenAddress}:{Port}"; }
                var published = await authority.PublishDefinitionBytesAsync(
                    cancellationToken: CancellationToken.None,
                    definition: Encoding.UTF8.GetBytes(s: world.ToJsonString()),
                    identity: new(
                        Owner: Guid.Parse(input: Owner),
                        World: SafeName.Parse(candidate: name)
                    )
                );

                if (!published.Ok) { throw new IOException(message: $"silo smoke-test definition '{name}' could not be published: {published.Detail}"); }
            }
        }
        var silo = SiloDocument(
            documentDirectory: WorldHostLayout.WorldsDirectory,
            keyFile: Path.Combine(path1: fixture, path2: "federation.pk8"),
            owner: Owner,
            store: new JsonObject { ["type"] = "directory", ["settings"] = new JsonObject { ["path"] = Path.Combine(path1: fixture, path2: "store") } },
            world: HostedWorldName
        );

        silo["stateDir"] = Path.Combine(path1: fixture, path2: "state");
        silo["lifecycle"] = new JsonObject {
            ["healthPort"] = 8081,
            ["healthAddress"] = DeploymentListenAddress,
            ["shutdownSeconds"] = 120,
            ["progressTimeoutSeconds"] = 30,
            ["checkpointTimeoutSeconds"] = 180,
            ["journalTimeoutSeconds"] = 30,
            ["journalBacklogLimit"] = 1024,
        };
        CliFiles.WriteJson(
            path: Path.Combine(
                path1: fixture,
                path2: "silo.json"
            ),
            value: silo
        );
        // The second boot runs the release the way a deployment with MCP enabled does, so this document has the shape the
        // deployment's worldMcpConfiguration output renders: a tenant issuer, oid subjects and an inventory observation,
        // which makes the Azure MCP host construct its ARM pipeline at startup. Nothing here reaches the network before
        // a caller arrives.
        const string Tenant = "5b1e4c3a-9d2f-4e8b-a6c1-0f7d2e3b4a59";
        const string Audience = "8f2d6a41-3c7e-4b95-9a0d-e1c6b7f45a23";

        CliFiles.WriteJson(
            path: Path.Combine(
                path1: fixture,
                path2: "mcp.json"
            ),
            value: new JsonObject {
                ["target"] = HostedWorldName,
                ["publicUrl"] = "https://world.invalid/mcp",
                ["listenUrl"] = "http://127.0.0.1:8082",
                ["issuer"] = $"https://login.microsoftonline.com/{Tenant}/v2.0",
                ["audience"] = Audience,
                ["scope"] = "user_impersonation",
                ["authorizationScope"] = $"api://{Audience}/user_impersonation",
                ["subjectClaim"] = "oid",
                ["tenantId"] = Tenant,
                ["allowedSubjects"] = new JsonArray(Owner),
                ["services"] = new JsonObject {
                    ["managedIdentityClientId"] = "0d4c8e2f-6a1b-4f37-b5e9-2c8a7d3f1e60",
                    ["onboardingUrl"] = "https://world.invalid/api/self-onboard",
                    ["observations"] = new JsonArray(new JsonObject {
                        ["name"] = "storage-inventory",
                        ["kind"] = "inventory",
                        ["subjects"] = new JsonArray(Owner),
                        ["settings"] = new JsonObject {
                            ["resourceGroup"] = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/silo-smoke",
                            ["apiVersion"] = "2021-04-01",
                            ["resourceType"] = "Microsoft.Storage/storageAccounts",
                            ["fields"] = new JsonObject { ["name"] = "/name" },
                        },
                    }),
                },
            }
        );
        var root = Path.Combine(path1: fixture, path2: "store", path3: $"{Owner}/{WorldOwnedWorldSync.HostedPrivateNamespace}/{HostedWorldName}/authority/root");
        var previous = "";

        for (var boot = 1; (boot <= 2); boot++) {
            // Boot 1 runs the deployment's shape without MCP; boot 2 runs its MCP shape, the release's CLI composing the
            // installed extensions over the silo it hosts. Both run from the version directory, as the units do.
            var log = Path.Combine(path1: fixture, path2: $"boot-{boot}.log");
            using var host = Puck.Abstractions.ChildProcess.StartRedirected(
                arguments: ["world", "host", "serve", "--silo", Path.Combine(path1: fixture, path2: "silo.json"), .. ((boot == 1) ? [] : (string[])["--mcp", Path.Combine(path1: fixture, path2: "mcp.json")])],
                fileName: executable,
                workingDirectory: version
            );
            var output = Task.WhenAll(host.StandardOutput.ReadToEndAsync(), host.StandardError.ReadToEndAsync());

            host.StandardInput.Close();
            try {
                var checkpoint = "";
                var ready = false;
                var healthReason = "readiness has not answered";

                for (var attempt = 0; (attempt < 90); attempt++) {
                    if (host.HasExited) { throw new InvalidOperationException(message: "Silo exited before checkpointing the primary world."); }
                    checkpoint = (File.Exists(path: root) ? (CheckpointOrdinal().Match(input: await File.ReadAllTextAsync(path: root)).Value) : "");
                    if (
                        (checkpoint.Length != 0) &&
                        (checkpoint != previous)
                    ) {
                        try {
                            using var health = await Http.GetAsync(requestUri: "http://127.0.0.1:8081/healthz");

                            healthReason = await health.Content.ReadAsStringAsync();
                            if (healthReason.Length > 4096) { healthReason = healthReason[..4096]; }
                            if (health.IsSuccessStatusCode) { ready = true; break; }
                        } catch (HttpRequestException error) { healthReason = error.Message; }
                    }
                    await Task.Delay(
                        delay: TimeSpan.FromSeconds(seconds: 2),
                        timeProvider: clock
                    );
                }
                if (
                    (checkpoint.Length == 0) ||
                    (checkpoint == previous)
                ) { throw new InvalidOperationException(message: $"Silo boot {boot} did not activate and checkpoint '{HostedWorldName}'."); }
                if (!ready) { throw new InvalidOperationException(message: $"Silo boot {boot} checkpointed but did not become ready: {healthReason}"); }
                using var liveness = await Http.GetAsync(requestUri: "http://127.0.0.1:8081/livez");

                liveness.EnsureSuccessStatusCode();
                using var azureHealth = await Http.GetAsync(requestUri: "http://127.0.0.1:8081/livez/azure");

                azureHealth.EnsureSuccessStatusCode();
                var richHealth = JsonNode.Parse(json: await azureHealth.Content.ReadAsStringAsync());

                if (
                    (azureHealth.Content.Headers.ContentType?.MediaType != "application/json") ||
                    (Text(value: richHealth?["ApplicationHealthState"]) != "Healthy")
                ) {
                    throw new InvalidDataException(message: "The world host does not satisfy Azure's rich application health contract.");
                }
                await PuckAsync(
                    "world",
                    "probe",
                    "127.0.0.1",
                    Port,
                    Path.Combine(
                        path1: fixture,
                        path2: "public-key"
                    )
                );
                previous = checkpoint;
                Console.WriteLine(value: $"PASS: Puck boot {boot} activated, checkpointed, and accepted an authenticated QUIC connection.");
            } finally {
                if (!host.HasExited) { host.Kill(entireProcessTree: true); }
                await host.WaitForExitAsync();
                var lines = await output;

                Console.WriteLine(value: lines[0]);
                Console.WriteLine(value: lines[1]);
            }
        }
    }
}
