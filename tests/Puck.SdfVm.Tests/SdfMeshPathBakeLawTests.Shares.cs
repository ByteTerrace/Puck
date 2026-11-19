using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

// Two residencies holding one library that plan the same geometry before either lands it bake it once
// (SdfMeshBakeShare): the second waits on the first's bake, and starts it itself only when the first can no longer land it.
public sealed partial class SdfMeshPathBakeLawTests {
    [Fact]
    public void AGroupAnotherResidencyIsBakingWaitsOnThatBakeAndTakesItsMesh() {
        using var library = new SdfMeshLibrary();
        using var leaderRun = new MeshBakeTestRun(new SdfMeshPathBake { Library = library, Residency = "leader" }, ownsLibrary: false);
        using var waiterRun = new MeshBakeTestRun(new SdfMeshPathBake { Library = library, Residency = "waiter" }, ownsLibrary: false);
        var (leaderProgram, materials) = Program(shapes: Shapes);
        var (waiterProgram, _) = Program(shapes: Shapes);
        var leaderFrame = new SdfFrame(Program: leaderProgram, ProgramChanged: true, Time: 0f, Views: []) { FarDistance = 100f };
        var waiterFrame = new SdfFrame(Program: waiterProgram, ProgramChanged: true, Time: 0f, Views: []) { FarDistance = 100f };

        _ = leaderRun.Dresser.Dress(frame: leaderFrame);
        _ = waiterRun.Dresser.Dress(frame: waiterFrame);

        var groups = library.Shares.Read(kind: SdfMeshBakeShare.Joined);

        Assert.True(condition: (groups > 0), userMessage: waiterRun.Bake.Describe());
        Assert.False(condition: waiterRun.Bake.TryTake(batch: out _, field: waiterProgram));

        Drive(bake: leaderRun.Bake, dresser: leaderRun.Dresser, frame: leaderFrame, materials: materials, shapes: Shapes);
        TestLiveness.Until(reason: waiterRun.Bake.Describe, step: () => {
            _ = waiterRun.Dresser.Dress(frame: waiterFrame);
            return !waiterRun.Bake.Pending;
        });

        Assert.Contains(expectedSubstring: "dispatches=0 ", actualString: waiterRun.Bake.Describe());
        Assert.Equal(expected: groups, actual: library.Shares.Read(kind: SdfMeshBakeShare.Landed));
        Assert.True(condition: (library.Shares.Read(kind: SdfMeshBakeShare.SamplesSaved) > 0));

        var led = leaderRun.Dresser.Dress(frame: leaderFrame).DressedDraws().Select(selector: static draw => draw.Mesh).ToHashSet(comparer: ReferenceEqualityComparer.Instance);

        Assert.All(collection: waiterRun.Dresser.Dress(frame: waiterFrame).DressedDraws(), action: draw => Assert.Contains(expected: draw.Mesh, collection: led));
    }
    // A leader retired mid-bake (its residency released, or a program that drops the group) hands each waiter back, and the
    // waiter bakes the geometry itself.
    [Fact]
    public void AWaiterWhoseLeaderRetiresMidBakeBakesItsGeometryItself() {
        using var library = new SdfMeshLibrary();
        using var waiterRun = new MeshBakeTestRun(new SdfMeshPathBake { Library = library, Residency = "waiter" }, ownsLibrary: false);
        var (leaderProgram, materials) = Program(shapes: Shapes);
        var (waiterProgram, _) = Program(shapes: Shapes);
        var waiterFrame = new SdfFrame(Program: waiterProgram, ProgramChanged: true, Time: 0f, Views: []) { FarDistance = 100f };

        using (var leaderRun = new MeshBakeTestRun(new SdfMeshPathBake { Library = library, Residency = "leader" }, ownsLibrary: false)) {
            _ = leaderRun.Dresser.Dress(frame: new SdfFrame(Program: leaderProgram, ProgramChanged: true, Time: 0f, Views: []) { FarDistance = 100f });
            _ = waiterRun.Dresser.Dress(frame: waiterFrame);
            Assert.True(condition: (library.Shares.Read(kind: SdfMeshBakeShare.Joined) > 0));
        }

        Drive(bake: waiterRun.Bake, dresser: waiterRun.Dresser, frame: waiterFrame, materials: materials, shapes: Shapes);

        Assert.Equal(expected: library.Shares.Read(kind: SdfMeshBakeShare.Joined), actual: library.Shares.Read(kind: SdfMeshBakeShare.Abandoned));
        Assert.DoesNotContain(expectedSubstring: "dispatches=0 ", actualString: waiterRun.Bake.Describe());
        Assert.NotEmpty(collection: waiterRun.Dresser.Dress(frame: waiterFrame).DressedDraws());
    }
    // A leader whose residency no frame renders is never asked for a lattice, while the waiter's bake is: the waiter deserts
    // it and bakes the geometry itself rather than wait on a bake that never lands.
    [Fact]
    public void AWaiterDesertsALeaderNoFrameDrivesAndBakesItsGeometryItself() {
        using var library = new SdfMeshLibrary();
        using var leaderRun = new MeshBakeTestRun(new SdfMeshPathBake { Library = library, Residency = "hidden" }, ownsLibrary: false);
        using var waiterRun = new MeshBakeTestRun(new SdfMeshPathBake { Library = library, Residency = "shown" }, ownsLibrary: false);
        var (leaderProgram, materials) = Program(shapes: Shapes);
        var (waiterProgram, _) = Program(shapes: Shapes);
        var waiterFrame = new SdfFrame(Program: waiterProgram, ProgramChanged: true, Time: 0f, Views: []) { FarDistance = 100f };

        _ = leaderRun.Dresser.Dress(frame: new SdfFrame(Program: leaderProgram, ProgramChanged: true, Time: 0f, Views: []) { FarDistance = 100f });
        _ = waiterRun.Dresser.Dress(frame: waiterFrame);
        Assert.True(condition: (library.Shares.Read(kind: SdfMeshBakeShare.Joined) > 0));

        Drive(bake: waiterRun.Bake, dresser: waiterRun.Dresser, frame: waiterFrame, materials: materials, shapes: Shapes);

        Assert.Equal(expected: library.Shares.Read(kind: SdfMeshBakeShare.Joined), actual: library.Shares.Read(kind: SdfMeshBakeShare.Stalled));
        Assert.True(condition: leaderRun.Bake.Pending);
        Assert.NotEmpty(collection: waiterRun.Dresser.Dress(frame: waiterFrame).DressedDraws());
    }
}
