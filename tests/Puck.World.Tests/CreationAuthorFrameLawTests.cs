using System.Numerics;
using System.Text;

using Puck.World.Authoring;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;
using Puck.World.Server;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Proves the <see cref="CreationFrame"/> author↔engine conversion and the <see cref="CreationGeometry"/>
/// unit-size table against the compiled fixed-point solid field — the same field <see cref="WorldSolidField"/>
/// compiles for contact resolution.</summary>
public sealed class CreationAuthorFrameLawTests {
    private static readonly FixedQ4816 SurfaceTolerance = FixedQ4816.FromDouble(value: 0.01);

    /// <summary>A text run authored on a shape's +Z face converts to the engine's −Z side, and the compiled solid
    /// field agrees the shape's surface sits exactly there — the render conversion and the collision compiler read
    /// the same author-frame document through the same <see cref="CreationFrame"/> transform.</summary>
    [Fact]
    public void AuthoredPlusZFaceConvertsToTheEngineMinusZSideAndTheSolidFieldAgrees() {
        var sphere = new ShapeDocument(
            Id: 0,
            Name: "core",
            Type: SdfSolidPrimitive.Sphere,
            Position: new Vector3(x: 0f, y: 1f, z: 5f),
            Rotation: Quaternion.Identity,
            Scale: Vector3.One,
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 0);
        var faceRun = new TextRunDocument(
            Text: "K",
            Position: new Vector3(x: 0f, y: 1f, z: 6f), // the sphere's authored +Z face (center + unit radius)
            Rotation: Quaternion.Identity,
            EmHeight: 0.3f,
            Depth: 0.02f,
            Mode: TextRunDocument.ModeEmboss,
            Material: 0);
        var document = new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: "faced",
            Palette: null,
            Shapes: [sphere],
            Frames: null,
            TextRuns: [faceRun]);
        var canonical = CreationCanonicalizer.Canonicalize(document: document, source: "faced");
        var creation = new WorldPrototype(Id: "faced", Document: canonical.Document, HashRaw: canonical.Hash);

        // The render conversion: the same function WorldPrototype.EngineDocument caches for every stamp/collision
        // consumer.
        var engineRun = CreationFrame.ToEngine(document: canonical.Document).TextRuns![0];

        Assert.True((engineRun.Position.Z < 0f), userMessage: $"an authored +Z face run converted to the engine +Z side; z={engineRun.Position.Z:0.###}");

        var source = Fixtures.BuildGradientUpDocument(gradientUp: false);
        var definition = source with {
            CreationsRaw = [creation],
            PlacementRowsRaw = [new WorldPlacement(Id: "faced", PrototypeId: creation.Id, Position: Vector3.Zero, YawDegrees: 0f, Scale: 1f, Solid: new WorldSolid(Margin: 0f))],
        };

        Assert.True(condition: WorldSolidField.TryBuild(definition: definition, built: out var field, reason: out var reason), userMessage: reason);

        var surfacePoint = FixedVector3.FromVector3(value: engineRun.Position);

        Assert.True(condition: field!.Probe(distance: out var distance, gradient: out _, material: out _, position: in surfacePoint));
        Assert.True((FixedQ4816.Abs(value: distance) < SurfaceTolerance),
            userMessage: $"the compiled solid field disagrees with the render conversion at the authored front point; distance={((double)distance):0.####}");
    }
    /// <summary>A capsule authored <c>scale {0.5,1,0.5}</c> compiles to radius 0.5 (the equator probe) and a
    /// 1-unit cylindrical section (the top-cap probe combines radius + length, isolating length by subtraction) —
    /// <see cref="CreationGeometry"/>'s unit table read literally.</summary>
    [Fact]
    public void CapsuleAuthoredScaleCompilesToItsUnitTableRadiusAndLength() {
        var capsule = new ShapeDocument(
            Id: 0,
            Name: "cap",
            Type: SdfSolidPrimitive.Capsule,
            Position: Vector3.Zero,
            Rotation: Quaternion.Identity,
            Scale: new Vector3(x: 0.5f, y: 1f, z: 0.5f),
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 0);
        var document = new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: "cap",
            Palette: null,
            Shapes: [capsule],
            Frames: null);
        var canonical = CreationCanonicalizer.Canonicalize(document: document, source: "cap");
        var creation = new WorldPrototype(Id: "cap", Document: canonical.Document, HashRaw: canonical.Hash);

        var source = Fixtures.BuildGradientUpDocument(gradientUp: false);
        var definition = source with {
            CreationsRaw = [creation],
            PlacementRowsRaw = [new WorldPlacement(Id: "cap", PrototypeId: creation.Id, Position: Vector3.Zero, YawDegrees: 0f, Scale: 1f, Solid: new WorldSolid(Margin: 0f))],
        };

        Assert.True(condition: WorldSolidField.TryBuild(definition: definition, built: out var field, reason: out var reason), userMessage: reason);

        var equatorPoint = new FixedVector3(X: FixedQ4816.FromDouble(value: 0.5), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero);
        var topPoint = new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.FromDouble(value: 1.0), Z: FixedQ4816.Zero);

        Assert.True(condition: field!.Probe(distance: out var equatorDistance, gradient: out _, material: out _, position: in equatorPoint));
        Assert.True((FixedQ4816.Abs(value: equatorDistance) < SurfaceTolerance),
            userMessage: $"scale.x/z=0.5 did not compile to radius 0.5; equator distance={((double)equatorDistance):0.####}");

        Assert.True(condition: field.Probe(distance: out var topDistance, gradient: out _, material: out _, position: in topPoint));
        Assert.True((FixedQ4816.Abs(value: topDistance) < SurfaceTolerance),
            userMessage: $"scale.y=1 did not compile to a 1-unit cylindrical section (2·radius + length = 2); top distance={((double)topDistance):0.####}");
    }
    /// <summary>A shape carrying a <c>symmetry</c> domain op reads solid at BOTH its own authored point and the point
    /// reflected across the fold plane — proved twice, through the same instruction stream two different ways: the
    /// render path (<see cref="CreationStampEmitter.Emit"/> read by a second, independent <see cref="SdfFieldEvaluator"/>,
    /// standing in for the GPU shader interpreter the same words feed) and the compiled fixed-point solid field
    /// (<see cref="WorldSolidField"/>, what contact resolution actually collides against).</summary>
    [Fact]
    public void SymmetryDomainOpMirrorsBothRenderAndTheCompiledSolidField() {
        const float radius = 0.15f;
        var sphere = new ShapeDocument(
            Id: 0,
            Name: "eye",
            Type: SdfSolidPrimitive.Sphere,
            Position: new Vector3(x: 0.3f, y: 1f, z: 0f),
            Rotation: Quaternion.Identity,
            Scale: new Vector3(value: radius),
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 0,
            Domain: [new ShapeDomainOp.Symmetry(Normal: Vector3.UnitX)]);
        var document = new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: "symmetric-eye",
            Palette: null,
            Shapes: [sphere],
            Frames: null);
        var canonical = CreationCanonicalizer.Canonicalize(document: document, source: "symmetric-eye");
        var creation = new WorldPrototype(Id: "symmetric-eye", Document: canonical.Document, HashRaw: canonical.Hash);
        var enginePosition = creation.EngineDocument.Shapes![0].Position;
        // A point on the sphere's +Y pole — offset perpendicular to the fold's X normal, so reflecting it across X
        // (the same reflection the fold applies) lands squarely on the mirrored copy's own +Y pole too.
        var authoredPoint = FixedVector3.FromVector3(value: (enginePosition + new Vector3(x: 0f, y: radius, z: 0f)));
        var reflectedPoint = new FixedVector3(X: -authoredPoint.X, Y: authoredPoint.Y, Z: authoredPoint.Z);

        var renderBuilder = new SdfProgramBuilder();
        var renderMaterial = renderBuilder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        CreationStampEmitter.Emit(
            builder: renderBuilder,
            document: creation.EngineDocument,
            transform: new CreationStampTransform(Origin: Vector3.Zero, Rotation: Quaternion.Identity, Scale: 1f, ReflectionNormal: null),
            materialFor: _ => renderMaterial);

        var renderEvaluator = new SdfFieldEvaluator(program: renderBuilder.Build(buildInstanceGrid: false));

        Assert.True(condition: renderEvaluator.TryDistance(position: FixedPosition.FromLocal(local: authoredPoint), distance: out var authoredRenderDistance, material: out _));
        Assert.True((FixedQ4816.Abs(value: authoredRenderDistance) < SurfaceTolerance),
            userMessage: $"the authored point did not read as solid in the render program; distance={((double)authoredRenderDistance):0.####}");
        Assert.True(condition: renderEvaluator.TryDistance(position: FixedPosition.FromLocal(local: reflectedPoint), distance: out var reflectedRenderDistance, material: out _));
        Assert.True((FixedQ4816.Abs(value: reflectedRenderDistance) < SurfaceTolerance),
            userMessage: $"the fold-reflected point did not read as solid in the render program; distance={((double)reflectedRenderDistance):0.####}");

        var source = Fixtures.BuildGradientUpDocument(gradientUp: false);
        var definition = source with {
            CreationsRaw = [creation],
            PlacementRowsRaw = [new WorldPlacement(Id: "symmetric-eye", PrototypeId: creation.Id, Position: Vector3.Zero, YawDegrees: 0f, Scale: 1f, Solid: new WorldSolid(Margin: 0f))],
        };

        Assert.True(condition: WorldSolidField.TryBuild(definition: definition, built: out var field, reason: out var reason), userMessage: reason);
        Assert.True(condition: field!.Probe(distance: out var authoredFieldDistance, gradient: out _, material: out _, position: in authoredPoint));
        Assert.True((FixedQ4816.Abs(value: authoredFieldDistance) < SurfaceTolerance),
            userMessage: $"the authored point did not read as solid in the compiled contact field; distance={((double)authoredFieldDistance):0.####}");
        Assert.True(condition: field.Probe(distance: out var reflectedFieldDistance, gradient: out _, material: out _, position: in reflectedPoint));
        Assert.True((FixedQ4816.Abs(value: reflectedFieldDistance) < SurfaceTolerance),
            userMessage: $"the fold-reflected point did not read as solid in the compiled contact field; distance={((double)reflectedFieldDistance):0.####}");
    }
    /// <summary>A shape authored with NO domain ops keeps <see cref="ShapeDocument.Domain"/> absent after
    /// canonicalization (never an empty/null-valued member) — the same canonical bytes and hash a domain-less
    /// creation always produced, undisturbed by the domain-op family's introduction.</summary>
    [Fact]
    public void DomainLessShapeCanonicalizesWithNoDomainMember() {
        var shape = new ShapeDocument(
            Id: 0,
            Name: "plain",
            Type: SdfSolidPrimitive.Box,
            Position: Vector3.Zero,
            Rotation: Quaternion.Identity,
            Scale: Vector3.One,
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 0);
        var document = new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: "domain-less",
            Palette: null,
            Shapes: [shape],
            Frames: null);

        var omitted = CreationCanonicalizer.Canonicalize(document: document, source: "domain-less");
        var explicitNull = CreationCanonicalizer.Canonicalize(document: (document with { Shapes = [shape with { Domain = null }] }), source: "domain-less");

        Assert.Null(@object: omitted.Document.Shapes![0].Domain);
        Assert.DoesNotContain(expectedSubstring: "\"domain\"", actualString: Encoding.UTF8.GetString(bytes: omitted.Bytes), comparisonType: StringComparison.Ordinal);
        Assert.Equal(expected: omitted.Hash, actual: explicitNull.Hash);
        Assert.Equal(expected: omitted.Bytes, actual: explicitNull.Bytes);
    }
}
