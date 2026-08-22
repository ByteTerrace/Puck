using Xunit;

namespace Puck.Commands.Tests;

public sealed class PagedInputBindingsTests {
    private const string ActionCommand = "test.action";
    private const string ChannelCommand = "test.channel";

    [Fact]
    public void ToggleChannelFlipsOnPressAndIgnoresPhysicalRelease() {
        var bindings = Bindings(entries: [new BindingPageEntryDefinition(
            Sources: ["key.toggle"],
            Channel: new ChannelRef.Name(Value: "movement"),
            Mode: BindingEntryMode.Toggle
        )]);
        var router = Router(
            bindings: bindings,
            definitions: [(ChannelCommand, CommandValueKind.Axis1D)]
        );

        router.Capture(signal: InputSignal.Press(source: "key.toggle"));
        var started = Assert.Single(collection: Assert.Single(collection: router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue).Lanes).Entries);

        Assert.Equal(expected: CommandPhase.Started, actual: started.Phase);
        Assert.Equal(expected: 1f, actual: started.Value.AsAxis1D);
        Assert.True(condition: router.IsCommandHeld(command: ChannelCommand, slot: 0));

        router.Capture(signal: InputSignal.Release(source: "key.toggle"));
        var carried = Assert.Single(collection: Assert.Single(collection: router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue).Lanes).Entries);

        Assert.Equal(expected: CommandPhase.Active, actual: carried.Phase);
        Assert.True(condition: router.IsCommandHeld(command: ChannelCommand, slot: 0));

        router.Capture(signal: InputSignal.Press(source: "key.toggle"));
        var stopped = Assert.Single(
            Assert.Single(collection: router.SnapshotForTick(tick: 3UL, windowEndTick: ulong.MaxValue).Lanes).Entries,
            predicate: static entry => (entry.Phase == CommandPhase.Completed)
        );

        Assert.Equal(expected: 0f, actual: stopped.Value.AsAxis1D);
        Assert.False(condition: router.IsCommandHeld(command: ChannelCommand, slot: 0));
        Assert.Empty(collection: router.SnapshotForTick(tick: 4UL, windowEndTick: ulong.MaxValue).Lanes);
    }
    [Fact]
    public void HeldActivatorOpensInOrderAndClosesWhenAMemberReleases() {
        var bindings = Bindings(entries: [new BindingPageEntryDefinition(
            Sources: null,
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
        var opened = Assert.Single(collection: Assert.Single(collection: router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue).Lanes).Entries);

        Assert.Equal(expected: CommandPhase.Started, actual: opened.Phase);
        Assert.True(condition: router.IsCommandHeld(command: ActionCommand, slot: 0));

        router.Capture(signal: InputSignal.Release(source: "key.a"));
        var closed = Assert.Single(
            Assert.Single(collection: router.SnapshotForTick(tick: 3UL, windowEndTick: ulong.MaxValue).Lanes).Entries,
            predicate: static entry => (entry.Phase == CommandPhase.Completed)
        );

        Assert.False(condition: closed.Dispatch);
        Assert.False(condition: router.IsCommandHeld(command: ActionCommand, slot: 0));
    }
    [Fact]
    public void AuthoredTextReachesAnActivatorPressButNotItsCarriedStateOrRelease() {
        var bindings = Bindings(entries: [new BindingPageEntryDefinition(
            Sources: null,
            Command: ActionCommand,
            Text: "first second",
            Activator: new BindingActivatorDefinition(Sequence: ["key.a", "key.b"])
        )]);
        var router = Router(
            bindings: bindings,
            definitions: [(ActionCommand, CommandValueKind.Digital)]
        );

        router.Capture(signal: InputSignal.Press(source: "key.a"));
        _ = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);
        router.Capture(signal: InputSignal.Press(source: "key.b"));
        var opened = Assert.Single(collection: Assert.Single(collection: router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue).Lanes).Entries);

        Assert.True(condition: opened.Dispatch);
        Assert.Equal(expected: $"{ActionCommand} first second", actual: opened.Text);

        var carried = Assert.Single(collection: Assert.Single(collection: router.SnapshotForTick(tick: 3UL, windowEndTick: ulong.MaxValue).Lanes).Entries);

        Assert.False(condition: carried.Dispatch);
        Assert.Null(@object: carried.Text);

        router.Capture(signal: InputSignal.Release(source: "key.a"));
        var closed = Assert.Single(
            Assert.Single(collection: router.SnapshotForTick(tick: 4UL, windowEndTick: ulong.MaxValue).Lanes).Entries,
            predicate: static entry => (entry.Phase == CommandPhase.Completed)
        );

        Assert.Null(@object: closed.Text);
    }
    [Fact]
    public void TappedActivatorFiresNowAndReleasesOnTheNextTick() {
        var bindings = Bindings(entries: [new BindingPageEntryDefinition(
            Sources: null,
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
        var pulse = Assert.Single(collection: Assert.Single(collection: router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue).Lanes).Entries);

        Assert.Equal(expected: CommandPhase.Started, actual: pulse.Phase);
        Assert.Equal(expected: CommandOrigin.Binding, actual: pulse.Origin);
        // A sequence activator has no ONE physical source to name — it is two presses — so the edge carries the
        // synthesized per-destination identity every binding-origin edge carries (BindingSourceIdentity). What the
        // tap actually depends on is that its release addresses the SAME identity: a different one on the release
        // edge would leave the destination's held contribution latched with nothing left to clear it.
        Assert.NotNull(@object: pulse.Source);
        Assert.True(condition: pulse.Dispatch);
        Assert.False(condition: router.IsCommandHeld(command: ActionCommand, slot: 0));

        var release = Assert.Single(collection: Assert.Single(collection: router.SnapshotForTick(tick: 3UL, windowEndTick: ulong.MaxValue).Lanes).Entries);

        Assert.Equal(expected: CommandPhase.Completed, actual: release.Phase);
        Assert.Equal(expected: pulse.Source, actual: release.Source);
        Assert.False(condition: release.Dispatch);
        Assert.Empty(collection: router.SnapshotForTick(tick: 4UL, windowEndTick: ulong.MaxValue).Lanes);
    }
    [Fact]
    public void ScheduledEdgeDrainReusesItsRetainedBuffer() {
        var bindings = Bindings(entries: [new BindingPageEntryDefinition(
            Sources: null,
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

        Assert.Same(actual: second, expected: first);
        Assert.Single(collection: second);
    }
    [Fact]
    public void ResetAllDropsADeferredTappedRelease() {
        var bindings = Bindings(entries: [new BindingPageEntryDefinition(
            Sources: null,
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
    [Fact]
    public void ModifierTrackerReportsOnlyHeldOrderChanges() {
        var profile = BindingProfile.Compile(document: new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [new BindingModifierDefinition(Id: "left", Sources: ["key.left"])],
            Chords: [new BindingChordDefinition(
                Group: "play",
                Chord: [],
                Page: new BindingPageDefinition(Id: "base", Entries: [])
            )]
        ));
        var tracker = new BindingChordTracker(profile: profile);
        var press = InputSignal.Press(source: "key.left");
        var release = InputSignal.Release(source: "key.left");

        Assert.True(condition: tracker.Apply(signal: in press));
        Assert.False(condition: tracker.Apply(signal: in press));
        Assert.True(condition: tracker.Apply(signal: in release));
        Assert.False(condition: tracker.Apply(signal: in release));
    }
    [Fact]
    public void MultiSourceModifierStaysHeldWhileOneOfItsSourcesRemainsDown() {
        var profile = BindingProfile.Compile(document: new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [new BindingModifierDefinition(Id: "wheel", Sources: ["gamepad.leftShoulder", "keyboard.tab"])],
            Chords: [new BindingChordDefinition(
                Group: "play",
                Chord: [],
                Page: new BindingPageDefinition(Id: "base", Entries: [])
            )]
        ));
        var tracker = new BindingChordTracker(profile: profile);

        // The FIRST source to press joins the held order.
        Assert.True(condition: tracker.Apply(signal: InputSignal.Press(source: "gamepad.leftShoulder")));
        Assert.Equal(expected: [0], actual: tracker.HeldOrder.ToArray());

        // A second source pressing while the modifier is already held is a no-op (still one held modifier).
        Assert.False(condition: tracker.Apply(signal: InputSignal.Press(source: "keyboard.tab")));
        Assert.Equal(expected: [0], actual: tracker.HeldOrder.ToArray());

        // Releasing the FIRST source while the SECOND is still down does not release the modifier.
        Assert.False(condition: tracker.Apply(signal: InputSignal.Release(source: "gamepad.leftShoulder")));
        Assert.Equal(expected: [0], actual: tracker.HeldOrder.ToArray());

        // Releasing the LAST down source releases the modifier.
        Assert.True(condition: tracker.Apply(signal: InputSignal.Release(source: "keyboard.tab")));
        Assert.Equal(expected: 0, actual: tracker.HeldOrder.Length);
    }
    [Fact]
    public void ReloadCancelsRouterHoldsAndDropsDeferredActivatorEdges() {
        var bindings = Bindings(entries: [
            new BindingPageEntryDefinition(Sources: ["key.hold"], Command: ActionCommand),
            new BindingPageEntryDefinition(
                Sources: null,
                Command: ActionCommand,
                Activator: new BindingActivatorDefinition(
                    Sequence: ["key.tap"],
                    Mode: BindingActivatorMode.Tapped
                )
            ),
        ]);
        var router = Router(
            bindings: bindings,
            definitions: [(ActionCommand, CommandValueKind.Digital)]
        );

        router.Capture(signal: InputSignal.Press(source: "key.hold"));
        _ = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);
        router.Capture(signal: InputSignal.Press(source: "key.tap"));
        _ = router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue);

        bindings.Reload(profile: Profile(entries: []));

        Assert.False(condition: router.IsCommandHeld(command: ActionCommand, slot: 0));
        var cancellation = Assert.Single(
            Assert.Single(collection: router.SnapshotForTick(tick: 3UL, windowEndTick: ulong.MaxValue).Lanes).Entries,
            predicate: static entry => (entry.Phase == CommandPhase.Canceled)
        );

        Assert.Equal(expected: "key.hold", actual: cancellation.Source);
        Assert.Empty(collection: bindings.DrainScheduledEdges());
    }
    [Fact]
    public void DigitalReassertionRecoversAChannelWithoutFiringCommandsOrActivators() {
        var bindings = Bindings(entries: [
            new BindingPageEntryDefinition(Sources: ["key.edge"], Command: ActionCommand),
            new BindingPageEntryDefinition(Sources: ["key.drive"], Channel: new ChannelRef.Name(Value: "movement")),
            new BindingPageEntryDefinition(
                Sources: null,
                Command: ActionCommand,
                Activator: new BindingActivatorDefinition(Sequence: ["key.a", "key.b"])
            ),
        ]);
        var router = Router(
            bindings: bindings,
            registry: out var registry,
            definitions: [
                (ActionCommand, CommandValueKind.Digital),
                (ChannelCommand, CommandValueKind.Axis1D),
            ]
        );

        router.Capture(signal: InputSignal.Reassert(source: "key.edge"));
        router.Capture(signal: InputSignal.Reassert(source: "key.a"));
        router.Capture(signal: InputSignal.Reassert(source: "key.b"));
        Assert.Empty(collection: router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue).Lanes);

        router.Capture(signal: InputSignal.Reassert(source: "key.drive"));
        var recovered = Assert.Single(collection: Assert.Single(collection: router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue).Lanes).Entries);

        Assert.True(condition: registry.TryGetId(id: out var channelId, name: ChannelCommand));
        Assert.Equal(expected: channelId, actual: recovered.CommandId);
        Assert.Equal(expected: CommandPhase.Active, actual: recovered.Phase);
        Assert.True(condition: recovered.Dispatch);
        Assert.Equal(expected: 1f, actual: recovered.Value.AsAxis1D);
        Assert.True(condition: router.IsCommandHeld(command: ChannelCommand, slot: 0));

        router.Capture(signal: InputSignal.Release(source: "key.drive"));
        var released = Assert.Single(
            Assert.Single(collection: router.SnapshotForTick(tick: 3UL, windowEndTick: ulong.MaxValue).Lanes).Entries,
            predicate: static entry => (entry.Phase == CommandPhase.Completed)
        );

        Assert.True(condition: released.Dispatch);
        Assert.False(condition: router.IsCommandHeld(command: ChannelCommand, slot: 0));
    }
    [Fact]
    public void TransientAxisChannelReleasesOnTheFollowingTickInsteadOfBecomingHeld() {
        var bindings = Bindings(entries: [new BindingPageEntryDefinition(
            Sources: ["mouse.motion.x"],
            Channel: new ChannelRef.Name(Value: "movement")
        )]);
        var router = Router(bindings: bindings, definitions: [(ChannelCommand, CommandValueKind.Axis1D)]);

        router.Capture(signal: InputSignal.Axis(source: "mouse.motion", value: new System.Numerics.Vector2(x: 4f, y: -2f), transient: true));
        var active = Assert.Single(collection: Assert.Single(collection: router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue).Lanes).Entries);

        Assert.Equal(expected: CommandPhase.Active, actual: active.Phase);
        Assert.Equal(expected: 4f, actual: active.Value.AsAxis1D);
        Assert.True(condition: active.Dispatch);
        Assert.False(condition: router.IsCommandHeld(command: ChannelCommand, slot: 0));

        var released = Assert.Single(collection: Assert.Single(collection: router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue).Lanes).Entries);

        Assert.Equal(expected: CommandPhase.Completed, actual: released.Phase);
        Assert.Equal(expected: 0f, actual: released.Value.AsAxis1D);
        Assert.True(condition: released.Dispatch);
        Assert.Empty(collection: router.SnapshotForTick(tick: 3UL, windowEndTick: ulong.MaxValue).Lanes);
    }
    [Fact]
    public void SuppressedTransientChannelSampleNeverBecomesCarriedState() {
        var bindings = Bindings(entries: [new BindingPageEntryDefinition(
            Sources: ["mouse.motion.x"],
            Channel: new ChannelRef.Name(Value: "movement"),
            ActivateOn: CommandPhase.Completed
        )]);
        var router = Router(bindings: bindings, definitions: [(ChannelCommand, CommandValueKind.Axis1D)]);

        router.Capture(signal: InputSignal.Axis(source: "mouse.motion", value: new System.Numerics.Vector2(x: 4f, y: -2f), transient: true));
        var suppressed = Assert.Single(collection: Assert.Single(collection: router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue).Lanes).Entries);

        Assert.False(condition: suppressed.Dispatch);
        Assert.False(condition: router.IsCommandHeld(command: ChannelCommand, slot: 0));
        Assert.Empty(collection: router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue).Lanes);
    }
    [Fact]
    public void OrderedModifierReassertionRestoresThePageButDoesNotFireItsCommandChord() {
        var profile = BindingProfile.Compile(
            document: new BindingProfileDocument(
                Version: BindingProfileDocument.CurrentVersion,
                Modifiers: [
                    new BindingModifierDefinition(Id: "first", Sources: ["key.first"]),
                    new BindingModifierDefinition(Id: "second", Sources: ["key.second"]),
                ],
                Chords: [
                    new BindingChordDefinition(
                        Group: "play",
                        Chord: [],
                        Page: new BindingPageDefinition(Id: "base", Entries: [])
                    ),
                    new BindingChordDefinition(
                        Group: "play",
                        Chord: ["first"],
                        Command: new BindingCommandDefinition(Command: ActionCommand)
                    ),
                    new BindingChordDefinition(
                        Group: "play",
                        Chord: ["first", "second"],
                        Page: new BindingPageDefinition(Id: "held-page", Entries: [
                            new BindingPageEntryDefinition(Sources: ["key.drive"], Channel: new ChannelRef.Name(Value: "movement")),
                        ])
                    ),
                ]
            ),
            channelCommandName: static _ => ChannelCommand
        );
        var bindings = new PagedInputBindings(profile: profile);
        var router = Router(
            bindings: bindings,
            registry: out var registry,
            definitions: [
                (ActionCommand, CommandValueKind.Digital),
                (ChannelCommand, CommandValueKind.Axis1D),
            ]
        );

        router.Capture(signal: InputSignal.Reassert(source: "key.first"));
        router.Capture(signal: InputSignal.Reassert(source: "key.second"));
        router.Capture(signal: InputSignal.Reassert(source: "key.drive"));
        var snapshot = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);

        Assert.Equal(expected: "held-page", actual: bindings.ViewFor(slot: 0).PageId);
        var recovered = Assert.Single(collection: Assert.Single(collection: snapshot.Lanes).Entries);

        Assert.True(condition: registry.TryGetId(id: out var channelId, name: ChannelCommand));
        Assert.True(condition: registry.TryGetId(id: out var actionId, name: ActionCommand));
        Assert.Equal(expected: channelId, actual: recovered.CommandId);
        Assert.DoesNotContain(collection: snapshot.Lanes.SelectMany(selector: static lane => lane.Entries), filter: entry => (entry.CommandId == actionId));
    }
    [Fact]
    public void ChannelCommandChordRecoversAsActiveWhileAnEdgeCommandChordDoesNot() {
        static PagedInputBindings ChordBindings(BindingCommandDefinition command) => new(profile: BindingProfile.Compile(
            document: new BindingProfileDocument(
                Version: BindingProfileDocument.CurrentVersion,
                Modifiers: [new BindingModifierDefinition(Id: "hold", Sources: ["key.hold"])],
                Chords: [
                    new BindingChordDefinition(Group: "play", Chord: [], Page: new BindingPageDefinition(Id: "base", Entries: [])),
                    new BindingChordDefinition(Group: "play", Chord: ["hold"], Command: command),
                ]
            ),
            channelCommandName: static _ => ChannelCommand
        ));

        var actionBindings = ChordBindings(command: new BindingCommandDefinition(Command: ActionCommand));
        var actionRouter = Router(bindings: actionBindings, definitions: [(ActionCommand, CommandValueKind.Digital)]);

        actionRouter.Capture(signal: InputSignal.Reassert(source: "key.hold"));
        Assert.Empty(collection: actionRouter.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue).Lanes);

        var channelBindings = ChordBindings(command: new BindingCommandDefinition(Channel: new ChannelRef.Name(Value: "movement")));
        var channelRouter = Router(bindings: channelBindings, definitions: [(ChannelCommand, CommandValueKind.Axis1D)]);

        channelRouter.Capture(signal: InputSignal.Reassert(source: "key.hold"));
        var recovered = Assert.Single(collection: Assert.Single(collection: channelRouter.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue).Lanes).Entries);

        Assert.Equal(expected: CommandPhase.Active, actual: recovered.Phase);
        Assert.True(condition: recovered.Dispatch);
        Assert.True(condition: channelRouter.IsCommandHeld(command: ChannelCommand, slot: 0));
    }
    [Fact]
    public void ReleaseAfterReloadCannotFireANewReleaseOnlyCommand() {
        var initial = Bindings(entries: [new BindingPageEntryDefinition(Sources: ["key.hold"], Command: ActionCommand)]);
        var router = Router(bindings: initial, definitions: [(ActionCommand, CommandValueKind.Digital)]);

        router.Capture(signal: InputSignal.Press(source: "key.hold"));
        _ = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);
        initial.Reload(profile: Profile(entries: [new BindingPageEntryDefinition(
            Sources: ["key.hold"],
            Command: ActionCommand,
            ActivateOn: CommandPhase.Completed
        )]));
        _ = router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue);

        router.Capture(signal: InputSignal.Reassert(source: "key.hold"));
        router.Capture(signal: InputSignal.Release(source: "key.hold"));

        Assert.Empty(collection: router.SnapshotForTick(tick: 3UL, windowEndTick: ulong.MaxValue).Lanes);
    }
    [Fact]
    public async Task ViewForRemainsProfileCoherentDuringConcurrentReloads() {
        var first = Profile(entries: [], pageId: "first");
        var second = BindingProfile.Compile(document: new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [],
            Chords: [
                new BindingChordDefinition(
                    Group: "play",
                    Chord: [],
                    Page: new BindingPageDefinition(Id: "second", Entries: [])
                ),
                new BindingChordDefinition(
                    Group: "menu",
                    Chord: [],
                    Page: new BindingPageDefinition(Id: "menu", Entries: [])
                ),
            ]
        ));
        var bindings = new PagedInputBindings(profile: second);

        Assert.True(condition: bindings.SetActiveGroup(group: "menu", slot: 0));

        var reload = Task.Run(action: () => {
            for (var index = 0; (index < 50_000); index++) {
                bindings.Reload(profile: (((index & 1) == 0) ? second : first));
            }
        }, cancellationToken: TestContext.Current.CancellationToken);
        var read = Task.Run(action: () => {
            for (var index = 0; (index < 50_000); index++) {
                var pageId = bindings.ViewFor(slot: 0).PageId;

                Assert.True(condition: (pageId is "first" or "menu"));
            }
        }, cancellationToken: TestContext.Current.CancellationToken);

        await Task.WhenAll(reload, read);
    }
    [Fact]
    public void CompiledProfileDefensivelyOwnsAuthoredCollections() {
        var modifiers = new List<BindingModifierDefinition> {
            new(Id: "shift", Sources: ["key.shift"]),
        };
        var sequence = new List<string> { "key.a", "key.b", };
        var sources = new List<string> { "key.action", };
        var entries = new List<BindingPageEntryDefinition> {
            new(Sources: sources, Command: ActionCommand),
            new(
                Sources: null,
                Command: ActionCommand,
                Activator: new BindingActivatorDefinition(Sequence: sequence)
            ),
        };
        var profile = BindingProfile.Compile(document: new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: modifiers,
            Chords: [new BindingChordDefinition(
                Group: "play",
                Chord: [],
                Page: new BindingPageDefinition(Id: "base", Entries: entries)
            )]
        ));

        modifiers.Clear();
        entries.Clear();
        sequence.Clear();
        sources.Clear();

        var bindings = new PagedInputBindings(profile: profile);
        var resolved = bindings.Resolve(slot: 0, source: "key.action");

        Assert.Single(collection: profile.Modifiers);
        Assert.Single(collection: resolved!);
        Assert.Equal(expected: 2, actual: bindings.ViewFor(slot: 0).Buttons.Count);
        Assert.Equal(expected: ["key.action"], actual: bindings.ViewFor(slot: 0).Buttons[0].Sources);

        Resolve(bindings: bindings, signal: InputSignal.Press(source: "key.a"));
        Resolve(bindings: bindings, signal: InputSignal.Press(source: "key.b"));
        Assert.Single(collection: bindings.DrainChordEdges(slot: 0).ToArray());
    }
    [Fact]
    public void CompiledWheelDefensivelyOwnsNestedThresholdCollections() {
        var thresholds = new List<float> { 0.5f, };
        var profile = BindingProfile.Compile(document: new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [],
            Chords: [new BindingChordDefinition(
                Group: "play",
                Chord: [],
                Page: new BindingPageDefinition(Id: "hold", Entries: [])
            )],
            Wheels: [new BindingWheelDefinition(
                Id: "tools",
                Group: "play",
                HoldPages: ["hold"],
                Rings: [
                    Ring(id: "inner"),
                    Ring(id: "outer"),
                ],
                Style: new BindingWheelStyleDefinition(
                    RingSelection: BindingWheelRingSelectionMode.Excursion,
                    Excursion: new BindingWheelExcursionDefinition(
                        DeadZone: 0.1f,
                        Thresholds: thresholds
                    )
                )
            )]
        ));

        thresholds[0] = 0.9f;

        var wheel = new PagedInputBindings(profile: profile).WheelFor(slot: 0)!;

        Assert.Equal(expected: 0.5f, actual: Assert.Single(collection: wheel.Style.Excursion!.Thresholds));
        Assert.Equal(expected: 0.25f, actual: Assert.Single(collection: wheel.Excursion!.ThresholdsSquared));
    }

    private static PagedInputBindings Bindings(IReadOnlyList<BindingPageEntryDefinition> entries) {
        return new PagedInputBindings(profile: Profile(entries: entries));
    }
    private static CompiledBindingProfile Profile(IReadOnlyList<BindingPageEntryDefinition> entries, string pageId = "base") {
        return BindingProfile.Compile(
            document: new BindingProfileDocument(
                Version: BindingProfileDocument.CurrentVersion,
                Modifiers: [],
                Chords: [new BindingChordDefinition(
                    Group: "play",
                    Chord: [],
                    Page: new BindingPageDefinition(Id: pageId, Entries: entries)
                )]
            ),
            channelCommandName: static _ => ChannelCommand
        );
    }
    private static BindingPageDefinition Ring(string id) {
        return new BindingPageDefinition(Id: id, Entries: [
            new BindingPageEntryDefinition(Sources: null, Command: ActionCommand),
            new BindingPageEntryDefinition(Sources: null, Command: ActionCommand),
        ]);
    }
    private static void Resolve(PagedInputBindings bindings, InputSignal signal) {
        _ = bindings.Resolve(signal: in signal, slot: 0);
    }
    private static InputRouter Router(PagedInputBindings bindings, params (string Name, CommandValueKind Kind)[] definitions) {
        return Router(bindings: bindings, registry: out _, definitions: definitions);
    }
    private static InputRouter Router(PagedInputBindings bindings, out CommandRegistry registry, params (string Name, CommandValueKind Kind)[] definitions) {
        registry = new CommandRegistry(modules: [new TestModule(definitions: definitions)]);

        return new InputRouter(
            registry: registry,
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
    [Fact]
    public void HeldCommandAuthoredAsAPressRowAndAReleaseRowDispatchesItsRelease() {
        var profile = BindingProfile.Compile(
            document: new BindingProfileDocument(
                Version: BindingProfileDocument.CurrentVersion,
                Modifiers: [new BindingModifierDefinition(Id: "lmb", Sources: ["mouse.button1"])],
                Chords: [new BindingChordDefinition(
                    Group: "play",
                    Chord: [],
                    Page: new BindingPageDefinition(Id: "base", Entries: [
                        new BindingPageEntryDefinition(Sources: ["mouse.button1"], Command: ActionCommand),
                        new BindingPageEntryDefinition(Sources: ["mouse.button1"], Command: ActionCommand, ActivateOn: CommandPhase.Completed),
                    ])
                )]
            ),
            channelCommandName: static _ => ChannelCommand
        );
        var bindings = new PagedInputBindings(profile: profile);
        var router = Router(bindings: bindings, definitions: [(ActionCommand, CommandValueKind.Digital)]);

        router.Capture(signal: InputSignal.Press(source: "mouse.button1"));
        var pressed = Assert.Single(collection: router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue).Lanes).Entries;
        Assert.Contains(collection: pressed, filter: e => e.Dispatch && (e.Phase == CommandPhase.Started));

        router.Capture(signal: InputSignal.Release(source: "mouse.button1"));
        var lanes = router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue).Lanes;
        Assert.NotEmpty(collection: lanes);
        Assert.Contains(collection: lanes[0].Entries, filter: e => e.Dispatch && (e.Phase == CommandPhase.Completed));
    }
    [Fact]
    public void MultiSourceRowPressesAndReleasesFromEitherControlIndependently() {
        var bindings = Bindings(entries: [
            new BindingPageEntryDefinition(Sources: ["gamepad.buttonSouth", "keyboard.space"], Command: ActionCommand),
            new BindingPageEntryDefinition(Sources: ["gamepad.buttonSouth", "keyboard.space"], Command: ActionCommand, ActivateOn: CommandPhase.Completed),
        ]);
        var router = Router(bindings: bindings, definitions: [(ActionCommand, CommandValueKind.Digital)]);

        router.Capture(signal: InputSignal.Press(source: "gamepad.buttonSouth"));
        var padPress = Assert.Single(collection: router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue).Lanes).Entries;
        Assert.Contains(collection: padPress, filter: e => e.Dispatch && (e.Phase == CommandPhase.Started));

        router.Capture(signal: InputSignal.Release(source: "gamepad.buttonSouth"));
        var padRelease = router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue).Lanes;
        Assert.NotEmpty(collection: padRelease);
        Assert.Contains(collection: padRelease[0].Entries, filter: e => e.Dispatch && (e.Phase == CommandPhase.Completed));

        // The keyboard source presses and releases on its own, unaffected by the gamepad source's earlier cycle.
        router.Capture(signal: InputSignal.Press(source: "keyboard.space"));
        var keyPress = Assert.Single(collection: router.SnapshotForTick(tick: 3UL, windowEndTick: ulong.MaxValue).Lanes).Entries;
        Assert.Contains(collection: keyPress, filter: e => e.Dispatch && (e.Phase == CommandPhase.Started));

        router.Capture(signal: InputSignal.Release(source: "keyboard.space"));
        var keyRelease = router.SnapshotForTick(tick: 4UL, windowEndTick: ulong.MaxValue).Lanes;
        Assert.NotEmpty(collection: keyRelease);
        Assert.Contains(collection: keyRelease[0].Entries, filter: e => e.Dispatch && (e.Phase == CommandPhase.Completed));
    }
}
