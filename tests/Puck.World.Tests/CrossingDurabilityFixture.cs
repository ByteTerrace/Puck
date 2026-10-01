using Puck.Commands;
using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>An authority dying mid-step: thrown from inside a crossing step so nothing after it runs, and the
/// authority's in-memory state is discarded by the law that throws it.</summary>
internal sealed class AuthorityCrashedException : Exception {
    public AuthorityCrashedException(string step) : base(message: $"the authority died at {step}") { }
}
/// <summary>An in-memory durable crossing store for one authority: encoded entries survive the authority object,
/// each activation fences every earlier one, and a crash point can be armed on one record kind.</summary>
internal sealed class MemoryCrossingStore {
    private readonly List<byte[]> m_entries = [];

    private long m_epoch;

    /// <summary>The record kind whose append kills the authority before the record lands, or null.</summary>
    public Type? CrashBefore { get; set; }
    /// <summary>The record kind whose append kills the authority after the record lands, or null.</summary>
    public Type? CrashAfter { get; set; }
    /// <summary>Gets how many records are durable.</summary>
    public int Count => m_entries.Count;

    /// <summary>Opens a new activation: its writer is the only one the store accepts from now on.</summary>
    public IWorldCrossingLog Activate() => new Writer(
        epoch: ++m_epoch,
        store: this
    );
    /// <summary>Reads every durable entry back through the codec, as a restarted authority would.</summary>
    public IReadOnlyList<WorldCrossingEntry> Read(WorldPlayerDefaults defaults) => [.. m_entries.Select(selector: bytes => {
        Assert.True(
            condition: WorldAuthorityCheckpointCodec.TryDecodeCrossingEntry(
                bytes: bytes,
                defaults: defaults,
                entry: out var entry,
                reason: out var reason
            ),
            userMessage: reason
        );
        return entry;
    })];

    private sealed class Writer(MemoryCrossingStore store, long epoch) : IWorldCrossingLog {
        public bool TryAppend(in WorldCrossingEntry entry, out string reason) {
            if (epoch != store.m_epoch) {
                reason = $"activation {epoch} was superseded by activation {store.m_epoch}";
                return false;
            }
            if (store.CrashBefore == entry.Record.GetType()) {
                throw new AuthorityCrashedException(step: $"{entry.Record.GetType().Name} before it was durable");
            }
            store.m_entries.Add(item: WorldAuthorityCheckpointCodec.EncodeCrossingEntry(entry: in entry));
            if (store.CrashAfter == entry.Record.GetType()) {
                throw new AuthorityCrashedException(step: $"{entry.Record.GetType().Name} after it was durable");
            }
            reason = string.Empty;
            return true;
        }
    }
}
/// <summary>The destination as the source's transport sees it: every call reaches the destination's own server, but a
/// destination that dies inside a commit leaves the source with no answer, and an armed lost answer drops a commit's
/// reply after the destination applied it.</summary>
internal sealed class DyingDestinationPeerCall(WorldServer destination) : IWorldPeerCall {
    public bool LoseCommitAnswer { get; set; }

    public void Abort(string sourceAuthority, ulong transferId) => destination.AbortTransfer(
        sourceAuthority: sourceAuthority,
        transferId: transferId
    );
    public void Acknowledge(string sourceAuthority, ulong transferId) => destination.AcknowledgeTransfer(
        sourceAuthority: sourceAuthority,
        transferId: transferId
    );
    public WorldTransferStep Commit(string sourceAuthority, ulong transferId, IReadOnlyList<WorldTransferCommitMember> members, out bool accepted, out string reason) {
        try {
            accepted = destination.CommitTransfer(
                members: members,
                reason: out reason,
                sourceAuthority: sourceAuthority,
                transferId: transferId
            );
        } catch (AuthorityCrashedException) {
            accepted = false;
            reason = "the destination stopped answering";
            return WorldTransferStep.Unreachable;
        }
        if (LoseCommitAnswer) {
            accepted = false;
            reason = "the commit's answer was lost";
            return WorldTransferStep.Unreachable;
        }
        return WorldTransferStep.Answered;
    }
    public WorldTransferReservationReply Reserve(WorldTransferReservationRequest request) => destination.ReserveTransfer(request: request);
    public bool TryStatus(string sourceAuthority, ulong transferId, out WorldTransferStatus status) {
        status = destination.TransferStatus(
            sourceAuthority: sourceAuthority,
            transferId: transferId
        );
        return true;
    }
}
/// <summary>Two authorities of one process, each with its own durable image — a checkpoint taken before any crossing
/// plus its crossing log — which a law can restart from after either one dies.</summary>
internal sealed class CrossingWorld : IDisposable {
    private readonly Guid m_machineId;
    private readonly TemporaryDirectory m_root;
    private readonly bool m_ownsRoot;
    private readonly List<IDisposable> m_owned = [];

    private CrossingWorld(Guid machineId, TemporaryDirectory root, bool ownsRoot, WorldInstanceHost host, HostRow source, HostRow destination, MemoryCrossingStore sourceLog, MemoryCrossingStore destinationLog, byte[] sourceImage, byte[] destinationImage, IWorldCrossingLog sourceWriter, IWorldCrossingLog destinationWriter) {
        m_machineId = machineId;
        m_root = root;
        m_ownsRoot = ownsRoot;
        Host = host;
        Source = source;
        Destination = destination;
        SourceLog = sourceLog;
        DestinationLog = destinationLog;
        SourceImage = sourceImage;
        DestinationImage = destinationImage;
        SourceWriter = sourceWriter;
        DestinationWriter = destinationWriter;
        m_owned.Add(item: host);
        m_owned.Add(item: source);
        m_owned.Add(item: destination);
    }

    public HostRow Destination { get; }
    public byte[] DestinationImage { get; private set; }
    public MemoryCrossingStore DestinationLog { get; }
    public IWorldCrossingLog DestinationWriter { get; }
    public WorldInstanceHost Host { get; }
    public HostRow Source { get; }
    public byte[] SourceImage { get; private set; }
    public MemoryCrossingStore SourceLog { get; }
    public IWorldCrossingLog SourceWriter { get; }

    public void CheckpointDestination() => DestinationImage = Image(host: Host, row: Destination);

    /// <summary>The traveler's stable identity: seat 0 of the source as it was first minted.</summary>
    public WorldEntityAddress Traveler { get; private set; }

    private static WorldInstanceHost NewHost(Guid machineId, TemporaryDirectory root) => new(
        applicationStopping: CancellationToken.None,
        admitsSpawn: true,
        machineHostFactory: Fixtures.MachineHostFactory,
        machineId: machineId,
        resolver: new WorldSessionResolver(),
        seats: WorldEmbodiedSeats.None,
        stateRoot: new WorldStateRoot(path: Directory.CreateDirectory(path: Path.Combine(
            path1: root.RootPath,
            path2: $"host-{Guid.NewGuid():N}"
        )).FullName)
    );
    private static byte[] Image(WorldInstanceHost host, HostRow row) {
        Assert.True(
            condition: row.Server.TryCaptureCheckpoint(
                checkpoint: out var checkpoint,
                hostRow: host.CaptureRow(row: row.Instance),
                reason: out var reason
            ),
            userMessage: reason
        );
        return WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint!);
    }
    private static HostRow Restore(byte[] image, string name) {
        Assert.True(
            condition: WorldAuthorityCheckpointCodec.TryDecode(
                bytes: image,
                checkpoint: out var checkpoint,
                reason: out var reason
            ),
            userMessage: reason
        );
        var definition = WorldDefinitionSerialization.Deserialize(utf8Json: checkpoint!.Server.DefinitionJson);
        var machines = new WorldMachineHost(
            engines: [],
            screens: definition.Screens
        );
        var profilesDirectory = Directory.CreateTempSubdirectory(prefix: $"puck-crossing-durability-{name}-").FullName;

        var (server, _) = WorldServer.FromCheckpoint(
            checkpoint: checkpoint,
            instanceIdentity: name,
            machines: machines,
            profiles: new WorldOwnedWorlds(
                directory: profilesDirectory,
                machineId: Guid.NewGuid(),
                template: definition
            )
        );

        return HostRow.Wrap(
            machines: machines,
            name: name,
            server: server,
            stateDirectory: profilesDirectory
        );
    }

    /// <summary>Builds both authorities with durable logs, seats the traveler on the source, and takes each
    /// authority's durable checkpoint before any crossing.</summary>
    public static CrossingWorld Build() {
        var machineId = Guid.NewGuid();
        var root = new TemporaryDirectory(prefix: "puck-crossing-durability-");
        var host = NewHost(
            machineId: machineId,
            root: root
        );
        var source = HostRow.Build(name: "row-a");
        var destination = HostRow.Build(name: "row-b");
        var sourceLog = new MemoryCrossingStore();
        var destinationLog = new MemoryCrossingStore();
        var sourceWriter = sourceLog.Activate();
        var destinationWriter = destinationLog.Activate();

        source.Server.InstallCrossingLog(log: sourceWriter);
        destination.Server.InstallCrossingLog(log: destinationWriter);
        host.Admit(row: source.Instance);
        host.Admit(row: destination.Instance);
        Assert.True(condition: source.Server.ApplySession(request: new SessionRequest.Join(
            IdentityName: null,
            Principal: Principal.Seat(slot: 0),
            Slot: 0,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        )).Accepted);
        // A second seat keeps the source from being reaped empty once the traveler leaves it.
        Assert.True(condition: source.Server.ApplySession(request: new SessionRequest.Join(
            IdentityName: null,
            Principal: Principal.Seat(slot: 1),
            Slot: 1,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        )).Accepted);

        var world = new CrossingWorld(
            destination: destination,
            destinationImage: [],
            destinationLog: destinationLog,
            destinationWriter: destinationWriter,
            host: host,
            machineId: machineId,
            ownsRoot: true,
            root: root,
            source: source,
            sourceImage: [],
            sourceLog: sourceLog,
            sourceWriter: sourceWriter
        );

        world.Step(ticks: 5);
        world.Traveler = source.Server.Population.ResolveIncarnation(
            authority: source.Server.AuthorityIdentity,
            index: 0
        )!.Value;
        world.SourceImage = Image(
            host: host,
            row: source
        );
        world.DestinationImage = Image(
            host: host,
            row: destination
        );

        return world;
    }
    public void Dispose() {
        foreach (var owned in m_owned) {
            owned.Dispose();
        }
        if (m_ownsRoot) {
            m_root.Dispose();
        }
    }
    public void Step(int ticks) {
        for (var tick = 0; (tick < ticks); tick++) {
            Host.DrainPendingTransfers();
            Host.StepInstances(masterDeltaTicks: Fixtures.StepTicks);
        }
    }
    /// <summary>Queues seat 0's crossing from the source to the destination and drains it.</summary>
    public ulong Cross() {
        var transferId = Host.EnqueueTransfer(
            actingPrincipal: Principal.Console,
            destination: WorldInstanceHost.TransferDestination.Existing(name: "row-b"),
            scope: WorldInstanceHost.TransferScope.Body,
            sourceInstance: "row-a",
            sourceSlot: 0
        );

        Host.DrainPendingTransfers();
        return transferId;
    }
    /// <summary>Counts the active bodies, across both authorities, that are the traveler.</summary>
    public int CountTraveler() {
        var count = 0;

        foreach (var row in new[] { Source, Destination }) {
            for (var slot = 0; (slot < row.Server.Population.Capacity); slot++) {
                if (row.Server.Population.ResolveIncarnation(
                    authority: row.Server.AuthorityIdentity,
                    index: slot
                ) == Traveler) {
                    count++;
                }
            }
        }
        return count;
    }
    public bool TravelerAt(HostRow row) {
        for (var slot = 0; (slot < row.Server.Population.Capacity); slot++) {
            if (row.Server.Population.ResolveIncarnation(
                authority: row.Server.AuthorityIdentity,
                index: slot
            ) == Traveler) {
                return true;
            }
        }
        return false;
    }
    /// <summary>Restarts the authorities that died from their durable images and keeps the survivors' live state:
    /// every authority lands in a fresh host, a dead one at its checkpoint plus every logged crossing record and on a
    /// new activation of its log, a live one at an image of its state this instant on its existing writer.</summary>
    /// <param name="sourceDied">Whether the source restarts from its durable image.</param>
    /// <param name="destinationDied">Whether the destination restarts from its durable image.</param>
    /// <param name="sourceLogLost">Whether a restarted source redoes nothing, as if it had logged nothing.</param>
    /// <param name="destinationLogLost">Whether a restarted destination redoes nothing.</param>
    public CrossingWorld Restart(bool sourceDied, bool destinationDied, bool sourceLogLost = false, bool destinationLogLost = false) {
        var sourceImage = (sourceDied
            ? SourceImage
            : Image(
                host: Host,
                row: Source
            ));
        var destinationImage = (destinationDied
            ? DestinationImage
            : Image(
                host: Host,
                row: Destination
            ));
        var host = NewHost(
            machineId: m_machineId,
            root: m_root
        );
        var source = Restore(
            image: sourceImage,
            name: "row-a"
        );
        var destination = Restore(
            image: destinationImage,
            name: "row-b"
        );
        var sourceWriter = (sourceDied
            ? SourceLog.Activate()
            : SourceWriter);
        var destinationWriter = (destinationDied
            ? DestinationLog.Activate()
            : DestinationWriter);

        SourceLog.CrashBefore = null;
        SourceLog.CrashAfter = null;
        DestinationLog.CrashBefore = null;
        DestinationLog.CrashAfter = null;
        source.Server.InstallCrossingLog(log: sourceWriter);
        destination.Server.InstallCrossingLog(log: destinationWriter);

        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(
            bytes: sourceImage,
            checkpoint: out var sourceCheckpoint,
            reason: out _
        ));
        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(
            bytes: destinationImage,
            checkpoint: out var destinationCheckpoint,
            reason: out _
        ));
        host.Admit(row: source.Instance);
        host.Admit(row: destination.Instance);
        host.RestoreRow(
            row: source.Instance,
            slice: sourceCheckpoint!.HostRow
        );
        host.RestoreRow(
            row: destination.Instance,
            slice: destinationCheckpoint!.HostRow
        );
        host.RecoverCrossings(
            entries: (destinationLogLost
                ? []
                : DestinationLog.Read(defaults: destination.Server.Definition.PlayerDefaults)),
            row: destination.Instance
        );
        host.RecoverCrossings(
            entries: (sourceLogLost
                ? []
                : SourceLog.Read(defaults: source.Server.Definition.PlayerDefaults)),
            row: source.Instance
        );

        var restarted = new CrossingWorld(
            destination: destination,
            destinationImage: DestinationImage,
            destinationLog: DestinationLog,
            destinationWriter: destinationWriter,
            host: host,
            machineId: m_machineId,
            ownsRoot: false,
            root: m_root,
            source: source,
            sourceImage: SourceImage,
            sourceLog: SourceLog,
            sourceWriter: sourceWriter
        ) {
            Traveler = Traveler,
        };

        return restarted;
    }
}
