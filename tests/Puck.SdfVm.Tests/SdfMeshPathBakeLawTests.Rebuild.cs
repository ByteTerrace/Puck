using System.Numerics;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfMeshPathBakeLawTests {
    [Fact]
    public void ACachedReplacementDrawsItsOwnMeshWithoutWaitingForAnotherInvalidation() {
        var library = new SdfMeshLibrary();

        SdfProgram Cached(float x, out SdfMesh mesh) {
            var builder = new SdfProgramBuilder();
            var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
            var program = builder.Translate(offset: new Vector3(x: x, y: 0, z: 0)).Sphere(radius: 0.5f, material: material).Build();

            mesh = new SdfMesh(positions: new Vector3[] { new(x: x, y: 0, z: 0), new(x: (x + 1), y: 0, z: 0), new(x: x, y: 1, z: 0) }, indices: new uint[] { 0, 1, 2 });
            foreach (var group in SdfMeshBakeGroup.Plan(program: program, transforms: [], farDistance: 100,
                create: static (selector, dynamic) => new SdfMeshBakeGroup(dynamic: dynamic, selector: selector))) {
                library.Store(geometry: group.Sealed, materials: group.MaterialOrder, mesh: mesh);
            }
            return program;
        }
        var first = Cached(mesh: out var firstMesh, x: 0);
        var second = Cached(mesh: out var secondMesh, x: 5);
        var bake = new SdfMeshPathBake { Library = library };
        var frame = new SdfFrame(Program: first, ProgramChanged: true, Time: 0, Views: []) { FarDistance = 100 };
        var firstDraw = bake.Dress(frame: frame);
        var revision = firstDraw.MeshDrawsRevision;

        Assert.Same(expected: firstMesh, actual: Assert.Single(collection: firstDraw.MeshDraws).Mesh);
        var replacement = bake.Dress(frame: frame with { Program = second });

        Assert.Same(expected: secondMesh, actual: Assert.Single(collection: replacement.MeshDraws).Mesh);
        Assert.True(condition: (replacement.MeshDrawsRevision > revision));
        Assert.False(condition: bake.Pending);
    }
    // A program rebuilt after the bake's identity moved (an applied update to the code the bake runs, a reloaded bake
    // kernel) keeps no group an earlier identity meshed, and neither it nor another residency takes a mesh the library
    // holds under that identity: each group bakes again under the identity standing, drawing its earlier mesh meanwhile.
    [Fact]
    public void AMovedIdentityKeepsAndTakesNothingAnEarlierIdentityMeshed() {
        var library = new SdfMeshLibrary();

        static SdfProgram Sphere() {
            var builder = new SdfProgramBuilder();
            var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

            return builder.Sphere(radius: 0.5f, material: material).Build();
        }

        var built = new SdfMeshBakeIdentity(value: 3);
        var moved = new SdfMeshBakeIdentity(value: 7);
        var mesh = new SdfMesh(positions: new Vector3[] { new(x: 0, y: 0, z: 0), new(x: 1, y: 0, z: 0), new(x: 0, y: 1, z: 0) }, indices: new uint[] { 0, 1, 2 });
        var first = Sphere();

        foreach (var group in SdfMeshBakeGroup.Plan(program: first, transforms: [], farDistance: 100,
            create: static (selector, dynamic) => new SdfMeshBakeGroup(dynamic: dynamic, selector: selector))) {
            library.Store(geometry: group.Sealed, identity: built.Value, materials: group.MaterialOrder, mesh: mesh);
        }

        var bake = new SdfMeshPathBake { Identity = built, Library = library, Residency = "built" };
        var frame = new SdfFrame(Program: first, ProgramChanged: true, Time: 0, Views: []) { FarDistance = 100 };

        Assert.Same(expected: mesh, actual: Assert.Single(collection: bake.Dress(frame: frame).MeshDraws).Mesh);
        Assert.False(condition: bake.Pending);

        // The same identity keeps the group through a rebuilt program, the control for what follows.
        Assert.Same(expected: mesh, actual: Assert.Single(collection: bake.Dress(frame: (frame with { Program = Sphere() })).MeshDraws).Mesh);
        Assert.False(condition: bake.Pending);

        bake.Identity = moved;

        var rebuilt = bake.Dress(frame: (frame with { Program = Sphere() }));

        Assert.True(condition: bake.Pending);
        Assert.Same(expected: mesh, actual: Assert.Single(collection: rebuilt.MeshDraws).Mesh);

        var arriving = new SdfMeshPathBake { Identity = moved, Library = library, Residency = "arriving" };

        _ = arriving.Dress(frame: frame);
        Assert.True(condition: arriving.Pending);
    }
    // A skinned group's mesh is the bake identity's too: a moved identity neither keeps the group nor renumbers it.
    [Fact]
    public void AMovedIdentityRebakesASkinnedGroupItWouldOtherwiseKeep() {
        var bake = new SdfMeshPathBake { Library = new SdfMeshLibrary(), Residency = "skinned" };
        var index = 0L;

        DriveSettle(bake: bake, frame: SettleFrame(groups: 1, offset: 0.35f, program: SettleProgram(groups: 1, leadingMaterials: 1), time: 0f), index: ref index);
        _ = bake.Dress(frame: SettleFrame(groups: 1, offset: 0.35f, program: bake.Program!, time: 0.1f));
        Assert.False(condition: bake.Pending);

        bake.Identity = new SdfMeshBakeIdentity(value: 7);
        _ = bake.Dress(frame: SettleFrame(groups: 1, offset: 0.353f, program: SettleProgram(groups: 1), time: 0.2f));

        Assert.True(condition: bake.Pending);
    }
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
    // A program its build planned ahead (PlanAhead) at the transforms it was emitted under restarts from that plan in the
    // frame that first draws it, with the member standing elsewhere by then: the group keeps its mesh and nothing bakes,
    // as when the frame plans the program itself.
    [Fact]
    public void AProgramPlannedAheadRestartsFromItsPlanAndKeepsTheGroup() {
        var bake = new SdfMeshPathBake { Library = new SdfMeshLibrary(), Residency = "ahead" };
        var index = 0L;

        DriveSettle(bake: bake, frame: SettleFrame(groups: 1, offset: 0.35f, program: SettleProgram(groups: 1), time: 0f), index: ref index);
        var baked = Assert.Single(collection: bake.Dress(frame: SettleFrame(groups: 1, offset: 0.35f, program: bake.Program!, time: 0.1f)).MeshDraws).Mesh;
        var program = SettleProgram(groups: 1);
        var drawn = SettleFrame(groups: 1, offset: 0.353f, program: program, time: 0.2f);

        bake.PlanAhead(
            farDistance: drawn.FarDistance,
            program: program,
            transforms: SettleFrame(groups: 1, offset: 0.35f, program: program, time: 0.15f).DynamicTransforms
        );
        var draws = bake.Dress(frame: drawn).MeshDraws;

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

        Assert.NotSame(actual: renumbered, expected: baked);
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
