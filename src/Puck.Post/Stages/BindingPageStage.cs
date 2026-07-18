using Puck.Commands;
using Puck.Input;

namespace Puck.Post;

/// <summary>
/// Tier-A stage A5. The binding-page determinism self-check (pure CPU), the POST port of the demo's
/// <c>--validate-bindings</c> gate: it proves the paged binding resolver (<see cref="PagedInputBindings"/>) is
/// deterministic and semantically correct <em>inside</em> the <see cref="InputRouter"/>'s pre-snapshot fold — the
/// property that makes recorded <see cref="CommandSnapshot"/>s page-resolved and replay-safe with no changes to the
/// replay machinery. A scripted signal sequence exercises every page transition the profile model defines:
/// <list type="number">
/// <item><description>threshold press/release with hysteresis (an analog trigger resting between the release and
/// press thresholds keeps its latched state — no page flap);</description></item>
/// <item><description>ordered chords: <c>left</c>-then-<c>right</c> and <c>right</c>-then-<c>left</c> select
/// distinct pages, and releasing one modifier of a chord leaves the ordered remainder's page;</description></item>
/// <item><description>the release latch: a button pressed on a page completes as that page's command even when the
/// modifier lifted first, and leaves no stuck held entry behind;</description></item>
/// <item><description>page GROUPS: a runtime <see cref="PagedInputBindings.SetActiveGroup"/> flip re-scopes
/// resolution (deepest-held page-chord PREFIX within the active group, resting-page fallback) without touching
/// the tracker, and the press latch survives the flip — the release resolves to the pressed group's command;</description></item>
/// <item><description>command chords: a chord row whose meaning is a COMMAND fires its press edge on the signal
/// that completes the chord and its release edge when a member releases (HoldRelease dispatching both);</description></item>
/// <item><description>the two compile-time uniqueness rules reject loudly (one meaning per <c>(group, chord)</c>;
/// one resting page per group; no empty-chord command);</description></item>
/// <item><description>determinism: two identical sessions produce identical per-tick snapshot hashes.</description></item>
/// </list>
/// </summary>
internal sealed class BindingPageStage : IPostStage {
    private const string BaseCommand = "bindpage.base";
    private const string ChordCommand = "bindpage.chord";
    private const string LeftCommand = "bindpage.left";
    private const string LeftRightCommand = "bindpage.leftRight";
    private const string ModeCommand = "bindpage.mode";
    private const string RightCommand = "bindpage.right";
    private const string RightLeftCommand = "bindpage.rightLeft";
    private const int StuckHeldProbeTick = 24;
    private const int GroupStuckHeldProbeTick = 41;
    private const int TickCount = 44;

    /// <inheritdoc/>
    public string Name => "binding-page";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        // The compile-time uniqueness rules reject loudly before any session runs.
        if (RuleRejections() is { } rejection) {
            return PostStageOutcome.Fail(detail: rejection);
        }

        var registry = new CommandRegistry(modules: [new BindingPageStageModule()]);
        var ids = new Dictionary<string, ushort>(comparer: StringComparer.Ordinal);

        foreach (var name in (string[])[BaseCommand, LeftCommand, RightCommand, LeftRightCommand, RightLeftCommand, ModeCommand, ChordCommand,]) {
            if (!registry.TryGetId(
                name: name,
                id: out var id
            )) {
                return PostStageOutcome.Infra(detail: $"stage command \"{name}\" is not interned");
            }

            ids[name] = id;
        }

        // Two identical sessions through fresh router + paged-bindings instances must agree tick-for-tick.
        var first = RunSession(registry: registry);
        var second = RunSession(registry: registry);
        var divergence = HashTrace.FirstDivergence(left: first.Hashes, right: second.Hashes);

        if (divergence >= 0) {
            return PostStageOutcome.Fail(detail: $"non-deterministic: two identical paged-binding sessions diverged at tick {divergence}");
        }

        // The semantic assertions: each scripted press must have resolved on the page its held chord selects.
        foreach (var (tick, command, phase) in ExpectedEntries()) {
            if (!first.Contains(tick: tick, commandId: ids[command], phase: phase)) {
                return PostStageOutcome.Fail(detail: $"page resolution wrong: tick {tick} does not carry {command} ({phase})");
            }
        }

        // The release-latch guarantees: after the page-4 press released post-modifier-drop, and after the
        // cross-group-flip release, nothing is held.
        if (first.HeldAt(tick: StuckHeldProbeTick)) {
            return PostStageOutcome.Fail(detail: $"stuck held entry: tick {StuckHeldProbeTick} still carries an entry after every control released — the release latch failed");
        }

        if (first.HeldAt(tick: GroupStuckHeldProbeTick)) {
            return PostStageOutcome.Fail(detail: $"stuck held entry: tick {GroupStuckHeldProbeTick} still carries an entry after the cross-group release — the press latch leaked across the group flip");
        }

        return PostStageOutcome.Pass(detail: $"paged bindings verified over {TickCount} ticks: threshold hysteresis, ordered chords (left→right vs right→left), chord-remainder fallback, the release latch (page flips AND group flips), group-scoped deepest-prefix resolution, command-chord press/release edges, the loud uniqueness-rule rejections, and two identical sessions hashing bit-for-bit (final hash 0x{first.Hashes[^1]:X16})");
    }

    // The three loud compile-time rules, each proven by a document that must be rejected. Null = all rejected.
    private static string? RuleRejections() {
        var modifiers = (IReadOnlyList<BindingModifierDefinition>)[new BindingModifierDefinition(Id: "left", Source: InputSources.Gamepad.LeftTrigger),];
        var resting = new BindingChordDefinition(Group: "main", Chord: [], Page: new BindingPageDefinition(Entries: [], Id: "base"));
        var probes = (IReadOnlyList<(string Rule, BindingProfileDocument Document)>)[
            ("one meaning per (group, chord)", new BindingProfileDocument(
                Chords: [
                    resting,
                    new BindingChordDefinition(Group: "main", Chord: ["left"], Page: new BindingPageDefinition(Entries: [], Id: "left")),
                    new BindingChordDefinition(Group: "main", Chord: ["left"], Command: new BindingCommandDefinition(Command: ChordCommand)),
                ],
                Modifiers: modifiers,
                Version: BindingProfileDocument.CurrentVersion
            )),
            ("one resting page per group", new BindingProfileDocument(
                Chords: [
                    resting,
                    new BindingChordDefinition(Group: "orphan", Chord: ["left"], Page: new BindingPageDefinition(Entries: [], Id: "orphan-left")),
                ],
                Modifiers: modifiers,
                Version: BindingProfileDocument.CurrentVersion
            )),
            ("no empty-chord command", new BindingProfileDocument(
                Chords: [
                    resting,
                    new BindingChordDefinition(Group: "other", Chord: [], Command: new BindingCommandDefinition(Command: ChordCommand)),
                ],
                Modifiers: modifiers,
                Version: BindingProfileDocument.CurrentVersion
            )),
        ];

        foreach (var (rule, document) in probes) {
            try {
                _ = BindingProfile.Compile(document: document);

                return $"the \"{rule}\" rule did not reject its violating document";
            } catch (ArgumentException) {
                // The loud rejection this probe demands.
            }
        }

        return null;
    }

    // One session: capture the whole script up front (each signal stamped with its tick), then pull one snapshot
    // per tick — the exact shape of the host loop — folding page state deterministically in capture order. The
    // scripted group flips fire between ticks, exactly where a host's verb handler would call SetActiveGroup.
    private static Session RunSession(CommandRegistry registry) {
        var bindings = new PagedInputBindings(profile: BindingProfile.Compile(document: BuildProfile()));
        var router = new InputRouter(bindings: bindings, registry: registry);

        foreach (var signal in Script()) {
            router.Capture(signal: signal);
        }

        var session = new Session(TickCount: TickCount);

        for (var tick = 0; (tick < TickCount); tick++) {
            // The scripted mode flips: into the "mode" group before tick 32, back to the default before tick 39
            // (while the mode-page press from tick 38 is still held — the latch-across-the-flip probe).
            if (tick == 32) {
                _ = bindings.SetActiveGroup(slot: 0, group: "mode");
            } else if (tick == 39) {
                _ = bindings.SetActiveGroup(slot: 0, group: null);
            }

            var window = ((ulong)tick);

            session.Fold(tick: tick, snapshot: router.SnapshotForTick(tick: window, windowEndTick: (window + 1UL)));
        }

        return session;
    }

    // The test profile: two trigger modifiers (0.5 press / 0.4 release hysteresis) and one probe button whose
    // command names the page that resolved it — so a wrong page is visible as a wrong command in the snapshot.
    private static BindingProfileDocument BuildProfile() {
        return new BindingProfileDocument(
            Modifiers: [
                new BindingModifierDefinition(Id: "left", Source: InputSources.Gamepad.LeftTrigger),
                new BindingModifierDefinition(Id: "right", Source: InputSources.Gamepad.RightTrigger),
            ],
            Chords: [
                new BindingChordDefinition(Group: "main", Chord: [], Page: new BindingPageDefinition(Entries: [new BindingPageEntryDefinition(Command: BaseCommand, Source: InputSources.Gamepad.ButtonSouth),], Id: "base")),
                new BindingChordDefinition(Group: "main", Chord: ["left"], Page: new BindingPageDefinition(Entries: [new BindingPageEntryDefinition(Command: LeftCommand, Source: InputSources.Gamepad.ButtonSouth),], Id: "left")),
                new BindingChordDefinition(Group: "main", Chord: ["right"], Page: new BindingPageDefinition(Entries: [new BindingPageEntryDefinition(Command: RightCommand, Source: InputSources.Gamepad.ButtonSouth),], Id: "right")),
                new BindingChordDefinition(Group: "main", Chord: ["left", "right"], Page: new BindingPageDefinition(Entries: [new BindingPageEntryDefinition(Command: LeftRightCommand, Source: InputSources.Gamepad.ButtonSouth),], Id: "left-right")),
                new BindingChordDefinition(Group: "main", Chord: ["right", "left"], Page: new BindingPageDefinition(Entries: [new BindingPageEntryDefinition(Command: RightLeftCommand, Source: InputSources.Gamepad.ButtonSouth),], Id: "right-left")),
                // The "mode" group: a resting page only — no [right] page, so a held right trigger proves the
                // deepest-PREFIX fallback — plus a HoldRelease COMMAND chord on [right].
                new BindingChordDefinition(Group: "mode", Chord: [], Page: new BindingPageDefinition(Entries: [new BindingPageEntryDefinition(Command: ModeCommand, Source: InputSources.Gamepad.ButtonSouth),], Id: "mode-base")),
                new BindingChordDefinition(Group: "mode", Chord: ["right"], Command: new BindingCommandDefinition(Command: ChordCommand, HoldRelease: true)),
            ],
            Version: BindingProfileDocument.CurrentVersion
        );
    }

    // The scripted session. Comments give the page each press must resolve on; ExpectedEntries() asserts them.
    private static IEnumerable<InputSignal> Script() {
        // Base page: a plain press/release.
        yield return InputSignal.Press(source: InputSources.Gamepad.ButtonSouth, captureTick: 0UL);
        yield return InputSignal.Release(source: InputSources.Gamepad.ButtonSouth, captureTick: 1UL);
        // Left trigger crosses the press threshold -> left page.
        yield return Trigger(source: InputSources.Gamepad.LeftTrigger, value: 0.6f, captureTick: 2UL);
        yield return InputSignal.Press(source: InputSources.Gamepad.ButtonSouth, captureTick: 3UL);
        // Hysteresis: 0.45 sits between release (0.4) and press (0.5) thresholds -> the latch holds.
        yield return Trigger(source: InputSources.Gamepad.LeftTrigger, value: 0.45f, captureTick: 4UL);
        yield return InputSignal.Release(source: InputSources.Gamepad.ButtonSouth, captureTick: 5UL);
        yield return InputSignal.Press(source: InputSources.Gamepad.ButtonSouth, captureTick: 6UL);
        yield return InputSignal.Release(source: InputSources.Gamepad.ButtonSouth, captureTick: 7UL);
        // 0.3 crosses the release threshold -> back to base.
        yield return Trigger(source: InputSources.Gamepad.LeftTrigger, value: 0.3f, captureTick: 8UL);
        yield return InputSignal.Press(source: InputSources.Gamepad.ButtonSouth, captureTick: 9UL);
        yield return InputSignal.Release(source: InputSources.Gamepad.ButtonSouth, captureTick: 10UL);
        // Right THEN left -> the order-sensitive right-left page.
        yield return Trigger(source: InputSources.Gamepad.RightTrigger, value: 0.7f, captureTick: 11UL);
        yield return Trigger(source: InputSources.Gamepad.LeftTrigger, value: 0.6f, captureTick: 12UL);
        yield return InputSignal.Press(source: InputSources.Gamepad.ButtonSouth, captureTick: 13UL);
        yield return InputSignal.Release(source: InputSources.Gamepad.ButtonSouth, captureTick: 14UL);
        // Releasing right leaves the ordered remainder [left] -> the left page.
        yield return Trigger(source: InputSources.Gamepad.RightTrigger, value: 0.2f, captureTick: 15UL);
        yield return InputSignal.Press(source: InputSources.Gamepad.ButtonSouth, captureTick: 16UL);
        yield return InputSignal.Release(source: InputSources.Gamepad.ButtonSouth, captureTick: 17UL);
        yield return Trigger(source: InputSources.Gamepad.LeftTrigger, value: 0.1f, captureTick: 18UL);
        // Left THEN right -> the order-sensitive left-right page.
        yield return Trigger(source: InputSources.Gamepad.LeftTrigger, value: 0.6f, captureTick: 19UL);
        yield return Trigger(source: InputSources.Gamepad.RightTrigger, value: 0.7f, captureTick: 20UL);
        yield return InputSignal.Press(source: InputSources.Gamepad.ButtonSouth, captureTick: 21UL);
        // Both modifiers drop while the button is still held...
        yield return Trigger(source: InputSources.Gamepad.LeftTrigger, value: 0.1f, captureTick: 22UL);
        yield return Trigger(source: InputSources.Gamepad.RightTrigger, value: 0.1f, captureTick: 22UL);
        // ...so this release MUST resolve via the latch to the left-right page's command, not the base page's.
        yield return InputSignal.Release(source: InputSources.Gamepad.ButtonSouth, captureTick: 23UL);
        // Tick 24 (StuckHeldProbeTick) is silent: nothing may still be held. Then base resolves again.
        yield return InputSignal.Press(source: InputSources.Gamepad.ButtonSouth, captureTick: 25UL);
        yield return InputSignal.Release(source: InputSources.Gamepad.ButtonSouth, captureTick: 26UL);
        // The trigger-release EDGE (the capture source's Completed, value 0): a trigger snapping from fully held
        // to rest between frames never crosses the hysteresis band with an Active sample — the edge alone must
        // unlatch the modifier, or the page sticks (the live stuck-page bug).
        yield return Trigger(source: InputSources.Gamepad.LeftTrigger, value: 0.9f, captureTick: 27UL);
        yield return TriggerRelease(source: InputSources.Gamepad.LeftTrigger, captureTick: 28UL);
        yield return InputSignal.Press(source: InputSources.Gamepad.ButtonSouth, captureTick: 29UL);
        yield return InputSignal.Release(source: InputSources.Gamepad.ButtonSouth, captureTick: 30UL);
        // ---- The GROUP block (RunSession flips to "mode" before tick 32, back to "main" before tick 39). ----
        // The flipped group's resting page answers.
        yield return InputSignal.Press(source: InputSources.Gamepad.ButtonSouth, captureTick: 32UL);
        yield return InputSignal.Release(source: InputSources.Gamepad.ButtonSouth, captureTick: 33UL);
        // The right trigger completes the mode group's [right] COMMAND chord -> the press edge fires on this very
        // sample (an Active-phase trigger sweep — the synthesized edge carries its own Started phase).
        yield return Trigger(source: InputSources.Gamepad.RightTrigger, value: 0.7f, captureTick: 34UL);
        // Deepest-prefix fallback: mode has no [right] PAGE, so the resting page still answers south while the
        // trigger is held (main's "right" page must not leak across the group scope).
        yield return InputSignal.Press(source: InputSources.Gamepad.ButtonSouth, captureTick: 35UL);
        yield return InputSignal.Release(source: InputSources.Gamepad.ButtonSouth, captureTick: 36UL);
        // The member releases -> the HoldRelease command chord dispatches its release edge.
        yield return Trigger(source: InputSources.Gamepad.RightTrigger, value: 0.2f, captureTick: 37UL);
        // The latch across the group flip: pressed on mode-base, released after the flip back to main — the
        // release MUST resolve to the mode page's command, and nothing may stay held (tick 41's silent probe).
        yield return InputSignal.Press(source: InputSources.Gamepad.ButtonSouth, captureTick: 38UL);
        yield return InputSignal.Release(source: InputSources.Gamepad.ButtonSouth, captureTick: 40UL);
        // New presses use the new (default) group.
        yield return InputSignal.Press(source: InputSources.Gamepad.ButtonSouth, captureTick: 42UL);
        yield return InputSignal.Release(source: InputSources.Gamepad.ButtonSouth, captureTick: 43UL);
    }
    private static IEnumerable<(int Tick, string Command, CommandPhase Phase)> ExpectedEntries() {
        yield return (0, BaseCommand, CommandPhase.Started);
        yield return (1, BaseCommand, CommandPhase.Completed);
        yield return (3, LeftCommand, CommandPhase.Started);
        yield return (5, LeftCommand, CommandPhase.Completed);
        yield return (6, LeftCommand, CommandPhase.Started);       // hysteresis: 0.45 kept the left page latched
        yield return (9, BaseCommand, CommandPhase.Started);       // 0.3 released it
        yield return (13, RightLeftCommand, CommandPhase.Started); // right then left
        yield return (16, LeftCommand, CommandPhase.Started);      // ordered remainder after right released
        yield return (21, LeftRightCommand, CommandPhase.Started); // left then right
        yield return (23, LeftRightCommand, CommandPhase.Completed); // the release latch
        yield return (25, BaseCommand, CommandPhase.Started);
        yield return (29, BaseCommand, CommandPhase.Started);        // the release edge alone unlatched the modifier
        // The group block.
        yield return (32, ModeCommand, CommandPhase.Started);        // the flipped group's resting page answers
        yield return (33, ModeCommand, CommandPhase.Completed);
        yield return (34, ChordCommand, CommandPhase.Started);       // the [right] command chord completes
        yield return (35, ModeCommand, CommandPhase.Started);        // deepest-prefix fallback under the held chord
        yield return (36, ModeCommand, CommandPhase.Completed);
        yield return (37, ChordCommand, CommandPhase.Completed);     // the member released -> the HoldRelease edge
        yield return (38, ModeCommand, CommandPhase.Started);
        yield return (40, ModeCommand, CommandPhase.Completed);      // the press latch survived the group flip
        yield return (42, BaseCommand, CommandPhase.Started);        // new presses use the restored default group
    }
    private static InputSignal Trigger(string source, float value, ulong captureTick) {
        return new InputSignal(
            CaptureTick: captureTick,
            DeviceId: default,
            Phase: CommandPhase.Active,
            Source: source,
            Value: CommandValue.Axis(value: value)
        );
    }

    // The trigger-release edge exactly as GamepadCaptureSource emits it on the first rest report after activity.
    private static InputSignal TriggerRelease(string source, ulong captureTick) {
        return new InputSignal(
            CaptureTick: captureTick,
            DeviceId: default,
            Phase: CommandPhase.Completed,
            Source: source,
            Value: CommandValue.Axis(value: 0f)
        );
    }

    // Per-tick snapshot digests plus the raw entries the semantic assertions probe. The hash is an explicit
    // FNV-1a fold (not System.HashCode, whose seed is per-process) over every lane's ordered entries.
    private sealed class Session {
        private readonly List<(ushort CommandId, CommandPhase Phase)>[] m_entries;

        public Session(int TickCount) {
            Hashes = new ulong[TickCount];
            m_entries = new List<(ushort, CommandPhase)>[TickCount];

            for (var tick = 0; (tick < TickCount); tick++) {
                m_entries[tick] = [];
            }
        }

        public ulong[] Hashes { get; }

        public bool Contains(int tick, ushort commandId, CommandPhase phase) {
            return m_entries[tick].Contains(item: (commandId, phase));
        }
        public bool HeldAt(int tick) {
            return (m_entries[tick].Count != 0);
        }
        public void Fold(int tick, CommandSnapshot snapshot) {
            const ulong offsetBasis = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;

            var hash = offsetBasis;

            void FoldValue(ulong value) {
                for (var shift = 0; (shift < 64); shift += 8) {
                    hash = ((hash ^ ((value >> shift) & 0xFFUL)) * prime);
                }
            }

            FoldValue(value: snapshot.Tick);

            foreach (var lane in snapshot.Lanes) {
                FoldValue(value: ((ulong)((uint)lane.Slot)));

                foreach (var entry in lane.Entries) {
                    FoldValue(value: ((ulong)entry.CommandId) | (((ulong)entry.Phase) << 16));
                    FoldValue(value: (((ulong)BitConverter.SingleToUInt32Bits(value: entry.Value.Raw.X))) | (((ulong)BitConverter.SingleToUInt32Bits(value: entry.Value.Raw.Y)) << 32));
                    FoldValue(value: (((ulong)BitConverter.SingleToUInt32Bits(value: entry.Value.Raw.Z))) | (((ulong)BitConverter.SingleToUInt32Bits(value: entry.Value.Raw.W)) << 32));
                    m_entries[tick].Add(item: (entry.CommandId, entry.Phase));
                }
            }

            Hashes[tick] = hash;
        }
    }

    /// <summary>Interns the stage's page-named probe commands. The handlers are no-ops — the stage reads the per-tick snapshots, not dispatch.</summary>
    private sealed class BindingPageStageModule : ICommandModule {
        /// <inheritdoc/>
        public IEnumerable<CommandDefinition> GetCommands() {
            foreach (var name in (string[])[BaseCommand, LeftCommand, RightCommand, LeftRightCommand, RightLeftCommand, ModeCommand, ChordCommand,]) {
                yield return CommandDefinition.Verb(
                    description: $"Binding-page stage probe ({name}).",
                    handler: static _ => CommandResult.None,
                    name: name,
                    valueKind: CommandValueKind.Digital
                );
            }
        }
    }
}
