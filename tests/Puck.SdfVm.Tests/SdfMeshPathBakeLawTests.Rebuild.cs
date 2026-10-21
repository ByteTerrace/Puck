using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfMeshPathBakeLawTests {
    // A program rebuilt while a skinned group's member stands off the pose its mesh was baked at (a body breathing when a
    // placement elsewhere changes) keeps the group: the same mesh draws on at once and nothing bakes, where planning the
    // group afresh at the member's new pose would key another geometry and bake it.
    [Fact]
    public void ARebuiltProgramKeepsASkinnedGroupWhoseMemberStandsElsewhere() {
        var bake = new SdfMeshPathBake { Library = new SdfMeshLibrary(), Residency = "rebuild" };
        var index = 0L;

        DriveSettle(bake: bake, frame: SettleFrame(groups: 1, offset: 0.35f, program: SettleProgram(groups: 1), time: 0f), index: ref index);
        var baked = Assert.Single(collection: bake.Dress(frame: SettleFrame(groups: 1, offset: 0.35f, program: bake.Program!, time: 0.1f)).MeshDraws).Mesh;

        _ = bake.Dress(frame: SettleFrame(groups: 1, offset: 0.353f, program: bake.Program!, time: 0.2f));
        var draws = bake.Dress(frame: SettleFrame(groups: 1, offset: 0.353f, program: SettleProgram(groups: 1), time: 0.3f)).MeshDraws;

        Assert.Same(expected: baked, actual: Assert.Single(collection: draws).Mesh);
        Assert.False(condition: bake.Pending);
    }
}
