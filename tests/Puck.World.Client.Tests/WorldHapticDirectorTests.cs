using System.Diagnostics;
using Puck.Commands;
using Puck.Input.Output;
using Puck.Maths;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>CONTRACT UNDER TEST: <see cref="WorldHapticDirector"/> evaluates <c>haptics</c> rows per local seat
/// against each seat's routed <see cref="WorldStateMirror"/> (including <c>$body</c> keys), drives
/// <see cref="IGamepadOutput.Rumble"/> for <c>Rise</c>, <c>Change</c>, and <c>Level</c> modes, stops active motors when
/// intensities return to zero or a pad leaves a seat, preserves edge tracking across same-world installs, re-seeds on
/// world replacement/crossing, and reports monotone status through <see cref="WorldHapticDirector.Describe"/>.</summary>
public sealed class WorldHapticDirectorTests {
    private sealed class RecordingGamepadOutput(InputDeviceId deviceId, bool accept = true) : IGamepadOutput {
        public List<RumbleEffect> Effects { get; } = [];
        public InputDeviceId DeviceId { get; } = deviceId;
        public GamepadOutputCapabilities Capabilities => GamepadOutputCapabilities.Rumble;
        public TrackpadHapticKinds TrackpadHaptics => TrackpadHapticKinds.None;

        public bool Rumble(in RumbleEffect effect) {
            Effects.Add(item: effect);

            return accept;
        }
        public bool PlayTrackpadHaptic(in TrackpadHapticEffect effect) => false;
        public bool RumbleHd(in HdRumbleEffect effect) => false;
        public bool RumbleTriggers(in TriggerRumbleEffect effect) => false;
        public bool SetHomeLedPattern(in HomeLedPattern pattern) => false;
        public bool SetLed(in LedColor color) => false;
        public bool SetTriggerEffect(in TriggerEffectSpec left, in TriggerEffectSpec right) => false;
        public bool SetTriggerEffectAt(in TriggerEffectSpec left, in TriggerEffectSpec right, ulong fireAtTick) => false;
        public bool SendEffect(ReadOnlySpan<byte> data) => false;
    }

    private sealed class AcceptingLink : IServerLink {
        public void Query(WorldQuery query, Principal principal, Action<QueryAnswer> completion) {
            if (query is WorldQuery.GrantAllows) {
                completion(obj: new QueryAnswer(
                    Payload: new GrantVerdict(Rule: GrantRule.ConcreteHold),
                    Text: string.Empty
                ));
            }
        }
        public long SubmitEnvelope(WorldSubmissionPayload payload, Principal principal) => 0L;
        public void SubmitIntent(in IntentSubmission submission) { }
        public void SubmitSession(SessionRequest request, Action<SessionReply> completion) => completion(obj: new SessionReply(
            Accepted: true,
            AssignedIndex: ((request is SessionRequest.Join join)
                ? (join.Slot + 1)
                : 1),
            Reason: string.Empty,
            RosterEcho: string.Empty
        ));
    }

    private sealed class Harness {
        private long m_now;
        private WorldDefinition m_definition;

        public Harness(WorldDefinition definition, int localSeats = 1) {
            m_definition = definition with {
                PopulationRaw = (definition.Population with { LocalSeatsRaw = localSeats }),
            };
            SeatBindings = new WorldSeatBindings(definition: m_definition);
            SeatRouter = new WorldSeatAuthorityRouter();
            Roster = new PlayerRoster(
                definition: m_definition,
                link: new AcceptingLink(),
                seatBindings: SeatBindings
            );
            View = new WorldDocumentStateView(definition: () => m_definition);
            Mirror = new WorldStateMirror(view: View);
            Mirror.Install(engineTick: 0UL, tick: 0UL);
            SeatBindings.SyncSeat(
                definition: m_definition,
                engineTick: 0UL,
                entityIndex: 0,
                nextInputTick: 1UL,
                slot: 0,
                state: Mirror
            );
            Director = new WorldHapticDirector(
                outputResolver: id => Outputs.GetValueOrDefault(key: id),
                roster: Roster,
                bodies: new WorldSeatBodies(seatBindings: SeatBindings, seatRouter: SeatRouter),
                seatBindings: SeatBindings,
                timestamp: () => m_now
            );
        }

        public WorldHapticDirector Director { get; }
        public WorldStateMirror Mirror { get; }
        public Dictionary<InputDeviceId, RecordingGamepadOutput> Outputs { get; } = [];
        public PlayerRoster Roster { get; }
        public WorldSeatBindings SeatBindings { get; }
        public WorldSeatAuthorityRouter SeatRouter { get; }
        public WorldDocumentStateView View { get; }

        public RecordingGamepadOutput AttachGamepad(int slot, string key) {
            var deviceId = InputDeviceId.FromKey(key: key);
            var output = new RecordingGamepadOutput(deviceId: deviceId);

            Outputs[deviceId] = output;
            Roster.ObserveDeviceKind(device: deviceId, kind: InputDeviceKind.Gamepad);
            Roster.ObserveDevice(device: deviceId, kind: InputDeviceKind.Gamepad, name: key);

            var outcome = Roster.AssignDevice(actingPrincipal: Principal.Console, device: deviceId, targetSlot: slot);

            Assert.True(condition: (outcome is AssignOutcome.JoinedTeam or AssignOutcome.CreatedPending or AssignOutcome.NoOp), userMessage: $"AssignDevice({slot}) -> {outcome}");
            Assert.True(condition: Roster.IsJoined(slot: slot));
            Assert.True(condition: Roster.TryGetSeatDevice(device: out var active, kind: InputDeviceKind.Gamepad, slot: slot));
            Assert.Equal(expected: deviceId, actual: active);

            return output;
        }
        public void AdvanceMilliseconds(double milliseconds) {
            m_now += (long)((milliseconds / 1000d) * Stopwatch.Frequency);
        }
        public void Refresh(WorldDefinition next, ulong tick, params string[] movedRows) {
            m_definition = next;
            var ordinals = new int[movedRows.Length];

            for (var index = 0; (index < movedRows.Length); index++) {
                Assert.True(condition: View.TryResolveRow(ordinal: out ordinals[index], rowName: movedRows[index]));
            }

            Mirror.Refresh(stamp: new WorldStateStamp(
                EngineTick: (tick * 1680UL),
                Everything: false,
                MovedRows: ordinals,
                Tick: tick
            ));
            for (var slot = 0; (slot < m_definition.Population.LocalSeats); slot++) {
                SeatBindings.SyncSeat(
                    definition: m_definition,
                    engineTick: (tick * 1680UL),
                    entityIndex: SeatBindings.RoutedEntityIndex(slot: slot),
                    nextInputTick: (tick + 1UL),
                    slot: slot,
                    state: Mirror
                );
            }
        }
        public void InstallNext(WorldDefinition next, ulong tick, bool newLifetime = false) {
            m_definition = next;

            if (newLifetime) {
                Mirror.BeginLifetime();
            }

            Mirror.Install(engineTick: (tick * 1680UL), tick: tick);
            for (var slot = 0; (slot < m_definition.Population.LocalSeats); slot++) {
                SeatBindings.SyncSeat(
                    definition: m_definition,
                    engineTick: (tick * 1680UL),
                    entityIndex: SeatBindings.RoutedEntityIndex(slot: slot),
                    nextInputTick: (tick + 1UL),
                    slot: slot,
                    state: Mirror
                );
            }
        }
    }

    private static WorldDefinition DocWithIntRow(string rowName, long initial, params WorldHapticRow[] haptics) =>
        Fixtures.BuildDocument().WithWorldState(rows: [
            new WorldStateRow(
                Name: CellName.Parse(candidate: rowName),
                Kind: CellKind.Int,
                Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Int(value: initial))]
            ),
        ]) with {
            HapticsRaw = haptics,
        };
    private static WorldDefinition DocWithFixedRow(string rowName, double initial, params WorldHapticRow[] haptics) =>
        Fixtures.BuildDocument().WithWorldState(rows: [
            new WorldStateRow(
                Name: CellName.Parse(candidate: rowName),
                Kind: CellKind.Fixed,
                Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Fixed(rawBits: FixedQ4816.FromDouble(value: initial).Value))]
            ),
        ]) with {
            HapticsRaw = haptics,
        };

    [Fact]
    public void Rise_seeds_without_firing_then_pulses_only_on_nonpositive_to_positive_transition() {
        var row = new WorldHapticRow(
            DurationSeconds: 0.08f,
            High: 0.75f,
            Id: "hit",
            Low: 0.5f,
            Mode: WorldHapticMode.Rise,
            Signal: new BindableScalar(binding: "state.trigger")
        );
        var initial = DocWithIntRow(rowName: "trigger", initial: 1L, row);
        var harness = new Harness(definition: initial);
        var pad = harness.AttachGamepad(slot: 0, key: "pad:rise");

        // Starting at 1 must seed without firing a spurious pulse.
        harness.Director.Tick(tick: 1UL);
        Assert.Empty(collection: pad.Effects);

        // Dropping to 0 does not fire Rise.
        harness.Refresh(next: DocWithIntRow(rowName: "trigger", initial: 0L, row), tick: 2UL, movedRows: "trigger");
        harness.Director.Tick(tick: 2UL);
        Assert.Empty(collection: pad.Effects);

        // Rising 0 -> 1 fires once.
        harness.Refresh(next: DocWithIntRow(rowName: "trigger", initial: 1L, row), tick: 3UL, movedRows: "trigger");
        harness.Director.Tick(tick: 3UL);
        var fired = Assert.Single(collection: pad.Effects);

        Assert.Equal(expected: 0.5f, actual: fired.LowFrequency);
        Assert.Equal(expected: 0.75f, actual: fired.HighFrequency);
        Assert.Equal(expected: 80u, actual: fired.DurationMilliseconds);

        // Staying at 1 (even across a same-world Install) must not re-fire.
        harness.InstallNext(next: DocWithIntRow(rowName: "trigger", initial: 1L, row), tick: 4UL, newLifetime: false);
        harness.Director.Tick(tick: 4UL);
        Assert.Single(collection: pad.Effects);

        // A new world lifetime resets the tracker and re-seeds cleanly at 0, so a subsequent rise fires again.
        harness.InstallNext(next: DocWithIntRow(rowName: "trigger", initial: 0L, row), tick: 5UL, newLifetime: true);
        harness.Director.Tick(tick: 5UL);
        Assert.Single(collection: pad.Effects);

        harness.Refresh(next: DocWithIntRow(rowName: "trigger", initial: 2L, row), tick: 6UL, movedRows: "trigger");
        harness.Director.Tick(tick: 6UL);
        Assert.Equal(expected: 2, actual: pad.Effects.Count);
        Assert.Equal(expected: 2UL, actual: harness.Director.Status(slot: 0).PulsesSent);
    }
    [Fact]
    public void Change_pulses_on_every_value_change_and_targets_body_key_and_seat() {
        var row1 = new WorldHapticRow(
            DurationSeconds: 0.045f,
            High: 0.1f,
            Id: "beat1",
            Low: 0.25f,
            Mode: WorldHapticMode.Change,
            Seat: "seat1",
            Signal: new BindableScalar(binding: "state.beat.$body")
        );
        var row2 = new WorldHapticRow(
            DurationSeconds: 0.045f,
            High: 0.9f,
            Id: "beat2",
            Low: 0.9f,
            Mode: WorldHapticMode.Change,
            Seat: "seat2",
            Signal: new BindableScalar(binding: "state.beat.$body")
        );

        WorldDefinition WithBeats(long body0, long body2) => Fixtures.BuildDocument().WithWorldState(rows: [
            new WorldStateRow(
                Name: CellName.Parse(candidate: "beat"),
                Kind: CellKind.Int,
                Capacity: 4,
                Cells: [
                    new StateCell(Key: CellName.Parse(candidate: "0"), Value: CellValue.Int(value: body0)),
                    new StateCell(Key: CellName.Parse(candidate: "2"), Value: CellValue.Int(value: body2)),
                ]
            ),
        ]) with {
            HapticsRaw = [row1, row2],
        };

        var harness = new Harness(definition: WithBeats(body0: 0L, body2: 0L), localSeats: 2);
        var pad1 = harness.AttachGamepad(slot: 0, key: "pad:1");
        var pad2 = harness.AttachGamepad(slot: 1, key: "pad:2");

        harness.SeatBindings.SyncSeat(
            definition: WithBeats(body0: 0L, body2: 0L),
            engineTick: 0UL,
            entityIndex: 2,
            nextInputTick: 1UL,
            slot: 1,
            state: harness.Mirror
        );

        // Seed both seats.
        harness.Director.Tick(tick: 1UL);
        Assert.Empty(collection: pad1.Effects);
        Assert.Empty(collection: pad2.Effects);

        // Move body 2 only: seat 2 pulses (targeting body 2 via $body), seat 1 stays quiet.
        harness.Refresh(next: WithBeats(body0: 0L, body2: 1L), tick: 2UL, movedRows: "beat");
        harness.Director.Tick(tick: 2UL);
        Assert.Empty(collection: pad1.Effects);
        var seat2Effect = Assert.Single(collection: pad2.Effects);

        Assert.Equal(expected: 0.9f, actual: seat2Effect.LowFrequency);
        Assert.Equal(expected: 45u, actual: seat2Effect.DurationMilliseconds);

        // Move body 0 only: seat 1 pulses with its own row intensities; seat 2 stays at 1 pulse.
        harness.Refresh(next: WithBeats(body0: 5L, body2: 1L), tick: 3UL, movedRows: "beat");
        harness.Director.Tick(tick: 3UL);
        var seat1Effect = Assert.Single(collection: pad1.Effects);

        Assert.Equal(expected: 0.25f, actual: seat1Effect.LowFrequency);
        Assert.Equal(expected: 0.1f, actual: seat1Effect.HighFrequency);
        Assert.Single(collection: pad2.Effects);
        Assert.Contains(
            actualString: harness.Director.Describe(),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "seat1:rows=1,pad=ready,pulses=1,last=beat1@t3"
        );
    }
    [Fact]
    public void Level_drives_refreshes_and_stops_on_zero_and_device_swap() {
        var row = new WorldHapticRow(
            DurationSeconds: 0.2f,
            High: 0f,
            Id: "hum",
            Low: new BindableScalar(binding: "state.rumble"),
            Mode: WorldHapticMode.Level
        );
        var harness = new Harness(definition: DocWithFixedRow(rowName: "rumble", initial: 0d, row));
        var pad1 = harness.AttachGamepad(slot: 0, key: "pad:a");

        // Zero intensity sends nothing.
        harness.Director.Tick(tick: 1UL);
        Assert.Empty(collection: pad1.Effects);

        // Positive level starts rumble.
        harness.Refresh(next: DocWithFixedRow(rowName: "rumble", initial: 0.4d, row), tick: 2UL, movedRows: "rumble");
        harness.Director.Tick(tick: 2UL);
        Assert.Single(collection: pad1.Effects);
        Assert.Equal(expected: 0.4f, actual: pad1.Effects[0].LowFrequency, precision: 3);

        // Same value before half-duration does not spam.
        harness.AdvanceMilliseconds(milliseconds: 50d);
        harness.Director.Tick(tick: 3UL);
        Assert.Single(collection: pad1.Effects);

        // Past half-duration (100 ms), level refreshes.
        harness.AdvanceMilliseconds(milliseconds: 60d);
        harness.Director.Tick(tick: 4UL);
        Assert.Equal(expected: 2, actual: pad1.Effects.Count);

        // Returning to zero sends RumbleEffect.Off.
        harness.Refresh(next: DocWithFixedRow(rowName: "rumble", initial: 0d, row), tick: 5UL, movedRows: "rumble");
        harness.Director.Tick(tick: 5UL);
        Assert.Equal(expected: 3, actual: pad1.Effects.Count);
        Assert.Equal(expected: RumbleEffect.Off, actual: pad1.Effects[2]);

        // Start rumble again, then swap pad on seat 0: previous pad receives RumbleEffect.Off and new pad takes over.
        harness.Refresh(next: DocWithFixedRow(rowName: "rumble", initial: 0.6d, row), tick: 6UL, movedRows: "rumble");
        harness.Director.Tick(tick: 6UL);
        Assert.Equal(expected: 4, actual: pad1.Effects.Count);

        var pad2 = harness.AttachGamepad(slot: 0, key: "pad:b");

        harness.Director.Tick(tick: 7UL);
        Assert.Equal(expected: 5, actual: pad1.Effects.Count);
        Assert.Equal(expected: RumbleEffect.Off, actual: pad1.Effects[4]);
        Assert.Single(collection: pad2.Effects);
        Assert.Equal(expected: 0.6f, actual: pad2.Effects[0].LowFrequency, precision: 3);
    }
}