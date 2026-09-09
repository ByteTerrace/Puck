#!/usr/bin/env dotnet
#:property PublishAot=false
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static Puck.AutomationProcess;

try {
    return await AzureAutomation.ExecuteAsync(args: args);
} catch (Exception error) {
    Console.Error.WriteLine(value: $"azure: {error.Message}");
    return 1;
}

// Deployment orchestration deliberately remains usable before Puck CLI is released.
// Artifact composition, hashing, and validation are CLI commands; this app owns Azure calls.
internal static class AzureAutomation {
    private static readonly HttpClient Http = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromMinutes(minutes: 5) };
    private static readonly Dictionary<string, string> Options = new(comparer: StringComparer.Ordinal);

    internal static async Task<int> ExecuteAsync(string[] args) {
        if (args is [] or ["-h" or "--help"]) {
            Console.WriteLine(value: """
                dotnet run -c Release --file build/Azure.cs -- <command> [options]
                  build [--output-directory artifacts/azure] [--runtime-artifacts DIR]
                  build-infrastructure [--output-directory artifacts/infrastructure]
                  deploy-infrastructure [--resource-group byteterrace] [--infrastructure-artifacts DIR]
                  deploy-applications --artifacts-directory DIR --commit SHA
                  deploy-world-platform [--resource-group byteterrace]
                  deploy-world-mcp [--resource-group byteterrace] [--plan-only]
                  deploy-world [--resource-group GROUP]
                  publish-static [--bundle-directory artifacts/azure]
                  publish-template-specs
                  publish-container --registry NAME --repository web-actors|world-silo --commit SHA [--archive FILE] [--output-file FILE]
                  functions-access [--restore] [--resource-group GROUP] [--name NAME]
                  vault-access [--restore]
                  test-production --commit SHA [--before-static-publication]
                  test-world-container --image IMAGE
                  test-world-release [--resource-group GROUP --scale-set NAME --image DIGEST]
                  test-world-mcp
                  build-actors [--no-restart] [--registry NAME] [--resource-group GROUP] [--container-app NAME]
                  stage-world-image --commit SHA
                  current
                """);
            return ((args.Length == 0) ? 2 : 0);
        }
        var allowed = new HashSet<string>(collection: args[0] switch {
            "build" => ["output-directory", "runtime-artifacts"],
            "build-infrastructure" => ["output-directory"],
            "deploy-infrastructure" => ["resource-group", "infrastructure-artifacts"],
            "deploy-world-platform" or "deploy-world" => ["resource-group"],
            "deploy-world-mcp" => ["resource-group", "plan-only"],
            "deploy-applications" => ["artifacts-directory", "commit", "resource-group"],
            "publish-static" => ["bundle-directory", "resource-group"],
            "publish-container" => ["registry", "repository", "commit", "archive", "output-file"],
            "functions-access" => ["restore", "resource-group", "name"],
            "vault-access" => ["restore"],
            "test-production" => ["commit", "before-static-publication"],
            "test-world-container" => ["image"],
            "test-world-release" => ["resource-group", "scale-set", "image"],
            "build-actors" => ["no-restart", "registry", "resource-group", "container-app"],
            "stage-world-image" => ["commit"],
            "publish-template-specs" or "current" or "test-world-mcp" => [],
            _ => throw new ArgumentException(message: $"Unknown command: {args[0]}"),
        }, comparer: StringComparer.Ordinal);

        for (var index = 1; (index < args.Length); index++) {
            var key = args[index];

            if (!key.StartsWith(comparisonType: StringComparison.Ordinal, value: "--") || !allowed.Contains(item: key[2..])) { throw new ArgumentException(message: $"Unknown option: {key}"); }
            var flag = (key is "--restore" or "--before-static-publication" or "--no-restart" or "--plan-only");
            var value = (flag ? "true" : ((++index < args.Length) ? args[index] : throw new ArgumentException(message: $"Missing value for {key}")));

            if (!Options.TryAdd(key: key[2..], value: value)) { throw new ArgumentException(message: $"Duplicate option: {key}"); }
        }
        var root = (Puck.RepositoryPaths.FindRoot() ?? throw new DirectoryNotFoundException(message: "Run in the Puck checkout."));

        if (Path.GetFullPath(path: Environment.CurrentDirectory) != root) { throw new ArgumentException(message: "Run Azure automation from the repository root."); }
        switch (args[0]) {
            case "build": await BuildAsync(); break;
            case "build-infrastructure": await BuildInfrastructureAsync(); break;
            case "deploy-infrastructure": await DeployInfrastructureAsync(); break;
            case "deploy-applications": await DeployApplicationsAsync(); break;
            case "deploy-world-platform": await DeployWorldPlatformAsync(); break;
            case "deploy-world-mcp": await DeployWorldMcpAsync(); break;
            case "deploy-world": await DeployWorldAsync(); break;
            case "publish-static": await PublishStaticAsync(); break;
            case "publish-template-specs": await PublishTemplateSpecsAsync(); break;
            case "publish-container": await PublishContainerAsync(registry: Required(key: "registry"), repository: Required(key: "repository"), commit: Commit(), archive: Option(key: "archive"), output: Option(key: "output-file")); break;
            case "functions-access": await FunctionsAccessAsync(); break;
            case "vault-access": await VaultAccessAsync(); break;
            case "test-production": await TestProductionAsync(); break;
            case "test-world-container": await TestWorldContainerAsync(); break;
            case "test-world-release": await TestWorldReleaseAsync(); break;
            case "test-world-mcp": await TestWorldMcpAsync(); break;
            case "build-actors": await BuildActorsAsync(); break;
            case "stage-world-image": await StageWorldImageAsync(); break;
            case "current": await CurrentAsync(); break;
            default: throw new ArgumentException(message: $"Unknown command: {args[0]}");
        }
        return 0;
    }

    private static string Option(string key, string fallback = "") => Options.GetValueOrDefault(defaultValue: fallback, key: key);
    private static string Required(string key) => ((Option(key: key) is { Length: > 0 } value) ? value : throw new ArgumentException(message: $"--{key} is required."));
    private static string Commit() {
        var value = Required(key: "commit");

        if (!Regex.IsMatch(input: value, pattern: "\\A[a-f0-9]{40}\\z")) { throw new ArgumentException(message: "Expected a full lowercase commit SHA."); }
        return value;
    }
    private static string Text(JsonNode? value) => (value?.ToString() ?? throw new InvalidDataException(message: "Missing required JSON value."));
    private static JsonNode Outputs() => Read(path: "artifacts/production-outputs.json");
    private static JsonNode Value(JsonNode outputs, string key) => (outputs[key]?["value"] ?? throw new InvalidDataException(message: $"Missing deployment output: {key}"));
    private static Task<string> AzAsync(params string[] args) => RunAsync(executable: "az", arguments: args, capture: true);
    private static async Task<JsonNode> AzJsonAsync(params string[] args) => (JsonNode.Parse(json: await AzAsync(args: args)) ?? throw new InvalidDataException(message: "Azure returned empty JSON."));
    private static Task<string> DockerAsync(params string[] args) => RunAsync(executable: "docker", arguments: args, capture: true);
    private static void Mask(string value) {
        if (Environment.GetEnvironmentVariable(variable: "GITHUB_ACTIONS") == "true") { Console.WriteLine(value: $"::add-mask::{value}"); }
    }
    private static void Output(string key, string value) {
        if (Environment.GetEnvironmentVariable(variable: "GITHUB_OUTPUT") is { Length: > 0 } output) { File.AppendAllText(contents: $"{key}={value}\n", path: output); }
    }
    private static async Task<string> TokenAsync(string resource) {
        var token = await AzAsync("account", "get-access-token", "--resource", resource, "--query", "accessToken", "-o", "tsv");

        Mask(value: token);
        return token;
    }
    private static async Task RetryAsync(Func<Task> action, int attempts = 12, int seconds = 5) {
        for (var attempt = 0; ; attempt++) {
            try { await action(); return; } catch (Exception error) when (((attempt + 1) < attempts)) {
                Console.Error.WriteLine($"Attempt {attempt + 1}/{attempts} failed: {error.Message}");
                await Task.Delay(delay: TimeSpan.FromSeconds(seconds: seconds));
            }
        }
    }
    private static async Task<JsonNode> GetJsonAsync(string uri) => (JsonNode.Parse(json: await Http.GetStringAsync(requestUri: uri)) ?? throw new InvalidDataException(message: $"Empty response: {uri}"));
    private static async Task CurrentAsync() {
        var reference = (Environment.GetEnvironmentVariable(variable: "GITHUB_REF") ?? throw new InvalidOperationException(message: "GITHUB_REF is required."));
        var tip = (await RunAsync(executable: "git", arguments: ["ls-remote", "origin", reference], capture: true)).Split(options: StringSplitOptions.RemoveEmptyEntries, separator: ((char[]?)null)).FirstOrDefault();

        if (tip is null) { throw new IOException(message: "Could not resolve the deployment branch."); }
        Output(key: "deploy", value: ((tip == Environment.GetEnvironmentVariable(variable: "GITHUB_SHA")) ? "true" : "false"));
    }
    private static async Task BuildAsync() {
        var output = Path.GetFullPath(path: Option(fallback: "artifacts/azure", key: "output-directory"));

        if (Directory.Exists(path: output)) { throw new IOException(message: $"Use a fresh output directory: {output}"); }
        Directory.CreateDirectory(path: output);
        var commit = await RunAsync(executable: "git", arguments: ["rev-parse", "HEAD"], capture: true);

        Environment.SetEnvironmentVariable(value: "true", variable: "CI");
        var runtimeArtifacts = Option("runtime-artifacts");
        if (runtimeArtifacts.Length == 0) {
            await RunAsync(executable: "dotnet", arguments: ["restore", "src/Puck.Azure.Functions", "--locked-mode"]);
            await RunAsync(executable: "dotnet", arguments: ["publish", "src/Puck.Azure.Functions", "-c", "Release", "--no-restore", "-o", Path.Combine(path1: output, path2: "functions")]);
        } else {
            if (Text(Read(Path.Combine(runtimeArtifacts, "source.json"))["commit"]) != commit) { throw new InvalidDataException("Runtime artifacts belong to another commit."); }
            CopyDirectory(Path.Combine(runtimeArtifacts, "functions"), Path.Combine(output, "functions"));
        }
        foreach (var file in new[] { "host.json", "worker.config.json", "functions.metadata", "Puck.Azure.Functions.dll" }) {
            if (!File.Exists(path: Path.Combine(path1: output, path2: "functions", path3: file))) { throw new IOException(message: $"Functions publish omitted {file}"); }
        }
        var configuration = Read(path: "src/Puck.Azure.Functions/configuration.json");
        var sentinel = configuration["items"]!.AsArray().Single(predicate: item => (((string?)item!["key"]) == "Version"))!;

        sentinel["value"] = commit;
        Write(path: Path.Combine(path1: output, path2: "configuration.json"), value: configuration);
        if (runtimeArtifacts.Length == 0) {
            await RunAsync(executable: "dotnet", arguments: ["restore", "src/Puck.World.Browser", "--locked-mode"]);
            await RunAsync(executable: "dotnet", arguments: ["publish", "src/Puck.World.Browser", "-c", "Release", "--no-restore"]);
        } else {
            CopyDirectory(Path.Combine(runtimeArtifacts, "browser"), "src/Puck.World.Browser/bin/Release/net10.0/browser-wasm/AppBundle");
        }
        await PuckAsync("official", "build", "--out", Path.Combine(path1: output, path2: "official"), "--channel", "stable", "--engine", "src/Puck.World.Browser/bin/Release/net10.0/browser-wasm/AppBundle");
        await PuckAsync("official", "verify", "--base", Path.Combine(path1: output, path2: "official"), "--channel", "stable", "--expect-commit", commit);
        await PuckAsync("official", "build", "--out", "artifacts/official", "--channel", "dev", "--engine", "src/Puck.World.Browser/bin/Release/net10.0/browser-wasm/AppBundle");
        await PuckAsync("world", "prepare", "src/Puck.World/Assets/worlds", Path.Combine(path1: output, path2: "silo-worlds"));
        var dashboard = Path.GetFullPath(path: "src/Puck.Dashboard/src");

        Environment.SetEnvironmentVariable(value: "stable", variable: "VITE_PUCK_OFFICIAL_CHANNEL");
        Environment.SetEnvironmentVariable(value: "https://puck.byteterrace.com/official", variable: "VITE_PUCK_OFFICIAL_BASE");
        foreach (var command in new string[][] { ["ci"], ["--workspace", "portal", "run", "check:types"], ["run", "build"], ["--workspace", "portal", "run", "test"], ["run", "stage"] }) {
            await RunAsync(executable: "npm", arguments: command, directory: dashboard);
        }
        CopyDirectory(source: "src/Puck.Dashboard/dist-deploy", destination: Path.Combine(path1: output, path2: "dashboard-storage"));
        await PuckAsync("docs", "build", Path.Combine(path1: output, path2: "dashboard-storage"));
        await PuckAsync("bundle", "create", output, commit);
    }
    private static async Task BuildInfrastructureAsync() {
        var output = Path.GetFullPath(Option("output-directory", "artifacts/infrastructure"));
        if (Directory.Exists(output)) { throw new IOException($"Use a fresh output directory: {output}"); }
        Directory.CreateDirectory(output);
        await AzAsync("bicep", "build", "--file", "src/Puck.Azure.Resources/main.bicep", "--outfile", Path.Combine(output, "infrastructure.json"));
        await AzAsync("bicep", "build", "--file", "src/Puck.Azure.Resources/worldSiloCompute.bicep", "--outfile", Path.Combine(output, "world-silo-compute.json"));
        await AzAsync("bicep", "build-params", "--file", "src/Puck.Azure.Resources/main.bicepparam", "--outfile", Path.Combine(output, "parameters.json"));
        Write(Path.Combine(output, "source.json"), new JsonObject { ["commit"] = await RunAsync("git", ["rev-parse", "HEAD"], capture: true) });
    }
    private static async Task SetOwnerAsync(string group) {
        Environment.SetEnvironmentVariable(variable: "BICEPPARAM_OWNER_OBJECT_ID", value: await AzAsync("identity", "show", "--name", "bytrcidpzzz", "-g", group, "--query", "principalId", "-o", "tsv"));
        Environment.SetEnvironmentVariable(value: "ServicePrincipal", variable: "BICEPPARAM_OWNER_PRINCIPAL_TYPE");
    }
    private static async Task DeployInfrastructureAsync() {
        var group = Option(fallback: "byteterrace", key: "resource-group");

        await SetOwnerAsync(group: group);
        var owner = Environment.GetEnvironmentVariable(variable: "BICEPPARAM_OWNER_OBJECT_ID")!;
        var graph = await AzJsonAsync("ad", "sp", "show", "--id", "00000003-0000-0000-c000-000000000000", "--query", "{id:id,roles:appRoles}", "-o", "json");
        var assigned = await AzJsonAsync("rest", "--method", "get", "--url", $"https://graph.microsoft.com/v1.0/servicePrincipals/{owner}/appRoleAssignments", "--query", "value", "-o", "json");

        foreach (var permission in new[] { "Application.ReadWrite.OwnedBy", "Application.Read.All", "AppRoleAssignment.ReadWrite.All", "GroupMember.Read.All" }) {
            var roles = graph["roles"]!.AsArray().Where(predicate: role => ((((string?)role!["value"]) == permission) && role["allowedMemberTypes"]!.AsArray().Any(predicate: type => (((string?)type) == "Application")))).ToArray();

            if ((roles.Length != 1) || !assigned.AsArray().Any(predicate: role => ((Text(value: role!["resourceId"]) == Text(value: graph["id"])) && (Text(value: role["appRoleId"]) == Text(value: roles[0]!["id"]))))) {
                throw new InvalidOperationException(message: $"CI is missing Microsoft Graph {permission}. Complete operator identity setup before deployment.");
            }
        }
        Directory.CreateDirectory(path: "artifacts");
        var infrastructure = Option("infrastructure-artifacts");
        if (infrastructure.Length == 0) {
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true") { throw new InvalidOperationException("CI deployment requires its compiled infrastructure artifact."); }
            await AzAsync("bicep", "build", "--file", "src/Puck.Azure.Resources/main.bicep", "--outfile", "artifacts/production-infrastructure.json");
            await AzAsync("bicep", "build-params", "--file", "src/Puck.Azure.Resources/main.bicepparam", "--outfile", "artifacts/production-parameters.json");
        } else {
            if (Text(Read(Path.Combine(infrastructure, "source.json"))["commit"]) != await RunAsync("git", ["rev-parse", "HEAD"], capture: true)) { throw new InvalidDataException("Infrastructure artifacts belong to another commit."); }
            File.Copy(Path.Combine(infrastructure, "infrastructure.json"), "artifacts/production-infrastructure.json", overwrite: true);
            File.Copy(Path.Combine(infrastructure, "parameters.json"), "artifacts/production-parameters.json", overwrite: true);
            File.Copy(Path.Combine(infrastructure, "world-silo-compute.json"), "artifacts/production-world-compute.json", overwrite: true);
        }
        var parameters = Read(path: "artifacts/production-parameters.json");
        parameters["parameters"]!["owner"]!["value"] = new JsonObject { ["objectId"] = owner, ["principalType"] = "ServicePrincipal" };
        var name = Text(value: parameters["parameters"]!["resources"]!["value"]!["actors"]!["name"]);
        var image = await AzAsync("containerapp", "show", "--name", name, "-g", group, "--query", "properties.template.containers[0].image", "-o", "tsv");

        parameters["parameters"]!["actorsImage"] = new JsonObject { ["value"] = image };
        Write(path: "artifacts/production-parameters.json", value: parameters);
        var plan = await AzJsonAsync("deployment", "group", "what-if", "-g", group, "--template-file", "artifacts/production-infrastructure.json", "--parameters", "artifacts/production-parameters.json", "--no-pretty-print");

        Write(path: "artifacts/production-plan.json", value: plan);
        if (((string?)plan["status"]) != "Succeeded") { throw new InvalidOperationException(message: "Production infrastructure planning failed."); }
        if (plan["changes"]!.AsArray().Any(predicate: change => (((string?)change!["changeType"]) == "Delete"))) { throw new InvalidOperationException(message: "Production plan includes resource deletion; inspect the retained plan."); }
        Write(path: "artifacts/production-outputs.json", value: await AzJsonAsync("deployment", "group", "create", "--name", "puck-production-platform", "-g", group, "--template-file", "artifacts/production-infrastructure.json", "--parameters", "artifacts/production-parameters.json", "--query", "properties.outputs", "-o", "json"));
    }
    private static async Task DeployWorldPlatformAsync() {
        var group = Option(fallback: "byteterrace", key: "resource-group");

        await SetOwnerAsync(group: group);
        Directory.CreateDirectory(path: "artifacts");
        await AzAsync("bicep", "build-params", "--file", "src/Puck.Azure.Resources/main.bicepparam", "--outfile", "artifacts/world-platform-source.json");
        var source = Read(path: "artifacts/world-platform-source.json")["parameters"]!;
        var values = new JsonObject {
            ["configuration"] = source["resources"]!["value"]!["worldSilo"]!.DeepClone(),
            ["actionGroup"] = source["resources"]!["value"]!["worldSiloActionGroup"]!.DeepClone(),
            ["publishingPrincipalId"] = Environment.GetEnvironmentVariable(variable: "BICEPPARAM_OWNER_OBJECT_ID"),
            ["storageAccountName"] = (Text(value: source["partitioning"]!["value"]!["prefix"]) + "stp001"),
            ["registryName"] = source["resources"]!["value"]!["containerRegistry"]!["name"]!.DeepClone(),
            ["keyVaultName"] = source["resources"]!["value"]!["keyVault"]!["name"]!.DeepClone(),
            ["location"] = await AzAsync("group", "show", "-n", group, "--query", "location", "-o", "tsv"),
            ["tags"] = source["tags"]!["value"]!.DeepClone(),
        };

        WriteParameters(path: "artifacts/world-platform-parameters.json", values: values);
        var outputs = await AzJsonAsync("deployment", "group", "create", "-g", group, "-n", "puck-world-platform", "--template-file", "src/Puck.Azure.Resources/worldSiloPlatform.bicep", "--parameters", "@artifacts/world-platform-parameters.json", "--query", "properties.outputs", "-o", "json");
        // A world-only release preserves the newest successfully deployed MCP policy from either composition.
        var mcp = await AzJsonAsync("deployment", "group", "list", "-g", group, "--query",
            "[?(name=='puck-production-platform' || name=='puck-world-mcp') && properties.provisioningState=='Succeeded'] | sort_by(@, &properties.timestamp) | [-1].properties.outputs.worldMcpConfiguration || `{}`", "-o", "json");

        if (mcp["value"] is not null) { outputs["worldMcpConfiguration"] = mcp.DeepClone(); } else if (source["worldMcp"]?["value"] is not null) { throw new InvalidOperationException(message: "Deploy unified infrastructure or deploy-world-mcp before enabling MCP in an application release."); }
        Write(path: "artifacts/production-outputs.json", value: outputs);
    }
    private static async Task DeployWorldMcpAsync() {
        var group = Option(key: "resource-group", fallback: "byteterrace");
        await AzAsync("bicep", "build-params", "--file", "src/Puck.Azure.Resources/main.bicepparam", "--outfile", "artifacts/world-mcp-source.json");
        var source = Read(path: "artifacts/world-mcp-source.json")["parameters"]!;
        var settings = source["worldMcp"]?["value"] ?? throw new InvalidOperationException(message: "Set BICEPPARAM_WORLD_MCP to the explicit participant policy.");
        var outputs = await AzJsonAsync("deployment", "group", "show", "-g", group, "-n", "puck-world-platform", "--query", "properties.outputs", "-o", "json");
        var configuration = Value(outputs: outputs, key: "worldSiloConfiguration");
        var applicationId = Text(value: configuration["authentication"]!["settings"]!["audience"]);
        var application = await AzJsonAsync("ad", "app", "show", "--id", applicationId, "--query", "{uniqueName:uniqueName,api:api,identifierUris:identifierUris}", "-o", "json");
        if (!application["api"]!["oauth2PermissionScopes"]!.AsArray().Any(scope => Text(value: scope!["value"]) == "user_impersonation" && scope["isEnabled"]!.GetValue<bool>())) {
            throw new InvalidOperationException(message: "The existing API must expose enabled user_impersonation before MCP deployment.");
        }
        var resources = source["resources"]!["value"]!;
        var identifier = Text(value: resources["applicationRegistration"]!["identifierUri"]);
        if (!application["identifierUris"]!.AsArray().Any(uri => Text(value: uri) == identifier)) { throw new InvalidOperationException(message: "The configured API identifier does not match the deployed registration."); }
        var route = resources["frontDoor"]!["routes"]!.AsArray().First(row => Text(value: row!["originGroupName"]) == "api")!;
        WriteParameters(path: "artifacts/world-mcp-parameters.json", values: new JsonObject {
            ["settings"] = settings.DeepClone(), ["configuration"] = configuration.DeepClone(),
            ["applicationUniqueName"] = Text(value: application["uniqueName"]), ["applicationId"] = applicationId,
            ["authorizationScope"] = identifier + "/user_impersonation",
            ["identityClientId"] = Value(outputs, "worldSiloClientId").DeepClone(), ["identityPrincipalId"] = Value(outputs, "worldSiloOwner").DeepClone(),
            ["onboardingUrl"] = "https://" + Text(value: route["customDomains"]![0]) + "/api/self-onboard",
            ["applicationInsightsName"] = resources["containerEnvironment"]!["applicationInsights"]!["name"]!.DeepClone(),
            ["location"] = Value(outputs, "deploymentLocation").DeepClone(), ["tags"] = Value(outputs, "deploymentTags").DeepClone(),
        });
        var plan = await AzJsonAsync("deployment", "group", "what-if", "-g", group, "--template-file", "src/Puck.Azure.Resources/worldMcp.bicep", "--parameters", "@artifacts/world-mcp-parameters.json", "--no-pretty-print");
        Write(path: "artifacts/world-mcp-plan.json", value: plan);
        if (((string?)plan["status"]) != "Succeeded" || plan["changes"]!.AsArray().Any(change => ((string?)change!["changeType"]) == "Delete")) {
            throw new InvalidOperationException(message: "MCP deployment planning failed or proposes deletion; inspect world-mcp-plan.json.");
        }
        if (Option(key: "plan-only") == "true") { return; }
        var deployed = await AzJsonAsync("deployment", "group", "create", "-g", group, "-n", "puck-world-mcp", "--template-file", "src/Puck.Azure.Resources/worldMcp.bicep", "--parameters", "@artifacts/world-mcp-parameters.json", "--query", "properties.outputs", "-o", "json");
        outputs["worldMcpConfiguration"] = deployed["worldMcpConfiguration"]!.DeepClone();
        Write(path: "artifacts/production-outputs.json", value: outputs);
    }
    private static void WriteParameters(string path, JsonObject values) {
        var parameters = new JsonObject();

        foreach (var item in values) { parameters[item.Key] = new JsonObject { ["value"] = item.Value?.DeepClone() }; }
        Write(path: path, value: new JsonObject { ["parameters"] = parameters });
    }
    private static async Task PublishContainerAsync(string registry, string repository, string commit, string archive, string output) {
        if ((repository is not ("web-actors" or "world-silo")) || !Regex.IsMatch(input: commit, pattern: "\\A[a-f0-9]{40}\\z")) { throw new ArgumentException(message: "Invalid repository or commit."); }
        var server = await AzAsync("acr", "show", "--name", registry, "--query", "loginServer", "-o", "tsv");
        var tenant = await AzAsync("account", "show", "--query", "tenantId", "-o", "tsv");
        var token = await TokenAsync(resource: "https://containerregistry.azure.net");
        using var form = new FormUrlEncodedContent(nameValueCollection: new Dictionary<string, string> { ["grant_type"] = "access_token", ["service"] = server, ["tenant"] = tenant, ["access_token"] = token });
        using var response = await Http.PostAsync(content: form, requestUri: $"https://{server}/oauth2/exchange");

        response.EnsureSuccessStatusCode();
        var refresh = Text(value: JsonNode.Parse(json: await response.Content.ReadAsStringAsync())!["refresh_token"]);

        Mask(value: refresh);
        var image = $"{server}/{repository}:{commit}";

        try {
            await RunAsync(executable: "docker", arguments: ["login", server, "--username", "00000000-0000-0000-0000-000000000000", "--password-stdin"], input: (refresh + "\n"));
            if (archive.Length != 0) { await DockerAsync("load", "--input", archive); }
            await DockerAsync("tag", $"puck/{repository}:{commit}", image);
            await DockerAsync("push", image);
            var inspected = JsonNode.Parse(json: await DockerAsync("image", "inspect", image))!;
            var digest = inspected[0]!["RepoDigests"]!.AsArray().Select(selector: value => Text(value: value)).Single(predicate: value => value.StartsWith(comparisonType: StringComparison.Ordinal, value: $"{server}/{repository}@sha256:"));

            if (output.Length != 0) { File.WriteAllText(contents: (digest + "\n"), path: output); }
            Console.WriteLine(value: digest);
        } finally { await DockerAsync("logout", server); }
    }
    private static async Task DeployApplicationsAsync() {
        var artifacts = Required(key: "artifacts-directory");
        var commit = Commit();
        var bundle = Path.Combine(path1: artifacts, path2: "azure-applications");

        await PuckAsync("bundle", "verify", bundle, commit);
        if (Directory.Exists(path: "artifacts/azure")) { throw new IOException(message: "Use a fresh deployment workspace."); }
        CopyDirectory(destination: "artifacts/azure", source: bundle);
        Write(path: "artifacts/release-source.json", value: new JsonObject { ["commit"] = commit, ["runId"] = Environment.GetEnvironmentVariable(variable: "GITHUB_RUN_ID") });
        foreach (var repository in new[] { "web-actors", "world-silo" }) {
            await PublishContainerAsync(registry: "bytrccrp000", repository: repository, commit: commit, archive: Path.Combine(path1: artifacts, path2: $"azure-{repository}/image.tar.gz"), output: $"artifacts/{repository}.digest");
        }
        var configuration = Read(path: "artifacts/azure/configuration.json");
        var outputs = Outputs();

        foreach (var item in configuration["items"]!.AsArray()) {
            if (((string?)item!["key"]) == "Onboarding:ActorsBaseUrl") { item["value"] = Value(key: "actorsEndpoint", outputs: outputs).DeepClone(); }
        }
        Write(path: "artifacts/production-configuration.json", value: configuration);
        // This imports the public application configuration and returns no values. Preserve CLI errors;
        // credential-reading commands continue to use captured output through AzAsync.
        await RunAsync("az", ["appconfig", "kv", "import", "--name", "bytrcappcsp000", "--auth-mode", "login", "--source", "file", "--path", "artifacts/production-configuration.json", "--format", "json", "--profile", "appconfig/kvset", "--yes", "-o", "none"]);
        var group = Option(fallback: "byteterrace", key: "resource-group");
        var before = await AzJsonAsync("containerapp", "show", "--name", "bytrccap001", "-g", group, "-o", "json");

        Write(path: "artifacts/previous-actor-release.json", value: new JsonObject { ["revision"] = before["properties"]!["latestReadyRevisionName"]!.DeepClone(), ["image"] = before["properties"]!["template"]!["containers"]![0]!["image"]!.DeepClone() });
        await AzAsync("containerapp", "update", "--name", "bytrccap001", "-g", group, "--image", File.ReadAllText(path: "artifacts/web-actors.digest").Trim(), "--revision-suffix", $"git-{commit[..12]}-{Environment.GetEnvironmentVariable(variable: "GITHUB_RUN_ID")}-{Environment.GetEnvironmentVariable(variable: "GITHUB_RUN_ATTEMPT")}", "-o", "none");
        Output(key: "function-app-name", value: "bytrcfuncp000");
    }
    private static async Task<string> RunnerIpAsync() {
        var text = (await Http.GetStringAsync(requestUri: "https://api.ipify.org")).Trim();

        if (!IPAddress.TryParse(address: out var ip, ipString: text) || (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)) { throw new InvalidDataException(message: "Expected the hosted runner IPv4 address."); }
        return text;
    }
    private static async Task FunctionsAccessAsync() {
        const string Snapshot = "artifacts/functions-scm-access.json";

        if (Options.ContainsKey(key: "restore")) {
            if (!File.Exists(path: Snapshot)) { return; }
            var saved = Read(path: Snapshot);

            await AzAsync("rest", "--method", "put", "--url", Text(value: saved["url"]), "--body", ("@" + Text(value: saved["bodyFile"])), "-o", "none");
            return;
        }
        var group = Option(fallback: "byteterrace", key: "resource-group");
        var name = Option(fallback: "bytrcfuncp000", key: "name");
        var id = await AzAsync("functionapp", "show", "--name", name, "-g", group, "--query", "id", "-o", "tsv");
        var url = $"https://management.azure.com{id}/config/web?api-version=2024-04-01";
        var config = await AzJsonAsync("rest", "--method", "get", "--url", url, "-o", "json");
        var properties = new JsonObject();

        foreach (var key in new[] { "scmIpSecurityRestrictions", "scmIpSecurityRestrictionsDefaultAction", "scmIpSecurityRestrictionsUseMain" }) { properties[key] = config["properties"]![key]?.DeepClone(); }
        Write(path: "artifacts/functions-scm-restore.json", value: new JsonObject { ["properties"] = properties });
        Write(path: Snapshot, value: new JsonObject { ["url"] = url, ["bodyFile"] = "artifacts/functions-scm-restore.json" });
        var ip = await RunnerIpAsync();

        await AzAsync("webapp", "config", "access-restriction", "add", "-g", group, "--name", name, "--scm-site", "true", "--rule-name", $"gha-{Environment.GetEnvironmentVariable(variable: "GITHUB_RUN_ID")}", "--action", "Allow", "--ip-address", (ip + "/32"), "--priority", "100", "-o", "none");
        await AzAsync("webapp", "config", "access-restriction", "set", "-g", group, "--name", name, "--use-same-restrictions-for-scm-site", "false", "--scm-default-action", "Deny", "-o", "none");
    }
    private static async Task VaultAccessAsync() {
        const string Snapshot = "artifacts/key-vault-access.json";

        if (Options.ContainsKey(key: "restore")) {
            if (!File.Exists(path: Snapshot)) { return; }
            var saved = Read(path: Snapshot);

            if (((bool?)saved["added"]) == true) { await AzAsync("keyvault", "network-rule", "remove", "--name", Text(value: saved["vault"]), "--ip-address", Text(value: saved["ip"]), "-o", "none"); }
            return;
        }
        var vault = Text(value: Value(outputs: Outputs(), key: "deploymentKeyVaultName"));
        var ip = await RunnerIpAsync();
        var rules = await AzJsonAsync("keyvault", "show", "--name", vault, "--query", "properties.networkAcls.ipRules[].value", "-o", "json");
        var added = !rules.AsArray().Any(predicate: rule => ((Text(value: rule) == ip) || (Text(value: rule) == (ip + "/32"))));

        Write(path: Snapshot, value: new JsonObject { ["vault"] = vault, ["ip"] = (ip + "/32"), ["added"] = added });
        if (added) { await AzAsync("keyvault", "network-rule", "add", "--name", vault, "--ip-address", (ip + "/32"), "-o", "none"); }
    }
    private static async Task PublishTemplateSpecsAsync() {
        const string Sources = "src/Puck.Azure.Resources";
        const string Destination = "artifacts/template-specs";
        var alias = Read(path: $"{Sources}/bicepconfig.json")["moduleAliases"]!["ts"]!["bvm"]!;
        var subscription = Text(value: alias["subscription"]);
        var group = Text(value: alias["resourceGroup"]);

        Directory.CreateDirectory(path: Destination);
        var location = await AzAsync("group", "show", "--name", group, "--subscription", subscription, "--query", "location", "-o", "tsv");
        var existing = await AzJsonAsync("ts", "list", "-g", group, "--subscription", subscription, "--query", "[].name", "-o", "json");

        foreach (var spec in Read(path: $"{Sources}/template-specs.json").AsArray()) {
            var name = Text(value: spec!["name"]);
            var version = Text(value: spec["version"]);
            var template = $"{Destination}/{name}.json";

            await AzAsync("bicep", "build", "--file", Path.Combine(path1: Sources, path2: Text(value: spec["path"])), "--outfile", template);
            var versions = (existing.AsArray().Any(predicate: value => (Text(value: value) == name))
                ? await AzJsonAsync("ts", "show", "--name", name, "-g", group, "--subscription", subscription, "--query", "versions", "-o", "json") : new JsonArray());

            if (versions.AsArray().Any(predicate: value => (Text(value: value) == version))) {
                var token = await AzAsync("account", "get-access-token", "--subscription", subscription, "--query", "accessToken", "-o", "tsv");

                Mask(value: token);
                using var request = new HttpRequestMessage(method: HttpMethod.Get, requestUri: $"https://management.azure.com/subscriptions/{subscription}/resourceGroups/{group}/providers/Microsoft.Resources/templateSpecs/{name}/versions/{version}?api-version=2022-02-01");

                request.Headers.Authorization = new AuthenticationHeaderValue(parameter: token, scheme: "Bearer");
                using var response = await Http.SendAsync(request: request);

                response.EnsureSuccessStatusCode();
                var published = JsonNode.Parse(json: await response.Content.ReadAsStringAsync())!["properties"]!["mainTemplate"]!;
                var compiled = Read(path: template);

                NormalizeTemplate(node: published);
                NormalizeTemplate(node: compiled);
                if (!JsonNode.DeepEquals(node1: published, node2: compiled)) { throw new InvalidDataException(message: $"Template Spec {name}:{version} differs from source. Author a new version; published versions are immutable."); }
                Console.WriteLine(value: $"Already published: {name}:{version}");
                continue;
            }
            await AzAsync("ts", "create", "--name", name, "--version", version, "-g", group, "--subscription", subscription, "--location", location, "--template-file", template, "--tags", "ManagedBy=Bicep", "-o", "none");
            Console.WriteLine(value: $"Published: {name}:{version}");
        }
    }
    private static void NormalizeTemplate(JsonNode? node) {
        if (node is JsonObject obj) {
            if ((obj["templateLink"] is JsonObject link) && (((string?)link["id"]) is { } id) && (((string?)link["resourceGroup"]) is { } group)) {
                var match = Regex.Match(input: id, pattern: "^/subscriptions/[^/]+/resourceGroups/([^/]+)/providers/Microsoft.Resources/templateSpecs/[^/]+/versions/[^/]+$");

                if (match.Success && string.Equals(a: match.Groups[1].Value, b: group, comparisonType: StringComparison.OrdinalIgnoreCase)) { link.Remove(propertyName: "resourceGroup"); }
            }
            foreach (var property in obj) { NormalizeTemplate(node: property.Value); }
        } else if (node is JsonArray array) {
            foreach (var child in array) { NormalizeTemplate(node: child); }
        }
    }
    private static async Task SendBlobAsync(string endpoint, string container, string name, string file, string token, string contentType, string cache = "no-cache", string encoding = "", string commit = "") {
        var path = string.Join(separator: "/", values: new[] { container }.Concat(second: name.Split('/')).Select(selector: Uri.EscapeDataString));

        await RetryAsync(action: async () => {
            using var request = new HttpRequestMessage(method: HttpMethod.Put, requestUri: $"{endpoint.TrimEnd(trimChar: '/')}/{path}");

            request.Headers.Authorization = new AuthenticationHeaderValue(parameter: token, scheme: "Bearer");
            request.Headers.Add(name: "x-ms-version", value: "2023-11-03");
            request.Headers.Add(name: "x-ms-date", value: DateTime.UtcNow.ToString(format: "R", provider: System.Globalization.CultureInfo.InvariantCulture));
            request.Headers.Add(name: "x-ms-blob-type", value: "BlockBlob");
            request.Headers.Add(name: "x-ms-blob-cache-control", value: cache);
            if (encoding.Length != 0) { request.Headers.Add(name: "x-ms-blob-content-encoding", value: encoding); }
            if (commit.Length != 0) { request.Headers.Add(name: "x-ms-meta-commit", value: commit); }
            using var stream = File.OpenRead(path: file);

            request.Headers.Add(name: "x-ms-meta-sha256", value: Convert.ToHexStringLower(inArray: SHA256.HashData(source: stream)));
            stream.Position = 0;
            request.Content = new StreamContent(content: stream);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType: contentType);
            using var response = await Http.SendAsync(request: request);

            response.EnsureSuccessStatusCode();
        }, attempts: 4);
    }
    private static async Task PublishStaticAsync() {
        var bundle = Option(fallback: "artifacts/azure", key: "bundle-directory");
        var release = Read(path: $"{bundle}/release.json");

        if (((string?)release["channel"]) != "stable") { throw new InvalidDataException(message: "Production requires the stable content channel."); }
        var outputs = Outputs();
        var container = Text(value: Value(key: "officialContentContainerName", outputs: outputs));

        if (!Regex.IsMatch(input: container, pattern: "\\A[a-f0-9-]{36}\\z")) { throw new InvalidDataException(message: "Missing official-content container."); }
        var endpoint = new Uri(uriString: Text(value: Value(key: "staticSiteEndpoint", outputs: outputs))).GetLeftPart(part: UriPartial.Authority);
        var token = await TokenAsync(resource: "https://storage.azure.com/");
        var manifest = Read(path: $"{bundle}/official/stable/manifest.json");
        var types = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
        var objects = new[] { manifest["worldSchemaBundle"] }.Concat(second: manifest["documents"]!.AsArray()).Concat(second: manifest["composed"]!.AsArray()).Concat(second: manifest["assets"]!.AsArray()).Concat(second: manifest["engine"]!["files"]!.AsArray());

        foreach (var item in objects) {
            var path = Text(value: item!["path"]);
            var type = Text(value: item["contentType"]);

            if (!Regex.IsMatch(input: path, pattern: "\\Aobjects/sha256/[a-f0-9]{2}/[a-f0-9]{64}\\z")) { throw new InvalidDataException(message: "Invalid official object path."); }
            if (types.TryGetValue(key: path, value: out var previous) && (previous != type)) { throw new InvalidDataException(message: "Conflicting official media types."); }
            types[path] = type;
        }
        var commit = Text(value: release["commit"]);

        foreach (var item in types) {
            await SendBlobAsync(endpoint: endpoint, container: container, name: $"public/puck/official/{item.Key}", file: $"{bundle}/official/{item.Key}", token: token, contentType: item.Value, cache: "public,max-age=31536000,immutable", commit: commit);
        }
        await SendBlobAsync(endpoint: endpoint, container: container, name: "public/puck/official/stable/manifest.json", file: $"{bundle}/official/stable/manifest.json", token: token, contentType: "application/json", commit: commit);
        var site = Path.GetFullPath(path: $"{bundle}/dashboard-storage");

        foreach (var file in Directory.EnumerateFiles(path: site, searchOption: SearchOption.AllDirectories, searchPattern: "*").OrderBy(keySelector: file => (file == Path.Combine(path1: site, path2: "index.html"))).ThenBy(file => file, StringComparer.Ordinal)) {
            var name = Path.GetRelativePath(path: file, relativeTo: site).Replace(newChar: '/', oldChar: '\\');
            var encoding = (((name == "index.html") || name.StartsWith(comparisonType: StringComparison.Ordinal, value: "assets/")) ? "br" : "");

            await SendBlobAsync(endpoint: endpoint, container: "$web", name: name, file: file, token: token, contentType: MediaType(path: file), encoding: encoding, commit: commit);
        }
        await SendBlobAsync(endpoint: endpoint, container: "$web", name: "release.json", file: $"{bundle}/release.json", token: token, contentType: "application/json", cache: "no-store", commit: commit);
        await AzAsync("afd", "endpoint", "purge", "-g", Option(fallback: "byteterrace", key: "resource-group"), "--profile-name", "bytrcfdp000", "--endpoint-name", "default", "--content-paths", "/*", "-o", "none");
    }
    private static string MediaType(string path) => Path.GetExtension(path: path).ToLowerInvariant() switch {
        ".html" => "text/html",
        ".js" or ".mjs" => "application/javascript",
        ".css" => "text/css",
        ".json" or ".map" => "application/json",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".ico" => "image/x-icon",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".ttf" => "font/ttf",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".pdf" => "application/pdf",
        ".xml" => "application/xml",
        ".yml" or ".yaml" => "application/yaml",
        ".wasm" => "application/wasm",
        ".txt" => "text/plain",
        _ => "application/octet-stream",
    };
    private static JsonObject SiloDocument(string owner, string world, string keyFile, JsonObject store, JsonNode? lifecycle = null) {
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
        if (Uri.CheckHostName(hostname) != UriHostNameType.Dns || hostname.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-'))) {
            throw new InvalidDataException("MCP requires a public DNS hostname.");
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

    private static async Task DeployWorldAsync() {
        var outputs = Outputs();
        var configuration = Value(key: "worldSiloConfiguration", outputs: outputs);
        var group = Option(key: "resource-group", fallback: Text(value: Value(key: "deploymentResourceGroupName", outputs: outputs)));
        var owner = Text(value: Value(key: "worldSiloOwner", outputs: outputs));
        var endpoint = Text(value: Value(key: "worldSiloStorageEndpoint", outputs: outputs)).TrimEnd(trimChar: '/');
        var vault = Text(value: Value(key: "deploymentKeyVaultName", outputs: outputs));
        var secret = Text(value: configuration["federationKeySecretName"]);
        var host = $"{configuration["dns"]!["recordName"]}.{configuration["dns"]!["zoneName"]}";
        var port = Text(value: configuration["port"]);
        JsonNode? secrets = null;

        await RetryAsync(action: async () => { secrets = await AzJsonAsync("keyvault", "secret", "list", "--vault-name", vault, "--query", "[].name", "-o", "json"); });
        var temporary = Path.Combine(path1: Path.GetTempPath(), path2: $"puck-silo-{Guid.NewGuid():N}");

        Directory.CreateDirectory(path: temporary);
        try {
            if (!secrets!.AsArray().Any(predicate: value => (Text(value: value) == secret))) {
                using var generated = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
                var encoded = Convert.ToBase64String(inArray: generated.ExportPkcs8PrivateKey());

                Mask(value: encoded);
                File.WriteAllText(path: Path.Combine(path1: temporary, path2: "key.txt"), contents: encoded);
                await AzAsync("keyvault", "secret", "set", "--vault-name", vault, "--name", secret, "--file", Path.Combine(path1: temporary, path2: "key.txt"), "-o", "none");
            }
            var encodedKey = await AzAsync("keyvault", "secret", "show", "--vault-name", vault, "--name", secret, "--query", "value", "-o", "tsv");

            Mask(value: encodedKey);
            using var key = ECDsa.Create();

            key.ImportPkcs8PrivateKey(source: Convert.FromBase64String(s: encodedKey), bytesRead: out _);
            File.WriteAllBytes(path: "artifacts/world-silo.public-key", bytes: key.ExportSubjectPublicKeyInfo());
            var token = await TokenAsync(resource: "https://storage.azure.com/");

            async Task PublishWorldsAsync() {
                foreach (var file in Directory.EnumerateFiles(path: "artifacts/azure/silo-worlds")) {
                    var world = Read(path: file);
                    var name = Path.GetFileName(path: file).Replace(comparisonType: StringComparison.Ordinal, newValue: "", oldValue: ".world.json");

                    if (name == Text(value: configuration["worldName"])) {
                        world["host"]!["authority"] = $"{host}:{port}";
                        world["host"]!["listen"] = $"0.0.0.0:{port}";
                        if (outputs["worldMcpConfiguration"]?["value"]?["admission"] is JsonArray delegated) {
                            var admission = world["admission"] as JsonArray ?? new JsonArray();
                            if (world["admission"] is null) { world["admission"] = admission; }
                            foreach (var participant in delegated) { admission.Add(participant!.DeepClone()); }
                        }
                    }
                    var path = Path.Combine(path1: temporary, path2: Path.GetFileName(path: file));

                    Write(path: path, value: world);
                    await SendBlobAsync(endpoint: endpoint, container: owner, name: $"private/puck/hosted/{name}/definition.json", file: path, token: token, contentType: "application/json");
                }
            }
            var lifecycle = new JsonObject {
                ["healthPort"] = configuration["lifecycle"]!["healthPort"]!.DeepClone(),
                ["shutdownSeconds"] = configuration["lifecycle"]!["shutdownSeconds"]!.DeepClone(),
                ["progressTimeoutSeconds"] = configuration["lifecycle"]!["progressTimeoutSeconds"]!.DeepClone(),
                ["checkpointTimeoutSeconds"] = configuration["lifecycle"]!["checkpointTimeoutSeconds"]!.DeepClone(),
                ["journalTimeoutSeconds"] = configuration["lifecycle"]!["journalTimeoutSeconds"]!.DeepClone(),
                ["journalBacklogLimit"] = configuration["lifecycle"]!["journalBacklogLimit"]!.DeepClone(),
            };

            if (configuration["lifecycle"]!["azureScheduledEvents"]!.GetValue<bool>()) {
                lifecycle["observer"] = new JsonObject {
                    ["type"] = "azure.scheduled-events",
                    ["settings"] = new JsonObject { ["pollSeconds"] = configuration["lifecycle"]!["pollSeconds"]!.DeepClone() },
                };
            }
            var silo = SiloDocument(owner: owner, world: Text(value: configuration["worldName"]), keyFile: "/configuration/federation.pk8",
                store: new JsonObject { ["type"] = "azure.blob", ["settings"] = new JsonObject { ["accountUrl"] = endpoint } }, lifecycle: lifecycle);
            var authentication = configuration["authentication"]!.DeepClone();

            silo["worlds"]![0]!["federation"]!["authentication"] = authentication.DeepClone();
            authentication["settings"]!["remoteKeyHash"] = Convert.ToHexStringLower(inArray: SHA256.HashData(source: key.ExportSubjectPublicKeyInfo()));
            Write(path: "artifacts/world-authentication.json", value: authentication);
            var image = File.ReadAllText(path: "artifacts/world-silo.digest").Trim();

            if (!Regex.IsMatch(input: image, pattern: "\\A[a-z0-9.-]+/[a-z0-9/_-]+@sha256:[a-f0-9]{64}\\z")) { throw new InvalidDataException(message: "World image must be an immutable registry digest."); }
            var script = File.ReadAllText(path: "build/Start-WorldSilo.sh");
            var mcpDeployment = outputs["worldMcpConfiguration"]?["value"];
            var mcpOptions = mcpDeployment?["options"];
            var replacements = new Dictionary<string, string> {
                ["__MCP_ENABLED__"] = ((mcpOptions is null) ? "0" : "1"),
                ["__MCP_DOCUMENT__"] = ((mcpOptions is null) ? "" : Convert.ToBase64String(inArray: Encoding.UTF8.GetBytes(s: mcpOptions.ToJsonString()))),
                ["__MCP_TLS_DOCUMENT__"] = mcpOptions is null ? "" : Convert.ToBase64String(Encoding.UTF8.GetBytes(McpTlsConfiguration(host).ToJsonString())),
                ["__MCP_HOST__"] = host,
                ["__MCP_ENTRYPOINT__"] = ((mcpOptions is null) ? "" : "--entrypoint dotnet"),
                ["__MCP_ARGUMENTS__"] = ((mcpOptions is null) ? "--silo /configuration/silo.json" : "/puck-cli/Puck.Cli.dll mcp --silo /configuration/silo.json --http /configuration/mcp.json"),
                ["__SILO_DOCUMENT__"] = Convert.ToBase64String(inArray: Encoding.UTF8.GetBytes(s: silo.ToJsonString())),
                ["__FEDERATION_KEY__"] = encodedKey,
                ["__CLIENT_ID__"] = Text(value: Value(key: "worldSiloClientId", outputs: outputs)),
                ["__REGISTRY__"] = image.Split('/')[0],
                ["__IMAGE__"] = image,
                ["__HEALTH_PORT__"] = Text(value: configuration["lifecycle"]!["healthPort"]),
                ["__QUIC_PORT__"] = port,
                ["__SHUTDOWN_SECONDS__"] = Text(value: configuration["lifecycle"]!["shutdownSeconds"]),
                ["__STOP_SECONDS__"] = (((int)configuration["lifecycle"]!["shutdownSeconds"]!) + 10).ToString(provider: System.Globalization.CultureInfo.InvariantCulture),
                ["__CPU__"] = Text(value: configuration["container"]!["cpu"]),
                ["__MEMORY__"] = Text(value: configuration["container"]!["memoryInGB"]),
            };

            foreach (var replacement in replacements) { script = script.Replace(oldValue: replacement.Key, newValue: replacement.Value, comparisonType: StringComparison.Ordinal); }
            if (Regex.IsMatch(input: script, pattern: "__[A-Z_]+__")) { throw new InvalidDataException(message: "Unresolved VM bootstrap placeholder."); }
            var workers = await StableWorkersAsync(group: group, scaleSet: Text(value: configuration["name"]));

            if (workers.Length > 1) { throw new InvalidOperationException(message: "This release requires one authoritative worker; reconcile existing placement before deployment."); }
            string ssh;

            if (workers.Length == 1) { ssh = Text(value: workers[0]!["osProfile"]!["linuxConfiguration"]!["ssh"]!["publicKeys"]![0]!["keyData"]); } else {
                await RunAsync(executable: "ssh-keygen", arguments: ["-q", "-t", "ed25519", "-N", "", "-f", Path.Combine(path1: temporary, path2: "ssh")]);
                ssh = File.ReadAllText(path: Path.Combine(path1: temporary, path2: "ssh.pub")).Trim();
            }
            var command = $"printf '%s' '{Convert.ToBase64String(inArray: Encoding.UTF8.GetBytes(s: script))}' | base64 -d | bash";

            Mask(value: command);
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

            using (var gzip = new System.IO.Compression.GZipStream(releaseBuffer, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true)) {
                var saved = new JsonObject { ["parameters"] = parameters.DeepClone(), ["publicKey"] = Convert.ToBase64String(inArray: key.ExportSubjectPublicKeyInfo()) };

                await gzip.WriteAsync(Encoding.UTF8.GetBytes(s: saved.ToJsonString()));
            }
            var state = Convert.ToBase64String(inArray: releaseBuffer.ToArray());

            if (Encoding.UTF8.GetByteCount(s: state) > 25000) { throw new InvalidDataException(message: "Compressed release state exceeds the vault secret limit."); }
            var statePath = Path.Combine(path1: temporary, path2: "release-state.txt");

            File.WriteAllText(contents: state, path: statePath);
            var stateSecret = Text(value: configuration["releaseStateSecretName"]);
            JsonNode? previousRelease = null;

            if (secrets!.AsArray().Any(predicate: value => (Text(value: value) == stateSecret))) {
                var encodedState = await AzAsync("keyvault", "secret", "show", "--vault-name", vault, "--name", stateSecret, "--query", "value", "-o", "tsv");

                Mask(value: encodedState);
                using var compressed = new MemoryStream(buffer: Convert.FromBase64String(s: encodedState));
                using var gzip = new System.IO.Compression.GZipStream(mode: System.IO.Compression.CompressionMode.Decompress, stream: compressed);

                previousRelease = await JsonNode.ParseAsync(gzip);
            }
            if ((workers.Length != 0) && (previousRelease is null)) { throw new InvalidOperationException(message: "The existing worker needs its known-good release state adopted into the configured vault secret before transactional deployment."); }
            var snapshot = new JsonArray();

            if (workers.Length != 0) {
                await WorldGuestAsync(group, Text(value: workers[0]!["name"]), $"curl --fail --silent --show-error --max-time {configuration["lifecycle"]!["shutdownSeconds"]} -X POST http://127.0.0.1:{configuration["lifecycle"]!["healthPort"]}/drain\nsystemctl stop puck-world.service");
            }
            try { snapshot = await SnapshotWorldStoreAsync(endpoint: endpoint, owner: owner, token: token); } catch {
                if (workers.Length != 0) { await WorldGuestAsync(group, Text(value: workers[0]!["name"]), "systemctl start puck-world.service"); }
                throw;
            }
            Write(path: "artifacts/world-rollback.json", value: new JsonObject { ["endpoint"] = endpoint, ["owner"] = owner, ["blobs"] = snapshot.DeepClone() });
            try {
                await PublishWorldsAsync();
                await ApplyWorldComputeAsync(group, Text(value: configuration["name"]), parameterPath);
                await RetryAsync(action: async () => { await PuckAsync("world", "probe", host, port, "artifacts/world-silo.public-key"); }, attempts: 6, seconds: 5);
                await TestWorldReleaseAsync();
                await AzAsync("keyvault", "secret", "set", "--vault-name", vault, "--name", stateSecret, "--file", statePath, "-o", "none");
            } catch (Exception failure) {
                Console.Error.WriteLine(value: $"World deployment failed; restoring pre-release persistence and the last verified worker, when present: {failure.Message}");
                foreach (var worker in await WorkersAsync(group: group, scaleSet: Text(value: configuration["name"]))) {
                    await WorldGuestAsync(group, Text(value: worker!["name"]), "if systemctl cat puck-world.service >/dev/null 2>&1; then systemctl stop puck-world.service; fi\nif docker inspect puck-world >/dev/null 2>&1; then docker rm -f puck-world; fi");
                }
                await RestoreWorldStoreAsync(endpoint, owner, await TokenAsync(resource: "https://storage.azure.com/"), snapshot, temporary);
                if (previousRelease is null) {
                    await AzAsync("vmss", "scale", "-g", group, "--name", Text(value: configuration["name"]), "--new-capacity", "0", "-o", "none");
                    throw new InvalidOperationException(innerException: failure, message: "The first world release failed; pre-release persistence was restored and the scale set is empty for a clean retry.");
                }
                WriteParameters(path: parameterPath, values: previousRelease["parameters"]!.AsObject());
                await ApplyWorldComputeAsync(group, Text(value: configuration["name"]), parameterPath);
                var previousKey = Path.Combine(path1: temporary, path2: "previous.public-key");

                File.WriteAllBytes(previousKey, Convert.FromBase64String(s: Text(value: previousRelease["publicKey"])));
                var previousConfiguration = previousRelease["parameters"]!["configuration"]!;
                var previousHost = $"{previousConfiguration["dns"]!["recordName"]}.{previousConfiguration["dns"]!["zoneName"]}";

                await RetryAsync(async () => { await PuckAsync("world", "probe", previousHost, Text(value: previousConfiguration["port"]), previousKey); }, attempts: 12, seconds: 5);
                throw new InvalidOperationException(innerException: failure, message: "World release failed; the previous release and its persistence versions were restored.");
            }
        } finally {
            // This freshly generated path is never derived from arguments or deployment documents.
            Directory.Delete(path: temporary, recursive: true);
        }
    }
    private static async Task ApplyWorldComputeAsync(string group, string scaleSet, string parameters) {
        const string CompiledCompute = "artifacts/production-world-compute.json";
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" && !File.Exists(CompiledCompute)) { throw new FileNotFoundException("CI world deployment requires its compiled compute template."); }
        await AzAsync("deployment", "group", "create", "-g", group, "--name", (scaleSet + "-application"), "--template-file", File.Exists(CompiledCompute) ? CompiledCompute : "src/Puck.Azure.Resources/worldSiloCompute.bicep", "--parameters", ("@" + parameters), "-o", "none");
        await AzAsync("vmss", "update-instances", "-g", group, "--name", scaleSet, "--instance-ids", "*", "-o", "none");
    }
    private static async Task WorldGuestAsync(string group, string worker, string script) {
        var marker = ("puck-operation-" + Guid.NewGuid().ToString(format: "N"));
        var path = Path.Combine(path1: Path.GetTempPath(), path2: (marker + ".sh"));

        try {
            File.WriteAllText(contents: (((("set -eu\n" + script) + "\nprintf '%s\\n' '") + marker) + "'\n"), path: path);
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
        var blobs = await AzJsonAsync("storage", "blob", "list", "--account-name", new Uri(uriString: endpoint).Host.Split('.')[0],
            "--container-name", owner, "--auth-mode", "login", "--num-results", "*", "--query", "[].name", "-o", "json");

        return blobs.AsArray().Select(selector: Text).Where(predicate: MutableWorldBlob).ToArray();
    }
    private static HttpRequestMessage WorldBlobRequest(HttpMethod method, string endpoint, string owner, string name, string token, string? version = null) {
        var uri = ((((endpoint + "/") + Uri.EscapeDataString(stringToEscape: owner)) + "/") + string.Join(separator: '/', values: name.Split('/').Select(selector: Uri.EscapeDataString)));

        if (version is not null) { uri += ("?versionid=" + Uri.EscapeDataString(stringToEscape: version)); }
        var request = new HttpRequestMessage(method: method, requestUri: uri);

        request.Headers.Authorization = new AuthenticationHeaderValue(parameter: token, scheme: "Bearer");
        request.Headers.Add(name: "x-ms-version", value: "2025-11-05");
        request.Headers.Add(name: "x-ms-date", value: DateTime.UtcNow.ToString(format: "R", provider: System.Globalization.CultureInfo.InvariantCulture));
        return request;
    }
    private static async Task<JsonArray> SnapshotWorldStoreAsync(string endpoint, string owner, string token) {
        var snapshot = new JsonArray();

        foreach (var name in await WorldBlobNamesAsync(endpoint: endpoint, owner: owner)) {
            using var request = WorldBlobRequest(HttpMethod.Head, endpoint, owner, name, token);
            using var response = await Http.SendAsync(request: request);

            response.EnsureSuccessStatusCode();
            if (!response.Headers.TryGetValues(name: "x-ms-version-id", values: out var versions)) { throw new InvalidDataException(message: "World rollback requires Blob versioning before deployment."); }
            snapshot.Add(item: ((JsonNode)new JsonObject { ["name"] = name, ["version"] = versions.Single() }));
        }
        return snapshot;
    }
    private static async Task RestoreWorldStoreAsync(string endpoint, string owner, string token, JsonArray snapshot, string temporary) {
        var names = snapshot.Select(selector: row => Text(value: row!["name"])).ToHashSet(comparer: StringComparer.Ordinal);

        foreach (var name in await WorldBlobNamesAsync(endpoint: endpoint, owner: owner)) {
            if (names.Contains(item: name)) { continue; }
            using var request = WorldBlobRequest(HttpMethod.Delete, endpoint, owner, name, token);
            using var response = await Http.SendAsync(request: request);

            response.EnsureSuccessStatusCode();
        }
        foreach (var row in snapshot) {
            var name = Text(value: row!["name"]);

            if (!MutableWorldBlob(name: name)) { throw new InvalidDataException(message: "Rollback manifest contains a blob outside hosted-world mutable state."); }
            using var request = WorldBlobRequest(HttpMethod.Get, endpoint, owner, name, token, Text(value: row["version"]));
            using var response = await Http.SendAsync(request: request);

            response.EnsureSuccessStatusCode();
            var file = Path.Combine(path1: temporary, path2: "restore-blob");

            await File.WriteAllBytesAsync(file, await response.Content.ReadAsByteArrayAsync());
            await SendBlobAsync(endpoint, owner, name, file, token, (response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream"));
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
            var workers = await WorkersAsync(group, scaleSet);
            var transitioning = workers.Any(worker => ((string?)worker?["provisioningState"]) is "Creating" or "Updating" or "Deleting");
            if (!transitioning) { return workers; }
            if (System.Diagnostics.Stopwatch.GetElapsedTime(started) >= TimeSpan.FromMinutes(10)) {
                throw new InvalidOperationException("Azure worker replacement did not settle within ten minutes; no release was applied.");
            }
            if (!announced) { Console.WriteLine("Waiting for Azure's existing worker replacement to settle before selecting the authoritative worker."); announced = true; }
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
    }
    private static async Task TestWorldReleaseAsync() {
        var outputs = ((Options.ContainsKey(key: "resource-group") && Options.ContainsKey(key: "scale-set")) ? null : Outputs());
        var group = (Options.TryGetValue(key: "resource-group", value: out var explicitGroup) ? explicitGroup : Text(value: Value(key: "deploymentResourceGroupName", outputs: outputs!)));
        var scaleSet = (Options.TryGetValue(key: "scale-set", value: out var explicitScaleSet) ? explicitScaleSet : Text(value: Value(key: "worldSiloConfiguration", outputs: outputs!)["name"]));
        var image = (Options.TryGetValue(key: "image", value: out var explicitImage) ? explicitImage : File.ReadAllText(path: "artifacts/world-silo.digest").Trim());
        var workers = await WorkersAsync(group: group, scaleSet: scaleSet);

        if (workers.Length != 1) { throw new InvalidOperationException(message: "This release requires exactly one authoritative world worker."); }
        if (outputs is not null) {
            var expected = Value(key: "worldSiloConfiguration", outputs: outputs)["compute"]!["imageReference"]!;
            var actual = workers[0]!["storageProfile"]!["imageReference"]!;

            foreach (var field in new[] { "publisher", "offer", "sku", "version" }) {
                if (!Text(value: expected[field]).Equals(Text(value: actual[field]), StringComparison.OrdinalIgnoreCase)) {
                    throw new InvalidDataException(message: $"World worker OS image {field} differs from the declared release. Drain and replace the worker before completing the host image change.");
                }
            }
        }
        var result = await AzJsonAsync("vm", "run-command", "invoke", "-g", group, "-n", Text(value: workers[0]!["name"]), "--command-id", "RunShellScript", "--scripts", "cat /etc/puck/release; docker inspect --format '{{.Config.Image}} {{.State.Running}}' puck-world", "-o", "json");
        var message = string.Join(separator: "\n", values: result["value"]!.AsArray().Select(selector: value => Text(value: value!["message"])));

        if (!message.Contains(comparisonType: StringComparison.Ordinal, value: (image + " true")) || !message.Contains(comparisonType: StringComparison.Ordinal, value: (("\n" + image) + "\n"))) { throw new InvalidDataException(message: "The running world container does not match the requested release digest."); }
        await RetryAsync(async () => {
            var view = await AzJsonAsync("vm", "get-instance-view", "-g", group, "-n", Text(workers[0]!["name"]), "-o", "json");
            var state = Text(view["instanceView"]?["vmHealth"]?["status"]?["code"]);
            if (!state.Equals("HealthState/healthy", StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidDataException($"Azure reports world application health '{state}'; the release requires Healthy.");
            }
        }, attempts: 12, seconds: 5);
        if (outputs?["worldMcpConfiguration"]?["value"]?["options"] is { } mcp) {
            await RetryAsync(async () => {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var response = await Http.GetAsync(new Uri(new Uri(Text(mcp["publicUrl"])), "/healthz"), deadline.Token);
                response.EnsureSuccessStatusCode();
            }, attempts: 6, seconds: 5);
        }
    }
    private static async Task TestWorldMcpAsync() {
        const string Protocol = "2026-07-28";
        var options = Value(outputs: Outputs(), key: "worldMcpConfiguration")["options"]!;
        var address = new Uri(Text(options["publicUrl"]));
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(125) };
        using (var health = await client.GetAsync(new Uri(address, "/healthz"))) { health.EnsureSuccessStatusCode(); }
        var metadata = JsonNode.Parse(await client.GetStringAsync(new Uri(address, "/.well-known/oauth-protected-resource/mcp")))!;
        if (Text(metadata["resource"]) != address.AbsoluteUri || !metadata["scopes_supported"]!.AsArray().Any(scope => Text(scope) == Text(options["authorizationScope"]))) {
            throw new InvalidDataException("Protected-resource discovery differs from deployment policy.");
        }
        var assertion = await TokenAsync("api://" + Text(options["audience"]));
        var sequence = 0;
        async Task<JsonNode> RequestAsync(string method, string? tool = null, JsonObject? arguments = null, bool authenticated = true) {
            var parameters = new JsonObject {
                ["_meta"] = new JsonObject {
                    ["io.modelcontextprotocol/protocolVersion"] = Protocol,
                    ["io.modelcontextprotocol/clientInfo"] = new JsonObject { ["name"] = "puck-production-verification", ["version"] = "1" },
                    ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject(),
                },
            };
            if (tool is not null) { parameters["name"] = tool; parameters["arguments"] = arguments ?? new JsonObject(); }
            using var request = new HttpRequestMessage(HttpMethod.Post, address);
            if (authenticated) { request.Headers.Authorization = new("Bearer", assertion); }
            request.Headers.Accept.ParseAdd("application/json, text/event-stream");
            request.Headers.Add("MCP-Protocol-Version", Protocol);
            request.Headers.Add("Mcp-Method", method);
            if (tool is not null) { request.Headers.Add("Mcp-Name", tool); }
            request.Content = new StringContent(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = ++sequence, ["method"] = method, ["params"] = parameters }.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request);
            if (!authenticated) {
                if (response.StatusCode != HttpStatusCode.Unauthorized || !response.Headers.WwwAuthenticate.Any()) { throw new InvalidDataException("Anonymous MCP access was not challenged."); }
                return new JsonObject();
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized) { throw new InvalidOperationException("Live delegated authorization needs fresh sign-in, consent or claims satisfaction; credentials were not printed or substituted."); }
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync();
            if (body.Length > 1024 * 1024) { throw new InvalidDataException("MCP verification response exceeded its budget."); }
            var reply = response.Content.Headers.ContentType?.MediaType == "text/event-stream"
                ? body.Split('\n').Where(line => line.StartsWith("data:", StringComparison.Ordinal)).Select(line => JsonNode.Parse(line[5..])).Last(node => node?["id"]?.GetValue<int>() == sequence)!
                : JsonNode.Parse(body)!;
            if (reply["error"] is not null || reply["result"] is null) { throw new InvalidDataException("Live MCP returned a protocol error."); }
            var result = reply["result"]!;
            if (result["isError"]?.GetValue<bool>() == true) {
                throw new InvalidDataException($"Live MCP {tool} was refused: {result["content"]?[0]?["text"]}");
            }
            return result;
        }
        await RequestAsync("tools/list", authenticated: false);
        var tools = (await RequestAsync("tools/list"))["tools"]!.AsArray();
        if (tools.Any(tool => Text(tool!["name"]) == "puck_capture_frame")) { throw new InvalidDataException("The headless silo advertised capture."); }
        if (!tools.Any(tool => Text(tool!["name"]) == "puck_exec" && Text(tool["description"]).Contains("world.state", StringComparison.Ordinal))) { throw new InvalidDataException("Admitted command discovery is absent."); }
        Console.WriteLine("Public TLS, readiness, discovery, authentication and headless capability checks passed.");
        var onboard = await RequestAsync("tools/call", "puck_onboard");
        var state = Text(onboard["structuredContent"]?["state"]);
        if (state is not ("Ready" or "Migrating")) { throw new InvalidOperationException("Account onboarding is still running; retry this verification explicitly."); }
        Console.WriteLine("Function onboarding through user OBO passed.");
        if (tools.SingleOrDefault(tool => Text(tool!["name"]) == "puck_service_observe") is { } observe) {
            foreach (var name in observe["inputSchema"]!["properties"]!["observation"]!["enum"]!.AsArray()) {
                await RequestAsync("tools/call", "puck_service_observe", new() { ["observation"] = Text(name) });
            }
            Console.WriteLine("Granted ARM observations through user OBO passed.");
        }
        var attached = await RequestAsync("tools/call", "puck_attach");
        var attachment = Text(attached["structuredContent"]?["attachmentId"]);
        try {
            var samples = new double[30];
            for (var index = 0; index < samples.Length + 3; index++) {
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                await RequestAsync("tools/call", "puck_exec", new() { ["attachmentId"] = attachment, ["command"] = "world.state" });
                if (index >= 3) { samples[index - 3] = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds; }
            }
            Array.Sort(samples);
            Console.WriteLine($"Delegated World reads passed: 30 samples after 3 warmups; p50={samples[14]:F1} ms, p95={samples[28]:F1} ms. This measures this caller and network, not a service SLO.");
        } finally { await RequestAsync("tools/call", "puck_detach", new() { ["attachmentId"] = attachment }); }
    }
    private static async Task TestProductionAsync() {
        var commit = Commit();
        var outputs = Outputs();
        var configuration = Value(key: "worldSiloConfiguration", outputs: outputs);
        var group = Text(value: Value(key: "deploymentResourceGroupName", outputs: outputs));
        var address = Regex.Replace(input: Text(value: Value(key: "officialContentBaseUrl", outputs: outputs)), pattern: "/official/?$", replacement: "");

        await RetryAsync(action: async () => {
            var app = await AzJsonAsync("containerapp", "show", "--name", Text(value: Value(key: "actorsName", outputs: outputs)), "-g", group, "-o", "json");
            var properties = app["properties"]!;

            if ((Text(value: properties["latestReadyRevisionName"]) != Text(value: properties["latestRevisionName"])) || (((string?)properties["provisioningState"]) != "Succeeded")) { throw new InvalidOperationException(message: "Actors has not made the release revision ready."); }
            if (Text(value: properties["template"]!["containers"]![0]!["image"]) != File.ReadAllText(path: "artifacts/web-actors.digest").Trim()) { throw new InvalidDataException(message: "Actors image digest differs from the release."); }
            await TestWorldReleaseAsync();
            await PuckAsync("world", "probe", $"{configuration["dns"]!["recordName"]}.{configuration["dns"]!["zoneName"]}", Text(value: configuration["port"]), "artifacts/world-silo.public-key");
            if (((string?)(await GetJsonAsync(uri: (address + "/api/health-check")))["status"]) != "Healthy") { throw new InvalidOperationException(message: "Production API dependency health is not Healthy."); }
            if (Options.ContainsKey(key: "before-static-publication")) { return; }
            if (((string?)(await GetJsonAsync(uri: (address + "/release.json")))["commit"]) != commit) { throw new InvalidDataException(message: "Front Door dashboard release commit differs."); }
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
    private static async Task TestWorldContainerAsync() {
        var image = Required(key: "image");
        await DockerAsync("run", "--rm", "--user", "1655:1655", "--read-only", "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges", "--entrypoint", "/usr/bin/caddy", image, "version");
        var fixture = Path.GetFullPath(path: "artifacts/silo-smoke");

        if (Directory.Exists(path: fixture)) { throw new IOException(message: "Use a fresh silo smoke-test directory."); }
        const string Owner = "c3cba5cd-41c9-477e-a9de-15e1f4a4d1ec";
        const string Port = "7825";

        Directory.CreateDirectory(path: Path.Combine(path1: fixture, path2: "worlds"));
        using (var key = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256)) {
            File.WriteAllBytes(path: Path.Combine(path1: fixture, path2: "federation.pk8"), bytes: key.ExportPkcs8PrivateKey());
            File.WriteAllBytes(path: Path.Combine(path1: fixture, path2: "public-key"), bytes: key.ExportSubjectPublicKeyInfo());
        }
        await DockerAsync("create", "--name", "silo-source", image);
        try { await DockerAsync("cp", "silo-source:/app/worlds/.", Path.Combine(path1: fixture, path2: "worlds")); } finally { await DockerAsync("rm", "silo-source"); }
        foreach (var source in Directory.EnumerateFiles(path: Path.Combine(path1: fixture, path2: "worlds"))) {
            var name = Path.GetFileName(path: source).Replace(comparisonType: StringComparison.Ordinal, newValue: "", oldValue: ".world.json");
            var world = Read(path: source);

            if (name == "puck") { world["host"]!["authority"] = $"localhost:{Port}"; world["host"]!["listen"] = $"0.0.0.0:{Port}"; }
            Write(path: Path.Combine(path1: fixture, path2: $"store/{Owner}/private/puck/hosted/{name}/definition.json"), value: world);
        }
        var silo = SiloDocument(owner: Owner, world: "puck", keyFile: "/fixture/federation.pk8", store: new JsonObject { ["type"] = "directory", ["settings"] = new JsonObject { ["path"] = "/fixture/store" } });

        silo["stateDir"] = "/fixture/state";
        silo["lifecycle"] = new JsonObject {
            ["healthPort"] = 8081,
            ["shutdownSeconds"] = 120,
            ["progressTimeoutSeconds"] = 30,
            ["checkpointTimeoutSeconds"] = 180,
            ["journalTimeoutSeconds"] = 30,
            ["journalBacklogLimit"] = 1024,
        };
        Write(path: Path.Combine(path1: fixture, path2: "silo.json"), value: silo);
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
                if (!ready) { throw new InvalidOperationException($"Silo boot {boot} checkpointed but did not become ready: {healthReason}"); }
                using var liveness = await Http.GetAsync(requestUri: "http://127.0.0.1:8081/livez");

                liveness.EnsureSuccessStatusCode();
                using var azureHealth = await Http.GetAsync("http://127.0.0.1:8081/livez/azure");
                azureHealth.EnsureSuccessStatusCode();
                var richHealth = JsonNode.Parse(await azureHealth.Content.ReadAsStringAsync());
                if (azureHealth.Content.Headers.ContentType?.MediaType != "application/json" || Text(richHealth?["ApplicationHealthState"]) != "Healthy") {
                    throw new InvalidDataException("The world container does not satisfy Azure's rich application health contract.");
                }
                await PuckAsync("world", "probe", "127.0.0.1", Port, Path.Combine(path1: fixture, path2: "public-key"));
                previous = checkpoint;
                Console.WriteLine(value: $"PASS: Puck boot {boot} activated, checkpointed, and accepted an authenticated QUIC connection.");
            } finally {
                try { Console.WriteLine(value: await DockerAsync("logs", "silo-smoke")); } finally { await DockerAsync("rm", "-f", "silo-smoke"); }
            }
        }
    }
    private static async Task StageWorldImageAsync() {
        var commit = Commit();

        await PublishContainerAsync(archive: "", commit: commit, output: "artifacts/world-silo.digest", registry: "bytrccrp000", repository: "world-silo");
        CopyDirectory(destination: "artifacts/azure/silo-worlds", source: "artifacts/silo-smoke/worlds");
    }
    private static async Task BuildActorsAsync() {
        if ((await RunAsync(executable: "git", arguments: ["status", "--porcelain", "--untracked-files=normal"], capture: true)).Length != 0) { throw new InvalidOperationException(message: "Commit the release sources before building an image."); }
        var commit = await RunAsync(executable: "git", arguments: ["rev-parse", "HEAD"], capture: true);

        await DockerAsync("build", "--file", "src/Puck.Actors/Dockerfile", "--tag", $"puck/web-actors:{commit}", ".");
        var digestFile = Path.Combine(path1: Path.GetTempPath(), path2: $"puck-actors-{Guid.NewGuid():N}.txt");

        try {
            await PublishContainerAsync(registry: Option(fallback: "bytrccrp000", key: "registry"), repository: "web-actors", commit: commit, archive: "", output: digestFile);
            if (!Options.ContainsKey(key: "no-restart")) {
                await AzAsync("containerapp", "update", "--name", Option(fallback: "bytrccap001", key: "container-app"), "-g", Option(fallback: "byteterrace", key: "resource-group"), "--image", File.ReadAllText(path: digestFile).Trim(), "--revision-suffix", $"git-{commit[..12]}", "-o", "none");
            }
        } finally { File.Delete(path: digestFile); }
    }
}
