using Xunit;

namespace Puck.Commands.Tests;

public sealed class CommandModalityTests {
    private const string Foot = "foot";
    private const string Menu = "menu";
    private const string Plan = "plan";
    private const string Tank = "tank";

    [Fact]
    public void ThreeSlotsResolveTankFootAndPlanIndependentlyFromTheSameSource() {
        var activations = new List<(string Name, int Slot)>();
        var registry = Registry(
            activations,
            new Spec(Map: CommandMaps.Global, Name: "common"),
            new Spec(Map: Tank, Name: "tank.fire"),
            new Spec(Map: Foot, Name: "foot.jump"),
            new Spec(Map: Plan, Name: "plan.place")
        );
        var tankDevice = InputDeviceId.FromConnectionKey(key: "tank-pad");
        var footDevice = InputDeviceId.FromConnectionKey(key: "foot-pad");
        var planDevice = InputDeviceId.FromConnectionKey(key: "plan-pad");
        var router = new InputRouter(
            registry: registry,
            bindings: new SameSourceBindings("common", "tank.fire", "foot.jump", "plan.place"),
            principalResolver: new SeatPrincipals(),
            slotResolver: device => ((device == tankDevice) ? 0 : ((device == footDevice) ? 1 : 2))
        );

        router.SetActiveMaps(maps: [Tank], slot: 0);
        router.SetActiveMaps(maps: [Foot], slot: 1);
        router.SetActiveMaps(maps: [Plan], slot: 2);
        router.Capture(signal: InputSignal.Press(source: "button.action", deviceId: tankDevice));
        router.Capture(signal: InputSignal.Press(source: "button.action", deviceId: footDevice));
        router.Capture(signal: InputSignal.Press(source: "button.action", deviceId: planDevice));

        var snapshot = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);

        registry.ApplySnapshot(snapshot: in snapshot);

        Assert.Equal(
            actual: activations,
            expected: [
                ("common", 0), ("tank.fire", 0),
                ("common", 1), ("foot.jump", 1),
                ("common", 2), ("plan.place", 2),
            ]
        );
    }
    [Fact]
    public void ReplacingOneSlotsModalityDoesNotChangeAnotherSlot() {
        var activations = new List<(string Name, int Slot)>();
        var registry = Registry(
            activations,
            new Spec(Map: Foot, Name: "foot.jump"),
            new Spec(Map: Menu, Name: "menu.accept")
        );
        var menuDevice = InputDeviceId.FromConnectionKey(key: "menu-pad");
        var footDevice = InputDeviceId.FromConnectionKey(key: "foot-pad");
        var router = new InputRouter(
            registry: registry,
            bindings: new SameSourceBindings("foot.jump", "menu.accept"),
            principalResolver: new SeatPrincipals(),
            slotResolver: device => ((device == menuDevice) ? 0 : 1)
        );

        router.SetActiveMaps(maps: [Menu], slot: 0);
        router.SetActiveMaps(maps: [Foot], slot: 1);
        router.Capture(signal: InputSignal.Press(source: "button.action", deviceId: menuDevice));
        router.Capture(signal: InputSignal.Press(source: "button.action", deviceId: footDevice));

        var snapshot = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);

        registry.ApplySnapshot(snapshot: in snapshot);

        Assert.Equal(actual: activations, expected: [("menu.accept", 0), ("foot.jump", 1)]);
        Assert.True(condition: router.IsMapActive(map: CommandMaps.Global, slot: 0));
        Assert.True(condition: router.IsMapActive(map: Menu, slot: 0));
        Assert.False(condition: router.IsMapActive(map: Foot, slot: 0));
        Assert.True(condition: router.IsMapActive(map: Foot, slot: 1));
    }
    [Fact]
    public void AModalityMayRetainGameplayWhileAddingAMenuOverlay() {
        var registry = Registry(
            activations: [],
            new Spec(Map: Foot, Name: "foot.jump"),
            new Spec(Map: Menu, Name: "menu.accept")
        );
        var router = Router(registry: registry, bindings: new EmptyBindings());

        router.SetActiveMaps(maps: [Foot, Menu], slot: 0);

        Assert.True(condition: router.IsMapActive(map: Foot, slot: 0));
        Assert.True(condition: router.IsMapActive(map: Menu, slot: 0));

        router.SetActiveMaps(maps: [Menu], slot: 0);

        Assert.False(condition: router.IsMapActive(map: Foot, slot: 0));
        Assert.True(condition: router.IsMapActive(map: Menu, slot: 0));
    }
    [Fact]
    public void RemovingAMapCancelsOnlyThatSlotsAffectedHolds() {
        var phases = new List<(int Slot, CommandPhase Phase)>();
        var registry = new CommandRegistry(modules: [new HoldModule(phases: phases)]);
        var firstDevice = InputDeviceId.FromConnectionKey(key: "first-pad");
        var secondDevice = InputDeviceId.FromConnectionKey(key: "second-pad");
        var router = new InputRouter(
            registry: registry,
            bindings: new ChannelBindings(command: "tank.drive"),
            principalResolver: new SeatPrincipals(),
            slotResolver: device => ((device == firstDevice) ? 0 : 1)
        );

        router.SetActiveMaps(maps: [Tank], slot: 0);
        router.SetActiveMaps(maps: [Tank], slot: 1);
        router.Capture(signal: InputSignal.Press(source: "axis.drive", deviceId: firstDevice));
        router.Capture(signal: InputSignal.Press(source: "axis.drive", deviceId: secondDevice));
        var started = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);

        registry.ApplySnapshot(snapshot: in started);
        phases.Clear();

        router.SetActiveMaps(maps: [Plan], slot: 0);
        var transitioned = router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue);

        registry.ApplySnapshot(snapshot: in transitioned);

        Assert.Equal(actual: phases, expected: [(0, CommandPhase.Canceled), (1, CommandPhase.Active)]);
        Assert.False(condition: router.IsCommandHeld(command: "tank.drive", slot: 0));
        Assert.True(condition: router.IsCommandHeld(command: "tank.drive", slot: 1));
    }
    [Fact]
    public void HeldDigitalChannelRecoversAfterItsMapBecomesActiveAgain() {
        var phases = new List<(int Slot, CommandPhase Phase)>();
        var registry = new CommandRegistry(modules: [new HoldModule(phases: phases)]);
        var bindings = new PagedInputBindings(profile: BindingProfile.Compile(
            document: new BindingProfileDocument(
                Version: BindingProfileDocument.CurrentVersion,
                Modifiers: [],
                Chords: [new BindingChordDefinition(
                    Group: "play",
                    Chord: [],
                    Page: new BindingPageDefinition(Id: "rest", Entries: [new BindingPageEntryDefinition(
                        Sources: ["key.drive"],
                        Channel: new ChannelRef.Name(Value: "movement")
                    )])
                )]
            ),
            channelCommandName: static _ => "tank.drive"
        ));
        var router = Router(bindings: bindings, registry: registry);

        router.SetActiveMaps(maps: [Tank], slot: 0);
        router.Capture(signal: InputSignal.Press(source: "key.drive"));
        var started = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);

        registry.ApplySnapshot(snapshot: in started);

        router.SetActiveMaps(maps: [Plan], slot: 0);
        var closed = router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue);

        registry.ApplySnapshot(snapshot: in closed);

        router.SetActiveMaps(maps: [Tank], slot: 0);
        router.Capture(signal: InputSignal.Reassert(source: "key.drive"));
        var reopened = router.SnapshotForTick(tick: 3UL, windowEndTick: ulong.MaxValue);

        registry.ApplySnapshot(snapshot: in reopened);

        Assert.Equal(
            actual: phases,
            expected: [
                (0, CommandPhase.Started),
                (0, CommandPhase.Canceled),
                (0, CommandPhase.Active),
            ]
        );
        Assert.True(condition: router.IsCommandHeld(command: "tank.drive", slot: 0));
    }
    [Fact]
    public void RemovingAMapCancelsATappedChannelBeforeItsDeferredRelease() {
        var phases = new List<(int Slot, CommandPhase Phase)>();
        var registry = new CommandRegistry(modules: [new HoldModule(phases: phases)]);
        var router = Router(
            registry: registry,
            bindings: TappedTankChannelBindings()
        );

        router.SetActiveMaps(maps: [Tank], slot: 0);
        router.Capture(signal: InputSignal.Press(source: "button.tap"));
        var started = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);

        registry.ApplySnapshot(snapshot: in started);

        Assert.False(condition: router.IsCommandHeld(command: "tank.drive", slot: 0));

        router.SetActiveMaps(maps: [Plan], slot: 0);
        var transitioned = router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue);

        registry.ApplySnapshot(snapshot: in transitioned);

        Assert.Equal(
            actual: phases,
            expected: [(0, CommandPhase.Started), (0, CommandPhase.Canceled)]
        );
        Assert.Empty(collection: router.SnapshotForTick(tick: 3UL, windowEndTick: ulong.MaxValue).Lanes);
    }
    [Fact]
    public void AnAlreadyBuiltSnapshotRetainsItsResolvedModality() {
        var activations = new List<(string Name, int Slot)>();
        var registry = Registry(
            activations,
            new Spec(Map: Tank, Name: "tank.fire"),
            new Spec(Map: Plan, Name: "plan.place")
        );
        var router = Router(
            registry: registry,
            bindings: new SameSourceBindings("tank.fire", "plan.place")
        );

        router.SetActiveMaps(maps: [Tank], slot: 0);
        router.Capture(signal: InputSignal.Press(source: "button.action"));
        var snapshot = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);

        router.SetActiveMaps(maps: [Plan], slot: 0);
        registry.ApplySnapshot(snapshot: in snapshot);

        Assert.Equal(actual: activations, expected: [("tank.fire", 0)]);
    }
    [Fact]
    public void CompiledPresentationActivationUsesTheOriginatingSlotsModality() {
        var activations = new List<(string Name, int Slot)>();
        var registry = Registry(activations, new Spec(Map: Menu, Name: "menu.accept"));
        var router = Router(registry: registry, bindings: new EmptyBindings());
        var activation = MenuActivation();

        Assert.True(condition: router.Activate(activation: activation, slot: 0));
        Assert.Empty(collection: router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue).Lanes);

        router.SetActiveMaps(maps: [Menu], slot: 0);
        Assert.True(condition: router.Activate(activation: activation, slot: 0));
        var snapshot = router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue);

        registry.ApplySnapshot(snapshot: in snapshot);

        Assert.Equal(actual: activations, expected: [("menu.accept", 0)]);
    }
    [Fact]
    public void CompiledCommandChordUsesTheSlotsModality() {
        var activations = new List<(string Name, int Slot)>();
        var registry = Registry(activations, new Spec(Map: Tank, Name: "tank.chord"));
        var bindings = ChordBindings();
        var router = Router(bindings: bindings, registry: registry);

        router.Capture(signal: InputSignal.Press(source: "button.modifier"));
        Assert.Empty(collection: router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue).Lanes);
        router.Capture(signal: InputSignal.Release(source: "button.modifier"));
        _ = router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue);

        router.SetActiveMaps(maps: [Tank], slot: 0);
        router.Capture(signal: InputSignal.Press(source: "button.modifier"));
        var snapshot = router.SnapshotForTick(tick: 3UL, windowEndTick: ulong.MaxValue);

        registry.ApplySnapshot(snapshot: in snapshot);

        Assert.Equal(actual: activations, expected: [("tank.chord", 0)]);
    }
    [Fact]
    public void SubmittedTextRemainsOutsidePlayerModality() {
        var activations = new List<(string Name, int Slot)>();
        var registry = new CommandRegistry(modules: [new SimulationTextModule(activations: activations)]);
        var router = Router(registry: registry, bindings: new EmptyBindings());

        registry.RouteSimulationTo(sink: router.ConsoleTextSink);
        Assert.Equal(expected: CommandResult.None, actual: registry.Submit(line: "plan.text"));
        var snapshot = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);

        registry.ApplySnapshot(snapshot: in snapshot);

        Assert.Equal(actual: activations, expected: [("plan.text", 0)]);
    }
    [Fact]
    public void UnknownMapIsRefusedWithoutChangingTheSlot() {
        var registry = Registry(activations: [], new Spec(Map: Tank, Name: "tank.fire"));
        var router = Router(registry: registry, bindings: new EmptyBindings());

        router.SetActiveMaps(maps: [Tank], slot: 0);

        _ = Assert.Throws<ArgumentException>(testCode: () => router.SetActiveMaps(maps: new[] { "typo" }, slot: 0));
        Assert.True(condition: router.IsMapActive(map: Tank, slot: 0));
    }

    private static CommandRegistry Registry(List<(string Name, int Slot)> activations, params Spec[] specs) {
        return new CommandRegistry(modules: [new ProbeModule(activations: activations, specs: specs)]);
    }
    private static InputRouter Router(CommandRegistry registry, IInputBindings bindings) {
        return new InputRouter(
            registry: registry,
            bindings: bindings,
            principalResolver: new SeatPrincipals()
        );
    }
    private static BindingActivation MenuActivation() {
        var profile = BindingProfile.Compile(document: new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [],
            Chords: [new BindingChordDefinition(
                Group: "menu",
                Chord: [],
                Page: new BindingPageDefinition(Id: "hold", Entries: [])
            )],
            Wheels: [new BindingWheelDefinition(
                Id: "menu",
                Group: "menu",
                HoldPages: ["hold"],
                Rings: [new BindingPageDefinition(Id: "actions", Entries: [
                    new BindingPageEntryDefinition(Sources: null, Command: "menu.accept"),
                    new BindingPageEntryDefinition(Sources: null, Command: "menu.accept"),
                ])]
            )]
        ));

        return new PagedInputBindings(profile: profile).WheelFor(slot: 0)!.Rings[0].Sectors[0].Activation;
    }
    private static PagedInputBindings ChordBindings() {
        return new PagedInputBindings(profile: BindingProfile.Compile(document: new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [new BindingModifierDefinition(Id: "modifier", Sources: ["button.modifier"])],
            Chords: [
                new BindingChordDefinition(
                    Group: "play",
                    Chord: [],
                    Page: new BindingPageDefinition(Id: "rest", Entries: [])
                ),
                new BindingChordDefinition(
                    Group: "play",
                    Chord: ["modifier"],
                    Command: new BindingCommandDefinition(Command: "tank.chord")
                ),
            ]
        )));
    }
    private static PagedInputBindings TappedTankChannelBindings() {
        return new PagedInputBindings(profile: BindingProfile.Compile(
            document: new BindingProfileDocument(
                Version: BindingProfileDocument.CurrentVersion,
                Modifiers: [],
                Chords: [new BindingChordDefinition(
                    Group: "play",
                    Chord: [],
                    Page: new BindingPageDefinition(Id: "rest", Entries: [new BindingPageEntryDefinition(
                        Sources: null,
                        Channel: new ChannelRef.Name(Value: "movement"),
                        Activator: new BindingActivatorDefinition(
                            Sequence: ["button.tap"],
                            Mode: BindingActivatorMode.Tapped
                        )
                    )])
                )]
            ),
            channelCommandName: static _ => "tank.drive"
        ));
    }

    private readonly record struct Spec(string Name, string Map);
    private sealed class ProbeModule(Spec[] specs, List<(string Name, int Slot)> activations) : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            foreach (var spec in specs) {
                yield return CommandDefinition.Verb(
                    name: spec.Name,
                    description: "Modality probe.",
                    valueKind: CommandValueKind.Digital,
                    handler: context => {
                        activations.Add(item: (spec.Name, context.Slot));

                        return CommandResult.None;
                    },
                    bindability: CommandBindability.Bindable,
                    map: spec.Map
                );
            }
        }
    }
    private sealed class HoldModule(List<(int Slot, CommandPhase Phase)> phases) : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.Verb(
                name: "tank.drive",
                description: "Held tank input.",
                valueKind: CommandValueKind.Axis1D,
                handler: context => {
                    phases.Add(item: (context.Slot, context.Phase));

                    return CommandResult.None;
                },
                bindability: CommandBindability.Bindable,
                map: Tank
            );
            yield return CommandDefinition.Verb(
                name: "plan.noop",
                description: "Plan-map declaration.",
                valueKind: CommandValueKind.Digital,
                handler: static _ => CommandResult.None,
                bindability: CommandBindability.Bindable,
                map: Plan
            );
        }
    }
    private sealed class SimulationTextModule(List<(string Name, int Slot)> activations) : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.WithWireArgs(
                name: "plan.text",
                description: "Mapped simulation text probe.",
                handler: (context, _) => {
                    activations.Add(item: ("plan.text", context.Slot));

                    return CommandResult.None;
                },
                bindability: CommandBindability.Unbindable,
                map: Plan,
                routing: CommandRouting.Simulation
            );
        }
    }
    private sealed class SameSourceBindings(params string[] commands) : IInputBindings {
        private readonly CommandBinding[] m_bindings = [.. commands.Select(selector: static command => new CommandBinding(Command: command))];

        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => ((source == "button.action") ? m_bindings : null);
    }
    private sealed class ChannelBindings(string command) : IInputBindings {
        private readonly CommandBinding[] m_bindings = [new CommandBinding(Command: command, ChannelScale: 1f)];

        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => ((source == "axis.drive") ? m_bindings : null);
    }
    private sealed class EmptyBindings : IInputBindings {
        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => null;
    }
    private sealed class SeatPrincipals : ICommandPrincipalResolver {
        public CommandPrincipal PrincipalOf(int slot) => CommandPrincipal.Seat(slot: slot);
    }
}
