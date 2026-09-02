using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// Laws for <see cref="WorldRowStepWindowGuard"/> — the read-your-writes guard behind <c>world.row.step</c>. A step
/// composes a WHOLE-ROW upsert from the pre-drain definition, so two steps to the SAME row inside one tick window
/// (before the buffered mutations drain) collide and the later reverts the earlier. The guard refuses the second by
/// name; steps to DIFFERENT rows, and repeats of one row across DIFFERENT windows (a held chord once per tick), never
/// collide. Every claim law pairs the collision with a one-input-different passing control.
/// </summary>
public sealed class WorldRowStepWindowGuardLawTests {
    [Fact]
    public void SameRow_SameWindow_SecondClaimCollides() {
        var guard = new WorldRowStepWindowGuard();

        Assert.False(condition: guard.IsClaimed(rowIdentity: "render", window: 5UL));
        guard.Claim(rowIdentity: "render");
        // A second step to the same row in the same window is a collision — the whole-row upsert would stomp the first.
        Assert.True(condition: guard.IsClaimed(rowIdentity: "render", window: 5UL));
    }
    [Fact]
    public void SameWindow_DifferentRows_DoNotCollide() {
        var guard = new WorldRowStepWindowGuard();

        Assert.False(condition: guard.IsClaimed(rowIdentity: "creations.a", window: 5UL));
        guard.Claim(rowIdentity: "creations.a");
        // A different row's whole-row upsert composes into the same candidate without loss, so it is allowed.
        Assert.False(condition: guard.IsClaimed(rowIdentity: "creations.b", window: 5UL));
    }
    [Fact]
    public void SameRow_AcrossWindows_NeverCollides_HeldChordKeepsStepping() {
        var guard = new WorldRowStepWindowGuard();

        // A held chord fires world.row.step once per tick — each fire lands in a new window (NextInputTick advances as
        // the prior step's mutation drains), so none is ever refused.
        for (var window = 1UL; (window <= 8UL); window++) {
            Assert.False(condition: guard.IsClaimed(rowIdentity: "render", window: window));
            guard.Claim(rowIdentity: "render");
        }
    }
    [Fact]
    public void UnclaimedProbe_DoesNotBlockRetry_InSameWindow() {
        var guard = new WorldRowStepWindowGuard();

        // IsClaimed alone never claims — a step that probes but then refuses for another reason (bad field, overflow)
        // submits nothing and must not block a corrected retry to the same row in the same window.
        Assert.False(condition: guard.IsClaimed(rowIdentity: "render", window: 5UL));
        Assert.False(condition: guard.IsClaimed(rowIdentity: "render", window: 5UL));
        // Once one genuinely commits, the next in the window collides.
        guard.Claim(rowIdentity: "render");
        Assert.True(condition: guard.IsClaimed(rowIdentity: "render", window: 5UL));
    }
    [Fact]
    public void WindowAdvance_ClearsPriorClaims() {
        var guard = new WorldRowStepWindowGuard();

        Assert.False(condition: guard.IsClaimed(rowIdentity: "render", window: 1UL));
        guard.Claim(rowIdentity: "render");
        Assert.True(condition: guard.IsClaimed(rowIdentity: "render", window: 1UL));
        // The next window starts empty.
        Assert.False(condition: guard.IsClaimed(rowIdentity: "render", window: 2UL));
    }
}
