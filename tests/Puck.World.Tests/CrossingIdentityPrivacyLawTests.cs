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
    internal const string OwnerId = "privacy-owner";
    private const string PrivatePanel = "private-hud-panel";
    private const string PrivatePayload = "private-payload";
    private const string PrivateRow = "private-secret-row";
    // Steps long enough for any destination lease taken during a crossing to expire.
    private const int PastEveryLease = 90;

    internal static readonly string[] Markers = [PrivateRow, PrivatePayload, PrivatePanel];

    private static CellName Name(string value) => CellName.Parse(candidate: value);

    internal static WorldDefinition OwnedDocument() {
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
    internal static WorldIdentity Owned(WorldIdentity? identity = null) {
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
    // THE LAW: a home arrival adopts nothing until it stands. The traveler comes home carrying a fact and a record value
    // its visitor identity gained abroad, and the arrival record is refused or uncertain: the owned identity, its
    // document, the catalog's revision and every catalog file are as they were. The red leg adopts and saves while the
    // traveler lands, ahead of the record, so the rollback leaves the carried values in the owned identity and on disk.
    [InlineData(WorldCrossingDurability.Refused)]
    [InlineData(WorldCrossingDurability.Uncertain)]
    [Theory]
    public void AHomeArrivalThatIsNotDurableDoesNotAdoptOrSave(WorldCrossingDurability durability) {
        using var world = CrossingWorld.Build();
        var catalog = world.Source.Server.Profiles;

        Assert.True(condition: catalog.ReplaceFromSync(document: OwnedDocument(), reason: out var reason), userMessage: reason);
        var owned = Owned(identity: catalog.FindById(id: OwnerId));

        Assert.True(condition: catalog.TrySave(identity: owned, reason: out reason), userMessage: reason);
        world.Source.Server.Population.SetSeatProfile(profile: owned, slot: 0);
        _ = world.Cross();
        var visitor = ArrivedAt(id: OwnerId, server: world.Destination.Server);

        Assert.True(condition: visitor.TrySetFact(key: Name(value: "awayFact"), value: 5, changed: out _, reason: out reason), userMessage: reason);
        Assert.True(condition: visitor.TryWriteRecord(record: Name(value: "stats"), field: Name(value: "score"), value: CellValue.Int(value: 77), reason: out reason), userMessage: reason);
        var before = WorldDefinitionSerialization.Serialize(definition: owned.Document!);
        var files = Directory.GetFiles(path: catalog.FilePath).ToDictionary(keySelector: static path => path, elementSelector: static path => File.ReadAllBytes(path: path));
        var revision = catalog.Revision;

        world.SourceLog.LoseAnswerOn = typeof(WorldCrossingRecord.Arrival);
        world.SourceLog.LostAnswerLands = false;
        world.SourceLog.LostAnswer = durability;

        _ = world.Host.EnqueueTransfer(
            actingPrincipal: Principal.Console,
            destination: WorldInstanceHost.TransferDestination.Existing(name: "row-a"),
            scope: WorldInstanceHost.TransferScope.Body,
            sourceInstance: "row-b",
            sourceSlot: SlotOf(id: OwnerId, server: world.Destination.Server)
        );
        world.Host.DrainPendingTransfers();

        Assert.Null(value: Fact(identity: owned, key: "awayFact"));
        Assert.Equal("0", Record(field: "score", identity: owned));
        Assert.Equal(before, WorldDefinitionSerialization.Serialize(definition: owned.Document!));
        Assert.Equal(revision, catalog.Revision);
        foreach (var (path, bytes) in files) {
            Assert.Equal(bytes, File.ReadAllBytes(path: path));
        }
    }
    // THE LAW: an identity that has written no fact still carries its authored facts row name and capacity, so its
    // first fact abroad meets the bound it meets at home. The red leg projects no row, and the visitor writes under the
    // default name and capacity, admitting a second fact its owner refuses.
    [Fact]
    public void AFirstFactAbroadUsesTheOwnersAuthoredNameAndCapacity() {
        var defaults = Fixtures.BuildDocument().PlayerDefaults;
        var document = OwnedDocument();
        var owned = new WorldIdentity(defaults: defaults, document: document with {
            Identity = document.Identity! with { Facts = new WorldIdentityFacts(State: Name(value: "small-facts"), Capacity: 1) },
        });

        Assert.Null(@object: owned.Facts);
        var writer = new Puck.Networking.WireWriter();

        WorldIdentityProjectionWire.Write(writer: writer, projection: owned.Project());
        var reader = new Puck.Networking.WireReader(bytes: writer.WrittenSpan);
        var projection = WorldIdentityProjectionWire.Read(reader: ref reader);

        Assert.True(condition: reader.TryFinish(failure: out var failure), userMessage: failure.ToString());
        var visitor = WorldIdentity.FromProjection(defaults: defaults, projection: projection);

        Assert.Equal(owned.FactsDefinition, visitor.FactsDefinition);
        Assert.True(condition: visitor.TrySetFact(key: Name(value: "first"), value: 1, changed: out _, reason: out var reason), userMessage: reason);
        Assert.Equal(Name(value: "small-facts"), visitor.Facts!.Name);
        Assert.False(condition: visitor.TrySetFact(key: Name(value: "second"), value: 2, changed: out var changed, reason: out reason));
        Assert.False(condition: changed);
        Assert.Contains(actualString: reason, expectedSubstring: "1 of 1");
        Assert.Single(collection: visitor.Facts.Cells!);
    }
    // THE LAW: a reservation on the federation wire has no admission field, so a peer cannot ask for a local seat and
    // the home adoption that comes with one. A colocated request and a peer request encode to the same bytes, the
    // traveler count follows the all-or-nothing flag directly, and the decoded reservation is a peer admission. The red
    // leg writes an admission flag between them, and the count read at its place is not the traveler count.
    [Fact]
    public void AReservationOnTheWireCarriesNoAdmissionField() {
        var address = new WorldEntityAddress(Authority: "origin", Generation: 1, Index: 0);
        var colocated = new WorldTransferReservationRequest(
            TransferId: 1, SourceAuthority: "origin", SourceRateHz: 240, SourceTick: 0,
            DeadlineSourceTick: 60, Border: "", BorderCapacity: null, PartyAllOrNothing: true,
            PeerAdmission: false, Members: [new WorldTransferReservationMember(
                Principal: Principal.Console, PreferredSlot: 0, Identity: Owned().Project(),
                Source: IntentSource.Live, BodyColor: default, CatalogRig: 0,
                Mobility: new WorldMobilityIdentity(DepartedFrom: address, Epoch: 0, Incarnation: address)
            )]
        );
        var encoded = WorldFederationCodec.EncodeReservation(request: colocated);

        Assert.Equal(expected: WorldFederationCodec.EncodeReservation(request: colocated with { PeerAdmission = true }), actual: encoded);
        var reader = new Puck.Networking.WireReader(bytes: encoded);

        Assert.Equal(expected: colocated.TransferId, actual: reader.ReadUInt64());
        Assert.Equal(expected: colocated.SourceAuthority, actual: reader.ReadRequiredString(field: "source authority"));
        Assert.Equal(expected: colocated.SourceRateHz, actual: reader.ReadInt32());
        Assert.Equal(expected: colocated.SourceTick, actual: reader.ReadUInt64());
        Assert.Equal(expected: colocated.DeadlineSourceTick, actual: reader.ReadUInt64());
        Assert.Equal(expected: colocated.Border, actual: reader.ReadString(field: "border"));
        Assert.False(condition: reader.ReadBoolean());
        Assert.True(condition: reader.ReadBoolean());
        Assert.Equal(expected: colocated.Members.Count, actual: reader.ReadInt32());
        Assert.True(condition: WorldFederationCodec.TryDecodeReservation(body: encoded, failure: out var failure, request: out var decoded), userMessage: failure.ToString());
        Assert.True(condition: decoded!.PeerAdmission);
    }

    // Records, on the fixture's own server, a colocated home arrival at seat 0 whose projection carries `replayFact` at
    // `carried`, and returns the stopped tape. The live commit adopts the fact into the catalog's identity.
    private static WorldReplayTape RecordHomeArrival(WorldFixture fixture, string directory, long carried) {
        var server = fixture.Server;
        var traveller = WorldIdentity.FromProjection(defaults: server.Definition.PlayerDefaults, projection: server.Profiles.BootProfile.Project());

        Assert.True(condition: traveller.TrySetFact(changed: out _, key: Name(value: "replayFact"), reason: out var reason, value: carried), userMessage: reason);
        return RecordHomeArrival(carried: traveller.Project(), directory: directory, fixture: fixture);
    }
    // Records, on the fixture's own server, `before` ticks, then a colocated home arrival at seat 0 carrying `carried`
    // and the tick it lands on, then `after` more ticks, and returns the stopped tape.
    private static WorldReplayTape RecordHomeArrival(WorldFixture fixture, string directory, WorldIdentityProjection carried, int before = 0, int after = 0) {
        var server = fixture.Server;
        var owned = server.Profiles.BootProfile;
        var tape = Tape(directory: directory, server: server);
        var reason = string.Empty;

        Assert.True(condition: tape.TryBeginRecording(name: "arrivals", refusal: out var refusal), userMessage: refusal);
        for (var tick = 0; (tick < before); tick++) {
            fixture.Step();
            tape.NoteTick();
        }
        var address = new WorldEntityAddress(Authority: server.AuthorityIdentity, Generation: 1, Index: 0);
        var request = new WorldTransferReservationRequest(
            TransferId: 1, SourceAuthority: "away", SourceRateHz: 240, SourceTick: 0,
            DeadlineSourceTick: 60, Border: "", BorderCapacity: null, PartyAllOrNothing: true,
            PeerAdmission: false, Members: [new WorldTransferReservationMember(
                Principal: Principal.Console, PreferredSlot: 0, Identity: carried,
                Source: IntentSource.Live, BodyColor: owned.Color, CatalogRig: 0,
                Mobility: new WorldMobilityIdentity(DepartedFrom: address, Epoch: 1, Incarnation: address)
            )]
        );

        Assert.True(condition: server.ReserveTransfer(request: request).Accepted);
        var member = new WorldTransferCommitMember(Profile: carried, HasMappedArrival: false,
            BodyMotionProgramName: "", Position: default, YawRadians: default, PlanarVelocity: default, VerticalVelocity: default);

        Assert.Equal(WorldTransferStatus.Committed, server.CommitTransfer(sourceAuthority: request.SourceAuthority,
            transferId: request.TransferId, members: [member], reason: out reason));
        for (var tick = 0; (tick <= after); tick++) {
            fixture.Step();
            tape.NoteTick();
        }
        _ = tape.StopRecording();
        return tape;
    }
    private static (byte[] Document, long Revision, Dictionary<string, byte[]> Files) Saved(WorldOwnedWorlds catalog, WorldIdentity owned) => (
        WorldDefinitionSerialization.Serialize(definition: owned.Document!),
        catalog.Revision,
        Directory.GetFiles(path: catalog.FilePath).ToDictionary(keySelector: static path => path, elementSelector: static path => File.ReadAllBytes(path: path))
    );
    private static void AssertUnchanged((byte[] Document, long Revision, Dictionary<string, byte[]> Files) before, WorldOwnedWorlds catalog, WorldIdentity owned) {
        Assert.Equal(expected: before.Document, actual: WorldDefinitionSerialization.Serialize(definition: owned.Document!));
        Assert.Equal(expected: before.Revision, actual: catalog.Revision);
        Assert.Equal(expected: before.Files.Keys.Order(), actual: Directory.GetFiles(path: catalog.FilePath).Order());
        foreach (var (path, bytes) in before.Files) {
            Assert.Equal(expected: bytes, actual: File.ReadAllBytes(path: path));
        }
    }

    // THE LAW: verifying a tape changes nothing the live session owns. A tape records a home arrival carrying a fact at
    // 1; the owner then writes it to 2; verifying the tape matches, and the live identity, its document, the catalog's
    // revision and its files still hold 2. The red leg re-drives against the live catalog and adopts into the identity
    // it finds there, saving 1.
    [Fact]
    public void VerifyingAHomeArrivalDoesNotRewindTheLiveCatalog() {
        using var directory = new TemporaryDirectory(prefix: "puck-privacy-replay-");
        using var fixture = Fixtures.FreshServer();
        var catalog = fixture.Server.Profiles;
        var owned = catalog.BootProfile;
        var tape = RecordHomeArrival(carried: 1, directory: directory.RootPath, fixture: fixture);

        Assert.True(condition: catalog.TrySetFact(changed: out _, identity: owned, key: Name(value: "replayFact"), reason: out var reason, value: 2), userMessage: reason);
        var before = Saved(catalog: catalog, owned: owned);

        var verdict = tape.Verify(name: "arrivals");

        Assert.Equal(-1, verdict.Primary.DivergedAt);
        Assert.Equal(2L, Fact(identity: owned, key: "replayFact"));
        AssertUnchanged(before: before, catalog: catalog, owned: owned);
    }
    // THE LAW: a re-drive of a recorded home arrival saves nothing, whichever catalog it runs against. A fresh server
    // whose catalog owns the same identity re-lands a taped arrival through the door every re-drive takes: the seat
    // binds the taped projection, carried fact included, in a detached identity, and the catalog's identity, document,
    // revision and files are untouched. The red leg adopts into the catalog's identity and saves it.
    [Fact]
    public void ARelandedHomeArrivalAdoptsIntoADetachedCopyAndSavesNothing() {
        using var directory = new TemporaryDirectory(prefix: "puck-privacy-reland-");
        using var recorded = Fixtures.FreshServer();
        var tape = RecordHomeArrival(carried: 5, directory: directory.RootPath, fixture: recorded);
        WorldReplaySnapshot snapshot;

        using (var stream = File.OpenRead(path: tape.PathFor(name: "arrivals"))) {
            snapshot = WorldReplaySnapshot.Read(stream: stream);
        }
        var arrival = Assert.Single(collection: snapshot.Ticks.SelectMany(selector: static tick => tick.Authority).OfType<WorldReplayEntry.Arrival>());

        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecodeCrossingArrival(arrival: out var decoded, bytes: arrival.Encoded, reason: out var reason), userMessage: reason);
        using var fixture = Fixtures.FreshServer();
        var server = fixture.Server;
        var catalog = server.Profiles;
        var owned = catalog.BootProfile;
        var before = Saved(catalog: catalog, owned: owned);
        var relandReason = string.Empty;

        Assert.True(
            condition: server.ExecuteAuthorityOperation(operation: () => server.TransferEscrow.TryReland(arrival: decoded!, reason: out relandReason, recorded: arrival.Outcome)),
            userMessage: relandReason
        );

        Assert.Null(@object: Fact(identity: owned, key: "replayFact"));
        AssertUnchanged(before: before, catalog: catalog, owned: owned);
        var seated = server.Population.EntryBody(index: 0)!.Profile!;

        Assert.NotSame(actual: seated, expected: owned);
        Assert.Null(@object: seated.Document);
        Assert.Equal(expected: owned.Name, actual: seated.Name);
        Assert.Equal(5L, Fact(identity: seated, key: "replayFact"));
    }
    // THE LAW: a re-driven home arrival reports where the owned identity has moved from the taped projection, as a
    // pinned seat's drift is reported. After the recording the owner writes a fact the tape never carried; verifying the
    // tape still matches and narrates that fact on the catalog's hub, while the fact the tape carried is not drift. The
    // red leg reports nothing.
    [Fact]
    public void VerifyingAHomeArrivalReportsAFactWrittenSinceTheRecording() {
        using var directory = new TemporaryDirectory(prefix: "puck-privacy-drift-");
        var hub = new WorldOutputHub();
        var sink = new RecordingNarrationSink();

        using var attached = hub.AttachNarrationSink(sink: sink);
        using var fixture = Fixtures.FreshServer(catalogNarration: hub);
        var catalog = fixture.Server.Profiles;
        var tape = RecordHomeArrival(carried: 1, directory: directory.RootPath, fixture: fixture);

        Assert.True(condition: catalog.TrySetFact(changed: out _, identity: catalog.BootProfile, key: Name(value: "laterFact"), reason: out var reason, value: 7), userMessage: reason);

        var verdict = tape.Verify(name: "arrivals");

        Assert.Equal(-1, verdict.Primary.DivergedAt);
        Assert.Contains(collection: sink.Narrations, filter: static narration => ((narration.Channel == "replay.profile") && narration.Text.Contains(comparisonType: StringComparison.Ordinal, value: "fact 'laterFact' drifted since record-start")));
        Assert.DoesNotContain(collection: sink.Narrations, filter: static narration => narration.Text.Contains(comparisonType: StringComparison.Ordinal, value: "fact 'replayFact'"));
    }
    // THE LAW: a replay's copy of the catalog writes nothing back. A fact written on the copy's identity lands on the
    // copy alone, and the source catalog's identity, document, revision and files are untouched. The red leg lets the
    // copy save into the directory it shares with its source.
    [Fact]
    public void AReplayCopyOfTheCatalogWritesNothingBack() {
        using var fixture = Fixtures.FreshServer();
        var catalog = fixture.Server.Profiles;
        var owned = catalog.BootProfile;
        var before = Saved(catalog: catalog, owned: owned);
        var copy = catalog.CreateReplayCopy();
        var copied = copy.FindById(id: owned.Id)!;

        Assert.NotSame(actual: copied, expected: owned);
        Assert.True(condition: copy.TrySetFact(changed: out _, identity: copied, key: Name(value: "copyFact"), reason: out var reason, value: 1), userMessage: reason);
        Assert.Equal(1L, Fact(identity: copied, key: "copyFact"));
        Assert.Null(@object: Fact(identity: owned, key: "copyFact"));
        AssertUnchanged(before: before, catalog: catalog, owned: owned);
    }
    // THE LAW: a partial home adoption replays its live hash. The owned identity declares a facts capacity of 1 and
    // holds homeFact=3; its traveler comes home carrying a capacity of 2 with homeFact=3 and replayFact=5. The live
    // adoption keeps homeFact, refuses replayFact and binds the owned identity, and the tape records that bound
    // projection as the arrival's outcome, so the re-drive binds it, detached, and the live and replayed authoritative
    // hashes agree on every tick: the tick before the arrival, the arrival's own, and the ticks after it. The red leg
    // tapes only the incoming projection, and the re-drive binds replayFact the live seat never held.
    [Fact]
    public void APartialHomeAdoptionReplaysItsLiveHash() {
        using var directory = new TemporaryDirectory(prefix: "puck-privacy-partial-");
        using var fixture = Fixtures.FreshServer();
        var catalog = fixture.Server.Profiles;
        var owned = catalog.BootProfile;
        var factsRow = owned.FactsDefinition.State;

        owned.ReplaceDocument(document: owned.Document! with {
            Identity = owned.Document!.Identity! with { Facts = new WorldIdentityFacts(Capacity: 1, State: factsRow) },
        });
        Assert.True(condition: catalog.TrySetFact(changed: out _, identity: owned, key: Name(value: "homeFact"), reason: out var reason, value: 3), userMessage: reason);
        Assert.True(condition: catalog.TrySave(identity: owned, reason: out reason), userMessage: reason);
        var carried = owned.Project() with {
            Facts = new WorldStateRow(Name: factsRow, Kind: CellKind.Int, Capacity: 2, Cells: [
                new StateCell(Key: Name(value: "homeFact"), Value: CellValue.Int(value: 3)),
                new StateCell(Key: Name(value: "replayFact"), Value: CellValue.Int(value: 5)),
            ]),
        };
        var tape = RecordHomeArrival(after: 2, before: 1, carried: carried, directory: directory.RootPath, fixture: fixture);

        Assert.Same(expected: owned, actual: fixture.Server.Population.EntryBody(index: 0)!.Profile);
        Assert.Equal(3L, Fact(identity: owned, key: "homeFact"));
        Assert.Null(@object: Fact(identity: owned, key: "replayFact"));

        WorldReplaySnapshot snapshot;

        using (var stream = File.OpenRead(path: tape.PathFor(name: "arrivals"))) {
            snapshot = WorldReplaySnapshot.Read(stream: stream);
        }
        Assert.Equal(expected: 4, actual: snapshot.Ticks.Count);
        Assert.Single(collection: snapshot.Ticks[1].Authority.OfType<WorldReplayEntry.Arrival>());

        var verdict = tape.Verify(name: "arrivals");

        Assert.True(condition: verdict.Primary.Match, userMessage: verdict.Primary.Describe());
        Assert.Equal(expected: 4, actual: verdict.Primary.Ticks);
    }
    // Reducing the owned facts capacity can prevent a taped fact from being adopted. No surplus live key is needed
    // for that drift, and every missing fact must be reported in ordinal key order through the catalog's hub.
    [Fact]
    public void VerifyingAHomeArrivalReportsFactsTheOwnedCapacityRefuses() {
        using var directory = new TemporaryDirectory(prefix: "puck-privacy-refused-drift-");
        var hub = new WorldOutputHub();
        var sink = new RecordingNarrationSink();

        using var attached = hub.AttachNarrationSink(sink: sink);
        using var fixture = Fixtures.FreshServer(catalogNarration: hub);
        var catalog = fixture.Server.Profiles;
        var owned = catalog.BootProfile;

        Assert.True(condition: catalog.TrySetFact(identity: owned, key: Name(value: "zFact"), value: 6, changed: out _, reason: out var reason), userMessage: reason);
        Assert.True(condition: catalog.TrySetFact(identity: owned, key: Name(value: "homeFact"), value: 3, changed: out _, reason: out reason), userMessage: reason);
        var tape = RecordHomeArrival(carried: 1, directory: directory.RootPath, fixture: fixture);
        var facts = owned.Facts!;

        owned.WriteState(row: facts with { Capacity = 1, Cells = [.. facts.Cells!.Where(predicate: cell => (cell.Key == Name(value: "homeFact")))] });
        owned.ReplaceDocument(document: owned.Document! with {
            Identity = owned.Document!.Identity! with { Facts = new WorldIdentityFacts(State: facts.Name, Capacity: 1) },
        });
        Assert.True(condition: catalog.TrySave(identity: owned, reason: out reason), userMessage: reason);
        var before = sink.Narrations.Count;

        _ = tape.Verify(name: "arrivals");
        var drift = sink.Narrations.Skip(count: before)
            .Where(predicate: static narration => ((narration.Channel == "replay.profile") && narration.Text.Contains(comparisonType: StringComparison.Ordinal, value: " fact '")))
            .Select(selector: static narration => narration.Text).ToArray();

        Assert.Equal(expected: 2, actual: drift.Length);
        Assert.Contains(expectedSubstring: "fact 'replayFact'", actualString: drift[0]);
        Assert.Contains(expectedSubstring: "taped 1, live none", actualString: drift[0]);
        Assert.Contains(expectedSubstring: "fact 'zFact'", actualString: drift[1]);
        Assert.Contains(expectedSubstring: "taped 6, live none", actualString: drift[1]);
    }
    // THE LAW: a restored checkpoint rebinds a seat this authority owns to its restored catalog identity, and only that
    // seat. A fresh local seat and a seat that came home bind the catalog's identity, so a fact written after the
    // restore reaches the catalog and its saved document; a visitor carrying the owner's id keeps its travelling row.
    // The red leg leaves every seat on its projection, so the fact lands on a detached copy and the catalog never sees it.
    [InlineData("local")]
    [InlineData("home")]
    [InlineData("visitor")]
    [Theory]
    public void ACheckpointRebindsOnlyAHomeSeatsOwnedIdentity(string origin) {
        using var fixture = Fixtures.FreshServer();
        var server = fixture.Server;
        var catalog = server.Profiles;
        var owned = catalog.BootProfile;

        Assert.True(condition: catalog.TrySetFact(identity: owned, key: Name(value: "restoreFact"), value: 1, changed: out _, reason: out var reason), userMessage: reason);
        Assert.True(condition: server.ApplySession(request: new SessionRequest.Join(Principal: Principal.Seat(slot: 0), Slot: 0,
            IdentityName: owned.Name, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);
        if (origin != "local") {
            var address = new WorldEntityAddress(Authority: ((origin == "home") ? server.AuthorityIdentity : "away"), Index: 0, Generation: 1);

            server.Population.SetMobility(index: 0, mobility: new WorldMobilityIdentity(DepartedFrom: address, Epoch: 2, Incarnation: address));
        }
        if (origin == "visitor") {
            var visitor = WorldIdentity.FromProjection(defaults: server.Definition.PlayerDefaults, projection: owned.Project());

            Assert.True(condition: visitor.TrySetFact(key: Name(value: "restoreFact"), value: 2, changed: out _, reason: out reason), userMessage: reason);
            server.Population.SetSeatProfile(profile: visitor, slot: 0);
        }
        Assert.True(condition: server.TryCaptureCheckpoint(hostRow: WorldAuthorityHostRowCheckpoint.Empty, checkpoint: out var captured, reason: out reason), userMessage: reason);
        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(bytes: WorldAuthorityCheckpointCodec.Encode(checkpoint: captured!), checkpoint: out var decoded, reason: out reason), userMessage: reason);

        server.RestoreCheckpoint(checkpoint: decoded!);

        var profile = server.Population.EntryBody(index: 0)!.Profile!;
        var restoredOwner = catalog.FindById(id: owned.Id)!;

        if (origin == "visitor") {
            Assert.NotSame(actual: profile, expected: restoredOwner);
            Assert.Null(@object: profile.Document);
            Assert.Equal(2L, Fact(identity: profile, key: "restoreFact"));
        } else {
            Assert.Same(actual: profile, expected: restoredOwner);
        }
        Assert.True(condition: catalog.TrySetFact(identity: profile, key: Name(value: "restoreFact"), value: 3, changed: out _, reason: out reason), userMessage: reason);
        Assert.Equal(((origin == "visitor") ? 1L : 3L), Fact(identity: restoredOwner, key: "restoreFact"));
        var saved = Directory.GetFiles(path: catalog.FilePath).Select(selector: static path => WorldDefinitionSerialization.Deserialize(utf8Json: File.ReadAllBytes(path: path)))
            .Single(predicate: document => (document.Identity?.Id.ToString() == owned.Id));

        Assert.Equal(((origin == "visitor") ? 1L : 3L), Fact(identity: new WorldIdentity(document: saved, defaults: server.Definition.PlayerDefaults), key: "restoreFact"));
    }
    // THE LAW: a crossing whose source restarts after its durable departure and whose destination then aborts restores
    // the seat to the identity it left with, from the restored catalog. A fact written after the rollback reaches that
    // identity. The red leg restores the projection the commit carried, a detached copy the catalog never sees.
    [Fact]
    public void AnAbortedCrossingAfterASourceRestartRestoresItsOwnedIdentity() {
        using var world = CrossingWorld.Build();
        var catalog = world.Source.Server.Profiles;

        Assert.True(condition: catalog.ReplaceFromSync(document: OwnedDocument(), reason: out var reason), userMessage: reason);
        var owned = Owned(identity: catalog.FindById(id: OwnerId));

        Assert.True(condition: catalog.TrySave(identity: owned, reason: out reason), userMessage: reason);
        world.Source.Server.Population.SetSeatProfile(profile: owned, slot: 0);
        world.CheckpointSource();
        world.SourceLog.CrashAfter = typeof(WorldCrossingRecord.Departure);
        Assert.Throws<AuthorityCrashedException>(testCode: () => world.Cross());
        // The destination has reserved but has never seen a commit. Its explicit abort is an authoritative Missing.
        var departure = Assert.IsType<WorldCrossingRecord.Departure>(@object: Assert.Single(collection: world.SourceLog.Read()).Record);

        world.Destination.Server.AbortTransfer(sourceAuthority: world.Source.Server.AuthorityIdentity,
            transferId: departure.Transfer.TransferId);

        using var restarted = world.Restart(sourceDied: true, destinationDied: false);

        restarted.Step(ticks: PastEveryLease);

        var restoredCatalog = restarted.Source.Server.Profiles;
        var restoredOwner = restoredCatalog.FindById(id: OwnerId)!;
        var profile = ArrivedAt(id: OwnerId, server: restarted.Source.Server);

        Assert.Same(actual: profile, expected: restoredOwner);
        Assert.True(condition: restoredCatalog.TrySetFact(identity: profile, key: Name(value: "afterAbort"), value: 9, changed: out _, reason: out reason), userMessage: reason);
        Assert.Equal(9L, Fact(identity: restoredOwner, key: "afterAbort"));
        Assert.Equal("champion", Record(field: "badge", identity: restoredOwner));
    }
    // THE LAW: a crossing that aborts after its source restarts keeps the facts the seat wrote between the source's
    // checkpoint and its departure. They live only in the departure's logged projection, which is this authority's own
    // state, and the rollback adopts it into the restored identity, which saves it. The red leg reseats the restored
    // identity alone, and the fact is lost.
    [Fact]
    public void AFactWrittenAfterTheCheckpointSurvivesAnAbortedCrossing() {
        using var world = CrossingWorld.Build();
        var catalog = world.Source.Server.Profiles;

        Assert.True(condition: catalog.ReplaceFromSync(document: OwnedDocument(), reason: out var reason), userMessage: reason);
        var owned = Owned(identity: catalog.FindById(id: OwnerId));

        Assert.True(condition: catalog.TrySave(identity: owned, reason: out reason), userMessage: reason);
        world.Source.Server.Population.SetSeatProfile(profile: owned, slot: 0);
        world.CheckpointSource();
        Assert.True(condition: catalog.TrySetFact(changed: out _, identity: owned, key: Name(value: "beforeDeparture"), reason: out reason, value: 4), userMessage: reason);
        world.SourceLog.CrashAfter = typeof(WorldCrossingRecord.Departure);
        _ = Assert.Throws<AuthorityCrashedException>(testCode: () => world.Cross());
        var departure = Assert.IsType<WorldCrossingRecord.Departure>(@object: Assert.Single(collection: world.SourceLog.Read()).Record);

        world.Destination.Server.AbortTransfer(sourceAuthority: world.Source.Server.AuthorityIdentity, transferId: departure.Transfer.TransferId);
        using var restarted = world.Restart(destinationDied: false, sourceDied: true);

        restarted.Step(ticks: PastEveryLease);
        var restoredCatalog = restarted.Source.Server.Profiles;
        var restoredOwner = restoredCatalog.FindById(id: OwnerId)!;

        Assert.Same(expected: restoredOwner, actual: ArrivedAt(id: OwnerId, server: restarted.Source.Server));
        Assert.Equal(4L, Fact(identity: restoredOwner, key: "beforeDeparture"));
        Assert.Equal(3L, Fact(identity: restoredOwner, key: "homeFact"));
        var saved = Directory.GetFiles(path: restoredCatalog.FilePath)
            .Select(selector: static path => WorldDefinitionSerialization.Deserialize(utf8Json: File.ReadAllBytes(path: path)))
            .Single(predicate: static document => (document.Identity?.Id.ToString() == OwnerId));

        Assert.Equal(4L, Fact(identity: new WorldIdentity(defaults: restarted.Source.Server.Definition.PlayerDefaults, document: saved), key: "beforeDeparture"));
    }
    // A recovered departure restores the owner's state before live writes resume. An abort must retain newer facts
    // and record fields, including when another checkpoint and restart intervene before the abort is resolved.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnAbortedCrossingKeepsIdentityWritesMadeAfterRecovery(bool restartAgain) {
        using var world = CrossingWorld.Build();
        var catalog = world.Source.Server.Profiles;

        Assert.True(condition: catalog.ReplaceFromSync(document: OwnedDocument(), reason: out var reason), userMessage: reason);
        var owned = Owned(identity: catalog.FindById(id: OwnerId));

        world.Source.Server.Population.SetSeatProfile(profile: owned, slot: 0);
        world.CheckpointSource();
        Assert.True(condition: owned.TryWriteRecord(record: Name(value: "stats"), field: Name(value: "score"), value: CellValue.Int(value: 4), reason: out reason), userMessage: reason);
        Assert.True(condition: catalog.TrySetFact(identity: owned, key: Name(value: "homeFact"), value: 4, changed: out _, reason: out reason), userMessage: reason);
        Assert.True(condition: catalog.TrySetFact(identity: owned, key: Name(value: "beforeDeparture"), value: 4, changed: out _, reason: out reason), userMessage: reason);
        world.SourceLog.CrashAfter = typeof(WorldCrossingRecord.Departure);
        _ = Assert.Throws<AuthorityCrashedException>(testCode: () => world.Cross());
        var departure = Assert.IsType<WorldCrossingRecord.Departure>(@object: Assert.Single(collection: world.SourceLog.Read()).Record);

        world.Destination.Server.AbortTransfer(sourceAuthority: world.Source.Server.AuthorityIdentity, transferId: departure.Transfer.TransferId);
        using var restarted = world.Restart(sourceDied: true, destinationDied: false);
        var restoredCatalog = restarted.Source.Server.Profiles;
        var restoredOwner = restoredCatalog.FindById(id: OwnerId)!;

        Assert.True(condition: restoredOwner.TryWriteRecord(record: Name(value: "stats"), field: Name(value: "score"), value: CellValue.Int(value: 9), reason: out reason), userMessage: reason);
        Assert.True(condition: restoredCatalog.TrySetFact(identity: restoredOwner, key: Name(value: "homeFact"), value: 9, changed: out _, reason: out reason), userMessage: reason);
        restarted.CheckpointSource();
        using var secondRestart = (restartAgain ? restarted.Restart(sourceDied: true, destinationDied: false) : null);
        var resolved = (secondRestart ?? restarted);

        resolved.Step(ticks: PastEveryLease);
        var finalCatalog = resolved.Source.Server.Profiles;
        var finalOwner = finalCatalog.FindById(id: OwnerId)!;

        Assert.Same(expected: finalOwner, actual: ArrivedAt(id: OwnerId, server: resolved.Source.Server));
        Assert.Equal(9L, Fact(identity: finalOwner, key: "homeFact"));
        Assert.Equal("9", Record(field: "score", identity: finalOwner));
        Assert.Equal(4L, Fact(identity: finalOwner, key: "beforeDeparture"));
        Assert.Empty(collection: resolved.Host.CaptureRow(row: resolved.Source.Instance).InDoubtTransfers);
        var saved = Directory.GetFiles(path: finalCatalog.FilePath)
            .Select(selector: static path => WorldDefinitionSerialization.Deserialize(utf8Json: File.ReadAllBytes(path: path)))
            .Single(predicate: static document => (document.Identity?.Id.ToString() == OwnerId));
        var savedOwner = new WorldIdentity(defaults: resolved.Source.Server.Definition.PlayerDefaults, document: saved);

        Assert.Equal(9L, Fact(identity: savedOwner, key: "homeFact"));
        Assert.Equal("9", Record(field: "score", identity: savedOwner));
        Assert.Equal(4L, Fact(identity: savedOwner, key: "beforeDeparture"));
    }
    // Restoring a replay's boot checkpoint must keep the pinned seat detached even when its name is also an owned id.
    // Otherwise the ordinary home-seat rebind replaces the pinned rate with the owner's later rate and saves replay facts.
    [Fact]
    public void ALiveReplayBootKeepsPinnedIdentityRatesOutOfTheCatalog() {
        using var directory = new TemporaryDirectory(prefix: "puck-privacy-drive-");
        using var fixture = Fixtures.FreshServer();
        var server = fixture.Server;
        var catalog = server.Profiles;
        var owned = catalog.BootProfile;

        owned.SetMoveSpeed(value: 3f);
        Assert.True(condition: catalog.TrySave(identity: owned, reason: out var reason), userMessage: reason);
        Assert.True(condition: server.ApplySession(request: new SessionRequest.Join(Principal: Principal.Seat(slot: 0), Slot: 0,
            IdentityName: owned.Name, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);
        var tape = Tape(server: server, directory: directory.RootPath);

        Assert.True(condition: tape.TryBeginRecording(name: "arrivals", refusal: out var refusal), userMessage: refusal);
        fixture.Step();
        tape.NoteTick();
        _ = tape.StopRecording();
        var pinnedRate = owned.FixedMoveSpeed;

        owned.SetMoveSpeed(value: 9f);
        Assert.True(condition: catalog.TrySave(identity: owned, reason: out reason), userMessage: reason);
        var before = Saved(catalog: catalog, owned: owned);

        Assert.True(condition: tape.TryBeginDrive(documentPath: null, forkName: null, name: "arrivals", refusal: out refusal, toTick: null), userMessage: refusal);
        var driven = server.Population.EntryBody(index: 0)!.Profile!;

        Assert.Equal(expected: pinnedRate, actual: driven.FixedMoveSpeed);
        Assert.Null(@object: driven.Document);
        Assert.True(condition: catalog.TrySetFact(identity: driven, key: Name(value: "drivenFact"), value: 5, changed: out _, reason: out reason), userMessage: reason);
        Assert.Null(@object: Fact(identity: catalog.FindById(id: owned.Id)!, key: "drivenFact"));
        AssertUnchanged(before: before, catalog: catalog, owned: owned);
        _ = tape.CancelDrive();
    }
    // Once an arrival is durable, an identity-file failure cannot interrupt its seat rebind or its arrival tap.
    // A directory at the catalog file's path forces an actual storage failure without relying on file permissions.
    [Fact]
    public void ADurableHomeArrivalKeepsItsOwnedBindingWhenTheCatalogCannotSave() {
        using var directory = new TemporaryDirectory(prefix: "puck-privacy-save-refusal-");
        using var fixture = Fixtures.FreshServer();
        var server = fixture.Server;
        var catalog = server.Profiles;
        var owned = catalog.BootProfile;
        var sink = new RecordingNarrationSink();
        var log = new CapturingCrossingLog();

        using var attached = server.AttachNarrationSink(sink: sink);

        server.InstallCrossingLog(log: log);
        var path = Path.Combine(path1: catalog.FilePath, path2: WorldDocumentName.For(id: SafeName.Parse(candidate: owned.Id)));

        File.Delete(path: path);
        _ = Directory.CreateDirectory(path: path);
        try {
            WorldReplayTape? tape = null;
            var failure = Xunit.Record.Exception(testCode: () => tape = RecordHomeArrival(carried: 5, directory: directory.RootPath, fixture: fixture));

            Assert.Null(@object: failure);
            Assert.Single(collection: log.Entries);
            Assert.Same(expected: owned, actual: server.Population.EntryBody(index: 0)!.Profile);
            Assert.Equal(5L, Fact(identity: owned, key: "replayFact"));
            Assert.Contains(collection: sink.Narrations, filter: static narration => narration.Text.Contains(comparisonType: StringComparison.Ordinal, value: "could not save"));
            using var stream = File.OpenRead(path: tape!.PathFor(name: "arrivals"));
            var recorded = WorldReplaySnapshot.Read(stream: stream);

            Assert.Single(collection: recorded.Ticks.SelectMany(selector: static tick => tick.Authority).OfType<WorldReplayEntry.Arrival>());
        } finally {
            Directory.Delete(path: path);
        }
        Assert.True(condition: catalog.TrySetFact(identity: owned, key: Name(value: "replayFact"), value: 6, changed: out _, reason: out var reason), userMessage: reason);
        var saved = new WorldIdentity(document: WorldDefinitionSerialization.Deserialize(utf8Json: File.ReadAllBytes(path: path)), defaults: server.Definition.PlayerDefaults);

        Assert.Equal(6L, Fact(identity: saved, key: "replayFact"));
    }
    // A save that has no refusal to return still reports a file it cannot write. The catalog's hub names the identity
    // whose save failed, so a console write that never reached disk is not silent; the red leg drops the refusal.
    [Fact]
    public void ACatalogSaveThatCannotWriteItsFileIsNarrated() {
        var hub = new WorldOutputHub();
        var sink = new RecordingNarrationSink();

        using var attached = hub.AttachNarrationSink(sink: sink);
        using var fixture = Fixtures.FreshServer(catalogNarration: hub);
        var catalog = fixture.Server.Profiles;
        var owned = catalog.BootProfile;
        var path = Path.Combine(path1: catalog.FilePath, path2: WorldDocumentName.For(id: SafeName.Parse(candidate: owned.Id)));

        File.Delete(path: path);
        _ = Directory.CreateDirectory(path: path);
        try {
            catalog.Save();
        } finally {
            Directory.Delete(path: path);
        }

        Assert.Contains(collection: sink.Narrations, filter: narration => (
            (narration.Channel == "identity") &&
            narration.Text.Contains(comparisonType: StringComparison.Ordinal, value: $"could not save identity '{owned.Id}'")
        ));
    }
    // THE LAW: two travelers of one arrival come home to seats 0 and 1 under the same owned identity, one carrying
    // score 1 and the other score 2. Live, both seats bind the one owned identity, so the second adoption is what both
    // hold; a rule then writes the score through seat 0 alone, which seat 1 observes because it holds the same
    // identity. The tape records each binding after every adoption, and the re-drive binds both seats to one detached
    // identity, so the live and replayed authoritative hashes agree on every tick: the arrival's own and the write's.
    // The red legs: a binding taped before the later adoption gives seat 0 score 1 on the re-drive, and two detached
    // identities leave seat 1 without the write.
    [Fact]
    public void TwoTravelersHomeUnderOneIdentityReplayTheirSharedBinding() => ProveSharedHomeBinding(separateArrivals: false);
    // The non-atomic party path lands one arrival per traveler. With an arrival-local detached map, the second
    // arrival leaves replay seat 0 at score 1 while live seat 0 reads 2, failing Primary.Match at that tick.
    [Fact]
    public void SeparateHomeArrivalsUnderOneIdentityReplayTheirSharedBinding() => ProveSharedHomeBinding(separateArrivals: true);

    private static void ProveSharedHomeBinding(bool separateArrivals) {
        const int LaneCapacity = 16;
        using var directory = new TemporaryDirectory(prefix: "puck-privacy-shared-");
        var definition = Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(World: [new WorldStateRow(
                Name: Name(value: WorldIdentityFactLane.RowName),
                Kind: CellKind.Int,
                Capacity: LaneCapacity
            )]),
            Rules = [new WorldRule(
                Name: Name(value: "write"),
                Effects: [new WorldEffect.SetIdentityFact(
                    Key: "0",
                    Fact: "score",
                    Value: 7m
                )],
                Gate: new ActionPredicate.CompareState(
                    State: RuleFacts.Tick,
                    Comparison: ExpressionOp.GreaterOrEqual,
                    Value: 4m
                )
            )],
        };
        using var fixture = Fixtures.FreshServer(definition: definition);
        var server = fixture.Server;
        var owned = server.Profiles.BootProfile;
        var carried = new WorldIdentityProjection[2];

        for (var index = 0; (index < carried.Length); index++) {
            var traveller = WorldIdentity.FromProjection(defaults: server.Definition.PlayerDefaults, projection: owned.Project());

            Assert.True(condition: traveller.TrySetFact(changed: out _, key: Name(value: "score"), reason: out var factReason, value: (index + 1)), userMessage: factReason);
            carried[index] = traveller.Project();
        }

        var tape = Tape(directory: directory.RootPath, server: server);

        Assert.True(condition: tape.TryBeginRecording(name: "arrivals", refusal: out var refusal), userMessage: refusal);
        fixture.Step();
        tape.NoteTick();

        var request = new WorldTransferReservationRequest(
            TransferId: 1, SourceAuthority: "away", SourceRateHz: 240, SourceTick: 0,
            DeadlineSourceTick: 60, Border: "", BorderCapacity: null, PartyAllOrNothing: true,
            PeerAdmission: false, Members: [.. carried.Select(selector: (projection, slot) => {
                var address = new WorldEntityAddress(Authority: server.AuthorityIdentity, Generation: 1, Index: slot);

                return new WorldTransferReservationMember(
                    Principal: Principal.Console, PreferredSlot: slot, Identity: projection,
                    Source: IntentSource.Live, BodyColor: owned.Color, CatalogRig: 0,
                    Mobility: new WorldMobilityIdentity(DepartedFrom: address, Epoch: 1, Incarnation: address)
                );
            })]
        );

        var arrivals = (separateArrivals
            ? request.Members.Select(selector: (member, index) => request with {
                TransferId = ((ulong)(index + 1)),
                PartyAllOrNothing = false,
                Members = [member],
            }).ToArray()
            : [request]);

        foreach (var arrival in arrivals) {
            Assert.True(condition: server.ReserveTransfer(request: arrival).Accepted);
            Assert.Equal(WorldTransferStatus.Committed, server.CommitTransfer(sourceAuthority: arrival.SourceAuthority,
                transferId: arrival.TransferId, reason: out _, members: [.. arrival.Members.Select(selector: static member => new WorldTransferCommitMember(
                    Profile: member.Identity, HasMappedArrival: false, BodyMotionProgramName: "", Position: default, YawRadians: default,
                    PlanarVelocity: default, VerticalVelocity: default))]));
            if (separateArrivals) {
                fixture.Step();
                tape.NoteTick();
            }
        }
        Assert.Same(expected: owned, actual: server.Population.EntryBody(index: 0)!.Profile);
        Assert.Same(expected: owned, actual: server.Population.EntryBody(index: 1)!.Profile);
        Assert.Equal(expected: 2L, actual: Fact(identity: owned, key: "score"));
        for (var tick = 0; (tick < 5); tick++) {
            fixture.Step();
            tape.NoteTick();
        }
        _ = tape.StopRecording();

        // Live: both seats hold the one identity, which holds the last adoption and then the write through seat 0.
        Assert.Same(expected: owned, actual: server.Population.EntryBody(index: 0)!.Profile);
        Assert.Same(expected: owned, actual: server.Population.EntryBody(index: 1)!.Profile);
        Assert.Equal(7L, Fact(identity: owned, key: "score"));

        var verdict = tape.Verify(name: "arrivals");

        Assert.True(condition: verdict.Primary.Match, userMessage: verdict.Primary.Describe());
        Assert.Equal(expected: -1, actual: verdict.Primary.DivergedAt);
        Assert.Equal(expected: (separateArrivals ? 8 : 6), actual: verdict.Primary.Ticks);
    }

    // Records one tick on a fresh server whose catalog owns the boot profile, seat 0 joined as that identity.
    private static (WorldReplayTape Tape, WorldIdentity Owned) RecordOwnedSeat(WorldFixture fixture, string directory) {
        var server = fixture.Server;
        var owned = server.Profiles.BootProfile;

        Assert.True(condition: server.ApplySession(request: new SessionRequest.Join(Principal: Principal.Seat(slot: 0), Slot: 0,
            IdentityName: owned.Name, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);
        var tape = Tape(directory: directory, server: server);

        Assert.True(condition: tape.TryBeginRecording(name: "arrivals", refusal: out var refusal), userMessage: refusal);
        fixture.Step();
        tape.NoteTick();
        _ = tape.StopRecording();
        return (tape, owned);
    }
    // Ends the drive `tape` holds the way the theory names: a cancel, or the one recorded tick stepped to its target.
    private static void EndDrive(WorldFixture fixture, WorldReplayTape tape, bool cancel) {
        if (cancel) {
            _ = tape.CancelDrive();
            return;
        }
        tape.InjectDriveTick();
        fixture.Step();
        tape.NoteTick();
        Assert.Null(@object: tape.DriveProgress);
    }
    private static WorldIdentity SavedOnDisk(WorldOwnedWorlds catalog, WorldIdentity owned, WorldServer server) => new(
        defaults: server.Definition.PlayerDefaults,
        document: WorldDefinitionSerialization.Deserialize(utf8Json: File.ReadAllBytes(path: Path.Combine(
            path1: catalog.FilePath,
            path2: WorldDocumentName.For(id: SafeName.Parse(candidate: owned.Id))
        )))
    );

    // THE LAW: when a live drive ends, a seat this authority's catalog owns is bound to the live catalog identity again,
    // and the drive's detached copy is discarded. The drive's own write (a fact written while the seat drove the taped
    // projection) is never saved, the owner's later edit is untouched, the two differences are narrated as drift on
    // replay.profile, and a live write after the drive ends is saved to the catalog. A cancel and a drive that reaches its
    // target end through the same door. The red leg leaves the seat on the detached copy: it is not the owned identity,
    // and the live write after the drive stays on the copy and is never saved.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ADriveThatEndsRebindsAnOwnedSeatToTheLiveIdentity(bool cancel) {
        using var directory = new TemporaryDirectory(prefix: "puck-privacy-rebind-");
        var hub = new WorldOutputHub();
        var sink = new RecordingNarrationSink();

        using var attached = hub.AttachNarrationSink(sink: sink);
        using var fixture = Fixtures.FreshServer(catalogNarration: hub);
        var server = fixture.Server;
        var catalog = server.Profiles;

        var (tape, owned) = RecordOwnedSeat(directory: directory.RootPath, fixture: fixture);

        Assert.True(condition: catalog.TrySetFact(changed: out _, identity: owned, key: Name(value: "laterFact"), reason: out var reason, value: 7), userMessage: reason);
        var before = Saved(catalog: catalog, owned: owned);

        Assert.True(condition: tape.TryBeginDrive(documentPath: null, forkName: null, name: "arrivals", refusal: out reason, toTick: null), userMessage: reason);
        var driven = server.Population.EntryBody(index: 0)!.Profile!;

        Assert.NotSame(actual: driven, expected: owned);
        Assert.True(condition: catalog.TrySetFact(changed: out _, identity: driven, key: Name(value: "drivenFact"), reason: out reason, value: 5), userMessage: reason);
        EndDrive(cancel: cancel, fixture: fixture, tape: tape);

        Assert.Same(expected: owned, actual: server.Population.EntryBody(index: 0)!.Profile);
        Assert.Null(@object: Fact(identity: owned, key: "drivenFact"));
        AssertUnchanged(before: before, catalog: catalog, owned: owned);
        Assert.Contains(collection: sink.Narrations, filter: static narration => ((narration.Channel == "replay.profile") && narration.Text.Contains(comparisonType: StringComparison.Ordinal, value: "fact 'drivenFact' drifted since record-start — taped 5, live none")));
        Assert.Contains(collection: sink.Narrations, filter: static narration => ((narration.Channel == "replay.profile") && narration.Text.Contains(comparisonType: StringComparison.Ordinal, value: "fact 'laterFact' drifted since record-start — taped none, live 7")));

        Assert.True(condition: catalog.TrySetFact(changed: out var changed, identity: server.Population.EntryBody(index: 0)!.Profile!, key: Name(value: "liveFact"), reason: out reason, value: 9), userMessage: reason);
        Assert.True(condition: changed);
        var saved = SavedOnDisk(catalog: catalog, owned: owned, server: server);

        Assert.Equal(9L, Fact(identity: saved, key: "liveFact"));
        Assert.Equal(7L, Fact(identity: saved, key: "laterFact"));
        Assert.Null(@object: Fact(identity: saved, key: "drivenFact"));
    }
    // THE LAW: a drive's end touches only the seats this authority's catalog owns. Seat 1 drives a visitor the catalog
    // holds no document for; after the drive it still carries the very identity the drive seated, and no drift is
    // narrated for it. Seat 0, owned, is the control: it is rebound. The red leg rebinds every profiled seat, so the
    // visitor seat loses its identity or the owned one is not rebound.
    [Fact]
    public void ADriveThatEndsLeavesASeatTheCatalogDoesNotOwnAlone() {
        using var directory = new TemporaryDirectory(prefix: "puck-privacy-rebind-visitor-");
        var hub = new WorldOutputHub();
        var sink = new RecordingNarrationSink();

        using var attached = hub.AttachNarrationSink(sink: sink);
        using var fixture = Fixtures.FreshServer(catalogNarration: hub);
        var server = fixture.Server;
        var catalog = server.Profiles;

        Assert.True(condition: server.ApplySession(request: new SessionRequest.Join(Principal: Principal.Seat(slot: 1), Slot: 1,
            IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);
        var visitor = WorldIdentity.FromProjection(
            defaults: server.Definition.PlayerDefaults,
            projection: new WorldIdentityProjection(Id: "guest-visitor", Name: "Guest", ColorHex: "#112233", MoveSpeed: null, TurnSpeed: null)
        );

        server.Population.SetSeatProfile(profile: visitor, slot: 1);
        Assert.False(condition: catalog.Owns(identity: visitor));
        var (tape, owned) = RecordOwnedSeat(directory: directory.RootPath, fixture: fixture);

        Assert.True(condition: tape.TryBeginDrive(documentPath: null, forkName: null, name: "arrivals", refusal: out var reason, toTick: null), userMessage: reason);
        var seated = server.Population.EntryBody(index: 1)!.Profile!;

        Assert.Equal(expected: "guest-visitor", actual: seated.Id);
        Assert.NotSame(actual: server.Population.EntryBody(index: 0)!.Profile, expected: owned);
        Assert.True(condition: catalog.TrySetFact(changed: out _, identity: seated, key: Name(value: "visitFact"), reason: out reason, value: 2), userMessage: reason);
        _ = tape.CancelDrive();

        Assert.Same(expected: seated, actual: server.Population.EntryBody(index: 1)!.Profile);
        Assert.Equal(2L, Fact(identity: seated, key: "visitFact"));
        Assert.False(condition: catalog.Owns(identity: seated));
        Assert.Same(expected: owned, actual: server.Population.EntryBody(index: 0)!.Profile);
        Assert.DoesNotContain(collection: sink.Narrations, filter: static narration => narration.Text.Contains(comparisonType: StringComparison.Ordinal, value: "drifted since record-start"));
    }
    // THE LAW: a save failure's narration and refusal name the file under the catalog and the kind of failure, never a
    // rooted path. A directory standing where the identity's file belongs fails the write; the narration the catalog's hub
    // carries, the reason TrySave returns, and the reason an unowned identity is refused with, hold no part of the
    // catalog's directory. The control is the same save succeeding with the directory gone. The red leg puts the exception
    // message, which names the path, back into the narration.
    [Fact]
    public void ASaveFailureNarrationCarriesNoRootedPath() {
        var hub = new WorldOutputHub();
        var sink = new RecordingNarrationSink();

        using var attached = hub.AttachNarrationSink(sink: sink);
        using var fixture = Fixtures.FreshServer(catalogNarration: hub);
        var catalog = fixture.Server.Profiles;
        var owned = catalog.BootProfile;
        var fileName = WorldDocumentName.For(id: SafeName.Parse(candidate: owned.Id));
        var path = Path.Combine(path1: catalog.FilePath, path2: fileName);

        Assert.True(condition: catalog.TrySave(identity: owned, reason: out var reason), userMessage: reason);
        File.Delete(path: path);
        _ = Directory.CreateDirectory(path: path);
        try {
            Assert.False(condition: catalog.TrySave(identity: owned, reason: out reason));
            catalog.Save();
        } finally {
            Directory.Delete(path: path);
        }

        NarrationPaths.AssertNone(root: catalog.FilePath, text: reason);
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: $"'{fileName}'");
        Assert.NotEmpty(collection: sink.Narrations);

        foreach (var narration in sink.Narrations) {
            NarrationPaths.AssertNone(root: catalog.FilePath, text: narration.Text);
        }

        var visitor = WorldIdentity.FromProjection(defaults: fixture.Server.Definition.PlayerDefaults, projection: owned.Project());

        Assert.False(condition: catalog.TrySave(identity: visitor, reason: out reason));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "not owned by this catalog");
        NarrationPaths.AssertNone(root: catalog.FilePath, text: reason);
        Assert.True(condition: catalog.TrySave(identity: owned, reason: out reason), userMessage: reason);
    }

    // A world whose one rule reads a fact of the identity seat 0 drives: `seen` becomes 1 while that fact is 1.
    private static WorldDefinition FactReadingDocument() => Fixtures.BuildDocument() with {
        StateRaw = new WorldStateSection(World: [
            new WorldStateRow(Name: Name(value: WorldIdentityFactLane.RowName), Kind: CellKind.Int, Capacity: 16),
            new WorldStateRow(Name: Name(value: "seen"), Kind: CellKind.Int, Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Int(value: 0L))]),
        ]),
        Rules = [new WorldRule(
            Name: Name(value: "read"),
            Effects: [new ActionEffect.SetState(State: "seen", Value: 1m)],
            Gate: new ActionPredicate.CompareState(State: $"{WorldRuleFacts.IdentityPrefix}body:0:dived", Comparison: ExpressionOp.Equal, Value: 1m)
        )],
    };

    // THE LAW: a fork records the identity it continues with. A tape is recorded with the owned identity carrying no
    // `dived` fact; the owner then writes it as 1 and the tape is driven to its end as a fork, which rebinds the seat to
    // that live identity. The fork's rule reads the fact, so the fork's world is not the tape's: the fork's own recording,
    // re-driven through a fresh world, must reproduce every tick bit-identically. The red leg records the old tape's pins
    // as the fork's starting identity, so the re-drive reads no fact and diverges from the tick the live fork's rule fired.
    [Fact]
    public void AForkRecordsTheIdentityItContinuesWith() {
        using var directory = new TemporaryDirectory(prefix: "puck-privacy-fork-");
        using var fixture = Fixtures.FreshServer(definition: FactReadingDocument());
        var server = fixture.Server;
        var catalog = server.Profiles;

        var (tape, owned) = RecordOwnedSeat(directory: directory.RootPath, fixture: fixture);

        Assert.Equal(0L, SeenSlot(fixture: fixture));
        Assert.True(condition: catalog.TrySetFact(changed: out _, identity: owned, key: Name(value: "dived"), reason: out var reason, value: 1), userMessage: reason);
        Assert.True(condition: tape.TryBeginDrive(documentPath: null, forkName: "branch", name: "arrivals", refusal: out reason, toTick: null), userMessage: reason);

        while (tape.Mode == WorldReplayMode.Replaying) {
            tape.InjectDriveTick();
            fixture.Step();
            tape.NoteTick();
        }

        Assert.Equal(WorldReplayMode.Recording, tape.Mode);
        Assert.Same(expected: owned, actual: server.Population.EntryBody(index: 0)!.Profile);
        Assert.Equal(0L, SeenSlot(fixture: fixture));

        for (var tick = 0; (tick < 3); tick++) {
            fixture.Step();
            tape.NoteTick();
        }

        Assert.Equal(1L, SeenSlot(fixture: fixture));
        _ = tape.StopRecording();

        var verdict = tape.Verify(name: "branch");

        Assert.Equal(-1, verdict.Primary.DivergedAt);

        WorldReplaySnapshot recorded;

        using (var stream = File.OpenRead(path: tape.PathFor(name: "branch"))) {
            recorded = WorldReplaySnapshot.Read(stream: stream);
        }

        var switches = recorded.Ticks.SelectMany(selector: static tick => tick.Authority).Where(predicate: static entry => (entry.GetType().Name == "SeatIdentity")).ToArray();

        Assert.Single(collection: switches);
    }

    private static long SeenSlot(WorldFixture fixture) => StateRows.FindCell(
        cells: WorldDefinitionRows.FindStateRow(rows: fixture.Server.Definition.State, name: "seen")!.Cells,
        key: WorldStateRow.SlotKey
    )!.Value.AsInt;
    // Two seats driving the one owned identity, a rule writing seat 0's fact when its `armed` fact is 1, and a rule that
    // reads seat 1's fact the write produced.
    private static WorldDefinition SharedIdentityDocument() => Fixtures.BuildDocument() with {
        StateRaw = new WorldStateSection(World: [
            new WorldStateRow(Name: Name(value: WorldIdentityFactLane.RowName), Kind: CellKind.Int, Capacity: 16),
            new WorldStateRow(Name: Name(value: "seen"), Kind: CellKind.Int, Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Int(value: 0L))]),
        ]),
        Rules = [
            new WorldRule(
                Name: Name(value: "write"),
                Effects: [new WorldEffect.SetIdentityFact(Key: "0", Fact: "score", Value: 7m)],
                Gate: new ActionPredicate.CompareState(State: $"{WorldRuleFacts.IdentityPrefix}body:0:armed", Comparison: ExpressionOp.Equal, Value: 1m)
            ),
            new WorldRule(
                Name: Name(value: "read"),
                Effects: [new ActionEffect.SetState(State: "seen", Value: 1m)],
                Gate: new ActionPredicate.CompareState(State: $"{WorldRuleFacts.IdentityPrefix}body:1:score", Comparison: ExpressionOp.Equal, Value: 7m)
            ),
        ],
    };

    // THE LAW: seats the live rebind gave the same owned identity share one identity on the fork's tape. Seats 0 and 1
    // both drive the owned identity; the owner arms it, the tape is driven to its end as a fork, and the fork's rule
    // writes seat 0's score, which the second rule reads through seat 1. The fork's own recording, re-driven through a
    // fresh world, must reproduce it. The red leg gives each switch entry its own detached identity: seat 1 never sees
    // the score and the re-drive diverges.
    [Fact]
    public void AForkKeepsSeatsOfOneIdentitySharingIt() {
        using var directory = new TemporaryDirectory(prefix: "puck-privacy-fork-shared-");
        using var fixture = Fixtures.FreshServer(definition: SharedIdentityDocument());
        var server = fixture.Server;
        var catalog = server.Profiles;
        var owned = catalog.BootProfile;

        foreach (var slot in new[] { 0, 1 }) {
            Assert.True(condition: server.ApplySession(request: new SessionRequest.Join(Principal: Principal.Seat(slot: slot), Slot: slot,
                IdentityName: owned.Name, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);
        }

        var tape = Tape(server: server, directory: directory.RootPath);

        Assert.True(condition: tape.TryBeginRecording(name: "arrivals", refusal: out var reason), userMessage: reason);
        fixture.Step();
        tape.NoteTick();
        _ = tape.StopRecording();

        Assert.True(condition: catalog.TrySetFact(changed: out _, identity: owned, key: Name(value: "armed"), reason: out reason, value: 1), userMessage: reason);
        Assert.True(condition: tape.TryBeginDrive(documentPath: null, forkName: "branch", name: "arrivals", refusal: out reason, toTick: null), userMessage: reason);

        while (tape.Mode == WorldReplayMode.Replaying) {
            tape.InjectDriveTick();
            fixture.Step();
            tape.NoteTick();
        }

        Assert.Same(expected: owned, actual: server.Population.EntryBody(index: 0)!.Profile);
        Assert.Same(expected: owned, actual: server.Population.EntryBody(index: 1)!.Profile);

        for (var tick = 0; (tick < 3); tick++) {
            fixture.Step();
            tape.NoteTick();
        }

        Assert.Equal(1L, SeenSlot(fixture: fixture));
        Assert.Equal(7L, Fact(identity: owned, key: "score"));
        _ = tape.StopRecording();
        Assert.Equal(-1, tape.Verify(name: "branch").Primary.DivergedAt);
    }
}
