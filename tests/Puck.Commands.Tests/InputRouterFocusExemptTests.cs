using Xunit;

namespace Puck.Commands.Tests;

/// <summary>Pins what a signal captured while its device's terminal focus is released may and may not do: a release
/// still reaches the resolver so the page, the chord tracker and the armed rows are not stranded, but nothing it
/// synthesizes may PRESS an ordinary gameplay command, and an idle continuous sample is not a release at all.</summary>
public sealed class InputRouterFocusExemptTests {
    private const string LongChordCommand = "test.chord.long";
    private const string ShortChordCommand = "test.chord.short";

    [Fact]
    public void AFocusExemptReleaseNeverStartsAShorterRowsCommand() {
        var bindings = ChordBindings();
        var registry = new CommandRegistry(modules: [new ProbeModule()]);
        var router = Router(
            bindings: bindings,
            registry: registry
        );

        router.Capture(signal: InputSignal.Press(source: "key.left"));
        router.Capture(signal: InputSignal.Press(source: "key.right"));
        _ = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);

        Assert.True(condition: router.IsCommandHeld(command: LongChordCommand, slot: 0));

        // The seat console has focus. Releasing key.left breaks [left, right] AND leaves [right] exactly satisfied,
        // so the resolver synthesizes the shorter row's PRESS — which no gameplay command may take while the
        // device's focus is released.
        router.CaptureFocusExempt(signal: InputSignal.Release(source: "key.left"));

        var entries = Assert.Single(collection: router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue).Lanes).Entries.ToArray();

        Assert.True(condition: registry.TryGetId(id: out var longChordId, name: LongChordCommand));
        Assert.True(condition: registry.TryGetId(id: out var shortChordId, name: ShortChordCommand));
        Assert.DoesNotContain(collection: entries, filter: entry => (entry.CommandId == shortChordId));

        var released = Assert.Single(
            collection: entries,
            predicate: static entry => (entry.Phase == CommandPhase.Completed)
        );

        Assert.Equal(actual: released.CommandId, expected: longChordId);
        Assert.True(condition: released.Dispatch);
        Assert.False(condition: router.IsCommandHeld(command: ShortChordCommand, slot: 0));
        Assert.False(condition: router.IsCommandHeld(command: LongChordCommand, slot: 0));

        // Nor may it linger: a latched press would re-assert on every later tick for as long as the console is open.
        Assert.Empty(collection: router.SnapshotForTick(tick: 3UL, windowEndTick: ulong.MaxValue).Lanes);
    }
    [Fact]
    public void AFocusExemptReleaseLeavesTheShorterRowUnarmed() {
        var bindings = ChordBindings();
        var registry = new CommandRegistry(modules: [new ProbeModule()]);
        var router = Router(
            bindings: bindings,
            registry: registry
        );

        router.Capture(signal: InputSignal.Press(source: "key.left"));
        router.Capture(signal: InputSignal.Press(source: "key.right"));
        _ = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);

        // Releasing key.left under focus exemption leaves [right] exactly satisfied. The shorter row's press is
        // withheld, so the row must not be ARMED either: an armed row owes a completion, and a completion for a
        // command that never started is a release the handler never asked for.
        router.CaptureFocusExempt(signal: InputSignal.Release(source: "key.left"));
        _ = router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue);

        // The console closes and the player lets go of key.right. Nothing is owed for the shorter row.
        router.Capture(signal: InputSignal.Release(source: "key.right"));

        Assert.Empty(collection: router.SnapshotForTick(tick: 3UL, windowEndTick: ulong.MaxValue).Lanes);
    }
    [Fact]
    public void AnIdleAnalogSampleUnderFocusExemptionNeverReachesTheResolver() {
        var bindings = new RecordingBindings();
        var router = Router(bindings: bindings);

        // A pad streams a centred stick every frame. It is not a press and it is not a release — it is the device
        // reporting, and the focus-exempt route must not consult the authored page for it.
        for (var index = 0; (index < 4); index++) {
            router.CaptureFocusExempt(signal: new InputSignal(
                Source: "pad.stick",
                DeviceId: default,
                Value: CommandValue.Axis(value: 0f),
                Phase: CommandPhase.Active
            ));
        }

        _ = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);

        Assert.Empty(collection: bindings.Resolved);

        // A genuine release of a control the router has seen active still forwards, which is the whole reason the
        // focus-exempt route consults the resolver at all.
        router.Capture(signal: new InputSignal(
            Source: "pad.stick",
            DeviceId: default,
            Value: CommandValue.Axis(value: 0.8f),
            Phase: CommandPhase.Active
        ));
        _ = router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue);
        bindings.Resolved.Clear();
        router.CaptureFocusExempt(signal: new InputSignal(
            Source: "pad.stick",
            DeviceId: default,
            Value: CommandValue.Axis(value: 0f),
            Phase: CommandPhase.Active
        ));
        _ = router.SnapshotForTick(tick: 3UL, windowEndTick: ulong.MaxValue);

        Assert.Equal(actual: Assert.Single(collection: bindings.Resolved), expected: "pad.stick");
    }
    [Fact]
    public void AHeldAnalogModifierReleasedUnderFocusExemptionReturnsThePageToRest() {
        var bindings = AnalogPageBindings();
        var router = Router(bindings: bindings);

        router.Capture(signal: Trigger(value: 0.9f));
        _ = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);

        Assert.Equal(actual: bindings.ViewFor(slot: 0).PageId, expected: "aim");

        // The console opened while the trigger was down, so the trigger's return to rest arrives on the focus-exempt
        // route as an Active-phase inactive sample — the one continuous shape that IS a release.
        router.CaptureFocusExempt(signal: Trigger(value: 0f));
        _ = router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue);

        Assert.Equal(actual: bindings.ViewFor(slot: 0).PageId, expected: "base");
    }
    [Fact]
    public void AHeldAnalogModifierSurvivingADeviceReleaseStillReturnsThePageToRest() {
        var bindings = AnalogPageBindings();
        var router = Router(bindings: bindings);

        router.Capture(signal: Trigger(value: 0.9f));
        _ = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);

        Assert.Equal(actual: bindings.ViewFor(slot: 0).PageId, expected: "aim");

        // A per-device release (a reseat) withdraws the ROUTER's holds for that device and deliberately leaves the
        // resolver's chord state alone — the trigger is still physically down and still flipping the page. Whether
        // its eventual return to rest is forwarded is therefore the RESOLVER's question, not a router-side memory of
        // having seen the control deflected.
        router.ReleaseHeld(device: default);
        router.CaptureFocusExempt(signal: Trigger(value: 0f));
        _ = router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue);

        Assert.Equal(actual: bindings.ViewFor(slot: 0).PageId, expected: "base");
    }

    private static PagedInputBindings AnalogPageBindings() {
        return new PagedInputBindings(profile: BindingProfile.Compile(document: new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [new BindingModifierDefinition(Id: "aim", Sources: ["pad.leftTrigger"])],
            Chords: [
                new BindingChordDefinition(
                    Group: "play",
                    Chord: [],
                    Page: new BindingPageDefinition(Id: "base", Entries: [])
                ),
                new BindingChordDefinition(
                    Group: "play",
                    Chord: ["aim"],
                    Page: new BindingPageDefinition(Id: "aim", Entries: [])
                ),
            ]
        )));
    }
    private static PagedInputBindings ChordBindings() {
        return new PagedInputBindings(profile: BindingProfile.Compile(document: new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [
                new BindingModifierDefinition(Id: "left", Sources: ["key.left"]),
                new BindingModifierDefinition(Id: "right", Sources: ["key.right"]),
            ],
            Chords: [
                new BindingChordDefinition(
                    Group: "play",
                    Chord: [],
                    Page: new BindingPageDefinition(Id: "base", Entries: [])
                ),
                new BindingChordDefinition(
                    Group: "play",
                    Chord: ["left", "right"],
                    Command: new BindingCommandDefinition(
                        Command: LongChordCommand,
                        HoldRelease: true
                    )
                ),
                new BindingChordDefinition(
                    Group: "play",
                    Chord: ["right"],
                    Command: new BindingCommandDefinition(Command: ShortChordCommand)
                ),
            ]
        )));
    }
    private static InputRouter Router(IInputBindings bindings, CommandRegistry? registry = null) => new(
        registry: (registry ?? new CommandRegistry(modules: [new ProbeModule()])),
        bindings: bindings,
        principalResolver: new ConsolePrincipal()
    );
    private static InputSignal Trigger(float value) => new(
        Source: "pad.leftTrigger",
        DeviceId: default,
        Value: CommandValue.Axis(value: value),
        Phase: CommandPhase.Active
    );

    private sealed class ConsolePrincipal : ICommandPrincipalResolver {
        public CommandPrincipal PrincipalOf(int slot) => CommandPrincipal.Console;
    }
    // A resolver with just enough state to be asked the question the router asks: which sources it is holding down.
    // A press marks one; its release gives it up. That is the whole of what PagedInputBindings' latches, tracker and
    // activator gates amount to from the router's side.
    private sealed class RecordingBindings : IInputBindings {
        private readonly HashSet<string> m_held = new(comparer: StringComparer.OrdinalIgnoreCase);

        public List<string> Resolved { get; } = [];

        public bool HoldsSource(int slot, string source) => m_held.Contains(item: source);
        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) {
            Resolved.Add(item: source);

            return null;
        }
        public IReadOnlyList<CommandBinding>? Resolve(int slot, in InputSignal signal, bool pressesWithheld) {
            if (
                signal.Value.IsActive &&
                (signal.Phase is not (CommandPhase.Completed or CommandPhase.Canceled))
            ) {
                _ = m_held.Add(item: signal.Source);
            } else {
                _ = m_held.Remove(item: signal.Source);
            }

            return Resolve(
                slot: slot,
                source: signal.Source
            );
        }
    }
    private sealed class ProbeModule : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.Verb(
                name: LongChordCommand,
                description: "The deeper chord row's ordinary gameplay command.",
                valueKind: CommandValueKind.Digital,
                handler: static _ => CommandResult.None,
                bindability: CommandBindability.Bindable
            );
            yield return CommandDefinition.Verb(
                name: ShortChordCommand,
                description: "The shorter row a member release can leave exactly satisfied.",
                valueKind: CommandValueKind.Digital,
                handler: static _ => CommandResult.None,
                bindability: CommandBindability.Bindable
            );
        }
    }
}
