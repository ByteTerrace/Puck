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
        Assert.Equal([0, 2], lines.Select(TickOf).ToArray());
        Assert.Contains("forward AxisSample", lines[0]);
        Assert.Contains("raw=65536", lines[0]);
        Assert.Contains("forward AxisSample", lines[1]);
        Assert.Contains("raw=0", lines[1]);
        Assert.Equal([0, 1, 2, 3, 4], run.Inspect(all: true).Select(TickOf).ToArray());
        var ranged = Assert.Single(run.Inspect(from: 2));
        Assert.Equal(2, TickOf(ranged));
        Assert.Contains("forward AxisSample", ranged);
        Assert.DoesNotContain(run.Inspect(), line => line.Contains("DIVERGED", StringComparison.Ordinal));
    }

    [Fact]
    public void EachOwnersEventsNameOnlyItsChangedChannels() {
        using var run = new Recording();
        run.Record(secondSeat: true);
        var line = Assert.Single(run.Inspect(), text => TickOf(text) == 1);
        Assert.Contains("seat2 strafe AxisSample", line);
        Assert.Contains("raw=-32768", line);
        Assert.DoesNotContain("seat1", line);
        Assert.DoesNotContain("forward", line);
    }

    [Fact]
    public void AnAuthorityEntryPrintsWithoutAnInputEvent() {
        using var run = new Recording();
        run.Record(secondSeat: false, grant: true);
        var line = Assert.Single(run.Inspect(), text => TickOf(text) == 3);
        Assert.Contains("grant drive body:0 -> seat2 by console", line);
        Assert.DoesNotContain("AxisSample", line);
    }

    private static int TickOf(string line) => int.Parse(line.Split(' ')[2]);

    private sealed class Recording : IDisposable {
        private readonly TemporaryDirectory m_directory = new(prefix: "puck-replay-input-");
        private readonly WorldFixture m_fixture = Fixtures.FreshServer();
        private readonly LoopbackTransport m_transport;
        private readonly WorldReplayTape m_tape;
        private WorldReplayLoad m_loaded;

        public Recording() {
            m_transport = new(m_fixture.Server);
            m_tape = new(stateRoot: new(m_directory.RootPath), liveServer: m_fixture.Server,
                profiles: m_fixture.Server.Profiles, transport: m_transport, engines: [],
                machineHostFactory: Fixtures.MachineHostFactory, addonHostFactory: static (_, _) => new NullAddonHost());
            m_fixture.JoinSeat(0);
            m_fixture.JoinSeat(1);
        }

        public void Record(bool secondSeat, bool grant = false) {
            Assert.True(m_tape.TryBeginRecording("input", out var reason), reason);
            var channels = m_fixture.Server.Population.Channels;
            for (var index = 0; index < 5; index++) {
                var tick = m_fixture.Server.NextInputTick;
                var image = channels.RoleOrdinals.Intent(moveAdvance: index < 2 ? FixedQ4816.One : FixedQ4816.Zero,
                    moveStrafe: FixedQ4816.Zero);
                m_transport.SubmitIntent(Fixtures.Input(m_fixture.Server, Principal.Seat(0), 0, image, tick, m_transport));
                if (secondSeat && index >= 1) {
                    image = channels.RoleOrdinals.Intent(moveAdvance: FixedQ4816.Zero, moveStrafe: FixedQ4816.FromDouble(-0.5));
                    m_transport.SubmitIntent(Fixtures.Input(m_fixture.Server, Principal.Seat(1), 1, image, tick, m_transport));
                }
                if (grant && index == 3) {
                    m_transport.SubmitGrant(new(Principal.Seat(1), WorldCapability.Drive, GrantSubject.Body(0), false), Principal.Console);
                }
                m_fixture.Step();
                m_tape.NoteTick();
            }
            var stopped = m_tape.StopRecording();
            Assert.True(stopped.Verdict!.Passing, stopped.Verdict.Describe());
            m_loaded = WorldReplayInspector.Load(m_tape.PathFor("input"));
        }

        public string[] Inspect(bool all = false, int from = 0) {
            var inspector = new WorldReplayInspector(profiles: m_fixture.Server.Profiles, engines: [],
                machineHostFactory: Fixtures.MachineHostFactory, addonHostFactory: static (_, _) => new NullAddonHost());
            return inspector.Inspect("input", m_loaded, from, 99, all, poses: false)
                .Where(line => line.StartsWith("[replay.inspect: tick ", StringComparison.Ordinal)).ToArray();
        }

        public void Dispose() { m_fixture.Dispose(); m_directory.Dispose(); }
    }
}
