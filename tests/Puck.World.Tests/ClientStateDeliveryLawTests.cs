using Puck.Commands;
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
    // The channel/target tables carry no public accessor (nothing outside the client needs one); reflection reads
    // the private field a compilation replaces, so an unchanged reference proves DeliverState recompiled neither.
    private static object ChannelTable(WorldClient client) => Field(
        client: client,
        name: "m_channels"
    );
    private static object Field(WorldClient client, string name) =>
        typeof(WorldClient).GetField(
            bindingAttr: BindingFlags.NonPublic | BindingFlags.Instance,
            name: name
        )!.GetValue(obj: client)!;
    private static long RowCellValue(WorldDefinition definition, string row, string key) =>
        definition.State.Single(predicate: r => (r.Name.Value == row)).Cells!.Single(predicate: c => (c.Key.Value == key)).Value.Raw;
    private static object TargetTable(WorldClient client) => Field(
        client: client,
        name: "m_targets"
    );

    [Fact]
    public void DeliverStateAcrossAScriptedDealUpdatesValuesWithoutRecompilingChannelTables() {
        using var fixture = Fixtures.FreshServer(definition: Game(game: "solitaireKlondike"));

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
            Principal: Principal.Console,
            Row: "solitaireKlondike",
            Key: "option",
            Value: 1,
            Kind: WorldDocumentWriteKind.Set
        ));
        fixture.Step();

        var client = ClientFixtures.Client(definition: fixture.Server.Definition);
        var installedChannels = ChannelTable(client: client);
        var installedTargets = TargetTable(client: client);
        var installedRevision = client.DefinitionRevision;
        var appliedBeforeDeal = Value(
            f: fixture,
            game: "solitaireKlondike",
            key: "applied"
        );

        // A scripted deal: repeated player requests, each installing a value-only mutation (a deal, a draw, or a
        // reject all write only cells the game already declares — never a channel, target, or HUD shape).
        for (var request = 0; (request < 6); request++) {
            Request(
                f: fixture,
                game: "solitaireKlondike",
                action: 1
            );
            client.DeliverState(
                definition: fixture.Server.Definition,
                stamp: new WorldStateStamp(
                EngineTick: 0UL,
                Everything: true,
                MovedRows: default,
                Tick: 0UL
            )
            );

            Assert.Same(
                expected: installedChannels,
                actual: ChannelTable(client: client)
            );
            Assert.Same(
                expected: installedTargets,
                actual: TargetTable(client: client)
            );
            Assert.Equal(
                expected: installedRevision,
                actual: client.DefinitionRevision
            );
        }

        var appliedAfterDeal = Value(
            f: fixture,
            game: "solitaireKlondike",
            key: "applied"
        );

        Assert.NotEqual(
            actual: appliedAfterDeal,
            expected: appliedBeforeDeal
        );
        Assert.Equal(
            expected: appliedAfterDeal,
            actual: RowCellValue(
                definition: client.Definition,
                row: "solitaireKlondike",
                key: "applied"
            )
        );

        // A real shape change recompiles both tables and bumps the rebuild-watch revision, in contrast.
        client.DeliverDefinition(definition: fixture.Server.Definition);

        Assert.NotSame(
            expected: installedChannels,
            actual: ChannelTable(client: client)
        );
        Assert.Equal(
            expected: (installedRevision + 1),
            actual: client.DefinitionRevision
        );
    }

}
