using System.Numerics;
using System.Text.Json;

using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.World.Client;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// <c>world.flag</c> on the CPU: it refuses a missing or unsafe exhibit and a caller with no seat, resolves <c>near</c>
/// to the spawn point nearest the seat's body, writes the record beside the package with the seat's camera when one is
/// published, and says why no capture was armed when no renderer can take one.
/// </summary>
public sealed class WorldFlagCommandModuleLawTests {
    private static WorldDefinition Definition(params WorldSpawnPoint[] spawns) => (Fixtures.BuildDocument() with {
        SpawnPointsRaw = spawns,
    });

    private static (CommandRegistry Registry, TextCommandSource Source) Registry(WorldDefinition definition, WorldCaptureScheduler scheduler, string package, WorldRenderProbe? probe = null, WorldSeatViewports? viewports = null) {
        var registry = new CommandRegistry(modules: [new WorldFlagCommandModule(
            captureScheduler: scheduler,
            client: ClientFixtures.Client(definition: definition),
            link: new SilentLink(),
            packageDirectory: () => package,
            renderProbe: probe,
            viewports: viewports
        )]);

        return (registry, new TextCommandSource(registry: registry));
    }
    private static WorldCaptureScheduler Scheduler(WorldFixture fixture) => new(
        backend: "directx",
        captureTarget: null,
        directory: string.Empty,
        server: fixture.Server,
        worldFile: "fixture.world.json"
    );

    [Fact]
    public void AFlagRecordsTheExhibitAndRefusesWhatItCannotName() {
        using var fixture = Fixtures.FreshServer();
        var package = Directory.CreateTempSubdirectory(prefix: "puck-flag-");

        try {
            var definition = Definition(new WorldSpawnPoint(Id: "far", Position: new Vector3(x: 9f, y: 0f, z: 9f)), new WorldSpawnPoint(Id: "close", Position: new Vector3(x: 1f, y: 0f, z: 1f)));
            var viewports = new WorldSeatViewports();

            viewports.Publish(
                camera: CameraSnapshot.LookAt(position: new Vector3(x: 0f, y: 2f, z: -4f), target: Vector3.Zero, fieldOfViewRadians: 1f, viewportWidth: 16f, viewportHeight: 9f),
                height: 9,
                region: new NormalizedRect(X: 0f, Y: 0f, Width: 1f, Height: 1f),
                slot: 0,
                width: 16
            );

            var (registry, source) = Registry(definition: definition, package: package.FullName, scheduler: Scheduler(fixture: fixture), viewports: viewports);

            Assert.StartsWith(expectedStartString: "[world.flag: expected", actualString: registry.Submit(line: "world.flag").Output, comparisonType: StringComparison.Ordinal);
            Assert.StartsWith(expectedStartString: "[world.flag: exhibit '..'", actualString: registry.Submit(line: "world.flag ..").Output, comparisonType: StringComparison.Ordinal);

            using (var peer = source.CreateSession(principal: Principal.Peer(index: 4, generation: 1))) {
                Assert.StartsWith(expectedStartString: "[world.flag: flagging requires", actualString: registry.SubmitSession(line: "world.flag close", session: peer).Output, comparisonType: StringComparison.Ordinal);
            }

            var flagged = registry.Submit(line: "world.flag near looks off");

            Assert.False(condition: flagged.IsError, userMessage: flagged.Output);
            Assert.StartsWith(expectedStartString: "[world.flag: close \"looks off\"", actualString: flagged.Output, comparisonType: StringComparison.Ordinal);
            Assert.Contains(expectedSubstring: "no capture armed (no renderer to capture (headless))", actualString: flagged.Output, comparisonType: StringComparison.Ordinal);

            using var record = JsonDocument.Parse(json: File.ReadAllText(path: Path.Combine(path1: package.FullName, path2: WorldFlagCommandModule.FolderName, path3: "close.json")));

            Assert.Equal(expected: "looks off", actual: record.RootElement.GetProperty(propertyName: "note").GetString());
            Assert.Equal(expected: JsonValueKind.Object, actual: record.RootElement.GetProperty(propertyName: "camera").ValueKind);
            Assert.Equal(expected: JsonValueKind.Null, actual: record.RootElement.GetProperty(propertyName: "capture").ValueKind);
        } finally {
            package.Delete(recursive: true);
        }
    }
    [Fact]
    public void NearRefusesWhenNoSafeSpawnPointStandsAndTheFolderRefusesWhatItCannotWrite() {
        using var fixture = Fixtures.FreshServer();
        var package = Directory.CreateTempSubdirectory(prefix: "puck-flag-");

        try {
            var scheduler = Scheduler(fixture: fixture);
            var (none, _) = Registry(definition: Definition(), package: package.FullName, scheduler: scheduler);
            var (unsafeName, _) = Registry(definition: Definition(new WorldSpawnPoint(Id: "a$b", Position: Vector3.Zero)), package: package.FullName, scheduler: scheduler);
            var (building, _) = Registry(definition: Definition(new WorldSpawnPoint(Id: "x", Position: Vector3.Zero)), package: package.FullName, probe: new WorldRenderProbe(), scheduler: scheduler);
            var file = Path.Combine(path1: package.FullName, path2: "not-a-folder");

            File.WriteAllText(path: file, contents: string.Empty);

            var (blocked, _) = Registry(definition: Definition(), package: file, scheduler: scheduler);

            Assert.StartsWith(expectedStartString: "[world.flag: 'near' names", actualString: none.Submit(line: "world.flag near").Output, comparisonType: StringComparison.Ordinal);
            Assert.StartsWith(expectedStartString: "[world.flag: the nearest spawn point 'a$b'", actualString: unsafeName.Submit(line: "world.flag near").Output, comparisonType: StringComparison.Ordinal);
            Assert.Contains(expectedSubstring: "no capture armed (the renderer is not built yet)", actualString: building.Submit(line: "world.flag near").Output, comparisonType: StringComparison.Ordinal);
            Assert.StartsWith(expectedStartString: "[world.flag: could not create the flags folder", actualString: blocked.Submit(line: "world.flag x").Output, comparisonType: StringComparison.Ordinal);

            // A folder standing where the record goes cannot be written.
            _ = Directory.CreateDirectory(path: Path.Combine(path1: package.FullName, path2: WorldFlagCommandModule.FolderName, path3: "y.json"));
            Assert.StartsWith(expectedStartString: "[world.flag: could not write", actualString: building.Submit(line: "world.flag y").Output, comparisonType: StringComparison.Ordinal);
        } finally {
            package.Delete(recursive: true);
        }
    }
}
