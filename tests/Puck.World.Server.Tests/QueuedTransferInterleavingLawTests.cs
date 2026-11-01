using Puck.Commands;
using Puck.Maths;
using Puck.Testing;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

// Transfers queue in one FIFO and drain at one fixed point, so whatever drains ahead of a transfer can move its
// traveller, refill the slot it left, stop and start the instance it stood in, or mint ids out of enqueue order. Each
// interleaving below queues work in one such order and checks one invariant: every body a scenario seats is carried by
// exactly one body this host runs, standing where the transfers that named it put it; and an instance a queued
// transfer is bound for is still there when it drains.
public sealed class QueuedTransferInterleavingLawTests {
    private const string RowA = "row-a";
    private const string RowB = "row-b";
    private const string RowC = "row-c";
    private static readonly FixedSpawnPoint Home = new(
        Position: new FixedVector3(
            X: FixedQ4816.FromDouble(value: 3.5),
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.FromDouble(value: -2.25)
        ),
        YawRadians: FixedQ4816.FromDegrees(degrees: 90.0)
    );

    public static TheoryData<string> Interleavings => [
        "move: a pose queued behind its traveller's move and a refill of the slot it left",
        "move: a slot transfer queued behind its traveller's move and a refill of the slot it left",
        "move: an emptied instance a queued transfer is bound for",
        "move: an emptied instance whose queued arrival is refused",
        "recreate: a pose whose traveller left an instance started again under its name",
        "recreate: a transfer queued by an instance stopped and started again",
        "re-route: a non-atomic party splitting ahead of a transfer from the same source",
        "pose: a pose followed to an instance with an unrelated transfer queued behind it",
    ];

    [Theory]
    [MemberData(memberName: nameof(Interleavings))]
    public void EveryQueuedTransferMovesOnlyTheTravellerItNamed(string interleaving) {
        using var host = new Interleaving();
        var expected = new Dictionary<string, string?>(comparer: StringComparer.Ordinal);
        var refusals = new List<string>();

        switch (interleaving[..interleaving.IndexOf(value: ':')]) {
            case "move" when interleaving.Contains(value: "a pose queued"):
                host.Start(RowA, RowB, RowC);
                host.Seat(label: "T", instance: RowA, slot: 0);
                host.Seat(label: "U", instance: RowC, slot: 0);
                host.Move(from: RowA, slot: 0, to: RowB);
                host.Move(from: RowC, slot: 0, to: RowA);
                host.Pose(label: "T");
                host.Drain();
                expected["T"] = WorldInstanceHost.BootInstanceName;
                expected["U"] = RowA;
                break;
            case "move" when interleaving.Contains(value: "a slot transfer"):
                host.Start(RowA, RowB, RowC);
                host.Seat(label: "T", instance: RowA, slot: 0);
                host.Seat(label: "U", instance: RowC, slot: 0);
                host.Move(from: RowA, slot: 0, to: RowB);
                host.Move(from: RowC, slot: 0, to: RowA);
                host.Move(from: RowA, slot: 0, to: RowC);
                host.Drain();
                expected["T"] = RowB;
                expected["U"] = RowA;
                refusals.Add(item: "has left seat 1 in 'row-a' since it was queued");
                break;
            case "move" when interleaving.Contains(value: "is refused"):
                host.Start(RowA, RowB);
                host.Seat(label: "T", instance: RowA, slot: 0);
                host.Move(from: RowA, slot: 0, to: RowB);
                host.Move(from: RowB, slot: 3, to: RowA);
                host.Drain();
                expected["T"] = RowB;
                refusals.Add(item: "seat 4 is not active in 'row-b'");
                Assert.False(condition: host.Host.TryGet(name: RowA, instance: out _), userMessage: "an emptied instance outlived the drain that refused its last queued arrival");
                break;
            case "move":
                host.Start(RowA, RowB, RowC);
                host.Seat(label: "T", instance: RowA, slot: 0);
                host.Seat(label: "U", instance: RowC, slot: 0);
                host.Move(from: RowA, slot: 0, to: RowB);
                host.Move(from: RowC, slot: 0, to: RowA);
                host.Drain();
                expected["T"] = RowB;
                expected["U"] = RowA;
                break;
            case "recreate" when interleaving.Contains(value: "a pose"): {
                host.Start(RowA, RowB, RowC);
                host.Seat(label: "T", instance: RowA, slot: 0);
                host.Move(from: RowA, slot: 0, to: RowB);
                host.Drain();
                Assert.False(condition: host.Host.TryGet(name: RowA, instance: out _));
                host.Start(RowA);
                host.Seat(label: "U", instance: RowA, slot: 0);

                var traveller = host.Incarnation(label: "T");
                var occupant = host.Incarnation(label: "U");

                // The hazard: the new lifetime seats a body at the slot and generation the traveller left.
                Assert.Equal(expected: (traveller.Index, traveller.Generation), actual: (occupant.Index, occupant.Generation));
                host.Move(from: RowB, slot: host.SlotOf(label: "T"), to: RowC);
                host.Pose(label: "T");
                host.Drain();
                expected["T"] = WorldInstanceHost.BootInstanceName;
                expected["U"] = RowA;
                break;
            }
            case "recreate":
                host.Start(RowA, RowB);
                host.Seat(label: "T", instance: RowA, slot: 0);
                host.Move(from: RowA, slot: 0, to: RowB);
                host.Stop(name: RowA);
                host.Start(RowA);
                host.Seat(label: "U", instance: RowA, slot: 0);
                host.Drain();
                expected["T"] = null;
                expected["U"] = RowA;
                refusals.Add(item: "'row-a' stopped after queuing it");
                break;
            case "re-route":
                host.Start(RowA, RowB, RowC);
                host.Seat(label: "P", instance: RowA, slot: 0);
                host.Seat(label: "Q", instance: RowA, slot: 1);
                host.Seat(label: "R", instance: RowA, slot: 2);
                _ = host.Host.EnqueueTransfer(
                    actingPrincipal: Principal.Console,
                    destination: WorldInstanceHost.TransferDestination.Existing(name: RowB),
                    frozenCohortSlots: [0, 1],
                    partyAllOrNothing: false,
                    scope: WorldInstanceHost.TransferScope.Party,
                    sourceInstance: RowA,
                    sourceSlot: 0
                );
                host.Move(from: RowA, slot: 2, to: RowC);
                host.Drain();
                expected["P"] = RowB;
                expected["Q"] = RowB;
                expected["R"] = RowC;
                break;
            case "pose":
                host.Start(RowA, RowB, RowC);
                host.Seat(label: "T", instance: RowA, slot: 0);
                host.Seat(label: "U", instance: RowB, slot: 0);
                host.Move(from: RowA, slot: 0, to: RowB);
                host.Pose(label: "T");
                host.Move(from: RowB, slot: 0, to: RowC);
                host.Drain();
                expected["T"] = WorldInstanceHost.BootInstanceName;
                expected["U"] = RowC;
                refusals.Add(item: "follows its traveller");
                break;
            default:
                throw new InvalidOperationException(message: $"no interleaving '{interleaving}'");
        }

        var transcript = host.Transcript();

        foreach (var (label, instance) in expected) {
            var (carriers, standing) = host.Locate(label: label);

            Assert.True(
                condition: (carriers == ((instance is null) ? 0 : 1)) && string.Equals(a: standing, b: instance, comparisonType: StringComparison.Ordinal),
                userMessage: $"{label} is carried by {carriers} bodies, standing in '{standing}', where '{instance}' was meant:\n{transcript}"
            );
        }

        Assert.DoesNotContain(expectedSubstring: "no instance named", actualString: transcript, comparisonType: StringComparison.Ordinal);

        foreach (var refusal in refusals) {
            Assert.Contains(expectedSubstring: refusal, actualString: transcript, comparisonType: StringComparison.Ordinal);
        }

        if (expected.TryGetValue(key: "T", value: out var home) && (home == WorldInstanceHost.BootInstanceName)) {
            var body = host.Host.Boot!.Server.Population.EntryBody(index: host.SlotOf(label: "T"))!;

            Assert.Equal(expected: Home.Position, actual: body.FixedPosition);
        }
    }

    // One host, a boot row, and rows started from one document file through the host's own start door, so stopping and
    // starting a name again goes through the path a running World takes.
    private sealed class Interleaving : IDisposable {
        private readonly TemporaryDirectory m_state = new(prefix: "puck-transfer-interleaving-");
        private readonly HostRow m_boot = HostRow.Build(name: WorldInstanceHost.BootInstanceName);
        private readonly RecordingNarrationSink m_narration = new();
        private readonly IDisposable m_lease;
        private readonly string m_document;
        private readonly Dictionary<string, WorldEntityAddress> m_bodies = new(comparer: StringComparer.Ordinal);

        public Interleaving() {
            Host = new WorldInstanceHost(
                applicationStopping: CancellationToken.None,
                admitsSpawn: true,
                machineHostFactory: Fixtures.MachineHostFactory,
                machineId: Guid.NewGuid(),
                resolver: new WorldSessionResolver(),
                seats: WorldEmbodiedSeats.None,
                stateRoot: new WorldStateRoot(path: m_state.RootPath)
            );
            Host.Admit(row: m_boot.Instance);
            m_lease = Host.AttachNarrationSink(sink: m_narration);
            m_document = m_state.WriteBytes(
                bytes: WorldDefinitionSerialization.Serialize(definition: Fixtures.BuildDocument()),
                name: "interleaving.world.json"
            );
        }

        public WorldInstanceHost Host { get; }

        public void Dispose() {
            m_lease.Dispose();
            Host.Dispose();
            m_boot.Dispose();
            m_state.Dispose();
        }
        public void Start(params string[] names) {
            foreach (var name in names) {
                Assert.True(condition: Host.TryStart(instance: out _, name: name, path: m_document, reason: out var reason), userMessage: reason);
            }
        }
        public void Stop(string name) => Assert.True(condition: Host.TryRetire(name: name, reason: out var reason), userMessage: reason);
        public void Seat(string label, string instance, int slot) {
            var row = Row(name: instance);

            Assert.True(condition: row.Server.ApplySession(request: new SessionRequest.Join(
                IdentityName: null,
                Principal: Principal.Seat(slot: slot),
                Slot: slot,
                WireProtocolKey: WorldProtocol.WireProtocolKey
            )).Accepted);
            Host.StepInstances(masterDeltaTicks: Fixtures.StepTicks);
            m_bodies[label] = row.Server.Population.ResolveIncarnation(
                authority: row.Server.AuthorityIdentity,
                index: slot
            )!.Value;
        }
        public void Move(string from, int slot, string to) => _ = Host.EnqueueTransfer(
            actingPrincipal: Principal.Console,
            destination: WorldInstanceHost.TransferDestination.Existing(name: to),
            scope: WorldInstanceHost.TransferScope.Body,
            sourceInstance: from,
            sourceSlot: slot
        );
        // Queues the pose a seat's route asks for: from the instance the traveller stands in now, naming the
        // generation-addressed body there.
        public void Pose(string label) {
            var (_, standing) = Locate(label: label);
            var row = Row(name: standing!);
            var slot = SlotOf(label: label);

            Assert.True(
                condition: Host.TryEnqueuePoseHome(
                    actingPrincipal: Principal.Console,
                    arrivalPose: Home,
                    reason: out var reason,
                    sourceInstance: standing!,
                    transferId: out _,
                    traveller: new WorldEntityAddress(
                        Authority: row.Server.AuthorityIdentity,
                        Index: slot,
                        Generation: row.Server.Population.Generation(index: slot)
                    )
                ),
                userMessage: reason
            );
        }
        public void Drain() => Host.DrainPendingTransfers();
        public WorldEntityAddress Incarnation(string label) => m_bodies[label];
        // How many bodies this host runs carry the label's incarnation, and the instance the first stands in.
        public (int Carriers, string? Instance) Locate(string label) {
            var carriers = 0;
            string? standing = null;

            foreach (var name in Host.Names) {
                var row = Row(name: name);

                for (var slot = 0; (slot < row.Server.Population.Capacity); slot++) {
                    if (row.Server.Population.ResolveIncarnation(authority: row.Server.AuthorityIdentity, index: slot) == m_bodies[label]) {
                        carriers++;
                        standing ??= name;
                    }
                }
            }

            return (carriers, standing);
        }
        public int SlotOf(string label) {
            var (_, standing) = Locate(label: label);
            var row = Row(name: standing!);

            for (var slot = 0; (slot < row.Server.Population.Capacity); slot++) {
                if (row.Server.Population.ResolveIncarnation(authority: row.Server.AuthorityIdentity, index: slot) == m_bodies[label]) {
                    return slot;
                }
            }

            return -1;
        }
        public string Transcript() => string.Join(separator: "\n", values: m_narration.Narrations.Select(selector: static line => line.Text));

        private WorldInstance Row(string name) {
            Assert.True(condition: Host.TryGet(name: name, instance: out var row), userMessage: $"no instance named '{name}'");

            return row!;
        }
    }
}
