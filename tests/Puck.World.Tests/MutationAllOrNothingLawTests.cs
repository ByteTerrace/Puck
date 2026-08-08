using Xunit;

using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// Ports the all-or-nothing half of <c>docs/verification/undo-all-or-nothing/run.ps1</c> as an in-process
/// substrate law directly against <see cref="WorldServer"/>'s apply pipeline (compose candidate, revalidate the
/// WHOLE document, swap): a mutation that fails whole-document validation leaves the live definition
/// BYTE-IDENTICAL, and the same mutation shape with the ONE discriminating fact fixed (a legal row name in place
/// of the engine-reserved <c>$</c> prefix — <see cref="WorldStateRow.ReservedNamePrefix"/>) changes it. This is
/// the general apply-time gate the undo runner's own journal-replay case also passes through
/// (<c>WorldServer.TryApplyMutation</c> calls <c>WorldDefinitionValidator.TryValidate</c> unconditionally before
/// installing); the runner's own journal-replay framing is left for a later port, noted in the task report.
/// The acting principal is <see cref="WorldPrincipal.Console"/> throughout — this law is about VALIDATION, not
/// authority (see <see cref="AuthorityAdministrationLawTests"/> for the authority law).
/// </summary>
public sealed class MutationAllOrNothingLawTests {
    [Fact]
    public void InvalidRowLeavesDocumentUnchanged_ValidRowChangesIt() {
        using var fixture = Fixtures.FreshServer();

        Laws.RefusalWithControl(
            lawId: "mutation.upsert-state-row-all-or-nothing",
            deniedOutcome: () => ApplyAndObserveChange(fixture: fixture, name: $"{WorldStateRow.ReservedNamePrefix}illegal"),
            controlOutcome: () => ApplyAndObserveChange(fixture: fixture, name: "probe"));
    }

    private static bool ApplyAndObserveChange(WorldFixture fixture, string name) {
        var before = fixture.DefinitionBytes();
        var row = new WorldStateRow(Name: WorldCellName.Parse(candidate: name), Kind: CellKind.Int);

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateRow(Principal: WorldPrincipal.Console, Row: row));
        fixture.Step();

        var after = fixture.DefinitionBytes();

        return !before.AsSpan().SequenceEqual(other: after);
    }
}
