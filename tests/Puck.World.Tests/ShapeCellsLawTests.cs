using System.Numerics;
using System.Text;
using System.Text.Json;
using Puck.Assets.Documents;
using Puck.World.Client.Sdf;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;
using Puck.Maths;
using Puck.World.Authoring;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

public sealed class ShapeCellsLawTests {
    private static readonly ShapeCellsDocument Cells = new(2f, .25f, uint.MaxValue, SdfCellMode.F2MinusF1, .2f);
    private static ShapeDocument Shape => new(Id: 0, Name: "surface", Type: SdfSolidPrimitive.Sphere,
        Position: Vector3.Zero, Rotation: Quaternion.Identity, Scale: Vector3.One, Material: 0,
        Blend: SdfBlendOp.Union, Smooth: 0f, Group: 0, Cells: Cells);
    private static CreationDocument Document(ShapeDocument shape) => new(Schema: CreationDocument.CurrentSchema,
        Name: "cells", Palette: [new("#AAAAAA", null, null, null)], Shapes: [shape], Frames: null);
    private static SdfProgram Emit(ShapeDocument shape, float scale, bool pooled) {
        var canonical = CreationCanonicalizer.Canonicalize(Document(shape), "cells");
        var builder = new SdfProgramBuilder();
        if (!pooled) {
            var material = builder.AddMaterial(new(Vector3.One));
            CreationStampEmitter.Emit(builder: builder, document: canonical.Document, materialFor: _ => material,
                transform: new(Vector3.Zero, Quaternion.Identity, scale, null));
        } else {
            var creation = new WorldPrototype("cells", canonical.Document, canonical.Hash);
            var definition = Fixtures.BuildGradientUpDocument(false) with {
                CreationsRaw = [creation],
                LookRowsRaw = [new("rig", new WorldLookSource.Creation("cells"), scale, WorldLookMotion.Default)],
            };
            var pool = new WorldStampPool();
            pool.Reconcile([], [creation], [], [new(0, creation, scale, WorldLookMotion.Default)]);
            pool.Emit(builder, definition, probeWorstCase: false, maxPlacementScale: scale, slotBase: 0);
        }
        return builder.Build(buildInstanceGrid: false);
    }
    [Theory]
    [InlineData(false, 0.25f)]
    [InlineData(false, 3f)]
    [InlineData(true, 0.25f)]
    [InlineData(true, 3f)]
    public void BothEmittersConvertUnitsAndIsolateRelief(bool pooled, float scale) {
        var program = Emit(Shape, scale, pooled);
        var instructions = program.Instructions.ToArray();
        var cell = Array.FindIndex(instructions, i => i.Op == SdfOp.CellDisplace);
        Assert.True(Array.FindIndex(instructions, i => i.Op == SdfOp.PushField) < cell);
        Assert.True(Array.FindIndex(instructions, i => i.Op == SdfOp.PopField) > cell);
        Assert.Equal(Cells.Frequency / scale, instructions[cell].Data0.X);
        Assert.Equal(Cells.Amplitude * scale, instructions[cell].Data0.Y);
        Assert.Equal(uint.MaxValue, instructions[cell].Shape);
        Assert.Equal(1f, program.StepScale);
        Assert.InRange(instructions.Single(i => i.Op == SdfOp.PopField).Data1.Y, .49999f, .50001f);
    }
    [Fact]
    public void WarpedGeometryRestoresItsRigidSamplingFrame() {
        var shape = Shape with { Shear = new(.4f, .2f), Flare = new(.5f, .2f, 2f) };
        foreach (var pooled in new[] { false, true }) {
            var ops = Emit(shape, 1f, pooled).Instructions.ToArray();
            var cell = Array.FindIndex(ops, i => i.Op == SdfOp.CellDisplace);
            var reset = Array.FindLastIndex(ops, cell, i => i.Op == SdfOp.ResetPoint);
            Assert.True(reset > Array.FindIndex(ops, i => i.Op == SdfOp.ShapeBlend));
        }
    }
    [Fact]
    public void ReliefDoesNotAlterAnUnrelatedPreviouslyEmittedShape() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new(Vector3.One));
        builder.Translate(new(-4,0,0)).Sphere(1f, material);
        CreationStampEmitter.Emit(builder: builder, document: Document(Shape), materialFor: _ => material, transform: new(Vector3.Zero,Quaternion.Identity,1f,null));
        var evaluator = new SdfFieldEvaluator(builder.Build());
        Assert.True(evaluator.TryDistance(FixedPosition.FromLocal(new(FixedQ4816.FromDouble(-4),FixedQ4816.One,FixedQ4816.Zero)), out var distance,out _));
        Assert.Equal(FixedQ4816.Zero, distance);
    }
    [Fact]
    public void AnisotropicPrimitiveBoundsIncludeTheFieldDistanceCorrection() {
        var shape = Shape with { Scale = new Vector3(1f, .1f, .1f) };
        var plain = CreationStampEmitter.ShapeStampBound(Document(shape with { Cells = null }), 0, new(Vector3.Zero, Quaternion.Identity, 1f, null));
        var displaced = CreationStampEmitter.ShapeStampBound(Document(shape), 0, new(Vector3.Zero, Quaternion.Identity, 1f, null));
        Assert.True(displaced.Radius >= plain.Radius + Cells.Amplitude * .5f * 10f);
    }
    [Fact]
    public void CellsRoundTripWithTheirUnsignedSeedAndMode() {
        var json = JsonSerializer.Serialize(Document(Shape), DocumentJsonOptions.Shared);
        var decoded = JsonSerializer.Deserialize<CreationDocument>(json, DocumentJsonOptions.Shared)!;
        Assert.Equal(Cells, Assert.Single(decoded.Shapes!).Cells);
        Assert.DoesNotContain("parameters", json, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public void LowLevelDocumentPreservesSeedAndRequiresScope() {
        var json = """
            {"schema":"puck.sdf.v1","materials":[{"albedo":[1,1,1]}],"ops":[
            {"op":"push"},{"op":"sphere","radius":1,"material":0},
            {"op":"cellDisplace","frequency":2,"amplitude":0.25,"seed":4294967295,"mode":"F2MinusF1","randomness":0.2},
            {"op":"pop"}]}
            """;
        var document = SdfDocumentDecoder.Decode(Encoding.UTF8.GetBytes(json));
        var builder = new SdfProgramBuilder();
        SdfDocumentDecoder.Replay(builder, document);
        Assert.Equal(uint.MaxValue, builder.Build().Instructions.Single(i => i.Op == SdfOp.CellDisplace).Shape);
        Assert.Throws<SdfDocumentException>(() => SdfDocumentDecoder.Decode(Encoding.UTF8.GetBytes(json.Replace("{\"op\":\"push\"},", "").Replace(",\n{\"op\":\"pop\"}", ""))));
    }
    [Fact]
    public void InvalidFieldsAndUnisolatableCombinationsAreRefusedByPath() {
        foreach (var (cells, field) in new[] {
            (Cells with { Frequency = 0f }, "frequency"), (Cells with { Frequency = 9f }, "frequency"),
            (Cells with { Amplitude = -1f }, "amplitude"), (Cells with { Amplitude = float.NaN }, "amplitude"),
            (Cells with { Mode = (SdfCellMode)9 }, "mode"), (Cells with { Randomness = .21f }, "randomness"),
            (Cells with { Randomness = -.01f }, "randomness"), (Cells with { Randomness = float.NaN }, "randomness"),
        }) {
            Assert.Contains(CreationCanonicalizer.Validate(Document(Shape with { Cells = cells })), e => e.Path == "shapes[0].cells." + field);
        }
        foreach (var shape in new[] { Shape with { Group = 1 }, Shape with { Blend = SdfBlendOp.GrooveUnion }, Shape with { Detail = true }, Shape with { Type = SdfSolidPrimitive.Sweep } }) {
            Assert.Contains(CreationCanonicalizer.Validate(Document(shape)), e => e.Path == "shapes[0].cells");
        }
    }
}
