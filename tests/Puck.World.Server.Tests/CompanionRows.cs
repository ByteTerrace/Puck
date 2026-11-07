using Puck.Commands;
using Puck.Testing;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>Three authorities in one host, each with a durable crossing log: home (row-a) holding the companions, and
/// two custodians (row-b, row-c). Seat 0 of home is the player; seat 1 keeps home running when the player leaves. A row
/// can be restarted from its durable image (the checkpoint taken when the rows stood, or one a law takes later) plus its
/// crossing log, as a crashed authority recovers.</summary>
internal sealed class CompanionRows : IDisposable {
    private readonly List<IDisposable> m_owned = [];
    private readonly Dictionary<string, HostRow> m_rows = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> m_images = new(comparer: StringComparer.Ordinal);
    private readonly Func<string, IWorldCrossingLog, IWorldCrossingLog> m_logs;

    public CompanionRows(int companions = 1, int peerSlots = 2, bool bDeclaresRole = true, int bPeerSlots = 2, Func<int, string?>? bLandingRefusal = null, bool cDeclaresRole = true, Func<int, string?>? cLandingRefusal = null, Func<string, IWorldCrossingLog, IWorldCrossingLog>? logs = null, bool retained = false, bool admitHumans = false) {
        WorldDefinition Document(int count, int slots, bool role = true) {
            var definition = Fixtures.BuildCompanionDocument(companions: count, peerSlots: slots, declaresRole: role);
            return admitHumans ? definition with {
                Admission = [Fixtures.AnyAuthorityArrivals()],
                PopulationRaw = definition.Population with { NetworkPlayers = slots },
            } : definition;
        }
        m_logs = (logs ?? ((_, log) => log));
        LandingRefusals["row-b"] = bLandingRefusal;
        LandingRefusals["row-c"] = cLandingRefusal;
        Host = new WorldInstanceHost(
            applicationStopping: CancellationToken.None,
            admitsSpawn: true,
            machineHostFactory: Fixtures.MachineHostFactory,
            machineId: Guid.NewGuid(),
            resolver: new WorldSessionResolver(),
            seats: WorldEmbodiedSeats.None,
            stateRoot: new WorldStateRoot(path: Directory.CreateTempSubdirectory(prefix: "puck-companion-host-").FullName)
        );
        m_owned.Add(item: Host);
        Admit(row: HostRow.Build(definition: Document(count: companions, slots: peerSlots), landingRefusal: Refusal(name: "row-a"), name: "row-a"), fresh: true);
        Admit(row: HostRow.Build(definition: Document(count: 0, slots: bPeerSlots, role: bDeclaresRole), landingRefusal: Refusal(name: "row-b"), name: "row-b"), fresh: true);
        Admit(row: HostRow.Build(definition: Document(count: 0, slots: 2, role: cDeclaresRole), landingRefusal: Refusal(name: "row-c"), name: "row-c"), fresh: true);
        foreach (var slot in new[] { 0, 1 }) {
            Assert.True(condition: Home.Server.ApplySession(request: new SessionRequest.Join(
                IdentityName: null,
                Principal: Principal.Seat(slot: slot),
                Slot: slot,
                WireProtocolKey: WorldProtocol.WireProtocolKey
            )).Accepted);
        }
        // A retained row stays up when its last body leaves (a persistent world), so a law that compares every
        // authority's state after each step always finds it running.
        if (retained) {
            foreach (var row in m_rows.Values) {
                Host.RestoreRow(row: row.Instance, slice: Host.CaptureRow(row: row.Instance) with { Retained = true });
            }
        }
        Step(ticks: 3);
        Player = Incarnation(row: Home, slot: 0)!.Value;
        // In member order: the cohort lists companions by the body they leave.
        Companions = [.. Enumerable.Range(start: 0, count: companions).Select(selector: index => PlacementSlot(row: Home, placementId: $"companion-{index}")).Order().Select(selector: slot => Incarnation(row: Home, slot: slot)!.Value)];
        foreach (var name in m_rows.Keys.ToArray()) {
            Checkpoint(name: name);
        }
    }

    public WorldInstanceHost Host { get; }
    public HostRow Home => m_rows["row-a"];
    public HostRow B => m_rows["row-b"];
    public HostRow C => m_rows["row-c"];
    public IEnumerable<HostRow> All => m_rows.Values;
    public Dictionary<string, MemoryCrossingStore> Logs { get; } = new(comparer: StringComparer.Ordinal);
    /// <summary>The admission policy each row's escrow evaluates at every commit member's ordinal; a law changes it
    /// while the rows run.</summary>
    public Dictionary<string, Func<int, string?>?> LandingRefusals { get; } = new(comparer: StringComparer.Ordinal);
    public WorldEntityAddress Player { get; }
    public WorldEntityAddress[] Companions { get; }
    /// <summary>Runs after a row is admitted (at construction and after every restart): where a law installs its seams.</summary>
    public Action<HostRow>? Admitted { get; set; }

    public HostRow Row(string name) => m_rows[name];
    public void Dispose() {
        foreach (var owned in Enumerable.Reverse(source: m_owned)) {
            owned.Dispose();
        }
    }
    public void Step(int ticks) {
        for (var tick = 0; (tick < ticks); tick++) {
            Host.DrainPendingTransfers();
            Host.StepInstances(masterDeltaTicks: Fixtures.StepTicks);
        }
    }
    /// <summary>Moves the player from <paramref name="from"/> to <paramref name="to"/> and settles everything the crossing owes.</summary>
    public void Cross(HostRow from, HostRow to) {
        _ = Enqueue(from: from, to: to);
        Step(ticks: 3);
    }
    /// <summary>Queues the player's crossing and returns its transfer id.</summary>
    public ulong Enqueue(HostRow from, HostRow to) => Host.EnqueueTransfer(
        actingPrincipal: Principal.Console,
        destination: WorldInstanceHost.TransferDestination.Existing(name: to.Instance.Name),
        scope: WorldInstanceHost.TransferScope.Body,
        sourceInstance: from.Instance.Name,
        sourceSlot: SlotOf(row: from, incarnation: Player)
    );
    /// <summary>Takes the row's durable image now: what a later <see cref="Restart"/> recovers from.</summary>
    public void Checkpoint(string name) {
        var row = m_rows[name];

        Assert.True(condition: row.Server.TryCaptureCheckpoint(checkpoint: out var checkpoint, hostRow: Host.CaptureRow(row: row.Instance), reason: out var reason), userMessage: reason);
        m_images[name] = WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint!);
    }
    /// <summary>Replaces the row inside this host, as a crashed authority restarts: the old row stops, a new one restores
    /// the row's latest image, redoes its crossing log on a new activation of that log, and is admitted under the same
    /// authority, where its peers' in-doubt transfers find it.</summary>
    public void Restart(string name) {
        Assert.True(condition: Host.TryRetire(name: name, reason: out var reason), userMessage: reason);
        Activate(name: name);
    }
    /// <summary>Makes every image an explicit stop writes the row's durable image, and records each write: the row and
    /// whether the image resumes the stop. <paramref name="refuse"/> names the rows whose final image the store
    /// refuses.</summary>
    public void KeepDurableImages(Func<string, bool>? refuse = null) => Host.DurableImages = new RecordingImages(rows: this, refuse: refuse);
    // Keeps every row's image in memory and records each write; reads nothing back, since a law restarts a row itself.
    private sealed class RecordingImages(CompanionRows rows, Func<string, bool>? refuse) : IWorldDurableImages {
        public bool Keeps(string instanceName) => true;
        public bool TryWrite(string instanceName, WorldAuthorityCheckpoint checkpoint, out string reason) {
            if (!checkpoint.HostRow.Stopping && (refuse?.Invoke(arg: instanceName) == true)) {
                reason = "the store refused the write";
                return false;
            }
            rows.m_images[instanceName] = WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint);
            rows.ImageWrites.Add(item: (instanceName, checkpoint.HostRow.Stopping));
            reason = string.Empty;
            return true;
        }
        public bool TryRead(string instanceName, string? documentDirectory, out WorldAuthorityCheckpoint? checkpoint, out string reason) {
            checkpoint = null;
            reason = string.Empty;
            return false;
        }
        public void SetAside(string instanceName) { }
    }
    /// <summary>Every image a stop wrote durably, in order: the row and whether the image resumes the stop.</summary>
    public List<(string Row, bool Stopping)> ImageWrites { get; } = [];
    /// <summary>Starts a row no longer running from its latest durable image and its crossing log, as a later activation
    /// of a stopped authority does.</summary>
    public void Activate(string name) {
        Assert.False(condition: Host.TryGet(instance: out _, name: name));
        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(bytes: m_images[name], checkpoint: out var checkpoint, reason: out var reason), userMessage: reason);

        var definition = WorldDefinitionSerialization.Deserialize(utf8Json: checkpoint!.Server.DefinitionJson);
        var machines = new WorldMachineHost(engines: [], screens: definition.Screens);
        var scratch = new TemporaryDirectory(bestEffortDelete: true, prefix: $"puck-companion-restart-{name}-");
        var (server, _) = WorldServer.FromCheckpoint(
            checkpoint: checkpoint,
            instanceIdentity: name,
            machines: machines,
            profiles: new WorldOwnedWorlds(directory: scratch.RootPath, machineId: Guid.NewGuid(), template: definition)
        );

        Admit(row: HostRow.Wrap(machines: machines, name: name, scratch: scratch, server: server), fresh: false);
        Host.RestoreRow(row: m_rows[name].Instance, slice: checkpoint.HostRow);
        Host.RecoverCrossings(entries: Logs[name].Read(), row: m_rows[name].Instance);
        Admitted?.Invoke(obj: m_rows[name]);
    }

    public static WorldEntityAddress? Incarnation(HostRow row, int slot) => row.Server.ExecuteAuthorityOperation(operation: () => row.Server.Population.ResolveIncarnation(
        authority: row.Server.AuthorityIdentity,
        index: slot
    ));
    public static int PlacementSlot(HostRow row, string placementId) {
        for (var slot = 0; (slot < row.Server.Population.Capacity); slot++) {
            if (row.Server.Population.IsActive(index: slot) && string.Equals(a: row.Server.Population.InhabitantPlacementId(index: slot), b: placementId, comparisonType: StringComparison.Ordinal)) {
                return slot;
            }
        }
        throw new InvalidOperationException(message: $"no body of placement '{placementId}' stands in '{row.Instance.Name}'");
    }
    public static int SlotOf(HostRow row, WorldEntityAddress incarnation) {
        for (var slot = 0; (slot < row.Server.Population.Capacity); slot++) {
            if (row.Server.Population.IsActive(index: slot) && (Incarnation(row: row, slot: slot) == incarnation)) {
                return slot;
            }
        }
        return -1;
    }
    public static ulong EpochAt(HostRow row, WorldEntityAddress incarnation) => row.Server.ExecuteAuthorityOperation(operation: () => row.Server.Population.ReadMobility(
        authority: row.Server.AuthorityIdentity,
        index: SlotOf(row: row, incarnation: incarnation)
    ).Epoch);
    public WorldCustodyTitle Title(WorldEntityAddress companion) => Home.Server.ExecuteAuthorityOperation(operation: () => Home.Server.TransferEscrow.Custody.TitleOf(incarnation: companion))!;
    public IReadOnlyList<WorldCrossingRecord> Records(HostRow row) => [.. Logs[row.Instance.Name].Read().Select(selector: static entry => entry.Record)];

    private Func<int, string?> Refusal(string name) => ordinal => LandingRefusals.GetValueOrDefault(key: name)?.Invoke(arg: ordinal);
    private void Admit(HostRow row, bool fresh) {
        var name = row.Instance.Name;

        if (fresh) {
            Logs[name] = new MemoryCrossingStore();
        }
        row.Server.InstallCrossingLog(log: m_logs(arg1: name, arg2: Logs[name].Activate()));
        Host.Admit(row: row.Instance);
        m_rows[name] = row;
        m_owned.Add(item: row);
        if (fresh) {
            Admitted?.Invoke(obj: row);
        }
    }
}
