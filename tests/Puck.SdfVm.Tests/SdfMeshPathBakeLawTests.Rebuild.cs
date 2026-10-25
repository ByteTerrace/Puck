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
    // A program rebuilt without a palette registered ahead of a skinned group's (a body leaving the world) numbers the
    // group's material another id while its member stands off the baked pose: the group keeps its mesh, renumbered place
    // for place, and nothing bakes, where planning it afresh would key another geometry and bake it.
    [Fact]
    public void ARebuiltProgramWhoseEarlierPaletteLeftKeepsASkinnedGroupRenumbered() {
        var bake = new SdfMeshPathBake { Library = new SdfMeshLibrary(), Residency = "renumber" };
        var index = 0L;

        DriveSettle(bake: bake, frame: SettleFrame(groups: 1, offset: 0.35f, program: SettleProgram(groups: 1, leadingMaterials: 1), time: 0f), index: ref index);
        var baked = Assert.Single(collection: bake.Dress(frame: SettleFrame(groups: 1, offset: 0.35f, program: bake.Program!, time: 0.1f)).MeshDraws).Mesh;
        var draws = bake.Dress(frame: SettleFrame(groups: 1, offset: 0.353f, program: SettleProgram(groups: 1), time: 0.2f)).MeshDraws;
        var renumbered = Assert.Single(collection: draws).Mesh;

        Assert.NotSame(expected: baked, actual: renumbered);
        Assert.True(condition: baked.Positions.Span.SequenceEqual(other: renumbered.Positions.Span));
        Assert.Equal(expected: baked.TriangleCount, actual: renumbered.TriangleCount);
        Assert.False(condition: bake.Pending);
    }
    // A skinned group arriving in another residency that shares the library (a body crossing into another world) with
    // its member at a pose no bake keyed takes the mesh its form baked at the other pose: nothing bakes on arrival, and
    // the group still settles once its member holds still away from the pose that mesh was baked at.
    [Fact]
    public void ASkinnedGroupArrivingAtAnUnbakedPoseTakesItsFormsMeshAndSettlesLater() {
        var library = new SdfMeshLibrary();
        var home = new SdfMeshPathBake { Library = library, Residency = "home" };
        var index = 0L;

        DriveSettle(bake: home, frame: SettleFrame(groups: 1, offset: 0.35f, program: SettleProgram(groups: 1), time: 0f), index: ref index);
        var baked = Assert.Single(collection: home.Dress(frame: SettleFrame(groups: 1, offset: 0.35f, program: home.Program!, time: 0.1f)).MeshDraws).Mesh;
        var away = new SdfMeshPathBake { Library = library, Residency = "away" };
        var program = SettleProgram(groups: 1);
        var arrived = away.Dress(frame: SettleFrame(groups: 1, offset: 0.36f, program: program, time: 0f)).MeshDraws;

        Assert.Same(expected: baked, actual: Assert.Single(collection: arrived).Mesh);
        Assert.False(condition: away.Pending);

        var settled = SettleFrame(groups: 1, offset: 0.5f, program: program, time: 1f);

        _ = away.Dress(frame: settled);
        _ = away.Dress(frame: (settled with { Time = (1f + (2f * SdfMeshSettle.SettleSeconds)) }));
        Assert.True(condition: away.Pending);
    }
}
