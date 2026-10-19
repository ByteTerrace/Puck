using System.Numerics;

using Puck.Commands;
using Puck.SignedDistance;
using Puck.World.Authoring;

using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>
/// THE LAW: <c>creation.shape</c> reads each shape's law off its own lowering. A primitive whose non-uniform scale has
/// no native spelling (a capsule, cone or torus scaled apart in X and Z) rides the generic <see cref="SdfOp.Scale"/> and
/// reads as a bound whose gain is its smallest scale over its largest, which the fixed-point evaluator and contact
/// refuse; an ellipsoid is the exponent-2 gauge, a bound contact admits; the native lowerings (an elliptic cylinder, a
/// torus scaled alike in X and Z) read exact; a warp reads approximate with its step clamp and the instructions contact
/// drops; a path, a revolve and an unbounded fold are refused for contact by name, and a sweep and a detail shape are
/// omitted. Unknown prototypes and shapes are refused by name.
/// </summary>
public sealed class WorldShapeLawCommandModuleLawTests {
    private const string PrototypeId = "gallery";

    private sealed class FakeConsoleAuthority(WorldInstance instance) : IWorldConsoleAuthority {
        public bool TryResolve(CommandContext context, out WorldInstance resolved, out string refusal) {
            resolved = instance;
            refusal = string.Empty;

            return true;
        }
    }

    private static ShapeDocument Shape(int id, string name, SdfSolidPrimitive type, Vector3 scale) => new(
        Id: id,
        Name: name,
        Type: type,
        Position: new Vector3(x: id, y: 0f, z: 0f),
        Rotation: Quaternion.Identity,
        Scale: scale,
        Material: 0,
        Blend: SdfBlendOp.Union,
        Smooth: 0f,
        Group: 0
    );
    private static IReadOnlyList<ShapeDocument> Gallery() => [
        Shape(id: 0, name: "box", scale: new Vector3(x: 0.5f, y: 0.3f, z: 0.2f), type: SdfSolidPrimitive.Box),
        Shape(id: 1, name: "capsule-xz", scale: new Vector3(x: 0.2f, y: 0.5f, z: 0.4f), type: SdfSolidPrimitive.Capsule),
        Shape(id: 2, name: "cone-xz", scale: new Vector3(x: 0.6f, y: 0.6f, z: 0.15f), type: SdfSolidPrimitive.Cone),
        Shape(id: 3, name: "torus-xz", scale: new Vector3(x: 1f, y: 0.5f, z: 0.5f), type: SdfSolidPrimitive.Torus),
        Shape(id: 4, name: "torus-alike", scale: new Vector3(x: 0.8f, y: 0.3f, z: 0.8f), type: SdfSolidPrimitive.Torus),
        Shape(id: 5, name: "cylinder-xz", scale: new Vector3(x: 0.6f, y: 0.9f, z: 0.25f), type: SdfSolidPrimitive.Cylinder),
        Shape(id: 6, name: "ellipsoid", scale: new Vector3(x: 0.3f, y: 0.2f, z: 0.1f), type: SdfSolidPrimitive.Ellipsoid),
        Shape(id: 7, name: "path", scale: Vector3.One, type: SdfSolidPrimitive.Prism) with {
            Profile = new SdfPrismProfile(SdfPrismProfileKind.Path, CornerRadius: 0,
                Path: new([new(new(x: -0.5f, y: -0.5f), [new(new(x: 0.5f, y: -0.5f)), new(new(x: 0f, y: 0.5f))])])),
        },
        Shape(id: 8, name: "revolve", scale: new Vector3(x: 0.2f, y: 0.3f, z: 0.5f), type: SdfSolidPrimitive.Prism) with { Lift = SdfLift.Revolve },
        Shape(id: 9, name: "flared", scale: new Vector3(value: 0.4f), type: SdfSolidPrimitive.Sphere) with { Flare = new ShapeFlareDocument(Amount: 2f, Bulge: 0f, Span: 1f) },
        Shape(id: 10, name: "sweep", scale: Vector3.One, type: SdfSolidPrimitive.Sweep) with {
            Curve = new ShapeCurveDocument(A: Vector3.Zero, B: new Vector3(x: 0.2f, y: 0.4f, z: 0f), C: new Vector3(x: 0f, y: 0.8f, z: 0f), RadiusStart: 0.05f, RadiusEnd: 0.02f),
        },
        Shape(id: 11, name: "detail", scale: new Vector3(value: 0.1f), type: SdfSolidPrimitive.Box) with { Detail = true },
        Shape(id: 12, name: "lattice", scale: new Vector3(value: 0.1f), type: SdfSolidPrimitive.Sphere) with {
            Domain = [new ShapeDomainOp.Repeat(Spacing: new Vector3(value: 0.5f))],
        },
    ];
    private static WorldDefinition BuildDocument() {
        var canonical = CreationCanonicalizer.Canonicalize(
            document: new CreationDocument(
                Schema: CreationDocument.CurrentSchema,
                Name: PrototypeId,
                Palette: [new PaletteEntryDocument(Color: "#FFFFFF", Emissive: null, Specular: null, Roughness: null)],
                Shapes: Gallery(),
                Frames: null
            ),
            source: PrototypeId
        );

        return (Fixtures.BuildDocument() with {
            CreationsRaw = [new WorldPrototype(Document: canonical.Document, HashRaw: canonical.Hash, Id: PrototypeId)],
        });
    }
    private static CommandResult Submit(string line) {
        using var row = HostRow.Build(definition: BuildDocument(), name: "boot");
        var source = new TextCommandSource(registry: new CommandRegistry(modules: [
            new WorldShapeLawCommandModule(authority: new FakeConsoleAuthority(instance: row.Instance)),
        ]));
        var result = CommandResult.None;

        using (var session = source.CreateSession(onResult: (_, value) => result = value, principal: Principal.Console)) {
            session.Enqueue(line: line);
            source.Collect();
        }

        return result;
    }
    private static string Line(string output, string shape) => output.Split(separator: '\n')
        .Single(predicate: line => line.Contains(value: $" {shape} ", comparisonType: StringComparison.Ordinal));

    /// <summary>Every shape of the gallery reads the verdict its lowering earns, and the summary counts them.</summary>
    [Fact]
    public void EachShapeReadsTheLawItsLoweringEarns() {
        var result = Submit(line: $"creation.shape {PrototypeId}");

        Assert.False(condition: result.IsError, userMessage: result.Output);
        Assert.StartsWith(
            actualString: result.Output,
            comparisonType: StringComparison.Ordinal,
            expectedStartString: $"[creation.shape: {PrototypeId} shapes=13/13 exact=6 bound=4 approximate=3 cpu-refused=5 contact-refused=6 unlowered=0]"
        );

        var output = result.Output;

        Assert.Contains(expectedSubstring: "lowered=Box distance=exact cpu=interprets contact=admitted", actualString: Line(output: output, shape: "box"), comparisonType: StringComparison.Ordinal);
        // The generic scale's gain is its smallest component over its largest.
        Assert.Contains(expectedSubstring: "lowered=Scale(0.2,0.5,0.4)·Capsule distance=bound gain=0.4 ", actualString: Line(output: output, shape: "capsule-xz"), comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "cpu=refuses (SdfFieldEvaluator cannot interpret non-uniform Scale", actualString: Line(output: output, shape: "capsule-xz"), comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "contact=refused (SdfFieldEvaluator cannot interpret non-uniform Scale", actualString: Line(output: output, shape: "capsule-xz"), comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "lowered=Scale(0.6,0.6,0.15)·Trapezoid distance=bound gain=0.25 ", actualString: Line(output: output, shape: "cone-xz"), comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "lowered=Scale(1,0.5,0.5)·Torus distance=bound gain=0.5 ", actualString: Line(output: output, shape: "torus-xz"), comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "lowered=Ellipse distance=exact cpu=interprets contact=admitted", actualString: Line(output: output, shape: "torus-alike"), comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "lowered=Rotate·Ellipse distance=exact cpu=interprets contact=admitted", actualString: Line(output: output, shape: "cylinder-xz"), comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "lowered=Superellipsoid(e=2) distance=bound gain=0.333 ", actualString: Line(output: output, shape: "ellipsoid"), comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "contact=admitted", actualString: Line(output: output, shape: "ellipsoid"), comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "lowered=Path distance=approximate (no certified gain) cpu=refuses", actualString: Line(output: output, shape: "path"), comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "contact=refused (uses profile Path)", actualString: Line(output: output, shape: "path"), comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "contact=refused (lifts by revolve)", actualString: Line(output: output, shape: "revolve"), comparisonType: StringComparison.Ordinal);
        Assert.Matches(expectedRegexPattern: @"lowered=AxialProfile·PushField·Scale\(0\.4\)·Sphere·PopField distance=approximate \(no certified gain\) step=0\.\d+ \(L=\d", actualString: Line(output: output, shape: "flared"));
        Assert.Contains(expectedSubstring: "contact=admitted contact-omits=AxialProfile+PushField+PopField", actualString: Line(output: output, shape: "flared"), comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "lowered=Sweep distance=approximate (no certified gain) cpu=interprets contact=omitted (a sweep is not a closed solid)", actualString: Line(output: output, shape: "sweep"), comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "facets=detail lowered=Scale(0.1)·Box(detail)", actualString: Line(output: output, shape: "detail"), comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "contact=omitted (detail)", actualString: Line(output: output, shape: "detail"), comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "facets=domain:Repeat lowered=Scale(0.1)·Sphere distance=exact", actualString: Line(output: output, shape: "lattice"), comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "contact=refused (carries ", actualString: Line(output: output, shape: "lattice"), comparisonType: StringComparison.Ordinal);
    }
    /// <summary>Named shapes read alone, in the order named.</summary>
    [Fact]
    public void NamedShapesReadAlone() {
        var result = Submit(line: $"creation.shape {PrototypeId} ellipsoid box");
        var lines = result.Output.Split(separator: '\n');

        Assert.False(condition: result.IsError, userMessage: result.Output);
        Assert.StartsWith(expectedStartString: $"[creation.shape: {PrototypeId} shapes=2/13 ", actualString: lines[0], comparisonType: StringComparison.Ordinal);
        Assert.Equal(expected: 3, actual: lines.Length);
        Assert.StartsWith(expectedStartString: "  6 ellipsoid ", actualString: lines[1], comparisonType: StringComparison.Ordinal);
        Assert.StartsWith(expectedStartString: "  0 box ", actualString: lines[2], comparisonType: StringComparison.Ordinal);
    }
    /// <summary>A missing prototype, an unknown prototype and an unknown shape are refused by name.</summary>
    [Theory]
    [InlineData("creation.shape", "[creation.shape: expected <prototype> [<shape-name>...]]")]
    [InlineData("creation.shape nope", "[creation.shape: no prototype named 'nope' — prototypes: gallery]")]
    [InlineData("creation.shape gallery nope", "[creation.shape: gallery has no shape named 'nope' — shapes: box, capsule-xz,")]
    public void UnknownNamesAreRefusedByName(string line, string refusal) {
        var result = Submit(line: line);

        Assert.True(condition: result.IsError, userMessage: result.Output);
        Assert.Contains(expectedSubstring: refusal, actualString: result.Output, comparisonType: StringComparison.OrdinalIgnoreCase);
    }
    /// <summary>A shape the renderer cannot lower at all reads its refusal in place of a law.</summary>
    [Fact]
    public void AShapeThatCannotLowerReadsItsRefusal() {
        var document = new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: "unlowered",
            Palette: null,
            Shapes: [Shape(id: 0, name: "bare-sweep", scale: Vector3.One, type: SdfSolidPrimitive.Sweep)],
            Frames: null
        );
        var law = CreationShapeLaws.Of(document: document, index: 0);
        var described = CreationShapeLaws.Describe(laws: [law], prototype: "unlowered", shapeCount: 1, verb: "creation.shape");

        Assert.Equal(expected: "A Sweep primitive requires a curve", actual: law.Failure);
        Assert.Contains(expectedSubstring: "unlowered=1]", actualString: described, comparisonType: StringComparison.Ordinal);
        Assert.EndsWith(expectedEndString: "0 bare-sweep Sweep scale=1 lowering=refused (A Sweep primitive requires a curve)", actualString: described, comparisonType: StringComparison.Ordinal);
    }
}
