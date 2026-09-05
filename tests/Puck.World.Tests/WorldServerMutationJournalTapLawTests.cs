using Xunit;

using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>Proves <see cref="WorldServer.MutationJournalTap"/> fires with the exact tick/mutation
/// <see cref="WorldServer.TryApplyJournalTailMutation"/> reapplies — a hosted row's live-journaling seam
/// (<c>Puck.World.Silo</c>'s <c>WorldSiloHost.ScheduleJournalAppend</c>), proved at this layer without a store or a
/// silo: a fresh, uncheckpointed server fed only the tapped-and-reencoded entry reaches the tapped mutation's own
/// effect — the "mutate, kill before any checkpoint, restart, the mutation survives" claim.</summary>
public sealed class WorldServerMutationJournalTapLawTests {
    private static readonly WorldPrincipal Seller = WorldPrincipal.Seat(slot: 0);

    private static WorldOwnedWorlds FreshProfiles(WorldDefinition definition) => new(
        directory: Directory.CreateTempSubdirectory(prefix: "puck-journal-tap-tests-").FullName,
        machineId: Guid.NewGuid(),
        template: definition
    );

    [Fact]
    public void MutationJournalTap_DoesNotFireForARejectedMutation() {
        using var fixture = Fixtures.FreshServer(definition: Fixtures.BuildDocument());
        var fired = 0;

        fixture.Server.MutationJournalTap = (_, _) => fired++;

        // A cell write against an undeclared row is refused by the compose-time gate before the mutation ever
        // reaches the journal — the tap must not fire for it.
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
            Principal: Seller, Row: "doesNotExist", Key: "0", Value: 1, Kind: WorldDocumentWriteKind.Set
        ));
        fixture.Step();

        Assert.Equal(
            actual: fired,
            expected: 0
        );
    }
    [Fact]
    public void MutationJournalTap_FiresOnApply_AndItsReEncodedEntryReplaysToTheSameEffect() {
        var row = new WorldStateRow(Name: CellName.Parse(candidate: "gauge"), Kind: CellKind.Int, Capacity: 8);
        var document = Fixtures.BuildDocument().WithWorldState(rows: [row]);
        using var fixture = Fixtures.FreshServer(definition: document);
        (ulong Tick, byte[] Encoded)? captured = null;

        fixture.Server.MutationJournalTap = (tick, mutation) => {
            Assert.Null(@object: captured);
            Assert.True(
                condition: WorldSubmissionCodec.TryEncodeCommittedMutation(
                bytes: out var encoded,
                failure: out var failure,
                mutation: mutation
            ),
                userMessage: failure.ToString()
            );
            captured = (tick, encoded);
        };

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
            Principal: Seller, Row: "gauge", Key: "0", Value: 42, Kind: WorldDocumentWriteKind.Set
        ));
        fixture.Step();

        Assert.NotNull(@object: captured);

        var liveRow = WorldDefinitionRows.FindStateRow(rows: fixture.Server.Definition.State, name: "gauge");

        Assert.NotNull(@object: liveRow);

        // A restart replays the tail against a fresh server built from the SAME (uncheckpointed) base document —
        // no checkpoint exists yet, so this is the whole recovered state.
        var definition = Fixtures.BuildDocument().WithWorldState(rows: [row]);
        using var restoredMachines = new WorldMachineHost(
            engines: [],
            screens: definition.Screens
        );
        var restoredServer = new WorldServer(
            definition: definition,
            envelope: new WorldRenderEnvelope(),
            instanceIdentity: "restored",
            machines: restoredMachines,
            population: new WorldPopulation(definition: definition),
            profiles: FreshProfiles(definition: definition)
        );

        Assert.True(condition: WorldSubmissionCodec.TryDecodeCommittedMutation(
            bytes: captured!.Value.Encoded,
            failure: out var decodeFailure,
            mutation: out var decoded
        ), userMessage: decodeFailure.ToString());
        Assert.True(condition: restoredServer.TryApplyJournalTailMutation(
            mutation: decoded!,
            tick: captured.Value.Tick
        ));

        var restoredRow = WorldDefinitionRows.FindStateRow(rows: restoredServer.Definition.State, name: "gauge");

        var writtenKey = CellName.Parse(candidate: "0");

        Assert.NotNull(@object: restoredRow);
        Assert.Equal(
            expected: WorldDefinitionRows.FindCell(cells: liveRow!.Cells, key: writtenKey)!.Value,
            actual: WorldDefinitionRows.FindCell(cells: restoredRow!.Cells, key: writtenKey)!.Value
        );
    }
}
