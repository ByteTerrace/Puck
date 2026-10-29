using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a toast narrates what a player's own act caused. An operator's grant or mutation (the
/// console principal), the world's own program and an applied addon edit raise none; a seat, peer or session
/// viewer's act, a refused addon edit and a grantless rebuild outcome do; a scoped replay toasts only a refusal.
/// </summary>
public sealed class WorldToastPolicyLawTests {
    private static WorldEditEcho Grant(Principal actor, bool rejected = false, int connectionId = SubmissionEnvelope.LocalConnectionId) => new(
        Message: "grant",
        Rejected: rejected,
        Kind: WorldEditEchoKind.GrantTable,
        ConnectionId: connectionId,
        Actor: actor
    );

    [Fact]
    public void PlayerActsToast() {
        Assert.True(WorldToastPolicy.Narrates(echo: Grant(actor: Principal.Seat(slot: 0))));
        Assert.True(WorldToastPolicy.Narrates(echo: Grant(actor: Principal.Peer(index: 3, generation: 1))));
        Assert.True(WorldToastPolicy.Narrates(echo: Grant(actor: Principal.Session(epoch: 1, ordinal: 2))));
        Assert.True(WorldToastPolicy.Narrates(echo: Grant(actor: Principal.Seat(slot: 0), rejected: true)));
    }
    [Fact]
    public void OperatorAndWorldActsDoNotToast() {
        Assert.False(WorldToastPolicy.Narrates(echo: Grant(actor: Principal.Console)));
        Assert.False(WorldToastPolicy.Narrates(echo: Grant(actor: Principal.Console, rejected: true)));
        Assert.False(WorldToastPolicy.Narrates(echo: Grant(actor: Principal.World)));
    }
    [Fact]
    public void MutationActorIsItsPrincipal() {
        WorldEditEcho Of(Principal principal) => new(
            Message: "mutation",
            Rejected: false,
            Kind: WorldEditEchoKind.Mutation,
            Mutation: new WorldMutation.RemoveKit(Principal: principal, Name: "kit")
        );

        Assert.False(WorldToastPolicy.Narrates(echo: Of(principal: Principal.Console)));
        Assert.True(WorldToastPolicy.Narrates(echo: Of(principal: Principal.Seat(slot: 1))));
    }
    [Fact]
    public void AddonEditsToastOnlyWhenRefused() {
        var addon = Principal.Addon(name: "guide");

        Assert.False(WorldToastPolicy.Narrates(echo: Grant(actor: addon)));
        Assert.True(WorldToastPolicy.Narrates(echo: Grant(actor: addon, rejected: true)));
    }
    [Fact]
    public void OutcomeWithoutActorToasts() => Assert.True(WorldToastPolicy.Narrates(echo: new(
        Message: "rebuilt",
        Rejected: false,
        Kind: WorldEditEchoKind.Rebuild
    )));
    [Fact]
    public void ScopedReplayToastsOnlyRefusals() {
        Assert.False(WorldToastPolicy.Narrates(echo: Grant(actor: Principal.Seat(slot: 0), connectionId: SubmissionEnvelope.ScopedReplayConnectionId)));
        Assert.True(WorldToastPolicy.Narrates(echo: Grant(actor: Principal.Seat(slot: 0), rejected: true, connectionId: SubmissionEnvelope.ScopedReplayConnectionId)));
    }
}
