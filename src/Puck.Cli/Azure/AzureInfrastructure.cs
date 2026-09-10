using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Puck.Cli.Azure;

internal static partial class AzureCommand {
    private static async Task SetOwnerAsync(string group) {
        // The shared CI identity that federates to GitHub's environment; it exists before any deployment output does.
        Environment.SetEnvironmentVariable(variable: "BICEPPARAM_OWNER_OBJECT_ID", value: await AzAsync("identity", "show", "--name", "bytrcidpzzz", "-g", group, "--query", "principalId", "-o", "tsv"));
        Environment.SetEnvironmentVariable(value: "ServicePrincipal", variable: "BICEPPARAM_OWNER_PRINCIPAL_TYPE");
    }
    private static async Task DeployInfrastructureAsync(string group, string? infrastructure) {
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
        if (string.IsNullOrEmpty(value: infrastructure)) {
            if (CliGitHub.IsActions) { throw new InvalidOperationException(message: "CI deployment requires its compiled infrastructure artifact."); }
            await AzAsync("bicep", "build", "--file", "src/Puck.Azure.Resources/main.bicep", "--outfile", "artifacts/production-infrastructure.json");
            await AzAsync("bicep", "build-params", "--file", "src/Puck.Azure.Resources/main.bicepparam", "--outfile", "artifacts/production-parameters.json");
        } else {
            if (Text(value: CliFiles.ReadJson(path: Path.Combine(path1: infrastructure, path2: "source.json"))["commit"]) != await RunAsync(arguments: ["rev-parse", "HEAD"], capture: true, executable: "git")) { throw new InvalidDataException(message: "Infrastructure artifacts belong to another commit."); }
            File.Copy(destFileName: "artifacts/production-infrastructure.json", overwrite: true, sourceFileName: Path.Combine(path1: infrastructure, path2: "infrastructure.json"));
            File.Copy(destFileName: "artifacts/production-parameters.json", overwrite: true, sourceFileName: Path.Combine(path1: infrastructure, path2: "parameters.json"));
            File.Copy(destFileName: "artifacts/production-world-compute.json", overwrite: true, sourceFileName: Path.Combine(path1: infrastructure, path2: "world-silo-compute.json"));
        }
        var parameters = CliFiles.ReadJson(path: "artifacts/production-parameters.json");

        parameters["parameters"]!["owner"]!["value"] = new JsonObject { ["objectId"] = owner, ["principalType"] = "ServicePrincipal" };
        var name = Text(value: parameters["parameters"]!["resources"]!["value"]!["actors"]!["name"]);
        var image = await AzAsync("containerapp", "show", "--name", name, "-g", group, "--query", "properties.template.containers[0].image", "-o", "tsv");

        parameters["parameters"]!["actorsImage"] = new JsonObject { ["value"] = image };
        CliFiles.WriteJson(path: "artifacts/production-parameters.json", value: parameters);
        var plan = await AzJsonAsync("deployment", "group", "what-if", "-g", group, "--template-file", "artifacts/production-infrastructure.json", "--parameters", "artifacts/production-parameters.json", "--no-pretty-print");

        CliFiles.WriteJson(path: "artifacts/production-plan.json", value: plan);
        if (((string?)plan["status"]) != "Succeeded") { throw new InvalidOperationException(message: "Production infrastructure planning failed."); }
        if (plan["changes"]!.AsArray().Any(predicate: change => (((string?)change!["changeType"]) == "Delete"))) { throw new InvalidOperationException(message: "Production plan includes resource deletion; inspect the retained plan."); }
        CliFiles.WriteJson(path: "artifacts/production-outputs.json", value: await AzJsonAsync("deployment", "group", "create", "--name", "puck-production-platform", "-g", group, "--template-file", "artifacts/production-infrastructure.json", "--parameters", "artifacts/production-parameters.json", "--query", "properties.outputs", "-o", "json"));
    }
    private static async Task DeployWorldPlatformAsync(string group) {
        await SetOwnerAsync(group: group);
        Directory.CreateDirectory(path: "artifacts");
        await AzAsync("bicep", "build-params", "--file", "src/Puck.Azure.Resources/main.bicepparam", "--outfile", "artifacts/world-platform-source.json");
        var source = CliFiles.ReadJson(path: "artifacts/world-platform-source.json")["parameters"]!;
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
        CliFiles.WriteJson(path: "artifacts/production-outputs.json", value: outputs);
    }
    private static async Task DeployWorldMcpAsync(string group, bool planOnly) {
        await AzAsync("bicep", "build-params", "--file", "src/Puck.Azure.Resources/main.bicepparam", "--outfile", "artifacts/world-mcp-source.json");
        var source = CliFiles.ReadJson(path: "artifacts/world-mcp-source.json")["parameters"]!;
        var settings = (source["worldMcp"]?["value"] ?? throw new InvalidOperationException(message: "Set BICEPPARAM_WORLD_MCP to the explicit participant policy."));
        var outputs = await AzJsonAsync("deployment", "group", "show", "-g", group, "-n", "puck-world-platform", "--query", "properties.outputs", "-o", "json");
        var configuration = Value(key: "worldSiloConfiguration", outputs: outputs);
        var applicationId = Text(value: configuration["authentication"]!["settings"]!["audience"]);
        var application = await AzJsonAsync("ad", "app", "show", "--id", applicationId, "--query", "{uniqueName:uniqueName,api:api,identifierUris:identifierUris}", "-o", "json");

        if (!application["api"]!["oauth2PermissionScopes"]!.AsArray().Any(predicate: scope => ((Text(value: scope!["value"]) == "user_impersonation") && scope["isEnabled"]!.GetValue<bool>()))) {
            throw new InvalidOperationException(message: "The existing API must expose enabled user_impersonation before MCP deployment.");
        }
        var resources = source["resources"]!["value"]!;
        var identifier = Text(value: resources["applicationRegistration"]!["identifierUri"]);

        if (!application["identifierUris"]!.AsArray().Any(predicate: uri => (Text(value: uri) == identifier))) { throw new InvalidOperationException(message: "The configured API identifier does not match the deployed registration."); }
        var route = resources["frontDoor"]!["routes"]!.AsArray().First(predicate: row => (Text(value: row!["originGroupName"]) == "api"))!;

        WriteParameters(path: "artifacts/world-mcp-parameters.json", values: new JsonObject {
            ["settings"] = settings.DeepClone(),
            ["configuration"] = configuration.DeepClone(),
            ["applicationUniqueName"] = Text(value: application["uniqueName"]),
            ["applicationId"] = applicationId,
            ["authorizationScope"] = (identifier + "/user_impersonation"),
            ["identityClientId"] = Value(key: "worldSiloClientId", outputs: outputs).DeepClone(),
            ["identityPrincipalId"] = Value(key: "worldSiloOwner", outputs: outputs).DeepClone(),
            ["onboardingUrl"] = (("https://" + Text(value: route["customDomains"]![0])) + "/api/self-onboard"),
            ["applicationInsightsName"] = resources["containerEnvironment"]!["applicationInsights"]!["name"]!.DeepClone(),
            ["location"] = Value(key: "deploymentLocation", outputs: outputs).DeepClone(),
            ["tags"] = Value(key: "deploymentTags", outputs: outputs).DeepClone(),
        });
        var plan = await AzJsonAsync("deployment", "group", "what-if", "-g", group, "--template-file", "src/Puck.Azure.Resources/worldMcp.bicep", "--parameters", "@artifacts/world-mcp-parameters.json", "--no-pretty-print");

        CliFiles.WriteJson(path: "artifacts/world-mcp-plan.json", value: plan);
        if ((((string?)plan["status"]) != "Succeeded") || plan["changes"]!.AsArray().Any(predicate: change => (((string?)change!["changeType"]) == "Delete"))) {
            throw new InvalidOperationException(message: "MCP deployment planning failed or proposes deletion; inspect world-mcp-plan.json.");
        }
        if (planOnly) { return; }
        var deployed = await AzJsonAsync("deployment", "group", "create", "-g", group, "-n", "puck-world-mcp", "--template-file", "src/Puck.Azure.Resources/worldMcp.bicep", "--parameters", "@artifacts/world-mcp-parameters.json", "--query", "properties.outputs", "-o", "json");

        outputs["worldMcpConfiguration"] = deployed["worldMcpConfiguration"]!.DeepClone();
        CliFiles.WriteJson(path: "artifacts/production-outputs.json", value: outputs);
    }
    private static void WriteParameters(string path, JsonObject values) {
        var parameters = new JsonObject();

        foreach (var item in values) { parameters[item.Key] = new JsonObject { ["value"] = item.Value?.DeepClone() }; }
        CliFiles.WriteJson(path: path, value: new JsonObject { ["parameters"] = parameters });
    }
    private static async Task PublishTemplateSpecsAsync() {
        const string Sources = "src/Puck.Azure.Resources";
        const string Destination = "artifacts/template-specs";
        var alias = CliFiles.ReadJson(path: $"{Sources}/bicepconfig.json")["moduleAliases"]!["ts"]!["bvm"]!;
        var subscription = Text(value: alias["subscription"]);
        var group = Text(value: alias["resourceGroup"]);

        Directory.CreateDirectory(path: Destination);
        var location = await AzAsync("group", "show", "--name", group, "--subscription", subscription, "--query", "location", "-o", "tsv");
        var existing = await AzJsonAsync("ts", "list", "-g", group, "--subscription", subscription, "--query", "[].name", "-o", "json");

        foreach (var spec in CliFiles.ReadJson(path: $"{Sources}/template-specs.json").AsArray()) {
            var name = Text(value: spec!["name"]);
            var version = Text(value: spec["version"]);
            var template = $"{Destination}/{name}.json";

            await AzAsync("bicep", "build", "--file", Path.Combine(path1: Sources, path2: Text(value: spec["path"])), "--outfile", template);
            var versions = (existing.AsArray().Any(predicate: value => (Text(value: value) == name))
                ? await AzJsonAsync("ts", "show", "--name", name, "-g", group, "--subscription", subscription, "--query", "versions", "-o", "json") : new JsonArray());

            if (versions.AsArray().Any(predicate: value => (Text(value: value) == version))) {
                var token = await AzAsync("account", "get-access-token", "--subscription", subscription, "--query", "accessToken", "-o", "tsv");

                CliGitHub.Mask(value: token);
                using var request = new HttpRequestMessage(method: HttpMethod.Get, requestUri: $"https://management.azure.com/subscriptions/{subscription}/resourceGroups/{group}/providers/Microsoft.Resources/templateSpecs/{name}/versions/{version}?api-version=2022-02-01");

                request.Headers.Authorization = new AuthenticationHeaderValue(parameter: token, scheme: "Bearer");
                using var response = await Http.SendAsync(request: request);

                response.EnsureSuccessStatusCode();
                var published = JsonNode.Parse(json: await response.Content.ReadAsStringAsync())!["properties"]!["mainTemplate"]!;
                var compiled = CliFiles.ReadJson(path: template);

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
}
