using System.Text;
using Puck.Commands;
using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// What crosses a seam is the traveler's identity projection and nothing else: its id, name, color, the two rates, its
/// selected records and its facts. The owned document behind it never travels, on any path: a remote commit, a commit
/// retried after its source restarts, a colocated crossing, and every durable leaf a crossing writes (the destination's
/// crossing log, its arrival tape, its checkpoint). A destination keeps a visitor's facts on the travelling row and never
/// saves the visitor into its own catalog; the authority the seat left adopts the carried facts and records, and only
/// those, when the seat comes home. The owned document gives the private rows distinctive values so that their absence
/// from bytes is a real search.
/// </summary>
public sealed class CrossingIdentityPrivacyLawTests {
    private const string OwnerId = "privacy-owner";
    private const string PrivatePanel = "private-hud-panel";
    private const string PrivatePayload = "private-payload";
    private const string PrivateRow = "private-secret-row";
    // Steps long enough for any destination lease taken during a crossing to expire.
    private const int PastEveryLease = 90;

    private static readonly string[] Markers = [PrivateRow, PrivatePayload, PrivatePanel];

    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static WorldDefinition OwnedDocument() {
        var basis = Fixtures.BuildDocument();

        return basis with {
            Identity = new WorldIdentityDefinition(
                Id: SafeName.Parse(candidate: OwnerId),
                Name: "Owner",
                Color: "#3366cc",
                MoveSpeedState: Name(value: "move"),
                TurnSpeedState: Name(value: "turn"),
                Records: [Name(value: "stats")]
            ),
            HudRaw = new WorldHudSection(
                Defaults: new WorldHudDefaults(Enabled: true),
                Panels: [new WorldHudPanel(
                    Id: PrivatePanel,
                    Rect: new WorldHudRect(Height: 1f, Width: 1f, X: 0f, Y: 0f),
                    Layer: WorldHudLayer.Over,
                    Style: WorldHudPanelStyle.Chip,
                    Elements: []
                )]
            ),
            StateRaw = new WorldStateSection(
                World: [new WorldStateRow(
                    Name: Name(value: PrivateRow),
                    Kind: CellKind.Text,
                    Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Text(value: PrivatePayload))]
                )],
                Records: [new StateRecord(Name: Name(value: "Stats"), Fields: [
                    new StatePoolField(Name: Name(value: "score"), Default: CellValue.Int(value: 0), Min: 0, Max: 100),
                    new StatePoolField(Name: Name(value: "badge"), Kind: CellKind.Text, Default: CellValue.Text(value: "visitor")),
                ])],
                Pools: [new StatePool(Name: Name(value: "stats"), Record: Name(value: "Stats"), Capacity: 1, Initial: [new StatePoolSeed(Slot: 0)])]
            ),
        };
    }
    // An owned identity carrying one fact and one written record field beside its private rows.
    private static WorldIdentity Owned(WorldIdentity? identity = null) {
        var owned = (identity ?? new WorldIdentity(
            defaults: Fixtures.BuildDocument().PlayerDefaults,
            document: OwnedDocument()
        ));

        Assert.True(condition: owned.TrySetFact(key: Name(value: "homeFact"), value: 3, changed: out _, reason: out var reason), userMessage: reason);
        Assert.True(condition: owned.TryWriteRecord(record: Name(value: "stats"), field: Name(value: "badge"), value: CellValue.Text(value: "champion"), reason: out reason), userMessage: reason);
        owned.Bindings = new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [],
            Chords: []
        );
        return owned;
    }
    private static long? Fact(WorldIdentity identity, string key) =>
        ((identity.Facts?.Cells?.FirstOrDefault(predicate: cell => (cell.Key == Name(value: key))) is { } cell)
            ? cell.Value.AsInt
            : null);
    private static string Record(WorldIdentity identity, string field) => (identity.TryReadRecord(
        field: Name(value: field),
        record: Name(value: "stats"),
        value: out var value
    )
        ? ((value.Kind == CellKind.Text) ? value.AsText : value.AsInt.ToString(provider: System.Globalization.CultureInfo.InvariantCulture))
        : "absent");
    // Everything the projection carries, spelled so two arrivals compare as one string.
    private static string Carried(WorldIdentity identity) =>
        $"{identity.Id}|{identity.Name}|{identity.ColorHex}|{identity.FixedMoveSpeed}|{identity.FixedTurnSpeed}|homeFact={Fact(identity: identity, key: "homeFact")}|badge={Record(field: "badge", identity: identity)}";
    private static void AssertArrivedAsProjection(WorldIdentity arrived, WorldIdentity departed) {
        Assert.NotSame(actual: arrived, expected: departed);
        Assert.Null(@object: arrived.Document);
        Assert.Null(@object: arrived.Hud);
        Assert.Null(@object: arrived.Bindings);
        Assert.Null(@object: arrived.SeatLook);
        Assert.False(condition: arrived.TryReadState(name: PrivateRow, row: out _));
        Assert.Equal(expected: Carried(identity: departed), actual: Carried(identity: arrived));
    }
    private static void AssertNoPrivateBytes(byte[] bytes, string what) {
        var text = Encoding.UTF8.GetString(bytes: bytes);

        foreach (var marker in Markers) {
            Assert.False(condition: text.Contains(comparisonType: StringComparison.Ordinal, value: marker), userMessage: $"{what} carries '{marker}'");
        }
    }
    private static WorldIdentity ArrivedAt(WorldServer server, string id) {
        for (var slot = 0; (slot < server.Population.Capacity); slot++) {
            if (string.Equals(a: server.Population.EntryBody(index: slot)?.Profile?.Id, b: id, comparisonType: StringComparison.Ordinal)) {
                return server.Population.EntryBody(index: slot)!.Profile!;
            }
        }
        throw new InvalidOperationException(message: $"'{server.AuthorityIdentity}' holds no body driven by '{id}'");
    }
    private static int SlotOf(WorldServer server, string id) {
        for (var slot = 0; (slot < server.Population.Capacity); slot++) {
            if (string.Equals(a: server.Population.EntryBody(index: slot)?.Profile?.Id, b: id, comparisonType: StringComparison.Ordinal)) {
                return slot;
            }
        }
        throw new InvalidOperationException(message: $"'{server.AuthorityIdentity}' holds no body driven by '{id}'");
    }
    private static WorldReplayTape Tape(WorldServer server, string directory) => new(
        addonHostFactory: static (_, _) => new NullAddonHost(),
        engines: [],
        liveServer: server,
        machineHostFactory: Fixtures.MachineHostFactory,
        profiles: server.Profiles,
        stateRoot: new WorldStateRoot(path: directory),
        transport: new LoopbackTransport(server: server)
    );
    private static byte[] StopTape(WorldReplayTape tape) {
        tape.NoteTick();
        _ = tape.StopRecording();
        return File.ReadAllBytes(path: tape.PathFor(name: "arrivals"));
    }
    private static byte[] Checkpoint(WorldInstanceHost host, HostRow row) {
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

    /// <summary>Records every crossing entry's encoded bytes and answers each append durable.</summary>
    private sealed class CapturingCrossingLog : IWorldCrossingLog {
        public List<byte[]> Entries { get; } = [];

        public WorldCrossingDurability Append(in WorldCrossingEntry entry, out string reason) {
            Entries.Add(item: WorldAuthorityCheckpointCodec.EncodeCrossingEntry(entry: in entry));
            reason = string.Empty;
            return WorldCrossingDurability.Durable;
        }
    }

    // THE LAW: a remote commit carries the projection alone. The traveler is seated on the source with an owned document
    // holding a private row, a private HUD panel and a binding layer; it crosses over QUIC to the destination row. The
    // destination's installed identity has no document, no panel, no bindings and no private row, and carries exactly
    // the projected id, name, color, rates, facts and records; the destination's crossing log, arrival tape and
    // checkpoint carry none of the private bytes. The red leg is the commit leaf that serializes the owned document: the
    // destination installs it whole, and its log and tape write it at rest.
    [Fact]
    public void ARemoteCommitInstallsTheProjectionAndWritesNoPrivateBytesAtRest() {
        using var directory = new TemporaryDirectory(prefix: "puck-privacy-remote-");
        using var hosts = FederatedHosts.Build();
        var log = new CapturingCrossingLog();
        var tape = Tape(directory: directory.RootPath, server: hosts.DestinationRow.Server);
        var owned = Owned();

        hosts.DestinationRow.Server.InstallCrossingLog(log: log);
        Assert.True(condition: tape.TryBeginRecording(name: "arrivals", refusal: out var refusal), userMessage: refusal);
        hosts.JoinSeat(slot: 0, travelTurn: default);
        hosts.SourceRow.Server.Population.SetSeatProfile(profile: owned, slot: 0);
        _ = hosts.Source.EnqueueTransfer(
            actingPrincipal: Principal.Console,
            destination: hosts.RemoteDestination,
            scope: WorldInstanceHost.TransferScope.Body,
            sourceInstance: WorldInstanceHost.BootInstanceName,
            sourceSlot: 0
        );
        hosts.Source.DrainPendingTransfers();

        AssertArrivedAsProjection(
            arrived: hosts.DestinationRow.Server.ExecuteAuthorityOperation(operation: () => ArrivedAt(id: OwnerId, server: hosts.DestinationRow.Server)),
            departed: owned
        );
        Assert.NotEmpty(collection: log.Entries);
        foreach (var entry in log.Entries) {
            AssertNoPrivateBytes(bytes: entry, what: "the destination's crossing log");
        }
        AssertNoPrivateBytes(bytes: StopTape(tape: tape), what: "the destination's arrival tape");
        AssertNoPrivateBytes(bytes: Checkpoint(host: hosts.Destination, row: hosts.DestinationRow), what: "the destination's checkpoint");
    }
    // THE LAW: a colocated crossing is the remote interface short-circuited, never a second path, so it carries the same
    // projection. The red leg hands the source's live identity object to the destination, which then holds the
    // traveler's document, panel and bindings, and writes the document into its log and tape.
    [Fact]
    public void AColocatedCrossingInstallsTheProjectionAndWritesNoPrivateBytesAtRest() {
        using var directory = new TemporaryDirectory(prefix: "puck-privacy-colocated-");
        using var world = CrossingWorld.Build();
        var tape = Tape(directory: directory.RootPath, server: world.Destination.Server);
        var owned = Owned();

        world.Source.Server.Population.SetSeatProfile(profile: owned, slot: 0);
        Assert.True(condition: tape.TryBeginRecording(name: "arrivals", refusal: out var refusal), userMessage: refusal);
        _ = world.Cross();

        AssertArrivedAsProjection(arrived: ArrivedAt(id: OwnerId, server: world.Destination.Server), departed: owned);
        Assert.NotEmpty(collection: world.DestinationLog.Encoded);
        foreach (var entry in world.SourceLog.Encoded.Concat(second: world.DestinationLog.Encoded)) {
            AssertNoPrivateBytes(bytes: entry, what: "a crossing log");
        }
        AssertNoPrivateBytes(bytes: StopTape(tape: tape), what: "the destination's arrival tape");
        world.CheckpointDestination();
        AssertNoPrivateBytes(bytes: world.DestinationImage, what: "the destination's checkpoint");
    }
    // THE LAW: one crossing lands one projection whether or not its source restarts. The first world crosses straight
    // through; the second's source dies once its departure is durable, restarts from its checkpoint and its log, and
    // retries the commit from what it logged. Both destinations install the same projection — facts and records
    // included — and neither holds the document. The red leg is the commit that carries the owned document while the
    // log carries a projection without facts: the first try lands the document, the retry lands a projection that lost
    // the traveler's facts.
    [Fact]
    public void ACommitRetriedAfterItsSourceRestartsDeliversTheSameProjection() {
        using var straight = CrossingWorld.Build();
        var first = Owned();

        straight.Source.Server.Population.SetSeatProfile(profile: first, slot: 0);
        _ = straight.Cross();
        var landed = ArrivedAt(id: OwnerId, server: straight.Destination.Server);

        AssertArrivedAsProjection(arrived: landed, departed: first);

        using var dying = CrossingWorld.Build();
        var second = Owned();

        dying.Source.Server.Population.SetSeatProfile(profile: second, slot: 0);
        dying.SourceLog.CrashAfter = typeof(WorldCrossingRecord.Departure);
        _ = Assert.Throws<AuthorityCrashedException>(testCode: () => dying.Cross());
        using var restarted = dying.Restart(destinationDied: false, sourceDied: true);

        restarted.Step(ticks: PastEveryLease);
        var retried = ArrivedAt(id: OwnerId, server: restarted.Destination.Server);

        AssertArrivedAsProjection(arrived: retried, departed: second);
        Assert.Equal(expected: Carried(identity: landed), actual: Carried(identity: retried));
        foreach (var entry in dying.SourceLog.Encoded.Concat(second: dying.DestinationLog.Encoded)) {
            AssertNoPrivateBytes(bytes: entry, what: "a crossing log");
        }
    }
    // THE LAW: a fact a destination writes on its visitor lives on the visitor's travelling row and survives that
    // destination's restart through its checkpoint and its log. The traveler crosses to row-b, where the catalog
    // writes a fact onto it; row-b checkpoints, offers the traveler back to row-a, and dies once its departure is
    // durable. Restarted from its image and its log, row-b retries the commit, and the identity that lands on row-a
    // carries the fact row-b wrote. The red leg is a checkpoint leaf that drops facts: the retried commit lands an
    // identity without the fact.
    [Fact]
    public void AFactADestinationWritesSurvivesThatDestinationsRestart() {
        using var world = CrossingWorld.Build();

        world.Source.Server.Population.SetSeatProfile(profile: Owned(), slot: 0);
        _ = world.Cross();
        var visitor = ArrivedAt(id: OwnerId, server: world.Destination.Server);

        Assert.True(
            condition: world.Destination.Server.Profiles.TrySetFact(changed: out _, identity: visitor, key: Name(value: "awayFact"), reason: out var reason, value: 5),
            userMessage: reason
        );
        world.CheckpointDestination();
        world.DestinationLog.CrashAfter = typeof(WorldCrossingRecord.Departure);
        _ = Assert.Throws<AuthorityCrashedException>(testCode: () => {
            _ = world.Host.EnqueueTransfer(
                actingPrincipal: Principal.Console,
                destination: WorldInstanceHost.TransferDestination.Existing(name: "row-a"),
                scope: WorldInstanceHost.TransferScope.Body,
                sourceInstance: "row-b",
                sourceSlot: SlotOf(id: OwnerId, server: world.Destination.Server)
            );
            world.Host.DrainPendingTransfers();
        });
        using var restarted = world.Restart(destinationDied: true, sourceDied: false);

        restarted.Step(ticks: PastEveryLease);
        var home = ArrivedAt(id: OwnerId, server: restarted.Source.Server);

        Assert.Equal(expected: 5L, actual: Fact(identity: home, key: "awayFact"));
        Assert.Equal(expected: 3L, actual: Fact(identity: home, key: "homeFact"));
    }
    // THE LAW: a destination never saves a visitor into its own catalog. A fact on a visitor's travelling row is
    // written and kept on that row; a foreign identity carrying a document is refused by name and nothing reaches the
    // catalog's directory; the catalog's own identity saves as before (the control). The red leg saves the foreign
    // document into the catalog and refuses the visitor's fact for want of a document.
    [Fact]
    public void ACatalogSavesOnlyTheIdentitiesItOwns() {
        using var directory = new TemporaryDirectory(prefix: "puck-privacy-catalog-");
        var defaults = Fixtures.BuildDocument().PlayerDefaults;
        var catalog = new WorldOwnedWorlds(
            directory: directory.RootPath,
            machineId: Guid.NewGuid(),
            template: Fixtures.BuildDocument()
        );
        var foreign = Owned();
        var visitor = WorldIdentity.FromProjection(defaults: defaults, projection: foreign.Project());

        Assert.False(condition: catalog.TrySetFact(changed: out _, identity: foreign, key: Name(value: "awayFact"), reason: out var reason, value: 5));
        Assert.Contains(actualString: reason, expectedSubstring: "not owned by this catalog");
        Assert.True(
            condition: catalog.TrySetFact(changed: out var changed, identity: visitor, key: Name(value: "awayFact"), reason: out reason, value: 5),
            userMessage: reason
        );
        Assert.True(condition: changed);
        Assert.Equal(expected: 5L, actual: Fact(identity: visitor, key: "awayFact"));
        Assert.DoesNotContain(
            collection: Directory.GetFiles(path: directory.RootPath),
            filter: static path => File.ReadAllText(path: path).Contains(comparisonType: StringComparison.Ordinal, value: OwnerId)
        );

        var own = catalog.BootProfile;

        Assert.True(condition: catalog.TrySetFact(changed: out _, identity: own, key: Name(value: "ownFact"), reason: out reason, value: 1), userMessage: reason);
        Assert.Contains(
            collection: Directory.GetFiles(path: directory.RootPath),
            filter: static path => File.ReadAllText(path: path).Contains(comparisonType: StringComparison.Ordinal, value: "ownFact")
        );
    }
    // THE LAW: a seat coming home rebinds to the identity it left with, and adopts the facts and records it carries and
    // nothing else. Seated on row-a with an identity row-a's catalog owns, the traveler crosses to row-b, where its
    // visitor identity gains a fact and a record value and is renamed, recolored, re-rated, and stripped of its panel
    // and bindings. Back on row-a its seat holds the very identity it left with: the fact and the record value are
    // adopted and saved in row-a's catalog, the name, color, rate, panel and bindings are as they were, and row-b's
    // catalog never saved it. The red leg hands one live identity object across both crossings, so every change row-b
    // made lands on the owner.
    [Fact]
    public void ASeatComingHomeAdoptsItsFactsAndRecordsAndNothingElse() {
        using var world = CrossingWorld.Build();
        var catalog = world.Source.Server.Profiles;

        Assert.True(condition: catalog.ReplaceFromSync(document: OwnedDocument(), reason: out var reason), userMessage: reason);
        var owned = Owned(identity: catalog.FindById(id: OwnerId));
        var panel = owned.Hud;
        var bindings = owned.Bindings;
        var moveSpeed = owned.FixedMoveSpeed;

        Assert.NotNull(@object: panel);
        world.Source.Server.Population.SetSeatProfile(profile: owned, slot: 0);
        _ = world.Cross();
        var visitor = ArrivedAt(id: OwnerId, server: world.Destination.Server);

        Assert.True(
            condition: world.Destination.Server.Profiles.TrySetFact(changed: out _, identity: visitor, key: Name(value: "awayFact"), reason: out reason, value: 5),
            userMessage: reason
        );
        Assert.True(condition: visitor.TryWriteRecord(record: Name(value: "stats"), field: Name(value: "score"), value: CellValue.Int(value: 77), reason: out reason), userMessage: reason);
        visitor.SetIdentity(colorHex: "#000000", name: "Renamed");
        visitor.SetMoveSpeed(value: 9f);
        visitor.Hud = null;
        visitor.Bindings = null;
        _ = world.Host.EnqueueTransfer(
            actingPrincipal: Principal.Console,
            destination: WorldInstanceHost.TransferDestination.Existing(name: "row-a"),
            scope: WorldInstanceHost.TransferScope.Body,
            sourceInstance: "row-b",
            sourceSlot: SlotOf(id: OwnerId, server: world.Destination.Server)
        );
        world.Host.DrainPendingTransfers();
        var home = ArrivedAt(id: OwnerId, server: world.Source.Server);

        Assert.Same(actual: home, expected: owned);
        Assert.Equal(expected: "Owner", actual: home.Name);
        Assert.Equal(expected: "#3366cc", actual: home.ColorHex);
        Assert.Equal(expected: moveSpeed, actual: home.FixedMoveSpeed);
        Assert.Same(expected: panel, actual: home.Hud);
        Assert.Same(expected: bindings, actual: home.Bindings);
        Assert.Equal(expected: 5L, actual: Fact(identity: home, key: "awayFact"));
        Assert.Equal(expected: 3L, actual: Fact(identity: home, key: "homeFact"));
        Assert.Equal(expected: "77", actual: Record(field: "score", identity: home));
        Assert.Equal(expected: "champion", actual: Record(field: "badge", identity: home));
        Assert.Contains(
            collection: Directory.GetFiles(path: catalog.FilePath),
            filter: static path => File.ReadAllText(path: path).Contains(comparisonType: StringComparison.Ordinal, value: "awayFact")
        );
        Assert.DoesNotContain(
            collection: Directory.GetFiles(path: world.Destination.Server.Profiles.FilePath),
            filter: static path => File.ReadAllText(path: path).Contains(comparisonType: StringComparison.Ordinal, value: OwnerId)
        );
    }
}
