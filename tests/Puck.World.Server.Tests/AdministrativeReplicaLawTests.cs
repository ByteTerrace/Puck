using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

public sealed class AdministrativeReplicaLawTests {
    [Fact]
    public void AnAdministrativeCellEditReachesAnExistingReplicaBeforeSimulationResumes() {
        var definition = new WorldDefinition(Admission: [new WorldAdmissionEntry(Domain: "*",
            Mode: WorldAdmissionTrustMode.FederatedAuthority, Disclosure: WorldDisclosureTier.Replica,
            Grants: [new WorldAdmissionGrant(Capability: WorldCapability.Observe, Subject: GrantSubject.All, Budget: 100, EventBudget: 100)])])
            .WithWorldState(rows: [new WorldStateRow(Name: CellName.Parse(candidate: "value"), Kind: CellKind.Int, Min: 0, Max: 100,
                Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Int(value: 1))])]);
        using var fixture = Fixtures.FreshServer(definition: definition);
        var sink = new Replica();
        using var observation = fixture.Server.TryObserveAsSession(refusal: out var refusal, sink: sink, sourceAuthority: "reader");

        Assert.NotNull(@object: observation);
        Assert.True(condition: string.IsNullOrEmpty(value: refusal), userMessage: refusal);
        Assert.Equal(actual: sink.Value, expected: 1);
        fixture.WriteSlot(row: "value", value: 7);
        Assert.True(condition: fixture.Server.DrainAdministrative());
        Assert.Equal(expected: 7, actual: fixture.SlotValue(row: "value"));
        Assert.Equal(actual: sink.Value, expected: 7);
        Assert.Equal(expected: 0UL, actual: fixture.Server.CompletedTick);
        fixture.Step();
        Assert.Equal(actual: sink.Value, expected: 7);
        fixture.WriteSlot(row: "value", value: 8);
        fixture.Step();
        Assert.Equal(actual: sink.Value, expected: 8);
    }

    private sealed class Replica : IClientSink {
        public long Value;

        public void DeliverDefinition(WorldDefinition definition, WorldDocumentVersion version) => Value = definition.State[0].Cells![0].Value.Raw;
        public void DeliverState(WorldDefinition definition, WorldDocumentVersion version, in WorldStateStamp stamp) => DeliverDefinition(definition: definition, version: version);
        public void DeliverSnapshot(in WorldSnapshot snapshot) {
            foreach (var delta in snapshot.FieldCells.Span) {
                if ((delta.Row == 0) && (delta.Cell >= 0)) { Value = delta.Image!.Value.Raw; }
            }
        }
        public void DeliverAnswer(in QueryAnswer answer) { }
        public void DeliverComposition(WorldComposition composition) { }
        public void DeliverSessionLever(WorldSessionLever lever) { }
    }
}
