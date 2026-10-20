using System.Numerics;
using System.Text.Json;
using Puck.Abstractions;
using Puck.Abstractions.Machines;
using Puck.HumbleGamingDeck;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Machines.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the <c>humble-gaming-deck</c> engine hosts a shipped NES cartridge in a world, behind two
/// controller ports an engaged screen reaches only through the port its route names, and refuses what it cannot run by
/// name; a screen-owned machine follows the same port rule.
/// </summary>
public sealed class DeckEngineLawTests {
    private static readonly WorldScreenRoute Engageable = new(
        Engageable: true,
        EngageRadius: 1f
    );

    private static string Cartridge(string file) => Path.Combine(
        path1: RepositoryPaths.Resolve(relativePath: "src/Puck.World/Assets/cartridges/deck"),
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
        Assert.False(condition: catalog.ContentProviders.ContainsKey(key: HumbleGamingDeckEngine.EngineId));

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
    [Fact]
    public void AScreenOwnedMultiPortMachineDrivesOnlyItsRoutedPort() {
        var engine = new TwoPortEngine();
        var catalog = WorldMachineCatalog.From(extensions: PuckExtensionSet.Compose(extensions: [new TestExtension(
            name: "two-port",
            register: registry => registry.AddMachineEngine(engine: engine)
        )]));
        var content = Cartridge(file: "colour-bars.nes");
        var unrouted = Screen(
            route: Engageable,
            source: new WorldScreenSource.None()
        );

        using (var host = new WorldMachineHost(
            catalog: catalog,
            screens: [unrouted]
        )) {
            var refused = host.TryInsert(
                contentPath: content,
                engineId: engine.Id,
                index: 0,
                options: null
            );

            Assert.False(condition: refused.Ok);
            Assert.Contains(
                actualString: refused.Message,
                comparisonType: StringComparison.Ordinal,
                expectedSubstring: "must name the one it drives"
            );
            Assert.True(condition: Assert.Single(collection: engine.Created).Disposed);
        }

        using var routed = new WorldMachineHost(
            catalog: catalog,
            screens: [unrouted with { Route = Engageable with { Port = "second" } }]
        );
        var inserted = routed.TryInsert(
            contentPath: content,
            engineId: engine.Id,
            index: 0,
            options: null
        );

        Assert.True(
            condition: inserted.Ok,
            userMessage: inserted.Message
        );
        routed.Advance(
            840,
            new ScreenPadSnapshot[] { new(
                Pad: MachinePadState.Neutral with { Buttons = MachineButtons.South },
                ScreenIndex: 0
            ) }
        );

        var runtime = engine.Created[1];

        Assert.Equal(
            expected: MachineButtons.None,
            actual: runtime.Ports["first"].State.Buttons
        );
        Assert.Equal(
            expected: MachineButtons.South,
            actual: runtime.Ports["second"].State.Buttons
        );
    }

    private sealed class TwoPortEngine : IMachineEngine {
        public List<TwoPortRuntime> Created { get; } = [];
        public MachineEngineDescriptor Descriptor { get; } = new(
            "two-port",
            "Two controller ports",
            new(
                Fields: [],
                Id: "puck.two-port.v1"
            ),
            [],
            [],
            [
                new(
                    Contract: "puck.machine.pad.v1",
                    Description: "Controller one",
                    Name: "first"
                ),
                new(
                    Contract: "puck.machine.pad.v1",
                    Description: "Controller two",
                    Name: "second"
                ),
            ],
            [],
            []
        );

        public string Id => "two-port";

        public IMachineRuntime Create(string? options, byte[]? contentBytes = null, string? savePath = null, int audioSampleRate = 0) {
            var runtime = new TwoPortRuntime();

            Created.Add(item: runtime);
            return runtime;
        }
        public IMachineRuntime CreateMachine(MachineCreationRequest request) =>
            Create(options: null);
    }
    private sealed class TwoPortRuntime : IMachineRuntime, IMachineInputPorts {
        public bool Disposed { get; private set; }
        public Dictionary<string, Port> Ports { get; } = new(comparer: StringComparer.Ordinal) {
            ["first"] = new(),
            ["second"] = new(),
        };
        public MachineRuntimeStatus Status => MachineRuntimeStatus.Running;

        IReadOnlyDictionary<string, IMachineInputPort> IMachineInputPorts.InputPorts => Ports.ToDictionary(
            comparer: StringComparer.Ordinal,
            elementSelector: static pair => ((IMachineInputPort)pair.Value),
            keySelector: static pair => pair.Key
        );

        public bool Advance(ulong deltaTicks) => true;
        public void Dispose() => Disposed = true;
    }
    private sealed class Port : IMachineInputPort {
        public MachinePadState State { get; private set; }

        public void SetState(in MachinePadState state) => State = state;
    }
}
