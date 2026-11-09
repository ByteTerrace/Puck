using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed class MovingBlendLawTests {
    [Fact]
    public void AThirdOwnerRefusesTheSkinAndBakesRigidPieces() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.BeginInstanceDynamic(slot: 0, boundOffset: Vector3.Zero, boundRadius: 0.1f, restPoseSkin: true);
        _ = builder.ResetPoint().TransformDynamic(slot: 0).Sphere(radius: 0.03f, material: material);
        _ = builder.ResetPoint().TransformDynamic(slot: 1).Sphere(radius: 0.03f, material: material, blend: SdfBlendOp.SmoothUnion, smooth: 0.04f);
        _ = builder.ResetPoint().TransformDynamic(slot: 2).Sphere(radius: 0.04f, material: material, blend: SdfBlendOp.SmoothUnion, smooth: 0.04f);
        var program = builder.EndInstance().Build();
        var group = Assert.Single(collection: SdfMeshBakeGroup.Plan<SdfMeshBakeGroup>(program: program, transforms: Enumerable.Repeat(new DynamicTransform(Vector3.Zero, Quaternion.Identity, Vector4.Zero), 3).ToArray(), farDistance: 1f, create: static (selector, dynamic) => new(dynamic: dynamic, selector: selector)));
        var mesh = new SdfMesh(positions: new Vector3[] { new(x: 0f, y: 0.03f, z: 0f), new(x: 0f, y: 0.03f, z: 0.001f), new(x: 0f, y: 0.031f, z: 0f) }, indices: new uint[] { 0, 1, 2 });

        Assert.Null(@object: SdfMeshSkin.Build(mesh: mesh, refusal: out var refusal, skin: group.Skin!));
        Assert.Equal(actual: refusal, expected: "third owner share exceeds 1/64");
        var split = SdfMeshSkin.Split(skin: group.Skin!);

        Assert.NotNull(@object: split);
        Assert.Empty(collection: split.Influences.ToArray());
        Assert.Equal(3, split.VertexMembers.ToArray().Distinct().Count());
    }
    [Fact]
    public void NestedSharesFollowLeavesAndScopes() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Vector3.One));

        _ = builder.PushField().ResetPoint().Sphere(1f, material);
        _ = builder.ResetPoint().Sphere(1f, material, SdfBlendOp.SmoothUnion, 1f).PopField();
        _ = builder.PushField(compose: SdfBlendOp.SmoothUnion, smooth: 1f).ResetPoint().Sphere(1.25f, material).PopField();
        var field = new SdfFieldEvaluator(program: builder.Build(), reading: SdfFieldReading.Render);
        var shares = new FixedQ4816[3];

        Assert.True(condition: field.ReadInfluences(position: FixedPosition.FromLocal(local: FixedVector3.UnitX), shapeOwners: [0, 1, 2], shares: shares));
        Assert.Equal(new[] { FixedQ4816.FromDouble(value: 0.25), FixedQ4816.FromDouble(value: 0.25), FixedQ4816.FromDouble(value: 0.5) }, shares);
    }

    private static SdfProgram Program() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Vector3.One));

        _ = builder.BeginInstanceDynamic(0, Vector3.Zero, 0.1f, restPoseSkin: true);
        _ = builder.ResetPoint().TransformDynamic(slot: 0).Translate(offset: new(x: -0.01f, y: 0f, z: 0f)).Sphere(0.03f, material);
        _ = builder.ResetPoint().TransformDynamic(slot: 1).Translate(offset: new(x: 0.01f, y: 0f, z: 0f)).Sphere(0.03f, material, SdfBlendOp.SmoothUnion, 0.04f);
        return builder.EndInstance().Build();
    }
    private static SdfMeshBakeGroup Group(SdfProgram program, DynamicTransform member) => Assert.Single(collection: SdfMeshBakeGroup.Plan<SdfMeshBakeGroup>(program, [new(Vector3.Zero, Quaternion.Identity, Vector4.Zero), member], 1f, static (selector, dynamic) => new(dynamic: dynamic, selector: selector)));

    [Fact]
    public void ArrivalPoseCannotChangeTheRestGeometryOrItsMembers() {
        var program = Program();
        var rest = Group(program, new(Vector3.Zero, Quaternion.Identity, Vector4.Zero));
        var moved = Group(program, new(new(x: 0.2f, y: 0.1f, z: -0.3f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.8f), Vector4.Zero));

        Assert.Equal(rest.Sealed, moved.Sealed);
        Assert.Equal(rest.Form, moved.Form);
        Assert.All(moved.Geometry.Poses.Values, pose => { Assert.Equal(Vector3.Zero, pose.Position); Assert.Equal(Quaternion.Identity, pose.Orientation); });
    }
    [Fact]
    public void CopiesShareTuplesAndTheStoreAndMergePreserveThem() {
        var program = Program();
        var group = Group(program, new(Vector3.Zero, Quaternion.Identity, Vector4.Zero));
        Vector3[] positions = [new(x: 0f, y: 0.03f, z: 0f), new(x: 0f, y: 0.03f, z: 0.001f), new(x: 0f, y: 0.031f, z: 0f), new(x: 0f, y: 0.03f, z: 0f)];
        var mesh = new SdfMesh(positions, new uint[] { 0, 1, 2, 3, 2, 1 }, Enumerable.Repeat(Vector3.UnitY, 4).ToArray());
        var skin = SdfMeshSkin.Build(mesh: mesh, refusal: out var refusal, skin: group.Skin!);

        Assert.Null(@object: refusal);
        Assert.NotNull(@object: skin);
        Assert.Equal(skin.VertexMembers.Span[0], skin.VertexMembers.Span[3]);
        Assert.Equal(skin.Influences.Span[0], skin.Influences.Span[3]);
        Assert.Equal((4, 0), SdfMeshSkin.Audit(mesh: skin));
        var merged = SdfMeshPlanarMerge.Merge(mesh: skin);

        Assert.Equal(skin.Positions.ToArray(), merged.Positions.ToArray());
        Assert.Equal(skin.Influences.ToArray(), merged.Influences.ToArray());
        using var scratch = new TemporaryDirectory("puck-moving-blend-");
        var store = new SdfMeshStore(scratch.PathOf(name: "meshes"));

        Assert.True(condition: store.Write(1, group.Sealed, skin, [0]));
        Assert.True(condition: store.TryRead(1, group.Sealed, out var read, out _, out _));
        Assert.NotNull(@object: read);
        Assert.Equal(skin.Influences.ToArray(), read.Influences.ToArray());
        Assert.Equal(0, SdfMeshSkin.Audit(mesh: read).SplitPlaces);
    }
    [Fact]
    public void TranslationBendsTheNormalAndQuaternionSignsDoNotChangeThePose() {
        var influence = SdfMeshSkinTuple.Of(first: SdfMesh.NoMember, gradient: Vector3.UnitX, second: 0, share: 32768).Influence;
        var mesh = new SdfMesh(new Vector3[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }, new uint[] { 0, 1, 2 }, Enumerable.Repeat(Vector3.UnitY, 3).ToArray(), members: new SdfMeshMember[] { new(1, Vector3.Zero, Quaternion.Identity) }, vertexMembers: new byte[] { 255, 255, 255 }, influences: Enumerable.Repeat(count: 3, element: influence).ToArray());
        var positive = SdfMeshSkin.Pose(mesh: mesh, poses: new Dictionary<int, DynamicTransform> { [1] = new((Vector3.UnitY * 0.1f), Quaternion.Identity, Vector4.Zero) });
        var negative = SdfMeshSkin.Pose(mesh: mesh, poses: new Dictionary<int, DynamicTransform> { [1] = new((Vector3.UnitY * 0.1f), Quaternion.Negate(value: Quaternion.Identity), Vector4.Zero) });

        Assert.Equal(positive.Positions.ToArray(), negative.Positions.ToArray());
        Assert.True(condition: (positive.Normals.Span[0].X < -0.09f));
        Assert.InRange(positive.Positions.Span[0].Y, 0.0499f, 0.0501f);
    }
}
