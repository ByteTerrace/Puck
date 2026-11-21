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
        var sink = new SessionReplica();
        using var observation = fixture.Server.TryObserveAsSession(refusal: out var refusal, sink: sink, sourceAuthority: "reader");

        Assert.NotNull(@object: observation);
        Assert.True(condition: string.IsNullOrEmpty(value: refusal), userMessage: refusal);
        Assert.Equal(actual: sink.ReadSlot(row: "value"), expected: 1);
        fixture.WriteSlot(row: "value", value: 7);
        Assert.True(condition: fixture.Server.DrainAdministrative());
        Assert.Equal(expected: 7, actual: fixture.SlotValue(row: "value"));
        Assert.Equal(actual: sink.ReadSlot(row: "value"), expected: 7);
        Assert.Equal(expected: 0UL, actual: fixture.Server.CompletedTick);
        fixture.Step();
        Assert.Equal(actual: sink.ReadSlot(row: "value"), expected: 7);
        fixture.WriteSlot(row: "value", value: 8);
        fixture.Step();
        Assert.Equal(actual: sink.ReadSlot(row: "value"), expected: 8);
    }
}
