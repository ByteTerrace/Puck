using System.Security.Cryptography;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Puck.Cli.Automation;
using Puck.Storage;
using Puck.World;
using Puck.World.Server;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>Runs the shipped entry points without the test process's preloaded providers.</summary>
public sealed class WorldReleasePackagedHostTests {
    private static Task<string> DockerAsync(IReadOnlyList<string> arguments, CancellationToken token) =>
        CliProcess.RunCheckedAsync(
            Environment.CurrentDirectory,
            "docker",
            arguments,
            capture: true,
            cancellationToken: token
        );
    private static Task<string> ReadHttpAsync(string container, int port, string path, CancellationToken token) =>
        DockerAsync(
            arguments: ["exec", container, "/bin/bash", "-c", $"exec 3<>/dev/tcp/127.0.0.1/{port} || exit 0; printf 'GET {path} HTTP/1.1\\r\\nHost: qualification.invalid\\r\\nConnection: close\\r\\n\\r\\n' >&3; cat <&3"],
            token: token
        );
    private static WorldScreen Screen(int index, string machine) => new(
        Index: index,
        Origin: new(
            x: 0,
            y: 1,
            z: (3 + index)
        ),
        Right: Vector3.UnitX,
        Up: Vector3.UnitY,
        HalfWidth: 0.3f,
        HalfHeight: 0.27f,
        HalfDepth: 0.03f,
        Round: 0,
        Source: new WorldScreenSource.Machine(
            Instance: machine,
            Output: "video"
        ),
        Route: WorldScreenRoute.Passive
    );

    [InlineData("silo")]
    [InlineData("silo-mcp")]
    [InlineData("cli-mcp")]
    [InlineData("silo-mcp-conflict")]
    [Theory]
    [Trait("Category", "Docker")]
    public async Task PackagedHostLoadsMachinesAndConfiguredServices(string entryPoint) {
        var image = Environment.GetEnvironmentVariable(variable: "PUCK_TEST_WORLD_IMAGE");

        if (image is null) { Assert.Skip(reason: "Set PUCK_TEST_WORLD_IMAGE to the candidate silo image."); return; }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token: TestContext.Current.CancellationToken);

        deadline.CancelAfter(delay: TimeSpan.FromMinutes(minutes: 2));
        var token = deadline.Token;
        var temporary = Directory.CreateTempSubdirectory(prefix: "puck-packaged-host-");
        var container = ("puck-packaged-host-" + Guid.NewGuid().ToString(format: "N"));
        var started = false;

        try {
            var services = new ServiceCollection();

            Puck.Storage.DependencyInjection.PuckStorageServiceRegistration.AddCore(services: services);
            using var provider = services.BuildServiceProvider();
            var blobs = provider.GetRequiredService<IObjectBlobStore>();
            var owner = Guid.NewGuid();
            var remote = new DirectoryObjectStorageTarget(Path.Combine(
                path1: temporary.FullName,
                path2: "archive"
            ));
            var archive = new WorldReleaseArchive(
                blobs,
                remote,
                owner
            );
            var bytes = WorldDefinitionSerialization.Serialize(definition: new WorldDefinition(
                HostRaw: WorldHostDefaults.Absent with { Width = 320, Height = 200, TargetHertz = 60 },
                MachinesRaw: [
                    new WorldMachine(
                        "cgb",
                        "gaming-brick",
                        JsonSerializer.SerializeToElement(new { schema = "puck.gaming-brick.configuration.v1", model = "cgb", boot = "fast", content = new { path = "../cartridges/tetris.cgb.cartridge.json" } })
                    ),
                    new WorldMachine(
                        "agb",
                        "advanced-gaming-brick",
                        JsonSerializer.SerializeToElement(new { schema = "puck.advanced-gaming-brick.config.v1", boot = "fast", content = new { path = "../cartridges/pip.agb.cartridge.json" } })
                    ),
                ],
                ScreensRaw: [Screen(
                        index: 0,
                        machine: "cgb"
                    ), Screen(
                        index: 1,
                        machine: "agb"
                    )]
            ));
            var package = Directory.CreateDirectory(path: Path.Combine(
                path1: temporary.FullName,
                path2: "package"
            )).FullName;

            File.WriteAllBytes(
                Path.Combine(
                    path1: package,
                    path2: "world.json"
                ),
                bytes
            );
            var digest = JsonNode.Parse(await DockerAsync(
                arguments: ["image", "inspect", image],
                token: token
            ))![0]!["Id"]!.GetValue<string>();
            var release = new WorldReleaseManifest {
                Label = "packaged-host",
                SourceRevision = "test",
                EngineImageDigest = digest,
                PersistenceContract = "test",
                PeerProtocolContract = "test",
                Definitions = new Dictionary<string, string> { [$"{owner:D}/alpha"] = ("sha256/" + Convert.ToHexStringLower(inArray: SHA256.HashData(source: bytes))) },
                DefinitionFiles = new Dictionary<string, string> { [$"{owner:D}/alpha"] = "world.json" },
            };

            await archive.SaveAsync(
                cancellationToken: token,
                manifest: release,
                packageDirectory: package
            );
            var fixture = Path.Combine(
                path1: temporary.FullName,
                path2: "fixture"
            );

            await new WorldReleaseFixtureBuilder(
                blobs,
                archive,
                new WorldReleaseFixtureArchive(
                    owner: owner,
                    store: blobs,
                    target: remote
                )
            ).BuildAsync(
                release,
                owner,
                null,
                fixture,
                token
            );
            var config = JsonNode.Parse(File.ReadAllBytes(path: Path.Combine(
                path1: fixture,
                path2: "silo.json"
            )))!;

            config["stateDir"] = "/fixture/state";
            config["store"]!["settings"]!["path"] = "/fixture/store";
            config["worlds"]![0]!["federation"]!["keyFile"] = $"/fixture/keys/{owner:D}-alpha.pk8";
            config["lifecycle"] = JsonSerializer.SerializeToNode(new { shutdownSeconds = 30, healthPort = 8081, progressTimeoutSeconds = 30, checkpointTimeoutSeconds = 180, journalTimeoutSeconds = 30, journalBacklogLimit = 1024 });
            File.WriteAllText(
                Path.Combine(
                    path1: fixture,
                    path2: "silo.json"
                ),
                config.ToJsonString()
            );
            File.WriteAllText(
                Path.Combine(
                    path1: fixture,
                    path2: "mcp.json"
                ),
                """
                {"target":"alpha","publicUrl":"https://qualification.invalid/mcp","listenUrl":"http://127.0.0.1:8082",
                 "issuer":"https://qualification.invalid","audience":"qualification","scope":"puck","allowedSubjects":[]}
                """
            );
            if (entryPoint == "silo-mcp-conflict") {
                var mcp = JsonNode.Parse(File.ReadAllBytes(path: Path.Combine(
                    path1: fixture,
                    path2: "mcp.json"
                )))!;

                mcp["listenUrl"] = "http://127.0.0.1:8081";
                File.WriteAllText(
                    Path.Combine(
                        path1: fixture,
                        path2: "mcp.json"
                    ),
                    mcp.ToJsonString()
                );
            }
            var arguments = new List<string> { "run", "--detach", "--name", container, "--network", "none", "--read-only", "--cap-drop", "ALL",
                "--security-opt", "no-new-privileges", "--pids-limit", "512", "--memory", "2g", "--cpus", "2", "--tmpfs", "/tmp:rw,nosuid,nodev,size=256m",
                "--mount", $"type=bind,source={fixture},target=/fixture" };

            if (entryPoint == "cli-mcp") { arguments.AddRange(collection: ["--entrypoint", "dotnet", image, "/puck-cli/Puck.Cli.dll", "mcp", "--silo", "/fixture/silo.json", "--http", "/fixture/mcp.json"]); } else {
                arguments.AddRange(collection: [image, "--silo", "/fixture/silo.json"]);
                if (entryPoint.StartsWith(
                    comparisonType: StringComparison.Ordinal,
                    value: "silo-mcp"
                )) { arguments.AddRange(collection: ["--mcp", "/fixture/mcp.json"]); }
            }
            await DockerAsync(
                arguments: arguments,
                token: token
            );
            started = true;
            if (entryPoint == "silo-mcp-conflict") {
                while ((await DockerAsync(
                    arguments: ["inspect", "--format", "{{.State.Running}}", container],
                    token: token
                )).Trim() == "true") {
                    await Task.Delay(
                        cancellationToken: token,
                        millisecondsDelay: 200
                    );
                }
                Assert.NotEqual(
                    "0",
                    (await DockerAsync(
                        arguments: ["inspect", "--format", "{{.State.ExitCode}}", container],
                        token: token
                    )).Trim()
                );
                return;
            }
            while (true) {
                Assert.Equal(
                    "true",
                    (await DockerAsync(
                        arguments: ["inspect", "--format", "{{.State.Running}}", container],
                        token: token
                    )).Trim()
                );
                var health = await ReadHttpAsync(
                    container: container,
                    path: "/healthz",
                    port: 8081,
                    token: token
                );

                if (health.StartsWith(
                    comparisonType: StringComparison.Ordinal,
                    value: "HTTP/1.1 200"
                )) { break; }
                await Task.Delay(
                    cancellationToken: token,
                    millisecondsDelay: 200
                );
            }
            Assert.Contains(
                "Healthy",
                await ReadHttpAsync(
                    container: container,
                    path: "/livez/azure",
                    port: 8081,
                    token: token
                )
            );
            if (entryPoint != "silo") {
                var discovery = await ReadHttpAsync(
                    container: container,
                    path: "/.well-known/oauth-protected-resource/mcp",
                    port: 8082,
                    token: token
                );

                Assert.StartsWith(
                    actualString: discovery,
                    expectedStartString: "HTTP/1.1 200"
                );
                Assert.Contains(
                    actualString: discovery,
                    expectedSubstring: "https://qualification.invalid/mcp"
                );
            }
            Assert.DoesNotContain(
                "Failed to load candidate",
                await DockerAsync(
                    arguments: ["logs", container],
                    token: token
                )
            );
            await DockerAsync(
                arguments: ["stop", "--time", "40", container],
                token: token
            );
            Assert.Equal(
                "0",
                (await DockerAsync(
                    arguments: ["inspect", "--format", "{{.State.ExitCode}}", container],
                    token: token
                )).Trim()
            );
        } finally {
            using var cleanup = new CancellationTokenSource(delay: TimeSpan.FromSeconds(seconds: 30));

            if (started) {
                TestContext.Current.TestOutputHelper?.WriteLine(message: await DockerAsync(
                    arguments: ["logs", container],
                    token: cleanup.Token
                ));
                await DockerAsync(
                    arguments: ["rm", "--force", container],
                    token: cleanup.Token
                );
            }
            temporary.Delete(recursive: true);
        }
    }
}
