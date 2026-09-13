using System.Numerics;
using System.Text.Json;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Machines;
using Puck.World.Machines;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Independent instance lifetime and candidate preparation, without emulator or renderer assumptions.</summary>
public sealed class NamedMachineLifetimeLawTests {
    private static WorldDefinition Document(params WorldMachine[] machines) => Fixtures.BuildDocument() with { MachinesRaw = machines, ScreensRaw = null };
    private static WorldMachineHost Host(IMachineEngine engine) => new(
        [],
        new WorldMachineCatalog([engine])
    );
    private static WorldMachine InputRow(string name) => new(
        name,
        "input-counter",
        Json(json: "{\"schema\":\"puck.input-counter.v1\"}")
    );
    private static void Install(WorldMachineHost host, WorldDefinition? current, WorldDefinition next) {
        Assert.True(
            condition: host.TryPrepare(
                candidate: next,
                current: current,
                plan: out var plan,
                reason: out var reason
            ),
            userMessage: reason
        );
        using (plan) {
            host.Commit(plan: plan!);
            host.Finish(plan: plan!);
        }
    }
    private static JsonElement Json(string json) {
        using var document = JsonDocument.Parse(json);

        return document.RootElement.Clone();
    }
    private static WorldScreen Panel() => new(
        0,
        Vector3.Zero,
        Vector3.UnitX,
        Vector3.UnitY,
        1,
        1,
        0.01f,
        0,
        new WorldScreenSource.None(),
        WorldScreenRoute.Passive
    );
    private static WorldMachine Row(string name, int seed = 0) => new(
        name,
        "counter",
        Json(json: $"{{\"schema\":\"puck.counter.config.v1\",\"seed\":{seed}}}")
    );

    [Fact]
    public void AFaultLaterInPreparationDisposesOnlyStagedDevices() {
        var engine = new CounterEngine();
        using var host = Host(engine: engine);
        var live = Document(Row("clock"));

        Install(
            current: null,
            host: host,
            next: live
        );
        var before = host.InstanceState(name: "clock");
        var replacement = Document(
            Row(
                name: "clock",
                seed: 1
            ),
            Row(
                name: "broken",
                seed: -1
            )
        );

        Assert.False(condition: host.TryPrepare(
            candidate: replacement,
            current: live,
            plan: out var plan,
            reason: out var reason
        ));
        Assert.Null(@object: plan);
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "broken"
        );
        Assert.Equal(
            before,
            host.InstanceState(name: "clock")
        );
        Assert.Equal(
            2,
            engine.Created.Count
        );
        Assert.False(condition: engine.Created[0].Disposed);
        Assert.True(condition: engine.Created[1].Disposed);
        host.Advance(
            840,
            ReadOnlyMemory<ScreenPadSnapshot>.Empty
        );
        Assert.Equal(
            840UL,
            engine.Created[0].Ticks
        );
    }
    [Fact]
    public void AStoppedInstanceRetainsStateAndResumesWithoutReplacement() {
        var engine = new CounterEngine();
        using var host = Host(engine: engine);
        var running = Document(Row("clock"));

        Install(
            current: null,
            host: host,
            next: running
        );
        host.Advance(
            840,
            ReadOnlyMemory<ScreenPadSnapshot>.Empty
        );
        var stopped = Document(Row("clock") with { Running = false });

        Install(
            current: running,
            host: host,
            next: stopped
        );
        host.Advance(
            840,
            ReadOnlyMemory<ScreenPadSnapshot>.Empty
        );
        Assert.False(condition: host.InstanceState(name: "clock")!.Value.Running);
        var runtime = Assert.Single(collection: engine.Created);

        Assert.Equal(
            840UL,
            runtime.Ticks
        );
        Install(
            current: stopped,
            host: host,
            next: running
        );
        host.Advance(
            420,
            ReadOnlyMemory<ScreenPadSnapshot>.Empty
        );
        Assert.Single(collection: engine.Created);
        Assert.Equal(
            1260UL,
            runtime.Ticks
        );
    }
    [Fact]
    public void AbandoningAValidReplacementLeavesTheLiveDeviceUsable() {
        var engine = new CounterEngine();
        using var host = Host(engine: engine);
        var live = Document(Row("clock"));

        Install(
            current: null,
            host: host,
            next: live
        );
        Assert.True(
            condition: host.TryPrepare(
                live,
                Document(Row(
                    name: "clock",
                    seed: 1
                )),
                out var plan,
                out var reason
            ),
            userMessage: reason
        );
        plan!.Dispose();
        Assert.False(condition: engine.Created[0].Disposed);
        Assert.True(condition: engine.Created[1].Disposed);
        Assert.Throws<InvalidOperationException>(testCode: () => host.Commit(plan: plan));
        host.Advance(
            840,
            ReadOnlyMemory<ScreenPadSnapshot>.Empty
        );
        Assert.Equal(
            840UL,
            engine.Created[0].Ticks
        );
    }
    [Fact]
    public void HeadlessMultiPortMachineAdvancesWithNeutralInputs_WhileDisplayedRouteIsRefused() {
        var engine = new InputEngine(portCount: 2);
        using var host = Host(engine: engine);
        var headless = Document(InputRow(name: "headless"));

        Install(
            current: null,
            host: host,
            next: headless
        );
        var runtime = Assert.Single(collection: engine.Created);

        host.Advance(
            840,
            ReadOnlyMemory<ScreenPadSnapshot>.Empty
        );
        Assert.Equal(
            840UL,
            runtime.Ticks
        );
        Assert.All(
            runtime.Ports.Values,
            port => Assert.Equal(
                MachinePadState.Neutral,
                port.State
            )
        );

        var displayed = headless with {
            ScreensRaw = [Panel() with { Source = new WorldScreenSource.Machine(
                Instance: "headless",
                Output: "video"
            ) }],
        };

        Assert.False(condition: host.TryPrepare(
            candidate: displayed,
            current: headless,
            plan: out var refused,
            reason: out var reason
        ));
        Assert.Null(@object: refused);
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "multiple input ports"
        );
        Assert.False(condition: runtime.Disposed);
    }
    [Fact]
    public void LinkRejectsDuplicateNamedMemberAndStoppedNamedMemberBeforeTopologyChange() {
        var engine = new CounterEngine(
            withOutputs: true,
            withLinking: true
        );
        using var host = Host(engine: engine);
        var duplicateScreens = new[] {
            Panel() with { Source = new WorldScreenSource.Machine(
            Instance: "left",
            Output: "video"
        ) },
            Panel() with { Index = 1, Source = new WorldScreenSource.Machine(
            Instance: "left",
            Output: "video"
        ) },
        };

        Install(
            host,
            null,
            Document(Row("left")) with { ScreensRaw = duplicateScreens }
        );
        var duplicate = host.TryLink(
            members: [0, 1],
            name: "duplicate"
        );

        Assert.False(condition: duplicate.Ok);
        Assert.Contains(
            actualString: duplicate.Message,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "named more than once"
        );

        var stopped = Document(
            Row("left") with { Running = false },
            Row("right")
        ) with {
            ScreensRaw = [Panel() with { Source = new WorldScreenSource.Machine(
                Instance: "left",
                Output: "video"
            ) },
                Panel() with { Index = 1, Source = new WorldScreenSource.Machine(
                Instance: "right",
                Output: "video"
            ) }],
        };

        Install(
            host,
            Document(Row("left")) with { ScreensRaw = duplicateScreens },
            stopped
        );
        var stoppedResult = host.TryLink(
            members: [0, 1],
            name: "stopped"
        );

        Assert.False(condition: stoppedResult.Ok);
        Assert.Contains(
            actualString: stoppedResult.Message,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "stopped"
        );
    }
    [Fact]
    public void LinkedNamedReplacementAndOperationRefuseBeforeRetiringRuntime_AndUnlinkAllowsReplacement() {
        var engine = new CounterEngine(
            withOutputs: true,
            withLinking: true,
            withOperations: true
        );
        using var host = Host(engine: engine);
        var screens = new[] {
            Panel() with { Source = new WorldScreenSource.Machine(
            Instance: "left",
            Output: "video"
        ) },
            Panel() with { Index = 1, Source = new WorldScreenSource.Machine(
            Instance: "right",
            Output: "video"
        ) },
        };
        var document = Document(
            Row("left"),
            Row("right")
        ) with { ScreensRaw = screens };

        Install(
            current: null,
            host: host,
            next: document
        );
        Assert.True(condition: host.TryLink(
            members: [0, 1],
            name: "cable"
        ).Ok);
        var beforeGeneration = host.InstanceState(name: "left")!.Value.Generation;
        var beforeTopology = host.DescribeLinks();

        Assert.False(condition: host.TryPrepare(
            document,
            Document(
                Row(
                    name: "left",
                    seed: 1
                ),
                Row("right")
            ) with { ScreensRaw = screens },
            out var refusedPlan,
            out var refusal
        ));
        Assert.Null(@object: refusedPlan);
        Assert.Contains(
            actualString: refusal,
            comparisonType: StringComparison.OrdinalIgnoreCase,
            expectedSubstring: "unlink"
        );
        Assert.Equal(
            beforeGeneration,
            host.InstanceState(name: "left")!.Value.Generation
        );
        Assert.Equal(
            beforeTopology,
            host.DescribeLinks()
        );
        Assert.False(condition: engine.Created[0].Disposed);

        var operation = new MachineOperationRequest(
            id: "counter.noop",
            payload: Json(json: "{}")
        );

        Assert.False(condition: host.TryPrepareOperation(
            expectedGeneration: beforeGeneration,
            instance: "left",
            plan: out var operationPlan,
            refusal: out var operationRefusal,
            request: operation
        ));
        Assert.Null(@object: operationPlan);
        Assert.Equal(
            MachineOperationStatus.Refused,
            operationRefusal.Status
        );
        Assert.Contains(
            "unlink",
            operationRefusal.Reason,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.Equal(
            beforeGeneration,
            host.InstanceState(name: "left")!.Value.Generation
        );
        Assert.Equal(
            beforeTopology,
            host.DescribeLinks()
        );
        Assert.False(condition: engine.Created[0].Disposed);

        Assert.True(condition: host.TryUnlink(name: "cable").Ok);
        Install(
            host,
            document,
            Document(
                Row(
                    name: "left",
                    seed: 1
                ),
                Row("right")
            ) with { ScreensRaw = screens }
        );
        Assert.True(condition: engine.Created[0].Disposed);
        Assert.True(condition: (host.InstanceState(name: "left")!.Value.Generation > beforeGeneration));
        Assert.Equal(
            "none",
            host.DescribeLinks()
        );
    }
    [Fact]
    public void NamedLinkMembersAdvanceOnce_AndRefusedRelinkPreservesTopology() {
        var engine = new CounterEngine(
            withOutputs: true,
            withLinking: true
        );
        using var host = Host(engine: engine);
        var screens = new[] {
            Panel() with { Source = new WorldScreenSource.Machine(
            Instance: "left",
            Output: "video"
        ) },
            Panel() with { Index = 1, Source = new WorldScreenSource.Machine(
            Instance: "right",
            Output: "video"
        ) },
        };
        var document = Document(
            Row("left"),
            Row("right")
        ) with { ScreensRaw = screens };

        Install(
            current: null,
            host: host,
            next: document
        );
        Assert.True(condition: host.TryLink(
            members: [0, 1],
            name: "cable"
        ).Ok);
        var before = host.DescribeLinks();

        Assert.False(condition: host.TryLink(
            members: [0, 1],
            name: "other"
        ).Ok);
        Assert.Equal(
            before,
            host.DescribeLinks()
        );

        host.Advance(
            840,
            ReadOnlyMemory<ScreenPadSnapshot>.Empty
        );
        Assert.Equal(
            840UL,
            engine.Created[0].Ticks
        );
        Assert.Equal(
            840UL,
            engine.Created[1].Ticks
        );
        Assert.Equal(
            1,
            engine.Created[0].AdvanceCalls
        );
        Assert.Equal(
            1,
            engine.Created[1].AdvanceCalls
        );
    }
    [Fact]
    public void NamedReadHelpersUseTheAuthoredOutputAndLiveRuntime() {
        var engine = new CounterEngine(
            withOutputs: true,
            output: "wide"
        );
        using var host = Host(engine: engine);
        var screen = Panel() with { Source = new WorldScreenSource.Machine(
            Instance: "clock",
            Output: "wide"
        ) };

        Install(
            host,
            null,
            Document(Row("clock")) with { ScreensRaw = [screen] }
        );

        Assert.Same(
            host.VideoOutput(
                instance: "clock",
                output: "wide"
            ),
            host.VideoOutput(index: 0)
        );
        Assert.Single(collection: host.MachineScreenIndices);
        Assert.Equal(
            0,
            host.MachineScreenIndices.Single()
        );
        Assert.True(condition: host.TryPeekMessage(
            address: 7,
            index: 0,
            value: out var before
        ).Ok);
        Assert.Equal(
            actual: before,
            expected: 0
        );
        Assert.True(condition: host.TryPokeMessage(
            address: 7,
            index: 0,
            value: 42
        ).Ok);
        Assert.True(condition: host.TryPeekMessage(
            address: 7,
            index: 0,
            value: out var after
        ).Ok);
        Assert.Equal(
            actual: after,
            expected: 42
        );
    }
    [Fact]
    public void NamedScreenRetargetTearsDownCableBeforePublishingTheNewConsumer() {
        var engine = new CounterEngine(
            withOutputs: true,
            withLinking: true
        );
        using var host = Host(engine: engine);
        var screens = new[] {
            Panel() with { Source = new WorldScreenSource.Machine(
            Instance: "left",
            Output: "video"
        ) },
            Panel() with { Index = 1, Source = new WorldScreenSource.Machine(
            Instance: "right",
            Output: "video"
        ) },
        };
        var document = Document(
            Row("left"),
            Row("right")
        ) with { ScreensRaw = screens };

        Install(
            current: null,
            host: host,
            next: document
        );
        Assert.True(condition: host.TryLink(
            members: [0, 1],
            name: "cable"
        ).Ok);

        var retargeted = document with {
            ScreensRaw = [
                screens[0] with { Source = new WorldScreenSource.Machine(
                Instance: "right",
                Output: "video"
            ) },
                screens[1],
            ],
        };

        Install(
            current: document,
            host: host,
            next: retargeted
        );

        Assert.Equal(
            "none",
            host.DescribeLinks()
        );
        Assert.Null(@object: host.LinkOf(index: 0));
        Assert.Null(@object: host.LinkOf(index: 1));
        Assert.False(condition: engine.Created[0].Disposed);
        Assert.False(condition: engine.Created[1].Disposed);
        Assert.Same(
            host.VideoOutput(
                instance: "right",
                output: "video"
            ),
            host.VideoOutput(index: 0)
        );
    }
    [Fact]
    public void OrdinaryMutationAndUndoPrepareDevicesAtTheAuthorityBoundary() {
        var engine = new CounterEngine();
        using var fixture = Fixtures.FreshServer(
            Document(),
            engines: [engine]
        );

        fixture.Server.EnqueueMutation(new WorldMutation.UpsertMachine(
            WorldPrincipal.Console,
            Row("clock")
        ));
        fixture.Step();
        var original = Assert.Single(collection: engine.Created);

        Assert.NotNull(value: fixture.Server.Machines.InstanceState(name: "clock"));

        fixture.Server.EnqueueMutation(new WorldMutation.UpsertMachine(
            WorldPrincipal.Console,
            Row(
                name: "clock",
                seed: -1
            )
        ));
        fixture.Step();
        Assert.Single(collection: engine.Created);
        Assert.False(condition: original.Disposed);
        Assert.Equal(
            0,
            fixture.Server.Definition.Machines[0].Configuration.GetProperty(propertyName: "seed").GetInt32()
        );

        fixture.Server.EnqueueMutation(new WorldMutation.RemoveMachine(
            WorldPrincipal.Console,
            "clock"
        ));
        fixture.Step();
        Assert.True(condition: original.Disposed);
        Assert.Null(value: fixture.Server.Machines.InstanceState(name: "clock"));

        fixture.Server.EnqueueUndo(
            count: 1,
            principal: WorldPrincipal.Console
        );
        fixture.Step();
        Assert.NotNull(value: fixture.Server.Machines.InstanceState(name: "clock"));
        Assert.Equal(
            2,
            engine.Created.Count
        );
        Assert.False(condition: engine.Created[1].Disposed);
    }
    [Fact]
    public void PassiveFirstDisplayDoesNotMaskAnEngagedSecondDisplay_AndRemovalReleasesInput() {
        var engine = new InputEngine(portCount: 1);
        using var host = Host(engine: engine);
        var first = Panel() with { Source = new WorldScreenSource.Machine(
            Instance: "controls",
            Output: "video"
        ) };
        var second = first with { Index = 1 };
        var document = Document(InputRow(name: "controls")) with { ScreensRaw = [first, second] };

        Install(
            current: null,
            host: host,
            next: document
        );
        var runtime = Assert.Single(collection: engine.Created);
        var pressed = MachinePadState.Neutral with { Buttons = MachineButtons.South };

        host.Advance(
            840,
            new ScreenPadSnapshot[] { new(
                Pad: pressed,
                ScreenIndex: 1
            ) }
        );
        Assert.Equal(
            MachineButtons.South,
            runtime.LastInput.Buttons
        );

        _ = host.ReconcileScreens(screens: [first]);
        host.Advance(
            840,
            ReadOnlyMemory<ScreenPadSnapshot>.Empty
        );
        Assert.Equal(
            MachineButtons.None,
            runtime.LastInput.Buttons
        );
        Assert.False(condition: runtime.Disposed);
    }
    [Fact]
    public void PreparedOperationRefusesIfALiveCableAppearsBeforeCommit() {
        var engine = new CounterEngine(
            withOutputs: true,
            withLinking: true,
            withOperations: true
        );
        using var host = Host(engine: engine);
        var screens = new[] {
            Panel() with { Source = new WorldScreenSource.Machine(
            Instance: "left",
            Output: "video"
        ) },
            Panel() with { Index = 1, Source = new WorldScreenSource.Machine(
            Instance: "right",
            Output: "video"
        ) },
        };
        var document = Document(
            Row("left"),
            Row("right")
        ) with { ScreensRaw = screens };

        Install(
            current: null,
            host: host,
            next: document
        );
        var generation = host.InstanceState(name: "left")!.Value.Generation;
        var operation = new MachineOperationRequest(
            id: "counter.noop",
            payload: Json(json: "{}")
        );

        Assert.True(
            condition: host.TryPrepareOperation(
                expectedGeneration: generation,
                instance: "left",
                plan: out var plan,
                refusal: out var preparationRefusal,
                request: operation
            ),
            userMessage: preparationRefusal.Reason
        );
        using (plan!) {
            Assert.True(condition: host.TryLink(
                members: [0, 1],
                name: "cable"
            ).Ok);
            var result = host.TryCommitOperation(plan: plan!);

            Assert.Equal(
                MachineOperationStatus.Refused,
                result.Status
            );
            Assert.Contains(
                "unlink",
                result.Reason,
                StringComparison.OrdinalIgnoreCase
            );
        }
        Assert.Equal(
            generation,
            host.InstanceState(name: "left")!.Value.Generation
        );
        Assert.False(condition: engine.Created[0].Disposed);
        Assert.True(condition: host.TryUnlink(name: "cable").Ok);
    }
    [Fact]
    public void RefusedMachineBatchDoesNotPublishCandidateSolidField() {
        var engine = new CounterEngine();
        var authored = Fixtures.BuildDocument();
        using var fixture = Fixtures.FreshServer(
            definition: authored,
            engines: [engine]
        );

        var beforeBytes = fixture.DefinitionBytes();
        var beforeField = fixture.Server.SolidField;
        var beforeSolidRevision = fixture.Server.SolidRevision;
        var beforePopulationRevision = fixture.Server.Population.Revision;
        var beforeJournalLength = fixture.Server.JournalLength;
        WorldEditEcho? refusal = null;

        fixture.Server.EchoTap = echo => { if (echo.Rejected) { refusal = echo; } };
        var candidateCollision = authored.Collision with {
            Requirements = [WorldContactRequirement.SmoothUnionContact],
        };

        fixture.Server.EnqueueMutation(new WorldMutation.Batch(
            Principal: WorldPrincipal.Console,
            Mutations: [
                new WorldMutation.SetCollision(
                    Principal: WorldPrincipal.Console,
                    Collision: candidateCollision
                ),
                new WorldMutation.UpsertMachine(
                    Principal: WorldPrincipal.Console,
                    Machine: Row(
                        name: "broken",
                        seed: -1
                    )
                ),
            ]
        ));
        fixture.Step();

        Assert.Equal(
            expected: beforeBytes,
            actual: fixture.DefinitionBytes()
        );
        Assert.Same(
            expected: beforeField,
            actual: fixture.Server.SolidField
        );
        Assert.Equal(
            expected: beforeSolidRevision,
            actual: fixture.Server.SolidRevision
        );
        Assert.Equal(
            expected: beforePopulationRevision,
            actual: fixture.Server.Population.Revision
        );
        Assert.Equal(
            expected: beforeJournalLength,
            actual: fixture.Server.JournalLength
        );
        Assert.True(
            condition: refusal.HasValue,
            userMessage: "the machine preparation refusal was not observed"
        );
    }
    [Fact]
    public void RemovingADisplayPreservesTheMachineAndItsGeneration() {
        var engine = new CounterEngine();
        using var host = Host(engine: engine);
        var document = Document(Row("clock")) with { ScreensRaw = [Panel()] };

        Install(
            current: null,
            host: host,
            next: document
        );
        var before = host.InstanceState(name: "clock")!.Value.Generation;
        var runtime = Assert.Single(collection: engine.Created);

        host.Advance(
            840,
            ReadOnlyMemory<ScreenPadSnapshot>.Empty
        );
        Install(
            host,
            document,
            document with { ScreensRaw = null }
        );
        host.Advance(
            420,
            ReadOnlyMemory<ScreenPadSnapshot>.Empty
        );
        Assert.Equal(
            before,
            host.InstanceState(name: "clock")!.Value.Generation
        );
        Assert.Single(collection: engine.Created);
        Assert.False(condition: runtime.Disposed);
        Assert.Equal(
            1260UL,
            runtime.Ticks
        );
    }
    [Fact]
    public void StalePlansCannotOverwriteANewerIncarnation() {
        var engine = new CounterEngine();
        using var host = Host(engine: engine);
        var live = Document(Row("clock"));

        Install(
            current: null,
            host: host,
            next: live
        );
        var generation = host.InstanceState(name: "clock")!.Value.Generation;

        Assert.True(
            condition: host.TryPrepare(
                live,
                Document(Row(
                    name: "clock",
                    seed: 1
                )),
                out var stale,
                out var reason
            ),
            userMessage: reason
        );
        using (stale) {
            Install(
                host,
                live,
                Document(Row(
                    name: "clock",
                    seed: 2
                ))
            );
            Assert.Throws<InvalidOperationException>(testCode: () => host.Commit(plan: stale!));
        }
        Assert.True(condition: (host.InstanceState(name: "clock")!.Value.Generation > generation));
        Assert.True(condition: engine.Created[0].Disposed);
        Assert.True(condition: engine.Created[1].Disposed);
        Assert.False(condition: engine.Created[2].Disposed);
    }
    [Fact]
    public void TwoDisplaysShareOneNamedProducerAndOneAdvance() {
        var engine = new CounterEngine(withOutputs: true);
        using var host = Host(engine: engine);
        var screens = new[] { Panel() with { Source = new WorldScreenSource.Machine(
            Instance: "clock",
            Output: "video"
        ) },
            Panel() with { Index = 1, Source = new WorldScreenSource.Machine(
            Instance: "clock",
            Output: "video"
        ) } };
        var document = Document(Row("clock")) with { ScreensRaw = screens };

        Install(
            current: null,
            host: host,
            next: document
        );

        var first = host.VideoOutput(
            instance: "clock",
            output: "video"
        );
        var second = host.VideoOutput(
            instance: "clock",
            output: "video"
        );

        Assert.NotNull(@object: first);
        Assert.Same(
            actual: second,
            expected: first
        );
        host.Advance(
            840,
            ReadOnlyMemory<ScreenPadSnapshot>.Empty
        );
        Assert.Equal(
            1,
            host.InstanceState(name: "clock")!.Value.FramesStepped
        );
        Assert.Single(collection: engine.Created);
    }
    [Fact]
    public void UnknownConfigurationAndDuplicateNamesRefuseBeforeConstruction() {
        var engine = new CounterEngine();
        using var host = Host(engine: engine);

        Assert.False(condition: host.TryPrepare(
            null,
            Document(
                Row("clock"),
                Row("clock")
            ),
            out _,
            out var reason
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "duplicate instance"
        );
        var invalid = Row("clock") with { Configuration = Json(json: "{\"schema\":\"puck.counter.config.v1\",\"typo\":1}") };

        Assert.False(condition: host.TryPrepare(
            null,
            Document(invalid),
            out _,
            out reason
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "configuration.typo"
        );
        Assert.Empty(collection: engine.Created);
    }
    [Fact]
    public void WorldBootsAndAdvancesADeviceWithoutContentOrScreens() {
        var engine = new CounterEngine();
        using var fixture = Fixtures.FreshServer(
            Document(Row("clock")),
            engines: [engine]
        );
        var runtime = Assert.Single(collection: engine.Created);

        fixture.Step(stepTicks: 840);
        fixture.Step(stepTicks: 420);
        Assert.Equal(
            1260UL,
            runtime.Ticks
        );
        Assert.Equal(
            "clock",
            Assert.Single(collection: fixture.Server.Machines.InstanceNames)
        );
        Assert.Equal(
            2,
            fixture.Server.Machines.InstanceState(name: "clock")!.Value.FramesStepped
        );
        Assert.Null(@object: fixture.Server.Machines.VideoOutput(
            instance: "clock",
            output: "video"
        ));
        Assert.Null(@object: fixture.Server.Machines.AudioOutput(
            instance: "clock",
            output: "audio"
        ));
        Assert.Equal(
            MachineAccessStatus.Unsupported,
            fixture.Server.Machines.Inspect(
                "clock",
                new(
                    Address: 0,
                    Space: "bus",
                    Width: 1
                )
            ).Status
        );
        Assert.Equal(
            MachineAccessStatus.Unavailable,
            fixture.Server.Machines.Inspect(
                "absent",
                new(
                    Address: 0,
                    Space: "bus",
                    Width: 1
                )
            ).Status
        );
    }

    private sealed class CounterEngine(bool withOutputs = false, bool withLinking = false, bool withOperations = false, string output = "video") : IMachineEngine, IMachineLinkingEngine, IMachineOperationProvider {
        public string Id => "counter";

        public List<CounterRuntime> Created { get; } = [];
        public MachineEngineDescriptor Descriptor { get; } = new(
            "counter",
            "Counter test device",
            new(
                "puck.counter.config.v1",
                [new(
                        "seed",
                        MachineFieldKind.Integer,
                        "Initial counter"
                    )]
            ),
            (withOutputs
            ? [new(
                        Contract: "puck.machine.video.v1",
                        Description: "Counter frame",
                        Name: output
                    )]
            : []),
            [],
            [],
            (withOperations
            ? [new(
                        "counter.noop",
                        "No-op test operation",
                        new(
                            Fields: [],
                            Id: "puck.counter.operation.v1"
                        )
                    )]
            : []),
            []
        );

        public IMachineRuntime Create(string? options, byte[]? contentBytes = null, string? savePath = null, int audioSampleRate = 0) =>
            throw new InvalidOperationException(message: "The device accepts structured construction only.");
        public IMachineRuntime CreateMachine(MachineCreationRequest request) {
            if (request.Configuration.GetProperty(propertyName: "seed").GetInt32() < 0) {
                throw new ArgumentException(message: "This device refuses a negative seed.");
            }
            var runtime = new CounterRuntime(
                output: output,
                withOutputs: withOutputs
            );

            Created.Add(item: runtime);
            return runtime;
        }
        public MachineOperationPreparation PrepareOperation(MachineCreationRequest current, MachineOperationRequest request) =>
            new MachineOperationPreparation.Runtime(new NoopOperation());
        public bool TryLink(IReadOnlyList<IMachineRuntime> machines, out IMachineLink? link, out string reason) {
            if (
                !withLinking ||
                (machines.Count < 2) ||
                machines.Any(predicate: machine => (machine is not CounterRuntime))
            ) {
                link = null;
                reason = "counter linking is unavailable";
                return false;
            }
            link = new CounterLink(machines: machines);
            reason = string.Empty;
            return true;
        }
    }
    private sealed class NoopOperation : IMachinePreparedOperation {
        public MachineOperationResult Apply(IMachineRuntime runtime) => new(MachineOperationStatus.Applied);
    }
    private sealed class InputEngine(int portCount) : IMachineEngine {
        public string Id => "input-counter";

        public List<InputRuntime> Created { get; } = [];
        public MachineEngineDescriptor Descriptor { get; } = new(
            "input-counter",
            "Input counter",
            new(
                Fields: [],
                Id: "puck.input-counter.v1"
            ),
            [new(
                    Contract: "puck.machine.video.v1",
                    Description: "Input frame",
                    Name: "video"
                )],
            [],
            [.. Enumerable.Range(
                    count: portCount,
                    start: 0
                ).Select(selector: index => new MachinePortDescriptor(
                    Contract: "puck.machine.pad.v1",
                    Description: "Controller",
                    Name: $"port{index}"
                ))],
            [],
            []
        );

        public IMachineRuntime Create(string? options, byte[]? contentBytes = null, string? savePath = null, int audioSampleRate = 0) =>
            throw new InvalidOperationException(message: "The device accepts structured construction only.");
        public IMachineRuntime CreateMachine(MachineCreationRequest request) {
            var runtime = new InputRuntime(portCount: portCount);

            Created.Add(item: runtime);
            return runtime;
        }
    }
    private sealed class InputRuntime(int portCount) : IMachineRuntime, IMachineInputPorts, IMachineVideoOutputs {
        public IReadOnlyDictionary<string, IMachineVideoOutput> VideoOutputs { get; } = new Dictionary<string, IMachineVideoOutput> { ["video"] = new CounterOutput() };
        public Dictionary<string, InputPort> Ports { get; } = Enumerable.Range(
            count: portCount,
            start: 0
        ).ToDictionary(
            index => $"port{index}",
            _ => new InputPort(),
            StringComparer.Ordinal
        );

        IReadOnlyDictionary<string, IMachineInputPort> IMachineInputPorts.InputPorts => Ports.ToDictionary(
            pair => pair.Key,
            pair => ((IMachineInputPort)pair.Value),
            StringComparer.Ordinal
        );

        public bool Disposed { get; private set; }
        public MachinePadState LastInput => Ports.Values.First().State;
        public MachineRuntimeStatus Status => MachineRuntimeStatus.Running;
        public ulong Ticks { get; private set; }

        public bool Advance(ulong deltaTicks) { Assert.False(condition: Disposed); Ticks += deltaTicks; return true; }
        public void Dispose() { Assert.False(condition: Disposed); Disposed = true; }
    }
    private sealed class InputPort : IMachineInputPort {
        public MachinePadState State { get; private set; }

        public void SetState(in MachinePadState state) => State = state;
    }
    private sealed class CounterLink(IReadOnlyList<IMachineRuntime> machines) : IMachineLink {
        public long CompletedTransfers { get; private set; }
        public IReadOnlyList<IMachineRuntime> Machines { get; } = machines;

        public void Dispose() { }
        public void Step(ulong deltaTicks, ReadOnlySpan<MachinePadState> inputs) {
            foreach (var machine in Machines) { _ = machine.Advance(deltaTicks: deltaTicks); }
            CompletedTransfers++;
        }
    }
    private sealed class CounterRuntime(bool withOutputs, string output) : IMachineRuntime, IMachineVideoOutputs, IMachineAudioOutputs, IMachineMemoryPeek {
        public IReadOnlyDictionary<string, IMachineVideoOutput> VideoOutputs { get; } = (withOutputs
            ? new Dictionary<string, IMachineVideoOutput> { [output] = new CounterOutput() }
            : new Dictionary<string, IMachineVideoOutput>()
        );
        public IReadOnlyDictionary<string, IAudioMachine> AudioOutputs { get; } = new Dictionary<string, IAudioMachine>();

        public int AdvanceCalls { get; private set; }
        public bool Disposed { get; private set; }
        public byte MemoryValue { get; private set; }
        public MachineRuntimeStatus Status => MachineRuntimeStatus.Running;
        public ulong Ticks { get; private set; }

        public bool Advance(ulong deltaTicks) {
            Assert.False(condition: Disposed);
            Ticks += deltaTicks;
            AdvanceCalls++;
            return true;
        }
        public void Dispose() {
            Assert.False(condition: Disposed);
            Disposed = true;
        }
        public byte PeekByte(int address) => ((address == 7)
            ? MemoryValue
            : (byte)0
        );
        public void PokeByte(int address, byte value) { if (address == 7) { MemoryValue = value; } }
    }
    private sealed class CounterOutput : IMachineVideoOutput {
        public Vector3 EmittedLight => Vector3.Zero;
        public nint NativeImageViewHandle => 0;

        public void NotifyDeviceLost() { }
        public void PublishFrame(IGpuDeviceContext deviceContext, IGpuComputeServices gpu) { }
    }
}
