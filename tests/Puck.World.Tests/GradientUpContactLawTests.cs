using Xunit;

using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: under <see cref="WorldContactRequirement.GradientDerivedUp"/>, a body's up axis follows
/// the contact field's gradient, so it GROUNDS on a surface whose normal is steeper (relative to world +Y) than the
/// walkable slope; without the requirement, the identical configuration never grounds there (a steep face pushes
/// but cannot support). WHY THIS LAW EXISTS: the four-world sweep removed the last shipped GradientDerivedUp world,
/// leaving the gradient-up contact path with no exerciser at all — this code-built law keeps the contract alive
/// with zero fixture-rot risk.
/// <para>Ports as an in-process substrate law <see cref="Puck.World.Server.WorldSolidField.TryUp"/> and
/// <see cref="Puck.World.Server.WorldSolidField.Resolve"/>'s own contract (see their class remarks): a body spawned
/// on the ball's steep FLANK (a point whose surface normal sits well past the fixture's <c>maxSlopeDegrees</c>)
/// settles GROUNDED there under <see cref="WorldContactRequirement.GradientDerivedUp"/>, and never grounds under
/// the identical configuration without it — the ONE discriminating fact is the requirement flag
/// (<see cref="Fixtures.BuildGradientUpDocument"/>).</para>
/// </summary>
public sealed class GradientUpContactLawTests {
    // "Still on the flank, not slid to a pole" — the settled horizontal offset is close to
    // Fixtures.BallSurfaceRadius * 0.9 (~2.7 world units) when grounding held on the flank; half the ball's radius
    // is a wide margin against numerical noise while still discriminating that from a pole landing (near-zero
    // horizontal offset). A coarse floor, not a pinned position (README.md red-line 1).
    private const float OffAxisFloor = (Fixtures.BallSurfaceRadius * 0.5f);
    // Chosen by observation (README.md's own instruction): free fall from the fixture's ~0.3-unit spawn clearance
    // under FallGravity 23 settles well inside 100 ticks; this leaves generous headroom for the iterative contact
    // solver's own settle time without materially slowing the suite.
    private const int SettleTicks = 480;

    [Fact]
    public void GradientDerivedUpGroundsTheSteepFlank_FlatUpNeverDoes() {
        var gradientHorizontalOffset = 0f;

        Laws.RefusalWithControl(
            lawId: "collision.gradient-up-grounds-the-flank",
            deniedOutcome: static () => Settle(gradientUp: false).Grounded,
            controlOutcome: () => {
                var settled = Settle(gradientUp: true);

                gradientHorizontalOffset = settled.HorizontalOffset;

                return settled.Grounded;
            });

        Assert.True(
            condition: (gradientHorizontalOffset > OffAxisFloor),
            userMessage: $"gradient-up settled {gradientHorizontalOffset} world units off the ball's axis, expected > {OffAxisFloor} (grounded ON THE FLANK, not after sliding to a pole)"
        );
    }

    // Boots a fresh fixture over Fixtures.BuildGradientUpDocument(gradientUp), joins the ONE seat the fixture
    // relocated onto the ball's flank (mirrors EngageAuthorityLawTests' seat-activation pattern to reach a LIVE
    // body — ActivateSeat only mints one on Join), steps SettleTicks ticks for gravity + contact to settle, and
    // reads back the body's Grounded fact (the honest observable — WorldServer.Body(index) is the same access path
    // EngageAuthorityLawTests documents) plus its horizontal (X-Z) distance from the ball's Y axis.
    private static (bool Grounded, float HorizontalOffset) Settle(bool gradientUp) {
        using var fixture = Fixtures.FreshServer(definition: Fixtures.BuildGradientUpDocument(gradientUp: gradientUp));
        var actor = WorldPrincipal.Seat(slot: Fixtures.GradientUpSeatSlot);

        _ = fixture.Server.ApplySession(request: new SessionRequest.Join(Principal: actor, Slot: actor.Index, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey));

        for (var tick = 0; (tick < SettleTicks); tick++) {
            fixture.Step();
        }

        var body = fixture.Server.Body(index: actor.Index)!;
        var position = body.Position;

        return (body.Grounded, MathF.Sqrt(x: ((position.X * position.X) + (position.Z * position.Z))));
    }
}
