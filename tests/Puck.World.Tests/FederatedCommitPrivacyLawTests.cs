using System.Net.Quic;
using System.Text;
using Puck.Commands;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST (acceptance law 5): a federated commit never carries private profile data to the destination.
/// The traveller's owned identity holds a private row, a private HUD panel, a line in its chat inbox, a HUD text and a
/// binding label, each with its own marker. A crossing commits the members its source logged at departure; encoded as
/// the commit the source sends, they carry none of the markers and no embedded world document, and do carry the
/// traveller's id. The seat the commit lands in has no document, no bindings and no HUD, and carries exactly the
/// traveller's projection, whether the crossing is colocated or federated.
/// </summary>
public sealed class FederatedCommitPrivacyLawTests {
    private const string BindingLabel = "private-binding-label";
    private const string HudText = "private-hud-text";
    private const string InboxLine = "private-inbox-line";

    private static readonly string[] Markers = [
        .. CrossingIdentityPrivacyLawTests.Markers,
        InboxLine,
        HudText,
        BindingLabel,
        WorldDefinition.SchemaVersion,
    ];

    // The privacy laws' owned identity, with a chat inbox line, a HUD text and a binding label beside its private rows.
    private static WorldIdentity Traveller() {
        var owned = CrossingIdentityPrivacyLawTests.Owned();
        var document = owned.Document!;

        // Declared as chat.inbox declares it, on the owned document in place.
        owned.ReplaceDocument(document: document.WithWorldState(rows: [
            .. document.AuthoredState,
            new WorldStateRow(Name: WorldIdentityRows.ChatInbox, Kind: CellKind.Text, Capacity: 4, Evicts: true),
        ]));
        Assert.True(condition: owned.TryAppendEvictingText(evictedKey: out _, reason: out var reason, rowName: WorldIdentityRows.ChatInbox, text: InboxLine), userMessage: reason);
        owned.Hud = new WorldHudPanel(
            Elements: [new WorldHudElement(Id: "note", Kind: WorldHudElementKind.Text, Rect: new WorldHudRect(Height: 1f, Width: 1f, X: 0f, Y: 0f), Style: WorldHudStyleToken.Primary, Text: HudText)],
            Id: "notes",
            Layer: WorldHudLayer.Over,
            Rect: new WorldHudRect(Height: 1f, Width: 1f, X: 0f, Y: 0f),
            Style: WorldHudPanelStyle.Chip
        );
        owned.Bindings = new BindingProfileDocument(
            Chords: [],
            Modifiers: [new BindingModifierDefinition(Id: "overlay", Label: BindingLabel, Sources: ["gamepad.a"])],
            Version: BindingProfileDocument.CurrentVersion
        );
        return owned;
    }
    // The commit the source sent: the members it logged at departure, encoded the way it sends them.
    private static byte[] SentCommit(IEnumerable<byte[]> sourceLog, string sourceAuthority) {
        foreach (var bytes in sourceLog) {
            Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecodeCrossingEntry(bytes: bytes, entry: out var entry, reason: out var reason), userMessage: reason);

            if (entry.Record is WorldCrossingRecord.Departure { Transfer: var transfer }) {
                return WorldFederationCodec.EncodeCommit(members: transfer.CommitMembers, sourceAuthority: sourceAuthority, transferId: transfer.TransferId);
            }
        }
        throw new InvalidOperationException(message: "the source logged no departure");
    }
    private static WorldIdentity LandedAt(WorldServer server) {
        for (var slot = 0; (slot < server.Population.Capacity); slot++) {
            if (server.Population.EntryBody(index: slot)?.Profile is { Id: CrossingIdentityPrivacyLawTests.OwnerId } profile) {
                return profile;
            }
        }
        throw new InvalidOperationException(message: $"'{server.AuthorityIdentity}' holds no body driven by '{CrossingIdentityPrivacyLawTests.OwnerId}'");
    }
    private static void AssertKeptAtHome(byte[] commit, WorldIdentity landed, WorldIdentity departed) {
        var text = Encoding.UTF8.GetString(bytes: commit);

        Assert.Contains(expectedSubstring: departed.Id, actualString: text);
        foreach (var marker in Markers) {
            Assert.False(condition: text.Contains(comparisonType: StringComparison.Ordinal, value: marker), userMessage: $"the commit carries '{marker}'");
        }
        Assert.True(condition: WorldFederationCodec.TryDecodeCommit(body: commit, failure: out var failure, members: out var members, sourceAuthority: out _, transferId: out _), userMessage: failure.ToString());
        Assert.True(condition: WorldIdentityProjectionWire.Matches(left: departed.Project(), right: Assert.Single(collection: members).Profile), userMessage: "the decoded commit is not the traveller's projection");
        Assert.Null(@object: landed.Document);
        Assert.Null(@object: landed.Bindings);
        Assert.Null(@object: landed.Hud);
        Assert.True(condition: WorldIdentityProjectionWire.Matches(left: departed.Project(), right: landed.Project()), userMessage: "the landed seat is not the traveller's projection");
    }
    private static WorldIdentity CrossColocated() {
        using var world = CrossingWorld.Build();
        var traveller = Traveller();

        world.Source.Server.Population.SetSeatProfile(profile: traveller, slot: 0);
        _ = world.Cross();

        var landed = LandedAt(server: world.Destination.Server);

        AssertKeptAtHome(commit: SentCommit(sourceAuthority: world.Source.Server.AuthorityIdentity, sourceLog: world.SourceLog.Encoded), departed: traveller, landed: landed);
        return landed;
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

    [Fact]
    public void AColocatedCommitKeepsPrivateProfileDataAtHome() => _ = CrossColocated();
    [Fact]
    public void AFederatedCommitKeepsPrivateProfileDataAtHomeAndLandsAsItsColocatedTwin() {
        Assert.SkipUnless(condition: QuicListener.IsSupported, reason: "this host has no QUIC, which a federated crossing needs");

        using var hosts = FederatedHosts.Build();
        var log = new CapturingCrossingLog();
        var traveller = Traveller();

        hosts.SourceRow.Server.InstallCrossingLog(log: log);
        hosts.JoinSeat(slot: 0, travelTurn: default);
        hosts.SourceRow.Server.Population.SetSeatProfile(profile: traveller, slot: 0);
        _ = hosts.Source.EnqueueTransfer(
            actingPrincipal: Principal.Console,
            destination: hosts.RemoteDestination,
            scope: WorldInstanceHost.TransferScope.Body,
            sourceInstance: WorldInstanceHost.BootInstanceName,
            sourceSlot: 0
        );
        hosts.Source.DrainPendingTransfers();

        var landed = hosts.DestinationRow.Server.ExecuteAuthorityOperation(operation: () => LandedAt(server: hosts.DestinationRow.Server));

        AssertKeptAtHome(commit: SentCommit(sourceAuthority: hosts.SourceRow.Server.AuthorityIdentity, sourceLog: log.Entries), departed: traveller, landed: landed);
        Assert.True(condition: WorldIdentityProjectionWire.Matches(left: CrossColocated().Project(), right: landed.Project()), userMessage: "the federated and colocated crossings landed different projections");
    }
}
