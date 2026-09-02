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
