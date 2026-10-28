using Puck.Testing;
using Puck.Commands;
using Xunit;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server.Tests;

/// <summary>Inspection names admitted events from the tape's actual re-drive, including unchanged held ticks,
/// several owners and authority entries. It never infers edges from a retired raw intent image.</summary>
public sealed class ReplayInspectLawTests {
    [Fact]
    public void AdmittedEventsUseRecordedNamesAndTheDefaultViewOmitsUnchangedHolds() {
        using var run = new Recording();

        run.Record(secondSeat: false);
        var lines = run.Inspect();

        Assert.Equal([0, 2], lines.Select(selector: TickOf).ToArray());
        Assert.Contains("forward AxisSample", lines[0]);
        Assert.Contains("raw=65536", lines[0]);
        Assert.Contains("forward AxisSample", lines[1]);
        Assert.Contains("raw=0", lines[1]);
        Assert.Equal([0, 1, 2, 3, 4], run.Inspect(all: true).Select(selector: TickOf).ToArray());
        var ranged = Assert.Single(collection: run.Inspect(from: 2));

        Assert.Equal(2, TickOf(line: ranged));
        Assert.Contains(actualString: ranged, expectedSubstring: "forward AxisSample");
        Assert.DoesNotContain(collection: run.Inspect(), filter: line => line.Contains(comparisonType: StringComparison.Ordinal, value: "DIVERGED"));
    }
    [Fact]
    public void EachOwnersEventsNameOnlyItsChangedChannels() {
        using var run = new Recording();

        run.Record(secondSeat: true);
        var line = Assert.Single(collection: run.Inspect(), predicate: text => (TickOf(line: text) == 1));

        Assert.Contains(actualString: line, expectedSubstring: "seat2 strafe AxisSample");
        Assert.Contains(actualString: line, expectedSubstring: "raw=-32768");
        Assert.DoesNotContain(actualString: line, expectedSubstring: "seat1");
        Assert.DoesNotContain(actualString: line, expectedSubstring: "forward");
    }
    [Fact]
    public void AnAuthorityEntryPrintsWithoutAnInputEvent() {
        using var run = new Recording();

        run.Record(grant: true, secondSeat: false);
        var line = Assert.Single(collection: run.Inspect(), predicate: text => (TickOf(line: text) == 3));

        Assert.Contains(actualString: line, expectedSubstring: "grant drive body:0 -> seat2 by console");
        Assert.DoesNotContain(actualString: line, expectedSubstring: "AxisSample");
    }

    private static int TickOf(string line) => int.Parse(s: line.Split(' ')[2]);

    private sealed class Recording : IDisposable {
        private readonly TemporaryDirectory m_directory = new(prefix: "puck-replay-input-");
        private readonly WorldFixture m_fixture = Fixtures.FreshServer();

        private readonly LoopbackTransport m_transport;
        private readonly WorldReplayTape m_tape;

        private WorldReplayLoad m_loaded;

        public Recording() {
            m_transport = new(server: m_fixture.Server);
            m_tape = new(stateRoot: new(path: m_directory.RootPath), liveServer: m_fixture.Server,
                profiles: m_fixture.Server.Profiles, transport: m_transport, engines: [],
                machineHostFactory: Fixtures.MachineHostFactory, addonHostFactory: static (_, _) => new NullAddonHost());
            m_fixture.JoinSeat(slot: 0);
            m_fixture.JoinSeat(slot: 1);
        }

        public void Record(bool secondSeat, bool grant = false) {
            Assert.True(condition: m_tape.TryBeginRecording(name: "input", refusal: out var reason), userMessage: reason);
            var channels = m_fixture.Server.Population.Channels;

            for (var index = 0; (index < 5); index++) {
                var tick = m_fixture.Server.NextInputTick;
                var image = channels.RoleOrdinals.Intent(moveAdvance: ((index < 2) ? FixedQ4816.One : FixedQ4816.Zero),
                    moveStrafe: FixedQ4816.Zero);

                m_transport.SubmitIntent(submission: Fixtures.Input(m_fixture.Server, Principal.Seat(slot: 0), 0, image, tick, m_transport));
                if (secondSeat && (index >= 1)) {
                    image = channels.RoleOrdinals.Intent(moveAdvance: FixedQ4816.Zero, moveStrafe: FixedQ4816.FromDouble(value: -0.5));
                    m_transport.SubmitIntent(submission: Fixtures.Input(m_fixture.Server, Principal.Seat(slot: 1), 1, image, tick, m_transport));
                }
                if (grant && (index == 3)) {
                    m_transport.SubmitGrant(new(Principal.Seat(slot: 1), WorldCapability.Drive, GrantSubject.Body(index: 0), false), Principal.Console);
                }
                m_fixture.Step();
                m_tape.NoteTick();
            }
            var stopped = m_tape.StopRecording();

            Assert.True(condition: stopped.Verdict!.Passing, userMessage: stopped.Verdict.Describe());
            m_loaded = WorldReplayInspector.Load(path: m_tape.PathFor(name: "input"));
        }
        public string[] Inspect(bool all = false, int from = 0) {
            var inspector = new WorldReplayInspector(profiles: m_fixture.Server.Profiles, engines: [],
                machineHostFactory: Fixtures.MachineHostFactory, addonHostFactory: static (_, _) => new NullAddonHost());

            return inspector.Inspect("input", m_loaded, from, 99, all, poses: false)
                .Where(predicate: line => line.StartsWith(comparisonType: StringComparison.Ordinal, value: "[replay.inspect: tick ")).ToArray();
        }
        public void Dispose() { m_fixture.Dispose(); m_directory.Dispose(); }
    }
}
