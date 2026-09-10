using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Puck.Cli.Azure;

internal static partial class AzureCommand {
    private static async Task PublishContainerAsync(string archive, string commit, string output, string registry, string repository) {
        if ((repository is not ("web-actors" or "world-silo")) || !Regex.IsMatch(input: commit, pattern: CommitPattern)) { throw new ArgumentException(message: "Invalid repository or commit."); }
        var server = await AzAsync("acr", "show", "--name", registry, "--query", "loginServer", "-o", "tsv");
        var tenant = await AzAsync("account", "show", "--query", "tenantId", "-o", "tsv");
        var token = await TokenAsync(resource: "https://containerregistry.azure.net");
        using var form = new FormUrlEncodedContent(nameValueCollection: new Dictionary<string, string> { ["grant_type"] = "access_token", ["service"] = server, ["tenant"] = tenant, ["access_token"] = token });
        using var response = await Http.PostAsync(content: form, requestUri: $"https://{server}/oauth2/exchange");

        response.EnsureSuccessStatusCode();
        var refresh = Text(value: JsonNode.Parse(json: await response.Content.ReadAsStringAsync())!["refresh_token"]);

        CliGitHub.Mask(value: refresh);
        var image = $"{server}/{repository}:{commit}";

        try {
            await RunAsync(arguments: ["login", server, "--username", "00000000-0000-0000-0000-000000000000", "--password-stdin"], executable: "docker", input: (refresh + "\n"));
            if (archive.Length != 0) { await DockerAsync("load", "--input", archive); }
            await DockerAsync("tag", $"puck/{repository}:{commit}", image);
            await DockerAsync("push", image);
            var inspected = JsonNode.Parse(json: await DockerAsync("image", "inspect", image))!;
            var digest = inspected[0]!["RepoDigests"]!.AsArray().Select(selector: value => Text(value: value)).Single(predicate: value => value.StartsWith(comparisonType: StringComparison.Ordinal, value: $"{server}/{repository}@sha256:"));

            if (output.Length != 0) { File.WriteAllText(contents: (digest + "\n"), path: output); }
            Console.WriteLine(value: digest);
        } finally { await DockerAsync("logout", server); }
    }
    private static async Task DeployApplicationsAsync(string artifacts, string commit, string group) {
        var bundle = Path.Combine(path1: artifacts, path2: "azure-applications");

        await PuckAsync("bundle", "verify", bundle, commit);
        if (Directory.Exists(path: "artifacts/azure")) { throw new IOException(message: "Use a fresh deployment workspace."); }
        CliFiles.CopyDirectory(destination: "artifacts/azure", source: bundle);
        CliFiles.WriteJson(path: "artifacts/release-source.json", value: new JsonObject { ["commit"] = commit, ["runId"] = Environment.GetEnvironmentVariable(variable: "GITHUB_RUN_ID") });
        foreach (var repository in new[] { "web-actors", "world-silo" }) {
            await PublishContainerAsync(archive: Path.Combine(path1: artifacts, path2: $"azure-{repository}/image.tar.gz"), commit: commit, output: $"artifacts/{repository}.digest", registry: ResourceName("containerRegistry", "name"), repository: repository);
        }
        var configuration = CliFiles.ReadJson(path: "artifacts/azure/configuration.json");
        var outputs = Outputs();

        foreach (var item in configuration["items"]!.AsArray()) {
            if (((string?)item!["key"]) == "Onboarding:ActorsBaseUrl") { item["value"] = Value(key: "actorsEndpoint", outputs: outputs).DeepClone(); }
        }
        CliFiles.WriteJson(path: "artifacts/production-configuration.json", value: configuration);
        // The import returns nothing, so its output stays on the console rather than being captured.
        await RunAsync(arguments: ["appconfig", "kv", "import", "--name", ResourceName("configurationStore", "name"), "--auth-mode", "login", "--source", "file", "--path", "artifacts/production-configuration.json", "--format", "json", "--profile", "appconfig/kvset", "--yes", "-o", "none"], executable: "az");
        var before = await AzJsonAsync("containerapp", "show", "--name", ResourceName("actors", "name"), "-g", group, "-o", "json");

        CliFiles.WriteJson(path: "artifacts/previous-actor-release.json", value: new JsonObject { ["revision"] = before["properties"]!["latestReadyRevisionName"]!.DeepClone(), ["image"] = before["properties"]!["template"]!["containers"]![0]!["image"]!.DeepClone() });
        await AzAsync("containerapp", "update", "--name", ResourceName("actors", "name"), "-g", group, "--image", File.ReadAllText(path: "artifacts/web-actors.digest").Trim(), "--revision-suffix", $"git-{commit[..12]}-{Environment.GetEnvironmentVariable(variable: "GITHUB_RUN_ID")}-{Environment.GetEnvironmentVariable(variable: "GITHUB_RUN_ATTEMPT")}", "-o", "none");
        CliGitHub.Output(key: "function-app-name", value: ResourceName("api", "functionApplication", "name"));
    }
    private static async Task<string> RunnerIpAsync() {
        var text = (await Http.GetStringAsync(requestUri: "https://api.ipify.org")).Trim();

        if (!IPAddress.TryParse(address: out var ip, ipString: text) || (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)) { throw new InvalidDataException(message: "Expected the hosted runner IPv4 address."); }
        return text;
    }
    private static async Task FunctionsAccessAsync(string group, string? name, bool restore) {
        const string Snapshot = "artifacts/functions-scm-access.json";

        if (restore) {
            if (!File.Exists(path: Snapshot)) { return; }
            var saved = CliFiles.ReadJson(path: Snapshot);

            await AzAsync("rest", "--method", "put", "--url", Text(value: saved["url"]), "--body", ("@" + Text(value: saved["bodyFile"])), "-o", "none");
            return;
        }
        name ??= ResourceName("api", "functionApplication", "name");
        var id = await AzAsync("functionapp", "show", "--name", name, "-g", group, "--query", "id", "-o", "tsv");
        var url = $"https://management.azure.com{id}/config/web?api-version=2024-04-01";
        var config = await AzJsonAsync("rest", "--method", "get", "--url", url, "-o", "json");
        var properties = new JsonObject();

        foreach (var key in new[] { "scmIpSecurityRestrictions", "scmIpSecurityRestrictionsDefaultAction", "scmIpSecurityRestrictionsUseMain" }) { properties[key] = config["properties"]![key]?.DeepClone(); }
        CliFiles.WriteJson(path: "artifacts/functions-scm-restore.json", value: new JsonObject { ["properties"] = properties });
        CliFiles.WriteJson(path: Snapshot, value: new JsonObject { ["url"] = url, ["bodyFile"] = "artifacts/functions-scm-restore.json" });
        var ip = await RunnerIpAsync();

        await AzAsync("webapp", "config", "access-restriction", "add", "-g", group, "--name", name, "--scm-site", "true", "--rule-name", $"gha-{Environment.GetEnvironmentVariable(variable: "GITHUB_RUN_ID")}", "--action", "Allow", "--ip-address", (ip + "/32"), "--priority", "100", "-o", "none");
        await AzAsync("webapp", "config", "access-restriction", "set", "-g", group, "--name", name, "--use-same-restrictions-for-scm-site", "false", "--scm-default-action", "Deny", "-o", "none");
    }
    private static async Task VaultAccessAsync(bool restore) {
        const string Snapshot = "artifacts/key-vault-access.json";

        if (restore) {
            if (!File.Exists(path: Snapshot)) { return; }
            var saved = CliFiles.ReadJson(path: Snapshot);

            if (((bool?)saved["added"]) == true) { await AzAsync("keyvault", "network-rule", "remove", "--name", Text(value: saved["vault"]), "--ip-address", Text(value: saved["ip"]), "-o", "none"); }
            return;
        }
        var vault = Text(value: Value(outputs: Outputs(), key: "deploymentKeyVaultName"));
        var ip = await RunnerIpAsync();
        var rules = await AzJsonAsync("keyvault", "show", "--name", vault, "--query", "properties.networkAcls.ipRules[].value", "-o", "json");
        var added = !rules.AsArray().Any(predicate: rule => ((Text(value: rule) == ip) || (Text(value: rule) == (ip + "/32"))));

        CliFiles.WriteJson(path: Snapshot, value: new JsonObject { ["vault"] = vault, ["ip"] = (ip + "/32"), ["added"] = added });
        if (added) { await AzAsync("keyvault", "network-rule", "add", "--name", vault, "--ip-address", (ip + "/32"), "-o", "none"); }
    }
    private static async Task StageWorldImageAsync(string commit) {
        await PublishContainerAsync(archive: "", commit: commit, output: "artifacts/world-silo.digest", registry: ResourceName("containerRegistry", "name"), repository: "world-silo");
        CliFiles.CopyDirectory(destination: "artifacts/azure/silo-worlds", source: "artifacts/silo-smoke/worlds");
    }
    private static async Task BuildActorsAsync(string? containerApp, string group, bool noRestart, string? registry) {
        if ((await RunAsync(arguments: ["status", "--porcelain", "--untracked-files=normal"], capture: true, executable: "git")).Length != 0) { throw new InvalidOperationException(message: "Commit the release sources before building an image."); }
        var commit = await RunAsync(arguments: ["rev-parse", "HEAD"], capture: true, executable: "git");

        await DockerAsync("build", "--file", "src/Puck.Actors/Dockerfile", "--tag", $"puck/web-actors:{commit}", ".");
        var digestFile = Path.Combine(path1: Path.GetTempPath(), path2: $"puck-actors-{Guid.NewGuid():N}.txt");

        try {
            await PublishContainerAsync(archive: "", commit: commit, output: digestFile, registry: (registry ?? ResourceName("containerRegistry", "name")), repository: "web-actors");
            if (!noRestart) {
                await AzAsync("containerapp", "update", "--name", (containerApp ?? ResourceName("actors", "name")), "-g", group, "--image", File.ReadAllText(path: digestFile).Trim(), "--revision-suffix", $"git-{commit[..12]}", "-o", "none");
            }
        } finally { File.Delete(path: digestFile); }
    }
}
