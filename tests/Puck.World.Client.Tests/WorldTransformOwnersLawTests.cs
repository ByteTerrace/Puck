using System.Numerics;
using Puck.SdfVm;
using Xunit;

namespace Puck.World.Client.Tests;

// An owner's repack reseats its slots exactly when they may hold another owner's pose: its first pack, a pack after it
// vacated them (a spawn into a freed range), and a pose that jumped. A restless owner moving on keeps its motion.
public sealed class WorldTransformOwnersLawTests {
    [Fact]
    public void ZeroTimePacksWakeForNewInputsAndPreservePendingMotionUntilTimeAdvances() {
        var owners = new WorldTransformOwners(capacity: 1);
        var moved = new SdfMovedTransforms();

        bool Wake(float seconds, Vector3 position) {
            moved.Begin(everything: false, tableRows: 1);
            return owners.Wake(castsSoftShadow: true, deltaSeconds: seconds, moved: moved,
                orientation: Quaternion.Identity, owner: 0, position: position);
        }

        Assert.True(condition: Wake(seconds: 0f, position: Vector3.Zero));
        owners.Settle(deltaSeconds: 0f, moved: true, owner: 0);
        Assert.False(condition: Wake(seconds: 0f, position: Vector3.Zero));
        Assert.True(condition: Wake(seconds: 0f, position: Vector3.UnitX));
        owners.Settle(deltaSeconds: 0f, moved: false, owner: 0);
        Assert.False(condition: Wake(seconds: 0f, position: Vector3.UnitX));
        Assert.True(condition: Wake(seconds: 1f, position: Vector3.UnitX));
        owners.Settle(deltaSeconds: 1f, moved: false, owner: 0);
        Assert.False(condition: Wake(seconds: 1f, position: Vector3.UnitX));
        Assert.True(condition: Wake(seconds: 0f, position: Vector3.UnitY));
        owners.Settle(deltaSeconds: 0f, moved: false, owner: 0);
        Assert.False(condition: Wake(seconds: 1f, position: Vector3.UnitY));
    }
    [Fact]
    public void AnOwnerReseatsOnItsFirstPackAfterAVacancyAndOnADiscontinuityOnly() {
        var owners = new WorldTransformOwners(capacity: 2);
        var moved = new SdfMovedTransforms();

        bool Wake(Vector3 position, bool discontinuity = false) {
            moved.Begin(everything: false, tableRows: 8);
            var woke = owners.Wake(castsSoftShadow: true, deltaSeconds: 1f, discontinuity: discontinuity, moved: moved, orientation: Quaternion.Identity, owner: 0, position: position);

            owners.Settle(deltaSeconds: 1f, moved: true, owner: 0);
            return woke;
        }

        Assert.True(condition: Wake(position: Vector3.Zero));
        Assert.True(condition: owners.Reseats(owner: 0));
        Assert.True(condition: Wake(position: Vector3.UnitX));
        Assert.False(condition: owners.Reseats(owner: 0));
        Assert.True(condition: Wake(position: Vector3.UnitY, discontinuity: true));
        Assert.True(condition: owners.Reseats(owner: 0));
        Assert.True(condition: owners.Vacate(moved: moved, owner: 0));
        Assert.True(condition: Wake(position: Vector3.UnitY));
        Assert.True(condition: owners.Reseats(owner: 0));
        Assert.True(condition: Wake(position: Vector3.UnitZ));
        Assert.False(condition: owners.Reseats(owner: 0));
    }
}
