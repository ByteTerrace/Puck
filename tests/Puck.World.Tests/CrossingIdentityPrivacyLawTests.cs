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
    // THE LAW: a federated reservation always asks for peer admission. The encoded control decodes; the same bytes with
    // the admission flag cleared are refused by name, so a remote peer cannot take a local seat or the home adoption
    // that comes with one. The red leg decodes the cleared flag into a local-seat reservation.
    [Fact]
    public void ARemoteReservationCannotClaimColocatedAdmission() {
        var address = new WorldEntityAddress(Authority: "origin", Generation: 1, Index: 0);
        var request = new WorldTransferReservationRequest(
            TransferId: 1, SourceAuthority: "origin", SourceRateHz: 240, SourceTick: 0,
            DeadlineSourceTick: 60, Border: "", BorderCapacity: null, PartyAllOrNothing: true,
            PeerAdmission: true, Members: [new WorldTransferReservationMember(
                Principal: Principal.Console, PreferredSlot: 0, Identity: Owned().Project(),
                Source: IntentSource.Live, BodyColor: default, CatalogRig: 0,
                Mobility: new WorldMobilityIdentity(DepartedFrom: address, Epoch: 0, Incarnation: address)
            )]
        );
        var encoded = WorldFederationCodec.EncodeReservation(request: request);

        Assert.True(condition: WorldFederationCodec.TryDecodeReservation(body: encoded, failure: out var failure, request: out var decoded), userMessage: failure.ToString());
        Assert.True(condition: decoded!.PeerAdmission);
        // Locate the flag from the complete header, independent of string-length encodings.
        var prefix = new Puck.Networking.WireWriter();

        prefix.WriteUInt64(value: request.TransferId);
        prefix.WriteString(value: request.SourceAuthority);
        prefix.WriteInt32(value: request.SourceRateHz);
        prefix.WriteUInt64(value: request.SourceTick);
        prefix.WriteUInt64(value: request.DeadlineSourceTick);
        prefix.WriteString(value: request.Border);
        prefix.WriteBoolean(value: false);
        prefix.WriteBoolean(value: request.PartyAllOrNothing);
        encoded[prefix.WrittenSpan.Length] = 0;

        Assert.False(condition: WorldFederationCodec.TryDecodeReservation(body: encoded, failure: out failure, request: out decoded));
        Assert.Null(@object: decoded);
        Assert.Contains("peer admission", failure.Detail);
    }
    // THE LAW: verifying a tape changes nothing the live session owns. A tape records a home arrival carrying a fact at
    // 1; the owner then writes it to 2; verifying the tape matches, and the live identity, its document, the catalog's
    // revision and its files still hold 2. The red leg re-drives against the live catalog, adopting 1 and saving it.
    [Fact]
    public void VerifyingAHomeArrivalDoesNotRewindTheLiveCatalog() {
        using var directory = new TemporaryDirectory(prefix: "puck-privacy-replay-");
        using var fixture = Fixtures.FreshServer();
        var server = fixture.Server;
        var catalog = server.Profiles;
        var owned = catalog.BootProfile;
        var fact = Name(value: "replayFact");

        Assert.True(condition: catalog.TrySetFact(changed: out _, identity: owned, key: fact, reason: out var reason, value: 1), userMessage: reason);
        var tape = Tape(server: server, directory: directory.RootPath);

        Assert.True(condition: tape.TryBeginRecording(name: "arrivals", refusal: out var refusal), userMessage: refusal);
        var address = new WorldEntityAddress(Authority: server.AuthorityIdentity, Index: 0, Generation: 1);
        var request = new WorldTransferReservationRequest(
            TransferId: 1, SourceAuthority: "away", SourceRateHz: 240, SourceTick: 0,
            DeadlineSourceTick: 60, Border: "", BorderCapacity: null, PartyAllOrNothing: true,
            PeerAdmission: false, Members: [new WorldTransferReservationMember(
                Principal: Principal.Console, PreferredSlot: 0, Identity: owned.Project(),
                Source: IntentSource.Live, BodyColor: owned.Color, CatalogRig: 0,
                Mobility: new WorldMobilityIdentity(DepartedFrom: address, Epoch: 1, Incarnation: address)
            )]
        );

        Assert.True(condition: server.ReserveTransfer(request: request).Accepted);
        var member = new WorldTransferCommitMember(Profile: owned.Project(), HasMappedArrival: false,
            BodyMotionProgramName: "", Position: default, YawRadians: default, PlanarVelocity: default, VerticalVelocity: default);

        Assert.Equal(WorldTransferStatus.Committed, server.CommitTransfer(sourceAuthority: request.SourceAuthority,
            transferId: request.TransferId, members: [member], reason: out reason));
        fixture.Step();
        tape.NoteTick();
        _ = tape.StopRecording();
        Assert.True(condition: catalog.TrySetFact(changed: out _, identity: owned, key: fact, reason: out reason, value: 2), userMessage: reason);
        var before = WorldDefinitionSerialization.Serialize(definition: owned.Document!);
        var files = Directory.GetFiles(path: catalog.FilePath).ToDictionary(keySelector: static path => path, elementSelector: static path => File.ReadAllBytes(path: path));
        var revision = catalog.Revision;

        var verdict = tape.Verify(name: "arrivals");

        Assert.Equal(-1, verdict.Primary.DivergedAt);
        Assert.Equal(2L, Fact(identity: owned, key: "replayFact"));
        Assert.Equal(before, WorldDefinitionSerialization.Serialize(definition: owned.Document!));
        Assert.Equal(revision, catalog.Revision);
        foreach (var (path, bytes) in files) {
            Assert.Equal(bytes, File.ReadAllBytes(path: path));
        }
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
}
