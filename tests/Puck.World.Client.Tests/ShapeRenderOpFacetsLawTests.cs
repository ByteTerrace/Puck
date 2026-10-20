using System.Numerics;

using Puck.Assets.Documents;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>
/// THE LAW: the facets and folds that reach the ISA's remaining ops lower on both emission paths as the ops they name.
/// <see cref="ShapeDocument.Warp"/> and <see cref="ShapeDocument.Displace"/> emit inside the shape's own field scope,
/// <see cref="ShapeDocument.Elongate"/> as a plain point op; the <see cref="ShapeDomainOp.Jitter"/> and
/// <see cref="ShapeDomainOp.LogSphere"/> folds lower to their lattice ops and refuse rigid-copy expansion by name; a
/// stairs or morph blend opens the shape's own scope and composes at its pop, inside a group's scope too. The
/// canonicalizer refuses what the builder would and clamps the rest.
/// </summary>
public sealed class ShapeRenderOpFacetsLawTests {
    private const string PrototypeId = "render-ops";

    private static CreationDocument Document(params ShapeDocument[] shapes) => CreationFixtures.Document(name: PrototypeId, shapes: shapes);
    private static SdfProgram EmitPool(params ShapeDocument[] shapes) => CreationFixtures.EmitPool(bodyScale: 1f, name: PrototypeId, shapes: shapes);
    private static SdfProgram EmitStatic(params ShapeDocument[] shapes) => CreationFixtures.EmitStatic(document: Document(shapes), stampScale: 1f);
    private static ShapeDocument Shape(SdfSolidPrimitive type = SdfSolidPrimitive.Box, int id = 0, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f, int group = 0) => new(
        Id: id,
        Name: null,
        Type: type,
        Position: new Vector3(x: (0.3f * id), y: 0f, z: 0f),
        Rotation: Quaternion.Identity,
        Scale: new Vector3(value: 0.4f),
        Material: 0,
        Blend: blend,
        Smooth: smooth,
        Group: group
    );
    // Whether the shape at `shapeIndex` lies between a PushField and the PopField that closes it.
    private static void AssertScoped(SdfProgram program, SdfOp op) {
        var instructions = program.Instructions.ToList();
        var opIndex = instructions.FindIndex(match: instruction => (instruction.Op == op));
        var shapeIndex = instructions.FindIndex(match: static instruction => (instruction.Op == SdfOp.ShapeBlend));

        Assert.True(condition: (opIndex >= 0), userMessage: $"no {op} was emitted");
        Assert.True(condition: (instructions.FindLastIndex(startIndex: shapeIndex, match: static instruction => (instruction.Op == SdfOp.PushField)) >= 0), userMessage: "the shape has no PushField before it");
        Assert.True(condition: (instructions.FindIndex(startIndex: shapeIndex, match: static instruction => (instruction.Op == SdfOp.PopField)) > shapeIndex), userMessage: "the shape has no PopField after it");
    }
    private static SdfInstruction Pop(SdfProgram program) => program.Instructions.Last(predicate: static instruction => (instruction.Op == SdfOp.PopField));

    [Fact]
    public void AWarpEmitsInsideTheShapesScopeOnBothPaths() {
        var shape = Shape() with { Warp = new ShapeWarpDocument(Amplitude: 0.05f, Frequency: new Vector3(x: 3f, y: 5f, z: 7f)) };

        AssertScoped(op: SdfOp.DomainWarp, program: EmitPool(shape));
        AssertScoped(op: SdfOp.DomainWarp, program: EmitStatic(shape));
    }
    [Fact]
    public void ADisplaceEmitsAfterTheShapeInsideItsScopeOnBothPaths() {
        var shape = Shape() with { Displace = new ShapeDisplaceDocument(Amplitude: 0.02f, Frequency: new Vector3(value: 20f)) };

        foreach (var program in ((SdfProgram[])[EmitPool(shape), EmitStatic(shape)])) {
            AssertScoped(op: SdfOp.Displace, program: program);

            var instructions = program.Instructions.ToList();

            Assert.True(condition: (instructions.FindIndex(match: static instruction => (instruction.Op == SdfOp.Displace)) > instructions.FindIndex(match: static instruction => (instruction.Op == SdfOp.ShapeBlend))));
        }
    }
    [Fact]
    public void AnElongationEmitsAsAPlainPointOpBeforeTheShape() {
        var shape = Shape(type: SdfSolidPrimitive.Sphere) with { Elongate = new DocumentVector3(x: 0.3f, y: 0f, z: 0.1f) };

        foreach (var program in ((SdfProgram[])[EmitPool(shape), EmitStatic(shape)])) {
            var instructions = program.Instructions.ToList();
            var elongate = instructions.FindIndex(match: static instruction => (instruction.Op == SdfOp.Elongate));

            Assert.True(condition: ((elongate >= 0) && (elongate < instructions.FindIndex(match: static instruction => (instruction.Op == SdfOp.ShapeBlend)))));
            Assert.Equal(expected: new Vector3(x: 0.3f, y: 0f, z: 0.1f), actual: new Vector3(x: instructions[elongate].Data0.X, y: instructions[elongate].Data0.Y, z: instructions[elongate].Data0.Z));
            Assert.DoesNotContain(collection: instructions, filter: static instruction => (instruction.Op == SdfOp.PushField));
        }
    }
    [Fact]
    public void TheJitterAndLogSphereFoldsLowerToTheirLatticeOpsAndRefuseExpansion() {
        var jittered = Shape(type: SdfSolidPrimitive.Sphere) with { Domain = [new ShapeDomainOp.Jitter(Amount: 0.1f, Spacing: new Vector3(value: 1.5f), Seed: 3u, Tumble: 0.5f, MaterialVariants: 0, Flavor: SdfNoiseFlavor.Blue)] };
        var shelled = Shape(type: SdfSolidPrimitive.Sphere) with { Domain = [new ShapeDomainOp.LogSphere(ShellRatio: 2f, Twist: 0.3f, Origin: new DocumentVector3(x: 0f, y: 1f, z: 0f))] };

        foreach (var program in ((SdfProgram[])[EmitPool(jittered), EmitStatic(jittered)])) {
            var jitter = Assert.Single(collection: program.Instructions, predicate: static instruction => (instruction.Op == SdfOp.CellJitter));

            Assert.Equal(expected: (uint)SdfNoiseFlavor.Blue, actual: jitter.Blend);
            Assert.Equal(expected: 3u, actual: jitter.Shape);
            Assert.Equal(expected: 0.5f, actual: jitter.Data1.W);
        }
        foreach (var program in ((SdfProgram[])[EmitPool(shelled), EmitStatic(shelled)])) {
            var shell = Assert.Single(collection: program.Instructions, predicate: static instruction => (instruction.Op == SdfOp.LogSphere));

            Assert.Equal(expected: MathF.Log(x: 2f), actual: shell.Data0.X, tolerance: 1e-6f);
            Assert.Equal(expected: 0.3f, actual: shell.Data0.Y);
        }

        Assert.False(condition: ShapeDomainOps.TryExpand(domain: jittered.Domain, frames: out _, refusal: out var jitterRefusal, subject: Vector3.Zero));
        Assert.Contains(expectedSubstring: "jitter", actualString: jitterRefusal);
        Assert.False(condition: ShapeDomainOps.TryExpand(domain: shelled.Domain, frames: out _, refusal: out var shellRefusal, subject: Vector3.Zero));
        Assert.Contains(expectedSubstring: "log-sphere", actualString: shellRefusal);
        Assert.Equal(expected: SdfBoundAlgebra.Unbounded, actual: ShapeDomainOps.Reach(domain: jittered.Domain));
        Assert.Equal(expected: SdfBoundAlgebra.Unbounded, actual: ShapeDomainOps.Reach(domain: shelled.Domain));
    }
    [Fact]
    public void AStairsBlendComposesAtTheShapesOwnPopOnBothPathsAndInsideAGroup() {
        var subject = Shape();
        var stairs = Shape(id: 1, blend: SdfBlendOp.StairsUnion, smooth: 0.2f) with { Steps = 3 };
        var carve = Shape(id: 1, blend: SdfBlendOp.StairsSubtraction, smooth: 0.1f);

        foreach (var program in ((SdfProgram[])[EmitStatic(subject, stairs), EmitPool(subject with { Group = 1 }, stairs with { Group = 1 })])) {
            var stepped = Assert.Single(collection: program.Instructions, predicate: static instruction => ((instruction.Op == SdfOp.PopField) && (instruction.Blend == (uint)SdfBlendOp.StairsUnion)));

            Assert.Equal(expected: 0.2f, actual: stepped.Data1.X);
            Assert.Equal(expected: 3f, actual: stepped.Data1.Z);
        }

        var carved = Assert.Single(collection: EmitStatic(subject, carve).Instructions, predicate: static instruction => ((instruction.Op == SdfOp.PopField) && (instruction.Blend == (uint)SdfBlendOp.StairsSubtraction)));

        Assert.Equal(expected: (float)ShapeScope.DefaultSteps, actual: carved.Data1.Z);
    }
    [Fact]
    public void AMorphBlendComposesAtTheShapesOwnPopWithItsLaneAndRange() {
        var subject = Shape();
        var morph = Shape(id: 1, blend: SdfBlendOp.Morph) with { Morph = new ShapeMorphDocument(Lane: 2, From: -1f, To: 1f) };

        foreach (var program in ((SdfProgram[])[EmitStatic(subject, morph), EmitPool(subject with { Group = 1 }, morph with { Group = 1 })])) {
            var pop = Assert.Single(collection: program.Instructions, predicate: static instruction => ((instruction.Op == SdfOp.PopField) && (instruction.Blend == (uint)SdfBlendOp.Morph)));

            Assert.Equal(expected: 2f, actual: pop.Data0.X);
            Assert.Equal(expected: -1f, actual: pop.Data0.Y);
            Assert.Equal(expected: 1f, actual: pop.Data0.Z);
        }
    }
    [Fact]
    public void TheCanonicalizerRefusesWhatTheBuilderWouldAndClampsTheRest() {
        CreationFixtures.AssertRefusesNaming(document: Document(Shape(blend: SdfBlendOp.StairsUnion)), needle: "smooth radius above zero");
        CreationFixtures.AssertRefusesNaming(document: Document(Shape(blend: SdfBlendOp.StairsUnion, smooth: 0.1f) with { Steps = 0 }), needle: "steps must be in");
        CreationFixtures.AssertRefusesNaming(document: Document(Shape() with { Steps = 2 }), needle: "steps requires a stairs blend");
        CreationFixtures.AssertRefusesNaming(document: Document(Shape(blend: SdfBlendOp.Morph)), needle: "morph facet");
        CreationFixtures.AssertRefusesNaming(document: Document(Shape(blend: SdfBlendOp.Morph) with { Morph = new ShapeMorphDocument(Lane: 0, From: 1f, To: 1f) }), needle: "differing from and to");
        CreationFixtures.AssertRefusesNaming(document: Document(Shape() with { Morph = new ShapeMorphDocument(Lane: 0, From: 0f, To: 1f) }), needle: "morph requires blend");
        CreationFixtures.AssertRefusesNaming(document: Document(Shape() with { Warp = new ShapeWarpDocument(Amplitude: float.NaN, Frequency: Vector3.One) }), needle: "warp requires");
        CreationFixtures.AssertRefusesNaming(document: Document(Shape(type: SdfSolidPrimitive.Sweep) with { Curve = new ShapeCurveDocument(A: Vector3.Zero, B: Vector3.UnitY, C: (2f * Vector3.UnitY), RadiusStart: 0.1f, RadiusEnd: 0.1f), Displace = new ShapeDisplaceDocument(Amplitude: 0.1f, Frequency: Vector3.One) }), needle: "closed primitive");
        CreationFixtures.AssertRefusesNaming(document: Document(Shape() with { Elongate = new DocumentVector3(x: float.PositiveInfinity, y: 0f, z: 0f) }), needle: "elongate is non-finite");
        CreationFixtures.AssertRefusesNaming(document: Document(Shape() with { Domain = [new ShapeDomainOp.Jitter(Amount: 2f, Spacing: Vector3.One)] }), needle: "under the least spacing");
        CreationFixtures.AssertRefusesNaming(document: Document(Shape() with { Domain = [new ShapeDomainOp.Jitter(Amount: 0.1f, Spacing: Vector3.One, Tumble: 2f)] }), needle: "tumble");
        CreationFixtures.AssertRefusesNaming(document: Document(Shape() with { Domain = [new ShapeDomainOp.LogSphere(ShellRatio: 1f)] }), needle: "shellRatio");
        CreationFixtures.AssertRefusesNaming(document: Document(Shape() with { Domain = [new ShapeDomainOp.LogSphere(ShellRatio: 2f, Twist: float.NaN)] }), needle: "twist is non-finite");

        var canonical = CreationCanonicalizer.Normalize(document: Document(Shape(blend: SdfBlendOp.StairsUnion, smooth: 0.1f) with {
            Steps = 500,
            Warp = new ShapeWarpDocument(Amplitude: 5f, Frequency: new Vector3(value: 1000f)),
            Displace = new ShapeDisplaceDocument(Amplitude: -5f, Frequency: new Vector3(value: -1000f)),
            Elongate = new DocumentVector3(x: 0f, y: 0f, z: 0f),
            Domain = [new ShapeDomainOp.Jitter(Amount: -0.2f, Spacing: Vector3.One, Tumble: 3f), new ShapeDomainOp.LogSphere(ShellRatio: 0.5f)],
        })).Shapes![0];

        Assert.Equal(expected: ShapeScope.MaxSteps, actual: canonical.Steps);
        Assert.Equal(expected: ShapeWarpDocument.MaxAmplitude, actual: canonical.Warp!.Amplitude);
        Assert.Equal(expected: ShapeWarpDocument.MaxFrequency, actual: canonical.Warp.Frequency.X);
        Assert.Equal(expected: -ShapeDisplaceDocument.MaxAmplitude, actual: canonical.Displace!.Amplitude);
        Assert.Equal(expected: -ShapeDisplaceDocument.MaxFrequency, actual: canonical.Displace.Frequency.Z);
        Assert.Null(@object: canonical.Elongate);
        Assert.Equal(expected: 0.2f, actual: Assert.IsType<ShapeDomainOp.Jitter>(@object: canonical.Domain![0]).Amount);
        Assert.Equal(expected: 1f, actual: Assert.IsType<ShapeDomainOp.Jitter>(@object: canonical.Domain[0]).Tumble);
        Assert.Equal(expected: ShapeDomainOp.LogSphere.MinShellRatio, actual: Assert.IsType<ShapeDomainOp.LogSphere>(@object: canonical.Domain[1]).ShellRatio);
    }
    [Fact]
    public void ThePointReachCountsBumpsWarpAndElongationAndTheProbeCoversThem() {
        var shape = Shape() with {
            Bumps = [new ShapeBumpDocument(Center: new DocumentVector3(x: 0f, y: 0f, z: 0f), Radii: new DocumentVector3(x: 0.2f, y: 0.2f, z: 0.2f), Push: new DocumentVector3(x: 0.3f, y: 0f, z: 0f))],
            Warp = new ShapeWarpDocument(Amplitude: 0.1f, Frequency: Vector3.One),
            Elongate = new DocumentVector3(x: 0.3f, y: 0.4f, z: 0f),
        };

        Assert.Equal(expected: ((0.3f + (0.1f * MathF.Sqrt(x: 3f))) + 0.5f), actual: ShapeWarpReach.PointReach(shape: shape), tolerance: 1e-6f);
        Assert.True(condition: (ShapeWarpReach.ProbePointReach >= ShapeWarpReach.PointReach(shape: shape)));
    }
}
