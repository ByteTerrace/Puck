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

    /// <summary>Creates the <c>azure</c> verb; <paramref name="clock"/> bounds its requests, leases, and
    /// qualification runs.</summary>
    /// <param name="clock">The CLI host's clock.</param>
    /// <returns>The verb.</returns>
    public static Command Create(TimeProvider clock) {
        var resourceGroupOption = new Option<string>(name: "--resource-group") { DefaultValueFactory = _ => DefaultResourceGroup, Description = "The production resource group." };
        var commitOption = new Option<string>(name: "--commit") { Description = "The full lowercase commit SHA of this release.", Required = true, Validators = { result => ValidateCommit(result: result) } };
        var buildOutput = Output(
            defaultPath: "artifacts/azure",
            description: "A fresh directory for the bundle."
        );
        var infrastructureOutput = Output(
            defaultPath: "artifacts/infrastructure",
            description: "A fresh directory for the compiled templates."
        );
        var releaseOutput = Output(
            defaultPath: "artifacts/world-release",
            description: "The directory that receives the bound worlds and their release.json."
        );
        var digestOutput = CliOptions.Output(description: "A file to receive the pushed digest.");
        var build = new Command(
            description: "Build the application bundle: Functions, the browser engine, the official tree, hosted worlds, the dashboard, and the documentation, with its release manifest.",
            name: "build"
        ) {
            buildOutput,
            new Option<string?>(name: "--runtime-artifacts") { Description = "This commit's compiled Functions and browser payloads, when a producer job already built them." },
        };
        var buildInfrastructure = new Command(
            description: "Compile the platform and world-compute templates and the parameters for this commit.",
            name: "build-infrastructure"
        ) {
            infrastructureOutput,
        };
        var deployInfrastructure = new Command(
            description: "Plan and reconcile the production platform from the compiled templates, refusing a plan that deletes a resource.",
            name: "deploy-infrastructure"
        ) {
            resourceGroupOption, new Option<string?>(name: "--infrastructure-artifacts") { Description = "This commit's compiled templates; required in CI." },
        };
        var deployApplications = new Command(
            description: "Verify the bundle, publish both container images, apply application configuration, and roll the actors revision.",
            name: "deploy-applications"
        ) {
            new Option<string>(name: "--artifacts-directory") { Description = "The downloaded artifacts holding azure-applications and the container images.", Required = true }, commitOption, resourceGroupOption,
        };
        var deployWorldPlatform = new Command(
            description: "Deploy the world platform composition alone and record its outputs.",
            name: "deploy-world-platform"
        ) { resourceGroupOption };
        var deployWorldMcp = new Command(
            description: "Plan and deploy the world MCP policy against the deployed platform.",
            name: "deploy-world-mcp"
        ) {
            resourceGroupOption, new Option<bool>(name: "--plan-only") { Description = "Stop after the what-if plan." },
        };
        var prepareWorldRelease = new Command(
            description: "Bind the deployed endpoints and admission into the composed worlds and prepare their release package for world release deploy.",
            name: "prepare-world-release"
        ) { releaseOutput };
        var publishStatic = new Command(
            description: "Publish official content and the website from the bundle.",
            name: "publish-static"
        ) {
            new Option<string>(name: "--bundle-directory") { DefaultValueFactory = _ => "artifacts/azure", Description = "The verified bundle." },
        };
        var publishTemplateSpecs = new Command(
            description: "Publish the Template Specs the platform composes from, refusing a changed version.",
            name: "publish-template-specs"
        );
        var publishContainer = new Command(
            description: "Push one built image to the registry and print its digest.",
            name: "publish-container"
        ) {
            new Option<string>(name: "--registry") { Description = "The container registry name.", Required = true },
            new Option<string>(name: "--repository") { Description = "web-actors or world-silo.", Required = true },
            commitOption,
            new Option<string?>(name: "--archive") { Description = "A saved image archive to load first." },
            digestOutput,
        };
        var functionsAccess = new Command(
            description: "Admit this runner to the Functions deployment endpoint, or restore the saved restrictions.",
            name: "functions-access"
        ) {
            new Option<bool>(name: "--restore") { Description = "Restore the restrictions saved by the admitting run." }, resourceGroupOption,
            new Option<string?>(name: "--name") { Description = "The function app; defaults to the compiled parameters." },
        };
        var vaultAccess = new Command(
            description: "Admit this runner to the signing-key vault, or remove the rule it added.",
            name: "vault-access"
        ) {
            new Option<bool>(name: "--restore") { Description = "Remove the rule the admitting run added." },
        };
        var testProduction = new Command(
            description: "Verify the deployed release end to end through Front Door.",
            name: "test-production"
        ) {
            commitOption, new Option<bool>(name: "--before-static-publication") { Description = "Verify services only; the website is not published yet." },
        };
        var testWorldContainer = new Command(
            description: "Boot the world image twice against a fixture store and prove activation, checkpointing, health, and QUIC.",
            name: "test-world-container"
        ) {
            new Option<string>(name: "--image") { Description = "The built world-silo image.", Required = true },
        };
        var testWorldRelease = new Command(
            description: "Verify the running world worker matches the requested release digest and reports healthy.",
            name: "test-world-release"
        ) {
            new Option<string?>(name: "--resource-group") { Description = "With --scale-set, verify without deployment outputs." },
            new Option<string?>(name: "--scale-set") { Description = "The worker scale set." },
            new Option<string?>(name: "--image") { Description = "The expected digest; defaults to artifacts/world-silo.digest." },
        };
        var testWorldMcp = new Command(
            description: "Exercise the live MCP endpoint with delegated authorization.",
            name: "test-world-mcp"
        );
        var buildActors = new Command(
            description: "Build, publish, and roll the actors image from this commit; resource names come from artifacts/production-parameters.json (build-infrastructure).",
            name: "build-actors"
        ) {
            new Option<bool>(name: "--no-restart") { Description = "Publish without rolling the container app." },
            new Option<string?>(name: "--registry") { Description = "The registry; defaults to the compiled parameters." },
            resourceGroupOption,
            new Option<string?>(name: "--container-app") { Description = "The actors app; defaults to the compiled parameters." },
        };
        var stageWorldImage = new Command(
            description: "Publish the world image and stage its worlds for a deployment; the registry name comes from artifacts/production-parameters.json (build-infrastructure).",
            name: "stage-world-image"
        ) { commitOption };
        var current = new Command(
            description: "Set the deploy output to whether GITHUB_SHA is still the tip of GITHUB_REF.",
            name: "current"
        );
        var command = new Command(
            description: "Build, deploy, publish, and verify Puck's Azure production.",
            name: "azure"
        ) {
            build, buildInfrastructure, deployInfrastructure, deployApplications, deployWorldPlatform, deployWorldMcp, prepareWorldRelease, publishStatic, publishTemplateSpecs, publishContainer,
            functionsAccess, vaultAccess, testProduction, testWorldContainer, testWorldRelease, testWorldMcp, buildActors, stageWorldImage, current,
        };

        Bind(command: build, work: (parseResult, _) => BuildAsync(
            output: Path.GetFullPath(path: parseResult.GetRequiredValue(option: buildOutput)),
            runtimeArtifacts: parseResult.GetValue<string?>(name: "--runtime-artifacts")
        ));
        Bind(command: buildInfrastructure, work: (parseResult, _) => BuildInfrastructureAsync(output: Path.GetFullPath(path: parseResult.GetRequiredValue(option: infrastructureOutput))));
        Bind(command: deployInfrastructure, work: (parseResult, _) => DeployInfrastructureAsync(
            group: parseResult.GetRequiredValue(option: resourceGroupOption),
            infrastructure: parseResult.GetValue<string?>(name: "--infrastructure-artifacts")
        ));
        Bind(command: deployApplications, work: (parseResult, _) => DeployApplicationsAsync(
            artifacts: parseResult.GetRequiredValue<string>(name: "--artifacts-directory"),
            commit: parseResult.GetRequiredValue(option: commitOption),
            group: parseResult.GetRequiredValue(option: resourceGroupOption)
        ));
        Bind(command: deployWorldPlatform, work: (parseResult, _) => DeployWorldPlatformAsync(group: parseResult.GetRequiredValue(option: resourceGroupOption)));
        Bind(command: deployWorldMcp, work: (parseResult, _) => DeployWorldMcpAsync(
            group: parseResult.GetRequiredValue(option: resourceGroupOption),
            planOnly: parseResult.GetValue<bool>(name: "--plan-only")
        ));
        Bind(command: prepareWorldRelease, work: (parseResult, _) => {
            PrepareOfficialWorldRelease(outputDirectory: parseResult.GetRequiredValue(option: releaseOutput));

            return Task.CompletedTask;
        });
        Bind(command: publishStatic, work: (parseResult, _) => PublishStaticAsync(bundle: parseResult.GetRequiredValue<string>(name: "--bundle-directory")));
        Bind(command: publishTemplateSpecs, work: (_, _) => PublishTemplateSpecsAsync());
        Bind(command: publishContainer, work: (parseResult, _) => PublishContainerAsync(
            archive: (parseResult.GetValue<string?>(name: "--archive") ?? ""),
            commit: parseResult.GetRequiredValue(option: commitOption),
            output: (parseResult.GetValue(option: digestOutput) ?? ""),
            registry: parseResult.GetRequiredValue<string>(name: "--registry"),
            repository: parseResult.GetRequiredValue<string>(name: "--repository")
        ));
        Bind(command: functionsAccess, work: (parseResult, _) => FunctionsAccessAsync(
            group: parseResult.GetRequiredValue(option: resourceGroupOption),
            name: parseResult.GetValue<string?>(name: "--name"),
            restore: parseResult.GetValue<bool>(name: "--restore")
        ));
        Bind(command: vaultAccess, work: (parseResult, _) => VaultAccessAsync(restore: parseResult.GetValue<bool>(name: "--restore")));
        Bind(command: testProduction, work: (parseResult, _) => TestProductionAsync(
            beforeStaticPublication: parseResult.GetValue<bool>(name: "--before-static-publication"),
            clock: clock,
            commit: parseResult.GetRequiredValue(option: commitOption)
        ));
        Bind(command: testWorldContainer, work: (parseResult, _) => TestWorldContainerAsync(
            clock: clock,
            image: parseResult.GetRequiredValue<string>(name: "--image")
        ));
        Bind(command: testWorldRelease, work: (parseResult, _) => TestWorldReleaseAsync(
            clock: clock,
            group: parseResult.GetValue<string?>(name: "--resource-group"),
            image: parseResult.GetValue<string?>(name: "--image"),
            scaleSet: parseResult.GetValue<string?>(name: "--scale-set")
        ));
        Bind(command: testWorldMcp, work: (_, _) => TestWorldMcpAsync());
        Bind(command: buildActors, work: (parseResult, _) => BuildActorsAsync(
            containerApp: parseResult.GetValue<string?>(name: "--container-app"),
            group: parseResult.GetRequiredValue(option: resourceGroupOption),
            noRestart: parseResult.GetValue<bool>(name: "--no-restart"),
            registry: parseResult.GetValue<string?>(name: "--registry")
        ));
        Bind(command: stageWorldImage, work: (parseResult, _) => StageWorldImageAsync(commit: parseResult.GetRequiredValue(option: commitOption)));
        Bind(command: current, work: (_, _) => CurrentAsync());
        return command;
    }

    private static void ValidateCommit(System.CommandLine.Parsing.OptionResult result) {
        if (!Regex.IsMatch(
            input: (result.GetValueOrDefault<string>() ?? ""),
            pattern: CommitPattern
        )) { result.AddError(errorMessage: "Expected a full lowercase commit SHA."); }
    }
    private static string Root() {
        var root = RepositoryPaths.RequireRoot();

        if (Path.GetFullPath(path: Environment.CurrentDirectory) != root) { throw new InvalidOperationException(message: "Run puck azure from the repository root."); }
        return root;
    }
    // Every azure verb reads and writes paths relative to the repository root, so each refuses to run anywhere else.
    // A failure is an exception, which CliExit reports as a refusal on one named line.
    private static void Bind(Command command, Func<ParseResult, CancellationToken, Task> work) => command.SetAction(action: async (parseResult, cancellationToken) => {
        _ = Root();
        await work(
            arg1: parseResult,
            arg2: cancellationToken
        );

        return CliExit.Success;
    });
    // An output directory with the default the workflows rely on.
    private static Option<string> Output(string defaultPath, string description) {
        var option = CliOptions.Output(description: description);

        option.DefaultValueFactory = _ => defaultPath;

        return option;
    }
    private static async Task<string> RunAsync(string executable, IEnumerable<string> arguments, string? directory = null, bool capture = false, string? input = null) {
        var controller = WorldReleaseController.Value;

        if (controller is not null) { await controller.EnsureHeldAsync().ConfigureAwait(continueOnCapturedContext: false); }
        return (await CliProcess.RunCheckedAsync(
            arguments: arguments,
            capture: capture,
            fileName: executable,
            input: input,
            workingDirectory: (directory ?? Root()),
            cancellationToken: (controller?.Token ?? default)
        )).Trim();
    }
    private static async Task PuckAsync(params string[] arguments) {
        if (await PuckRootCommand.InvokeAsync(args: arguments) != 0) { throw new InvalidOperationException(message: $"puck {arguments[0]} failed."); }
    }
    private static string Text(JsonNode? value) => (value?.ToString() ?? throw new InvalidDataException(message: "Missing required JSON value."));
    private static JsonNode Outputs() => CliFiles.ReadJson(path: "artifacts/production-outputs.json");
    private static JsonNode Value(JsonNode outputs, string key) => (outputs[key]?["value"] ?? throw new InvalidDataException(message: $"Missing deployment output: {key}"));
    // Resource names come from the compiled bicep parameters that deploy-infrastructure leaves beside its outputs.
    private static string ResourceName(params string[] path) => Text(value: path.Aggregate(
        func: (node, segment) => node![segment],
        seed: CliFiles.ReadJson(path: "artifacts/production-parameters.json")["parameters"]!["resources"]!["value"]
    ));
    private static Task<string> AzAsync(params string[] args) => RunAsync(
        arguments: args,
        capture: true,
        executable: "az"
    );
    private static async Task<JsonNode> AzJsonAsync(params string[] args) => (JsonNode.Parse(json: await AzAsync(args: args)) ?? throw new InvalidDataException(message: "Azure returned empty JSON."));
    private static Task<string> DockerAsync(params string[] args) => RunAsync(
        arguments: args,
        capture: true,
        executable: "docker"
    );
    private static async Task<string> TokenAsync(string resource) {
        var token = await AzAsync(
            "account",
            "get-access-token",
            "--resource",
            resource,
            "--query",
            "accessToken",
            "-o",
            "tsv"
        );

        CliGitHub.Mask(value: token);
        return token;
    }
    // Cloud steps settle at their own pace: every az/docker/puck step this verb drives retries under one policy, its
    // pacing on the verb's clock.
    private static Task RetryAsync(Func<Task> action, TimeProvider clock, int attempts = 12, int seconds = 5) =>
        CliRetry.RetryAsync(
            action: action,
            attempts: attempts,
            clock: clock,
            delay: TimeSpan.FromSeconds(seconds: seconds),
            report: (error, attempt) => $"Attempt {attempt}/{attempts} failed: {error.Message}"
        );
    private static async Task<JsonNode> GetJsonAsync(string uri) => (JsonNode.Parse(json: await Http.GetStringAsync(requestUri: uri)) ?? throw new InvalidDataException(message: $"Empty response: {uri}"));
    private static async Task CurrentAsync() {
        var reference = CliGitHub.Ref;
        var tip = (await RunAsync(
            arguments: ["ls-remote", "origin", reference],
            capture: true,
            executable: "git"
        )).Split(
            options: StringSplitOptions.RemoveEmptyEntries,
            separator: ((char[]?)null)
        ).FirstOrDefault();

        if (tip is null) { throw new IOException(message: "Could not resolve the deployment branch."); }
        CliGitHub.Output(
            key: "deploy",
            value: ((tip == Environment.GetEnvironmentVariable(variable: "GITHUB_SHA"))
            ? "true"
            : "false")
        );
    }
    private static async Task BuildAsync(string output, string? runtimeArtifacts) {
        if (runtimeArtifacts is { Length: 0 }) { runtimeArtifacts = null; }
        if (Directory.Exists(path: output)) { throw new IOException(message: $"Use a fresh output directory: {output}"); }
        Directory.CreateDirectory(path: output);
        var commit = await RunAsync(
            arguments: ["rev-parse", "HEAD"],
            capture: true,
            executable: "git"
        );

        Environment.SetEnvironmentVariable(
            value: "true",
            variable: "CI"
        );
        if (runtimeArtifacts is null) {
            await RunAsync(
                arguments: ["restore", "src/Puck.Azure.Functions", "--locked-mode"],
                executable: "dotnet"
            );
            await RunAsync(
                arguments: ["publish", "src/Puck.Azure.Functions", "-c", "Release", "--no-restore", "-o", Path.Combine(
                        path1: output,
                        path2: "functions"
                    )],
                executable: "dotnet"
            );
        } else {
            if (Text(value: CliFiles.ReadJson(path: Path.Combine(
                path1: runtimeArtifacts,
                path2: "source.json"
            ))["commit"]) != commit) { throw new InvalidDataException(message: "Runtime artifacts belong to another commit."); }
            CliFiles.CopyDirectory(
                destination: Path.Combine(
                    path1: output,
                    path2: "functions"
                ),
                source: Path.Combine(
                    path1: runtimeArtifacts,
                    path2: "functions"
                )
            );
        }
        foreach (var file in new[] { "host.json", "worker.config.json", "functions.metadata", "Puck.Azure.Functions.dll" }) {
            if (!File.Exists(path: Path.Combine(
                path1: output,
                path2: "functions",
                path3: file
            ))) { throw new IOException(message: $"Functions publish omitted {file}"); }
        }
        var configuration = CliFiles.ReadJson(path: "src/Puck.Azure.Functions/configuration.json");
        var sentinel = configuration["items"]!.AsArray().Single(predicate: item => (((string?)item!["key"]) == "Version"))!;

        sentinel["value"] = commit;
        CliFiles.WriteJson(
            path: Path.Combine(
                path1: output,
                path2: "configuration.json"
            ),
            value: configuration
        );
        if (runtimeArtifacts is null) {
            await RunAsync(
                arguments: ["restore", "src/Puck.World.Browser", "--locked-mode"],
                executable: "dotnet"
            );
            await RunAsync(
                arguments: ["publish", "src/Puck.World.Browser", "-c", "Release", "--no-restore"],
                executable: "dotnet"
            );
        } else {
            CliFiles.CopyDirectory(
                destination: "src/Puck.World.Browser/bin/Release/net10.0/browser-wasm/AppBundle",
                source: Path.Combine(
                    path1: runtimeArtifacts,
                    path2: "browser"
                )
            );
        }
        await PuckAsync(
            "official",
            "build",
            "--tree",
            Path.Combine(
                path1: output,
                path2: "official"
            ),
            "--channel",
            "stable",
            "--engine",
            "src/Puck.World.Browser/bin/Release/net10.0/browser-wasm/AppBundle"
        );
        await PuckAsync(
            "official",
            "verify",
            "--tree",
            Path.Combine(
                path1: output,
                path2: "official"
            ),
            "--channel",
            "stable",
            "--expect-commit",
            commit
        );
        await PuckAsync(
            "world",
            "prepare",
            "src/Puck.World/Assets/worlds",
            "--output",
            Path.Combine(
                path1: output,
                path2: "silo-worlds"
            )
        );
        var dashboard = Path.GetFullPath(path: "src/Puck.Dashboard/src");

        Environment.SetEnvironmentVariable(
            value: "stable",
            variable: "VITE_PUCK_OFFICIAL_CHANNEL"
        );
        Environment.SetEnvironmentVariable(
            value: "https://puck.byteterrace.com/official",
            variable: "VITE_PUCK_OFFICIAL_BASE"
        );
        Environment.SetEnvironmentVariable(
            value: Path.GetFullPath(path: Path.Combine(
                path1: output,
                path2: "official",
                path3: "stable",
                path4: "manifest.json"
            )),
            variable: "PUCK_TEST_OFFICIAL_MANIFEST"
        );
        foreach (var command in new string[][] { ["ci"], ["audit", "--audit-level=high"], ["run", "build"], ["--workspace", "portal", "run", "test"], ["run", "stage"] }) {
            await RunAsync(
                arguments: command,
                directory: dashboard,
                executable: "npm"
            );
        }
        CliFiles.CopyDirectory(
            destination: Path.Combine(
                path1: output,
                path2: "dashboard-storage"
            ),
            source: "src/Puck.Dashboard/dist-deploy"
        );
        await PuckAsync(
            "docs",
            "build",
            "--output",
            Path.Combine(
                path1: output,
                path2: "dashboard-storage"
            )
        );
        await PuckAsync(
            "bundle",
            "create",
            output,
            commit
        );
    }
    /// <summary>Verifies that every Bicep source is formatted and has no diagnostic, so compiler warnings stop the build like linter errors do.</summary>
    /// <remarks>A linter error exits non-zero; a warning exits zero and appears only as a SARIF result.</remarks>
    private static async Task VerifyBicepAsync() {
        var findings = new List<string>();

        foreach (var file in Directory.EnumerateFiles(
            path: "src/Puck.Azure.Resources",
            searchOption: SearchOption.AllDirectories,
            searchPattern: "*.bicep*"
        ).Where(predicate: path => (Path.GetExtension(path: path) is ".bicep" or ".bicepparam")).Order(comparer: StringComparer.Ordinal)) {
            var path = file.Replace(
                newChar: '/',
                oldChar: '\\'
            );
            var report = JsonNode.Parse(json: await AzAsync(
                "bicep",
                "lint",
                "--file",
                path,
                "--diagnostics-format",
                "sarif"
            ))!;

            foreach (var result in report["runs"]!.AsArray().SelectMany(selector: run => run!["results"]!.AsArray())) {
                findings.Add(item: $"{path}: {Text(value: result!["ruleId"])} {Text(value: result["message"]!["text"])}");
            }
            // The Azure CLI's console stream rewrites line endings, so the formatter writes a file to compare byte for byte.
            var formatted = Path.Combine(
                path1: Path.GetTempPath(),
                path2: ($"puck-bicep-{Guid.NewGuid():N}" + Path.GetExtension(path: path))
            );

            try {
                await AzAsync(
                    "bicep",
                    "format",
                    "--file",
                    path,
                    "--outfile",
                    formatted
                );
                if (!File.ReadAllBytes(path: formatted).AsSpan().SequenceEqual(other: File.ReadAllBytes(path: path))) { findings.Add(item: $"{path}: not formatted; run az bicep format --file {path}"); }
            } finally { File.Delete(path: formatted); }
        }
        if (findings.Count != 0) { throw new InvalidDataException(message: ("Bicep sources must be formatted and free of diagnostics:\n" + string.Join(separator: "\n", values: findings))); }
    }
    private static async Task BuildInfrastructureAsync(string output) {
        if (Directory.Exists(path: output)) { throw new IOException(message: $"Use a fresh output directory: {output}"); }
        Directory.CreateDirectory(path: output);
        await VerifyBicepAsync();
        await AzAsync(
            "bicep",
            "build",
            "--file",
            "src/Puck.Azure.Resources/main.bicep",
            "--outfile",
            Path.Combine(
                path1: output,
                path2: "infrastructure.json"
            )
        );
        await AzAsync(
            "bicep",
            "build",
            "--file",
            "src/Puck.Azure.Resources/worldSiloCompute.bicep",
            "--outfile",
            Path.Combine(
                path1: output,
                path2: "world-silo-compute.json"
            )
        );
        await AzAsync(
            "bicep",
            "build-params",
            "--file",
            "src/Puck.Azure.Resources/main.bicepparam",
            "--outfile",
            Path.Combine(
                path1: output,
                path2: "parameters.json"
            )
        );
        CliFiles.WriteJson(
            path: Path.Combine(
                path1: output,
                path2: "source.json"
            ),
            value: new JsonObject {
                ["commit"] = await RunAsync(
                arguments: ["rev-parse", "HEAD"],
                capture: true,
                executable: "git"
            ),
            }
        );
    }
}
