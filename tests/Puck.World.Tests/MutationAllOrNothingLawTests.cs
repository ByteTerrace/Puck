using Xunit;

using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// An in-process substrate law directly against <see cref="WorldServer"/>'s apply pipeline (compose candidate,
/// revalidate the WHOLE document, swap): a mutation that fails whole-document validation leaves the live
/// definition BYTE-IDENTICAL, and the same mutation shape with the ONE discriminating fact fixed (a legal row
/// name in place of the engine-reserved <c>$</c> prefix — <see cref="WorldStateRow.ReservedNamePrefix"/>) changes
/// it. <c>WorldServer.ApplyUndo</c>'s journal-replay loop passes every kept entry through this SAME gate
/// (<c>TryCompose</c> then <c>WorldDefinitionValidator.TryValidateLocally</c>) before installing anything, so this
/// law covers its all-or-nothing shape too — but replaying a kept journal prefix is a strict re-run of mutations
/// that already validated once live, in the same order, against the same base document, so nothing in the current
/// engine can make a legitimate replay fail: there is no code path here that constructs a REAL (non-injected)
/// mid-replay failure to prove the loop's own early-return against, and none is proven by this suite. The acting
/// principal is <see cref="WorldPrincipal.Console"/> throughout — this law is about VALIDATION, not authority (see
/// <see cref="AuthorityAdministrationLawTests"/> for the authority law).
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
        var row = new WorldStateRow(Name: CellName.Parse(candidate: name), Kind: CellKind.Int);

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateRow(Principal: WorldPrincipal.Console, Row: row));
        fixture.Step();

        var after = fixture.DefinitionBytes();

        return !before.AsSpan().SequenceEqual(other: after);
    }
}
