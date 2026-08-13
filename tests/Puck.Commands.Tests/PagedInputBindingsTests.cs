using Xunit;

namespace Puck.Commands.Tests;

public sealed class PagedInputBindingsTests {
    private const string ActionCommand = "test.action";
    private const string ChannelCommand = "test.channel";

    [Fact]
    public void ToggleChannelFlipsOnPressAndIgnoresPhysicalRelease() {
        var bindings = Bindings(entries: [new BindingPageEntryDefinition(
            Source: "key.toggle",
            Channel: new ChannelRef.Name(Value: "movement"),
            Mode: BindingEntryMode.Toggle
        )]);
        var router = Router(
            bindings: bindings,
            definitions: [(ChannelCommand, CommandValueKind.Axis1D)]
        );

        router.Capture(signal: InputSignal.Press(source: "key.toggle"));
        var started = Assert.Single(Assert.Single(router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue).Lanes).Entries);

        Assert.Equal(expected: CommandPhase.Started, actual: started.Phase);
        Assert.Equal(expected: 1f, actual: started.Value.AsAxis1D);
        Assert.True(condition: router.IsCommandHeld(slot: 0, command: ChannelCommand));

        router.Capture(signal: InputSignal.Release(source: "key.toggle"));
        var carried = Assert.Single(Assert.Single(router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue).Lanes).Entries);

        Assert.Equal(expected: CommandPhase.Active, actual: carried.Phase);
        Assert.True(condition: router.IsCommandHeld(slot: 0, command: ChannelCommand));

        router.Capture(signal: InputSignal.Press(source: "key.toggle"));
        var stopped = Assert.Single(
            Assert.Single(router.SnapshotForTick(tick: 3UL, windowEndTick: ulong.MaxValue).Lanes).Entries,
            predicate: static entry => entry.Phase == CommandPhase.Completed
        );

        Assert.Equal(expected: 0f, actual: stopped.Value.AsAxis1D);
        Assert.False(condition: router.IsCommandHeld(slot: 0, command: ChannelCommand));
        Assert.Empty(collection: router.SnapshotForTick(tick: 4UL, windowEndTick: ulong.MaxValue).Lanes);
    }

    [Fact]
    public void HeldActivatorOpensInOrderAndClosesWhenAMemberReleases() {
        var bindings = Bindings(entries: [new BindingPageEntryDefinition(
            Source: null,
            Command: ActionCommand,
            Activator: new BindingActivatorDefinition(Sequence: ["key.a", "key.b"])
        )]);
        var router = Router(
            bindings: bindings,
            definitions: [(ActionCommand, CommandValueKind.Digital)]
        );

        router.Capture(signal: InputSignal.Press(source: "key.a"));
        Assert.Empty(collection: router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue).Lanes);

        router.Capture(signal: InputSignal.Press(source: "key.b"));
        var opened = Assert.Single(Assert.Single(router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue).Lanes).Entries);

        Assert.Equal(expected: CommandPhase.Started, actual: opened.Phase);
        Assert.True(condition: router.IsCommandHeld(slot: 0, command: ActionCommand));

        router.Capture(signal: InputSignal.Release(source: "key.a"));
        var closed = Assert.Single(
            Assert.Single(router.SnapshotForTick(tick: 3UL, windowEndTick: ulong.MaxValue).Lanes).Entries,
            predicate: static entry => entry.Phase == CommandPhase.Completed
        );

        Assert.False(condition: closed.Dispatch);
        Assert.False(condition: router.IsCommandHeld(slot: 0, command: ActionCommand));
    }

    [Fact]
    public void TappedActivatorFiresNowAndReleasesOnTheNextTick() {
        var bindings = Bindings(entries: [new BindingPageEntryDefinition(
            Source: null,
            Command: ActionCommand,
            Activator: new BindingActivatorDefinition(
                Sequence: ["key.a", "key.b"],
                Mode: BindingActivatorMode.Tapped
            )
        )]);
        var router = Router(
            bindings: bindings,
            definitions: [(ActionCommand, CommandValueKind.Digital)]
        );

        router.Capture(signal: InputSignal.Press(source: "key.a"));
        Assert.Empty(collection: router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue).Lanes);

        router.Capture(signal: InputSignal.Press(source: "key.b"));
        var pulse = Assert.Single(Assert.Single(router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue).Lanes).Entries);

        Assert.Equal(expected: CommandPhase.Started, actual: pulse.Phase);
        Assert.True(condition: pulse.Dispatch);
        Assert.False(condition: router.IsCommandHeld(slot: 0, command: ActionCommand));

        var release = Assert.Single(Assert.Single(router.SnapshotForTick(tick: 3UL, windowEndTick: ulong.MaxValue).Lanes).Entries);

        Assert.Equal(expected: CommandPhase.Completed, actual: release.Phase);
        Assert.False(condition: release.Dispatch);
        Assert.Empty(collection: router.SnapshotForTick(tick: 4UL, windowEndTick: ulong.MaxValue).Lanes);
    }

    [Fact]
    public void ScheduledEdgeDrainReusesItsRetainedBuffer() {
        var bindings = Bindings(entries: [new BindingPageEntryDefinition(
            Source: null,
            Command: ActionCommand,
            Activator: new BindingActivatorDefinition(
                Sequence: ["key.a", "key.b"],
                Mode: BindingActivatorMode.Tapped
            )
        )]);

        Resolve(bindings: bindings, signal: InputSignal.Press(source: "key.a"));
        Resolve(bindings: bindings, signal: InputSignal.Press(source: "key.b"));
        var first = bindings.DrainScheduledEdges();

        Assert.Single(collection: first);
        Assert.Empty(collection: bindings.DrainScheduledEdges());

        Resolve(bindings: bindings, signal: InputSignal.Press(source: "key.a"));
        Resolve(bindings: bindings, signal: InputSignal.Press(source: "key.b"));
        var second = bindings.DrainScheduledEdges();

        Assert.Same(expected: first, actual: second);
        Assert.Single(collection: second);
    }

    [Fact]
    public void ResetAllDropsADeferredTappedRelease() {
        var bindings = Bindings(entries: [new BindingPageEntryDefinition(
            Source: null,
            Command: ActionCommand,
            Activator: new BindingActivatorDefinition(
                Sequence: ["key.a"],
                Mode: BindingActivatorMode.Tapped
            )
        )]);

        Resolve(bindings: bindings, signal: InputSignal.Press(source: "key.a"));
        bindings.ResetAll();

        Assert.Empty(collection: bindings.DrainScheduledEdges());
    }

    [Fact]
    public void ModifierPageAndCommandChordTransitionsStayCoherent() {
        var profile = BindingProfile.Compile(document: new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [
                new BindingModifierDefinition(Id: "left", Source: "key.left"),
                new BindingModifierDefinition(Id: "right", Source: "key.right"),
            ],
            Chords: [
                new BindingChordDefinition(
                    Group: "play",
                    Chord: [],
                    Page: new BindingPageDefinition(Id: "base", Entries: [])
                ),
                new BindingChordDefinition(
                    Group: "play",
                    Chord: ["left"],
                    Page: new BindingPageDefinition(Id: "left-page", Entries: [])
                ),
                new BindingChordDefinition(
                    Group: "play",
                    Chord: ["left", "right"],
                    Command: new BindingCommandDefinition(Command: ActionCommand, HoldRelease: true)
                ),
            ]
        ));
        var bindings = new PagedInputBindings(profile: profile);

        Resolve(bindings: bindings, signal: InputSignal.Press(source: "key.left"));
        Assert.Equal(expected: "left-page", actual: bindings.ViewFor(slot: 0).PageId);
        Assert.Empty(collection: bindings.DrainChordEdges(slot: 0).ToArray());

        Resolve(bindings: bindings, signal: InputSignal.Press(source: "key.right"));
        var press = Assert.Single(collection: bindings.DrainChordEdges(slot: 0).ToArray());

        Assert.Equal(expected: ActionCommand, actual: press.Command);
        Assert.Equal(expected: CommandPhase.Started, actual: press.Phase);
        Assert.Equal(expected: "left-page", actual: bindings.ViewFor(slot: 0).PageId);

        Resolve(bindings: bindings, signal: InputSignal.Release(source: "key.left"));
        var release = Assert.Single(collection: bindings.DrainChordEdges(slot: 0).ToArray());

        Assert.Equal(expected: ActionCommand, actual: release.Command);
        Assert.Equal(expected: CommandPhase.Completed, actual: release.Phase);
        Assert.True(condition: release.Dispatch);
        Assert.Equal(expected: "base", actual: bindings.ViewFor(slot: 0).PageId);
    }

    private static PagedInputBindings Bindings(IReadOnlyList<BindingPageEntryDefinition> entries) {
        return new PagedInputBindings(profile: BindingProfile.Compile(
            document: new BindingProfileDocument(
                Version: BindingProfileDocument.CurrentVersion,
                Modifiers: [],
                Chords: [new BindingChordDefinition(
                    Group: "play",
                    Chord: [],
                    Page: new BindingPageDefinition(Id: "base", Entries: entries)
                )]
            ),
            channelCommandName: static _ => ChannelCommand
        ));
    }

    private static void Resolve(PagedInputBindings bindings, InputSignal signal) {
        _ = bindings.Resolve(slot: 0, signal: in signal);
    }

    private static InputRouter Router(PagedInputBindings bindings, params (string Name, CommandValueKind Kind)[] definitions) {
        return new InputRouter(
            registry: new CommandRegistry(modules: [new TestModule(definitions: definitions)]),
            bindings: bindings,
            principalResolver: new ConsolePrincipal()
        );
    }

    private sealed class ConsolePrincipal : ICommandPrincipalResolver {
        public CommandPrincipal PrincipalOf(int slot) => CommandPrincipal.Console;
    }

    private sealed class TestModule((string Name, CommandValueKind Kind)[] definitions) : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            foreach (var (name, kind) in definitions) {
                yield return CommandDefinition.Verb(
                    name: name,
                    description: "Binding behavior probe.",
                    valueKind: kind,
                    handler: static _ => CommandResult.None,
                    bindability: CommandBindability.Bindable
                );
            }
        }
    }
}
