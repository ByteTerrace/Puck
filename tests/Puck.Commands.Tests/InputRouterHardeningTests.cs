using Xunit;

namespace Puck.Commands.Tests;

/// <summary>Pins the router behaviors an audit found unproven: the one-tick obligations a modality transition used to
/// destroy, the resolver state a focus-exempt release used to bypass, the ordering-only (never clock-derived) delay of
/// every router-synthesized edge, and the entry-point refusals and bounds that keep a misbehaving producer from
/// growing the capture queue without limit.</summary>
public sealed class InputRouterHardeningTests {
    private const string ActionCommand = "test.action";
    private const string AlphaCommand = "test.alpha";
    private const string BetaCommand = "test.beta";
    private const string ChannelCommand = "test.channel";
    private const string DigitalCommand = "test.digital";
    private const string MappedCommand = "test.mapped";

    [Fact]
    public void AMapTransitionThatKeepsTheMapActiveStillCompletesATappedActivator() {
        // A Tapped activator on a CHANNEL destination dispatches its release, so its completion press leaves the
        // router carrying a one-tick obligation and the resolver carrying the scheduled edge that discharges it.
        var bindings = new PagedInputBindings(profile: Compile(rows: [new BindingChordDefinition(
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
        )]));
        var router = Router(bindings: bindings);

        router.Capture(signal: InputSignal.Press(source: "key.a"));
        var pulse = Assert.Single(collection: Assert.Single(collection: router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue).Lanes).Entries);

        Assert.Equal(expected: CommandPhase.Started, actual: pulse.Phase);
        Assert.True(condition: pulse.Dispatch);

        // The transition KEEPS the channel command's map active (it is Global), so the modality-scoped cancellation
        // pass below it has nothing to say — yet Reset deletes the scheduled release the handler is owed.
        router.SetActiveMaps(maps: ["play"], slot: 0);

        var completion = Assert.Single(collection: Assert.Single(collection: router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue).Lanes).Entries);

        Assert.Equal(expected: CommandPhase.Canceled, actual: completion.Phase);
        Assert.True(condition: completion.Dispatch);
        Assert.False(condition: router.IsCommandHeld(command: ChannelCommand, slot: 0));
        Assert.Empty(collection: router.SnapshotForTick(tick: 3UL, windowEndTick: ulong.MaxValue).Lanes);
    }
    [Fact]
    public void AFocusExemptReleaseReturnsAFlippedPageToRest() {
        var bindings = new PagedInputBindings(profile: Compile(
            modifiers: [new BindingModifierDefinition(Id: "lmb", Sources: ["mouse.button1"])],
            rows: [
                new BindingChordDefinition(
                    Group: "play",
                    Chord: [],
                    Page: Page(id: "base", source: "key.x")
                ),
                new BindingChordDefinition(
                    Group: "play",
                    Chord: ["lmb"],
                    Page: Page(id: "shift", source: "key.y")
                ),
            ]
        ));
        var router = Router(bindings: bindings);

        router.Capture(signal: InputSignal.Press(source: "mouse.button1"));
        _ = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);

        Assert.Equal(expected: "shift", actual: bindings.ViewFor(slot: 0).PageId);

        // The seat console opened while the modifier was down, so the release arrives on the focus-exempt route. It
        // must still reach the tracker: otherwise the page stays flipped for as long as the console stays open.
        router.CaptureFocusExempt(signal: InputSignal.Release(source: "mouse.button1"));
        _ = router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue);

        Assert.Equal(expected: "base", actual: bindings.ViewFor(slot: 0).PageId);

        // The page is genuinely at rest rather than merely reporting so: a fresh press on the resting page's own
        // source resolves through it.
        router.Capture(signal: InputSignal.Press(source: "key.x"));
        var pressed = Assert.Single(collection: Assert.Single(collection: router.SnapshotForTick(tick: 3UL, windowEndTick: ulong.MaxValue).Lanes).Entries);

        Assert.Equal(expected: CommandPhase.Started, actual: pressed.Phase);
        Assert.Equal(expected: "key.x", actual: pressed.Source);
    }
    [Fact]
    public void AFocusExemptReleaseDeliversAnArmedChordRowsCompletion() {
        var bindings = new PagedInputBindings(profile: Compile(
            modifiers: [new BindingModifierDefinition(Id: "lmb", Sources: ["mouse.button1"])],
            rows: [
                new BindingChordDefinition(
                    Group: "play",
                    Chord: [],
                    Page: Page(id: "base", source: "key.x")
                ),
                new BindingChordDefinition(
                    Group: "play",
                    Chord: ["lmb"],
                    Command: new BindingCommandDefinition(
                        Command: ActionCommand,
                        HoldRelease: true
                    )
                ),
            ]
        ));
        var router = Router(bindings: bindings);

        router.Capture(signal: InputSignal.Press(source: "mouse.button1"));
        _ = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);

        Assert.True(condition: router.IsCommandHeld(command: ActionCommand, slot: 0));

        router.CaptureFocusExempt(signal: InputSignal.Release(source: "mouse.button1"));
        var released = Assert.Single(
            Assert.Single(collection: router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue).Lanes).Entries,
            predicate: static entry => (entry.Phase == CommandPhase.Completed)
        );

        Assert.True(condition: released.Dispatch);
        Assert.False(condition: router.IsCommandHeld(command: ActionCommand, slot: 0));
    }
    [Fact]
    public void AFocusExemptPressStillNeverReachesTheAuthoredPage() {
        var bindings = new PagedInputBindings(profile: Compile(rows: [new BindingChordDefinition(
            Group: "play",
            Chord: [],
            Page: Page(id: "base", source: "key.x")
        )]));
        var router = Router(bindings: bindings);

        router.CaptureFocusExempt(signal: InputSignal.Press(source: "key.x"));

        Assert.Empty(collection: router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue).Lanes);
        Assert.False(condition: router.IsCommandHeld(command: ActionCommand, slot: 0));
    }
    [Fact]
    public void SynthesizedEdgesLandExactlyOneTickAfterTheirCauseDuringACatchUp() {
        var registry = new CommandRegistry(modules: [new ProbeModule()]);
        var clock = new FakeClock { NowTicks = 10UL, };
        var router = new InputRouter(
            registry: registry,
            bindings: new SourceBindings(bindings: new Dictionary<string, CommandBinding[]>(comparer: StringComparer.Ordinal) {
                ["key.w"] = [new CommandBinding(Command: DigitalCommand)],
                ["pad.trigger"] = [new CommandBinding(Command: ChannelCommand, ChannelScale: 1f)],
            }),
            principalResolver: new ConsolePrincipal(),
            slotResolver: new FakeSlotResolver(raiseDisconnect: out var raiseDisconnect),
            clock: clock
        );
        var device = InputDeviceId.FromConnectionKey(key: "pad-1");

        router.Capture(signal: InputSignal.Press(captureTick: 10UL, deviceId: device, source: "key.w"));
        router.Capture(signal: new InputSignal(
            Source: "pad.trigger",
            DeviceId: device,
            Value: CommandValue.Axis(value: 0.5f),
            Phase: CommandPhase.Active,
            CaptureTick: 10UL,
            Transient: true
        ));

        // The pump has fallen behind: the capture clock is thousands of ticks past every window this catch-up will
        // close. A synthesized edge stamped from that clock would be deferred past all of these steps.
        clock.NowTicks = 10_000UL;

        var first = Assert.Single(collection: router.SnapshotForTick(tick: 1UL, windowEndTick: 11UL).Lanes).Entries;

        Assert.Contains(collection: first.ToArray(), filter: static entry => ((entry.Phase == CommandPhase.Started) && entry.Dispatch));

        // The disconnect's cancellation is synthesized DURING step 1's window, so it is owed to step 2 — exactly as
        // the transient impulse's inactive twin is.
        raiseDisconnect(device);

        var second = Assert.Single(collection: router.SnapshotForTick(tick: 2UL, windowEndTick: 12UL).Lanes).Entries.ToArray();

        Assert.Contains(collection: second, filter: static entry => ((entry.Phase == CommandPhase.Completed) && (entry.Value.AsAxis1D == 0f)));
        Assert.Contains(collection: second, filter: static entry => (entry.Phase == CommandPhase.Canceled));
        Assert.Empty(collection: router.SnapshotForTick(tick: 3UL, windowEndTick: 13UL).Lanes);
    }
    [Fact]
    public void StrandedReleasesEmitInCommandIdOrder() {
        var registry = new CommandRegistry(modules: [new ProbeModule()]);
        // Deliberately reversed: the two holds enter the slot's held table in descending command-id order, so an
        // emission that simply walked that dictionary would answer descending too.
        var bindings = new SwitchableBindings {
            Current = [
                new CommandBinding(Command: BetaCommand),
                new CommandBinding(Command: AlphaCommand),
            ],
        };
        var router = new InputRouter(
            registry: registry,
            bindings: bindings,
            principalResolver: new ConsolePrincipal()
        );

        Assert.True(condition: registry.TryGetId(id: out var alphaId, name: AlphaCommand));
        Assert.True(condition: registry.TryGetId(id: out var betaId, name: BetaCommand));
        Assert.True(condition: (alphaId < betaId));

        router.Capture(signal: InputSignal.Press(source: "key.x"));
        _ = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);

        // The page stops binding the source while it is down: both holds are stranded and must be released.
        bindings.Current = null;
        router.Capture(signal: InputSignal.Release(source: "key.x"));

        var stranded = Assert.Single(collection: router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue).Lanes)
            .Entries
            .ToArray()
            .Where(predicate: static entry => (entry.Phase == CommandPhase.Completed))
            .ToArray();

        Assert.Equal(actual: stranded.Length, expected: 2);
        Assert.Equal(actual: stranded[0].CommandId, expected: alphaId);
        Assert.Equal(actual: stranded[1].CommandId, expected: betaId);
    }
    [Fact]
    public void BoundSignalFoldAllocatesNothingAfterBuffersAreWarm() {
        var registry = new CommandRegistry(modules: [new ProbeModule()]);
        // REAL bindings on the captured source, and a PAIR of them on one command — the shape whose per-signal
        // memos used to be a fresh dictionary each. An unbound source (or no capture at all) never reaches it.
        var router = new InputRouter(
            registry: registry,
            bindings: new SourceBindings(bindings: new Dictionary<string, CommandBinding[]>(comparer: StringComparer.Ordinal) {
                ["pad.trigger"] = [
                    new CommandBinding(Command: ChannelCommand, ChannelScale: 1f),
                    new CommandBinding(Command: ChannelCommand, ChannelScale: 1f, ActivateOn: CommandPhase.Completed),
                ],
            }),
            principalResolver: new ConsolePrincipal()
        );

        for (var tick = 0UL; (tick < 1_024UL); tick++) {
            CaptureAxis(router: router, tick: tick);
            _ = router.SnapshotForTick(tick: tick, windowEndTick: ulong.MaxValue);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        var observedEntries = 0;

        for (var tick = 1_024UL; (tick < 2_048UL); tick++) {
            CaptureAxis(router: router, tick: tick);
            observedEntries += router.SnapshotForTick(tick: tick, windowEndTick: ulong.MaxValue).Lanes[0].Entries.Count;
        }

        var allocated = (GC.GetAllocatedBytesForCurrentThread() - before);

        Assert.True(condition: (observedEntries > 0));
        Assert.Equal(actual: allocated, expected: 0L);
    }
    [Fact]
    public void ActivateRefusesANegativeSlot() {
        var router = Router(bindings: new EmptyBindings());

        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => router.Activate(
            activation: Activation(),
            slot: -5
        ));
    }
    [Fact]
    public void CaptureRefusesASignalThatNamesNoSource() {
        var router = Router(bindings: new EmptyBindings());

        _ = Assert.Throws<ArgumentException>(testCode: () => router.Capture(signal: default));
        _ = Assert.Throws<ArgumentException>(testCode: () => router.CaptureFocusExempt(signal: default));
        _ = Assert.Throws<ArgumentException>(testCode: () => router.Capture(signal: InputSignal.Press(source: "")));
    }
    [Fact]
    public void TheCaptureQueueDropsItsOldestBeyondTheCap() {
        const int overflow = 5;

        var bindings = new RecordingBindings();
        var router = Router(bindings: bindings);

        for (var index = 0; (index < (InputRouter.MaxCapturedSignals + overflow)); index++) {
            router.Capture(signal: InputSignal.Press(source: $"key.{index}"));
        }

        Assert.Equal(actual: router.DroppedCaptureCount, expected: ((long)overflow));

        _ = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);

        Assert.Equal(actual: bindings.Resolved.Count, expected: InputRouter.MaxCapturedSignals);
        Assert.Equal(actual: bindings.Resolved[0], expected: $"key.{overflow}");
    }
    [Fact]
    public void TypedCharactersAreNeverLatchedAsRepeatedPresses() {
        var bindings = new RecordingBindings();
        var router = Router(bindings: bindings);

        foreach (var text in new[] { "p", "u", "c", "k", }) {
            router.Capture(signal: InputSignal.Typed(source: "keyboard.text", text: text));
        }

        _ = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);

        // A typed character is a Started signal with no release, so latching it would seat its source forever and
        // swallow every character after the first as an OS repeat.
        Assert.Equal(actual: bindings.Resolved.Count, expected: 4);
    }
    [Fact]
    public void DisposeDetachesTheRouterFromItsCollaborators() {
        var bindings = new ReloadableBindings();
        var slots = new FakeSlotResolver(raiseDisconnect: out _);
        var router = new InputRouter(
            registry: new CommandRegistry(modules: [new ProbeModule()]),
            bindings: bindings,
            principalResolver: new ConsolePrincipal(),
            slotResolver: slots
        );

        Assert.True(condition: bindings.HasSubscribers);
        Assert.True(condition: slots.HasSubscribers);

        router.Dispose();

        Assert.False(condition: bindings.HasSubscribers);
        Assert.False(condition: slots.HasSubscribers);

        router.Dispose();

        Assert.False(condition: bindings.HasSubscribers);
    }
    [Fact]
    public void HeldSeedingEmitsOneSlotsHoldsInCommandIdOrder() {
        var registry = new CommandRegistry(modules: [new ProbeModule()]);
        var router = new InputRouter(
            registry: registry,
            bindings: new SwitchableBindings {
                Current = [
                    new CommandBinding(Command: BetaCommand),
                    new CommandBinding(Command: AlphaCommand),
                ],
            },
            principalResolver: new ConsolePrincipal()
        );

        Assert.True(condition: registry.TryGetId(id: out var alphaId, name: AlphaCommand));
        Assert.True(condition: registry.TryGetId(id: out var betaId, name: BetaCommand));

        router.Capture(signal: InputSignal.Press(source: "key.x"));
        _ = router.SnapshotForTick(tick: 1UL, windowEndTick: ulong.MaxValue);

        var reasserted = Assert.Single(collection: router.SnapshotForTick(tick: 2UL, windowEndTick: ulong.MaxValue).Lanes).Entries.ToArray();

        Assert.Equal(actual: reasserted.Length, expected: 2);
        Assert.Equal(actual: reasserted[0].CommandId, expected: alphaId);
        Assert.Equal(actual: reasserted[1].CommandId, expected: betaId);
    }

    // A compiler-minted activation, the only shape Activate accepts — its constructor is deliberately not public.
    private static BindingActivation Activation() {
        var profile = BindingProfile.Compile(document: new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [],
            Chords: [new BindingChordDefinition(
                Group: "play",
                Chord: [],
                Page: new BindingPageDefinition(Id: "hold", Entries: [])
            )],
            Wheels: [new BindingWheelDefinition(
                Id: "menu",
                Group: "play",
                HoldPages: ["hold"],
                Rings: [new BindingPageDefinition(Id: "actions", Entries: [
                    new BindingPageEntryDefinition(Sources: null, Command: ActionCommand),
                    new BindingPageEntryDefinition(Sources: null, Command: ActionCommand),
                ])]
            )]
        ));

        return new PagedInputBindings(profile: profile).WheelFor(slot: 0)!.Rings[0].Sectors[0].Activation;
    }
    private static void CaptureAxis(InputRouter router, ulong tick) => router.Capture(signal: new InputSignal(
        Source: "pad.trigger",
        DeviceId: default,
        Value: CommandValue.Axis(value: 0.5f),
        Phase: CommandPhase.Active,
        CaptureTick: tick
    ));
    private static CompiledBindingProfile Compile(IReadOnlyList<BindingChordDefinition> rows, IReadOnlyList<BindingModifierDefinition>? modifiers = null) {
        return BindingProfile.Compile(
            document: new BindingProfileDocument(
                Version: BindingProfileDocument.CurrentVersion,
                Modifiers: (modifiers ?? []),
                Chords: rows
            ),
            channelCommandName: static _ => ChannelCommand
        );
    }
    private static BindingPageDefinition Page(string id, string source) => new(
        Id: id,
        Entries: [new BindingPageEntryDefinition(
            Sources: [source],
            Command: ActionCommand
        )]
    );
    private static InputRouter Router(IInputBindings bindings) => new(
        registry: new CommandRegistry(modules: [new ProbeModule()]),
        bindings: bindings,
        principalResolver: new ConsolePrincipal()
    );

    private sealed class ConsolePrincipal : ICommandPrincipalResolver {
        public CommandPrincipal PrincipalOf(int slot) => CommandPrincipal.Console;
    }
    private sealed class EmptyBindings : IInputBindings {
        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => null;
    }
    private sealed class FakeClock : IInputClock {
        public ulong NowTicks { get; set; }
    }
    private sealed class FakeSlotResolver : IInputSlotResolver {
        public FakeSlotResolver(out Action<InputDeviceId> raiseDisconnect) {
            raiseDisconnect = device => DeviceSlotChanging?.Invoke(obj: device);
        }

        public event Action<InputDeviceId>? DeviceSlotChanging;

        public bool HasSubscribers => (DeviceSlotChanging is not null);

        public int ResolveSlot(InputDeviceId device) => 0;
        public bool CommitSlot(InputDeviceId device, int slot) => true;
    }
    private sealed class RecordingBindings : IInputBindings {
        public List<string> Resolved { get; } = [];

        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) {
            Resolved.Add(item: source);

            return null;
        }
    }
    private sealed class ReloadableBindings : IInputBindings, IInputBindingsReloadSource {
        public event Action<int?>? Reloading;

        public bool HasSubscribers => (Reloading is not null);

        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => null;
    }
    private sealed class SourceBindings(Dictionary<string, CommandBinding[]> bindings) : IInputBindings {
        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => (bindings.TryGetValue(
            key: source,
            value: out var resolved
        )
            ? resolved
            : null
        );
    }
    private sealed class SwitchableBindings : IInputBindings {
        public IReadOnlyList<CommandBinding>? Current { get; set; }

        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => Current;
    }
    private sealed class ProbeModule : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.Verb(
                name: ActionCommand,
                description: "Chord/page probe.",
                valueKind: CommandValueKind.Digital,
                handler: static _ => CommandResult.None,
                bindability: CommandBindability.Bindable
            );
            yield return CommandDefinition.Verb(
                name: AlphaCommand,
                description: "Ordering probe (lower id).",
                valueKind: CommandValueKind.Digital,
                handler: static _ => CommandResult.None,
                bindability: CommandBindability.Bindable
            );
            yield return CommandDefinition.Verb(
                name: BetaCommand,
                description: "Ordering probe (higher id).",
                valueKind: CommandValueKind.Digital,
                handler: static _ => CommandResult.None,
                bindability: CommandBindability.Bindable
            );
            yield return CommandDefinition.Verb(
                name: ChannelCommand,
                description: "Channel probe.",
                valueKind: CommandValueKind.Axis1D,
                handler: static _ => CommandResult.None,
                bindability: CommandBindability.Bindable
            );
            yield return CommandDefinition.Verb(
                name: DigitalCommand,
                description: "Digital probe.",
                valueKind: CommandValueKind.Digital,
                handler: static _ => CommandResult.None,
                bindability: CommandBindability.Bindable
            );
            yield return CommandDefinition.Verb(
                name: MappedCommand,
                description: "Mapped probe.",
                valueKind: CommandValueKind.Digital,
                handler: static _ => CommandResult.None,
                bindability: CommandBindability.Bindable,
                map: "play"
            );
        }
    }
}
