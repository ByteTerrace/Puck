using Xunit;

using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>
/// The engage/disengage-authority law (a denial paired with a control, actor always distinct from the target), as an
/// in-process substrate law directly against the engage door:
/// <see cref="Puck.World.Server.WorldEngagement.CheckEngage"/>, which is nothing more than a
/// <see cref="WorldCapability.Control"/> read over the ONE grant table (<c>m_grants.Allows</c>) — the SAME table
/// <c>player.engage</c> consults server-side before it ever calls
/// <see cref="Puck.World.Server.WorldEngagement.Engage"/>. THE LAW: a principal that LACKS Control over a target
/// screen is REFUSED engagement; the identical engage attempt by the SAME principal, once it HOLDS Control over
/// that exact target, SUCCEEDS.
/// <para>Every seat is boot-seeded with the wide <c>Control/all</c> wildcard (the puck-world skill's authority
/// reference, "domain seed": Observe/Control/Edit over <c>all</c> for every seat and Console) — so a seat's
/// self-targeting a screen proves nothing on its own; the wildcard is stripped from the acting seat FIRST (the
/// "revoke first, then prove the denial, then re-grant and prove success" shape the puck-world skill's own
/// engagement-authority "Verifying" section names), which is what makes the denied case a REAL refusal rather than
/// a no-op against a permissive default. The control case then re-grants Control narrowly over the target screen
/// ONLY — <see cref="Puck.World.Tests.Laws.RefusalWithControl"/>'s "the SAME action with the ONE discriminating
/// fact reversed", never the wildcard restored wholesale.</para>
/// Actor (Seat 1, body index 1) and target (the code-built test-pattern screen at index 0,
/// <see cref="Fixtures.TestPatternScreenIndex"/>) are different subject kinds entirely — actor ≠ target by
/// construction, so self-targeting discriminates nothing here either.
/// </summary>
public sealed class EngageAuthorityLawTests {
    [Fact]
    public void SeatLackingScreenControlIsRefused_SeatHoldingScreenControlSucceeds() {
        using var fixture = Fixtures.FreshServer();

        var actor = WorldPrincipal.Seat(slot: 1);
        var target = GrantSubject.Screen(index: Fixtures.TestPatternScreenIndex);
        var controlAll = new WorldGrant(Principal: actor, Capability: WorldCapability.Control, Subject: GrantSubject.All, Exclusive: false);
        var controlOverTarget = new WorldGrant(Principal: actor, Capability: WorldCapability.Control, Subject: target, Exclusive: false);

        // Engage(...) requires a LIVE body at the routed entity index — a local seat's body is not minted at
        // WorldPopulation construction (Fixtures.FreshServer boots with every seat unjoined), only on Join
        // (Server.WorldPopulation.ActivateSeat, reached here through the ordinary session door). This mirrors
        // WorldReplaySnapshot.Drive's own seat rehydration and is furniture the entity-index resolution needs, not
        // part of the authority fact under test.
        _ = fixture.Server.ApplySession(request: new SessionRequest.Join(Principal: actor, Slot: actor.Index, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey));

        // Strip the boot-seeded wildcard FIRST — without this, CheckEngage would allow the denied case through via
        // WildcardHold and the law would prove nothing (README.md red-line #2: every test must fail for a real
        // reason).
        fixture.Server.Revoke(grant: controlAll, actor: WorldPrincipal.Console);

        Laws.RefusalWithControl(
            lawId: "engagement.control-authority-required",
            deniedOutcome: () => EngageAndObserve(fixture: fixture, actor: actor, target: target),
            controlOutcome: () => {
                // The missing grant restored — narrowly, over the target screen only, never the wildcard.
                fixture.Server.Grant(grant: controlOverTarget, actor: WorldPrincipal.Console);

                return EngageAndObserve(fixture: fixture, actor: actor, target: target);
            });
    }

    // Engage(...) itself IS the observation: it checks CheckEngage first and returns false without mutating
    // anything on a denial (Puck.World.Server.WorldEngagement.Engage's own contract), so its bool return is exactly
    // the door's verdict — no need to separately probe the grant table or the route afterward.
    private static bool EngageAndObserve(WorldFixture fixture, WorldPrincipal actor, GrantSubject target) =>
        fixture.Server.Engagement.Engage(entityIndex: actor.Index, target: target, capture: true, actingPrincipal: actor, targetPrincipal: actor);
}
