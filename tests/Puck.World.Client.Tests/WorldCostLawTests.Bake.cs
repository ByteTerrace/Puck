using System.Numerics;
using Puck.Assets.Documents;
using Puck.SdfVm;
using Puck.SignedDistance;
using Xunit;

namespace Puck.World.Client.Tests;

public sealed partial class WorldCostLawTests {
    [Fact]
    public void BakeRepresentationBelongsToTheCapturedFrameInsteadOfAnotherWorldCache() {
        var definition = new WorldDefinition {
            CreationsRaw = [CreationFixtures.UnitSphere(id: "shared-name")],
            PlacementRowsRaw = [new WorldPlacement(Id: "object", Position: new DocumentVector3(value: Vector3.Zero),
                PrototypeId: "shared-name", Scale: 1, YawDegrees: 0)],
        };
        var mesh = new SdfMesh(positions: new Vector3[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }, indices: new uint[] { 0, 1, 2 });
        var baked = Capture(definition: definition, mesh: mesh);
        // Another world can use the same authored ids while drawing its field. Its cache or emission must not
        // reinterpret the already captured first world's representation.
        var field = Capture(definition: definition, mesh: null);
        var first = Assert.Single(collection: WorldPlacementCostReport.Read(frame: baked).Placements);
        var second = Assert.Single(collection: WorldPlacementCostReport.Read(frame: field).Placements);

        Assert.True(condition: first.DrawsBake);
        Assert.Equal(expected: 1, actual: first.MeshDraws);
        Assert.Equal(expected: first.Instances, actual: first.HiddenFieldInstances);
        Assert.False(condition: second.DrawsBake);
        Assert.Equal(expected: 0, actual: second.MeshDraws);
        Assert.Equal(expected: 0, actual: second.HiddenFieldInstances);
    }

    private static SdfFrame Capture(WorldDefinition definition, SdfMesh? mesh) {
        var emitter = new StaticCostEmitter(definition: definition, mesh: mesh);

        return new SdfCompositionFrameSource(dresser: emitter, emitters: [emitter])
            .CaptureFrame(deltaSeconds: 0, height: 32, interpolationAlpha: 0, width: 32);
    }

    private sealed class StaticCostEmitter(WorldDefinition definition, SdfMesh? mesh) : ISdfSceneEmitter, ISdfFrameDresser {
        private readonly WorldPickMapBuilder m_map = new();
        private readonly List<SdfMeshDraw> m_draws = [];

        public IReadOnlyList<SdfMeshDraw> MeshDraws => m_draws;
        public ISdfPickMap PickMap => m_map.Snapshot(pool: []);

        public void Emit(SdfProgramBuilder builder, in SdfEmitContext context) {
            m_map.Clear(); m_draws.Clear();
            WorldPlacementStamper.EmitStatic(builder: builder, definition: definition, creations: definition.Creations,
                placements: definition.Placements, meshDraws: m_draws, bakedFor: _ => ((mesh is null) ? null : new WorldBakedDraw(Impostor: null, Mesh: mesh)), picks: m_map);
        }
        public SdfFrame Dress(SdfProgram program, DynamicTransform[] transforms, SdfMovedTransforms moved,
            IReadOnlyList<SdfMeshDraw> meshDraws, long meshDrawsRevision, uint width, uint height,
            float deltaSeconds, float interpolationAlpha) => new(Program: program, ProgramChanged: true, Time: 0, Views: []) { MeshDraws = meshDraws, MeshDrawsRevision = meshDrawsRevision };
    }
}
