using System.CommandLine;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Puck.Cli.Azure;

/// <summary>
/// <c>puck azure</c> — the production deployment: build the application bundle, reconcile the platform, publish
/// containers, the world, Functions, and the website, and verify the release through Front Door. Every verb runs
/// from the repository root and reads the deployment outputs and compiled parameters the platform reconcile leaves
/// under <c>artifacts/</c>.
/// </summary>
internal static partial class AzureCommand {
    private const string CommitPattern = "\\A[a-f0-9]{40}\\z";
    private const string DefaultResourceGroup = "byteterrace";

    private static readonly HttpClient Http = new(handler: new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromMinutes(minutes: 5) };

    public static Command Create() {
        var resourceGroupOption = new Option<string>(name: "--resource-group") { DefaultValueFactory = _ => DefaultResourceGroup, Description = "The production resource group." };
        var commitOption = new Option<string>(name: "--commit") { Description = "The full lowercase commit SHA of this release.", Required = true, Validators = { result => ValidateCommit(result: result) } };
        var build = new Command(description: "Build the application bundle: Functions, the browser engine, the official tree, hosted worlds, the dashboard, and the documentation, with its release manifest.", name: "build") {
            new Option<string>(name: "--output-directory") { DefaultValueFactory = _ => "artifacts/azure", Description = "A fresh directory for the bundle." },
            new Option<string?>(name: "--runtime-artifacts") { Description = "This commit's compiled Functions and browser payloads, when a producer job already built them." },
        };
        var buildInfrastructure = new Command(description: "Compile the platform and world-compute templates and the parameters for this commit.", name: "build-infrastructure") {
            new Option<string>(name: "--output-directory") { DefaultValueFactory = _ => "artifacts/infrastructure", Description = "A fresh directory for the compiled templates." },
        };
        var deployInfrastructure = new Command(description: "Plan and reconcile the production platform from the compiled templates, refusing a plan that deletes a resource.", name: "deploy-infrastructure") {
            resourceGroupOption, new Option<string?>(name: "--infrastructure-artifacts") { Description = "This commit's compiled templates; required in CI." },
        };
        var deployApplications = new Command(description: "Verify the bundle, publish both container images, apply application configuration, and roll the actors revision.", name: "deploy-applications") {
            new Option<string>(name: "--artifacts-directory") { Description = "The downloaded artifacts holding azure-applications and the container images.", Required = true }, commitOption, resourceGroupOption,
        };
        var deployWorldPlatform = new Command(description: "Deploy the world platform composition alone and record its outputs.", name: "deploy-world-platform") { resourceGroupOption };
        var deployWorldMcp = new Command(description: "Plan and deploy the world MCP policy against the deployed platform.", name: "deploy-world-mcp") {
            resourceGroupOption, new Option<bool>(name: "--plan-only") { Description = "Stop after the what-if plan." },
        };
        var deployWorld = new Command(description: "Deploy the primary world transactionally: snapshot persistence, roll the worker, verify, and roll back on failure.", name: "deploy-world") {
            new Option<string?>(name: "--resource-group") { Description = "The resource group; defaults to the deployment output." },
        };
        var publishStatic = new Command(description: "Publish official content and the website from the bundle.", name: "publish-static") {
            new Option<string>(name: "--bundle-directory") { DefaultValueFactory = _ => "artifacts/azure", Description = "The verified bundle." },
        };
        var publishTemplateSpecs = new Command(description: "Publish the Template Specs the platform composes from, refusing a changed version.", name: "publish-template-specs");
        var publishContainer = new Command(description: "Push one built image to the registry and print its digest.", name: "publish-container") {
            new Option<string>(name: "--registry") { Description = "The container registry name.", Required = true },
            new Option<string>(name: "--repository") { Description = "web-actors or world-silo.", Required = true },
            commitOption,
            new Option<string?>(name: "--archive") { Description = "A saved image archive to load first." },
            new Option<string?>(name: "--output-file") { Description = "Where to write the pushed digest." },
        };
        var functionsAccess = new Command(description: "Admit this runner to the Functions deployment endpoint, or restore the saved restrictions.", name: "functions-access") {
            new Option<bool>(name: "--restore") { Description = "Restore the restrictions saved by the admitting run." }, resourceGroupOption,
            new Option<string?>(name: "--name") { Description = "The function app; defaults to the compiled parameters." },
        };
        var vaultAccess = new Command(description: "Admit this runner to the signing-key vault, or remove the rule it added.", name: "vault-access") {
            new Option<bool>(name: "--restore") { Description = "Remove the rule the admitting run added." },
        };
        var testProduction = new Command(description: "Verify the deployed release end to end through Front Door.", name: "test-production") {
            commitOption, new Option<bool>(name: "--before-static-publication") { Description = "Verify services only; the website is not published yet." },
        };
        var testWorldContainer = new Command(description: "Boot the world image twice against a fixture store and prove activation, checkpointing, health, and QUIC.", name: "test-world-container") {
            new Option<string>(name: "--image") { Description = "The built world-silo image.", Required = true },
        };
        var testWorldRelease = new Command(description: "Verify the running world worker matches the requested release digest and reports healthy.", name: "test-world-release") {
            new Option<string?>(name: "--resource-group") { Description = "With --scale-set, verify without deployment outputs." },
            new Option<string?>(name: "--scale-set") { Description = "The worker scale set." },
            new Option<string?>(name: "--image") { Description = "The expected digest; defaults to artifacts/world-silo.digest." },
        };
        var testWorldMcp = new Command(description: "Exercise the live MCP endpoint with delegated authorization.", name: "test-world-mcp");
        var buildActors = new Command(description: "Build, publish, and roll the actors image from this commit; resource names come from artifacts/production-parameters.json (build-infrastructure).", name: "build-actors") {
            new Option<bool>(name: "--no-restart") { Description = "Publish without rolling the container app." },
            new Option<string?>(name: "--registry") { Description = "The registry; defaults to the compiled parameters." },
            resourceGroupOption,
            new Option<string?>(name: "--container-app") { Description = "The actors app; defaults to the compiled parameters." },
        };
        var stageWorldImage = new Command(description: "Publish the world image and stage its worlds for a deployment; the registry name comes from artifacts/production-parameters.json (build-infrastructure).", name: "stage-world-image") { commitOption };
        var current = new Command(description: "Set the deploy output to whether GITHUB_SHA is still the tip of GITHUB_REF.", name: "current");
        var command = new Command(description: "Build, deploy, publish, and verify Puck's Azure production.", name: "azure") {
            build, buildInfrastructure, deployInfrastructure, deployApplications, deployWorldPlatform, deployWorldMcp, deployWorld, publishStatic, publishTemplateSpecs, publishContainer,
            functionsAccess, vaultAccess, testProduction, testWorldContainer, testWorldRelease, testWorldMcp, buildActors, stageWorldImage, current,
        };

        build.SetAction(action: (parseResult, _) => RunAsync(action: () => BuildAsync(output: Path.GetFullPath(path: parseResult.GetRequiredValue<string>(name: "--output-directory")), runtimeArtifacts: parseResult.GetValue<string?>(name: "--runtime-artifacts"))));
        buildInfrastructure.SetAction(action: (parseResult, _) => RunAsync(action: () => BuildInfrastructureAsync(output: Path.GetFullPath(path: parseResult.GetRequiredValue<string>(name: "--output-directory")))));
        deployInfrastructure.SetAction(action: (parseResult, _) => RunAsync(action: () => DeployInfrastructureAsync(group: parseResult.GetRequiredValue(option: resourceGroupOption), infrastructure: parseResult.GetValue<string?>(name: "--infrastructure-artifacts"))));
        deployApplications.SetAction(action: (parseResult, _) => RunAsync(action: () => DeployApplicationsAsync(artifacts: parseResult.GetRequiredValue<string>(name: "--artifacts-directory"), commit: parseResult.GetRequiredValue(option: commitOption), group: parseResult.GetRequiredValue(option: resourceGroupOption))));
        deployWorldPlatform.SetAction(action: (parseResult, _) => RunAsync(action: () => DeployWorldPlatformAsync(group: parseResult.GetRequiredValue(option: resourceGroupOption))));
        deployWorldMcp.SetAction(action: (parseResult, _) => RunAsync(action: () => DeployWorldMcpAsync(group: parseResult.GetRequiredValue(option: resourceGroupOption), planOnly: parseResult.GetValue<bool>(name: "--plan-only"))));
        deployWorld.SetAction(action: (parseResult, _) => RunAsync(action: () => DeployWorldAsync(group: parseResult.GetValue<string?>(name: "--resource-group"))));
        publishStatic.SetAction(action: (parseResult, _) => RunAsync(action: () => PublishStaticAsync(bundle: parseResult.GetRequiredValue<string>(name: "--bundle-directory"))));
        publishTemplateSpecs.SetAction(action: (_, _) => RunAsync(action: PublishTemplateSpecsAsync));
        publishContainer.SetAction(action: (parseResult, _) => RunAsync(action: () => PublishContainerAsync(archive: (parseResult.GetValue<string?>(name: "--archive") ?? ""), commit: parseResult.GetRequiredValue(option: commitOption), output: (parseResult.GetValue<string?>(name: "--output-file") ?? ""), registry: parseResult.GetRequiredValue<string>(name: "--registry"), repository: parseResult.GetRequiredValue<string>(name: "--repository"))));
        functionsAccess.SetAction(action: (parseResult, _) => RunAsync(action: () => FunctionsAccessAsync(group: parseResult.GetRequiredValue(option: resourceGroupOption), name: parseResult.GetValue<string?>(name: "--name"), restore: parseResult.GetValue<bool>(name: "--restore"))));
        vaultAccess.SetAction(action: (parseResult, _) => RunAsync(action: () => VaultAccessAsync(restore: parseResult.GetValue<bool>(name: "--restore"))));
        testProduction.SetAction(action: (parseResult, _) => RunAsync(action: () => TestProductionAsync(beforeStaticPublication: parseResult.GetValue<bool>(name: "--before-static-publication"), commit: parseResult.GetRequiredValue(option: commitOption))));
        testWorldContainer.SetAction(action: (parseResult, _) => RunAsync(action: () => TestWorldContainerAsync(image: parseResult.GetRequiredValue<string>(name: "--image"))));
        testWorldRelease.SetAction(action: (parseResult, _) => RunAsync(action: () => TestWorldReleaseAsync(group: parseResult.GetValue<string?>(name: "--resource-group"), image: parseResult.GetValue<string?>(name: "--image"), scaleSet: parseResult.GetValue<string?>(name: "--scale-set"))));
        testWorldMcp.SetAction(action: (_, _) => RunAsync(action: TestWorldMcpAsync));
        buildActors.SetAction(action: (parseResult, _) => RunAsync(action: () => BuildActorsAsync(containerApp: parseResult.GetValue<string?>(name: "--container-app"), group: parseResult.GetRequiredValue(option: resourceGroupOption), noRestart: parseResult.GetValue<bool>(name: "--no-restart"), registry: parseResult.GetValue<string?>(name: "--registry"))));
        stageWorldImage.SetAction(action: (parseResult, _) => RunAsync(action: () => StageWorldImageAsync(commit: parseResult.GetRequiredValue(option: commitOption))));
        current.SetAction(action: (_, _) => RunAsync(action: CurrentAsync));
        return command;
    }

    private static void ValidateCommit(System.CommandLine.Parsing.OptionResult result) {
        if (!Regex.IsMatch(input: (result.GetValueOrDefault<string>() ?? ""), pattern: CommitPattern)) { result.AddError(errorMessage: "Expected a full lowercase commit SHA."); }
    }
    private static string Root() {
        var root = (RepositoryPaths.FindRoot() ?? throw new DirectoryNotFoundException(message: "Run within the Puck checkout."));

        if (Path.GetFullPath(path: Environment.CurrentDirectory) != root) { throw new InvalidOperationException(message: "Run puck azure from the repository root."); }
        return root;
    }
    private static async Task<int> RunAsync(Func<Task> action) {
        try {
            Root();
            await action();
            return 0;
        } catch (Exception error) {
            Console.Error.WriteLine(value: $"azure: {error.Message}");
            return 1;
        }
    }
    private static async Task<string> RunAsync(string executable, IEnumerable<string> arguments, string? directory = null, bool capture = false, string? input = null) =>
        (await CliProcess.RunCheckedAsync(arguments: arguments, capture: capture, executable: executable, input: input, root: (directory ?? Root()))).Trim();
    private static async Task PuckAsync(params string[] arguments) {
        if (await PuckRootCommand.InvokeAsync(args: arguments) != 0) { throw new InvalidOperationException(message: $"puck {arguments[0]} failed."); }
    }
    private static string Text(JsonNode? value) => (value?.ToString() ?? throw new InvalidDataException(message: "Missing required JSON value."));
    private static JsonNode Outputs() => CliFiles.ReadJson(path: "artifacts/production-outputs.json");
    private static JsonNode Value(JsonNode outputs, string key) => (outputs[key]?["value"] ?? throw new InvalidDataException(message: $"Missing deployment output: {key}"));
    // Resource names come from the compiled bicep parameters that deploy-infrastructure leaves beside its outputs.
    private static string ResourceName(params string[] path) => Text(value: path.Aggregate(func: (node, segment) => node![segment], seed: CliFiles.ReadJson(path: "artifacts/production-parameters.json")["parameters"]!["resources"]!["value"]));
    private static Task<string> AzAsync(params string[] args) => RunAsync(arguments: args, capture: true, executable: "az");
    private static async Task<JsonNode> AzJsonAsync(params string[] args) => (JsonNode.Parse(json: await AzAsync(args: args)) ?? throw new InvalidDataException(message: "Azure returned empty JSON."));
    private static Task<string> DockerAsync(params string[] args) => RunAsync(arguments: args, capture: true, executable: "docker");
    private static async Task<string> TokenAsync(string resource) {
        var token = await AzAsync("account", "get-access-token", "--resource", resource, "--query", "accessToken", "-o", "tsv");

        CliGitHub.Mask(value: token);
        return token;
    }
    // Cloud steps settle at their own pace: every az/docker/puck step this verb drives retries under one policy.
    private static Task RetryAsync(Func<Task> action, int attempts = 12, int seconds = 5) =>
        CliRetry.RetryAsync(
            action: action,
            attempts: attempts,
            delay: TimeSpan.FromSeconds(seconds: seconds),
            report: (error, attempt) => $"Attempt {attempt}/{attempts} failed: {error.Message}"
        );
    private static async Task<JsonNode> GetJsonAsync(string uri) => (JsonNode.Parse(json: await Http.GetStringAsync(requestUri: uri)) ?? throw new InvalidDataException(message: $"Empty response: {uri}"));
    private static async Task CurrentAsync() {
        var reference = CliGitHub.EnvironmentVariable(name: "GITHUB_REF");
        var tip = (await RunAsync(arguments: ["ls-remote", "origin", reference], capture: true, executable: "git")).Split(options: StringSplitOptions.RemoveEmptyEntries, separator: ((char[]?)null)).FirstOrDefault();

        if (tip is null) { throw new IOException(message: "Could not resolve the deployment branch."); }
        CliGitHub.Output(key: "deploy", value: ((tip == Environment.GetEnvironmentVariable(variable: "GITHUB_SHA")) ? "true" : "false"));
    }
    private static async Task BuildAsync(string output, string? runtimeArtifacts) {
        if (runtimeArtifacts is { Length: 0 }) { runtimeArtifacts = null; }
        if (Directory.Exists(path: output)) { throw new IOException(message: $"Use a fresh output directory: {output}"); }
        Directory.CreateDirectory(path: output);
        var commit = await RunAsync(arguments: ["rev-parse", "HEAD"], capture: true, executable: "git");

        Environment.SetEnvironmentVariable(value: "true", variable: "CI");
        if (runtimeArtifacts is null) {
            await RunAsync(arguments: ["restore", "src/Puck.Azure.Functions", "--locked-mode"], executable: "dotnet");
            await RunAsync(arguments: ["publish", "src/Puck.Azure.Functions", "-c", "Release", "--no-restore", "-o", Path.Combine(path1: output, path2: "functions")], executable: "dotnet");
        } else {
            if (Text(value: CliFiles.ReadJson(path: Path.Combine(path1: runtimeArtifacts, path2: "source.json"))["commit"]) != commit) { throw new InvalidDataException(message: "Runtime artifacts belong to another commit."); }
            CliFiles.CopyDirectory(destination: Path.Combine(path1: output, path2: "functions"), source: Path.Combine(path1: runtimeArtifacts, path2: "functions"));
        }
        foreach (var file in new[] { "host.json", "worker.config.json", "functions.metadata", "Puck.Azure.Functions.dll" }) {
            if (!File.Exists(path: Path.Combine(path1: output, path2: "functions", path3: file))) { throw new IOException(message: $"Functions publish omitted {file}"); }
        }
        var configuration = CliFiles.ReadJson(path: "src/Puck.Azure.Functions/configuration.json");
        var sentinel = configuration["items"]!.AsArray().Single(predicate: item => (((string?)item!["key"]) == "Version"))!;

        sentinel["value"] = commit;
        CliFiles.WriteJson(path: Path.Combine(path1: output, path2: "configuration.json"), value: configuration);
        if (runtimeArtifacts is null) {
            await RunAsync(arguments: ["restore", "src/Puck.World.Browser", "--locked-mode"], executable: "dotnet");
            await RunAsync(arguments: ["publish", "src/Puck.World.Browser", "-c", "Release", "--no-restore"], executable: "dotnet");
        } else {
            CliFiles.CopyDirectory(destination: "src/Puck.World.Browser/bin/Release/net10.0/browser-wasm/AppBundle", source: Path.Combine(path1: runtimeArtifacts, path2: "browser"));
        }
        await PuckAsync("official", "build", "--out", Path.Combine(path1: output, path2: "official"), "--channel", "stable", "--engine", "src/Puck.World.Browser/bin/Release/net10.0/browser-wasm/AppBundle");
        await PuckAsync("official", "verify", "--base", Path.Combine(path1: output, path2: "official"), "--channel", "stable", "--expect-commit", commit);
        await PuckAsync("world", "prepare", "src/Puck.World/Assets/worlds", Path.Combine(path1: output, path2: "silo-worlds"));
        var dashboard = Path.GetFullPath(path: "src/Puck.Dashboard/src");

        Environment.SetEnvironmentVariable(value: "stable", variable: "VITE_PUCK_OFFICIAL_CHANNEL");
        Environment.SetEnvironmentVariable(value: "https://puck.byteterrace.com/official", variable: "VITE_PUCK_OFFICIAL_BASE");
        Environment.SetEnvironmentVariable(value: Path.GetFullPath(path: Path.Combine(path1: output, path2: "official", path3: "stable", path4: "manifest.json")), variable: "PUCK_TEST_OFFICIAL_MANIFEST");
        foreach (var command in new string[][] { ["ci"], ["audit", "--audit-level=high"], ["--workspace", "portal", "run", "check:types"], ["run", "build"], ["--workspace", "portal", "run", "test"], ["run", "stage"] }) {
            await RunAsync(arguments: command, directory: dashboard, executable: "npm");
        }
        CliFiles.CopyDirectory(destination: Path.Combine(path1: output, path2: "dashboard-storage"), source: "src/Puck.Dashboard/dist-deploy");
        await PuckAsync("docs", "build", Path.Combine(path1: output, path2: "dashboard-storage"));
        await PuckAsync("bundle", "create", output, commit);
    }
    private static async Task BuildInfrastructureAsync(string output) {
        if (Directory.Exists(path: output)) { throw new IOException(message: $"Use a fresh output directory: {output}"); }
        Directory.CreateDirectory(path: output);
        await AzAsync("bicep", "build", "--file", "src/Puck.Azure.Resources/main.bicep", "--outfile", Path.Combine(path1: output, path2: "infrastructure.json"));
        await AzAsync("bicep", "build", "--file", "src/Puck.Azure.Resources/worldSiloCompute.bicep", "--outfile", Path.Combine(path1: output, path2: "world-silo-compute.json"));
        await AzAsync("bicep", "build-params", "--file", "src/Puck.Azure.Resources/main.bicepparam", "--outfile", Path.Combine(path1: output, path2: "parameters.json"));
        CliFiles.WriteJson(path: Path.Combine(path1: output, path2: "source.json"), value: new JsonObject { ["commit"] = await RunAsync(arguments: ["rev-parse", "HEAD"], capture: true, executable: "git") });
    }
}
