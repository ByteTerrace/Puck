using Puck.Physics.Motion;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>A cross-document identity write (<see cref="WorldOwnedWorlds.Submit"/>) runs outside the simulation
/// tick, so it has no clock to settle a value-over-time cell against: a granted write into a slot whose effective
/// behavior is advance, dynamics, or cycle refuses by name, while the identical door still admits an ordinary
/// behavior-free slot.</summary>
public sealed class CrossDocumentIdentityBehaviorWriteLawTests {
    private const string SourceDocumentId = "external-doc";

    private static WorldStateRow AdvancingRow() => StateFixtures.Slot(
        advance: new StateAdvance(
            PerSecondDenominator: 1,
            PerSecondNumerator: 1
        ),
        kind: CellKind.Fixed,
        name: "counterAdvancing",
        value: CellValue.Fixed(rawBits: 0)
    );
    private static WorldStateRow PlainRow() => StateFixtures.FixedSlot(name: "counterPlain", rawBits: 0);
    private static WorldGrant WriteGrant(string slot) => new(
        Grantee: Grantee.Document(id: SourceDocumentId),
        Capability: WorldCapability.Mutate,
        Subject: GrantSubject.State(name: slot),
        Exclusive: false,
        WriteMask: DocumentWriteMask.All
    );
    private static WorldDefinition Document() => Fixtures.BuildDocument() with {
        StateRaw = new WorldStateSection(World: [AdvancingRow(), PlainRow()]),
        GrantsRaw = [WriteGrant(slot: "counterAdvancing"), WriteGrant(slot: "counterPlain")],
    };

    [Fact]
    public void ABehaviorCarryingSlotRefusesByNameWhileABehaviorFreeSlotStillSucceeds() {
        using var fixture = Fixtures.FreshServer(definition: Document());
        var identity = fixture.Server.Profiles.Create(
            colorHex: "#336699",
            name: SafeName.Parse(candidate: "recipient"),
            reason: out var createReason
        );

        Assert.NotNull(@object: identity);

        var refused = fixture.Server.Profiles.Submit(submission: new WorldDocumentSubmission(
            Kind: WorldDocumentWriteKind.Set,
            OwnerDocumentId: identity!.Name,
            Slot: "counterAdvancing",
            SourceDocumentId: SourceDocumentId,
            StorageKind: ActionStateKind.Counter,
            Tick: 0,
            Value: 42
        ));

        Assert.False(condition: refused.Accepted);
        Assert.Contains(
            actualString: refused.Reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "changes over time"
        );
        Assert.True(condition: identity.TryReadState(
            name: "counterAdvancing",
            row: out var untouchedRow
        ));
        Assert.Equal(
            expected: 0L,
            actual: untouchedRow!.Cells!.Single().Value.Raw
        );

        var accepted = fixture.Server.Profiles.Submit(submission: new WorldDocumentSubmission(
            Kind: WorldDocumentWriteKind.Set,
            OwnerDocumentId: identity.Name,
            Slot: "counterPlain",
            SourceDocumentId: SourceDocumentId,
            StorageKind: ActionStateKind.Counter,
            Tick: 0,
            Value: 42
        ));

        Assert.True(
            condition: accepted.Accepted,
            userMessage: accepted.Reason
        );
        Assert.True(condition: identity.TryReadState(
            name: "counterPlain",
            row: out var writtenRow
        ));
        Assert.Equal(
            expected: 42L,
            actual: writtenRow!.Cells!.Single().Value.Raw
        );
    }
}
