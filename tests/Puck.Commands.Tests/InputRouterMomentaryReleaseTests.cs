using Xunit;

namespace Puck.Commands.Tests;

/// <summary>Pins the one-release-per-command rule where a live chord hold and a <see cref="BindingActivatorMode.Tapped"/>
/// activator's pending momentary release name the SAME destination: a modality transition owes that destination exactly
/// one cancellation, and the tap's press never becomes the hold's carried payload.</summary>
public sealed class InputRouterMomentaryReleaseTests {
    private const string ChannelCommand = "test.channel";
    private const string HudCommand = "test.hud";

    [Fact]
    public void AHoldAndAPendingTapOnOneCommandAreCancelledExactlyOnceWhenTheMapCloses() {
        var router = Router();

        router.SetActiveMaps(maps: ["play"], slot: 0);
        PressThrough(
            router: router,
            source: "mouse.button1",
            tick: 1UL
        );

        Assert.True(condition: router.IsCommandHeld(command: ChannelCommand, slot: 0));

        PressThrough(
            router: router,
            source: "key.a",
            tick: 2UL
        );

        // The map closes while ONE destination carries both a live chord hold and the tap's one-tick obligation. Two
        // obligations on one command are still one command owing one release.
        router.SetActiveMaps(maps: [], slot: 0);

        var entry = Assert.Single(collection: Assert.Single(collection: router.SnapshotForTick(tick: 3UL, windowEndTick: ulong.MaxValue).Lanes).Entries);

        Assert.Equal(actual: entry.Phase, expected: CommandPhase.Canceled);
        Assert.True(condition: entry.Dispatch);
        Assert.False(condition: router.IsCommandHeld(command: ChannelCommand, slot: 0));
        Assert.Empty(collection: router.SnapshotForTick(tick: 4UL, windowEndTick: ulong.MaxValue).Lanes);
    }
    [Fact]
    public void APendingTapIsDischargedWhileTheHoldKeepsReassertingItsOwnEntry() {
        var router = Router();

        router.SetActiveMaps(maps: ["play"], slot: 0);
        PressThrough(
            router: router,
            source: "mouse.button1",
            tick: 1UL
        );
        PressThrough(
            router: router,
            source: "key.a",
            tick: 2UL
        );

        // The destination's own map survives this transition, so only the tap's obligation is discharged — the chord
        // hold stays carried and must re-assert with ITS payload, not the tap's press.
        router.SetActiveMaps(maps: ["play", "hud"], slot: 0);

        var entries = Assert.Single(collection: router.SnapshotForTick(tick: 3UL, windowEndTick: ulong.MaxValue).Lanes).Entries.ToArray();

        Assert.Equal(actual: entries.Length, expected: 2);
        // The held seeding runs before the synthesized-edge drain, so the re-assertion is first.
        Assert.Equal(actual: entries[0].Phase, expected: CommandPhase.Active);
        Assert.False(condition: entries[0].Dispatch);
        Assert.Equal(actual: entries[1].Phase, expected: CommandPhase.Canceled);
        Assert.True(condition: router.IsCommandHeld(command: ChannelCommand, slot: 0));
    }
    [Fact]
    public void ATapsScheduledReleaseLeavesALiveChordHoldOnTheSameCommandIntact() {
        var router = Router();

        router.SetActiveMaps(maps: ["play"], slot: 0);
        PressThrough(
            router: router,
            source: "mouse.button1",
            tick: 1UL
        );
        PressThrough(
            router: router,
            source: "key.a",
            tick: 2UL
        );

        // The tap's release, scheduled one tick after its press, arrives now. It discharges the TAP's obligation and
        // nothing else: the chord hold on the same destination is owed to a physical control that is still down.
        var scheduled = Assert.Single(collection: router.SnapshotForTick(tick: 3UL, windowEndTick: ulong.MaxValue).Lanes).Entries.ToArray();

        Assert.Equal(actual: scheduled.Length, expected: 2);
        Assert.Equal(actual: scheduled[1].Phase, expected: CommandPhase.Completed);
        Assert.True(condition: router.IsCommandHeld(command: ChannelCommand, slot: 0));

        // And it keeps re-asserting, with its own payload, for as long as the chord is held.
        var carried = Assert.Single(collection: Assert.Single(collection: router.SnapshotForTick(tick: 4UL, windowEndTick: ulong.MaxValue).Lanes).Entries);

        Assert.Equal(actual: carried.Phase, expected: CommandPhase.Active);
        Assert.False(condition: carried.Dispatch);

        // The chord's OWN release is the one that drops it, and it dispatches exactly once.
        router.Capture(signal: InputSignal.Release(source: "mouse.button1"));

        var released = Assert.Single(
            collection: Assert.Single(collection: router.SnapshotForTick(tick: 5UL, windowEndTick: ulong.MaxValue).Lanes).Entries,
            predicate: static entry => (entry.Phase == CommandPhase.Completed)
        );

        Assert.True(condition: released.Dispatch);
        Assert.False(condition: router.IsCommandHeld(command: ChannelCommand, slot: 0));
        Assert.Empty(collection: router.SnapshotForTick(tick: 6UL, windowEndTick: ulong.MaxValue).Lanes);
    }
    [Fact]
    public void ATapFollowedByAFullSlotClearDeliversExactlyOneRelease() {
        var router = Router();

        router.SetActiveMaps(maps: ["play"], slot: 0);
        router.Capture(signal: InputSignal.Press(source: "key.a"));

        var pressed = Assert.Single(collection: Assert.Single(collection: router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue).Lanes).Entries);

        Assert.Equal(actual: pressed.Phase, expected: CommandPhase.Started);

        // ClearSlotHeld deliberately leaves IInputBindings alone, so the tap's scheduled release is still in flight:
        // cancelling it here as well would hand the handler two releases for one tap.
        _ = router.ClearSlotHeld(slot: 0);

        var released = Assert.Single(collection: Assert.Single(collection: router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue).Lanes).Entries);

        Assert.True(condition: released.Dispatch);
        Assert.True(condition: (released.Phase is CommandPhase.Completed or CommandPhase.Canceled));
        Assert.Empty(collection: router.SnapshotForTick(tick: 3UL, windowEndTick: ulong.MaxValue).Lanes);
    }

    private static PagedInputBindings Bindings() {
        return new PagedInputBindings(profile: BindingProfile.Compile(
            channelCommandName: static _ => ChannelCommand,
            document: new BindingProfileDocument(
                Version: BindingProfileDocument.CurrentVersion,
                Modifiers: [new BindingModifierDefinition(Id: "lmb", Sources: ["mouse.button1"])],
                Chords: [
                    new BindingChordDefinition(
                        Group: "play",
                        Chord: [],
                        Page: new BindingPageDefinition(Id: "base", Entries: [new BindingPageEntryDefinition(
                            Sources: null,
                            Channel: new ChannelRef.Name(Value: "movement"),
                            Activator: new BindingActivatorDefinition(
                                Sequence: ["key.a"],
                                Mode: BindingActivatorMode.Tapped
                            )
                        )])
                    ),
                    new BindingChordDefinition(
                        Group: "play",
                        Chord: ["lmb"],
                        Command: new BindingCommandDefinition(Channel: new ChannelRef.Name(Value: "movement"))
                    ),
                ]
            )
        ));
    }
    private static void PressThrough(InputRouter router, string source, ulong tick) {
        router.Capture(signal: InputSignal.Press(source: source));
        _ = router.SnapshotForTick(tick: tick, windowEndTick: ulong.MaxValue);
    }
    private static InputRouter Router() => new(
        registry: new CommandRegistry(modules: [new ProbeModule()]),
        bindings: Bindings(),
        principalResolver: new ConsolePrincipal()
    );

    private sealed class ConsolePrincipal : ICommandPrincipalResolver {
        public CommandPrincipal PrincipalOf(int slot) => CommandPrincipal.Console;
    }
    private sealed class ProbeModule : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.Verb(
                name: ChannelCommand,
                description: "Channel destination shared by a chord row and a tapped activator.",
                valueKind: CommandValueKind.Axis1D,
                handler: static _ => CommandResult.None,
                bindability: CommandBindability.Bindable,
                map: "play"
            );
            yield return CommandDefinition.Verb(
                name: HudCommand,
                description: "Registers a second map so a transition can keep the first one active.",
                valueKind: CommandValueKind.Digital,
                handler: static _ => CommandResult.None,
                bindability: CommandBindability.Bindable,
                map: "hud"
            );
        }
    }
}
