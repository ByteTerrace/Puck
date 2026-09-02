using Xunit;

namespace Puck.Commands.Tests;

/// <summary>Laws for a binding row's two member lists: <c>held</c> (a set) and <c>chord</c> (a press-ordered
/// sequence), raw sources as members, and the row-identity refusals.</summary>
public sealed class BindingRowMemberLawTests {
    private const string ActionCommand = "test.action";
    private const string ChannelCommand = "test.channel";

    private static CompiledBindingProfile Compile(IReadOnlyList<BindingChordDefinition> rows, IReadOnlyList<BindingModifierDefinition>? modifiers = null) =>
        BindingProfile.Compile(
            document: new BindingProfileDocument(
                Version: BindingProfileDocument.CurrentVersion,
                Modifiers: (modifiers ?? []),
                Chords: [new BindingChordDefinition(Group: "g", Chord: [], Page: new BindingPageDefinition(Id: "base", Entries: [])), .. rows]
            ),
            channelCommandName: static _ => ChannelCommand
        );
    private static InputRouter Router(CompiledBindingProfile profile) =>
        new(
            registry: new CommandRegistry(modules: [new Module()]),
            bindings: new PagedInputBindings(profile: profile),
            principalResolver: new Principal()
        );
    private static bool Fired(InputRouter router, ulong tick) =>
        router.SnapshotForTick(tick: tick, windowEndTick: ulong.MaxValue).Lanes.Any(predicate: lane => lane.Entries.Any(predicate: e => (e.Dispatch && (e.Phase == CommandPhase.Started))));

    [Fact]
    public void HeldRowFiresInEitherPressOrder() {
        var profile = Compile(rows: [new BindingChordDefinition(Group: "g", Held: ["mouse.button1", "mouse.button2"], Command: new BindingCommandDefinition(Command: ActionCommand))]);

        var leftFirst = Router(profile: profile);

        leftFirst.Capture(signal: InputSignal.Press(source: "mouse.button1"));
        Assert.False(condition: Fired(router: leftFirst, tick: 1UL));
        leftFirst.Capture(signal: InputSignal.Press(source: "mouse.button2"));
        Assert.True(condition: Fired(router: leftFirst, tick: 2UL));

        var rightFirst = Router(profile: profile);

        rightFirst.Capture(signal: InputSignal.Press(source: "mouse.button2"));
        rightFirst.Capture(signal: InputSignal.Press(source: "mouse.button1"));
        Assert.True(condition: Fired(router: rightFirst, tick: 1UL));
    }
    [Fact]
    public void ChordRowFiresOnlyInItsPressOrder() {
        var profile = Compile(rows: [new BindingChordDefinition(Group: "g", Chord: ["key.a", "key.b"], Command: new BindingCommandDefinition(Command: ActionCommand))]);

        var inOrder = Router(profile: profile);

        inOrder.Capture(signal: InputSignal.Press(source: "key.a"));
        inOrder.Capture(signal: InputSignal.Press(source: "key.b"));
        Assert.True(condition: Fired(router: inOrder, tick: 1UL));

        var reversed = Router(profile: profile);

        reversed.Capture(signal: InputSignal.Press(source: "key.b"));
        reversed.Capture(signal: InputSignal.Press(source: "key.a"));
        Assert.False(condition: Fired(router: reversed, tick: 1UL));
    }
    [Fact]
    public void HeldAndChordOnOneRowIgnoreTheHeldMembersPositionInTheSequence() {
        var profile = Compile(rows: [new BindingChordDefinition(Group: "g", Held: ["key.shift"], Chord: ["key.a", "key.b"], Command: new BindingCommandDefinition(Command: ActionCommand))]);

        var shiftBetween = Router(profile: profile);

        shiftBetween.Capture(signal: InputSignal.Press(source: "key.a"));
        shiftBetween.Capture(signal: InputSignal.Press(source: "key.shift"));
        shiftBetween.Capture(signal: InputSignal.Press(source: "key.b"));
        Assert.True(condition: Fired(router: shiftBetween, tick: 1UL));

        var wrongOrder = Router(profile: profile);

        wrongOrder.Capture(signal: InputSignal.Press(source: "key.shift"));
        wrongOrder.Capture(signal: InputSignal.Press(source: "key.b"));
        wrongOrder.Capture(signal: InputSignal.Press(source: "key.a"));
        Assert.False(condition: Fired(router: wrongOrder, tick: 1UL));

        var noShift = Router(profile: profile);

        noShift.Capture(signal: InputSignal.Press(source: "key.a"));
        noShift.Capture(signal: InputSignal.Press(source: "key.b"));
        Assert.False(condition: Fired(router: noShift, tick: 1UL));
    }
    [Fact]
    public void RawSourceMembersResolveToDeclaredModifiersOwningThemOrBecomeImplicitOnes() {
        var profile = Compile(
            modifiers: [new BindingModifierDefinition(Id: "wheel", Sources: ["gamepad.leftShoulder", "keyboard.tab"])],
            rows: [
                new BindingChordDefinition(Group: "g", Chord: ["keyboard.tab"], Page: new BindingPageDefinition(Id: "wheel-page", Entries: [])),
                new BindingChordDefinition(Group: "g", Held: ["mouse.button1"], Command: new BindingCommandDefinition(Command: ActionCommand)),
            ]
        );

        Assert.Equal(expected: 2, actual: profile.Modifiers.Count);
        Assert.Equal(expected: "wheel", actual: profile.Modifiers[0].Id);
        Assert.Equal(expected: "mouse.button1", actual: profile.Modifiers[1].Id);
        Assert.Equal(expected: ["mouse.button1"], actual: profile.Modifiers[1].Sources);
    }
    [Fact]
    public void RowsOverTheSameMembersAreRefusedUnlessBothAreOrderedPaths() {
        var refused = Assert.Throws<ArgumentException>(testCode: () => Compile(rows: [
            new BindingChordDefinition(Group: "g", Held: ["key.a", "key.b"], Command: new BindingCommandDefinition(Command: ActionCommand)),
            new BindingChordDefinition(Group: "g", Chord: ["key.b", "key.a"], Command: new BindingCommandDefinition(Command: ActionCommand)),
        ]));

        Assert.Contains(expectedSubstring: "not chord-only", actualString: refused.Message);

        _ = Compile(rows: [
            new BindingChordDefinition(Group: "g", Chord: ["key.a", "key.b"], Command: new BindingCommandDefinition(Command: ActionCommand)),
            new BindingChordDefinition(Group: "g", Chord: ["key.b", "key.a"], Command: new BindingCommandDefinition(Command: ActionCommand)),
        ]);
    }
    [Fact]
    public void AMemberListedInBothListsIsRefused() {
        var refused = Assert.Throws<ArgumentException>(testCode: () => Compile(rows: [
            new BindingChordDefinition(Group: "g", Held: ["key.a"], Chord: ["key.a"], Command: new BindingCommandDefinition(Command: ActionCommand)),
        ]));

        Assert.Contains(expectedSubstring: "more than once", actualString: refused.Message);
    }

    private sealed class Principal : ICommandPrincipalResolver {
        public CommandPrincipal PrincipalOf(int slot) => CommandPrincipal.Console;
    }
    private sealed class Module : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.Verb(name: ActionCommand, description: "probe", valueKind: CommandValueKind.Digital, handler: static _ => CommandResult.None, bindability: CommandBindability.Bindable);
            yield return CommandDefinition.Verb(name: ChannelCommand, description: "probe", valueKind: CommandValueKind.Axis1D, handler: static _ => CommandResult.None, bindability: CommandBindability.Bindable);
        }
    }
}
