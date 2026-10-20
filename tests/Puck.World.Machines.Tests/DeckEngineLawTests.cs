using System.Numerics;
using System.Text.Json;
using Puck.Abstractions.Machines;
using Puck.HumbleGamingDeck;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Machines.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the <c>humble-gaming-deck</c> engine hosts a shipped NES cartridge in a world, behind two
/// controller ports an engaged screen reaches only through the port its route names, and refuses what it cannot run by
/// name.
/// </summary>
public sealed class DeckEngineLawTests {
    private static readonly WorldScreenRoute Engageable = new(
        Engageable: true,
        EngageRadius: 1f
    );

    private static string Cartridge(string file) => Path.Combine(
        path1: RepositoryPaths.Resolve(relativePath: "worlds/debug/cartridges/deck"),
        path2: file
    );
    private static WorldScreen Screen(WorldScreenSource source, WorldScreenRoute route) => new(
        Index: 0,
        Origin: Vector3.Zero,
        Right: Vector3.UnitX,
        Up: Vector3.UnitY,
        HalfWidth: 0.256f,
        HalfHeight: 0.24f,
        HalfDepth: 0.005f,
        Round: 0f,
        Source: source,
        Route: route
    );

    [Fact]
    public void ADeckMachineShowsItsCartridgeOnTheScreenThatNamesItsPort() {
        var catalog = TestMachines.Catalog();

        Assert.True(condition: catalog.IsRegistered(engineId: HumbleGamingDeckEngine.EngineId));
        Assert.False(condition: catalog.HasContentProvider(engineId: HumbleGamingDeckEngine.EngineId));

        var document = Fixtures.BuildDocument();
        var deck = new WorldMachine(
            "deck",
            HumbleGamingDeckEngine.EngineId,
            JsonSerializer.SerializeToElement(new { schema = "puck.humble-gaming-deck.configuration.v1", content = new { path = Cartridge(file: "colour-bars.nes") } })
        );
        var display = Screen(
            route: Engageable,
            source: new WorldScreenSource.Machine(
                Instance: "deck",
                Output: "video"
            )
        );
        using var host = new WorldMachineHost(
            catalog: catalog,
            screens: []
        );

        Assert.False(condition: host.TryPrepare(
            candidate: document with { MachinesRaw = [deck], ScreensRaw = [display] },
            current: null,
            plan: out _,
            reason: out var unrouted
        ));
        Assert.Contains(
            actualString: unrouted,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "controls, controls-2"
        );
        Assert.True(
            condition: host.TryPrepare(
                candidate: document with { MachinesRaw = [deck], ScreensRaw = [display with { Route = Engageable with { Port = "controls" } }] },
                current: null,
                plan: out var plan,
                reason: out var reason
            ),
            userMessage: reason
        );
        using (plan) {
            host.Commit(plan: plan!);
            host.Finish(plan: plan!);
        }

        Assert.Equal(
            expected: MachineRuntimeStatus.Running,
            actual: host.InstanceState(name: "deck")!.Value.Status
        );
        var video = host.VideoOutput(
            instance: "deck",
            output: "video"
        );

        Assert.NotNull(@object: video);
        Assert.Equal(
            expected: (DeckMachineHost.ScreenWidth, DeckMachineHost.ScreenHeight),
            actual: (video.Width, video.Height)
        );
    }
    [Fact]
    public void TheDeckEngineRefusesOptionsAndImagesItCannotRunByName() {
        var engine = new HumbleGamingDeckEngine();

        Assert.Contains(
            actualString: Assert.Throws<ArgumentException>(testCode: () => engine.Create(options: "fast")).Message,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "takes no options"
        );
        Assert.Contains(
            actualString: Assert.Throws<ArgumentException>(testCode: () => engine.Create(
                contentBytes: [0x4E, 0x45, 0x53],
                options: null
            )).Message,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "cannot run the cartridge"
        );

        using var empty = engine.Create(options: null);

        Assert.Equal(
            expected: MachineRuntimeStatus.Empty,
            actual: empty.Status
        );

        using var configuration = JsonDocument.Parse(json: "{\"schema\":\"puck.humble-gaming-deck.configuration.v1\"}");
        using var payload = JsonDocument.Parse(json: "{\"schema\":\"puck.humble-gaming-deck.content-insert.v1\",\"content\":{\"path\":\"moving-sprite.nes\"}}");
        var inserted = Assert.IsType<MachineOperationPreparation.Replacement>(@object: engine.PrepareOperation(
            current: new MachineCreationRequest(
                Assets: new Dictionary<string, PreparedMachineAsset>(),
                Configuration: configuration.RootElement
            ),
            request: new MachineOperationRequest(
                id: "content.insert",
                payload: payload.RootElement
            )
        ));

        Assert.Equal(
            expected: "moving-sprite.nes",
            actual: inserted.Configuration.GetProperty(propertyName: "content").GetProperty(propertyName: "path").GetString()
        );
    }
}
