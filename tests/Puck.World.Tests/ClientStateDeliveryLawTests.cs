using System.Reflection;

using Puck.World.Client;
using Puck.World.Protocol;

using Xunit;

using static Puck.World.Tests.SolitaireFixtures;

namespace Puck.World.Tests;

/// <summary>Pins the <see cref="IClientSink.DeliverState"/>/<see cref="IClientSink.DeliverDefinition"/> split: a
/// value-only mutation delivers through <c>DeliverState</c>, which the client folds into state-value reads without
/// recompiling its channel or target-register tables or bumping the scene rebuild-watch revision; only a shape
/// change (<c>DeliverDefinition</c>) does either.</summary>
public sealed class ClientStateDeliveryLawTests {
    [Fact]
    public void DeliverStateAcrossAScriptedDealUpdatesValuesWithoutRecompilingChannelTables() {
        using var fixture = Fixtures.FreshServer(definition: Game(game: "solitaireKlondike"));
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(Principal: WorldPrincipal.Console, Row: "solitaireKlondike", Key: "option", Value: 1, Kind: WorldDocumentWriteKind.Set));
        fixture.Step();

        var client = Client(definition: fixture.Server.Definition);
        var installedChannels = ChannelTable(client: client);
        var installedTargets = TargetTable(client: client);
        var installedRevision = client.DefinitionRevision;
        var appliedBeforeDeal = Value(f: fixture, game: "solitaireKlondike", key: "applied");

        // A scripted deal: repeated player requests, each installing a value-only mutation (a deal, a draw, or a
        // reject all write only cells the game already declares — never a channel, target, or HUD shape).
        for (var request = 0; request < 6; request++) {
            Request(f: fixture, game: "solitaireKlondike", action: 1);
            client.DeliverState(definition: fixture.Server.Definition);

            Assert.Same(expected: installedChannels, actual: ChannelTable(client: client));
            Assert.Same(expected: installedTargets, actual: TargetTable(client: client));
            Assert.Equal(expected: installedRevision, actual: client.DefinitionRevision);
        }

        var appliedAfterDeal = Value(f: fixture, game: "solitaireKlondike", key: "applied");

        Assert.NotEqual(expected: appliedBeforeDeal, actual: appliedAfterDeal);
        Assert.Equal(expected: appliedAfterDeal, actual: RowCellValue(definition: client.Definition, row: "solitaireKlondike", key: "applied"));

        // A real shape change recompiles both tables and bumps the rebuild-watch revision, in contrast.
        client.DeliverDefinition(definition: fixture.Server.Definition);

        Assert.NotSame(expected: installedChannels, actual: ChannelTable(client: client));
        Assert.Equal(expected: (installedRevision + 1), actual: client.DefinitionRevision);
    }

    private static long RowCellValue(WorldDefinition definition, string row, string key) =>
        definition.State.Single(predicate: r => (r.Name.Value == row)).Cells!.Single(predicate: c => (c.Key.Value == key)).Value;
    // The narrowest link a PlayerRoster can be built over: it answers the one construction-time query and drops
    // everything else, so no server has to run to construct the client under test.
    private sealed class SilentLink(WorldDefinition definition) : IServerLink {
        public void Query(WorldQuery query, Action<QueryAnswer> completion) {
            if (query is WorldQuery.PopulationChannels) {
                completion(obj: new QueryAnswer(
                    Payload: WorldChannelTable.Compile(channels: definition.Channels),
                    Text: string.Empty
                ));
            }
        }
        public long SubmitEnvelope(WorldSubmissionPayload payload, WorldPrincipal principal) => 0L;
        public void SubmitIntent(in IntentSubmission submission) {
        }
        public void SubmitSession(SessionRequest request, Action<SessionReply> completion) {
        }
    }
    private static WorldClient Client(WorldDefinition definition) => new(
        composition: new WorldCompositionState(),
        definition: definition,
        roster: new PlayerRoster(
            definition: definition,
            link: new SilentLink(definition: definition),
            seatBindings: new WorldSeatBindings(definition: definition)
        ),
        seatRouter: new WorldSeatAuthorityRouter()
    );
    // The channel/target tables carry no public accessor (nothing outside the client needs one); reflection reads
    // the private field a compilation replaces, so an unchanged reference proves DeliverState recompiled neither.
    private static object ChannelTable(WorldClient client) => Field(client: client, name: "m_channels");
    private static object TargetTable(WorldClient client) => Field(client: client, name: "m_targets");
    private static object Field(WorldClient client, string name) =>
        typeof(WorldClient).GetField(name: name, bindingAttr: (BindingFlags.NonPublic | BindingFlags.Instance))!.GetValue(obj: client)!;
}
