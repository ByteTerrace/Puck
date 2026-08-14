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
            new Spec("common", CommandMaps.Global),
            new Spec("tank.fire", Tank),
            new Spec("foot.jump", Foot),
            new Spec("plan.place", Plan)
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

        router.SetActiveMaps(slot: 0, maps: [Tank]);
        router.SetActiveMaps(slot: 1, maps: [Foot]);
        router.SetActiveMaps(slot: 2, maps: [Plan]);
        router.Capture(signal: InputSignal.Press(source: "button.action", deviceId: tankDevice));
        router.Capture(signal: InputSignal.Press(source: "button.action", deviceId: footDevice));
        router.Capture(signal: InputSignal.Press(source: "button.action", deviceId: planDevice));

        var snapshot = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);

        registry.ApplySnapshot(snapshot: in snapshot);

        Assert.Equal(
            expected: [
                ("common", 0), ("tank.fire", 0),
                ("common", 1), ("foot.jump", 1),
                ("common", 2), ("plan.place", 2),
            ],
            actual: activations
        );
    }

    [Fact]
    public void ReplacingOneSlotsModalityDoesNotChangeAnotherSlot() {
        var activations = new List<(string Name, int Slot)>();
        var registry = Registry(
            activations,
            new Spec("foot.jump", Foot),
            new Spec("menu.accept", Menu)
        );
        var menuDevice = InputDeviceId.FromConnectionKey(key: "menu-pad");
        var footDevice = InputDeviceId.FromConnectionKey(key: "foot-pad");
        var router = new InputRouter(
            registry: registry,
            bindings: new SameSourceBindings("foot.jump", "menu.accept"),
            principalResolver: new SeatPrincipals(),
            slotResolver: device => ((device == menuDevice) ? 0 : 1)
        );

        router.SetActiveMaps(slot: 0, maps: [Menu]);
        router.SetActiveMaps(slot: 1, maps: [Foot]);
        router.Capture(signal: InputSignal.Press(source: "button.action", deviceId: menuDevice));
        router.Capture(signal: InputSignal.Press(source: "button.action", deviceId: footDevice));

        var snapshot = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);

        registry.ApplySnapshot(snapshot: in snapshot);

        Assert.Equal(expected: [("menu.accept", 0), ("foot.jump", 1)], actual: activations);
        Assert.True(condition: router.IsMapActive(slot: 0, map: CommandMaps.Global));
        Assert.True(condition: router.IsMapActive(slot: 0, map: Menu));
        Assert.False(condition: router.IsMapActive(slot: 0, map: Foot));
        Assert.True(condition: router.IsMapActive(slot: 1, map: Foot));
    }

    [Fact]
    public void AModalityMayRetainGameplayWhileAddingAMenuOverlay() {
        var registry = Registry(
            activations: [],
            new Spec("foot.jump", Foot),
            new Spec("menu.accept", Menu)
        );
        var router = Router(registry: registry, bindings: new EmptyBindings());

        router.SetActiveMaps(slot: 0, maps: [Foot, Menu]);

        Assert.True(condition: router.IsMapActive(slot: 0, map: Foot));
        Assert.True(condition: router.IsMapActive(slot: 0, map: Menu));

        router.SetActiveMaps(slot: 0, maps: [Menu]);

        Assert.False(condition: router.IsMapActive(slot: 0, map: Foot));
        Assert.True(condition: router.IsMapActive(slot: 0, map: Menu));
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

        router.SetActiveMaps(slot: 0, maps: [Tank]);
        router.SetActiveMaps(slot: 1, maps: [Tank]);
        router.Capture(signal: InputSignal.Press(source: "axis.drive", deviceId: firstDevice));
        router.Capture(signal: InputSignal.Press(source: "axis.drive", deviceId: secondDevice));
        var started = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);
        registry.ApplySnapshot(snapshot: in started);
        phases.Clear();

        router.SetActiveMaps(slot: 0, maps: [Plan]);
        var transitioned = router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue);
        registry.ApplySnapshot(snapshot: in transitioned);

        Assert.Equal(expected: [(0, CommandPhase.Canceled), (1, CommandPhase.Active)], actual: phases);
        Assert.False(condition: router.IsCommandHeld(slot: 0, command: "tank.drive"));
        Assert.True(condition: router.IsCommandHeld(slot: 1, command: "tank.drive"));
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
                        Source: "key.drive",
                        Channel: new ChannelRef.Name(Value: "movement")
                    )])
                )]
            ),
            channelCommandName: static _ => "tank.drive"
        ));
        var router = Router(registry: registry, bindings: bindings);

        router.SetActiveMaps(slot: 0, maps: [Tank]);
        router.Capture(signal: InputSignal.Press(source: "key.drive"));
        var started = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);
        registry.ApplySnapshot(snapshot: in started);

        router.SetActiveMaps(slot: 0, maps: [Plan]);
        var closed = router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue);
        registry.ApplySnapshot(snapshot: in closed);

        router.SetActiveMaps(slot: 0, maps: [Tank]);
        router.Capture(signal: InputSignal.Reassert(source: "key.drive"));
        var reopened = router.SnapshotForTick(tick: 3UL, windowEndTick: ulong.MaxValue);
        registry.ApplySnapshot(snapshot: in reopened);

        Assert.Equal(
            expected: [
                (0, CommandPhase.Started),
                (0, CommandPhase.Canceled),
                (0, CommandPhase.Active),
            ],
            actual: phases
        );
        Assert.True(condition: router.IsCommandHeld(slot: 0, command: "tank.drive"));
    }

    [Fact]
    public void RemovingAMapCancelsATappedChannelBeforeItsDeferredRelease() {
        var phases = new List<(int Slot, CommandPhase Phase)>();
        var registry = new CommandRegistry(modules: [new HoldModule(phases: phases)]);
        var router = Router(
            registry: registry,
            bindings: TappedTankChannelBindings()
        );

        router.SetActiveMaps(slot: 0, maps: [Tank]);
        router.Capture(signal: InputSignal.Press(source: "button.tap"));
        var started = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);
        registry.ApplySnapshot(snapshot: in started);

        Assert.False(condition: router.IsCommandHeld(slot: 0, command: "tank.drive"));

        router.SetActiveMaps(slot: 0, maps: [Plan]);
        var transitioned = router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue);
        registry.ApplySnapshot(snapshot: in transitioned);

        Assert.Equal(
            expected: [(0, CommandPhase.Started), (0, CommandPhase.Canceled)],
            actual: phases
        );
        Assert.Empty(collection: router.SnapshotForTick(tick: 3UL, windowEndTick: ulong.MaxValue).Lanes);
    }

    [Fact]
    public void AnAlreadyBuiltSnapshotRetainsItsResolvedModality() {
        var activations = new List<(string Name, int Slot)>();
        var registry = Registry(
            activations,
            new Spec("tank.fire", Tank),
            new Spec("plan.place", Plan)
        );
        var router = Router(
            registry: registry,
            bindings: new SameSourceBindings("tank.fire", "plan.place")
        );

        router.SetActiveMaps(slot: 0, maps: [Tank]);
        router.Capture(signal: InputSignal.Press(source: "button.action"));
        var snapshot = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);

        router.SetActiveMaps(slot: 0, maps: [Plan]);
        registry.ApplySnapshot(snapshot: in snapshot);

        Assert.Equal(expected: [("tank.fire", 0)], actual: activations);
    }

    [Fact]
    public void CompiledPresentationActivationUsesTheOriginatingSlotsModality() {
        var activations = new List<(string Name, int Slot)>();
        var registry = Registry(activations, new Spec("menu.accept", Menu));
        var router = Router(registry: registry, bindings: new EmptyBindings());
        var activation = MenuActivation();

        Assert.True(condition: router.Activate(slot: 0, activation: activation));
        Assert.Empty(collection: router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue).Lanes);

        router.SetActiveMaps(slot: 0, maps: [Menu]);
        Assert.True(condition: router.Activate(slot: 0, activation: activation));
        var snapshot = router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue);
        registry.ApplySnapshot(snapshot: in snapshot);

        Assert.Equal(expected: [("menu.accept", 0)], actual: activations);
    }

    [Fact]
    public void CompiledCommandChordUsesTheSlotsModality() {
        var activations = new List<(string Name, int Slot)>();
        var registry = Registry(activations, new Spec("tank.chord", Tank));
        var bindings = ChordBindings();
        var router = Router(registry: registry, bindings: bindings);

        router.Capture(signal: InputSignal.Press(source: "button.modifier"));
        Assert.Empty(collection: router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue).Lanes);
        router.Capture(signal: InputSignal.Release(source: "button.modifier"));
        _ = router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue);

        router.SetActiveMaps(slot: 0, maps: [Tank]);
        router.Capture(signal: InputSignal.Press(source: "button.modifier"));
        var snapshot = router.SnapshotForTick(tick: 3UL, windowEndTick: ulong.MaxValue);
        registry.ApplySnapshot(snapshot: in snapshot);

        Assert.Equal(expected: [("tank.chord", 0)], actual: activations);
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

        Assert.Equal(expected: [("plan.text", 0)], actual: activations);
    }

    [Fact]
    public void UnknownMapIsRefusedWithoutChangingTheSlot() {
        var registry = Registry(activations: [], new Spec("tank.fire", Tank));
        var router = Router(registry: registry, bindings: new EmptyBindings());

        router.SetActiveMaps(slot: 0, maps: [Tank]);

        _ = Assert.Throws<ArgumentException>(testCode: () => router.SetActiveMaps(slot: 0, maps: new[] { "typo" }));
        Assert.True(condition: router.IsMapActive(slot: 0, map: Tank));
    }

    private static CommandRegistry Registry(List<(string Name, int Slot)> activations, params Spec[] specs) {
        return new CommandRegistry(modules: [new ProbeModule(specs: specs, activations: activations)]);
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
                    new BindingPageEntryDefinition(Source: null, Command: "menu.accept"),
                    new BindingPageEntryDefinition(Source: null, Command: "menu.accept"),
                ])]
            )]
        ));

        return new PagedInputBindings(profile: profile).WheelFor(slot: 0)!.Rings[0].Sectors[0].Activation;
    }

    private static PagedInputBindings ChordBindings() {
        return new PagedInputBindings(profile: BindingProfile.Compile(document: new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [new BindingModifierDefinition(Id: "modifier", Source: "button.modifier")],
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
                        Source: null,
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
