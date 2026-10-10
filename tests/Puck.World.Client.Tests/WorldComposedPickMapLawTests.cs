using System.Numerics;
using Puck.SdfVm;
using Puck.SignedDistance;
using Xunit;

namespace Puck.World.Client.Tests;

[Collection(SceneProbeCollection.Name)]
public sealed class WorldComposedPickMapLawTests {
    [InlineData(0x40000001U, 0)]
    [InlineData(0x40000002U, 1)]
    [InlineData(0x80000000U, 0)]
    [InlineData(0x80000001U, 1)]
    [Theory]
    public void CapturedMaterialNamesFollowTheSameComposedOwnerAsTheHit(uint identity, int owner) {
        var emitters = new NamedEmitter[] { new(name: "first"), new(name: "second") };
        var source = new SdfCompositionFrameSource(dresser: emitters[0], emitters: emitters);
        var captured = source.CaptureFrame(deltaSeconds: 0, height: 32, interpolationAlpha: 0, width: 32).PickMap!;
        var selected = emitters[owner];
        var name = ((owner == 0) ? "first" : "second");
        var target = Assert.IsType<WorldPickTarget>(@object: captured.Resolve(identity: identity));

        Assert.Equal(expected: name, actual: target.Prototype);
        Assert.Equal(expected: $"{name}.palette[0]", actual: captured.MaterialName(identity: identity, material: selected.Material));
        selected.Replace(name: "replacement");
        var replacement = source.CaptureFrame(deltaSeconds: 0, height: 32, interpolationAlpha: 0, width: 32).PickMap!;

        Assert.NotSame(actual: replacement, expected: captured);
        Assert.Equal(expected: $"{name}.palette[0]", actual: captured.MaterialName(identity: identity, material: selected.Material));
        Assert.Equal(expected: "replacement.palette[0]", actual: replacement.MaterialName(identity: identity, material: selected.Material));
        Assert.Null(@object: captured.MaterialName(identity: 0, material: selected.Material));
    }

    private sealed class NamedEmitter(string name) : ISdfSceneEmitter, ISdfFrameDresser {
        private readonly WorldPickMapBuilder m_map = new();
        private readonly List<SdfMeshDraw> m_meshes = [];

        private int m_instance;

        public int Material { get; private set; }
        public IReadOnlyList<SdfMeshDraw> MeshDraws => m_meshes;
        public ISdfPickMap PickMap => m_map.Snapshot(pool: []);

        public void Emit(SdfProgramBuilder builder, in SdfEmitContext context) {
            Material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
            m_instance = builder.InstanceCount;
            _ = builder.BeginInstance(boundCenter: Vector3.Zero, boundRadius: 2).Sphere(radius: 1, material: Material).EndInstance();
            m_meshes.Clear();
            m_meshes.Add(item: new SdfMeshDraw(Material: Material,
                Mesh: new SdfMesh(positions: new Vector3[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }, indices: new uint[] { 0, 1, 2 }),
                ObjectToWorld: Matrix4x4.Identity, Identity: name));
            Replace(name: name);
        }
        public void Replace(string name) {
            m_map.Clear();
            var target = new WorldPickTarget(BodyIndex: null, Placement: name) { Prototype = name };

            m_map.Instances(end: (m_instance + 1), first: m_instance, target: target);
            m_map.Meshes(end: 1, first: 0, target: target);
            m_map.Materials(prototype: name, ids: [Material]);
        }
        public SdfFrame Dress(SdfProgram program, DynamicTransform[] transforms, SdfMovedTransforms moved,
            IReadOnlyList<SdfMeshDraw> meshDraws, long meshDrawsRevision, uint width, uint height,
            float deltaSeconds, float interpolationAlpha) => new(Program: program, ProgramChanged: false, Time: 0, Views: []);
    }
}
