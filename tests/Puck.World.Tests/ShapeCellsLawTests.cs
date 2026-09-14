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
    private static readonly ShapeCellsDocument Cells = new(
        Amplitude: .25f,
        Frequency: 2f,
        Mode: SdfCellMode.F2MinusF1,
        Randomness: .2f,
        Seed: uint.MaxValue
    );

    private static ShapeDocument Shape => new(
        Id: 0,
        Name: "surface",
        Type: SdfSolidPrimitive.Sphere,
        Position: Vector3.Zero,
        Rotation: Quaternion.Identity,
        Scale: Vector3.One,
        Material: 0,
        Blend: SdfBlendOp.Union,
        Smooth: 0f,
        Group: 0,
        Cells: Cells
    );

    private static CreationDocument Document(ShapeDocument shape) => new(
        Schema: CreationDocument.CurrentSchema,
        Name: "cells",
        Palette: [new(
                "#AAAAAA",
                null,
                null,
                null
            )],
        Shapes: [shape],
        Frames: null
    );
    private static SdfProgram Emit(ShapeDocument shape, float scale, bool pooled, bool probe = false, float? probeScale = null) {
        var canonical = CreationCanonicalizer.Canonicalize(
            document: Document(shape: shape),
            source: "cells"
        );
        var builder = new SdfProgramBuilder();

        if (!pooled) {
            var material = builder.AddMaterial(material: new(Vector3.One));

            CreationStampEmitter.Emit(
                builder: builder,
                document: canonical.Document,
                materialFor: _ => material,
                transform: new(
                    Vector3.Zero,
                    Quaternion.Identity,
                    scale,
                    null
                )
            );
        } else {
            var creation = new WorldPrototype(
                "cells",
                canonical.Document,
                canonical.Hash
            );
            var definition = Fixtures.BuildGradientUpDocument(gradientUp: false) with {
                CreationsRaw = [creation],
                LookRowsRaw = [new(
                    "rig",
                    new WorldLookSource.Creation(PrototypeId: "cells"),
                    scale,
                    WorldLookMotion.Default
                )],
            };
            var pool = new WorldStampPool();

            pool.Reconcile(
                [],
                [creation],
                [],
                [new(
                        0,
                        creation,
                        scale,
                        WorldLookMotion.Default
                    )]
            );
            pool.Emit(
                builder,
                definition,
                probeWorstCase: probe,
                maxPlacementScale: (probeScale ?? scale),
                slotBase: 0
            );
        }
        return builder.Build(buildInstanceGrid: false);
    }

    [Fact]
    public void AnisotropicPrimitiveBoundsIncludeTheFieldDistanceCorrection() {
        var shape = Shape with { Scale = new Vector3(
            x: 1f,
            y: .1f,
            z: .1f
        ) };
        var plain = CreationStampEmitter.ShapeStampBound(
            document: Document(shape: shape with { Cells = null }),
            shapeIndex: 0,
            transform: new(
                Vector3.Zero,
                Quaternion.Identity,
                1f,
                null
            )
        );
        var displaced = CreationStampEmitter.ShapeStampBound(
            document: Document(shape: shape),
            shapeIndex: 0,
            transform: new(
                Vector3.Zero,
                Quaternion.Identity,
                1f,
                null
            )
        );

        Assert.True(condition: (displaced.Radius >= (plain.Radius + ((Cells.Amplitude * .5f) * 10f))));
    }
    [InlineData(false, 0.25f)]
    [InlineData(false, 3f)]
    [InlineData(true, 0.25f)]
    [InlineData(true, 3f)]
    [Theory]
    public void BothEmittersConvertUnitsAndIsolateRelief(bool pooled, float scale) {
        var program = Emit(
            Shape,
            scale,
            pooled
        );
        var instructions = program.Instructions.ToArray();
        var cell = Array.FindIndex(
            array: instructions,
            match: i => (i.Op == SdfOp.CellDisplace)
        );

        Assert.True(condition: (Array.FindIndex(
            array: instructions,
            match: i => (i.Op == SdfOp.PushField)
        ) < cell));
        Assert.True(condition: (Array.FindIndex(
            array: instructions,
            match: i => (i.Op == SdfOp.PopField)
        ) > cell));
        Assert.Equal(
            (Cells.Frequency / scale),
            instructions[cell].Data0.X
        );
        Assert.Equal(
            (Cells.Amplitude * scale),
            instructions[cell].Data0.Y
        );
        Assert.Equal(
            uint.MaxValue,
            instructions[cell].Shape
        );
        Assert.Equal(
            1f,
            program.StepScale
        );
        Assert.InRange(
            instructions.Single(predicate: i => (i.Op == SdfOp.PopField)).Data1.Y,
            .49999f,
            .50001f
        );
    }
    [Fact]
    public void CapacityProbeWithoutPlacementsUsesFiniteCellParameters() {
        var program = Emit(
            Shape with { Cells = null },
            1f,
            pooled: true,
            probe: true,
            probeScale: 0f
        );
        var cell = program.Instructions.First(predicate: i => (i.Op == SdfOp.CellDisplace));

        Assert.Equal(
            1f,
            cell.Data0.X
        );
        Assert.Equal(
            .1f,
            cell.Data0.Y
        );
    }
    [Fact]
    public void CellsRoundTripWithTheirUnsignedSeedAndMode() {
        var json = JsonSerializer.Serialize(
            Document(shape: Shape),
            DocumentJsonOptions.Shared
        );
        var decoded = JsonSerializer.Deserialize<CreationDocument>(
            json: json,
            options: DocumentJsonOptions.Shared
        )!;

        Assert.Equal(
            Cells,
            Assert.Single(collection: decoded.Shapes!).Cells
        );
        Assert.DoesNotContain(
            actualString: json,
            comparisonType: StringComparison.OrdinalIgnoreCase,
            expectedSubstring: "parameters"
        );
    }
    [Fact]
    public void InvalidFieldsAndUnisolatableCombinationsAreRefusedByPath() {
        foreach (var (cells, field) in new[] {
            (Cells with { Frequency = 0f }, "frequency"), (Cells with { Frequency = 9f }, "frequency"),
            (Cells with { Amplitude = -1f }, "amplitude"), (Cells with { Amplitude = float.NaN }, "amplitude"),
            (Cells with { Mode = ((SdfCellMode)9) }, "mode"), (Cells with { Randomness = .21f }, "randomness"),
            (Cells with { Randomness = -.01f }, "randomness"), (Cells with { Randomness = float.NaN }, "randomness"),
        }) {
            Assert.Contains(
                collection: CreationCanonicalizer.Validate(document: Document(shape: Shape with { Cells = cells })),
                filter: e => (e.Path == ("shapes[0].cells." + field))
            );
        }
        foreach (var shape in new[] { Shape with { Group = 1 }, Shape with { Blend = SdfBlendOp.GrooveUnion }, Shape with { Detail = true }, Shape with { Type = SdfSolidPrimitive.Sweep } }) {
            Assert.Contains(
                collection: CreationCanonicalizer.Validate(document: Document(shape: shape)),
                filter: e => (e.Path == "shapes[0].cells")
            );
        }
    }
    [Fact]
    public void LowLevelDocumentPreservesSeedAndRequiresScope() {
        var json = """
            {"schema":"puck.sdf.v1","materials":[{"albedo":[1,1,1]}],"ops":[
            {"op":"push"},{"op":"sphere","radius":1,"material":0},
            {"op":"cellDisplace","frequency":2,"amplitude":0.25,"seed":4294967295,"mode":"F2MinusF1","randomness":0.2},
            {"op":"pop"}]}
            """;
        var document = SdfDocumentDecoder.Decode(utf8Json: Encoding.UTF8.GetBytes(s: json));
        var builder = new SdfProgramBuilder();

        SdfDocumentDecoder.Replay(
            builder: builder,
            program: document
        );
        Assert.Equal(
            uint.MaxValue,
            builder.Build().Instructions.Single(predicate: i => (i.Op == SdfOp.CellDisplace)).Shape
        );
        Assert.Throws<SdfDocumentException>(testCode: () => SdfDocumentDecoder.Decode(utf8Json: Encoding.UTF8.GetBytes(s: json.Replace(
            newValue: "",
            oldValue: "{\"op\":\"push\"},"
        ).Replace(
            newValue: "",
            oldValue: ",\n{\"op\":\"pop\"}"
        ))));
    }
    [Fact]
    public void ReliefDoesNotAlterAnUnrelatedPreviouslyEmittedShape() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new(Vector3.One));

        builder.Translate(offset: new(
            x: -4,
            y: 0,
            z: 0
        )).Sphere(
            1f,
            material
        );
        CreationStampEmitter.Emit(
            builder: builder,
            document: Document(shape: Shape),
            materialFor: _ => material,
            transform: new(
                Vector3.Zero,
                Quaternion.Identity,
                1f,
                null
            )
        );
        var evaluator = new SdfFieldEvaluator(program: builder.Build());

        Assert.True(condition: evaluator.TryDistance(
            FixedPosition.FromLocal(local: new(
                X: FixedQ4816.FromDouble(value: -4),
                Y: FixedQ4816.One,
                Z: FixedQ4816.Zero
            )),
            out var distance,
            out _
        ));
        Assert.Equal(
            FixedQ4816.Zero,
            distance
        );
    }
    [Fact]
    public void WarpedGeometryRestoresItsRigidSamplingFrame() {
        var shape = Shape with { Shear = new(
            .4f,
            .2f
        ), Flare = new(
            .5f,
            .2f,
            2f
        ) };

        foreach (var pooled in new[] { false, true }) {
            var ops = Emit(
                shape,
                1f,
                pooled
            ).Instructions.ToArray();
            var cell = Array.FindIndex(
                array: ops,
                match: i => (i.Op == SdfOp.CellDisplace)
            );
            var reset = Array.FindLastIndex(
                ops,
                cell,
                i => (i.Op == SdfOp.ResetPoint)
            );

            Assert.True(condition: (reset > Array.FindIndex(
                array: ops,
                match: i => (i.Op == SdfOp.ShapeBlend)
            )));
        }
    }
}
