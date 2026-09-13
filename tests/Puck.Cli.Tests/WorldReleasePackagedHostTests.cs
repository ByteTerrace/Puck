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
    [Theory]
    [InlineData("silo")]
    [InlineData("silo-mcp")]
    [InlineData("cli-mcp")]
    [InlineData("silo-mcp-conflict")]
    [Trait("Category", "Docker")]
    public async Task PackagedHostLoadsMachinesAndConfiguredServices(string entryPoint) {
        var image = Environment.GetEnvironmentVariable("PUCK_TEST_WORLD_IMAGE");
        if (image is null) { Assert.Skip("Set PUCK_TEST_WORLD_IMAGE to the candidate silo image."); return; }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        var token = deadline.Token;
        var temporary = Directory.CreateTempSubdirectory("puck-packaged-host-");
        var container = "puck-packaged-host-" + Guid.NewGuid().ToString("N");
        var started = false;
        try {
            var services = new ServiceCollection();
            Puck.Storage.DependencyInjection.PuckStorageServiceRegistration.AddCore(services);
            using var provider = services.BuildServiceProvider();
            var blobs = provider.GetRequiredService<IObjectBlobStore>();
            var owner = Guid.NewGuid();
            var remote = new DirectoryObjectStorageTarget(Path.Combine(temporary.FullName, "archive"));
            var archive = new WorldReleaseArchive(blobs, remote, owner);
            var bytes = WorldDefinitionSerialization.Serialize(new WorldDefinition(
                HostRaw: WorldHostDefaults.Absent with { Width = 320, Height = 200, TargetHertz = 60 },
                MachinesRaw: [
                    new WorldMachine("cgb", "gaming-brick", JsonSerializer.SerializeToElement(new { schema = "puck.gaming-brick.configuration.v1", model = "cgb", boot = "fast", content = new { path = "../cartridges/tetris.cgb.cartridge.json" } })),
                    new WorldMachine("agb", "advanced-gaming-brick", JsonSerializer.SerializeToElement(new { schema = "puck.advanced-gaming-brick.config.v1", boot = "fast", content = new { path = "../cartridges/pip.agb.cartridge.json" } })),
                ], ScreensRaw: [Screen(0, "cgb"), Screen(1, "agb")]));
            var package = Directory.CreateDirectory(Path.Combine(temporary.FullName, "package")).FullName;
            File.WriteAllBytes(Path.Combine(package, "world.json"), bytes);
            var digest = JsonNode.Parse(await DockerAsync(["image", "inspect", image], token))![0]!["Id"]!.GetValue<string>();
            var release = new WorldReleaseManifest {
                Label = "packaged-host", SourceRevision = "test", EngineImageDigest = digest,
                PersistenceContract = "test", PeerProtocolContract = "test",
                Definitions = new Dictionary<string, string> { [$"{owner:D}/alpha"] = "sha256/" + Convert.ToHexStringLower(SHA256.HashData(bytes)) },
                DefinitionFiles = new Dictionary<string, string> { [$"{owner:D}/alpha"] = "world.json" },
            };
            await archive.SaveAsync(release, package, token);
            var fixture = Path.Combine(temporary.FullName, "fixture");
            await new WorldReleaseFixtureBuilder(blobs, archive, new WorldReleaseFixtureArchive(blobs, remote, owner)).BuildAsync(release, owner, null, fixture, token);
            var config = JsonNode.Parse(File.ReadAllBytes(Path.Combine(fixture, "silo.json")))!;
            config["stateDir"] = "/fixture/state";
            config["store"]!["settings"]!["path"] = "/fixture/store";
            config["worlds"]![0]!["federation"]!["keyFile"] = $"/fixture/keys/{owner:D}-alpha.pk8";
            config["lifecycle"] = JsonSerializer.SerializeToNode(new { shutdownSeconds = 30, healthPort = 8081, progressTimeoutSeconds = 30, checkpointTimeoutSeconds = 180, journalTimeoutSeconds = 30, journalBacklogLimit = 1024 });
            File.WriteAllText(Path.Combine(fixture, "silo.json"), config.ToJsonString());
            File.WriteAllText(Path.Combine(fixture, "mcp.json"), """
                {"target":"alpha","publicUrl":"https://qualification.invalid/mcp","listenUrl":"http://127.0.0.1:8082",
                 "issuer":"https://qualification.invalid","audience":"qualification","scope":"puck","allowedSubjects":[]}
                """);
            if (entryPoint == "silo-mcp-conflict") {
                var mcp = JsonNode.Parse(File.ReadAllBytes(Path.Combine(fixture, "mcp.json")))!;
                mcp["listenUrl"] = "http://127.0.0.1:8081";
                File.WriteAllText(Path.Combine(fixture, "mcp.json"), mcp.ToJsonString());
            }
            var arguments = new List<string> { "run", "--detach", "--name", container, "--network", "none", "--read-only", "--cap-drop", "ALL",
                "--security-opt", "no-new-privileges", "--pids-limit", "512", "--memory", "2g", "--cpus", "2", "--tmpfs", "/tmp:rw,nosuid,nodev,size=256m",
                "--mount", $"type=bind,source={fixture},target=/fixture" };
            if (entryPoint == "cli-mcp") { arguments.AddRange(["--entrypoint", "dotnet", image, "/puck-cli/Puck.Cli.dll", "mcp", "--silo", "/fixture/silo.json", "--http", "/fixture/mcp.json"]); }
            else {
                arguments.AddRange([image, "--silo", "/fixture/silo.json"]);
                if (entryPoint.StartsWith("silo-mcp", StringComparison.Ordinal)) { arguments.AddRange(["--mcp", "/fixture/mcp.json"]); }
            }
            await DockerAsync(arguments, token);
            started = true;
            if (entryPoint == "silo-mcp-conflict") {
                while ((await DockerAsync(["inspect", "--format", "{{.State.Running}}", container], token)).Trim() == "true") {
                    await Task.Delay(200, token);
                }
                Assert.NotEqual("0", (await DockerAsync(["inspect", "--format", "{{.State.ExitCode}}", container], token)).Trim());
                return;
            }
            while (true) {
                Assert.Equal("true", (await DockerAsync(["inspect", "--format", "{{.State.Running}}", container], token)).Trim());
                var health = await ReadHttpAsync(container, 8081, "/healthz", token);
                if (health.StartsWith("HTTP/1.1 200", StringComparison.Ordinal)) { break; }
                await Task.Delay(200, token);
            }
            Assert.Contains("Healthy", await ReadHttpAsync(container, 8081, "/livez/azure", token));
            if (entryPoint != "silo") {
                var discovery = await ReadHttpAsync(container, 8082, "/.well-known/oauth-protected-resource/mcp", token);
                Assert.StartsWith("HTTP/1.1 200", discovery);
                Assert.Contains("https://qualification.invalid/mcp", discovery);
            }
            Assert.DoesNotContain("Failed to load candidate", await DockerAsync(["logs", container], token));
            await DockerAsync(["stop", "--time", "40", container], token);
            Assert.Equal("0", (await DockerAsync(["inspect", "--format", "{{.State.ExitCode}}", container], token)).Trim());
        } finally {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            if (started) {
                TestContext.Current.TestOutputHelper?.WriteLine(await DockerAsync(["logs", container], cleanup.Token));
                await DockerAsync(["rm", "--force", container], cleanup.Token);
            }
            temporary.Delete(recursive: true);
        }
    }

    private static Task<string> ReadHttpAsync(string container, int port, string path, CancellationToken token) =>
        DockerAsync(["exec", container, "/bin/bash", "-c", $"exec 3<>/dev/tcp/127.0.0.1/{port} || exit 0; printf 'GET {path} HTTP/1.1\\r\\nHost: qualification.invalid\\r\\nConnection: close\\r\\n\\r\\n' >&3; cat <&3"], token);

    private static WorldScreen Screen(int index, string machine) => new(Index: index, Origin: new(0, 1, 3 + index),
        Right: Vector3.UnitX, Up: Vector3.UnitY, HalfWidth: 0.3f, HalfHeight: 0.27f, HalfDepth: 0.03f, Round: 0,
        Source: new WorldScreenSource.Machine(machine, "video"), Route: WorldScreenRoute.Passive);

    private static Task<string> DockerAsync(IReadOnlyList<string> arguments, CancellationToken token) =>
        CliProcess.RunCheckedAsync(Environment.CurrentDirectory, "docker", arguments, capture: true, cancellationToken: token);
}
