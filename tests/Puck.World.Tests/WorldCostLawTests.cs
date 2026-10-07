using System.Numerics;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

[Collection(AllocationCollection.Name)]
public sealed partial class WorldCostLawTests {
    [Fact]
    public void LiveScopedAndPerShapeCostsReconcileToThePackedProgram() {
        var emitter = new CostEmitter();
        var source = new SdfCompositionFrameSource(dresser: emitter, emitters: [emitter]);
        var frame = source.CaptureFrame(deltaSeconds: 0, height: 32, interpolationAlpha: 0, width: 32);
        var program = frame.Program;
        var report = WorldPlacementCostReport.Read(frame: frame);
        var scoped = Assert.Single(collection: report.Placements, predicate: row => (row.Placement == "scoped"));
        var separate = Assert.Single(collection: report.Placements, predicate: row => (row.Placement == "separate"));

        Assert.Equal(expected: (2, 1), actual: (scoped.Shapes, scoped.Instances));
        Assert.Equal(expected: (2, 2), actual: (separate.Shapes, separate.Instances));
        Assert.True(condition: (scoped.OwnedWords >= (program.Instances[0].End * 20)));
        Assert.True(condition: (report.SharedWords >= (80 + 4)));
        Assert.Equal(expected: program.Words.Length,
            actual: ((report.Placements.Sum(selector: row => row.OwnedWords) + report.OtherInstanceWords) + report.SharedWords));
        Assert.Equal(expected: 0, actual: report.OtherInstanceWords);
        Assert.True(condition: report.Placements.All(predicate: row => !row.Unmaskable));
        Assert.True(condition: (scoped.BoundRadius > 2));
    }

    private sealed class CostEmitter : ISdfSceneEmitter, ISdfFrameDresser {
        private readonly WorldPickMapBuilder m_map = new();

        public ISdfPickMap PickMap => m_map.Snapshot(pool: []);

        public void Emit(SdfProgramBuilder builder, in SdfEmitContext context) {
            var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

            ShapeDocument Shape(int id) => new(Id: id, Name: null, Type: SdfSolidPrimitive.Sphere,
                Position: Vector3.Zero, Rotation: Quaternion.Identity, Scale: Vector3.One, Material: 0,
                Blend: null, Smooth: null, Group: 0);
            var creation = new CreationDocument(Schema: CreationDocument.CurrentSchema, Name: "pair",
                Palette: null, Shapes: [Shape(id: 1), Shape(id: 2)], Frames: null);
            var transform = new CreationStampTransform(Origin: Vector3.Zero, Rotation: Quaternion.Identity, Scale: 1, ReflectionNormal: null);

            _ = builder.BeginInstance(boundCenter: Vector3.Zero, boundRadius: 2);
            _ = builder.PushField(compose: SdfBlendOp.Union);
            CreationStampEmitter.Emit(builder: builder, document: creation, inScope: true, materialFor: _ => material, transform: transform);
            _ = builder.PopField();
            _ = builder.EndInstance();
            for (var shape = 0; (shape < 2); shape++) {
                _ = builder.BeginInstance(boundCenter: Vector3.Zero, boundRadius: 2);
                CreationStampEmitter.EmitShapeStamp(builder: builder, document: creation, material: material,
                    paletteIds: [material], shapeIndex: shape, transform: transform);
                _ = builder.EndInstance();
            }
            m_map.Clear();
            m_map.Instances(first: 0, end: 1, target: new WorldPickTarget(BodyIndex: null, Placement: "scoped") { Prototype = "pair" });
            m_map.Instances(first: 1, end: 3, target: new WorldPickTarget(BodyIndex: null, Placement: "separate") { Prototype = "pair" });
        }
        public SdfFrame Dress(SdfProgram program, DynamicTransform[] transforms, SdfMovedTransforms moved,
            IReadOnlyList<SdfMeshDraw> meshDraws, long meshDrawsRevision, uint width, uint height,
            float deltaSeconds, float interpolationAlpha) => new(Program: program, ProgramChanged: true, Time: 0, Views: []);
    }

}
