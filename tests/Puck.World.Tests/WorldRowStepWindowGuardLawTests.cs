using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// Laws for <see cref="WorldRowStepWindowGuard"/> — the read-your-writes guard behind <c>world.row.step</c>. A step
/// composes a WHOLE-ROW upsert from the pre-drain definition, so two steps to the SAME row inside one tick window of one
/// world (before the buffered mutations drain) collide and the later reverts the earlier. The guard refuses the second
/// by name; steps to DIFFERENT rows, repeats of one row across DIFFERENT windows (a held chord once per tick), and
/// steps in different worlds or in a world recreated under the same name never collide. Every claim law pairs the
/// collision with a one-input-different passing control.
/// </summary>
public sealed class WorldRowStepWindowGuardLawTests {
    private static readonly Guid Activation = Guid.Parse(input: "0f0e0d0c-0b0a-0908-0706-050403020100");

    private static WorldRowStepWindow At(ulong tick, string authority = "boot", Guid? activation = null) => new(
        Activation: (activation ?? Activation),
        Authority: authority,
        Tick: tick
    );

    [Fact]
    public void SameRow_AcrossWindows_NeverCollides_HeldChordKeepsStepping() {
        var guard = new WorldRowStepWindowGuard();

        // A held chord fires world.row.step once per tick — each fire lands in a new window (NextInputTick advances as
        // the prior step's mutation drains), so none is ever refused.
        for (var tick = 1UL; (tick <= 8UL); tick++) {
            Assert.False(condition: guard.IsClaimed(
                rowIdentity: "render",
                window: At(tick: tick)
            ));
            guard.Claim(rowIdentity: "render", window: At(tick: tick));
        }
    }
    // A second step to the same row in the same window is a collision — the whole-row upsert would stomp the first;
    // a different row's whole-row upsert composes into the same candidate without loss, so it is allowed.
    [InlineData("render", "render", true)]
    [InlineData("creations.a", "creations.b", false)]
    [Theory]
    public void WithinOneWindow_OnlyTheSameRowCollides(string claimed, string probed, bool collides) {
        var guard = new WorldRowStepWindowGuard();

        Assert.False(condition: guard.IsClaimed(
            rowIdentity: claimed,
            window: At(tick: 5UL)
        ));
        guard.Claim(rowIdentity: claimed, window: At(tick: 5UL));
        Assert.Equal(
            expected: collides,
            actual: guard.IsClaimed(
                rowIdentity: probed,
                window: At(tick: 5UL)
            )
        );
    }
    [Fact]
    public void UnclaimedProbe_DoesNotBlockRetry_InSameWindow() {
        var guard = new WorldRowStepWindowGuard();

        // IsClaimed alone never claims — a step that probes but then refuses for another reason (bad field, overflow)
        // submits nothing and must not block a corrected retry to the same row in the same window.
        Assert.False(condition: guard.IsClaimed(
            rowIdentity: "render",
            window: At(tick: 5UL)
        ));
        Assert.False(condition: guard.IsClaimed(
            rowIdentity: "render",
            window: At(tick: 5UL)
        ));
        // Once one genuinely commits, the next in the window collides.
        guard.Claim(rowIdentity: "render", window: At(tick: 5UL));
        Assert.True(condition: guard.IsClaimed(
            rowIdentity: "render",
            window: At(tick: 5UL)
        ));
    }
    [Fact]
    public void WindowAdvance_ClearsPriorClaims() {
        var guard = new WorldRowStepWindowGuard();

        guard.Claim(rowIdentity: "render", window: At(tick: 1UL));
        Assert.True(condition: guard.IsClaimed(
            rowIdentity: "render",
            window: At(tick: 1UL)
        ));
        // The next window starts empty.
        Assert.False(condition: guard.IsClaimed(
            rowIdentity: "render",
            window: At(tick: 2UL)
        ));
    }
    [Fact]
    public void AnotherWorldOrActivationNeverInheritsAClaimNorErasesOne() {
        var guard = new WorldRowStepWindowGuard();
        var other = Guid.NewGuid();

        guard.Claim(rowIdentity: "placements.crate1", window: At(tick: 1UL));

        // Another world, and another activation of the same authority (a world recreated under its name, or two instances
        // declaring one authority), at the same tick, hold none of the claim.
        Assert.False(condition: guard.IsClaimed(rowIdentity: "placements.crate1", window: At(authority: "north", tick: 1UL)));
        Assert.False(condition: guard.IsClaimed(rowIdentity: "placements.crate1", window: At(activation: other, tick: 1UL)));

        // Red leg: seeing another activation of the same authority leaves the first activation's claim standing.
        Assert.True(condition: guard.IsClaimed(rowIdentity: "placements.crate1", window: At(tick: 1UL)));
    }
    [Fact]
    public void AStoppedActivationsClaimsAreDropped() {
        var guard = new WorldRowStepWindowGuard();
        using var stopping = new CancellationTokenSource();
        var window = At(tick: 1UL) with { Retired = stopping.Token };

        guard.Claim(rowIdentity: "placements.crate1", window: window);
        guard.Claim(rowIdentity: "placements.crate1", window: At(authority: "north", tick: 1UL));

        // Red leg: while the activation runs, its claim set is held.
        Assert.Equal(actual: guard.Worlds, expected: 2);
        stopping.Cancel();
        Assert.Equal(actual: guard.Worlds, expected: 1);
    }
}
