using Xunit;

using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>
/// The acting-principal law behind <c>player.mode</c>'s fly control application: composing a camera state idles the
/// TARGET body through a <see cref="WorldCommand.SetControl"/>, and that command must be stamped with the ACTING
/// principal so the server's Drive gate (<see cref="Puck.World.Server.WorldServer.ApplyCommand"/>) checks the actor's
/// authority over the target body — never the target seat's own principal, which would trivially pass and let a
/// session holding Drive over only its own body idle another seat's body.
/// <para>THE LAW: a principal that LACKS Drive over a target body is REFUSED the idle (the body's intent source is
/// untouched); the identical command by the SAME principal, once it HOLDS Drive over that exact body, SUCCEEDS.
/// Every seat is boot-seeded Drive over its OWN body only (no Drive wildcard), so Seat 1 genuinely lacks Drive over
/// Seat 0's body — the denied case is a real refusal, not a no-op against a permissive default. Actor (Seat 1,
/// body index 1) and target (body index 0) are distinct by construction, so self-targeting discriminates
/// nothing.</para>
/// </summary>
public sealed class SeatModeControlAuthorityLawTests {
    private const int TargetBody = 0;

    [Fact]
    public void ActorLackingDriveOverTargetBodyIsRefused_ActorHoldingDriveSucceeds() {
        using var fixture = Fixtures.FreshServer();

        var actor = WorldPrincipal.Seat(slot: 1);
        var driveOverTarget = new WorldGrant(Principal: actor, Capability: WorldCapability.Drive, Subject: GrantSubject.Body(index: TargetBody), Exclusive: false);

        // Both bodies must be live: a local seat's body is minted on Join, not at construction, and the observation
        // reads the target body's source directly.
        _ = fixture.Server.ApplySession(request: new SessionRequest.Join(Principal: WorldPrincipal.Seat(slot: TargetBody), Slot: TargetBody, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey));
        _ = fixture.Server.ApplySession(request: new SessionRequest.Join(Principal: actor, Slot: actor.Index, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey));

        Laws.RefusalWithControl(
            lawId: "player.mode.acting-principal-drive-required",
            deniedOutcome: () => IdledTargetBody(actor: actor, fixture: fixture),
            controlOutcome: () => {
                // The one discriminating fact reversed: the actor now holds Drive over the target body only.
                fixture.Server.Grant(grant: driveOverTarget, actor: WorldPrincipal.Console);

                return IdledTargetBody(actor: actor, fixture: fixture);
            });
    }

    // ApplyCommand(SetControl) runs the SAME acting-principal Drive gate the fly control application relies on: a
    // denial leaves the body's source untouched (reset to Live first, so the observation is clean either way), an
    // accept latches Idle.
    private static bool IdledTargetBody(WorldFixture fixture, WorldPrincipal actor) {
        fixture.Server.Body(index: TargetBody)!.SetIntentSource(source: IntentSource.Live);
        fixture.Server.ApplyCommand(command: new WorldCommand.SetControl(
            Principal: actor,
            EntityIndex: TargetBody,
            Source: IntentSource.Idle
        ));

        return fixture.Server.Body(index: TargetBody)!.Source.IsIdle;
    }
}
