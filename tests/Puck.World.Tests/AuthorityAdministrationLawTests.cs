using Xunit;

using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>
/// Ports the law <c>verification/authority</c> proved out-of-process before its quarantine (a denial paired with a
/// control, actor always distinct from the target, so a check that consults the wrong principal is caught) as an in-process
/// substrate law directly against <see cref="Puck.World.Server.WorldServer.Grant"/>'s actor check
/// (<c>WorldGrants.HoldsForAdministration</c>): a <see cref="PrincipalKind.Seat"/> actor may administer a grant row
/// ONLY when the row's SUBJECT is its own body — <c>IsOwnSeatBody</c>'s rule, the narrowed 2026-08 posture the
/// runner's own <c>03-assign.txt</c>/<c>05-disengage.txt</c> cases exist to keep closed (a handler that consults
/// the wrong principal, or the wrong subject, is the exact defect class both name). Administering a grant over a
/// DIFFERENT body — one the acting seat does not itself occupy — refuses; the identical call naming the actor's
/// OWN body succeeds. The RECIPIENT of the grant (a third principal, distinct from the actor and never itself
/// checked here) stays fixed across both cases, which is what isolates the actor's own standing as the fact under
/// test — a defect that checked the RECIPIENT's standing instead (the laundering shape the runner's round-1
/// findings named) would pass this law only by coincidence, not by the recipient having a body:0 or body:1 hold of
/// its own (it starts with neither — seats seed Drive over their OWN body index only).
/// </summary>
public sealed class AuthorityAdministrationLawTests {
    [Fact]
    public void SeatAdministersOnlyItsOwnBody_OtherBodyRefused_OwnBodySucceeds() {
        using var fixture = Fixtures.FreshServer();

        var actor = WorldPrincipal.Seat(slot: 1);
        var recipient = WorldPrincipal.Seat(slot: 2);
        // The other actor's own body — Seat(1) administering it is exactly the cross-body laundering case; the
        // control substitutes the actor's OWN body (index 1), the one subject IsOwnSeatBody admits for a Seat actor.
        var otherBody = GrantSubject.Body(index: 0);
        var ownBody = GrantSubject.Body(index: actor.Index);

        Laws.RefusalWithControl(
            lawId: "authority.grant-administration-own-body-only",
            deniedOutcome: () => GrantAndObserveHeld(fixture: fixture, actor: actor, recipient: recipient, subject: otherBody),
            controlOutcome: () => GrantAndObserveHeld(fixture: fixture, actor: actor, recipient: recipient, subject: ownBody));
    }

    private static bool GrantAndObserveHeld(WorldFixture fixture, WorldPrincipal actor, WorldPrincipal recipient, GrantSubject subject) {
        var grant = new WorldGrant(Principal: recipient, Capability: WorldCapability.Drive, Subject: subject, Exclusive: false);

        fixture.Server.Grant(grant: grant, actor: actor);

        return fixture.Server.Grants.Allows(principal: recipient, capability: WorldCapability.Drive, subject: subject);
    }
}
